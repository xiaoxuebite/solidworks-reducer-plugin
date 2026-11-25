"""Bounded automatic shaft/key/bearing sizing using the page's calculation scope."""

import copy
import math

from design_service import (
    DesignInputError, FIXED_GEAR_FIELDS, NUMERIC_FIELDS, STANDARD_MODULES,
    _interpolate, calculate_design, number, propose_design,
)
from model_generator.shaft_params import BEARING_CATALOG, key_for_diameter, preferred_at_least
from model_generator.housing_params import annotate_housing, cast_housing_dimensions
from model_generator.axial_layout import axial_layout


NOTICE = "项目简化模型内的数值校核通过；系数来源、材料许用值、轴向定位、全轴疲劳、齿形、配合公差和装配干涉仍需人工复核，不可直接作为制造依据。"
STATIC_RATINGS = {
    **{str(6200 + i): value for i, value in enumerate((2.38, 3.05, 3.72, 4.78, 6.65, 7.88, 11.5, 15.2, 18, 20.5, 23.2, 29.2, 32.8, 40, 45, 49.5, 54.2, 63.8, 71.5, 81.8, 92.8))},
    **{str(6300 + i): value for i, value in enumerate((3.48, 5.08, 5.42, 6.58, 7.88, 11.5, 15.2, 19.2, 24, 31.8, 38, 44.8, 51.8, 60.5, 68, 76.8, 86.5, 96.5, 108))},
}
AXIAL_FACTORS = ((.014, .19, 2.30), (.028, .22, 1.99), (.056, .26, 1.71),
                 (.084, .28, 1.55), (.110, .30, 1.45), (.170, .34, 1.31),
                 (.280, .38, 1.15), (.420, .42, 1.04), (.560, .44, 1.00))


def geometry(inputs, prefix):
    lengths = [inputs[f"{prefix}S{i}L"] for i in range(1, 9 if prefix == "high" else 7)]
    if prefix == "high":
        return lengths[0] + lengths[1] / 2, sum(lengths[:4]) + lengths[4] / 2, sum(lengths[:7]) + lengths[7] / 2
    return lengths[0] / 2, lengths[0] + lengths[1] / 2, sum(lengths[:3]) + lengths[3] / 2


def shaft_stress(torque, pitch_diameter, section_diameter, positions, alpha, beta, factor, tangential_force=None):
    left, gear, right = positions
    span, x = max(right - left, 1), gear - left
    ft = 2 * torque / pitch_diameter if tangential_force is None else tangential_force
    fr = ft * math.tan(alpha) / math.cos(beta)
    bending = factor * math.hypot(ft, fr) * (span - x) / span * x
    return math.hypot(bending, .6 * torque) / (.1 * section_diameter ** 3)


def bearing_loads(code, ft, positions, alpha, beta, support="envelope"):
    left, gear, right = positions
    if not left < gear < right or support not in ("envelope", "left", "right", "none"):
        raise DesignInputError("轴承支承位置或轴向定位方式无效。")
    fraction = (gear - left) / (right - left)
    radial = ft * math.hypot(1, math.tan(alpha) / math.cos(beta))
    axial = abs(ft * math.tan(beta))
    loads = []
    for side, reaction in (("left", radial * (1 - fraction)), ("right", radial * fraction)):
        # Envelope cases are alternatives, not two simultaneous full axial loads.
        fa = axial if support in ("envelope", side) else 0
        ratio = fa / (STATIC_RATINGS[code] * 1000)
        e, y = AXIAL_FACTORS[-1][1:]
        if ratio <= AXIAL_FACTORS[0][0]:
            e, y = AXIAL_FACTORS[0][1:]
        else:
            for previous, following in zip(AXIAL_FACTORS, AXIAL_FACTORS[1:]):
                if ratio <= following[0]:
                    weight = (ratio - previous[0]) / (following[0] - previous[0])
                    e, y = (previous[i] + weight * (following[i] - previous[i]) for i in (1, 2))
                    break
        combined = fa > 0 and (reaction == 0 or fa / reaction > e)
        loads.append({"side": side, "radialLoad": reaction, "axialLoad": fa,
                      "p": .56 * reaction + y * fa if combined else reaction,
                      "within_table": ratio <= AXIAL_FACTORS[-1][0]})
    return loads


def bearing_life(code, speed, ft, positions, alpha, beta, factor, support="envelope"):
    loads = bearing_loads(code, ft, positions, alpha, beta, support)
    if speed <= 0 or ft <= 0 or not all(item["within_table"] for item in loads):
        return 0
    return 1000000 / (60 * speed) * (BEARING_CATALOG[code]["dynamic_load"] * 1000
                                    / (factor * max(item["p"] for item in loads))) ** 3


