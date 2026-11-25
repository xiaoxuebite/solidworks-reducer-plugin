"""统一参数契约（schema v4）：把齿轮、轴承、附件、箱体并入同一份文件。

为什么需要这个脚本
------------------
`ReducerAddin.Generate` 已经调用：

    MakeGearTrain(p.gears)   MakeBearings(p.bearings)
    MakeAuxiliary(p.auxiliary)   MakeHousings(p.housing)

但这四段在 `parameters_v3.json` 里并不存在，反序列化后全部是 null，
四个 Make* 直接返回空集合，所以"完整生成"实际仍然只产出 12 个旧零件
（从动轴、端盖、调整环、轴套、通盖、油封、挡油环）。

本脚本复用 `export_parameters` / `export_housing` / `export_auxiliary` 的
构建函数，把四段补齐，并按 C# 端 `ValidateGearTrain` / `ValidateBearing`
的规则先做一遍 Python 侧预检——把"螺旋角越界""齿数越界"
"轴承径向空间不足"这类必失败的情况在写文件前拦下，而不是丢给
SolidWorks 跑几分钟再报错。

用法
----
    python export_all.py                                  # 全部段，输出 parameters_v4.json
    python export_all.py --sections gears,bearings        # 只启用部分新段，便于增量验证
    python export_all.py --sections housing --output D:\\tmp\\p.json
"""
import argparse
import math
import os
from pathlib import Path
import sys

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT.parent / 'model_generator'))

import shaft_params as sp  # noqa: E402
import export_parameters  # noqa: E402
import export_auxiliary  # noqa: E402
import export_housing  # noqa: E402
from gear_geometry import driven_mesh_phase  # noqa: E402

NEW_SECTIONS = ('gears', 'bearings', 'auxiliary', 'housing')


# ---------- 齿轮 ----------

def build_gears(base):
    """按 GearTrainSpec 的字段生成齿轮段。

    数据来源全部是原有 Python 参数层，不重新推导：
      - 主动轮直接用 HIGH_SHAFT_SECTIONS（第 5 段 kind=gear）
      - 从动轮轴孔径取低速轴第 2 段（齿轮安装段）
      - 从动轮键槽取 base['keyways'][0]（低速轴齿轮键），避免与旧契约分叉
    """
    sections = [dict(kind=s['kind'], diameter=float(s['diameter']), length=float(s['length']))
                for s in sp.HIGH_SHAFT_SECTIONS]
    if not any(s['kind'] == 'gear' for s in sections):
        raise ValueError('HIGH_SHAFT_SECTIONS 中没有 kind=gear 的轴段，无法确定主动轮位置。')

    low_key = base['keyways'][0]
    bore = float(base['shaft_segments'][1]['diameter'])
    # shaft_params 没有 Z2 模块常量，与 export_housing / original export_parameters
    # 保持一致：从环境变量读取并夹到下限 6。
    driven_teeth = int(round(max(sp.env_float('REDUCER_Z2', 42), 6)))

    high_diameter = sections[0]['diameter']
    high_key = sp.key_from_env('REDUCER_HIGH_KEY', high_diameter, 18.0)
    # Keyway.depth 若参数层没给，按 0.22d 估计并限制在轴半径以内。
    key_depth = high_key.get('depth')
    if key_depth is None:
        key_depth = min(2.5, high_diameter * 0.22)
    key_length = min(float(high_key['length']), max(sections[0]['length'] - 6.0, 1.0))

    return {
        'module': float(sp.MODULE),
        'pressure_angle': float(os.environ.get('REDUCER_PRESSURE_ANGLE', 20.0)),
        'helix_angle': float(sp.HELIX_ANGLE),
        'pinion_teeth': int(round(sp.Z1)),
        'driven_teeth': driven_teeth,
        'driven_width': float(sp.GEAR_FACE_WIDTH),
        'driven_bore': bore,
        'driven_key_width': float(low_key['width']),
        'driven_key_depth': float(low_key['depth']),
        # Reuse the source frame convention; a quarter pitch only works for
        # certain tooth-count combinations (including the default 17/42).
        'driven_mesh_phase_deg': driven_mesh_phase(
            int(round(sp.Z1)), driven_teeth, float(sp.MODULE),
            next(s['length'] for s in sections if s['kind'] == 'gear'),
            float(sp.GEAR_FACE_WIDTH), float(sp.HELIX_ANGLE)),
        'high_sections': sections,
        'high_keyway': {
            'segment_index': 0,
            'diameter': high_diameter,
            'start': 4.0,
            'length': key_length,
            'width': float(high_key['width']),
            'depth': float(key_depth),
        },
    }


