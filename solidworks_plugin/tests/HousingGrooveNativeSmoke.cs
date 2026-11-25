using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using ReducerSW;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

class HousingGrooveNativeSmoke {
    static ModelDoc2 Open(SldWorks app,string path) {
        int error=0,warning=0;
        var doc=(ModelDoc2)app.OpenDoc6(path,1,3,"",ref error,ref warning);
        if(doc==null||error!=0)throw new Exception("Cannot open native housing: "+path+" error="+error);
        return doc;
    }
    static double Difference(Body2 first,Body2 second) {
        int error;var copies=((Body2)first.Copy()).Operations2((int)swBodyOperationType_e.SWBODYCUT,
            (Body2)second.Copy(),out error) as object[];
        if(error!=(int)swBodyOperationError_e.swBodyOperationNoError&&
           error!=(int)swBodyOperationError_e.swBodyOperationEmptyBody)
            throw new Exception("Boolean difference failed: "+error);
        return (copies??new object[0]).Cast<Body2>().Sum(b=>((double[])b.GetMassProperties(1))[3])*1e9;
    }
    [STAThread]static int Main(string[] args) {
        try {
            var app=(SldWorks)Marshal.GetActiveObject("SldWorks.Application");
            var p=new JavaScriptSerializer{MaxJsonLength=16*1024*1024}.Deserialize<Parameters>(File.ReadAllText(args[0]));
            string folder=Path.GetFullPath(args[1]);Directory.CreateDirectory(folder);
            var method=typeof(Generator).GetMethod("HPart",BindingFlags.Static|BindingFlags.NonPublic);
            var original=p.housing.lower;var optimized=Generator.OptimizeHousing(p.housing).lower;
            if(original.Length!=40||optimized.Length!=30)throw new Exception("Unexpected recipe counts.");
            var metrics=new List<object>();
            string before=(string)method.Invoke(null,new object[]{app,original,"before_housing",folder,metrics,p.housing.expected_lower_bounds});
            string after=(string)method.Invoke(null,new object[]{app,optimized,"after_housing",folder,metrics,p.housing.expected_lower_bounds});
            ModelDoc2 a=null,b=null;
            try {
                a=Open(app,before);b=Open(app,after);
                var first=a.FirstFeature() as Feature;var second=b.FirstFeature() as Feature;
                if(first==null||second==null)throw new Exception("Feature tree missing.");
                var names=new List<string>();
                for(var f=second;f!=null;f=f.GetNextFeature() as Feature)names.Add(f.Name);
                if(names.Any(n=>n.StartsWith("Plane_",StringComparison.Ordinal))||
                   !names.Any(n=>n.StartsWith("建模基准面",StringComparison.Ordinal))||
                   !names.Any(n=>n.Contains("侧油槽主体")))throw new Exception("Feature tree was not named in Chinese.");
                var aa=((PartDoc)a).GetBodies2(0,false) as object[];
                var bb=((PartDoc)b).GetBodies2(0,false) as object[];
                if(aa==null||bb==null||aa.Length!=1||bb.Length!=1)throw new Exception("Expected one body per housing.");
                double forward=Difference((Body2)aa[0],(Body2)bb[0]);
                double backward=Difference((Body2)bb[0],(Body2)aa[0]);
                if(forward>1e-4||backward>1e-4)throw new Exception("Housing shape changed: "+forward+" / "+backward+" mm3");
                Console.WriteLine("Native oil groove equivalence: PASS, 40 -> 30 features; difference "+
                    forward.ToString("R",CultureInfo.InvariantCulture)+" / "+backward.ToString("R",CultureInfo.InvariantCulture)+" mm3");
                if(args.Length>2) {
                    ModelDoc2 old=null;
                    try {
                        old=Open(app,Path.GetFullPath(args[2]));
                        var oldBodies=((PartDoc)old).GetBodies2(0,false) as object[];
                        Console.WriteLine("Old housing versus revised groove: "+
                            Difference((Body2)oldBodies[0],(Body2)aa[0]).ToString("R",CultureInfo.InvariantCulture)+" / "+
                            Difference((Body2)aa[0],(Body2)oldBodies[0]).ToString("R",CultureInfo.InvariantCulture)+" mm3");
                    } finally {if(old!=null)app.CloseDoc(old.GetTitle());}
                }
                return 0;
            } finally {if(b!=null)app.CloseDoc(b.GetTitle());if(a!=null)app.CloseDoc(a.GetTitle());}
        } catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
