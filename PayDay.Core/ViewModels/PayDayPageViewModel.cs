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
/// View model behind <c>PayDayPage</c>. Owns the 3-period assignment, splits the
/// current period's bills into auto-pay / unpaid / paid lists, and exposes the
/// commands the page binds to.
/// </summary>
public sealed partial class PayDayPageViewModel : ObservableObject
{
    private readonly IDatabaseService _db;
    private readonly PayPeriodService _periodService;
    private readonly PaymentService _paymentService;
    private readonly NotionSyncService? _notion;
    private readonly BackupRotationService? _backups;

    public PayDayPageViewModel(
        IDatabaseService db,
        NotionSyncService? notion = null,
        BackupRotationService? backups = null)
    {
        _db = db;
        _periodService = new PayPeriodService(db);
        _paymentService = new PaymentService(db);
        _notion = notion;
        _backups = backups;
    }

    [ObservableProperty]
    private NotionPushStatus _lastNotionPushStatus = NotionPushStatus.NotConfigured;

    [ObservableProperty]
    private string _lastNotionPushError = string.Empty;

    /// <summary>
    /// The most recent push (or batch of pushes) kicked off by a mark-paid action.
    /// Tests await this to wait for fire-and-forget pushes to finish deterministically.
    /// Null if no pushes have been started yet.
    /// </summary>
    public Task? PendingNotionPush { get; private set; }

    [ObservableProperty]
    private BackupStatus _lastBackupStatus = BackupStatus.NotConfigured;

    [ObservableProperty]
    private string _lastBackupError = string.Empty;

    /// <summary>
    /// The most recent auto-backup kicked off by a mark-paid action. Tests
    /// await this to wait for the fire-and-forget rotation to finish.
    /// </summary>
    public Task? PendingAutoBackup { get; private set; }

    // ------------------------------------------------------------------
    // Bound state
    // ------------------------------------------------------------------

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _periodLabel = string.Empty;

    [ObservableProperty]
    private string _periodRangeLabel = string.Empty;

    [ObservableProperty]
    private double _totalDue;

    [ObservableProperty]
    private double _totalPaid;

    [ObservableProperty]
    private double _remaining;

    /// <summary>0..1 — what fraction of the period's total has been paid.</summary>
    [ObservableProperty]
    private double _progressFraction;

    [ObservableProperty]
    private bool _isAllPaid;

    [ObservableProperty]
    private double _autoPayTotal;

    [ObservableProperty]
    private bool _hasCurrentPeriod;

    public ObservableCollection<AssignedPayPeriod> Periods { get; } = new();
    public ObservableCollection<PeriodBillRow> AutoPayBills { get; } = new();
    public ObservableCollection<PeriodBillRow> UnpaidBills { get; } = new();
    public ObservableCollection<PeriodBillRow> PaidBills { get; } = new();

    public AssignedPayPeriod? CurrentPeriod { get; private set; }
    public string? CurrentPeriodKey => CurrentPeriod?.Period.Key;

    // ------------------------------------------------------------------
    // Load
    // ------------------------------------------------------------------

