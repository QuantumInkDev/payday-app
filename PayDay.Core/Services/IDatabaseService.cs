using System.Collections.Generic;
using System.Threading.Tasks;
using PayDay.Models;

namespace PayDay.Services;

/// <summary>
/// Abstraction over the persistence layer. The concrete WinUI-backed implementation
/// lives in the PayDay app project (it uses Windows.Storage); putting the interface
/// here in PayDay.Core lets business-logic services be unit-tested without WinUI.
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

    Task<long> InsertSnapshotAsync(Snapshot snapshot);
    Task<IReadOnlyList<Snapshot>> GetAllSnapshotsAsync();

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
