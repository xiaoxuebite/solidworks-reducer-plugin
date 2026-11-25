"""Check the offline two-stage design, CAD handoff and stale-input safeguards."""
from pathlib import Path
import sys
import tempfile

PLUGIN = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PLUGIN.parent))

from solidworks_plugin.design_bridge import handle_request
from solidworks_plugin.two_stage_design import calculate


def main():
    schema = handle_request({"action": "two_schema"})
    assert schema["ok"] and schema["schema"]["defaults"]["high_pinion_teeth"] == 29

    default = handle_request({"action": "two_calculate", "parameters": {}})["design"]
    assert default["system_checks_passed"]
    assert "motor" not in default and "motor_power" not in default["checks"]
    assert "motor_model" not in schema["schema"]["defaults"]
    assert len(default["shafts"]) == 3
    assert abs(default["shafts"]["output"]["power_kw"] / default["inputs"]["input_power_kw"] - default["efficiency"]) < 1e-12
    assert default["ratio"]["error_percent"] < 1
    assert default["high_stage"]["gear_checks_passed"]
    assert default["low_stage"]["gear_checks_passed"]
    assert default["couplings"]["input"]["model"] == "LT5"
    assert default["couplings"]["output"]["model"] == "LT6"
    assert default["checks"]["output_coupling"]["passed"]

    automatic = handle_request({"action": "two_auto", "parameters": {"input_speed_rpm": 1350, "input_power_kw": 1.2,
                                                                    "target_output_speed_rpm": 100}})["design"]
    assert automatic["system_checks_passed"]
    assert automatic["inputs"]["input_speed_rpm"] == 1350 and automatic["inputs"]["input_power_kw"] == 1.2
    assert automatic["inputs"]["high_wheel_teeth"] != default["inputs"]["high_wheel_teeth"]
    assert automatic["ratio"]["error_percent"] <= 5

    changed = calculate({"high_wheel_teeth": 112, "low_wheel_teeth": 115})
    assert changed["system_checks_passed"]
    assert changed["model_parameters"]["two_stage"]["high_pair"]["wheel_teeth"] == 112
    assert changed["model_parameters"]["two_stage"]["shafts"][0]["center_x"] != default["model_parameters"]["two_stage"]["shafts"][0]["center_x"]

    helical = calculate({"high_gear_type": "helical", "high_helix_angle": 15,
                         "low_gear_type": "helical", "low_helix_angle": -15})
    assert helical["system_checks_passed"]
    assert helical["high_stage"]["results"]["centerDistance"] > default["high_stage"]["results"]["centerDistance"]
    assert helical["model_parameters"]["two_stage"]["high_pair"]["helix_angle"] == 15
    assert helical["model_parameters"]["two_stage"]["low_pair"]["helix_angle"] == -15
    assert helical["model_parameters"]["two_stage"]["shafts"][0]["sections"][2]["diameter"] > default["model_parameters"]["two_stage"]["shafts"][0]["sections"][2]["diameter"]

    overloaded = calculate({"input_power_kw": 5})
    assert not overloaded["system_checks_passed"]
    assert not overloaded["checks"]["high_contact_strength"]["passed"]

    legacy = calculate({"working_power_kw": 1, "motor_model": "Y90L-4", "motor_power_kw": 1.5,
                        "motor_speed_rpm": 1400, "coupling_efficiency": 0.99, "machine_efficiency": 0.96})
    assert abs(legacy["inputs"]["input_power_kw"] - default["inputs"]["input_power_kw"]) < 0.001
    assert legacy["inputs"]["input_speed_rpm"] == 1400

    with tempfile.TemporaryDirectory(prefix="two-stage-design-") as folder:
        result = handle_request({"action": "two_report", "parameters": changed["inputs"],
                                 "project": {"name": "二级设计验证", "designer": "测试"}, "output_dir": folder})
        assert result["ok"] and Path(result["model_parameters_file"]).is_file()
        report = Path(result["report_file"]).read_text(encoding="utf-8")
        assert "高速级" in report and "低速级" in report and "三轴" in report
        assert "联轴器初选" in report and "润滑与箱体" in report
        assert "输出端" in report and "齿轮弯曲" in report
        assert "电机" not in report
        assert r"L_{10h}=\frac{10^6}" in report
        assert r"\sigma_H=Z_HZ_E" in report
        assert "L10h=10⁶/" not in report
        assert report.count("$$") % 2 == 0
        assert Path(result["project_file"]).is_file()
    print("Two-stage schema, auto/manual calculation, stress rejection, native v5 export and report: PASS")


if __name__ == "__main__":
    main()
