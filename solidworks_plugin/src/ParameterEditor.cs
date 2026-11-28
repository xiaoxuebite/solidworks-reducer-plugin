using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ReducerSW {
    public sealed class ParameterEditor : Form {
        readonly Parameters parameters;
        readonly DataGridView shaft=Grid(), keys=Grid(), covers=Grid(), rings=Grid(), through=Grid(), seals=Grid(), sections=Grid(), datums=Grid();
        readonly DataGridView highShaft=Grid(), highKeys=Grid(), gearData=Grid(), bearingData=Grid();
        readonly CheckBox addMates=CreateMateOption(DockStyle.Bottom);
        public string SnapshotPath {get; private set;}
        public string ModelOutputRoot {get; private set;}
        public bool AddAssemblyMates {get{return addMates.Checked;}}
        internal static CheckBox CreateMateOption(DockStyle dock) {
            return new CheckBox{Text="添加装配配合（取消勾选时按计算位置固定零件）",
                Checked=true,Dock=dock,AutoSize=true,Padding=new Padding(12,6,8,6)};
        }
        static DataGridView Grid() {
            var grid=new DataGridView{Dock=DockStyle.Fill,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,
                AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,
                ColumnHeadersHeightSizeMode=DataGridViewColumnHeadersHeightSizeMode.AutoSize};
            grid.ColumnHeadersDefaultCellStyle.Padding=new Padding(3,5,3,5);
            return grid;
        }
        static void Columns(DataGridView grid, params string[] names) { foreach(string name in names) grid.Columns.Add(name,name); }
        static double Number(DataGridView grid,int row,int column) {
            double value;
            if(!Double.TryParse(Convert.ToString(grid.Rows[row].Cells[column].Value),out value) || Double.IsNaN(value) || Double.IsInfinity(value)) throw new ArgumentException("请检查“"+grid.Columns[column].HeaderText+"”第 "+(row+1)+" 行的数值。");
            return value;
        }
        public ParameterEditor(string file) : this(file,false) {}
        public ParameterEditor(string file,bool chooseOutputDirectory) {
            parameters=new JavaScriptSerializer().Deserialize<Parameters>(File.ReadAllText(file));
            Generator.Validate(parameters);
            SuspendLayout();
            ToolbarIcons.ApplyWindowIcon(this);
            Text="一级减速器 — 参数化建模（mm）"; Width=1000; Height=620; MinimumSize=new Size(800,500); StartPosition=FormStartPosition.CenterScreen;
            Font=new Font("Microsoft YaHei UI",10);
            var tabs=new TabControl{Dock=DockStyle.Fill};
            foreach(var item in new[]{Tuple.Create("从动轴",shaft),Tuple.Create("键槽",keys),Tuple.Create("闷盖",covers),Tuple.Create("轴套与调整环",rings),Tuple.Create("通盖",through),Tuple.Create("油封轴径",seals),Tuple.Create("旋转截面",sections),Tuple.Create("总装基准",datums)}) {
                var page=new TabPage(item.Item1); page.Controls.Add(item.Item2); tabs.TabPages.Add(page);
            }
            Columns(shaft,"轴段","直径","长度"); shaft.Columns[0].ReadOnly=true;
            for(int i=0;i<parameters.shaft_segments.Length;i++) shaft.Rows.Add(i+1,parameters.shaft_segments[i].diameter,parameters.shaft_segments[i].length);
            if(parameters.gears!=null) {
                foreach(var item in new[]{Tuple.Create("高速齿轮轴",highShaft),Tuple.Create("高速轴键槽",highKeys),Tuple.Create("齿轮副",gearData)}) {
                    var page=new TabPage(item.Item1);page.Controls.Add(item.Item2);tabs.TabPages.Add(page);
                }
                Columns(highShaft,"轴段","类型","直径","长度");highShaft.Columns[0].ReadOnly=true;highShaft.Columns[1].ReadOnly=true;
                var g=parameters.gears;
                for(int i=0;i<g.high_sections.Length;i++) {
                    var s=g.high_sections[i];int row=highShaft.Rows.Add(i+1,s.kind=="gear"?"整体齿轮":"圆柱轴段",s.diameter,s.length);
                    if(s.kind=="gear")highShaft.Rows[row].Cells[2].ReadOnly=true;
                }
                Columns(highKeys,"段内起点","总长度","宽度","切入深度");
                var k=g.high_keyway;highKeys.Rows.Add(k.start,k.length,k.width,k.depth);
                Columns(gearData,"参数","数值","来源");gearData.Columns[0].ReadOnly=true;gearData.Columns[2].ReadOnly=true;
                string[] labels={"模数","压力角（度）","小齿轮齿数","大齿轮齿数","大齿轮齿宽","大齿轮轴孔","键槽宽度","轮毂键槽深度","大齿轮相位（度）"};
                double[] values={g.module,g.pressure_angle,g.pinion_teeth,g.driven_teeth,g.driven_width,g.driven_bore,g.driven_key_width,g.driven_key_depth,g.driven_mesh_phase_deg};
                for(int i=0;i<labels.Length;i++) {
                    bool linked=i==5||i==6||i==7;
                    bool source=parameters.housing!=null&&(i==0||i==2||i==3||i==4);
                    int row=gearData.Rows.Add(labels[i],values[i],linked?"联动轴和齿轮平键":source?"随箱体设计导入":"可编辑");
                    gearData.Rows[row].Cells[1].ReadOnly=linked||source;
                }
                if(parameters.housing!=null)foreach(DataGridViewRow row in highShaft.Rows)if(g.high_sections[row.Index].kind=="gear")row.Cells[3].ReadOnly=true;
            }
            if((parameters.bearings??new BearingSpec[0]).Length>0) {
                var page=new TabPage("轴承");page.Controls.Add(bearingData);tabs.TabPages.Add(page);
                Columns(bearingData,"零件","型号 / 说明","内径","外径","宽度");bearingData.Columns[0].ReadOnly=true;
                foreach(var b in parameters.bearings)bearingData.Rows.Add(b.name,b.catalog_name,b.inner_diameter,b.outer_diameter,b.width);
                // Bearing bores and housing seats are one imported design. A new
                // bearing size requires regenerating the housing recipe too.
                if(parameters.housing!=null)bearingData.ReadOnly=true;
            }
            Columns(keys,"所在轴段","段内起点","总长度","宽度","切入深度");
            foreach(var key in parameters.keyways) {
                double offset=0; int index=-1;
                for(int i=0;i<parameters.shaft_segments.Length;i++) {
                    var s=parameters.shaft_segments[i];
                    if(key.start>=offset-1e-8 && key.start+key.length<=offset+s.length+1e-8 && Math.Abs(key.diameter-s.diameter)<1e-8) {index=i;break;}
                    offset+=s.length;
                }
                if(index<0) throw new ArgumentException("键槽无法匹配轴段。");
                keys.Rows.Add(index+1,key.start-offset,key.length,key.width,key.depth);
            }
            Columns(covers,"零件","外径","台阶直径","凹槽直径","总厚度","台阶高度","凹槽深度"); covers.Columns[0].ReadOnly=true;
            foreach(var pair in new[]{Tuple.Create("低速轴闷盖",parameters.blank_cover),Tuple.Create("高速轴闷盖",parameters.high_blank_cover)}) {
                var c=pair.Item2; if(c!=null) covers.Rows.Add(pair.Item1,c.outer_diameter,c.step_diameter,c.inner_recess_diameter,c.total_thickness,c.step_height,c.recess_depth);
            }
            Columns(rings,"零件标识","外径","内径","厚度","倒角"); rings.Columns[0].ReadOnly=true;
            foreach(var r in parameters.accessories??new Ring[0]) rings.Rows.Add(r.name,r.outer_diameter,r.inner_diameter,r.thickness,r.chamfer);
            Columns(through,"零件","台阶类型","序号","起始直径","终止直径","长度");foreach(int i in new[]{0,1,2})through.Columns[i].ReadOnly=true;
            Columns(seals,"通盖","油封孔径 / 轴径","联动来源");seals.Columns[0].ReadOnly=true;seals.Columns[2].ReadOnly=true;
            foreach(var c in parameters.through_covers??new ThroughCover[0]) {
                for(int i=0;i<c.outer_steps.Length;i++) {
                    int row=through.Rows.Add(c.name,"外台阶",i+1,c.outer_steps[i][0],c.outer_steps[i][0],c.outer_steps[i][1]);
                    through.Rows[row].Tag=Tuple.Create(c,true,i);through.Rows[row].Cells[4].ReadOnly=true;
                }
                for(int i=0;i<c.inner_steps.Length;i++) {
                    int row=through.Rows.Add(c.name,"内腔",i+1,c.inner_steps[i][0],c.inner_steps[i][1],c.inner_steps[i][2]);
                    through.Rows[row].Tag=Tuple.Create(c,false,i);
                }
                bool high=c.name=="high_through_cover"&&parameters.gears!=null;
                int seal=seals.Rows.Add(c.name,c.seal_bore_diameter,c.seal_shaft_segment_index.HasValue?"低速轴第 "+(c.seal_shaft_segment_index.Value+1)+" 段":high?"高速轴第 1 段":"手工输入");
                seals.Rows[seal].Cells[1].ReadOnly=c.seal_shaft_segment_index.HasValue||high;
            }
            Columns(sections,"零件","顶点","轴向位置","半径");sections.Columns[0].ReadOnly=true;sections.Columns[1].ReadOnly=true;
            foreach(var part in parameters.rotary_parts??new RotaryPart[0])for(int i=0;i<part.section.Length;i++) {
                int row=sections.Rows.Add(part.name,i+1,part.section[i][0],part.section[i][1]);sections.Rows[row].Tag=Tuple.Create(part,i);
            }
            Columns(datums,"装配参考量","数值");datums.Columns[0].ReadOnly=true;
            if(parameters.assembly_layout!=null) {
                var d=parameters.assembly_layout;
                string[] names={"低速轴中心 X","高速轴中心 X","轴线高度 Z","箱体凸台总宽","啮合中心 Y","低速轴参考轴承宽度","高速轴参考轴承宽度"};
                double[] values={d.low_center_x,d.high_center_x,d.axis_height,d.boss_depth,d.mesh_center_y,d.low_bearing_width,d.high_bearing_width};
                for(int i=0;i<names.Length;i++) {
                    int row=datums.Rows.Add(names[i],values[i]);
                    if(parameters.housing!=null||((parameters.bearings??new BearingSpec[0]).Length>0&&i>=5))datums.Rows[row].Cells[1].ReadOnly=true;
                }
            }
            var note=new Label{Dock=DockStyle.Top,Height=85,Padding=new Padding(10),Text="修改轴段后，键槽、平键、齿轮孔及油封孔径一起更新；生成前核对轴承安装位置。\n齿轮副、两根轴和轴承可在各页查看。带箱体的参数文件锁定中心距、齿数及轴承规格，变更这些尺寸请先从源设计重新导出完整参数。\n生成所有已配置零件和一个一级总装；已有参数文件与模型保留。"};
            var actions=new FlowLayoutPanel{Dock=DockStyle.Bottom,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,FlowDirection=FlowDirection.RightToLeft,Padding=new Padding(8)};
            var cancel=ActionButton("取消",100);cancel.DialogResult=DialogResult.Cancel;
            var generate=ActionButton("生成模型",125);
            var save=ActionButton("另存参数",125);
            generate.Click+=(s,e)=>{
                try {
                    Commit();
                    if(chooseOutputDirectory) {
                        ModelOutputRoot=Generator.ChooseOutputDirectory(this,"选择模型保存目录。零件和总装会保存在新的子文件夹中。",Path.Combine(Generator.ProjectRoot(),"output"));
                        if(ModelOutputRoot==null)return;
                    }
                    SnapshotPath=Snapshot();
                    DialogResult=DialogResult.OK;
                    Close();
                }catch(Exception ex){MessageBox.Show(this,ex.Message,"参数检查");}
            };
            save.Click+=(s,e)=>{try {Commit(); using(var dialog=new SaveFileDialog{Filter="参数文件 (*.json)|*.json",FileName="reducer_parameters.json"}) if(dialog.ShowDialog(this)==DialogResult.OK) File.WriteAllText(dialog.FileName,new JavaScriptSerializer().Serialize(parameters));}catch(Exception ex){MessageBox.Show(this,ex.Message,"参数检查");}};
            actions.Controls.Add(cancel); actions.Controls.Add(generate); actions.Controls.Add(save);
            Controls.Add(tabs); Controls.Add(note); Controls.Add(addMates); Controls.Add(actions); CancelButton=cancel;
            AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;
            ResumeLayout(false);
        }
        internal static Button ActionButton(string text,int width) {
            return new Button {Text=text,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,
                MinimumSize=new Size(width,34),Padding=new Padding(8,4,8,4)};
        }
        void Commit() {
            foreach(var grid in new[]{shaft,keys,covers,rings,through,seals,sections,datums,highShaft,highKeys,gearData,bearingData}) grid.EndEdit();
            for(int i=0;i<parameters.shaft_segments.Length;i++) {parameters.shaft_segments[i].diameter=Number(shaft,i,1);parameters.shaft_segments[i].length=Number(shaft,i,2);}
            for(int i=0;i<parameters.keyways.Length;i++) {
                double raw=Number(keys,i,0); int index=(int)raw-1;
                if(raw!=Math.Truncate(raw) || index<0 || index>=parameters.shaft_segments.Length) throw new ArgumentException("键槽所在轴段必须是有效的整数编号。");
                double within=Number(keys,i,1); if(within<0) throw new ArgumentException("键槽段内起点不能为负。");
                var k=parameters.keyways[i]; k.segment_index=index;
                k.start=parameters.shaft_segments.Take(index).Sum(s=>s.length)+within; k.diameter=parameters.shaft_segments[index].diameter;
                k.length=Number(keys,i,2); k.width=Number(keys,i,3); k.depth=Number(keys,i,4);
            }
            if(parameters.gears!=null) {
                var g=parameters.gears;
                g.module=Number(gearData,0,1);g.pressure_angle=Number(gearData,1,1);
                double pinion=Number(gearData,2,1),driven=Number(gearData,3,1);
                if(pinion!=Math.Truncate(pinion)||driven!=Math.Truncate(driven))throw new ArgumentException("齿数必须是整数。");
                g.pinion_teeth=(int)pinion;g.driven_teeth=(int)driven;g.driven_width=Number(gearData,4,1);g.driven_mesh_phase_deg=Number(gearData,8,1);
                for(int i=0;i<g.high_sections.Length;i++) {
                    var s=g.high_sections[i];s.diameter=s.kind=="gear"?g.module*(g.pinion_teeth-2.5):Number(highShaft,i,2);s.length=Number(highShaft,i,3);
                    highShaft.Rows[i].Cells[2].Value=s.diameter;
                }
                var k=g.high_keyway;k.start=Number(highKeys,0,0);k.length=Number(highKeys,0,1);k.width=Number(highKeys,0,2);k.depth=Number(highKeys,0,3);k.diameter=g.high_sections[0].diameter;
                g.driven_bore=parameters.shaft_segments[1].diameter;
            }
            for(int i=0;i<(parameters.bearings??new BearingSpec[0]).Length;i++) {
                var b=parameters.bearings[i];b.catalog_name=Convert.ToString(bearingData.Rows[i].Cells[1].Value);b.inner_diameter=Number(bearingData,i,2);b.outer_diameter=Number(bearingData,i,3);b.width=Number(bearingData,i,4);
            }
            var coverList=new List<Cover>{parameters.blank_cover}; if(parameters.high_blank_cover!=null)coverList.Add(parameters.high_blank_cover);
            for(int i=0;i<coverList.Count;i++) {var c=coverList[i];c.outer_diameter=Number(covers,i,1);c.step_diameter=Number(covers,i,2);c.inner_recess_diameter=Number(covers,i,3);c.total_thickness=Number(covers,i,4);c.step_height=Number(covers,i,5);c.recess_depth=Number(covers,i,6);}
            for(int i=0;i<(parameters.accessories??new Ring[0]).Length;i++) {var r=parameters.accessories[i];r.outer_diameter=Number(rings,i,1);r.inner_diameter=Number(rings,i,2);r.thickness=Number(rings,i,3);r.chamfer=Number(rings,i,4);}
            for(int row=0;row<through.Rows.Count;row++) {
                var tag=(Tuple<ThroughCover,bool,int>)through.Rows[row].Tag;
                if(tag.Item2) {tag.Item1.outer_steps[tag.Item3][0]=Number(through,row,3);tag.Item1.outer_steps[tag.Item3][1]=Number(through,row,5);through.Rows[row].Cells[4].Value=Number(through,row,3);}
                else {tag.Item1.inner_steps[tag.Item3][0]=Number(through,row,3);tag.Item1.inner_steps[tag.Item3][1]=Number(through,row,4);tag.Item1.inner_steps[tag.Item3][2]=Number(through,row,5);}
            }
            for(int i=0;i<(parameters.through_covers??new ThroughCover[0]).Length;i++) {
                var c=parameters.through_covers[i];c.seal_bore_diameter=c.seal_shaft_segment_index.HasValue?parameters.shaft_segments[c.seal_shaft_segment_index.Value].diameter:Number(seals,i,1);
                if(c.name=="high_through_cover"&&parameters.gears!=null)c.seal_bore_diameter=parameters.gears.high_sections[0].diameter;
                seals.Rows[i].Cells[1].Value=c.seal_bore_diameter;
            }
            for(int row=0;row<sections.Rows.Count;row++) {
                var tag=(Tuple<RotaryPart,int>)sections.Rows[row].Tag;tag.Item1.section[tag.Item2][0]=Number(sections,row,2);tag.Item1.section[tag.Item2][1]=Number(sections,row,3);
            }
            if(parameters.assembly_layout!=null) {
                var d=parameters.assembly_layout;d.low_center_x=Number(datums,0,1);d.high_center_x=Number(datums,1,1);d.axis_height=Number(datums,2,1);d.boss_depth=Number(datums,3,1);d.mesh_center_y=Number(datums,4,1);d.low_bearing_width=Number(datums,5,1);d.high_bearing_width=Number(datums,6,1);
            }
            SynchronizeLinkedParts();
            Generator.Validate(parameters);
        }
        void SynchronizeLinkedParts() {
            var d=parameters.assembly_layout;
            foreach(var a in parameters.auxiliary??new AuxiliarySpec[0]) {
                if(a.kind!="key")continue;
                bool high=a.shaft_link=="high";
                Keyway k=high?parameters.gears==null?null:parameters.gears.high_keyway:parameters.keyways.FirstOrDefault(key=>key.segment_index==a.segment_index);
                if(k==null)throw new ArgumentException("平键缺少对应轴槽："+a.name);
                double before=high?parameters.gears.high_sections.Take(a.segment_index).Sum(s=>s.length):parameters.shaft_segments.Take(a.segment_index).Sum(s=>s.length);
                a.length=k.length;a.width=k.width;a.start_x=k.start;a.start_offset=k.start-before;a.shaft_diameter=k.diameter;a.shaft_slot_depth=k.depth;a.start_z=k.diameter/2-k.depth;
                a.reference_volume_mm3=((a.length-a.width)*a.width+Math.PI*a.width*a.width/4)*a.height;
                if(d!=null) {
                    double origin=d.mesh_center_y;
                    if(high){foreach(var s in parameters.gears.high_sections){origin+=s.kind=="gear"?s.length/2:s.length;if(s.kind=="gear")break;}}
                    else origin+=parameters.shaft_segments[0].length+parameters.shaft_segments[1].length/2;
                    a.placement=new[]{0.0,-1,0,1,0,0,0,0,1,high?d.high_center_x:d.low_center_x,origin,d.axis_height};
                }
                if(!high&&a.segment_index==1&&parameters.gears!=null) {
                    parameters.gears.driven_key_width=a.width;
                    parameters.gears.driven_key_depth=a.height-k.depth+1;
                }
            }
            if(parameters.gears!=null) {
                gearData.Rows[5].Cells[1].Value=parameters.gears.driven_bore;gearData.Rows[6].Cells[1].Value=parameters.gears.driven_key_width;gearData.Rows[7].Cells[1].Value=parameters.gears.driven_key_depth;
            }
        }
        string Snapshot() {
            string folder=Path.Combine(Generator.ProjectRoot(),"parameter_sets");Directory.CreateDirectory(folder);
            string path=Path.Combine(folder,"parameters_"+DateTime.Now.ToString("yyyyMMdd_HHmmss_fff")+"_"+Guid.NewGuid().ToString("N").Substring(0,6)+".json");
            File.WriteAllText(path,new JavaScriptSerializer().Serialize(parameters));return path;
        }
    }
}
