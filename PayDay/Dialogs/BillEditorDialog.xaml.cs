using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PayDay.Models;
using PayDay.Services;
using PayDay.ViewModels;

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
        if (result != ContentDialogResult.Primary) return false;
        dialog.ViewModel.ApplyToOriginal();
        return true;
    }

    private static async Task<IEnumerable<string>> GetExistingCustomTypesAsync()
    {
        var bills = await DatabaseService.Instance.GetAllBillsAsync();
        return bills.Select(b => b.Type).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct();
    }
}
