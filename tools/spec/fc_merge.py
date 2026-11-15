"""Per-field last-write-wins merge, tombstones, balance checkpoints and the writer-side rules
for mark paid / unmark / editing Remaining. Spec: spec/sync-protocol.md sections 4 and 5.
"""

import json
import math
import re

import fc_files
import fc_hlc
import fc_ids
import fc_money

COUNTS_AGAINST_REMAINING = ("Regular", "Extra")


def value_key(v):
    """Total order on record values, used only to break exact HLC ties."""
    if v is None:
        return (0,)
    if v is False:
        return (1,)
    if v is True:
        return (2,)
    if isinstance(v, (int, float)):
        return (3, float(v))
    if isinstance(v, str):
        return (4, v.encode("utf-8"))
    raise ValueError("not a record value")


def _is_nonempty_str(x):
    return isinstance(x, str) and x != ""


def is_valid_record(r):
    if not isinstance(r, dict):
        return False
    for k in ("entity", "id", "field", "hlc"):
        if not _is_nonempty_str(r.get(k)):
            return False
    if "value" not in r:
        return False
    v = r["value"]
    if v is not None and not isinstance(v, (bool, int, float, str)):
        return False
    if isinstance(v, float) and not math.isfinite(v):
        return False
    return fc_hlc.is_valid(r["hlc"])


def wins(new_value, new_hlc, old_value, old_hlc):
    c = fc_hlc.compare(new_hlc, old_hlc)
    if c != 0:
        return c > 0
    return value_key(new_value) > value_key(old_value)


class Store:
    """The LWW store: (entity, id, field) -> (value, hlc)."""

    def __init__(self):
        self.cells = {}

    def apply(self, r):
        if not is_valid_record(r):
            return False
        key = (r["entity"], r["id"], r["field"])
        cur = self.cells.get(key)
        if cur is None or wins(r["value"], r["hlc"], cur[0], cur[1]):
            self.cells[key] = (r["value"], r["hlc"])
            return True
        return False

    def apply_all(self, records):
        for r in records:
            self.apply(r)
        return self

    def get(self, entity, id_, field):
        return self.cells.get((entity, id_, field))

    def value(self, entity, id_, field, default=None):
        c = self.get(entity, id_, field)
        return default if c is None else c[0]

    def records(self):
        out = [
            {"entity": e, "id": i, "field": f, "value": v, "hlc": h}
            for (e, i, f), (v, h) in self.cells.items()
        ]
        out.sort(key=lambda r: (r["entity"], r["id"], r["field"]))
        return out

    def rows(self):
        out = {}
        for (e, i, f), (v, _) in sorted(self.cells.items()):
            out.setdefault(e, {}).setdefault(i, {})[f] = v
        return out

    def ids(self, entity):
        return sorted({i for (e, i, _) in self.cells if e == entity})

    def entities(self):
        return sorted({e for (e, _, _) in self.cells})

    def is_live(self, entity, id_):
        if entity == "setting":
            return self.get(entity, id_, "value") is not None
        c = self.get(entity, id_, "deleted")
        return c is not None and c[0] is False

    def live_ids(self, entity):
        return [i for i in self.ids(entity) if self.is_live(entity, i)]

    def live(self):
        return {e: self.live_ids(e) for e in self.entities()}

    # ---- derived values -------------------------------------------------

    def _money_cents(self, entity, id_, field):
        v = self.value(entity, id_, field)
        if isinstance(v, bool) or not isinstance(v, (int, float)):
            return 0
        try:
            return fc_money.cents(v)
        except fc_money.MoneyError:
            return 0

    def _live_payments_for(self, bill_id):
        for pid in self.live_ids("payment"):
            if self.value("payment", pid, "billId") == bill_id:
                yield pid

    def _kind(self, pid):
        """Missing or wrong-type kind reads as the default, Regular (data-model.md 1.2)."""
        k = self.value("payment", pid, "kind")
        return k if isinstance(k, str) else "Regular"

    def mark_hlc(self, pid):
        return self.get("payment", pid, "deleted")[1]

    def is_absorbed(self, pid):
        """A live payment whose mark HLC is at or before its bill's checkpoint HLC."""
        bill = self.value("payment", pid, "billId")
        cp = self.get("bill", bill, "remainingCheckpoint") if isinstance(bill, str) else None
        return cp is not None and fc_hlc.compare(self.mark_hlc(pid), cp[1]) <= 0

    def remaining_unclamped_cents(self, bill_id):
        cp = self.get("bill", bill_id, "remainingCheckpoint")
        cp_cents = self._money_cents("bill", bill_id, "remainingCheckpoint")
        cp_hlc = cp[1] if cp is not None else None
        paid = 0
        for pid in self._live_payments_for(bill_id):
            if self._kind(pid) not in COUNTS_AGAINST_REMAINING:
                continue
            if cp_hlc is None or fc_hlc.compare(self.mark_hlc(pid), cp_hlc) > 0:
                paid += self._money_cents("payment", pid, "amountPaid")
        return cp_cents - paid

    def remaining_cents(self, bill_id):
        return max(0, self.remaining_unclamped_cents(bill_id))

    def set_aside_balance_cents(self, bill_id):
        regular_periods, set_asides = [], []
        for pid in self._live_payments_for(bill_id):
            period = self.value("payment", pid, "periodKey")
            if not fc_ids.is_date(period):
                continue
            kind = self._kind(pid)
            if kind == "Regular":
                regular_periods.append(period)
            elif kind == "SetAside":
                set_asides.append((period, pid))
        last = max(regular_periods) if regular_periods else None
        return sum(
            self._money_cents("payment", pid, "amountPaid")
            for period, pid in set_asides
            if last is None or period > last
        )

    def derived(self):
        bills = self.live_ids("bill")
        return {
            "remaining": {b: self.remaining_cents(b) / 100.0 for b in bills},
            "setAsideBalance": {b: self.set_aside_balance_cents(b) / 100.0 for b in bills},
        }


