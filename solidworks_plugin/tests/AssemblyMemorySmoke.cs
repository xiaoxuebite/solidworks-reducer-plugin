using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using ReducerSW;
using SolidWorks.Interop.sldworks;

class AssemblyMemorySmoke {
    static object Call(string name,params object[] args) {
        var method=typeof(Generator).GetMethod(name,BindingFlags.NonPublic|BindingFlags.Static);
        if(method==null)throw new Exception("Missing method: "+name);
        try{return method.Invoke(null,args);}catch(TargetInvocationException e){throw e.InnerException;}
    }
    [STAThread]static int Main(string[] args) {
        try {
            if(args.Length!=2)throw new ArgumentException("parts-folder output-folder");
            string parts=Path.GetFullPath(args[0]),output=Path.GetFullPath(args[1]);
            Directory.CreateDirectory(output);
            var app=(SldWorks)Marshal.GetActiveObject("SldWorks.Application");
            var doc=(ModelDoc2)Call("NewDocument",app,2);
            try {
                var assembly=(AssemblyDoc)doc;
                string housing=Path.Combine(parts,"housing.SLDPRT");
                string bolt=Path.Combine(parts,"two_hex_bolt_m8x80.SLDPRT");
                foreach(var item in new[]{new{file=housing,name="housing",x=0.0},
                    new{file=bolt,name="bolt1",x=100.0},new{file=bolt,name="bolt2",x=120.0}}) {
                    var component=(Component2)Call("InsertSavedPart",app,doc,assembly,item.file,item.x,0.0,0.0,item.name);
                    if(component.GetModelDoc2()==null||!component.IsLoaded())
                        throw new Exception("Component unloaded after source window closed: "+item.name);
                    Console.WriteLine("Inserted "+item.name+" and closed source window");
                }
                int count=(assembly.GetComponents(true) as object[]??new object[0]).Length;
                if(count!=3)throw new Exception("Expected three components, got "+count);
                string path=Path.Combine(output,"assembly_memory_smoke.SLDASM");
                int error=0,warning=0;
                if(!doc.Extension.SaveAs(path,0,1,null,ref error,ref warning)||error!=0)
                    throw new Exception("Assembly save failed: "+error);
                Console.WriteLine("Saved "+path+" components="+count);
            } finally {app.CloseDoc(doc.GetTitle());}
            return 0;
        } catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
