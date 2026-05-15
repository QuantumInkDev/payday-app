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

    Task<long> InsertPaymentAsync(Payment payment);
    Task<IReadOnlyList<Payment>> GetPaymentsByPeriodAsync(string periodKey);
    Task<int> DeletePaymentsForBillInPeriodAsync(string periodKey, string billId);
}
