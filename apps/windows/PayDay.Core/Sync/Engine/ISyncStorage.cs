using PayDay.Sync.Clock;
using PayDay.Sync.Merge;
using PayDay.Sync.Model;

namespace PayDay.Sync.Engine;

/// <summary>A sealed batch (section 10.1): immutable records with a sequence number that is never reused.</summary>
public sealed record SealedBatch(long Seq, IReadOnlyList<ChangeRecord> Records, bool Uploaded = false);

/// <summary>
/// A device's local sync state that is never synced (<c>data-model.md</c> section 6.2): device and
/// vault identity, the clock state, the outgoing queue, sealed batches, read cursors and snapshot
/// first-listed times.
/// </summary>
public interface ISyncMetadata
{
    string? DeviceId { get; set; }

    string? VaultId { get; set; }

    /// <summary>The persisted HLC clock state (section 2.3).</summary>
    Hlc? ClockState { get; set; }

    /// <summary>The sequence number the next sealed batch gets (starts at 1).</summary>
    long NextSeq { get; set; }

    /// <summary>Records written locally and not yet sealed into a batch.</summary>
    IReadOnlyList<ChangeRecord> PendingRecords { get; }

    void EnqueuePending(IEnumerable<ChangeRecord> records);

    void ClearPending();

    /// <summary>Sealed batches, uploaded or not, in sequence order.</summary>
    IReadOnlyList<SealedBatch> SealedBatches { get; }

    void AddSealedBatch(SealedBatch batch);

    void MarkBatchUploaded(long seq);

    /// <summary>The highest <c>seq</c> of this device's that has uploaded (0 when none).</summary>
    long LastUploadedSeq { get; }

    /// <summary>Read cursor per device (section 10.2); a device never heard of reads as 0.</summary>
    IReadOnlyDictionary<string, long> Cursors { get; }

    void SetCursor(string deviceId, long seq);

    /// <summary>When this device first listed a snapshot (local clock, Unix ms), by file name (section 11.1).</summary>
    long? SnapshotFirstListed(string snapshotName);

    void SetSnapshotFirstListed(string snapshotName, long unixMs);

    /// <summary>Forgets all sync state (leaving or rejoining a vault, sections 12.2 and 12.4).</summary>
    void Reset();
}

/// <summary>
/// Everything a device persists for sync. <see cref="RunInTransaction"/> MUST make the merged
/// records, the clock state and the outgoing queue change together or not at all (sections 2.3
/// and 10.1); a SQLite implementation wraps it in one database transaction.
/// </summary>
public interface ISyncStorage
{
    IRecordStore Records { get; }

    ISyncMetadata Metadata { get; }

    void RunInTransaction(Action action);
}
