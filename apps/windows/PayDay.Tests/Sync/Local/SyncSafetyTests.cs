using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PayDay.Models;
using PayDay.Services;
using PayDay.Sync.Clock;
using PayDay.Sync.Ids;
using PayDay.Sync.Local;
using PayDay.Sync.Merge;
using PayDay.Sync.Model;
using PayDay.ViewModels;

namespace PayDay.Tests.Sync.Local;

/// <summary>
/// What must hold before anything may turn sync on: start-up leaves the database untouched, no write
/// slips past the migration, saves never revert another device's edits, one unmark is one action.
/// All data is fictional.
/// </summary>
[Collection(TypeColorServiceTests.StaticState)]
public sealed class SyncSafetyTests : IDisposable
{
    private const string OtherDevice = "0b0b0b0b0b0b0b0b";

    private readonly SyncTestEnvironment _env = new();

    public SyncSafetyTests() => TypeColorService.ResetInMemory();

    public void Dispose()
    {
        TypeColorService.ResetInMemory();
        _env.Dispose();
    }

    private static string B1 => LegacyMapping.MapBillId("1");

    private static string B13 => LegacyMapping.MapBillId("13");

    private ChangeRecord Remote(string entity, string id, string field, SyncValue value, long laterMs = 60_000)
        => new(entity, id, field, value, Hlc.Encode(_env.Time.UnixMs + laterMs, 0, OtherDevice));

    // ---- A5: start-up with sync off leaves payday.db byte for byte unchanged ---------------------------

    [Fact]
    public async Task AppStart_WithSyncOff_LeavesTheDatabaseFileByteForByteUnchanged()
    {
        await _env.InitializeAsync();
        _env.InsertLegacyData();
        SqliteTestFiles.ClearPoolsFor(_env.Path);
        var before = File.ReadAllBytes(_env.Path);

        // Exactly what App.OnLaunched does, then every page's load through the routing write path.
        await _env.Db.InitializeAsync();
        _env.Sync.Load();
        await TypeColorService.LoadAsync(_env.Routed);
        var payDay = new PayDayPageViewModel(_env.Routed, null, _env.Backups);
        await payDay.LoadAsync(SyncTestEnvironment.Today);
        await new DashboardPageViewModel(_env.Routed).LoadAsync(SyncTestEnvironment.Today);
        await new AllBillsPageViewModel(_env.Routed).LoadAsync();
        await new InsightsPageViewModel(_env.Routed, null, _env.Backups).LoadAsync();
        await new PayoffTrackerPageViewModel(_env.Routed).LoadAsync();
        await new SettingsPageViewModel(_env.Routed).LoadAsync();
        await new PayPeriodService(_env.Routed).GetPayAnchorAsync();
        _ = _env.Routed.GetNotionState();

        // The sync loop the app starts at launch: client ID lookup, coordinator, scheduler (a launch
        // sync), the Sync page and the Notion connector's state. With sync off none of it writes.
        var credentials = new InMemoryCredentialStore();
        var clientId = PayDay.Sync.Drive.OneDrive.OneDriveConfig.Resolve(null, await _env.Db.GetSettingAsync(PayDay.Sync.Drive.OneDrive.OneDriveConfig.SettingKey));
        var auth = new PayDay.Sync.Drive.OneDrive.OneDriveAuth(clientId ?? "00000000-0000-0000-0000-00000000c1d0", credentials);
        var drive = new PayDay.Sync.Drive.InMemoryDriveProvider(_env.Time);
        using (var coordinator = new PayDay.Sync.Coordination.SyncCoordinator(_env.Sync, drive, new PayDay.Sync.Keys.CredentialVaultKeyStore(credentials), _env.Backups, _env.Db,
            new PayDay.Sync.Coordination.DeviceInfo("Windows PC", "windows", null), () => false, _env.Time))
        using (var scheduler = new PayDay.Sync.Coordination.SyncScheduler(ct => coordinator.SyncAsync(ct), _env.Time))
        using (var notion = new NotionSyncService(_env.Routed, credentials, new RecordingHttpHandler()))
        using (var page = new SyncPageViewModel(coordinator, auth, notion, _env.Time))
        {
            scheduler.Attach(_env.Sync);
            scheduler.NotifyLaunch();
            Assert.True(await scheduler.RunDueAsync());
            Assert.Equal(PayDay.Sync.Coordination.SyncPhase.Off, coordinator.Status.Phase);
            await page.LoadAsync();
            Assert.Equal(SyncScreen.Off, page.Screen);
            Assert.Equal(NotionRole.SyncOff, (await notion.GetConnectorStateAsync()).Role);
        }
        Assert.Empty(drive.Paths);
        Assert.Equal(0, drive.ListCalls);
        Assert.Empty(credentials.Items);

        // And a second start of the same database.
        using (var again = LocalSync.Open(LocalSync.ConnectionStringFor(_env.Path)))
        {
            Assert.False(again.IsOn);
        }
        await _env.Db.InitializeAsync();

        SqliteTestFiles.ClearPoolsFor(_env.Path);
        Assert.Equal(before, File.ReadAllBytes(_env.Path));
        Assert.False(File.Exists(_env.Path + "-journal"));
        Assert.False(File.Exists(_env.Path + "-wal"));
        Assert.Empty(_env.Store.WriteHistory);
        Assert.Equal(NotionConnectorState.SyncOff, _env.Routed.GetNotionState());
    }

