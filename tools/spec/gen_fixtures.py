#!/usr/bin/env python3
"""Generate spec/conformance/sync/*.json.

Inputs and the scenario expectations are written by hand in this file. Merge scenarios carry a
hand-written expectation (live rows, Remaining, key field values) that the reference merge must
reproduce before the fixture is written; the full expected state in the fixture is then taken from
the reference merge. check_fixtures.py re-derives everything from the JSON alone.

Usage (needs the `cryptography` package):
    python3 tools/spec/gen_fixtures.py
"""

import datetime
import hashlib
import json
import os
import sys

sys.dont_write_bytecode = True  # keep __pycache__ out of the repo
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import fc_bip39 as B  # noqa: E402
import fc_compact as K  # noqa: E402
import fc_files as FL  # noqa: E402
import fc_frame as F  # noqa: E402
import fc_hlc as H  # noqa: E402
import fc_ids as I  # noqa: E402
import fc_merge as G  # noqa: E402

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
OUT = os.path.join(ROOT, "spec", "conformance", "sync")

T0 = 1794657600000  # 2026-11-14T12:00:00.000Z
DEV_A = "0a0a0a0a0a0a0a0a"
DEV_B = "0b0b0b0b0b0b0b0b"
DEV_C = "0c0c0c0c0c0c0c0c"

# Fictional row IDs (valid UUIDv7 layout).
BILL_1 = "019a0000-0000-7000-8000-0000000000b1"  # Woodgrove Rewards (card)
BILL_2 = "019a0000-0000-7000-8000-0000000000b2"  # Contoso Card
BILL_3 = "019a0000-0000-7000-8000-0000000000b3"  # Home Insurance (yearly)
INCOME_1 = "019a0000-0000-7000-8000-0000000000c1"
EXTRA_1 = "019a0000-0000-7000-8000-0000000000e1"
EXTRA_2 = "019a0000-0000-7000-8000-0000000000e2"
VAULT_ID = "019a0000-0000-7000-8000-00000000fa01"

P1, P2, P3 = "2026-10-29", "2026-11-12", "2026-11-26"  # pay-period keys from anchor 2025-03-20


def h(sec, counter=0, dev=DEV_A):
    return H.encode(T0 + sec * 1000, counter, dev)


def iso(sec):
    t = datetime.datetime.fromtimestamp((T0 + sec * 1000) / 1000, tz=datetime.timezone.utc)
    return t.strftime("%Y-%m-%dT%H:%M:%S.") + f"{t.microsecond // 1000:03d}Z"


def r(entity, id_, field, value, hlc):
    return {"entity": entity, "id": id_, "field": field, "value": value, "hlc": hlc}


def bill(bid, hlc, name, payment, checkpoint, type_="Cards"):
    return [
        r("bill", bid, "deleted", False, hlc),
        r("bill", bid, "name", name, hlc),
        r("bill", bid, "payment", payment, hlc),
        r("bill", bid, "remainingCheckpoint", checkpoint, hlc),
        r("bill", bid, "type", type_, hlc),
    ]


def mark(bid, period, amount, hlc, kind="Regular", paid_at=None):
    sec = (H.decode(hlc)[0] - T0) // 1000
    return G.op_mark_paid(None, {
        "billId": bid, "periodKey": period, "amount": amount, "kind": kind,
        "paidAt": paid_at or iso(sec), "hlc": hlc,
    })


def extra(pid, bid, period, amount, hlc):
    sec = (H.decode(hlc)[0] - T0) // 1000
    return [
        r("payment", pid, "amountPaid", amount, hlc),
        r("payment", pid, "billId", bid, hlc),
        r("payment", pid, "deleted", False, hlc),
        r("payment", pid, "kind", "Extra", hlc),
        r("payment", pid, "paidAt", iso(sec), hlc),
        r("payment", pid, "periodKey", period, hlc),
    ]


def play(records, ops):
    """Run ops in order on one device that has merged `records`. Returns the records the ops emitted."""
    store = G.Store().apply_all(records)
    out = []
    for op in ops:
        emitted = G.run_op(store, op)
        store.apply_all(emitted)
        out.extend(emitted)
    return out


def op_mark(bid, period, amount, hlc, kind="Regular"):
    sec = (H.decode(hlc)[0] - T0) // 1000
    return {"op": "markPaid", "billId": bid, "periodKey": period, "kind": kind, "amount": amount,
            "paidAt": iso(sec), "hlc": hlc}


def op_unmark(bid, period, hlc, kind="Regular"):
    return {"op": "unmarkPaid", "billId": bid, "periodKey": period, "kind": kind, "hlc": hlc}


def op_remaining(bid, value, hlc, source="app", last_pushed=None):
    op = {"op": "setRemaining", "billId": bid, "value": value, "source": source, "hlc": hlc}
    if last_pushed is not None:
        op["lastPushed"] = last_pushed
    return op


def edit_case(name, records, op, expect, note):
    """A merge case whose edit runs on a device that has seen every record in `records`."""
    return merge_case(name, records, expect, note, edit={"seen": list(range(len(records))), "op": op})


def rid(bid, period):
    return I.regular_payment_id(bid, period)


def sid(bid, period):
    return I.set_aside_id(bid, period)


def write(name, description, cases):
    doc = {"version": 1, "description": description, "cases": cases}
    path = os.path.join(OUT, name)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(doc, f, indent=2, ensure_ascii=False)
        f.write("\n")
    print(f"wrote {os.path.relpath(path, ROOT)} ({len(cases)} cases)")


def expect_error(fn, *args):
    try:
        fn(*args)
    except (H.HlcError, I.IdError, F.FrameError, B.PhraseError, FL.FileError) as e:
        return {"error": e.code}
    raise AssertionError(f"{fn.__name__}{args} did not fail")


# ---------------------------------------------------------------------------
# hlc.json
# ---------------------------------------------------------------------------


def gen_hlc():
    cases = []

    def add(name, inp, exp, note=None):
        c = {"name": name, "input": inp, "expected": exp}
        if note:
            c["note"] = note
        cases.append(c)

    for name, w, c, d in [
        ("encode zero", 0, 0, DEV_A),
        ("encode fixture base time", T0, 0, DEV_A),
        ("encode with counter", T0, 1, DEV_B),
        ("encode counter uses 4 lowercase hex digits", T0 + 1, 0x00AB, DEV_C),
        ("encode maximum wall and counter", H.MAX_WALL, H.MAX_COUNTER, "ffffffffffffffff"),
    ]:
        add(name, {"fn": "encode", "wall": w, "counter": c, "deviceId": d}, {"hlc": H.encode(w, c, d)})
    for name, w, c, d in [
        ("encode rejects wall above 48 bits", H.MAX_WALL + 1, 0, DEV_A),
        ("encode rejects negative wall", -1, 0, DEV_A),
        ("encode rejects counter above 16 bits", T0, 0x10000, DEV_A),
        ("encode rejects uppercase device ID", T0, 0, "0A0A0A0A0A0A0A0A"),
        ("encode rejects short device ID", T0, 0, "0a0a0a0a0a0a0a0"),
    ]:
        add(name, {"fn": "encode", "wall": w, "counter": c, "deviceId": d}, expect_error(H.encode, w, c, d))

    for s in [h(0), h(1, 2, DEV_B), "000000000000-0000-0000000000000000", "ffffffffffff-ffff-ffffffffffffffff"]:
        w, c, d = H.decode(s)
        add(f"decode {s}", {"fn": "decode", "hlc": s}, {"wall": w, "counter": c, "deviceId": d})
    bad = [
        ("decode rejects uppercase hex", h(0).upper()),
        ("decode rejects missing separators", h(0).replace("-", "")),
        ("decode rejects wrong separator", h(0).replace("-", ":")),
        ("decode rejects short wall", h(0)[1:]),
        ("decode rejects trailing whitespace", h(0) + " "),
        ("decode rejects leading whitespace", " " + h(0)),
        ("decode rejects non-hex device", h(0)[:-1] + "g"),
        ("decode rejects empty string", ""),
        ("decode rejects 13-digit wall", "0" + h(0)),
    ]
    for name, s in bad:
        add(name, {"fn": "decode", "hlc": s}, expect_error(H.decode, s))

    cmp_cases = [
        ("compare: smaller wall is earlier", h(0), h(1)),
        ("compare: wall dominates counter", H.encode(T0, 0xFFFF, DEV_A), H.encode(T0 + 1, 0, DEV_A)),
        ("compare: same wall, counter decides", H.encode(T0, 1, DEV_B), H.encode(T0, 2, DEV_A)),
        ("compare: same wall and counter, device ID decides", h(5, 0, DEV_A), h(5, 0, DEV_B)),
        ("compare: equal", h(5, 3, DEV_C), h(5, 3, DEV_C)),
        ("compare: later is greater", h(9), h(8, 0xFFFF, DEV_C)),
        ("compare: hex digit boundary (0x0fff... vs 0x1000...)",
         H.encode(0x0FFFFFFFFFFF, 0, DEV_A), H.encode(0x100000000000, 0, DEV_A)),
    ]
    for name, a, b in cmp_cases:
        add(name, {"fn": "compare", "a": a, "b": b}, {"result": H.compare(a, b)},
            note="result is -1 if a < b, 0 if equal, 1 if a > b; equals byte-wise comparison of the strings")

    tick_cases = [
        ("tick with no previous state starts at now, counter 0", None, T0),
        ("tick when the clock moved forward resets the counter", h(0, 5), T0 + 1000),
        ("tick in the same millisecond increments the counter", h(0, 5), T0),
        ("tick when the clock went backwards keeps the wall time and increments", h(10, 0), T0),
        ("tick at counter 0xffff carries into the wall time", H.encode(T0, 0xFFFF, DEV_A), T0),
        ("tick after observing a future remote clock keeps the remote wall time", H.encode(T0 + 3600000, 7, DEV_A), T0),
    ]
    for name, last, now in tick_cases:
        add(name, {"fn": "tick", "last": last, "now": now, "deviceId": DEV_A}, {"hlc": H.tick(last, now, DEV_A)})
    add("tick fails if the wall time would exceed 48 bits",
        {"fn": "tick", "last": H.encode(H.MAX_WALL, 0xFFFF, DEV_A), "now": 0, "deviceId": DEV_A},
        expect_error(H.tick, H.encode(H.MAX_WALL, 0xFFFF, DEV_A), 0, DEV_A))

    obs_cases = [
        ("observe with no previous state adopts the remote wall and counter", None, h(3, 2, DEV_B)),
        ("observe a later remote wall time", h(0, 9), h(3, 2, DEV_B)),
        ("observe an earlier remote: state unchanged", h(10, 1), h(3, 2, DEV_B)),
        ("observe same wall, higher counter", h(3, 1), h(3, 2, DEV_B)),
        ("observe same wall and counter from another device: unchanged", h(3, 2), h(3, 2, DEV_B)),
    ]
    for name, last, remote in obs_cases:
        add(name, {"fn": "observe", "last": last, "remote": remote, "deviceId": DEV_A},
            {"hlc": H.observe(last, remote, DEV_A)},
            note="the state always carries the local device ID; observe never increments")

    seqs = [
        ("sequence: local edits in one millisecond, then a later millisecond",
         [{"op": "tick", "now": T0}, {"op": "tick", "now": T0}, {"op": "tick", "now": T0}, {"op": "tick", "now": T0 + 1}]),
        ("sequence: observe a device that is ahead, then edit; the edit sorts after the remote",
         [{"op": "tick", "now": T0}, {"op": "observe", "remote": h(120, 4, DEV_B)}, {"op": "tick", "now": T0 + 5}]),
        ("sequence: observe an equal remote, then edit",
         [{"op": "observe", "remote": h(0, 0, DEV_B)}, {"op": "tick", "now": T0}]),
        ("sequence: clock jumps back an hour; edits stay ordered",
         [{"op": "tick", "now": T0 + 3600000}, {"op": "tick", "now": T0}, {"op": "tick", "now": T0 + 1}]),
    ]
    for name, steps in seqs:
        state, outs = None, []
        for s in steps:
            state = H.tick(state, s["now"], DEV_A) if s["op"] == "tick" else H.observe(state, s["remote"], DEV_A)
            outs.append(state)
        add(name, {"fn": "sequence", "deviceId": DEV_A, "steps": steps}, {"states": outs})

    for name, remote, now in [
        ("skew warning: remote 10 minutes ahead exactly is not flagged", H.encode(T0 + 600000, 0, DEV_B), T0),
        ("skew warning: remote more than 10 minutes ahead is flagged", H.encode(T0 + 600001, 0, DEV_B), T0),
        ("skew warning: remote behind is not flagged", h(-86400, 0, DEV_B), T0),
    ]:
        add(name, {"fn": "skewWarning", "remote": remote, "now": now}, {"warning": H.skew_warning(remote, now)})

    write("hlc.json", "Hybrid logical clock encoding, comparison and update rules (sync-protocol.md section 2).", cases)


