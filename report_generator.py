# -*- coding: utf-8 -*-
import math
import re
import time
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont
from model_generator.housing_params import MODEL_PROFILE
from model_generator.housing_height import design_height
from model_generator.vent_layout import cover_vent_layout
from model_generator.axial_layout import axial_layout
from design_service import application_factor
from model_generator.gear_operating import AXIAL_SUPPORT_METHOD, gear_operating_conditions


def _num(value, digits=None, default="-"):
    try:
        number = float(value)
    except (TypeError, ValueError):
        return default
    if digits is None:
        if number.is_integer():
            return str(int(number))
        return f"{number:g}"
    return f"{number:.{digits}f}"


def _status_text(raw):
    text = str(raw or "")
    text = re.sub(r"<[^>]+>", "", text)
    return text.strip() or "-"


def _choice_label(value, choices):
    return choices.get(str(value or ""), str(value or "-") or "-")


KEY_COUNT_LABELS = {
    "single": "单键（$Z=1.0$）",
    "double": "双键（$Z=1.5$）",
}

def _md_cell(value):
    text = "" if value is None else str(value)
    text = re.sub(r"<br\s*/?>", "\n", text)
    text = text.replace("|", "\\|").replace("\r\n", "\n").replace("\r", "\n")
    return text.replace("\n", "<br>") or "-"


def _md_table(headers, rows):
    out = [
        "| " + " | ".join(_md_cell(item) for item in headers) + " |",
        "| " + " | ".join("---" for _ in headers) + " |",
    ]
    for row in rows:
        out.append("| " + " | ".join(_md_cell(item) for item in row) + " |")
    return "\n".join(out)


def _float(value, default=0.0):
    try:
        number = float(value)
    except (TypeError, ValueError):
        return default
    return number if math.isfinite(number) else default


def _load_font(size, bold=False):
    candidates = [
        r"C:\Windows\Fonts\msyhbd.ttc" if bold else r"C:\Windows\Fonts\msyh.ttc",
        r"C:\Windows\Fonts\simhei.ttf",
        r"C:\Windows\Fonts\simsun.ttc",
        r"C:\Windows\Fonts\arial.ttf",
    ]
    for candidate in candidates:
        try:
            if candidate and Path(candidate).exists():
                return ImageFont.truetype(candidate, size)
        except OSError:
            continue
    return ImageFont.load_default()


def _reaction_pair(load, load_position, left_position, right_position):
    span = max(right_position - left_position, 1.0)
    x = min(max(load_position - left_position, 0.0), span)
    return {
        "left": load * (span - x) / span,
        "right": load * x / span,
    }


def _moment_from_point_load(x, reaction, load, load_position, left_position, right_position):
    moment = 0.0
    if x >= left_position:
        moment += reaction["left"] * (x - left_position)
    if x >= load_position:
        moment -= load * (x - load_position)
    if x >= right_position:
        moment -= reaction["right"] * (x - right_position)
    return moment


def _shaft_sum_lengths(inputs, prefix, start, end):
    return sum(max(_float(inputs.get(f"{prefix}S{i}L")), 0.0) for i in range(start, end + 1))


def _shaft_segment_center(inputs, prefix, segment_index):
    return _shaft_sum_lengths(inputs, prefix, 1, segment_index - 1) + _float(inputs.get(f"{prefix}S{segment_index}L")) / 2


def _shaft_geometry(inputs, prefix):
    if prefix == "high":
        return {
            "leftBearing": _shaft_sum_lengths(inputs, "high", 1, 1) + _float(inputs.get("highS2L")) / 2,
            "gear": _shaft_sum_lengths(inputs, "high", 1, 4) + _float(inputs.get("highS5L")) / 2,
            "rightBearing": _shaft_sum_lengths(inputs, "high", 1, 7) + _float(inputs.get("highS8L")) / 2,
            "totalLength": _shaft_sum_lengths(inputs, "high", 1, 8),
        }
    return {
        "leftBearing": _float(inputs.get("lowS1L")) / 2,
        "gear": _shaft_sum_lengths(inputs, "low", 1, 1) + _float(inputs.get("lowS2L")) / 2,
        "rightBearing": _shaft_sum_lengths(inputs, "low", 1, 3) + _float(inputs.get("lowS4L")) / 2,
        "totalLength": _shaft_sum_lengths(inputs, "low", 1, 6),
    }


def _shaft_torque_points(inputs, prefix, torque, geometry, total_length):
    def clamp_position(value):
        return min(max(value, 0.0), max(total_length, 1.0))

    if prefix == "high":
        points = [
            {"label": "K", "x": clamp_position(_shaft_segment_center(inputs, "high", 1)), "magnitude": abs(torque)},
            {"label": "G", "x": clamp_position(geometry["gear"]), "magnitude": -abs(torque)},
        ]
    else:
        points = [
            {"label": "G", "x": clamp_position(geometry["gear"]), "magnitude": abs(torque)},
            {"label": "K", "x": clamp_position(_shaft_segment_center(inputs, "low", 6)), "magnitude": -abs(torque)},
        ]
    return sorted(points, key=lambda item: item["x"])


def _shaft_internal_torque_at(x, torque_points):
    return sum(point["magnitude"] for point in torque_points if point["x"] <= x + 1e-7)


