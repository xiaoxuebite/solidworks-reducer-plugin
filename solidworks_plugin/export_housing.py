"""Native feature recipes for the active restored lower housing and upper lid.

The recipe describes original analytic profiles (lines and circular arcs), not
a tessellated or STEP-derived envelope. All coordinates are in millimetres.
"""
from __future__ import annotations

import math
import os
from copy import deepcopy

XY = ([1., 0., 0.], [0., 1., 0.])
XZ = ([1., 0., 0.], [0., 0., 1.])
YZ = ([0., 1., 0.], [0., 0., 1.])


def _line(a, b):
    return dict(kind='line', start=list(a), end=list(b))


def _arc(a, midpoint, b):
    return dict(kind='arc', start=list(a), mid=list(midpoint), end=list(b))


def _polygon(points):
    return [_line(a, b) for a, b in zip(points, points[1:] + points[:1])]


def _rectangle(cx, cy, length, width):
    return _polygon([[cx-length/2, cy-width/2], [cx+length/2, cy-width/2],
                     [cx+length/2, cy+width/2], [cx-length/2, cy+width/2]])


def _rounded(cx, cy, length, width, radius):
    x, y, r = length/2, width/2, radius
    k = 1 / math.sqrt(2)
    pts = [[cx+x-r, cy+y], [cx-x+r, cy+y], [cx-x, cy+y-r],
           [cx-x, cy-y+r], [cx-x+r, cy-y], [cx+x-r, cy-y],
           [cx+x, cy-y+r], [cx+x, cy+y-r]]
    mids = [[cx-x+r-r*k, cy+y-r+r*k], [cx-x+r-r*k, cy-y+r-r*k],
            [cx+x-r+r*k, cy-y+r-r*k], [cx+x-r+r*k, cy+y-r+r*k]]
    return [_line(pts[0], pts[1]), _arc(pts[1], mids[0], pts[2]),
            _line(pts[2], pts[3]), _arc(pts[3], mids[1], pts[4]),
            _line(pts[4], pts[5]), _arc(pts[5], mids[2], pts[6]),
            _line(pts[6], pts[7]), _arc(pts[7], mids[3], pts[0])]


def _d_pad(cx, cy, radius, reach, side):
    if side == 'left':
        pts = [[cx, cy-radius], [cx+reach, cy-radius], [cx+reach, cy+radius], [cx, cy+radius]]
        return [_line(pts[0], pts[1]), _line(pts[1], pts[2]), _line(pts[2], pts[3]),
                _arc(pts[3], [cx-radius, cy], pts[0])]
    pts = [[cx, cy-radius], [cx-reach, cy-radius], [cx-reach, cy+radius], [cx, cy+radius]]
    return [_line(pts[0], pts[1]), _line(pts[1], pts[2]), _line(pts[2], pts[3]),
            _arc(pts[3], [cx+radius, cy], pts[0])]


def _circle(x, y, radius):
    return [dict(kind='circle', center=[x, y], radius=radius)]


def _feature(name, kind, curves, origin, basis, depth, axis=None):
    item = dict(name=name, kind=kind, curves=curves, origin=list(origin),
                u=basis[0], v=basis[1], depth=depth)
    if axis is not None:
        item['axis'] = axis
    return item


def _outer_profile(small_x, large_x, small_radius, large_radius):
    delta = (large_radius-small_radius)/(large_x-small_x)
    if abs(delta) >= 1:
        raise ValueError('Housing common tangent cannot be constructed')
    cosine = math.sqrt(1-delta*delta)
    angle = math.atan2(cosine, -delta)
    left = [small_x-small_radius, 0.]
    a = [small_x-delta*small_radius, cosine*small_radius]
    b = [large_x-delta*large_radius, cosine*large_radius]
    right = [large_x+large_radius, 0.]
    amid = [small_x+small_radius*math.cos((math.pi+angle)/2), small_radius*math.sin((math.pi+angle)/2)]
    bmid = [large_x+large_radius*math.cos(angle/2), large_radius*math.sin(angle/2)]
    return [_arc(left, amid, a), _line(a, b), _arc(b, bmid, right), _line(right, left)]


