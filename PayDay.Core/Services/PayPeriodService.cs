using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using PayDay.Models;

namespace PayDay.Services;

public sealed class PayPeriodService
{
    public const int PeriodLengthDays = 14;
    public const string PayAnchorKey = "PayAnchor";
    public const string EarlyStartKey = "EarlyStart";
    /// <summary>
    /// A new pay period unlocks at 3 PM the day before its official start date.
    /// Adding 9 hours to "today" before stripping to a date shifts after-3-PM
    /// times into the next calendar day — every other time of day is unchanged.
    /// </summary>
    public static readonly TimeSpan EarlyStartShift = TimeSpan.FromHours(9);

    private static readonly DateTime DefaultAnchor = new DateTime(2026, 3, 20);

    private readonly IDatabaseService _db;

    public PayPeriodService(IDatabaseService db) => _db = db;

    // ------------------------------------------------------------------
    // Pure functions (testable without DB)
    // ------------------------------------------------------------------

    /// <summary>
    /// Returns the 3 labeled periods (This / Next / Following) around <paramref name="today"/>,
    /// computed by walking 14-day windows from <paramref name="anchor"/>.
    /// Mirrors the JS getPayPeriods() in payday.html.
    /// </summary>
    public static IReadOnlyList<PayPeriod> GetPayPeriods(DateTime anchor, DateTime today, int count = 8)
    {
        // Treat any time at or after 3 PM as the next calendar day so the upcoming
        // pay period opens "for paying" at 3 PM the day prior to its start date.
        // For times before 3 PM the shift stays on the same day (no observable effect).
        var todayDate = today.Add(EarlyStartShift).Date;
        var current = anchor.Date;

        while (current > todayDate)
        {
            current = current.AddDays(-PeriodLengthDays);
        }
        while (current.AddDays(PeriodLengthDays) <= todayDate)
        {
            current = current.AddDays(PeriodLengthDays);
        }

        var start = current.AddDays(-PeriodLengthDays);

        var periods = new List<PayPeriod>(count);
        for (var i = 0; i < count; i++)
        {
            var periodStart = start.AddDays(i * PeriodLengthDays);
            var periodEnd = periodStart.AddDays(PeriodLengthDays - 1);
            var isCurrent = todayDate >= periodStart && todayDate <= periodEnd;
            periods.Add(new PayPeriod(periodStart, periodEnd, periodStart, isCurrent, Label: null));
        }

        var currentIndex = periods.FindIndex(p => p.IsCurrent);
        if (currentIndex < 0)
        {
            return Array.Empty<PayPeriod>();
        }

        var labels = new[] { "This Pay Period", "Next Pay Period", "Following Period" };
        var result = new List<PayPeriod>(labels.Length);
        for (var i = 0; i < labels.Length && currentIndex + i < periods.Count; i++)
        {
            result.Add(periods[currentIndex + i] with { Label = labels[i] });
        }
        return result;
    }

