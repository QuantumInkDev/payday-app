using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using PayDay.Sync.Drive;
using PayDay.Sync.Drive.GoogleDrive;

namespace PayDay.Tests.Sync.GoogleDrive;

/// <summary>
/// The Google client configuration, the loopback receiver, and <see cref="GoogleOAuth"/>: the browser
/// sign-in with PKCE and state over a real loopback port (the "browser" is a test callback that follows
/// the redirect), the token endpoint faked, silent refresh, and sign-out. No client ID is real and no
/// request leaves this machine.
/// </summary>
public sealed class GoogleOAuthTests : IDisposable
{
    private const string FictionalClientId = "000000000000-fictionalclient0000.apps.googleusercontent.com";
    private static readonly GoogleOAuthClient Client = new(FictionalClientId, "fictional-secret");

    private readonly InMemoryCredentialStore _credentials = new();
    private readonly FakeTokenEndpoint _endpoint = new();
    private readonly ManualTimeProvider _time = new(1_750_000_000_000);
    private readonly List<GoogleOAuth> _made = new();

    public void Dispose()
    {
        foreach (var auth in _made) auth.Dispose();
    }

    private GoogleOAuth Auth(Func<Uri, bool>? browser = null, GoogleOAuthClient? client = null)
    {
        var auth = new GoogleOAuth(client ?? Client, _credentials, _endpoint, browser ?? FollowRedirect(), _time);
        _made.Add(auth);
        return auth;
    }

    /// <summary>A "browser" that approves at once: it sends the loopback redirect Google would, built by <paramref name="answer"/>.</summary>
    private Func<Uri, bool> FollowRedirect(Func<IReadOnlyDictionary<string, string>, string>? answer = null)
    {
        return url =>
        {
            var q = LoopbackReceiver.ParseRedirectTarget("/" + url.Query)!;
            _endpoint.LastAuthorization = q;
            var reply = answer?.Invoke(q) ?? $"?code=fictional-code&state={Uri.EscapeDataString(q["state"])}";
            _ = Task.Run(async () =>
            {
                using var http = new HttpClient();
                await http.GetAsync(q["redirect_uri"] + "/" + reply);
            });
            return true;
        };
    }

    // ---- Configuration ----------------------------------------------------------------------------

    [Fact]
    public void NoClientIsBuiltIn_AndOneCanBeSuppliedByEnvironmentOrSetting()
    {
        Assert.Empty(GoogleDriveConfig.BuiltInClientId);
        Assert.Empty(GoogleDriveConfig.BuiltInClientSecret);
        Assert.Null(GoogleDriveConfig.Resolve(null, null, null, null));
        Assert.Equal(new GoogleOAuthClient(FictionalClientId, "s1"), GoogleDriveConfig.Resolve($" {FictionalClientId} ", "s1", "999-other.apps.googleusercontent.com", "s2"));
        // An invalid environment value falls through to the setting, with the setting's own secret.
        Assert.Equal(new GoogleOAuthClient(FictionalClientId, "s2"), GoogleDriveConfig.Resolve("not-a-client", "s1", FictionalClientId, "s2"));
        Assert.Equal(new GoogleOAuthClient(FictionalClientId, null), GoogleDriveConfig.Resolve(null, null, FictionalClientId, "  "));
    }

    [Theory]
    [InlineData("1234-abc123.apps.googleusercontent.com", true)]
    [InlineData("1234-.apps.googleusercontent.com", false)]
    [InlineData("-abc.apps.googleusercontent.com", false)]
    [InlineData("12a4-abc.apps.googleusercontent.com", false)]
    [InlineData("1234-ab_c.apps.googleusercontent.com", false)]
    [InlineData("1234-abc.apps.googleusercontent.com.example.invalid", false)]
    [InlineData("00000000-0000-0000-0000-00000000c1d0", false)]
    [InlineData("", false)]
    public void ClientIds_AreChecked(string id, bool valid) => Assert.Equal(valid, GoogleDriveConfig.IsValidClientId(id));

    [Fact]
    public void TheScopes_AreTheAppDataFolderAndTheAccountAddress_Only()
    {
        Assert.Equal(new[] { "https://www.googleapis.com/auth/drive.appdata", "openid", "email" }, GoogleDriveConfig.Scopes);
    }

