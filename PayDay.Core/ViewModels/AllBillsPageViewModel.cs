using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PayDay.Models;
using PayDay.Services;

namespace PayDay.ViewModels;

public enum AllBillsSortColumn
{
    Name,
    Cost,
    Owed,
    Due,
    Rate,
}

/// <summary>
/// View model behind <c>AllBillsPage</c>. Reads every <see cref="Bill"/> from
/// the DB, groups by <see cref="Bill.Type"/>, and persists per-bill changes
/// (currently only the Active toggle) through <see cref="IDatabaseService"/>.
/// Sort state lives here (not per group) so the single column header above
/// the grouped list controls the order inside every group uniformly.
/// </summary>
public sealed partial class AllBillsPageViewModel : ObservableObject
{
    /// <summary>Canonical order of well-known bill types (plan §4.8). Custom types follow alphabetically.</summary>
    private static readonly string[] TypeOrder =
    {
        "Cards", "Bills", "Loans", "Subscriptions", "Business", "People", "Medical", "Other"
    };

    private readonly IDatabaseService _db;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalBillsLabel))]
    private int _totalBills;

    public string TotalBillsLabel => $"{TotalBills} bill{(TotalBills == 1 ? "" : "s")} across {Groups.Count} type{(Groups.Count == 1 ? "" : "s")}";

    public ObservableCollection<BillGroup> Groups { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameIndicator))]
    [NotifyPropertyChangedFor(nameof(CostIndicator))]
    [NotifyPropertyChangedFor(nameof(OwedIndicator))]
    [NotifyPropertyChangedFor(nameof(DueIndicator))]
    [NotifyPropertyChangedFor(nameof(RateIndicator))]
    private AllBillsSortColumn _sortColumn = AllBillsSortColumn.Name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameIndicator))]
    [NotifyPropertyChangedFor(nameof(CostIndicator))]
    [NotifyPropertyChangedFor(nameof(OwedIndicator))]
    [NotifyPropertyChangedFor(nameof(DueIndicator))]
    [NotifyPropertyChangedFor(nameof(RateIndicator))]
    private bool _sortAscending = true;

    public string NameIndicator => IndicatorFor(AllBillsSortColumn.Name);
    public string CostIndicator => IndicatorFor(AllBillsSortColumn.Cost);
    public string OwedIndicator => IndicatorFor(AllBillsSortColumn.Owed);
    public string DueIndicator => IndicatorFor(AllBillsSortColumn.Due);
    public string RateIndicator => IndicatorFor(AllBillsSortColumn.Rate);

    private string IndicatorFor(AllBillsSortColumn col)
        => col != SortColumn ? string.Empty : SortAscending ? " ▲" : " ▼";

    public AllBillsPageViewModel(IDatabaseService db)
    {
        _db = db;
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var bills = await _db.GetAllBillsAsync().ConfigureAwait(true);
            Groups.Clear();

            var grouped = bills
                .GroupBy(b => b.Type)
                .OrderBy(g => OrderKeyFor(g.Key))
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var g in grouped)
            {
                Groups.Add(new BillGroup(g.Key, g));
            }

            ApplySort();

            TotalBills = bills.Count;
            OnPropertyChanged(nameof(TotalBillsLabel));
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Persists the bill (only used today for the Active toggle).</summary>
    public Task SaveBillAsync(Bill bill) => _db.UpsertBillAsync(bill);

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    /// <summary>
    /// Sorts every group's bills by the named column. Clicking the same column
    /// again flips the direction; clicking a new column starts ascending.
    /// </summary>
    [RelayCommand]
    private void SortBy(string? columnName)
    {
        if (!Enum.TryParse<AllBillsSortColumn>(columnName, ignoreCase: true, out var col)) return;
        if (col == SortColumn)
        {
            SortAscending = !SortAscending;
        }
        else
        {
            SortColumn = col;
            SortAscending = true;
        }
        ApplySort();
    }

    private void ApplySort()
    {
        foreach (var group in Groups)
        {
            SortInPlace(group.Bills);
        }
    }

    private void SortInPlace(ObservableCollection<Bill> bills)
    {
        IEnumerable<Bill> ordered = SortColumn switch
        {
            AllBillsSortColumn.Cost => bills.OrderBy(b => b.Cost),
            AllBillsSortColumn.Owed => bills.OrderBy(b => b.Owed),
            AllBillsSortColumn.Due  => bills.OrderBy(b => b.DueDay),
            AllBillsSortColumn.Rate => bills.OrderBy(b => b.Rate, StringComparer.OrdinalIgnoreCase),
            _                       => bills.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase),
        };
        var snapshot = ordered.ToList();
        if (!SortAscending) snapshot.Reverse();
        bills.Clear();
        foreach (var b in snapshot) bills.Add(b);
    }

    private static int OrderKeyFor(string type)
    {
        var i = Array.IndexOf(TypeOrder, type);
        return i >= 0 ? i : int.MaxValue;
    }
}
