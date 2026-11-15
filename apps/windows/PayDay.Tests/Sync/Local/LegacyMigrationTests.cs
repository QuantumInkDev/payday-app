using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PayDay.Models;
using PayDay.Services;
using PayDay.Sync.Ids;
using PayDay.Sync.Local;
using PayDay.Sync.Model;
using PayDay.ViewModels;

namespace PayDay.Tests.Sync.Local;

/// <summary>
/// The one-time migration of today's tables into synced records (<c>data-model.md</c> section 8): it
/// never runs on app start, writes a backup first, maps every shape of today's data, keeps every row,
/// is all-or-nothing and idempotent. Fictional data only.
/// </summary>
public sealed class LegacyMigrationTests : IDisposable
{
    private readonly SyncTestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    private static readonly string[] TodaysTables = { "Bills", "Payments", "Settings", "Snapshots", "sqlite_sequence" };

    private static string B1 => LegacyMapping.MapBillId("1");

    private static string B13 => LegacyMapping.MapBillId("13");

    private static string B7 => LegacyMapping.MapBillId("7");

    private static string B27 => LegacyMapping.MapBillId("27");

    private const string BUpper = "6f9619ff-8b86-d011-b42d-00c04fc964ff";

    private const string BLower = SyncTestEnvironment.GuidLower;

    private async Task<string> PrepareAsync()
    {
        await _env.InitializeAsync();
        _env.InsertLegacyData();
        return _env.Dump();
    }

    [Fact]
    public async Task AppStart_WithSyncOff_CreatesNoTableAndChangesNoData()
    {
        var before = await PrepareAsync();

        // What the app does on every start, then a page load through the write path.
        await _env.Db.InitializeAsync();
        _env.Sync.Load();
        using (var again = LocalSync.Open(LocalSync.ConnectionStringFor(_env.Path)))
        {
            Assert.False(again.IsOn);
        }
        var vm = new PayDayPageViewModel(_env.Routed);
        await vm.LoadAsync(SyncTestEnvironment.Today);

        Assert.False(_env.Sync.IsOn);
        Assert.Equal(SyncSettings.Off, _env.Sync.Settings);
        Assert.Equal(TodaysTables, _env.Tables());
        Assert.Equal(before, _env.Dump());
        Assert.Empty(_env.Store.WriteHistory);
    }

    [Fact]
    public async Task TurnOn_WritesAJsonBackupOfTodaysDataFirst()
    {
        await PrepareAsync();
        var result = await _env.TurnOnAsync();

        Assert.NotNull(result);
        var backupName = Assert.Single(_env.Store.WriteHistory);
        var backup = BackupSerializer.FromJson(_env.Store.Snapshot()[backupName]);
        Assert.Equal(new[] { "1", "13", "27", "6F9619FF-8B86-D011-B42D-00C04FC964FF", "7", "c56a4180-65aa-42ec-a945-5fd21dec0538" },
            backup.Bills.Select(b => b.Id).Order(StringComparer.Ordinal));
        Assert.Equal(6, backup.Payments.Count);
        Assert.Equal(3, backup.Snapshots.Count);
        Assert.Equal("Dark", backup.Settings["Theme"]);
    }

