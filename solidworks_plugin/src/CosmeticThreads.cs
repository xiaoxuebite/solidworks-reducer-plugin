using System;
using System.Collections.Generic;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace ReducerSW {
    public static partial class Generator {
        static void ShowCosmeticThreads(ModelDoc2 doc) {
            var preferences=doc.Extension;
            int option=(int)swUserPreferenceOption_e.swDetailingNoOptionSpecified;
            preferences.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swDisplayAnnotations,option,true);
            preferences.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swDisplayCosmeticThreads,option,true);
            preferences.SetUserPreferenceToggle((int)swUserPreferenceToggle_e.swDisplayShadedCosmeticThreads,option,true);
        }
        // These simplified bores clear solid fasteners at their nominal diameter.
        // A cosmetic internal thread requires a major diameter larger than the bore.
        static void RecordNominalThread(ModelDoc2 doc,string location,string callout) {
            var properties=doc.Extension.CustomPropertyManager[""];
            int result=properties.Add3(location+"螺纹规格",(int)swCustomInfoType_e.swCustomInfoText,
                callout,(int)swCustomPropertyAddOption_e.swCustomPropertyReplaceValue);
            if(result!=0)throw new Exception("无法保存螺纹规格："+location+" "+callout+"，错误码 "+result);
        }
        // A circular edge, rather than the cylindrical face, is required by
        // InsertCosmeticThread2 in the installed SOLIDWORKS 2024 build.
        static void AddCosmeticThread(ModelDoc2 doc,double[] center,double[] axis,
            double cylinderRadius,double threadDiameter,double length,string callout,string name) {
            Edge selected=null;double best=double.MaxValue;
            var bodies=((PartDoc)doc).GetBodies2(0,false) as object[]??new object[0];
            foreach(object rawBody in bodies) {
                var body=rawBody as Body2;if(body==null)continue;
                foreach(object rawFace in body.GetFaces() as object[]??new object[0]) {
                    var face=rawFace as Face2;if(face==null)continue;
                    var surface=face.GetSurface() as Surface;
                    if(surface==null||!surface.IsCylinder())continue;
                    var cylinder=(double[])surface.CylinderParams;
                    if(Math.Abs(cylinder[6]-M(cylinderRadius))>M(.03))continue;
                    double dot=Math.Abs(cylinder[3]*axis[0]+cylinder[4]*axis[1]+cylinder[5]*axis[2]);
                    if(dot<.999)continue;
                    foreach(object rawEdge in face.GetEdges() as object[]??new object[0]) {
                        var edge=rawEdge as Edge;if(edge==null)continue;
                        var curve=edge.GetCurve() as Curve;if(curve==null||!curve.IsCircle())continue;
                        var circle=(double[])curve.CircleParams;
                        if(Math.Abs(circle[6]-M(cylinderRadius))>M(.03))continue;
                        double distance=Math.Sqrt(Math.Pow(circle[0]-M(center[0]),2)+
                            Math.Pow(circle[1]-M(center[1]),2)+Math.Pow(circle[2]-M(center[2]),2));
                        if(distance<best){best=distance;selected=edge;}
                    }
                }
            }
            if(selected==null||best>M(.35))
                throw new Exception("Cannot locate circular thread entry edge: "+name+" distance_mm="+(best*1000));
            doc.ClearSelection2(true);
            if(!((Entity)selected).Select4(false,null))throw new Exception("Cannot select thread edge: "+name);
            var feature=doc.FeatureManager.InsertCosmeticThread2(0,M(threadDiameter),M(length),callout);
            if(feature==null)throw new Exception("SOLIDWORKS cosmetic thread creation failed: "+name);
            ModelPresentation.NameCreated(feature,name);
            bool warning;int error=feature.GetErrorCode2(out warning);
            if(error!=0&&!warning)throw new Exception("Cosmetic thread rebuild failed: "+name+" code="+error);
            ShowCosmeticThreads(doc);
            doc.ClearSelection2(true);
        }

        static void RecordHousingThread(ModelDoc2 doc,string name) {
            if(name.StartsWith("Inspection_Screw_Hole_",StringComparison.Ordinal))
                RecordNominalThread(doc,"检查盖","M3×0.5");
            else if(name=="Right_M8_Major_Diameter")RecordNominalThread(doc,"放油孔","M8×1");
            else if(name=="Left_M20_Major_Diameter")RecordNominalThread(doc,"油位孔","M20×1.5");
            else if(name=="Vent_Through_Bore")RecordNominalThread(doc,"通气孔","M10×1.5");
            else if(name.EndsWith("_M6_EndCover_Taps",StringComparison.Ordinal))RecordNominalThread(doc,"端盖","M6×1");
            else if(name=="Inspection_M5_Taps")RecordNominalThread(doc,"检查盖","M5×0.8");
            else if(name=="Drain_M12_Tap")RecordNominalThread(doc,"放油孔","M12×1.75");
        }
    }
}
