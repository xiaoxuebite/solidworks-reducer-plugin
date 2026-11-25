// Involute/root-relief section adapted from the project's cq_gears/SpurGear:
// Copyright 2021 meadiode@github, licensed under the Apache License, Version 2.0.
// https://www.apache.org/licenses/LICENSE-2.0
// Native SW feature construction is implemented here independently.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using SolidWorks.Interop.sldworks;

namespace ReducerSW {
    public class GearShaftSection {
        public string kind {get;set;}
        public double diameter {get;set;}
        public double length {get;set;}
    }
    public class GearTrainSpec {
        public double module {get;set;}
        public double pressure_angle {get;set;}
        public double helix_angle {get;set;}
        public int pinion_teeth {get;set;}
        public int driven_teeth {get;set;}
        public double driven_width {get;set;}
        public double driven_bore {get;set;}
        public double driven_key_width {get;set;}
        public double driven_key_depth {get;set;}
        public double driven_mesh_phase_deg {get;set;}
        public GearShaftSection[] high_sections {get;set;}
        public Keyway high_keyway {get;set;}
    }

    // The involute and three-point root relief follow the existing cq_gears
    // transverse section. Native splines replace CQ's approximated side faces;
    // tip and root relief are exact circular arcs in the native sketch.
    public static partial class Generator {
        const int GearFlankSamples=100;
        // 齿距级建模开关：true 时先做齿坯再切一个齿槽，最后圆周阵列；false 回退到旧的"整圈草图"实现。
        // 旧实现把 z 个齿全部画进同一个草图，42 齿时为 84 条样条 + 84 段圆弧
        // （8400 个样条控制点），SW 的草图求解是主要耗时来源。
        static readonly bool UseCircularToothPattern=true;

        public static void ValidateGearTrain(GearTrainSpec spec) {
            if(spec==null)return;
            Positive(spec.module);Positive(spec.driven_width);Positive(spec.driven_bore);
            Positive(spec.driven_key_width);Positive(spec.driven_key_depth);
            if(Double.IsNaN(spec.pressure_angle)||Double.IsInfinity(spec.pressure_angle)||spec.pressure_angle<=0||spec.pressure_angle>=45)
                throw new ArgumentException("齿轮压力角必须在 0 到 45 度之间。");
            if(Double.IsNaN(spec.helix_angle)||Double.IsInfinity(spec.helix_angle)||Math.Abs(spec.helix_angle)>35)
                throw new ArgumentException("斜齿轮螺旋角必须在 -35° 到 35° 之间。");
            if(spec.pinion_teeth<6||spec.pinion_teeth>500||spec.driven_teeth<6||spec.driven_teeth>500)
                throw new ArgumentException("当前原生齿轮支持 6 到 500 齿。");
            if(Double.IsNaN(spec.driven_mesh_phase_deg)||Double.IsInfinity(spec.driven_mesh_phase_deg))
                throw new ArgumentException("齿轮啮合相位必须有限。");
            if(spec.high_sections==null||spec.high_sections.Length<3)
                throw new ArgumentException("高速轴需要包含齿轮段及两侧轴段。");
            int gearIndex=-1;
            for(int i=0;i<spec.high_sections.Length;i++) {
                var section=spec.high_sections[i];
                if(section==null)throw new ArgumentException("高速轴段不能为空。");
                Positive(section.length);Positive(section.diameter);
                if(section.kind=="gear") {if(gearIndex>=0)throw new ArgumentException("高速轴只能包含一个整体齿轮段。");gearIndex=i;}
                else if(section.kind!="cylinder")throw new ArgumentException("高速轴段类型必须是 cylinder 或 gear。");
            }
            if(gearIndex<=0||gearIndex>=spec.high_sections.Length-1)
                throw new ArgumentException("整体小齿轮段必须位于高速轴的中间。");
            double root=GearRootDiameter(spec.module,spec.pinion_teeth,spec.helix_angle);
            if(Math.Abs(spec.high_sections[gearIndex].diameter-root)>1e-6)
                throw new ArgumentException("整体小齿轮的齿根直径必须由模数和齿数派生，请同步更新轴段参数。");
            if(Math.Max(spec.high_sections[gearIndex-1].diameter,spec.high_sections[gearIndex+1].diameter)>root+1e-8)
                throw new ArgumentException("高速轴邻接轴段超过小齿轮齿根，原模型的连接芯体会填入齿槽。");
            double drivenRoot=GearRootDiameter(spec.module,spec.driven_teeth,spec.helix_angle)/2;
            if(spec.driven_key_width>=spec.driven_bore||spec.driven_bore/2+spec.driven_key_depth>=drivenRoot)
                throw new ArgumentException("从动齿轮孔和键槽必须保留齿根以内的轮毂壁厚。");
            if(spec.high_keyway==null)throw new ArgumentException("高速轴缺少外伸端键槽参数。");
            var key=spec.high_keyway;
            Positive(key.width);Positive(key.length);Positive(key.depth);
            if(Double.IsNaN(key.start)||Double.IsInfinity(key.start)||key.start<0||key.start+key.length>spec.high_sections[0].length+1e-8||key.length<=key.width||key.width>=spec.high_sections[0].diameter)
                throw new ArgumentException("高速轴键槽必须落在第一轴段内，且长度、宽度需要适合轴径。");
            if(key.depth>spec.high_sections[0].diameter/2-.5)
                throw new ArgumentException("高速轴键槽深度必须在轴心之外保留至少 0.5 mm，不会自动缩小输入深度。");
            GearToothGeometry(spec.module,spec.pinion_teeth,spec.pressure_angle,spec.helix_angle);
            GearToothGeometry(spec.module,spec.driven_teeth,spec.pressure_angle,spec.helix_angle);
        }

