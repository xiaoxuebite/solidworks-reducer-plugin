using System;
using System.IO;
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
            string location = obj.BuildLocation();
            if (args.Length > 0 && !String.Equals(Path.GetFullPath(args[0]), Path.GetFullPath(location), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("COM loaded an unexpected add-in: " + location);
            Console.WriteLine("COM activation OK: " + location); return 0;
        } catch(Exception ex) { Console.WriteLine(ex.ToString()); return 1; }
    }
}
