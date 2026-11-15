using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PayDay.Services;
using PayDay.Sync.Coordination;
using PayDay.Sync.Crypto;
using PayDay.Sync.Drive;
using PayDay.Sync.Files;
using PayDay.Sync.Keys;
using PayDay.Sync.Local;
using PayDay.Sync.Model;
using PayDay.Tests.Sync.Local;

namespace PayDay.Tests.Sync.Loop;

/// <summary>
/// The fixes the PR #6 review asked for before sync ships, end to end on the in-memory drive: the
/// Notion connector is claimed only by an explicit action and released on leaving, a lost creation
/// race cleans up, creation failures are reported as failures, a locked device unlocks by pairing,
/// leaving and cancelling never throw, the 24-hour timers survive a restart, merges notify one at a
/// time, and the upload order and clash checks hold. All data is fictional.
/// </summary>
public sealed class SyncReviewFixTests : IDisposable
{
    private const long Hour = 60L * 60 * 1000;
    private const string Token = "secret_fictional_token";

    private readonly SyncWorld _world = new();

    public void Dispose() => _world.Dispose();

    private static string B1 => LegacyMapping.MapBillId("1");

    private async Task<(SyncDevice A, string Phrase)> CreatorAsync(bool notionToken = false)
    {
        var a = await _world.AddDeviceAsync("Windows PC", notionToken: notionToken);
        a.Env.InsertLegacyData();
        _world.Advance(1000);
        var created = await a.Coordinator.CreateVaultAsync();
        Assert.Equal(CreateVaultOutcome.Created, created.Outcome);
        return (a, created.RecoveryPhrase!);
    }

    private async Task<SyncDevice> JoinerAsync(string name, string phrase, bool notionToken = false)
    {
        var d = await _world.AddDeviceAsync(name, notionToken: notionToken);
        _world.Advance(1000);
        Assert.Equal(JoinVaultOutcome.Joined, (await d.Coordinator.JoinWithRecoveryPhraseAsync(phrase, replaceLocalData: false)).Outcome);
        return d;
    }

    private async Task SyncAllAsync(params SyncDevice[] devices)
    {
        for (var round = 0; round < 2; round++)
        {
            foreach (var d in devices)
            {
                _world.Advance(1000);
                await d.SyncAsync();
            }
        }
    }

    private static async Task RenameAsync(SyncDevice d, string name)
    {
        var bill = (await d.Env.Routed.GetAllBillsAsync()).Single(x => x.Id == B1);
        bill.Name = name;
        await d.Env.Routed.UpsertBillAsync(bill);
    }

    private static long LastUploaded(SyncDevice d) => d.Env.Sync.Replica!.Read((_, m) => m.LastUploadedSeq);

    private static SyncCoordinator CoordinatorFor(SyncDevice d, IDriveProvider drive, IVaultKeyStore keys, BackupRotationService? backups = null)
        => new(d.Env.Sync, drive, keys, backups ?? d.Env.Backups, d.Env.Db, new DeviceInfo(d.Name, "windows", "0.0.0-test"),
            () => d.Credentials.Exists(NotionSyncService.TokenKey), d.Env.Time);

    // ---- A2: the Notion connector is claimed only by connecting, and released on leaving -----------

