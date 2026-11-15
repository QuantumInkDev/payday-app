import Foundation
import Testing
@testable import PayDayKit

/// Every case of spec/conformance/core/*.json, one parameterized test case per fixture case,
/// compared with the core README's rules (spec rounding to cents, or 4 decimals for `...Fraction`).
@Suite("Core conformance")
struct CoreConformanceTests {
    @Test("pay-periods.json", arguments: Fixtures.cases("core/pay-periods.json"))
    func payPeriods(_ c: FixtureCase) throws { try check(c, CoreRunner.payPeriods) }

    @Test("bill-assignment.json", arguments: Fixtures.cases("core/bill-assignment.json"))
    func billAssignment(_ c: FixtureCase) throws { try check(c, CoreRunner.billAssignment) }

    @Test("totals.json", arguments: Fixtures.cases("core/totals.json"))
    func totals(_ c: FixtureCase) throws { try check(c, CoreRunner.totals) }

    @Test("payoff.json", arguments: Fixtures.cases("core/payoff.json"))
    func payoff(_ c: FixtureCase) throws { try check(c, CoreRunner.payoff) }

    @Test("backup.json", arguments: Fixtures.cases("core/backup.json"))
    func backup(_ c: FixtureCase) throws { try check(c, CoreRunner.backup) }

    private func check(_ c: FixtureCase, _ run: (JSONValue) throws -> JSONValue) throws {
        if let e = c.loadError { Issue.record("fixture file failed to load: \(e)"); return }
        let actual = try run(c.input)
        let diffs = Compare.coreDifferences(expected: c.expected, actual: actual)
        #expect(diffs.isEmpty, "\(c.name):\n\(diffs.joined(separator: "\n"))")
    }
}

/// Runs one core case through PayDayKit and returns the result in the fixture's `expected` shape.
enum CoreRunner {
    static let billKeys: Set<String> = ["id", "name", "type", "payment", "remaining", "available", "creditLimit", "dueDay",
                                        "rate", "apr", "autoPay", "active", "yearlyDate", "notes", "createdAt",
                                        "updatedAt", "notionPageId"]

    // MARK: pay-periods.json

    static func payPeriods(_ input: JSONValue) throws -> JSONValue {
        try input.allowKeys(["anchor", "now"])
        let periods = PayPeriods.current(anchor: try date(input, "anchor"), now: try dateTime(input, "now"))
        return .object(JSONObject(["periods": .array(periods.map { p in
            .object(JSONObject([
                "label": p.label.map(JSONValue.string) ?? .null, "key": .string(p.key), "start": .string(p.start.iso),
                "end": .string(p.end.iso), "payday": .string(p.payday.iso), "isCurrent": .bool(p.isCurrent),
            ]))
        })]))
    }

    // MARK: bill-assignment.json

    static func billAssignment(_ input: JSONValue) throws -> JSONValue {
        switch try input.string("kind") {
        case "periods":
            try input.allowKeys(["kind", "anchor", "now", "bills"])
            let periods = PayPeriods.current(anchor: try date(input, "anchor"), now: try dateTime(input, "now"))
            let assigned = BillAssignment.assign(try bills(input), to: periods)
            return .object(JSONObject(["periods": .array(assigned.map { a in
                .object(JSONObject([
                    "label": a.period.label.map(JSONValue.string) ?? .null, "key": .string(a.period.key),
                    "start": .string(a.period.start.iso), "end": .string(a.period.end.iso),
                    "bills": .array(a.bills.map { pb in
                        .object(JSONObject(["id": .string(pb.bill.id), "dueDate": pb.dueDate.map { .string($0.iso) } ?? .null]))
                    }),
                ]))
            })]))
        case "monthlyDueDate":
            try input.allowKeys(["kind", "dueDay", "periodStart", "periodEnd"])
            let due = BillAssignment.monthlyDueDate(dueDay: try input.int("dueDay"), periodStart: try date(input, "periodStart"),
                                                    periodEnd: try date(input, "periodEnd"))
            return .object(JSONObject(["dueDate": due.map { .string($0.iso) } ?? .null]))
        case let k:
            throw FixtureFailure("bill-assignment: unknown kind \(k)")
        }
    }