    [Fact]
    public async Task TurnOn_MapsBillIdsAndKeepsEveryBillsValues()
    {
        await PrepareAsync();
        var oldBills = (await _env.Db.GetAllBillsAsync()).ToDictionary(b => LegacyMapping.MapBillId(b.Id));
        var result = await _env.TurnOnAsync();

        Assert.Equal("ac253b5d-8ef2-5489-a207-2d390f5a7d44", B13); // spec/sync-protocol.md 3.2 example
        var bills = (await _env.Routed.GetAllBillsAsync()).ToDictionary(b => b.Id);
        Assert.Equal(new[] { B1, B13, B27, BUpper, B7, BLower }.Order(StringComparer.Ordinal), bills.Keys.Order(StringComparer.Ordinal));
        foreach (var (id, old) in oldBills)
        {
            var now = bills[id];
            Assert.Equal(old.Name, now.Name);
            Assert.Equal(old.Type, now.Type);
            Assert.Equal(old.Payment, now.Payment);
            Assert.Equal(old.Remaining, now.Remaining);
            Assert.Equal(old.Available, now.Available);
            Assert.Equal(old.CreditLimit, now.CreditLimit);
            Assert.Equal(old.DueDay, now.DueDay);
            Assert.Equal(old.Rate, now.Rate);
            Assert.Equal(old.APR, now.APR);
            Assert.Equal(old.AutoPay, now.AutoPay);
            Assert.Equal(old.Active, now.Active);
            Assert.Equal(old.YearlyDate, now.YearlyDate);
            Assert.Equal(old.Notes, now.Notes);
            Assert.Equal(old.NotionPageId, now.NotionPageId);
            Assert.Equal(old.CreatedAt, now.CreatedAt);
            Assert.Equal("2025-05-22 12:00:01", now.UpdatedAt);
        }

        // The shapes the fixture covers survive: an inactive bill, Installments, a custom type, a yearly bill.
        Assert.False(bills[B7].Active);
        Assert.Equal("Installments", bills[BUpper].Type);
        Assert.Equal("Fitness", bills[BLower].Type);
        Assert.Equal("03-15", bills[B27].YearlyDate);

        // Synced values: legacy names mapped, createdAt reformatted, Remaining as the checkpoint.
        Assert.Equal(SyncValue.Number(910.25), _env.Value(SyncEntities.Bill, BUpper, "remainingCheckpoint"));
        Assert.Equal(SyncValue.Number(45.5), _env.Value(SyncEntities.Bill, BUpper, "payment"));
        Assert.Equal(SyncValue.Number(24.99), _env.Value(SyncEntities.Bill, B1, "apr"));
        Assert.Equal(SyncValue.String("2025-03-01T10:00:00.000Z"), _env.Value(SyncEntities.Bill, B1, "createdAt"));
        Assert.Equal(SyncValue.Null, _env.Value(SyncEntities.Bill, BLower, "createdAt"));
        Assert.Equal(SyncValue.Null, _env.Value(SyncEntities.Bill, B1, "targetPayoffDate"));
        Assert.Equal(SyncValue.False, _env.Value(SyncEntities.Bill, B1, "deleted"));
        Assert.Equal(6, result!.Bills);
    }

