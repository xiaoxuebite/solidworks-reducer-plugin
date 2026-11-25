using System;
using System.Collections.Generic;
using System.IO;
using SolidWorks.Interop.sldworks;

namespace ReducerSW {
    public class AuxiliarySpec {
        public string name {get;set;} public string kind {get;set;} public string notes {get;set;}
        public double length {get;set;} public double width {get;set;} public double height {get;set;}
        public double corner_radius {get;set;} public double inner_square {get;set;}
        public double hole_diameter {get;set;} public double hole_pitch_x {get;set;} public double hole_pitch_y {get;set;}
        public double start_x {get;set;} public double start_z {get;set;}
        public string shaft_link {get;set;} public int segment_index {get;set;} public double start_offset {get;set;}
        public double shaft_diameter {get;set;} public double shaft_slot_depth {get;set;}
        public double[][] section {get;set;} public double[][] axial_hole_profile {get;set;}
        public double side_hole_diameter {get;set;} public double side_hole_x {get;set;} public double side_hole_depth {get;set;}
        public double inner_diameter {get;set;} public double outer_diameter {get;set;}
        public double front_thickness {get;set;} public double scallop_radius {get;set;}
        public double outer_arc_radius {get;set;} public double inner_arc_radius {get;set;}
        public double outer_arc_height {get;set;} public double inner_arc_height {get;set;}
        public double right_vertical_x {get;set;} public double upper_step_length {get;set;} public double lower_return_length {get;set;}
        public double drop_1 {get;set;} public double drop_2 {get;set;} public double drop_3 {get;set;}
        public double hex_diameter {get;set;} public double hex_offset {get;set;}
        public double[] placement {get;set;} public double reference_volume_mm3 {get;set;}
    }

