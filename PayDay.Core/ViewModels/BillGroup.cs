using System.Collections.Generic;
using PayDay.Models;

namespace PayDay.ViewModels;

/// <summary>
/// A header + list of bills for one bill <see cref="Bill.Type"/>, used by
/// <c>AllBillsPage</c>'s grouped list. Kept as a plain wrapper (no
/// observability) — the page re-binds the whole groups collection on refresh.
/// </summary>
public sealed class BillGroup
{
    public string Key { get; }
    public IReadOnlyList<Bill> Bills { get; }
    public int Count => Bills.Count;

    public BillGroup(string key, IReadOnlyList<Bill> bills)
    {
        Key = key;
        Bills = bills;
    }
}
