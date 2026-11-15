using PayDay.Sync.Clock;
using PayDay.Sync.Merge;
using PayDay.Sync.Model;

namespace PayDay.Sync.Engine;

/// <summary>
/// A sealed batch (section 10.1): immutable records with a sequence number that is never reused.
/// Two batches are equal when their numbers, upload flags and records (in order) are equal, so a batch
/// read back from storage equals the one that was sealed.
/// </summary>
public sealed record SealedBatch(long Seq, IReadOnlyList<ChangeRecord> Records, bool Uploaded = false)
{
    public bool Equals(SealedBatch? other)
        => other is not null
            && Seq == other.Seq
            && Uploaded == other.Uploaded
            && Records.SequenceEqual(other.Records);

    public override int GetHashCode() => HashCode.Combine(Seq, Uploaded, Records.Count);
}

/// <summary>
/// A device's local sync state that is never synced (<c>data-model.md</c> section 6.2): device and
/// vault identity, the clock state, the outgoing queue, sealed batches, read cursors, change files
/// merged above a gap, and snapshot first-listed times. Not thread-safe; see <see cref="ISyncStorage"/>.
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

    /// <summary>
    /// Change files of <paramref name="deviceId"/> already merged above its cursor (beyond a gap,
    /// section 10.2 "Order-free"), so the cursor can advance through them once the gap is filled.
    /// </summary>
    IReadOnlyCollection<long> MergedAhead(string deviceId);

    void SetMergedAhead(string deviceId, IEnumerable<long> seqs);

    /// <summary>When this device first listed a snapshot (local clock, Unix ms), by file name (section 11.1).</summary>
    long? SnapshotFirstListed(string snapshotName);

    void SetSnapshotFirstListed(string snapshotName, long unixMs);

    /// <summary>Forgets all sync state (leaving or rejoining a vault, sections 12.2 and 12.4).</summary>
    void Reset();
}

/// <summary>
/// Everything a device persists for sync.
/// <para>
/// <b>Threading and transactions.</b> Storage is not thread-safe: <see cref="SyncReplica"/>
/// serializes every operation behind one lock, and nothing else should touch the storage while a
/// replica owns it. <see cref="RunInTransaction"/> is synchronous and MUST make the merged records,
/// the clock state, cursors and the outgoing queue change together or not at all (sections 2.3 and
/// 10.1): when the action throws, every change it made is rolled back and the exception propagates.
/// It is not reentrant: calling it from inside a running transaction throws
/// <see cref="InvalidOperationException"/>. A SQLite implementation uses one dedicated connection for
/// sync and wraps the action in one <c>BEGIN IMMEDIATE</c> … <c>COMMIT</c>.
/// </para>
/// </summary>
public interface ISyncStorage
{
    IRecordStore Records { get; }

    ISyncMetadata Metadata { get; }

    /// <summary>Runs <paramref name="action"/> as one atomic transaction (see the type's remarks).</summary>
    void RunInTransaction(Action action);
}