def validate_gears(gear):
    """对应 src/GearParts.cs 的 ValidateGearTrain，规则保持一致。"""
    for key in ('module', 'driven_width', 'driven_bore', 'driven_key_width', 'driven_key_depth'):
        value = gear[key]
        if not (math.isfinite(value) and value > 0):
            raise ValueError('齿轮参数 %s 必须是有限正数，当前为 %r。' % (key, value))
    if not 0 < gear['pressure_angle'] < 45:
        raise ValueError('齿轮压力角必须在 0 到 45 度之间，当前为 %g。' % gear['pressure_angle'])
    if not math.isfinite(gear['helix_angle']) or abs(gear['helix_angle']) > 35:
        raise ValueError('齿轮螺旋角必须在 -35° 到 35° 之间。')
    for key in ('pinion_teeth', 'driven_teeth'):
        if not math.isfinite(gear[key]) or not 6 <= gear[key] <= 500:
            raise ValueError('原生齿轮支持 6 到 500 齿，%s 当前为 %d。' % (key, gear[key]))
    if not math.isfinite(gear['driven_mesh_phase_deg']):
        raise ValueError('齿轮啮合相位必须有限。')
    sections = gear['high_sections']
    for section in sections:
        for key in ('diameter', 'length'):
            if not (math.isfinite(section[key]) and section[key] > 0):
                raise ValueError('高速轴段 %s 必须是有限正数。' % key)
    high_key = gear['high_keyway']
    for key in ('width', 'length', 'depth'):
        if not (math.isfinite(high_key[key]) and high_key[key] > 0):
            raise ValueError('高速轴键槽 %s 必须是有限正数。' % key)
    if (not math.isfinite(high_key['start']) or high_key['start'] < 0
            or high_key['start'] + high_key['length'] > sections[0]['length'] + 1e-8
            or high_key['length'] <= high_key['width']
            or high_key['width'] >= sections[0]['diameter']):
        raise ValueError('高速轴键槽必须落在第一轴段内，且长度、宽度需要适合轴径。')
    if high_key['depth'] > sections[0]['diameter'] / 2 - 0.5:
        raise ValueError('高速轴键槽深度必须在轴心之外保留至少 0.5 mm，不会自动缩小输入深度。')


# ---------- 轴承 ----------

def build_bearings():
    specs = []
    for name, label, spec in (('high_bearing', sp.HIGH_BEARING_NAME, sp.HIGH_BEARING),
                              ('low_bearing', sp.LOW_BEARING_NAME, sp.LOW_BEARING)):
        specs.append({
            'name': name,
            'catalog_name': str(label or ''),
            'inner_diameter': float(spec['inner_diameter']),
            'outer_diameter': float(spec['outer_diameter']),
            'width': float(spec['width']),
        })
    return specs


def validate_bearings(specs):
    """对应 src/BearingParts.cs 的 ValidateBearing。"""
    for spec in specs:
        if spec['name'] not in ('high_bearing', 'low_bearing'):
            raise ValueError('轴承标识必须为 high_bearing 或 low_bearing，当前为 %r。' % spec['name'])
        for key in ('inner_diameter', 'outer_diameter', 'width'):
            if not (math.isfinite(spec[key]) and spec[key] > 0):
                raise ValueError('%s 的 %s 必须是有限正数。' % (spec['name'], key))
        if spec['outer_diameter'] <= spec['inner_diameter']:
            raise ValueError('%s 外径必须大于内径。' % spec['name'])
        space = (spec['outer_diameter'] - spec['inner_diameter']) / 2
        ball_radius = space * .294
        if ball_radius <= .35:
            raise ValueError(
                '%s 径向空间不足以生成参考滚动体：外径 %g、内径 %g、宽 %g。'
                % (spec['name'], spec['outer_diameter'], spec['inner_diameter'], spec['width']))
        if ball_radius * 2 >= spec['width'] - .06:
            raise ValueError('%s 宽度不足：参考滚珠直径必须小于宽度减 0.06 mm，不能伸出端面。' % spec['name'])


# ---------- 装配前置校验 ----------

# src/AssemblyBuilder.cs 的 PlanAssembly 里用 switch 显式处理的零件名。
PLACEMENT_IN_SWITCH = {
    'driven_shaft', 'drive_gear_shaft', 'driven_gear', 'high_bearing', 'low_bearing',
    'housing', 'cover', 'low_blank_cover', 'high_blank_cover', 'low_adjusting_ring',
    'high_adjusting_ring', 'sleeve', 'low_through_cover', 'low_oil_seal',
    'high_through_cover', 'high_oil_seal', 'oil_retaining_ring',
}


def produced_parts(payload):
    """按 Generate 的调用顺序，列出本次会产出的零件名（不含 .SLDPRT）。"""
    names = {'driven_shaft', 'low_blank_cover'}
    if payload.get('high_blank_cover'):
        names.add('high_blank_cover')
    if payload.get('housing'):
        names |= {'housing', 'cover'}
    names |= {a['name'] for a in payload.get('accessories') or []}
    for cover in payload.get('through_covers') or []:
        names.add(cover['name'])
        names.add(cover['name'].replace('_through_cover', '_oil_seal'))
    names |= {r['name'] for r in payload.get('rotary_parts') or []}
    if payload.get('gears'):
        names |= {'drive_gear_shaft', 'driven_gear'}
    names |= {b['name'] for b in payload.get('bearings') or []}
    names |= {a['name'] for a in payload.get('auxiliary') or []}
    return names


