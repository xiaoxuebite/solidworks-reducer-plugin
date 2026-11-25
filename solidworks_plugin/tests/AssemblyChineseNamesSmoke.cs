using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using ReducerSW;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

class AssemblyChineseNamesSmoke {
    [STAThread] static int Main(string[] args) {
        var app=(SldWorks)Marshal.GetActiveObject("SldWorks.Application");
        var folder=Path.GetFullPath(args[0]);var path=Path.Combine(folder,"reducer_assembly.SLDASM");
        int error=0,warning=0;var doc=(ModelDoc2)app.OpenDoc6(path,2,0,"",ref error,ref warning);
        if(doc==null||error!=0)throw new Exception("Cannot open test assembly: "+error);
        bool preference=app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swExtRefUpdateCompNames);
        try {
            var json=new JavaScriptSerializer{MaxJsonLength=Int32.MaxValue};
            var manifestPath=Path.Combine(folder,"parts_manifest.json");
            var manifest=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(manifestPath));
            var parameters=json.Deserialize<Parameters>(File.ReadAllText(Path.Combine(folder,"parameters.json")));
            var plan=Generator.PlanAssembly(parameters,Directory.GetFiles(folder,"*.SLDPRT")
                .Where(file=>!Path.GetFileName(file).StartsWith("~$",StringComparison.Ordinal)));
            var components=((AssemblyDoc)doc).GetComponents(true) as object[]??new object[0];
            var byName=components.Cast<Component2>().ToDictionary(c=>c.Name2,c=>c);
            var mapped=new Dictionary<string,Component2>();var used=new HashSet<Component2>();
            foreach(var item in plan) {
                Component2 component;
                if(!byName.TryGetValue(item.name,out component))
                    component=components.Cast<Component2>().FirstOrDefault(c=>
                        !used.Contains(c)&&String.Equals(Path.GetFileName(c.GetPathName()),item.file,StringComparison.OrdinalIgnoreCase));
                if(component==null)throw new Exception("Missing component "+item.name);
                mapped.Add(item.name,component);used.Add(component);
            }
            int namingPreference=(int)swUserPreferenceToggle_e.swExtRefUpdateCompNames;
            app.SetUserPreferenceToggle(namingPreference,false);
            try {
                ModelPresentation.NameAssemblyComponents(app,doc,plan,mapped,null);
                if(plan.Any(i=>i.name==Path.GetFileNameWithoutExtension(i.file)+"-1"))
                    throw new Exception("English component labels remain");
                if(!doc.EditRebuild3())throw new Exception("Renamed assembly did not rebuild");
                if(!doc.Extension.SaveAs(path,0,1,null,ref error,ref warning)||error!=0)
                    throw new Exception("Saving renamed assembly failed: "+error);
            } finally {app.SetUserPreferenceToggle(namingPreference,preference);}
            manifest["instances"]=plan;File.WriteAllText(manifestPath,json.Serialize(manifest));
            Console.WriteLine("Renamed "+plan.Count+" assembly components; first="+plan[0].name);
            return 0;
        } catch(Exception ex) {Console.Error.WriteLine(ex);return 1;}
        finally {app.CloseDoc(doc.GetTitle());}
    }
}
