using System;
using System.Linq;
using System.Threading.Tasks;
using PayDay.Models;
using PayDay.Sync.Coordination;
using PayDay.Sync.Drive;
using PayDay.Sync.Engine;
using PayDay.Sync.Files;
using PayDay.Sync.Local;
using PayDay.Sync.Model;
using PayDay.Tests.Sync.Local;

namespace PayDay.Tests.Sync.Loop;

/// <summary>
/// The sync loop when the drive misbehaves: failed and half-failed uploads, Google-style duplicate
/// names, listings that lag, clocks that disagree, a cloned install, a wiped keystore, a replaced or
/// missing vault and a corrupt file. Local edits are never lost and sync never throws.
/// All data is fictional.
/// </summary>
public sealed class SyncLoopFaultTests
{
    private const long Hour = 60L * 60 * 1000;

    private static string B1 => LegacyMapping.MapBillId("1");

    private static async Task<(SyncDevice A, SyncDevice B, string Phrase)> PairAsync(SyncWorld world)
    {
        var a = await world.AddDeviceAsync("Windows PC");
        a.Env.InsertLegacyData();
        world.Advance(1000);
        var created = await a.Coordinator.CreateVaultAsync();
        Assert.Equal(CreateVaultOutcome.Created, created.Outcome);
        var b = await world.AddDeviceAsync("Windows Laptop");
        world.Advance(1000);
        Assert.Equal(JoinVaultOutcome.Joined, (await b.Coordinator.JoinWithRecoveryPhraseAsync(created.RecoveryPhrase!, false)).Outcome);
        world.Advance(1000);
        await a.SyncAsync();
        return (a, b, created.RecoveryPhrase!);
    }

    private static async Task RenameAsync(SyncDevice d, string name)
    {
        var bill = (await d.Env.Routed.GetAllBillsAsync()).Single(x => x.Id == B1);
        bill.Name = name;
        await d.Env.Routed.UpsertBillAsync(bill);
    }

    private static async Task<string> NameAsync(SyncDevice d) => (await d.Env.Db.GetAllBillsAsync()).Single(x => x.Id == B1).Name;

    private static string Chg(SyncDevice d, long seq) => SyncPaths.ChangeFilePath(d.DeviceId, seq);

    private static long LastUploaded(SyncDevice d) => d.Env.Sync.Replica!.Read((_, m) => m.LastUploadedSeq);

    // ---- Uploads ------------------------------------------------------------------------------------

    [Fact]
    public async Task AFailedUpload_KeepsTheEdit_AndTheNextSyncUploadsIt()
    {
        using var world = new SyncWorld();
        var (a, b, _) = await PairAsync(world);
        await RenameAsync(a, "Woodgrove Travel");
        world.Drive.FailNext(DriveOperation.Upload);

        world.Advance(1000);
        var failed = await a.SyncAsync();
        Assert.Equal(SyncPhase.Offline, failed.Phase);
        Assert.Equal(1, failed.PendingUploads);
        await b.SyncAsync();
        Assert.Equal("Woodgrove Rewards", await NameAsync(b));

        // Another edit meanwhile goes into the next batch, uploaded strictly after the first.
        await RenameAsync(a, "Woodgrove Travel Card");
        world.Advance(1000);
        Assert.Equal(SyncPhase.Idle, (await a.SyncAsync()).Phase);
        Assert.True(world.Drive.Exists(Chg(a, 2)));
        Assert.True(world.Drive.Exists(Chg(a, 3)));
        await b.SyncAsync();
        Assert.Equal("Woodgrove Travel Card", await NameAsync(b));
        Assert.Equal(a.Records(), b.Records());
    }

    [Fact]
    public async Task AnUploadWhoseReplyWasLost_IsRecognisedOnRetry_WithoutADuplicate()
    {
        using var world = new SyncWorld();
        var (a, b, _) = await PairAsync(world);
        await RenameAsync(a, "Woodgrove Travel");
        world.Drive.LoseNextUploadReply();
        world.Advance(1000);
        Assert.Equal(SyncPhase.Offline, (await a.SyncAsync()).Phase);
        Assert.True(world.Drive.Exists(Chg(a, 2)));
        Assert.Equal(1, LastUploaded(a));

        world.Advance(1000);
        Assert.Equal(SyncPhase.Idle, (await a.SyncAsync()).Phase);
        Assert.Equal(2, LastUploaded(a));
        Assert.Single(world.Drive.Paths, p => p == Chg(a, 2));
        await b.SyncAsync();
        Assert.Equal("Woodgrove Travel", await NameAsync(b));
    }

