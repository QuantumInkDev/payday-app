using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace PayDay.Services;

/// <summary>
/// Per-type pill color overrides, persisted to the Settings table as a JSON dict
/// (key = type name, value = "#RRGGBB"). Defaults match the original palette baked
/// into <c>Styles/TypeBrushes.xaml</c>. Loaded once on app startup via
/// <see cref="LoadAsync"/>; reads are synchronous lookups against an in-memory map.
/// </summary>
public static class TypeColorService
{
    public const string SettingKey = "TypeColors";

    /// <summary>Built-in defaults — used when no override has been saved for a type.</summary>
    public static readonly IReadOnlyDictionary<string, string> Defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Cards"]         = "#FD79A8",
        ["Bills"]         = "#B2945B",
        ["Loans"]         = "#6C5CE7",
        ["Installments"]  = "#FDCB6E",
        ["Subscriptions"] = "#E17055",
        ["Business"]      = "#74B9FF",
        ["People"]        = "#00B894",
        ["Medical"]       = "#55EFC4",
        ["Other"]         = "#8B8FA3",
    };

    private static Dictionary<string, string> _overrides = new(StringComparer.OrdinalIgnoreCase);

    public static async Task LoadAsync(IDatabaseService db)
    {
        var raw = await db.GetSettingAsync(SettingKey).ConfigureAwait(false);
        _overrides = ParseSafe(raw);
    }

    /// <summary>Hex string ("#RRGGBB") for the given type — override if present, otherwise default, otherwise the "Other" fallback.</summary>
    public static string GetHex(string type)
    {
        if (!string.IsNullOrWhiteSpace(type) && _overrides.TryGetValue(type, out var ov)) return ov;
        if (!string.IsNullOrWhiteSpace(type) && Defaults.TryGetValue(type, out var def)) return def;
        return Defaults["Other"];
    }

    /// <summary>
    /// Saves one type's colour. With sync on (<see cref="ITypeColorWriter"/>) only that type's synced
    /// colour is written, so a colour another device set since this map was loaded is never reverted;
    /// with sync off the whole map is saved as it always was.
    /// </summary>
    public static async Task SetHexAsync(IDatabaseService db, string type, string hex)
    {
        if (string.IsNullOrWhiteSpace(type) || !IsValidHex(hex)) return;
        _overrides[type] = hex.ToUpperInvariant();
        if (db is ITypeColorWriter writer && await writer.TrySetTypeColorAsync(type, hex.ToUpperInvariant()).ConfigureAwait(false)) return;
        await db.SetSettingAsync(SettingKey, JsonSerializer.Serialize(_overrides)).ConfigureAwait(false);
    }

    /// <summary>Resets one type to its default colour (with sync on, only that type's synced colour is cleared).</summary>
    public static async Task ResetAsync(IDatabaseService db, string type)
    {
        var removed = _overrides.Remove(type);
        if (db is ITypeColorWriter writer && await writer.TrySetTypeColorAsync(type, null).ConfigureAwait(false)) return;
        if (removed)
        {
            await db.SetSettingAsync(SettingKey, JsonSerializer.Serialize(_overrides)).ConfigureAwait(false);
        }
    }

    /// <summary>Exposed for tests — clears the in-memory overrides without touching the DB.</summary>
    public static void ResetInMemory() => _overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> ParseSafe(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(raw);
            return parsed is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static bool IsValidHex(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex.Length != 7 || hex[0] != '#') return false;
        for (var i = 1; i < hex.Length; i++)
        {
            var c = hex[i];
            if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f'))) return false;
        }
        return true;
    }
}

/// <summary>
/// Writes one type's colour on its own. The sync write path implements it: with sync on each colour is
/// its own synced setting (<c>TypeColor/&lt;type&gt;</c>), and writing the whole map a page loaded
/// earlier could revert a colour another device changed since.
/// </summary>
public interface ITypeColorWriter
{
    /// <summary>
    /// Sets (or with null resets) one type's colour and returns true, or returns false without writing
    /// anything when this database saves the whole <c>TypeColors</c> map instead (sync off).
    /// </summary>
    Task<bool> TrySetTypeColorAsync(string type, string? hex);
}
