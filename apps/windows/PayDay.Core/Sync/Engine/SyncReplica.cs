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
/// <param name="ChangedRows">The rows that had at least one winning value change, sorted.</param>
public sealed record MergeOutcome(int Changed, IReadOnlyList<string> SkewedDevices, IReadOnlyList<RowKey> ChangedRows)
{
    public static readonly MergeOutcome None = new(0, Array.Empty<string>(), Array.Empty<RowKey>());
}

/// <summary>A snapshot ready to seal (section 11.2).</summary>
public sealed record SnapshotContent(Hlc Hlc, IReadOnlyDictionary<string, long> Covers, IReadOnlyList<ChangeRecord> Records);

/// <summary>
/// Keeps derived storage (the app's materialized <c>Bills</c>, <c>Payments</c>, ... tables) in step
/// with the merged state. <see cref="SyncReplica"/> calls it inside the same transaction as every
/// merge or local action that changed rows, so both commit or roll back together.
/// </summary>
public interface ISyncMaterializer
{
    /// <summary>Called with every row whose winning values changed; read the new values from <paramref name="state"/>.</summary>
    void RowsChanged(SyncState state, IReadOnlyCollection<RowKey> rows);
}

/// <summary>
/// One device's replica of the vault: local edits (one tick per user action, applied and queued in
/// one transaction), merging remote files with clock observation and cursor updates, sealing
/// batches and preparing snapshots. Drive I/O and encryption sit above this
/// (<see cref="VaultCodec"/>); persistence sits below it (<see cref="ISyncStorage"/>).
/// <para>
/// <b>Threading.</b> Every public method takes one lock, so calls from the UI thread and a
/// background sync are serialized; each mutating call runs exactly one
/// <see cref="ISyncStorage.RunInTransaction"/>. The callbacks passed to <see cref="Commit"/> and
/// <see cref="Read{T}"/> run under that lock and must not call back into the replica (a nested
/// transaction throws <see cref="InvalidOperationException"/>). <see cref="State"/> and
/// <see cref="Metadata"/> are the live objects and are not thread-safe; read them through
/// <see cref="Read{T}"/> when another thread may be syncing.
/// </para>
/// </summary>
public sealed class SyncReplica
{
    /// <summary>Writers SHOULD split batches above about 5,000 records (section 10.1, step 6).</summary>
    public const int MaxBatchRecords = 5000;

    private readonly ISyncStorage _storage;
    private readonly TimeProvider _time;
    private readonly ISyncMaterializer? _materializer;
    private readonly object _gate = new();

    public SyncReplica(ISyncStorage storage, TimeProvider time, ISyncMaterializer? materializer = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _materializer = materializer;
        var id = storage.Metadata.DeviceId;
        if (!DeviceIds.IsValid(id)) throw new ArgumentException("Storage has no valid device ID; set Metadata.DeviceId on create or join.", nameof(storage));
        DeviceId = id!;
        State = new SyncState(storage.Records);
    }

    /// <summary>
    /// Starts sync state for a device creating or joining a vault (sections 1, 12.1 step 3, 12.2):
    /// a new device ID, no clock state, cursors at 0. With <paramref name="clearSyncedData"/>
    /// (joining a vault after the user agreed to replace this device's data, 12.2 step 3) every
    /// synced record is removed as well, in the same transaction.
    /// </summary>
    /// <param name="alsoInTransaction">
    /// Runs inside the same transaction after the reset, with the (possibly cleared) state: joining a
    /// vault rebuilds the materialized tables here so they change together with the records.
    /// </param>
    public static SyncReplica Start(
        ISyncStorage storage,
        TimeProvider time,
        string vaultId,
        bool clearSyncedData = false,
        ISyncMaterializer? materializer = null,
        Action<SyncState>? alsoInTransaction = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        if (!Uuid.IsCanonical(vaultId)) throw new ArgumentException("Vault ID must be a canonical UUID.", nameof(vaultId));
        storage.RunInTransaction(() =>
        {
            storage.Metadata.Reset();
            storage.Metadata.DeviceId = DeviceIds.NewRandom();
            storage.Metadata.VaultId = vaultId;
            if (clearSyncedData) storage.Records.Clear();
            alsoInTransaction?.Invoke(new SyncState(storage.Records));
        });
        return new SyncReplica(storage, time, materializer);
    }

