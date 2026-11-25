"""Independent saved-assembly gate for the schema-v5 mating workflow."""
import json
from collections import Counter
from pathlib import Path
import sys


folder = Path(sys.argv[1]).resolve()
parameters = json.loads((folder / "parameters.json").read_text(encoding="utf-8-sig"))
manifest = json.loads((folder / "parts_manifest.json").read_text(encoding="utf-8-sig"))
audit = json.loads((folder / "native_audit.json").read_text(encoding="utf-8-sig"))
assert parameters["schema_version"] == 5
assert manifest["schema_version"] == 5
assert audit["passed"] and not audit["issues"], audit["issues"]
hardware = parameters["two_stage"].get("hardware_version", 0) == 2
expected_components = 103 if hardware else 25
expected_parts = 33 if hardware else 22
assert len(manifest["instances"]) == manifest["component_count"] == expected_components
assert audit["assembly"]["top_level_count"] == expected_components
assert len(audit["parts"]) == expected_parts

mates = manifest["top_level_mates"]
if manifest.get("assembly_mates_added") is False:
    assert not mates and not audit["assembly"]["gear_mates"]
    assert len(audit["assembly"]["components"]) == expected_components
    assert all(part["is_fixed"] and part["mate_count"] == 0 for part in audit["assembly"]["components"])
    assert not any(f["type"].startswith("Mate") and f["type"] != "MateGroup" for f in audit["assembly"]["features"])
    print(f"Two-stage saved layout: {expected_parts} parts, {expected_components} fixed components, zero mates: PASS")
    sys.exit(0)
kinds = Counter(mate["kind"] for mate in mates)
if hardware:
    assert len(mates) in {219, 220} and kinds["gear"] == 2, kinds
else:
    assert kinds == {"concentric": 18, "distance": 19, "coincident": 20, "gear": 2}, kinds
features = {feature["name"]: feature for feature in audit["assembly"]["features"]}
assert len({mate["feature"] for mate in mates}) == len(mates)
assert all(mate["feature"] in features and features[mate["feature"]]["error"] == 0 for mate in mates)
assert {mate["feature"] for mate in mates if mate["kind"] == "gear"} == {
    "TS_high_stage_gear_ratio", "TS_low_stage_gear_ratio"}
gear_mates = {mate["name"]: mate for mate in audit["assembly"]["gear_mates"]}
assert set(gear_mates) == {"TS_high_stage_gear_ratio", "TS_low_stage_gear_ratio"}
for name, driver, driven in [
    ("TS_high_stage_gear_ratio", "input_shaft-1", "high_wheel-1"),
    ("TS_low_stage_gear_ratio", "low_pinion-1", "low_wheel-1"),
]:
    gear = gear_mates[name]
    assert gear.get("driver_file", gear["driver"].rsplit("-", 1)[0] + ".SLDPRT") == driver.rsplit("-", 1)[0] + ".SLDPRT"
    assert gear.get("driven_file", gear["driven"].rsplit("-", 1)[0] + ".SLDPRT") == driven.rsplit("-", 1)[0] + ".SLDPRT"
    assert gear["tooth_ratio_matches"] and gear["numerator"] > 0 and gear["denominator"] > 0
    assert features[name]["type"] == "MateGearDim"

components = audit["assembly"]["components"]
grounded = [part for part in components if part["is_fixed"]]
assert len(grounded) == 1 and grounded[0]["file"] == "housing.SLDPRT", grounded
assert all(part["mate_count"] >= 2 for part in components if part["file"] != "housing.SLDPRT")
print(f"Two-stage saved assembly: {expected_parts} parts, {expected_components} components, "
      f"{len(mates)} native mates, one grounded housing: PASS")
