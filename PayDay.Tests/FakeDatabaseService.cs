using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PayDay.Models;
using PayDay.Services;

namespace PayDay.Tests;

/// <summary>
/// In-memory <see cref="IDatabaseService"/> stand-in for VM tests. Trades
/// persistence for visibility — tests can poke <see cref="Bills"/>, <see cref="Payments"/>,
/// and <see cref="Settings"/> directly to set up scenarios or assert on outcomes.
/// </summary>
internal sealed class FakeDatabaseService : IDatabaseService
{
    public Dictionary<string, string?> Settings { get; } = new();
    public List<Bill> Bills { get; } = new();
    public List<Payment> Payments { get; } = new();
    public List<Snapshot> Snapshots { get; } = new();

    private long _nextPaymentId = 1;
    private long _nextSnapshotId = 1;

    public Task<string?> GetSettingAsync(string key)
        => Task.FromResult(Settings.TryGetValue(key, out var v) ? v : null);

    public Task SetSettingAsync(string key, string? value)
    {
        Settings[key] = value;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Bill>> GetAllBillsAsync()
        => Task.FromResult<IReadOnlyList<Bill>>(Bills.ToList());

    public Task UpsertBillAsync(Bill bill)
    {
        var existing = Bills.FindIndex(b => b.Id == bill.Id);
        if (existing >= 0)
        {
            Bills[existing] = bill;
        }
        else
        {
            Bills.Add(bill);
        }
        return Task.CompletedTask;
    }

    public Task<long> InsertPaymentAsync(Payment payment)
    {
        payment.Id = _nextPaymentId++;
        Payments.Add(payment);
        return Task.FromResult(payment.Id);
    }

    public Task<IReadOnlyList<Payment>> GetPaymentsByPeriodAsync(string periodKey)
        => Task.FromResult<IReadOnlyList<Payment>>(
            Payments.Where(p => p.PeriodKey == periodKey).ToList());

    public Task<int> DeletePaymentsForBillInPeriodAsync(string periodKey, string billId)
    {
        var removed = Payments.RemoveAll(p => p.PeriodKey == periodKey && p.BillId == billId);
        return Task.FromResult(removed);
    }

    public Task<long> InsertSnapshotAsync(Snapshot snapshot)
    {
        snapshot.Id = _nextSnapshotId++;
        Snapshots.Add(snapshot);
        return Task.FromResult(snapshot.Id);
    }

    public Task<IReadOnlyList<Snapshot>> GetAllSnapshotsAsync()
        => Task.FromResult<IReadOnlyList<Snapshot>>(
            Snapshots.OrderBy(s => s.SnapshotDate, System.StringComparer.Ordinal).ToList());
}
