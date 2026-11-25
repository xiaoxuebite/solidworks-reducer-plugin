"""Common bolt/pad datum at the large bearing boss, independent of gear envelope."""

import math

LOWER_FLANGE_THICKNESS = 6
LOWER_PAD_DROP = 12
LOWER_PAD_REACH = 18
PAD_ROOT_OVERLAP = 0.05


def large_pad_center_x(shaft_x, boss_radius):
    """Keep the lower pad's inner end just inside the boss at its lowest edge.

    The lid uses the same bolt axis. Its existing 20 mm pad reach also overlaps
    the boss. The overlap is internal to the housing/lid union.
    """
    drop = LOWER_FLANGE_THICKNESS + LOWER_PAD_DROP
    boss_edge_x = shaft_x + math.sqrt(boss_radius ** 2 - drop ** 2)
    return boss_edge_x + LOWER_PAD_REACH - PAD_ROOT_OVERLAP
