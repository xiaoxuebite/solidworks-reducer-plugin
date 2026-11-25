using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using ReducerSW;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

class HousingNativeOptimizationSmoke {
    static ModelDoc2 Open(SldWorks app,string path) {
        int error=0,warning=0;var doc=(ModelDoc2)app.OpenDoc6(path,1,3,"",ref error,ref warning);
        if(doc==null||error!=0)throw new Exception("Native test open failed: "+path+" code="+error);return doc;
    }
    static Body2 Body(ModelDoc2 doc) {var b=(object[])((PartDoc)doc).GetBodies2(0,false);if(b==null||b.Length!=1)throw new Exception("Expected one solid.");return (Body2)b[0];}
    static double Volume(ModelDoc2 doc) {doc.ClearSelection2(true);int status;var p=(double[])doc.Extension.GetMassProperties2(2,out status,false);if(status!=0)throw new Exception("Volume calculation failed.");return p[3]*1e9;}
    static double Difference(Body2 first,Body2 second) {
        var target=(Body2)first.Copy();var tool=(Body2)second.Copy();int error;
        var pieces=target.Operations2((int)swBodyOperationType_e.SWBODYCUT,tool,out error) as object[];
        if(error!=(int)swBodyOperationError_e.swBodyOperationNoError&&error!=(int)swBodyOperationError_e.swBodyOperationEmptyBody)throw new Exception("Native Boolean difference failed: "+error);
        double total=0;foreach(Body2 b in pieces??new object[0])total+=((double[])b.GetMassProperties(1))[3]*1e9;return total;
    }
    static int Features(ModelDoc2 doc) {
        int count=0;for(var f=(Feature)doc.FirstFeature();f!=null;f=(Feature)f.GetNextFeature()) {
            bool warning;int error=f.GetErrorCode2(out warning);if(error!=0&&!warning)throw new Exception("Feature error: "+f.Name);
            string type=f.GetTypeName2();if(type=="Extrusion"||type=="ICE"||type=="Boss"||type=="Cut"||type=="RevCut"||type=="Revolution")count++;
        }
        return count;
    }
    [STAThread]static int Main(string[] args) {
        var app=(SldWorks)Marshal.GetActiveObject("SldWorks.Application");var json=new JavaScriptSerializer();
        string referenceFolder=Path.GetFullPath(args[0]),folder=Path.GetFullPath(args[1]);Directory.CreateDirectory(folder);
        var parameters=json.Deserialize<Parameters>(File.ReadAllText(Path.Combine(referenceFolder,"parameters.json")));
        var optimized=Generator.OptimizeHousing(parameters.housing);var report=new List<object>();
        bool prompt=app.GetUserPreferenceToggle(10);app.SetUserPreferenceToggle(10,false);
        try {
            foreach(string name in new[]{"housing","cover"}) {
                string reference=Path.Combine(folder,"reference_"+name+".SLDPRT");File.Copy(Path.Combine(referenceFolder,name+".SLDPRT"),reference,false);
                var features=name=="housing"?optimized.lower:optimized.upper;var bounds=name=="housing"?optimized.expected_lower_bounds:optimized.expected_upper_bounds;
                var clock=Stopwatch.StartNew();var metrics=new List<object>();
                string output=(string)typeof(Generator).GetMethod("HPart",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{app,features,"optimized_"+name,folder,metrics,bounds});clock.Stop();
                ModelDoc2 before=null,after=null;
                try {
                    before=Open(app,reference);after=Open(app,output);var oldBody=Body(before);var newBody=Body(after);
                    double oldVolume=Volume(before),newVolume=Volume(after);
                    double forward=Difference(oldBody,newBody),backward=Difference(newBody,oldBody);
                    var oldBounds=(double[])oldBody.GetBodyBox();var newBounds=(double[])newBody.GetBodyBox();double boxDelta=oldBounds.Select((v,n)=>Math.Abs(v-newBounds[n])*1000).Max();
                    int oldCount=Features(before),newCount=Features(after);
                    report.Add(new{name,passed=forward<=1e-4&&backward<=1e-4&&boxDelta<1e-5,original_recipe_features=name=="housing"?parameters.housing.lower.Length:parameters.housing.upper.Length,optimized_recipe_features=features.Length,
                        original_native_features=oldCount,optimized_native_features=newCount,original_volume_mm3=oldVolume,optimized_volume_mm3=newVolume,volume_delta_mm3=newVolume-oldVolume,
                        old_minus_new_volume_mm3=forward,new_minus_old_volume_mm3=backward,max_bbox_delta_mm=boxDelta,optimized_build_seconds=clock.Elapsed.TotalSeconds,file=output});
                    File.WriteAllText(Path.Combine(folder,"native_housing_optimization.json"),json.Serialize(report));
                    if(forward>1e-4||backward>1e-4||boxDelta>=1e-5)throw new Exception("Native housing geometry changed: "+name);
                    Console.WriteLine(name+": native geometry PASS, recipe "+(name=="housing"?parameters.housing.lower.Length:parameters.housing.upper.Length)+" -> "+features.Length+"; difference "+forward.ToString("R",CultureInfo.InvariantCulture)+" / "+backward.ToString("R",CultureInfo.InvariantCulture)+" mm3; "+clock.Elapsed.TotalSeconds.ToString("0.00")+"s");
                } finally {if(after!=null)app.CloseDoc(after.GetTitle());if(before!=null)app.CloseDoc(before.GetTitle());}
            }
            return 0;
        } catch(Exception e){Console.Error.WriteLine(e);return 1;}
        finally {app.SetUserPreferenceToggle(10,prompt);}
    }
}
