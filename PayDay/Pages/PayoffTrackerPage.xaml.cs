using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PayDay.Services;
using PayDay.ViewModels;

namespace PayDay.Pages;

public sealed partial class PayoffTrackerPage : Page
{
    public PayoffTrackerPageViewModel ViewModel { get; }

    public PayoffTrackerPage()
    {
        ViewModel = new PayoffTrackerPageViewModel(DatabaseService.Instance);
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += OnPageLoaded;
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
    }
}
