/// Entity names of sync format 1 (spec/data-model.md section 4).
public enum Entity {
    public static let bill = "bill"
    public static let payment = "payment"
    public static let balanceSnapshot = "balanceSnapshot"
    public static let incomeSource = "incomeSource"
    public static let paycheck = "paycheck"
    public static let device = "device"
    public static let setting = "setting"

    public static let known: Set<String> = [bill, payment, balanceSnapshot, incomeSource, paycheck, device, setting]
    /// The plan entities a backup restore writes and tombstones (sync-protocol.md section 12.3).
    public static let restorable = [bill, payment, balanceSnapshot, incomeSource, paycheck]
}

/// Typed reads of a row's fields with spec defaults: a missing field, or one of the wrong JSON
/// type, reads as its default (spec/data-model.md section 1.2).
public struct FieldReader: Sendable {
    let read: @Sendable (String) -> RecordValue?

    public init(_ read: @escaping @Sendable (String) -> RecordValue?) { self.read = read }

    public func string(_ f: String, _ def: String = "") -> String { read(f)?.stringValue ?? def }

    public func optionalString(_ f: String) -> String? { read(f)?.stringValue }

    public func bool(_ f: String, _ def: Bool) -> Bool { read(f)?.boolValue ?? def }

    public func number(_ f: String, _ def: Double = 0) -> Double { read(f)?.numberValue ?? def }

    /// Money: a finite number in range; otherwise the default.
    public func money(_ f: String, _ def: Double = 0) -> Double {
        guard let d = read(f)?.numberValue, (try? Money.cents(d)) != nil else { return def }
        return d
    }

    /// An `int` field: any number that is integral once parsed to binary64 (never a boolean).
    public func int(_ f: String, _ def: Int) -> Int { optionalInt(f) ?? def }

    public func optionalInt(_ f: String) -> Int? {
        guard let d = read(f)?.numberValue, d.rounded(.towardZero) == d, abs(d) <= 9_007_199_254_740_991 else { return nil }
        return Int(d)
    }

    /// A `date?` field: a valid `YYYY-MM-DD` string, else `nil`.
    public func optionalDate(_ f: String) -> CivilDate? { read(f)?.stringValue.flatMap(CivilDate.init(iso:)) }
}

extension RecordStore {
    /// A typed reader over one row.
    public func reader(entity: String, id: String) -> FieldReader {
        let fields = Dictionary(uniqueKeysWithValues: self.fields(entity: entity, id: id).map { ($0.field, $0.cell.value) })
        return FieldReader { fields[$0] }
    }
}

/// How a bill repeats; unknown values are kept and treated as `Once`.
public enum Rate: Sendable, Hashable {
    case known(BillRate)
    case unknown(String)

    public init(_ raw: String) { self = BillRate(rawValue: raw).map(Rate.known) ?? .unknown(raw) }
    public var rawValue: String { switch self { case .known(let r): r.rawValue; case .unknown(let s): s } }
}

/// `bill` (spec/data-model.md section 4.1).
public struct BillEntity: Sendable, Hashable, Identifiable {
    public static let fieldNames = ["name", "type", "payment", "remainingCheckpoint", "available", "creditLimit",
                                    "dueDay", "rate", "apr", "autoPay", "active", "yearlyDate", "notes",
                                    "targetPayoffDate", "notionPageId", "createdAt", "deleted"]
    public var id: String
    public var name = ""
    public var type = ""
    public var payment = 0.0
    public var remainingCheckpoint = 0.0
    public var available = 0.0
    public var creditLimit = 0.0
    public var dueDay = 1
    public var rate = BillRate.monthly.rawValue
    public var apr = 0.0
    public var autoPay = false
    public var active = true
    public var yearlyDate: String?
    public var notes: String?
    public var targetPayoffDate: String?
    public var notionPageId: String?
    public var createdAt: String?
    public var deleted = false

    public init(id: String) { self.id = id }

