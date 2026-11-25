using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;

namespace ReducerSW {
    // The delivered GB Toolbox does not contain the drawing's exact configurations.
    // These editable native parts therefore carry the nominal GB dimensions.
    // Do not switch a shared Toolbox part to Default or an approximate configuration.
    public static partial class Generator {
        static readonly HashSet<string> HardwarePartNames=new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "gb_hex_bolt_m8x50", "gb_hex_bolt_m8x25", "gb_hex_nut_m8",
            "gb_spring_washer_8", "gb_pan_screw_m3x12",
            "gb_taper_pin_4x20", "gb_oil_plug_m8x1", "gb_flat_washer_8",
            "gb_o_ring_19x1p8",
            "gb_jack_bolt_m5x10" // Exclude the obsolete part when reassembling an older output directory.
        };

        static bool HSource(HouseFeatureSpec feature,string prefix) {
            return feature.name.StartsWith(prefix,StringComparison.Ordinal)||
                (feature.source_features??new string[0]).Any(s=>s.StartsWith(prefix,StringComparison.Ordinal));
        }
        static List<double[]> HCenters(HouseFeatureSpec[] features,string prefix,double offsetZ=0) {
            var result=new List<double[]>();
            foreach(var feature in features.Where(f=>HSource(f,prefix)))
                foreach(var circle in feature.curves.Where(c=>c.kind=="circle"))
                    result.Add(new[]{feature.origin[0]+feature.u[0]*circle.center[0]+feature.v[0]*circle.center[1],
                        feature.origin[1]+feature.u[1]*circle.center[0]+feature.v[1]*circle.center[1],
                        feature.origin[2]+feature.u[2]*circle.center[0]+feature.v[2]*circle.center[1]+offsetZ,
                        circle.radius});
            return result.OrderBy(c=>c[0]).ThenBy(c=>c[1]).ToList();
        }
        static double[] HTransformPoint(double[] matrix,double x,double y,double z) {
            return new[]{matrix[0]*x+matrix[3]*y+matrix[6]*z+matrix[9],
                matrix[1]*x+matrix[4]*y+matrix[7]*z+matrix[10],
                matrix[2]*x+matrix[5]*y+matrix[8]*z+matrix[11]};
        }
        static double[] HNormal(double[] matrix) {return new[]{matrix[6],matrix[7],matrix[8]};}
        const double OilRingSectionRadius=.9;
        static double[] HOilLevelSeat(Parameters p) {
            var centers=HCenters(p.housing.lower,"Left_Side_Boss");
            if(centers.Count!=1)throw new ArgumentException("油位盖需要一个箱体左侧安装凸台。");
            var center=centers[0];
            var boss=p.housing.lower.Single(f=>HSource(f,"Left_Side_Boss"));
            return new[]{center[0]+boss.depth,center[1],center[2]};
        }
        static double HOilLevelCoverX(AuxiliarySpec cover,double seatX) {
            return seatX-2*OilRingSectionRadius-(cover.right_vertical_x-cover.upper_step_length);
        }
        public static void ValidateHardwareInterfaces(Parameters p) {
            if(p.schema_version!=4||p.housing==null||p.assembly_layout==null||p.auxiliary==null)
                throw new ArgumentException("一级标准件需要完整的 v4 箱体、附件和总装参数。");
            var lower=HCenters(p.housing.lower,"Flange_Fastener_Hole_");
            var upper=HCenters(p.housing.upper,"Flange_Fastener_Hole_",p.housing.cover_offset_z);
            if(lower.Count!=6||upper.Count!=6)throw new ArgumentException("一级法兰必须有六组 M8 螺栓孔。");
            for(int i=0;i<6;i++) {
                InterfaceEqual(lower[i][0],upper[i][0],"上下法兰螺栓孔 X 错位");
                InterfaceEqual(lower[i][1],upper[i][1],"上下法兰螺栓孔 Y 错位");
                InterfaceEqual(lower[i][3],4.5,"箱体螺栓通孔应为 φ9");
                InterfaceEqual(upper[i][3],4.5,"箱盖螺栓通孔应为 φ9");
            }
            foreach(var groove in p.housing.lower.Where(f=>f.name.StartsWith("Side_Oil_Groove_",StringComparison.Ordinal))) {
                if(groove.curves.Length%4!=0)throw new ArgumentException("回油槽轮廓必须由闭合矩形组成："+groove.name);
                for(int start=0;start<groove.curves.Length;start+=4) {
                    var loop=new HouseFeatureSpec{curves=groove.curves.Skip(start).Take(4).ToArray()};
                    var box=HRectangleBounds(loop);
                    if(box==null)throw new ArgumentException("回油槽轮廓不是矩形："+groove.name);
                    foreach(var hole in lower) {
                        double dx=Math.Max(Math.Max(box[0]-hole[0],0),hole[0]-box[2]);
                        double dy=Math.Max(Math.Max(box[1]-hole[1],0),hole[1]-box[3]);
                        if(Math.Sqrt(dx*dx+dy*dy)-hole[3]<.5-1e-6)
                            throw new ArgumentException("法兰孔与回油槽净距不足 0.5 mm："+groove.name);
                    }
                }
            }
            var cover=p.auxiliary.FirstOrDefault(a=>a.name=="inspection_cover");
            if(cover==null||cover.hole_pitch_x!=28||cover.hole_pitch_y!=28||cover.hole_diameter!=3.2)
                throw new ArgumentException("一级检查盖需四个 φ3.2 螺钉孔，孔距 28×28。");
            var screws=HCenters(p.housing.upper,"Inspection_Screw_Hole_",p.housing.cover_offset_z);
            if(screws.Count!=4)throw new ArgumentException("箱盖缺少四个检查盖螺钉孔。");
            var expected=new List<double[]>();
            foreach(double x in new[]{-14.0,14.0})foreach(double y in new[]{-14.0,14.0})
                expected.Add(HTransformPoint(cover.placement,x,y,0));
            var normal=HNormal(cover.placement);
            foreach(var target in expected)if(!screws.Any(actual=>
                HNorm(HCross(new[]{actual[0]-target[0],actual[1]-target[1],actual[2]-target[2]},normal))<1e-6&&
                Math.Abs(actual[3]-1.6)<1e-6))
                    throw new ArgumentException("检查盖螺钉孔与箱盖孔错位。");
            var oilPort=HCenters(p.housing.lower,"Right_M8_Major_Diameter");
            if(oilPort.Count!=1||Math.Abs(oilPort[0][3]-4)>1e-6)
                throw new ArgumentException("箱体右侧缺少 M8×1 螺塞孔。");
            if(!p.auxiliary.Any(a=>a.name=="oil_level_cover"))
                throw new ArgumentException("19×1.8 密封圈需要左侧油标盖定位。");
            HOilLevelSeat(p);
            var pins=HCenters(p.housing.lower,"Taper_Dowel_");
            if(pins.Count!=0)throw new ArgumentException("圆锥销孔配方不应被误当作圆柱孔。");
            foreach(var name in HardwarePartNames)
                if((p.auxiliary??new AuxiliarySpec[0]).Any(a=>a.name==name))throw new ArgumentException("标准件文件名与附件重复："+name);
        }

