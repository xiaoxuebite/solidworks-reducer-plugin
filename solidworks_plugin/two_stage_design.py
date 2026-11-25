"""Offline two-stage spur/helical reducer design and schema-v5 model handoff.

The workflow follows the two-stage project reference: input duty -> ratio
split -> two gear checks -> three shafts/bearings/keys -> report. Gear contact
and bending calculations use the shared design engine.
"""
from __future__ import annotations

import copy
from datetime import datetime
import json
import math
from pathlib import Path
import subprocess
import tempfile

from design_service import STANDARD_MODULES, calculate_design, catalog_material_kind
from model_generator.shaft_params import BEARING_CATALOG


PLUGIN = Path(__file__).resolve().parent
TEMPLATE = PLUGIN / "parameters_two_stage.json"

# Preliminary LT choices from the locally supplied Reducer-Designer reference.
# The bore is a catalogue upper bound; a production coupling still needs its
# actual bore, key, hub and shaft-end dimensions confirmed.
COUPLINGS = (
    ("LT5", 125, 4600, 35), ("LT6", 250, 3800, 42),
    ("LT7", 500, 3600, 48), ("LT8", 710, 3000, 65),
    ("LT9", 1000, 2850, 70), ("LT10", 2000, 2300, 95),
    ("LT11", 4000, 1800, 110), ("LT12", 8000, 1450, 125),
    ("LT13", 16000, 1150, 170),
)

DEFAULTS = {
    "input_power_kw": 1.1178, "input_speed_rpm": 1400.0, "target_output_speed_rpm": 119.0,
    "bearing_efficiency": 0.99, "gear_efficiency": 0.98,
    "ratio_distribution_factor": 1.31, "speed_tolerance": 0.05,
    "life_years": 8.0, "days_per_year": 300.0, "hours_per_day": 8.0,
    "application_factor": 1.0, "shaft_allow_mpa": 60.0,
    "key_allow_mpa": 100.0,
    "high_gear_material": "", "low_gear_material": "", "shaft_material": "", "key_material": "",
    "high_pinion_teeth": 29, "high_wheel_teeth": 114,
    "high_module": 1.25, "high_pinion_width": 40.0, "high_wheel_width": 36.25,
    "high_gear_type": "spur", "high_helix_angle": 0.0,
    "low_pinion_teeth": 38, "low_wheel_teeth": 114,
    "low_module": 1.5, "low_pinion_width": 62.0, "low_wheel_width": 57.0,
    "low_gear_type": "spur", "low_helix_angle": 0.0,
    "high_contact_allow": 600.0, "high_bend_allow": 180.0,
    "low_contact_allow": 600.0, "low_bend_allow": 180.0,
    "high_kv": 1.2, "high_kha": 1.0, "high_kfa": 1.0,
    "high_khb": 1.0, "high_kfb": 1.0,
    "low_kv": 1.2, "low_kha": 1.0, "low_kfa": 1.0,
    "low_khb": 1.0, "low_kfb": 1.0,
}
INTEGER_FIELDS = {"high_pinion_teeth", "high_wheel_teeth", "low_pinion_teeth", "low_wheel_teeth"}


def _inputs(raw):
    if not isinstance(raw, dict):
        raise ValueError("设计参数必须是对象。")
    raw = dict(raw)
    # Migrate saved projects from the former motor-based workflow. Its input
    # shaft power was Pw / total_efficiency * coupling_efficiency * bearing_efficiency.
    if "input_power_kw" not in raw and "working_power_kw" in raw:
        bearing = float(raw.get("bearing_efficiency", DEFAULTS["bearing_efficiency"]))
        gear = float(raw.get("gear_efficiency", DEFAULTS["gear_efficiency"]))
        machine = float(raw.get("machine_efficiency", 0.96))
        if bearing <= 0 or gear <= 0 or machine <= 0:
            raise ValueError("旧项目效率必须为正数。")
        raw["input_power_kw"] = float(raw["working_power_kw"]) / (bearing ** 3 * gear ** 2 * machine)
    if "input_speed_rpm" not in raw and "motor_speed_rpm" in raw:
        raw["input_speed_rpm"] = raw["motor_speed_rpm"]
    for key in ("working_power_kw", "motor_model", "motor_power_kw", "motor_speed_rpm",
                "coupling_efficiency", "machine_efficiency"):
        raw.pop(key, None)
    unknown = set(raw) - set(DEFAULTS)
    if unknown:
        raise ValueError("未知设计参数：" + ", ".join(sorted(unknown)))
    values = dict(DEFAULTS)
    values.update(raw)
    for key, value in list(values.items()):
        if key.endswith("_material"):
            if not isinstance(value, str) or len(value) > 88:
                raise ValueError(key + " 必须为有效材料名称。")
            if value and key.endswith("_gear_material") and not catalog_material_kind(value):
                raise ValueError(key + " 须包含齿轮材料类别，请从材料参数表选用。")
            continue
        if key.endswith("_gear_type"):
            if value not in ("spur", "helical"):
                raise ValueError(key + " 只能为 spur 或 helical。")
            continue
        if isinstance(value, bool):
            raise ValueError(key + " 必须是数值。")
        try:
            number = float(value)
        except (ValueError, TypeError, OverflowError):
            raise ValueError(key + " 必须是数值。") from None
        if not math.isfinite(number) or (number <= 0 and not key.endswith("_helix_angle")):
            raise ValueError(key + " 必须为正的有限数。")
        if key in INTEGER_FIELDS:
            if not number.is_integer() or not 17 <= number <= 500:
                raise ValueError(key + " 必须是 17–500 的整数。")
            values[key] = int(number)
        else:
            values[key] = number
    for key in ("bearing_efficiency", "gear_efficiency"):
        if values[key] > 1:
            raise ValueError(key + " 必须在 0–1 之间。")
    if values["speed_tolerance"] > 0.2:
        raise ValueError("输出转速允许偏差不得超过 20%。")
    for prefix in ("high", "low"):
        if values[prefix + "_module"] not in STANDARD_MODULES:
            stage = "高速级" if prefix == "high" else "低速级"
            raise ValueError(stage + "法面模数必须从标准模数系列中取值。")
        beta = values[prefix + "_helix_angle"]
        if values[prefix + "_gear_type"] == "spur":
            if beta != 0:
                raise ValueError(prefix + " 直齿副的螺旋角必须为 0°。")
        elif not 8 <= abs(beta) <= 35:
            raise ValueError(prefix + " 斜齿副的螺旋角绝对值需在 8°–35°。")
        for suffix in ("kv", "kha", "kfa", "khb", "kfb"):
            key = prefix + "_" + suffix
            if values[key] < 1 or (suffix == "kv" and values[key] > 1.8):
                raise ValueError(key + " 需按参考表或图人工核定，且不得小于 1。")
    return values


