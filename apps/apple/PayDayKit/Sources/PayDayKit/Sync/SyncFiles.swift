import CryptoKit
import Foundation

/// A sync file's place in the drive layout (spec/sync-protocol.md sections 6.1 and 6.2).
public enum SyncPath: Sendable, Hashable {
    case changes(deviceId: DeviceID, seq: Int64)
    case snapshot(hlc: HLC)
    case pairing(pairingId: String)

    /// The `type` its payload must declare.
    public var payloadType: String {
        switch self {
        case .changes: "changes"
        case .snapshot: "snapshot"
        case .pairing: "pairing"
        }
    }

    /// The vault-relative path.
    public var path: String {
        switch self {
        case let .changes(d, seq): "devices/\(d.rawValue)/" + ((try? SyncFiles.changeFileName(seq: seq)) ?? "")
        case let .snapshot(h): "snapshots/\(h.string).snap"
        case let .pairing(p): "pairing/\(p).pair"
        }
    }
}

/// File names, strict payload parsing, envelope checks, vault.json and the pairing QR code
/// (spec/sync-protocol.md sections 6, 7 and 9).
public enum SyncFiles {
    public static let maxSafeInteger: Int64 = 9_007_199_254_740_991

    // MARK: Names and paths

    /// `<seq>.chg`, zero-padded to at least 6 digits. `badSeq` for `seq < 1`.
    public static func changeFileName(seq: Int64) throws(SyncError) -> String {
        guard seq >= 1 else { throw .badSeq }
        return CivilDate.pad(Int(seq), 6) + ".chg"
    }

    /// The sequence number of a change file name; `nil` unless the name is exactly that
    /// formatting of its own number (`00001.chg`, `0000001.chg`, `000001.CHG` are not).
    public static func parseChangeFileName(_ name: String) -> Int64? {
        guard name.hasSuffix(".chg") else { return nil }
        let digits = name.utf8.dropLast(4)
        guard digits.count >= 6, digits.count <= 18,
              digits.allSatisfy({ $0 >= 0x30 && $0 <= 0x39 }),
              let seq = Int64(String(decoding: digits, as: UTF8.self)), seq >= 1,
              (try? changeFileName(seq: seq)) == name else { return nil }
        return seq
    }

    /// Parses a vault-relative path; `badPath` for anything that is not a sync file.
    public static func parsePath(_ path: String) throws(SyncError) -> SyncPath {
        let parts = path.split(separator: "/", omittingEmptySubsequences: false).map(String.init)
        if parts.count == 3, parts[0] == "devices", let device = DeviceID(parts[1]),
           let seq = parseChangeFileName(parts[2]) {
            return .changes(deviceId: device, seq: seq)
        }
        if parts.count == 2, parts[0] == "snapshots", parts[1].hasSuffix(".snap"),
           let hlc = try? HLC(String(parts[1].dropLast(5))) {
            return .snapshot(hlc: hlc)
        }
        if parts.count == 2, parts[0] == "pairing", parts[1].hasSuffix(".pair") {
            let id = String(parts[1].dropLast(5))
            if Hex.isLowercase(id, count: 32) { return .pairing(pairingId: id) }
        }
        throw .badPath
    }

    // MARK: Payload JSON

    /// Strict parse of a decrypted payload or vault.json (section 6.3): UTF-8 without BOM, exactly
    /// one JSON value, no duplicate keys, no lone surrogates, no non-JSON tokens. Else `malformed`.
    public static func parsePayload(_ bytes: [UInt8]) throws(SyncError) -> JSONValue {
        do { return try JSONParser.parseStrict(bytes) } catch { throw .malformed }
    }

    /// An integer-typed protocol field: any JSON number that is integral once parsed to binary64,
    /// within ±(2^53 − 1). Booleans, strings, `null` and fractions are not integers.
    public static func asInt(_ value: JSONValue?) -> Int64? {
        guard case .number(let n)? = value, n.value.isFinite, n.value.rounded(.towardZero) == n.value,
              abs(n.value) <= Double(maxSafeInteger) else { return nil }
        return Int64(n.value)
    }

    // MARK: Envelope

    /// The result of a passed envelope check: indexes of records to ignore (in a change file, the
    /// records that are not objects with a valid HLC of the file's own device).
    public struct EnvelopeResult: Sendable, Hashable {
        public let path: SyncPath
        public let ignoredRecords: [Int]
    }

