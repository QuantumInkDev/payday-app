using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PayDay.Tests.Conformance;

/// <summary>
/// Loads the shared conformance fixtures from <c>spec/conformance/</c>. The csproj copies every
/// <c>spec/conformance/**/*.json</c> file into <c>conformance/</c> under the test output directory,
/// so the same lookup works on the Mac and on the Windows VM. Only the <c>core/</c> folder is read
/// here; other folders (for example <c>sync/</c>) are ignored until a test class claims them.
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

    public static string OutputCoreDirectory =>
        Path.Combine(AppContext.BaseDirectory, "conformance", CoreFolder);

    public static JsonObject LoadFile(string fileName)
    {
        lock (CacheLock)
        {
            if (!Cache.TryGetValue(fileName, out var root))
            {
                var path = Path.Combine(OutputCoreDirectory, fileName);
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
    /// folder (the source files, not the copies). Used only when regenerating expected values.
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
