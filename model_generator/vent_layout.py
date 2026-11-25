"""Shared geometric placement for the vent, its raised seat and through bore."""

import math
try:
    from .axial_layout import axial_layout
except ImportError:
    from axial_layout import axial_layout

VENT_FIELDS = {
    "ventOffsetX": ("REDUCER_VENT_OFFSET_X", 0, -10000, "通气器 X 偏移（相对大齿轮轴线）"),
    "ventOffsetY": ("REDUCER_VENT_OFFSET_Y", 0, -10000, "通气器 Y 偏移（相对箱体中面）"),
    "ventStandOff": ("REDUCER_VENT_STAND_OFF", 6, 0, "通气器座面高出局部箱盖的最小高度"),
    "ventGearClearance": ("REDUCER_VENT_GEAR_CLEARANCE", 4, 0, "通气器底端到齿顶包络的最小净空"),
}
SEAT_RADIUS = 10.0
BORE_RADIUS = 5.1
STEM_RADIUS = 5.0
INSERTION_LENGTH = 12.0


def vent_parameters(inputs):
    parameters = {}
    for name, (_, default, minimum, label) in VENT_FIELDS.items():
        raw = inputs.get(name, default)
        try:
            value = float(raw)
        except (TypeError, ValueError, OverflowError):
            value = float("nan")
        if isinstance(raw, bool) or not math.isfinite(value) or (minimum == 0 and value < 0):
            raise ValueError(f"{label}必须为{'非负' if minimum == 0 else ''}有限数值（mm）")
        parameters[name] = value
    return parameters


def roof_height(x, small_x, large_x, small_radius, large_radius):
    delta = (large_radius - small_radius) / (large_x - small_x)
    if abs(delta) >= 1:
        raise ValueError("箱盖圆弧无法构造外公切线")
    cosine = math.sqrt(1 - delta * delta)
    small_tangent_x, large_tangent_x = small_x - delta * small_radius, large_x - delta * large_radius
    if small_tangent_x <= x <= large_tangent_x:
        return small_radius * cosine + delta / cosine * (x - small_tangent_x)
    cx, radius = (small_x, small_radius) if x < small_tangent_x else (large_x, large_radius)
    squared = radius * radius - (x - cx) ** 2
    if squared < -1e-7:
        raise ValueError("通气器安装孔超出箱盖内腔范围，请减小 X 偏移")
    return math.sqrt(max(0, squared))


def cover_vent_layout(inputs, selections, center_distance=None, inner_width=None):
    parameters = vent_parameters(inputs)
    module = max(float(inputs.get("moduleM", 2)), .1)
    z1, z2 = (max(float(inputs.get(key, default)), 6) for key, default in (("z1", 17), ("z2", 42)))
    beta = float(inputs.get("helixAngle", 0)) if selections.get("gearType") == "helical" else 0
    cosine = max(math.cos(math.radians(beta)), .01)
    distance = module * (z1 + z2) / (2 * cosine) if center_distance is None else center_distance
    tip_radii = (module * z1 / (2 * cosine) + module, module * z2 / (2 * cosine) + module)
    small_radius, large_radius = max(47, tip_radii[0] + 12), max(59, tip_radii[1] + 12)
    extra_length = small_radius + large_radius - 47 - 59
    plate_length = max(197, distance + 142 + extra_length)
    large_x = plate_length / 2 - max(77, large_radius + 18)
    small_x = large_x - distance
    x, y = large_x + parameters["ventOffsetX"], parameters["ventOffsetY"]
    depth_extra = (axial_layout(inputs, selections)['inner_width'] if inner_width is None else inner_width) - 41
    if abs(y) + SEAT_RADIUS > (47 + depth_extra) / 2 or abs(y) + BORE_RADIUS > (41 + depth_extra) / 2:
        raise ValueError("通气器安装座或通孔超出箱盖顶部/内腔宽度，请减小 Y 偏移")

    def roof(value, inset=0):
        return roof_height(value, small_x, large_x, small_radius - inset, large_radius - inset)

    # A concave upper profile has its minimum at an interval end; circle peaks
    # are the only additional candidates for its maximum.
    edges = (x - SEAT_RADIUS, x + SEAT_RADIUS)
    heights = [roof(value) for value in edges]
    heights.extend(roof(cx) for cx in (small_x, large_x) if edges[0] <= cx <= edges[1])
    outer_low, outer_high = min(heights), max(heights)
    bore_low = min(roof(x - BORE_RADIUS, 6), roof(x + BORE_RADIUS, 6))
    gear_tops = []
    for cx, radius in zip((small_x, large_x), tip_radii):
        nearest_x = max(0, abs(x - cx) - STEM_RADIUS)
        if nearest_x <= radius:
            gear_tops.append(math.sqrt(max(0, radius * radius - nearest_x * nearest_x)))
    gear_top = max(gear_tops) if gear_tops else None
    seat_z = outer_high + parameters["ventStandOff"]
    if gear_top is not None:
        seat_z = max(seat_z, gear_top + parameters["ventGearClearance"] + INSERTION_LENGTH)
    return {**parameters, "x": x, "y": y, "seat_z": seat_z,
            "boss_bottom_z": outer_low - .2, "bore_bottom_z": max(0, bore_low - 1),
            "seat_radius": SEAT_RADIUS, "bore_radius": BORE_RADIUS,
            "plug_bottom_z": seat_z - INSERTION_LENGTH, "roof_max_z": outer_high,
            "gear_envelope_top_z": gear_top,
            "minimum_gear_envelope_gap": None if gear_top is None else seat_z - INSERTION_LENGTH - gear_top,
            "units": "mm", "height_datum": "shaft_axis",
            "scope": "geometric placement; simplified bore, not a certified thread or full assembly check"}
