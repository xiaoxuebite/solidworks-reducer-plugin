using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using ReducerSW;
using SolidWorks.Interop.sldworks;

class Launcher
{
    static string ReadImport(string path, out Dictionary<string,object> project, out bool twoStage) {
        path=Path.GetFullPath(path);
        var json=new JavaScriptSerializer{MaxJsonLength=32*1024*1024,RecursionLimit=128};
        var data=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(path));
        object value;
        string format=data!=null&&data.TryGetValue("format",out value)?value as string:null;
        twoStage=format=="two_stage_reducer_design_project";
        project=null;
        if(!twoStage&&format!="reducer_design_project"&&format!="reducer_offline_project")return path;

        string reference=data.TryGetValue("model_parameters_file",out value)?value as string:null;
        if(!String.IsNullOrWhiteSpace(reference)) {
            string directory=Path.GetDirectoryName(path);
            // Keep exported pairs portable when their containing folder moves.
            string candidate=Path.Combine(directory,Path.GetFileName(reference));
            if(!File.Exists(candidate))candidate=Path.Combine(directory,reference);
            if(File.Exists(candidate)) {
                var parameters=json.Deserialize<Parameters>(File.ReadAllText(candidate));
                Generator.Validate(parameters);
                if((parameters.schema_version==5)!=twoStage)
                    throw new ArgumentException("项目关联的建模参数与减速器级数不一致。请从设计窗口重新保存建模方案。");
                return Path.GetFullPath(candidate);
            }
        }
        project=data;
        return null;
    }

    static void RunDesign(SldWorks app,bool twoStage,Dictionary<string,object> project=null) {
        using(Form wizard=twoStage?(Form)new TwoStageDesignWizard(app,project):new DesignWizard(app,project))
            Application.Run(wizard);
    }
    static string MachineAddinPath() {
        const string server=@"SOFTWARE\Classes\CLSID\{6B5C4699-06DB-4A1C-A17F-C267532598D5}\InprocServer32";
        using(var key=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(server)) {
            string codebase=key==null?null:key.GetValue("CodeBase") as string;
            if(String.IsNullOrEmpty(codebase))return null;
            Uri uri;
            return Uri.TryCreate(codebase,UriKind.Absolute,out uri)&&uri.IsFile?uri.LocalPath:codebase;
        }
    }
    static string UpdateInstruction(string expected) {
        string installed=MachineAddinPath();
        if(installed!=null&&!String.Equals(Path.GetFullPath(installed),Path.GetFullPath(expected),StringComparison.OrdinalIgnoreCase))
            return "系统注册的插件仍是旧版："+installed+System.Environment.NewLine+
                "请以管理员身份运行 install.ps1 更新系统注册，然后重启 SOLIDWORKS。";
        return "请保存文档并重启 SOLIDWORKS，然后重新加载插件。";
    }
    [STAThread]
    static int Main(string[] args)
    {
        try {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if(args.Length>0 && args[0]=="--register-user") {
                using(var installed=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\SolidWorks\AddIns\{6B5C4699-06DB-4A1C-A17F-C267532598D5}"))
                    if(installed==null)throw new Exception("请先用安装插件.cmd 完成一次管理员安装。");
                ReducerAddin.Register(typeof(ReducerAddin));
                Console.WriteLine("Current-user plugin registration updated: "+typeof(ReducerAddin).Assembly.Location);
                Console.WriteLine(UpdateInstruction(typeof(ReducerAddin).Assembly.Location));
                return 0;
            }
            if(args.Length>0 && (args[0]=="--design"||args[0]=="--two-design")) {
                // Calculations and project editing remain usable before SW opens.
                SldWorks designApp=null;
                try {designApp=(SldWorks)Marshal.GetActiveObject("SldWorks.Application");}
                catch(COMException) {}
                if(designApp!=null&&!designApp.RevisionNumber().StartsWith("32."))throw new Exception("完整设计的原生建模需要 SOLIDWORKS 2024，请切换到 2024 后重试。");
                RunDesign(designApp,args[0]=="--two-design");
                return 0;
            }
            if(args.Length>1 && args[0]=="--validate") {
                Dictionary<string,object> project;bool twoStage;
                string path=ReadImport(args[1],out project,out twoStage);
                if(project!=null)throw new ArgumentException("设计项目尚无可用的建模参数。请打开设计项目并保存建模方案。");
                var p=new JavaScriptSerializer{MaxJsonLength=32*1024*1024,RecursionLimit=128}.Deserialize<Parameters>(File.ReadAllText(path));
                Generator.Validate(p);Console.WriteLine("Parameter validation: PASS");return 0;
            }
            var app = (SldWorks)Marshal.GetActiveObject("SldWorks.Application");
            if (!app.RevisionNumber().StartsWith("32.")) throw new Exception("This build requires SOLIDWORKS 2024 (revision 32).");
            string root = Generator.ProjectRoot();
            if(args.Length>1 && args[0]=="--remate-two-stage") {
                bool addMates=args.Length<3||args[2]!="--skip-mates";
                var result=Generator.RemateTwoStage(app,args[1],addMates);
                Console.WriteLine("Two-stage assembly: "+result.instances.Count+" components; "+result.mates.Count+" native mates.");
            } else if(args.Length>1 && args[0]=="--audit-gear") {
                GearSectionAudit.Run(app,args[1]);
            } else if(args.Length>1 && args[0]=="--audit-output") {
                NativeAudit.Run(app,args[1]);
            } else if(args.Length>1 && args[0]=="--audit-interference") {
                NativeAudit.RunInterferenceOnly(app,args[1]);
            } else if(args.Length>1 && args[0]=="--show-output") {
                string folder=Path.GetFullPath(args[1]);
                string path=Path.Combine(folder,"reducer_assembly.SLDASM");
                var doc=(ModelDoc2)app.GetOpenDocumentByName(path);int error=0,warning=0;
                if(doc==null)doc=(ModelDoc2)app.OpenDoc6(path,2,1,"",ref error,ref warning);
                if(doc==null||error!=0)throw new Exception("Cannot open generated assembly: "+error);
                app.ActivateDoc3(doc.GetTitle(),false,0,ref error);doc.ShowNamedView2("",7);doc.ViewZoomtofit2();
                if(!doc.SaveBMP(Path.Combine(folder,"assembly_preview.bmp"),1400,900))throw new Exception("Cannot save assembly preview bitmap.");
                Console.WriteLine("Opened "+path);
            } else if(args.Length>2 && args[0]=="--bearings") {
                var serializer=new System.Web.Script.Serialization.JavaScriptSerializer();var specs=serializer.Deserialize<BearingSpec[]>(File.ReadAllText(args[1]));
                string output=Path.GetFullPath(args[2]);Directory.CreateDirectory(output);var metrics=new System.Collections.Generic.List<object>();
                bool prompt=app.GetUserPreferenceToggle((int)SolidWorks.Interop.swconst.swUserPreferenceToggle_e.swInputDimValOnCreate);
                app.SetUserPreferenceToggle((int)SolidWorks.Interop.swconst.swUserPreferenceToggle_e.swInputDimValOnCreate,false);
                try {Generator.MakeBearings(app,specs,output,metrics);File.WriteAllText(Path.Combine(output,"bearing_metrics.json"),serializer.Serialize(metrics));Console.WriteLine(output);}
                finally {app.SetUserPreferenceToggle((int)SolidWorks.Interop.swconst.swUserPreferenceToggle_e.swInputDimValOnCreate,prompt);}
            } else if(args.Length>1 && args[0]=="--reassemble") {
                string output=Path.GetFullPath(args[1]);var serializer=new System.Web.Script.Serialization.JavaScriptSerializer();
                string parameterFile=args.Length>2?args[2]:Path.Combine(output,"parameters.json");
                var p=serializer.Deserialize<Parameters>(File.ReadAllText(parameterFile));Generator.Validate(p);
                var files=new System.Collections.Generic.List<string>();foreach(string file in Directory.GetFiles(output,"*.SLDPRT"))if(!Path.GetFileName(file).StartsWith("~$"))files.Add(file);
                var openBefore=new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach(object item in app.GetDocuments() as object[]??new object[0]) {
                    var prior=item as ModelDoc2;if(prior!=null&&!String.IsNullOrEmpty(prior.GetPathName()))openBefore.Add(prior.GetPathName());
                }
                try {
                    var rebuilt=Generator.CreateTotalAssembly(app,p,files,output);
                    string manifestPath=Path.Combine(output,"parts_manifest.json");
                    if(File.Exists(manifestPath)) {
                        var manifest=serializer.Deserialize<System.Collections.Generic.Dictionary<string,object>>(File.ReadAllText(manifestPath));
                        manifest["instances"]=rebuilt.instances;manifest["component_count"]=rebuilt.instances.Count;manifest["top_level_mates"]=rebuilt.seal_mates;manifest["native_mates"]=rebuilt.native_mates;
                        manifest["assembly_mates_added"]=true;manifest["layout_scope"]=Generator.OneStageLayoutScope(p,true);
                        File.WriteAllText(manifestPath,serializer.Serialize(manifest));
                    }
                    Console.WriteLine(serializer.Serialize(rebuilt));
                } finally {
                    foreach(object item in app.GetDocuments() as object[]??new object[0]) {
                        var opened=item as ModelDoc2;if(opened==null)continue;
                        string path=opened.GetPathName();
                        if(String.IsNullOrEmpty(path)||!path.StartsWith(output+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||openBefore.Contains(path)||opened.GetSaveFlag())continue;
                        app.CloseDoc(opened.GetTitle());
                    }
                }
            } else if(args.Length>0 && args[0]=="--editor") {
                Dictionary<string,object> project;bool twoStage;
                string path=ReadImport(args.Length>1?args[1]:Generator.DefaultParameters(),out project,out twoStage);
                if(project!=null) {
                    RunDesign(app,twoStage,project);
                    return 0;
                }
                var source=new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Parameters>(File.ReadAllText(path));
                if(source.schema_version==5) {
                    using(var editor=new TwoStageEditor(path))if(editor.ShowDialog()==System.Windows.Forms.DialogResult.OK) {
                        string output=Generator.Generate(app,editor.SnapshotPath,Path.Combine(root,"output"),editor.AddAssemblyMates);
                        Console.WriteLine(output);
                        string mode=editor.AddAssemblyMates?"含装配配合":"零件已按计算位置固定";
                        System.Windows.Forms.MessageBox.Show("二级减速器原生零件与总装已保存（"+mode+"）。"+System.Environment.NewLine+output,"减速器生成器");
                    }
                } else using(var editor=new ParameterEditor(path)) {
                    if(editor.ShowDialog()==System.Windows.Forms.DialogResult.OK) {
                        string output=Generator.Generate(app,editor.SnapshotPath,Path.Combine(root,"output"),editor.AddAssemblyMates);
                        Console.WriteLine(output);
                        string mode=editor.AddAssemblyMates?"含装配配合":"零件已按计算位置固定";
                        System.Windows.Forms.MessageBox.Show("一级减速器原生零件与总装已保存（"+mode+"）。"+System.Environment.NewLine+output,"减速器生成器");
                    }
                }
            } else if(args.Length>0 && args[0]=="--com-check") {
                dynamic probe=Activator.CreateInstance(Type.GetTypeFromProgID("ReducerSW.Addin",true));
                Console.WriteLine("Registered build="+probe.BuildLocation());
                if(Marshal.IsComObject(probe)) Marshal.FinalReleaseComObject(probe);
            } else if(args.Length>0 && args[0]=="--upgrade-ui") {
                dynamic active=app.GetAddInObject("{6B5C4699-06DB-4A1C-A17F-C267532598D5}");
                if(active!=null) {
                    string old=active.BuildLocation();
                    int unloaded=app.UnloadAddIn(old);
                    Console.WriteLine("Unload old="+unloaded);
                    if(unloaded!=0) throw new Exception("Cannot unload the current reducer add-in.");
                }
                ReducerAddin.Register(typeof(ReducerAddin));
                string dll=typeof(ReducerAddin).Assembly.Location;
                Console.WriteLine("Load new="+app.LoadAddIn(dll));
                active=app.GetAddInObject("{6B5C4699-06DB-4A1C-A17F-C267532598D5}");
                if(active==null) throw new Exception("New plugin was not activated.");
                string current=active.BuildLocation();
                Console.WriteLine("Active="+current);
                if(!String.Equals(current,dll,StringComparison.OrdinalIgnoreCase)) throw new Exception("New UI registered. SOLIDWORKS is retaining the older DLL; save your documents and fully restart SOLIDWORKS to activate the new command tab. No reinstall is required.");
                active.RefreshCommands();
                Console.WriteLine("Command tabs rebuilt.");
            } else if (args.Length > 1 && args[0] == "--inspect-assembly") {
                var doc=(ModelDoc2)app.GetOpenDocumentByName(Path.GetFullPath(args[1]));
                if(doc==null) throw new Exception("Document is not open.");
                var components=(object[])((AssemblyDoc)doc).GetComponents(false);
                Console.WriteLine("AssemblyComponents="+components.Length);
                string manifest=Path.Combine(Path.GetDirectoryName(args[1]),"parts_manifest.json");
                int expected=2;bool flat=false;
                if(File.Exists(manifest)) {
                    var data=new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string,object>>(File.ReadAllText(manifest));
                    expected=Convert.ToInt32(data["component_count"]);flat=Convert.ToInt32(data["schema_version"])>=2;
                }
                if(components.Length!=expected) throw new Exception("Unexpected prototype component count.");
                int sealCount=0;
                foreach(Component2 component in components) {
                    Console.WriteLine(component.Name2+" fixed="+component.IsFixed()+" resolved="+(component.GetModelDoc2()!=null));
                    if(component.GetModelDoc2()==null)throw new Exception("Unresolved component.");
                    if(flat&&((ModelDoc2)component.GetModelDoc2()).GetType()!=1)throw new Exception("Nested assembly found.");
                    bool seal=flat&&Path.GetFileNameWithoutExtension(component.GetPathName()).EndsWith("_oil_seal");
                    if(seal){sealCount++;if(component.IsFixed()||component.GetConstrainedStatus()!=3)throw new Exception("Seal is not constrained by real mates.");}
                    else if(!component.IsFixed()&&((component.GetMates() as object[])??new object[0]).Length==0)throw new Exception("Floating component has no real mate.");
                }
                if(flat) {
                    int mates=0;var f=(Feature)doc.FirstFeature();
                    while(f!=null) {if(f.GetTypeName2()=="MateGroup") {var sub=(Feature)f.GetFirstSubFeature();while(sub!=null){bool warning;int code=sub.GetErrorCode2(out warning);if(code!=0&&!warning)throw new Exception("Mate rebuild error: "+sub.Name);if(sub.GetTypeName2()=="MateCoincident")mates++;sub=(Feature)sub.GetNextSubFeature();}}f=(Feature)f.GetNextFeature();}
                    Console.WriteLine("TopLevelCoincidentMates="+mates);
                    if(mates!=sealCount*3)throw new Exception("Expected three real plane mates per seal.");
                    if(((object[])((AssemblyDoc)doc).GetComponents(true)).Length!=components.Length)throw new Exception("Top level is not flat.");
                }
                int error=0; app.ActivateDoc3(doc.GetTitle(),false,0,ref error);
                doc.ShowNamedView2("",7); doc.ViewZoomtofit2();
                doc.SaveBMP(Path.ChangeExtension(args[1],".bmp"),1400,900);
            } else if (args.Length > 1 && args[0] == "--edit-check") {
                var doc=(ModelDoc2)app.GetOpenDocumentByName(Path.GetFullPath(args[1]));
                if(doc==null) throw new Exception("Document is not open.");
                var sketch=ModelPresentation.ChineseName("DrivenShaft_Revolve_Profile");
                var dim=(Dimension)doc.Parameter("Axial_1@"+sketch) ??
                    (Dimension)doc.Parameter("Axial_1@DrivenShaft_Revolve_Profile");
                if(dim==null) throw new Exception("Editable dimension missing.");
                double original=dim.SystemValue;
                double before=((MassProperty)doc.Extension.CreateMassProperty()).Volume;
                try {
                    dim.SystemValue=original+.001;
                    if(!doc.EditRebuild3()) throw new Exception("Dimension edit failed to rebuild.");
                    double after=((MassProperty)doc.Extension.CreateMassProperty()).Volume;
                    Console.WriteLine("Axial_1 changed +1 mm; volume delta mm3="+((after-before)*1e9).ToString("R",System.Globalization.CultureInfo.InvariantCulture));
                    if(Math.Abs(after-before)<1e-12) throw new Exception("Dimension did not change solid.");
                } finally { dim.SystemValue=original; if(!doc.EditRebuild3()) throw new Exception("Cannot restore dimension."); }
            } else if (args.Length > 1 && args[0] == "--inspect") {
                var doc = (ModelDoc2)app.GetOpenDocumentByName(Path.GetFullPath(args[1]));
                if (doc == null) throw new Exception("Document is not open.");
                var mass = (MassProperty)doc.Extension.CreateMassProperty();
                Console.WriteLine("Volume_mm3=" + (mass.Volume*1e9).ToString("R",System.Globalization.CultureInfo.InvariantCulture));
                var bodies = (object[])((PartDoc)doc).GetBodies2(0,false);
                Console.WriteLine("SolidBodies=" + bodies.Length);
                Feature f=(Feature)doc.FirstFeature();
                while(f!=null) {
                    bool warning=false; int featureError=f.GetErrorCode2(out warning);
                    Console.WriteLine(f.Name+" : "+f.GetTypeName2()+" error="+featureError+" warning="+warning);
                    if(f.GetTypeName2()=="ProfileFeature") Console.WriteLine("  constraintStatus="+((Sketch)f.GetSpecificFeature2()).GetConstrainedStatus());
                    if(featureError!=0 && !warning) throw new Exception("Invalid feature: "+f.Name);
                    f=(Feature)f.GetNextFeature();
                }
                int error=0;
                app.ActivateDoc3(doc.GetTitle(),false,0,ref error);
                doc.ShowNamedView2("",7); doc.ViewZoomtofit2();
                Console.WriteLine("Preview="+doc.SaveBMP(Path.ChangeExtension(args[1],".bmp"),1400,900));
            } else if (args.Length > 0 && args[0] == "--load") {
                using(var installed=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\SolidWorks\AddIns\{6B5C4699-06DB-4A1C-A17F-C267532598D5}")) {
                    if(installed==null) throw new Exception("Plugin menu requires one-time administrator installation: install.ps1. Direct model generation remains available without installation.");
                }
                const string id="{6B5C4699-06DB-4A1C-A17F-C267532598D5}";
                string dll=typeof(ReducerAddin).Assembly.Location;
                dynamic active=app.GetAddInObject(id);
                if(active!=null) {
                    string loaded;
                    try { loaded=active.BuildLocation(); }
                    catch { loaded="legacy build without BuildLocation"; }
                    Console.WriteLine("Loaded="+loaded);
                    if(String.Equals(loaded,dll,StringComparison.OrdinalIgnoreCase))return 0;
                    throw new Exception("SolidWorks 当前加载的是旧版插件。"+UpdateInstruction(dll)+" 当前构建："+dll);
                }
                ReducerAddin.Register(typeof(ReducerAddin));
                int result = app.LoadAddIn(dll);
                Console.WriteLine("LoadAddIn=" + result);
                if (result != 0 && result != 2) return 1;
                active=app.GetAddInObject(id);
                if(active==null) throw new Exception("SOLIDWORKS 未暴露已加载的插件。"+UpdateInstruction(dll));
                string current;
                try {current=active.BuildLocation();}catch {current="legacy build without BuildLocation";}
                Console.WriteLine("Loaded="+current);
                if(!String.Equals(current,dll,StringComparison.OrdinalIgnoreCase))throw new Exception("SolidWorks 保留了旧版插件 DLL。"+UpdateInstruction(dll)+" 目标构建："+dll);
            } else {
                string parameters = args.Length > 0 ? Path.GetFullPath(args[0]) : Generator.DefaultParameters();
                Console.WriteLine(Generator.Generate(app, parameters, Path.Combine(root, "output")));
            }
            return 0;
        } catch (OperationCanceledException ex) {
            Console.WriteLine(ex.Message);
            if(args.Length>0&&args[0]=="--editor") {
                MessageBox.Show(ex.Message,"减速器建模");
                return 0;
            }
            return 2;
        } catch (Exception ex) {
            Console.Error.WriteLine(ex.ToString());
            if(args.Length>0&&(args[0]=="--design"||args[0]=="--two-design"||args[0]=="--editor"))System.Windows.Forms.MessageBox.Show(ex.Message,"减速器设计",System.Windows.Forms.MessageBoxButtons.OK,System.Windows.Forms.MessageBoxIcon.Error);
            return 1;
        }
    }
}