def key_spec(diameter, torque, maximum_length=None):
    spec = key_for_diameter(diameter)
    width, height = spec["width"], spec["height"]
    needed = width + 2 * torque / (diameter * height / 2 * 100 * .85)
    length = max(18, math.ceil(needed / 2) * 2)
    if maximum_length is not None and length > maximum_length:
        return None
    stress = 2 * torque / (diameter * (length - width) * height / 2)
    return f"{width:g} x {height:g} x {length:g}", length, stress


def _select_shaft(design, prefix):
    p, r = design["inputs"], design["results"]
    high = prefix == "high"
    torque = 9550000 * p["P"] / p["n1"] if high else r["outTorque"]
    speed = p["n1"] if high else r["outSpeed"]
    minimum = r["highMinDiameter" if high else "lowMinDiameter"] * 1.1
    root = r["dPinion"] - 2.5 * p["moduleM"]
    alpha, beta = math.radians(p["pressureAngle"]), math.radians(p["helixAngle"])
    factor = design["factors"]["applicationFactor"]
    codes = sorted(STATIC_RATINGS, key=lambda code: (BEARING_CATALOG[code]["inner_diameter"], BEARING_CATALOG[code]["outer_diameter"]))
    for code in codes:
        bearing = BEARING_CATALOG[code]
        seat, width = bearing["inner_diameter"], bearing["width"]
        if seat < minimum + 3 or speed > bearing["oil_speed"]:
            continue
        outer = preferred_at_least(max(minimum, seat - (5 if high else 8)))
        if outer >= seat:
            continue
        outer_key = key_spec(outer, torque)
        if not outer_key or outer_key[1] > 160:
            continue
        if high:
            shoulder = preferred_at_least(seat + 3)
            if shoulder + 4 > root:
                continue
            diameters = [outer, seat, seat - 1, shoulder, root, shoulder, seat - 1, seat]
            shoulder_length = 6.5
            lengths = [max(24, outer_key[1] + 8), width + 22, 2, shoulder_length, p["pinionFaceWidth"], shoulder_length, 2, width + 5]
            section = root
            gear_key = None
        else:
            section = preferred_at_least(seat + 3)
            shoulder = preferred_at_least(section + 4)
            if shoulder + 8 > r["dGear"] - 2.5 * p["moduleM"]:
                continue
            gear_key = key_spec(section, torque, p["gearFaceWidth"] - 8)
            if gear_key is None:
                continue
            diameters = [seat, section, shoulder, seat, seat - 3, outer]
            lengths = [width + 10, p["gearFaceWidth"], 10, width, 20, max(33, outer_key[1] + 8)]
        fields = {f"{prefix}S{i}{name}": value for i, pair in enumerate(zip(diameters, lengths), 1)
                  for name, value in zip(("D", "L"), pair)}
        positions = geometry(fields, prefix)
        stress = shaft_stress(torque, r["dPinion" if high else "dGear"], section, positions, alpha, beta, factor,
                              tangential_force=r["tangentialForce"])
        life = bearing_life(code, speed, r["tangentialForce"], positions, alpha, beta, factor,
                            design["selections"][prefix + "AxialSupport"])
        if stress > 60 or life < r["requiredBearingLife"]:
            continue
        return fields, code, outer_key, gear_key, stress, life
    return None


