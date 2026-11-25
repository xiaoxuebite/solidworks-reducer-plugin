"""Validate the restored housing's analytic feature recipe without a CAD kernel."""
import math
import json
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parents[1]
sys.path[:0] = [str(ROOT), str(ROOT.parent / 'model_generator')]
from export_housing import build_housing
import shaft_params


def closed_profile(feature):
    curves = feature['curves']
    first = previous = None
    for current in curves:
        if current['kind'] == 'circle':
            assert first is None, feature['name']
            assert math.isfinite(current['radius']) and current['radius'] > 0
            continue
        assert current['kind'] in ('line', 'arc')
        if first is None:
            first = current['start']
        else:
            assert max(abs(a-b) for a, b in zip(previous, current['start'])) < 1e-8, feature['name']
        previous = current['end']
        if max(abs(a-b) for a, b in zip(first, previous)) < 1e-8:
            first = previous = None
        if current['kind'] == 'arc':
            a, b, c = current['start'], current['mid'], current['end']
            cross = (b[0]-a[0])*(c[1]-a[1]) - (b[1]-a[1])*(c[0]-a[0])
            assert abs(cross) > 1e-8
    assert first is None, feature['name']


def taper_diameter_at(feature, level):
    lower, upper = feature['curves'][1]['start'], feature['curves'][1]['end']
    axis_x = feature['axis'][0]
    radius = lower[0]-axis_x + (upper[0]-lower[0])*(level-lower[1])/(upper[1]-lower[1])
    return radius * 2


spec = build_housing(shaft_params)
for features in (spec['lower'], spec['upper']):
    assert features[0]['kind'] == 'boss'
    assert len({feature['name'] for feature in features}) == len(features)
    for feature in features:
        closed_profile(feature)
        assert all(math.isfinite(v) for name in ('origin', 'u', 'v') for v in feature[name])
        assert math.isclose(sum(v*v for v in feature['u']), 1, abs_tol=1e-9)
        assert math.isclose(sum(v*v for v in feature['v']), 1, abs_tol=1e-9)
        assert abs(sum(a*b for a, b in zip(feature['u'], feature['v']))) < 1e-9
        if feature['kind'] != 'revolve_cut':
            assert math.isfinite(feature['depth']) and feature['depth'] != 0
for index in (1, 2):
    lower = next(f for f in spec['lower'] if f['name'] == f'Taper_Dowel_{index}')
    upper = next(f for f in spec['upper'] if f['name'] == f'Taper_Dowel_{index}')
    assert math.isclose(taper_diameter_at(lower, spec['cover_offset_z']), taper_diameter_at(upper, 0), abs_tol=1e-9)
    assert math.isclose(taper_diameter_at(lower, spec['cover_offset_z']), 4.275, abs_tol=1e-9)
for name in ('High_Bearing_Bore', 'Low_Bearing_Bore', 'Inspection_Aperture', 'Vent_Through_Bore'):
    assert any(feature['name'] == name for feature in spec['upper'])
for sample in (spec, json.loads((ROOT / 'parameters_v4.json').read_text(encoding='utf-8-sig'))['housing']):
    lower = sample['lower']
    groove_z = min(feature['origin'][2] for feature in lower if feature['name'].startswith('Side_Oil_Groove_'))
    for feature in lower:
        if feature['name'] in ('Left_M20_Major_Diameter', 'Right_M8_Major_Diameter'):
            circle = feature['curves'][0]
            assert groove_z - (circle['center'][1] + circle['radius']) > 1, feature['name']
    holes = [curve for feature in lower if feature['name'].startswith('Flange_Fastener_Hole_')
             for curve in feature['curves'] if curve['kind'] == 'circle']
    bridges = [feature for feature in lower if feature['name'] == 'Side_Oil_Groove_Bridge_Combined']
    if not bridges:
        bridges = [feature for feature in lower if feature['name'].startswith('Side_Oil_Groove_') and
                   feature['name'].endswith(('_Top', '_Bottom')) and not feature['name'].endswith(('InnerTop', 'InnerBottom'))]
    assert bridges and len(holes) == 6
    for bridge in bridges:
        for index in range(0, len(bridge['curves']), 4):
            loop = bridge['curves'][index:index+4]
            assert len(loop) == 4 and all(curve['kind'] == 'line' for curve in loop)
            xs = [curve['start'][0] for curve in loop]
            ys = [curve['start'][1] for curve in loop]
            xmin, xmax, ymin, ymax = min(xs), max(xs), min(ys), max(ys)
            for hole in holes:
                x, y = hole['center']
                dx = max(xmin-x, 0, x-xmax)
                dy = max(ymin-y, 0, y-ymax)
                gap = math.hypot(dx, dy) - hole['radius']
                assert gap >= .5-1e-8, (bridge['name'], hole['center'], gap)
print(f"Housing analytic recipe / closed arcs / split taper checks: PASS ({len(spec['lower'])} lower, {len(spec['upper'])} upper features)")
