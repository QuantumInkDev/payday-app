using System.Security.Cryptography;
using System.Text;

namespace PayDay.Sync.Crypto;

/// <summary>
/// Key derivation (<c>spec/sync-protocol.md</c> section 6.5): HKDF-SHA256 (RFC 5869), salt = 32
/// zero bytes, with fixed ASCII <c>info</c> strings. The vault key itself never encrypts anything.
/// </summary>
public static class SyncKeys
{
    public const int KeyLength = 32;
    public const int KeyCheckLength = 16;

    public static readonly byte[] FileKeyInfo = Encoding.ASCII.GetBytes("futurecheck/v1/file-key");
    public static readonly byte[] KeyCheckInfo = Encoding.ASCII.GetBytes("futurecheck/v1/key-check");
    public static readonly byte[] PairingKeyInfo = Encoding.ASCII.GetBytes("futurecheck/v1/pairing-key");

    private static readonly byte[] ZeroSalt = new byte[32];

    /// <summary>The AES-256-GCM key for change files and snapshots of one key generation.</summary>
    public static byte[] FileKey(ReadOnlySpan<byte> vaultKey) => Derive(vaultKey, FileKeyInfo, KeyLength);

    /// <summary>The 16-byte key check value stored in vault.json (section 7.2).</summary>
    public static byte[] KeyCheck(ReadOnlySpan<byte> vaultKey) => Derive(vaultKey, KeyCheckInfo, KeyCheckLength);

    /// <summary>The key check value as vault.json stores it: 32 lowercase hex digits.</summary>
    public static string KeyCheckHex(ReadOnlySpan<byte> vaultKey) => Convert.ToHexStringLower(KeyCheck(vaultKey));

    /// <summary>The AES-256-GCM key of a pairing file, from the 32-byte pairing secret in the QR code.</summary>
    public static byte[] PairingKey(ReadOnlySpan<byte> pairingSecret) => Derive(pairingSecret, PairingKeyInfo, KeyLength);

    /// <summary>A new random 32-byte vault key or pairing secret.</summary>
    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeyLength);

    private static byte[] Derive(ReadOnlySpan<byte> inputKey, byte[] info, int length)
    {
        if (inputKey.Length != KeyLength) throw new SyncException(SyncErrorCodes.BadKeyLength);
        var output = new byte[length];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, inputKey, output, ZeroSalt, info);
        return output;
    }
}
