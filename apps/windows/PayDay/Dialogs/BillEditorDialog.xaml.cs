using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PayDay.Models;
using PayDay.Services;
using PayDay.ViewModels;
using Windows.System;

namespace PayDay.Dialogs;

public sealed partial class BillEditorDialog : ContentDialog
{
    public BillEditorViewModel ViewModel { get; }

    private BillEditorDialog(Bill bill, bool isAddMode, IEnumerable<string>? extraTypes)
    {
        ViewModel = new BillEditorViewModel(bill, isAddMode, extraTypes);
        InitializeComponent();
    }

    /// <summary>
    /// Opens the editor for the given bill. On Save, copies edited values back onto
    /// the bill and returns true; on Cancel, leaves the bill untouched and returns false.
    /// Existing custom types (from already-saved bills) are surfaced in the Type
    /// dropdown so a user-entered "Crypto" appears next session without ceremony.
    /// </summary>
    public static async Task<bool> ShowAsync(XamlRoot xamlRoot, Bill bill, bool isAddMode)
    {
        var extra = await GetExistingCustomTypesAsync();
        var dialog = new BillEditorDialog(bill, isAddMode, extra) { XamlRoot = xamlRoot };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary && !dialog._savedWithEnter) return false;
        dialog.ViewModel.ApplyToOriginal();
        return true;
    }

    private bool _savedWithEnter;

    /// <summary>Only an Enter pressed in this same box counts (not the tail of an Enter that opened something).</summary>
    private object? _enterArmed;

    private void AmountBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        _enterArmed = e.Key == VirtualKey.Enter ? sender : null;
    }

    /// <summary>
    /// Enter in an amount box saves, like the Save button. NumberBox handles Enter's KeyDown itself
    /// (to commit the typed value), so the dialog's default button never sees it; KeyUp runs after the
    /// value is committed. Hide() reports None, so the flag tells ShowAsync it was a save.
    /// </summary>
    private void AmountBox_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || !ReferenceEquals(_enterArmed, sender)) return;
        _enterArmed = null;
        if (!ViewModel.CanSave) return;
        _savedWithEnter = true;
        e.Handled = true;
        Hide();
    }

    private static async Task<IEnumerable<string>> GetExistingCustomTypesAsync()
    {
        var bills = await App.Database.GetAllBillsAsync();
        return bills.Select(b => b.Type).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct();
    }
}
