using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using ReducerSW;
using SolidWorks.Interop.sldworks;

// Reads saved analytic SW geometry, not feature names or manifest counts.
// Usage: BearingNativePositionCheck.exe <specs-array-or-parameters.json> <parts-directory> [report.json]
// Existing open documents are inspected in place and remain open; other parts open read-only.
class BearingNativePositionCheck {
    const double LinearToleranceMm=1e-5;
    const double AngularToleranceRad=1e-6;
    sealed class Ball {
        public string body;
        public double[] center_mm;
        public double radius_mm, radial_center_mm, axial_error_mm, radial_error_mm;
        public double angle_rad, end_clearance_mm, volume_mm3, sphere_area_mm2, groove_clearance_mm;
    }
    sealed class Raceway {
        public string body;
        public double[] center_mm, axis;
        public double major_radius_mm, minor_radius_mm;
    }
    static string Hash(string file) {using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(file))).Replace("-","").ToLowerInvariant();}
    static double Distance(double[] a,double[] b){return Math.Sqrt(a.Select((value,index)=>(value-b[index])*(value-b[index])).Sum());}
    static double Number(object value){return Convert.ToDouble(value,CultureInfo.InvariantCulture);}
    static void Near(List<string> issues,double actual,double expected,double tolerance,string label) {
        if(Double.IsNaN(actual)||Double.IsInfinity(actual)||Math.Abs(actual-expected)>tolerance)
            issues.Add(label+": actual="+actual.ToString("R",CultureInfo.InvariantCulture)+" expected="+expected.ToString("R",CultureInfo.InvariantCulture));
    }
    static double[] Mm(double[] values,int count){return values.Take(count).Select(value=>value*1000).ToArray();}
    static double GrooveClearance(Ball ball,Raceway raceway) {
        var delta=ball.center_mm.Select((value,index)=>value-raceway.center_mm[index]).ToArray();
        double axial=delta.Select((value,index)=>value*raceway.axis[index]).Sum();
        double radial=Math.Sqrt(Math.Max(0,delta.Sum(value=>value*value)-axial*axial));
        double offset=Math.Sqrt(axial*axial+(radial-raceway.major_radius_mm)*(radial-raceway.major_radius_mm));
        // A sphere entirely inside this toroidal cut tube has no intersection
        // with either trimmed race. This uses measured surfaces and centers.
        return raceway.minor_radius_mm-offset-ball.radius_mm;
    }
    static object Inspect(SldWorks app,BearingSpec spec,string folder,List<string> allIssues) {
        string file=Path.GetFullPath(Path.Combine(folder,spec.name+".SLDPRT"));
        if(!File.Exists(file))throw new FileNotFoundException("Bearing part does not exist.",file);
        string beforeHash=Hash(file);ModelDoc2 doc=(ModelDoc2)app.GetOpenDocumentByName(file);bool owned=doc==null;
        int error=0,warning=0;
        if(owned)doc=(ModelDoc2)app.OpenDoc6(file,1,3,"",ref error,ref warning);
        if(doc==null||error!=0)throw new Exception("Read-only bearing open failed: "+spec.name+" error="+error+" warning="+warning);
        try {
            if(!String.Equals(Path.GetFullPath(doc.GetPathName()),file,StringComparison.OrdinalIgnoreCase))
                throw new Exception("SW opened a different same-named document: "+doc.GetPathName());
            bool dirtyBefore=doc.GetSaveFlag();var issues=new List<string>();
            var balls=new List<Ball>();var races=new List<Raceway>();int torusBodies=0,unknownBodies=0;
            var bodies=(object[])((PartDoc)doc).GetBodies2(0,false)??new object[0];
            double width=spec.width,pitch=(spec.inner_diameter+spec.outer_diameter)/4;
            double ballRadius=(spec.outer_diameter-spec.inner_diameter)/2*.294;
            int expectedCount=Math.Min(Math.Max((int)(2*Math.PI*pitch/Math.Max(ballRadius*2.35,1)),8),36);
            foreach(Body2 body in bodies) {
                var spherical=new List<double[]>();var toroidal=new List<double[]>();double area=0;bool allSpherical=true;
                var faces=(object[])body.GetFaces()??new object[0];
                foreach(Face2 face in faces) {
                    var surface=(Surface)face.GetSurface();bool sphere=surface!=null&&surface.IsSphere();allSpherical&=sphere;
                    if(sphere){spherical.Add((double[])surface.SphereParams);area+=face.GetArea()*1e6;}
                    if(surface!=null&&surface.IsTorus())toroidal.Add((double[])surface.TorusParams);
                }
                if(spherical.Count>0) {
                    var p=spherical[0];var ball=new Ball{body=body.Name,center_mm=Mm(p,3),radius_mm=p[3]*1000,sphere_area_mm2=area};
                    if(!allSpherical)issues.Add("Rolling element is not a complete analytic spherical body: "+body.Name);
                    foreach(var face in spherical)for(int i=0;i<4;i++)Near(issues,face[i]*1000,p[i]*1000,LinearToleranceMm,body.Name+" sphere face agreement "+i);
                    var mass=(double[])body.GetMassProperties(1);ball.volume_mm3=mass[3]*1e9;
                    Near(issues,ball.volume_mm3,4*Math.PI*Math.Pow(ball.radius_mm,3)/3,Math.Max(1e-6,ball.volume_mm3*1e-8),body.Name+" full sphere volume");
                    Near(issues,area,4*Math.PI*ball.radius_mm*ball.radius_mm,Math.Max(1e-6,area*1e-8),body.Name+" full sphere area");
                    ball.radial_center_mm=Math.Sqrt(ball.center_mm[1]*ball.center_mm[1]+ball.center_mm[2]*ball.center_mm[2]);
                    ball.axial_error_mm=ball.center_mm[0]-width/2;ball.radial_error_mm=ball.radial_center_mm-pitch;
                    ball.angle_rad=Math.Atan2(ball.center_mm[2],ball.center_mm[1]);if(ball.angle_rad<0)ball.angle_rad+=2*Math.PI;
                    ball.end_clearance_mm=Math.Min(ball.center_mm[0]-ball.radius_mm,width-ball.center_mm[0]-ball.radius_mm);
                    Near(issues,ball.center_mm[0],width/2,LinearToleranceMm,body.Name+" axial center X (rolling plane must be YZ)");
                    Near(issues,ball.radial_center_mm,pitch,LinearToleranceMm,body.Name+" pitch radius about X");
                    Near(issues,ball.radius_mm,ballRadius,LinearToleranceMm,body.Name+" ball radius");
                    if(ball.end_clearance_mm < -LinearToleranceMm)issues.Add(body.Name+" protrudes beyond a bearing end face.");
                    balls.Add(ball);
                } else if(toroidal.Count==0)unknownBodies++;
                if(toroidal.Count>0) {
                    torusBodies++;
                    foreach(var p in toroidal) {
                        var race=new Raceway{body=body.Name,center_mm=Mm(p,3),axis=p.Skip(3).Take(3).ToArray(),major_radius_mm=p[6]*1000,minor_radius_mm=p[7]*1000};
                        Near(issues,race.center_mm[0],width/2,LinearToleranceMm,body.Name+" race center X");
                        Near(issues,race.center_mm[1],0,LinearToleranceMm,body.Name+" race center Y");Near(issues,race.center_mm[2],0,LinearToleranceMm,body.Name+" race center Z");
                        Near(issues,Math.Abs(race.axis[0]),1,1e-8,body.Name+" race axis X");Near(issues,race.axis[1],0,1e-8,body.Name+" race axis Y");Near(issues,race.axis[2],0,1e-8,body.Name+" race axis Z");
                        Near(issues,race.major_radius_mm,pitch,LinearToleranceMm,body.Name+" race major radius");
                        Near(issues,race.minor_radius_mm,ballRadius*1.04,LinearToleranceMm,body.Name+" race groove radius");races.Add(race);
                    }
                }
            }
            if(balls.Count!=expectedCount)issues.Add("Ball count="+balls.Count+" expected="+expectedCount);
            if(torusBodies!=2)issues.Add("Expected two race bodies with analytic toroidal grooves; actual="+torusBodies);
            if(unknownBodies!=0)issues.Add("Unexpected non-ball/non-race solid bodies="+unknownBodies);
            if(bodies.Length!=expectedCount+2)issues.Add("Solid body count="+bodies.Length+" expected="+(expectedCount+2));
            var ordered=balls.OrderBy(ball=>ball.angle_rad).ToArray();double maximumAngleError=0;
            for(int i=0;i<ordered.Length;i++) {
                double next=ordered[(i+1)%ordered.Length].angle_rad+(i+1==ordered.Length?2*Math.PI:0);
                double difference=next-ordered[i].angle_rad;maximumAngleError=Math.Max(maximumAngleError,Math.Abs(difference-2*Math.PI/expectedCount));
                Near(issues,difference,2*Math.PI/expectedCount,AngularToleranceRad,"Adjacent ball angular spacing "+i);
            }
            double minimumBallClearance=Double.PositiveInfinity;
            for(int i=0;i<balls.Count;i++)for(int j=i+1;j<balls.Count;j++) {
                double clearance=Distance(balls[i].center_mm,balls[j].center_mm)-balls[i].radius_mm-balls[j].radius_mm;
                minimumBallClearance=Math.Min(minimumBallClearance,clearance);
                if(clearance < -LinearToleranceMm)issues.Add("Rolling elements overlap: "+balls[i].body+" / "+balls[j].body);
            }
            foreach(var ball in balls) {
                ball.groove_clearance_mm=races.Count==0?-Double.MaxValue:races.Min(race=>GrooveClearance(ball,race));
                if(ball.groove_clearance_mm < -LinearToleranceMm)issues.Add(ball.body+" lies outside the actual toroidal groove tube; clearance="+ball.groove_clearance_mm.ToString("R",CultureInfo.InvariantCulture)+" mm.");
            }
            bool dirtyAfter=doc.GetSaveFlag();string afterHash=Hash(file);
            if(dirtyBefore!=dirtyAfter)issues.Add("Source document save flag changed during read-only audit.");
            if(beforeHash!=afterHash)issues.Add("Source bearing file bytes changed during read-only audit.");
            foreach(string issue in issues)allIssues.Add(spec.name+": "+issue);
            Console.WriteLine(spec.name+": "+(issues.Count==0?"PASS":"FAIL")+"; balls="+balls.Count+"; actual torus bodies="+torusBodies+"; max X error="+(balls.Count==0?0:balls.Max(ball=>Math.Abs(ball.axial_error_mm))).ToString("R",CultureInfo.InvariantCulture)+" mm");
            return new {name=spec.name,catalog_name=spec.catalog_name,path=file,passed=issues.Count==0,already_open=!owned,audited_unsaved_state=dirtyBefore,save_flag_before=dirtyBefore,save_flag_after=dirtyAfter,source_file_unchanged=beforeHash==afterHash,source_sha256=beforeHash,linear_tolerance_mm=LinearToleranceMm,angular_tolerance_rad=AngularToleranceRad,solid_bodies=bodies.Length,expected_balls=expectedCount,torus_bodies=torusBodies,pitch_radius_mm=pitch,expected_ball_radius_mm=ballRadius,max_axial_error_mm=balls.Count==0?0:balls.Max(ball=>Math.Abs(ball.axial_error_mm)),max_pitch_radius_error_mm=balls.Count==0?0:balls.Max(ball=>Math.Abs(ball.radial_error_mm)),max_angular_gap_error_rad=maximumAngleError,min_ball_pair_clearance_mm=Double.IsInfinity(minimumBallClearance)?0:minimumBallClearance,balls=ordered,raceway_surfaces=races,issues=issues};
        } finally {if(owned&&doc!=null)app.CloseDoc(doc.GetTitle());}
    }
    [STAThread]static int Main(string[] args) {
        try {
            if(args.Length<2||args.Length>3)throw new ArgumentException("Usage: BearingNativePositionCheck.exe <specs-array-or-parameters.json> <parts-directory> [report.json]");
            var json=new JavaScriptSerializer{MaxJsonLength=32*1024*1024};string input=File.ReadAllText(args[0]);
            BearingSpec[] specs=input.TrimStart('\ufeff',' ','\t','\r','\n').StartsWith("[")?json.Deserialize<BearingSpec[]>(input):json.Deserialize<Parameters>(input).bearings;
            if(specs==null||specs.Length==0)throw new ArgumentException("No bearing specifications supplied.");
            var app=(SldWorks)Marshal.GetActiveObject("SldWorks.Application");if(!app.RevisionNumber().StartsWith("32."))throw new Exception("SOLIDWORKS 2024 is required.");
            string folder=Path.GetFullPath(args[1]);var issues=new List<string>();var parts=new List<object>();
            foreach(var spec in specs) {
                try {parts.Add(Inspect(app,spec,folder,issues));}
                catch(Exception error){issues.Add(spec.name+": "+error.Message);parts.Add(new {name=spec.name,passed=false,error=error.ToString()});}
            }
            string report=args.Length>2?Path.GetFullPath(args[2]):Path.Combine(folder,"bearing_position_audit.json");
            Directory.CreateDirectory(Path.GetDirectoryName(report));
            File.WriteAllText(report,json.Serialize(new {schema_version=1,created_utc=DateTime.UtcNow.ToString("o"),scope="Read-only analytic sphere/torus geometry and persisted file checks; no rebuild or source save.",passed=issues.Count==0,parts=parts,issues=issues}),new UTF8Encoding(false));
            Console.WriteLine("Report="+report);
            if(issues.Count>0){Console.Error.WriteLine(String.Join(System.Environment.NewLine,issues));return 1;}
            Console.WriteLine("Native bearing positions/raceway geometry: PASS");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
