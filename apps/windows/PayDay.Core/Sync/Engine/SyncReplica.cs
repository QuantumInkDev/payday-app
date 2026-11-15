using PayDay.Sync.Clock;
using PayDay.Sync.Files;
using PayDay.Sync.Ids;
using PayDay.Sync.Merge;
using PayDay.Sync.Model;
using PayDay.Sync.Reading;

namespace PayDay.Sync.Engine;

/// <summary>The result of merging remote records.</summary>
/// <param name="Changed">How many records became the winning value.</param>
/// <param name="SkewedDevices">Devices with a merged record more than 10 minutes ahead of the local clock (section 2.4).</param>
public sealed record MergeOutcome(int Changed, IReadOnlyList<string> SkewedDevices);

/// <summary>A snapshot ready to seal (section 11.2).</summary>
public sealed record SnapshotContent(Hlc Hlc, IReadOnlyDictionary<string, long> Covers, IReadOnlyList<ChangeRecord> Records);

/// <summary>
/// One device's replica of the vault: local edits (one tick per user action, applied and queued in
/// one transaction), merging remote files with clock observation and cursor updates, sealing
/// batches and preparing snapshots. Drive I/O and encryption sit above this
/// (<see cref="VaultCodec"/>); persistence sits below it (<see cref="ISyncStorage"/>).
/// </summary>
public sealed class SyncReplica
{
    private readonly ISyncStorage _storage;
    private readonly TimeProvider _time;

