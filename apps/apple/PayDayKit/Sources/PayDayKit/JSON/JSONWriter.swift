/// Errors from writing JSON.
public enum JSONWriteError: Error, Sendable, Equatable {
    /// JSON has no NaN or infinity. Writers never produce one (money is checked before writing).
    case nonFiniteNumber
}

/// Writes JSON text. Members are written in the order the object holds them; nothing in the sync
/// protocol depends on key order or whitespace (spec/sync-protocol.md section 6.3).
public enum JSONWriter {
    /// Compact (no whitespace) or indented with two spaces.
    public static func text(_ value: JSONValue, indented: Bool = false) throws(JSONWriteError) -> String {
        var out = ""
        try write(value, into: &out, indent: indented ? 0 : nil)
        return out
    }

    public static func bytes(_ value: JSONValue, indented: Bool = false) throws(JSONWriteError) -> [UInt8] {
        Array(try text(value, indented: indented).utf8)
    }

    /// The text of a finite number: integral values within ±(2^53 − 1) as plain digits (`3`, never
    /// `3.0`), anything else in Swift's shortest round-trip form (`24.99`, `1e-05`), which is valid
    /// JSON. `nil` for NaN and infinities.
    public static func numberText(_ x: Double) -> String? {
        guard x.isFinite else { return nil }
        if x.rounded(.towardZero) == x, abs(x) <= 9_007_199_254_740_991 {
            return String(Int64(x))
        }
        return "\(x)"
    }

    private static func write(_ value: JSONValue, into out: inout String, indent: Int?) throws(JSONWriteError) {
        switch value {
        case .null: out += "null"
        case .bool(let b): out += b ? "true" : "false"
        case .number(let n):
            guard n.value.isFinite, let t = n.literal ?? numberText(n.value) else { throw .nonFiniteNumber }
            out += t // a parsed number is written back exactly as it was written
        case .string(let s): writeString(s, into: &out)
        case .array(let items):
            if items.isEmpty { out += "[]"; return }
            out += "["
            for (i, item) in items.enumerated() {
                if i > 0 { out += "," }
                newline(&out, indent.map { $0 + 1 })
                try write(item, into: &out, indent: indent.map { $0 + 1 })
            }
            newline(&out, indent)
            out += "]"
        case .object(let obj):
            if obj.members.isEmpty { out += "{}"; return }
            out += "{"
            for (i, m) in obj.members.enumerated() {
                if i > 0 { out += "," }
                newline(&out, indent.map { $0 + 1 })
                writeString(m.key, into: &out)
                out += indent == nil ? ":" : ": "
                try write(m.value, into: &out, indent: indent.map { $0 + 1 })
            }
            newline(&out, indent)
            out += "}"
        }
    }

    private static func newline(_ out: inout String, _ indent: Int?) {
        guard let indent else { return }
        out += "\n"
        out += String(repeating: "  ", count: indent)
    }

    private static func writeString(_ s: String, into out: inout String) {
        out += "\""
        for scalar in s.unicodeScalars {
            switch scalar {
            case "\"": out += "\\\""
            case "\\": out += "\\\\"
            case "\n": out += "\\n"
            case "\r": out += "\\r"
            case "\t": out += "\\t"
            case "\u{08}": out += "\\b"
            case "\u{0C}": out += "\\f"
            default:
                if scalar.value < 0x20 {
                    let hex = String(scalar.value, radix: 16)
                    out += "\\u" + String(repeating: "0", count: 4 - hex.count) + hex
                } else {
                    out.unicodeScalars.append(scalar)
                }
            }
        }
        out += "\""
    }
}
