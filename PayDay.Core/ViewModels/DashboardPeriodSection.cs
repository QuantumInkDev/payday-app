using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PayDay.Models;

namespace PayDay.ViewModels;

public enum DashboardSortColumn
{
    DueDate,
    Name,
    Type,
    Cost,
}

/// <summary>
/// One period in the Dashboard overview. Holds the auto-pay and manual bills
/// for the period separately, exposes per-section totals, and supports
/// column-header sorting that flips ascending/descending on repeat clicks.
/// The sortable-table pattern shipped here is also the template the All Bills
/// page is meant to retrofit to (see <c>SESSION_STATUS.md</c>).
/// </summary>
public sealed partial class DashboardPeriodSection : ObservableObject
{
    public AssignedPayPeriod Assigned { get; }
    public string Label => Assigned.Period.Label ?? "Period";
    public string RangeLabel => $"{Assigned.Period.Start:MMM d} – {Assigned.Period.End:MMM d, yyyy}";

    public ObservableCollection<PeriodBill> ManualBills { get; } = new();
    public ObservableCollection<PeriodBill> AutoPayBills { get; } = new();

    public double ManualTotal { get; private set; }
    public double AutoPayTotal { get; private set; }
    public double GrandTotal => ManualTotal + AutoPayTotal;
    public int TotalBillCount => ManualBills.Count + AutoPayBills.Count;
    public bool HasManualBills => ManualBills.Count > 0;
    public bool HasAutoPayBills => AutoPayBills.Count > 0;
    public bool IsEmpty => TotalBillCount == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DueDateIndicator))]
    [NotifyPropertyChangedFor(nameof(NameIndicator))]
    [NotifyPropertyChangedFor(nameof(TypeIndicator))]
    [NotifyPropertyChangedFor(nameof(CostIndicator))]
    private DashboardSortColumn _sortColumn = DashboardSortColumn.DueDate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DueDateIndicator))]
    [NotifyPropertyChangedFor(nameof(NameIndicator))]
    [NotifyPropertyChangedFor(nameof(TypeIndicator))]
    [NotifyPropertyChangedFor(nameof(CostIndicator))]
    private bool _sortAscending = true;

    public string DueDateIndicator => IndicatorFor(DashboardSortColumn.DueDate);
    public string NameIndicator => IndicatorFor(DashboardSortColumn.Name);
    public string TypeIndicator => IndicatorFor(DashboardSortColumn.Type);
    public string CostIndicator => IndicatorFor(DashboardSortColumn.Cost);

    private string IndicatorFor(DashboardSortColumn col)
        => col != SortColumn ? string.Empty : SortAscending ? " ▲" : " ▼";

    public DashboardPeriodSection(AssignedPayPeriod assigned)
    {
        Assigned = assigned;
        foreach (var pb in assigned.Bills)
        {
            if (pb.Bill.AutoPay)
            {
                AutoPayBills.Add(pb);
            }
            else
            {
                ManualBills.Add(pb);
            }
        }
        ManualTotal = ManualBills.Sum(b => b.Bill.Cost);
        AutoPayTotal = AutoPayBills.Sum(b => b.Bill.Cost);
        ApplySort();
    }

    /// <summary>
    /// Sorts both lists by the named column. Clicking the same column again
    /// flips the direction; clicking a new column starts ascending.
    /// </summary>
    [RelayCommand]
    private void SortBy(string? columnName)
    {
        if (!Enum.TryParse<DashboardSortColumn>(columnName, ignoreCase: true, out var col)) return;
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
        SortInPlace(ManualBills);
        SortInPlace(AutoPayBills);
    }

    private void SortInPlace(ObservableCollection<PeriodBill> list)
    {
        IEnumerable<PeriodBill> ordered = SortColumn switch
        {
            DashboardSortColumn.Name => list.OrderBy(b => b.Bill.Name, StringComparer.OrdinalIgnoreCase),
            DashboardSortColumn.Type => list.OrderBy(b => b.Bill.Type, StringComparer.OrdinalIgnoreCase),
            DashboardSortColumn.Cost => list.OrderBy(b => b.Bill.Cost),
            _ => list.OrderBy(b => b.DueDate ?? DateTime.MaxValue),
        };
        var snapshot = ordered.ToList();
        if (!SortAscending) snapshot.Reverse();
        list.Clear();
        foreach (var b in snapshot) list.Add(b);
    }
}
