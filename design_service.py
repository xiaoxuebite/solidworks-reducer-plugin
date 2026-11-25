"""Headless preliminary reducer design, following the existing page formulas."""

import math
from model_generator.gear_operating import DRIVER_ROTATIONS, HELIX_HANDS, AXIAL_SUPPORT_METHOD, gear_operating_conditions, normalize_driver_rotation
from model_generator.housing_params import MODEL_PROFILE, RIB_THICKNESS_SPEC, HOUSING_GEOMETRY_FIELDS, annotate_housing, cast_housing_dimensions
from model_generator.vent_layout import VENT_FIELDS
from model_generator.axial_layout import AXIAL_FIELDS
from model_generator.gear_geometry import transverse_contact_ratio


STANDARD_MODULES = (1, 1.25, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10)
CALCULATION_VERSION = "project_preliminary_v2"
MANUAL_LOAD_FACTORS = {
    "toothFactorH": ("KHα", "齿间载荷参考表"),
    "toothFactorF": ("KFα", "齿间载荷参考表"),
    "contactFaceFactor": ("KHβ", "接触齿向载荷参考表"),
    "bendingFaceFactor": ("KFβ", "弯曲齿向载荷参考图"),
}
FIXED_GEAR_FIELDS = ("z1", "z2", "moduleM", "pinionFaceWidth", "gearFaceWidth")
GEAR_FACTORS = (
    (17, 2.97, 1.52), (18, 2.91, 1.53), (19, 2.85, 1.54),
    (20, 2.80, 1.55), (21, 2.76, 1.56), (22, 2.72, 1.57),
    (23, 2.69, 1.575), (24, 2.65, 1.58), (25, 2.62, 1.59),
    (26, 2.60, 1.595), (27, 2.57, 1.60), (28, 2.55, 1.61),
    (29, 2.53, 1.62), (30, 2.52, 1.625), (35, 2.45, 1.65),
    (40, 2.40, 1.67), (45, 2.35, 1.68), (50, 2.32, 1.70),
    (60, 2.28, 1.73), (70, 2.24, 1.75), (80, 2.22, 1.77),
    (90, 2.20, 1.78), (100, 2.18, 1.79), (150, 2.14, 1.83),
)
PENDING_CHECKS = ["shaft_combined_strength", "key_pressure", "bearing_life", "assembly_geometry"]
PRELIMINARY_NOTE = "初步设计：按项目简化公式完成齿轮强度及轴径初算；系数来源与材料许用值需复核，轴、键、轴承寿命和装配结构尚需校核。"

