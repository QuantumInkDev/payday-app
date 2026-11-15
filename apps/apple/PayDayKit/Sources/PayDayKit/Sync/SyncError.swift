/// The sync protocol's error codes (spec/sync-protocol.md section 14). The raw value is the code
/// the fixtures use; apps map codes to messages, the codes are the contract.
public enum SyncError: String, Error, Sendable, CaseIterable {
    // Shapes
    case malformed
    // HLC
    case wallOutOfRange, counterOutOfRange, badDeviceId
    // IDs
    case badInput, notAUuid, notLegacy
    // Opening a file
    case tooShort, badMagic, unsupportedVersion, unknownKind, kindMismatch, reservedNonZero
    case unsupportedCompression, badKeyGeneration, unknownKeyGeneration, authFailed
    // Payload envelope
    case unsupportedFormat, typeMismatch, vaultMismatch, deviceMismatch, seqMismatch, hlcMismatch
    // File names
    case badPath, badSeq
    // Recovery phrase
    case wrongWordCount, unknownWord, badChecksum
    // Pairing QR
    case badQr
    // Crypto helpers
    case badKeyLength, badNonceLength

    /// The embedded BIP39 wordlist is missing or does not match its pinned SHA-256. Not a
    /// protocol code: it means the app bundle is broken.
    case wordlistUnavailable
}

/// Lowercase hex encoding and decoding of byte strings (fixtures use `...Hex` keys).
public enum Hex {
    public static func encode(_ bytes: some Sequence<UInt8>, uppercase: Bool = false) -> String {
        let digits = Array((uppercase ? "0123456789ABCDEF" : "0123456789abcdef").utf8)
        var out: [UInt8] = []
        for b in bytes {
            out.append(digits[Int(b >> 4)])
            out.append(digits[Int(b & 0x0F)])
        }
        return String(decoding: out, as: UTF8.self)
    }

    /// Decodes hex of either case; `nil` for odd length or a non-hex character.
    public static func decode(_ text: String) -> [UInt8]? {
        let chars = Array(text.utf8)
        guard chars.count % 2 == 0 else { return nil }
        var out: [UInt8] = []
        out.reserveCapacity(chars.count / 2)
        var i = 0
        while i < chars.count {
            guard let hi = nibble(chars[i]), let lo = nibble(chars[i + 1]) else { return nil }
            out.append(hi << 4 | lo)
            i += 2
        }
        return out
    }

    /// True when `text` is exactly `count` lowercase hex digits.
    public static func isLowercase(_ text: String, count: Int) -> Bool {
        let u = text.utf8
        return u.count == count && u.allSatisfy { ($0 >= 0x30 && $0 <= 0x39) || ($0 >= 0x61 && $0 <= 0x66) }
    }

    static func nibble(_ c: UInt8) -> UInt8? {
        switch c {
        case 0x30...0x39: return c - 0x30
        case 0x61...0x66: return c - 0x61 + 10
        case 0x41...0x46: return c - 0x41 + 10
        default: return nil
        }
    }
}
