using PayDay.Sync.Clock;
using PayDay.Sync.Json;
using PayDay.Sync.Model;

namespace PayDay.Sync.Merge;

/// <summary>
/// The merged state of a vault on one device: per-field last-write-wins over an
/// <see cref="IRecordStore"/> (<c>spec/sync-protocol.md</c> section 4), liveness
/// (<c>data-model.md</c> section 1.4) and the derived values of <c>data-model.md</c> section 5.
/// Merging is commutative, associative and idempotent: the same records in any order, any number
/// of times, give the same state.
/// </summary>
public sealed partial class SyncState
{
    public SyncState(IRecordStore store)
    {
        Store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>A state over a new <see cref="InMemoryRecordStore"/>.</summary>
    public SyncState()
        : this(new InMemoryRecordStore())
    {
    }

    public IRecordStore Store { get; }

    /// <summary>
    /// The merge rule of section 4.2: the new record wins when its HLC is greater, or when the HLCs
    /// are identical and its value is greater by the value order.
    /// </summary>
    public static bool Wins(SyncValue newValue, Hlc newHlc, SyncValue oldValue, Hlc oldHlc)
    {
        var c = newHlc.CompareTo(oldHlc);
        if (c != 0) return c > 0;
        return SyncValue.CompareOrder(newValue, oldValue) > 0;
    }

    /// <summary>Merges one valid record. Returns true when it became the winning value.</summary>
    public bool Merge(ChangeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (Store.TryGet(record.Entity, record.Id, record.Field, out var cur)
            && !Wins(record.Value, record.Hlc, cur.Value, cur.Hlc))
        {
            return false;
        }
        Store.Set(record.Entity, record.Id, record.Field, new SyncCell(record.Value, record.Hlc));
        return true;
    }

    /// <summary>Merges a record as found in a payload; invalid records (section 4.1) are ignored.</summary>
    public bool Merge(JsonData record)
        => ChangeRecord.TryFromJson(record, out var r) && Merge(r!);

    public int MergeAll(IEnumerable<ChangeRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var changed = 0;
        foreach (var r in records)
        {
            if (Merge(r)) changed++;
        }
        return changed;
    }

    public bool TryGet(string entity, string id, string field, out SyncCell cell) => Store.TryGet(entity, id, field, out cell);

    /// <summary>The winning value, or null when the field has no record.</summary>
    public SyncValue? Value(string entity, string id, string field)
        => Store.TryGet(entity, id, field, out var cell) ? cell.Value : null;

    /// <summary>
    /// A known field read for display: its winning value, or the field's default when it has no
    /// record or the value has the wrong JSON type (<c>data-model.md</c> section 1.2).
    /// </summary>
    public SyncValue Read(string entity, string id, string field)
    {
        if (!SyncSchema.TryGetField(entity, field, out var spec))
        {
            return Value(entity, id, field) ?? SyncValue.Null;
        }
        return Value(entity, id, field) is { } v && spec.Accepts(v) ? v : spec.Default;
    }

    /// <summary>
    /// Live: the winning <c>deleted</c> value is <c>false</c>. A <c>setting</c> row is live when its
    /// <c>value</c> has a record. A row with records but no <c>deleted</c> record is pending, not live.
    /// </summary>
    public bool IsLive(string entity, string id)
    {
        if (entity == SyncEntities.Setting)
        {
            return Store.TryGet(entity, id, SyncEntities.ValueField, out _);
        }
        return Store.TryGet(entity, id, SyncEntities.DeletedField, out var cell) && cell.Value.IsFalse;
    }

    /// <summary>A row with field records but no <c>deleted</c> record: some edits arrived before its create.</summary>
    public bool IsPending(string entity, string id)
        => entity != SyncEntities.Setting
            && !Store.TryGet(entity, id, SyncEntities.DeletedField, out _)
            && Store.Fields(entity, id).Any();

    /// <summary>Every entity with records, sorted by UTF-8 bytes.</summary>
    public IReadOnlyList<string> Entities() => Sorted(Store.Entities());

    /// <summary>Every row ID of an entity (live, tombstoned, pending), sorted by UTF-8 bytes.</summary>
    public IReadOnlyList<string> Ids(string entity) => Sorted(Store.Ids(entity));

    public IReadOnlyList<string> LiveIds(string entity) => Ids(entity).Where(id => IsLive(entity, id)).ToArray();

    /// <summary>The winning value of every field of one row.</summary>
    public IReadOnlyDictionary<string, SyncValue> Row(string entity, string id)
        => Store.Fields(entity, id).ToDictionary(f => f.Key, f => f.Value.Value, StringComparer.Ordinal);

    /// <summary>
    /// The winning record of every (entity, id, field), sorted by entity, id and field. This is the
    /// content of a snapshot (section 11.2): tombstoned, pending and unknown rows included.
    /// </summary>
    public IReadOnlyList<ChangeRecord> AllRecords()
    {
        var list = new List<ChangeRecord>(Store.Count);
        foreach (var entity in Store.Entities())
        {
            foreach (var id in Store.Ids(entity))
            {
                foreach (var (field, cell) in Store.Fields(entity, id))
                {
                    list.Add(new ChangeRecord(entity, id, field, cell.Value, cell.Hlc));
                }
            }
        }
        list.Sort(ChangeRecord.CompareByKey);
        return list;
    }

    /// <summary>
    /// The greatest HLC stamped by <paramref name="deviceId"/> in the store, for the startup safety
    /// net of section 2.3.
    /// </summary>
    public Hlc? LatestHlcOf(string deviceId)
    {
        Hlc? best = null;
        foreach (var entity in Store.Entities())
        {
            foreach (var id in Store.Ids(entity))
            {
                foreach (var (_, cell) in Store.Fields(entity, id))
                {
                    if (cell.Hlc.DeviceId == deviceId && (best is null || cell.Hlc > best.Value)) best = cell.Hlc;
                }
            }
        }
        return best;
    }

    private static string[] Sorted(IEnumerable<string> items)
    {
        var arr = items.ToArray();
        Array.Sort(arr, Utf8Order.Instance);
        return arr;
    }
}