def _upper_boss_profile(small_x, large_x, small_radius, large_radius, connection=23.):
    left = [small_x-small_radius, 0.]
    a = [small_x+math.sqrt(small_radius**2-connection**2), connection]
    b = [large_x-math.sqrt(large_radius**2-connection**2), connection]
    right = [large_x+large_radius, 0.]
    aa = math.atan2(a[1], a[0]-small_x)
    ba = math.atan2(b[1], b[0]-large_x)
    amid = [small_x+small_radius*math.cos((math.pi+aa)/2), small_radius*math.sin((math.pi+aa)/2)]
    bmid = [large_x+large_radius*math.cos(ba/2), large_radius*math.sin(ba/2)]
    return [_arc(left, amid, a), _line(a, b), _arc(b, bmid, right), _line(right, left)]


def _rectangle_bounds(feature):
    curves = feature['curves']
    if len(curves) != 4 or any(c['kind'] != 'line' for c in curves):
        return None
    vertices = [c['start'] for c in curves]
    xs, ys = sorted({p[0] for p in vertices}), sorted({p[1] for p in vertices})
    if len(xs) != 2 or len(ys) != 2 or xs[0] >= xs[1] or ys[0] >= ys[1]:
        return None
    if {(p[0], p[1]) for p in vertices} != {(x, y) for x in xs for y in ys}:
        return None
    for curve, following in zip(curves, curves[1:] + curves[:1]):
        if curve['end'] != following['start'] or (curve['start'][0] != curve['end'][0] and curve['start'][1] != curve['end'][1]):
            return None
    return xs[0], ys[0], xs[1], ys[1]


def _rectangle_union_curves(rectangles):
    """Exact axis-aligned cell union; retain original coordinates, no tessellation."""
    xs = sorted({r[i] for r in rectangles for i in (0, 2)})
    ys = sorted({r[i] for r in rectangles for i in (1, 3)})
    # A near-degenerate user-edited recipe is left unchanged for SW to diagnose.
    if any(b-a < 1e-8 for axis in (xs, ys) for a, b in zip(axis, axis[1:])):
        return None
    occupied = {(i, j) for i in range(len(xs)-1) for j in range(len(ys)-1)
                if any(r[0] < (xs[i]+xs[i+1])/2 < r[2]
                       and r[1] < (ys[j]+ys[j+1])/2 < r[3] for r in rectangles)}
    following = {}

    def edge(a, b):
        if a in following:  # point-touching boundaries are not one simple contour
            return False
        following[a] = b
        return True

    for i, j in sorted(occupied):
        boundaries = [((i, j-1), (i, j), (i+1, j)),
                      ((i+1, j), (i+1, j), (i+1, j+1)),
                      ((i, j+1), (i+1, j+1), (i, j+1)),
                      ((i-1, j), (i, j+1), (i, j))]
        for neighbour, a, b in boundaries:
            if neighbour not in occupied and not edge(a, b):
                return None
    result = []
    while following:
        start = min(following)
        loop, current = [], start
        while current in following:
            loop.append(current)
            current = following.pop(current)
            if current == start:
                break
        if current != start or len(loop) < 4:
            return None
        changed = True
        while changed and len(loop) >= 4:
            changed = False
            for index, vertex in enumerate(loop):
                a, c = loop[index-1], loop[(index+1) % len(loop)]
                if a[0] == vertex[0] == c[0] or a[1] == vertex[1] == c[1]:
                    loop.pop(index)
                    changed = True
                    break
        result.extend(_polygon([[xs[i], ys[j]] for i, j in loop]))
    return result


def _optimization_group(feature):
    name = feature['name']
    head, _, tail = name.rpartition('_')
    if feature['kind'] == 'boss' and name.startswith('Flange_Reinforcement_') and tail.isdigit():
        return 'boss_loops', 'Flange_Reinforcements'
    if feature['kind'] != 'cut':
        return None
    # The concave five-rectangle union failed a native SolidWorks comparison.
    # Combine disjoint contours and keep the overlapping bridge arms separate.
    if name.startswith('Side_Oil_Groove_'):
        if tail in ('Vertical', 'InnerTop', 'InnerBottom'):
            return ('side_loops', 'Side_Oil_Groove_Active') if _rectangle_bounds(feature) else None
        if tail in ('Top', 'Bottom'):
            return ('side_loops', 'Side_Oil_Groove_Bridge') if _rectangle_bounds(feature) else None
    if name.startswith('Split_U_Groove_') and tail in ('Left', 'Right', 'Bar'):
        return ('rectangles', head) if _rectangle_bounds(feature) else None
    if len(feature['curves']) != 1 or feature['curves'][0]['kind'] != 'circle':
        return None
    if name.startswith('Flange_Fastener_Hole_') and tail.isdigit():
        return 'circles', 'Flange_Fastener_Holes'
    if name.startswith('Inspection_Screw_Hole_') and tail.isdigit():
        return 'circles', 'Inspection_Screw_Holes'
    if head in ('High_Counterbore', 'Low_Counterbore') and tail in ('-1', '1'):
        return 'circles', 'Bearing_Counterbores_' + tail
    return None