    // MARK: totals.json

    static func totals(_ input: JSONValue) throws -> JSONValue {
        switch try input.string("kind") {
        case "payDayPage":
            try input.allowKeys(["kind", "anchor", "now", "bills", "payments"])
            let payments = try input.array("payments").map { p in
                try p.allowKeys(["billId", "periodKey", "amountPaid"], "payment")
                return PeriodPayment(billId: try p.string("billId"), periodKey: try p.string("periodKey"), amountPaid: try p.double("amountPaid"))
            }
            guard let s = PayDayPageSummary.compute(anchor: try date(input, "anchor"), now: try dateTime(input, "now"),
                                                    bills: try bills(input), payments: payments) else {
                throw FixtureFailure("no current period")
            }
            return .object(JSONObject([
                "periodKey": .string(s.periodKey), "totalDue": .number(s.totalDue), "totalPaid": .number(s.totalPaid),
                "remaining": .number(s.remaining), "progressFraction": .number(s.progressFraction),
                "isAllPaid": .bool(s.isAllPaid), "autoPayTotal": .number(s.autoPayTotal),
                "autoPayBills": ids(s.autoPayBills.map(\.bill.id)), "unpaidBills": ids(s.unpaidBills.map(\.bill.id)),
                "paidBills": .array(s.paidBills.map { .object(JSONObject(["id": .string($0.bill.bill.id), "amountPaid": .number($0.amountPaid)])) }),
            ]))
        case "dashboard":
            try input.allowKeys(["kind", "anchor", "now", "bills"])
            let d = DashboardSummary.compute(anchor: try date(input, "anchor"), now: try dateTime(input, "now"), bills: try bills(input))
            return .object(JSONObject([
                "totalMonthlyObligations": .number(d.totalMonthlyObligations), "totalRemaining": .number(d.totalRemaining),
                "hasCreditCards": .bool(d.hasCreditCards), "creditUtilizationPct": .number(d.creditUtilizationPct),
                "billsDueThisPeriod": .number(d.billsDueThisPeriod),
                "sections": .array(d.sections.map { s in
                    .object(JSONObject([
                        "label": .string(s.label), "key": .string(s.period.period.key), "manualTotal": .number(s.manualTotal),
                        "autoPayTotal": .number(s.autoPayTotal), "grandTotal": .number(s.grandTotal), "billCount": .number(s.billCount),
                        "manualBills": ids(s.manualBills.map(\.bill.id)), "autoPayBills": ids(s.autoPayBills.map(\.bill.id)),
                    ]))
                }),
            ]))
        case "billGroups":
            try input.allowKeys(["kind", "bills"])
            let all = try bills(input)
            let groups = BillGroups.compute(all)
            return .object(JSONObject([
                "totalBills": .number(all.count),
                "groups": .array(groups.map { g in
                    .object(JSONObject([
                        "type": .string(g.type), "count": .number(g.count), "totalPayment": .number(g.totalPayment),
                        "totalRemaining": .number(g.totalRemaining), "bills": ids(g.bills.map(\.id)),
                    ]))
                }),
            ]))
        case let k:
            throw FixtureFailure("totals: unknown kind \(k)")
        }
    }

    // MARK: payoff.json

    static func months(_ e: PayoffEstimate) -> JSONValue {
        switch e {
        case .months(let n): .number(n)
        case .never: .string("never")
        case .notApplicable: .null
        }
    }

