using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;

namespace ReducerSW {
    // Both gear stages use the same offline calculation method.
    // CAD creation starts only after the current design and native geometry pass.
    public sealed class TwoStageDesignWizard : Form {
        sealed class Field {public DataGridViewCell Cell;public bool Numeric;}
        readonly SldWorks app;
        readonly Func<Dictionary<string,object>,Dictionary<string,object>> bridge;
        readonly JavaScriptSerializer json=new JavaScriptSerializer{MaxJsonLength=Int32.MaxValue,RecursionLimit=128};
        readonly Dictionary<string,Field> fields=new Dictionary<string,Field>();
        readonly List<DataGridView> inputs=new List<DataGridView>();
        readonly Panel[] pages=new Panel[8];
        readonly ListBox navigation=new ListBox{Dock=DockStyle.Fill,BorderStyle=BorderStyle.None,IntegralHeight=false,ItemHeight=44};
        readonly Panel content=new Panel{Dock=DockStyle.Fill};
        readonly Label heading=new Label{Dock=DockStyle.Top,Height=52,Padding=new Padding(14,10,0,0),Font=new Font("Microsoft YaHei UI",15,FontStyle.Bold)};
        readonly Label status=new Label{Dock=DockStyle.Bottom,Height=43,Padding=new Padding(12,9,12,3),BackColor=Color.FromArgb(235,241,247)};
        readonly FlowLayoutPanel toolbar=DesignWizard.ActionBar(DockStyle.Top);
        readonly FlowLayoutPanel footer=DesignWizard.ActionBar(DockStyle.Bottom);
        readonly TextBox projectName=new TextBox{Text="二级圆柱齿轮减速器设计"},designer=new TextBox(),projectNumber=new TextBox(),notes=new TextBox{Multiline=true,Height=105,ScrollBars=ScrollBars.Vertical};
        readonly TextBox highSummary=ReadOnlyText(),lowSummary=ReadOnlyText(),steps=ReadOnlyText(),generationSummary=ReadOnlyText();
        readonly DataGridView shaftResults=Grid(true),checks=Grid(true);
        readonly Button autoButton=DesignWizard.ActionBarButton("自动分配齿数",148);
        readonly Button calculateButton=DesignWizard.ActionBarButton("重新计算与校核",148);
        readonly Button exportButton=new Button{Text="保存建模方案",Width=130,Height=32};
        readonly Button generateButton=new Button{Text="生成 SOLIDWORKS 模型",Width=205,Height=32};
        readonly Button reportButton=new Button{Text="生成设计说明书",Width=145,Height=32};
        readonly CheckBox addMates=ParameterEditor.CreateMateOption(DockStyle.Top);
        readonly Button openOutput=new Button{Text="打开模型目录",Width=130,Height=32};
        readonly Button openReport=new Button{Text="打开说明书",Width=115,Height=32};
        Dictionary<string,object> design;
        bool applying,dirty=true,busy;
        string workFolder,modelFile,outputFolder,reportFile;
        static readonly string[] Titles={"项目信息","工况输入","传动比分配","高速级齿轮","低速级齿轮","三轴与轴承","计算与校核","模型与说明书"};

