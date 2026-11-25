using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using ReducerSW;
class AssemblyPlanSmoke {
    static void Equal(double actual,double expected,string label){if(Math.Abs(actual-expected)>1e-8)throw new Exception(label+": "+actual+" != "+expected);}
    static int Main(string[] args) {
        try {
            var p=new JavaScriptSerializer().Deserialize<Parameters>(File.ReadAllText(args[0]));Generator.Validate(p);
            if(p.assembly_layout==null){Console.WriteLine("No v3 assembly datum supplied.");return 0;}
            var files=new List<string>{"driven_shaft.SLDPRT","low_blank_cover.SLDPRT"};
            if(p.high_blank_cover!=null)files.Add("high_blank_cover.SLDPRT");
            files.AddRange((p.accessories??new Ring[0]).Select(r=>r.name+".SLDPRT"));
            foreach(var c in p.through_covers??new ThroughCover[0]){files.Add(c.name+".SLDPRT");files.Add(c.name.Replace("_through_cover","_oil_seal")+".SLDPRT");}
            files.AddRange((p.rotary_parts??new RotaryPart[0]).Select(r=>r.name+".SLDPRT"));
            if(p.gears!=null)files.AddRange(new[]{"drive_gear_shaft.SLDPRT","driven_gear.SLDPRT"});
            files.AddRange((p.bearings??new BearingSpec[0]).Select(r=>r.name+".SLDPRT"));
            files.AddRange((p.auxiliary??new AuxiliarySpec[0]).Select(r=>r.name+".SLDPRT"));
            if(p.housing!=null)files.AddRange(new[]{"housing.SLDPRT","cover.SLDPRT"});
            if(p.hardware_version==1)files.AddRange(new[]{"gb_hex_bolt_m8x50.SLDPRT","gb_hex_bolt_m8x25.SLDPRT",
                "gb_hex_nut_m8.SLDPRT","gb_spring_washer_8.SLDPRT","gb_pan_screw_m3x12.SLDPRT",
                "gb_taper_pin_4x20.SLDPRT",
                "gb_oil_plug_m8x1.SLDPRT","gb_flat_washer_8.SLDPRT","gb_o_ring_19x1p8.SLDPRT"});
            var plan=Generator.PlanAssembly(p,files);var dat=p.assembly_layout;
            int extra=files.Count(f=>f=="oil_retaining_ring.SLDPRT"||f=="high_bearing.SLDPRT"||f=="low_bearing.SLDPRT");
            if(plan.Count!=files.Count+extra+(p.hardware_version==1?18:0))throw new Exception("Repeated shaft parts and hardware copy counts are wrong.");
            if(plan.Select(i=>i.name).Distinct().Count()!=plan.Count)throw new Exception("Duplicate component name.");
            if(plan.Any(i=>!i.file.EndsWith(".SLDPRT")))throw new Exception("Nested assembly in plan.");
            foreach(var i in plan){
                var aux=(p.auxiliary??new AuxiliarySpec[0]).FirstOrDefault(a=>a.name==i.name);
                if(aux!=null){
                    if(i.placement==null||i.placement.Length!=12)throw new Exception("Auxiliary transform missing: "+i.name);
                    bool oilLevel=p.hardware_version==1&&(i.name=="oil_level_cover"||i.name=="oil_level_indicator");
                    for(int j=0;j<(oilLevel?9:12);j++)Equal(i.placement[j],aux.placement[j],i.name+" placement "+j);
                    Equal(i.x,i.placement[9],i.name+" X");Equal(i.y,i.placement[10],i.name+" Y");Equal(i.z,i.placement[11],i.name+" Z");
                } else if(i.name=="housing"||i.name=="cover"){
                    double z=i.name=="cover"?p.housing.cover_offset_z:0;
                    double[] identity={1,0,0,0,1,0,0,0,1,0,0,z};
                    if(i.placement==null)throw new Exception("Housing transform missing.");
                    for(int j=0;j<12;j++)Equal(i.placement[j],identity[j],i.name+" identity "+j);
                    Equal(i.x,0,i.name+" X");Equal(i.y,0,i.name+" Y");Equal(i.z,z,i.name+" offset");
                } else if(p.hardware_version==1&&
                    (i.name.StartsWith("flange_")||i.name.StartsWith("inspection_screw_")||
                     i.name.StartsWith("taper_pin_")||
                     i.name.StartsWith("oil_drain_")||i.name=="oil_level_o_ring")) {
                    if(i.placement==null||i.placement.Length!=12)throw new Exception("Standard hardware transform missing: "+i.name);
                    Equal(i.x,i.placement[9],i.name+" X");Equal(i.y,i.placement[10],i.name+" Y");Equal(i.z,i.placement[11],i.name+" Z");
                } else {
                    Equal(i.z,dat.axis_height,"Axis height");
                    bool high=i.name.StartsWith("high_")||i.name.StartsWith("oil_retaining")||i.name=="drive_gear_shaft";
                    Equal(i.x,high?dat.high_center_x:dat.low_center_x,"Shaft center");
                }
            }
            var shaft=plan.First(i=>i.name=="driven_shaft");Equal(shaft.y-(p.shaft_segments[0].length+p.shaft_segments[1].length/2),dat.mesh_center_y,"Gear center");
            if(shaft.axis_y_direction!=-1)throw new Exception("Shaft axis reversed.");
            if(p.gears!=null){
                var high=plan.First(i=>i.name=="drive_gear_shaft");
                double before=0,width=0;foreach(var s in p.gears.high_sections){if(s.kind=="gear"){width=s.length;break;}before+=s.length;}
                Equal(high.y-before-width/2,dat.mesh_center_y,"Pinion centre");
                Equal(plan.First(i=>i.name=="driven_gear").y-p.gears.driven_width/2,dat.mesh_center_y,"Driven gear centre");
                if(high.axis_y_direction!=-1)throw new Exception("High shaft axis reversed.");
            }
            if(p.hardware_version==1) {
                var legacyPlan=Generator.PlanAssembly(p,files.Concat(new[]{"gb_jack_bolt_m5x10.SLDPRT"}));
                if(legacyPlan.Any(i=>i.file=="gb_jack_bolt_m5x10.SLDPRT")||legacyPlan.Count!=plan.Count)
                    throw new Exception("Legacy output reintroduced retired jacking bolts.");
                var seal=plan.Single(i=>i.name=="oil_level_o_ring");
                var oilCover=plan.Single(i=>i.name=="oil_level_cover");
                var indicator=plan.Single(i=>i.name=="oil_level_indicator");
                Equal(oilCover.x,-94.8,"Oil cover withdrawn to leave the entire seal thickness");
                Equal(seal.x,-86.8,"Seal starts at the oil cover shoulder");
                Equal(seal.x+1.8,-85,"Seal ends at the housing seat");
                Equal(indicator.x-oilCover.x,14,"Indicator still seats inside the cover");
                double split=p.housing.cover_offset_z;
                foreach(var pin in plan.Where(i=>i.name.StartsWith("taper_pin_"))) {
                    if(pin.file!="gb_taper_pin_4x20.SLDPRT")throw new Exception("Dowel must be 4x20.");
                    Equal(pin.z+20,split+6.25,"Dowel top above the six-millimetre lid flange");
                }
                foreach(var washer in plan.Where(i=>i.name.StartsWith("flange_spring_washer_"))) {
                    bool atEnd=Math.Abs(washer.y)<1e-8;
                    Equal(washer.z,split-(atEnd?6.05:18),"Washer against actual housing face");
                }
                var invalid=new JavaScriptSerializer().Deserialize<Parameters>(new JavaScriptSerializer().Serialize(p));
                var bridge=invalid.housing.lower.First(f=>f.name=="Side_Oil_Groove_-1_Top");
                double outer=bridge.curves.SelectMany(c=>new[]{c.start[1],c.end[1]}).Max();
                foreach(var curve in bridge.curves) {
                    if(Math.Abs(curve.start[1]-outer)<1e-8)curve.start[1]+=1;
                    if(Math.Abs(curve.end[1]-outer)<1e-8)curve.end[1]+=1;
                }
                bool rejected=false;try{Generator.ValidateHardwareInterfaces(invalid);}catch(ArgumentException){rejected=true;}
                if(!rejected)throw new Exception("Oil groove overlap with the flange hole was accepted.");
            }
            foreach(var c in p.through_covers??new ThroughCover[0]){var cover=plan.First(i=>i.name==c.name);var seal=plan.First(i=>i.name==c.name.Replace("_through_cover","_oil_seal"));Equal(seal.y,cover.y,"Seal/cover origin");if(seal.axis_y_direction!=cover.axis_y_direction)throw new Exception("Seal axis mismatch.");}
            foreach(var b in p.bearings??new BearingSpec[0]){
                var instances=plan.Where(i=>i.file==b.name+".SLDPRT").ToArray();
                if(instances.Length!=2||!instances.Any(i=>i.axis_y_direction==1)||!instances.Any(i=>i.axis_y_direction==-1))throw new Exception("Bearing pair must face opposite directions.");
                var upper=instances.First(i=>i.name.EndsWith("_upper"));var lower=instances.First(i=>i.name.EndsWith("_lower"));
                var through=p.through_covers.First(c=>c.name==(b.name=="high_bearing"?"high_through_cover":"low_through_cover"));
                if(b.name=="high_bearing"){
                    Equal(upper.y,dat.boss_depth/2+through.outer_steps[0][1]-through.outer_steps.Sum(s=>s[1]),"High upper bearing face");
                    Equal(lower.y,-dat.boss_depth/2+p.high_blank_cover.total_thickness+(p.accessories??new Ring[0]).Where(r=>r.name=="high_adjusting_ring").Sum(r=>r.thickness),"High lower bearing face");
                } else {
                    Equal(upper.y,dat.boss_depth/2-p.blank_cover.total_thickness-(p.accessories??new Ring[0]).Where(r=>r.name=="low_adjusting_ring").Sum(r=>r.thickness),"Low upper bearing face");
                    Equal(lower.y,-dat.boss_depth/2-through.outer_steps[0][1]+through.outer_steps.Sum(s=>s[1]),"Low lower bearing face");
                }
            }
            var retaining=(p.rotary_parts??new RotaryPart[0]).FirstOrDefault(i=>i.name=="oil_retaining_ring");
            if(retaining!=null){foreach(var point in retaining.section)point[0]+=1;
            var shifted=Generator.PlanAssembly(p,files);double edge=retaining.section.Max(v=>v[0]);
            var top=shifted.First(i=>i.name=="oil_retaining_ring_upper");var coverTop=shifted.First(i=>i.name=="high_through_cover");
            double bearingFace=coverTop.y-p.through_covers.First(c=>c.name=="high_through_cover").outer_steps.Sum(s=>s[1])-dat.high_bearing_width;
            Equal(top.y+edge,bearingFace,"Edited retaining ring bearing-side face");
            }
            Console.WriteLine("Flat assembly / axes / seals / bearings / auxiliary frames: PASS ("+files.Count+" parts, "+plan.Count+" instances)");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