def _complete(candidate):
    design = copy.deepcopy(candidate)
    high, low = _select_shaft(design, "high"), _select_shaft(design, "low")
    if not high or not low:
        return None
    p, s, r, checks = design["inputs"], design["selections"], design["results"], design["checks"]
    for prefix, selected in (("high", high), ("low", low)):
        fields, code, outer_key, _, stress, life = selected
        p.update(fields)
        p.update({f"{prefix}AllowStress": 60, f"{prefix}TauAllow": 35})
        s.update({f"{prefix}Bearing": code, f"{prefix}ShaftMaterial": "S45", f"{prefix}ShaftLoadCondition": "mid"})
        r["highShaftStress" if prefix == "high" else "shaftStress"] = stress
        r[f"{prefix}BearingLife"] = life
        checks[f"{prefix}_shaft_strength"] = {"value": stress, "limit": 60, "passed": stress <= 60}
        checks[f"{prefix}_bearing_life"] = {"value": life, "minimum": r["requiredBearingLife"], "passed": life >= r["requiredBearingLife"]}
    for field, selected in (("highKey", high[2]), ("lowGearKey", low[3]), ("lowOuterKey", low[2])):
        s[field], s[field + "Count"] = selected[0], "single"
        p[field + "PressAllow"] = 100
        r[field + "PressStress"] = selected[2]
        checks[field + "_pressure"] = {"value": selected[2], "limit": 100, "passed": selected[2] <= 100}
    layout = axial_layout(p, s)
    p.update(layout["shaft_lengths"])
    design["axial_layout"] = layout
    for prefix, diameter in (("high", "highS5D"), ("low", "lowS2D")):
        positions = layout["local_supports"][prefix]
        torque = 9550000 * p["P"] / p["n1"] if prefix == "high" else r["outTorque"]
        speed = p["n1"] if prefix == "high" else r["outSpeed"]
        alpha, beta = math.radians(p["pressureAngle"]), math.radians(p["helixAngle"])
        factor = design["factors"]["applicationFactor"]
        stress = shaft_stress(torque, r["dPinion" if prefix == "high" else "dGear"], p[diameter], positions,
                              alpha, beta, factor, tangential_force=r["tangentialForce"])
        life = bearing_life(s[prefix + "Bearing"], speed, r["tangentialForce"], positions, alpha, beta,
                            factor, s[prefix + "AxialSupport"])
        r["highShaftStress" if prefix == "high" else "shaftStress"] = stress
        r[prefix + "BearingLife"] = life
        checks[prefix + "_shaft_strength"] = {"value": stress, "limit": 60, "passed": stress <= 60}
        checks[prefix + "_bearing_life"] = {"value": life, "minimum": r["requiredBearingLife"], "passed": life >= r["requiredBearingLife"]}
    if any(not checks[prefix + suffix]["passed"] for prefix in ("high", "low")
           for suffix in ("_shaft_strength", "_bearing_life")):
        return None
    r["requiredBearingLifeLow"] = r["requiredBearingLife"]
    s.update(housingMaterial="HT200", lubrication="浸油润滑", sealType="毡圈密封")
    checks["basic_geometry"] = {"passed": True, "note": "轴承孔径与轴颈一致，键长不超出安装段，齿轮轮缘留有径向余量；未做三维干涉检查。"}
    for field in ("highShaftConclusion", "shaftConclusion", "highKeyPressConclusion",
                  "lowGearKeyPressConclusion", "lowOuterKeyPressConclusion", "highBearingConclusion", "lowBearingConclusion"):
        design[field] = "满足"
    design.update(system_checks_passed=all(item["passed"] for item in checks.values()),
                  fully_validated=False, pending_checks=["material_allowables_review", "coefficient_source_review", "tooth_geometry_review",
                                                          "axial_support_review", "whole_shaft_fatigue", "assembly_interference", "manufacturing_tolerances"],
                  designConclusion=NOTICE, adjustmentAdvice=[])
    design["assumptions"].extend([
        "轴材料采用 45 钢，齿轮截面弯扭合成许用应力 60 MPa；键许用挤压应力 100 MPa，依据需人工确认。",
        "外伸端初算轴径放大 10%；深沟球轴承采用系统内置 62/63 系列额定值与油润滑转速上限。",
        "啮合载荷统一按输入侧转矩保守计算，总效率只修正输出；未细分啮合、轴承等损失。",
        "按现有两端向外限位模板，由转向、旋向自动分配轴向载荷：+Y 由观察端侧轴承承担，-Y 由对侧轴承承担，不按均分；其他定位结构需另建传力模型。",
        "未评估外部联轴器载荷、全轴疲劳和三维干涉；齿轮许用应力为人工提供的最终值。",
        "箱体、端盖和紧固件采用现有参数化建模模板，不能替代工程制造图。",
    ])
    design["formulaNotes"] = _formula_notes(design)
    return design


