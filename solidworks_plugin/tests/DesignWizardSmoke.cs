using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using ReducerSW;

class DesignWizardSmoke {
    static readonly BindingFlags Private=BindingFlags.NonPublic|BindingFlags.Instance;
    static object Field(object target,string name){return target.GetType().GetField(name,Private).GetValue(target);}
    static object Call(object target,string name,params object[] arguments){return target.GetType().GetMethod(name,Private).Invoke(target,arguments);}
    static void Assert(bool value,string message){if(!value)throw new Exception(message);}
    static Dictionary<string,object> Map(object value){return (Dictionary<string,object>)value;}
    [STAThread] static int Main(string[] args) {
        try {
            Application.EnableVisualStyles();
            using(var wizard=new DesignWizard(null)) {
                wizard.GetType().GetField("workFolder",Private).SetValue(wizard,Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),"smoke_work"));
                var navigation=(ListBox)Field(wizard,"navigation");
                Assert(navigation.Items.Count==8,"The native design workflow must have eight pages.");
                var fields=(IDictionary)Field(wizard,"fields");
                foreach(string key in new[]{"P","n1","target_speed","moduleM","highBearing","lowBearing","highKey","lowGearKey","lowOuterKey","housingAxialWidth","ventStandOff","ventOffsetY"})Assert(fields.Contains(key),"Missing design input: "+key);
                Assert(!fields.Contains("housingWallThickness")&&!fields.Contains("housingRibThickness"),"Unapplied independent housing dimensions must not be editable.");
                foreach(string prefix in new[]{"high","low"})for(int index=1;index<=(prefix=="high"?8:6);index++)foreach(string dimension in new[]{"D","L"})Assert(fields.Contains(prefix+"S"+index+dimension),"Missing shaft segment field.");
                foreach(string key in new[]{"highS2D","highS8D","lowS1D","lowS4D","highS5D","highS5L","lowS2L"}){
                    var cell=(DataGridViewCell)fields[key].GetType().GetField("Cell").GetValue(fields[key]);Assert(cell.ReadOnly,"Linked shaft geometry must be read-only: "+key);
                }
                foreach(string name in new[]{"results","checks","layoutResults"})Assert(((DataGridView)Field(wizard,name)).ReadOnly,"Calculated result grids must be read-only.");
                Call(wizard,"ApplySchema",new Dictionary<string,object>{{"parameters",new Dictionary<string,object>()},{"choices",new Dictionary<string,object>()}});
                var automatic=Map(Call(wizard,"CollectParameters",true));
                Assert(automatic.ContainsKey("P")&&automatic.ContainsKey("target_speed")&&automatic.ContainsKey("toothFactorH"),"Automatic design must use current operating conditions and manual factors.");
                foreach(string key in new[]{"z1","z2","moduleM","pinionFaceWidth","gearFaceWidth","highS1D","highBearing"})Assert(!automatic.ContainsKey(key),"Automatic design must not silently fix current selected sizes: "+key);
                var design=new Dictionary<string,object>{
                    {"inputs",new Dictionary<string,object>{{"P",1.2},{"n1",1450.0},{"z1",33},{"z2",99},{"moduleM",1.25}}},
                    {"selections",new Dictionary<string,object>{{"gearType","spur"},{"highBearing","6202"}}},
                    {"results",new Dictionary<string,object>{{"outSpeed",483.333},{"centerDistance",82.5}}},
                    {"checks",new Dictionary<string,object>{{"contact_strength",new Dictionary<string,object>{{"value",300.0},{"limit",600.0},{"passed",true}}}}},
                    {"calculation_steps",new object[]{new Dictionary<string,object>{{"title","传动参数"},{"text","i=z2/z1=3；n2=483.333 r/min"}}}},
                    {"system_checks_passed",true},{"designConclusion","测试方案"}
                };
                if(args.Length>0){var loaded=new JavaScriptSerializer{MaxJsonLength=Int32.MaxValue}.Deserialize<Dictionary<string,object>>(File.ReadAllText(args[0]));design=loaded.ContainsKey("design")?Map(loaded["design"]):loaded;}
                Call(wizard,"ApplyDesign",design);
                Assert(!(bool)Field(wizard,"dirty"),"Fresh calculation should enable reviewed actions.");
                Assert(((DataGridView)Field(wizard,"results")).Rows.Count>0,"Calculated results were not rendered.");
                Assert(!String.IsNullOrWhiteSpace(((TextBox)Field(wizard,"calculationProcess")).Text),"Calculated steps and review basis must be visible.");
                if(design.ContainsKey("geometry_linkage"))Assert(((TextBox)Field(wizard,"calculationProcess")).Text.Contains("修改前 → 实际采用")&&((DataGridView)Field(wizard,"layoutResults")).Rows.Count>0,"Linked dimension changes must display their before/after values and applied layout.");
                Call(wizard,"RequireCurrent");
                var manual=Map(Call(wizard,"CollectParameters",false));Assert(manual.ContainsKey("highS1D")&&!manual.ContainsKey("target_speed"),"Manual calculations must include structural fields without search-only options.");
                var autoRequest=Map(Call(wizard,"ComputeRequest",true));Assert(!autoRequest.ContainsKey("design"),"A second automatic proposal must not inherit old fixed gears and shafts.");
                Assert(Object.Equals(autoRequest["derive_linked_geometry"],true),"Dimension linkage must be visible and enabled by default.");
                Assert(Map(Call(wizard,"ComputeRequest",false)).ContainsKey("design"),"Manual recalculation must preserve the current design.");
                ((CheckBox)Field(wizard,"linkedGeometry")).Checked=false;Assert(Object.Equals(Map(Call(wizard,"ComputeRequest",false))["derive_linked_geometry"],false),"Manual linkage preference must be sent explicitly.");Call(wizard,"ApplyDesign",design);
                ((TextBox)Field(wizard,"designer")).Text="测试设计者";((TextBox)Field(wizard,"notes")).Text="项目备注";((TextBox)Field(wizard,"projectNumber")).Text="SW-01";
                var project=Map(Call(wizard,"Project"));Assert((string)project["author"]=="测试设计者"&&(string)project["comment"]=="项目备注"&&(string)project["number"]=="SW-01"&&project.ContainsKey("date"),"Project fields must match the shared report contract.");
                Call(wizard,"SetBusy",true,"测试计算状态");Assert(!((Button)Field(wizard,"calculateButton")).Enabled&&!((Button)Field(wizard,"generateButton")).Enabled,"Busy workflows must prevent concurrent commands.");Call(wizard,"SetBusy",false,"操作完成");
                var pCell=(DataGridViewCell)fields["P"].GetType().GetField("Cell").GetValue(fields["P"]);pCell.Value=2.0;
                Assert((bool)Field(wizard,"dirty"),"Editing input must invalidate previous results.");
                Assert(!((Button)Field(wizard,"generateButton")).Enabled,"Stale designs must not generate models.");
                bool rejected=false;try{Call(wizard,"RequireCurrent");}catch(TargetInvocationException e){rejected=e.InnerException is InvalidOperationException;}Assert(rejected,"Stale export guard must reject modified inputs.");
                var draft=Map(Call(wizard,"DraftDesign"));Assert(!draft.ContainsKey("results")&&!draft.ContainsKey("checks"),"Saved edited drafts must not retain stale results or checks.");
                Call(wizard,"LoadProjectSnapshot",new Dictionary<string,object>{{"format","other_compatible_project"},{"version",1},{"project",new Dictionary<string,object>{{"name","跨端项目"},{"author","网页作者"},{"comment","网页备注"},{"number","WEB-01"},{"date","2026-10-27"}}},{"design",design},{"requirements",new Dictionary<string,object>{{"target_speed",480.0}}}});
                Assert((bool)Field(wizard,"dirty"),"Loaded projects must be recalculated before native generation.");
                var loadedProject=Map(Call(wizard,"Project"));Assert((string)loadedProject["author"]=="网页作者"&&(string)loadedProject["comment"]=="网页备注"&&(string)loadedProject["number"]=="WEB-01","Web project metadata must survive cross-client loading.");
            }
            Console.WriteLine("DesignWizard eight pages, editable inputs, linked geometry, read-only results, auto/manual payloads, stale-result guards and cross-client project loading: PASS");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
