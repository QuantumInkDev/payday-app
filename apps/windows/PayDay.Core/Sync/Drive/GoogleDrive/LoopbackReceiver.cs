using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PayDay.Sync.Drive.GoogleDrive;

/// <summary>
/// Receives the OAuth redirect in the system-browser sign-in: listens on <c>127.0.0.1</c> at a free
/// port (no URL reservation or admin rights needed, unlike <c>HttpListener</c>), waits for the browser's
/// <c>GET /?code=…&amp;state=…</c> (or <c>?error=…</c>) and answers it with a short page. Other requests
/// (a favicon, a probe) get a 404 and are ignored. Only the loopback interface is bound, so nothing
/// off this machine can reach it.
/// </summary>
public sealed class LoopbackReceiver : IDisposable
{
    /// <summary>The longest request head read; anything longer is not a redirect from Google.</summary>
    public const int MaxRequestBytes = 16 * 1024;

    private readonly TcpListener _listener;

    public LoopbackReceiver()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    public int Port { get; }

    /// <summary>The redirect URI to register with the authorization request.</summary>
    public string RedirectUri => $"http://127.0.0.1:{Port}";

    public void Dispose() => _listener.Stop();

    /// <summary>
    /// Waits for the redirect and returns its query parameters. <paramref name="page"/> picks the page
    /// sent back to the browser from those parameters.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> WaitForRedirectAsync(Func<IReadOnlyDictionary<string, string>, string> page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(page);
        while (true)
        {
            using var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            using var stream = client.GetStream();
            string? target;
            try
            {
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                target = await ReadRequestTargetAsync(stream, readTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                continue; // a connection that never sent a request
            }
            catch (IOException)
            {
                continue;
            }
            var query = target is null ? null : ParseRedirectTarget(target);
            if (query is null || !(query.ContainsKey("code") || query.ContainsKey("error")))
            {
                await RespondAsync(stream, 404, "Not found", "<p>Not found.</p>", ct).ConfigureAwait(false);
                continue;
            }
            await RespondAsync(stream, 200, "OK", page(query), ct).ConfigureAwait(false);
            return query;
        }
    }

    /// <summary>The target of a <c>GET</c> request line, or null when the head is not a GET request.</summary>
    private static async Task<string?> ReadRequestTargetAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxRequestBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
            if (IndexOfHeadEnd(buffer, length) >= 0) break;
        }
        var head = Encoding.ASCII.GetString(buffer, 0, length);
        var lineEnd = head.IndexOf("\r\n", StringComparison.Ordinal);
        if (lineEnd < 0) return null;
        var parts = head[..lineEnd].Split(' ');
        return parts.Length == 3 && parts[0] == "GET" && parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal) ? parts[1] : null;
    }

    private static int IndexOfHeadEnd(byte[] buffer, int length)
    {
        for (var i = 3; i < length; i++)
        {
            if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n' && buffer[i - 1] == '\r' && buffer[i] == '\n') return i;
        }
        return -1;
    }

    /// <summary>The query of a redirect to the root path (<c>/?a=b</c>), or null for any other target.</summary>
    internal static IReadOnlyDictionary<string, string>? ParseRedirectTarget(string target)
    {
        var q = target.IndexOf('?');
        var path = q < 0 ? target : target[..q];
        if (path != "/") return null;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (q < 0) return result;
        foreach (var pair in target[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var name = Decode(eq < 0 ? pair : pair[..eq]);
            var value = eq < 0 ? string.Empty : Decode(pair[(eq + 1)..]);
            // A repeated parameter makes the redirect ambiguous; refuse it rather than pick one.
            if (!result.TryAdd(name, value)) return null;
        }
        return result;
    }

    private static string Decode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));

    private static async Task RespondAsync(NetworkStream stream, int status, string reason, string html, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes($"<!doctype html><html><head><meta charset=\"utf-8\"><title>FutureCheck</title></head><body style=\"font-family:system-ui,sans-serif;margin:3em\">{html}</body></html>");
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n");
        try
        {
            await stream.WriteAsync(head, ct).ConfigureAwait(false);
            await stream.WriteAsync(body, ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The browser went away; the redirect itself was already received.
        }
    }
}
