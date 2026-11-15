using System;
using System.Linq;
using System.Threading.Tasks;
using PayDay.Sync.Coordination;
using PayDay.Sync.Drive;
using PayDay.Sync.Drive.OneDrive;
using PayDay.Sync.Files;
using PayDay.Sync.Keys;
using PayDay.Sync.Local;
using PayDay.Tests.Sync.Local;

namespace PayDay.Tests.Sync.OneDrive;

/// <summary>
/// The client-ID configuration, the chunked token cache in the credential store, the unconfigured
/// sign-in, and two devices syncing end to end through <see cref="OneDriveDriveProvider"/> over
/// <see cref="FakeGraph"/>. All data is fictional; no client ID is real.
/// </summary>
public sealed class OneDriveSupportTests
{
    private const string FictionalClientId = "00000000-0000-0000-0000-00000000c1d0";

    [Fact]
    public void TheClientId_IsEmptyInThisBuild_AndCanBeSuppliedByEnvironmentOrSetting()
    {
        Assert.Empty(OneDriveConfig.BuiltInClientId);
        Assert.Null(OneDriveConfig.Resolve(null, null));
        Assert.Null(OneDriveConfig.Resolve("not a guid", "  "));
        Assert.Equal(FictionalClientId, OneDriveConfig.Resolve(null, FictionalClientId));
        Assert.Equal(FictionalClientId, OneDriveConfig.Resolve($" {FictionalClientId.ToUpperInvariant()} ", "00000000-0000-0000-0000-000000000001"));
        Assert.Equal("00000000-0000-0000-0000-000000000001", OneDriveConfig.Resolve("bad", "00000000-0000-0000-0000-000000000001"));
        Assert.Equal(new[] { "Files.ReadWrite.AppFolder", "offline_access" }, OneDriveConfig.ConsentedScopes);
        Assert.Equal(new[] { "Files.ReadWrite.AppFolder" }, OneDriveConfig.RequestedScopes);
    }

    [Fact]
    public async Task WithoutAClientId_SignInSaysOneDriveIsNotConfigured_AndNoTokenIsGiven()
    {
        var auth = new OneDriveAuth(OneDriveConfig.Resolve(null, null), new InMemoryCredentialStore());

        Assert.False(auth.IsConfigured);
        var result = await auth.SignInAsync();
        Assert.Equal(DriveSignInOutcome.NotConfigured, result.Outcome);
        Assert.Equal("OneDrive isn't configured in this build.", result.Message);
        Assert.Null(await auth.GetSignedInUserAsync());
        Assert.Equal(DriveErrorKind.Unauthorized, (await Assert.ThrowsAsync<DriveException>(() => auth.GetAccessTokenAsync(false))).Kind);
        await auth.SignOutAsync();
    }

    [Fact]
    public void WithAClientId_TheClientIsBuiltWithoutSigningIn()
    {
        var credentials = new InMemoryCredentialStore();
        var auth = new OneDriveAuth(FictionalClientId, credentials);
        Assert.True(auth.IsConfigured);
        Assert.Equal("OneDrive", auth.ProviderName);
        Assert.False(credentials.Exists(CredentialTokenCacheStore.KeyPrefix + "/count"));
    }

