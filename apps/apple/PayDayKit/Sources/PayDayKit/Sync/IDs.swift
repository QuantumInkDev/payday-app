import CryptoKit

/// IDs (spec/sync-protocol.md section 3, spec/data-model.md section 2).
public enum SyncIDs {
    /// The UUIDv5 namespace (decided in the spec).
    public static let namespace = "def9d7a0-dc52-41e0-906a-f9767c7f894d"

    /// The cadences whose paychecks get the deterministic ID.
    public static let scheduledCadences: Set<String> = ["Weekly", "Bi-Weekly", "Semi-Monthly", "Monthly"]

    // MARK: Shapes

    /// Canonical: lowercase hex, `8-4-4-4-12`.
    public static func isCanonicalUUID(_ s: String) -> Bool { uuidShape(s, allowUppercase: false) }

    /// UUID-shaped in either case.
    public static func isUUIDShaped(_ s: String) -> Bool { uuidShape(s, allowUppercase: true) }

    /// Lowercases a UUID-shaped ID; `notAUuid` for anything else.
    public static func canonicalizeUUID(_ s: String) throws(SyncError) -> String {
        guard isUUIDShaped(s) else { throw .notAUuid }
        return asciiLowercased(s)
    }

    private static func uuidShape(_ s: String, allowUppercase: Bool) -> Bool {
        let b = Array(s.utf8)
        guard b.count == 36 else { return false }
        for (i, c) in b.enumerated() {
            if i == 8 || i == 13 || i == 18 || i == 23 {
                if c != UInt8(ascii: "-") { return false }
            } else {
                let lowerHex = (c >= 0x30 && c <= 0x39) || (c >= 0x61 && c <= 0x66)
                let upperHex = c >= 0x41 && c <= 0x46
                if !(lowerHex || (allowUppercase && upperHex)) { return false }
            }
        }
        return true
    }

    static func asciiLowercased(_ s: String) -> String {
        String(decoding: s.utf8.map { $0 >= 0x41 && $0 <= 0x5A ? $0 + 0x20 : $0 }, as: UTF8.self)
    }

    static func format(_ bytes: [UInt8]) -> String {
        let h = Hex.encode(bytes)
        let c = Array(h)
        return String(c[0..<8]) + "-" + String(c[8..<12]) + "-" + String(c[12..<16]) + "-"
            + String(c[16..<20]) + "-" + String(c[20..<32])
    }

    // MARK: UUIDv5 and UUIDv7

    /// RFC 9562 UUIDv5: SHA-1 of the namespace bytes then the UTF-8 name, first 16 bytes, version
    /// nibble 5, variant `10`.
    public static func uuid5(namespace: String = SyncIDs.namespace, name: String) -> String {
        let ns = Hex.decode(namespace.replacingOccurrencesOfDash()) ?? []
        var sha = Insecure.SHA1()
        sha.update(data: ns)
        sha.update(data: Array(name.utf8))
        var b = Array(sha.finalize().prefix(16))
        b[6] = (b[6] & 0x0F) | 0x50
        b[8] = (b[8] & 0x3F) | 0x80
        return format(b)
    }

    /// UUIDv7 from a 48-bit Unix-ms timestamp and 10 random bytes, in the spec's layout.
    public static func uuid7(unixMs: Int64, random: [UInt8]) throws(SyncError) -> String {
        guard unixMs >= 0, unixMs < (1 << 48), random.count == 10 else { throw .badInput }
        var b = [UInt8](repeating: 0, count: 16)
        for i in 0..<6 { b[i] = UInt8(truncatingIfNeeded: unixMs >> (8 * (5 - i))) }
        b[6] = 0x70 | (random[0] & 0x0F)
        b[7] = random[1]
        b[8] = 0x80 | (random[2] & 0x3F)
        for i in 0..<7 { b[9 + i] = random[3 + i] }
        return format(b)
    }

    /// A new random UUIDv7 (bills, extra payments, balance snapshots, income sources, Irregular
    /// paychecks, vault IDs), from the system's cryptographically secure generator.
    public static func newUUID7(unixMs: Int64) throws(SyncError) -> String {
        var g = SystemRandomNumberGenerator()
        return try uuid7(unixMs: unixMs, random: (0..<10).map { _ in UInt8.random(in: .min ... .max, using: &g) })
    }

    /// Layout of a UUID: whether it is canonical, its version, whether it has the RFC 9562
    /// variant, and (version 7) its timestamp.
    public struct Inspection: Sendable, Hashable {
        public let canonical: Bool
        public let version: Int?
        public let rfc9562Variant: Bool?
        public let unixMs: Int64?
    }

    public static func inspect(_ s: String) -> Inspection {
        guard isCanonicalUUID(s), let raw = Hex.decode(s.replacingOccurrencesOfDash()) else {
            return Inspection(canonical: false, version: nil, rfc9562Variant: nil, unixMs: nil)
        }
        let version = Int(raw[6] >> 4)
        var ms: Int64? = nil
        if version == 7 { ms = raw[0..<6].reduce(Int64(0)) { $0 << 8 | Int64($1) } }
        return Inspection(canonical: true, version: version, rfc9562Variant: raw[8] & 0xC0 == 0x80, unixMs: ms)
    }

