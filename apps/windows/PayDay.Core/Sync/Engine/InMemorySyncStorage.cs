using PayDay.Sync.Clock;
using PayDay.Sync.Merge;
using PayDay.Sync.Model;

namespace PayDay.Sync.Engine;

/// <summary>An in-memory <see cref="ISyncMetadata"/>.</summary>
public sealed class InMemorySyncMetadata : ISyncMetadata
{
    private readonly List<ChangeRecord> _pending = new();
    private readonly List<SealedBatch> _batches = new();
    private readonly Dictionary<string, long> _cursors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _firstListed = new(StringComparer.Ordinal);

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
        _firstListed.Clear();
    }
}

/// <summary>An in-memory <see cref="ISyncStorage"/>. Its "transaction" just runs the action.</summary>
public sealed class InMemorySyncStorage : ISyncStorage
{
    public IRecordStore Records { get; } = new InMemoryRecordStore();

    public ISyncMetadata Metadata { get; } = new InMemorySyncMetadata();

    public void RunInTransaction(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }
}
