using System.Threading.Tasks;
using PayDay.Services;

namespace PayDay.Tests;

public class TypeColorServiceTests
{
    public TypeColorServiceTests()
    {
        // The service holds process-wide static state; reset before each test so
        // tests can run in any order without leaking overrides between them.
        TypeColorService.ResetInMemory();
    }

    [Fact]
    public void GetHex_NoOverrides_ReturnsBuiltInDefault()
    {
        Assert.Equal(TypeColorService.Defaults["Cards"], TypeColorService.GetHex("Cards"));
        Assert.Equal(TypeColorService.Defaults["Other"], TypeColorService.GetHex("Cards" + "-unknown"));
    }

    [Fact]
    public async Task SetHex_PersistsToDb_AndChangesGetHex()
    {
        var db = new FakeDatabaseService();
        await TypeColorService.SetHexAsync(db, "Cards", "#112233");

        Assert.Equal("#112233", TypeColorService.GetHex("Cards"));
        Assert.NotNull(db.Settings[TypeColorService.SettingKey]);
        Assert.Contains("112233", db.Settings[TypeColorService.SettingKey]!);
    }

    [Fact]
    public async Task SetHex_InvalidHex_IsIgnored()
    {
        var db = new FakeDatabaseService();
        await TypeColorService.SetHexAsync(db, "Cards", "not-a-color");
        Assert.Equal(TypeColorService.Defaults["Cards"], TypeColorService.GetHex("Cards"));
    }

    [Fact]
    public async Task LoadAsync_ReadsExistingSettingAndPopulatesOverrides()
    {
        var db = new FakeDatabaseService();
        db.Settings[TypeColorService.SettingKey] = """{"Cards":"#AABBCC","Bills":"#001122"}""";

        await TypeColorService.LoadAsync(db);

        Assert.Equal("#AABBCC", TypeColorService.GetHex("Cards"));
        Assert.Equal("#001122", TypeColorService.GetHex("Bills"));
        // Untouched types still get the default.
        Assert.Equal(TypeColorService.Defaults["Loans"], TypeColorService.GetHex("Loans"));
    }

    [Fact]
    public async Task ResetAsync_RemovesOverrideAndRevertsToDefault()
    {
        var db = new FakeDatabaseService();
        await TypeColorService.SetHexAsync(db, "Cards", "#112233");
        Assert.Equal("#112233", TypeColorService.GetHex("Cards"));

        await TypeColorService.ResetAsync(db, "Cards");
        Assert.Equal(TypeColorService.Defaults["Cards"], TypeColorService.GetHex("Cards"));
    }

    [Fact]
    public async Task LoadAsync_MalformedJson_IsSwallowedAndDefaultsApply()
    {
        var db = new FakeDatabaseService();
        db.Settings[TypeColorService.SettingKey] = "not valid json{";

        await TypeColorService.LoadAsync(db);

        Assert.Equal(TypeColorService.Defaults["Cards"], TypeColorService.GetHex("Cards"));
    }
}
