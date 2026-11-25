using System;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;
class ComProbe {
    [STAThread] static int Main(string[] args) {
        try {
            if(args.Length>0 && args[0]=="--sw-addins") {
                var sw=(SldWorks)Marshal.GetActiveObject("SldWorks.Application");
                sw.RunCommand(1073,""); return 0;
            }
            if(args.Length>0 && args[0]=="--cancel-test-dimension") {
                var sw=(SldWorks)Marshal.GetActiveObject("SldWorks.Application");
                sw.SetUserPreferenceToggle(10,false);
                Console.WriteLine(sw.RunCommand(1037,""));return 0;
            }
            if(args.Length>0 && args[0]=="--restore-test-dimension-prompt") {
                var sw=(SldWorks)Marshal.GetActiveObject("SldWorks.Application");sw.SetUserPreferenceToggle(10,true);return 0;
            }
            dynamic obj=Activator.CreateInstance(Type.GetTypeFromProgID("ReducerSW.Addin",true));
            Console.WriteLine("COM activation OK: "+obj.BuildLocation()); return 0;
        } catch(Exception ex) { Console.WriteLine(ex.ToString()); return 1; }
    }
}
