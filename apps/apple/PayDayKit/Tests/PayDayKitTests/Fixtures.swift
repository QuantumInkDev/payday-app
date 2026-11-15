import Foundation
import Testing
@testable import PayDayKit

/// One fixture case, as a Swift Testing argument. Reported by its fixture name.
struct FixtureCase: Sendable, CustomTestStringConvertible {
    let file: String
    let name: String
    let input: JSONValue
    let expected: JSONValue
    /// Set when the fixture file could not be loaded: the case fails instead of the file
    /// silently contributing no cases.
    let loadError: String?

    var testDescription: String { name }
}

struct FixtureFailure: Error, CustomStringConvertible {
    let description: String
    init(_ d: String) { description = d }
}

/// Loads fixtures from the repository's spec/conformance folder at test time (spec/README.md,
/// "Loading fixtures"): found by walking up from this source file. Nothing is copied.
enum Fixtures {
    static let conformanceDirectory: URL? = {
        var dir = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
        for _ in 0..<12 {
            let candidate = dir.appendingPathComponent("spec/conformance", isDirectory: true)
            if FileManager.default.fileExists(atPath: candidate.path) { return candidate }
            dir.deleteLastPathComponent()
        }
        return nil
    }()

    static var specDirectory: URL? { conformanceDirectory?.deletingLastPathComponent() }

    /// The fixture files each suite runs. A file on disk missing from this list fails
    /// `everyFixtureFileHasARunner`.
    static let coreFiles = ["pay-periods.json", "bill-assignment.json", "totals.json", "payoff.json", "backup.json"]
    static let syncFiles = ["hlc.json", "ids.json", "crypto.json", "recovery-phrase.json", "files.json", "merge.json", "compaction.json"]

    static func document(_ relativePath: String) throws -> JSONValue {
        guard let dir = conformanceDirectory else { throw FixtureFailure("spec/conformance not found above \(#filePath)") }
        let data = try Data(contentsOf: dir.appendingPathComponent(relativePath))
        // Python's json (which generated the sync fixtures) keeps the last of duplicate keys.
        return try JSONParser.parseLenient(Array(data))
    }

    /// Every case of `spec/conformance/<relativePath>`.
    static func cases(_ relativePath: String) -> [FixtureCase] {
        do {
            let doc = try document(relativePath)
            guard doc["version"] == .number(1), case .array(let items)? = doc["cases"], !items.isEmpty else {
                throw FixtureFailure("\(relativePath): not a version-1 fixture file with cases")
            }
            var names = Set<String>()
            return try items.map { item in
                guard let name = item["name"]?.stringValue, let input = item["input"], let expected = item["expected"] else {
                    throw FixtureFailure("\(relativePath): a case lacks name, input or expected")
                }
                guard names.insert(name).inserted else { throw FixtureFailure("\(relativePath): duplicate case name \(name)") }
                return FixtureCase(file: relativePath, name: name, input: input, expected: expected, loadError: nil)
            }
        } catch {
            return [FixtureCase(file: relativePath, name: "LOAD FAILURE \(relativePath)", input: .null, expected: .null,
                                loadError: "\(error)")]
        }
    }

    static func fileNames(in sub: String) -> [String] {
        guard let dir = conformanceDirectory?.appendingPathComponent(sub),
              let names = try? FileManager.default.contentsOfDirectory(atPath: dir.path) else { return [] }
        return names.filter { $0.hasSuffix(".json") }.sorted()
    }
}

// MARK: - Input helpers

extension JSONValue {
    func req(_ key: String) throws -> JSONValue {
        guard let v = self[key] else { throw FixtureFailure("input lacks \"\(key)\"") }
        return v
    }

    func string(_ key: String) throws -> String {
        guard let s = try req(key).stringValue else { throw FixtureFailure("\(key) is not a string") }
        return s
    }

    func optionalString(_ key: String) throws -> String? {
        switch self[key] {
        case nil, .null?: return nil
        case .string(let s)?: return s
        default: throw FixtureFailure("\(key) is not a string or null")
        }
    }

    func double(_ key: String) throws -> Double {
        guard let d = try req(key).doubleValue else { throw FixtureFailure("\(key) is not a number") }
        return d
    }

    func int64(_ key: String) throws -> Int64 {
        guard case .number(let n) = try req(key), n.value.rounded(.towardZero) == n.value,
              abs(n.value) < 9.2e18 else { throw FixtureFailure("\(key) is not an integer") }
        return Int64(n.value)
    }

    func int(_ key: String) throws -> Int { Int(try int64(key)) }

    func bool(_ key: String) throws -> Bool {
        guard let b = try req(key).boolValue else { throw FixtureFailure("\(key) is not a boolean") }
        return b
    }

    func array(_ key: String) throws -> [JSONValue] {
        guard let a = try req(key).arrayValue else { throw FixtureFailure("\(key) is not an array") }
        return a
    }

    func object(_ key: String) throws -> JSONObject {
        guard let o = try req(key).objectValue else { throw FixtureFailure("\(key) is not an object") }
        return o
    }

    func hexBytes(_ key: String) throws -> [UInt8] {
        guard let b = Hex.decode(try string(key)) else { throw FixtureFailure("\(key) is not hex") }
        return b
    }

