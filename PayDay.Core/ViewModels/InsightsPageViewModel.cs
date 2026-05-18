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
    private readonly NotionSyncService? _notion;
    private readonly BackupRotationService? _backups;

    public InsightsPageViewModel(
        IDatabaseService db,
        NotionSyncService? notion = null,
        BackupRotationService? backups = null)
    {
        _db = db;
        _notion = notion;
        _backups = backups;
    }

    [ObservableProperty]
    private NotionPushStatus _lastNotionPushStatus = NotionPushStatus.NotConfigured;

    [ObservableProperty]
    private string _lastNotionPushError = string.Empty;

    public Task? PendingNotionPush { get; private set; }

    [ObservableProperty]
    private BackupStatus _lastBackupStatus = BackupStatus.NotConfigured;

    [ObservableProperty]
    private string _lastBackupError = string.Empty;

    public Task? PendingAutoBackup { get; private set; }

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private double _currentTotalRemaining;

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
    /// <summary>Full snapshot rows for the management UI (delete-by-id, clear-all).</summary>
    public ObservableCollection<Snapshot> SnapshotsList { get; } = new();

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var bills = await _db.GetAllBillsAsync().ConfigureAwait(true);
            var active = bills.Where(b => b.Active).ToList();
            CurrentTotalRemaining = active.Sum(b => b.Remaining);

            History.Clear();
            SnapshotsList.Clear();
            var snapshots = await _db.GetAllSnapshotsAsync().ConfigureAwait(true);
            foreach (var s in snapshots.OrderBy(s => s.SnapshotDate, StringComparer.Ordinal))
            {
                History.Add(new SnapshotPoint(ParseDate(s.SnapshotDate), s.TotalRemaining));
            }
            // Newest-first for the manage-snapshots list.
            foreach (var s in snapshots.OrderByDescending(s => s.SnapshotDate, StringComparer.Ordinal))
            {
                SnapshotsList.Add(s);
            }
            SnapshotCount = History.Count;

            TypeBreakdown.Clear();
            var totalPayment = active.Sum(b => b.Payment);
            var grouped = active
                .GroupBy(b => b.Type)
                .Select(g => new
                {
                    Type = g.Key,
                    Total = g.Sum(b => b.Payment),
                    Count = g.Count(),
                })
                .Where(g => g.Total > 0)
                .OrderByDescending(g => g.Total);
            foreach (var g in grouped)
            {
                var pct = totalPayment > 0 ? Math.Round(g.Total / totalPayment * 100.0, 1) : 0;
                TypeBreakdown.Add(new TypeBreakdownEntry(g.Type, g.Total, g.Count, pct));
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Deletes a single snapshot by ID, then reloads so the chart updates.</summary>
    public async Task DeleteSnapshotAsync(long id)
    {
        await _db.DeleteSnapshotAsync(id).ConfigureAwait(true);
        await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>Deletes every snapshot, then reloads so the chart empties out.</summary>
    public async Task ClearAllSnapshotsAsync()
    {
        await _db.DeleteAllSnapshotsAsync().ConfigureAwait(true);
        await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Persists a snapshot of the current state (total remaining + a JSON breakdown
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
            TotalRemaining = active.Sum(b => b.Remaining),
            Details = SerializeDetails(active),
        };
        var id = await _db.InsertSnapshotAsync(snapshot).ConfigureAwait(true);
        snapshot.Id = id;
        await LoadAsync().ConfigureAwait(true);
        PendingNotionPush = PushSnapshotSafeAsync(snapshot);
        PendingAutoBackup = BackupSafeAsync();
        return id;
    }

    /// <summary>Fire-and-forget Notion push for the newly saved snapshot.</summary>
    private async Task PushSnapshotSafeAsync(Snapshot snapshot)
    {
        if (_notion is null || !_notion.HasToken())
        {
            LastNotionPushStatus = NotionPushStatus.NotConfigured;
            return;
        }
        try
        {
            await _notion.PushSnapshotAsync(snapshot).ConfigureAwait(true);
            LastNotionPushStatus = NotionPushStatus.Ok;
            LastNotionPushError = string.Empty;
        }
        catch (Exception ex)
        {
            LastNotionPushStatus = NotionPushStatus.Failed;
            LastNotionPushError = ex.Message;
        }
    }

    /// <summary>Fire-and-forget auto-backup mirror of <c>PayDayPageViewModel.BackupSafeAsync</c>.</summary>
    private async Task BackupSafeAsync()
    {
        if (_backups is null)
        {
            LastBackupStatus = BackupStatus.NotConfigured;
            return;
        }
        try
        {
            await _backups.CreateAsync().ConfigureAwait(true);
            LastBackupStatus = BackupStatus.Ok;
            LastBackupError = string.Empty;
        }
        catch (Exception ex)
        {
            LastBackupStatus = BackupStatus.Failed;
            LastBackupError = ex.Message;
        }
    }

    private static DateTime ParseDate(string raw)
        => DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d)
            ? d.Date
            : DateTime.Today;

    /// <summary>
    /// Compact JSON-ish list of "id:remaining" pairs. Skips the full serializer —
    /// this column is informational/debug only and isn't read back yet.
    /// </summary>
    private static string SerializeDetails(IEnumerable<Bill> bills)
    {
        var entries = bills
            .Where(b => b.Remaining > 0)
            .Select(b => $"\"{b.Id}\":{b.Remaining.ToString("F2", CultureInfo.InvariantCulture)}");
        return "{" + string.Join(",", entries) + "}";
    }
}

/// <summary>One point in the remaining-over-time chart.</summary>
public sealed record SnapshotPoint(DateTime Date, double TotalRemaining);

/// <summary>One slice of the type-breakdown donut, plus the legend stats.</summary>
public sealed record TypeBreakdownEntry(string Type, double TotalPayment, int Count, double Percent);
