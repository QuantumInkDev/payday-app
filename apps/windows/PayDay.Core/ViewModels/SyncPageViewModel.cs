using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using PayDay.Services;
using PayDay.Sync.Coordination;
using PayDay.Sync.Crypto;
using PayDay.Sync.Drive;

namespace PayDay.ViewModels;

/// <summary>Which part of the Sync page is showing.</summary>
public enum SyncScreen
{
    /// <summary>This build cannot sign in to OneDrive (no client ID).</summary>
    NotConfigured,

    /// <summary>Sync is off: "Turn on sync".</summary>
    Off,

    /// <summary>No vault on the drive: explain, then create one.</summary>
    Create,

    /// <summary>A vault exists: join it with the recovery phrase or a pairing code.</summary>
    Join,

    /// <summary>Some rows must be fixed before sync can be turned on.</summary>
    Blocked,

    /// <summary>The new vault's recovery phrase, shown once until the user confirms they saved it.</summary>
    RecoveryPhrase,

    /// <summary>In a vault: status, warnings and actions.</summary>
    Status,

    /// <summary>"Add a device": the pairing QR code with its countdown.</summary>
    Pairing,
}

/// <summary>
/// The Sync page (design 6.5, <c>sync-protocol.md</c> 9 and 12): turning sync on (sign in, then create
/// a vault or join one), the recovery phrase shown once, the consent before a join replaces this
/// device's data, the status with plain-language warnings, adding a device with a QR code, unlocking,
/// rejoining and turning sync off. Nothing here turns sync on without the user pressing a button, and
/// the coordinator always writes the pre-sync backup first.
/// <para>
/// All logic lives here so it is unit-tested; the page's code-behind only shows dialogs, saves files,
/// renders the QR image and runs a one-second timer that calls <see cref="TickAsync"/>.
/// <see cref="SyncCoordinator.StatusChanged"/> may fire on any thread: the page passes a dispatcher.
/// </para>
/// </summary>
public sealed partial class SyncPageViewModel : ObservableObject, IDisposable
{
    /// <summary>While the pairing screen is open, sync this often so the new device's arrival is noticed.</summary>
    public static readonly TimeSpan PairingSyncInterval = TimeSpan.FromSeconds(15);

    private readonly SyncCoordinator? _coordinator;
    private readonly IDriveAccount? _account;
    private readonly NotionSyncService? _notion;
    private readonly TimeProvider _time;
    private readonly Action<Action> _dispatch;
    private Func<bool, Task<JoinVaultResult>>? _pendingJoin;
    private PairingSession? _pairing;
    private DateTimeOffset _lastPairingSync;

    /// <param name="coordinator">Null when this build has no OneDrive client ID.</param>
    /// <param name="account">The drive account (sign in / out).</param>
    /// <param name="notion">For "Notion: connected on …"; optional.</param>
    /// <param name="dispatch">Runs an action on the UI thread (default: at once, for tests).</param>
    public SyncPageViewModel(SyncCoordinator? coordinator, IDriveAccount? account, NotionSyncService? notion = null, TimeProvider? time = null, Action<Action>? dispatch = null)
    {
        _coordinator = coordinator;
        _account = account;
        _notion = notion;
        _time = time ?? TimeProvider.System;
        _dispatch = dispatch ?? (a => a());
        if (_coordinator is not null) _coordinator.StatusChanged += OnStatusChanged;
    }

    public void Dispose()
    {
        if (_coordinator is not null) _coordinator.StatusChanged -= OnStatusChanged;
    }

    private bool Available => _coordinator is not null && _account is { IsConfigured: true };

    public string ProviderName => _account?.ProviderName ?? "OneDrive";

    // ---- State ------------------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotConfigured), nameof(IsOff), nameof(IsCreate), nameof(IsJoin), nameof(IsBlocked), nameof(IsRecoveryPhrase), nameof(IsStatus), nameof(IsPairing))]
    private SyncScreen _screen = SyncScreen.Off;