    public init(id: String, reading r: FieldReader) {
        self.id = id
        name = r.string("name"); type = r.string("type"); payment = r.money("payment")
        remainingCheckpoint = r.money("remainingCheckpoint"); available = r.money("available")
        creditLimit = r.money("creditLimit"); dueDay = r.int("dueDay", 1)
        rate = r.string("rate", BillRate.monthly.rawValue); apr = r.number("apr")
        autoPay = r.bool("autoPay", false); active = r.bool("active", true)
        yearlyDate = r.optionalString("yearlyDate"); notes = r.optionalString("notes")
        targetPayoffDate = r.optionalDate("targetPayoffDate")?.iso; notionPageId = r.optionalString("notionPageId")
        createdAt = r.optionalString("createdAt"); deleted = r.bool("deleted", false)
    }

    /// Every field (writers create a row with all of them plus `deleted: false`), money rounded.
    public func fieldValues() throws(MoneyError) -> [(String, RecordValue)] {
        [("name", .string(name)), ("type", .string(type)), ("payment", .number(try Money.round2(payment))),
         ("remainingCheckpoint", .number(try Money.round2(remainingCheckpoint))),
         ("available", .number(try Money.round2(available))), ("creditLimit", .number(try Money.round2(creditLimit))),
         ("dueDay", .number(Double(dueDay))), ("rate", .string(rate)), ("apr", .number(apr)),
         ("autoPay", .bool(autoPay)), ("active", .bool(active)), ("yearlyDate", optional(yearlyDate)),
         ("notes", optional(notes)), ("targetPayoffDate", optional(targetPayoffDate)),
         ("notionPageId", optional(notionPageId)), ("createdAt", optional(createdAt)), ("deleted", .bool(deleted))]
    }

    /// The core-rules view of this bill, with its computed Remaining.
    public func coreBill(remaining: Double) -> Bill {
        Bill(id: id, name: name, type: type, payment: payment, remaining: remaining, available: available,
             creditLimit: creditLimit, dueDay: dueDay, rate: rate, apr: apr, autoPay: autoPay, active: active,
             yearlyDate: yearlyDate, notes: notes, createdAt: createdAt, notionPageId: notionPageId)
    }
}

/// `payment.kind`.
public enum PaymentKind: Sendable, Hashable {
    case regular, extra, setAside
    /// Written by a newer version: kept, not counted, not shown.
    case unknown(String)

    /// A missing or wrong-type kind reads as the default `Regular`.
    public init(_ value: RecordValue?) {
        switch value?.stringValue {
        case nil, "Regular"?: self = .regular
        case "Extra"?: self = .extra
        case "SetAside"?: self = .setAside
        case let s?: self = .unknown(s)
        }
    }

    public var rawValue: String {
        switch self {
        case .regular: "Regular"
        case .extra: "Extra"
        case .setAside: "SetAside"
        case .unknown(let s): s
        }
    }

    /// Only Regular and Extra lower Remaining.
    public var countsAgainstRemaining: Bool { self == .regular || self == .extra }
}

/// `payment` (spec/data-model.md section 4.2).
public struct PaymentEntity: Sendable, Hashable, Identifiable {
    public var id: String
    public var billId = ""
    public var kind = PaymentKind.regular
    public var periodKey = ""
    public var amountPaid = 0.0
    public var paidAt: String?
    public var confirmationNumber: String?
    public var note: String?
    public var notionPageId: String?
    public var deleted = false

    public init(id: String, reading r: FieldReader) {
        self.id = id
        billId = r.string("billId"); kind = PaymentKind(r.read("kind")); periodKey = r.string("periodKey")
        amountPaid = r.money("amountPaid"); paidAt = r.optionalString("paidAt")
        confirmationNumber = r.optionalString("confirmationNumber"); note = r.optionalString("note")
        notionPageId = r.optionalString("notionPageId"); deleted = r.bool("deleted", false)
    }
}

