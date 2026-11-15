/// Where a Remaining edit came from (spec/sync-protocol.md section 5.3).
public enum RemainingSource: String, Sendable {
    /// The bill editor.
    case app
    /// The Notion connector's pull.
    case notion
}

/// The writer-side rules for one user action (spec/sync-protocol.md sections 4.4, 5.3 and 12.3).
/// Each takes the editing device's current merged state and the action's one `tick` HLC and
/// returns the records to write, sorted by entity, id, field. The caller applies them to its own
/// store with `merge` and queues them, in one local transaction with the new clock state.
public enum WriterRules {
    // MARK: Create / edit / delete

    /// A create: a record for every field (defaults included) plus `deleted: false`.
    public static func create(entity: String, id: String, fields: [(String, RecordValue)], hlc: HLC) throws(SyncError) -> [ChangeRecord] {
        var out: [ChangeRecord] = []
        for (f, v) in fields where f != "deleted" { out.append(try ChangeRecord(entity: entity, id: id, field: f, value: v, hlc: hlc)) }
        out.append(try ChangeRecord(entity: entity, id: id, field: "deleted", value: .bool(false), hlc: hlc))
        return out.sorted(by: ChangeRecord.storageOrderLess)
    }

    /// An edit: records only for fields whose value changed (money compared in cents when both
    /// values are numbers and the field is listed in `moneyFields`).
    public static func edit<S: RecordStore>(_ store: S, entity: String, id: String, fields: [(String, RecordValue)],
                                            moneyFields: Set<String> = [], hlc: HLC) throws(SyncError) -> [ChangeRecord] {
        var out: [ChangeRecord] = []
        for (f, v) in fields where f != "deleted" {
            let cur = store.value(entity: entity, id: id, field: f)
            if let cur, unchanged(cur, v, money: moneyFields.contains(f)) { continue }
            out.append(try ChangeRecord(entity: entity, id: id, field: f, value: v, hlc: hlc))
        }
        return out.sorted(by: ChangeRecord.storageOrderLess)
    }

    /// Delete (`true`) or undelete (`false`): one `deleted` record.
    public static func setDeleted(entity: String, id: String, deleted: Bool, hlc: HLC) throws(SyncError) -> [ChangeRecord] {
        [try ChangeRecord(entity: entity, id: id, field: "deleted", value: .bool(deleted), hlc: hlc)]
    }

    static func unchanged(_ a: RecordValue, _ b: RecordValue, money: Bool) -> Bool {
        if money, let x = a.numberValue, let y = b.numberValue { return Money.equal(x, y) }
        return a == b
    }

    // MARK: Payments

    /// The deterministic ID of a Regular or SetAside payment; `badInput` for other kinds.
    public static func paymentId(kind: PaymentKind, billId: String, periodKey: String) throws(SyncError) -> String {
        switch kind {
        case .regular: try SyncIDs.regularPaymentId(billId: billId, periodKey: periodKey)
        case .setAside: try SyncIDs.setAsideId(billId: billId, periodKey: periodKey)
        default: throw .badInput
        }
    }

    /// Mark paid (kind Regular or SetAside). On a payment already live on this device only
    /// `amountPaid` and `paidAt` are written, so the mark HLC never moves past a checkpoint that
    /// absorbed it. Otherwise (new, or re-marking after an unmark) all six mark fields.
    public static func markPaid<S: RecordStore>(_ store: S?, kind: PaymentKind = .regular, billId: String, periodKey: String,
                                                amount: Double, paidAt: RecordValue, hlc: HLC) throws(SyncError) -> [ChangeRecord] {
        let pid = try paymentId(kind: kind, billId: billId, periodKey: periodKey)
        let amountValue: Double
        do { amountValue = try Money.round2(amount) } catch { throw .badInput }
        func rec(_ f: String, _ v: RecordValue) -> ChangeRecord { ChangeRecord(unchecked: Entity.payment, pid, f, v, hlc) }
        if let store, store.isLive(entity: Entity.payment, id: pid) {
            return [rec("amountPaid", .number(amountValue)), rec("paidAt", paidAt)]
        }
        return [
            rec("amountPaid", .number(amountValue)), rec("billId", .string(billId)), rec("deleted", .bool(false)),
            rec("kind", .string(kind.rawValue)), rec("paidAt", paidAt), rec("periodKey", .string(periodKey)),
        ]
    }

