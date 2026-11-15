using System.Security.Cryptography;

namespace PayDay.Sync.Ids;

/// <summary>Device IDs (<c>spec/sync-protocol.md</c> section 1): 8 random bytes as 16 lowercase hex characters.</summary>
public static class DeviceIds
{
    public const int Length = 16;

    public static bool IsValid(string? id)
    {
        if (id is null || id.Length != Length) return false;
        foreach (var c in id)
        {
            if (!IsLowerHex(c)) return false;
        }
        return true;
    }

    /// <summary>A fresh device ID from a cryptographically secure generator. Made on every create or join.</summary>
    public static string NewRandom() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));

    internal static bool IsLowerHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f';

    internal static bool IsLowerHex(string? s, int length)
    {
        if (s is null || s.Length != length) return false;
        foreach (var c in s)
        {
            if (!IsLowerHex(c)) return false;
        }
        return true;
    }
}
