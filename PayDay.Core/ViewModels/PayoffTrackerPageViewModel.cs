using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PayDay.Services;

namespace PayDay.ViewModels;

/// <summary>Sort strategy for the Payoff Tracker list.</summary>
public enum PayoffStrategy
{
    /// <summary>Soonest payoff first ("never" and uncomputable last) — the original Phase 4 behavior.</summary>
    PayoffTime = 0,
    /// <summary>Smallest balance first — Dave Ramsey's "snowball" for psychological momentum.</summary>
    Snowball = 1,
    /// <summary>Highest APR first — mathematically optimal "avalanche" interest reduction.</summary>
    Avalanche = 2,
}

/// <summary>
/// View model behind <c>PayoffTrackerPage</c>. Loads active bills with an
/// outstanding balance, wraps each in a <see cref="PayoffItem"/>, and sorts
/// by the selected <see cref="Strategy"/>.
/// </summary>
public sealed partial class PayoffTrackerPageViewModel : ObservableObject
{
    private readonly IDatabaseService _db;
    private List<PayoffItem> _allItems = new();

    public PayoffTrackerPageViewModel(IDatabaseService db) => _db = db;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private PayoffStrategy _strategy = PayoffStrategy.PayoffTime;

    /// <summary>Two-way bound to the page's SegmentedItem / ComboBox SelectedIndex.</summary>
    public int StrategyIndex
    {
        get => (int)Strategy;
        set
        {
            if (value < 0) return;
            var next = (PayoffStrategy)value;
            if (next == Strategy) return;
            Strategy = next;
            ApplySort();
        }
    }

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
            _allItems = bills
                .Where(b => b.Active && b.Remaining > 0)
                .Select(b => new PayoffItem(b))
                .ToList();

            ItemCount = _allItems.Count;
            TotalRemaining = _allItems.Sum(i => i.Bill.Remaining);
            ApplySort();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplySort()
    {
        IEnumerable<PayoffItem> ordered = Strategy switch
        {
            PayoffStrategy.Snowball =>
                _allItems
                    .OrderBy(i => i.Bill.Remaining)
                    .ThenBy(i => i.Bill.Name, System.StringComparer.OrdinalIgnoreCase),
            PayoffStrategy.Avalanche =>
                _allItems
                    .OrderByDescending(i => i.Bill.APR)
                    .ThenBy(i => i.Bill.Remaining)
                    .ThenBy(i => i.Bill.Name, System.StringComparer.OrdinalIgnoreCase),
            _ =>
                _allItems
                    .OrderBy(i => i.SortOrder.Bucket)
                    .ThenBy(i => i.SortOrder.Months)
                    .ThenBy(i => i.Bill.Name, System.StringComparer.OrdinalIgnoreCase),
        };

        Items.Clear();
        foreach (var i in ordered) Items.Add(i);
    }
}
