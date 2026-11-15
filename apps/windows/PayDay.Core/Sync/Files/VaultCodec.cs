using System.Globalization;
using System.Security.Cryptography;
using PayDay.Sync.Clock;
using PayDay.Sync.Crypto;
using PayDay.Sync.Json;
using PayDay.Sync.Model;

namespace PayDay.Sync.Files;

/// <summary>Why a pairing file was or was not accepted (<see cref="VaultCodec.AcceptPairing"/>).</summary>
public enum PairingStatus
{
    /// <summary>The key matches vault.json's key check for the stated generation: store it and join.</summary>
    Accepted,

    /// <summary><c>expiresAt</c> has passed by the local clock (section 9.1, new device step 2).</summary>
    Expired,

    /// <summary>The vault key does not match vault.json's key check for the stated generation (step 3).</summary>
    KeyCheckMismatch,
}

/// <summary>The outcome of checking a pairing file. <see cref="VaultKey"/> is set only when accepted; the caller owns (and should zero) it.</summary>
public sealed record PairingResult(PairingStatus Status, long Generation, byte[]? VaultKey, string? SenderDeviceId);

/// <summary>
/// End-to-end encoding of vault files for one unlocked vault: payload JSON, strict parsing,
/// envelope checks and the encrypted framing together. This is what a drive provider layer calls
/// with the bytes it uploads and downloads. The codec keeps its own copies of the vault keys and
/// zeroes them on <see cref="Dispose"/>; it never hands them out.
/// </summary>
public sealed class VaultCodec : IDisposable
{
    private readonly Dictionary<uint, byte[]> _keys;
    private bool _disposed;

    /// <param name="vaultId">vault.json's <c>vaultId</c>.</param>
    /// <param name="vaultKeys">Every vault key this device holds, by generation (copied).</param>
    /// <param name="currentGeneration">The generation new files are written with (vault.json <c>currentGeneration</c>).</param>
    public VaultCodec(string vaultId, IReadOnlyDictionary<uint, byte[]> vaultKeys, uint currentGeneration)
    {
        ArgumentNullException.ThrowIfNull(vaultId);
        ArgumentNullException.ThrowIfNull(vaultKeys);
        if (!vaultKeys.ContainsKey(currentGeneration)) throw new ArgumentException("No key for the current generation.", nameof(currentGeneration));
        if (vaultKeys.Values.Any(k => k is null || k.Length != SyncKeys.KeyLength)) throw new SyncException(SyncErrorCodes.BadKeyLength);
        VaultId = vaultId;
        CurrentGeneration = currentGeneration;
        _keys = vaultKeys.ToDictionary(k => k.Key, k => k.Value.ToArray());
    }

    public string VaultId { get; }

    public uint CurrentGeneration { get; }

    /// <summary>The key generations this codec can open.</summary>
    public IReadOnlyCollection<uint> Generations => _keys.Keys.ToArray();

    public bool HasKey(uint generation) => _keys.ContainsKey(generation);

    /// <summary>Encrypts a change file. Returns its path and bytes; a retry calls this again for a fresh nonce.</summary>
    public (string Path, byte[] Data) SealChangeFile(string deviceId, long seq, IReadOnlyList<ChangeRecord> records)
    {
        ThrowIfDisposed();
        var path = SyncPaths.ChangeFilePath(deviceId, seq);
        var plaintext = StrictJson.Serialize(Payloads.ChangeFile(VaultId, deviceId, seq, records));
        return (path, SealAndZero(SyncFileKind.Changes, path, plaintext));
    }

    public (string Path, byte[] Data) SealSnapshot(Hlc hlc, IReadOnlyDictionary<string, long> covers, IReadOnlyList<ChangeRecord> records)
    {
        ThrowIfDisposed();
        var path = SyncPaths.SnapshotPath(hlc);
        var plaintext = StrictJson.Serialize(Payloads.Snapshot(VaultId, hlc, covers, records));
        return (path, SealAndZero(SyncFileKind.Snapshot, path, plaintext));
    }

    /// <summary>Writes the pairing file for a QR code (section 9.1).</summary>
    public (string Path, byte[] Data) SealPairing(PairingCode code, string deviceId, string expiresAt)
    {
        ArgumentNullException.ThrowIfNull(code);
        ThrowIfDisposed();
        var path = code.PairingPath;
        var plaintext = StrictJson.Serialize(Payloads.Pairing(VaultId, CurrentGeneration, _keys[CurrentGeneration], deviceId, expiresAt));
        try
        {
            return (path, FileFrame.Seal(code.PairingSecret, SyncFileKind.Pairing, 0, path, plaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>Opens and checks a change file or snapshot: framing (section 6.6), strict JSON, envelope (section 6.3).</summary>
    public EnvelopeResult Open(string path, ReadOnlySpan<byte> data)
    {
        ThrowIfDisposed();
        return Envelope.CheckBytes(FileFrame.Open(data, path, _keys), path, VaultId);
    }

    /// <summary>
    /// Opens a pairing file on the joining device, which has only the QR code and vault.json, and runs
    /// the envelope checks. Use <see cref="AcceptPairing"/> to also check expiry and the key check value.
    /// </summary>
    public static PairingPayload OpenPairing(string vaultId, PairingCode code, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(code);
        var path = code.PairingPath;
        var plaintext = FileFrame.Open(data, path, vaultKeys: null, pairingSecret: code.PairingSecret);
        try
        {
            return Envelope.CheckBytes(plaintext, path, vaultId).Pairing!;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// The joining device's whole pairing check (section 9.1, new device steps 2 and 3): open the
    /// <c>.pair</c> file with the QR code's secret, run the envelope checks against vault.json's
    /// <c>vaultId</c>, reject it when <c>expiresAt</c> has passed by <paramref name="now"/>, and
    /// accept the key only when it matches vault.json's key check value for the stated generation.
    /// Framing and envelope failures throw <see cref="SyncException"/> with their spec codes.
    /// </summary>
    public static PairingResult AcceptPairing(VaultInfo vault, PairingCode code, ReadOnlySpan<byte> data, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(vault);
        var payload = OpenPairing(vault.VaultId, code, data);
        try
        {
            if (!DateTimeOffset.TryParseExact(payload.ExpiresAt, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expires)
                || now > expires)
            {
                return new PairingResult(PairingStatus.Expired, payload.Generation, null, payload.DeviceId);
            }
            if (VaultFile.MatchGeneration(vault, payload.VaultKey) != payload.Generation)
            {
                return new PairingResult(PairingStatus.KeyCheckMismatch, payload.Generation, null, payload.DeviceId);
            }
            return new PairingResult(PairingStatus.Accepted, payload.Generation, payload.VaultKey.ToArray(), payload.DeviceId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload.VaultKey);
        }
    }

    /// <summary>Zeroes the codec's copies of the vault keys.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        foreach (var key in _keys.Values) CryptographicOperations.ZeroMemory(key);
        _keys.Clear();
        _disposed = true;
    }

    private byte[] SealAndZero(SyncFileKind kind, string path, byte[] plaintext)
    {
        try
        {
            return FileFrame.Seal(_keys[CurrentGeneration], kind, CurrentGeneration, path, plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
