using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using PayDay.Sync.Clock;
using PayDay.Sync.Merge;
using PayDay.Sync.Model;
using static PayDay.Tests.Conformance.SyncFixtureJson;

namespace PayDay.Tests.Conformance;

/// <summary>
/// Runs one <c>merge.json</c> case (<c>spec/README.md</c>, "merge.json cases"):
/// <list type="number">
///   <item>with an <c>edit</c>, merges the <c>seen</c> records, runs the op on that state and checks
///     the emitted records exactly (and that they are valid and later than everything seen);</item>
///   <item>merges <c>records</c> plus the expected emitted records in the given order and compares
///     the full state with <c>expected</c>;</item>
///   <item>repeats in every delivery order (all permutations up to 8 records; otherwise forward,
///     reverse and 2,000 seeded shuffles), every 50th order also with a third of the records
///     delivered twice, and requires the identical state each time.</item>
/// </list>
/// </summary>
internal static class SyncMergeHarness
{
    public const int AllPermutationsMax = 8;
    public const int SampledPermutations = 2000;

    public static void Run(string caseName, JsonObject input, JsonObject expected)
        => Run(caseName, input, expected, () => new SyncState(), allOrders: true);

    /// <summary>
    /// The same checks with states made by <paramref name="newState"/> (for example over the SQLite
    /// record store). With <paramref name="allOrders"/> false only the given order is merged.
    /// </summary>
    public static void Run(string caseName, JsonObject input, JsonObject expected, Func<SyncState> newState, bool allOrders)
    {
        Assert.Equal("merge", Str(input["fn"]));
        var raw = input["records"]!.AsArray().ToList();
        var records = raw.Select(Parse).ToList();

        var final = new List<ChangeRecord?>(records);
        if (input["edit"] is JsonObject edit)
        {
            var seenIdx = edit["seen"]!.AsArray().Select(n => (int)Long(n)).ToList();
            var seen = newState();
            foreach (var k in seenIdx)
            {
                if (records[k] is { } r) seen.Merge(r);
            }
            var emitted = RunOp(seen, edit["op"]!.AsObject());
            var diffs = Differences(expected["emitted"], ToNode(emitted));
            Assert.True(diffs.Count == 0, $"{caseName}: emitted records differ:\n  " + string.Join("\n  ", diffs));

            var latestSeen = seenIdx.Select(k => records[k]).Where(r => r is not null).Select(r => r!.Hlc).DefaultIfEmpty(Hlc.Encode(0, 0, "0000000000000000")).Max();
            Assert.All(emitted, r => Assert.True(r.Hlc > latestSeen, $"{caseName}: an edit's HLC must be later than everything the editing device has seen"));
            final.AddRange(expected["emitted"]!.AsArray().Select(Parse));
        }
        else
        {
            Assert.False(expected.ContainsKey("emitted"), $"{caseName}: emitted without an edit");
        }

        // The given order, compared field by field with the fixture.
        var given = StateOf(final, newState);
        var stateDiffs = Differences(Without(expected, "emitted"), StateNode(given), "remaining", "setAsideBalance");
        Assert.True(stateDiffs.Count == 0, $"{caseName} (given order):\n  " + string.Join("\n  ", stateDiffs));
        var reference = Fingerprint(given);

        // The in-memory state is the reference for the given order; the other states also give it.
        var inMemory = Fingerprint(StateOf(final));
        Assert.True(inMemory == reference, $"{caseName}: this store gives a different state than the in-memory store.\n  expected {inMemory}\n  got      {reference}");
        if (!allOrders)
        {
            var reversed = Enumerable.Reverse(final).ToList();
            var got = Fingerprint(StateOf(reversed, newState));
            Assert.True(got == reference, $"{caseName}: the reverse order gives a different state.\n  expected {reference}\n  got      {got}");
            return;
        }

        // Every other delivery order must give exactly the same state.
        var n = final.Count;
        var rng = new Random(StableSeed(caseName));
        var count = 0;
        foreach (var order in Orders(n, rng))
        {
            var ordered = order.Select(k => final[k]).ToList();
            AssertSame(caseName, reference, ordered, order);
            count++;
            if (count % 50 == 1)
            {
                var extra = Enumerable.Range(0, n).OrderBy(_ => rng.Next()).Take(Math.Max(1, n / 3)).Select(k => ordered[k]);
                var duplicated = ordered.Concat(extra).OrderBy(_ => rng.Next()).ToList();
                AssertSame(caseName, reference, duplicated, null);
            }
        }
        Assert.True(count >= Math.Min(Factorial(n), SampledPermutations), $"{caseName}: only {count} delivery orders ran");
    }

