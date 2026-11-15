using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using PayDay.Sync;
using PayDay.Sync.Clock;
using PayDay.Sync.Ids;
using PayDay.Sync.Json;
using PayDay.Sync.Model;

namespace PayDay.Tests.Sync;

/// <summary>Unit tests for the sync primitives beyond what spec/conformance/sync reaches.</summary>
public class SyncPrimitivesTests
{
    private const string DeviceA = "0a0a0a0a0a0a0a0a";

    private static string Code(Action action) => Assert.Throws<SyncException>(action).Code;

    // ---- StrictJson ------------------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{\"a\":1,}")]
    [InlineData("[1,2,]")]
    [InlineData("{\"a\":1} // comment")]
    [InlineData("/* c */ {}")]
    [InlineData("{\"a\":Infinity}")]
    [InlineData("{\"a\":-Infinity}")]
    [InlineData("{\"a\":01}")]
    [InlineData("{\"a\":'x'}")]
    [InlineData("{} {}")]
    [InlineData("{\"o\":{\"k\":1,\"k\":2}}")]
    [InlineData("{\"a\":\"\\udc00\"}")]
    [InlineData("{\"\\ud800\":1}")]
    public void StrictJson_RejectsAsMalformed(string text)
        => Assert.Equal(SyncErrorCodes.Malformed, Code(() => StrictJson.Parse(text)));

    [Fact]
    public void StrictJson_DuplicateKeysCompareAfterUnescaping()
        => Assert.Equal(SyncErrorCodes.Malformed, Code(() => StrictJson.Parse("{\"a\":1,\"\\u0061\":2}")));

    [Fact]
    public void StrictJson_KeepsOverflowingNumbersAsInfinity_ForTheReadingRuleToReject()
    {
        var v = StrictJson.Parse("{\"x\":1e400,\"y\":-1e400}");
        Assert.True(double.IsPositiveInfinity(v["x"]!.NumberValue));
        Assert.True(double.IsNegativeInfinity(v["y"]!.NumberValue));
        Assert.False(StrictJson.TryGetInteger(v["x"], out _));
    }

    [Fact]
    public void StrictJson_SerializesCompactly_WithIntegersAsPlainDigits()
    {
        var value = JsonData.Object(
            ("seq", JsonData.Number(3.0)),
            ("money", JsonData.Number(1145.5)),
            ("big", JsonData.Number(9007199254740991)),
            ("text", JsonData.String("Café \u20ac \U0001F600")),
            ("list", JsonData.Array(new[] { JsonData.True, JsonData.Null, JsonData.Number(-0.25) })));
        var text = Encoding.UTF8.GetString(StrictJson.Serialize(value));
        // Characters above U+FFFF come out as an escaped surrogate pair: valid JSON that every parser reads back.
        Assert.Equal("{\"seq\":3,\"money\":1145.5,\"big\":9007199254740991,\"text\":\"Café € \\uD83D\\uDE00\",\"list\":[true,null,-0.25]}", text);
        Assert.True(JsonData.DeepEquals(value, StrictJson.Parse(text)));
    }

    [Fact]
    public void StrictJson_CannotWriteNonFiniteNumbers()
        => Assert.Throws<ArgumentOutOfRangeException>(() => StrictJson.Serialize(JsonData.Number(double.NaN)));

    [Theory]
    [InlineData("0", true, 0L)]
    [InlineData("-0", true, 0L)]
    [InlineData("-9007199254740991", true, -9007199254740991L)]
    [InlineData("-9007199254740993", false, 0L)]
    [InlineData("1e2", true, 100L)]
    [InlineData("0.5", false, 0L)]
    [InlineData("\"3\"", false, 0L)]
    [InlineData("null", false, 0L)]
    [InlineData("true", false, 0L)]
    public void StrictJson_TryGetInteger(string json, bool ok, long expected)
    {
        Assert.Equal(ok, StrictJson.TryGetInteger(StrictJson.Parse(json), out var v));
        Assert.Equal(expected, v);
    }

    [Fact]
    public void JsonData_RejectsDuplicateKeysWhenBuilt()
        => Assert.Throws<ArgumentException>(() => JsonData.Object(("a", JsonData.Null), ("a", JsonData.True)));

    // ---- Utf8Order / SyncValue ----------------------------------------------------------------

