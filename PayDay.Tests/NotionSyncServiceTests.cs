using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using PayDay.Models;
using PayDay.Services;

namespace PayDay.Tests;

public class NotionSyncServiceTests
{
    private const string BillsDsId = "bills-ds-id";
    private const string PaymentsDsId = "payments-ds-id";

    private static (FakeDatabaseService db, InMemoryCredentialStore creds) SetupFakes(bool seedToken = true)
    {
        var db = new FakeDatabaseService();
        db.Settings[NotionSyncService.BillsDataSourceSetting] = BillsDsId;
        db.Settings[NotionSyncService.PaymentsDataSourceSetting] = PaymentsDsId;
        db.Settings[NotionSyncService.SnapshotsDataSourceSetting] = "snap-ds-id";
        var creds = new InMemoryCredentialStore();
        if (seedToken) creds.Set(NotionSyncService.TokenKey, "secret_test_token");
        return (db, creds);
    }

    private static NotionSyncService Build(FakeDatabaseService db, InMemoryCredentialStore creds, RecordingHttpHandler handler)
        => new(db, creds, handler);

    private static string QueryResponse(params string[] pageJsons)
    {
        var joined = string.Join(",", pageJsons);
        return $$"""{ "object": "list", "results": [{{joined}}], "has_more": false, "next_cursor": null }""";
    }

    private static string PageJson(
        string id,
        string billId,
        string name,
        string lastEdited,
        string type = "Cards",
        double payment = 0,
        double owed = 0,
        bool active = true,
        bool autoPay = false,
        int dueDay = 1)
    {
        static string T(string content) =>
            $$"""[{ "type": "text", "text": { "content": {{JsonSerializer.Serialize(content)}} }, "plain_text": {{JsonSerializer.Serialize(content)}} }]""";

        var nameTitle = T(name);
        var typeText = T(type);
        var billIdText = T(billId);
        var paymentN = payment.ToString("R", CultureInfo.InvariantCulture);
        var owedN = owed.ToString("R", CultureInfo.InvariantCulture);
        var dueDayN = dueDay.ToString(CultureInfo.InvariantCulture);
        var activeC = active ? "true" : "false";
        var autoPayC = autoPay ? "true" : "false";

        return $$"""
        {
          "object": "page",
          "id": {{JsonSerializer.Serialize(id)}},
          "last_edited_time": {{JsonSerializer.Serialize(lastEdited)}},
          "archived": false,
          "properties": {
            "Name":         { "type": "title",     "title":     {{nameTitle}} },
            "Type":         { "type": "rich_text", "rich_text": {{typeText}} },
            "Payment":      { "type": "number",    "number": {{paymentN}} },
            "Owed":         { "type": "number",    "number": {{owedN}} },
            "Available":    { "type": "number",    "number": 0 },
            "Credit Limit": { "type": "number",    "number": 0 },
            "Due Day":      { "type": "number",    "number": {{dueDayN}} },
            "APR":          { "type": "number",    "number": 0 },
            "Frequency":    { "type": "rich_text", "rich_text": [] },
            "Auto-Pay":     { "type": "checkbox",  "checkbox": {{autoPayC}} },
            "Active":       { "type": "checkbox",  "checkbox": {{activeC}} },
            "Bill ID":      { "type": "rich_text", "rich_text": {{billIdText}} },
            "Yearly Date":  { "type": "rich_text", "rich_text": [] },
            "Notes":        { "type": "rich_text", "rich_text": [] }
          }
        }
        """;
    }

    // ------------------------------------------------------------------
    // Connection checks
    // ------------------------------------------------------------------

