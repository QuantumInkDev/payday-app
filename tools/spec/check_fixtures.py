#!/usr/bin/env python3
"""Re-derive every expected value in spec/conformance/sync/*.json and exit non-zero on any mismatch.

Checks, besides re-running the reference implementation on each case:
  * fixture file conventions (version, description, unique case names, input/expected present);
  * merge cases in every delivery order (all permutations up to 8 records, otherwise a seeded sample
    of 2000 permutations plus forward and reverse order), also with records delivered twice;
  * cross-checks against independent code: uuid.uuid5 for deterministic IDs, uuid.UUID for UUIDv7
    layout, an HMAC-only HKDF for key derivation, published BIP39 vectors for the recovery phrase,
    and byte-wise string comparison for HLC order.

Usage (needs the `cryptography` package):
    python3 tools/spec/check_fixtures.py [-v]
"""

import glob
import itertools
import json
import os
import random
import sys
import uuid

sys.dont_write_bytecode = True  # keep __pycache__ out of the repo
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import fc_bip39 as B  # noqa: E402
import fc_compact as K  # noqa: E402
import fc_files as FL  # noqa: E402
import fc_frame as F  # noqa: E402
import fc_hlc as H  # noqa: E402
import fc_ids as I  # noqa: E402
import fc_merge as G  # noqa: E402
import fc_money as M  # noqa: E402

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
FIXTURES = os.path.join(ROOT, "spec", "conformance", "sync")
ALL_PERMS_MAX = 8
SAMPLE_PERMS = 2000

BIP39_REFERENCE = {
    "00" * 32: " ".join(["abandon"] * 23 + ["art"]),
    "7f" * 32: "legal winner thank year wave sausage worth useful legal winner thank year wave sausage worth useful "
               "legal winner thank year wave sausage worth title",
    "80" * 32: "letter advice cage absurd amount doctor acoustic avoid letter advice cage absurd amount doctor "
               "acoustic avoid letter advice cage absurd amount doctor acoustic bless",
    "ff" * 32: " ".join(["zoo"] * 23 + ["vote"]),
    "68a79eaca2324873eacc50cb9c6eca8cc68ea5d936f98787c60c7ebc74e6ce7c":
        "hamster diagram private dutch cause delay private meat slide toddler razor book happy fancy gospel tennis "
        "maple dilemma loan word shrug inflict delay length",
}


class Failure(Exception):
    pass


def same(a, b):
    """JSON equality that keeps booleans distinct from numbers and compares numbers by value."""
    if isinstance(a, bool) or isinstance(b, bool):
        return type(a) is type(b) and a == b
    if isinstance(a, (int, float)) and isinstance(b, (int, float)):
        return float(a) == float(b)
    if isinstance(a, dict) and isinstance(b, dict):
        return a.keys() == b.keys() and all(same(a[k], b[k]) for k in a)
    if isinstance(a, list) and isinstance(b, list):
        return len(a) == len(b) and all(same(x, y) for x, y in zip(a, b))
    return type(a) is type(b) and a == b


def expect(cond, msg):
    if not cond:
        raise Failure(msg)


def check_equal(got, want, what):
    if not same(got, want):
        raise Failure(f"{what}: got {json.dumps(got)[:400]}, expected {json.dumps(want)[:400]}")


def run_or_error(fn, *args):
    try:
        return fn(*args), None
    except (H.HlcError, I.IdError, F.FrameError, B.PhraseError, FL.FileError) as e:
        return None, e.code


def check_result_or_error(expected, fn, *args, shape=lambda v: v):
    value, err = run_or_error(fn, *args)
    if "error" in expected:
        check_equal(err, expected["error"], "error code")
        return None
    expect(err is None, f"unexpected error {err}")
    check_equal(shape(value), expected, "result")
    return value


# ---------------------------------------------------------------------------


