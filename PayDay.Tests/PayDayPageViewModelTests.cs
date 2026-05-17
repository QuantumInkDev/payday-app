using System;
using System.Threading.Tasks;
using PayDay.Models;
using PayDay.ViewModels;

namespace PayDay.Tests;

public class PayDayPageViewModelTests
{
    // Anchor 2026-03-20 + today 2026-05-22 → current period starts 2026-05-15 (key "2026-05-15"),
    // ends 2026-05-28. A monthly bill with DueDay=20 lands inside that window.
    private static readonly DateTime DefaultAnchor = new(2026, 3, 20);
    private static readonly DateTime DefaultToday = new(2026, 5, 22);
    private const string CurrentPeriodKey = "2026-05-15";

    private static FakeDatabaseService MakeDb(params Bill[] bills)
    {
        var db = new FakeDatabaseService();
        db.Settings["PayAnchor"] = DefaultAnchor.ToString("yyyy-MM-dd");
        db.Bills.AddRange(bills);
        return db;
    }

    private static Bill MakeBill(
        string id, string name, double cost,
        bool autoPay = false, int dueDay = 20, string rate = "Monthly", string type = "Bills")
        => new()
        {
            Id = id, Name = name, Payment = cost, AutoPay = autoPay,
            DueDay = dueDay, Rate = rate, Type = type, Active = true,
        };

    [Fact]
    public async Task LoadAsync_EmptyDb_RendersPeriodsWithNoBills()
    {
        var db = MakeDb();
        var vm = new PayDayPageViewModel(db);

        await vm.LoadAsync(DefaultToday);

        Assert.NotEmpty(vm.Periods);
        Assert.NotNull(vm.CurrentPeriod);
        Assert.Equal(CurrentPeriodKey, vm.CurrentPeriodKey);
        Assert.Empty(vm.UnpaidBills);
        Assert.Empty(vm.PaidBills);
        Assert.Empty(vm.AutoPayBills);
        Assert.Equal(0, vm.TotalDue);
    }

    [Fact]
    public async Task LoadAsync_OneManualBill_ShowsInUnpaid()
    {
        var bill = MakeBill("electric", "Electric", cost: 400, dueDay: 20);
        var db = MakeDb(bill);
        var vm = new PayDayPageViewModel(db);

        await vm.LoadAsync(DefaultToday);

        Assert.Single(vm.UnpaidBills);
        Assert.Empty(vm.PaidBills);
        Assert.Equal(400, vm.TotalDue);
        Assert.Equal(0, vm.TotalPaid);
        Assert.Equal(400, vm.Remaining);
        Assert.False(vm.IsAllPaid);
    }

    [Fact]
    public async Task MarkPaidCommand_MovesRowAndPersistsPayment()
    {
        var bill = MakeBill("electric", "Electric", cost: 400, dueDay: 20);
        var db = MakeDb(bill);
        var vm = new PayDayPageViewModel(db);
        await vm.LoadAsync(DefaultToday);

        var row = Assert.Single(vm.UnpaidBills);
        await vm.MarkPaidCommand.ExecuteAsync(row);

        Assert.Empty(vm.UnpaidBills);
        Assert.Single(vm.PaidBills);
        Assert.True(row.IsPaid);
        Assert.NotNull(row.PaymentId);
        Assert.Equal(400, vm.TotalPaid);
        Assert.Equal(0, vm.Remaining);
        Assert.True(vm.IsAllPaid);
        Assert.Equal(1, vm.ProgressFraction);

        var payment = Assert.Single(db.Payments);
        Assert.Equal("electric", payment.BillId);
        Assert.Equal(CurrentPeriodKey, payment.PeriodKey);
        Assert.Equal(400, payment.AmountPaid);
    }

    [Fact]
    public async Task UnmarkPaidCommand_RestoresRowAndDeletesPayment()
    {
        var bill = MakeBill("electric", "Electric", cost: 400, dueDay: 20);
        var db = MakeDb(bill);
        db.Payments.Add(new Payment
        {
            Id = 1, BillId = "electric", PeriodKey = CurrentPeriodKey, AmountPaid = 400,
        });

        var vm = new PayDayPageViewModel(db);
        await vm.LoadAsync(DefaultToday);

        var row = Assert.Single(vm.PaidBills);
        Assert.True(row.IsPaid);

        await vm.UnmarkPaidCommand.ExecuteAsync(row);

        Assert.Single(vm.UnpaidBills);
        Assert.Empty(vm.PaidBills);
        Assert.False(row.IsPaid);
        Assert.Null(row.PaymentId);
        Assert.Empty(db.Payments);
    }

    [Fact]
    public async Task LoadAsync_AutoPayBill_IsolatedFromManualLists()
    {
        // Both bills due day 20 → both land in the 5-15..5-28 window for Monthly rate.
        var auto = MakeBill("spotify", "Spotify", cost: 10.65, autoPay: true, dueDay: 20, type: "Subscriptions");
        var manual = MakeBill("electric", "Electric", cost: 400, dueDay: 20);
        var db = MakeDb(auto, manual);
        db.Payments.Add(new Payment
        {
            Id = 1, BillId = "electric", PeriodKey = CurrentPeriodKey, AmountPaid = 400,
        });

        var vm = new PayDayPageViewModel(db);
        await vm.LoadAsync(DefaultToday);

        Assert.Single(vm.AutoPayBills);
        Assert.Empty(vm.UnpaidBills);
        Assert.Single(vm.PaidBills);
        Assert.True(vm.IsAllPaid); // Auto-pay doesn't block the all-paid state
        Assert.Equal(10.65, vm.AutoPayTotal);
        Assert.Equal(400, vm.TotalDue); // Auto-pay total isn't part of manual TotalDue
    }

    [Fact]
    public async Task MarkAllPaidCommand_ClearsAllManualBills()
    {
        var bill1 = MakeBill("electric", "Electric", cost: 400, dueDay: 20);
        var bill2 = MakeBill("phone", "Phone", cost: 151, dueDay: 24);
        var db = MakeDb(bill1, bill2);
        var vm = new PayDayPageViewModel(db);
        await vm.LoadAsync(DefaultToday);

        Assert.Equal(2, vm.UnpaidBills.Count);
        await vm.MarkAllPaidCommand.ExecuteAsync(null);

        Assert.Empty(vm.UnpaidBills);
        Assert.Equal(2, vm.PaidBills.Count);
        Assert.True(vm.IsAllPaid);
        Assert.Equal(2, db.Payments.Count);
    }

    [Fact]
    public async Task LoadAsync_InactiveBill_IsExcluded()
    {
        var bill = MakeBill("electric", "Electric", cost: 400, dueDay: 20);
        bill.Active = false;
        var db = MakeDb(bill);
        var vm = new PayDayPageViewModel(db);

        await vm.LoadAsync(DefaultToday);

        Assert.Empty(vm.UnpaidBills);
        Assert.Empty(vm.PaidBills);
        Assert.Empty(vm.AutoPayBills);
    }
}
