using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PayDay.Models;
using PayDay.Services;

namespace PayDay.Tests.Sync.Local;

/// <summary>
/// A small in-memory Notion for the connector: one Bills data source whose pages keep the properties
/// the connector writes (query, create, update), with <c>last_edited_time</c> set by a test clock on
/// every create or update, like Notion does. Payments and snapshots pages are counted only.
/// </summary>
internal sealed class FakeNotion : HttpMessageHandler
{
    public const string BillsDb = "00000000-0000-0000-0000-000000000102";

    private readonly Func<DateTimeOffset> _now;
    private int _nextPage = 1;

    public FakeNotion(Func<DateTimeOffset> now) => _now = now;

    public sealed class Page
    {
        public required string Id { get; init; }
        public required string Parent { get; init; }
        public required JsonObject Properties { get; set; }
        public DateTimeOffset LastEdited { get; set; }
    }

    public List<Page> Pages { get; } = new();

    public List<string> Calls { get; } = new();

    public IEnumerable<Page> BillPages => Pages.Where(p => p.Parent == BillsDb);

    /// <summary>Adds a Bills page holding what the connector would push for <paramref name="bill"/>.</summary>
    public Page AddBillPage(string pageId, Bill bill, DateTimeOffset lastEdited)
    {
        var page = new Page
        {
            Id = pageId,
            Parent = BillsDb,
            Properties = (JsonObject)JsonSerializer.SerializeToNode(NotionSyncService.BuildBillProperties(bill))!,
            LastEdited = lastEdited,
        };
        Pages.Add(page);
        return page;
    }

    public static string Text(Page page, string property)
        => page.Properties[property]?["rich_text"]?[0]?["text"]?["content"]?.GetValue<string>()
            ?? page.Properties[property]?["title"]?[0]?["text"]?["content"]?.GetValue<string>()
            ?? string.Empty;

    public static double Number(Page page, string property) => page.Properties[property]?["number"]?.GetValue<double>() ?? 0;

    /// <summary>Someone edits a page in Notion.</summary>
    public void EditInNotion(Page page, string property, JsonNode value)
    {
        page.Properties[property] = value;
        page.LastEdited = _now();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        Calls.Add($"{request.Method.Method} {path}");

        if (request.Method == HttpMethod.Post && path.StartsWith("v1/data_sources/", StringComparison.Ordinal) && path.EndsWith("/query", StringComparison.Ordinal))
        {
            var db = path.Split('/')[2];
            var pages = Pages.Where(p => p.Parent == db).ToList();
            if (body?["filter"]?["rich_text"]?["equals"]?.GetValue<string>() is { } billId)
            {
                pages = pages.Where(p => Text(p, "Bill ID") == billId).Take(1).ToList();
            }
            var results = new JsonArray(pages.Select(ToJson).ToArray<JsonNode?>());
            return Ok(new JsonObject { ["object"] = "list", ["results"] = results, ["has_more"] = false, ["next_cursor"] = null });
        }
        if (request.Method == HttpMethod.Post && path == "v1/pages")
        {
            var page = new Page
            {
                Id = $"fake-page-{(_nextPage++).ToString(CultureInfo.InvariantCulture)}",
                Parent = body!["parent"]!["data_source_id"]!.GetValue<string>(),
                Properties = (JsonObject)body["properties"]!.DeepClone(),
                LastEdited = _now(),
            };
            Pages.Add(page);
            return Ok(ToJson(page));
        }
        if (request.Method == HttpMethod.Patch && path.StartsWith("v1/pages/", StringComparison.Ordinal))
        {
            var id = path["v1/pages/".Length..];
            var page = Pages.SingleOrDefault(p => p.Id == id);
            if (page is null) return new HttpResponseMessage(HttpStatusCode.NotFound);
            foreach (var (k, v) in body!["properties"]!.AsObject()) page.Properties[k] = v?.DeepClone();
            page.LastEdited = _now();
            return Ok(ToJson(page));
        }
        return new HttpResponseMessage(HttpStatusCode.NotImplemented);
    }

    private static JsonObject ToJson(Page page) => new()
    {
        ["object"] = "page",
        ["id"] = page.Id,
        ["last_edited_time"] = page.LastEdited.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
        ["archived"] = false,
        ["properties"] = page.Properties.DeepClone(),
    };

    private static HttpResponseMessage Ok(JsonNode body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json") };
}