def check_hlc_case(c):
    i, e = c["input"], c["expected"]
    fn = i["fn"]
    if fn == "encode":
        v = check_result_or_error(e, H.encode, i["wall"], i["counter"], i["deviceId"], shape=lambda s: {"hlc": s})
        if v is not None:
            check_equal(H.decode(v), (i["wall"], i["counter"], i["deviceId"]), "round trip")
    elif fn == "decode":
        check_result_or_error(e, H.decode, i["hlc"],
                              shape=lambda t: {"wall": t[0], "counter": t[1], "deviceId": t[2]})
    elif fn == "compare":
        got = H.compare(i["a"], i["b"])
        check_equal(got, e["result"], "compare")
        check_equal((i["a"] > i["b"]) - (i["a"] < i["b"]), e["result"], "byte-wise string compare")
        check_equal(H.compare(i["b"], i["a"]), -e["result"], "antisymmetry")
    elif fn == "tick":
        v = check_result_or_error(e, H.tick, i["last"], i["now"], i["deviceId"], shape=lambda s: {"hlc": s})
        if v is not None and i["last"] is not None:
            expect(H.compare(v, i["last"]) > 0, "tick must move the clock forward")
    elif fn == "observe":
        check_equal({"hlc": H.observe(i["last"], i["remote"], i["deviceId"])}, e, "observe")
    elif fn == "sequence":
        state, outs = None, []
        for s in i["steps"]:
            if s["op"] == "tick":
                new = H.tick(state, s["now"], i["deviceId"])
                expect(state is None or H.compare(new, state) > 0, "tick must increase")
                state = new
            else:
                state = H.observe(state, s["remote"], i["deviceId"])
            outs.append(state)
        check_equal(outs, e["states"], "sequence")
        # every tick after an observe must sort after the observed remote
        seen = []
        for s, out in zip(i["steps"], outs):
            if s["op"] == "observe":
                seen.append(s["remote"])
            else:
                for rmt in seen:
                    expect(H.compare(out, rmt) > 0, "tick after observe must exceed the remote")
    elif fn == "skewWarning":
        check_equal({"warning": H.skew_warning(i["remote"], i["now"])}, e, "skew warning")
    else:
        raise Failure(f"unknown fn {fn}")


def check_ids_case(c):
    i, e = c["input"], c["expected"]
    fn = i["fn"]
    derived = {
        "regularPaymentId": (I.regular_payment_id, I.name_regular_payment, ("billId", "periodKey")),
        "setAsideId": (I.set_aside_id, I.name_set_aside, ("billId", "periodKey")),
        "paycheckId": (I.paycheck_id, I.name_paycheck, ("incomeSourceId", "payDate")),
    }
    if fn in derived:
        idf, namef, keys = derived[fn]
        args = [i[k] for k in keys]
        v = check_result_or_error(e, idf, *args, shape=lambda s: {"name": namef(*args), "id": s})
        if v is not None:
            check_equal(v, str(uuid.uuid5(uuid.UUID(I.NAMESPACE), e["name"])), "uuid.uuid5 cross-check")
            check_equal(uuid.UUID(v).version, 5, "version")
    elif fn == "paycheckIdKind":
        check_equal({"kind": I.paycheck_id_kind(i["cadence"])}, e, "paycheck id kind")
    elif fn in ("legacyPaymentId", "legacySnapshotId"):
        if fn == "legacyPaymentId":
            args = [i["billId"], i["periodKey"], i["amountCents"], i["paidAt"], i["occurrence"]]
            idf, namef = I.legacy_payment_id, I.name_legacy_payment
        else:
            args = [i["snapshotDate"], i["totalCents"], i["occurrence"]]
            idf, namef = I.legacy_snapshot_id, I.name_legacy_snapshot
        v = check_result_or_error(e, idf, *args, shape=lambda s: {"name": namef(*args), "id": s})
        if v is not None:
            check_equal(v, str(uuid.uuid5(uuid.UUID(I.NAMESPACE), e["name"])), "uuid.uuid5 cross-check")
    elif fn == "migrateBillId":
        got = I.migrate_bill_id(i["id"])
        check_equal(got, e["id"], "migrated id")
        if "name" in e:
            check_equal(I.name_legacy_bill(i["id"]), e["name"], "legacy name")
            check_equal(str(uuid.uuid5(uuid.UUID(I.NAMESPACE), e["name"])), e["id"], "uuid.uuid5 cross-check")
    elif fn == "uuid7":
        got = I.uuid7(i["unixMs"], bytes.fromhex(i["randomHex"]))
        check_equal(got, e["id"], "uuid7")
        u = uuid.UUID(got)
        expect(u.version == 7 and u.variant == uuid.RFC_4122, "uuid module disagrees on version/variant")
        expect(int(u.hex[:12], 16) == i["unixMs"], "timestamp field")
    elif fn == "inspectUuid":
        check_equal(I.inspect_uuid(i["id"]), e, "inspect")
    elif fn == "isDeviceId":
        check_equal({"valid": H.is_device_id(i["id"])}, e, "device id")
    else:
        raise Failure(f"unknown fn {fn}")


