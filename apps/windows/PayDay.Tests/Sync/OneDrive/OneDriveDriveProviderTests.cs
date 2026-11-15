using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PayDay.Sync.Drive;
using PayDay.Sync.Drive.OneDrive;

namespace PayDay.Tests.Sync.OneDrive;

/// <summary>
/// <see cref="OneDriveDriveProvider"/> against <see cref="FakeGraph"/>: addressing in the app folder,
/// paging, provider creation times and server time, never-overwrite uploads (simple and session),
/// redirected downloads, deletes, and the error and retry mapping. No network; all data fictional.
/// </summary>
public sealed class OneDriveDriveProviderTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2025, 5, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeGraph _graph = new(Start);
    private readonly FakeTokens _tokens = new();
    private readonly List<TimeSpan> _waits = new();
    private readonly OneDriveDriveProvider _drive;

    public OneDriveDriveProviderTests()
    {
        _drive = new OneDriveDriveProvider(_tokens, _graph, (t, _) =>
        {
            _waits.Add(t);
            return Task.CompletedTask;
        });
    }

    public void Dispose() => _drive.Dispose();

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    private FakeGraph.Recorded Last => _graph.Requests[^1];

    // ---- Shape ------------------------------------------------------------------------------------

    [Fact]
    public void ItIsOneDrive_WithUniqueNames_AndARecycleBin()
    {
        Assert.Equal("OneDrive", _drive.Name);
        Assert.True(_drive.EnforcesUniqueNames);
        Assert.False(_drive.DeletesPermanently);
        Assert.Equal("me/drive/special/approot:/PayDay:", _drive.ItemAddress(string.Empty));
        Assert.Equal("me/drive/special/approot:/PayDay/devices/0a0a0a0a0a0a0a0a/000001.chg:", _drive.ItemAddress("devices/0a0a0a0a0a0a0a0a/000001.chg"));
        Assert.Throws<ArgumentException>(() => _drive.ItemAddress("devices/../vault.json"));
        Assert.Throws<ArgumentException>(() => _drive.ItemAddress("devices//x"));
    }

    // ---- Listing ----------------------------------------------------------------------------------

    [Fact]
    public async Task AFolderThatDoesNotExist_ListsAsEmpty_WithTheServersTime()
    {
        var listing = await _drive.ListAsync("devices");

        Assert.Empty(listing.Files);
        Assert.Empty(listing.Folders);
        Assert.Equal(Start, listing.ServerTime);
        Assert.Equal("https://graph.microsoft.com/v1.0/me/drive/special/approot:/PayDay/devices:/children?$select=id,name,size,createdDateTime,file,folder&$top=200", Last.Uri.OriginalString);
        Assert.Equal("Bearer token-1", Last.Authorization);
    }

    [Fact]
    public async Task Listing_FollowsEveryNextLink_AndSeparatesFilesFromFolders()
    {
        _graph.PageSize = 2;
        for (var i = 1; i <= 5; i++) _graph.Put($"devices/0a0a0a0a0a0a0a0a/{i:000000}.chg", Bytes($"file {i}"), Start.AddMinutes(-i));
        _graph.Put("devices/0b0b0b0b0b0b0b0b/000001.chg", Bytes("other"));
        _graph.Put("vault.json", Bytes("{}"));

        var files = await _drive.ListAsync("devices/0a0a0a0a0a0a0a0a");
        var devices = await _drive.ListAsync("devices");
        var root = await _drive.ListAsync(string.Empty);

        Assert.Equal(Enumerable.Range(1, 5).Select(i => $"devices/0a0a0a0a0a0a0a0a/{i:000000}.chg"), files.Files.Select(f => f.Path));
        Assert.Equal(3, _graph.Requests.Count(r => r.Uri.AbsolutePath.EndsWith("/0a0a0a0a0a0a0a0a:/children", StringComparison.Ordinal)));
        Assert.All(_graph.Requests, r => Assert.Equal("Bearer token-1", r.Authorization));
        // createdDateTime, not the client-set fileSystemInfo.
        Assert.Equal(Start.AddMinutes(-3), files.Files[2].CreatedTime);
        Assert.Equal(6, files.Files[0].Size);
        Assert.StartsWith("ITEM", files.Files[0].Id, StringComparison.Ordinal);
        Assert.Equal(new[] { "0a0a0a0a0a0a0a0a", "0b0b0b0b0b0b0b0b" }, devices.Folders);
        Assert.Empty(devices.Files);
        Assert.Equal(new[] { "vault.json" }, root.Files.Select(f => f.Path));
        Assert.Equal(new[] { "devices" }, root.Folders);
    }

    // ---- Uploads ----------------------------------------------------------------------------------

    [Fact]
    public async Task ASmallUpload_IsOnePutThatNeverOverwrites()
    {
        _graph.Now = Start.AddMinutes(5);
        var item = await _drive.UploadNewAsync("devices/0a0a0a0a0a0a0a0a/000001.chg", Bytes("sealed batch"));

        Assert.Equal(HttpMethod.Put, Last.Method);
        Assert.Equal("https://graph.microsoft.com/v1.0/me/drive/special/approot:/PayDay/devices/0a0a0a0a0a0a0a0a/000001.chg:/content?@microsoft.graph.conflictBehavior=fail", Last.Uri.OriginalString);
        Assert.Equal("application/octet-stream", Last.ContentType);
        Assert.Equal("sealed batch", Encoding.UTF8.GetString(Last.Body));
        Assert.Equal(("devices/0a0a0a0a0a0a0a0a/000001.chg", 12L, Start.AddMinutes(5)), (item.Path, item.Size, item.CreatedTime!.Value));
        Assert.Equal("sealed batch", Encoding.UTF8.GetString(_graph.Read("devices/0a0a0a0a0a0a0a0a/000001.chg")));

        var taken = await Assert.ThrowsAsync<DriveException>(() => _drive.UploadNewAsync("devices/0a0a0a0a0a0a0a0a/000001.chg", Bytes("different")));
        Assert.Equal(DriveErrorKind.AlreadyExists, taken.Kind);
        Assert.Equal("sealed batch", Encoding.UTF8.GetString(_graph.Read("devices/0a0a0a0a0a0a0a0a/000001.chg")));
    }

    [Fact]
    public async Task ALargeUpload_UsesAnUploadSession_InChunks_WithoutTheToken()
    {
        var data = new byte[OneDriveDriveProvider.SimpleUploadLimit + 1000];
        new Random(7).NextBytes(data);

        var item = await _drive.UploadNewAsync("snapshots/big.snap", data);

        var session = _graph.Requests.Single(r => r.Uri.AbsolutePath.EndsWith(":/createUploadSession", StringComparison.Ordinal));
        Assert.Equal("Bearer token-1", session.Authorization);
        Assert.Equal("fail", JsonNode.Parse(session.Body)!["item"]!["@microsoft.graph.conflictBehavior"]!.GetValue<string>());
        var chunks = _graph.Requests.Where(r => r.Uri.Host == "upload.fake").ToList();
        Assert.Equal(2, chunks.Count);
        Assert.All(chunks, c => Assert.Null(c.Authorization));
        Assert.Equal($"bytes 0-{OneDriveDriveProvider.UploadChunkSize - 1}/{data.Length}", chunks[0].ContentRange);
        Assert.Equal(data, _graph.Read("snapshots/big.snap"));
        Assert.Equal(data.Length, item.Size);
        Assert.DoesNotContain(_graph.Requests, r => r.Method == HttpMethod.Put && r.Uri.Host == "graph.microsoft.com");
    }

    [Fact]
    public async Task ALargeUploadToATakenName_IsAlreadyExists()
    {
        _graph.Put("snapshots/big.snap", Bytes("first"));
        var ex = await Assert.ThrowsAsync<DriveException>(() => _drive.UploadNewAsync("snapshots/big.snap", new byte[OneDriveDriveProvider.SimpleUploadLimit + 1]));
        Assert.Equal(DriveErrorKind.AlreadyExists, ex.Kind);
    }

    // ---- Download, delete, server time ------------------------------------------------------------

    [Fact]
    public async Task Download_FollowsTheRedirect_WithoutSendingTheToken()
    {
        _graph.Put("vault.json", Bytes("{\"type\":\"futurecheck-vault\"}"));
        var item = (await _drive.ListAsync(string.Empty)).Files.Single();

        var bytes = await _drive.DownloadAsync(item);

        Assert.Equal("{\"type\":\"futurecheck-vault\"}", Encoding.UTF8.GetString(bytes));
        Assert.Equal("download.fake", Last.Uri.Host);
        Assert.Null(Last.Authorization);
        var gone = new DriveItem("ITEM999", "vault.json", 0, null);
        Assert.Equal(DriveErrorKind.NotFound, (await Assert.ThrowsAsync<DriveException>(() => _drive.DownloadAsync(gone))).Kind);
    }

    [Fact]
    public async Task Delete_IsAnOrdinaryDelete_AndAFileAlreadyGoneIsFine()
    {
        _graph.Put("pairing/0123456789abcdef0123456789abcdef.pair", Bytes("x"));
        var item = (await _drive.ListAsync("pairing")).Files.Single();

        await _drive.DeleteAsync(item);
        await _drive.DeleteAsync(item);

        Assert.Empty(_graph.Paths);
        Assert.Equal(new[] { "pairing/0123456789abcdef0123456789abcdef.pair" }, _graph.RecycleBin);
        Assert.Equal(HttpMethod.Delete, Last.Method);
        Assert.Equal($"/v1.0/me/drive/items/{item.Id}", Last.Uri.AbsolutePath);
    }

    [Fact]
    public async Task ServerTime_IsTheDateHeader()
    {
        _graph.Now = Start.AddHours(3);
        Assert.Equal(Start.AddHours(3), await _drive.GetServerTimeAsync());
    }

    // ---- Errors and retries -----------------------------------------------------------------------

    [Fact]
    public async Task A401_RefreshesTheTokenOnce_ThenIsUnauthorized()
    {
        _graph.FailNext(HttpStatusCode.Unauthorized);
        var ok = await _drive.ListAsync("devices");
        Assert.Empty(ok.Files);
        Assert.Equal(new[] { false, true }, _tokens.Calls);
        Assert.Equal("Bearer token-2", Last.Authorization);

        _graph.ValidTokens.Clear();
        _tokens.Calls.Clear();
        var ex = await Assert.ThrowsAsync<DriveException>(() => _drive.ListAsync("devices"));
        Assert.Equal(DriveErrorKind.Unauthorized, ex.Kind);
        Assert.Equal(new[] { false, true }, _tokens.Calls);
    }

    [Fact]
    public async Task ATokenSourceThatNeedsASignIn_IsUnauthorized_WithoutCallingGraph()
    {
        _tokens.FailWith = DriveErrorKind.Unauthorized;
        var ex = await Assert.ThrowsAsync<DriveException>(() => _drive.ListAsync(string.Empty));
        Assert.Equal(DriveErrorKind.Unauthorized, ex.Kind);
        Assert.Empty(_graph.Requests);
    }

    [Fact]
    public async Task Throttling_IsRetried_AfterRetryAfter()
    {
        _graph.FailNext(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(7));
        _graph.FailNext(HttpStatusCode.ServiceUnavailable, TimeSpan.FromSeconds(3));

        var listing = await _drive.ListAsync("devices");

        Assert.Empty(listing.Files);
        Assert.Equal(new[] { TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(3) }, _waits);
        Assert.Equal(3, _graph.Requests.Count);
    }

    [Fact]
    public async Task ServerErrors_AreRetriedWithBackoff_ThenTransient()
    {
        for (var i = 0; i < 4; i++) _graph.FailNext(HttpStatusCode.InternalServerError);

        var ex = await Assert.ThrowsAsync<DriveException>(() => _drive.UploadNewAsync("vault.json", Bytes("{}")));

        Assert.Equal(DriveErrorKind.Transient, ex.Kind);
        Assert.Equal(new[] { 1.0, 2.0, 4.0 }, _waits.Select(w => w.TotalSeconds));
        Assert.Equal(1 + OneDriveDriveProvider.MaxRetries, _graph.Requests.Count);
        Assert.Empty(_graph.Paths);
    }

    [Fact]
    public async Task ALongRetryAfter_IsNotWaitedFor_TheCallFailsAsTransient()
    {
        _graph.FailNext(HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(10));

        var ex = await Assert.ThrowsAsync<DriveException>(() => _drive.ListAsync("devices"));

        Assert.Equal(DriveErrorKind.Transient, ex.Kind);
        Assert.Empty(_waits);
        Assert.Single(_graph.Requests);
    }

    [Fact]
    public async Task AFullDrive_IsQuotaExceeded_AndNotRetried()
    {
        _graph.FailNext(HttpStatusCode.InsufficientStorage, errorCode: "quotaLimitReached");

        var ex = await Assert.ThrowsAsync<DriveException>(() => _drive.UploadNewAsync("vault.json", Bytes("{}")));

        Assert.Equal(DriveErrorKind.QuotaExceeded, ex.Kind);
        Assert.Contains("quotaLimitReached", ex.Message, StringComparison.Ordinal);
        Assert.Single(_graph.Requests);
        Assert.Empty(_waits);
    }

    [Fact]
    public async Task ADroppedConnection_IsRetried_AndThenTransient()
    {
        _graph.DropNext();
        Assert.Empty((await _drive.ListAsync("devices")).Files);
        Assert.Single(_waits);

        _graph.DropNext(1 + OneDriveDriveProvider.MaxRetries);
        var ex = await Assert.ThrowsAsync<DriveException>(() => _drive.ListAsync("devices"));
        Assert.Equal(DriveErrorKind.Transient, ex.Kind);
    }

    [Fact]
    public async Task OtherClientErrors_AreOther_WithGraphsErrorCode()
    {
        _graph.FailNext(HttpStatusCode.Forbidden, errorCode: "accessDenied");
        var ex = await Assert.ThrowsAsync<DriveException>(() => _drive.ListAsync("devices"));
        Assert.Equal(DriveErrorKind.Other, ex.Kind);
        Assert.Contains("accessDenied", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_waits);
    }
}