        readonly Dictionary<string,object> initialProject;
        public TwoStageDesignWizard(SldWorks application):this(application,DesignBridge.Call) {}
        public TwoStageDesignWizard(SldWorks application,Dictionary<string,object> project):this(application) {initialProject=project;}
        internal TwoStageDesignWizard(SldWorks application,Func<Dictionary<string,object>,Dictionary<string,object>> call) {
            SuspendLayout();
            ToolbarIcons.ApplyWindowIcon(this);
            app=application;bridge=call;
            Text="二级减速器 — 完整设计流程";Width=1180;Height=820;MinimumSize=new Size(950,650);
            StartPosition=FormStartPosition.CenterScreen;Font=new Font("Microsoft YaHei UI",10);BackColor=Color.White;
            var left=new Panel{Dock=DockStyle.Left,Width=190,Padding=new Padding(8,12,8,8),BackColor=Color.FromArgb(240,244,249)};
            for(int i=0;i<Titles.Length;i++)navigation.Items.Add((i+1)+".  "+Titles[i]);
            navigation.SelectedIndexChanged+=(s,e)=>SelectPage(navigation.SelectedIndex);left.Controls.Add(navigation);
            Controls.Add(content);Controls.Add(left);Controls.Add(footer);Controls.Add(status);Controls.Add(toolbar);Controls.Add(heading);
            DesignWizard.AddButton(toolbar,"打开项目",OpenProject);DesignWizard.AddButton(toolbar,"保存项目",SaveProject);
            toolbar.Controls.Add(autoButton);toolbar.Controls.Add(calculateButton);
            DesignWizard.AddButton(toolbar,"设计参考图表",()=>DesignWizard.ShowReferences(this,Error));
            autoButton.Click+=(s,e)=>Compute(true);calculateButton.Click+=(s,e)=>Compute(false);
            DesignWizard.AddButton(footer,"下一步 →",()=>navigation.SelectedIndex=Math.Min(7,navigation.SelectedIndex+1));
            DesignWizard.AddButton(footer,"← 上一步",()=>navigation.SelectedIndex=Math.Max(0,navigation.SelectedIndex-1));
            for(int i=0;i<pages.Length;i++){pages[i]=new Panel{Dock=DockStyle.Fill,Padding=new Padding(14,7,14,12),Visible=false};content.Controls.Add(pages[i]);}
            BuildProject();BuildOperating();BuildRatios();BuildGear(3,"high",highSummary);BuildGear(4,"low",lowSummary);BuildShafts();BuildChecks();BuildGeneration();
            navigation.SelectedIndex=0;UpdateActions();status.Text="填写工况、两级齿轮参数，点击“重新计算与校核”。";
            Shown+=(s,e)=>{var area=Screen.FromControl(this).WorkingArea;
                MinimumSize=new Size(Math.Min(MinimumSize.Width,area.Width),Math.Min(MinimumSize.Height,area.Height));
                Width=Math.Min(Width,area.Width);Height=Math.Min(Height,area.Height);
                Location=new Point(area.Left+(area.Width-Width)/2,area.Top+(area.Height-Height)/2);
                navigation.SelectedIndex=0;SelectPage(0);
                RunBridge(new Dictionary<string,object>{{"action","two_schema"}},"正在载入设计参数…",response=>{
                    var defaults=Map(Get(Map(Get(response,"schema")),"defaults"));
                    applying=true;try{foreach(var item in defaults){Field field;if(fields.TryGetValue(item.Key,out field))SetFieldValue(field,item.Value);}}finally{applying=false;}
                    if(initialProject!=null)LoadProjectSnapshot(initialProject);
                    else status.Text="参数已就绪；可按参考装配的默认尺寸计算，或修改两级齿轮参数。";});};
            FormClosing+=(s,e)=>{
                if(!busy)return;
                e.Cancel=true;
                if(GenerationControl.RequestStop()) {
                    closeAfterGeneration=true;
                    status.Text="正在停止建模，完成清理后关闭窗口。";
                } else status.Text="正在处理设计数据，请完成后再关闭。";
            };
            AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;
            ResumeLayout(false);
        }
        static TextBox ReadOnlyText(){return new TextBox{Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,BackColor=Color.White,Font=new Font("Microsoft YaHei UI",10)};}
        static DataGridView Grid(bool readOnly){return DesignWizard.Grid(readOnly);}
        static void Columns(DataGridView grid,params string[] titles){foreach(string title in titles)grid.Columns.Add(title,title);}
        static Label Note(string value,int height){return new Label{Dock=DockStyle.Top,Height=height,Padding=new Padding(4,5,4,3),Text=value,ForeColor=Color.FromArgb(65,75,88)};}
        DataGridView InputGrid(Control parent,string note){var grid=Grid(false);Columns(grid,"参数","数值","单位","说明");
            foreach(int index in new[]{0,2,3})grid.Columns[index].ReadOnly=true;
            grid.Columns[0].FillWeight=30;grid.Columns[1].FillWeight=20;grid.Columns[2].FillWeight=12;grid.Columns[3].FillWeight=45;
            grid.DefaultCellStyle.WrapMode=DataGridViewTriState.True;
            parent.Controls.Add(grid);if(!String.IsNullOrEmpty(note))parent.Controls.Add(Note(note,65));
            inputs.Add(grid);grid.CellValueChanged+=(s,e)=>{if(applying||e.RowIndex<0||e.ColumnIndex!=1)return;var material=grid[e.ColumnIndex,e.RowIndex] as MaterialChoiceCell;if(material==null||!material.HandleChoice(this))MarkDirty();};
            grid.CurrentCellDirtyStateChanged+=(s,e)=>{if(grid.IsCurrentCellDirty&&grid.CurrentCell is MaterialChoiceCell)grid.CommitEdit(DataGridViewDataErrorContexts.Commit);};
            grid.DataError+=(s,e)=>{e.ThrowException=false;status.Text="请检查输入值。";};return grid;}
        void Row(DataGridView grid,string key,string label,object value,string unit,string note){int row=grid.Rows.Add(label,value,unit,note);
            fields[key]=new Field{Cell=grid.Rows[row].Cells[1],Numeric=!(value is string)};}
        void GearTypeRow(DataGridView grid,string key){
            int row=grid.Rows.Add("齿轮类型","spur","spur / helical","直齿 spur；斜齿 helical");
            var choice=new DesignChoiceCell();choice.Items.AddRange("spur","helical");choice.Value="spur";
            grid.Rows[row].Cells[1]=choice;fields[key]=new Field{Cell=choice,Numeric=false};
        }
        void BuildProject(){var table=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=2,Padding=new Padding(15)};
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,110));table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            Control[] values={projectName,designer,projectNumber,notes};string[] labels={"项目名称","设计者","项目编号","项目备注"};
            for(int i=0;i<values.Length;i++){values[i].Dock=DockStyle.Fill;table.Controls.Add(new Label{Text=labels[i],AutoSize=true,Padding=new Padding(0,5,0,0)},0,i);table.Controls.Add(values[i],1,i);}
            pages[0].Controls.Add(table);pages[0].Controls.Add(Note("本流程面向无 V 带的二级直齿或斜齿圆柱齿轮减速器。项目可保存后重新打开；载入后需重新计算。",55));}
        void BuildOperating(){var g=InputGrid(pages[1],"直接填写减速器输入轴功率和转速；目标输出转速用于分配两级传动比。效率用于估算中间轴与输出轴功率。");
            Row(g,"input_power_kw","减速器输入功率",1.1178,"kW","输入轴实际功率");Row(g,"input_speed_rpm","减速器输入转速",1400.0,"r/min","输入轴实际转速");
            Row(g,"target_output_speed_rpm","目标输出转速",119.0,"r/min","用于计算目标传动比");
            Row(g,"bearing_efficiency","每级轴承效率",0.99,"—","每级功率计算计入一次");Row(g,"gear_efficiency","每级齿轮效率",0.98,"—","每级功率计算计入一次");
            Row(g,"life_years","工作年限",8.0,"年","");Row(g,"days_per_year","每年工作天数",300.0,"天","");Row(g,"hours_per_day","每日工作小时",8.0,"h","");
            Row(g,"application_factor","使用系数 KA",1.0,"—","按工况查取");}
        void BuildRatios(){var g=InputGrid(pages[2],"总传动比 i=输入转速/目标输出转速；推荐 i1≈√(i·λ)、i2≈i/i1。点击“自动分配齿数”仅调整两级大齿轮齿数；手动齿数按实际传动比校核。");
            Row(g,"ratio_distribution_factor","高速/低速级分配系数 λ",1.31,"—","自动分配齿数时使用");Row(g,"speed_tolerance","输出转速允许偏差",0.05,"比例","0.05 表示 5%");
            Row(g,"high_pinion_teeth","高速级小齿轮齿数",29,"齿","");Row(g,"high_wheel_teeth","高速级大齿轮齿数",114,"齿","");
            Row(g,"low_pinion_teeth","低速级小齿轮齿数",38,"齿","");Row(g,"low_wheel_teeth","低速级大齿轮齿数",114,"齿","");}
        void BuildGear(int pageIndex,string prefix,TextBox summary){var tabs=new TabControl{Dock=DockStyle.Fill};var parameters=new TabPage("材料、几何与载荷系数");var computed=new TabPage("当前校核结果");
            tabs.TabPages.Add(parameters);tabs.TabPages.Add(computed);pages[pageIndex].Controls.Add(tabs);
            var g=InputGrid(parameters,"高速级和低速级分别进行齿面接触强度与齿根弯曲强度初算。许用应力需计入寿命和安全修正。Kv、KHα、KFα、KHβ、KFβ 请查图表后填写。");computed.Controls.Add(summary);
            bool high=prefix=="high";
            GearTypeRow(g,prefix+"_gear_type");
            Row(g,prefix+"_helix_angle","小齿轮螺旋角",0.0,"°","正负号表示旋向；配对大齿轮自动取反，直齿填 0");
            Row(g,prefix+"_module","法面标准模数",high?1.25:1.5,"mm","从标准模数系列中取值");
            Row(g,prefix+"_pinion_width","小齿轮齿宽",high?40.0:62.0,"mm","进入原生模型");Row(g,prefix+"_wheel_width","大齿轮齿宽",high?36.25:57.0,"mm","进入原生模型");
            MaterialRow(g,prefix+"_gear_material","齿轮副材料",MaterialCatalog.Gear);
            Row(g,prefix+"_contact_allow","许用接触应力",600.0,"MPa","人工确认材料依据");Row(g,prefix+"_bend_allow","许用弯曲应力",180.0,"MPa","两齿轮当前采用同一许用值");
            Row(g,prefix+"_kv","动载系数 Kv",1.2,"—","教材动载系数图");Row(g,prefix+"_kha","接触齿间系数 KHα",1.0,"—","参考齿间载荷表");
            Row(g,prefix+"_kfa","弯曲齿间系数 KFα",1.0,"—","参考齿间载荷表");Row(g,prefix+"_khb","接触齿向系数 KHβ",1.0,"—","参考齿向载荷表");
            Row(g,prefix+"_kfb","弯曲齿向系数 KFβ",1.0,"—","参考齿向载荷图");}
        void BuildShafts(){Columns(shaftResults,"轴","功率 / kW","转速 / r/min","转矩 / N·mm","轴承","L10h / h","危险截面 / MPa");
            pages[5].Controls.Add(shaftResults);pages[5].Controls.Add(Note("三轴以参考装配的轴颈、轴承和键为初始结构。按两级齿轮力、简支梁和最不利同向载荷初算中间轴；输入/输出联轴器初选、润滑建议见计算过程。",72));
            var table=InputGrid(pages[5],""); // allowable values above the read-only results
            table.Dock=DockStyle.Top;table.Height=180;
            MaterialRow(table,"shaft_material","三轴材料",MaterialCatalog.Shaft);
            Row(table,"shaft_allow_mpa","轴许用弯扭应力",60.0,"MPa","人工确认材料与安全系数");
            MaterialRow(table,"key_material","平键材料",MaterialCatalog.Key);
            Row(table,"key_allow_mpa","键许用挤压应力",100.0,"MPa","人工确认键材与轮毂材料");

        }
        void MaterialRow(DataGridView grid,string key,string label,string use) {
            int row=grid.Rows.Add(label,null,"—","可选预设材料或自定义，空白沿用默认材料。");
            var cell=new MaterialChoiceCell(use,new[]{""},"");
            grid.Rows[row].Cells[1]=cell;
            fields[key]=new Field{Cell=cell,Numeric=false};
        }
        static void SetFieldValue(Field field,object value) {
            var material=field.Cell as MaterialChoiceCell;
            if(material!=null)material.SetValue(value);else field.Cell.Value=value;
        }
        void BuildChecks(){Columns(checks,"校核项目","计算值","许用/目标","结论");
            var tabs=new TabControl{Dock=DockStyle.Fill};var checksPage=new TabPage("逐项校核");var processPage=new TabPage("计算过程与依据");
            checksPage.Controls.Add(checks);processPage.Controls.Add(steps);tabs.TabPages.Add(checksPage);tabs.TabPages.Add(processPage);pages[6].Controls.Add(tabs);
            pages[6].Controls.Add(Note("任一齿轮、三轴、轴承、键、转速或原生装配几何校核未通过时，不能导出或生成模型。计算为初步设计，制造前还需完整工程复核。",62));}
        void BuildGeneration(){var actions=new FlowLayoutPanel{Dock=DockStyle.Top,Height=94,WrapContents=true,Padding=new Padding(0,10,0,0)};
            foreach(var button in new[]{exportButton,generateButton,reportButton,openOutput,openReport})actions.Controls.Add(button);
            pages[7].Controls.Add(generationSummary);pages[7].Controls.Add(Note("保存建模方案将写出 schema v5 参数；生成模型会使用已校核的齿数、模数、齿宽和轴线尺寸。更改高级尺寸后必须重新校核。",65));pages[7].Controls.Add(addMates);pages[7].Controls.Add(actions);
            exportButton.Click+=(s,e)=>Prepare(false);generateButton.Click+=(s,e)=>Prepare(true);reportButton.Click+=(s,e)=>Report();
            openOutput.Click+=(s,e)=>OpenPath(outputFolder);openReport.Click+=(s,e)=>OpenPath(reportFile);}
        void SelectPage(int index){if(index<0)return;for(int i=0;i<pages.Length;i++)pages[i].Visible=i==index;heading.Text=(index+1)+" / 8  "+Titles[index];pages[index].BringToFront();}
        void MarkDirty(){dirty=true;design=null;modelFile=null;outputFolder=null;reportFile=null;shaftResults.Rows.Clear();checks.Rows.Clear();steps.Clear();highSummary.Clear();lowSummary.Clear();
            generationSummary.Text="设计参数已修改，请重新计算与校核。";status.Text="参数已修改，旧结果已失效。";UpdateActions();}
        void UpdateActions(){bool ready=!dirty&&design!=null&&Flag(design,"system_checks_passed")&&!busy;
            autoButton.Enabled=calculateButton.Enabled=!busy;exportButton.Enabled=generateButton.Enabled=reportButton.Enabled=ready;
            openOutput.Enabled=!busy&&Directory.Exists(outputFolder);openReport.Enabled=!busy&&File.Exists(reportFile);}
        void SetBusy(bool value,string message){busy=value;navigation.Enabled=content.Enabled=toolbar.Enabled=footer.Enabled=!value;status.Text=message;status.Update();UpdateActions();}
        static Dictionary<string,object> Map(object value){return value as Dictionary<string,object>??new Dictionary<string,object>();}
        static object Get(Dictionary<string,object> value,string key){object found;return value!=null&&value.TryGetValue(key,out found)?found:null;}
        static bool Flag(Dictionary<string,object> value,string key){return Object.Equals(Get(value,key),true);}
        static string Display(object value){if(value==null)return "—";if(value is double||value is float||value is decimal)return Convert.ToDouble(value).ToString("0.###",CultureInfo.CurrentCulture);
            if(value is IList)return String.Join(" ~ ",((IList)value).Cast<object>().Select(Display).ToArray());
            return Convert.ToString(value,CultureInfo.CurrentCulture);}
        Dictionary<string,object> Collect(){foreach(var grid in inputs)grid.EndEdit();var result=new Dictionary<string,object>();
            foreach(var item in fields){var value=item.Value.Cell.Value;if(!item.Value.Numeric){result[item.Key]=Convert.ToString(value).Trim();continue;}
                double number;string raw=Convert.ToString(value,CultureInfo.CurrentCulture);
                if(!Double.TryParse(raw,NumberStyles.Float,CultureInfo.CurrentCulture,out number)&&!Double.TryParse(raw,NumberStyles.Float,CultureInfo.InvariantCulture,out number))throw new ArgumentException(item.Key+" 请输入数值。");
                result[item.Key]=number;}return result;}
        Dictionary<string,object> Project(){if(String.IsNullOrWhiteSpace(projectName.Text))throw new ArgumentException("项目名称不能为空。");
            return new Dictionary<string,object>{{"name",projectName.Text.Trim()},{"designer",designer.Text.Trim()},{"number",projectNumber.Text.Trim()},{"notes",notes.Text},{"date",DateTime.Now.ToString("yyyy-MM-dd")}};}
        string WorkFolder(){if(workFolder==null){workFolder=Path.Combine(Generator.ProjectRoot(),"design_projects",DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")+"_two_"+Guid.NewGuid().ToString("N").Substring(0,6));}return workFolder;}
        Dictionary<string,object> Request(string action){return RequestForOutput(action,null);}
        Dictionary<string,object> RequestForOutput(string action,string outputDirectory){return new Dictionary<string,object>{{"action",action},{"project",Project()},{"parameters",Collect()},{"output_dir",outputDirectory??WorkFolder()}};}
        void RunBridge(Dictionary<string,object> request,string message,Action<Dictionary<string,object>> completed){if(busy)return;SetBusy(true,message);
            var worker=new BackgroundWorker();worker.DoWork+=(s,e)=>e.Result=bridge(request);worker.RunWorkerCompleted+=(s,e)=>{SetBusy(false,"操作完成。");
                try{if(e.Error!=null)throw e.Error;var response=(Dictionary<string,object>)e.Result;if(!Flag(response,"ok"))throw new ArgumentException(Display(Get(response,"error")));completed(response);}
                catch(Exception error){Error(error);}finally{worker.Dispose();UpdateActions();}};worker.RunWorkerAsync();}
        void Compute(bool automatic){try{RunBridge(Request(automatic?"two_auto":"two_calculate"),automatic?"正在分配两级齿数…":"正在计算两级齿轮、三轴和装配…",response=>{
            ApplyDesign(Map(Get(response,"design")));
            if(automatic)navigation.SelectedIndex=2;
            status.Text=Flag(design,"system_checks_passed")?"结果已更新，初步系统校核通过。":"结果已更新，请在第 7 步“计算与校核”查看未通过项。";});}catch(Exception error){Error(error);}}
        void ApplyDesign(Dictionary<string,object> value,bool keepArtifacts=false){if(value.Count==0)throw new ArgumentException("计算结果为空。");applying=true;try{
                design=value;foreach(var item in Map(Get(value,"inputs"))){Field field;if(fields.TryGetValue(item.Key,out field))SetFieldValue(field,item.Value);}
                dirty=false;if(!keepArtifacts){modelFile=null;outputFolder=null;reportFile=null;}Render();RenderArtifacts();}finally{applying=false;}UpdateActions();}
        void RenderArtifacts(){if(File.Exists(modelFile))generationSummary.AppendText(System.Environment.NewLine+"建模参数："+modelFile);
            if(Directory.Exists(outputFolder))generationSummary.AppendText(System.Environment.NewLine+"已生成模型："+outputFolder);
            if(File.Exists(reportFile))generationSummary.AppendText(System.Environment.NewLine+"设计说明书："+reportFile);}
        void Render(){var ratio=Map(Get(design,"ratio"));var values=Map(Get(design,"inputs"));
            foreach(var pair in new[]{new{Stage="high_stage",Label="高速级",Box=highSummary},new{Stage="low_stage",Label="低速级",Box=lowSummary}}){
                var stage=Map(Get(design,pair.Stage));var results=Map(Get(stage,"results"));var text=new System.Text.StringBuilder();
                string prefix=pair.Stage=="high_stage"?"high":"low";
                text.AppendLine(pair.Label+(Convert.ToString(Get(Map(Get(design,"inputs")),prefix+"_gear_type"))=="helical"?"斜齿副":"直齿副"))
                    .AppendLine("小齿轮螺旋角："+Display(Get(Map(Get(design,"inputs")),prefix+"_helix_angle"))+"°")
                    .AppendLine("分度圆："+Display(Get(results,"dPinion"))+" / "+Display(Get(results,"dGear"))+" mm")
                    .AppendLine("中心距："+Display(Get(results,"centerDistance"))+" mm")
                    .AppendLine("切向载荷："+Display(Get(results,"tangentialForce"))+" N")
                    .AppendLine("齿面接触应力："+Display(Get(results,"contactStress"))+" MPa")
                    .AppendLine("小齿轮弯曲应力："+Display(Get(results,"pinionBendStress"))+" MPa")
                    .AppendLine("大齿轮弯曲应力："+Display(Get(results,"gearBendStress"))+" MPa")
                    .AppendLine("节线速度："+Display(Get(results,"pitchLineVelocity"))+" m/s")
                    .AppendLine("初步齿轮校核："+(Flag(stage,"gear_checks_passed")?"满足":"需调整"));pair.Box.Text=text.ToString();}
            shaftResults.Rows.Clear();foreach(var item in Map(Get(design,"shafts"))){var shaft=Map(item.Value);
                shaftResults.Rows.Add(ShaftLabel(item.Key),Display(Get(shaft,"power_kw")),Display(Get(shaft,"speed_rpm")),Display(Get(shaft,"torque_nmm")),
                    Display(Get(shaft,"bearing")),Display(Get(shaft,"bearing_life_h")),Display(Get(shaft,"shaft_stress_mpa")));}
            checks.Rows.Clear();foreach(var item in Map(Get(design,"checks"))){var check=Map(item.Value);object limit=Get(check,"limit")??Get(check,"minimum")??Get(check,"limits");
                int row=checks.Rows.Add(CheckLabel(item.Key),Display(Get(check,"value")),Display(limit),Flag(check,"passed")?"满足":"需调整");
                checks.Rows[row].Cells[3].Style.ForeColor=Flag(check,"passed")?Color.DarkGreen:Color.Firebrick;}
            var process=new System.Text.StringBuilder();foreach(object item in Items(Get(design,"calculation_steps"))){var step=Map(item);
                process.AppendLine(Display(Get(step,"title"))).AppendLine(Display(Get(step,"text"))).AppendLine();}
            process.AppendLine("适用范围与复核项");foreach(object item in Items(Get(design,"limitations")))process.AppendLine("• "+Display(item));steps.Text=process.ToString();
            generationSummary.Text="减速器输入功率/转速："+Display(Get(values,"input_power_kw"))+" kW / "+Display(Get(values,"input_speed_rpm"))+" r/min。"+System.Environment.NewLine+
                "高速级/低速级实际传动比："+Display(Get(ratio,"high"))+" / "+Display(Get(ratio,"low"))+"；输出转速 "+Display(Get(ratio,"output_speed_rpm"))+" r/min。"+System.Environment.NewLine+
                "校核状态："+(Flag(design,"system_checks_passed")?"初步通过，可导出":"有未通过项，请查看第 7 步")+System.Environment.NewLine+
                "模型基于参考件的三轴、轴承和箱体结构；改变轴颈、轴承或键规格需重新校核。";}
        static IEnumerable Items(object value){return value as IEnumerable??new object[0];}
        static string ShaftLabel(string key){switch(key){case "input":return "高速轴";case "intermediate":return "中间轴";case "output":return "低速轴";default:return key;}}
        static string CheckLabel(string key){if(key=="output_speed")return "输出转速偏差";if(key=="native_geometry")return "原生装配几何";
            string[] parts=key.Split('_');string prefix=parts.Length>0?parts[0]:"";string label=prefix=="high"?"高速级":prefix=="low"?"低速级":ShaftLabel(prefix);
            if(key.EndsWith("contact_strength"))return label+"接触强度";if(key.EndsWith("pinion_bending"))return label+"小齿轮弯曲";if(key.EndsWith("gear_bending"))return label+"大齿轮弯曲";
            if(key.EndsWith("bearing_life"))return label+"轴承寿命";if(key.EndsWith("bearing_speed"))return label+"轴承限速";
            if(key.EndsWith("shaft_stress"))return label+"轴弯扭应力";if(key.EndsWith("shaft_diameter"))return label+"轴径初算";
            if(key.EndsWith("coupling"))return label+"联轴器初选";
            if(key.EndsWith("bearing_geometry"))return label+"轴承跨距";if(key.EndsWith("bearing_catalog"))return label+"轴承型号";
            if(key.Contains("_key_"))return label+"键压强 "+parts[parts.Length-1];
            if(key.EndsWith("center_distance"))return label+"中心距";if(key.EndsWith("design_module"))return label+"模数";
            if(key.EndsWith("coefficient_domain"))return label+"公式适用范围";return key;}
        void RequireCurrent(){foreach(var grid in inputs)grid.EndEdit();if(dirty||design==null)throw new InvalidOperationException("参数已更改或尚未计算，请先重新计算与校核。");
            if(!Flag(design,"system_checks_passed"))throw new InvalidOperationException("当前存在未通过的校核，请查看第 7 步。");}
        void Prepare(bool generate){if(busy)return;try{RequireCurrent();
            string modelRoot=null;
            if(generate) {
                if(app==null)throw new InvalidOperationException("请打开 SolidWorks 2024 后重新进入二级设计窗口生成原生模型。");
                modelRoot=Generator.ChooseOutputDirectory(this,"选择模型保存目录。零件和总装会保存在新的子文件夹中。",Path.Combine(Generator.ProjectRoot(),"output"));
                if(modelRoot==null)return;
            }
            RunBridge(Request("two_export"),"正在保存已校核的二级建模方案…",response=>{
            ApplyDesign(Map(Get(response,"design")),true);modelFile=Convert.ToString(Get(response,"model_parameters_file"));
            if(String.IsNullOrWhiteSpace(modelFile)||!File.Exists(modelFile))throw new FileNotFoundException("模型参数未保存。");
            Generator.Validate(json.Deserialize<Parameters>(File.ReadAllText(modelFile)));
            Render();RenderArtifacts();navigation.SelectedIndex=7;
            if(generate)GenerateNative(modelRoot);else status.Text="二级建模方案已保存。";});}catch(Exception error){Error(error);}}
        bool closeAfterGeneration;
        void GenerateNative(string modelRoot){
            RequireCurrent();SetBusy(true,"正在生成三轴、两级齿轮和原生总装…");try{
                outputFolder=Generator.Generate(app,modelFile,modelRoot,addMates.Checked);
                Render();RenderArtifacts();status.Text=addMates.Checked?"二级减速器零件与配合总装已生成。":"二级总装已生成，零件按计算位置固定。";
            }catch(OperationCanceledException ex) {
                status.Text=ex.Message;
            }finally {
                SetBusy(false,status.Text);
                if(closeAfterGeneration) {closeAfterGeneration=false;BeginInvoke((Action)Close);}
            }}
        void Report(){if(busy)return;try{RequireCurrent();
            string root=Generator.ChooseOutputDirectory(this,"选择说明书保存目录。说明书会保存在新的子文件夹中。",Path.Combine(Generator.ProjectRoot(),"design_projects"));
            if(root==null)return;
            RunBridge(RequestForOutput("two_report",Generator.ReportOutputDirectory(root)),"正在生成二级设计说明书…",response=>{
            ApplyDesign(Map(Get(response,"design")),true);modelFile=Convert.ToString(Get(response,"model_parameters_file"));reportFile=Convert.ToString(Get(response,"report_file"));
            if(!File.Exists(reportFile))throw new FileNotFoundException("未找到设计说明书。");
            Render();RenderArtifacts();status.Text="说明书已生成。";});}catch(Exception error){Error(error);}}
        void SaveProject(){try{var snapshot=new Dictionary<string,object>{{"format","two_stage_reducer_design_project"},{"version",1},{"project",Project()},{"parameters",Collect()}};
            using(var dialog=new SaveFileDialog{Title="保存二级减速器设计项目",Filter="设计项目 (*.json)|*.json",FileName="二级减速器设计项目.json"})
                if(dialog.ShowDialog(this)==DialogResult.OK){File.WriteAllText(dialog.FileName,json.Serialize(snapshot));status.Text="项目已保存。";}}
            catch(Exception error){Error(error);}}
        void OpenProject(){try{using(var dialog=new OpenFileDialog{Title="打开二级减速器设计项目",Filter="设计项目 (*.json)|*.json"})if(dialog.ShowDialog(this)==DialogResult.OK)
            LoadProjectSnapshot(json.Deserialize<Dictionary<string,object>>(File.ReadAllText(dialog.FileName)));}
            catch(Exception error){Error(error);}}
        void LoadProjectSnapshot(Dictionary<string,object> snapshot){
            if(Convert.ToString(Get(snapshot,"format"))!="two_stage_reducer_design_project")throw new ArgumentException("该文件不是二级减速器设计项目。");
            var values=Map(Get(snapshot,"parameters"));if(values.Count==0)values=Map(Get(Map(Get(snapshot,"design")),"inputs"));
            if(values.Count==0)throw new ArgumentException("该文件没有可读取的设计方案。");
            if(!values.ContainsKey("input_power_kw")&&values.ContainsKey("working_power_kw")){
                double bearing=Convert.ToDouble(Get(values,"bearing_efficiency")??0.99),gear=Convert.ToDouble(Get(values,"gear_efficiency")??0.98),machine=Convert.ToDouble(Get(values,"machine_efficiency")??0.96);
                if(bearing<=0||gear<=0||machine<=0)throw new ArgumentException("旧项目效率无效，无法换算输入功率。");
                values["input_power_kw"]=Convert.ToDouble(Get(values,"working_power_kw"))/(Math.Pow(bearing,3)*Math.Pow(gear,2)*machine);}
            if(!values.ContainsKey("input_speed_rpm")&&values.ContainsKey("motor_speed_rpm"))values["input_speed_rpm"]=values["motor_speed_rpm"];
            var project=Map(Get(snapshot,"project"));applying=true;try{
                projectName.Text=Convert.ToString(Get(project,"name"));designer.Text=Convert.ToString(Get(project,"designer"));
                projectNumber.Text=Convert.ToString(Get(project,"number"));notes.Text=Convert.ToString(Get(project,"notes"));
                foreach(string key in new[]{"high_gear_material","low_gear_material","shaft_material","key_material"})
                    SetFieldValue(fields[key],Get(values,key));
                foreach(var item in values){Field field;if(fields.TryGetValue(item.Key,out field))SetFieldValue(field,item.Value);}}
            finally{applying=false;}workFolder=null;MarkDirty();navigation.SelectedIndex=1;status.Text="项目已载入；请重新计算与校核。";}
        void OpenPath(string path){try{if(String.IsNullOrEmpty(path)||(!File.Exists(path)&&!Directory.Exists(path)))throw new FileNotFoundException("结果尚未生成。");
            Process.Start(new ProcessStartInfo(path){UseShellExecute=true});}catch(Exception error){Error(error);}}
        void Error(Exception error){status.Text="操作未完成："+error.Message;MessageBox.Show(this,error.Message,"二级减速器设计",MessageBoxButtons.OK,MessageBoxIcon.Information);}
    }
}
