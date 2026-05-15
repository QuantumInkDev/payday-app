using System;
using System.Collections.Generic;

namespace PayDay.Models;

public sealed record PayPeriod(
    DateTime Start,
    DateTime End,
    DateTime Payday,
    bool IsCurrent,
    string? Label)
{
    public string Key => Start.ToString("yyyy-MM-dd");
}

public sealed record AssignedPayPeriod(
    PayPeriod Period,
    IReadOnlyList<PeriodBill> Bills,
    double Total);
