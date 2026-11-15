import CryptoKit
import Foundation

/// The kind byte of an encrypted file.
public enum SyncFileKind: UInt8, Sendable, CaseIterable {
    case changes = 1
    case snapshot = 2
    case pairing = 3

    /// The kind a vault-relative path's extension implies.
    public init?(path: String) {
        if path.hasSuffix(".chg") { self = .changes }
        else if path.hasSuffix(".snap") { self = .snapshot }
        else if path.hasSuffix(".pair") { self = .pairing }
        else { return nil }
    }
}

/// Key derivation and AES-256-GCM file framing (spec/sync-protocol.md sections 6.5 and 6.6).
public enum FileCrypto {
    public static let magic: [UInt8] = Array("FCSY".utf8)
    public static let formatVersion: UInt8 = 1
    public static let headerLength = 24
    public static let tagLength = 16
    public static let nonceLength = 12

    public static let fileKeyInfo = "futurecheck/v1/file-key"
    public static let keyCheckInfo = "futurecheck/v1/key-check"
    public static let pairingKeyInfo = "futurecheck/v1/pairing-key"

    // MARK: Key derivation

    /// HKDF-SHA256 (RFC 5869) with salt = 32 zero bytes.
    public static func hkdf(_ inputKey: [UInt8], info: String, length: Int) -> [UInt8] {
        let key = HKDF<SHA256>.deriveKey(
            inputKeyMaterial: SymmetricKey(data: inputKey),
            salt: [UInt8](repeating: 0, count: 32),
            info: Array(info.utf8),
            outputByteCount: length
        )
        return key.withUnsafeBytes { Array($0) }
    }

    /// The file key of a vault key (32 bytes).
    public static func fileKey(vaultKey: [UInt8]) throws(SyncError) -> [UInt8] {
        try requireKey(vaultKey)
        return hkdf(vaultKey, info: fileKeyInfo, length: 32)
    }

    /// The key check value of a vault key (16 bytes), stored in vault.json.
    public static func keyCheck(vaultKey: [UInt8]) throws(SyncError) -> [UInt8] {
        try requireKey(vaultKey)
        return hkdf(vaultKey, info: keyCheckInfo, length: 16)
    }

    /// The pairing key of a pairing secret (32 bytes).
    public static func pairingKey(pairingSecret: [UInt8]) throws(SyncError) -> [UInt8] {
        try requireKey(pairingSecret)
        return hkdf(pairingSecret, info: pairingKeyInfo, length: 32)
    }

    static func requireKey(_ k: [UInt8]) throws(SyncError) {
        guard k.count == 32 else { throw .badKeyLength }
    }

    // MARK: Framing

    /// The 24-byte header: magic, version, kind, compression 0, reserved 0, generation, nonce.
    public static func header(kind: SyncFileKind, generation: UInt32, nonce: [UInt8]) throws(SyncError) -> [UInt8] {
        guard nonce.count == nonceLength else { throw .badNonceLength }
        var h = magic + [formatVersion, kind.rawValue, 0, 0]
        h += [UInt8(truncatingIfNeeded: generation >> 24), UInt8(truncatingIfNeeded: generation >> 16),
              UInt8(truncatingIfNeeded: generation >> 8), UInt8(truncatingIfNeeded: generation)]
        return h + nonce
    }

    /// Associated data: header bytes 0 to 11, then the UTF-8 path relative to the vault root.
    public static func aad(header: [UInt8], path: String) -> [UInt8] {
        Array(header.prefix(12)) + Array(path.utf8)
    }

    /// Seals a plaintext. `secret` is the vault key of `generation` (kinds 1 and 2) or the pairing
    /// secret (kind 3, generation 0). Pass a fresh random `nonce` for every file written; it is a
    /// parameter only so the fixtures can pin it.
    public static func seal(secret: [UInt8], kind: SyncFileKind, generation: UInt32, nonce: [UInt8], path: String, plaintext: [UInt8]) throws(SyncError) -> [UInt8] {
        let key = kind == .pairing ? try pairingKey(pairingSecret: secret) : try fileKey(vaultKey: secret)
        let h = try header(kind: kind, generation: generation, nonce: nonce)
        do {
            let box = try AES.GCM.seal(plaintext, using: SymmetricKey(data: key),
                                       nonce: AES.GCM.Nonce(data: nonce), authenticating: aad(header: h, path: path))
            return h + Array(box.ciphertext) + Array(box.tag)
        } catch {
            throw .badNonceLength
        }
    }

    /// Seals with a fresh random nonce.
    public static func seal(secret: [UInt8], kind: SyncFileKind, generation: UInt32, path: String, plaintext: [UInt8]) throws(SyncError) -> [UInt8] {
        var g = SystemRandomNumberGenerator()
        let nonce = (0..<nonceLength).map { _ in UInt8.random(in: .min ... .max, using: &g) }
        return try seal(secret: secret, kind: kind, generation: generation, nonce: nonce, path: path, plaintext: plaintext)
    }

    /// Opens a file, with the checks in the spec's order: `tooShort`, `badMagic`,
    /// `unsupportedVersion`, `unknownKind`, `unsupportedCompression`, `reservedNonZero`,
    /// `kindMismatch`, `badKeyGeneration`, `unknownKeyGeneration`, `authFailed`.
    /// `vaultKeys` maps key generations to vault keys; `pairingSecret` opens a pairing file.
    public static func open(_ data: [UInt8], path: String, vaultKeys: [UInt32: [UInt8]]?, pairingSecret: [UInt8]? = nil) throws(SyncError) -> [UInt8] {
        guard data.count >= headerLength + tagLength else { throw .tooShort }
        guard Array(data[0..<4]) == magic else { throw .badMagic }
        guard data[4] == formatVersion else { throw .unsupportedVersion }
        guard let kind = SyncFileKind(rawValue: data[5]) else { throw .unknownKind }
        guard data[6] == 0 else { throw .unsupportedCompression }
        guard data[7] == 0 else { throw .reservedNonZero }
        guard SyncFileKind(path: path) == kind else { throw .kindMismatch }
        let generation = data[8..<12].reduce(UInt32(0)) { $0 << 8 | UInt32($1) }
        let key: [UInt8]
        if kind == .pairing {
            guard generation == 0 else { throw .badKeyGeneration }
            guard let pairingSecret else { throw .unknownKeyGeneration }
            key = try pairingKey(pairingSecret: pairingSecret)
        } else {
            guard generation != 0 else { throw .badKeyGeneration }
            guard let vaultKey = vaultKeys?[generation] else { throw .unknownKeyGeneration }
            key = try fileKey(vaultKey: vaultKey)
        }
        let header = Array(data[0..<headerLength])
        do {
            let box = try AES.GCM.SealedBox(
                nonce: AES.GCM.Nonce(data: data[12..<24]),
                ciphertext: data[headerLength..<(data.count - tagLength)],
                tag: data[(data.count - tagLength)...]
            )
            return Array(try AES.GCM.open(box, using: SymmetricKey(data: key), authenticating: aad(header: header, path: path)))
        } catch {
            throw .authFailed
        }
    }
}
