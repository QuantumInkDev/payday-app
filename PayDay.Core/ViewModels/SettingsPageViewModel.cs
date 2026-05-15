using System;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using PayDay.Services;

namespace PayDay.ViewModels;

public enum AppTheme
{
    System = 0,
    Light = 1,
    Dark = 2,
}

/// <summary>
/// View model behind <c>SettingsPage</c>. Owns the pay-anchor date, theme
/// selection, and the JSON export/import seam. Theme + pay anchor persist
/// to the <c>Settings</c> table; export/import use <see cref="BackupSerializer"/>
/// and <c>IDatabaseService.ReplaceAllDataAsync</c>.
/// </summary>
public sealed partial class SettingsPageViewModel : ObservableObject
{
    public const string ThemeKey = "Theme";

    private readonly IDatabaseService _db;
    private readonly PayPeriodService _periodService;

    public SettingsPageViewModel(IDatabaseService db)
    {
        _db = db;
        _periodService = new PayPeriodService(db);
    }

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private DateTimeOffset _payAnchorDate = new(2026, 3, 20, 0, 0, 0, TimeSpan.Zero);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedThemeIndex))]
    private AppTheme _selectedTheme = AppTheme.System;

    /// <summary>Int proxy for RadioButtons.SelectedIndex two-way binding.</summary>
    public int SelectedThemeIndex
    {
        get => (int)SelectedTheme;
        set
        {
            if (value < 0) return;
            var next = (AppTheme)value;
            if (next != SelectedTheme) SelectedTheme = next;
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _statusMessage = string.Empty;

    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var anchor = await _periodService.GetPayAnchorAsync().ConfigureAwait(true);
            // GetPayAnchorAsync returns a DateTime with Kind=Local — strip the Kind so
            // DateTimeOffset(date, TimeSpan.Zero) doesn't complain about the offset
            // mismatch on machines where the local offset isn't UTC.
            PayAnchorDate = new DateTimeOffset(anchor.Year, anchor.Month, anchor.Day, 0, 0, 0, TimeSpan.Zero);

            var themeRaw = await _db.GetSettingAsync(ThemeKey).ConfigureAwait(true);
            SelectedTheme = ParseTheme(themeRaw);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task SavePayAnchorAsync()
    {
        await _periodService.SetPayAnchorAsync(PayAnchorDate.Date).ConfigureAwait(true);
        StatusMessage = $"Pay anchor saved: {PayAnchorDate:MMM d, yyyy}";
    }

    public async Task SaveThemeAsync()
    {
        await _db.SetSettingAsync(ThemeKey, SelectedTheme.ToString()).ConfigureAwait(true);
        StatusMessage = $"Theme set to {SelectedTheme}";
    }

    /// <summary>Serializes the entire DB to JSON. Caller writes the string to whatever destination it wants.</summary>
    public async Task<string> ExportAsync()
    {
        var bills = await _db.GetAllBillsAsync().ConfigureAwait(true);
        var payments = await _db.GetAllPaymentsAsync().ConfigureAwait(true);
        var snapshots = await _db.GetAllSnapshotsAsync().ConfigureAwait(true);
        var settings = await _db.GetAllSettingsAsync().ConfigureAwait(true);
        return BackupSerializer.ToJson(bills, payments, snapshots, settings);
    }

    /// <summary>Parses <paramref name="json"/> and atomically replaces the local DB. Throws on invalid input.</summary>
    public async Task ImportAsync(string json)
    {
        var file = BackupSerializer.FromJson(json);
        await _db.ReplaceAllDataAsync(file.Bills, file.Payments, file.Snapshots, file.Settings).ConfigureAwait(true);
        await LoadAsync().ConfigureAwait(true);
        StatusMessage = $"Imported {file.Bills.Count} bills, {file.Payments.Count} payments, {file.Snapshots.Count} snapshots.";
    }

    public string FormatExportFileName(DateTime? now = null)
    {
        var stamp = (now ?? DateTime.Now).ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return $"payday-backup-{stamp}";
    }

    private static AppTheme ParseTheme(string? raw)
        => Enum.TryParse<AppTheme>(raw, ignoreCase: true, out var t) ? t : AppTheme.System;
}
