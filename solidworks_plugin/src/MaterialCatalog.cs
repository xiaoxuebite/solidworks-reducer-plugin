using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ReducerSW {
    internal static class MaterialCatalog {
        internal const string Gear="齿轮", Shaft="轴", Key="平键", Housing="箱体";
        const string Forged="锻钢";
        const string NonMetal="非金属（仅参考）";
        static readonly string[] GearKinds={Forged,"铸钢","灰铸铁","球墨铸铁",NonMetal};
        public sealed class Material {
            public string Name {get;set;}
            public string Use {get;set;}
            public string GearKind {get;set;}
            public double? Hardness {get;set;}
            public double? Contact {get;set;}
            public double? Bending {get;set;}
            public double? ShaftStress {get;set;}
            public double? Shear {get;set;}
            public double? A0 {get;set;}
            public double? KeyPressure {get;set;}
            public string Source {get;set;}
            public Dictionary<string,string> Reference {get;set;}
            internal string GearSelection {get{return Name+"（"+GearKind+"）";}}
        }

        public sealed class SavedCatalog {
            public int Version {get;set;}
            public List<Material> Materials {get;set;}
        }

        static string CatalogPath {get{return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ReducerSW","materials.json");}}
        internal static DataGridView HousingMaterials() {
            var grid=DesignWizard.Grid(true);
            grid.Columns.Add("Name","材料牌号");
            grid.Columns.Add("Source","依据 / 备注");
            foreach(var material in DefaultMaterials().Where(value=>value.Use==Housing))
                grid.Rows.Add(material.Name,material.Reference["Notes"]);
            return grid;
        }
        static void AddAction(FlowLayoutPanel panel,string text,Action action) {
            var button=new Button{Text=text,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,
                MinimumSize=new Size(96,38),Padding=new Padding(12,4,12,4)};
            button.Click+=(s,e)=>action();
            panel.Controls.Add(button);
        }

        internal static string[] Choices(string use,IEnumerable<string> preferred) {
            var choices=preferred.ToList();
            foreach(var entry in DefaultMaterials().Concat(PreviouslySavedMaterials())) {
                if(entry.Use!=use||String.IsNullOrWhiteSpace(entry.Name))continue;
                if(use==Gear) {
                    if(entry.GearKind==NonMetal||!GearKinds.Contains(entry.GearKind)||choices.Contains(entry.Name))continue;
                    choices.Add(entry.GearSelection);
                } else {
                    choices.Add(entry.Name);
                }
            }
            return choices.Distinct().ToArray();
        }

        static IEnumerable<Material> PreviouslySavedMaterials() {
            if(!File.Exists(CatalogPath))return new Material[0];
            try {
                string data=File.ReadAllText(CatalogPath);
                var json=new JavaScriptSerializer();
                if(data.TrimStart().StartsWith("[",StringComparison.Ordinal))
                    return json.Deserialize<List<Material>>(data)??new List<Material>();
                var saved=json.Deserialize<SavedCatalog>(data);
                if(saved!=null&&saved.Version==2&&saved.Materials!=null)return saved.Materials;
            } catch(ArgumentException) {
            } catch(InvalidOperationException) {
            } catch(IOException) {
            } catch(UnauthorizedAccessException) {
            }
            return new Material[0];
        }
        static List<Material> DefaultMaterials() {
            using(var stream=typeof(MaterialCatalog).Assembly.GetManifestResourceStream("ReducerSW.MaterialCatalogDefaults.json"))
            using(var reader=new StreamReader(stream))return new JavaScriptSerializer().Deserialize<List<Material>>(reader.ReadToEnd());
        }

        internal static string EnterMaterial(Form owner,bool gear) {
            using(var dialog=new Form{Text="自定义材料",Font=owner.Font,StartPosition=FormStartPosition.CenterParent,
                FormBorderStyle=FormBorderStyle.FixedDialog,MinimizeBox=false,MaximizeBox=false,AutoSize=true,
                AutoSizeMode=AutoSizeMode.GrowAndShrink}) {
                ToolbarIcons.ApplyWindowIcon(dialog);
                dialog.SuspendLayout();
                var content=new TableLayoutPanel{AutoSize=true,Dock=DockStyle.Fill,ColumnCount=2,Padding=new Padding(12)};
                content.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                content.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                var name=new TextBox{MaxLength=80,Width=295};
                content.Controls.Add(new Label{Text="材料牌号 / 热处理",AutoSize=true,Anchor=AnchorStyles.Left},0,0);
                content.Controls.Add(name,1,0);
                var kind=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=295};
                if(gear) {
                    kind.Items.AddRange(GearKinds.Where(value=>value!=NonMetal).ToArray());
                    content.Controls.Add(new Label{Text="齿轮材料类别",AutoSize=true,Anchor=AnchorStyles.Left},0,1);
                    content.Controls.Add(kind,1,1);
                }
                var buttons=new FlowLayoutPanel{AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,
                    FlowDirection=FlowDirection.RightToLeft,Dock=DockStyle.Fill};
                AddAction(buttons,"取消",()=>{dialog.DialogResult=DialogResult.Cancel;});
                AddAction(buttons,"确定",()=>{
                    if(String.IsNullOrWhiteSpace(name.Text)||name.Text.Trim()==MaterialChoiceCell.Custom||
                        gear&&kind.SelectedIndex<0) {
                        MessageBox.Show(dialog,gear?"请填写材料牌号，并选择齿轮材料类别。":"请填写有效的材料牌号。","自定义材料",
                            MessageBoxButtons.OK,MessageBoxIcon.Information);
                        return;
                    }
                    dialog.DialogResult=DialogResult.OK;
                });
                dialog.AcceptButton=(Button)buttons.Controls[1];
                dialog.CancelButton=(Button)buttons.Controls[0];
                content.Controls.Add(buttons,0,2);
                content.SetColumnSpan(buttons,2);
                dialog.Controls.Add(content);
                dialog.AutoScaleDimensions=new SizeF(96,96);
                dialog.AutoScaleMode=AutoScaleMode.Dpi;
                dialog.ResumeLayout(false);
                if(dialog.ShowDialog(owner)!=DialogResult.OK)return null;
                return gear?name.Text.Trim()+"（"+kind.SelectedItem+"）":name.Text.Trim();
            }
        }
    }

    internal sealed class MaterialChoiceCell : DesignChoiceCell {
        internal const string Custom="自定义材料…";
        bool gear;
        string lastValue="";
        bool restoring;
        bool promptPending;

        public MaterialChoiceCell(){}

        internal MaterialChoiceCell(string use,string[] choices,string value):this() {
            gear=use==MaterialCatalog.Gear;
            Items.Add(Custom);
            Items.AddRange(MaterialCatalog.Choices(use,choices));
            SetValue(value);
        }

        public override object Clone() {
            var clone=(MaterialChoiceCell)base.Clone();
            clone.gear=gear;
            clone.lastValue=lastValue;
            return clone;
        }

        internal void SetValue(object value) {
            string text=Convert.ToString(value);
            if(text==Custom)text=lastValue;
            if(!Items.Contains(text))Items.Add(text);
            lastValue=text;
            Value=text;
        }

        internal bool HandleChoice(Form owner) {
            if(restoring||promptPending)return true;
            string text=Convert.ToString(Value);
            if(text!=Custom) {
                lastValue=text;
                return false;
            }
            restoring=true;
            try {Value=lastValue;} finally {restoring=false;}
            promptPending=true;
            var grid=DataGridView;
            owner.BeginInvoke(new Action(()=>{
                promptPending=false;
                if(owner.IsDisposed||grid.IsDisposed||DataGridView!=grid)return;
                grid.CancelEdit();
                string value=MaterialCatalog.EnterMaterial(owner,gear);
                if(value!=null)SetValue(value);
            }));
            return true;
        }
    }
}
