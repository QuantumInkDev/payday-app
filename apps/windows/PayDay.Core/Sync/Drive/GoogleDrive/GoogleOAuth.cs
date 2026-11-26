using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PayDay.Services;

namespace PayDay.Sync.Drive.GoogleDrive;

/// <summary>Access tokens for the Google Drive API, obtained silently (never interactively: sync runs in the background).</summary>
public interface IGoogleTokenSource
{
    /// <summary>
    /// A current access token. <paramref name="forceRefresh"/> skips the cached one (Drive just said
    /// 401). Throws <see cref="DriveException"/>: <see cref="DriveErrorKind.Unauthorized"/> when the
    /// user must sign in again, <see cref="DriveErrorKind.Transient"/> when Google could not be reached.
    /// </summary>
    Task<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken ct = default);
}

/// <summary>
/// Signing in to Google Drive as an installed app (RFC 8252): the authorization-code flow with PKCE
/// (S256) and a <c>state</c> check, in the system browser, with the redirect received on a loopback port
/// (<see cref="LoopbackReceiver"/>). The sign-in runs only when the user asks; the sync loop refreshes
/// the access token silently with the stored refresh token.
/// <para>
/// Kept in the credential store (Credential Manager on Windows): the refresh token and the account's
/// e-mail address, nothing else. Access tokens stay in memory. Signing out only forgets them on this
/// device: Google's revoke endpoint drops the whole grant for this client, which would sign out the
/// user's other devices too. Without a usable client
/// (<see cref="GoogleDriveConfig"/>) nothing signs in: <see cref="IsConfigured"/> is false and sign-in
/// reports <see cref="DriveSignInOutcome.NotConfigured"/>.
/// </para>
/// </summary>
public sealed class GoogleOAuth : IGoogleTokenSource, IDriveAccount, IDisposable
{
    public const string RefreshTokenKey = "GoogleDrive/RefreshToken";
    public const string AccountKey = "GoogleDrive/Account";

    /// <summary>How long the browser sign-in may take before it is given up.</summary>
    public static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    /// <summary>An access token this close to expiry is refreshed before use.</summary>
    public static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(2);

    private readonly GoogleOAuthClient? _client;
    private readonly ICredentialStore _credentials;
    private readonly HttpClient _http;
    private readonly Func<Uri, bool> _openBrowser;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpires;

    /// <param name="client">The OAuth client, or null when this build has none.</param>
    /// <param name="credentials">Where the refresh token is kept.</param>
    /// <param name="handler">The HTTP handler for Google's token endpoints (tests pass a fake).</param>
    /// <param name="openBrowser">Opens the sign-in page; returns false when no browser could be started. The default uses the system's URL handler.</param>
    public GoogleOAuth(GoogleOAuthClient? client, ICredentialStore credentials, HttpMessageHandler? handler = null, Func<Uri, bool>? openBrowser = null, TimeProvider? time = null)
    {
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _client = client is not null && GoogleDriveConfig.IsValidClientId(client.ClientId) ? client : null;
        _http = new HttpClient(handler ?? new HttpClientHandler(), disposeHandler: handler is null) { Timeout = TimeSpan.FromSeconds(60) };
        _openBrowser = openBrowser ?? OpenSystemBrowser;
        _time = time ?? TimeProvider.System;
    }

    public string ProviderName => "Google Drive";

    public bool IsConfigured => _client is not null;

    public void Dispose()
    {
        _http.Dispose();
        _lock.Dispose();
    }

    public Task<string?> GetSignedInUserAsync(CancellationToken ct = default)
    {
        if (_client is null || _credentials.Get(RefreshTokenKey) is not { Length: > 0 }) return Task.FromResult<string?>(null);
        // The address is only for display; a refresh token without one is still signed in.
        return Task.FromResult<string?>(_credentials.Get(AccountKey) is { Length: > 0 } name ? name : "your Google account");
    }

    // ---- Interactive sign-in ----------------------------------------------------------------------

