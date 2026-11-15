import Foundation

/// A bill as stored in a backup file. String fields are optional because today's C# reader keeps
/// an explicit JSON `null` (it does not enforce nullability); `bill` gives the app's `Bill` with
/// defaults for anything missing.
public struct BackupBill: Sendable, Hashable {
    public var id: String? = ""
    public var name: String? = ""
    public var type: String? = ""
    public var payment: Double = 0
    public var remaining: Double = 0
    public var available: Double = 0
    public var creditLimit: Double = 0
    public var dueDay: Int = 1
    public var rate: String? = "Monthly"
    public var apr: Double = 0
    public var autoPay: Bool = false
    public var active: Bool = true
    public var yearlyDate: String? = nil
    public var notes: String? = nil
    public var createdAt: String? = nil
    public var updatedAt: String? = nil
    public var notionPageId: String? = nil

    public init() {}

    public init(_ b: Bill) {
        id = b.id; name = b.name; type = b.type; payment = b.payment; remaining = b.remaining
        available = b.available; creditLimit = b.creditLimit; dueDay = b.dueDay; rate = b.rate
        apr = b.apr; autoPay = b.autoPay; active = b.active; yearlyDate = b.yearlyDate
        notes = b.notes; createdAt = b.createdAt; updatedAt = b.updatedAt; notionPageId = b.notionPageId
    }

    public var bill: Bill {
        Bill(id: id ?? "", name: name ?? "", type: type ?? "", payment: payment, remaining: remaining,
             available: available, creditLimit: creditLimit, dueDay: dueDay, rate: rate ?? "Monthly",
             apr: apr, autoPay: autoPay, active: active, yearlyDate: yearlyDate, notes: notes,
             createdAt: createdAt, updatedAt: updatedAt, notionPageId: notionPageId)
    }
}

/// A payment row in a backup file (today's integer-ID `Payments` table).
public struct BackupPayment: Sendable, Hashable {
    public var id: Int64 = 0
    public var billId: String? = ""
    public var periodKey: String? = ""
    public var amountPaid: Double = 0
    public var paidAt: String? = nil
    public var notionPageId: String? = nil

    public init(id: Int64 = 0, billId: String? = "", periodKey: String? = "", amountPaid: Double = 0,
                paidAt: String? = nil, notionPageId: String? = nil) {
        self.id = id; self.billId = billId; self.periodKey = periodKey; self.amountPaid = amountPaid
        self.paidAt = paidAt; self.notionPageId = notionPageId
    }
}

/// A balance-snapshot row in a backup file.
public struct BackupSnapshot: Sendable, Hashable {
    public var id: Int64 = 0
    public var snapshotDate: String? = ""
    public var totalRemaining: Double = 0
    public var details: String? = nil
    public var notionPageId: String? = nil

    public init(id: Int64 = 0, snapshotDate: String? = "", totalRemaining: Double = 0,
                details: String? = nil, notionPageId: String? = nil) {
        self.id = id; self.snapshotDate = snapshotDate; self.totalRemaining = totalRemaining
        self.details = details; self.notionPageId = notionPageId
    }
}

/// A parsed backup file, normalized: every collection present, defaults filled in.
public struct BackupFile: Sendable, Hashable {
    public var formatVersion: Int
    /// Missing gives `""`; an explicit `null` stays `nil` (core README quirk 9).
    public var exportedAt: String?
    public var bills: [BackupBill]
    public var payments: [BackupPayment]
    public var snapshots: [BackupSnapshot]
    /// The settings table, in file order. Values are text or `null`.
    public var settings: [(key: String, value: String?)]

    public static func == (a: BackupFile, b: BackupFile) -> Bool {
        a.formatVersion == b.formatVersion && a.exportedAt == b.exportedAt && a.bills == b.bills
            && a.payments == b.payments && a.snapshots == b.snapshots
            && a.settings.map(\.key) == b.settings.map(\.key) && a.settings.map(\.value) == b.settings.map(\.value)
    }

    public func hash(into hasher: inout Hasher) {
        hasher.combine(formatVersion); hasher.combine(exportedAt); hasher.combine(bills)
    }

    /// The settings as a dictionary (the last value wins if a key repeats).
    public var settingsDictionary: [String: String?] {
        var d: [String: String?] = [:]
        for s in settings { d[s.key] = .some(s.value) }
        return d
    }
}