    [Fact]
    public async Task WithoutAClient_SignInSaysNotConfigured_AndNoTokenIsGiven()
    {
        var auth = new GoogleOAuth(null, _credentials, _endpoint, _ => throw new InvalidOperationException("no browser expected"));
        _made.Add(auth);

        Assert.False(auth.IsConfigured);
        Assert.Equal(DriveSignInOutcome.NotConfigured, (await auth.SignInAsync()).Outcome);
        Assert.Equal(DriveErrorKind.Unauthorized, (await Assert.ThrowsAsync<DriveException>(() => auth.GetAccessTokenAsync(false))).Kind);
        Assert.Null(await auth.GetSignedInUserAsync());
        Assert.Empty(_endpoint.Requests);
    }

    // ---- Sign-in ----------------------------------------------------------------------------------

    [Fact]
    public async Task SigningIn_UsesPkceAndState_StoresTheRefreshToken_AndNamesTheAccount()
    {
        var auth = Auth();

        var result = await auth.SignInAsync();

        Assert.Equal(DriveSignInOutcome.SignedIn, result.Outcome);
        Assert.Equal("someone@example.invalid", result.UserName);
        var q = _endpoint.LastAuthorization!;
        Assert.Equal(FictionalClientId, q["client_id"]);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal("offline", q["access_type"]);
        Assert.Equal(string.Join(' ', GoogleDriveConfig.Scopes), q["scope"]);
        Assert.StartsWith("http://127.0.0.1:", q["redirect_uri"], StringComparison.Ordinal);
        Assert.True(q["state"].Length >= 43);

        var exchange = _endpoint.Requests.Single();
        Assert.Equal("authorization_code", exchange["grant_type"]);
        Assert.Equal("fictional-code", exchange["code"]);
        Assert.Equal(q["redirect_uri"], exchange["redirect_uri"]);
        Assert.Equal("fictional-secret", exchange["client_secret"]);
        Assert.Equal(q["code_challenge"], GoogleOAuth.Challenge(exchange["code_verifier"]));

        Assert.Equal("fictional-refresh-1", _credentials.Get(GoogleOAuth.RefreshTokenKey));
        Assert.Equal("someone@example.invalid", _credentials.Get(GoogleOAuth.AccountKey));
        Assert.Equal("someone@example.invalid", await auth.GetSignedInUserAsync());
        // The access token from the sign-in is used until it is close to expiry.
        Assert.Equal("fictional-access-1", await auth.GetAccessTokenAsync(false));
        Assert.Single(_endpoint.Requests);
    }

    [Fact]
    public async Task AReplyForAnotherSignIn_IsIgnored_UntilTheSignInTimesOut()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var auth = Auth(FollowRedirect(_ => "?code=fictional-code&state=somebody-elses-state"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => auth.SignInAsync(cts.Token));

        Assert.Empty(_endpoint.Requests);
        Assert.Null(_credentials.Get(GoogleOAuth.RefreshTokenKey));
    }

    [Fact]
    public async Task AStrayLocalRequest_DoesNotEndTheSignIn()
    {
        var auth = Auth(url =>
        {
            var q = LoopbackReceiver.ParseRedirectTarget("/" + url.Query)!;
            _endpoint.LastAuthorization = q;
            _ = Task.Run(async () =>
            {
                using var http = new HttpClient();
                // Another local process gets there first, with the wrong state and with none.
                var stray = await http.GetAsync(q["redirect_uri"] + "/?error=access_denied&state=wrong");
                Assert.Equal(HttpStatusCode.NotFound, stray.StatusCode);
                await http.GetAsync(q["redirect_uri"] + "/?code=forged");
                await http.GetAsync(q["redirect_uri"] + $"/?code=fictional-code&state={Uri.EscapeDataString(q["state"])}");
            });
            return true;
        });

        var result = await auth.SignInAsync();

        Assert.Equal(DriveSignInOutcome.SignedIn, result.Outcome);
        Assert.Equal("fictional-code", _endpoint.Requests.Single()["code"]);
    }

    [Fact]
    public async Task DecliningInTheBrowser_IsACancel()
    {
        var auth = Auth(FollowRedirect(q => $"?error=access_denied&state={Uri.EscapeDataString(q["state"])}"));

        var result = await auth.SignInAsync();

        Assert.Equal(DriveSignInOutcome.Cancelled, result.Outcome);
        Assert.Empty(_endpoint.Requests);
    }

    [Fact]
    public async Task UntickingTheDrivePermission_FailsTheSignIn_AndStoresNothing()
    {
        _endpoint.GrantedScope = "openid email";
        var auth = Auth();

        var result = await auth.SignInAsync();

        Assert.Equal(DriveSignInOutcome.Failed, result.Outcome);
        Assert.Contains("permission", result.Message, StringComparison.Ordinal);
        Assert.Empty(_endpoint.Revoked);
        Assert.Null(_credentials.Get(GoogleOAuth.RefreshTokenKey));
    }

