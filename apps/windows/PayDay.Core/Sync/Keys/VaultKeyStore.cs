using System.Globalization;
using System.Security.Cryptography;
using PayDay.Services;
using PayDay.Sync.Crypto;

namespace PayDay.Sync.Keys;

/// <summary>
/// Where this device keeps its vault keys (<c>data-model.md</c> 6.2: the OS keystore, never the drive).
/// One 32-byte key per (vault, key generation). Callers zero the arrays they get back.
/// </summary>
public interface IVaultKeyStore
{
    /// <summary>The key of <paramref name="generation"/> of <paramref name="vaultId"/>, or null when this device does not hold it.</summary>
    byte[]? Load(string vaultId, long generation);

    void Save(string vaultId, long generation, ReadOnlySpan<byte> key);

    /// <summary>Forgets the keys of every listed generation (turning sync off, <c>sync-protocol.md</c> 12.4).</summary>
    void Delete(string vaultId, IEnumerable<long> generations);
}

/// <summary>
/// <see cref="IVaultKeyStore"/> over an <see cref="ICredentialStore"/>: on Windows the app passes its
/// <c>WindowsCredentialStore</c> (Credential Manager, DPAPI-protected per user), so each key is the
/// generic credential <c>PayDay:VaultKey/&lt;vaultId&gt;/&lt;generation&gt;</c>, stored as hex. Tests pass
/// an in-memory credential store.
/// </summary>
public sealed class CredentialVaultKeyStore : IVaultKeyStore
{
    private readonly ICredentialStore _credentials;

    public CredentialVaultKeyStore(ICredentialStore credentials)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    }

    public static string KeyName(string vaultId, long generation)
        => $"VaultKey/{vaultId}/{generation.ToString(CultureInfo.InvariantCulture)}";

    public byte[]? Load(string vaultId, long generation)
    {
        ArgumentNullException.ThrowIfNull(vaultId);
        var hex = _credentials.Get(KeyName(vaultId, generation));
        if (hex is null || hex.Length != SyncKeys.KeyLength * 2) return null;
        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public void Save(string vaultId, long generation, ReadOnlySpan<byte> key)
    {
        ArgumentNullException.ThrowIfNull(vaultId);
        if (key.Length != SyncKeys.KeyLength) throw new SyncException(SyncErrorCodes.BadKeyLength);
        _credentials.Set(KeyName(vaultId, generation), Convert.ToHexStringLower(key));
    }

    public void Delete(string vaultId, IEnumerable<long> generations)
    {
        ArgumentNullException.ThrowIfNull(vaultId);
        ArgumentNullException.ThrowIfNull(generations);
        foreach (var g in generations) _credentials.Delete(KeyName(vaultId, g));
    }
}

/// <summary>An in-memory <see cref="IVaultKeyStore"/> for tests. <see cref="Clear"/> simulates a wiped keystore.</summary>
public sealed class InMemoryVaultKeyStore : IVaultKeyStore
{
    private readonly Dictionary<(string, long), byte[]> _keys = new();
    private readonly object _lock = new();

    public int Count
    {
        get
        {
            lock (_lock) return _keys.Count;
        }
    }

    public byte[]? Load(string vaultId, long generation)
    {
        lock (_lock) return _keys.TryGetValue((vaultId, generation), out var k) ? k.ToArray() : null;
    }

    public void Save(string vaultId, long generation, ReadOnlySpan<byte> key)
    {
        if (key.Length != SyncKeys.KeyLength) throw new SyncException(SyncErrorCodes.BadKeyLength);
        var copy = key.ToArray();
        lock (_lock) _keys[(vaultId, generation)] = copy;
    }

    public void Delete(string vaultId, IEnumerable<long> generations)
    {
        ArgumentNullException.ThrowIfNull(generations);
        lock (_lock)
        {
            foreach (var g in generations)
            {
                if (_keys.Remove((vaultId, g), out var k)) CryptographicOperations.ZeroMemory(k);
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            foreach (var k in _keys.Values) CryptographicOperations.ZeroMemory(k);
            _keys.Clear();
        }
    }
}