# Names and units deliberately match the fields already saved by the website.
NUMERIC_FIELDS = {
    "P": (1.2, 0.001, 1000, "kW", "输入功率"),
    "n1": (1450, 1, 30000, "r/min", "输入转速"),
    "eta": (0.96, 0.01, 1, "1", "总效率"),
    "applicationFactor": (1, 0, None, "1", "人工输入的使用系数 KA，默认 1，必须为正数；按原动机与工作机器参考表确定"),
    "z1": (17, 17, 150, "1", "小齿轮齿数"),
    "z2": (42, 26, 1500, "1", "大齿轮齿数"),
    "moduleM": (2, 1, 10, "mm", "法面模数"),
    "pressureAngle": (20, 14, 30, "deg", "法面压力角"),
    "helixAngle": (15, 0, 35, "deg", "螺旋角，直齿轮忽略"),
    "pinionFaceWidth": (32, 1, 500, "mm", "小齿轮齿宽"),
    "gearFaceWidth": (24, 1, 500, "mm", "大齿轮齿宽"),
    "pinionHardness": (240, 1, 1000, "HB", "小齿轮硬度"),
    "gearHardness": (220, 1, 1000, "HB", "大齿轮硬度"),
    "contactAllow": (600, 1, 3000, "MPa", "小齿轮最终许用接触应力，已含寿命及安全修正，需确认材料依据"),
    "gearContactAllow": (600, 1, 3000, "MPa", "大齿轮最终许用接触应力，已含寿命及安全修正"),
    "pinionBendAllow": (180, 1, 1500, "MPa", "小齿轮许用弯曲应力"),
    "gearBendAllow": (180, 1, 1500, "MPa", "大齿轮许用弯曲应力"),
    "highShaftA0": (118, 1, 300, "mm*(r/min/kW)^(1/3)", "高速轴直径估算系数"),
    "lowShaftA0": (118, 1, 300, "mm*(r/min/kW)^(1/3)", "低速轴直径估算系数"),
    "lifeYears": (8, 0.1, 100, "year", "使用年限"),
    "daysPerYear": (300, 1, 366, "day/year", "每年工作天数"),
    "hoursPerDay": (8, 0.1, 24, "h/day", "每天工作小时数"),
    "gearPrecisionGrade": (7, 5, 8, "1", "齿轮精度等级"),
    "dynamicFactorManual": (1.2, 1, 1.8, "1", "动载系数 Kv，默认 1.2，可按教材参考图 人工调整（1-1.8）"),
    **{name: (1, 1, None, "1", f"人工输入的 {symbol}，参考教材{reference}；初始值 1，不自动查表或由其他系数推算")
       for name, (symbol, reference) in MANUAL_LOAD_FACTORS.items()},
    "housingRibThickness": RIB_THICKNESS_SPEC,
    "housingBottomClearance": (12, 0, 10000, "mm", "齿顶到底部净空；联动轴心、箱壁和装配高度，默认值仅用于几何布置"),
    **{name: (default, 0, 10000, "mm", label) for name, (_, default, label) in AXIAL_FIELDS.items()},
    **{name: (default, minimum, 10000, "mm", label)
       for name, (_, default, minimum, label) in VENT_FIELDS.items()},
    **{name: (0, 0, 10000, "mm", label + "；0 自动，正值为独立几何尺寸")
       for name, (_, _, label) in HOUSING_GEOMETRY_FIELDS.items()},
}
CHOICES = {
    "dynamicFactorMode": ("manual",),
    "gearType": ("spur", "helical"),
    "driverRotation": DRIVER_ROTATIONS,
    "helixHand": HELIX_HANDS,
    "gearDesignMethod": ("bending", "contact"),
    "gearLayout": ("symmetrical",),
    "pinionGearMaterial": ("40Cr 调质", "45 正火", "20CrMnTi 渗碳后淬火", "35SiMn 调质", "38SiMnMo 调质"),
    "gearMaterial": ("45 正火", "40Cr 调质", "20CrMnTi 渗碳后淬火", "HT300 正火", "ZG340-640 调质"),
}
DEFAULT_CHOICES = {name: values[0] for name, values in CHOICES.items()}
MATERIAL_LABELS = {
    "forgedSteel": "45 正火", "castSteel": "ZG340-640 调质",
    "castIron": "HT300 正火", "ductileIron": "QT球墨铸铁",
}
MATERIAL_KIND_LABELS = {
    "锻钢": "forgedSteel", "铸钢": "castSteel",
    "灰铸铁": "castIron", "球墨铸铁": "ductileIron",
}
ELASTIC_FACTORS = (
    (189.8, 188.9, 162.0, 181.4), (188.9, 188.0, 161.4, 180.5),
    (162.0, 161.4, 143.7, 156.6), (181.4, 180.5, 156.6, 173.9),
)


class DesignInputError(ValueError):
    pass


def number(value, name, minimum, maximum):
    if isinstance(value, bool):
        raise DesignInputError(f"{name} must be a number, not a boolean")
    try:
        result = float(value)
    except (ValueError, TypeError, OverflowError):
        raise DesignInputError(f"{name} must be a finite number") from None
    if not math.isfinite(result) or result < minimum or (maximum is not None and result > maximum):
        raise DesignInputError(f"{name} must be a finite number >= {minimum}" if maximum is None
                               else f"{name} must be between {minimum} and {maximum}")
    return result


def application_factor(inputs, selections=None):
    """Manual KA wins; migrate only explicit load categories in older projects."""
    if "applicationFactor" in inputs:
        raw = inputs["applicationFactor"]
    elif selections and "applicationFactor" in selections:
        raw = selections["applicationFactor"]
    else:
        legacy = (selections or {}).get("loadType", inputs.get("loadType"))
        legacy_values = {"light": 1, "medium": 1.25, "heavy": 1.5}
        if legacy is not None and (not isinstance(legacy, str) or legacy not in legacy_values):
            raise DesignInputError("旧版 loadType 无效，请直接输入使用系数 applicationFactor。")
        raw = legacy_values.get(legacy, 1)
    value = number(raw, "applicationFactor", 0, None)
    if value <= 0:
        raise DesignInputError("使用系数 KA 必须为大于 0 的有效数值。")
    return value


