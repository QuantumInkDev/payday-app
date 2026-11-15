using Microsoft.Identity.Client;
using PayDay.Services;

namespace PayDay.Sync.Drive.OneDrive;

/// <summary>
/// Signing in to OneDrive with MSAL.NET as a public client: interactive sign-in in the system browser
/// with the loopback redirect <c>http://localhost</c> (only when the user asks), silent token refresh
/// for the sync loop, and MSAL's token cache kept in the app's credential store
/// (<see cref="CredentialTokenCacheStore"/>; Credential Manager on Windows). Scopes:
/// <c>Files.ReadWrite.AppFolder</c> plus the <c>offline_access</c> MSAL always adds.
/// <para>
/// Without a valid client ID (<see cref="OneDriveConfig"/>) nothing is built: <see cref="IsConfigured"/>
/// is false, sign-in reports <see cref="DriveSignInOutcome.NotConfigured"/>, and tokens fail as
/// unauthorized. The MSAL calls themselves need Microsoft's sign-in service, so they are exercised by
/// the manual test on Windows, not by unit tests.
/// </para>
/// </summary>
public sealed class OneDriveAuth : IOneDriveTokenSource, IDriveAccount
{
    private readonly IPublicClientApplication? _app;
    private readonly CredentialTokenCacheStore _cache;
    private readonly object _cacheLock = new();

    /// <param name="clientId">The app registration's client ID, or null/empty when this build has none.</param>
    /// <param name="credentials">Where the token cache is kept.</param>
    public OneDriveAuth(string? clientId, ICredentialStore credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        _cache = new CredentialTokenCacheStore(credentials);
        if (!OneDriveConfig.IsValidClientId(clientId)) return;
        // The /common authority: personal Microsoft accounts and work or school accounts. The app
        // registration's supported account types must be "Accounts in any organizational directory
        // and personal Microsoft accounts" to match.
        _app = PublicClientApplicationBuilder.Create(clientId!.Trim())
            .WithAuthority(AzureCloudInstance.AzurePublic, AadAuthorityAudience.AzureAdAndPersonalMicrosoftAccount)
            .WithRedirectUri(OneDriveConfig.RedirectUri)
            .Build();
        _app.UserTokenCache.SetBeforeAccess(args =>
        {
            lock (_cacheLock)
            {
                try
                {
                    args.TokenCache.DeserializeMsalV3(_cache.Load(), shouldClearExistingCache: true);
                }
                catch (MsalException)
                {
                    // A damaged cache is an empty one: the user signs in again.
                    args.TokenCache.DeserializeMsalV3(null, shouldClearExistingCache: true);
                }
            }
        });
        _app.UserTokenCache.SetAfterAccess(args =>
        {
            if (!args.HasStateChanged) return;
            lock (_cacheLock) _cache.Save(args.TokenCache.SerializeMsalV3());
        });
    }

    public string ProviderName => "OneDrive";

    public bool IsConfigured => _app is not null;

    public async Task<string?> GetSignedInUserAsync(CancellationToken ct = default)
    {
        if (_app is null) return null;
        var account = (await _app.GetAccountsAsync().ConfigureAwait(false)).FirstOrDefault();
        return account?.Username;
    }

    public async Task<DriveSignInResult> SignInAsync(CancellationToken ct = default)
    {
        if (_app is null) return new DriveSignInResult(DriveSignInOutcome.NotConfigured, null, OneDriveConfig.NotConfiguredMessage);
        try
        {
            var result = await _app.AcquireTokenInteractive(OneDriveConfig.RequestedScopes)
                .WithPrompt(Prompt.SelectAccount)
                .WithUseEmbeddedWebView(false)
                .WithSystemWebViewOptions(new SystemWebViewOptions
                {
                    HtmlMessageSuccess = "<p>Signed in to OneDrive. You can close this tab and return to FutureCheck.</p>",
                    HtmlMessageError = "<p>Sign-in did not finish. Return to FutureCheck and try again.</p>",
                })
                .ExecuteAsync(ct)
                .ConfigureAwait(false);
            return new DriveSignInResult(DriveSignInOutcome.SignedIn, result.Account?.Username, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (MsalClientException e) when (e.ErrorCode == MsalError.AuthenticationCanceledError)
        {
            return new DriveSignInResult(DriveSignInOutcome.Cancelled, null, "Sign-in was cancelled.");
        }
        catch (MsalException e)
        {
            return new DriveSignInResult(DriveSignInOutcome.Failed, null, $"Sign-in failed: {e.Message}");
        }
        catch (Exception e)
        {
            return new DriveSignInResult(DriveSignInOutcome.Failed, null, $"Sign-in failed: {e.Message}");
        }
    }

    public async Task SignOutAsync(CancellationToken ct = default)
    {
        if (_app is not null)
        {
            foreach (var account in await _app.GetAccountsAsync().ConfigureAwait(false))
            {
                await _app.RemoveAsync(account).ConfigureAwait(false);
            }
        }
        lock (_cacheLock) _cache.Clear();
    }

    /// <summary>How a failed silent token request is reported: network errors are transient, everything else needs a new sign-in.</summary>
    internal static DriveErrorKind ClientErrorKind(string? errorCode) => errorCode switch
    {
        MsalError.RequestTimeout or MsalError.ServiceNotAvailable => DriveErrorKind.Transient,
        _ => DriveErrorKind.Unauthorized,
    };

    public async Task<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken ct = default)
    {
        if (_app is null) throw new DriveException(DriveErrorKind.Unauthorized, OneDriveConfig.NotConfiguredMessage);
        var account = (await _app.GetAccountsAsync().ConfigureAwait(false)).FirstOrDefault()
            ?? throw new DriveException(DriveErrorKind.Unauthorized, "Not signed in to OneDrive.");
        try
        {
            var result = await _app.AcquireTokenSilent(OneDriveConfig.RequestedScopes, account)
                .WithForceRefresh(forceRefresh)
                .ExecuteAsync(ct)
                .ConfigureAwait(false);
            return result.AccessToken;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (MsalUiRequiredException e)
        {
            throw new DriveException(DriveErrorKind.Unauthorized, "Sign in to OneDrive again.", e);
        }
        catch (MsalServiceException e) when (e.StatusCode == 0 || e.StatusCode == 429 || e.StatusCode >= 500)
        {
            throw new DriveException(DriveErrorKind.Transient, "The Microsoft sign-in service could not be reached.", e);
        }
        catch (MsalServiceException e)
        {
            throw new DriveException(DriveErrorKind.Unauthorized, "Sign in to OneDrive again.", e);
        }
        catch (MsalClientException e)
        {
            // Only network trouble is worth retrying; anything else would fail the same way on every
            // sync, so the user is asked to sign in again instead.
            var kind = ClientErrorKind(e.ErrorCode);
            throw new DriveException(kind, kind == DriveErrorKind.Transient ? "The Microsoft sign-in service could not be reached." : $"Sign in to OneDrive again ({e.ErrorCode}).", e);
        }
        catch (HttpRequestException e)
        {
            throw new DriveException(DriveErrorKind.Transient, "The Microsoft sign-in service could not be reached.", e);
        }
    }
}