    public async Task<DriveSignInResult> SignInAsync(CancellationToken ct = default)
    {
        if (_client is null) return new DriveSignInResult(DriveSignInOutcome.NotConfigured, null, GoogleDriveConfig.NotConfiguredMessage);
        try
        {
            using var receiver = new LoopbackReceiver();
            var state = RandomUrlSafe(32);
            var verifier = RandomUrlSafe(32);
            var redirectUri = receiver.RedirectUri;
            var url = AuthorizationUrl(_client.ClientId, redirectUri, state, Challenge(verifier));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SignInTimeout);
            // Only the reply carrying this sign-in's state ends the wait; anything else (another
            // local process, a stale tab) is answered with "not found" and ignored.
            var waiting = receiver.WaitForRedirectAsync(
                q => q.TryGetValue("state", out var s) && FixedTimeEquals(s, state),
                q => RedirectPage(q),
                timeout.Token);
            IReadOnlyDictionary<string, string> query;
            try
            {
                if (!_openBrowser(url))
                {
                    return new DriveSignInResult(DriveSignInOutcome.Failed, null, "Sign-in failed: no web browser could be opened.");
                }
                query = await waiting.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return new DriveSignInResult(DriveSignInOutcome.Cancelled, null, "Sign-in timed out. Try again.");
            }
            finally
            {
                if (!waiting.IsCompleted)
                {
                    // Stop listening before the port closes, so the wait ends as cancelled, not as a stray error.
                    timeout.Cancel();
                    try
                    {
                        await waiting.ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            if (query.TryGetValue("error", out var error))
            {
                return error == "access_denied"
                    ? new DriveSignInResult(DriveSignInOutcome.Cancelled, null, "Sign-in was cancelled.")
                    : new DriveSignInResult(DriveSignInOutcome.Failed, null, $"Sign-in failed: Google said {error}.");
            }
            if (!query.TryGetValue("state", out var returnedState) || !FixedTimeEquals(returnedState, state))
            {
                return new DriveSignInResult(DriveSignInOutcome.Failed, null, "Sign-in failed: the reply did not match this sign-in. Try again.");
            }
            if (!query.TryGetValue("code", out var code) || code.Length == 0)
            {
                return new DriveSignInResult(DriveSignInOutcome.Failed, null, "Sign-in failed: Google sent no authorization code.");
            }
            return await RedeemCodeAsync(code, verifier, redirectUri, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            return new DriveSignInResult(DriveSignInOutcome.Failed, null, $"Sign-in failed: {e.Message}");
        }
    }

    internal static Uri AuthorizationUrl(string clientId, string redirectUri, string state, string challenge)
    {
        var query = new (string, string)[]
        {
            ("client_id", clientId),
            ("redirect_uri", redirectUri),
            ("response_type", "code"),
            ("scope", string.Join(' ', GoogleDriveConfig.Scopes)),
            ("code_challenge", challenge),
            ("code_challenge_method", "S256"),
            ("state", state),
            // A refresh token for silent sync, and the account picker so the user chooses the account.
            ("access_type", "offline"),
            ("prompt", "select_account consent"),
        };
        return new Uri(GoogleDriveConfig.AuthorizationEndpoint + "?" + string.Join('&', query.Select(p => $"{p.Item1}={Uri.EscapeDataString(p.Item2)}")));
    }

    private static string RedirectPage(IReadOnlyDictionary<string, string> query)
    {
        return query.ContainsKey("code") && !query.ContainsKey("error")
            ? "<p>Signed in to Google Drive. You can close this tab and return to FutureCheck.</p>"
            : "<p>Sign-in did not finish. Return to FutureCheck and try again.</p>";
    }

    private async Task<DriveSignInResult> RedeemCodeAsync(string code, string verifier, string redirectUri, CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "authorization_code"),
            new("code", code),
            new("code_verifier", verifier),
            new("redirect_uri", redirectUri),
            new("client_id", _client!.ClientId),
        };
        if (_client.ClientSecret is { } secret) form.Add(new("client_secret", secret));
        TokenReply reply;
        try
        {
            reply = await PostTokenAsync(form, ct).ConfigureAwait(false);
        }
        catch (DriveException e)
        {
            return new DriveSignInResult(DriveSignInOutcome.Failed, null, $"Sign-in failed: {e.Message}");
        }
        if (reply.RefreshToken is not { Length: > 0 } refresh)
        {
            return new DriveSignInResult(DriveSignInOutcome.Failed, null, "Sign-in failed: Google gave no lasting sign-in. Try again.");
        }
        if (!reply.Scopes.Contains(GoogleDriveConfig.DriveScope))
        {
            // Google lets the user untick a permission on the consent page. The grant is not revoked:
            // that would also end the sign-in of the user's other devices.
            return new DriveSignInResult(DriveSignInOutcome.Failed, null, "FutureCheck needs permission to keep its own data in your Google Drive. Sign in again and leave that permission ticked.");
        }
        var account = EmailFromIdToken(reply.IdToken);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _credentials.Set(RefreshTokenKey, refresh);
            if (account is not null) _credentials.Set(AccountKey, account);
            else _credentials.Delete(AccountKey);
            _accessToken = reply.AccessToken;
            _accessTokenExpires = _time.GetUtcNow() + reply.ExpiresIn;
        }
        finally
        {
            _lock.Release();
        }
        return new DriveSignInResult(DriveSignInOutcome.SignedIn, account ?? "your Google account", null);
    }