    [Fact]
    public async Task TurnOn_MapsPayments_KeepingLocalIds_AndDuplicatesBecomeExtras()
    {
        await PrepareAsync();
        await _env.TurnOnAsync();

        var payments = (await _env.Routed.GetAllPaymentsAsync()).ToDictionary(p => p.Id);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 7 }, payments.Keys.Order());
        Assert.Equal(B1, payments[1].BillId);
        Assert.Equal(BUpper, payments[5].BillId);
        Assert.Equal(new[] { 55.0, 120, 55, 20, 45.5, 30 }, new long[] { 1, 2, 3, 4, 5, 7 }.Select(i => payments[i].AmountPaid));
        Assert.Equal("2025-05-02T15:04:05.000Z", payments[1].PaidAt);
        Assert.Equal("2025-05-02T15:04:05.123Z", payments[2].PaidAt);
        Assert.Null(payments[4].PaidAt);
        Assert.Equal("2025-05-16T08:00:00.000Z", payments[5].PaidAt);
        Assert.Equal("2025-05-16T13:00:00.000Z", payments[7].PaidAt);
        Assert.Equal("fictional-notion-payment-2", payments[2].NotionPageId);

        var regular = SyncIds.RegularPaymentId(B1, "2025-05-01");
        var duplicate = SyncIds.LegacyPaymentId(B1, "2025-05-01", 5500, "2025-05-02T15:04:05.000Z", 2);
        var noPaidAt = SyncIds.LegacyPaymentId(B1, "2025-05-01", 2000, "", 1);
        Assert.Equal(SyncValue.String(PaymentKinds.Regular), _env.Value(SyncEntities.Payment, regular, "kind"));
        Assert.Equal(SyncValue.String(PaymentKinds.Extra), _env.Value(SyncEntities.Payment, duplicate, "kind"));
        Assert.Equal(SyncValue.String(PaymentKinds.Extra), _env.Value(SyncEntities.Payment, noPaidAt, "kind"));
        Assert.Equal(SyncValue.Null, _env.Value(SyncEntities.Payment, noPaidAt, "paidAt"));
        Assert.Equal(1, _env.Sync.Materializer!.LocalId(SyncEntities.Payment, regular) is { } r1 ? r1 : -1);
        Assert.Equal(3, _env.Sync.Replica!.Read((_, _) => _env.Sync.Materializer!.LocalId(SyncEntities.Payment, duplicate)));
        Assert.Equal(SyncValue.String(BUpper), _env.Value(SyncEntities.Payment, SyncIds.RegularPaymentId(BUpper, "2025-05-15"), "billId"));
    }

    [Fact]
    public async Task TurnOn_MapsSnapshots_WithContentIds_AndRewritesDetailsKeys()
    {
        await PrepareAsync();
        await _env.TurnOnAsync();

        var snapshots = (await _env.Routed.GetAllSnapshotsAsync()).ToDictionary(s => s.Id);
        Assert.Equal(new long[] { 1, 2, 3 }, snapshots.Keys.Order());
        Assert.Equal($"{{\"{B1}\":1200.00,\"{BUpper}\":910.25}}", snapshots[1].Details);
        Assert.Null(snapshots[2].Details);
        Assert.Equal("not json", snapshots[3].Details);
        Assert.Equal("fictional-notion-snapshot-2", snapshots[2].NotionPageId);
        Assert.Equal(3000.5, snapshots[2].TotalRemaining);

        var first = SyncIds.LegacySnapshotId("2025-05-01", 300050, 1);
        var second = SyncIds.LegacySnapshotId("2025-05-01", 300050, 2);
        Assert.Equal(SyncValue.String("2025-05-01"), _env.Value(SyncEntities.BalanceSnapshot, first, "snapshotDate"));
        Assert.Equal(SyncValue.String("fictional-notion-snapshot-2"), _env.Value(SyncEntities.BalanceSnapshot, second, "notionPageId"));
        Assert.Equal(SyncValue.False, _env.Value(SyncEntities.BalanceSnapshot, SyncIds.LegacySnapshotId("2025-05-15", 280000, 1), "deleted"));
    }

    [Fact]
    public async Task TurnOn_MapsSettings_AndLeavesPerDeviceKeysAlone()
    {
        await PrepareAsync();
        var schemaVersion = await _env.Db.GetSettingAsync("SchemaVersion");
        await _env.TurnOnAsync();

        Assert.Equal(SyncValue.String("2025-03-20"), _env.Value(SyncEntities.Setting, SyncSettingKeys.PayAnchor, "value"));
        Assert.Equal(SyncValue.False, _env.Value(SyncEntities.Setting, SyncSettingKeys.EarlyStart, "value"));
        Assert.Equal(SyncValue.String("#FD79A8"), _env.Value(SyncEntities.Setting, "TypeColor/Cards", "value"));
        Assert.Equal(SyncValue.String("#00B894"), _env.Value(SyncEntities.Setting, "TypeColor/Fitness", "value"));
        Assert.Null(_env.Value(SyncEntities.Setting, "TypeColor/Bad", "value"));
        Assert.Equal(SyncValue.String("00000000-0000-0000-0000-000000000102"), _env.Value(SyncEntities.Setting, SyncSettingKeys.NotionBillsDb, "value"));
        Assert.Null(_env.Value(SyncEntities.Setting, SyncSettingKeys.NotionOwnerDeviceId, "value"));
        Assert.Null(_env.Value(SyncEntities.Setting, "Theme", "value"));
        Assert.Null(_env.Value(SyncEntities.Setting, "LastNotionSync", "value"));

        var table = await _env.Routed.GetAllSettingsAsync();
        Assert.Equal("2025-03-20", table["PayAnchor"]);
        Assert.Equal("false", table["EarlyStart"]);
        Assert.Equal("{\"Cards\":\"#FD79A8\",\"Fitness\":\"#00B894\"}", table["TypeColors"]);
        Assert.Equal("00000000-0000-0000-0000-000000000302", table["NotionSnapshotsDb"]);
        Assert.Equal("Dark", table["Theme"]);
        Assert.Equal("2025-05-20T12:00:00.0000000Z", table["LastNotionSync"]);
        Assert.Equal(schemaVersion, table["SchemaVersion"]);
    }

    [Fact]
    public async Task TurnOn_StampsEveryRecordWithOneHlc_QueuesThem_AndPaymentsAreAbsorbed()
    {
        await PrepareAsync();
        var result = await _env.TurnOnAsync();

        var replica = _env.Sync.Replica!;
        var records = replica.Read((s, _) => s.AllRecords());
        Assert.Equal(result!.Records, records.Count);
        Assert.All(records, r => Assert.Equal(result.Hlc, r.Hlc));
        Assert.Equal(replica.DeviceId, result.Hlc.DeviceId);
        Assert.Equal(result.Hlc, replica.Metadata.ClockState);
        Assert.Equal(records.Count, replica.Metadata.PendingRecords.Count);
        Assert.Equal(new SyncSettings(true, replica.DeviceId), _env.Sync.Settings);

        // Strictly-after: migrated payments are absorbed by the migrated checkpoints (8.1).
        Assert.Equal(120000, replica.Read((s, _) => s.RemainingCents(B1)));
        Assert.Equal(91025, replica.Read((s, _) => s.RemainingCents(BUpper)));
    }

    [Fact]
    public async Task TurnOn_IsIdempotent_AndDoesNothingWhileOn()
    {
        var original = await PrepareAsync();
        await _env.TurnOnAsync();
        var tables = _env.Dump("UpdatedAt");
        var values = _env.RecordValues();
        var device = _env.Sync.Replica!.DeviceId;

        Assert.Null(await _env.TurnOnAsync());
        Assert.Single(_env.Store.WriteHistory);
        Assert.Equal(tables, _env.Dump("UpdatedAt"));

        // Off and on again: a fresh device ID, the same rows, IDs and values.
        _env.Sync.TurnOff();
        Assert.False(_env.Sync.IsOn);
        Assert.Equal(tables, _env.Dump("UpdatedAt"));
        await _env.TurnOnAsync();
        Assert.NotEqual(device, _env.Sync.Replica!.DeviceId);
        Assert.Equal(tables, _env.Dump("UpdatedAt"));
        Assert.Equal(values, _env.RecordValues());
        Assert.NotEqual(original, tables);
    }

    [Fact]
    public async Task TurnOn_RollsBackEverything_WhenARowCannotBeMapped()
    {
        await PrepareAsync();
        _env.Exec("INSERT INTO Payments (Id, BillId, PeriodKey, AmountPaid) VALUES (9, '13', 'not-a-date', 1);");
        var before = _env.Dump();

        var error = await Assert.ThrowsAsync<LegacyMigrationException>(_env.TurnOnAsync);
        Assert.Contains("Payment 9", error.Message, StringComparison.Ordinal);
        Assert.False(_env.Sync.IsOn);
        Assert.Equal(before, _env.Dump());
        Assert.Equal(TodaysTables, _env.Tables());
        using var reopened = LocalSync.Open(LocalSync.ConnectionStringFor(_env.Path));
        Assert.False(reopened.IsOn);
    }

    [Fact]
    public async Task TurnOn_RefusesTwoBillsThatWouldMerge()
    {
        await PrepareAsync();
        _env.Exec("INSERT INTO Bills (Id, Name, Type) VALUES ('c56a4180-65aa-42ec-a945-5FD21DEC0538', 'Fabrikam Gym copy', 'Fitness');");
        var before = _env.Dump();

        await Assert.ThrowsAsync<LegacyMigrationException>(_env.TurnOnAsync);
        Assert.Equal(before, _env.Dump());
        Assert.Equal(TodaysTables, _env.Tables());
    }

    [Fact]
    public async Task TurnOn_DoesNothing_WhenTheBackupFails()
    {
        var before = await PrepareAsync();
        var failing = new BackupRotationService(_env.Db, new FailingBackupStore());

        await Assert.ThrowsAsync<System.IO.IOException>(() => _env.Sync.TurnOnAsync(failing));
        Assert.False(_env.Sync.IsOn);
        Assert.Equal(before, _env.Dump());
        Assert.Equal(TodaysTables, _env.Tables());
    }

    [Fact]
    public async Task TurnOn_MigratesASchemaV1Database_OnceTheAppHasUpgradedIt()
    {
        // A database from before the Cost→Payment / Owed→Remaining rename (schema v1).
        _env.Exec("""
            CREATE TABLE Bills (Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Type TEXT NOT NULL, Cost REAL NOT NULL DEFAULT 0,
                Owed REAL DEFAULT 0, Available REAL DEFAULT 0, CreditLimit REAL DEFAULT 0, DueDay INTEGER DEFAULT 1,
                Rate TEXT DEFAULT 'Monthly', APR REAL DEFAULT 0, AutoPay INTEGER DEFAULT 0, Active INTEGER DEFAULT 1,
                YearlyDate TEXT, Notes TEXT, CreatedAt TEXT DEFAULT (datetime('now')), UpdatedAt TEXT DEFAULT (datetime('now')), NotionPageId TEXT);
            CREATE TABLE Snapshots (Id INTEGER PRIMARY KEY AUTOINCREMENT, SnapshotDate TEXT NOT NULL, TotalOwed REAL NOT NULL, Details TEXT, NotionPageId TEXT);
            CREATE TABLE Payments (Id INTEGER PRIMARY KEY AUTOINCREMENT, BillId TEXT NOT NULL REFERENCES Bills(Id), PeriodKey TEXT NOT NULL,
                AmountPaid REAL NOT NULL, PaidAt TEXT DEFAULT (datetime('now')), NotionPageId TEXT);
            CREATE TABLE Settings (Key TEXT PRIMARY KEY, Value TEXT);
            INSERT INTO Settings (Key, Value) VALUES ('SchemaVersion', '1');
            INSERT INTO Bills (Id, Name, Type, Cost, Owed, DueDay) VALUES ('4', 'Tailspin – Electronics', 'Cards', 110, 3200, 25);
            INSERT INTO Snapshots (SnapshotDate, TotalOwed) VALUES ('2025-04-01', 3200);
            """);
        var v1 = _env.Dump();

        // Sync refuses the v1 shape and changes nothing.
        await Assert.ThrowsAsync<LegacyMigrationException>(_env.TurnOnAsync);
        Assert.Equal(v1, _env.Dump());

        // The app's start-up migration renames the columns; then sync carries the legacy values over.
        await _env.InitializeAsync();
        await _env.TurnOnAsync();
        var bill = Assert.Single(await _env.Routed.GetAllBillsAsync());
        Assert.Equal(LegacyMapping.MapBillId("4"), bill.Id);
        Assert.Equal(110, bill.Payment);
        Assert.Equal(3200, bill.Remaining);
        Assert.Equal(SyncValue.Number(110), _env.Value(SyncEntities.Bill, bill.Id, "payment"));
        Assert.Equal(SyncValue.Number(3200), _env.Value(SyncEntities.Bill, bill.Id, "remainingCheckpoint"));
        Assert.Equal(3200, Assert.Single(await _env.Routed.GetAllSnapshotsAsync()).TotalRemaining);
    }

    [Fact]
    public async Task TodaysPages_ReadTheMigratedTables()
    {
        await PrepareAsync();
        await _env.TurnOnAsync();

        var payDay = new PayDayPageViewModel(_env.Routed);
        await payDay.LoadAsync(SyncTestEnvironment.Today);
        Assert.Equal(SyncTestEnvironment.CurrentPeriod, payDay.CurrentPeriodKey);
        var paid = Assert.Single(payDay.PaidBills);
        Assert.Equal("Contoso Phone Plan", paid.Bill.Name);
        Assert.Equal(45.5, paid.AmountPaid);
        Assert.Equal(5, paid.PaymentId);
        Assert.Contains(payDay.UnpaidBills, r => r.Bill.Name == "Woodgrove Rewards");

        var allBills = new AllBillsPageViewModel(_env.Routed);
        await allBills.LoadAsync();
        Assert.Equal(6, allBills.TotalBills);
        Assert.Contains(allBills.Groups, g => g.Key == "Installments");
        Assert.Contains(allBills.Groups, g => g.Key == "Fitness");

        var insights = new InsightsPageViewModel(_env.Routed);
        await insights.LoadAsync();
        Assert.Equal(3, insights.SnapshotCount);
        Assert.Equal(1200 + 910.25, insights.CurrentTotalRemaining);
    }

    [Fact]
    public async Task Seeding_IsSkipped_WhileSyncIsOn()
    {
        var seed = new[] { new Bill { Id = "1", Name = "Woodgrove Rewards", Type = "Cards", Payment = 55 } };
        var seeded = new SqliteDatabaseService(_env.Path, seed);
        await seeded.InitializeAsync();
        Assert.Equal(1, _env.Count("Bills"));
        await _env.TurnOnAsync();

        // Delete every bill through sync, then start the app again: the empty table stays empty.
        var replica = _env.Sync.Replica!;
        foreach (var id in replica.Read((s, _) => s.LiveIds(SyncEntities.Bill))) replica.DeleteRow(SyncEntities.Bill, id);
        Assert.Equal(0, _env.Count("Bills"));
        await seeded.InitializeAsync();
        Assert.Equal(0, _env.Count("Bills"));

        // With sync off an empty table is seeded, as today.
        _env.Sync.TurnOff();
        await seeded.InitializeAsync();
        Assert.Equal(1, _env.Count("Bills"));
    }

    private sealed class FailingBackupStore : IBackupStore
    {
        public Task WriteAsync(string fileName, string content, CancellationToken ct = default) => throw new System.IO.IOException("disk full");

        public Task<IReadOnlyList<BackupEntry>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<BackupEntry>>(Array.Empty<BackupEntry>());

        public Task<string> ReadAsync(string fileName, CancellationToken ct = default) => throw new System.IO.FileNotFoundException(fileName);

        public Task DeleteAsync(string fileName, CancellationToken ct = default) => Task.CompletedTask;
    }
}
