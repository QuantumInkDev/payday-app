using System;
using System.Linq;
using System.Threading.Tasks;
using PayDay.Models;
using PayDay.ViewModels;

namespace PayDay.Tests;

public class DashboardPageViewModelTests
{
    // Mirror PayDayPageViewModelTests: anchor 2026-03-20 + today 2026-05-22 →
    // current period starts 2026-05-15, ends 2026-05-28. DueDay=20 lands in.
    private static readonly DateTime DefaultAnchor = new(2026, 3, 20);
    private static readonly DateTime DefaultToday = new(2026, 5, 22);

    private static FakeDatabaseService MakeDb(params Bill[] bills)
    {
        var db = new FakeDatabaseService();
        db.Settings["PayAnchor"] = DefaultAnchor.ToString("yyyy-MM-dd");
        db.Bills.AddRange(bills);
        return db;
    }

    private static Bill MakeBill(
        string id, string name, double cost,
        string type = "Bills", string rate = "Monthly",
        int dueDay = 20, bool autoPay = false, bool active = true,
        double owed = 0, double creditLimit = 0)
        => new()
        {
            Id = id, Name = name, Payment = cost, Type = type, Rate = rate,
            DueDay = dueDay, AutoPay = autoPay, Active = active,
            Remaining = owed, CreditLimit = creditLimit,
        };

    [Fact]
    public async Task LoadAsync_TotalMonthlyObligations_NormalizesAcrossRates()
    {
        var monthly = MakeBill("m", "Monthly", cost: 100, rate: "Monthly");
        var biweekly = MakeBill("bw", "BiW", cost: 100, rate: "Bi-Weekly");
        var yearly = MakeBill("y", "Yearly", cost: 120, rate: "Yearly");
        var once = MakeBill("o", "Once", cost: 999, rate: "Once");
        var db = MakeDb(monthly, biweekly, yearly, once);

        var vm = new DashboardPageViewModel(db);
        await vm.LoadAsync(DefaultToday);

        // 100 + (100*26/12) + (120/12) + 0 = 100 + 216.666... + 10 + 0
        Assert.Equal(100 + (100.0 * 26.0 / 12.0) + 10.0, vm.TotalMonthlyObligations, 4);
    }

    [Fact]
    public async Task LoadAsync_InactiveBillsExcludedFromAllStats()
    {
        var active = MakeBill("a", "Active", cost: 100, owed: 500);
        var inactive = MakeBill("i", "Inactive", cost: 200, owed: 9999, active: false);
        var db = MakeDb(active, inactive);

        var vm = new DashboardPageViewModel(db);
        await vm.LoadAsync(DefaultToday);

        Assert.Equal(100, vm.TotalMonthlyObligations);
        Assert.Equal(500, vm.TotalRemaining);
    }

    [Fact]
    public async Task LoadAsync_CreditUtilization_AveragesAcrossActiveCardsOnly()
    {
        // Total limit = 1000 + 2000 = 3000. Owed = 500 + 1000 = 1500. → 50%
        var card1 = MakeBill("c1", "Card1", cost: 0, type: "Cards", owed: 500, creditLimit: 1000);
        var card2 = MakeBill("c2", "Card2", cost: 0, type: "Cards", owed: 1000, creditLimit: 2000);
        var nonCard = MakeBill("b", "Bill", cost: 0, type: "Bills", owed: 9999, creditLimit: 0);
        var db = MakeDb(card1, card2, nonCard);

        var vm = new DashboardPageViewModel(db);
        await vm.LoadAsync(DefaultToday);

        Assert.True(vm.HasCreditCards);
        Assert.Equal(50.0, vm.CreditUtilizationPct);
    }

    [Fact]
    public async Task LoadAsync_NoCardsOrZeroLimit_DisablesCreditUtilization()
    {
        var billOnly = MakeBill("b", "Bill", cost: 100, type: "Bills");
        var db = MakeDb(billOnly);

        var vm = new DashboardPageViewModel(db);
        await vm.LoadAsync(DefaultToday);

        Assert.False(vm.HasCreditCards);
        Assert.Equal(0, vm.CreditUtilizationPct);
    }

    [Fact]
    public async Task LoadAsync_ThreeSectionsAndCurrentPeriodCount()
    {
        var b1 = MakeBill("1", "Electric", cost: 400, dueDay: 20);
        var b2 = MakeBill("2", "Spotify", cost: 10, dueDay: 20, autoPay: true);
        var db = MakeDb(b1, b2);

        var vm = new DashboardPageViewModel(db);
        await vm.LoadAsync(DefaultToday);

        Assert.True(vm.HasCurrentPeriod);
        Assert.Equal(3, vm.Sections.Count);
        Assert.Equal("This Pay Period", vm.Sections[0].Label);
        Assert.Equal("Next Pay Period", vm.Sections[1].Label);
        Assert.Equal("Following Period", vm.Sections[2].Label);
        Assert.Equal(2, vm.BillsDueThisPeriod); // 1 manual + 1 auto-pay in current period
    }

