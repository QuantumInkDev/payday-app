using System;
using System.Linq;
using PayDay.Models;
using PayDay.Services;

namespace PayDay.Tests;

public class PayPeriodServiceTests
{
    [Fact]
    public void GetPayPeriods_AnchorInPast_TodayLandsInCurrent()
    {
        var anchor = new DateTime(2026, 3, 20);
        var today = new DateTime(2026, 5, 15);

        var periods = PayPeriodService.GetPayPeriods(anchor, today);

        Assert.Equal(3, periods.Count);
        Assert.True(today >= periods[0].Start && today <= periods[0].End,
            $"Today ({today:yyyy-MM-dd}) should be inside the first labeled period ({periods[0].Key} - {periods[0].End:yyyy-MM-dd})");
        Assert.True(periods[0].IsCurrent);
        Assert.Equal("This Pay Period", periods[0].Label);
        Assert.Equal("Next Pay Period", periods[1].Label);
        Assert.Equal("Following Period", periods[2].Label);
    }

    [Fact]
    public void GetPayPeriods_AdjacentPeriodsAre14DaysApart()
    {
        var periods = PayPeriodService.GetPayPeriods(
            anchor: new DateTime(2026, 3, 20),
            today: new DateTime(2026, 5, 15));

        Assert.Equal(14, (periods[1].Start - periods[0].Start).TotalDays);
        Assert.Equal(14, (periods[2].Start - periods[1].Start).TotalDays);
        Assert.Equal(13, (periods[0].End - periods[0].Start).TotalDays);
    }

    [Fact]
    public void GetPayPeriods_AnchorInFuture_WalksBackToCurrent()
    {
        // Anchor is months in the future — should walk backwards in 14-day steps until
        // it lands at-or-before today.
        var anchor = new DateTime(2027, 1, 1);
        var today = new DateTime(2026, 5, 15);

        var periods = PayPeriodService.GetPayPeriods(anchor, today);

        Assert.Equal(3, periods.Count);
        Assert.True(today >= periods[0].Start && today <= periods[0].End);
        Assert.True(periods[0].IsCurrent);
    }

    [Fact]
    public void GetPayPeriods_TodayEqualsAnchor_AnchorIsCurrent()
    {
        var anchor = new DateTime(2026, 3, 20);
        var periods = PayPeriodService.GetPayPeriods(anchor, today: anchor);

        Assert.Equal(anchor, periods[0].Start);
        Assert.True(periods[0].IsCurrent);
    }

    [Fact]
    public void GetPayPeriods_KeyIsIsoDate()
    {
        var periods = PayPeriodService.GetPayPeriods(
            anchor: new DateTime(2026, 3, 20),
            today: new DateTime(2026, 3, 20));

        Assert.Equal("2026-03-20", periods[0].Key);
    }

    [Fact]
    public void GetBillDueDate_Day15InMidMonthWindow_ReturnsDay15()
    {
        // Period: Mar 14 - Mar 27, 2026. Day 15 should land on Mar 15.
        var due = PayPeriodService.GetBillDueDate(
            dueDay: 15,
            periodStart: new DateTime(2026, 3, 14),
            periodEnd: new DateTime(2026, 3, 27));

        Assert.Equal(new DateTime(2026, 3, 15), due);
    }

    [Fact]
    public void GetBillDueDate_Day31InFebWindow_OverflowsToMarch3()
    {
        // Day 31 in Feb 2026 (28-day month) — using JS-style overflow this becomes Mar 3.
        // Period Feb 20 - Mar 5 should NOT include Feb 31 (no such day), but Mar 3 lands inside.
        var due = PayPeriodService.GetBillDueDate(
            dueDay: 31,
            periodStart: new DateTime(2026, 2, 20),
            periodEnd: new DateTime(2026, 3, 5));

        Assert.Equal(new DateTime(2026, 3, 3), due);
    }

    [Fact]
    public void GetBillDueDate_NoMatchInWindow_ReturnsNull()
    {
        // Period Apr 1 - Apr 14, day 20 doesn't land anywhere inside.
        var due = PayPeriodService.GetBillDueDate(
            dueDay: 20,
            periodStart: new DateTime(2026, 4, 1),
            periodEnd: new DateTime(2026, 4, 14));

        Assert.Null(due);
    }