    public SyncReplica(ISyncStorage storage, TimeProvider time)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        var id = storage.Metadata.DeviceId;
        if (!DeviceIds.IsValid(id)) throw new ArgumentException("Storage has no valid device ID; set Metadata.DeviceId on create or join.", nameof(storage));
        DeviceId = id!;
        State = new SyncState(storage.Records);
    }

    /// <summary>Starts sync state for a device creating or joining a vault: a new device ID, cursors at 0 (sections 1, 12.1, 12.2).</summary>
    public static SyncReplica Start(ISyncStorage storage, TimeProvider time, string vaultId)
    {
        ArgumentNullException.ThrowIfNull(storage);
        storage.RunInTransaction(() =>
        {
            storage.Metadata.Reset();
            storage.Metadata.DeviceId = DeviceIds.NewRandom();
            storage.Metadata.VaultId = vaultId;
        });
        return new SyncReplica(storage, time);
    }

    public string DeviceId { get; }

    public SyncState State { get; }

    public ISyncMetadata Metadata => _storage.Metadata;

    public long NowMs => _time.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>A new random row ID (UUIDv7) for a bill, extra payment, snapshot, income source or irregular paycheck.</summary>
    public string NewRowId() => Uuid.NewV7(_time);

    /// <summary>
    /// Runs one user action: takes one tick, lets <paramref name="edit"/> build the records at that
    /// HLC from the current state, merges them locally, queues them for upload and saves the clock,
    /// all in one transaction. Returns the records (empty when the action changed nothing; the
    /// tick is then not saved).
    /// </summary>
    public IReadOnlyList<ChangeRecord> Commit(Func<SyncState, Hlc, IReadOnlyList<ChangeRecord>> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        IReadOnlyList<ChangeRecord> written = Array.Empty<ChangeRecord>();
        _storage.RunInTransaction(() =>
        {
            var hlc = HlcClock.Tick(Metadata.ClockState, NowMs, DeviceId);
            var records = edit(State, hlc);
            if (records.Count == 0) return;
            if (records.Any(r => r.Hlc != hlc))
            {
                throw new InvalidOperationException("Every record of one action carries that action's HLC.");
            }
            State.MergeAll(records);
            Metadata.EnqueuePending(records);
            Metadata.ClockState = hlc;
            written = records;
        });
        return written;
    }

    public IReadOnlyList<ChangeRecord> MarkPaid(string kind, string billId, string periodKey, double amount, string? paidAt)
        => Commit((s, h) => PaymentWriter.MarkPaid(s, kind, billId, periodKey, amount, paidAt, h));

    public IReadOnlyList<ChangeRecord> UnmarkPaid(string kind, string billId, string periodKey)
        => Commit((s, h) => PaymentWriter.UnmarkPaid(s, kind, billId, periodKey, h));

    public IReadOnlyList<ChangeRecord> DeletePayment(string paymentId)
        => Commit((s, h) => PaymentWriter.DeletePayment(s, paymentId, h));

    public IReadOnlyList<ChangeRecord> SetRemaining(string billId, double value, RemainingSource source, double? lastPushed = null)
        => Commit((s, h) => PaymentWriter.SetRemaining(s, billId, value, source, lastPushed, h));

    public IReadOnlyList<ChangeRecord> Restore(RestoreInput backup)
        => Commit((s, h) => RestoreWriter.Restore(s, backup, h));

    public IReadOnlyList<ChangeRecord> CreateRow(string entity, string id, IReadOnlyDictionary<string, SyncValue> fields)
        => Commit((_, h) => RowWriter.Create(entity, id, fields, h));

    public IReadOnlyList<ChangeRecord> EditRow(string entity, string id, IReadOnlyDictionary<string, SyncValue> changes)
        => Commit((s, h) => RowWriter.Edit(s, entity, id, changes, h));

    public IReadOnlyList<ChangeRecord> DeleteRow(string entity, string id)
        => Commit((s, h) => RowWriter.Delete(s, entity, id, h));

    public IReadOnlyList<ChangeRecord> SetSetting(string key, SyncValue value)
        => Commit((s, h) => RowWriter.SetSetting(s, key, value, h));

    /// <summary>
    /// Merges remote records and observes each one's HLC, in one transaction (section 2.3). Callers
    /// pass only records that passed the envelope checks; use <see cref="MergeFile"/> for whole files.
    /// </summary>
    public MergeOutcome MergeRecords(IEnumerable<ChangeRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        MergeOutcome outcome = new(0, Array.Empty<string>());
        _storage.RunInTransaction(() => outcome = MergeInsideTransaction(records));
        return outcome;
    }

    /// <summary>
    /// Merges an opened change file or snapshot (sections 10.2 and 11.3). A change file advances
    /// its device's cursor when it is the next file; a snapshot raises every cursor to its <c>covers</c>.
    /// </summary>
    public MergeOutcome MergeFile(EnvelopeResult file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Path.Kind == SyncPathKind.Pairing) throw new ArgumentException("Pairing files hold no records.", nameof(file));
        MergeOutcome outcome = new(0, Array.Empty<string>());
        _storage.RunInTransaction(() =>
        {
            outcome = MergeInsideTransaction(file.Records);
            if (file.Path.Kind == SyncPathKind.Changes)
            {
                var device = file.Path.DeviceId!;
                if (file.Seq == Cursor(device) + 1) Metadata.SetCursor(device, file.Seq);
            }
            else
            {
                foreach (var (device, seq) in file.Covers)
                {
                    if (seq > Cursor(device)) Metadata.SetCursor(device, seq);
                }
            }
        });
        return outcome;
    }

    /// <summary>The read cursor for a device (0 for a device never heard of).</summary>
    public long Cursor(string deviceId) => Metadata.Cursors.TryGetValue(deviceId, out var c) ? c : 0;

    /// <summary>
    /// Seals every queued record into one immutable batch with the next sequence number
    /// (section 10.1, step 2). Returns null when nothing is queued.
    /// </summary>
    public SealedBatch? SealPending()
    {
        SealedBatch? batch = null;
        _storage.RunInTransaction(() =>
        {
            if (Metadata.PendingRecords.Count == 0) return;
            batch = new SealedBatch(Metadata.NextSeq, Metadata.PendingRecords.ToArray());
            Metadata.AddSealedBatch(batch);
            Metadata.NextSeq = batch.Seq + 1;
            Metadata.ClearPending();
        });
        return batch;
    }

    /// <summary>The lowest sealed batch not uploaded yet: uploads go strictly in sequence order.</summary>
    public SealedBatch? NextBatchToUpload => Metadata.SealedBatches.Where(b => !b.Uploaded).OrderBy(b => b.Seq).FirstOrDefault();

    /// <summary>Records a successful upload. Only the next batch in order can be marked.</summary>
    public void MarkUploaded(long seq)
    {
        if (NextBatchToUpload is not { } next || next.Seq != seq)
        {
            throw new InvalidOperationException($"Batch {seq} is not the next batch to upload.");
        }
        _storage.RunInTransaction(() => Metadata.MarkBatchUploaded(seq));
    }

    /// <summary>
    /// Prepares a snapshot (section 11.2): one tick (saved), <c>covers</c> from the read cursors plus
    /// this device's last uploaded sequence number, and every winning record in the state.
    /// </summary>
    public SnapshotContent PrepareSnapshot()
    {
        SnapshotContent? content = null;
        _storage.RunInTransaction(() =>
        {
            var hlc = HlcClock.Tick(Metadata.ClockState, NowMs, DeviceId);
            Metadata.ClockState = hlc;
            var covers = Compaction.SnapshotCovers(Metadata.Cursors, DeviceId, Metadata.LastUploadedSeq);
            content = new SnapshotContent(hlc, covers, State.AllRecords());
        });
        return content!;
    }

    /// <summary>
    /// The startup safety net of section 2.3: raises the clock state to the greatest HLC this device
    /// stamped anywhere in its local store, queue or batches.
    /// </summary>
    public void RecoverClock()
    {
        _storage.RunInTransaction(() =>
        {
            var clock = new HybridLogicalClock(DeviceId, Metadata.ClockState, _time);
            if (State.LatestHlcOf(DeviceId) is { } stored) clock.RaiseTo(stored);
            foreach (var r in Metadata.PendingRecords.Concat(Metadata.SealedBatches.SelectMany(b => b.Records)))
            {
                if (r.Hlc.DeviceId == DeviceId) clock.RaiseTo(r.Hlc);
            }
            Metadata.ClockState = clock.State;
        });
    }

    private MergeOutcome MergeInsideTransaction(IEnumerable<ChangeRecord> records)
    {
        var changed = 0;
        var skewed = new SortedSet<string>(StringComparer.Ordinal);
        var now = NowMs;
        foreach (var r in records)
        {
            if (State.Merge(r)) changed++;
            Metadata.ClockState = HlcClock.Observe(Metadata.ClockState, r.Hlc, DeviceId);
            if (HlcClock.SkewWarning(r.Hlc, now)) skewed.Add(r.Hlc.DeviceId);
        }
        return new MergeOutcome(changed, skewed.ToArray());
    }
}
