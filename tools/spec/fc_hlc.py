"""Hybrid logical clock, as specified in spec/sync-protocol.md section 2.

Reference implementation for fixture generation and checking only. Apps implement this
themselves in C#, Swift, Kotlin and TypeScript.

Canonical string: <wall: 12 lowercase hex>-<counter: 4 lowercase hex>-<deviceId: 16 lowercase hex>
"""

import re

MAX_WALL = (1 << 48) - 1
MAX_COUNTER = 0xFFFF
SKEW_WARNING_MS = 10 * 60 * 1000  # 10 minutes

_HLC_RE = re.compile(r"([0-9a-f]{12})-([0-9a-f]{4})-([0-9a-f]{16})")
_DEVICE_RE = re.compile(r"[0-9a-f]{16}")
# Every pattern is matched against the WHOLE string (fullmatch) and uses ASCII classes only.


class HlcError(ValueError):
    def __init__(self, code):
        super().__init__(code)
        self.code = code


def is_device_id(s):
    return isinstance(s, str) and bool(_DEVICE_RE.fullmatch(s))


def encode(wall, counter, device):
    if not isinstance(wall, int) or wall < 0 or wall > MAX_WALL:
        raise HlcError("wallOutOfRange")
    if not isinstance(counter, int) or counter < 0 or counter > MAX_COUNTER:
        raise HlcError("counterOutOfRange")
    if not is_device_id(device):
        raise HlcError("badDeviceId")
    return f"{wall:012x}-{counter:04x}-{device}"


def decode(s):
    if not isinstance(s, str):
        raise HlcError("malformed")
    m = _HLC_RE.fullmatch(s)
    if not m:
        raise HlcError("malformed")
    return int(m.group(1), 16), int(m.group(2), 16), m.group(3)


def is_valid(s):
    try:
        decode(s)
        return True
    except HlcError:
        return False


def compare(a, b):
    ta, tb = decode(a), decode(b)
    return (ta > tb) - (ta < tb)


def tick(last, now_ms, device):
    """New HLC for a local event. `last` is the device's stored clock state (or None)."""
    if last is None:
        return encode(now_ms, 0, device)
    lw, lc, _ = decode(last)
    w = max(now_ms, lw)
    if w == lw:
        c = lc + 1
        if c > MAX_COUNTER:
            w, c = w + 1, 0
    else:
        c = 0
    if w > MAX_WALL:
        raise HlcError("wallOutOfRange")
    return encode(w, c, device)


def observe(last, remote, device):
    """Clock state after merging a record stamped `remote`. Never ticks."""
    rw, rc, _ = decode(remote)
    if last is None:
        return encode(rw, rc, device)
    lw, lc, _ = decode(last)
    if (rw, rc) > (lw, lc):
        return encode(rw, rc, device)
    return encode(lw, lc, device)


def skew_warning(remote, now_ms):
    rw, _, _ = decode(remote)
    return rw - now_ms > SKEW_WARNING_MS
