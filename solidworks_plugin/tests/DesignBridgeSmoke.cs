using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using ReducerSW;

class DesignBridgeSmoke {
    static Dictionary<string,object> Map(object value){return (Dictionary<string,object>)value;}
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
    static Dictionary<string,object> Run(string action,Dictionary<string,object> design,string output){
        var request=new Dictionary<string,object>{{"action",action},{"project",new Dictionary<string,object>{{"name","完整设计集成验证"},{"designer","回归测试"}}}};
        if(design!=null)request["design"]=design;
        if(output!=null)request["output_dir"]=output;
        return DesignBridge.Call(request);
    }
    static int Main(string[] args){
        try {
            string output=args.Length>0?Path.GetFullPath(args[0]):Path.Combine(Generator.ProjectRoot(),".bridge","integration_"+DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            var schema=Run("schema",null,null);Check(Map(schema["schema"]).ContainsKey("bridge"),"Missing structural schema.");
            var request=new Dictionary<string,object>{{"action","auto"},{"parameters",new Dictionary<string,object>{{"P",2.0},{"n1",1440.0},{"target_speed",300.0}}}};
            var automatic=Map(DesignBridge.Call(request)["design"]);Check(Object.Equals(automatic["system_checks_passed"],true),"Automatic design failed checks.");
            var calculated=Map(Run("calculate",automatic,null)["design"]);
            foreach(string key in new[]{"highShaftStress","shaftStress","highBearingLife","lowBearingLife"}) {
                double before=Convert.ToDouble(Map(automatic["results"])[key]),after=Convert.ToDouble(Map(calculated["results"])[key]);
                Check(Math.Abs(before-after)<=Math.Max(1,Math.Abs(before))*1e-10,"Recalculation drifted: "+key);
            }
            var exported=Run("export",calculated,output);string parameterFile=Convert.ToString(exported["model_parameters_file"]);
            var json=new JavaScriptSerializer{MaxJsonLength=Int32.MaxValue};
            var p=json.Deserialize<Parameters>(File.ReadAllText(parameterFile));Generator.Validate(p);
            Check(p.shaft_segments.Length==6&&p.gears.high_sections.Length==8,"Incomplete shaft chain.");
            var reported=Run("report",Map(exported["design"]),output);
            Check(File.Exists(Convert.ToString(reported["report_file"]))&&File.Exists(Convert.ToString(reported["project_file"])),"Missing saved project/report.");
            Console.WriteLine("Design bridge real Python schema -> auto -> calculate -> native validation -> report: PASS");
            Console.WriteLine("Output="+output);return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
}
