using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using PayDay.Services;
using PayDay.ViewModels;
using Windows.System;

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

    /// <summary>Only an Enter pressed in this same box counts (not the tail of an Enter that opened something).</summary>
    private object? _enterArmed;

    private void AmountBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        _enterArmed = e.Key == VirtualKey.Enter ? sender : null;
    }

    /// <summary>
    /// Enter in a bill's amount box marks it paid, same as the Mark Paid button. KeyUp, not KeyDown:
    /// NumberBox commits the typed text to Value on Enter's KeyDown (and handles it), so by KeyUp the
    /// two-way binding has already updated the row's amount.
    /// </summary>
    private void AmountBox_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || !ReferenceEquals(_enterArmed, sender)) return;
        _enterArmed = null;
        if (sender is FrameworkElement { DataContext: PeriodBillRow row } && ViewModel.MarkPaidCommand.CanExecute(row))
        {
            ViewModel.MarkPaidCommand.Execute(row);
            e.Handled = true;
        }
    }

    /// <summary>Another device's changes were merged: show them.</summary>
    public Task ReloadAfterSyncAsync(bool settingsChanged) => ViewModel.LoadAsync();
}
