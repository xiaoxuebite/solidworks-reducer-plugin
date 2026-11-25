using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ReducerSW {
    public class AssemblyInstance {
        public string name {get;set;} public string file {get;set;}
        public double x {get;set;} public double y {get;set;} public double z {get;set;}
        public int axis_y_direction {get;set;}
        public double[] placement {get;set;}
    }
    public class TotalAssemblyResult {
        public List<AssemblyInstance> instances=new List<AssemblyInstance>();
        public List<object> seal_mates=new List<object>();
        public List<TwoStageMateRecord> native_mates=new List<TwoStageMateRecord>();
    }
    public static partial class Generator {
        public static List<AssemblyInstance> PlanAssembly(Parameters p,IEnumerable<string> files) {
            var result=new List<AssemblyInstance>();var dat=p.assembly_layout;
            double lowRing=0,highRing=0;
            foreach(var ring in p.accessories??new Ring[0]) {if(ring.name=="low_adjusting_ring")lowRing=ring.thickness;if(ring.name=="high_adjusting_ring")highRing=ring.thickness;}
            double lowBlankY=dat==null?0:dat.boss_depth/2-p.blank_cover.total_thickness;
            double highBlankY=dat==null?0:-dat.boss_depth/2+(p.high_blank_cover==null?7:p.high_blank_cover.total_thickness);
            var lowThrough=(p.through_covers??new ThroughCover[0]).FirstOrDefault(c=>c.name=="low_through_cover");
            var highThrough=(p.through_covers??new ThroughCover[0]).FirstOrDefault(c=>c.name=="high_through_cover");
            foreach(string path in files) {
                string name=Path.GetFileNameWithoutExtension(path);
                if(p.hardware_version==1&&HardwarePartNames.Contains(name))continue;
                var placement=new AssemblyInstance{name=name,file=Path.GetFileName(path),axis_y_direction=1};
                if(dat==null) {
                    placement.y=result.Count*100;
                    if(name.EndsWith("_oil_seal")){var cover=result.FirstOrDefault(i=>i.name==name.Replace("_oil_seal","_through_cover"));if(cover!=null)placement.y=cover.y;}
                    result.Add(placement);continue;
                }
                placement.z=dat.axis_height;
                switch(name) {
                    case "driven_shaft":placement.x=dat.low_center_x;placement.y=dat.mesh_center_y+p.shaft_segments[0].length+p.shaft_segments[1].length/2;placement.axis_y_direction=-1;break;
                    case "drive_gear_shaft":
                        if(p.gears==null)throw new ArgumentException("高速轴参数缺失。");
                        double before=0;double gearLength=0;foreach(var s in p.gears.high_sections){if(s.kind=="gear"){gearLength=s.length;break;}before+=s.length;}
                        placement.x=dat.high_center_x;placement.y=dat.mesh_center_y+before+gearLength/2;placement.axis_y_direction=-1;break;
                    case "driven_gear":placement.x=dat.low_center_x;placement.y=dat.mesh_center_y+p.gears.driven_width/2;placement.axis_y_direction=-1;break;
                    case "high_bearing":
                        if(highThrough==null)throw new ArgumentException("高速轴轴承定位需要通盖。");
                        placement.name="high_bearing_upper";placement.x=dat.high_center_x;placement.y=dat.boss_depth/2+highThrough.outer_steps[0][1]-highThrough.outer_steps.Sum(s=>s[1]);placement.axis_y_direction=-1;
                        result.Add(new AssemblyInstance{name="high_bearing_lower",file=placement.file,x=dat.high_center_x,y=highBlankY+highRing,z=dat.axis_height,axis_y_direction=1});break;
                    case "low_bearing":
                        if(lowThrough==null)throw new ArgumentException("低速轴轴承定位需要通盖。");
                        placement.name="low_bearing_upper";placement.x=dat.low_center_x;placement.y=lowBlankY-lowRing;placement.axis_y_direction=-1;
                        result.Add(new AssemblyInstance{name="low_bearing_lower",file=placement.file,x=dat.low_center_x,y=-dat.boss_depth/2-lowThrough.outer_steps[0][1]+lowThrough.outer_steps.Sum(s=>s[1]),z=dat.axis_height,axis_y_direction=1});break;
                    case "housing":case "cover":
                        placement.x=0;placement.y=0;placement.z=name=="cover"?p.housing.cover_offset_z:0;
                        placement.placement=new double[]{1,0,0,0,1,0,0,0,1,0,0,placement.z};break;
                    case "low_blank_cover":placement.x=dat.low_center_x;placement.y=lowBlankY;break;
                    case "high_blank_cover":placement.x=dat.high_center_x;placement.y=highBlankY;placement.axis_y_direction=-1;break;
                    case "low_adjusting_ring":placement.x=dat.low_center_x;placement.y=lowBlankY-lowRing;break;
                    case "high_adjusting_ring":placement.x=dat.high_center_x;placement.y=highBlankY;break;
                    case "sleeve":placement.x=dat.low_center_x;placement.y=lowBlankY-lowRing-dat.low_bearing_width;placement.axis_y_direction=-1;break;
                    case "low_through_cover":case "low_oil_seal":
                        if(lowThrough==null)throw new ArgumentException("低速轴通盖定位信息缺失。");
                        placement.x=dat.low_center_x;placement.y=-dat.boss_depth/2-lowThrough.outer_steps[0][1];break;
                    case "high_through_cover":case "high_oil_seal":
                        if(highThrough==null)throw new ArgumentException("高速轴通盖定位信息缺失。");
                        placement.x=dat.high_center_x;placement.y=dat.boss_depth/2+highThrough.outer_steps[0][1];placement.axis_y_direction=-1;break;
                    case "oil_retaining_ring":
                        if(highThrough==null)throw new ArgumentException("挡油环定位需要高速轴通盖。");
                        var part=(p.rotary_parts??new RotaryPart[0]).First(r=>r.name==name);
                        double thickness=part.section.Max(v=>v[0]);
                        placement.name="oil_retaining_ring_upper";placement.x=dat.high_center_x;
                        placement.y=dat.boss_depth/2+highThrough.outer_steps[0][1]-highThrough.outer_steps.Sum(s=>s[1])-dat.high_bearing_width-thickness;
                        result.Add(new AssemblyInstance{name="oil_retaining_ring_lower",file=placement.file,x=dat.high_center_x,y=highBlankY+highRing+dat.high_bearing_width+thickness,z=dat.axis_height,axis_y_direction=-1});break;
                    default:
                        var aux=(p.auxiliary??new AuxiliarySpec[0]).FirstOrDefault(a=>a.name==name);
                        if(aux==null)throw new ArgumentException("零件尚未配置总装定位规则："+name);
                        placement.placement=(double[])aux.placement.Clone();
                        if(p.hardware_version==1&&(name=="oil_level_cover"||name=="oil_level_indicator")) {
                            var seat=HOilLevelSeat(p);
                            var oilCover=p.auxiliary.First(a=>a.name=="oil_level_cover");
                            double offset=name=="oil_level_indicator"?oilCover.right_vertical_x-oilCover.lower_return_length:0;
                            placement.placement[9]=HOilLevelCoverX(oilCover,seat[0])+offset;
                            placement.placement[10]=seat[1];placement.placement[11]=seat[2];
                        }
                        placement.x=placement.placement[9];placement.y=placement.placement[10];placement.z=placement.placement[11];break;
                }
                result.Add(placement);
            }
            if(p.hardware_version==1)result.AddRange(PlanHardware(p));
            return result;
        }
        static void InterfaceEqual(double actual,double expected,string message) {
            if(Double.IsNaN(actual)||Double.IsInfinity(actual)||Double.IsNaN(expected)||Double.IsInfinity(expected)||Math.Abs(actual-expected)>1e-6)
                throw new ArgumentException(message+"（实际 "+actual.ToString("0.######")+"，应为 "+expected.ToString("0.######")+"）。");
        }
        // Check the saved recipe itself: changing a datum must not leave the old
        // housing bores or the separately positioned keys behind.
        static void ValidateHousingInterfaces(Parameters p) {
            if(p.housing==null)return;
            var dat=p.assembly_layout;
            if(dat==null)throw new ArgumentException("箱体配方需要总装基准，不能使用零件展示排列。");
            InterfaceEqual(p.housing.cover_offset_z,dat.axis_height,"上盖分型面高度与总装轴线高度不一致；请重新导出箱体配方");
            foreach(string label in new[]{"High","Low"}) {
                var bore=p.housing.lower.FirstOrDefault(f=>f.name==label+"_Bearing_Bore");
                if(bore==null||bore.kind!="cut"||bore.curves.Length!=1||bore.curves[0].kind!="circle")
                    throw new ArgumentException("箱体缺少可核对的 "+label+"_Bearing_Bore 圆孔配方。");
                var circle=bore.curves[0];
                double x=bore.origin[0]+bore.u[0]*circle.center[0]+bore.v[0]*circle.center[1];
                double z=bore.origin[2]+bore.u[2]*circle.center[0]+bore.v[2]*circle.center[1];
                bool high=label=="High";
                InterfaceEqual(x,high?dat.high_center_x:dat.low_center_x,"箱体轴承孔中心 X 与总装基准不一致；请重新导出箱体配方");
                InterfaceEqual(z,dat.axis_height,"箱体轴承孔高度与总装基准不一致；请重新导出箱体配方");
                InterfaceEqual(Math.Abs(bore.origin[1])*2,dat.boss_depth,"箱体轴承凸台宽度与总装基准不一致；请重新导出箱体配方");
                var normal=HCross(bore.u,bore.v);
                InterfaceEqual(Math.Abs(normal[1]),1,"箱体轴承孔轴线必须平行于总装 Y 轴");
                var bearing=(p.bearings??new BearingSpec[0]).FirstOrDefault(b=>b.name==(high?"high_bearing":"low_bearing"));
                if(bearing!=null)InterfaceEqual(circle.radius*2,bearing.outer_diameter,"箱体轴承孔径与轴承外径不一致；请重新导出箱体配方");
            }
        }
        static void ValidateKeyInterfaces(Parameters p) {
            foreach(var key in (p.auxiliary??new AuxiliarySpec[0]).Where(a=>a.kind=="key")) {
                bool high=key.shaft_link=="high";
                if(!high&&key.shaft_link!="low")throw new ArgumentException("平键缺少有效的 low/high 轴关联："+key.name);
                if(p.assembly_layout==null)throw new ArgumentException("平键定位需要总装基准："+key.name);
                Keyway slot;
                double diameter,originY;
                if(high) {
                    if(p.gears==null)throw new ArgumentException("高速轴平键缺少高速轴和键槽参数。");
                    if(key.segment_index!=0)throw new ArgumentException("高速轴平键必须关联第一轴段。");
                    slot=p.gears.high_keyway;diameter=p.gears.high_sections[0].diameter;
                    double before=0;double gearLength=0;
                    foreach(var section in p.gears.high_sections){if(section.kind=="gear"){gearLength=section.length;break;}before+=section.length;}
                    originY=p.assembly_layout.mesh_center_y+before+gearLength/2;
                } else {
                    if(key.segment_index<0||key.segment_index>=p.shaft_segments.Length)throw new ArgumentException("低速轴平键关联的轴段无效："+key.name);
                    var candidates=p.keyways.Where(k=>k.segment_index==key.segment_index).ToArray();
                    if(candidates.Length!=1)throw new ArgumentException("低速轴平键必须关联唯一键槽："+key.name);
                    slot=candidates[0];diameter=p.shaft_segments[key.segment_index].diameter;
                    originY=p.assembly_layout.mesh_center_y+p.shaft_segments[0].length+p.shaft_segments[1].length/2;
                }
                InterfaceEqual(key.length,slot.length,"平键长度与实际键槽不一致："+key.name);
                InterfaceEqual(key.width,slot.width,"平键宽度与实际键槽不一致："+key.name);
                InterfaceEqual(key.start_x,slot.start,"平键轴向起点与实际键槽不一致："+key.name);
                InterfaceEqual(slot.diameter,diameter,"键槽关联轴径与实际轴段不一致："+key.name);
                InterfaceEqual(key.shaft_diameter,diameter,"平键关联轴径已过期："+key.name);
                InterfaceEqual(key.shaft_slot_depth,slot.depth,"平键关联槽深已过期："+key.name);
                InterfaceEqual(key.start_z,diameter/2-slot.depth,"平键底面与实际键槽不一致："+key.name);
                double[] expected={0,-1,0,1,0,0,0,0,1,high?p.assembly_layout.high_center_x:p.assembly_layout.low_center_x,originY,p.assembly_layout.axis_height};
                for(int i=0;i<expected.Length;i++)InterfaceEqual(key.placement[i],expected[i],"平键装配位置与实际轴基准不一致："+key.name);
            }
        }
        public static void ValidateAssemblyInterfaces(Parameters p) {
            // Bearing checks use the actual two installed seats, not an arbitrary
            // same-diameter segment somewhere else on the shaft.
            foreach(var bearing in p.bearings??new BearingSpec[0]) {
                bool high=bearing.name=="high_bearing";
                if(p.assembly_layout==null)throw new ArgumentException("轴承定位需要总装基准："+bearing.name);
                if(high&&p.gears==null)throw new ArgumentException("高速轴轴承缺少可比对的高速轴参数。");
                double[] diameters=high?p.gears.high_sections.Select(s=>s.diameter).ToArray():p.shaft_segments.Select(s=>s.diameter).ToArray();
                double[] lengths=high?p.gears.high_sections.Select(s=>s.length).ToArray():p.shaft_segments.Select(s=>s.length).ToArray();
                if((high&&lengths.Length<3)||(!high&&lengths.Length<4))throw new ArgumentException("轴承定位缺少两处有效轴颈："+bearing.name);
                InterfaceEqual(bearing.width,high?p.assembly_layout.high_bearing_width:p.assembly_layout.low_bearing_width,"轴承宽度与总装定位基准不一致："+bearing.name);
                string shaftName=high?"drive_gear_shaft":"driven_shaft";
                var plan=PlanAssembly(p,new[]{shaftName+".SLDPRT",bearing.name+".SLDPRT"});
                double originY=plan.First(i=>i.name==shaftName).y;
                foreach(var installed in plan.Where(i=>i.file==bearing.name+".SLDPRT")) {
                    int index=high?(installed.name.EndsWith("_upper")?1:lengths.Length-1):(installed.name.EndsWith("_upper")?0:3);
                    if(high&&p.gears.high_sections[index].kind!="cylinder")throw new ArgumentException("轴承安装段必须是圆柱轴颈："+installed.name);
                    InterfaceEqual(bearing.inner_diameter,diameters[index],"轴承内径与实际安装轴颈不一致："+installed.name);
                    double start=lengths.Take(index).Sum();
                    double first=originY-installed.y,second=originY-installed.y-installed.axis_y_direction*bearing.width;
                    if(Math.Min(first,second)<start-1e-6||Math.Max(first,second)>start+lengths[index]+1e-6)
                        throw new ArgumentException("轴承越过实际安装轴颈台阶："+installed.name+"；请同步轴段长度与端盖、调整环和总装基准。");
                }
            }

            // 2) Assembly datum spacing must equal the standard centre distance of the
            //    gear pair, otherwise the teeth do not mesh. Unshifted profiles are
            //    already enforced by ValidateGearTrain, so no profile shift term here.
            if(p.gears!=null&&p.assembly_layout!=null) {
                double cosine=Math.Cos(p.gears.helix_angle*Math.PI/180);
                double standard=p.gears.module*(p.gears.pinion_teeth+p.gears.driven_teeth)/(2*cosine);
                double actual=Math.Abs(p.assembly_layout.low_center_x-p.assembly_layout.high_center_x);
                if(Math.Abs(actual-standard)>1e-4)
                    throw new ArgumentException("装配中心距 "+actual.ToString("0.###")+" mm 与齿轮副标准中心距 "
                        +standard.ToString("0.###")+" mm 不一致，齿轮无法正确啮合。请把 REDUCER_CENTER_DISTANCE 设为 "
                        +standard.ToString("0.###")+"，或同步修改模数与齿数。");
            }

            // 3) The driven gear cannot be wider than the shaft seat it is mounted on.
            if(p.gears!=null&&p.shaft_segments!=null&&p.shaft_segments.Length>=2) {
                if(p.gears.driven_width>p.shaft_segments[1].length+1e-6)
                    throw new ArgumentException("从动齿轮宽 "+p.gears.driven_width.ToString("0.###")
                        +" mm 超过低速轴第二段轴向空间 "+p.shaft_segments[1].length.ToString("0.###")+" mm，齿轮无法完全落在轴颈上。");
            }

            ValidateHousingInterfaces(p);
            ValidateKeyInterfaces(p);
        }
        static void MateSeal(ModelDoc2 doc,AssemblyDoc assembly,Component2 cover,Component2 seal) {
            doc.ClearSelection2(true);seal.Select4(false,null,false);assembly.UnfixComponent();
            var planes=new List<string>();var source=(ModelDoc2)cover.GetModelDoc2();var feature=(Feature)source.FirstFeature();
            while(feature!=null&&planes.Count<3){if(feature.GetTypeName2()=="RefPlane")planes.Add(feature.Name);feature=(Feature)feature.GetNextFeature();}
            if(planes.Count!=3)throw new Exception("组件缺少三基准面。");
            foreach(string plane in planes) {
                doc.ClearSelection2(true);var first=cover.FeatureByName(plane);var second=seal.FeatureByName(plane);
                if(first==null||second==null||!first.Select2(false,1)||!second.Select2(true,1))throw new Exception("总装配合基准面选择失败："+plane);
                int error;var mate=assembly.AddMate5(0,0,false,0,0,0,0,0,0,0,0,false,false,0,out error);
                if(mate==null||error!=(int)swAddMateError_e.swAddMateError_NoError)throw new Exception("总装重合配合失败："+plane+"，错误码 "+error);
            }
            doc.ClearSelection2(true);doc.EditRebuild3();
            if(seal.IsFixed()||seal.GetConstrainedStatus()!=(int)swConstrainedStatus_e.swFullyConstrained)throw new Exception("油封必须由配合完全约束。");
        }
        // 组件的 GetBody() 对多实体零件（轴承内外圈+滚珠+保持架）只返回一个体，且可能取不到边。
        // 统一从组件引用的零件文档里枚举全部实体，再逐个取边。
        static List<Body2> ComponentBodies(Component2 component) {
            var bodies=new List<Body2>();
            if(component==null)return bodies;
            try {
                var part=component.GetModelDoc2() as PartDoc;
                if(part!=null) {
                    var all=(object[])part.GetBodies2(0,false);
                    if(all!=null)foreach(object item in all){var body=item as Body2;if(body!=null)bodies.Add(body);}
                }
            } catch(Exception) { }
            if(bodies.Count==0) {
                var single=(Body2)component.GetBody();
                if(single!=null)bodies.Add(single);
            }
            return bodies;
        }
        // 在组件里找一个半径与轴向都匹配的圆边，用于同轴心配合。
        // 坐标取自零件的模型空间，必须先用组件变换映射到总装空间再比对。
        static Entity FindCircularEdge(SldWorks app,Component2 component,double radius,double[] axis) {
            var math=(MathUtility)app.GetMathUtility();
            var transform=(MathTransform)component.Transform2;
            foreach(var body in ComponentBodies(component)) {
                var edges=(object[])body.GetEdges();
                if(edges==null)continue;
                foreach(object item in edges) {
                    var edge=item as Edge;if(edge==null)continue;
                    var curve=(Curve)edge.GetCurve();if(curve==null||!curve.IsCircle())continue;
                    var cp=(double[])curve.CircleParams;
                    // CircleParams 的半径单位是米（与 M() 同一套内部单位），而调用方传进来的是毫米。
                    if(Math.Abs(cp[6]-M(radius))>M(0.05))continue;
                    var vector=(MathVector)math.CreateVector(new double[]{cp[3],cp[4],cp[5]});
                    var world=(double[])((MathVector)vector.MultiplyTransform(transform)).ArrayData;
                    double dot=world[0]*axis[0]+world[1]*axis[1]+world[2]*axis[2];
                    if(Math.Abs(Math.Abs(dot)-1)>1e-2)continue;
                    return (Entity)edge;
                }
            }
            return null;
        }
        // 失败时把组件里实际的圆边半径打出来，避免再靠猜。
        static string DescribeCircles(SldWorks app,Component2 component) {
            var bodies=ComponentBodies(component);
            if(bodies.Count==0)return "body=null(lightweight?)";
            var math=(MathUtility)app.GetMathUtility();
            var transform=(MathTransform)component.Transform2;
            var radii=new List<string>();int edges=0,circles=0;
            foreach(var body in bodies) {
                var list=(object[])body.GetEdges();
                if(list==null)continue;
                edges+=list.Length;
                foreach(object item in list) {
                    var edge=item as Edge;if(edge==null)continue;
                    var curve=(Curve)edge.GetCurve();if(curve==null||!curve.IsCircle())continue;
                    circles++;
                    var cp=(double[])curve.CircleParams;
                    var vector=(MathVector)math.CreateVector(new double[]{cp[3],cp[4],cp[5]});
                    var world=(double[])((MathVector)vector.MultiplyTransform(transform)).ArrayData;
                    radii.Add((cp[6]*1000).ToString("0.##")+(Math.Abs(Math.Abs(world[1])-1)<1e-2?"Y":"?"));
                    if(radii.Count>=12)break;
                }
                if(radii.Count>=12)break;
            }
            return "bodies="+bodies.Count+" edges="+edges+" circles="+circles+" radii(mm)="+String.Join("/",radii.ToArray());
        }
        // 圆柱面比圆边更适合做同轴心配合：面是 Mate 的常规选择对象。
        // CylinderParams 顺序为 [origin(3), axis(3), radius]，与 CircleParams 一致。
        static Entity FindCylindricalFace(SldWorks app,Component2 component,double radius,double[] axis) {
            var math=(MathUtility)app.GetMathUtility();
            var transform=(MathTransform)component.Transform2;
            foreach(var body in ComponentBodies(component)) {
                var faces=(object[])body.GetFaces();
                if(faces==null)continue;
                foreach(object item in faces) {
                    var face=item as Face2;if(face==null)continue;
                    var surface=(Surface)face.GetSurface();if(surface==null||!surface.IsCylinder())continue;
                    var cp=(double[])surface.CylinderParams;
                    if(Math.Abs(cp[6]-M(radius))>M(0.05))continue;
                    var vector=(MathVector)math.CreateVector(new double[]{cp[3],cp[4],cp[5]});
                    var world=(double[])((MathVector)vector.MultiplyTransform(transform)).ArrayData;
                    double dot=world[0]*axis[0]+world[1]*axis[1]+world[2]*axis[2];
                    if(Math.Abs(Math.Abs(dot)-1)>1e-2)continue;
                    return (Entity)face;
                }
            }
            return null;
        }
        static string MateErrorName(int code) {
            switch(code) {
                case 0:return "ErrorUnknown";
                case 1:return "NoError";
                case 2:return "IncorrectMateType";
                case 3:return "IncorrectAlignment";
                case 4:return "IncorrectSelections";
                case 5:return "OverDefinedAssembly";
                case 6:return "IncorrectGearRatios";
                default:return "code "+code;
            }
        }
        static IEnumerable<Feature> MateDescendants(Feature parent) {
            var child=(Feature)parent.GetFirstSubFeature();
            while(child!=null) {
                yield return child;
                foreach(var nested in MateDescendants(child))yield return nested;
                child=(Feature)child.GetNextSubFeature();
            }
        }
        internal static List<Feature> AssemblyMateFeatures(ModelDoc2 doc) {
            var result=new List<Feature>();var feature=(Feature)doc.FirstFeature();
            while(feature!=null) {
                if(feature.GetTypeName2()=="MateGroup") {
                    result.Add(feature);
                    result.AddRange(MateDescendants(feature));
                }
                feature=(Feature)feature.GetNextFeature();
            }
            return result;
        }
        static bool MateFeatureHealthy(Feature feature) {
            bool warning;return feature.GetErrorCode2(out warning)==0;
        }
        static void ValidateAssemblyMates(ModelDoc2 doc) {
            foreach(var feature in AssemblyMateFeatures(doc)) {
                bool warning;int code=feature.GetErrorCode2(out warning);
                if(code!=0)throw new Exception("总装配合重建错误："+feature.Name+"，类型 "+feature.GetTypeName2()+"，错误码 "+code+"，warning="+warning);
            }
        }
        static Feature CreatedMateFeature(SldWorks app,ModelDoc2 doc,Mate2 mate,HashSet<string> previous) {
            var created=AssemblyMateFeatures(doc).Where(f=>f.GetTypeName2().StartsWith("Mate")&&f.GetTypeName2()!="MateGroup"&&!previous.Contains(f.Name)).ToArray();
            if(mate!=null)foreach(var feature in created) {
                var specific=feature.GetSpecificFeature2();
                if(specific!=null&&app.IsSame(specific,mate)==(int)swObjectEquality.swObjectSame)return feature;
            }
            // Failed AddMate5 calls may still leave a feature while returning null.
            // The synchronous call owns only the new, uniquely identified mate.
            if(created.Length==1&&created[0].GetTypeName2()=="MateConcentric")return created[0];
            if(created.Length!=0||mate!=null)throw new Exception("无法唯一识别本次新建配合，停止以免删除其他配合。");
            return null;
        }
        static void DeleteCreatedMate(ModelDoc2 doc,Feature feature) {
            if(feature==null)return;
            string name=feature.Name;
            doc.ClearSelection2(true);
            if(!feature.Select2(false,0)||!doc.Extension.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Children|(int)swDeleteSelectionOptions_e.swDelete_Absorbed))
                throw new Exception("无法删除本次未接受的配合："+name);
            doc.ClearSelection2(true);doc.EditRebuild3();
            if(AssemblyMateFeatures(doc).Any(f=>f.Name==name))throw new Exception("本次未接受的配合删除后仍然存在："+name);
            Console.WriteLine("  removed rejected mate: "+name);
        }
        static Feature TryConcentric(SldWorks app,ModelDoc2 doc,AssemblyDoc assembly,Component2 driver,Component2 driven,double radius,double[] axis,string label,bool flip) {
            var math=(MathUtility)app.GetMathUtility();
            double[] original=(double[])driven.Transform2.ArrayData;
            for(int attempt=0;attempt<2;attempt++) {
                if(attempt>0)driven.Transform2=(MathTransform)math.CreateTransform(original);
                Entity rawFirst,rawSecond;
                if(attempt==0) {
                    rawFirst=FindCylindricalFace(app,driver,radius,axis);
                    rawSecond=FindCylindricalFace(app,driven,radius,axis);
                } else {
                    rawFirst=FindCircularEdge(app,driver,radius,axis);
                    rawSecond=FindCircularEdge(app,driven,radius,axis);
                }
                if(rawFirst==null||rawSecond==null)continue;
                // 零件文档里取到的几何在装配环境下必须映射成组件实体，否则 AddMate5 会报
                // swAddMateError_IncorrectSelections(4)。
                var first=driver.GetCorrespondingEntity(rawFirst) as Entity;
                var second=driven.GetCorrespondingEntity(rawSecond) as Entity;
                if(first==null||second==null) {
                    Console.WriteLine("  mate attempt "+(attempt==0?"face":"edge")+" no assembly entity: "+label);
                    continue;
                }
                doc.ClearSelection2(true);
                var select=((SelectionMgr)doc.SelectionManager).CreateSelectData();
                select.Mark=1;
                first.Select4(false,select);
                second.Select4(true,select);
                int error;
                var previous=new HashSet<string>(AssemblyMateFeatures(doc).Select(f=>f.Name),StringComparer.Ordinal);
                // 1 = swMateCONCENTRIC；最终布尔参数是"反转对齐"，用于避免配合把零件翻转 180°。
                var mate=assembly.AddMate5(1,0,flip,0,0,0,0,0,0,0,0,false,false,0,out error);
                var feature=CreatedMateFeature(app,doc,mate,previous);
                if(mate!=null&&feature!=null&&error==(int)swAddMateError_e.swAddMateError_NoError) {
                    doc.ClearSelection2(true);doc.EditRebuild3();
                    if(MateFeatureHealthy(feature)) {
                        Console.WriteLine("  mate ok ("+(attempt==0?"face":"edge")+(flip?", flipped":"")+"): "+label);
                        return feature;
                    }
                    Console.WriteLine("  mate rebuild rejected: "+label);
                }
                DeleteCreatedMate(doc,feature);
                driven.Transform2=(MathTransform)math.CreateTransform(original);
                Console.WriteLine("  mate attempt "+(attempt==0?"face":"edge")+(flip?", flipped":"")+" failed ("+MateErrorName(error)+"): "+label);
            }
            Console.WriteLine("  mate skipped: "+label
                +" | driver("+driver.Name2+"): "+DescribeCircles(app,driver)
                +" | driven("+driven.Name2+"): "+DescribeCircles(app,driven));
            return null;
        }
        // 组件几何中心在装配坐标系下的位置。用于判断"翻转是否真的挪动了实体"：
        // 回转对称件绕轴翻转 180° 后中心不变，材质占位也不变。
        static double[] ComponentCenter(SldWorks app,Component2 component,double[] transformData) {
            double[] box=null;
            foreach(var body in ComponentBodies(component)) {
                var bb=(double[])body.GetBodyBox();
                if(bb==null)continue;
                if(box==null){box=new double[6];for(int i=0;i<6;i++)box[i]=bb[i];continue;}
                for(int i=0;i<3;i++){if(bb[i]<box[i])box[i]=bb[i];if(bb[i+3]>box[i+3])box[i+3]=bb[i+3];}
            }
            if(box==null||transformData==null)return null;
            var math=(MathUtility)app.GetMathUtility();
            var transform=(MathTransform)math.CreateTransform(transformData);
            var point=(MathPoint)math.CreatePoint(new double[]{
                (box[0]+box[3])/2,(box[1]+box[4])/2,(box[2]+box[5])/2});
            return (double[])((MathPoint)point.MultiplyTransform(transform)).ArrayData;
        }
        // 轴系同轴心配合：把每根轴上所有回转零件约束到轴线上，轴本身作为固定基准。
        // 逐个尝试，失败就把该零件退回固定定位，保证总装一定能够生成，同时把结果打进日志。
        static void AddShaftMates(SldWorks app,ModelDoc2 doc,AssemblyDoc assembly,Parameters p,Dictionary<string,Component2> components,HashSet<string> relaxed) {
            var pending=new List<object[]>();
            double lowBore=0,highBore=0;
            foreach(var bearing in p.bearings??new BearingSpec[0]) {
                if(bearing.name=="low_bearing")lowBore=bearing.inner_diameter/2;
                if(bearing.name=="high_bearing")highBore=bearing.inner_diameter/2;
            }
            if(lowBore>0) {
                pending.Add(new object[]{"driven_shaft","low_bearing_upper",lowBore,true});
                pending.Add(new object[]{"driven_shaft","low_bearing_lower",lowBore,true});
            }
            if(highBore>0) {
                pending.Add(new object[]{"drive_gear_shaft","high_bearing_upper",highBore,true});
                pending.Add(new object[]{"drive_gear_shaft","high_bearing_lower",highBore,true});
            }
            // 只做"过盈/同径"的轴系配合。通盖和调整环是间隙配合（例如轴颈 r=11 而盖孔 r=12），
            // 半径本来就不相等，不能用圆边半径匹配来配对，它们继续沿用固定定位。
            foreach(var ring in p.accessories??new Ring[0]) {
                if(ring.name!="sleeve")continue;
                pending.Add(new object[]{"driven_shaft",ring.name,ring.inner_diameter/2,true});
            }
            // 从动齿轮有键槽，不是回转对称件，翻转会改变键槽方位，必须用严格判据。
            if(p.gears!=null)pending.Add(new object[]{"driven_shaft","driven_gear",p.gears.driven_bore/2,false});
            foreach(var part in p.rotary_parts??new RotaryPart[0]) {
                double bore=double.MaxValue;
                foreach(var point in part.section)if(point[1]<bore)bore=point[1];
                // PlanAssembly 会把挡油环拆成上下两个实例，组件名带 _upper/_lower。
                // 挡油环截面轴向不对称，翻转会把它镜像，同样用严格判据。
                pending.Add(new object[]{"drive_gear_shaft",part.name+"_upper",bore,false});
                pending.Add(new object[]{"drive_gear_shaft",part.name+"_lower",bore,false});
            }
            double[] axis=new double[]{0,1,0};
            int light=assembly.GetLightWeightComponentCount();
            if(light>0) {
                Console.WriteLine("Resolving "+light+" lightweight component(s) before mating.");
                assembly.ResolveAllLightweight();
                doc.EditRebuild3();
            }
            int applied=0,leftFixed=0;
            foreach(var item in pending) {
                string shaftName=(string)item[0],partName=(string)item[1];double radius=(double)item[2];
                Component2 shaft,part;
                if(!components.TryGetValue(shaftName,out shaft)||!components.TryGetValue(partName,out part)) {
                    Console.WriteLine("  mate skipped (component missing): "+partName);
                    leftFixed++;continue;
                }
                doc.ClearSelection2(true);part.Select4(false,null,false);assembly.UnfixComponent();
                var math=(MathUtility)app.GetMathUtility();
                double[] before=(double[])part.Transform2.ArrayData;
                bool symmetric=(bool)item[3];
                bool done=false;
                for(int attempt=0;attempt<2&&!done;attempt++) {
                    // 先在原地配合；若配合把零件翻转/拉走了，撤销并把变换写回计划值，再用
                    // "反转对齐"重试一次。两次都不行才退回固定定位。
                    if(attempt>0)part.Transform2=(MathTransform)math.CreateTransform(before);
                    var createdMate=TryConcentric(app,doc,assembly,shaft,part,radius,axis,partName,attempt>0);
                    if(createdMate==null)continue;
                    double[] after=(double[])part.Transform2.ArrayData;
                    bool moved=false;
                    for(int n=0;n<12&&n<before.Length&&n<after.Length;n++)
                        if(Math.Abs(before[n]-after[n])>1e-4)moved=true;
                    if(!moved){applied++;done=true;continue;}
                    // 回转对称件（轴承、轴套）被翻转后几何等价，但零件本地原点在端面上，
                    // 翻转会造成纯轴向平移。同轴心配合只约束轴线，轴向是自由自由度，
                    // 因此可以把轴向平移补回去，配合依然成立。
                    if(symmetric) {
                        var beforeCentre=ComponentCenter(app,part,before);
                        var afterCentre=ComponentCenter(app,part,after);
                        if(beforeCentre!=null&&afterCentre!=null) {
                            double dx=Math.Abs(beforeCentre[0]-afterCentre[0]),dz=Math.Abs(beforeCentre[2]-afterCentre[2]);
                            if(dx<M(0.02)&&dz<M(0.02)) {
                                double[] compensated=(double[])after.Clone();
                                compensated[10]+=beforeCentre[1]-afterCentre[1];
                                part.Transform2=(MathTransform)math.CreateTransform(compensated);
                                doc.EditRebuild3();
                                var settled=ComponentCenter(app,part,(double[])part.Transform2.ArrayData);
                                if(settled!=null&&Enumerable.Range(0,3).All(n=>Math.Abs(settled[n]-beforeCentre[n])<M(0.02))&&MateFeatureHealthy(createdMate)) {
                                    applied++;done=true;relaxed.Add(partName);
                                    Console.WriteLine("  mate accepted with axial compensation: "+partName);
                                    continue;
                                }
                                Console.WriteLine("  axial compensation rejected by mate solver: "+partName);
                            }
                            Console.WriteLine("  mate shifted component laterally ("+(dx*1000).ToString("0.###")+"/"+(dz*1000).ToString("0.###")+" mm): "+partName);
                        }
                    }
                    Console.WriteLine("  mate moved component (flip="+(attempt>0)+"): "+partName);
                    DeleteCreatedMate(doc,createdMate);
                    part.Transform2=(MathTransform)math.CreateTransform(before);
                }
                if(done)continue;
                doc.ClearSelection2(true);part.Select4(false,null,false);assembly.FixComponent();
                leftFixed++;
            }
            Console.WriteLine("Shaft concentric mates: "+applied+" applied, "+leftFixed+" left fixed.");
        }
        // AddComponent5 needs a loaded source part. Open only the file being
        // inserted, return focus to the assembly, then close our part window.
        // The inserted component remains referenced by the assembly for mates.
        static Component2 InsertSavedPart(SldWorks app,ModelDoc2 assemblyDoc,AssemblyDoc assembly,
            string path,double x,double y,double z,string label) {
            var part=(ModelDoc2)app.GetOpenDocumentByName(path);
            bool openedHere=part==null;
            if(openedHere) {
                int error=0,warning=0;
                // Loading a visible part builds an extra graphics SceneGraph.
                // SOLIDWORKS recommends hiding the source while OpenDoc6 runs.
                bool visible=app.GetDocumentVisible(1);
                app.DocumentVisible(false,1);
                try {part=(ModelDoc2)app.OpenDoc6(path,1,1,"",ref error,ref warning);}
                finally {app.DocumentVisible(visible,1);}
                if(part==null||error!=0) {
                    if(part!=null)app.CloseDoc(part.GetTitle());
                    throw new Exception("总装零件打开失败："+path+" error="+error);
                }
            }
            try {
                int activationError=0;
                if(app.ActivateDoc3(assemblyDoc.GetTitle(),false,0,ref activationError)==null||activationError!=0)
                    throw new Exception("无法切回总装："+assemblyDoc.GetTitle()+" error="+activationError);
                var component=assembly.AddComponent5(path,0,"",false,"",M(x),M(y),M(z));
                if(component==null)throw new Exception("总装组件插入失败："+label);
                return component;
            } finally {
                if(openedHere&&part!=null)app.CloseDoc(part.GetTitle());
            }
        }
        public static TotalAssemblyResult CreateTotalAssembly(SldWorks app,Parameters p,IEnumerable<string> partPaths,string output) {
            return CreateTotalAssembly(app,p,partPaths,output,true);
        }
        public static string OneStageLayoutScope(Parameters p,bool addMates) {
            if(!addMates)return TwoStageFixedLayout;
            return p.assembly_layout==null?"legacy catalog layout":"housing grounded; all other components constrained by native mates";
        }
        public static TotalAssemblyResult CreateTotalAssembly(SldWorks app,Parameters p,IEnumerable<string> partPaths,string output,bool addMates) {
            var files=partPaths.ToArray();var result=new TotalAssemblyResult{instances=PlanAssembly(p,files)};
            bool nativeMating=p.schema_version>=4&&p.housing!=null&&p.assembly_layout!=null;
            var doc=NewDocument(app,2,"reducer_assembly");ShowCosmeticThreads(doc);var assembly=(AssemblyDoc)doc;var math=(MathUtility)app.GetMathUtility();var components=new Dictionary<string,Component2>();
            try {
            foreach(var item in result.instances) {
                GenerationControl.Checkpoint("总装定位："+ModelPresentation.ChineseName(item.file)+"（"+(components.Count+1)+"/"+result.instances.Count+"）");
                var component=InsertSavedPart(app,doc,assembly,Path.Combine(output,item.file),item.x,item.y,item.z,item.name);
                int d=item.axis_y_direction;
                double[] transform={0,d,0,-d,0,0,0,0,1,M(item.x),M(item.y),M(item.z),1,0,0,0};
                if(item.placement!=null)for(int n=0;n<9;n++)transform[n]=item.placement[n];
                if(!addMates&&component.IsFixed()) {
                    doc.ClearSelection2(true);
                    if(!component.Select4(false,null,false))throw new Exception("无法选择总装组件："+item.name);
                    assembly.UnfixComponent();
                    if(component.IsFixed())throw new Exception("无法解除总装组件的初始固定："+item.name);
                }
                component.Transform2=(MathTransform)math.CreateTransform(transform);
                component.Name2=item.name;
                if(!addMates||!nativeMating||item.name=="housing") {
                    doc.ClearSelection2(true);
                    if(!component.Select4(false,null,false))throw new Exception("无法选择总装组件："+item.name);
                    assembly.FixComponent();
                    if(!component.IsFixed())throw new Exception("无法固定总装组件："+item.name);
                }
                components.Add(item.name,component);
            }
            var relaxed=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if(addMates) {
                GenerationControl.Checkpoint("正在添加总装配合");
                if(nativeMating) {
                    BatchNativeMates=true;
                    try {result.native_mates=MateOneStage(app,doc,assembly,p,components,result.seal_mates);}
                    finally {BatchNativeMates=false;}
                } else {
                    AddShaftMates(app,doc,assembly,p,components,relaxed);
                    foreach(var through in p.through_covers??new ThroughCover[0]) {
                        string sealName=through.name.Replace("_through_cover","_oil_seal");
                        MateSeal(doc,assembly,components[through.name],components[sealName]);
                        result.seal_mates.Add(new {cover=through.name,seal=sealName,coincident_plane_mates=3,seal_fixed=false,seal_constraint_status=components[sealName].GetConstrainedStatus()});
                    }
                }
            }
            GenerationControl.Checkpoint("正在重建总装");
            doc.ClearSelection2(true);
            if(!doc.EditRebuild3()&&!addMates)throw new Exception("一级总装重建失败。");
            ValidateAssemblyMates(doc);
            foreach(var pair in components) {
                var desired=result.instances.First(i=>i.name==pair.Key);var transform=(double[])pair.Value.Transform2.ArrayData;
                // 被配合翻转过的回转对称件是按"材质占位不变"接受的，不能再比对旋转矩阵，
                // 改比几何中心：只要实体还在计划位置上就算合格。
                if(relaxed.Contains(pair.Key)) {
                    double[] planned={0,0,0,0,1,0,0,0,1,M(desired.x),M(desired.y),M(desired.z),1,0,0,0};
                    if(desired.placement!=null) {
                        for(int n=0;n<9;n++)planned[n]=desired.placement[n];
                        planned[9]=desired.placement[9];planned[10]=desired.placement[10];planned[11]=desired.placement[11];
                    } else {
                        double d=desired.axis_y_direction;
                        planned=new double[]{0,d,0,-d,0,0,0,0,1,M(desired.x),M(desired.y),M(desired.z),1,0,0,0};
                    }
                    var plannedCentre=ComponentCenter(app,pair.Value,planned);
                    var actualCentre=ComponentCenter(app,pair.Value,transform);
                    if(plannedCentre==null||actualCentre==null)continue;
                    double shift=Math.Abs(plannedCentre[0]-actualCentre[0])+Math.Abs(plannedCentre[1]-actualCentre[1])
                        +Math.Abs(plannedCentre[2]-actualCentre[2]);
                    if(shift>M(0.02))throw new Exception("配合后组件偏离总装定位："+pair.Key+" 偏移 "+(shift*1000).ToString("0.###")+" mm");
                    continue;
                }
                double[] rotation=desired.placement??new double[]{0,desired.axis_y_direction,0,-desired.axis_y_direction,0,0,0,0,1};
                bool wrong=false;for(int n=0;n<9;n++)if(Math.Abs(transform[n]-rotation[n])>1e-5)wrong=true;
                if(Math.Abs(transform[9]-M(desired.x))+Math.Abs(transform[10]-M(desired.y))+Math.Abs(transform[11]-M(desired.z))>1e-5 || wrong)throw new Exception("组件偏离总装定位："+pair.Key);
            }
            int namingPreference=(int)swUserPreferenceToggle_e.swExtRefUpdateCompNames;
            bool previousNaming=app.GetUserPreferenceToggle(namingPreference);
            app.SetUserPreferenceToggle(namingPreference,false);
            try {
                ModelPresentation.NameAssemblyComponents(app,doc,result.instances,components,result.native_mates);
                Save(doc,Path.Combine(output,"reducer_assembly.SLDASM"));
            } catch {app.SetUserPreferenceToggle(namingPreference,previousNaming);throw;}
            ValidateAssemblyMates(doc);
            return result;
            } finally {
                app.CloseDoc(doc.GetTitle());
            }
        }
    }
}