def _auto(values):
    speed = values["target_output_speed_rpm"]
    total = values["input_speed_rpm"] / speed
    high = math.sqrt(total * values["ratio_distribution_factor"])
    low = total / high
    values["high_wheel_teeth"] = round(values["high_pinion_teeth"] * high)
    values["low_wheel_teeth"] = round(values["low_pinion_teeth"] * low)
    if not all(17 <= values[p + "_wheel_teeth"] <= 500 for p in ("high", "low")):
        raise ValueError("自动分配的齿数超出原生模型范围；请调整目标转速或小齿轮齿数。")


def _stage(values, prefix, power, speed):
    parameters = {
        "P": power, "n1": speed,
        "eta": values["gear_efficiency"] * values["bearing_efficiency"],
        "applicationFactor": values["application_factor"],
        "z1": values[prefix + "_pinion_teeth"], "z2": values[prefix + "_wheel_teeth"],
        "moduleM": values[prefix + "_module"], "pressureAngle": 20,
        "pinionFaceWidth": values[prefix + "_pinion_width"],
        "gearFaceWidth": values[prefix + "_wheel_width"],
        "contactAllow": values[prefix + "_contact_allow"],
        "gearContactAllow": values[prefix + "_contact_allow"],
        "pinionBendAllow": values[prefix + "_bend_allow"],
        "gearBendAllow": values[prefix + "_bend_allow"],
        "dynamicFactorManual": values[prefix + "_kv"],
        "toothFactorH": values[prefix + "_kha"],
        "toothFactorF": values[prefix + "_kfa"],
        "contactFaceFactor": values[prefix + "_khb"],
        "bendingFaceFactor": values[prefix + "_kfb"],
        "lifeYears": values["life_years"], "daysPerYear": values["days_per_year"],
        "hoursPerDay": values["hours_per_day"], "gearType": values[prefix + "_gear_type"],
        "helixAngle": abs(values[prefix + "_helix_angle"]),
    }
    if values[prefix + "_gear_material"]:
        parameters["pinionGearMaterial"] = parameters["gearMaterial"] = values[prefix + "_gear_material"]
    return calculate_design(parameters, auto_select_module=False)