/// Why a backup file was rejected.
public enum BackupError: Error, Sendable, Equatable {
    case notJSON
    case rootNotObject
    case missingFormatVersion
    case unsupportedFormatVersion(Int)
    /// A known key holds a value of the wrong JSON type (path to it, for diagnostics).
    case wrongType(String)
}

/// The backup JSON format (spec/conformance/core/backup.json), as today's C# `BackupSerializer`:
/// System.Text.Json with camelCase names, nulls omitted from records (but written inside
/// `settings`), case-sensitive keys, unknown keys ignored, and legacy `cost`/`owed` bill keys
/// read as `payment`/`remaining`.
public enum BackupFormat {
    public static let currentFormatVersion = 1

    // MARK: Writing

    /// The backup file as a JSON value (write it with `JSONWriter.text(_:indented:)`).
    public static func document(
        bills: [Bill], payments: [BackupPayment], snapshots: [BackupSnapshot],
        settings: [(key: String, value: String?)], exportedAt: Date
    ) -> JSONValue {
        var root = JSONObject()
        root["formatVersion"] = .number(currentFormatVersion)
        root["exportedAt"] = .string(roundTripTimestamp(exportedAt))
        root["bills"] = .array(bills.map { billJSON(BackupBill($0)) })
        root["payments"] = .array(payments.map(paymentJSON))
        root["snapshots"] = .array(snapshots.map(snapshotJSON))
        // A null settings value is written as null.
        root["settings"] = .object(JSONObject(settings.map { JSONMember($0.key, $0.value.map(JSONValue.string) ?? .null) }))
        return .object(root)
    }

    /// The backup file as indented JSON text.
    public static func text(
        bills: [Bill], payments: [BackupPayment], snapshots: [BackupSnapshot],
        settings: [(key: String, value: String?)], exportedAt: Date
    ) throws(JSONWriteError) -> String {
        try JSONWriter.text(document(bills: bills, payments: payments, snapshots: snapshots,
                                     settings: settings, exportedAt: exportedAt), indented: true)
    }

    /// .NET `DateTime.ToString("o")` of a UTC time: `yyyy-MM-ddTHH:mm:ss.fffffffZ`.
    public static func roundTripTimestamp(_ date: Date) -> String {
        let t = date.timeIntervalSince1970
        let whole = t.rounded(.down)
        let ticks = min(9_999_999, max(0, Int(((t - whole) * 10_000_000).rounded(.down))))
        let secs = Int(whole)
        let dayShift = secs >= 0 ? secs / 86_400 : (secs - 86_399) / 86_400
        let rest = secs - dayShift * 86_400
        let day = CivilDate(dayNumber: dayShift) ?? CivilDate(year: 1, month: 1, day: 1)!
        return day.iso + "T" + CivilDate.pad(rest / 3600, 2) + ":" + CivilDate.pad(rest / 60 % 60, 2) + ":"
            + CivilDate.pad(rest % 60, 2) + "." + CivilDate.pad(ticks, 7) + "Z"
    }

    static func billJSON(_ b: BackupBill) -> JSONValue {
        var o = JSONObject()
        func put(_ k: String, _ s: String?) { if let s { o[k] = .string(s) } }
        put("id", b.id); put("name", b.name); put("type", b.type)
        o["payment"] = .number(b.payment); o["remaining"] = .number(b.remaining)
        o["available"] = .number(b.available); o["creditLimit"] = .number(b.creditLimit)
        o["dueDay"] = .number(b.dueDay); put("rate", b.rate); o["apr"] = .number(b.apr)
        o["autoPay"] = .bool(b.autoPay); o["active"] = .bool(b.active)
        put("yearlyDate", b.yearlyDate); put("notes", b.notes); put("createdAt", b.createdAt)
        put("updatedAt", b.updatedAt); put("notionPageId", b.notionPageId)
        return .object(o)
    }

    static func paymentJSON(_ p: BackupPayment) -> JSONValue {
        var o = JSONObject()
        o["id"] = .number(p.id)
        if let v = p.billId { o["billId"] = .string(v) }
        if let v = p.periodKey { o["periodKey"] = .string(v) }
        o["amountPaid"] = .number(p.amountPaid)
        if let v = p.paidAt { o["paidAt"] = .string(v) }
        if let v = p.notionPageId { o["notionPageId"] = .string(v) }
        return .object(o)
    }