    // ---- Silent tokens ----------------------------------------------------------------------------

    public async Task<string> GetAccessTokenAsync(bool forceRefresh, CancellationToken ct = default)
    {
        if (_client is null) throw new DriveException(DriveErrorKind.Unauthorized, GoogleDriveConfig.NotConfiguredMessage);
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _accessToken is { } token && _time.GetUtcNow() + ExpiryMargin < _accessTokenExpires) return token;
            var refresh = _credentials.Get(RefreshTokenKey);
            if (refresh is not { Length: > 0 }) throw new DriveException(DriveErrorKind.Unauthorized, "Not signed in to Google Drive.");
            var form = new List<KeyValuePair<string, string>>
            {
                new("grant_type", "refresh_token"),
                new("refresh_token", refresh),
                new("client_id", _client.ClientId),
            };
            if (_client.ClientSecret is { } secret) form.Add(new("client_secret", secret));
            var reply = await PostTokenAsync(form, ct).ConfigureAwait(false);
            if (reply.RefreshToken is { Length: > 0 } rotated && rotated != refresh) _credentials.Set(RefreshTokenKey, rotated);
            _accessToken = reply.AccessToken;
            _accessTokenExpires = _time.GetUtcNow() + reply.ExpiresIn;
            return reply.AccessToken;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Forgets the refresh token and the account on this device (the grant itself stays, see the class notes).</summary>
    public async Task SignOutAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _credentials.Delete(RefreshTokenKey);
            _credentials.Delete(AccountKey);
            _accessToken = null;
        }
        finally
        {
            _lock.Release();
        }
    }

    // ---- Token endpoint ---------------------------------------------------------------------------

    private sealed record TokenReply(string AccessToken, TimeSpan ExpiresIn, string? RefreshToken, string? IdToken, IReadOnlySet<string> Scopes);

    private async Task<TokenReply> PostTokenAsync(IEnumerable<KeyValuePair<string, string>> form, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            using var content = new FormUrlEncodedContent(form);
            using var req = new HttpRequestMessage(HttpMethod.Post, GoogleDriveConfig.TokenEndpoint) { Content = content };
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e)
        {
            throw new DriveException(DriveErrorKind.Transient, "Google's sign-in service could not be reached.", e);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new DriveException(DriveErrorKind.Transient, "Google's sign-in service did not answer in time.", e);
        }
        using (resp)
        {
            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
                root = doc.RootElement.Clone();
            }
            catch (JsonException e)
            {
                throw new DriveException(DriveErrorKind.Transient, "Google's sign-in service sent an unreadable reply.", e);
            }
            if (!resp.IsSuccessStatusCode)
            {
                var error = String(root, "error");
                if ((int)resp.StatusCode >= 500 || resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    throw new DriveException(DriveErrorKind.Transient, $"Google's sign-in service is busy ({(int)resp.StatusCode}).");
                }
                var message = error switch
                {
                    "invalid_grant" => "Sign in to Google Drive again.",
                    "invalid_client" or "unauthorized_client" => "This copy of FutureCheck's Google sign-in is set up wrongly (client ID or secret).",
                    _ => $"Sign in to Google Drive again ({error ?? ((int)resp.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture)}).",
                };
                throw new DriveException(DriveErrorKind.Unauthorized, message);
            }
            var access = String(root, "access_token");
            if (access is not { Length: > 0 }) throw new DriveException(DriveErrorKind.Transient, "Google's sign-in service sent no access token.");
            var expires = root.TryGetProperty("expires_in", out var exp) && exp.ValueKind == JsonValueKind.Number && exp.TryGetInt32(out var seconds) && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.FromMinutes(30);
            var scopes = (String(root, "scope") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            return new TokenReply(access, expires, String(root, "refresh_token"), String(root, "id_token"), scopes);
        }
    }

    private static string? String(JsonElement obj, string name)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// The <c>email</c> claim of the ID token, for display only. The token came straight from Google's
    /// token endpoint over TLS, so its signature is not checked; nothing is decided by it.
    /// </summary>
    internal static string? EmailFromIdToken(string? idToken)
    {
        if (idToken is null) return null;
        var parts = idToken.Split('.');
        if (parts.Length != 3) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return String(doc.RootElement, "email") is { Length: > 0 and <= 320 } email ? email : null;
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return null;
        }
    }

    // ---- Helpers ----------------------------------------------------------------------------------

    internal static string RandomUrlSafe(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    /// <summary>The PKCE S256 challenge: base64url(SHA-256(verifier)).</summary>
    internal static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool FixedTimeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static bool OpenSystemBrowser(Uri url)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
