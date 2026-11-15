/// A field value inside a change record: `null`, a boolean, a finite number or a string. Objects
/// and arrays are not allowed (spec/sync-protocol.md section 4.1).
public enum RecordValue: Sendable, Hashable, CustomStringConvertible {
    case null
    case bool(Bool)
    case number(Double)
    case string(String)

    /// `nil` for objects, arrays and non-finite numbers.
    public init?(json: JSONValue) {
        switch json {
        case .null: self = .null
        case .bool(let b): self = .bool(b)
        case .number(let n):
            guard n.value.isFinite else { return nil }
            self = .number(n.value)
        case .string(let s): self = .string(s)
        case .array, .object: return nil
        }
    }

    public var json: JSONValue {
        switch self {
        case .null: .null
        case .bool(let b): .bool(b)
        case .number(let d): .number(d)
        case .string(let s): .string(s)
        }
    }

    public var stringValue: String? { if case .string(let s) = self { s } else { nil } }
    public var boolValue: Bool? { if case .bool(let b) = self { b } else { nil } }
    public var numberValue: Double? { if case .number(let d) = self { d } else { nil } }
    public var isNull: Bool { if case .null = self { true } else { false } }

    public var description: String { (try? JSONWriter.text(json)) ?? "?" }

    /// The value order that breaks identical-HLC ties (section 4.2):
    /// `null < false < true < any number < any string`; numbers by value; strings by UTF-8 bytes.
    public static func valueOrderLess(_ a: RecordValue, _ b: RecordValue) -> Bool {
        func rank(_ v: RecordValue) -> Int {
            switch v {
            case .null: 0
            case .bool(let b): b ? 2 : 1
            case .number: 3
            case .string: 4
            }
        }
        let ra = rank(a), rb = rank(b)
        if ra != rb { return ra < rb }
        switch (a, b) {
        case let (.number(x), .number(y)): return x < y
        case let (.string(x), .string(y)): return StringOrder.utf8Less(x, y)
        default: return false
        }
    }
}

/// One change record: a new value for one field of one row, stamped with an HLC.
public struct ChangeRecord: Sendable, Hashable, CustomStringConvertible {
    public let entity: String
    public let id: String
    public let field: String
    public let value: RecordValue
    public let hlc: HLC

    /// Fails (`badInput`) on an empty entity, id or field: such a record would be invalid.
    public init(entity: String, id: String, field: String, value: RecordValue, hlc: HLC) throws(SyncError) {
        guard !entity.isEmpty, !id.isEmpty, !field.isEmpty else { throw .badInput }
        self.init(unchecked: entity, id, field, value, hlc)
    }

    init(unchecked entity: String, _ id: String, _ field: String, _ value: RecordValue, _ hlc: HLC) {
        self.entity = entity
        self.id = id
        self.field = field
        self.value = value
        self.hlc = hlc
    }

    /// Validates a record from a payload: `entity`, `id`, `field` non-empty strings, `value`
    /// present and a record value, `hlc` a valid HLC. Extra keys are ignored. `nil` means invalid
    /// (not stored, not observed).
    public init?(json: JSONValue) {
        guard case .object(let o) = json,
              let entity = o["entity"]?.stringValue, !entity.isEmpty,
              let id = o["id"]?.stringValue, !id.isEmpty,
              let field = o["field"]?.stringValue, !field.isEmpty,
              let rawValue = o["value"], let value = RecordValue(json: rawValue),
              let h = o["hlc"]?.stringValue, let hlc = try? HLC(h) else { return nil }
        self.init(unchecked: entity, id, field, value, hlc)
    }

    public var json: JSONValue {
        .object(JSONObject([
            "entity": .string(entity), "id": .string(id), "field": .string(field),
            "value": value.json, "hlc": .string(hlc.string),
        ]))
    }

    public var description: String { (try? JSONWriter.text(json)) ?? "?" }

    /// Sort order for emitted records: by entity, then id, then field (UTF-8 byte order).
    public static func storageOrderLess(_ a: ChangeRecord, _ b: ChangeRecord) -> Bool {
        if a.entity != b.entity { return StringOrder.utf8Less(a.entity, b.entity) }
        if a.id != b.id { return StringOrder.utf8Less(a.id, b.id) }
        return StringOrder.utf8Less(a.field, b.field)
    }

    /// Whether this record beats the current winner `(value, hlc)` of its cell (section 4.2).
    public func wins(over value: RecordValue, at hlc: HLC) -> Bool {
        if self.hlc != hlc { return self.hlc > hlc }
        return RecordValue.valueOrderLess(value, self.value)
    }
}
