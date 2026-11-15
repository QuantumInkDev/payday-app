using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PayDay.Sync.Drive;
using PayDay.Sync.Drive.OneDrive;

namespace PayDay.Tests.Sync.OneDrive;

/// <summary>
/// A small in-memory Microsoft Graph for <see cref="OneDriveDriveProvider"/>: the app folder with the
/// vault folder in it, paged child listings (<c>@odata.nextLink</c>), path uploads with
/// <c>conflictBehavior=fail</c>, upload sessions, downloads through a pre-authenticated redirect, deletes
/// into a recycle bin, a server clock in every <c>Date</c> header, bearer-token checks and injected
/// failures. No network. All names and data are fictional.
/// </summary>
internal sealed class FakeGraph : HttpMessageHandler
{
    private const string AppRootPrefix = "/v1.0/me/drive/special/approot:/";
    private const string Vault = OneDriveConfig.VaultFolder;

    private readonly object _lock = new();
    private readonly Dictionary<string, StoredFile> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage?>> _faults = new();
    private long _nextId = 1;

    public FakeGraph(DateTimeOffset start) => Now = start;

    /// <summary>The server's clock (every response's <c>Date</c>, every new file's <c>createdDateTime</c>).</summary>
    public DateTimeOffset Now { get; set; }

    /// <summary>Where next-page links point (a hostile or broken server could point them elsewhere).</summary>
    public string NextLinkBase { get; set; } = "https://graph.microsoft.com";

    /// <summary>Children per listing page.</summary>
    public int PageSize { get; set; } = 200;

    /// <summary>Bearer tokens Graph accepts.</summary>
    public HashSet<string> ValidTokens { get; } = new(StringComparer.Ordinal) { FakeTokens.First, FakeTokens.Refreshed };

    public List<Recorded> Requests { get; } = new();

    public List<string> RecycleBin { get; } = new();

    public sealed record Recorded(HttpMethod Method, Uri Uri, string? Authorization, byte[] Body, string? ContentRange, string? ContentType);

    private sealed class StoredFile
    {
        public required string Id { get; init; }
        public required string Path { get; init; }
        public required byte[] Data { get; init; }
        public required DateTimeOffset Created { get; init; }
    }

    private sealed class Session
    {
        public required string Path { get; init; }
        public required byte[] Data { get; init; }
        public long Received { get; set; }
    }

    public IReadOnlyList<string> Paths
    {
        get
        {
            lock (_lock) return _files.Keys.Order(StringComparer.Ordinal).ToArray();
        }
    }

    public byte[] Read(string path)
    {
        lock (_lock) return _files[path].Data.ToArray();
    }

    /// <summary>Adds a file directly (vault-relative path), created now or at <paramref name="created"/>.</summary>
    public void Put(string path, byte[] data, DateTimeOffset? created = null)
    {
        lock (_lock) Store(path, data, created ?? Now);
    }

    /// <summary>The next request (or the next one <paramref name="when"/> matches) gets this status instead.</summary>
    public void FailNext(HttpStatusCode status, TimeSpan? retryAfter = null, Func<HttpRequestMessage, bool>? when = null, string? errorCode = null)
    {
        lock (_lock)
        {
            _faults.Enqueue(req =>
            {
                if (when is not null && !when(req)) return null;
                var resp = Error(status, errorCode ?? status.ToString(), "Injected failure.");
                if (retryAfter is { } ra) resp.Headers.RetryAfter = new RetryConditionHeaderValue(ra);
                return resp;
            });
        }
    }