def get_design_schema():
    return {
        "version": "1.2", "scope": "preliminary_gear_design",
        "calculation_method": {
            "id": CALCULATION_VERSION, "standard_certified": False,
            "module_selection": "Fixed face widths; enumerate standard modules, recompute factors, check both contact and bending. Null minimum means no feasible standard module.",
            "mesh_load": "Common conservative Ft=2*T1/d1; eta only adjusts delivered output power/torque; losses not apportioned.",
            "allowables": "User-supplied FINAL allowable stresses, already including life/safety corrections; contact uses the lower of the two gears.",
            "dynamic_accuracy": "Kv uses the entered dynamicFactorManual (1..1.8), default 1.2 as requested by the user. It may be adjusted using the dynamic-factor chart at v=0..50 m/s. Do not describe the default as an actual chart reading or use an Av formula.",
            "load_distribution": "Four independent manual inputs: toothFactorH (KHalpha), toothFactorF (KFalpha), contactFaceFactor (KHbeta), bendingFaceFactor (KFbeta). Each >=1, initial default 1. Refer to the tooth-load tables and bending face-load chart; never infer KFbeta from KHbeta or claim a default is a chart reading.",
            "factor_table_domain": "Unshifted 20 deg normal pressure angle, virtual teeth 17..150, beta<=30 deg, transverse contact ratio 1..2.5. Other cases cannot pass with current tables.",
            "axial_support": "Automatic for the current opposing end-stop template: +Y thrust loads the upper bearing (calculation left: high segment 2 / low segment 1), -Y loads the lower bearing (right: high 8 / low 4). Spur gears have zero mesh axial load. highAxialSupport / lowAxialSupport are derived outputs; legacy input overrides are ignored. Never divide Fa equally or infer this rule for arbitrary locating/floating structures.",
            "rotation_and_hand": "View from the input-shaft extension (+Y) toward the housing interior (-Y), using the same direction for both shafts. driverRotation selects clockwise_from_input (default) or counterclockwise_from_input; the driven gear rotates oppositely. Do not use the lid-to-base (-Z) view to define shaft rotation. Legacy *_from_top values are normalized to *_from_input. Reversing rotation reverses signed axial forces and bearing load allocation, without changing gear hand or CAD tooth geometry.",
        },
        "parameters": {
            name: {"default": spec[0],
                   **({"exclusiveMinimum": 0} if name == "applicationFactor" else {"minimum": spec[1]}),
                   **({"maximum": spec[2]} if spec[2] is not None else {}),
                   "unit": spec[3], "description": spec[4],
                   "model_applied": name not in {"housingRibThickness", *HOUSING_GEOMETRY_FIELDS},
                   "integer": name in ("z1", "z2", "gearPrecisionGrade")}
            for name, spec in NUMERIC_FIELDS.items()
        },
        "choices": CHOICES, "material_labels": MATERIAL_LABELS,
        "standard_modules": STANDARD_MODULES,
        "limits": {"ratio": [1.5, 10], "center_distance_mm": [50, 450]},
        "required_for_calculation": [],
        "required_for_proposal": ["P", "n1", "target_speed"],
        "proposal_options": {
            "target_speed": {"unit": "r/min", "minimum": 0.1, "maximum": 20000},
            "speed_tolerance": {"unit": "fraction", "default": 0.02, "minimum": 0, "maximum": 0.2},
            "max_center_distance": {"unit": "mm", "default": 450, "minimum": 50, "maximum": 450},
            "max_candidates": {"default": 3, "minimum": 1, "maximum": 5},
        },
        "pending_checks": PENDING_CHECKS, "note": PRELIMINARY_NOTE,
        "model_details": {"editable": [], "revision_tool": None,
                          "scope": "2026-10-14 baseline with aligned bores, height/vent placement and axial layout (housingAxialWidth, housingGearEndClearance). Spacer fixed at 10 mm; shaft lengths follow bearing/cover locations. High-speed segments 2/8 and low-speed segments 1/4 use the selected bearing bore automatically and are not independently editable; the driven gear bore uses low-speed segment 2. Other shaft diameters and gear widths remain independently specified. Use calculation/new workflow to regenerate; in-place revision and other historical housing overrides unavailable.",
                          "profile": dict(MODEL_PROFILE)},
        "automatic_workflow": {"tool": "start_design_workflow", "required": ["P", "n1", "target_speed"],
                               "optional_fixed_parameters": FIXED_GEAR_FIELDS,
                               "target_speed_note": "May be derived from n1 when both z1 and z2 are fixed",
                               "outputs": ["new_project", "STEP", "GLB", "Markdown_ZIP"],
                               "checks": ["gears", "gear_section_shaft_strength", "key_pressure", "bearing_life", "basic_dimensions"],
                               "pending": ["material_allowables_review", "whole_shaft_fatigue", "assembly_interference", "manufacturing_tolerances"],
                               "requires": "Explicit user request to generate artifacts"},
    }


