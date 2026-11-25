using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using ReducerSW;

class TwoStageSmoke {
    static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
    static Parameters Copy(Parameters source){return Json.Deserialize<Parameters>(Json.Serialize(source));}
    static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
    static void Reject(Parameters source,Action<TwoStageSpec> change,string label){var copy=Copy(source);change(copy.two_stage);try{Generator.Validate(copy);}catch(ArgumentException){return;}throw new Exception("Invalid parameters accepted: "+label);}
    [STAThread]static int Main(string[] args){try {
        var p=Json.Deserialize<Parameters>(File.ReadAllText(args[0]));Generator.Validate(p);
        string sourceText=File.ReadAllText(args[0]);
        using(var editor=new TwoStageEditor(args[0])) {
            Require(editor.Text.Contains("二级减速器"),"Two-stage editor failed to load the sample.");
            var tabs=(TabControl)typeof(TwoStageEditor).GetField("tabs",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(editor);
            Require(tabs.TabPages.Count==9&&tabs.TabPages.Cast<TabPage>().Any(t=>t.Text=="高速级齿轮副")&&
                tabs.TabPages.Cast<TabPage>().Any(t=>t.Text=="输出轴轴段")&&
                !tabs.TabPages.Cast<TabPage>().Any(t=>t.Text=="参数预览"),"Two-stage editor pages are inconsistent.");
            var housing=tabs.TabPages.Cast<TabPage>().Single(t=>t.Text=="箱体与端盖").Controls.OfType<DataGridView>().Single();
            var row=housing.Rows.Cast<DataGridViewRow>().Single(r=>Convert.ToString(r.Cells[1].Value)=="轴承端盖厚度");
            double initial=Convert.ToDouble(row.Cells[2].Value);row.Cells[2].Value=initial+1;
            var parse=typeof(TwoStageEditor).GetMethod("Parse",BindingFlags.NonPublic|BindingFlags.Instance);
            var edited=(Parameters)parse.Invoke(editor,null);
            Require(Math.Abs(edited.two_stage.end_cover_thickness-initial-1)<1e-8,"Structured edit was not committed.");
            Require(File.ReadAllText(args[0])==sourceText,"Editor overwrote its source file.");
            row.Cells[2].Value=-1;bool rejected=false;
            try{parse.Invoke(editor,null);}catch(TargetInvocationException ex){rejected=ex.InnerException is ArgumentException;}
            Require(rejected,"Invalid structured edit was accepted.");
        }
        var spec=p.two_stage;
        string[] files={"input_shaft","intermediate_shaft","output_shaft","high_wheel","low_pinion","low_wheel",
            "input_bearing","intermediate_bearing","output_bearing","housing","cover",
            "input_key_1","intermediate_key_1","intermediate_key_2","output_key_1","output_key_2",
            "input_front_end_cover","input_rear_end_cover","intermediate_front_end_cover","intermediate_rear_end_cover","output_front_end_cover","output_rear_end_cover"};
        var legacy=Copy(p);legacy.two_stage.hardware_version=0;
        var plan=Generator.PlanTwoStageAssembly(legacy.two_stage,files.Select(f=>f+".SLDPRT"));
        Require(plan.Count==25&&plan.Select(i=>i.name).Distinct().Count()==25,"Flat assembly must have 25 distinct component instances.");
        Require(plan.Count(i=>i.name.Contains("bearing_"))==6,"Three bearing types must be installed twice each.");
        Require(Math.Abs(plan.Single(i=>i.name=="high_wheel").x-plan.Single(i=>i.name=="input_shaft").x+89.375)<1e-7,"High-stage center distance mismatch.");
        Require(Math.Abs(plan.Single(i=>i.name=="low_wheel").x-plan.Single(i=>i.name=="intermediate_shaft").x+114)<1e-7,"Low-stage center distance mismatch.");
        Reject(p,s=>s.high_pair.pinion_teeth++,"high-stage center distance");
        Reject(p,s=>s.low_pair.wheel_start_y+=20,"axial mesh overlap");
        Reject(p,s=>s.low_pair.wheel_bore+=1,"wheel/shaft bore");
        Reject(p,s=>s.bearings.First(b=>b.name=="output").rear_y=170,"rear bearing seat");
        Reject(p,s=>s.housing.x_min=-80,"gear clearance");
        string[] hardware={"two_hex_bolt_m8x80","two_hex_bolt_m8x30","gb_hex_nut_m8","two_flat_washer_8",
            "two_socket_screw_m6x30","two_socket_screw_m5x10","two_socket_screw_m12x25","two_drain_seal_12",
            "two_locating_pin","two_inspection_plate","two_inspection_gasket"};
        var hardwarePlan=Generator.PlanTwoStageAssembly(spec,files.Concat(hardware).Select(f=>f+".SLDPRT"));
        Require(hardwarePlan.Count==103&&hardwarePlan.Count(i=>i.name.Contains("_screw_"))==32,
            "Two-stage reference hardware counts changed.");
        Require(hardwarePlan.Count(i=>i.file=="two_flat_washer_8.SLDPRT")==20,"M8 washer count differs from reference.");
        var changed=Copy(legacy);changed.two_stage.high_pair.module=1.4;
        var input=changed.two_stage.shafts.Single(s=>s.name=="input");
        var middle=changed.two_stage.shafts.Single(s=>s.name=="intermediate");
        input.sections.Single(s=>s.kind=="gear").diameter=1.4*(changed.two_stage.high_pair.pinion_teeth-2.5);
        input.center_x=middle.center_x+1.4*(changed.two_stage.high_pair.pinion_teeth+changed.two_stage.high_pair.wheel_teeth)/2;
        changed.two_stage.housing.x_max=270;
        Generator.Validate(changed);
        var updatedPlan=Generator.PlanTwoStageAssembly(changed.two_stage,files.Select(f=>f+".SLDPRT"));
        Require(Math.Abs(updatedPlan.Single(i=>i.name=="input_shaft").x-input.center_x)<1e-7,"Module edit did not update assembly position.");
        Console.WriteLine("Two-stage legacy 25-instance and hardware 103-instance plans: PASS");return 0;
    }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}
