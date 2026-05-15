using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PayDay.Models;
using PayDay.ViewModels;

namespace PayDay.Dialogs;

public sealed partial class BillEditorDialog : ContentDialog
{
    public BillEditorViewModel ViewModel { get; }

    private BillEditorDialog(Bill bill, bool isAddMode)
    {
        ViewModel = new BillEditorViewModel(bill, isAddMode);
        InitializeComponent();
    }

    /// <summary>
    /// Opens the editor for the given bill. On Save, copies edited values back onto
    /// the bill and returns true; on Cancel, leaves the bill untouched and returns false.
    /// </summary>
    public static async Task<bool> ShowAsync(XamlRoot xamlRoot, Bill bill, bool isAddMode)
    {
        var dialog = new BillEditorDialog(bill, isAddMode) { XamlRoot = xamlRoot };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return false;
        dialog.ViewModel.ApplyToOriginal();
        return true;
    }
}