    [Fact]
    public void Utf8Order_SortsByCodePoint_NotUtf16CodeUnit()
    {
        const string astral = "\U0001F600";   // UTF-16 starts with 0xD83D
        const string bmpHigh = "\uFF5E";       // above the surrogate range in UTF-16
        Assert.True(string.CompareOrdinal(astral, bmpHigh) < 0, "UTF-16 order puts the surrogate pair first");
        Assert.True(Utf8Order.Compare(astral, bmpHigh) > 0, "UTF-8 / code point order puts U+1F600 last");
        Assert.True(Utf8Order.Compare("a", "ab") < 0);
        Assert.Equal(0, Utf8Order.Compare("same", "same"));
        var sorted = new[] { "b", astral, "a", bmpHigh }.Order(Utf8Order.Instance).ToArray();
        Assert.Equal(new[] { "a", "b", bmpHigh, astral }, sorted);
    }

    [Fact]
    public void SyncValue_OrderIsNullFalseTrueNumberString()
    {
        var ordered = new[]
        {
            SyncValue.Null, SyncValue.False, SyncValue.True,
            SyncValue.Number(-5), SyncValue.Number(0), SyncValue.Number(1e12),
            SyncValue.String(""), SyncValue.String("A"), SyncValue.String("a"), SyncValue.String("\U0001F600"),
        };
        for (var i = 0; i < ordered.Length; i++)
        {
            for (var j = 0; j < ordered.Length; j++)
            {
                Assert.Equal(Math.Sign(i.CompareTo(j)), SyncValue.CompareOrder(ordered[i], ordered[j]));
            }
        }
    }

