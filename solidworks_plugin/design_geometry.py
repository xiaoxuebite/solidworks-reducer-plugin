"""Geometry adaptation used only by the new reducer design workflow.

The historical exporters intentionally retain drawing-defined cover cavities.
Newly sized shafts can outgrow those cavities. This adapter keeps their axial
stations and diameter differences, and provides the required shaft clearance
without changing the legacy export path or enlarging a cover's outside wall.
Inspection mounting stations that outgrow the roof's flat tangent are moved
as a complete assembly; legacy reference stations remain exactly unchanged.
"""
from copy import deepcopy
import math


def _positive(value, label):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or value <= 0:
        raise ValueError(f'{label} must be a finite positive number.')
    return float(value)


def _check_outer_wall(cover, inner_steps):
    """Use the same overlapping axial intervals as C# CheckThroughFit.

    This early check cannot replace the native contract's complete cover and
    oil-seal validation. It prevents silently solving a tight bore by changing
    the outside diameter or accepting a cavity that has crossed the wall.
    """
    name = cover.get('name', 'through_cover')
    outer_steps = cover.get('outer_steps')
    if not isinstance(outer_steps, (list, tuple)) or not outer_steps:
        raise ValueError(f'{name}: outer_steps are required to check the adapted bore.')
    wall, outer_start = math.inf, 0.0
    for index, outer in enumerate(outer_steps):
        if not isinstance(outer, (list, tuple)) or len(outer) != 2:
            raise ValueError(f'{name}: outer step {index + 1} must have a diameter and length.')
        diameter = _positive(outer[0], f'{name} outer diameter')
        length = _positive(outer[1], f'{name} outer step length')
        inner_start = 0.0
        for first, second, inner_length in inner_steps:
            lo, hi = max(outer_start, inner_start), min(outer_start + length, inner_start + inner_length)
            if hi - lo > 1e-9:
                a = first + (second - first) * (lo - inner_start) / inner_length
                b = first + (second - first) * (hi - inner_start) / inner_length
                wall = min(wall, (diameter - max(a, b)) / 2)
            inner_start += inner_length
        outer_start += length
    if wall <= 1e-8 or not math.isfinite(wall):
        raise ValueError(f'{name}: adapted inner bore would cross the outside wall; resize the cover explicitly. Its outside diameter was not changed.')


def _arc_radius(curve):
    """Recover an analytic source arc's radius for the housing envelope."""
    (ax, ay), (bx, by), (cx, cy) = curve['start'], curve['mid'], curve['end']
    divisor = 2 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by))
    if abs(divisor) < 1e-10:
        raise ValueError('Inspection mounting needs a nondegenerate analytic roof arc.')
    aa, bb, cc = ax * ax + ay * ay, bx * bx + by * by, cx * cx + cy * cy
    ox = (aa * (by - cy) + bb * (cy - ay) + cc * (ay - by)) / divisor
    oy = (aa * (cx - bx) + bb * (ax - cx) + cc * (bx - ax)) / divisor
    return oy + math.hypot(ax - ox, ay - oy)


def _adapt_inspection_mount(payload):
    """Keep the complete inspection assembly on the actual common flat roof.

    The historical large-center-minus-39 station is valid for the reference
    reducer, but can lie wholly beyond the common tangent for larger gears.
    Such a pad floats above the circular roof and becomes a separate body.
    Relocate only that invalid station, without changing any part profile.
    """
    housing = payload.get('housing')
    if not housing:
        return []
    upper = {feature['name']: feature for feature in housing['upper']}
    required = ('Outer_Tangent_Arc_Shell', 'Inspection_Raised_Pad', 'Inspection_Aperture', 'Vent_Raised_Seat')
    if not all(name in upper for name in required):
        raise ValueError('The native design needs the complete inspection mounting recipe.')
    shell, pad = upper[required[0]], upper[required[1]]
    if len(shell['curves']) != 4 or shell['curves'][1]['kind'] != 'line':
        raise ValueError('The inspection pad needs the analytic common roof tangent.')
    tangent = shell['curves'][1]
    half_length = max(abs(point[0]) for curve in pad['curves'] for point in (curve['start'], curve['end']))
    horizontal = pad['u'][0]
    if horizontal <= 0:
        raise ValueError('The inspection pad tangent must point in positive X.')
    first, last = sorted((tangent['start'][0], tangent['end'][0]))
    minimum, maximum = first + half_length * horizontal, last - half_length * horizontal
    if minimum > maximum + 1e-8:
        raise ValueError('The 44 mm inspection pad cannot fit on this roof tangent; change the housing dimensions explicitly.')
    old_x = pad['origin'][0]
    new_x = max(minimum, min(maximum, old_x))
    if abs(new_x - old_x) <= 1e-8:
        return []
    delta_x = new_x - old_x
    delta_z = delta_x * pad['u'][2] / horizontal
    plates = {part['name']: part for part in payload.get('auxiliary', [])
              if part['name'] in ('inspection_cover', 'inspection_cover_gasket')}
    if len(plates) != 2:
        raise ValueError('Relocating the inspection pad requires both its cover and gasket.')
    screw_holes = [feature for feature in housing['upper']
                   if feature['name'].startswith('Inspection_Screw_Hole_') or
                   any(name.startswith('Inspection_Screw_Hole_') for name in feature.get('source_features', []))]
    if payload.get('hardware_version') == 1 and sum(len(f['curves']) for f in screw_holes) != 4:
        raise ValueError('Relocating the inspection mount requires four M3 screw holes.')
    for feature in (pad, upper['Inspection_Aperture'], *screw_holes):
        feature['origin'][0] += delta_x
        feature['origin'][2] += delta_z
    for plate in plates.values():
        plate['placement'][9] += delta_x
        plate['placement'][11] += delta_z
    normal_z = pad['u'][0] * pad['v'][1] - pad['u'][1] * pad['v'][0]
    pad_top = pad['origin'][2] + half_length * abs(pad['u'][2]) + pad['depth'] * normal_z
    vent = upper['Vent_Raised_Seat']
    housing['expected_upper_bounds'][5] = max(_arc_radius(shell['curves'][0]), _arc_radius(shell['curves'][2]),
                                               vent['origin'][2] + vent['depth'], pad_top)
    message = (f'检查口原安装中心 X={old_x:g} mm 超出箱盖公切平面可用范围，'
               f'凸台、开孔、检查盖和垫片共同沿原切向移动 ΔX={delta_x:g}、ΔZ={delta_z:g} mm，'
               f'新中心 X={new_x:g} mm；四者外形、尺寸及安装方向不变。')
    return [dict(kind='inspection_mount_tangent_relocation', part='inspection_mount', old_center_x_mm=old_x,
                 new_center_x_mm=new_x, translation_mm=[delta_x, 0.0, delta_z],
                 tangent_center_range_x_mm=[minimum, maximum], message=message)]