def optimize_housing_features(features):
    """Combine only equal-station, equal-depth cuts within additive-feature barriers.

    U grooves use the exact union of the original rectangular tools, including
    portions that happen to miss the current solid. Side grooves combine only
    disjoint rectangles; crossing bridge cuts remain a separate feature.
    Repeated disjoint holes stay exact circles in one multi-contour sketch.
    Legacy JSON recipes receive the same optimization in C#.
    """
    result, consumed = [], set()
    if any(f['name'].startswith('Side_Oil_Groove_') and f['name'].endswith('_Continuous') for f in features):
        raise ValueError('Combined side oil grooves are not supported by the SolidWorks kernel; re-export from the original housing recipe.')
    for index, feature in enumerate(features):
        if index in consumed:
            continue
        group = _optimization_group(feature)
        if group is None:
            result.append(feature)
            continue
        station = tuple(feature['origin'] + list(feature['u']) + list(feature['v']) + [feature['depth']])
        candidates = []
        for j in range(index, len(features)):
            other = features[j]
            if group[0] == 'boss_loops':
                if other['kind'] != 'boss':
                    break
            elif other['kind'] not in ('cut', 'revolve_cut'):
                break
            other_station = tuple(other['origin'] + list(other['u']) + list(other['v']) + [other['depth']])
            if j not in consumed and _optimization_group(other) == group and other_station == station:
                candidates.append(j)
        if len(candidates) < 2:
            result.append(feature)
            continue
        originals = [features[j] for j in candidates]
        if group[0] == 'boss_loops':
            def bounds(item):
                points = [p for curve in item['curves'] for p in
                          (curve.get('start'), curve.get('end'), curve.get('mid')) if p is not None]
                return min(p[0] for p in points), min(p[1] for p in points), max(p[0] for p in points), max(p[1] for p in points)
            boxes = [bounds(item) for item in originals]
            overlap = any(min(a[2], b[2]) > max(a[0], b[0]) + 1e-8 and
                          min(a[3], b[3]) > max(a[1], b[1]) + 1e-8
                          for i, a in enumerate(boxes) for b in boxes[i+1:])
            curves = None if overlap else [curve for item in originals for curve in item['curves']]
        elif group[0] == 'side_loops':
            boxes = [_rectangle_bounds(item) for item in originals]
            overlap = any(min(a[2], b[2]) >= max(a[0], b[0])-1e-8 and
                          min(a[3], b[3]) >= max(a[1], b[1])-1e-8
                          for i, a in enumerate(boxes) for b in boxes[i+1:])
            curves = None if overlap else [curve for item in originals for curve in item['curves']]
        elif group[0] == 'rectangles':
            curves = _rectangle_union_curves([_rectangle_bounds(f) for f in originals])
        else:
            circles = [f['curves'][0] for f in originals]
            overlap = any(math.dist(a['center'], b['center']) <= a['radius'] + b['radius'] + 1e-9
                          for i, a in enumerate(circles) for b in circles[i+1:])
            curves = None if overlap else circles
        if curves is None:
            result.append(feature)
            continue
        combined = deepcopy(feature)
        combined['name'] = (group[1] + '_Continuous' if group[0] == 'rectangles' else
                            group[1] + '_Combined' if group[0] == 'side_loops' else
                            feature['name'] + '_Combined')
        combined['curves'] = curves
        combined['source_features'] = [f['name'] for f in originals]
        result.append(combined)
        consumed.update(candidates)
    return result


