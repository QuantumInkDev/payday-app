using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PayDay.Models;
using PayDay.Services;
using PayDay.Sync.Clock;
using PayDay.Sync.Engine;
using PayDay.Sync.Ids;
using PayDay.Sync.Local;
using PayDay.Sync.Merge;
using PayDay.Sync.Model;
using PayDay.ViewModels;

namespace PayDay.Tests.Sync.Local;

/// <summary>
/// The write path: with sync off the app's view-models behave exactly as on the plain tables; with
/// sync on their writes become sync actions (payment writer rules, row edits, setRemaining, restore)
/// and the tables follow the merged state, including changes merged from another device.
/// </summary>
public sealed class SyncWritePathTests : IDisposable
{
    private const string OtherDevice = "0b0b0b0b0b0b0b0b";

    private readonly SyncTestEnvironment _env = new();

    public void Dispose() => _env.Dispose();

    private static string B1 => LegacyMapping.MapBillId("1");

    private const string BUpper = "6f9619ff-8b86-d011-b42d-00c04fc964ff";

    private async Task OnAsync()
    {
        await _env.InitializeAsync();
        _env.InsertLegacyData();
        await _env.TurnOnAsync();
    }

    private SyncReplica Replica => _env.Sync.Replica!;

    private IReadOnlyList<ChangeRecord> Pending() => Replica.Read((_, m) => m.PendingRecords.ToList());

    /// <summary>Records written after the migration's own records.</summary>
    private IReadOnlyList<ChangeRecord> Written(int afterMigration) => Pending().Skip(afterMigration).ToList();

    private ChangeRecord Remote(string entity, string id, string field, SyncValue value, long laterMs = 60_000)
        => new(entity, id, field, value, Hlc.Encode(_env.Time.UnixMs + laterMs, 0, OtherDevice));

    // ---- Sync off: exactly today's behaviour --------------------------------------------------------

    [Fact]
    public async Task SyncOff_EveryViewModelWriteGivesTheSameTablesAsTheTablesAlone()
    {
        using var plain = new SyncTestEnvironment();
        foreach (var env in new[] { plain, _env })
        {
            await env.InitializeAsync();
            env.InsertLegacyData();
        }

        async Task Act(IDatabaseService db)
        {
            var payDay = new PayDayPageViewModel(db);
            await payDay.LoadAsync(SyncTestEnvironment.Today);
            await payDay.MarkAllPaidCommand.ExecuteAsync(null);
            await payDay.UnmarkPaidCommand.ExecuteAsync(payDay.PaidBills.First(r => r.Bill.Name == "Woodgrove Rewards"));
            await payDay.MarkPaidCommand.ExecuteAsync(payDay.UnpaidBills.First());

            var allBills = new AllBillsPageViewModel(db);
            await allBills.LoadAsync();
            var bill = allBills.Groups.SelectMany(g => g.Bills).First(b => b.Name == "Electric");
            bill.Name = "Electric & Gas";
            bill.Remaining = 75;
            await allBills.SaveBillAsync(bill);
            await allBills.SaveBillAsync(new Bill { Id = "new-bill", Name = "Proseware Card", Type = "Cards", Payment = 40 });

            var insights = new InsightsPageViewModel(db);
            var snap = await insights.SaveSnapshotAsync(SyncTestEnvironment.Today);
            await insights.DeleteSnapshotAsync(1);
            Assert.True(snap > 0);

            await new PayPeriodService(db).SetPayAnchorAsync(new DateTime(2025, 3, 6));
            await db.SetSettingAsync("Theme", "Light");
        }

        await Act(plain.Db);
        await Act(_env.Routed);

        Assert.False(_env.Sync.IsOn);
        Assert.Equal(plain.Dump("PaidAt", "UpdatedAt", "CreatedAt"), _env.Dump("PaidAt", "UpdatedAt", "CreatedAt"));
        Assert.Equal(new[] { "Bills", "Payments", "Settings", "Snapshots", "sqlite_sequence" }, _env.Tables());
    }

    // ---- Mark / unmark --------------------------------------------------------------------------------

