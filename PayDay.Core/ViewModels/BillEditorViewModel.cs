using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using PayDay.Models;

namespace PayDay.ViewModels;

/// <summary>
/// Form-state wrapper around a <see cref="Bill"/>. The editor dialog binds to
/// this view model so a user cancel doesn't mutate the original — call
/// <see cref="ApplyToOriginal"/> on save to commit the edits.
/// </summary>
public sealed partial class BillEditorViewModel : ObservableObject
{
    /// <summary>Well-known bill types — also offered as ComboBox suggestions; users can still type a custom one.</summary>
    public static readonly string[] KnownTypes =
    {
        "Cards", "Bills", "Loans", "Subscriptions", "Business", "People", "Medical", "Other",
    };

    /// <summary>
    /// Bound to the editor's Type ComboBox. Always starts with <see cref="KnownTypes"/>
    /// and then any custom types passed in via the ctor (typed before, persisted as
    /// existing bills). Lets the editor surface previously-typed customs without
    /// requiring a separate settings table.
    /// </summary>
    public ObservableCollection<string> TypeOptions { get; } = new();

    /// <summary>Bill rate options (the period engine recognises these exactly).</summary>
    public static readonly string[] Rates = { "Monthly", "Bi-Weekly", "Yearly", "Once" };

    private readonly Bill _original;

    public bool IsAddMode { get; }
    public string Title => IsAddMode ? "Add bill" : $"Edit {_original.Name}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _name;

    [ObservableProperty]
    private string _type;

    [ObservableProperty]
    private double _payment;

    [ObservableProperty]
    private double _remaining;

    [ObservableProperty]
    private double _available;

    [ObservableProperty]
    private double _creditLimit;

    [ObservableProperty]
    private double _dueDay;

    [ObservableProperty]
    private string _rate;

    [ObservableProperty]
    private double _apr;

    [ObservableProperty]
    private bool _autoPay;

    [ObservableProperty]
    private bool _active;

    [ObservableProperty]
    private string _yearlyDate;

    [ObservableProperty]
    private string _notes;

    public bool CanSave => !string.IsNullOrWhiteSpace(Name);

    public BillEditorViewModel(Bill bill, bool isAddMode, IEnumerable<string>? extraTypes = null)
    {
        _original = bill;
        IsAddMode = isAddMode;

        foreach (var t in KnownTypes) TypeOptions.Add(t);
        if (extraTypes is not null)
        {
            var known = new HashSet<string>(TypeOptions, StringComparer.OrdinalIgnoreCase);
            foreach (var t in extraTypes
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (known.Add(t)) TypeOptions.Add(t);
            }
        }
        // If the bill being edited has a type not in the list (e.g. legacy), surface it too.
        if (!string.IsNullOrWhiteSpace(bill.Type)
            && !TypeOptions.Any(t => string.Equals(t, bill.Type, StringComparison.OrdinalIgnoreCase)))
        {
            TypeOptions.Add(bill.Type);
        }

        _name = bill.Name;
        _type = string.IsNullOrEmpty(bill.Type) ? "Bills" : bill.Type;
        _payment = bill.Payment;
        _remaining = bill.Remaining;
        _available = bill.Available;
        _creditLimit = bill.CreditLimit;
        _dueDay = bill.DueDay;
        _rate = string.IsNullOrEmpty(bill.Rate) ? "Monthly" : bill.Rate;
        _apr = bill.APR;
        _autoPay = bill.AutoPay;
        _active = bill.Active;
        _yearlyDate = bill.YearlyDate ?? string.Empty;
        _notes = bill.Notes ?? string.Empty;
    }

    /// <summary>Copies the edited values back onto the original bill instance.</summary>
    public void ApplyToOriginal()
    {
        _original.Name = Name?.Trim() ?? string.Empty;
        _original.Type = string.IsNullOrWhiteSpace(Type) ? "Other" : Type.Trim();
        _original.Payment = Payment;
        _original.Remaining = Remaining;
        _original.Available = Available;
        _original.CreditLimit = CreditLimit;
        _original.DueDay = ClampDueDay((int)Math.Round(DueDay));
        _original.Rate = string.IsNullOrWhiteSpace(Rate) ? "Monthly" : Rate;
        _original.APR = Apr;
        _original.AutoPay = AutoPay;
        _original.Active = Active;
        _original.YearlyDate = string.IsNullOrWhiteSpace(YearlyDate) ? null : YearlyDate.Trim();
        _original.Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim();
    }

    private static int ClampDueDay(int d) => d < 1 ? 1 : d > 31 ? 31 : d;
}
