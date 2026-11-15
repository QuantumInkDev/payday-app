"""Recovery phrase: BIP39 English, 24 words, encoding the 256-bit vault key directly.

Spec: spec/sync-protocol.md section 8. The BIP39 seed derivation (PBKDF2) is NOT used.
"""

import hashlib
import os

WORDLIST_SHA256 = "2f5eed53a4727b4bf8880d8f3f199efc90e58503646d9ff8eff3a2ed3b24dbda"
_WORDLIST_PATH = os.path.join(os.path.dirname(__file__), "..", "..", "spec", "bip39-english.txt")


class PhraseError(ValueError):
    def __init__(self, code):
        super().__init__(code)
        self.code = code


def _load():
    with open(_WORDLIST_PATH, "rb") as f:
        raw = f.read()
    if hashlib.sha256(raw).hexdigest() != WORDLIST_SHA256:
        raise RuntimeError("spec/bip39-english.txt does not match its pinned SHA-256")
    words = raw.decode("ascii").split("\n")
    if words[-1] == "":
        words = words[:-1]
    if len(words) != 2048:
        raise RuntimeError("wordlist must have 2048 words")
    return words


WORDS = _load()
INDEX = {w: i for i, w in enumerate(WORDS)}


def encode(key):
    if len(key) != 32:
        raise PhraseError("badKeyLength")
    checksum = hashlib.sha256(key).digest()[0]
    bits = (int.from_bytes(key, "big") << 8) | checksum  # 264 bits
    return [WORDS[(bits >> (11 * (23 - i))) & 0x7FF] for i in range(24)]


SEPARATORS = " \t\r\n\u00a0"  # space, tab, CR, LF, no-break space; nothing else


def normalize(phrase):
    """Lowercase A-Z only, split on runs of SEPARATORS. Other characters stay in the word."""
    lowered = "".join(chr(ord(c) + 32) if "A" <= c <= "Z" else c for c in phrase)
    words, cur = [], []
    for c in lowered:
        if c in SEPARATORS:
            if cur:
                words.append("".join(cur))
                cur = []
        else:
            cur.append(c)
    if cur:
        words.append("".join(cur))
    return words


def decode(phrase):
    words = normalize(phrase)
    if len(words) != 24:
        raise PhraseError("wrongWordCount")
    bits = 0
    for w in words:
        if w not in INDEX:
            raise PhraseError("unknownWord")
        bits = (bits << 11) | INDEX[w]
    key = (bits >> 8).to_bytes(32, "big")
    if hashlib.sha256(key).digest()[0] != bits & 0xFF:
        raise PhraseError("badChecksum")
    return key