def model_parameters(values):
    """Apply supported gear geometry changes to the assembled reference layout."""
    model = json.loads(TEMPLATE.read_text(encoding="utf-8-sig"))
    spec = model["two_stage"]
    high, low = spec["high_pair"], spec["low_pair"]
    for name, pair in (("high", high), ("low", low)):
        for source, destination in (("module", "module"), ("pinion_teeth", "pinion_teeth"),
                                    ("wheel_teeth", "wheel_teeth"), ("pinion_width", "pinion_width"),
                                    ("wheel_width", "wheel_width")):
            pair[destination] = values[name + "_" + source]
        pair["helix_angle"] = values[name + "_helix_angle"]
        pair["wheel_phase_deg"] = 180 / pair["wheel_teeth"]
    shafts = {shaft["name"]: shaft for shaft in spec["shafts"]}
    low_distance = low["module"] * (low["pinion_teeth"] + low["wheel_teeth"]) / (2 * math.cos(math.radians(low["helix_angle"])))
    high_distance = high["module"] * (high["pinion_teeth"] + high["wheel_teeth"]) / (2 * math.cos(math.radians(high["helix_angle"])))
    shafts["intermediate"]["center_x"] = low_distance
    shafts["input"]["center_x"] = low_distance + high_distance
    input_shaft = shafts["input"]
    delta = high["pinion_width"] - 40.0
    input_shaft["sections"][1]["length"] -= delta / 2
    input_shaft["sections"][3]["length"] -= delta / 2
    input_shaft["sections"][2].update(
        length=high["pinion_width"], diameter=high["module"] * (high["pinion_teeth"] / math.cos(math.radians(high["helix_angle"])) - 2.5))
    high["pinion_start_y"] = (input_shaft["origin_y"] - sum(s["length"] for s in input_shaft["sections"][:3]))
    for pair, base_pinion_start, base_pinion_width, base_wheel_start, base_wheel_width in (
        (high, None, None, 122.97, 36.25),
        (low, 46.47, 62.0, 47.97, 57.0),
    ):
        if base_pinion_start is not None:
            pair["pinion_start_y"] = base_pinion_start + (base_pinion_width - pair["pinion_width"]) / 2
        pair["wheel_start_y"] = base_wheel_start + (base_wheel_width - pair["wheel_width"]) / 2
    housing = spec["housing"]
    gears = ((shafts["input"], high, "pinion_teeth"), (shafts["intermediate"], high, "wheel_teeth"),
             (shafts["intermediate"], low, "pinion_teeth"), (shafts["output"], low, "wheel_teeth"))
    extents = [(shaft["center_x"], (pair["module"] * pair[teeth] / math.cos(math.radians(pair["helix_angle"])) + 2 * pair["module"]) / 2)
               for shaft, pair, teeth in gears]
    housing["x_min"] = min(-117.0, min(x - r - 30 for x, r in extents))
    housing["x_max"] = max(shafts["input"]["center_x"] + 48.625,
                            max(x + r + 30 for x, r in extents))
    largest = max(radius for _, radius in extents)
    housing["axis_height"] = housing["upper_height"] = max(105.0, largest + 18)
    model["migration_notes"] = [
        "二级设计以输入功率和转速为起点，分别计算两级齿轮的齿面接触应力和齿根弯曲应力，并校核三根轴及轴承。",
        "此模型沿用参考件轴颈、轴承、键和轴向装配链；改变这些尺寸需重新进行设计校核。",
    ]
    return model


def _native_validation(model):
    latest = PLUGIN / "latest-build.txt"
    if not latest.is_file():
        return False, "未找到插件构建，请先运行 build.ps1。"
    launcher = Path(latest.read_text(encoding="utf-8-sig").strip()) / "ReducerLauncher.exe"
    if not launcher.is_file():
        return False, "未找到当前构建的 ReducerLauncher.exe。"
    with tempfile.TemporaryDirectory(prefix="two-stage-check-") as folder:
        path = Path(folder) / "parameters.json"
        path.write_text(json.dumps(model, ensure_ascii=False), encoding="utf-8")
        result = subprocess.run([str(launcher), "--validate", str(path)], capture_output=True, timeout=45)
    if result.returncode:
        message = result.stderr or result.stdout
        try:
            decoded = message.decode("utf-8")
        except UnicodeDecodeError:
            decoded = message.decode("gbk", errors="replace")
        detail = decoded.strip().splitlines()
        return False, detail[0] if detail else "原生模型几何校验失败。"
    return True, "原生装配参数校验通过。"


