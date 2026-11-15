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
/// "Fields that changed" are measured against the values the bill object had when it was read through
/// this service, so saving a page's copy of a bill never reverts a field another device changed in the
/// meantime unless this device edited that field too.
/// </para>
/// </summary>
public sealed class SyncRoutingDatabaseService : IDatabaseService
{
    private readonly IDatabaseService _tables;
    private readonly LocalSync _sync;
    private readonly BackupRotationService? _backups;
    private readonly ConditionalWeakTable<Bill, BillBaseline> _baselines = new();

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

    public Task<string?> GetSettingAsync(string key) => _tables.GetSettingAsync(key);

    public async Task<IReadOnlyList<Bill>> GetAllBillsAsync()
    {
        var bills = await _tables.GetAllBillsAsync().ConfigureAwait(false);
        if (_sync.IsOn)
        {
            foreach (var bill in bills) Remember(bill);
        }
        return bills;
    }

    public Task<IReadOnlyList<Payment>> GetPaymentsByPeriodAsync(string periodKey) => _tables.GetPaymentsByPeriodAsync(periodKey);

    public Task<IReadOnlyList<Payment>> GetAllPaymentsAsync() => _tables.GetAllPaymentsAsync();

    public Task<IReadOnlyList<Snapshot>> GetAllSnapshotsAsync() => _tables.GetAllSnapshotsAsync();

    public Task<IReadOnlyDictionary<string, string?>> GetAllSettingsAsync() => _tables.GetAllSettingsAsync();

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

    public Task SetSettingAsync(string key, string? value)
    {
        if (_sync.Replica is not { } replica || !IsSyncedSettingKey(key)) return _tables.SetSettingAsync(key, value);
        replica.Commit((s, h) => SettingRecords(s, key, value, h));
        return Task.CompletedTask;
    }

