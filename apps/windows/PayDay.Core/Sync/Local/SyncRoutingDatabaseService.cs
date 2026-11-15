using System.Runtime.CompilerServices;
using PayDay.Models;
using PayDay.Services;
using PayDay.Sync.Engine;
using PayDay.Sync.Json;
using PayDay.Sync.Merge;
using PayDay.Sync.Model;

namespace PayDay.Sync.Local;

/// <summary>
/// The app's write path. With local sync <b>off</b> every call goes straight to the tables
/// (<paramref name="tables"/>, today's <see cref="SqliteDatabaseService"/>), so the app behaves exactly
/// as before. With sync <b>on</b>, reads still come from the tables (the
/// <see cref="TableMaterializer"/> keeps them equal to the merged state) and every write becomes one
/// user action on the <see cref="SyncReplica"/>:
/// <list type="bullet">
///   <item>mark / unmark / mark all: <see cref="PaymentWriter.MarkPaid"/> and
///     <see cref="PaymentWriter.UnmarkPaid"/> (Remaining is then computed, never decremented);</item>
///   <item>saving a bill: a create (new UUIDv7 ID) or <see cref="RowWriter.Edit"/> of the fields that
///     changed, plus <see cref="PaymentWriter.SetRemaining"/> when Remaining was edited;</item>
///   <item>snapshots: create and delete rows; synced settings: <see cref="RowWriter.SetSetting"/>;</item>
///   <item>restoring a backup: <see cref="RestoreWriter"/>, after saving a backup of the current data.</item>
/// </list>
/// Per-device settings (<c>Theme</c>, <c>LastNotionSync</c>, …) always go to the tables.
/// <para>
/// Every write holds <see cref="LocalSync"/>'s write gate, so none can slip in while sync is being
/// turned on or off or a vault joined, and the replica cannot change under a write.
/// </para>
/// <para>
/// "Fields that changed" are measured against the values the object had when it was read or written
/// through this service (with sync on or off), so saving a page's copy of a bill never reverts a
/// field another device changed in the meantime unless this device edited that field too. An object
/// this service never saw is compared with the merged state. The same holds for the type colour map:
/// only the colours that differ from the map last read are written.
/// </para>
/// </summary>
public sealed class SyncRoutingDatabaseService : IDatabaseService, INotionSyncHost
{
    private readonly IDatabaseService _tables;
    private readonly LocalSync _sync;
    private readonly BackupRotationService? _backups;
    private readonly ConditionalWeakTable<Bill, BillBaseline> _baselines = new();
    private readonly object _typeColorsLock = new();
    private string? _typeColorsLoaded;
    private bool _typeColorsKnown;

    /// <param name="tables">Today's tables.</param>
    /// <param name="sync">Local sync; while it is off this service only passes calls through.</param>
    /// <param name="backups">Writes the backup a restore with sync on saves first (<c>sync-protocol.md</c> 12.3 step 1); optional.</param>
    public SyncRoutingDatabaseService(IDatabaseService tables, LocalSync sync, BackupRotationService? backups = null)
    {
        _tables = tables ?? throw new ArgumentNullException(nameof(tables));
        _sync = sync ?? throw new ArgumentNullException(nameof(sync));
        _backups = backups;
    }

    /// <summary>The tables this service reads, and writes while sync is off.</summary>
    public IDatabaseService Tables => _tables;

    public LocalSync Sync => _sync;

    // ---- Reads: always the tables ---------------------------------------------------------------------

    public async Task<string?> GetSettingAsync(string key)
    {
        var value = await _tables.GetSettingAsync(key).ConfigureAwait(false);
        if (key == TypeColorService.SettingKey) RememberTypeColors(value);
        return value;
    }

    public async Task<IReadOnlyList<Bill>> GetAllBillsAsync()
    {
        var bills = await _tables.GetAllBillsAsync().ConfigureAwait(false);
        foreach (var bill in bills) Remember(bill);
        return bills;
    }

    public Task<IReadOnlyList<Payment>> GetPaymentsByPeriodAsync(string periodKey) => _tables.GetPaymentsByPeriodAsync(periodKey);