    /// Unmark a Regular or SetAside payment.
    public static func unmarkPaid<S: RecordStore>(_ store: S, kind: PaymentKind = .regular, billId: String, periodKey: String, hlc: HLC) throws(SyncError) -> [ChangeRecord] {
        deletePayment(store, paymentId: try paymentId(kind: kind, billId: billId, periodKey: periodKey), hlc: hlc)
    }

    /// Delete (unmark) a payment of any kind. Nothing if it is not live. If it counts against
    /// Remaining, its bill is live and the checkpoint absorbed it, the checkpoint is raised by its
    /// amount from the **unclamped** Remaining, at the same HLC.
    public static func deletePayment<S: RecordStore>(_ store: S, paymentId pid: String, hlc: HLC) -> [ChangeRecord] {
        guard store.isLive(entity: Entity.payment, id: pid) else { return [] }
        var out = [ChangeRecord(unchecked: Entity.payment, pid, "deleted", .bool(true), hlc)]
        if store.paymentKind(pid).countsAgainstRemaining,
           let bill = store.value(entity: Entity.payment, id: pid, field: "billId")?.stringValue,
           store.isLive(entity: Entity.bill, id: bill), store.isAbsorbed(pid) {
            let raised = max(0, store.remainingUnclampedCents(bill: bill)
                + store.moneyCents(entity: Entity.payment, id: pid, field: "amountPaid"))
            out.append(ChangeRecord(unchecked: Entity.bill, bill, "remainingCheckpoint", .number(Money.value(cents: raised)), hlc))
        }
        return out.sorted(by: ChangeRecord.storageOrderLess)
    }

    /// Set a bill's Remaining (bill editor or Notion pull). Nothing when the bill is not live,
    /// when Notion echoes the value the connector last pushed, or when the value equals the
    /// computed Remaining: a spurious checkpoint would absorb another device's unmerged payment.
    /// `value` must be ≥ 0 (`badInput` otherwise).
    public static func setRemaining<S: RecordStore>(_ store: S, billId: String, value: Double, source: RemainingSource,
                                                    lastPushed: Double?, hlc: HLC) throws(SyncError) -> [ChangeRecord] {
        guard store.isLive(entity: Entity.bill, id: billId) else { return [] }
        let v: Int64
        do { v = try Money.cents(value) } catch { throw .badInput }
        guard v >= 0 else { throw .badInput }
        if source == .notion, let lastPushed, let lp = try? Money.cents(lastPushed), lp == v { return [] }
        if v == store.remainingCents(bill: billId) { return [] }
        return [ChangeRecord(unchecked: Entity.bill, billId, "remainingCheckpoint", .number(Money.value(cents: v)), hlc)]
    }

    // MARK: Restore

    /// Restore a backup with sync on (section 12.3): one HLC; every field of every backup row of
    /// the plan entities plus `deleted: false`; `deleted: true` for every known plan row not in
    /// the backup and not already tombstoned; every mapped plan setting; `null` for every known
    /// plan setting the mapping did not produce that is not already `null`. Never the Notion
    /// connector keys, `device` rows, unknown entities or unknown setting keys.
    ///
    /// `rows` holds the backup rows by entity and ID, IDs already migrated, each bill's Remaining
    /// already mapped to `remainingCheckpoint`; `settings` is the backup's settings object exactly
    /// as stored in the file.
    public static func restore<S: RecordStore>(_ store: S, rows: [String: [String: [(String, RecordValue)]]],
                                               settings: JSONObject, hlc: HLC) throws(SyncError) -> [ChangeRecord] {
        for entity in rows.keys where !Entity.restorable.contains(entity) { throw .badInput }
        let mapped = BackupSettingsMapping.map(settings)
        var out: [ChangeRecord] = []
        for entity in Entity.restorable {
            let backup = rows[entity] ?? [:]
            for (id, fields) in backup {
                for (f, v) in fields where f != "deleted" {
                    out.append(try ChangeRecord(entity: entity, id: id, field: f, value: v, hlc: hlc))
                }
                out.append(try ChangeRecord(entity: entity, id: id, field: "deleted", value: .bool(false), hlc: hlc))
            }
            for id in store.rowIDs(entity: entity) where backup[id] == nil {
                if store.value(entity: entity, id: id, field: "deleted") != .bool(true) {
                    out.append(ChangeRecord(unchecked: entity, id, "deleted", .bool(true), hlc))
                }
            }
        }
        for (key, v) in mapped {
            out.append(try ChangeRecord(entity: Entity.setting, id: key, field: "value", value: v, hlc: hlc))
        }
        for key in store.rowIDs(entity: Entity.setting) where mapped[key] == nil && SettingKey.isRestorable(key) {
            if let cur = store.value(entity: Entity.setting, id: key, field: "value"), !cur.isNull {
                out.append(ChangeRecord(unchecked: Entity.setting, key, "value", .null, hlc))
            }
        }
        return out.sorted(by: ChangeRecord.storageOrderLess)
    }
}

