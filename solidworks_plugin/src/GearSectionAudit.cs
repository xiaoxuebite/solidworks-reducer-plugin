using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ReducerSW {
    // Read the saved gear's boundary curves; do not rebuild, select, or save its model.
    public static class GearSectionAudit {
        const double FineChordTolerance=1e-8,CoarseChordTolerance=2e-8;
        const double VolumeTolerance=.5,ConvergenceTolerance=.1;
        public sealed class FaceResult {
            public double x_mm,area_mm2,coarse_area_mm2,face_api_area_mm2;
            public double volume_from_section_mm3,coarse_volume_from_section_mm3,residual_mm3,convergence_mm3;
            public int outer_loops,inner_loops,circle_edges,line_edges,tessellated_edges,tessellation_points;
            public bool coarse_evaluated;
            public List<LoopResult> loops;
        }
        public sealed class LoopResult {
            public bool is_outer;public double signed_area_mm2,absolute_area_mm2;
            public int coedges,circle_edges,line_edges,tessellated_edges,tessellation_points;
        }
        sealed class Counts {
            public int circles,lines,other,points,outer,inner;
            public readonly List<LoopResult> loops=new List<LoopResult>();
        }
        static double Cross(double[] a,double[] b) {return a[1]*b[2]-a[2]*b[1];}
        static double Distance(double[] a,double[] b) {
            return Math.Sqrt(Math.Pow(a[0]-b[0],2)+Math.Pow(a[1]-b[1],2)+Math.Pow(a[2]-b[2],2));
        }
        static double[] Point(Curve curve,double u) {
            var result=curve.Evaluate2(u,0) as double[];
            if(result==null||result.Length<3)throw new Exception("Curve endpoint evaluation failed.");
            return new[]{result[0],result[1],result[2]};
        }
        static string TessellationDiagnostic(Curve curve,CurveParamData data,double first,double last,
                double[] evaluatedStart,double[] evaluatedEnd,double[] trimStart,double[] trimEnd,
                double[] tessStart,double[] tessEnd,int pointCount,double gap,bool reverse) {
            return new JavaScriptSerializer().Serialize(new {
                curve_type=curve.Identity(),sense=data.Sense,u_min_raw=data.UMinValue,u_max_raw=data.UMaxValue,
                u_first_normalized=first,u_last_normalized=last,evaluated_start=evaluatedStart,evaluated_end=evaluatedEnd,
                trim_start=trimStart,trim_end=trimEnd,tess_start=tessStart,tess_end=tessEnd,
                evaluated_trim_forward_gap_m=Math.Max(Distance(evaluatedStart,trimStart),Distance(evaluatedEnd,trimEnd)),
                evaluated_trim_reverse_gap_m=Math.Max(Distance(evaluatedStart,trimEnd),Distance(evaluatedEnd,trimStart)),
                tess_trim_forward_gap_m=Math.Max(Distance(tessStart,trimStart),Distance(tessEnd,trimEnd)),
                tess_trim_reverse_gap_m=Math.Max(Distance(tessStart,trimEnd),Distance(tessEnd,trimStart)),
                maximum_endpoint_gap_m=gap,endpoint_tolerance_m=2e-8,tessellation_points=pointCount,reverse=reverse});
        }
        static double CurveIntegral(Edge edge,Curve curve,CurveParamData data,double tolerance,Counts counts) {
            double first=data.UMinValue,last=data.UMaxValue;
            // Reversed edges encode the curve interval in negative parameter space.
            if(!data.Sense){double saved=first;first=-last;last=-saved;}
            if(last<=first)throw new Exception("Invalid native edge parameter interval.");
            double[] evaluatedStart=Point(curve,first),evaluatedEnd=Point(curve,last);
            // Each branch returns its integral in the actual EDGE direction. The
            // enclosing face only applies the coedge-to-edge direction once.
            var firstVertex=edge.GetStartVertex() as Vertex;var lastVertex=edge.GetEndVertex() as Vertex;
            double[] start=firstVertex==null?null:firstVertex.GetPoint() as double[];
            double[] end=lastVertex==null?null:lastVertex.GetPoint() as double[];
            if(start==null||end==null) {
                start=data.StartPoint as double[];end=data.EndPoint as double[];
                if(!data.Sense){var saved=start;start=end;end=saved;}
            }
            if(start==null||end==null)throw new Exception("Native edge vertices are missing.");
            if(curve.IsLine()){counts.lines++;return Cross(start,end)/2;}
            if(curve.IsCircle()) {
                counts.circles++;
                var circle=(double[])curve.CircleParams;
                if(Math.Abs(circle[3])<.999999)throw new Exception("End-face circular edge is not in a plane normal to X.");
                var tangent=(double[])curve.Evaluate2(first,1);
                double orient=(evaluatedStart[1]-circle[1])*tangent[5]-(evaluatedStart[2]-circle[2])*tangent[4];
                double sweep=Math.Sign(orient)*(last-first)*(data.Sense?1:-1);
                // 1/2 integral(y dz-z dy), exact for translated circular arcs.
                return (circle[1]*(end[2]-start[2])-circle[2]*(end[1]-start[1])
                    +circle[6]*circle[6]*sweep)/2;
            }
            counts.other++;
            // Use actual edge vertices for tessellation and orient returned points
            // to these vertices, independent of signed U-interval encoding.
            var trimStart=start;var trimEnd=end;
            if(trimStart==null||trimEnd==null||trimStart.Length!=3||trimEnd.Length!=3)
                throw new Exception("Native curve trim coordinates are missing.");
            var tess=curve.GetTessPts(tolerance,1e-10,trimStart,trimEnd) as double[];
            if(tess==null||tess.Length<6||tess.Length%3!=0)throw new Exception("Native curve tessellation failed.");
            int size=tess.Length/3;counts.points+=size;
            double[] tessStart={tess[0],tess[1],tess[2]},tessEnd={tess[tess.Length-3],tess[tess.Length-2],tess[tess.Length-1]};
            bool reverse=Distance(tessStart,trimStart)+Distance(tessEnd,trimEnd)>Distance(tessStart,trimEnd)+Distance(tessEnd,trimStart);
            double gap=reverse?Math.Max(Distance(tessStart,trimEnd),Distance(tessEnd,trimStart)):Math.Max(Distance(tessStart,trimStart),Distance(tessEnd,trimEnd));
            if(gap>2e-8)throw new Exception("Tessellation endpoints differ from the native trimmed edge. Diagnostics: "
                +TessellationDiagnostic(curve,data,first,last,evaluatedStart,evaluatedEnd,trimStart,trimEnd,tessStart,tessEnd,size,gap,reverse));
            double sum=0,correction=0;
            for(int index=0;index<size-1;index++) {
                int i=index*3,j=i+3;
                double term=(tess[i+1]*tess[j+2]-tess[i+2]*tess[j+1])/2-correction;
                double next=sum+term;correction=(next-sum)-term;sum=next;
            }
            // Close the tiny allowed endpoint gaps to the exact trim vertices.
            return reverse?(-sum+Cross(trimStart,tessEnd)/2+Cross(tessStart,trimEnd)/2)
                :(sum+Cross(trimStart,tessStart)/2+Cross(tessEnd,trimEnd)/2);
        }
        static double FaceArea(Face2 face,double tolerance,Counts counts) {
            double area=0;
            foreach(Loop2 loop in face.GetLoops() as object[]??new object[0]) {
                double loopArea=0;
                var coedges=loop.GetCoEdges() as object[];
                if(coedges==null||coedges.Length==0)throw new Exception("End-face loop has no coedges.");
                int circleBefore=counts.circles,lineBefore=counts.lines,otherBefore=counts.other,pointsBefore=counts.points;
                foreach(CoEdge coedge in coedges) {
                    var edge=(Edge)coedge.GetEdge();var curve=(Curve)edge.GetCurve();
                    var data=edge.GetCurveParams3();
                    double contribution=CurveIntegral(edge,curve,data,tolerance,counts);
                    loopArea+=(coedge.GetSense()?1:-1)*contribution;
                }
                counts.loops.Add(new LoopResult{is_outer=loop.IsOuter(),signed_area_mm2=loopArea*1e6,
                    absolute_area_mm2=Math.Abs(loopArea)*1e6,coedges=coedges.Length,circle_edges=counts.circles-circleBefore,
                    line_edges=counts.lines-lineBefore,tessellated_edges=counts.other-otherBefore,tessellation_points=counts.points-pointsBefore});
                if(loop.IsOuter()){counts.outer++;area+=Math.Abs(loopArea);}
                else {counts.inner++;area-=Math.Abs(loopArea);}
            }
            if(area<=0)throw new Exception("End-face boundary integration produced a non-positive area.");
            return area*1e6;
        }
        static double ReferenceArea(GearTrainSpec gear) {
            var polygon=Generator.GearReferencePerimeter(gear.module,gear.driven_teeth,gear.pressure_angle,1600);
            double sum=0,correction=0;
            for(int i=0;i<polygon.Length;i++) {
                var a=polygon[i];var b=polygon[(i+1)%polygon.Length];
                double term=a[0]*b[1]-a[1]*b[0]-correction,next=sum+term;
                correction=(next-sum)-term;sum=next;
            }
            double radius=gear.driven_bore/2,half=gear.driven_key_width/2;
            double circleStrip=half*Math.Sqrt(radius*radius-half*half)+radius*radius*Math.Asin(half/radius);
            double keyway=gear.driven_key_width*(radius+gear.driven_key_depth)-circleStrip;
            return Math.Abs(sum/2)-Math.PI*radius*radius-keyway;
        }
        public static string Run(SldWorks app,string directory) {
            string folder=Path.GetFullPath(directory),path=Path.Combine(folder,"driven_gear.SLDPRT");
            string reportPath=Path.Combine(folder,"gear_section_audit.json");
            var json=new JavaScriptSerializer{MaxJsonLength=32*1024*1024};
            var parameters=json.Deserialize<Parameters>(File.ReadAllText(Path.Combine(folder,"parameters.json")));
            if(parameters==null||parameters.gears==null)throw new ArgumentException("Gear parameters are required.");
            var gear=parameters.gears;Generator.ValidateGearTrain(gear);
            bool helical=Math.Abs(gear.helix_angle)>1e-8;
            double referenceArea=helical?0:ReferenceArea(gear),expected=referenceArea*gear.driven_width;
            var issues=new List<string>();var faces=new List<FaceResult>();
            long originalLength=new FileInfo(path).Length;DateTime originalWrite=File.GetLastWriteTimeUtc(path);
            var doc=(ModelDoc2)app.GetOpenDocumentByName(path);bool owned=doc==null;
            if(owned) {
                int error=0,warning=0;
                doc=(ModelDoc2)app.OpenDoc6(path,1,(int)(swOpenDocOptions_e.swOpenDocOptions_Silent|swOpenDocOptions_e.swOpenDocOptions_ReadOnly),"",ref error,ref warning);
                if(doc==null||error!=0)throw new Exception("Gear section audit could not open the saved native gear: "+error);
            }
            bool dirtyBefore=doc.GetSaveFlag(),dirtyAfter=dirtyBefore;
            try {
                var bodies=(object[])((PartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody,false);
                if(bodies==null||bodies.Length!=1)throw new Exception("Driven gear must have exactly one solid body.");
                if(helical) {
                    int status;
                    var mass=doc.Extension.GetMassProperties2(2,out status,false) as double[];
                    if(mass==null||mass.Length<4||status!=0)throw new Exception("Helical gear high-accuracy volume is unavailable.");
                    expected=mass[3]*1e9;referenceArea=expected/gear.driven_width;
                    Feature sweep=null;
                    for(var feature=(Feature)doc.FirstFeature();feature!=null;feature=(Feature)feature.GetNextFeature())
                        if(feature.GetTypeName2()=="Sweep"){sweep=feature;break;}
                    var sweepData=sweep==null?null:sweep.GetDefinition() as SweepFeatureData;
                    double pitch=gear.module*gear.driven_teeth/(2*Math.Cos(gear.helix_angle*Math.PI/180));
                    double twist=-gear.driven_width*Math.Tan(gear.helix_angle*Math.PI/180)/pitch;
                    if(sweepData==null||Math.Abs(sweepData.GetTwistAngle()-twist)>1e-6)
                        issues.Add("Native helical sweep twist differs from the signed design angle.");
                }
                foreach(Face2 face in ((Body2)bodies[0]).GetFaces() as object[]??new object[0]) {
                    var surface=(Surface)face.GetSurface();if(surface==null||!surface.IsPlane())continue;
                    var normal=face.Normal as double[];if(normal==null||Math.Abs(normal[0])<.999999)continue;
                    var box=(double[])face.GetBox();double x=(box[0]+box[3])*500;
                    if(Math.Min(Math.Abs(x),Math.Abs(x-gear.driven_width))>.001)continue;
                    var fine=new Counts();var coarse=new Counts();
                    // A twisted sweep can expose a spline with SW tessellation
                    // endpoints about one micron from its trimmed vertices.
                    // The face kernel's area avoids treating that display
                    // tessellation as exact geometry.
                    double area=helical?face.GetArea()*1e6:FaceArea(face,FineChordTolerance,fine);
                    var result=new FaceResult{x_mm=x,area_mm2=area,
                        face_api_area_mm2=face.GetArea()*1e6,volume_from_section_mm3=area*gear.driven_width,
                        residual_mm3=area*gear.driven_width-expected,outer_loops=fine.outer,inner_loops=fine.inner,
                        circle_edges=fine.circles,line_edges=fine.lines,tessellated_edges=fine.other,tessellation_points=fine.points,loops=fine.loops};
                    faces.Add(result);
                    Console.WriteLine("Gear end section x="+x.ToString("R",CultureInfo.InvariantCulture)+" mm; area="+area.ToString("R",CultureInfo.InvariantCulture)
                        +" mm2; equivalent volume="+result.volume_from_section_mm3.ToString("R",CultureInfo.InvariantCulture)
                        +" mm3; residual="+result.residual_mm3.ToString("R",CultureInfo.InvariantCulture)+" mm3");
                    double allowed=helical?Math.Max(VolumeTolerance,expected*2e-5):VolumeTolerance;
                    if(Math.Abs(result.residual_mm3)>allowed) {
                        issues.Add("End-face section differs from the reference volume by more than "+allowed.ToString("G6",CultureInfo.InvariantCulture)+" mm3 at x="+x);
                        break;
                    }
                    if(!helical) {
                        double coarseArea=FaceArea(face,CoarseChordTolerance,coarse);
                        result.coarse_evaluated=true;result.coarse_area_mm2=coarseArea;
                        result.coarse_volume_from_section_mm3=coarseArea*gear.driven_width;
                        result.convergence_mm3=(area-coarseArea)*gear.driven_width;
                        if(Math.Abs(result.convergence_mm3)>ConvergenceTolerance)issues.Add("End-face tessellation did not converge within 0.1 mm3 at x="+x);
                    }
                }
                if(faces.Count!=2||!faces.Any(f=>Math.Abs(f.x_mm)<.001)||!faces.Any(f=>Math.Abs(f.x_mm-gear.driven_width)<.001))
                    issues.Add("Expected one continuous planar end face at each end of the driven gear.");
                dirtyAfter=doc.GetSaveFlag();if(dirtyBefore!=dirtyAfter)issues.Add("Native document save flag changed.");
            } catch(Exception ex) {issues.Add(ex.ToString());}
            finally {if(owned)app.CloseDoc(doc.GetTitle());}
            bool unchanged=originalLength==new FileInfo(path).Length&&originalWrite==File.GetLastWriteTimeUtc(path);
            if(!unchanged)issues.Add("Native source file changed during the read-only section audit.");
            File.WriteAllText(reportPath,json.Serialize(new {schema_version=1,file="driven_gear.SLDPRT",passed=issues.Count==0,
                method=helical?"Helical native twist angle and end-face kernel areas compared with high-accuracy solid volume; independent involute reference is limited to spur gears.":
                    "Actual end-face boundary Green integrals: exact lines/circles, trimmed spline tessellations; independent spur reference.",
                chord_tolerance_m=FineChordTolerance,coarse_chord_tolerance_m=CoarseChordTolerance,reference_samples_per_flank=1600,
                absolute_tolerance_mm3=helical?Math.Max(VolumeTolerance,expected*2e-5):VolumeTolerance,convergence_tolerance_mm3=ConvergenceTolerance,driven_width_mm=gear.driven_width,
                reference_area_mm2=referenceArea,expected_volume_mm3=expected,faces=faces,source_file_unchanged=unchanged,
                already_open=!owned,audited_unsaved_state=dirtyBefore,save_flag_before=dirtyBefore,save_flag_after=dirtyAfter,issues=issues}));
            if(issues.Count>0)throw new Exception("Gear section audit failed: "+String.Join(System.Environment.NewLine,issues)+" Report: "+reportPath);
            return reportPath;
        }
    }
}
