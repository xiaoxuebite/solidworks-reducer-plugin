# -*- coding: utf-8 -*-
"""Shared reducer shaft sizing rules.

The original part scripts used independent fixed dimensions.  This module
keeps the shaft, bearings, gear bores, covers, adjusting rings and sleeve on
the same dimensional contract.
"""
from __future__ import annotations

import os
import re
import math


BEARING_CATALOG = {
    "6000": {"inner_diameter": 10, "outer_diameter": 26, "width": 8, "dynamic_load": 4.58, "grease_speed": 20000, "oil_speed": 28000},
    "6001": {"inner_diameter": 12, "outer_diameter": 28, "width": 8, "dynamic_load": 5.10, "grease_speed": 19000, "oil_speed": 26000},
    "6002": {"inner_diameter": 15, "outer_diameter": 32, "width": 9, "dynamic_load": 5.58, "grease_speed": 18000, "oil_speed": 24000},
    "6003": {"inner_diameter": 17, "outer_diameter": 35, "width": 10, "dynamic_load": 6.00, "grease_speed": 17000, "oil_speed": 22000},
    "6004": {"inner_diameter": 20, "outer_diameter": 42, "width": 12, "dynamic_load": 9.38, "grease_speed": 15000, "oil_speed": 19000},
    "6005": {"inner_diameter": 25, "outer_diameter": 47, "width": 12, "dynamic_load": 10.0, "grease_speed": 13000, "oil_speed": 17000},
    "6006": {"inner_diameter": 30, "outer_diameter": 55, "width": 13, "dynamic_load": 13.2, "grease_speed": 10000, "oil_speed": 14000},
    "6007": {"inner_diameter": 35, "outer_diameter": 62, "width": 14, "dynamic_load": 16.2, "grease_speed": 9000, "oil_speed": 12000},
    "6008": {"inner_diameter": 40, "outer_diameter": 68, "width": 15, "dynamic_load": 17.0, "grease_speed": 8500, "oil_speed": 11000},
    "6009": {"inner_diameter": 45, "outer_diameter": 75, "width": 16, "dynamic_load": 21.0, "grease_speed": 8000, "oil_speed": 10000},
    "6010": {"inner_diameter": 50, "outer_diameter": 80, "width": 16, "dynamic_load": 22.4, "grease_speed": 7000, "oil_speed": 9000},
    "6011": {"inner_diameter": 55, "outer_diameter": 90, "width": 18, "dynamic_load": 30.2, "grease_speed": 6300, "oil_speed": 8000},
    "6012": {"inner_diameter": 60, "outer_diameter": 95, "width": 18, "dynamic_load": 31.5, "grease_speed": 6000, "oil_speed": 7500},
    "6013": {"inner_diameter": 65, "outer_diameter": 100, "width": 18, "dynamic_load": 32.0, "grease_speed": 5600, "oil_speed": 7000},
    "6014": {"inner_diameter": 70, "outer_diameter": 110, "width": 20, "dynamic_load": 38.5, "grease_speed": 5300, "oil_speed": 6700},
    "6015": {"inner_diameter": 75, "outer_diameter": 115, "width": 20, "dynamic_load": 40.2, "grease_speed": 5000, "oil_speed": 6300},
    "6016": {"inner_diameter": 80, "outer_diameter": 125, "width": 22, "dynamic_load": 47.5, "grease_speed": 4800, "oil_speed": 6000},
    "6017": {"inner_diameter": 85, "outer_diameter": 130, "width": 22, "dynamic_load": 50.8, "grease_speed": 4500, "oil_speed": 5600},
    "6018": {"inner_diameter": 90, "outer_diameter": 140, "width": 24, "dynamic_load": 58.0, "grease_speed": 4300, "oil_speed": 5300},
    "6019": {"inner_diameter": 95, "outer_diameter": 145, "width": 24, "dynamic_load": 57.8, "grease_speed": 4000, "oil_speed": 5000},
    "6020": {"inner_diameter": 100, "outer_diameter": 150, "width": 24, "dynamic_load": 64.5, "grease_speed": 3800, "oil_speed": 4800},
    "6200": {"inner_diameter": 10, "outer_diameter": 30, "width": 9, "dynamic_load": 5.10, "grease_speed": 19000, "oil_speed": 26000},
    "6201": {"inner_diameter": 12, "outer_diameter": 32, "width": 10, "dynamic_load": 6.82, "grease_speed": 18000, "oil_speed": 24000},
    "6202": {"inner_diameter": 15, "outer_diameter": 35, "width": 11, "dynamic_load": 7.65, "grease_speed": 17000, "oil_speed": 22000},
    "6203": {"inner_diameter": 17, "outer_diameter": 40, "width": 12, "dynamic_load": 9.58, "grease_speed": 16000, "oil_speed": 20000},
    "6204": {"inner_diameter": 20, "outer_diameter": 47, "width": 14, "dynamic_load": 12.8, "grease_speed": 14000, "oil_speed": 18000},
    "6205": {"inner_diameter": 25, "outer_diameter": 52, "width": 15, "dynamic_load": 14.0, "grease_speed": 12000, "oil_speed": 16000},
    "6206": {"inner_diameter": 30, "outer_diameter": 62, "width": 16, "dynamic_load": 19.5, "grease_speed": 9500, "oil_speed": 13000},
    "6207": {"inner_diameter": 35, "outer_diameter": 72, "width": 17, "dynamic_load": 25.5, "grease_speed": 8500, "oil_speed": 11000},
    "6208": {"inner_diameter": 40, "outer_diameter": 80, "width": 18, "dynamic_load": 29.5, "grease_speed": 8000, "oil_speed": 10000},
    "6209": {"inner_diameter": 45, "outer_diameter": 85, "width": 19, "dynamic_load": 31.5, "grease_speed": 7000, "oil_speed": 9000},
    "6210": {"inner_diameter": 50, "outer_diameter": 90, "width": 20, "dynamic_load": 35.0, "grease_speed": 6700, "oil_speed": 8500},
    "6211": {"inner_diameter": 55, "outer_diameter": 100, "width": 21, "dynamic_load": 43.2, "grease_speed": 6000, "oil_speed": 7500},
    "6212": {"inner_diameter": 60, "outer_diameter": 110, "width": 22, "dynamic_load": 47.8, "grease_speed": 5600, "oil_speed": 7000},
    "6213": {"inner_diameter": 65, "outer_diameter": 120, "width": 23, "dynamic_load": 57.2, "grease_speed": 5000, "oil_speed": 6300},
    "6214": {"inner_diameter": 70, "outer_diameter": 125, "width": 24, "dynamic_load": 60.8, "grease_speed": 4800, "oil_speed": 6000},
    "6215": {"inner_diameter": 75, "outer_diameter": 130, "width": 25, "dynamic_load": 66.0, "grease_speed": 4500, "oil_speed": 5600},
    "6216": {"inner_diameter": 80, "outer_diameter": 140, "width": 26, "dynamic_load": 71.5, "grease_speed": 4300, "oil_speed": 5300},
    "6217": {"inner_diameter": 85, "outer_diameter": 150, "width": 28, "dynamic_load": 83.2, "grease_speed": 4000, "oil_speed": 5000},
    "6218": {"inner_diameter": 90, "outer_diameter": 160, "width": 30, "dynamic_load": 95.8, "grease_speed": 3800, "oil_speed": 4800},
    "6219": {"inner_diameter": 95, "outer_diameter": 170, "width": 32, "dynamic_load": 110.0, "grease_speed": 3600, "oil_speed": 4500},
    "6220": {"inner_diameter": 100, "outer_diameter": 180, "width": 34, "dynamic_load": 122.0, "grease_speed": 3400, "oil_speed": 4300},
    "6300": {"inner_diameter": 10, "outer_diameter": 35, "width": 11, "dynamic_load": 7.65, "grease_speed": 18000, "oil_speed": 24000},
    "6301": {"inner_diameter": 12, "outer_diameter": 37, "width": 12, "dynamic_load": 9.72, "grease_speed": 17000, "oil_speed": 22000},
    "6302": {"inner_diameter": 15, "outer_diameter": 42, "width": 13, "dynamic_load": 11.5, "grease_speed": 16000, "oil_speed": 20000},
    "6303": {"inner_diameter": 17, "outer_diameter": 47, "width": 14, "dynamic_load": 13.5, "grease_speed": 15000, "oil_speed": 19000},
    "6304": {"inner_diameter": 20, "outer_diameter": 52, "width": 15, "dynamic_load": 15.8, "grease_speed": 13000, "oil_speed": 17000},
    "6305": {"inner_diameter": 25, "outer_diameter": 62, "width": 17, "dynamic_load": 22.2, "grease_speed": 10000, "oil_speed": 14000},
    "6306": {"inner_diameter": 30, "outer_diameter": 72, "width": 19, "dynamic_load": 27.0, "grease_speed": 9000, "oil_speed": 12000},
    "6307": {"inner_diameter": 35, "outer_diameter": 80, "width": 21, "dynamic_load": 33.2, "grease_speed": 8000, "oil_speed": 10000},
    "6308": {"inner_diameter": 40, "outer_diameter": 90, "width": 23, "dynamic_load": 40.8, "grease_speed": 7000, "oil_speed": 9000},
    "6309": {"inner_diameter": 45, "outer_diameter": 100, "width": 25, "dynamic_load": 52.8, "grease_speed": 6300, "oil_speed": 8000},
    "6310": {"inner_diameter": 50, "outer_diameter": 110, "width": 27, "dynamic_load": 61.8, "grease_speed": 6000, "oil_speed": 7500},
    "6311": {"inner_diameter": 55, "outer_diameter": 120, "width": 29, "dynamic_load": 71.5, "grease_speed": 5300, "oil_speed": 6700},
    "6312": {"inner_diameter": 60, "outer_diameter": 130, "width": 31, "dynamic_load": 81.8, "grease_speed": 5000, "oil_speed": 6300},
    "6313": {"inner_diameter": 65, "outer_diameter": 140, "width": 33, "dynamic_load": 93.8, "grease_speed": 4500, "oil_speed": 5600},
    "6314": {"inner_diameter": 70, "outer_diameter": 150, "width": 35, "dynamic_load": 105.0, "grease_speed": 4300, "oil_speed": 5300},
    "6315": {"inner_diameter": 75, "outer_diameter": 160, "width": 37, "dynamic_load": 112.0, "grease_speed": 4000, "oil_speed": 5000},
    "6316": {"inner_diameter": 80, "outer_diameter": 170, "width": 39, "dynamic_load": 122.0, "grease_speed": 3800, "oil_speed": 4800},
    "6317": {"inner_diameter": 85, "outer_diameter": 180, "width": 41, "dynamic_load": 132.0, "grease_speed": 3600, "oil_speed": 4500},
    "6318": {"inner_diameter": 90, "outer_diameter": 190, "width": 43, "dynamic_load": 145.0, "grease_speed": 3400, "oil_speed": 4300},
    "6319": {"inner_diameter": 95, "outer_diameter": 200, "width": 45, "dynamic_load": 158.0, "grease_speed": 3200, "oil_speed": 4000},
    "6320": {"inner_diameter": 100, "outer_diameter": 215, "width": 47, "dynamic_load": 172.0, "grease_speed": 2800, "oil_speed": 3600},
    "6403": {"inner_diameter": 17, "outer_diameter": 62, "width": 17, "dynamic_load": 22.5, "grease_speed": 11000, "oil_speed": 15000},
    "6404": {"inner_diameter": 20, "outer_diameter": 72, "width": 19, "dynamic_load": 31.0, "grease_speed": 9500, "oil_speed": 13000},
    "6405": {"inner_diameter": 25, "outer_diameter": 80, "width": 21, "dynamic_load": 38.2, "grease_speed": 8500, "oil_speed": 11000},
    "6406": {"inner_diameter": 30, "outer_diameter": 90, "width": 23, "dynamic_load": 47.5, "grease_speed": 8000, "oil_speed": 10000},
    "6407": {"inner_diameter": 35, "outer_diameter": 100, "width": 25, "dynamic_load": 56.8, "grease_speed": 6700, "oil_speed": 8500},
    "6408": {"inner_diameter": 40, "outer_diameter": 110, "width": 27, "dynamic_load": 65.5, "grease_speed": 6300, "oil_speed": 8000},
    "6409": {"inner_diameter": 45, "outer_diameter": 120, "width": 29, "dynamic_load": 77.5, "grease_speed": 5600, "oil_speed": 7000},
    "6410": {"inner_diameter": 50, "outer_diameter": 130, "width": 31, "dynamic_load": 92.2, "grease_speed": 5300, "oil_speed": 6700},
    "6411": {"inner_diameter": 55, "outer_diameter": 140, "width": 33, "dynamic_load": 100.0, "grease_speed": 4800, "oil_speed": 6000},
    "6412": {"inner_diameter": 60, "outer_diameter": 150, "width": 35, "dynamic_load": 108.0, "grease_speed": 4500, "oil_speed": 5600},
    "6413": {"inner_diameter": 65, "outer_diameter": 160, "width": 37, "dynamic_load": 118.0, "grease_speed": 4300, "oil_speed": 5300},
    "6414": {"inner_diameter": 70, "outer_diameter": 180, "width": 42, "dynamic_load": 140.0, "grease_speed": 3800, "oil_speed": 4800},
    "6415": {"inner_diameter": 75, "outer_diameter": 190, "width": 45, "dynamic_load": 155.0, "grease_speed": 3600, "oil_speed": 4500},
    "6416": {"inner_diameter": 80, "outer_diameter": 200, "width": 48, "dynamic_load": 162.0, "grease_speed": 3400, "oil_speed": 4300},
    "6417": {"inner_diameter": 85, "outer_diameter": 210, "width": 52, "dynamic_load": 175.0, "grease_speed": 3200, "oil_speed": 4000},
    "6418": {"inner_diameter": 90, "outer_diameter": 225, "width": 54, "dynamic_load": 192.0, "grease_speed": 2800, "oil_speed": 3600},
    "6420": {"inner_diameter": 100, "outer_diameter": 250, "width": 58, "dynamic_load": 222.0, "grease_speed": 2400, "oil_speed": 3200},
}

