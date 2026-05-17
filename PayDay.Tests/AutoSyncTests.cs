using System.Linq;
using System.Net;
using System.Threading.Tasks;
using PayDay.Models;
using PayDay.Services;
using PayDay.ViewModels;

namespace PayDay.Tests;

/// <summary>
/// Auto-sync wiring tests (chunk 5c). PayDayPageViewModel + InsightsPageViewModel
/// fire fire-and-forget Notion pushes after local persistence; tests await the
/// VM's <c>PendingNotionPush</c> task to make the side effects deterministic.
/// </summary>
public class AutoSyncTests
{
    private static FakeDatabaseService SeedDb()
    {
        var db = new FakeDatabaseService();
        db.Settings["PayAnchor"] = "2026-05-15";
        db.Settings[NotionSyncService.BillsDataSourceSetting] = "bills-ds";
        db.Settings[NotionSyncService.PaymentsDataSourceSetting] = "payments-ds";
        db.Settings[NotionSyncService.SnapshotsDataSourceSetting] = "snap-ds";
        db.Bills.Add(new Bill { Id = "1", Name = "Electric", Type = "Bills", Payment =400, DueDay = 15, Rate = "Monthly", Active = true });
        return db;
    }

    private static InMemoryCredentialStore Creds(bool withToken)
    {
        var c = new InMemoryCredentialStore();
        if (withToken) c.Set(NotionSyncService.TokenKey, "test-token");
        return c;
    }

    // ------------------------------------------------------------------
    // PayDayPageViewModel — payments
    // ------------------------------------------------------------------

    [Fact]
    public async Task MarkPaid_NoNotionService_StatusStaysNotConfigured()
    {
        var db = SeedDb();
        var vm = new PayDayPageViewModel(db);
        await vm.LoadAsync(new System.DateTime(2026, 5, 15));

        var row = vm.UnpaidBills.Single();
        await vm.MarkPaidCommand.ExecuteAsync(row);
        if (vm.PendingNotionPush is not null) await vm.PendingNotionPush;

        Assert.Single(db.Payments);
        Assert.Equal(NotionPushStatus.NotConfigured, vm.LastNotionPushStatus);
    }

    [Fact]
    public async Task MarkPaid_NotionWithoutToken_StatusStaysNotConfigured()
    {
        var db = SeedDb();
        var handler = new RecordingHttpHandler();
        using var notion = new NotionSyncService(db, Creds(withToken: false), handler);
        var vm = new PayDayPageViewModel(db, notion);
        await vm.LoadAsync(new System.DateTime(2026, 5, 15));

        var row = vm.UnpaidBills.Single();
        await vm.MarkPaidCommand.ExecuteAsync(row);
        if (vm.PendingNotionPush is not null) await vm.PendingNotionPush;

        Assert.Single(db.Payments);
        Assert.Equal(NotionPushStatus.NotConfigured, vm.LastNotionPushStatus);
        Assert.Empty(handler.Requests); // no token → no HTTP call
    }

    [Fact]
    public async Task MarkPaid_PushSucceeds_StatusOk()
    {
        var db = SeedDb();
        var handler = new RecordingHttpHandler();
        handler.OnPost("v1/pages", _ => RecordingHttpHandler.Ok("""{ "id": "payment-page" }"""));
        using var notion = new NotionSyncService(db, Creds(withToken: true), handler);
        var vm = new PayDayPageViewModel(db, notion);
        await vm.LoadAsync(new System.DateTime(2026, 5, 15));

        var row = vm.UnpaidBills.Single();
        await vm.MarkPaidCommand.ExecuteAsync(row);
        Assert.NotNull(vm.PendingNotionPush);
        await vm.PendingNotionPush!;

        Assert.Single(db.Payments);
        Assert.Equal(NotionPushStatus.Ok, vm.LastNotionPushStatus);
        Assert.Empty(vm.LastNotionPushError);
        var req = Assert.Single(handler.Requests);
        Assert.Contains("payments-ds", req.Body);
        Assert.Contains("Electric", req.Body);
    }

