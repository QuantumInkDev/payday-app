using PayDay.Sync.Clock;
using PayDay.Sync.Ids;
using PayDay.Sync.Json;
using PayDay.Sync.Model;

namespace PayDay.Sync.Files;

/// <summary>The decoded contents of a pairing file payload (section 9.1).</summary>
public sealed record PairingPayload(string VaultId, long Generation, byte[] VaultKey, string DeviceId, string ExpiresAt);

/// <summary>
/// A payload that passed the envelope checks. For change files and snapshots, <see cref="Records"/>
/// holds the records to merge: those that are valid (section 4.1) and, in a change file, stamped by
/// the file's own device. <see cref="IgnoredRecords"/> lists the indexes of change-file records
/// stamped by another device (or with no valid HLC).
/// </summary>
public sealed class EnvelopeResult
{
    internal EnvelopeResult(SyncPath path, JsonData payload)
    {
        Path = path;
        Payload = payload;
    }

    public SyncPath Path { get; }

    public JsonData Payload { get; }

    public IReadOnlyList<int> IgnoredRecords { get; internal set; } = Array.Empty<int>();

    public IReadOnlyList<ChangeRecord> Records { get; internal set; } = Array.Empty<ChangeRecord>();

    /// <summary>How many entries of <c>records</c> were invalid records (section 4.1) and are not in <see cref="Records"/>.</summary>
    public int InvalidRecordCount { get; internal set; }

    /// <summary>The change file's sequence number.</summary>
    public long Seq { get; internal set; }

    /// <summary>The snapshot's HLC.</summary>
    public Hlc? SnapshotHlc { get; internal set; }

    /// <summary>The snapshot's <c>covers</c> map.</summary>
    public IReadOnlyDictionary<string, long> Covers { get; internal set; } = new Dictionary<string, long>();

    /// <summary>The pairing payload, for a pairing file.</summary>
    public PairingPayload? Pairing { get; internal set; }
}

/// <summary>Envelope checks on a decrypted payload (<c>spec/sync-protocol.md</c> section 6.3), in the spec's order.</summary>
public static class Envelope
{
    public const long SupportedFormatVersion = 1;

    /// <summary>Strict-parses the plaintext (section 6.3), then runs <see cref="Check"/>.</summary>
    public static EnvelopeResult CheckBytes(ReadOnlySpan<byte> plaintext, string path, string vaultId)
        => Check(StrictJson.Parse(plaintext), path, vaultId);

    /// <summary>
    /// Validates a parsed payload against its path and the vault ID. Fails with <c>badPath</c>,
    /// <c>malformed</c>, <c>typeMismatch</c>, <c>unsupportedFormat</c>, <c>vaultMismatch</c>,
    /// <c>deviceMismatch</c>, <c>seqMismatch</c> or <c>hlcMismatch</c>, reporting the first failure.
    /// </summary>
    public static EnvelopeResult Check(JsonData payload, string path, string vaultId)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var where = SyncPaths.Parse(path);
        if (payload.Kind != JsonDataKind.Object) throw new SyncException(SyncErrorCodes.Malformed, "payload is not an object");
        if (payload["type"]?.AsString() != where.PayloadType) throw new SyncException(SyncErrorCodes.TypeMismatch);
        CheckFormatVersion(payload);
        if (payload["vaultId"]?.AsString() != vaultId) throw new SyncException(SyncErrorCodes.VaultMismatch);

        var result = new EnvelopeResult(where, payload);
        if (where.Kind == SyncPathKind.Pairing)
        {
            result.Pairing = ReadPairing(payload, vaultId);
            return result;
        }

        if (payload["deviceId"]?.AsString() != where.DeviceId) throw new SyncException(SyncErrorCodes.DeviceMismatch);
        if (where.Kind == SyncPathKind.Changes)
        {
            if (!StrictJson.TryGetInteger(payload["seq"], out var seq)) throw new SyncException(SyncErrorCodes.Malformed, "seq is not an integer");
            if (seq != where.Seq) throw new SyncException(SyncErrorCodes.SeqMismatch);
            result.Seq = seq;
        }
        else
        {
            if (payload["hlc"]?.AsString() != where.Hlc) throw new SyncException(SyncErrorCodes.HlcMismatch);
            result.SnapshotHlc = Hlc.Parse(where.Hlc);
            result.Covers = ReadCovers(payload["covers"]);
        }

        var records = payload["records"];
        if (records is null || records.Kind != JsonDataKind.Array) throw new SyncException(SyncErrorCodes.Malformed, "records is not an array");

        var ignored = new List<int>();
        var valid = new List<ChangeRecord>();
        var invalid = 0;
        for (var i = 0; i < records.Items.Count; i++)
        {
            var item = records.Items[i];
            if (where.Kind == SyncPathKind.Changes)
            {
                var h = item.Kind == JsonDataKind.Object ? item["hlc"]?.AsString() : null;
                if (!Hlc.TryParse(h, out var hlc) || hlc.DeviceId != where.DeviceId)
                {
                    ignored.Add(i);
                    continue;
                }
            }
            if (ChangeRecord.TryFromJson(item, out var record))
            {
                valid.Add(record!);
            }
            else
            {
                invalid++;
            }
        }
        result.IgnoredRecords = ignored;
        result.Records = valid;
        result.InvalidRecordCount = invalid;
        return result;
    }

    private static void CheckFormatVersion(JsonData obj)
    {
        if (!StrictJson.TryGetInteger(obj["formatVersion"], out var v)) throw new SyncException(SyncErrorCodes.Malformed, "formatVersion is not an integer");
        if (v != SupportedFormatVersion) throw new SyncException(SyncErrorCodes.UnsupportedFormat);
    }

    internal static void CheckFormatVersionOf(JsonData obj) => CheckFormatVersion(obj);

    private static IReadOnlyDictionary<string, long> ReadCovers(JsonData? covers)
    {
        if (covers is null || covers.Kind != JsonDataKind.Object) throw new SyncException(SyncErrorCodes.Malformed, "covers is not an object");
        var map = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (device, value) in covers.Members)
        {
            if (!DeviceIds.IsValid(device) || !StrictJson.TryGetInteger(value, out var seq) || seq < 0)
            {
                throw new SyncException(SyncErrorCodes.Malformed, "covers must map device IDs to integers >= 0");
            }
            map[device] = seq;
        }
        return map;
    }

    private static PairingPayload ReadPairing(JsonData payload, string vaultId)
    {
        var key = payload["vaultKey"]?.AsString();
        var device = payload["deviceId"]?.AsString();
        var expires = payload["expiresAt"]?.AsString();
        if (!StrictJson.TryGetInteger(payload["generation"], out var gen) || gen < 1
            || !DeviceIds.IsLowerHex(key, 64)
            || !DeviceIds.IsValid(device)
            || !SyncDates.IsTimestamp(expires))
        {
            throw new SyncException(SyncErrorCodes.Malformed, "bad pairing payload");
        }
        return new PairingPayload(vaultId, gen, Convert.FromHexString(key!), device!, expires!);
    }
}
