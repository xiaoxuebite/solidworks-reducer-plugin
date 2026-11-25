"""Unshifted external gears with normal module, normal angle and normal addendum."""

import math


def normal_gear_dimensions(module, teeth, pressure_angle=20, helix_angle=0):
    values = (module, teeth, pressure_angle, helix_angle)
    if not all(math.isfinite(value) for value in values):
        raise ValueError("Gear geometry must be finite")
    if module <= 0 or teeth < 6 or not 0 < pressure_angle < 45 or abs(helix_angle) > 35:
        raise ValueError("Unsupported normal gear geometry")
    beta = math.radians(helix_angle)
    cos_beta = math.cos(beta)
    mt = module / cos_beta
    alpha_t = math.atan(math.tan(math.radians(pressure_angle)) / cos_beta)
    pitch = mt * teeth
    return {"transverse_module": mt, "transverse_pressure_angle": math.degrees(alpha_t),
            "pitch_diameter": pitch, "base_diameter": pitch * math.cos(alpha_t),
            "tip_diameter": pitch + 2 * module, "root_diameter": pitch - 2.5 * module}


def cad_gear_parameters(module, pressure_angle=20, helix_angle=0):
    """Adapt the normal system to cq_gears' transverse-section constructor."""
    geometry = normal_gear_dimensions(module, 20, pressure_angle, helix_angle)
    cos_beta = math.cos(math.radians(helix_angle))
    return {"module": geometry["transverse_module"],
            "pressure_angle": geometry["transverse_pressure_angle"],
            "helix_angle": helix_angle, "addendum_coeff": cos_beta,
            "dedendum_coeff": 1.25 * cos_beta}


def transverse_contact_ratio(module, z1, z2, pressure_angle=20, helix_angle=0):
    """Textbook Eq. (10-20), with working angle equal to alpha_t at standard center distance."""
    gears = [normal_gear_dimensions(module, z, pressure_angle, helix_angle) for z in (z1, z2)]
    alpha_t = math.radians(gears[0]["transverse_pressure_angle"])
    tip_angles = [math.acos(max(-1, min(1, g["base_diameter"] / g["tip_diameter"])))
                  for g in gears]
    return (z1 * (math.tan(tip_angles[0]) - math.tan(alpha_t))
            + z2 * (math.tan(tip_angles[1]) - math.tan(alpha_t))) / (2 * math.pi)


def driven_mesh_phase(z1, z2, module, pinion_width, gear_width, helix_angle=0):
    """Local Z tooth phase for the reducer's X-to-Y and Z-to-Y gear frames.

    Evaluate the tooth/gap relation at the common face midplane. The pinion's
    frame contributes a quarter turn, the wheel's line of centers a half turn.
    cq_gears twists row vectors, opposite a positive CadQuery Z rotation.
    """
    beta = math.radians(helix_angle)
    mt = module / math.cos(beta)
    twist = (pinion_width - gear_width) * math.tan(beta) / (2 * math.pi * mt)
    fraction = (z1 / 4 + z2 / 2 + twist) % 1
    return -((0.5 - fraction) % 1) * 360 / z2
