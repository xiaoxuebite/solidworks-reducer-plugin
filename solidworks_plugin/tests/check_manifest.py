"""Independent native volume, part inventory and flat assembly checks (v1-v4)."""
from collections import Counter
import json
import math
from pathlib import Path
import sys

folder = Path(sys.argv[1])
p = json.loads((folder / 'parameters.json').read_text(encoding='utf-8-sig'))
manifest = json.loads((folder / 'parts_manifest.json').read_text(encoding='utf-8-sig'))
parts = {item['file']: item for item in manifest['parts']}
assert len(parts) == len(manifest['parts']), 'Duplicate part metrics'
inventory = {'driven_shaft.SLDPRT', 'low_blank_cover.SLDPRT'}
if p.get('high_blank_cover'):
    inventory.add('high_blank_cover.SLDPRT')
expected = {}
for name, c in [('low_blank_cover', p['blank_cover']), ('high_blank_cover', p.get('high_blank_cover'))]:
    if c:
        expected[name] = math.pi / 4 * (c['outer_diameter'] ** 2 * (c['total_thickness'] - c['step_height'])
                                        + c['step_diameter'] ** 2 * c['step_height']
                                        - c['inner_recess_diameter'] ** 2 * c['recess_depth'])
for r in p.get('accessories', []):
    inventory.add(r['name'] + '.SLDPRT')
    expected[r['name']] = math.pi / 4 * (r['outer_diameter'] ** 2 - r['inner_diameter'] ** 2) * r['thickness']
    expected[r['name']] -= math.pi * r.get('chamfer', 0) ** 2 * (r['outer_diameter'] + r['inner_diameter'])


def section_volume(points):
    # Pappus / exact line integration: pi * integral r(x)^2 dx around the boundary.
    return abs(math.pi / 3 * sum((b[0] - a[0]) * (a[1] ** 2 + a[1] * b[1] + b[1] ** 2)
                                for a, b in zip(points, points[1:] + points[:1])))


for c in p.get('through_covers', []):
    inventory.update((c['name'] + '.SLDPRT', c['name'].replace('_through_cover', '_oil_seal') + '.SLDPRT'))
    length = sum(step[1] for step in c['outer_steps'])
    outside = math.pi / 4 * sum(d ** 2 * width for d, width in c['outer_steps'])
    removed = 0.
    remaining = length
    for start, end, width in c['inner_steps']:
        used = min(remaining, width)
        if used <= 0:
            break
        clipped_end = start + (end - start) * used / width
        removed += math.pi / 12 * used * (start ** 2 + start * clipped_end + clipped_end ** 2)
        remaining -= used
    expected[c['name']] = outside - removed
    steps = c['inner_steps'][:5]
    maximum = max(max(a, b) for a, b, _ in steps)
    x = 0.
    for index, (a, b, width) in enumerate(steps):
        if a == b == maximum:
            left = steps[index - 1][2] if index and steps[index - 1][1] == maximum else 0
            right = steps[index + 1][2] if index + 1 < len(steps) and steps[index + 1][0] == maximum else 0
            center = x + width / 2
            inner_width = width + left + right
            seal = [[x, maximum / 2], [x + width, maximum / 2],
                    [center + inner_width / 2, c['seal_bore_diameter'] / 2],
                    [center - inner_width / 2, c['seal_bore_diameter'] / 2]]
            expected[c['name'].replace('_through_cover', '_oil_seal')] = section_volume(seal)
            break
        x += width
    else:
        raise AssertionError('No reference seal pocket')
for part in p.get('rotary_parts', []):
    inventory.add(part['name'] + '.SLDPRT')
    expected[part['name']] = section_volume(part['section'])

# These are parameter-derived analytic values, not a previously generated model.
auxiliary_residuals = []
for part in p.get('auxiliary') or []:
    name, kind = part['name'], part['kind']
    inventory.add(name + '.SLDPRT')
    if kind == 'key':
        expected[name] = ((part['length'] - part['width']) * part['width']
                          + math.pi * part['width'] ** 2 / 4) * part['height']
    elif kind == 'plate':
        expected[name] = (part['length'] * part['width'] - (4 - math.pi) * part['corner_radius'] ** 2
                          - math.pi * part['hole_diameter'] ** 2 - part['inner_square'] ** 2) * part['height']
    elif part.get('reference_volume_mm3', 0) > 0:
        # The intersecting curved vent bore and spherical shell have tiny kernel
        # integration residuals. Keep explicit absolute tolerances and print them.
        tolerance = {'vent': .02, 'oil_cover': .001}.get(kind, 1e-6)
        delta = parts[name + '.SLDPRT']['volume_mm3'] - part['reference_volume_mm3']
        assert abs(delta) <= tolerance, (name, 'reference volume residual', delta, tolerance)
        auxiliary_residuals.append((name, delta, tolerance))

