using PayDay.Services;

namespace PayDay.Sync.Drive;

/// <summary>One drive provider this build can sync through.</summary>
/// <param name="Key">The stable name stored for this device's choice (<see cref="DriveChoice.GoogleDriveKey"/>, <see cref="DriveChoice.OneDriveKey"/>).</param>
public sealed record DriveOption(string Key, IDriveAccount Account, IDriveProvider Provider);

/// <summary>Choosing which provider this device syncs through, for the Sync page.</summary>
public interface IDriveSelection
{
    /// <summary>The providers this build can sign in to, in the order offered.</summary>
    IReadOnlyList<DriveOption> Options { get; }

    /// <summary>The chosen provider's key.</summary>
    string CurrentKey { get; }

    /// <summary>Chooses a provider and remembers it on this device. Only while sync is off.</summary>
    void Select(string key);
}

/// <summary>
/// The drive this device syncs through, picked from the providers the build has (Google Drive first,
/// then OneDrive). It stands in for the chosen provider's <see cref="IDriveProvider"/> and
/// <see cref="IDriveAccount"/>, so the coordinator and the Sync page work with one object while the
/// user can still choose before turning sync on.
/// <para>
/// The choice is kept per device in the credential store (<see cref="ChoiceKey"/>; data-model 6.2:
/// "drive provider and sign-in tokens"). It is stored when the user picks a provider and again when
/// sign-in succeeds, so a vault never moves to another provider because a build gained one: a device in
/// a vault whose stored provider is not configured any more shows "not configured" instead of switching.
/// </para>
/// </summary>
public sealed class DriveChoice : IDriveProvider, IDriveAccount, IDriveSelection
{
    public const string ChoiceKey = "Sync/Provider";
    public const string GoogleDriveKey = "googledrive";
    public const string OneDriveKey = "onedrive";

    private readonly IReadOnlyList<DriveOption> _all;
    private readonly ICredentialStore _store;
    private volatile DriveOption _current;

    /// <param name="options">Every provider in this build, configured or not, in the order offered.</param>
    /// <param name="store">Where this device's choice is kept.</param>
    /// <param name="inVault">
    /// True when this device is in a vault: its provider is kept even when this build cannot sign in
    /// to it. Out of a vault an unusable stored choice gives way to the first configured provider.
    /// </param>
    /// <param name="vaultProviderKey">
    /// The provider recorded with the vault state (<see cref="Coordination.SyncCoordinator.DriveProviderKey"/>),
    /// which wins over the credential store's copy. With neither, OneDrive is assumed: the only
    /// provider before the choice existed.
    /// </param>
    public DriveChoice(IReadOnlyList<DriveOption> options, ICredentialStore store, bool inVault, string? vaultProviderKey = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Count == 0) throw new ArgumentException("At least one provider is required.", nameof(options));
        if (options.Select(o => o.Key).Distinct(StringComparer.Ordinal).Count() != options.Count) throw new ArgumentException("Provider keys must be unique.", nameof(options));
        _all = options;
        _store = store ?? throw new ArgumentNullException(nameof(store));
        var stored = Find(_store.Get(ChoiceKey));
        var firstConfigured = options.FirstOrDefault(o => o.Account.IsConfigured);
        _current = inVault
            ? Find(vaultProviderKey) ?? stored ?? Find(OneDriveKey) ?? firstConfigured ?? options[0]
            : (stored is { Account.IsConfigured: true } ? stored : null) ?? firstConfigured ?? stored ?? options[0];
    }

    private DriveOption? Find(string? key) => key is null ? null : _all.FirstOrDefault(o => o.Key == key);

    /// <summary>The chosen provider.</summary>
    public DriveOption Current => _current;

    public IReadOnlyList<DriveOption> Options => _all.Where(o => o.Account.IsConfigured).ToList();

    public string CurrentKey => _current.Key;

    public void Select(string key)
    {
        var option = Options.FirstOrDefault(o => o.Key == key) ?? throw new ArgumentException($"No configured provider '{key}'.", nameof(key));
        _current = option;
        _store.Set(ChoiceKey, option.Key);
    }

    // ---- IDriveAccount ----------------------------------------------------------------------------

    public string ProviderName => _current.Account.ProviderName;

    public bool IsConfigured => _current.Account.IsConfigured;

    public Task<string?> GetSignedInUserAsync(CancellationToken ct = default) => _current.Account.GetSignedInUserAsync(ct);

    public async Task<DriveSignInResult> SignInAsync(CancellationToken ct = default)
    {
        var option = _current;
        var result = await option.Account.SignInAsync(ct).ConfigureAwait(false);
        if (result.SignedIn) _store.Set(ChoiceKey, option.Key);
        return result;
    }

    public Task SignOutAsync(CancellationToken ct = default) => _current.Account.SignOutAsync(ct);

    // ---- IDriveProvider ---------------------------------------------------------------------------

    public string Name => _current.Provider.Name;

    public string Key => _current.Key;

    public bool EnforcesUniqueNames => _current.Provider.EnforcesUniqueNames;

    public bool DeletesPermanently => _current.Provider.DeletesPermanently;

    public Task<DriveFolderListing> ListAsync(string folder, CancellationToken ct = default) => _current.Provider.ListAsync(folder, ct);

    public Task<byte[]> DownloadAsync(DriveItem item, CancellationToken ct = default) => _current.Provider.DownloadAsync(item, ct);

    public Task<DriveItem> UploadNewAsync(string path, ReadOnlyMemory<byte> data, CancellationToken ct = default) => _current.Provider.UploadNewAsync(path, data, ct);

    public Task DeleteAsync(DriveItem item, CancellationToken ct = default) => _current.Provider.DeleteAsync(item, ct);

    public Task<DateTimeOffset> GetServerTimeAsync(CancellationToken ct = default) => _current.Provider.GetServerTimeAsync(ct);
}