    [Fact]
    public async Task TestConnection_NoToken_ReturnsFalse()
    {
        var (db, creds) = SetupFakes(seedToken: false);
        var handler = new RecordingHttpHandler();
        using var svc = Build(db, creds, handler);

        Assert.False(await svc.TestConnectionAsync());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TestConnection_2xx_ReturnsTrue()
    {
        var (db, creds) = SetupFakes();
        var handler = new RecordingHttpHandler();
        handler.OnGet("v1/users/me", _ => RecordingHttpHandler.Ok("""{ "id": "u1", "type": "person" }"""));
        using var svc = Build(db, creds, handler);

        Assert.True(await svc.TestConnectionAsync());
        var req = Assert.Single(handler.Requests);
        Assert.Equal("Bearer secret_test_token", req.Headers["Authorization"]);
        Assert.Equal("2025-09-03", req.Headers["Notion-Version"]);
    }

    [Fact]
    public async Task TestConnection_401_ReturnsFalse()
    {
        var (db, creds) = SetupFakes();
        var handler = new RecordingHttpHandler();
        handler.OnGet("v1/users/me", _ => RecordingHttpHandler.Json(HttpStatusCode.Unauthorized, """{ "code": "unauthorized" }"""));
        using var svc = Build(db, creds, handler);

        Assert.False(await svc.TestConnectionAsync());
    }

    // ------------------------------------------------------------------
    // SyncBillsAsync — preflight
    // ------------------------------------------------------------------

    [Fact]
    public async Task SyncBills_NoToken_Throws()
    {
        var (db, creds) = SetupFakes(seedToken: false);
        using var svc = Build(db, creds, new RecordingHttpHandler());
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SyncBillsAsync());
    }