    // ---- A3: no write slips past turning sync on or off ------------------------------------------------

    [Fact]
    public async Task AWriteRacingTheMigration_WaitsForIt_AndGoesThroughSync()
    {
        await _env.InitializeAsync();
        _env.InsertLegacyData();
        var bill = (await _env.Routed.GetAllBillsAsync()).Single(b => b.Id == "1");
        var store = new GatedBackupStore();
        var backups = new BackupRotationService(_env.Db, store, () => DateTime.UnixEpoch.AddMilliseconds(_env.Time.UnixMs));

        _env.Time.Advance(1000);
        var turnOn = _env.Sync.TurnOnAsync(backups);
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The backup is being written: reads still work, every write waits.
        Assert.NotEmpty(await _env.Routed.GetAllBillsAsync());
        var mark = _env.Routed.MarkBillPaidAsync(SyncTestEnvironment.CurrentPeriod, bill, 55);
        var theme = _env.Routed.SetSettingAsync("Theme", "Light");
        await Task.Delay(100);
        Assert.False(mark.IsCompleted);
        Assert.False(theme.IsCompleted);
        Assert.False(_env.Sync.IsOn);

        store.Release.SetResult();
        var result = await turnOn;
        await mark;
        await theme;

        Assert.Equal(TurnOnStatus.TurnedOn, result.Status);
        // The mark went through the sync engine after the migration, not into the tables behind it.
        var paymentId = SyncIds.RegularPaymentId(B1, SyncTestEnvironment.CurrentPeriod);
        Assert.Equal(SyncValue.False, _env.Value(SyncEntities.Payment, paymentId, SyncEntities.DeletedField));
        Assert.Equal(1145, (await _env.Db.GetAllBillsAsync()).Single(b => b.Id == B1).Remaining);
        Assert.Contains(await _env.Db.GetAllPaymentsAsync(), p => p.BillId == B1 && p.PeriodKey == SyncTestEnvironment.CurrentPeriod);
        // The pre-sync backup holds the data as it was before the write.
        var backup = BackupSerializer.FromJson(store.Inner.Snapshot()[result.BackupFileName!]);
        Assert.DoesNotContain(backup.Payments, p => p.PeriodKey == SyncTestEnvironment.CurrentPeriod && p.BillId == "1");
        Assert.Equal(1200, backup.Bills.Single(b => b.Id == "1").Remaining);
        Assert.Equal("Light", await _env.Db.GetSettingAsync("Theme"));
    }

