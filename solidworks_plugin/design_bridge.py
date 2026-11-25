"""Offline JSON bridge between the eight-step design engine and the SW launcher.

This entry point computes and writes parameters/reports only. It never connects
to SolidWorks, starts HTTP, imports CadQuery, or creates a CAD document.
"""
from __future__ import annotations

import argparse
import copy
from datetime import datetime
import json
import math
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile

PLUGIN = Path(__file__).resolve().parent
ROOT = PLUGIN.parent
sys.path.insert(0, str(ROOT))

from complete_design import (NOTICE, STATIC_RATINGS, _complete, _formula_notes, bearing_life,
                             bearing_loads, complete_design, geometry, shaft_stress)
from design_service import CHOICES, NUMERIC_FIELDS, DesignInputError, calculate_design, get_design_schema
from model_generator.axial_layout import AXIAL_FIELDS, axial_layout, bearing_seat_diameters
from model_generator.housing_height import design_height
from model_generator.gear_operating import gear_operating_conditions
from model_generator.shaft_params import BEARING_CATALOG, root_diameter
from model_generator.vent_layout import VENT_FIELDS, cover_vent_layout

SEGMENTS = [f"{prefix}S{index}{field}" for prefix, count in (("high", 8), ("low", 6))
            for index in range(1, count + 1) for field in ("D", "L")]
KEY_FIELDS = ("highKey", "lowGearKey", "lowOuterKey")
INPUT_EXTRAS = {f"{prefix}{suffix}" for prefix in ("high", "low") for suffix in ("AllowStress", "TauAllow")}
INPUT_EXTRAS.update(field + "PressAllow" for field in KEY_FIELDS)
SELECTION_EXTRAS = {"highBearing", "lowBearing", "housingMaterial", "lubrication", "sealType",
                    "footBolt", "coverBolt", "ventPlug", "oilGauge",
                    "highShaftMaterial", "lowShaftMaterial", "highShaftLoadCondition", "lowShaftLoadCondition",
                    *KEY_FIELDS, *(field + "Count" for field in KEY_FIELDS)}
PROPOSAL_FIELDS = {"target_speed", "speed_tolerance", "max_center_distance", "max_candidates"}
PENDING = ["material_allowables_review", "coefficient_source_review", "tooth_geometry_review",
           "axial_support_review", "whole_shaft_fatigue", "assembly_interference", "manufacturing_tolerances"]


def _object(value, name):
    if not isinstance(value, dict):
        raise DesignInputError(f"{name} 必须是 JSON 对象。")
    return value


def _positive(value, name):
    try:
        result = float(value)
    except (TypeError, ValueError, OverflowError):
        result = float("nan")
    if isinstance(value, bool) or not math.isfinite(result) or result <= 0:
        raise DesignInputError(f"{name} 必须为大于 0 的有限数值。")
    return result


def _key(value, name):
    match = re.fullmatch(r"\s*(?:键\s*)?(\d+(?:\.\d+)?)\s*[x×*]\s*(\d+(?:\.\d+)?)\s*[x×*]\s*(\d+(?:\.\d+)?)\s*", str(value))
    if not match:
        raise DesignInputError(f"{name} 的键规格应为 宽 x 高 x 长，例如 6 x 6 x 20。")
    dimensions = tuple(_positive(part, name) for part in match.groups())
    if dimensions[2] <= dimensions[0]:
        raise DesignInputError(f"{name} 的键长必须大于键宽。")
    return dimensions


def _supplied(request):
    design = copy.deepcopy(_object(request.get("design", {}), "design"))
    project = _object(request.get("project", {}), "project")
    if not design and isinstance(project.get("design"), dict):
        design = copy.deepcopy(project["design"])
    inputs = copy.deepcopy(_object(design.get("inputs", {}), "design.inputs"))
    selections = copy.deepcopy(_object(design.get("selections", {}), "design.selections"))
    parameters = _object(request.get("parameters", {}), "parameters")
    known = set(NUMERIC_FIELDS) | set(CHOICES) | set(SEGMENTS) | INPUT_EXTRAS | SELECTION_EXTRAS | PROPOSAL_FIELDS
    known |= {"loadType", "dynamicAccuracyGrade", "highAxialSupport", "lowAxialSupport"}
    unknown = set(parameters) - known
    if unknown:
        raise DesignInputError("不支持的参数字段：" + "、".join(sorted(unknown)))
    for key, value in parameters.items():
        (selections if key in CHOICES or key in SELECTION_EXTRAS else inputs)[key] = value
    return design, inputs, selections


