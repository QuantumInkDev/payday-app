using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace PayDay.Tests;

/// <summary>
/// <see cref="HttpMessageHandler"/> for unit tests that records every outgoing
/// request and dispatches it to a per-route response factory. Routes match
/// against <c>METHOD path</c> (e.g. <c>POST v1/data_sources/abc/query</c>);
/// query strings are ignored.
/// </summary>
internal sealed class RecordingHttpHandler : HttpMessageHandler
{
    public List<RecordedRequest> Requests { get; } = new();
    public Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Routes { get; } = new();
    public Func<HttpRequestMessage, HttpResponseMessage>? Fallback { get; set; }

    public void OnGet(string path, Func<HttpRequestMessage, HttpResponseMessage> factory)
        => Routes[$"GET {path}"] = factory;
    public void OnPost(string path, Func<HttpRequestMessage, HttpResponseMessage> factory)
        => Routes[$"POST {path}"] = factory;
    public void OnPatch(string path, Func<HttpRequestMessage, HttpResponseMessage> factory)
        => Routes[$"PATCH {path}"] = factory;

    public static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Ok(string body) => Json(HttpStatusCode.OK, body);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        var bodyText = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        Requests.Add(new RecordedRequest(request.Method, path, bodyText, ReadHeaders(request)));

        var key = $"{request.Method.Method} {path}";
        if (Routes.TryGetValue(key, out var factory))
            return factory(request);
        if (Fallback is not null)
            return Fallback(request);
        return new HttpResponseMessage(HttpStatusCode.NotImplemented)
        {
            Content = new StringContent($"No route registered for {key}", System.Text.Encoding.UTF8, "text/plain"),
        };
    }

    private static Dictionary<string, string> ReadHeaders(HttpRequestMessage req)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in req.Headers)
            dict[k] = string.Join(",", v);
        return dict;
    }
}

internal sealed record RecordedRequest(HttpMethod Method, string Path, string? Body, IReadOnlyDictionary<string, string> Headers);
