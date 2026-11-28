using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;

namespace ReducerSW {
    // Engineering calculations run outside the COM thread. All SOLIDWORKS
    // calls remain in the form's STA completion callback.
    public sealed class DesignWizard : Form {
        sealed class Field {
            public string Key, Label, Unit; public DataGridViewCell Cell;
            public bool Numeric, AutoInput, ModelApplied=true;
        }
        sealed class Choice { public string Value {get;set;} public string Text {get;set;} }
        readonly SldWorks app;
        readonly Func<Dictionary<string,object>,Dictionary<string,object>> bridge;
        readonly JavaScriptSerializer json=new JavaScriptSerializer{MaxJsonLength=Int32.MaxValue};
        readonly Dictionary<string,Field> fields=new Dictionary<string,Field>();
        readonly List<DataGridView> editableGrids=new List<DataGridView>();
        readonly ListBox navigation=new ListBox{Dock=DockStyle.Fill,BorderStyle=BorderStyle.None,ItemHeight=47,IntegralHeight=false};
        readonly Panel content=new Panel{Dock=DockStyle.Fill};
        readonly Panel[] pages=new Panel[8];
        readonly Label heading=new Label{Dock=DockStyle.Top,Height=51,Padding=new Padding(15,12,0,0),Font=new Font("Microsoft YaHei UI",15,FontStyle.Bold)};
        readonly Label status=new Label{Dock=DockStyle.Bottom,Height=46,Padding=new Padding(12,9,12,4),BackColor=Color.FromArgb(235,241,247)};
        readonly FlowLayoutPanel toolbar=ActionBar(DockStyle.Top);
        readonly FlowLayoutPanel footer=ActionBar(DockStyle.Bottom);
        readonly TextBox projectName=new TextBox{Text="一级圆柱齿轮减速器设计"}, designer=new TextBox(), projectNumber=new TextBox(), projectDate=new TextBox{Text=DateTime.Now.ToString("yyyy-MM-dd")}, notes=new TextBox{Multiline=true,Height=110,ScrollBars=ScrollBars.Vertical};
        readonly DataGridView results=Grid(true), checks=Grid(true), layoutResults=Grid(true);
        readonly TextBox calculationProcess=new TextBox{Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,BackColor=Color.White,Font=new Font("Microsoft YaHei UI",10),Margin=new Padding(8)};
        readonly TextBox generationSummary=new TextBox{Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,BackColor=Color.White};
        readonly TextBox conclusion=new TextBox{Dock=DockStyle.Bottom,Height=135,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,BackColor=Color.White};
        readonly TextBox outputLocation=new TextBox{Dock=DockStyle.Top,ReadOnly=true};
        readonly CheckBox advanced=new CheckBox{Text="生成前打开详细尺寸编辑器（更改后需重新复核工程校核）",AutoSize=true};
        readonly CheckBox linkedGeometry=new CheckBox{Text="按轴承、齿宽与轴向尺寸链联动轴段（显示调整项）",Checked=true,Dock=DockStyle.Top,Height=30};
        readonly Button autoButton=ActionBarButton("自动设计方案",148), calculateButton=ActionBarButton("重新计算与校核",148);
        readonly Button generateButton=new Button{Text="生成 SOLIDWORKS 模型",Width=205,Height=32}, exportButton=new Button{Text="保存建模方案",Width=130,Height=32}, reportButton=new Button{Text="生成设计说明书",Width=145,Height=32};
        readonly Button openOutput=new Button{Text="打开模型目录",Width=130,Height=32}, openReport=new Button{Text="打开说明书",Width=120,Height=32};
        Dictionary<string,object> design, schema=new Dictionary<string,object>();
        bool applying, dirty=true, busy, schemaReady;
        string modelParametersFile, outputFolder, reportFile, workFolder;
        static readonly string[] Titles={"项目信息","工况输入","传动设计","传动结果","轴系与支承","箱体与附件","强度与寿命","生成模型"};