    static func snapshotJSON(_ s: BackupSnapshot) -> JSONValue {
        var o = JSONObject()
        o["id"] = .number(s.id)
        if let v = s.snapshotDate { o["snapshotDate"] = .string(v) }
        o["totalRemaining"] = .number(s.totalRemaining)
        if let v = s.details { o["details"] = .string(v) }
        if let v = s.notionPageId { o["notionPageId"] = .string(v) }
        return .object(o)
    }

    // MARK: Reading

    public static func parse(text: String) throws(BackupError) -> BackupFile {
        try parse(bytes: Array(text.utf8))
    }

    /// Reads a backup file. Rejected: invalid JSON (comments and trailing commas included), a root
    /// that is not an object, `formatVersion` missing, below 1 or above 1, or a known key whose
    /// value has the wrong JSON type. A key that repeats keeps its last value; a bill holding both
    /// `cost` and `payment` keeps whichever comes later (core README quirk 9).
    public static func parse(bytes: [UInt8]) throws(BackupError) -> BackupFile {
        let root: JSONValue
        do { root = try JSONParser.parseLenient(bytes) } catch { throw .notJSON }
        guard case .object(var obj) = root else { throw .rootNotObject }
        obj = migrateLegacyBillKeys(obj)

        var formatVersion = 0
        var exportedAt: String? = ""
        var bills: [BackupBill]? = []
        var payments: [BackupPayment]? = []
        var snapshots: [BackupSnapshot]? = []
        var settings: [(key: String, value: String?)]? = []
        for m in obj {
            switch m.key {
            case "formatVersion": formatVersion = try int32(m.value, "formatVersion")
            case "exportedAt": exportedAt = try optionalString(m.value, "exportedAt")
            case "bills": bills = try list(m.value, "bills", readBill)
            case "payments": payments = try list(m.value, "payments", readPayment)
            case "snapshots": snapshots = try list(m.value, "snapshots", readSnapshot)
            case "settings": settings = try readSettings(m.value)
            default: continue
            }
        }
        guard formatVersion > 0 else { throw .missingFormatVersion }
        guard formatVersion <= currentFormatVersion else { throw .unsupportedFormatVersion(formatVersion) }
        return BackupFile(formatVersion: formatVersion, exportedAt: exportedAt, bills: bills ?? [],
                          payments: payments ?? [], snapshots: snapshots ?? [], settings: settings ?? [])
    }

    /// Pre-7c backups stored `cost` and `owed` in each bill. If any bill has either key, every
    /// bill's `cost`/`owed` keys are renamed in place to `payment`/`remaining`.
    static func migrateLegacyBillKeys(_ root: JSONObject) -> JSONObject {
        guard case .array(let bills)? = root["bills"],
              bills.contains(where: { $0.objectValue.map { $0.contains("cost") || $0.contains("owed") } ?? false })
        else { return root }
        var out = root
        for i in out.members.indices where out.members[i].key == "bills" {
            guard case .array(let items) = out.members[i].value else { continue }
            out.members[i].value = .array(items.map { item in
                guard case .object(var bill) = item else { return item }
                for j in bill.members.indices {
                    switch bill.members[j].key {
                    case "cost": bill.members[j].key = "payment"
                    case "owed": bill.members[j].key = "remaining"
                    default: break
                    }
                }
                return .object(bill)
            })
        }
        return out
    }

    static func readBill(_ v: JSONValue, _ path: String) throws(BackupError) -> BackupBill {
        guard case .object(let o) = v else { throw .wrongType(path) }
        var b = BackupBill()
        for m in o {
            let p = path + "." + m.key
            switch m.key {
            case "id": b.id = try optionalString(m.value, p)
            case "name": b.name = try optionalString(m.value, p)
            case "type": b.type = try optionalString(m.value, p)
            case "payment": b.payment = try double(m.value, p)
            case "remaining": b.remaining = try double(m.value, p)
            case "available": b.available = try double(m.value, p)
            case "creditLimit": b.creditLimit = try double(m.value, p)
            case "dueDay": b.dueDay = try int32(m.value, p)
            case "rate": b.rate = try optionalString(m.value, p)
            case "apr": b.apr = try double(m.value, p)
            case "autoPay": b.autoPay = try bool(m.value, p)
            case "active": b.active = try bool(m.value, p)
            case "yearlyDate": b.yearlyDate = try optionalString(m.value, p)
            case "notes": b.notes = try optionalString(m.value, p)
            case "createdAt": b.createdAt = try optionalString(m.value, p)
            case "updatedAt": b.updatedAt = try optionalString(m.value, p)
            case "notionPageId": b.notionPageId = try optionalString(m.value, p)
            default: continue
            }
        }
        return b
    }

