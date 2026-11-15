using System.Globalization;
using PayDay.Services;

namespace PayDay.Sync.Drive.OneDrive;

/// <summary>
/// Keeps MSAL's serialized token cache in the app's <see cref="ICredentialStore"/> (on Windows,
/// Credential Manager: per user, DPAPI-protected). A Credential Manager secret holds at most 2,560
/// bytes (1,280 UTF-16 characters) and a token cache is several kilobytes, so the cache is stored
/// base64-encoded in numbered chunks plus a count, written count last. A cache that cannot be read
/// back whole (a chunk missing or damaged) loads as empty: the user signs in again, nothing worse.
/// </summary>
public sealed class CredentialTokenCacheStore
{
    /// <summary>Characters per chunk; well under Credential Manager's 1,280-character limit.</summary>
    public const int ChunkLength = 1000;

    public const string KeyPrefix = "OneDrive/TokenCache";

    private readonly ICredentialStore _credentials;
    private readonly object _lock = new();

    public CredentialTokenCacheStore(ICredentialStore credentials)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    }

    private static string CountKey => KeyPrefix + "/count";

    private static string ChunkKey(int i) => KeyPrefix + "/" + i.ToString(CultureInfo.InvariantCulture);

    /// <summary>The stored cache, or null when there is none (or it cannot be read back whole).</summary>
    public byte[]? Load()
    {
        lock (_lock)
        {
            if (!int.TryParse(_credentials.Get(CountKey), NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count <= 0) return null;
            var text = new System.Text.StringBuilder(count * ChunkLength);
            for (var i = 0; i < count; i++)
            {
                if (_credentials.Get(ChunkKey(i)) is not { } chunk) return null;
                text.Append(chunk);
            }
            try
            {
                return Convert.FromBase64String(text.ToString());
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }

    /// <summary>Replaces the stored cache (an empty one clears it).</summary>
    public void Save(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        lock (_lock)
        {
            if (data.Length == 0)
            {
                ClearLocked();
                return;
            }
            var text = Convert.ToBase64String(data);
            var count = (text.Length + ChunkLength - 1) / ChunkLength;
            var oldCount = int.TryParse(_credentials.Get(CountKey), NumberStyles.None, CultureInfo.InvariantCulture, out var c) ? c : 0;
            for (var i = 0; i < count; i++)
            {
                _credentials.Set(ChunkKey(i), text.Substring(i * ChunkLength, Math.Min(ChunkLength, text.Length - i * ChunkLength)));
            }
            _credentials.Set(CountKey, count.ToString(CultureInfo.InvariantCulture));
            for (var i = count; i < oldCount; i++) _credentials.Delete(ChunkKey(i));
        }
    }

    /// <summary>Removes every chunk and the count (signing out).</summary>
    public void Clear()
    {
        lock (_lock) ClearLocked();
    }

    private void ClearLocked()
    {
        var oldCount = int.TryParse(_credentials.Get(CountKey), NumberStyles.None, CultureInfo.InvariantCulture, out var c) ? c : 0;
        _credentials.Delete(CountKey);
        for (var i = 0; i < oldCount; i++) _credentials.Delete(ChunkKey(i));
    }
}