    /// Runs the envelope checks of section 6.3 in order; the first failure is thrown.
    public static func checkEnvelope(_ payload: JSONValue, path: String, vaultId: String) throws(SyncError) -> EnvelopeResult {
        let where_ = try parsePath(path)
        guard case .object(let obj) = payload else { throw .malformed }
        guard obj["type"]?.stringValue == where_.payloadType else { throw .typeMismatch }
        try checkFormatVersion(obj)
        guard obj["vaultId"]?.stringValue == vaultId else { throw .vaultMismatch }
        let deviceText = obj["deviceId"]?.stringValue
        switch where_ {
        case .pairing:
            guard let gen = asInt(obj["generation"]), gen >= 1,
                  let key = obj["vaultKey"]?.stringValue, Hex.isLowercase(key, count: 64),
                  let device = deviceText, DeviceID.isValid(device),
                  let expires = obj["expiresAt"]?.stringValue, Timestamp.isValid(expires) else { throw .malformed }
            return EnvelopeResult(path: where_, ignoredRecords: [])
        case .changes(let device, let seq):
            guard deviceText == device.rawValue else { throw .deviceMismatch }
            guard let s = asInt(obj["seq"]) else { throw .malformed }
            guard s == seq else { throw .seqMismatch }
        case .snapshot(let hlc):
            guard deviceText == hlc.deviceId.rawValue else { throw .deviceMismatch }
            guard obj["hlc"]?.stringValue == hlc.string else { throw .hlcMismatch }
            guard case .object(let covers)? = obj["covers"],
                  covers.allSatisfy({ DeviceID.isValid($0.key) && (asInt($0.value).map { $0 >= 0 } ?? false) })
            else { throw .malformed }
        }
        guard case .array(let records)? = obj["records"] else { throw .malformed }
        var ignored: [Int] = []
        if case .changes(let device, _) = where_ {
            for (i, r) in records.enumerated() {
                guard let h = r["hlc"]?.stringValue, let hlc = try? HLC(h), hlc.deviceId == device else {
                    ignored.append(i)
                    continue
                }
            }
        }
        return EnvelopeResult(path: where_, ignoredRecords: ignored)
    }

    static func checkFormatVersion(_ obj: JSONObject) throws(SyncError) {
        guard let v = asInt(obj["formatVersion"]) else { throw .malformed }
        guard v == 1 else { throw .unsupportedFormat }
    }

    // MARK: vault.json

    /// A checked vault.json (section 7).
    public struct Vault: Sendable, Hashable {
        /// A canonical lowercase UUID.
        public let vaultId: String
        public let currentGeneration: Int64
        /// Generation IDs, sorted.
        public let generations: [Int64]
        /// Generation ID to key check value (lowercase hex).
        public let keyChecks: [Int64: String]
    }

    public static func checkVault(_ vault: JSONValue) throws(SyncError) -> Vault {
        guard case .object(let obj) = vault, obj["type"]?.stringValue == "futurecheck-vault" else { throw .malformed }
        try checkFormatVersion(obj)
        guard let vaultId = obj["vaultId"]?.stringValue, SyncIDs.isCanonicalUUID(vaultId) else { throw .malformed }
        guard case .array(let gens)? = obj["generations"], !gens.isEmpty else { throw .malformed }
        var ids: [Int64] = []
        var checks: [Int64: String] = [:]
        for g in gens {
            guard let gid = asInt(g["id"]), gid >= 1 else { throw .malformed }
            guard let kc = g["keyCheck"]?.stringValue, Hex.isLowercase(kc, count: 32) else { throw .malformed }
            ids.append(gid)
            if checks[gid] == nil { checks[gid] = kc }
        }
        guard Set(ids).count == ids.count, let current = asInt(obj["currentGeneration"]), ids.contains(current) else {
            throw .malformed
        }
        return Vault(vaultId: vaultId, currentGeneration: current, generations: ids.sorted(), keyChecks: checks)
    }

    /// Parses and checks vault.json text strictly.
    public static func checkVault(bytes: [UInt8]) throws(SyncError) -> Vault {
        try checkVault(parsePayload(bytes))
    }

    /// The generation whose key check value matches `vaultKey`, or `nil`.
    public static func matchGeneration(_ vault: Vault, vaultKey: [UInt8]) throws(SyncError) -> Int64? {
        let kc = Hex.encode(try FileCrypto.keyCheck(vaultKey: vaultKey))
        return vault.generations.first { vault.keyChecks[$0] == kc }
    }

