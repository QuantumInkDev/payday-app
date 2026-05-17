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
    Payment,
    Remaining,
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
        "Cards", "Bills", "Loans", "Installments", "Subscriptions", "Business", "People", "Medical", "Other"
    };

    private readonly IDatabaseService _db;
    private readonly NotionSyncService? _notion;

    [ObservableProperty]
    private NotionPushStatus _lastNotionPushStatus = NotionPushStatus.NotConfigured;

    [ObservableProperty]
    private string _lastNotionPushError = string.Empty;

    /// <summary>
    /// The most recent push kicked off by <see cref="SaveBillAsync"/>. Tests
    /// await this to wait for the fire-and-forget push to finish deterministically.
    /// Null until the first save.
    /// </summary>
    public Task? PendingNotionPush { get; private set; }

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalBillsLabel))]
    private int _totalBills;

    public string TotalBillsLabel => $"{TotalBills} bill{(TotalBills == 1 ? "" : "s")} across {Groups.Count} type{(Groups.Count == 1 ? "" : "s")}";

    public ObservableCollection<BillGroup> Groups { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameIndicator))]
    [NotifyPropertyChangedFor(nameof(PaymentIndicator))]
    [NotifyPropertyChangedFor(nameof(RemainingIndicator))]
    [NotifyPropertyChangedFor(nameof(DueIndicator))]
    [NotifyPropertyChangedFor(nameof(RateIndicator))]
    private AllBillsSortColumn _sortColumn = AllBillsSortColumn.Name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NameIndicator))]
    [NotifyPropertyChangedFor(nameof(PaymentIndicator))]
    [NotifyPropertyChangedFor(nameof(RemainingIndicator))]
    [NotifyPropertyChangedFor(nameof(DueIndicator))]
    [NotifyPropertyChangedFor(nameof(RateIndicator))]
    private bool _sortAscending = true;

    public string NameIndicator => IndicatorFor(AllBillsSortColumn.Name);
    public string PaymentIndicator => IndicatorFor(AllBillsSortColumn.Payment);
    public string RemainingIndicator => IndicatorFor(AllBillsSortColumn.Remaining);
    public string DueIndicator => IndicatorFor(AllBillsSortColumn.Due);
    public string RateIndicator => IndicatorFor(AllBillsSortColumn.Rate);

    private string IndicatorFor(AllBillsSortColumn col)
        => col != SortColumn ? string.Empty : SortAscending ? " ▲" : " ▼";

    public AllBillsPageViewModel(IDatabaseService db, NotionSyncService? notion = null)
    {
        _db = db;
        _notion = notion;
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

    /// <summary>
    /// Persists the bill locally, then fire-and-forgets a Notion push so
    /// Active-toggle changes and bill edits flow to Notion without waiting
    /// for the next manual Sync Now. Tests await <see cref="PendingNotionPush"/>
    /// to make the side effect deterministic.
    /// </summary>
    public async Task SaveBillAsync(Bill bill)
    {
        await _db.UpsertBillAsync(bill).ConfigureAwait(true);
        PendingNotionPush = PushBillSafeAsync(bill);
    }

    private async Task PushBillSafeAsync(Bill bill)
    {
        if (_notion is null || !_notion.HasToken())
        {
            LastNotionPushStatus = NotionPushStatus.NotConfigured;
            return;
        }
        try
        {
            await _notion.PushBillAsync(bill).ConfigureAwait(true);
            LastNotionPushStatus = NotionPushStatus.Ok;
            LastNotionPushError = string.Empty;
        }
        catch (Exception ex)
        {
            LastNotionPushStatus = NotionPushStatus.Failed;
            LastNotionPushError = ex.Message;
        }
    }

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
            AllBillsSortColumn.Payment   => bills.OrderBy(b => b.Payment),
            AllBillsSortColumn.Remaining => bills.OrderBy(b => b.Remaining),
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