# ---------------------------------------------------------------------------
# ids.json
# ---------------------------------------------------------------------------


def gen_ids():
    cases = []

    def add(name, inp, exp, note=None):
        c = {"name": name, "input": inp, "expected": exp}
        if note:
            c["note"] = note
        cases.append(c)

    for bid, pk in [(BILL_1, P1), (BILL_1, P2), (BILL_2, P2), ("00000000-0000-0000-0000-000000000000", "2025-03-20")]:
        add(f"regular payment ID for {bid} / {pk}", {"fn": "regularPaymentId", "billId": bid, "periodKey": pk},
            {"name": I.name_regular_payment(bid, pk), "id": I.regular_payment_id(bid, pk)})
    for bid, pk in [(BILL_3, P1), (BILL_3, P2)]:
        add(f"set-aside ID for {bid} / {pk}", {"fn": "setAsideId", "billId": bid, "periodKey": pk},
            {"name": I.name_set_aside(bid, pk), "id": I.set_aside_id(bid, pk)})
    add("regular and set-aside IDs differ for the same bill and period",
        {"fn": "setAsideId", "billId": BILL_1, "periodKey": P1},
        {"name": I.name_set_aside(BILL_1, P1), "id": I.set_aside_id(BILL_1, P1)},
        note=f"compare with the regular payment ID {I.regular_payment_id(BILL_1, P1)}")
    for src, d in [(INCOME_1, "2026-11-20"), (INCOME_1, "2026-12-04")]:
        add(f"paycheck ID for {src} / {d}", {"fn": "paycheckId", "incomeSourceId": src, "payDate": d},
            {"name": I.name_paycheck(src, d), "id": I.paycheck_id(src, d)})
    for name, fn, args in [
        ("regular payment ID rejects an uppercase bill ID (canonicalize first)", "regularPaymentId",
         {"billId": BILL_1.upper(), "periodKey": P1}),
        ("regular payment ID rejects a legacy bill ID (migrate first)", "regularPaymentId", {"billId": "13", "periodKey": P1}),
        ("regular payment ID rejects an unpadded date", "regularPaymentId", {"billId": BILL_1, "periodKey": "2026-11-5"}),
        ("set-aside ID rejects an impossible date", "setAsideId", {"billId": BILL_3, "periodKey": "2026-02-30"}),
        ("paycheck ID rejects a timestamp instead of a date", "paycheckId",
         {"incomeSourceId": INCOME_1, "payDate": "2026-11-20T00:00:00Z"}),
    ]:
        f = {"regularPaymentId": I.regular_payment_id, "setAsideId": I.set_aside_id, "paycheckId": I.paycheck_id}[fn]
        add(name, dict({"fn": fn}, **args), expect_error(f, *args.values()))

    for cad in ["Weekly", "Bi-Weekly", "Semi-Monthly", "Monthly", "Irregular", "Quarterly", ""]:
        add(f"paycheck ID kind for cadence {cad!r}", {"fn": "paycheckIdKind", "cadence": cad},
            {"kind": I.paycheck_id_kind(cad)},
            note="Scheduled cadences derive the ID from (source, pay date); Irregular and unknown cadences use "
                 "a random UUIDv7 so two payouts on one day stay two rows")

    for args in [
        (BILL_1, P1, 5500, "2026-10-30T15:04:05.000Z", 1),
        (BILL_1, P1, 5500, "2026-10-30T15:04:05.000Z", 2),
        (BILL_1, P1, 5500, "", 1),
        (BILL_2, P2, 4000, "2026-11-13T09:00:00.000Z", 1),
    ]:
        add(f"legacy payment ID {args}", {"fn": "legacyPaymentId", "billId": args[0], "periodKey": args[1],
                                          "amountCents": args[2], "paidAt": args[3], "occurrence": args[4]},
            {"name": I.name_legacy_payment(*args), "id": I.legacy_payment_id(*args)},
            note="Used only for extra payments created by the migration (data-model.md 8.3)")
    for name, args in [
        ("legacy payment ID rejects a paidAt that is not normalized", (BILL_1, P1, 5500, "2026-10-30 15:04:05", 1)),
        ("legacy payment ID rejects occurrence 0", (BILL_1, P1, 5500, "", 0)),
    ]:
        add(name, {"fn": "legacyPaymentId", "billId": args[0], "periodKey": args[1], "amountCents": args[2],
                   "paidAt": args[3], "occurrence": args[4]}, expect_error(I.legacy_payment_id, *args))
    for args in [("2026-10-29", 812345, 1), ("2026-10-29", 812345, 2), ("2026-11-12", 0, 1)]:
        add(f"legacy balance snapshot ID {args}", {"fn": "legacySnapshotId", "snapshotDate": args[0],
                                                   "totalCents": args[1], "occurrence": args[2]},
            {"name": I.name_legacy_snapshot(*args), "id": I.legacy_snapshot_id(*args)})
    add("legacy balance snapshot ID rejects a bad date",
        {"fn": "legacySnapshotId", "snapshotDate": "2026-10-32", "totalCents": 1, "occurrence": 1},
        expect_error(I.legacy_snapshot_id, "2026-10-32", 1, 1))

    for old in ["1", "13", "27", "Legacy Bill"]:
        add(f"migrate legacy bill ID {old!r}", {"fn": "migrateBillId", "id": old},
            {"name": I.name_legacy_bill(old), "id": I.migrate_bill_id(old)})
    for old in ["9F6C1E2A-3B4D-4E5F-8A6B-7C8D9E0F1A2B", "9f6c1e2a-3b4d-4e5f-8a6b-7c8d9e0f1a2b"]:
        add(f"migrate GUID bill ID {old}", {"fn": "migrateBillId", "id": old}, {"id": I.migrate_bill_id(old)},
            note="GUID-shaped IDs keep their value and are lowercased")

    for ms, rnd in [
        (T0, "00000000000000000000"),
        (T0, "ffffffffffffffffffff"),
        (T0 + 1, "0123456789abcdef0123"),
        (0, "a5a5a5a5a5a5a5a5a5a5"),
    ]:
        add(f"uuid7 from {ms} ms and random {rnd}", {"fn": "uuid7", "unixMs": ms, "randomHex": rnd},
            {"id": I.uuid7(ms, bytes.fromhex(rnd))})

    for s in [
        I.uuid7(T0, bytes.fromhex("0123456789abcdef0123")),
        BILL_1,
        I.regular_payment_id(BILL_1, P1),
        "9f6c1e2a-3b4d-4e5f-8a6b-7c8d9e0f1a2b",
        "9F6C1E2A-3B4D-4E5F-8A6B-7C8D9E0F1A2B",
        "019a0000-0000-7000-c000-000000000000",
        "019a00000000-7000-8000-000000000000",
    ]:
        add(f"inspect {s}", {"fn": "inspectUuid", "id": s}, I.inspect_uuid(s))

    for s, ok in [(DEV_A, True), ("9c2e44f0a1b3d5e7", True), ("0A0A0A0A0A0A0A0A", False),
                  ("0a0a0a0a0a0a0a0", False), ("0a0a0a0a0a0a0a0a0", False), ("0a0a0a0a-0a0a0a0a", False)]:
        add(f"device ID {s!r}", {"fn": "isDeviceId", "id": s}, {"valid": ok})

    write("ids.json", "Deterministic IDs (UUIDv5), UUIDv7 layout, legacy ID migration, device IDs "
          "(sync-protocol.md section 3; data-model.md section 8).", cases)


# ---------------------------------------------------------------------------
# crypto.json
# ---------------------------------------------------------------------------

K1 = hashlib.sha256(b"futurecheck fixture vault key generation 1").digest()
K2 = hashlib.sha256(b"futurecheck fixture vault key generation 2").digest()
PAIR_SECRET = hashlib.sha256(b"futurecheck fixture pairing secret").digest()
PAIR_ID = hashlib.sha256(b"futurecheck fixture pairing id").digest()[:16]


def nonce(label):
    return hashlib.sha256(b"nonce " + label.encode()).digest()[:12]


def change_payload():
    return json.dumps({
        "type": "changes", "formatVersion": 1, "vaultId": VAULT_ID, "deviceId": DEV_A, "seq": 1,
        "records": bill(BILL_1, h(0), "Woodgrove Rewards", 55.0, 1200.0),
    }, separators=(",", ":"))