def check_crypto_case(c):
    i, e = c["input"], c["expected"]
    fn = i["fn"]
    if fn == "deriveKeys":
        k = bytes.fromhex(i["vaultKeyHex"])
        check_equal({"fileKeyHex": F.file_key(k).hex(), "keyCheckHex": F.key_check(k).hex()}, e, "derived keys")
        check_equal(F.hkdf_manual(k, F.INFO_FILE_KEY, 32).hex(), e["fileKeyHex"], "HMAC-only HKDF (file key)")
        check_equal(F.hkdf_manual(k, F.INFO_KEY_CHECK, 16).hex(), e["keyCheckHex"], "HMAC-only HKDF (key check)")
    elif fn == "derivePairingKey":
        s = bytes.fromhex(i["pairingSecretHex"])
        check_equal(F.pairing_key(s).hex(), e["pairingKeyHex"], "pairing key")
        check_equal(F.hkdf_manual(s, F.INFO_PAIRING_KEY, 32).hex(), e["pairingKeyHex"], "HMAC-only HKDF (pairing)")
    elif fn == "seal":
        secret = bytes.fromhex(i.get("vaultKeyHex") or i["pairingSecretHex"])
        pt = i["plaintextUtf8"].encode("utf-8")
        data = F.seal(secret, i["kind"], i["keyGeneration"], bytes.fromhex(i["nonceHex"]), i["path"], pt)
        check_equal(data.hex(), e["fileHex"], "sealed file")
        check_equal(data[:24].hex(), e["headerHex"], "header")
        check_equal(F.aad(data[:24], i["path"]).hex(), e["aadHex"], "aad")
        check_equal(len(data), 24 + len(pt) + 16, "length")
        vk = {i["keyGeneration"]: secret} if i["kind"] != F.KIND_PAIRING else None
        ps = secret if i["kind"] == F.KIND_PAIRING else None
        check_equal(F.open_file(data, i["path"], vk, ps), pt, "round trip")
    elif fn == "open":
        vk = {int(g): bytes.fromhex(x) for g, x in i["vaultKeysHex"].items()} if i["vaultKeysHex"] else None
        ps = bytes.fromhex(i["pairingSecretHex"]) if "pairingSecretHex" in i else None
        check_result_or_error(e, F.open_file, bytes.fromhex(i["fileHex"]), i["path"], vk, ps,
                              shape=lambda b: {"plaintextUtf8": b.decode("utf-8")})
    elif fn == "matchGeneration":
        FL.check_vault(i["vault"])
        check_equal({"generation": FL.match_generation(i["vault"], bytes.fromhex(i["vaultKeyHex"]))}, e, "generation")
    elif fn == "pairingQr":
        pid, sec = bytes.fromhex(i["pairingIdHex"]), bytes.fromhex(i["pairingSecretHex"])
        check_equal({"qr": FL.pairing_qr(pid, sec), "path": f"pairing/{pid.hex()}.pair"}, e, "qr")
        expect(all(ch in "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:" for ch in e["qr"]),
               "QR string must fit QR alphanumeric mode")
    elif fn == "parsePairingQr":
        check_result_or_error(e, FL.parse_pairing_qr, i["qr"],
                              shape=lambda t: {"pairingIdHex": t[0].hex(), "pairingSecretHex": t[1].hex()})
    else:
        raise Failure(f"unknown fn {fn}")


def check_phrase_case(c):
    i, e = c["input"], c["expected"]
    if i["fn"] == "encode":
        words = B.encode(bytes.fromhex(i["keyHex"]))
        check_equal({"words": words, "phrase": " ".join(words)}, e, "phrase")
        if i["keyHex"] in BIP39_REFERENCE:
            check_equal(e["phrase"], BIP39_REFERENCE[i["keyHex"]], "published BIP39 vector")
    elif i["fn"] == "decode":
        check_result_or_error(e, B.decode, i["phrase"], shape=lambda k: {"keyHex": k.hex()})
        if "keyHex" in e and e["keyHex"] in BIP39_REFERENCE:
            check_equal(B.normalize(i["phrase"]), BIP39_REFERENCE[e["keyHex"]].split(), "published BIP39 vector")
    else:
        raise Failure(f"unknown fn {i['fn']}")