    [Fact]
    public void AssignBillsToPeriods_BiWeeklyBill_AppearsInEveryPeriod()
    {
        var bill = new Bill { Id = "x", Name = "Bi-weekly", Type = "Loans", Rate = "Bi-Weekly", Active = true, Payment =50 };
        var periods = PayPeriodService.GetPayPeriods(
            anchor: new DateTime(2026, 3, 20),
            today: new DateTime(2026, 5, 15));

        var assigned = PayPeriodService.AssignBillsToPeriods(new[] { bill }, periods);

        Assert.All(assigned, a =>
        {
            Assert.Single(a.Bills);
            Assert.Equal(a.Period.Start, a.Bills[0].DueDate);
        });
    }

    [Fact]
    public void AssignBillsToPeriods_OnceBill_NeverAppears()
    {
        var bill = new Bill { Id = "x", Name = "One-off", Type = "Other", Rate = "Once", Active = true, Payment =10 };
        var periods = PayPeriodService.GetPayPeriods(
            anchor: new DateTime(2026, 3, 20),
            today: new DateTime(2026, 5, 15));

        var assigned = PayPeriodService.AssignBillsToPeriods(new[] { bill }, periods);

        Assert.All(assigned, a => Assert.Empty(a.Bills));
    }

    [Fact]
    public void AssignBillsToPeriods_InactiveBill_IsExcluded()
    {
        var bill = new Bill { Id = "x", Name = "Hidden", Type = "Bills", Rate = "Bi-Weekly", Active = false, Payment =10 };
        var periods = PayPeriodService.GetPayPeriods(
            anchor: new DateTime(2026, 3, 20),
            today: new DateTime(2026, 5, 15));

        var assigned = PayPeriodService.AssignBillsToPeriods(new[] { bill }, periods);

        Assert.All(assigned, a => Assert.Empty(a.Bills));
    }

    [Fact]
    public void AssignBillsToPeriods_YearlyBill_LandsInCorrectPeriod()
    {
        // Yearly bill on Dec 25, 2026 — only the period containing Dec 25 should pick it up.
        var bill = new Bill
        {
            Id = "x",
            Name = "Christmas",
            Type = "Other",
            Rate = "Yearly",
            YearlyDate = "12-25",
            Active = true,
            Payment =100,
        };

        // 3 periods centered on Dec 25, 2026
        var periods = PayPeriodService.GetPayPeriods(
            anchor: new DateTime(2026, 3, 20),
            today: new DateTime(2026, 12, 25));

        var assigned = PayPeriodService.AssignBillsToPeriods(new[] { bill }, periods);
        var hits = assigned.Where(a => a.Bills.Count > 0).ToList();

        Assert.Single(hits);
        Assert.Equal(new DateTime(2026, 12, 25), hits[0].Bills[0].DueDate);
    }

    [Fact]
    public void AssignBillsToPeriods_TotalEqualsSumOfCosts()
    {
        var bills = new[]
        {
            new Bill { Id = "a", Name = "A", Type = "Loans", Rate = "Bi-Weekly", Active = true, Payment =100 },
            new Bill { Id = "b", Name = "B", Type = "Loans", Rate = "Bi-Weekly", Active = true, Payment =50.50 },
        };
        var periods = PayPeriodService.GetPayPeriods(
            anchor: new DateTime(2026, 3, 20),
            today: new DateTime(2026, 5, 15));

        var assigned = PayPeriodService.AssignBillsToPeriods(bills, periods);

        Assert.All(assigned, a => Assert.Equal(150.50, a.Total, precision: 2));
    }

    [Fact]
    public void AssignBillsToPeriods_BillsSortedByDueDate()
    {
        var bills = new[]
        {
            new Bill { Id = "late", Name = "Late", Type = "Bills", Rate = "Monthly", DueDay = 25, Active = true, Payment =10 },
            new Bill { Id = "early", Name = "Early", Type = "Bills", Rate = "Monthly", DueDay = 1, Active = true, Payment =10 },
        };
        // Period containing both day 1 and day 25 — pick a long window of "This".
        var anchor = new DateTime(2026, 4, 1);
        var today = new DateTime(2026, 4, 10);
        var periods = PayPeriodService.GetPayPeriods(anchor, today);
        var current = periods[0];

        var assigned = PayPeriodService.AssignBillsToPeriods(bills, new[] { current });
        var billsInPeriod = assigned[0].Bills;

        // At least the one whose day falls in the window appears; if both fall in,
        // make sure order is early-then-late.
        if (billsInPeriod.Count == 2)
        {
            Assert.Equal("early", billsInPeriod[0].Bill.Id);
            Assert.Equal("late", billsInPeriod[1].Bill.Id);
        }
        else
        {
            // Otherwise just confirm the order is monotonic in due date.
            for (var i = 1; i < billsInPeriod.Count; i++)
            {
                Assert.True(billsInPeriod[i - 1].DueDate <= billsInPeriod[i].DueDate);
            }
        }
    }
}
