"""Encrypted file framing and key derivation, as specified in spec/sync-protocol.md section 6.

Layout (all integers big-endian):
  0..3   magic "FCSY"
  4      format version (1)
  5      kind (1 changes, 2 snapshot, 3 pairing)
  6      compression (0 none)
  7      reserved (0)
  8..11  key generation (uint32; 0 for pairing files)
  12..23 nonce (12 bytes)
  24..   ciphertext
  last16 GCM tag
AAD = bytes 0..11 || UTF-8(path relative to the vault root)
"""

import hashlib
import hmac

from cryptography.exceptions import InvalidTag
from cryptography.hazmat.primitives import hashes
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from cryptography.hazmat.primitives.kdf.hkdf import HKDF

MAGIC = b"FCSY"
VERSION = 1
KIND_CHANGES = 1
KIND_SNAPSHOT = 2
KIND_PAIRING = 3
KINDS = {KIND_CHANGES, KIND_SNAPSHOT, KIND_PAIRING}
HEADER_LEN = 24
TAG_LEN = 16

INFO_FILE_KEY = b"futurecheck/v1/file-key"
INFO_KEY_CHECK = b"futurecheck/v1/key-check"
INFO_PAIRING_KEY = b"futurecheck/v1/pairing-key"


class FrameError(ValueError):
    def __init__(self, code):
        super().__init__(code)
        self.code = code


def hkdf(ikm, info, length):
    """RFC 5869 HKDF-SHA256, salt = 32 zero bytes (via the cryptography package)."""
    return HKDF(algorithm=hashes.SHA256(), length=length, salt=None, info=info).derive(ikm)


def hkdf_manual(ikm, info, length):
    """Same as hkdf(), written out with HMAC only, as an Android implementation would."""
    prk = hmac.new(b"\x00" * 32, ikm, hashlib.sha256).digest()
    okm, t, i = b"", b"", 1
    while len(okm) < length:
        t = hmac.new(prk, t + info + bytes([i]), hashlib.sha256).digest()
        okm += t
        i += 1
    return okm[:length]


def file_key(vault_key):
    _require_key(vault_key)
    return hkdf(vault_key, INFO_FILE_KEY, 32)


def key_check(vault_key):
    _require_key(vault_key)
    return hkdf(vault_key, INFO_KEY_CHECK, 16)


def pairing_key(pairing_secret):
    _require_key(pairing_secret)
    return hkdf(pairing_secret, INFO_PAIRING_KEY, 32)


def _require_key(k):
    if not isinstance(k, (bytes, bytearray)) or len(k) != 32:
        raise FrameError("badKeyLength")


def kind_for_path(path):
    if path.endswith(".chg"):
        return KIND_CHANGES
    if path.endswith(".snap"):
        return KIND_SNAPSHOT
    if path.endswith(".pair"):
        return KIND_PAIRING
    return None


def header(kind, generation, nonce):
    if len(nonce) != 12:
        raise FrameError("badNonceLength")
    return MAGIC + bytes([VERSION, kind, 0, 0]) + generation.to_bytes(4, "big") + nonce


def aad(header_bytes, path):
    return header_bytes[0:12] + path.encode("utf-8")


def seal(secret, kind, generation, nonce, path, plaintext):
    """secret = vault key (kinds 1, 2) or pairing secret (kind 3)."""
    enc_key = pairing_key(secret) if kind == KIND_PAIRING else file_key(secret)
    h = header(kind, generation, nonce)
    ct_and_tag = AESGCM(enc_key).encrypt(nonce, plaintext, aad(h, path))
    return h + ct_and_tag


def open_file(data, path, vault_keys=None, pairing_secret=None):
    """Return plaintext or raise FrameError. vault_keys: {generation: 32-byte key}."""
    if len(data) < HEADER_LEN + TAG_LEN:
        raise FrameError("tooShort")
    if data[0:4] != MAGIC:
        raise FrameError("badMagic")
    if data[4] != VERSION:
        raise FrameError("unsupportedVersion")
    kind = data[5]
    if kind not in KINDS:
        raise FrameError("unknownKind")
    if data[6] != 0:
        raise FrameError("unsupportedCompression")
    if data[7] != 0:
        raise FrameError("reservedNonZero")
    if kind_for_path(path) != kind:
        raise FrameError("kindMismatch")
    generation = int.from_bytes(data[8:12], "big")
    if kind == KIND_PAIRING:
        if generation != 0:
            raise FrameError("badKeyGeneration")
        if pairing_secret is None:
            raise FrameError("unknownKeyGeneration")
        enc_key = pairing_key(pairing_secret)
    else:
        if generation == 0:
            raise FrameError("badKeyGeneration")
        if not vault_keys or generation not in vault_keys:
            raise FrameError("unknownKeyGeneration")
        enc_key = file_key(vault_keys[generation])
    nonce = data[12:24]
    try:
        return AESGCM(enc_key).decrypt(nonce, data[24:], aad(data[0:24], path))
    except InvalidTag:
        raise FrameError("authFailed")
