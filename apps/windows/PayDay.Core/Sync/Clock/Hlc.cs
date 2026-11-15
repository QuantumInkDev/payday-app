using System.Globalization;
using PayDay.Sync.Ids;

namespace PayDay.Sync.Clock;

/// <summary>
/// A hybrid logical clock value (<c>spec/sync-protocol.md</c> section 2): 48-bit Unix milliseconds,
/// a 16-bit counter and the 16-hex-digit device ID, written canonically as
/// <c>&lt;wall:12 hex&gt;-&lt;counter:4 hex&gt;-&lt;deviceId:16 hex&gt;</c> (34 ASCII characters).
/// Values compare by (wall, counter, deviceId), which equals byte-wise comparison of the string.
/// </summary>
public readonly struct Hlc : IEquatable<Hlc>, IComparable<Hlc>
{
    public const long MaxWall = (1L << 48) - 1;
    public const int MaxCounter = 0xFFFF;
    public const int Length = 34;

    private readonly string? _text;

    private Hlc(string text, long wall, int counter)
    {
        _text = text;
        Wall = wall;
        Counter = counter;
    }

    public long Wall { get; }

    public int Counter { get; }

    /// <summary>The device part: the device that made the edit.</summary>
    public string DeviceId => Text.Substring(18, 16);

    /// <summary>The canonical string.</summary>
    public string Text => _text ?? throw new InvalidOperationException("default(Hlc) has no value.");

    /// <summary>Builds an HLC, failing with <c>wallOutOfRange</c>, <c>counterOutOfRange</c> or <c>badDeviceId</c>.</summary>
    public static Hlc Encode(long wall, long counter, string deviceId)
    {
        if (wall < 0 || wall > MaxWall) throw new SyncException(SyncErrorCodes.WallOutOfRange);
        if (counter < 0 || counter > MaxCounter) throw new SyncException(SyncErrorCodes.CounterOutOfRange);
        if (!DeviceIds.IsValid(deviceId)) throw new SyncException(SyncErrorCodes.BadDeviceId);
        var text = string.Create(CultureInfo.InvariantCulture, $"{wall:x12}-{counter:x4}-{deviceId}");
        return new Hlc(text, wall, (int)counter);
    }

    /// <summary>Parses a canonical HLC string, failing with <c>malformed</c> on anything else (including uppercase).</summary>
    public static Hlc Parse(string? text)
        => TryParse(text, out var hlc) ? hlc : throw new SyncException(SyncErrorCodes.Malformed);

    public static bool TryParse(string? text, out Hlc hlc)
    {
        hlc = default;
        if (text is null || text.Length != Length || text[12] != '-' || text[17] != '-') return false;
        long wall = 0;
        for (var i = 0; i < 12; i++)
        {
            var v = HexValue(text[i]);
            if (v < 0) return false;
            wall = (wall << 4) | (long)v;
        }
        var counter = 0;
        for (var i = 13; i < 17; i++)
        {
            var v = HexValue(text[i]);
            if (v < 0) return false;
            counter = (counter << 4) | v;
        }
        for (var i = 18; i < 34; i++)
        {
            if (HexValue(text[i]) < 0) return false;
        }
        hlc = new Hlc(text, wall, counter);
        return true;
    }

    public static bool IsValid(string? text) => TryParse(text, out _);

    /// <summary>Compares two HLC strings, failing with <c>malformed</c> if either is not valid. Returns -1, 0 or 1.</summary>
    public static int Compare(string a, string b) => Math.Sign(Parse(a).CompareTo(Parse(b)));

    public int CompareTo(Hlc other) => string.CompareOrdinal(Text, other.Text);

    public bool Equals(Hlc other) => string.Equals(_text, other._text, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is Hlc other && Equals(other);

    public override int GetHashCode() => _text is null ? 0 : StringComparer.Ordinal.GetHashCode(_text);

    public override string ToString() => _text ?? string.Empty;

    public static bool operator ==(Hlc left, Hlc right) => left.Equals(right);

    public static bool operator !=(Hlc left, Hlc right) => !left.Equals(right);

    public static bool operator <(Hlc left, Hlc right) => left.CompareTo(right) < 0;

    public static bool operator >(Hlc left, Hlc right) => left.CompareTo(right) > 0;

    public static bool operator <=(Hlc left, Hlc right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Hlc left, Hlc right) => left.CompareTo(right) >= 0;

    /// <summary>Lowercase hex digit value, or -1. Uppercase is not a hex digit here.</summary>
    internal static int HexValue(char c)
        => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            _ => -1,
        };
}
