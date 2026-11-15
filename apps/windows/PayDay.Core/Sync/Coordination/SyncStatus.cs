using PayDay.Sync.Local;

namespace PayDay.Sync.Coordination;

/// <summary>Where sync stands on this device, for the UI.</summary>
public enum SyncPhase
{
    /// <summary>Sync is off, or this device is not in a vault.</summary>
    Off,

    /// <summary>The last sync finished; nothing is wrong.</summary>
    Idle,

    /// <summary>A sync is running.</summary>
    Syncing,

    /// <summary>The drive could not be reached; local edits are kept and sync retries later.</summary>
    Offline,

    /// <summary>The drive sign-in expired: sign in again.</summary>
    SignedOut,

    /// <summary>This device has no vault key (the keystore was wiped, or the vault was re-keyed): enter the recovery phrase or pair with a QR code.</summary>
    Locked,

    /// <summary>The vault uses a newer sync format: update FutureCheck.</summary>
    NeedsUpdate,

    /// <summary>The vault this device belongs to is no longer on the drive.</summary>
    VaultMissing,

    /// <summary>The drive holds a different vault than the one this device joined (deleted and created again): offer to join it.</summary>
    VaultReplaced,

    /// <summary>Another install uses this device's identity (a cloned disk or restored app data): sync stopped; rejoin.</summary>
    IdentityClash,

    /// <summary>Something unexpected failed; local edits are kept.</summary>
    Error,
}

/// <summary>Things worth telling the user that do not stop sync.</summary>
public enum SyncWarningKind
{
    /// <summary>A device's clock is more than 10 minutes ahead (<c>sync-protocol.md</c> 2.4).</summary>
    ClockAhead,

    /// <summary>A device's change file has been missing for 24 hours while a later one is listed (10.2, "Gaps").</summary>
    MissingChanges,

    /// <summary>A file has failed to open for 24 hours (10.2, "Unreadable files").</summary>
    UnreadableFile,

    /// <summary>A file uses a key generation this device does not have (14, <c>unknownKeyGeneration</c>).</summary>
    ReKeyed,

    /// <summary>A file uses a newer format (14, <c>unsupportedVersion</c> / <c>unsupportedFormat</c>).</summary>
    NeedsUpdate,

    /// <summary>Two different files share one name: two installs use one device identity (10.2, "Duplicate names").</summary>
    DuplicateIdentity,
}

/// <summary>One warning; <see cref="DeviceId"/> is the device it concerns, when there is one.</summary>
public sealed record SyncWarning(SyncWarningKind Kind, string? DeviceId, string Message);

/// <summary>
/// The sync status the UI shows. Sync never throws into the app: every failure ends up here, and local
/// edits are never lost because of one.
/// </summary>
/// <param name="PendingUploads">Batches (sealed or still queued) not on the drive yet.</param>
/// <param name="ErrorCode">The protocol error code (<c>sync-protocol.md</c> 14) or drive error kind, when there is one.</param>
/// <param name="NotionOwnerDeviceId">The device that runs the Notion connector, if any (12.5).</param>
public sealed record SyncStatus(
    SyncPhase Phase,
    string Message,
    DateTimeOffset? LastSyncedAt,
    int PendingUploads,
    IReadOnlyList<SyncWarning> Warnings,
    string? ErrorCode = null,
    string? NotionOwnerDeviceId = null)
{
    public static readonly SyncStatus Off = new(SyncPhase.Off, "Sync is off.", null, 0, Array.Empty<SyncWarning>());
}

/// <summary>How creating a vault ended.</summary>
public enum CreateVaultOutcome
{
    /// <summary>The vault is on the drive; show the recovery phrase.</summary>
    Created,

    /// <summary>
    /// The vault exists on this device but not (completely) on the drive yet: show the recovery phrase;
    /// later syncs finish the upload (vault.json goes last).
    /// </summary>
    CreatedUploadPending,

    /// <summary>The drive already has a vault: offer "join" instead (12.1 step 2).</summary>
    VaultExists,

    /// <summary>This device is already in the vault on the drive.</summary>
    AlreadyInVault,

    /// <summary>Some rows cannot be migrated; see <see cref="CreateVaultResult.TurnOn"/>. Nothing changed.</summary>
    Blocked,

    /// <summary>
    /// Something failed before the vault existed (for example the pre-sync backup could not be
    /// written): no vault, no recovery phrase; sync stays as it was. See <see cref="CreateVaultResult.Status"/>.
    /// </summary>
    Failed,
}

/// <summary>What <see cref="SyncCoordinator.ProbeVaultAsync"/> found on the drive.</summary>
public enum VaultProbeOutcome
{
    /// <summary>No vault.json: offer to create a vault.</summary>
    NoVault,

    /// <summary>A vault exists: offer to join it.</summary>
    VaultExists,

    /// <summary>The vault on the drive is the one this device is in.</summary>
    ThisDevicesVault,

