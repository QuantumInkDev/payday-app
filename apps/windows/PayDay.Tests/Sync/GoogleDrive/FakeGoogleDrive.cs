using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PayDay.Sync.Drive.GoogleDrive;

namespace PayDay.Tests.Sync.GoogleDrive;

/// <summary>
/// A small in-memory Google Drive API v3 for <see cref="GoogleDriveDriveProvider"/>: the app-data
/// folder, files and folders that may share names, a paged whole-space listing, multipart and
/// resumable uploads, <c>alt=media</c> downloads, permanent deletes, a server clock in every
/// <c>Date</c> header, bearer-token checks and injected failures. No network. All names and data are
/// fictional.
/// </summary>
internal sealed class FakeGoogleDrive : HttpMessageHandler
{
    public const string AppDataId = "appdata-0001";
    private const string Host = "www.googleapis.com";
    private const string FolderMime = GoogleDriveDriveProvider.FolderMimeType;

    private readonly object _lock = new();
    private readonly List<Node> _nodes = new();
    private readonly Dictionary<string, (string Metadata, long Length)> _sessions = new(StringComparer.Ordinal);
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage?>> _faults = new();
    private long _nextId = 1;

    public FakeGoogleDrive(DateTimeOffset start) => Now = start;

    public DateTimeOffset Now { get; set; }

    /// <summary>Entries per listing page.</summary>
    public int PageSize { get; set; } = 1000;

    /// <summary>Where a resumable upload's session points (a hostile server could point it elsewhere).</summary>
    public string SessionHost { get; set; } = Host;

    public HashSet<string> ValidTokens { get; } = new(StringComparer.Ordinal) { FakeGoogleTokens.First, FakeGoogleTokens.Refreshed };

    public List<Recorded> Requests { get; } = new();

    /// <summary>Metadata fields uploads sent that writers must never set (createdTime, modifiedTime).</summary>
    public List<string> ForbiddenFields { get; } = new();

    public List<string> Deleted { get; } = new();

    public sealed record Recorded(HttpMethod Method, Uri Uri, string? Authorization);

