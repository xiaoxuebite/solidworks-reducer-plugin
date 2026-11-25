"""Shared gear hand, viewing direction and signed mesh thrust convention."""

import math

DRIVER_ROTATION = "clockwise_from_input"
DRIVER_ROTATIONS = (DRIVER_ROTATION, "counterclockwise_from_input")
LEGACY_DRIVER_ROTATIONS = {"clockwise_from_top": DRIVER_ROTATION,
                           "counterclockwise_from_top": DRIVER_ROTATIONS[1]}
HELIX_HANDS = ("pinion_left_gear_right", "pinion_right_gear_left")
DEFAULT_HELIX_HAND = HELIX_HANDS[0]
AXIAL_SUPPORT_METHOD = 'two_end_stops_by_rotation_hand_v2'
ROTATION_LABELS = {
    DRIVER_ROTATIONS[0]: "从主动轴外伸端朝箱内看：主动轮顺时针，从动轮逆时针",
    DRIVER_ROTATIONS[1]: "从主动轴外伸端朝箱内看：主动轮逆时针，从动轮顺时针",
}
HAND_LABELS = {
    HELIX_HANDS[0]: "主动轮左旋、从动轮右旋",
    HELIX_HANDS[1]: "主动轮右旋、从动轮左旋",
}


def normalize_driver_rotation(value):
    if not isinstance(value, str):
        raise ValueError('请选择主动轮顺时针或逆时针转动。')
    rotation = LEGACY_DRIVER_ROTATIONS.get(value, value)
    if rotation not in DRIVER_ROTATIONS:
        raise ValueError('请选择主动轮顺时针或逆时针转动（从主动轴外伸端朝箱内看）。')
    return rotation


def gear_operating_conditions(inputs, selections, tangential_force=0):
    """Look from the input-shaft extension (+Y) toward the housing interior (-Y).

    Under forward power flow the pinion is the driver. A left-hand pinion
    rotating clockwise as viewed from its extension is thrust toward the observer.
    cq_gears uses row-vector rotations: positive constructor beta gives a
    left-hand tooth helix. Both restored gear frames map their local axes to -Y.
    The current template has opposing outward end stops: the viewing-side (+Y)
    bearing is on high segment 2 / low segment 1; the opposite (-Y) bearing is
    on high segment 8 / low segment 4. Allocate thrust along that load path,
    not as a generic inference about arbitrary locating/floating arrangements.
    """
    rotation = normalize_driver_rotation(selections.get("driverRotation", DRIVER_ROTATION))
    hand = selections.get("helixHand", DEFAULT_HELIX_HAND)
    if hand not in HELIX_HANDS:
        raise ValueError("请选择主动轮左旋/从动轮右旋，或主动轮右旋/从动轮左旋。")
    helical = selections.get("gearType", "spur") == "helical"
    beta = float(inputs.get("helixAngle", 15)) if helical else 0.0
    ft = float(tangential_force)
    if not math.isfinite(beta) or not 0 <= beta <= 35 or not math.isfinite(ft):
        raise ValueError("螺旋角应为 0-35° 的角度值，旋向通过旋向选项设置。")
    hand_sign = 1 if hand == HELIX_HANDS[0] else -1
    rotation_sign = 1 if rotation == DRIVER_ROTATION else -1
    sign = hand_sign * rotation_sign
    magnitude = abs(ft) * math.tan(math.radians(beta))
    force = sign * magnitude
    thrust_sign = sign if helical and beta > 0 else 0
    high_support = 'left' if thrust_sign > 0 else 'right' if thrust_sign < 0 else 'none'
    low_support = 'right' if thrust_sign > 0 else 'left' if thrust_sign < 0 else 'none'
    return {
        "driverRotation": rotation, "helixHand": hand,
        "rotationLabel": ROTATION_LABELS[rotation],
        "viewDirection": "+Y → -Y（从主动轴外伸端朝箱内看；两轴采用同一观察方向）",
        "helixHandLabel": HAND_LABELS[hand] if helical else "直齿轮",
        "pinionHand": ("left" if hand_sign > 0 else "right") if helical else "none",
        "gearHand": ("right" if hand_sign > 0 else "left") if helical else "none",
        "cadPinionHelixAngle": hand_sign * beta,
        "pinionAxialForce": force, "gearAxialForce": -force,
        "axialSupportMethod": AXIAL_SUPPORT_METHOD,
        "highAxialSupport": high_support, "lowAxialSupport": low_support,
        "highAxialSupportLabel": {'left': '观察端侧轴承（高速轴第2段）', 'right': '对侧轴承（高速轴第8段）', 'none': '无齿轮轴向力'}[high_support],
        "lowAxialSupportLabel": {'left': '观察端侧轴承（低速轴第1段）', 'right': '对侧轴承（低速轴第4段）', 'none': '无齿轮轴向力'}[low_support],
        "pinionThrustDirection": ("朝观察者（输入端一侧）" if force > 0 else "背离观察者（另一侧）") if force else "无齿轮轴向力",
        "gearThrustDirection": ("背离观察者（另一侧）" if force > 0 else "朝观察者（输入端一侧）") if force else "无齿轮轴向力",
    }
