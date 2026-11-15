using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using PayDay.Models;
using PayDay.Services;
using PayDay.Sync.Coordination;
using PayDay.Sync.Ids;
using PayDay.Sync.Local;
using PayDay.Sync.Merge;
using PayDay.Sync.Model;
using PayDay.Tests.Sync.Local;

namespace PayDay.Tests.Sync.Loop;

/// <summary>
/// End-to-end scenarios on one in-memory drive: concurrent offline edits on three devices, an offline
/// device catching up after compaction, a restore with sync on, and the Notion connector moving
/// between devices. Every scenario ends with all devices holding the same records and tables.
/// All data is fictional.
/// </summary>
public sealed class SyncLoopScenarioTests : IDisposable
{
    private const long Day = 24L * 60 * 60 * 1000;

    private readonly SyncWorld _world = new();

    public void Dispose() => _world.Dispose();

    private static string B1 => LegacyMapping.MapBillId("1");

    private static string B13 => LegacyMapping.MapBillId("13");

    private const string BUpper = "6f9619ff-8b86-d011-b42d-00c04fc964ff";

    private async Task<(SyncDevice A, string Phrase)> CreatorAsync(bool notionToken = false)
    {
        var a = await _world.AddDeviceAsync("Windows PC", notionToken: notionToken);
        a.Env.InsertLegacyData();
        _world.Advance(1000);
        var created = await a.Coordinator.CreateVaultAsync();
        Assert.Equal(CreateVaultOutcome.Created, created.Outcome);
        return (a, created.RecoveryPhrase!);
    }

    private async Task<SyncDevice> JoinerAsync(string name, string phrase, long clockSkewMs = 0)
    {
        var device = await _world.AddDeviceAsync(name, clockSkewMs);
        _world.Advance(1000);
        Assert.Equal(JoinVaultOutcome.Joined, (await device.Coordinator.JoinWithRecoveryPhraseAsync(phrase, false)).Outcome);
        return device;
    }

    /// <summary>Syncs every device, twice round, so everything reaches everyone.</summary>
    private async Task SyncAllAsync(params SyncDevice[] devices)
    {
        for (var round = 0; round < 2; round++)
        {
            foreach (var d in devices)
            {
                _world.Advance(1000);
                var status = await d.SyncAsync();
                Assert.Equal(SyncPhase.Idle, status.Phase);
            }
        }
    }

    private static async Task AssertConvergedAsync(params SyncDevice[] devices)
    {
        var records = devices[0].Records();
        var tables = await devices[0].TablesAsync();
        foreach (var d in devices.Skip(1))
        {
            Assert.Equal(records, d.Records());
            Assert.Equal(tables, await d.TablesAsync());
        }
    }

    private static async Task<Bill> BillAsync(SyncDevice d, string id) => (await d.Env.Routed.GetAllBillsAsync()).Single(b => b.Id == id);

    // ---- Concurrent edits ---------------------------------------------------------------------------

    [Fact]
    public async Task ConcurrentOfflineEditsOnThreeDevices_Converge()
    {
        var (a, phrase) = await CreatorAsync();
        var b = await JoinerAsync("Windows Laptop", phrase);
        var c = await JoinerAsync("Surface Go", phrase);
        await SyncAllAsync(a, b, c);

        // All three offline. A renames bill 1 and marks bill 13 paid; B renames bill 1 later (wins),
        // marks bill 1 paid and edits bill 13's notes; C unmarks the migrated payment of the phone plan.
        var aBill1 = await BillAsync(a, B1);
        aBill1.Name = "Woodgrove (A)";
        await a.Env.Routed.UpsertBillAsync(aBill1);
        await a.Env.Routed.MarkBillPaidAsync(SyncTestEnvironment.CurrentPeriod, await BillAsync(a, B13), 120);
        _world.Advance(5000);
        var bBill1 = await BillAsync(b, B1);
        bBill1.Name = "Woodgrove (B)";
        await b.Env.Routed.UpsertBillAsync(bBill1);
        await b.Env.Routed.MarkBillPaidAsync(SyncTestEnvironment.CurrentPeriod, await BillAsync(b, B1), 55);
        var bBill13 = await BillAsync(b, B13);
        bBill13.Notes = "Estimate from the laptop";
        await b.Env.Routed.UpsertBillAsync(bBill13);
        _world.Advance(5000);
        await c.Env.Routed.UnmarkBillPaidAsync(SyncTestEnvironment.CurrentPeriod, await BillAsync(c, BUpper), 45.5);
        // And a setting changed on two devices: the later one wins.
        await a.Env.Routed.SetSettingAsync("PayAnchor", "2025-03-06");
        _world.Advance(1000);
        await c.Env.Routed.SetSettingAsync("PayAnchor", "2025-03-13");

        // Sync in an unhelpful order.
        await SyncAllAsync(c, a, b);
        await AssertConvergedAsync(a, b, c);

        var bill1 = await BillAsync(a, B1);
        Assert.Equal("Woodgrove (B)", bill1.Name);
        Assert.Equal(1145, bill1.Remaining);
        var bill13 = await BillAsync(c, B13);
        Assert.Equal("Estimate from the laptop", bill13.Notes);
        Assert.Contains(await c.Env.Db.GetAllPaymentsAsync(), p => p.BillId == B13 && p.PeriodKey == SyncTestEnvironment.CurrentPeriod);
        Assert.Equal(955.75, (await BillAsync(b, BUpper)).Remaining); // the unmark gave 45.50 back
        Assert.Equal("2025-03-13", await b.Env.Db.GetSettingAsync("PayAnchor"));
    }