def catalog_material_kind(value):
    name, separator, suffix = value.rpartition("（")
    if separator and 0 < len(name.strip()) <= 80 and suffix.endswith("）"):
        return MATERIAL_KIND_LABELS.get(suffix[:-1])
    return None


def _material_kind(value):
    if value in MATERIAL_LABELS:
        return value
    catalog_kind = catalog_material_kind(value)
    if catalog_kind:
        return catalog_kind
    # Accept the existing page's Chinese material labels as well as API enums.
    for marker, kind in (("HT", "castIron"), ("灰铸铁", "castIron"),
                         ("QT", "ductileIron"), ("球墨", "ductileIron"),
                         ("ZG", "castSteel"), ("铸钢", "castSteel")):
        if marker in value:
            return kind
    if ("钢" in value or value in ("45", "40Cr", "20CrMnTi")
            or value in CHOICES["pinionGearMaterial"] or value in CHOICES["gearMaterial"]):
        return "forgedSteel"
    raise DesignInputError(f"Unknown gear material: {value}")


def normalize_parameters(parameters):
    if not isinstance(parameters, dict):
        raise DesignInputError("parameters must be an object")
    # Accept and discard the obsolete precision parameter when reading older data.
    unknown = set(parameters) - NUMERIC_FIELDS.keys() - CHOICES.keys() - {"dynamicAccuracyGrade", "loadType", "highAxialSupport", "lowAxialSupport"}
    if unknown:
        raise DesignInputError(f"Unknown parameters: {', '.join(sorted(unknown))}")
    result = {}
    for name, spec in NUMERIC_FIELDS.items():
        value = application_factor(parameters) if name == "applicationFactor" else number(parameters.get(name, spec[0]), name, spec[1], spec[2])
        if name in ("z1", "z2", "gearPrecisionGrade"):
            if not value.is_integer():
                raise DesignInputError(f"{name} must be an integer")
            value = int(value)
        result[name] = value
    if "gearContactAllow" not in parameters:
        result["gearContactAllow"] = result["contactAllow"]
    for name, options in CHOICES.items():
        value = parameters.get(name, DEFAULT_CHOICES[name])
        if not isinstance(value, str):
            raise DesignInputError(f"{name} must be a string")
        if name == 'driverRotation':
            try:
                value = normalize_driver_rotation(value)
            except ValueError as error:
                raise DesignInputError(str(error)) from error
        if name == "dynamicFactorMode" and value != "manual":
            raise DesignInputError("动载系数 Kv 仅支持教材查图，请输入人工查取的 dynamicFactorManual。")
        if name in ("pinionGearMaterial", "gearMaterial"):
            value = MATERIAL_LABELS.get(value, value)
            if catalog_material_kind(value):
                result[name] = value
                continue
        if value not in options:
            raise DesignInputError(f"{name} must be one of {options}")
        result[name] = value
    if result["moduleM"] not in STANDARD_MODULES:
        raise DesignInputError("moduleM must be a standard module")
    if not 1.5 <= result["z2"] / result["z1"] <= 10:
        raise DesignInputError("gear ratio must be between 1.5 and 10")
    if result["gearType"] == "helical" and result["helixAngle"] < 8:
        raise DesignInputError("helical gears require helixAngle between 8 and 35 deg")
    if result["gearType"] == "spur":
        result["helixAngle"] = 0
    return result


def _interpolate(z, column):
    if z <= GEAR_FACTORS[0][0]:
        return GEAR_FACTORS[0][column]
    for previous, following in zip(GEAR_FACTORS, GEAR_FACTORS[1:]):
        if z <= following[0]:
            fraction = (z - previous[0]) / (following[0] - previous[0])
            return previous[column] + fraction * (following[column] - previous[column])
    return GEAR_FACTORS[-1][column]


