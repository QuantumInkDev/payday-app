using System;
using System.Collections.ObjectModel;
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
/// selection, JSON export/import, and the Notion-sync configuration block
/// (token save/clear, test connection, manual sync now, last-synced label).
/// </summary>
public sealed partial class SettingsPageViewModel : ObservableObject
{
    public const string ThemeKey = "Theme";

    private readonly IDatabaseService _db;
    private readonly PayPeriodService _periodService;
    private readonly NotionSyncService? _notion;

    public SettingsPageViewModel(IDatabaseService db, NotionSyncService? notion = null)
    {
        _db = db;
        _periodService = new PayPeriodService(db);
        _notion = notion;
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

    // ------------------------------------------------------------------
    // Notion sync (5d)
    // ------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotionSectionEnabled))]
    private bool _notionTokenSet;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotionBusy))]
    private bool _isTesting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotionBusy))]
    private bool _isSyncing;

    [ObservableProperty]
    private NotionPushStatus _notionStatus = NotionPushStatus.NotConfigured;

    [ObservableProperty]
    private string _notionStatusLabel = "Paste an integration token to enable sync.";

    [ObservableProperty]
    private string _lastSyncedLabel = "Never synced";

    /// <summary>True only if the app actually has a Notion service wired in (production has one; tests can omit).</summary>
    public bool NotionAvailable => _notion is not null;

    /// <summary>The Notion card's interactive controls (test/sync buttons) only light up once a token is saved.</summary>
    public bool NotionSectionEnabled => NotionTokenSet;

    public bool IsNotionBusy => IsTesting || IsSyncing;

    // ------------------------------------------------------------------

    /// <summary>Per-known-type pill color rows bound to the Settings type-colors card.</summary>
    public ObservableCollection<TypeColorEntry> TypeColorEntries { get; } = new();

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

            LoadTypeColors();
            await LoadNotionStateAsync().ConfigureAwait(true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void LoadTypeColors()
    {
        TypeColorEntries.Clear();
        foreach (var type in TypeColorService.Defaults.Keys)
        {
            TypeColorEntries.Add(new TypeColorEntry(type, TypeColorService.GetHex(type)));
        }
    }

    public async Task SetTypeColorAsync(string type, string hex)
    {
        await TypeColorService.SetHexAsync(_db, type, hex).ConfigureAwait(true);
        // Re-read the service in case the input was invalid (service no-ops on bad hex).
        foreach (var entry in TypeColorEntries)
        {
            if (string.Equals(entry.Type, type, StringComparison.OrdinalIgnoreCase))
            {
                entry.Hex = TypeColorService.GetHex(type);
                break;
            }
        }
    }

    public async Task ResetTypeColorAsync(string type)
    {
        await TypeColorService.ResetAsync(_db, type).ConfigureAwait(true);
        foreach (var entry in TypeColorEntries)
        {
            if (string.Equals(entry.Type, type, StringComparison.OrdinalIgnoreCase))
            {
                entry.Hex = TypeColorService.GetHex(type);
                break;
            }
        }
    }

    private async Task LoadNotionStateAsync()
    {
        if (_notion is null)
        {
            NotionTokenSet = false;
            NotionStatus = NotionPushStatus.NotConfigured;
            NotionStatusLabel = "Notion sync unavailable in this build.";
            return;
        }
        NotionTokenSet = _notion.HasToken();
        var last = await _notion.GetLastSyncedAsync().ConfigureAwait(true);
        LastSyncedLabel = last is null
            ? "Never synced"
            : $"Last synced {last.Value.ToLocalTime():MMM d, yyyy 'at' h:mm tt}";
        if (!NotionTokenSet)
        {
            NotionStatus = NotionPushStatus.NotConfigured;
            NotionStatusLabel = "Paste an integration token to enable sync.";
        }
        else
        {
            NotionStatus = NotionPushStatus.Ok;
            NotionStatusLabel = last is null
                ? "Token saved. Run 'Test connection' to verify."
                : "Connected.";
        }
    }

    public Task SaveTokenAsync(string token)
    {
        if (_notion is null) return Task.CompletedTask;
        if (string.IsNullOrWhiteSpace(token))
        {
            StatusMessage = "Paste a token first.";
            return Task.CompletedTask;
        }
        _notion.SaveToken(token.Trim());
        NotionTokenSet = true;
        NotionStatus = NotionPushStatus.Ok;
        NotionStatusLabel = "Token saved. Run 'Test connection' to verify.";
        StatusMessage = "Notion token saved.";
        return Task.CompletedTask;
    }

    public Task ClearTokenAsync()
    {
        if (_notion is null) return Task.CompletedTask;
        _notion.DeleteToken();
        NotionTokenSet = false;
        NotionStatus = NotionPushStatus.NotConfigured;
        NotionStatusLabel = "Paste an integration token to enable sync.";
        StatusMessage = "Notion token cleared.";
        return Task.CompletedTask;
    }

    public async Task TestConnectionAsync()
    {
        if (_notion is null) return;
        IsTesting = true;
        try
        {
            var ok = await _notion.TestConnectionAsync().ConfigureAwait(true);
            NotionStatus = ok ? NotionPushStatus.Ok : NotionPushStatus.Failed;
            NotionStatusLabel = ok
                ? "Connected — token verified."
                : "Connection failed — check the token and try again.";
            StatusMessage = ok ? "Notion connection OK." : "Notion connection failed.";
        }
        finally
        {
            IsTesting = false;
        }
    }

    public async Task SyncNowAsync()
    {
        if (_notion is null) return;
        IsSyncing = true;
        try
        {
            var result = await _notion.SyncBillsAsync().ConfigureAwait(true);
            await LoadNotionStateAsync().ConfigureAwait(true); // refresh last-synced label
            if (result.HasErrors)
            {
                NotionStatus = NotionPushStatus.Failed;
                NotionStatusLabel = $"Sync finished with {result.Errors.Count} error(s). {result.Errors[0]}";
            }
            else
            {
                NotionStatus = NotionPushStatus.Ok;
                NotionStatusLabel = $"Synced — created {result.Created}, updated {result.Updated}, pulled {result.Pulled}.";
            }
            StatusMessage = NotionStatusLabel;
        }
        catch (Exception ex)
        {
            NotionStatus = NotionPushStatus.Failed;
            NotionStatusLabel = $"Sync failed: {ex.Message}";
            StatusMessage = NotionStatusLabel;
        }
        finally
        {
            IsSyncing = false;
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

/// <summary>One row in the Settings type-colors card. Observable so the swatch updates when the color changes.</summary>
public sealed partial class TypeColorEntry : ObservableObject
{
    public string Type { get; }

    [ObservableProperty]
    private string _hex;

    public TypeColorEntry(string type, string hex)
    {
        Type = type;
        _hex = hex;
    }
}
