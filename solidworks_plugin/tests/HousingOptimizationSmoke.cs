using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using ReducerSW;

// Offline only: validate and optimize JSON without creating a SolidWorks COM object.
class HousingOptimizationSmoke {
    static HouseFeatureSpec Circle(string name,double x,double depth=3) {
        return new HouseFeatureSpec {name=name,kind="cut",origin=new double[]{0,0,0},u=new double[]{1,0,0},v=new double[]{0,1,0},depth=depth,
            curves=new[]{new HouseCurve {kind="circle",center=new[]{x,0.0},radius=1}}};
    }
    static void Assert(bool value,string message) {if(!value)throw new Exception(message);}
    static object Canonical(object value) {
        var dictionary=value as Dictionary<string,object>;
        if(dictionary!=null)return new SortedDictionary<string,object>(dictionary.ToDictionary(p=>p.Key,p=>Canonical(p.Value)));
        var array=value as object[];return array==null?value:array.Select(Canonical).ToArray();
    }
    static string Stable(JavaScriptSerializer json,object value) {return json.Serialize(Canonical(json.DeserializeObject(json.Serialize(value))));}
    static int Main(string[] args) {
        try {
            var json=new JavaScriptSerializer {MaxJsonLength=Int32.MaxValue};
            var root=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(args[0]));
            var spec=json.ConvertToType<HousingSpec>(root.ContainsKey("housing")?root["housing"]:root);
            Assert(ModelPresentation.ChineseName("output_ShaftRevolve")=="输出轴旋转","Shaft feature label must be Chinese from creation.");
            Assert(ModelPresentation.ChineseName("Keyway_1")=="键槽1","Keyway label must be Chinese from creation.");
            Assert(ModelPresentation.ChineseName("Keyway_2_Profile")=="键槽2草图","Keyway sketch label must be Chinese from creation.");
            Assert(ModelPresentation.ChineseName("high_wheel_Involute_Blank")=="高速大齿轮齿坯","Two-stage gear blank must not be named as a blind cover.");
            Assert(ModelPresentation.ChineseName("DrivenGear_InvoluteExtrude_Blank")=="从动齿轮齿坯","One-stage gear blank must be named as tooth stock.");
            Assert(ModelPresentation.ChineseName("low_blank_cover")=="低速闷盖","Blind cover translation must remain intact.");
            string original=Stable(json,spec);var result=Generator.OptimizeHousing(spec);
            Assert(Stable(json,spec)==original,"Optimizer changed the source recipe.");
            Assert(Stable(json,Generator.OptimizeHousing(result))==Stable(json,result),"Optimizer is not idempotent.");
            Assert(!result.upper.Any(f=>f.name.StartsWith("Jack_Screw_Hole_",StringComparison.Ordinal)||
                (f.source_features??new string[0]).Any(n=>n.StartsWith("Jack_Screw_Hole_",StringComparison.Ordinal))),
                "Retired jacking holes survived optimization.");
            foreach(var feature in result.lower.Concat(result.upper)) {
                string translated=Regex.Replace(ModelPresentation.ChineseName(feature.name),@"(?:M|R)\d+","");
                Assert(!Regex.IsMatch(translated,@"[A-Za-z]{2,}"),"Untranslated housing feature: "+feature.name+" -> "+translated);
            }
            if(spec.lower.Length==54&&spec.upper.Length==30)
                Assert(result.lower.Length==30&&result.upper.Length==18,"Unexpected optimized feature counts.");
            if(spec.lower.Count(f=>f.name.StartsWith("Side_Oil_Groove_",StringComparison.Ordinal))==10) {
                var active=result.lower.Single(f=>f.name=="Side_Oil_Groove_Active_Combined");
                var bridge=result.lower.Single(f=>f.name=="Side_Oil_Groove_Bridge_Combined");
                Assert(active.source_features.Length==6&&active.curves.Length==24,"Active oil grooves were not combined.");
                Assert(bridge.source_features.Length==4&&bridge.curves.Length==16,"Bridge oil grooves were not combined.");
            }
            foreach(var arrays in new[]{new[]{spec.lower,result.lower},new[]{spec.upper,result.upper}})
                foreach(string name in new[]{"High_Bearing_Bore","Low_Bearing_Bore"}) {
                    var before=arrays[0].Single(f=>f.name==name);var after=arrays[1].Single(f=>f.name==name);
                    Assert(Stable(json,before)==Stable(json,after),"Bearing interface changed: "+name);
                }
            // Touching/overlapping circles stay as separate features, including special user recipes.
            foreach(double distance in new[]{1.5,2.0,2.0000000005})
                Assert(Generator.OptimizeHousingFeatures(new[]{Circle("Flange_Fastener_Hole_1",0),Circle("Flange_Fastener_Hole_2",distance)}).Length==2,"Unsafe circle merge.");
            Assert(Generator.OptimizeHousingFeatures(new[]{Circle("Flange_Fastener_Hole_1",0),Circle("Flange_Fastener_Hole_2",3)}).Length==1,"Disjoint circle merge missing.");
            Assert(Generator.OptimizeHousingFeatures(new[]{Circle("Flange_Fastener_Hole_1",0),Circle("Flange_Fastener_Hole_2",3,4)}).Length==2,"Different depths merged.");
            var shifted=Circle("Flange_Fastener_Hole_2",3);shifted.origin[2]=1;
            Assert(Generator.OptimizeHousingFeatures(new[]{Circle("Flange_Fastener_Hole_1",0),shifted}).Length==2,"Different sketch planes merged.");
            var barrier=Circle("Barrier",10);barrier.kind="boss";
            Assert(Generator.OptimizeHousingFeatures(new[]{Circle("Flange_Fastener_Hole_1",0),barrier,Circle("Flange_Fastener_Hole_2",3)}).Length==3,"Merged across an additive feature.");
            var invalid=json.Deserialize<HousingSpec>(json.Serialize(spec));
            invalid.lower[0].curves=new[]{new HouseCurve {kind="line",start=new double[]{0,0},end=new double[]{1,0}}};
            bool rejected=false;try {Generator.ValidateHousing(invalid);} catch(ArgumentException) {rejected=true;}
            Assert(rejected,"Open housing contour accepted.");
            rejected=false;try {Generator.OptimizeHousingFeatures(new[]{Circle("Side_Oil_Groove_-1_Continuous",0)});} catch(ArgumentException) {rejected=true;}
            Assert(rejected,"Unsafe earlier combined side groove accepted.");
            if(args.Length>1)File.WriteAllText(args[1],json.Serialize(result));
            Console.WriteLine("Offline housing optimization: PASS ("+spec.lower.Length+" -> "+result.lower.Length+" lower, "+spec.upper.Length+" -> "+result.upper.Length+" upper)");
            return 0;
        } catch(Exception ex) {Console.Error.WriteLine(ex);return 1;}
    }
}
