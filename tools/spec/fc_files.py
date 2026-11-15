"""Drive paths, file names, decrypted-payload (envelope) checks, vault.json and the pairing QR.
Spec: spec/sync-protocol.md sections 6, 7 and 9.
"""

import json
import math
import re

import fc_frame
import fc_hlc

_SEQ_NAME_RE = re.compile(r"^([0-9]{6,})\.chg$")
_SNAP_NAME_RE = re.compile(r"^(.+)\.snap$")
_PAIR_NAME_RE = re.compile(r"^([0-9a-f]{32})\.pair$")
_QR_RE = re.compile(r"^FCPAIR:1:([0-9A-F]{32}):([0-9A-F]{64})$")


class FileError(ValueError):
    def __init__(self, code):
        super().__init__(code)
        self.code = code


def change_file_name(seq):
    if not isinstance(seq, int) or seq < 1:
        raise FileError("badSeq")
    return f"{seq:06d}.chg"


def parse_change_file_name(name):
    m = _SEQ_NAME_RE.match(name)
    if not m:
        return None
    seq = int(m.group(1))
    if seq < 1 or change_file_name(seq) != name:
        return None
    return seq


def parse_snapshot_file_name(name):
    m = _SNAP_NAME_RE.match(name)
    if not m or not fc_hlc.is_valid(m.group(1)):
        return None
    return m.group(1)


def parse_pairing_file_name(name):
    m = _PAIR_NAME_RE.match(name)
    return m.group(1) if m else None


def parse_path(path):
    """Return a dict describing a vault-relative path, or raise FileError('badPath')."""
    parts = path.split("/")
    if len(parts) == 3 and parts[0] == "devices" and fc_hlc.is_device_id(parts[1]):
        seq = parse_change_file_name(parts[2])
        if seq is not None:
            return {"kind": "changes", "deviceId": parts[1], "seq": seq}
    if len(parts) == 2 and parts[0] == "snapshots":
        h = parse_snapshot_file_name(parts[1])
        if h is not None:
            return {"kind": "snapshot", "hlc": h, "deviceId": fc_hlc.decode(h)[2]}
    if len(parts) == 2 and parts[0] == "pairing":
        pid = parse_pairing_file_name(parts[1])
        if pid is not None:
            return {"kind": "pairing", "pairingId": pid}
    raise FileError("badPath")


MAX_SAFE_INT = (1 << 53) - 1
_TS_RE = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$")


def as_int(x):
    """Integer-typed protocol fields: any JSON number with an integral value (3, 3.0, 3e0) within
    +/-(2^53 - 1) reads as that integer. Booleans, strings, null and fractions are not integers."""
    if isinstance(x, bool):
        return None
    if isinstance(x, int):
        return x if abs(x) <= MAX_SAFE_INT else None
    if isinstance(x, float) and math.isfinite(x) and x.is_integer() and abs(x) <= MAX_SAFE_INT:
        return int(x)
    return None


def _no_duplicates(pairs):
    out = {}
    for k, v in pairs:
        if k in out:
            raise FileError("malformed")
        out[k] = v
    return out


def _reject_constant(name):
    raise FileError("malformed")  # NaN, Infinity, -Infinity are not JSON


def _has_lone_surrogate(v):
    if isinstance(v, str):
        return any(0xD800 <= ord(c) <= 0xDFFF for c in v)
    if isinstance(v, dict):
        return any(_has_lone_surrogate(k) or _has_lone_surrogate(x) for k, x in v.items())
    if isinstance(v, list):
        return any(_has_lone_surrogate(x) for x in v)
    return False


def parse_json_bytes(data):
    """Strict parse of a decrypted payload or vault.json: UTF-8 without BOM, valid JSON, no
    duplicate object keys, no unpaired surrogate escapes. Anything else is `malformed`."""
    if data.startswith(b"\xef\xbb\xbf"):
        raise FileError("malformed")
    try:
        text = data.decode("utf-8")
    except UnicodeDecodeError:
        raise FileError("malformed")
    try:
        value = json.loads(text, object_pairs_hook=_no_duplicates, parse_constant=_reject_constant)
    except ValueError:
        raise FileError("malformed")
    if _has_lone_surrogate(value):
        raise FileError("malformed")
    return value


