"""Geometry-only housing settings shared by design, HTTP and CAD runtimes."""

import math
from dataclasses import asdict, dataclass
try:
    from .housing_height import design_height
    from .vent_layout import cover_vent_layout, VENT_FIELDS
    from .axial_layout import axial_layout, AXIAL_FIELDS
    from .housing_fasteners import fastener_plan, inspection_cover_reference
except ImportError:
    from housing_height import design_height
    from vent_layout import cover_vent_layout, VENT_FIELDS
    from axial_layout import axial_layout, AXIAL_FIELDS
    from housing_fasteners import fastener_plan, inspection_cover_reference

RIB_THICKNESS_SPEC = (7, .01, 10000, "mm", "下箱体轴承座加强肋板厚度；只改变几何，未作强度校核")

# Zero selects the reference default; positive values are independent model inputs.
HOUSING_GEOMETRY_FIELDS = {
    "wallThickness": ("REDUCER_HOUSING_WALL_THICKNESS", "lower_wall", "箱座壁厚"),
    "coverWallThickness": ("REDUCER_COVER_WALL_THICKNESS", "upper_wall", "箱盖壁厚"),
    "housingRadialClearance": ("REDUCER_HOUSING_RADIAL_CLEARANCE", "radial_clearance", "径向间隙"),
    "housingAxialClearance": ("REDUCER_HOUSING_AXIAL_CLEARANCE", "axial_clearance", "端面间隙"),
    "housingCavityDepth": ("REDUCER_HOUSING_CAVITY_DEPTH", None, "内腔轴向宽度"),
}

MODEL_PROFILE = {"id": "backup_20261014_214326", "label": "2026-10-14 备份建模逻辑",
                 "source": "backups/project_backup_20261014_214326（加MCP前）/model_generator",
                 "editable_housing_details": [], "automatic_assembly_check": False,
                 "alignment_revision": 1, "height_revision": 1,
                 "height_parameters": ["housingBottomClearance"],
                 "vent_revision": 1, "vent_parameters": list(VENT_FIELDS),
                 "axial_revision": 1, "axial_parameters": list(AXIAL_FIELDS), "fixed_sleeve_length": 10}


def geometry_settings(values):
    settings = {}
    for name, (_, _, label) in HOUSING_GEOMETRY_FIELDS.items():
        if name not in values:
            continue
        value = values[name]
        try:
            parsed = float(value)
        except (TypeError, ValueError, OverflowError):
            parsed = float("nan")
        if isinstance(value, bool) or not math.isfinite(parsed) or parsed < 0:
            raise ValueError(f"{label}必须为非负有限数值；0 表示自动尺寸")
        settings[name] = parsed
    return settings


@dataclass(frozen=True)
class CastHousingDimensions:
    center_distance: float
    lower_wall: float
    upper_wall: float
    lower_flange: float
    upper_flange: float
    base_flange: float
    lower_rib: float
    upper_rib: float
    radial_clearance: float
    axial_clearance: float
    anchor_diameter_raw: float
    anchor_count: int

    def to_dict(self):
        def reference(factory):
            try:
                return factory(self.center_distance)
            except ValueError as error:
                return {"available": False, "reason": str(error)}
        return {**asdict(self), "units": "mm", "source": "User reference, Table 11-1, printed p.164",
                "scope": "single-stage cast-iron cylindrical gear reducer; empirical sizing, not certification",
                "rounding": "thicknesses rounded upward to whole mm; strict clearances rounded to next whole mm",
                "formulae": {"lower_wall": "max(8, 0.025*a+1)", "upper_wall": "max(8, 0.02*a+1)",
                             "lower_flange": "1.5*delta", "upper_flange": "1.5*delta1", "base_flange": "2.5*delta",
                             "lower_rib": "approximately 0.85*delta", "upper_rib": "approximately 0.85*delta1",
                             "radial_clearance": ">1.2*delta", "axial_clearance": ">delta",
                             "anchor_diameter_raw": "0.036*a+12"},
                "fasteners": reference(fastener_plan),
                "inspection_cover_reference": reference(inspection_cover_reference),
                "pending": ["actual wrench access and bolt strength", "Table 1-38 casting transitions",
                            "inspection opening and accessories layout", "casting stiffness, fits and sealing"]}


def cast_housing_dimensions(center_distance):
    if isinstance(center_distance, bool) or not math.isfinite(center_distance) or center_distance <= 0:
        raise ValueError("Housing center distance must be finite and positive")
    lower = math.ceil(max(8, .025 * center_distance + 1))
    upper = math.ceil(max(8, .02 * center_distance + 1))
    return CastHousingDimensions(center_distance, lower, upper, math.ceil(1.5 * lower), math.ceil(1.5 * upper),
                                 math.ceil(2.5 * lower), math.ceil(.85 * lower), math.ceil(.85 * upper),
                                 math.floor(1.2 * lower) + 1, lower + 1, .036 * center_distance + 12,
                                 4 if center_distance <= 250 else 6 if center_distance <= 500 else 8)


def rib_thickness(value=RIB_THICKNESS_SPEC[0]):
    try:
        result = float(value)
    except (TypeError, ValueError, OverflowError):
        result = float("nan")
    if isinstance(value, bool) or not math.isfinite(result) or result <= 0:
        raise ValueError("下箱体肋板厚度必须为大于 0 的有限数值；未作强度校核。")
    return result


def annotate_housing(design):
    thickness = rib_thickness(design["inputs"].get("housingRibThickness", RIB_THICKNESS_SPEC[0]))
    design["inputs"]["housingRibThickness"] = thickness
    prefix = "下箱体加强肋板厚度："
    design["assumptions"] = [text for text in design.get("assumptions", []) if not text.startswith(prefix)]
    design["assumptions"].append(f"{prefix}历史输入记录 {thickness:g} mm；当前恢复的备份模板肋厚固定为 6 mm，历史箱体控制参数未接入模型。")
    dimensions = cast_housing_dimensions(design["results"]["centerDistance"])
    design["housing_dimensions"] = dimensions.to_dict()
    design["housing_dimensions"]["actual_lower_rib"] = 6
    design["housing_dimensions"]["applied_to_model"] = False
    design["model_generation"] = dict(MODEL_PROFILE)
    design["housing_height"] = design_height(design["inputs"], design.get("selections", {}))
    design["axial_layout"] = axial_layout(design["inputs"], design.get("selections", {}))
    design["vent_mount"] = cover_vent_layout(design["inputs"], design.get("selections", {}))
    design["housing_dimensions"]["geometry_overrides"] = geometry_settings(design["inputs"])
    design["housing_dimensions"]["warnings"] = ["表格经验尺寸仅供参考，未用于当前备份模板建模。"]
    design["pending_checks"] = list(dict.fromkeys([*design.get("pending_checks", []), "housing_rib_strength_and_castability"]))
    design["fully_validated"] = False