    [Fact]
    public async Task TurningSyncOff_WaitsForAWriteInProgress()
    {
        await _env.InitializeAsync();
        _env.InsertLegacyData();
        await _env.TurnOnAsync();

        var lease = await _env.Sync.EnterWriteAsync();
        var off = _env.Sync.TurnOffAsync();
        await Task.Delay(100);
        Assert.False(off.IsCompleted);
        Assert.True(_env.Sync.IsOn);
        lease.Dispose();
        await off;
        Assert.False(_env.Sync.IsOn);

        // A lease from another LocalSync is refused.
        using var other = new LocalSync(LocalSync.ConnectionStringFor(_env.Path));
        using var foreign = await other.EnterWriteAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => _env.Sync.TurnOffAsync(foreign));
    }

    // ---- A4: saves compare with what the caller saw, never write every field ---------------------------

    [Fact]
    public async Task ABillLoadedBeforeSyncWasOn_NeverRevertsAnotherDevicesEdit()
    {
        await _env.InitializeAsync();
        _env.InsertLegacyData();
        // A page loaded its bills before sync was turned on, and keeps those objects.
        var pageCopy = (await _env.Routed.GetAllBillsAsync()).Single(b => b.Id == "13");
        await _env.TurnOnAsync();

        // Another device changes the notes; this device's page still holds the old ones.
        _env.Sync.Replica!.MergeRecords(new[] { Remote(SyncEntities.Bill, B13, "notes", SyncValue.String("Estimate from the other device")) });
        var pendingBefore = _env.Sync.Replica.Read((_, m) => m.PendingRecords.Count);

        _env.Time.Advance(120_000);
        pageCopy.Name = "Electric (Contoso Power)";
        await _env.Routed.UpsertBillAsync(pageCopy);

        var written = _env.Sync.Replica.Read((_, m) => m.PendingRecords.Skip(pendingBefore).ToList());
        var record = Assert.Single(written);
        Assert.Equal(("name", SyncValue.String("Electric (Contoso Power)")), (record.Field, record.Value));
        Assert.Equal(SyncValue.String("Estimate from the other device"), _env.Value(SyncEntities.Bill, B13, "notes"));
        Assert.Equal(B13, pageCopy.Id);
    }

    [Fact]
    public async Task ABillThisServiceNeverSaw_IsComparedWithTheMergedState()
    {
        await _env.InitializeAsync();
        _env.InsertLegacyData();
        await _env.TurnOnAsync();
        var replica = _env.Sync.Replica!;
        var pendingBefore = replica.Read((_, m) => m.PendingRecords.Count);

        // A copy built from the table row, as a page that bypassed this service would hold it.
        var fresh = TableMaterializer.BillFromState(replica.State, B1);
        fresh.Payment = 60;
        _env.Time.Advance(1000);
        await _env.Routed.UpsertBillAsync(fresh);

        var written = replica.Read((_, m) => m.PendingRecords.Skip(pendingBefore).ToList());
        Assert.Equal(new[] { "payment" }, written.Select(r => r.Field));
    }

    [Fact]
    public async Task TypeColors_OnlyTheChangedColourIsWritten_AndAColourFromAnotherDeviceSurvives()
    {
        await _env.InitializeAsync();
        _env.InsertLegacyData();
        await _env.TurnOnAsync();
        await TypeColorService.LoadAsync(_env.Routed);
        var replica = _env.Sync.Replica!;

        // Another device sets a colour this device's in-memory map never loaded.
        replica.MergeRecords(new[] { Remote(SyncEntities.Setting, "TypeColor/Loans", SyncEntities.ValueField, SyncValue.String("#112233")) });
        var pendingBefore = replica.Read((_, m) => m.PendingRecords.Count);

        _env.Time.Advance(120_000);
        await TypeColorService.SetHexAsync(_env.Routed, "Bills", "#445566");

        var written = replica.Read((_, m) => m.PendingRecords.Skip(pendingBefore).ToList());
        var record = Assert.Single(written);
        Assert.Equal(("TypeColor/Bills", SyncValue.String("#445566")), (record.Id, record.Value));
        Assert.Equal(SyncValue.String("#112233"), _env.Value(SyncEntities.Setting, "TypeColor/Loans", SyncEntities.ValueField));

        // Resetting one colour writes only that colour's null.
        _env.Time.Advance(1000);
        await TypeColorService.ResetAsync(_env.Routed, "Cards");
        var reset = replica.Read((_, m) => m.PendingRecords.Skip(pendingBefore + 1).ToList());
        Assert.Equal(("TypeColor/Cards", SyncValue.Null), (Assert.Single(reset).Id, reset[0].Value));
        Assert.Equal(SyncValue.String("#112233"), _env.Value(SyncEntities.Setting, "TypeColor/Loans", SyncEntities.ValueField));

        // After a reload the service sees every colour again.
        await TypeColorService.LoadAsync(_env.Routed);
        Assert.Equal("#112233", TypeColorService.GetHex("Loans"));
        Assert.Equal("#445566", TypeColorService.GetHex("Bills"));
    }

    [Fact]
    public async Task TypeColors_AReadAfterAMerge_DoesNotLetAnOlderMapRevertAnotherDevicesColour()
    {
        await _env.InitializeAsync();
        _env.InsertLegacyData();
        await _env.TurnOnAsync();
        await TypeColorService.LoadAsync(_env.Routed); // Cards is #FD79A8 in this device's map
        var replica = _env.Sync.Replica!;

        // Another device changes Cards; then something reads every setting (the backup on mark-paid does).
        replica.MergeRecords(new[] { Remote(SyncEntities.Setting, "TypeColor/Cards", SyncEntities.ValueField, SyncValue.String("#112233")) });
        await _env.Routed.GetAllSettingsAsync();
        await _env.Routed.GetSettingAsync(TypeColorService.SettingKey);
        var pendingBefore = replica.Read((_, m) => m.PendingRecords.Count);

        // The user changes another colour from the map loaded before the merge.
        _env.Time.Advance(120_000);
        await TypeColorService.SetHexAsync(_env.Routed, "Bills", "#445566");

        var written = replica.Read((_, m) => m.PendingRecords.Skip(pendingBefore).ToList());
        Assert.Equal("TypeColor/Bills", Assert.Single(written).Id);
        Assert.Equal(SyncValue.String("#112233"), _env.Value(SyncEntities.Setting, "TypeColor/Cards", SyncEntities.ValueField));
        Assert.Contains("#112233", await _env.Db.GetSettingAsync(TypeColorService.SettingKey), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TypeColors_WithSyncOff_StillSaveTheWholeMapAsBefore()
    {
        await _env.InitializeAsync();
        _env.InsertLegacyData();
        await TypeColorService.LoadAsync(_env.Routed);

        await TypeColorService.SetHexAsync(_env.Routed, "Bills", "#445566");

        Assert.False(_env.Sync.IsOn);
        var expected = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["Cards"] = "#fd79a8",
            ["Fitness"] = "#00B894",
            ["Bad"] = "red",
            ["Bills"] = "#445566",
        });
        Assert.Equal(expected, await _env.Db.GetSettingAsync(TypeColorService.SettingKey));
    }

    // ---- A6: one unmark is one action ------------------------------------------------------------------

    [Fact]
    public async Task UnmarkingAPeriodWithExtras_IsOneActionAtOneHlc_WithOneCheckpoint()
    {
        await _env.InitializeAsync();
        _env.InsertLegacyData();
        await _env.TurnOnAsync();
        var replica = _env.Sync.Replica!;
        var pendingBefore = replica.Read((_, m) => m.PendingRecords.Count);
        var clockBefore = replica.Read((_, m) => m.ClockState);

        _env.Time.Advance(1000);
        Assert.Equal(3, await _env.Routed.DeletePaymentsForBillInPeriodAsync("2025-05-01", B1));

        var written = replica.Read((_, m) => m.PendingRecords.Skip(pendingBefore).ToList());
        var hlc = Assert.Single(written.Select(r => r.Hlc).Distinct());
        Assert.True(hlc > clockBefore!.Value);
        Assert.Equal(hlc, replica.Read((_, m) => m.ClockState));
        Assert.Equal(3, written.Count(r => r.Entity == SyncEntities.Payment && r.Field == SyncEntities.DeletedField && r.Value.IsTrue));
        // Every absorbed payment raised the one checkpoint: 1200 + 55 + 55 + 20.
        var checkpoint = Assert.Single(written, r => r.Field == "remainingCheckpoint");
        Assert.Equal(SyncValue.Number(1330), checkpoint.Value);
        Assert.Equal(SyncValue.Number(1330), _env.Value(SyncEntities.Bill, B1, "remainingCheckpoint"));
        Assert.Equal(written.Count, written.Select(r => (r.Entity, r.Id, r.Field)).Distinct().Count());

        // Unmarking again writes nothing and takes no tick.
        Assert.Equal(0, await _env.Routed.DeletePaymentsForBillInPeriodAsync("2025-05-01", B1));
        Assert.Equal(hlc, replica.Read((_, m) => m.ClockState));
    }

    [Fact]
    public async Task ALaterStepOfOneAction_ReplacesAnEarlierStepsValueForTheSameField()
    {
        await _env.InitializeAsync();
        _env.InsertLegacyData();
        await _env.TurnOnAsync();
        var replica = _env.Sync.Replica!;
        var pendingBefore = replica.Read((_, m) => m.PendingRecords.Count);

        _env.Time.Advance(1000);
        var written = replica.CommitSteps(
            (_, h) => new[] { new ChangeRecord(SyncEntities.Bill, B1, "notes", SyncValue.String("zzz first"), h) },
            (_, h) => new[] { new ChangeRecord(SyncEntities.Bill, B1, "notes", SyncValue.String("aaa second"), h) });

        Assert.Equal(SyncValue.String("aaa second"), Assert.Single(written).Value);
        Assert.Equal(SyncValue.String("aaa second"), _env.Value(SyncEntities.Bill, B1, "notes"));
        Assert.Equal("aaa second", (await _env.Db.GetAllBillsAsync()).Single(b => b.Id == B1).Notes);
        Assert.Single(replica.Read((_, m) => m.PendingRecords.Skip(pendingBefore).ToList()));
    }

    /// <summary>A backup store whose pre-sync write blocks until the test releases it.</summary>
    private sealed class GatedBackupStore : IBackupStore
    {
        public InMemoryBackupStore Inner { get; } = new();

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WriteAsync(string fileName, string content, CancellationToken ct = default)
        {
            if (fileName.StartsWith(BackupRotationService.PreSyncFileNamePrefix, StringComparison.Ordinal))
            {
                Entered.TrySetResult();
                await Release.Task.ConfigureAwait(false);
            }
            await Inner.WriteAsync(fileName, content, ct).ConfigureAwait(false);
        }

        public Task<IReadOnlyList<BackupEntry>> ListAsync(CancellationToken ct = default) => Inner.ListAsync(ct);

        public Task<string> ReadAsync(string fileName, CancellationToken ct = default) => Inner.ReadAsync(fileName, ct);

        public Task DeleteAsync(string fileName, CancellationToken ct = default) => Inner.DeleteAsync(fileName, ct);
    }
}