    private static ChangeRecord? Parse(JsonNode? node)
        => ChangeRecord.TryFromJson(ToData(node), out var r) ? r : null;

    private static void AssertSame(string caseName, string reference, IReadOnlyList<ChangeRecord?> ordered, int[]? order)
    {
        var state = StateOf(ordered);
        var got = Fingerprint(state);
        if (got != reference)
        {
            var label = order is null ? "an order with duplicated records" : "order " + string.Join(",", order.Take(16));
            Assert.Fail($"{caseName}: {label} gives a different state.\n  expected {reference}\n  got      {got}");
        }
    }

    private static SyncState StateOf(IEnumerable<ChangeRecord?> records) => StateOf(records, () => new SyncState());

    private static SyncState StateOf(IEnumerable<ChangeRecord?> records, Func<SyncState> newState)
    {
        var state = newState();
        foreach (var r in records)
        {
            if (r is not null) state.Merge(r);
        }
        return state;
    }

    private static JsonObject StateNode(SyncState s)
    {
        var rows = new JsonObject();
        var live = new JsonObject();
        foreach (var entity in s.Entities())
        {
            var byId = new JsonObject();
            foreach (var id in s.Ids(entity))
            {
                var fields = new JsonObject();
                foreach (var (field, value) in s.Row(entity, id).OrderBy(f => f.Key, StringComparer.Ordinal))
                {
                    fields[field] = ToNode(value);
                }
                byId[id] = fields;
            }
            rows[entity] = byId;
            live[entity] = Strings(s.LiveIds(entity));
        }
        var remaining = new JsonObject();
        var setAside = new JsonObject();
        foreach (var bill in s.LiveIds(SyncEntities.Bill))
        {
            remaining[bill] = s.Remaining(bill);
            setAside[bill] = s.SetAsideBalance(bill);
        }
        return new JsonObject { ["rows"] = rows, ["live"] = live, ["remaining"] = remaining, ["setAsideBalance"] = setAside };
    }

    /// <summary>A canonical text of everything the fixture compares: rows (values by value), live IDs, and cents.</summary>
    private static string Fingerprint(SyncState s)
    {
        var sb = new StringBuilder();
        foreach (var r in s.AllRecords())
        {
            sb.Append(r.Entity).Append('\u0001').Append(r.Id).Append('\u0001').Append(r.Field).Append('\u0001');
            var v = r.Value;
            sb.Append(v.IsNumber && v.NumberValue == 0 ? "0" : v.ToString()).Append('\u0002');
        }
        foreach (var entity in s.Entities())
        {
            sb.Append("live:").Append(entity).Append('=').Append(string.Join(",", s.LiveIds(entity))).Append('\u0002');
        }
        foreach (var bill in s.LiveIds(SyncEntities.Bill))
        {
            sb.Append("bill:").Append(bill).Append('=')
                .Append(s.RemainingCents(bill).ToString(CultureInfo.InvariantCulture)).Append('/')
                .Append(s.SetAsideBalanceCents(bill).ToString(CultureInfo.InvariantCulture)).Append('\u0002');
        }
        return sb.ToString();
    }

