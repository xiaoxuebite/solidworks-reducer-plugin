using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using ReducerSW;
class EditorSmoke {
    [STAThread] static int Main(string[] args) {
        try {
            var flags=BindingFlags.NonPublic|BindingFlags.Instance;
            string before=File.ReadAllText(args[0]);
            using(var editor=new ParameterEditor(args[0])) {
                var type=typeof(ParameterEditor);
                var addMates=(CheckBox)type.GetField("addMates",flags).GetValue(editor);
                if(!editor.AddAssemblyMates||addMates.Parent!=editor)throw new Exception("Assembly mates option must be visible and checked by default.");
                addMates.Checked=false;
                if(editor.AddAssemblyMates)throw new Exception("Editor did not return the fixed assembly option.");
                var shaft=(DataGridView)type.GetField("shaft",flags).GetValue(editor);
                var p=(Parameters)type.GetField("parameters",flags).GetValue(editor);
                double old=p.shaft_segments[0].length;
                double[] starts={p.keyways[0].start,p.keyways[1].start};
                shaft.Rows[0].Cells[2].Value=old+1;
                type.GetMethod("Commit",flags).Invoke(editor,null);
                if(p.keyways[0].start!=starts[0]+1 || p.keyways[1].start!=starts[1]+1) throw new Exception("Keyway offsets did not follow shaft edit.");
                if(File.ReadAllText(args[0])!=before) throw new Exception("Editor overwrote the original parameter file.");
                if(p.gears!=null) {
                    foreach(var a in p.auxiliary??new AuxiliarySpec[0])if(a.kind=="key"&&a.shaft_link=="low") {
                        var key=Array.Find(p.keyways,k=>k.segment_index==a.segment_index);
                        if(key==null||Math.Abs(a.start_x-key.start)>1e-8||Math.Abs(a.start_z-(key.diameter/2-key.depth))>1e-8)throw new Exception("Physical key did not follow the low shaft edit.");
                    }
                    var high=(DataGridView)type.GetField("highKeys",flags).GetValue(editor);
                    double start=p.gears.high_keyway.start;high.Rows[0].Cells[0].Value=start+1;
                    type.GetMethod("Commit",flags).Invoke(editor,null);
                    var highKey=Array.Find(p.auxiliary??new AuxiliarySpec[0],a=>a.name=="key_drive_outer");
                    if(highKey!=null&&Math.Abs(highKey.start_x-start-1)>1e-8)throw new Exception("Physical high-shaft key did not follow its slot.");
                    var gears=(DataGridView)type.GetField("gearData",flags).GetValue(editor);
                    if(p.housing!=null&&!gears.Rows[0].Cells[1].ReadOnly)throw new Exception("Housing-dependent module must not be independently editable.");
                    double bore=p.shaft_segments[1].diameter;shaft.Rows[1].Cells[1].Value=bore-1;
                    type.GetMethod("Commit",flags).Invoke(editor,null);
                    if(p.gears.driven_bore!=bore-1)throw new Exception("Driven gear bore did not follow shaft seat.");
                    shaft.Rows[1].Cells[1].Value=bore;type.GetMethod("Commit",flags).Invoke(editor,null);
                }
                if(p.through_covers!=null&&p.through_covers.Length>0) {
                    double diameter=p.shaft_segments[4].diameter;
                    shaft.Rows[4].Cells[1].Value=diameter-1;
                    type.GetMethod("Commit",flags).Invoke(editor,null);
                    if(p.through_covers[0].seal_bore_diameter!=diameter-1)throw new Exception("Linked low-speed seal bore did not follow the shaft.");
                    shaft.Rows[4].Cells[1].Value=diameter;
                    type.GetMethod("Commit",flags).Invoke(editor,null);
                }
                shaft.Rows[0].Cells[1].Value=-1;
                bool rejected=false;
                try {type.GetMethod("Commit",flags).Invoke(editor,null);}catch(TargetInvocationException e){rejected=e.InnerException is ArgumentException;}
                if(!rejected) throw new Exception("Invalid diameter was accepted.");
            }
            Console.WriteLine("Editor edit/offset/validation/source-preservation: PASS");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
