using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PayDay.Sync;
using PayDay.Sync.Coordination;
using PayDay.Sync.Drive;
using PayDay.Sync.Keys;
using PayDay.Tests.Sync.Local;

namespace PayDay.Tests.Sync.Loop;

/// <summary>The scheduler's triggers, the in-memory drive's simulated behaviour and the vault key stores.</summary>
public sealed class SyncLoopPartsTests
{
    // ---- Scheduler (sync-protocol.md 10.1, "Sync triggers") -------------------------------------------

    private static (SyncScheduler Scheduler, ManualTimeProvider Time, Func<int> Runs) NewScheduler()
    {
        var time = new ManualTimeProvider(SyncTestEnvironment.T0);
        var runs = 0;
        var scheduler = new SyncScheduler(_ =>
        {
            runs++;
            return Task.CompletedTask;
        }, time);
        return (scheduler, time, () => runs);
    }

    [Fact]
    public async Task Scheduler_SyncsAtLaunch_ThenEveryFiveMinutesInTheForeground()
    {
        var (s, time, runs) = NewScheduler();
        using var _ = s;
        Assert.Null(s.NextDue);
        Assert.False(await s.RunDueAsync());

        s.NotifyLaunch();
        Assert.True(await s.RunDueAsync());
        Assert.Equal(1, runs());
        Assert.Equal(time.GetUtcNow() + TimeSpan.FromMinutes(5), s.NextDue);

        time.Advance(299_000);
        Assert.False(await s.RunDueAsync());
        time.Advance(1000);
        Assert.True(await s.RunDueAsync());
        Assert.Equal(2, runs());
    }

    [Fact]
    public async Task Scheduler_SyncsFiveSecondsAfterTheLastEdit()
    {
        var (s, time, runs) = NewScheduler();
        using var _ = s;
        s.NotifyLocalEdit();
        time.Advance(3000);
        s.NotifyLocalEdit(); // debounced: the timer restarts
        time.Advance(4000);
        Assert.False(await s.RunDueAsync());
        time.Advance(1000);
        Assert.True(await s.RunDueAsync());
        Assert.Equal(1, runs());
        Assert.False(await s.RunDueAsync());
    }

    [Fact]
    public async Task Scheduler_InTheBackground_HasNoTimer_ButEditsAndSyncNowStillRun_AndForegroundSyncsAtOnce()
    {
        var (s, time, runs) = NewScheduler();
        using var _ = s;
        s.NotifyLaunch();
        await s.RunDueAsync();
        s.NotifyForeground(false);
        Assert.False(s.InForeground);
        Assert.Null(s.NextDue);
        time.Advance(3_600_000);
        Assert.False(await s.RunDueAsync());

        s.NotifyLocalEdit();
        time.Advance(5000);
        Assert.True(await s.RunDueAsync());
        s.SyncNow();
        Assert.True(await s.RunDueAsync());
        s.NotifyForeground(true);
        Assert.True(await s.RunDueAsync());
        Assert.Equal(4, runs());
    }

    [Fact]
    public async Task Scheduler_KeepsRunning_WhenASyncThrows()
    {
        var time = new ManualTimeProvider(SyncTestEnvironment.T0);
        using var s = new SyncScheduler(_ => throw new InvalidOperationException("drive exploded"), time);
        s.SyncNow();
        Assert.True(await s.RunDueAsync());
        Assert.IsType<InvalidOperationException>(s.LastError);
        Assert.NotNull(s.NextDue);
    }

    [Fact]
    public async Task Scheduler_BackgroundLoop_RunsOnTriggers()
    {
        var ran = new SemaphoreSlim(0);
        using var s = new SyncScheduler(_ =>
        {
            ran.Release();
            return Task.CompletedTask;
        }, TimeProvider.System, editDebounce: TimeSpan.FromMilliseconds(50), interval: TimeSpan.FromHours(1));
        s.Start();
        s.NotifyLaunch();
        Assert.True(await ran.WaitAsync(TimeSpan.FromSeconds(10)));
        s.NotifyLocalEdit();
        Assert.True(await ran.WaitAsync(TimeSpan.FromSeconds(10)));
        await s.StopAsync();
        Assert.Equal(2, s.Runs);
    }

    [Fact]
    public async Task Scheduler_AttachedToLocalSync_SyncsAfterAnAppWrite()
    {
        using var env = new SyncTestEnvironment();
        await env.InitializeAsync();
        env.InsertLegacyData();
        await env.TurnOnAsync();
        var runs = 0;
        using var s = new SyncScheduler(_ =>
        {
            runs++;
            return Task.CompletedTask;
        }, env.Time);
        s.Attach(env.Sync);
        await env.Routed.SetSettingAsync("PayAnchor", "2025-03-06");
        env.Time.Advance(5000);
        Assert.True(await s.RunDueAsync());
        Assert.Equal(1, runs);
        s.Detach();
        await env.Routed.SetSettingAsync("PayAnchor", "2025-03-13");
        env.Time.Advance(5000);
        Assert.False(await s.RunDueAsync());
    }

    // ---- In-memory drive ----------------------------------------------------------------------------

