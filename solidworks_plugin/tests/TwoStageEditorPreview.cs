using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using ReducerSW;
class TwoStageEditorPreview {
    [STAThread] static void Main(string[] args) {
        Application.EnableVisualStyles();
        using(var form=new TwoStageEditor(args[0])) {
            form.StartPosition=FormStartPosition.Manual;form.Location=new Point(10,10);form.Show();Application.DoEvents();
            if(args.Length>2) {
                var tabs=form.Controls.OfType<TabControl>().Single();
                tabs.SelectedTab=tabs.TabPages.Cast<TabPage>().Single(page=>page.Text==args[2]);
                Application.DoEvents();
            }
            using(var image=new Bitmap(form.Width,form.Height)) {
                form.DrawToBitmap(image,new Rectangle(0,0,form.Width,form.Height));image.Save(args[1]);
            }
        }
    }
}