    [Fact]
    public async Task NoBrowser_FailsTheSignIn()
    {
        var auth = Auth(_ => false);

        var result = await auth.SignInAsync();

        Assert.Equal(DriveSignInOutcome.Failed, result.Outcome);
        Assert.Contains("browser", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATokenEndpointRefusal_FailsTheSignIn_WithoutStoringAnything()
    {
        _endpoint.Next = () => Reply(HttpStatusCode.Unauthorized, new JsonObject { ["error"] = "invalid_client" });
        var auth = Auth();

        var result = await auth.SignInAsync();

        Assert.Equal(DriveSignInOutcome.Failed, result.Outcome);
        Assert.Contains("client ID or secret", result.Message, StringComparison.Ordinal);
        Assert.Empty(_credentials.Items);
    }

    // ---- Silent refresh and sign-out --------------------------------------------------------------

    [Fact]
    public async Task AnExpiringAccessToken_IsRefreshedWithTheStoredRefreshToken()
    {
        var auth = Auth();
        await auth.SignInAsync();

        _time.Advance((long)TimeSpan.FromMinutes(59).TotalMilliseconds);
        var token = await auth.GetAccessTokenAsync(false);

        Assert.Equal("fictional-access-2", token);
        var refresh = _endpoint.Requests[^1];
        Assert.Equal("refresh_token", refresh["grant_type"]);
        Assert.Equal("fictional-refresh-1", refresh["refresh_token"]);
        Assert.Equal("fictional-access-3", await auth.GetAccessTokenAsync(forceRefresh: true));
    }

    [Fact]
    public async Task ASecondDevicesSession_RefreshesFromTheCredentialStoreAlone()
    {
        _credentials.Set(GoogleOAuth.RefreshTokenKey, "fictional-refresh-1");
        var auth = Auth(_ => throw new InvalidOperationException("no browser expected"));

        Assert.Equal("your Google account", await auth.GetSignedInUserAsync());
        Assert.Equal("fictional-access-1", await auth.GetAccessTokenAsync(false));
    }

    [Fact]
    public async Task ARevokedRefreshToken_MeansSignInAgain()
    {
        _credentials.Set(GoogleOAuth.RefreshTokenKey, "fictional-refresh-1");
        _endpoint.Next = () => Reply(HttpStatusCode.BadRequest, new JsonObject { ["error"] = "invalid_grant" });
        var auth = Auth();

        var e = await Assert.ThrowsAsync<DriveException>(() => auth.GetAccessTokenAsync(false));

        Assert.Equal(DriveErrorKind.Unauthorized, e.Kind);
    }

    [Fact]
    public async Task AnUnreachableOrBusyTokenEndpoint_IsTransient()
    {
        _credentials.Set(GoogleOAuth.RefreshTokenKey, "fictional-refresh-1");
        var auth = Auth();

        _endpoint.Next = () => throw new HttpRequestException("offline");
        Assert.Equal(DriveErrorKind.Transient, (await Assert.ThrowsAsync<DriveException>(() => auth.GetAccessTokenAsync(false))).Kind);
        _endpoint.Next = () => Reply(HttpStatusCode.ServiceUnavailable, new JsonObject { ["error"] = "unavailable" });
        Assert.Equal(DriveErrorKind.Transient, (await Assert.ThrowsAsync<DriveException>(() => auth.GetAccessTokenAsync(false))).Kind);
    }

    [Fact]
    public async Task NotSignedIn_MeansSignInAgain()
    {
        var auth = Auth();

        Assert.Equal(DriveErrorKind.Unauthorized, (await Assert.ThrowsAsync<DriveException>(() => auth.GetAccessTokenAsync(false))).Kind);
        Assert.Null(await auth.GetSignedInUserAsync());
    }

    [Fact]
    public async Task SigningOut_ForgetsTheAccountOnThisDeviceOnly()
    {
        var auth = Auth();
        await auth.SignInAsync();

        await auth.SignOutAsync();

        Assert.Empty(_credentials.Items);
        // Revoking would end the grant for the user's other devices too.
        Assert.Empty(_endpoint.Revoked);
        Assert.Null(await auth.GetSignedInUserAsync());
        await Assert.ThrowsAsync<DriveException>(() => auth.GetAccessTokenAsync(false));
    }

    [Fact]
    public void TheAccountAddress_IsReadFromTheIdToken()
    {
        Assert.Equal("someone@example.invalid", GoogleOAuth.EmailFromIdToken(FakeTokenEndpoint.IdToken("someone@example.invalid")));
        Assert.Null(GoogleOAuth.EmailFromIdToken("not-a-jwt"));
        Assert.Null(GoogleOAuth.EmailFromIdToken("a.%%%.c"));
        Assert.Null(GoogleOAuth.EmailFromIdToken(null));
    }

    // ---- The loopback receiver --------------------------------------------------------------------

    [Fact]
    public async Task TheReceiver_IgnoresOtherRequests_AndAnswersTheRedirect()
    {
        using var receiver = new LoopbackReceiver();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var waiting = receiver.WaitForRedirectAsync(q => q.GetValueOrDefault("state") == "s", _ => "<p>done</p>", cts.Token);
        using var http = new HttpClient();
        // A browser's idle pre-connection that never sends a request must not hold up the redirect.
        using var idle = new TcpClient();
        await idle.ConnectAsync(IPAddress.Loopback, receiver.Port, cts.Token);

        var favicon = await http.GetAsync(receiver.RedirectUri + "/favicon.ico", cts.Token);
        using (var raw = new TcpClient())
        {
            // A connection that sends garbage, then closes.
            await raw.ConnectAsync(IPAddress.Loopback, receiver.Port, cts.Token);
            await raw.GetStream().WriteAsync(Encoding.ASCII.GetBytes("NOT HTTP\r\n\r\n"), cts.Token);
        }
        var redirect = await http.GetAsync(receiver.RedirectUri + "/?code=a%2Bb&state=s", cts.Token);
        var query = await waiting;

        Assert.Equal(HttpStatusCode.NotFound, favicon.StatusCode);
        Assert.Equal(HttpStatusCode.OK, redirect.StatusCode);
        Assert.Contains("done", await redirect.Content.ReadAsStringAsync(cts.Token), StringComparison.Ordinal);
        Assert.Equal("a+b", query["code"]);
        Assert.Equal("s", query["state"]);
    }

    [Fact]
    public void RedirectTargets_AreParsedStrictly()
    {
        Assert.Equal("x y", LoopbackReceiver.ParseRedirectTarget("/?code=x+y")!["code"]);
        Assert.Null(LoopbackReceiver.ParseRedirectTarget("/callback?code=x"));
        Assert.Null(LoopbackReceiver.ParseRedirectTarget("/?code=x&code=y"));
        Assert.Empty(LoopbackReceiver.ParseRedirectTarget("/")!);
    }

    // ---- The fake token endpoint ------------------------------------------------------------------

    private static HttpResponseMessage Reply(HttpStatusCode status, JsonObject body)
        => new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    private sealed class FakeTokenEndpoint : HttpMessageHandler
    {
        private int _accessTokens;

        public List<Dictionary<string, string>> Requests { get; } = new();

        public List<string> Revoked { get; } = new();

        public IReadOnlyDictionary<string, string>? LastAuthorization { get; set; }

        public string GrantedScope { get; set; } = "openid https://www.googleapis.com/auth/drive.appdata https://www.googleapis.com/auth/userinfo.email";

        /// <summary>Answers the next token request instead of the normal reply.</summary>
        public Func<HttpResponseMessage>? Next { get; set; }

        public static string IdToken(string email)
        {
            static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return $"{B64("{\"alg\":\"none\"}")}.{B64(new JsonObject { ["email"] = email, ["sub"] = "1" }.ToJsonString())}.sig";
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var form = body.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Split('='))
                .ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
            if (request.RequestUri!.AbsoluteUri == "https://oauth2.googleapis.com/revoke")
            {
                Revoked.Add(form["token"]);
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            Assert.Equal(GoogleDriveConfig.TokenEndpoint, request.RequestUri.AbsoluteUri);
            Requests.Add(form);
            if (Next is { } next)
            {
                Next = null;
                return next();
            }
            _accessTokens++;
            var reply = new JsonObject
            {
                ["access_token"] = $"fictional-access-{_accessTokens}",
                ["expires_in"] = 3599,
                ["token_type"] = "Bearer",
                ["scope"] = GrantedScope,
            };
            if (form["grant_type"] == "authorization_code")
            {
                reply["refresh_token"] = "fictional-refresh-1";
                reply["id_token"] = IdToken("someone@example.invalid");
            }
            return Reply(HttpStatusCode.OK, reply);
        }
    }
}
