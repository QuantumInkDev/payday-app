using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PayDay.Dialogs;
using PayDay.Models;
using PayDay.Services;
using PayDay.ViewModels;

namespace PayDay.Pages;

public sealed partial class DashboardPage : Page
{
    public DashboardPageViewModel ViewModel { get; }

    public DashboardPage()
    {
        ViewModel = new DashboardPageViewModel(DatabaseService.Instance, App.Notion);
        InitializeComponent();
        DataContext = ViewModel;
        Loaded += OnPageLoaded;
    }

    private async void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        await ViewModel.LoadAsync();
    }

    private async void OnDashboardRowTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PeriodBill pb)
        {
            if (await BillEditorDialog.ShowAsync(this.XamlRoot, pb.Bill, isAddMode: false))
            {
                await ViewModel.SaveBillAsync(pb.Bill);
                await ViewModel.LoadAsync();
            }
        }
    }
}
