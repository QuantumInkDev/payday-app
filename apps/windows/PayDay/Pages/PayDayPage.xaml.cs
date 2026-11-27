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
        _amountKeyUp = AmountBox_KeyUp;
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
    private readonly KeyEventHandler _amountKeyUp;

    /// <summary>
    /// NumberBox handles Enter on its inner TextBox's KeyUp (it commits the typed text to Value there and
    /// marks the event handled), so a plain KeyUp attribute never sees Enter. Listen with
    /// handledEventsToo instead: the handler then runs after the commit. Loaded can fire more than once,
    /// so remove before adding.
    /// </summary>
    private void AmountBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not UIElement box) return;
        box.RemoveHandler(KeyUpEvent, _amountKeyUp);
        box.AddHandler(KeyUpEvent, _amountKeyUp, handledEventsToo: true);
    }

    private void AmountBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        _enterArmed = e.Key == VirtualKey.Enter ? sender : null;
    }

    /// <summary>
    /// Enter in a bill's amount box marks it paid, same as the Mark Paid button. This runs after NumberBox
    /// has committed the typed text (see AmountBox_Loaded), so the two-way binding has already updated
    /// the row's amount.
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
