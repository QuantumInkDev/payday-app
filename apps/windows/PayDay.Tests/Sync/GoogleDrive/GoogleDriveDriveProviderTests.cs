using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using PayDay.Services;
using PayDay.Sync.Coordination;
using PayDay.Sync.Drive;
using PayDay.Sync.Drive.GoogleDrive;
using PayDay.Sync.Files;
using PayDay.Sync.Keys;
using PayDay.Sync.Local;
using PayDay.Tests.Sync.Local;

namespace PayDay.Tests.Sync.GoogleDrive;

/// <summary>
/// <see cref="GoogleDriveDriveProvider"/> against <see cref="FakeGoogleDrive"/>: the vault folder in the
/// app-data folder, folders and files that share names, paging, provider creation times and server
/// time, uploads that set no timestamps (multipart and resumable), downloads, permanent deletes, the
/// error and retry mapping, and two devices syncing end to end. No network; all data fictional.
/// </summary>
public sealed class GoogleDriveDriveProviderTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2025, 5, 22, 12, 0, 0, TimeSpan.Zero);
    private const string Device = "0a0a0a0a0a0a0a0a";

    private readonly FakeGoogleDrive _google = new(Start);
    private readonly FakeGoogleTokens _tokens = new();
    private readonly List<TimeSpan> _waits = new();
    private readonly GoogleDriveDriveProvider _drive;

    public GoogleDriveDriveProviderTests()
    {
        _drive = new GoogleDriveDriveProvider(_tokens, _google, (t, _) =>
        {
            _waits.Add(t);
            return Task.CompletedTask;
        });
    }

    public void Dispose() => _drive.Dispose();

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void ItIsGoogleDrive_WithoutUniqueNames_AndWithPermanentDeletes()
    {
        Assert.Equal("Google Drive", _drive.Name);
        Assert.False(_drive.EnforcesUniqueNames);
        Assert.True(_drive.DeletesPermanently);
    }

    [Fact]
    public async Task AnEmptyAppFolder_ListsEmpty_AtEveryPath()
    {
        var root = await _drive.ListAsync(string.Empty);
        var devices = await _drive.ListAsync("devices");

        Assert.Empty(root.Files);
        Assert.Empty(root.Folders);
        Assert.Empty(devices.Files);
        Assert.Equal(Start, root.ServerTime);
    }

    [Fact]
    public async Task Uploading_CreatesTheMissingFolders_AndSetsNoTimestamps()
    {
        var item = await _drive.UploadNewAsync($"devices/{Device}/000001.chg", Bytes("one"));
        _google.Now = Start.AddMinutes(1);
        await _drive.UploadNewAsync($"devices/{Device}/000002.chg", Bytes("two"));
        await _drive.UploadNewAsync("vault.json", Bytes("{}"));

        Assert.Equal($"devices/{Device}/000001.chg", item.Path);
        Assert.Equal(3, item.Size);
        Assert.Equal(Start, item.CreatedTime);
        Assert.Equal(new[] { "PayDay", "PayDay/devices", $"PayDay/devices/{Device}" }, _google.FolderPaths);
        Assert.Equal(new[] { $"devices/{Device}/000001.chg", $"devices/{Device}/000002.chg", "vault.json" }, _google.Paths);
        Assert.Equal("two", Encoding.UTF8.GetString(_google.Read($"devices/{Device}/000002.chg")));
        Assert.Empty(_google.ForbiddenFields);
        Assert.All(_google.Requests, r => Assert.Equal($"Bearer {FakeGoogleTokens.First}", r.Authorization));

        var root = await _drive.ListAsync(string.Empty);
        Assert.Equal(new[] { "vault.json" }, root.Files.Select(f => f.Path));
        Assert.Equal(new[] { "devices" }, root.Folders);
        var files = (await _drive.ListAsync($"devices/{Device}")).Files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        Assert.Equal(new DateTimeOffset?[] { Start, Start.AddMinutes(1) }, files.Select(f => f.CreatedTime));
        Assert.Equal(new long[] { 3, 3 }, files.Select(f => f.Size));
    }

    [Fact]
    public async Task FoldersWithTheSameName_AreListedTogether_AndUploadsGoToTheEarliest()
    {
        // Two devices created PayDay/devices at the same time, each before seeing the other's.
        var vault = _google.AddFolder("PayDay", created: Start);
        var later = _google.AddFolder("devices", vault, Start.AddSeconds(2));
        var earlier = _google.AddFolder("devices", vault, Start.AddSeconds(1));
        var a = _google.AddFolder("aaaaaaaaaaaaaaaa", later);
        var b = _google.AddFolder("bbbbbbbbbbbbbbbb", earlier);
        _google.AddFile("000001.chg", a, Bytes("a1"));
        _google.AddFile("000001.chg", b, Bytes("b1"));

        var devices = await _drive.ListAsync("devices");
        Assert.Equal(new[] { "aaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbb" }, devices.Folders);

        await _drive.UploadNewAsync($"devices/{Device}/000001.chg", Bytes("c1"));
        Assert.Contains($"PayDay/devices/{Device}", _google.FolderPaths);
        // The new device folder sits in the earlier "devices" folder.
        var listing = await _drive.ListAsync("devices");
        Assert.Equal(new[] { Device, "aaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbb" }, listing.Folders);
        Assert.Single((await _drive.ListAsync($"devices/{Device}")).Files);
        Assert.Equal(new[] { "bbbbbbbbbbbbbbbb", Device }, _google.ChildNames(earlier));
        Assert.Equal(new[] { "aaaaaaaaaaaaaaaa" }, _google.ChildNames(later));
    }

    [Fact]
    public async Task TopLevelItemsNamingTheAppFolderByItsAlias_AreFound()
    {
        _google.ReportAliasAsParent = true;
        await _drive.UploadNewAsync($"devices/{Device}/000001.chg", Bytes("one"));
        await _drive.UploadNewAsync("vault.json", Bytes("{}"));

        Assert.Equal(new[] { "vault.json" }, (await _drive.ListAsync(string.Empty)).Files.Select(f => f.Path));
        Assert.Single((await _drive.ListAsync($"devices/{Device}")).Files);
        Assert.Single(_google.FolderPaths, p => p == "PayDay");
    }

    private static bool IsUpload(HttpRequestMessage r) => r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.StartsWith("/upload/", StringComparison.Ordinal);

    private int Uploads => _google.Requests.Count(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath.StartsWith("/upload/", StringComparison.Ordinal));

    [Fact]
    public async Task ACreateThatMayHaveGoneThrough_IsNotRepeated()
    {
        // A 5xx or a lost connection after the request was sent: Drive may have created the file, and
        // the sync engine looks before uploading again.
        _google.FailWhen(IsUpload, _ => FakeGoogleDrive.Error(HttpStatusCode.ServiceUnavailable, "backendError"));
        var e = await Assert.ThrowsAsync<DriveException>(() => _drive.UploadNewAsync("snapshots/a.snap", Bytes("a")));
        Assert.Equal(DriveErrorKind.Transient, e.Kind);
        Assert.Equal(1, Uploads);

        _google.FailWhen(IsUpload, _ => throw new HttpRequestException("connection reset"));
        e = await Assert.ThrowsAsync<DriveException>(() => _drive.UploadNewAsync("snapshots/a.snap", Bytes("a")));
        Assert.Equal(DriveErrorKind.Transient, e.Kind);
        Assert.Equal(2, Uploads);
        Assert.Empty(_waits);
        Assert.Empty(_google.Paths);
    }

    [Fact]
    public async Task ACreateRefusedForTheRateLimit_IsRetried()
    {
        _google.FailWhen(IsUpload, _ => FakeGoogleDrive.Error(HttpStatusCode.Forbidden, "userRateLimitExceeded"));

        await _drive.UploadNewAsync("snapshots/a.snap", Bytes("a"));

        Assert.Equal(2, Uploads);
        Assert.Equal(new[] { "snapshots/a.snap" }, _google.Paths);
    }

    [Fact]
    public async Task FilesWithTheSameName_AreBothListed()
    {
        await _drive.UploadNewAsync("vault.json", Bytes("{\"a\":1}"));
        await _drive.UploadNewAsync("vault.json", Bytes("{\"b\":2}"));

        var files = (await _drive.ListAsync(string.Empty)).Files;

        Assert.Equal(2, files.Count);
        Assert.All(files, f => Assert.Equal("vault.json", f.Path));
        Assert.NotEqual(files[0].Id, files[1].Id);
    }

    [Fact]
    public async Task Listing_FollowsEveryPage()
    {
        _google.PageSize = 2;
        for (var i = 1; i <= 5; i++) await _drive.UploadNewAsync($"devices/{Device}/{i:D6}.chg", Bytes($"f{i}"));

        var files = (await _drive.ListAsync($"devices/{Device}")).Files;

        Assert.Equal(5, files.Count);
    }

    [Fact]
    public async Task FilesOutsideTheVaultFolder_AreNotListed()
    {
        var other = _google.AddFolder("Other");
        _google.AddFile("vault.json", other, Bytes("{}"));
        _google.AddFile("vault.json", FakeGoogleDrive.AppDataId, Bytes("{}"));

        Assert.Empty((await _drive.ListAsync(string.Empty)).Files);
    }

    [Fact]
    public async Task Downloading_AndDeleting_UseTheFileId()
    {
        var item = await _drive.UploadNewAsync("pairing/00112233445566778899aabbccddeeff.pair", Bytes("secret"));

        Assert.Equal("secret", Encoding.UTF8.GetString(await _drive.DownloadAsync(item)));
        await _drive.DeleteAsync(item);
        Assert.Equal(new[] { "pairing/00112233445566778899aabbccddeeff.pair" }, _google.Deleted);
        Assert.Empty(_google.Paths);

        await _drive.DeleteAsync(item); // already gone: not an error
        var e = await Assert.ThrowsAsync<DriveException>(() => _drive.DownloadAsync(item));
        Assert.Equal(DriveErrorKind.NotFound, e.Kind);
    }

    [Fact]
    public async Task ALargeFile_GoesUpResumably()
    {
        var data = new byte[GoogleDriveDriveProvider.MultipartLimit + 10];
        new Random(7).NextBytes(data);

        var item = await _drive.UploadNewAsync("snapshots/big.snap", data);

        Assert.Equal(data.Length, item.Size);
        Assert.Equal(data, _google.Read("snapshots/big.snap"));
        Assert.Contains(_google.Requests, r => r.Method == HttpMethod.Put && r.Uri.Query.Contains("upload_id", StringComparison.Ordinal));
        Assert.Empty(_google.ForbiddenFields);
    }

    [Fact]
    public async Task AnUploadSessionOnAnotherHost_IsRefused_AndGetsNoToken()
    {
        _google.SessionHost = "uploads.example.invalid";
        var data = new byte[GoogleDriveDriveProvider.MultipartLimit + 1];

        var e = await Assert.ThrowsAsync<DriveException>(() => _drive.UploadNewAsync("snapshots/big.snap", data));

        Assert.Equal(DriveErrorKind.Other, e.Kind);
        Assert.DoesNotContain(_google.Requests, r => r.Uri.Host != "www.googleapis.com");
    }

    [Fact]
    public async Task TheServerTime_IsTheDateHeader()
    {
        _google.Now = Start.AddHours(3);

        Assert.Equal(Start.AddHours(3), await _drive.GetServerTimeAsync());
    }

    // ---- Errors and retries -----------------------------------------------------------------------

    [Fact]
    public async Task A401_RefreshesTheTokenOnce_ThenSucceeds()
    {
        _google.ValidTokens.Remove(FakeGoogleTokens.First);

        await _drive.ListAsync(string.Empty);

        Assert.Equal(1, _tokens.Refreshes);
        Assert.Equal($"Bearer {FakeGoogleTokens.Refreshed}", _google.Requests[^1].Authorization);
    }

    [Fact]
    public async Task A401AfterTheRefresh_MeansSignInAgain()
    {
        _google.ValidTokens.Clear();

        var e = await Assert.ThrowsAsync<DriveException>(() => _drive.ListAsync(string.Empty));

        Assert.Equal(DriveErrorKind.Unauthorized, e.Kind);
        Assert.Equal(1, _tokens.Refreshes);
    }

    [Fact]
    public async Task RateLimits_AndServerErrors_AreRetried()
    {
        _google.FailNext(1, HttpStatusCode.Forbidden, "userRateLimitExceeded");
        _google.FailNext(1, HttpStatusCode.TooManyRequests, "rateLimitExceeded", TimeSpan.FromSeconds(5));
        _google.FailNext(1, HttpStatusCode.ServiceUnavailable);

        await _drive.ListAsync(string.Empty);

        Assert.Equal(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(4) }, _waits);
    }

    [Fact]
    public async Task ServerErrors_ThatDoNotStop_AreTransient()
    {
        _google.FailNext(GoogleDriveDriveProvider.MaxRetries + 1, HttpStatusCode.InternalServerError);

        var e = await Assert.ThrowsAsync<DriveException>(() => _drive.ListAsync(string.Empty));

        Assert.Equal(DriveErrorKind.Transient, e.Kind);
        Assert.Equal(GoogleDriveDriveProvider.MaxRetries, _waits.Count);
    }

    [Fact]
    public async Task ALongRetryAfter_IsNotWaitedFor()
    {
        _google.FailNext(1, HttpStatusCode.TooManyRequests, "rateLimitExceeded", TimeSpan.FromMinutes(10));

        var e = await Assert.ThrowsAsync<DriveException>(() => _drive.ListAsync(string.Empty));

        Assert.Equal(DriveErrorKind.Transient, e.Kind);
        Assert.Empty(_waits);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "storageQuotaExceeded", DriveErrorKind.QuotaExceeded)]
    [InlineData(HttpStatusCode.Forbidden, "insufficientPermissions", DriveErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, "appNotAuthorizedToFile", DriveErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound, "notFound", DriveErrorKind.NotFound)]
    [InlineData(HttpStatusCode.BadRequest, "invalid", DriveErrorKind.Other)]
    public async Task Errors_MapToDriveErrorKinds(HttpStatusCode status, string reason, DriveErrorKind kind)
    {
        _google.FailNext(1, status, reason);

        var e = await Assert.ThrowsAsync<DriveException>(() => _drive.GetServerTimeAsync());

        Assert.Equal(kind, e.Kind);
        Assert.Contains(reason, e.Message, StringComparison.Ordinal);
        Assert.Empty(_waits);
    }

    [Fact]
    public async Task NetworkFailures_AreRetried_ThenTransient()
    {
        for (var i = 0; i <= GoogleDriveDriveProvider.MaxRetries; i++) _google.Fail(_ => throw new HttpRequestException("offline"));

        var e = await Assert.ThrowsAsync<DriveException>(() => _drive.ListAsync(string.Empty));

        Assert.Equal(DriveErrorKind.Transient, e.Kind);
    }

    [Fact]
    public async Task InvalidPaths_AreRefused()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _drive.UploadNewAsync("devices/../vault.json", Bytes("x")));
        await Assert.ThrowsAsync<ArgumentException>(() => _drive.ListAsync("devices//x"));
    }

    // ---- End to end -------------------------------------------------------------------------------

    [Fact]
    public async Task TwoDevices_CreateJoinAndConverge_ThroughTheGoogleDriveProvider()
    {
        var google = new FakeGoogleDrive(DateTimeOffset.FromUnixTimeMilliseconds(SyncTestEnvironment.T0)) { PageSize = 3, ReportAliasAsParent = true };
        using var a = new SyncTestEnvironment();
        using var b = new SyncTestEnvironment();
        await a.InitializeAsync();
        await b.InitializeAsync();
        a.InsertLegacyData();
        using var aDrive = new GoogleDriveDriveProvider(new FakeGoogleTokens(), google, (_, _) => Task.CompletedTask);
        using var bDrive = new GoogleDriveDriveProvider(new FakeGoogleTokens(), google, (_, _) => Task.CompletedTask);
        using var aSync = new SyncCoordinator(a.Sync, aDrive, new InMemoryVaultKeyStore(), a.Backups, a.Db, new DeviceInfo("Windows PC", "windows", "0.0.0-test"), time: a.Time);
        using var bSync = new SyncCoordinator(b.Sync, bDrive, new InMemoryVaultKeyStore(), b.Backups, b.Db, new DeviceInfo("Windows Laptop", "windows", "0.0.0-test"), time: b.Time);

        void Advance(long ms)
        {
            a.Time.Advance(ms);
            b.Time.Advance(ms);
            google.Now = google.Now.AddMilliseconds(ms);
        }

        Advance(1000);
        Assert.Equal(VaultProbeOutcome.NoVault, (await aSync.ProbeVaultAsync()).Outcome);
        var created = await aSync.CreateVaultAsync();
        Assert.Equal(CreateVaultOutcome.Created, created.Outcome);
        Assert.Equal(new[] { SyncPaths.ChangeFilePath(a.Sync.Replica!.DeviceId, 1), SyncPaths.VaultFileName }, google.Paths);

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
        Assert.Empty(google.ForbiddenFields);
        // One folder per level: the second device reused the first one's folders.
        Assert.Single(google.FolderPaths, p => p == "PayDay/devices");
    }
}
