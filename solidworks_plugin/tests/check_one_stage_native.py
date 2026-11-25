"""Inspect a saved schema-v4 assembly and its read-only SolidWorks audit."""
import json
from collections import Counter
from pathlib import Path
import sys


folder = Path(sys.argv[1]).resolve()
parameters = json.loads((folder / "parameters.json").read_text(encoding="utf-8-sig"))
manifest = json.loads((folder / "parts_manifest.json").read_text(encoding="utf-8-sig"))
audit = json.loads((folder / "native_audit.json").read_text(encoding="utf-8-sig"))
assert parameters["schema_version"] == 4
assert audit["passed"] and not audit["issues"], audit["issues"]
hardware = parameters.get("hardware_version") == 1
expected_components = 55 if hardware else 28
assert manifest["component_count"] == len(manifest["instances"]) == expected_components
assert audit["assembly"]["component_count"] == audit["assembly"]["top_level_count"] == expected_components

mates = manifest["native_mates"]
expected_mates = {"angle": 4, "coincident": 27, "concentric": 14,
                  "distance": 24, "gear": 1, "lock": 1}
if hardware:
    expected_mates["concentric"] += 24
    expected_mates["distance"] += 30
    expected_mates["coincident"] += 3
assert Counter(mate["kind"] for mate in mates) == expected_mates
features = {feature["name"]: feature for feature in audit["assembly"]["features"]}
assert len({mate["feature"] for mate in mates}) == len(mates)
assert all(mate["feature"] in features and features[mate["feature"]]["error"] == 0 for mate in mates)
assert sum(feature["type"] == "MateConcentric" for feature in features.values()) >= 12

gear_mates = audit["assembly"]["gear_mates"]
assert len(gear_mates) == 1
gear = gear_mates[0]
assert gear["name"] == "TS_one_stage_gear_ratio"
assert gear.get("driver_file", gear["driver"].rsplit("-", 1)[0] + ".SLDPRT") == "drive_gear_shaft.SLDPRT"
assert gear.get("driven_file", gear["driven"].rsplit("-", 1)[0] + ".SLDPRT") == "driven_gear.SLDPRT"
assert gear["tooth_ratio_matches"] and gear["numerator"] > 0 and gear["denominator"] > 0
assert features[gear["name"]]["type"] == "MateGearDim"

components = audit["assembly"]["components"]
assert all(item["allowed"] or item["volume_mm3"] <= 0.1
           for item in audit["assembly"]["interferences"]), audit["assembly"]["interferences"]
grounded = [part for part in components if part["is_fixed"]]
assert len(grounded) == 1 and grounded[0]["file"] == "housing.SLDPRT", grounded
assert all(part["mate_count"] >= 1 for part in components if part["file"] != "housing.SLDPRT")
assert all(part["mate_count"] >= 3 for part in components if part["file"].endswith("_oil_seal.SLDPRT"))
assert len(manifest["top_level_mates"]) == 2
print(f"One-stage saved assembly: {expected_components} components, {len(mates)} native mates, one grounded housing: PASS")