    [Fact]
    public async Task InMemoryDrive_LikeOneDrive_RefusesASecondFileWithOneName_AndKeepsDeletedFiles()
    {
        var clock = new ManualTimeProvider(SyncTestEnvironment.T0);
        var drive = new InMemoryDriveProvider(clock, enforcesUniqueNames: true, deletesPermanently: false) { ServerClockOffset = TimeSpan.FromMinutes(3) };
        var item = await drive.UploadNewAsync("devices/0a0a0a0a0a0a0a0a/000001.chg", new byte[] { 1 });
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(SyncTestEnvironment.T0) + TimeSpan.FromMinutes(3), item.CreatedTime);
        var e = await Assert.ThrowsAsync<DriveException>(() => drive.UploadNewAsync(item.Path, new byte[] { 2 }));
        Assert.Equal(DriveErrorKind.AlreadyExists, e.Kind);

        var root = await drive.ListAsync(string.Empty);
        Assert.Equal(new[] { "devices" }, root.Folders);
        var devices = await drive.ListAsync("devices");
        Assert.Equal(new[] { "0a0a0a0a0a0a0a0a" }, devices.Folders);
        Assert.Equal(drive.ServerNow, devices.ServerTime);
        Assert.Equal("000001.chg", Assert.Single((await drive.ListAsync("devices/0a0a0a0a0a0a0a0a")).Files).Name);

        await drive.DeleteAsync(item);
        await drive.DeleteAsync(item); // already gone: not an error
        Assert.Empty(drive.Paths);
        Assert.Equal(new[] { item.Path }, drive.RecycleBin);
        Assert.Equal(DriveErrorKind.NotFound, (await Assert.ThrowsAsync<DriveException>(() => drive.DownloadAsync(item))).Kind);
    }

    [Fact]
    public async Task InMemoryDrive_LikeGoogleDrive_ListsTwoFilesWithOneName()
    {
        var drive = new InMemoryDriveProvider(new ManualTimeProvider(SyncTestEnvironment.T0), enforcesUniqueNames: false);
        var a = await drive.UploadNewAsync("vault.json", new byte[] { 1 });
        var b = await drive.UploadNewAsync("vault.json", new byte[] { 2 });
        var files = (await drive.ListAsync(string.Empty)).Files;
        Assert.Equal(2, files.Count);
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(new byte[] { 2 }, await drive.DownloadAsync(b));
    }

    [Fact]
    public async Task InMemoryDrive_SimulatesLaggingListings_Failures_AndLostReplies()
    {
        var clock = new ManualTimeProvider(SyncTestEnvironment.T0);
        var drive = new InMemoryDriveProvider(clock) { ListingDelay = TimeSpan.FromSeconds(10) };
        await drive.UploadNewAsync("snapshots/x.snap", new byte[] { 1 });
        Assert.Empty((await drive.ListAsync("snapshots")).Files);
        clock.Advance(10_000);
        Assert.Single((await drive.ListAsync("snapshots")).Files);

        drive.HideFromListings("snapshots/x.snap", 2);
        Assert.Empty((await drive.ListAsync("snapshots")).Files);
        Assert.Empty((await drive.ListAsync("snapshots")).Files);
        Assert.Single((await drive.ListAsync("snapshots")).Files);

        drive.FailNext(DriveOperation.List, "pairing");
        Assert.Single((await drive.ListAsync("snapshots")).Files);
        await Assert.ThrowsAsync<DriveException>(() => drive.ListAsync("pairing"));
        Assert.Empty((await drive.ListAsync("pairing")).Files);

        drive.LoseNextUploadReply();
        await Assert.ThrowsAsync<DriveException>(() => drive.UploadNewAsync("pairing/p.pair", new byte[] { 3 }));
        Assert.True(drive.Exists("pairing/p.pair"));
        Assert.Equal(drive.ServerNow, await drive.GetServerTimeAsync());
    }

    // ---- Vault key stores ---------------------------------------------------------------------------

    [Fact]
    public void CredentialVaultKeyStore_KeepsOneKeyPerVaultAndGeneration_AsHexInTheCredentialStore()
    {
        var credentials = new InMemoryCredentialStore();
        var store = new CredentialVaultKeyStore(credentials);
        const string vault = "019a0000-0000-7000-8000-00000000fa01";
        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

        Assert.Null(store.Load(vault, 1));
        store.Save(vault, 1, key);
        Assert.Equal(key, store.Load(vault, 1));
        Assert.Null(store.Load(vault, 2));
        Assert.Equal(Convert.ToHexStringLower(key), credentials.Get("VaultKey/019a0000-0000-7000-8000-00000000fa01/1"));
        Assert.Equal(SyncErrorCodes.BadKeyLength, Assert.Throws<SyncException>(() => store.Save(vault, 1, new byte[16])).Code);

        credentials.Set(CredentialVaultKeyStore.KeyName(vault, 3), "not hex at all, but sixty-four characters long, so it is checked");
        Assert.Null(store.Load(vault, 3));
        store.Delete(vault, new long[] { 1, 3 });
        Assert.Null(store.Load(vault, 1));
        Assert.Empty(credentials.Items);
    }

    [Fact]
    public void InMemoryVaultKeyStore_ReturnsCopies()
    {
        var store = new InMemoryVaultKeyStore();
        var key = new byte[32];
        key[0] = 7;
        store.Save("v", 1, key);
        var loaded = store.Load("v", 1)!;
        loaded[0] = 0;
        Assert.Equal(7, store.Load("v", 1)![0]);
        store.Clear();
        Assert.Equal(0, store.Count);
    }
}