def _base_parameters(inputs, selections):
    # Only send the public gear-engine schema to its strict normalizer.
    allowed = set(NUMERIC_FIELDS) | set(CHOICES) | {"loadType", "dynamicAccuracyGrade"}
    return {key: value for key, value in {**inputs, **selections}.items() if key in allowed}


def _plain_steps(design):
    p, r = design["inputs"], design["results"]
    steps = [
        {"title": "传动参数", "text": f"i=z2/z1={p['z2']}/{p['z1']}={r['ratio']:.6g}；n2=n1/i={r['outSpeed']:.6g} r/min；P2=ηP1={r['outPower']:.6g} kW；T2=9550000P2/n2={r['outTorque']:.6g} N·mm。"},
        {"title": "齿轮尺寸与载荷", "text": f"d1=m·z1/cosβ={r['dPinion']:.6g} mm；d2={r['dGear']:.6g} mm；a=(d1+d2)/2={r['centerDistance']:.6g} mm；Ft=2T1/d1={r['tangentialForce']:.6g} N。"},
        {"title": "齿轮强度", "text": f"接触应力 σH={r['contactStress']:.6g} MPa；小/大齿轮弯曲应力={r['pinionBendStress']:.6g}/{r['gearBendStress']:.6g} MPa。系数与许用值按当前输入逐项校核。"},
    ]
    for name, check in design["checks"].items():
        if name.startswith(("high_", "low_")) or name.endswith("_pressure"):
            state = "满足" if check.get("passed") else "不满足" if check.get("performed", True) else "未计算"
            value = check.get("value")
            detail = f"计算值={value:.6g}；" if isinstance(value, (int, float)) else ""
            detail += str(check.get("note", ""))
            titles = {"high_shaft_strength": "高速轴齿轮截面弯扭强度", "low_shaft_strength": "低速轴齿轮截面弯扭强度",
                      "high_bearing_life": "高速轴承寿命", "low_bearing_life": "低速轴承寿命",
                      "highKey_pressure": "高速轴外伸端键挤压强度", "lowGearKey_pressure": "低速轴齿轮键挤压强度",
                      "lowOuterKey_pressure": "低速轴外伸端键挤压强度"}
            steps.append({"title": titles.get(name, name), "text": f"{state}；{detail}"})
    return steps


