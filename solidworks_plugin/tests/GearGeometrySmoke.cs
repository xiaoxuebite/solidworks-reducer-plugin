using System;
using System.Linq;
using ReducerSW;
class GearGeometrySmoke {
    static double Area(double[][] p){double s=0;for(int i=0;i<p.Length;i++){var a=p[i];var b=p[(i+1)%p.Length];s+=a[0]*b[1]-a[1]*b[0];}return Math.Abs(s/2);}
    static GearTrainSpec Spec(){return new GearTrainSpec{module=2,pressure_angle=20,helix_angle=0,pinion_teeth=17,driven_teeth=42,driven_width=24,driven_bore=28,driven_key_width=6,driven_key_depth=3.5,driven_mesh_phase_deg=-360.0/42/4,high_sections=new[]{new GearShaftSection{kind="cylinder",diameter=12,length=37},new GearShaftSection{kind="cylinder",diameter=15,length=14},new GearShaftSection{kind="cylinder",diameter=14,length=2},new GearShaftSection{kind="cylinder",diameter=19,length=6},new GearShaftSection{kind="gear",diameter=29,length=32},new GearShaftSection{kind="cylinder",diameter=19,length=6},new GearShaftSection{kind="cylinder",diameter=14,length=2},new GearShaftSection{kind="cylinder",diameter=15,length=13}},high_keyway=new Keyway{segment_index=0,diameter=12,start=4,length=18,width=4,depth=2.5}};}
    static int Main(){try{
        var spec=Spec();Generator.ValidateGearTrain(spec);
        foreach(int teeth in new[]{17,42}) {
            var p=Generator.GearReferencePerimeter(2,teeth,20,400);var dense=Generator.GearReferencePerimeter(2,teeth,20,800);
            if(p.Any(a=>a.Any(v=>Double.IsInfinity(v)||Double.IsNaN(v))))throw new Exception("Non-finite profile.");
            var radius=p.Select(a=>Math.Sqrt(a[0]*a[0]+a[1]*a[1])).ToArray();
            if(Math.Abs(radius.Max()-(teeth+2))>1e-8||Math.Abs(radius.Min()-(teeth-2.5))>.0002)throw new Exception("Tip/root radii mismatch.");
            if(Math.Abs(p.Average(a=>a[0]))+Math.Abs(p.Average(a=>a[1]))>1e-9)throw new Exception("Tooth replication is not centered.");
            if(Math.Abs(Area(p)-Area(dense))/Area(dense)>1e-6)throw new Exception("Area integration failed to converge.");
            Console.WriteLine("z="+teeth+" area_mm2="+Area(dense).ToString("R",System.Globalization.CultureInfo.InvariantCulture));
        }
        spec.helix_angle=15;spec.high_sections[4].diameter=spec.module*(spec.pinion_teeth/Math.Cos(Math.PI/12)-2.5);Generator.ValidateGearTrain(spec);
        spec=Spec();spec.driven_bore=90;rejected=false;try{Generator.ValidateGearTrain(spec);}catch(ArgumentException){rejected=true;}if(!rejected)throw new Exception("Invalid bore accepted.");
        Console.WriteLine("Involute radii / rotational symmetry / converged area / normal-module helical checks: PASS");return 0;
    }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}}
}
