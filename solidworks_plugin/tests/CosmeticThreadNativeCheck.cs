using System;
using System.IO;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;

class CosmeticThreadNativeCheck {
    static int Count(Feature first,bool sub=false) {
        int n=0;
        for(var feature=first;feature!=null;feature=(Feature)(sub?feature.GetNextSubFeature():feature.GetNextFeature())) {
            if(feature.GetTypeName2()=="CosmeticThread") {
                bool warning;int error=feature.GetErrorCode2(out warning);
                if(error!=0&&!warning)throw new Exception("Invalid thread "+feature.Name+" code="+error);
                n++;
            }
            var child=(Feature)feature.GetFirstSubFeature();
            if(child!=null)n+=Count(child,true);
        }
        return n;
    }
    [STAThread]static int Main(string[] args) {
        try {
            var app=(SldWorks)Marshal.GetActiveObject("SldWorks.Application");
            foreach(string argument in args) {
                int separator=argument.LastIndexOf('=');
                string file=separator>=0?argument.Substring(0,separator):argument;
                int expected=separator>=0?Int32.Parse(argument.Substring(separator+1)):1;
                int error=0,warning=0;string path=Path.GetFullPath(file);
                var doc=(ModelDoc2)app.OpenDoc6(path,1,1,"",ref error,ref warning);
                if(doc==null||error!=0)throw new Exception("Open failed: "+path+" code="+error);
                int count=Count((Feature)doc.FirstFeature());
                Console.WriteLine(Path.GetFileName(file)+" cosmetic_threads="+count+
                    " annotations_visible="+doc.Extension.GetUserPreferenceToggle(31,0)+
                    " threads_visible="+doc.Extension.GetUserPreferenceToggle(41,0)+
                    " shaded_threads="+doc.Extension.GetUserPreferenceToggle(230,0));
                app.CloseDoc(doc.GetTitle());
                if(count!=expected)throw new Exception("Expected "+expected+" cosmetic threads in "+file+", found "+count);
            }
            return 0;
        } catch(Exception e){Console.Error.WriteLine(e.GetType().Name+": "+e.Message);return 1;}
    }
}
