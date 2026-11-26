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
    /// <summary>This build cannot sign in to any drive provider (no client ID).</summary>
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

    /// <param name="coordinator">Null when this build has no drive provider it can sign in to.</param>
    /// <param name="account">The drive account (sign in / out); a <see cref="IDriveSelection"/> (<see cref="DriveChoice"/>) also lets the user choose the provider while sync is off.</param>
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

    public string ProviderName => _account?.ProviderName ?? "your cloud drive";

    // ---- Choosing the provider (while sync is off) ------------------------------------------------

    private IDriveSelection? Selection => _account as IDriveSelection;

    /// <summary>The providers to choose from on the "Sync is off" screen; empty when there is no choice.</summary>
    public IReadOnlyList<string> ProviderOptions => Selection is { } s && s.Options.Count > 1
        ? s.Options.Select(o => o.Account.ProviderName).ToList()
        : Array.Empty<string>();

    public bool HasProviderChoice => ProviderOptions.Count > 0;

    /// <summary>The chosen provider's position in <see cref="ProviderOptions"/>, or -1.</summary>
    public int SelectedProviderIndex
    {
        get
        {
            if (Selection is not { } s) return -1;
            var options = s.Options;
            for (var i = 0; i < options.Count; i++)
            {
                if (options[i].Key == s.CurrentKey) return i;
            }
            return -1;
        }
        set
        {
            if (Selection is not { } s || value < 0 || value >= s.Options.Count || value == SelectedProviderIndex) return;
            // The vault lives on one provider: the choice is only open while this device is not in one.
            if (IsBusy || Screen != SyncScreen.Off || (_coordinator?.IsInVault ?? false))
            {
                OnPropertyChanged();
                return;
            }
            s.Select(s.Options[value].Key);
            ClearMessages();
            OnPropertyChanged();
            OnProviderChanged();
            _ = RefreshAccountAsync();
        }
    }

    private void OnProviderChanged()
    {
        OnPropertyChanged(nameof(ProviderName));
        OnPropertyChanged(nameof(AccountText));
        OnPropertyChanged(nameof(IntroText));
        OnPropertyChanged(nameof(OffText));
        OnPropertyChanged(nameof(CreateUploadText));
        OnPropertyChanged(nameof(CreateIntroText));
        OnPropertyChanged(nameof(JoinText));
        OnPropertyChanged(nameof(PhraseWarningText));
        OnPropertyChanged(nameof(PairingText));
        OnPropertyChanged(nameof(SignInAgainLabel));
        OnPropertyChanged(nameof(PhraseFileAdvice));
        OnPropertyChanged(nameof(NotConfiguredMessage));
    }

    private async Task RefreshAccountAsync()
    {
        try
        {
            var name = await _account!.GetSignedInUserAsync().ConfigureAwait(true);
            _dispatch(() => AccountName = name);
        }
        catch (Exception)
        {
            _dispatch(() => AccountName = null);
        }
    }

    // ---- Text that names the provider -------------------------------------------------------------

    public string IntroText => $"Keep your bills and payments the same on every device. Your data is encrypted on this PC before it is uploaded: {ProviderName} only stores files it cannot read.";

    public string OffText => $"Turning sync on signs in to {ProviderName}. If you don't use sync yet, this PC's data becomes your synced data (a backup is saved first). If you already sync another device, you join it with your recovery phrase or a pairing code.";

    public string CreateUploadText => $"• encrypts this PC's bills, payments, snapshots and synced settings and uploads them to the app's own {ProviderName} folder;";

    public string CreateIntroText => $"There is no FutureCheck vault on this {ProviderName} account yet. Creating one:";

    public string JoinText => $"Your {ProviderName} already has a FutureCheck vault. Unlock it with your recovery phrase, or with the pairing code another device shows under Sync, Add a device.";

    public string PhraseWarningText => $"These 24 words are the key to your synced data. You need them to add a device without a pairing code, or if this PC's key is lost. They are shown only now. Anyone with these words and access to your {ProviderName} can read your data, and if you lose every device and the words, the synced data is gone. Write them down or save them somewhere private.";

    public string PairingText => $"On the new device, sign in to the same {ProviderName} account, choose Turn on sync, then scan this code (or type the code below). Show it only while pairing, don't screenshot or photograph it, and pair only on a {ProviderName} account that is yours alone.";

    public string SignInAgainLabel => $"Sign in to {ProviderName} again";

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

    /// <summary>"Signed in to Google Drive as …", or empty.</summary>
    public string AccountText => AccountName is { Length: > 0 } name ? $"Signed in to {ProviderName} as {name}." : string.Empty;

    public string NotConfiguredMessage => _account is null || (Selection is { } s && s.Options.Count == 0)
        ? "No cloud drive is set up in this copy of FutureCheck."
        : Selection is null
            ? $"{ProviderName} isn't configured in this build."
            : $"This PC syncs through {ProviderName}, which isn't set up in this copy of FutureCheck.";

    public const string NotConfiguredDetail = "This copy of FutureCheck has no Google Drive or OneDrive app registration, so it cannot sign in. Everything else works as usual on this PC.";

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

    /// <summary>This PC created the vault and the user has not confirmed saving its recovery phrase.</summary>
    [ObservableProperty]
    private bool _hasUnconfirmedPhrase;

    /// <summary>Shows the unconfirmed recovery phrase again (rebuilt from the keystore).</summary>
    public void ShowRecoveryPhraseAgain()
    {
        if (_coordinator?.GetUnconfirmedRecoveryPhrase() is { } phrase) ShowRecoveryPhrase(phrase, uploadPending: null);
    }

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
            $"access to your {ProviderName}, can read that data. Keep this file somewhere safe and private (or print",
            "it and delete the file). If you lose every device and these words, the synced data is lost.",
            string.Empty,
            _recoveryPhrase,
            string.Empty,
        });

    /// <summary>Shown before the file picker: the phrase file must not sit next to the vault.</summary>
    public string PhraseFileAdvice =>
        $"Save the file somewhere {ProviderName} (or any other cloud service) does not back up, such as a USB drive, or print it and delete the file. "
        + $"A copy in {ProviderName} sits in the same account as your vault, so anyone who gets into that account would have both.";

    /// <summary>The OneDrive folders on this PC (personal and work), from the environment variables OneDrive sets.</summary>
    public static IReadOnlyList<string> OneDriveFolders(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }
            .Select(environment)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>True when <paramref name="path"/> is inside one of <paramref name="folders"/> (Windows paths: case-insensitive, either slash).</summary>
    public static bool IsInFolder(string path, IEnumerable<string> folders)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(folders);
        static string Norm(string p) => p.Replace('/', '\\').TrimEnd('\\') + "\\";
        var target = Norm(path);
        return folders.Any(f => target.StartsWith(Norm(f), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True when <paramref name="path"/> looks like a Google Drive for desktop folder: a path segment
    /// named "My Drive", "Shared drives" or "Google Drive" (the streamed drive letter and the mirrored
    /// folder both use them). Google Drive sets no environment variable to look it up by.
    /// </summary>
    public static bool IsInGoogleDriveFolder(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Split('/', '\\').SkipLast(1).Any(segment =>
            segment.Equals("My Drive", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("Shared drives", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("Google Drive", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The cloud service that backs <paramref name="path"/> up ("OneDrive", "Google Drive"), or null.</summary>
    public static string? CloudServiceFor(string path, Func<string, string?> environment)
    {
        if (IsInFolder(path, OneDriveFolders(environment))) return "OneDrive";
        if (IsInGoogleDriveFolder(path)) return "Google Drive";
        return null;
    }

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
        if (IsBusy) return; // an operation (for example creating the vault) is still running
        if (Screen is SyncScreen.Pairing && _coordinator!.IsInVault) return;
        // The phrase of a vault this PC created stays on offer until the user confirms saving it,
        // whatever happened meanwhile (another page, a restart, a crash).
        if (_coordinator!.GetUnconfirmedRecoveryPhrase() is { } phrase)
        {
            if (Screen != SyncScreen.RecoveryPhrase) ShowRecoveryPhrase(phrase, uploadPending: null);
            return;
        }
        Screen = _coordinator.IsInVault ? SyncScreen.Status : SyncScreen.Off;
        RefreshStatus();
    }

    /// <summary>
    /// What to ask before the window closes, or null when closing is fine: an operation still running,
    /// or a recovery phrase the user has not confirmed saving.
    /// </summary>
    public string? CloseWarning()
    {
        if (IsBusy) return $"FutureCheck is still working: {BusyMessage} Closing now stops it; it will finish or be retried next time. Close anyway?";
        if (_coordinator?.GetUnconfirmedRecoveryPhrase() is not null)
        {
            return "You haven't confirmed that you saved your recovery phrase. It will be shown again on the Sync page next time, but without it you can lose your synced data. Close anyway?";
        }
        return null;
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
        HasUnconfirmedPhrase = Screen != SyncScreen.RecoveryPhrase && _coordinator.GetUnconfirmedRecoveryPhrase() is not null;
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

    /// <param name="interactive">True when the cached account no longer works (an expired or revoked sign-in): always open the sign-in page.</param>
    private async Task<bool> EnsureSignedInAsync(bool interactive = false)
    {
        if (!interactive && await _account!.GetSignedInUserAsync().ConfigureAwait(true) is { } user)
        {
            AccountName = user;
            return true;
        }
        BusyMessage = $"Finish signing in to {ProviderName} in your browser…";
        var result = await _account!.SignInAsync().ConfigureAwait(true);
        if (!result.SignedIn)
        {
            if (result.Outcome != DriveSignInOutcome.Cancelled) ErrorMessage = result.Message ?? $"Could not sign in to {ProviderName}.";
            else InfoMessage = "Sign-in was cancelled.";
            return false;
        }
        AccountName = result.UserName;
        return true;
    }

    /// <summary>True when the drive refused the sign-in (expired or revoked refresh token, consent withdrawn).</summary>
    private static bool IsSignedOut(SyncStatus status)
        => status.Phase == SyncPhase.SignedOut || status.ErrorCode == nameof(DriveErrorKind.Unauthorized);

    private async Task ShowCreateOrJoinAsync()
    {
        BusyMessage = $"Looking for your vault on {ProviderName}…";
        var probe = await _coordinator!.ProbeVaultAsync().ConfigureAwait(true);
        if (probe.Outcome == VaultProbeOutcome.SignedOut)
        {
            // The cached account's sign-in no longer works: sign in again, then look once more.
            if (!await EnsureSignedInAsync(interactive: true).ConfigureAwait(true)) return;
            BusyMessage = $"Looking for your vault on {ProviderName}…";
            probe = await _coordinator.ProbeVaultAsync().ConfigureAwait(true);
        }
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
        if (result.Outcome == CreateVaultOutcome.Failed && IsSignedOut(result.Status))
        {
            if (!await EnsureSignedInAsync(interactive: true).ConfigureAwait(true)) return;
            BusyMessage = "Backing up this PC's data and creating your vault…";
            result = await _coordinator.CreateVaultAsync().ConfigureAwait(true);
        }
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
                InfoMessage = $"A vault already exists on your {ProviderName}. Join it instead.";
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

    private void ShowRecoveryPhrase(string phrase, bool? uploadPending)
    {
        _recoveryPhrase = phrase;
        RecoveryWords.Clear();
        var words = phrase.Split(' ');
        for (var i = 0; i < words.Length; i++) RecoveryWords.Add($"{i + 1}. {words[i]}");
        PhraseSaved = false;
        RecoveryPhraseNote = uploadPending is null ? "You haven't confirmed that you saved this phrase yet." : uploadPending.Value
            ? $"Your vault is set up on this PC. It will finish uploading to {ProviderName} on the next sync."
            : $"Your vault is on {ProviderName}.";
        OnPropertyChanged(nameof(RecoveryPhraseFileText));
        Screen = SyncScreen.RecoveryPhrase;
    }

    /// <summary>"I saved it": the phrase leaves the screen and memory for good (it is never offered again).</summary>
    public async Task FinishRecoveryPhraseAsync()
    {
        if (!PhraseSaved || _coordinator is null) return;
        try
        {
            await _coordinator.ConfirmRecoveryPhraseSavedAsync().ConfigureAwait(true);
        }
        catch (Exception e)
        {
            ErrorMessage = $"Could not record that the phrase was saved: {e.Message}";
            return;
        }
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

    /// <summary>One join attempt; a refused sign-in leads to the sign-in page and one more attempt.</summary>
    private async Task<JoinVaultResult?> TryJoinAsync(Func<bool, Task<JoinVaultResult>> join, bool replace)
    {
        var result = await join(replace).ConfigureAwait(true);
        if (result.Outcome == JoinVaultOutcome.Failed && IsSignedOut(result.Status))
        {
            if (!await EnsureSignedInAsync(interactive: true).ConfigureAwait(true)) return null;
            BusyMessage = "Joining your vault…";
            result = await join(replace).ConfigureAwait(true);
        }
        return result;
    }

    private Task JoinAsync(Func<bool, Task<JoinVaultResult>> join) => RunAsync("Joining your vault…", async () =>
    {
        if (await TryJoinAsync(join, false).ConfigureAwait(true) is not { } result) return;
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
        return RunAsync("Backing up this PC's data and joining your vault…", async () =>
        {
            if (await TryJoinAsync(join, true).ConfigureAwait(true) is { } result) HandleJoin(result);
        });
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
                ErrorMessage = $"That pairing code wasn't found on {ProviderName}. It may have expired or not be uploaded yet. Try again in a moment, or start pairing again on the other device.";
                break;
            case JoinVaultOutcome.PairingExpired:
                ErrorMessage = "That pairing code expired. Start pairing again on the other device.";
                break;
            case JoinVaultOutcome.NoVault:
                ErrorMessage = $"There is no vault on this {ProviderName} account yet.";
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
            InfoMessage = $"The pairing code will be removed from {ProviderName} on the next sync.";
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