bearings = p.get('bearings') or []
for bearing in bearings:
    filename = bearing['name'] + '.SLDPRT'
    inventory.add(filename)
    radial_space = (bearing['outer_diameter'] - bearing['inner_diameter']) / 2
    ball_radius = radial_space * .294
    pitch_radius = (bearing['inner_diameter'] + bearing['outer_diameter']) / 4
    ball_count = min(max(int(2 * math.pi * pitch_radius / max(ball_radius * 2.35, 1)), 8), 36)
    actual = parts[filename]
    assert actual['multiple_bodies_allowed'] is True
    assert actual['solid_bodies'] == ball_count + 2, (filename, actual['solid_bodies'], ball_count + 2)

if p.get('gears'):
    inventory.update(('drive_gear_shaft.SLDPRT', 'driven_gear.SLDPRT'))
    geometry = json.loads((folder / 'gear_geometry.json').read_text(encoding='utf-8-sig'))
    for key in ('module', 'pressure_angle', 'helix_angle', 'pinion_teeth', 'driven_teeth',
                'driven_width', 'driven_mesh_phase_deg'):
        assert math.isclose(geometry[key], p['gears'][key], abs_tol=1e-10), key
    assert math.isclose(geometry['pinion_width'], next(s['length'] for s in p['gears']['high_sections']
                                                     if s['kind'] == 'gear'), abs_tol=1e-10)
if p.get('housing'):
    inventory.update(('housing.SLDPRT', 'cover.SLDPRT'))
if p.get('hardware_version') == 1:
    inventory.update((
        'gb_hex_bolt_m8x50.SLDPRT', 'gb_hex_bolt_m8x25.SLDPRT',
        'gb_hex_nut_m8.SLDPRT', 'gb_spring_washer_8.SLDPRT',
        'gb_pan_screw_m3x12.SLDPRT',
        'gb_taper_pin_4x20.SLDPRT', 'gb_oil_plug_m8x1.SLDPRT',
        'gb_flat_washer_8.SLDPRT', 'gb_o_ring_19x1p8.SLDPRT'))
assert set(parts) == inventory, ('Unexpected/missing parts', set(parts) ^ inventory)
for name, item in parts.items():
    assert math.isfinite(item['volume_mm3']) and item['volume_mm3'] > 0, name
    if name not in {b['name'] + '.SLDPRT' for b in bearings}:
        assert item['solid_bodies'] == 1, (name, item['solid_bodies'])
for name, volume in expected.items():
    actual = parts[name + '.SLDPRT']
    assert actual['solid_bodies'] == 1
    assert math.isclose(actual['volume_mm3'], volume, rel_tol=1e-9, abs_tol=1e-6), (name, volume, actual)