def gen_crypto():
    cases = []

    def add(name, inp, exp, note=None):
        c = {"name": name, "input": inp, "expected": exp}
        if note:
            c["note"] = note
        cases.append(c)

    for label, k in [("generation 1 fixture key", K1), ("generation 2 fixture key", K2), ("all-zero key", bytes(32))]:
        add(f"derive keys from {label}", {"fn": "deriveKeys", "vaultKeyHex": k.hex()},
            {"fileKeyHex": F.file_key(k).hex(), "keyCheckHex": F.key_check(k).hex()})
    add("derive pairing key", {"fn": "derivePairingKey", "pairingSecretHex": PAIR_SECRET.hex()},
        {"pairingKeyHex": F.pairing_key(PAIR_SECRET).hex()})

    chg_path = f"devices/{DEV_A}/000001.chg"
    snap_hlc = h(600)
    snap_path = f"snapshots/{snap_hlc}.snap"
    pair_path = f"pairing/{PAIR_ID.hex()}.pair"
    snap_payload = json.dumps({
        "type": "snapshot", "formatVersion": 1, "vaultId": VAULT_ID, "deviceId": DEV_A, "hlc": snap_hlc,
        "covers": {DEV_A: 1}, "records": bill(BILL_1, h(0), "Woodgrove Rewards", 55.0, 1200.0),
    }, separators=(",", ":"))
    pair_payload = json.dumps({
        "type": "pairing", "formatVersion": 1, "vaultId": VAULT_ID, "generation": 1,
        "vaultKey": K1.hex(), "deviceId": DEV_A, "expiresAt": iso(600),
    }, separators=(",", ":"))

    seals = [
        ("seal a change file", K1, 1, F.KIND_CHANGES, nonce("chg"), chg_path, change_payload()),
        ("seal a change file with key generation 2", K2, 2, F.KIND_CHANGES, nonce("chg2"),
         f"devices/{DEV_B}/000042.chg", '{"type":"changes"}'),
        ("seal a snapshot", K1, 1, F.KIND_SNAPSHOT, nonce("snap"), snap_path, snap_payload),
        ("seal an empty plaintext", K1, 1, F.KIND_CHANGES, bytes(12), f"devices/{DEV_A}/000002.chg", ""),
        ("seal non-ASCII text (UTF-8)", K1, 1, F.KIND_CHANGES, nonce("utf8"), f"devices/{DEV_A}/000003.chg",
         '{"note":"Café – 5 € ✓"}'),
    ]
    sealed = {}
    for name, k, gen, kind, n, path, pt in seals:
        data = F.seal(k, kind, gen, n, path, pt.encode("utf-8"))
        sealed[name] = (data, path)
        add(name, {"fn": "seal", "vaultKeyHex": k.hex(), "keyGeneration": gen, "kind": kind, "nonceHex": n.hex(),
                   "path": path, "plaintextUtf8": pt},
            {"headerHex": data[:24].hex(), "aadHex": F.aad(data[:24], path).hex(), "fileHex": data.hex()})
    pair_data = F.seal(PAIR_SECRET, F.KIND_PAIRING, 0, nonce("pair"), pair_path, pair_payload.encode())
    add("seal a pairing file", {"fn": "seal", "pairingSecretHex": PAIR_SECRET.hex(), "keyGeneration": 0,
                                "kind": F.KIND_PAIRING, "nonceHex": nonce("pair").hex(), "path": pair_path,
                                "plaintextUtf8": pair_payload},
        {"headerHex": pair_data[:24].hex(), "aadHex": F.aad(pair_data[:24], pair_path).hex(),
         "fileHex": pair_data.hex()})

    keys = {"1": K1.hex(), "2": K2.hex()}
    base, base_path = sealed["seal a change file"]

    def flip(data, i, mask=0x01):
        b = bytearray(data)
        b[i] ^= mask
        return bytes(b)

    def set_byte(data, i, v):
        b = bytearray(data)
        b[i] = v
        return bytes(b)

    def open_case(name, data, path, vk=keys, secret=None, note=None):
        vkeys = {int(g): bytes.fromhex(x) for g, x in vk.items()} if vk else None
        try:
            pt = F.open_file(data, path, vkeys, secret)
            exp = {"plaintextUtf8": pt.decode("utf-8")}
        except F.FrameError as e:
            exp = {"error": e.code}
        inp = {"fn": "open", "fileHex": data.hex(), "path": path, "vaultKeysHex": vk}
        if secret is not None:
            inp["pairingSecretHex"] = secret.hex()
        add(name, inp, exp, note)

    open_case("open a valid change file", base, base_path)
    open_case("open a valid snapshot", *sealed["seal a snapshot"])
    open_case("open a file sealed with generation 2", *sealed["seal a change file with key generation 2"])
    open_case("open a valid pairing file", pair_data, pair_path, vk=None, secret=PAIR_SECRET)
    open_case("tamper: flipped ciphertext byte fails", flip(base, 30), base_path)
    open_case("tamper: flipped tag byte fails", flip(base, len(base) - 1), base_path)
    open_case("tamper: flipped nonce byte fails", flip(base, 12), base_path)
    open_case("tamper: file moved to another device's folder fails", base, f"devices/{DEV_B}/000001.chg",
              note="the path is part of the associated data")
    open_case("tamper: file renamed to another sequence number fails", base, f"devices/{DEV_A}/000007.chg")
    open_case("tamper: key generation byte changed to another known generation fails",
              set_byte(base, 11, 2), base_path, note="header bytes 0-11 are associated data")
    open_case("tamper: kind and name changed together fails", set_byte(base, 5, F.KIND_SNAPSHOT),
              f"snapshots/{snap_hlc}.snap")
    open_case("wrong key fails authentication", base, base_path, vk={"1": K2.hex()})
    open_case("truncated file is rejected before decryption", base[:39], base_path)
    open_case("bad magic is rejected", set_byte(base, 0, ord("X")), base_path)
    open_case("unknown format version is rejected", set_byte(base, 4, 2), base_path)
    open_case("unknown kind is rejected", set_byte(base, 5, 9), base_path)
    open_case("nonzero compression byte is rejected", set_byte(base, 6, 1), base_path)
    open_case("nonzero reserved byte is rejected", set_byte(base, 7, 1), base_path)
    open_case("kind byte that does not match the file name is rejected", base, f"snapshots/{snap_hlc}.snap")
    open_case("unknown key generation is reported", base, base_path, vk={"2": K2.hex()})
    open_case("pairing file with a nonzero key generation is rejected", set_byte(pair_data, 11, 1), pair_path,
              vk=None, secret=PAIR_SECRET)
    open_case("change file with key generation 0 is rejected", set_byte(base, 11, 0), base_path)
    open_case("pairing file opened with the wrong secret fails", pair_data, pair_path, vk=None, secret=K1)

    vault = {
        "type": "futurecheck-vault", "formatVersion": 1, "vaultId": VAULT_ID, "createdAt": iso(0),
        "createdBy": DEV_A, "currentGeneration": 2,
        "generations": [
            {"id": 1, "createdAt": iso(0), "keyCheck": F.key_check(K1).hex()},
            {"id": 2, "createdAt": iso(86400), "keyCheck": F.key_check(K2).hex()},
        ],
    }
    for name, k in [("key check matches generation 1", K1), ("key check matches generation 2", K2),
                    ("key check matches nothing for an unrelated key", bytes(32))]:
        add(name, {"fn": "matchGeneration", "vault": vault, "vaultKeyHex": k.hex()},
            {"generation": FL.match_generation(vault, k)})

    add("pairing QR string", {"fn": "pairingQr", "pairingIdHex": PAIR_ID.hex(), "pairingSecretHex": PAIR_SECRET.hex()},
        {"qr": FL.pairing_qr(PAIR_ID, PAIR_SECRET), "path": pair_path})
    qr = FL.pairing_qr(PAIR_ID, PAIR_SECRET)
    for name, text in [("parse pairing QR", qr), ("parse pairing QR typed in lowercase", qr.lower()),
                       ("parse pairing QR with surrounding whitespace", "  " + qr + "\n")]:
        pid, sec = FL.parse_pairing_qr(text)
        add(name, {"fn": "parsePairingQr", "qr": text}, {"pairingIdHex": pid.hex(), "pairingSecretHex": sec.hex()})
    for name, text in [("pairing QR with unknown version is rejected", qr.replace("FCPAIR:1:", "FCPAIR:2:")),
                       ("pairing QR with a short secret is rejected", qr[:-2]),
                       ("pairing QR with a different prefix is rejected", qr.replace("FCPAIR", "FCPAIRING"))]:
        add(name, {"fn": "parsePairingQr", "qr": text}, expect_error(FL.parse_pairing_qr, text))

    write("crypto.json", "Key derivation, AES-256-GCM file framing, key check values and the pairing QR "
          "(sync-protocol.md sections 6, 7 and 9).", cases)


# ---------------------------------------------------------------------------
# recovery-phrase.json
# ---------------------------------------------------------------------------


def gen_phrase():
    cases = []
    keys = [
        ("all-zero key (BIP39 reference vector)", bytes(32)),
        ("0x7f repeated (BIP39 reference vector)", bytes([0x7F] * 32)),
        ("0x80 repeated (BIP39 reference vector)", bytes([0x80] * 32)),
        ("0xff repeated (BIP39 reference vector)", bytes([0xFF] * 32)),
        ("BIP39 reference vector 68a79eac...", bytes.fromhex("68a79eaca2324873eacc50cb9c6eca8cc68ea5d936f98787c60c7ebc74e6ce7c")),
        ("fixture vault key generation 1", K1),
        ("fixture vault key generation 2", K2),
    ]
    for name, k in keys:
        words = B.encode(k)
        cases.append({"name": f"encode {name}", "input": {"fn": "encode", "keyHex": k.hex()},
                      "expected": {"words": words, "phrase": " ".join(words)}})
        cases.append({"name": f"decode {name}", "input": {"fn": "decode", "phrase": " ".join(words)},
                      "expected": {"keyHex": k.hex()}})
    w = B.encode(K1)
    messy = "  " + "\n".join(" ".join(x.upper() if i % 2 else x for x in w[j:j + 6]) for i, j in enumerate(range(0, 24, 6))) + "\t"
    cases.append({"name": "decode accepts any case and any whitespace between words",
                  "input": {"fn": "decode", "phrase": messy}, "expected": {"keyHex": K1.hex()}})
    for name, sep in [("tab", "\t"), ("CR LF", "\r\n"), ("no-break space U+00A0", "\u00a0"),
                      ("mixed separators", " \t\u00a0\n ")]:
        cases.append({"name": f"decode accepts {name} between words",
                      "input": {"fn": "decode", "phrase": sep.join(w)}, "expected": {"keyHex": K1.hex()}})
    for name, sep in [("em space U+2003", "\u2003"), ("ideographic space U+3000", "\u3000"),
                      ("vertical tab", "\u000b"), ("form feed", "\u000c")]:
        p = " ".join(w[:11]) + sep + " ".join(w[11:])
        cases.append({"name": f"decode does not treat {name} as a separator",
                      "input": {"fn": "decode", "phrase": p}, "expected": expect_error(B.decode, p),
                      "note": "Only space, tab, CR, LF and U+00A0 separate words; anything else is part of a word"})
    p = " ".join(w[:5] + [w[5][:-1] + "\u0130"] + w[6:])
    cases.append({"name": "decode lowercases only A-Z (non-ASCII capitals are not folded)",
                  "input": {"fn": "decode", "phrase": p}, "expected": expect_error(B.decode, p)})
    bad_checksum = w[:23] + [B.WORDS[(B.INDEX[w[23]] + 1) % 2048]]
    bad = [
        ("decode rejects a wrong final word (checksum)", " ".join(bad_checksum)),
        ("decode rejects two swapped words (checksum)", " ".join([w[1], w[0]] + w[2:])),
        ("decode rejects 23 words", " ".join(w[:23])),
        ("decode rejects 25 words", " ".join(w + ["abandon"])),
        ("decode rejects a valid 12-word BIP39 phrase", " ".join(["abandon"] * 11 + ["about"])),
        ("decode rejects a word not in the list", " ".join(w[:5] + ["payday"] + w[6:])),
        ("decode rejects a 4-letter prefix (apps may autocomplete, decode needs full words)",
         " ".join([w[0][:4]] + w[1:]) if len(w[0]) > 4 else " ".join(["aban"] + w[1:])),
        ("decode rejects an empty phrase", ""),
    ]
    for name, p in bad:
        cases.append({"name": name, "input": {"fn": "decode", "phrase": p}, "expected": expect_error(B.decode, p)})
    write("recovery-phrase.json", "Recovery phrase: BIP39 English, 24 words, encoding the 256-bit vault key "
          "(sync-protocol.md section 8). Wordlist: spec/bip39-english.txt.", cases)


# ---------------------------------------------------------------------------
# files.json
# ---------------------------------------------------------------------------