PREFERRED_DIAMETERS = (
    10, 12, 15, 17, 20, 22, 25, 28, 30, 32, 35, 38, 40, 42, 45,
    48, 50, 52, 55, 58, 60, 62, 65, 68, 70, 72, 75, 80,
)


def env_float(name: str, fallback: float) -> float:
    try:
        return float(os.environ.get(name, fallback))
    except (TypeError, ValueError):
        return fallback


def env_text(name: str, fallback: str) -> str:
    value = os.environ.get(name)
    return value if value else fallback


def preferred_at_least(value: float) -> float:
    for diameter in PREFERRED_DIAMETERS:
        if diameter >= value:
            return float(diameter)
    return float(value)


def segment_env(prefix: str, index: int, field: str, fallback: float) -> float:
    value = env_float(f"{prefix}_S{index}_{field}", fallback)
    return max(value, 0.1)


def bearing_for_min_diameter(requested_name: str, min_diameter: float) -> tuple[str, dict]:
    requested_name = str(requested_name or "").strip()
    requested = BEARING_CATALOG.get(requested_name)
    if requested:
        return requested_name, dict(requested)
    for name, spec in BEARING_CATALOG.items():
        if spec["inner_diameter"] >= min_diameter:
            return name, dict(spec)
    name = max(BEARING_CATALOG, key=lambda key: BEARING_CATALOG[key]["inner_diameter"])
    return name, dict(BEARING_CATALOG[name])


