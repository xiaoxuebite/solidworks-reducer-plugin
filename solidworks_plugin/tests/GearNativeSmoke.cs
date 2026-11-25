using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using ReducerSW;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;
class GearNativeSmoke {
    [STAThread] static int Main(string[] args) {
        SldWorks app=null;bool prompt=false;
        try {
            app=(SldWorks)Marshal.GetActiveObject("SldWorks.Application");
            prompt=app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate);
            app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate,false);
            string root=Path.GetFullPath(args[0]);
            string folder=Path.Combine(root,"gear_smoke_"+DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));Directory.CreateDirectory(folder);
            var spec=new GearTrainSpec{module=2,pressure_angle=20,pinion_teeth=17,driven_teeth=42,driven_width=24,driven_bore=28,driven_key_width=6,driven_key_depth=3.5,driven_mesh_phase_deg=-360.0/42/4,
                high_sections=new[]{new GearShaftSection{kind="cylinder",diameter=12,length=37},new GearShaftSection{kind="cylinder",diameter=15,length=14},new GearShaftSection{kind="cylinder",diameter=14,length=2},new GearShaftSection{kind="cylinder",diameter=19,length=6},new GearShaftSection{kind="gear",diameter=29,length=32},new GearShaftSection{kind="cylinder",diameter=19,length=6},new GearShaftSection{kind="cylinder",diameter=14,length=2},new GearShaftSection{kind="cylinder",diameter=15,length=13}},
                high_keyway=new Keyway{segment_index=0,diameter=12,start=4,length=18,width=4,depth=2.5}};
            var metrics=new List<object>();Generator.MakeGearTrain(app,spec,folder,metrics);
            File.WriteAllText(Path.Combine(folder,"gear_smoke_metrics.json"),new JavaScriptSerializer().Serialize(metrics));
            Console.WriteLine(folder);return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
        finally{if(app!=null)app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate,prompt);}
    }
}