def _bearing_and_shaft_checks(model, powers, speeds, high, low, values):
    spec = model["two_stage"]
    bearings = {bearing["name"]: bearing for bearing in spec["bearings"]}
    shafts = {shaft["name"]: shaft for shaft in spec["shafts"]}
    ft_high = high["results"]["tangentialForce"]
    ft_low = low["results"]["tangentialForce"]
    def load_factor(prefix):
        beta = math.radians(values[prefix + "_helix_angle"])
        radial = math.tan(math.radians(20)) / math.cos(beta)
        axial = math.tan(beta)
        return math.sqrt(1 + radial * radial + axial * axial)
    high_factor, low_factor = load_factor("high"), load_factor("low")
    forces = {
        "input": [(model["two_stage"]["high_pair"]["pinion_start_y"] + values["high_pinion_width"] / 2, ft_high * high_factor)],
        "intermediate": [
            (model["two_stage"]["high_pair"]["wheel_start_y"] + values["high_wheel_width"] / 2, ft_high * high_factor),
            (model["two_stage"]["low_pair"]["pinion_start_y"] + values["low_pinion_width"] / 2, ft_low * low_factor),
        ],
        "output": [(model["two_stage"]["low_pair"]["wheel_start_y"] + values["low_wheel_width"] / 2, ft_low * low_factor)],
    }
    gear_diameters = {
        "input": model["two_stage"]["high_pair"]["module"] * (values["high_pinion_teeth"] / math.cos(math.radians(values["high_helix_angle"])) - 2.5),
        "intermediate": min(model["two_stage"]["high_pair"]["wheel_bore"], model["two_stage"]["low_pair"]["pinion_bore"]),
        "output": model["two_stage"]["low_pair"]["wheel_bore"],
    }
    torque = {name: 9550000 * powers[name] / speeds[name] for name in speeds}
    checks, summaries = {}, {}
    life_required = values["life_years"] * values["days_per_year"] * values["hours_per_day"]
    for name in ("input", "intermediate", "output"):
        bearing = bearings[name]
        front, rear = bearing["front_y"] + bearing["width"] / 2, bearing["rear_y"] + bearing["width"] / 2
        span = rear - front
        loads = forces[name]
        if span <= 0 or any(not front < location < rear for location, _ in loads):
            checks[name + "_bearing_geometry"] = {"value": "齿轮不在两轴承之间", "limit": "轴承跨距内", "passed": False}
            continue
        reactions = (sum(force * (rear - y) / span for y, force in loads),
                     sum(force * (y - front) / span for y, force in loads))
        equivalent = max(reactions)
        catalog = BEARING_CATALOG.get(bearing["catalog_name"])
        if not catalog:
            checks[name + "_bearing_catalog"] = {"value": bearing["catalog_name"], "limit": "目录型号", "passed": False}
            continue
        life = 1e6 / (60 * speeds[name]) * (catalog["dynamic_load"] * 1000 / equivalent) ** 3
        checks[name + "_bearing_life"] = {"value": life, "limit": life_required, "passed": life >= life_required,
                                            "note": "L10h=10^6/(60n)*(C/P)^3；保守叠加两级径向载荷反力。"}
        checks[name + "_bearing_speed"] = {"value": speeds[name], "limit": catalog["oil_speed"],
                                             "passed": speeds[name] <= catalog["oil_speed"]}
        moments = []
        for station, _ in loads:
            moments.append(abs(reactions[0] * (station - front)
                               - sum(force * max(station - y, 0) for y, force in loads)))
        moment = max(moments)
        diameter = gear_diameters[name]
        stress = math.hypot(values["application_factor"] * moment, 0.6 * torque[name]) / (0.1 * diameter ** 3)
        minimum = 118 * (powers[name] / speeds[name]) ** (1 / 3)
        checks[name + "_shaft_stress"] = {"value": stress, "limit": values["shaft_allow_mpa"],
                                           "passed": stress <= values["shaft_allow_mpa"],
                                           "note": "σeq=sqrt((KA*M)^2+(0.6*T)^2)/(0.1*d^3)，简支梁初算。"}
        checks[name + "_shaft_diameter"] = {"value": diameter, "limit": minimum,
                                             "passed": diameter >= minimum,
                                             "note": "dmin=A0*(P/n)^(1/3)，A0=118。"}
        for index, key in enumerate(shafts[name]["keyways"]):
            working = 2 * torque[name] / (key["diameter"] * (key["length"] - key["width"]) * key["depth"])
            checks[name + "_key_" + str(index + 1)] = {"value": working, "limit": values["key_allow_mpa"],
                                                        "passed": working <= values["key_allow_mpa"],
                                                        "note": "p≈2T/(d*(L-b)*t)，按键槽切深作有效接触高度的初算。"}
        summaries[name] = {"power_kw": powers[name], "speed_rpm": speeds[name], "torque_nmm": torque[name],
                           "bearing": bearing["catalog_name"], "bearing_reactions_n": reactions,
                           "bearing_life_h": life, "shaft_stress_mpa": stress, "shaft_diameter_mm": diameter,
                           "shaft_min_diameter_mm": minimum}
    return checks, summaries


def _coupling_selections(model, shafts, values):
    selections, checks = {}, {}
    model_shafts = {shaft["name"]: shaft for shaft in model["two_stage"]["shafts"]}
    for name in ("input", "output"):
        if name not in shafts:
            checks[name + "_coupling"] = {"value": "轴承跨距或载荷无效", "limit": "先完成轴系计算", "passed": False}
            continue
        shaft = shafts[name]
        diameter = model_shafts[name]["sections"][0]["diameter"]
        service_factor = max(1.3, values["application_factor"])
        required = service_factor * shaft["torque_nmm"] / 1000
        match = next((row for row in COUPLINGS
                      if row[1] >= required and row[2] >= shaft["speed_rpm"] and row[3] >= diameter), None)
        chosen = match or COUPLINGS[-1]
        model_name, nominal, max_speed, max_bore = chosen
        selections[name] = {"model": model_name, "shaft_end_diameter_mm": diameter,
                            "service_factor": service_factor, "calculated_torque_nm": required,
                            "nominal_torque_nm": nominal, "speed_rpm": shaft["speed_rpm"],
                            "max_speed_rpm": max_speed, "max_bore_mm": max_bore,
                            "passed": match is not None}
        checks[name + "_coupling"] = {"value": f"{model_name}；Tc={required:.2f} N·m；轴端={diameter:g} mm",
                                       "limit": f"Tn={nominal:g} N·m；n≤{max_speed:g} r/min；最大孔径={max_bore:g} mm",
                                       "passed": match is not None}
    return selections, checks