def root_diameter(teeth: float, module: float) -> float:
    return max((teeth - 2.5) * module, 1.0)


def key_for_diameter(diameter: float) -> dict:
    if diameter <= 17:
        width, height, depth = 5.0, 5.0, 3.0
    elif diameter <= 22:
        width, height, depth = 6.0, 6.0, 3.5
    elif diameter <= 30:
        width, height, depth = 8.0, 7.0, 4.0
    elif diameter <= 38:
        width, height, depth = 10.0, 8.0, 5.0
    else:
        width, height, depth = 12.0, 8.0, 5.0
    return {"width": width, "height": height, "depth": depth}


def key_from_env(name: str, diameter: float, fallback_length: float) -> dict:
    spec = key_for_diameter(diameter)
    text = os.environ.get(name, "")
    numbers = [float(value) for value in re.findall(r"\d+(?:\.\d+)?", text)]
    if len(numbers) >= 3:
        spec.update({"width": numbers[-3], "height": numbers[-2], "length": numbers[-1]})
    else:
        spec["length"] = float(fallback_length)
    return spec


MODULE = max(env_float("REDUCER_MODULE", 2.0), 0.1)
GEAR_TYPE = env_text("REDUCER_GEAR_TYPE", "spur")
HELIX_ANGLE = env_float("REDUCER_HELIX_ANGLE", 0.0) if GEAR_TYPE == "helical" else 0.0
HELIX_COS = max(math.cos(math.radians(HELIX_ANGLE)), 0.01)
Z1 = max(env_float("REDUCER_Z1", 17.0), 6.0)
PINION_FACE_WIDTH = max(env_float("REDUCER_PINION_FACE_WIDTH", 32.0), 1.0)
GEAR_FACE_WIDTH = max(env_float("REDUCER_GEAR_FACE_WIDTH", 24.0), 1.0)

