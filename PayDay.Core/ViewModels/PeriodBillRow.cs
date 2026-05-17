using System;
using CommunityToolkit.Mvvm.ComponentModel;
using PayDay.Models;

namespace PayDay.ViewModels;

/// <summary>
/// View-layer wrapper around a <see cref="PeriodBill"/> with mutable state for
/// the PayDay page row: edited amount, payment id (if paid), and IsPaid flag.
/// </summary>
public sealed partial class PeriodBillRow : ObservableObject
{
    public Bill Bill { get; }
    public DateTime? DueDate { get; }
    public string PeriodKey { get; }

    [ObservableProperty]
    private bool _isPaid;

    [ObservableProperty]
    private double _amountPaid;

    [ObservableProperty]
    private long? _paymentId;

    public PeriodBillRow(PeriodBill periodBill, string periodKey)
    {
        Bill = periodBill.Bill;
        DueDate = periodBill.DueDate;
        PeriodKey = periodKey;
        _amountPaid = periodBill.Bill.Payment;
    }

    public string DueDateLabel => DueDate is { } d
        ? $"Due {d:MMM d}"
        : Bill.Rate == "Bi-Weekly" ? "Due this period" : string.Empty;
}
