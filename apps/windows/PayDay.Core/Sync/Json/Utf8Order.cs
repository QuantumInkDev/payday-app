namespace PayDay.Sync.Json;

/// <summary>
/// Orders strings by their UTF-8 bytes, which is the same as ordering by Unicode code point. This is
/// the order the spec uses for value ties (<c>sync-protocol.md</c> section 4.2), for sorted ID lists
/// and for emitted records. It differs from <see cref="StringComparer.Ordinal"/> (UTF-16 code units)
/// for characters above U+FFFF against U+E000–U+FFFF.
/// </summary>
public sealed class Utf8Order : IComparer<string>
{
    public static readonly Utf8Order Instance = new();

    private Utf8Order()
    {
    }

    int IComparer<string>.Compare(string? x, string? y) => Compare(x, y);

    public static int Compare(string? a, string? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            if (a[i] == b[i]) continue;
            return CodePointAt(a, i).CompareTo(CodePointAt(b, i));
        }
        return a.Length.CompareTo(b.Length);
    }

    private static int CodePointAt(string s, int i)
    {
        var c = s[i];
        if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
        {
            return char.ConvertToUtf32(c, s[i + 1]);
        }
        // A low surrogate here is the second half of a pair whose first half was equal in both
        // strings, so comparing the halves orders the code points correctly.
        return c;
    }
}
