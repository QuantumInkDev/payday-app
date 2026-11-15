/// A device ID: 8 random bytes as 16 lowercase hex characters (spec/sync-protocol.md section 1).
public struct DeviceID: Sendable, Hashable, Comparable, CustomStringConvertible {
    public let rawValue: String

    /// `nil` unless `text` matches `^[0-9a-f]{16}$`.
    public init?(_ text: String) {
        guard DeviceID.isValid(text) else { return nil }
        rawValue = text
    }

    public static func isValid(_ text: String) -> Bool { Hex.isLowercase(text, count: 16) }

    /// A new device ID from the system's cryptographically secure generator. Every create or join
    /// of a vault gets a new one; it is never reused.
    public static func random() -> DeviceID {
        var g = SystemRandomNumberGenerator()
        let bytes = (0..<8).map { _ in UInt8.random(in: .min ... .max, using: &g) }
        return DeviceID(Hex.encode(bytes))!
    }

    public var description: String { rawValue }
    public static func < (a: DeviceID, b: DeviceID) -> Bool { a.rawValue < b.rawValue }
}

/// A hybrid logical clock value (spec/sync-protocol.md section 2): `<wall>-<counter>-<deviceId>`,
/// 12 + 4 + 16 lowercase hex digits, always 34 ASCII characters. Comparison is `(wall, counter,
/// deviceId)`, which equals byte-wise comparison of the strings.
public struct HLC: Sendable, Hashable, Comparable, CustomStringConvertible {
    public static let maxWall: UInt64 = (1 << 48) - 1
    public static let maxCounter: UInt64 = 0xFFFF
    /// A merged record more than 10 minutes ahead of the local clock triggers a warning.
    public static let skewWarningMilliseconds: Int64 = 10 * 60 * 1000

    /// Unix time in milliseconds (UTC), 48 bits.
    public let wall: UInt64
    public let counter: UInt16
    public let deviceId: DeviceID
    /// The canonical string.
    public let string: String

    /// Encodes the parts; fails with `wallOutOfRange`, `counterOutOfRange` or `badDeviceId`.
    public init(wall: Int64, counter: Int64, deviceId: String) throws(SyncError) {
        guard wall >= 0, UInt64(wall) <= HLC.maxWall else { throw .wallOutOfRange }
        guard counter >= 0, UInt64(counter) <= HLC.maxCounter else { throw .counterOutOfRange }
        guard let device = DeviceID(deviceId) else { throw .badDeviceId }
        self.init(unchecked: UInt64(wall), UInt16(counter), device)
    }

    private init(unchecked wall: UInt64, _ counter: UInt16, _ device: DeviceID) {
        self.wall = wall
        self.counter = counter
        self.deviceId = device
        let w = String(wall, radix: 16), c = String(counter, radix: 16)
        string = String(repeating: "0", count: 12 - w.count) + w + "-"
            + String(repeating: "0", count: 4 - c.count) + c + "-" + device.rawValue
    }

    /// Decodes a canonical HLC string; anything else (uppercase included) is `malformed`.
    public init(_ text: String) throws(SyncError) {
        let b = Array(text.utf8)
        guard b.count == 34, b[12] == UInt8(ascii: "-"), b[17] == UInt8(ascii: "-") else { throw .malformed }
        let wallText = String(decoding: b[0..<12], as: UTF8.self)
        let counterText = String(decoding: b[13..<17], as: UTF8.self)
        let deviceText = String(decoding: b[18..<34], as: UTF8.self)
        guard Hex.isLowercase(wallText, count: 12), Hex.isLowercase(counterText, count: 4),
              let device = DeviceID(deviceText),
              let w = UInt64(wallText, radix: 16), let c = UInt16(counterText, radix: 16) else { throw .malformed }
        self.wall = w
        self.counter = c
        self.deviceId = device
        self.string = text
    }

    public static func isValid(_ text: String) -> Bool { (try? HLC(text)) != nil }

    public var description: String { string }

    public static func < (a: HLC, b: HLC) -> Bool {
        (a.wall, a.counter, a.deviceId.rawValue) < (b.wall, b.counter, b.deviceId.rawValue)
    }

    public static func == (a: HLC, b: HLC) -> Bool { a.string == b.string }
    public func hash(into hasher: inout Hasher) { hasher.combine(string) }

    /// -1, 0 or 1.
    public static func compare(_ a: HLC, _ b: HLC) -> Int { a < b ? -1 : (a == b ? 0 : 1) }

    /// A local event: the HLC to stamp it with, which is also the new clock state.
    public static func tick(last: HLC?, now: Int64, device: DeviceID) throws(SyncError) -> HLC {
        guard let last else { return try HLC(wall: now, counter: 0, deviceId: device.rawValue) }
        var w = max(now < 0 ? 0 : UInt64(now), last.wall)
        var c: UInt64
        if w == last.wall {
            c = UInt64(last.counter) + 1
            if c > maxCounter {
                w += 1
                c = 0
            }
        } else {
            c = 0
        }
        guard w <= maxWall else { throw .wallOutOfRange }
        return HLC(unchecked: w, UInt16(c), device)
    }

    /// The clock state after merging a record stamped `remote`. Never increments.
    public static func observe(last: HLC?, remote: HLC, device: DeviceID) -> HLC {
        guard let last else { return HLC(unchecked: remote.wall, remote.counter, device) }
        if (remote.wall, remote.counter) > (last.wall, last.counter) {
            return HLC(unchecked: remote.wall, remote.counter, device)
        }
        return HLC(unchecked: last.wall, last.counter, device)
    }

    /// True when `remote`'s wall time is more than 10 minutes ahead of `now` (local Unix ms).
    public static func skewWarning(remote: HLC, now: Int64) -> Bool {
        Int64(remote.wall) - now > skewWarningMilliseconds
    }
}

/// A device's clock state (spec/sync-protocol.md section 2.3). Persist `state` in the same local
/// transaction as the records stamped with it, and after `observe` in the same transaction as the
/// merge.
public struct HLCClock: Sendable, Hashable {
    public let device: DeviceID
    public private(set) var state: HLC?

    public init(device: DeviceID, state: HLC? = nil) {
        self.device = device
        self.state = state.map { HLC.observe(last: nil, remote: $0, device: device) }
    }

    /// One tick for one user action; every record the action writes carries the result.
    public mutating func tick(now: Int64) throws(SyncError) -> HLC {
        let next = try HLC.tick(last: state, now: now, device: device)
        state = next
        return next
    }

    public mutating func observe(_ remote: HLC) {
        state = HLC.observe(last: state, remote: remote, device: device)
    }
}