def check_files_case(c):
    i, e = c["input"], c["expected"]
    fn = i["fn"]
    if fn == "changeFileName":
        v = check_result_or_error(e, FL.change_file_name, i["seq"], shape=lambda s: {"name": s})
        if v is not None:
            check_equal(FL.parse_change_file_name(v), i["seq"], "parse back")
    elif fn == "parsePath":
        check_result_or_error(e, FL.parse_path, i["path"])
    elif fn == "checkEnvelope":
        given = [k for k in ("payload", "payloadText", "payloadHex") if k in i]
        expect(len(given) == 1, "exactly one of payload, payloadText, payloadHex")
        if given[0] == "payload":
            check_result_or_error(e, FL.check_envelope, i["payload"], i["path"], i["vaultId"])
        else:
            data = i["payloadText"].encode("utf-8") if given[0] == "payloadText" else bytes.fromhex(i["payloadHex"])
            check_result_or_error(e, lambda: FL.check_envelope(FL.parse_json_bytes(data), i["path"], i["vaultId"]))
    elif fn == "checkVault":
        if "vaultText" in i:
            check_result_or_error(e, lambda: FL.check_vault(FL.parse_json_bytes(i["vaultText"].encode("utf-8"))))
        else:
            check_result_or_error(e, FL.check_vault, i["vault"])
    else:
        raise Failure(f"unknown fn {fn}")


# ---------------------------------------------------------------------------


def state_of(records):
    s = G.Store().apply_all(records)
    d = s.derived()
    return s, {"rows": s.rows(), "live": s.live(), "remaining": d["remaining"], "setAsideBalance": d["setAsideBalance"]}


def compare_state(got, want, label):
    for k in ("rows", "live"):
        check_equal(got[k], want[k], f"{label}: {k}")
    for k in ("remaining", "setAsideBalance"):
        expect(got[k].keys() == want[k].keys(), f"{label}: {k} bills differ: {sorted(got[k])} vs {sorted(want[k])}")
        for b in got[k]:
            expect(M.cents(got[k][b]) == M.cents(want[k][b]), f"{label}: {k}[{b}] {got[k][b]} != {want[k][b]}")


def orders(n, rng):
    if n <= ALL_PERMS_MAX:
        yield from itertools.permutations(range(n))
        return
    yield tuple(range(n))
    yield tuple(reversed(range(n)))
    for _ in range(SAMPLE_PERMS):
        p = list(range(n))
        rng.shuffle(p)
        yield tuple(p)