    private static IReadOnlyList<ChangeRecord> SettingRecords(SyncState s, string key, string? value, Clock.Hlc h)
    {
        var output = new List<ChangeRecord>();
        if (key == TypeColorService.SettingKey)
        {
            // Today's whole-map value becomes one TypeColor/<type> row per type; only changed types are written.
            var legacy = JsonData.Object((BackupSettings.LegacyTypeColorsKey, value is null ? JsonData.Null : JsonData.String(value)));
            var desired = BackupSettings.MapForRestore(legacy);
            foreach (var (k, v) in desired) output.AddRange(RowWriter.SetSetting(s, k, v, h));
            foreach (var k in s.Ids(SyncEntities.Setting))
            {
                if (k.StartsWith(SyncSettingKeys.TypeColorPrefix, StringComparison.Ordinal) && !desired.ContainsKey(k))
                {
                    output.AddRange(RowWriter.SetSetting(s, k, SyncValue.Null, h));
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

    // ---- Bills ----------------------------------------------------------------------------------------

    public Task UpsertBillAsync(Bill bill)
    {
        ArgumentNullException.ThrowIfNull(bill);
        if (_sync.Replica is not { } replica) return _tables.UpsertBillAsync(bill);
        SaveBill(replica, bill);
        return Task.CompletedTask;
    }

    private void SaveBill(SyncReplica replica, Bill bill)
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
        else
        {
            var baseline = _baselines.TryGetValue(bill, out var b) ? b : null;
            replica.Commit((s, h) => BillEditRecords(s, id, bill, baseline, h));
        }
        bill.Id = id;
        bill.Remaining = replica.Read((s, _) => s.Remaining(id));
        Remember(bill);
    }

    private static IReadOnlyList<ChangeRecord> BillEditRecords(SyncState s, string id, Bill bill, BillBaseline? baseline, Clock.Hlc h)
    {
        var desired = LegacyMapping.EditableBillFields(bill);
        var changes = baseline is null
            ? desired
            : desired.Where(kv => !baseline.Fields.TryGetValue(kv.Key, out var before) || before != kv.Value)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
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
                output.AddRange(PaymentWriter.SetRemaining(s, id, LegacyMapping.Money(remaining), RemainingSource.App, null, h));
            }
            else
            {
                output.Add(new ChangeRecord(SyncEntities.Bill, id, "remainingCheckpoint", SyncValue.Number(LegacyMapping.Money(remaining)), h));
            }
        }
        return ChangeRecord.SortByKey(output);
    }

    // ---- Payments -------------------------------------------------------------------------------------

    public Task<long> MarkBillPaidAsync(string periodKey, Bill bill, double amount)
    {
        ArgumentNullException.ThrowIfNull(bill);
        if (_sync.Replica is not { } replica) return _tables.MarkBillPaidAsync(periodKey, bill, amount);
        var billId = LegacyMapping.MapBillId(bill.Id);
        var localId = MarkPaid(replica, billId, periodKey, amount, null);
        bill.Id = billId;
        RefreshRemaining(replica, bill);
        return Task.FromResult(localId);
    }

    public Task UnmarkBillPaidAsync(string periodKey, Bill bill, double refunded)
    {
        ArgumentNullException.ThrowIfNull(bill);
        if (_sync.Replica is not { } replica) return _tables.UnmarkBillPaidAsync(periodKey, bill, refunded);
        var billId = LegacyMapping.MapBillId(bill.Id);
        Unmark(replica, billId, periodKey);
        bill.Id = billId;
        RefreshRemaining(replica, bill);
        return Task.CompletedTask;
    }

    public Task<long> InsertPaymentAsync(Payment payment)
    {
        ArgumentNullException.ThrowIfNull(payment);
        if (_sync.Replica is not { } replica) return _tables.InsertPaymentAsync(payment);
        var billId = LegacyMapping.MapBillId(payment.BillId);
        payment.Id = MarkPaid(replica, billId, payment.PeriodKey, payment.AmountPaid, LegacyMapping.NormalizeTimestamp(payment.PaidAt));
        payment.BillId = billId;
        return Task.FromResult(payment.Id);
    }

    public Task<int> DeletePaymentsForBillInPeriodAsync(string periodKey, string billId)
    {
        if (_sync.Replica is not { } replica) return _tables.DeletePaymentsForBillInPeriodAsync(periodKey, billId);
        return Task.FromResult(Unmark(replica, LegacyMapping.MapBillId(billId), periodKey));
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
    /// Returns how many payments were removed.
    /// </summary>
    private static int Unmark(SyncReplica replica, string billId, string periodKey)
    {
        var removed = replica.UnmarkPaid(PaymentKinds.Regular, billId, periodKey).Count > 0 ? 1 : 0;
        var extras = replica.Read((s, _) => s.LivePaymentsFor(billId)
            .Where(p => s.PaymentKind(p) == PaymentKinds.Extra
                && s.Value(SyncEntities.Payment, p, "periodKey") is { IsString: true } k && k.StringValue == periodKey)
            .ToArray());
        foreach (var extra in extras)
        {
            if (replica.DeletePayment(extra).Count > 0) removed++;
        }
        return removed;
    }

    private void RefreshRemaining(SyncReplica replica, Bill bill)
    {
        bill.Remaining = replica.Read((s, _) => s.Remaining(bill.Id));
        if (_baselines.TryGetValue(bill, out var baseline)) baseline.Remaining = LegacyMapping.Money(bill.Remaining);
    }

    // ---- Snapshots ------------------------------------------------------------------------------------

    public Task<long> InsertSnapshotAsync(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (_sync.Replica is not { } replica) return _tables.InsertSnapshotAsync(snapshot);
        var id = replica.NewRowId();
        replica.CreateRow(SyncEntities.BalanceSnapshot, id, new Dictionary<string, SyncValue>(StringComparer.Ordinal)
        {
            ["snapshotDate"] = SyncValue.String(snapshot.SnapshotDate ?? string.Empty),
            ["totalRemaining"] = SyncValue.Number(LegacyMapping.Money(snapshot.TotalRemaining)),
            ["details"] = SyncValue.StringOrNull(snapshot.Details),
            ["notionPageId"] = SyncValue.StringOrNull(snapshot.NotionPageId),
        });
        snapshot.Id = replica.Read((_, _) => LocalIdOf(SyncEntities.BalanceSnapshot, id)) ?? 0;
        return Task.FromResult(snapshot.Id);
    }

    public Task<int> DeleteSnapshotAsync(long id)
    {
        if (_sync.Replica is not { } replica) return _tables.DeleteSnapshotAsync(id);
        var syncId = replica.Read((_, _) => SyncIdOf(SyncEntities.BalanceSnapshot, id));
        if (syncId is null) return Task.FromResult(0);
        return Task.FromResult(replica.DeleteRow(SyncEntities.BalanceSnapshot, syncId).Count);
    }

    public Task<int> DeleteAllSnapshotsAsync()
    {
        if (_sync.Replica is not { } replica) return _tables.DeleteAllSnapshotsAsync();
        var records = replica.Commit((s, h) => s.LiveIds(SyncEntities.BalanceSnapshot)
            .SelectMany(id => RowWriter.Delete(s, SyncEntities.BalanceSnapshot, id, h))
            .ToArray());
        return Task.FromResult(records.Count);
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
