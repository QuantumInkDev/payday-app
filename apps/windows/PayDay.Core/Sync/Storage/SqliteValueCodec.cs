using System.Globalization;
using PayDay.Sync.Model;

namespace PayDay.Sync.Storage;

/// <summary>
/// How a record value is stored in a TEXT column: one type letter, then the payload.
/// <c>z</c> null, <c>f</c> false, <c>t</c> true, <c>d</c> + a round-trip binary64 number,
/// <c>s</c> + the string. Decoding gives back exactly the value that was encoded (numbers bit for
/// bit, including <c>-0</c>), and two values that are equal as <see cref="SyncValue"/>s encode to the
/// same text except <c>0</c> and <c>-0</c>, which <see cref="EqualityKeys"/> handles.
/// </summary>
internal static class SqliteValueCodec
{
    public static string Encode(SyncValue value)
        => value.Kind switch
        {
            SyncValueKind.Null => "z",
            SyncValueKind.Bool => value.BoolValue ? "t" : "f",
            SyncValueKind.Number => "d" + value.NumberValue.ToString("R", CultureInfo.InvariantCulture),
            _ => "s" + value.StringValue,
        };

    public static SyncValue Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0) throw new FormatException("Empty stored value.");
        return text[0] switch
        {
            'z' when text.Length == 1 => SyncValue.Null,
            'f' when text.Length == 1 => SyncValue.False,
            't' when text.Length == 1 => SyncValue.True,
            'd' => SyncValue.Number(double.Parse(text.AsSpan(1), NumberStyles.Float, CultureInfo.InvariantCulture)),
            's' => SyncValue.String(text.Substring(1)),
            _ => throw new FormatException($"Unknown stored value '{text}'."),
        };
    }

    /// <summary>
    /// Every stored text equal to <paramref name="value"/> by <see cref="SyncValue"/> equality
    /// (numbers by value, so <c>0</c> also matches <c>-0</c>).
    /// </summary>
    public static IReadOnlyList<string> EqualityKeys(SyncValue value)
        => value.IsNumber && value.NumberValue == 0 ? new[] { "d0", "d-0" } : new[] { Encode(value) };
}
