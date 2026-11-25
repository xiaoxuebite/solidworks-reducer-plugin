"""Check the CLI's parameter boundary without opening or editing SW documents."""
import copy
import json
from pathlib import Path
import subprocess
import tempfile
import sys

root = Path(__file__).resolve().parents[1]
launcher = Path(sys.argv[1])
base = json.loads((root / ('parameters_v3.json' if (root / 'parameters_v3.json').exists() else 'parameters_v2.json')).read_text(encoding='utf-8'))
cases = []
for label, modify in [
    ('negative shaft length', lambda p: p['shaft_segments'][0].update(length=-1)),
    ('key outside shaft', lambda p: p['keyways'][0].update(start=1000)),
    ('invalid ring bore', lambda p: p['accessories'][0].update(inner_diameter=1000)),
    ('path traversal', lambda p: p['accessories'][0].update(name='../outside')),
    ('reserved filename', lambda p: p['accessories'][0].update(name='CON')),
    ('duplicate filename', lambda p: p['accessories'][0].update(name='driven_shaft')),
]:
    payload = copy.deepcopy(base)
    modify(payload)
    cases.append((label, payload))
if base.get('through_covers'):
    for label, modify in [
        ('cavity breaks outer wall', lambda p: p['through_covers'][0]['inner_steps'][0].__setitem__(0, 200)),
        ('unlinked seal bore', lambda p: p['through_covers'][0].update(seal_bore_diameter=15)),
        ('incomplete through hole', lambda p: p['through_covers'][0]['outer_steps'][0].__setitem__(1, 100)),
        ('profile intersects itself', lambda p: p['rotary_parts'][0].update(section=[[0, 1], [4, 5], [0, 5], [4, 1]])),
    ]:
        payload = copy.deepcopy(base)
        modify(payload)
        cases.append((label, payload))
if base.get('assembly_layout'):
    payload = copy.deepcopy(base)
    payload['shaft_segments'] = payload['shaft_segments'][:1]
    payload['keyways'] = []
    cases.append(('single shaft segment cannot locate gear center', payload))
    payload = copy.deepcopy(base)
    payload['assembly_layout']['boss_depth'] = -1
    cases.append(('negative assembly boss depth', payload))
full_file = root / 'parameters_v4.json'
if full_file.exists():
    full = json.loads(full_file.read_text(encoding='utf-8-sig'))
    for label, modify in [
        ('gear/housing center mismatch', lambda p: p['assembly_layout'].update(low_center_x=24.5, high_center_x=-34.5)),
        ('bearing matches wrong shaft step', lambda p: p['shaft_segments'][3].update(diameter=26)),
        ('bearing ball outside width', lambda p: p['bearings'][0].update(width=3)),
        ('stale physical key', lambda p: p['auxiliary'][0].update(start_x=1000)),
        ('housing bearing bore mismatch', lambda p: p['bearings'][0].update(outer_diameter=37)),
        ('high key beyond shaft floor', lambda p: p['gears']['high_keyway'].update(depth=12)),
    ]:
        payload = copy.deepcopy(full)
        modify(payload)
        cases.append((label, payload))
with tempfile.TemporaryDirectory() as tmp:
    for label, payload in cases:
        file = Path(tmp) / 'invalid.json'
        file.write_text(json.dumps(payload), encoding='utf-8')
        result = subprocess.run([str(launcher), '--validate', str(file)], capture_output=True)
        assert result.returncode != 0, label
print(f'Invalid parameter rejection: PASS ({len(cases)} cases)')
