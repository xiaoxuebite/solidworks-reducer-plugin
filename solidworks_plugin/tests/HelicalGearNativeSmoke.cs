using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using ReducerSW;
using SolidWorks.Interop.sldworks;

class HelicalGearNativeSmoke {
    static object Call(string name,params object[] args) {
        var method=typeof(Generator).GetMethod(name,BindingFlags.NonPublic|BindingFlags.Static);
        if(method==null)throw new Exception("Missing method "+name);
        try{return method.Invoke(null,args);}catch(TargetInvocationException e){throw e.InnerException;}
    }
    [STAThread]static int Main(string[] args) {
        var app=(SldWorks)Marshal.GetActiveObject("SldWorks.Application");
        var doc=(ModelDoc2)Call("NewDocument",app,1);
        try {
            bool large=args.Length>1&&args[1]=="large";
            var spec=new GearTrainSpec{module=large?1.5:2,pressure_angle=20,helix_angle=15};
            int teeth=large?114:20;double width=large?57:10;
            bool shaft=args.Length>1&&args[1]=="shaft";
            if(shaft) {
                double root=spec.module*(teeth/Math.Cos(Math.PI/12)-2.5)/2;
                Call("Revolve",doc,new double[][]{new[]{0.0,0.0},new[]{0.0,root},new[]{30.0,root},new[]{30.0,0.0}},"ShaftCore",false);
            }
            Call("AddGearBody",app,doc,spec,teeth,width,shaft?10.0:0.0,0.0,shaft,"HelicalSmoke");
            var bodies=((PartDoc)doc).GetBodies2(0,false) as object[];
            if(bodies==null||bodies.Length!=1)throw new Exception("Expected one helical solid body");
            bool sweep=false,pattern=false;
            for(var f=(Feature)doc.FirstFeature();f!=null;f=(Feature)f.GetNextFeature()) {
                Console.WriteLine(f.Name+" "+f.GetTypeName2());
                if(f.Name=="HelicalSmoke_TwistedTooth") {
                    sweep=true;
                    var data=f.GetDefinition() as SweepFeatureData;
                    double expected=width*Math.Tan(Math.PI/12)/(spec.module*teeth/(2*Math.Cos(Math.PI/12)));
                    if(data==null||Math.Abs(data.GetTwistAngle()-expected)>1e-6)
                        throw new Exception("Native sweep twist does not match helix angle");
                }
                if(f.Name=="GearToothPattern")pattern=true;
            }
            if(!sweep||!pattern)throw new Exception("Missing twisted sweep or tooth pattern");
            string path=Path.GetFullPath(args.Length>0?args[0]:"helical_smoke.SLDPRT");
            Call("Save",doc,path);
            var appearance=doc.MaterialPropertyValues as double[];
            if(appearance==null||appearance.Length!=9||appearance[0]<=appearance[1])
                throw new Exception("Gear appearance was not assigned");
            bool chineseSweep=false,chinesePattern=false,chineseAxis=false;
            for(var f=(Feature)doc.FirstFeature();f!=null;f=(Feature)f.GetNextFeature()) {
                if(f.GetTypeName2()=="Sweep"&&f.Name.Contains("扭转"))chineseSweep=true;
                if(f.GetTypeName2()=="CirPattern"&&f.Name.Contains("阵列"))chinesePattern=true;
                if(f.GetTypeName2()=="RefAxis"&&f.Name=="齿轮旋转轴线")chineseAxis=true;
            }
            if(!chineseSweep||!chinesePattern||!chineseAxis)throw new Exception("Chinese feature names were not saved");
            doc.ShowNamedView2("",7);doc.ViewZoomtofit2();
            if(!doc.SaveBMP(Path.ChangeExtension(path,".bmp"),1200,900))
                throw new Exception("Helical preview could not be saved");
            Console.WriteLine("Saved "+path);
            return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
        finally{app.CloseDoc(doc.GetTitle());}
    }
}
