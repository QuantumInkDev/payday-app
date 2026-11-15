/// Why a JSON text was rejected. Callers map any of these to their own error code (`malformed` in
/// the sync protocol, "rejected" for a backup file).
public enum JSONParseError: Error, Sendable, Equatable {
    case byteOrderMark
    case invalidUTF8
    case unexpectedEnd
    case unexpectedByte(offset: Int)
    case trailingData(offset: Int)
    case duplicateKey(String)
    case loneSurrogate
    case tooDeep
    /// A number that overflows binary64 (`1e400`, a 400-digit integer); rejected in strict mode.
    case numberOverflow(offset: Int)
}

/// An RFC 8259 JSON parser over UTF-8 bytes.
///
/// It is strict about everything the sync protocol (spec/sync-protocol.md section 6.3) requires:
/// no byte-order mark, valid UTF-8, exactly one value with only JSON whitespace around it, no
/// `NaN`/`Infinity` or other non-JSON tokens, no unpaired UTF-16 surrogate escapes, no raw control
/// characters in strings. Duplicate object keys are rejected unless `allowDuplicateKeys` is set
/// (the backup reader needs them, because today's C# reader keeps the last one).
///
/// Numbers parse to the nearest binary64 value. In strict mode a number that overflows binary64
/// (`1e400`) is an error (spec/sync-protocol.md section 6.3); in lenient mode it parses to an
/// infinity and the caller decides.
public struct JSONParser {
    public var allowDuplicateKeys: Bool
    public var rejectNumberOverflow: Bool
    public var maxDepth: Int

    public init(allowDuplicateKeys: Bool = false, rejectNumberOverflow: Bool = true, maxDepth: Int = 512) {
        self.allowDuplicateKeys = allowDuplicateKeys
        self.rejectNumberOverflow = rejectNumberOverflow
        self.maxDepth = maxDepth
    }

    /// Strict parse: duplicates and binary64 overflow rejected. The sync protocol's payload,
    /// vault.json and legacy `TypeColors` rules.
    public static func parseStrict(_ bytes: some Collection<UInt8>) throws(JSONParseError) -> JSONValue {
        try JSONParser().parse(bytes)
    }

    public static func parseStrict(_ text: String) throws(JSONParseError) -> JSONValue {
        try parseStrict(Array(text.utf8))
    }

    /// Parse that keeps duplicate keys (lookups see the last one) and lets numbers overflow to
    /// infinity.
    public static func parseLenient(_ bytes: some Collection<UInt8>) throws(JSONParseError) -> JSONValue {
        try JSONParser(allowDuplicateKeys: true, rejectNumberOverflow: false).parse(bytes)
    }

    public static func parseLenient(_ text: String) throws(JSONParseError) -> JSONValue {
        try parseLenient(Array(text.utf8))
    }

    public func parse(_ input: some Collection<UInt8>) throws(JSONParseError) -> JSONValue {
        let bytes = Array(input)
        if bytes.count >= 3, bytes[0] == 0xEF, bytes[1] == 0xBB, bytes[2] == 0xBF {
            throw .byteOrderMark
        }
        guard String(validating: bytes, as: UTF8.self) != nil else { throw .invalidUTF8 }
        var cursor = Cursor(bytes: bytes, allowDuplicateKeys: allowDuplicateKeys,
                            rejectNumberOverflow: rejectNumberOverflow, maxDepth: maxDepth)
        cursor.skipWhitespace()
        let value = try cursor.parseValue(depth: 0)
        cursor.skipWhitespace()
        if cursor.index != bytes.count { throw .trailingData(offset: cursor.index) }
        return value
    }
}

private struct Cursor {
    let bytes: [UInt8]
    let allowDuplicateKeys: Bool
    let rejectNumberOverflow: Bool
    let maxDepth: Int
    var index = 0

    init(bytes: [UInt8], allowDuplicateKeys: Bool, rejectNumberOverflow: Bool, maxDepth: Int) {
        self.bytes = bytes
        self.allowDuplicateKeys = allowDuplicateKeys
        self.rejectNumberOverflow = rejectNumberOverflow
        self.maxDepth = maxDepth
    }