    /// <summary>The vault uses a newer sync format.</summary>
    NeedsUpdate,

    /// <summary>The drive sign-in expired.</summary>
    SignedOut,

    /// <summary>The drive could not be read; see <see cref="VaultProbe.Message"/>.</summary>
    Failed,
}

public sealed record VaultProbe(VaultProbeOutcome Outcome, string? Message, string? ErrorCode);

/// <summary>How leaving a vault ended.</summary>
public enum LeaveVaultOutcome
{
    /// <summary>Sync is off and the vault key is gone from this device.</summary>
    Left,

    /// <summary>Sync is off, but the keystore refused to delete the vault key.</summary>
    LeftKeyNotRemoved,

    /// <summary>Sync could not be turned off (a local database error); see <see cref="LeaveVaultResult.Status"/>.</summary>
    Failed,
}

/// <summary>What happened to the Notion connector when this device left (<c>sync-protocol.md</c> 12.5).</summary>
public enum NotionRelease
{
    /// <summary>This device did not run the connector.</summary>
    NotOwner,

    /// <summary>It did; <c>NotionOwnerDeviceId: null</c> reached the drive, so other devices see the connector is free.</summary>
    Released,

    /// <summary>It did, but the change could not be uploaded (offline): other devices keep naming this device until Notion is connected on one of them.</summary>
    NotUploaded,
}

public sealed record LeaveVaultResult(LeaveVaultOutcome Outcome, NotionRelease Notion, string Message, SyncStatus Status);

/// <param name="RecoveryPhrase">The 24 words, shown once (12.1 step 7). Set for <see cref="CreateVaultOutcome.Created"/> and <see cref="CreateVaultOutcome.CreatedUploadPending"/>.</param>
public sealed record CreateVaultResult(
    CreateVaultOutcome Outcome,
    string? VaultId,
    string? RecoveryPhrase,
    TurnOnResult? TurnOn,
    SyncStatus Status);

/// <summary>How joining a vault ended.</summary>
public enum JoinVaultOutcome
{
    Joined,

    /// <summary>This device has data: ask the user, then call again with <c>replaceLocalData: true</c> (12.2 step 3). Nothing changed.</summary>
    NeedsConsent,

    /// <summary>No vault on the drive: offer to create one.</summary>
    NoVault,

    /// <summary>This device is already in that vault (and holds its key).</summary>
    AlreadyInVault,

    /// <summary>
    /// This device is in that vault but was locked (no key): the phrase or pairing code unlocked it in
    /// place, keeping its identity and data.
    /// </summary>
    Unlocked,

    /// <summary>The phrase does not decode (<see cref="JoinVaultResult.ErrorCode"/>: <c>wrongWordCount</c>, <c>unknownWord</c>, <c>badChecksum</c>): "Check the words and try again."</summary>
    WrongPhrase,

    /// <summary>The key (phrase or pairing file) is not this vault's: it matches no key check value.</summary>
    KeyMismatch,

    /// <summary>"That isn't a FutureCheck pairing code." (<c>badQr</c>)</summary>
    BadPairingCode,

    /// <summary>The pairing file is not on the drive (expired and cleaned up, or not listed yet).</summary>
    PairingNotFound,

    /// <summary>The pairing code expired (10 minutes): start pairing again on the other device.</summary>
    PairingExpired,

    /// <summary>The vault uses a newer sync format.</summary>
    NeedsUpdate,

    /// <summary>The drive or a file failed; see <see cref="JoinVaultResult.Status"/>. Nothing on this device changed unless <see cref="JoinVaultResult.DeviceId"/> is set.</summary>
    Failed,
}

/// <param name="DeviceId">This device's new ID once it joined.</param>
/// <param name="BackupFileName">The backup of this device's data saved before it was replaced (never rotated).</param>
public sealed record JoinVaultResult(
    JoinVaultOutcome Outcome,
    string? DeviceId,
    string? BackupFileName,
    string? ErrorCode,
    SyncStatus Status);

/// <summary>An "Add a device" screen in progress: show <see cref="QrText"/> as a QR code until <see cref="ExpiresAt"/>.</summary>
public sealed record PairingSession(string QrText, DateTimeOffset ExpiresAt, string PairingPath);

/// <summary>How unlocking with the recovery phrase ended.</summary>
public enum UnlockOutcome
{
    Unlocked,
    WrongPhrase,
    KeyMismatch,
    NotInVault,
    Failed,
}

public sealed record UnlockResult(UnlockOutcome Outcome, string? ErrorCode, SyncStatus Status);

/// <summary>What this device says about itself in its <c>device</c> row (<c>data-model.md</c> 4.6).</summary>
/// <param name="Platform"><c>windows</c>, <c>macos</c>, <c>ios</c>, <c>android</c> or <c>web</c>.</param>
public sealed record DeviceInfo(string Name, string Platform, string? AppVersion);
