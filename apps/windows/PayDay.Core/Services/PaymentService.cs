using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using PayDay.Models;

namespace PayDay.Services;

public sealed class PaymentService
{
    private readonly IDatabaseService _db;

    public PaymentService(IDatabaseService db) => _db = db;

    public Task<long> MarkPaidAsync(string periodKey, string billId, double amount)
    {
        return _db.InsertPaymentAsync(new Payment
        {
            BillId = billId,
            PeriodKey = periodKey,
            AmountPaid = amount,
        });
    }

    public Task<int> UnmarkPaidAsync(string periodKey, string billId)
        => _db.DeletePaymentsForBillInPeriodAsync(periodKey, billId);

    public Task<IReadOnlyList<Payment>> GetPeriodPaymentsAsync(string periodKey)
        => _db.GetPaymentsByPeriodAsync(periodKey);

    /// <summary>
    /// True if every non-AutoPay bill in <paramref name="bills"/> has at least one
    /// payment recorded for <paramref name="periodKey"/>. AutoPay bills are excluded
    /// because they're deducted automatically and don't need manual confirmation.
    /// </summary>
    public async Task<bool> IsAllPaidAsync(string periodKey, IEnumerable<PeriodBill> bills)
    {
        var manualBillIds = bills
            .Where(b => !b.Bill.AutoPay)
            .Select(b => b.Bill.Id)
            .ToHashSet();
        if (manualBillIds.Count == 0) return true;

        var paid = await _db.GetPaymentsByPeriodAsync(periodKey).ConfigureAwait(false);
        var paidBillIds = paid.Select(p => p.BillId).ToHashSet();
        return manualBillIds.IsSubsetOf(paidBillIds);
    }
}