    /// <summary>The next request fails before any reply, like a dropped connection.</summary>
    public void DropNext(int count = 1)
    {
        lock (_lock)
        {
            for (var i = 0; i < count; i++) _faults.Enqueue(_ => throw new HttpRequestException("Connection reset (injected)."));
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var auth = request.Headers.Authorization?.ToString();
        lock (_lock)
        {
            Requests.Add(new Recorded(request.Method, request.RequestUri!, auth, body, request.Content?.Headers.ContentRange?.ToString(), request.Content?.Headers.ContentType?.MediaType));
            var response = Handle(request, body, auth);
            response.Headers.Date = Now;
            response.RequestMessage = request;
            return response;
        }
    }

    private HttpResponseMessage Handle(HttpRequestMessage request, byte[] body, string? auth)
    {
        if (_faults.Count > 0)
        {
            var fault = _faults.Peek();
            HttpResponseMessage? injected;
            try
            {
                injected = fault(request);
            }
            catch (HttpRequestException)
            {
                _faults.Dequeue();
                throw;
            }
            if (injected is not null)
            {
                _faults.Dequeue();
                return injected;
            }
        }
        var uri = request.RequestUri!;
        if (uri.Host == "upload.fake") return Upload(request, uri, body);
        if (uri.Host == "download.fake")
        {
            var id = uri.AbsolutePath.Trim('/');
            var file = _files.Values.SingleOrDefault(f => f.Id == id);
            return file is null ? Error(HttpStatusCode.NotFound, "itemNotFound", "Gone.") : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file.Data) };
        }
        if (uri.Host != "graph.microsoft.com") return new HttpResponseMessage(HttpStatusCode.BadGateway);
        if (auth is null || !auth.StartsWith("Bearer ", StringComparison.Ordinal) || !ValidTokens.Contains(auth["Bearer ".Length..]))
        {
            return Error(HttpStatusCode.Unauthorized, "InvalidAuthenticationToken", "Access token has expired.");
        }

        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        var query = uri.Query;
        if (path == "/v1.0/me/drive/special/approot")
        {
            return Json(HttpStatusCode.OK, new JsonObject { ["id"] = "approot-id" });
        }
        if (path.StartsWith(AppRootPrefix, StringComparison.Ordinal))
        {
            var rest = path[AppRootPrefix.Length..];
            var split = rest.LastIndexOf(":/", StringComparison.Ordinal);
            var itemPath = rest[..split];
            var action = rest[(split + 2)..];
            if (itemPath != Vault && !itemPath.StartsWith(Vault + "/", StringComparison.Ordinal)) return Error(HttpStatusCode.BadRequest, "invalidRequest", "Outside the vault.");
            var vaultPath = itemPath == Vault ? string.Empty : itemPath[(Vault.Length + 1)..];
            if (request.Method == HttpMethod.Get && action == "children") return List(vaultPath, query);
            if (request.Method == HttpMethod.Put && action == "content")
            {
                if (!query.Contains("@microsoft.graph.conflictBehavior=fail", StringComparison.Ordinal)) return Error(HttpStatusCode.BadRequest, "invalidRequest", "Expected conflictBehavior=fail.");
                if (_files.ContainsKey(vaultPath)) return Error(HttpStatusCode.Conflict, "nameAlreadyExists", "The specified item name already exists.");
                return Json(HttpStatusCode.Created, ItemJson(Store(vaultPath, body, Now)));
            }
            if (request.Method == HttpMethod.Post && action == "createUploadSession")
            {
                var json = JsonNode.Parse(body);
                if (json?["item"]?["@microsoft.graph.conflictBehavior"]?.GetValue<string>() != "fail") return Error(HttpStatusCode.BadRequest, "invalidRequest", "Expected conflictBehavior=fail.");
                if (json["item"]?["fileSystemInfo"] is not null) return Error(HttpStatusCode.BadRequest, "invalidRequest", "Clients must not set fileSystemInfo.");
                if (_files.ContainsKey(vaultPath)) return Error(HttpStatusCode.Conflict, "nameAlreadyExists", "The specified item name already exists.");
                var sessionId = (_nextId++).ToString(CultureInfo.InvariantCulture);
                _sessions[sessionId] = new Session { Path = vaultPath, Data = new byte[0] };
                return Json(HttpStatusCode.OK, new JsonObject { ["uploadUrl"] = $"https://upload.fake/session/{sessionId}" });
            }
            return Error(HttpStatusCode.BadRequest, "invalidRequest", $"Unknown action {action}.");
        }
        const string items = "/v1.0/me/drive/items/";
        if (path.StartsWith(items, StringComparison.Ordinal))
        {
            var rest = path[items.Length..];
            var id = rest.Split('/')[0];
            var file = _files.Values.SingleOrDefault(f => f.Id == id);
            if (request.Method == HttpMethod.Get && rest.EndsWith("/content", StringComparison.Ordinal))
            {
                if (file is null) return Error(HttpStatusCode.NotFound, "itemNotFound", "Item not found.");
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri($"https://download.fake/{id}");
                return redirect;
            }
            if (request.Method == HttpMethod.Delete)
            {
                if (file is null) return Error(HttpStatusCode.NotFound, "itemNotFound", "Item not found.");
                _files.Remove(file.Path);
                RecycleBin.Add(file.Path);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
        }
        return Error(HttpStatusCode.BadRequest, "invalidRequest", $"Unknown request {request.Method} {path}.");
    }

