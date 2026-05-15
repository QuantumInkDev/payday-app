using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using PayDay.Models;
using PayDay.Services;

namespace PayDay.ViewModels;

/// <summary>
/// View model behind <c>InsightsPage</c>. Aggregates two chart-feeding lists
/// from the database — owed-over-time (line) and type-spending breakdown
/// (donut) — plus the headline current-total figure. Knows how to take a
/// new <see cref="Snapshot"/> from the current state.
/// </summary>
public sealed partial class InsightsPageViewModel : ObservableObject
{
    private readonly IDatabaseService _db;

    public InsightsPageViewModel(IDatabaseService db) => _db = db;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private double _currentTotalOwed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSnapshots))]
    [NotifyPropertyChangedFor(nameof(SummaryLabel))]
    private int _snapshotCount;

    public bool HasSnapshots => SnapshotCount > 0;

    public string SummaryLabel => SnapshotCount switch
    {
        0 => "No snapshots saved yet — capture one to start tracking your trend.",
        1 => "1 snapshot saved",
        _ => $"{SnapshotCount} snapshots saved",
    };

    public ObservableCollection<SnapshotPoint> History { get; } = new();
    public ObservableCollection<TypeBreakdownEntry> TypeBreakdown { get; } = new();

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var bills = await _db.GetAllBillsAsync().ConfigureAwait(true);
            var active = bills.Where(b => b.Active).ToList();
            CurrentTotalOwed = active.Sum(b => b.Owed);

            History.Clear();
            var snapshots = await _db.GetAllSnapshotsAsync().ConfigureAwait(true);
            foreach (var s in snapshots.OrderBy(s => s.SnapshotDate, StringComparer.Ordinal))
            {
                History.Add(new SnapshotPoint(ParseDate(s.SnapshotDate), s.TotalOwed));
            }
            SnapshotCount = History.Count;

            TypeBreakdown.Clear();
            var totalCost = active.Sum(b => b.Cost);
            var grouped = active
                .GroupBy(b => b.Type)
                .Select(g => new
                {
                    Type = g.Key,
                    Total = g.Sum(b => b.Cost),
                    Count = g.Count(),
                })
                .Where(g => g.Total > 0)
                .OrderByDescending(g => g.Total);
            foreach (var g in grouped)
            {
                var pct = totalCost > 0 ? Math.Round(g.Total / totalCost * 100.0, 1) : 0;
                TypeBreakdown.Add(new TypeBreakdownEntry(g.Type, g.Total, g.Count, pct));
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Persists a snapshot of the current state (total owed + a JSON breakdown
    /// of per-bill balances) and re-loads the page so the new point shows up
    /// in the chart immediately.
    /// </summary>
    public async Task<long> SaveSnapshotAsync(DateTime? today = null)
    {
        var bills = await _db.GetAllBillsAsync().ConfigureAwait(true);
        var active = bills.Where(b => b.Active).ToList();
        var date = (today ?? DateTime.Today).ToString("yyyy-MM-dd");
        var snapshot = new Snapshot
        {
            SnapshotDate = date,
            TotalOwed = active.Sum(b => b.Owed),
            Details = SerializeDetails(active),
        };
        var id = await _db.InsertSnapshotAsync(snapshot).ConfigureAwait(true);
        await LoadAsync().ConfigureAwait(true);
        return id;
    }

    private static DateTime ParseDate(string raw)
        => DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d)
            ? d.Date
            : DateTime.Today;

    /// <summary>
    /// Compact JSON-ish list of "id:owed" pairs. Skips the full serializer —
    /// this column is informational/debug only and isn't read back yet.
    /// </summary>
    private static string SerializeDetails(IEnumerable<Bill> bills)
    {
        var entries = bills
            .Where(b => b.Owed > 0)
            .Select(b => $"\"{b.Id}\":{b.Owed.ToString("F2", CultureInfo.InvariantCulture)}");
        return "{" + string.Join(",", entries) + "}";
    }
}

/// <summary>One point in the owed-over-time chart.</summary>
public sealed record SnapshotPoint(DateTime Date, double TotalOwed);

/// <summary>One slice of the type-breakdown donut, plus the legend stats.</summary>
public sealed record TypeBreakdownEntry(string Type, double TotalCost, int Count, double Percent);
