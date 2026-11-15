using System.Globalization;
using System.Security.Cryptography;
using PayDay.Services;
using PayDay.Sync.Crypto;
using PayDay.Sync.Drive;
using PayDay.Sync.Engine;
using PayDay.Sync.Files;
using PayDay.Sync.Ids;
using PayDay.Sync.Json;
using PayDay.Sync.Keys;
using PayDay.Sync.Local;
using PayDay.Sync.Merge;
using PayDay.Sync.Model;
using PayDay.Sync.Reading;

namespace PayDay.Sync.Coordination;

/// <summary>
/// The sync loop of one device against one drive (<c>spec/sync-protocol.md</c> sections 9 to 12):
/// creating a vault, joining one by recovery phrase or QR pairing, and the sync cycle (seal and upload
/// in order, list, read in plan order with cursors, snapshots and compaction, pairing clean-up). It
/// drives <see cref="LocalSync"/>'s replica and never touches the tables itself.
/// <para>
/// <b>Never crashes the app, never loses local edits.</b> Every public method returns a status or a
/// result; a failure is reported in <see cref="Status"/> and retried on a later sync. Local steps
/// (merging a file, sealing, marking an upload) hold <see cref="LocalSync"/>'s write gate briefly and
/// commit on their own; network calls never hold it. One operation runs at a time.
/// </para>
/// </summary>
public sealed class SyncCoordinator : IDisposable
{
    /// <summary>A pairing file lives 10 minutes (section 9.1).</summary>
    public static readonly TimeSpan PairingLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Unreadable files and gaps are reported after this long (section 10.2).</summary>
    public static readonly TimeSpan ReportAfter = TimeSpan.FromHours(24);

    /// <summary><c>sync_device</c> key: <c>creating</c> until vault.json is on the drive, then <c>member</c>.</summary>
    public const string VaultStateKey = "vaultState";

    /// <summary><c>sync_device</c> key: the vault's <c>createdAt</c>, kept until vault.json is uploaded.</summary>
    public const string VaultCreatedAtKey = "vaultCreatedAt";

    /// <summary><c>sync_device</c> key: "1" once a device-ID clash stopped sync (10.1 step 5).</summary>
    public const string IdentityClashKey = "identityClash";

    private const string Creating = "creating";
    private const string Member = "member";

    private readonly LocalSync _sync;
    private readonly IDriveProvider _drive;
    private readonly IVaultKeyStore _keys;
    private readonly BackupRotationService _backups;
    private readonly IDatabaseService _tables;
    private readonly DeviceInfo _device;
    private readonly Func<bool> _hasNotionToken;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly object _statusLock = new();

    // Per identity (reset when the device ID changes): snapshots merged in this process, snapshot covers.
    private string? _identity;
    private readonly HashSet<string> _mergedSnapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyDictionary<string, long>> _snapshotCovers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _problemsSince = new(StringComparer.Ordinal);
    private PairingState? _pairing;
    private SyncStatus _status = SyncStatus.Off;
    private DateTimeOffset? _lastSynced;

    /// <param name="sync">This device's local sync (the replica and the write gate).</param>
    /// <param name="drive">The drive holding the vault folder.</param>
    /// <param name="keys">Where this device keeps vault keys.</param>
    /// <param name="backups">Writes the backups that protect data before sync is turned on and before a join replaces it.</param>
    /// <param name="tables">Today's tables, read only to tell whether this device has data before a join.</param>
    /// <param name="device">What this device's <c>device</c> row says.</param>
    /// <param name="hasNotionToken">True when this device has a Notion token (it then owns the connector in a vault it creates).</param>
    public SyncCoordinator(
        LocalSync sync,
        IDriveProvider drive,
        IVaultKeyStore keys,
        BackupRotationService backups,
        IDatabaseService tables,
        DeviceInfo device,
        Func<bool>? hasNotionToken = null,
        TimeProvider? time = null)
    {
        _sync = sync ?? throw new ArgumentNullException(nameof(sync));
        _drive = drive ?? throw new ArgumentNullException(nameof(drive));
        _keys = keys ?? throw new ArgumentNullException(nameof(keys));
        _backups = backups ?? throw new ArgumentNullException(nameof(backups));
        _tables = tables ?? throw new ArgumentNullException(nameof(tables));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _hasNotionToken = hasNotionToken ?? (() => false);
        _time = time ?? sync.Time;
    }

    /// <summary>Raised whenever <see cref="Status"/> changes (possibly off the UI thread).</summary>
    public event EventHandler<SyncStatus>? StatusChanged;

    /// <summary>Whether the app is in the foreground: snapshots are written only then (section 11.2).</summary>
    public bool InForeground { get; set; } = true;

    public SyncStatus Status
    {
        get
        {
            lock (_statusLock) return _status;
        }
    }

    /// <summary>True when this device is in a vault (or creating one).</summary>
    public bool IsInVault => _sync.Replica is { } r && r.Read((_, m) => m.VaultId) is not null && _sync.GetDeviceSetting(VaultStateKey) is not null;

    public void Dispose() => _operation.Dispose();

    // ==== Creating a vault (12.1) =======================================================================