def _lubrication_suggestions(values, high, low):
    suggestions = {}
    for name, stage in (("high", high), ("low", low)):
        velocity = stage["results"]["pitchLineVelocity"]
        tooth_height = 2.25 * values[name + "_module"]
        suggestions[name] = {"pitch_speed_mps": velocity, "tooth_height_mm": tooth_height,
                             "immersion_depth_mm": max(10.0, tooth_height),
                             "gear_lubrication": "浸油润滑" if velocity < 12 else "喷油润滑",
                             "bearing_lubrication": "锂基润滑脂，规格待核定"}
    return suggestions


def calculate(raw, *, automatic=False):
    values = _inputs(raw)
    if automatic:
        _auto(values)
    stage_efficiency = values["gear_efficiency"] * values["bearing_efficiency"]
    efficiency = stage_efficiency ** 2
    high_ratio = values["high_wheel_teeth"] / values["high_pinion_teeth"]
    low_ratio = values["low_wheel_teeth"] / values["low_pinion_teeth"]
    speeds = {"input": values["input_speed_rpm"],
              "intermediate": values["input_speed_rpm"] / high_ratio,
              "output": values["input_speed_rpm"] / (high_ratio * low_ratio)}
    powers = {"input": values["input_power_kw"]}
    powers["intermediate"] = powers["input"] * stage_efficiency
    powers["output"] = powers["intermediate"] * stage_efficiency
    stage_high = _stage(values, "high", powers["input"], speeds["input"])
    stage_low = _stage(values, "low", powers["intermediate"], speeds["intermediate"])
    model = model_parameters(values)
    shaft_checks, shafts = _bearing_and_shaft_checks(model, powers, speeds, stage_high, stage_low, values)
    couplings, coupling_checks = _coupling_selections(model, shafts, values)
    lubrication = _lubrication_suggestions(values, stage_high, stage_low)
    speed_error = abs(speeds["output"] / values["target_output_speed_rpm"] - 1)
    checks = {
        "output_speed": {"value": speed_error * 100, "limit": values["speed_tolerance"] * 100,
                         "passed": speed_error <= values["speed_tolerance"], "unit": "%"},
    }
    for prefix, design in (("high", stage_high), ("low", stage_low)):
        for key, check in design["checks"].items():
            checks[prefix + "_" + key] = check
    checks.update(shaft_checks)
    checks.update(coupling_checks)
    model_ok, model_message = _native_validation(model)
    checks["native_geometry"] = {"value": model_message, "limit": "schema v5 装配链", "passed": model_ok}
    steps = [
        {"title": "输入工况与效率", "text": f"减速器输入功率 P1={powers['input']:.4f} kW，输入转速 n1={speeds['input']:.3f} r/min。两级总效率 η=(ηg·ηb)^2={efficiency:.4f}。"},
        {"title": "传动比分配", "text": f"目标总传动比={speeds['input']/values['target_output_speed_rpm']:.4f}；按 λ={values['ratio_distribution_factor']:.4g}，i1≈sqrt(i·λ)；实际 i1={high_ratio:.4f}、i2={low_ratio:.4f}、i={high_ratio*low_ratio:.4f}。"},
        {"title": "三轴动力参数", "text": "；".join(f"{name}: P={powers[name]:.4f} kW，n={speeds[name]:.3f} r/min，T=9550000P/n={9550000*powers[name]/speeds[name]:.1f} N·mm" for name in ("input", "intermediate", "output"))},
    ]
    for prefix, design in (("high", stage_high), ("low", stage_low)):
        result = design["results"]
        factors = design["factors"]
        width = min(values[prefix + "_pinion_width"], values[prefix + "_wheel_width"])
        steps.append({"title": ("高速级" if prefix == "high" else "低速级") + "齿轮设计与强度",
                      "text": f"m={values[prefix+'_module']:g} mm，z={values[prefix+'_pinion_teeth']}/{values[prefix+'_wheel_teeth']}，"
                              f"β={values[prefix+'_helix_angle']:g}°，d1/d2={result['dPinion']:.3f}/{result['dGear']:.3f} mm，"
                              f"a=mn(z1+z2)/(2cosβ)={result['centerDistance']:.3f} mm，"
                              f"有效齿宽 b={width:g} mm，Ft=2T/d1={result['tangentialForce']:.2f} N，v={result['pitchLineVelocity']:.3f} m/s。"
                              f"KH=KA·Kv·KHα·KHβ={factors['contactLoadFactor']:.3f}，KF=KA·Kv·KFα·KFβ={factors['bendingLoadFactor']:.3f}；"
                              f"εα={factors['contactRatio']:.3f}，ZH={factors['zoneFactor']:.3f}，ZE={factors['elasticFactor']:.3f}，Zε={factors['contactOverlap']:.3f}。"
                              f"σH=ZH·ZE·Zε·√[2KH·T/(φd·d1³)·(u+1)/u]={result['contactStress']:.2f} MPa，"
                              f"[σH]={values[prefix+'_contact_allow']:.2f} MPa；"
                              f"σF=KF·Ft·YFa·YSa·Yε/(b·m)，小/大齿轮={result['pinionBendStress']:.2f}/{result['gearBendStress']:.2f} MPa，"
                              f"[σF]={values[prefix+'_bend_allow']:.2f} MPa。载荷系数及许用应力需由设计者核定。"})
    steps.append({"title": "三轴、轴承与键", "text": "按三轴轴承跨距及齿轮切向、径向、轴向载荷合成保守初算简支反力、弯扭等效应力及 L10h；中间轴两级载荷按不利同向叠加。斜齿轴向力的实际轴承分担与轴向定位仍需工程复核。"})
    for name in ("input", "intermediate", "output"):
        if name not in shafts:
            continue
        shaft = shafts[name]
        steps.append({"title": {"input": "高速轴", "intermediate": "中间轴", "output": "低速轴"}[name] + "载荷与寿命",
                      "text": f"P={shaft['power_kw']:.4f} kW，n={shaft['speed_rpm']:.2f} r/min，T={shaft['torque_nmm']:.1f} N·mm；"
                              f"{shaft['bearing']} 两支承反力={shaft['bearing_reactions_n'][0]:.1f}/{shaft['bearing_reactions_n'][1]:.1f} N，"
                              f"L10h=10⁶/(60n)·(C/P)³={shaft['bearing_life_h']:.0f} h；"
                              f"危险截面 d={shaft['shaft_diameter_mm']:g} mm，弯扭等效应力={shaft['shaft_stress_mpa']:.2f} MPa，"
                              f"轴径初算下限={shaft['shaft_min_diameter_mm']:.2f} mm。"})
    steps.append({"title": "联轴器初选", "text": "；".join(
        f"{'输入端' if name == 'input' else '输出端'} {part['model']}：Tc=max(1.3,KA)·T={part['calculated_torque_nm']:.2f} N·m，"
        f"Tn={part['nominal_torque_nm']:g} N·m，轴端直径={part['shaft_end_diameter_mm']:g} mm，"
        f"目录最大孔径={part['max_bore_mm']:g} mm，允许转速={part['max_speed_rpm']:g} r/min"
        for name, part in couplings.items()) + "。联轴器实体未纳入当前原生装配；孔径、轮毂和键槽需实物核对。"})
    steps.append({"title": "润滑与箱体", "text": "；".join(
        f"{'高速级' if name == 'high' else '低速级'} v={item['pitch_speed_mps']:.3f} m/s，"
        f"齿高={item['tooth_height_mm']:.2f} mm，建议{item['gear_lubrication']}、初步浸油深度≥{item['immersion_depth_mm']:.1f} mm"
        for name, item in lubrication.items()) + "。轴承润滑脂、油位、密封与简化箱体油池须在制造设计时核定。"})
    passed = all(check["passed"] for check in checks.values())
    return {"inputs": values,
            "ratio": {"target": speeds["input"] / values["target_output_speed_rpm"],
                      "high": high_ratio, "low": low_ratio, "actual": high_ratio * low_ratio,
                      "output_speed_rpm": speeds["output"], "error_percent": speed_error * 100},
            "efficiency": efficiency, "high_stage": stage_high, "low_stage": stage_low,
            "shafts": shafts, "couplings": couplings, "lubrication": lubrication,
            "checks": checks, "calculation_steps": steps,
            "system_checks_passed": passed, "model_parameters": model,
            "limitations": ["材料许用值及载荷系数需按工况人工复核。",
                            "轴采用简支梁危险截面初算，尚需全轴疲劳、键配合及轴承实际载荷校核。",
                            "联轴器为目录初选；原生装配没有联轴器实体，油池、密封和润滑系统也尚未建模。",
                            "原生模型沿用参考装配的轴颈、轴承、键及轴向链，尚未校核整机干涉和制造公差。"]}


