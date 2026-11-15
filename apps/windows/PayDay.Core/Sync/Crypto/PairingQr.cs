namespace PayDay.Sync.Crypto;

/// <summary>A parsed pairing QR code: the pairing ID (16 bytes) and pairing secret (32 bytes).</summary>
public sealed record PairingCode(byte[] PairingId, byte[] PairingSecret)
{
    /// <summary>The pairing ID as the file name uses it: 32 lowercase hex characters.</summary>
    public string PairingIdHex => Convert.ToHexStringLower(PairingId);

    /// <summary>The pairing file's vault-relative path.</summary>
    public string PairingPath => Files.SyncPaths.PairingPath(PairingIdHex);
}

/// <summary>The QR text of section 9.1: <c>FCPAIR:1:&lt;pairingId, 32 uppercase hex&gt;:&lt;secret, 64 uppercase hex&gt;</c>.</summary>
public static class PairingQr
{
    public const string Prefix = "FCPAIR:1:";
    public const int PairingIdLength = 16;
    public const int SecretLength = 32;

    /// <summary>A new random pairing ID and secret.</summary>
    public static PairingCode NewCode()
        => new(System.Security.Cryptography.RandomNumberGenerator.GetBytes(PairingIdLength), SyncKeys.NewKey());

    public static string Format(ReadOnlySpan<byte> pairingId, ReadOnlySpan<byte> secret)
    {
        if (pairingId.Length != PairingIdLength) throw new ArgumentException("Pairing ID must be 16 bytes.", nameof(pairingId));
        if (secret.Length != SecretLength) throw new SyncException(SyncErrorCodes.BadKeyLength);
        return Prefix + Convert.ToHexString(pairingId) + ":" + Convert.ToHexString(secret);
    }

    public static string Format(PairingCode code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return Format(code.PairingId, code.PairingSecret);
    }

    /// <summary>
    /// Parses scanned text: case-insensitive (ASCII letters), surrounding whitespace ignored,
    /// anything else fails with <c>badQr</c>.
    /// </summary>
    public static PairingCode Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var t = text.Trim();
        var upper = new char[t.Length];
        for (var i = 0; i < t.Length; i++)
        {
            var c = t[i];
            upper[i] = c is >= 'a' and <= 'z' ? (char)(c - 32) : c;
        }
        var s = new string(upper);
        const int expected = 9 + 32 + 1 + 64;
        if (s.Length != expected || !s.StartsWith(Prefix, StringComparison.Ordinal) || s[9 + 32] != ':')
        {
            throw new SyncException(SyncErrorCodes.BadQr);
        }
        var idHex = s.Substring(9, 32);
        var secretHex = s.Substring(9 + 32 + 1, 64);
        if (!IsUpperHex(idHex) || !IsUpperHex(secretHex)) throw new SyncException(SyncErrorCodes.BadQr);
        return new PairingCode(Convert.FromHexString(idHex), Convert.FromHexString(secretHex));
    }

    private static bool IsUpperHex(string s)
    {
        foreach (var c in s)
        {
            if (c is not (>= '0' and <= '9' or >= 'A' and <= 'F')) return false;
        }
        return true;
    }
}
