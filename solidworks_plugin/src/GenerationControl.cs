using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ReducerSW {
    // The progress window has its own message loop. Only the generating
    // thread calls SOLIDWORKS and decides when it is safe to pause or stop.
    internal sealed class GenerationControl : IDisposable {
        [ThreadStatic] static GenerationControl current;
        readonly Thread windowThread;
        Form form;
        Label status;
        Button pause;
        Button stop;
        string currentStep="正在准备建模…";
        Point dragCursor;
        Point dragWindow;
        bool dragging;
        volatile bool paused;
        volatile bool stopRequested;
        volatile bool disposing;
        GenerationControl() {
            var area=Screen.FromPoint(Cursor.Position).WorkingArea;
            var ready=new TaskCompletionSource<bool>();
            windowThread=new Thread(()=>{
                try {
                    BuildWindow(area);
                    form.Shown+=(s,e)=>ready.TrySetResult(true);
                    Application.Run(form);
                } catch(Exception error) {
                    ready.TrySetException(error);
                } finally {
                    if(form!=null)form.Dispose();
                    ready.TrySetException(new InvalidOperationException("建模进度窗口提前退出。"));
                }
            }) {IsBackground=true,Name="Reducer generation progress"};
            windowThread.SetApartmentState(ApartmentState.STA);
            windowThread.Start();
            ready.Task.GetAwaiter().GetResult();
        }
        void BuildWindow(Rectangle area) {
            form=new Form {Text="减速器建模进度",ClientSize=new Size(410,120),
                StartPosition=FormStartPosition.Manual,TopMost=true,ControlBox=true,
                MinimizeBox=true,MaximizeBox=false,ShowInTaskbar=true,
                FormBorderStyle=FormBorderStyle.FixedSingle,Font=new Font("Microsoft YaHei UI",10)};
            form.SuspendLayout();
            ToolbarIcons.ApplyWindowIcon(form);
            form.Location=new Point(area.Right-form.Width-24,area.Bottom-form.Height-24);
            form.Shown+=(s,e)=>form.Location=new Point(Math.Max(area.Left,area.Right-form.Width-24),
                Math.Max(area.Top,area.Bottom-form.Height-24));
            status=new Label {Dock=DockStyle.Fill,Padding=new Padding(12,8,12,4),Text=currentStep};
            // The native title bar is draggable; allow dragging from the
            // status area as well when the cursor starts on its text.
            status.MouseDown+=(s,e)=>{if(e.Button!=MouseButtons.Left)return;
                dragCursor=Cursor.Position;dragWindow=form.Location;dragging=true;};
            status.MouseMove+=(s,e)=>{if(!dragging)return;
                var cursor=Cursor.Position;
                form.Location=new Point(dragWindow.X+cursor.X-dragCursor.X,dragWindow.Y+cursor.Y-dragCursor.Y);};
            status.MouseUp+=(s,e)=>{dragging=false;};
            pause=new Button {Dock=DockStyle.Fill,Text="暂停建模"};
            pause.Click+=(s,e)=>{if(stopRequested)return;paused=!paused;pause.Text=paused?"继续建模":"暂停建模";
                status.Text=paused?currentStep+"\n等待当前操作结束后暂停…":currentStep;};
            stop=new Button {Dock=DockStyle.Fill,Text="停止建模"};
            stop.Click+=(s,e)=>Stop();
            var actions=new TableLayoutPanel {Dock=DockStyle.Bottom,Height=46,Padding=new Padding(8,0,8,5),ColumnCount=2,RowCount=1};
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,50));
            actions.Controls.Add(pause,0,0);actions.Controls.Add(stop,1,0);
            form.FormClosing+=(s,e)=>{
                if(disposing)return;
                e.Cancel=true;
                Stop();
            };
            form.Controls.Add(status);form.Controls.Add(actions);
            form.AutoScaleDimensions=new SizeF(96,96);
            form.AutoScaleMode=AutoScaleMode.Dpi;
            form.ResumeLayout(false);
        }
        public static GenerationControl Start() {
            if(current!=null)throw new InvalidOperationException("建模进度窗口已存在。");
            current=new GenerationControl();
            return current;
        }
        void Post(Action action) {
            try {form.BeginInvoke(action);}
            catch(InvalidOperationException) {if(!disposing)throw;}
        }
        void Stop() {
            stopRequested=true;
            pause.Enabled=stop.Enabled=false;
            status.Text=currentStep+"\n正在停止建模，等待当前操作结束…";
        }
        public static bool RequestStop() {
            var control=current;
            if(control==null||control.disposing)return false;
            control.stopRequested=true;
            control.Post(control.Stop);
            return true;
        }
        public static void Checkpoint(string step) {
            var control=current;if(control==null)return;
            Application.DoEvents();
            bool pausedAtCheckpoint=control.paused;
            control.Post(()=>{
                if(control.stopRequested)return;
                control.currentStep=step;
                control.status.Text=control.paused
                    ? step+(pausedAtCheckpoint?"\n已暂停":"\n等待当前操作结束后暂停…")
                    : step;
            });
            while(control.paused&&!control.stopRequested) {
                Application.DoEvents();Thread.Sleep(80);
            }
            if(control.stopRequested)throw new OperationCanceledException("建模已停止。已保存的文件保留在本次输出目录。");
        }
        public void Dispose() {
            if(disposing)return;
            disposing=true;
            current=null;
            Post(()=>form.Close());
            windowThread.Join();
        }
    }
}
