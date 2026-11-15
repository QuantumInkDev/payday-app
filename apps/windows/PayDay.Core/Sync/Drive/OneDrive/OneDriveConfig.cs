namespace PayDay.Sync.Drive.OneDrive;

/// <summary>
/// Which Microsoft identity app registration (client ID) this build signs in to OneDrive with.
/// <para>
/// There is no registration yet, so <see cref="BuiltInClientId"/> is empty and the setup screen says
/// "OneDrive isn't configured in this build" instead of signing in. A developer can supply one without
/// changing the code: the environment variable <see cref="EnvironmentVariable"/>, or the per-device
/// setting <see cref="SettingKey"/> in this device's Settings table. Client IDs are public identifiers,
/// not secrets, but none is committed until the registration exists.
/// </para>
/// </summary>
public static class OneDriveConfig
{
    /// <summary>The registered app's client ID, set once the app registration exists. Empty: not configured.</summary>
    public const string BuiltInClientId = "";

    /// <summary>Overrides the built-in client ID (for example on a developer's machine).</summary>
    public const string EnvironmentVariable = "FUTURECHECK_ONEDRIVE_CLIENT_ID";

    /// <summary>A per-device Settings table key that overrides the built-in client ID (never synced).</summary>
    public const string SettingKey = "OneDriveClientId";

    public const string NotConfiguredMessage = "OneDrive isn't configured in this build.";

    /// <summary>The vault folder inside the app folder (<c>sync-protocol.md</c> 6.1).</summary>
    public const string VaultFolder = "PayDay";

    /// <summary>
    /// The scope the app asks for: its own app folder only. MSAL always adds <c>offline_access</c>
    /// (the refresh token for silent sign-in), <c>openid</c> and <c>profile</c> itself and refuses them
    /// in this list, so <see cref="ConsentedScopes"/> lists what the user actually consents to.
    /// </summary>
    public static readonly IReadOnlyList<string> RequestedScopes = new[] { "Files.ReadWrite.AppFolder" };

    /// <summary>What the consent covers: <c>Files.ReadWrite.AppFolder offline_access</c>.</summary>
    public static readonly IReadOnlyList<string> ConsentedScopes = new[] { "Files.ReadWrite.AppFolder", "offline_access" };

    /// <summary>The loopback redirect for the system-browser sign-in (any free port is used).</summary>
    public const string RedirectUri = "http://localhost";

    /// <summary>The first valid client ID of: the environment variable, the setting, the built-in one; null when none is.</summary>
    public static string? Resolve(string? environmentValue, string? settingValue, string? builtIn = BuiltInClientId)
    {
        foreach (var candidate in new[] { environmentValue, settingValue, builtIn })
        {
            if (IsValidClientId(candidate)) return candidate!.Trim().ToLowerInvariant();
        }
        return null;
    }

    /// <summary>A client ID is a GUID in its usual 8-4-4-4-12 form.</summary>
    public static bool IsValidClientId(string? clientId)
        => !string.IsNullOrWhiteSpace(clientId) && Guid.TryParseExact(clientId.Trim(), "D", out _);
}