def _factors(p, module, ft):
    beta = math.radians(p["helixAngle"]) if p["gearType"] == "helical" else 0
    cos_beta = math.cos(beta)
    alpha = math.atan(math.tan(math.radians(p["pressureAngle"])) / cos_beta)
    z1, z2 = p["z1"], p["z2"]
    width = min(p["pinionFaceWidth"], p["gearFaceWidth"])
    d1 = module * z1 / cos_beta
    overlap = transverse_contact_ratio(module, z1, z2, p["pressureAngle"], p["helixAngle"])
    velocity = math.pi * d1 * p["n1"] / 60000
    dynamic = p["dynamicFactorManual"]
    velocity_limit = 50
    application = p["applicationFactor"]
    kinds = tuple(MATERIAL_LABELS)
    elastic = ELASTIC_FACTORS[kinds.index(_material_kind(p["pinionGearMaterial"]))][kinds.index(_material_kind(p["gearMaterial"]))]
    beta_factor = max(1 - min(width * math.sin(beta) / (math.pi * module), 1)
                      * math.degrees(beta) / 120, 0.75) if beta else 1
    return {
        "contactRatio": overlap,
        "zoneFactor": math.sqrt(2 / (math.cos(alpha) * math.sin(alpha))),
        "elasticFactor": elastic,
        "contactOverlap": math.sqrt(max((4 - overlap) / 3, 0.01)) * math.sqrt(cos_beta),
        "bendingOverlap": 0.25 + 0.75 / overlap,
        "helixBendingFactor": beta_factor, "applicationFactor": application,
        "dynamicFactor": dynamic, "contactLoadFactor": application * dynamic * p["toothFactorH"] * p["contactFaceFactor"],
        "bendingLoadFactor": application * dynamic * p["toothFactorF"] * p["bendingFaceFactor"],
        "pitchLineVelocity": velocity, "dynamicVelocityLimit": velocity_limit,
        "contactOverlapOnly": math.sqrt(max((4 - overlap) / 3, 0.01)),
        "helixContactFactor": math.sqrt(cos_beta),
        **{name: p[name] for name in MANUAL_LOAD_FACTORS},
    }


def _gear_state(p, module):
    cos_beta = math.cos(math.radians(p["helixAngle"]))
    ratio = p["z2"] / p["z1"]
    d1, d2 = (module * p[z] / cos_beta for z in ("z1", "z2"))
    # One mesh load for gears, both shafts and bearings. Losses are not apportioned;
    # use input torque for a conservative mesh load, eta only for delivered output.
    ft = 2 * (9550000 * p["P"] / p["n1"]) / d1
    f = _factors(p, module, ft)
    width = min(p["pinionFaceWidth"], p["gearFaceWidth"])
    torque = 9550000 * p["P"] / p["n1"]
    phi_d = width / d1
    # Textbook (10-10), external mesh: u+1. Helical correction stays in contactOverlap.
    contact = f["zoneFactor"] * f["elasticFactor"] * f["contactOverlap"] * math.sqrt(
        2 * f["contactLoadFactor"] * torque / (phi_d * d1 ** 3) * (ratio + 1) / ratio)
    bends = [f["bendingLoadFactor"] * ft * _interpolate(p[z] / cos_beta ** 3, 1)
             * _interpolate(p[z] / cos_beta ** 3, 2) * f["bendingOverlap"] * f["helixBendingFactor"]
             / (width * module) for z in ("z1", "z2")]
    return {"d1": d1, "d2": d2, "ft": ft, "factors": f, "contact": contact, "bends": bends}


def _module_selection(p):
    """Re-evaluate all factors for each standard module at the fixed input widths."""
    states = {module: _gear_state(p, module) for module in STANDARD_MODULES}
    contact_limit = min(p["contactAllow"], p["gearContactAllow"])
    bending_ok = lambda state: all(stress <= p[allow] for stress, allow in
                                  zip(state["bends"], ("pinionBendAllow", "gearBendAllow")))
    contact_ok = lambda state: state["contact"] <= contact_limit
    bending_min = next((m for m, state in states.items() if bending_ok(state)), None)
    contact_min = next((m for m, state in states.items() if contact_ok(state)), None)
    feasible = [m for m, state in states.items() if bending_ok(state) and contact_ok(state)]
    return states, feasible, bending_min, contact_min


def _method_errors(p, f):
    errors = []
    if f["pitchLineVelocity"] > f["dynamicVelocityLimit"]:
        errors.append("节线速度超出教材图示的 0-50 m/s 范围，请另查适用的 Kv 数据。")
    if any(not GEAR_FACTORS[0][0] <= p[z] / math.cos(math.radians(p["helixAngle"])) ** 3 <= GEAR_FACTORS[-1][0]
           for z in ("z1", "z2")):
        errors.append("当量齿数超出 YFa/YSa 内置数据的 17-150 范围，端点估算不得用于通过判定。")
    if not 1 <= f["contactRatio"] <= 2.5:
        errors.append("端面重合度超出当前简化系数模型的 1-2.5 范围。")
    if p["pressureAngle"] != 20 or p["helixAngle"] > 30:
        errors.append("内置齿形系数仅按 20°、无变位齿轮估算；螺旋角大于 30° 需另行复核。")
    return errors