    /// Fails on any key not listed, so a typo in a fixture cannot fall back to a default.
    func allowKeys(_ allowed: Set<String>, _ what: String = "input") throws {
        guard let o = objectValue else { throw FixtureFailure("\(what) is not an object") }
        for k in o.keys where !allowed.contains(k) { throw FixtureFailure("\(what) has unknown key \"\(k)\"") }
    }
}

// MARK: - Comparison

enum Compare {
    /// spec/conformance/core/README.md "Comparing results": same keys (null is not missing), same
    /// array order, exact strings/booleans/null, numbers equal after the spec's rounding at scale
    /// 10,000 for `...Fraction` properties and 100 otherwise. No tolerance.
    static func coreDifferences(expected: JSONValue, actual: JSONValue, path: String = "$", property: String? = nil) -> [String] {
        switch (expected, actual) {
        case let (.number(e), .number(a)):
            let scale = (property?.hasSuffix("Fraction") ?? false) ? 10_000.0 : 100.0
            let re = Money.roundScaled(e.value, scale: scale), ra = Money.roundScaled(a.value, scale: scale)
            return re != nil && re == ra ? [] : ["\(path): expected \(e.value), got \(a.value) (scale \(Int(scale)))"]
        case let (.object(e), .object(a)):
            var diffs: [String] = []
            for k in e.keys where !a.contains(k) { diffs.append("\(path).\(k): missing") }
            for k in a.keys where !e.contains(k) { diffs.append("\(path).\(k): unexpected key") }
            for k in e.keys where a.contains(k) {
                diffs += coreDifferences(expected: e[k]!, actual: a[k]!, path: "\(path).\(k)", property: k)
            }
            return diffs
        case let (.array(e), .array(a)):
            guard e.count == a.count else { return ["\(path): expected \(e.count) items, got \(a.count): \(text(actual))"] }
            return zip(e, a).enumerated().flatMap { i, p in
                coreDifferences(expected: p.0, actual: p.1, path: "\(path)[\(i)]", property: property)
            }
        case (.string, .string), (.bool, .bool), (.null, .null):
            return expected == actual ? [] : ["\(path): expected \(text(expected)), got \(text(actual))"]
        default:
            return ["\(path): expected \(text(expected)), got \(text(actual))"]
        }
    }

    /// The sync fixtures' equality (tools/spec/check_fixtures.py `same`): booleans never equal
    /// numbers, numbers compare by value, objects by key set, arrays in order.
    static func same(_ a: JSONValue, _ b: JSONValue) -> Bool {
        switch (a, b) {
        case let (.number(x), .number(y)): return x.value == y.value
        case let (.object(x), .object(y)):
            guard Set(x.keys) == Set(y.keys) else { return false }
            return x.keys.allSatisfy { same(x[$0]!, y[$0]!) }
        case let (.array(x), .array(y)): return x.count == y.count && zip(x, y).allSatisfy { same($0, $1) }
        case (.null, .null): return true
        case let (.bool(x), .bool(y)): return x == y
        case let (.string(x), .string(y)): return x == y
        default: return false
        }
    }

    static func text(_ v: JSONValue) -> String {
        let t = (try? JSONWriter.text(v)) ?? "?"
        return t.count > 400 ? String(t.prefix(400)) + "…" : t
    }
}

/// Fails the test with a readable message unless `got` equals `want` (sync `same` rules).
func expectSame(_ got: JSONValue, _ want: JSONValue, _ what: String, sourceLocation: SourceLocation = #_sourceLocation) {
    #expect(Compare.same(got, want), "\(what): got \(Compare.text(got)), expected \(Compare.text(want))", sourceLocation: sourceLocation)
}

/// Runs `body`, expecting either a result (compared by the caller) or the fixture's error code.
func expectResultOrError(_ expected: JSONValue, sourceLocation: SourceLocation = #_sourceLocation,
                         _ body: () throws(SyncError) -> JSONValue) {
    do {
        let got = try body()
        if let code = expected["error"]?.stringValue {
            Issue.record("expected error \(code), got \(Compare.text(got))", sourceLocation: sourceLocation)
        } else {
            expectSame(got, expected, "result", sourceLocation: sourceLocation)
        }
    } catch {
        if let code = expected["error"]?.stringValue {
            #expect(error.rawValue == code, "error code", sourceLocation: sourceLocation)
        } else {
            Issue.record("unexpected error \(error.rawValue)", sourceLocation: sourceLocation)
        }
    }
}

/// A small deterministic PRNG (SplitMix64) for seeded delivery orders.
struct SplitMix64: RandomNumberGenerator {
    var state: UInt64
    init(seed: UInt64) { state = seed }
    init(seedText: String) {
        // FNV-1a of the case name.
        var h: UInt64 = 0xcbf2_9ce4_8422_2325
        for b in seedText.utf8 { h = (h ^ UInt64(b)) &* 0x100_0000_01b3 }
        state = h
    }

    mutating func next() -> UInt64 {
        state &+= 0x9E37_79B9_7F4A_7C15
        var z = state
        z = (z ^ (z >> 30)) &* 0xBF58_476D_1CE4_E5B9
        z = (z ^ (z >> 27)) &* 0x94D0_49BB_1331_11EB
        return z ^ (z >> 31)
    }
}