    /// <summary>
    /// For a monthly bill, finds the concrete due date in prev/current/next month that
    /// lands within [<paramref name="periodStart"/>, <paramref name="periodEnd"/>].
    /// Days beyond the calendar month overflow (e.g. day 31 in Feb 2026 → Mar 3),
    /// matching the JS Date constructor behavior in payday.html.
    /// </summary>
    public static DateTime? GetBillDueDate(int dueDay, DateTime periodStart, DateTime periodEnd)
    {
        var anchor = new DateTime(periodStart.Year, periodStart.Month, 1);
        foreach (var offset in new[] { -1, 0, 1 })
        {
            var monthStart = anchor.AddMonths(offset);
            var candidate = monthStart.AddDays(dueDay - 1);
            if (candidate >= periodStart && candidate <= periodEnd)
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// Assigns each bill to the periods where it's due, by rate:
    ///   Bi-Weekly → every period (due date = period start);
    ///   Monthly → period whose window contains the bill's due day;
    ///   Yearly → period whose window contains YearlyDate (MM-DD);
    ///   Once → never auto-assigned.
    /// Inactive bills are filtered out. Within each period, bills are sorted by due date.
    /// </summary>
    public static IReadOnlyList<AssignedPayPeriod> AssignBillsToPeriods(
        IEnumerable<Bill> bills,
        IReadOnlyList<PayPeriod> periods)
    {
        var activeBills = bills.Where(b => b.Active).ToList();
        var result = new List<AssignedPayPeriod>(periods.Count);

        foreach (var period in periods)
        {
            var assigned = new List<PeriodBill>();
            foreach (var bill in activeBills)
            {
                var due = ResolveDueDate(bill, period);
                if (due is null && bill.Rate != "Bi-Weekly") continue;
                assigned.Add(new PeriodBill(bill, due));
            }
            assigned.Sort((a, b) =>
            {
                var da = a.DueDate ?? DateTime.MaxValue;
                var db = b.DueDate ?? DateTime.MaxValue;
                return da.CompareTo(db);
            });
            var total = assigned.Sum(b => b.Bill.Payment);
            result.Add(new AssignedPayPeriod(period, assigned, total));
        }
        return result;
    }

    private static DateTime? ResolveDueDate(Bill bill, PayPeriod period)
    {
        switch (bill.Rate)
        {
            case "Bi-Weekly":
                return period.Start;
            case "Monthly":
                return GetBillDueDate(bill.DueDay, period.Start, period.End);
            case "Yearly":
                if (string.IsNullOrWhiteSpace(bill.YearlyDate)) return null;
                var parts = bill.YearlyDate.Split('-');
                if (parts.Length != 2
                    || !int.TryParse(parts[0], out var mm)
                    || !int.TryParse(parts[1], out var dd))
                {
                    return null;
                }
                foreach (var year in new[] { period.Start.Year, period.Start.Year + 1 })
                {
                    DateTime candidate;
                    try
                    {
                        candidate = new DateTime(year, mm, 1).AddDays(dd - 1);
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        continue;
                    }
                    if (candidate >= period.Start && candidate <= period.End)
                    {
                        return candidate;
                    }
                }
                return null;
            default:
                // "Once" or unknown — never auto-assigned
                return null;
        }
    }

    // ------------------------------------------------------------------
    // DB-coupled helpers
    // ------------------------------------------------------------------

    public async Task<DateTime> GetPayAnchorAsync()
    {
        var raw = await _db.GetSettingAsync(PayAnchorKey).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(raw)
            && DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var anchor))
        {
            return anchor.Date;
        }
        return DefaultAnchor;
    }

    public Task SetPayAnchorAsync(DateTime anchor)
        => _db.SetSettingAsync(PayAnchorKey, anchor.Date.ToString("yyyy-MM-dd"));

    public async Task<bool> GetEarlyStartAsync()
    {
        var raw = await _db.GetSettingAsync(EarlyStartKey).ConfigureAwait(false);
        return string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase);
    }

    public Task SetEarlyStartAsync(bool value)
        => _db.SetSettingAsync(EarlyStartKey, value ? "true" : "false");

    /// <summary>
    /// Reads the pay anchor + all active bills from the DB and returns the 3 labeled
    /// periods (This / Next / Following) with bills already assigned.
    /// </summary>
    public async Task<IReadOnlyList<AssignedPayPeriod>> GetCurrentPeriodsAsync(DateTime? today = null)
    {
        var anchor = await GetPayAnchorAsync().ConfigureAwait(false);
        var bills = await _db.GetAllBillsAsync().ConfigureAwait(false);
        // DateTime.Now (not Today) so the time-of-day component drives the
        // EarlyStartShift in GetPayPeriods. Tests pass an explicit DateTime.
        var periods = GetPayPeriods(anchor, today ?? DateTime.Now);
        return AssignBillsToPeriods(bills, periods);
    }
}
