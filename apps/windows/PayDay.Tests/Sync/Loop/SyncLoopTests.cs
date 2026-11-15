using System;
using System.Linq;
using System.Threading.Tasks;
using PayDay.Models;
using PayDay.Services;
using PayDay.Sync;
using PayDay.Sync.Coordination;
using PayDay.Sync.Drive;
using PayDay.Sync.Files;
using PayDay.Sync.Ids;
using PayDay.Sync.Local;
using PayDay.Sync.Model;
using PayDay.Tests.Sync.Local;
using PayDay.ViewModels;

namespace PayDay.Tests.Sync.Loop;

/// <summary>
/// End-to-end: two or three devices (separate SQLite files) on one in-memory drive create, join,
/// edit concurrently, compact, restore and hand the Notion connector over, and always converge on the
/// same records and the same tables. All data is fictional.
/// </summary>
public sealed class SyncLoopTests : IDisposable
{
    private readonly SyncWorld _world = new();

    public void Dispose() => _world.Dispose();

    private static string B1 => LegacyMapping.MapBillId("1");

    private static string B13 => LegacyMapping.MapBillId("13");

    /// <summary>A desktop with today's data creates the vault.</summary>
    private async Task<(SyncDevice Device, string Phrase)> CreatorAsync(bool notionToken = false)
    {
        var a = await _world.AddDeviceAsync("Windows PC", notionToken: notionToken);
        a.Env.InsertLegacyData();
        _world.Advance(1000);
        var created = await a.Coordinator.CreateVaultAsync();
        Assert.Equal(CreateVaultOutcome.Created, created.Outcome);
        Assert.Equal(SyncPhase.Idle, created.Status.Phase);
        return (a, created.RecoveryPhrase!);
    }

