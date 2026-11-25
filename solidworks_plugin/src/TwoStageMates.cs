using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ReducerSW {
    public sealed class TwoStageMateRecord {
        public string driver {get;set;}
        public string driven {get;set;}
        public string kind {get;set;}
        public string feature {get;set;}
    }
    public sealed class TwoStageAssemblyResult {
        public List<AssemblyInstance> instances {get;set;}
        public List<TwoStageMateRecord> mates {get;set;}
    }
    public static partial class Generator {
        // One-stage assemblies contain many repeated fasteners. AddMate5 solves
        // each mate immediately; a full gear/housing rebuild is needed once at
        // the end, not after every fastener and shaft mate.
        static bool BatchNativeMates;
        static HashSet<string> TwoStageMateNames;
        static Feature TPlane(Component2 part,int index) {
            var model=part.GetModelDoc2() as ModelDoc2;
            if(model==null)throw new Exception("配合组件未解析："+part.Name2);
            int found=0;
            for(var feature=(Feature)model.FirstFeature();feature!=null;feature=(Feature)feature.GetNextFeature())
                if(feature.GetTypeName2()=="RefPlane"&&found++==index) {
                    var inAssembly=part.FeatureByName(feature.Name);
                    if(inAssembly==null)break;
                    return inAssembly;
                }
            throw new Exception("组件缺少第 "+(index+1)+" 个配合基准面："+part.Name2);
        }
        static bool TMatePosition(double[] expected,double[] actual) {
            if(expected==null||actual==null||expected.Length<12||actual.Length<12)return false;
            for(int i=0;i<9;i++)if(Math.Abs(expected[i]-actual[i])>1e-4)return false;
            for(int i=9;i<12;i++)if(Math.Abs(expected[i]-actual[i])>M(.04))return false;
            return true;
        }
        static Feature TAddMate(SldWorks app,ModelDoc2 doc,AssemblyDoc assembly,Component2 driven,
            Func<bool> select,int kind,double distance,string label,double gearNumerator=0,double gearDenominator=0) {
            var math=(MathUtility)app.GetMathUtility();
            var expected=(double[])driven.Transform2.ArrayData;
            int[] alignments=kind==0?new[]{0,1,2}:new[]{2,0,1};
            var diagnostics=new List<string>();
            foreach(int attempt in Enumerable.Range(0,kind==5?alignments.Length*2:alignments.Length)) {
                int align=alignments[attempt%alignments.Length];bool flip=attempt>=alignments.Length;
                driven.Transform2=(MathTransform)math.CreateTransform(expected);
                doc.ClearSelection2(true);
                if(!select())throw new Exception("无法选择配合实体："+label);
                var before=TwoStageMateNames??new HashSet<string>(AssemblyMateFeatures(doc).Select(f=>f.Name),StringComparer.Ordinal);
                int error;
                var mate=assembly.AddMate5(kind,align,flip,M(distance),0,0,gearNumerator,gearDenominator,0,0,0,false,false,0,out error);
                doc.ClearSelection2(true);
                // Gear mates retain a rotational degree of freedom. Solve them
                // immediately while their deliberately chosen tooth phase is
                // still the current assembly state; postpone ordinary mates.
                if(!BatchNativeMates||kind==10)doc.EditRebuild3();
                Feature last=null;
                if(TwoStageMateNames!=null&&mate!=null&&error==(int)swAddMateError_e.swAddMateError_NoError) {
                    var candidate=doc.Extension.GetLastFeatureAdded();
                    if(candidate!=null&&!before.Contains(candidate.Name)) {
                        var specific=candidate.GetSpecificFeature2() as Mate2;
                        if(specific!=null&&app.IsSame(specific,mate)==(int)swObjectEquality.swObjectSame)last=candidate;
                    }
                }
                var created=last!=null?new[]{last}:AssemblyMateFeatures(doc)
                    .Where(f=>f.GetTypeName2().StartsWith("Mate")&&f.GetTypeName2()!="MateGroup"&&!before.Contains(f.Name)).ToArray();
                if(created.Length>1)throw new Exception("本次配合产生多个特征，无法安全回退："+label);
                if(mate!=null&&error==(int)swAddMateError_e.swAddMateError_NoError&&created.Length==1&&
                    MateFeatureHealthy(created[0])&&TMatePosition(expected,(double[])driven.Transform2.ArrayData)) {
                    created[0].Name="TS_"+label;
                    if(TwoStageMateNames!=null)TwoStageMateNames.Add(created[0].Name);
                    return created[0];
                }
                var actual=(double[])driven.Transform2.ArrayData;
                diagnostics.Add("align="+align+" flip="+flip+" error="+MateErrorName(error)+" feature="+(created.Length==1?created[0].GetTypeName2():"none")+
                    " position="+TMatePosition(expected,actual)+" y="+(actual.Length>10?actual[10]/M(1):Double.NaN).ToString("0.###"));
                if(created.Length==1)DeleteCreatedMate(doc,created[0]);
            }
            driven.Transform2=(MathTransform)math.CreateTransform(expected);
            throw new Exception("装配配合失败："+label+"；"+String.Join("；",diagnostics));
        }
        static Feature TConcentric(SldWorks app,ModelDoc2 doc,AssemblyDoc assembly,
            Component2 driver,Component2 driven,double driverRadius,double drivenRadius,string label) {
            return TAddMate(app,doc,assembly,driven,()=>{
                var a=FindCylindricalFace(app,driver,driverRadius,new[]{0.0,1.0,0.0});
                var b=FindCylindricalFace(app,driven,drivenRadius,new[]{0.0,1.0,0.0});
                if(a==null||b==null)throw new Exception("未找到同轴圆柱面："+label+
                    "；基准="+DescribeCircles(app,driver)+"；组件="+DescribeCircles(app,driven));
                var first=driver.GetCorrespondingEntity(a) as Entity;
                var second=driven.GetCorrespondingEntity(b) as Entity;
                if(first==null||second==null)return false;
                var selection=((SelectionMgr)doc.SelectionManager).CreateSelectData();selection.Mark=1;
                return first.Select4(false,selection)&&second.Select4(true,selection);
            },1,0,label);
        }
        static Feature TPlaneMate(SldWorks app,ModelDoc2 doc,AssemblyDoc assembly,
            Component2 driver,int driverPlane,Component2 driven,int drivenPlane,double distance,string label) {
            int kind=distance<1e-6?0:5;
            return TAddMate(app,doc,assembly,driven,()=>{
                var first=TPlane(driver,driverPlane);var second=TPlane(driven,drivenPlane);
                return TSelectMatePlane(doc,first,false)&&TSelectMatePlane(doc,second,true);
            },kind,distance,label);
        }
        static bool TSelectMatePlane(ModelDoc2 doc,Feature plane,bool append) {
            if(plane.Select2(append,1))return true;
            string selectionType;
            string selectionName=plane.GetNameForSelection(out selectionType);
            return !String.IsNullOrEmpty(selectionName)&&!String.IsNullOrEmpty(selectionType)&&
                doc.Extension.SelectByID2(selectionName,selectionType,0,0,0,append,1,null,0);
        }
        static Feature TGearMate(SldWorks app,ModelDoc2 doc,AssemblyDoc assembly,
            Component2 driver,Component2 driven,double driverRadius,double drivenRadius,
            int driverTeeth,int drivenTeeth,string label) {
            return TAddMate(app,doc,assembly,driven,()=>{
                var a=FindCylindricalFace(app,driver,driverRadius,new[]{0.0,1.0,0.0});
                var b=FindCylindricalFace(app,driven,drivenRadius,new[]{0.0,1.0,0.0});
                if(a==null||b==null)throw new Exception("找不到齿轮运动配合轴线："+label);
                var first=driver.GetCorrespondingEntity(a) as Entity;
                var second=driven.GetCorrespondingEntity(b) as Entity;
                if(first==null||second==null)return false;
                var selection=((SelectionMgr)doc.SelectionManager).CreateSelectData();selection.Mark=1;
                return first.Select4(false,selection)&&second.Select4(true,selection);
            },10,0,label,driverTeeth,drivenTeeth);
        }
        static void TRecord(List<TwoStageMateRecord> records,Component2 driver,Component2 driven,string kind,Feature feature) {
            records.Add(new TwoStageMateRecord{driver=driver.Name2,driven=driven.Name2,kind=kind,feature=feature.Name});
            Console.WriteLine("  mate "+kind+": "+driver.Name2+" -> "+driven.Name2+" ("+feature.Name+")");
        }
        static double TShaftRotation(double[] start,double[] current) {
            return Math.Atan2(start[8]*current[6]-start[6]*current[8],start[6]*current[6]+start[8]*current[8]);
        }
        static void TDrivePhase(ModelDoc2 doc,Dimension phase,double angle) {
            if(phase.SetSystemValue3(angle,(int)swSetValueInConfiguration_e.swSetValue_InThisConfiguration,null)!=0||!doc.EditRebuild3())
                throw new Exception("二级传动试转无法求解输入轴角度："+(angle*180/Math.PI).ToString("0.###")+"°");
        }
        static void TReleaseMotion(ModelDoc2 doc,AssemblyDoc assembly,TwoStageSpec spec,
            Dictionary<string,Component2> parts,Feature inputPhase) {
            var initial=parts.ToDictionary(part=>part.Key,part=>(double[])part.Value.Transform2.ArrayData);
            var input=parts["input_shaft"];
            var middle=parts["intermediate_shaft"];
            var output=parts["output_shaft"];
            var display=inputPhase.GetFirstDisplayDimension() as DisplayDimension;
            var phase=display==null?null:display.GetDimension2(0) as Dimension;
            if(phase==null)throw new Exception("缺少二级输入轴的初始相位尺寸。");
            double highRatio=(double)spec.high_pair.pinion_teeth/spec.high_pair.wheel_teeth;
            double lowRatio=(double)spec.low_pair.pinion_teeth/spec.low_pair.wheel_teeth;
            double highPitch=2*Math.PI/spec.high_pair.pinion_teeth;
            double lowPitchAtInput=Math.Min(Math.PI/3,2*Math.PI/(spec.low_pair.pinion_teeth*highRatio));
            var angles=new[]{0.0}.Concat(new[]{highPitch,lowPitchAtInput}.SelectMany(pitch=>
                Enumerable.Range(1,4).Select(step=>pitch*step/4))).Distinct().OrderBy(angle=>angle);
            foreach(double angle in angles) {
                string degrees=(angle*180/Math.PI).ToString("0.###")+"°";
                GenerationControl.Checkpoint("二级传动试转："+degrees);
                TDrivePhase(doc,phase,angle);
                double inputTurn=TShaftRotation(initial["input_shaft"],(double[])input.Transform2.ArrayData);
                double middleTurn=TShaftRotation(initial["intermediate_shaft"],(double[])middle.Transform2.ArrayData);
                double outputTurn=TShaftRotation(initial["output_shaft"],(double[])output.Transform2.ArrayData);
                if(Math.Abs(Math.Abs(inputTurn)-angle)>1e-5||
                    Math.Abs(middleTurn+inputTurn*highRatio)>1e-5||
                    Math.Abs(outputTurn+middleTurn*lowRatio)>1e-5)
                    throw new Exception("二级传动试转与齿数比不符，输入轴角度 "+degrees);
                var issues=new List<string>();
                var hits=NativeAudit.ScanInterferences(assembly,issues);
                if(issues.Count>0)throw new Exception("二级传动试转在 "+degrees+" 检出干涉："+issues[0]);
                Console.WriteLine("Two-stage motion "+degrees+": interference detections="+hits.Count);
            }
            TDrivePhase(doc,phase,0);
            if(!inputPhase.SetSuppression2((int)swFeatureSuppressionAction_e.swSuppressFeature,
                (int)swInConfigurationOpts_e.swThisConfiguration,null)||!doc.EditRebuild3())
                throw new Exception("无法解除二级输入轴的初始相位定位。");
            ValidateAssemblyMates(doc);
            foreach(var part in parts)
                if(!TMatePosition(initial[part.Key],(double[])part.Value.Transform2.ArrayData))
                    throw new Exception("二级传动试转后组件未回到初始装配位置："+part.Key);
            foreach(var shaft in spec.shafts)
                if(parts[shaft.name+"_shaft"].GetConstrainedStatus()!=(int)swConstrainedStatus_e.swUnderConstrained)
                    throw new Exception("二级轴系未保留旋转自由度："+shaft.name);
        }
        static List<TwoStageMateRecord> MateTwoStage(SldWorks app,ModelDoc2 doc,AssemblyDoc assembly,
            TwoStageSpec spec,Dictionary<string,Component2> parts) {
            BatchNativeMates=true;
            try {
                TwoStageMateNames=new HashSet<string>(AssemblyMateFeatures(doc).Select(f=>f.Name),StringComparer.Ordinal);
                return MateTwoStageBatch(app,doc,assembly,spec,parts);
            } finally {
                TwoStageMateNames=null;
                BatchNativeMates=false;
            }
        }
        static List<TwoStageMateRecord> MateTwoStageBatch(SldWorks app,ModelDoc2 doc,AssemblyDoc assembly,
            TwoStageSpec spec,Dictionary<string,Component2> parts) {
            var mates=new List<TwoStageMateRecord>();var housing=parts["housing"];
            int light=assembly.GetLightWeightComponentCount();
            if(light>0){assembly.ResolveAllLightweight();doc.EditRebuild3();}
            foreach(var pair in parts)if(pair.Key!="housing") {
                doc.ClearSelection2(true);pair.Value.Select4(false,null,false);assembly.UnfixComponent();
                if(pair.Value.IsFixed())throw new Exception("组件无法解除固定："+pair.Key);
            }
            var cover=parts["cover"];
            TRecord(mates,housing,cover,"coincident",TPlaneMate(app,doc,assembly,housing,1,cover,1,0,"cover_y"));
            TRecord(mates,housing,cover,"coincident",TPlaneMate(app,doc,assembly,housing,2,cover,2,0,"cover_x"));
            TRecord(mates,housing,cover,"distance",TPlaneMate(app,doc,assembly,housing,0,cover,0,spec.housing.axis_height,"cover_split"));
            foreach(var shaft in spec.shafts) {
                GenerationControl.Checkpoint("正在配合："+ModelPresentation.ChineseName(shaft.name+"_shaft"));
                var component=parts[shaft.name+"_shaft"];var bearing=TBearing(spec,shaft.name);
                TRecord(mates,housing,component,"concentric",TConcentric(app,doc,assembly,housing,component,bearing.outer_diameter/2+.05,bearing.inner_diameter/2,shaft.name+"_shaft_axis"));
                TRecord(mates,housing,component,"distance",TPlaneMate(app,doc,assembly,housing,1,component,2,Math.Abs(shaft.origin_y),shaft.name+"_shaft_axial"));
            }
            // Hold the initial tooth phase while creating and solving the gear mates.
            var inputPhase=OAngle(app,doc,assembly,housing,parts["input_shaft"],"input_shaft_phase");
            TRecord(mates,housing,parts["input_shaft"],"angle",inputPhase);
            foreach(var gear in new[]{new{name="high_wheel",shaft="intermediate",bore=spec.high_pair.wheel_bore,start=spec.high_pair.wheel_start_y},
                                     new{name="low_pinion",shaft="intermediate",bore=spec.low_pair.pinion_bore,start=spec.low_pair.pinion_start_y},
                                     new{name="low_wheel",shaft="output",bore=spec.low_pair.wheel_bore,start=spec.low_pair.wheel_start_y}}) {
                var driver=parts[gear.shaft+"_shaft"];var component=parts[gear.name];var shaft=TShaft(spec,gear.shaft);
                TRecord(mates,driver,component,"concentric",TConcentric(app,doc,assembly,driver,component,gear.bore/2,gear.bore/2,gear.name+"_bore"));
                TRecord(mates,driver,component,"distance",TPlaneMate(app,doc,assembly,driver,2,component,2,Math.Abs(gear.start-shaft.origin_y),gear.name+"_axial"));
                TRecord(mates,driver,component,"coincident",TPlaneMate(app,doc,assembly,driver,1,component,1,0,gear.name+"_key_angle"));
            }
            foreach(var bearing in spec.bearings)foreach(string side in new[]{"front","rear"}) {
                var component=parts[bearing.name+"_bearing_"+side];
                TRecord(mates,housing,component,"concentric",TConcentric(app,doc,assembly,housing,component,bearing.outer_diameter/2+.05,bearing.outer_diameter/2,bearing.name+"_bearing_"+side+"_seat"));
                double y=side=="front"?bearing.front_y:bearing.rear_y;
                TRecord(mates,housing,component,"distance",TPlaneMate(app,doc,assembly,housing,1,component,2,Math.Abs(y),bearing.name+"_bearing_"+side+"_axial"));
            }
            foreach(var shaft in spec.shafts)for(int i=0;i<shaft.keyways.Length;i++) {
                var driver=parts[shaft.name+"_shaft"];var key=parts[shaft.name+"_key_"+(i+1)];
                for(int plane=0;plane<3;plane++)
                    TRecord(mates,driver,key,"coincident",TPlaneMate(app,doc,assembly,driver,plane,key,plane,0,shaft.name+"_key_"+(i+1)+"_plane"+plane));
            }
            foreach(var shaft in spec.shafts)foreach(string side in new[]{"front","rear"}) {
                var bearing=TBearing(spec,shaft.name);var component=parts[shaft.name+"_"+side+"_end_cover"];
                string label=shaft.name+"_"+side+"_end_cover";
                TRecord(mates,housing,component,"concentric",TConcentric(app,doc,assembly,housing,component,bearing.outer_diameter/2+.05,bearing.outer_diameter/2+8,label+"_axis"));
                double y=side=="front"?spec.housing.y_min-spec.end_cover_thickness:spec.housing.y_max;
                TRecord(mates,housing,component,"distance",TPlaneMate(app,doc,assembly,housing,1,component,2,Math.Abs(y),label+"_axial"));
            }
            TRecord(mates,parts["input_shaft"],parts["high_wheel"],"gear",
                TGearMate(app,doc,assembly,parts["input_shaft"],parts["high_wheel"],
                    TBearing(spec,"input").inner_diameter/2,spec.high_pair.wheel_bore/2,
                    spec.high_pair.pinion_teeth,spec.high_pair.wheel_teeth,"high_stage_gear_ratio"));
            TRecord(mates,parts["low_pinion"],parts["low_wheel"],"gear",
                TGearMate(app,doc,assembly,parts["low_pinion"],parts["low_wheel"],
                    spec.low_pair.pinion_bore/2,spec.low_pair.wheel_bore/2,
                    spec.low_pair.pinion_teeth,spec.low_pair.wheel_teeth,"low_stage_gear_ratio"));
            if(spec.hardware_version==2)TMateHardware(app,doc,assembly,spec,parts,mates);
            doc.ClearSelection2(true);if(!doc.EditRebuild3())throw new Exception("二级配合总装重建失败。");
            ValidateAssemblyMates(doc);
            TReleaseMotion(doc,assembly,spec,parts,inputPhase);
            foreach(var part in parts) {
                if(part.Key=="housing")continue;
                if(part.Value.IsFixed()||((part.Value.GetMates() as object[])??new object[0]).Length<2)
                    throw new Exception("二级组件未由配合定位："+part.Key);
            }
            return mates;
        }
    }
}
