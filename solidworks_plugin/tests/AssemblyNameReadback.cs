using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using ReducerSW;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

class AssemblyNameReadback {
    [STAThread] static int Main(string[] args) {
        var app=(SldWorks)Marshal.GetActiveObject("SldWorks.Application");
        string path=Path.GetFullPath(args[0]);int error=0,warning=0;
        int setting=(int)swUserPreferenceToggle_e.swExtRefUpdateCompNames;
        bool previous=app.GetUserPreferenceToggle(setting);app.SetUserPreferenceToggle(setting,false);
        var doc=(ModelDoc2)app.OpenDoc6(path,2,1,"",ref error,ref warning);
        if(doc==null||error!=0)throw new Exception("Open failed: "+error);
        try {
            var components=((AssemblyDoc)doc).GetComponents(true) as object[]??new object[0];
            var names=components.Cast<Component2>().Select(c=>c.Name2).ToArray();
            Console.WriteLine("components="+names.Length+" first="+names[0]+" Chinese="+names.Count(n=>n.Any(ch=>ch>127)));
            if(names.Any(n=>!n.Any(ch=>ch>127)))throw new Exception("Some assembly labels reverted to English");
            Feature gear=null;
            for(var feature=(Feature)doc.FirstFeature();feature!=null;feature=(Feature)feature.GetNextFeature()) {
                if(feature.Name=="TS_one_stage_gear_ratio"){gear=feature;break;}
                for(var child=(Feature)feature.GetFirstSubFeature();child!=null;child=(Feature)child.GetNextSubFeature())
                    if(child.Name=="TS_one_stage_gear_ratio"){gear=child;break;}
                if(gear!=null)break;
            }
            if(gear!=null) {
                var mate=gear.GetSpecificFeature2() as Mate2;
                if(mate==null||mate.GetMateEntityCount()!=2||
                    !mate.MateEntity(0).ReferenceComponent.Name2.Any(ch=>ch>127)||
                    !mate.MateEntity(1).ReferenceComponent.Name2.Any(ch=>ch>127))
                    throw new Exception("Gear mate lost its Chinese-named component references");
                Console.WriteLine("gear="+mate.MateEntity(0).ReferenceComponent.Name2+" / "+mate.MateEntity(1).ReferenceComponent.Name2);
            }
            return 0;
        } catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
        finally {app.CloseDoc(doc.GetTitle());if(args.Length<2||args[1]!="keep")app.SetUserPreferenceToggle(setting,previous);}
    }
}
