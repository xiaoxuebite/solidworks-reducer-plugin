using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using ReducerSW;

class GenerationControlSmoke {
    static readonly BindingFlags Private=BindingFlags.NonPublic|BindingFlags.Instance;
    static void Check(bool condition,string message) {
        if(!condition)throw new Exception(message);
    }

    [STAThread] static int Main() {
        try {
            Application.EnableVisualStyles();
            var type=typeof(Generator).Assembly.GetType("ReducerSW.GenerationControl",true);
            var start=type.GetMethod("Start");
            var checkpoint=type.GetMethod("Checkpoint");
            Action<object,Action<Form,Button,Button>> onWindow=(control,action)=>{
                var form=(Form)type.GetField("form",Private).GetValue(control);
                var pause=(Button)type.GetField("pause",Private).GetValue(control);
                var stop=(Button)type.GetField("stop",Private).GetValue(control);
                using(var done=new ManualResetEventSlim()) {
                    Exception error=null;
                    form.BeginInvoke((Action)(()=>{
                        try {action(form,pause,stop);}
                        catch(Exception ex) {error=ex;}
                        finally {done.Set();}
                    }));
                    Check(done.Wait(3000),"Progress UI did not respond while the generating thread waited.");
                    if(error!=null)throw error;
                }
            };
            using(var control=(IDisposable)start.Invoke(null,null)) {
                const string partStep="正在生成：输入轴";
                const string assemblyStep="总装定位：高速大齿轮（2/103）";
                checkpoint.Invoke(null,new object[]{partStep});
                onWindow(control,(form,pause,stop)=>{
                    var status=form.Controls.OfType<Label>().Single();
                    Check(status.Text==partStep,"The progress window did not display the current part.");
                    Check(form.MinimizeBox&&form.ControlBox&&form.ShowInTaskbar,"Missing progress window controls.");
                    var bar=form.Controls.OfType<TableLayoutPanel>().Single();
                    foreach(var button in new[]{pause,stop}) {
                        var text=TextRenderer.MeasureText(button.Text,button.Font);
                        Check(bar.ClientRectangle.Contains(button.Bounds)&&text.Height<button.Height&&text.Width<button.Width,"Button text is clipped.");
                    }
                    form.WindowState=FormWindowState.Minimized;
                    Check(form.WindowState==FormWindowState.Minimized,"Progress window did not minimize.");
                    form.WindowState=FormWindowState.Normal;
                    pause.PerformClick();
                    Check(status.Text.Contains(partStep),"Requesting pause hid the current part.");
                    var timer=new System.Windows.Forms.Timer{Interval=400};
                    timer.Tick+=(s,e)=>{timer.Dispose();pause.PerformClick();};
                    timer.Start();
                });
                var clock=Stopwatch.StartNew();
                checkpoint.Invoke(null,new object[]{assemblyStep});
                Check(clock.ElapsedMilliseconds>=250,"Pause did not wait for Resume.");
                onWindow(control,(form,pause,stop)=>
                    Check(form.Controls.OfType<Label>().Single().Text==assemblyStep,"Resume did not show the named assembly step."));
            }
            foreach(bool closeWindow in new[]{false,true}) {
                using(var control=(IDisposable)start.Invoke(null,null)) {
                    onWindow(control,(form,pause,stop)=>{
                        pause.PerformClick();
                        if(closeWindow)form.Close();else stop.PerformClick();
                        Check(!stop.Enabled&&!pause.Enabled,"Stop request did not update the window.");
                    });
                    bool stopped=false;
                    try {checkpoint.Invoke(null,new object[]{"stop at checkpoint"});}
                    catch(TargetInvocationException e) {
                        if(!(e.InnerException is OperationCanceledException))throw;
                        stopped=true;
                    }
                    Check(stopped,"Closing or stopping did not cancel generation.");
                }
            }
            Console.WriteLine("Independent progress UI, layout, minimize, pause, resume and cancellation: PASS");return 0;
        } catch(Exception error) {Console.Error.WriteLine(error);return 1;}
    }
}
