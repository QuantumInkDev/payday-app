"""Money rounding, as specified in spec/data-model.md section 3.

cents(x) mirrors .NET's Math.Round(x, 2, MidpointRounding.AwayFromZero) for |x| < 1e13:
scale by 100 in IEEE-754 double arithmetic, truncate, add one if the fractional part is >= 0.5.
"""

import math

MAX_ABS = 1e13


class MoneyError(ValueError):
    pass


def cents(x):
    if isinstance(x, bool) or not isinstance(x, (int, float)):
        raise MoneyError("notANumber")
    x = float(x)
    if not math.isfinite(x) or abs(x) >= MAX_ABS:
        raise MoneyError("outOfRange")
    s = abs(x) * 100.0
    i = math.trunc(s)
    if s - i >= 0.5:
        i += 1
    return -i if x < 0 else i


def round2(x):
    return cents(x) / 100.0