    var atEnd: Bool { index >= bytes.count }

    mutating func skipWhitespace() {
        while index < bytes.count {
            switch bytes[index] {
            case 0x20, 0x09, 0x0A, 0x0D: index += 1
            default: return
            }
        }
    }

    mutating func peek() throws(JSONParseError) -> UInt8 {
        guard index < bytes.count else { throw .unexpectedEnd }
        return bytes[index]
    }

    mutating func expect(_ byte: UInt8) throws(JSONParseError) {
        guard try peek() == byte else { throw .unexpectedByte(offset: index) }
        index += 1
    }

    mutating func expectLiteral(_ text: String) throws(JSONParseError) {
        for b in text.utf8 { try expect(b) }
    }

    mutating func parseValue(depth: Int) throws(JSONParseError) -> JSONValue {
        guard depth < maxDepth else { throw .tooDeep }
        switch try peek() {
        case UInt8(ascii: "{"): return .object(try parseObject(depth: depth))
        case UInt8(ascii: "["): return .array(try parseArray(depth: depth))
        case UInt8(ascii: "\""): return .string(try parseString())
        case UInt8(ascii: "t"): try expectLiteral("true"); return .bool(true)
        case UInt8(ascii: "f"): try expectLiteral("false"); return .bool(false)
        case UInt8(ascii: "n"): try expectLiteral("null"); return .null
        case UInt8(ascii: "-"), UInt8(ascii: "0")...UInt8(ascii: "9"): return .number(try parseNumber())
        default: throw .unexpectedByte(offset: index)
        }
    }

    mutating func parseObject(depth: Int) throws(JSONParseError) -> JSONObject {
        try expect(UInt8(ascii: "{"))
        var members: [JSONMember] = []
        var seen = Set<String>()
        skipWhitespace()
        if try peek() == UInt8(ascii: "}") {
            index += 1
            return JSONObject(members)
        }
        while true {
            skipWhitespace()
            guard try peek() == UInt8(ascii: "\"") else { throw .unexpectedByte(offset: index) }
            let key = try parseString()
            if !allowDuplicateKeys {
                guard seen.insert(key).inserted else { throw .duplicateKey(key) }
            }
            skipWhitespace()
            try expect(UInt8(ascii: ":"))
            skipWhitespace()
            let value = try parseValue(depth: depth + 1)
            members.append(JSONMember(key, value))
            skipWhitespace()
            let b = try peek()
            index += 1
            if b == UInt8(ascii: ",") { continue }
            if b == UInt8(ascii: "}") { return JSONObject(members) }
            throw .unexpectedByte(offset: index - 1)
        }
    }

    mutating func parseArray(depth: Int) throws(JSONParseError) -> [JSONValue] {
        try expect(UInt8(ascii: "["))
        var items: [JSONValue] = []
        skipWhitespace()
        if try peek() == UInt8(ascii: "]") {
            index += 1
            return items
        }
        while true {
            skipWhitespace()
            items.append(try parseValue(depth: depth + 1))
            skipWhitespace()
            let b = try peek()
            index += 1
            if b == UInt8(ascii: ",") { continue }
            if b == UInt8(ascii: "]") { return items }
            throw .unexpectedByte(offset: index - 1)
        }
    }

    mutating func parseHex4() throws(JSONParseError) -> UInt16 {
        var v: UInt16 = 0
        for _ in 0..<4 {
            let b = try peek()
            let d: UInt8
            switch b {
            case UInt8(ascii: "0")...UInt8(ascii: "9"): d = b - UInt8(ascii: "0")
            case UInt8(ascii: "a")...UInt8(ascii: "f"): d = b - UInt8(ascii: "a") + 10
            case UInt8(ascii: "A")...UInt8(ascii: "F"): d = b - UInt8(ascii: "A") + 10
            default: throw .unexpectedByte(offset: index)
            }
            v = v << 4 | UInt16(d)
            index += 1
        }
        return v
    }