    [Fact]
    public async Task MergedChanges_RaiseDataChanged_AfterTheyAreCommitted()
    {
        var (a, phrase) = await CreatorAsync();
        var b = await JoinerAsync("Windows Laptop", phrase);
        await b.Env.Routed.SetSettingAsync(TypeColorService.SettingKey, "{\"Cards\":\"#123456\"}");
        _world.Advance(1000);
        await b.SyncAsync();

        string? seenInHandler = null;
        SyncDataChangedEventArgs? args = null;
        a.Env.Sync.DataChanged += (_, e) =>
        {
            args = e;
            // Another connection sees the merged colour: the transaction already committed.
            using var conn = new SqliteConnection(SqliteTestFiles.ConnectionString(a.Env.Path));
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Value FROM Settings WHERE Key = 'TypeColors';";
            seenInHandler = cmd.ExecuteScalar() as string;
        };
        _world.Advance(1000);
        await a.SyncAsync();

        Assert.NotNull(args);
        Assert.True(args!.SettingsChanged);
        Assert.Contains("#123456", seenInHandler, StringComparison.Ordinal);
    }

    // ---- Offline device catching up after compaction --------------------------------------------------

    [Fact]
    public async Task AnOfflineDevice_CatchesUpFromASnapshot_AfterTheFilesItMissedWereCompacted()
    {
        var (a, phrase) = await CreatorAsync();
        var c = await JoinerAsync("Surface Go", phrase);
        var b = await JoinerAsync("Windows Laptop", phrase);
        await SyncAllAsync(a, b, c);
        var cursorOfA = c.Env.Sync.Replica!.Cursor(a.DeviceId);

        // C goes offline. A edits 24 times, syncing after each; B edits a few times.
        for (var i = 0; i < 24; i++)
        {
            _world.Advance(10_000);
            var bill = await BillAsync(a, B13);
            bill.Notes = $"Reading {i}";
            await a.Env.Routed.UpsertBillAsync(bill);
            Assert.Equal(SyncPhase.Idle, (await a.SyncAsync()).Phase);
        }
        for (var i = 0; i < 3; i++)
        {
            _world.Advance(10_000);
            await b.Env.Routed.MarkBillPaidAsync($"2025-0{i + 1}-06", await BillAsync(b, B1), 10);
            await b.SyncAsync();
        }
        _world.Advance(10_000);
        await a.SyncAsync();

        // With 20 or more change files and no snapshot, A wrote one (11.2).
        var snapshots = _world.Drive.Paths.Where(p => p.StartsWith("snapshots/", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(snapshots);
        Assert.Contains($"devices/{a.DeviceId}/000002.chg", _world.Drive.Paths);

        // A week later (by this device's clock and the provider's) A deletes the files the snapshot covers.
        _world.Advance(8 * Day);
        await a.SyncAsync();
        Assert.DoesNotContain($"devices/{a.DeviceId}/000002.chg", _world.Drive.Paths);
        Assert.DoesNotContain($"devices/{a.DeviceId}/000010.chg", _world.Drive.Paths);
        // Only the owner deletes change files: B's are still there.
        Assert.Contains($"devices/{b.DeviceId}/000002.chg", _world.Drive.Paths);

        // C comes back: the files after its cursor are gone, so it reads the snapshot instead.
        Assert.False(_world.Drive.Exists($"devices/{a.DeviceId}/{(cursorOfA + 1).ToString("D6", System.Globalization.CultureInfo.InvariantCulture)}.chg"));
        var status = await c.SyncAsync();
        Assert.Equal(SyncPhase.Idle, status.Phase);
        Assert.Empty(status.Warnings);
        Assert.Equal("Reading 23", (await BillAsync(c, B13)).Notes);
        await SyncAllAsync(a, b, c);
        await AssertConvergedAsync(a, b, c);
    }

    [Fact]
    public async Task ANewDevice_JoiningAfterCompaction_ReadsTheSnapshot()
    {
        var (a, phrase) = await CreatorAsync();
        for (var i = 0; i < 22; i++)
        {
            _world.Advance(10_000);
            var bill = await BillAsync(a, B1);
            bill.Notes = $"Statement {i}";
            await a.Env.Routed.UpsertBillAsync(bill);
            await a.SyncAsync();
        }
        _world.Advance(10_000);
        await a.SyncAsync();
        _world.Advance(8 * Day);
        await a.SyncAsync();
        Assert.False(_world.Drive.Exists($"devices/{a.DeviceId}/000001.chg"));

        var d = await JoinerAsync("Windows Tablet", phrase);
        Assert.Equal("Statement 21", (await BillAsync(d, B1)).Notes);
        await SyncAllAsync(a, d);
        await AssertConvergedAsync(a, d);
    }

    // ---- Restore with sync on (12.3) ------------------------------------------------------------------

    [Fact]
    public async Task RestoringABackupWithSyncOn_MakesEveryDeviceLookLikeTheBackup()
    {
        var (a, phrase) = await CreatorAsync();
        var b = await JoinerAsync("Windows Laptop", phrase);
        await SyncAllAsync(a, b);

        var backup = BackupSerializer.FromJson("""
            {
              "formatVersion": 1,
              "exportedAt": "2025-05-20T08:00:00Z",
              "bills": [
                { "id": "13", "name": "Electric", "type": "Bills", "payment": 130, "remaining": 0, "dueDay": 3, "rate": "Monthly", "active": true },
                { "id": "40", "name": "Proseware Streaming", "type": "Subscriptions", "payment": 12.99, "remaining": 0, "dueDay": 9, "rate": "Monthly", "active": true }
              ],
              "payments": [ { "id": 1, "billId": "13", "periodKey": "2025-05-01", "amountPaid": 130, "paidAt": "2025-05-02 09:00:00" } ],
              "snapshots": [],
              "settings": { "PayAnchor": "2025-03-20", "Theme": "Light" }
            }
            """);
        _world.Advance(1000);
        await b.Env.Routed.ReplaceAllDataAsync(backup.Bills, backup.Payments, backup.Snapshots, backup.Settings);
        await SyncAllAsync(b, a);
        await AssertConvergedAsync(a, b);

        var bills = await a.Env.Db.GetAllBillsAsync();
        Assert.Equal(new[] { "Electric", "Proseware Streaming" }, bills.Select(x => x.Name).Order(StringComparer.Ordinal));
        Assert.Equal(130, bills.Single(x => x.Name == "Electric").Payment);
        Assert.Single(await a.Env.Db.GetAllPaymentsAsync());
        Assert.Empty(await a.Env.Db.GetAllSnapshotsAsync());
        // Per-device settings of the backup apply only to the device that restored it.
        Assert.Equal("Light", await b.Env.Db.GetSettingAsync("Theme"));
        Assert.Equal("Dark", await a.Env.Db.GetSettingAsync("Theme"));
    }

    // ---- Notion owner rules (12.5) --------------------------------------------------------------------

    [Fact]
    public async Task TheNotionConnector_RunsOnOneDeviceAtATime_AndMovesWhenAnotherConnects()
    {
        var (a, phrase) = await CreatorAsync(notionToken: true);
        Assert.Equal(SyncValue.String(a.DeviceId), a.Env.Value(SyncEntities.Setting, SyncSettingKeys.NotionOwnerDeviceId, SyncEntities.ValueField));
        Assert.Equal(NotionRole.Owner, a.Env.Routed.GetNotionState().Role); // the creator keeps running it, no forced push
        var b = await JoinerAsync("Windows Laptop", phrase);
        b.Credentials.Set(NotionSyncService.TokenKey, "secret_fictional_token");

        var bNotion = new FakeNotion(() => b.Env.Time.GetUtcNow());
        using var bService = new NotionSyncService(b.Env.Routed, b.Credentials, bNotion);
        var state = bService.GetConnectorState();
        Assert.Equal((NotionRole.OtherDevice, "Windows PC"), (state.Role, state.OwnerDeviceName));
        var refused = await bService.SyncBillsAsync();
        Assert.Contains("connected on Windows PC", Assert.Single(refused.Errors), StringComparison.Ordinal);
        Assert.Empty(bNotion.Calls);

        // Connecting Notion on the laptop moves the connector there; the desktop stops after it syncs.
        await bService.ConnectAsync("secret_fictional_token");
        await SyncAllAsync(b, a);
        var aNotion = new FakeNotion(() => a.Env.Time.GetUtcNow());
        using var aService = new NotionSyncService(a.Env.Routed, a.Credentials, aNotion);
        Assert.Equal((NotionRole.OtherDevice, "Windows Laptop"), (aService.GetConnectorState().Role, aService.GetConnectorState().OwnerDeviceName));
        await aService.SyncBillsAsync();
        await aService.PushBillAsync(await BillAsync(a, B1));
        Assert.Empty(aNotion.Calls);
        Assert.Equal(b.DeviceId, a.Coordinator.Status.NotionOwnerDeviceId);

        // The new owner pushes every bill before its first pull.
        Assert.Equal(NotionRole.OwnerMustPushFirst, bService.GetConnectorState().Role);
        var first = await bService.SyncBillsAsync();
        Assert.Equal(6, first.Created);
        Assert.Equal(NotionRole.Owner, bService.GetConnectorState().Role);
        // The page IDs it learned sync to the desktop, so a later owner updates the same pages.
        await SyncAllAsync(b, a);
        Assert.All(await a.Env.Db.GetAllBillsAsync(), bill => Assert.StartsWith("fake-page-", bill.NotionPageId, StringComparison.Ordinal));
        await AssertConvergedAsync(a, b);
    }

    // ---- Leaving and creating again -------------------------------------------------------------------

    [Fact]
    public async Task LeavingTheVault_KeepsLocalDataAndTheDrive_AndRejoiningUsesANewDeviceId()
    {
        var (a, phrase) = await CreatorAsync();
        var b = await JoinerAsync("Windows Laptop", phrase);
        await SyncAllAsync(a, b);
        var oldId = b.DeviceId;
        var tables = await b.TablesAsync();
        var files = _world.Drive.Paths;

        await b.Coordinator.LeaveVaultAsync();
        Assert.False(b.Env.Sync.IsOn);
        Assert.Equal(0, b.Keys.Count);
        Assert.Equal(SyncPhase.Off, b.Coordinator.Status.Phase);
        Assert.Equal(SyncPhase.Off, (await b.SyncAsync()).Phase);
        Assert.Equal(tables, await b.TablesAsync());
        Assert.Equal(files, _world.Drive.Paths);

        // Writes go straight to the tables while sync is off.
        var bill = await BillAsync(b, B1);
        bill.Notes = "Offline note";
        await b.Env.Routed.UpsertBillAsync(bill);
        Assert.Equal("Offline note", (await b.Env.Db.GetAllBillsAsync()).Single(x => x.Id == B1).Notes);

        _world.Advance(1000);
        var again = await b.Coordinator.JoinWithRecoveryPhraseAsync(phrase, replaceLocalData: true);
        Assert.Equal(JoinVaultOutcome.Joined, again.Outcome);
        Assert.NotEqual(oldId, again.DeviceId);
        Assert.NotNull(again.BackupFileName);
        await SyncAllAsync(a, b);
        await AssertConvergedAsync(a, b);
    }

    [Fact]
    public async Task ADeviceThatSyncedBefore_CreatesANewVaultUnderANewIdentity()
    {
        var (a, _) = await CreatorAsync();
        var oldId = a.DeviceId;
        // The vault disappears from the drive.
        foreach (var path in _world.Drive.Paths) _world.Drive.Remove(path);
        _world.Advance(1000);
        Assert.Equal(SyncPhase.VaultMissing, (await a.SyncAsync()).Phase);

        var created = await a.Coordinator.CreateVaultAsync();
        Assert.Equal(CreateVaultOutcome.Created, created.Outcome);
        Assert.NotEqual(oldId, a.DeviceId);
        // The first change file carries only the new identity's records.
        var records = a.Env.Sync.Replica!.Read((s, _) => s.AllRecords());
        Assert.Contains($"devices/{a.DeviceId}/000001.chg", _world.Drive.Paths);
        Assert.All(records.Where(r => r.Entity != SyncEntities.Device), r => Assert.Equal(a.DeviceId, r.Hlc.DeviceId));
        Assert.Equal(6, (await a.Env.Db.GetAllBillsAsync()).Count);

        var b = await JoinerAsync("Windows Laptop", created.RecoveryPhrase!);
        await SyncAllAsync(a, b);
        await AssertConvergedAsync(a, b);
    }
}