    [Fact]
    public async Task TwoDevicesWithTokens_InAVaultWithNoOwner_NeitherClaimsTheConnectorByUsingNotion()
    {
        var (a, phrase) = await CreatorAsync();
        var b = await JoinerAsync("Windows Laptop", phrase, notionToken: true);
        var c = await JoinerAsync("Windows Tablet", phrase, notionToken: true);
        var bNotion = new FakeNotion(() => b.Env.Time.GetUtcNow());
        var cNotion = new FakeNotion(() => c.Env.Time.GetUtcNow());
        using var bService = new NotionSyncService(b.Env.Routed, b.Credentials, bNotion);
        using var cService = new NotionSyncService(c.Env.Routed, c.Credentials, cNotion);

        var bResult = await bService.SyncBillsAsync();
        var cResult = await cService.SyncBillsAsync();
        await bService.PushBillAsync((await b.Env.Routed.GetAllBillsAsync()).First());
        await SyncAllAsync(a, b, c);

        Assert.Contains("isn't connected on any device", Assert.Single(bResult.Errors), StringComparison.Ordinal);
        Assert.Contains("isn't connected on any device", Assert.Single(cResult.Errors), StringComparison.Ordinal);
        Assert.Empty(bNotion.Calls);
        Assert.Empty(cNotion.Calls);
        foreach (var d in new[] { a, b, c }) Assert.Equal(NotionRole.NoOwner, d.Env.Routed.GetNotionState().Role);

        // Connecting is the explicit action that claims it; the other device then stays out.
        await bService.ConnectAsync(Token);
        await SyncAllAsync(b, c);
        Assert.Equal(NotionRole.OwnerMustPushFirst, bService.GetConnectorState().Role);
        Assert.Equal((NotionRole.OtherDevice, "Windows Laptop"), (cService.GetConnectorState().Role, cService.GetConnectorState().OwnerDeviceName));
    }

    [Fact]
    public async Task LeavingAsTheNotionOwner_FreesTheConnector_AndThisDeviceWaitsForAReconnect()
    {
        var (a, phrase) = await CreatorAsync(notionToken: true);
        var b = await JoinerAsync("Windows Laptop", phrase);
        Assert.Equal(NotionRole.Owner, a.Env.Routed.GetNotionState().Role);

        _world.Advance(1000);
        var left = await a.Coordinator.LeaveVaultAsync();
        Assert.Equal((LeaveVaultOutcome.Left, NotionRelease.Released), (left.Outcome, left.Notion));
        await b.SyncAsync();
        Assert.Equal(NotionRole.NoOwner, b.Env.Routed.GetNotionState().Role);

        // Sync is off on the PC now, but its connector does not quietly resume against the same databases.
        var aNotion = new FakeNotion(() => a.Env.Time.GetUtcNow());
        using var aService = new NotionSyncService(a.Env.Routed, a.Credentials, aNotion);
        Assert.Equal(NotionRole.ReconnectRequired, (await aService.GetConnectorStateAsync()).Role);
        var refused = await aService.SyncBillsAsync();
        Assert.Contains("Save the Notion token again", Assert.Single(refused.Errors), StringComparison.Ordinal);
        await aService.PushBillAsync((await a.Env.Routed.GetAllBillsAsync()).First());
        Assert.Empty(aNotion.Calls);

        // Connecting Notion again is the user's explicit choice; then it runs as before sync.
        await aService.ConnectAsync(Token);
        Assert.Equal(NotionRole.SyncOff, (await aService.GetConnectorStateAsync()).Role);
        var ran = await aService.SyncBillsAsync();
        Assert.False(ran.HasErrors, string.Join("; ", ran.Errors));
        Assert.NotEmpty(aNotion.Calls);
    }

    [Fact]
    public async Task LeavingAsTheOwnerWhileOffline_SaysTheReleaseDidNotReachTheOtherDevices()
    {
        var (a, phrase) = await CreatorAsync(notionToken: true);
        var b = await JoinerAsync("Windows Laptop", phrase);
        var drive = new FlakyDrive(_world.Drive) { Offline = true };
        using var offline = CoordinatorFor(a, drive, a.Keys);

        var left = await offline.LeaveVaultAsync();

        Assert.Equal((LeaveVaultOutcome.Left, NotionRelease.NotUploaded), (left.Outcome, left.Notion));
        Assert.False(a.Env.Sync.IsOn);
        await b.SyncAsync();
        Assert.Equal(NotionRole.OtherDevice, b.Env.Routed.GetNotionState().Role);
    }

    // ---- A10: a new vault under a new identity does not keep the old owner -------------------------