def coefficient_review(p):
    return {
        "method": CALCULATION_VERSION, "standard_certified": False,
        "axial_load_method": AXIAL_SUPPORT_METHOD,
        "load_basis": "Ft=2*T1/d1；总效率仅修正输出功率和转矩，啮合载荷按输入侧保守估算，未细分损失。",
        "face_width_basis": "接触和两轮弯曲均按 min(b1,b2) 的共同受载齿宽保守估算，未计错位和修形。",
        "allowable_basis": "人工输入的最终许用值，应已包含材料、热处理、寿命和安全修正；系统不自动推导或重复修正。",
        "factor_sources": {
            "KA": "人工输入的使用系数，按原动机与工作机器参考表确定；默认 1",
            "Kv": "采用输入的 Kv，默认 1.2；可按教材参考图 调整",
            **{symbol: f"独立人工输入，参考教材{reference}；采用值 {p[name]:g}，未填时初始值 1，不自动查图表"
               for name, (symbol, reference) in MANUAL_LOAD_FACTORS.items()},
            "YFa_YSa": "项目原有无变位 20° 齿形插值表，17-150，原表/刀具条件待复核",
            "ZE": "材料组合表，单位 sqrt(MPa)，材料弹性参数来源待复核",
            "Zepsilon_Zbeta_Yepsilon_Ybeta_ZH": "保留项目简化体系，已修正实际几何输入；完整斜齿分段定义待统一教材复核",
            "bearings": "项目内置通用型号额定值，未指定厂家版本，不能视为供应商批准数据",
        },
        "review_required": ["coefficient_source_review", "material_allowables_review", "tooth_geometry_review"],
    }


