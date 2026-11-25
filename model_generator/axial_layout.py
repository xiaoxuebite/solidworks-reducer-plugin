"""Shared bearing fits and axial dimension chain; the spacer stays 10 mm long."""

import math
import os
import re

AXIAL_FIELDS = {
    "housingAxialWidth": ("REDUCER_HOUSING_AXIAL_WIDTH", 0, "箱体内腔轴向宽度；0 为自动"),
    "housingGearEndClearance": ("REDUCER_GEAR_END_CLEARANCE", 8, "齿轮端面到内壁的最小净空"),
}
SLEEVE_LENGTH = 10.0
OIL_RING_LENGTH = 4.0
BEARING_SEAT_FIELDS = {'high': ('highS2D', 'highS8D'), 'low': ('lowS1D', 'lowS4D')}


def selected_bearings(selections):
    try:
        from .shaft_params import bearing_for_min_diameter
    except ImportError:
        from shaft_params import bearing_for_min_diameter
    return (bearing_for_min_diameter(selections.get('highBearing', '6202'), 15)[1],
            bearing_for_min_diameter(selections.get('lowBearing', '6205'), 25)[1])


def bearing_seat_diameters(selections, high_bearing=None, low_bearing=None):
    if high_bearing is None or low_bearing is None:
        high_bearing, low_bearing = selected_bearings(selections)
    return {key: float(bearing['inner_diameter'])
            for prefix, bearing in (('high', high_bearing), ('low', low_bearing))
            for key in BEARING_SEAT_FIELDS[prefix]}


def _number(values, name, default, positive=False):
    raw = values.get(name, default)
    try:
        value = float(raw)
    except (TypeError, ValueError, OverflowError):
        value = float("nan")
    if isinstance(raw, bool) or not math.isfinite(value) or value < 0 or positive and value == 0:
        raise ValueError(f"轴向参数 {name} 必须为{'正' if positive else '非负'}有限数值")
    return value


