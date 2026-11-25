using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swpublished;
using SolidWorks.Interop.swconst;

[assembly: ComVisible(false)]

namespace ReducerSW {
    [ComVisible(true), Guid("6B5C4699-06DB-4A1C-A17F-C267532598D5"), ProgId("ReducerSW.Addin"), ClassInterface(ClassInterfaceType.AutoDispatch)]
    public class ReducerAddin : ISwAddin {
        SldWorks app;
        ICommandManager manager;
        CommandGroup commandGroup;
        Process designWindow;
        public bool ConnectToSW(object ThisSW, int cookie) {
            File.AppendAllText(Path.Combine(Generator.ProjectRoot(),"plugin-load.log"),DateTime.Now+" ConnectToSW "+BuildLocation()+System.Environment.NewLine);
            app = (SldWorks)ThisSW;
            app.SetAddinCallbackInfo2(0, this, cookie);
            manager = app.GetCommandManager(cookie);
            RefreshCommands();
            return true;
        }
        public void RefreshCommands() {
            const string title="减速器生成器";
            foreach(int type in new[]{1,2,3}) {
                var old=manager.GetCommandTab(type,title);
                if(old!=null) manager.RemoveCommandTab(old);
            }
            manager.RemoveCommandGroup(240126);
            manager.RemoveCommandGroup(240127);
            manager.RemoveCommandGroup(240128);
            int error=0;
            commandGroup=manager.CreateCommandGroup2(240128,title,"一级与二级减速器设计和建模",title,-1,true,ref error);
            if(commandGroup==null || error!=(int)swCreateCommandGroupErrors.swCreateCommandGroup_Success) throw new Exception("CreateCommandGroup2 failed: "+error);
            string[] icons,mainIcons;
            ToolbarIcons.Write(Path.Combine(Path.GetDirectoryName(BuildLocation()),"icons"),out icons,out mainIcons);
            commandGroup.IconList=icons; commandGroup.MainIconList=mainIcons;
            int design=commandGroup.AddCommandItem2("一级减速器设计",-1,"从工况、齿轮、轴系设计到校核、说明书和总装","一级减速器设计",(int)ToolbarIcons.Command.OneStageDesign,"Design","",1,3);
            int editor=commandGroup.AddCommandItem2("一级减速器建模",-1,"编辑一级减速器参数并生成零件和总装","一级减速器建模",(int)ToolbarIcons.Command.OneStageModel,"EditParameters","",2,3);
            int twoStageDesign=commandGroup.AddCommandItem2("二级减速器设计",-1,"从输入工况到两级齿轮、三轴校核、说明书和总装","二级减速器设计",(int)ToolbarIcons.Command.TwoStageDesign,"TwoStageDesign","",3,3);
            int twoStage=commandGroup.AddCommandItem2("二级减速器建模",-1,"编辑二级减速器参数并生成零件和总装","二级减速器建模",(int)ToolbarIcons.Command.TwoStageModel,"TwoStage","",4,3);
            int import=commandGroup.AddCommandItem2("导入参数生成",-1,"打开建模参数或设计项目","导入参数生成",(int)ToolbarIcons.Command.Import,"Generate","",5,3);
            if(design<0||editor<0||import<0||twoStage<0||twoStageDesign<0) throw new Exception("AddCommandItem2 failed.");
            commandGroup.HasMenu=true; commandGroup.HasToolbar=true;
            if(!commandGroup.Activate()) throw new Exception("Command group activation failed.");
            int[] commands={commandGroup.get_CommandID(design),commandGroup.get_CommandID(editor),commandGroup.get_CommandID(twoStageDesign),commandGroup.get_CommandID(twoStage),commandGroup.get_CommandID(import)};
            foreach(int type in new[]{1,2,3}) {
                var tab=manager.AddCommandTab(type,title);
                if(tab==null) throw new Exception("Cannot create command tab: "+type);
                var box=tab.AddCommandTabBox();
                if(!box.AddCommands(commands,new[]{2,2,2,2,2})) throw new Exception("Cannot add command tab button.");
                tab.Visible=true;
                if(app.ActiveDoc!=null && ((ModelDoc2)app.ActiveDoc).GetType()==type) tab.Active=true;
            }
            File.AppendAllText(Path.Combine(Generator.ProjectRoot(),"plugin-load.log"),"Single-stage and two-stage design/model command tabs created."+System.Environment.NewLine);
        }
        public bool DisconnectFromSW() {
            if (manager != null) {
                foreach(int type in new[]{1,2,3}) { var tab=manager.GetCommandTab(type,"减速器生成器"); if(tab!=null) manager.RemoveCommandTab(tab); }
                manager.RemoveCommandGroup(240128);
            }
            manager = null; app = null; return true;
        }
        public string BuildLocation() { return typeof(ReducerAddin).Assembly.Location; }
        void LaunchWindow(string arguments) {
            try {
                if(designWindow!=null&&!designWindow.HasExited) {MessageBox.Show("设计窗口已经打开，请切换到现有窗口。","减速器设计");return;}
                if(designWindow!=null){designWindow.Dispose();designWindow=null;}
                string root=Generator.ProjectRoot(),latest=Path.Combine(root,"latest-build.txt");
                string build=File.Exists(latest)?File.ReadAllText(latest).Trim():Path.GetDirectoryName(BuildLocation());
                string launcher=Path.Combine(build,"ReducerLauncher.exe");
                if(!File.Exists(launcher))throw new FileNotFoundException("设计入口不完整，请重新运行 build.ps1。",launcher);
                // CreateNoWindow suppresses the console. The design form itself
                // must start normally; SW_HIDE can also hide its first window.
                designWindow=Process.Start(new ProcessStartInfo{FileName=launcher,Arguments=arguments,WorkingDirectory=root,UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Normal});
            }catch(Exception ex){MessageBox.Show(ex.Message,"减速器设计",MessageBoxButtons.OK,MessageBoxIcon.Error);}
        }
        public void Design() {LaunchWindow("--design");}
        public void EditParameters() {LaunchWindow("--editor");}
        public void TwoStage() {LaunchWindow("--editor \""+Path.Combine(Generator.ProjectRoot(),"parameters_two_stage.json")+"\"");}
        public void TwoStageDesign() {LaunchWindow("--two-design");}
        public void Generate() {
            try {
                string root = Generator.ProjectRoot();
                using (var picker = new OpenFileDialog { Title="选择建模参数或设计项目", Filter="减速器参数或设计项目 (*.json)|*.json", InitialDirectory=root }) {
                    if (picker.ShowDialog() != DialogResult.OK) return;
                    // Open the current build so subsequent design updates do not
                    // depend on the assembly cached inside the SW process.
                    LaunchWindow("--editor \""+picker.FileName+"\"");
                }
            } catch (Exception ex) { MessageBox.Show(ex.ToString(), "Generation failed"); }
        }
        [ComRegisterFunction]
        public static void Register(Type type) {
            // Per-user COM registration permits explicit loading without administrator access.
            string id = "{" + type.GUID.ToString().ToUpperInvariant() + "}";
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\CLSID\" + id + @"\InprocServer32")) {
                key.SetValue("", "mscoree.dll"); key.SetValue("ThreadingModel", "Both");
                key.SetValue("Class", type.FullName); key.SetValue("Assembly", type.Assembly.FullName);
                key.SetValue("RuntimeVersion", "v4.0.30319"); key.SetValue("CodeBase", new Uri(type.Assembly.Location).ToString());
                using(var version=key.CreateSubKey(type.Assembly.GetName().Version.ToString())) {
                    version.SetValue("Class",type.FullName); version.SetValue("Assembly",type.Assembly.FullName);
                    version.SetValue("RuntimeVersion","v4.0.30319"); version.SetValue("CodeBase",new Uri(type.Assembly.Location).ToString());
                }
            }
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\CLSID\" + id + @"\ProgId")) key.SetValue("", "ReducerSW.Addin");
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\ReducerSW.Addin\CLSID")) key.SetValue("", id);
        }
    }
    public class Segment { public double diameter {get;set;} public double length {get;set;} }
    public class Keyway { public int? segment_index {get;set;} public double start {get;set;} public double length {get;set;} public double width {get;set;} public double depth {get;set;} public double diameter {get;set;} }
    public class Cover { public double outer_diameter {get;set;} public double step_diameter {get;set;} public double inner_recess_diameter {get;set;} public double total_thickness {get;set;} public double step_height {get;set;} public double recess_depth {get;set;} }
    public class Ring { public string name {get;set;} public double outer_diameter {get;set;} public double inner_diameter {get;set;} public double thickness {get;set;} public double chamfer {get;set;} }
    public class ThroughCover { public string name {get;set;} public double[][] outer_steps {get;set;} public double[][] inner_steps {get;set;} public double seal_bore_diameter {get;set;} public int? seal_shaft_segment_index {get;set;} }
    public class RotaryPart { public string name {get;set;} public double[][] section {get;set;} }
    public class AssemblyDatum { public double low_center_x {get;set;} public double high_center_x {get;set;} public double axis_height {get;set;} public double boss_depth {get;set;} public double mesh_center_y {get;set;} public double low_bearing_width {get;set;} public double high_bearing_width {get;set;} public string source {get;set;} }
    public class Parameters { public int schema_version {get;set;} public int hardware_version {get;set;} public string units {get;set;} public Segment[] shaft_segments {get;set;} public Keyway[] keyways {get;set;} public Cover blank_cover {get;set;} public Cover high_blank_cover {get;set;} public Ring[] accessories {get;set;} public ThroughCover[] through_covers {get;set;} public RotaryPart[] rotary_parts {get;set;} public AssemblyDatum assembly_layout {get;set;} public GearTrainSpec gears {get;set;} public BearingSpec[] bearings {get;set;} public AuxiliarySpec[] auxiliary {get;set;} public HousingSpec housing {get;set;} public TwoStageSpec two_stage {get;set;} public string[] migration_notes {get;set;} }

    public static partial class Generator {
        static HashSet<IntPtr> generationDocuments;
        static IntPtr DocumentIdentity(object document) {
            IntPtr identity=Marshal.GetIUnknownForObject(document);
            Marshal.Release(identity);return identity;
        }
        public static string DefaultParameters() {
            string root=ProjectRoot();string full=Path.Combine(root,"parameters_v4.json");if(File.Exists(full))return full;string newest=Path.Combine(root,"parameters_v3.json");if(File.Exists(newest))return newest;string next=Path.Combine(root,"parameters_v2.json");
            return File.Exists(next)?next:Path.Combine(root,"parameters.json");
        }
        public static string ProjectRoot() {
            var folder=new DirectoryInfo(Path.GetDirectoryName(typeof(Generator).Assembly.Location));
            while(folder!=null) { if(Directory.Exists(Path.Combine(folder.FullName,"src"))) return folder.FullName; folder=folder.Parent; }
            throw new Exception("Cannot locate plugin project folder.");
        }
        static double M(double mm) { return mm / 1000.0; }
        static void Positive(double value) { if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) throw new ArgumentException("Dimensions must be finite and positive."); }
        public static void Validate(Parameters p) {
            if(p!=null&&p.schema_version==5) {
                if(p.units!="mm"||p.two_stage==null)throw new ArgumentException("二级参数需要 schema_version=5、mm 单位和 two_stage 数据。");
                ValidateTwoStage(p.two_stage);return;
            }
            if (p == null || p.schema_version<1 || p.schema_version>4 || p.units != "mm" || p.shaft_segments == null || p.shaft_segments.Length == 0 || p.blank_cover == null || p.keyways == null) throw new ArgumentException("参数格式不完整：需要毫米单位、轴段、键槽和低速轴闷盖。");
            foreach (var s in p.shaft_segments) { Positive(s.diameter); Positive(s.length); }
            foreach(var c in new[]{p.blank_cover,p.high_blank_cover}) {
            if(c==null) continue;
            foreach (double v in new [] {c.outer_diameter,c.step_diameter,c.inner_recess_diameter,c.total_thickness,c.step_height,c.recess_depth}) Positive(v);
            if (c.inner_recess_diameter >= Math.Min(c.outer_diameter,c.step_diameter) || c.step_height >= c.total_thickness || c.recess_depth >= c.total_thickness-c.step_height) throw new ArgumentException("Invalid cover proportions.");
            }
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"driven_shaft","low_blank_cover","high_blank_cover","prototype_exploded","parameters"};
            foreach(var r in p.accessories??new Ring[0]) {
                Positive(r.outer_diameter); Positive(r.inner_diameter); Positive(r.thickness);
                RegisterName(names,r.name);
                if(r.inner_diameter>=r.outer_diameter || double.IsNaN(r.chamfer) || double.IsInfinity(r.chamfer) || r.chamfer<0 || r.chamfer>=Math.Min(r.thickness/2,(r.outer_diameter-r.inner_diameter)/4)) throw new ArgumentException("附件内外径、厚度或倒角不合理："+r.name);
            }
            ValidateRotaryParts(p,names);
            foreach(var bearing in p.bearings??new BearingSpec[0]){ValidateBearing(bearing);RegisterName(names,bearing.name);}
            if(p.gears!=null){ValidateGearTrain(p.gears);RegisterName(names,"drive_gear_shaft");RegisterName(names,"driven_gear");if(p.shaft_segments.Length<2)throw new ArgumentException("齿轮定位至少需要两个低速轴段。");if(Math.Abs(p.gears.driven_bore-p.shaft_segments[1].diameter)>1e-7)throw new ArgumentException("从动齿轮孔径必须匹配低速轴第二段。");}
            ValidateAuxiliary(p.auxiliary,names);
            if(p.housing!=null){ValidateHousing(p.housing);RegisterName(names,"housing");RegisterName(names,"cover");}
            ValidateAssemblyInterfaces(p);
            if(p.hardware_version!=0&&p.hardware_version!=1)throw new ArgumentException("不支持的一级标准件版本。");
            if(p.hardware_version==1)ValidateHardwareInterfaces(p);
            if(p.assembly_layout!=null) {
                if(p.shaft_segments.Length<2)throw new ArgumentException("总装定位至少需要两个低速轴段。");
                var a=p.assembly_layout;Positive(a.axis_height);Positive(a.boss_depth);Positive(a.low_bearing_width);Positive(a.high_bearing_width);
                foreach(double v in new[]{a.low_center_x,a.high_center_x,a.mesh_center_y})if(double.IsNaN(v)||double.IsInfinity(v))throw new ArgumentException("装配定位坐标必须有限。");
                if(a.low_center_x<=a.high_center_x)throw new ArgumentException("低速轴中心必须位于高速轴中心右侧。");
            }
            foreach (var k in p.keyways) {
                Positive(k.length); Positive(k.width); Positive(k.depth); Positive(k.diameter);
                if (k.length <= k.width || k.width>=k.diameter || k.depth >= k.diameter/2 || double.IsNaN(k.start) || double.IsInfinity(k.start) || k.start < 0) throw new ArgumentException("键槽长度必须大于宽度，且宽度、深度必须适合对应轴径。");
                double start = 0; bool found = false;
                foreach (var s in p.shaft_segments) {
                    if (k.start >= start-1e-8 && k.start+k.length <= start+s.length+1e-8 && Math.Abs(k.diameter-s.diameter)<1e-8) found=true;
                    start += s.length;
                }
                if (!found) throw new ArgumentException("Keyway is not contained in its shaft segment.");
            }
        }
        static ModelDoc2 NewDocument(SldWorks app, int kind, string name) {
            GenerationControl.Checkpoint("正在生成："+ModelPresentation.ChineseName(name));
            string template = app.GetDocumentTemplate(kind, "", 0, 0, 0);
            if (String.IsNullOrEmpty(template) || !File.Exists(template)) throw new Exception("Set a default part/assembly template in SOLIDWORKS first.");
            var doc = (ModelDoc2)app.NewDocument(template, 0, 0, 0);
            if (doc == null) throw new Exception("Cannot create document: " + template);
            if(generationDocuments!=null)generationDocuments.Add(DocumentIdentity(doc));
            return doc;
        }
        static Feature FrontPlane(ModelDoc2 doc) {
            Feature f = (Feature)doc.FirstFeature();
            while (f != null) { if (f.GetTypeName2() == "RefPlane") return f; f = (Feature)f.GetNextFeature(); }
            throw new Exception("Template has no reference plane.");
        }
        static void BeginSketch(ModelDoc2 doc) {
            doc.ClearSelection2(true);
            if (!FrontPlane(doc).Select2(false, 0)) throw new Exception("Cannot select front plane.");
            doc.SketchManager.InsertSketch(true);
            doc.SketchManager.AddToDB = true;
        }
        static Feature Revolve(ModelDoc2 doc, double[][] points, string name, bool ring=false) {
            BeginSketch(doc);
            var sm = doc.SketchManager;
            var lines = new List<SketchSegment>();
            var edges = new List<double[][]>(Edges(points));
            foreach (var pair in edges) lines.Add(sm.CreateLine(M(pair[0][0]),M(pair[0][1]),0,M(pair[1][0]),M(pair[1][1]),0));
            var axis = sm.CreateCenterLine(-.01,0,0,M(points[points.Length-2][0])+.01,0,0);
            var verticalAxis=sm.CreateCenterLine(0,-.01,0,0,.1,0);
            sm.AddToDB = false;
            doc.ClearSelection2(true); axis.Select4(false,null); doc.SketchAddConstraints("sgFIXED");
            doc.ClearSelection2(true); verticalAxis.Select4(false,null); doc.SketchAddConstraints("sgFIXED");
            doc.ClearSelection2(true); ((SketchPoint)((SketchLine)lines[0]).GetStartPoint2()).Select4(false,null); verticalAxis.Select4(true,null); doc.SketchAddConstraints("sgCOINCIDENT");
            int axial = 0, radial = 0;
            for (int i=0;i<lines.Count;i++) {
                var a=edges[i][0]; var b=edges[i][1];
                doc.ClearSelection2(true); lines[i].Select4(false,null);
                bool horizontal=Math.Abs(a[1]-b[1])<1e-9;
                doc.SketchAddConstraints(horizontal ? "sgHORIZONTAL2D" : "sgVERTICAL2D");
                // Leave the closing base line undimensioned to avoid a redundant total length.
                if(horizontal && a[1]==0) {
                    doc.ClearSelection2(true); lines[i].Select4(false,null); axis.Select4(true,null); doc.SketchAddConstraints("sgCOLINEAR");
                    continue;
                }
                if((!horizontal && b[1]==0) || (ring && i>=2)) continue;
                doc.ClearSelection2(true); lines[i].Select4(false,null);
                DisplayDimension display=horizontal
                    ? (DisplayDimension)doc.AddHorizontalDimension2(M((a[0]+b[0])/2),M(Math.Max(a[1],b[1])+8+axial*2),0)
                    : (DisplayDimension)doc.AddVerticalDimension2(M(a[0]-8-radial*2),M((a[1]+b[1])/2),0);
                if(display==null) throw new Exception("Cannot add editable profile dimension.");
                var dimension=(Dimension)display.GetDimension2(0);
                dimension.Name=horizontal ? "Axial_"+(++axial) : "Radial_"+(++radial);
            }
            if(ring) {
                doc.ClearSelection2(true); lines[lines.Count-1].Select4(false,null); axis.Select4(true,null);
                var inner=(DisplayDimension)doc.AddVerticalDimension2(-.01,M(points[0][1]/2),0);
                if(inner==null) throw new Exception("Cannot dimension ring bore radius.");
                ((Dimension)inner.GetDimension2(0)).Name="BoreRadius";
            }
            if(sm.ActiveSketch.GetConstrainedStatus()!=(int)swConstrainedStatus_e.swFullyConstrained) throw new Exception("旋转轮廓未完全约束："+name);
            var sketch = (Feature)sm.ActiveSketch;
            sm.AddToDB = false; sm.InsertSketch(true);
            doc.ClearSelection2(true);
            sketch.Select2(false,0);
            var selection = ((SelectionMgr)doc.SelectionManager).CreateSelectData(); selection.Mark=16;
            if (!axis.Select4(true,selection)) throw new Exception("Cannot select revolve axis.");
            Feature feature=doc.FeatureManager.FeatureRevolve2(true,true,false,false,false,false,0,0,Math.PI*2,0,false,false,0,0,0,0,0,true,true,true);
            if(feature==null) throw new Exception("Revolve failed: "+name);
            ModelPresentation.NameCreated(feature,name); ModelPresentation.NameCreated(sketch,name+"_Profile");
            return feature;
        }
        static IEnumerable<double[][]> Edges(double[][] points) {
            for(int i=0;i<points.Length;i++) {
                var a=points[i]; var b=points[(i+1)%points.Length];
                if(Math.Abs(a[0]-b[0])+Math.Abs(a[1]-b[1])>1e-9) yield return new [] {a,b};
            }
        }
        static void KeySlot(ModelDoc2 doc, Keyway k, int index) {
            BeginSketch(doc);
            var sm=doc.SketchManager;
            double a=M(k.start+k.width/2), b=M(k.start+k.length-k.width/2), r=M(k.width/2);
            sm.CreateLine(a,r,0,b,r,0);
            sm.CreateArc(b,0,0,b,r,0,b,-r,0,-1);
            sm.CreateLine(b,-r,0,a,-r,0);
            sm.CreateArc(a,0,0,a,-r,0,a,r,0,-1);
            var sketch=(Feature)sm.ActiveSketch;
            sm.AddToDB=false; sm.InsertSketch(true);
            doc.ClearSelection2(true); sketch.Select2(false,0);
            // Sketch on XY; start above shaft and cut toward -Z to the keyway floor.
            var feature=doc.FeatureManager.FeatureCut3(true,false,false,0,0,M(k.depth+1),0,false,false,false,false,0,0,false,false,false,false,false,true,true,true,true,false,3,M(k.diameter/2+1),false);
            if(feature==null) throw new Exception("Keyway cut failed: "+index);
            ModelPresentation.NameCreated(feature,"Keyway_"+index); ModelPresentation.NameCreated(sketch,"Keyway_"+index+"_Profile");
        }
        static void Save(ModelDoc2 doc,string path) {
            string name=ModelPresentation.ChineseName(path);
            GenerationControl.Checkpoint("正在保存："+name);
            if(!doc.EditRebuild3()) throw new Exception("Rebuild failed: "+path);
            ModelPresentation.Apply(doc,path);
            Feature feature=(Feature)doc.FirstFeature();
            while(feature!=null) {
                bool warning; int code=feature.GetErrorCode2(out warning);
                if(code!=0 && !warning) throw new Exception("特征重建失败："+feature.Name+"，错误码 "+code);
                feature=(Feature)feature.GetNextFeature();
            }
            doc.ViewZoomtofit2();
            int errors=0,warnings=0;
            if(!doc.Extension.SaveAs(path,0,1,null,ref errors,ref warnings) || errors!=0) throw new Exception("Save failed: "+path+" errors="+errors+" warnings="+warnings);
            GenerationControl.Checkpoint("已保存："+name);
        }
        // 一次生成会创建二十多个零件文档和一个总装文档。Save 只存不关，这些文档会一直占用
        // 内存和 GDI 句柄；累积几轮之后 SW 就会因内存不足报错、甚至直接崩溃（RPC 服务器不可用）。
        // 所以统一"存完即关"：体积与实体数量在关闭之前取好，之后只保留文件路径。
        static void SaveAndClose(SldWorks app,ModelDoc2 doc,string path) {
            try {Save(doc,path);}
            finally {app.CloseDoc(doc.GetTitle());}
        }
        static string SaveWithMetrics(SldWorks app,ModelDoc2 doc,string path,List<object> metrics,bool allowMultipleBodies=false) {
            try {
                Save(doc,path);
                metrics.Add(Metrics(doc,path,allowMultipleBodies));
                return path;
            } finally {app.CloseDoc(doc.GetTitle());}
        }
        static object Metrics(ModelDoc2 doc,string path,bool allowMultipleBodies=false) {
            var bodies=(object[])((PartDoc)doc).GetBodies2(0,false);
            if(bodies==null || bodies.Length<1 || (!allowMultipleBodies&&bodies.Length!=1)) throw new Exception("生成零件实体数量不符合预期："+path);
            doc.ClearSelection2(true);int status;
            var mass=(double[])doc.Extension.GetMassProperties2(2,out status,false);
            if(mass==null||mass.Length<4||status!=0)throw new Exception("高精度体积计算失败："+path+" status="+status);
            return new {file=Path.GetFileName(path),solid_bodies=bodies.Length,volume_mm3=mass[3]*1e9,multiple_bodies_allowed=allowMultipleBodies};
        }
        static string MakeCover(SldWorks app,Cover c,string name,string output,List<object> metrics) {
            var doc=NewDocument(app,1,name); double t=c.total_thickness-c.step_height;
            Revolve(doc,new[]{new[]{0.0,c.inner_recess_diameter/2},new[]{0.0,c.outer_diameter/2},new[]{t,c.outer_diameter/2},new[]{t,c.step_diameter/2},new[]{c.total_thickness,c.step_diameter/2},new[]{c.total_thickness,0.0},new[]{c.recess_depth,0.0},new[]{c.recess_depth,c.inner_recess_diameter/2}},"BlankCover_Revolve");
            string path=Path.Combine(output,name+".SLDPRT");return SaveWithMetrics(app,doc,path,metrics);
        }
        static string MakeRing(SldWorks app,Ring r,string output,List<object> metrics) {
            var doc=NewDocument(app,1,r.name);
            Revolve(doc,new[]{new[]{0.0,r.inner_diameter/2},new[]{0.0,r.outer_diameter/2},new[]{r.thickness,r.outer_diameter/2},new[]{r.thickness,r.inner_diameter/2}},"Ring_Revolve",true);
            if(r.chamfer>0) {
                var bodies=(object[])((PartDoc)doc).GetBodies2(0,false);
                doc.ClearSelection2(true);int count=0;
                foreach(Edge edge in (object[])((Body2)bodies[0]).GetEdges()) {
                    if(((Curve)edge.GetCurve()).IsCircle()) {((Entity)edge).Select4(true,null);count++;}
                }
                if(count!=4) throw new Exception("轴套倒角边识别失败。");
                var chamfer=doc.FeatureManager.InsertFeatureChamfer(0,1,M(r.chamfer),Math.PI/4,0,0,0,0);
                if(chamfer==null) throw new Exception("轴套倒角创建失败。");ModelPresentation.NameCreated(chamfer,"EndChamfers");
            }
            string path=Path.Combine(output,r.name+".SLDPRT");return SaveWithMetrics(app,doc,path,metrics);
        }
        public static string Generate(SldWorks app,string parameterFile,string outputRoot) {
            return Generate(app,parameterFile,outputRoot,true);
        }
        public static string Generate(SldWorks app,string parameterFile,string outputRoot,bool addAssemblyMates) {
            using(var gate=new System.Threading.Mutex(false,@"Local\ReducerSW.NativeGeneration.6B5C4699")) {
                bool acquired=false;
                try {
                    try {acquired=gate.WaitOne(0);}catch(System.Threading.AbandonedMutexException){acquired=true;}
                    if(!acquired)throw new InvalidOperationException("另一个窗口正在生成减速器模型，请等待本次生成完成后重试。");
                    using(GenerationControl.Start())return GenerateCore(app,parameterFile,outputRoot,addAssemblyMates);
                } finally {if(acquired)gate.ReleaseMutex();}
            }
        }
        static string GenerateCore(SldWorks app,string parameterFile,string outputRoot,bool addAssemblyMates) {
            var json=new JavaScriptSerializer();
            var p=json.Deserialize<Parameters>(File.ReadAllText(parameterFile)); Validate(p);
            if(p.schema_version==5)return GenerateTwoStage(app,p,parameterFile,outputRoot,addAssemblyMates);
            // Unique output folders preserve previous generated and manually edited models.
            string output=Path.Combine(outputRoot,DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")+"_"+Guid.NewGuid().ToString("N").Substring(0,6));
            Directory.CreateDirectory(output);
            File.Copy(parameterFile,Path.Combine(output,"parameters.json"));
            var log=new List<string>();
            var metrics=new List<object>(); var partPaths=new List<string>();
            // Older generated documents may now contain user edits. Track only
            // this invocation's new documents, including an unsaved failed part.
            generationDocuments=new HashSet<IntPtr>();
            bool prompt=app.GetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate);
            app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate,false);
            try {
                var shaft=NewDocument(app,1,"driven_shaft");
                var points=new List<double[]> {new[]{0.0,0.0},new[]{0.0,p.shaft_segments[0].diameter/2}};
                double x=0;
                foreach(var s in p.shaft_segments) { points.Add(new[]{x,s.diameter/2}); x+=s.length; points.Add(new[]{x,s.diameter/2}); }
                points.Add(new[]{x,0.0});
                Revolve(shaft,points.ToArray(),"DrivenShaft_Revolve");
                for(int i=0;i<p.keyways.Length;i++) KeySlot(shaft,p.keyways[i],i+1);
                string shaftPath=Path.Combine(output,"driven_shaft.SLDPRT");
                metrics.Add(Metrics(shaft,shaftPath)); partPaths.Add(shaftPath);
                SaveAndClose(app,shaft,shaftPath); log.Add("Saved native driven shaft with keyways.");
                partPaths.Add(MakeCover(app,p.blank_cover,"low_blank_cover",output,metrics));
                if(p.high_blank_cover!=null) partPaths.Add(MakeCover(app,p.high_blank_cover,"high_blank_cover",output,metrics));
                foreach(var ring in p.accessories??new Ring[0]) partPaths.Add(MakeRing(app,ring,output,metrics));
                var fits=new List<object>();
                foreach(var through in p.through_covers??new ThroughCover[0]) {
                    partPaths.Add(MakeThroughCover(app,through,output,metrics));
                    string sealName=through.name.Replace("_through_cover","_oil_seal");
                    partPaths.Add(MakeRotaryPart(app,new RotaryPart{name=sealName,section=OilSealSection(through)},output,metrics));
                    fits.Add(CheckThroughFit(through));
                }
                foreach(var part in p.rotary_parts??new RotaryPart[0]) partPaths.Add(MakeRotaryPart(app,part,output,metrics));
                if(p.gears!=null)partPaths.AddRange(MakeGearTrain(app,p.gears,output,metrics));
                partPaths.AddRange(MakeBearings(app,p.bearings,output,metrics));
                partPaths.AddRange(MakeAuxiliary(app,p.auxiliary,output,metrics));
                if(p.housing!=null)partPaths.AddRange(MakeHousings(app,p.housing,output,metrics));
                if(p.hardware_version==1)partPaths.AddRange(MakeHardware(app,p,output,metrics));
                var assembly=CreateTotalAssembly(app,p,partPaths,output,addAssemblyMates);
                string layoutScope=OneStageLayoutScope(p,addAssemblyMates);
                log.Add("Saved one-stage reducer_assembly.SLDASM ("+layoutScope+").");
                File.WriteAllText(Path.Combine(output,"parts_manifest.json"),json.Serialize(new {schema_version=2,units="mm",parts=metrics,assembly="reducer_assembly.SLDASM",component_count=assembly.instances.Count,instances=assembly.instances,top_level_mates=assembly.seal_mates,native_mates=assembly.native_mates,assembly_mates_added=addAssemblyMates,layout_scope=layoutScope}));
                File.WriteAllText(Path.Combine(output,"fit_report.json"),json.Serialize(new {schema_version=1,scope="local cover/shaft/seal dimensions; not full assembly interference analysis",checks=fits}));
                log.Add("Saved "+partPaths.Count+" native parts and "+assembly.instances.Count+" top-level components.");
                log.Add("Limitations: low-shaft chamfers and housing toroidal root relief are not migrated; some sketches remain underconstrained; bearings use reference internals.");
                File.WriteAllLines(Path.Combine(output,"generation.log"),log);
                return output;
            } catch(Exception ex) { log.Add(ex.ToString()); File.WriteAllLines(Path.Combine(output,"generation.log"),log); throw; }
            finally {
                app.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swInputDimValOnCreate,prompt);
                // The current output folder is unique. Close only documents owned
                // by this invocation; preserve previously open generated models.
                try {
                    foreach(object item in (app.GetDocuments() as object[])??new object[0]) {
                        var document=item as ModelDoc2;
                        if(document==null)continue;
                        string opened=document.GetPathName();
                        if(generationDocuments.Contains(DocumentIdentity(document))||(!String.IsNullOrEmpty(opened)&&opened.StartsWith(output+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)))
                            app.CloseDoc(document.GetTitle());
                    }
                } catch(Exception cleanup) { log.Add("Document cleanup skipped: "+cleanup.Message); }
                generationDocuments=null;
                File.WriteAllLines(Path.Combine(output,"generation.log"),log);
            }
        }
    }
}