def build_housing(p, optimize=True):
    from housing_height import height_layout
    from reinforcement_layout import large_pad_center_x
    from vent_layout import cover_vent_layout, VENT_FIELDS

    def number(name, fallback):
        try:
            return float(os.environ.get(name, fallback))
        except (ValueError, TypeError):
            return fallback

    center_distance = max(number('REDUCER_CENTER_DISTANCE', 55), 20)
    module = max(number('REDUCER_MODULE', 2), .1)
    teeth_high, teeth_low = max(number('REDUCER_Z1', 17), 6), max(number('REDUCER_Z2', 42), 6)
    gear_type = os.environ.get('REDUCER_GEAR_TYPE', 'spur')
    beta = number('REDUCER_HELIX_ANGLE', 0) if gear_type == 'helical' else 0
    cosine = max(math.cos(math.radians(beta)), .01)
    pinion_tip, gear_tip = module*teeth_high/cosine+2*module, module*teeth_low/cosine+2*module
    small_d, large_d = max(94, pinion_tip+24), max(118, gear_tip+24)
    extra = (small_d+large_d-94-118)/2
    axis = height_layout(max(pinion_tip, gear_tip)/2, os.environ.get('REDUCER_HOUSING_BOTTOM_CLEARANCE', 12))['axis_height']
    length = max(160, center_distance+105+extra)
    plate_length = max(197, center_distance+142+extra)
    width = p.AXIAL_LAYOUT['outer_width']
    cavity_depth = p.AXIAL_LAYOUT['inner_width']
    wall_width = cavity_depth+6
    plate_width = width-4
    wall_height = axis-16
    large_x = plate_length/2-max(77, large_d/2+18)
    small_x = large_x-center_distance
    small_bore, large_bore = p.HIGH_BEARING['outer_diameter'], p.LOW_BEARING['outer_diameter']
    small_boss_r, large_boss_r = (small_bore+18)/2, (large_bore+18)/2
    pad_right = large_pad_center_x(large_x, large_boss_r)
    pad_left, pad_y = -length/2+8, plate_width/2-12
    flange_bottom = axis-6
    bottom = []

    def add(out, name, curves, origin, basis, depth, kind='boss', axis_line=None):
        out.append(_feature(name, kind, curves, origin, basis, depth, axis_line))

    add(bottom, 'Base_R10', _rounded(0, 0, length, width, 10), [0, 0, 0], XY, 10)
    add(bottom, 'Center_Wall', _rectangle(0, 0, length, wall_width), [0, 0, 9.95], XY, wall_height+.1)
    add(bottom, 'Split_Flange_R10', _rounded(0, 0, plate_length, plate_width, 10), [0, 0, flange_bottom-.05], XY, 6.05)
    for name, cx, radius in [('High', small_x, small_boss_r), ('Low', large_x, large_boss_r)]:
        profile = [_line([cx-radius, axis], [cx+radius, axis]),
                   _arc([cx+radius, axis], [cx, axis-radius], [cx-radius, axis])]
        add(bottom, name+'_Lower_Bearing_Boss', profile, [0, width/2, 0], XZ, width)
    connect_z = axis-19
    left = small_x+math.sqrt(small_boss_r**2-19**2)
    right = large_x-math.sqrt(large_boss_r**2-19**2)
    add(bottom, 'Bearing_Bridge', _rectangle((left+right)/2, 0, max(1, right-left), width), [0, 0, connect_z], XY, 19)
    for name, cx, bore in [('High', small_x, small_bore), ('Low', large_x, large_bore)]:
        add(bottom, name+'_Bearing_Bore', _circle(cx, axis, bore/2), [0, width/2, 0], XZ, width, 'cut')
    add(bottom, 'Gear_Cavity', _rectangle(0, 10+(wall_height+6)/2, length-12, wall_height+6+.1), [0, (cavity_depth+.1)/2, 0], XZ, cavity_depth+.1, 'cut')
    rib_depth = (width-cavity_depth)/2-5
    for name, cx, radius in [('High', small_x, small_boss_r), ('Low', large_x, large_boss_r)]:
        for side in [-1, 1]:
            cy = side*(cavity_depth/2+rib_depth/2)
            add(bottom, name+'_Boss_Rib_'+str(side), _rectangle(cx, cy, 6, rib_depth), [0, 0, 10], XY, max(1, axis-radius-10))
    add(bottom, 'Bottom_Relief_Slot', _rectangle(0, 0, 94, width+4), [0, 0, 0], XY, 4, 'cut')
    for label, root_x, direction, diameter, center_z in [('Right', length/2, 1, 30, 15), ('Left', -length/2, -1, 40, 22)]:
        add(bottom, label+'_Side_Boss', _circle(0, center_z, diameter/2), [root_x-direction*.05, 0, 0], YZ, direction*3.05)
    for i, (cx, cy, side) in enumerate([(pad_left, pad_y, 'left'), (pad_left, -pad_y, 'left'), (pad_right, pad_y, 'right'), (pad_right, -pad_y, 'right')]):
        add(bottom, 'Flange_Reinforcement_'+str(i+1), _d_pad(cx, cy, 12, 18, side), [0, 0, flange_bottom-12], XY, 12)
    # The source re-cuts the high bore in added lower pads.
    add(bottom, 'High_Bore_Pad_Clearance', _circle(small_x, axis, small_bore/2), [0, width/2, 0], XZ, width, 'cut')
    lower_holes = [(pad_left, pad_y, 9), (pad_left, -pad_y, 9), (pad_right, pad_y, 9), (pad_right, -pad_y, 9), (-plate_length/2+9, 0, 9), (plate_length/2-10, 0, 9)]
    for i, (x, y, diameter) in enumerate(lower_holes):
        add(bottom, 'Flange_Fastener_Hole_'+str(i+1), _circle(x, y, diameter/2), [0, 0, flange_bottom-12-.05], XY, 18.1, 'cut')
    dowels = [(-plate_length/2+9, -29.5), (plate_length/2-10, 35.5)]
    for i, (x, y) in enumerate(dowels):
        z0, z1 = axis-13.75, axis+.05
        r0, r1 = 2., (4+(z1-axis+13.75)/50)/2
        profile = _polygon([[x, z0], [x+r0, z0], [x+r1, z1], [x, z1]])
        add(bottom, 'Taper_Dowel_'+str(i+1), profile, [0, y, 0], XZ, 0, 'revolve_cut', [x, z0-1, x, z1+1])
    for name, cx, bore in [('High', small_x, small_bore), ('Low', large_x, large_bore)]:
        for side in [-1, 1]:
            center_y = side*(width/2-4-1.5)
            add(bottom, name+'_Counterbore_'+str(side), _circle(cx, axis, (bore+5)/2), [0, center_y+1.5, 0], XZ, 3, 'cut')
    for name, root_x, direction, z, major, tap in [('Left_M20', -length/2, -1, 22, 20, 18.5), ('Right_M8', length/2, 1, 15, 8, 6.8)]:
        outside_x = root_x+direction*3.05
        add(bottom, name+'_Tap_Drill', _circle(0, z, tap/2), [outside_x, 0, 0], YZ, -direction*12.05, 'cut')
        add(bottom, name+'_Major_Diameter', _circle(0, z, major/2), [outside_x, 0, 0], YZ, -direction*10.05, 'cut')
    groove_left, groove_right = small_x+small_bore/2, large_x-large_bore/2
    groove_y = cavity_depth/2+4+7
    for side in [-1, 1]:
        center_y = side*groove_y
        for name, cx, cy, lx, wy in [('Left', groove_left+1.5, center_y, 3, 14), ('Right', groove_right-1.5, center_y, 3, 14), ('Bar', (groove_left+groove_right)/2, center_y-side*(7-1.5), groove_right-groove_left, 3)]:
            add(bottom, 'Split_U_Groove_'+str(side)+'_'+name, _rectangle(cx, cy, lx, wy), [0, 0, axis-1], XY, 1.05, 'cut')
    half_inner_x = length/2-6
    # Keep at least 0.5 mm between each outer groove bridge and the adjacent
    # Ø9 flange clearance hole. With the narrow legacy housing the old 3 mm
    # bridge reached 0.5 mm into that hole.
    bridge_inner_y = cavity_depth/2+4
    bridge_outer_y = min(cavity_depth/2+7, pad_y-5)
    if bridge_outer_y-bridge_inner_y < .5:
        raise ValueError('箱体宽度不足，法兰孔与回油槽无法保留 0.5 mm 净距')
    bridge_y = (bridge_inner_y+bridge_outer_y)/2
    bridge_width = bridge_outer_y-bridge_inner_y
    for side in [-1, 1]:
        vertical_x = side*(half_inner_x+5.5)
        inner_x = small_x-small_bore/2-1.5 if side < 0 else large_x+large_bore/2+1.5
        top_y = cavity_depth/2+5.5
        pieces = [('Vertical', vertical_x, 0, 3, 2*top_y+3),
                  ('Top', (vertical_x+inner_x)/2, bridge_y, abs(inner_x-vertical_x), bridge_width),
                  ('Bottom', (vertical_x+inner_x)/2, -bridge_y, abs(inner_x-vertical_x), bridge_width),
                  ('InnerTop', inner_x, groove_y, 3, 14), ('InnerBottom', inner_x, -groove_y, 3, 14)]
        for name, cx, cy, lx, wy in pieces:
            add(bottom, 'Side_Oil_Groove_'+str(side)+'_'+name, _rectangle(cx, cy, lx, wy), [0, 0, axis-1], XY, 1.05, 'cut')
    add(bottom, 'Sloped_Floor_Oil_Channel', _polygon([[-74, 10.3], [71, 10.3], [71, 8.73]]), [0, cavity_depth/2, 0], XZ, cavity_depth, 'cut')

    vent = cover_vent_layout(dict(moduleM=module, z1=teeth_high, z2=teeth_low, helixAngle=beta,
                                  pinionFaceWidth=max(number('REDUCER_PINION_FACE_WIDTH', 32), 1),
                                  gearFaceWidth=max(number('REDUCER_GEAR_FACE_WIDTH', 24), 1),
                                  **{key: os.environ.get(env, default) for key, (env, default, _, _) in VENT_FIELDS.items()}),
                             dict(gearType=gear_type), center_distance=center_distance, inner_width=cavity_depth)
    top = []
    add(top, 'Split_Flange_R10', _rounded(0, 0, plate_length, plate_width, 10), [0, 0, 0], XY, 6)
    add(top, 'Outer_Tangent_Arc_Shell', _outer_profile(small_x, large_x, small_d/2, large_d/2), [0, wall_width/2, 0], XZ, wall_width)
    add(top, 'Upper_Bearing_Bridge', _upper_boss_profile(small_x, large_x, small_boss_r, large_boss_r), [0, width/2, 0], XZ, width)
    add(top, 'Vent_Raised_Seat', _circle(vent['x'], vent['y'], 10), [0, 0, vent['boss_bottom_z']], XY, vent['seat_z']-vent['boss_bottom_z'])
    delta = (large_d/2-small_d/2)/center_distance
    c = math.sqrt(1-delta*delta)
    slope = delta/c
    small_tangent_x = small_x-delta*small_d/2
    intercept = small_d/2*c-slope*small_tangent_x
    tangent_x = large_x-39
    tangent_origin = [tangent_x, 0., slope*tangent_x+intercept]
    tangent_basis = ([c, 0., delta], [0., 1., 0.])
    normal = [-delta, 0., c]
    add(top, 'Inspection_Raised_Pad', _rounded(0, 0, 44, 37, 2), tangent_origin, tangent_basis, 2)

    def roof(x):
        large_tangent_x = large_x-delta*large_d/2
        if small_tangent_x <= x <= large_tangent_x:
            return slope*x+intercept
        cx, r = (small_x, small_d/2) if x < small_tangent_x else (large_x, large_d/2)
        return math.sqrt(max(0., r*r-(x-cx)**2))

    for label, cx, radius in [('High', small_x, small_boss_r), ('Low', large_x, large_boss_r)]:
        for side in [-1, 1]:
            inner_y, outer_y = side*wall_width/2, side*(width/2-3)
            profile = _polygon([[inner_y, radius-.2], [inner_y, roof(cx)-3], [outer_y, radius-.2]])
            add(top, label+'_Bearing_Cheek_Rib_'+str(side), profile, [cx-3, 0, 0], YZ, 6)
    upper_pad_left = -plate_length/2+26.5
    for i, (cx, cy, side) in enumerate([(upper_pad_left, pad_y, 'left'), (upper_pad_left, -pad_y, 'left'), (pad_right, pad_y, 'right'), (pad_right, -pad_y, 'right')]):
        add(top, 'Flange_Reinforcement_'+str(i+1), _d_pad(cx, cy, 12, 20, side), [0, 0, 6], XY, 11)
    inspection_start = [tangent_origin[i]-50*normal[i] for i in range(3)]
    add(top, 'Inspection_Aperture', _rounded(0, 0, 23, 23, 2), inspection_start, tangent_basis, 100, 'cut')
    # The four M3 pan-head screws pass through the cover and gasket and enter
    # the raised seat. Keep the holes outside the 23 mm inspection opening.
    screw_start = [tangent_origin[i]+2.05*normal[i] for i in range(3)]
    for i, (u, v) in enumerate(((-14,-14),(-14,14),(14,-14),(14,14))):
        add(top, 'Inspection_Screw_Hole_'+str(i+1), _circle(u, v, 1.6),
            screw_start, tangent_basis, -10.1, 'cut')
    add(top, 'Inner_Tangent_Arc_Cavity', _outer_profile(small_x, large_x, small_d/2-6, large_d/2-6), [0, cavity_depth/2, 0], XZ, cavity_depth, 'cut')
    for label, cx, bore in [('High', small_x, small_bore), ('Low', large_x, large_bore)]:
        add(top, label+'_Bearing_Bore', _circle(cx, 0, bore/2), [0, (width+4)/2, 0], XZ, width+4, 'cut')
        for side in [-1, 1]:
            center_y = side*(width/2-4-1.5)
            add(top, label+'_Counterbore_'+str(side), _circle(cx, 0, (bore+5)/2), [0, center_y+1.5, 0], XZ, 3, 'cut')
    upper_holes = [(upper_pad_left, pad_y, 9), (upper_pad_left, -pad_y, 9), (pad_right, pad_y, 9), (pad_right, -pad_y, 9), (-plate_length/2+9, 0, 9), (plate_length/2-10, 0, 9)]
    zmax = max(large_d/2, vent['seat_z'], tangent_origin[2]+22*delta+2*c)
    for i, (x, y, diameter) in enumerate(upper_holes):
        add(top, 'Flange_Fastener_Hole_'+str(i+1), _circle(x, y, diameter/2), [0, 0, -100], XY, 200, 'cut')
    add(top, 'Vent_Through_Bore', _circle(vent['x'], vent['y'], vent['bore_radius']), [0, 0, vent['bore_bottom_z']], XY, vent['seat_z']-vent['bore_bottom_z']+.2, 'cut')
    for i, (x, y) in enumerate(dowels):
        z0, z1 = -.1, zmax+.1
        r0, r1 = (4+(z0+13.75)/50)/2, (4+(z1+13.75)/50)/2
        profile = _polygon([[x, z0], [x+r0, z0], [x+r1, z1], [x, z1]])
        add(top, 'Taper_Dowel_'+str(i+1), profile, [0, y, 0], XZ, 0, 'revolve_cut', [x, z0-1, x, z1+1])
    return dict(lower=optimize_housing_features(bottom) if optimize else bottom,
                upper=optimize_housing_features(top) if optimize else top, cover_offset_z=axis,
                expected_lower_bounds=[-plate_length/2, -width/2, 0., plate_length/2, width/2, axis],
                expected_upper_bounds=[-plate_length/2, -width/2, 0., plate_length/2, width/2, zmax],
                source='active restored parametric_housing_start.py and parametric_cover_start.py',
                limitations=['Housing sketch profiles are native and editable but are not fully constrained.',
                             'Reference-plane locations are explicit regenerated datums rather than linked dimensions.',
                             'The source left side-boss toroidal root relief is not migrated; the straight cylindrical boss is retained.',
                             'Side threaded holes retain the source simplified tap/major-diameter steps, not physical helical threads.'])


if __name__ == '__main__':
    import json
    from pathlib import Path
    import sys
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'model_generator'))
    import shaft_params
    data = build_housing(shaft_params)
    print(json.dumps(dict(lower_features=len(data['lower']), upper_features=len(data['upper']),
                          cover_offset_z=data['cover_offset_z'], lower_bounds=data['expected_lower_bounds'],
                          upper_bounds=data['expected_upper_bounds']), indent=2))