# ---- writer-side operations --------------------------------------------


def _rec(entity, id_, field, value, hlc):
    return {"entity": entity, "id": id_, "field": field, "value": value, "hlc": hlc}


def payment_id_for(kind, bill_id, period_key):
    if kind == "Regular":
        return fc_ids.regular_payment_id(bill_id, period_key)
    if kind == "SetAside":
        return fc_ids.set_aside_id(bill_id, period_key)
    raise ValueError("deterministic IDs exist only for Regular and SetAside")


def _sorted(recs):
    return sorted(recs, key=lambda r: (r["entity"], r["id"], r["field"]))


def op_mark_paid(store, op):
    """Mark a Regular or SetAside payment paid. `store` is the editing device's state (None = empty)."""
    kind = op.get("kind", "Regular")
    pid = payment_id_for(kind, op["billId"], op["periodKey"])
    h = op["hlc"]
    if store is not None and store.is_live("payment", pid):
        # Already paid on this device: only the amount and time change. Rewriting `deleted`
        # would move the mark HLC past a checkpoint that already absorbed this payment.
        return _sorted([
            _rec("payment", pid, "amountPaid", fc_money.round2(op["amount"]), h),
            _rec("payment", pid, "paidAt", op["paidAt"], h),
        ])
    return _sorted([
        _rec("payment", pid, "amountPaid", fc_money.round2(op["amount"]), h),
        _rec("payment", pid, "billId", op["billId"], h),
        _rec("payment", pid, "deleted", False, h),
        _rec("payment", pid, "kind", kind, h),
        _rec("payment", pid, "paidAt", op["paidAt"], h),
        _rec("payment", pid, "periodKey", op["periodKey"], h),
    ])


def _delete_payment(store, pid, h):
    if not store.is_live("payment", pid):
        return []
    out = [_rec("payment", pid, "deleted", True, h)]
    bill = store.value("payment", pid, "billId")
    if (store._kind(pid) in COUNTS_AGAINST_REMAINING and isinstance(bill, str)
            and store.is_live("bill", bill) and store.is_absorbed(pid)):
        # The checkpoint already reflects this payment; raise it back by the amount.
        new = max(0, store.remaining_unclamped_cents(bill) + store._money_cents("payment", pid, "amountPaid"))
        out.append(_rec("bill", bill, "remainingCheckpoint", new / 100.0, h))
    return _sorted(out)


def op_unmark_paid(store, op):
    pid = payment_id_for(op.get("kind", "Regular"), op["billId"], op["periodKey"])
    return _delete_payment(store, pid, op["hlc"])


def op_delete_payment(store, op):
    return _delete_payment(store, op["paymentId"], op["hlc"])


def op_set_remaining(store, op):
    bill = op["billId"]
    if not store.is_live("bill", bill):
        return []
    new = fc_money.cents(op["value"])
    if new < 0:
        raise ValueError("Remaining cannot be negative")
    if op.get("source") == "notion" and op.get("lastPushed") is not None:
        if fc_money.cents(op["lastPushed"]) == new:
            return []  # echo of the connector's own write
    if new == store.remaining_cents(bill):
        return []  # unchanged value
    return [_rec("bill", bill, "remainingCheckpoint", new / 100.0, op["hlc"])]