    [Fact]
    public async Task MarkPaid_PushFails_LocalRowStillPersists_StatusFailed()
    {
        var db = SeedDb();
        var handler = new RecordingHttpHandler();
        handler.OnPost("v1/pages", _ => RecordingHttpHandler.Json(HttpStatusCode.InternalServerError, """{ "code": "boom" }"""));
        using var notion = new NotionSyncService(db, Creds(withToken: true), handler);
        var vm = new PayDayPageViewModel(db, notion);
        await vm.LoadAsync(new System.DateTime(2026, 5, 15));

        var row = vm.UnpaidBills.Single();
        await vm.MarkPaidCommand.ExecuteAsync(row);
        await vm.PendingNotionPush!;

        Assert.Single(db.Payments); // local insert was NOT rolled back
        Assert.Equal(NotionPushStatus.Failed, vm.LastNotionPushStatus);
        Assert.NotEmpty(vm.LastNotionPushError);
        Assert.Single(vm.PaidBills);
    }

    [Fact]
    public async Task MarkAllPaid_PushesEveryPayment()
    {
        var db = SeedDb();
        db.Bills.Add(new Bill { Id = "2", Name = "Phone", Type = "Bills", Payment =100, DueDay = 15, Rate = "Monthly", Active = true });
        var handler = new RecordingHttpHandler();
        handler.OnPost("v1/pages", _ => RecordingHttpHandler.Ok("""{ "id": "p" }"""));
        using var notion = new NotionSyncService(db, Creds(withToken: true), handler);
        var vm = new PayDayPageViewModel(db, notion);
        await vm.LoadAsync(new System.DateTime(2026, 5, 15));

        await vm.MarkAllPaidCommand.ExecuteAsync(null);
        await vm.PendingNotionPush!;

        Assert.Equal(2, db.Payments.Count);
        Assert.Equal(2, handler.Requests.Count(r => r.Path == "v1/pages"));
        Assert.Equal(NotionPushStatus.Ok, vm.LastNotionPushStatus);
    }

    // ------------------------------------------------------------------
    // InsightsPageViewModel — snapshots
    // ------------------------------------------------------------------

    [Fact]
    public async Task SaveSnapshot_NoNotion_StatusStaysNotConfigured()
    {
        var db = SeedDb();
        var vm = new InsightsPageViewModel(db);
        await vm.LoadAsync();

        await vm.SaveSnapshotAsync(new System.DateTime(2026, 5, 15));

        Assert.Single(db.Snapshots);
        Assert.Equal(NotionPushStatus.NotConfigured, vm.LastNotionPushStatus);
    }

    [Fact]
    public async Task SaveSnapshot_PushSucceeds_StatusOk()
    {
        var db = SeedDb();
        var handler = new RecordingHttpHandler();
        handler.OnPost("v1/pages", _ => RecordingHttpHandler.Ok("""{ "id": "snap-page" }"""));
        using var notion = new NotionSyncService(db, Creds(withToken: true), handler);
        var vm = new InsightsPageViewModel(db, notion);
        await vm.LoadAsync();

        await vm.SaveSnapshotAsync(new System.DateTime(2026, 5, 15));
        await vm.PendingNotionPush!;

        Assert.Single(db.Snapshots);
        Assert.Equal(NotionPushStatus.Ok, vm.LastNotionPushStatus);
        var req = Assert.Single(handler.Requests);
        Assert.Contains("snap-ds", req.Body);
    }

    [Fact]
    public async Task SaveSnapshot_PushFails_LocalSnapshotPersists_StatusFailed()
    {
        var db = SeedDb();
        var handler = new RecordingHttpHandler();
        handler.OnPost("v1/pages", _ => RecordingHttpHandler.Json(HttpStatusCode.BadRequest, """{ "code": "validation_error" }"""));
        using var notion = new NotionSyncService(db, Creds(withToken: true), handler);
        var vm = new InsightsPageViewModel(db, notion);
        await vm.LoadAsync();

        await vm.SaveSnapshotAsync(new System.DateTime(2026, 5, 15));
        await vm.PendingNotionPush!;

        Assert.Single(db.Snapshots);
        Assert.Equal(NotionPushStatus.Failed, vm.LastNotionPushStatus);
        Assert.NotEmpty(vm.LastNotionPushError);
    }

