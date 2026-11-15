using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PayDay.Sync.Drive.OneDrive;

/// <summary>Access tokens for Microsoft Graph, obtained silently (never interactively: sync runs in the background).</summary>
public interface IOneDriveTokenSource
{
    /// <summary>
    /// A current access token. <paramref name="forceRefresh"/> skips the cached one (Graph just said
    /// 401). Throws <see cref="DriveException"/>: <see cref="DriveErrorKind.Unauthorized"/> when the
    /// user must sign in again, <see cref="DriveErrorKind.Transient"/> when the sign-in service could
    /// not be reached.
    /// </summary>
    Task<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken ct = default);
}

/// <summary>
/// <see cref="IDriveProvider"/> over Microsoft Graph: the vault lives in the app's own OneDrive folder
/// (<c>/me/drive/special/approot</c>, scope <c>Files.ReadWrite.AppFolder</c>), in the folder
/// <c>PayDay</c> (<c>sync-protocol.md</c> 6.1).
/// <list type="bullet">
///   <item>Listing follows <c>@odata.nextLink</c> pages; a folder that does not exist lists as empty.
///     Creation times are the provider's <c>createdDateTime</c>, never <c>fileSystemInfo</c>; the
///     provider's clock is the HTTP <c>Date</c> header (11.1).</item>
///   <item>Uploads never overwrite (<c>@microsoft.graph.conflictBehavior=fail</c>; a taken name is
///     <see cref="DriveErrorKind.AlreadyExists"/>), never send <c>fileSystemInfo</c>, create missing
///     parent folders, and use an upload session above <see cref="SimpleUploadLimit"/>.</item>
///   <item>Deletes are ordinary deletes (OneDrive keeps the file in its recycle bin).</item>
///   <item>Errors: 401 → <see cref="DriveErrorKind.Unauthorized"/> (after one forced token refresh),
///     404 → NotFound, 409 → AlreadyExists, 507 → QuotaExceeded, 429 and other 5xx → retried honouring
///     <c>Retry-After</c>, then Transient; network failures and timeouts are Transient.</item>
/// </list>
/// Tests drive it with a fake <see cref="HttpMessageHandler"/>; it never touches the network itself.
/// </summary>
public sealed class OneDriveDriveProvider : IDriveProvider, IDisposable
{
    public const string GraphBase = "https://graph.microsoft.com/v1.0/";

    /// <summary>Files up to this size go up in one PUT (Graph's simple upload takes up to 250 MB; 4 MiB keeps requests small).</summary>
    public const int SimpleUploadLimit = 4 * 1024 * 1024;

    /// <summary>Upload-session chunk size: a multiple of 320 KiB, as Graph requires.</summary>
    public const int UploadChunkSize = 10 * 320 * 1024;

    /// <summary>Attempts after the first for throttling (429) and server errors (5xx other than 507).</summary>
    public const int MaxRetries = 3;

    /// <summary>A <c>Retry-After</c> longer than this is not waited for inside one call: the call fails as transient and a later sync retries.</summary>
    public static readonly TimeSpan MaxRetryWait = TimeSpan.FromSeconds(60);

    private const string AppRoot = "me/drive/special/approot";
    private const string ListSelect = "$select=id,name,size,createdDateTime,file,folder&$top=200";

    private readonly HttpClient _http;
    private readonly IOneDriveTokenSource _tokens;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly string _root;