    public static partial class Generator {
        public static void ValidateAuxiliary(AuxiliarySpec[] specs,HashSet<string> names=null) {
            if(names==null)names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var s in specs??new AuxiliarySpec[0]) {
                if(s==null)throw new ArgumentException("辅助零件参数不能为空。");RegisterName(names,s.name);
                foreach(var property in typeof(AuxiliarySpec).GetProperties())if(property.PropertyType==typeof(double)) {double v=(double)property.GetValue(s,null);if(Double.IsNaN(v)||Double.IsInfinity(v))throw new ArgumentException("辅助零件数值必须有限："+s.name+" / "+property.Name);}
                if(s.placement==null||s.placement.Length!=12)throw new ArgumentException("辅助零件需要 12 项装配变换："+s.name);
                foreach(double value in s.placement)if(Double.IsNaN(value)||Double.IsInfinity(value))throw new ArgumentException("辅助零件装配变换必须有限。");
                for(int i=0;i<3;i++)for(int j=i;j<3;j++){double dot=0;for(int k=0;k<3;k++)dot+=s.placement[i*3+k]*s.placement[j*3+k];if(Math.Abs(dot-(i==j?1:0))>1e-7)throw new ArgumentException("辅助零件变换必须为正交单位坐标系。");}
                var a=s.placement;double determinant=a[0]*(a[4]*a[8]-a[5]*a[7])-a[1]*(a[3]*a[8]-a[5]*a[6])+a[2]*(a[3]*a[7]-a[4]*a[6]);if(Math.Abs(determinant-1)>1e-7)throw new ArgumentException("辅助零件变换不能镜像。");
                if(s.kind=="key") {Positive(s.length);Positive(s.width);Positive(s.height);Positive(s.shaft_diameter);Positive(s.shaft_slot_depth);if(s.length<=s.width||s.width>=s.shaft_diameter||s.start_x<0||s.start_z<0||s.height<=s.shaft_slot_depth)throw new ArgumentException("平键尺寸无效。");}
                else if(s.kind=="plate") {
                    foreach(double v in new[]{s.length,s.width,s.height,s.corner_radius,s.hole_diameter,s.hole_pitch_x,s.hole_pitch_y})Positive(v);
                    if(s.corner_radius>=Math.Min(s.length,s.width)/2||s.inner_square<0||s.inner_square>=Math.Min(s.length,s.width))throw new ArgumentException("检查盖/垫片轮廓尺寸无效。");
                    if(s.hole_pitch_x+s.hole_diameter>=s.length||s.hole_pitch_y+s.hole_diameter>=s.width)throw new ArgumentException("检查盖螺钉孔越界。");
                    double qx=s.hole_pitch_x/2-s.length/2+s.corner_radius,qy=s.hole_pitch_y/2-s.width/2+s.corner_radius;
                    double signed=Math.Sqrt(Math.Pow(Math.Max(qx,0),2)+Math.Pow(Math.Max(qy,0),2))+Math.Min(Math.Max(qx,qy),0)-s.corner_radius;
                    if(signed+s.hole_diameter/2>1e-8)throw new ArgumentException("螺钉孔超出检查盖圆角轮廓。");
                    if(s.inner_square>0){double dx=Math.Max((s.hole_pitch_x-s.inner_square)/2,0),dy=Math.Max((s.hole_pitch_y-s.inner_square)/2,0);if(Math.Sqrt(dx*dx+dy*dy)<s.hole_diameter/2)throw new ArgumentException("螺钉孔与垫片窗口重叠。");}
                } else if(s.kind=="vent") {ValidateSection(s.section);ValidateSection(s.axial_hole_profile);Positive(s.side_hole_diameter);Positive(s.side_hole_depth);Positive(s.side_hole_x);}
                else if(s.kind=="indicator") {Positive(s.inner_diameter);Positive(s.outer_diameter);Positive(s.height);Positive(s.front_thickness);Positive(s.scallop_radius);if(s.outer_diameter<=s.inner_diameter||s.front_thickness>=s.height||s.scallop_radius<=(s.outer_diameter-s.inner_diameter)/2||s.scallop_radius>=s.outer_diameter/2)throw new ArgumentException("油标尺寸无效。");}
                else if(s.kind=="oil_cover") {
                    foreach(double v in new[]{s.outer_arc_radius,s.inner_arc_radius,s.outer_arc_height,s.inner_arc_height,s.right_vertical_x,s.upper_step_length,s.lower_return_length,s.drop_1,s.drop_2,s.drop_3,s.hex_diameter,s.hex_offset})Positive(v);
                    if(s.inner_arc_radius>=s.outer_arc_radius||s.outer_arc_height>=s.outer_arc_radius||s.inner_arc_height>=s.inner_arc_radius)throw new ArgumentException("油标盖圆弧尺寸无效。");
                    if(s.right_vertical_x<=Math.Max(s.upper_step_length,s.lower_return_length)||Math.Abs(s.outer_arc_height-s.drop_1-s.drop_2-s.drop_3-s.inner_arc_height)>1e-7)throw new ArgumentException("油标盖折线尺寸不连续。");
                } else throw new ArgumentException("未知辅助零件类型："+s.kind);
            }
        }
        static Sketch AuxBeginPlane(ModelDoc2 doc,int planeNumber) {
            doc.ClearSelection2(true);Feature f=(Feature)doc.FirstFeature();int count=0;
            while(f!=null){if(f.GetTypeName2()=="RefPlane"&&++count==planeNumber)break;f=(Feature)f.GetNextFeature();}
            if(f==null||!f.Select2(false,0))throw new Exception("辅助零件基准面选择失败。");
            doc.SketchManager.InsertSketch(true);doc.SketchManager.AddToDB=true;return doc.SketchManager.ActiveSketch;
        }
        static Feature AuxCloseSketch(ModelDoc2 doc,string name) {
            var feature=(Feature)doc.SketchManager.ActiveSketch;doc.SketchManager.AddToDB=false;doc.SketchManager.InsertSketch(true);ModelPresentation.NameCreated(feature,name);
            doc.ClearSelection2(true);feature.Select2(false,0);return feature;
        }
        static Feature AuxExtrude(ModelDoc2 doc,double depth,double start,string name) {
            AuxCloseSketch(doc,name+"_Profile");
            var f=doc.FeatureManager.FeatureExtrusion3(true,false,false,0,0,M(depth),0,false,false,false,false,0,0,false,false,false,false,true,true,true,3,M(Math.Abs(start)),start<0);
            if(f==null)throw new Exception("辅助零件拉伸失败："+name);ModelPresentation.NameCreated(f,name);return f;
        }
        static Feature AuxCut(ModelDoc2 doc,double depth,double start,bool reverse,string name) {
            AuxCloseSketch(doc,name+"_Profile");
            var f=doc.FeatureManager.FeatureCut3(true,false,reverse,0,0,M(depth),0,false,false,false,false,0,0,false,false,false,false,false,true,true,true,true,false,3,M(Math.Abs(start)),start<0);
            if(f==null)throw new Exception("辅助零件切除失败："+name);ModelPresentation.NameCreated(f,name);return f;
        }
        static void AuxLine(SketchManager sm,double ax,double ay,double bx,double by){sm.CreateLine(M(ax),M(ay),0,M(bx),M(by),0);}
        static void AuxArc(SketchManager sm,double cx,double cy,double ax,double ay,double bx,double by,short direction){sm.CreateArc(M(cx),M(cy),0,M(ax),M(ay),0,M(bx),M(by),0,direction);}
        static void AuxRoundedRectangle(SketchManager sm,double length,double width,double r) {
            double x=length/2,y=width/2;
            AuxLine(sm,x-r,y,-x+r,y);AuxArc(sm,-x+r,y-r,-x+r,y,-x,y-r,1);
            AuxLine(sm,-x,y-r,-x,-y+r);AuxArc(sm,-x+r,-y+r,-x,-y+r,-x+r,-y,1);
            AuxLine(sm,-x+r,-y,x-r,-y);AuxArc(sm,x-r,-y+r,x-r,-y,x,-y+r,1);
            AuxLine(sm,x,-y+r,x,y-r);AuxArc(sm,x-r,y-r,x,y-r,x-r,y,1);
        }
        static void BuildAuxKey(ModelDoc2 doc,AuxiliarySpec s) {
            AuxBeginPlane(doc,1);var sm=doc.SketchManager;double r=s.width/2,a=s.start_x+r,b=s.start_x+s.length-r;
            AuxLine(sm,a,r,b,r);AuxArc(sm,b,0,b,r,b,-r,-1);AuxLine(sm,b,-r,a,-r);AuxArc(sm,a,0,a,-r,a,r,-1);
            AuxExtrude(doc,s.height,s.start_z,"ParallelKey");
        }
        static void BuildAuxPlate(ModelDoc2 doc,AuxiliarySpec s) {
            AuxBeginPlane(doc,1);AuxRoundedRectangle(doc.SketchManager,s.length,s.width,s.corner_radius);AuxExtrude(doc,s.height,0,"Plate");
            AuxBeginPlane(doc,1);var sm=doc.SketchManager;
            foreach(double x in new[]{-s.hole_pitch_x/2,s.hole_pitch_x/2})foreach(double y in new[]{-s.hole_pitch_y/2,s.hole_pitch_y/2})sm.CreateCircleByRadius(M(x),M(y),0,M(s.hole_diameter/2));
            if(s.inner_square>0){double r=s.inner_square/2;AuxLine(sm,-r,-r,r,-r);AuxLine(sm,r,-r,r,r);AuxLine(sm,r,r,-r,r);AuxLine(sm,-r,r,-r,-r);}
            AuxCut(doc,s.height+2,s.height+1,false,"ScrewHoles"+(s.inner_square>0?"_AndOpening":""));
        }
        static void BuildAuxVent(ModelDoc2 doc,AuxiliarySpec s) {
            RevolvePolygon(doc,s.section,"VentBody");RevolvePolygon(doc,s.axial_hole_profile,"AxialBoreAndCone",true);
            AuxBeginPlane(doc,1);doc.SketchManager.CreateCircleByRadius(M(s.side_hole_x),0,0,M(s.side_hole_diameter/2));
            AuxCut(doc,s.side_hole_depth,-s.side_hole_depth,true,"RadialBreatherHole");
        }
        static double[] AuxRightUv(Sketch sketch,double y,double z) {
            var a=(double[])sketch.ModelToSketchTransform.ArrayData;
            return new[]{M(y)*a[3]+M(z)*a[6]+a[9],M(y)*a[4]+M(z)*a[7]+a[10]};
        }
        static void BuildAuxIndicator(ModelDoc2 doc,AuxiliarySpec s) {
            double inner=s.inner_diameter/2,outer=s.outer_diameter/2;
            Revolve(doc,new[]{new[]{0.0,0.0},new[]{0.0,inner},new[]{s.front_thickness,inner},new[]{s.front_thickness,0.0}},"FrontPlate");
            var sketch=AuxBeginPlane(doc,3);var sm=doc.SketchManager;var transform=(double[])sketch.ModelToSketchTransform.ArrayData;double normalX=transform[2];
            if(Math.Abs(normalX)<.99)throw new Exception("油标端面必须垂直于本地 X。");
            foreach(var center in new[]{new[]{outer,0.0},new[]{0.0,outer},new[]{-outer,0.0},new[]{0.0,-outer}}){var uv=AuxRightUv(sketch,center[0],center[1]);sm.CreateCircleByRadius(uv[0],uv[1],0,M(s.scallop_radius));}
            AuxCut(doc,s.front_thickness+.2,normalX*(s.front_thickness+.1),normalX<0,"FourScallops");
            Revolve(doc,new[]{new[]{0.0,inner},new[]{0.0,outer},new[]{s.height,outer},new[]{s.height,inner}},"OuterRing",true);
        }
        static void BuildAuxOilCover(ModelDoc2 doc,AuxiliarySpec s) {
            AuxBeginPlane(doc,1);var sm=doc.SketchManager;
            double center=s.outer_arc_radius,outerX=center-Math.Sqrt(s.outer_arc_radius*s.outer_arc_radius-s.outer_arc_height*s.outer_arc_height);
            double innerAxis=center-s.inner_arc_radius,innerX=center-Math.Sqrt(s.inner_arc_radius*s.inner_arc_radius-s.inner_arc_height*s.inner_arc_height);
            double top=s.outer_arc_height,mid=top-s.drop_1,lower=mid-s.drop_2,inner=lower-s.drop_3;
            if(Math.Abs(inner-s.inner_arc_height)>1e-7)throw new ArgumentException("油标盖内圆弧高度与折线不连续。");
            AuxArc(sm,center,0,0,0,outerX,top,-1);
            double upperX=s.right_vertical_x-s.upper_step_length,returnX=s.right_vertical_x-s.lower_return_length;
            foreach(var line in new[]{new[]{outerX,top,upperX,top},new[]{upperX,top,upperX,mid},new[]{upperX,mid,s.right_vertical_x,mid},new[]{s.right_vertical_x,mid,s.right_vertical_x,lower},new[]{s.right_vertical_x,lower,returnX,lower},new[]{returnX,lower,returnX,inner},new[]{returnX,inner,innerX,inner}})AuxLine(sm,line[0],line[1],line[2],line[3]);
            AuxArc(sm,center,0,innerX,inner,innerAxis,0,1);AuxLine(sm,innerAxis,0,0,0);
            var axis=sm.CreateCenterLine(-.01,0,0,.1,0,0);var feature=AuxCloseSketch(doc,"OilCover_Profile");
            var selection=((SelectionMgr)doc.SelectionManager).CreateSelectData();selection.Mark=16;axis.Select4(true,selection);
            var revolve=doc.FeatureManager.FeatureRevolve2(true,true,false,false,false,false,0,0,2*Math.PI,0,false,false,0,0,0,0,0,true,true,true);
            if(revolve==null)throw new Exception("油标盖圆弧旋转失败。");ModelPresentation.NameCreated(revolve,"SphericalHead_Revolve");
            var right=AuxBeginPlane(doc,3);var normal=(double[])right.ModelToSketchTransform.ArrayData;double normalX=normal[2];
            if(Math.Abs(normalX)<.99)throw new Exception("六角切除面必须垂直于 X。");
            sm.CreateCircleByRadius(0,0,0,M(s.outer_arc_radius+10));
            var points=new List<double[]>();for(int i=0;i<6;i++){double a=2*Math.PI*i/6;points.Add(AuxRightUv(right,s.hex_diameter/2*Math.Cos(a),s.hex_diameter/2*Math.Sin(a)));}
            for(int i=0;i<6;i++){var a=points[i];var b=points[(i+1)%6];sm.CreateLine(a[0],a[1],0,b[0],b[1],0);}
            double cutX=innerAxis+s.hex_offset;AuxCut(doc,cutX+1,normalX*cutX,normalX<0,"HexagonalHeadTrim");
        }
        public static List<string> MakeAuxiliary(SldWorks app,AuxiliarySpec[] specs,string output,List<object> metrics) {
            ValidateAuxiliary(specs);var files=new List<string>();
            foreach(var s in specs??new AuxiliarySpec[0]) {
                var doc=NewDocument(app,1,s.name);
                switch(s.kind){case "key":BuildAuxKey(doc,s);break;case "plate":BuildAuxPlate(doc,s);break;case "vent":BuildAuxVent(doc,s);break;case "indicator":BuildAuxIndicator(doc,s);break;case "oil_cover":BuildAuxOilCover(doc,s);break;}
                if(s.kind=="vent")AddCosmeticThread(doc,new[]{1.0,0.0,0.0},new[]{1.0,0,0},5,
                    8.5,9,"M10×1.5","External_M10x1p5");
                if(s.kind=="oil_cover")AddCosmeticThread(doc,new[]{18.0,0.0,0.0},new[]{1.0,0,0},10,
                    18.5,10,"M20×1.5","External_M20x1p5");
                string path=Path.Combine(output,s.name+".SLDPRT");files.Add(SaveWithMetrics(app,doc,path,metrics));
            }
            return files;
        }
    }
}
