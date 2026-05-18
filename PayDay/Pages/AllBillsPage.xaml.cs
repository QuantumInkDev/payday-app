using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PayDay.Dialogs;
using PayDay.Models;
using PayDay.Services;
using PayDay.ViewModels;

namespace PayDay.Pages;

public sealed partial class AllBillsPage : Page
{
    public AllBillsPageViewModel ViewModel { get; }

    public AllBillsPage()
    {
        ViewModel = new AllBillsPageViewModel(DatabaseService.Instance, App.Notion);
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
            // Reload so the per-group subtotals re-exclude / re-include the
            // toggled bill. Page state (sort + scroll) is preserved by LoadAsync.
            await ViewModel.LoadAsync();
        }
    }

    private async void OnAddBillClick(object sender, RoutedEventArgs e)
    {
        var bill = new Bill
        {
            Id = Guid.NewGuid().ToString(),
            Type = "Bills",
            Rate = "Monthly",
            DueDay = 1,
            Active = true,
        };
        if (await BillEditorDialog.ShowAsync(this.XamlRoot, bill, isAddMode: true))
        {
            await ViewModel.SaveBillAsync(bill);
            await ViewModel.LoadAsync();
        }
    }

    private async void OnBillRowTapped(object sender, TappedRoutedEventArgs e)
    {
        // A tap on the Active ToggleSwitch shouldn't also open the editor.
        if (IsAncestorOrSelf<ToggleSwitch>(e.OriginalSource as DependencyObject)) return;

        if (sender is FrameworkElement fe && fe.DataContext is Bill bill)
        {
            if (await BillEditorDialog.ShowAsync(this.XamlRoot, bill, isAddMode: false))
            {
                await ViewModel.SaveBillAsync(bill);
                await ViewModel.LoadAsync();
            }
        }
    }

    private static bool IsAncestorOrSelf<T>(DependencyObject? obj) where T : DependencyObject
    {
        while (obj is not null)
        {
            if (obj is T) return true;
            obj = VisualTreeHelper.GetParent(obj);
        }
        return false;
    }
}