    [Fact]
    public async Task MarkPaid_WritesTheRegularPayment_AndRemainingIsComputedNotStored()
    {
        await OnAsync();
        var migrated = Pending().Count;
        var vm = new PayDayPageViewModel(_env.Routed);
        await vm.LoadAsync(SyncTestEnvironment.Today);
        var row = vm.UnpaidBills.Single(r => r.Bill.Name == "Woodgrove Rewards");

        _env.Time.Advance(1000);
        await vm.MarkPaidCommand.ExecuteAsync(row);

        var pid = SyncIds.RegularPaymentId(B1, SyncTestEnvironment.CurrentPeriod);
        var written = Written(migrated);
        Assert.Equal(new[] { "amountPaid", "billId", "deleted", "kind", "paidAt", "periodKey" }, written.Select(r => r.Field));
        Assert.All(written, r => Assert.Equal(pid, r.Id));
        Assert.Single(written.Select(r => r.Hlc).Distinct());
        Assert.Equal(SyncValue.String("2025-05-22T12:00:02.000Z"), _env.Value(SyncEntities.Payment, pid, "paidAt"));

        Assert.Equal(1145, row.Bill.Remaining);
        Assert.True(row.PaymentId > 7);
        var table = (await _env.Db.GetAllBillsAsync()).Single(b => b.Id == B1);
        Assert.Equal(1145, table.Remaining);
        Assert.Contains(await _env.Db.GetPaymentsByPeriodAsync(SyncTestEnvironment.CurrentPeriod), p => p.Id == row.PaymentId && p.BillId == B1 && p.AmountPaid == 55);
        Assert.Equal(SyncValue.Number(1200), _env.Value(SyncEntities.Bill, B1, "remainingCheckpoint"));

        // Unmark gives it back; marking again reuses the same row and the same local ID.
        var localId = row.PaymentId;
        _env.Time.Advance(1000);
        await vm.UnmarkPaidCommand.ExecuteAsync(row);
        Assert.Equal(SyncValue.True, _env.Value(SyncEntities.Payment, pid, "deleted"));
        Assert.Equal(1200, (await _env.Db.GetAllBillsAsync()).Single(b => b.Id == B1).Remaining);
        Assert.DoesNotContain(await _env.Db.GetAllPaymentsAsync(), p => p.Id == localId);
        _env.Time.Advance(1000);
        await vm.MarkPaidCommand.ExecuteAsync(row);
        Assert.Equal(localId, row.PaymentId);
        Assert.Equal(1145, row.Bill.Remaining);
    }

    [Fact]
    public async Task UnmarkingAMigratedPayment_RaisesTheCheckpoint()
    {
        await OnAsync();
        var vm = new PayDayPageViewModel(_env.Routed);
        await vm.LoadAsync(SyncTestEnvironment.Today);
        var row = vm.PaidBills.Single(r => r.Bill.Id == BUpper);
        Assert.Equal(910.25, row.Bill.Remaining);

        _env.Time.Advance(1000);
        await vm.UnmarkPaidCommand.ExecuteAsync(row);

        // The migrated payment was absorbed by the migrated checkpoint, so unmarking raises it (5.3).
        Assert.Equal(SyncValue.Number(955.75), _env.Value(SyncEntities.Bill, BUpper, "remainingCheckpoint"));
        Assert.Equal(955.75, row.Bill.Remaining);
        Assert.Equal(955.75, (await _env.Db.GetAllBillsAsync()).Single(b => b.Id == BUpper).Remaining);
        Assert.DoesNotContain(await _env.Db.GetAllPaymentsAsync(), p => p.Id == 5);
    }

    [Fact]
    public async Task UnmarkingAPeriodWithDuplicateMigratedPayments_RemovesThemAll()
    {
        await OnAsync();
        Assert.Equal(3, await _env.Routed.DeletePaymentsForBillInPeriodAsync("2025-05-01", B1));
        Assert.DoesNotContain(await _env.Db.GetAllPaymentsAsync(), p => p.BillId == B1);
        // 55 + 55 + 20 were all absorbed: the checkpoint goes up by all three.
        Assert.Equal(1330, (await _env.Db.GetAllBillsAsync()).Single(b => b.Id == B1).Remaining);
    }

    [Fact]
    public async Task MarkAllPaid_MarksEveryUnpaidBillThroughThePaymentWriter()
    {
        await OnAsync();
        var vm = new PayDayPageViewModel(_env.Routed);
        await vm.LoadAsync(SyncTestEnvironment.Today);
        var unpaid = vm.UnpaidBills.Select(r => r.Bill.Id).ToList();
        Assert.NotEmpty(unpaid);

        _env.Time.Advance(1000);
        await vm.MarkAllPaidCommand.ExecuteAsync(null);

        Assert.Empty(vm.UnpaidBills);
        foreach (var bill in unpaid)
        {
            Assert.Equal(SyncValue.False, _env.Value(SyncEntities.Payment, SyncIds.RegularPaymentId(bill, SyncTestEnvironment.CurrentPeriod), "deleted"));
        }
        var paidIds = (await _env.Db.GetPaymentsByPeriodAsync(SyncTestEnvironment.CurrentPeriod)).Select(p => p.BillId).ToHashSet();
        Assert.Superset(unpaid.ToHashSet(), paidIds);
    }