    public bool IsNotConfigured => Screen == SyncScreen.NotConfigured;
    public bool IsOff => Screen == SyncScreen.Off;
    public bool IsCreate => Screen == SyncScreen.Create;
    public bool IsJoin => Screen == SyncScreen.Join;
    public bool IsBlocked => Screen == SyncScreen.Blocked;
    public bool IsRecoveryPhrase => Screen == SyncScreen.RecoveryPhrase;
    public bool IsStatus => Screen == SyncScreen.Status;
    public bool IsPairing => Screen == SyncScreen.Pairing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isBusy;

    public bool IsIdle => !IsBusy;

    [ObservableProperty]
    private string _busyMessage = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorMessage = string.Empty;

    public bool HasError => ErrorMessage.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInfo))]
    private string _infoMessage = string.Empty;

    public bool HasInfo => InfoMessage.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccountText))]
    private string? _accountName;

    /// <summary>"Signed in to OneDrive as …", or empty.</summary>
    public string AccountText => AccountName is { Length: > 0 } name ? $"Signed in to {ProviderName} as {name}." : string.Empty;

    public string NotConfiguredMessage => Sync.Drive.OneDrive.OneDriveConfig.NotConfiguredMessage;

    // ---- Status -----------------------------------------------------------------------------------

    [ObservableProperty]
    private SyncPhase _phase = SyncPhase.Off;

    [ObservableProperty]
    private string _statusTitle = "Sync is off";

    [ObservableProperty]
    private string _statusMessage = "Sync is off.";

    [ObservableProperty]
    private string _lastSyncedText = string.Empty;

    [ObservableProperty]
    private string _pendingText = string.Empty;

    /// <summary>Plain-language warnings that do not stop sync.</summary>
    public ObservableCollection<string> Warnings { get; } = new();

    [ObservableProperty]
    private bool _hasWarnings;

    [ObservableProperty]
    private string _notionLine = string.Empty;

    [ObservableProperty]
    private bool _canRunNotionHere;

    [ObservableProperty]
    private bool _needsUnlock;

    [ObservableProperty]
    private bool _needsRejoin;

    [ObservableProperty]
    private bool _needsSignIn;

    [ObservableProperty]
    private bool _canCreateNewVault;

    // ---- Join / unlock inputs ---------------------------------------------------------------------

    [ObservableProperty]
    private string _phraseInput = string.Empty;

    [ObservableProperty]
    private string _pairingCodeInput = string.Empty;

    [ObservableProperty]
    private bool _isConsentPending;

    [ObservableProperty]
    private string _consentMessage = string.Empty;

    // ---- Blocked rows -----------------------------------------------------------------------------

    /// <summary>The rows to fix before sync can be turned on, one plain line each.</summary>
    public ObservableCollection<string> BlockingRows { get; } = new();

    // ---- Recovery phrase --------------------------------------------------------------------------

    /// <summary>The 24 words, numbered ("1. abandon"), while the recovery-phrase screen is open.</summary>
    public ObservableCollection<string> RecoveryWords { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanFinishRecoveryPhrase))]
    private bool _phraseSaved;

    public bool CanFinishRecoveryPhrase => PhraseSaved;

    [ObservableProperty]
    private string _recoveryPhraseNote = string.Empty;

    private string? _recoveryPhrase;

    /// <summary>What "Save to a file" writes: the words with what they are for. Null once the screen closed.</summary>
    public string? RecoveryPhraseFileText => _recoveryPhrase is null
        ? null
        : string.Join(Environment.NewLine, new[]
        {
            "FutureCheck recovery phrase",
            string.Empty,
            "These 24 words unlock your synced FutureCheck data on a new device. Anyone who has them, and",
            "access to your OneDrive, can read that data. Keep this file somewhere safe and private (or print",
            "it and delete the file). If you lose every device and these words, the synced data is lost.",
            string.Empty,
            _recoveryPhrase,
            string.Empty,
        });

    // ---- Pairing ----------------------------------------------------------------------------------

    [ObservableProperty]
    private byte[]? _pairingQrPng;

    [ObservableProperty]
    private string _pairingCodeText = string.Empty;

    [ObservableProperty]
    private string _pairingCountdownText = string.Empty;

    [ObservableProperty]
    private bool _showPairingCodeText;

    // ---- Loading and status -----------------------------------------------------------------------

    /// <summary>Picks the screen from the current state. Reads only local state; no network.</summary>
    public async Task LoadAsync()
    {
        ClearMessages();
        if (!Available)
        {
            Screen = SyncScreen.NotConfigured;
            return;
        }
        try
        {
            AccountName = await _account!.GetSignedInUserAsync().ConfigureAwait(true);
        }
        catch (Exception)
        {
            AccountName = null;
        }
        if (Screen is SyncScreen.RecoveryPhrase or SyncScreen.Pairing && _coordinator!.IsInVault) return;
        Screen = _coordinator!.IsInVault ? SyncScreen.Status : SyncScreen.Off;
        RefreshStatus();
    }

    private void OnStatusChanged(object? sender, SyncStatus status) => _dispatch(RefreshStatus);

    /// <summary>Copies the coordinator's status (and the Notion owner line) into the page.</summary>
    public void RefreshStatus()
    {
        if (_coordinator is null) return;
        var status = _coordinator.Status;
        var inVault = _coordinator.IsInVault;
        Phase = status.Phase;
        if (inVault && status.Phase == SyncPhase.Off)
        {
            // In a vault, but no sync has run in this process yet.
            StatusTitle = "Sync is on";
            StatusMessage = "Waiting for the first sync.";
        }
        else
        {
            StatusTitle = Title(status.Phase);
            StatusMessage = status.Message;
        }
        LastSyncedText = status.LastSyncedAt is { } at
            ? $"Last synced {at.ToLocalTime().ToString("MMM d, h:mm tt", CultureInfo.CurrentCulture)}"
            : "Not synced yet";
        PendingText = status.PendingUploads switch
        {
            0 => "All changes from this PC are uploaded.",
            1 => "1 batch of changes from this PC is waiting to upload.",
            var n => $"{n} batches of changes from this PC are waiting to upload.",
        };
        Warnings.Clear();
        foreach (var w in status.Warnings.Select(w => w.Message).Distinct(StringComparer.Ordinal)) Warnings.Add(w);
        HasWarnings = Warnings.Count > 0;
        NeedsUnlock = status.Phase == SyncPhase.Locked;
        NeedsRejoin = status.Phase is SyncPhase.IdentityClash or SyncPhase.VaultReplaced;
        NeedsSignIn = status.Phase == SyncPhase.SignedOut;
        CanCreateNewVault = status.Phase == SyncPhase.VaultMissing;
        RefreshNotionLine();
    }

    private void RefreshNotionLine()
    {
        NotionLine = string.Empty;
        CanRunNotionHere = false;
        if (_notion is null || _coordinator is null || !_coordinator.IsInVault) return;
        var state = _notion.GetConnectorState();
        switch (state.Role)
        {
            case NotionRole.Owner:
            case NotionRole.OwnerMustPushFirst:
                NotionLine = "Notion: connected on this PC.";
                break;
            case NotionRole.OtherDevice:
                NotionLine = $"Notion: connected on {state.OwnerDeviceName ?? "another device"}. This PC doesn't push to or pull from Notion.";
                break;
            case NotionRole.NoOwner:
                NotionLine = "Notion: not connected on any device.";
                CanRunNotionHere = _notion.HasToken();
                break;
        }
    }

    private static string Title(SyncPhase phase) => phase switch
    {
        SyncPhase.Off => "Sync is off",
        SyncPhase.Idle => "Up to date",
        SyncPhase.Syncing => "Syncing…",
        SyncPhase.Offline => "Waiting to sync",
        SyncPhase.SignedOut => "Sign in again",
        SyncPhase.Locked => "Sync is locked",
        SyncPhase.NeedsUpdate => "Update needed",
        SyncPhase.VaultMissing => "Vault not found",
        SyncPhase.VaultReplaced => "A different vault",
        SyncPhase.IdentityClash => "Sync stopped",
        _ => "Sync problem",
    };

    // ---- Turning sync on --------------------------------------------------------------------------

    /// <summary>"Turn on sync": sign in (if needed), then look at the drive and offer create or join. Changes no data.</summary>
    public Task TurnOnAsync() => RunAsync("Signing in…", async () =>
    {
        if (!await EnsureSignedInAsync().ConfigureAwait(true)) return;
        await ShowCreateOrJoinAsync().ConfigureAwait(true);
    });

    private async Task<bool> EnsureSignedInAsync()
    {
        if (await _account!.GetSignedInUserAsync().ConfigureAwait(true) is { } user)
        {
            AccountName = user;
            return true;
        }
        BusyMessage = $"Finish signing in to {ProviderName} in your browser…";
        var result = await _account.SignInAsync().ConfigureAwait(true);
        if (!result.SignedIn)
        {
            if (result.Outcome != DriveSignInOutcome.Cancelled) ErrorMessage = result.Message ?? $"Could not sign in to {ProviderName}.";
            else InfoMessage = "Sign-in was cancelled.";
            return false;
        }
        AccountName = result.UserName;
        return true;
    }

    private async Task ShowCreateOrJoinAsync()
    {
        BusyMessage = $"Looking for your vault on {ProviderName}…";
        var probe = await _coordinator!.ProbeVaultAsync().ConfigureAwait(true);
        switch (probe.Outcome)
        {
            case VaultProbeOutcome.NoVault:
                Screen = SyncScreen.Create;
                break;
            case VaultProbeOutcome.VaultExists:
                PhraseInput = string.Empty;
                PairingCodeInput = string.Empty;
                Screen = SyncScreen.Join;
                break;
            case VaultProbeOutcome.ThisDevicesVault:
                Screen = SyncScreen.Status;
                RefreshStatus();
                break;
            case VaultProbeOutcome.NeedsUpdate:
                ErrorMessage = "Your vault uses a newer sync format. Update FutureCheck to use it.";
                break;
            default:
                ErrorMessage = probe.Message ?? $"{ProviderName} could not be read. Try again.";
                break;
        }
    }

    /// <summary>Creates the vault (pre-sync backup, migration, first upload) and shows the recovery phrase once.</summary>
    public Task CreateVaultAsync() => RunAsync("Backing up this PC's data and creating your vault…", async () =>
    {
        var result = await _coordinator!.CreateVaultAsync().ConfigureAwait(true);
        switch (result.Outcome)
        {
            case CreateVaultOutcome.Created:
            case CreateVaultOutcome.CreatedUploadPending:
                ShowRecoveryPhrase(result.RecoveryPhrase!, result.Outcome == CreateVaultOutcome.CreatedUploadPending);
                break;
            case CreateVaultOutcome.Blocked:
                BlockingRows.Clear();
                foreach (var p in result.TurnOn?.Problems ?? Array.Empty<Sync.Local.MigrationProblem>())
                {
                    BlockingRows.Add($"{p.Table} row {p.RowId}, {p.Column}{(p.Value is null ? string.Empty : $" (\"{p.Value}\")")}: {p.Message}");
                }
                Screen = SyncScreen.Blocked;
                break;
            case CreateVaultOutcome.VaultExists:
                InfoMessage = "A vault already exists on your OneDrive. Join it instead.";
                Screen = SyncScreen.Join;
                break;
            case CreateVaultOutcome.AlreadyInVault:
                Screen = SyncScreen.Status;
                RefreshStatus();
                break;
            default:
                ErrorMessage = result.Status.Message;
                Screen = SyncScreen.Off;
                break;
        }
    });

    private void ShowRecoveryPhrase(string phrase, bool uploadPending)
    {
        _recoveryPhrase = phrase;
        RecoveryWords.Clear();
        var words = phrase.Split(' ');
        for (var i = 0; i < words.Length; i++) RecoveryWords.Add($"{i + 1}. {words[i]}");
        PhraseSaved = false;
        RecoveryPhraseNote = uploadPending
            ? "Your vault is set up on this PC. It will finish uploading to OneDrive on the next sync."
            : "Your vault is on OneDrive.";
        OnPropertyChanged(nameof(RecoveryPhraseFileText));
        Screen = SyncScreen.RecoveryPhrase;
    }

    /// <summary>"I saved it": the phrase leaves the screen and memory for good (it is never shown again).</summary>
    public void FinishRecoveryPhrase()
    {
        if (!PhraseSaved) return;
        _recoveryPhrase = null;
        RecoveryWords.Clear();
        PhraseSaved = false;
        OnPropertyChanged(nameof(RecoveryPhraseFileText));
        Screen = SyncScreen.Status;
        RefreshStatus();
    }

    // ---- Joining ----------------------------------------------------------------------------------

    /// <summary>Joins with the typed (or pasted) 24 words.</summary>
    public Task JoinWithPhraseAsync()
    {
        var phrase = PhraseInput;
        return JoinAsync(replace => _coordinator!.JoinWithRecoveryPhraseAsync(phrase, replace));
    }

    /// <summary>Joins with the pairing code text shown on another device (Windows PCs often have no camera).</summary>
    public Task JoinWithPairingCodeAsync()
    {
        var code = PairingCodeInput;
        return JoinAsync(replace => _coordinator!.JoinWithPairingCodeAsync(code, replace));
    }

    private Task JoinAsync(Func<bool, Task<JoinVaultResult>> join) => RunAsync("Joining your vault…", async () =>
    {
        var result = await join(false).ConfigureAwait(true);
        if (result.Outcome == JoinVaultOutcome.NeedsConsent)
        {
            _pendingJoin = join;
            ConsentMessage = "This PC already has bills or payments. Joining saves them to a backup file on this PC "
                + "and then replaces them with the data in your vault. Nothing is merged. Continue?";
            IsConsentPending = true;
            return;
        }
        HandleJoin(result);
    });

    /// <summary>The user agreed: back up this device's data, replace it with the vault's, and join.</summary>
    public Task ConfirmReplaceAsync()
    {
        IsConsentPending = false;
        if (_pendingJoin is not { } join) return Task.CompletedTask;
        _pendingJoin = null;
        return RunAsync("Backing up this PC's data and joining your vault…", async () => HandleJoin(await join(true).ConfigureAwait(true)));
    }

    public void CancelReplace()
    {
        IsConsentPending = false;
        _pendingJoin = null;
        InfoMessage = "Nothing changed on this PC.";
    }

    private void HandleJoin(JoinVaultResult result)
    {
        switch (result.Outcome)
        {
            case JoinVaultOutcome.Joined:
            case JoinVaultOutcome.Unlocked:
            case JoinVaultOutcome.AlreadyInVault:
                PhraseInput = string.Empty;
                PairingCodeInput = string.Empty;
                InfoMessage = result.Outcome switch
                {
                    JoinVaultOutcome.Unlocked => "Sync is unlocked.",
                    JoinVaultOutcome.AlreadyInVault => "This PC is already in this vault.",
                    _ => result.BackupFileName is { } backup
                        ? $"This PC joined your vault. Its earlier data was saved to the backup {backup}."
                        : "This PC joined your vault.",
                };
                Screen = SyncScreen.Status;
                RefreshStatus();
                break;
            case JoinVaultOutcome.WrongPhrase:
                ErrorMessage = "Check the words and try again.";
                break;
            case JoinVaultOutcome.KeyMismatch:
                ErrorMessage = "Those words (or that code) belong to a different vault.";
                break;
            case JoinVaultOutcome.BadPairingCode:
                ErrorMessage = "That isn't a FutureCheck pairing code.";
                break;
            case JoinVaultOutcome.PairingNotFound:
                ErrorMessage = "That pairing code wasn't found on OneDrive. It may have expired or not be uploaded yet. Try again in a moment, or start pairing again on the other device.";
                break;
            case JoinVaultOutcome.PairingExpired:
                ErrorMessage = "That pairing code expired. Start pairing again on the other device.";
                break;
            case JoinVaultOutcome.NoVault:
                ErrorMessage = "There is no vault on this OneDrive account yet.";
                Screen = SyncScreen.Create;
                break;
            case JoinVaultOutcome.NeedsUpdate:
                ErrorMessage = "Your vault uses a newer sync format. Update FutureCheck to use it.";
                break;
            default:
                ErrorMessage = result.Status.Message;
                break;
        }
    }

    // ---- In a vault -------------------------------------------------------------------------------

    public Task SyncNowAsync() => RunAsync("Syncing…", async () =>
    {
        await _coordinator!.SyncAsync().ConfigureAwait(true);
        RefreshStatus();
    });

    /// <summary>Locked: store the key again from the recovery phrase.</summary>
    public Task UnlockWithPhraseAsync()
    {
        var phrase = PhraseInput;
        return RunAsync("Unlocking…", async () =>
        {
            var result = await _coordinator!.UnlockWithRecoveryPhraseAsync(phrase).ConfigureAwait(true);
            switch (result.Outcome)
            {
                case UnlockOutcome.Unlocked:
                    PhraseInput = string.Empty;
                    InfoMessage = "Sync is unlocked.";
                    break;
                case UnlockOutcome.WrongPhrase:
                    ErrorMessage = "Check the words and try again.";
                    break;
                case UnlockOutcome.KeyMismatch:
                    ErrorMessage = "Those words belong to a different vault.";
                    break;
                default:
                    ErrorMessage = result.Status.Message;
                    break;
            }
            RefreshStatus();
        });
    }

    /// <summary>Locked: unlock with a pairing code from another device.</summary>
    public Task UnlockWithPairingCodeAsync() => JoinWithPairingCodeAsync();

    /// <summary>Signed out: sign in again, then sync.</summary>
    public Task SignInAgainAsync() => RunAsync("Signing in…", async () =>
    {
        BusyMessage = $"Finish signing in to {ProviderName} in your browser…";
        var result = await _account!.SignInAsync().ConfigureAwait(true);
        if (!result.SignedIn)
        {
            ErrorMessage = result.Outcome == DriveSignInOutcome.Cancelled ? "Sign-in was cancelled." : result.Message ?? "Could not sign in.";
            return;
        }
        AccountName = result.UserName;
        await _coordinator!.SyncAsync().ConfigureAwait(true);
        RefreshStatus();
    });

    /// <summary>Identity clash or replaced vault: leave locally (data stays), then join again with a new device ID.</summary>
    public Task RejoinAsync() => RunAsync("Preparing to join again…", async () =>
    {
        var left = await _coordinator!.LeaveVaultAsync().ConfigureAwait(true);
        if (left.Outcome == LeaveVaultOutcome.Failed)
        {
            ErrorMessage = left.Message;
            return;
        }
        if (!await EnsureSignedInAsync().ConfigureAwait(true))
        {
            Screen = SyncScreen.Off;
            return;
        }
        await ShowCreateOrJoinAsync().ConfigureAwait(true);
    });

    /// <summary>The vault disappeared from the drive: create a new one from this PC's data.</summary>
    public void StartNewVault()
    {
        ClearMessages();
        Screen = SyncScreen.Create;
    }

    /// <summary>"Run Notion on this PC" in a vault where no device runs it, with the token saved here.</summary>
    public Task RunNotionHereAsync() => RunAsync("Connecting Notion…", async () =>
    {
        if (_notion is null || !await _notion.ConnectWithSavedTokenAsync().ConfigureAwait(true))
        {
            ErrorMessage = "Save a Notion token in Settings first.";
            return;
        }
        InfoMessage = "Notion runs on this PC now. Its first Notion sync pushes every bill.";
        RefreshStatus();
    });

    /// <summary>"Turn off sync" (after the page's warning): leave the vault, then sign out of the drive.</summary>
    public Task TurnOffAsync() => RunAsync("Turning sync off…", async () =>
    {
        var left = await _coordinator!.LeaveVaultAsync().ConfigureAwait(true);
        if (left.Outcome == LeaveVaultOutcome.Failed)
        {
            ErrorMessage = left.Message;
            RefreshStatus();
            return;
        }
        try
        {
            await _account!.SignOutAsync().ConfigureAwait(true);
            AccountName = null;
        }
        catch (Exception e)
        {
            ErrorMessage = $"Sync is off, but signing out of {ProviderName} failed: {e.Message}";
        }
        var notes = new List<string> { left.Message };
        if (left.Notion == NotionRelease.NotUploaded) notes.Add("Other devices will still show Notion as connected on this PC until Notion is connected on one of them.");
        if (left.Notion != NotionRelease.NotOwner || (_notion?.HasToken() ?? false)) notes.Add("To use Notion on this PC again, save the Notion token in Settings.");
        InfoMessage = string.Join(" ", notes);
        if (left.Outcome == LeaveVaultOutcome.LeftKeyNotRemoved) ErrorMessage = left.Message;
        Screen = SyncScreen.Off;
        RefreshStatus();
    });

    /// <summary>Back from the create, join or blocked screen without changing anything.</summary>
    public void Cancel()
    {
        ClearMessages();
        _pendingJoin = null;
        IsConsentPending = false;
        Screen = _coordinator?.IsInVault == true ? SyncScreen.Status : SyncScreen.Off;
        RefreshStatus();
    }

    // ---- Adding a device --------------------------------------------------------------------------

    /// <summary>"Add a device": writes the pairing file and shows its QR code for 10 minutes.</summary>
    public Task StartPairingAsync() => RunAsync("Preparing a pairing code…", async () =>
    {
        var session = await _coordinator!.StartPairingAsync().ConfigureAwait(true);
        if (session is null)
        {
            ErrorMessage = _coordinator.Status.Phase is SyncPhase.Idle or SyncPhase.Syncing
                ? "A pairing code could not be made. Try again."
                : _coordinator.Status.Message;
            RefreshStatus();
            return;
        }
        _pairing = session;
        _lastPairingSync = _time.GetUtcNow();
        PairingCodeText = session.QrText;
        ShowPairingCodeText = false;
        PairingQrPng = PairingQrImage.Png(session.QrText);
        UpdateCountdown();
        Screen = SyncScreen.Pairing;
    });

    /// <summary>
    /// Called every second while the pairing screen is open: updates the countdown, syncs every
    /// <see cref="PairingSyncInterval"/> to notice the new device, and closes the screen when the
    /// device arrived or the code expired.
    /// </summary>
    public async Task TickAsync()
    {
        if (_pairing is null || Screen != SyncScreen.Pairing) return;
        var now = _time.GetUtcNow();
        if (now >= _pairing.ExpiresAt)
        {
            await ClosePairingAsync().ConfigureAwait(true);
            InfoMessage = "The pairing code expired. Start again to add a device.";
            return;
        }
        UpdateCountdown();
        if (!IsBusy && now - _lastPairingSync >= PairingSyncInterval)
        {
            _lastPairingSync = now;
            await _coordinator!.SyncAsync().ConfigureAwait(true);
            if (!_coordinator.IsPairing && _pairing is not null)
            {
                ClearPairing();
                InfoMessage = "The new device joined.";
                Screen = SyncScreen.Status;
                RefreshStatus();
            }
        }
    }

    private void UpdateCountdown()
    {
        if (_pairing is null) return;
        var left = _pairing.ExpiresAt - _time.GetUtcNow();
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        PairingCountdownText = $"This code expires in {(int)left.TotalMinutes}:{left.Seconds:00}.";
    }

    /// <summary>Closing the pairing screen (or leaving the page) deletes the pairing file.</summary>
    public async Task ClosePairingAsync()
    {
        if (_pairing is null) return;
        ClearPairing();
        if (_coordinator is not null && !await _coordinator.CancelPairingAsync().ConfigureAwait(true))
        {
            InfoMessage = "The pairing code will be removed from OneDrive on the next sync.";
        }
        Screen = SyncScreen.Status;
        RefreshStatus();
    }

    private void ClearPairing()
    {
        _pairing = null;
        PairingQrPng = null;
        PairingCodeText = string.Empty;
        PairingCountdownText = string.Empty;
        ShowPairingCodeText = false;
    }

    // ---- Helpers ----------------------------------------------------------------------------------

    private void ClearMessages()
    {
        ErrorMessage = string.Empty;
        InfoMessage = string.Empty;
    }

    /// <summary>Runs one action at a time with the busy indicator; nothing escapes as an exception.</summary>
    private async Task RunAsync(string busy, Func<Task> action)
    {
        if (IsBusy || !Available) return;
        ClearMessages();
        BusyMessage = busy;
        IsBusy = true;
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception e)
        {
            ErrorMessage = $"Something went wrong: {e.Message}";
        }
        finally
        {
            IsBusy = false;
            BusyMessage = string.Empty;
        }
    }
}