    private sealed class Node
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public required bool IsFolder { get; init; }
        public required string Parent { get; init; }
        public required DateTimeOffset Created { get; init; }
        public byte[] Data { get; init; } = Array.Empty<byte>();
    }

    /// <summary>Every file's vault path (with duplicates), sorted.</summary>
    public IReadOnlyList<string> Paths
    {
        get
        {
            lock (_lock) return _nodes.Where(n => !n.IsFolder).Select(PathOf).Where(p => p is not null).Select(p => p!).Order(StringComparer.Ordinal).ToArray();
        }
    }

    /// <summary>Every folder's path under the app-data folder, with duplicates, sorted.</summary>
    public IReadOnlyList<string> FolderPaths
    {
        get
        {
            lock (_lock) return _nodes.Where(n => n.IsFolder).Select(FullPathOf).Order(StringComparer.Ordinal).ToArray();
        }
    }

    /// <summary>The names of one folder's children, in creation order.</summary>
    public IReadOnlyList<string> ChildNames(string folderId)
    {
        lock (_lock) return _nodes.Where(n => n.Parent == folderId).Select(n => n.Name).ToArray();
    }

    public byte[] Read(string path)
    {
        lock (_lock) return _nodes.Single(n => !n.IsFolder && PathOf(n) == path).Data;
    }

    /// <summary>Adds a folder directly (for duplicate-folder cases); <paramref name="parentId"/> defaults to the app-data folder.</summary>
    public string AddFolder(string name, string? parentId = null, DateTimeOffset? created = null)
    {
        lock (_lock) return Add(name, true, parentId ?? AppDataId, Array.Empty<byte>(), created).Id;
    }

    public string AddFile(string name, string parentId, byte[] data, DateTimeOffset? created = null)
    {
        lock (_lock) return Add(name, false, parentId, data, created).Id;
    }

    public void Fail(Func<HttpRequestMessage, HttpResponseMessage?> fault)
    {
        lock (_lock) _faults.Enqueue(fault);
    }

    /// <summary>The next <paramref name="count"/> requests answer <paramref name="status"/> with a Drive error body.</summary>
    public void FailNext(int count, HttpStatusCode status, string? reason = null, TimeSpan? retryAfter = null)
    {
        for (var i = 0; i < count; i++)
        {
            Fail(_ =>
            {
                var r = Error(status, reason ?? "backendError");
                if (retryAfter is { } ra) r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(ra);
                return r;
            });
        }
    }

    private Node Add(string name, bool folder, string parent, byte[] data, DateTimeOffset? created)
    {
        var node = new Node { Id = $"id{_nextId++:D4}", Name = name, IsFolder = folder, Parent = parent, Created = created ?? Now, Data = data };
        _nodes.Add(node);
        return node;
    }

    private string FullPathOf(Node n)
    {
        var parts = new List<string> { n.Name };
        var parent = n.Parent;
        while (parent != AppDataId)
        {
            var p = _nodes.FirstOrDefault(x => x.Id == parent);
            if (p is null) return "?/" + string.Join('/', Enumerable.Reverse(parts));
            parts.Add(p.Name);
            parent = p.Parent;
        }
        parts.Reverse();
        return string.Join('/', parts);
    }

    /// <summary>The path relative to the vault folder, or null when the node is outside it.</summary>
    private string? PathOf(Node n)
    {
        var full = FullPathOf(n);
        const string prefix = GoogleDriveConfig.VaultFolder + "/";
        return full.StartsWith(prefix, StringComparison.Ordinal) ? full[prefix.Length..] : null;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        lock (_lock)
        {
            Requests.Add(new Recorded(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString()));
            var response = Handle(request, body);
            response.Headers.Date = Now;
            response.RequestMessage = request;
            return response;
        }
    }

    private HttpResponseMessage Handle(HttpRequestMessage req, byte[] body)
    {
        if (_faults.Count > 0 && _faults.Dequeue()(req) is { } fault) return fault;
        var uri = req.RequestUri!;
        if (uri.Host != Host) return new HttpResponseMessage(HttpStatusCode.BadGateway);
        if (req.Headers.Authorization is not { Scheme: "Bearer" } auth || auth.Parameter is null || !ValidTokens.Contains(auth.Parameter))
        {
            return Error(HttpStatusCode.Unauthorized, "authError");
        }
        var path = uri.AbsolutePath;
        var query = Query(uri);

        if (req.Method == HttpMethod.Get && path == "/drive/v3/files/appDataFolder") return Json(new JsonObject { ["id"] = AppDataId });
        if (req.Method == HttpMethod.Get && path == "/drive/v3/files") return List(query);
        if (req.Method == HttpMethod.Get && path.StartsWith("/drive/v3/files/", StringComparison.Ordinal) && query.GetValueOrDefault("alt") == "media")
        {
            var node = _nodes.FirstOrDefault(n => n.Id == path["/drive/v3/files/".Length..] && !n.IsFolder);
            return node is null ? Error(HttpStatusCode.NotFound, "notFound") : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(node.Data) };
        }
        if (req.Method == HttpMethod.Delete && path.StartsWith("/drive/v3/files/", StringComparison.Ordinal))
        {
            var id = path["/drive/v3/files/".Length..];
            var node = _nodes.FirstOrDefault(n => n.Id == id);
            if (node is null) return Error(HttpStatusCode.NotFound, "notFound");
            _nodes.Remove(node);
            Deleted.Add(PathOf(node) ?? node.Name);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        if (req.Method == HttpMethod.Post && path == "/drive/v3/files") return Create(Encoding.UTF8.GetString(body), null);
        if (req.Method == HttpMethod.Post && path == "/upload/drive/v3/files" && query.GetValueOrDefault("uploadType") == "multipart")
        {
            var (metadata, data) = Multipart(req, body);
            return metadata is null ? Error(HttpStatusCode.BadRequest, "badContent") : Create(metadata, data);
        }
        if (req.Method == HttpMethod.Post && path == "/upload/drive/v3/files" && query.GetValueOrDefault("uploadType") == "resumable")
        {
            var length = req.Headers.TryGetValues("X-Upload-Content-Length", out var v) ? long.Parse(v.Single(), CultureInfo.InvariantCulture) : -1;
            var sessionId = $"s{_nextId++}";
            _sessions[sessionId] = (Encoding.UTF8.GetString(body), length);
            var r = new HttpResponseMessage(HttpStatusCode.OK);
            r.Headers.Location = new Uri($"https://{SessionHost}/upload/drive/v3/files?uploadType=resumable&upload_id={sessionId}");
            return r;
        }
        if (req.Method == HttpMethod.Put && path == "/upload/drive/v3/files" && query.TryGetValue("upload_id", out var sid))
        {
            if (!_sessions.Remove(sid, out var session)) return Error(HttpStatusCode.NotFound, "notFound");
            if (session.Length != body.Length) return Error(HttpStatusCode.BadRequest, "badContent");
            return Create(session.Metadata, body);
        }
        return Error(HttpStatusCode.BadRequest, "unexpected");
    }

    private HttpResponseMessage List(Dictionary<string, string> query)
    {
        if (query.GetValueOrDefault("spaces") != "appDataFolder" || query.GetValueOrDefault("q") != "trashed = false") return Error(HttpStatusCode.BadRequest, "badQuery");
        var start = query.TryGetValue("pageToken", out var token) ? int.Parse(token, CultureInfo.InvariantCulture) : 0;
        var page = _nodes.Skip(start).Take(PageSize).ToList();
        var files = new JsonArray();
        foreach (var n in page) files.Add(Describe(n));
        var result = new JsonObject { ["files"] = files };
        if (start + page.Count < _nodes.Count) result["nextPageToken"] = (start + page.Count).ToString(CultureInfo.InvariantCulture);
        return Json(result);
    }

    private HttpResponseMessage Create(string metadata, byte[]? data)
    {
        var meta = JsonNode.Parse(metadata)!.AsObject();
        foreach (var field in new[] { "createdTime", "modifiedTime" })
        {
            if (meta.ContainsKey(field)) ForbiddenFields.Add(field);
        }
        var name = meta["name"]!.GetValue<string>();
        var parent = meta["parents"]!.AsArray().Single()!.GetValue<string>();
        if (parent != AppDataId && !_nodes.Any(n => n.Id == parent && n.IsFolder)) return Error(HttpStatusCode.NotFound, "notFound");
        var folder = meta["mimeType"]?.GetValue<string>() == FolderMime;
        if (folder != (data is null)) return Error(HttpStatusCode.BadRequest, "badContent");
        var node = Add(name, folder, parent, data ?? Array.Empty<byte>(), null);
        return Json(Describe(node));
    }

    private static (string? Metadata, byte[] Data) Multipart(HttpRequestMessage req, byte[] body)
    {
        var type = req.Content!.Headers.ContentType;
        if (type?.MediaType != "multipart/related") return (null, Array.Empty<byte>());
        var boundary = type.Parameters.Single(p => p.Name == "boundary").Value!.Trim('"');
        var text = Encoding.Latin1.GetString(body);
        var parts = text.Split("--" + boundary);
        // parts: "", metadata part, data part, "--\r\n"
        if (parts.Length != 4 || parts[3] != "--\r\n") return (null, Array.Empty<byte>());
        static (string Head, string Body) Split(string part)
        {
            var i = part.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            return (part[..i], part[(i + 4)..^2]);
        }
        var (metaHead, metaBody) = Split(parts[1]);
        var (dataHead, dataBody) = Split(parts[2]);
        if (!metaHead.Contains("application/json", StringComparison.Ordinal) || !dataHead.Contains("application/octet-stream", StringComparison.Ordinal)) return (null, Array.Empty<byte>());
        return (Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(metaBody)), Encoding.Latin1.GetBytes(dataBody));
    }

    private static JsonObject Describe(Node n)
    {
        var o = new JsonObject
        {
            ["id"] = n.Id,
            ["name"] = n.Name,
            ["mimeType"] = n.IsFolder ? FolderMime : "application/octet-stream",
            ["createdTime"] = n.Created.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["parents"] = new JsonArray(n.Parent),
        };
        if (!n.IsFolder) o["size"] = n.Data.Length.ToString(CultureInfo.InvariantCulture);
        return o;
    }

    private static Dictionary<string, string> Query(Uri uri)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            result[Uri.UnescapeDataString(pair[..eq])] = Uri.UnescapeDataString(pair[(eq + 1)..]);
        }
        return result;
    }

    private static HttpResponseMessage Json(JsonNode node)
        => new(HttpStatusCode.OK) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Error(HttpStatusCode status, string reason)
    {
        var body = new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["code"] = (int)status,
                ["message"] = "Fake error.",
                ["errors"] = new JsonArray(new JsonObject { ["domain"] = "global", ["reason"] = reason, ["message"] = "Fake error." }),
            },
        };
        return new HttpResponseMessage(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
    }
}

/// <summary>Access tokens for the fake: the first, and the one a forced refresh returns.</summary>
internal sealed class FakeGoogleTokens : IGoogleTokenSource
{
    public const string First = "fake-google-token-1";
    public const string Refreshed = "fake-google-token-2";

    public int Refreshes { get; private set; }

    public Task<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken ct = default)
    {
        if (forceRefresh) Refreshes++;
        return Task.FromResult(Refreshes > 0 ? Refreshed : First);
    }
}
