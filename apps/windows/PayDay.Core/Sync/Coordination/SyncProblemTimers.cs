using System.Globalization;
using System.Text.Json;

namespace PayDay.Sync.Coordination;

/// <summary>
/// When each open sync problem was first seen on this device: an unreadable file (keyed by its path)
/// or a gap (<c>gap:&lt;device&gt;:&lt;seq&gt;</c>). Problems are reported only once they have lasted 24 hours
/// (<c>sync-protocol.md</c> 10.2), so the times must outlive the process: they are kept in this
/// device's <c>sync_device</c> table as one JSON value naming the device identity they belong to.
/// <para>
/// A cycle marks every problem it still sees (<see cref="Since"/>) and clears the ones it resolved
/// (<see cref="Resolve"/>); <see cref="EndCycle"/> then forgets problems the cycle no longer saw (a
/// file compacted away, a gap that filled), so the stored map never grows without bound.
/// </para>
/// </summary>
internal sealed class SyncProblemTimers
{
    private readonly Dictionary<string, long> _since = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

    /// <summary>True when the map changed since it was loaded or last saved.</summary>
    public bool Dirty { get; private set; }

    public int Count => _since.Count;

    /// <summary>Replaces the map with the stored one when it belongs to <paramref name="deviceId"/>; anything unreadable is ignored.</summary>
    public void Load(string? json, string deviceId)
    {
        _since.Clear();
        _seen.Clear();
        Dirty = false;
        if (string.IsNullOrEmpty(json)) return;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("deviceId", out var d) || d.ValueKind != JsonValueKind.String || d.GetString() != deviceId
                || !root.TryGetProperty("since", out var since) || since.ValueKind != JsonValueKind.Object)
            {
                Dirty = true; // another identity's (or a damaged) map: overwrite it
                return;
            }
            foreach (var p in since.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt64(out var ms)) _since[p.Name] = ms;
            }
        }
        catch (JsonException)
        {
            Dirty = true;
        }
    }

    public void Clear()
    {
        if (_since.Count > 0) Dirty = true;
        _since.Clear();
        _seen.Clear();
    }

    /// <summary>Starts a cycle: nothing seen yet.</summary>
    public void BeginCycle() => _seen.Clear();

    /// <summary>The problem is still there: when it was first seen (now, if it is new).</summary>
    public DateTimeOffset Since(string key, DateTimeOffset now)
    {
        _seen.Add(key);
        if (_since.TryGetValue(key, out var ms)) return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        _since[key] = now.ToUnixTimeMilliseconds();
        Dirty = true;
        return now;
    }

    /// <summary>The problem is gone (the file opened).</summary>
    public void Resolve(string key)
    {
        _seen.Remove(key);
        if (_since.Remove(key)) Dirty = true;
    }

    /// <summary>Forgets every problem this cycle did not see.</summary>
    public void EndCycle()
    {
        foreach (var key in _since.Keys.Where(k => !_seen.Contains(k)).ToArray())
        {
            _since.Remove(key);
            Dirty = true;
        }
    }

    /// <summary>The stored form, or null when there is nothing to keep.</summary>
    public string? ToJson(string deviceId)
    {
        Dirty = false;
        if (_since.Count == 0) return null;
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();
            w.WriteString("deviceId", deviceId);
            w.WriteStartObject("since");
            foreach (var (key, ms) in _since.OrderBy(kv => kv.Key, StringComparer.Ordinal)) w.WriteNumber(key, ms);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string GapKey(string device, long seq) => $"gap:{device}:{seq.ToString(CultureInfo.InvariantCulture)}";
}
