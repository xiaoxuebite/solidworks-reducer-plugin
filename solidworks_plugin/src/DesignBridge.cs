using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ReducerSW {
    // Run the shared calculation engine in a short-lived local process.
    // Neither an HTTP server nor a CadQuery runtime is needed for calculations.
    public static class DesignBridge {
        static JavaScriptSerializer Json() {return new JavaScriptSerializer {MaxJsonLength=32*1024*1024,RecursionLimit=128};}
        static string Argument(string value) {
            var result=new StringBuilder("\"");int slashes=0;
            foreach(char c in value) {
                if(c=='\\'){slashes++;continue;}
                if(c=='\"'){result.Append('\\',slashes*2+1);result.Append(c);slashes=0;continue;}
                result.Append('\\',slashes);slashes=0;result.Append(c);
            }
            result.Append('\\',slashes*2);return result.Append('"').ToString();
        }
        static string Tail(string value,int limit) {return value==null?"":value.Length<=limit?value:value.Substring(value.Length-limit);}
        public static Dictionary<string,object> Call(Dictionary<string,object> request) {
            if(request==null||!request.ContainsKey("action"))throw new ArgumentException("请选择设计操作。");
            string root=Generator.ProjectRoot(),script=Path.Combine(root,"design_bridge.py");
            if(!File.Exists(script))throw new FileNotFoundException("设计计算组件不完整，请保留完整插件目录。",script);
            string work=Path.Combine(root,".bridge",DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff")+"_"+Guid.NewGuid().ToString("N").Substring(0,8));
            Directory.CreateDirectory(work);string input=Path.Combine(work,"request.json"),output=Path.Combine(work,"response.json");
            File.WriteAllText(input,Json().Serialize(request),new UTF8Encoding(false));
            string python=Environment.GetEnvironmentVariable("REDUCER_SW_PYTHON");
            string prefix="";
            if(String.IsNullOrWhiteSpace(python)) {
                string launcher=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"py.exe");
                python=File.Exists(launcher)?launcher:"py.exe";prefix="-3.12 ";
            } else {
                python=Environment.ExpandEnvironmentVariables(python.Trim().Trim('"'));
                if(!File.Exists(python))throw new FileNotFoundException("配置的设计计算环境不存在，请检查 REDUCER_SW_PYTHON。",python);
            }
            var start=new ProcessStartInfo {FileName=python,Arguments=prefix+Argument(script)+" --request "+Argument(input)+" --response "+Argument(output),
                WorkingDirectory=root,UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,
                RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
            start.EnvironmentVariables["PYTHONIOENCODING"]="utf-8";start.EnvironmentVariables["PYTHONUTF8"]="1";
            string stdout="",stderr="";int exitCode;
            try {
                using(var process=Process.Start(start)) {
                    if(process==null)throw new Exception("无法启动设计计算。");
                    var outTask=process.StandardOutput.ReadToEndAsync();var errTask=process.StandardError.ReadToEndAsync();
                    if(!process.WaitForExit(180000)) {process.Kill();throw new TimeoutException("设计计算超过三分钟，请缩小选型范围后重试。");}
                    if(!Task.WaitAll(new Task[]{outTask,errTask},10000))throw new Exception("设计计算未能完成输出，请重试。");
                    stdout=outTask.Result;stderr=errTask.Result;exitCode=process.ExitCode;
                }
            } catch(Win32Exception) {throw new Exception("未找到 Python 3.12 设计计算环境。请安装项目依赖，或用 REDUCER_SW_PYTHON 指定现有解释器。");}
            File.WriteAllText(Path.Combine(work,"engine.log"),Tail(stdout,32768)+Environment.NewLine+Tail(stderr,32768),new UTF8Encoding(false));
            if(!File.Exists(output))throw new Exception("设计计算未返回结果。"+(String.IsNullOrWhiteSpace(stderr)?"":Environment.NewLine+Tail(stderr,1600)));
            var response=Json().Deserialize<Dictionary<string,object>>(File.ReadAllText(output,Encoding.UTF8));
            if(response==null)throw new Exception("设计计算返回了空结果。");
            object ok;if(!response.TryGetValue("ok",out ok)||!(ok is bool)||(bool)ok==false) {
                object detail;response.TryGetValue("error",out detail);throw new ArgumentException(Convert.ToString(detail)??"请检查设计输入。");
            }
            if(exitCode!=0)throw new Exception("设计计算异常退出："+exitCode+Environment.NewLine+Tail(stderr,1600));
            object model;
            if(response.TryGetValue("model_parameters_file",out model)&&model!=null) {
                string path=Convert.ToString(model);
                if(!File.Exists(path))throw new Exception("设计参数文件未能保存。");
                Generator.Validate(Json().Deserialize<Parameters>(File.ReadAllText(path,Encoding.UTF8)));
            }
            return response;
        }
    }
}
