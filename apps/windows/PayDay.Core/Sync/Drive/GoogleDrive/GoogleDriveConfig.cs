namespace PayDay.Sync.Drive.GoogleDrive;

/// <summary>The OAuth client this build signs in to Google Drive with: a Google Cloud "Desktop app" client.</summary>
/// <param name="ClientId">For example <c>1234-abc.apps.googleusercontent.com</c>.</param>
/// <param name="ClientSecret">
/// The secret Google issues with a desktop client. Google treats it as public for installed apps (it
/// ships inside every copy), but it is still not committed; null when the client has none.
/// </param>
public sealed record GoogleOAuthClient(string ClientId, string? ClientSecret);

/// <summary>
/// Which Google OAuth client this build uses, and the fixed parts of the Google sign-in.
/// <para>
/// No client is built in yet, so <see cref="BuiltInClientId"/> is empty and Google Drive is offered
/// only where a developer supplies one: the environment variables <see cref="ClientIdVariable"/> and
/// <see cref="ClientSecretVariable"/>, or the per-device settings <see cref="ClientIdSettingKey"/> and
/// <see cref="ClientSecretSettingKey"/> in this device's Settings table (never synced).
/// </para>
/// <para>
/// The client: a Google Cloud project (no billing needed) with the Google Drive API enabled, an OAuth
/// consent screen of user type External in publishing status "In production" (in "Testing", Google
/// expires sign-ins after 7 days), the scopes below, and an OAuth client of type "Desktop app". The
/// scopes are non-sensitive, so the app needs no Google review. Every platform's client must live in
/// the same project: the app-data folder belongs to the project, so clients of another project see an
/// empty drive.
/// </para>
/// </summary>
public static class GoogleDriveConfig
{
    /// <summary>The built-in client ID, once one exists. Empty: not configured.</summary>
    public const string BuiltInClientId = "";

    /// <summary>The built-in client secret, once one exists.</summary>
    public const string BuiltInClientSecret = "";

    public const string ClientIdVariable = "FUTURECHECK_GOOGLE_CLIENT_ID";
    public const string ClientSecretVariable = "FUTURECHECK_GOOGLE_CLIENT_SECRET";
    public const string ClientIdSettingKey = "GoogleDriveClientId";
    public const string ClientSecretSettingKey = "GoogleDriveClientSecret";

    public const string NotConfiguredMessage = "Google Drive isn't configured in this build.";

    /// <summary>The vault folder inside the app-data folder (<c>sync-protocol.md</c> 6.1).</summary>
    public const string VaultFolder = "PayDay";

    /// <summary>The app's own hidden folder; nothing else in the user's Drive is visible to it.</summary>
    public const string DriveScope = "https://www.googleapis.com/auth/drive.appdata";

    /// <summary>
    /// What the sign-in asks for: the app-data folder, plus <c>openid email</c> so the Sync page can
    /// say which account is signed in. All three are non-sensitive scopes.
    /// </summary>
    public static readonly IReadOnlyList<string> Scopes = new[] { DriveScope, "openid", "email" };

    public const string AuthorizationEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    /// <summary>
    /// The first usable client: the environment variables, then the settings, then the built-in one.
    /// A client ID and its secret are always taken from the same place, never mixed.
    /// </summary>
    public static GoogleOAuthClient? Resolve(
        string? environmentId,
        string? environmentSecret,
        string? settingId,
        string? settingSecret,
        string? builtInId = BuiltInClientId,
        string? builtInSecret = BuiltInClientSecret)
    {
        foreach (var (id, secret) in new[] { (environmentId, environmentSecret), (settingId, settingSecret), (builtInId, builtInSecret) })
        {
            if (IsValidClientId(id))
            {
                return new GoogleOAuthClient(id!.Trim(), string.IsNullOrWhiteSpace(secret) ? null : secret.Trim());
            }
        }
        return null;
    }

    /// <summary>A Google OAuth client ID: <c>&lt;project number&gt;-&lt;letters and digits&gt;.apps.googleusercontent.com</c>.</summary>
    public static bool IsValidClientId(string? clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return false;
        const string suffix = ".apps.googleusercontent.com";
        var id = clientId.Trim();
        if (!id.EndsWith(suffix, StringComparison.Ordinal) || id.Length == suffix.Length) return false;
        var head = id[..^suffix.Length];
        var dash = head.IndexOf('-');
        if (dash <= 0 || dash == head.Length - 1) return false;
        return head[..dash].All(char.IsAsciiDigit) && head[(dash + 1)..].All(c => char.IsAsciiLetterOrDigit(c));
    }
}
