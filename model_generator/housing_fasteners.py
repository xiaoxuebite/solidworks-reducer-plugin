"""Table-driven cast housing mounting interfaces, all dimensions in mm."""

import math

# User reference Table 11-2, printed p.167. D0 is a head/nut seat, NOT a through hole.
FLANGE_TABLE = {
    6: (12, 10, 13, 5, 3), 8: (13, 11, 18, 5, 3),
    10: (16, 14, 22, 5, 3), 12: (18, 16, 26, 5, 3),
    14: (20, 18, 30, 5, 3), 16: (22, 20, 33, 8, 3),
    18: (24, 22, 36, 8, 5), 20: (26, 24, 40, 8, 5),
    22: (30, 26, 43, 8, 5), 24: (34, 28, 48, 10, 5),
    27: (36, 32, 53, 10, 8), 30: (40, 34, 61, 10, 8),
}
# ISO 273 medium series; independent of the textbook's D0 seat diameter.
CLEARANCE_MEDIUM = {6: 6.6, 8: 9, 10: 11, 12: 13.5, 14: 15.5, 16: 17.5,
                    18: 20, 20: 22, 22: 24, 24: 26, 27: 30, 30: 33}


def flange_dimensions(nominal):
    if isinstance(nominal, bool) or nominal not in FLANGE_TABLE:
        raise ValueError("Bolt diameter is outside reference Table 11-2; no extrapolation")
    c1, c2, seat, radius, fillet = FLANGE_TABLE[nominal]
    return {"nominal": nominal, "thread": f"M{nominal}", "clearance_hole": CLEARANCE_MEDIUM[nominal],
            "c1_min": c1, "c2_min": c2, "seat_diameter_d0": seat,
            "root_radius_max": radius, "edge_radius_max": fillet}


def fastener_plan(center_distance):
    if isinstance(center_distance, bool) or not math.isfinite(center_distance) or center_distance <= 0:
        raise ValueError("Fastener center distance must be finite and positive")
    raw = .036 * center_distance + 12
    anchor = next((d for d in FLANGE_TABLE if d >= raw), None)
    if anchor is None:
        raise ValueError("Anchor bolt exceeds available M30 reference table; review required")
    candidates = [d for d in FLANGE_TABLE if .5 * anchor <= d <= .6 * anchor]
    if not candidates:
        raise ValueError("No catalog cover-to-base bolt satisfies 0.5-0.6 df")
    connection = min(candidates, key=lambda d: (abs(d - .55 * anchor), d))
    pins = [d for d in (4, 5, 6, 8, 9, 10, 12, 14, 16, 20) if .7 * connection <= d <= .8 * connection]
    if not pins:
        raise ValueError("No locating pin satisfies 0.7-0.8 d2")
    return {"source": "User reference Tables 11-1/11-2, printed pp.164/167",
            "anchor_raw": raw, "anchor": flange_dimensions(anchor),
            "anchor_count": 4 if center_distance <= 250 else 6 if center_distance <= 500 else 8,
            "connection": flange_dimensions(connection), "dowel_diameter": pins[-1],
            "connection_spacing_recommended": [150, 200],
            "selection": "df rounded upward to the available table; d2 nearest 0.55 df within 0.5-0.6 df",
            "d0_is_not_through_hole": True,
            "clearance_source": "ISO 273 medium series; norelem Technical Information, Screws and Nuts, p.4",
            "pending": ["bolt preload, strength, foundation and positional tolerances",
                        "bearing-adjacent d1 bolt pattern and actual wrench access",
                        "embedded end-cover retention; do not apply flanged cover Table 11-10",
                        "Table 1-38 casting transitions not supplied"]}


def inspection_cover_reference(center_distance):
    """Reference selection only: the mounting opening needs a separate layout change."""
    for limit, length, pitch_x, width, pitch_y, hole, count, radius in (
        (150, 90, 75, 70, 55, 7, 4, 5), (250, 120, 105, 90, 75, 7, 4, 5),
        (350, 180, 165, 140, 125, 7, 8, 5), (450, 200, 180, 180, 160, 11, 8, 10),
        (500, 220, 200, 200, 180, 11, 8, 10)):
        if 0 < center_distance <= limit:
            return {"source": "User reference Table 11-4, single stage, printed p.167", "modeled": False,
                    "length": length, "width": width, "pitch_x": pitch_x, "pitch_y": pitch_y,
                    "hole_diameter": hole, "hole_count": count, "thickness": 4, "corner_radius": radius}
    raise ValueError("Inspection cover is outside Table 11-4 single-stage range")


def model_fastener_plan(center_distance):
    """Keep catalog recommendations strict, but permit provisional review geometry."""
    try:
        return {**fastener_plan(center_distance), "warnings": []}
    except ValueError as error:
        if not math.isfinite(center_distance) or center_distance <= 0:
            raise
        plan = fastener_plan(500)
        return {**plan, "reference_available": False, "anchor_raw": .036 * center_distance + 12,
                "anchor_count": 4 if center_distance <= 250 else 6 if center_distance <= 500 else 8,
                "selection": "Provisional M30/M16 review geometry; not a compliant catalog selection",
                "warnings": [f"紧固件超出参考表，暂用 M30/M16 生成复核几何：{error}"]}