    // ---- Bills ----------------------------------------------------------------------------------------

    [Fact]
    public async Task SaveBill_WritesOnlyTheFieldsThatChanged_AndRemainingThroughSetRemaining()
    {
        await OnAsync();
        var vm = new AllBillsPageViewModel(_env.Routed);
        await vm.LoadAsync();
        var bill = vm.Groups.SelectMany(g => g.Bills).Single(b => b.Id == B1);

        var before = Pending().Count;
        _env.Time.Advance(1000);
        await vm.SaveBillAsync(bill);
        Assert.Equal(before, Pending().Count);

        bill.Name = "Woodgrove Travel";
        _env.Time.Advance(1000);
        await vm.SaveBillAsync(bill);
        var renamed = Assert.Single(Written(before));
        Assert.Equal(("name", SyncValue.String("Woodgrove Travel")), (renamed.Field, renamed.Value));

        bill.Remaining = 1000;
        bill.Active = false;
        _env.Time.Advance(1000);
        await vm.SaveBillAsync(bill);
        var edited = Written(before + 1);
        Assert.Equal(new[] { "active", "remainingCheckpoint" }, edited.Select(r => r.Field));
        Assert.Single(edited.Select(r => r.Hlc).Distinct());

        var table = (await _env.Db.GetAllBillsAsync()).Single(b => b.Id == B1);
        Assert.Equal(("Woodgrove Travel", 1000.0, false), (table.Name, table.Remaining, table.Active));
    }

    [Fact]
    public async Task SaveBill_NeverRevertsAFieldAnotherDeviceChanged()
    {
        await OnAsync();
        var vm = new AllBillsPageViewModel(_env.Routed);
        await vm.LoadAsync();
        var stale = vm.Groups.SelectMany(g => g.Bills).Single(b => b.Id == B1);

        // Another device renames the bill; this page still shows the old name.
        Replica.MergeRecords(new[] { Remote(SyncEntities.Bill, B1, "name", SyncValue.String("Woodgrove Platinum")) });
        Assert.Equal("Woodgrove Platinum", (await _env.Db.GetAllBillsAsync()).Single(b => b.Id == B1).Name);
        Assert.Equal("Woodgrove Rewards", stale.Name);

        _env.Time.Advance(120_000);
        stale.Active = false;
        await vm.SaveBillAsync(stale);

        var table = (await _env.Db.GetAllBillsAsync()).Single(b => b.Id == B1);
        Assert.Equal("Woodgrove Platinum", table.Name);
        Assert.False(table.Active);
    }

    [Fact]
    public async Task AddingABill_CreatesARowWithANewUuidV7()
    {
        await OnAsync();
        var vm = new AllBillsPageViewModel(_env.Routed);
        var bill = new Bill { Id = Guid.NewGuid().ToString(), Name = "Proseware Card", Type = "Cards", Payment = 40, Remaining = 500.005, Active = true };
        _env.Time.Advance(1000);
        await vm.SaveBillAsync(bill);

        Assert.Equal(7, UuidVersion(bill.Id));
        Assert.True(Replica.Read((s, _) => s.IsLive(SyncEntities.Bill, bill.Id)));
        Assert.Equal(SyncValue.Number(500.01), _env.Value(SyncEntities.Bill, bill.Id, "remainingCheckpoint"));
        Assert.Equal(SyncValue.String("2025-05-22T12:00:02.000Z"), _env.Value(SyncEntities.Bill, bill.Id, "createdAt"));
        var table = (await _env.Db.GetAllBillsAsync()).Single(b => b.Id == bill.Id);
        Assert.Equal(("Proseware Card", 500.01, "2025-05-22 12:00:02"), (table.Name, table.Remaining, table.CreatedAt));
    }

    private static int UuidVersion(string id) => Uuid.Inspect(id).Version ?? -1;

    // ---- Snapshots ------------------------------------------------------------------------------------