/// Maps a backup's (or today's settings table's) settings to synced setting values
/// (spec/data-model.md section 8.5).
public enum BackupSettingsMapping {
    /// Only restorable plan keys with a usable value are returned. Notion, per-device and unknown
    /// keys are dropped. An explicit `TypeColor/<type>` key overrides a `TypeColors` entry.
    public static func map(_ raw: JSONObject) -> [String: RecordValue] {
        var out: [String: RecordValue] = [:]
        if case .string(let text)? = raw["TypeColors"],
           case .object(let colors)? = try? JSONParser.parseLenient(text) {
            for m in colors where !m.key.isEmpty {
                let key = SettingKey.typeColor(m.key)
                if let v = mapOne(key, m.value) { out[key] = v }
            }
        }
        for m in raw {
            let key = m.key
            guard !SettingKey.notion.contains(key), SettingKey.isRestorable(key), key != SettingKey.typeColorPrefix else { continue }
            if let v = mapOne(key, raw[key] ?? .null) { out[key] = v }
        }
        return out
    }

    /// One value, or `nil` if it cannot be used.
    static func mapOne(_ key: String, _ raw: JSONValue) -> RecordValue? {
        if raw.isNull { return nil }
        switch key {
        case SettingKey.payAnchor:
            guard let s = raw.stringValue, CivilDate.isValid(s) else { return nil }
            return .string(s)
        case SettingKey.earlyStart:
            if let b = raw.boolValue { return .bool(b) }
            guard let s = raw.stringValue else { return nil }
            return .bool(s.lowercased() == "true")
        case SettingKey.safeToSpendBuffer:
            var d: Double?
            if let s = raw.stringValue, isDecimalText(s) { d = Double(s) }
            else if case .number(let n) = raw { d = n.value }
            guard let d, d.isFinite, let r = try? Money.round2(d) else { return nil }
            return .number(r)
        case SettingKey.reminderDaysBefore:
            if let s = raw.stringValue, !s.isEmpty, s.utf8.allSatisfy({ $0 >= 0x30 && $0 <= 0x39 }) {
                return Double(s).map(RecordValue.number)
            }
            guard let i = SyncFiles.asInt(raw), i >= 0 else { return nil }
            return .number(Double(i))
        default:
            guard key.hasPrefix(SettingKey.typeColorPrefix), let s = raw.stringValue, isHexColor(s) else { return nil }
            return .string(s.uppercased())
        }
    }

    /// `-?digits[.digits]`.
    static func isDecimalText(_ s: String) -> Bool {
        var b = s.utf8[...]
        if b.first == UInt8(ascii: "-") { b = b.dropFirst() }
        func digits(_ x: Substring.UTF8View.SubSequence) -> Bool { !x.isEmpty && x.allSatisfy { $0 >= 0x30 && $0 <= 0x39 } }
        let parts = b.split(separator: UInt8(ascii: "."), maxSplits: 1, omittingEmptySubsequences: false)
        switch parts.count {
        case 1: return digits(parts[0])
        case 2: return digits(parts[0]) && digits(parts[1])
        default: return false
        }
    }

    /// `#` plus 6 hex digits, either case.
    static func isHexColor(_ s: String) -> Bool {
        let b = Array(s.utf8)
        return b.count == 7 && b[0] == UInt8(ascii: "#") && b[1...].allSatisfy { Hex.nibble($0) != nil }
    }
}
