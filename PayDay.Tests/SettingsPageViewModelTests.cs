using System;
using System.Threading.Tasks;
using PayDay.Models;
using PayDay.Services;
using PayDay.ViewModels;

namespace PayDay.Tests;

public class SettingsPageViewModelTests
{
    [Fact]
    public async Task LoadAsync_ReadsPayAnchorFromSettings()
    {
        var db = new FakeDatabaseService();
        db.Settings["PayAnchor"] = "2026-04-03";

        var vm = new SettingsPageViewModel(db);
        await vm.LoadAsync();

        Assert.Equal(new DateTime(2026, 4, 3), vm.PayAnchorDate.Date);
    }

    [Fact]
    public async Task LoadAsync_MissingPayAnchor_FallsBackToDefault()
    {
        var db = new FakeDatabaseService();
        var vm = new SettingsPageViewModel(db);
        await vm.LoadAsync();
        // PayPeriodService default is 2026-03-20.
        Assert.Equal(new DateTime(2026, 3, 20), vm.PayAnchorDate.Date);
    }

    [Fact]
    public async Task LoadAsync_ReadsThemeFromSettings()
    {
        var db = new FakeDatabaseService();
        db.Settings["Theme"] = "Dark";
        var vm = new SettingsPageViewModel(db);
        await vm.LoadAsync();
        Assert.Equal(AppTheme.Dark, vm.SelectedTheme);
    }

    [Fact]
    public async Task LoadAsync_UnknownTheme_FallsBackToSystem()
    {
        var db = new FakeDatabaseService();
        db.Settings["Theme"] = "Tron";
        var vm = new SettingsPageViewModel(db);
        await vm.LoadAsync();
        Assert.Equal(AppTheme.System, vm.SelectedTheme);
    }

    [Fact]
    public async Task SavePayAnchorAsync_PersistsAsIsoDate()
    {
        var db = new FakeDatabaseService();
        var vm = new SettingsPageViewModel(db);
        vm.PayAnchorDate = new DateTimeOffset(2026, 6, 5, 0, 0, 0, TimeSpan.Zero);

        await vm.SavePayAnchorAsync();

        Assert.Equal("2026-06-05", db.Settings["PayAnchor"]);
        Assert.Contains("Pay anchor saved", vm.StatusMessage);
    }

    [Fact]
    public async Task SaveThemeAsync_PersistsThemeName()
    {
        var db = new FakeDatabaseService();
        var vm = new SettingsPageViewModel(db);
        vm.SelectedTheme = AppTheme.Light;

        await vm.SaveThemeAsync();

        Assert.Equal("Light", db.Settings["Theme"]);
    }

    [Fact]
    public async Task ExportAsync_ReturnsRoundTrippableJson()
    {
        var db = new FakeDatabaseService();
        db.Bills.Add(new Bill { Id = "1", Name = "Test", Type = "Cards", Cost = 50, Active = true });
        db.Settings["PayAnchor"] = "2026-03-20";

        var vm = new SettingsPageViewModel(db);
        var json = await vm.ExportAsync();
        var parsed = BackupSerializer.FromJson(json);

        Assert.Single(parsed.Bills);
        Assert.Equal("Test", parsed.Bills[0].Name);
        Assert.Equal("2026-03-20", parsed.Settings["PayAnchor"]);
    }

    [Fact]
    public async Task ImportAsync_ReplacesAllData()
    {
        var db = new FakeDatabaseService();
        db.Bills.Add(new Bill { Id = "old", Name = "Old", Type = "Cards" });
        db.Payments.Add(new Payment { Id = 1, BillId = "old", PeriodKey = "k", AmountPaid = 10 });
        db.Settings["PayAnchor"] = "2020-01-01";

        var vm = new SettingsPageViewModel(db);

        var newJson = BackupSerializer.ToJson(
            new[] { new Bill { Id = "new", Name = "New", Type = "Bills", Active = true } },
            Array.Empty<Payment>(),
            Array.Empty<Snapshot>(),
            new System.Collections.Generic.Dictionary<string, string?> { ["PayAnchor"] = "2026-03-20" });

        await vm.ImportAsync(newJson);

        Assert.Single(db.Bills);
        Assert.Equal("new", db.Bills[0].Id);
        Assert.Empty(db.Payments);
        Assert.Equal("2026-03-20", db.Settings["PayAnchor"]);
        Assert.Contains("Imported 1 bills", vm.StatusMessage);
    }

    [Fact]
    public async Task ImportAsync_BadJson_ThrowsBeforeMutating()
    {
        var db = new FakeDatabaseService();
        db.Bills.Add(new Bill { Id = "old", Name = "Old", Type = "Cards" });
        var vm = new SettingsPageViewModel(db);

        await Assert.ThrowsAnyAsync<Exception>(() => vm.ImportAsync("{ this is not json"));

        // The original bill is still there — the failure happened before ReplaceAllDataAsync ran.
        Assert.Single(db.Bills);
        Assert.Equal("old", db.Bills[0].Id);
    }

    [Fact]
    public void FormatExportFileName_UsesIsoStamp()
    {
        var vm = new SettingsPageViewModel(new FakeDatabaseService());
        var name = vm.FormatExportFileName(new DateTime(2026, 5, 15, 14, 30, 5));
        Assert.Equal("payday-backup-20260515-143005", name);
    }
}
