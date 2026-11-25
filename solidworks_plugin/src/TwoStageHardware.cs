using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SolidWorks.Interop.sldworks;

namespace ReducerSW {
    public static partial class Generator {
        const double TEndFlangeProjection=20;
        const double TEndFlangeHeight=35;
        // GB reference assembly: 8 M8x80 + 2 M8x30, 10 nuts, 20 washers,
        // 28 M6x30 end-cover screws, 4 M5x10 inspection screws, two pins,
        // and one M12x25 drain screw with its seal. All stations follow the
        // parametric housing and bearing centers rather than source transforms.
        sealed class TFlangeSite { public string name; public double x,y; public bool longBolt; }
        static List<TFlangeSite> TFlangeSites(TwoStageSpec spec) {
            var h=spec.housing;double output=TShaft(spec,"output").center_x;
            double middle=TShaft(spec,"intermediate").center_x,input=TShaft(spec,"input").center_x;
            var xs=new[]{h.x_min+25,(h.x_min+output)/2,(output+middle)/2,(middle+input)/2};
            var sites=new List<TFlangeSite>();int index=0;
            foreach(double y in new[]{h.y_min-12,h.y_max+12})foreach(double x in xs)
                sites.Add(new TFlangeSite{name="flange_"+(++index),x=x,y=y,longBolt=true});
            sites.Add(new TFlangeSite{name="flange_9",x=h.x_min-13,y=(h.y_min+h.y_max)/2,longBolt=false});
            sites.Add(new TFlangeSite{name="flange_10",x=h.x_max+13,y=(h.y_min+h.y_max)/2,longBolt=false});
            return sites;
        }
        static double[][] TPinSites(TwoStageSpec spec) {
            var h=spec.housing;double mid=(h.y_min+h.y_max)/2;
            return new[]{new[]{h.x_min-13,mid-12},new[]{h.x_max+13,mid+12}};
        }
        static double[] TInspectionCenter(TwoStageSpec spec) {
            var h=spec.housing;
            return new[]{(TShaft(spec,"output").center_x+TShaft(spec,"intermediate").center_x)/2,
                (h.y_min+h.y_max)/2};
        }
        static List<double[]> TCoverHoles(TwoStageBearingSet bearing) {
            double r=bearing.outer_diameter/2+3.5;int n=bearing.name=="output"?6:4;
            var result=new List<double[]>();
            for(int i=0;i<n;i++) {
                double angle=2*Math.PI*(i+.5)/n;
                result.Add(new[]{r*Math.Cos(angle),r*Math.Sin(angle)});
            }
            return result;
        }
        static HouseFeatureSpec TCircles(string name,double[] origin,double[] u,double[] v,double depth,
            IEnumerable<double[]> centers,double radius) {
            return new HouseFeatureSpec{name=name,kind="cut",origin=origin,u=u,v=v,depth=depth,
                curves=centers.Select(c=>new HouseCurve{kind="circle",center=new[]{c[0],c[1]},radius=radius}).ToArray()};
        }
        static void TAppendHardwareHousingFeatures(TwoStageSpec spec,List<HouseFeatureSpec> lower,List<HouseFeatureSpec> upper) {
            var h=spec.housing;double mid=(h.y_min+h.y_max)/2;
            foreach(var pair in new[]{new{label="Front",lo=h.y_min-TEndFlangeProjection,hi=h.y_min+.1},
                                      new{label="Rear",lo=h.y_max-.1,hi=h.y_max+TEndFlangeProjection}}) {
                lower.Add(TBoxFeature(pair.label+"_Lower_Flange","boss",h.axis_height-TEndFlangeHeight,TEndFlangeHeight,h.x_min,pair.lo,h.x_max,pair.hi));
                upper.Add(TBoxFeature(pair.label+"_Upper_Flange","boss",0,TEndFlangeHeight,h.x_min,pair.lo,h.x_max,pair.hi));
            }
            foreach(bool front in new[]{true,false}) {
                double exterior=front?h.y_min:h.y_max;
                double depth=(front?-1:1)*(TEndFlangeProjection+.05);
                foreach(var bearing in spec.bearings) {
                    double x=TShaft(spec,bearing.name).center_x;
                    // Clear the cover and the M6 heads while retaining the bearing seat in the end wall.
                    double radius=bearing.outer_diameter/2+9.25;
                    // An exactly tangent sketch creates a zero-thickness edge in SOLIDWORKS.
                    if(Math.Abs(radius-TEndFlangeHeight)<.05)radius=TEndFlangeHeight+.05;
                    double start=exterior+depth;
                    string name=bearing.name+(front?"_Front":"_Rear")+"_EndCover_Relief";
                    lower.Add(TBoreFeature(name,start,depth,x,h.axis_height,radius));
                    upper.Add(TBoreFeature(name,start,depth,x,0,radius));
                }
            }
            lower.Add(TBoxFeature("Left_Lower_Lug","boss",h.axis_height-10,10,h.x_min-18,mid-20,h.x_min+.1,mid+20));
            lower.Add(TBoxFeature("Right_Lower_Lug","boss",h.axis_height-10,10,h.x_max-.1,mid-20,h.x_max+18,mid+20));
            upper.Add(TBoxFeature("Left_Upper_Lug","boss",0,10,h.x_min-18,mid-20,h.x_min+.1,mid+20));
            upper.Add(TBoxFeature("Right_Upper_Lug","boss",0,10,h.x_max-.1,mid-20,h.x_max+18,mid+20));
            foreach(bool longBolt in new[]{true,false}) {
                var points=TFlangeSites(spec).Where(s=>s.longBolt==longBolt).Select(s=>new[]{s.x,s.y}).ToArray();
                string label=longBolt?"Long":"Short";double thickness=longBolt?TEndFlangeHeight:10;
                lower.Add(TCircles(label+"_Lower_M8_Holes",new[]{0.0,0.0,h.axis_height},new[]{1.0,0,0},new[]{0.0,1,0},-thickness,points,4.5));
                upper.Add(TCircles(label+"_Upper_M8_Holes",new[]{0.0,0.0,thickness},new[]{1.0,0,0},new[]{0.0,1,0},-thickness,points,4.5));
            }
            var pins=TPinSites(spec);
            lower.Add(TCircles("Lower_Pin_Holes",new[]{0.0,0.0,h.axis_height},new[]{1.0,0,0},new[]{0.0,1,0},-10,pins,3.65));
            upper.Add(TCircles("Upper_Pin_Holes",new[]{0.0,0.0,10.0},new[]{1.0,0,0},new[]{0.0,1,0},-10,pins,3.65));
            foreach(bool front in new[]{true,false}) {
                double y=front?h.y_min:h.y_max,depth=front?-24:24;
                foreach(bool top in new[]{false,true}) {
                    var points=new List<double[]>();
                    foreach(var b in spec.bearings)foreach(var offset in TCoverHoles(b))
                        if((offset[1]>0)==top)points.Add(new[]{TShaft(spec,b.name).center_x+offset[0],top?offset[1]:h.axis_height+offset[1]});
                    // Upper housing is built around local Z=0 and lifted by axis_height.
                    (top?upper:lower).Add(TCircles((front?"Front":"Rear")+(top?"_Upper":"_Lower")+"_M6_EndCover_Taps",
                        new[]{0.0,y,0.0},new[]{1.0,0,0},new[]{0.0,0,1},depth,points,3.05));
                }
            }
            var c=TInspectionCenter(spec);
            upper.Add(TBoxFeature("Inspection_Aperture","cut",h.upper_height,-h.roof_thickness,
                c[0]-25,c[1]-20,c[0]+25,c[1]+20));
            upper.Add(TCircles("Inspection_M5_Taps",new[]{0.0,0.0,h.upper_height},new[]{1.0,0,0},new[]{0.0,1,0},-8,
                TInspectionHoles(spec),2.55));
            lower.Add(TBoreFeature("Drain_M12_Tap",h.y_min,-25,h.x_min+50,20,6));
        }
        static double[][] TInspectionHoles(TwoStageSpec spec) {
            var c=TInspectionCenter(spec);var result=new List<double[]>();
            foreach(double x in new[]{-32.0,32.0})foreach(double y in new[]{-27.0,27.0})result.Add(new[]{c[0]+x,c[1]+y});
            return result.ToArray();
        }
        static void TDrillEndCover(SldWorks app,TwoStageSpec spec,TwoStageBearingSet bearing,string name,string output,List<object> metrics) {
            GenerationControl.Checkpoint("正在生成："+ModelPresentation.ChineseName(name));
            string path=Path.Combine(output,name+".SLDPRT");int error=0,warning=0;
            var doc=(ModelDoc2)app.OpenDoc6(path,1,1,"",ref error,ref warning);
            if(doc==null||error!=0)throw new Exception("Cannot reopen two-stage end cover for screw holes: "+name+" error="+error);
            try {
                int activationError=0;
                if(app.ActivateDoc3(doc.GetTitle(),false,0,ref activationError)==null||activationError!=0)
                    throw new Exception("Cannot activate two-stage end cover: "+name);
                var holes=TCoverHoles(bearing);
                HFeature(app,doc,TCircles("M6_Clearance",new[]{0.0,0.0,0.0},new[]{0.0,1,0},new[]{0.0,0,1},spec.end_cover_thickness,holes,3.3),
                    new Dictionary<string,Feature>(),null);
                Save(doc,path);
                metrics[metrics.Count-1]=Metrics(doc,path);
            } finally {app.CloseDoc(doc.GetTitle());}
        }
        static string TMakeSocketScrew(SldWorks app,string output,List<object> metrics,string name,
            double radius,double length,double headRadius,double headHeight,double hexFlats) {
            var doc=NewDocument(app,1,name);
            RevolvePolygon(doc,new[]{new[]{0.0,0.0},new[]{0.0,radius},
                new[]{length-.4,radius},new[]{length,radius-.4},new[]{length,0.0}},"Nominal_Shank");
            var headSketch=AuxBeginPlane(doc,3);var sm=doc.SketchManager;
            sm.CreateCircleByRadius(0,0,0,M(headRadius));
            double normalX=((double[])headSketch.ModelToSketchTransform.ArrayData)[2];
            AuxExtrude(doc,headHeight,normalX>0?-headHeight:0,"Socket_Head");
            double r=hexFlats/Math.Sqrt(3);var points=new List<double[]>();
            for(int i=0;i<6;i++){double angle=Math.PI*(i+.5)/3;points.Add(new[]{r*Math.Cos(angle),r*Math.Sin(angle)});}
            var curves=Enumerable.Range(0,6).Select(i=>new HouseCurve{kind="line",start=points[i],end=points[(i+1)%6]}).ToArray();
            HFeature(app,doc,new HouseFeatureSpec{name="Hex_Socket",kind="cut",origin=new[]{-headHeight,0.0,0.0},
                u=new[]{0.0,1,0},v=new[]{0.0,0,1},depth=Math.Min(3,headHeight-.8),curves=curves},new Dictionary<string,Feature>(),null);
            double major=2*radius;
            string note=major==12?"M12×1.75":major==6?"M6×1":"M5×0.8";
            double minor=major-(major==12?1.75:major==6?1:.8);
            AddCosmeticThread(doc,new[]{length-.4,0.0,0.0},new[]{1.0,0,0},radius,
                minor,length-.4,note,"External_"+note.Replace('×','x'));
            return SaveWithMetrics(app,doc,Path.Combine(output,name+".SLDPRT"),metrics);
        }
        static string TMakeFlatWasher(SldWorks app,string output,List<object> metrics,string name,
            double inner,double outer,double thickness) {
            var doc=NewDocument(app,1,name);
            RevolvePolygon(doc,new[]{new[]{0.0,inner/2},new[]{0.0,outer/2},new[]{thickness,outer/2},new[]{thickness,inner/2}},"Flat_Washer");
            return SaveWithMetrics(app,doc,Path.Combine(output,name+".SLDPRT"),metrics);
        }
        static string TMakePin(SldWorks app,string output,List<object> metrics) {
            var doc=NewDocument(app,1,"two_locating_pin");
            RevolvePolygon(doc,new[]{new[]{0.0,0.0},new[]{0.0,3.55},new[]{20.0,3.6},new[]{20.0,0.0}},"Locating_Pin");
            return SaveWithMetrics(app,doc,Path.Combine(output,"two_locating_pin.SLDPRT"),metrics);
        }
        static string TMakeInspectionPart(SldWorks app,TwoStageSpec spec,string output,List<object> metrics,bool gasket) {
            double t=gasket?1.2:5;string name=gasket?"two_inspection_gasket":"two_inspection_plate";
            var features=new List<HouseFeatureSpec>{TBoxFeature("Plate","boss",0,t,-38,-33,38,33),
                TBoxFeature("Opening","cut",t,-t,-25,-20,25,20),
                TCircles("M5_Clearance",new[]{0.0,0.0,t},new[]{1.0,0,0},new[]{0.0,1,0},-t,
                    new[]{new[]{-32.0,-27.0},new[]{-32.0,27.0},new[]{32.0,-27.0},new[]{32.0,27.0}},2.75)};
            return HPart(app,features.ToArray(),name,output,metrics,new[]{-38.0,-33.0,0.0,38.0,33.0,t});
        }
        static List<string> TMakeHardware(SldWorks app,TwoStageSpec spec,string output,List<object> metrics) {
            return new List<string>{
                HMakeBolt(app,output,metrics,"two_hex_bolt_m8x80",4,80,13,5.3),
                HMakeBolt(app,output,metrics,"two_hex_bolt_m8x30",4,30,13,5.3),
                HMakeNut(app,output,metrics),
                TMakeFlatWasher(app,output,metrics,"two_flat_washer_8",9,16,1.3),
                TMakeSocketScrew(app,output,metrics,"two_socket_screw_m6x30",3,30,5,6,5),
                TMakeSocketScrew(app,output,metrics,"two_socket_screw_m5x10",2.5,10,4.2,5,4),
                TMakeSocketScrew(app,output,metrics,"two_socket_screw_m12x25",6,25,9,12,10),
                TMakeFlatWasher(app,output,metrics,"two_drain_seal_12",13,20.8,2.2),
                TMakePin(app,output,metrics),
                TMakeInspectionPart(app,spec,output,metrics,false),
                TMakeInspectionPart(app,spec,output,metrics,true)};
        }
        static List<AssemblyInstance> TPlanHardware(TwoStageSpec spec) {
            var h=spec.housing;var list=new List<AssemblyInstance>();
            var down=new[]{0.0,0.0,-1.0};var up=new[]{0.0,0.0,1.0};var yAxis=new[]{0.0,1.0,0.0};
            foreach(var site in TFlangeSites(spec)) {
                string label=site.name;double thickness=site.longBolt?TEndFlangeHeight:10;
                double top=h.axis_height+thickness,bottom=h.axis_height-thickness;
                list.Add(HInstance(label+"_bolt",site.longBolt?"two_hex_bolt_m8x80":"two_hex_bolt_m8x30",
                    new[]{site.x,site.y,top+1.3},down,yAxis));
                list.Add(HInstance(label+"_upper_washer","two_flat_washer_8",new[]{site.x,site.y,top+1.3},down,yAxis));
                list.Add(HInstance(label+"_lower_washer","two_flat_washer_8",new[]{site.x,site.y,bottom},down,yAxis));
                list.Add(HInstance(label+"_nut","gb_hex_nut_m8",new[]{site.x,site.y,bottom-1.3},down,yAxis));
            }
            foreach(var bearing in spec.bearings)foreach(bool front in new[]{true,false}) {
                string side=front?"front":"rear";int index=0;
                foreach(var offset in TCoverHoles(bearing)) {
                    var origin=new[]{TShaft(spec,bearing.name).center_x+offset[0],
                        front?h.y_min-spec.end_cover_thickness:h.y_max+spec.end_cover_thickness,
                        h.axis_height+offset[1]};
                    list.Add(HInstance(bearing.name+"_"+side+"_screw_"+(++index),"two_socket_screw_m6x30",
                        origin,front?yAxis:new[]{0.0,-1.0,0.0},up));
                }
            }
            var c=TInspectionCenter(spec);double gasketZ=h.axis_height+h.upper_height;
            list.Add(new AssemblyInstance{name="inspection_gasket",file="two_inspection_gasket.SLDPRT",x=c[0],y=c[1],z=gasketZ,
                placement=new[]{1.0,0,0,0,1,0,0,0,1,c[0],c[1],gasketZ}});
            list.Add(new AssemblyInstance{name="inspection_plate",file="two_inspection_plate.SLDPRT",x=c[0],y=c[1],z=gasketZ+1.2,
                placement=new[]{1.0,0,0,0,1,0,0,0,1,c[0],c[1],gasketZ+1.2}});
            int screw=0;foreach(var hole in TInspectionHoles(spec))
                list.Add(HInstance("inspection_screw_"+(++screw),"two_socket_screw_m5x10",
                    new[]{hole[0],hole[1],gasketZ+6.2},down,yAxis));
            int pin=0;foreach(var point in TPinSites(spec))
                list.Add(HInstance("locating_pin_"+(++pin),"two_locating_pin",
                    new[]{point[0],point[1],h.axis_height+10},down,yAxis));
            var inward=new[]{0.0,1.0,0.0};double drainY=h.y_min-2.2,drainX=h.x_min+50;
            list.Add(HInstance("drain_screw","two_socket_screw_m12x25",new[]{drainX,drainY,20.0},inward,up));
            list.Add(HInstance("drain_seal","two_drain_seal_12",new[]{drainX,drainY,20.0},inward,up));
            return list;
        }
        static void TMateAtHole(SldWorks app,ModelDoc2 doc,AssemblyDoc assembly,
            Component2 axisDriver,Component2 axialDriver,Component2 driven,
            double driverRadius,double drivenRadius,double[] axis,double[] point,int axialWorldAxis,
            string label,List<TwoStageMateRecord> records) {
            GenerationControl.Checkpoint("正在配合："+ModelPresentation.ChineseName(driven.GetPathName()));
            TRecord(records,axisDriver,driven,"concentric",HConcentric(app,doc,assembly,axisDriver,driven,
                driverRadius,drivenRadius,axis,point,label+"_axis"));
            var a=(double[])axialDriver.Transform2.ArrayData;var b=(double[])driven.Transform2.ArrayData;
            double distance=Math.Abs(b[9+axialWorldAxis]-a[9+axialWorldAxis])*1000;
            TRecord(records,axialDriver,driven,distance<1e-6?"coincident":"distance",
                TPlaneMate(app,doc,assembly,axialDriver,OPlaneForWorldAxis(axialDriver,axialWorldAxis),
                    driven,OPlaneForWorldAxis(driven,axialWorldAxis),distance,label+"_seat"));
        }
        static void TMateHardware(SldWorks app,ModelDoc2 doc,AssemblyDoc assembly,TwoStageSpec spec,
            Dictionary<string,Component2> parts,List<TwoStageMateRecord> records) {
            var housing=parts["housing"];var cover=parts["cover"];double z=spec.housing.axis_height;
            // The drain pair is checked before the repeated fasteners so a
            // failed seat is reported before processing the whole batch.
            var port=new[]{spec.housing.x_min+50,spec.housing.y_min,20.0};
            var drain=parts["drain_screw"];
            TMateAtHole(app,doc,assembly,housing,housing,drain,6,6,new[]{0.0,1.0,0.0},port,1,"drain_screw",records);
            TMateAtHole(app,doc,assembly,drain,drain,parts["drain_seal"],6,6.5,
                new[]{0.0,1.0,0.0},port,1,"drain_seal",records);
            foreach(var site in TFlangeSites(spec)) {
                var bolt=parts[site.name+"_bolt"];var upper=parts[site.name+"_upper_washer"];
                var lower=parts[site.name+"_lower_washer"];var nut=parts[site.name+"_nut"];
                var point=new[]{site.x,site.y,z};var axis=new[]{0.0,0.0,1.0};
                TMateAtHole(app,doc,assembly,cover,housing,bolt,4.5,4,axis,point,2,site.name+"_bolt",records);
                TMateAtHole(app,doc,assembly,bolt,bolt,upper,4,4.5,axis,point,2,site.name+"_upper_washer",records);
                TMateAtHole(app,doc,assembly,bolt,bolt,lower,4,4.5,axis,point,2,site.name+"_lower_washer",records);
                TMateAtHole(app,doc,assembly,bolt,bolt,nut,4,4,axis,point,2,site.name+"_nut",records);
            }
            foreach(var bearing in spec.bearings)foreach(bool front in new[]{true,false}) {
                string side=front?"front":"rear";var cap=parts[bearing.name+"_"+side+"_end_cover"];
                int index=0;foreach(var offset in TCoverHoles(bearing)) {
                    var screw=parts[bearing.name+"_"+side+"_screw_"+(++index)];
                    var point=new[]{TShaft(spec,bearing.name).center_x+offset[0],
                        front?spec.housing.y_min:spec.housing.y_max,z+offset[1]};
                    TMateAtHole(app,doc,assembly,cap,cap,screw,3.3,3,new[]{0.0,1.0,0.0},point,1,
                        bearing.name+"_"+side+"_screw_"+index,records);
                }
            }
            var gasket=parts["inspection_gasket"];var plate=parts["inspection_plate"];
            ODatum3(app,doc,assembly,cover,gasket,"inspection_gasket",records);
            ODatum3(app,doc,assembly,cover,plate,"inspection_plate",records);
            int n=0;foreach(var hole in TInspectionHoles(spec)) {
                var screw=parts["inspection_screw_"+(++n)];
                TMateAtHole(app,doc,assembly,plate,plate,screw,2.75,2.5,new[]{0.0,0.0,1.0},
                    new[]{hole[0],hole[1],z+spec.housing.upper_height},2,"inspection_screw_"+n,records);
            }
            for(int i=1;i<=2;i++)ODatum3(app,doc,assembly,housing,parts["locating_pin_"+i],"locating_pin_"+i,records);
        }
    }
}
