using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using PayDay.Models;

namespace PayDay.Services;

/// <summary>
/// Abstraction over the persistence layer. The SQL lives in <see cref="SqliteDatabaseService"/>;
/// the app's <c>DatabaseService</c> supplies the file path. With local sync on, the app talks to a
/// <see cref="PayDay.Sync.Local.SyncRoutingDatabaseService"/> that sends every write through the
/// sync engine and reads the tables it keeps materialized; with sync off that wrapper passes every
/// call straight through.
/// </summary>
public interface IDatabaseService
{
    Task<string?> GetSettingAsync(string key);
    Task SetSettingAsync(string key, string? value);

    Task<IReadOnlyList<Bill>> GetAllBillsAsync();
    Task UpsertBillAsync(Bill bill);

    Task<long> InsertPaymentAsync(Payment payment);
    Task<IReadOnlyList<Payment>> GetPaymentsByPeriodAsync(string periodKey);
    Task<IReadOnlyList<Payment>> GetAllPaymentsAsync();
    Task<int> DeletePaymentsForBillInPeriodAsync(string periodKey, string billId);

    /// <summary>
    /// "Mark paid" for one bill in one period: records the payment and lowers
    /// <paramref name="bill"/>'s Remaining by <paramref name="amount"/> (clamped at 0), updating the
    /// object. Returns the payment's ID. The default is today's behaviour (insert the payment, then
    /// save the decremented bill); the sync write path replaces it with the payment writer rules.
    /// </summary>
    async Task<long> MarkBillPaidAsync(string periodKey, Bill bill, double amount)
    {
        var id = await InsertPaymentAsync(new Payment
        {
            BillId = bill.Id,
            PeriodKey = periodKey,
            AmountPaid = amount,
        });
        bill.Remaining = Math.Max(0, bill.Remaining - amount);
        await UpsertBillAsync(bill);
        return id;
    }

    /// <summary>
    /// "Unmark paid" for one bill in one period: removes its payments and gives
    /// <paramref name="refunded"/> back to <paramref name="bill"/>'s Remaining, updating the object.
    /// The default is today's behaviour; the sync write path replaces it.
    /// </summary>
    async Task UnmarkBillPaidAsync(string periodKey, Bill bill, double refunded)
    {
        await DeletePaymentsForBillInPeriodAsync(periodKey, bill.Id);
        if (refunded > 0)
        {
            bill.Remaining += refunded;
            await UpsertBillAsync(bill);
        }
    }

    Task<long> InsertSnapshotAsync(Snapshot snapshot);
    Task<IReadOnlyList<Snapshot>> GetAllSnapshotsAsync();
    Task<int> DeleteSnapshotAsync(long id);
    Task<int> DeleteAllSnapshotsAsync();

    Task<IReadOnlyDictionary<string, string?>> GetAllSettingsAsync();

    /// <summary>
    /// Atomically clears the Bills / Payments / Snapshots / Settings tables and
    /// inserts the supplied data. Used by JSON import (plan §4.7 / §6.1).
    /// </summary>
    Task ReplaceAllDataAsync(
        IReadOnlyList<Bill> bills,
        IReadOnlyList<Payment> payments,
        IReadOnlyList<Snapshot> snapshots,
        IReadOnlyDictionary<string, string?> settings);
}
