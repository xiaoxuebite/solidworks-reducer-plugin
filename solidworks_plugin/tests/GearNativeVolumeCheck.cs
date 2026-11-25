using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using ReducerSW;

// Offline comparison: run with one generation-output directory after --audit-native.
// This never opens SolidWorks and never uses the manifest's default-accuracy volume.
class GearNativeVolumeCheck {
    const int ReferenceSamplesPerFlank=1600;
    const double AbsoluteToleranceMm3=.5;

    public sealed class AuditPart {
        public string file {get;set;}
        public double? volume_mm3_high_accuracy {get;set;}
        public int? mass_accuracy_level {get;set;}
        public string error {get;set;}
    }
    public sealed class AuditReport {public AuditPart[] parts {get;set;}}
    public sealed class SectionFace {
        public double x_mm {get;set;} public double volume_from_section_mm3 {get;set;}
        public double convergence_mm3 {get;set;}
    }
    public sealed class SectionReport {
        public bool passed {get;set;} public bool source_file_unchanged {get;set;}
        public double chord_tolerance_m {get;set;} public SectionFace[] faces {get;set;}
    }

    static double Area(double[][] points) {
        // Compensated shoelace sum limits cancellation in the dense perimeter.
        double sum=0,correction=0;
        for(int i=0;i<points.Length;i++) {
            var a=points[i];var b=points[(i+1)%points.Length];
            double term=a[0]*b[1]-a[1]*b[0]-correction;
            double next=sum+term;correction=(next-sum)-term;sum=next;
        }
        return Math.Abs(sum/2);
    }

    static double KeywayAreaOutsideBore(double boreRadius,double keyWidth,double keyDepth) {
        // Integrate r+depth-sqrt(r^2-x^2) from -width/2 to +width/2.
        // Only the rectangle outside the existing circular bore removes material.
        double half=keyWidth/2;
        if(half>=boreRadius)throw new ArgumentException("Keyway width must be smaller than the bore diameter.");
        double circleStrip=half*Math.Sqrt(boreRadius*boreRadius-half*half)
            +boreRadius*boreRadius*Math.Asin(half/boreRadius);
        return keyWidth*(boreRadius+keyDepth)-circleStrip;
    }

    static string Number(double value) {return value.ToString("R",CultureInfo.InvariantCulture);}

    static int Main(string[] args) {
        try {
            if(args.Length!=1)throw new ArgumentException("Usage: GearNativeVolumeCheck.exe <generation-output-directory>");
            string folder=Path.GetFullPath(args[0]);
            var json=new JavaScriptSerializer{MaxJsonLength=32*1024*1024};
            var parameters=json.Deserialize<Parameters>(File.ReadAllText(Path.Combine(folder,"parameters.json")));
            if(parameters==null||parameters.gears==null)throw new ArgumentException("Output parameters do not contain a gear train.");
            var gear=parameters.gears;Generator.ValidateGearTrain(gear);
            double perimeterArea=Area(Generator.GearReferencePerimeter(gear.module,gear.driven_teeth,
                gear.pressure_angle,ReferenceSamplesPerFlank));
            double boreRadius=gear.driven_bore/2,boreArea=Math.PI*boreRadius*boreRadius;
            double keywayArea=KeywayAreaOutsideBore(boreRadius,gear.driven_key_width,gear.driven_key_depth);
            double expected=(perimeterArea-boreArea-keywayArea)*gear.driven_width;
            if(expected<=0)throw new Exception("Independent reference volume is not positive.");
            Console.WriteLine("driven_gear reference_samples_per_flank="+ReferenceSamplesPerFlank
                +" perimeter_area_mm2="+Number(perimeterArea)+" bore_area_mm2="+Number(boreArea)
                +" keyway_area_outside_bore_mm2="+Number(keywayArea));
            string sectionPath=Path.Combine(folder,"gear_section_audit.json");
            if(File.Exists(sectionPath)) {
                // Native mass integration can cache lower-accuracy results. An independently
                // integrated actual boundary at both ends checks the geometry directly.
                var section=json.Deserialize<SectionReport>(File.ReadAllText(sectionPath));
                if(section==null||!section.passed||!section.source_file_unchanged||section.faces==null||section.faces.Length!=2
                    ||section.chord_tolerance_m<=0||section.chord_tolerance_m>1e-8)
                    throw new Exception("Native gear section audit is missing, failed, or insufficiently precise.");
                foreach(var face in section.faces) {
                    double volume=face.volume_from_section_mm3,sectionResidual=volume-expected;
                    Console.WriteLine("driven_gear end_x_mm="+Number(face.x_mm)+" expected_mm3="+Number(expected)
                        +" actual_boundary_volume_mm3="+Number(volume)+" residual_mm3="+Number(sectionResidual)
                        +" absolute_tolerance_mm3="+Number(AbsoluteToleranceMm3));
                    if(Double.IsNaN(volume)||Double.IsInfinity(volume)||Double.IsNaN(face.convergence_mm3)
                        ||Math.Abs(sectionResidual)>AbsoluteToleranceMm3||Math.Abs(face.convergence_mm3)>.1)
                        throw new Exception("Native driven-gear boundary differs by more than 0.5 mm3 or fails tessellation convergence.");
                }
                if(!section.faces.Any(f=>Math.Abs(f.x_mm)<.001)||!section.faces.Any(f=>Math.Abs(f.x_mm-gear.driven_width)<.001))
                    throw new Exception("Native gear section audit must include both gear ends.");
                Console.WriteLine("Native driven-gear independent boundary volume: PASS");return 0;
            }
            var audit=json.Deserialize<AuditReport>(File.ReadAllText(Path.Combine(folder,"native_audit.json")));
            var matches=(audit==null||audit.parts==null?new AuditPart[0]:audit.parts)
                .Where(p=>p!=null&&String.Equals(p.file,"driven_gear.SLDPRT",StringComparison.OrdinalIgnoreCase)).ToArray();
            if(matches.Length!=1)throw new Exception("Audit must contain exactly one driven_gear.SLDPRT result.");
            var part=matches[0];
            if(!String.IsNullOrEmpty(part.error))throw new Exception("Native gear audit failed: "+part.error);
            if(part.mass_accuracy_level!=2||!part.volume_mm3_high_accuracy.HasValue)
                throw new Exception("Gear volume must come from the level-2 high-accuracy native audit.");
            double actual=part.volume_mm3_high_accuracy.Value;
            if(Double.IsNaN(actual)||Double.IsInfinity(actual)||actual<=0)throw new Exception("Invalid audited native gear volume.");

            double residual=actual-expected;
            Console.WriteLine("driven_gear expected_mm3="+Number(expected)+" actual_high_accuracy_mm3="+Number(actual)
                +" residual_mm3="+Number(residual)+" absolute_tolerance_mm3="+Number(AbsoluteToleranceMm3));
            if(expected<=0||Math.Abs(residual)>AbsoluteToleranceMm3)
                throw new Exception("Native driven-gear volume differs from the independent cross-section reference by more than 0.5 mm3.");
            Console.WriteLine("Native driven-gear high-accuracy volume: PASS");return 0;
        } catch(Exception ex) {Console.Error.WriteLine(ex);return 1;}
    }
}