    /// A new vault.json for generation 1 (section 7.1).
    public static func vaultDocument(vaultId: String, createdAt: String, createdBy: DeviceID, vaultKey: [UInt8]) throws(SyncError) -> JSONValue {
        let kc = Hex.encode(try FileCrypto.keyCheck(vaultKey: vaultKey))
        return .object(JSONObject([
            "type": .string("futurecheck-vault"),
            "formatVersion": .number(1),
            "vaultId": .string(vaultId),
            "createdAt": .string(createdAt),
            "createdBy": .string(createdBy.rawValue),
            "currentGeneration": .number(1),
            "generations": .array([.object(JSONObject([
                "id": .number(1), "createdAt": .string(createdAt), "keyCheck": .string(kc),
            ]))]),
        ]))
    }

    // MARK: Payloads

    /// A change file's plaintext (section 6.3), compact JSON.
    public static func changesPayload(vaultId: String, deviceId: DeviceID, seq: Int64, records: [ChangeRecord]) throws(JSONWriteError) -> [UInt8] {
        try JSONWriter.bytes(.object(JSONObject([
            "type": .string("changes"),
            "formatVersion": .number(1),
            "vaultId": .string(vaultId),
            "deviceId": .string(deviceId.rawValue),
            "seq": .number(seq),
            "records": .array(records.map(\.json)),
        ])))
    }

    /// A snapshot's plaintext (section 11.2). `covers` maps device IDs to sequence numbers.
    public static func snapshotPayload(vaultId: String, hlc: HLC, covers: [String: Int64], records: [ChangeRecord]) throws(JSONWriteError) -> [UInt8] {
        let coverMembers = covers.keys.sorted(by: StringOrder.utf8Less).map { JSONMember($0, .number(covers[$0]!)) }
        return try JSONWriter.bytes(.object(JSONObject([
            "type": .string("snapshot"),
            "formatVersion": .number(1),
            "vaultId": .string(vaultId),
            "deviceId": .string(hlc.deviceId.rawValue),
            "hlc": .string(hlc.string),
            "covers": .object(JSONObject(coverMembers)),
            "records": .array(records.map(\.json)),
        ])))
    }

    /// The records of an envelope-checked payload that may be merged: valid records, minus the
    /// ignored ones (other devices' records in a change file).
    public static func mergeableRecords(_ payload: JSONValue, checked: EnvelopeResult) -> [ChangeRecord] {
        guard case .array(let items)? = payload["records"] else { return [] }
        let ignored = Set(checked.ignoredRecords)
        return items.enumerated().compactMap { ignored.contains($0.offset) ? nil : ChangeRecord(json: $0.element) }
    }

    // MARK: Pairing QR

    /// `FCPAIR:1:<pairingId, 32 uppercase hex>:<secret, 64 uppercase hex>`.
    public static func pairingQr(pairingId: [UInt8], secret: [UInt8]) -> String {
        "FCPAIR:1:" + Hex.encode(pairingId, uppercase: true) + ":" + Hex.encode(secret, uppercase: true)
    }

    /// Parses a scanned code (section 9.1): trims leading and trailing characters of the recovery
    /// phrase's separator set only, folds ASCII `a`-`z` to upper case only, then the whole string
    /// must be `FCPAIR:1:<32 hex>:<64 hex>`; else `badQr`.
    public static func parsePairingQr(_ text: String) throws(SyncError) -> (pairingId: [UInt8], secret: [UInt8]) {
        var scalars = Array(text.unicodeScalars)
        while let f = scalars.first, RecoveryPhrase.separators.contains(f) { scalars.removeFirst() }
        while let l = scalars.last, RecoveryPhrase.separators.contains(l) { scalars.removeLast() }
        var folded = String.UnicodeScalarView()
        for s in scalars { folded.append(s.value >= 0x61 && s.value <= 0x7A ? Unicode.Scalar(s.value - 0x20)! : s) }
        let t = String(folded)
        let parts = t.split(separator: ":", omittingEmptySubsequences: false).map(String.init)
        guard parts.count == 4, parts[0] == "FCPAIR", parts[1] == "1",
              isUpperHex(parts[2], count: 32), isUpperHex(parts[3], count: 64),
              let id = Hex.decode(parts[2]), let secret = Hex.decode(parts[3]) else { throw .badQr }
        return (id, secret)
    }

    static func isUpperHex(_ s: String, count: Int) -> Bool {
        s.utf8.count == count && s.utf8.allSatisfy { ($0 >= 0x30 && $0 <= 0x39) || ($0 >= 0x41 && $0 <= 0x46) }
    }

    /// A pairing ID and secret from the system's cryptographically secure generator.
    public static func newPairing() -> (pairingId: [UInt8], secret: [UInt8]) {
        var g = SystemRandomNumberGenerator()
        return ((0..<16).map { _ in UInt8.random(in: .min ... .max, using: &g) },
                (0..<32).map { _ in UInt8.random(in: .min ... .max, using: &g) })
    }
}