    mutating func parseString() throws(JSONParseError) -> String {
        try expect(UInt8(ascii: "\""))
        var out: [UInt8] = []
        while true {
            let b = try peek()
            index += 1
            switch b {
            case UInt8(ascii: "\""):
                // The input was validated as UTF-8 and escapes only add valid scalars.
                return String(decoding: out, as: UTF8.self)
            case UInt8(ascii: "\\"):
                let e = try peek()
                index += 1
                switch e {
                case UInt8(ascii: "\""): out.append(0x22)
                case UInt8(ascii: "\\"): out.append(0x5C)
                case UInt8(ascii: "/"): out.append(0x2F)
                case UInt8(ascii: "b"): out.append(0x08)
                case UInt8(ascii: "f"): out.append(0x0C)
                case UInt8(ascii: "n"): out.append(0x0A)
                case UInt8(ascii: "r"): out.append(0x0D)
                case UInt8(ascii: "t"): out.append(0x09)
                case UInt8(ascii: "u"):
                    let first = try parseHex4()
                    let scalarValue: UInt32
                    if UTF16.isLeadSurrogate(first) {
                        // Must be followed by \uDC00-\uDFFF.
                        guard index + 1 < bytes.count, bytes[index] == UInt8(ascii: "\\"),
                              bytes[index + 1] == UInt8(ascii: "u") else { throw .loneSurrogate }
                        index += 2
                        let second = try parseHex4()
                        guard UTF16.isTrailSurrogate(second) else { throw .loneSurrogate }
                        scalarValue = 0x10000 + (UInt32(first - 0xD800) << 10) + UInt32(second - 0xDC00)
                    } else if UTF16.isTrailSurrogate(first) {
                        throw .loneSurrogate
                    } else {
                        scalarValue = UInt32(first)
                    }
                    guard let scalar = Unicode.Scalar(scalarValue) else { throw .loneSurrogate }
                    out.append(contentsOf: String(Character(scalar)).utf8)
                default:
                    throw .unexpectedByte(offset: index - 1)
                }
            case 0x00..<0x20:
                throw .unexpectedByte(offset: index - 1)
            default:
                out.append(b)
            }
        }
    }

    mutating func parseNumber() throws(JSONParseError) -> JSONNumber {
        let start = index
        if try peek() == UInt8(ascii: "-") { index += 1 }
        // int = zero / ( digit1-9 *DIGIT )
        let first = try peek()
        if first == UInt8(ascii: "0") {
            index += 1
        } else if first >= UInt8(ascii: "1") && first <= UInt8(ascii: "9") {
            while !atEnd, isDigit(bytes[index]) { index += 1 }
        } else {
            throw .unexpectedByte(offset: index)
        }
        if !atEnd, bytes[index] == UInt8(ascii: ".") {
            index += 1
            guard !atEnd, isDigit(bytes[index]) else { throw atEnd ? .unexpectedEnd : .unexpectedByte(offset: index) }
            while !atEnd, isDigit(bytes[index]) { index += 1 }
        }
        if !atEnd, bytes[index] == UInt8(ascii: "e") || bytes[index] == UInt8(ascii: "E") {
            index += 1
            if !atEnd, bytes[index] == UInt8(ascii: "+") || bytes[index] == UInt8(ascii: "-") { index += 1 }
            guard !atEnd, isDigit(bytes[index]) else { throw atEnd ? .unexpectedEnd : .unexpectedByte(offset: index) }
            while !atEnd, isDigit(bytes[index]) { index += 1 }
        }
        let literal = String(decoding: bytes[start..<index], as: UTF8.self)
        // Swift's Double(String) is correctly rounded and gives ±infinity on overflow.
        guard let value = Double(literal) else { throw .unexpectedByte(offset: start) }
        if rejectNumberOverflow && !value.isFinite { throw .numberOverflow(offset: start) }
        return JSONNumber(value: value, literal: literal)
    }

    func isDigit(_ b: UInt8) -> Bool { b >= UInt8(ascii: "0") && b <= UInt8(ascii: "9") }
}