def _build_shaft_diagram_data(inputs, results, selections, prefix):
    layout = axial_layout(inputs, selections)
    inputs = {**inputs, **layout['shaft_lengths']}
    geometry = _shaft_geometry(inputs, prefix)
    left, gear, right = layout['local_supports'][prefix]
    geometry.update(leftBearing=left, gear=gear, rightBearing=right)
    total_length = max(geometry["totalLength"], geometry["rightBearing"], geometry["gear"], 1.0)
    pressure = math.radians(_float(inputs.get("pressureAngle"), 20.0))
    beta = math.radians(_float(inputs.get("helixAngle"), 0.0) if selections.get("gearType") == "helical" else 0.0)
    load_factor = application_factor(inputs, selections)
    if prefix == "high":
        torque = 9550000 * _float(inputs.get("P"), 1.2) / max(_float(inputs.get("n1"), 1450), 1.0)
        pitch_diameter = max(_float(results.get("dPinion")), 1.0)
    else:
        torque = _float(results.get("outTorque"))
        pitch_diameter = max(_float(results.get("dGear")), 1.0)
    torque_points = _shaft_torque_points(inputs, prefix, torque, geometry, total_length)
    tangential = _float(results.get("tangentialForce"), 2 * torque / pitch_diameter)
    radial = tangential * math.tan(pressure) / max(math.cos(beta), 0.01)
    vertical_load = load_factor * tangential
    horizontal_load = load_factor * radial
    vertical_reaction = _reaction_pair(vertical_load, geometry["gear"], geometry["leftBearing"], geometry["rightBearing"])
    horizontal_reaction = _reaction_pair(horizontal_load, geometry["gear"], geometry["leftBearing"], geometry["rightBearing"])
    position_samples = [total_length * index / 160 for index in range(161)]
    step_eps = total_length / 100000
    for point in torque_points:
        position_samples.extend([max(point["x"] - step_eps, 0.0), point["x"]])
    positions = sorted({round(value, 6) for value in position_samples})
    torques, vertical_moments, horizontal_moments, bending, equivalent = [], [], [], [], []
    for x in positions:
        torque_value = abs(_shaft_internal_torque_at(x, torque_points))
        mv = _moment_from_point_load(x, vertical_reaction, vertical_load, geometry["gear"], geometry["leftBearing"], geometry["rightBearing"])
        mh = _moment_from_point_load(x, horizontal_reaction, horizontal_load, geometry["gear"], geometry["leftBearing"], geometry["rightBearing"])
        moment = math.hypot(mv, mh)
        torques.append(torque_value)
        vertical_moments.append(mv)
        horizontal_moments.append(mh)
        bending.append(moment)
        equivalent.append(math.hypot(moment, 0.6 * torque_value))
    def max_pair(values):
        index = max(range(len(values)), key=lambda item: abs(values[item]))
        return values[index], positions[index]
    return {
        "title": "高速轴" if prefix == "high" else "低速轴",
        "geometry": geometry,
        "totalLength": total_length,
        "torquePoints": torque_points,
        "tangentialForce": tangential,
        "positions": positions,
        "torques": torques,
        "verticalMoments": vertical_moments,
        "horizontalMoments": horizontal_moments,
        "bendingMoments": bending,
        "equivalentMoments": equivalent,
        "maxima": {
            "torque": max_pair(torques),
            "vertical": max_pair(vertical_moments),
            "horizontal": max_pair(horizontal_moments),
            "bending": max_pair(bending),
            "equivalent": max_pair(equivalent),
        },
    }