def gen_files():
    cases = []

    def add(name, inp, exp, note=None):
        c = {"name": name, "input": inp, "expected": exp}
        if note:
            c["note"] = note
        cases.append(c)

    for seq in [1, 42, 999999, 1000000]:
        add(f"change file name for seq {seq}", {"fn": "changeFileName", "seq": seq}, {"name": FL.change_file_name(seq)})
    add("change file name rejects seq 0", {"fn": "changeFileName", "seq": 0}, expect_error(FL.change_file_name, 0))

    snap_hlc = h(600)
    paths = [
        f"devices/{DEV_A}/000001.chg", f"devices/{DEV_B}/000042.chg", f"devices/{DEV_A}/1000000.chg",
        f"snapshots/{snap_hlc}.snap", "pairing/" + "ab" * 16 + ".pair",
        f"devices/{DEV_A}/00001.chg", f"devices/{DEV_A}/000000.chg", f"devices/{DEV_A}/0000001.chg",
        f"devices/{DEV_A}/000001.CHG", f"devices/{DEV_A}/000001.chg.tmp", f"devices/{DEV_A.upper()}/000001.chg",
        f"/devices/{DEV_A}/000001.chg", f"devices/{DEV_A}/sub/000001.chg", f"snapshots/{snap_hlc.upper()}.snap",
        "snapshots/latest.snap", "pairing/" + "AB" * 16 + ".pair", "vault.json",
    ]
    for p in paths:
        try:
            exp = FL.parse_path(p)
        except FL.FileError as e:
            exp = {"error": e.code}
        add(f"parse path {p!r}", {"fn": "parsePath", "path": p}, exp)

    good_chg = {"type": "changes", "formatVersion": 1, "vaultId": VAULT_ID, "deviceId": DEV_A, "seq": 3,
                "records": bill(BILL_1, h(0), "Woodgrove Rewards", 55.0, 1200.0)}
    good_snap = {"type": "snapshot", "formatVersion": 1, "vaultId": VAULT_ID, "deviceId": DEV_A, "hlc": snap_hlc,
                 "covers": {DEV_A: 3, DEV_B: 7}, "records": bill(BILL_1, h(0, 0, DEV_B), "Woodgrove Rewards", 55.0, 1200.0)}
    chg_path = f"devices/{DEV_A}/000003.chg"
    snap_path = f"snapshots/{snap_hlc}.snap"

    def env(name, payload, path, note=None):
        try:
            exp = FL.check_envelope(payload, path, VAULT_ID)
        except FL.FileError as e:
            exp = {"error": e.code}
        add(name, {"fn": "checkEnvelope", "payload": payload, "path": path, "vaultId": VAULT_ID}, exp, note)

    env("valid change file envelope", good_chg, chg_path)
    env("valid snapshot envelope (records from any device)", good_snap, snap_path)
    env("change file records stamped by another device are ignored",
        dict(good_chg, records=good_chg["records"] + [r("bill", BILL_1, "name", "Spoofed", h(5, 0, DEV_B))]), chg_path,
        note="a change file only carries its own device's edits")
    env("change file seq must match the file name", dict(good_chg, seq=4), chg_path)
    env("change file device must match the folder", dict(good_chg, deviceId=DEV_B), chg_path)
    env("vault ID must match vault.json", dict(good_chg, vaultId="019a0000-0000-7000-8000-00000000fa02"), chg_path)
    env("payload type must match the file name", dict(good_chg, type="snapshot"), chg_path)
    env("newer payload format is not read", dict(good_chg, formatVersion=2), chg_path)
    env("records must be an array", dict(good_chg, records={}), chg_path)
    env("snapshot hlc must match the file name", dict(good_snap, hlc=h(601)), snap_path)
    env("snapshot device must match the hlc's device", dict(good_snap, deviceId=DEV_B), snap_path)
    env("snapshot covers must map device IDs to sequence numbers", dict(good_snap, covers={DEV_A: "3"}), snap_path)

    def raw_env(name, data, path, as_text=True, note=None):
        """Payload given as bytes: goes through the strict JSON parser first."""
        try:
            exp = FL.check_envelope(FL.parse_json_bytes(data), path, VAULT_ID)
        except FL.FileError as e:
            exp = {"error": e.code}
        inp = {"fn": "checkEnvelope", "path": path, "vaultId": VAULT_ID}
        if as_text:
            inp["payloadText"] = data.decode("utf-8")
        else:
            inp["payloadHex"] = data.hex()
        add(name, inp, exp, note)

    def chg_text(seq_text="3", extra=""):
        return ('{"type":"changes","formatVersion":1,"vaultId":"%s","deviceId":"%s","seq":%s,"records":[]%s}'
                % (VAULT_ID, DEV_A, seq_text, extra)).encode()

    env("integer field written as 3.0 is accepted", dict(good_chg, seq=3.0), chg_path,
        note="Writers write plain digits; readers accept any number with an integral value")
    raw_env("integer field written as 3e0 is accepted", chg_text("3e0"), chg_path)
    raw_env("integer field written as 30e-1 is accepted", chg_text("30e-1"), chg_path)
    env("boolean in an integer field is malformed", dict(good_chg, seq=True), chg_path)
    env("string in an integer field is malformed", dict(good_chg, seq="3"), chg_path)
    env("fraction in an integer field is malformed", dict(good_chg, seq=3.5), chg_path)
    env("formatVersion 1.0 is accepted", dict(good_chg, formatVersion=1.0), chg_path)
    env("formatVersion true is malformed, not version 1", dict(good_chg, formatVersion=True), chg_path)
    env("missing formatVersion is malformed", {k: v for k, v in good_chg.items() if k != "formatVersion"}, chg_path)
    env("snapshot covers value 7.0 is accepted", dict(good_snap, covers={DEV_A: 3, DEV_B: 7.0}), snap_path)
    env("snapshot covers value true is malformed", dict(good_snap, covers={DEV_A: 3, DEV_B: True}), snap_path)
    env("snapshot covers value -1 is malformed", dict(good_snap, covers={DEV_A: -1}), snap_path)
    raw_env("duplicate object keys are malformed", chg_text("3", ',"seq":3'), chg_path,
            note="Many parsers keep the last duplicate silently; a reader must detect duplicates")
    raw_env("duplicate keys inside a record are malformed",
            chg_text("3").replace(b'"records":[]', b'"records":[{"entity":"bill","entity":"payment"}]'), chg_path)
    raw_env("an unpaired surrogate escape is malformed", chg_text("3", ',"note":"\\ud800"'), chg_path)
    raw_env("a paired surrogate escape is fine", chg_text("3", ',"note":"\\ud83d\\ude00"'), chg_path)
    raw_env("a byte-order mark is malformed", b"\xef\xbb\xbf" + chg_text("3"), chg_path, as_text=False)
    raw_env("invalid UTF-8 is malformed", chg_text("3").replace(b'"records":[]', b'"records":[],"note":"\xff"'),
            chg_path, as_text=False)
    raw_env("NaN is not JSON", chg_text("NaN"), chg_path)
    raw_env("trailing text after the object is malformed", chg_text("3") + b" x", chg_path)
    raw_env("whitespace around the object is fine", b" \n" + chg_text("3") + b"\n", chg_path)

    pair_path = "pairing/" + "ab" * 16 + ".pair"
    good_pair = {"type": "pairing", "formatVersion": 1, "vaultId": VAULT_ID, "generation": 1,
                 "vaultKey": K1.hex(), "deviceId": DEV_A, "expiresAt": iso(600)}
    env("valid pairing envelope", good_pair, pair_path)
    env("pairing generation 1.0 is accepted", dict(good_pair, generation=1.0), pair_path)
    env("pairing generation true is malformed", dict(good_pair, generation=True), pair_path)
    env("pairing vault key must be 64 lowercase hex digits", dict(good_pair, vaultKey=K1.hex().upper()), pair_path)
    env("pairing expiresAt must be a timestamp", dict(good_pair, expiresAt="in ten minutes"), pair_path)

    vault = {
        "type": "futurecheck-vault", "formatVersion": 1, "vaultId": VAULT_ID, "createdAt": iso(0),
        "createdBy": DEV_A, "currentGeneration": 1,
        "generations": [{"id": 1, "createdAt": iso(0), "keyCheck": F.key_check(K1).hex()}],
    }

    def vlt(name, v):
        try:
            exp = FL.check_vault(v)
        except FL.FileError as e:
            exp = {"error": e.code}
        add(name, {"fn": "checkVault", "vault": v}, exp)

    def vlt_text(name, text):
        try:
            exp = FL.check_vault(FL.parse_json_bytes(text.encode()))
        except FL.FileError as e:
            exp = {"error": e.code}
        add(name, {"fn": "checkVault", "vaultText": text}, exp)

    gen1 = {"id": 1, "createdAt": iso(0), "keyCheck": F.key_check(K1).hex()}
    vlt("vault.json formatVersion 1.0 is accepted", dict(vault, formatVersion=1.0))
    vlt("vault.json formatVersion true is malformed", dict(vault, formatVersion=True))
    vlt("vault.json currentGeneration 1.0 is accepted", dict(vault, currentGeneration=1.0))
    vlt("vault.json currentGeneration true is malformed", dict(vault, currentGeneration=True))
    vlt("vault.json generation id true is malformed", dict(vault, generations=[dict(gen1, id=True)]))
    vlt("vault.json generation id 1.0 is accepted", dict(vault, generations=[dict(gen1, id=1.0)]))
    vlt_text("vault.json with a duplicate key is malformed",
             json.dumps(vault, separators=(",", ":"))[:-1] + ',"currentGeneration":1}')

    vlt("valid vault.json", vault)
    vlt("vault.json with an unknown extra field is valid", dict(vault, futureField=True))
    vlt("vault.json from a newer format is not read", dict(vault, formatVersion=2))
    vlt("vault.json current generation must be listed", dict(vault, currentGeneration=2))
    vlt("vault.json key check must be 32 lowercase hex digits",
        dict(vault, generations=[{"id": 1, "createdAt": iso(0), "keyCheck": F.key_check(K1).hex().upper()}]))
    vlt("vault.json needs at least one generation", dict(vault, generations=[]))

    write("files.json", "Drive paths, file names, decrypted payload envelopes and vault.json "
          "(sync-protocol.md sections 6 and 7).", cases)


# ---------------------------------------------------------------------------
# merge.json
# ---------------------------------------------------------------------------


def merge_case(name, records, expect, note, edit=None):
    """expect: hand-written partial expectation, checked against the reference merge."""
    emitted = []
    if edit is not None:
        seen = G.Store().apply_all([records[i] for i in edit["seen"]])
        emitted = G.run_op(seen, edit["op"])
    store = G.Store().apply_all(records + emitted)
    d = store.derived()
    full = {"rows": store.rows(), "live": store.live(), "remaining": d["remaining"],
            "setAsideBalance": d["setAsideBalance"]}
    if edit is not None:
        full["emitted"] = emitted

    # Hand-written checks.
    for ent, ids in expect.get("live", {}).items():
        assert full["live"].get(ent, []) == sorted(ids), (name, ent, full["live"].get(ent))
    if "remaining" in expect:
        assert full["remaining"] == expect["remaining"], (name, full["remaining"])
    for b, v in expect.get("setAsideBalance", {}).items():
        assert full["setAsideBalance"][b] == v, (name, b, full["setAsideBalance"][b])
    for (ent, id_), fields in expect.get("fields", {}).items():
        for f, v in fields.items():
            got = full["rows"].get(ent, {}).get(id_, {}).get(f, "<absent>")
            assert got == v and type(got) is type(v), (name, ent, id_, f, got, v)
    if "emitted" in expect:
        assert emitted == expect["emitted"], (name, emitted)
    if "emittedCount" in expect:
        assert len(emitted) == expect["emittedCount"], (name, emitted)

    inp = {"fn": "merge", "records": records}
    if edit is not None:
        inp["edit"] = edit
    return {"name": name, "note": note, "input": inp, "expected": full}


