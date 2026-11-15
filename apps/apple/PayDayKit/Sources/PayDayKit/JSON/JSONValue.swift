/// A JSON number: the binary64 value it parses to (round-to-nearest, as `JSON.parse` does) plus
/// the literal text it was written as, when it came from parsed text. The literal is needed only to
/// mirror readers that care how an integer was written (today's C# backup reader rejects `3.0`
/// for an `int` field). Equality compares the value only, so `3` equals `3.0`.
public struct JSONNumber: Sendable, Hashable, CustomStringConvertible {
    public let value: Double
    public let literal: String?

    public init(_ value: Double) {
        self.value = value
        self.literal = nil
    }

    init(value: Double, literal: String) {
        self.value = value
        self.literal = literal
    }

    /// True when the literal was plain integer digits (`-?[0-9]+`), with no fraction or exponent.
    /// A number built from a `Double` counts as integer-literal when it is integral.
    public var isIntegerLiteral: Bool {
        guard let literal else { return value.isFinite && value.rounded(.towardZero) == value }
        var bytes = Substring(literal).utf8[...]
        if bytes.first == UInt8(ascii: "-") { bytes = bytes.dropFirst() }
        return !bytes.isEmpty && bytes.allSatisfy { $0 >= UInt8(ascii: "0") && $0 <= UInt8(ascii: "9") }
    }

    public static func == (lhs: JSONNumber, rhs: JSONNumber) -> Bool { lhs.value == rhs.value }
    public func hash(into hasher: inout Hasher) { hasher.combine(value) }
    public var description: String { literal ?? JSONWriter.numberText(value) ?? "\(value)" }
}

/// One `key: value` member of a JSON object, in document order.
public struct JSONMember: Sendable, Hashable {
    public var key: String
    public var value: JSONValue

    public init(_ key: String, _ value: JSONValue) {
        self.key = key
        self.value = value
    }
}

/// A JSON object that keeps its members in document order, including duplicate keys when the
/// parser was told to allow them. Lookups by key return the **last** member with that key, which
/// is what last-wins readers (Python's `json`, .NET's serializer) see.
public struct JSONObject: Sendable, Hashable, Sequence {
    public var members: [JSONMember]

    public init(_ members: [JSONMember] = []) { self.members = members }

    public init(_ pairs: KeyValuePairs<String, JSONValue>) {
        self.members = pairs.map { JSONMember($0.key, $0.value) }
    }

    public subscript(key: String) -> JSONValue? {
        get { members.last(where: { $0.key == key })?.value }
        set {
            if let newValue {
                if let i = members.lastIndex(where: { $0.key == key }) {
                    members[i].value = newValue
                } else {
                    members.append(JSONMember(key, newValue))
                }
            } else {
                members.removeAll { $0.key == key }
            }
        }
    }

    public func contains(_ key: String) -> Bool { members.contains { $0.key == key } }
    public var keys: [String] { members.map(\.key) }
    public var count: Int { members.count }
    public func makeIterator() -> IndexingIterator<[JSONMember]> { members.makeIterator() }

    /// Equality as JSON objects: same key set, equal values, order ignored. (Objects with duplicate
    /// keys compare by their last member per key.)
    public static func == (lhs: JSONObject, rhs: JSONObject) -> Bool {
        let lk = Set(lhs.keys), rk = Set(rhs.keys)
        guard lk == rk else { return false }
        return lk.allSatisfy { lhs[$0] == rhs[$0] }
    }

    public func hash(into hasher: inout Hasher) { hasher.combine(Set(keys)) }
}

/// A parsed JSON value. Booleans and numbers are distinct (`true` is never `1`).
public enum JSONValue: Sendable, Hashable {
    case null
    case bool(Bool)
    case number(JSONNumber)
    case string(String)
    case array([JSONValue])
    case object(JSONObject)

    public static func number(_ value: Double) -> JSONValue { .number(JSONNumber(value)) }
    public static func number(_ value: Int) -> JSONValue { .number(JSONNumber(Double(value))) }
    public static func number(_ value: Int64) -> JSONValue { .number(JSONNumber(Double(value))) }

    public var isNull: Bool { if case .null = self { true } else { false } }

    public var stringValue: String? { if case .string(let s) = self { s } else { nil } }
    public var boolValue: Bool? { if case .bool(let b) = self { b } else { nil } }
    public var doubleValue: Double? { if case .number(let n) = self { n.value } else { nil } }
    public var numberValue: JSONNumber? { if case .number(let n) = self { n } else { nil } }
    public var arrayValue: [JSONValue]? { if case .array(let a) = self { a } else { nil } }
    public var objectValue: JSONObject? { if case .object(let o) = self { o } else { nil } }

    /// Member lookup on an object (last member wins); `nil` for a missing key or a non-object.
    public subscript(key: String) -> JSONValue? { objectValue?[key] }
}