def _report_steps(design):
    """Render the offline appendix as math without changing UI calculation steps."""
    p, r = design["inputs"], design["results"]
    def equation(expression):
        return "\n\n$$\n" + expression + "\n$$\n"

    sections = [
        ("传动参数", [
            rf"i=\frac{{z_2}}{{z_1}}=\frac{{{p['z2']}}}{{{p['z1']}}}={r['ratio']:.6g}",
            rf"n_2=\frac{{n_1}}{{i}}={r['outSpeed']:.6g}\;\mathrm{{r/min}}",
            rf"P_2=\eta P_1={r['outPower']:.6g}\;\mathrm{{kW}}",
            rf"T_2=\frac{{9550000P_2}}{{n_2}}={r['outTorque']:.6g}\;\mathrm{{N\cdot mm}}",
        ], ""),
        ("齿轮尺寸与载荷", [
            rf"d_1=\frac{{m_nz_1}}{{\cos\beta}}={r['dPinion']:.6g}\;\mathrm{{mm}}",
            rf"d_2=\frac{{m_nz_2}}{{\cos\beta}}={r['dGear']:.6g}\;\mathrm{{mm}}",
            rf"a=\frac{{d_1+d_2}}{{2}}={r['centerDistance']:.6g}\;\mathrm{{mm}}",
            rf"F_t=\frac{{2T_1}}{{d_1}}={r['tangentialForce']:.6g}\;\mathrm{{N}}",
        ], ""),
        ("齿轮强度", [
            rf"\sigma_H={r['contactStress']:.6g}\;\mathrm{{MPa}}",
            rf"\sigma_{{F1}}={r['pinionBendStress']:.6g}\;\mathrm{{MPa}},\quad\sigma_{{F2}}={r['gearBendStress']:.6g}\;\mathrm{{MPa}}",
        ], "系数与许用值按当前输入逐项校核。"),
    ]
    titles = {"high_shaft_strength": "高速轴齿轮截面弯扭强度", "low_shaft_strength": "低速轴齿轮截面弯扭强度",
              "high_bearing_life": "高速轴承寿命", "low_bearing_life": "低速轴承寿命",
              "highKey_pressure": "高速轴外伸端键挤压强度", "lowGearKey_pressure": "低速轴齿轮键挤压强度",
              "lowOuterKey_pressure": "低速轴外伸端键挤压强度"}
    for name, check in design["checks"].items():
        if name not in titles:
            continue
        state = "满足" if check.get("passed") else "不满足" if check.get("performed", True) else "未计算"
        note = str(check.get("note", ""))
        formulas = []
        if name.endswith("_shaft_strength") and check.get("performed", True):
            formulas.append(r"\sigma_{ca}=\frac{\sqrt{M^2+(0.6T)^2}}{0.1d^3}")
            note = note.split("，", 1)[-1]
        elif name.endswith("_bearing_life") and check.get("performed", True):
            formulas.append(r"L_h=\frac{10^6}{60n}\left(\frac{C_r}{f_pP_{eq}}\right)^3")
            note = note.split("；", 1)[-1]
            note += "其中载荷系数 $f_p=K_A$。"
        elif name.endswith("_pressure") and check.get("performed", True):
            formulas.append(r"\sigma_p=\frac{2T}{Z\,d(L-b)(h/2)}")
            note = note.split("；", 1)[-1]
            note = note.replace("单键Z=1", "单键 $Z=1$").replace("双键Z=1.5", "双键 $Z=1.5$")
        value = check.get("value")
        if isinstance(value, (int, float)):
            symbol = "L_h" if name.endswith("_bearing_life") else "\\sigma_p" if name.endswith("_pressure") else "\\sigma_{ca}"
            unit = "\\mathrm{h}" if name.endswith("_bearing_life") else "\\mathrm{MPa}"
            formulas.append(rf"{symbol}={value:.6g}\;{unit}")
        sections.append((titles[name], formulas, f"结论：{state}。{note}"))
    return "\n\n".join("### " + title + "\n\n" + prose + "".join(equation(item) for item in formulas)
                       for title, formulas, prose in sections)


