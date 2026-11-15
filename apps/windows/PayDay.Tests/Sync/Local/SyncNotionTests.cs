using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using PayDay.Models;
using PayDay.Services;
using PayDay.Sync.Clock;
using PayDay.Sync.Ids;
using PayDay.Sync.Local;
using PayDay.Sync.Merge;
using PayDay.Sync.Model;

namespace PayDay.Tests.Sync.Local;

/// <summary>
/// The Notion connector with sync on, against <see cref="FakeNotion"/>: migrated bills keep their
/// pages, Notion values go through setRemaining with the echo rule, and only the owner device runs
/// the connector (<c>sync-protocol.md</c> 5.3, 12.5). All data is fictional.
/// </summary>
public sealed class SyncNotionTests : IDisposable
{
    private const string OtherDevice = "0b0b0b0b0b0b0b0b";

    /// <summary>Notion pages were last edited after the legacy UpdatedAt (2025-05-01) and before sync was turned on (2025-05-22).</summary>
    private static readonly DateTimeOffset NotionEdited = new(2025, 5, 10, 0, 0, 0, TimeSpan.Zero);

    private readonly SyncTestEnvironment _env = new();
    private readonly FakeNotion _notion;
    private readonly InMemoryCredentialStore _credentials = new();
    private readonly NotionSyncService _service;

    public SyncNotionTests()
    {
        _notion = new FakeNotion(() => _env.Time.GetUtcNow());
        _credentials.Set(NotionSyncService.TokenKey, "secret_fictional_token");
        _service = new NotionSyncService(_env.Routed, _credentials, _notion);
    }

    public void Dispose()
    {
        _service.Dispose();
        _env.Dispose();
    }

    private static string B1 => LegacyMapping.MapBillId("1");

    private static string B13 => LegacyMapping.MapBillId("13");

    private static string B27 => LegacyMapping.MapBillId("27");

    /// <summary>Today's data with Notion pages for bills "1", "13" and "27" (Bill ID = the legacy ID), then sync turned on.</summary>
    private async Task OnWithPagesAsync()
    {
        await _env.InitializeAsync();
        _env.InsertLegacyData();
        foreach (var bill in await _env.Db.GetAllBillsAsync())
        {
            var pageId = bill.Id switch
            {
                "1" => "page-1",
                "13" => "page-13",
                "27" => "fictional-notion-page-27",
                _ => null,
            };
            if (pageId is not null) _notion.AddBillPage(pageId, bill, NotionEdited);
        }
        await _env.TurnOnAsync();
    }

    private int Pending() => _env.Sync.Replica!.Read((_, m) => m.PendingRecords.Count);

    private ChangeRecord[] PendingSince(int count) => _env.Sync.Replica!.Read((_, m) => m.PendingRecords.Skip(count).ToArray());

    private void MergeRemote(string entity, string id, string field, SyncValue value)
        => _env.Sync.Replica!.MergeRecords(new[] { new ChangeRecord(entity, id, field, value, Hlc.Encode(_env.Time.UnixMs + 60_000, 0, OtherDevice)) });

    [Fact]
    public async Task SyncNow_AfterTheMigration_FindsEveryMigratedBillsPage_AndCreatesNoDuplicate()
    {
        await OnWithPagesAsync();
        await _service.ConnectAsync("secret_fictional_token");
        var before = Pending();

        _env.Time.Advance(1000);
        var result = await _service.SyncBillsAsync();

        Assert.False(result.HasErrors, string.Join("; ", result.Errors));
        // Three bills had no page and get one; the three with pages are found, not duplicated.
        Assert.Equal(3, result.Created);
        Assert.Equal(6, _notion.BillPages.Count());
        Assert.Single(_notion.BillPages, p => FakeNotion.Text(p, "Name") == "Woodgrove Rewards");
        Assert.Single(_notion.BillPages, p => FakeNotion.Text(p, "Name") == "Electric");
        var bills = (await _env.Db.GetAllBillsAsync()).ToDictionary(b => b.Id);
        Assert.Equal("page-1", bills[B1].NotionPageId);
        Assert.Equal("page-13", bills[B13].NotionPageId);
        Assert.Equal("fictional-notion-page-27", bills[B27].NotionPageId);

        // A second Sync Now changes nothing in Notion.
        var pages = _notion.BillPages.Count();
        _env.Time.Advance(1000);
        var second = await _service.SyncBillsAsync();
        Assert.Equal(0, second.Created);
        Assert.Equal(pages, _notion.BillPages.Count());
        Assert.DoesNotContain(PendingSince(before), r => r.Field is not "notionPageId" && r.Entity == SyncEntities.Bill && (r.Id == B1 || r.Id == B13 || r.Id == B27));
    }