    [Fact]
    public async Task CreatingANewVaultWithoutAToken_ClearsTheOldVaultsNotionOwner()
    {
        var (a, _) = await CreatorAsync(notionToken: true);
        var oldId = a.DeviceId;
        Assert.Equal(SyncValue.String(oldId), a.Env.Value(SyncEntities.Setting, SyncSettingKeys.NotionOwnerDeviceId, SyncEntities.ValueField));
        foreach (var path in _world.Drive.Paths) _world.Drive.Remove(path);
        a.Credentials.Delete(NotionSyncService.TokenKey);

        _world.Advance(1000);
        var created = await a.Coordinator.CreateVaultAsync();

        Assert.Equal(CreateVaultOutcome.Created, created.Outcome);
        Assert.NotEqual(oldId, a.DeviceId);
        Assert.Equal(SyncValue.Null, a.Env.Value(SyncEntities.Setting, SyncSettingKeys.NotionOwnerDeviceId, SyncEntities.ValueField));
        var b = await JoinerAsync("Windows Laptop", created.RecoveryPhrase!);
        Assert.Equal(NotionRole.NoOwner, b.Env.Routed.GetNotionState().Role);
    }

    // ---- A4: losing a creation race cleans up wherever it is noticed --------------------------------

    [Fact]
    public async Task LosingACreationRace_WhileVaultJsonWasNotUp_DeletesThisDevicesFiles()
    {
        var a = await _world.AddDeviceAsync("Windows PC");
        a.Env.InsertLegacyData();
        _world.Drive.FailNext(DriveOperation.Upload, SyncPaths.VaultFileName);
        _world.Advance(1000);
        var pending = await a.Coordinator.CreateVaultAsync();
        Assert.Equal(CreateVaultOutcome.CreatedUploadPending, pending.Outcome);
        Assert.True(_world.Drive.Exists(SyncPaths.ChangeFilePath(a.DeviceId, 1)));
        Assert.False(_world.Drive.Exists(SyncPaths.VaultFileName));

        // Another device creates its vault before the PC's next sync.
        var other = await _world.AddDeviceAsync("Windows Tablet");
        other.Env.Exec("INSERT INTO Bills (Id, Name, Type, Payment) VALUES ('5', 'Fabrikam Water', 'Bills', 30);");
        _world.Advance(1000);
        Assert.Equal(CreateVaultOutcome.Created, (await other.Coordinator.CreateVaultAsync()).Outcome);

        _world.Advance(1000);
        var status = await a.SyncAsync();
        Assert.Equal(SyncPhase.VaultReplaced, status.Phase);
        Assert.DoesNotContain(_world.Drive.Paths, p => p.StartsWith($"{SyncPaths.DevicesFolder}/{a.DeviceId}/", StringComparison.Ordinal));
        // Nobody is left with a file they cannot read.
        Assert.Empty((await other.SyncAsync()).Warnings);
    }

    // ---- A6: a failure before the vault exists is a failure ------------------------------------------

    [Fact]
    public async Task CreateVault_WhenThePreSyncBackupCannotBeWritten_FailsAndChangesNothing()
    {
        var a = await _world.AddDeviceAsync("Windows PC");
        a.Env.InsertLegacyData();
        var dump = a.Env.Dump();
        var backups = new BackupRotationService(a.Env.Db, new FailingBackupStore(), () => DateTime.UnixEpoch.AddMilliseconds(a.Env.Time.UnixMs));
        using var coordinator = CoordinatorFor(a, _world.Drive, a.Keys, backups);

        var result = await coordinator.CreateVaultAsync();

        Assert.Equal(CreateVaultOutcome.Failed, result.Outcome);
        Assert.Null(result.VaultId);
        Assert.Null(result.RecoveryPhrase);
        Assert.Equal(SyncPhase.Off, result.Status.Phase);
        Assert.Contains("could not be created", result.Status.Message, StringComparison.Ordinal);
        Assert.False(a.Env.Sync.IsOn);
        Assert.Equal(0, a.Keys.Count);
        Assert.Empty(_world.Drive.Paths);
        Assert.Equal(dump, a.Env.Dump());
    }