        readonly Dictionary<string,object> initialProject;
        public DesignWizard(SldWorks application):this(application,DesignBridge.Call) {}
        public DesignWizard(SldWorks application,Dictionary<string,object> project):this(application) {initialProject=project;}
        internal DesignWizard(SldWorks application,Func<Dictionary<string,object>,Dictionary<string,object>> call) {
            SuspendLayout();
            ToolbarIcons.ApplyWindowIcon(this);
            app=application;bridge=call;
            Text="一级减速器 — 完整设计流程";Width=1180;Height=825;MinimumSize=new Size(970,650);
            StartPosition=FormStartPosition.CenterScreen;Font=new Font("Microsoft YaHei UI",10);BackColor=Color.White;
            var left=new Panel{Dock=DockStyle.Left,Width=190,Padding=new Padding(8,15,8,8),BackColor=Color.FromArgb(240,244,249)};
            for(int i=0;i<Titles.Length;i++)navigation.Items.Add((i+1)+".  "+Titles[i]);
            navigation.SelectedIndexChanged+=(s,e)=>SelectPage(navigation.SelectedIndex);left.Controls.Add(navigation);
            Controls.Add(content);Controls.Add(left);Controls.Add(footer);Controls.Add(status);Controls.Add(toolbar);Controls.Add(heading);
            AddButton(toolbar,"打开项目",OpenProject);AddButton(toolbar,"保存项目",SaveProject);
            toolbar.Controls.Add(autoButton);toolbar.Controls.Add(calculateButton);
            AddButton(toolbar,"设计参考图表",()=>ShowReferences(this,Error));
            autoButton.Click+=(s,e)=>Compute(true);calculateButton.Click+=(s,e)=>Compute(false);
            AddButton(footer,"下一步 →",()=>navigation.SelectedIndex=Math.Min(7,navigation.SelectedIndex+1));
            AddButton(footer,"← 上一步",()=>navigation.SelectedIndex=Math.Max(0,navigation.SelectedIndex-1));
            for(int i=0;i<pages.Length;i++){pages[i]=new Panel{Dock=DockStyle.Fill,Padding=new Padding(14,6,14,12),Visible=false};content.Controls.Add(pages[i]);}
            BuildProject();BuildOperating();BuildGears();BuildResults();BuildShafts();BuildHousing();BuildChecks();BuildGeneration();
            navigation.SelectedIndex=0;UpdateActions();status.Text="填写工况后点击“自动设计方案”，也可编辑已有方案并重新校核。";
            Shown+=(s,e)=>{
                // Apply after font/DPI scaling so the footer stays above the
                // taskbar on the current display's usable work area.
                var area=Screen.FromControl(this).WorkingArea;
                MinimumSize=new Size(Math.Min(MinimumSize.Width,area.Width),Math.Min(MinimumSize.Height,area.Height));
                Width=Math.Min(Width,area.Width);Height=Math.Min(Height,area.Height);
                Location=new Point(area.Left+(area.Width-Width)/2,area.Top+(area.Height-Height)/2);
                LoadSchema();
            };
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

        internal static DataGridView Grid(bool readOnly) {
            var grid=new DataGridView{Dock=DockStyle.Fill,ReadOnly=readOnly,AllowUserToAddRows=false,AllowUserToDeleteRows=false,
                AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,RowHeadersVisible=false,BackgroundColor=Color.White,
                BorderStyle=BorderStyle.FixedSingle,SelectionMode=DataGridViewSelectionMode.CellSelect,AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells};
            grid.DefaultCellStyle.WrapMode=DataGridViewTriState.True;
            grid.DefaultCellStyle.Padding=new Padding(3,4,3,4);
            grid.RowTemplate.MinimumHeight=30;
            grid.ColumnHeadersHeightSizeMode=DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            grid.ColumnHeadersDefaultCellStyle.Padding=new Padding(3,5,3,5);
            grid.CellPainting+=PaintCoefficientLabel;
            return grid;
        }
        static void PaintCoefficientLabel(object sender,DataGridViewCellPaintingEventArgs e) {
            if(e.RowIndex<0||e.ColumnIndex!=0) return;
            var label=e.FormattedValue as string;
            if(String.IsNullOrEmpty(label)) return;
            int symbolStart=label.LastIndexOf(' ')+1;
            switch(label.Substring(symbolStart)) {
                case "A0": case "KA": case "Kv": case "KH": case "KF":
                case "KHα": case "KFα": case "KHβ": case "KFβ":
                    break;
                default:
                    return;
            }

            string mainText=label.Substring(0,symbolStart+1);
            string subscript=label.Substring(symbolStart+1);
            var style=e.CellStyle;
            var font=style.Font;
            const TextFormatFlags flags=TextFormatFlags.NoPadding|TextFormatFlags.NoPrefix|
                TextFormatFlags.SingleLine|TextFormatFlags.PreserveGraphicsClipping;
            using(var subscriptFont=new Font(font.FontFamily,font.Size*0.75f,font.Style,font.Unit)) {
                Size mainSize=TextRenderer.MeasureText(e.Graphics,mainText,font,Size.Empty,flags);
                Size subscriptSize=TextRenderer.MeasureText(e.Graphics,subscript,subscriptFont,Size.Empty,flags);
                Rectangle bounds=e.CellBounds;
                bounds.X+=style.Padding.Left+2;
                bounds.Width-=style.Padding.Horizontal+4;
                bounds.Height-=1;
                int drop=Math.Max(1,mainSize.Height/8);
                int textHeight=mainSize.Height+drop;
                if(mainSize.Width+subscriptSize.Width>bounds.Width||textHeight>bounds.Height) return;

                e.Paint(e.ClipBounds,e.PaintParts&~DataGridViewPaintParts.ContentForeground);
                var color=(e.State&DataGridViewElementStates.Selected)!=0?style.SelectionForeColor:style.ForeColor;
                int y=bounds.Y+Math.Max(0,(bounds.Height-textHeight+style.Padding.Top-style.Padding.Bottom)/2);
                var graphicsState=e.Graphics.Save();
                try {
                    e.Graphics.SetClip(Rectangle.Intersect(bounds,e.ClipBounds),System.Drawing.Drawing2D.CombineMode.Intersect);
                    TextRenderer.DrawText(e.Graphics,mainText,font,new Point(bounds.X,y),color,flags);
                    TextRenderer.DrawText(e.Graphics,subscript,subscriptFont,
                        new Point(bounds.X+mainSize.Width,y+textHeight-subscriptSize.Height),color,flags);
                } finally {
                    e.Graphics.Restore(graphicsState);
                }
                e.Handled=true;
            }
        }
        static void Columns(DataGridView grid,params string[] titles){foreach(string title in titles)grid.Columns.Add(title,title);}
        internal static FlowLayoutPanel ActionBar(DockStyle dock) {
            return new FlowLayoutPanel {
                Dock=dock,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Padding=new Padding(8,4,8,4),
                FlowDirection=dock==DockStyle.Bottom?FlowDirection.RightToLeft:FlowDirection.LeftToRight
            };
        }
        internal static Button ActionBarButton(string text,int width) {
            return new Button {Text=text,Size=new Size(width,38)};
        }
        internal static void AddButton(FlowLayoutPanel parent,string text,Action action) {
            var button=ActionBarButton(text,parent.Dock==DockStyle.Top?148:100);
            button.Click+=(s,e)=>action();
            parent.Controls.Add(button);
        }
        static Label Note(string text,int height){return new Label{Dock=DockStyle.Top,Height=height,Padding=new Padding(2,5,2,4),Text=text,ForeColor=Color.FromArgb(65,75,88)};}
        DataGridView ParameterGrid(Panel parent,string note) {
            var grid=Grid(false);Columns(grid,"参数","数值 / 选项","单位","说明");
            grid.Columns[0].ReadOnly=true;grid.Columns[2].ReadOnly=true;grid.Columns[3].ReadOnly=true;
            grid.Columns[0].FillWeight=27;grid.Columns[1].FillWeight=24;grid.Columns[2].FillWeight=12;grid.Columns[3].FillWeight=50;
            grid.DefaultCellStyle.WrapMode=DataGridViewTriState.True;
            parent.Controls.Add(grid);if(!String.IsNullOrEmpty(note))parent.Controls.Add(Note(note,57));Watch(grid);return grid;
        }
        void Watch(DataGridView grid){editableGrids.Add(grid);grid.CellValueChanged+=(s,e)=>{if(applying||e.RowIndex<0||e.ColumnIndex<0)return;var material=grid[e.ColumnIndex,e.RowIndex] as MaterialChoiceCell;if(material==null||!material.HandleChoice(this))MarkDirty();};grid.CurrentCellDirtyStateChanged+=(s,e)=>{if(grid.IsCurrentCellDirty)grid.CommitEdit(DataGridViewDataErrorContexts.Commit);};grid.DataError+=(s,e)=>{e.ThrowException=false;status.Text="请选择有效选项，或检查输入的数值。";};}
        void Bind(DataGridViewCell cell,string key,string label,string unit,bool numeric,bool autoInput){fields[key]=new Field{Key=key,Label=label,Unit=unit,Cell=cell,Numeric=numeric,AutoInput=autoInput};}
        void FieldRow(DataGridView grid,string key,string label,object value,string unit,string help,bool autoInput){int row=grid.Rows.Add(label,value,unit,help);Bind(grid.Rows[row].Cells[1],key,label,unit,!(value is string),autoInput);}
        void ChoiceRow(DataGridView grid,string key,string label,string value,string[] values,bool autoInput){int row=grid.Rows.Add(label,value,"—",
            key=="driverRotation"?"转向从轴外伸端朝箱内观察。":"");Bind(grid.Rows[row].Cells[1],key,label,"",false,autoInput);SetChoices(fields[key],values,value);}
        void SetChoices(Field field,IEnumerable values,string value) {
            var choices=new List<Choice>();foreach(object item in values){string raw=Convert.ToString(item,CultureInfo.InvariantCulture);choices.Add(new Choice{Value=raw,Text=ChoiceLabel(raw)});}
            if(!choices.Any(c=>c.Value==value))choices.Add(new Choice{Value=value,Text=ChoiceLabel(value)});
            var old=field.Cell;var combo=new DesignChoiceCell{DataSource=choices,DisplayMember="Text",ValueMember="Value"};
            old.DataGridView.Rows[old.RowIndex].Cells[old.ColumnIndex]=combo;combo.Value=value;field.Cell=combo;
        }
        static string ChoiceLabel(string value){switch(value){case "spur":return "直齿圆柱齿轮";case "helical":return "斜齿圆柱齿轮";case "bending":return "按齿根弯曲强度设计";case "contact":return "按齿面接触强度设计";case "clockwise_from_input":return "主动轮顺时针";case "counterclockwise_from_input":return "主动轮逆时针";case "pinion_left_gear_right":return "主动左旋、从动右旋";case "pinion_right_gear_left":return "主动右旋、从动左旋";case "single":return "单键";case "double":return "双键";case "S45":return "45 钢";case "mid":return "一般载荷";case "best":return "有利工况";case "worst":return "不利工况";case "manual":return "人工查图输入";case "symmetrical":return "对称布置";default:return value;}}
        void MaterialRow(DataGridView grid,string key,string label,string value,string use,params string[] choices) {
            int row=grid.Rows.Add(label,null,"—","可选预设材料，也可自定义。数值请在本页手动填写。");
            var cell=new MaterialChoiceCell(use,choices,value);
            grid.Rows[row].Cells[1]=cell;
            Bind(cell,key,label,"",false,true);
        }
        void BuildProject(){var table=new TableLayoutPanel{Dock=DockStyle.Top,AutoSize=true,ColumnCount=2,Padding=new Padding(15)};table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,110));table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            Control[] values={projectName,designer,projectNumber,projectDate,notes};string[] labels={"项目名称","设计者","项目编号","设计日期","项目备注"};for(int i=0;i<values.Length;i++){values[i].Dock=DockStyle.Fill;table.Controls.Add(new Label{Text=labels[i],AutoSize=true,Padding=new Padding(0,5,0,0)},0,i);table.Controls.Add(values[i],1,i);}
            pages[0].Controls.Add(table);pages[0].Controls.Add(Note("项目可独立保存和重新打开。先完成工况与方案设计，再校核并生成模型。",55));}
        void BuildOperating(){var g=ParameterGrid(pages[1],"自动设计根据目标输出转速选择齿轮、轴、轴承及键。自动选型仅在明确点击按钮时修改方案。使用系数由设计者按工况确认。");
            FieldRow(g,"P","输入功率",1.2,"kW","输入轴有效功率",true);FieldRow(g,"n1","输入转速",1450.0,"r/min","输入轴转速",true);FieldRow(g,"target_speed","目标输出转速",480.0,"r/min","自动选型的目标转速",true);
            FieldRow(g,"eta","总效率",0.96,"—","输出功率按效率修正",true);FieldRow(g,"lifeYears","工作年限",8.0,"年","",true);FieldRow(g,"daysPerYear","每年工作天数",300.0,"天/年","",true);FieldRow(g,"hoursPerDay","每天工作时间",8.0,"h/天","",true);FieldRow(g,"applicationFactor","使用系数 KA",1.0,"—","人工按工况查取，必须大于0",true);
            FieldRow(g,"speed_tolerance","目标转速允许偏差",0.02,"比例","0.02 表示 2%；用于自动选型",true);FieldRow(g,"max_center_distance","最大中心距",450.0,"mm","自动选型的空间约束",true);}
        void BuildGears(){var g=ParameterGrid(pages[2],"材料许用应力为人工输入的最终值，需包含寿命和安全修正。四个载荷分配系数独立查表填写，默认值不表示已完成查表。");
            ChoiceRow(g,"gearDesignMethod","设计方法","bending",new[]{"bending","contact"},true);ChoiceRow(g,"gearType","齿轮类型","spur",new[]{"spur","helical"},true);
            ChoiceRow(g,"driverRotation","主动轮转向","clockwise_from_input",new[]{"clockwise_from_input","counterclockwise_from_input"},true);ChoiceRow(g,"helixHand","齿轮旋向","pinion_left_gear_right",new[]{"pinion_left_gear_right","pinion_right_gear_left"},true);
            string[] keys={"z1","z2","moduleM","pressureAngle","helixAngle","pinionFaceWidth","gearFaceWidth","pinionHardness","gearHardness","contactAllow","gearContactAllow","pinionBendAllow","gearBendAllow","gearPrecisionGrade","dynamicFactorManual","toothFactorH","toothFactorF","contactFaceFactor","bendingFaceFactor"};
            string[] labels={"小齿轮齿数","大齿轮齿数","法面模数","法面压力角","螺旋角","小齿轮齿宽","大齿轮齿宽","小齿轮硬度","大齿轮硬度","小齿轮许用接触应力","大齿轮许用接触应力","小齿轮许用弯曲应力","大齿轮许用弯曲应力","齿轮精度等级","动载系数 Kv","接触齿间系数 KHα","弯曲齿间系数 KFα","接触齿向系数 KHβ","弯曲齿向系数 KFβ"};
            double[] defaults={17,42,2,20,0,32,24,240,220,600,600,180,180,7,1.2,1,1,1,1};string[] units={"齿","齿","mm","°","°","mm","mm","HBW","HBW","MPa","MPa","MPa","MPa","级","—","—","—","—","—"};
            for(int i=0;i<keys.Length;i++)FieldRow(g,keys[i],labels[i],defaults[i],units[i],"",!(new[]{"z1","z2","moduleM","pinionFaceWidth","gearFaceWidth"}).Contains(keys[i]));
            MaterialRow(g,"pinionGearMaterial","小齿轮材料","40Cr 调质",MaterialCatalog.Gear,"40Cr 调质","45 正火","20CrMnTi 渗碳后淬火","35SiMn 调质","38SiMnMo 调质");MaterialRow(g,"gearMaterial","大齿轮材料","45 正火",MaterialCatalog.Gear,"45 正火","40Cr 调质","20CrMnTi 渗碳后淬火","HT300 正火","ZG340-640 调质");}
        void BuildResults(){Columns(results,"计算项目","结果","单位");var tabs=new TabControl{Dock=DockStyle.Fill};var summary=new TabPage("传动与载荷结果");var process=new TabPage("计算过程与依据");summary.Controls.Add(results);process.Controls.Add(calculationProcess);tabs.TabPages.Add(summary);tabs.TabPages.Add(process);pages[3].Controls.Add(tabs);pages[3].Controls.Add(Note("本页显示最近一次有效计算的结果、公式及校核依据。修改设计参数后须重新计算。",55));}
        void BuildShafts(){var tabs=new TabControl{Dock=DockStyle.Fill};pages[4].Controls.Add(tabs);pages[4].Controls.Add(Note("勾选联动后按当前轴承、齿轮与轴向尺寸链调整相关轴段，计算过程和下方轴向布置结果列出改动；未勾选时保留手工值并逐项校核。键规格按“宽 × 高 × 长”填写。",65));pages[4].Controls.Add(linkedGeometry);linkedGeometry.CheckedChanged+=(s,e)=>{if(!applying)MarkDirty();};
            var supportPage=new TabPage("轴承、材料与平键");var support=new Panel{Dock=DockStyle.Fill};supportPage.Controls.Add(support);tabs.TabPages.Add(supportPage);var g=ParameterGrid(support,"材料与载荷状态记录到说明书；A0、许用弯扭应力及剪应力采用当前数值，请按材料和工况查取后填写。");
            FieldRow(g,"highBearing","高速轴轴承","6202","型号","可输入项目轴承目录中的型号",false);FieldRow(g,"lowBearing","低速轴轴承","6205","型号","",false);
            FieldRow(g,"highKey","高速轴外伸端键","5 x 5 x 18","mm","宽 × 高 × 长",false);FieldRow(g,"lowGearKey","低速轴齿轮键","6 x 6 x 20","mm","",false);FieldRow(g,"lowOuterKey","低速轴外伸端键","5 x 5 x 20","mm","",false);
            foreach(string key in new[]{"highKey","lowGearKey","lowOuterKey"}){ChoiceRow(g,key+"Count",fields[key].Label+"数量","single",new[]{"single","double"},false);FieldRow(g,key+"PressAllow",fields[key].Label+"许用挤压应力",100.0,"MPa","人工确认",true);}
            foreach(string prefix in new[]{"high","low"}){string label=prefix=="high"?"高速轴":"低速轴";MaterialRow(g,prefix+"ShaftMaterial",label+"材料（说明书）","S45",MaterialCatalog.Shaft,"S45");ChoiceRow(g,prefix+"ShaftLoadCondition",label+"载荷状态（说明书）","mid",new[]{"mid"},true);FieldRow(g,prefix+"AllowStress",label+"许用弯扭应力",60.0,"MPa","人工确认",true);FieldRow(g,prefix+"TauAllow",label+"许用剪应力",35.0,"MPa","人工确认",true);FieldRow(g,prefix+"ShaftA0",label+"轴径估算系数 A0",118.0,"—","人工按材料及工况查取",true);}
            BuildShaftGrid(tabs,"high",new double[]{12,15,14,19,29,19,14,15},new double[]{24,37,2,6.5,32,6.5,2,17});BuildShaftGrid(tabs,"low",new double[]{25,28,32,25,22,17},new double[]{25,24,10,15,20,33});}
        void BuildShaftGrid(TabControl tabs,string prefix,double[] diameters,double[] lengths){var page=new TabPage(prefix=="high"?"高速轴 8 段":"低速轴 6 段");var g=Grid(false);Columns(g,"轴段","直径 / mm","长度 / mm","联动说明");g.Columns[0].ReadOnly=true;g.Columns[3].ReadOnly=true;page.Controls.Add(g);tabs.TabPages.Add(page);Watch(g);
            for(int i=0;i<diameters.Length;i++){int segment=i+1;bool bearing=prefix=="high"?(segment==2||segment==8):(segment==1||segment==4);bool gear=prefix=="high"&&segment==5;int row=g.Rows.Add(segment,diameters[i],lengths[i],bearing?"直径由轴承型号决定":gear?"直径和长度由齿轮决定":prefix=="low"&&segment==2?"长度由大齿轮齿宽决定":"长度参与轴向尺寸链联动");
                Bind(g.Rows[row].Cells[1],prefix+"S"+segment+"D",(prefix=="high"?"高速轴":"低速轴")+segment+"段直径","mm",true,false);Bind(g.Rows[row].Cells[2],prefix+"S"+segment+"L",(prefix=="high"?"高速轴":"低速轴")+segment+"段长度","mm",true,false);
                g.Rows[row].Cells[1].ReadOnly=bearing||gear;g.Rows[row].Cells[2].ReadOnly=gear||(prefix=="low"&&segment==2);}}
        void BuildHousing(){var split=new SplitContainer{Dock=DockStyle.Fill,Orientation=Orientation.Horizontal,SplitterDistance=280};pages[5].Controls.Add(split);var g=ParameterGrid(split.Panel1,"轴向内腔、齿轮端面净空和通气器安装尺寸参与当前模型。独立壁厚、底座、筋厚等未接入模板的尺寸不提供独立编辑。");
            MaterialRow(g,"housingMaterial","箱体材料（说明书）","HT200",MaterialCatalog.Housing,"HT200");ChoiceRow(g,"lubrication","润滑方式（联动轴承速度上限）","浸油润滑",new[]{"浸油润滑","飞溅润滑","油脂润滑"},true);
            FieldRow(g,"housingBottomClearance","齿顶到底部净空",12.0,"mm","联动轴心与箱体高度",true);FieldRow(g,"housingAxialWidth","内腔轴向宽度",0.0,"mm","0 为自动，正值按输入尺寸布置",true);FieldRow(g,"housingGearEndClearance","齿轮端面最小净空",8.0,"mm","",true);
            FieldRow(g,"ventOffsetX","通气器 X 偏移",0.0,"mm","相对大齿轮轴线",true);FieldRow(g,"ventOffsetY","通气器 Y 偏移",0.0,"mm","相对箱体中面",true);FieldRow(g,"ventStandOff","通气器座面最小抬高",6.0,"mm","",true);FieldRow(g,"ventGearClearance","通气器底端齿顶净空",4.0,"mm","",true);
            Columns(layoutResults,"轴向布置结果","采用值","单位");split.Panel2.Controls.Add(layoutResults);}
        void BuildChecks(){Columns(checks,"校核项目","计算值","许用 / 期望值","结论");pages[6].Controls.Add(checks);pages[6].Controls.Add(conclusion);pages[6].Controls.Add(Note("重新计算按当前手动值校核，不主动重新选择齿轮与轴承。初步强度、键压强和轴承寿命校核不替代全轴疲劳、制造公差及三维干涉复核。",62));}
        void BuildGeneration(){var actions=new FlowLayoutPanel{Dock=DockStyle.Top,Height=92,WrapContents=true,Padding=new Padding(0,10,0,0)};actions.Controls.Add(exportButton);actions.Controls.Add(generateButton);actions.Controls.Add(reportButton);actions.Controls.Add(openOutput);actions.Controls.Add(openReport);
            pages[7].Controls.Add(generationSummary);pages[7].Controls.Add(Note("工程计算采用当前方案。可按直齿或斜齿方案生成独立零件及一级总装；双键方案仍需先改为单键并重新校核。",65));pages[7].Controls.Add(outputLocation);pages[7].Controls.Add(actions);pages[7].Controls.Add(advanced);
            advanced.Dock=DockStyle.Top;generateButton.Click+=(s,e)=>PrepareModel(true);exportButton.Click+=(s,e)=>PrepareModel(false);reportButton.Click+=(s,e)=>CreateReport();openOutput.Click+=(s,e)=>OpenPath(outputFolder);openReport.Click+=(s,e)=>OpenPath(reportFile);}
        void SelectPage(int index){if(index<0)return;for(int i=0;i<pages.Length;i++)pages[i].Visible=i==index;heading.Text=(index+1)+" / 8  "+Titles[index];pages[index].BringToFront();}
        void MarkDirty(){dirty=true;modelParametersFile=null;outputFolder=null;reportFile=null;outputLocation.Clear();results.Rows.Clear();checks.Rows.Clear();layoutResults.Rows.Clear();calculationProcess.Clear();generationSummary.Text="参数已修改，请重新计算与校核后再准备建模。";conclusion.Text="参数已修改，请重新计算与校核后再生成模型。";status.Text="参数已修改，旧结果已失效。";UpdateActions();}
        void UpdateActions(){bool fresh=!dirty&&design!=null;autoButton.Enabled=calculateButton.Enabled=schemaReady&&!busy;generateButton.Enabled=exportButton.Enabled=fresh&&Flag(design,"system_checks_passed")&&!busy;reportButton.Enabled=fresh&&!busy;openOutput.Enabled=!busy&&Directory.Exists(outputFolder);openReport.Enabled=!busy&&File.Exists(reportFile);}
        void SetBusy(bool value,string message){busy=value;content.Enabled=navigation.Enabled=toolbar.Enabled=footer.Enabled=!value;status.Text=message;UpdateActions();status.Update();}
        void LoadSchema(){if(busy||schemaReady)return;RunBridge(new Dictionary<string,object>{{"action","schema"}},"正在准备设计参数…",response=>{
            ApplySchema(Map(Get(response,"schema")));
            if(initialProject!=null)LoadProjectSnapshot(initialProject);
            else status.Text="设计参数已准备好。填写工况后点击“自动设计方案”。";
        });}
        void ApplySchema(Dictionary<string,object> value){schema=value;applying=true;try{var numeric=Map(Get(value,"parameters"));foreach(var item in numeric){Field field;if(!fields.TryGetValue(item.Key,out field))continue;var spec=Map(item.Value);field.ModelApplied=!spec.ContainsKey("model_applied")||Flag(spec,"model_applied");if(!field.ModelApplied){field.Cell.ReadOnly=true;field.Cell.Style.BackColor=Color.Gainsboro;}if(spec.ContainsKey("default")&&dirty&&design==null&&item.Key!="helixAngle")field.Cell.Value=spec["default"];}
                foreach(var item in Map(Get(value,"choices"))){Field field;if(fields.TryGetValue(item.Key,out field)&&!(field.Cell is MaterialChoiceCell))SetChoices(field,Items(item.Value),Convert.ToString(field.Cell.Value,CultureInfo.InvariantCulture));}
                schemaReady=true;}finally{applying=false;}UpdateActions();}
        Dictionary<string,object> CollectParameters(bool automatic){foreach(var grid in editableGrids){grid.EndEdit();}var values=new Dictionary<string,object>();var specs=new Dictionary<string,object>(Map(Get(schema,"parameters")));foreach(var option in Map(Get(schema,"proposal_options")))specs[option.Key]=option.Value;foreach(var item in fields){var f=item.Value;if(!f.ModelApplied||(automatic&&!f.AutoInput)||(!automatic&&(f.Key=="target_speed"||f.Key=="speed_tolerance"||f.Key=="max_center_distance")))continue;
                if(f.Numeric){double number;if(!Double.TryParse(Convert.ToString(f.Cell.Value,CultureInfo.CurrentCulture),NumberStyles.Float,CultureInfo.CurrentCulture,out number)&&!Double.TryParse(Convert.ToString(f.Cell.Value,CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out number)||Double.IsNaN(number)||Double.IsInfinity(number))throw new ArgumentException("请输入有效数值："+f.Label);
                    var spec=Map(Get(specs,f.Key));if(spec.ContainsKey("minimum")&&number<Convert.ToDouble(spec["minimum"])||spec.ContainsKey("maximum")&&number>Convert.ToDouble(spec["maximum"])||spec.ContainsKey("exclusiveMinimum")&&number<=Convert.ToDouble(spec["exclusiveMinimum"]))throw new ArgumentException(f.Label+"超出允许范围。");if(Flag(spec,"integer")&&number!=Math.Truncate(number))throw new ArgumentException(f.Label+"必须为整数。");values[f.Key]=number;
                }else{string text=Convert.ToString(f.Cell.Value,CultureInfo.InvariantCulture);if(String.IsNullOrWhiteSpace(text))throw new ArgumentException("请填写："+f.Label);values[f.Key]=text;}}
            values["dynamicFactorMode"]="manual";values["gearLayout"]="symmetrical";return values;}
        Dictionary<string,object> Project(){string name=projectName.Text.Trim();if(name.Length==0||name.Length>120)throw new ArgumentException("项目名称需要为 1 至 120 个字符。");return new Dictionary<string,object>{{"name",name},{"author",designer.Text.Trim()},{"designer",designer.Text.Trim()},{"number",projectNumber.Text.Trim()},{"date",projectDate.Text.Trim()},{"comment",notes.Text},{"notes",notes.Text}};}
        string WorkFolder(){if(workFolder==null){workFolder=Path.Combine(Generator.ProjectRoot(),"design_projects",DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")+"_"+Guid.NewGuid().ToString("N").Substring(0,6));Directory.CreateDirectory(workFolder);}return workFolder;}
        Dictionary<string,object> Request(string action){return RequestForOutput(action,null);}
        Dictionary<string,object> RequestForOutput(string action,string outputDirectory){var request=new Dictionary<string,object>{{"action",action},{"project",Project()},{"output_dir",outputDirectory??WorkFolder()}};if(design!=null)request["design"]=design;return request;}
        Dictionary<string,object> ComputeRequest(bool automatic){var request=Request(automatic?"auto":"calculate");if(automatic)request.Remove("design");request["parameters"]=CollectParameters(automatic);request["derive_linked_geometry"]=linkedGeometry.Checked;return request;}
        void Compute(bool automatic){if(busy)return;try{if(!schemaReady)throw new InvalidOperationException("设计参数尚未准备好，请重新打开设计窗口。");var request=ComputeRequest(automatic);RunBridge(request,automatic?"正在自动选择可行方案…":"正在按当前参数计算与校核…",response=>{
            ApplyDesign(Map(Get(response,"design")));
            if(automatic)navigation.SelectedIndex=3;
            status.Text=Flag(design,"system_checks_passed")?"结果已更新，初步系统校核通过。":"结果已更新，请在第 7 步“强度与寿命”查看未通过项。";
        });}catch(Exception ex){Error(ex);}}
        void ApplyDesign(Dictionary<string,object> value){if(value.Count==0)throw new InvalidOperationException("未获得完整设计结果。");applying=true;try{design=value;foreach(var group in new[]{"inputs","selections"})foreach(var item in Map(Get(value,group))){Field f;if(!fields.TryGetValue(item.Key,out f))continue;var material=f.Cell as MaterialChoiceCell;if(material!=null)material.SetValue(item.Value);else if(f.Cell is DataGridViewComboBoxCell){var cell=(DataGridViewComboBoxCell)f.Cell;var options=(List<Choice>)cell.DataSource;if(!options.Any(c=>c.Value==Convert.ToString(item.Value)))SetChoices(f,options.Select(c=>c.Value).Concat(new[]{Convert.ToString(item.Value)}).ToArray(),Convert.ToString(item.Value));else cell.Value=item.Value;}else f.Cell.Value=item.Value;}
                dirty=false;modelParametersFile=null;outputFolder=null;reportFile=null;outputLocation.Clear();RenderResults();RenderGeneration(null);}finally{applying=false;}UpdateActions();}
        static Dictionary<string,object> Map(object value){return value as Dictionary<string,object>??new Dictionary<string,object>();}
        static object Get(Dictionary<string,object> value,string key){object result;return value!=null&&value.TryGetValue(key,out result)?result:null;}
        static bool Flag(Dictionary<string,object> value,string key){return Object.Equals(Get(value,key),true);}
        static IEnumerable Items(object value){return value as IEnumerable??new object[0];}
        static string Display(object value){if(value==null)return "—";if(value is double||value is float||value is decimal)return Convert.ToDouble(value).ToString("0.####",CultureInfo.CurrentCulture);if(value is bool)return (bool)value?"满足":"不满足";if(value is string)return (string)value;if(value is IEnumerable)return String.Join("；",Items(value).Cast<object>().Select(Display).ToArray());return Convert.ToString(value,CultureInfo.CurrentCulture);}
        void RenderResults(){results.Rows.Clear();checks.Rows.Clear();layoutResults.Rows.Clear();var r=Map(Get(design,"results"));string[] keys={"outPower","outSpeed","outTorque","ratio","inputTorque","dPinion","dGear","centerDistance","tangentialForce","pinionAxialForce","gearAxialForce","pitchLineVelocity","effectiveFaceWidth","contactStress","pinionBendStress","gearBendStress","moduleMin","highMinDiameter","lowMinDiameter","requiredBearingLife","highBearingLife","lowBearingLife","highShaftStress","shaftStress","contactLoadFactor","bendingLoadFactor","contactRatio"};
            string[] labels={"输出功率","输出转速","输出转矩","传动比","输入转矩","小齿轮分度圆直径","大齿轮分度圆直径","中心距","啮合切向力","高速轴轴向力","低速轴轴向力","节线速度","有效齿宽","齿面接触应力","小齿轮弯曲应力","大齿轮弯曲应力","最小可用标准模数","高速轴估算最小轴径","低速轴估算最小轴径","要求轴承寿命","高速轴承寿命","低速轴承寿命","高速轴弯扭应力","低速轴弯扭应力","接触载荷系数 KH","弯曲载荷系数 KF","端面重合度"};
            string[] units={"kW","r/min","N·mm","—","N·mm","mm","mm","mm","N","N","N","m/s","mm","MPa","MPa","MPa","mm","mm","mm","h","h","h","MPa","MPa","—","—","—"};for(int i=0;i<keys.Length;i++)if(r.ContainsKey(keys[i]))results.Rows.Add(labels[i],Display(r[keys[i]]),units[i]);
            foreach(var item in Map(Get(design,"checks"))){var check=Map(item.Value);string name=CheckLabel(item.Key);string unit=item.Key.Contains("bearing_life")?" h":item.Key.Contains("strength")||item.Key.Contains("bending")||item.Key.Contains("pressure")?" MPa":item.Key=="center_distance"||item.Key=="design_module"?" mm":"";object limit=Get(check,"limit")??Get(check,"minimum")??Get(check,"limits");int row=checks.Rows.Add(name,Display(Get(check,"value"))+unit,Display(limit)+unit,Flag(check,"passed")?"满足":"不满足 / 待复核");checks.Rows[row].Cells[3].Style.ForeColor=Flag(check,"passed")?Color.DarkGreen:Color.Firebrick;}
            var layout=Map(Get(design,"axial_layout"));string[] lk={"inner_width","recommended_inner_width","outer_width","mesh_center_y","gear_positive_clearance","gear_negative_clearance","sleeve_length","oil_ring_length"};string[] ll={"采用内腔轴向宽度","建议最小内腔宽度","轴承凸台总宽","啮合中心 Y","正向齿轮端面净空","负向齿轮端面净空","轴套长度","挡油环厚度"};for(int i=0;i<lk.Length;i++)if(layout.ContainsKey(lk[i]))layoutResults.Rows.Add(ll[i],Display(layout[lk[i]]),"mm");
            foreach(object item in Items(Get(design,"geometry_linkage"))){var change=Map(item);layoutResults.Rows.Add(LinkedFieldLabel(Convert.ToString(Get(change,"field"))),Display(Get(change,"before"))+" → "+Display(Get(change,"after")),"mm");}
            conclusion.Text=Display(Get(design,"designConclusion"))+System.Environment.NewLine+"布置提示："+Display(Get(layout,"warnings"))+System.Environment.NewLine+"仍需人工复核：材料与系数依据、全轴疲劳、装配干涉及制造公差。";RenderCalculationProcess();}
        string LinkedFieldLabel(string key){Field field;if(fields.TryGetValue(key,out field))return field.Label+(key.EndsWith("D")?"直径":key.EndsWith("L")?"长度":"");return "轴段联动尺寸";}
        void RenderCalculationProcess(){var text=new StringBuilder();var selection=Map(Get(design,"automatic_selection"));if(selection.Count>0)text.AppendLine("自动选型依据").AppendLine("按目标转速及当前工况搜索齿轮与轴系组合；检查齿轮组合 "+Display(Get(selection,"gear_combinations"))+" 组，轴系尝试 "+Display(Get(selection,"shaft_trials"))+" 次。").AppendLine();
            foreach(object item in Items(Get(design,"calculation_steps"))){var step=Map(item);text.AppendLine(Display(Get(step,"title"))).AppendLine(Display(Get(step,"text"))).AppendLine();}
            var linkage=Items(Get(design,"geometry_linkage")).Cast<object>().ToArray();if(linkage.Length>0){text.AppendLine("轴段尺寸联动（修改前 → 实际采用）");foreach(object item in linkage){var change=Map(item);text.AppendLine(LinkedFieldLabel(Convert.ToString(Get(change,"field")))+"："+Display(Get(change,"before"))+" → "+Display(Get(change,"after"))+" mm；"+Display(Get(change,"reason")));}text.AppendLine();}
            text.AppendLine("结构及适用条件");foreach(var item in Map(Get(design,"checks"))){var check=Map(item.Value);string note=Convert.ToString(Get(check,"note"));var messages=Items(Get(check,"messages")).Cast<object>().Select(Display).Where(x=>!String.IsNullOrWhiteSpace(x)).ToArray();if(!String.IsNullOrWhiteSpace(note)||messages.Length>0){text.Append(CheckLabel(item.Key)).Append("：").AppendLine(Flag(check,"passed")?"满足当前初步校核条件":"未通过 / 待复核");if(!String.IsNullOrWhiteSpace(note))text.AppendLine(note);foreach(string message in messages)text.AppendLine(message);text.AppendLine();}}
            text.AppendLine("后续人工复核").AppendLine("材料许用值及系数依据、全轴疲劳、装配干涉与制造公差。初步系统校核通过后仍需完成这些复核。");calculationProcess.Text=text.ToString();calculationProcess.SelectionStart=0;}
        void RenderGeneration(Dictionary<string,object> response){var text=new StringBuilder();text.AppendLine("当前建模方案");text.AppendLine(Flag(design,"system_checks_passed")?"初步系统校核通过。":"存在未通过项目，请先查看强度与寿命页。");
            var p=Map(Get(design,"inputs"));var s=Map(Get(design,"selections"));var r=Map(Get(design,"results"));text.AppendLine("齿轮："+ChoiceLabel(Convert.ToString(Get(s,"gearType")))+"；齿数 "+Display(Get(p,"z1"))+" / "+Display(Get(p,"z2"))+"；模数 "+Display(Get(p,"moduleM"))+" mm；中心距 "+Display(Get(r,"centerDistance"))+" mm。");text.AppendLine("轴承：高速轴 "+Display(Get(s,"highBearing"))+"；低速轴 "+Display(Get(s,"lowBearing"))+"。");
            var adjustments=Items(Get(response,"design_adjustments")??Get(design,"native_geometry_adjustments")).Cast<object>().ToArray();if(adjustments.Length>0){text.AppendLine().AppendLine("为当前轴径采用的建模尺寸调整");foreach(object adjustment in adjustments)text.AppendLine(Display(Get(Map(adjustment),"message")));}
            if(!String.IsNullOrEmpty(modelParametersFile))text.AppendLine().AppendLine("已保存建模方案：").AppendLine(modelParametersFile);text.AppendLine().AppendLine("设计项目目录：").AppendLine(workFolder??"保存方案后显示");
            if(File.Exists(reportFile))text.AppendLine().AppendLine("设计说明书：").AppendLine(reportFile);generationSummary.Text=text.ToString();}
        static string CheckLabel(string key){switch(key){case "contact_strength":return "齿面接触强度";case "pinion_bending":return "小齿轮齿根弯曲强度";case "gear_bending":return "大齿轮齿根弯曲强度";case "center_distance":return "中心距范围";case "design_module":return "模数强度条件";case "coefficient_domain":return "计算系数适用范围";case "high_shaft_strength":return "高速轴齿轮截面强度";case "low_shaft_strength":return "低速轴齿轮截面强度";case "high_bearing_life":return "高速轴承寿命";case "low_bearing_life":return "低速轴承寿命";case "highKey_pressure":return "高速轴外伸端键挤压强度";case "lowGearKey_pressure":return "低速轴齿轮键挤压强度";case "lowOuterKey_pressure":return "低速轴外伸端键挤压强度";case "basic_geometry":return "轴承、键及轮缘基本尺寸";default:return "结构与适用条件校核";}}
        void RunBridge(Dictionary<string,object> request,string message,Action<Dictionary<string,object>> completed){if(busy)return;SetBusy(true,message);var worker=new BackgroundWorker();worker.DoWork+=(s,e)=>e.Result=bridge(request);worker.RunWorkerCompleted+=(s,e)=>{SetBusy(false,"操作完成。");try{if(e.Error!=null)throw e.Error;var response=(Dictionary<string,object>)e.Result;if(!Flag(response,"ok"))throw new InvalidOperationException(Display(Get(response,"error")));completed(response);}catch(Exception ex){Error(ex);}finally{worker.Dispose();UpdateActions();}};worker.RunWorkerAsync();}
        void RequireCurrent(){foreach(var grid in editableGrids)grid.EndEdit();if(dirty||design==null)throw new InvalidOperationException("参数已更改或尚未计算，请先重新计算与校核。");}
        void PrepareModel(bool generate){if(busy)return;try{RequireCurrent();if(!Flag(design,"system_checks_passed"))throw new InvalidOperationException("当前方案存在未通过的系统校核，不能生成模型。");string modelRoot=null;
            if(generate) {
                if(app==null)throw new InvalidOperationException("请打开 SolidWorks 2024 后重新打开设计窗口以生成原生模型。");
                modelRoot=Generator.ChooseOutputDirectory(this,"选择模型保存目录。零件和总装会保存在新的子文件夹中。",Path.Combine(Generator.ProjectRoot(),"output"));
                if(modelRoot==null)return;
            }
            RunBridge(Request("export"),"正在保存已校核方案并准备建模…",response=>{string path=Convert.ToString(Get(response,"model_parameters_file"));if(String.IsNullOrWhiteSpace(path)||!File.Exists(path))throw new InvalidOperationException("未获得可用的模型方案，请重新保存方案。");Generator.Validate(json.Deserialize<Parameters>(File.ReadAllText(path)));ApplyDesign(Map(Get(response,"design")));modelParametersFile=path;RenderGeneration(response);status.Text="建模方案已保存，采用尺寸见本页说明。";navigation.SelectedIndex=7;if(generate)GenerateNative(modelRoot);});}catch(Exception ex){Error(ex);}}
        bool closeAfterGeneration;
        void GenerateNative(string modelRoot){RequireCurrent();string path=modelParametersFile;
            bool addMates=true;
            if(advanced.Checked){using(var editor=new ParameterEditor(path)){if(editor.ShowDialog(this)!=DialogResult.OK)return;path=editor.SnapshotPath;addMates=editor.AddAssemblyMates;}Generator.Validate(json.Deserialize<Parameters>(File.ReadAllText(path)));}
            SetBusy(true,"正在 SOLIDWORKS 中创建原生零件和总装，请等待完成…");
            try {
                outputFolder=Generator.Generate(app,path,modelRoot,addMates);
                outputLocation.Text=outputFolder;navigation.SelectedIndex=7;
                status.Text="原生模型已生成，可打开模型目录查看零件与总装。";
            } catch(OperationCanceledException ex) {
                status.Text=ex.Message;
            } finally {
                SetBusy(false,status.Text);
                if(closeAfterGeneration) {closeAfterGeneration=false;BeginInvoke((Action)Close);}
            }
        }
        void CreateReport(){if(busy)return;try{RequireCurrent();
            string root=Generator.ChooseOutputDirectory(this,"选择说明书保存目录。说明书和插图会保存在新的子文件夹中。",Path.Combine(Generator.ProjectRoot(),"design_projects"));
            if(root==null)return;
            RunBridge(RequestForOutput("report",Generator.ReportOutputDirectory(root)),"正在编写设计说明书…",response=>{reportFile=Convert.ToString(Get(response,"report_file"));if(!File.Exists(reportFile))throw new InvalidOperationException("未获得可用的设计说明书。");RenderGeneration(null);status.Text="设计说明书已生成，可点击“打开说明书”。";});}catch(Exception ex){Error(ex);}}
        Dictionary<string,object> DraftDesign(){var draft=design==null?new Dictionary<string,object>():json.Deserialize<Dictionary<string,object>>(json.Serialize(design));var inputs=Map(Get(draft,"inputs"));var selections=Map(Get(draft,"selections"));foreach(var item in CollectParameters(false)){Field field;if(fields.TryGetValue(item.Key,out field)&&!field.Numeric)selections[item.Key]=item.Value;else inputs[item.Key]=item.Value;}draft["inputs"]=inputs;draft["selections"]=selections;if(dirty){foreach(string key in new[]{"results","factors","checks","axial_layout","gear_operating"})draft.Remove(key);draft["system_checks_passed"]=false;draft["calculation_stale"]=true;}return draft;}
        void SaveProject(){try{var snapshot=new Dictionary<string,object>{{"format","reducer_design_project"},{"version",1},{"schema_version",1},{"project",Project()},{"design",DraftDesign()},{"derive_linked_geometry",linkedGeometry.Checked},{"requirements",new Dictionary<string,object>{{"target_speed",fields["target_speed"].Cell.Value},{"speed_tolerance",fields["speed_tolerance"].Cell.Value},{"max_center_distance",fields["max_center_distance"].Cell.Value}}}};using(var dialog=new SaveFileDialog{Title="保存设计项目",Filter="减速器设计项目 (*.json)|*.json",FileName="减速器设计项目.json"})if(dialog.ShowDialog(this)==DialogResult.OK){File.WriteAllText(dialog.FileName,json.Serialize(snapshot));status.Text="项目已保存。";}}catch(Exception ex){Error(ex);}}
        void LoadProjectSnapshot(Dictionary<string,object> snapshot){var loaded=Map(Get(snapshot,"design"));if(loaded.Count==0&&snapshot.ContainsKey("inputs"))loaded=snapshot;if(!loaded.ContainsKey("inputs"))throw new ArgumentException("该文件没有可读取的设计方案。");var project=Map(Get(snapshot,"project"));if(project.Count==0)project=Map(Get(loaded,"projectInfo"));string name=Convert.ToString(Get(project,"name"));if(!String.IsNullOrWhiteSpace(name))projectName.Text=name;designer.Text=Convert.ToString(Get(project,"author")??Get(project,"designer"));projectNumber.Text=Convert.ToString(Get(project,"number"));projectDate.Text=Convert.ToString(Get(project,"date"));notes.Text=Convert.ToString(Get(project,"comment")??Get(project,"notes"));ApplyDesign(loaded);applying=true;try{linkedGeometry.Checked=!snapshot.ContainsKey("derive_linked_geometry")||Flag(snapshot,"derive_linked_geometry");foreach(var item in Map(Get(snapshot,"requirements")))if(fields.ContainsKey(item.Key))fields[item.Key].Cell.Value=item.Value;}finally{applying=false;}workFolder=null;MarkDirty();status.Text="项目已载入，请按当前参数重新校核后再生成模型。";navigation.SelectedIndex=1;}
        void OpenProject(){try{using(var dialog=new OpenFileDialog{Title="打开设计项目",Filter="减速器设计项目 (*.json)|*.json"})if(dialog.ShowDialog(this)==DialogResult.OK)LoadProjectSnapshot(json.Deserialize<Dictionary<string,object>>(File.ReadAllText(dialog.FileName)));}catch(Exception ex){Error(ex);}}
        internal static void ShowReferences(Form owner,Action<Exception> onError) {
            try {
                using(var dialog=CreateReferenceDialog(owner.Font))dialog.ShowDialog(owner);
            } catch(Exception error) {onError(error);}
        }

        static Form CreateReferenceDialog(Font font) {
            var dialog=new Form{Text="设计参考图表",Font=font,ClientSize=new Size(1560,750),
                MinimumSize=new Size(840,440),StartPosition=FormStartPosition.CenterParent};
            try {
                ToolbarIcons.ApplyWindowIcon(dialog);
                dialog.SuspendLayout();
                var tabs=new TabControl{Dock=DockStyle.Fill,Multiline=true};
                dialog.Controls.Add(tabs);
                string root=Directory.GetParent(Generator.ProjectRoot()).FullName;
                string[] files={"kv-textbook-chart.png","gear-load-table-10-3.png","gear-load-table-10-4.png",
                    "gear-load-table-10-4-continuation.jpg","kfbeta-textbook-chart.png","reducer.jpg"};
                string[] labels={"动载系数 Kᵥ","齿间载荷分配","接触齿向载荷分配","接触齿向载荷续表","弯曲齿向载荷分配","减速器轴系布置"};
                for(int i=0;i<files.Length;i++)
                    AddReferenceImage(tabs,labels[i],Path.Combine(root,"images",files[i]),false);
                AddReferenceImage(tabs,"齿轮材料",Path.Combine(root,"images","齿轮材料.png"),true);
                AddReferenceImage(tabs,"轴的常用材料及其主要力学性能",Path.Combine(root,"images","material_props.jpg"),true);
                AddReferenceImage(tabs,"轴材料计算参数",Path.Combine(root,"images","shaft-material-table-15-3.png"),true);
                var housing=new TabPage("箱体材料");
                tabs.TabPages.Add(housing);
                housing.Controls.Add(MaterialCatalog.HousingMaterials());
                dialog.AutoScaleDimensions=new SizeF(96,96);
                dialog.AutoScaleMode=AutoScaleMode.Dpi;
                dialog.Load+=(s,e)=>{
                    Rectangle area=Screen.FromControl(dialog).WorkingArea;
                    var size=new Size(Math.Min(dialog.Width,area.Width*94/100),Math.Min(dialog.Height,area.Height*89/100));
                    dialog.MinimumSize=new Size(Math.Min(dialog.MinimumSize.Width,size.Width),Math.Min(dialog.MinimumSize.Height,size.Height));
                    dialog.Size=size;
                    dialog.Location=new Point(area.Left+(area.Width-size.Width)/2,area.Top+(area.Height-size.Height)/2);
                };
                dialog.ResumeLayout(false);
                return dialog;
            } catch {
                dialog.Dispose();
                throw;
            }
        }
        static void AddReferenceImage(TabControl tabs,string title,string path,bool scroll) {
            if(!File.Exists(path))return;
            var page=new TabPage(title);
            tabs.TabPages.Add(page);
            var picture=new PictureBox{SizeMode=PictureBoxSizeMode.Zoom};
            using(var source=Image.FromFile(path))picture.Image=new Bitmap(source);
            Image image=picture.Image;
            picture.Disposed+=(s,e)=>image.Dispose();
            if(!scroll) {
                picture.Dock=DockStyle.Fill;
                page.Controls.Add(picture);
                return;
            }

            var viewport=new Panel{Dock=DockStyle.Fill,AutoScroll=true,BackColor=Color.White};
            page.Controls.Add(viewport);
            viewport.Controls.Add(picture);
            EventHandler fit=(s,e)=>{
                const int minimumWidth=1200;
                const int sideMargin=12;
                double aspect=(double)image.Height/image.Width;
                int availableWidth=viewport.Width;
                int width=Math.Min(image.Width,Math.Max(minimumWidth,availableWidth-sideMargin*2));
                if(Math.Round(width*aspect)>viewport.Height) {
                    availableWidth=Math.Max(0,availableWidth-SystemInformation.VerticalScrollBarWidth);
                    width=Math.Min(image.Width,Math.Max(minimumWidth,availableWidth-sideMargin*2));
                }
                int height=(int)Math.Round(width*aspect);
                viewport.SuspendLayout();
                try {
                    viewport.AutoScrollMinSize=new Size(width,height);
                    Point offset=viewport.AutoScrollPosition;
                    picture.Bounds=new Rectangle(
                        Math.Max(0,(availableWidth-width)/2)+offset.X,
                        Math.Max(0,(viewport.Height-height)/2)+offset.Y,
                        width,height);
                } finally {viewport.ResumeLayout();}
            };
            viewport.SizeChanged+=fit;
            fit(viewport,EventArgs.Empty);
        }
        void OpenPath(string path){try{if(String.IsNullOrEmpty(path)||(!File.Exists(path)&&!Directory.Exists(path)))throw new FileNotFoundException("结果文件尚未生成或已被移动。");Process.Start(new ProcessStartInfo(path){UseShellExecute=true});}catch(Exception ex){Error(ex);}}
        void Error(Exception error){status.Text="操作未完成："+error.Message;MessageBox.Show(this,error.Message,"减速器设计",MessageBoxButtons.OK,MessageBoxIcon.Information);}
    }

    internal class DesignChoiceCell : DataGridViewComboBoxCell {
        public DesignChoiceCell(){FlatStyle=FlatStyle.Flat;}

        protected override void Paint(Graphics graphics,Rectangle clipBounds,Rectangle cellBounds,int rowIndex,
            DataGridViewElementStates state,object value,object formattedValue,string errorText,
            DataGridViewCellStyle style,DataGridViewAdvancedBorderStyle borderStyle,DataGridViewPaintParts parts) {
            bool current=DataGridView!=null&&DataGridView.CurrentCellAddress==new Point(ColumnIndex,rowIndex);
            bool drawArrow=(parts&DataGridViewPaintParts.ContentBackground)!=0&&DataGridView!=null&&
                DisplayStyle!=DataGridViewComboBoxDisplayStyle.Nothing&&(!DisplayStyleForCurrentCellOnly||current)&&
                (!current||DataGridView.EditingControl==null)&&FlatStyle==FlatStyle.Flat;
            if(!drawArrow) {
                base.Paint(graphics,clipBounds,cellBounds,rowIndex,state,value,formattedValue,errorText,style,borderStyle,parts);
                return;
            }

            // Match the flat WinForms button bounds so its mouse hit area and text spacing stay aligned.
            Rectangle borders=BorderWidths(borderStyle);
            bool rtl=DataGridView.RightToLeft==RightToLeft.Yes;
            int contentWidth=cellBounds.Width-borders.Right-style.Padding.Horizontal;
            int contentHeight=cellBounds.Height-borders.Bottom-style.Padding.Vertical;
            int textHeight=TextRenderer.MeasureText(graphics," ",style.Font,Size.Empty,TextFormatFlags.Default).Height;
            int buttonWidth=Math.Min(SystemInformation.HorizontalScrollBarThumbWidth,contentWidth-7);
            int buttonHeight=Math.Min(textHeight+4,contentHeight-2);
            if(buttonWidth<7||buttonHeight<5) {
                base.Paint(graphics,clipBounds,cellBounds,rowIndex,state,value,formattedValue,errorText,style,borderStyle,parts);
                return;
            }

            int x=rtl ? cellBounds.Left+borders.X+style.Padding.Right+1
                : cellBounds.Right-(borders.Right-borders.X)-style.Padding.Right-buttonWidth-1;
            var button=new Rectangle(x,cellBounds.Top+borders.Y+style.Padding.Top+1,buttonWidth,buttonHeight);
            base.Paint(graphics,clipBounds,cellBounds,rowIndex,state,value,formattedValue,errorText,style,borderStyle,
                parts&~DataGridViewPaintParts.ContentBackground);
            int width=Math.Min(button.Width-4,Math.Max(9,textHeight*3/5));
            int height=Math.Min(button.Height-2,(width+1)/2);
            int left=button.Left+(button.Width-width)/2;
            int top=button.Top+(button.Height-height)/2;
            graphics.FillRectangle(SystemBrushes.Control,Rectangle.Intersect(button,clipBounds));
            var graphicsState=graphics.Save();
            try {
                graphics.SetClip(Rectangle.Intersect(button,clipBounds),System.Drawing.Drawing2D.CombineMode.Intersect);
                graphics.FillPolygon(SystemBrushes.ControlText,new[]{
                    new Point(left,top),new Point(left+width,top),new Point(left+width/2,top+height)
                });
            } finally {graphics.Restore(graphicsState);}
        }
    }
}