    [Fact]
    public async Task TheMigration_DoesNotMakeEveryBillLookNewerThanNotion()
    {
        await OnWithPagesAsync();
        // The device that ran the connector before sync keeps running it (as when it creates the vault).
        _env.Sync.ContinueNotionAsCreator();
        Assert.Equal(NotionRole.Owner, _env.Routed.GetNotionState().Role);

        // Edits made in Notion since the last Notion sync (2025-05-10, after today's UpdatedAt of 2025-05-01).
        var page13 = _notion.BillPages.Single(p => p.Id == "page-13");
        _notion.EditInNotion(page13, "Payment", JsonNode.Parse("""{"number":125}""")!);
        page13.LastEdited = NotionEdited;
        var page1 = _notion.BillPages.Single(p => p.Id == "page-1");
        _notion.EditInNotion(page1, "Remaining", JsonNode.Parse("""{"number":1100}""")!);
        page1.LastEdited = NotionEdited;
        var before = Pending();

        _env.Time.Advance(1000);
        var result = await _service.SyncBillsAsync();

        // Sync was turned on after those edits, but the bills keep today's UpdatedAt: Notion's values
        // come in and nothing is pushed over them.
        Assert.Equal(0, result.Updated);
        Assert.Equal(125, (await _env.Db.GetAllBillsAsync()).Single(b => b.Id == B13).Payment);
        Assert.Equal(1100, (await _env.Db.GetAllBillsAsync()).Single(b => b.Id == B1).Remaining);
        Assert.Equal(125, FakeNotion.Number(page13, "Payment"));
        var written = PendingSince(before).Where(r => r.Id == B13).Select(r => r.Field).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "notionPageId", "payment" }, written);
        Assert.Equal(SyncValue.Number(1100), _env.Value(SyncEntities.Bill, B1, "remainingCheckpoint"));

        // Once edited after the migration, UpdatedAt follows the edit's HLC.
        Assert.Equal("2025-05-22 12:00:02", (await _env.Db.GetAllBillsAsync()).Single(b => b.Id == B13).UpdatedAt);
        Assert.Equal("2025-05-01 09:00:00", (await _env.Db.GetAllBillsAsync()).Single(b => b.Id == B27).UpdatedAt);
    }

    [Fact]
    public async Task ANewOwner_PushesEveryBillBeforeItsFirstPull()
    {
        await OnWithPagesAsync();
        await _service.ConnectAsync("secret_fictional_token");
        Assert.Equal(NotionRole.OwnerMustPushFirst, _env.Routed.GetNotionState().Role);
        var page = _notion.BillPages.Single(p => p.Id == "page-13");
        _notion.EditInNotion(page, "Payment", JsonNode.Parse("""{"number":125}""")!);

        _env.Time.Advance(1000);
        var result = await _service.SyncBillsAsync();

        Assert.Equal(3, result.Updated);
        Assert.Equal(0, result.Pulled);
        Assert.Equal(120, FakeNotion.Number(page, "Payment"));
        Assert.Equal(NotionRole.Owner, _env.Routed.GetNotionState().Role);
        Assert.Equal(1200, _env.Sync.GetNotionLastPushed(B1));
    }

    [Fact]
    public async Task ThePushAfterALocalEdit_WritesTheBillsNewIdToNotion()
    {
        await OnWithPagesAsync();
        await _service.ConnectAsync("secret_fictional_token");
        _env.Time.Advance(1000);
        await _service.SyncBillsAsync();
        Assert.Equal(B1, FakeNotion.Text(_notion.BillPages.Single(p => p.Id == "page-1"), "Bill ID"));

        // A later local edit pushes with the new ID too.
        _env.Time.Advance(120_000);
        var bill = (await _env.Routed.GetAllBillsAsync()).Single(b => b.Id == B1);
        bill.Name = "Woodgrove Rewards Plus";
        await _env.Routed.UpsertBillAsync(bill);
        _env.Time.Advance(1000);
        bill = (await _env.Routed.GetAllBillsAsync()).Single(b => b.Id == B1);
        await _service.PushBillAsync(bill);
        var page = _notion.BillPages.Single(p => p.Id == "page-1");
        Assert.Equal("Woodgrove Rewards Plus", FakeNotion.Text(page, "Name"));
        Assert.Equal(B1, FakeNotion.Text(page, "Bill ID"));
    }

    [Fact]
    public async Task ANotionValueEqualToTheLastPush_IsAnEcho_AndDoesNotAbsorbAnotherDevicesPayment()
    {
        await OnWithPagesAsync();
        await _service.ConnectAsync("secret_fictional_token");
        _env.Time.Advance(1000);
        await _service.SyncBillsAsync(); // pushes Remaining 1200 for bill 1; last pushed = 1200
        Assert.Equal(1200, _env.Sync.GetNotionLastPushed(B1));

        // Another device marks a payment of 55: Remaining is 1145 here, Notion still shows 1200.
        foreach (var (field, value) in new (string, SyncValue)[]
        {
            ("amountPaid", SyncValue.Number(55)), ("billId", SyncValue.String(B1)), ("deleted", SyncValue.False),
            ("kind", SyncValue.String(PaymentKinds.Regular)), ("paidAt", SyncValue.Null), ("periodKey", SyncValue.String(SyncTestEnvironment.CurrentPeriod)),
        })
        {
            MergeRemote(SyncEntities.Payment, SyncIds.RegularPaymentId(B1, SyncTestEnvironment.CurrentPeriod), field, value);
        }
        Assert.Equal(1145, (await _env.Db.GetAllBillsAsync()).Single(b => b.Id == B1).Remaining);

        // Notion's page was edited (another column) after our push, so the connector pulls.
        _env.Time.Advance(60_000);
        var page = _notion.BillPages.Single(p => p.Id == "page-1");
        _notion.EditInNotion(page, "Notes", JsonNode.Parse("""{"rich_text":[{"type":"text","text":{"content":"Statement due"}}]}""")!);
        var before = Pending();
        _env.Time.Advance(1000);
        await _service.SyncBillsAsync();

        // Remaining 1200 is the connector's own push: no checkpoint, the payment still counts.
        Assert.DoesNotContain(PendingSince(before), r => r.Field == "remainingCheckpoint");
        Assert.Equal(1145, (await _env.Db.GetAllBillsAsync()).Single(b => b.Id == B1).Remaining);

        // A real statement figure typed in Notion goes through setRemaining (source notion).
        _env.Time.Advance(60_000);
        _notion.EditInNotion(page, "Remaining", JsonNode.Parse("""{"number":1000}""")!);
        _env.Time.Advance(1000);
        await _service.SyncBillsAsync();
        Assert.Equal(1000, (await _env.Db.GetAllBillsAsync()).Single(b => b.Id == B1).Remaining);
        Assert.Equal(SyncValue.Number(1000), _env.Value(SyncEntities.Bill, B1, "remainingCheckpoint"));
    }

    [Fact]
    public async Task OnlyTheOwnerDeviceRunsTheConnector()
    {
        await OnWithPagesAsync();
        MergeRemote(SyncEntities.Device, OtherDevice, "name", SyncValue.String("Windows Laptop"));
        MergeRemote(SyncEntities.Device, OtherDevice, "deleted", SyncValue.False);
        MergeRemote(SyncEntities.Setting, SyncSettingKeys.NotionOwnerDeviceId, SyncEntities.ValueField, SyncValue.String(OtherDevice));

        var state = _service.GetConnectorState();
        Assert.Equal(NotionRole.OtherDevice, state.Role);
        Assert.Equal("Windows Laptop", state.OwnerDeviceName);

        var result = await _service.SyncBillsAsync();
        Assert.Contains("connected on Windows Laptop", Assert.Single(result.Errors), StringComparison.Ordinal);
        var bill = (await _env.Routed.GetAllBillsAsync()).First();
        await _service.PushBillAsync(bill);
        await _service.PushPaymentAsync(new Payment { BillId = bill.Id, PeriodKey = SyncTestEnvironment.CurrentPeriod, AmountPaid = 1 }, bill.Name);
        await _service.PushSnapshotAsync(new Snapshot { SnapshotDate = "2025-05-22", TotalRemaining = 1 });
        Assert.Empty(_notion.Calls);

        // Connecting Notion here takes the connector over: it pushes every bill before its first pull.
        await _service.ConnectAsync("secret_fictional_token");
        Assert.Equal(_env.Sync.Replica!.DeviceId, _env.Value(SyncEntities.Setting, SyncSettingKeys.NotionOwnerDeviceId, SyncEntities.ValueField)!.Value.StringValue);
        Assert.Equal(NotionRole.OwnerMustPushFirst, _service.GetConnectorState().Role);
        _env.Time.Advance(1000);
        var first = await _service.SyncBillsAsync();
        Assert.Equal(3, first.Updated); // pages newer than the bills, but the new owner pushes first
        Assert.Equal(0, first.Pulled);
        Assert.Equal(NotionRole.Owner, _service.GetConnectorState().Role);

        // When ownership moves to another device again, this one stops.
        MergeRemote(SyncEntities.Setting, SyncSettingKeys.NotionOwnerDeviceId, SyncEntities.ValueField, SyncValue.String(OtherDevice));
        var calls = _notion.Calls.Count;
        await _service.SyncBillsAsync();
        Assert.Equal(calls, _notion.Calls.Count);
    }

    [Fact]
    public async Task AVaultWithNoOwner_IsClaimedByTheFirstDeviceThatUsesNotion()
    {
        await OnWithPagesAsync();
        Assert.Equal(NotionRole.NoOwner, _service.GetConnectorState().Role);
        _env.Time.Advance(1000);
        await _service.SyncBillsAsync();
        Assert.Equal(NotionRole.Owner, _service.GetConnectorState().Role);
    }
}
