using System;
using System.Linq;
using System.Threading.Tasks;
using PayDay.Models;
using PayDay.ViewModels;

namespace PayDay.Tests;

public class InsightsPageViewModelTests
{
    private static Bill MakeBill(
        string id, string name, double cost,
        string type = "Bills", double owed = 0, bool active = true)
        => new()
        {
            Id = id, Name = name, Type = type, Payment = cost, Remaining = owed,
            Active = active, Rate = "Monthly", DueDay = 1,
        };

    [Fact]
    public async Task LoadAsync_CurrentTotalOwed_SumsActiveBills()
    {
        var db = new FakeDatabaseService();
        db.Bills.Add(MakeBill("a", "A", cost: 50, owed: 100));
        db.Bills.Add(MakeBill("b", "B", cost: 50, owed: 250));
        db.Bills.Add(MakeBill("c", "C", cost: 50, owed: 999, active: false));

        var vm = new InsightsPageViewModel(db);
        await vm.LoadAsync();

        Assert.Equal(350, vm.CurrentTotalRemaining);
    }

    [Fact]
    public async Task LoadAsync_History_OrderedByDateAscending()
    {
        var db = new FakeDatabaseService();
        db.Snapshots.Add(new Snapshot { Id = 1, SnapshotDate = "2026-03-15", TotalRemaining =500 });
        db.Snapshots.Add(new Snapshot { Id = 2, SnapshotDate = "2026-01-01", TotalRemaining =100 });
        db.Snapshots.Add(new Snapshot { Id = 3, SnapshotDate = "2026-02-15", TotalRemaining =300 });

        var vm = new InsightsPageViewModel(db);
        await vm.LoadAsync();

        Assert.Equal(3, vm.SnapshotCount);
        Assert.Equal(new[] { 100.0, 300, 500 }, vm.History.Select(p => p.TotalRemaining));
        Assert.True(vm.History[0].Date < vm.History[1].Date);
    }

    [Fact]
    public async Task LoadAsync_TypeBreakdown_SumsAndPercents()
    {
        var db = new FakeDatabaseService();
        db.Bills.Add(MakeBill("a", "A", cost: 100, type: "Cards"));
        db.Bills.Add(MakeBill("b", "B", cost: 100, type: "Cards"));
        db.Bills.Add(MakeBill("c", "C", cost: 50, type: "Bills"));
        db.Bills.Add(MakeBill("d", "D", cost: 50, type: "Loans"));

        var vm = new InsightsPageViewModel(db);
        await vm.LoadAsync();

        // Total = 300. Cards = 200 (66.7%), Bills = 50 (16.7%), Loans = 50 (16.7%).
        Assert.Equal(3, vm.TypeBreakdown.Count);
        Assert.Equal("Cards", vm.TypeBreakdown[0].Type);
        Assert.Equal(200, vm.TypeBreakdown[0].TotalPayment);
        Assert.Equal(2, vm.TypeBreakdown[0].Count);
        Assert.Equal(66.7, vm.TypeBreakdown[0].Percent);
    }

    [Fact]
    public async Task LoadAsync_TypeBreakdown_OrderedByTotalDescending()
    {
        var db = new FakeDatabaseService();
        db.Bills.Add(MakeBill("a", "A", cost: 10, type: "Bills"));
        db.Bills.Add(MakeBill("b", "B", cost: 200, type: "Cards"));
        db.Bills.Add(MakeBill("c", "C", cost: 50, type: "Loans"));

        var vm = new InsightsPageViewModel(db);
        await vm.LoadAsync();

        Assert.Equal(new[] { "Cards", "Loans", "Bills" }, vm.TypeBreakdown.Select(e => e.Type));
    }

    [Fact]
    public async Task LoadAsync_TypeBreakdown_SkipsTypesWithZeroCost()
    {
        var db = new FakeDatabaseService();
        db.Bills.Add(MakeBill("a", "A", cost: 100, type: "Cards"));
        db.Bills.Add(MakeBill("b", "B", cost: 0, type: "People"));

        var vm = new InsightsPageViewModel(db);
        await vm.LoadAsync();

        Assert.Single(vm.TypeBreakdown);
        Assert.Equal("Cards", vm.TypeBreakdown[0].Type);
    }