    // ---- A7: a locked device unlocks with a pairing code ---------------------------------------------

    [Fact]
    public async Task ALockedDevice_UnlocksWithAPairingCode_KeepingItsIdentityAndData()
    {
        var (a, phrase) = await CreatorAsync();
        var b = await JoinerAsync("Windows Laptop", phrase);
        var id = b.DeviceId;
        b.Keys.Clear();
        await RenameAsync(b, "Edited while locked");
        _world.Advance(1000);
        Assert.Equal(SyncPhase.Locked, (await b.SyncAsync()).Phase);

        var session = await a.Coordinator.StartPairingAsync();
        Assert.NotNull(session);
        var code = PairingQr.Parse(session!.QrText);
        var result = await b.Coordinator.JoinWithPairingCodeAsync(code, replaceLocalData: false);

        Assert.Equal(JoinVaultOutcome.Unlocked, result.Outcome);
        Assert.Equal(id, b.DeviceId);
        Assert.Equal(SyncPhase.Idle, result.Status.Phase);
        Assert.False(_world.Drive.Exists(session.PairingPath));
        // A11: the pairing secret does not linger in memory once the join returns.
        Assert.All(code.PairingSecret, x => Assert.Equal(0, x));
        await a.SyncAsync();
        Assert.Equal("Edited while locked", (await a.Env.Db.GetAllBillsAsync()).Single(x => x.Id == B1).Name);

        // With the key back, the same code path reports "already in the vault".
        var again = await b.Coordinator.JoinWithRecoveryPhraseAsync(phrase, replaceLocalData: false);
        Assert.Equal(JoinVaultOutcome.AlreadyInVault, again.Outcome);
    }

    [Fact]
    public async Task JoinWithAPairingCode_ZeroesTheSecret_EvenWhenItFails()
    {
        var (_, _) = await CreatorAsync();
        var b = await _world.AddDeviceAsync("Windows Laptop");
        var code = PairingQr.NewCode();

        var result = await b.Coordinator.JoinWithPairingCodeAsync(code, replaceLocalData: false);

        Assert.Equal(JoinVaultOutcome.PairingNotFound, result.Outcome);
        Assert.All(code.PairingSecret, x => Assert.Equal(0, x));
    }

    // ---- A8: leaving and cancelling report, never throw ----------------------------------------------

    [Fact]
    public async Task LeaveVault_WhenTheKeystoreRefusesToDelete_StillTurnsSyncOff_AndSaysSo()
    {
        var (_, phrase) = await CreatorAsync();
        var b = await JoinerAsync("Windows Laptop", phrase);
        using var coordinator = CoordinatorFor(b, _world.Drive, new ThrowingKeyStore(b.Keys));

        var left = await coordinator.LeaveVaultAsync();

        Assert.Equal(LeaveVaultOutcome.LeftKeyNotRemoved, left.Outcome);
        Assert.Contains("could not be removed", left.Message, StringComparison.Ordinal);
        Assert.False(b.Env.Sync.IsOn);
        Assert.Equal(SyncPhase.Off, left.Status.Phase);
    }

    [Fact]
    public async Task CancelPairing_WhenTheDriveThrowsSomethingUnexpected_ReturnsFalse()
    {
        var (a, _) = await CreatorAsync();
        var drive = new FlakyDrive(_world.Drive);
        using var coordinator = CoordinatorFor(a, drive, a.Keys);
        Assert.NotNull(await coordinator.StartPairingAsync());
        drive.ThrowOnDelete = true;

        Assert.False(await coordinator.CancelPairingAsync());
        var left = await coordinator.LeaveVaultAsync();
        Assert.Equal(LeaveVaultOutcome.Left, left.Outcome);
    }

    // ---- A12: upload order and the clash check -------------------------------------------------------