def gen_merge():
    C = []
    W1 = bill(BILL_1, h(0), "Woodgrove Rewards", 55.0, 1200.0)

    C.append(merge_case(
        "concurrent edits to different fields both survive", W1 + [
            r("bill", BILL_1, "name", "Woodgrove Rewards Visa", h(10)),
            r("bill", BILL_1, "payment", 60.0, h(12, 0, DEV_B)),
        ],
        {"live": {"bill": [BILL_1]}, "remaining": {BILL_1: 1200.0},
         "fields": {("bill", BILL_1): {"name": "Woodgrove Rewards Visa", "payment": 60.0}}},
        "A renames the bill while B changes the payment; neither edit is lost."))

    C.append(merge_case(
        "same field: later wall time wins regardless of device", W1 + [
            r("bill", BILL_1, "name", "Edited on A", h(20, 0, DEV_A)),
            r("bill", BILL_1, "name", "Edited on B", h(15, 0, DEV_B)),
        ],
        {"fields": {("bill", BILL_1): {"name": "Edited on A"}}},
        "B's device ID is higher, but A's edit is 5 seconds later."))

    C.append(merge_case(
        "same field, same wall time: higher counter wins", W1 + [
            r("bill", BILL_1, "name", "Counter 1 on A", h(20, 1, DEV_A)),
            r("bill", BILL_1, "name", "Counter 0 on B", h(20, 0, DEV_B)),
        ],
        {"fields": {("bill", BILL_1): {"name": "Counter 1 on A"}}},
        "The counter is compared before the device ID."))

    C.append(merge_case(
        "same field, same wall time and counter: higher device ID wins", W1 + [
            r("bill", BILL_1, "name", "From A", h(20, 0, DEV_A)),
            r("bill", BILL_1, "name", "From B", h(20, 0, DEV_B)),
            r("bill", BILL_1, "name", "From C", h(20, 0, DEV_C)),
        ],
        {"fields": {("bill", BILL_1): {"name": "From C"}}},
        "0c0c... > 0b0b... > 0a0a..."))

    C.append(merge_case(
        "identical HLC with different values: value order breaks the tie", W1 + [
            r("bill", BILL_1, "payment", 60.0, h(30)),
            r("bill", BILL_1, "payment", 70.0, h(30)),
            r("bill", BILL_1, "notes", None, h(30)),
            r("bill", BILL_1, "notes", "Statement on the 4th", h(30)),
            r("bill", BILL_1, "autoPay", True, h(30)),
            r("bill", BILL_1, "autoPay", False, h(30)),
            r("bill", BILL_1, "name", "Alpha", h(30)),
            r("bill", BILL_1, "name", "Beta", h(30)),
            r("bill", BILL_1, "apr", 19.99, h(30)),
            r("bill", BILL_1, "apr", "19.99", h(30)),
        ],
        {"fields": {("bill", BILL_1): {"payment": 70.0, "notes": "Statement on the 4th", "autoPay": True,
                                       "name": "Beta", "apr": "19.99"}}},
        "Only a buggy writer produces this, but merge must still converge: null < false < true < numbers < strings; "
        "numbers by value; strings by UTF-8 bytes."))

    C.append(merge_case(
        "delete wins over an earlier edit", W1 + [
            r("bill", BILL_1, "name", "Renamed on B", h(10, 0, DEV_B)),
            r("bill", BILL_1, "deleted", True, h(20)),
        ],
        {"live": {"bill": []}, "remaining": {},
         "fields": {("bill", BILL_1): {"deleted": True, "name": "Renamed on B"}}},
        "The tombstone is an ordinary field; other fields keep merging but the row is not live."))

    C.append(merge_case(
        "an edit after a delete does not bring the row back", W1 + [
            r("bill", BILL_1, "deleted", True, h(10)),
            r("bill", BILL_1, "name", "Renamed on B", h(20, 0, DEV_B)),
        ],
        {"live": {"bill": []}, "fields": {("bill", BILL_1): {"deleted": True, "name": "Renamed on B"}}},
        "B edited a row it had not yet seen deleted. Only deleted=false undeletes."))

    C.append(merge_case(
        "undelete: a later deleted=false brings the row back", W1 + [
            r("bill", BILL_1, "deleted", True, h(10)),
            r("bill", BILL_1, "deleted", False, h(20, 0, DEV_B)),
        ],
        {"live": {"bill": [BILL_1]}, "remaining": {BILL_1: 1200.0}},
        "B restored the bill after A deleted it."))

    C.append(merge_case(
        "delete after undelete: the later write wins", W1 + [
            r("bill", BILL_1, "deleted", True, h(10)),
            r("bill", BILL_1, "deleted", False, h(20, 0, DEV_B)),
            r("bill", BILL_1, "deleted", True, h(30, 0, DEV_C)),
        ],
        {"live": {"bill": []}}, "Three devices toggle the tombstone; the last write wins."))

    C.append(merge_case(
        "duplicate mark-paid on two devices yields one payment", W1
        + mark(BILL_1, P2, 55.0, h(100, 0, DEV_A)) + mark(BILL_1, P2, 55.0, h(105, 0, DEV_B)),
        {"live": {"bill": [BILL_1], "payment": [rid(BILL_1, P2)]}, "remaining": {BILL_1: 1145.0}},
        "Both devices derive the same payment ID from (bill, period), so the payment is counted once."))

    C.append(merge_case(
        "duplicate mark-paid with different amounts: one payment, later amount wins", W1
        + mark(BILL_1, P2, 55.0, h(100, 0, DEV_A)) + mark(BILL_1, P2, 60.0, h(90, 0, DEV_B)),
        {"live": {"payment": [rid(BILL_1, P2)]}, "remaining": {BILL_1: 1145.0},
         "fields": {("payment", rid(BILL_1, P2)): {"amountPaid": 55.0}}},
        "B typed 60 offline, A typed 55 ten seconds later."))

    C.append(merge_case(
        "unmark then remark: the payment is live and counted once", W1
        + mark(BILL_1, P2, 55.0, h(100))
        + [r("payment", rid(BILL_1, P2), "deleted", True, h(110))]
        + mark(BILL_1, P2, 55.0, h(120, 0, DEV_B)),
        {"live": {"payment": [rid(BILL_1, P2)]}, "remaining": {BILL_1: 1145.0}},
        "A marks and unmarks; B, after syncing, marks it again."))

    C.append(merge_case(
        "unmark concurrent with an amount edit: stays unmarked", W1
        + mark(BILL_1, P2, 55.0, h(100))
        + [r("payment", rid(BILL_1, P2), "deleted", True, h(120)),
           r("payment", rid(BILL_1, P2), "amountPaid", 70.0, h(125, 0, DEV_B))],
        {"live": {"payment": []}, "remaining": {BILL_1: 1200.0},
         "fields": {("payment", rid(BILL_1, P2)): {"amountPaid": 70.0, "deleted": True}}},
        "Editing the amount does not re-mark the payment."))

    C.append(merge_case(
        "rows whose create has not arrived are pending, not live", [
            r("bill", BILL_2, "name", "Contoso Card", h(50, 0, DEV_B)),
            r("bill", BILL_2, "payment", 40.0, h(50, 0, DEV_B)),
        ],
        {"live": {"bill": []}, "remaining": {},
         "fields": {("bill", BILL_2): {"name": "Contoso Card", "payment": 40.0}}},
        "A row is live only when its deleted field is present and false. Edits are kept until the create arrives."))

    C.append(merge_case(
        "out-of-order delivery of a create and three later edits converges", W1 + [
            r("bill", BILL_1, "payment", 56.0, h(10, 0, DEV_B)),
            r("bill", BILL_1, "payment", 57.0, h(20, 0, DEV_C)),
            r("bill", BILL_1, "payment", 58.0, h(30, 0, DEV_A)),
        ],
        {"fields": {("bill", BILL_1): {"payment": 58.0}}},
        "Every delivery order gives the same result; nothing is applied 'in arrival order'."))

    C.append(merge_case(
        "duplicate delivery is harmless", W1 + W1 + mark(BILL_1, P2, 55.0, h(100)) + mark(BILL_1, P2, 55.0, h(100)),
        {"remaining": {BILL_1: 1145.0}},
        "Re-reading a file applies the same records again; the state does not change."))

    pay_b = mark(BILL_1, P2, 100.0, h(100, 0, DEV_B))
    C.append(merge_case(
        "a checkpoint set after an offline payment absorbs it", bill(BILL_1, h(0), "Woodgrove Rewards", 55.0, 1000.0)
        + pay_b + [r("bill", BILL_1, "remainingCheckpoint", 950.0, h(200, 0, DEV_A))],
        {"remaining": {BILL_1: 950.0}},
        "B paid 100 offline (HLC +100s). A, not having seen it, typed a statement balance of 950 (HLC +200s). "
        "The checkpoint is later, so the payment is treated as already reflected in 950."))

    C.append(merge_case(
        "a payment after the checkpoint is subtracted", bill(BILL_1, h(0), "Woodgrove Rewards", 55.0, 1000.0)
        + [r("bill", BILL_1, "remainingCheckpoint", 950.0, h(100, 0, DEV_A))]
        + mark(BILL_1, P2, 100.0, h(200, 0, DEV_B)),
        {"remaining": {BILL_1: 850.0}},
        "A sets 950; B pays 100 later (by HLC)."))

    C.append(merge_case(
        "HLC, not paidAt, decides whether a payment is after the checkpoint",
        bill(BILL_1, h(0), "Woodgrove Rewards", 55.0, 1000.0)
        + [r("bill", BILL_1, "remainingCheckpoint", 950.0, h(100, 0, DEV_A))]
        + mark(BILL_1, P2, 100.0, h(200, 0, DEV_B), paid_at="2026-11-14T11:00:00.000Z"),
        {"remaining": {BILL_1: 850.0}},
        "B's wall clock said 11:00 (before the checkpoint), but its HLC is later, so the payment counts."))

    # ---- checkpoint vs unmark/remark (writer rules, sync-protocol.md 5.3) ----
    base1000 = bill(BILL_1, h(0), "Woodgrove Rewards", 55.0, 1000.0)
    paid_then_statement = base1000 + play(base1000, [op_mark(BILL_1, P2, 100.0, h(100)),
                                                     op_remaining(BILL_1, 880.0, h(200))])
    C.append(edit_case(
        "unmark after a statement balance raises Remaining back", paid_then_statement,
        op_unmark(BILL_1, P2, h(300)),
        {"emitted": [r("bill", BILL_1, "remainingCheckpoint", 980.0, h(300)),
                     r("payment", rid(BILL_1, P2), "deleted", True, h(300))],
         "remaining": {BILL_1: 980.0}},
        "Remaining 1000, mark paid 100 (900), statement balance 880 typed (checkpoint absorbs the payment), "
        "unmark. The payment is at or before the checkpoint, so unmark also raises the checkpoint by its "
        "amount: 880 + 100 = 980."))

    unmarked = paid_then_statement + play(paid_then_statement, [op_unmark(BILL_1, P2, h(300))])
    C.append(edit_case(
        "remark after that unmark subtracts once", unmarked, op_mark(BILL_1, P2, 100.0, h(400)),
        {"emittedCount": 6, "remaining": {BILL_1: 880.0}},
        "The re-mark is after the new checkpoint (+300s), so it subtracts: 980 - 100 = 880. Before this rule "
        "the result was 780 (subtracted twice)."))

    C.append(edit_case(
        "markPaid on a payment already live writes only amountPaid and paidAt", paid_then_statement,
        op_mark(BILL_1, P2, 120.0, h(300)),
        {"emitted": [r("payment", rid(BILL_1, P2), "amountPaid", 120.0, h(300)),
                     r("payment", rid(BILL_1, P2), "paidAt", iso(300), h(300))],
         "remaining": {BILL_1: 880.0}},
        "Writing deleted=false again would move the mark HLC past the checkpoint and subtract a payment the "
        "checkpoint already includes. The payment stays absorbed, so the amount change does not move Remaining."))

    paid_only = base1000 + play(base1000, [op_mark(BILL_1, P2, 100.0, h(100))])
    C.append(edit_case(
        "markPaid on a live payment after the checkpoint changes the amount it subtracts", paid_only,
        op_mark(BILL_1, P2, 120.0, h(200)),
        {"emittedCount": 2, "remaining": {BILL_1: 880.0}},
        "The payment is after the checkpoint, so its new amount counts: 1000 - 120."))

    migrated = bill(BILL_1, h(0), "Woodgrove Rewards", 55.0, 1145.0) + mark(BILL_1, P1, 55.0, h(0))
    C.append(edit_case(
        "unmarking a migrated payment raises Remaining back", migrated, op_unmark(BILL_1, P1, h(100)),
        {"emitted": [r("bill", BILL_1, "remainingCheckpoint", 1200.0, h(100)),
                     r("payment", rid(BILL_1, P1), "deleted", True, h(100))],
         "remaining": {BILL_1: 1200.0}},
        "Migration stamps the checkpoint and the payment with one HLC, so the payment is absorbed (equal HLC). "
        "Unmark gives it back: 1145 + 55."))

    clamped_base = bill(BILL_1, h(0), "Woodgrove Rewards", 55.0, 100.0)
    clamped = clamped_base + play(clamped_base, [op_mark(BILL_1, P1, 30.0, h(10)),
                                                 op_remaining(BILL_1, 50.0, h(20)),
                                                 op_mark(BILL_1, P2, 60.0, h(30))])
    C.append(edit_case(
        "unmark while Remaining is clamped at zero uses the unclamped value", clamped,
        op_unmark(BILL_1, P1, h(40)),
        {"emitted": [r("bill", BILL_1, "remainingCheckpoint", 20.0, h(40)),
                     r("payment", rid(BILL_1, P1), "deleted", True, h(40))],
         "remaining": {BILL_1: 20.0}},
        "Checkpoint 50 absorbs the 30 payment; a later 60 payment shows Remaining 0 (unclamped -10). Unmarking "
        "the 30 gives -10 + 30 = 20, not 0 + 30."))

    with_extra = base1000 + extra(EXTRA_1, BILL_1, P2, 100.0, h(100)) 
    with_extra = with_extra + play(with_extra, [op_remaining(BILL_1, 850.0, h(200))])
    C.append(edit_case(
        "deleting an extra payment absorbed by a checkpoint raises Remaining back", with_extra,
        {"op": "deletePayment", "paymentId": EXTRA_1, "hlc": h(300)},
        {"emitted": [r("bill", BILL_1, "remainingCheckpoint", 950.0, h(300)),
                     r("payment", EXTRA_1, "deleted", True, h(300))],
         "remaining": {BILL_1: 950.0}},
        "Same rule for Extra payments."))

    ins_base = bill(BILL_3, h(0), "Home Insurance", 480.0, 600.0, type_="Bills")
    set_aside_then_cp = ins_base + play(ins_base, [op_mark(BILL_3, P1, 40.0, h(10), kind="SetAside"),
                                                   op_remaining(BILL_3, 500.0, h(20))])
    C.append(edit_case(
        "unmarking a set-aside never writes a checkpoint", set_aside_then_cp,
        op_unmark(BILL_3, P1, h(30), kind="SetAside"),
        {"emitted": [r("payment", sid(BILL_3, P1), "deleted", True, h(30))], "remaining": {BILL_3: 500.0}},
        "Set-asides never count against Remaining, so there is nothing to give back."))

    a_side = base1000 + play(base1000, [op_mark(BILL_1, P2, 100.0, h(100)), op_remaining(BILL_1, 880.0, h(200))])
    b_side = play(base1000, [{**op_mark(BILL_1, P2, 100.0, h(300, 0, DEV_B))}])
    C.append(merge_case(
        "known limitation: a device that missed the mark re-marks after a checkpoint", a_side + b_side,
        {"remaining": {BILL_1: 780.0}},
        "A marks 100 and types statement 880. B, offline and not having seen A's mark, marks the same bill and "
        "period paid (+300s). B's deleted=false is the latest mark and is after the checkpoint, so the payment "
        "counts on top of the checkpoint that already includes it: 780, not 880. No per-field rule can tell this "
        "from a real re-mark; the user corrects it by typing Remaining (sync-protocol.md 5.4)."))

    b_unmark = play(base1000 + mark(BILL_1, P2, 100.0, h(100)), [op_unmark(BILL_1, P2, h(300, 0, DEV_B))])
    C.append(merge_case(
        "known limitation: a device that missed the checkpoint unmarks an absorbed payment",
        a_side + b_unmark,
        {"remaining": {BILL_1: 880.0}, "live": {"payment": []}},
        "B saw A's mark but not A's statement 880, so to B the payment is after its checkpoint and unmark writes "
        "only deleted=true. Merged: the payment is gone but the checkpoint still includes it, so Remaining stays "
        "880 instead of 980 (sync-protocol.md 5.4)."))

    C.append(merge_case(
        "an amount edit after the checkpoint does not move a pre-checkpoint payment",
        bill(BILL_1, h(0), "Woodgrove Rewards", 55.0, 1000.0)
        + mark(BILL_1, P2, 100.0, h(50))
        + [r("bill", BILL_1, "remainingCheckpoint", 900.0, h(100)),
           r("payment", rid(BILL_1, P2), "amountPaid", 120.0, h(150))],
        {"remaining": {BILL_1: 900.0},
         "fields": {("payment", rid(BILL_1, P2)): {"amountPaid": 120.0}}},
        "Only the mark HLC is compared with the checkpoint."))

    C.append(merge_case(
        "Remaining is clamped at zero", bill(BILL_1, h(0), "Woodgrove Rewards", 55.0, 50.0)
        + mark(BILL_1, P2, 80.0, h(10)),
        {"remaining": {BILL_1: 0.0}}, "50 - 80 is shown as 0."))

    C.append(merge_case(
        "migration stamp: payments at the checkpoint's own HLC are not subtracted",
        bill(BILL_1, h(0), "Woodgrove Rewards", 55.0, 1145.0) + mark(BILL_1, P1, 55.0, h(0)),
        {"remaining": {BILL_1: 1145.0}},
        "The migration writes every row at one HLC. 'After the checkpoint' is strict, so existing payments "
        "already reflected in today's Remaining are not subtracted a second time."))

    C.append(merge_case(
        "money is aggregated in integer cents", bill(BILL_1, h(0), "Woodgrove Rewards", 55.0, 100.0)
        + mark(BILL_1, P1, 0.1, h(10)) + mark(BILL_1, P2, 0.2, h(11))
        + [r("payment", EXTRA_1, f, v, h(12)) for f, v in
           [("amountPaid", 33.335), ("billId", BILL_1), ("deleted", False), ("kind", "Extra"),
            ("paidAt", iso(12)), ("periodKey", P2)]],
        {"remaining": {BILL_1: 66.36}},
        "Each amount is rounded to cents before summing: 10000 - 10 - 20 - 3334 = 6636 cents. "
        "33.335 rounds to 33.34 under data-model.md section 3 (33.335 * 100 = 3333.5 in binary floating point)."))

    C.append(merge_case(
        "extra payments with random IDs add up", W1
        + mark(BILL_1, P2, 55.0, h(100))
        + extra(EXTRA_1, BILL_1, P2, 100.0, h(110, 0, DEV_A))
        + extra(EXTRA_2, BILL_1, P2, 100.0, h(110, 0, DEV_B)),
        {"live": {"payment": sorted([rid(BILL_1, P2), EXTRA_1, EXTRA_2])}, "remaining": {BILL_1: 945.0}},
        "Two devices each record an extra 100; both are separate payments."))

    C.append(merge_case(
        "an unmarked extra payment does not count", W1
        + extra(EXTRA_1, BILL_1, P2, 100.0, h(110))
        + [r("payment", EXTRA_1, "deleted", True, h(120, 0, DEV_B))],
        {"live": {"payment": []}, "remaining": {BILL_1: 1200.0}}, "Deleting an extra payment works like unmark."))

    INS = bill(BILL_3, h(0), "Home Insurance", 480.0, 600.0, type_="Bills")
    C.append(merge_case(
        "set-asides never lower Remaining; balance sums set-asides with no regular payment yet", INS
        + mark(BILL_3, P1, 40.0, h(10), kind="SetAside") + mark(BILL_3, P2, 40.0, h(20), kind="SetAside"),
        {"live": {"payment": sorted([sid(BILL_3, P1), sid(BILL_3, P2)])}, "remaining": {BILL_3: 600.0},
         "setAsideBalance": {BILL_3: 80.0}},
        "Set-aside payments use their own deterministic ID and kind SetAside."))

    C.append(merge_case(
        "set-aside balance restarts after a regular payment", INS
        + mark(BILL_3, P1, 40.0, h(10), kind="SetAside") + mark(BILL_3, P2, 40.0, h(20), kind="SetAside")
        + mark(BILL_3, P2, 480.0, h(30)) + mark(BILL_3, P3, 40.0, h(40), kind="SetAside"),
        {"remaining": {BILL_3: 120.0}, "setAsideBalance": {BILL_3: 40.0}},
        "Only set-asides in periods after the latest live Regular payment's period count (P3 only). "
        "The regular payment lowers Remaining; set-asides never do."))

    C.append(merge_case(
        "duplicate set-aside on two devices yields one row", INS
        + mark(BILL_3, P1, 40.0, h(10, 0, DEV_A), kind="SetAside")
        + mark(BILL_3, P1, 40.0, h(12, 0, DEV_B), kind="SetAside"),
        {"live": {"payment": [sid(BILL_3, P1)]}, "setAsideBalance": {BILL_3: 40.0}},
        "Like regular payments, set-asides are deterministic per (bill, period)."))

    C.append(merge_case(
        "a deleted bill has no Remaining; its payments stay as rows", W1
        + mark(BILL_1, P2, 55.0, h(100)) + [r("bill", BILL_1, "deleted", True, h(200, 0, DEV_B))],
        {"live": {"bill": [], "payment": [rid(BILL_1, P2)]}, "remaining": {}},
        "Payments are not cascaded; apps hide payments of deleted bills."))

    restore = h(1000)
    known_to_a = (
        W1
        + bill(BILL_2, h(10, 0, DEV_B), "Contoso Card", 40.0, 900.0)
        + [r("bill", BILL_1, "name", "Woodgrove (renamed)", h(20, 0, DEV_B))]
        + mark(BILL_1, P1, 55.0, h(30))
        + [r("setting", "PayAnchor", "value", "2025-03-27", h(31)),
           r("setting", "SafeToSpendBuffer", "value", 100.0, h(32)),
           r("setting", "TypeColor/Cards", "value", "#123456", h(33)),
           r("setting", "NotionOwnerDeviceId", "value", DEV_A, h(34)),
           r("setting", "FutureSetting", "value", "kept", h(35, 0, DEV_B)),
           r("device", DEV_A, "deleted", False, h(1)), r("device", DEV_A, "name", "Windows PC", h(1)),
           r("device", DEV_B, "deleted", False, h(2, 0, DEV_B)), r("device", DEV_B, "name", "Phone", h(2, 0, DEV_B)),
           r("budgetGoal", "019a0000-0000-7000-8000-0000000000d1", "deleted", False, h(36, 0, DEV_B))]
    )
    later = mark(BILL_1, P2, 60.0, h(900, 0, DEV_B)) + mark(BILL_1, P3, 60.0, h(1100, 0, DEV_B))
    restore_op = {
        "op": "restore", "hlc": restore,
        "rows": {
            "bill": {BILL_1: {"name": "Woodgrove Rewards", "type": "Cards", "payment": 55.0,
                              "remainingCheckpoint": 1145.0}},
            "payment": {rid(BILL_1, P1): {"billId": BILL_1, "kind": "Regular", "periodKey": P1,
                                          "amountPaid": 55.0, "paidAt": iso(30)}},
        },
        "settings": {"PayAnchor": "2025-03-20"},
    }
    C.append(merge_case(
        "restore re-stamps the backup and tombstones rows missing from it",
        known_to_a + later,
        {"live": {"bill": [BILL_1], "payment": sorted([rid(BILL_1, P1), rid(BILL_1, P2), rid(BILL_1, P3)]),
                  "device": sorted([DEV_A, DEV_B]), "budgetGoal": ["019a0000-0000-7000-8000-0000000000d1"]},
         "remaining": {BILL_1: 1085.0},
         "fields": {("bill", BILL_1): {"name": "Woodgrove Rewards"},
                    ("setting", "PayAnchor"): {"value": "2025-03-20"},
                    ("setting", "SafeToSpendBuffer"): {"value": None},
                    ("setting", "TypeColor/Cards"): {"value": None},
                    ("setting", "NotionOwnerDeviceId"): {"value": DEV_A},
                    ("setting", "FutureSetting"): {"value": "kept"}}},
        "A restores at +1000s a backup with BILL_1 (Remaining 1145), its P1 payment and PayAnchor. BILL_2 is "
        "tombstoned. Synced settings missing from the backup are set to null (defaults); Notion connector "
        "settings, unknown setting keys, device rows and unknown entities are never touched. B's offline P2 "
        "payment (+900s, unknown to A) survives as a row but is before the restore checkpoint, so it does not "
        "lower Remaining; B's P3 payment after the restore does: 1145 - 60 = 1085.",
        edit={"seen": list(range(len(known_to_a))), "op": restore_op}))

    C.append(merge_case(
        "settings: last write wins per key, keys are independent", [
            r("setting", "PayAnchor", "value", "2025-03-20", h(0)),
            r("setting", "PayAnchor", "value", "2025-03-27", h(10, 0, DEV_B)),
            r("setting", "TypeColor/Cards", "value", "#FD79A8", h(5)),
            r("setting", "TypeColor/Loans", "value", "#6C5CE7", h(6, 0, DEV_B)),
            r("setting", "TypeColor/Loans", "value", None, h(7)),
            r("setting", "SafeToSpendBuffer", "value", 100.0, h(1)),
            r("setting", "ReminderDaysBefore", "value", 3, h(2, 0, DEV_B)),
        ],
        {"live": {"setting": ["PayAnchor", "ReminderDaysBefore", "SafeToSpendBuffer", "TypeColor/Cards",
                              "TypeColor/Loans"]},
         "fields": {("setting", "PayAnchor"): {"value": "2025-03-27"},
                    ("setting", "TypeColor/Loans"): {"value": None}}},
        "Settings have no deleted field; a null value means 'use the default' (here: reset the Loans colour)."))

    pc = I.paycheck_id(INCOME_1, "2026-11-20")
    C.append(merge_case(
        "the same paycheck recorded on two devices merges into one row", [
            r("incomeSource", INCOME_1, "cadence", "Bi-Weekly", h(0)),
            r("incomeSource", INCOME_1, "deleted", False, h(0)),
            r("incomeSource", INCOME_1, "expectedAmount", 1500.0, h(0)),
            r("incomeSource", INCOME_1, "name", "Main job", h(0)),
            r("paycheck", pc, "amount", 1480.25, h(100)),
            r("paycheck", pc, "deleted", False, h(100)),
            r("paycheck", pc, "incomeSourceId", INCOME_1, h(100)),
            r("paycheck", pc, "payDate", "2026-11-20", h(100)),
            r("paycheck", pc, "amount", 1490.0, h(110, 0, DEV_B)),
            r("paycheck", pc, "deleted", False, h(110, 0, DEV_B)),
            r("paycheck", pc, "incomeSourceId", INCOME_1, h(110, 0, DEV_B)),
            r("paycheck", pc, "payDate", "2026-11-20", h(110, 0, DEV_B)),
        ],
        {"live": {"incomeSource": [INCOME_1], "paycheck": [pc]},
         "fields": {("paycheck", pc): {"amount": 1490.0}}},
        "Paycheck IDs are derived from (income source, pay date)."))

    C.append(merge_case(
        "unknown entities and fields from a newer app version are kept", W1 + [
            r("bill", BILL_1, "colorTag", "teal", h(10, 0, DEV_B)),
            r("budgetGoal", "019a0000-0000-7000-8000-0000000000d1", "deleted", False, h(10, 0, DEV_B)),
            r("budgetGoal", "019a0000-0000-7000-8000-0000000000d1", "name", "Emergency fund", h(10, 0, DEV_B)),
        ],
        {"live": {"bill": [BILL_1], "budgetGoal": ["019a0000-0000-7000-8000-0000000000d1"]},
         "fields": {("bill", BILL_1): {"colorTag": "teal"}}},
        "Older apps store and forward records they do not understand, and include them in snapshots."))

    C.append(merge_case(
        "invalid records are ignored", W1 + [
            r("bill", BILL_1, "name", "Uppercase HLC", h(50).upper()),
            r("bill", BILL_1, "name", {"text": "object value"}, h(51)),
            r("bill", BILL_1, "name", ["array value"], h(52)),
            {"entity": "bill", "id": BILL_1, "value": "missing field", "hlc": h(53)},
            {"entity": "bill", "id": BILL_1, "field": "name", "hlc": h(54)},
            r("bill", "", "name", "empty id", h(55)),
            r(5, BILL_1, "name", "numeric entity", h(56)),
            dict(r("bill", BILL_1, "notes", "Extra keys are allowed", h(57)), source="notion"),
        ],
        {"fields": {("bill", BILL_1): {"name": "Woodgrove Rewards", "notes": "Extra keys are allowed"}},
         "live": {"bill": [BILL_1]}},
        "A record needs string entity, id, field and hlc (valid HLC), and a value that is null, boolean, number or "
        "string. Unknown extra keys are ignored. A missing value key is invalid; null must be explicit."))

    odd = ["019a0000-0000-7000-8000-0000000000f1", "019a0000-0000-7000-8000-0000000000f2",
           "019a0000-0000-7000-8000-0000000000f3"]

    def pay(pid, bid, period, amount, hlc, kind):
        recs = [r("payment", pid, "amountPaid", amount, hlc), r("payment", pid, "billId", bid, hlc),
                r("payment", pid, "deleted", False, hlc), r("payment", pid, "periodKey", period, hlc)]
        if kind != "<absent>":
            recs.append(r("payment", pid, "kind", kind, hlc))
        return recs

    C.append(merge_case(
        "payment kind: missing or wrong type reads as Regular, an unknown string does not count", W1
        + pay(odd[0], BILL_1, P2, 10.0, h(10), None)
        + pay(odd[1], BILL_1, P2, 20.0, h(11), 5)
        + pay(odd[2], BILL_1, P2, 40.0, h(12), "Refund")
        + pay(EXTRA_1, BILL_1, P2, 80.0, h(13), "<absent>"),
        {"remaining": {BILL_1: 1090.0}},
        "kind null, kind 5 and a missing kind are the default Regular (data-model.md 1.2) and count: "
        "1200 - 10 - 20 - 80 = 1090. kind \"Refund\" is an unknown value and does not count."))

    C.append(merge_case(
        "set-aside balance ignores payments whose periodKey is not a valid date", INS
        + mark(BILL_3, P1, 40.0, h(10), kind="SetAside") + mark(BILL_3, P2, 40.0, h(20), kind="SetAside")
        + pay(odd[0], BILL_3, "2026-13-01", 100.0, h(30), "Regular")
        + pay(odd[1], BILL_3, 20261112, 50.0, h(31), "Regular")
        + pay(odd[2], BILL_3, "someday", 40.0, h(32), "SetAside"),
        {"remaining": {BILL_3: 450.0}, "setAsideBalance": {BILL_3: 80.0}},
        "Regular payments with an invalid periodKey do not reset the balance and set-asides with one are not "
        "counted. They still lower Remaining (Regular): 600 - 100 - 50 = 450."))

    irr = [
        r("incomeSource", INCOME_1, "cadence", "Irregular", h(0)),
        r("incomeSource", INCOME_1, "deleted", False, h(0)),
        r("incomeSource", INCOME_1, "name", "Side job", h(0)),
    ]
    pc1 = I.uuid7(T0 + 100000, bytes.fromhex("11111111111111111111"))
    pc2 = I.uuid7(T0 + 105000, bytes.fromhex("22222222222222222222"))
    for pc, hh, amt in [(pc1, h(100), 120.0), (pc2, h(105, 0, DEV_B), 80.0)]:
        irr += [r("paycheck", pc, "amount", amt, hh), r("paycheck", pc, "deleted", False, hh),
                r("paycheck", pc, "incomeSourceId", INCOME_1, hh), r("paycheck", pc, "payDate", "2026-11-20", hh)]
    C.append(merge_case(
        "two payouts on one day from an Irregular source stay two rows", irr,
        {"live": {"incomeSource": [INCOME_1], "paycheck": sorted([pc1, pc2])}},
        "Irregular actuals use random UUIDv7 IDs (like Extra payments); only scheduled cadences derive the ID "
        "from (source, pay date)."))

    # ---- writer-side rules ---------------------------------------------
    base = bill(BILL_1, h(0), "Woodgrove Rewards", 55.0, 1000.0)
    b_pay = mark(BILL_1, P2, 100.0, h(300, 0, DEV_B))
    C.append(merge_case(
        "echo: Notion reporting the value the connector last pushed creates no checkpoint",
        base + b_pay,
        {"emitted": [], "remaining": {BILL_1: 900.0}},
        "A pushed Remaining=1000 to Notion, then read it back. B's offline payment (HLC +300s) is not yet on A. "
        "Treating the echo as an edit would set a checkpoint at +400s and silently drop B's payment.",
        edit={"seen": list(range(len(base))),
              "op": {"op": "setRemaining", "billId": BILL_1, "value": 1000.0, "source": "notion",
                     "lastPushed": 1000.0, "hlc": h(400)}}))

    C.append(merge_case(
        "Notion edit with a new value creates a checkpoint", base + b_pay,
        {"emittedCount": 1, "remaining": {BILL_1: 975.0}},
        "The user typed a statement balance of 975 in Notion. It differs from the last pushed value, so it is a real "
        "edit: checkpoint 975 at A's next HLC. B's earlier payment (+300s) is treated as included.",
        edit={"seen": list(range(len(base))),
              "op": {"op": "setRemaining", "billId": BILL_1, "value": 975.0, "source": "notion",
                     "lastPushed": 1000.0, "hlc": h(400)}}))

    a_pay = mark(BILL_1, P1, 100.0, h(100))
    C.append(merge_case(
        "app edit to the current computed value creates no checkpoint", base + a_pay + b_pay,
        {"emitted": [], "remaining": {BILL_1: 800.0}},
        "On A, Remaining shows 900. Saving the editor with 900 unchanged must not write a checkpoint, or B's "
        "offline payment would be dropped.",
        edit={"seen": list(range(len(base) + len(a_pay))),
              "op": {"op": "setRemaining", "billId": BILL_1, "value": 900.0, "source": "app", "hlc": h(400)}}))

    later_b = mark(BILL_1, P2, 50.0, h(500, 0, DEV_B))
    C.append(merge_case(
        "app edit to a new value creates a checkpoint; later payments still subtract", base + a_pay + later_b,
        {"emitted": [r("bill", BILL_1, "remainingCheckpoint", 880.0, h(400))], "remaining": {BILL_1: 830.0}},
        "A sets 880 at +400s. B's payment at +500s is after it: 880 - 50 = 830.",
        edit={"seen": list(range(len(base) + len(a_pay))),
              "op": {"op": "setRemaining", "billId": BILL_1, "value": 880.0, "source": "app", "hlc": h(400)}}))

    C.append(merge_case(
        "Notion value equal to the current computed value creates no checkpoint", base + a_pay,
        {"emitted": [], "remaining": {BILL_1: 900.0}},
        "Not an echo (last pushed was 1000), but unchanged from what A computes, so nothing is written.",
        edit={"seen": list(range(len(base) + len(a_pay))),
              "op": {"op": "setRemaining", "billId": BILL_1, "value": 900.0, "source": "notion",
                     "lastPushed": 1000.0, "hlc": h(400)}}))

    C.append(merge_case(
        "markPaid writes the deterministic payment with every mark field", base,
        {"emittedCount": 6, "remaining": {BILL_1: 945.0}, "live": {"payment": [rid(BILL_1, P2)]}},
        "Fields written: amountPaid, billId, deleted=false, kind, paidAt, periodKey, all at the op's HLC.",
        edit={"seen": list(range(len(base))),
              "op": {"op": "markPaid", "billId": BILL_1, "periodKey": P2, "kind": "Regular", "amount": 55.0,
                     "paidAt": iso(100), "hlc": h(100)}}))

    marked_then_unmarked = base + mark(BILL_1, P2, 55.0, h(50)) + [r("payment", rid(BILL_1, P2), "deleted", True, h(60))]
    C.append(merge_case(
        "markPaid after unmark reuses the deterministic ID", marked_then_unmarked,
        {"emittedCount": 6, "remaining": {BILL_1: 940.0}, "live": {"payment": [rid(BILL_1, P2)]},
         "fields": {("payment", rid(BILL_1, P2)): {"amountPaid": 60.0, "deleted": False}}},
        "Re-marking writes deleted=false at a new HLC, which also becomes the payment's mark HLC.",
        edit={"seen": list(range(len(marked_then_unmarked))),
              "op": {"op": "markPaid", "billId": BILL_1, "periodKey": P2, "kind": "Regular", "amount": 60.0,
                     "paidAt": iso(100), "hlc": h(100)}}))

    marked = base + mark(BILL_1, P2, 55.0, h(50))
    C.append(merge_case(
        "unmarkPaid tombstones the deterministic payment", marked,
        {"emitted": [r("payment", rid(BILL_1, P2), "deleted", True, h(100))], "remaining": {BILL_1: 1000.0}},
        "Unmark writes only deleted=true.",
        edit={"seen": list(range(len(marked))),
              "op": {"op": "unmarkPaid", "billId": BILL_1, "periodKey": P2, "kind": "Regular", "hlc": h(100)}}))

    C.append(merge_case(
        "unmarkPaid on a payment that is not live writes nothing", base,
        {"emitted": [], "remaining": {BILL_1: 1000.0}}, "Nothing to unmark.",
        edit={"seen": list(range(len(base))),
              "op": {"op": "unmarkPaid", "billId": BILL_1, "periodKey": P2, "kind": "Regular", "hlc": h(100)}}))

    C.append(merge_case(
        "markPaid for a set-aside uses the set-aside ID and does not lower Remaining", INS,
        {"emittedCount": 6, "remaining": {BILL_3: 600.0}, "setAsideBalance": {BILL_3: 40.0},
         "live": {"payment": [sid(BILL_3, P2)]}},
        "Kind SetAside.",
        edit={"seen": list(range(len(INS))),
              "op": {"op": "markPaid", "billId": BILL_3, "periodKey": P2, "kind": "SetAside", "amount": 40.0,
                     "paidAt": iso(100), "hlc": h(100)}}))

    write("merge.json", "Merge scenarios (sync-protocol.md sections 4 and 5). Each case must give the expected state "
          "for every delivery order of input.records (plus the emitted records, for cases with an edit).", C)