    [Fact]
    public async Task OnADriveWithoutUniqueNames_ALostReplyIsFoundByListing_AndNotUploadedTwice()
    {
        using var world = new SyncWorld(enforcesUniqueNames: false);
        var (a, b, _) = await PairAsync(world);
        await RenameAsync(a, "Woodgrove Travel");
        world.Drive.LoseNextUploadReply();
        world.Advance(1000);
        await a.SyncAsync();
        world.Advance(1000);
        Assert.Equal(SyncPhase.Idle, (await a.SyncAsync()).Phase);
        Assert.Single(world.Drive.Paths, p => p == Chg(a, 2));
        await b.SyncAsync();
        Assert.Equal(a.Records(), b.Records());
    }

    // ---- Duplicate names (10.2) ---------------------------------------------------------------------

    [Fact]
    public async Task DuplicateNamesWithTheSameRecords_AreMergedOnce()
    {
        using var world = new SyncWorld(enforcesUniqueNames: false);
        var (a, b, _) = await PairAsync(world);
        await RenameAsync(a, "Woodgrove Travel");
        world.Advance(1000);
        await a.SyncAsync();
        world.Drive.AddCopy(Chg(a, 2), world.Drive.Read(Chg(a, 2)));

        var status = await b.SyncAsync();
        Assert.Equal(SyncPhase.Idle, status.Phase);
        Assert.Empty(status.Warnings);
        Assert.Equal("Woodgrove Travel", await NameAsync(b));
        Assert.Equal(2, b.Env.Sync.Replica!.Cursor(a.DeviceId));
    }

    [Fact]
    public async Task DuplicateNamesWithDifferentRecords_AreAnIdentityClash_AndNeitherIsMerged()
    {
        using var world = new SyncWorld(enforcesUniqueNames: false);
        var (a, b, _) = await PairAsync(world);
        await RenameAsync(a, "Woodgrove Travel");
        world.Advance(1000);
        await a.SyncAsync();

        // A second install with A's identity wrote a different file 2.
        var vaultId = a.Env.Sync.Replica!.Read((_, m) => m.VaultId)!;
        var key = a.Keys.Load(vaultId, 1)!;
        using (var codec = new VaultCodec(vaultId, new System.Collections.Generic.Dictionary<uint, byte[]> { [1] = key }, 1))
        {
            var other = new[] { new ChangeRecord(SyncEntities.Bill, B1, "name", SyncValue.String("Cloned name"), PayDay.Sync.Clock.Hlc.Encode(world.ServerClock.UnixMs, 0, a.DeviceId)) };
            world.Drive.AddCopy(Chg(a, 2), codec.SealChangeFile(a.DeviceId, 2, other).Data);
        }

        var status = await b.SyncAsync();
        var warning = Assert.Single(status.Warnings);
        Assert.Equal((SyncWarningKind.DuplicateIdentity, a.DeviceId), (warning.Kind, warning.DeviceId));
        Assert.Contains("Windows PC", warning.Message, StringComparison.Ordinal);
        Assert.Equal("Woodgrove Rewards", await NameAsync(b));
        Assert.Equal(1, b.Env.Sync.Replica!.Cursor(a.DeviceId));
    }

    [Fact]
    public async Task AClonedInstall_IsStoppedWithAnIdentityClash_AndNoDataIsLost()
    {
        using var world = new SyncWorld();
        var (a, b, phrase) = await PairAsync(world);
        // B's disk is cloned: the copy has B's device ID, cursors and next sequence number.
        var clone = await world.AddDeviceAsync("Windows Laptop (copy)", clonePath: world.CloneDatabase(b));
        clone.Env.Sync.Load();
        var vaultId = b.Env.Sync.Replica!.Read((_, m) => m.VaultId)!;
        clone.Keys.Save(vaultId, 1, b.Keys.Load(vaultId, 1)!);
        Assert.Equal(b.DeviceId, clone.DeviceId);

        await RenameAsync(b, "Name from the laptop");
        await RenameAsync(clone, "Name from the copy");
        world.Advance(1000);
        Assert.Equal(SyncPhase.Idle, (await b.SyncAsync()).Phase);
        world.Advance(1000);
        var clash = await clone.SyncAsync();
        Assert.Equal(SyncPhase.IdentityClash, clash.Phase);
        // It stays stopped, its edit kept locally, until it rejoins.
        Assert.Equal(SyncPhase.IdentityClash, (await clone.SyncAsync()).Phase);
        Assert.Equal("Name from the copy", await NameAsync(clone));
        Assert.Equal(1, clone.Coordinator.Status.PendingUploads);

        await clone.Coordinator.LeaveVaultAsync();
        world.Advance(1000);
        var rejoined = await clone.Coordinator.JoinWithRecoveryPhraseAsync(phrase, replaceLocalData: true);
        Assert.Equal(JoinVaultOutcome.Joined, rejoined.Outcome);
        Assert.NotEqual(b.DeviceId, rejoined.DeviceId);
        await a.SyncAsync();
        Assert.Equal("Name from the laptop", await NameAsync(a));
    }