    [Fact]
    public async Task VaultJson_IsNotUploaded_WhenTheFirstChangeFileFailed()
    {
        var a = await _world.AddDeviceAsync("Windows PC");
        a.Env.InsertLegacyData();
        _world.Drive.FailNext(DriveOperation.Upload, SyncPaths.DevicesFolder + "/");
        _world.Advance(1000);

        var created = await a.Coordinator.CreateVaultAsync();

        Assert.Equal(CreateVaultOutcome.CreatedUploadPending, created.Outcome);
        Assert.NotNull(created.RecoveryPhrase);
        Assert.Empty(_world.Drive.Paths);
        var b = await _world.AddDeviceAsync("Windows Laptop");
        Assert.Equal(VaultProbeOutcome.NoVault, (await b.Coordinator.ProbeVaultAsync()).Outcome);

        _world.Advance(1000);
        Assert.Equal(SyncPhase.Idle, (await a.SyncAsync()).Phase);
        Assert.Equal(new[] { SyncPaths.ChangeFilePath(a.DeviceId, 1), SyncPaths.VaultFileName }, _world.Drive.Paths);
    }

    [Fact]
    public async Task AnUploadThatAlreadyExistsButIsNotListedYet_IsNotTakenAsUploaded_AndTheClashCheckStillRuns()
    {
        var (_, phrase) = await CreatorAsync();
        var b = await JoinerAsync("Windows Laptop", phrase);
        var clone = await _world.AddDeviceAsync("Windows Laptop (copy)", clonePath: _world.CloneDatabase(b));
        clone.Env.Sync.Load();
        var vaultId = b.Env.Sync.Replica!.Read((_, m) => m.VaultId)!;
        clone.Keys.Save(vaultId, 1, b.Keys.Load(vaultId, 1)!);

        await RenameAsync(b, "Name from the laptop");
        _world.Advance(1000);
        Assert.Equal(SyncPhase.Idle, (await b.SyncAsync()).Phase);
        var seq = LastUploaded(b);
        _world.Drive.HideFromListings(SyncPaths.ChangeFilePath(b.DeviceId, seq), 1);

        await RenameAsync(clone, "Name from the copy");
        _world.Advance(1000);
        var first = await clone.SyncAsync();

        // The name is taken but the file is not listed yet: not uploaded, and not a clash yet.
        Assert.Equal(SyncPhase.Offline, first.Phase);
        Assert.Equal(1, first.PendingUploads);
        Assert.Equal(seq - 1, LastUploaded(clone));

        // Once it is listed, its records are compared: they differ, so it is a clash.
        _world.Advance(1000);
        Assert.Equal(SyncPhase.IdentityClash, (await clone.SyncAsync()).Phase);
        Assert.Equal(seq - 1, LastUploaded(clone));
    }

    // ---- A13: the 24-hour timers survive a restart ----------------------------------------------------

    [Fact]
    public async Task AnUnreadableFile_IsReportedAfter24Hours_EvenAcrossARestart()
    {
        var (a, phrase) = await CreatorAsync();
        var b = await JoinerAsync("Windows Laptop", phrase);
        await RenameAsync(a, "Woodgrove Travel");
        _world.Advance(1000);
        await a.SyncAsync();
        var path = SyncPaths.ChangeFilePath(a.DeviceId, 2);
        var good = _world.Drive.Read(path);
        var bad = good.ToArray();
        bad[^1] ^= 0x01;
        _world.Drive.Overwrite(path, bad);
        Assert.Empty((await b.SyncAsync()).Warnings);

        // The app restarts 23 hours later: a new coordinator over the same database.
        _world.Advance(23 * Hour);
        using (var restarted = CoordinatorFor(b, _world.Drive, b.Keys))
        {
            Assert.Empty((await restarted.SyncAsync()).Warnings);
            _world.Advance(2 * Hour);
            var warning = Assert.Single((await restarted.SyncAsync()).Warnings);
            Assert.Equal(SyncWarningKind.UnreadableFile, warning.Kind);

            // Once the file opens, the timer is forgotten.
            _world.Drive.Overwrite(path, good);
            Assert.Empty((await restarted.SyncAsync()).Warnings);
        }
        Assert.Null(b.Env.Sync.GetDeviceSetting(SyncCoordinator.ProblemsSinceKey));
    }