    [Fact]
    public void SyncValue_EqualityKeepsBooleansAndNumbersApart()
    {
        Assert.NotEqual(SyncValue.True, SyncValue.Number(1));
        Assert.NotEqual(SyncValue.False, SyncValue.Number(0));
        Assert.NotEqual(SyncValue.Null, SyncValue.String(""));
        Assert.Equal(SyncValue.Number(3), SyncValue.Number(3.0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SyncValue.Number(double.PositiveInfinity));
    }

    [Theory]
    [InlineData("{\"entity\":\"bill\",\"id\":\"b\",\"field\":\"name\",\"value\":1e400,\"hlc\":\"01a1d9edc200-0000-0a0a0a0a0a0a0a0a\"}")]
    [InlineData("{\"entity\":\"bill\",\"id\":\"b\",\"field\":\"\",\"value\":1,\"hlc\":\"01a1d9edc200-0000-0a0a0a0a0a0a0a0a\"}")]
    [InlineData("{\"entity\":\"bill\",\"id\":\"b\",\"field\":\"name\",\"value\":1,\"hlc\":\"01a1d9edc200-0000-0a0a0a0a0a0a0a0a\\n\"}")]
    [InlineData("{\"entity\":\"bill\",\"id\":\"b\",\"field\":\"name\",\"value\":1,\"hlc\":5}")]
    [InlineData("[\"bill\"]")]
    public void ChangeRecord_InvalidRecordsAreRejected(string json)
        => Assert.False(ChangeRecord.TryFromJson(StrictJson.Parse(json), out _));

    [Fact]
    public void ChangeRecord_RoundTripsThroughJson()
    {
        var r = new ChangeRecord("bill", "019a0000-0000-7000-8000-0000000000b1", "payment", SyncValue.Number(55), Hlc.Encode(1794657600000, 0, DeviceA));
        Assert.True(ChangeRecord.TryFromJson(StrictJson.Parse(StrictJson.Serialize(r.ToJson())), out var back));
        Assert.Equal(r, back);
    }

    // ---- HLC ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("01a1d9edc200-0000-0a0a0a0a0a0a0a0a\n")]
    [InlineData("01a1d9edc200-0000-0a0a0a0a0a0a0a0a\r\n")]
    [InlineData(null)]
    [InlineData("\u0661\u0661a1d9edc200-0000-0a0a0a0a0a0a0a0a")]
    public void Hlc_RejectsAnythingButExactly34CanonicalCharacters(string? text)
    {
        Assert.False(Hlc.IsValid(text));
        Assert.Equal(SyncErrorCodes.Malformed, Code(() => Hlc.Parse(text)));
    }

    [Fact]
    public void HybridLogicalClock_TicksObservesAndRaises()
    {
        var time = new ManualTimeProvider(1794657600000);
        var clock = new HybridLogicalClock(DeviceA, null, time);
        var first = clock.Tick();
        Assert.Equal("01a1d9edc200-0000-0a0a0a0a0a0a0a0a", first.Text);
        clock.Observe(Hlc.Parse("01a1d9ef96c0-0004-0b0b0b0b0b0b0b0b"));
        Assert.Equal("01a1d9ef96c0-0004-0a0a0a0a0a0a0a0a", clock.State!.Value.Text);
        Assert.Equal("01a1d9ef96c0-0005-0a0a0a0a0a0a0a0a", clock.Tick().Text);
        clock.RaiseTo(Hlc.Parse("01a1da000000-0009-0a0a0a0a0a0a0a0a"));
        Assert.Equal("01a1da000000-0009-0a0a0a0a0a0a0a0a", clock.State!.Value.Text);
        clock.RaiseTo(first);
        Assert.Equal("01a1da000000-0009-0a0a0a0a0a0a0a0a", clock.State!.Value.Text);
        Assert.Equal(SyncErrorCodes.BadDeviceId, Code(() => _ = new HybridLogicalClock("0A0A0A0A0A0A0A0A", null, time)));
    }

    [Fact]
    public void Hlc_OperatorsFollowByteOrder()
    {
        var a = Hlc.Parse("01a1d9edc200-ffff-0a0a0a0a0a0a0a0a");
        var b = Hlc.Parse("01a1d9edc201-0000-0a0a0a0a0a0a0a0a");
        var a2 = Hlc.Parse(a.Text);
        Assert.True(a < b && b > a && a <= a2 && a >= a2 && a == a2 && a != b);
        Assert.Equal(0x01a1d9edc200, a.Wall);
        Assert.Equal(0xffff, a.Counter);
        Assert.Equal(DeviceA, a.DeviceId);
    }

    // ---- IDs, dates, money ----------------------------------------------------------------------

    [Fact]
    public void DeviceIds_NewRandomIsValidAndFresh()
    {
        var ids = Enumerable.Range(0, 50).Select(_ => DeviceIds.NewRandom()).ToList();
        Assert.All(ids, id => Assert.True(DeviceIds.IsValid(id)));
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void Uuid_NewV7HasTheSpecLayout()
    {
        var time = new ManualTimeProvider(1794657600123);
        var id = Uuid.NewV7(time);
        var info = Uuid.Inspect(id);
        Assert.True(info.Canonical);
        Assert.Equal(7, info.Version);
        Assert.True(info.Rfc9562Variant);
        Assert.Equal(1794657600123, info.UnixMs);
        Assert.Equal(7, Guid.Parse(id).Version);
        Assert.Equal(SyncErrorCodes.BadInput, Code(() => Uuid.V7(1L << 48, new byte[10])));
        Assert.Equal(SyncErrorCodes.BadInput, Code(() => Uuid.V7(0, new byte[9])));
    }

    [Fact]
    public void SyncIds_EdgeCases()
    {
        Assert.Equal(SyncErrorCodes.NotLegacy, Code(() => SyncIds.MigrateBillId("")));
        Assert.Equal(SyncErrorCodes.NotLegacy, Code(() => SyncIds.LegacyBillName("9F6C1E2A-3B4D-4E5F-8A6B-7C8D9E0F1A2B")));
        Assert.Throws<ArgumentException>(() => SyncIds.PaymentIdFor(PaymentKinds.Extra, "019a0000-0000-7000-8000-0000000000b1", "2026-11-12"));
        Assert.Equal(SyncIds.SetAsideId("019a0000-0000-7000-8000-0000000000b1", "2026-11-12"),
            SyncIds.PaymentIdFor(PaymentKinds.SetAside, "019a0000-0000-7000-8000-0000000000b1", "2026-11-12"));
        Assert.Equal(SyncErrorCodes.BadInput, Code(() => SyncIds.LegacySnapshotId("2026-10-29", 1, 0)));

        var time = new ManualTimeProvider(1794657600000);
        const string source = "019a0000-0000-7000-8000-0000000000c1";
        Assert.Equal(SyncIds.PaycheckId(source, "2026-11-20"), SyncIds.NewPaycheckId(source, "2026-11-20", "Bi-Weekly", time));
        var a = SyncIds.NewPaycheckId(source, "2026-11-20", "Irregular", time);
        var b = SyncIds.NewPaycheckId(source, "2026-11-20", "Irregular", time);
        Assert.NotEqual(a, b);
        Assert.Equal(7, Uuid.Inspect(a).Version);
    }

    [Theory]
    [InlineData("2028-02-29", true)]
    [InlineData("2026-02-29", false)]
    [InlineData("0000-01-01", false)]
    [InlineData("2026-11-12\n", false)]
    [InlineData("2026-1-12", false)]
    [InlineData("\u0662026-11-12", false)]
    public void SyncDates_IsDate(string s, bool ok) => Assert.Equal(ok, SyncDates.IsDate(s));

    [Fact]
    public void SyncDates_FormatsTimestampsInUtcWithTruncatedMilliseconds()
    {
        var t = new DateTimeOffset(2026, 11, 14, 7, 30, 5, 120, TimeSpan.FromHours(-5)).AddTicks(9999);
        Assert.Equal("2026-11-14T12:30:05.120Z", SyncDates.FormatTimestamp(t));
        Assert.True(SyncDates.IsTimestamp(SyncDates.FormatTimestamp(t)));
        Assert.False(SyncDates.IsTimestamp("2026-11-14T12:30:05Z"));
    }

    [Theory]
    [InlineData(2.675, 268)]
    [InlineData(1.005, 100)]
    [InlineData(1.115, 112)]
    [InlineData(0.285, 28)]
    [InlineData(33.335, 3334)]
    [InlineData(-2.675, -268)]
    [InlineData(0.30000000000000004, 30)]
    [InlineData(19.999, 2000)]
    [InlineData(-0.004, 0)]
    [InlineData(9999999999999.99, 999999999999999)]
    public void SyncMoney_CentsMatchesTheSpecTable(double x, long cents)
    {
        Assert.Equal(cents, SyncMoney.Cents(x));
        Assert.Equal(Math.Round(x, 2, MidpointRounding.AwayFromZero), SyncMoney.Round2(x));
    }

    [Theory]
    [InlineData(1e13)]
    [InlineData(-1e13)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void SyncMoney_RejectsOutOfRange(double x)
    {
        Assert.False(SyncMoney.TryCents(x, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => SyncMoney.Cents(x));
    }

    [Fact]
    public void SyncMoney_AgreesWithMathRoundOnASample()
    {
        var rng = new Random(20261115);
        for (var i = 0; i < 200_000; i++)
        {
            var x = (rng.NextDouble() - 0.5) * Math.Pow(10, rng.Next(0, 12));
            x = Math.Round(x, rng.Next(0, 5));
            Assert.Equal(Math.Round(x, 2, MidpointRounding.AwayFromZero), SyncMoney.Round2(x));
        }
    }

    // ---- Schema ----------------------------------------------------------------------------------

    [Fact]
    public void SyncSchema_KnowsTheDefaultsAndTypes()
    {
        Assert.True(SyncSchema.TryGetField(SyncEntities.Bill, "dueDay", out var dueDay));
        Assert.Equal(SyncValue.Number(1), dueDay.Default);
        Assert.True(dueDay.Accepts(SyncValue.Number(3.0)));
        Assert.False(dueDay.Accepts(SyncValue.True));
        Assert.False(dueDay.Accepts(SyncValue.Number(3.5)));
        Assert.True(SyncSchema.TryGetField(SyncEntities.Payment, "kind", out var kind));
        Assert.Equal(SyncValue.String(PaymentKinds.Regular), kind.Default);
        Assert.True(SyncSchema.TryGetField(SyncEntities.Bill, "remainingCheckpoint", out var cp));
        Assert.True(cp.SameValue(SyncValue.Number(10.004), SyncValue.Number(10)));
        Assert.False(SyncSchema.TryGetField("futureEntity", "x", out _));
        Assert.True(SyncSchema.IsKnownEntity(SyncEntities.Setting));
        Assert.Equal(17, SyncSchema.FieldsOf(SyncEntities.Bill).Count);
    }
}

/// <summary>A <see cref="TimeProvider"/> whose clock only moves when told to.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    public ManualTimeProvider(long unixMs)
    {
        UnixMs = unixMs;
    }

    public long UnixMs { get; set; }

    public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(UnixMs);

    public void Advance(long ms) => UnixMs += ms;
}