    static func readPayment(_ v: JSONValue, _ path: String) throws(BackupError) -> BackupPayment {
        guard case .object(let o) = v else { throw .wrongType(path) }
        var p = BackupPayment()
        for m in o {
            let q = path + "." + m.key
            switch m.key {
            case "id": p.id = try int64(m.value, q)
            case "billId": p.billId = try optionalString(m.value, q)
            case "periodKey": p.periodKey = try optionalString(m.value, q)
            case "amountPaid": p.amountPaid = try double(m.value, q)
            case "paidAt": p.paidAt = try optionalString(m.value, q)
            case "notionPageId": p.notionPageId = try optionalString(m.value, q)
            default: continue
            }
        }
        return p
    }

    static func readSnapshot(_ v: JSONValue, _ path: String) throws(BackupError) -> BackupSnapshot {
        guard case .object(let o) = v else { throw .wrongType(path) }
        var s = BackupSnapshot()
        for m in o {
            let q = path + "." + m.key
            switch m.key {
            case "id": s.id = try int64(m.value, q)
            case "snapshotDate": s.snapshotDate = try optionalString(m.value, q)
            case "totalRemaining": s.totalRemaining = try double(m.value, q)
            case "details": s.details = try optionalString(m.value, q)
            case "notionPageId": s.notionPageId = try optionalString(m.value, q)
            default: continue
            }
        }
        return s
    }

    static func readSettings(_ v: JSONValue) throws(BackupError) -> [(key: String, value: String?)]? {
        switch v {
        case .null: return nil
        case .object(let o):
            var out: [(key: String, value: String?)] = []
            for m in o {
                let value = try optionalString(m.value, "settings." + m.key)
                if let i = out.firstIndex(where: { $0.key == m.key }) {
                    out[i].value = value // dictionary indexer: last value, first position
                } else {
                    out.append((m.key, value))
                }
            }
            return out
        default: throw .wrongType("settings")
        }
    }

    /// A list property: `null` leaves it unset (it becomes empty), an array reads each element.
    /// A `null` element is rejected (today's C# reader would crash on it later).
    static func list<T>(_ v: JSONValue, _ path: String, _ read: (JSONValue, String) throws(BackupError) -> T) throws(BackupError) -> [T]? {
        switch v {
        case .null: return nil
        case .array(let items):
            var out: [T] = []
            for (i, item) in items.enumerated() { out.append(try read(item, "\(path)[\(i)]")) }
            return out
        default: throw .wrongType(path)
        }
    }

    static func optionalString(_ v: JSONValue, _ path: String) throws(BackupError) -> String? {
        switch v {
        case .null: return nil
        case .string(let s): return s
        default: throw .wrongType(path)
        }
    }

    static func double(_ v: JSONValue, _ path: String) throws(BackupError) -> Double {
        guard case .number(let n) = v, n.value.isFinite else { throw .wrongType(path) }
        return n.value
    }

    static func bool(_ v: JSONValue, _ path: String) throws(BackupError) -> Bool {
        guard case .bool(let b) = v else { throw .wrongType(path) }
        return b
    }

    /// System.Text.Json reads an `int` only from plain integer digits within range.
    static func int32(_ v: JSONValue, _ path: String) throws(BackupError) -> Int {
        let n = try int64(v, path)
        guard n >= Int64(Int32.min), n <= Int64(Int32.max) else { throw .wrongType(path) }
        return Int(n)
    }

    static func int64(_ v: JSONValue, _ path: String) throws(BackupError) -> Int64 {
        guard case .number(let n) = v, n.isIntegerLiteral else { throw .wrongType(path) }
        if let literal = n.literal {
            guard let i = Int64(literal) else { throw .wrongType(path) }
            return i
        }
        guard abs(n.value) <= 9_007_199_254_740_991 else { throw .wrongType(path) }
        return Int64(n.value)
    }
}
