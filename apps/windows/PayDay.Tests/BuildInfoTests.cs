using System;
using System.Globalization;
using PayDay.Services;

namespace PayDay.Tests;

public class BuildInfoTests
{
    [Fact]
    public void Parse_VersionWithFullCommit_ShortensCommit()
    {
        var info = BuildInfo.Parse("0.7.0+3F2A9C1D0E5B6A7980C1D2E3F4A5B6C7D8E9F001", null);

        Assert.Equal("0.7.0", info.Version);
        Assert.Equal("3f2a9c1", info.Commit);
    }

    [Fact]
    public void Parse_VersionOnly_HasNoCommit()
    {
        var info = BuildInfo.Parse("0.7.0", null);

        Assert.Equal("0.7.0", info.Version);
        Assert.Null(info.Commit);
    }

    [Theory]
    [InlineData("0.7.0+", "0.7.0", null)]
    [InlineData(" 0.7.0+abc ", "0.7.0", "abc")]
    [InlineData("0.7.0+local.dev", "0.7.0", "local.dev")]
    [InlineData("0.7.0+20261128", "0.7.0", "20261128")]
    [InlineData("0.7.0+abcdef0123", "0.7.0", "abcdef0123")]
    [InlineData("", "0.0.0", null)]
    public void Parse_EdgeCases(string input, string version, string? commit)
    {
        var info = BuildInfo.Parse(input, null);

        Assert.Equal(version, info.Version);
        Assert.Equal(commit, info.Commit);
    }

    [Fact]
    public void FormatBuiltAt_ConvertsToTheGivenZone()
    {
        var info = new BuildInfo("0.7.0", "abc1234", new DateTimeOffset(2026, 11, 28, 1, 55, 0, TimeSpan.Zero));
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test-5", TimeSpan.FromHours(-5), "Test-5", "Test-5");

        Assert.Equal("Nov 27, 2026 20:55", info.FormatBuiltAt(zone, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void FormatBuiltAt_TwentyFourHourCulture_HasNoDanglingAmPm()
    {
        var info = new BuildInfo("0.7.0", null, new DateTimeOffset(2026, 11, 28, 13, 55, 0, TimeSpan.Zero));

        var text = info.FormatBuiltAt(TimeZoneInfo.Utc, CultureInfo.GetCultureInfo("de-DE"));

        Assert.EndsWith(" 13:55", text);
    }

    [Fact]
    public void FormatBuiltAt_Unknown()
    {
        var info = new BuildInfo("0.7.0", null, null);

        Assert.Equal("unknown", info.FormatBuiltAt(TimeZoneInfo.Utc, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void DetailLines_ListsEveryField()
    {
        var info = new BuildInfo("0.7.0", null, null);

        var lines = info.DetailLines("X64", "10.0.26100", ".NET 9.0.0", TimeZoneInfo.Utc, CultureInfo.InvariantCulture);

        Assert.Equal(
            ["FutureCheck 0.7.0", "Commit: unknown", "Built: unknown", "Architecture: X64", "Windows: 10.0.26100", "Runtime: .NET 9.0.0"],
            lines);
    }

    [Fact]
    public void FromAssembly_ReadsTheTestAssembly()
    {
        var info = BuildInfo.FromAssembly(typeof(BuildInfoTests).Assembly);

        Assert.False(string.IsNullOrWhiteSpace(info.Version));
        Assert.NotNull(info.BuiltAt);
    }
}
