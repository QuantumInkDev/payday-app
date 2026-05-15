using PayDay.Models;
using PayDay.Services;

namespace PayDay.ViewModels;

/// <summary>
/// View-layer wrapper around a <see cref="Bill"/> with an outstanding balance.
/// Computes the months-to-payoff estimate, the limit-utilization progress
/// (only meaningful for credit cards with a <see cref="Bill.CreditLimit"/>),
/// and human-readable labels for the page card.
/// </summary>
public sealed class PayoffItem
{
    public Bill Bill { get; }

    /// <summary>null = inputs invalid (e.g. payment is zero); <c>int.MaxValue</c> = never pays off; otherwise months.</summary>
    public int? EstimatedMonths { get; }

    /// <summary>0..1 — fraction of the credit limit currently in use. Always 0 when <see cref="HasProgress"/> is false.</summary>
    public double ProgressFraction { get; }

    /// <summary>True when the bill has a credit limit we can compute utilization against (i.e. a credit card).</summary>
    public bool HasProgress { get; }

    /// <summary>Top-line months label: "12 months" / "Never at this rate" / "—".</summary>
    public string PayoffLabel { get; }

    /// <summary>Secondary label: "$100/mo @ 12.0% APR" or "$100/mo".</summary>
    public string PaymentLabel { get; }

    public PayoffItem(Bill bill)
    {
        Bill = bill;
        EstimatedMonths = PayoffCalculator.EstimatePayoff(bill.Owed, bill.Cost, bill.APR);

        HasProgress = bill.CreditLimit > 0;
        ProgressFraction = HasProgress
            ? System.Math.Clamp(bill.Owed / bill.CreditLimit, 0, 1)
            : 0;

        PayoffLabel = EstimatedMonths switch
        {
            null => "—",
            int.MaxValue => "Never at this rate",
            1 => "1 month",
            int n => $"{n} months",
        };

        PaymentLabel = bill.APR > 0
            ? $"${bill.Cost:N2}/mo @ {bill.APR:N1}% APR"
            : $"${bill.Cost:N2}/mo";
    }

    /// <summary>
    /// Sort tuple: bucket 0 = a concrete month count; bucket 1 = "never at this rate";
    /// bucket 2 = couldn't compute. Within bucket 0 we sort by ascending months.
    /// </summary>
    public (int Bucket, int Months) SortOrder => EstimatedMonths switch
    {
        null => (2, 0),
        int.MaxValue => (1, 0),
        int n => (0, n),
    };
}