HIGH_MIN_DIAMETER = max(env_float("REDUCER_HIGH_SHAFT_D", 15.0), 1.0)
LOW_MIN_DIAMETER = max(env_float("REDUCER_LOW_SHAFT_D", 25.0), 1.0)

HIGH_BEARING_NAME, HIGH_BEARING = bearing_for_min_diameter(
    env_text("REDUCER_HIGH_BEARING", "6202"),
    HIGH_MIN_DIAMETER,
)
LOW_BEARING_NAME, LOW_BEARING = bearing_for_min_diameter(
    env_text("REDUCER_LOW_BEARING", "6205"),
    LOW_MIN_DIAMETER,
)

HIGH_BEARING_D = HIGH_BEARING["inner_diameter"]
LOW_BEARING_D = LOW_BEARING["inner_diameter"]

PINION_ROOT_D = max(MODULE * Z1 / HELIX_COS - 2.5 * MODULE, 1.0)
HIGH_OUTER_D = preferred_at_least(max(10.0, HIGH_BEARING_D - 5.0))
HIGH_SHOULDER_D = min(preferred_at_least(HIGH_BEARING_D + 4.0), max(HIGH_BEARING_D, PINION_ROOT_D - 4.0))

LOW_GEAR_SEAT_D = preferred_at_least(max(LOW_BEARING_D + 3.0, LOW_MIN_DIAMETER))
LOW_SHOULDER_D = preferred_at_least(LOW_GEAR_SEAT_D + 4.0)
LOW_OUTER_D = preferred_at_least(max(12.0, LOW_BEARING_D - 8.0))