        static void HHexSketch(ModelDoc2 doc,double acrossFlats,double bore) {
            var sketch=AuxBeginPlane(doc,3);var sm=doc.SketchManager;
            double r=acrossFlats/Math.Sqrt(3);
            var points=new List<double[]>();
            for(int i=0;i<6;i++) {
                double angle=Math.PI*i/3+Math.PI/6;
                points.Add(AuxRightUv(sketch,r*Math.Cos(angle),r*Math.Sin(angle)));
            }
            for(int i=0;i<6;i++) {
                var a=points[i];var b=points[(i+1)%6];sm.CreateLine(a[0],a[1],0,b[0],b[1],0);
            }
            if(bore>0)sm.CreateCircleByRadius(0,0,0,M(bore));
        }
        static void HHexExtrude(ModelDoc2 doc,double acrossFlats,double bore,double x0,double length,string feature) {
            HHexSketch(doc,acrossFlats,bore);
            var sketch=doc.SketchManager.ActiveSketch;
            double normalX=((double[])sketch.ModelToSketchTransform.ArrayData)[2];
            // The Right-plane normal can point either way in an installed template.
            // Let the extrusion span exactly [x0,x0+length] in model coordinates.
            double start=normalX>0?x0:-(x0+length);
            AuxExtrude(doc,length,start,feature);
        }
        static string HSave(SldWorks app,ModelDoc2 doc,string output,string name,List<object> metrics) {
            return SaveWithMetrics(app,doc,Path.Combine(output,name+".SLDPRT"),metrics);
        }
        static string HMakeBolt(SldWorks app,string output,List<object> metrics,string name,
            double shankRadius,double shankLength,double headFlats,double headHeight) {
            var doc=NewDocument(app,1,name);
            RevolvePolygon(doc,new[]{new[]{0.0,0.0},new[]{0.0,shankRadius},
                new[]{shankLength-.5,shankRadius},new[]{shankLength,shankRadius-.5},
                new[]{shankLength,0.0}},"Nominal_Shank");
            HHexExtrude(doc,headFlats,0,-headHeight,headHeight,"Hex_Head");
            string note=name=="gb_oil_plug_m8x1"?"M8×1":"M8×1.25";
            double major=2*shankRadius;
            double minor=major-(major==8?(note=="M8×1"?1.1:1.25):.8);
            AddCosmeticThread(doc,new[]{shankLength-.5,0.0,0.0},new[]{1.0,0,0},shankRadius,
                minor,Math.Min(shankLength-.5,major==8?22:shankLength-.5),note,"External_"+note.Replace('×','x'));
            return HSave(app,doc,output,name,metrics);
        }
        static string HMakeNut(SldWorks app,string output,List<object> metrics) {
            var doc=NewDocument(app,1,"gb_hex_nut_m8");
            HHexExtrude(doc,13,4,0,6.8,"Hex_Nut_M8");
            RecordNominalThread(doc,"螺母","M8×1.25");
            return HSave(app,doc,output,"gb_hex_nut_m8",metrics);
        }
        static string HMakeWasher(SldWorks app,string output,List<object> metrics) {
            var doc=NewDocument(app,1,"gb_spring_washer_8");
            RevolvePolygon(doc,new[]{new[]{0.0,4.1},new[]{0.0,7.4},
                new[]{2.0,7.4},new[]{2.0,4.1}},"Spring_Washer_8");
            return HSave(app,doc,output,"gb_spring_washer_8",metrics);
        }
        static string HMakePanScrew(SldWorks app,string output,List<object> metrics) {
            var doc=NewDocument(app,1,"gb_pan_screw_m3x12");
            RevolvePolygon(doc,new[]{new[]{0.0,0.0},new[]{0.0,2.45},
                new[]{.6,2.8},new[]{2.0,2.8},new[]{2.4,2.45},
                new[]{2.4,1.5},new[]{13.9,1.5},new[]{14.4,1.0},
                new[]{14.4,0.0}},"Pan_Head_And_M3_Shank");
            AddCosmeticThread(doc,new[]{13.9,0.0,0.0},new[]{1.0,0,0},1.5,2.5,11.5,
                "M3×0.5","External_M3x0p5");
            return HSave(app,doc,output,"gb_pan_screw_m3x12",metrics);
        }
        static string HMakePin(SldWorks app,string output,List<object> metrics) {
            var doc=NewDocument(app,1,"gb_taper_pin_4x20");
            RevolvePolygon(doc,new[]{new[]{0.0,0.0},new[]{0.0,2.0},
                new[]{20.0,2.2},new[]{20.0,0.0}},"Taper_Pin_4x20");
            return HSave(app,doc,output,"gb_taper_pin_4x20",metrics);
        }
        static string HMakeOring(SldWorks app,string output,List<object> metrics,AuxiliarySpec oilCover) {
            var doc=NewDocument(app,1,"gb_o_ring_19x1p8");BeginSketch(doc);var sm=doc.SketchManager;
            // 19 x 1.8 is the free size. In the assembly it stretches to the
            // cover stem. Keep the section diameter for the simplified model.
            double stemRadius=oilCover.outer_arc_height-oilCover.drop_1;
            var circle=sm.CreateCircleByRadius(M(OilRingSectionRadius),M(stemRadius+OilRingSectionRadius),0,M(OilRingSectionRadius));
            var axis=sm.CreateCenterLine(-.01,0,0,.1,0,0);sm.AddToDB=false;
            foreach(var entity in new[]{circle,axis}) {
                doc.ClearSelection2(true);entity.Select4(false,null);doc.SketchAddConstraints("sgFIXED");
            }
            var sketch=(Feature)sm.ActiveSketch;sm.InsertSketch(true);
            doc.ClearSelection2(true);sketch.Select2(false,0);
            var select=((SelectionMgr)doc.SelectionManager).CreateSelectData();select.Mark=16;axis.Select4(true,select);
            var feature=doc.FeatureManager.FeatureRevolve2(true,true,false,false,false,false,
                0,0,Math.PI*2,0,false,false,0,0,0,0,0,true,false,true);
            if(feature==null)throw new Exception("19×1.8 密封圈旋转特征创建失败。");
            ModelPresentation.NameCreated(feature,"O_Ring_19x1p8");ModelPresentation.NameCreated(sketch,"O_Ring_Section");
            return HSave(app,doc,output,"gb_o_ring_19x1p8",metrics);
        }
        static List<string> MakeHardware(SldWorks app,Parameters p,string output,List<object> metrics) {
            ValidateHardwareInterfaces(p);
            return new List<string> {
                HMakeBolt(app,output,metrics,"gb_hex_bolt_m8x50",4,50,13,5.3),
                HMakeBolt(app,output,metrics,"gb_hex_bolt_m8x25",4,25,13,5.3),
                HMakeNut(app,output,metrics), HMakeWasher(app,output,metrics),
                HMakePanScrew(app,output,metrics),
                HMakePin(app,output,metrics),
                HMakeBolt(app,output,metrics,"gb_oil_plug_m8x1",4,10,13,5.3),
                HSave(app,HMakeFlatWasher(app),output,"gb_flat_washer_8",metrics),
                HMakeOring(app,output,metrics,p.auxiliary.First(a=>a.name=="oil_level_cover"))
            };
        }
        static ModelDoc2 HMakeFlatWasher(SldWorks app) {
            var doc=NewDocument(app,1,"gb_flat_washer_8");
            RevolvePolygon(doc,new[]{new[]{0.0,4.2},new[]{0.0,8.0},
                new[]{1.6,8.0},new[]{1.6,4.2}},"Flat_Washer_8");
            return doc;
        }