if manifest.get('schema_version', 1) >= 2:
    instances = manifest['instances']
    assert manifest['component_count'] == len(instances)
    assert {i['file'] for i in instances} == set(parts)
    assert len({i['name'] for i in instances}) == len(instances)
    assert all(i['file'].endswith('.SLDPRT') for i in instances)
    assert 'seal_modules' not in manifest
    assemblies = [file.name for file in folder.glob('*.SLDASM') if not file.name.startswith('~$')]
    assert assemblies == ['reducer_assembly.SLDASM'], assemblies
    for mate in manifest['top_level_mates']:
        assert mate['coincident_plane_mates'] == 3 and mate['seal_fixed'] is False
        assert mate['seal_constraint_status'] == 3
    assert len(manifest['top_level_mates']) == len(p.get('through_covers', []))
    counts = Counter(i['file'] for i in instances)
    for name in inventory:
        copies = 2 if name in {'oil_retaining_ring.SLDPRT', 'high_bearing.SLDPRT', 'low_bearing.SLDPRT'} else 1
        if p.get('hardware_version') == 1:
            copies = {
                'gb_hex_bolt_m8x50.SLDPRT': 4,
                'gb_hex_bolt_m8x25.SLDPRT': 2,
                'gb_hex_nut_m8.SLDPRT': 6,
                'gb_spring_washer_8.SLDPRT': 6,
                'gb_pan_screw_m3x12.SLDPRT': 4,
                'gb_taper_pin_4x20.SLDPRT': 2,
            }.get(name, copies)
        assert counts[name] == copies, (name, counts[name], copies)
    if p.get('assembly_layout'):
        dat = p['assembly_layout']
        aux = {a['name']: a for a in p.get('auxiliary') or []}
        for instance in instances:
            name = instance['name']
            if p.get('hardware_version') == 1 and instance['file'] in (
                    'oil_level_cover.SLDPRT', 'oil_level_indicator.SLDPRT', 'gb_o_ring_19x1p8.SLDPRT'):
                boss = next(f for f in p['housing']['lower'] if f['name'] == 'Left_Side_Boss')
                seal = next(i for i in instances if i['file'] == 'gb_o_ring_19x1p8.SLDPRT')
                seat = boss['origin'][0] + boss['depth']
                cover = aux['oil_level_cover']
                shoulder = cover['right_vertical_x'] - cover['upper_step_length']
                offsets = {
                    'oil_level_cover.SLDPRT': -shoulder,
                    'oil_level_indicator.SLDPRT': cover['right_vertical_x'] - cover['lower_return_length'] - shoulder,
                    'gb_o_ring_19x1p8.SLDPRT': 0,
                }
                assert math.isclose(seal['x'] + 1.8, seat, abs_tol=1e-8)
                expected = [seal['x'] + offsets[instance['file']], seal['y'], seal['z']]
                assert all(math.isclose(instance[k], expected[j], abs_tol=1e-8)
                           and math.isclose(instance['placement'][9+j], expected[j], abs_tol=1e-8)
                           for j, k in enumerate(('x', 'y', 'z'))), name
            elif name in aux:
                assert instance['placement'] == aux[name]['placement'], name
                assert all(math.isclose(instance[k], aux[name]['placement'][9 + index], abs_tol=1e-8)
                           for index, k in enumerate(('x', 'y', 'z'))), name
            elif name in ('housing', 'cover'):
                z = p['housing']['cover_offset_z'] if name == 'cover' else 0
                assert instance['placement'] == [1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, z], name
                assert instance['x'] == instance['y'] == 0 and instance['z'] == z, name
            elif p.get('hardware_version') == 1 and name.startswith(('flange_', 'inspection_screw_', 'taper_pin_', 'oil_drain_')) or p.get('hardware_version') == 1 and name == 'oil_level_o_ring':
                assert instance['placement'] is not None and len(instance['placement']) == 12, name
                assert all(math.isclose(instance[k], instance['placement'][9+j], abs_tol=1e-8)
                           for j, k in enumerate(('x', 'y', 'z'))), name
            else:
                assert instance['z'] == dat['axis_height'], name
                high = name.startswith(('high_', 'oil_retaining_ring')) or name == 'drive_gear_shaft'
                assert instance['x'] == dat['high_center_x' if high else 'low_center_x'], name
        retaining = [i for i in instances if i['file'] == 'oil_retaining_ring.SLDPRT']
        if 'oil_retaining_ring.SLDPRT' in parts:
            assert len(retaining) == 2
else:
    assert manifest['component_count'] == len(parts)
assert all((folder / filename).is_file() for filename in parts)
for module in manifest.get('seal_modules', []):
    assert module['components'] == 2 and module['coincident_plane_mates'] == 3
    assert module['seal_fixed'] is False and module['seal_constraint_status'] == 3
    assert (folder / module['file']).is_file()
if p.get('through_covers'):
    fit = json.loads((folder / 'fit_report.json').read_text(encoding='utf-8-sig'))
    assert len(fit['checks']) == len(p['through_covers'])
    for item in fit['checks']:
        assert item['min_wall_mm'] > 0 and item['min_shaft_radial_clearance_mm'] >= -1e-8
        assert item['min_seal_cavity_radial_clearance_mm'] >= -1e-8
print(f'Native volume / file / component checks: PASS ({len(expected)} analytical volumes, {len(parts)} parts)')
for name, delta, tolerance in auxiliary_residuals:
    print(f'  {name}: reference residual {delta:+.9g} mm3 (absolute tolerance {tolerance:g} mm3)')
