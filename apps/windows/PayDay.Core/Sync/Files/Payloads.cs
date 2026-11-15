using PayDay.Sync.Clock;
using PayDay.Sync.Ids;
using PayDay.Sync.Json;
using PayDay.Sync.Model;

namespace PayDay.Sync.Files;

/// <summary>Builds the plaintext payloads of section 6.3 and section 9.1, written compactly.</summary>
public static class Payloads
{
    public static JsonData ChangeFile(string vaultId, string deviceId, long seq, IReadOnlyList<ChangeRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (!DeviceIds.IsValid(deviceId)) throw new SyncException(SyncErrorCodes.BadDeviceId);
        if (seq < 1) throw new SyncException(SyncErrorCodes.BadSeq);
        if (records.Count == 0) throw new ArgumentException("A batch must contain at least one record (section 10.1).", nameof(records));
        if (records.Any(r => r.Hlc.DeviceId != deviceId))
        {
            throw new ArgumentException("A change file carries only its own device's records (section 6.3).", nameof(records));
        }
        return JsonData.Object(
            ("type", JsonData.String("changes")),
            ("formatVersion", JsonData.Number(Envelope.SupportedFormatVersion)),
            ("vaultId", JsonData.String(vaultId)),
            ("deviceId", JsonData.String(deviceId)),
            ("seq", JsonData.Number(seq)),
            ("records", JsonData.Array(records.Select(r => r.ToJson()))));
    }

    public static JsonData Snapshot(string vaultId, Hlc hlc, IReadOnlyDictionary<string, long> covers, IReadOnlyList<ChangeRecord> records)
    {
        ArgumentNullException.ThrowIfNull(covers);
        ArgumentNullException.ThrowIfNull(records);
        var coverMembers = covers
            .OrderBy(c => c.Key, StringComparer.Ordinal)
            .Select(c =>
            {
                if (!DeviceIds.IsValid(c.Key) || c.Value < 0) throw new ArgumentException("covers must map device IDs to integers >= 0.", nameof(covers));
                return new KeyValuePair<string, JsonData>(c.Key, JsonData.Number(c.Value));
            });
        return JsonData.Object(
            ("type", JsonData.String("snapshot")),
            ("formatVersion", JsonData.Number(Envelope.SupportedFormatVersion)),
            ("vaultId", JsonData.String(vaultId)),
            ("deviceId", JsonData.String(hlc.DeviceId)),
            ("hlc", JsonData.String(hlc.Text)),
            ("covers", JsonData.Object(coverMembers)),
            ("records", JsonData.Array(records.Select(r => r.ToJson()))));
    }

    public static JsonData Pairing(string vaultId, long generation, ReadOnlySpan<byte> vaultKey, string deviceId, string expiresAt)
    {
        if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation), generation, "Generation must be at least 1.");
        if (vaultKey.Length != 32) throw new SyncException(SyncErrorCodes.BadKeyLength);
        if (!DeviceIds.IsValid(deviceId)) throw new SyncException(SyncErrorCodes.BadDeviceId);
        if (!SyncDates.IsTimestamp(expiresAt)) throw new ArgumentException("expiresAt must be a timestamp.", nameof(expiresAt));
        return JsonData.Object(
            ("type", JsonData.String("pairing")),
            ("formatVersion", JsonData.Number(Envelope.SupportedFormatVersion)),
            ("vaultId", JsonData.String(vaultId)),
            ("generation", JsonData.Number(generation)),
            ("vaultKey", JsonData.String(Convert.ToHexStringLower(vaultKey))),
            ("deviceId", JsonData.String(deviceId)),
            ("expiresAt", JsonData.String(expiresAt)));
    }

    /// <summary>
    /// "The same records" (section 10.1): equal <c>records</c> arrays, same length, each record equal
    /// as parsed JSON values, in order. Used for upload conflicts and duplicate file names.
    /// </summary>
    public static bool SameRecords(JsonData? a, JsonData? b)
    {
        var ra = a?["records"];
        var rb = b?["records"];
        if (ra is null || rb is null || ra.Kind != JsonDataKind.Array || rb.Kind != JsonDataKind.Array) return false;
        return JsonData.DeepEquals(ra, rb);
    }
}