        static AssemblyInstance HInstance(string name,string file,double[] origin,double[] axis,double[] yAxis) {
            var zAxis=HCross(axis,yAxis);
            var matrix=new[]{axis[0],axis[1],axis[2],yAxis[0],yAxis[1],yAxis[2],
                zAxis[0],zAxis[1],zAxis[2],origin[0],origin[1],origin[2]};
            return new AssemblyInstance{name=name,file=file+".SLDPRT",x=origin[0],y=origin[1],
                z=origin[2],axis_y_direction=1,placement=matrix};
        }
        static List<AssemblyInstance> PlanHardware(Parameters p) {
            ValidateHardwareInterfaces(p);
            var list=new List<AssemblyInstance>();double axis=p.housing.cover_offset_z;
            var down=new[]{0.0,0.0,-1.0};var up=new[]{0.0,0.0,1.0};var y=new[]{0.0,1.0,0.0};
            var holes=HCenters(p.housing.lower,"Flange_Fastener_Hole_");
            for(int i=0;i<holes.Count;i++) {
                var hole=holes[i];bool longBolt=Math.Abs(hole[1])>1e-8;
                string suffix=(i+1).ToString();double underside=axis-(longBolt?18:6);
                list.Add(HInstance("flange_bolt_"+suffix,longBolt?"gb_hex_bolt_m8x50":"gb_hex_bolt_m8x25",
                    new[]{hole[0],hole[1],axis+(longBolt?17:6)},down,y));
                // At the two end holes the lower flange extends 0.05 mm
                // beneath the nominal shoulder. Seat on the actual face.
                double washerSeat=underside-(longBolt?0:0.05);
                list.Add(HInstance("flange_spring_washer_"+suffix,"gb_spring_washer_8",
                    new[]{hole[0],hole[1],washerSeat},down,y));
                list.Add(HInstance("flange_nut_"+suffix,"gb_hex_nut_m8",
                    new[]{hole[0],hole[1],washerSeat-2},down,y));
            }
            var cover=p.auxiliary.First(a=>a.name=="inspection_cover");
            var normal=HNormal(cover.placement);var inward=normal.Select(v=>-v).ToArray();
            int index=0;
            foreach(double x in new[]{-14.0,14.0})foreach(double yy in new[]{-14.0,14.0}) {
                var origin=HTransformPoint(cover.placement,x,yy,cover.height+2.4);
                list.Add(HInstance("inspection_screw_"+(++index),"gb_pan_screw_m3x12",origin,inward,y));
            }
            // The lower bore starts 13.75 mm beneath the split face. A 20 mm
            // pin ends 0.25 mm above the 6 mm cover flange at these holes.
            var dowels=new[]{new[]{-p.housing.expected_lower_bounds[3]+9.0,-29.5},
                new[]{p.housing.expected_lower_bounds[3]-10.0,35.5}};
            for(int i=0;i<2;i++)list.Add(HInstance("taper_pin_"+(i+1),"gb_taper_pin_4x20",
                new[]{dowels[i][0],dowels[i][1],axis-13.75},up,y));
            var port=HCenters(p.housing.lower,"Right_M8_Major_Diameter").Single();
            var inwardX=new[]{-1.0,0.0,0.0};var portSeat=new[]{port[0]+1.6,port[1],port[2]};
            list.Add(HInstance("oil_drain_plug","gb_oil_plug_m8x1",portSeat,inwardX,y));
            list.Add(HInstance("oil_drain_flat_washer","gb_flat_washer_8",portSeat,inwardX,y));
            var seat=HOilLevelSeat(p);
            list.Add(HInstance("oil_level_o_ring","gb_o_ring_19x1p8",
                new[]{seat[0]-2*OilRingSectionRadius,seat[1],seat[2]},new[]{1.0,0.0,0.0},y));
            return list;
        }

