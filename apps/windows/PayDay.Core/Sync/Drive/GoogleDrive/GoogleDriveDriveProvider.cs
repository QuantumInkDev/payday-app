using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PayDay.Sync.Drive.GoogleDrive;

/// <summary>
/// <see cref="IDriveProvider"/> over the Google Drive API v3: the vault lives in the app's hidden
/// app-data folder (scope <c>drive.appdata</c>), in the folder <c>PayDay</c> (<c>sync-protocol.md</c> 6.1).
/// <list type="bullet">
///   <item>Google Drive does not enforce unique names, so two folders or files can share a path (two
///     devices creating <c>devices/</c> at once, or an upload retried after its reply was lost). A
///     listing therefore covers <b>every</b> folder at the path and can show two files with one path,
///     as <see cref="IDriveProvider"/> allows; the sync engine handles both (10.2). Uploads go into the
///     earliest-created folder at the path, so devices converge on one.</item>
///   <item>Listing reads the whole app-data folder in pages (it holds only the vault), so duplicate
///     folders are always seen. Creation times are Drive's <c>createdTime</c>, which this class never
///     sets; the provider's clock is the HTTP <c>Date</c> header (11.1).</item>
///   <item>Uploads are multipart up to <see cref="MultipartLimit"/>, resumable above it, and send only
///     a name and a parent. Deletes are permanent (<c>files.delete</c> skips the trash, 9.1).</item>
///   <item>Errors: 401 → one forced token refresh, then <see cref="DriveErrorKind.Unauthorized"/>;
///     403 rate limits, 429 and 5xx → retried honouring <c>Retry-After</c> within
///     <see cref="MaxRetryTime"/>, then Transient; 403 <c>storageQuotaExceeded</c> → QuotaExceeded;
///     other 403s → Unauthorized; 404 → NotFound; network failures and timeouts → Transient.</item>
/// </list>
/// Tests drive it with a fake <see cref="HttpMessageHandler"/>; it never touches the network itself.
/// </summary>
public sealed class GoogleDriveDriveProvider : IDriveProvider, IDisposable
{
    public const string ApiBase = "https://www.googleapis.com/";

    public const string FolderMimeType = "application/vnd.google-apps.folder";

    /// <summary>Files up to this size go up in one multipart request (Google's limit for it is 5 MB).</summary>
    public const int MultipartLimit = 4 * 1024 * 1024;

    /// <summary>Attempts after the first for throttling and server errors.</summary>
    public const int MaxRetries = 3;

    /// <summary>The most time one operation spends retrying; then it ends as Transient and a later sync retries.</summary>
    public static readonly TimeSpan MaxRetryTime = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    private const string FileFields = "id,name,mimeType,size,createdTime,parents";

    private readonly HttpClient _http;
    private readonly IGoogleTokenSource _tokens;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly string _root;
    private string? _appDataId;

