using System;

namespace PayDay.Services;

public static class PayoffCalculator
{
    /// <summary>
    /// Months to pay off <paramref name="owed"/> at a fixed monthly <paramref name="payment"/>
    /// and annual <paramref name="apr"/>. Returns:
    ///   null       — no balance, no payment, or invalid input;
    ///   int.MaxValue — payment can't cover monthly interest (never pays off);
    ///   N          — months (ceiling) until paid off.
    /// Mirrors the JS estimatePayoff() in payday.html.
    /// </summary>
    public static int? EstimatePayoff(double owed, double payment, double apr = 0)
    {
        if (owed <= 0 || payment <= 0) return null;
        if (apr <= 0) return (int)Math.Ceiling(owed / payment);

        var r = apr / 12.0 / 100.0;
        if (payment <= owed * r) return int.MaxValue;

        var months = -Math.Log(1 - (owed * r) / payment) / Math.Log(1 + r);
        return (int)Math.Ceiling(months);
    }
}
