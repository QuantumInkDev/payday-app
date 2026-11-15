"""IDs, as specified in spec/sync-protocol.md section 3.

UUIDv5 is implemented here with hashlib so check_fixtures.py can cross-check it against
Python's uuid.uuid5.
"""

import datetime
import hashlib
import re

NAMESPACE = "def9d7a0-dc52-41e0-906a-f9767c7f894d"

_UUID_RE = re.compile(r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")
_UUID_ANYCASE_RE = re.compile(r"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")
_DATE_RE = re.compile(r"[0-9]{4}-[0-9]{2}-[0-9]{2}")
# Every pattern is matched against the WHOLE string (fullmatch) and uses ASCII classes only.


def ascii_lower(s):
    return "".join(chr(ord(c) + 32) if "A" <= c <= "Z" else c for c in s)


class IdError(ValueError):
    def __init__(self, code):
        super().__init__(code)
        self.code = code


def _fmt(b):
    h = b.hex()
    return f"{h[0:8]}-{h[8:12]}-{h[12:16]}-{h[16:20]}-{h[20:32]}"


def is_canonical_uuid(s):
    return isinstance(s, str) and bool(_UUID_RE.fullmatch(s))


def canonicalize_uuid(s):
    """Lowercase a UUID in 8-4-4-4-12 form. Raises if s is not a UUID."""
    if not isinstance(s, str) or not _UUID_ANYCASE_RE.fullmatch(s):
        raise IdError("notAUuid")
    return ascii_lower(s)


def is_date(s):
    if not isinstance(s, str) or not _DATE_RE.fullmatch(s):
        return False
    try:
        datetime.date.fromisoformat(s)
        return True
    except ValueError:
        return False


def uuid5(namespace, name):
    ns = bytes.fromhex(namespace.replace("-", ""))
    digest = hashlib.sha1(ns + name.encode("utf-8")).digest()[:16]
    b = bytearray(digest)
    b[6] = (b[6] & 0x0F) | 0x50
    b[8] = (b[8] & 0x3F) | 0x80
    return _fmt(bytes(b))


def name_regular_payment(bill_id, period_key):
    _require_uuid(bill_id)
    _require_date(period_key)
    return f"payment/regular/{bill_id}/{period_key}"


def name_set_aside(bill_id, period_key):
    _require_uuid(bill_id)
    _require_date(period_key)
    return f"payment/setaside/{bill_id}/{period_key}"


def name_paycheck(income_source_id, pay_date):
    _require_uuid(income_source_id)
    _require_date(pay_date)
    return f"paycheck/{income_source_id}/{pay_date}"


def name_legacy_bill(old_id):
    if not isinstance(old_id, str) or old_id == "" or is_canonical_uuid(ascii_lower(old_id)):
        raise IdError("notLegacy")
    return f"legacy-bill/{old_id}"


_TS_RE = re.compile(r"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\.[0-9]{3}Z")


def _require_occurrence(n):
    if isinstance(n, bool) or not isinstance(n, int) or n < 1:
        raise IdError("badInput")


def _require_cents(c):
    if isinstance(c, bool) or not isinstance(c, int):
        raise IdError("badInput")


def name_legacy_payment(bill_id, period_key, amount_cents, paid_at, n):
    """Extra payments made by the migration (duplicate legacy rows for one bill and period)."""
    _require_uuid(bill_id)
    _require_date(period_key)
    _require_cents(amount_cents)
    if paid_at != "" and not (isinstance(paid_at, str) and _TS_RE.fullmatch(paid_at)):
        raise IdError("badInput")
    _require_occurrence(n)
    return f"legacy-payment/{bill_id}/{period_key}/{amount_cents}/{paid_at}/{n}"


def name_legacy_snapshot(snapshot_date, total_cents, n):
    _require_date(snapshot_date)
    _require_cents(total_cents)
    _require_occurrence(n)
    return f"legacy-snapshot/{snapshot_date}/{total_cents}/{n}"


def legacy_payment_id(bill_id, period_key, amount_cents, paid_at, n):
    return uuid5(NAMESPACE, name_legacy_payment(bill_id, period_key, amount_cents, paid_at, n))


def legacy_snapshot_id(snapshot_date, total_cents, n):
    return uuid5(NAMESPACE, name_legacy_snapshot(snapshot_date, total_cents, n))


SCHEDULED_CADENCES = ("Weekly", "Bi-Weekly", "Semi-Monthly", "Monthly")


def paycheck_id_kind(cadence):
    """Scheduled cadences get the deterministic paycheck ID; Irregular (or unknown) gets UUIDv7."""
    return "uuidv5" if cadence in SCHEDULED_CADENCES else "uuidv7"


def regular_payment_id(bill_id, period_key):
    return uuid5(NAMESPACE, name_regular_payment(bill_id, period_key))


def set_aside_id(bill_id, period_key):
    return uuid5(NAMESPACE, name_set_aside(bill_id, period_key))


def paycheck_id(income_source_id, pay_date):
    return uuid5(NAMESPACE, name_paycheck(income_source_id, pay_date))


def migrate_bill_id(old_id):
    """Bill ID migration: UUID-shaped IDs are lowercased, anything else maps via UUIDv5."""
    if isinstance(old_id, str) and _UUID_ANYCASE_RE.fullmatch(old_id):
        return ascii_lower(old_id)
    return uuid5(NAMESPACE, name_legacy_bill(old_id))


def _require_uuid(s):
    if not is_canonical_uuid(s):
        raise IdError("badInput")


def _require_date(s):
    if not is_date(s):
        raise IdError("badInput")


def uuid7(unix_ms, rand10):
    """UUIDv7 from a 48-bit millisecond timestamp and 10 random bytes (layout in the spec)."""
    if not (0 <= unix_ms < (1 << 48)) or len(rand10) != 10:
        raise IdError("badInput")
    b = bytearray(16)
    b[0:6] = unix_ms.to_bytes(6, "big")
    b[6] = 0x70 | (rand10[0] & 0x0F)
    b[7] = rand10[1]
    b[8] = 0x80 | (rand10[2] & 0x3F)
    b[9:16] = rand10[3:10]
    return _fmt(bytes(b))


def inspect_uuid(s):
    """Layout check used by ids.json 'inspect' cases."""
    if not is_canonical_uuid(s):
        return {"canonical": False}
    raw = bytes.fromhex(s.replace("-", ""))
    version = raw[6] >> 4
    variant_ok = (raw[8] & 0xC0) == 0x80
    out = {"canonical": True, "version": version, "rfc9562Variant": variant_ok}
    if version == 7:
        out["unixMs"] = int.from_bytes(raw[0:6], "big")
    return out
