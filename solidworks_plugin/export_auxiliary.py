"""Pure-Python contracts for the active CadQuery assembly's auxiliary parts.

All placement arrays are SW row-vector basis rows (9 values), followed by a
translation in millimetres (3 values).  No CadQuery import or model build.
"""
import math
import os


def _matrix(x_axis, y_axis, z_axis, origin):
    return list(x_axis) + list(y_axis) + list(z_axis) + list(origin)


def _circle_intersection(radius_a, radius_b, distance):
    a, b, d = radius_a, radius_b, distance
    if d >= a + b:
        return 0.
    if d <= abs(a - b):
        return math.pi * min(a, b) ** 2
    return (a*a*math.acos((d*d+a*a-b*b)/(2*d*a))
            + b*b*math.acos((d*d+b*b-a*a)/(2*d*b))
            - .5*math.sqrt((-d+a+b)*(d+a-b)*(d-a+b)*(d+a+b)))


def _simpson(function, low, high, count=8192):
    if high <= low:
        return 0.
    step = (high-low)/count
    return step/3*(function(low)+function(high)
                   + 4*sum(function(low+i*step) for i in range(1,count,2))
                   + 2*sum(function(low+i*step) for i in range(2,count,2)))


def _revolution_volume(points):
    return abs(math.pi/3*sum((b[0]-a[0])*(a[1]**2+a[1]*b[1]+b[1]**2)
                            for a,b in zip(points,points[1:]+points[:1])))


def _oil_cover_reference_volume():
    # Exact revolved shell integration, followed by a one-dimensional integration
    # of the six flats' circular segments.  Independent of the SW feature code.
    radius, inner_radius, height, inner_height = 30.,27.,13.5,6.
    x_outer = radius-math.sqrt(radius*radius-height*height)
    x_inner = radius-math.sqrt(inner_radius*inner_radius-inner_height*inner_height)
    x_axis = radius-inner_radius
    outside = radius*x_outer*x_outer-x_outer**3/3+height**2*(8-x_outer)+10**2*(18-8)
    inside = (inner_radius**2*(x_inner-x_axis)-((x_inner-radius)**3-(x_axis-radius)**3)/3
              + inner_height**2*(14-x_inner)+7.5**2*(18-14))
    circumradius = 13.5
    apothem = circumradius*math.cos(math.pi/6)
    x_starts = radius-math.sqrt(radius*radius-apothem*apothem)

    def removed_at(x):
        r = math.sqrt(max(0.,2*radius*x-x*x))
        if r <= apothem:
            return 0.
        # For r <= circumradius, adjacent exterior circular segments do not overlap.
        return 6*(r*r*math.acos(min(1.,apothem/r))-apothem*math.sqrt(max(0.,r*r-apothem*apothem)))

    trim = _simpson(removed_at,x_starts,x_outer)
    hex_area = 3*math.sqrt(3)/2*circumradius*circumradius
    trim += (5-x_outer)*(math.pi*height*height-hex_area)
    return math.pi*(outside-inside)-trim


