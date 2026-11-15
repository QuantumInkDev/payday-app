using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using PayDay.Models;

namespace PayDay.ViewModels;

/// <summary>
/// A header + list of bills for one bill <see cref="Bill.Type"/>, used by
/// <c>AllBillsPage</c>'s grouped list. <see cref="Bills"/> is an
/// <see cref="ObservableCollection{T}"/> so the parent VM can re-order it
/// in place when the user sorts a column.
/// </summary>
public sealed class BillGroup
{
    public string Key { get; }
    public ObservableCollection<Bill> Bills { get; }
    public int Count => Bills.Count;

    public double TotalPayment { get; }
    public double TotalRemaining { get; }

    public BillGroup(string key, IEnumerable<Bill> bills)
    {
        Key = key;
        var list = bills.ToList();
        Bills = new ObservableCollection<Bill>(list);
        // Inactive bills are shown in the list (so the user can toggle them back
        // on) but excluded from the subtotals — same convention as the Dashboard
        // and Payoff aggregates.
        TotalPayment = list.Where(b => b.Active).Sum(b => b.Payment);
        TotalRemaining = list.Where(b => b.Active).Sum(b => b.Remaining);
    }
}
