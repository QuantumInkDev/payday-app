using System.Net;
using System.Threading.Tasks;
using PayDay.Services;
using PayDay.ViewModels;

namespace PayDay.Tests;

/// <summary>
/// 5d Settings-page Notion section tests. Exercises the VM seam — the
/// XAML side is verified via the manual smoke test at sprint close.
/// </summary>
public class SettingsPageNotionTests
{
    private static (FakeDatabaseService db, InMemoryCredentialStore creds, RecordingHttpHandler handler, NotionSyncService notion)
        Setup(bool seedToken = false)
    {
        var db = new FakeDatabaseService();
        db.Settings[NotionSyncService.BillsDataSourceSetting] = "bills-ds";
        db.Settings[NotionSyncService.PaymentsDataSourceSetting] = "payments-ds";
        db.Settings[NotionSyncService.SnapshotsDataSourceSetting] = "snap-ds";
        var creds = new InMemoryCredentialStore();
        if (seedToken) creds.Set(NotionSyncService.TokenKey, "test-token");
        var handler = new RecordingHttpHandler();
        var notion = new NotionSyncService(db, creds, handler);
        return (db, creds, handler, notion);
    }

    [Fact]
    public async Task LoadAsync_NoNotionService_NotionSectionDisabled()
    {
        var db = new FakeDatabaseService();
        var vm = new SettingsPageViewModel(db);
        await vm.LoadAsync();

        Assert.False(vm.NotionAvailable);
        Assert.False(vm.NotionTokenSet);
        Assert.Equal(NotionPushStatus.NotConfigured, vm.NotionStatus);
    }

    [Fact]
    public async Task LoadAsync_TokenAbsent_PromptToPaste()
    {
        var (db, _, _, notion) = Setup(seedToken: false);
        var vm = new SettingsPageViewModel(db, notion);
        await vm.LoadAsync();

        Assert.True(vm.NotionAvailable);
        Assert.False(vm.NotionTokenSet);
        Assert.False(vm.NotionSectionEnabled);
        Assert.Equal(NotionPushStatus.NotConfigured, vm.NotionStatus);
        Assert.Contains("integration token", vm.NotionStatusLabel);
    }

    [Fact]
    public async Task LoadAsync_TokenPresent_SectionEnabled()
    {
        var (db, _, _, notion) = Setup(seedToken: true);
        var vm = new SettingsPageViewModel(db, notion);
        await vm.LoadAsync();

        Assert.True(vm.NotionTokenSet);
        Assert.True(vm.NotionSectionEnabled);
        Assert.Equal(NotionPushStatus.Ok, vm.NotionStatus);
    }

    [Fact]
    public async Task SaveTokenAsync_SavesCredentialAndEnablesSection()
    {
        var (db, creds, _, notion) = Setup();
        var vm = new SettingsPageViewModel(db, notion);
        await vm.LoadAsync();

        await vm.SaveTokenAsync("secret_abc123");

        Assert.True(vm.NotionTokenSet);
        Assert.Equal("secret_abc123", creds.Get(NotionSyncService.TokenKey));
        Assert.Equal(NotionPushStatus.Ok, vm.NotionStatus);
    }

    [Fact]
    public async Task SaveTokenAsync_BlankInput_NoOp()
    {
        var (db, creds, _, notion) = Setup();
        var vm = new SettingsPageViewModel(db, notion);
        await vm.LoadAsync();

        await vm.SaveTokenAsync("   ");

        Assert.False(vm.NotionTokenSet);
        Assert.Null(creds.Get(NotionSyncService.TokenKey));
    }

    [Fact]
    public async Task ClearTokenAsync_RemovesCredentialAndDisablesSection()
    {
        var (db, creds, _, notion) = Setup(seedToken: true);
        var vm = new SettingsPageViewModel(db, notion);
        await vm.LoadAsync();

        await vm.ClearTokenAsync();

        Assert.False(vm.NotionTokenSet);
        Assert.False(vm.NotionSectionEnabled);
        Assert.Null(creds.Get(NotionSyncService.TokenKey));
    }

    [Fact]
    public async Task TestConnectionAsync_Success_StatusOk()
    {
        var (db, _, handler, notion) = Setup(seedToken: true);
        handler.OnGet("v1/users/me", _ => RecordingHttpHandler.Ok("""{ "id": "u1" }"""));
        var vm = new SettingsPageViewModel(db, notion);
        await vm.LoadAsync();

        await vm.TestConnectionAsync();

        Assert.Equal(NotionPushStatus.Ok, vm.NotionStatus);
        Assert.Contains("verified", vm.NotionStatusLabel);
        Assert.False(vm.IsTesting);
    }

    [Fact]
    public async Task TestConnectionAsync_Failure_StatusFailed()
    {
        var (db, _, handler, notion) = Setup(seedToken: true);
        handler.OnGet("v1/users/me", _ => RecordingHttpHandler.Json(HttpStatusCode.Unauthorized, """{ "code": "unauthorized" }"""));
        var vm = new SettingsPageViewModel(db, notion);
        await vm.LoadAsync();

        await vm.TestConnectionAsync();

        Assert.Equal(NotionPushStatus.Failed, vm.NotionStatus);
        Assert.Contains("failed", vm.NotionStatusLabel, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SyncNowAsync_Success_UpdatesStatusAndLastSyncedLabel()
    {
        var (db, _, handler, notion) = Setup(seedToken: true);
        handler.OnPost("v1/data_sources/bills-ds/query", _ => RecordingHttpHandler.Ok("""{ "object": "list", "results": [], "has_more": false, "next_cursor": null }"""));
        var vm = new SettingsPageViewModel(db, notion);
        await vm.LoadAsync();
        Assert.Equal("Never synced", vm.LastSyncedLabel);

        await vm.SyncNowAsync();

        Assert.Equal(NotionPushStatus.Ok, vm.NotionStatus);
        Assert.False(vm.IsSyncing);
        Assert.StartsWith("Last synced", vm.LastSyncedLabel);
        Assert.Contains("Synced", vm.NotionStatusLabel);
    }

    [Fact]
    public async Task SyncNowAsync_Exception_StatusFailed()
    {
        var (db, _, handler, notion) = Setup(seedToken: true);
        handler.OnPost("v1/data_sources/bills-ds/query", _ => RecordingHttpHandler.Json(HttpStatusCode.InternalServerError, """{ "code": "boom" }"""));
        var vm = new SettingsPageViewModel(db, notion);
        await vm.LoadAsync();

        await vm.SyncNowAsync();

        Assert.Equal(NotionPushStatus.Failed, vm.NotionStatus);
        Assert.Contains("failed", vm.NotionStatusLabel, System.StringComparison.OrdinalIgnoreCase);
    }
}