    // ---- Listings and clocks ------------------------------------------------------------------------

    [Fact]
    public async Task ALaggingListing_DelaysChangesUntilTheyAreListed()
    {
        using var world = new SyncWorld();
        var (a, b, _) = await PairAsync(world);
        world.Advance(60_000);
        world.Drive.ListingDelay = TimeSpan.FromSeconds(30);
        await RenameAsync(a, "Woodgrove Travel");
        world.Advance(1000);
        await a.SyncAsync();
        await b.SyncAsync();
        Assert.Equal("Woodgrove Rewards", await NameAsync(b));

        world.Advance(31_000);
        await b.SyncAsync();
        Assert.Equal("Woodgrove Travel", await NameAsync(b));
    }

    [Fact]
    public async Task AGap_IsRetried_AndReportedOnlyAfter24Hours()
    {
        using var world = new SyncWorld();
        var (a, b, _) = await PairAsync(world);
        await RenameAsync(a, "Woodgrove Travel");
        world.Advance(1000);
        await a.SyncAsync();
        await RenameAsync(a, "Woodgrove Travel Card");
        world.Advance(1000);
        await a.SyncAsync();

        world.Drive.HideFromListings(Chg(a, 2), 1000);
        var status = await b.SyncAsync();
        Assert.Empty(status.Warnings);
        Assert.Equal(1, b.Env.Sync.Replica!.Cursor(a.DeviceId));
        // File 3 may already be merged above the gap; the cursor waits for file 2.
        world.Advance(25 * Hour);
        status = await b.SyncAsync();
        var warning = Assert.Single(status.Warnings);
        Assert.Equal(SyncWarningKind.MissingChanges, warning.Kind);
        Assert.Contains("Windows PC", warning.Message, StringComparison.Ordinal);

        world.Drive.HideFromListings(Chg(a, 2), 0);
        status = await b.SyncAsync();
        Assert.Empty(status.Warnings);
        Assert.Equal(3, b.Env.Sync.Replica!.Cursor(a.DeviceId));
        Assert.Equal("Woodgrove Travel Card", await NameAsync(b));
    }

    [Fact]
    public async Task ADeviceWhoseClockRunsAhead_IsReported_AndStillMerged()
    {
        using var world = new SyncWorld();
        var (a, _, phrase) = await PairAsync(world);
        var fast = await world.AddDeviceAsync("Windows Tablet", clockSkewMs: 2 * Hour);
        world.Advance(1000);
        Assert.Equal(JoinVaultOutcome.Joined, (await fast.Coordinator.JoinWithRecoveryPhraseAsync(phrase, false)).Outcome);
        await RenameAsync(fast, "Renamed on the tablet");
        world.Advance(1000);
        await fast.SyncAsync();

        world.Advance(1000);
        var status = await a.SyncAsync();
        var warning = Assert.Single(status.Warnings);
        Assert.Equal((SyncWarningKind.ClockAhead, fast.DeviceId), (warning.Kind, warning.DeviceId));
        Assert.Equal("Windows Tablet's clock is ahead; check its date and time.", warning.Message);
        Assert.Equal("Renamed on the tablet", await NameAsync(a));
    }

    [Fact]
    public async Task SnapshotAge_UsesTheProvidersClock_NotOnlyTheDevices()
    {
        using var world = new SyncWorld();
        var (a, _, _) = await PairAsync(world);
        for (var i = 0; i < 22; i++)
        {
            await RenameAsync(a, $"Name {i}");
            world.Advance(1000);
            await a.SyncAsync();
        }
        world.Advance(1000);
        await a.SyncAsync();
        Assert.Contains(world.Drive.Paths, p => p.StartsWith("snapshots/", StringComparison.Ordinal));

        // The device's clock says a week passed; the provider's says an hour did: nothing is deleted.
        a.Env.Time.Advance(8 * 24 * Hour);
        world.Advance(Hour);
        await a.SyncAsync();
        Assert.True(world.Drive.Exists(Chg(a, 2)));

        // Once the provider agrees, the covered files go.
        world.Advance(8 * 24 * Hour);
        await a.SyncAsync();
        Assert.False(world.Drive.Exists(Chg(a, 2)));
    }

    // ---- Keys and vaults ----------------------------------------------------------------------------

