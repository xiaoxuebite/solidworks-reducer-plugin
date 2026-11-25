using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ReducerSW {
    public static partial class Generator {
        static void RegisterName(HashSet<string> names,string name) {
            if(String.IsNullOrEmpty(name) || !System.Text.RegularExpressions.Regex.IsMatch(name,@"\A[a-zA-Z][a-zA-Z0-9_]{0,63}\z") || System.Text.RegularExpressions.Regex.IsMatch(name,@"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\z",System.Text.RegularExpressions.RegexOptions.IgnoreCase) || !names.Add(name)) throw new ArgumentException("零件名称必须唯一，只能包含字母、数字、下划线，且不能是系统保留名称。");
        }
        static void ValidateRotaryParts(Parameters p,HashSet<string> names) {
            foreach(var cover in p.through_covers??new ThroughCover[0]) {
                if(cover==null || (cover.name!="low_through_cover" && cover.name!="high_through_cover"))throw new ArgumentException("通盖标识必须是 low_through_cover 或 high_through_cover。");
                RegisterName(names,cover.name);RegisterName(names,cover.name.Replace("_through_cover","_oil_seal"));
                if(cover.outer_steps==null || cover.inner_steps==null || cover.outer_steps.Length==0 || cover.inner_steps.Length==0)throw new ArgumentException("通盖必须包含外台阶和内腔。");
                foreach(var s in cover.outer_steps){if(s==null||s.Length!=2)throw new ArgumentException("外台阶格式为直径、长度。");foreach(double v in s)Positive(v);}
                foreach(var s in cover.inner_steps){if(s==null||s.Length!=3)throw new ArgumentException("内腔格式为起始直径、终止直径、长度。");foreach(double v in s)Positive(v);}
                Positive(cover.seal_bore_diameter);
                if(cover.seal_shaft_segment_index.HasValue) {
                    int index=cover.seal_shaft_segment_index.Value;
                    if(index<0||index>=p.shaft_segments.Length||Math.Abs(cover.seal_bore_diameter-p.shaft_segments[index].diameter)>1e-7)throw new ArgumentException("低速轴油封孔径必须与联动轴段一致。");
                }
                if(cover.inner_steps.Sum(s=>s[2])+1e-8<cover.outer_steps.Sum(s=>s[1]))throw new ArgumentException("通盖内腔长度必须贯穿外部总长。");
                CheckThroughFit(cover);
            }
            foreach(var r in p.rotary_parts??new RotaryPart[0]) {if(r==null)throw new ArgumentException("旋转零件不能为 null。");RegisterName(names,r.name);ValidateSection(r.section);}
        }
        static double Cross(double[] a,double[] b,double[] c){return (b[0]-a[0])*(c[1]-a[1])-(b[1]-a[1])*(c[0]-a[0]);}
        static bool Intersect(double[] a,double[] b,double[] c,double[] d) {
            const double e=1e-9;
            if(Math.Max(a[0],b[0])+e<Math.Min(c[0],d[0])||Math.Max(c[0],d[0])+e<Math.Min(a[0],b[0])||Math.Max(a[1],b[1])+e<Math.Min(c[1],d[1])||Math.Max(c[1],d[1])+e<Math.Min(a[1],b[1]))return false;
            return Cross(a,b,c)*Cross(a,b,d)<=e && Cross(c,d,a)*Cross(c,d,b)<=e;
        }
        public static void ValidateSection(double[][] points) {
            if(points==null||points.Length<3)throw new ArgumentException("旋转截面至少需要三个顶点。");
            double area=0;
            for(int i=0;i<points.Length;i++) {
                var a=points[i];if(a==null||a.Length!=2||a.Any(v=>double.IsNaN(v)||double.IsInfinity(v))||a[0]<0||a[1]<0)throw new ArgumentException("截面顶点需要非负的有限轴向坐标和半径。");
                var b=points[(i+1)%points.Length];
                if(b==null||b.Length!=2)throw new ArgumentException("截面顶点需要两个坐标。");
                if(Math.Abs(a[0]-b[0])+Math.Abs(a[1]-b[1])<1e-8)throw new ArgumentException("截面不能包含重复的相邻顶点，闭合点无需重复输入。");
                area+=a[0]*b[1]-b[0]*a[1];
            }
            if(Math.Abs(area)<1e-8)throw new ArgumentException("截面面积必须大于零。");
            for(int i=0;i<points.Length;i++)for(int j=i+1;j<points.Length;j++) {
                if(j==i+1||(i==0&&j==points.Length-1))continue;
                if(Intersect(points[i],points[(i+1)%points.Length],points[j],points[(j+1)%points.Length]))throw new ArgumentException("旋转截面不能自交或重复接触。");
            }
        }
        public static double[][] OilSealSection(ThroughCover cover) {
            var steps=cover.inner_steps.Take(5).ToArray();double diameter=steps.Max(s=>Math.Max(s[0],s[1])),x=0;
            for(int i=0;i<steps.Length;i++) {
                var s=steps[i];
                if(Math.Abs(s[0]-diameter)<1e-8&&Math.Abs(s[1]-diameter)<1e-8) {
                    double left=i>0&&Math.Abs(steps[i-1][1]-diameter)<1e-8?steps[i-1][2]:0;
                    double right=i+1<steps.Length&&Math.Abs(steps[i+1][0]-diameter)<1e-8?steps[i+1][2]:0;
                    double center=x+s[2]/2,width=s[2]+left+right;
                    if(cover.seal_bore_diameter>=diameter)throw new ArgumentException("油封孔径必须小于外径。");
                    return new[]{new[]{x,diameter/2},new[]{x+s[2],diameter/2},new[]{center+width/2,cover.seal_bore_diameter/2},new[]{center-width/2,cover.seal_bore_diameter/2}};
                }
                x+=s[2];
            }
            throw new ArgumentException("通盖前五级内腔没有可识别的油封座。");
        }
        public static object CheckThroughFit(ThroughCover cover) {
            double wall=Double.PositiveInfinity,clearance=Double.PositiveInfinity,outerX=0;
            foreach(var outer in cover.outer_steps) {
                double innerX=0;
                foreach(var inner in cover.inner_steps) {
                    double lo=Math.Max(outerX,innerX),hi=Math.Min(outerX+outer[1],innerX+inner[2]);
                    if(hi-lo>1e-9) {
                        double a=inner[0]+(inner[1]-inner[0])*(lo-innerX)/inner[2];
                        double b=inner[0]+(inner[1]-inner[0])*(hi-innerX)/inner[2];
                        wall=Math.Min(wall,(outer[0]-Math.Max(a,b))/2);
                        clearance=Math.Min(clearance,(Math.Min(a,b)-cover.seal_bore_diameter)/2);
                    }
                    innerX+=inner[2];
                }
                outerX+=outer[1];
            }
            if(wall<=1e-8)throw new ArgumentException("通盖内腔穿出外壁："+cover.name);
            if(clearance< -1e-8)throw new ArgumentException("通盖内孔与对应轴径干涉："+cover.name);
            var seal=OilSealSection(cover);ValidateSection(seal);
            if(seal.Max(s=>s[0])>outerX+1e-8)throw new ArgumentException("油封截面超出通盖轴向范围："+cover.name);
            double sealGap=Double.PositiveInfinity,innerStart=0;
            foreach(var step in cover.inner_steps) {
                for(int i=0;i<seal.Length;i++) {
                    var a=seal[i];var b=seal[(i+1)%seal.Length];
                    if(Math.Abs(a[0]-b[0])<1e-9)continue;
                    double lo=Math.Max(innerStart,Math.Min(a[0],b[0])),hi=Math.Min(innerStart+step[2],Math.Max(a[0],b[0]));
                    if(hi<lo)continue;
                    foreach(double at in new[]{lo,hi}) {
                        double cavity=(step[0]+(step[1]-step[0])*(at-innerStart)/step[2])/2;
                        double sealRadius=a[1]+(b[1]-a[1])*(at-a[0])/(b[0]-a[0]);
                        sealGap=Math.Min(sealGap,cavity-sealRadius);
                    }
                }
                innerStart+=step[2];
            }
            if(sealGap< -1e-8)throw new ArgumentException("油封外轮廓与通盖内腔干涉："+cover.name);
            return new {part=cover.name,min_wall_mm=wall,min_shaft_radial_clearance_mm=clearance,min_seal_cavity_radial_clearance_mm=sealGap,seal_bore_mm=cover.seal_bore_diameter,note="Nominal seal/shaft contact; elastic compression and installed axial positions are not evaluated."};
        }
        static Feature RevolvePolygon(ModelDoc2 doc,double[][] points,string name,bool cut=false,bool merge=true) {
            ValidateSection(points);BeginSketch(doc);var sm=doc.SketchManager;
            var lines=new List<SketchSegment>();
            for(int i=0;i<points.Length;i++){var a=points[i];var b=points[(i+1)%points.Length];lines.Add(sm.CreateLine(M(a[0]),M(a[1]),0,M(b[0]),M(b[1]),0));}
            var axis=sm.CreateCenterLine(-.01,0,0,.2,0,0);var vertical=sm.CreateCenterLine(0,-.01,0,0,.2,0);
            sm.AddToDB=false;
            foreach(var construction in new[]{axis,vertical}){doc.ClearSelection2(true);construction.Select4(false,null);doc.SketchAddConstraints("sgFIXED");}
            for(int i=0;i<points.Length;i++) {
                var point=(SketchPoint)((SketchLine)lines[i]).GetStartPoint2();
                for(int coordinate=0;coordinate<2;coordinate++) {
                    doc.ClearSelection2(true);point.Select4(false,null);(coordinate==0?vertical:axis).Select4(true,null);
                    if(points[i][coordinate]==0){doc.SketchAddConstraints("sgCOINCIDENT");continue;}
                    DisplayDimension display=coordinate==0?(DisplayDimension)doc.AddHorizontalDimension2(M(points[i][0]/2),M(points[i][1]+5+i),0):(DisplayDimension)doc.AddVerticalDimension2(M(points[i][0]+5+i),M(points[i][1]/2),0);
                    if(display==null)throw new Exception("截面坐标尺寸创建失败："+name+" #"+i);
                    ((Dimension)display.GetDimension2(0)).Name=(coordinate==0?"Station_":"Radius_")+(i+1);
                }
            }
            if(sm.ActiveSketch.GetConstrainedStatus()!=(int)swConstrainedStatus_e.swFullyConstrained)throw new Exception("复杂旋转截面未完全约束："+name);
            var sketch=(Feature)sm.ActiveSketch;sm.InsertSketch(true);doc.ClearSelection2(true);sketch.Select2(false,0);
            var selection=((SelectionMgr)doc.SelectionManager).CreateSelectData();selection.Mark=16;axis.Select4(true,selection);
            var feature=doc.FeatureManager.FeatureRevolve2(true,true,false,cut,false,false,0,0,Math.PI*2,0,false,false,0,0,0,0,0,merge,true,true);
            if(feature==null)throw new Exception("复杂旋转特征创建失败："+name);
            ModelPresentation.NameCreated(feature,name);ModelPresentation.NameCreated(sketch,name+"_Profile");return feature;
        }
        static string MakeRotaryPart(SldWorks app,RotaryPart part,string output,List<object> metrics) {
            var doc=NewDocument(app,1,part.name);RevolvePolygon(doc,part.section,"Section_Revolve");return SaveWithMetrics(app,doc,Path.Combine(output,part.name+".SLDPRT"),metrics);
        }
        static string MakeThroughCover(SldWorks app,ThroughCover cover,string output,List<object> metrics) {
            var doc=NewDocument(app,1,cover.name);double x=0;
            var outer=new List<double[]>{new[]{0.0,0.0},new[]{0.0,cover.outer_steps[0][0]/2}};
            foreach(var s in cover.outer_steps){outer.Add(new[]{x,s[0]/2});x+=s[1];outer.Add(new[]{x,s[0]/2});}outer.Add(new[]{x,0.0});
            Revolve(doc,outer.ToArray(),"OuterSteps_Revolve");
            var cavity=new List<double[]>{new[]{0.0,0.0}};x=0;
            foreach(var s in cover.inner_steps){var start=new[]{x,s[0]/2};if(cavity.Last()[0]!=start[0]||cavity.Last()[1]!=start[1])cavity.Add(start);x+=s[2];cavity.Add(new[]{x,s[1]/2});}cavity.Add(new[]{x,0.0});
            RevolvePolygon(doc,cavity.ToArray(),"InnerCavity_RevolveCut",true);
            string path=Path.Combine(output,cover.name+".SLDPRT");return SaveWithMetrics(app,doc,path,metrics);
        }
    }
}