HIGH_SHAFT_SECTIONS = (
    {"kind": "cylinder", "diameter": segment_env("REDUCER_HIGH", 1, "D", 12.0), "length": segment_env("REDUCER_HIGH", 1, "L", 24.0)},
    {"kind": "cylinder", "diameter": float(HIGH_BEARING_D), "length": segment_env("REDUCER_HIGH", 2, "L", 37.0)},
    {"kind": "cylinder", "diameter": segment_env("REDUCER_HIGH", 3, "D", 14.0), "length": segment_env("REDUCER_HIGH", 3, "L", 2.0)},
    {"kind": "cylinder", "diameter": segment_env("REDUCER_HIGH", 4, "D", 19.0), "length": segment_env("REDUCER_HIGH", 4, "L", 6.5)},
    {"kind": "gear", "module": MODULE, "teeth": Z1, "diameter": PINION_ROOT_D, "length": segment_env("REDUCER_HIGH", 5, "L", PINION_FACE_WIDTH)},
    {"kind": "cylinder", "diameter": segment_env("REDUCER_HIGH", 6, "D", 19.0), "length": segment_env("REDUCER_HIGH", 6, "L", 6.5)},
    {"kind": "cylinder", "diameter": segment_env("REDUCER_HIGH", 7, "D", 14.0), "length": segment_env("REDUCER_HIGH", 7, "L", 2.0)},
    {"kind": "cylinder", "diameter": float(HIGH_BEARING_D), "length": segment_env("REDUCER_HIGH", 8, "L", 17.0)},
)

LOW_SHAFT_SEGMENTS = (
    {"diameter": float(LOW_BEARING_D), "length": segment_env("REDUCER_LOW", 1, "L", 25.0)},
    {"diameter": segment_env("REDUCER_LOW", 2, "D", 28.0), "length": segment_env("REDUCER_LOW", 2, "L", GEAR_FACE_WIDTH)},
    {"diameter": segment_env("REDUCER_LOW", 3, "D", 32.0), "length": segment_env("REDUCER_LOW", 3, "L", 10.0)},
    {"diameter": float(LOW_BEARING_D), "length": segment_env("REDUCER_LOW", 4, "L", 15.0)},
    {"diameter": segment_env("REDUCER_LOW", 5, "D", 22.0), "length": segment_env("REDUCER_LOW", 5, "L", 20.0)},
    {"diameter": segment_env("REDUCER_LOW", 6, "D", 17.0), "length": segment_env("REDUCER_LOW", 6, "L", 33.0)},
)