    public Task<IReadOnlyList<Payment>> GetAllPaymentsAsync() => _tables.GetAllPaymentsAsync();

    public Task<IReadOnlyList<Snapshot>> GetAllSnapshotsAsync() => _tables.GetAllSnapshotsAsync();

    public async Task<IReadOnlyDictionary<string, string?>> GetAllSettingsAsync()
    {
        var all = await _tables.GetAllSettingsAsync().ConfigureAwait(false);
        RememberTypeColors(all.TryGetValue(TypeColorService.SettingKey, out var colors) ? colors : null);
        return all;
    }

    // ---- Settings -----------------------------------------------------------------------------------

    /// <summary>
    /// True for a key the sync layer owns while sync is on: the synced settings of
    /// <c>data-model.md</c> 6.1 and today's <c>TypeColors</c> object. Everything else is per-device.
    /// </summary>
    public static bool IsSyncedSettingKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return SyncSettingKeys.IsPlanSetting(key)
            || SyncSettingKeys.NotionKeys.Contains(key, StringComparer.Ordinal)
            || key == TypeColorService.SettingKey;
    }

    public async Task SetSettingAsync(string key, string? value)
    {
        using (await _sync.EnterWriteAsync().ConfigureAwait(false))
        {
            if (_sync.Replica is not { } replica || !IsSyncedSettingKey(key))
            {
                await _tables.SetSettingAsync(key, value).ConfigureAwait(false);
            }
            else
            {
                var loaded = TypeColorsBaseline();
                replica.Commit((s, h) => SettingRecords(s, key, value, loaded, h));
                if (_sync.IsOn) _sync.NotifyLocalEdit();
            }
            if (key == TypeColorService.SettingKey) RememberTypeColors(value);
        }
    }

    private static IReadOnlyList<ChangeRecord> SettingRecords(SyncState s, string key, string? value, (bool Known, string? Text) loaded, Clock.Hlc h)
    {
        var output = new List<ChangeRecord>();
        if (key == TypeColorService.SettingKey)
        {
            // Today's whole-map value becomes one TypeColor/<type> row per type.
            var desired = TypeColorRows(value);
            if (loaded.Known)
            {
                // Only the colours that differ from the map the caller last read: a colour another
                // device set since then (and this map never saw) is left alone.
                var before = TypeColorRows(loaded.Text);
                foreach (var k in desired.Keys.Union(before.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
                {
                    var now = desired.TryGetValue(k, out var d) ? d : SyncValue.Null;
                    var then = before.TryGetValue(k, out var b) ? b : SyncValue.Null;
                    if (now != then) output.AddRange(RowWriter.SetSetting(s, k, now, h));
                }
            }
            else
            {
                foreach (var (k, v) in desired) output.AddRange(RowWriter.SetSetting(s, k, v, h));
                foreach (var k in s.Ids(SyncEntities.Setting))
                {
                    if (k.StartsWith(SyncSettingKeys.TypeColorPrefix, StringComparison.Ordinal) && !desired.ContainsKey(k))
                    {
                        output.AddRange(RowWriter.SetSetting(s, k, SyncValue.Null, h));
                    }
                }
            }
        }
        else if (SyncSettingKeys.NotionKeys.Contains(key, StringComparer.Ordinal))
        {
            output.AddRange(RowWriter.SetSetting(s, key, SyncValue.StringOrNull(value), h));
        }
        else
        {
            var mapped = value is null ? null : BackupSettings.MapOne(key, JsonData.String(value));
            output.AddRange(RowWriter.SetSetting(s, key, mapped ?? SyncValue.Null, h));
        }
        return ChangeRecord.SortByKey(output);
    }

    private static IReadOnlyDictionary<string, SyncValue> TypeColorRows(string? typeColorsJson)
        => BackupSettings.MapForRestore(JsonData.Object((BackupSettings.LegacyTypeColorsKey, typeColorsJson is null ? JsonData.Null : JsonData.String(typeColorsJson))));

    private void RememberTypeColors(string? text)
    {
        lock (_typeColorsLock)
        {
            _typeColorsLoaded = text;
            _typeColorsKnown = true;
        }
    }

    private (bool Known, string? Text) TypeColorsBaseline()
    {
        lock (_typeColorsLock) return (_typeColorsKnown, _typeColorsLoaded);
    }

    // ---- Bills ----------------------------------------------------------------------------------------

    public async Task UpsertBillAsync(Bill bill)
    {
        ArgumentNullException.ThrowIfNull(bill);
        using (await _sync.EnterWriteAsync().ConfigureAwait(false))
        {
            if (_sync.Replica is not { } replica)
            {
                await _tables.UpsertBillAsync(bill).ConfigureAwait(false);
                Remember(bill);
                return;
            }
            SaveBill(replica, bill, fromNotion: false);
        }
        _sync.NotifyLocalEdit();
    }

    private void SaveBill(SyncReplica replica, Bill bill, bool fromNotion)
    {
        var id = LegacyMapping.MapBillId(bill.Id);
        var exists = replica.Read((s, _) => s.Store.Fields(SyncEntities.Bill, id).Any());
        if (!exists)
        {
            id = replica.NewRowId();
            var createdAt = SyncDates.FormatTimestamp(DateTimeOffset.FromUnixTimeMilliseconds(replica.NowMs));
            var fields = LegacyMapping.AllBillFields(bill, createdAt);
            fields["remainingCheckpoint"] = SyncValue.Number(LegacyMapping.Money(Math.Max(0, bill.Remaining)));
            replica.CreateRow(SyncEntities.Bill, id, fields);
        }
        else if (fromNotion)
        {
            var lastPushed = _sync.GetNotionLastPushed(id);
            replica.Commit((s, h) => BillEditRecords(s, id, bill, baseline: null, RemainingSource.Notion, lastPushed, h));
        }
        else
        {
            var baseline = _baselines.TryGetValue(bill, out var b) ? b : null;
            replica.Commit((s, h) => BillEditRecords(s, id, bill, baseline, RemainingSource.App, null, h));
        }
        bill.Id = id;
        bill.Remaining = replica.Read((s, _) => s.Remaining(id));
        Remember(bill);
    }

    /// <summary>
    /// The records of one bill save: the editable fields that differ from <paramref name="baseline"/>
    /// (or, without one, from the merged state), an undelete when the row is not live, and
    /// <c>setRemaining</c> when Remaining was edited.
    /// </summary>
    private static IReadOnlyList<ChangeRecord> BillEditRecords(
        SyncState s, string id, Bill bill, BillBaseline? baseline, RemainingSource source, double? lastPushed, Clock.Hlc h)
    {
        var desired = LegacyMapping.EditableBillFields(bill);
        var changes = baseline is null
            ? desired
            : desired.Where(kv => !baseline.Fields.TryGetValue(kv.Key, out var before) || before != kv.Value)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        // RowWriter.Edit writes only the fields whose value differs from the merged state.
        var output = new List<ChangeRecord>(RowWriter.Edit(s, SyncEntities.Bill, id, changes, h));
        var live = s.IsLive(SyncEntities.Bill, id);
        if (!live)
        {
            // Today an upsert of a missing bill brings it back; with sync that is an undelete.
            output.AddRange(RowWriter.Undelete(s, SyncEntities.Bill, id, h));
        }
        var remaining = Math.Max(0, bill.Remaining);
        var remainingEdited = baseline is null || SyncMoney.Cents(LegacyMapping.Money(remaining)) != SyncMoney.Cents(baseline.Remaining);
        if (remainingEdited)
        {
            if (live)
            {
                output.AddRange(PaymentWriter.SetRemaining(s, id, LegacyMapping.Money(remaining), source, lastPushed, h));
            }
            else if (!(source == RemainingSource.Notion && lastPushed is { } pushed && SyncMoney.Cents(pushed) == SyncMoney.Cents(remaining)))
            {
                output.Add(new ChangeRecord(SyncEntities.Bill, id, "remainingCheckpoint", SyncValue.Number(LegacyMapping.Money(remaining)), h));
            }
        }
        return ChangeRecord.SortByKey(output);
    }

    // ---- Payments -------------------------------------------------------------------------------------

    public async Task<long> MarkBillPaidAsync(string periodKey, Bill bill, double amount)
    {
        ArgumentNullException.ThrowIfNull(bill);
        long localId;
        using (await _sync.EnterWriteAsync().ConfigureAwait(false))
        {
            if (_sync.Replica is not { } replica)
            {
                localId = await _tables.MarkBillPaidAsync(periodKey, bill, amount).ConfigureAwait(false);
                Remember(bill);
                return localId;
            }
            var billId = LegacyMapping.MapBillId(bill.Id);
            localId = MarkPaid(replica, billId, periodKey, amount, null);
            bill.Id = billId;
            RefreshRemaining(replica, bill);
        }
        _sync.NotifyLocalEdit();
        return localId;
    }

    public async Task UnmarkBillPaidAsync(string periodKey, Bill bill, double refunded)
    {
        ArgumentNullException.ThrowIfNull(bill);
        using (await _sync.EnterWriteAsync().ConfigureAwait(false))
        {
            if (_sync.Replica is not { } replica)
            {
                await _tables.UnmarkBillPaidAsync(periodKey, bill, refunded).ConfigureAwait(false);
                RememberRemaining(bill);
                return;
            }
            var billId = LegacyMapping.MapBillId(bill.Id);
            Unmark(replica, billId, periodKey);
            bill.Id = billId;
            RefreshRemaining(replica, bill);
        }
        _sync.NotifyLocalEdit();
    }

    public async Task<long> InsertPaymentAsync(Payment payment)
    {
        ArgumentNullException.ThrowIfNull(payment);
        using (await _sync.EnterWriteAsync().ConfigureAwait(false))
        {
            if (_sync.Replica is not { } replica) return await _tables.InsertPaymentAsync(payment).ConfigureAwait(false);
            var billId = LegacyMapping.MapBillId(payment.BillId);
            payment.Id = MarkPaid(replica, billId, payment.PeriodKey, payment.AmountPaid, LegacyMapping.NormalizeTimestamp(payment.PaidAt));
            payment.BillId = billId;
        }
        _sync.NotifyLocalEdit();
        return payment.Id;
    }

    public async Task<int> DeletePaymentsForBillInPeriodAsync(string periodKey, string billId)
    {
        int removed;
        using (await _sync.EnterWriteAsync().ConfigureAwait(false))
        {
            if (_sync.Replica is not { } replica) return await _tables.DeletePaymentsForBillInPeriodAsync(periodKey, billId).ConfigureAwait(false);
            removed = Unmark(replica, LegacyMapping.MapBillId(billId), periodKey);
        }
        _sync.NotifyLocalEdit();
        return removed;
    }

    /// <summary>Marks the bill's Regular payment for the period; returns the payment's local ID (0 when it is not shown).</summary>
    private long MarkPaid(SyncReplica replica, string billId, string periodKey, double amount, string? paidAt)
    {
        paidAt ??= SyncDates.FormatTimestamp(DateTimeOffset.FromUnixTimeMilliseconds(replica.NowMs));
        replica.MarkPaid(PaymentKinds.Regular, billId, periodKey, LegacyMapping.Money(amount), paidAt);
        var paymentId = Ids.SyncIds.RegularPaymentId(billId, periodKey);
        return replica.Read((_, _) => LocalIdOf(SyncEntities.Payment, paymentId)) ?? 0;
    }

    /// <summary>
    /// Today's unmark removes every payment of the bill in the period: the Regular one through the
    /// unmark rule, and any Extra payment of that period (from an old import) through deletePayment.
    /// It is <b>one</b> user action: one HLC and one transaction, each rule seeing the checkpoint the
    /// previous one raised. Returns how many payments were removed.
    /// </summary>
    private static int Unmark(SyncReplica replica, string billId, string periodKey)
    {
        var steps = new List<Func<SyncState, Clock.Hlc, IReadOnlyList<ChangeRecord>>>
        {
            (s, h) => PaymentWriter.UnmarkPaid(s, PaymentKinds.Regular, billId, periodKey, h),
        };
        var extras = replica.Read((s, _) => s.LivePaymentsFor(billId)
            .Where(p => s.PaymentKind(p) == PaymentKinds.Extra
                && s.Value(SyncEntities.Payment, p, "periodKey") is { IsString: true } k && k.StringValue == periodKey)
            .ToArray());
        foreach (var extra in extras) steps.Add((s, h) => PaymentWriter.DeletePayment(s, extra, h));
        var written = replica.CommitSteps(steps.ToArray());
        return written.Count(r => r.Entity == SyncEntities.Payment && r.Field == SyncEntities.DeletedField && r.Value.IsTrue);
    }

    private void RefreshRemaining(SyncReplica replica, Bill bill)
    {
        bill.Remaining = replica.Read((s, _) => s.Remaining(bill.Id));
        RememberRemaining(bill);
    }

    private void RememberRemaining(Bill bill)
    {
        if (_baselines.TryGetValue(bill, out var baseline)) baseline.Remaining = LegacyMapping.Money(bill.Remaining);
    }

    // ---- Snapshots ------------------------------------------------------------------------------------

    public async Task<long> InsertSnapshotAsync(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using (await _sync.EnterWriteAsync().ConfigureAwait(false))
        {
            if (_sync.Replica is not { } replica) return await _tables.InsertSnapshotAsync(snapshot).ConfigureAwait(false);
            var id = replica.NewRowId();
            replica.CreateRow(SyncEntities.BalanceSnapshot, id, new Dictionary<string, SyncValue>(StringComparer.Ordinal)
            {
                ["snapshotDate"] = SyncValue.String(snapshot.SnapshotDate ?? string.Empty),
                ["totalRemaining"] = SyncValue.Number(LegacyMapping.Money(snapshot.TotalRemaining)),
                ["details"] = SyncValue.StringOrNull(snapshot.Details),
                ["notionPageId"] = SyncValue.StringOrNull(snapshot.NotionPageId),
            });
            snapshot.Id = replica.Read((_, _) => LocalIdOf(SyncEntities.BalanceSnapshot, id)) ?? 0;
        }
        _sync.NotifyLocalEdit();
        return snapshot.Id;
    }

    public async Task<int> DeleteSnapshotAsync(long id)
    {
        int count;
        using (await _sync.EnterWriteAsync().ConfigureAwait(false))
        {
            if (_sync.Replica is not { } replica) return await _tables.DeleteSnapshotAsync(id).ConfigureAwait(false);
            var syncId = replica.Read((_, _) => SyncIdOf(SyncEntities.BalanceSnapshot, id));
            if (syncId is null) return 0;
            count = replica.DeleteRow(SyncEntities.BalanceSnapshot, syncId).Count;
        }
        _sync.NotifyLocalEdit();
        return count;
    }

    public async Task<int> DeleteAllSnapshotsAsync()
    {
        int count;
        using (await _sync.EnterWriteAsync().ConfigureAwait(false))
        {
            if (_sync.Replica is not { } replica) return await _tables.DeleteAllSnapshotsAsync().ConfigureAwait(false);
            var records = replica.Commit((s, h) => s.LiveIds(SyncEntities.BalanceSnapshot)
                .SelectMany(id => RowWriter.Delete(s, SyncEntities.BalanceSnapshot, id, h))
                .ToArray());
            count = records.Count;
        }
        _sync.NotifyLocalEdit();
        return count;
    }

    // ---- Restore --------------------------------------------------------------------------------------

    /// <summary>
    /// With sync on, a restore means "make every device look like this backup"
    /// (<c>sync-protocol.md</c> 12.3): save a backup of the current data, map the file
    /// (<see cref="LegacyMapping"/>), write it at one HLC with <see cref="RestoreWriter"/>, then apply
    /// the backup's per-device settings to this device only (not <c>SchemaVersion</c>, which
    /// describes this database, not a preference).
    /// </summary>
    public async Task ReplaceAllDataAsync(
        IReadOnlyList<Bill> bills,
        IReadOnlyList<Payment> payments,
        IReadOnlyList<Snapshot> snapshots,
        IReadOnlyDictionary<string, string?> settings)
    {
        using (await _sync.EnterWriteAsync().ConfigureAwait(false))
        {
            if (_sync.Replica is not { } replica)
            {
                await _tables.ReplaceAllDataAsync(bills, payments, snapshots, settings).ConfigureAwait(false);
                return;
            }
            ArgumentNullException.ThrowIfNull(settings);
            var rows = LegacyMapping.Map(bills, payments, snapshots);
            if (_backups is not null) await _backups.CreateAsync().ConfigureAwait(false);
            replica.Restore(ToRestoreInput(rows, settings));
            foreach (var (key, value) in settings)
            {
                if (!IsSyncedSettingKey(key) && key != "SchemaVersion") await _tables.SetSettingAsync(key, value).ConfigureAwait(false);
            }
        }
        _sync.NotifyLocalEdit();
    }

    /// <summary>A mapped backup as the restore writer's input.</summary>
    public static RestoreInput ToRestoreInput(LegacyRows rows, IReadOnlyDictionary<string, string?> settings)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var byEntity = rows.Rows.ToDictionary(
            e => e.Key,
            e => (IReadOnlyDictionary<string, IReadOnlyDictionary<string, SyncValue>>)e.Value,
            StringComparer.Ordinal);
        return new RestoreInput(byEntity, LegacyMapping.SettingsJson(settings));
    }

    // ---- Notion connector (sync-protocol.md 12.5) ---------------------------------------------------

    public NotionConnectorState GetNotionState()
    {
        if (_sync.Replica is not { } replica) return NotionConnectorState.SyncOff;
        var (owner, name) = replica.Read((s, _) =>
        {
            var o = s.Value(SyncEntities.Setting, SyncSettingKeys.NotionOwnerDeviceId, SyncEntities.ValueField) is { IsString: true } v ? v.StringValue : null;
            var n = o is not null && s.Value(SyncEntities.Device, o, "name") is { IsString: true } dn ? dn.StringValue : null;
            return (o, n);
        });
        if (owner is null) return new NotionConnectorState(NotionRole.NoOwner);
        if (owner != replica.DeviceId) return new NotionConnectorState(NotionRole.OtherDevice, owner, name);
        return new NotionConnectorState(_sync.HasNotionPushes ? NotionRole.Owner : NotionRole.OwnerMustPushFirst, owner, name);
    }

    public async Task ClaimNotionAsync()
    {
        using (await _sync.EnterWriteAsync().ConfigureAwait(false))
        {
            if (_sync.Replica is not { } replica) return;
            replica.SetSetting(SyncSettingKeys.NotionOwnerDeviceId, SyncValue.String(replica.DeviceId));
            _sync.ClearNotionLastPushed();
        }
        _sync.NotifyLocalEdit();
    }

    public async Task SaveBillFromNotionAsync(Bill pulled)
    {
        ArgumentNullException.ThrowIfNull(pulled);
        using (await _sync.EnterWriteAsync().ConfigureAwait(false))
        {
            if (_sync.Replica is not { } replica)
            {
                await _tables.UpsertBillAsync(pulled).ConfigureAwait(false);
                Remember(pulled);
                return;
            }
            SaveBill(replica, pulled, fromNotion: true);
        }
        _sync.NotifyLocalEdit();
    }

    public void RecordNotionPush(string billId, double remaining) => _sync.SetNotionLastPushed(billId, remaining);

    public string MapNotionBillId(string notionBillId) => LegacyMapping.MapBillId(notionBillId);

    // ---- Helpers --------------------------------------------------------------------------------------

    private long? LocalIdOf(string entity, string syncId) => _sync.Materializer?.LocalId(entity, syncId);

    private string? SyncIdOf(string entity, long localId) => _sync.Materializer?.SyncIdOf(entity, localId);

    private void Remember(Bill bill)
        => _baselines.AddOrUpdate(bill, new BillBaseline(LegacyMapping.EditableBillFields(bill), LegacyMapping.Money(bill.Remaining)));

    /// <summary>The values a bill object had when this service last read or wrote it.</summary>
    private sealed class BillBaseline
    {
        public BillBaseline(IReadOnlyDictionary<string, SyncValue> fields, double remaining)
        {
            Fields = fields;
            Remaining = remaining;
        }

        public IReadOnlyDictionary<string, SyncValue> Fields { get; }

        public double Remaining { get; set; }
    }
}