    private static IReadOnlyList<ChangeRecord> RunOp(SyncState state, JsonObject op)
    {
        var hlc = Hlc.Parse(Str(op["hlc"]));
        var kind = NullableStr(op["kind"]) ?? PaymentKinds.Regular;
        switch (Str(op["op"]))
        {
            case "markPaid":
                return PaymentWriter.MarkPaid(state, kind, Str(op["billId"]), Str(op["periodKey"]), Number(op["amount"]!), NullableStr(op["paidAt"]), hlc);
            case "unmarkPaid":
                return PaymentWriter.UnmarkPaid(state, kind, Str(op["billId"]), Str(op["periodKey"]), hlc);
            case "deletePayment":
                return PaymentWriter.DeletePayment(state, Str(op["paymentId"]), hlc);
            case "setRemaining":
            {
                var source = Str(op["source"]) switch
                {
                    "app" => RemainingSource.App,
                    "notion" => RemainingSource.Notion,
                    var other => throw new InvalidOperationException($"unknown source {other}"),
                };
                double? lastPushed = op["lastPushed"] is { } lp ? Number(lp) : null;
                return PaymentWriter.SetRemaining(state, Str(op["billId"]), Number(op["value"]!), source, lastPushed, hlc);
            }
            case "restore":
            {
                var rows = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyDictionary<string, SyncValue>>>(StringComparer.Ordinal);
                if (op["rows"] is JsonObject entities)
                {
                    foreach (var (entity, byId) in entities)
                    {
                        var ids = new Dictionary<string, IReadOnlyDictionary<string, SyncValue>>(StringComparer.Ordinal);
                        foreach (var (id, fields) in byId!.AsObject())
                        {
                            ids[id] = fields!.AsObject().ToDictionary(f => f.Key, f => SyncValue.FromJson(ToData(f.Value)), StringComparer.Ordinal);
                        }
                        rows[entity] = ids;
                    }
                }
                var settings = ToData(op["settings"] ?? new JsonObject());
                return RestoreWriter.Restore(state, new RestoreInput(rows, settings), hlc);
            }
            default:
                throw new InvalidOperationException($"unknown op {op["op"]}");
        }
    }

    /// <summary>All permutations for up to 8 items (Heap's algorithm); otherwise forward, reverse and seeded shuffles.</summary>
    private static IEnumerable<int[]> Orders(int n, Random rng)
    {
        if (n <= AllPermutationsMax)
        {
            var a = Enumerable.Range(0, n).ToArray();
            var c = new int[n];
            yield return (int[])a.Clone();
            var i = 0;
            while (i < n)
            {
                if (c[i] < i)
                {
                    var j = i % 2 == 0 ? 0 : c[i];
                    (a[j], a[i]) = (a[i], a[j]);
                    yield return (int[])a.Clone();
                    c[i]++;
                    i = 0;
                }
                else
                {
                    c[i] = 0;
                    i++;
                }
            }
            yield break;
        }
        yield return Enumerable.Range(0, n).ToArray();
        yield return Enumerable.Range(0, n).Reverse().ToArray();
        for (var s = 0; s < SampledPermutations; s++)
        {
            var p = Enumerable.Range(0, n).ToArray();
            for (var k = n - 1; k > 0; k--)
            {
                var j = rng.Next(k + 1);
                (p[k], p[j]) = (p[j], p[k]);
            }
            yield return p;
        }
    }

    private static long Factorial(int n) => n <= 1 ? 1 : n * Factorial(n - 1);

    /// <summary>FNV-1a over the case name's UTF-8 bytes, so the sampled orders are the same on every run and machine.</summary>
    private static int StableSeed(string name)
    {
        var h = 2166136261u;
        foreach (var b in Encoding.UTF8.GetBytes(name))
        {
            h = (h ^ b) * 16777619u;
        }
        return (int)(h & 0x7FFFFFFF);
    }

    private static JsonObject Without(JsonObject o, string key)
    {
        var copy = o.DeepClone().AsObject();
        copy.Remove(key);
        return copy;
    }
}