    /// <summary>
    /// Changes local sync state (never records) under the replica's lock, in one transaction: for
    /// example the vault ID, or snapshot first-listed times. <paramref name="update"/> must not call
    /// back into the replica.
    /// </summary>
    public void Update(Action<ISyncMetadata> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate) _storage.RunInTransaction(() => update(Metadata));
    }

    public string DeviceId { get; }

    /// <summary>The live merged state. Not thread-safe; see the type's remarks.</summary>
    public SyncState State { get; }

    /// <summary>The live local sync state. Not thread-safe; see the type's remarks.</summary>
    public ISyncMetadata Metadata => _storage.Metadata;

    public long NowMs => _time.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>A new random row ID (UUIDv7) for a bill, extra payment, snapshot, income source or irregular paycheck.</summary>
    public string NewRowId() => Uuid.NewV7(_time);

    /// <summary>Reads the state under the replica's lock.</summary>
    public T Read<T>(Func<SyncState, ISyncMetadata, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        lock (_gate) return read(State, Metadata);
    }

    /// <summary>
    /// Runs one user action: takes one tick, lets <paramref name="edit"/> build the records at that
    /// HLC from the current state, merges them locally, queues them for upload, saves the clock and
    /// refreshes materialized rows, all in one transaction. Returns the records (empty when the
    /// action changed nothing; the tick is then not saved). If anything throws, nothing is kept.
    /// </summary>
    public IReadOnlyList<ChangeRecord> Commit(Func<SyncState, Hlc, IReadOnlyList<ChangeRecord>> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return CommitSteps(edit);
    }

    /// <summary>
    /// One user action made of several writer rules that must see each other's effect (for example
    /// unmarking a period's Regular payment and then each Extra payment of it, where the second unmark
    /// must see the checkpoint the first one raised): one tick, and each step builds its records from
    /// the state with the previous steps' records already merged. Everything is queued, the clock
    /// saved and the rows materialized in one transaction; if any step throws, nothing is kept.
    /// </summary>
    public IReadOnlyList<ChangeRecord> CommitSteps(params Func<SyncState, Hlc, IReadOnlyList<ChangeRecord>>[] steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        lock (_gate)
        {
            IReadOnlyList<ChangeRecord> written = Array.Empty<ChangeRecord>();
            _storage.RunInTransaction(() =>
            {
                var hlc = HlcClock.Tick(Metadata.ClockState, NowMs, DeviceId);
                var all = new List<ChangeRecord>();
                var changed = new SortedSet<RowKey>(RowKeyOrder.Instance);
                foreach (var step in steps)
                {
                    var records = step(State, hlc);
                    if (records.Any(r => r.Hlc != hlc))
                    {
                        throw new InvalidOperationException("Every record of one action carries that action's HLC.");
                    }
                    foreach (var r in records)
                    {
                        if (State.Merge(r)) changed.Add(new RowKey(r.Entity, r.Id));
                    }
                    all.AddRange(records);
                }
                if (all.Count == 0) return;
                IReadOnlyList<ChangeRecord> sorted = all;
                if (steps.Length > 1)
                {
                    // One action never writes one field twice (section 4.2): a later step's record for a
                    // field replaces an earlier step's, and the local state holds exactly that value.
                    var last = new Dictionary<(string, string, string), ChangeRecord>();
                    foreach (var r in all) last[(r.Entity, r.Id, r.Field)] = r;
                    sorted = ChangeRecord.SortByKey(last.Values);
                    foreach (var r in sorted)
                    {
                        if (State.TryGet(r.Entity, r.Id, r.Field, out var cell) && cell.Hlc == r.Hlc && cell.Value != r.Value)
                        {
                            State.Store.Set(r.Entity, r.Id, r.Field, new SyncCell(r.Value, r.Hlc));
                            changed.Add(new RowKey(r.Entity, r.Id));
                        }
                    }
                }
                Metadata.EnqueuePending(sorted);
                Metadata.ClockState = hlc;
                Materialize(changed);
                written = sorted;
            });
            return written;
        }
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

    /// <summary>Deletes a row of any entity except <c>payment</c> (use <see cref="DeletePayment"/> or <see cref="UnmarkPaid"/>).</summary>
    public IReadOnlyList<ChangeRecord> DeleteRow(string entity, string id)
        => Commit((s, h) => RowWriter.Delete(s, entity, id, h));

    public IReadOnlyList<ChangeRecord> SetSetting(string key, SyncValue value)
        => Commit((s, h) => RowWriter.SetSetting(s, key, value, h));

    /// <summary>
    /// Re-stamps all local state at one new tick, so this device can be the first writer of a new
    /// vault from state that already holds other devices' HLCs (for example after leaving one vault
    /// and creating another, section 12.1 step 5): every winning value is written again with this
    /// device's HLC and queued, so <c>000001.chg</c> carries only this device's records. Values do not
    /// change. <c>device</c> rows are not carried over; each device of the new vault writes its own.
    /// </summary>
    public IReadOnlyList<ChangeRecord> RestampLocalState()
        => Commit((s, h) => s.AllRecords()
            .Where(r => r.Entity != SyncEntities.Device)
            .Select(r => new ChangeRecord(r.Entity, r.Id, r.Field, r.Value, h))
            .ToArray());

    /// <summary>
    /// Merges remote records and observes each one's HLC, in one transaction (section 2.3). Callers
    /// pass only records that passed the envelope checks; use <see cref="MergeFile"/> for whole files.
    /// </summary>
    public MergeOutcome MergeRecords(IEnumerable<ChangeRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        lock (_gate)
        {
            var outcome = MergeOutcome.None;
            _storage.RunInTransaction(() => outcome = MergeInsideTransaction(records));
            return outcome;
        }
    }

    /// <summary>
    /// Merges an opened change file or snapshot (sections 10.2 and 11.3), in any order. A change
    /// file at <c>cursor + 1</c> advances its device's cursor, and on through any files already
    /// merged above the gap; a file above a gap is remembered so the cursor can pass it later. A
    /// snapshot raises every cursor to its <c>covers</c> and then advances through remembered files.
    /// </summary>
    public MergeOutcome MergeFile(EnvelopeResult file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Path.Kind == SyncPathKind.Pairing) throw new ArgumentException("Pairing files hold no records.", nameof(file));
        lock (_gate)
        {
            var outcome = MergeOutcome.None;
            _storage.RunInTransaction(() =>
            {
                outcome = MergeInsideTransaction(file.Records);
                if (file.Path.Kind == SyncPathKind.Changes)
                {
                    var device = file.Path.DeviceId!;
                    var cursor = CursorOf(device);
                    if (file.Seq == cursor + 1)
                    {
                        Metadata.SetCursor(device, file.Seq);
                        AdvanceThroughMergedAhead(device);
                    }
                    else if (file.Seq > cursor + 1)
                    {
                        Metadata.SetMergedAhead(device, Metadata.MergedAhead(device).Append(file.Seq));
                    }
                }
                else
                {
                    foreach (var (device, seq) in file.Covers)
                    {
                        if (seq > CursorOf(device)) Metadata.SetCursor(device, seq);
                        AdvanceThroughMergedAhead(device);
                    }
                }
            });
            return outcome;
        }
    }

    /// <summary>The read cursor for a device (0 for a device never heard of).</summary>
    public long Cursor(string deviceId)
    {
        lock (_gate) return CursorOf(deviceId);
    }

    /// <summary>
    /// Seals every queued record into immutable batches with the next sequence numbers (section
    /// 10.1, step 2), at most <see cref="MaxBatchRecords"/> records each. Returns the new batches
    /// (none when nothing is queued).
    /// </summary>
    public IReadOnlyList<SealedBatch> SealPending()
    {
        lock (_gate)
        {
            var sealedBatches = new List<SealedBatch>();
            _storage.RunInTransaction(() =>
            {
                var pending = Metadata.PendingRecords.ToArray();
                for (var start = 0; start < pending.Length; start += MaxBatchRecords)
                {
                    var batch = new SealedBatch(Metadata.NextSeq, pending.Skip(start).Take(MaxBatchRecords).ToArray());
                    Metadata.AddSealedBatch(batch);
                    Metadata.NextSeq = batch.Seq + 1;
                    sealedBatches.Add(batch);
                }
                Metadata.ClearPending();
            });
            return sealedBatches;
        }
    }

    /// <summary>The lowest sealed batch not uploaded yet: uploads go strictly in sequence order.</summary>
    public SealedBatch? NextBatchToUpload
    {
        get
        {
            lock (_gate) return Metadata.SealedBatches.Where(b => !b.Uploaded).OrderBy(b => b.Seq).FirstOrDefault();
        }
    }

    /// <summary>Records a successful upload. Only the next batch in order can be marked.</summary>
    public void MarkUploaded(long seq)
    {
        lock (_gate)
        {
            var next = Metadata.SealedBatches.Where(b => !b.Uploaded).OrderBy(b => b.Seq).FirstOrDefault();
            if (next is null || next.Seq != seq)
            {
                throw new InvalidOperationException($"Batch {seq} is not the next batch to upload.");
            }
            _storage.RunInTransaction(() => Metadata.MarkBatchUploaded(seq));
        }
    }

    /// <summary>
    /// Prepares a snapshot (section 11.2): one tick (saved), <c>covers</c> from the read cursors plus
    /// this device's last uploaded sequence number, and every winning record in the state.
    /// </summary>
    public SnapshotContent PrepareSnapshot()
    {
        lock (_gate)
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
    }

    /// <summary>
    /// The startup safety net of section 2.3: raises the clock state to the greatest HLC this device
    /// stamped anywhere in its local store, queue or batches.
    /// </summary>
    public void RecoverClock()
    {
        lock (_gate)
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
    }

    private long CursorOf(string deviceId) => Metadata.Cursors.TryGetValue(deviceId, out var c) ? c : 0;

    private void AdvanceThroughMergedAhead(string device)
    {
        var ahead = new SortedSet<long>(Metadata.MergedAhead(device));
        if (ahead.Count == 0) return;
        var cursor = CursorOf(device);
        while (ahead.Remove(cursor + 1)) cursor++;
        ahead.RemoveWhere(s => s <= cursor);
        Metadata.SetCursor(device, cursor);
        Metadata.SetMergedAhead(device, ahead);
    }

    private MergeOutcome MergeInsideTransaction(IEnumerable<ChangeRecord> records)
    {
        var changed = 0;
        var rows = new SortedSet<RowKey>(RowKeyOrder.Instance);
        var skewed = new SortedSet<string>(StringComparer.Ordinal);
        var now = NowMs;
        foreach (var r in records)
        {
            if (State.Merge(r))
            {
                changed++;
                rows.Add(new RowKey(r.Entity, r.Id));
            }
            Metadata.ClockState = HlcClock.Observe(Metadata.ClockState, r.Hlc, DeviceId);
            if (HlcClock.SkewWarning(r.Hlc, now)) skewed.Add(r.Hlc.DeviceId);
        }
        Materialize(rows);
        return new MergeOutcome(changed, skewed.ToArray(), rows.ToArray());
    }

    private void Materialize(IReadOnlyCollection<RowKey> rows)
    {
        if (rows.Count > 0) _materializer?.RowsChanged(State, rows);
    }

    private sealed class RowKeyOrder : IComparer<RowKey>
    {
        public static readonly RowKeyOrder Instance = new();

        public int Compare(RowKey x, RowKey y)
        {
            var c = Json.Utf8Order.Compare(x.Entity, y.Entity);
            return c != 0 ? c : Json.Utf8Order.Compare(x.Id, y.Id);
        }
    }
}
