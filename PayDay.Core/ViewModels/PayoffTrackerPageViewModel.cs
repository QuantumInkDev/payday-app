using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using PayDay.Services;

namespace PayDay.ViewModels;

/// <summary>
/// View model behind <c>PayoffTrackerPage</c>. Loads active bills with an
/// outstanding balance, wraps each in a <see cref="PayoffItem"/>, and sorts
/// by payoff timeline (soonest first; "never" and uncomputable last).
/// </summary>
public sealed partial class PayoffTrackerPageViewModel : ObservableObject
{
    private readonly IDatabaseService _db;

    public PayoffTrackerPageViewModel(IDatabaseService db) => _db = db;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    [NotifyPropertyChangedFor(nameof(SummaryLabel))]
    private int _itemCount;

    [ObservableProperty]
    private double _totalRemaining;

    public bool IsEmpty => ItemCount == 0;
    public string SummaryLabel => ItemCount == 1
        ? "1 bill with an outstanding balance"
        : $"{ItemCount} bills with outstanding balances";

    public ObservableCollection<PayoffItem> Items { get; } = new();

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var bills = await _db.GetAllBillsAsync().ConfigureAwait(true);
            var items = bills
                .Where(b => b.Active && b.Remaining > 0)
                .Select(b => new PayoffItem(b))
                .OrderBy(i => i.SortOrder.Bucket)
                .ThenBy(i => i.SortOrder.Months)
                .ThenBy(i => i.Bill.Name, System.StringComparer.OrdinalIgnoreCase)
                .ToList();

            Items.Clear();
            foreach (var i in items) Items.Add(i);

            ItemCount = items.Count;
            TotalRemaining = items.Sum(i => i.Bill.Remaining);
        }
        finally
        {
            IsLoading = false;
        }
    }
}
