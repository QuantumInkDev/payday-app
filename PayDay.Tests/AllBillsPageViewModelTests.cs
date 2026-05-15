using System.Threading.Tasks;
using PayDay.Models;
using PayDay.ViewModels;

namespace PayDay.Tests;

public class AllBillsPageViewModelTests
{
    private static Bill MakeBill(string id, string name, string type, bool active = true)
        => new() { Id = id, Name = name, Type = type, Active = active };

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
}
