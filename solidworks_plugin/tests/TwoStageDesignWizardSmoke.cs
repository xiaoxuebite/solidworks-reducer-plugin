using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Windows.Forms;
using ReducerSW;

class TwoStageDesignWizardSmoke {
    static readonly BindingFlags Private=BindingFlags.NonPublic|BindingFlags.Instance;
    static object Field(object target,string name){return target.GetType().GetField(name,Private).GetValue(target);}
    static object Call(object target,string name,params object[] arguments){return target.GetType().GetMethod(name,Private).Invoke(target,arguments);}
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
    [STAThread] static int Main(){try{
        Application.EnableVisualStyles();
        using(var wizard=new TwoStageDesignWizard(null)){
            var navigation=(ListBox)Field(wizard,"navigation");
            Check(navigation.Items.Count==8,"Two-stage design must have eight steps.");
            var fields=(IDictionary)Field(wizard,"fields");
            foreach(string key in new[]{"input_power_kw","input_speed_rpm","ratio_distribution_factor","high_pinion_teeth","low_wheel_teeth","high_module","low_module","shaft_allow_mpa","key_allow_mpa"})
                Check(fields.Contains(key),"Missing input: "+key);
            var request=(Dictionary<string,object>)Call(wizard,"Request","two_calculate");
            var response=DesignBridge.Call(request);
            var design=(Dictionary<string,object>)response["design"];
            Call(wizard,"ApplyDesign",design,false);
            Check(!(bool)Field(wizard,"dirty"),"Calculated design should be fresh.");
            Check(((DataGridView)Field(wizard,"shaftResults")).Rows.Count==3,"Three shaft results missing.");
            Check(((DataGridView)Field(wizard,"checks")).Rows.Count>20,"Gear/shaft/bearing checks missing.");
            Check(!String.IsNullOrWhiteSpace(((TextBox)Field(wizard,"steps")).Text),"Calculation process missing.");
            Check(((Button)Field(wizard,"generateButton")).Enabled,"Valid design should enable model generation.");
            var field=fields["high_wheel_teeth"];
            ((DataGridViewCell)field.GetType().GetField("Cell").GetValue(field)).Value=112;
            Check((bool)Field(wizard,"dirty"),"Edited tooth count must invalidate old checks.");
            Check(!((Button)Field(wizard,"generateButton")).Enabled,"Stale design must not generate CAD.");
            bool rejected=false;try{Call(wizard,"RequireCurrent");}catch(TargetInvocationException error){rejected=error.InnerException is InvalidOperationException;}
            Check(rejected,"Stale design must be rejected.");
        }
        Console.WriteLine("Two-stage wizard eight steps, three-shaft results and stale-model guard: PASS");return 0;
    }catch(Exception error){Console.Error.WriteLine(error);return 1;}}
}
