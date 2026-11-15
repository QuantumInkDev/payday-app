import Foundation
import Testing
@testable import PayDayKit

/// merge.json: the expected state must come out of every delivery order of the records (plus the
/// emitted records, for cases with an edit). Like tools/spec/check_fixtures.py: every permutation
/// for up to 8 records, otherwise the given order, the reverse order and 2,000 seeded shuffles;
/// every 50th order is also delivered with a third of its records duplicated.
enum MergeRunner {
    static let allPermutationsMax = 8
    static let samplePermutations = 2000

    struct CellKey: Hashable {
        let entity: String, id: String, field: String
    }

    /// The expected state, pre-digested for fast comparison.
    struct Expected {
        let cells: [CellKey: JSONValue]
        let live: [String: [String]]
        let remaining: [String: Int64]
        let setAside: [String: Int64]

        init(_ e: JSONValue) throws {
            var cells: [CellKey: JSONValue] = [:]
            for ent in try e.object("rows") {
                guard case .object(let rows) = ent.value else { throw FixtureFailure("rows.\(ent.key)") }
                for row in rows {
                    guard case .object(let fields) = row.value else { throw FixtureFailure("rows.\(ent.key).\(row.key)") }
                    for f in fields { cells[CellKey(entity: ent.key, id: row.key, field: f.key)] = f.value }
                }
            }
            self.cells = cells
            var live: [String: [String]] = [:]
            for m in try e.object("live") {
                live[m.key] = try (m.value.arrayValue ?? []).map { v in
                    guard let s = v.stringValue else { throw FixtureFailure("live") }
                    return s
                }
            }
            self.live = live
            func centsMap(_ key: String) throws -> [String: Int64] {
                var out: [String: Int64] = [:]
                for m in try e.object(key) {
                    guard let d = m.value.doubleValue else { throw FixtureFailure(key) }
                    out[m.key] = try Money.cents(d)
                }
                return out
            }
            remaining = try centsMap("remaining")
            setAside = try centsMap("setAsideBalance")
        }

        /// `nil` when `store` matches, else the first difference.
        func difference(_ store: InMemoryRecordStore) -> String? {
            if store.count != cells.count { return "rows: \(store.count) cells, expected \(cells.count)" }
            for (k, want) in cells {
                guard let got = store.value(entity: k.entity, id: k.id, field: k.field) else {
                    return "rows: missing \(k.entity)/\(k.id)/\(k.field)"
                }
                if !Compare.same(got.json, want) {
                    return "rows: \(k.entity)/\(k.id)/\(k.field) = \(got), expected \(Compare.text(want))"
                }
            }
            let entities = store.entityNames()
            if Set(entities) != Set(live.keys) { return "live: entities \(entities.sorted()) vs \(live.keys.sorted())" }
            for e in entities where store.liveIDs(entity: e) != live[e] {
                return "live[\(e)]: \(store.liveIDs(entity: e)) vs \(live[e] ?? [])"
            }
            let bills = store.liveIDs(entity: Entity.bill)
            if Set(bills) != Set(remaining.keys) { return "remaining: bills \(bills) vs \(remaining.keys.sorted())" }
            if Set(bills) != Set(setAside.keys) { return "setAsideBalance: bills \(bills) vs \(setAside.keys.sorted())" }
            for b in bills {
                let r = store.remainingCents(bill: b), s = store.setAsideBalanceCents(bill: b)
                if r != remaining[b] { return "remaining[\(b)]: \(r) cents, expected \(remaining[b] ?? -1)" }
                if s != setAside[b] { return "setAsideBalance[\(b)]: \(s) cents, expected \(setAside[b] ?? -1)" }
            }
            return nil
        }
    }

