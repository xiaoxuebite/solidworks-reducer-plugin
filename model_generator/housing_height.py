"""Vertical layout only; keeps the restored template's other dimensions intact."""

import math

DEFAULT_BOTTOM_CLEARANCE = 12.0
BASE_THICKNESS = 10.0
JOINT_FLANGE_THICKNESS = 6.0
MIN_AXIS_HEIGHT = 64.0
BEARING_BRIDGE_DROP = 19.0


def bottom_clearance(value=DEFAULT_BOTTOM_CLEARANCE):
    try:
        number = float(value)
    except (TypeError, ValueError, OverflowError):
        number = float("nan")
    if isinstance(value, bool) or not math.isfinite(number) or number < 0:
        raise ValueError("齿顶到底部净空必须为非负有限数值（mm）")
    return number


def height_layout(tip_radius, clearance=DEFAULT_BOTTOM_CLEARANCE):
    clearance = bottom_clearance(clearance)
    if not math.isfinite(tip_radius) or tip_radius <= 0:
        raise ValueError("Gear tip envelope radius must be finite and positive")
    axis = max(MIN_AXIS_HEIGHT, BASE_THICKNESS + tip_radius + clearance)
    return {"bottom_clearance": clearance, "tip_envelope_radius": tip_radius,
            "axis_height": axis, "wall_height": axis - BASE_THICKNESS - JOINT_FLANGE_THICKNESS,
            "base_thickness": BASE_THICKNESS, "joint_flange_thickness": JOINT_FLANGE_THICKNESS,
            "minimum_envelope_floor_gap": axis - tip_radius - BASE_THICKNESS,
            "bearing_bridge_drop": BEARING_BRIDGE_DROP, "units": "mm"}


def design_height(inputs, selections):
    module = max(float(inputs.get("moduleM", 2)), .1)
    teeth = max(float(inputs.get("z1", 17)), float(inputs.get("z2", 42)), 6)
    beta = float(inputs.get("helixAngle", 0)) if selections.get("gearType") == "helical" else 0
    cosine = max(math.cos(math.radians(beta)), .01)
    return height_layout(module * teeth / (2 * cosine) + module,
                         inputs.get("housingBottomClearance", DEFAULT_BOTTOM_CLEARANCE))