def build_auxiliary(p):
    from housing_height import height_layout
    from vent_layout import cover_vent_layout, VENT_FIELDS

    module = max(p.env_float('REDUCER_MODULE', 2), .1)
    z1, z2 = max(p.env_float('REDUCER_Z1', 17), 6), max(p.env_float('REDUCER_Z2', 42), 6)
    cosine = max(math.cos(math.radians(p.HELIX_ANGLE)), .01)
    distance = max(p.env_float('REDUCER_CENTER_DISTANCE', 55), 20)
    small_radius = max(47, module*z1/(2*cosine)+module+12)
    large_radius = max(59, module*z2/(2*cosine)+module+12)
    plate_length = max(197, distance+142+small_radius+large_radius-106)
    low_x = plate_length/2-max(77, large_radius+18)
    high_x = low_x-distance
    axis_z = height_layout(max(module*z1/cosine+2*module, module*z2/cosine+2*module)/2,
                           os.environ.get('REDUCER_HOUSING_BOTTOM_CLEARANCE', 12))['axis_height']
    mesh_y = p.AXIAL_LAYOUT['mesh_center_y']
    low_origin = mesh_y+p.LOW_SHAFT_SEGMENTS[0]['length']+p.LOW_SHAFT_SEGMENTS[1]['length']/2
    high_start = 0.
    for section in p.HIGH_SHAFT_SECTIONS:
        if section['kind'] == 'gear':
            high_origin = mesh_y+high_start+section['length']/2
            break
        high_start += section['length']
    else:
        raise ValueError('High-speed shaft has no integral gear section')
    result = []
    key_inputs = [
        ('key_driven_gear', 'low', 1, 'REDUCER_LOW_GEAR_KEY', 20., p.LOW_SHAFT_SEGMENTS, low_origin, low_x),
        ('key_driven_outer', 'low', 5, 'REDUCER_LOW_OUTER_KEY', 20., p.LOW_SHAFT_SEGMENTS, low_origin, low_x),
        ('key_drive_outer', 'high', 0, 'REDUCER_HIGH_KEY', 18., p.HIGH_SHAFT_SECTIONS, high_origin, high_x),
    ]
    for name, link, index, prefix, default_length, segments, origin_y, origin_x in key_inputs:
        spec = p.key_from_env(prefix, segments[index]['diameter'], default_length)
        if name == 'key_driven_gear' and prefix not in os.environ:
            spec.update(width=6., height=6., depth=3.5, length=20.)
        available = max(float(segments[index]['length'])-4., 1.)
        length = min(float(spec['length']), available)
        start_offset = min(4., max(float(segments[index]['length'])-length, 0.))
        width, height, diameter, depth = spec['width'], spec['height'], segments[index]['diameter'], spec['depth']
        start = sum(s['length'] for s in segments[:index])+start_offset
        result.append(dict(name=name, kind='key', length=length, width=width, height=height,
                           start_x=start, start_z=diameter/2-depth, shaft_link=link,
                           segment_index=index, start_offset=start_offset,
                           shaft_diameter=diameter, shaft_slot_depth=depth,
                           placement=_matrix((0,-1,0),(1,0,0),(0,0,1),(origin_x,origin_y,axis_z)),
                           reference_volume_mm3=((length-width)*width+math.pi*width*width/4)*height))

    ratio = (large_radius-small_radius)/distance
    if abs(ratio) >= 1:
        raise ValueError('Inspection-cover tangent cannot be constructed')
    horizontal = math.sqrt(1-ratio*ratio)
    slope = ratio/horizontal
    small_tangent_x = high_x-ratio*small_radius
    intercept = small_radius*horizontal-slope*small_tangent_x
    center_x = low_x-39.
    center_z = slope*center_x+intercept
    tangent, normal = (horizontal,0,ratio), (-ratio,0,horizontal)
    rounded_area = 36*34-(4-math.pi)*3*3
    hole_area = 4*math.pi*3.2**2/4
    for name, thickness, offset, inner_square in [('inspection_cover',3.,2.5,0.), ('inspection_cover_gasket',.5,2.,23.)]:
        result.append(dict(name=name, kind='plate', length=36., width=34., height=thickness,
                           corner_radius=3., hole_diameter=3.2, hole_pitch_x=28., hole_pitch_y=28., inner_square=inner_square,
                           placement=_matrix(tangent,(0,1,0),normal,(center_x+normal[0]*offset,0,axis_z+center_z+normal[2]*offset)),
                           reference_volume_mm3=(rounded_area-hole_area-inner_square**2)*thickness))

    inputs = dict(moduleM=module,z1=z1,z2=z2,helixAngle=p.HELIX_ANGLE,
                  pinionFaceWidth=p.PINION_FACE_WIDTH,gearFaceWidth=p.GEAR_FACE_WIDTH,
                  **{key:os.environ.get(env, default) for key,(env,default,_,_) in VENT_FIELDS.items()})
    vent = cover_vent_layout(inputs, {'gearType':p.GEAR_TYPE}, center_distance=distance, inner_width=p.AXIAL_LAYOUT['inner_width'])
    vent_section = [[0,0],[0,4],[1,5],[10,5],[10,4],[12,4],[12,9.5],[20,9.5],[22,11.5],[29,11.5],[30,10.5],[30,0]]
    vent_bore = [[0,0],[0,1.5],[20,1.5],[21,0]]
    # Radial-hole material is the negative-Z half of the side cylinder, excluding
    # its overlap with the central bore (radius 1.5 on x=15.5..18.5).
    a, shoulder = 1.5,9.5
    radial_removal = 4*a*a*_simpson(lambda t:math.cos(t)**2*math.sqrt(shoulder**2-a*a*math.sin(t)**2),0,math.pi/2)-8*a**3/3
    result.append(dict(name='vent_plug',kind='vent',section=vent_section,
                       axial_hole_profile=vent_bore,
                       side_hole_diameter=3.,side_hole_x=17.,side_hole_depth=9.7,
                       placement=_matrix((0,0,1),(0,1,0),(-1,0,0),(vent['x'],vent['y'],axis_z+vent['seat_z']-12)),
                       notes='Source THREAD_RADIUS/HEIGHT is a plain cylinder; helical/cosmetic thread is not added.',
                       reference_volume_mm3=_revolution_volume(vent_section)-_revolution_volume(vent_bore)-radial_removal))

    length = max(160,distance+105+small_radius+large_radius-106)
    oil_cover_x = -length/2-3-8-.9
    result.append(dict(name='oil_level_cover',kind='oil_cover',outer_arc_radius=30.,inner_arc_radius=27.,
                       outer_arc_height=13.5,inner_arc_height=6.,right_vertical_x=18.,upper_step_length=10.,lower_return_length=4.,
                       drop_1=3.5,drop_2=2.5,drop_3=1.5,hex_diameter=27.,hex_offset=2.,
                       placement=_matrix((1,0,0),(0,1,0),(0,0,1),(oil_cover_x,0,22)),
                       reference_volume_mm3=_oil_cover_reference_volume()))
    inner, outer, depth, front = 5.5, 7.5, 4., 1.5
    lobe_area = math.pi*inner**2-4*_circle_intersection(inner,4.,outer)
    result.append(dict(name='oil_level_indicator',kind='indicator',inner_diameter=11.,outer_diameter=15.,
                       height=depth,front_thickness=front,scallop_radius=4.,
                       placement=_matrix((1,0,0),(0,1,0),(0,0,1),(oil_cover_x+14,0,22)),
                       reference_volume_mm3=math.pi*(outer**2-inner**2)*depth+lobe_area*front))
    return result
