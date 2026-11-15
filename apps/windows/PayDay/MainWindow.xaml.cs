using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PayDay.Pages;
using PayDay.Services;
using PayDay.ViewModels;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace PayDay;

public sealed partial class MainWindow : Window
{
    private bool _restorePromptChecked;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");

        _ = ApplyPersistedThemeAsync();
        _ = RefreshLastSyncedLabelAsync();
        Activated += MainWindow_Activated;
        Closed += (_, _) => App.StopSyncLoop();

        _syncReloadTimer = DispatcherQueue.CreateTimer();
        _syncReloadTimer.Interval = System.TimeSpan.FromMilliseconds(400);
        _syncReloadTimer.IsRepeating = false;
        _syncReloadTimer.Tick += async (_, _) => await ReloadAfterSyncAsync();
    }

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _syncReloadTimer;
    private bool _syncReloadSettings;

    /// <summary>
    /// Another device's changes were merged (called on the UI thread for every merge): reload the
    /// type colours and the current page once things settle, instead of once per merged file.
    /// </summary>
    public void ScheduleSyncReload(bool settingsChanged)
    {
        _syncReloadSettings |= settingsChanged;
        _syncReloadTimer.Stop();
        _syncReloadTimer.Start();
    }

    private async System.Threading.Tasks.Task ReloadAfterSyncAsync()
    {
        var settings = _syncReloadSettings;
        _syncReloadSettings = false;
        try
        {
            if (settings) await TypeColorService.LoadAsync(App.Database);
            if (NavFrame.Content is ISyncReloadable page) await page.ReloadAfterSyncAsync(settings);
        }
        catch (System.Exception)
        {
            // The next merge or navigation reloads again.
        }
    }

    private async void SyncNowButton_Click(object sender, RoutedEventArgs e)
    {
        SyncProgress.IsActive = true;
        SyncProgress.Visibility = Visibility.Visible;
        SyncNowButton.IsEnabled = false;
        try
        {
            if (!App.Notion.HasToken())
            {
                await ShowSyncErrorAsync("No Notion token saved. Add one in Settings first.");
                return;
            }
            // With sync on, only the connector's owner runs it; say why instead of doing nothing.
            var connector = await App.Notion.GetConnectorStateAsync();
            if (!connector.MayRun)
            {
                var refused = await App.Notion.SyncBillsAsync();
                await ShowSyncErrorAsync(refused.Errors.Count > 0 ? refused.Errors[0] : "Notion doesn't run on this PC.");
                return;
            }
            await App.Notion.SyncBillsAsync();
            await RefreshLastSyncedLabelAsync();
        }
        catch (System.Exception ex)
        {
            await ShowSyncErrorAsync(ex.Message);
        }
        finally
        {
            SyncProgress.IsActive = false;
            SyncProgress.Visibility = Visibility.Collapsed;
            SyncNowButton.IsEnabled = true;
        }
    }

    private async System.Threading.Tasks.Task RefreshLastSyncedLabelAsync()
    {
        var last = await App.Notion.GetLastSyncedAsync();
        LastSyncedLabel.Text = last.HasValue
            ? $"Synced {last.Value.ToLocalTime():h:mm tt}"
            : "Never synced";
    }

    private async System.Threading.Tasks.Task ShowSyncErrorAsync(string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Sync failed",
            Content = message,
            CloseButtonText = "OK",
        };
        await dialog.ShowAsync();
    }

    private async void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        // Returning to the foreground syncs (sync-protocol.md 10.1); in the background only edits do.
        App.NotifyForeground(args.WindowActivationState != WindowActivationState.Deactivated);
        if (_restorePromptChecked) return;
        _restorePromptChecked = true;

        var prompt = new BackupRestorePrompt(App.Database, App.Backups);
        var candidate = await prompt.GetCandidateAsync();
        if (candidate is null) return;

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Restore from backup?",
            Content =
                $"PayDay didn't find any bills, but a recent backup exists:\n\n" +
                $"{candidate.FileName}\nSaved {candidate.LastWriteUtc.ToLocalTime():g}\n\n" +
                "Restore from this backup, or start fresh?",
            PrimaryButtonText = "Restore",
            CloseButtonText = "Start fresh",
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        try
        {
            await prompt.ApplyAsync(candidate);
        }
        catch (System.Exception ex)
        {
            var err = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "Restore failed",
                Content = ex.Message,
                CloseButtonText = "OK",
            };
            await err.ShowAsync();
            return;
        }

        // Refresh the currently selected page so the freshly restored data shows up.
        NavFrame.Navigate(typeof(PayDayPage));
    }

    /// <summary>Reads the saved theme from the DB and applies it to <see cref="RootGrid"/>.</summary>
    public async System.Threading.Tasks.Task ApplyPersistedThemeAsync()
    {
        var raw = await App.Database.GetSettingAsync(SettingsPageViewModel.ThemeKey);
        ApplyTheme(System.Enum.TryParse<AppTheme>(raw, ignoreCase: true, out var t) ? t : AppTheme.System);
    }

    /// <summary>Sets <c>RequestedTheme</c> on the root grid; safe to call from page code.</summary>
    public void ApplyTheme(AppTheme theme)
    {
        RootGrid.RequestedTheme = theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
    }

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        NavFrame.GoBack();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            NavFrame.Navigate(typeof(SettingsPage));
        }
        else if (args.SelectedItem is NavigationViewItem item)
        {
            switch (item.Tag)
            {
                case "payday":
                    NavFrame.Navigate(typeof(PayDayPage));
                    break;
                case "dashboard":
                    NavFrame.Navigate(typeof(DashboardPage));
                    break;
                case "bills":
                    NavFrame.Navigate(typeof(AllBillsPage));
                    break;
                case "payoff":
                    NavFrame.Navigate(typeof(PayoffTrackerPage));
                    break;
                case "insights":
                    NavFrame.Navigate(typeof(InsightsPage));
                    break;
                case "sync":
                    NavFrame.Navigate(typeof(SyncPage));
                    break;
                case "about":
                    NavFrame.Navigate(typeof(AboutPage));
                    break;
                default:
                    throw new InvalidOperationException($"Unknown navigation item tag: {item.Tag}");
            }
        }
    }
}