def _formula_notes(d):
    p, s, r, f = d["inputs"], d["selections"], d["results"], d["factors"]
    notes = [{"title": "传动参数", "formulaLatex": [r"i=z_2/z_1,\quad n_2=n_1/i,\quad P_2=\eta P_1", r"T_2=9550000P_2/n_2\;(N\cdot mm)"],
              "substitutionLatex": [f"i={p['z2']}/{p['z1']}={r['ratio']:.5f}", f"n_2={p['n1']}/{r['ratio']:.5f}={r['outSpeed']:.5f}", f"T_2=9550000\\times {r['outPower']:.5f}/{r['outSpeed']:.5f}={r['outTorque']:.5f}"]},
             {"title": "齿面接触应力", "formulaLatex": r"F_t=2T_1/d_1,\quad b=\min(b_1,b_2),\quad\sigma_H=Z_HZ_EZ_\varepsilon Z_\beta\sqrt{\frac{K_H F_t(u+1)}{bd_1u}}",
              "substitutionLatex": f"\\sigma_H={f['zoneFactor']*f['elasticFactor']*f['contactOverlap']:.5f}\\sqrt{{ {f['contactLoadFactor']*r['tangentialForce']*(r['ratio']+1):.5f}/{min(p['pinionFaceWidth'],p['gearFaceWidth'])*r['dPinion']*r['ratio']:.5f} }}={r['contactStress']:.5f}\\;MPa"}]
    beta = math.radians(p["helixAngle"])
    operating = d["gear_operating"]
    notes.insert(1, {"title": "转向、旋向与轴向力",
                     "formulaLatex": r"|F_{a1}|=|F_{a2}|=F_t\tan\beta,\quad F_{a2}=-F_{a1}",
                     "substitutionLatex": [r"\text{" + operating["rotationLabel"] + "}",
                                           r"\text{" + operating["helixHandLabel"] + "}",
                                           f"F_{{a1}}={r['pinionAxialForce']:.2f}\\;N,\\quad F_{{a2}}={r['gearAxialForce']:.2f}\\;N",
                                           r"\text{正号表示朝向输入端一侧的观察者；承载侧按现有两端限位结构自动确定。}"]})
    for title, teeth, width, field in (("小齿轮弯曲应力", "z1", "pinionFaceWidth", "pinionBendStress"),
                                        ("大齿轮弯曲应力", "z2", "gearFaceWidth", "gearBendStress")):
        equivalent_teeth = p[teeth] / math.cos(beta) ** 3
        yf, ys = _interpolate(equivalent_teeth, 1), _interpolate(equivalent_teeth, 2)
        notes.append({"title": title, "formulaLatex": r"\sigma_F=\frac{K_F F_tY_FY_SY_\varepsilon Y_\beta}{bm_n}",
                      "substitutionLatex": f"\\sigma_F=\\frac{{{f['bendingLoadFactor']:.5f}\\times{r['tangentialForce']:.5f}\\times{yf:.5f}\\times{ys:.5f}\\times{f['bendingOverlap']:.5f}\\times{f['helixBendingFactor']:.5f}}}{{{r['effectiveFaceWidth']}\\times{p['moduleM']}}}={r[field]:.5f}\\;MPa"})
    for prefix, title, field, diameter in (("high", "高速轴齿轮截面", "highShaftStress", "highS5D"), ("low", "低速轴齿轮截面", "shaftStress", "lowS2D")):
        torque = 9550000 * p["P"] / p["n1"] if prefix == "high" else r["outTorque"]
        equivalent = r[field] * .1 * p[diameter] ** 3
        bending = math.sqrt(max(0, equivalent ** 2 - (.6 * torque) ** 2))
        notes.append({"title": title, "formulaLatex": r"\sigma_{ca}=\frac{\sqrt{M^2+(0.6T)^2}}{0.1d^3}",
                      "substitutionLatex": f"\\sigma_{{ca}}=\\frac{{\\sqrt{{{bending:.5f}^2+(0.6\\times{torque:.5f})^2}}}}{{0.1\\times{p[diameter]}^3}}={r[field]:.5f}\\;MPa"})
        code, speed = s[prefix + "Bearing"], p["n1"] if prefix == "high" else r["outSpeed"]
        cr, life, fp = BEARING_CATALOG[code]["dynamic_load"] * 1000, r[prefix + "BearingLife"], f["applicationFactor"]
        load = cr / (fp * (life * 60 * speed / 1000000) ** (1 / 3))
        notes.append({"title": ("高速" if prefix == "high" else "低速") + "轴承寿命", "formulaLatex": r"L_h=\frac{10^6}{60n}\left(\frac{C_r}{f_pP_{eq}}\right)^3",
                      "substitutionLatex": f"L_h=\\frac{{10^6}}{{60\\times{speed:.5f}}}\\left(\\frac{{{cr:.5f}}}{{{fp}\\times{load:.5f}}}\\right)^3={life:.5f}\\;h"})
    for title, field, diameter, torque in (("高速轴外伸端键", "highKey", "highS1D", 9550000 * p["P"] / p["n1"]),
                                          ("低速轴齿轮键", "lowGearKey", "lowS2D", r["outTorque"]),
                                          ("低速轴外伸端键", "lowOuterKey", "lowS6D", r["outTorque"])):
        width, height, length = (float(value) for value in s[field].split(" x "))
        notes.append({"title": title, "formulaLatex": r"\sigma_p=\frac{2T}{d(L-b)(h/2)}",
                      "substitutionLatex": f"\\sigma_p=\\frac{{2\\times{torque:.5f}}}{{{p[diameter]}\\times({length}-{width})\\times({height}/2)}}={r[field+'PressStress']:.5f}\\;MPa"})
    return notes


