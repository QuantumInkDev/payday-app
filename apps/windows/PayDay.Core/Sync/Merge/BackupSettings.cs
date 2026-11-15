using System.Globalization;
using PayDay.Sync.Json;
using PayDay.Sync.Model;

namespace PayDay.Sync.Merge;

/// <summary>
/// Maps a settings table or a backup's <c>settings</c> object to synced plan settings
/// (<c>spec/data-model.md</c> section 8.5). Values in today's table are text (or null); a backup
/// written after sync may hold JSON booleans and numbers.
/// </summary>
public static class BackupSettings
{
    /// <summary>Today's single-row JSON object of type colours, replaced by one <c>TypeColor/&lt;type&gt;</c> key per type.</summary>
    public const string LegacyTypeColorsKey = "TypeColors";

    /// <summary>
    /// The plan settings a restore writes: only keys with a usable value, sorted by key. Notion
    /// connector keys, per-device keys and unknown keys are dropped. An explicit
    /// <c>TypeColor/&lt;type&gt;</c> key overrides a <c>TypeColors</c> entry for the same type.
    /// </summary>
    public static IReadOnlyDictionary<string, SyncValue> MapForRestore(JsonData settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Kind != JsonDataKind.Object) throw new ArgumentException("Backup settings must be a JSON object.", nameof(settings));
        var output = new SortedDictionary<string, SyncValue>(Utf8Order.Instance);

        if (settings[LegacyTypeColorsKey] is { Kind: JsonDataKind.String } colors)
        {
            foreach (var (type, hex) in ParseLegacyTypeColors(colors.StringValue))
            {
                if (type.Length == 0) continue;
                if (MapOne(SyncSettingKeys.TypeColor(type), hex) is { } v) output[SyncSettingKeys.TypeColor(type)] = v;
            }
        }

        foreach (var (key, raw) in settings.Members)
        {
            if (SyncSettingKeys.NotionKeys.Contains(key, StringComparer.Ordinal)
                || !SyncSettingKeys.IsPlanSetting(key)
                || key == SyncSettingKeys.TypeColorPrefix)
            {
                continue;
            }
            if (MapOne(key, raw) is { } v) output[key] = v;
        }
        return output;
    }

    /// <summary>
    /// The migration's mapping (creating a vault from today's database): the plan settings of
    /// <see cref="MapForRestore"/> plus the Notion data-source IDs, which only the migration carries
    /// over. <c>NotionOwnerDeviceId</c> is never taken from a table.
    /// </summary>
    public static IReadOnlyDictionary<string, SyncValue> MapForMigration(JsonData settings)
    {
        var output = new SortedDictionary<string, SyncValue>(Utf8Order.Instance);
        foreach (var (k, v) in MapForRestore(settings)) output[k] = v;
        foreach (var key in new[] { SyncSettingKeys.NotionBillsDb, SyncSettingKeys.NotionPaymentsDb, SyncSettingKeys.NotionSnapshotsDb })
        {
            if (settings[key] is { Kind: JsonDataKind.String } s) output[key] = SyncValue.String(s.StringValue);
        }
        return output;
    }

    /// <summary>Converts one value, or returns null when it cannot be used ("not used" in section 8.5).</summary>
    public static SyncValue? MapOne(string key, JsonData? raw)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (raw is null || raw.IsNull) return null;
        switch (key)
        {
            case SyncSettingKeys.PayAnchor:
                return SyncDates.IsDate(raw.AsString()) ? SyncValue.String(raw.StringValue) : null;
            case SyncSettingKeys.EarlyStart:
                if (raw.Kind == JsonDataKind.Bool) return SyncValue.Bool(raw.BoolValue);
                return raw.AsString() is { } text ? SyncValue.Bool(IsAsciiTrue(text)) : null;
            case SyncSettingKeys.SafeToSpendBuffer:
            {
                double number;
                if (raw.AsString() is { } t)
                {
                    if (!IsDecimalText(t)) return null;
                    number = double.Parse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
                }
                else if (raw.Kind == JsonDataKind.Number)
                {
                    number = raw.NumberValue;
                }
                else
                {
                    return null;
                }
                return SyncMoney.TryCents(number, out var cents) ? SyncValue.Number(SyncMoney.FromCents(cents)) : null;
            }
            case SyncSettingKeys.ReminderDaysBefore:
            {
                if (raw.AsString() is { } t)
                {
                    // Digit text whose value is at most 2^53 - 1 (leading zeros allowed).
                    if (t.Length == 0 || !t.All(c => c is >= '0' and <= '9')) return null;
                    return ulong.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days <= StrictJson.MaxSafeInteger
                        ? SyncValue.Number(days)
                        : null;
                }
                return StrictJson.TryGetInteger(raw, out var n) && n >= 0 ? SyncValue.Number(n) : null;
            }
            default:
                if (key.StartsWith(SyncSettingKeys.TypeColorPrefix, StringComparison.Ordinal))
                {
                    return IsHexColor(raw.AsString()) ? SyncValue.String(raw.StringValue.ToUpperInvariant()) : null;
                }
                return null;
        }
    }

    /// <summary>
    /// Reads the legacy <c>TypeColors</c> JSON object text with the strict rules of
    /// <c>sync-protocol.md</c> 6.3 (no NaN, duplicate keys, lone surrogates or trailing text). Text
    /// that is invalid, or valid JSON that is not an object, gives no colours.
    /// </summary>
    private static IReadOnlyList<KeyValuePair<string, JsonData>> ParseLegacyTypeColors(string text)
    {
        // A lone surrogate cannot be encoded as UTF-8; Encoding.UTF8 would silently replace it.
        if (StrictJson.HasLoneSurrogate(text)) return Array.Empty<KeyValuePair<string, JsonData>>();
        try
        {
            var parsed = StrictJson.Parse(text);
            return parsed.Kind == JsonDataKind.Object ? parsed.Members : Array.Empty<KeyValuePair<string, JsonData>>();
        }
        catch (SyncException)
        {
            return Array.Empty<KeyValuePair<string, JsonData>>();
        }
    }

    /// <summary>The whole text is <c>true</c> in any mix of ASCII upper and lower case.</summary>
    private static bool IsAsciiTrue(string s)
        => s.Length == 4 && (s[0] | 0x20) == 't' && (s[1] | 0x20) == 'r' && (s[2] | 0x20) == 'u' && (s[3] | 0x20) == 'e';

    private static bool IsHexColor(string? s)
        => s is { Length: 7 } && s[0] == '#' && s.Skip(1).All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    /// <summary><c>-?digits[.digits]</c>, ASCII digits only.</summary>
    private static bool IsDecimalText(string s)
    {
        var i = 0;
        if (i < s.Length && s[i] == '-') i++;
        var start = i;
        while (i < s.Length && s[i] is >= '0' and <= '9') i++;
        if (i == start) return false;
        if (i == s.Length) return true;
        if (s[i] != '.') return false;
        i++;
        var fracStart = i;
        while (i < s.Length && s[i] is >= '0' and <= '9') i++;
        return i > fracStart && i == s.Length;
    }
}
