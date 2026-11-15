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
/// a full checkout. The <c>core/</c> folder is run by <see cref="CoreConformanceTests"/> and the
/// <c>sync/</c> folder by <see cref="SyncConformanceTests"/>.
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

    public const string SyncFolder = "sync";

    /// <summary>The sync fixture files (spec/README.md, "Operations in conformance/sync/").</summary>
    public static readonly string[] SyncFiles =
    {
        "hlc.json",
        "ids.json",
        "crypto.json",
        "recovery-phrase.json",
        "files.json",
        "merge.json",
        "compaction.json",
    };

    private static readonly Dictionary<string, JsonObject> Cache = new(StringComparer.Ordinal);
    private static readonly object CacheLock = new();

    private static readonly Lazy<string> SourceCoreDirectory = new(FindSourceCoreDirectory);
    private static readonly Lazy<string> SourceSyncDirectory = new(() => FindSourceDirectory(SyncFolder));

    /// <summary>The <c>spec/conformance/core</c> folder in the source tree.</summary>
    public static string CoreDirectory => SourceCoreDirectory.Value;

    /// <summary>The <c>spec/conformance/sync</c> folder in the source tree.</summary>
    public static string SyncDirectory => SourceSyncDirectory.Value;

    public static JsonObject LoadFile(string fileName) => LoadFile(CoreFolder, fileName);

    public static JsonObject LoadFile(string folder, string fileName)
    {
        var cacheKey = folder + "/" + fileName;
        lock (CacheLock)
        {
            if (!Cache.TryGetValue(cacheKey, out var root))
            {
                var dir = folder == CoreFolder ? CoreDirectory : folder == SyncFolder ? SyncDirectory : FindSourceDirectory(folder);
                root = ParseFile(Path.Combine(dir, fileName));
                Cache[cacheKey] = root;
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

    /// <summary>xUnit MemberData for a <c>sync/</c> fixture file: one row per case name.</summary>
    public static IEnumerable<object[]> SyncCaseNames(string fileName)
        => Cases(LoadFile(SyncFolder, fileName))
            .Select(c => (string)c!["name"]!)
            .Select(name => new object[] { name });

    public static JsonObject FindSyncCase(string fileName, string caseName)
    {
        var match = Cases(LoadFile(SyncFolder, fileName))
            .OfType<JsonObject>()
            .SingleOrDefault(c => (string?)c["name"] == caseName);
        return match ?? throw new InvalidDataException($"sync/{fileName}: no case named \"{caseName}\".");
    }

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
    public static string FindSourceCoreDirectory() => FindSourceDirectory(CoreFolder);

    /// <summary>Walks up from the test output directory to <c>spec/conformance/&lt;folder&gt;</c>.</summary>
    public static string FindSourceDirectory(string folder)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "spec", "conformance", folder);
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException($"Could not find spec/conformance/{folder} above " + AppContext.BaseDirectory);
    }

    /// <summary>The repository's <c>spec/</c> folder (for files such as <c>bip39-english.txt</c>).</summary>
    public static string SpecDirectory => Path.GetDirectoryName(Path.GetDirectoryName(SyncDirectory)!)!;

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
