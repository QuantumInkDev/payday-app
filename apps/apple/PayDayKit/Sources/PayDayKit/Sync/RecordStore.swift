/// The winning value of one (entity, id, field) and the HLC of the record that set it.
public struct Cell: Sendable, Hashable {
    public let value: RecordValue
    public let hlc: HLC

    public init(value: RecordValue, hlc: HLC) {
        self.value = value
        self.hlc = hlc
    }
}

/// The merged state: for every (entity, id, field), one winning cell (spec/sync-protocol.md
/// section 4.2, spec/data-model.md section 7). Implementations only store and look up cells; the
/// merge rule, liveness, derived values and writer rules are protocol extensions so every store
/// behaves the same. Persistence (GRDB/SQLite) will be another conforming type.
public protocol RecordStore {
    /// The cell, or `nil` if no valid record for it was merged.
    func cell(entity: String, id: String, field: String) -> Cell?
    /// Replace (or create) a cell. Called only by `merge(_:)` once the record has won.
    mutating func setCell(entity: String, id: String, field: String, to cell: Cell)
    /// Every entity name that has at least one cell, in any order.
    func entityNames() -> [String]
    /// Every row ID of `entity` that has at least one cell, in any order.
    func rowIDs(entity: String) -> [String]
    /// Every (field, cell) of one row, in any order.
    func fields(entity: String, id: String) -> [(field: String, cell: Cell)]
}

extension RecordStore {
    /// Merges one record by per-field last write wins; `true` if it became the winner. Commutative,
    /// associative and idempotent.
    @discardableResult
    public mutating func merge(_ r: ChangeRecord) -> Bool {
        if let cur = cell(entity: r.entity, id: r.id, field: r.field), !r.wins(over: cur.value, at: cur.hlc) {
            return false
        }
        setCell(entity: r.entity, id: r.id, field: r.field, to: Cell(value: r.value, hlc: r.hlc))
        return true
    }

    public mutating func merge<S: Sequence<ChangeRecord>>(_ records: S) {
        for r in records { merge(r) }
    }

    /// Merges a record from a payload; invalid records are ignored (`false`).
    @discardableResult
    public mutating func merge(json: JSONValue) -> Bool {
        guard let r = ChangeRecord(json: json) else { return false }
        return merge(r)
    }

    public func value(entity: String, id: String, field: String) -> RecordValue? {
        cell(entity: entity, id: id, field: field)?.value
    }

    /// Row IDs of `entity`, sorted by UTF-8 bytes.
    public func sortedRowIDs(entity: String) -> [String] {
        rowIDs(entity: entity).sorted(by: StringOrder.utf8Less)
    }

    /// Entities, sorted by UTF-8 bytes.
    public func sortedEntityNames() -> [String] {
        entityNames().sorted(by: StringOrder.utf8Less)
    }

    /// Live: the winning `deleted` value is `false`. A row with fields but no `deleted` record is
    /// pending, not live. A `setting` row is live when its `value` has a record (even `null`).
    public func isLive(entity: String, id: String) -> Bool {
        if entity == Entity.setting { return cell(entity: entity, id: id, field: "value") != nil }
        return value(entity: entity, id: id, field: "deleted") == .bool(false)
    }

    /// Live row IDs, sorted by UTF-8 bytes.
    public func liveIDs(entity: String) -> [String] {
        sortedRowIDs(entity: entity).filter { isLive(entity: entity, id: $0) }
    }

    /// Every winning record, sorted by entity, id, field: the contents of a snapshot.
    public func allRecords() -> [ChangeRecord] {
        var out: [ChangeRecord] = []
        for e in entityNames() {
            for id in rowIDs(entity: e) {
                for f in fields(entity: e, id: id) {
                    out.append(ChangeRecord(unchecked: e, id, f.field, f.cell.value, f.cell.hlc))
                }
            }
        }
        return out.sorted(by: ChangeRecord.storageOrderLess)
    }

    /// The latest HLC carrying `device` (startup safety net, section 2.3).
    public func latestHLC(of device: DeviceID) -> HLC? {
        var best: HLC?
        for e in entityNames() {
            for id in rowIDs(entity: e) {
                for f in fields(entity: e, id: id) where f.cell.hlc.deviceId == device {
                    if best.map({ f.cell.hlc > $0 }) ?? true { best = f.cell.hlc }
                }
            }
        }
        return best
    }
}

/// An in-memory record store. Value type: copying it copies the state.
public struct InMemoryRecordStore: RecordStore, Sendable, Hashable {
    private var cells: [String: [String: [String: Cell]]] = [:]

    public init() {}

    public init<S: Sequence<ChangeRecord>>(merging records: S) {
        merge(records)
    }

    public func cell(entity: String, id: String, field: String) -> Cell? {
        cells[entity]?[id]?[field]
    }

    public mutating func setCell(entity: String, id: String, field: String, to cell: Cell) {
        cells[entity, default: [:]][id, default: [:]][field] = cell
    }

    public func entityNames() -> [String] { Array(cells.keys) }

    public func rowIDs(entity: String) -> [String] { cells[entity].map { Array($0.keys) } ?? [] }

    public func fields(entity: String, id: String) -> [(field: String, cell: Cell)] {
        (cells[entity]?[id] ?? [:]).map { ($0.key, $0.value) }
    }

    /// Number of cells.
    public var count: Int { cells.values.reduce(0) { $0 + $1.values.reduce(0) { $0 + $1.count } } }
}