/// `balanceSnapshot` (spec/data-model.md section 4.3).
public struct BalanceSnapshotEntity: Sendable, Hashable, Identifiable {
    public var id: String
    public var snapshotDate = ""
    public var totalRemaining = 0.0
    public var details: String?
    public var notionPageId: String?
    public var deleted = false

    public init(id: String, reading r: FieldReader) {
        self.id = id
        snapshotDate = r.string("snapshotDate"); totalRemaining = r.money("totalRemaining")
        details = r.optionalString("details"); notionPageId = r.optionalString("notionPageId")
        deleted = r.bool("deleted", false)
    }
}

/// `incomeSource.cadence`; unknown values are treated as `Irregular`.
public enum Cadence: String, Sendable, CaseIterable {
    case weekly = "Weekly", biWeekly = "Bi-Weekly", semiMonthly = "Semi-Monthly", monthly = "Monthly", irregular = "Irregular"
}

/// `incomeSource` (spec/data-model.md section 4.4).
public struct IncomeSourceEntity: Sendable, Hashable, Identifiable {
    public var id: String
    public var name = ""
    /// The stored string; `cadenceValue` maps unknown values to `Irregular`.
    public var cadence = Cadence.irregular.rawValue
    public var expectedAmount = 0.0
    public var anchorDate: String?
    public var payDay1: Int?
    public var payDay2: Int?
    public var active = true
    public var note: String?
    public var deleted = false

    public var cadenceValue: Cadence { Cadence(rawValue: cadence) ?? .irregular }

    public init(id: String, reading r: FieldReader) {
        self.id = id
        name = r.string("name"); cadence = r.string("cadence", Cadence.irregular.rawValue)
        expectedAmount = r.money("expectedAmount"); anchorDate = r.optionalDate("anchorDate")?.iso
        payDay1 = r.optionalInt("payDay1"); payDay2 = r.optionalInt("payDay2"); active = r.bool("active", true)
        note = r.optionalString("note"); deleted = r.bool("deleted", false)
    }
}

/// `paycheck` (spec/data-model.md section 4.5).
public struct PaycheckEntity: Sendable, Hashable, Identifiable {
    public var id: String
    public var incomeSourceId = ""
    public var payDate = ""
    public var amount = 0.0
    public var note: String?
    public var deleted = false

    public init(id: String, reading r: FieldReader) {
        self.id = id
        incomeSourceId = r.string("incomeSourceId"); payDate = r.string("payDate")
        amount = r.money("amount"); note = r.optionalString("note"); deleted = r.bool("deleted", false)
    }
}

/// `device` (spec/data-model.md section 4.6). The row ID is the device ID.
public struct DeviceEntity: Sendable, Hashable, Identifiable {
    public var id: String
    public var name = ""
    /// `windows`, `macos`, `ios`, `android`, `web`.
    public var platform = ""
    public var appVersion: String?
    public var createdAt: String?
    public var deleted = false

    public init(id: String, reading r: FieldReader) {
        self.id = id
        name = r.string("name"); platform = r.string("platform"); appVersion = r.optionalString("appVersion")
        createdAt = r.optionalString("createdAt"); deleted = r.bool("deleted", false)
    }
}

/// Synced setting keys (spec/data-model.md section 6.1). The row ID is the key; the field is `value`.
public enum SettingKey {
    public static let payAnchor = "PayAnchor"
    public static let earlyStart = "EarlyStart"
    public static let typeColorPrefix = "TypeColor/"
    public static let safeToSpendBuffer = "SafeToSpendBuffer"
    public static let reminderDaysBefore = "ReminderDaysBefore"
    public static let notionOwnerDeviceId = "NotionOwnerDeviceId"
    public static let notionBillsDb = "NotionBillsDb"
    public static let notionPaymentsDb = "NotionPaymentsDb"
    public static let notionSnapshotsDb = "NotionSnapshotsDb"

    /// Plan settings (besides `TypeColor/<type>`) that a restore writes or clears.
    public static let plan: Set<String> = [payAnchor, earlyStart, safeToSpendBuffer, reminderDaysBefore]
    /// Connector state: a restore never writes or clears these.
    public static let notion: Set<String> = [notionOwnerDeviceId, notionBillsDb, notionPaymentsDb, notionSnapshotsDb]