def _draw_line_chart_png(path, diagram, value_key, title, color, max_key, series=None):
    width, height = 1200, 620
    margin_left, margin_right, margin_top, margin_bottom = 120, 44, 88, 90
    plot_w = width - margin_left - margin_right
    plot_h = height - margin_top - margin_bottom
    series = series or [{"key": value_key, "label": "", "color": color}]
    values = diagram[value_key]
    positions = diagram["positions"]
    max_value = max(
        [abs(value) for item in series for value in diagram.get(item["key"], [])]
        + [abs(item) for item in values]
        + [1.0]
    )
    total_length = max(diagram["totalLength"], 1.0)
    image = Image.new("RGB", (width, height), "white")
    draw = ImageDraw.Draw(image)
    font = _load_font(28)
    small = _load_font(22)
    bold = _load_font(30, bold=True)
    axis_color = "#8ea9cf"
    grid_color = "#e8f0fb"
    draw.text((margin_left, 28), title, fill="#183a68", font=bold)
    max_val, max_pos = diagram["maxima"][max_key]
    draw.text((margin_left + 360, 34), f"max={max_val:.1f} N·mm @ x={max_pos:.1f} mm", fill="#52687f", font=small)
    if len(series) > 1:
        legend_x = width - margin_right - 360
        for index, item in enumerate(series):
            x0 = legend_x + index * 110
            dash = item.get("dash")
            if dash:
                cursor = x0
                while cursor < x0 + 32:
                    draw.line((cursor, 48, min(cursor + dash[0], x0 + 32), 48), fill=item["color"], width=5)
                    cursor += sum(dash)
            else:
                draw.line((x0, 48, x0 + 32, 48), fill=item["color"], width=5)
            draw.text((x0 + 40, 34), item["label"], fill="#244f84", font=small)
    for ratio in (0.0, 0.25, 0.5, 0.75, 1.0):
        y = margin_top + plot_h * (1 - ratio)
        draw.line((margin_left, y, width - margin_right, y), fill=grid_color, width=1)
        draw.text((16, y - 12), f"{max_value * ratio:.0f}", fill="#52687f", font=small)
    draw.line((margin_left, margin_top, margin_left, height - margin_bottom), fill=axis_color, width=2)
    draw.line((margin_left, height - margin_bottom, width - margin_right, height - margin_bottom), fill=axis_color, width=2)
    def x_scale(x):
        return margin_left + x / total_length * plot_w
    def y_scale(value):
        return margin_top + plot_h - abs(value) / max_value * plot_h

    def unique_positions(items, eps=0.15):
        clipped = sorted(
            min(max(float(value), 0.0), total_length)
            for value in items
            if isinstance(value, (int, float)) and math.isfinite(float(value))
        )
        result = []
        for value in clipped:
            if not result or abs(value - result[-1]) > eps:
                result.append(value)
        return result

    x_ticks = unique_positions(
        [
            0.0,
            diagram["geometry"]["leftBearing"],
            diagram["geometry"]["gear"],
            diagram["geometry"]["rightBearing"],
            *[point["x"] for point in diagram.get("torquePoints", [])],
            total_length,
        ]
    )
    for x in x_ticks:
        px = x_scale(x)
        draw.line((px, margin_top, px, height - margin_bottom), fill="#d9e6f8", width=2)
        draw.line((px, height - margin_bottom, px, height - margin_bottom + 8), fill=axis_color, width=2)
        label = f"{x:.1f}" if abs(x - round(x)) > 0.05 else f"{x:.0f}"
        draw.text((px - 20, height - margin_bottom + 16), label, fill="#52687f", font=small)
    for item in series:
        item_values = diagram.get(item["key"], [])
        points = [(x_scale(x), y_scale(item_values[index])) for index, x in enumerate(positions)]
        if len(points) > 1:
            if value_key == "torques":
                for index in range(1, len(points)):
                    prev_x, prev_y = points[index - 1]
                    curr_x, curr_y = points[index]
                    draw.line((prev_x, prev_y, curr_x, prev_y), fill=item["color"], width=5)
                    if abs(curr_y - prev_y) > 0.01:
                        draw.line((curr_x, prev_y, curr_x, curr_y), fill=item["color"], width=5)
            else:
                dash = item.get("dash")
                line_width = item.get("width", 5)
                if dash:
                    for index in range(1, len(points)):
                        x1, y1 = points[index - 1]
                        x2, y2 = points[index]
                        length = math.hypot(x2 - x1, y2 - y1)
                        if length <= 0:
                            continue
                        dx = (x2 - x1) / length
                        dy = (y2 - y1) / length
                        distance = 0.0
                        while distance < length:
                            start = distance
                            end = min(distance + dash[0], length)
                            draw.line(
                                (
                                    x1 + dx * start,
                                    y1 + dy * start,
                                    x1 + dx * end,
                                    y1 + dy * end,
                                ),
                                fill=item["color"],
                                width=line_width,
                            )
                            distance += sum(dash)
                else:
                    draw.line(points, fill=item["color"], width=line_width, joint="curve")
    if len(series) == 1:
        max_x = x_scale(max_pos)
        max_y = y_scale(max_val)
        draw.ellipse((max_x - 7, max_y - 7, max_x + 7, max_y + 7), fill=color)
    draw.text((margin_left + plot_w / 2 - 78, height - 38), "轴向位置 x / mm", fill="#52687f", font=small)
    draw.text((20, margin_top + 8), "N·mm", fill="#52687f", font=small)
    path.parent.mkdir(parents=True, exist_ok=True)
    image.save(path, format="PNG")
    return path


def generate_shaft_diagram_images(design, image_dir):
    inputs = design.get("inputs") if isinstance(design.get("inputs"), dict) else {}
    selections = design.get("selections") if isinstance(design.get("selections"), dict) else {}
    results = design.get("results") if isinstance(design.get("results"), dict) else {}
    image_dir = Path(image_dir)
    generated = []
    specs = [
        ("high", "torques", "高速轴扭矩图 T", "#2563eb", "torque", "high_torque.png", None),
        ("high", "bendingMoments", "高速轴组合弯矩图 Mv/Mh/M", "#dc2626", "bending", "high_bending_moment.png", [
            {"key": "verticalMoments", "label": "Mv", "color": "#2563eb", "dash": (18, 10)},
            {"key": "horizontalMoments", "label": "Mh", "color": "#059669", "dash": (8, 10)},
            {"key": "bendingMoments", "label": "M", "color": "#dc2626", "width": 7},
        ]),
        ("high", "equivalentMoments", "高速轴当量弯矩图 Me", "#7c3aed", "equivalent", "high_equivalent_moment.png", None),
        ("low", "torques", "低速轴扭矩图 T", "#2563eb", "torque", "low_torque.png", None),
        ("low", "bendingMoments", "低速轴组合弯矩图 Mv/Mh/M", "#dc2626", "bending", "low_bending_moment.png", [
            {"key": "verticalMoments", "label": "Mv", "color": "#2563eb", "dash": (18, 10)},
            {"key": "horizontalMoments", "label": "Mh", "color": "#059669", "dash": (8, 10)},
            {"key": "bendingMoments", "label": "M", "color": "#dc2626", "width": 7},
        ]),
        ("low", "equivalentMoments", "低速轴当量弯矩图 Me", "#7c3aed", "equivalent", "low_equivalent_moment.png", None),
    ]
    diagrams = {
        "high": _build_shaft_diagram_data(inputs, results, selections, "high"),
        "low": _build_shaft_diagram_data(inputs, results, selections, "low"),
    }
    for prefix, key, title, color, max_key, filename, series in specs:
        path = image_dir / filename
        _draw_line_chart_png(path, diagrams[prefix], key, title, color, max_key, series=series)
        generated.append({"title": title, "filename": filename, "prefix": prefix})
    return generated


