using System.Linq;
using System.Threading.Tasks;
using PayDay.Models;
using PayDay.ViewModels;

namespace PayDay.Tests;

public class AllBillsPageViewModelTests
{
    private static Bill MakeBill(
        string id,
        string name,
        string type,
        bool active = true,
        double cost = 0,
        double owed = 0,
        int dueDay = 1,
        string rate = "Monthly")
        => new()
        {
            Id = id,
            Name = name,
            Type = type,
            Active = active,
            Cost = cost,
            Owed = owed,
            DueDay = dueDay,
            Rate = rate,
        };

    [Fact]
    public async Task LoadAsync_GroupsBillsByTypeInCanonicalOrder()
    {
        var db = new FakeDatabaseService();
        db.Bills.Add(MakeBill("1", "Mom", "People"));
        db.Bills.Add(MakeBill("2", "Amazon", "Cards"));
        db.Bills.Add(MakeBill("3", "Electric", "Bills"));
        db.Bills.Add(MakeBill("4", "Spotify", "Subscriptions"));

        var vm = new AllBillsPageViewModel(db);
        await vm.LoadAsync();

        Assert.Equal(4, vm.TotalBills);
        Assert.Equal(4, vm.Groups.Count);
        // Canonical: Cards → Bills → Loans → Subscriptions → Business → People → Medical → Other.
        Assert.Equal("Cards", vm.Groups[0].Key);
        Assert.Equal("Bills", vm.Groups[1].Key);
        Assert.Equal("Subscriptions", vm.Groups[2].Key);
        Assert.Equal("People", vm.Groups[3].Key);
    }

    [Fact]
    public async Task LoadAsync_SortsBillsWithinGroupByName()
    {
        var db = new FakeDatabaseService();
        db.Bills.Add(MakeBill("1", "Zebra", "Cards"));
        db.Bills.Add(MakeBill("2", "Amazon", "Cards"));
        db.Bills.Add(MakeBill("3", "Mickey", "Cards"));

        var vm = new AllBillsPageViewModel(db);
        await vm.LoadAsync();

        var cards = Assert.Single(vm.Groups);
        Assert.Equal(3, cards.Bills.Count);
        Assert.Equal("Amazon", cards.Bills[0].Name);
        Assert.Equal("Mickey", cards.Bills[1].Name);
        Assert.Equal("Zebra", cards.Bills[2].Name);
    }

    [Fact]
    public async Task LoadAsync_CustomTypeSortsAfterKnownTypes()
    {
        var db = new FakeDatabaseService();
        db.Bills.Add(MakeBill("1", "X", "Crypto"));
        db.Bills.Add(MakeBill("2", "Y", "Other"));
        db.Bills.Add(MakeBill("3", "Z", "Cards"));

        var vm = new AllBillsPageViewModel(db);
        await vm.LoadAsync();

        Assert.Equal("Cards", vm.Groups[0].Key);
        Assert.Equal("Other", vm.Groups[1].Key);
        Assert.Equal("Crypto", vm.Groups[2].Key); // custom type falls through
    }

    [Fact]
    public async Task SaveBillAsync_UpsertsBillIntoDb()
    {
        var db = new FakeDatabaseService();
        var bill = MakeBill("1", "Foo", "Cards", active: true);
        db.Bills.Add(bill);
        var vm = new AllBillsPageViewModel(db);
        await vm.LoadAsync();

        bill.Active = false;
        await vm.SaveBillAsync(bill);

        Assert.Single(db.Bills);
        Assert.False(db.Bills[0].Active);
    }

    [Fact]
    public async Task TotalBillsLabel_PluralizesCorrectly()
    {
        var db = new FakeDatabaseService();
        var vm = new AllBillsPageViewModel(db);

        await vm.LoadAsync();
        Assert.Contains("0 bills across 0 types", vm.TotalBillsLabel);

        db.Bills.Add(MakeBill("1", "X", "Cards"));
        await vm.LoadAsync();
        Assert.Contains("1 bill across 1 type", vm.TotalBillsLabel);
        Assert.DoesNotContain("1 bills", vm.TotalBillsLabel);
        Assert.DoesNotContain("1 types", vm.TotalBillsLabel);
    }