    /// <summary>
    /// Creates a vault from this device's data (12.1): refuses when vault.json exists; turns sync on
    /// (backup, migration) if it is off; a new vault key and vault ID; this device's <c>device</c> row
    /// and, with a Notion token, <c>NotionOwnerDeviceId</c>; uploads the first change file(s), then
    /// vault.json <b>last</b>. Returns the recovery phrase to show once. When an upload fails the vault
    /// still exists locally and later syncs finish it.
    /// </summary>
    public async Task<CreateVaultResult> CreateVaultAsync(CancellationToken ct = default)
    {
        await _operation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SetStatus(Status with { Phase = SyncPhase.Syncing, Message = "Creating the vault…" });
            var existing = await ReadVaultAsync(ct).ConfigureAwait(false);
            if (existing.Vault is not null)
            {
                var mine = _sync.Replica?.Read((_, m) => m.VaultId);
                var outcome = mine == existing.Vault.VaultId ? CreateVaultOutcome.AlreadyInVault : CreateVaultOutcome.VaultExists;
                return new CreateVaultResult(outcome, existing.Vault.VaultId, null, null, SetIdle());
            }
            if (existing.Phase is { } problem) return new CreateVaultResult(CreateVaultOutcome.VaultExists, null, null, null, Fail(problem, existing.Message!, existing.Code));

            string vaultId;
            byte[] key;
            TurnOnResult? turnOn = null;
            using (var lease = await _sync.EnterWriteAsync(ct).ConfigureAwait(false))
            {
                if (_sync.GetDeviceSetting(VaultStateKey) == Creating && _sync.Replica!.Read((_, m) => m.VaultId) is { } pendingVault
                    && _keys.Load(pendingVault, 1) is { } pendingKey)
                {
                    // A creation that has not reached the drive yet: finish it.
                    vaultId = pendingVault;
                    key = pendingKey;
                }
                else
                {
                    if (!_sync.IsOn)
                    {
                        turnOn = await _sync.TurnOnAsync(_backups, lease, ct).ConfigureAwait(false);
                        if (turnOn.Status == TurnOnStatus.Blocked)
                        {
                            return new CreateVaultResult(CreateVaultOutcome.Blocked, null, null, turnOn, Fail(SyncPhase.Off, "Some rows must be fixed before sync can be turned on.", null));
                        }
                    }
                    vaultId = Uuid.NewV7(_time);
                    key = SyncKeys.NewKey();
                    var replica = _sync.Replica!;
                    if (NeedsNewIdentity(replica))
                    {
                        _sync.StartNewIdentity(vaultId, lease);
                        replica = _sync.Replica!;
                    }
                    else
                    {
                        replica.Update(m => m.VaultId = vaultId);
                    }
                    _keys.Save(vaultId, 1, key);
                    _sync.SetDeviceSetting(VaultCreatedAtKey, Timestamp(_time.GetUtcNow()));
                    _sync.SetDeviceSetting(VaultStateKey, Creating);
                    _sync.SetDeviceSetting(IdentityClashKey, null);
                    WriteDeviceRow(replica);
                    if (_hasNotionToken()) _sync.ContinueNotionAsCreator();
                }
            }
            try
            {
                var phrase = RecoveryPhrase.EncodeToString(key);
                var status = await SyncCoreAsync(ct).ConfigureAwait(false);
                var created = _sync.GetDeviceSetting(VaultStateKey) == Member;
                return new CreateVaultResult(created ? CreateVaultOutcome.Created : CreateVaultOutcome.CreatedUploadPending, vaultId, phrase, turnOn, status);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CreateVaultResult(CreateVaultOutcome.CreatedUploadPending, _sync.Replica?.Read((_, m) => m.VaultId), null, null, Fail(ex));
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>
    /// A device that already synced (it uploaded files, or holds other devices' records) needs a new ID
    /// and re-stamped records to be the first writer of a new vault (12.1 step 5, 12.4). A device that
    /// only migrated keeps its ID, so its migration HLC (and the bills' pre-sync UpdatedAt) stay.
    /// </summary>
    private static bool NeedsNewIdentity(SyncReplica replica)
        => replica.Read((s, m) => m.VaultId is not null
            || m.LastUploadedSeq > 0
            || m.SealedBatches.Count > 0
            || s.AllRecords().Any(r => r.Hlc.DeviceId != replica.DeviceId));

    private void WriteDeviceRow(SyncReplica replica)
    {
        replica.CreateRow(SyncEntities.Device, replica.DeviceId, new Dictionary<string, SyncValue>(StringComparer.Ordinal)
        {
            ["name"] = SyncValue.String(_device.Name),
            ["platform"] = SyncValue.String(_device.Platform),
            ["appVersion"] = SyncValue.StringOrNull(_device.AppVersion),
            ["createdAt"] = SyncValue.String(Timestamp(_time.GetUtcNow())),
        });
    }

    /// <summary>How finishing a vault creation ended.</summary>
    private enum Creation
    {
        Done,
        NotYet,
        LostRace,
    }

    /// <summary>
    /// Uploads vault.json once every batch of the creating device is on the drive (12.1 step 6). When
    /// another device's vault.json won (it was there first, or has the earlier <c>createdAt</c>), this
    /// device's uploaded change files are deleted (no other device can read them) and it must join.
    /// </summary>
    private async Task<Creation> FinishCreationAsync(SyncReplica replica, VaultCodec codec, CancellationToken ct)
    {
        if (replica.NextBatchToUpload is not null) return Creation.NotYet;
        var key = _keys.Load(codec.VaultId, 1);
        if (key is null) return Creation.NotYet;
        try
        {
            var createdAt = _sync.GetDeviceSetting(VaultCreatedAtKey) ?? Timestamp(_time.GetUtcNow());
            var json = StrictJson.Serialize(VaultFile.Create(codec.VaultId, createdAt, replica.DeviceId, key));
            try
            {
                await _drive.UploadNewAsync(SyncPaths.VaultFileName, json, ct).ConfigureAwait(false);
            }
            catch (DriveException e) when (e.Kind == DriveErrorKind.AlreadyExists)
            {
                // Someone created a vault at the same moment; ReadVaultAsync decides below.
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
        var vault = await ReadVaultAsync(ct).ConfigureAwait(false);
        if (vault.Vault is null) return Creation.NotYet; // not listed yet
        if (vault.Vault.VaultId != codec.VaultId)
        {
            var mine = await _drive.ListAsync($"{SyncPaths.DevicesFolder}/{replica.DeviceId}", ct).ConfigureAwait(false);
            foreach (var f in mine.Files) await _drive.DeleteAsync(f, ct).ConfigureAwait(false);
            return Creation.LostRace;
        }
        await MarkMemberAsync(replica, ct).ConfigureAwait(false);
        return Creation.Done;
    }

    private async Task MarkMemberAsync(SyncReplica replica, CancellationToken ct)
    {
        using (await EnterAsync(replica, ct).ConfigureAwait(false))
        {
            _sync.SetDeviceSetting(VaultStateKey, Member);
            _sync.SetDeviceSetting(VaultCreatedAtKey, null);
        }
    }

    // ==== Joining a vault (12.2) ========================================================================

    /// <summary>Joins the vault on the drive with the 24-word recovery phrase (sections 8 and 12.2).</summary>
    public Task<JoinVaultResult> JoinWithRecoveryPhraseAsync(string phrase, bool replaceLocalData, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(phrase);
        return JoinAsync(vault =>
        {
            byte[] key;
            try
            {
                key = RecoveryPhrase.Decode(phrase);
            }
            catch (SyncException e)
            {
                return Task.FromResult<(byte[]?, long, JoinVaultOutcome, string?)>((null, 0, JoinVaultOutcome.WrongPhrase, e.Code));
            }
            if (VaultFile.MatchGeneration(vault, key) is not { } generation)
            {
                CryptographicOperations.ZeroMemory(key);
                return Task.FromResult<(byte[]?, long, JoinVaultOutcome, string?)>((null, 0, JoinVaultOutcome.KeyMismatch, null));
            }
            return Task.FromResult<(byte[]?, long, JoinVaultOutcome, string?)>((key, generation, JoinVaultOutcome.Joined, null));
        }, replaceLocalData, deletePairing: null, ct);
    }

    /// <summary>
    /// Joins the vault on the drive with the text of a pairing QR code shown on another device
    /// (section 9.1, new device): downloads and checks the pairing file, then joins and deletes it.
    /// </summary>
    public Task<JoinVaultResult> JoinWithPairingCodeAsync(string qrText, bool replaceLocalData, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(qrText);
        PairingCode code;
        try
        {
            code = PairingQr.Parse(qrText);
        }
        catch (SyncException e)
        {
            return Task.FromResult(new JoinVaultResult(JoinVaultOutcome.BadPairingCode, null, null, e.Code, Status));
        }
        DriveItem? pairingFile = null;
        return JoinAsync(async vault =>
        {
            var listing = await _drive.ListAsync(SyncPaths.PairingFolder, ct).ConfigureAwait(false);
            pairingFile = listing.Files.FirstOrDefault(f => f.Path == code.PairingPath);
            if (pairingFile is null) return (null, 0, JoinVaultOutcome.PairingNotFound, null);
            var bytes = await _drive.DownloadAsync(pairingFile, ct).ConfigureAwait(false);
            PairingResult result;
            try
            {
                result = VaultCodec.AcceptPairing(vault, code, bytes, _time.GetUtcNow());
            }
            catch (SyncException e)
            {
                return (null, 0, JoinVaultOutcome.KeyMismatch, e.Code);
            }
            return result.Status switch
            {
                PairingStatus.Accepted => (result.VaultKey, result.Generation, JoinVaultOutcome.Joined, null),
                PairingStatus.Expired => (null, 0, JoinVaultOutcome.PairingExpired, null),
                _ => (null, 0, JoinVaultOutcome.KeyMismatch, null),
            };
        }, replaceLocalData, () => pairingFile, ct);
    }

    private async Task<JoinVaultResult> JoinAsync(
        Func<VaultInfo, Task<(byte[]? Key, long Generation, JoinVaultOutcome Outcome, string? Code)>> unlock,
        bool replaceLocalData,
        Func<DriveItem?>? deletePairing,
        CancellationToken ct)
    {
        await _operation.WaitAsync(ct).ConfigureAwait(false);
        byte[]? key = null;
        string? deviceId = null;
        string? backup = null;
        try
        {
            var read = await ReadVaultAsync(ct).ConfigureAwait(false);
            if (read.Vault is not { } vault)
            {
                var outcome = read.Phase == SyncPhase.NeedsUpdate ? JoinVaultOutcome.NeedsUpdate
                    : read.Phase is null ? JoinVaultOutcome.NoVault : JoinVaultOutcome.Failed;
                return new JoinVaultResult(outcome, null, null, read.Code, Status);
            }
            if (_sync.Replica?.Read((_, m) => m.VaultId) == vault.VaultId && _sync.GetDeviceSetting(VaultStateKey) == Member)
            {
                return new JoinVaultResult(JoinVaultOutcome.AlreadyInVault, _sync.Replica!.DeviceId, null, null, Status);
            }
            var (k, generation, unlocked, code) = await unlock(vault).ConfigureAwait(false);
            key = k;
            if (key is null) return new JoinVaultResult(unlocked, null, null, code, Status);

            using (var lease = await _sync.EnterWriteAsync(ct).ConfigureAwait(false))
            {
                var bills = await _tables.GetAllBillsAsync().ConfigureAwait(false);
                var payments = await _tables.GetAllPaymentsAsync().ConfigureAwait(false);
                if (bills.Count > 0 || payments.Count > 0)
                {
                    if (!replaceLocalData) return new JoinVaultResult(JoinVaultOutcome.NeedsConsent, null, null, null, Status);
                    backup = await _backups.CreateKeptAsync(BackupRotationService.PreJoinFileNamePrefix, ct).ConfigureAwait(false);
                }
                _keys.Save(vault.VaultId, generation, key);
                _sync.Join(vault.VaultId, lease);
                _sync.SetDeviceSetting(VaultStateKey, Member);
                _sync.SetDeviceSetting(VaultCreatedAtKey, null);
                _sync.SetDeviceSetting(IdentityClashKey, null);
                var replica = _sync.Replica!;
                deviceId = replica.DeviceId;
                WriteDeviceRow(replica);
            }
            _sync.RaiseDataChanged(new SyncDataChangedEventArgs(Array.Empty<RowKey>(), everything: true));

            if (deletePairing?.Invoke() is { } pairingFile)
            {
                try
                {
                    await _drive.DeleteAsync(pairingFile, ct).ConfigureAwait(false);
                }
                catch (DriveException)
                {
                    // Every device deletes stale pairing files on sync (9.1).
                }
            }
            var status = await SyncCoreAsync(ct).ConfigureAwait(false);
            return new JoinVaultResult(JoinVaultOutcome.Joined, deviceId, backup, null, status);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new JoinVaultResult(JoinVaultOutcome.Failed, deviceId, backup, (ex as SyncException)?.Code, Fail(ex));
        }
        finally
        {
            if (key is not null) CryptographicOperations.ZeroMemory(key);
            _operation.Release();
        }
    }

    // ==== Unlock, pairing, leaving ========================================================================

    /// <summary>Stores the vault key again from the recovery phrase on a device that is in the vault but lost its key.</summary>
    public async Task<UnlockResult> UnlockWithRecoveryPhraseAsync(string phrase, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(phrase);
        await _operation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var mine = _sync.Replica?.Read((_, m) => m.VaultId);
            var read = await ReadVaultAsync(ct).ConfigureAwait(false);
            if (mine is null || read.Vault is not { } vault || vault.VaultId != mine) return new UnlockResult(UnlockOutcome.NotInVault, read.Code, Status);
            byte[] key;
            try
            {
                key = RecoveryPhrase.Decode(phrase);
            }
            catch (SyncException e)
            {
                return new UnlockResult(UnlockOutcome.WrongPhrase, e.Code, Status);
            }
            try
            {
                if (VaultFile.MatchGeneration(vault, key) is not { } generation) return new UnlockResult(UnlockOutcome.KeyMismatch, null, Status);
                _keys.Save(vault.VaultId, generation, key);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
            return new UnlockResult(UnlockOutcome.Unlocked, null, await SyncCoreAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UnlockResult(UnlockOutcome.Failed, (ex as SyncException)?.Code, Fail(ex));
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>
    /// "Add a device" (9.1, existing device): writes an encrypted pairing file and returns the QR text.
    /// The file is deleted when the new device's <c>device</c> row arrives, on <see cref="CancelPairingAsync"/>,
    /// or after 10 minutes, whichever is first. Null when this device cannot pair now (see <see cref="Status"/>).
    /// </summary>
    public async Task<PairingSession?> StartPairingAsync(CancellationToken ct = default)
    {
        await _operation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var replica = _sync.Replica;
            var read = await ReadVaultAsync(ct).ConfigureAwait(false);
            if (replica is null || read.Vault is not { } vault || vault.VaultId != replica.Read((_, m) => m.VaultId)) return null;
            using var codec = OpenCodec(vault);
            if (codec is null) return null;
            await DeletePairingFileAsync(ct).ConfigureAwait(false);
            var code = PairingQr.NewCode();
            try
            {
                var expires = _time.GetUtcNow() + PairingLifetime;
                var (path, data) = codec.SealPairing(code, replica.DeviceId, Timestamp(expires));
                var item = await _drive.UploadNewAsync(path, data, ct).ConfigureAwait(false);
                var known = replica.Read((s, _) => s.Ids(SyncEntities.Device).ToHashSet(StringComparer.Ordinal));
                _pairing = new PairingState(item, expires, known);
                return new PairingSession(PairingQr.Format(code), expires, path);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(code.PairingSecret);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Fail(ex);
            return null;
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>The user closed the pairing screen: delete the pairing file now.</summary>
    public async Task CancelPairingAsync(CancellationToken ct = default)
    {
        await _operation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await DeletePairingFileAsync(ct).ConfigureAwait(false);
        }
        catch (DriveException)
        {
            // Stale pairing files are deleted on later syncs by every device.
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>
    /// "Turn off sync" (12.4): stops syncing and forgets the vault key; local data stays and the
    /// device's files stay in the vault. Turning sync on again is a join with a new device ID.
    /// (Signing out of the drive is the provider's part.)
    /// </summary>
    public async Task LeaveVaultAsync(CancellationToken ct = default)
    {
        await _operation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                await DeletePairingFileAsync(ct).ConfigureAwait(false);
            }
            catch (DriveException)
            {
            }
            using (var lease = await _sync.EnterWriteAsync(ct).ConfigureAwait(false))
            {
                if (_sync.Replica is { } replica && replica.Read((_, m) => m.VaultId) is { } vaultId)
                {
                    // Format 1 has one generation; leaving may happen offline, so clear a generous range.
                    _keys.Delete(vaultId, Enumerable.Range(1, 16).Select(g => (long)g));
                }
                if (_sync.IsOn)
                {
                    _sync.SetDeviceSetting(VaultStateKey, null);
                    _sync.SetDeviceSetting(VaultCreatedAtKey, null);
                }
                await _sync.TurnOffAsync(lease, ct).ConfigureAwait(false);
            }
            ResetIdentityCaches(null);
            _lastSynced = null;
            SetStatus(SyncStatus.Off);
        }
        finally
        {
            _operation.Release();
        }
    }

    // ==== The sync cycle (10.1, 10.2, 11) ================================================================

    /// <summary>
    /// One sync: read vault.json, seal and upload pending batches in order, list the vault, merge what is
    /// new in plan order, write a snapshot or compact when due, finish pairing, delete stale pairing
    /// files. Never throws (except for cancellation); the outcome is the returned (and current) status.
    /// </summary>
    public async Task<SyncStatus> SyncAsync(CancellationToken ct = default)
    {
        await _operation.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await SyncCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    private async Task<SyncStatus> SyncCoreAsync(CancellationToken ct)
    {
        var changed = new HashSet<RowKey>();
        try
        {
            var replica = _sync.Replica;
            var myVault = replica?.Read((_, m) => m.VaultId);
            var vaultState = replica is null ? null : _sync.GetDeviceSetting(VaultStateKey);
            if (replica is null || myVault is null || vaultState is null) return SetStatus(SyncStatus.Off);
            ResetIdentityCaches(replica.DeviceId);
            if (_sync.GetDeviceSetting(IdentityClashKey) == "1")
            {
                return Fail(SyncPhase.IdentityClash, "Another install is using this device's identity. Turn sync off and join again on this device.", null);
            }
            SetStatus(Status with { Phase = SyncPhase.Syncing, Message = "Syncing…" });

            // 1. vault.json
            var read = await ReadVaultAsync(ct).ConfigureAwait(false);
            if (read.Phase is { } problem) return Fail(problem, read.Message!, read.Code);
            VaultInfo vault;
            if (read.Vault is null)
            {
                if (vaultState != Creating) return Fail(SyncPhase.VaultMissing, "The vault is no longer on the drive.", null);
                vault = PendingVault(myVault);
            }
            else
            {
                vault = read.Vault;
                if (vault.VaultId != myVault)
                {
                    return Fail(SyncPhase.VaultReplaced, "The drive holds a different vault now. Join it to keep syncing.", null);
                }
                if (vaultState == Creating) await MarkMemberAsync(replica, ct).ConfigureAwait(false);
            }
            using var codec = OpenCodec(vault);
            if (codec is null)
            {
                return Fail(SyncPhase.Locked, "Enter your recovery phrase or pair with another device to unlock sync.", SyncErrorCodes.UnknownKeyGeneration);
            }

            // 2. Seal and upload, strictly in order.
            var upload = await UploadPendingAsync(replica, codec, ct).ConfigureAwait(false);
            if (upload == UploadOutcome.Clash)
            {
                using (await EnterAsync(replica, ct).ConfigureAwait(false)) _sync.SetDeviceSetting(IdentityClashKey, "1");
                return Fail(SyncPhase.IdentityClash, "Another install is using this device's identity. Turn sync off and join again on this device.", null);
            }
            if (read.Vault is null)
            {
                // Creating: vault.json goes up only after the first data (12.1 step 6).
                var creation = upload == UploadOutcome.Done ? await FinishCreationAsync(replica, codec, ct).ConfigureAwait(false) : Creation.NotYet;
                if (creation == Creation.LostRace)
                {
                    return Fail(SyncPhase.VaultReplaced, "Another device created a vault at the same time. Join it to keep syncing.", null);
                }
                if (creation == Creation.NotYet)
                {
                    return Fail(SyncPhase.Offline, "The vault is not on the drive yet; it will be uploaded on the next sync.", null);
                }
            }

            // 3. List, 4. read.
            var listing = await ListVaultAsync(ct).ConfigureAwait(false);
            await RecordSnapshotsListedAsync(replica, listing, ct).ConfigureAwait(false);
            var warnings = new List<SyncWarning>();
            await ReadAsync(replica, codec, listing, changed, warnings, ct).ConfigureAwait(false);

            // Pairing (9.1): done when the new device's row arrived or the code expired.
            await FinishPairingAsync(replica, ct).ConfigureAwait(false);

            // 11. Snapshots and compaction, only with every batch uploaded.
            if (upload == UploadOutcome.Done) await CompactAsync(replica, codec, listing, ct).ConfigureAwait(false);

            // 5. Stale pairing files.
            await DeleteStalePairingFilesAsync(listing, ct).ConfigureAwait(false);

            _lastSynced = _time.GetUtcNow();
            var owner = replica.Read((s, _) => s.Value(SyncEntities.Setting, SyncSettingKeys.NotionOwnerDeviceId, SyncEntities.ValueField) is { IsString: true } v ? v.StringValue : null);
            if (upload == UploadOutcome.Failed)
            {
                return SetStatus(new SyncStatus(SyncPhase.Offline, "Changes from this device could not be uploaded; sync will retry.", _lastSynced, PendingUploads(replica), warnings, DriveErrorKind.Transient.ToString(), owner));
            }
            return SetStatus(new SyncStatus(SyncPhase.Idle, "Up to date.", _lastSynced, PendingUploads(replica), warnings, null, owner));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
        finally
        {
            if (changed.Count > 0) _sync.RaiseDataChanged(new SyncDataChangedEventArgs(changed.ToArray()));
        }
    }

    // ---- vault.json ---------------------------------------------------------------------------------

    private sealed record VaultRead(VaultInfo? Vault, SyncPhase? Phase = null, string? Message = null, string? Code = null);

    /// <summary>
    /// Reads vault.json (section 7, 12.1 step 6): every copy is checked; with two valid ones, the
    /// earliest <c>createdAt</c> wins. Null vault and null phase: there is none.
    /// </summary>
    private async Task<VaultRead> ReadVaultAsync(CancellationToken ct)
    {
        var root = await _drive.ListAsync(string.Empty, ct).ConfigureAwait(false);
        var copies = root.Files.Where(f => f.Path == SyncPaths.VaultFileName).ToList();
        if (copies.Count == 0) return new VaultRead(null);
        var valid = new List<VaultInfo>();
        string? code = null;
        foreach (var copy in copies)
        {
            byte[] bytes;
            try
            {
                bytes = await _drive.DownloadAsync(copy, ct).ConfigureAwait(false);
            }
            catch (DriveException e) when (e.Kind == DriveErrorKind.NotFound)
            {
                continue;
            }
            try
            {
                valid.Add(VaultFile.CheckBytes(bytes));
            }
            catch (SyncException e)
            {
                code ??= e.Code;
                if (e.Code == SyncErrorCodes.UnsupportedFormat)
                {
                    return new VaultRead(null, SyncPhase.NeedsUpdate, "This vault uses a newer sync format. Update FutureCheck to keep syncing.", e.Code);
                }
            }
        }
        if (valid.Count == 0)
        {
            return code is null ? new VaultRead(null) : new VaultRead(null, SyncPhase.Error, "The vault's vault.json could not be read.", code);
        }
        return new VaultRead(VaultFile.ChooseAmongDuplicates(valid));
    }

    /// <summary>The vault being created, before its vault.json is on the drive.</summary>
    private VaultInfo PendingVault(string vaultId)
    {
        var key = _keys.Load(vaultId, 1);
        var keyCheck = key is null ? new string('0', 32) : SyncKeys.KeyCheckHex(key);
        if (key is not null) CryptographicOperations.ZeroMemory(key);
        return new VaultInfo(vaultId, null, null, 1, new[] { new VaultGeneration(1, keyCheck, null) });
    }

    /// <summary>A codec with every key generation this device holds, or null when it lacks the current one.</summary>
    private VaultCodec? OpenCodec(VaultInfo vault)
    {
        var keys = new Dictionary<uint, byte[]>();
        try
        {
            foreach (var g in vault.Generations)
            {
                if (g.Id > uint.MaxValue) continue;
                if (_keys.Load(vault.VaultId, g.Id) is { } k)
                {
                    if (VaultFile.MatchGeneration(vault, k) == g.Id) keys[(uint)g.Id] = k;
                    else CryptographicOperations.ZeroMemory(k);
                }
            }
            if (vault.CurrentGeneration > uint.MaxValue || !keys.ContainsKey((uint)vault.CurrentGeneration)) return null;
            return new VaultCodec(vault.VaultId, keys, (uint)vault.CurrentGeneration);
        }
        finally
        {
            foreach (var k in keys.Values) CryptographicOperations.ZeroMemory(k);
        }
    }

    // ---- Uploading (10.1) ---------------------------------------------------------------------------

    private enum UploadOutcome
    {
        Done,
        Failed,
        Clash,
    }

    private async Task<UploadOutcome> UploadPendingAsync(SyncReplica replica, VaultCodec codec, CancellationToken ct)
    {
        using (await EnterAsync(replica, ct).ConfigureAwait(false)) replica.SealPending();
        var me = replica.DeviceId;
        try
        {
            while (replica.NextBatchToUpload is { } batch)
            {
                var path = SyncPaths.ChangeFilePath(me, batch.Seq);
                IReadOnlyList<DriveItem> existing = Array.Empty<DriveItem>();
                if (!_drive.EnforcesUniqueNames)
                {
                    // Without unique names an earlier upload whose reply was lost shows up only in a listing.
                    existing = await ListCopiesAsync($"{SyncPaths.DevicesFolder}/{me}", path, ct).ConfigureAwait(false);
                }
                if (existing.Count == 0)
                {
                    try
                    {
                        var (_, data) = codec.SealChangeFile(me, batch.Seq, batch.Records);
                        await _drive.UploadNewAsync(path, data, ct).ConfigureAwait(false);
                    }
                    catch (DriveException e) when (e.Kind == DriveErrorKind.AlreadyExists)
                    {
                        existing = await ListCopiesAsync($"{SyncPaths.DevicesFolder}/{me}", path, ct).ConfigureAwait(false);
                        if (existing.Count == 0) return UploadOutcome.Failed; // not listed yet: retry next sync
                    }
                }
                if (existing.Count > 0)
                {
                    // Step 5: the same records mean the earlier upload worked; anything else is an identity clash.
                    var expected = Payloads.ChangeFile(codec.VaultId, me, batch.Seq, batch.Records);
                    foreach (var copy in existing)
                    {
                        var bytes = await _drive.DownloadAsync(copy, ct).ConfigureAwait(false);
                        try
                        {
                            if (!Payloads.SameRecords(codec.Open(path, bytes).Payload, expected)) return UploadOutcome.Clash;
                        }
                        catch (SyncException)
                        {
                            return UploadOutcome.Clash;
                        }
                    }
                }
                using (await EnterAsync(replica, ct).ConfigureAwait(false)) replica.MarkUploaded(batch.Seq);
            }
            return UploadOutcome.Done;
        }
        catch (DriveException)
        {
            return UploadOutcome.Failed;
        }
    }

    private async Task<IReadOnlyList<DriveItem>> ListCopiesAsync(string folder, string path, CancellationToken ct)
        => (await _drive.ListAsync(folder, ct).ConfigureAwait(false)).Files.Where(f => f.Path == path).ToList();

    // ---- Listing ------------------------------------------------------------------------------------

    private sealed class VaultListing
    {
        /// <summary>Change files by device and sequence number; several copies when names repeat.</summary>
        public Dictionary<string, Dictionary<long, List<DriveItem>>> ChangeFiles { get; } = new(StringComparer.Ordinal);

        /// <summary>Snapshots by HLC (the file name without <c>.snap</c>).</summary>
        public SortedDictionary<string, List<DriveItem>> Snapshots { get; } = new(StringComparer.Ordinal);

        public List<DriveItem> Pairing { get; } = new();

        public DateTimeOffset? ServerTime { get; set; }
    }

    private async Task<VaultListing> ListVaultAsync(CancellationToken ct)
    {
        var listing = new VaultListing();
        void Time(DateTimeOffset? t)
        {
            if (t is { } v && (listing.ServerTime is null || v > listing.ServerTime)) listing.ServerTime = v;
        }

        var devices = await _drive.ListAsync(SyncPaths.DevicesFolder, ct).ConfigureAwait(false);
        Time(devices.ServerTime);
        foreach (var device in devices.Folders.Where(DeviceIds.IsValid))
        {
            var files = await _drive.ListAsync($"{SyncPaths.DevicesFolder}/{device}", ct).ConfigureAwait(false);
            Time(files.ServerTime);
            foreach (var f in files.Files)
            {
                if (!SyncPaths.TryParse(f.Path, out var p) || p!.Kind != SyncPathKind.Changes || p.DeviceId != device) continue;
                if (!listing.ChangeFiles.TryGetValue(device, out var bySeq)) listing.ChangeFiles[device] = bySeq = new Dictionary<long, List<DriveItem>>();
                if (!bySeq.TryGetValue(p.Seq, out var copies)) bySeq[p.Seq] = copies = new List<DriveItem>();
                copies.Add(f);
            }
        }
        var snapshots = await _drive.ListAsync(SyncPaths.SnapshotsFolder, ct).ConfigureAwait(false);
        Time(snapshots.ServerTime);
        foreach (var f in snapshots.Files)
        {
            if (!SyncPaths.TryParse(f.Path, out var p) || p!.Kind != SyncPathKind.Snapshot) continue;
            if (!listing.Snapshots.TryGetValue(p.Hlc!, out var copies)) listing.Snapshots[p.Hlc!] = copies = new List<DriveItem>();
            copies.Add(f);
        }
        var pairing = await _drive.ListAsync(SyncPaths.PairingFolder, ct).ConfigureAwait(false);
        Time(pairing.ServerTime);
        foreach (var f in pairing.Files)
        {
            if (SyncPaths.TryParse(f.Path, out var p) && p!.Kind == SyncPathKind.Pairing) listing.Pairing.Add(f);
        }
        if (listing.ServerTime is null)
        {
            try
            {
                listing.ServerTime = await _drive.GetServerTimeAsync(ct).ConfigureAwait(false);
            }
            catch (DriveException)
            {
                // No provider time: no snapshot is old enough this time (11.1).
            }
        }
        return listing;
    }

    private async Task RecordSnapshotsListedAsync(SyncReplica replica, VaultListing listing, CancellationToken ct)
    {
        var now = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var unseen = replica.Read((_, m) => listing.Snapshots.Keys.Where(n => m.SnapshotFirstListed(n) is null).ToArray());
        if (unseen.Length == 0) return;
        using (await EnterAsync(replica, ct).ConfigureAwait(false))
        {
            replica.Update(m =>
            {
                foreach (var name in unseen) m.SetSnapshotFirstListed(name, now);
            });
        }
    }

    // ---- Reading (10.2) -----------------------------------------------------------------------------

    private enum OpenStatus
    {
        Opened,
        Missing,
        Unreadable,
        Clash,
    }

    private sealed record Opened(OpenStatus Status, EnvelopeResult? File = null, string? Code = null);

    /// <summary>Downloads and opens every copy of one name: one merges only when every copy opens and all hold the same records.</summary>
    private async Task<Opened> OpenCopiesAsync(VaultCodec codec, string path, IReadOnlyList<DriveItem> copies, CancellationToken ct)
    {
        var opened = new List<EnvelopeResult>();
        foreach (var copy in copies)
        {
            byte[] bytes;
            try
            {
                bytes = await _drive.DownloadAsync(copy, ct).ConfigureAwait(false);
            }
            catch (DriveException e) when (e.Kind == DriveErrorKind.NotFound)
            {
                continue;
            }
            try
            {
                opened.Add(codec.Open(path, bytes));
            }
            catch (SyncException e)
            {
                return new Opened(OpenStatus.Unreadable, Code: e.Code);
            }
        }
        if (opened.Count == 0) return new Opened(OpenStatus.Missing);
        if (opened.Skip(1).Any(o => !Payloads.SameRecords(opened[0].Payload, o.Payload))) return new Opened(OpenStatus.Clash);
        return new Opened(OpenStatus.Opened, opened[0]);
    }

    private async Task ReadAsync(SyncReplica replica, VaultCodec codec, VaultListing listing, HashSet<RowKey> changed, List<SyncWarning> warnings, CancellationToken ct)
    {
        var me = replica.DeviceId;
        var skewed = new SortedSet<string>(StringComparer.Ordinal);

        async Task MergeAsync(EnvelopeResult file)
        {
            MergeOutcome outcome;
            using (await EnterAsync(replica, ct).ConfigureAwait(false)) outcome = replica.MergeFile(file);
            changed.UnionWith(outcome.ChangedRows);
            skewed.UnionWith(outcome.SkewedDevices);
        }

        // Every listed snapshot is merged once per process (allowed by 10.2; it also gives its covers).
        foreach (var (name, copies) in listing.Snapshots)
        {
            if (_mergedSnapshots.Contains(name)) continue;
            var path = $"{SyncPaths.SnapshotsFolder}/{name}.snap";
            var opened = await OpenCopiesAsync(codec, path, copies, ct).ConfigureAwait(false);
            if (opened.Status != OpenStatus.Opened)
            {
                NoteFileProblem(path, Hlc(name)?.DeviceId, opened, warnings, replica);
                continue;
            }
            await MergeAsync(opened.File!).ConfigureAwait(false);
            _mergedSnapshots.Add(name);
            _snapshotCovers[name] = opened.File!.Covers;
            _problemsSince.Remove(path);
        }

        // Change files of every other device, in plan order; a file that fails to open counts as not listed.
        var unreadable = new Dictionary<string, IReadOnlyCollection<long>>(StringComparer.Ordinal);
        ReadPlan plan;
        for (var attempt = 0; ; attempt++)
        {
            var cursors = replica.Read((_, m) => new Dictionary<string, long>(m.Cursors, StringComparer.Ordinal));
            cursors[me] = Math.Max(cursors.TryGetValue(me, out var c) ? c : 0, replica.Read((_, m) => m.LastUploadedSeq));
            var files = listing.ChangeFiles
                .Where(d => d.Key != me)
                .ToDictionary(d => d.Key, d => (IReadOnlyCollection<long>)d.Value.Keys.ToArray(), StringComparer.Ordinal);
            plan = ReadPlanner.Plan(cursors, files, SnapshotInfos(listing, null, null), unreadable);
            var replan = false;
            foreach (var step in plan.Steps)
            {
                if (step.Kind == ReadStepKind.Snapshot)
                {
                    // Already merged above unless it failed to open; nothing more to do.
                    continue;
                }
                var device = step.DeviceId!;
                var path = SyncPaths.ChangeFilePath(device, step.Seq);
                var opened = await OpenCopiesAsync(codec, path, listing.ChangeFiles[device][step.Seq], ct).ConfigureAwait(false);
                if (opened.Status != OpenStatus.Opened)
                {
                    NoteFileProblem(path, device, opened, warnings, replica);
                    unreadable[device] = (unreadable.TryGetValue(device, out var bad) ? bad : Array.Empty<long>()).Append(step.Seq).ToArray();
                    replan = true;
                    break;
                }
                await MergeAsync(opened.File!).ConfigureAwait(false);
                _problemsSince.Remove(path);
            }
            if (!replan || attempt > listing.ChangeFiles.Values.Sum(v => v.Count)) break;
        }

        // Gaps (a missing file with a later one listed): probably listing lag; reported after 24 hours.
        var now = _time.GetUtcNow();
        foreach (var (device, seq) in plan.Gaps)
        {
            if (device == me || unreadable.TryGetValue(device, out var bad) && bad.Contains(seq)) continue;
            var key = $"gap:{device}:{seq.ToString(CultureInfo.InvariantCulture)}";
            var since = _problemsSince.TryGetValue(key, out var s) ? s : _problemsSince[key] = now;
            if (now - since >= ReportAfter)
            {
                warnings.Add(new SyncWarning(SyncWarningKind.MissingChanges, device, $"Some changes from {DeviceName(replica, device)} could not be found."));
            }
        }
        foreach (var device in skewed)
        {
            warnings.Add(new SyncWarning(SyncWarningKind.ClockAhead, device, $"{DeviceName(replica, device)}'s clock is ahead; check its date and time."));
        }
    }

    private void NoteFileProblem(string path, string? device, Opened opened, List<SyncWarning> warnings, SyncReplica replica)
    {
        if (opened.Status == OpenStatus.Missing) return;
        var now = _time.GetUtcNow();
        var since = _problemsSince.TryGetValue(path, out var s) ? s : _problemsSince[path] = now;
        var name = device is null ? "another device" : DeviceName(replica, device);
        if (opened.Status == OpenStatus.Clash)
        {
            warnings.Add(new SyncWarning(SyncWarningKind.DuplicateIdentity, device, $"Two devices are using the same identity; {name} needs to rejoin."));
            return;
        }
        switch (opened.Code)
        {
            case SyncErrorCodes.UnknownKeyGeneration:
                warnings.Add(new SyncWarning(SyncWarningKind.ReKeyed, device, "This vault was re-keyed. Enter the current recovery phrase or pair with a QR code."));
                break;
            case SyncErrorCodes.UnsupportedVersion or SyncErrorCodes.UnsupportedFormat:
                warnings.Add(new SyncWarning(SyncWarningKind.NeedsUpdate, device, "Update FutureCheck to keep syncing."));
                break;
            default:
                if (now - since >= ReportAfter) warnings.Add(new SyncWarning(SyncWarningKind.UnreadableFile, device, $"A file from {name} could not be read."));
                break;
        }
    }

    private static string DeviceName(SyncReplica replica, string device)
        => replica.Read((s, _) => s.Value(SyncEntities.Device, device, "name") is { IsString: true } n && n.StringValue.Length > 0 ? n.StringValue : "another device");

    private static Clock.Hlc? Hlc(string text) => Clock.Hlc.TryParse(text, out var h) ? h : null;

    // ---- Snapshots and compaction (11) --------------------------------------------------------------

    /// <summary>The listed snapshots whose covers are known, with their age (11.1) when clocks are given.</summary>
    private List<SnapshotInfo> SnapshotInfos(VaultListing listing, SyncReplica? replica, DateTimeOffset? localNow)
    {
        var infos = new List<SnapshotInfo>();
        foreach (var (name, copies) in listing.Snapshots)
        {
            if (!_snapshotCovers.TryGetValue(name, out var covers)) continue;
            var ageOk = false;
            if (replica is not null && localNow is { } now)
            {
                var first = replica.Read((_, m) => m.SnapshotFirstListed(name));
                var created = copies.Select(c => c.CreatedTime).Where(t => t is not null).Select(t => t!.Value.ToUnixTimeMilliseconds()).DefaultIfEmpty(-1).Min();
                ageOk = Compaction.SnapshotAgeOk(first, now.ToUnixTimeMilliseconds(), created < 0 ? null : created, listing.ServerTime?.ToUnixTimeMilliseconds());
            }
            infos.Add(new SnapshotInfo(name, covers, ageOk));
        }
        return infos;
    }

    private async Task CompactAsync(SyncReplica replica, VaultCodec codec, VaultListing listing, CancellationToken ct)
    {
        // Only with every listed snapshot readable and merged: an unknown snapshot could cover less.
        if (listing.Snapshots.Keys.Any(n => !_mergedSnapshots.Contains(n))) return;
        var me = replica.DeviceId;
        var now = _time.GetUtcNow();
        var infos = SnapshotInfos(listing, replica, now);
        var newest = infos.OrderByDescending(i => i.Hlc, StringComparer.Ordinal).FirstOrDefault();
        var total = listing.ChangeFiles.Sum(d => d.Value.Count);
        var notCovered = newest is null ? total : listing.ChangeFiles.Sum(d => d.Value.Keys.Count(seq => seq > newest.CoversFor(d.Key)));
        var inputs = new SnapshotWriteInputs(
            InForeground,
            replica.NextBatchToUpload is null,
            MergedEveryListedSnapshot: true,
            infos.Count,
            total,
            notCovered,
            newest?.AgeOk ?? false);
        if (Compaction.ShouldWriteSnapshot(inputs))
        {
            SnapshotContent content;
            using (await EnterAsync(replica, ct).ConfigureAwait(false)) content = replica.PrepareSnapshot();
            var (path, data) = codec.SealSnapshot(content.Hlc, content.Covers, content.Records);
            var item = await _drive.UploadNewAsync(path, data, ct).ConfigureAwait(false);
            _mergedSnapshots.Add(content.Hlc.Text);
            _snapshotCovers[content.Hlc.Text] = content.Covers;
            listing.Snapshots[content.Hlc.Text] = new List<DriveItem> { item };
            infos.Add(new SnapshotInfo(content.Hlc.Text, content.Covers, AgeOk: false));
        }

        foreach (var name in Compaction.DeletableSnapshots(infos))
        {
            foreach (var copy in listing.Snapshots[name]) await _drive.DeleteAsync(copy, ct).ConfigureAwait(false);
        }
        if (listing.ChangeFiles.TryGetValue(me, out var mine))
        {
            foreach (var seq in Compaction.DeletableChangeFiles(me, mine.Keys, infos))
            {
                foreach (var copy in mine[seq]) await _drive.DeleteAsync(copy, ct).ConfigureAwait(false);
            }
        }
    }

    // ---- Pairing (9.1) ------------------------------------------------------------------------------

    private sealed record PairingState(DriveItem File, DateTimeOffset ExpiresAt, IReadOnlySet<string> KnownDevices);

    private async Task FinishPairingAsync(SyncReplica replica, CancellationToken ct)
    {
        if (_pairing is not { } pairing) return;
        var arrived = replica.Read((s, _) => s.LiveIds(SyncEntities.Device).Any(d => d != replica.DeviceId && !pairing.KnownDevices.Contains(d)));
        if (arrived || _time.GetUtcNow() > pairing.ExpiresAt)
        {
            await DeletePairingFileAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task DeletePairingFileAsync(CancellationToken ct)
    {
        if (_pairing is not { } pairing) return;
        await _drive.DeleteAsync(pairing.File, ct).ConfigureAwait(false);
        _pairing = null;
    }

    /// <summary>True while a pairing file of this device's is waiting for the new device.</summary>
    public bool IsPairing => _pairing is not null;

    private async Task DeleteStalePairingFilesAsync(VaultListing listing, CancellationToken ct)
    {
        if (listing.ServerTime is not { } serverNow) return;
        foreach (var f in listing.Pairing)
        {
            if (f.CreatedTime is { } created && serverNow - created > PairingLifetime)
            {
                await _drive.DeleteAsync(f, ct).ConfigureAwait(false);
                if (_pairing?.File.Id == f.Id) _pairing = null;
            }
        }
    }

    // ---- Helpers ------------------------------------------------------------------------------------

    /// <summary>The write gate, for one local step of a cycle; aborts the cycle when sync was turned off or rejoined meanwhile.</summary>
    private async Task<SyncWriteLease> EnterAsync(SyncReplica expected, CancellationToken ct)
    {
        var lease = await _sync.EnterWriteAsync(ct).ConfigureAwait(false);
        if (!ReferenceEquals(_sync.Replica, expected))
        {
            lease.Dispose();
            throw new SyncInterruptedException();
        }
        return lease;
    }

    private void ResetIdentityCaches(string? deviceId)
    {
        if (_identity == deviceId) return;
        _identity = deviceId;
        _mergedSnapshots.Clear();
        _snapshotCovers.Clear();
        _problemsSince.Clear();
        _pairing = null;
    }

    private static int PendingUploads(SyncReplica replica)
        => replica.Read((_, m) => m.SealedBatches.Count(b => !b.Uploaded) + (m.PendingRecords.Count > 0 ? 1 : 0));

    private static string Timestamp(DateTimeOffset t) => SyncDates.FormatTimestamp(t);

    private SyncStatus SetIdle() => SetStatus(Status with { Phase = _sync.IsOn ? SyncPhase.Idle : SyncPhase.Off, Message = _sync.IsOn ? "Up to date." : "Sync is off." });

    private SyncStatus SetStatus(SyncStatus status)
    {
        lock (_statusLock) _status = status;
        StatusChanged?.Invoke(this, status);
        return status;
    }

    private SyncStatus Fail(SyncPhase phase, string message, string? code)
    {
        var pending = _sync.Replica is { } r ? PendingUploads(r) : 0;
        return SetStatus(new SyncStatus(phase, message, _lastSynced, pending, Status.Warnings, code, Status.NotionOwnerDeviceId));
    }

    private SyncStatus Fail(Exception ex) => ex switch
    {
        SyncInterruptedException => SetStatus(_sync.IsOn ? Status with { Phase = SyncPhase.Idle, Message = "Sync was interrupted; it will run again." } : SyncStatus.Off),
        DriveException { Kind: DriveErrorKind.Unauthorized } d => Fail(SyncPhase.SignedOut, $"Sign in to {_drive.Name} again to keep syncing.", d.Kind.ToString()),
        DriveException d => Fail(SyncPhase.Offline, $"{_drive.Name} could not be reached; your changes are kept and sync will retry.", d.Kind.ToString()),
        SyncException s => Fail(SyncPhase.Error, s.Message, s.Code),
        _ => Fail(SyncPhase.Error, "Sync failed unexpectedly; your changes are kept and sync will retry.", null),
    };

    /// <summary>Sync was turned off or the device rejoined while a cycle was running.</summary>
    private sealed class SyncInterruptedException : Exception
    {
        public SyncInterruptedException()
            : base("Sync state changed during the cycle.")
        {
        }
    }
}