def recalculate(request, seed=None):
    """Preserve every explicit manual field and recompute all performed checks."""
    old, manual_p, manual_s = _supplied(request)
    preliminary = calculate_design(_base_parameters(manual_p, manual_s), auto_select_module=False)
    needs_fill = any(key not in manual_p for key in SEGMENTS) or any(key not in manual_s for key in (*KEY_FIELDS, "highBearing", "lowBearing"))
    fill = copy.deepcopy(seed)
    fill_error = None
    if needs_fill and fill is None:
        fill = _complete(copy.deepcopy(preliminary))
        if fill is None:
            # Gear checks may legitimately fail for a manual design. A separate
            # automatic proposal can supply missing shaft data, never its gears.
            requirements = _base_parameters(manual_p, manual_s)
            for key in ("z1", "z2", "moduleM", "pinionFaceWidth", "gearFaceWidth"):
                requirements.pop(key, None)
            requirements["target_speed"] = preliminary["results"]["outSpeed"]
            try:
                fill = complete_design(requirements)
            except DesignInputError as error:
                fill_error = str(error)
    d = {**old, **preliminary}
    d.pop("native_geometry_adjustments", None)
    d.pop("geometry_linkage", None)
    p, s = d["inputs"], d["selections"]
    if fill:
        for key in (*SEGMENTS, *INPUT_EXTRAS):
            if key in fill["inputs"] and key not in manual_p:
                p[key] = fill["inputs"][key]
        for key in SELECTION_EXTRAS:
            if key in fill["selections"] and key not in manual_s:
                s[key] = fill["selections"][key]
    for key in (*SEGMENTS, *INPUT_EXTRAS):
        if key in manual_p:
            p[key] = _positive(manual_p[key], key)
    for key in SELECTION_EXTRAS:
        if key in manual_s:
            s[key] = manual_s[key]
    lubrication = s.setdefault("lubrication", "浸油润滑")
    if lubrication not in ("浸油润滑", "飞溅润滑", "油脂润滑"):
        raise DesignInputError("润滑方式仅支持浸油润滑、飞溅润滑或油脂润滑，请重新选择。")
    assumptions = list(preliminary.get("assumptions", []))
    filled = [key for key in (*SEGMENTS, *INPUT_EXTRAS) if key not in manual_p and key in p]
    filled += [key for key in SELECTION_EXTRAS if key not in manual_s and key in s]
    if filled:
        assumptions.append("缺失字段由自动选型补全，手工值优先：" + "、".join(sorted(filled)))
    if fill_error:
        assumptions.append("缺失数据无法自动补齐，相关校核标记为未计算：" + fill_error)
    documentary = [key for key in ("footBolt", "coverBolt", "ventPlug", "oilGauge") if key in manual_s]
    if documentary:
        assumptions.append("以下附件选项会记录在项目和说明书中，模型尺寸沿用当前建模模板：" + "、".join(documentary))
    for key in INPUT_EXTRAS:
        if key not in p:
            p[key] = 100.0 if key.endswith("PressAllow") else 35.0 if key.endswith("TauAllow") else 60.0
            assumptions.append(f"未给定 {key}，暂取 {p[key]:g} MPa，材料依据需确认。")
    checks, r = d["checks"], d["results"]
    dimensions_ok = True
    messages = []
    # Bearing-seat segments can include spacers and oil rings. Their segment
    # midpoints are not the assembled bearing centers used by the webpage.
    # Resolve the shared chain first, without overwriting any manual dimension.
    for key in SEGMENTS:
        if key in p:
            p[key] = _positive(p[key], key)
    for prefix in ("high", "low"):
        if prefix + "Bearing" in s:
            s[prefix + "Bearing"] = str(s[prefix + "Bearing"])
    derive = request.get("derive_linked_geometry", False)
    if not isinstance(derive, bool):
        raise DesignInputError("derive_linked_geometry 必须为 true 或 false。")
    if derive and all(key in p for key in SEGMENTS) and all(s.get(prefix + "Bearing") in BEARING_CATALOG for prefix in ("high", "low")):
        changes = []

        def apply_linkage(values, reason):
            for key, value in values.items():
                if abs(p[key] - value) > 1e-7:
                    changes.append({"field": key, "before": p[key], "after": value, "reason": reason})
                    p[key] = value

        apply_linkage({**bearing_seat_diameters(s), "highS5D": root_diameter(p["z1"] / math.cos(math.radians(p["helixAngle"])), p["moduleM"]),
                       "highS5L": p["pinionFaceWidth"], "lowS2L": p["gearFaceWidth"]}, "轴承型号及齿轮尺寸联动")
        linked = axial_layout(p, s)
        apply_linkage(linked["shaft_lengths"], "轴向装配尺寸链联动")
        if changes:
            d["geometry_linkage"] = changes
            assumptions.append("已按当前轴承型号、齿轮尺寸及轴向装配链同步联动尺寸：" + "、".join(item["field"] for item in changes) + "；逐项修改前后数值见 geometry_linkage。")
    layout = None
    if all(key in p for key in SEGMENTS) and all(s.get(prefix + "Bearing") in BEARING_CATALOG for prefix in ("high", "low")):
        layout = axial_layout(p, s)
        d["axial_layout"] = layout
    linked_supports = layout is not None and not (layout["length_changes"] or layout["diameter_changes"])
    for prefix, count in (("high", 8), ("low", 6)):
        fields = [f"{prefix}S{i}{field}" for i in range(1, count + 1) for field in ("D", "L")]
        missing = [key for key in fields if key not in p]
        code = str(s.get(prefix + "Bearing", ""))
        if code and code not in BEARING_CATALOG:
            raise DesignInputError(f"{prefix}Bearing 轴承型号不在内置目录中：{code}")
        for key in fields:
            if key in p:
                p[key] = _positive(p[key], key)
        stress_key = "highShaftStress" if prefix == "high" else "shaftStress"
        if missing or not code:
            dimensions_ok = False
            note = "缺少轴段或轴承数据：" + "、".join(missing or [prefix + "Bearing"])
            for suffix, result_key in (("_shaft_strength", stress_key), ("_bearing_life", prefix + "BearingLife")):
                checks[prefix + suffix] = {"passed": False, "performed": False, "note": note}
                r[result_key] = None
            continue
        s[prefix + "Bearing"] = code
        torque = r["inputTorque"] if prefix == "high" else r["outTorque"]
        speed = p["n1"] if prefix == "high" else r["outSpeed"]
        positions = layout["local_supports"][prefix] if linked_supports else geometry(p, prefix)
        alpha, beta = math.radians(p["pressureAngle"]), math.radians(p["helixAngle"])
        factor = d["factors"]["applicationFactor"]
        section = p["highS5D" if prefix == "high" else "lowS2D"]
        stress = shaft_stress(torque, r["dPinion" if prefix == "high" else "dGear"], section,
                              positions, alpha, beta, factor, r["tangentialForce"])
        r[stress_key] = stress
        checks[prefix + "_shaft_strength"] = {"value": stress, "limit": p[prefix + "AllowStress"], "passed": stress <= p[prefix + "AllowStress"], "performed": True,
                                               "note": "σca=√(M²+(0.6T)²)/(0.1d³)，按装配链中的实际轴承中心计算。" if linked_supports else "σca=√(M²+(0.6T)²)/(0.1d³)，当前手工尺寸未合链，暂按轴颈段中心计算；不可据此生成原生装配。"}
        if code in STATIC_RATINGS:
            life = bearing_life(code, speed, r["tangentialForce"], positions, alpha, beta, factor, s[prefix + "AxialSupport"])
            loads = bearing_loads(code, r["tangentialForce"], positions, alpha, beta, s[prefix + "AxialSupport"])
            speed_key = "grease_speed" if lubrication == "油脂润滑" else "oil_speed"
            speed_limit = BEARING_CATALOG[code][speed_key]
            valid = all(load["within_table"] for load in loads) and speed <= speed_limit
            r[prefix + "BearingLife"] = life
            checks[prefix + "_bearing_life"] = {"value": life, "minimum": r["requiredBearingLife"], "passed": valid and life >= r["requiredBearingLife"], "performed": True,
                                                "loads": loads, "within_table_and_speed": valid, "lubrication": lubrication, "speed_limit_rpm": speed_limit,
                                                "note": f"Lh=10^6/(60n)·(Cr/(KA·Pe))³；左右分别计算取控制载荷；{lubrication}目录转速上限 {speed_limit:g} r/min。"}
        else:
            r[prefix + "BearingLife"] = None
            checks[prefix + "_bearing_life"] = {"passed": False, "performed": False, "note": f"{code} 缺少本项目轴向静载荷系数表，寿命未计算；保留手选型号。"}
        bearing = BEARING_CATALOG[code]
        for index in ((2, 8) if prefix == "high" else (1, 4)):
            if abs(p[f"{prefix}S{index}D"] - bearing["inner_diameter"]) > 1e-7:
                dimensions_ok = False
                messages.append(f"{prefix}S{index}D 与 {code} 内径不一致，保留手工值。")
    for field, diameter, segment, torque in (("highKey", "highS1D", "highS1L", r["inputTorque"]),
                                            ("lowGearKey", "lowS2D", "lowS2L", r["outTorque"]),
                                            ("lowOuterKey", "lowS6D", "lowS6L", r["outTorque"])):
        if field not in s or diameter not in p or segment not in p:
            checks[field + "_pressure"] = {"passed": False, "performed": False, "note": "缺少键或安装轴段数据。"}
            r[field + "PressStress"] = None
            continue
        width, height, length = _key(s[field], field)
        count = s.setdefault(field + "Count", "single")
        if count not in ("single", "double"):
            raise DesignInputError(f"{field}Count 仅支持 single 或 double。")
        stress = 2 * torque / ((1.5 if count == "double" else 1) * p[diameter] * (length - width) * height / 2)
        fits = length <= p[segment] - (6 if field == "highKey" else 4) + 1e-7 and width < p[diameter] and height / 2 < p[diameter] / 2
        r[field + "PressStress"] = stress
        checks[field + "_pressure"] = {"value": stress, "limit": p[field + "PressAllow"], "passed": fits and stress <= p[field + "PressAllow"], "performed": True,
                                       "fits_segment": fits, "note": "σp=2T/[Z·d·(L-b)·(h/2)]；单键Z=1，双键Z=1.5，同时检查键长与安装段。"}
        dimensions_ok &= fits
    if layout is not None:
        if layout["length_changes"] or layout["diameter_changes"]:
            dimensions_ok = False
            messages.append("当前手工轴段与箱体轴向装配链不一致，计算保留原值；原生参数导出前需调整。")
        root = p["moduleM"] * (p["z1"] / math.cos(math.radians(p["helixAngle"])) - 2.5)
        if abs(p["highS5D"] - root) > 1e-6:
            dimensions_ok = False
            messages.append("highS5D 与一体小齿轮齿根径不一致；原生契约不能单独修改该直径。")
        if abs(p["highS5L"] - p["pinionFaceWidth"]) > 1e-7 or abs(p["lowS2L"] - p["gearFaceWidth"]) > 1e-7:
            dimensions_ok = False
            messages.append("齿轮安装段长度与齿宽不一致。")
        messages.extend(layout["warnings"])
    checks["basic_geometry"] = {"passed": bool(dimensions_ok), "performed": True, "messages": messages,
                                 "note": "仅检查参数契约及轴承/键/轴向链；未做三维干涉。"}
    for field, check in (("highShaftConclusion", "high_shaft_strength"), ("shaftConclusion", "low_shaft_strength"),
                         ("highBearingConclusion", "high_bearing_life"), ("lowBearingConclusion", "low_bearing_life"),
                         *((key + "PressConclusion", key + "_pressure") for key in KEY_FIELDS)):
        d[field] = "满足" if checks[check]["passed"] else "不满足" if checks[check].get("performed", True) else "未计算"
    r["requiredBearingLifeLow"] = r["requiredBearingLife"]
    all_passed = all(check.get("passed", False) for check in checks.values())
    d.update(assumptions=assumptions, system_checks_passed=all_passed,
             fully_validated=False, pending_checks=list(PENDING) + [name for name, check in checks.items() if not check.get("performed", True)],
             designConclusion=NOTICE if all_passed else "当前方案存在不满足或未完成的校核；手工输入已保留，请根据逐项结果调整后重新计算。全轴疲劳、材料依据及三维干涉仍需复核。",
             adjustmentAdvice=messages + [name for name, check in checks.items() if not check.get("passed")])
    d["formulaNotes"] = []  # Saved substitutions belong to the previous inputs.
    if all(r.get(prefix + "BearingLife", 0) for prefix in ("high", "low")) and all(s.get(key + "Count", "single") == "single" for key in KEY_FIELDS):
        formula_design = copy.deepcopy(d)
        for key in KEY_FIELDS:
            formula_design["selections"][key] = " x ".join(f"{part:g}" for part in _key(s[key], key))
        d["formulaNotes"] = _formula_notes(formula_design)
    d["calculation_steps"] = _plain_steps(d)
    return d