    // ------------------------------------------------------------------
    // AllBillsPageViewModel — bills (chunk 7b)
    // ------------------------------------------------------------------

    [Fact]
    public async Task SaveBill_NoNotionService_StatusStaysNotConfigured()
    {
        var db = SeedDb();
        var vm = new AllBillsPageViewModel(db);
        await vm.LoadAsync();

        var bill = db.Bills.Single();
        bill.Name = "Electric Co. (renamed)";
        await vm.SaveBillAsync(bill);
        if (vm.PendingNotionPush is not null) await vm.PendingNotionPush;

        Assert.Equal("Electric Co. (renamed)", db.Bills.Single().Name);
        Assert.Equal(NotionPushStatus.NotConfigured, vm.LastNotionPushStatus);
    }

    [Fact]
    public async Task SaveBill_NotionWithoutToken_StatusStaysNotConfigured_NoHttp()
    {
        var db = SeedDb();
        var handler = new RecordingHttpHandler();
        using var notion = new NotionSyncService(db, Creds(withToken: false), handler);
        var vm = new AllBillsPageViewModel(db, notion);
        await vm.LoadAsync();

        var bill = db.Bills.Single();
        bill.Active = false;
        await vm.SaveBillAsync(bill);
        if (vm.PendingNotionPush is not null) await vm.PendingNotionPush;

        Assert.False(db.Bills.Single().Active);
        Assert.Equal(NotionPushStatus.NotConfigured, vm.LastNotionPushStatus);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task SaveBill_PushSucceeds_StatusOk()
    {
        var db = SeedDb();
        // Pre-seed NotionPageId so PushBillAsync hits the fast path (PATCH only, no query).
        db.Bills.Single().NotionPageId = "page-electric";
        var handler = new RecordingHttpHandler();
        handler.OnPatch("v1/pages/page-electric", _ => RecordingHttpHandler.Ok("""{ "id": "page-electric" }"""));
        using var notion = new NotionSyncService(db, Creds(withToken: true), handler);
        var vm = new AllBillsPageViewModel(db, notion);
        await vm.LoadAsync();

        var bill = db.Bills.Single();
        bill.Payment =450;
        await vm.SaveBillAsync(bill);
        Assert.NotNull(vm.PendingNotionPush);
        await vm.PendingNotionPush!;

        Assert.Equal(450, db.Bills.Single().Payment);
        Assert.Equal(NotionPushStatus.Ok, vm.LastNotionPushStatus);
        Assert.Empty(vm.LastNotionPushError);
        var req = Assert.Single(handler.Requests);
        Assert.Equal(System.Net.Http.HttpMethod.Patch, req.Method);
        Assert.Equal("v1/pages/page-electric", req.Path);
        Assert.Contains("Electric", req.Body);
    }

    [Fact]
    public async Task SaveBill_PushFails_LocalRowStillPersists_StatusFailed()
    {
        var db = SeedDb();
        db.Bills.Single().NotionPageId = "page-electric";
        var handler = new RecordingHttpHandler();
        handler.OnPatch("v1/pages/page-electric",
            _ => RecordingHttpHandler.Json(HttpStatusCode.InternalServerError, """{ "code": "boom" }"""));
        using var notion = new NotionSyncService(db, Creds(withToken: true), handler);
        var vm = new AllBillsPageViewModel(db, notion);
        await vm.LoadAsync();

        var bill = db.Bills.Single();
        bill.Payment =999;
        await vm.SaveBillAsync(bill);
        await vm.PendingNotionPush!;

        Assert.Equal(999, db.Bills.Single().Payment); // local persist is NOT rolled back
        Assert.Equal(NotionPushStatus.Failed, vm.LastNotionPushStatus);
        Assert.NotEmpty(vm.LastNotionPushError);
    }
}
