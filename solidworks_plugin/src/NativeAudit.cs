using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ReducerSW {
    // Inspect saved output without rebuilding or saving the source documents.
    // Documents already open in the user's session are reused and kept open.
    public static class NativeAudit {
        public sealed class PartMetric {
            public string file {get;set;} public int solid_bodies {get;set;}
            public double volume_mm3 {get;set;} public bool multiple_bodies_allowed {get;set;}
        }
        public sealed class Manifest {
            public int schema_version {get;set;} public PartMetric[] parts {get;set;}
            public string assembly {get;set;} public int component_count {get;set;}
            public AssemblyInstance[] instances {get;set;}
            public TwoStageMateRecord[] top_level_mates {get;set;}
            public TwoStageMateRecord[] native_mates {get;set;}
            public bool? assembly_mates_added {get;set;}
        }
        public sealed class FeatureRecord {
            public string name, type; public int depth, error; public bool warning;
        }
        public sealed class FeatureAudit {
            public readonly List<FeatureRecord> features=new List<FeatureRecord>();
            public readonly Dictionary<string,int> feature_types=new Dictionary<string,int>();
            public readonly List<string> issues=new List<string>();
        }
        static IntPtr Identity(object item) {
            IntPtr pointer=Marshal.GetIUnknownForObject(item);
            Marshal.Release(pointer);return pointer;
        }
        static void VisitFeatures(Feature first,int depth,HashSet<IntPtr> visited,FeatureAudit result) {
            var siblingChain=new HashSet<IntPtr>();
            for(var feature=first;feature!=null;feature=(Feature)feature.GetNextSubFeature()) {
                IntPtr id=Identity(feature);
                if(!siblingChain.Add(id))break;
                VisitFeature(feature,depth,visited,result);
            }
        }
        static void VisitFeature(Feature feature,int depth,HashSet<IntPtr> visited,FeatureAudit result) {
            if(feature==null||!visited.Add(Identity(feature)))return;
            if(visited.Count>10000)throw new Exception("Feature traversal exceeded its safety bound.");
            bool warning;int error=feature.GetErrorCode2(out warning);
            string type=feature.GetTypeName2()??"",name=feature.Name;
            result.features.Add(new FeatureRecord{name=name,type=type,depth=depth,error=error,warning=warning});
            int count;result.feature_types.TryGetValue(type,out count);result.feature_types[type]=count+1;
            if(error!=0&&!warning)result.issues.Add("Feature error: "+name+" ("+type+") code="+error);
            if(type.StartsWith("Imported",StringComparison.OrdinalIgnoreCase)||type=="BaseBody")
                result.issues.Add("Imported/base-body feature found: "+name+" ("+type+")");
            VisitFeatures((Feature)feature.GetFirstSubFeature(),depth+1,visited,result);
        }
        internal static FeatureAudit InspectFeatures(ModelDoc2 doc) {
            var result=new FeatureAudit();var visited=new HashSet<IntPtr>();var roots=new HashSet<IntPtr>();
            for(var feature=(Feature)doc.FirstFeature();feature!=null;feature=(Feature)feature.GetNextFeature()) {
                if(!roots.Add(Identity(feature)))break;
                VisitFeature(feature,0,visited,result);
            }
            return result;
        }
        static ModelDoc2 Open(SldWorks app,string path,int type,out bool owned) {
            var existing=(ModelDoc2)app.GetOpenDocumentByName(path);
            owned=existing==null;if(existing!=null)return existing;
            int error=0,warning=0;
            var doc=(ModelDoc2)app.OpenDoc6(path,type,
                (int)(swOpenDocOptions_e.swOpenDocOptions_Silent|swOpenDocOptions_e.swOpenDocOptions_ReadOnly),
                "",ref error,ref warning);
            if(doc==null||error!=0)throw new Exception("Audit open failed: "+path+" error="+error+" warning="+warning);
            return doc;
        }
        static string SafePath(string folder,string filename) {
            if(String.IsNullOrWhiteSpace(filename)||Path.GetFileName(filename)!=filename)
                throw new ArgumentException("Audit manifest must contain simple file names.");
            return Path.Combine(folder,filename);
        }
        internal static List<object> ScanInterferences(AssemblyDoc assembly,List<string> issues) {
            var reports=new List<object>();
            var detector=assembly.InterferenceDetectionManager;
            try {
                detector.TreatCoincidenceAsInterference=false;
                detector.ShowIgnoredInterferences=true;
                detector.IncludeMultibodyPartInterferences=false;
                foreach(Interference hit in detector.GetInterferences() as object[]??new object[0]) {
                    var pair=hit.Components as object[]??new object[0];
                    if(pair.Length!=2)continue;
                    var first=pair[0] as Component2;var second=pair[1] as Component2;
                    if(first==null||second==null)continue;
                    string a=Path.GetFileName(first.GetPathName()),b=Path.GetFileName(second.GetPathName());
                    double volume=hit.Volume*1e9;
                    reports.Add(new {first_file=a,second_file=b,volume_mm3=volume,
                        allowed=false,possible=hit.IsPossibleInterference});
                    if(volume>0.1)issues.Add("Solid interference: "+a+" / "+b+
                        " volume="+volume.ToString("0.###",CultureInfo.InvariantCulture)+" mm3");
                }
            } finally {detector.Done();}
            return reports;
        }
        public static string RunInterferenceOnly(SldWorks app,string directory) {
            string folder=Path.GetFullPath(directory);
            string assemblyPath=Path.Combine(folder,"reducer_assembly.SLDASM");
            var before=new HashSet<IntPtr>();
            foreach(object item in app.GetDocuments() as object[]??new object[0])before.Add(Identity(item));
            ModelDoc2 doc=null;bool owned=false;
            var issues=new List<string>();List<object> reports;
            try {
                doc=Open(app,assemblyPath,2,out owned);
                reports=ScanInterferences((AssemblyDoc)doc,issues);
            } finally {
                if(owned&&doc!=null)app.CloseDoc(doc.GetTitle());
                foreach(object item in app.GetDocuments() as object[]??new object[0]) {
                    var opened=item as ModelDoc2;
                    if(opened==null||before.Contains(Identity(opened)))continue;
                    string path=opened.GetPathName();
                    if(!String.IsNullOrEmpty(path)&&path.StartsWith(folder+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                        app.CloseDoc(opened.GetTitle());
                }
            }
            string reportPath=Path.Combine(folder,"interference_report.json");
            File.WriteAllText(reportPath,new JavaScriptSerializer().Serialize(new {schema_version=1,
                created_utc=DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture),
                passed=issues.Count==0,interferences=reports,issues=issues}));
            Console.WriteLine("Assembly interference: "+(issues.Count==0?"PASS":"FAIL")+"; "+reportPath);
            if(issues.Count>0)throw new Exception(String.Join(System.Environment.NewLine,issues));
            return reportPath;
        }
        public static string Run(SldWorks app,string directory) {
            string folder=Path.GetFullPath(directory);
            var json=new JavaScriptSerializer{MaxJsonLength=16*1024*1024};
            var manifest=json.Deserialize<Manifest>(File.ReadAllText(Path.Combine(folder,"parts_manifest.json")));
            if(manifest==null||manifest.parts==null||manifest.parts.Length==0)throw new ArgumentException("Audit needs a non-empty parts manifest.");
            var parameters=json.Deserialize<Parameters>(File.ReadAllText(Path.Combine(folder,"parameters.json")));
            var initiallyOpen=new HashSet<IntPtr>();
            foreach(object item in app.GetDocuments() as object[]??new object[0])initiallyOpen.Add(Identity(item));
            var ownedPaths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var parts=new List<object>();var issues=new List<string>();object assemblyReport=null;
            string reportPath=Path.Combine(folder,"native_audit.json");
            try {
                foreach(var metric in manifest.parts) {
                    string path=SafePath(folder,metric.file);ModelDoc2 doc=null;bool owned=false;
                    try {
                        long lengthBefore=new FileInfo(path).Length;DateTime writeBefore=File.GetLastWriteTimeUtc(path);
                        doc=Open(app,path,1,out owned);if(owned)ownedPaths.Add(path);
                        bool dirtyBefore=doc.GetSaveFlag();
                        if(doc.GetType()!=1)throw new Exception("Expected a native part document.");
                        var bodies=(object[])((PartDoc)doc).GetBodies2((int)swBodyType_e.swSolidBody,false);
                        int bodyCount=bodies==null?0:bodies.Length;
                        int torusBodies=0,sphereBodies=0;
                        foreach(Body2 body in bodies??new object[0]) {
                            bool torus=false,sphere=false;
                            foreach(Face2 face in body.GetFaces() as object[]??new object[0]) {
                                var surface=(Surface)face.GetSurface();
                                if(surface==null)continue;
                                torus|=surface.IsTorus();sphere|=surface.IsSphere();
                            }
                            if(torus)torusBodies++;if(sphere)sphereBodies++;
                        }
                        var features=InspectFeatures(doc);var localIssues=new List<string>(features.issues);
                        if(parameters.hardware_version==1) {
                            var expectedAxial=new Dictionary<string,double[]> {
                                {"gb_hex_bolt_m8x50.SLDPRT",new[]{-5.3,50.0}},
                                {"gb_hex_bolt_m8x25.SLDPRT",new[]{-5.3,25.0}},
                                {"gb_hex_nut_m8.SLDPRT",new[]{0.0,6.8}},
                                {"gb_spring_washer_8.SLDPRT",new[]{0.0,2.0}},
                                {"gb_pan_screw_m3x12.SLDPRT",new[]{0.0,14.4}},
                                {"gb_jack_bolt_m5x10.SLDPRT",new[]{-3.5,10.0}},
                                {"gb_taper_pin_4x20.SLDPRT",new[]{0.0,20.0}},
                                {"gb_oil_plug_m8x1.SLDPRT",new[]{-5.3,10.0}},
                                {"gb_flat_washer_8.SLDPRT",new[]{0.0,1.6}},
                                {"gb_o_ring_19x1p8.SLDPRT",new[]{0.0,1.8}}
                            };
                            double[] axial;
                            if(expectedAxial.TryGetValue(metric.file,out axial)&&bodies!=null) {
                                double min=Double.PositiveInfinity,max=Double.NegativeInfinity;
                                foreach(Body2 body in bodies) {
                                    var box=(double[])body.GetBodyBox();
                                    if(box==null||box.Length<6)continue;
                                    min=Math.Min(min,box[0]*1000);max=Math.Max(max,box[3]*1000);
                                }
                                if(Math.Abs(min-axial[0])>.15||Math.Abs(max-axial[1])>.15)
                                    localIssues.Add("Hardware axial size is wrong: "+metric.file+" actual="+min.ToString("0.###")+".."+max.ToString("0.###")+
                                        " expected="+axial[0]+".."+axial[1]+" mm");
                            }
                        }
                        bool bearing=(parameters.bearings??new BearingSpec[0]).Any(b=>b.name==Path.GetFileNameWithoutExtension(metric.file));
                        if(bearing&&torusBodies!=2)localIssues.Add("Both bearing race bodies must have a true toroidal raceway.");
                        if(bearing) {
                            var spec=parameters.bearings.First(b=>b.name==Path.GetFileNameWithoutExtension(metric.file));
                            try {Generator.ValidateBearingGeometry(doc,spec);}
                            catch(Exception geometry){localIssues.Add("Bearing rolling geometry: "+geometry.Message);}
                        }
                        if(bodyCount!=metric.solid_bodies||bodyCount==0||(!metric.multiple_bodies_allowed&&bodyCount!=1))
                            localIssues.Add("Solid body count differs from manifest: "+bodyCount+" expected="+metric.solid_bodies);
                        // In SW 2024, MassProperty2.Recalculate can reuse the saved
                        // low-accuracy integral. Force the documented maximum
                        // calculation first, then read MassProperty2 consistently.
                        int massStatus;doc.ClearSelection2(true);
                        var maximum=(double[])doc.Extension.GetMassProperties2(2,out massStatus,false);
                        if(maximum==null||maximum.Length<4||massStatus!=0)throw new Exception("Maximum-accuracy mass calculation failed.");
                        var mass=(MassProperty2)doc.Extension.CreateMassProperty2();
                        if(mass==null)throw new Exception("CreateMassProperty2 returned no mass-property object.");
                        mass.UseSystemUnits=true;mass.IncludeHiddenBodiesOrComponents=true;mass.AccuracyLevel=2;
                        if(!mass.Recalculate())throw new Exception("High-accuracy mass calculation failed.");
                        double volume=mass.Volume*1e9;
                        if(Double.IsNaN(volume)||Double.IsInfinity(volume)||volume<=0)localIssues.Add("Invalid high-accuracy volume.");
                        var auxiliary=(parameters.auxiliary??new AuxiliarySpec[0]).FirstOrDefault(a=>a.name==Path.GetFileNameWithoutExtension(metric.file));
                        double? reference=auxiliary!=null&&auxiliary.reference_volume_mm3>0?(double?)auxiliary.reference_volume_mm3:null;
                        bool dirtyAfter=doc.GetSaveFlag();
                        if(dirtyBefore!=dirtyAfter)localIssues.Add("Document save flag changed during read-only audit.");
                        bool fileUnchanged=lengthBefore==new FileInfo(path).Length&&writeBefore==File.GetLastWriteTimeUtc(path);
                        if(!fileUnchanged)localIssues.Add("Source file timestamp or length changed during audit.");
                        foreach(string issue in localIssues)issues.Add(metric.file+": "+issue);
                        parts.Add(new {file=metric.file,already_open=!owned,audited_unsaved_state=dirtyBefore,
                            save_flag_before=dirtyBefore,save_flag_after=dirtyAfter,source_file_unchanged=fileUnchanged,
                            solid_bodies=bodyCount,manifest_solid_bodies=metric.solid_bodies,
                            torus_bodies_count=torusBodies,sphere_bodies_count=sphereBodies,
                            volume_mm3_high_accuracy=volume,mass_accuracy_level=mass.AccuracyLevel,
                            manifest_volume_mm3=metric.volume_mm3,manifest_delta_mm3=volume-metric.volume_mm3,
                            reference_volume_mm3=reference,reference_delta_mm3=reference.HasValue?(double?)(volume-reference.Value):null,
                            feature_types=features.feature_types,features=features.features,issues=localIssues});
                        Console.WriteLine("Audit "+metric.file+": bodies="+bodyCount+" volume="+volume.ToString("R",CultureInfo.InvariantCulture)+" mm3");
                    } catch(Exception ex) {issues.Add(metric.file+": "+ex.Message);parts.Add(new {file=metric.file,error=ex.ToString()});}
                    finally {if(owned&&doc!=null)app.CloseDoc(doc.GetTitle());}
                }
                string assemblyFile=String.IsNullOrEmpty(manifest.assembly)?"reducer_assembly.SLDASM":manifest.assembly;
                string assemblyPath=SafePath(folder,assemblyFile);ModelDoc2 assemblyDoc=null;bool assemblyOwned=false;
                try {
                    assemblyDoc=Open(app,assemblyPath,2,out assemblyOwned);if(assemblyOwned)ownedPaths.Add(assemblyPath);
                    bool dirtyBefore=assemblyDoc.GetSaveFlag();
                    var assembly=(AssemblyDoc)assemblyDoc;
                    var all=assembly.GetComponents(false) as object[]??new object[0];
                    var top=assembly.GetComponents(true) as object[]??new object[0];
                    var localIssues=new List<string>();var components=new List<object>();int sealCount=0;
                    bool twoStage=parameters.schema_version==5;
                    bool fixedLayout=manifest.assembly_mates_added==false;
                    bool oneStageNative=parameters.schema_version==4&&manifest.native_mates!=null&&manifest.native_mates.Length>0;
                    bool plannedNative=!fixedLayout&&(twoStage||oneStageNative);
                    bool checkPlacements=plannedNative||fixedLayout;
                    if(all.Length!=manifest.component_count)localIssues.Add("Component count "+all.Length+" differs from manifest "+manifest.component_count+".");
                    if(top.Length!=all.Length)localIssues.Add("Nested assembly components found.");
                    var expectedCounts=manifest.instances==null?new Dictionary<string,int>():manifest.instances.GroupBy(i=>i.file,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.Count(),StringComparer.OrdinalIgnoreCase);
                    var actualCounts=new Dictionary<string,int>(StringComparer.OrdinalIgnoreCase);
                    var remainingPlacements=checkPlacements?(manifest.instances??new AssemblyInstance[0]).ToList():null;
                    foreach(Component2 component in all) {
                        string componentFile=Path.GetFileName(component.GetPathName());int count;
                        actualCounts.TryGetValue(componentFile,out count);actualCounts[componentFile]=count+1;
                        var part=(ModelDoc2)component.GetModelDoc2();bool resolved=part!=null;
                        if(!resolved)localIssues.Add("Unresolved component: "+component.Name2);
                        else if(part.GetType()!=1)localIssues.Add("Component is not a part: "+component.Name2);
                        var mates=component.GetMates() as object[]??new object[0];
                        bool fixedComponent=component.IsFixed();int constrained=component.GetConstrainedStatus();
                        bool seal=Path.GetFileNameWithoutExtension(componentFile).EndsWith("_oil_seal",StringComparison.OrdinalIgnoreCase);
                        if(fixedLayout) {
                            if(!fixedComponent||mates.Length!=0)localIssues.Add("Fixed layout component must be fixed with no mates: "+component.Name2);
                        }
                        else if(seal){sealCount++;if(fixedComponent||constrained!=3||mates.Length<3)localIssues.Add("Oil seal lacks its three constraining mates: "+component.Name2);}
                        else if(plannedNative) {
                            bool grounded=String.Equals(componentFile,"housing.SLDPRT",StringComparison.OrdinalIgnoreCase);
                            if(grounded) {if(!fixedComponent)localIssues.Add("Housing is not grounded.");}
                            else if(fixedComponent||mates.Length<(twoStage?2:1))localIssues.Add("Component is fixed or lacks its required native mates: "+component.Name2+" fixed="+fixedComponent+" mates="+mates.Length);
                        }
                        else if(!fixedComponent&&mates.Length==0)localIssues.Add("Floating component has no real mate: "+component.Name2);
                        if(checkPlacements) {
                            var transform=component.Transform2 as MathTransform;
                            var data=transform==null?null:transform.ArrayData as double[];
                            var matching=remainingPlacements.Where(i=>String.Equals(i.file,componentFile,StringComparison.OrdinalIgnoreCase))
                                .OrderBy(i=>data==null?Double.PositiveInfinity:Math.Abs(data[9]-i.x/1000)+Math.Abs(data[10]-i.y/1000)+Math.Abs(data[11]-i.z/1000)).FirstOrDefault();
                            if(matching==null||data==null)localIssues.Add("Component placement cannot be checked: "+component.Name2);
                            else {
                                remainingPlacements.Remove(matching);
                                double[] expected={0,matching.axis_y_direction,0,-matching.axis_y_direction,0,0,0,0,1,
                                    matching.x/1000,matching.y/1000,matching.z/1000};
                                if(matching.placement!=null)for(int k=0;k<12;k++)expected[k]=k<9?matching.placement[k]:matching.placement[k]/1000;
                                if(Enumerable.Range(0,9).Any(k=>Math.Abs(data[k]-expected[k])>1e-4)||
                                   Enumerable.Range(9,3).Any(k=>Math.Abs(data[k]-expected[k])>0.00005))
                                    localIssues.Add("Component differs from its planned transform: "+component.Name2);
                            }
                        }
                        components.Add(new {name=component.Name2,file=componentFile,resolved=resolved,is_fixed=fixedComponent,constrained_status=constrained,mate_count=mates.Length});
                    }
                    foreach(var expected in expectedCounts) {int count;actualCounts.TryGetValue(expected.Key,out count);if(count!=expected.Value)localIssues.Add("Component copies differ: "+expected.Key+" actual="+count+" expected="+expected.Value);}
                    foreach(string actual in actualCounts.Keys)if(expectedCounts.Count>0&&!expectedCounts.ContainsKey(actual))localIssues.Add("Unexpected component file: "+actual);
                    if(checkPlacements&&remainingPlacements.Count>0)localIssues.Add("Planned placements not found: "+remainingPlacements.Count);
                    var features=InspectFeatures(assemblyDoc);localIssues.AddRange(features.issues);
                    int coincident=features.features.Count(f=>f.type=="MateCoincident");
                    if(manifest.schema_version>=2&&coincident<sealCount*3)localIssues.Add("Missing top-level oil-seal plane mates.");
                    var gearMateReports=new List<object>();
                    if(oneStageNative) {
                        var records=manifest.native_mates;
                        if(records.Length<manifest.component_count-1)localIssues.Add("One-stage native mate manifest is incomplete: "+records.Length);
                        var actualMates=new HashSet<string>(features.features.Where(f=>f.type.StartsWith("Mate")&&f.type!="MateGroup").Select(f=>f.name),StringComparer.Ordinal);
                        foreach(var record in records)if(record==null||String.IsNullOrWhiteSpace(record.feature)||!actualMates.Contains(record.feature))
                            localIssues.Add("One-stage mate feature missing after reopen: "+(record==null?"null":record.feature));
                        if(records.Count(r=>r!=null&&r.kind=="gear")!=1)localIssues.Add("Expected one-stage gear motion mate.");
                        if(features.features.Count(f=>f.type=="MateConcentric")<12)localIssues.Add("Too few cylindrical concentric mates in one-stage assembly.");
                    }
                    if(fixedLayout) {
                        if((manifest.top_level_mates??new TwoStageMateRecord[0]).Length!=0||
                            (manifest.native_mates??new TwoStageMateRecord[0]).Length!=0||
                            features.features.Any(f=>f.type.StartsWith("Mate")&&f.type!="MateGroup"))
                            localIssues.Add("Fixed layout contains unexpected mate features.");
                    } else if(twoStage) {
                        var records=manifest.top_level_mates??new TwoStageMateRecord[0];
                        if(records.Length<57)localIssues.Add("Two-stage native mate manifest is incomplete: "+records.Length);
                        var actualMates=new HashSet<string>(features.features.Where(f=>f.type.StartsWith("Mate")&&f.type!="MateGroup").Select(f=>f.name),StringComparer.Ordinal);
                        foreach(var record in records)if(record==null||String.IsNullOrWhiteSpace(record.feature)||!actualMates.Contains(record.feature))
                            localIssues.Add("Two-stage mate feature missing after reopen: "+(record==null?"null":record.feature));
                        if(records.Count(r=>r!=null&&r.kind=="gear")!=2)localIssues.Add("Expected two stage gear motion mates.");
                        if(features.features.Count(f=>f.type=="MateConcentric")<12)localIssues.Add("Too few cylindrical concentric mates in two-stage assembly.");
                    }
                    if(plannedNative) {
                        var gearPairs=twoStage?new[]{new{name="TS_high_stage_gear_ratio",driver="input_shaft-1",driven="high_wheel-1",pinion=parameters.two_stage.high_pair.pinion_teeth,wheel=parameters.two_stage.high_pair.wheel_teeth},
                                                     new{name="TS_low_stage_gear_ratio",driver="low_pinion-1",driven="low_wheel-1",pinion=parameters.two_stage.low_pair.pinion_teeth,wheel=parameters.two_stage.low_pair.wheel_teeth}}
                            :new[]{new{name="TS_one_stage_gear_ratio",driver="drive_gear_shaft-1",driven="driven_gear-1",pinion=parameters.gears.pinion_teeth,wheel=parameters.gears.driven_teeth}};
                        foreach(var stage in gearPairs) {
                            var feature=Generator.AssemblyMateFeatures(assemblyDoc).FirstOrDefault(f=>f.Name==stage.name);
                            var gearData=feature==null?null:feature.GetDefinition() as IGearMateFeatureData;
                            var mate=feature==null?null:feature.GetSpecificFeature2() as Mate2;
                            var firstComponent=mate!=null&&mate.GetMateEntityCount()==2?mate.MateEntity(0).ReferenceComponent:null;
                            var secondComponent=mate!=null&&mate.GetMateEntityCount()==2?mate.MateEntity(1).ReferenceComponent:null;
                            string first=firstComponent==null?null:firstComponent.Name2;
                            string second=secondComponent==null?null:secondComponent.Name2;
                            string firstFile=firstComponent==null?null:Path.GetFileName(firstComponent.GetPathName());
                            string secondFile=secondComponent==null?null:Path.GetFileName(secondComponent.GetPathName());
                            double numerator=gearData==null?0:gearData.GearRatioNumerator;
                            double denominator=gearData==null?0:gearData.GearRatioDenominator;
                            // AddMate5 may serialize the same selected gear pair as wheel/pinion.
                            bool toothRatio=denominator>0&&numerator>0&&
                                (Math.Abs(numerator/denominator-(double)stage.pinion/stage.wheel)<1e-8||
                                 Math.Abs(numerator/denominator-(double)stage.wheel/stage.pinion)<1e-8);
                            if(feature==null||feature.GetTypeName2()!="MateGearDim"||gearData==null||!toothRatio)
                                localIssues.Add("Gear motion mate ratio differs from tooth counts: "+stage.name+
                                    " actual="+(gearData==null?"definition missing":numerator+"/"+denominator)+
                                    " tooth pair="+stage.pinion+"/"+stage.wheel);
                            string driverFile=stage.driver.Substring(0,stage.driver.LastIndexOf('-'))+".SLDPRT";
                            string drivenFile=stage.driven.Substring(0,stage.driven.LastIndexOf('-'))+".SLDPRT";
                            if(!String.Equals(firstFile,driverFile,StringComparison.OrdinalIgnoreCase)||
                               !String.Equals(secondFile,drivenFile,StringComparison.OrdinalIgnoreCase))
                                localIssues.Add("Gear motion mate references wrong shaft pair: "+stage.name+" actual="+first+"/"+second);
                            gearMateReports.Add(new {name=stage.name,driver=first,driven=second,driver_file=firstFile,driven_file=secondFile,numerator=numerator,
                                denominator=denominator,reverse=gearData!=null&&gearData.Reverse,tooth_ratio_matches=toothRatio});
                        }
                    }
                    var interferenceReports=new List<object>();
                    if(oneStageNative)interferenceReports=ScanInterferences(assembly,localIssues);
                    bool dirtyAfter=assemblyDoc.GetSaveFlag();if(dirtyBefore!=dirtyAfter)localIssues.Add("Assembly save flag changed during read-only audit.");
                    foreach(string issue in localIssues)issues.Add(assemblyFile+": "+issue);
                    assemblyReport=new {file=assemblyFile,already_open=!assemblyOwned,save_flag_before=dirtyBefore,save_flag_after=dirtyAfter,
                        component_count=all.Length,top_level_count=top.Length,is_flat=top.Length==all.Length,components=components,
                        feature_types=features.feature_types,features=features.features,gear_mates=gearMateReports,
                        interferences=interferenceReports,issues=localIssues};
                    Console.WriteLine("Audit assembly: "+all.Length+" components; top-level="+top.Length+"; coincident mates="+coincident);
                } catch(Exception ex) {issues.Add(assemblyFile+": "+ex.Message);assemblyReport=new {file=assemblyFile,error=ex.ToString()};}
                finally {if(assemblyOwned&&assemblyDoc!=null)app.CloseDoc(assemblyDoc.GetTitle());}
            } finally {
                // Assembly loading can also open dependent parts. Close only files
                // in this manifest that the audit opened, preserving all initial docs.
                var manifestPaths=new HashSet<string>(manifest.parts.Select(m=>SafePath(folder,m.file)),StringComparer.OrdinalIgnoreCase);
                manifestPaths.UnionWith(ownedPaths);
                foreach(object item in app.GetDocuments() as object[]??new object[0]) {
                    var doc=item as ModelDoc2;if(doc==null||initiallyOpen.Contains(Identity(doc)))continue;
                    if(manifestPaths.Contains(doc.GetPathName()))app.CloseDoc(doc.GetTitle());
                }
            }
            var report=new {schema_version=1,created_utc=DateTime.UtcNow.ToString("o",CultureInfo.InvariantCulture),
                scope="Read-only native feature, high-accuracy volume and flat assembly audit; no rebuild or source save.",
                source_directory=folder,passed=issues.Count==0,part_count=parts.Count,parts=parts,assembly=assemblyReport,issues=issues};
            File.WriteAllText(reportPath,json.Serialize(report));
            if(issues.Count>0)throw new Exception("Native audit found "+issues.Count+" issue(s). Report: "+reportPath+System.Environment.NewLine+String.Join(System.Environment.NewLine,issues));
            Console.WriteLine("Native audit: PASS. "+reportPath);return reportPath;
        }
    }
}