    public async Task LoadAsync(DateTime? today = null)
    {
        IsLoading = true;
        try
        {
            var assigned = await _periodService.GetCurrentPeriodsAsync(today).ConfigureAwait(true);

            Periods.Clear();
            foreach (var p in assigned) Periods.Add(p);

            CurrentPeriod = assigned.FirstOrDefault();
            HasCurrentPeriod = CurrentPeriod is not null;
            if (CurrentPeriod is null)
            {
                ResetTotals();
                AutoPayBills.Clear();
                UnpaidBills.Clear();
                PaidBills.Clear();
                return;
            }

            var period = CurrentPeriod.Period;
            PeriodLabel = period.Label ?? "Current Pay Period";
            PeriodRangeLabel = $"{period.Start:MMM d} – {period.End:MMM d, yyyy}";

            var periodKey = period.Key;
            var payments = await _paymentService.GetPeriodPaymentsAsync(periodKey).ConfigureAwait(true);
            var paymentsByBill = payments
                .GroupBy(p => p.BillId)
                .ToDictionary(g => g.Key, g => g.First());

            AutoPayBills.Clear();
            UnpaidBills.Clear();
            PaidBills.Clear();

            foreach (var pb in CurrentPeriod.Bills)
            {
                var row = new PeriodBillRow(pb, periodKey);
                if (pb.Bill.AutoPay)
                {
                    AutoPayBills.Add(row);
                    continue;
                }
                if (paymentsByBill.TryGetValue(pb.Bill.Id, out var payment))
                {
                    row.IsPaid = true;
                    row.AmountPaid = payment.AmountPaid;
                    row.PaymentId = payment.Id;
                    PaidBills.Add(row);
                }
                else
                {
                    UnpaidBills.Add(row);
                }
            }

            RecalculateTotals();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ResetTotals()
    {
        TotalDue = 0;
        TotalPaid = 0;
        Remaining = 0;
        ProgressFraction = 0;
        AutoPayTotal = 0;
        IsAllPaid = false;
        PeriodLabel = string.Empty;
        PeriodRangeLabel = string.Empty;
    }

    private void RecalculateTotals()
    {
        var manualTotal = UnpaidBills.Sum(r => r.Bill.Cost) + PaidBills.Sum(r => r.Bill.Cost);
        AutoPayTotal = AutoPayBills.Sum(r => r.Bill.Cost);
        TotalDue = manualTotal;
        TotalPaid = PaidBills.Sum(r => r.AmountPaid);
        Remaining = Math.Max(0, manualTotal - TotalPaid);
        ProgressFraction = manualTotal > 0 ? Math.Min(1, TotalPaid / manualTotal) : 0;
        IsAllPaid = UnpaidBills.Count == 0 && (PaidBills.Count > 0 || manualTotal == 0);
        MarkAllPaidCommand.NotifyCanExecuteChanged();
    }

    // ------------------------------------------------------------------
    // Commands
    // ------------------------------------------------------------------

    [RelayCommand]
    private async Task MarkPaidAsync(PeriodBillRow? row)
    {
        if (row is null || CurrentPeriodKey is null) return;
        var amount = row.AmountPaid > 0 ? row.AmountPaid : row.Bill.Cost;
        var id = await _paymentService.MarkPaidAsync(CurrentPeriodKey, row.Bill.Id, amount).ConfigureAwait(true);
        row.PaymentId = id;
        row.AmountPaid = amount;
        row.IsPaid = true;
        UnpaidBills.Remove(row);
        PaidBills.Add(row);
        RecalculateTotals();
        PendingNotionPush = PushPaymentSafeAsync(new Payment
        {
            Id = id,
            BillId = row.Bill.Id,
            PeriodKey = CurrentPeriodKey,
            AmountPaid = amount,
            PaidAt = DateTime.UtcNow.ToString("O"),
        }, row.Bill.Name);
        PendingAutoBackup = BackupSafeAsync();
    }

    [RelayCommand]
    private async Task UnmarkPaidAsync(PeriodBillRow? row)
    {
        if (row is null || CurrentPeriodKey is null) return;
        await _paymentService.UnmarkPaidAsync(CurrentPeriodKey, row.Bill.Id).ConfigureAwait(true);
        row.PaymentId = null;
        row.IsPaid = false;
        row.AmountPaid = row.Bill.Cost;
        PaidBills.Remove(row);
        UnpaidBills.Add(row);
        RecalculateTotals();
    }

    [RelayCommand(CanExecute = nameof(CanMarkAllPaid))]
    private async Task MarkAllPaidAsync()
    {
        if (CurrentPeriodKey is null) return;
        var pushes = new List<Task>();
        foreach (var row in UnpaidBills.ToList())
        {
            var amount = row.AmountPaid > 0 ? row.AmountPaid : row.Bill.Cost;
            var id = await _paymentService.MarkPaidAsync(CurrentPeriodKey, row.Bill.Id, amount).ConfigureAwait(true);
            row.PaymentId = id;
            row.AmountPaid = amount;
            row.IsPaid = true;
            UnpaidBills.Remove(row);
            PaidBills.Add(row);
            pushes.Add(PushPaymentSafeAsync(new Payment
            {
                Id = id,
                BillId = row.Bill.Id,
                PeriodKey = CurrentPeriodKey,
                AmountPaid = amount,
                PaidAt = DateTime.UtcNow.ToString("O"),
            }, row.Bill.Name));
        }
        RecalculateTotals();
        PendingNotionPush = Task.WhenAll(pushes);
        PendingAutoBackup = BackupSafeAsync();
    }

    private bool CanMarkAllPaid() => UnpaidBills.Count > 0;

    /// <summary>
    /// Fire-and-forget Notion push: catches all exceptions and surfaces them via
    /// <see cref="LastNotionPushStatus"/> / <see cref="LastNotionPushError"/>.
    /// </summary>
    private async Task PushPaymentSafeAsync(Payment payment, string billName)
    {
        if (_notion is null || !_notion.HasToken())
        {
            LastNotionPushStatus = NotionPushStatus.NotConfigured;
            return;
        }
        try
        {
            await _notion.PushPaymentAsync(payment, billName).ConfigureAwait(true);
            LastNotionPushStatus = NotionPushStatus.Ok;
            LastNotionPushError = string.Empty;
        }
        catch (Exception ex)
        {
            LastNotionPushStatus = NotionPushStatus.Failed;
            LastNotionPushError = ex.Message;
        }
    }

    /// <summary>
    /// Fire-and-forget auto-backup: snapshots the full DB to LocalFolder/backups/
    /// and trims to <see cref="BackupRotationService.MaxBackups"/>. Failures are
    /// surfaced via <see cref="LastBackupStatus"/> but never throw to the caller.
    /// </summary>
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
}