def calculate_design(parameters, auto_select_module=True):
    """Calculate gear checks. Never represents incomplete checks as full approval."""
    if not isinstance(auto_select_module, bool):
        raise DesignInputError("auto_select_module must be a boolean")
    p = normalize_parameters(parameters)
    beta = math.radians(p["helixAngle"]) if p["gearType"] == "helical" else 0
    cos_beta = math.cos(beta)
    ratio = p["z2"] / p["z1"]
    speed = p["n1"] / ratio
    power = p["P"] * p["eta"]
    torque = 9550000 * power / speed
    states, feasible, bending_min, contact_min = _module_selection(p)
    required = feasible[0] if feasible else None
    module = p["moduleM"]
    if auto_select_module and module not in feasible:
        module = next((m for m in feasible if m >= module), STANDARD_MODULES[-1])
    p["moduleM"] = module
    state = states[module]
    d1, d2, ft, f, contact, bends = (state[key] for key in ("d1", "d2", "ft", "factors", "contact", "bends"))
    operating = gear_operating_conditions(p, p, ft)
    contact_d = contact_min * p["z1"] / cos_beta if contact_min is not None else None
    method_errors = _method_errors(p, f)
    contact_limit = min(p["contactAllow"], p["gearContactAllow"])
    results = {
        "outPower": power, "outSpeed": speed, "outTorque": torque, "ratio": ratio,
        "dPinion": d1, "dGear": d2, "centerDistance": (d1 + d2) / 2,
        "tangentialForce": ft, "inputTorque": 9550000 * p["P"] / p["n1"], "contactStress": contact,
        "effectiveFaceWidth": min(p["pinionFaceWidth"], p["gearFaceWidth"]),
        "contactAllowEffective": contact_limit,
        "pinionBendStress": bends[0], "gearBendStress": bends[1], "bendStress": max(bends),
        "moduleMin": required, "bendingModuleMin": bending_min, "contactModuleMin": contact_min,
        "contactDesignDiameterMin": contact_d,
        "highMinDiameter": p["highShaftA0"] * (p["P"] / p["n1"]) ** (1 / 3),
        "lowMinDiameter": p["lowShaftA0"] * (power / speed) ** (1 / 3),
        "requiredBearingLife": p["lifeYears"] * p["daysPerYear"] * p["hoursPerDay"],
        "zoneFactor": f["zoneFactor"], "elasticFactor": f["elasticFactor"],
        "contactOverlapFactorResult": f["contactOverlap"], "bendingOverlapFactor": f["bendingOverlap"],
        "contactRatio": f["contactRatio"], "contactOverlapOnly": f["contactOverlapOnly"],
        "helixContactFactor": f["helixContactFactor"],
        "contactLoadFactor": f["contactLoadFactor"], "bendingLoadFactor": f["bendingLoadFactor"],
        "dynamicFactor": f["dynamicFactor"], "pitchLineVelocity": f["pitchLineVelocity"],
        "pinionAxialForce": operating["pinionAxialForce"], "gearAxialForce": operating["gearAxialForce"],
    }
    checks = {
        "contact_strength": {"value": contact, "limit": contact_limit, "passed": contact <= contact_limit},
        "pinion_bending": {"value": bends[0], "limit": p["pinionBendAllow"], "passed": bends[0] <= p["pinionBendAllow"]},
        "gear_bending": {"value": bends[1], "limit": p["gearBendAllow"], "passed": bends[1] <= p["gearBendAllow"]},
        "center_distance": {"value": results["centerDistance"], "limits": [50, 450], "passed": 50 <= results["centerDistance"] <= 450},
        "design_module": {"value": module, "minimum": required, "passed": module in feasible},
        "coefficient_domain": {"passed": not method_errors, "messages": method_errors},
    }
    selections = {name: p[name] for name in CHOICES}
    selections.update({name: operating[name] for name in ('highAxialSupport', 'lowAxialSupport')})
    selections["gearPrecisionGrade"] = p["gearPrecisionGrade"]
    design = {
        "inputs": {name: p[name] for name in NUMERIC_FIELDS if name != "gearPrecisionGrade"},
        "selections": selections, "results": results, "factors": f, "checks": checks,
        "gear_operating": operating,
        "gear_checks_passed": all(check["passed"] for check in checks.values()),
        "fully_validated": False, "pending_checks": list(PENDING_CHECKS) + coefficient_review(p)["review_required"],
        "calculation_review": coefficient_review(p), "calculation_warnings": method_errors,
        "module_selection": {"method": "fixed_width_standard_enumeration", "feasible_modules": feasible,
                             "no_feasible_module": not feasible},
        "designConclusion": PRELIMINARY_NOTE,
        "assumptions": [f"{name}={p[name]} ({NUMERIC_FIELDS[name][3]})"
                        for name in NUMERIC_FIELDS if name not in parameters]
                       + [f"{name}={p[name]}" for name in CHOICES if name not in parameters],
        "adjustmentAdvice": method_errors + [f"{name}: adjust parameters and recalculate"
                             for name, check in checks.items() if not check["passed"]],
    }
    for field, check in (("contactConclusion", "contact_strength"),
                         ("pinionBendConclusion", "pinion_bending"),
                         ("gearBendConclusion", "gear_bending")):
        design[field] = "满足" if checks[check]["passed"] else "需调整"
    for field in ("highShaftConclusion", "shaftConclusion", "highKeyPressConclusion",
                  "lowGearKeyPressConclusion", "lowOuterKeyPressConclusion",
                  "highBearingConclusion", "lowBearingConclusion"):
        design[field] = "未校核"
    if "housingRibThickness" not in parameters:
        design["inputs"]["housingRibThickness"] = cast_housing_dimensions(results["centerDistance"]).lower_rib
    try:
        annotate_housing(design)
    except ValueError as error:
        raise DesignInputError(str(error)) from error
    return design


