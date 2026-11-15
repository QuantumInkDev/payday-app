using PayDay.Sync.Clock;
using PayDay.Sync.Merge;
using PayDay.Sync.Model;

namespace PayDay.Sync.Engine;

/// <summary>An in-memory <see cref="ISyncMetadata"/>.</summary>
public sealed class InMemorySyncMetadata : ISyncMetadata
{
    private List<ChangeRecord> _pending = new();
    private List<SealedBatch> _batches = new();
    private Dictionary<string, long> _cursors = new(StringComparer.Ordinal);
    private Dictionary<string, SortedSet<long>> _ahead = new(StringComparer.Ordinal);
    private Dictionary<string, long> _firstListed = new(StringComparer.Ordinal);

    public string? DeviceId { get; set; }

    public string? VaultId { get; set; }

    public Hlc? ClockState { get; set; }

    public long NextSeq { get; set; } = 1;

    public IReadOnlyList<ChangeRecord> PendingRecords => _pending;

    public void EnqueuePending(IEnumerable<ChangeRecord> records) => _pending.AddRange(records);

    public void ClearPending() => _pending.Clear();

    public IReadOnlyList<SealedBatch> SealedBatches => _batches;

    public void AddSealedBatch(SealedBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        _batches.Add(batch);
    }

    public void MarkBatchUploaded(long seq)
    {
        var i = _batches.FindIndex(b => b.Seq == seq);
        if (i < 0) throw new InvalidOperationException($"No sealed batch {seq}.");
        _batches[i] = _batches[i] with { Uploaded = true };
    }

    public long LastUploadedSeq => _batches.Where(b => b.Uploaded).Select(b => b.Seq).DefaultIfEmpty(0).Max();

    public IReadOnlyDictionary<string, long> Cursors => _cursors;

    public void SetCursor(string deviceId, long seq) => _cursors[deviceId] = seq;

    public IReadOnlyCollection<long> MergedAhead(string deviceId)
        => _ahead.TryGetValue(deviceId, out var set) ? set.ToArray() : Array.Empty<long>();

    public void SetMergedAhead(string deviceId, IEnumerable<long> seqs)
    {
        var set = new SortedSet<long>(seqs);
        if (set.Count == 0) _ahead.Remove(deviceId);
        else _ahead[deviceId] = set;
    }

    public long? SnapshotFirstListed(string snapshotName) => _firstListed.TryGetValue(snapshotName, out var t) ? t : null;

    public void SetSnapshotFirstListed(string snapshotName, long unixMs) => _firstListed[snapshotName] = unixMs;

    public void Reset()
    {
        DeviceId = null;
        VaultId = null;
        ClockState = null;
        NextSeq = 1;
        _pending.Clear();
        _batches.Clear();
        _cursors.Clear();
        _ahead.Clear();
        _firstListed.Clear();
    }

    /// <summary>A deep copy of the current state, for rollback.</summary>
    internal object Capture()
        => new Saved(DeviceId, VaultId, ClockState, NextSeq, _pending.ToList(), _batches.ToList(),
            new Dictionary<string, long>(_cursors, StringComparer.Ordinal),
            _ahead.ToDictionary(a => a.Key, a => new SortedSet<long>(a.Value), StringComparer.Ordinal),
            new Dictionary<string, long>(_firstListed, StringComparer.Ordinal));

    internal void Restore(object captured)
    {
        var s = (Saved)captured;
        DeviceId = s.DeviceId;
        VaultId = s.VaultId;
        ClockState = s.ClockState;
        NextSeq = s.NextSeq;
        _pending = s.Pending;
        _batches = s.Batches;
        _cursors = s.Cursors;
        _ahead = s.Ahead;
        _firstListed = s.FirstListed;
    }

    private sealed record Saved(
        string? DeviceId,
        string? VaultId,
        Hlc? ClockState,
        long NextSeq,
        List<ChangeRecord> Pending,
        List<SealedBatch> Batches,
        Dictionary<string, long> Cursors,
        Dictionary<string, SortedSet<long>> Ahead,
        Dictionary<string, long> FirstListed);
}

/// <summary>
/// An in-memory <see cref="ISyncStorage"/>. Transactions roll back records and metadata when the
/// action throws, and nested transactions throw, like the SQLite implementation will.
/// </summary>
public sealed class InMemorySyncStorage : ISyncStorage
{
    private readonly InMemoryRecordStore _records = new();
    private readonly InMemorySyncMetadata _metadata = new();
    private bool _inTransaction;

    public IRecordStore Records => _records;

    public ISyncMetadata Metadata => _metadata;

    public void RunInTransaction(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_inTransaction)
        {
            throw new InvalidOperationException("RunInTransaction is not reentrant: a sync transaction is already running on this storage.");
        }
        _inTransaction = true;
        var saved = _metadata.Capture();
        _records.BeginJournal();
        try
        {
            action();
            _records.CommitJournal();
        }
        catch
        {
            _records.RollbackJournal();
            _metadata.Restore(saved);
            throw;
        }
        finally
        {
            _inTransaction = false;
        }
    }
}
