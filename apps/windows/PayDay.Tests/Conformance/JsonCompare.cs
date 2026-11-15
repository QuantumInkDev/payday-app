using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PayDay.Tests.Conformance;

/// <summary>
/// Structural JSON comparison used by the conformance suite, implementing the rules in
/// <c>spec/conformance/core/README.md</c>:
/// <list type="bullet">
///   <item>objects must have exactly the same keys (order ignored);</item>
///   <item>arrays must have the same length and order;</item>
///   <item>strings, booleans and null compare exactly;</item>
///   <item>numbers compare within 0.0001 when the property name ends in <c>Fraction</c>,
///     otherwise within 0.005 (half a cent).</item>
/// </list>
/// </summary>
internal static class JsonCompare
{
    public const double MoneyTolerance = 0.005;
    public const double FractionTolerance = 0.0001;

    public static IReadOnlyList<string> Differences(JsonNode? expected, JsonNode? actual)
    {
        var diffs = new List<string>();
        Compare(expected, actual, "$", propertyName: null, diffs);
        return diffs;
    }

    private static void Compare(JsonNode? expected, JsonNode? actual, string path, string? propertyName, List<string> diffs)
    {
        var ek = Kind(expected);
        var ak = Kind(actual);
        if (ek == JsonValueKind.Number && ak == JsonValueKind.Number)
        {
            var e = Number(expected!);
            var a = Number(actual!);
            var tolerance = IsFraction(propertyName) ? FractionTolerance : MoneyTolerance;
            if (Math.Abs(e - a) > tolerance)
            {
                diffs.Add($"{path}: expected {Format(e)}, got {Format(a)}");
            }
            return;
        }
        if (ek != ak && !(IsBool(ek) && IsBool(ak)))
        {
            diffs.Add($"{path}: expected {Describe(expected)}, got {Describe(actual)}");
            return;
        }
        switch (ek)
        {
            case JsonValueKind.Object:
            {
                var eo = expected!.AsObject();
                var ao = actual!.AsObject();
                foreach (var key in eo.Select(p => p.Key))
                {
                    if (!ao.ContainsKey(key))
                    {
                        diffs.Add($"{path}.{key}: missing (expected {Describe(eo[key])})");
                        continue;
                    }
                    Compare(eo[key], ao[key], $"{path}.{key}", key, diffs);
                }
                foreach (var key in ao.Select(p => p.Key).Where(k => !eo.ContainsKey(k)))
                {
                    diffs.Add($"{path}.{key}: unexpected key (value {Describe(ao[key])})");
                }
                break;
            }
            case JsonValueKind.Array:
            {
                var ea = expected!.AsArray();
                var aa = actual!.AsArray();
                if (ea.Count != aa.Count)
                {
                    diffs.Add($"{path}: expected {ea.Count} items, got {aa.Count} (expected {Describe(expected)}, got {Describe(actual)})");
                    return;
                }
                for (var i = 0; i < ea.Count; i++)
                {
                    Compare(ea[i], aa[i], $"{path}[{i}]", propertyName, diffs);
                }
                break;
            }
            case JsonValueKind.String:
            case JsonValueKind.True:
            case JsonValueKind.False:
                if (!JsonNode.DeepEquals(expected, actual))
                {
                    diffs.Add($"{path}: expected {Describe(expected)}, got {Describe(actual)}");
                }
                break;
        }
    }

    public static bool IsFraction(string? propertyName)
        => propertyName is not null && propertyName.EndsWith("Fraction", StringComparison.Ordinal);

    /// <summary>
    /// Rounds every number in a freshly generated expected value: 4 decimals for
    /// <c>*Fraction</c> properties, 2 decimals for everything else (money and percentages).
    /// Integers stay integers.
    /// </summary>
    public static JsonNode? RoundForFixture(JsonNode? node, string? propertyName = null)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonObject obj:
            {
                var result = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    result[key] = RoundForFixture(value, key);
                }
                return result;
            }
            case JsonArray arr:
            {
                var result = new JsonArray();
                foreach (var item in arr)
                {
                    result.Add(RoundForFixture(item, propertyName));
                }
                return result;
            }
            case JsonValue value when value.GetValueKind() == JsonValueKind.Number:
            {
                var d = Number(value);
                var digits = IsFraction(propertyName) ? 4 : 2;
                var rounded = Math.Round(d, digits, MidpointRounding.AwayFromZero);
                if (rounded == Math.Floor(rounded) && Math.Abs(rounded) < 1e15)
                {
                    return JsonValue.Create((long)rounded);
                }
                return JsonValue.Create(rounded);
            }
            default:
                return node.DeepClone();
        }
    }

    private static JsonValueKind Kind(JsonNode? node) => node?.GetValueKind() ?? JsonValueKind.Null;

    /// <summary>Reads any JSON number (whether it came from an int, long, double or parsed text) as a double.</summary>
    private static double Number(JsonNode node)
        => double.Parse(node.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);

    private static bool IsBool(JsonValueKind k) => k is JsonValueKind.True or JsonValueKind.False;

    private static string Describe(JsonNode? node)
    {
        if (node is null) return "null";
        var text = node.ToJsonString();
        return text.Length > 300 ? text[..300] + "…" : text;
    }

    private static string Format(double d) => d.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}
