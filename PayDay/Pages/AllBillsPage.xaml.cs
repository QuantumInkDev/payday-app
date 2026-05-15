using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PayDay.Models;
using PayDay.Services;
using PayDay.ViewModels;

namespace PayDay.Pages;

public sealed partial class AllBillsPage : Page
{
    public AllBillsPageViewModel ViewModel { get; }

    public AllBillsPage()
    {
        ViewModel = new AllBillsPageViewModel(DatabaseService.Instance);
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += OnPageLoaded;
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
    }

    private async void OnActiveToggled(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleSwitch ts && ts.DataContext is Bill bill && bill.Active != ts.IsOn)
        {
            bill.Active = ts.IsOn;
            await ViewModel.SaveBillAsync(bill);
        }
    }
}
