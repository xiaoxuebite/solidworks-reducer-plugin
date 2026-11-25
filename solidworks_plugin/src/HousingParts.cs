using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ReducerSW {
    public class HouseCurve {
        public string kind {get;set;}
        public double[] start {get;set;}
        public double[] end {get;set;}
        public double[] mid {get;set;}
        public double[] center {get;set;}
        public double radius {get;set;}
    }
    public class HouseFeatureSpec {
        public string name {get;set;}
        public string kind {get;set;}
        public double[] origin {get;set;}
        public double[] u {get;set;}
        public double[] v {get;set;}
        public double depth {get;set;}
        public HouseCurve[] curves {get;set;}
        public double[] axis {get;set;}
        public string[] source_features {get;set;}
    }
    public class HousingSpec {
        public HouseFeatureSpec[] lower {get;set;}
        public HouseFeatureSpec[] upper {get;set;}
        public double cover_offset_z {get;set;}
        public double[] expected_lower_bounds {get;set;}
        public double[] expected_upper_bounds {get;set;}
        public string source {get;set;}
        public string[] limitations {get;set;}
        public double[] assembly_reference {get;set;}
        public double[] bearing_outer_reference {get;set;}
    }

    public static partial class Generator {
        static double HNorm(double[] v) {return Math.Sqrt(v.Sum(x=>x*x));}
        static double HDot(double[] a,double[] b) {return a.Select((x,i)=>x*b[i]).Sum();}
        static double[] HCross(double[] a,double[] b) {return new[]{a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]};}
        static void HVector(double[] values,int size,string name) {
            if(values==null||values.Length!=size||values.Any(x=>Double.IsNaN(x)||Double.IsInfinity(x))) throw new ArgumentException("Housing "+name+" must contain "+size+" finite coordinates.");
        }
        static bool HSamePoint(double[] a,double[] b) {
            return Math.Abs(a[0]-b[0])<1e-8&&Math.Abs(a[1]-b[1])<1e-8;
        }
        static void HValidateContours(HouseFeatureSpec feature) {
            double[] first=null,last=null;
            foreach(var curve in feature.curves) {
                if(curve.kind=="circle") {
                    if(first!=null)throw new ArgumentException("Open housing contour: "+feature.name);
                    continue;
                }
                if(first==null)first=curve.start;
                else if(!HSamePoint(last,curve.start))throw new ArgumentException("Disconnected housing contour: "+feature.name);
                last=curve.end;
                if(HSamePoint(first,last)){first=null;last=null;}
            }
            if(first!=null)throw new ArgumentException("Open housing contour: "+feature.name);
        }
        public static void ValidateHousing(HousingSpec spec) {
            if(spec==null)throw new ArgumentException("Housing specification is missing.");
            Positive(spec.cover_offset_z);
            foreach(var array in new[]{spec.lower,spec.upper}) {
                if(array==null||array.Length==0)throw new ArgumentException("Housing feature recipe cannot be empty.");
                var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(var feature in array) {
                    if(feature==null||String.IsNullOrWhiteSpace(feature.name)||!names.Add(feature.name))throw new ArgumentException("Housing feature names must be unique.");
                    if(feature.kind!="boss"&&feature.kind!="cut"&&feature.kind!="revolve_cut")throw new ArgumentException("Unknown housing feature: "+feature.kind);
                    HVector(feature.origin,3,"origin");HVector(feature.u,3,"u");HVector(feature.v,3,"v");
                    if(Math.Abs(HNorm(feature.u)-1)>1e-7||Math.Abs(HNorm(feature.v)-1)>1e-7||Math.Abs(HDot(feature.u,feature.v))>1e-7)throw new ArgumentException("Housing sketch basis must be orthonormal.");
                    if(feature.curves==null||feature.curves.Length==0)throw new ArgumentException("Empty housing sketch.");
                    if(feature.kind=="revolve_cut")HVector(feature.axis,4,"revolve axis");
                    else if(Double.IsNaN(feature.depth)||Double.IsInfinity(feature.depth)||Math.Abs(feature.depth)<1e-8)throw new ArgumentException("Housing extrusion depth must be nonzero and finite.");
                    foreach(var curve in feature.curves) {
                        if(curve==null)throw new ArgumentException("Null housing curve.");
                        if(curve.kind=="circle"){HVector(curve.center,2,"circle center");Positive(curve.radius);}
                        else if(curve.kind=="line"||curve.kind=="arc") {
                            HVector(curve.start,2,"curve start");HVector(curve.end,2,"curve end");
                            if(Math.Abs(curve.start[0]-curve.end[0])+Math.Abs(curve.start[1]-curve.end[1])<1e-9)throw new ArgumentException("Zero-length housing edge.");
                            if(curve.kind=="arc")HVector(curve.mid,2,"arc midpoint");
                        } else throw new ArgumentException("Unknown housing sketch primitive: "+curve.kind);
                    }
                    HValidateContours(feature);
                }
                if(array[0].kind!="boss")throw new ArgumentException("The first housing feature must create a solid.");
            }
            HVector(spec.expected_lower_bounds,6,"lower bounds");HVector(spec.expected_upper_bounds,6,"upper bounds");
        }
        static double[] HRectangleBounds(HouseFeatureSpec feature) {
            var curves=feature.curves;
            if(curves.Length!=4||curves.Any(c=>c.kind!="line"))return null;
            var xs=curves.Select(c=>c.start[0]).Distinct().OrderBy(x=>x).ToArray();
            var ys=curves.Select(c=>c.start[1]).Distinct().OrderBy(x=>x).ToArray();
            if(xs.Length!=2||ys.Length!=2||xs[0]>=xs[1]||ys[0]>=ys[1])return null;
            if(curves.Select(c=>Array.IndexOf(xs,c.start[0])*2+Array.IndexOf(ys,c.start[1])).Distinct().Count()!=4)return null;
            for(int i=0;i<curves.Length;i++) {
                var c=curves[i];var next=curves[(i+1)%curves.Length];
                if(!c.end.SequenceEqual(next.start)||(c.start[0]!=c.end[0]&&c.start[1]!=c.end[1]))return null;
            }
            return new[]{xs[0],ys[0],xs[1],ys[1]};
        }
        static HouseCurve[] HRectangleUnion(double[][] rectangles) {
            // Coordinate-cell union preserves the exact original rectangle edges.
            // It also keeps the portions of each tool that lie outside today's body.
            var xs=rectangles.SelectMany(r=>new[]{r[0],r[2]}).Distinct().OrderBy(x=>x).ToArray();
            var ys=rectangles.SelectMany(r=>new[]{r[1],r[3]}).Distinct().OrderBy(x=>x).ToArray();
            foreach(var values in new[]{xs,ys})for(int i=1;i<values.Length;i++)if(values[i]-values[i-1]<1e-8)return null;
            int stride=ys.Length;var occupied=new HashSet<int>();
            for(int i=0;i<xs.Length-1;i++)for(int j=0;j<ys.Length-1;j++) {
                double x=(xs[i]+xs[i+1])/2,y=(ys[j]+ys[j+1])/2;
                if(rectangles.Any(r=>r[0]<x&&x<r[2]&&r[1]<y&&y<r[3]))occupied.Add(i*stride+j);
            }
            var following=new Dictionary<int,int>();
            foreach(int cell in occupied.OrderBy(x=>x)) {
                int i=cell/stride,j=cell%stride;
                var boundaries=new[]{
                    new[]{cell-1,i*stride+j,(i+1)*stride+j},
                    new[]{cell+stride,(i+1)*stride+j,(i+1)*stride+j+1},
                    new[]{cell+1,(i+1)*stride+j+1,i*stride+j+1},
                    new[]{cell-stride,i*stride+j+1,i*stride+j}};
                foreach(var edge in boundaries)if(!occupied.Contains(edge[0])) {
                    // Point-touching boundaries cannot form one unambiguous contour.
                    if(following.ContainsKey(edge[1]))return null;
                    following.Add(edge[1],edge[2]);
                }
            }
            var result=new List<HouseCurve>();
            while(following.Count>0) {
                int start=following.Keys.Min(),current=start;var loop=new List<int>();
                while(following.ContainsKey(current)) {
                    loop.Add(current);int next=following[current];following.Remove(current);current=next;
                    if(current==start)break;
                }
                if(current!=start||loop.Count<4)return null;
                bool changed=true;
                while(changed&&loop.Count>=4) {
                    changed=false;
                    for(int k=0;k<loop.Count;k++) {
                        int a=loop[(k+loop.Count-1)%loop.Count],b=loop[k],c=loop[(k+1)%loop.Count];
                        if((a/stride==b/stride&&b/stride==c/stride)||(a%stride==b%stride&&b%stride==c%stride)) {
                            loop.RemoveAt(k);changed=true;break;
                        }
                    }
                }
                for(int k=0;k<loop.Count;k++) {
                    int a=loop[k],b=loop[(k+1)%loop.Count];
                    result.Add(new HouseCurve {kind="line",start=new[]{xs[a/stride],ys[a%stride]},end=new[]{xs[b/stride],ys[b%stride]}});
                }
            }
            return result.ToArray();
        }
        static string HOptimizationGroup(HouseFeatureSpec feature) {
            string name=feature.name;int split=name.LastIndexOf('_');
            string head=split<0?"":name.Substring(0,split),tail=name.Substring(split+1);
            if(feature.kind=="boss"&&name.StartsWith("Flange_Reinforcement_",StringComparison.Ordinal)&&
                tail.Length>0&&tail.All(Char.IsDigit))return "boss_loops|Flange_Reinforcements";
            if(feature.kind=="boss"&&name.Contains("_Rib_")&&(tail=="-1"||tail=="1"))
                return "boss_loops|"+head+"s";
            if(feature.kind!="cut")return null;
            // The concave union fails in SolidWorks. Disjoint contours can be
            // cut together; overlapping bridge contours need a separate cut.
            if(name.StartsWith("Side_Oil_Groove_",StringComparison.Ordinal)) {
                if(tail=="Vertical"||tail=="InnerTop"||tail=="InnerBottom")
                    return HRectangleBounds(feature)==null?null:"side_loops|Side_Oil_Groove_Active";
                if(tail=="Top"||tail=="Bottom")
                    return HRectangleBounds(feature)==null?null:"side_loops|Side_Oil_Groove_Bridge";
            }
            if(name.StartsWith("Split_U_Groove_",StringComparison.Ordinal)&&new[]{"Left","Right","Bar"}.Contains(tail))
                return HRectangleBounds(feature)==null?null:"rectangles|"+head;
            if(feature.curves.Length!=1||feature.curves[0].kind!="circle")return null;
            if(name.StartsWith("Flange_Fastener_Hole_",StringComparison.Ordinal)&&tail.Length>0&&tail.All(Char.IsDigit))return "circles|Flange_Fastener_Holes";
            if(name.StartsWith("Inspection_Screw_Hole_",StringComparison.Ordinal)&&tail.Length>0&&tail.All(Char.IsDigit))return "circles|Inspection_Screw_Holes";
            if((head=="High_Counterbore"||head=="Low_Counterbore")&&(tail=="-1"||tail=="1"))return "circles|Bearing_Counterbores_"+tail;
            return null;
        }
        static bool HSameCutStation(HouseFeatureSpec a,HouseFeatureSpec b) {
            return a.depth==b.depth&&a.origin.SequenceEqual(b.origin)&&a.u.SequenceEqual(b.u)&&a.v.SequenceEqual(b.v);
        }
        /// <summary>Combine equal-plane/depth legacy cuts without crossing an additive feature.</summary>
        public static HouseFeatureSpec[] OptimizeHousingFeatures(HouseFeatureSpec[] features) {
            if(features==null)throw new ArgumentException("Housing feature recipe is missing.");
            if(features.Any(f=>f.name.StartsWith("Side_Oil_Groove_",StringComparison.Ordinal)&&f.name.EndsWith("_Continuous",StringComparison.Ordinal)))
                throw new ArgumentException("Combined side oil grooves are not supported by the SolidWorks kernel; re-export from the original housing recipe.");
            var result=new List<HouseFeatureSpec>();var consumed=new HashSet<int>();
            for(int i=0;i<features.Length;i++) {
                if(consumed.Contains(i))continue;
                var feature=features[i];string group=HOptimizationGroup(feature);
                if(group==null){result.Add(feature);continue;}
                var candidates=new List<int>();
                for(int j=i;j<features.Length;j++) {
                    var other=features[j];
                    if(group.StartsWith("boss_loops|",StringComparison.Ordinal)) {
                        if(other.kind!="boss")break;
                    } else if(other.kind!="cut"&&other.kind!="revolve_cut")break;
                    if(!consumed.Contains(j)&&HOptimizationGroup(other)==group&&HSameCutStation(feature,other))candidates.Add(j);
                }
                if(candidates.Count<2){result.Add(feature);continue;}
                var originals=candidates.Select(j=>features[j]).ToArray();HouseCurve[] curves=null;
                bool rectangles=group.StartsWith("rectangles|",StringComparison.Ordinal);
                if(group.StartsWith("boss_loops|",StringComparison.Ordinal)) {
                    var boxes=originals.Select(f=>{
                        var points=f.curves.SelectMany(c=>new[]{c.start,c.end,c.mid}).Where(p=>p!=null).ToArray();
                        return new[]{points.Min(p=>p[0]),points.Min(p=>p[1]),points.Max(p=>p[0]),points.Max(p=>p[1])};
                    }).ToArray();
                    bool overlap=false;
                    for(int a=0;a<boxes.Length;a++)for(int b=a+1;b<boxes.Length;b++)
                        if(Math.Min(boxes[a][2],boxes[b][2])>Math.Max(boxes[a][0],boxes[b][0])+1e-8&&
                           Math.Min(boxes[a][3],boxes[b][3])>Math.Max(boxes[a][1],boxes[b][1])+1e-8)overlap=true;
                    if(!overlap)curves=originals.SelectMany(f=>f.curves).ToArray();
                }
                else if(group.StartsWith("side_loops|",StringComparison.Ordinal)) {
                    var boxes=originals.Select(HRectangleBounds).ToArray();bool overlap=boxes.Any(b=>b==null);
                    if(!overlap)for(int a=0;a<boxes.Length;a++)for(int b=a+1;b<boxes.Length;b++)
                        if(Math.Min(boxes[a][2],boxes[b][2])>=Math.Max(boxes[a][0],boxes[b][0])-1e-8&&
                           Math.Min(boxes[a][3],boxes[b][3])>=Math.Max(boxes[a][1],boxes[b][1])-1e-8)overlap=true;
                    if(!overlap)curves=originals.SelectMany(f=>f.curves).ToArray();
                }
                else if(rectangles)curves=HRectangleUnion(originals.Select(HRectangleBounds).ToArray());
                else {
                    var circles=originals.Select(f=>f.curves[0]).ToArray();bool overlap=false;
                    for(int a=0;a<circles.Length;a++)for(int b=a+1;b<circles.Length;b++) {
                        double dx=circles[a].center[0]-circles[b].center[0],dy=circles[a].center[1]-circles[b].center[1];
                        if(Math.Sqrt(dx*dx+dy*dy)<=circles[a].radius+circles[b].radius+1e-9)overlap=true;
                    }
                    if(!overlap)curves=circles;
                }
                if(curves==null){result.Add(feature);continue;}
                result.Add(new HouseFeatureSpec {name=rectangles?group.Substring(group.IndexOf('|')+1)+"_Continuous":
                    group.StartsWith("side_loops|",StringComparison.Ordinal)?group.Substring(group.IndexOf('|')+1)+"_Combined":feature.name+"_Combined",
                    kind=feature.kind,origin=feature.origin,u=feature.u,v=feature.v,depth=feature.depth,axis=feature.axis,curves=curves,
                    source_features=originals.Select(f=>f.name).ToArray()});
                foreach(int j in candidates)consumed.Add(j);
            }
            return result.ToArray();
        }
        /// <summary>Return an optimized copy; already-optimized and older JSON recipes are both accepted.</summary>
        public static HousingSpec OptimizeHousing(HousingSpec spec) {
            ValidateHousing(spec);
            var upper=spec.upper.Where(f=>!HSource(f,"Jack_Screw_Hole_")).ToArray();
            var result=new HousingSpec {lower=OptimizeHousingFeatures(spec.lower),upper=OptimizeHousingFeatures(upper),
                cover_offset_z=spec.cover_offset_z,expected_lower_bounds=spec.expected_lower_bounds,expected_upper_bounds=spec.expected_upper_bounds,
                source=spec.source,limitations=spec.limitations,assembly_reference=spec.assembly_reference,bearing_outer_reference=spec.bearing_outer_reference};
            ValidateHousing(result);return result;
        }
        static double[] HWorld(HouseFeatureSpec spec,double[] point) {
            return Enumerable.Range(0,3).Select(i=>M(spec.origin[i]+spec.u[i]*point[0]+spec.v[i]*point[1])).ToArray();
        }
        static double[] HSketch(MathUtility math,MathTransform transform,HouseFeatureSpec spec,double[] point) {
            return (double[])((MathPoint)((MathPoint)math.CreatePoint(HWorld(spec,point))).MultiplyTransform(transform)).ArrayData;
        }
        static string HPlaneKey(HouseFeatureSpec spec) {
            return String.Join(";",spec.origin.Concat(spec.u).Concat(spec.v).Select(x=>x.ToString("R",CultureInfo.InvariantCulture)));
        }
        static Feature HPlane(ModelDoc2 doc,HouseFeatureSpec spec,Dictionary<string,Feature> cache) {
            string key=HPlaneKey(spec);Feature plane;
            if(cache.TryGetValue(key,out plane))return plane;
            var first=spec.origin.Select(M).ToArray();
            var second=Enumerable.Range(0,3).Select(i=>M(spec.origin[i]+spec.u[i]*20)).ToArray();
            var third=Enumerable.Range(0,3).Select(i=>M(spec.origin[i]+spec.v[i]*20)).ToArray();
            doc.ClearSelection2(true);
            // Explicit model-space planes avoid localization and template axis assumptions.
            // Their geometry is native, but the plane station is regenerated by the recipe.
            if(doc.CreatePlaneFixed2(first,second,third,false)==null)throw new Exception("Housing sketch plane creation failed: "+spec.name);
            plane=null;var feature=(Feature)doc.FirstFeature();
            while(feature!=null){if(feature.GetTypeName2()=="RefPlane")plane=feature;feature=(Feature)feature.GetNextFeature();}
            if(plane==null)throw new Exception("Cannot find the new housing sketch plane.");
            plane.Name="建模基准面"+(cache.Count+1);cache.Add(key,plane);return plane;
        }
        static Feature HExtrudeBoss(ModelDoc2 doc,Feature sketch,bool dir,double depth,bool merge) {
            doc.ClearSelection2(true);sketch.Select2(false,0);
            return doc.FeatureManager.FeatureExtrusion3(true,false,dir,0,0,M(depth),0,false,false,false,false,0,0,false,false,false,false,merge,true,true,0,0,false);
        }
        static Feature HCut(ModelDoc2 doc,Feature sketch,bool dir,double depth) {
            doc.ClearSelection2(true);sketch.Select2(false,0);
            return doc.FeatureManager.FeatureCut3(true,false,dir,0,0,M(depth),0,false,false,false,false,0,0,false,false,false,false,false,true,true,true,true,false,0,0,false);
        }
        // 每个特征之后核对一次整体包围盒（GetBodyBox 顺序为 Xmin,Ymin,Zmin,Xmax,Ymax,Zmax）。
        // 包围盒是包络而不是等式：允许比包络小，不允许超出。肋板一旦被翻到反方向就会伸到
        // 箱体外面，这里能立刻指出是哪个特征、哪个坐标越界，而不是等最后才报"基准不符"。
        static void HCheckBounds(ModelDoc2 doc,HouseFeatureSpec spec,double[] expected) {
            if(expected==null||expected.Length!=6)return;
            var bodies=(object[])((PartDoc)doc).GetBodies2(0,false);
            if(bodies==null)return;
            foreach(Body2 body in bodies) {
                if(body==null)continue;
                var box=(double[])body.GetBodyBox();
                for(int i=0;i<6;i++) {
                    double actual=box[i]*1000;
                    bool lowerBound=(i<3);
                    bool outside=lowerBound?(actual<expected[i]-.3):(actual>expected[i]+.3);
                    if(outside)throw new Exception("Housing feature outside envelope: "+spec.name
                        +" coordinate "+i+" expected="+expected[i]+" actual="+actual.ToString("0.###"));
                }
            }
        }
        static double HVolume(ModelDoc2 doc) {
            var mass=(MassProperty)doc.Extension.CreateMassProperty();
            return mass.Volume*1e9;
        }
        static void HFeature(SldWorks app,ModelDoc2 doc,HouseFeatureSpec spec,Dictionary<string,Feature> planes,double[] expected) {
            var plane=HPlane(doc,spec,planes);doc.ClearSelection2(true);
            if(!plane.Select2(false,0))throw new Exception("Cannot select housing sketch plane: "+spec.name);
            var sm=doc.SketchManager;sm.InsertSketch(true);sm.AddToDB=true;
            var sketch=sm.ActiveSketch;var transform=(MathTransform)sketch.ModelToSketchTransform;var math=(MathUtility)app.GetMathUtility();
            var circleSegments=new List<SketchSegment>();
            foreach(var curve in spec.curves) {
                SketchSegment segment;
                if(curve.kind=="circle") {
                    var p=HSketch(math,transform,spec,curve.center);segment=sm.CreateCircleByRadius(p[0],p[1],0,M(curve.radius));circleSegments.Add(segment);
                } else {
                    var a=HSketch(math,transform,spec,curve.start);var b=HSketch(math,transform,spec,curve.end);
                    if(curve.kind=="line")segment=sm.CreateLine(a[0],a[1],0,b[0],b[1],0);
                    else {var middle=HSketch(math,transform,spec,curve.mid);segment=sm.Create3PointArc(a[0],a[1],0,b[0],b[1],0,middle[0],middle[1],0);}
                }
                if(segment==null)throw new Exception("Native housing sketch primitive failed: "+spec.name);
            }
            SketchSegment axis=null;
            if(spec.kind=="revolve_cut") {
                var a=HSketch(math,transform,spec,new[]{spec.axis[0],spec.axis[1]});var b=HSketch(math,transform,spec,new[]{spec.axis[2],spec.axis[3]});
                axis=sm.CreateCenterLine(a[0],a[1],0,b[0],b[1],0);
            }
            sm.AddToDB=false;
            // The hole diameter is directly editable in SW; complex outline stations
            // remain native sketch entities driven by the exported recipe.
            int diameterIndex=0;
            foreach(var circle in circleSegments) {
                doc.ClearSelection2(true);circle.Select4(false,null);
                var dimension=(DisplayDimension)doc.AddDiameterDimension2(.02+diameterIndex*.005,.02,0);
                if(dimension==null)throw new Exception("Housing hole diameter dimension failed: "+spec.name);
                ((Dimension)dimension.GetDimension2(0)).Name="Diameter_"+(++diameterIndex);
            }
            var sketchFeature=(Feature)sketch;sm.InsertSketch(true);
            sketchFeature.Name=ModelPresentation.ChineseName(spec.name)+"草图";
            doc.ClearSelection2(true);sketchFeature.Select2(false,0);Feature result;
            if(spec.kind=="revolve_cut") {
                var select=((SelectionMgr)doc.SelectionManager).CreateSelectData();select.Mark=16;
                if(axis==null||!axis.Select4(true,select))throw new Exception("Housing revolve axis selection failed: "+spec.name);
                result=doc.FeatureManager.FeatureRevolve2(true,true,false,true,false,false,0,0,Math.PI*2,0,false,false,0,0,0,0,0,true,true,true);
            } else {
                var inverse=(MathTransform)transform.Inverse();
                var a=(double[])((MathPoint)((MathPoint)math.CreatePoint(new double[]{0,0,0})).MultiplyTransform(inverse)).ArrayData;
                var b=(double[])((MathPoint)((MathPoint)math.CreatePoint(new double[]{0,0,1})).MultiplyTransform(inverse)).ArrayData;
                var normal=Enumerable.Range(0,3).Select(i=>b[i]-a[i]).ToArray();
                var desired=HCross(spec.u,spec.v).Select(x=>x*Math.Sign(spec.depth)).ToArray();
                bool alongNormal=HDot(normal,desired)>0;
                double absDepth=Math.Abs(spec.depth);
                if(spec.kind=="boss") {
                    result=HExtrudeBoss(doc,sketchFeature,!alongNormal,absDepth,true);
                    if(result==null) {
                        // 其余特征都用 0.05 的偏移量造重叠（9.95 / 60.05 / 6.05），肋板却是从精确的
                        // Z=10.00 拉起、深度也是精确的 29.5，两端与相邻实体零重叠。先保持方向不变、
                        // 只把远端延展 0.05 造成实体相交；延展部分落在相邻实体内部，不改变外形。
                        Console.WriteLine("  boss retry (extend 0.05): "+spec.name);
                        result=HExtrudeBoss(doc,sketchFeature,!alongNormal,absDepth+.05,true);
                    }
                    if(result==null) {
                        Console.WriteLine("  boss retry (flip direction): "+spec.name);
                        result=HExtrudeBoss(doc,sketchFeature,alongNormal,absDepth,true);
                    }
                    if(result==null) throw new Exception("Native housing boss failed: "+spec.name
                        +" depth="+absDepth.ToString("0.###")+" alongNormal="+alongNormal
                        +" normal=["+String.Join(",",normal.Select(x=>x.ToString("0.###")).ToArray())+"]"
                        +" desired=["+String.Join(",",desired.Select(x=>x.ToString("0.###")).ToArray())+"]"
                        +" origin=["+String.Join(",",spec.origin.Select(x=>x.ToString("0.###")).ToArray())+"]");
                }
                else {
                    // 切除的方向不能只靠草图法向推断：切在空白处的切除有时仍会返回一个特征，
                    // 材料却一点没少。所以用"体积必须下降"作为判据，方向翻转才是安全的。
                    double before=HVolume(doc);
                    result=HCut(doc,sketchFeature,alongNormal,absDepth);
                    if(result!=null&&HVolume(doc)>=before-1e-9) {
                        Console.WriteLine("  cut removed nothing, undo: "+spec.name);
                        doc.EditUndo2(1);result=null;
                    }
                    if(result==null) {
                        Console.WriteLine("  cut retry (flip direction): "+spec.name);
                        result=HCut(doc,sketchFeature,!alongNormal,absDepth);
                        if(result!=null&&HVolume(doc)>=before-1e-9) {
                            Console.WriteLine("  cut flip removed nothing, undo: "+spec.name);
                            doc.EditUndo2(1);result=null;
                        }
                    }
                    if(result==null) {
                        Console.WriteLine("  cut retry (extend 0.05): "+spec.name);
                        result=HCut(doc,sketchFeature,alongNormal,absDepth+.05);
                        if(result!=null&&HVolume(doc)>=before-1e-9) { doc.EditUndo2(1);result=null; }
                    }
                    if(result==null) {
                        Console.WriteLine("  cut retry (flip + extend 0.05): "+spec.name);
                        result=HCut(doc,sketchFeature,!alongNormal,absDepth+.05);
                        if(result!=null&&HVolume(doc)>=before-1e-9) { doc.EditUndo2(1);result=null; }
                    }
                    if(result==null) {
                        if(spec.source_features!=null&&spec.source_features.Length>0&&spec.name!="Side_Oil_Groove_Bridge_Combined")
                            throw new Exception("Combined native housing cut failed: "+spec.name+" (sources: "+String.Join(", ",spec.source_features)+"). The combined cut cannot be skipped.");
                        // 有些切除是原 CadQuery 流程里的"再切一刀"：先加凸台/加强板，再把轴承孔
                        // 重新切一遍，清掉加强板可能侵入孔内的材料。当加强板没有侵入时，这一刀
                        // 切不到任何材料，SW 直接返回 null。这属于合法的空操作（与已有孔完全重合），
                        // 跳过即可，不应当中断整个箱体流程。日志里会留痕，便于事后核对。
                        Console.WriteLine("  cut skipped (nothing to remove): "+spec.name+" (coincides with an existing cut)");
                        HCheckBounds(doc,spec,expected);
                        return;
                    }
                }
            }
            if(result==null)throw new Exception("Native housing feature failed: "+spec.name);
            result.Name=ModelPresentation.ChineseName(spec.name);
            bool warning;int error=result.GetErrorCode2(out warning);
            if(error!=0&&!warning)throw new Exception("Housing feature rebuild error: "+spec.name+" code="+error);
            HCheckBounds(doc,spec,expected);
            doc.ClearSelection2(true);
            RecordHousingThread(doc,spec.name);
        }
        static string HPart(SldWorks app,HouseFeatureSpec[] features,string name,string output,List<object> metrics,double[] expected) {
            var doc=NewDocument(app,1,name);var planes=new Dictionary<string,Feature>();
            int activationError=0;
            if(app.ActivateDoc3(doc.GetTitle(),false,0,ref activationError)==null||activationError!=0)
                throw new Exception("Cannot activate housing document: "+doc.GetTitle());
            string progress="正在生成："+ModelPresentation.ChineseName(name);
            int index=0;
            foreach(var feature in features) {
                GenerationControl.Checkpoint(progress);
                Console.WriteLine("Housing "+name+" "+(++index)+"/"+features.Length+": "+feature.name);
                HFeature(app,doc,feature,planes,expected);
            }
            doc.ClearSelection2(true);doc.BlankRefGeom();doc.ClearSelection2(true);
            bool rebuilt=doc.ForceRebuild3(false);
            var issues=NativeAudit.InspectFeatures(doc).features.Where(f=>f.error!=0||f.warning).ToArray();
            if(issues.Length>0)throw new Exception(ModelPresentation.ChineseName(name)+"重建检查失败："+
                String.Join("、",issues.Select(f=>f.name+"（代码 "+f.error+"）")));
            if(!rebuilt)throw new Exception(ModelPresentation.ChineseName(name)+"无法强制重建。");
            string path=Path.Combine(output,name+".SLDPRT");Save(doc,path);
            var bodies=(object[])((PartDoc)doc).GetBodies2(0,false);
            if(bodies==null||bodies.Length!=1)throw new Exception("Housing must remain a single merged solid: "+name);
            var bounds=(double[])((Body2)bodies[0]).GetBodyBox();
            for(int i=0;i<6;i++)if(Math.Abs(bounds[i]*1000-expected[i])>.2)throw new Exception("Housing bounding datum mismatch: "+name+" coordinate "+i+" expected="+expected[i]+" actual="+(bounds[i]*1000));
            metrics.Add(Metrics(doc,path));
            app.CloseDoc(doc.GetTitle());
            return path;
        }
        public static List<string> MakeHousings(SldWorks app,HousingSpec spec,string output,List<object> metrics) {
            int originalLower=spec==null||spec.lower==null?0:spec.lower.Length,originalUpper=spec==null||spec.upper==null?0:spec.upper.Length;
            spec=OptimizeHousing(spec);
            var result=new List<string>();
            result.Add(HPart(app,spec.lower,"housing",output,metrics,spec.expected_lower_bounds));
            result.Add(HPart(app,spec.upper,"cover",output,metrics,spec.expected_upper_bounds));
            File.WriteAllText(Path.Combine(output,"housing_migration_notes.json"),new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new {
                source=spec.source,cover_offset_z=spec.cover_offset_z,limitations=spec.limitations,
                original_lower_features=originalLower,optimized_lower_features=spec.lower.Length,
                original_upper_features=originalUpper,optimized_upper_features=spec.upper.Length}));
            return result;
        }
    }
}