        static Entity HCylinderAt(SldWorks app,Component2 component,double radius,double[] worldAxis,double[] linePoint) {
            var math=(MathUtility)app.GetMathUtility();var transform=(MathTransform)component.Transform2;
            foreach(var body in ComponentBodies(component))
                foreach(object raw in body.GetFaces() as object[]??new object[0]) {
                    var face=raw as Face2;if(face==null)continue;
                    var surface=(Surface)face.GetSurface();if(surface==null||!surface.IsCylinder())continue;
                    var cylinder=(double[])surface.CylinderParams;
                    if(Math.Abs(cylinder[6]-M(radius))>M(.06))continue;
                    var origin=(double[])((MathPoint)((MathPoint)math.CreatePoint(new[]{cylinder[0],cylinder[1],cylinder[2]})).MultiplyTransform(transform)).ArrayData;
                    var axis=(double[])((MathVector)((MathVector)math.CreateVector(new[]{cylinder[3],cylinder[4],cylinder[5]})).MultiplyTransform(transform)).ArrayData;
                    if(Math.Abs(Math.Abs(HDot(axis,worldAxis))-1)>1e-3)continue;
                    var between=new[]{origin[0]-M(linePoint[0]),origin[1]-M(linePoint[1]),origin[2]-M(linePoint[2])};
                    if(HNorm(HCross(between,worldAxis))>M(.1))continue;
                    return (Entity)face;
                }
            return null;
        }
        static Feature HConcentric(SldWorks app,ModelDoc2 doc,AssemblyDoc assembly,
            Component2 driver,Component2 driven,double driverRadius,double drivenRadius,
            double[] worldAxis,double[] linePoint,string label) {
            return TAddMate(app,doc,assembly,driven,()=>{
                var a=HCylinderAt(app,driver,driverRadius,worldAxis,linePoint);
                var b=HCylinderAt(app,driven,drivenRadius,worldAxis,linePoint);
                if(a==null||b==null)throw new Exception("标准件未找到指定孔位的圆柱面："+label);
                var first=driver.GetCorrespondingEntity(a) as Entity;
                var second=driven.GetCorrespondingEntity(b) as Entity;
                if(first==null||second==null)return false;
                var selection=((SelectionMgr)doc.SelectionManager).CreateSelectData();selection.Mark=1;
                return first.Select4(false,selection)&&second.Select4(true,selection);
            },1,0,label);
        }
        static void HMatePair(SldWorks app,ModelDoc2 doc,AssemblyDoc assembly,
            Component2 axisDriver,Component2 axialDriver,Component2 driven,
            double driverRadius,double drivenRadius,double[] axis,double[] point,
            int axialPlane,double axialDistance,string label,List<TwoStageMateRecord> records) {
            ORecord(records,axisDriver,driven,"concentric",HConcentric(app,doc,assembly,axisDriver,driven,
                driverRadius,drivenRadius,axis,point,label+"_axis"));
            ORecord(records,axialDriver,driven,axialDistance<1e-6?"coincident":"distance",
                TPlaneMate(app,doc,assembly,axialDriver,axialPlane,driven,2,axialDistance,label+"_seat"));
        }
        static void MateHardware(SldWorks app,ModelDoc2 doc,AssemblyDoc assembly,Parameters p,
            Dictionary<string,Component2> parts,List<TwoStageMateRecord> records) {
            var housing=parts["housing"];var cover=parts["cover"];
            var holes=HCenters(p.housing.lower,"Flange_Fastener_Hole_");
            double axisZ=p.housing.cover_offset_z;
            for(int i=0;i<6;i++) {
                string suffix=(i+1).ToString();var hole=holes[i];var point=new[]{hole[0],hole[1],axisZ};
                var bolt=parts["flange_bolt_"+suffix];
                HMatePair(app,doc,assembly,cover,cover,bolt,4.5,4,new[]{0.0,0.0,1.0},point,
                    0,Math.Abs(hole[1])>1e-8?17:6,"flange_bolt_"+suffix,records);
                HMatePair(app,doc,assembly,bolt,housing,parts["flange_spring_washer_"+suffix],
                    4,4.1,new[]{0.0,0.0,1.0},point,0,axisZ-(Math.Abs(hole[1])>1e-8?18:6.05),
                    "flange_washer_"+suffix,records);
                HMatePair(app,doc,assembly,bolt,housing,parts["flange_nut_"+suffix],
                    4,4,new[]{0.0,0.0,1.0},point,0,axisZ-(Math.Abs(hole[1])>1e-8?18:6.05)-2,
                    "flange_nut_"+suffix,records);
            }
            var plate=p.auxiliary.First(a=>a.name=="inspection_cover");
            var platePart=parts["inspection_cover"];var normal=HNormal(plate.placement);
            int index=0;
            foreach(double x in new[]{-14.0,14.0})foreach(double y in new[]{-14.0,14.0}) {
                var point=HTransformPoint(plate.placement,x,y,0);
                HMatePair(app,doc,assembly,platePart,platePart,parts["inspection_screw_"+(++index)],
                    1.6,1.5,normal,point,0,plate.height+2.4,"inspection_screw_"+index,records);
            }
            for(int i=1;i<=2;i++)ODatum3(app,doc,assembly,housing,parts["taper_pin_"+i],
                "taper_pin_"+i,records);
            var port=HCenters(p.housing.lower,"Right_M8_Major_Diameter").Single();
            var portPoint=new[]{port[0],port[1],port[2]};var portAxis=new[]{1.0,0.0,0.0};
            var plug=parts["oil_drain_plug"];
            HMatePair(app,doc,assembly,housing,housing,plug,4,4,portAxis,portPoint,
                2,port[0]+1.6,"oil_drain_plug",records);
            HMatePair(app,doc,assembly,plug,plug,parts["oil_drain_flat_washer"],
                4,4.2,portAxis,portPoint,2,0,"oil_drain_flat_washer",records);
            ODatum3(app,doc,assembly,parts["oil_level_cover"],parts["oil_level_o_ring"],
                "oil_level_o_ring",records);
        }
    }
}
