using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ReducerSW {
    // English filenames and recipe keys remain stable; native features are
    // named in Chinese immediately after creation so the live tree is readable.
    public static class ModelPresentation {
        static readonly Dictionary<string,string> Words=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase) {
            {"high","高速"},{"low","低速"},{"input","输入"},{"output","输出"},{"intermediate","中间"},
            {"drive","主动"},{"driven","从动"},{"shaft","轴"},{"gear","齿轮"},{"pinion","小齿轮"},{"wheel","大齿轮"},
            {"bearing","轴承"},{"housing","箱体"},{"cover","盖"},{"blank","闷"},{"through","透"},
            {"front","前"},{"rear","后"},{"end","端"},{"adjusting","调整"},{"ring","环"},
            {"rolling","滚动"},{"elements","体"},{"spherical","球面"},{"start","起始"},{"path","路径"},
            {"steps","台阶"},{"parallel","平行"},{"and","与"},{"opening","开口"},{"body","主体"},
            {"cone","锥面"},{"breather","通气"},{"four","四"},{"scallops","扇形缺口"},
            {"hexagonal","六角"},{"trim","修整"},{"raceway","滚道"},{"seed","单个"},
            {"ball","滚珠"},{"core","主体"},{"gap","齿槽"},
            {"sleeve","轴套"},{"oil","油"},{"seal","封"},{"retaining","挡"},{"level","位"},
            {"indicator","指示器"},{"vent","通气"},{"plug","塞"},{"inspection","检查"},
            {"gasket","垫片"},{"key","平键"},{"keyway","键槽"},{"bolt","螺栓"},{"screw","螺钉"},
            {"nut","螺母"},{"washer","垫圈"},{"flat","平"},{"spring","弹簧"},
            {"hex","六角"},{"socket","内六角"},{"pan","盘头"},{"jack","起盖"},
            {"taper","锥"},{"pin","销"},{"locating","定位"},{"drain","放油"},
            {"plate","板"},{"two","二级"},{"gb","国标"},{"o","O"},
            {"rib","肋板"},{"boss","凸台"},{"reinforcement","加强块"},{"reinforcements","加强块"},
            {"flange","法兰"},{"hole","孔"},{"holes","孔"},{"bore","孔"},{"counterbore","沉孔"},
            {"cut","切除"},{"extrude","拉伸"},{"extrusion","拉伸"},{"revolve","旋转"},
            {"profile","草图"},{"sketch","草图"},{"plane","基准面"},{"axis","轴线"},
            {"involute","渐开线"},{"twisted","扭转"},{"tooth","齿"},{"pattern","阵列"},
            {"circular","圆周"},{"combined","合并"},{"continuous","连续"},{"chamfer","倒角"},
            {"chamfers","倒角"},{"thread","装饰螺纹"},{"shank","杆"},{"nominal","公称"},
            {"head","头"},{"section","截面"},{"side","侧"},{"wall","壁"},
            {"base","底"},{"split","分型"},{"groove","槽"},{"cheek","侧板"},
            {"clearance","通孔"},{"tap","螺孔"},{"taps","螺孔"},{"diameter","直径"},
            {"radius","半径"},{"station","轴向位置"},{"axial","轴向"},{"radial","径向"}
            ,{"vertical","纵向"},{"top","上"},{"bottom","下"},{"inner","内"},{"outer","外"}
            ,{"active","主体"},{"bridge","连接"},{"major","大径"}
            ,{"drill","底孔"},{"aperture","开口"},{"tangent","相切"},{"shell","壳体"}
            ,{"channel","流道"},{"floor","底面"},{"sloped","斜面"},{"raised","凸起"}
            ,{"cavity","腔"},{"relief","避让"},{"slot","槽"},{"right","右"},{"left","左"}
            ,{"pad","加强座"},{"fastener","紧固件"},{"dowel","定位销"},{"arc","圆弧"}
            ,{"upper","上"},{"lower","下"},{"seat","座"},{"center","中央"}
        };
        public static string ChineseName(string raw) {
            if(String.IsNullOrEmpty(raw))return raw;
            string stem=Path.GetFileNameWithoutExtension(raw);
            var instance=Regex.Match(stem,@"^(.*?)(-\d+)$");
            string suffix=instance.Success?instance.Groups[2].Value:"";
            if(instance.Success)stem=instance.Groups[1].Value;
            if(stem=="cover")return "箱盖"+suffix;
            if(stem=="housing")return "下箱体"+suffix;
            if(stem=="reducer_assembly")return "减速器总装"+suffix;
            if(stem=="driven_gear")return "从动大齿轮"+suffix;
            if(stem=="drive_gear_shaft")return "主动齿轮轴"+suffix;
            // A gear blank is a cylindrical tooth stock; "闷" applies only to blank covers.
            var gearBlank=Regex.Match(stem,@"^(.*(?:gear|wheel|pinion))_Involute(?:Extrude)?_Blank$",RegexOptions.IgnoreCase);
            if(gearBlank.Success)return ChineseName(gearBlank.Groups[1].Value)+"齿坯"+suffix;
            if(stem.StartsWith("Side_Oil_Groove_-1_",StringComparison.OrdinalIgnoreCase))
                stem="下侧油槽_"+stem.Substring("Side_Oil_Groove_-1_".Length);
            if(stem.StartsWith("Side_Oil_Groove_1_",StringComparison.OrdinalIgnoreCase))
                stem="上侧油槽_"+stem.Substring("Side_Oil_Groove_1_".Length);
            stem=Regex.Replace(stem,@"(?<=[a-z])(?=[A-Z])","_");
            var translated=stem.Split('_').Where(x=>x.Length>0).Select(x=>Words.ContainsKey(x)?Words[x]:x);
            return String.Join("",translated)+suffix;
        }
        public static Feature NameCreated(Feature feature,string recipeName) {
            if(feature==null)throw new ArgumentNullException("feature");
            feature.Name=ChineseName(recipeName);
            return feature;
        }
        static string FeatureName(string original,string type) {
            if(String.IsNullOrEmpty(original)||original.Any(c=>c>127))return original;
            if(type=="RefPlane"&&original.StartsWith("Plane_",StringComparison.Ordinal))return "建模基准面"+original.Substring(6);
            if(type=="RefAxis")return "齿轮旋转轴线";
            string translated=ChineseName(original);
            if(translated==original) {
                string prefix=type=="ProfileFeature"?"轮廓草图":type=="3DProfileFeature"?"三维路径":
                    type=="Sweep"?"扭转扫描":type=="CirPattern"?"圆周阵列":
                    type=="Revolution"?"旋转":type=="Extrusion"||type=="Boss"?"拉伸":
                    type=="Cut"||type=="RevCut"?"切除":type=="Chamfer"?"倒角":
                    type=="Fillet"?"圆角":"特征";
                return prefix+"（"+original+"）";
            }
            return translated;
        }
        static void RenameTree(Feature feature) {
            for(var f=feature;f!=null;f=(Feature)f.GetNextFeature()) {
                string type=f.GetTypeName2();
                if(type=="RefPlane"&&!f.Name.StartsWith("Plane_",StringComparison.Ordinal))continue;
                if(type=="ProfileFeature"||type=="3DProfileFeature"||type=="Sweep"||
                    type=="CirPattern"||type=="Revolution"||type=="Extrusion"||type=="Cut"||
                    type=="Boss"||type=="RevCut"||type=="Chamfer"||type=="Fillet"||type=="ICE"||
                    type=="RefAxis"||type=="RefPlane"||type=="CosmeticThread") {
                    string name=FeatureName(f.Name,type);
                    if(name!=f.Name)f.Name=name;
                }
                var child=(Feature)f.GetFirstSubFeature();if(child!=null)RenameSubTree(child);
            }
        }
        static void RenameSubTree(Feature feature) {
            for(var f=feature;f!=null;f=(Feature)f.GetNextSubFeature()) {
                string type=f.GetTypeName2();
                if(type=="ProfileFeature"||type=="3DProfileFeature") {
                    string name=FeatureName(f.Name,type);if(name!=f.Name)f.Name=name;
                }
            }
        }
        public static void Apply(ModelDoc2 doc,string path) {
            if(doc.GetType()!=1)return;
            string file=Path.GetFileNameWithoutExtension(path),name=ChineseName(file);
            var rgb=file=="housing"?new[]{.25,.42,.70}:file=="cover"?new[]{.40,.64,.83}:
                file=="high_wheel"?new[]{.89,.65,.20}:file=="low_wheel"?new[]{.93,.47,.16}:
                file=="low_pinion"?new[]{.95,.76,.30}:file=="drive_gear_shaft"?new[]{.79,.51,.29}:
                file=="input_shaft"?new[]{.57,.70,.83}:file=="intermediate_shaft"?new[]{.66,.65,.81}:
                file=="output_shaft"||file=="driven_shaft"?new[]{.75,.78,.82}:
                file.Contains("gear")||file.Contains("pinion")||file.Contains("wheel")?new[]{.90,.62,.18}:
                file.Contains("shaft")?new[]{.72,.76,.80}:
                file.Contains("bearing")?new[]{.34,.55,.50}:
                file.Contains("seal")||file.Contains("gasket")?new[]{.16,.22,.26}:
                file.Contains("key")?new[]{.81,.36,.30}:
                file.Contains("bolt")||file.Contains("screw")||file.Contains("nut")||file.Contains("washer")?new[]{.62,.67,.74}:
                new[]{.67,.72,.58};
            doc.MaterialPropertyValues=new[]{rgb[0],rgb[1],rgb[2],.30,.75,.35,.45,0.0,0.0};
            doc.Extension.CustomPropertyManager[""].Add3("中文名称",30,name,2);
            RenameTree((Feature)doc.FirstFeature());
        }
        public static void NameAssemblyComponents(SldWorks app,ModelDoc2 assemblyDoc,IEnumerable<AssemblyInstance> plan,
            IDictionary<string,Component2> components,IEnumerable<TwoStageMateRecord> mates) {
            var renamed=new Dictionary<string,string>(StringComparer.Ordinal);
            int activationError=0;
            if(app.ActivateDoc3(assemblyDoc.GetTitle(),false,0,ref activationError)==null||activationError!=0)
                throw new InvalidOperationException("无法激活总装以设置中文组件名。");
            int preference=(int)swUserPreferenceToggle_e.swExtRefUpdateCompNames;
            bool previous=app.GetUserPreferenceToggle(preference);
            app.SetUserPreferenceToggle(preference,false);
            try {
                foreach(var item in plan) {
                    var component=components[item.name];string original=item.name;
                    string requested=ChineseName(Path.GetFileNameWithoutExtension(item.file));
                    assemblyDoc.ClearSelection2(true);
                    if(!component.Select4(false,null,false))throw new InvalidOperationException("无法选择总装组件："+original);
                    component.Name2=requested;
                    item.name=component.Name2;
                    if(!item.name.StartsWith(requested+"-",StringComparison.Ordinal))
                        throw new InvalidOperationException("总装组件中文命名失败："+original+" -> "+requested+"；实际 "+item.name);
                    renamed[original]=item.name;
                }
            } finally {
                try {assemblyDoc.ClearSelection2(true);}
                finally {app.SetUserPreferenceToggle(preference,previous);}
            }
            if(mates!=null)foreach(var mate in mates) {
                string name;
                if(mate.driver!=null&&renamed.TryGetValue(mate.driver,out name))mate.driver=name;
                if(mate.driven!=null&&renamed.TryGetValue(mate.driven,out name))mate.driven=name;
            }
        }
    }
}
