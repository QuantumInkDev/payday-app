using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PayDay.Services;
using PayDay.ViewModels;

namespace PayDay.Pages;

public sealed partial class PayDayPage : Page, ISyncReloadable
{
    public PayDayPageViewModel ViewModel { get; }

    public PayDayPage()
    {
        ViewModel = new PayDayPageViewModel(App.Database, App.Notion, App.Backups);
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += OnPageLoaded;
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
    }

    /// <summary>Another device's changes were merged: show them.</summary>
    public Task ReloadAfterSyncAsync(bool settingsChanged) => ViewModel.LoadAsync();
}
