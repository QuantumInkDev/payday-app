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
///   <item>numbers are rounded with the spec's rule (<c>spec/data-model.md</c> section 3.1: scale,
///     truncate, add one if the fraction is at least 0.5) to 4 decimals when the property name ends
///     in <c>Fraction</c> and to 2 decimals (cents) otherwise, and the rounded integers must be equal.
///     There is no tolerance: an app that does not round the way the spec says fails.</item>
/// </list>
/// </summary>
internal static class JsonCompare
{
    public const double MoneyScale = 100;
    public const double FractionScale = 10_000;

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
            var scale = IsFraction(propertyName) ? FractionScale : MoneyScale;
            if (RoundScaled(e, scale) != RoundScaled(a, scale))
            {
                diffs.Add($"{path}: expected {Format(e)}, got {Format(a)} (rounded: {RoundScaled(e, scale)} vs {RoundScaled(a, scale)} at scale {scale})");
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

    /// <summary>
    /// The spec's rounding (<c>spec/data-model.md</c> section 3.1) at any scale: <c>cents(x)</c> is
    /// <c>RoundScaled(x, 100)</c>. Round half away from zero, computed literally in binary64.
    /// </summary>
    public static long RoundScaled(double x, double scale)
    {
        if (double.IsNaN(x) || double.IsInfinity(x)) throw new ArgumentOutOfRangeException(nameof(x), x, "not finite");
        var s = Math.Abs(x) * scale;
        var i = Math.Truncate(s);
        if (s - i >= 0.5) i += 1;
        return (long)(x < 0 ? -i : i);
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