def validate_placement(payload):
    """任何走不到 switch 分支的零件，都必须有合法的 auxiliary.placement 矩阵。

    PlanAssembly 的 default 分支在缺定位规则时会直接抛异常，那已经是
    SolidWorks 跑了几分钟之后。同一件事在写参数文件时就能判掉。
    """
    for name in sorted(produced_parts(payload) - PLACEMENT_IN_SWITCH):
        entry = next((a for a in payload.get('auxiliary') or [] if a['name'] == name), None)
        if entry is None:
            raise ValueError('零件 %s 没有定位规则：既不在 PlanAssembly 的 switch 中，'
                             '也没有 auxiliary 条目。' % name)
        placement = entry.get('placement')
        if not isinstance(placement, list) or len(placement) != 12:
            raise ValueError('零件 %s 的 auxiliary.placement 必须是 12 元矩阵。' % name)
        if any(not isinstance(v, (int, float)) for v in placement):
            raise ValueError('零件 %s 的 auxiliary.placement 含非数值。' % name)


def apply_derived_center_distance():
    """未显式指定时，按齿轮副推出标准中心距，而不是沿用 55 mm 的旧默认值。

    export_parameters / export_housing / export_auxiliary 都读同一个
    REDUCER_CENTER_DISTANCE 环境变量，不统一就会生成啮合不上的减速器。
    默认齿轮副 m=2、z=17/42 的标准中心距是 59 mm，而代码里的兜底值是 55 mm
    （对应 z2=38），两者混用会让装配基准与齿形互相矛盾。

    网页流程由 app.py 自行计算并传入该变量，因此这里只影响独立导出路径。
    """
    if os.environ.get('REDUCER_CENTER_DISTANCE'):
        return float(os.environ['REDUCER_CENTER_DISTANCE'])
    module = float(sp.MODULE)
    cosine = float(sp.HELIX_COS)
    z1 = int(round(sp.Z1))
    z2 = int(round(max(sp.env_float('REDUCER_Z2', 42), 6)))
    distance = module * (z1 + z2) / (2 * cosine)
    os.environ['REDUCER_CENTER_DISTANCE'] = repr(distance)
    return distance


# ---------- 主流程 ----------

def build_all(sections):
    unknown = set(sections) - set(NEW_SECTIONS)
    if unknown:
        raise ValueError('未知参数段：%s。可选：%s。' % ('、'.join(sorted(unknown)), '、'.join(NEW_SECTIONS)))

    # 必须在构建任何段之前确定，否则轴、箱体、附件会各用各的中心距。
    center_distance = apply_derived_center_distance()

    payload = export_parameters.build_parameters()
    payload['schema_version'] = 4
    payload['source'] = 'model_generator/shaft_params.py + export_housing + export_auxiliary'

    if 'gears' in sections:
        payload['gears'] = build_gears(payload)
        validate_gears(payload['gears'])
    if 'bearings' in sections:
        payload['bearings'] = build_bearings()
        validate_bearings(payload['bearings'])
    if 'auxiliary' in sections:
        payload['auxiliary'] = export_auxiliary.build_auxiliary(sp)
    if 'housing' in sections:
        payload['housing'] = export_housing.build_housing(sp)

    # 总装定位规则依赖齿轮与轴承的宽度，缺段时提前说明，避免生成后才报错。
    missing = [name for name in ('gears', 'bearings') if name not in payload]
    if missing:
        payload.setdefault('migration_notes', []).append(
            '未包含参数段：%s；总装中对应零件不会生成。' % '、'.join(missing))

    # 缺段时 housing 不存在，定位校验只在本段完整时才做。
    if 'housing' in payload:
        validate_placement(payload)
    if {'gears', 'bearings', 'auxiliary', 'housing'} <= set(sections):
        payload['hardware_version'] = 1
    return payload


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--output', type=Path, default=ROOT / 'parameters_v4.json')
    parser.add_argument('--sections', default=','.join(NEW_SECTIONS),
                        help='要并入的新段，逗号分隔，默认全部：%s' % ','.join(NEW_SECTIONS))
    args = parser.parse_args()

    sections = [name.strip() for name in args.sections.split(',') if name.strip()]
    payload = build_all(sections)
    export_parameters.write_payload(payload, args.output)

    summary = ['schema_version=%d' % payload['schema_version'], 'sections=' + ','.join(sections)]
    summary.append('center_distance=%g' % float(os.environ['REDUCER_CENTER_DISTANCE']))
    if 'gears' in payload:
        g = payload['gears']
        summary.append('gears=%d/%d 齿 m=%g' % (g['pinion_teeth'], g['driven_teeth'], g['module']))
    if 'bearings' in payload:
        summary.append('bearings=' + '/'.join(b['catalog_name'] or b['name'] for b in payload['bearings']))
    if 'auxiliary' in payload:
        summary.append('auxiliary=%d 项' % len(payload['auxiliary']))
    if 'housing' in payload:
        summary.append('housing=%d+%d 特征' % (len(payload['housing']['lower']), len(payload['housing']['upper'])))
    print(' | '.join(summary))


if __name__ == '__main__':
    main()
