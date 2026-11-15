using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace PayDay.Tests.Conformance;

/// <summary>
/// Runs every case in <c>spec/conformance/core/*.json</c> against the C# reference implementation.
/// Each fixture case is its own xUnit test case, named by the case's <c>name</c>.
///
/// To regenerate the expected values from the current C# behaviour (after a deliberate rule
/// change, or after adding cases with <c>"expected": null</c>), run:
/// <code>PAYDAY_REGEN_FIXTURES=1 dotnet test apps/windows/PayDay.Tests --filter RegenerateFixtures</code>
/// then review the diff in <c>spec/conformance/core/</c>.
/// </summary>
public class CoreConformanceTests
{
    public static IEnumerable<object[]> PayPeriodCases() => ConformanceFixtures.CaseNames("pay-periods.json");
    public static IEnumerable<object[]> BillAssignmentCases() => ConformanceFixtures.CaseNames("bill-assignment.json");
    public static IEnumerable<object[]> TotalsCases() => ConformanceFixtures.CaseNames("totals.json");
    public static IEnumerable<object[]> PayoffCases() => ConformanceFixtures.CaseNames("payoff.json");
    public static IEnumerable<object[]> BackupCases() => ConformanceFixtures.CaseNames("backup.json");

    [Theory]
    [MemberData(nameof(PayPeriodCases))]
    public Task PayPeriods(string caseName) => CheckAsync("pay-periods.json", caseName);

    [Theory]
    [MemberData(nameof(BillAssignmentCases))]
    public Task BillAssignment(string caseName) => CheckAsync("bill-assignment.json", caseName);

    [Theory]
    [MemberData(nameof(TotalsCases))]
    public Task Totals(string caseName) => CheckAsync("totals.json", caseName);

    [Theory]
    [MemberData(nameof(PayoffCases))]
    public Task Payoff(string caseName) => CheckAsync("payoff.json", caseName);

    [Theory]
    [MemberData(nameof(BackupCases))]
    public Task Backup(string caseName) => CheckAsync("backup.json", caseName);

    private static async Task CheckAsync(string fileName, string caseName)
    {
        var testCase = ConformanceFixtures.FindCase(fileName, caseName);
        var expected = testCase["expected"];
        Assert.True(expected is JsonObject, $"{fileName} / {caseName}: \"expected\" is missing; regenerate the fixtures.");

        var actual = await CoreReference.RunAsync(fileName, testCase["input"]!.AsObject()).ConfigureAwait(true);

        var diffs = JsonCompare.Differences(expected, actual);
        Assert.True(diffs.Count == 0,
            $"{fileName} / {caseName}:\n  " + string.Join("\n  ", diffs));
    }

    [Fact]
    public void CoreFolder_HasExactlyTheKnownFiles_WithWellFormedCases()
    {
        var dir = ConformanceFixtures.CoreDirectory;
        Assert.True(Directory.Exists(dir), $"Fixture folder not found: {dir}");

        var present = Directory.GetFiles(dir, "*.json").Select(Path.GetFileName).OrderBy(n => n).ToArray();
        Assert.Equal(ConformanceFixtures.CoreFiles.OrderBy(n => n).ToArray(), present);

        foreach (var file in ConformanceFixtures.CoreFiles)
        {
            var root = ConformanceFixtures.LoadFile(file);
            Assert.Equal(1, (int?)root["version"]);
            Assert.False(string.IsNullOrWhiteSpace((string?)root["description"]), $"{file}: description is empty");

            var cases = ConformanceFixtures.Cases(root).Select(c => c!.AsObject()).ToList();
            Assert.NotEmpty(cases);
            var names = cases.Select(c => (string?)c["name"]).ToList();
            Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n), $"{file}: a case has no name"));
            var duplicates = names.GroupBy(n => n).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.True(duplicates.Count == 0, $"{file}: duplicate case names: {string.Join(", ", duplicates)}");
            Assert.All(cases, c =>
            {
                var keys = c.Select(p => p.Key).OrderBy(k => k).ToArray();
                Assert.Equal(new[] { "expected", "input", "name" }, keys);
            });
        }
    }

    /// <summary>
    /// Writes C# results into the <c>expected</c> field of every case in the source fixture files.
    /// Does nothing unless <c>PAYDAY_REGEN_FIXTURES=1</c>.
    /// </summary>
    [Fact]
    public async Task RegenerateFixtures()
    {
        if (Environment.GetEnvironmentVariable("PAYDAY_REGEN_FIXTURES") != "1") return;

        var sourceDir = ConformanceFixtures.FindSourceCoreDirectory();
        foreach (var file in ConformanceFixtures.CoreFiles)
        {
            var path = Path.Combine(sourceDir, file);
            var root = ConformanceFixtures.ParseFile(path);
            foreach (var testCase in ConformanceFixtures.Cases(root).Select(c => c!.AsObject()))
            {
                var actual = await CoreReference.RunAsync(file, testCase["input"]!.AsObject()).ConfigureAwait(true);
                testCase["expected"] = JsonCompare.RoundForFixture(actual);
            }
            File.WriteAllText(path, ConformanceFixtures.Serialize(root));
        }
    }
}