    // MARK: Deterministic names

    public static func regularPaymentName(billId: String, periodKey: String) throws(SyncError) -> String {
        try requireUUID(billId); try requireDate(periodKey)
        return "payment/regular/\(billId)/\(periodKey)"
    }

    public static func setAsideName(billId: String, periodKey: String) throws(SyncError) -> String {
        try requireUUID(billId); try requireDate(periodKey)
        return "payment/setaside/\(billId)/\(periodKey)"
    }

    public static func paycheckName(incomeSourceId: String, payDate: String) throws(SyncError) -> String {
        try requireUUID(incomeSourceId); try requireDate(payDate)
        return "paycheck/\(incomeSourceId)/\(payDate)"
    }

    /// `legacy-bill/<oldId>`, the old ID exactly as stored. `notLegacy` for an empty or UUID-shaped ID.
    public static func legacyBillName(oldId: String) throws(SyncError) -> String {
        guard !oldId.isEmpty, !isUUIDShaped(oldId) else { throw .notLegacy }
        return "legacy-bill/\(oldId)"
    }

    /// Extra payments made by the migration. `paidAt` is the normalized timestamp or `""`.
    public static func legacyPaymentName(billId: String, periodKey: String, amountCents: Int64, paidAt: String, occurrence: Int64) throws(SyncError) -> String {
        try requireUUID(billId); try requireDate(periodKey)
        guard paidAt.isEmpty || Timestamp.isValid(paidAt) else { throw .badInput }
        guard occurrence >= 1 else { throw .badInput }
        return "legacy-payment/\(billId)/\(periodKey)/\(amountCents)/\(paidAt)/\(occurrence)"
    }

    public static func legacySnapshotName(snapshotDate: String, totalCents: Int64, occurrence: Int64) throws(SyncError) -> String {
        try requireDate(snapshotDate)
        guard occurrence >= 1 else { throw .badInput }
        return "legacy-snapshot/\(snapshotDate)/\(totalCents)/\(occurrence)"
    }

    // MARK: Deterministic IDs

    public static func regularPaymentId(billId: String, periodKey: String) throws(SyncError) -> String {
        uuid5(name: try regularPaymentName(billId: billId, periodKey: periodKey))
    }

    public static func setAsideId(billId: String, periodKey: String) throws(SyncError) -> String {
        uuid5(name: try setAsideName(billId: billId, periodKey: periodKey))
    }

    public static func paycheckId(incomeSourceId: String, payDate: String) throws(SyncError) -> String {
        uuid5(name: try paycheckName(incomeSourceId: incomeSourceId, payDate: payDate))
    }

    public static func legacyPaymentId(billId: String, periodKey: String, amountCents: Int64, paidAt: String, occurrence: Int64) throws(SyncError) -> String {
        uuid5(name: try legacyPaymentName(billId: billId, periodKey: periodKey, amountCents: amountCents, paidAt: paidAt, occurrence: occurrence))
    }

    public static func legacySnapshotId(snapshotDate: String, totalCents: Int64, occurrence: Int64) throws(SyncError) -> String {
        uuid5(name: try legacySnapshotName(snapshotDate: snapshotDate, totalCents: totalCents, occurrence: occurrence))
    }

    /// Bill ID migration (data-model.md 8.2): a UUID-shaped ID is lowercased; anything else maps
    /// to `UUIDv5(namespace, "legacy-bill/" + oldId)`.
    public static func migrateBillId(_ oldId: String) throws(SyncError) -> String {
        if isUUIDShaped(oldId) { return asciiLowercased(oldId) }
        return uuid5(name: try legacyBillName(oldId: oldId))
    }

    public enum PaycheckIDKind: String, Sendable { case uuidv5, uuidv7 }

    /// Scheduled cadences get the deterministic ID; `Irregular` or an unknown cadence gets UUIDv7.
    public static func paycheckIdKind(cadence: String) -> PaycheckIDKind {
        scheduledCadences.contains(cadence) ? .uuidv5 : .uuidv7
    }

    static func requireUUID(_ s: String) throws(SyncError) {
        guard isCanonicalUUID(s) else { throw .badInput }
    }

    static func requireDate(_ s: String) throws(SyncError) {
        guard CivilDate.isValid(s) else { throw .badInput }
    }
}

/// The `timestamp` type: `YYYY-MM-DDTHH:MM:SS.sssZ`, UTC, exactly three fraction digits.
public enum Timestamp {
    public static func isValid(_ s: String) -> Bool {
        let b = Array(s.utf8)
        guard b.count == 24 else { return false }
        let pattern = Array("dddd-dd-ddTdd:dd:dd.dddZ".utf8)
        for (c, p) in zip(b, pattern) {
            if p == UInt8(ascii: "d") {
                guard c >= 0x30, c <= 0x39 else { return false }
            } else if c != p {
                return false
            }
        }
        return true
    }
}

extension String {
    func replacingOccurrencesOfDash() -> String { String(decoding: utf8.filter { $0 != UInt8(ascii: "-") }, as: UTF8.self) }
}
