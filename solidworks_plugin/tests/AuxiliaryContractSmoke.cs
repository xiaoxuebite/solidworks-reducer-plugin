using System;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using ReducerSW;

class AuxiliaryContractSmoke {
    static void Reject(AuxiliarySpec[] values,string label) {
        bool rejected=false;try{Generator.ValidateAuxiliary(values);}catch(ArgumentException){rejected=true;}
        if(!rejected)throw new Exception("Invalid auxiliary contract accepted: "+label);
    }
    static int Main(string[] args) {
        try {
            var values=new JavaScriptSerializer().Deserialize<AuxiliarySpec[]>(File.ReadAllText(args[0]));
            Generator.ValidateAuxiliary(values);
            if(values.Length!=8||values.Select(x=>x.name).Distinct().Count()!=8)throw new Exception("Expected 8 active auxiliary parts.");
            var key=values.First(x=>x.kind=="key");double old=key.start_x;key.start_x=Double.NaN;Reject(values,"non-finite key origin");key.start_x=old;
            old=key.placement[8];key.placement[8]=-old;Reject(values,"mirrored assembly basis");key.placement[8]=old;
            var plate=values.First(x=>x.kind=="plate");old=plate.hole_pitch_x;plate.hole_pitch_x=plate.length;Reject(values,"plate hole outside cover");plate.hole_pitch_x=old;
            var indicator=values.First(x=>x.kind=="indicator");old=indicator.scallop_radius;indicator.scallop_radius=.1;Reject(values,"scallops do not intersect front plate");indicator.scallop_radius=old;
            var cover=values.First(x=>x.kind=="oil_cover");old=cover.inner_arc_height;cover.inner_arc_height=old+.1;Reject(values,"discontinuous inner arc");cover.inner_arc_height=old;
            Generator.ValidateAuxiliary(values);
            Console.WriteLine("Auxiliary contracts / finite values / proper placement / feature guards: PASS (8 parts)");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
