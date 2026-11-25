"""Pure parameter/export checks, independent of SolidWorks availability."""
import math
from pathlib import Path
import sys

root = Path(__file__).resolve().parents[1]
sys.path[:0] = [str(root), str(root.parent / 'model_generator')]
import shaft_params
from export_auxiliary import build_auxiliary

parts = build_auxiliary(shaft_params)
assert len(parts) == 8
assert len({part['name'] for part in parts}) == 8
for part in parts:
    a = part['placement']
    assert len(a) == 12 and all(math.isfinite(value) for value in a)
    for i in range(3):
        for j in range(3):
            assert math.isclose(sum(a[i*3+k]*a[j*3+k] for k in range(3)), float(i == j), abs_tol=1e-12)
    determinant = a[0]*(a[4]*a[8]-a[5]*a[7])-a[1]*(a[3]*a[8]-a[5]*a[6])+a[2]*(a[3]*a[7]-a[4]*a[6])
    assert math.isclose(determinant, 1., abs_tol=1e-12)
    if part['kind'] == 'key':
        expected = ((part['length']-part['width'])*part['width']+math.pi*part['width']**2/4)*part['height']
        assert math.isclose(part['reference_volume_mm3'], expected, rel_tol=1e-12)
    elif part['kind'] == 'plate':
        expected = (part['length']*part['width']-(4-math.pi)*part['corner_radius']**2
                    -math.pi*part['hole_diameter']**2-part['inner_square']**2)*part['height']
        assert math.isclose(part['reference_volume_mm3'], expected, rel_tol=1e-12)
print('Auxiliary export / analytic plate and key volume / proper transforms: PASS (8 parts)')