def _environment(design):
    p, s = design["inputs"], design["selections"]
    if s.get("gearType") not in ("spur", "helical"):
        raise DesignInputError("齿轮类型必须为 spur 或 helical。")
    missing = [key for key in SEGMENTS if key not in p]
    missing += [key for key in (*KEY_FIELDS, "highBearing", "lowBearing") if key not in s]
    if missing:
        raise DesignInputError("原生导出缺少完整设计数据：" + "、".join(missing))
    if not design["checks"]["basic_geometry"]["passed"]:
        raise DesignInputError("手工尺寸不能忠实映射至当前原生装配契约：" + "；".join(design["checks"]["basic_geometry"].get("messages", [])))
    if any(s.get(key + "Count", "single") != "single" for key in KEY_FIELDS):
        raise DesignInputError("当前原生契约仅有单键槽，双键可以校核，但不能静默导出为单键。")
    if not design.get("system_checks_passed"):
        raise DesignInputError("当前设计存在未通过或未完成的强度/寿命校核，请调整后重新计算；可先保存项目及说明书。")
    height, axial, vent = design_height(p, s), axial_layout(p, s), cover_vent_layout(p, s)
    operating = gear_operating_conditions(p, s)
    fields = {"REDUCER_Z1": p["z1"], "REDUCER_Z2": p["z2"], "REDUCER_MODULE": p["moduleM"],
              "REDUCER_PRESSURE_ANGLE": p["pressureAngle"], "REDUCER_GEAR_TYPE": s["gearType"],
              "REDUCER_HELIX_ANGLE": operating["cadPinionHelixAngle"],
              "REDUCER_CENTER_DISTANCE": design["results"]["centerDistance"],
              "REDUCER_PINION_FACE_WIDTH": p["pinionFaceWidth"], "REDUCER_GEAR_FACE_WIDTH": p["gearFaceWidth"],
              "REDUCER_HIGH_SHAFT_D": p["highS2D"], "REDUCER_LOW_SHAFT_D": p["lowS1D"],
              "REDUCER_HIGH_BEARING": s["highBearing"], "REDUCER_LOW_BEARING": s["lowBearing"],
              "REDUCER_HIGH_KEY": s["highKey"], "REDUCER_LOW_GEAR_KEY": s["lowGearKey"], "REDUCER_LOW_OUTER_KEY": s["lowOuterKey"],
              "REDUCER_HOUSING_BOTTOM_CLEARANCE": height["bottom_clearance"]}
    for prefix, count in (("high", 8), ("low", 6)):
        for index in range(1, count + 1):
            for field in ("D", "L"):
                fields[f"REDUCER_{prefix.upper()}_S{index}_{field}"] = p[f"{prefix}S{index}{field}"]
    fields.update({env: p.get(key, default) for key, (env, default, _) in AXIAL_FIELDS.items()})
    fields.update({env: vent[key] for key, (env, _, _, _) in VENT_FIELDS.items()})
    environment = {key: value for key, value in os.environ.items() if not key.startswith("REDUCER_")}
    environment.update({key: str(value) for key, value in fields.items()})
    environment["PYTHONIOENCODING"] = "utf-8"
    return environment


