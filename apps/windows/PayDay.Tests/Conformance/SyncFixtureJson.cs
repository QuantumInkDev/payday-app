using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using PayDay.Sync.Json;
using PayDay.Sync.Model;

namespace PayDay.Tests.Conformance;

/// <summary>
/// Conversions between the fixture files (read with System.Text.Json) and the sync library's own
/// JSON model, plus the comparison rules of <c>spec/README.md</c> "Value conventions in fixtures":
/// numbers compare by value, booleans never equal numbers, objects need the same keys, lists are
/// ordered, and money results (<c>remaining</c>, <c>setAsideBalance</c>) compare after rounding to cents.
/// </summary>
internal static class SyncFixtureJson
{
    /// <summary>Converts a fixture node to the library's <see cref="JsonData"/> without going through the strict parser.</summary>
    public static JsonData ToData(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return JsonData.Null;
            case JsonObject obj:
                return JsonData.Object(obj.Select(p => new KeyValuePair<string, JsonData>(p.Key, ToData(p.Value))));
            case JsonArray arr:
                return JsonData.Array(arr.Select(ToData));
            default:
                return node.GetValueKind() switch
                {
                    JsonValueKind.True => JsonData.True,
                    JsonValueKind.False => JsonData.False,
                    JsonValueKind.String => JsonData.String(node.GetValue<string>()),
                    JsonValueKind.Number => JsonData.Number(Number(node)),
                    _ => JsonData.Null,
                };
        }
    }

    public static JsonNode? ToNode(SyncValue value)
        => value.Kind switch
        {
            SyncValueKind.Null => null,
            SyncValueKind.Bool => JsonValue.Create(value.BoolValue),
            SyncValueKind.Number => JsonValue.Create(value.NumberValue),
            _ => JsonValue.Create(value.StringValue),
        };

    public static JsonObject ToNode(ChangeRecord r)
        => new()
        {
            ["entity"] = r.Entity,
            ["id"] = r.Id,
            ["field"] = r.Field,
            ["value"] = ToNode(r.Value),
            ["hlc"] = r.Hlc.Text,
        };

    public static JsonArray ToNode(IEnumerable<ChangeRecord> records) => new(records.Select(r => (JsonNode?)ToNode(r)).ToArray());

    public static JsonArray Strings(IEnumerable<string> items) => new(items.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());

    public static JsonArray Longs(IEnumerable<long> items) => new(items.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());

    public static JsonObject LongMap(IEnumerable<KeyValuePair<string, long>> map)
    {
        var o = new JsonObject();
        foreach (var (k, v) in map) o[k] = v;
        return o;
    }

    public static double Number(JsonNode node)
        => double.Parse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture);

    public static long Long(JsonNode? node)
    {
        if (node is null) throw new InvalidOperationException("Expected an integer, got null.");
        var d = Number(node);
        if (Math.Floor(d) != d) throw new InvalidOperationException($"Expected an integer, got {d}.");
        return (long)d;
    }

    public static long? NullableLong(JsonNode? node) => node is null ? null : Long(node);

    public static string Str(JsonNode? node) => node?.GetValue<string>() ?? throw new InvalidOperationException("Expected a string.");

    public static string? NullableStr(JsonNode? node) => node?.GetValue<string>();

    public static byte[] Hex(JsonNode? node) => Convert.FromHexString(Str(node));

    public static IReadOnlyDictionary<string, long> LongDict(JsonNode? node)
        => node is JsonObject o
            ? o.ToDictionary(p => p.Key, p => Long(p.Value), StringComparer.Ordinal)
            : new Dictionary<string, long>(StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, IReadOnlyCollection<long>> LongListDict(JsonNode? node)
        => node is JsonObject o
            ? o.ToDictionary(p => p.Key, p => (IReadOnlyCollection<long>)p.Value!.AsArray().Select(Long).ToArray(), StringComparer.Ordinal)
            : new Dictionary<string, IReadOnlyCollection<long>>(StringComparer.Ordinal);

    /// <summary>Differences between an expected and an actual value; empty when they match.</summary>
    public static IReadOnlyList<string> Differences(JsonNode? expected, JsonNode? actual, params string[] centsKeys)
    {
        var diffs = new List<string>();
        Compare(expected, actual, "$", cents: false, new HashSet<string>(centsKeys, StringComparer.Ordinal), diffs);
        return diffs;
    }

    private static void Compare(JsonNode? e, JsonNode? a, string path, bool cents, HashSet<string> centsKeys, List<string> diffs)
    {
        var ek = e?.GetValueKind() ?? JsonValueKind.Null;
        var ak = a?.GetValueKind() ?? JsonValueKind.Null;
        if (ek == JsonValueKind.Number && ak == JsonValueKind.Number)
        {
            var ev = Number(e!);
            var av = Number(a!);
            var same = cents ? SyncMoney.Cents(ev) == SyncMoney.Cents(av) : ev == av;
            if (!same) diffs.Add($"{path}: expected {Show(e)}, got {Show(a)}");
            return;
        }
        var eb = ek is JsonValueKind.True or JsonValueKind.False;
        var ab = ak is JsonValueKind.True or JsonValueKind.False;
        if (ek != ak && !(eb && ab))
        {
            diffs.Add($"{path}: expected {Show(e)}, got {Show(a)}");
            return;
        }
        switch (ek)
        {
            case JsonValueKind.Object:
            {
                var eo = e!.AsObject();
                var ao = a!.AsObject();
                foreach (var (key, value) in eo)
                {
                    if (!ao.ContainsKey(key))
                    {
                        diffs.Add($"{path}.{key}: missing (expected {Show(value)})");
                        continue;
                    }
                    Compare(value, ao[key], $"{path}.{key}", cents || centsKeys.Contains(key), centsKeys, diffs);
                }
                foreach (var (key, value) in ao)
                {
                    if (!eo.ContainsKey(key)) diffs.Add($"{path}.{key}: unexpected (value {Show(value)})");
                }
                break;
            }
            case JsonValueKind.Array:
            {
                var ea = e!.AsArray();
                var aa = a!.AsArray();
                if (ea.Count != aa.Count)
                {
                    diffs.Add($"{path}: expected {ea.Count} items {Show(e)}, got {aa.Count} {Show(a)}");
                    return;
                }
                for (var i = 0; i < ea.Count; i++) Compare(ea[i], aa[i], $"{path}[{i}]", cents, centsKeys, diffs);
                break;
            }
            case JsonValueKind.Null:
                break;
            default:
                if (!JsonNode.DeepEquals(e, a)) diffs.Add($"{path}: expected {Show(e)}, got {Show(a)}");
                break;
        }
    }

    private static string Show(JsonNode? n)
    {
        var s = n?.ToJsonString() ?? "null";
        return s.Length > 300 ? s[..300] + "..." : s;
    }
}