    [Fact]
    public async Task RefreshCommand_ReloadsBillsFromDb()
    {
        var db = new FakeDatabaseService();
        db.Bills.Add(MakeBill("1", "A", "Cards"));
        var vm = new AllBillsPageViewModel(db);
        await vm.LoadAsync();
        Assert.Equal(1, vm.TotalBills);

        db.Bills.Add(MakeBill("2", "B", "Bills"));
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.TotalBills);
        Assert.Equal(2, vm.Groups.Count);
    }

    [Fact]
    public async Task DefaultSort_IsNameAscending()
    {
        var db = new FakeDatabaseService();
        var vm = new AllBillsPageViewModel(db);
        await vm.LoadAsync();

        Assert.Equal(AllBillsSortColumn.Name, vm.SortColumn);
        Assert.True(vm.SortAscending);
        Assert.Equal(" ▲", vm.NameIndicator);
        Assert.Equal(string.Empty, vm.CostIndicator);
        Assert.Equal(string.Empty, vm.OwedIndicator);
        Assert.Equal(string.Empty, vm.DueIndicator);
        Assert.Equal(string.Empty, vm.RateIndicator);
    }

    [Fact]
    public async Task SortByCommand_SameColumnTwice_FlipsDirection()
    {
        var db = new FakeDatabaseService();
        db.Bills.Add(MakeBill("1", "Amazon", "Cards", cost: 30));
        db.Bills.Add(MakeBill("2", "Best Buy", "Cards", cost: 10));
        db.Bills.Add(MakeBill("3", "Chase", "Cards", cost: 20));
        var vm = new AllBillsPageViewModel(db);
        await vm.LoadAsync();

        vm.SortByCommand.Execute("Cost");
        Assert.Equal(AllBillsSortColumn.Cost, vm.SortColumn);
        Assert.True(vm.SortAscending);
        Assert.Equal(" ▲", vm.CostIndicator);
        Assert.Equal(new[] { "Best Buy", "Chase", "Amazon" }, vm.Groups[0].Bills.Select(b => b.Name).ToArray());

        vm.SortByCommand.Execute("Cost");
        Assert.False(vm.SortAscending);
        Assert.Equal(" ▼", vm.CostIndicator);
        Assert.Equal(new[] { "Amazon", "Chase", "Best Buy" }, vm.Groups[0].Bills.Select(b => b.Name).ToArray());
    }

    [Fact]
    public async Task SortByCommand_SwitchingColumn_ResetsToAscending()
    {
        var db = new FakeDatabaseService();
        db.Bills.Add(MakeBill("1", "Zeta", "Cards", cost: 10));
        db.Bills.Add(MakeBill("2", "Alpha", "Cards", cost: 99));
        var vm = new AllBillsPageViewModel(db);
        await vm.LoadAsync();

        vm.SortByCommand.Execute("Name"); // default was Name asc; same column flips to desc
        Assert.False(vm.SortAscending);

        vm.SortByCommand.Execute("Cost"); // switching column resets to asc
        Assert.Equal(AllBillsSortColumn.Cost, vm.SortColumn);
        Assert.True(vm.SortAscending);
        Assert.Equal(new[] { "Zeta", "Alpha" }, vm.Groups[0].Bills.Select(b => b.Name).ToArray());
    }

    [Fact]
    public async Task SortByCommand_AppliesSortToEveryGroup()
    {
        var db = new FakeDatabaseService();
        db.Bills.Add(MakeBill("1", "Z-card", "Cards", cost: 5));
        db.Bills.Add(MakeBill("2", "A-card", "Cards", cost: 50));
        db.Bills.Add(MakeBill("3", "Y-bill", "Bills", cost: 7));
        db.Bills.Add(MakeBill("4", "B-bill", "Bills", cost: 70));
        var vm = new AllBillsPageViewModel(db);
        await vm.LoadAsync();

        vm.SortByCommand.Execute("Cost");

        var cards = vm.Groups.Single(g => g.Key == "Cards");
        var bills = vm.Groups.Single(g => g.Key == "Bills");
        Assert.Equal(new[] { "Z-card", "A-card" }, cards.Bills.Select(b => b.Name).ToArray());
        Assert.Equal(new[] { "Y-bill", "B-bill" }, bills.Bills.Select(b => b.Name).ToArray());
    }

    [Fact]
    public async Task SortByCommand_SortsByDueDay()
    {
        var db = new FakeDatabaseService();
        db.Bills.Add(MakeBill("1", "C", "Cards", dueDay: 28));
        db.Bills.Add(MakeBill("2", "A", "Cards", dueDay: 3));
        db.Bills.Add(MakeBill("3", "B", "Cards", dueDay: 15));
        var vm = new AllBillsPageViewModel(db);
        await vm.LoadAsync();

        vm.SortByCommand.Execute("Due");

        Assert.Equal(new[] { "A", "B", "C" }, vm.Groups[0].Bills.Select(b => b.Name).ToArray());
    }

    [Fact]
    public async Task SortByCommand_SortsByOwedAndRate()
    {
        var db = new FakeDatabaseService();
        db.Bills.Add(MakeBill("1", "A", "Cards", owed: 500, rate: "Yearly"));
        db.Bills.Add(MakeBill("2", "B", "Cards", owed: 100, rate: "Monthly"));
        db.Bills.Add(MakeBill("3", "C", "Cards", owed: 250, rate: "Bi-Weekly"));
        var vm = new AllBillsPageViewModel(db);
        await vm.LoadAsync();

        vm.SortByCommand.Execute("Owed");
        Assert.Equal(new[] { "B", "C", "A" }, vm.Groups[0].Bills.Select(b => b.Name).ToArray());

        vm.SortByCommand.Execute("Rate");
        // Bi-Weekly, Monthly, Yearly alphabetically.
        Assert.Equal(new[] { "C", "B", "A" }, vm.Groups[0].Bills.Select(b => b.Name).ToArray());
    }

    [Fact]
    public async Task SortByCommand_UnknownColumn_IsNoOp()
    {
        var db = new FakeDatabaseService();
        db.Bills.Add(MakeBill("1", "X", "Cards", cost: 10));
        db.Bills.Add(MakeBill("2", "Y", "Cards", cost: 20));
        var vm = new AllBillsPageViewModel(db);
        await vm.LoadAsync();

        var beforeColumn = vm.SortColumn;
        var beforeAsc = vm.SortAscending;
        var beforeOrder = vm.Groups[0].Bills.Select(b => b.Name).ToArray();

        vm.SortByCommand.Execute("NotARealColumn");

        Assert.Equal(beforeColumn, vm.SortColumn);
        Assert.Equal(beforeAsc, vm.SortAscending);
        Assert.Equal(beforeOrder, vm.Groups[0].Bills.Select(b => b.Name).ToArray());
    }

    [Fact]
    public async Task LoadAsync_PreservesCurrentSortState()
    {
        var db = new FakeDatabaseService();
        db.Bills.Add(MakeBill("1", "Amazon", "Cards", cost: 30));
        db.Bills.Add(MakeBill("2", "Best Buy", "Cards", cost: 10));
        var vm = new AllBillsPageViewModel(db);
        await vm.LoadAsync();

        vm.SortByCommand.Execute("Cost");
        vm.SortByCommand.Execute("Cost"); // descending by cost
        Assert.Equal(new[] { "Amazon", "Best Buy" }, vm.Groups[0].Bills.Select(b => b.Name).ToArray());

        // Add a new bill and refresh — sort state must survive.
        db.Bills.Add(MakeBill("3", "Chase", "Cards", cost: 20));
        await vm.RefreshCommand.ExecuteAsync(null);

        Assert.Equal(AllBillsSortColumn.Cost, vm.SortColumn);
        Assert.False(vm.SortAscending);
        Assert.Equal(new[] { "Amazon", "Chase", "Best Buy" }, vm.Groups[0].Bills.Select(b => b.Name).ToArray());
    }
}