    static func check(_ c: FixtureCase) throws {
        let i = c.input, e = c.expected
        guard try i.string("fn") == "merge" else { throw FixtureFailure("fn must be merge") }
        let records = try i.array("records")
        var finalRecords = records

        if let edit = i["edit"] {
            let seen = try edit.array("seen").map { v -> Int in
                guard let n = SyncFiles.asInt(v), n >= 0, Int(n) < records.count else { throw FixtureFailure("seen") }
                return Int(n)
            }
            var store = InMemoryRecordStore()
            for k in seen { store.merge(json: records[k]) }
            let emitted = try runOp(store, try edit.req("op"))
            let wantEmitted = try e.array("emitted")
            expectSame(.array(emitted.map(\.json)), .array(wantEmitted), "emitted records")
            let seenMax = seen.compactMap { ChangeRecord(json: records[$0])?.hlc }.max()
            for r in emitted {
                #expect(ChangeRecord(json: r.json) != nil, "emitted record must be valid")
                if let seenMax { #expect(r.hlc > seenMax, "edit HLC must be later than everything the device has seen") }
            }
            finalRecords += wantEmitted
        } else {
            #expect(e["emitted"] == nil, "emitted without an edit")
        }

        let want = try Expected(e)
        // Parse once; invalid records are ignored by the merge in every order.
        let parsed = finalRecords.map { ChangeRecord(json: $0) }
        var rng = SplitMix64(seedText: c.name)
        var count = 0
        var failure: String?

        func run(_ order: [Int]) {
            guard failure == nil else { return }
            var store = InMemoryRecordStore()
            for k in order { if let r = parsed[k] { store.merge(r) } }
            if let d = want.difference(store) {
                failure = "order \(order.prefix(16)): \(d)"
                return
            }
            count += 1
            if count % 50 == 1 {
                var dup = order
                let extra = max(1, order.count / 3)
                var pool = Array(order.indices)
                pool.shuffle(using: &rng)
                dup += pool.prefix(extra).map { order[$0] }
                dup.shuffle(using: &rng)
                var again = InMemoryRecordStore()
                for k in dup { if let r = parsed[k] { again.merge(r) } }
                if let d = want.difference(again) { failure = "order with duplicated records \(dup.prefix(16)): \(d)" }
            }
        }

        let n = finalRecords.count
        if n <= allPermutationsMax {
            var a = Array(0..<n)
            heapPermutations(&a, a.count) { run($0) }
            #expect(failure != nil || count == factorial(n), "every permutation ran")
        } else {
            run(Array(0..<n))
            run(Array((0..<n).reversed()))
            for _ in 0..<samplePermutations {
                var p = Array(0..<n)
                p.shuffle(using: &rng)
                run(p)
            }
        }
        #expect(failure == nil, "\(failure ?? "")")
    }

    static func factorial(_ n: Int) -> Int { n <= 1 ? 1 : n * factorial(n - 1) }

    /// Heap's algorithm: calls `body` with every permutation of `a`.
    static func heapPermutations(_ a: inout [Int], _ k: Int, _ body: ([Int]) -> Void) {
        if k <= 1 { body(a); return }
        heapPermutations(&a, k - 1, body)
        for i in 0..<(k - 1) {
            if k % 2 == 0 { a.swapAt(i, k - 1) } else { a.swapAt(0, k - 1) }
            heapPermutations(&a, k - 1, body)
        }
    }

    /// Runs `input.edit.op` against the editing device's state.
    static func runOp(_ store: InMemoryRecordStore, _ op: JSONValue) throws -> [ChangeRecord] {
        let hlc = try HLC(try op.string("hlc"))
        func kind() throws -> PaymentKind { PaymentKind(.string(try op.optionalString("kind") ?? "Regular")) }
        switch try op.string("op") {
        case "markPaid":
            guard let paidAt = RecordValue(json: op["paidAt"] ?? .null) else { throw FixtureFailure("paidAt") }
            return try WriterRules.markPaid(store, kind: try kind(), billId: try op.string("billId"), periodKey: try op.string("periodKey"),
                                            amount: try op.double("amount"), paidAt: paidAt, hlc: hlc)
        case "unmarkPaid":
            return try WriterRules.unmarkPaid(store, kind: try kind(), billId: try op.string("billId"), periodKey: try op.string("periodKey"), hlc: hlc)
        case "deletePayment":
            return WriterRules.deletePayment(store, paymentId: try op.string("paymentId"), hlc: hlc)
        case "setRemaining":
            guard let source = RemainingSource(rawValue: try op.string("source")) else { throw FixtureFailure("source") }
            let lastPushed = op["lastPushed"]?.doubleValue
            return try WriterRules.setRemaining(store, billId: try op.string("billId"), value: try op.double("value"),
                                                source: source, lastPushed: lastPushed, hlc: hlc)
        case "restore":
            var rows: [String: [String: [(String, RecordValue)]]] = [:]
            for ent in (op["rows"]?.objectValue ?? JSONObject()) {
                guard case .object(let byId) = ent.value else { throw FixtureFailure("rows") }
                for row in byId {
                    guard case .object(let fields) = row.value else { throw FixtureFailure("rows") }
                    rows[ent.key, default: [:]][row.key] = try fields.members.map { f in
                        guard let v = RecordValue(json: f.value) else { throw FixtureFailure("row value") }
                        return (f.key, v)
                    }
                }
            }
            return try WriterRules.restore(store, rows: rows, settings: op["settings"]?.objectValue ?? JSONObject(), hlc: hlc)
        case let o:
            throw FixtureFailure("unknown op \(o)")
        }
    }
}
