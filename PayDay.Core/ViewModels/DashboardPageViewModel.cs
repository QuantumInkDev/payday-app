using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using PayDay.Models;
using PayDay.Services;

namespace PayDay.ViewModels;

/// <summary>
/// View model behind <c>DashboardPage</c>. Owns the four headline stats and
/// the three period sections (This / Next / Following), each with separate
/// auto-pay and manual lists that can be sorted by column.
/// </summary>
public sealed partial class DashboardPageViewModel : ObservableObject
{
    private readonly IDatabaseService _db;
    private readonly PayPeriodService _periodService;

    public DashboardPageViewModel(IDatabaseService db)
    {
        _db = db;
        _periodService = new PayPeriodService(db);
    }

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Sum of cost across active bills, normalized to a monthly cadence.</summary>
    [ObservableProperty]
    private double _totalMonthlyObligations;

    [ObservableProperty]
    private double _totalRemaining;

    /// <summary>Percentage 0..100. Only meaningful when <see cref="HasCreditCards"/> is true.</summary>
    [ObservableProperty]
    private double _creditUtilizationPct;

    [ObservableProperty]
    private bool _hasCreditCards;

    [ObservableProperty]
    private int _billsDueThisPeriod;

    [ObservableProperty]
    private bool _hasCurrentPeriod;

    public ObservableCollection<DashboardPeriodSection> Sections { get; } = new();

    public async Task LoadAsync(DateTime? today = null)
    {
        IsLoading = true;
        try
        {
            var bills = await _db.GetAllBillsAsync().ConfigureAwait(true);
            var active = bills.Where(b => b.Active).ToList();

            TotalMonthlyObligations = active.Sum(MonthlyEquivalent);
            TotalRemaining = active.Sum(b => b.Remaining);

            var cards = active.Where(b => b.Type == "Cards" && b.CreditLimit > 0).ToList();
            var totalLimit = cards.Sum(c => c.CreditLimit);
            HasCreditCards = cards.Count > 0 && totalLimit > 0;
            CreditUtilizationPct = HasCreditCards
                ? Math.Round(cards.Sum(c => c.Remaining) / totalLimit * 100.0, 1)
                : 0;

            var periods = await _periodService.GetCurrentPeriodsAsync(today).ConfigureAwait(true);
            Sections.Clear();
            foreach (var p in periods)
            {
                Sections.Add(new DashboardPeriodSection(p));
            }

            HasCurrentPeriod = Sections.Count > 0;
            BillsDueThisPeriod = HasCurrentPeriod ? Sections[0].TotalBillCount : 0;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Normalizes a bill's payment to a monthly cadence so the headline number is
    /// comparable across rate types. Bi-Weekly bills hit 26 times a year, so
    /// they contribute (payment × 26 / 12). Yearly bills divide by 12. "Once"
    /// and unknown rates contribute zero because there's no recurring cadence.
    /// </summary>
    public static double MonthlyEquivalent(Bill b) => b.Rate switch
    {
        "Monthly" => b.Payment,
        "Bi-Weekly" => b.Payment * 26.0 / 12.0,
        "Yearly" => b.Payment / 12.0,
        _ => 0,
    };
}
