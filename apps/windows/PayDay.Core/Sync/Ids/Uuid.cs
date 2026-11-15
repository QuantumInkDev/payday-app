using System.Security.Cryptography;
using System.Text;

namespace PayDay.Sync.Ids;

/// <summary>What <see cref="Uuid.Inspect"/> found. <see cref="UnixMs"/> is set only for version 7.</summary>
public sealed record UuidInfo(bool Canonical, int? Version = null, bool? Rfc9562Variant = null, long? UnixMs = null);

/// <summary>UUIDv5 and UUIDv7 exactly as <c>spec/sync-protocol.md</c> section 3 lays them out.</summary>
public static class Uuid
{
    /// <summary>The FutureCheck UUIDv5 namespace (section 3.2).</summary>
    public const string Namespace = "def9d7a0-dc52-41e0-906a-f9767c7f894d";

    /// <summary>Canonical form: lowercase hex, 8-4-4-4-12, 36 characters.</summary>
    public static bool IsCanonical(string? s) => IsShaped(s, lowercaseOnly: true);

    /// <summary>8-4-4-4-12 hex in any letter case.</summary>
    public static bool IsUuidShaped(string? s) => IsShaped(s, lowercaseOnly: false);

    /// <summary>RFC 9562 UUIDv5: SHA-1 of the namespace bytes then the UTF-8 name, version 5, variant 10.</summary>
    public static string V5(string namespaceUuid, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!IsUuidShaped(namespaceUuid)) throw new ArgumentException("Namespace must be a UUID.", nameof(namespaceUuid));
        var ns = Convert.FromHexString(namespaceUuid.Replace("-", string.Empty, StringComparison.Ordinal));
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[ns.Length + nameBytes.Length];
        ns.CopyTo(input, 0);
        nameBytes.CopyTo(input, ns.Length);
        var digest = SHA1.HashData(input);
        var b = digest.AsSpan(0, 16).ToArray();
        b[6] = (byte)((b[6] & 0x0F) | 0x50);
        b[8] = (byte)((b[8] & 0x3F) | 0x80);
        return Format(b);
    }

    /// <summary>UUIDv5 in the FutureCheck namespace.</summary>
    public static string V5(string name) => V5(Namespace, name);

    /// <summary>UUIDv7 from a 48-bit Unix-ms timestamp and 10 random bytes (section 3.1); <c>badInput</c> otherwise.</summary>
    public static string V7(long unixMs, ReadOnlySpan<byte> random10)
    {
        if (unixMs < 0 || unixMs >= 1L << 48 || random10.Length != 10) throw new SyncException(SyncErrorCodes.BadInput);
        var b = new byte[16];
        for (var i = 0; i < 6; i++)
        {
            b[i] = (byte)(unixMs >> (8 * (5 - i)));
        }
        b[6] = (byte)(0x70 | (random10[0] & 0x0F));
        b[7] = random10[1];
        b[8] = (byte)(0x80 | (random10[2] & 0x3F));
        random10[3..10].CopyTo(b.AsSpan(9));
        return Format(b);
    }

    /// <summary>A new random UUIDv7 stamped with the given clock.</summary>
    public static string NewV7(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        return V7(time.GetUtcNow().ToUnixTimeMilliseconds(), RandomNumberGenerator.GetBytes(10));
    }

    /// <summary>Layout check (fixture <c>inspectUuid</c>).</summary>
    public static UuidInfo Inspect(string? s)
    {
        if (!IsCanonical(s)) return new UuidInfo(false);
        var raw = Convert.FromHexString(s!.Replace("-", string.Empty, StringComparison.Ordinal));
        var version = raw[6] >> 4;
        var variantOk = (raw[8] & 0xC0) == 0x80;
        long? ms = null;
        if (version == 7)
        {
            long t = 0;
            for (var i = 0; i < 6; i++) t = (t << 8) | raw[i];
            ms = t;
        }
        return new UuidInfo(true, version, variantOk, ms);
    }

    private static string Format(ReadOnlySpan<byte> b)
    {
        var h = Convert.ToHexStringLower(b);
        return $"{h[..8]}-{h[8..12]}-{h[12..16]}-{h[16..20]}-{h[20..32]}";
    }

    private static bool IsShaped(string? s, bool lowercaseOnly)
    {
        if (s is null || s.Length != 36) return false;
        for (var i = 0; i < 36; i++)
        {
            var c = s[i];
            if (i is 8 or 13 or 18 or 23)
            {
                if (c != '-') return false;
            }
            else if (!(DeviceIds.IsLowerHex(c) || (!lowercaseOnly && c is >= 'A' and <= 'F')))
            {
                return false;
            }
        }
        return true;
    }
}
