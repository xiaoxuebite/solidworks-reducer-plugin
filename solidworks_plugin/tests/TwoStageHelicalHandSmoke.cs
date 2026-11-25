using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using ReducerSW;
using SolidWorks.Interop.sldworks;

class TwoStageHelicalHandSmoke {
    [STAThread]static int Main(string[] args) {
        try {
            if(args.Length!=2)throw new ArgumentException("parameters.json output-directory");
            var spec=new JavaScriptSerializer().Deserialize<Parameters>(File.ReadAllText(args[0])).two_stage;
            Generator.ValidateTwoStage(spec);
            string output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
            int inputDirection=Array.Find(spec.shafts,s=>s.name=="input").axis_direction;
            double expected=-spec.high_pair.helix_angle*inputDirection;
            var app=(SldWorks)Marshal.GetActiveObject("SldWorks.Application");
            var method=typeof(Generator).GetMethod("TMakeGear",BindingFlags.NonPublic|BindingFlags.Static);
            try {
                var metrics=new List<object>();
                method.Invoke(null,new object[]{app,"high_wheel",spec.high_pair,false,expected,output,metrics});
                string path=Path.Combine(output,"high_wheel.SLDPRT");
                if(!File.Exists(path)||metrics.Count!=1)throw new Exception("High wheel was not saved as one solid");
                File.WriteAllText(Path.Combine(output,"metrics.json"),new JavaScriptSerializer().Serialize(metrics));
                Console.WriteLine("High wheel local helix="+expected+"°, path="+path);
                return 0;
            } catch(TargetInvocationException ex){throw ex.InnerException;}
        } catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