    // ---- A3: DataChanged after each merge --------------------------------------------------------------

    [Fact]
    public async Task DataChanged_IsRaisedAfterEachMergedFile_NotOnceAtTheEnd()
    {
        var (a, phrase) = await CreatorAsync();
        var b = await JoinerAsync("Windows Laptop", phrase);
        await SyncAllAsync(a, b);
        await RenameAsync(a, "First name");
        _world.Advance(1000);
        await a.SyncAsync();
        await RenameAsync(a, "Second name");
        _world.Advance(1000);
        await a.SyncAsync();

        var seen = new List<string>();
        b.Env.Sync.DataChanged += (_, e) =>
        {
            if (e.Rows.Any(r => r.Id == B1)) seen.Add(b.Env.Sync.Replica!.Read((s, _) => s.Value(SyncEntities.Bill, B1, "name")!.Value.StringValue));
        };
        _world.Advance(1000);
        await b.SyncAsync();

        Assert.Equal(new[] { "First name", "Second name" }, seen);
    }

    // ---- Test doubles ---------------------------------------------------------------------------------

    private sealed class FailingBackupStore : IBackupStore
    {
        public Task WriteAsync(string fileName, string content, CancellationToken ct = default) => throw new IOException("The disk is full.");

        public Task<IReadOnlyList<BackupEntry>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<BackupEntry>>(Array.Empty<BackupEntry>());

        public Task<string> ReadAsync(string fileName, CancellationToken ct = default) => throw new FileNotFoundException(fileName);

        public Task DeleteAsync(string fileName, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>A keystore whose delete fails, as Credential Manager can.</summary>
    private sealed class ThrowingKeyStore : IVaultKeyStore
    {
        private readonly IVaultKeyStore _inner;

        public ThrowingKeyStore(IVaultKeyStore inner) => _inner = inner;

        public byte[]? Load(string vaultId, long generation) => _inner.Load(vaultId, generation);

        public void Save(string vaultId, long generation, ReadOnlySpan<byte> key) => _inner.Save(vaultId, generation, key);

        public void Delete(string vaultId, IEnumerable<long> generations) => throw new System.ComponentModel.Win32Exception(5, "Access is denied.");
    }

    /// <summary>A drive that can be offline (every call fails as transient) or whose delete throws a non-drive exception.</summary>
    private sealed class FlakyDrive : IDriveProvider
    {
        private readonly IDriveProvider _inner;

        public FlakyDrive(IDriveProvider inner) => _inner = inner;

        public bool Offline { get; set; }

        public bool ThrowOnDelete { get; set; }

        public string Name => _inner.Name;

        public bool EnforcesUniqueNames => _inner.EnforcesUniqueNames;

        public bool DeletesPermanently => _inner.DeletesPermanently;

        private void Check()
        {
            if (Offline) throw new DriveException(DriveErrorKind.Transient, "Offline.");
        }

        public Task<DriveFolderListing> ListAsync(string folder, CancellationToken ct = default)
        {
            Check();
            return _inner.ListAsync(folder, ct);
        }

        public Task<byte[]> DownloadAsync(DriveItem item, CancellationToken ct = default)
        {
            Check();
            return _inner.DownloadAsync(item, ct);
        }

        public Task<DriveItem> UploadNewAsync(string path, ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            Check();
            return _inner.UploadNewAsync(path, data, ct);
        }

        public Task DeleteAsync(DriveItem item, CancellationToken ct = default)
        {
            Check();
            return ThrowOnDelete ? throw new InvalidOperationException("Unexpected provider failure.") : _inner.DeleteAsync(item, ct);
        }

        public Task<DateTimeOffset> GetServerTimeAsync(CancellationToken ct = default)
        {
            Check();
            return _inner.GetServerTimeAsync(ct);
        }
    }
}
