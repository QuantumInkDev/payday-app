import CryptoKit
import Foundation

/// The recovery phrase: the 32-byte vault key as 24 BIP39 English words (spec/sync-protocol.md
/// section 8). The BIP39 seed derivation (PBKDF2, passphrase) is not used.
public enum RecoveryPhrase {
    /// SHA-256 of spec/bip39-english.txt, which this package embeds unchanged.
    public static let wordlistSHA256 = "2f5eed53a4727b4bf8880d8f3f199efc90e58503646d9ff8eff3a2ed3b24dbda"
    public static let wordCount = 24

    /// The separators decoding splits on: space, tab, CR, LF and no-break space; nothing else.
    public static let separators: Set<Unicode.Scalar> = [" ", "\t", "\r", "\n", "\u{00A0}"]

    private struct Wordlist: Sendable {
        let words: [String]
        let index: [String: Int]
    }

    /// The embedded wordlist file's bytes (`nil` if the resource is missing).
    public static var embeddedWordlistBytes: [UInt8]? {
        guard let url = Bundle.module.url(forResource: "bip39-english", withExtension: "txt"),
              let data = try? Data(contentsOf: url) else { return nil }
        return Array(data)
    }

    private static let wordlist: Wordlist? = {
        guard let raw = embeddedWordlistBytes,
              Hex.encode(SHA256.hash(data: raw)) == wordlistSHA256 else { return nil }
        var words = String(decoding: raw, as: UTF8.self).split(separator: "\n", omittingEmptySubsequences: false).map(String.init)
        if words.last == "" { words.removeLast() }
        guard words.count == 2048 else { return nil }
        var index: [String: Int] = [:]
        for (i, w) in words.enumerated() { index[w] = i }
        return Wordlist(words: words, index: index)
    }()

    /// The 2048 words, or `wordlistUnavailable` if the bundle's copy is missing or altered.
    public static func words() throws(SyncError) -> [String] {
        guard let wordlist else { throw .wordlistUnavailable }
        return wordlist.words
    }

    /// 24 words: the 256 key bits followed by the first byte of SHA-256(key), in 11-bit groups
    /// from the most significant end.
    public static func encode(key: [UInt8]) throws(SyncError) -> [String] {
        guard key.count == 32 else { throw .badKeyLength }
        guard let wordlist else { throw .wordlistUnavailable }
        let bits = key + [Array(SHA256.hash(data: key))[0]] // 33 bytes = 264 bits
        return (0..<wordCount).map { wordlist.words[bitGroup(bits, at: $0 * 11)] }
    }

    /// The phrase as text: words joined with single spaces.
    public static func phrase(key: [UInt8]) throws(SyncError) -> String {
        try encode(key: key).joined(separator: " ")
    }

    /// Lowercases `A`-`Z` only and splits on runs of the five separators; any other character,
    /// including other Unicode spaces, is part of a word.
    public static func normalize(_ phrase: String) -> [String] {
        var words: [String] = []
        var current = String.UnicodeScalarView()
        for s in phrase.unicodeScalars {
            if separators.contains(s) {
                if !current.isEmpty { words.append(String(current)); current = String.UnicodeScalarView() }
            } else if s.value >= 0x41, s.value <= 0x5A {
                current.append(Unicode.Scalar(s.value + 0x20)!)
            } else {
                current.append(s)
            }
        }
        if !current.isEmpty { words.append(String(current)) }
        return words
    }

    /// The 32-byte key, or `wrongWordCount`, `unknownWord`, `badChecksum`. Confirm the result
    /// against vault.json's key check values before use.
    public static func decode(_ phrase: String) throws(SyncError) -> [UInt8] {
        guard let wordlist else { throw .wordlistUnavailable }
        let words = normalize(phrase)
        guard words.count == wordCount else { throw .wrongWordCount }
        var bits = [UInt8](repeating: 0, count: 33)
        for (i, w) in words.enumerated() {
            guard let idx = wordlist.index[w] else { throw .unknownWord }
            for j in 0..<11 where idx & (1 << (10 - j)) != 0 {
                let pos = i * 11 + j
                bits[pos / 8] |= 0x80 >> UInt8(pos % 8)
            }
        }
        let key = Array(bits[0..<32])
        guard Array(SHA256.hash(data: key))[0] == bits[32] else { throw .badChecksum }
        return key
    }

    private static func bitGroup(_ bytes: [UInt8], at offset: Int) -> Int {
        var v = 0
        for j in 0..<11 {
            let pos = offset + j
            v = v << 1 | Int((bytes[pos / 8] >> UInt8(7 - pos % 8)) & 1)
        }
        return v
    }
}
