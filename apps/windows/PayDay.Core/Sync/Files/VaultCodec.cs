using PayDay.Sync.Clock;
using PayDay.Sync.Crypto;
using PayDay.Sync.Json;
using PayDay.Sync.Model;

namespace PayDay.Sync.Files;

/// <summary>
/// End-to-end encoding of vault files for one unlocked vault: payload JSON, strict parsing,
/// envelope checks and the encrypted framing together. This is what a drive provider layer calls
/// with the bytes it uploads and downloads.
/// </summary>
public sealed class VaultCodec
{
    private readonly Dictionary<uint, byte[]> _keys;

    /// <param name="vaultId">vault.json's <c>vaultId</c>.</param>
    /// <param name="vaultKeys">Every vault key this device holds, by generation.</param>
    /// <param name="currentGeneration">The generation new files are written with (vault.json <c>currentGeneration</c>).</param>
    public VaultCodec(string vaultId, IReadOnlyDictionary<uint, byte[]> vaultKeys, uint currentGeneration)
    {
        ArgumentNullException.ThrowIfNull(vaultId);
        ArgumentNullException.ThrowIfNull(vaultKeys);
        if (!vaultKeys.ContainsKey(currentGeneration)) throw new ArgumentException("No key for the current generation.", nameof(currentGeneration));
        VaultId = vaultId;
        CurrentGeneration = currentGeneration;
        _keys = vaultKeys.ToDictionary(k => k.Key, k => k.Value.ToArray());
    }

    public string VaultId { get; }

    public uint CurrentGeneration { get; }

    public IReadOnlyDictionary<uint, byte[]> Keys => _keys;

    /// <summary>Encrypts a change file. Returns its path and bytes; a retry calls this again for a fresh nonce.</summary>
    public (string Path, byte[] Data) SealChangeFile(string deviceId, long seq, IReadOnlyList<ChangeRecord> records)
    {
        var path = SyncPaths.ChangeFilePath(deviceId, seq);
        var plaintext = StrictJson.Serialize(Payloads.ChangeFile(VaultId, deviceId, seq, records));
        return (path, FileFrame.Seal(_keys[CurrentGeneration], SyncFileKind.Changes, CurrentGeneration, path, plaintext));
    }

    public (string Path, byte[] Data) SealSnapshot(Hlc hlc, IReadOnlyDictionary<string, long> covers, IReadOnlyList<ChangeRecord> records)
    {
        var path = SyncPaths.SnapshotPath(hlc);
        var plaintext = StrictJson.Serialize(Payloads.Snapshot(VaultId, hlc, covers, records));
        return (path, FileFrame.Seal(_keys[CurrentGeneration], SyncFileKind.Snapshot, CurrentGeneration, path, plaintext));
    }

    /// <summary>Writes the pairing file for a QR code (section 9.1).</summary>
    public (string Path, byte[] Data) SealPairing(PairingCode code, string deviceId, string expiresAt)
    {
        ArgumentNullException.ThrowIfNull(code);
        var path = code.PairingPath;
        var plaintext = StrictJson.Serialize(Payloads.Pairing(VaultId, CurrentGeneration, _keys[CurrentGeneration], deviceId, expiresAt));
        return (path, FileFrame.Seal(code.PairingSecret, SyncFileKind.Pairing, 0, path, plaintext));
    }

    /// <summary>Opens and checks a change file or snapshot: framing (section 6.6), strict JSON, envelope (section 6.3).</summary>
    public EnvelopeResult Open(string path, ReadOnlySpan<byte> data)
        => Envelope.CheckBytes(FileFrame.Open(data, path, _keys), path, VaultId);

    /// <summary>Opens a pairing file on the joining device, which has only the QR code and vault.json.</summary>
    public static PairingPayload OpenPairing(string vaultId, PairingCode code, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(code);
        var path = code.PairingPath;
        var plaintext = FileFrame.Open(data, path, vaultKeys: null, pairingSecret: code.PairingSecret);
        return Envelope.CheckBytes(plaintext, path, vaultId).Pairing!;
    }
}