    [Fact]
    public async Task SaveSnapshotAsync_InsertsAndReloads()
    {
        var db = new FakeDatabaseService();
        db.Bills.Add(MakeBill("a", "A", cost: 50, owed: 500));
        var vm = new InsightsPageViewModel(db);
        await vm.LoadAsync();

        Assert.Equal(0, vm.SnapshotCount);
        var id = await vm.SaveSnapshotAsync(new DateTime(2026, 5, 15));

        Assert.True(id > 0);
        Assert.Equal(1, vm.SnapshotCount);
        Assert.Single(vm.History);
        Assert.Equal(500, vm.History[0].TotalRemaining);
        Assert.Equal(new DateTime(2026, 5, 15), vm.History[0].Date);
        Assert.Single(db.Snapshots);
        Assert.Contains("\"a\":500.00", db.Snapshots[0].Details);
    }

    [Fact]
    public async Task SummaryLabel_PluralizesCorrectly()
    {
        var db = new FakeDatabaseService();
        var vm = new InsightsPageViewModel(db);

        await vm.LoadAsync();
        Assert.Contains("No snapshots", vm.SummaryLabel);

        db.Snapshots.Add(new Snapshot { SnapshotDate = "2026-01-01", TotalRemaining =100 });
        await vm.LoadAsync();
        Assert.Equal("1 snapshot saved", vm.SummaryLabel);

        db.Snapshots.Add(new Snapshot { SnapshotDate = "2026-02-01", TotalRemaining =100 });
        await vm.LoadAsync();
        Assert.Equal("2 snapshots saved", vm.SummaryLabel);
    }

    [Fact]
    public async Task HasSnapshots_FlipsWhenFirstSaved()
    {
        var db = new FakeDatabaseService();
        db.Bills.Add(MakeBill("a", "A", cost: 50, owed: 100));
        var vm = new InsightsPageViewModel(db);
        await vm.LoadAsync();

        Assert.False(vm.HasSnapshots);
        await vm.SaveSnapshotAsync(new DateTime(2026, 5, 15));
        Assert.True(vm.HasSnapshots);
    }

    [Fact]
    public async Task DeleteSnapshotAsync_RemovesOneAndReloadsHistory()
    {
        var db = new FakeDatabaseService();
        db.Snapshots.Add(new Snapshot { Id = 1, SnapshotDate = "2026-01-01", TotalRemaining = 100 });
        db.Snapshots.Add(new Snapshot { Id = 2, SnapshotDate = "2026-02-01", TotalRemaining = 200 });
        var vm = new InsightsPageViewModel(db);
        await vm.LoadAsync();
        Assert.Equal(2, vm.SnapshotCount);

        await vm.DeleteSnapshotAsync(1);

        Assert.Equal(1, vm.SnapshotCount);
        Assert.Single(vm.History);
        Assert.Equal(200, vm.History[0].TotalRemaining);
        Assert.Single(db.Snapshots);
    }

    [Fact]
    public async Task ClearAllSnapshotsAsync_EmptiesEverything()
    {
        var db = new FakeDatabaseService();
        db.Snapshots.Add(new Snapshot { Id = 1, SnapshotDate = "2026-01-01", TotalRemaining = 100 });
        db.Snapshots.Add(new Snapshot { Id = 2, SnapshotDate = "2026-02-01", TotalRemaining = 200 });
        var vm = new InsightsPageViewModel(db);
        await vm.LoadAsync();

        await vm.ClearAllSnapshotsAsync();

        Assert.Equal(0, vm.SnapshotCount);
        Assert.Empty(vm.History);
        Assert.Empty(db.Snapshots);
    }

    [Fact]
    public async Task LoadAsync_SnapshotsList_OrderedNewestFirst()
    {
        var db = new FakeDatabaseService();
        db.Snapshots.Add(new Snapshot { Id = 1, SnapshotDate = "2026-01-01", TotalRemaining = 100 });
        db.Snapshots.Add(new Snapshot { Id = 2, SnapshotDate = "2026-03-15", TotalRemaining = 50 });
        db.Snapshots.Add(new Snapshot { Id = 3, SnapshotDate = "2026-02-15", TotalRemaining = 75 });
        var vm = new InsightsPageViewModel(db);

        await vm.LoadAsync();

        Assert.Equal(new[] { "2026-03-15", "2026-02-15", "2026-01-01" }, vm.SnapshotsList.Select(s => s.SnapshotDate));
    }

    [Fact]
    public async Task LoadAsync_EmptyDb_LeavesAllListsEmpty()
    {
        var db = new FakeDatabaseService();
        var vm = new InsightsPageViewModel(db);

        await vm.LoadAsync();

        Assert.Equal(0, vm.CurrentTotalRemaining);
        Assert.Empty(vm.History);
        Assert.Empty(vm.TypeBreakdown);
        Assert.False(vm.HasSnapshots);
    }
}
