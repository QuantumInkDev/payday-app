using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PayDay.Tests.Conformance;

/// <summary>
/// Loads the shared conformance fixtures straight from the repository's <c>spec/conformance/</c>
/// folder, found by walking up from the test output directory. Fixtures are deliberately not copied
/// into the build output: a copy goes stale (with <c>PreserveNewest</c>, a deleted or renamed
/// fixture stays in <c>bin/</c> until a clean build). The Mac and the VM both run the tests from
/// a full checkout. Only the <c>core/</c> folder is read here; other folders (for example
/// <c>sync/</c>) are ignored until a test class claims them.
/// </summary>
internal static class ConformanceFixtures
{
    public const string CoreFolder = "core";

    /// <summary>The fixture files this suite knows how to run, by file name.</summary>
    public static readonly string[] CoreFiles =
    {
        "pay-periods.json",
        "bill-assignment.json",
        "totals.json",
        "payoff.json",
        "backup.json",
    };

    private static readonly Dictionary<string, JsonObject> Cache = new(StringComparer.Ordinal);
    private static readonly object CacheLock = new();

    private static readonly Lazy<string> SourceCoreDirectory = new(FindSourceCoreDirectory);

    /// <summary>The <c>spec/conformance/core</c> folder in the source tree.</summary>
    public static string CoreDirectory => SourceCoreDirectory.Value;

    public static JsonObject LoadFile(string fileName)
    {
        lock (CacheLock)
        {
            if (!Cache.TryGetValue(fileName, out var root))
            {
                var path = Path.Combine(CoreDirectory, fileName);
                root = ParseFile(path);
                Cache[fileName] = root;
            }
            return root;
        }
    }

    public static JsonObject ParseFile(string path)
    {
        var text = File.ReadAllText(path);
        return JsonNode.Parse(text) as JsonObject
            ?? throw new InvalidDataException($"{path}: root must be a JSON object.");
    }

    public static JsonArray Cases(JsonObject file)
        => file["cases"] as JsonArray
            ?? throw new InvalidDataException("Fixture file has no \"cases\" array.");

    /// <summary>xUnit MemberData: one row per case name, so each fixture case is its own test.</summary>
    public static IEnumerable<object[]> CaseNames(string fileName)
        => Cases(LoadFile(fileName))
            .Select(c => (string)c!["name"]!)
            .Select(name => new object[] { name });

    public static JsonObject FindCase(string fileName, string caseName)
    {
        var match = Cases(LoadFile(fileName))
            .OfType<JsonObject>()
            .SingleOrDefault(c => (string?)c["name"] == caseName);
        return match ?? throw new InvalidDataException($"{fileName}: no case named \"{caseName}\".");
    }

    /// <summary>
    /// Walks up from the test output directory to the repository's <c>spec/conformance/core</c>
    /// folder. Used for loading and for regenerating expected values.
    /// </summary>
    public static string FindSourceCoreDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "spec", "conformance", CoreFolder);
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not find spec/conformance/core above " + AppContext.BaseDirectory);
    }

    public static string Serialize(JsonNode node)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NewLine = "\n",
        };
        return node.ToJsonString(options) + "\n";
    }
}
