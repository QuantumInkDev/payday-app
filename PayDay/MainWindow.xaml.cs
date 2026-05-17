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
        if (_restorePromptChecked) return;
        _restorePromptChecked = true;

        var prompt = new BackupRestorePrompt(DatabaseService.Instance, App.Backups);
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
        var raw = await DatabaseService.Instance.GetSettingAsync(SettingsPageViewModel.ThemeKey);
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
                case "about":
                    NavFrame.Navigate(typeof(AboutPage));
                    break;
                default:
                    throw new InvalidOperationException($"Unknown navigation item tag: {item.Tag}");
            }
        }
    }
}