    /// <param name="tokens">Silent access tokens.</param>
    /// <param name="handler">The HTTP handler (tests pass a fake).</param>
    /// <param name="delay">How retries wait (tests pass one that returns at once).</param>
    /// <param name="vaultFolder">The vault folder inside the app-data folder.</param>
    public GoogleDriveDriveProvider(
        IGoogleTokenSource tokens,
        HttpMessageHandler? handler = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        string vaultFolder = GoogleDriveConfig.VaultFolder)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: handler is null)
        {
            BaseAddress = new Uri(ApiBase),
            Timeout = RequestTimeout,
        };
        _delay = delay ?? ((t, ct) => Task.Delay(t, ct));
        _root = string.IsNullOrEmpty(vaultFolder) ? throw new ArgumentException("A vault folder is required.", nameof(vaultFolder)) : vaultFolder;
    }

    public string Name => "Google Drive";

    public bool EnforcesUniqueNames => false;

    public bool DeletesPermanently => true;

    public void Dispose() => _http.Dispose();

    // ---- IDriveProvider ---------------------------------------------------------------------------

    public async Task<DriveFolderListing> ListAsync(string folder, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var segments = Segments(folder);
        var (tree, serverTime) = await ScanAsync(ct).ConfigureAwait(false);
        var parents = tree.Resolve(_root, segments);
        var files = new List<DriveItem>();
        var folders = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var parent in parents)
        {
            foreach (var child in tree.ChildrenOf(parent.Id))
            {
                if (child.IsFolder) folders.Add(child.Name);
                else files.Add(new DriveItem(child.Id, Join(folder, child.Name), child.Size, child.Created));
            }
        }
        return new DriveFolderListing(files, folders.ToList(), serverTime);
    }

    public async Task<byte[]> DownloadAsync(DriveItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var url = $"drive/v3/files/{Uri.EscapeDataString(item.Id)}?alt=media";
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, $"downloading {item.Path}", ct).ConfigureAwait(false);
        return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    public async Task<DriveItem> UploadNewAsync(string path, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var segments = Segments(path);
        var name = segments[^1];
        var parentId = await EnsureFolderAsync(segments[..^1], ct).ConfigureAwait(false);
        var metadata = Metadata(name, parentId, folder: false);
        var bytes = data.ToArray();
        using var resp = bytes.Length <= MultipartLimit
            ? await SendAsync(() => MultipartUpload(metadata, bytes), ct).ConfigureAwait(false)
            : await ResumableUploadAsync(metadata, bytes, path, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, $"uploading {path}", ct).ConfigureAwait(false);
        var node = await ReadNodeAsync(resp, path, ct).ConfigureAwait(false);
        return new DriveItem(node.Id, path, node.Size, node.Created);
    }

    public async Task DeleteAsync(DriveItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        var url = $"drive/v3/files/{Uri.EscapeDataString(item.Id)}";
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, url), ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) return; // already gone
        await EnsureSuccessAsync(resp, $"deleting {item.Path}", ct).ConfigureAwait(false);
    }

    public async Task<DateTimeOffset> GetServerTimeAsync(CancellationToken ct = default)
    {
        var (_, date) = await GetAppDataIdAsync(ct).ConfigureAwait(false);
        return date ?? throw new DriveException(DriveErrorKind.Transient, "Google Drive sent no Date header.");
    }

    // ---- The folder tree --------------------------------------------------------------------------

    internal sealed record Node(string Id, string Name, bool IsFolder, long Size, DateTimeOffset? Created, IReadOnlyList<string> Parents);

    internal sealed class Tree
    {
        private readonly Dictionary<string, List<Node>> _children = new(StringComparer.Ordinal);

        public Tree(string appDataId, IEnumerable<Node> nodes)
        {
            AppDataId = appDataId;
            foreach (var node in nodes)
            {
                foreach (var parent in node.Parents)
                {
                    if (!_children.TryGetValue(parent, out var list)) _children[parent] = list = new List<Node>();
                    list.Add(node);
                }
            }
        }

        public string AppDataId { get; }

        public IReadOnlyList<Node> ChildrenOf(string id) => _children.TryGetValue(id, out var list) ? list : Array.Empty<Node>();

        /// <summary>Every folder at <c>root/segments…</c>, earliest-created first (ties by ID).</summary>
        public IReadOnlyList<Node> Resolve(string root, IReadOnlyList<string> segments)
        {
            var current = FoldersNamed(new[] { AppDataId }, root);
            foreach (var segment in segments)
            {
                if (current.Count == 0) break;
                current = FoldersNamed(current.Select(n => n.Id), segment);
            }
            return current;
        }

        private List<Node> FoldersNamed(IEnumerable<string> parents, string name)
            => parents.SelectMany(ChildrenOf)
                .Where(n => n.IsFolder && string.Equals(n.Name, name, StringComparison.Ordinal))
                .DistinctBy(n => n.Id)
                .OrderBy(n => n.Created ?? DateTimeOffset.MaxValue)
                .ThenBy(n => n.Id, StringComparer.Ordinal)
                .ToList();
    }

    /// <summary>Reads every file and folder in the app-data folder (it holds only the vault), following pages.</summary>
    private async Task<(Tree Tree, DateTimeOffset? ServerTime)> ScanAsync(CancellationToken ct)
    {
        var appDataId = _appDataId;
        DateTimeOffset? serverTime = null;
        if (appDataId is null) (appDataId, serverTime) = await GetAppDataIdAsync(ct).ConfigureAwait(false);
        var nodes = new List<Node>();
        string? pageToken = null;
        do
        {
            var url = "drive/v3/files?spaces=appDataFolder&q=" + Uri.EscapeDataString("trashed = false")
                + $"&fields={Uri.EscapeDataString($"nextPageToken,files({FileFields})")}&pageSize=1000"
                + (pageToken is null ? string.Empty : "&pageToken=" + Uri.EscapeDataString(pageToken));
            using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct).ConfigureAwait(false);
            serverTime = Later(serverTime, resp.Headers.Date);
            await EnsureSuccessAsync(resp, "listing the vault folder", ct).ConfigureAwait(false);
            using var doc = await ReadJsonAsync(resp, ct).ConfigureAwait(false);
            var root = doc.RootElement;
            if (root.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in files.EnumerateArray())
                {
                    if (ToNode(f) is { } node) nodes.Add(node);
                }
            }
            pageToken = root.TryGetProperty("nextPageToken", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
        }
        while (pageToken is { Length: > 0 });
        return (new Tree(appDataId, nodes), serverTime);
    }

    /// <summary>The app-data folder's real ID (children list it as their parent, not the alias), and the server's clock.</summary>
    private async Task<(string Id, DateTimeOffset? Date)> GetAppDataIdAsync(CancellationToken ct)
    {
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "drive/v3/files/appDataFolder?fields=id"), ct).ConfigureAwait(false);
        await EnsureSuccessAsync(resp, "opening the app folder", ct).ConfigureAwait(false);
        if (_appDataId is null)
        {
            using var doc = await ReadJsonAsync(resp, ct).ConfigureAwait(false);
            _appDataId = doc.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() is { Length: > 0 } s
                ? s
                : throw new DriveException(DriveErrorKind.Other, "Google Drive returned no ID for the app folder.");
        }
        return (_appDataId, resp.Headers.Date);
    }

    /// <summary>The ID of the folder at <c>root/segments…</c>, creating the missing levels.</summary>
    private async Task<string> EnsureFolderAsync(IReadOnlyList<string> segments, CancellationToken ct)
    {
        var (tree, _) = await ScanAsync(ct).ConfigureAwait(false);
        var levels = new List<string> { _root };
        levels.AddRange(segments);
        // The deepest level that already exists, taking the earliest folder at each level.
        var parentId = tree.AppDataId;
        var depth = 0;
        for (; depth < levels.Count; depth++)
        {
            var found = tree.Resolve(_root, levels.Skip(1).Take(depth).ToList());
            if (found.Count == 0) break;
            parentId = found[0].Id;
        }
        for (; depth < levels.Count; depth++)
        {
            var metadata = Metadata(levels[depth], parentId, folder: true);
            using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, $"drive/v3/files?fields={Uri.EscapeDataString(FileFields)}")
            {
                Content = new StringContent(metadata, Encoding.UTF8, "application/json"),
            }, ct).ConfigureAwait(false);
            await EnsureSuccessAsync(resp, $"creating the folder {string.Join('/', levels.Take(depth + 1))}", ct).ConfigureAwait(false);
            parentId = (await ReadNodeAsync(resp, levels[depth], ct).ConfigureAwait(false)).Id;
        }
        return parentId;
    }

    // ---- Uploads ----------------------------------------------------------------------------------

    /// <summary>Only a name and a parent: never <c>createdTime</c> or <c>modifiedTime</c> (11.1).</summary>
    private static string Metadata(string name, string parentId, bool folder)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("name", name);
            if (folder) w.WriteString("mimeType", FolderMimeType);
            w.WriteStartArray("parents");
            w.WriteStringValue(parentId);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static HttpRequestMessage MultipartUpload(string metadata, byte[] bytes)
    {
        var boundary = "fc" + Guid.NewGuid().ToString("N");
        using var body = new MemoryStream();
        void Text(string s) => body.Write(Encoding.UTF8.GetBytes(s));
        Text($"--{boundary}\r\nContent-Type: application/json; charset=UTF-8\r\n\r\n{metadata}\r\n");
        Text($"--{boundary}\r\nContent-Type: application/octet-stream\r\n\r\n");
        body.Write(bytes);
        Text($"\r\n--{boundary}--\r\n");
        var content = new ByteArrayContent(body.ToArray());
        var type = new MediaTypeHeaderValue("multipart/related");
        type.Parameters.Add(new NameValueHeaderValue("boundary", boundary));
        content.Headers.ContentType = type;
        return new HttpRequestMessage(HttpMethod.Post, $"upload/drive/v3/files?uploadType=multipart&fields={Uri.EscapeDataString(FileFields)}") { Content = content };
    }

    private async Task<HttpResponseMessage> ResumableUploadAsync(string metadata, byte[] bytes, string path, CancellationToken ct)
    {
        Uri session;
        using (var start = await SendAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"upload/drive/v3/files?uploadType=resumable&fields={Uri.EscapeDataString(FileFields)}")
            {
                Content = new StringContent(metadata, Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("X-Upload-Content-Type", "application/octet-stream");
            req.Headers.Add("X-Upload-Content-Length", bytes.Length.ToString(CultureInfo.InvariantCulture));
            return req;
        }, ct).ConfigureAwait(false))
        {
            await EnsureSuccessAsync(start, $"starting the upload of {path}", ct).ConfigureAwait(false);
            session = start.Headers.Location is { } location
                ? (location.IsAbsoluteUri ? location : new Uri(new Uri(ApiBase), location))
                : throw new DriveException(DriveErrorKind.Other, $"Google Drive returned no upload address for {path}.");
        }
        if (!IsGoogleApiUrl(session))
        {
            // The bearer token goes only to Google's API host.
            throw new DriveException(DriveErrorKind.Other, $"Google Drive sent an upload address on another host for {path}.");
        }
        return await SendAsync(() =>
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            return new HttpRequestMessage(HttpMethod.Put, session) { Content = content };
        }, ct).ConfigureAwait(false);
    }

    internal static bool IsGoogleApiUrl(Uri url)
        => url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttps && string.Equals(url.Host, "www.googleapis.com", StringComparison.OrdinalIgnoreCase);

    // ---- HTTP -------------------------------------------------------------------------------------

    /// <summary>
    /// Sends one request (built afresh for every attempt) with a bearer token: one forced token refresh
    /// on a 401, and up to <see cref="MaxRetries"/> retries on 429, 5xx and 403 rate limits, waiting
    /// <c>Retry-After</c> or an exponential backoff. Network failures are retried the same way.
    /// Returns the last response.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, CancellationToken ct)
    {
        var refreshed = false;
        var started = System.Diagnostics.Stopwatch.StartNew();
        var waited = TimeSpan.Zero;
        bool CanWait(TimeSpan wait) => started.Elapsed + waited + wait <= MaxRetryTime;
        for (var attempt = 0; ; attempt++)
        {
            using var req = make();
            var token = await _tokens.GetAccessTokenAsync(refreshed, ct).ConfigureAwait(false);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException e)
            {
                if (attempt >= MaxRetries || !CanWait(Backoff(attempt))) throw new DriveException(DriveErrorKind.Transient, "Google Drive could not be reached.", e);
                waited += Backoff(attempt);
                await _delay(Backoff(attempt), ct).ConfigureAwait(false);
                continue;
            }
            catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
            {
                if (attempt >= MaxRetries || !CanWait(Backoff(attempt))) throw new DriveException(DriveErrorKind.Transient, "Google Drive did not answer in time.", e);
                waited += Backoff(attempt);
                await _delay(Backoff(attempt), ct).ConfigureAwait(false);
                continue;
            }

            if (resp.StatusCode == HttpStatusCode.Unauthorized && !refreshed)
            {
                resp.Dispose();
                refreshed = true;
                attempt--;
                continue;
            }
            if (attempt < MaxRetries && await IsRetryableAsync(resp, ct).ConfigureAwait(false))
            {
                var wait = RetryAfter(resp) ?? Backoff(attempt);
                if (CanWait(wait))
                {
                    resp.Dispose();
                    waited += wait;
                    await _delay(wait, ct).ConfigureAwait(false);
                    continue;
                }
            }
            return resp;
        }
    }

    private static async Task<bool> IsRetryableAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var status = (int)resp.StatusCode;
        if (status == 429 || status >= 500) return true;
        if (status != 403) return false;
        var (reason, _) = await ErrorAsync(resp, ct).ConfigureAwait(false);
        return IsRateLimit(reason);
    }

    private static bool IsRateLimit(string? reason)
        => reason is "rateLimitExceeded" or "userRateLimitExceeded" or "sharingRateLimitExceeded";

    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Pow(2, attempt));

    internal static TimeSpan? RetryAfter(HttpResponseMessage resp)
    {
        if (resp.Headers.RetryAfter is not { } ra) return null;
        if (ra.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (ra.Date is { } date)
        {
            var wait = date - (resp.Headers.Date ?? DateTimeOffset.UtcNow);
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }
        return null;
    }

    /// <summary>Throws the <see cref="DriveException"/> a failed response maps to.</summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, string what, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;
        var (reason, message) = await ErrorAsync(resp, ct).ConfigureAwait(false);
        var status = (int)resp.StatusCode;
        var kind = status switch
        {
            401 => DriveErrorKind.Unauthorized,
            403 when reason == "storageQuotaExceeded" => DriveErrorKind.QuotaExceeded,
            403 when IsRateLimit(reason) || reason == "dailyLimitExceeded" => DriveErrorKind.Transient,
            403 => DriveErrorKind.Unauthorized,
            404 => DriveErrorKind.NotFound,
            429 => DriveErrorKind.Transient,
            >= 500 => DriveErrorKind.Transient,
            _ => DriveErrorKind.Other,
        };
        var text = kind switch
        {
            DriveErrorKind.Unauthorized when status == 403 => "Google Drive refused access. Sign in again and allow FutureCheck to keep its own data in your Google Drive.",
            DriveErrorKind.Unauthorized => "The Google Drive sign-in expired.",
            DriveErrorKind.NotFound => $"Not found on Google Drive while {what}.",
            DriveErrorKind.QuotaExceeded => "Your Google storage is full.",
            DriveErrorKind.Transient => $"Google Drive is busy or unavailable ({status}) while {what}.",
            _ => $"Google Drive refused {what} ({status}).",
        };
        var detail = reason is null ? message : $"({reason}{(message is null ? string.Empty : ": " + message)})";
        throw new DriveException(kind, detail is null ? text : $"{text} {detail}");
    }

    /// <summary>Drive's error reason and message (<c>{"error":{"errors":[{"reason":…}],"message":…}}</c>), when present.</summary>
    private static async Task<(string? Reason, string? Message)> ErrorAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text)) return (null, null);
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("error", out var e) || e.ValueKind != JsonValueKind.Object) return (null, null);
            string? reason = null;
            if (e.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in errors.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String)
                    {
                        reason = r.GetString();
                        break;
                    }
                }
            }
            var message = e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            return (reason, message);
        }
        catch (JsonException)
        {
            return (null, null);
        }
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
            throw new DriveException(DriveErrorKind.Transient, "Google Drive sent an unreadable reply.", e);
        }
    }

    private static async Task<Node> ReadNodeAsync(HttpResponseMessage resp, string what, CancellationToken ct)
    {
        using var doc = await ReadJsonAsync(resp, ct).ConfigureAwait(false);
        return ToNode(doc.RootElement) ?? throw new DriveException(DriveErrorKind.Other, $"Google Drive returned no ID for {what}.");
    }

    private static Node? ToNode(JsonElement f)
    {
        if (f.ValueKind != JsonValueKind.Object) return null;
        if (!f.TryGetProperty("id", out var i) || i.ValueKind != JsonValueKind.String || i.GetString() is not { Length: > 0 } id) return null;
        var name = f.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? string.Empty : string.Empty;
        var isFolder = f.TryGetProperty("mimeType", out var m) && m.ValueKind == JsonValueKind.String && m.GetString() == FolderMimeType;
        long size = 0;
        if (f.TryGetProperty("size", out var s))
        {
            // Drive v3 sends int64 values as strings.
            if (s.ValueKind == JsonValueKind.String) long.TryParse(s.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out size);
            else if (s.ValueKind == JsonValueKind.Number) s.TryGetInt64(out size);
        }
        DateTimeOffset? created = null;
        if (f.TryGetProperty("createdTime", out var c) && c.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(c.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var t))
        {
            created = t;
        }
        var parents = new List<string>();
        if (f.TryGetProperty("parents", out var p) && p.ValueKind == JsonValueKind.Array)
        {
            foreach (var parent in p.EnumerateArray())
            {
                if (parent.ValueKind == JsonValueKind.String && parent.GetString() is { Length: > 0 } pid) parents.Add(pid);
            }
        }
        return new Node(id, name, isFolder, size, created, parents);
    }

    private static string[] Segments(string vaultPath)
    {
        if (vaultPath.Length == 0) return Array.Empty<string>();
        var segments = vaultPath.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or "..") throw new ArgumentException($"Not a vault path: '{vaultPath}'.", nameof(vaultPath));
        }
        return segments;
    }

    private static string Join(string folder, string name) => folder.Length == 0 ? name : $"{folder}/{name}";

    private static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b)
        => a is null ? b : b is null ? a : (a > b ? a : b);
}
