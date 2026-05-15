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

/// <summary>
/// View model behind <c>AllBillsPage</c>. Reads every <see cref="Bill"/> from
/// the DB, groups by <see cref="Bill.Type"/>, and persists per-bill changes
/// (currently only the Active toggle) through <see cref="DatabaseService"/>.
/// </summary>
public sealed partial class AllBillsPageViewModel : ObservableObject
{
    /// <summary>Canonical order of well-known bill types (plan §4.8). Custom types follow alphabetically.</summary>
    private static readonly string[] TypeOrder =
    {
        "Cards", "Bills", "Loans", "Subscriptions", "Business", "People", "Medical", "Other"
    };

    private readonly DatabaseService _db;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalBillsLabel))]
    private int _totalBills;

    public string TotalBillsLabel => $"{TotalBills} bill{(TotalBills == 1 ? "" : "s")} across {Groups.Count} type{(Groups.Count == 1 ? "" : "s")}";

    public ObservableCollection<BillGroup> Groups { get; } = new();

    public AllBillsPageViewModel(DatabaseService db)
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
                var ordered = g.OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
                Groups.Add(new BillGroup(g.Key, ordered));
            }

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

    private static int OrderKeyFor(string type)
    {
        var i = Array.IndexOf(TypeOrder, type);
        return i >= 0 ? i : int.MaxValue;
    }
}