    [Fact]
    public async Task SyncBills_NoDataSourceId_Throws()
    {
        var (db, creds) = SetupFakes();
        db.Settings[NotionSyncService.BillsDataSourceSetting] = null;
        using var svc = Build(db, creds, new RecordingHttpHandler());
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SyncBillsAsync());
    }

    // ------------------------------------------------------------------
    // SyncBillsAsync — create / update / pull
    // ------------------------------------------------------------------

    [Fact]
    public async Task SyncBills_LocalBillNotInNotion_CreatesPage()
    {
        var (db, creds) = SetupFakes();
        db.Bills.Add(new Bill { Id = "1", Name = "Electric", Type = "Bills", Cost = 400, DueDay = 8, Rate = "Monthly", Active = true, UpdatedAt = "2026-05-15 10:00:00" });
        var handler = new RecordingHttpHandler();
        handler.OnPost($"v1/data_sources/{BillsDsId}/query", _ => RecordingHttpHandler.Ok(QueryResponse()));
        handler.OnPost("v1/pages", _ => RecordingHttpHandler.Ok("""{ "id": "new-page-id" }"""));
        using var svc = Build(db, creds, handler);

        var result = await svc.SyncBillsAsync();

        Assert.Equal(1, result.Created);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.Pulled);
        Assert.False(result.HasErrors);
        Assert.Equal("new-page-id", db.Bills[0].NotionPageId);

        var createReq = handler.Requests.Single(r => r.Method == HttpMethod.Post && r.Path == "v1/pages");
        Assert.Contains("\"data_source_id\":\"" + BillsDsId + "\"", createReq.Body);
        Assert.Contains("\"Bill ID\"", createReq.Body);
        Assert.Contains("\"Electric\"", createReq.Body);
    }

    [Fact]
    public async Task SyncBills_LocalNewer_UpdatesNotion()
    {
        var (db, creds) = SetupFakes();
        db.Bills.Add(new Bill
        {
            Id = "1",
            Name = "Electric (renamed)",
            Type = "Bills",
            Cost = 400,
            DueDay = 8,
            Rate = "Monthly",
            Active = true,
            UpdatedAt = "2026-05-15 12:00:00",
            NotionPageId = "page-1",
        });
        var handler = new RecordingHttpHandler();
        handler.OnPost($"v1/data_sources/{BillsDsId}/query", _ => RecordingHttpHandler.Ok(QueryResponse(
            PageJson("page-1", "1", "Electric", "2026-05-15T10:00:00.000Z"))));
        handler.OnPatch("v1/pages/page-1", _ => RecordingHttpHandler.Ok("""{ "id": "page-1" }"""));
        using var svc = Build(db, creds, handler);

        var result = await svc.SyncBillsAsync();

        Assert.Equal(0, result.Created);
        Assert.Equal(1, result.Updated);
        Assert.Equal(0, result.Pulled);

        var patch = handler.Requests.Single(r => r.Method == HttpMethod.Patch);
        Assert.Equal("v1/pages/page-1", patch.Path);
        Assert.Contains("Electric (renamed)", patch.Body);
    }

    [Fact]
    public async Task SyncBills_RemoteNewer_PullsIntoLocal()
    {
        var (db, creds) = SetupFakes();
        db.Bills.Add(new Bill
        {
            Id = "1",
            Name = "Electric",
            Type = "Bills",
            Cost = 400,
            DueDay = 8,
            Rate = "Monthly",
            Active = true,
            UpdatedAt = "2026-05-15 08:00:00",
            NotionPageId = "page-1",
        });
        var handler = new RecordingHttpHandler();
        handler.OnPost($"v1/data_sources/{BillsDsId}/query", _ => RecordingHttpHandler.Ok(QueryResponse(
            PageJson("page-1", "1", "Electric Co.", "2026-05-15T15:00:00.000Z", type: "Bills", payment: 425, dueDay: 8))));
        using var svc = Build(db, creds, handler);

        var result = await svc.SyncBillsAsync();

        Assert.Equal(1, result.Pulled);
        Assert.Equal("Electric Co.", db.Bills[0].Name);
        Assert.Equal(425, db.Bills[0].Cost);
        Assert.Equal("page-1", db.Bills[0].NotionPageId);
        Assert.DoesNotContain(handler.Requests, r => r.Method == HttpMethod.Patch);
    }

    [Fact]
    public async Task SyncBills_BillIdMatch_PopulatesNotionPageIdOnLocal()
    {
        var (db, creds) = SetupFakes();
        // Local bill has matching ID but no NotionPageId yet.
        db.Bills.Add(new Bill
        {
            Id = "1",
            Name = "Electric",
            Type = "Bills",
            Cost = 400,
            DueDay = 8,
            Rate = "Monthly",
            Active = true,
            UpdatedAt = "2026-05-15 12:00:00",
            NotionPageId = null,
        });
        var handler = new RecordingHttpHandler();
        handler.OnPost($"v1/data_sources/{BillsDsId}/query", _ => RecordingHttpHandler.Ok(QueryResponse(
            PageJson("page-1", "1", "Electric", "2026-05-15T10:00:00.000Z"))));
        handler.OnPatch("v1/pages/page-1", _ => RecordingHttpHandler.Ok("""{ "id": "page-1" }"""));
        using var svc = Build(db, creds, handler);

        var result = await svc.SyncBillsAsync();

        Assert.Equal("page-1", db.Bills[0].NotionPageId);
        Assert.Equal(1, result.Updated);
    }

    [Fact]
    public async Task SyncBills_RemoteOnly_PullsAsNewLocal()
    {
        var (db, creds) = SetupFakes();
        // No local bills.
        var handler = new RecordingHttpHandler();
        handler.OnPost($"v1/data_sources/{BillsDsId}/query", _ => RecordingHttpHandler.Ok(QueryResponse(
            PageJson("page-2", "99", "Internet", "2026-05-15T10:00:00.000Z", payment: 75, dueDay: 5, type: "Bills"))));
        using var svc = Build(db, creds, handler);

        var result = await svc.SyncBillsAsync();

        Assert.Equal(1, result.Pulled);
        Assert.Equal(0, result.Created);
        Assert.Single(db.Bills);
        Assert.Equal("99", db.Bills[0].Id);
        Assert.Equal("Internet", db.Bills[0].Name);
        Assert.Equal(75, db.Bills[0].Cost);
        Assert.Equal("page-2", db.Bills[0].NotionPageId);
    }

    [Fact]
    public async Task SyncBills_SetsLastSyncedTimestamp()
    {
        var (db, creds) = SetupFakes();
        var handler = new RecordingHttpHandler();
        handler.OnPost($"v1/data_sources/{BillsDsId}/query", _ => RecordingHttpHandler.Ok(QueryResponse()));
        using var svc = Build(db, creds, handler);

        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        await svc.SyncBillsAsync();
        var stored = await svc.GetLastSyncedAsync();

        Assert.NotNull(stored);
        Assert.InRange(stored!.Value, before, DateTimeOffset.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task SyncBills_PageCreateFailure_RecordedAsError_OthersContinue()
    {
        var (db, creds) = SetupFakes();
        db.Bills.Add(new Bill { Id = "1", Name = "Bad", Type = "Bills", Cost = 0, DueDay = 1, Rate = "Monthly", Active = true, UpdatedAt = "2026-05-15 10:00:00" });
        db.Bills.Add(new Bill { Id = "2", Name = "Good", Type = "Bills", Cost = 0, DueDay = 1, Rate = "Monthly", Active = true, UpdatedAt = "2026-05-15 10:00:00" });
        var handler = new RecordingHttpHandler();
        handler.OnPost($"v1/data_sources/{BillsDsId}/query", _ => RecordingHttpHandler.Ok(QueryResponse()));
        var calls = 0;
        handler.OnPost("v1/pages", _ =>
        {
            calls++;
            return calls == 1
                ? RecordingHttpHandler.Json(HttpStatusCode.BadRequest, """{ "code": "validation_error" }""")
                : RecordingHttpHandler.Ok("""{ "id": "page-good" }""");
        });
        using var svc = Build(db, creds, handler);

        var result = await svc.SyncBillsAsync();

        Assert.Equal(1, result.Created);
        Assert.Single(result.Errors);
        Assert.Contains("Bad", result.Errors[0]);
    }

    // ------------------------------------------------------------------
    // Push helpers (5c)
    // ------------------------------------------------------------------

    [Fact]
    public async Task PushPaymentAsync_PostsToPaymentsDataSource()
    {
        var (db, creds) = SetupFakes();
        var handler = new RecordingHttpHandler();
        handler.OnPost("v1/pages", _ => RecordingHttpHandler.Ok("""{ "id": "payment-page" }"""));
        using var svc = Build(db, creds, handler);

        var pageId = await svc.PushPaymentAsync(
            new Payment { Id = 1, BillId = "1", PeriodKey = "2026-05-15", AmountPaid = 400, PaidAt = "2026-05-15T18:00:00Z" },
            "Electric");

        Assert.Equal("payment-page", pageId);
        var req = handler.Requests.Single(r => r.Method == HttpMethod.Post && r.Path == "v1/pages");
        Assert.Contains("payments-ds-id", req.Body);
        Assert.Contains("2026-05-15", req.Body);
        Assert.Contains("Electric", req.Body);
    }

    [Fact]
    public async Task PushSnapshotAsync_PostsToSnapshotsDataSource()
    {
        var (db, creds) = SetupFakes();
        var handler = new RecordingHttpHandler();
        handler.OnPost("v1/pages", _ => RecordingHttpHandler.Ok("""{ "id": "snap-page" }"""));
        using var svc = Build(db, creds, handler);

        var pageId = await svc.PushSnapshotAsync(new Snapshot { Id = 1, SnapshotDate = "2026-05-15", TotalOwed = 12000, Details = "{}" });

        Assert.Equal("snap-page", pageId);
        var req = handler.Requests.Single(r => r.Method == HttpMethod.Post && r.Path == "v1/pages");
        Assert.Contains("snap-ds-id", req.Body);
        Assert.Contains("2026-05-15", req.Body);
    }

    // ------------------------------------------------------------------
    // ParseSqliteUtc — focused
    // ------------------------------------------------------------------

    [Fact]
    public void ParseSqliteUtc_HandlesSqliteFormat()
    {
        var v = NotionSyncService.ParseSqliteUtc("2026-05-15 14:23:11");
        Assert.Equal(2026, v.Year);
        Assert.Equal(5, v.Month);
        Assert.Equal(TimeSpan.Zero, v.Offset);
    }

    [Fact]
    public void ParseSqliteUtc_NullOrEmpty_ReturnsMin()
    {
        Assert.Equal(DateTimeOffset.MinValue, NotionSyncService.ParseSqliteUtc(null));
        Assert.Equal(DateTimeOffset.MinValue, NotionSyncService.ParseSqliteUtc(""));
    }
}
