using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ReducerSW {
    public sealed class TwoStageEditor : Form {
        sealed class Entry { public object Target; public PropertyInfo Property; }
        string source;
        readonly Label summary;
        readonly CheckBox addMates=ParameterEditor.CreateMateOption(DockStyle.Bottom);
        readonly Parameters working;
        readonly TabControl tabs=new TabControl{Dock=DockStyle.Fill};
        readonly List<DataGridView> grids=new List<DataGridView>();
        public string SnapshotPath {get;private set;}
        public string ModelOutputRoot {get;private set;}
        public bool AddAssemblyMates {get{return addMates.Checked;}}
        public TwoStageEditor(string file) : this(file,false) {}
        public TwoStageEditor(string file,bool chooseOutputDirectory) {
            SuspendLayout();
            ToolbarIcons.ApplyWindowIcon(this);
            Text="二级减速器 — 参数化建模";
            Width=1080;Height=760;MinimumSize=new Size(760,550);StartPosition=FormStartPosition.CenterScreen;
            Font=new Font("Microsoft YaHei UI",10);
            working=new JavaScriptSerializer().Deserialize<Parameters>(File.ReadAllText(file));Generator.Validate(working);
            source=File.ReadAllText(file);
            BuildTables(working.two_stage);
            var note=new Label{Dock=DockStyle.Top,Height=78,Padding=new Padding(12,9,12,4),
                Text="三根轴、键槽、两级齿轮副、轴承及箱体在各页编辑，尺寸单位为 mm。\n修改齿数、法面模数或螺旋角后点击“同步中心距”，再校验轴承座、齿轮孔和箱体净空。生成时另存参数快照。"};
            summary=new Label{Dock=DockStyle.Bottom,Height=40,Padding=new Padding(12,8,12,4)};
            var buttons=new FlowLayoutPanel{Dock=DockStyle.Bottom,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,FlowDirection=FlowDirection.RightToLeft,Padding=new Padding(8)};
            var cancel=ParameterEditor.ActionButton("取消",100);cancel.DialogResult=DialogResult.Cancel;
            var generate=ParameterEditor.ActionButton("生成模型",125);
            var save=ParameterEditor.ActionButton("另存参数",110);
            var sync=ParameterEditor.ActionButton("同步中心距",125);
            var validate=ParameterEditor.ActionButton("校验参数",110);
            validate.Click+=(s,e)=>{try{UpdateSummary(Parse());MessageBox.Show(this,"二级参数校验通过。","参数校验");}catch(Exception ex){MessageBox.Show(this,ex.Message,"参数校验");}};
            sync.Click+=(s,e)=>{try{var p=ReadTables();var spec=p.two_stage;if(spec==null||spec.shafts==null||spec.high_pair==null||spec.low_pair==null)throw new ArgumentException("缺少 two_stage 参数。");
                    var output=spec.shafts.First(x=>x.name=="output");var middle=spec.shafts.First(x=>x.name=="intermediate");var input=spec.shafts.First(x=>x.name=="input");
                    middle.center_x=output.center_x+spec.low_pair.module*(spec.low_pair.pinion_teeth+spec.low_pair.wheel_teeth)/(2*Math.Cos(spec.low_pair.helix_angle*Math.PI/180));
                    input.center_x=middle.center_x+spec.high_pair.module*(spec.high_pair.pinion_teeth+spec.high_pair.wheel_teeth)/(2*Math.Cos(spec.high_pair.helix_angle*Math.PI/180));
                    var integral=input.sections.Single(x=>x.kind=="gear");integral.diameter=spec.high_pair.module*(spec.high_pair.pinion_teeth/Math.Cos(spec.high_pair.helix_angle*Math.PI/180)-2.5);integral.length=spec.high_pair.pinion_width;
                    double before=0;foreach(var section in input.sections){if(section==integral)break;before+=section.length;}
                    double a=input.origin_y+input.axis_direction*before,b=input.origin_y+input.axis_direction*(before+integral.length);
                    spec.high_pair.pinion_start_y=Math.Min(a,b);
                    source=PrettyJson(new JavaScriptSerializer().Serialize(p));RefreshTables();UpdateSummary(p);
                }catch(Exception ex){MessageBox.Show(this,ex.Message,"同步中心距");}};
            save.Click+=(s,e)=>{try{Parse();using(var picker=new SaveFileDialog{Filter="参数文件 (*.json)|*.json",FileName="two_stage_parameters.json"})if(picker.ShowDialog(this)==DialogResult.OK)File.WriteAllText(picker.FileName,source);}catch(Exception ex){MessageBox.Show(this,ex.Message,"参数校验");}};
            generate.Click+=(s,e)=>{try{
                    Parse();
                    if(chooseOutputDirectory) {
                        ModelOutputRoot=Generator.ChooseOutputDirectory(this,"选择模型保存目录。零件和总装会保存在新的子文件夹中。",Path.Combine(Generator.ProjectRoot(),"output"));
                        if(ModelOutputRoot==null)return;
                    }
                    string folder=Path.Combine(Generator.ProjectRoot(),"parameter_sets");Directory.CreateDirectory(folder);
                    SnapshotPath=Path.Combine(folder,"two_stage_"+DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")+"_"+Guid.NewGuid().ToString("N").Substring(0,6)+".json");
                    File.WriteAllText(SnapshotPath,source);DialogResult=DialogResult.OK;Close();
                }catch(Exception ex){MessageBox.Show(this,ex.Message,"参数校验");}};
            buttons.Controls.Add(cancel);buttons.Controls.Add(generate);buttons.Controls.Add(save);buttons.Controls.Add(sync);buttons.Controls.Add(validate);
            Controls.Add(tabs);Controls.Add(summary);Controls.Add(addMates);Controls.Add(buttons);Controls.Add(note);CancelButton=cancel;
            UpdateSummary(Parse());
            AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;
            ResumeLayout(false);
        }
        Parameters Deserialize(){return working;}
        Parameters Parse(){var p=ReadTables();Generator.Validate(p);source=PrettyJson(new JavaScriptSerializer().Serialize(p));return p;}
        static string Title(string name) {
            var labels=new Dictionary<string,string> {
                {"center_x","中心 X"},{"origin_y","轴向原点 Y"},{"axis_direction","轴向方向"},
                {"diameter","直径"},{"length","长度"},{"start","起点"},{"width","宽度"},{"depth","切入深度"},
                {"module","法面模数"},{"pressure_angle","压力角（度）"},{"helix_angle","螺旋角（度）"},
                {"pinion_teeth","小齿轮齿数"},{"wheel_teeth","大齿轮齿数"},
                {"pinion_width","小齿轮齿宽"},{"wheel_width","大齿轮齿宽"},
                {"pinion_start_y","小齿轮起点 Y"},{"wheel_start_y","大齿轮起点 Y"},
                {"pinion_bore","小齿轮轴孔"},{"wheel_bore","大齿轮轴孔"},
                {"pinion_key_width","小齿轮键槽宽"},{"wheel_key_width","大齿轮键槽宽"},
                {"pinion_key_depth","小齿轮键槽深"},{"wheel_key_depth","大齿轮键槽深"},
                {"pinion_phase_deg","小齿轮相位（度）"},{"wheel_phase_deg","大齿轮相位（度）"},
                {"catalog_name","型号 / 说明"},{"inner_diameter","内径"},{"outer_diameter","外径"},
                {"front_y","前端 Y"},{"rear_y","后端 Y"},{"axis_height","轴线高度"},
                {"upper_height","箱盖高度"},{"floor_thickness","箱底厚度"},{"roof_thickness","箱顶厚度"},
                {"side_wall","侧壁厚度"},{"front_wall","前端壁厚度"},{"rear_wall","后端壁厚度"},
                {"x_min","左边界 X"},{"x_max","右边界 X"},{"y_min","前边界 Y"},{"y_max","后边界 Y"},
                {"end_cover_thickness","轴承端盖厚度"}
            };
            return labels.ContainsKey(name)?labels[name]:name;
        }
        static string ShaftName(string name){return name=="input"?"输入轴":name=="intermediate"?"中间轴":"输出轴";}
        DataGridView Page(string name) {
            var grid=new DataGridView{Dock=DockStyle.Fill,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,
                AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,
                ColumnHeadersHeightSizeMode=DataGridViewColumnHeadersHeightSizeMode.AutoSize};
            grid.DefaultCellStyle.Alignment=DataGridViewContentAlignment.MiddleLeft;
            grid.DefaultCellStyle.Padding=new Padding(6,0,6,0);
            grid.ColumnHeadersDefaultCellStyle.Alignment=DataGridViewContentAlignment.MiddleLeft;
            grid.ColumnHeadersDefaultCellStyle.Padding=new Padding(3,5,3,5);
            grid.Columns.Add("item","零件 / 位置");grid.Columns.Add("parameter","参数");grid.Columns.Add("value","数值");
            grid.Columns[0].ReadOnly=true;grid.Columns[1].ReadOnly=true;
            // DataGridView has no row-spanning cells. Paint consecutive equal
            // labels as one visual group while retaining all editable rows.
            grid.CellPainting+=PaintGroupedFirstColumn;
            grid.Scroll+=(s,e)=>grid.InvalidateColumn(0);
            var page=new TabPage(name);page.Controls.Add(grid);tabs.TabPages.Add(page);grids.Add(grid);return grid;
        }
        static void PaintGroupedFirstColumn(object sender,DataGridViewCellPaintingEventArgs e) {
            if(e.RowIndex<0||e.ColumnIndex!=0)return;
            var grid=(DataGridView)sender;
            string label=Convert.ToString(grid.Rows[e.RowIndex].Cells[0].Value);
            int first=e.RowIndex,last=e.RowIndex;
            while(first>0&&String.Equals(label,Convert.ToString(grid.Rows[first-1].Cells[0].Value),StringComparison.Ordinal))first--;
            while(last+1<grid.Rows.Count&&String.Equals(label,Convert.ToString(grid.Rows[last+1].Cells[0].Value),StringComparison.Ordinal))last++;
            if(first==last)return;
            int visibleStart=grid.FirstDisplayedScrollingRowIndex;
            int visibleCount=grid.DisplayedRowCount(true);
            if(visibleStart<0||visibleCount<1)return;
            int top=Math.Max(first,visibleStart),bottom=Math.Min(last,visibleStart+visibleCount-1);
            if(top>bottom)return;
            Rectangle upper=grid.GetCellDisplayRectangle(0,top,true);
            Rectangle lower=grid.GetCellDisplayRectangle(0,bottom,true);
            var span=new Rectangle(e.CellBounds.Left,upper.Top,e.CellBounds.Width,lower.Bottom-upper.Top);
            using(var background=new SolidBrush(grid.DefaultCellStyle.BackColor.IsEmpty?Color.White:grid.DefaultCellStyle.BackColor))
                e.Graphics.FillRectangle(background,e.CellBounds);
            using(var line=new Pen(grid.GridColor)) {
                int left=e.CellBounds.Left,right=e.CellBounds.Right-1;
                e.Graphics.DrawLine(line,left,e.CellBounds.Top,left,e.CellBounds.Bottom);
                e.Graphics.DrawLine(line,right,e.CellBounds.Top,right,e.CellBounds.Bottom);
                if(e.RowIndex==first)e.Graphics.DrawLine(line,left,e.CellBounds.Top,right,e.CellBounds.Top);
                if(e.RowIndex==last)e.Graphics.DrawLine(line,left,e.CellBounds.Bottom-1,right,e.CellBounds.Bottom-1);
            }
            var clip=e.Graphics.Save();e.Graphics.SetClip(e.CellBounds);
            span.Inflate(-6,0);
            TextRenderer.DrawText(e.Graphics,label,e.CellStyle.Font??grid.Font,span,e.CellStyle.ForeColor,
                TextFormatFlags.Left|TextFormatFlags.VerticalCenter|TextFormatFlags.SingleLine|
                TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);
            e.Graphics.Restore(clip);e.Handled=true;
        }
        static void Add(DataGridView grid,string item,object target,params string[] properties) {
            foreach(string property in properties) {
                var info=target.GetType().GetProperty(property);
                if(info==null)throw new ArgumentException("未知参数："+property);
                int row=grid.Rows.Add(item,Title(property),info.GetValue(target,null));
                grid.Rows[row].Tag=new Entry{Target=target,Property=info};
            }
        }
        void BuildTables(TwoStageSpec spec) {
            var axes=Page("三轴位置");
            foreach(var shaft in spec.shafts)Add(axes,ShaftName(shaft.name),shaft,"center_x","origin_y","axis_direction");
            foreach(var shaft in spec.shafts) {
                var sections=Page(ShaftName(shaft.name)+"轴段");
                for(int i=0;i<shaft.sections.Length;i++)Add(sections,"第 "+(i+1)+" 段（"+(shaft.sections[i].kind=="gear"?"整体齿轮":"圆柱")+"）",
                    shaft.sections[i],"diameter","length");
            }
            var keys=Page("键槽");
            foreach(var shaft in spec.shafts)for(int i=0;i<shaft.keyways.Length;i++)
                Add(keys,ShaftName(shaft.name)+"键槽 "+(i+1),shaft.keyways[i],"start","length","width","depth","diameter");
            string[] pairFields={"module","pressure_angle","helix_angle","pinion_teeth","wheel_teeth","pinion_width","wheel_width",
                "pinion_start_y","wheel_start_y","pinion_bore","wheel_bore","pinion_key_width","wheel_key_width",
                "pinion_key_depth","wheel_key_depth","pinion_phase_deg","wheel_phase_deg"};
            Add(Page("高速级齿轮副"),"高速级",spec.high_pair,pairFields);
            Add(Page("低速级齿轮副"),"低速级",spec.low_pair,pairFields);
            var bearings=Page("轴承");foreach(var bearing in spec.bearings)Add(bearings,ShaftName(bearing.name),bearing,
                "catalog_name","inner_diameter","outer_diameter","width","front_y","rear_y");
            var housing=Page("箱体与端盖");Add(housing,"箱体",spec.housing,"x_min","x_max","y_min","y_max","axis_height",
                "upper_height","floor_thickness","roof_thickness","side_wall","front_wall","rear_wall");
            Add(housing,"端盖",spec,"end_cover_thickness");
        }
        Parameters ReadTables() {
            foreach(var grid in grids)grid.EndEdit();
            foreach(var grid in grids)foreach(DataGridViewRow row in grid.Rows) {
                var entry=(Entry)row.Tag;string raw=Convert.ToString(row.Cells[2].Value);
                object value;
                if(entry.Property.PropertyType==typeof(string))value=raw;
                else if(entry.Property.PropertyType==typeof(int)) {
                    int number;if(!Int32.TryParse(raw,NumberStyles.Integer,CultureInfo.CurrentCulture,out number))
                        throw new ArgumentException(row.Cells[1].Value+"需要整数。");value=number;
                } else {
                    double number;if((!Double.TryParse(raw,NumberStyles.Float,CultureInfo.CurrentCulture,out number)&&
                        !Double.TryParse(raw,NumberStyles.Float,CultureInfo.InvariantCulture,out number))||Double.IsNaN(number)||Double.IsInfinity(number))
                        throw new ArgumentException(row.Cells[1].Value+"需要有效数值。");value=number;
                }
                entry.Property.SetValue(entry.Target,value,null);
            }
            return working;
        }
        void RefreshTables(){foreach(var grid in grids)foreach(DataGridViewRow row in grid.Rows){var entry=(Entry)row.Tag;row.Cells[2].Value=entry.Property.GetValue(entry.Target,null);}}
        static string PrettyJson(string raw) {
            var result=new StringBuilder();int level=0;bool quoted=false,escaped=false;
            foreach(char ch in raw) {
                if(quoted) {
                    result.Append(ch);
                    if(ch=='"'&&!escaped)quoted=false;
                    if(ch=='\\')escaped=!escaped;else escaped=false;
                    continue;
                }
                if(ch=='"'){quoted=true;result.Append(ch);}
                else if(ch=='{'||ch=='['){result.Append(ch);result.AppendLine();level++;result.Append(new string(' ',level*2));}
                else if(ch=='}'||ch==']'){result.AppendLine();level--;result.Append(new string(' ',level*2));result.Append(ch);}
                else if(ch==','){result.Append(ch);result.AppendLine();result.Append(new string(' ',level*2));}
                else if(ch==':'){result.Append(": ");}
                else if(!Char.IsWhiteSpace(ch))result.Append(ch);
            }
            return result.ToString();
        }
        void UpdateSummary(Parameters p){var s=p.two_stage;double high=s.high_pair.module*(s.high_pair.pinion_teeth+s.high_pair.wheel_teeth)/(2*Math.Cos(s.high_pair.helix_angle*Math.PI/180));
            double low=s.low_pair.module*(s.low_pair.pinion_teeth+s.low_pair.wheel_teeth)/(2*Math.Cos(s.low_pair.helix_angle*Math.PI/180));
            double ratio=(double)s.high_pair.wheel_teeth/s.high_pair.pinion_teeth*s.low_pair.wheel_teeth/s.low_pair.pinion_teeth;
            summary.Text=String.Format("高速级中心距 {0:0.###} mm   低速级中心距 {1:0.###} mm   总传动比 {2:0.####}   三根轴 / 四个齿轮 / 六个轴承",high,low,ratio);}
    }
}
