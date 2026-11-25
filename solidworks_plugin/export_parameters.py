"""Export the existing reducer parameter contract without importing CadQuery."""
import argparse
import json
import math
import os
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parent


def build_parameters():
    """构建 schema v3 参数契约：轴、端盖、调整环、轴套、通盖、油封、总装基准。

    返回值是纯 dict，不写文件，方便 export_all.py 在其上叠加新段。
    """
    # A fresh process ensures environment changes are never hidden by module caching.
    sys.path.insert(0, str(ROOT.parent / 'model_generator'))
    import shaft_params as p
    segments = [dict(s) for s in p.LOW_SHAFT_SEGMENTS]
    keys = []
    for index, prefix, default_length in [(1, 'REDUCER_LOW_GEAR_KEY', 20), (5, 'REDUCER_LOW_OUTER_KEY', 20)]:
        key = p.key_from_env(prefix, segments[index]['diameter'], default_length)
        if index == 1 and prefix not in os.environ:
            key.update(width=6., height=6., depth=3.5, length=20.)
        available = max(float(segments[index]['length']) - 4., 1.)
        length = min(float(key['length']), available)
        offset = min(4., max(float(segments[index]['length']) - length, 0.))
        keys.append(dict(segment_index=index, start=sum(s['length'] for s in segments[:index]) + offset,
                         length=length, width=key['width'], depth=key['depth'],
                         diameter=segments[index]['diameter']))
    accessories = [dict(name='sleeve', outer_diameter=p.SLEEVE_OUTER_DIAMETER,
                        inner_diameter=p.SLEEVE_INNER_DIAMETER, thickness=p.SLEEVE_THICKNESS, chamfer=.5)]
    for name, spec in [('low_adjusting_ring', p.LOW_ADJUSTING_RING), ('high_adjusting_ring', p.HIGH_ADJUSTING_RING)]:
        accessories.append(dict(name=name, **spec, chamfer=0))
    through = []
    for name, spec, bore, segment_index in [
        ('low_through_cover', p.LOW_THROUGH_COVER, p.LOW_SHAFT_SEGMENTS[4]['diameter'], 4),
        ('high_through_cover', p.HIGH_THROUGH_COVER, p.HIGH_SHAFT_SECTIONS[0]['diameter'], None),
    ]:
        through.append(dict(name=name, outer_steps=spec['outer_steps'], inner_steps=spec['inner_steps'],
                            seal_bore_diameter=bore, seal_shaft_segment_index=segment_index))
    bore = p.HIGH_BEARING['inner_diameter'] / 2
    outer = p.HIGH_BEARING['outer_diameter'] / 2
    raised = min(outer * 2 - 4, bore * 2 + 8) / 2
    step = min(outer * 2 - 3, bore * 2 + 10) / 2
    lower = min(outer * 2 - 2, bore * 2 + 12) / 2
    rotary = [dict(name='oil_retaining_ring', section=[
        [1., bore], [4., bore], [4., raised], [3., step],
        [3., outer], [0., outer], [0., lower], [1., step],
    ])]
    # Datums follow the restored CadQuery housing/cover scripts, not the alternate
    # cast-housing layout. No CadQuery import or model build is required.
    from housing_height import height_layout
    center_distance = max(p.env_float('REDUCER_CENTER_DISTANCE', 55), 20)
    small_tip = p.MODULE * p.Z1 / p.HELIX_COS + 2 * p.MODULE
    large_teeth = max(p.env_float('REDUCER_Z2', 42), 6)
    large_tip = p.MODULE * large_teeth / p.HELIX_COS + 2 * p.MODULE
    small_envelope = max(94, small_tip + 24)
    large_envelope = max(118, large_tip + 24)
    extra = (small_envelope + large_envelope - 94 - 118) / 2
    plate_length = max(197, center_distance + 142 + extra)
    low_center = plate_length / 2 - max(77, large_envelope / 2 + 18)
    assembly_layout = dict(
        low_center_x=low_center, high_center_x=low_center-center_distance,
        axis_height=height_layout(max(small_tip, large_tip)/2, os.environ.get('REDUCER_HOUSING_BOTTOM_CLEARANCE', 12))['axis_height'],
        boss_depth=p.AXIAL_LAYOUT['outer_width'], mesh_center_y=p.AXIAL_LAYOUT['mesh_center_y'],
        low_bearing_width=p.LOW_BEARING['width'], high_bearing_width=p.HIGH_BEARING['width'],
        source='parametric_cover_start.py / parametric_housing_start.py / parametric_reducer_assembly.py')
    payload = dict(schema_version=3, units='mm', source='model_generator/shaft_params.py',
                   shaft_segments=segments, keyways=keys,
                   blank_cover=dict(p.LOW_BLANK_COVER, total_thickness=7., step_height=4., recess_depth=.5),
                   high_blank_cover=dict(p.HIGH_BLANK_COVER, total_thickness=7., step_height=4., recess_depth=.5),
                   accessories=accessories, through_covers=through, rotary_parts=rotary, assembly_layout=assembly_layout)
    for segment in segments:
        if any(not math.isfinite(float(v)) or float(v) <= 0 for v in segment.values()):
            raise ValueError('Shaft dimensions must be finite and positive')
    return payload


def write_payload(payload, output):
    output = Path(output)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(payload, ensure_ascii=False, indent=2, allow_nan=False), encoding='utf-8')
    print(output)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, default=ROOT / 'parameters_v3.json')
    args = parser.parse_args()
    write_payload(build_parameters(), args.output)


if __name__ == '__main__':
    main()