    [Fact]
    public async Task Snapshots_AreCreatedAndDeletedAsSyncedRows()
    {
        await OnAsync();
        var vm = new InsightsPageViewModel(_env.Routed);
        _env.Time.Advance(1000);
        var id = await vm.SaveSnapshotAsync(SyncTestEnvironment.Today);
        Assert.True(id > 3);
        Assert.Equal(4, vm.SnapshotCount);
        var syncId = Replica.Read((_, _) => _env.Sync.Materializer!.SyncIdOf(SyncEntities.BalanceSnapshot, id))!;
        Assert.Equal(7, UuidVersion(syncId));
        Assert.Equal(SyncValue.String("2025-05-22"), _env.Value(SyncEntities.BalanceSnapshot, syncId, "snapshotDate"));
        Assert.Equal(SyncValue.Number(1200 + 910.25), _env.Value(SyncEntities.BalanceSnapshot, syncId, "totalRemaining"));

        await vm.DeleteSnapshotAsync(1);
        Assert.Equal(3, vm.SnapshotCount);
        Assert.Equal(SyncValue.True, _env.Value(SyncEntities.BalanceSnapshot, SyncIds.LegacySnapshotId("2025-05-01", 300050, 1), "deleted"));
        Assert.Equal(0, await _env.Routed.DeleteSnapshotAsync(1));

        await vm.ClearAllSnapshotsAsync();
        Assert.Equal(0, vm.SnapshotCount);
        Assert.Equal(0, _env.Count("Snapshots"));
        Assert.Empty(Replica.Read((s, _) => s.LiveIds(SyncEntities.BalanceSnapshot)));
    }

    // ---- Settings -------------------------------------------------------------------------------------

    [Fact]
    public async Task Settings_SyncedKeysBecomeRecords_PerDeviceKeysStayOnThisDevice()
    {
        await OnAsync();
        _env.Time.Advance(1000);
        await new PayPeriodService(_env.Routed).SetPayAnchorAsync(new DateTime(2025, 3, 6));
        Assert.Equal(SyncValue.String("2025-03-06"), _env.Value(SyncEntities.Setting, SyncSettingKeys.PayAnchor, "value"));
        Assert.Equal("2025-03-06", await _env.Routed.GetSettingAsync("PayAnchor"));

        var before = Pending().Count;
        await _env.Routed.SetSettingAsync("Theme", "Light");
        Assert.Equal(before, Pending().Count);
        Assert.Equal("Light", await _env.Routed.GetSettingAsync("Theme"));

        // TypeColorService writes the whole map; only changed types become records.
        _env.Time.Advance(1000);
        await _env.Routed.SetSettingAsync(TypeColorService.SettingKey, "{\"Cards\":\"#FD79A8\",\"Fitness\":\"#00B894\",\"Loans\":\"#112233\"}");
        var colour = Assert.Single(Written(before));
        Assert.Equal(("TypeColor/Loans", SyncValue.String("#112233")), (colour.Id, colour.Value));

        _env.Time.Advance(1000);
        await _env.Routed.SetSettingAsync(TypeColorService.SettingKey, "{\"Cards\":\"#FD79A8\",\"Loans\":\"#112233\"}");
        Assert.Equal(SyncValue.Null, _env.Value(SyncEntities.Setting, "TypeColor/Fitness", "value"));
        Assert.Equal("{\"Cards\":\"#FD79A8\",\"Loans\":\"#112233\"}", await _env.Routed.GetSettingAsync(TypeColorService.SettingKey));

        _env.Time.Advance(1000);
        await _env.Routed.SetSettingAsync(PayPeriodService.EarlyStartKey, "true");
        Assert.Equal(SyncValue.True, _env.Value(SyncEntities.Setting, SyncSettingKeys.EarlyStart, "value"));
    }

    // ---- Restore --------------------------------------------------------------------------------------

