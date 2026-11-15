using System.Globalization;

namespace PayDay.Sync.Model;

/// <summary>The <c>date</c> and <c>timestamp</c> formats of <c>spec/data-model.md</c> section 1.1.</summary>
public static class SyncDates
{
    /// <summary>A valid, zero-padded calendar date <c>YYYY-MM-DD</c> (ASCII digits only).</summary>
    public static bool IsDate(string? s)
    {
        if (s is null || s.Length != 10 || s[4] != '-' || s[7] != '-') return false;
        for (var i = 0; i < 10; i++)
        {
            if (i is 4 or 7) continue;
            if (s[i] is < '0' or > '9') return false;
        }
        return DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    /// <summary>
    /// The timestamp shape <c>YYYY-MM-DDTHH:MM:SS.sssZ</c> (ASCII digits, exactly three fraction
    /// digits, literal Z). This is a shape check only, as the protocol's envelope rule uses it.
    /// </summary>
    public static bool IsTimestamp(string? s)
    {
        const string pattern = "dddd-dd-ddTdd:dd:dd.dddZ";
        if (s is null || s.Length != pattern.Length) return false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var p = pattern[i];
            var c = s[i];
            if (p == 'd' ? c is < '0' or > '9' : c != p) return false;
        }
        return true;
    }

    /// <summary>Formats a time as a protocol timestamp (UTC, milliseconds, sub-millisecond digits truncated).</summary>
    public static string FormatTimestamp(DateTimeOffset time)
    {
        var utc = time.ToUniversalTime();
        var truncated = new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
        return truncated.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }
}