def _write_json(path, value):
    path = Path(path).resolve()
    path.parent.mkdir(parents=True, exist_ok=True)
    # Replace atomically so a failed or interrupted write cannot leave half JSON.
    with tempfile.NamedTemporaryFile("w", encoding="utf-8", dir=path.parent, suffix=".tmp", delete=False) as stream:
        temporary = Path(stream.name)
        json.dump(value, stream, ensure_ascii=False, indent=2, allow_nan=False)
    temporary.replace(path)
    return str(path)


def handle_request(request):
    _object(request, "请求")
    action = request.get("action")
    if isinstance(action, str) and action.startswith("two_"):
        from solidworks_plugin.two_stage_design import handle_request as two_stage_request
        return two_stage_request(request)
    if action == "schema":
        # The public schema exposes the engine's choice catalog by reference.
        # Bridge-only manual fields must not mutate that shared strict schema.
        schema = copy.deepcopy(get_design_schema())
        schema["choices"].update({"highBearing": tuple(BEARING_CATALOG), "lowBearing": tuple(BEARING_CATALOG),
                                  "highKeyCount": ("single", "double"), "lowGearKeyCount": ("single", "double"), "lowOuterKeyCount": ("single", "double"),
                                  "highShaftMaterial": ("Q235A", "S20", "Q275", "S35", "1Cr18Ni9Ti", "S45", "40Cr", "35SiMn", "38SiMnMo", "3Cr13"),
                                  "lowShaftMaterial": ("Q235A", "S20", "Q275", "S35", "1Cr18Ni9Ti", "S45", "40Cr", "35SiMn", "38SiMnMo", "3Cr13"),
                                  "highShaftLoadCondition": ("best", "mid", "worst"), "lowShaftLoadCondition": ("best", "mid", "worst"),
                                  "housingMaterial": ("HT200", "HT250", "QT500-7"), "lubrication": ("浸油润滑", "飞溅润滑", "油脂润滑")})
        schema["bridge"] = {"actions": ["schema", "auto", "calculate", "export", "report"], "native_gear_types": ["spur", "helical"],
                            "request_options": {"derive_linked_geometry": {"type": "boolean", "default": False}},
                            "shaft_fields": SEGMENTS, "manual_input_fields": sorted(INPUT_EXTRAS),
                            "manual_selection_fields": sorted(SELECTION_EXTRAS), "bearing_catalog": BEARING_CATALOG}
        return {"ok": True, "schema": schema}
    if action not in ("auto", "calculate", "export", "report"):
        raise DesignInputError("action 必须为 schema、auto、calculate、export 或 report。")
    if action == "auto":
        # A prior design is a result snapshot, not an implicit set of fixed
        # gears, bearings and shafts for a new search. Only this request's
        # explicit parameter values constrain automatic selection.
        _, p, s = _supplied({"parameters": request.get("parameters", {})})
        requirements = _base_parameters(p, s)
        requirements.update({key: p[key] for key in PROPOSAL_FIELDS if key in p})
        seed = complete_design(requirements)
        # Complete-design geometry is the baseline; explicit shaft/key inputs
        # still win, and are checked again instead of retaining saved approvals.
        merged_request = {**request, "design": {"inputs": {**seed["inputs"], **{key: value for key, value in p.items() if key in SEGMENTS or key in INPUT_EXTRAS}},
                                                "selections": {**seed["selections"], **{key: value for key, value in s.items() if key in SELECTION_EXTRAS}}}, "parameters": {}}
        design = recalculate(merged_request, seed=seed)
        design["automatic_selection"] = seed["automatic_selection"]
        design["assumptions"] = seed["assumptions"] + design["assumptions"]
    else:
        design = recalculate(request)
    result = {"ok": True, "design": design, "calculation_steps": design["calculation_steps"]}
    if action in ("export", "report") or request.get("output_dir"):
        output = Path(request.get("output_dir") or PLUGIN / "output/designs" / datetime.now().strftime("%Y%m%d_%H%M%S_%f")).resolve()
        output.mkdir(parents=True, exist_ok=True)
        if action == "export":
            destination = output / "model_parameters.json"
            environment = _environment(design)
            with tempfile.TemporaryDirectory(prefix="reducer-native-") as temporary:
                candidate = Path(temporary) / "parameters.json"
                process = subprocess.run([sys.executable, str(PLUGIN / "export_all.py"), "--output", str(candidate)],
                                         cwd=str(PLUGIN), env=environment, capture_output=True, text=True, encoding="utf-8", timeout=180)
                if process.returncode or not candidate.is_file():
                    detail = (process.stderr or process.stdout or "参数文件未生成").strip().splitlines()[-1]
                    raise DesignInputError("原生参数校验失败：" + detail)
                payload = json.loads(candidate.read_text(encoding="utf-8-sig"))
                from solidworks_plugin.design_geometry import adapt_native_parameters
                payload = adapt_native_parameters(payload, design)
                for key in ("design_adjustments", "migration_notes"):
                    if payload.get(key):
                        result[key] = copy.deepcopy(payload[key])
                if payload.get("design_adjustments"):
                    design["native_geometry_adjustments"] = copy.deepcopy(payload["design_adjustments"])
                    design["assumptions"].extend(item["message"] for item in payload["design_adjustments"])
                result["model_parameters_file"] = _write_json(destination, payload)
        if action == "report":
            from report_generator import generate_reducer_report_markdown
            path = output / "reducer_design.md"
            project = request.get("project", {})
            generate_reducer_report_markdown(project, design, path, diagram_image_dir=output / "images", diagram_relative_dir="images")
            appendix = "\n\n## 离线计算过程（当前输入）\n\n" + _report_steps(design)
            path.write_text(path.read_text(encoding="utf-8") + appendix, encoding="utf-8")
            if design.get("axial_layout", {}).get("length_changes") or design.get("axial_layout", {}).get("diameter_changes"):
                warning = "> 手工轴段与当前装配链不同：本说明书的装配图按共享参数链显示；计算响应及项目快照保留手工值。未经该链重算的报告校核不标为满足。\n\n"
                path.write_text(warning + path.read_text(encoding="utf-8"), encoding="utf-8")
            result["report_file"] = str(path)
        snapshot = {"format": "reducer_offline_project", "version": 1, "project": request.get("project", {}), "design": design,
                    **{key: result[key] for key in ("model_parameters_file", "report_file") if key in result}}
        result["project_file"] = _write_json(output / "project.json", snapshot)
    return result


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--request", required=True, type=Path)
    parser.add_argument("--response", required=True, type=Path)
    args = parser.parse_args(argv)
    try:
        request = json.loads(args.request.read_text(encoding="utf-8-sig"))
        response = handle_request(request)
    except Exception as error:
        response = {"ok": False, "error": "离线设计处理失败：" + str(error)}
    _write_json(args.response, response)
    return 0 if response["ok"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