    static func payoff(_ input: JSONValue) throws -> JSONValue {
        switch try input.string("kind") {
        case "estimate":
            try input.allowKeys(["kind", "remaining", "payment", "apr"])
            let e = Payoff.estimate(remaining: try input.double("remaining"), payment: try input.double("payment"), apr: try input.double("apr"))
            return .object(JSONObject(["months": months(e)]))
        case "tracker":
            try input.allowKeys(["kind", "bills"])
            let t = PayoffTracker(bills: try bills(input))
            return .object(JSONObject([
                "itemCount": .number(t.items.count), "totalRemaining": .number(t.totalRemaining),
                "items": .array(t.items.map { i in
                    .object(JSONObject([
                        "id": .string(i.bill.id), "months": months(i.estimate), "hasLimit": .bool(i.hasLimit),
                        "limitUsedFraction": .number(i.limitUsedFraction),
                    ]))
                }),
                "order": .object(JSONObject([
                    "payoffTime": ids(t.ordered(by: .payoffTime).map(\.bill.id)),
                    "snowball": ids(t.ordered(by: .snowball).map(\.bill.id)),
                    "avalanche": ids(t.ordered(by: .avalanche).map(\.bill.id)),
                ])),
            ]))
        case let k:
            throw FixtureFailure("payoff: unknown kind \(k)")
        }
    }

    // MARK: backup.json

    static func backup(_ input: JSONValue) throws -> JSONValue {
        switch try input.string("kind") {
        case "serialize":
            let text = try serializeDataset(input)
            return .object(JSONObject(["document": try JSONParser.parseStrict(Array(text.utf8))]))
        case "roundTrip":
            let text = try serializeDataset(input)
            return .object(JSONObject(["backup": normalized(try BackupFormat.parse(text: text))]))
        case "parse":
            let text: String
            if input["text"] != nil {
                try input.allowKeys(["kind", "text"])
                text = try input.string("text")
            } else {
                try input.allowKeys(["kind", "document"])
                text = try JSONWriter.text(try input.req("document"))
            }
            do {
                return .object(JSONObject(["backup": normalized(try BackupFormat.parse(text: text))]))
            } catch {
                return .object(JSONObject(["rejected": .bool(true)]))
            }
        case let k:
            throw FixtureFailure("backup: unknown kind \(k)")
        }
    }

    static func serializeDataset(_ input: JSONValue) throws -> String {
        try input.allowKeys(["kind", "exportedAtUtc", "bills", "payments", "snapshots", "settings"])
        let exported = try dateTime(input, "exportedAtUtc")
        let exportedAt = Date(timeIntervalSince1970: TimeInterval(exported.date.dayNumber * 86_400 + exported.secondsOfDay))
        let payments = try input.array("payments").map { p in
            try p.allowKeys(["id", "billId", "periodKey", "amountPaid", "paidAt", "notionPageId"], "payment")
            return BackupPayment(id: p["id"] == nil ? 0 : try p.int64("id"), billId: try p.optionalString("billId") ?? "",
                                 periodKey: try p.optionalString("periodKey") ?? "",
                                 amountPaid: p["amountPaid"] == nil ? 0 : try p.double("amountPaid"),
                                 paidAt: try p.optionalString("paidAt"), notionPageId: try p.optionalString("notionPageId"))
        }
        let snapshots = try input.array("snapshots").map { s in
            try s.allowKeys(["id", "snapshotDate", "totalRemaining", "details", "notionPageId"], "snapshot")
            return BackupSnapshot(id: s["id"] == nil ? 0 : try s.int64("id"), snapshotDate: try s.optionalString("snapshotDate") ?? "",
                                  totalRemaining: s["totalRemaining"] == nil ? 0 : try s.double("totalRemaining"),
                                  details: try s.optionalString("details"), notionPageId: try s.optionalString("notionPageId"))
        }
        let settings = try input.object("settings").members.map { m -> (key: String, value: String?) in
            switch m.value {
            case .null: return (m.key, nil)
            case .string(let s): return (m.key, s)
            default: throw FixtureFailure("settings values are strings or null")
            }
        }
        return try BackupFormat.text(bills: try bills(input), payments: payments, snapshots: snapshots,
                                     settings: settings, exportedAt: exportedAt)
    }

