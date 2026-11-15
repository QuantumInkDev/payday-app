"""Drive paths, file names, decrypted-payload (envelope) checks, vault.json and the pairing QR.
Spec: spec/sync-protocol.md sections 6, 7 and 9.
"""

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


def _is_int(x):
    return isinstance(x, int) and not isinstance(x, bool)


def check_envelope(payload, path, vault_id):
    """Validate a decrypted payload against its path. Returns {'ignoredRecords': [...]}."""
    where = parse_path(path)
    if not isinstance(payload, dict):
        raise FileError("malformed")
    if payload.get("type") != where["kind"]:
        raise FileError("typeMismatch")
    if payload.get("formatVersion") != 1:
        raise FileError("unsupportedFormat")
    if payload.get("vaultId") != vault_id:
        raise FileError("vaultMismatch")
    if where["kind"] == "pairing":
        return {"ignoredRecords": []}
    if payload.get("deviceId") != where["deviceId"]:
        raise FileError("deviceMismatch")
    if where["kind"] == "changes":
        if not _is_int(payload.get("seq")):
            raise FileError("malformed")
        if payload["seq"] != where["seq"]:
            raise FileError("seqMismatch")
    else:
        if payload.get("hlc") != where["hlc"]:
            raise FileError("hlcMismatch")
        covers = payload.get("covers")
        if not isinstance(covers, dict) or not all(
            fc_hlc.is_device_id(k) and _is_int(v) and v >= 0 for k, v in covers.items()
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
    if vault.get("formatVersion") != 1:
        raise FileError("unsupportedFormat")
    gens = vault.get("generations")
    if not isinstance(gens, list) or not gens:
        raise FileError("malformed")
    ids = []
    for g in gens:
        if not isinstance(g, dict) or not _is_int(g.get("id")) or g["id"] < 1:
            raise FileError("malformed")
        kc = g.get("keyCheck")
        if not isinstance(kc, str) or not re.match(r"^[0-9a-f]{32}$", kc):
            raise FileError("malformed")
        ids.append(g["id"])
    if len(set(ids)) != len(ids) or vault.get("currentGeneration") not in ids:
        raise FileError("malformed")
    return {"currentGeneration": vault["currentGeneration"], "generations": sorted(ids)}


def match_generation(vault, vault_key):
    """Generation whose keyCheck matches vault_key, or None."""
    kc = fc_frame.key_check(vault_key).hex()
    for g in vault["generations"]:
        if g["keyCheck"] == kc:
            return g["id"]
    return None


def pairing_qr(pairing_id, secret):
    return f"FCPAIR:1:{pairing_id.hex().upper()}:{secret.hex().upper()}"


def parse_pairing_qr(text):
    m = _QR_RE.match(text.strip().upper())
    if not m:
        raise FileError("badQr")
    return bytes.fromhex(m.group(1)), bytes.fromhex(m.group(2))