def complete_design(requirements):
    if not isinstance(requirements, dict):
        raise DesignInputError("设计要求 requirements 必须是参数对象。")
    requirements = dict(requirements)
    fixed = {name: requirements[name] for name in FIXED_GEAR_FIELDS if name in requirements}
    derived_target = "target_speed" not in requirements and {"z1", "z2"} <= fixed.keys()
    if derived_target:
        values = {name: number(requirements.get(name), name, NUMERIC_FIELDS[name][1], NUMERIC_FIELDS[name][2])
                  for name in ("n1", "z1", "z2")}
        requirements["target_speed"] = values["n1"] * values["z1"] / values["z2"]
    search_requirements = {name: value for name, value in requirements.items() if name not in FIXED_GEAR_FIELDS}
    proposal = propose_design({**search_requirements, "max_candidates": 5}, fixed_parameters=fixed)
    fixed = proposal["search_space"]["fixed_parameters"]
    candidates = proposal["candidates"]
    if not candidates and fixed:
        raise DesignInputError("固定齿数、模数或齿宽与目标转速、中心距或齿轮强度要求不兼容，未找到可行方案。请调整固定参数，或允许自动选型。")
    trials = 0
    for candidate in candidates:
        # If compact gears cannot accommodate shaft seats or keys, grow the standard module.
        modules = (candidate["inputs"]["moduleM"],) if "moduleM" in fixed else STANDARD_MODULES
        for module in modules:
            if module < candidate["inputs"]["moduleM"]:
                continue
            scale = module / candidate["inputs"]["moduleM"]
            widths = {name: candidate["inputs"][name] if name in fixed else math.ceil(candidate["inputs"][name] * scale)
                      for name in ("pinionFaceWidth", "gearFaceWidth")}
            pitch = candidate["results"]["dPinion"] * scale
            alternatives = [widths]
            # A fixed module may still require a wider gear to accommodate its shaft key.
            for factor in (0.5, 0.7, 1.0):
                gear_width = widths["gearFaceWidth"] if "gearFaceWidth" in fixed else max(widths["gearFaceWidth"], math.ceil(factor * pitch))
                alternative = {"gearFaceWidth": gear_width,
                               "pinionFaceWidth": widths["pinionFaceWidth"] if "pinionFaceWidth" in fixed else max(widths["pinionFaceWidth"], gear_width + 5)}
                if alternative not in alternatives:
                    alternatives.append(alternative)
            for trial_widths in alternatives:
                parameters = {**candidate["inputs"], **candidate["selections"], "moduleM": module, **trial_widths}
                if max(trial_widths.values()) > 500:
                    continue
                trial = calculate_design(parameters, auto_select_module=False)
                if trial["results"]["outSpeed"] < 1:
                    raise DesignInputError("自动流程暂不支持输出转速低于 1 r/min 的工况。")
                trials += 1
                if not trial["gear_checks_passed"] or trial["results"]["centerDistance"] > float(requirements.get("max_center_distance", 450)):
                    continue
                trial["assumptions"] = candidate["assumptions"]
                design = _complete(trial)
                if design:
                    if fixed:
                        design["assumptions"].append("固定齿轮参数已保留：" + "，".join(f"{name}={value:g}" for name, value in fixed.items()) + "；未指定的参数仍由系统选型。")
                    if derived_target:
                        design["assumptions"].append("未单独指定目标输出转速，按输入转速和固定齿数计算目标值。")
                    design["automatic_selection"] = {"gear_combinations": proposal["evaluated_candidates"], "shaft_trials": trials,
                                                      "requirements": requirements, "fixed_parameters": fixed,
                                                      "method": "bounded_catalog_search"}
                    if "housingRibThickness" not in requirements:
                        design["inputs"]["housingRibThickness"] = cast_housing_dimensions(design["results"]["centerDistance"]).lower_rib
                    annotate_housing(design)
                    return design
    if fixed:
        raise DesignInputError("固定参数的齿轮方案未能满足轴、键、轴承寿命或安装尺寸要求。固定值未被擅自修改；请允许调整模数或齿宽，或修改工况限制后重试。")
    raise DesignInputError("当前要求下未找到同时满足齿轮、轴、键、轴承寿命及基础尺寸约束的方案。请调整转速、载荷、寿命或中心距限制。")
