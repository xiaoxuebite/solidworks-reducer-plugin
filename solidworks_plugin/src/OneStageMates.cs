using System;
using System.Collections.Generic;
using System.Linq;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ReducerSW {
    public static partial class Generator {
        static void ORecord(List<TwoStageMateRecord> records,Component2 driver,Component2 driven,string kind,Feature feature) {
            if(feature==null)throw new Exception("一级配合特征未创建："+driver.Name2+" -> "+driven.Name2);
            records.Add(new TwoStageMateRecord{driver=driver.Name2,driven=driven.Name2,kind=kind,feature=feature.Name});
            Console.WriteLine("  mate "+kind+": "+driver.Name2+" -> "+driven.Name2+" ("+feature.Name+")");
        }
        static int OPlaneForWorldAxis(Component2 part,int axis) {
            var data=(double[])part.Transform2.ArrayData;
            for(int local=0;local<3;local++)if(Math.Abs(data[3*local+axis])>.99999)return new[]{2,1,0}[local];
            throw new Exception("组件基准面不平行总装轴线："+part.Name2+" axis="+axis);
        }
        static void ODatum3(SldWorks app,ModelDoc2 doc,AssemblyDoc assembly,
            Component2 driver,Component2 driven,string label,List<TwoStageMateRecord> records) {
            var a=(double[])driver.Transform2.ArrayData;var b=(double[])driven.Transform2.ArrayData;
            foreach(int axis in new[]{0,1,2}) {
                double distance=Math.Abs(b[9+axis]-a[9+axis])*1000;
                var feature=TPlaneMate(app,doc,assembly,driver,OPlaneForWorldAxis(driver,axis),
                    driven,OPlaneForWorldAxis(driven,axis),distance,label+"_datum_"+axis);
                ORecord(records,driver,driven,distance<1e-6?"coincident":"distance",feature);
            }
        }
        static void OConcentricAxial(SldWorks app,ModelDoc2 doc,AssemblyDoc assembly,
            Component2 driver,Component2 driven,double driverRadius,double drivenRadius,
            Component2 axialDatum,string label,List<TwoStageMateRecord> records) {
            ORecord(records,driver,driven,"concentric",TConcentric(app,doc,assembly,driver,driven,
                driverRadius,drivenRadius,label+"_axis"));
            double distance=Math.Abs(((double[])driven.Transform2.ArrayData)[10]-((double[])axialDatum.Transform2.ArrayData)[10])*1000;
            ORecord(records,axialDatum,driven,distance<1e-6?"coincident":"distance",
                TPlaneMate(app,doc,assembly,axialDatum,OPlaneForWorldAxis(axialDatum,1),
                    driven,OPlaneForWorldAxis(driven,1),distance,label+"_axial"));
        }
        static Feature OLock(SldWorks app,ModelDoc2 doc,AssemblyDoc assembly,
            Component2 driver,Component2 driven,string label) {
            return TAddMate(app,doc,assembly,driven,()=>{
                doc.ClearSelection2(true);
                return driver.Select4(false,null,false)&&driven.Select4(true,null,false);
            },(int)swMateType_e.swMateLOCK,0,label);
        }
        static Feature OAngle(SldWorks app,ModelDoc2 doc,AssemblyDoc assembly,
            Component2 driver,Component2 driven,string label) {
            return TAddMate(app,doc,assembly,driven,()=>{
                var first=TPlane(driver,OPlaneForWorldAxis(driver,2));
                var second=TPlane(driven,OPlaneForWorldAxis(driven,2));
                return first.Select2(false,1)&&second.Select2(true,1);
            },(int)swMateType_e.swMateANGLE,0,label);
        }
        static void OMateSeal(ModelDoc2 doc,AssemblyDoc assembly,Component2 cover,Component2 seal,
            List<TwoStageMateRecord> records) {
            var before=new HashSet<string>(AssemblyMateFeatures(doc).Select(f=>f.Name),StringComparer.Ordinal);
            MateSeal(doc,assembly,cover,seal);
            var created=AssemblyMateFeatures(doc).Where(f=>f.GetTypeName2()=="MateCoincident"&&!before.Contains(f.Name)).ToArray();
            if(created.Length!=3)throw new Exception("一级油封缺少三条原生重合配合："+seal.Name2);
            for(int index=0;index<created.Length;index++) {
                created[index].Name="OS_"+seal.Name2.Replace('-','_')+"_plane"+index;
                ORecord(records,cover,seal,"coincident",created[index]);
            }
        }
        static List<TwoStageMateRecord> MateOneStage(SldWorks app,ModelDoc2 doc,AssemblyDoc assembly,
            Parameters p,Dictionary<string,Component2> parts,List<object> sealMates) {
            var records=new List<TwoStageMateRecord>();var housing=parts["housing"];
            int light=assembly.GetLightWeightComponentCount();
            if(light>0){assembly.ResolveAllLightweight();doc.EditRebuild3();}
            foreach(var pair in parts)if(pair.Key!="housing") {
                doc.ClearSelection2(true);pair.Value.Select4(false,null,false);assembly.UnfixComponent();
                if(pair.Value.IsFixed())throw new Exception("一级组件无法解除固定："+pair.Key);
            }
            ODatum3(app,doc,assembly,housing,parts["cover"],"cover",records);
            var lowBearing=p.bearings.First(b=>b.name=="low_bearing");
            var highBearing=p.bearings.First(b=>b.name=="high_bearing");
            OConcentricAxial(app,doc,assembly,housing,parts["driven_shaft"],
                lowBearing.outer_diameter/2,lowBearing.inner_diameter/2,housing,"driven_shaft",records);
            OConcentricAxial(app,doc,assembly,housing,parts["drive_gear_shaft"],
                highBearing.outer_diameter/2,highBearing.inner_diameter/2,housing,"drive_gear_shaft",records);
            OConcentricAxial(app,doc,assembly,parts["driven_shaft"],parts["driven_gear"],
                p.gears.driven_bore/2,p.gears.driven_bore/2,parts["driven_shaft"],"driven_gear",records);
            ORecord(records,parts["driven_shaft"],parts["driven_gear"],"coincident",
                TPlaneMate(app,doc,assembly,parts["driven_shaft"],1,parts["driven_gear"],1,0,"driven_gear_key_angle"));
            foreach(var bearing in p.bearings)foreach(string side in new[]{"upper","lower"}) {
                var component=parts[bearing.name+"_"+side];
                OConcentricAxial(app,doc,assembly,housing,component,
                    bearing.outer_diameter/2,bearing.outer_diameter/2,housing,bearing.name+"_"+side,records);
            }
            OConcentricAxial(app,doc,assembly,parts["driven_shaft"],parts["sleeve"],
                lowBearing.inner_diameter/2,lowBearing.inner_diameter/2,parts["driven_shaft"],"sleeve",records);
            foreach(var ring in p.accessories.Where(r=>r.name!="sleeve")) {
                double bore=ring.name.StartsWith("low_")?lowBearing.outer_diameter/2:highBearing.outer_diameter/2;
                OConcentricAxial(app,doc,assembly,housing,parts[ring.name],
                    bore,ring.outer_diameter/2,housing,ring.name,records);
            }
            foreach(var cover in new[]{new{name="low_blank_cover",spec=p.blank_cover,bore=lowBearing.outer_diameter/2},
                                     new{name="high_blank_cover",spec=p.high_blank_cover,bore=highBearing.outer_diameter/2}}) {
                if(cover.spec==null||!parts.ContainsKey(cover.name))continue;
                OConcentricAxial(app,doc,assembly,housing,parts[cover.name],
                    cover.bore,cover.spec.step_diameter/2,housing,cover.name,records);
                ORecord(records,housing,parts[cover.name],"angle",
                    OAngle(app,doc,assembly,housing,parts[cover.name],cover.name+"_angle"));
            }
            foreach(var through in p.through_covers) {
                var component=parts[through.name];
                double bore=through.name.StartsWith("low_")?lowBearing.outer_diameter/2:highBearing.outer_diameter/2;
                double coverRadius=through.outer_steps.Select(step=>step[0]).OrderBy(d=>Math.Abs(d-2*bore)).First()/2;
                OConcentricAxial(app,doc,assembly,housing,component,bore,coverRadius,housing,through.name,records);
                ORecord(records,housing,component,"angle",
                    OAngle(app,doc,assembly,housing,component,through.name+"_angle"));
            }
            foreach(string name in new[]{"oil_retaining_ring_upper","oil_retaining_ring_lower"})
                if(parts.ContainsKey(name))ODatum3(app,doc,assembly,parts["drive_gear_shaft"],parts[name],name,records);
            foreach(var key in p.auxiliary.Where(a=>a.kind=="key")) {
                var shaft=parts[key.shaft_link=="high"?"drive_gear_shaft":"driven_shaft"];
                ODatum3(app,doc,assembly,shaft,parts[key.name],key.name,records);
            }
            if(parts.ContainsKey("inspection_cover"))
                ORecord(records,housing,parts["inspection_cover"],"lock",
                    OLock(app,doc,assembly,housing,parts["inspection_cover"],"inspection_cover_mount"));
            if(parts.ContainsKey("inspection_cover_gasket")) {
                var driver=parts["inspection_cover"];var gasket=parts["inspection_cover_gasket"];
                ORecord(records,driver,gasket,"coincident",TPlaneMate(app,doc,assembly,driver,1,gasket,1,0,"inspection_gasket_plane_y"));
                ORecord(records,driver,gasket,"coincident",TPlaneMate(app,doc,assembly,driver,2,gasket,2,0,"inspection_gasket_plane_x"));
                var a=(double[])driver.Transform2.ArrayData;var b=(double[])gasket.Transform2.ArrayData;
                double distance=Math.Sqrt(Math.Pow(a[9]-b[9],2)+Math.Pow(a[10]-b[10],2)+Math.Pow(a[11]-b[11],2))*1000;
                ORecord(records,driver,gasket,"distance",TPlaneMate(app,doc,assembly,driver,0,gasket,0,distance,"inspection_gasket_offset"));
            }
            foreach(string name in new[]{"vent_plug","oil_level_cover","oil_level_indicator"})
                if(parts.ContainsKey(name))ODatum3(app,doc,assembly,housing,parts[name],name,records);
            foreach(var through in p.through_covers) {
                string sealName=through.name.Replace("_through_cover","_oil_seal");
                var cover=parts[through.name];var seal=parts[sealName];
                OMateSeal(doc,assembly,cover,seal,records);
                sealMates.Add(new {cover=through.name,seal=sealName,coincident_plane_mates=3,seal_fixed=false,seal_constraint_status=seal.GetConstrainedStatus()});
            }
            ORecord(records,parts["drive_gear_shaft"],parts["driven_gear"],"gear",
                TGearMate(app,doc,assembly,parts["drive_gear_shaft"],parts["driven_gear"],
                    highBearing.inner_diameter/2,p.gears.driven_bore/2,
                    p.gears.pinion_teeth,p.gears.driven_teeth,"one_stage_gear_ratio"));
            if(p.hardware_version==1)MateHardware(app,doc,assembly,p,parts,records);
            doc.ClearSelection2(true);if(!doc.EditRebuild3())throw new Exception("一级配合总装重建失败。");
            ValidateAssemblyMates(doc);
            foreach(var pair in parts)if(pair.Key!="housing") {
                var mates=pair.Value.GetMates() as object[]??new object[0];
                if(pair.Value.IsFixed()||mates.Length==0)throw new Exception("一级组件仍为固定或缺少配合："+pair.Key);
            }
            return records;
        }
    }
}