    [Fact]
    public async Task Restore_WithSyncOn_MakesThisDeviceLookLikeTheBackupAtOneHlc()
    {
        await OnAsync();
        var backupsBefore = _env.Store.WriteHistory.Count;
        var before = Pending().Count;
        const string legacyBackup = """
            {
              "formatVersion": 1,
              "exportedAt": "2025-04-30T12:00:00.0000000Z",
              "bills": [
                { "id": "1", "name": "Woodgrove Rewards", "type": "Cards", "cost": 60, "owed": 1500, "dueDay": 18, "rate": "Monthly", "active": true },
                { "id": "30", "name": "Litware Loan", "type": "Loans", "payment": 200, "remaining": 4000, "dueDay": 9, "rate": "Monthly", "active": true }
              ],
              "payments": [ { "id": 1, "billId": "30", "periodKey": "2025-04-17", "amountPaid": 200, "paidAt": "2025-04-18 09:00:00" } ],
              "snapshots": [],
              "settings": { "PayAnchor": "2025-03-20", "Theme": "Light", "NotionBillsDb": "an-old-connection", "TypeColors": "{\"Loans\":\"#123456\"}", "SchemaVersion": "1" }
            }
            """;
        var settingsVm = new SettingsPageViewModel(_env.Routed);
        _env.Time.Advance(1000);
        await settingsVm.ImportAsync(legacyBackup);

        var restore = Written(before);
        Assert.Single(restore.Select(r => r.Hlc).Distinct());
        Assert.Equal(backupsBefore + 1, _env.Store.WriteHistory.Count);

        var bills = (await _env.Db.GetAllBillsAsync()).ToDictionary(b => b.Id);
        Assert.Equal(new[] { B1, LegacyMapping.MapBillId("30") }.Order(StringComparer.Ordinal), bills.Keys.Order(StringComparer.Ordinal));
        Assert.Equal((60.0, 1500.0), (bills[B1].Payment, bills[B1].Remaining));
        Assert.Equal(4000, bills[LegacyMapping.MapBillId("30")].Remaining); // the restored payment is absorbed
        Assert.Single(await _env.Db.GetAllPaymentsAsync());
        Assert.Equal(0, _env.Count("Snapshots"));
        Assert.Equal(SyncValue.True, _env.Value(SyncEntities.Bill, BUpper, "deleted"));

        var settings = await _env.Db.GetAllSettingsAsync();
        Assert.Equal("Light", settings["Theme"]);
        Assert.Equal("00000000-0000-0000-0000-000000000102", settings["NotionBillsDb"]);
        Assert.Equal("2", settings["SchemaVersion"]);
        Assert.Equal("{\"Loans\":\"#123456\"}", settings["TypeColors"]);
        Assert.Equal(SyncValue.Null, _env.Value(SyncEntities.Setting, "TypeColor/Cards", "value"));
    }

    // ---- Remote changes reach the tables --------------------------------------------------------------

    [Fact]
    public async Task MergedRemoteChanges_AreMaterialized_IncludingDeletesAndUndeletes()
    {
        await OnAsync();
        var remote = SyncReplica.Start(new InMemorySyncStorage(), new ManualTimeProvider(_env.Time.UnixMs + 60_000), "019a0000-0000-7000-8000-00000000fa01");
        var bill = remote.NewRowId();
        remote.CreateRow(SyncEntities.Bill, bill, new Dictionary<string, SyncValue>
        {
            ["name"] = SyncValue.String("Adventure Works Card"),
            ["type"] = SyncValue.String("Cards"),
            ["remainingCheckpoint"] = SyncValue.Number(800),
            ["dueDay"] = SyncValue.Number(25),
        });
        remote.MarkPaid(PaymentKinds.Regular, bill, SyncTestEnvironment.CurrentPeriod, 50, "2025-05-22T13:00:00.000Z");
        remote.MarkPaid(PaymentKinds.SetAside, bill, SyncTestEnvironment.CurrentPeriod, 10, null);

        var outcome = Replica.MergeRecords(remote.State.AllRecords());
        Assert.Contains(new RowKey(SyncEntities.Bill, bill), outcome.ChangedRows);

        var shown = (await _env.Db.GetAllBillsAsync()).Single(b => b.Id == bill);
        Assert.Equal(("Adventure Works Card", 750.0), (shown.Name, shown.Remaining));
        var payment = Assert.Single(await _env.Db.GetAllPaymentsAsync(), p => p.BillId == bill); // the set-aside is not shown
        Assert.Equal(50, payment.AmountPaid);

        // A deleted bill and its payments disappear from the tables; undeleting brings both back.
        var deleted = remote.DeleteRow(SyncEntities.Bill, bill);
        Replica.MergeRecords(deleted);
        Assert.DoesNotContain(await _env.Db.GetAllBillsAsync(), b => b.Id == bill);
        Assert.DoesNotContain(await _env.Db.GetAllPaymentsAsync(), p => p.BillId == bill);
        Replica.MergeRecords(remote.Commit((s, h) => RowWriter.Undelete(s, SyncEntities.Bill, bill, h)));
        Assert.Contains(await _env.Db.GetAllBillsAsync(), b => b.Id == bill);
        Assert.Equal(payment.Id, Assert.Single(await _env.Db.GetAllPaymentsAsync(), p => p.BillId == bill).Id);

        // Remote settings too.
        Replica.MergeRecords(new[] { Remote(SyncEntities.Setting, "TypeColor/cards", SyncEntities.ValueField, SyncValue.String("#abcdef"), 120_000) });
        Assert.Equal("{\"Fitness\":\"#00B894\",\"cards\":\"#ABCDEF\"}", await _env.Db.GetSettingAsync(TypeColorService.SettingKey));
    }
}
