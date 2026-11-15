using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PayDay.Services;
using PayDay.ViewModels;

namespace PayDay.Pages;

public sealed partial class PayoffTrackerPage : Page, ISyncReloadable
{
    public PayoffTrackerPageViewModel ViewModel { get; }

    public PayoffTrackerPage()
    {
        ViewModel = new PayoffTrackerPageViewModel(App.Database);
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