    private HttpResponseMessage Upload(HttpRequestMessage request, Uri uri, byte[] body)
    {
        if (request.Headers.Authorization is not null) return Error(HttpStatusCode.BadRequest, "invalidRequest", "Upload URLs are pre-authenticated; no Authorization header.");
        var sessionId = uri.AbsolutePath.Split('/').Last();
        if (!_sessions.TryGetValue(sessionId, out var session)) return Error(HttpStatusCode.NotFound, "itemNotFound", "No such session.");
        if (request.Method == HttpMethod.Delete)
        {
            _sessions.Remove(sessionId);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
        var range = request.Content?.Headers.ContentRange;
        if (range?.From is not { } from || range.To is not { } to || range.Length is not { } total || from != session.Received || to - from + 1 != body.Length)
        {
            return Error(HttpStatusCode.RequestedRangeNotSatisfiable, "invalidRange", "Bad Content-Range.");
        }
        if (body.Length % (320 * 1024) != 0 && to + 1 != total) return Error(HttpStatusCode.BadRequest, "invalidRange", "Chunks must be multiples of 320 KiB.");
        var data = session.Data.Concat(body).ToArray();
        _sessions[sessionId] = new Session { Path = session.Path, Data = data, Received = data.Length };
        if (data.Length < total)
        {
            return Json(HttpStatusCode.Accepted, new JsonObject { ["nextExpectedRanges"] = new JsonArray($"{data.Length}-") });
        }
        _sessions.Remove(sessionId);
        if (_files.ContainsKey(session.Path)) return Error(HttpStatusCode.Conflict, "nameAlreadyExists", "The specified item name already exists.");
        return Json(HttpStatusCode.Created, ItemJson(Store(session.Path, data, Now)));
    }

    private HttpResponseMessage List(string folder, string query)
    {
        var prefix = folder.Length == 0 ? string.Empty : folder + "/";
        var exists = folder.Length == 0 ? _files.Count > 0 : _files.Keys.Any(p => p.StartsWith(prefix, StringComparison.Ordinal));
        if (!exists) return Error(HttpStatusCode.NotFound, "itemNotFound", "The resource could not be found.");
        var children = new List<JsonObject>();
        foreach (var sub in _files.Keys.Where(p => p.StartsWith(prefix, StringComparison.Ordinal)).Select(p => p[prefix.Length..]).Where(p => p.Contains('/')).Select(p => p[..p.IndexOf('/')]).Distinct().Order(StringComparer.Ordinal))
        {
            children.Add(new JsonObject { ["id"] = $"folder-{prefix}{sub}", ["name"] = sub, ["folder"] = new JsonObject { ["childCount"] = 1 }, ["createdDateTime"] = "2025-01-01T00:00:00Z" });
        }
        foreach (var f in _files.Values.Where(f => f.Path.StartsWith(prefix, StringComparison.Ordinal) && !f.Path[prefix.Length..].Contains('/')).OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            children.Add(ItemJson(f));
        }
        var skip = 0;
        var token = System.Web.HttpUtility.ParseQueryString(query)["$skiptoken"];
        if (token is not null) skip = int.Parse(token, CultureInfo.InvariantCulture);
        var page = children.Skip(skip).Take(PageSize).ToList();
        var result = new JsonObject { ["value"] = new JsonArray(page.Select(c => (JsonNode)c).ToArray()) };
        if (skip + PageSize < children.Count)
        {
            var address = folder.Length == 0 ? Vault : $"{Vault}/{folder}";
            result["@odata.nextLink"] = $"{NextLinkBase}/v1.0/me/drive/special/approot:/{address}:/children?$top={PageSize}&$skiptoken={skip + PageSize}";
        }
        return Json(HttpStatusCode.OK, result);
    }

    private StoredFile Store(string path, byte[] data, DateTimeOffset created)
    {
        var file = new StoredFile { Id = $"ITEM{(_nextId++).ToString(CultureInfo.InvariantCulture)}", Path = path, Data = data.ToArray(), Created = created };
        _files[path] = file;
        return file;
    }

    private static JsonObject ItemJson(StoredFile f) => new()
    {
        ["id"] = f.Id,
        ["name"] = f.Path[(f.Path.LastIndexOf('/') + 1)..],
        ["size"] = f.Data.Length,
        ["createdDateTime"] = f.Created.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        // A client-set time that readers must ignore (sync-protocol.md 11.1).
        ["fileSystemInfo"] = new JsonObject { ["createdDateTime"] = "2001-01-01T00:00:00Z" },
        ["file"] = new JsonObject { ["mimeType"] = "application/octet-stream" },
    };

    private static HttpResponseMessage Json(HttpStatusCode status, JsonNode body)
        => new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(HttpStatusCode status, string code, string message)
        => Json(status, new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message } });
}

/// <summary>A token source for <see cref="FakeGraph"/>: "token-1", and "token-2" after a forced refresh.</summary>
internal sealed class FakeTokens : IOneDriveTokenSource
{
    public const string First = "token-1";
    public const string Refreshed = "token-2";

    public List<bool> Calls { get; } = new();

    /// <summary>When set, every call fails with this kind (the user must sign in again, or the sign-in service is down).</summary>
    public DriveErrorKind? FailWith { get; set; }

    public Task<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken ct = default)
    {
        Calls.Add(forceRefresh);
        if (FailWith is { } kind) throw new DriveException(kind, "Token unavailable (test).");
        return Task.FromResult(forceRefresh ? Refreshed : First);
    }
}