def _write_json(path, payload):
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile("w", encoding="utf-8", dir=path.parent, suffix=".tmp", delete=False) as stream:
        temporary = Path(stream.name)
        json.dump(payload, stream, ensure_ascii=False, indent=2, allow_nan=False)
    temporary.replace(path)
    return str(path)


def _check_label(key):
    exact = {"output_speed": "输出转速偏差",
             "native_geometry": "原生装配几何"}
    if key in exact:
        return exact[key]
    prefix = key.split("_")[0]
    label = {"high": "高速级", "low": "低速级", "input": "高速轴",
             "intermediate": "中间轴", "output": "低速轴"}.get(prefix, prefix)
    suffixes = {"contact_strength": "接触强度", "pinion_bending": "小齿轮弯曲",
                "gear_bending": "大齿轮弯曲", "center_distance": "中心距",
                "design_module": "模数", "coefficient_domain": "公式适用范围",
                "bearing_life": "轴承寿命", "bearing_speed": "轴承限速",
                "shaft_stress": "轴弯扭应力", "shaft_diameter": "轴径初算",
                "bearing_geometry": "轴承跨距", "bearing_catalog": "轴承型号",
                "coupling": "联轴器初选"}
    for suffix, text in suffixes.items():
        if key.endswith("_" + suffix):
            return label + text
    if "_key_" in key:
        return label + "键压强 " + key.rsplit("_", 1)[-1]
    return key