    private async Task<SyncDevice> JoinerAsync(string name, string phrase, long clockSkewMs = 0)
    {
        var device = await _world.AddDeviceAsync(name, clockSkewMs);
        _world.Advance(1000);
        var joined = await device.Coordinator.JoinWithRecoveryPhraseAsync(phrase, replaceLocalData: false);
        Assert.Equal(JoinVaultOutcome.Joined, joined.Outcome);
        return device;
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

    // ---- Create and join ----------------------------------------------------------------------------

    [Fact]
    public async Task CreateVault_WritesTheFirstChangeFileThenVaultJson_AndAnotherDeviceJoinsByPhrase()
    {
        var (a, phrase) = await CreatorAsync();

        Assert.Equal(24, phrase.Split(' ').Length);
        Assert.Equal(new[] { $"devices/{a.DeviceId}/000001.chg", "vault.json" }, _world.Drive.Paths);
        var vault = VaultFile.CheckBytes(_world.Drive.Read("vault.json"));
        Assert.Equal(a.DeviceId, vault.CreatedBy);
        Assert.Equal(a.Env.Sync.Replica!.Read((_, m) => m.VaultId), vault.VaultId);
        // The pre-sync backup is kept, the device row is in the first file, nothing is left to upload.
        Assert.Contains(a.Env.Store.WriteHistory, n => n.StartsWith(BackupRotationService.PreSyncFileNamePrefix, StringComparison.Ordinal));
        Assert.Equal(SyncValue.String("Windows PC"), a.Env.Value(SyncEntities.Device, a.DeviceId, "name"));
        Assert.Equal(0, a.Coordinator.Status.PendingUploads);
        Assert.Null(a.Env.Value(SyncEntities.Setting, SyncSettingKeys.NotionOwnerDeviceId, SyncEntities.ValueField));

        var b = await JoinerAsync("Windows Laptop", phrase);
        Assert.NotEqual(a.DeviceId, b.DeviceId);
        Assert.Equal(SyncPhase.Idle, b.Coordinator.Status.Phase);
        Assert.Contains($"devices/{b.DeviceId}/000001.chg", _world.Drive.Paths);

        _world.Advance(1000);
        await a.SyncAsync();
        await AssertConvergedAsync(a, b);
        Assert.Equal(6, (await b.Env.Db.GetAllBillsAsync()).Count);
        Assert.Equal(SyncValue.String("Windows Laptop"), a.Env.Value(SyncEntities.Device, b.DeviceId, "name"));

        // The joined device's pages read the vault's data.
        var payDay = new PayDayPageViewModel(b.Env.Routed);
        await payDay.LoadAsync(SyncTestEnvironment.Today);
        Assert.Contains(payDay.PaidBills, r => r.Bill.Name == "Contoso Phone Plan");
    }

    [Fact]
    public async Task CreateVault_RefusesWhenTheDriveAlreadyHasOne()
    {
        var (_, phrase) = await CreatorAsync();
        var b = await _world.AddDeviceAsync("Windows Laptop");
        b.Env.InsertLegacyData();

        var result = await b.Coordinator.CreateVaultAsync();
        Assert.Equal(CreateVaultOutcome.VaultExists, result.Outcome);
        Assert.False(b.Env.Sync.IsOn);
        Assert.Empty(b.Env.Store.WriteHistory);
        _ = phrase;
    }

    [Fact]
    public async Task Join_AsksBeforeReplacingThisDevicesData_ThenBacksItUpAndReplacesIt()
    {
        var (a, phrase) = await CreatorAsync();
        var b = await _world.AddDeviceAsync("Windows Laptop");
        b.Env.Exec("INSERT INTO Bills (Id, Name, Type, Payment) VALUES ('99', 'Local only bill', 'Other', 10);");
        var before = b.Env.Dump();

        var ask = await b.Coordinator.JoinWithRecoveryPhraseAsync(phrase, replaceLocalData: false);
        Assert.Equal(JoinVaultOutcome.NeedsConsent, ask.Outcome);
        Assert.Equal(before, b.Env.Dump());
        Assert.False(b.Env.Sync.IsOn);
        Assert.Equal(0, b.Keys.Count);

        _world.Advance(1000);
        var joined = await b.Coordinator.JoinWithRecoveryPhraseAsync(phrase, replaceLocalData: true);
        Assert.Equal(JoinVaultOutcome.Joined, joined.Outcome);
        Assert.StartsWith(BackupRotationService.PreJoinFileNamePrefix, joined.BackupFileName, StringComparison.Ordinal);
        var backup = BackupSerializer.FromJson(b.Env.Store.Snapshot()[joined.BackupFileName!]);
        Assert.Equal("Local only bill", Assert.Single(backup.Bills).Name);
        Assert.DoesNotContain(await b.Env.Db.GetAllBillsAsync(), x => x.Name == "Local only bill");
        await a.SyncAsync();
        await AssertConvergedAsync(a, b);
    }

    [Fact]
    public async Task Join_RefusesAWrongPhrase_AndAPhraseOfAnotherVault()
    {
        var (_, phrase) = await CreatorAsync();
        var b = await _world.AddDeviceAsync("Windows Laptop");

        var words = phrase.Split(' ');
        var wrongCount = await b.Coordinator.JoinWithRecoveryPhraseAsync(string.Join(' ', words.Skip(1)), false);
        Assert.Equal((JoinVaultOutcome.WrongPhrase, SyncErrorCodes.WrongWordCount), (wrongCount.Outcome, wrongCount.ErrorCode));
        var other = PayDay.Sync.Crypto.RecoveryPhrase.EncodeToString(new byte[32]);
        var mismatch = await b.Coordinator.JoinWithRecoveryPhraseAsync(other, false);
        Assert.Equal(JoinVaultOutcome.KeyMismatch, mismatch.Outcome);
        Assert.False(b.Env.Sync.IsOn);

        // Typed in capitals with extra spaces and a no-break space, it still unlocks (section 8).
        var typed = "  " + string.Join("  ", words).ToUpperInvariant().Replace("  ", "  ", StringComparison.Ordinal) + "\n";
        Assert.Equal(JoinVaultOutcome.Joined, (await b.Coordinator.JoinWithRecoveryPhraseAsync(typed, false)).Outcome);
    }

    [Fact]
    public async Task Join_ByQrPairing_DeletesThePairingFile_AndTheOldDeviceFinishesPairing()
    {
        var (a, _) = await CreatorAsync();
        var session = await a.Coordinator.StartPairingAsync();
        Assert.NotNull(session);
        Assert.StartsWith("FCPAIR:1:", session!.QrText, StringComparison.Ordinal);
        Assert.Contains(session.PairingPath, _world.Drive.Paths);
        Assert.True(a.Coordinator.IsPairing);

        var c = await _world.AddDeviceAsync("Surface Go");
        _world.Advance(1000);
        var joined = await c.Coordinator.JoinWithPairingCodeAsync(session.QrText.ToLowerInvariant() + " ", replaceLocalData: false);
        Assert.Equal(JoinVaultOutcome.Joined, joined.Outcome);
        Assert.DoesNotContain(session.PairingPath, _world.Drive.Paths);

        _world.Advance(1000);
        await a.SyncAsync();
        Assert.False(a.Coordinator.IsPairing);
        await AssertConvergedAsync(a, c);
    }

    [Fact]
    public async Task Pairing_RefusesAnExpiredOrForeignCode_AndStaleFilesAreCleanedUp()
    {
        var (a, _) = await CreatorAsync();
        var c = await _world.AddDeviceAsync("Surface Go");

        Assert.Equal(JoinVaultOutcome.BadPairingCode, (await c.Coordinator.JoinWithPairingCodeAsync("FCPAIR:1:not-a-code", false)).Outcome);
        var stranger = PayDay.Sync.Crypto.PairingQr.Format(PayDay.Sync.Crypto.PairingQr.NewCode());
        Assert.Equal(JoinVaultOutcome.PairingNotFound, (await c.Coordinator.JoinWithPairingCodeAsync(stranger, false)).Outcome);

        var session = await a.Coordinator.StartPairingAsync();
        _world.Advance(SyncCoordinator.PairingLifetime.Ticks / TimeSpan.TicksPerMillisecond + 1000);
        Assert.Equal(JoinVaultOutcome.PairingExpired, (await c.Coordinator.JoinWithPairingCodeAsync(session!.QrText, false)).Outcome);
        Assert.False(c.Env.Sync.IsOn);

        // A pairing screen that was killed: any device's next sync deletes the stale file.
        Assert.Contains(session.PairingPath, _world.Drive.Paths);
        var b = await JoinerAsync("Windows Laptop", await RecoveryFrom(a));
        Assert.DoesNotContain(_world.Drive.Paths, p => p.StartsWith("pairing/", StringComparison.Ordinal));
    }

    /// <summary>The creator's recovery phrase, from its key store (tests only).</summary>
    private static Task<string> RecoveryFrom(SyncDevice device)
    {
        var vaultId = device.Env.Sync.Replica!.Read((_, m) => m.VaultId)!;
        var key = device.Keys.Load(vaultId, 1)!;
        return Task.FromResult(PayDay.Sync.Crypto.RecoveryPhrase.EncodeToString(key));
    }
}