RESTORABLE = ("bill", "payment", "balanceSnapshot", "incomeSource", "paycheck")
KNOWN_SETTINGS = {"PayAnchor", "EarlyStart", "SafeToSpendBuffer", "ReminderDaysBefore"}
TYPE_COLOR_PREFIX = "TypeColor/"
# Connector state, not plan data: a restore never writes or clears these (sync-protocol.md 12.3).
NOTION_KEYS = {"NotionOwnerDeviceId", "NotionBillsDb", "NotionPaymentsDb", "NotionSnapshotsDb"}

_HEX_COLOR = re.compile(r"^#[0-9A-Fa-f]{6}$")
_DECIMAL = re.compile(r"^-?[0-9]+(\.[0-9]+)?$")
_DIGITS = re.compile(r"^[0-9]+$")


def is_restorable_setting(key):
    """Synced plan settings a restore writes or clears (never the Notion keys)."""
    return key in KNOWN_SETTINGS or key.startswith(TYPE_COLOR_PREFIX)


def _map_one(key, raw):
    """Convert one backup setting value to its synced value, or None if it cannot be used."""
    if raw is None:
        return None
    if key == "PayAnchor":
        return raw if fc_ids.is_date(raw) else None
    if key == "EarlyStart":
        if isinstance(raw, bool):
            return raw
        return raw.lower() == "true" if isinstance(raw, str) else None  # as today's C# reads it
    if key == "SafeToSpendBuffer":
        if isinstance(raw, str) and _DECIMAL.match(raw):
            raw = float(raw)
        if isinstance(raw, bool) or not isinstance(raw, (int, float)) or not math.isfinite(raw):
            return None
        return fc_money.round2(raw)
    if key == "ReminderDaysBefore":
        if isinstance(raw, str) and _DIGITS.match(raw):
            return int(raw)
        v = fc_files.as_int(raw)
        return v if v is not None and v >= 0 else None
    if key.startswith(TYPE_COLOR_PREFIX):
        return raw.upper() if isinstance(raw, str) and _HEX_COLOR.match(raw) else None
    return None


def map_backup_settings(raw_settings):
    """data-model.md 8.5: map a backup's Settings object (string values today) to synced setting
    values. Returns only restorable keys with a usable value; Notion, per-device and unknown keys
    are dropped."""
    out = {}
    colors = raw_settings.get("TypeColors")
    if isinstance(colors, str):
        try:
            parsed = json.loads(colors)
        except ValueError:
            parsed = None
        if isinstance(parsed, dict):
            for type_name, hex_ in parsed.items():
                v = _map_one(TYPE_COLOR_PREFIX + type_name, hex_)
                if v is not None and type_name != "":
                    out[TYPE_COLOR_PREFIX + type_name] = v
    for key, raw in raw_settings.items():
        if key in NOTION_KEYS or not is_restorable_setting(key) or key == TYPE_COLOR_PREFIX:
            continue
        v = _map_one(key, raw)
        if v is not None:
            out[key] = v  # an explicit TypeColor/<type> key overrides the TypeColors entry
    return out


def op_restore(store, op):
    """Restore a backup with sync on. op.rows: {entity: {id: {field: value}}} (IDs already
    migrated, bill Remaining already mapped to remainingCheckpoint); op.settings: the backup's
    Settings object as stored in the file (mapped here per data-model.md 8.5)."""
    h = op["hlc"]
    rows = op.get("rows", {})
    settings = map_backup_settings(op.get("settings", {}))
    for entity in rows:
        if entity not in RESTORABLE:
            raise ValueError(f"restore does not write {entity} rows")
    out = []
    for entity in RESTORABLE:
        backup = rows.get(entity, {})
        for id_, fields in backup.items():
            for f, v in fields.items():
                if f != "deleted":
                    out.append(_rec(entity, id_, f, v, h))
            out.append(_rec(entity, id_, "deleted", False, h))
        for id_ in store.ids(entity):
            if id_ not in backup and store.value(entity, id_, "deleted") is not True:
                out.append(_rec(entity, id_, "deleted", True, h))
    for key, v in sorted(settings.items()):
        out.append(_rec("setting", key, "value", v, h))
    for key in store.ids("setting"):
        if key not in settings and is_restorable_setting(key) and store.value("setting", key, "value") is not None:
            out.append(_rec("setting", key, "value", None, h))
    return _sorted(out)


OPS = {
    "markPaid": op_mark_paid,
    "unmarkPaid": op_unmark_paid,
    "deletePayment": op_delete_payment,
    "setRemaining": op_set_remaining,
    "restore": op_restore,
}


def run_op(store, op):
    return OPS[op["op"]](store, op)