        static double GearRootDiameter(double normalModule,int teeth,double helixDegrees) {
            return normalModule*(teeth/Math.Cos(helixDegrees*Math.PI/180)-2.5);
        }

        sealed class ToothGeometry {
            public double pitch, tip, root, basis, flankStart, tau, phiRoot, phiTip;
            public double[] rootCenter, rootA, rootMid, rootB;
        }
        static ToothGeometry GearToothGeometry(double module,int teeth,double pressureDegrees,double helixDegrees=0) {
            double cosine=Math.Cos(helixDegrees*Math.PI/180);
            double transverse=module/cosine;
            double transversePressure=Math.Atan(Math.Tan(pressureDegrees*Math.PI/180)/cosine)*180/Math.PI;
            return GearToothGeometryTransverse(transverse,module,teeth,transversePressure);
        }
        static ToothGeometry GearToothGeometryTransverse(double transverseModule,double normalModule,int teeth,double pressureDegrees) {
            double alpha=pressureDegrees*Math.PI/180;
            var g=new ToothGeometry{pitch=transverseModule*teeth/2,tip=transverseModule*teeth/2+normalModule,root=transverseModule*teeth/2-1.25*normalModule,tau=2*Math.PI/teeth};
            g.basis=g.pitch*Math.Cos(alpha);g.flankStart=Math.Max(g.basis,g.root);
            Func<double,double> phi=radius=>Math.PI/(2*teeth)+Math.Tan(alpha)-alpha-Involute(Math.Acos(Math.Min(1,g.basis/radius)));
            g.phiRoot=phi(g.flankStart);g.phiTip=phi(g.tip);
            if(g.root<=0||g.phiTip<=0||g.tau<=2*g.phiRoot)
                throw new ArgumentException("齿轮横截面不闭合：请检查模数、齿数和压力角。");
            g.rootA=Polar(g.flankStart,-g.phiRoot);
            g.rootMid=Polar(g.root,-g.tau/2);
            g.rootB=Polar(g.flankStart,g.phiRoot-g.tau);
            g.rootCenter=Circumcenter(g.rootA,g.rootMid,g.rootB);
            return g;
        }
        static double Involute(double alpha){return Math.Tan(alpha)-alpha;}
        static double[] Polar(double radius,double angle){return new[]{radius*Math.Cos(angle),radius*Math.Sin(angle)};}
        static double[] Rotate2(double[] p,double angle){double c=Math.Cos(angle),s=Math.Sin(angle);return new[]{p[0]*c-p[1]*s,p[0]*s+p[1]*c};}
        static double[] Circumcenter(double[] a,double[] b,double[] c) {
            double d=2*(a[0]*(b[1]-c[1])+b[0]*(c[1]-a[1])+c[0]*(a[1]-b[1]));
            if(Math.Abs(d)<1e-14)throw new ArgumentException("齿根圆弧退化，无法生成原生齿形。");
            double aa=a[0]*a[0]+a[1]*a[1],bb=b[0]*b[0]+b[1]*b[1],cc=c[0]*c[0]+c[1]*c[1];
            return new[]{(aa*(b[1]-c[1])+bb*(c[1]-a[1])+cc*(a[1]-b[1]))/d,(aa*(c[0]-b[0])+bb*(a[0]-c[0])+cc*(b[0]-a[0]))/d};
        }
        static double NormalizeAngle(double value){value%=2*Math.PI;return value<0?value+2*Math.PI:value;}
        static short ArcDirection(double[] center,double[] start,double[] mid,double[] end) {
            double a=Math.Atan2(start[1]-center[1],start[0]-center[0]);
            double m=NormalizeAngle(Math.Atan2(mid[1]-center[1],mid[0]-center[0])-a);
            double b=NormalizeAngle(Math.Atan2(end[1]-center[1],end[0]-center[0])-a);
            return (short)(m<=b+1e-10?1:-1);
        }
        static double[][] FlankPoints(ToothGeometry g,int teeth,double pressureDegrees,bool right) {
            var points=new double[GearFlankSamples][];double alpha=pressureDegrees*Math.PI/180;
            for(int i=0;i<points.Length;i++) {
                double r=g.flankStart+(g.tip-g.flankStart)*i/(points.Length-1);
                double angle=Math.PI/(2*teeth)+Involute(alpha)-Involute(Math.Acos(Math.Min(1,g.basis/r)));
                points[right?points.Length-1-i:i]=Polar(r,right?-angle:angle);
            }
            return points;
        }