def _list_items(value):
    if isinstance(value, list):
        return [str(item).strip() for item in value if str(item or "").strip()]
    if str(value or "").strip():
        return [str(value).strip()]
    return []


def _formula_markdown_block(title, formulas, substitutions, calculation_steps=None):
    lines = [f"### {title}"]
    formula_items = _list_items(formulas)
    step_items = _list_items(calculation_steps)
    substitution_items = _list_items(substitutions)
    if formula_items:
        lines.extend(["", "**计算公式：**"])
        for item in formula_items:
            lines.extend(["", f"$${item}$$"])
    if step_items:
        lines.extend(["", "**计算过程：**"])
        for item in step_items:
            lines.extend(["", f"$${item}$$"])
    elif substitution_items:
        lines.extend(["", "**代入计算：**"])
        for item in substitution_items:
            lines.extend(["", f"$${item}$$"])
    return "\n".join(lines)


def generate_reducer_report_markdown(project, design, output_path, image_path=None, diagram_image_dir=None, diagram_relative_dir="images"):
    output_path = Path(output_path)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    image_path = Path(image_path) if image_path else None

    inputs = design.get("inputs") if isinstance(design.get("inputs"), dict) else {}
    selections = design.get("selections") if isinstance(design.get("selections"), dict) else {}
    results = design.get("results") if isinstance(design.get("results"), dict) else {}
    formula_notes = design.get("formulaNotes") if isinstance(design.get("formulaNotes"), list) else []
    adjustment_advice = design.get("adjustmentAdvice") if isinstance(design.get("adjustmentAdvice"), list) else []
    manual_kv = selections.get("dynamicFactorMode", inputs.get("dynamicFactorMode")) == "manual"
    kv_value = inputs.get("dynamicFactorManual", 1.2) if manual_kv else None
    saved_review = design.get('calculation_review')
    if not isinstance(saved_review, dict) or saved_review.get('axial_load_method') != AXIAL_SUPPORT_METHOD:
        if any(results.get(key) not in (None, '') for key in ('highBearingLife', 'lowBearingLife')):
            results = {**results, 'highBearingLife': None, 'lowBearingLife': None}
            design = {**design, 'results': results, 'highBearingConclusion': '未按自动承载侧重算',
                      'lowBearingConclusion': '未按自动承载侧重算'}
            formula_notes = [note for note in formula_notes if not isinstance(note, dict)
                             or '轴承寿命' not in str(note.get('title', ''))]

    axial = axial_layout(inputs, selections)
    stale_axial_checks = (bool(axial['length_changes']) and any(key in inputs for key in axial['shaft_lengths'])) or bool(axial['diameter_changes'])
    stale_reason = '未按新轴径重算' if axial['diameter_changes'] else '未按新长度重算'
    if stale_axial_checks:
        results = {**results, **{key: None for key in ('highShaftStress','shaftStress','highBearingLife','lowBearingLife')}}
        design = {**design, 'results': results, **{key: stale_reason for key in
                  ('highShaftConclusion','shaftConclusion','highBearingConclusion','lowBearingConclusion')}}
        formula_notes = [note for note in formula_notes if not isinstance(note, dict)
                         or not any(word in str(note.get('title','')) for word in ('轴齿轮截面','轴承寿命'))]
    inputs = {**inputs, **axial['shaft_lengths'], **axial['shaft_diameters']}
    design = {**design, 'inputs': inputs}
    project_name = project.get("name") or "减速器设计项目"
    project_number = project.get("number") or "-"
    project_author = project.get("author") or "-"
    project_date = project.get("date") or "-"
    project_comment = project.get("comment") or "-"
    project_id = project.get("project_id") or project.get("id") or "-"
    now = time.strftime("%Y-%m-%d %H:%M:%S")
    gear_design_method = selections.get("gearDesignMethod") or "bending"
    gear_design_method_label = "按齿面接触强度设计" if gear_design_method == "contact" else "按齿根弯曲强度设计"
    gear_type = selections.get("gearType") or "spur"
    gear_type_label = "斜齿圆柱齿轮" if gear_type == "helical" else "直齿圆柱齿轮"
    operating = gear_operating_conditions(inputs, selections, _float(results.get("tangentialForce")))
    key_count_label = lambda value: _choice_label(value or "single", KEY_COUNT_LABELS)
    contact_limit = min(_float(inputs.get("contactAllow"), 600),
                        _float(inputs.get("gearContactAllow"), _float(inputs.get("contactAllow"), 600)))

    lines = [
        "# 一级圆柱齿轮减速器设计计算说明书",
        "",
        "## 项目信息",
        "",
        _md_table(["项目", "内容"], [
            ["项目名称", project_name],
            ["项目编号", project_number],
            ["作者", project_author],
            ["日期", project_date],
            ["项目ID", project_id],
            ["导出时间", now],
            ["项目备注", project_comment],
        ]),
        "",
        "## 1 设计任务与原始工况",
        "",
        _md_table(["项目", "数值", "单位"], [
            ["输入功率 P", _num(inputs.get("P"), 3), "kW"],
            ["输入转速 n<sub>1</sub>", _num(inputs.get("n1"), 0), "r/min"],
            ["总效率 η", _num(inputs.get("eta"), 3), "-"],
            ["工作年限", _num(inputs.get("lifeYears"), 0), "年"],
            ["每年工作日", _num(inputs.get("daysPerYear"), 0), "天"],
            ["每日工作时间", _num(inputs.get("hoursPerDay"), 0), "h"],
            ["使用系数 K<sub>A</sub>", _num(application_factor(inputs, selections), 2), "-"],
        ]),
    ]

    if design.get("fully_validated") is False:
        lines[2:2] = ["> " + _status_text(design.get("designConclusion")), ""]
        if design.get("assumptions"):
            lines.extend(["", "### 采用的默认参数与校核范围", ""])
            lines.extend(f"- {_md_cell(item)}" for item in design["assumptions"])
        if design.get("automatic_selection"):
            audit = design["automatic_selection"]
            lines.extend(["", "### 自动选型记录", "",
                          f"齿轮搜索组合数：{audit['gear_combinations']}；轴系候选轮次：{audit['shaft_trials']}。",
                          "轴强度按齿轮截面弯扭合成应力计算，不包含其他截面应力集中与疲劳；键按有效长度 $L-b$ 校核挤压应力；轴承按系统内置额定值及等效动载荷计算寿命。",
                          "", _md_table(["校核项", "计算值", "限制值", "结论"], [
                              [key, _num(check.get("value"), 3), _num(check.get("limit", check.get("minimum")), 3),
                               "通过" if check["passed"] else "不满足"]
                              for key, check in design["checks"].items()]),
                          "", "待复核：材料许用值、全轴疲劳、三维装配干涉、制造公差及润滑密封方案。"])

    review = design.get("calculation_review")
    if isinstance(review, dict):
        lines.extend(["", "### 计算口径与系数来源", ""])
        lines.extend(f"- {_md_cell(review[key])}" for key in ("load_basis", "face_width_basis", "allowable_basis") if review.get(key))
        lines.extend(["", _md_table(["系数", "来源与限制"], list(review.get("factor_sources", {}).items()))])
        lines.extend(["", "以上为项目初步计算体系，不代表完整 ISO/AGMA 标准认证。全轴疲劳、齿形与装配仍需复核。"])
    if design.get("calculation_warnings"):
        lines.extend(["", "### 不满足的适用条件", ""])
        lines.extend(f"- {_md_cell(item)}" for item in design["calculation_warnings"])

    lines.extend([
        "",
        "## 2 齿轮传动设计计算",
        "",
        "本节列出传动比、输出转速、输出转矩、模数、分度圆直径、中心距及载荷系数等主要计算结果。详细公式与当前代入过程见第 4.1 节。",
        "",
        _md_table(["项目", "计算式 / 说明", "结果"], [
            ["齿轮类型", "第 3 步选择", gear_type_label],
            ["转向基准", "从主动轴外伸端朝箱内看，两轴采用同一观察方向", operating["rotationLabel"]],
            ["齿轮旋向", "平行轴外啮合，两轮旋向相反" if gear_type == "helical" else "直齿轮不适用", operating["helixHandLabel"]],
            ["主动轮轴向力", operating["pinionThrustDirection"], f"{_num(abs(operating['pinionAxialForce']), 2)} N"],
            ["从动轮轴向力", operating["gearThrustDirection"], f"{_num(abs(operating['gearAxialForce']), 2)} N"],
            ["螺旋角 β", "直齿轮取 0°，斜齿轮按输入值计算", f"{_num(inputs.get('helixAngle'), 1)} °"],
            ["传动比 i", r"$i=\frac{z_2}{z_1}$", _num(results.get("ratio"), 3)],
            ["中心距 a", r"$a=\frac{m_n(z_1+z_2)}{2\cos\beta}$", f"{_num(results.get('centerDistance'), 2)} mm"],
            ["输出功率 P<sub>2</sub>", r"$P_2=\eta P_1$", f"{_num(results.get('outPower'), 3)} kW"],
            ["输出转速 n<sub>2</sub>", r"$n_2=\frac{n_1}{i}$", f"{_num(results.get('outSpeed'), 2)} r/min"],
            ["输出转矩 T<sub>2</sub>", r"$T_2=\frac{9550000P_2}{n_2}$", f"{_num(results.get('outTorque'), 2)} N·mm"],
            ["设计计算方法", "第 3 步选择", gear_design_method_label],
            ["校核所需最小标准模数 m<sub>min</sub>", "固定齿宽，逐项重算系数，接触与弯曲同时校核", f"{_num(results.get('moduleMin'), 3)} mm"],
            ["接触校核最小标准直径 d<sub>1min,H</sub>", "接触强度可行的最小标准模数对应的分度圆", f"{_num(results.get('contactDesignDiameterMin'), 2)} mm"],
            ["标准法面模数 m<sub>n</sub>", "用户选择不小于 m<sub>min</sub> 的标准模数", f"{_num(inputs.get('moduleM'), 2)} mm"],
            ["小齿轮分度圆直径 d<sub>1</sub>", r"$d_1=\frac{m_nz_1}{\cos\beta}$", f"{_num(results.get('dPinion'), 2)} mm"],
            ["大齿轮分度圆直径 d<sub>2</sub>", r"$d_2=\frac{m_nz_2}{\cos\beta}$", f"{_num(results.get('dGear'), 2)} mm"],
            ["圆周力 F<sub>t</sub>", r"$F_t=\frac{2T_1}{d_1}$，按输入侧保守估算", f"{_num(results.get('tangentialForce'), 2)} N"],
            ["区域系数 Z<sub>H</sub>", "按压力角计算", _num(results.get("zoneFactor"), 3)],
            ["弹性系数 Z<sub>E</sub>", "按齿轮材料组合查取，单位 MPa^(1/2)", _num(results.get("elasticFactor"), 1)],
            ["接触合并系数 Z<sub>ε</sub>Z<sub>β</sub>", "已含螺旋角系数，不可重复相乘", _num(results.get("contactOverlapFactorResult"), 3)],
            ["动载系数 Kv", "采用输入值，默认 1.2，可按教材参考图 调整" if manual_kv else "历史数据未提供教材查图值，请重新查取", _num(kv_value, 2)],
            ["接触齿间载荷系数 K<sub>Hα</sub>", "独立人工输入，参考齿间载荷参考表", _num(inputs.get("toothFactorH"), 3)],
            ["弯曲齿间载荷系数 K<sub>Fα</sub>", "独立人工输入，参考齿间载荷参考表", _num(inputs.get("toothFactorF"), 3)],
            ["接触齿向载荷系数 K<sub>Hβ</sub>", "独立人工输入，参考接触齿向载荷参考表", _num(inputs.get("contactFaceFactor"), 3)],
            ["弯曲齿向载荷系数 K<sub>Fβ</sub>", "独立人工输入，参考弯曲齿向载荷参考图，不作固定指数折算", _num(inputs.get("bendingFaceFactor"), 3)],
            ["节线速度 v", r"$v=\frac{\pi d_1n_1}{60000}$，$d_1$ 用 mm，$n_1$ 用 r/min", f"{_num(results.get('pitchLineVelocity'), 2)} m/s"],
            ["小齿轮接触许用应力", "人工输入的最终许用值", f"{_num(inputs.get('contactAllow'), 2)} MPa"],
            ["大齿轮接触许用应力", "人工输入的最终许用值，两轮取较小值比较", f"{_num(inputs.get('gearContactAllow', inputs.get('contactAllow')), 2)} MPa"],
            ["弯曲重合度系数 Y<sub>ε</sub>", "按端面重合度计算", _num(results.get("bendingOverlapFactor"), 3)],
            ["接触载荷系数 K<sub>H</sub>", r"$K_H=K_AK_VK_{H\alpha}K_{H\beta}$", _num(results.get("contactLoadFactor"), 3)],
            ["弯曲载荷系数 K<sub>F</sub>", r"$K_F=K_AK_VK_{F\alpha}K_{F\beta}$", _num(results.get("bendingLoadFactor"), 3)],
        ]),
        "",
        _md_table(["齿轮参数", "小齿轮", "大齿轮"], [
            ["齿数", _num(inputs.get("z1"), 0), _num(inputs.get("z2"), 0)],
            ["法面模数", f"{_num(inputs.get('moduleM'), 2)} mm", f"{_num(inputs.get('moduleM'), 2)} mm"],
            ["螺旋角", f"{_num(inputs.get('helixAngle'), 1)} °", f"{_num(inputs.get('helixAngle'), 1)} °"],
            ["齿宽", f"{_num(inputs.get('pinionFaceWidth'), 2)} mm", f"{_num(inputs.get('gearFaceWidth'), 2)} mm"],
            ["材料", selections.get("pinionGearMaterial") or "-", selections.get("gearMaterial") or "-"],
            ["硬度", f"{_num(inputs.get('pinionHardness'), 0)} HBW", f"{_num(inputs.get('gearHardness'), 0)} HBW"],
            ["精度等级", f"{selections.get('gearPrecisionGrade') or '-'} 级", f"{selections.get('gearPrecisionGrade') or '-'} 级"],
            ["布置方式", "对称布置", "对称布置"],
        ]),
        "",
        "## 3 轴系、支承与键连接设计",
        "",
        _md_table(["项目", "高速轴", "低速轴"], [
            ["轴材料", selections.get("highShaftMaterial") or "-", selections.get("lowShaftMaterial") or "-"],
            ["[τ<sub>T</sub>]", f"{_num(inputs.get('highTauAllow'), 1)} MPa", f"{_num(inputs.get('lowTauAllow'), 1)} MPa"],
            ["A<sub>0</sub>", _num(inputs.get("highShaftA0"), 1), _num(inputs.get("lowShaftA0"), 1)],
            ["最小直径", f"{_num(results.get('highMinDiameter'), 2)} mm", f"{_num(results.get('lowMinDiameter'), 2)} mm"],
            ["轴承型号", selections.get("highBearing") or "-", selections.get("lowBearing") or "-"],
            ["轴向承载侧（自动）", operating['highAxialSupportLabel'], operating['lowAxialSupportLabel']],
        ]),
        "",
        _md_table(["键连接位置", "键规格", "键数量"], [
            ["高速轴外伸端键", selections.get("highKey") or "-", key_count_label(selections.get("highKeyCount"))],
            ["低速轴齿轮键", selections.get("lowGearKey") or "-", key_count_label(selections.get("lowGearKeyCount"))],
            ["低速轴外伸端键", selections.get("lowOuterKey") or "-", key_count_label(selections.get("lowOuterKeyCount"))],
        ]),
        "",
        "### 高速齿轮轴轴段尺寸",
        "",
        _md_table(["轴段", "直径 d", "长度 L"], [[str(i), f"{_num(inputs.get(f'highS{i}D'), 2)} mm", f"{_num(inputs.get(f'highS{i}L'), 2)} mm"] for i in range(1, 9)]),
        "",
        "### 低速轴轴段尺寸",
        "",
        _md_table(["轴段", "直径 d", "长度 L"], [[str(i), f"{_num(inputs.get(f'lowS{i}D'), 2)} mm", f"{_num(inputs.get(f'lowS{i}L'), 2)} mm"] for i in range(1, 7)]),
        "",
        "## 4 强度与寿命校核",
        "",
        _md_table(["校核项目", "计算值", "许用值 / 期望值", "结论"], [
            ["齿面接触强度", f"{_num(results.get('contactStress'), 2)} MPa", f"{_num(contact_limit, 2)} MPa（两轮较小值）", _status_text(design.get("contactConclusion"))],
            ["小齿轮齿根弯曲强度", f"{_num(results.get('pinionBendStress'), 2)} MPa", f"{_num(inputs.get('pinionBendAllow'), 1)} MPa", _status_text(design.get("pinionBendConclusion"))],
            ["大齿轮齿根弯曲强度", f"{_num(results.get('gearBendStress'), 2)} MPa", f"{_num(inputs.get('gearBendAllow'), 1)} MPa", _status_text(design.get("gearBendConclusion"))],
            ["高速轴弯扭合成强度", f"{_num(results.get('highShaftStress'), 2)} MPa", f"{_num(inputs.get('highAllowStress'), 1)} MPa", _status_text(design.get("highShaftConclusion"))],
            ["低速轴弯扭合成强度", f"{_num(results.get('shaftStress'), 2)} MPa", f"{_num(inputs.get('lowAllowStress'), 1)} MPa", _status_text(design.get("shaftConclusion"))],
            ["高速轴外伸端键挤压强度", f"{_num(results.get('highKeyPressStress'), 2)} MPa", f"{_num(inputs.get('highKeyPressAllow'), 1)} MPa", _status_text(design.get("highKeyPressConclusion"))],
            ["低速轴齿轮键挤压强度", f"{_num(results.get('lowGearKeyPressStress'), 2)} MPa", f"{_num(inputs.get('lowGearKeyPressAllow'), 1)} MPa", _status_text(design.get("lowGearKeyPressConclusion"))],
            ["低速轴外伸端键挤压强度", f"{_num(results.get('lowOuterKeyPressStress'), 2)} MPa", f"{_num(inputs.get('lowOuterKeyPressAllow'), 1)} MPa", _status_text(design.get("lowOuterKeyPressConclusion"))],
            ["高速轴轴承寿命", f"{_num(results.get('highBearingLife'), 0)} h", f"{_num(results.get('requiredBearingLife'), 0)} h", _status_text(design.get("highBearingConclusion"))],
            ["低速轴轴承寿命", f"{_num(results.get('lowBearingLife'), 0)} h", f"{_num(results.get('requiredBearingLifeLow'), 0)} h", _status_text(design.get("lowBearingConclusion"))],
        ]),
    ])

    if diagram_image_dir:
        diagram_assets = generate_shaft_diagram_images(design, diagram_image_dir)
        if diagram_assets:
            lines.extend(["", "## 4.1 轴受力图", ""])
            groups = [
                ("高速轴", [item for item in diagram_assets if item.get("prefix") == "high"]),
                ("低速轴", [item for item in diagram_assets if item.get("prefix") == "low"]),
            ]
            for title, assets in groups:
                if not assets:
                    continue
                lines.extend([f"### {title}", ""])
                for asset in assets:
                    rel_path = f"{diagram_relative_dir.rstrip('/')}/{asset['filename']}"
                    lines.extend([f"**{asset['title']}**", "", f"![{asset['title']}]({rel_path})", ""])

    if formula_notes:
        formula_section_title = "## 4.2 主要计算公式与计算过程" if diagram_image_dir else "## 4.1 主要计算公式与计算过程"
        lines.extend(["", formula_section_title, ""])
        for item in formula_notes:
            if not isinstance(item, dict):
                continue
            formulas = item.get("formulaLatex") or item.get("formulaMath") or item.get("formula")
            substitutions = item.get("substitutionLatex") or item.get("substitutionMath") or item.get("substitution")
            calculation_steps = item.get("calculationStepLatex") or item.get("calculationSteps") or item.get("calculationStepMath")
            lines.append(_formula_markdown_block(item.get("title") or "计算参数", formulas, substitutions, calculation_steps))
            lines.append("")

    if adjustment_advice:
        advice_section_title = "## 4.3 不满足项调整建议" if diagram_image_dir else "## 4.2 不满足项调整建议"
        lines.extend(["", advice_section_title, ""])
        for index, advice in enumerate(adjustment_advice, start=1):
            lines.append(f"{index}. {_status_text(advice)}")

    housing_height = design_height(inputs, selections)
    vent_mount = cover_vent_layout(inputs, selections)
    lines.extend([
        "",
        "## 5 箱体、附件与模型输出",
        "",
        _md_table(["项目", "选型 / 数值"], [
            ["箱体材料", selections.get("housingMaterial") or "-"],
            ["内腔轴向宽度（实际 / 建议最小）", f"{_num(axial['inner_width'], 2)} / {_num(axial['recommended_inner_width'], 2)} mm"],
            ["轴承座总轴向宽度", f"{_num(axial['outer_width'], 2)} mm"],
            ["共同啮合中面 Y", f"{_num(axial['mesh_center_y'], 2)} mm"],
            ["低速轴套筒长度", "10 mm（固定，不自动调整）"],
            ["齿轮正 / 负端内壁净空", f"{_num(axial['gear_positive_clearance'], 2)} / {_num(axial['gear_negative_clearance'], 2)} mm"],
            ["建模版本", MODEL_PROFILE["label"]],
            ["下箱体肋板厚度", "6 mm（备份模板固定值）"],
            ["齿顶到底部净空（设定）", f"{_num(housing_height['bottom_clearance'], 2)} mm"],
            ["轴心距箱底外表面", f"{_num(housing_height['axis_height'], 2)} mm"],
            ["箱壁高度（不含底板、法兰）", f"{_num(housing_height['wall_height'], 2)} mm"],
            ["齿顶包络到底板内表面最小间隙", f"{_num(housing_height['minimum_envelope_floor_gap'], 2)} mm"],
            ["箱体壁厚填写记录", f"{_num(inputs.get('wallThickness'), 2)} mm（记录值，不驱动当前备份几何）"],
            ["润滑方式", selections.get("lubrication") or "-"],
            ["地脚螺栓填写记录", selections.get("footBolt") or "-"],
            ["端盖螺钉填写记录", selections.get("coverBolt") or "-"],
            ["通气器", selections.get("ventPlug") or "模板示意，规格待复核"],
            ["通气器 X/Y 偏移", f"{_num(vent_mount['ventOffsetX'], 2)} / {_num(vent_mount['ventOffsetY'], 2)} mm（相对大齿轮轴线 / 箱体中面）"],
            ["通气器座面最小抬高量", f"{_num(vent_mount['ventStandOff'], 2)} mm"],
            ["通气器座面距轴心高度", f"{_num(vent_mount['seat_z'], 2)} mm"],
            ["通气器底端到齿顶净空（设定 / 包络计算）", f"{_num(vent_mount['ventGearClearance'], 2)} / {_num(vent_mount['minimum_gear_envelope_gap'], 2)} mm"],
            ["通气器通孔直径（简化几何）", f"{_num(vent_mount['bore_radius'] * 2, 2)} mm；与现有通气器几何配合，不代表填写规格的制造螺纹"],
            ["圆形油标", selections.get("oilGauge") or "模板示意，规格待复核"],
            ["密封方式", "毡圈密封"],
        ]),
        "",
        r"基于用户指定的 2026-10-14 备份，已修正上下箱孔位对齐，并加入底部净空驱动的高度联动。轴心高度 $H=\max(64,10+R_{\max}+c)$，箱壁高度 $H-10-6$（单位 mm）；$R_{\max}$ 为两齿轮齿顶包络半径的较大值，$c$ 为底部净空。12 mm 默认净空仅为几何布置初值，不代表标准或强度校核。轴承连接桥保持低于轴心 19 mm；轴系及箱盖共同上移，油标、放油孔保留底部基准。其他历史壁厚、径向/轴向间隙、内腔控制未接入当前模板。旧模型需重新生成，全模型干涉仍须复核。",
        "",
        "### 模型生成说明",
        "通气器、安装座和贯通孔共用 X/Y 定位。座面高度取局部箱盖最高面加抬高量，与齿顶包络高度加底端净空及插入长度二者的较大值，不再固定在距轴心 61 mm 处。默认抬高 6 mm、齿顶净空 4 mm 仅用于几何布置；未验证螺纹、密封、扳手空间或其他附件的全部干涉。",
        "",
        "系统生成的三维模型主要用于课程设计表达、结构布置检查和装配关系预览，不等同于完整可制造的减速器总成。部分标准件、连接件和附属零件仍需根据设计手册、国家标准或企业标准复核。",
    ])

    model_review = design.get("model_review")
    lines.extend(["", "### 轴向联动后的轴段长度", "",
                  _md_table(["轴段", "长度 / mm"], [[key, _num(value, 2)] for key, value in axial['shaft_lengths'].items()]),
                  "", "套筒固定为 10 mm。轴承座、啮合中面与轴段长度共用轴向尺寸链；高速轴第 2、8 段和低速轴第 1、4 段直径取所选轴承内径。改变轴段尺寸后，旧的轴强度和轴承寿命结论必须重新计算。",
                  *axial['warnings'], ""])
    if stale_axial_checks:
        lines.extend(['旧轴向校核数据已作废：请按联动后的轴段尺寸重新计算轴强度和轴承寿命。', ''])
    if not isinstance(model_review, dict) and "modelReviewRequired" in design:
        model_review = {"assembly_check_passed": design.get("modelAssemblyCheckPassed"),
                        "warnings": design.get("modelWarnings", [])}
    if isinstance(model_review, dict):
        lines.extend(["", "### 本次模型检查", "",
                      "静态实体重叠检查通过，其他工程校核仍待复核。" if model_review.get("assembly_check_passed") is True
                      else "静态装配检查未通过或未完成；模型仅供检查，不是无干涉或可制造结论。"])
        lines.extend(f"- {_md_cell(warning)}" for warning in model_review.get("warnings", []))

    output_path.write_text("\n".join(lines), encoding="utf-8-sig")
    return output_path
