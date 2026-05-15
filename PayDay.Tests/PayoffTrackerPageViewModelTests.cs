using System.Linq;
using System.Threading.Tasks;
using PayDay.Models;
using PayDay.ViewModels;

namespace PayDay.Tests;

public class PayoffTrackerPageViewModelTests
{
    private static Bill MakeBill(
        string id, string name, double owed, double cost,
        double apr = 0, double creditLimit = 0,
        string type = "Cards", bool active = true)
        => new()
        {
            Id = id, Name = name, Type = type, Cost = cost,
            Owed = owed, APR = apr, CreditLimit = creditLimit,
            Active = active,
        };

    [Fact]
    public async Task LoadAsync_FiltersToBillsWithOwedGreaterThanZero()
    {
        var db = new FakeDatabaseService();
        db.Bills.Add(MakeBill("a", "A", owed: 100, cost: 10));
        db.Bills.Add(MakeBill("b", "B", owed: 0, cost: 10));  // skip
        db.Bills.Add(MakeBill("c", "C", owed: 500, cost: 50));

        var vm = new PayoffTrackerPageViewModel(db);
        await vm.LoadAsync();

        Assert.Equal(2, vm.ItemCount);
        Assert.DoesNotContain(vm.Items, i => i.Bill.Id == "b");
    }

    [Fact]
    public async Task LoadAsync_InactiveBillsExcluded()
    {
        var db = new FakeDatabaseService();
        db.Bills.Add(MakeBill("a", "A", owed: 100, cost: 10, active: true));
        db.Bills.Add(MakeBill("b", "B", owed: 100, cost: 10, active: false));

        var vm = new PayoffTrackerPageViewModel(db);
        await vm.LoadAsync();

        Assert.Equal(1, vm.ItemCount);
        Assert.Equal("a", vm.Items[0].Bill.Id);
    }

    [Fact]
    public async Task LoadAsync_SortsByPayoffTimelineAscending()
    {
        // a → 100/10 = 10 months. b → 500/10 = 50 months. c → 1000/100 = 10 months.
        // 'a' and 'c' tied at 10 months; tiebreak by Name (A before C).
        var db = new FakeDatabaseService();
        db.Bills.Add(MakeBill("b", "B", owed: 500, cost: 10));
        db.Bills.Add(MakeBill("a", "A", owed: 100, cost: 10));
        db.Bills.Add(MakeBill("c", "C", owed: 1000, cost: 100));

        var vm = new PayoffTrackerPageViewModel(db);
        await vm.LoadAsync();

        Assert.Equal(new[] { "A", "C", "B" }, vm.Items.Select(i => i.Bill.Name));
    }

    [Fact]
    public async Task LoadAsync_NeverPaysOff_SortsLast()
    {
        // never → APR 24% (2%/mo) on $1000 owed = $20/mo interest. Cost $15 → never.
        // normal → owed 200, cost 20 → 10 months.
        var db = new FakeDatabaseService();
        db.Bills.Add(MakeBill("never", "Never", owed: 1000, cost: 15, apr: 24));
        db.Bills.Add(MakeBill("normal", "Normal", owed: 200, cost: 20));

        var vm = new PayoffTrackerPageViewModel(db);
        await vm.LoadAsync();

        Assert.Equal("Normal", vm.Items[0].Bill.Name);
        Assert.Equal("Never", vm.Items[1].Bill.Name);
        Assert.Equal(int.MaxValue, vm.Items[1].EstimatedMonths);
        Assert.Equal("Never at this rate", vm.Items[1].PayoffLabel);
    }

    [Fact]
    public async Task LoadAsync_ZeroPayment_SortsAfterNever()
    {
        // null payoff (cost=0) sorts last; "never" before that; concrete first.
        var db = new FakeDatabaseService();
        db.Bills.Add(MakeBill("normal", "Normal", owed: 200, cost: 20));
        db.Bills.Add(MakeBill("never", "Never", owed: 1000, cost: 15, apr: 24));
        db.Bills.Add(MakeBill("zero", "Zero", owed: 500, cost: 0));

        var vm = new PayoffTrackerPageViewModel(db);
        await vm.LoadAsync();

        Assert.Equal("Normal", vm.Items[0].Bill.Name);
        Assert.Equal("Never", vm.Items[1].Bill.Name);
        Assert.Equal("Zero", vm.Items[2].Bill.Name);
        Assert.Null(vm.Items[2].EstimatedMonths);
        Assert.Equal("—", vm.Items[2].PayoffLabel);
    }

    [Fact]
    public void PayoffItem_ProgressFraction_OnlyWhenCreditLimitPositive()
    {
        var card = new PayoffItem(MakeBill("c", "Card", owed: 750, cost: 50, creditLimit: 1000));
        var loan = new PayoffItem(MakeBill("l", "Loan", owed: 500, cost: 50, creditLimit: 0, type: "Loans"));

        Assert.True(card.HasProgress);
        Assert.Equal(0.75, card.ProgressFraction);
        Assert.False(loan.HasProgress);
        Assert.Equal(0, loan.ProgressFraction);
    }

    [Fact]
    public void PayoffItem_ProgressFraction_ClampedTo1WhenOverLimit()
    {
        var maxedOut = new PayoffItem(MakeBill("c", "Card", owed: 1500, cost: 50, creditLimit: 1000));
        Assert.True(maxedOut.HasProgress);
        Assert.Equal(1, maxedOut.ProgressFraction);
    }

    [Fact]
    public async Task LoadAsync_SetsTotalOwedAndSummaryLabel()
    {
        var db = new FakeDatabaseService();
        db.Bills.Add(MakeBill("a", "A", owed: 100, cost: 10));
        db.Bills.Add(MakeBill("b", "B", owed: 250, cost: 25));

        var vm = new PayoffTrackerPageViewModel(db);
        await vm.LoadAsync();

        Assert.Equal(350, vm.TotalOwed);
        Assert.False(vm.IsEmpty);
        Assert.Contains("2 bills", vm.SummaryLabel);
    }

    [Fact]
    public async Task LoadAsync_EmptyDb_ReportsIsEmpty()
    {
        var db = new FakeDatabaseService();
        var vm = new PayoffTrackerPageViewModel(db);

        await vm.LoadAsync();

        Assert.True(vm.IsEmpty);
        Assert.Equal(0, vm.ItemCount);
        Assert.Equal(0, vm.TotalOwed);
    }

    [Fact]
    public void PayoffItem_PayoffLabel_PluralizesMonths()
    {
        var one = new PayoffItem(MakeBill("a", "A", owed: 50, cost: 50));
        var many = new PayoffItem(MakeBill("b", "B", owed: 500, cost: 50));
        Assert.Equal("1 month", one.PayoffLabel);
        Assert.Equal("10 months", many.PayoffLabel);
    }
}