    [Fact]
    public async Task PeriodSection_SeparatesAutoPayFromManual()
    {
        var manual = MakeBill("m", "Electric", cost: 400, dueDay: 20);
        var auto = MakeBill("a", "Spotify", cost: 10, dueDay: 20, autoPay: true);
        var db = MakeDb(manual, auto);

        var vm = new DashboardPageViewModel(db);
        await vm.LoadAsync(DefaultToday);

        var current = vm.Sections[0];
        Assert.Single(current.ManualBills);
        Assert.Single(current.AutoPayBills);
        Assert.Equal("Electric", current.ManualBills[0].Bill.Name);
        Assert.Equal("Spotify", current.AutoPayBills[0].Bill.Name);
        Assert.Equal(400, current.ManualTotal);
        Assert.Equal(10, current.AutoPayTotal);
        Assert.Equal(410, current.GrandTotal);
    }

    [Fact]
    public async Task PeriodSection_SortByName_OrdersAscThenDesc()
    {
        // Both bills due day 20 land in current period. Bi-Weekly is always in.
        var z = MakeBill("z", "Zebra", cost: 100, dueDay: 20);
        var a = MakeBill("a", "Amazon", cost: 200, dueDay: 20);
        var m = MakeBill("m", "Mickey", cost: 150, dueDay: 20);
        var db = MakeDb(z, a, m);

        var vm = new DashboardPageViewModel(db);
        await vm.LoadAsync(DefaultToday);

        var section = vm.Sections[0];
        section.SortByCommand.Execute("Name");
        Assert.Equal(new[] { "Amazon", "Mickey", "Zebra" }, section.ManualBills.Select(b => b.Bill.Name));
        Assert.Equal(DashboardSortColumn.Name, section.SortColumn);
        Assert.True(section.SortAscending);

        section.SortByCommand.Execute("Name");
        Assert.Equal(new[] { "Zebra", "Mickey", "Amazon" }, section.ManualBills.Select(b => b.Bill.Name));
        Assert.False(section.SortAscending);
    }

    [Fact]
    public async Task PeriodSection_SortByCost_OrdersByCostAsc()
    {
        var a = MakeBill("a", "Alpha", cost: 50, dueDay: 20);
        var b = MakeBill("b", "Bravo", cost: 200, dueDay: 20);
        var c = MakeBill("c", "Charlie", cost: 100, dueDay: 20);
        var db = MakeDb(a, b, c);

        var vm = new DashboardPageViewModel(db);
        await vm.LoadAsync(DefaultToday);

        var section = vm.Sections[0];
        section.SortByCommand.Execute("Payment");

        Assert.Equal(new[] { 50.0, 100.0, 200.0 }, section.ManualBills.Select(b => b.Bill.Payment));
    }

    [Fact]
    public async Task PeriodSection_SortBy_ChangingColumnResetsToAscending()
    {
        var z = MakeBill("z", "Zebra", cost: 100, dueDay: 20);
        var a = MakeBill("a", "Amazon", cost: 200, dueDay: 20);
        var db = MakeDb(z, a);

        var vm = new DashboardPageViewModel(db);
        await vm.LoadAsync(DefaultToday);

        var section = vm.Sections[0];
        section.SortByCommand.Execute("Name");
        section.SortByCommand.Execute("Name"); // desc
        Assert.False(section.SortAscending);

        section.SortByCommand.Execute("Payment"); // switching columns → ascending
        Assert.Equal(DashboardSortColumn.Payment, section.SortColumn);
        Assert.True(section.SortAscending);
    }

    [Fact]
    public async Task PeriodSection_TotalsUnchangedBySort()
    {
        var a = MakeBill("a", "A", cost: 50, dueDay: 20);
        var b = MakeBill("b", "B", cost: 100, dueDay: 20, autoPay: true);
        var db = MakeDb(a, b);

        var vm = new DashboardPageViewModel(db);
        await vm.LoadAsync(DefaultToday);

        var section = vm.Sections[0];
        var manualBefore = section.ManualTotal;
        var autoBefore = section.AutoPayTotal;
        section.SortByCommand.Execute("Name");

        Assert.Equal(manualBefore, section.ManualTotal);
        Assert.Equal(autoBefore, section.AutoPayTotal);
    }
}
