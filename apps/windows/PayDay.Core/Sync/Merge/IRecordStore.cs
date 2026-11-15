using PayDay.Sync.Clock;
using PayDay.Sync.Model;

namespace PayDay.Sync.Merge;

/// <summary>The winning (value, HLC) of one (entity, id, field).</summary>
public readonly record struct SyncCell(SyncValue Value, Hlc Hlc);

/// <summary>One row: (entity, id).</summary>
public readonly record struct RowKey(string Entity, string Id);

/// <summary>
/// Storage for the merged state: for every (entity, id, field), the winning value and its HLC
/// (<c>spec/data-model.md</c> section 7). It holds known and unknown entities and fields alike.
/// The store does no merging; <see cref="SyncState"/> decides what to write. A SQLite
/// implementation maps this onto a <c>SyncCells</c> table with an index on
/// (<c>Entity</c>, <c>Field</c>, <c>Value</c>) for <see cref="IdsWhere"/>.
/// <para>
/// Not thread-safe: <see cref="Engine.SyncReplica"/> serializes every access, and every write
/// happens inside <see cref="Engine.ISyncStorage.RunInTransaction"/>.
/// </para>
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

    /// <summary>
    /// The IDs of the rows of <paramref name="entity"/> whose winning <paramref name="field"/> value
    /// equals <paramref name="value"/> (numbers by value, booleans never equal to numbers), in no
    /// particular order. Used for "payments of a bill" and similar lookups.
    /// </summary>
    IEnumerable<string> IdsWhere(string entity, string field, SyncValue value);

    /// <summary>Removes every cell (a device joining a vault replaces its data, <c>sync-protocol.md</c> 12.2 step 3).</summary>
    void Clear();
}

/// <summary>
/// An in-memory <see cref="IRecordStore"/> with a value index on every (entity, field), and an undo
/// journal that <see cref="Engine.InMemorySyncStorage"/> uses to roll back a failed transaction.
/// </summary>
public sealed class InMemoryRecordStore : IRecordStore
{
    private readonly Dictionary<string, Dictionary<string, Dictionary<string, SyncCell>>> _cells = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Entity, string Field), Dictionary<SyncValue, HashSet<string>>> _index = new();
    private List<Action>? _undo;

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
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(field);
        if (_undo is not null)
        {
            if (TryGet(entity, id, field, out var old))
            {
                _undo.Add(() => SetCore(entity, id, field, old));
            }
            else
            {
                _undo.Add(() => RemoveCore(entity, id, field));
            }
        }
        SetCore(entity, id, field, cell);
    }

    public IEnumerable<string> Entities() => _cells.Keys;

    public IEnumerable<string> Ids(string entity)
        => _cells.TryGetValue(entity, out var rows) ? rows.Keys : Enumerable.Empty<string>();

    public IEnumerable<KeyValuePair<string, SyncCell>> Fields(string entity, string id)
        => _cells.TryGetValue(entity, out var rows) && rows.TryGetValue(id, out var fields)
            ? fields
            : Enumerable.Empty<KeyValuePair<string, SyncCell>>();

    public IEnumerable<string> IdsWhere(string entity, string field, SyncValue value)
        => _index.TryGetValue((entity, field), out var byValue) && byValue.TryGetValue(value, out var ids)
            ? ids
            : Enumerable.Empty<string>();

    public void Clear()
    {
        if (_undo is not null)
        {
            var saved = AllCells().ToList();
            _undo.Add(() =>
            {
                ClearCore();
                foreach (var (e, i, f, c) in saved) SetCore(e, i, f, c);
            });
        }
        ClearCore();
    }

    /// <summary>Starts recording undo steps (one transaction at a time).</summary>
    internal void BeginJournal()
    {
        if (_undo is not null) throw new InvalidOperationException("A journal is already open.");
        _undo = new List<Action>();
    }

    internal void CommitJournal() => _undo = null;

    internal void RollbackJournal()
    {
        var steps = _undo;
        _undo = null;
        if (steps is null) return;
        for (var i = steps.Count - 1; i >= 0; i--) steps[i]();
    }

    private IEnumerable<(string Entity, string Id, string Field, SyncCell Cell)> AllCells()
    {
        foreach (var (entity, rows) in _cells)
        {
            foreach (var (id, fields) in rows)
            {
                foreach (var (field, cell) in fields) yield return (entity, id, field, cell);
            }
        }
    }

    private void SetCore(string entity, string id, string field, SyncCell cell)
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
        if (fields.TryGetValue(field, out var old))
        {
            Unindex(entity, field, old.Value, id);
        }
        else
        {
            Count++;
        }
        fields[field] = cell;
        if (!_index.TryGetValue((entity, field), out var byValue))
        {
            byValue = new Dictionary<SyncValue, HashSet<string>>();
            _index[(entity, field)] = byValue;
        }
        if (!byValue.TryGetValue(cell.Value, out var ids))
        {
            ids = new HashSet<string>(StringComparer.Ordinal);
            byValue[cell.Value] = ids;
        }
        ids.Add(id);
    }

    private void RemoveCore(string entity, string id, string field)
    {
        if (!_cells.TryGetValue(entity, out var rows) || !rows.TryGetValue(id, out var fields) || !fields.Remove(field, out var old)) return;
        Count--;
        Unindex(entity, field, old.Value, id);
        if (fields.Count == 0) rows.Remove(id);
        if (rows.Count == 0) _cells.Remove(entity);
    }

    private void Unindex(string entity, string field, SyncValue value, string id)
    {
        if (_index.TryGetValue((entity, field), out var byValue) && byValue.TryGetValue(value, out var ids))
        {
            ids.Remove(id);
            if (ids.Count == 0) byValue.Remove(value);
        }
    }

    private void ClearCore()
    {
        _cells.Clear();
        _index.Clear();
        Count = 0;
    }
}