    static func normalized(_ b: BackupFile) -> JSONValue {
        func opt(_ s: String?) -> JSONValue { s.map(JSONValue.string) ?? .null }
        return .object(JSONObject([
            "formatVersion": .number(b.formatVersion), "exportedAt": opt(b.exportedAt),
            "bills": .array(b.bills.map { x in
                .object(JSONObject([
                    "id": opt(x.id), "name": opt(x.name), "type": opt(x.type), "payment": .number(x.payment),
                    "remaining": .number(x.remaining), "available": .number(x.available), "creditLimit": .number(x.creditLimit),
                    "dueDay": .number(x.dueDay), "rate": opt(x.rate), "apr": .number(x.apr), "autoPay": .bool(x.autoPay),
                    "active": .bool(x.active), "yearlyDate": opt(x.yearlyDate), "notes": opt(x.notes),
                    "createdAt": opt(x.createdAt), "updatedAt": opt(x.updatedAt), "notionPageId": opt(x.notionPageId),
                ]))
            }),
            "payments": .array(b.payments.map { p in
                .object(JSONObject([
                    "id": .number(p.id), "billId": opt(p.billId), "periodKey": opt(p.periodKey),
                    "amountPaid": .number(p.amountPaid), "paidAt": opt(p.paidAt), "notionPageId": opt(p.notionPageId),
                ]))
            }),
            "snapshots": .array(b.snapshots.map { s in
                .object(JSONObject([
                    "id": .number(s.id), "snapshotDate": opt(s.snapshotDate), "totalRemaining": .number(s.totalRemaining),
                    "details": opt(s.details), "notionPageId": opt(s.notionPageId),
                ]))
            }),
            "settings": .object(JSONObject(b.settings.map { JSONMember($0.key, opt($0.value)) })),
        ]))
    }

    // MARK: Shared readers

    /// `input.bills`; omitted fields take the defaults of a new C# `Bill`.
    static func bills(_ input: JSONValue) throws -> [Bill] {
        try input.array("bills").map { o in
            try o.allowKeys(billKeys, "bill")
            var b = Bill(id: try o.string("id"))
            if o["name"] != nil { b.name = try o.string("name") }
            if o["type"] != nil { b.type = try o.string("type") }
            if o["payment"] != nil { b.payment = try o.double("payment") }
            if o["remaining"] != nil { b.remaining = try o.double("remaining") }
            if o["available"] != nil { b.available = try o.double("available") }
            if o["creditLimit"] != nil { b.creditLimit = try o.double("creditLimit") }
            if o["dueDay"] != nil { b.dueDay = try o.int("dueDay") }
            if o["rate"] != nil { b.rate = try o.string("rate") }
            if o["apr"] != nil { b.apr = try o.double("apr") }
            if o["autoPay"] != nil { b.autoPay = try o.bool("autoPay") }
            if o["active"] != nil { b.active = try o.bool("active") }
            b.yearlyDate = try o.optionalString("yearlyDate")
            b.notes = try o.optionalString("notes")
            b.createdAt = try o.optionalString("createdAt")
            b.updatedAt = try o.optionalString("updatedAt")
            b.notionPageId = try o.optionalString("notionPageId")
            return b
        }
    }

    static func ids(_ list: [String]) -> JSONValue { .array(list.map(JSONValue.string)) }

    static func date(_ input: JSONValue, _ key: String) throws -> CivilDate {
        guard let d = CivilDate(iso: try input.string(key)) else { throw FixtureFailure("\(key) is not a date") }
        return d
    }

    static func dateTime(_ input: JSONValue, _ key: String) throws -> LocalDateTime {
        guard let d = LocalDateTime(iso: try input.string(key)) else { throw FixtureFailure("\(key) is not a local date-time") }
        return d
    }
}
