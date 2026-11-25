using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ReducerSW {
    // Schema v5 is deliberately separate from the one-stage CadQuery migration.
    // All coordinates below are millimetres. Native part axes are X; the three
    // installed shaft axes are parallel to assembly Y at the common axis height.
    public class TwoStageSpec {
        public int hardware_version {get;set;}
        public TwoStageShaft[] shafts {get;set;}
        public TwoStagePair high_pair {get;set;}
        public TwoStagePair low_pair {get;set;}
        public TwoStageBearingSet[] bearings {get;set;}
        public TwoStageHousing housing {get;set;}
        public double end_cover_thickness {get;set;}
        public string[] reference_files {get;set;}
        public string[] limitations {get;set;}
    }
    public class TwoStageShaft {
        public string name {get;set;} // input, intermediate, output
        public GearShaftSection[] sections {get;set;}
        public Keyway[] keyways {get;set;}
        public double center_x {get;set;}
        public double origin_y {get;set;}
        public int axis_direction {get;set;} // +1 or -1 along assembly Y
    }
    public class TwoStagePair {
        public double module {get;set;}
        public double pressure_angle {get;set;}
        public double helix_angle {get;set;} // signed pinion hand; mating wheel uses the opposite sign
        public int pinion_teeth {get;set;}
        public int wheel_teeth {get;set;}
        public double pinion_width {get;set;}
        public double wheel_width {get;set;}
        public double pinion_start_y {get;set;}
        public double wheel_start_y {get;set;}
        public bool integral_pinion {get;set;}
        public double pinion_bore {get;set;}
        public double pinion_key_width {get;set;}
        public double pinion_key_depth {get;set;}
        public double wheel_bore {get;set;}
        public double wheel_key_width {get;set;}
        public double wheel_key_depth {get;set;}
        public double pinion_phase_deg {get;set;}
        public double wheel_phase_deg {get;set;}
    }
    public class TwoStageBearingSet {
        public string name {get;set;} // input, intermediate, output
        public string catalog_name {get;set;}
        public double inner_diameter {get;set;}
        public double outer_diameter {get;set;}
        public double width {get;set;}
        public double front_y {get;set;}
        public double rear_y {get;set;}
    }
    public class TwoStageHousing {
        public double x_min {get;set;}
        public double x_max {get;set;}
        public double y_min {get;set;}
        public double y_max {get;set;}
        public double axis_height {get;set;}
        public double upper_height {get;set;}
        public double floor_thickness {get;set;}
        public double roof_thickness {get;set;}
        public double side_wall {get;set;}
        public double front_wall {get;set;}
        public double rear_wall {get;set;}
    }

    public static partial class Generator {
        static readonly string[] TwoStageShaftNames={"input","intermediate","output"};
        static TwoStageShaft TShaft(TwoStageSpec spec,string name){return spec.shafts.First(s=>s.name==name);}
        static TwoStageBearingSet TBearing(TwoStageSpec spec,string name){return spec.bearings.First(b=>b.name==name);}
        static double TTotal(TwoStageShaft shaft){return shaft.sections.Sum(s=>s.length);}
        static double TPitchDiameter(TwoStagePair pair,int teeth){return pair.module*teeth/Math.Cos(pair.helix_angle*Math.PI/180);}
        static double TRootDiameter(TwoStagePair pair,int teeth){return TPitchDiameter(pair,teeth)-2.5*pair.module;}
        static double TTipDiameter(TwoStagePair pair,int teeth){return TPitchDiameter(pair,teeth)+2*pair.module;}
        static double TStart(TwoStageShaft shaft,int index){return shaft.sections.Take(index).Sum(s=>s.length);}
        static double TY(TwoStageShaft shaft,double station){return shaft.origin_y+shaft.axis_direction*station;}
        static void TFinite(double value,string label){if(Double.IsNaN(value)||Double.IsInfinity(value))throw new ArgumentException(label+" 必须为有限数。");}
        static void TNear(double a,double b,string label){if(Math.Abs(a-b)>1e-5)throw new ArgumentException(label+"："+a.ToString("0.###")+" != "+b.ToString("0.###")+" mm。");}
        static void TPositive(double value,string label){TFinite(value,label);if(value<=0)throw new ArgumentException(label+" 必须为正数。");}
        static void TCheckGearSeat(TwoStageShaft shaft,double startY,double width,double bore,string label) {
            double endY=startY+width,covered=0;
            for(int i=0;i<shaft.sections.Length;i++) {
                var section=shaft.sections[i];if(section.kind!="cylinder"||Math.Abs(section.diameter-bore)>1e-5)continue;
                double a=Math.Min(TY(shaft,TStart(shaft,i)),TY(shaft,TStart(shaft,i+1)));
                double b=Math.Max(TY(shaft,TStart(shaft,i)),TY(shaft,TStart(shaft,i+1)));
                covered+=Math.Max(0,Math.Min(endY,b)-Math.Max(startY,a));
            }
            if(covered<width-2.1)throw new ArgumentException(label+" 与对应轴段重合不足；轴孔须配合安装轴颈。");
        }
        static void TCheckBearingSeat(TwoStageShaft shaft,TwoStageBearingSet bearing,double startY,string label) {
            double endY=startY+bearing.width;
            bool fits=false;
            for(int i=0;i<shaft.sections.Length;i++) {
                var section=shaft.sections[i];if(section.kind!="cylinder"||Math.Abs(section.diameter-bearing.inner_diameter)>1e-5)continue;
                double a=Math.Min(TY(shaft,TStart(shaft,i)),TY(shaft,TStart(shaft,i+1)));
                double b=Math.Max(TY(shaft,TStart(shaft,i)),TY(shaft,TStart(shaft,i+1)));
                if(startY>=a-1e-5&&endY<=b+1e-5)fits=true;
            }
            if(!fits)throw new ArgumentException(label+" 超出同径轴承座。");
        }
        public static void ValidateTwoStage(TwoStageSpec spec) {
            if(spec==null||spec.shafts==null||spec.shafts.Length!=3||spec.high_pair==null||spec.low_pair==null||spec.bearings==null||spec.bearings.Length!=3||spec.housing==null)
                throw new ArgumentException("二级参数需要三根轴、两对齿轮、三组轴承和箱体参数。");
            if(spec.hardware_version!=0&&spec.hardware_version!=2)throw new ArgumentException("二级标准件版本只支持 0 或 2。");
            if(!spec.shafts.Select(s=>s==null?null:s.name).OrderBy(x=>x).SequenceEqual(TwoStageShaftNames.OrderBy(x=>x)))throw new ArgumentException("轴名称必须为 input、intermediate、output。");
            if(!spec.bearings.Select(b=>b==null?null:b.name).OrderBy(x=>x).SequenceEqual(TwoStageShaftNames.OrderBy(x=>x)))throw new ArgumentException("轴承组名称必须与三根轴对应。");
            foreach(var shaft in spec.shafts) {
                if(shaft.sections==null||shaft.sections.Length<3||shaft.keyways==null||shaft.axis_direction!=1&&shaft.axis_direction!=-1)
                    throw new ArgumentException("轴段、键槽或方向无效："+shaft.name);
                TFinite(shaft.center_x,shaft.name+" 中心 X");TFinite(shaft.origin_y,shaft.name+" 轴向原点");
                int gearCount=0;
                foreach(var section in shaft.sections) {
                    if(section==null||section.kind!="cylinder"&&section.kind!="gear")throw new ArgumentException("轴段类型只允许 cylinder 或 gear："+shaft.name);
                    TPositive(section.diameter,shaft.name+" 轴径");TPositive(section.length,shaft.name+" 轴段长度");
                    if(section.kind=="gear")gearCount++;
                }
                if(gearCount!=(shaft.name=="input"?1:0))throw new ArgumentException("仅输入轴含一个整体高速小齿轮段。");
                foreach(var key in shaft.keyways) {
                    if(key==null)throw new ArgumentException("键槽不能为空。");
                    TPositive(key.length,"键槽长度");TPositive(key.width,"键槽宽度");TPositive(key.depth,"键槽切深");
                    TFinite(key.start,"键槽起点");
                    if(key.start<0||key.length<=key.width||key.depth>=key.diameter/2)throw new ArgumentException("键槽比例无效："+shaft.name);
                    bool onSegment=false;
                    for(int i=0;i<shaft.sections.Length;i++)if(shaft.sections[i].kind=="cylinder"&&Math.Abs(shaft.sections[i].diameter-key.diameter)<1e-5&&key.start>=TStart(shaft,i)-1e-5&&key.start+key.length<=TStart(shaft,i+1)+1e-5)onSegment=true;
                    if(!onSegment)throw new ArgumentException("键槽未落在同径圆柱轴段："+shaft.name);
                }
            }
            foreach(var pair in new[]{spec.high_pair,spec.low_pair}) {
                TPositive(pair.module,"模数");TPositive(pair.pinion_width,"小齿轮宽度");TPositive(pair.wheel_width,"大齿轮宽度");
                TFinite(pair.pinion_start_y,"小齿轮轴向起点");TFinite(pair.wheel_start_y,"大齿轮轴向起点");
                TFinite(pair.pinion_phase_deg,"小齿轮相位");TFinite(pair.wheel_phase_deg,"大齿轮相位");
                if(pair.pinion_teeth<17||pair.wheel_teeth<17||pair.pinion_teeth>500||pair.wheel_teeth>500||pair.pressure_angle<=0||pair.pressure_angle>=45)
                    throw new ArgumentException("齿数需在 17–500，压力角需在 0–45°。");
                TFinite(pair.helix_angle,"螺旋角");
                if(Math.Abs(pair.helix_angle)>1e-8&&(Math.Abs(pair.helix_angle)<8||Math.Abs(pair.helix_angle)>35))
                    throw new ArgumentException("斜齿螺旋角绝对值需在 8°–35°；直齿请设为 0°。");
                GearToothGeometry(pair.module,pair.pinion_teeth,pair.pressure_angle,pair.helix_angle);
                GearToothGeometry(pair.module,pair.wheel_teeth,pair.pressure_angle,pair.helix_angle);
                if(Math.Min(pair.pinion_start_y+pair.pinion_width,pair.wheel_start_y+pair.wheel_width)-Math.Max(pair.pinion_start_y,pair.wheel_start_y)<Math.Min(pair.pinion_width,pair.wheel_width)-4)
                    throw new ArgumentException("齿轮副轴向重叠不足。");
                TPositive(pair.wheel_bore,"大齿轮孔径");TPositive(pair.wheel_key_width,"大齿轮键宽");TPositive(pair.wheel_key_depth,"大齿轮键槽深度");
                if(pair.wheel_bore/2+pair.wheel_key_depth>=TRootDiameter(pair,pair.wheel_teeth)/2)throw new ArgumentException("大齿轮孔/键槽穿过齿根。");
                if(!pair.integral_pinion){TPositive(pair.pinion_bore,"小齿轮孔径");TPositive(pair.pinion_key_width,"小齿轮键宽");TPositive(pair.pinion_key_depth,"小齿轮键槽深度");if(pair.pinion_bore/2+pair.pinion_key_depth>=TRootDiameter(pair,pair.pinion_teeth)/2)throw new ArgumentException("小齿轮孔/键槽穿过齿根。");}
            }
            if(!spec.high_pair.integral_pinion||spec.low_pair.integral_pinion)throw new ArgumentException("高速小齿轮应与输入轴整体建模，低速小齿轮应独立建模。");
            TwoStageShaft input=TShaft(spec,"input"),middle=TShaft(spec,"intermediate"),output=TShaft(spec,"output");
            TNear(Math.Abs(input.center_x-middle.center_x),(TPitchDiameter(spec.high_pair,spec.high_pair.pinion_teeth)+TPitchDiameter(spec.high_pair,spec.high_pair.wheel_teeth))/2,"高速级中心距");
            TNear(Math.Abs(middle.center_x-output.center_x),(TPitchDiameter(spec.low_pair,spec.low_pair.pinion_teeth)+TPitchDiameter(spec.low_pair,spec.low_pair.wheel_teeth))/2,"低速级中心距");
            if(!(input.center_x>middle.center_x&&middle.center_x>output.center_x))throw new ArgumentException("轴线 X 顺序应为输出、中间、输入。");
            int integralIndex=Array.FindIndex(input.sections,s=>s.kind=="gear");
            TNear(input.sections[integralIndex].diameter,TRootDiameter(spec.high_pair,spec.high_pair.pinion_teeth),"高速整体小齿轮齿根直径");
            TNear(input.sections[integralIndex].length,spec.high_pair.pinion_width,"高速整体小齿轮宽度");
            TNear(Math.Min(TY(input,TStart(input,integralIndex)),TY(input,TStart(input,integralIndex+1))),spec.high_pair.pinion_start_y,"高速整体小齿轮轴向位置");
            if(integralIndex==0||integralIndex==input.sections.Length-1||input.sections[integralIndex-1].diameter>input.sections[integralIndex].diameter||input.sections[integralIndex+1].diameter>input.sections[integralIndex].diameter)throw new ArgumentException("整体小齿轮两侧轴颈超过齿根或齿轮位于轴端。");
            TCheckGearSeat(middle,spec.high_pair.wheel_start_y,spec.high_pair.wheel_width,spec.high_pair.wheel_bore,"高速大齿轮");
            TCheckGearSeat(middle,spec.low_pair.pinion_start_y,spec.low_pair.pinion_width,spec.low_pair.pinion_bore,"低速小齿轮");
            TCheckGearSeat(output,spec.low_pair.wheel_start_y,spec.low_pair.wheel_width,spec.low_pair.wheel_bore,"低速大齿轮");
            foreach(var set in spec.bearings) {
                var bearing=new BearingSpec{name=set.name+"_bearing",catalog_name=set.catalog_name,inner_diameter=set.inner_diameter,outer_diameter=set.outer_diameter,width=set.width};
                ValidateBearing(bearing);TFinite(set.front_y,"前轴承位置");TFinite(set.rear_y,"后轴承位置");
                if(set.front_y+set.width>=set.rear_y)throw new ArgumentException("前后轴承位置颠倒："+set.name);
                TCheckBearingSeat(TShaft(spec,set.name),set,set.front_y,set.name+" 前轴承");
                TCheckBearingSeat(TShaft(spec,set.name),set,set.rear_y,set.name+" 后轴承");
            }
            var h=spec.housing;
            foreach(var value in new[]{h.x_min,h.x_max,h.y_min,h.y_max,h.axis_height,h.upper_height,h.floor_thickness,h.roof_thickness,h.side_wall,h.front_wall,h.rear_wall})TFinite(value,"箱体尺寸");
            if(h.x_min>=output.center_x||h.x_max<=input.center_x||h.y_min>=h.y_max||h.axis_height<=h.floor_thickness||h.upper_height<=h.roof_thickness||h.side_wall<=0||h.front_wall<=0||h.rear_wall<=0||h.x_max-h.x_min<=2*h.side_wall||h.y_max-h.y_min<=h.front_wall+h.rear_wall)throw new ArgumentException("箱体外形、壁厚或分型面尺寸无效。");
            foreach(var pair in new[]{spec.high_pair,spec.low_pair})if(pair.pinion_start_y<h.y_min+h.front_wall||pair.wheel_start_y<h.y_min+h.front_wall||pair.pinion_start_y+pair.pinion_width>h.y_max-h.rear_wall||pair.wheel_start_y+pair.wheel_width>h.y_max-h.rear_wall)throw new ArgumentException("齿轮进入箱体端壁，请调整轴向装配链。");
            foreach(var gear in new[]{new{shaft=input,diameter=TTipDiameter(spec.high_pair,spec.high_pair.pinion_teeth)},new{shaft=middle,diameter=TTipDiameter(spec.high_pair,spec.high_pair.wheel_teeth)},new{shaft=middle,diameter=TTipDiameter(spec.low_pair,spec.low_pair.pinion_teeth)},new{shaft=output,diameter=TTipDiameter(spec.low_pair,spec.low_pair.wheel_teeth)}}) {
                double radius=gear.diameter/2;
                if(gear.shaft.center_x-radius<=h.x_min+h.side_wall||gear.shaft.center_x+radius>=h.x_max-h.side_wall||radius>=h.axis_height-h.floor_thickness||radius>=h.upper_height-h.roof_thickness)throw new ArgumentException("齿顶圆超出箱体内腔："+gear.shaft.name);
            }
            foreach(var set in spec.bearings) {
                if(set.front_y<h.y_min-1e-5||set.front_y+set.width>h.y_min+h.front_wall+1e-5||set.rear_y<h.y_max-h.rear_wall-1e-5||set.rear_y+set.width>h.y_max+1e-5)throw new ArgumentException("轴承须落在箱体前后端壁："+set.name);
                if(TShaft(spec,set.name).center_x-set.outer_diameter/2<=h.x_min||TShaft(spec,set.name).center_x+set.outer_diameter/2>=h.x_max)throw new ArgumentException("轴承座超出箱体侧边。");
            }
            for(int i=0;i<spec.bearings.Length;i++)for(int j=i+1;j<spec.bearings.Length;j++)if(Math.Abs(TShaft(spec,spec.bearings[i].name).center_x-TShaft(spec,spec.bearings[j].name).center_x)<(spec.bearings[i].outer_diameter+spec.bearings[j].outer_diameter)/2+2)
                throw new ArgumentException("两组轴承座间距不足，箱体端壁会贯通。");
            TPositive(spec.end_cover_thickness,"端盖厚度");
            if(spec.end_cover_thickness<=1.6)throw new ArgumentException("端盖厚度需大于盲凹槽深度的两倍（1.6 mm）。");
            if(spec.hardware_version==2&&(h.axis_height<45||h.upper_height<45||h.roof_thickness<8||
                h.front_wall<24||h.rear_wall<24||spec.end_cover_thickness<6))
                throw new ArgumentException("二级标准件需要至少 45 mm 箱体半高、8 mm 顶壁、24 mm 端壁和 6 mm 端盖厚度。");
        }

        static string TMakeShaft(SldWorks app,TwoStageShaft shaft,TwoStagePair high,string output,List<object> metrics) {
            var doc=NewDocument(app,1,shaft.name+"_shaft");
            var profile=new List<double[]>{new[]{0.0,0.0},new[]{0.0,shaft.sections[0].diameter/2}};
            double axial=0,gearStart=0;
            foreach(var section in shaft.sections) {
                if(section.kind=="gear")gearStart=axial;
                profile.Add(new[]{axial,section.diameter/2});axial+=section.length;profile.Add(new[]{axial,section.diameter/2});
            }
            profile.Add(new[]{axial,0.0});Revolve(doc,profile.ToArray(),shaft.name+"_ShaftRevolve");
            if(shaft.name=="input") {
                var parameters=new GearTrainSpec{module=high.module,pressure_angle=high.pressure_angle,helix_angle=high.helix_angle};
                AddGearBody(app,doc,parameters,high.pinion_teeth,high.pinion_width,gearStart,high.pinion_phase_deg,true,"HighPinion_Involute");
            }
            for(int i=0;i<shaft.keyways.Length;i++)KeySlot(doc,shaft.keyways[i],i+1);
            return SaveWithMetrics(app,doc,Path.Combine(output,shaft.name+"_shaft.SLDPRT"),metrics);
        }
        static double TWheelPhase(TwoStagePair pair,int pinionAxisDirection) {
            double pitch=360.0/pair.wheel_teeth;
            // A half pitch is the default in existing parameter files. Retain explicit phase overrides.
            if(Math.Abs(pair.helix_angle)>1e-8||Math.Abs(pair.wheel_phase_deg-pitch/2)>1e-8)
                return pair.wheel_phase_deg;
            double pinionFacingAngle=pair.integral_pinion?90.0*pinionAxisDirection:0;
            double pinionPitchPosition=(pinionFacingAngle-pair.pinion_phase_deg)*pair.pinion_teeth/360;
            // An integral pinion on a reversed shaft rotates in the opposite sketch sense to its wheel.
            int sketchSense=pair.integral_pinion?pinionAxisDirection:1;
            double phaseInPitches=pair.wheel_teeth/2.0+sketchSense*pinionPitchPosition-.5;
            return (phaseInPitches-Math.Floor(phaseInPitches))*pitch;
        }
        static string TMakeGear(SldWorks app,string name,TwoStagePair pair,bool pinion,double localHelix,string output,List<object> metrics,int pinionAxisDirection=1) {
            double width=pinion?pair.pinion_width:pair.wheel_width;
            var parameters=new GearTrainSpec {module=pair.module,pressure_angle=pair.pressure_angle,helix_angle=localHelix,
                driven_width=width,driven_bore=pinion?pair.pinion_bore:pair.wheel_bore,
                driven_key_width=pinion?pair.pinion_key_width:pair.wheel_key_width,
                driven_key_depth=pinion?pair.pinion_key_depth:pair.wheel_key_depth};
            var doc=NewDocument(app,1,name);
            AddGearBody(app,doc,parameters,pinion?pair.pinion_teeth:pair.wheel_teeth,width,0,pinion?pair.pinion_phase_deg:TWheelPhase(pair,pinionAxisDirection),false,name+"_Involute");
            CutDrivenGearBore(app,doc,parameters);CutDrivenGearKeyway(app,doc,parameters);
            return SaveWithMetrics(app,doc,Path.Combine(output,name+".SLDPRT"),metrics);
        }
        static HouseCurve[] TRectangle(double left,double bottom,double right,double top) {
            var points=new[]{new[]{left,bottom},new[]{right,bottom},new[]{right,top},new[]{left,top}};
            return Enumerable.Range(0,4).Select(i=>new HouseCurve{kind="line",start=points[i],end=points[(i+1)%4]}).ToArray();
        }
        static HouseFeatureSpec TBoxFeature(string name,string kind,double z,double depth,double left,double front,double right,double rear) {
            return new HouseFeatureSpec{name=name,kind=kind,origin=new[]{0.0,0.0,z},u=new[]{1.0,0.0,0.0},v=new[]{0.0,1.0,0.0},depth=depth,
                curves=TRectangle(left,front,right,rear)};
        }
        static HouseFeatureSpec TBoreFeature(string name,double y,double depth,double centerX,double centerZ,double radius) {
            return new HouseFeatureSpec{name=name,kind="cut",origin=new[]{0.0,y,0.0},u=new[]{1.0,0.0,0.0},v=new[]{0.0,0.0,1.0},depth=depth,
                curves=new[]{new HouseCurve{kind="circle",center=new[]{centerX,centerZ},radius=radius}}};
        }
        static List<string> TMakeHousing(SldWorks app,TwoStageSpec spec,string output,List<object> metrics) {
            var h=spec.housing;
            var lower=new List<HouseFeatureSpec>{
                TBoxFeature("Lower_Outer","boss",0,h.axis_height,h.x_min,h.y_min,h.x_max,h.y_max),
                TBoxFeature("Lower_Cavity","cut",h.axis_height,-(h.axis_height-h.floor_thickness),h.x_min+h.side_wall,h.y_min+h.front_wall,h.x_max-h.side_wall,h.y_max-h.rear_wall)};
            var upper=new List<HouseFeatureSpec>{
                TBoxFeature("Upper_Outer","boss",0,h.upper_height,h.x_min,h.y_min,h.x_max,h.y_max),
                TBoxFeature("Upper_Cavity","cut",0,h.upper_height-h.roof_thickness,h.x_min+h.side_wall,h.y_min+h.front_wall,h.x_max-h.side_wall,h.y_max-h.rear_wall)};
            if(spec.hardware_version==2)TAppendHardwareHousingFeatures(spec,lower,upper);
            foreach(var bearing in spec.bearings) {
                double x=TShaft(spec,bearing.name).center_x,radius=bearing.outer_diameter/2+.05;
                lower.Add(TBoreFeature(bearing.name+"_FrontBearingSeat",h.y_min,-h.front_wall,x,h.axis_height,radius));
                lower.Add(TBoreFeature(bearing.name+"_RearBearingSeat",h.y_max,h.rear_wall,x,h.axis_height,radius));
                upper.Add(TBoreFeature(bearing.name+"_FrontBearingSeat",h.y_min,-h.front_wall,x,0,radius));
                upper.Add(TBoreFeature(bearing.name+"_RearBearingSeat",h.y_max,h.rear_wall,x,0,radius));
            }
            var result=new List<string>();
            double ext=spec.hardware_version==2?18:0,front=spec.hardware_version==2?TEndFlangeProjection:0;
            result.Add(HPart(app,lower.ToArray(),"housing",output,metrics,new[]{h.x_min-ext,h.y_min-front,0.0,h.x_max+ext,h.y_max+front,h.axis_height}));
            result.Add(HPart(app,upper.ToArray(),"cover",output,metrics,new[]{h.x_min-ext,h.y_min-front,0.0,h.x_max+ext,h.y_max+front,h.upper_height}));
            return result;
        }
        static AuxiliarySpec[] TKeys(TwoStageSpec spec) {
            var keys=new List<AuxiliarySpec>();
            foreach(var shaft in spec.shafts)for(int i=0;i<shaft.keyways.Length;i++) {
                var k=shaft.keyways[i];double d=shaft.axis_direction;
                keys.Add(new AuxiliarySpec{name=shaft.name+"_key_"+(i+1),kind="key",length=k.length,width=k.width,height=k.depth+Math.Max(1.5,k.width/3),
                    shaft_diameter=k.diameter,shaft_slot_depth=k.depth,start_x=k.start,start_z=k.diameter/2-k.depth,
                    placement=new[]{0.0,d,0,-d,0,0,0,0,1,shaft.center_x,shaft.origin_y,spec.housing.axis_height}});
            }
            return keys.ToArray();
        }
        static double TShaftDiameterAt(TwoStageShaft shaft,double station) {
            double diameter=0;
            for(int i=0;i<shaft.sections.Length;i++)if(station>=TStart(shaft,i)-1e-5&&station<=TStart(shaft,i+1)+1e-5)
                diameter=Math.Max(diameter,shaft.sections[i].diameter);
            if(diameter<=0)throw new ArgumentException("端盖轴孔无法定位轴段："+shaft.name);
            return diameter;
        }
        static string[] TMakeEndCovers(SldWorks app,TwoStageSpec spec,string output,List<object> metrics) {
            var result=new List<string>();double thickness=spec.end_cover_thickness;
            foreach(var shaft in spec.shafts) {
                var bearing=TBearing(spec,shaft.name);
                foreach(string side in new[]{"front","rear"}) {
                    bool front=side=="front";double exterior=front?spec.housing.y_min:spec.housing.y_max;
                    double station=(exterior-shaft.origin_y)/shaft.axis_direction;
                    bool through=station>=0&&station<=TTotal(shaft);
                    string name=shaft.name+"_"+side+"_end_cover";
                    if(through) {
                        double diameter=TShaftDiameterAt(shaft,station);
                        result.Add(MakeRing(app,new Ring{name=name,outer_diameter=bearing.outer_diameter+16,inner_diameter=diameter+1,thickness=thickness,chamfer=0},output,metrics));
                    } else result.Add(MakeCover(app,new Cover{outer_diameter=bearing.outer_diameter+16,step_diameter=bearing.outer_diameter+8,
                        inner_recess_diameter=bearing.inner_diameter+4,total_thickness=thickness,step_height=thickness/2,recess_depth=.8},name,output,metrics));
                    if(spec.hardware_version==2)TDrillEndCover(app,spec,bearing,name,output,metrics);
                }
            }
            return result.ToArray();
        }
        public static List<AssemblyInstance> PlanTwoStageAssembly(TwoStageSpec spec,IEnumerable<string> files) {
            ValidateTwoStage(spec);
            var result=new List<AssemblyInstance>();var h=spec.housing;
            foreach(var shaft in spec.shafts)result.Add(new AssemblyInstance{name=shaft.name+"_shaft",file=shaft.name+"_shaft.SLDPRT",x=shaft.center_x,y=shaft.origin_y,z=h.axis_height,axis_y_direction=shaft.axis_direction});
            result.Add(new AssemblyInstance{name="high_wheel",file="high_wheel.SLDPRT",x=TShaft(spec,"intermediate").center_x,y=spec.high_pair.wheel_start_y,z=h.axis_height,axis_y_direction=1});
            result.Add(new AssemblyInstance{name="low_pinion",file="low_pinion.SLDPRT",x=TShaft(spec,"intermediate").center_x,y=spec.low_pair.pinion_start_y,z=h.axis_height,axis_y_direction=1});
            result.Add(new AssemblyInstance{name="low_wheel",file="low_wheel.SLDPRT",x=TShaft(spec,"output").center_x,y=spec.low_pair.wheel_start_y,z=h.axis_height,axis_y_direction=1});
            foreach(var bearing in spec.bearings)foreach(string side in new[]{"front","rear"})result.Add(new AssemblyInstance{name=bearing.name+"_bearing_"+side,file=bearing.name+"_bearing.SLDPRT",x=TShaft(spec,bearing.name).center_x,
                y=side=="front"?bearing.front_y:bearing.rear_y,z=h.axis_height,axis_y_direction=1});
            result.Add(new AssemblyInstance{name="housing",file="housing.SLDPRT",x=0,y=0,z=0,placement=new[]{1.0,0,0,0,1,0,0,0,1,0,0,0}});
            result.Add(new AssemblyInstance{name="cover",file="cover.SLDPRT",x=0,y=0,z=h.axis_height,placement=new[]{1.0,0,0,0,1,0,0,0,1,0,0,h.axis_height}});
            foreach(var key in TKeys(spec))result.Add(new AssemblyInstance{name=key.name,file=key.name+".SLDPRT",x=key.placement[9],y=key.placement[10],z=key.placement[11],placement=key.placement});
            foreach(var shaft in spec.shafts)foreach(string side in new[]{"front","rear"})result.Add(new AssemblyInstance{name=shaft.name+"_"+side+"_end_cover",file=shaft.name+"_"+side+"_end_cover.SLDPRT",x=shaft.center_x,
                y=side=="front"?h.y_min-spec.end_cover_thickness:h.y_max,z=h.axis_height,axis_y_direction=1});
            if(spec.hardware_version==2)result.AddRange(TPlanHardware(spec));
            var actual=new HashSet<string>(files.Select(Path.GetFileName),StringComparer.OrdinalIgnoreCase);
            var expected=new HashSet<string>(result.Select(i=>i.file),StringComparer.OrdinalIgnoreCase);
            if(!actual.SetEquals(expected))throw new ArgumentException("二级装配零件与计划不符：缺少 "+String.Join(",",expected.Except(actual))+"；多出 "+String.Join(",",actual.Except(expected)));
            return result;
        }
        const string TwoStageMatedLayout="housing grounded; other components constrained by native mates";
        const string TwoStageFixedLayout="components fixed at calculated positions; no native mates";
        static double[] TAssemblyTransform(AssemblyInstance item) {
            double d=item.axis_y_direction;
            double[] transform={0,d,0,-d,0,0,0,0,1,M(item.x),M(item.y),M(item.z),1,0,0,0};
            if(item.placement!=null)
                for(int i=0;i<12;i++)transform[i]=i>=9?M(item.placement[i]):item.placement[i];
            return transform;
        }

        static TwoStageAssemblyResult TAssemble(SldWorks app,TwoStageSpec spec,IEnumerable<string> paths,string output,bool addMates) {
            var files=paths.ToArray();var plan=PlanTwoStageAssembly(spec,files);
            var doc=NewDocument(app,2,"reducer_assembly");ShowCosmeticThreads(doc);var assembly=(AssemblyDoc)doc;var math=(MathUtility)app.GetMathUtility();var components=new Dictionary<string,Component2>();
            try {
                foreach(var item in plan) {
                    GenerationControl.Checkpoint("总装定位："+ModelPresentation.ChineseName(item.file)+"（"+(components.Count+1)+"/"+plan.Count+"）");
                    var component=InsertSavedPart(app,doc,assembly,Path.Combine(output,item.file),item.x,item.y,item.z,item.name);
                    var transform=TAssemblyTransform(item);
                    if(!addMates&&component.IsFixed()) {
                        doc.ClearSelection2(true);
                        if(!component.Select4(false,null,false))throw new Exception("无法选择二级总装组件："+item.name);
                        assembly.UnfixComponent();
                        if(component.IsFixed())throw new Exception("无法解除二级总装组件的初始固定："+item.name);
                    }
                    component.Transform2=(MathTransform)math.CreateTransform(transform);
                    component.Name2=item.name;components.Add(item.name,component);
                    if(addMates&&item.name=="housing") {
                        doc.ClearSelection2(true);
                        if(!component.Select4(false,null,false))throw new Exception("无法选择二级总装组件："+item.name);
                        assembly.FixComponent();
                        if(!component.IsFixed())throw new Exception("无法固定二级总装组件："+item.name);
                    }
                }
                if(!addMates) {
                    GenerationControl.Checkpoint("正在固定总装零件");
                    doc.ClearSelection2(true);
                    foreach(var pair in components)
                        if(!pair.Value.Select4(true,null,false))throw new Exception("无法选择二级总装组件："+pair.Key);
                    assembly.FixComponent();
                    foreach(var pair in components.Where(pair=>!pair.Value.IsFixed())) {
                        doc.ClearSelection2(true);
                        if(!pair.Value.Select4(false,null,false))throw new Exception("无法选择二级总装组件："+pair.Key);
                        assembly.FixComponent();
                        if(!pair.Value.IsFixed())throw new Exception("无法固定二级总装组件："+pair.Key);
                    }
                }
                if(addMates)GenerationControl.Checkpoint("正在添加总装配合");
                var mates=addMates?MateTwoStage(app,doc,assembly,spec,components):new List<TwoStageMateRecord>();
                GenerationControl.Checkpoint("正在重建总装");
                doc.ClearSelection2(true);if(!doc.EditRebuild3())throw new Exception("二级总装重建失败。");
                if((assembly.GetComponents(true) as object[]??new object[0]).Length!=plan.Count)throw new Exception("二级总装顶层组件数与计划不符。");
                if(!addMates)foreach(var item in plan)
                    if(!TMatePosition(TAssemblyTransform(item),(double[])components[item.name].Transform2.ArrayData))
                        throw new Exception("二级总装组件偏离计算位置："+item.name);
                int namingPreference=(int)swUserPreferenceToggle_e.swExtRefUpdateCompNames;
                bool previousNaming=app.GetUserPreferenceToggle(namingPreference);
                app.SetUserPreferenceToggle(namingPreference,false);
                try {
                    ModelPresentation.NameAssemblyComponents(app,doc,plan,components,mates);
                    Save(doc,Path.Combine(output,"reducer_assembly.SLDASM"));
                } finally {app.SetUserPreferenceToggle(namingPreference,previousNaming);}
                return new TwoStageAssemblyResult{instances=plan,mates=mates};
            } finally {
                app.CloseDoc(doc.GetTitle());
            }
        }
        public static TwoStageAssemblyResult RemateTwoStage(SldWorks app,string output) {
            return RemateTwoStage(app,output,true);
        }
        public static TwoStageAssemblyResult RemateTwoStage(SldWorks app,string output,bool addMates) {
            string folder=Path.GetFullPath(output);var json=new JavaScriptSerializer();
            var parameters=json.Deserialize<Parameters>(File.ReadAllText(Path.Combine(folder,"parameters.json")));
            if(parameters.schema_version!=5)throw new ArgumentException("仅能重装配二级 schema v5 模型。");
            ValidateTwoStage(parameters.two_stage);
            var paths=Directory.GetFiles(folder,"*.SLDPRT").Where(path=>!Path.GetFileName(path).StartsWith("~$")).ToArray();
            var existing=new HashSet<IntPtr>((app.GetDocuments() as object[]??new object[0]).Select(DocumentIdentity));
            try {
                var result=TAssemble(app,parameters.two_stage,paths,folder,addMates);
                var manifestPath=Path.Combine(folder,"parts_manifest.json");
                if(File.Exists(manifestPath)) {
                    var manifest=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(manifestPath));
                    manifest["schema_version"]=5;manifest["instances"]=result.instances;manifest["component_count"]=result.instances.Count;
                    manifest["top_level_mates"]=result.mates;manifest["assembly_mates_added"]=addMates;manifest["layout_scope"]=addMates?TwoStageMatedLayout:TwoStageFixedLayout;
                    File.WriteAllText(manifestPath,json.Serialize(manifest));
                }
                return result;
            } finally {
                foreach(object item in app.GetDocuments() as object[]??new object[0]) {
                    var doc=item as ModelDoc2;if(doc==null||existing.Contains(DocumentIdentity(doc)))continue;
                    string path=doc.GetPathName();
                    if(String.IsNullOrEmpty(path)||!path.StartsWith(folder+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||doc.GetSaveFlag())continue;
                    try {app.CloseDoc(doc.GetTitle());}
                    catch(Exception closeError) {Console.Error.WriteLine("Could not close remate part: "+path+" "+closeError.Message);}
                }
            }
        }
        static string GenerateTwoStage(SldWorks app,Parameters p,string parameterFile,string outputRoot,bool addMates) {
            var spec=p.two_stage;ValidateTwoStage(spec);
            string output=Path.Combine(outputRoot,DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")+"_two_stage_"+Guid.NewGuid().ToString("N").Substring(0,6));
            Directory.CreateDirectory(output);File.Copy(parameterFile,Path.Combine(output,"parameters.json"));
            var log=new List<string>();var metrics=new List<object>();var paths=new List<string>();var serializer=new JavaScriptSerializer();
            generationDocuments=new HashSet<IntPtr>();bool prompt=app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate);
            app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate,false);
            try {
                foreach(var shaft in spec.shafts){paths.Add(TMakeShaft(app,shaft,spec.high_pair,output,metrics));log.Add("shaft "+shaft.name);}
                // The input shaft is mounted with a reversed local X axis in
                // the reference assembly. External mesh hands must oppose in
                // world coordinates, so the high wheel's local hand follows
                // the input axis direction instead of blindly negating beta.
                int inputDirection=TShaft(spec,"input").axis_direction;
                paths.Add(TMakeGear(app,"high_wheel",spec.high_pair,false,-spec.high_pair.helix_angle*inputDirection,output,metrics,inputDirection));
                paths.Add(TMakeGear(app,"low_pinion",spec.low_pair,true,spec.low_pair.helix_angle,output,metrics));
                paths.Add(TMakeGear(app,"low_wheel",spec.low_pair,false,-spec.low_pair.helix_angle,output,metrics));
                var bearings=spec.bearings.Select(b=>new BearingSpec{name=b.name+"_bearing",catalog_name=b.catalog_name,inner_diameter=b.inner_diameter,outer_diameter=b.outer_diameter,width=b.width}).ToArray();
                paths.AddRange(MakeBearings(app,bearings,output,metrics));
                paths.AddRange(MakeAuxiliary(app,TKeys(spec),output,metrics));
                paths.AddRange(TMakeHousing(app,spec,output,metrics));
                paths.AddRange(TMakeEndCovers(app,spec,output,metrics));
                if(spec.hardware_version==2)paths.AddRange(TMakeHardware(app,spec,output,metrics));
                var assembled=TAssemble(app,spec,paths,output,addMates);
                File.WriteAllText(Path.Combine(output,"parts_manifest.json"),serializer.Serialize(new{schema_version=5,units="mm",reducer_type="two_stage_spur",parts=metrics,assembly="reducer_assembly.SLDASM",component_count=assembled.instances.Count,instances=assembled.instances,top_level_mates=assembled.mates,assembly_mates_added=addMates,layout_scope=addMates?TwoStageMatedLayout:TwoStageFixedLayout}));
                File.WriteAllText(Path.Combine(output,"two_stage_geometry.json"),serializer.Serialize(new{high_center_distance=(TPitchDiameter(spec.high_pair,spec.high_pair.pinion_teeth)+TPitchDiameter(spec.high_pair,spec.high_pair.wheel_teeth))/2,
                    low_center_distance=(TPitchDiameter(spec.low_pair,spec.low_pair.pinion_teeth)+TPitchDiameter(spec.low_pair,spec.low_pair.wheel_teeth))/2,
                    total_ratio=(double)spec.high_pair.wheel_teeth/spec.high_pair.pinion_teeth*spec.low_pair.wheel_teeth/spec.low_pair.pinion_teeth,
                    source_files=spec.reference_files,limitations=spec.limitations}));
                log.Add("Saved "+paths.Count+" native parts, "+assembled.instances.Count+" top-level components and "+assembled.mates.Count+" native mates.");
                log.Add("Two-stage input pinion is integral; other gears, three shafts, three bearing pairs, keys, split housing and six end covers are native parts.");
                return output;
            } catch(Exception e){log.Add(e.ToString());throw;}
            finally {
                app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate,prompt);
                try{foreach(object item in app.GetDocuments() as object[]??new object[0]){var doc=item as ModelDoc2;if(doc==null)continue;string opened=doc.GetPathName();if(generationDocuments.Contains(DocumentIdentity(doc))||(!String.IsNullOrEmpty(opened)&&opened.StartsWith(output+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)))app.CloseDoc(doc.GetTitle());}}catch(Exception cleanup){log.Add("Document cleanup: "+cleanup.Message);}
                generationDocuments=null;File.WriteAllLines(Path.Combine(output,"generation.log"),log);
            }
        }
    }
}