def check_merge_case(c, stats):
    i, e = c["input"], c["expected"]
    expect(i["fn"] == "merge", "fn must be merge")
    records = i["records"]
    final_records = list(records)
    if "edit" in i:
        seen_store, _ = state_of([records[k] for k in i["edit"]["seen"]])
        emitted = G.run_op(seen_store, i["edit"]["op"])
        check_equal(emitted, e["emitted"], "emitted records")
        for rec in emitted:
            expect(G.is_valid_record(rec), "emitted record must be valid")
            expect(H.compare(rec["hlc"], max((r["hlc"] for r in [records[k] for k in i["edit"]["seen"]]
                                              if G.is_valid_record(r)), default=H.encode(0, 0, "0" * 16))) > 0,
                   "edit HLC must be later than everything the editing device has seen")
        final_records += e["emitted"]
    else:
        expect("emitted" not in e, "emitted without an edit")

    rng = random.Random(c["name"])
    n = len(final_records)
    count = 0
    for perm in orders(n, rng):
        ordered = [final_records[k] for k in perm]
        _, got = state_of(ordered)
        compare_state(got, e, f"order {perm[:12]}{'...' if n > 12 else ''}")
        count += 1
        if count % 50 == 1:
            dup = ordered + [ordered[k] for k in rng.sample(range(n), max(1, n // 3))]
            rng.shuffle(dup)
            _, got = state_of(dup)
            compare_state(got, e, "order with duplicated records")
    stats["orders"] += count


def check_compaction_case(c):
    i, e = c["input"], c["expected"]
    fn = i["fn"]
    if fn == "readPlan":
        got = K.read_plan(i["cursors"], i["files"], i["snapshots"], i.get("unreadable"))
        check_equal(got, e, "read plan")
        # Optional extra snapshot merges must not change the resulting cursors.
        cur = dict(i["cursors"])
        for snap in i["snapshots"]:
            for d, v in snap["covers"].items():
                cur[d] = max(cur.get(d, 0), v)
        extra = K.read_plan(cur, i["files"], i["snapshots"], i.get("unreadable"))
        expect(all(extra["cursorsAfter"].get(d, 0) == v for d, v in e["cursorsAfter"].items()),
               "merging every snapshot up front must reach the same cursors")
    elif fn == "snapshotAgeOk":
        check_equal({"ok": K.snapshot_age_ok(i["firstSeenLocalMs"], i["localNowMs"], i["serverCreatedMs"],
                                             i["serverNowMs"])}, e, "age")
    elif fn == "deletableChangeFiles":
        got = K.deletable_change_files(i["deviceId"], i["seqs"], i["snapshots"])
        check_equal({"deletable": got}, e, "deletable files")
        # Invariant: every deletable file stays covered by a snapshot that is not itself deletable.
        survivors = [x for x in i["snapshots"] if x["hlc"] not in K.deletable_snapshots(i["snapshots"])]
        for n in got:
            expect(any(x["covers"].get(i["deviceId"], 0) >= n for x in survivors), f"file {n} would be lost")
    elif fn == "deletableSnapshots":
        got = K.deletable_snapshots(i["snapshots"])
        check_equal({"deletable": got}, e, "deletable snapshots")
        kept = [x for x in i["snapshots"] if x["hlc"] not in got]
        for x in i["snapshots"]:
            if x["hlc"] in got:
                expect(any(all(k["covers"].get(d, 0) >= v for d, v in x["covers"].items()) for k in kept),
                       f"deleting {x['hlc']} loses coverage")
    else:
        raise Failure(f"unknown fn {fn}")


# ---------------------------------------------------------------------------

CHECKERS = {
    "hlc.json": check_hlc_case,
    "ids.json": check_ids_case,
    "crypto.json": check_crypto_case,
    "recovery-phrase.json": check_phrase_case,
    "files.json": check_files_case,
    "compaction.json": check_compaction_case,
    "merge.json": None,  # needs stats
}


def main():
    verbose = "-v" in sys.argv
    files = sorted(glob.glob(os.path.join(FIXTURES, "*.json")))
    failures, total = 0, 0
    stats = {"orders": 0}
    names_on_disk = {os.path.basename(f) for f in files}
    for missing in sorted(set(CHECKERS) - names_on_disk):
        print(f"MISSING {missing}")
        failures += 1
    for path in files:
        base = os.path.basename(path)
        with open(path, encoding="utf-8") as f:
            doc = json.load(f)
        try:
            expect(doc.get("version") == 1, "version must be 1")
            expect(isinstance(doc.get("description"), str) and doc["description"], "description required")
            expect(isinstance(doc.get("cases"), list) and doc["cases"], "cases required")
            names = [c.get("name") for c in doc["cases"]]
            expect(len(set(names)) == len(names), "case names must be unique")
        except Failure as ex:
            print(f"FAIL {base}: {ex}")
            failures += 1
            continue
        if base not in CHECKERS:
            print(f"FAIL {base}: no checker for this file")
            failures += 1
            continue
        file_fail = 0
        for c in doc["cases"]:
            total += 1
            try:
                expect({"name", "input", "expected"} <= c.keys(), "case needs name, input, expected")
                if base == "merge.json":
                    check_merge_case(c, stats)
                else:
                    CHECKERS[base](c)
                if verbose:
                    print(f"  ok   {base}: {c['name']}")
            except Exception as ex:  # report every failure, keep going
                failures += 1
                file_fail += 1
                print(f"FAIL {base}: {c.get('name')}: {type(ex).__name__}: {ex}")
        print(f"{base}: {len(doc['cases']) - file_fail}/{len(doc['cases'])} cases pass")
    print(f"merge delivery orders checked: {stats['orders']}")
    if failures:
        print(f"{failures} failure(s) in {total} cases")
        return 1
    print(f"all {total} cases pass")
    return 0


if __name__ == "__main__":
    sys.exit(main())