    [Fact]
    public void TheTokenCache_IsSplitIntoChunksThatFitCredentialManager_AndReadBackWhole()
    {
        var credentials = new InMemoryCredentialStore();
        var store = new CredentialTokenCacheStore(credentials);
        var cache = new byte[5000];
        new Random(3).NextBytes(cache);

        store.Save(cache);

        Assert.Equal(cache, store.Load());
        var count = int.Parse(credentials.Get(CredentialTokenCacheStore.KeyPrefix + "/count")!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(7, count); // 6,668 base64 characters in chunks of 1,000
        for (var i = 0; i < count; i++)
        {
            Assert.True(credentials.Get($"{CredentialTokenCacheStore.KeyPrefix}/{i}")!.Length <= 1280);
        }

        // A smaller cache replaces it and leaves no stale chunk behind.
        store.Save(new byte[] { 1, 2, 3 });
        Assert.Equal(new byte[] { 1, 2, 3 }, store.Load());
        Assert.False(credentials.Exists($"{CredentialTokenCacheStore.KeyPrefix}/1"));

        // A missing chunk makes the cache empty (sign in again), never garbage.
        store.Save(cache);
        credentials.Delete($"{CredentialTokenCacheStore.KeyPrefix}/3");
        Assert.Null(store.Load());

        store.Clear();
        Assert.Null(store.Load());
        Assert.False(credentials.Exists(CredentialTokenCacheStore.KeyPrefix + "/count"));
        Assert.False(credentials.Exists($"{CredentialTokenCacheStore.KeyPrefix}/0"));
    }

    [Fact]
    public async Task TwoDevices_CreateJoinAndConverge_ThroughTheOneDriveProvider()
    {
        var graph = new FakeGraph(DateTimeOffset.FromUnixTimeMilliseconds(SyncTestEnvironment.T0)) { PageSize = 2 };
        using var a = new SyncTestEnvironment();
        using var b = new SyncTestEnvironment();
        await a.InitializeAsync();
        await b.InitializeAsync();
        a.InsertLegacyData();
        using var aDrive = new OneDriveDriveProvider(new FakeTokens(), graph, (_, _) => Task.CompletedTask);
        using var bDrive = new OneDriveDriveProvider(new FakeTokens(), graph, (_, _) => Task.CompletedTask);
        using var aSync = new SyncCoordinator(a.Sync, aDrive, new InMemoryVaultKeyStore(), a.Backups, a.Db, new DeviceInfo("Windows PC", "windows", "0.0.0-test"), time: a.Time);
        using var bSync = new SyncCoordinator(b.Sync, bDrive, new InMemoryVaultKeyStore(), b.Backups, b.Db, new DeviceInfo("Windows Laptop", "windows", "0.0.0-test"), time: b.Time);

        void Advance(long ms)
        {
            a.Time.Advance(ms);
            b.Time.Advance(ms);
            graph.Now = graph.Now.AddMilliseconds(ms);
        }

        Advance(1000);
        Assert.Equal(VaultProbeOutcome.NoVault, (await aSync.ProbeVaultAsync()).Outcome);
        var created = await aSync.CreateVaultAsync();
        Assert.Equal(CreateVaultOutcome.Created, created.Outcome);
        Assert.Equal(new[] { SyncPaths.ChangeFilePath(a.Sync.Replica!.DeviceId, 1), SyncPaths.VaultFileName }, graph.Paths);

        Advance(1000);
        Assert.Equal(VaultProbeOutcome.VaultExists, (await bSync.ProbeVaultAsync()).Outcome);
        var joined = await bSync.JoinWithRecoveryPhraseAsync(created.RecoveryPhrase!, replaceLocalData: false);
        Assert.Equal(JoinVaultOutcome.Joined, joined.Outcome);
        Assert.Equal(6, (await b.Db.GetAllBillsAsync()).Count);

        var bill = (await b.Routed.GetAllBillsAsync()).Single(x => x.Id == LegacyMapping.MapBillId("13"));
        bill.Notes = "Estimate from the laptop";
        await b.Routed.UpsertBillAsync(bill);
        Advance(1000);
        Assert.Equal(SyncPhase.Idle, (await bSync.SyncAsync()).Phase);
        Advance(1000);
        Assert.Equal(SyncPhase.Idle, (await aSync.SyncAsync()).Phase);

        Assert.Equal("Estimate from the laptop", (await a.Db.GetAllBillsAsync()).Single(x => x.Id == bill.Id).Notes);
        Assert.Equal(
            a.Sync.Replica!.Read((s, _) => s.AllRecords().Select(r => r.ToString()).ToList()),
            b.Sync.Replica!.Read((s, _) => s.AllRecords().Select(r => r.ToString()).ToList()));
    }
}