def _check_format_version(obj):
    v = as_int(obj.get("formatVersion"))
    if v is None:
        raise FileError("malformed")
    if v != 1:
        raise FileError("unsupportedFormat")


def check_envelope(payload, path, vault_id):
    """Validate a decrypted payload against its path. Returns {'ignoredRecords': [...]}."""
    where = parse_path(path)
    if not isinstance(payload, dict):
        raise FileError("malformed")
    if payload.get("type") != where["kind"]:
        raise FileError("typeMismatch")
    _check_format_version(payload)
    if payload.get("vaultId") != vault_id:
        raise FileError("vaultMismatch")
    if where["kind"] == "pairing":
        gen = as_int(payload.get("generation"))
        key = payload.get("vaultKey")
        if (gen is None or gen < 1
                or not isinstance(key, str) or not re.match(r"^[0-9a-f]{64}$", key)
                or not fc_hlc.is_device_id(payload.get("deviceId"))
                or not isinstance(payload.get("expiresAt"), str) or not _TS_RE.match(payload["expiresAt"])):
            raise FileError("malformed")
        return {"ignoredRecords": []}
    if payload.get("deviceId") != where["deviceId"]:
        raise FileError("deviceMismatch")
    if where["kind"] == "changes":
        seq = as_int(payload.get("seq"))
        if seq is None:
            raise FileError("malformed")
        if seq != where["seq"]:
            raise FileError("seqMismatch")
    else:
        if payload.get("hlc") != where["hlc"]:
            raise FileError("hlcMismatch")
        covers = payload.get("covers")
        if not isinstance(covers, dict) or not all(
            fc_hlc.is_device_id(k) and as_int(v) is not None and as_int(v) >= 0 for k, v in covers.items()
        ):
            raise FileError("malformed")
    records = payload.get("records")
    if not isinstance(records, list):
        raise FileError("malformed")
    ignored = []
    if where["kind"] == "changes":
        for i, r in enumerate(records):
            h = r.get("hlc") if isinstance(r, dict) else None
            if not fc_hlc.is_valid(h) or fc_hlc.decode(h)[2] != where["deviceId"]:
                ignored.append(i)
    return {"ignoredRecords": ignored}


def check_vault(vault):
    if not isinstance(vault, dict) or vault.get("type") != "futurecheck-vault":
        raise FileError("malformed")
    _check_format_version(vault)
    gens = vault.get("generations")
    if not isinstance(gens, list) or not gens:
        raise FileError("malformed")
    ids = []
    for g in gens:
        gid = as_int(g.get("id")) if isinstance(g, dict) else None
        if gid is None or gid < 1:
            raise FileError("malformed")
        kc = g.get("keyCheck")
        if not isinstance(kc, str) or not re.match(r"^[0-9a-f]{32}$", kc):
            raise FileError("malformed")
        ids.append(gid)
    current = as_int(vault.get("currentGeneration"))
    if len(set(ids)) != len(ids) or current not in ids:
        raise FileError("malformed")
    return {"currentGeneration": current, "generations": sorted(ids)}


def match_generation(vault, vault_key):
    """Generation whose keyCheck matches vault_key, or None."""
    kc = fc_frame.key_check(vault_key).hex()
    for g in vault["generations"]:
        if g["keyCheck"] == kc:
            return as_int(g["id"])
    return None


def pairing_qr(pairing_id, secret):
    return f"FCPAIR:1:{pairing_id.hex().upper()}:{secret.hex().upper()}"


def parse_pairing_qr(text):
    m = _QR_RE.match(text.strip().upper())
    if not m:
        raise FileError("badQr")
    return bytes.fromhex(m.group(1)), bytes.fromhex(m.group(2))