def adapt_native_parameters(payload, design=None):
    """Return a deep copy with explicitly recorded design geometry adaptations.

    Only the new design bridge should call this function, after a fresh-process
    native JSON export. ``design`` is accepted for bridge API consistency;
    actual geometry always comes from the exported native contract. Native C#
    Validate/CheckThroughFit remain required after this adaptation.

    All inner diameters of a changed cover receive the same offset, ensuring
    a minimum bore of shaft diameter + 1 mm (0.5 mm radial clearance). Axial
    lengths, outside diameters and every other part remain unchanged. New
    design_adjustments and migration_notes entries are added only on changes.
    The inspection pad, aperture, cover and gasket share one tangent relocation
    when the historical station lies outside the available flat roof segment.
    """
    if not isinstance(payload, dict):
        raise ValueError('Native parameters must be an object.')
    result = deepcopy(payload)
    covers = result.get('through_covers', [])
    if covers is None:
        covers = []
    if not isinstance(covers, (list, tuple)):
        raise ValueError('through_covers must be a list.')
    adjustments = _adapt_inspection_mount(result)
    for cover in covers:
        if not isinstance(cover, dict):
            raise ValueError('Each through cover must be an object.')
        name = cover.get('name', 'through_cover')
        shaft = _positive(cover.get('seal_bore_diameter'), f'{name} seal_bore_diameter')
        steps = cover.get('inner_steps')
        if not isinstance(steps, (list, tuple)) or not steps:
            raise ValueError(f'{name}: inner_steps cannot be empty.')
        checked = []
        for index, step in enumerate(steps):
            if not isinstance(step, (list, tuple)) or len(step) != 3:
                raise ValueError(f'{name}: inner step {index + 1} must contain two diameters and a length.')
            checked.append([_positive(value, f'{name} inner step {index + 1}') for value in step])
        old_min = min(min(step[:2]) for step in checked)
        delta = max(0.0, shaft + 1.0 - old_min)
        if delta == 0:
            continue
        adapted = [[first + delta, second + delta, length] for first, second, length in checked]
        _check_outer_wall(cover, adapted)
        # Preserve the original axial values, including their numeric type.
        cover['inner_steps'] = [[step[0] + delta, step[1] + delta, step[2]] for step in steps]
        message = (f'{name}: 为实际轴径 {shaft:g} mm 保留 0.5 mm 径向净空，'
                   f'内腔各级直径统一增加 {delta:g} mm，最小孔径 {old_min:g} → {old_min + delta:g} mm；'
                   '轴向层级及外径保持原值。')
        adjustments.append(dict(kind='through_cover_bore_clearance', part=name,
                                shaft_diameter_mm=shaft, old_min_bore_mm=old_min,
                                new_min_bore_mm=old_min + delta, diameter_offset_mm=delta, message=message))
    if adjustments:
        for key in ('design_adjustments', 'migration_notes'):
            if key in result and not isinstance(result[key], list):
                raise ValueError(f'{key} must be a list.')
        result.setdefault('design_adjustments', []).extend(adjustments)
        result.setdefault('migration_notes', []).extend(item['message'] for item in adjustments)
    return result
