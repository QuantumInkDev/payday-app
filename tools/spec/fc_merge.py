"""Per-field last-write-wins merge, tombstones, balance checkpoints and the writer-side rules
for mark paid / unmark / editing Remaining. Spec: spec/sync-protocol.md sections 4 and 5.
"""

import math

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
        k = self.value("payment", pid, "kind", "Regular")
        return k if isinstance(k, str) else None

    def remaining_cents(self, bill_id):
        cp = self.get("bill", bill_id, "remainingCheckpoint")
        cp_cents = self._money_cents("bill", bill_id, "remainingCheckpoint")
        cp_hlc = cp[1] if cp is not None else None
        paid = 0
        for pid in self._live_payments_for(bill_id):
            if self._kind(pid) not in COUNTS_AGAINST_REMAINING:
                continue
            mark_hlc = self.get("payment", pid, "deleted")[1]
            if cp_hlc is None or fc_hlc.compare(mark_hlc, cp_hlc) > 0:
                paid += self._money_cents("payment", pid, "amountPaid")
        return max(0, cp_cents - paid)

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


def op_mark_paid(store, op):
    kind = op.get("kind", "Regular")
    pid = payment_id_for(kind, op["billId"], op["periodKey"])
    h = op["hlc"]
    return sorted(
        [
            _rec("payment", pid, "amountPaid", fc_money.round2(op["amount"]), h),
            _rec("payment", pid, "billId", op["billId"], h),
            _rec("payment", pid, "deleted", False, h),
            _rec("payment", pid, "kind", kind, h),
            _rec("payment", pid, "paidAt", op["paidAt"], h),
            _rec("payment", pid, "periodKey", op["periodKey"], h),
        ],
        key=lambda r: r["field"],
    )


def op_unmark_paid(store, op):
    pid = payment_id_for(op.get("kind", "Regular"), op["billId"], op["periodKey"])
    if not store.is_live("payment", pid):
        return []
    return [_rec("payment", pid, "deleted", True, op["hlc"])]


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


OPS = {
    "markPaid": op_mark_paid,
    "unmarkPaid": op_unmark_paid,
    "setRemaining": op_set_remaining,
}


def run_op(store, op):
    return OPS[op["op"]](store, op)
