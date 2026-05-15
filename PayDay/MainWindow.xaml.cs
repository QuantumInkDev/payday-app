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
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");

        _ = ApplyPersistedThemeAsync();
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
