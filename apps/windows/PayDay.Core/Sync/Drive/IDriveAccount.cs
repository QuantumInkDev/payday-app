namespace PayDay.Sync.Drive;

/// <summary>How an interactive sign-in ended.</summary>
public enum DriveSignInOutcome
{
    SignedIn,

    /// <summary>The user closed the sign-in page or cancelled.</summary>
    Cancelled,

    /// <summary>This build has no app registration for the provider (no client ID).</summary>
    NotConfigured,

    /// <summary>The sign-in failed; see <see cref="DriveSignInResult.Message"/>.</summary>
    Failed,
}

public sealed record DriveSignInResult(DriveSignInOutcome Outcome, string? UserName, string? Message)
{
    public bool SignedIn => Outcome == DriveSignInOutcome.SignedIn;
}

/// <summary>
/// The user's account with a drive provider, for the setup screens: sign in (interactive, only on a
/// user action), who is signed in, and sign out. The sync loop never signs in interactively; when its
/// silent token refresh fails it reports <see cref="DriveErrorKind.Unauthorized"/> and the UI offers
/// "Sign in again".
/// </summary>
public interface IDriveAccount
{
    /// <summary>"OneDrive".</summary>
    string ProviderName { get; }

    /// <summary>False when this build cannot sign in to the provider at all (no client ID).</summary>
    bool IsConfigured { get; }

    /// <summary>The signed-in account's user name, or null when nobody is signed in.</summary>
    Task<string?> GetSignedInUserAsync(CancellationToken ct = default);

    /// <summary>Interactive sign-in (the system browser). Never throws (except for cancellation).</summary>
    Task<DriveSignInResult> SignInAsync(CancellationToken ct = default);

    /// <summary>Forgets the account and its tokens on this device.</summary>
    Task SignOutAsync(CancellationToken ct = default);
}
