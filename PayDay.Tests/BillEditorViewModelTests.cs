using PayDay.Models;
using PayDay.ViewModels;

namespace PayDay.Tests;

public class BillEditorViewModelTests
{
    [Fact]
    public void Ctor_PopulatesFieldsFromBill()
    {
        var bill = new Bill
        {
            Id = "abc", Name = "Electric", Type = "Bills", Payment =400,
            Remaining =50, Available = 0, CreditLimit = 0, DueDay = 8,
            Rate = "Monthly", APR = 0, AutoPay = false, Active = true,
            YearlyDate = null, Notes = "Variable",
        };

        var vm = new BillEditorViewModel(bill, isAddMode: false);

        Assert.Equal("Electric", vm.Name);
        Assert.Equal("Bills", vm.Type);
        Assert.Equal(400, vm.Payment);
        Assert.Equal(8, vm.DueDay);
        Assert.Equal("Variable", vm.Notes);
        Assert.True(vm.Active);
        Assert.False(vm.IsAddMode);
        Assert.Equal("Edit Electric", vm.Title);
    }

    [Fact]
    public void Ctor_AddMode_DefaultsTypeAndRate()
    {
        var bill = new Bill { Id = "new" };
        var vm = new BillEditorViewModel(bill, isAddMode: true);

        Assert.Equal("Bills", vm.Type);
        Assert.Equal("Monthly", vm.Rate);
        Assert.Equal("Add bill", vm.Title);
    }

    [Fact]
    public void CanSave_FalseWhenNameEmpty()
    {
        var bill = new Bill { Name = "" };
        var vm = new BillEditorViewModel(bill, isAddMode: true);

        Assert.False(vm.CanSave);

        vm.Name = "Something";
        Assert.True(vm.CanSave);

        vm.Name = "   ";
        Assert.False(vm.CanSave);
    }

    [Fact]
    public void ApplyToOriginal_CopiesFieldsBack()
    {
        var bill = new Bill { Id = "abc", Name = "Old", Type = "Bills", Payment =100 };
        var vm = new BillEditorViewModel(bill, isAddMode: false)
        {
            Name = "New Name",
            Type = "Cards",
            Payment =250,
            DueDay = 15,
            AutoPay = true,
            Notes = "  trimmed  ",
        };

        vm.ApplyToOriginal();

        Assert.Equal("abc", bill.Id); // Id untouched
        Assert.Equal("New Name", bill.Name);
        Assert.Equal("Cards", bill.Type);
        Assert.Equal(250, bill.Payment);
        Assert.Equal(15, bill.DueDay);
        Assert.True(bill.AutoPay);
        Assert.Equal("trimmed", bill.Notes); // whitespace trimmed
    }

    [Fact]
    public void ApplyToOriginal_ClampsDueDayToValidRange()
    {
        var bill = new Bill { Name = "X" };
        var vm = new BillEditorViewModel(bill, isAddMode: false) { Name = "X", DueDay = 99 };
        vm.ApplyToOriginal();
        Assert.Equal(31, bill.DueDay);

        vm.DueDay = -5;
        vm.ApplyToOriginal();
        Assert.Equal(1, bill.DueDay);
    }

    [Fact]
    public void Ctor_ExtraTypes_AreAddedToTypeOptionsAlongsideKnown()
    {
        var bill = new Bill { Id = "x", Name = "X" };
        var vm = new BillEditorViewModel(bill, isAddMode: true, extraTypes: new[] { "Crypto", "Cards", "Animal Care" });

        // KnownTypes (8) + 2 new customs ("Crypto", "Animal Care"). "Cards" is deduped.
        Assert.Equal(BillEditorViewModel.KnownTypes.Length + 2, vm.TypeOptions.Count);
        Assert.Contains("Crypto", vm.TypeOptions);
        Assert.Contains("Animal Care", vm.TypeOptions);
    }

    [Fact]
    public void Ctor_BillTypeNotInKnownOrExtras_IsStillSurfacedInTypeOptions()
    {
        var bill = new Bill { Id = "x", Name = "X", Type = "Legacy" };
        var vm = new BillEditorViewModel(bill, isAddMode: false);

        Assert.Contains("Legacy", vm.TypeOptions);
    }

    [Fact]
    public void ApplyToOriginal_BlankNotesAndYearlyDateBecomeNull()
    {
        var bill = new Bill { Name = "X", Notes = "old", YearlyDate = "12-25" };
        var vm = new BillEditorViewModel(bill, isAddMode: false)
        {
            Name = "X",
            Notes = "   ",
            YearlyDate = "",
        };

        vm.ApplyToOriginal();

        Assert.Null(bill.Notes);
        Assert.Null(bill.YearlyDate);
    }
}