    /// <param name="tokens">Silent access tokens.</param>
    /// <param name="handler">The HTTP handler (tests pass a fake); the default is a plain <see cref="HttpClientHandler"/>.</param>
    /// <param name="delay">How retries wait (tests pass one that records and returns at once).</param>
    /// <param name="vaultFolder">The vault folder inside the app folder.</param>
    public OneDriveDriveProvider(
        IOneDriveTokenSource tokens,
        HttpMessageHandler? handler = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        string vaultFolder = OneDriveConfig.VaultFolder)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _http = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: handler is null)
        {
            BaseAddress = new Uri(GraphBase),
            Timeout = TimeSpan.FromSeconds(100),
        };
        _delay = delay ?? ((t, ct) => Task.Delay(t, ct));
        _root = string.IsNullOrEmpty(vaultFolder) ? throw new ArgumentException("A vault folder is required.", nameof(vaultFolder)) : vaultFolder;
    }

    public string Name => "OneDrive";

    public bool EnforcesUniqueNames => true;

    /// <summary>False: OneDrive's ordinary delete keeps the file in the recycle bin (9.1).</summary>
    public bool DeletesPermanently => false;

    public void Dispose() => _http.Dispose();

    // ---- IDriveProvider ---------------------------------------------------------------------------

    public async Task<DriveFolderListing> ListAsync(string folder, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var files = new List<DriveItem>();
        var folders = new List<string>();
        DateTimeOffset? serverTime = null;
        string? next = $"{ItemAddress(folder)}/children?{ListSelect}";
        while (next is not null)
        {
            var url = next;
            using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), authorize: true, ct).ConfigureAwait(false);
            serverTime = Later(serverTime, resp.Headers.Date);
            if (resp.StatusCode == HttpStatusCode.NotFound && files.Count == 0 && folders.Count == 0)
            {
                return new DriveFolderListing(files, folders, serverTime); // the folder does not exist yet
            }
            await EnsureSuccessAsync(resp, $"listing {Describe(folder)}", ct).ConfigureAwait(false);
            using var doc = await ReadJsonAsync(resp, ct).ConfigureAwait(false);
            var root = doc.RootElement;
            if (root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    if (item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } name)
                    {
                        if (item.TryGetProperty("folder", out _)) folders.Add(name);
                        else if (item.TryGetProperty("file", out _)) files.Add(ToItem(item, Join(folder, name)));
                    }
                }
            }
            next = root.TryGetProperty("@odata.nextLink", out var link) && link.ValueKind == JsonValueKind.String ? link.GetString() : null;
        }
        return new DriveFolderListing(files, folders, serverTime);
    }

    public async Task<byte[]> DownloadAsync(DriveItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var url = $"me/drive/items/{Uri.EscapeDataString(item.Id)}/content";
        var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), authorize: true, ct).ConfigureAwait(false);
        try
        {
            // Graph answers with a redirect to a pre-authenticated URL; it must not get our token.
            if (IsRedirect(resp.StatusCode) && resp.Headers.Location is { } location)
            {
                var target = location.IsAbsoluteUri ? location : new Uri(new Uri(GraphBase), location);
                resp.Dispose();
                resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, target), authorize: false, ct).ConfigureAwait(false);
            }
            await EnsureSuccessAsync(resp, $"downloading {item.Path}", ct).ConfigureAwait(false);
            return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            resp.Dispose();
        }
    }

    public async Task<DriveItem> UploadNewAsync(string path, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var bytes = data.ToArray();
        if (bytes.Length <= SimpleUploadLimit)
        {
            var url = $"{ItemAddress(path)}/content?@microsoft.graph.conflictBehavior=fail";
            using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Put, url) { Content = Bytes(bytes, 0, bytes.Length) }, authorize: true, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(resp, $"uploading {path}", ct).ConfigureAwait(false);
            return await ReadItemAsync(resp, path, ct).ConfigureAwait(false);
        }
        return await UploadInSessionAsync(path, bytes, ct).ConfigureAwait(false);
    }

    private async Task<DriveItem> UploadInSessionAsync(string path, byte[] bytes, CancellationToken ct)
    {
        var create = $"{ItemAddress(path)}/createUploadSession";
        const string body = """{"item":{"@microsoft.graph.conflictBehavior":"fail"}}""";
        string uploadUrl;
        using (var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, create) { Content = new StringContent(body, Encoding.UTF8, "application/json") }, authorize: true, ct).ConfigureAwait(false))
        {
            await EnsureSuccessAsync(resp, $"starting the upload of {path}", ct).ConfigureAwait(false);
            using var doc = await ReadJsonAsync(resp, ct).ConfigureAwait(false);
            uploadUrl = doc.RootElement.TryGetProperty("uploadUrl", out var u) && u.ValueKind == JsonValueKind.String
                ? u.GetString()!
                : throw new DriveException(DriveErrorKind.Other, $"OneDrive returned no upload URL for {path}.");
        }
        var target = new Uri(uploadUrl);
        var done = false;
        try
        {
            for (var start = 0; start < bytes.Length; start += UploadChunkSize)
            {
                var length = Math.Min(UploadChunkSize, bytes.Length - start);
                var from = start;
                using var resp = await SendAsync(() =>
                {
                    var req = new HttpRequestMessage(HttpMethod.Put, target) { Content = Bytes(bytes, from, length) };
                    req.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, from + length - 1, bytes.Length);
                    return req;
                }, authorize: false, ct).ConfigureAwait(false);
                await EnsureSuccessAsync(resp, $"uploading {path}", ct).ConfigureAwait(false);
                if (resp.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
                {
                    done = true;
                    return await ReadItemAsync(resp, path, ct).ConfigureAwait(false);
                }
            }
            throw new DriveException(DriveErrorKind.Other, $"OneDrive did not finish the upload of {path}.");
        }
        finally
        {
            if (!done)
            {
                try
                {
                    // Cancel the session so no partial file is left behind.
                    using var cancel = new HttpRequestMessage(HttpMethod.Delete, target);
                    using var _ = await _http.SendAsync(cancel, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Sessions expire on their own.
                }
            }
        }
    }

    public async Task DeleteAsync(DriveItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var url = $"me/drive/items/{Uri.EscapeDataString(item.Id)}";
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, url), authorize: true, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) return; // already gone
        await EnsureSuccessAsync(resp, $"deleting {item.Path}", ct).ConfigureAwait(false);
    }

    public async Task<DateTimeOffset> GetServerTimeAsync(CancellationToken ct = default)
    {
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{AppRoot}?$select=id"), authorize: true, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, "reading the server time", ct).ConfigureAwait(false);
        return resp.Headers.Date ?? throw new DriveException(DriveErrorKind.Transient, "OneDrive sent no Date header.");
    }

    // ---- HTTP -------------------------------------------------------------------------------------

    /// <summary>
    /// Sends one request (built afresh for every attempt): one forced token refresh on a 401, and up
    /// to <see cref="MaxRetries"/> retries on 429 and 5xx (not 507), waiting <c>Retry-After</c> or an
    /// exponential backoff. Network failures are retried the same way. Returns the last response.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, bool authorize, CancellationToken ct)
    {
        var refreshed = false;
        for (var attempt = 0; ; attempt++)
        {
            using var req = make();
            if (authorize)
            {
                var token = await _tokens.GetAccessTokenAsync(refreshed, ct).ConfigureAwait(false);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException e)
            {
                if (attempt >= MaxRetries) throw new DriveException(DriveErrorKind.Transient, "OneDrive could not be reached.", e);
                await _delay(Backoff(attempt), ct).ConfigureAwait(false);
                continue;
            }
            catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
            {
                if (attempt >= MaxRetries) throw new DriveException(DriveErrorKind.Transient, "OneDrive did not answer in time.", e);
                await _delay(Backoff(attempt), ct).ConfigureAwait(false);
                continue;
            }

            if (resp.StatusCode == HttpStatusCode.Unauthorized && authorize && !refreshed)
            {
                // The cached token may have just expired or been revoked: refresh once.
                resp.Dispose();
                refreshed = true;
                attempt--;
                continue;
            }
            if (IsRetryable(resp.StatusCode) && attempt < MaxRetries)
            {
                var wait = RetryAfter(resp) ?? Backoff(attempt);
                if (wait <= MaxRetryWait)
                {
                    resp.Dispose();
                    await _delay(wait, ct).ConfigureAwait(false);
                    continue;
                }
            }
            return resp;
        }
    }

    private static bool IsRetryable(HttpStatusCode status)
        => status == HttpStatusCode.TooManyRequests || ((int)status >= 500 && status != HttpStatusCode.InsufficientStorage);

    private static bool IsRedirect(HttpStatusCode status)
        => status is HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.MovedPermanently;

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Pow(2, attempt));

    /// <summary>The <c>Retry-After</c> header (seconds or an HTTP date), when there is one.</summary>
    internal static TimeSpan? RetryAfter(HttpResponseMessage resp)
    {
        if (resp.Headers.RetryAfter is not { } ra) return null;
        if (ra.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (ra.Date is { } date)
        {
            var now = resp.Headers.Date ?? DateTimeOffset.UtcNow;
            var wait = date - now;
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }
        return null;
    }

    /// <summary>Throws the <see cref="DriveException"/> a failed response maps to.</summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, string what, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var detail = await GraphErrorAsync(resp, ct).ConfigureAwait(false);
        var status = (int)resp.StatusCode;
        var kind = resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized => DriveErrorKind.Unauthorized,
            HttpStatusCode.NotFound => DriveErrorKind.NotFound,
            HttpStatusCode.Conflict => DriveErrorKind.AlreadyExists,
            HttpStatusCode.InsufficientStorage => DriveErrorKind.QuotaExceeded,
            HttpStatusCode.TooManyRequests => DriveErrorKind.Transient,
            _ when status >= 500 => DriveErrorKind.Transient,
            _ => DriveErrorKind.Other,
        };
        var text = kind switch
        {
            DriveErrorKind.Unauthorized => "The OneDrive sign-in expired.",
            DriveErrorKind.NotFound => $"Not found on OneDrive while {what}.",
            DriveErrorKind.AlreadyExists => $"A file with that name is already on OneDrive ({what}).",
            DriveErrorKind.QuotaExceeded => "Your OneDrive is full.",
            DriveErrorKind.Transient => $"OneDrive is busy or unavailable ({status}) while {what}.",
            _ => $"OneDrive refused {what} ({status}).",
        };
        throw new DriveException(kind, detail is null ? text : $"{text} {detail}");
    }

    /// <summary>Graph's error code and message (<c>{"error":{"code":…,"message":…}}</c>), when present.</summary>
    private static async Task<string?> GraphErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text)) return null;
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object)
            {
                var code = e.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                var message = e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                return code is null ? message : $"({code}{(message is null ? string.Empty : ": " + message)})";
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        try
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException e)
        {
            throw new DriveException(DriveErrorKind.Transient, "OneDrive sent an unreadable reply.", e);
        }
    }

    private static async Task<DriveItem> ReadItemAsync(HttpResponseMessage resp, string path, CancellationToken ct)
    {
        using var doc = await ReadJsonAsync(resp, ct).ConfigureAwait(false);
        return ToItem(doc.RootElement, path);
    }

    private static DriveItem ToItem(JsonElement item, string path)
    {
        var id = item.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString()! : throw new DriveException(DriveErrorKind.Other, $"OneDrive returned no ID for {path}.");
        var size = item.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var n) ? n : 0;
        DateTimeOffset? created = null;
        // The provider-assigned creation time, never fileSystemInfo (sync-protocol.md 11.1).
        if (item.TryGetProperty("createdDateTime", out var c) && c.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(c.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t))
        {
            created = t;
        }
        return new DriveItem(id, path, size, created);
    }

    private static ByteArrayContent Bytes(byte[] bytes, int offset, int count)
    {
        var content = new ByteArrayContent(bytes, offset, count);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return content;
    }

    /// <summary>The Graph address of a vault path: <c>me/drive/special/approot:/PayDay/devices/…:</c>.</summary>
    internal string ItemAddress(string vaultPath)
    {
        var segments = new List<string> { Uri.EscapeDataString(_root) };
        if (vaultPath.Length > 0)
        {
            foreach (var segment in vaultPath.Split('/'))
            {
                if (segment.Length == 0 || segment is "." or "..") throw new ArgumentException($"Not a vault path: '{vaultPath}'.", nameof(vaultPath));
                segments.Add(Uri.EscapeDataString(segment));
            }
        }
        return $"{AppRoot}:/{string.Join('/', segments)}:";
    }

    private static string Join(string folder, string name) => folder.Length == 0 ? name : $"{folder}/{name}";

    private static string Describe(string folder) => folder.Length == 0 ? "the vault folder" : folder;

    private static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b)
        => a is null ? b : b is null ? a : (a > b ? a : b);
}