try:
    from .axial_layout import layout_from_env
except ImportError:
    from axial_layout import layout_from_env

AXIAL_LAYOUT = layout_from_env(HIGH_BEARING, LOW_BEARING)
HIGH_SHAFT_SECTIONS = tuple({**section, "length": AXIAL_LAYOUT["shaft_lengths"][f"highS{i}L"]}
                            for i, section in enumerate(HIGH_SHAFT_SECTIONS, 1))
LOW_SHAFT_SEGMENTS = tuple({**section, "length": AXIAL_LAYOUT["shaft_lengths"][f"lowS{i}L"]}
                          for i, section in enumerate(LOW_SHAFT_SEGMENTS, 1))

# The mating bore follows the actual gear-seat segment, including manual edits.
# A preliminary bearing-based estimate must not override the installed shaft.
GEAR_BORE_DIAMETER = float(LOW_SHAFT_SEGMENTS[1]["diameter"])
SLEEVE_INNER_DIAMETER = LOW_BEARING_D
SLEEVE_OUTER_DIAMETER = preferred_at_least(LOW_BEARING_D + 11.0)
SLEEVE_THICKNESS = 10.0


def cover_dimensions(bearing: dict, shaft_diameter: float, through: bool) -> dict:
    inner = bearing["inner_diameter"]
    outer = bearing["outer_diameter"]
    if through:
        seal_d = shaft_diameter + 1.0
        relief_d = shaft_diameter + 10.0
        return {
            "outer_steps": (
                (outer - 4.0, 3.0),
                (outer, 4.0),
                (outer + 3.0, 3.0),
                (outer, 5.0),
            ),
            "inner_steps": (
                (seal_d, seal_d, 4.0),
                (seal_d, relief_d, 6.0),
                (relief_d, relief_d, 2.0),
                (relief_d, seal_d, 1.0),
                (seal_d, seal_d, 1.0),
                (outer - 17.0, outer - 13.0, 1.0),
            ),
        }
    return {
        "outer_diameter": outer + 3.0,
        "step_diameter": outer,
        "inner_recess_diameter": inner + 9.0,
    }


HIGH_THROUGH_COVER = cover_dimensions(HIGH_BEARING, HIGH_OUTER_D, True)
LOW_THROUGH_COVER = cover_dimensions(LOW_BEARING, LOW_OUTER_D, True)
HIGH_BLANK_COVER = cover_dimensions(HIGH_BEARING, HIGH_BEARING_D, False)
LOW_BLANK_COVER = cover_dimensions(LOW_BEARING, LOW_BEARING_D, False)

# Keep through-cover inner cavities consistent with the reference reducer
# CadQuery models. These cavities are drawing-defined details rather than
# dimensions that should scale from the selected shaft diameter.
HIGH_THROUGH_COVER["inner_steps"] = (
    (16.0, 16.0, 8.0),
    (16.0, 21.0, 0.5),
    (21.0, 21.0, 2.0),
    (21.0, 16.0, 0.5),
    (16.0, 16.0, 3.0),
    (22.0, 24.0, 1.0),
)
LOW_THROUGH_COVER["outer_steps"] = (
    (LOW_BEARING["outer_diameter"] - 4.0, 3.0),
    (LOW_BEARING["outer_diameter"], 4.0),
    (LOW_BEARING["outer_diameter"] + 3.0, 3.0),
    (LOW_BEARING["outer_diameter"], 4.5),
)
LOW_THROUGH_COVER["inner_steps"] = (
    (24.0, 23.0, 0.5),
    (23.0, 23.0, 3.5),
    (23.0, 28.0, 0.5),
    (28.0, 28.0, 3.0),
    (28.0, 23.0, 0.5),
    (23.0, 23.0, 4.5),
    (23.0, 35.0, 2.0),
    (35.0, 39.0, 2.0),
)

HIGH_ADJUSTING_RING = {
    "outer_diameter": HIGH_BEARING["outer_diameter"],
    "inner_diameter": min(HIGH_BEARING["outer_diameter"] - 4.0, HIGH_BEARING_D + 10.0),
    "thickness": 5.0,
}
LOW_ADJUSTING_RING = {
    "outer_diameter": LOW_BEARING["outer_diameter"],
    "inner_diameter": min(LOW_BEARING["outer_diameter"] - 4.0, LOW_BEARING_D + 15.0),
    "thickness": 5.0,
}