    public static func typeColor(_ type: String) -> String { typeColorPrefix + type }

    /// Whether a restore writes or clears this key.
    public static func isRestorable(_ key: String) -> Bool { plan.contains(key) || key.hasPrefix(typeColorPrefix) }
}

/// Typed reads of the synced settings, with their defaults.
extension RecordStore {
    func settingValue(_ key: String) -> RecordValue? { value(entity: Entity.setting, id: key, field: "value") }

    /// `PayAnchor`, defaulting to 2025-03-20 when absent, `null` or invalid.
    public var payAnchor: CivilDate {
        settingValue(SettingKey.payAnchor)?.stringValue.flatMap(CivilDate.init(iso:)) ?? PayPeriods.defaultAnchor
    }

    public var earlyStart: Bool { settingValue(SettingKey.earlyStart)?.boolValue ?? false }

    public var safeToSpendBuffer: Double {
        guard let d = settingValue(SettingKey.safeToSpendBuffer)?.numberValue, (try? Money.cents(d)) != nil else { return 0 }
        return d
    }

    public var reminderDaysBefore: Int {
        guard let d = settingValue(SettingKey.reminderDaysBefore)?.numberValue, d.rounded(.towardZero) == d,
              abs(d) <= 1e15 else { return 2 }
        return Int(d)
    }

    public var notionOwnerDeviceId: String? { settingValue(SettingKey.notionOwnerDeviceId)?.stringValue }

    /// The `#RRGGBB` override for a bill type, matched case-insensitively; if keys differ only in
    /// case, the one whose value record has the greater HLC wins. `nil` means the built-in colour.
    public func typeColor(for type: String) -> String? {
        let wanted = SettingKey.typeColor(type)
        var best: Cell?
        for key in rowIDs(entity: Entity.setting)
        where key.hasPrefix(SettingKey.typeColorPrefix) && StringOrder.ordinalIgnoreCaseCompare(key, wanted) == 0 {
            guard let c = cell(entity: Entity.setting, id: key, field: "value") else { continue }
            if best.map({ c.hlc > $0.hlc }) ?? true { best = c }
        }
        return best?.value.stringValue
    }

    public func bills() -> [BillEntity] {
        liveIDs(entity: Entity.bill).map { BillEntity(id: $0, reading: reader(entity: Entity.bill, id: $0)) }
    }

    public func payments() -> [PaymentEntity] {
        liveIDs(entity: Entity.payment).map { PaymentEntity(id: $0, reading: reader(entity: Entity.payment, id: $0)) }
    }

    public func balanceSnapshots() -> [BalanceSnapshotEntity] {
        liveIDs(entity: Entity.balanceSnapshot).map { BalanceSnapshotEntity(id: $0, reading: reader(entity: Entity.balanceSnapshot, id: $0)) }
    }

    public func incomeSources() -> [IncomeSourceEntity] {
        liveIDs(entity: Entity.incomeSource).map { IncomeSourceEntity(id: $0, reading: reader(entity: Entity.incomeSource, id: $0)) }
    }

    public func paychecks() -> [PaycheckEntity] {
        liveIDs(entity: Entity.paycheck).map { PaycheckEntity(id: $0, reading: reader(entity: Entity.paycheck, id: $0)) }
    }

    public func devices() -> [DeviceEntity] {
        liveIDs(entity: Entity.device).map { DeviceEntity(id: $0, reading: reader(entity: Entity.device, id: $0)) }
    }

    /// Live bills in the core-rules shape, each with its computed Remaining.
    public func coreBills() -> [Bill] {
        bills().map { $0.coreBill(remaining: Money.value(cents: remainingCents(bill: $0.id))) }
    }
}

private func optional(_ s: String?) -> RecordValue { s.map(RecordValue.string) ?? .null }