        // Dense reference points are for independent area/clearance checks only.
        // The actual SW geometry below uses native smooth B-splines and arcs.
        public static double[][] GearReferencePerimeter(double module,int teeth,double pressureDegrees,int samplesPerFlank=400) {
            var g=GearToothGeometry(module,teeth,pressureDegrees);var result=new List<double[]>();
            double alpha=pressureDegrees*Math.PI/180;
            for(int tooth=0;tooth<teeth;tooth++) {
                double rotation=-tooth*g.tau;
                Action<double[]> add=p=>result.Add(Rotate2(p,rotation));
                for(int i=0;i<samplesPerFlank;i++){double r=g.flankStart+(g.tip-g.flankStart)*i/(samplesPerFlank-1);double phi=Math.PI/(2*teeth)+Involute(alpha)-Involute(Math.Acos(Math.Min(1,g.basis/r)));add(Polar(r,phi));}
                for(int i=1;i<samplesPerFlank;i++)add(Polar(g.tip,g.phiTip-2*g.phiTip*i/(samplesPerFlank-1)));
                for(int i=samplesPerFlank-2;i>=0;i--){double r=g.flankStart+(g.tip-g.flankStart)*i/(samplesPerFlank-1);double phi=Math.PI/(2*teeth)+Involute(alpha)-Involute(Math.Acos(Math.Min(1,g.basis/r)));add(Polar(r,-phi));}
                double radius=Math.Sqrt(Math.Pow(g.rootA[0]-g.rootCenter[0],2)+Math.Pow(g.rootA[1]-g.rootCenter[1],2));
                double start=Math.Atan2(g.rootA[1]-g.rootCenter[1],g.rootA[0]-g.rootCenter[0]);
                short direction=ArcDirection(g.rootCenter,g.rootA,g.rootMid,g.rootB);
                double end=Math.Atan2(g.rootB[1]-g.rootCenter[1],g.rootB[0]-g.rootCenter[0]);
                double sweep=direction>0?NormalizeAngle(end-start):-NormalizeAngle(start-end);
                for(int i=1;i<samplesPerFlank;i++)add(new[]{g.rootCenter[0]+radius*Math.Cos(start+sweep*i/(samplesPerFlank-1)),g.rootCenter[1]+radius*Math.Sin(start+sweep*i/(samplesPerFlank-1))});
            }
            return result.ToArray();
        }

