using PayDay.Sync.Clock;
using PayDay.Sync.Model;

namespace PayDay.Sync.Merge;

/// <summary>The winning (value, HLC) of one (entity, id, field).</summary>
public readonly record struct SyncCell(SyncValue Value, Hlc Hlc);

/// <summary>
/// Storage for the merged state: for every (entity, id, field), the winning value and its HLC
/// (<c>spec/data-model.md</c> section 7). It holds known and unknown entities and fields alike.
/// The store does no merging; <see cref="SyncState"/> decides what to write. A SQLite
/// implementation maps this onto a <c>SyncCells</c> table.
/// </summary>
public interface IRecordStore
{
    int Count { get; }

    bool TryGet(string entity, string id, string field, out SyncCell cell);

    /// <summary>Sets a cell unconditionally (the merge rule is applied by the caller).</summary>
    void Set(string entity, string id, string field, SyncCell cell);

    /// <summary>Every entity with at least one cell, in no particular order.</summary>
    IEnumerable<string> Entities();

    /// <summary>Every row ID of an entity with at least one cell, in no particular order.</summary>
    IEnumerable<string> Ids(string entity);

    /// <summary>Every cell of one row, in no particular order.</summary>
    IEnumerable<KeyValuePair<string, SyncCell>> Fields(string entity, string id);
}

/// <summary>An in-memory <see cref="IRecordStore"/>.</summary>
public sealed class InMemoryRecordStore : IRecordStore
{
    private readonly Dictionary<string, Dictionary<string, Dictionary<string, SyncCell>>> _cells = new(StringComparer.Ordinal);

    public int Count { get; private set; }

    public bool TryGet(string entity, string id, string field, out SyncCell cell)
    {
        cell = default;
        return _cells.TryGetValue(entity, out var rows)
            && rows.TryGetValue(id, out var fields)
            && fields.TryGetValue(field, out cell);
    }

    public void Set(string entity, string id, string field, SyncCell cell)
    {
        if (!_cells.TryGetValue(entity, out var rows))
        {
            rows = new Dictionary<string, Dictionary<string, SyncCell>>(StringComparer.Ordinal);
            _cells[entity] = rows;
        }
        if (!rows.TryGetValue(id, out var fields))
        {
            fields = new Dictionary<string, SyncCell>(StringComparer.Ordinal);
            rows[id] = fields;
        }
        if (!fields.ContainsKey(field)) Count++;
        fields[field] = cell;
    }

    public IEnumerable<string> Entities() => _cells.Keys;

    public IEnumerable<string> Ids(string entity)
        => _cells.TryGetValue(entity, out var rows) ? rows.Keys : Enumerable.Empty<string>();

    public IEnumerable<KeyValuePair<string, SyncCell>> Fields(string entity, string id)
        => _cells.TryGetValue(entity, out var rows) && rows.TryGetValue(id, out var fields)
            ? fields
            : Enumerable.Empty<KeyValuePair<string, SyncCell>>();

    /// <summary>Removes everything (used when a device joins a vault and replaces its data, section 12.2).</summary>
    public void Clear()
    {
        _cells.Clear();
        Count = 0;
    }
}
