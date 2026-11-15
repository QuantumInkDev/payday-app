using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PayDay.Sync.Crypto;

/// <summary>The kind byte of an encrypted file (section 6.6).</summary>
public enum SyncFileKind : byte
{
    Changes = 1,
    Snapshot = 2,
    Pairing = 3,
}

/// <summary>
/// The encrypted file framing of <c>spec/sync-protocol.md</c> section 6.6:
/// <c>FCSY | version | kind | compression | reserved | generation (u32 BE) | nonce (12) | ciphertext | tag (16)</c>,
/// AES-256-GCM with AAD = header bytes 0–11 followed by the UTF-8 vault-relative path.
/// </summary>
public static class FileFrame
{
    public const int HeaderLength = 24;
    public const int NonceLength = 12;
    public const int TagLength = 16;
    public const int MinLength = HeaderLength + TagLength;
    public const byte FormatVersion = 1;

    private static readonly byte[] Magic = "FCSY"u8.ToArray();

    /// <summary>The kind a path's extension requires, or null for any other name.</summary>
    public static SyncFileKind? KindForPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.EndsWith(".chg", StringComparison.Ordinal)) return SyncFileKind.Changes;
        if (path.EndsWith(".snap", StringComparison.Ordinal)) return SyncFileKind.Snapshot;
        if (path.EndsWith(".pair", StringComparison.Ordinal)) return SyncFileKind.Pairing;
        return null;
    }

    /// <summary>The 24-byte header. Fails with <c>badNonceLength</c> unless the nonce is 12 bytes.</summary>
    public static byte[] Header(SyncFileKind kind, uint generation, ReadOnlySpan<byte> nonce)
    {
        if (nonce.Length != NonceLength) throw new SyncException(SyncErrorCodes.BadNonceLength);
        var h = new byte[HeaderLength];
        Magic.CopyTo(h, 0);
        h[4] = FormatVersion;
        h[5] = (byte)kind;
        h[6] = 0; // compression: none (section 6.4)
        h[7] = 0; // reserved
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(8, 4), generation);
        nonce.CopyTo(h.AsSpan(12));
        return h;
    }

    /// <summary>Associated data: header bytes 0–11 then the UTF-8 path, no separator.</summary>
    public static byte[] Aad(ReadOnlySpan<byte> header, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var p = Encoding.UTF8.GetBytes(path);
        var aad = new byte[12 + p.Length];
        header[..12].CopyTo(aad);
        p.CopyTo(aad, 12);
        return aad;
    }

    /// <summary>
    /// Seals a file with a caller-supplied nonce. Internal: only the conformance tests pin a nonce;
    /// every real file gets a fresh random one (section 6.6) through the public overload.
    /// <paramref name="secret"/> is the vault key of <paramref name="generation"/> for change files
    /// and snapshots, or the pairing secret for a pairing file (generation 0).
    /// </summary>
    internal static byte[] Seal(ReadOnlySpan<byte> secret, SyncFileKind kind, uint generation, ReadOnlySpan<byte> nonce, string path, ReadOnlySpan<byte> plaintext)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown file kind.");
        if ((kind == SyncFileKind.Pairing) != (generation == 0))
        {
            throw new ArgumentException("Pairing files use key generation 0; other files a generation of at least 1.", nameof(generation));
        }
        if (KindForPath(path) != kind) throw new ArgumentException("The path's extension does not match the file kind.", nameof(path));
        var header = Header(kind, generation, nonce);
        var key = kind == SyncFileKind.Pairing ? SyncKeys.PairingKey(secret) : SyncKeys.FileKey(secret);
        try
        {
            var output = new byte[HeaderLength + plaintext.Length + TagLength];
            header.CopyTo(output, 0);
            using var aes = new AesGcm(key, TagLength);
            aes.Encrypt(
                nonce,
                plaintext,
                output.AsSpan(HeaderLength, plaintext.Length),
                output.AsSpan(HeaderLength + plaintext.Length, TagLength),
                Aad(header, path));
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Seals a file with a fresh random nonce (every file written, including every upload retry).
    /// <paramref name="secret"/> is the vault key of <paramref name="generation"/> for change files
    /// and snapshots, or the pairing secret for a pairing file (generation 0).
    /// </summary>
    public static byte[] Seal(ReadOnlySpan<byte> secret, SyncFileKind kind, uint generation, string path, ReadOnlySpan<byte> plaintext)
        => Seal(secret, kind, generation, RandomNumberGenerator.GetBytes(NonceLength), path, plaintext);

    /// <summary>
    /// Opens a file, running the checks of section 6.6 in order and failing with the first one's code:
    /// <c>tooShort</c>, <c>badMagic</c>, <c>unsupportedVersion</c>, <c>unknownKind</c>,
    /// <c>unsupportedCompression</c>, <c>reservedNonZero</c>, <c>kindMismatch</c>,
    /// <c>badKeyGeneration</c>, <c>unknownKeyGeneration</c>, <c>authFailed</c>.
    /// </summary>
    /// <param name="vaultKeys">Vault keys by generation, or null.</param>
    /// <param name="pairingSecret">The pairing secret when opening a pairing file, or null.</param>
    public static byte[] Open(ReadOnlySpan<byte> data, string path, IReadOnlyDictionary<uint, byte[]>? vaultKeys, byte[]? pairingSecret = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (data.Length < MinLength) throw new SyncException(SyncErrorCodes.TooShort);
        if (!data[..4].SequenceEqual(Magic)) throw new SyncException(SyncErrorCodes.BadMagic);
        if (data[4] != FormatVersion) throw new SyncException(SyncErrorCodes.UnsupportedVersion);
        var kindByte = data[5];
        if (kindByte is < 1 or > 3) throw new SyncException(SyncErrorCodes.UnknownKind);
        if (data[6] != 0) throw new SyncException(SyncErrorCodes.UnsupportedCompression);
        if (data[7] != 0) throw new SyncException(SyncErrorCodes.ReservedNonZero);
        var kind = (SyncFileKind)kindByte;
        if (KindForPath(path) != kind) throw new SyncException(SyncErrorCodes.KindMismatch);
        var generation = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8, 4));

        byte[] key;
        if (kind == SyncFileKind.Pairing)
        {
            if (generation != 0) throw new SyncException(SyncErrorCodes.BadKeyGeneration);
            if (pairingSecret is null) throw new SyncException(SyncErrorCodes.UnknownKeyGeneration);
            key = SyncKeys.PairingKey(pairingSecret);
        }
        else
        {
            if (generation == 0) throw new SyncException(SyncErrorCodes.BadKeyGeneration);
            if (vaultKeys is null || !vaultKeys.TryGetValue(generation, out var vaultKey))
            {
                throw new SyncException(SyncErrorCodes.UnknownKeyGeneration);
            }
            key = SyncKeys.FileKey(vaultKey);
        }

        var nonce = data.Slice(12, NonceLength);
        var cipherLength = data.Length - MinLength;
        var ciphertext = data.Slice(HeaderLength, cipherLength);
        var tag = data.Slice(HeaderLength + cipherLength, TagLength);
        var plaintext = new byte[cipherLength];
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Aad(data[..HeaderLength], path));
        }
        catch (CryptographicException ex)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new SyncException(SyncErrorCodes.AuthFailed, "GCM authentication failed", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
        return plaintext;
    }

    /// <summary>The key generation named in a file's header (no other checks).</summary>
    public static uint ReadGeneration(ReadOnlySpan<byte> data)
    {
        if (data.Length < MinLength) throw new SyncException(SyncErrorCodes.TooShort);
        return BinaryPrimitives.ReadUInt32BigEndian(data.Slice(8, 4));
    }
}