def _report_value(value):
    if value is None:
        return "—"
    if isinstance(value, (list, tuple)):
        return "～".join(_report_value(item) for item in value)
    if isinstance(value, float):
        return f"{value:.4g}"
    return str(value).replace("|", "∣")


def _report_calculations(design):
    """Keep the calculation data unchanged while typesetting report equations."""
    values, ratio = design["inputs"], design["ratio"]
    shafts = design["shafts"]
    stages = (("高速级", "high", design["high_stage"]),
              ("低速级", "low", design["low_stage"]))
    sections = []

    def add(title, description, *equations):
        sections.append((title, description, equations))

    add("输入工况与效率", "以减速器输入轴功率和转速为起点；效率用于计算后续轴的功率。",
        rf"P_1={values['input_power_kw']:.4f}\;\mathrm{{kW}},\quad n_1={values['input_speed_rpm']:.3f}\;\mathrm{{r/min}}",
        rf"\eta=(\eta_g\eta_b)^2={design['efficiency']:.4f}")
    add("传动比分配", "按目标输出转速与输入转速分配两级传动比。",
        rf"i_\mathrm{{target}}=\frac{{n_1}}{{n_\mathrm{{target}}}}={ratio['target']:.4f}",
        rf"i_1\approx\sqrt{{i_\mathrm{{target}}\lambda}},\quad\lambda={values['ratio_distribution_factor']:.4g}",
        rf"i_1={ratio['high']:.4f},\quad i_2={ratio['low']:.4f},\quad i=i_1i_2={ratio['actual']:.4f}")
    dynamics = []
    for name, label in (("input", "输入轴"), ("intermediate", "中间轴"), ("output", "输出轴")):
        shaft = shafts[name]
        dynamics.append(rf"\text{{{label}}}:\quad P={shaft['power_kw']:.4f}\;\mathrm{{kW}},\quad n={shaft['speed_rpm']:.3f}\;\mathrm{{r/min}},\quad T=\frac{{9550000P}}{{n}}={shaft['torque_nmm']:.1f}\;\mathrm{{N\cdot mm}}")
    add("三轴动力参数", "按功率、转速计算各轴转矩。", *dynamics)
    for label, prefix, stage in stages:
        result, factors = stage["results"], stage["factors"]
        width = min(values[prefix + "_pinion_width"], values[prefix + "_wheel_width"])
        add(label + "齿轮设计与强度", f"有效齿宽 {width:g} mm；载荷系数及许用应力需由设计者核定。",
            rf"m_n={values[prefix+'_module']:g}\;\mathrm{{mm}},\quad(z_1,z_2)=({values[prefix+'_pinion_teeth']},{values[prefix+'_wheel_teeth']}),\quad\beta={values[prefix+'_helix_angle']:g}^\circ",
            rf"d_1={result['dPinion']:.3f}\;\mathrm{{mm}},\quad d_2={result['dGear']:.3f}\;\mathrm{{mm}},\quad a=\frac{{m_n(z_1+z_2)}}{{2\cos\beta}}={result['centerDistance']:.3f}\;\mathrm{{mm}}",
            rf"F_t=\frac{{2T}}{{d_1}}={result['tangentialForce']:.2f}\;\mathrm{{N}},\quad v={result['pitchLineVelocity']:.3f}\;\mathrm{{m/s}}",
            rf"K_H=K_AK_VK_{{H\alpha}}K_{{H\beta}}={factors['contactLoadFactor']:.3f},\quad K_F=K_AK_VK_{{F\alpha}}K_{{F\beta}}={factors['bendingLoadFactor']:.3f}",
            rf"\varepsilon_\alpha={factors['contactRatio']:.3f},\quad Z_H={factors['zoneFactor']:.3f},\quad Z_E={factors['elasticFactor']:.3f},\quad Z_\varepsilon={factors['contactOverlap']:.3f}",
            rf"\sigma_H=Z_HZ_EZ_\varepsilon\sqrt{{\frac{{2K_HT}}{{\phi_d d_1^3}}\frac{{u+1}}{{u}}}}={result['contactStress']:.2f}\;\mathrm{{MPa}},\quad[\sigma_H]={values[prefix+'_contact_allow']:.2f}\;\mathrm{{MPa}}",
            rf"\sigma_F=\frac{{K_FF_tY_{{Fa}}Y_{{Sa}}Y_\varepsilon}}{{bm_n}},\quad(\sigma_{{F1}},\sigma_{{F2}})=({result['pinionBendStress']:.2f},{result['gearBendStress']:.2f})\;\mathrm{{MPa}},\quad[\sigma_F]={values[prefix+'_bend_allow']:.2f}\;\mathrm{{MPa}}")
    add("三轴、轴承与键", "按三轴轴承跨距及齿轮切向、径向、轴向载荷合成简支反力。中间轴两级载荷按不利同向叠加；斜齿轴向力分担与定位仍需复核。")
    for name, label in (("input", "高速轴"), ("intermediate", "中间轴"), ("output", "低速轴")):
        shaft = shafts.get(name)
        if not shaft:
            continue
        reactions = shaft["bearing_reactions_n"]
        add(label + "载荷与寿命", f"轴承型号 {shaft['bearing']}；危险截面直径 {shaft['shaft_diameter_mm']:g} mm。",
            rf"P={shaft['power_kw']:.4f}\;\mathrm{{kW}},\quad n={shaft['speed_rpm']:.2f}\;\mathrm{{r/min}},\quad T={shaft['torque_nmm']:.1f}\;\mathrm{{N\cdot mm}}",
            rf"(R_1,R_2)=({reactions[0]:.1f},{reactions[1]:.1f})\;\mathrm{{N}}",
            rf"L_{{10h}}=\frac{{10^6}}{{60n}}\left(\frac{{C}}{{P_e}}\right)^3={shaft['bearing_life_h']:.0f}\;\mathrm{{h}}",
            rf"\sigma_{{eq}}={shaft['shaft_stress_mpa']:.2f}\;\mathrm{{MPa}},\quad d_\mathrm{{min}}={shaft['shaft_min_diameter_mm']:.2f}\;\mathrm{{mm}}")
    coupling_lines = []
    for name, part in design["couplings"].items():
        label = "输入端" if name == "input" else "输出端"
        coupling_lines.append(rf"\text{{{label} {part['model']}}}:\quad T_c=\max(1.3,K_A)T={part['calculated_torque_nm']:.2f}\;\mathrm{{N\cdot m}},\quad T_n={part['nominal_torque_nm']:g}\;\mathrm{{N\cdot m}}")
    add("联轴器初选", "轴端孔径、轮毂和键槽应按实物复核。", *coupling_lines)
    lubrication_lines = []
    for name, item in design["lubrication"].items():
        label = "高速级" if name == "high" else "低速级"
        lubrication_lines.append(rf"\text{{{label}}}:\quad v={item['pitch_speed_mps']:.3f}\;\mathrm{{m/s}},\quad h_t={item['tooth_height_mm']:.2f}\;\mathrm{{mm}},\quad h_\mathrm{{oil}}\geq {item['immersion_depth_mm']:.1f}\;\mathrm{{mm}}")
    add("润滑与箱体", "油位、密封与箱体油池仍需制造设计复核。", *lubrication_lines)
    lines = []
    for index, (title, description, equations) in enumerate(sections, 1):
        lines.extend([f"### {index}. {title}", "", description])
        for expression in equations:
            lines.extend(["", "$$", expression, "$$"])
        lines.append("")
    return lines