        sealed class GearSketchFrame {
            public MathUtility math;public MathTransform modelToSketch;public bool shaftFrame;public double normalX,start;
            public double[] Map(double[] originalXY) {
                // Drive pinion follows CQ's Ry(+90) before shaft assembly.
                // The separate wheel instead bakes the cyclic frame so that
                // original +Y (the bore keyway) is native +Z and remains +Z in assembly.
                double y=shaftFrame?originalXY[1]:originalXY[0],z=shaftFrame?-originalXY[0]:originalXY[1];
                var point=(MathPoint)math.CreatePoint(new[]{M(start),M(y),M(z)});
                return (double[])((MathPoint)point.MultiplyTransform(modelToSketch)).ArrayData;
            }
        }
        static GearSketchFrame BeginGearSketch(SldWorks app,ModelDoc2 doc,bool shaftFrame,double start=0) {
            Feature plane=(Feature)doc.FirstFeature();int count=0;
            while(plane!=null){if(plane.GetTypeName2()=="RefPlane"&&++count==3)break;plane=(Feature)plane.GetNextFeature();}
            if(plane==null)throw new Exception("零件模板没有右视基准面，无法沿本地 X 轴建齿轮。");
            if(Math.Abs(start)>1e-9) {
                double x=M(start);
                doc.ClearSelection2(true);
                if(doc.CreatePlaneFixed2(new[]{x,0.0,0.0},new[]{x,0.02,0.0},new[]{x,0.0,0.02},false)==null)
                    throw new Exception("斜齿轮轴向草图基准面创建失败。");
                for(var feature=(Feature)doc.FirstFeature();feature!=null;feature=(Feature)feature.GetNextFeature())
                    if(feature.GetTypeName2()=="RefPlane")plane=feature;
                ModelPresentation.NameCreated(plane,"HelicalGear_StartPlane");
            }
            doc.ClearSelection2(true);if(!plane.Select2(false,0))throw new Exception("齿轮基准面选择失败。");
            doc.SketchManager.InsertSketch(true);var sketch=doc.SketchManager.ActiveSketch;
            var math=(MathUtility)app.GetMathUtility();var transform=sketch.ModelToSketchTransform;
            var normal=(MathVector)math.CreateVector(new[]{0.0,0.0,1.0});
            var data=(double[])((MathVector)normal.MultiplyTransform((MathTransform)transform.Inverse())).ArrayData;
            if(Math.Abs(data[0])<.999999)throw new Exception("右视基准面的法向不沿 X，当前模板不适用。");
            doc.SketchManager.AddToDB=true;doc.SketchManager.DisplayWhenAdded=false;
            return new GearSketchFrame{math=math,modelToSketch=transform,shaftFrame=shaftFrame,normalX=data[0],start=start};
        }
        static void GearSpline(SketchManager manager,GearSketchFrame frame,double[][] points,double rotation) {
            var values=new double[points.Length*3];
            for(int i=0;i<points.Length;i++){var p=frame.Map(Rotate2(points[i],rotation));values[i*3]=p[0];values[i*3+1]=p[1];values[i*3+2]=0;}
            if(manager.CreateSpline2(values,false)==null)throw new Exception("原生渐开线样条创建失败。");
        }
        static void GearArc(SketchManager manager,GearSketchFrame frame,double[] center,double[] start,double[] mid,double[] end,double rotation) {
            var c=frame.Map(Rotate2(center,rotation));var a=frame.Map(Rotate2(start,rotation));var m=frame.Map(Rotate2(mid,rotation));var b=frame.Map(Rotate2(end,rotation));
            if(manager.CreateArc(c[0],c[1],0,a[0],a[1],0,b[0],b[1],0,ArcDirection(c,a,m,b))==null)throw new Exception("原生齿顶或齿根圆弧创建失败。");
        }
        static Feature FinishGearExtrude(ModelDoc2 doc,GearSketchFrame frame,double start,double length,string name) {
            var manager=doc.SketchManager;var sketch=(Feature)manager.ActiveSketch;
            manager.AddToDB=false;manager.DisplayWhenAdded=true;manager.InsertSketch(true);doc.ClearSelection2(true);sketch.Select2(false,0);
            var feature=doc.FeatureManager.FeatureExtrusion3(true,false,frame.normalX<0,0,0,M(length),0,false,false,false,false,0,0,false,false,false,false,true,true,true,start==0?0:3,M(Math.Abs(start)),(start<0)^(frame.normalX<0));
            if(feature==null)throw new Exception("原生齿形拉伸失败："+name);
            ModelPresentation.NameCreated(feature,name);ModelPresentation.NameCreated(sketch,name+"_Profile");return feature;
        }
        static Feature FinishGearCut(ModelDoc2 doc,GearSketchFrame frame,double start,double length,string name) {
            var manager=doc.SketchManager;var sketch=(Feature)manager.ActiveSketch;
            manager.AddToDB=false;manager.DisplayWhenAdded=true;manager.InsertSketch(true);doc.ClearSelection2(true);sketch.Select2(false,0);
            var feature=doc.FeatureManager.FeatureCut3(true,false,frame.normalX>0,0,0,M(length),0,false,false,false,false,0,0,false,false,false,false,false,true,true,true,true,false,start==0?0:3,M(Math.Abs(start)),(start<0)^(frame.normalX<0));
            if(feature==null)throw new Exception("原生齿轮切除失败："+name);
            ModelPresentation.NameCreated(feature,name);ModelPresentation.NameCreated(sketch,name+"_Profile");return feature;
        }
        // 复用轴承滚动体阵列已经验证过的轴创建逻辑（选两个默认基准面 -> InsertAxis2）。
        static Feature GearAxis(ModelDoc2 doc) {
            var axis=BearingAxis(doc);
            axis.Name="齿轮旋转轴线";
            return axis;
        }
        // 齿坯：先按齿顶圆拉出整圈圆柱（整体小齿轮沿齿轮段叠加到轴身上），再切齿槽。
        // 这样齿槽轮廓的上边界正好是齿顶圆，两个齿面顶端都落在齿顶圆上，
        // 轮廓天然闭合，不需要任何额外的收口几何。
        static void AddGearBlank(SldWorks app,ModelDoc2 doc,ToothGeometry g,double width,double start,bool shaftFrame,string name,bool rootOnly=false) {
            var frame=BeginGearSketch(app,doc,shaftFrame);var manager=doc.SketchManager;
            var center=frame.Map(new[]{0.0,0.0});
            if(manager.CreateCircleByRadius(center[0],center[1],0,M(rootOnly?g.root:g.tip))==null)throw new Exception("齿轮齿坯草图创建失败："+name);
            FinishGearExtrude(doc,frame,start,width,name);
        }
        static void GearLine(SketchManager manager,GearSketchFrame frame,double[] a,double[] b,double rotation) {
            var first=frame.Map(Rotate2(a,rotation));var second=frame.Map(Rotate2(b,rotation));
            if(manager.CreateLine(first[0],first[1],0,second[0],second[1],0)==null)
                throw new Exception("斜齿轮齿根连接线创建失败。");
        }
        static void AddHelicalTooth(SldWorks app,ModelDoc2 doc,ToothGeometry g,int teeth,
            double normalModule,double pressureDegrees,double helixDegrees,double width,double start,
            double phaseDegrees,bool shaftFrame,string name) {
            var frame=BeginGearSketch(app,doc,shaftFrame,start);var manager=doc.SketchManager;
            double angle=phaseDegrees*Math.PI/180;
            var left=FlankPoints(g,teeth,pressureDegrees,false);
            var right=FlankPoints(g,teeth,pressureDegrees,true);
            double inner=g.root-Math.Min(normalModule*.15,g.root*.02);
            var leftInner=Polar(inner,g.phiRoot);var rightInner=Polar(inner,-g.phiRoot);
            GearSpline(manager,frame,left,angle);
            GearArc(manager,frame,new[]{0.0,0.0},left[left.Length-1],Polar(g.tip,0),right[0],angle);
            GearSpline(manager,frame,right,angle);
            GearLine(manager,frame,right[right.Length-1],rightInner,angle);
            GearArc(manager,frame,new[]{0.0,0.0},rightInner,Polar(inner,0),leftInner,angle);
            GearLine(manager,frame,leftInner,left[0],angle);
            var profile=(Feature)manager.ActiveSketch;
            manager.AddToDB=false;manager.DisplayWhenAdded=true;manager.InsertSketch(true);
            doc.ClearSelection2(true);manager.Insert3DSketch(true);
            var path=(Feature)manager.ActiveSketch;
            if(manager.CreateLine(M(start),0,0,M(start+width),0,0)==null)throw new Exception("斜齿轮扫描轴线创建失败。");
            manager.Insert3DSketch(true);
            doc.ClearSelection2(true);
            if(!profile.Select2(false,1)||!path.Select2(true,4))throw new Exception("斜齿轮扫描截面或轴线选择失败。");
            double twist=width*Math.Tan(helixDegrees*Math.PI/180)/g.pitch;
            var feature=doc.FeatureManager.InsertProtrusionSwept4(false,false,8,false,false,0,0,false,0,0,0,0,true,true,true,twist,true,false,0,0);
            if(feature==null)throw new Exception("斜齿轮原生扭转扫描失败："+name);
            ModelPresentation.NameCreated(feature,name+"_TwistedTooth");ModelPresentation.NameCreated(profile,name+"_ToothProfile");ModelPresentation.NameCreated(path,name+"_AxialPath");
            if(!doc.EditRebuild3())throw new Exception("斜齿轮扭转扫描重建失败："+name);
            PatternGearTeeth(doc,feature,teeth);
        }
        // 一个齿槽的闭合轮廓（四段首尾相接）：
        //   第 i 齿右齿面(齿顶->齿根) -> 齿根过渡弧 -> 第 i+1 齿左齿面(齿根->齿顶) -> 齿顶弧(回到起点)
        static Feature AddGearGap(SldWorks app,ModelDoc2 doc,ToothGeometry g,int teeth,double pressureDegrees,double start,double width,double phaseDegrees,bool shaftFrame,string name) {
            var frame=BeginGearSketch(app,doc,shaftFrame);var manager=doc.SketchManager;
            double tau=g.tau;double angle=phaseDegrees*Math.PI/180;
            var right=FlankPoints(g,teeth,pressureDegrees,true);    // 齿顶 -> 齿根
            var left=FlankPoints(g,teeth,pressureDegrees,false);     // 齿根 -> 齿顶
            GearSpline(manager,frame,right,angle);
            GearArc(manager,frame,g.rootCenter,g.rootA,g.rootMid,g.rootB,angle);
            GearSpline(manager,frame,left,angle-tau);
            GearArc(manager,frame,new[]{0.0,0.0},Polar(g.tip,g.phiTip-tau),Polar(g.tip,-tau/2),Polar(g.tip,-g.phiTip),angle);
            // 轴向多切 1 mm、两侧各多切，保证切透齿坯而不留薄壳。
            return FinishGearCut(doc,frame,start-1.0,width+2.0,name);
        }
        // 把一个齿槽复制成一圈。
        // 采用 SolidWorks 宏里记录的圆周阵列调用约定：种子特征用 SelectionMark=4、
        // 阵列轴用 SelectionMark=1 显式加入选择集，DName 传 "NULL" 让 SW 用选中的方向。
        // 之前只选种子、把轴名塞进 DName 的调用方式在 SW2024 上返回 null。
        static void PatternGearTeeth(ModelDoc2 doc,Feature seed,int teeth) {
            if(teeth<2)return;
            var axis=GearAxis(doc);
            // Spacing 按弧度给定（整圈 2*PI），与本项目 InsertFeatureChamfer 传 PI/4 表示 45° 一致。
            string[][] variants={new[]{"NULL","true"},new[]{"NULL","false"},new[]{axis.Name,"false"}};
            foreach(string[] variant in variants) {
                doc.ClearSelection2(true);
                if(!doc.Extension.SelectByID2(seed.Name,"BODYFEATURE",0,0,0,false,4,null,0))continue;
                if(!doc.Extension.SelectByID2(axis.Name,"AXIS",0,0,0,true,1,null,0))continue;
                bool geometry=variant[1]=="true";
                var pattern=doc.FeatureManager.FeatureCircularPattern4(teeth,2*Math.PI,false,variant[0],geometry,true,false);
                if(pattern!=null) {
                    ModelPresentation.NameCreated(pattern,"GearToothPattern");
                    doc.EditRebuild3();
                    Console.WriteLine("  gear pattern ok: teeth="+teeth+" DName="+variant[0]+" geometry="+geometry);
                    return;
                }
                Console.WriteLine("  gear pattern retry failed: teeth="+teeth+" DName="+variant[0]+" geometry="+geometry);
            }
            throw new Exception("齿轮圆周阵列创建失败：齿数 "+teeth+"，种子 "+seed.Name+"，阵列轴 "+axis.Name+"。");
        }
        static void AddGearBody(SldWorks app,ModelDoc2 doc,GearTrainSpec spec,int teeth,double width,double start,double phaseDegrees,bool shaftFrame,string name) {
            var g=GearToothGeometry(spec.module,teeth,spec.pressure_angle,spec.helix_angle);
            double transversePressure=Math.Atan(Math.Tan(spec.pressure_angle*Math.PI/180)/Math.Cos(spec.helix_angle*Math.PI/180))*180/Math.PI;
            if(Math.Abs(spec.helix_angle)>1e-8) {
                if(!shaftFrame)AddGearBlank(app,doc,g,width,start,false,name+"_RootCore",true);
                AddHelicalTooth(app,doc,g,teeth,spec.module,transversePressure,spec.helix_angle,width,start,phaseDegrees,shaftFrame,name);
                return;
            }
            if(UseCircularToothPattern) {
                // 齿坯 + 单个齿槽 + 圆周阵列。小齿轮的齿坯叠加到已有的轴身上，
                // 独立齿轮则从零拉伸整圈齿坯。
                AddGearBlank(app,doc,g,width,start,shaftFrame,name+"_Blank");
                var gap=AddGearGap(app,doc,g,teeth,spec.pressure_angle,start,width,phaseDegrees,shaftFrame,name+"_Gap");
                PatternGearTeeth(doc,gap,teeth);
                return;
            }
            var frame=BeginGearSketch(app,doc,shaftFrame);var manager=doc.SketchManager;
            var left=FlankPoints(g,teeth,spec.pressure_angle,false);var right=FlankPoints(g,teeth,spec.pressure_angle,true);
            for(int tooth=0;tooth<teeth;tooth++) {
                double angle=phaseDegrees*Math.PI/180-tooth*g.tau;
                GearSpline(manager,frame,left,angle);
                GearArc(manager,frame,new[]{0.0,0.0},left[left.Length-1],Polar(g.tip,0),right[0],angle);
                GearSpline(manager,frame,right,angle);
                GearArc(manager,frame,g.rootCenter,g.rootA,g.rootMid,g.rootB,angle);
            }
            FinishGearExtrude(doc,frame,start,width,name);
        }
        static void CutDrivenGearBore(SldWorks app,ModelDoc2 doc,GearTrainSpec spec) {
            var frame=BeginGearSketch(app,doc,false);var manager=doc.SketchManager;var center=frame.Map(new[]{0.0,0.0});
            var circle=manager.CreateCircleByRadius(center[0],center[1],0,M(spec.driven_bore/2));
            if(circle==null)throw new Exception("从动齿轮轴孔草图创建失败。");
            manager.AddToDB=false;
            doc.ClearSelection2(true);((SketchPoint)((SketchArc)circle).GetCenterPoint2()).Select4(false,null);doc.SketchAddConstraints("sgFIXED");
            doc.ClearSelection2(true);circle.Select4(false,null);
            var diameter=(DisplayDimension)doc.AddDiameterDimension2(M(spec.driven_bore/2+8),M(spec.driven_bore/2+8),0);
            if(diameter==null)throw new Exception("从动齿轮轴孔尺寸创建失败。");((Dimension)diameter.GetDimension2(0)).Name="BoreDiameter";
            FinishGearCut(doc,frame,-.1,spec.driven_width+.2,"DrivenGear_BoreCut");
        }
        static void CutDrivenGearKeyway(SldWorks app,ModelDoc2 doc,GearTrainSpec spec) {
            var frame=BeginGearSketch(app,doc,false);var manager=doc.SketchManager;
            double overlap=Math.Max(1,Math.Max(spec.driven_key_width,spec.driven_key_depth));
            double low=spec.driven_bore/2-overlap,high=spec.driven_bore/2+spec.driven_key_depth;
            double[][] points={new[]{-spec.driven_key_width/2,low},new[]{spec.driven_key_width/2,low},new[]{spec.driven_key_width/2,high},new[]{-spec.driven_key_width/2,high}};
            for(int i=0;i<points.Length;i++){var a=frame.Map(points[i]);var b=frame.Map(points[(i+1)%points.Length]);manager.CreateLine(a[0],a[1],0,b[0],b[1],0);}
            double margin=Math.Max(1,spec.driven_key_width);FinishGearCut(doc,frame,-margin,spec.driven_width+margin*2,"DrivenGear_KeywayCut");
        }
        static void ChamferHighShaftEnds(ModelDoc2 doc,double total,double distance) {
            var bodies=(object[])((PartDoc)doc).GetBodies2(0,false);var chosen=new Dictionary<int,Edge>();var radii=new Dictionary<int,double>();
            foreach(Edge edge in (object[])((Body2)bodies[0]).GetEdges()) {
                var curve=(Curve)edge.GetCurve();if(!curve.IsCircle())continue;
                var p=(double[])curve.CircleParams;if(Math.Abs(p[3])<.99999)continue;
                int end=Math.Abs(p[0])<1e-7?0:(Math.Abs(p[0]-M(total))<1e-7?1:-1);
                if(end>=0&&(!radii.ContainsKey(end)||p[6]>radii[end])){chosen[end]=edge;radii[end]=p[6];}
            }
            if(chosen.Count!=2)throw new Exception("高速轴端面倒角的圆边识别失败。");
            doc.ClearSelection2(true);foreach(var edge in chosen.Values)((Entity)edge).Select4(true,null);
            var feature=doc.FeatureManager.InsertFeatureChamfer(0,1,M(distance),Math.PI/4,0,0,0,0);
            if(feature==null)throw new Exception("高速轴两端 C1 倒角创建失败。");ModelPresentation.NameCreated(feature,"HighShaft_EndChamfers");
        }
        public static List<string> MakeGearTrain(SldWorks app,GearTrainSpec spec,string output,List<object> metrics) {
            var files=new List<string>();if(spec==null)return files;ValidateGearTrain(spec);
            var drive=NewDocument(app,1,"drive_gear_shaft");var profile=new List<double[]>{new[]{0.0,0.0},new[]{0.0,spec.high_sections[0].diameter/2}};
            double x=0,gearStart=0,gearWidth=0;
            foreach(var s in spec.high_sections){if(s.kind=="gear"){gearStart=x;gearWidth=s.length;}profile.Add(new[]{x,s.diameter/2});x+=s.length;profile.Add(new[]{x,s.diameter/2});}
            profile.Add(new[]{x,0.0});Revolve(drive,profile.ToArray(),"HighShaft_CoreRevolve");
            AddGearBody(app,drive,spec,spec.pinion_teeth,gearWidth,gearStart,0,true,"Pinion_InvoluteExtrude");
            var input=spec.high_keyway;
            KeySlot(drive,new Keyway{segment_index=0,start=input.start,length=input.length,width=input.width,diameter=spec.high_sections[0].diameter,depth=input.depth},1);
            ChamferHighShaftEnds(drive,x,1);
            files.Add(SaveWithMetrics(app,drive,Path.Combine(output,"drive_gear_shaft.SLDPRT"),metrics));
            var wheel=NewDocument(app,1,"driven_gear");
            var wheelGear=new GearTrainSpec{module=spec.module,pressure_angle=spec.pressure_angle,helix_angle=-spec.helix_angle};
            AddGearBody(app,wheel,wheelGear,spec.driven_teeth,spec.driven_width,0,spec.driven_mesh_phase_deg,false,"DrivenGear_InvoluteExtrude");
            CutDrivenGearBore(app,wheel,spec);CutDrivenGearKeyway(app,wheel,spec);
            files.Add(SaveWithMetrics(app,wheel,Path.Combine(output,"driven_gear.SLDPRT"),metrics));
            File.WriteAllText(Path.Combine(output,"gear_geometry.json"),new JavaScriptSerializer().Serialize(new {
                module=spec.module,pressure_angle=spec.pressure_angle,helix_angle=spec.helix_angle,pinion_teeth=spec.pinion_teeth,driven_teeth=spec.driven_teeth,
                pinion_width=gearWidth,driven_width=spec.driven_width,driven_mesh_phase_deg=spec.driven_mesh_phase_deg,
                flank_method="Tip-radius blank extrude, one tooth gap cut (interpolating B-spline flanks plus exact tip and root-relief arcs) repeated by a native circular pattern; same involute section as PrecisionSpurGear.",
                root_method="Exact circular arc through cq_gears root/relief three points; not a generated trochoid.",
                limitations="Spur: patterned tooth gap. Helical: twisted sweep tooth and native circular pattern. Gear sketches are editable but not fully dimension-constrained; update geometry through plugin parameters."
            }));
            return files;
        }
    }
}