def gen_compaction():
    cases = []

    def add(name, inp, exp, note=None):
        c = {"name": name, "input": inp, "expected": exp}
        if note:
            c["note"] = note
        cases.append(c)

    def snap(sec, dev, covers, age_ok=True):
        return {"hlc": h(sec, 0, dev), "covers": covers, "ageOk": age_ok}

    def plan(name, cursors, files, snaps, note=None):
        add(name, {"fn": "readPlan", "cursors": cursors, "files": files,
                   "snapshots": [{"hlc": x["hlc"], "covers": x["covers"]} for x in snaps]},
            K.read_plan(cursors, files, snaps), note)

    rng = lambda a, b: list(range(a, b + 1))  # noqa: E731
    plan("fresh join with no snapshots reads every file", {}, {DEV_A: rng(1, 3), DEV_B: rng(1, 2)}, [])
    plan("join after compaction merges the snapshot that covers the missing files", {},
         {DEV_A: rng(41, 42), DEV_B: rng(1, 5)}, [snap(500, DEV_B, {DEV_A: 40, DEV_B: 3})])
    plan("a device that compacted and went quiet: the snapshot is merged although no higher file exists",
         {DEV_B: 10}, {DEV_B: []}, [snap(500, DEV_A, {DEV_A: 7, DEV_B: 40})],
         note="B deleted files 11-40 after a snapshot covered them and wrote nothing since. A reader at "
              "cursor 10 finds no file 11 and no higher file; it must still merge the snapshot.")
    plan("concurrent snapshots: the older one is the one that covers the gap",
         {DEV_A: 12, DEV_B: 5}, {DEV_A: rng(13, 20), DEV_B: rng(11, 12)},
         [snap(600, DEV_A, {DEV_A: 20, DEV_B: 5}), snap(590, DEV_B, {DEV_A: 12, DEV_B: 10})],
         note="Reading only the newest snapshot would never fill B's gap 6-10; readers consider every listed "
              "snapshot.")
    plan("a gap no snapshot covers is reported", {DEV_A: 0}, {DEV_A: [1, 2, 4]}, [])
    plan("two snapshots fill two gaps", {DEV_A: 0}, {DEV_A: rng(1, 3) + rng(11, 13) + [21, 22]},
         [snap(300, DEV_A, {DEV_A: 10}), snap(400, DEV_A, {DEV_A: 20})])
    plan("a snapshot is not required when every file is present", {DEV_A: 0}, {DEV_A: rng(1, 3)},
         [snap(300, DEV_B, {DEV_A: 3})],
         note="A reader MAY merge it anyway; cursorsAfter is the same either way.")

    day = K.DAY_MS
    for name, args in [
        ("snapshot age: both clocks agree it is 8 days old", (T0, T0 + 8 * day, T0, T0 + 8 * day)),
        ("snapshot age: exactly 7 days by both clocks", (T0, T0 + 7 * day, T0, T0 + 7 * day)),
        ("snapshot age: the local clock is 6 days ahead, the provider says 2 days", (T0, T0 + 8 * day, T0, T0 + 2 * day)),
        ("snapshot age: provider time unavailable", (T0, T0 + 30 * day, None, None)),
        ("snapshot age: the local clock is behind", (T0, T0 + 1 * day, T0, T0 + 9 * day)),
    ]:
        add(name, {"fn": "snapshotAgeOk", "snapshotWallMs": args[0], "localNowMs": args[1],
                   "serverCreatedMs": args[2], "serverNowMs": args[3]}, {"ok": K.snapshot_age_ok(*args)})

    def del_files(name, me, seqs, snaps, note=None):
        add(name, {"fn": "deletableChangeFiles", "deviceId": me, "seqs": seqs, "snapshots": snaps},
            {"deletable": K.deletable_change_files(me, seqs, snaps)}, note)

    del_files("own files covered by an old enough snapshot", DEV_A, rng(1, 5), [snap(100, DEV_B, {DEV_A: 3})])
    del_files("nothing is deleted while the covering snapshot is too young", DEV_A, rng(1, 5),
              [snap(100, DEV_B, {DEV_A: 3}, age_ok=False)])
    s_x = snap(600, DEV_A, {DEV_A: 20, DEV_B: 5})
    s_y = snap(590, DEV_B, {DEV_A: 12, DEV_B: 10})
    del_files("a newer snapshot that covers less blocks deletion", DEV_B, rng(1, 12), [s_x, s_y],
              note="S_Y covers B's 6-10 but the newer S_X does not, so 6-10 stay; 1-5 are covered by both.")
    s_z = snap(700, DEV_C, {DEV_A: 20, DEV_B: 12}, age_ok=False)
    del_files("once only dominating snapshots remain, the files can go", DEV_B, rng(1, 12), [s_y, s_z],
              note="11-12 are covered only by S_Z, which is not old enough yet.")

    def del_snaps(name, snaps, note=None):
        add(name, {"fn": "deletableSnapshots", "snapshots": snaps}, {"deletable": K.deletable_snapshots(snaps)}, note)

    del_snaps("the two newest snapshots are never deleted", [s_x, s_y])
    del_snaps("an older snapshot dominated by a kept one can be deleted",
              [snap(100, DEV_A, {DEV_A: 3, DEV_B: 2}), snap(200, DEV_A, {DEV_A: 5, DEV_B: 4}),
               snap(300, DEV_B, {DEV_A: 6, DEV_B: 9})])
    del_snaps("an older snapshot that covers more for some device is kept",
              [snap(100, DEV_A, {DEV_A: 3, DEV_C: 8}), snap(200, DEV_A, {DEV_A: 5, DEV_B: 4}),
               snap(300, DEV_B, {DEV_A: 6, DEV_B: 9})],
              note="Neither kept snapshot covers C up to 8.")
    del_snaps("nothing is deleted while the newest snapshot is too young",
              [snap(100, DEV_A, {DEV_A: 3}), snap(200, DEV_A, {DEV_A: 5}), snap(300, DEV_B, {DEV_A: 6}, age_ok=False)])
    del_snaps("concurrent snapshots resolved by a later dominating one",
              [s_y, s_x, dict(s_z, ageOk=True)],
              note="S_Z merged both, so it dominates S_Y, which can go; S_X is one of the two newest and stays.")

    write("compaction.json", "Reading with snapshots and compaction rules (sync-protocol.md sections 10.2 and 11).",
          cases)


def main():
    os.makedirs(OUT, exist_ok=True)
    gen_hlc()
    gen_ids()
    gen_crypto()
    gen_phrase()
    gen_files()
    gen_merge()
    gen_compaction()


if __name__ == "__main__":
    main()