    [Fact]
    public async Task AWipedKeystore_LocksSync_UntilTheRecoveryPhraseIsEntered()
    {
        using var world = new SyncWorld();
        var (a, b, phrase) = await PairAsync(world);
        b.Keys.Clear();
        await RenameAsync(b, "Edited while locked");

        world.Advance(1000);
        var locked = await b.SyncAsync();
        Assert.Equal(SyncPhase.Locked, locked.Phase);
        Assert.Equal("Edited while locked", await NameAsync(b));

        Assert.Equal(UnlockOutcome.WrongPhrase, (await b.Coordinator.UnlockWithRecoveryPhraseAsync("not the words")).Outcome);
        var unlocked = await b.Coordinator.UnlockWithRecoveryPhraseAsync(phrase);
        Assert.Equal(UnlockOutcome.Unlocked, unlocked.Outcome);
        Assert.Equal(SyncPhase.Idle, unlocked.Status.Phase);
        await a.SyncAsync();
        Assert.Equal("Edited while locked", await NameAsync(a));
    }

    [Fact]
    public async Task AMissingOrReplacedVault_StopsSync_WithoutTouchingLocalData()
    {
        using var world = new SyncWorld();
        var (a, _, _) = await PairAsync(world);
        var tables = await a.TablesAsync();
        foreach (var path in world.Drive.Paths) world.Drive.Remove(path);

        world.Advance(1000);
        Assert.Equal(SyncPhase.VaultMissing, (await a.SyncAsync()).Phase);

        var other = await world.AddDeviceAsync("Windows Tablet");
        other.Env.Exec("INSERT INTO Bills (Id, Name, Type, Payment) VALUES ('5', 'Fabrikam Water', 'Bills', 30);");
        world.Advance(1000);
        Assert.Equal(CreateVaultOutcome.Created, (await other.Coordinator.CreateVaultAsync()).Outcome);

        world.Advance(1000);
        Assert.Equal(SyncPhase.VaultReplaced, (await a.SyncAsync()).Phase);
        Assert.Equal(tables, await a.TablesAsync());
    }

    [Fact]
    public async Task ACorruptFile_IsNotMerged_AndIsReportedAfter24Hours()
    {
        using var world = new SyncWorld();
        var (a, b, _) = await PairAsync(world);
        await RenameAsync(a, "Woodgrove Travel");
        world.Advance(1000);
        await a.SyncAsync();
        var bytes = world.Drive.Read(Chg(a, 2));
        bytes[^1] ^= 0x01;
        world.Drive.Overwrite(Chg(a, 2), bytes);

        var status = await b.SyncAsync();
        Assert.Equal(SyncPhase.Idle, status.Phase);
        Assert.Empty(status.Warnings);
        Assert.Equal("Woodgrove Rewards", await NameAsync(b));
        Assert.Equal(1, b.Env.Sync.Replica!.Cursor(a.DeviceId));

        world.Advance(25 * Hour);
        var warning = Assert.Single((await b.SyncAsync()).Warnings);
        Assert.Equal(SyncWarningKind.UnreadableFile, warning.Kind);
        Assert.Equal("A file from Windows PC could not be read.", warning.Message);
    }

    [Fact]
    public async Task DriveFailures_BecomeAStatus_NeverAnException()
    {
        using var world = new SyncWorld();
        var (a, _, _) = await PairAsync(world);
        await RenameAsync(a, "Kept through failures");
        foreach (var op in new[] { DriveOperation.List, DriveOperation.Download, DriveOperation.Upload })
        {
            world.Drive.FailNext(op);
            world.Advance(1000);
            var status = await a.SyncAsync();
            Assert.Equal(SyncPhase.Offline, status.Phase);
        }
        world.Drive.FailNext(DriveOperation.List, kind: DriveErrorKind.Unauthorized);
        Assert.Equal(SyncPhase.SignedOut, (await a.SyncAsync()).Phase);
        Assert.Equal("Kept through failures", await NameAsync(a));

        world.Advance(1000);
        Assert.Equal(SyncPhase.Idle, (await a.SyncAsync()).Phase);
        Assert.Equal(0, a.Coordinator.Status.PendingUploads);
    }

    [Fact]
    public async Task TurningSyncOffDuringACycle_EndsTheCycleQuietly()
    {
        using var world = new SyncWorld();
        var (a, _, _) = await PairAsync(world);
        await RenameAsync(a, "Edited");
        // Hold the write gate so the cycle waits at its first local step, then turn sync off under it.
        var lease = await a.Env.Sync.EnterWriteAsync();
        var cycle = a.SyncAsync();
        await a.Env.Sync.TurnOffAsync(lease);
        lease.Dispose();
        var status = await cycle;
        Assert.Equal(SyncPhase.Off, status.Phase);
        Assert.Equal("Edited", await NameAsync(a));
    }
}
