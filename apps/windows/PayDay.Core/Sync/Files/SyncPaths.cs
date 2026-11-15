using System.Globalization;
using PayDay.Sync.Clock;
using PayDay.Sync.Ids;

namespace PayDay.Sync.Files;

public enum SyncPathKind
{
    Changes,
    Snapshot,
    Pairing,
}

/// <summary>
/// A parsed vault-relative path (section 6.1, 6.2). Change files set <see cref="DeviceId"/> and
/// <see cref="Seq"/>; snapshots set <see cref="Hlc"/> and <see cref="DeviceId"/> (the HLC's device);
/// pairing files set <see cref="PairingId"/>.
/// </summary>
public sealed record SyncPath(SyncPathKind Kind, string Path, string? DeviceId = null, long Seq = 0, string? Hlc = null, string? PairingId = null)
{
    /// <summary>The <c>type</c> a payload stored at this path must declare (section 6.3).</summary>
    public string PayloadType
        => Kind switch
        {
            SyncPathKind.Changes => "changes",
            SyncPathKind.Snapshot => "snapshot",
            _ => "pairing",
        };
}

/// <summary>Drive layout and file names (<c>spec/sync-protocol.md</c> sections 6.1 and 6.2).</summary>
public static class SyncPaths
{
    public const string DevicesFolder = "devices";
    public const string SnapshotsFolder = "snapshots";
    public const string PairingFolder = "pairing";
    public const string VaultFileName = "vault.json";

    /// <summary><c>&lt;seq&gt;.chg</c>, zero-padded to at least 6 digits. Fails with <c>badSeq</c> for seq &lt; 1.</summary>
    public static string ChangeFileName(long seq)
    {
        if (seq < 1) throw new SyncException(SyncErrorCodes.BadSeq);
        return seq.ToString("D6", CultureInfo.InvariantCulture) + ".chg";
    }

    public static string ChangeFilePath(string deviceId, long seq)
    {
        if (!DeviceIds.IsValid(deviceId)) throw new SyncException(SyncErrorCodes.BadDeviceId);
        return $"{DevicesFolder}/{deviceId}/{ChangeFileName(seq)}";
    }

    public static string SnapshotPath(Hlc hlc) => $"{SnapshotsFolder}/{hlc.Text}.snap";

    public static string PairingPath(string pairingIdHex)
    {
        if (!DeviceIds.IsLowerHex(pairingIdHex, 32)) throw new ArgumentException("Pairing ID must be 32 lowercase hex characters.", nameof(pairingIdHex));
        return $"{PairingFolder}/{pairingIdHex}.pair";
    }

    /// <summary>
    /// The sequence number of a change file name, or false when the name is not exactly the
    /// formatting of its own number (<c>00001.chg</c>, <c>0000001.chg</c>, <c>000000.chg</c>,
    /// <c>000001.CHG</c> are not change files).
    /// </summary>
    public static bool TryParseChangeFileName(string? name, out long seq)
    {
        seq = 0;
        if (name is null || !name.EndsWith(".chg", StringComparison.Ordinal)) return false;
        var digits = name[..^4];
        if (digits.Length < 6) return false;
        foreach (var c in digits)
        {
            if (c is < '0' or > '9') return false;
        }
        if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1) return false;
        if (ChangeFileName(n) != name) return false;
        seq = n;
        return true;
    }

    /// <summary>Parses a vault-relative path, failing with <c>badPath</c> for anything that is not a sync file.</summary>
    public static SyncPath Parse(string path)
        => TryParse(path, out var parsed) ? parsed! : throw new SyncException(SyncErrorCodes.BadPath);

    public static bool TryParse(string? path, out SyncPath? parsed)
    {
        parsed = null;
        if (path is null) return false;
        var parts = path.Split('/');
        if (parts.Length == 3 && parts[0] == DevicesFolder && DeviceIds.IsValid(parts[1])
            && TryParseChangeFileName(parts[2], out var seq))
        {
            parsed = new SyncPath(SyncPathKind.Changes, path, DeviceId: parts[1], Seq: seq);
            return true;
        }
        if (parts.Length == 2 && parts[0] == SnapshotsFolder && parts[1].EndsWith(".snap", StringComparison.Ordinal)
            && Hlc.TryParse(parts[1][..^5], out var hlc))
        {
            parsed = new SyncPath(SyncPathKind.Snapshot, path, DeviceId: hlc.DeviceId, Hlc: hlc.Text);
            return true;
        }
        if (parts.Length == 2 && parts[0] == PairingFolder && parts[1].EndsWith(".pair", StringComparison.Ordinal)
            && DeviceIds.IsLowerHex(parts[1][..^5], 32))
        {
            parsed = new SyncPath(SyncPathKind.Pairing, path, PairingId: parts[1][..^5]);
            return true;
        }
        return false;
    }
}
