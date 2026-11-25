using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ReducerSW {
    public class BearingSpec {
        public string name {get;set;} public string catalog_name {get;set;}
        public double inner_diameter {get;set;} public double outer_diameter {get;set;} public double width {get;set;}
    }
    public static partial class Generator {
        public static void ValidateBearing(BearingSpec spec) {
            if(spec==null)throw new ArgumentException("轴承参数不能为空。");
            if(spec.name!="high_bearing"&&spec.name!="low_bearing"&&spec.name!="input_bearing"&&spec.name!="intermediate_bearing"&&spec.name!="output_bearing")throw new ArgumentException("轴承标识不属于支持的轴系。");
            Positive(spec.inner_diameter);Positive(spec.outer_diameter);Positive(spec.width);
            if(spec.outer_diameter<=spec.inner_diameter)throw new ArgumentException("轴承外径必须大于内径。");
            double space=(spec.outer_diameter-spec.inner_diameter)/2;
            double ballR=space*.294;
            if(ballR<=.35)throw new ArgumentException("轴承径向空间不足以生成参考滚动体。");
            if(ballR*2>=spec.width-.06)throw new ArgumentException("轴承宽度不足：参考滚珠直径必须小于宽度减 0.06 mm，不能伸出端面。");
        }
        static Feature BearingCircleRevolve(ModelDoc2 doc,double x,double radiusCenter,double circleRadius,string name,bool sphere) {
            BeginSketch(doc);var sm=doc.SketchManager;
            var axis=sm.CreateCenterLine(-.01,sphere?M(radiusCenter):0,0,M(x+circleRadius)+.01,sphere?M(radiusCenter):0,0);
            var geometry=new List<SketchSegment>();
            if(sphere) {
                geometry.Add(sm.Create3PointArc(M(x-circleRadius),M(radiusCenter),0,M(x+circleRadius),M(radiusCenter),0,M(x),M(radiusCenter+circleRadius),0));
                geometry.Add(sm.CreateLine(M(x+circleRadius),M(radiusCenter),0,M(x-circleRadius),M(radiusCenter),0));
            } else geometry.Add(sm.CreateCircleByRadius(M(x),M(radiusCenter),0,M(circleRadius)));
            sm.AddToDB=false;
            foreach(var entity in geometry.Concat(new[]{axis})) {doc.ClearSelection2(true);entity.Select4(false,null);doc.SketchAddConstraints("sgFIXED");}
            var sketch=(Feature)sm.ActiveSketch;sm.InsertSketch(true);doc.ClearSelection2(true);sketch.Select2(false,0);
            var selection=((SelectionMgr)doc.SelectionManager).CreateSelectData();selection.Mark=16;axis.Select4(true,selection);
            // Non-sphere profiles are cut revolves. UseFeatScope=false cuts both rings;
            // sphere profiles have Merge=false so every rolling element stays a separate body.
            var feature=doc.FeatureManager.FeatureRevolve2(true,true,false,!sphere,false,false,0,0,Math.PI*2,0,false,false,0,0,0,0,0,!sphere,false,true);
            if(feature==null)throw new Exception("轴承旋转特征失败："+name);ModelPresentation.NameCreated(feature,name);ModelPresentation.NameCreated(sketch,name+"_Profile");return feature;
        }
        static Feature BearingAxis(ModelDoc2 doc) {
            var planes=new List<Feature>();var f=(Feature)doc.FirstFeature();
            while(f!=null&&planes.Count<2){if(f.GetTypeName2()=="RefPlane")planes.Add(f);f=(Feature)f.GetNextFeature();}
            if(planes.Count!=2)throw new Exception("轴承缺少默认基准面。");
            doc.ClearSelection2(true);planes[0].Select2(false,0);planes[1].Select2(true,0);
            if(!doc.InsertAxis2(true))throw new Exception("轴承阵列轴创建失败。");
            f=(Feature)doc.FirstFeature();Feature axis=null;
            while(f!=null){if(f.GetTypeName2()=="RefAxis")axis=f;f=(Feature)f.GetNextFeature();}
            if(axis==null)throw new Exception("轴承阵列轴未找到。");ModelPresentation.NameCreated(axis,"BearingAxis");return axis;
        }
        // ---------- Toolbox 标准件 ----------
        // Toolbox 的 GB 标准件就是一个 .sldprt，里面用配置装全部规格；按型号切配置即可。
        // sldworks interop 里没有任何 IToolbox 类型，这条路也不需要它。

        static string ToolboxRoot() {
            string root=System.Environment.GetEnvironmentVariable("REDUCER_TOOLBOX_ROOT");
            return String.IsNullOrEmpty(root)?@"C:\SOLIDWORKS Data\browser":root;
        }
        static string ToolboxPart(params string[] parts) {
            string path=ToolboxRoot();
            foreach(string part in parts)path=Path.Combine(path,part);
            return path;
        }
        static string DescribeConfigurations(ModelDoc2 doc) {
            var names=(object[])doc.GetConfigurationNames();
            if(names==null||names.Length==0)return "(空)";
            var list=new List<string>();
            foreach(object name in names){var text=name as string;if(!String.IsNullOrEmpty(text))list.Add(text);}
            if(list.Count>30)return list.Count+" 个："+String.Join(" | ",list.GetRange(0,30).ToArray())+" ...";
            return list.Count+" 个："+String.Join(" | ",list.ToArray());
        }
        // 配置名不一定等于型号，按"完全相等 -> 前缀 -> 包含"三级匹配。
        static string PickConfiguration(ModelDoc2 doc,string partNumber) {
            if(String.IsNullOrEmpty(partNumber))return null;
            var names=(object[])doc.GetConfigurationNames();
            if(names==null||names.Length==0)return null;
            var list=new List<string>();
            foreach(object name in names){var text=name as string;if(!String.IsNullOrEmpty(text))list.Add(text);}
            foreach(string name in list)if(String.Equals(name,partNumber,StringComparison.OrdinalIgnoreCase))return name;
            foreach(string name in list)if(name.StartsWith(partNumber,StringComparison.OrdinalIgnoreCase))return name;
            foreach(string name in list)if(name.IndexOf(partNumber,StringComparison.OrdinalIgnoreCase)>=0)return name;
            return null;
        }
        static double[] UnionBodyBox(ModelDoc2 doc) {
            var bodies=(object[])((PartDoc)doc).GetBodies2(0,false);
            if(bodies==null)return null;
            double[] box=null;
            foreach(object item in bodies) {
                var body=item as Body2;if(body==null)continue;
                var bb=(double[])body.GetBodyBox();
                if(box==null){box=new double[6];for(int i=0;i<6;i++)box[i]=bb[i];continue;}
                for(int i=0;i<3;i++){if(bb[i]<box[i])box[i]=bb[i];if(bb[i+3]>box[i+3])box[i+3]=bb[i+3];}
            }
            return box;
        }
        // 总装里所有零件都按"本地 X 是回转轴"映射到世界 ±Y。Toolbox 零件不一定如此，
        // 所以取包围盒最薄的一维当轴向，旋转到本地 X，再把中心平移回原位。
        static int AlignAxisToLocalX(SldWorks app,ModelDoc2 doc) {
            var box=UnionBodyBox(doc);
            if(box==null)return -1;
            double dx=box[3]-box[0],dy=box[4]-box[1],dz=box[5]-box[2];
            int thin=2;
            if(dx<=dy&&dx<=dz)thin=0;else if(dy<=dz)thin=1;
            if(thin==0)return 0;
            double[] motion;
            if(thin==2)motion=new double[]{0,0,1,0,1,0,-1,0,0,0,0,0,1,0,0,0};   // 本地 Z -> X
            else         motion=new double[]{0,1,0,-1,0,0,0,0,1,0,0,0,1,0,0,0};   // 本地 Y -> X
            double[] center={(box[0]+box[3])/2,(box[1]+box[4])/2,(box[2]+box[5])/2};
            var math=(MathUtility)app.GetMathUtility();
            var rotate=(MathTransform)math.CreateTransform(motion);
            foreach(object item in (object[])((PartDoc)doc).GetBodies2(0,false)){var body=item as Body2;if(body!=null)body.ApplyTransform(rotate);}
            doc.EditRebuild3();
            var after=UnionBodyBox(doc);
            if(after!=null) {
                double[] move={1,0,0,0,1,0,0,0,1,
                    center[0]-(after[0]+after[3])/2,center[1]-(after[1]+after[4])/2,center[2]-(after[2]+after[5])/2,1,0,0,0};
                var shift=(MathTransform)math.CreateTransform(move);
                foreach(object item in (object[])((PartDoc)doc).GetBodies2(0,false)){var body=item as Body2;if(body!=null)body.ApplyTransform(shift);}
                doc.EditRebuild3();
            }
            return thin;
        }
        // 从 Toolbox 库按型号取深沟球轴承，另存到本次输出目录，后续总装流程不变。
        // 旧的 Toolbox 取件实现保留但不再调用：GB 库零件只有 Default/PreviewCfg 两个配置，
        // 规格由 Toolbox 插件按需生成，且 sldtoolboxdata 是纯 IDispatch，无法脚本化指定型号。
        public static List<string> MakeBearingsFromToolbox(SldWorks app,BearingSpec[] specs,string output,List<object> metrics) {
            var paths=new List<string>();
            foreach(var spec in specs??new BearingSpec[0]) {
                ValidateBearing(spec);
                string library=ToolboxPart("GB","bearing","rolling bearings","deep groove ball bearings gb.sldprt");
                if(!File.Exists(library))throw new Exception("Toolbox 轴承库不存在："+library
                    +"（可用 REDUCER_TOOLBOX_ROOT 指定 C:\\SOLIDWORKS Data\\browser）");
                string path=Path.Combine(output,spec.name+".SLDPRT");
                int error=0,warning=0;
                var doc=app.OpenDoc6(library,1,1,"",ref error,ref warning);
                if(doc==null)throw new Exception("打开 Toolbox 轴承库失败："+library+" error="+error);
                try {
                    string config=PickConfiguration(doc,spec.catalog_name);
                    if(config==null)throw new Exception("Toolbox 轴承库中没有型号 "+spec.catalog_name+" 的配置；实际配置："
                        +DescribeConfigurations(doc));
                    if(!doc.ShowConfiguration2(config))throw new Exception("切换轴承配置失败："+config);
                    doc.EditRebuild3();
                    int axis=AlignAxisToLocalX(app,doc);
                    Console.WriteLine("  bearing "+spec.name+" <- Toolbox "+spec.catalog_name+" (config "+config+", axis "+axis+")");
                    int saveErrors=0,saveWarnings=0;
                    if(!doc.Extension.SaveAs(path,0,1,null,ref saveErrors,ref saveWarnings)||saveErrors!=0)
                        throw new Exception("轴承另存失败："+path+" errors="+saveErrors);
                    metrics.Add(Metrics(doc,path,true));
                    paths.Add(path);
                } finally {
                    app.CloseDoc(doc.GetTitle());
                }
            }
            return paths;
        }
        static int BearingBallCount(double pitch,double radius) {
            return Math.Min(Math.Max((int)(2*Math.PI*pitch/Math.Max(radius*2.35,1)),8),36);
        }
        static void BearingNear(double actual,double expected,string description) {
            if(Double.IsNaN(actual)||Double.IsInfinity(actual)||Math.Abs(actual-expected)>1e-5)
                throw new Exception("轴承几何校验失败："+description+"，实际="+actual+"，预期="+expected+" mm。");
        }
        // Read the actual analytic surfaces. Count and volume alone cannot
        // distinguish a correctly placed sphere from one rotated about Z.
        public static void ValidateBearingGeometry(ModelDoc2 doc,BearingSpec spec) {
            ValidateBearing(spec);
            double pitch=(spec.inner_diameter+spec.outer_diameter)/4;
            double radius=(spec.outer_diameter-spec.inner_diameter)/2*.294;
            int count=BearingBallCount(pitch,radius),raceBodies=0;
            var angles=new List<double>();
            var bodies=(object[])((PartDoc)doc).GetBodies2(0,false)??new object[0];
            foreach(Body2 body in bodies) {
                double[] ball=null;bool torus=false;
                foreach(Face2 face in (object[])body.GetFaces()) {
                    var surface=(Surface)face.GetSurface();
                    if(surface.IsSphere())ball=(double[])surface.SphereParams;
                    if(surface.IsTorus()) {
                        torus=true;var p=(double[])surface.TorusParams;
                        BearingNear(p[0]*1000,spec.width/2,spec.name+" 滚道中心 X");
                        BearingNear(p[1]*1000,0,spec.name+" 滚道中心 Y");BearingNear(p[2]*1000,0,spec.name+" 滚道中心 Z");
                        BearingNear(Math.Abs(p[3]),1,spec.name+" 滚道轴向 X");
                        BearingNear(p[4],0,spec.name+" 滚道轴向 Y");BearingNear(p[5],0,spec.name+" 滚道轴向 Z");
                        BearingNear(p[6]*1000,pitch,spec.name+" 滚道节圆半径");
                        BearingNear(p[7]*1000,radius*1.04,spec.name+" 滚道截面半径");
                    }
                }
                if(torus)raceBodies++;
                if(ball==null)continue;
                BearingNear(ball[0]*1000,spec.width/2,spec.name+" 滚珠中心 X（应处于 YZ 平面）");
                BearingNear(Math.Sqrt(ball[1]*ball[1]+ball[2]*ball[2])*1000,pitch,spec.name+" 滚珠节圆半径");
                BearingNear(ball[3]*1000,radius,spec.name+" 滚珠半径");
                double angle=Math.Atan2(ball[2],ball[1]);angles.Add(angle<0?angle+2*Math.PI:angle);
            }
            if(bodies.Length!=count+2||angles.Count!=count||raceBodies!=2)
                throw new Exception("轴承必须包含两个带环面滚道的套圈及 "+count+" 个滚珠："+spec.name);
            angles.Sort();
            for(int i=0;i<count;i++) {
                double gap=(i==count-1?angles[0]+2*Math.PI:angles[i+1])-angles[i];
                if(Math.Abs(gap-2*Math.PI/count)>1e-7)throw new Exception("轴承滚珠未沿 X 轴等角分布："+spec.name);
            }
            if(2*pitch*Math.Sin(Math.PI/count)<=2*radius)
                throw new Exception("轴承相邻滚珠发生干涉："+spec.name);
        }
        static ModelDoc2 MakeBearingModel(SldWorks app,BearingSpec spec) {
                ValidateBearing(spec);var doc=NewDocument(app,1,spec.name);
                try {
                double bore=spec.inner_diameter/2,outer=spec.outer_diameter/2,width=spec.width;
                double space=outer-bore;
                double ballR=space*.294;                        // 深沟球轴承球半径约为径向厚度的 0.294 倍
                double pitch=(bore+outer)/2;                    // 球心所在半径，也是沟槽中心
                double ring=ballR*.72;                          // 沟槽在两圈上的切入基准
                double outerBore=pitch+ring,innerOD=pitch-ring;  // 外圈内表面 / 内圈外表面
                double grooveR=ballR*1.04;                      // 沟槽圆弧半径，略大于球半径
                RevolvePolygon(doc,new[]{new[]{0.0,outerBore},new[]{0.0,outer},new[]{width,outer},new[]{width,outerBore}},"OuterRing",false,false);
                RevolvePolygon(doc,new[]{new[]{0.0,bore},new[]{0.0,innerOD},new[]{width,innerOD},new[]{width,bore}},"InnerRing",false,false);
                // 环面沟槽：沟槽圆截面绕轴旋转一周后切除，内外圈同时成形。
                BearingCircleRevolve(doc,width/2,pitch,grooveR,"RacewayGroove",false);
                // The seed and both toroidal grooves use the same axial and
                // radial center. Pattern this body about an explicit X axis.
                BearingCircleRevolve(doc,width/2,pitch,ballR,"SeedBall",true);
                Body2 seed=null;
                foreach(Body2 body in (object[])((PartDoc)doc).GetBodies2(0,false)){var box=(double[])body.GetBodyBox();if(box[1]>0)seed=body;}
                if(seed==null)throw new Exception("轴承滚动体实体未找到。");
                int count=BearingBallCount(pitch,ballR);
                var axis=BearingAxis(doc);doc.ClearSelection2(true);
                var selected=((SelectionMgr)doc.SelectionManager).CreateSelectData();selected.Mark=256;
                if(!seed.Select2(false,selected)||!axis.Select2(true,1))throw new Exception("轴承滚珠阵列实体或轴线选择失败。");
                var definition=(CircularPatternFeatureData)doc.FeatureManager.CreateDefinition((int)swFeatureNameID_e.swFmCirPattern);
                definition.BodyPattern=true;definition.PatternBodyArray=new object[]{seed};definition.Axis=axis;
                definition.EqualSpacing=true;definition.Spacing=2*Math.PI;definition.TotalInstances=count;
                definition.GeometryPattern=true;
                var pattern=doc.FeatureManager.CreateFeature(definition);
                if(pattern==null)throw new Exception("轴承滚珠实体圆周阵列创建失败。");
                ModelPresentation.NameCreated(pattern,"RollingElements_CircularPattern");doc.EditRebuild3();
                bool warning;int code=pattern.GetErrorCode2(out warning);
                if(code!=0&&!warning)throw new Exception("轴承滚珠圆周阵列重建失败："+code);
                doc.ClearSelection2(true);axis.Select2(false,0);doc.BlankRefGeom();doc.ClearSelection2(true);
                ValidateBearingGeometry(doc,spec);
                return doc;
                } catch {app.CloseDoc(doc.GetTitle());throw;}
        }
        // Rings and raceways stay unchanged; one native body pattern positions
        // every ball in the YZ rolling plane before saving the editable part.
        public static List<string> MakeBearings(SldWorks app,BearingSpec[] specs,string output,List<object> metrics) {
            var paths=new List<string>();
            foreach(var spec in specs??new BearingSpec[0]) {
                var doc=MakeBearingModel(app,spec);bool closed=false;
                try {
                string path=Path.Combine(output,spec.name+".SLDPRT");
                metrics.Add(Metrics(doc,path,true));paths.Add(path);
                SaveAndClose(app,doc,path);closed=true;
                } finally {if(!closed)app.CloseDoc(doc.GetTitle());}
            }
            return paths;
        }
    }
}