def _report(project, design):
    values = design["inputs"]
    lines = ["# 二级圆柱齿轮减速器设计说明书", "",
             "项目：" + str(project.get("name", "二级减速器设计")),
             "设计者：" + str(project.get("designer", "")), "",
             "## 工况与设计步骤", ""]
    lines += _report_calculations(design)
    lines += ["## 校核结果", "", "| 项目 | 计算值 | 许用或目标 | 结论 |", "| --- | ---: | ---: | --- |"]
    for name, check in design["checks"].items():
        actual = check.get("value", "适用" if name.endswith("coefficient_domain") else "—")
        limit = check.get("limit", check.get("minimum", check.get("limits", "—")))
        lines.append(f"| {_check_label(name)} | {_report_value(actual)} | {_report_value(limit)} | {'满足' if check['passed'] else '需调整'} |")
    lines += ["", "## 模型参数", "", "齿轮和轴线尺寸来自本设计结果；轴颈、轴承、键和轴向安装链沿用参考模型，详见同目录 model_parameters.json。", "",
              "## 后续复核", ""]
    lines += ["- " + line for line in design["limitations"]]
    lines += ["", f"设计输入：{json.dumps(values, ensure_ascii=False)}", ""]
    return "\n".join(lines)


def handle_request(request):
    action = request.get("action")
    if action == "two_schema":
        return {"ok": True, "schema": {"defaults": DEFAULTS, "standard_modules": STANDARD_MODULES,
                                         "bearing_catalog": ["6204", "6205", "6209"],
                                         "actions": ["two_schema", "two_auto", "two_calculate", "two_export", "two_report"]}}
    if action not in ("two_auto", "two_calculate", "two_export", "two_report"):
        raise ValueError("未知二级设计操作。")
    design = calculate(request.get("parameters", {}), automatic=action == "two_auto")
    result = {"ok": True, "design": design}
    if action in ("two_export", "two_report"):
        if not design["system_checks_passed"]:
            raise ValueError("当前方案有未通过的校核，先调整工况、齿轮参数或装配链。")
        folder = Path(request.get("output_dir") or PLUGIN / "design_projects" / datetime.now().strftime("%Y%m%d_%H%M%S_%f")).resolve()
        model = design.pop("model_parameters")
        result["model_parameters_file"] = _write_json(folder / "model_parameters.json", model)
        if action == "two_report":
            path = folder / "two_stage_design.md"
            path.write_text(_report(request.get("project", {}), design), encoding="utf-8")
            result["report_file"] = str(path)
        result["project_file"] = _write_json(folder / "project.json", {
            "format": "two_stage_reducer_design_project", "version": 1,
            "project": request.get("project", {}), "design": design,
            "model_parameters_file": result["model_parameters_file"],
        })
        result["design"] = design
    return result