def axial_layout(inputs, selections, high_bearing=None, low_bearing=None):
    if high_bearing is None or low_bearing is None:
        high_bearing, low_bearing = selected_bearings(selections)
    diameters = bearing_seat_diameters(selections, high_bearing, low_bearing)
    diameter_changes = {}
    for key, diameter in diameters.items():
        if key not in inputs:
            continue
        try:
            previous = float(inputs[key])
        except (ValueError, TypeError, OverflowError):
            previous = float('nan')
        if isinstance(inputs[key], bool) or not math.isfinite(previous) or abs(previous - diameter) > 1e-7:
            diameter_changes[key] = {'before': inputs[key], 'after': diameter}
    hb, lb = high_bearing.get('width', 11), low_bearing.get('width', 15)
    h_bore = high_bearing.get('inner_diameter', 15)
    b1 = _number(inputs, 'highS5L', inputs.get('pinionFaceWidth', 32), True)
    b2 = _number(inputs, 'gearFaceWidth', 24, True)
    requested = _number(inputs, 'housingAxialWidth', 0)
    gap = _number(inputs, 'housingGearEndClearance', 8)
    maximum_width = max(b1, b2)
    # Blank cover 7 + adjusting ring 5 + low bearing + fixed spacer.
    positive_stack = b2 / 2 + SLEEVE_LENGTH + lb + 12
    side_depth = max(6, min(28, positive_stack - maximum_width / 2 - gap))
    recommended = max(41, maximum_width + 2 * gap,
                      positive_stack + maximum_width / 2 + gap - side_depth)
    inner = requested or math.ceil(recommended - 1e-9)
    outer = inner + 2 * side_depth
    half = outer / 2
    mesh_y = half - positive_stack
    bearings = {'high_positive': [half - 12 - hb, half - 12],
                'high_negative': [-half + 12, -half + 12 + hb],
                'low_positive': [half - 12 - lb, half - 12],
                'low_negative': [-half + 11.5, -half + 11.5 + lb]}
    warnings = []
    if inner < recommended - 1e-7:
        warnings.append(f'内腔轴向宽度 {inner:g} mm 小于当前建议值 {recommended:g} mm；保留手动值，端面净空需复核。')
    original = {}
    defaults = {'high': [24, 37, 2, 6.5, b1, 6.5, 2, 17], 'low': [25, b2, 10, 15, 20, 33]}
    for prefix, lengths in defaults.items():
        for i, value in enumerate(lengths, 1):
            original[f'{prefix}S{i}L'] = _number(inputs, f'{prefix}S{i}L', value, True)
    linked = dict(original)
    linked['highS5L'], linked['lowS2L'] = b1, b2
    for side, shoulder_i, groove_i in (('positive', 4, 3), ('negative', 6, 7)):
        available = ((bearings['high_positive'][0] - mesh_y) if side == 'positive'
                     else (mesh_y - bearings['high_negative'][1])) - b1 / 2
        groove = min(original[f'highS{groove_i}L'], max(.5, available - .5))
        groove_d = _number(inputs, f'highS{groove_i}D', h_bore - 1, True)
        reserve = OIL_RING_LENGTH + groove if groove_d > h_bore else max(OIL_RING_LENGTH, groove)
        shoulder = min(original[f'highS{shoulder_i}L'], max(.5, available - reserve))
        if available < shoulder + reserve - 1e-7:
            warnings.append(f'高速轴 {side} 侧无法容纳当前齿宽、挡油环和退刀段；需调整齿宽或连接件布置。')
        linked[f'highS{shoulder_i}L'], linked[f'highS{groove_i}L'] = shoulder, groove
    # The legacy high cover has a second 16-mm throat ending at local z=14.
    high_transition_y = half + 3 - 14
    positive_neck = b1 / 2 + linked['highS4L'] + linked['highS3L']
    negative_neck = b1 / 2 + linked['highS6L'] + linked['highS7L']
    linked['highS2L'] = max(.5, high_transition_y - mesh_y - positive_neck)
    linked['highS8L'] = max(.5, mesh_y - bearings['high_negative'][0] - negative_neck)
    key_numbers = re.findall(r'\d+(?:\.\d+)?', str(selections.get('highKey', '')))
    key_length = float(key_numbers[-1]) if len(key_numbers) >= 3 else 18
    linked['highS1L'] = max(original['highS1L'], 14 + 4 + key_length + 1)
    linked['lowS1L'] = SLEEVE_LENGTH + lb
    low_negative_room = mesh_y - bearings['low_negative'][1] - b2 / 2
    linked['lowS3L'] = min(original['lowS3L'], max(.5, low_negative_room))
    linked['lowS4L'] = max(.5, mesh_y - bearings['low_negative'][0] - b2 / 2 - linked['lowS3L'])
    linked['lowS5L'] = max(original['lowS5L'], 14.5 - 4 + 1)
    high_center = sum(linked[f'highS{i}L'] for i in range(1, 5)) + b1 / 2
    low_center = linked['lowS1L'] + b2 / 2
    supports = {}
    for prefix, center in (('high', high_center), ('low', low_center)):
        supports[prefix] = [center + mesh_y - sum(bearings[prefix + '_positive']) / 2,
                            center, center + mesh_y - sum(bearings[prefix + '_negative']) / 2]
    if _number(inputs, 'highS1D', 12, True) > 16:
        warnings.append('高速轴外伸段直径大于当前透盖 16 mm 最小孔径；本轮长度联动不改变孔径，需单独复核。')
    if _number(inputs, 'lowS5D', 22, True) > 23:
        warnings.append('低速轴密封段直径大于当前透盖 23 mm 最小孔径；需单独复核径向配合。')
    return {'inner_width': inner, 'recommended_inner_width': recommended, 'outer_width': outer,
            'requested_inner_width': requested, 'gear_end_clearance': gap,
            'side_depth': side_depth, 'mesh_center_y': mesh_y,
            'gear_positive_clearance': inner / 2 - mesh_y - maximum_width / 2,
            'gear_negative_clearance': inner / 2 + mesh_y - maximum_width / 2,
            'sleeve_length': SLEEVE_LENGTH, 'oil_ring_length': OIL_RING_LENGTH,
            'sleeve_interval': [bearings['low_positive'][0] - SLEEVE_LENGTH, bearings['low_positive'][0]],
            'bearings': bearings, 'shaft_lengths': linked, 'local_supports': supports,
            'shaft_diameters': diameters, 'diameter_changes': diameter_changes,
            'length_changes': {key: {'before': original[key], 'after': value} for key, value in linked.items()
                               if abs(value - original[key]) > 1e-7},
            'warnings': warnings, 'units': 'mm',
            'scope': 'linked bearing-seat diameters and axial geometry; no thread, tolerance, fatigue or full assembly certification'}


def layout_from_env(high_bearing, low_bearing):
    names = {'pinionFaceWidth': 'REDUCER_PINION_FACE_WIDTH', 'gearFaceWidth': 'REDUCER_GEAR_FACE_WIDTH'}
    names.update({key: env for key, (env, _, _) in AXIAL_FIELDS.items()})
    for prefix in ('high', 'low'):
        for i in range(1, 9 if prefix == 'high' else 7):
            for field in ('D', 'L'):
                names[f'{prefix}S{i}{field}'] = f'REDUCER_{prefix.upper()}_S{i}_{field}'
    values = {key: os.environ[env] for key, env in names.items() if env in os.environ}
    return axial_layout(values, {'highKey': os.environ.get('REDUCER_HIGH_KEY', '')}, high_bearing, low_bearing)