def propose_design(requirements, *, fixed_parameters=None):
    """Bounded deterministic enumeration; no model-generated formulas or ratings."""
    if not isinstance(requirements, dict):
        raise DesignInputError("requirements must be an object")
    for name in ("P", "n1", "target_speed"):
        if name not in requirements:
            raise DesignInputError(f"Missing required parameter: {name}")
    options = {"target_speed", "speed_tolerance", "max_center_distance", "max_candidates"}
    unknown = set(requirements) - NUMERIC_FIELDS.keys() - CHOICES.keys() - options - {"dynamicAccuracyGrade", "loadType", "highAxialSupport", "lowAxialSupport"}
    if unknown:
        raise DesignInputError(f"Unknown requirements: {', '.join(sorted(unknown))}")
    # These fields are chosen by the search. Reject contradictory input explicitly.
    if set(requirements) & set(FIXED_GEAR_FIELDS):
        raise DesignInputError("Use calculate_design for fixed teeth, module or widths")
    fixed = {}
    if fixed_parameters is not None:
        if not isinstance(fixed_parameters, dict) or set(fixed_parameters) - set(FIXED_GEAR_FIELDS):
            raise DesignInputError("固定参数只能包含 z1、z2、moduleM、pinionFaceWidth、gearFaceWidth。")
        for name, value in fixed_parameters.items():
            _, minimum, maximum, _, label = NUMERIC_FIELDS[name]
            try:
                parsed = number(value, name, minimum, maximum)
            except DesignInputError:
                raise DesignInputError(f"固定参数{label}（{name}）必须为 {minimum}-{maximum} 范围内的有效数值。") from None
            if name in ("z1", "z2"):
                if not parsed.is_integer():
                    raise DesignInputError(f"固定齿数 {name} 必须是整数。")
                parsed = int(parsed)
            fixed[name] = parsed
        if "moduleM" in fixed and fixed["moduleM"] not in STANDARD_MODULES:
            raise DesignInputError("固定模数必须选用系统支持的标准模数。")
    target = number(requirements["target_speed"], "target_speed", 0.1, 20000)
    tolerance = number(requirements.get("speed_tolerance", 0.02), "speed_tolerance", 0, 0.2)
    center_limit = number(requirements.get("max_center_distance", 450), "max_center_distance", 50, 450)
    count = number(requirements.get("max_candidates", 3), "max_candidates", 1, 5)
    if not count.is_integer():
        raise DesignInputError("max_candidates must be an integer")
    base = {name: value for name, value in requirements.items() if name not in options}
    p = normalize_parameters(base)
    target_ratio = p["n1"] / target
    if not 1.5 <= target_ratio <= 10:
        raise DesignInputError("Target ratio is outside the single-stage range 1.5-10")
    candidates, evaluated = [], 0
    for z1 in (fixed["z1"],) if "z1" in fixed else range(17, 41):
        ideal = z1 * target_ratio
        for z2 in (fixed["z2"],) if "z2" in fixed else sorted({math.floor(ideal), math.ceil(ideal)}):
            if not NUMERIC_FIELDS["z2"][1] <= z2 <= NUMERIC_FIELDS["z2"][2]:
                continue
            ratio = z2 / z1
            if not 1.5 <= ratio <= 10:
                continue
            error = abs(p["n1"] / ratio - target) / target
            if error > tolerance + 1e-12:
                continue
            for module in (fixed["moduleM"],) if "moduleM" in fixed else STANDARD_MODULES:
                beta = math.radians(p["helixAngle"]) if p["gearType"] == "helical" else 0
                center = module * (z1 + z2) / (2 * math.cos(beta))
                if not 50 <= center <= center_limit:
                    continue
                widths = (fixed["gearFaceWidth"],) if "gearFaceWidth" in fixed else (
                    math.ceil(max(10, factor * module * z1 / math.cos(beta))) for factor in (0.3, 0.5, 0.7, 1.0))
                for width in widths:
                    pinion_width = fixed.get("pinionFaceWidth", width + 5)
                    if max(width, pinion_width) > 500:
                        continue
                    evaluated += 1
                    try:
                        design = calculate_design({**base, "z1": z1, "z2": z2, "moduleM": module,
                                                   "gearFaceWidth": width, "pinionFaceWidth": pinion_width},
                                                  auto_select_module=False)
                    except DesignInputError:
                        continue
                    if design["gear_checks_passed"]:
                        design["speed_error_fraction"] = error
                        design["selection_reason"] = "满足目标转速误差和现有齿轮校核，按中心距及齿宽排序。"
                        candidates.append(design)
                        break
    candidates.sort(key=lambda item: (item["results"]["centerDistance"],
                                     item["inputs"]["gearFaceWidth"], item["speed_error_fraction"]))
    return {"status": "candidates_found" if candidates else "no_feasible_gear_design",
            "candidates": candidates[:int(count)], "evaluated_candidates": evaluated,
            "search_space": {"z1": [fixed["z1"], fixed["z1"]] if "z1" in fixed else [17, 40],
                             "standard_modules": (fixed["moduleM"],) if "moduleM" in fixed else STANDARD_MODULES,
                             "width_factors": [0.3, 0.5, 0.7, 1.0], "fixed_parameters": fixed},
            "fully_validated": False, "pending_checks": list(PENDING_CHECKS),
            "note": PRELIMINARY_NOTE,
            "next_action": "Review assumptions and complete shaft/key/bearing checks" if candidates
                           else "Relax center-distance/speed constraints or review material allowables"}
