using System.Net;
using System.Text;
using CouchHud.Core.Integrations;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Tests;

/// <summary>
/// "Sign in with Home Assistant" (OAuth flow of Home Assistant's Authentication API): the sign-in address, the state, the check of the
/// redirect, the token endpoint and the session that refreshes the access token. No network: a fake Home Assistant behind an
/// HttpMessageHandler.
/// </summary>
public sealed class HomeAssistantSignInTests
{
    private const string Base = "http://ha.local:8123";
    private const string Page = "http://ha.local:8123/auth/authorize?response_type=code&client_id=x";

    private sealed class Clock
    {
        public DateTimeOffset Now = new(2026, 10, 27, 20, 0, 0, TimeSpan.Zero);
    }

    private static string StateOf(string authorizeUrl)
    {
        var query = new Uri(authorizeUrl).Query.TrimStart('?').Split('&');
        return Uri.UnescapeDataString(query.Single(p => p.StartsWith("state=", StringComparison.Ordinal))["state=".Length..]);
    }

    private static string Callback(string state, string code = "0123456789abcdef0123456789abcdef") =>
        $"{HomeAssistantLogin.RedirectUri}?code={code}&state={Uri.EscapeDataString(state)}";

    // ---- The sign-in address and the state ----

    [Fact]
    public void The_sign_in_page_is_Home_Assistants_authorize_with_client_id_redirect_and_state()
    {
        var login = new HomeAssistantLogin();
        var url = login.Start(Base + "/");
        var uri = new Uri(url);
        Assert.Equal("http://ha.local:8123/auth/authorize", uri.GetLeftPart(UriPartial.Path));
        Assert.Contains("response_type=code", url);
        Assert.Contains("client_id=" + Uri.EscapeDataString("https://couchhud.invalid/"), url);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString("https://couchhud.invalid/auth/callback"), url);
        // client_id and redirect_uri on the same scheme and host: Home Assistant needs no registration.
        var client = new Uri(HomeAssistantLogin.ClientId);
        var redirect = new Uri(HomeAssistantLogin.RedirectUri);
        Assert.Equal(client.Scheme, redirect.Scheme);
        Assert.Equal(client.Authority, redirect.Authority);
        Assert.True(login.Pending);
        Assert.Equal(Base, login.BaseUrl);
    }

    [Fact]
    public void The_state_has_256_random_bits_and_is_new_every_time()
    {
        var states = Enumerable.Range(0, 200).Select(_ => HomeAssistantLogin.NewState()).ToList();
        Assert.All(states, s =>
        {
            Assert.Equal(43, s.Length);
            Assert.Matches("^[A-Za-z0-9_-]+$", s);
        });
        Assert.Equal(states.Count, states.Distinct().Count());
    }

    [Theory]
    [InlineData("http://ha.local:8123", "http://ha.local:8123")]
    [InlineData(" https://ha.example.org/ ", "https://ha.example.org")]
    [InlineData("http://192.168.1.5:8123//", "http://192.168.1.5:8123")]
    [InlineData("ha.local:8123", null)]
    [InlineData("ftp://ha.local", null)]
    [InlineData("http://user:pw@ha.local:8123", null)]
    [InlineData("http://ha.local:8123/?x=1", null)]
    [InlineData("http://ha.local:8123/#x", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("", null)]
    public void Only_a_plain_http_address_is_a_Home_Assistant_address(string input, string? expected) =>
        Assert.Equal(expected, HomeAssistantLogin.NormalizeBase(input));

    [Theory]
    [InlineData("https://ha.example.org", true)]
    [InlineData("https://203.0.113.7:8123", true)]
    [InlineData("http://homeassistant.local:8123", true)]
    [InlineData("http://HA.LOCAL.", true)]
    [InlineData("http://localhost:8123", true)]
    [InlineData("http://127.0.0.1:8123", true)]
    [InlineData("http://[::1]:8123", true)]
    [InlineData("http://10.0.0.5", true)]
    [InlineData("http://172.16.0.1", true)]
    [InlineData("http://172.31.255.254", true)]
    [InlineData("http://192.168.1.5:8123", true)]
    [InlineData("http://169.254.10.1", true)]
    [InlineData("http://[fe80::1]:8123", true)]
    [InlineData("http://[fd12:3456::1]:8123", true)]
    [InlineData("http://[::ffff:192.168.1.5]:8123", true)]
    // Names only the home network answers, and Tailscale's addresses (100.64.0.0/10).
    [InlineData("http://homeassistant:8123", true)]
    [InlineData("http://local", true)]
    [InlineData("http://ha.home.arpa", true)]
    [InlineData("http://homeassistant.fritz.box:8123", true)]
    [InlineData("http://ha.lan", true)]
    [InlineData("http://ha.internal", true)]
    [InlineData("http://100.64.0.1", true)]
    [InlineData("http://100.101.102.103:8123", true)]
    [InlineData("http://100.127.255.254", true)]
    // Clear text to anything outside the home network: no.
    [InlineData("http://ha.example.org", false)]
    [InlineData("http://203.0.113.7:8123", false)]
    [InlineData("http://172.32.0.1", false)]
    [InlineData("http://172.15.0.1", false)]
    [InlineData("http://192.169.1.1", false)]
    [InlineData("http://8.8.8.8", false)]
    [InlineData("http://[2001:db8::1]", false)]
    [InlineData("http://[::ffff:8.8.8.8]", false)]
    [InlineData("http://100.63.255.255", false)]
    [InlineData("http://100.128.0.1", false)]
    [InlineData("http://home.arpa", false)]
    [InlineData("http://fritz.box", false)]
    [InlineData("http://ha.lan.example.com", false)]
    [InlineData("http://ha.fritz.box.example.com", false)]
    [InlineData("http://myha.box", false)]
    [InlineData("http://ha.duckdns.org", false)]
    [InlineData("http://evil.local.example.com", false)]
    [InlineData("http://localhost.example.com", false)]
    [InlineData("ftp://ha.local", false)]
    [InlineData("", false)]
    public void Sign_in_over_http_only_in_the_home_network(string url, bool allowed) =>
        Assert.Equal(allowed, HomeAssistantLogin.SignInAllowed(url));

    [Fact]
    public void A_public_http_address_starts_no_sign_in()
    {
        var login = new HomeAssistantLogin();
        var ex = Assert.Throws<ArgumentException>(() => login.Start("http://ha.example.org:8123"));
        // The person sees exactly the localized text, no " (Parameter 'baseUrl')" from .NET (VM test 0.5).
        Assert.Equal(Strings.Get("ha.httpsRequired"), ex.Message);
        Assert.False(login.Pending);
        Assert.Contains("https://", login.Start("https://ha.example.org:8123"));
    }

    [Fact]
    public void A_bad_address_starts_no_sign_in()
    {
        var login = new HomeAssistantLogin();
        Assert.Throws<ArgumentException>(() => login.Start("file:///c:/x"));
        Assert.False(login.Pending);
    }

    // ---- The redirect ----

    [Fact]
    public void The_right_state_from_the_right_page_gives_the_code_once()
    {
        var login = new HomeAssistantLogin();
        var state = StateOf(login.Start(Base));
        var result = login.Complete(Callback(state) + "&storeToken=true", Page);
        Assert.Equal(HaCallbackOutcome.Ok, result.Outcome);
        Assert.Equal("0123456789abcdef0123456789abcdef", result.Code);
        Assert.Equal(Base, result.BaseUrl);
        Assert.False(login.Pending);
        // Used once: the same redirect again is nothing.
        Assert.Equal(HaCallbackOutcome.NoFlow, login.Complete(Callback(state), Page).Outcome);
    }

    [Fact]
    public void A_code_of_another_flow_is_never_taken_and_does_not_end_the_running_one()
    {
        var login = new HomeAssistantLogin();
        var state = StateOf(login.Start(Base));
        Assert.Equal(HaCallbackOutcome.WrongState, login.Complete(Callback(HomeAssistantLogin.NewState()), Page).Outcome);
        Assert.Equal(HaCallbackOutcome.WrongState, login.Complete(Callback(state[..^1] + (state[^1] == 'A' ? 'B' : 'A')), Page).Outcome);
        Assert.True(login.Pending);
        Assert.True(login.Complete(Callback(state), Page).Ok);
    }

    [Fact]
    public void A_new_sign_in_makes_the_old_state_worthless()
    {
        var login = new HomeAssistantLogin();
        var first = StateOf(login.Start(Base));
        var second = StateOf(login.Start(Base));
        Assert.Equal(HaCallbackOutcome.WrongState, login.Complete(Callback(first), Page).Outcome);
        Assert.True(login.Complete(Callback(second), Page).Ok);
    }

    [Fact]
    public void Without_a_sign_in_or_after_cancel_nothing_is_taken()
    {
        var login = new HomeAssistantLogin();
        Assert.Equal(HaCallbackOutcome.NoFlow, login.Complete(Callback("abc"), Page).Outcome);
        var state = StateOf(login.Start(Base));
        login.Cancel();
        Assert.False(login.Pending);
        Assert.Equal(HaCallbackOutcome.NoFlow, login.Complete(Callback(state), Page).Outcome);
    }

    [Fact]
    public void A_sign_in_runs_ten_minutes_at_most()
    {
        var clock = new Clock();
        var login = new HomeAssistantLogin(() => clock.Now);
        var state = StateOf(login.Start(Base));
        clock.Now += TimeSpan.FromMinutes(9);
        Assert.True(login.Pending);
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.False(login.Pending);
        Assert.Null(login.BaseUrl);
        Assert.Equal(HaCallbackOutcome.Expired, login.Complete(Callback(state), Page).Outcome);
        Assert.Equal(HaCallbackOutcome.NoFlow, login.Complete(Callback(state), Page).Outcome);
    }

    [Fact]
    public void After_the_ten_minutes_a_wrong_state_is_still_only_a_wrong_state()
    {
        var clock = new Clock();
        var login = new HomeAssistantLogin(() => clock.Now);
        var state = StateOf(login.Start(Base));
        clock.Now += TimeSpan.FromMinutes(11);
        // A page guessing must not be able to end the sign-in (and so close the browser) as "too late".
        Assert.Equal(HaCallbackOutcome.WrongState, login.Complete(Callback(HomeAssistantLogin.NewState()), Page).Outcome);
        Assert.Equal(HaCallbackOutcome.WrongState, login.Complete(Callback(HomeAssistantLogin.NewState()), "https://evil.example/").Outcome);
        // Only the sign-in's own redirect says so.
        Assert.Equal(HaCallbackOutcome.Expired, login.Complete(Callback(state), Page).Outcome);
    }

    [Theory]
    [InlineData("https://evil.example/auth/authorize")]
    [InlineData("https://ha.local:8123/auth/authorize")]
    [InlineData("http://ha.local:8124/auth/authorize")]
    [InlineData("")]
    public void The_redirect_counts_only_from_the_page_of_the_Home_Assistant_signed_in_to(string page)
    {
        var login = new HomeAssistantLogin();
        var state = StateOf(login.Start(Base));
        Assert.Equal(HaCallbackOutcome.WrongPage, login.Complete(Callback(state), page).Outcome);
        // The state is used up: a second try with the right page does not get the code either.
        Assert.Equal(HaCallbackOutcome.NoFlow, login.Complete(Callback(state), Page).Outcome);
    }

    [Theory]
    [InlineData("http://couchhud.invalid/auth/callback?code=abc&state={0}")]
    [InlineData("https://couchhud.invalid:8443/auth/callback?code=abc&state={0}")]
    [InlineData("https://couchhud.invalid/auth/callback/x?code=abc&state={0}")]
    [InlineData("https://couchhud.invalid/other?code=abc&state={0}")]
    [InlineData("https://couchhud.invalid/auth/callback?code=abc&state={0}#frag")]
    [InlineData("https://user@couchhud.invalid/auth/callback?code=abc&state={0}")]
    [InlineData("https://couchhud.invalid.evil.example/auth/callback?code=abc&state={0}")]
    [InlineData("https://evil.example/auth/callback?code=abc&state={0}")]
    [InlineData("https://couchhud.invalid/auth/callback?code=abc&code=def&state={0}")]
    [InlineData("https://couchhud.invalid/auth/callback?code=abc&state={0}&state={0}")]
    [InlineData("https://couchhud.invalid/auth/callback?code=a%20b&state={0}")]
    [InlineData("https://couchhud.invalid/auth/callback?code=a%0Ab&state={0}")]
    [InlineData("https://couchhud.invalid/auth/callback?code=&state={0}")]
    [InlineData("https://couchhud.invalid/auth/callback?state={0}")]
    [InlineData("https://couchhud.invalid/auth/callback?code=abc")]
    [InlineData("not a url {0}")]
    public void Only_exactly_the_expected_redirect_is_read(string template)
    {
        var login = new HomeAssistantLogin();
        var state = StateOf(login.Start(Base));
        var result = login.Complete(string.Format(template, state), Page);
        Assert.Equal(HaCallbackOutcome.Malformed, result.Outcome);
        Assert.Null(result.Code);
        // A malformed redirect leaves the sign-in running.
        Assert.True(login.Pending);
    }

    [Fact]
    public void A_code_longer_than_the_limit_is_not_taken()
    {
        var login = new HomeAssistantLogin();
        var state = StateOf(login.Start(Base));
        Assert.Equal(HaCallbackOutcome.Malformed, login.Complete(Callback(state, new string('a', HomeAssistantLogin.MaxCodeLength + 1)), Page).Outcome);
        Assert.True(login.Complete(Callback(state, new string('a', HomeAssistantLogin.MaxCodeLength)), Page).Ok);
    }

    [Fact]
    public void A_refusal_ends_the_sign_in_without_a_code()
    {
        var login = new HomeAssistantLogin();
        var state = StateOf(login.Start(Base));
        var result = login.Complete($"{HomeAssistantLogin.RedirectUri}?error=access_denied&state={state}", Page);
        Assert.Equal(HaCallbackOutcome.Denied, result.Outcome);
        Assert.Null(result.Code);
        Assert.False(login.Pending);
    }

    [Theory]
    [InlineData("https://couchhud.invalid/auth/callback?code=x", true)]
    [InlineData("http://COUCHHUD.INVALID/anything", true)]
    [InlineData("https://couchhud.invalid./auth/callback", true)]
    [InlineData("https://couchhud.invalid:444/", true)]
    [InlineData("https://couchhud.invalid.example/", false)]
    [InlineData("https://example.org/?r=https://couchhud.invalid/", false)]
    [InlineData("https://xcouchhud.invalid/", false)]
    [InlineData("data:text/html,couchhud.invalid", false)]
    [InlineData(null, false)]
    public void The_browser_never_loads_the_redirect_host_in_any_form(string? url, bool redirect) =>
        Assert.Equal(redirect, HomeAssistantLogin.IsRedirect(url));

    [Fact]
    public void The_bar_shows_Home_Assistants_page_without_the_state()
    {
        Assert.Equal("http://ha.local:8123/auth/authorize", HomeAssistantLogin.Shown(Page + "&state=secret"));
        Assert.True(HomeAssistantLogin.SameOrigin(Page, Base));
        Assert.False(HomeAssistantLogin.SameOrigin("https://ha.local:8123/", Base));
    }

    [Fact]
    public void Logs_never_see_the_code()
    {
        var result = new HaCallback(HaCallbackOutcome.Ok, "the-code", Base);
        Assert.DoesNotContain("the-code", result.ToString());
        var tokens = new HaTokens("access-secret", "refresh-secret", TimeSpan.FromMinutes(30));
        Assert.DoesNotContain("secret", tokens.ToString());
    }

    // ---- The token endpoint ----

    /// <summary>A fake Home Assistant: records each request, answers with the function's response.</summary>
    private sealed class FakeHa(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Method, string Url, string Body, string? Auth)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Requests) Requests.Add((request.Method.Method, request.RequestUri!.ToString(), body, request.Headers.Authorization?.ToString()));
            var response = respond(request, body);
            response.RequestMessage = request;
            return response;
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private const string CodeAnswer = """{"access_token":"ACCESS1","expires_in":1800,"refresh_token":"REFRESH1","token_type":"Bearer"}""";

    [Fact]
    public async Task The_code_is_exchanged_at_the_same_Home_Assistant_for_both_tokens()
    {
        var ha = new FakeHa((_, _) => Json(HttpStatusCode.OK, CodeAnswer));
        var tokens = await new HomeAssistantAuth(new HttpClient(ha)).ExchangeCodeAsync(Base + "/", "CODE1");
        Assert.Equal("ACCESS1", tokens.AccessToken);
        Assert.Equal("REFRESH1", tokens.RefreshToken);
        Assert.Equal(TimeSpan.FromMinutes(30), tokens.ExpiresIn);
        var (method, url, body, auth) = Assert.Single(ha.Requests);
        Assert.Equal("POST", method);
        Assert.Equal("http://ha.local:8123/auth/token", url);
        Assert.Null(auth);
        Assert.Equal("grant_type=authorization_code&code=CODE1&client_id=" + Uri.EscapeDataString(HomeAssistantLogin.ClientId), body);
    }

    [Fact]
    public async Task The_access_token_is_renewed_with_the_refresh_token()
    {
        var ha = new FakeHa((_, _) => Json(HttpStatusCode.OK, """{"access_token":"ACCESS2","expires_in":1800,"token_type":"Bearer"}"""));
        var tokens = await new HomeAssistantAuth(new HttpClient(ha)).RefreshAsync(Base, "REFRESH1");
        Assert.Equal("ACCESS2", tokens.AccessToken);
        Assert.Null(tokens.RefreshToken);
        Assert.Equal("grant_type=refresh_token&refresh_token=REFRESH1&client_id=" + Uri.EscapeDataString(HomeAssistantLogin.ClientId), ha.Requests[0].Body);
    }

    [Theory]
    [InlineData("invalid_grant")]
    [InlineData("access_denied")]
    public async Task A_refused_code_or_refresh_token_asks_to_sign_in_again(string error)
    {
        var ha = new FakeHa((_, _) => Json(HttpStatusCode.BadRequest, $$"""{"error":"{{error}}","error_description":"Invalid code"}"""));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => new HomeAssistantAuth(new HttpClient(ha)).RefreshAsync(Base, "REFRESH1"));
        Assert.Equal(Strings.Get("ha.signInAgain"), ex.Message);
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.DoesNotContain("REFRESH1", ex.Message);
    }

    [Fact]
    public async Task The_token_endpoint_follows_no_redirect()
    {
        var ha = new FakeHa((_, _) =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Found);
            r.Headers.Location = new Uri("https://evil.example/steal?code=CODE1");
            return r;
        });
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => new HomeAssistantAuth(new HttpClient(ha)).ExchangeCodeAsync(Base, "CODE1"));
        Assert.Contains("evil.example/steal", ex.Message);
        Assert.DoesNotContain("CODE1", ex.Message);
        Assert.Single(ha.Requests);
        // The client the host uses really does not follow redirects.
        var handler = typeof(HttpMessageInvoker).GetField("_handler", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(HomeAssistantClient.CreateHttpClient());
        Assert.False(Assert.IsType<SocketsHttpHandler>(handler).AllowAutoRedirect);
    }

    [Theory]
    [InlineData("""{"access_token":"A","expires_in":1800,"token_type":"Bearer"}""")]
    [InlineData("""{"access_token":"","expires_in":1800,"refresh_token":"R","token_type":"Bearer"}""")]
    [InlineData("""{"access_token":"A","expires_in":1800,"refresh_token":"R","token_type":"mac"}""")]
    [InlineData("""{"access_token":"A","expires_in":-5,"refresh_token":"R"}""")]
    [InlineData("""{"access_token":"A B","expires_in":1800,"refresh_token":"R"}""")]
    [InlineData("""{"access_token":5,"refresh_token":"R"}""")]
    [InlineData("""["A"]""")]
    [InlineData("<html>login</html>")]
    public async Task A_strange_token_answer_is_refused(string json)
    {
        var ha = new FakeHa((_, _) => Json(HttpStatusCode.OK, json));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => new HomeAssistantAuth(new HttpClient(ha)).ExchangeCodeAsync(Base, "CODE1"));
        Assert.Equal(Strings.Get("ha.signInBadAnswer"), ex.Message);
    }

    [Fact]
    public async Task A_huge_token_answer_is_not_read()
    {
        var big = "{\"access_token\":\"" + new string('a', HomeAssistantAuth.MaxAnswerBytes) + "\"}";
        var ha = new FakeHa((_, _) => Json(HttpStatusCode.OK, big));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => new HomeAssistantAuth(new HttpClient(ha)).ExchangeCodeAsync(Base, "CODE1"));
        Assert.Equal(Strings.Get("ha.tooLarge"), ex.Message);
    }

    [Fact]
    public void A_long_lifetime_is_cut_and_a_missing_one_is_half_an_hour()
    {
        Assert.Equal(HomeAssistantAuth.MaxLifetime, HomeAssistantAuth.ParseTokens(Encoding.UTF8.GetBytes("""{"access_token":"A","expires_in":99999999}"""))!.ExpiresIn);
        Assert.Equal(TimeSpan.FromMinutes(30), HomeAssistantAuth.ParseTokens(Encoding.UTF8.GetBytes("""{"access_token":"A"}"""))!.ExpiresIn);
    }

    [Fact]
    public async Task Sign_out_revokes_at_auth_revoke()
    {
        var ha = new FakeHa((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        Assert.True(await new HomeAssistantAuth(new HttpClient(ha)).RevokeAsync(Base, "REFRESH1"));
        var request = Assert.Single(ha.Requests);
        Assert.Equal("http://ha.local:8123/auth/revoke", request.Url);
        Assert.Equal("token=REFRESH1", request.Body);
    }

    [Fact]
    public async Task An_older_Home_Assistant_is_revoked_with_action_revoke_on_auth_token()
    {
        var ha = new FakeHa((r, _) => r.RequestUri!.AbsolutePath == "/auth/revoke" ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK));
        Assert.True(await new HomeAssistantAuth(new HttpClient(ha)).RevokeAsync(Base, "REFRESH1"));
        Assert.Equal(2, ha.Requests.Count);
        Assert.Equal("http://ha.local:8123/auth/token", ha.Requests[1].Url);
        Assert.Equal("token=REFRESH1&action=revoke", ha.Requests[1].Body);
    }

    // ---- The session and the REST client ----

    [Fact]
    public async Task The_session_uses_its_access_token_until_shortly_before_the_end()
    {
        var clock = new Clock();
        var refreshes = 0;
        var ha = new FakeHa((_, _) =>
        {
            Interlocked.Increment(ref refreshes);
            return Json(HttpStatusCode.OK, """{"access_token":"ACCESS2","expires_in":1800,"token_type":"Bearer"}""");
        });
        var session = new HomeAssistantSession(new HomeAssistantAuth(new HttpClient(ha)), Base, "REFRESH1", () => clock.Now);
        session.Seed(new HaTokens("ACCESS1", "REFRESH1", TimeSpan.FromMinutes(30)));
        Assert.Equal("ACCESS1", await session.AccessTokenAsync());
        clock.Now += TimeSpan.FromMinutes(28);
        Assert.Equal("ACCESS1", await session.AccessTokenAsync());
        clock.Now += TimeSpan.FromMinutes(1.5);
        // Ten callers at once: one refresh.
        var all = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => session.AccessTokenAsync()));
        Assert.All(all, t => Assert.Equal("ACCESS2", t));
        Assert.Equal(1, refreshes);
        Assert.Equal("http://ha.local:8123/auth/token", ha.Requests[0].Url);
    }

    [Fact]
    public async Task A_refused_access_token_is_renewed_once_and_the_call_made_again()
    {
        var ha = new FakeHa((r, _) => r.RequestUri!.AbsolutePath switch
        {
            "/auth/token" => Json(HttpStatusCode.OK, """{"access_token":"ACCESS2","expires_in":1800,"token_type":"Bearer"}"""),
            "/api/states" when r.Headers.Authorization?.Parameter == "ACCESS2" => Json(HttpStatusCode.OK, """[{"entity_id":"light.sofa","state":"on","attributes":{}}]"""),
            _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        });
        var http = new HttpClient(ha);
        var session = new HomeAssistantSession(new HomeAssistantAuth(http), Base, "REFRESH1");
        session.Seed(new HaTokens("ACCESS1", "REFRESH1", TimeSpan.FromMinutes(30)));
        var client = new HomeAssistantClient(http) { BaseUrl = Base, Session = session };
        var entities = await client.GetEntitiesAsync();
        Assert.Equal("light.sofa", Assert.Single(entities).Id);
        Assert.Equal(["Bearer ACCESS1", null, "Bearer ACCESS2"], ha.Requests.Select(r => r.Auth));
    }

    [Fact]
    public async Task A_dead_sign_in_says_sign_in_again_and_does_not_loop()
    {
        var ha = new FakeHa((r, _) => r.RequestUri!.AbsolutePath == "/auth/token"
            ? Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""")
            : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var http = new HttpClient(ha);
        var session = new HomeAssistantSession(new HomeAssistantAuth(http), Base, "REFRESH1");
        var client = new HomeAssistantClient(http) { BaseUrl = Base, Session = session };
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.ActivateAsync("light.sofa"));
        Assert.Equal(Strings.Get("ha.signInAgain"), ex.Message);
        Assert.Single(ha.Requests);
    }

    [Fact]
    public async Task An_old_http_session_to_a_public_address_ends_at_once_and_says_https()
    {
        const string Public = "http://ha.example.org:8123";
        var ha = new FakeHa((_, _) => Json(HttpStatusCode.OK, """{"access_token":"ACCESS2","expires_in":1800,"token_type":"Bearer"}"""));
        var session = new HomeAssistantSession(new HomeAssistantAuth(new HttpClient(ha)), Public, "REFRESH1");
        Assert.True(session.NeedsHttps);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => session.AccessTokenAsync());
        Assert.Equal(Strings.Get("ha.httpsRequired"), ex.Message);
        Assert.True(session.Dead);
        // No retry on every call: the next one fails at once with the same reason, and nothing ever went out.
        ex = await Assert.ThrowsAsync<HttpRequestException>(() => session.AccessTokenAsync());
        Assert.Equal(Strings.Get("ha.httpsRequired"), ex.Message);
        Assert.Empty(ha.Requests);
        Assert.False(new HomeAssistantSession(new HomeAssistantAuth(new HttpClient(ha)), Base, "REFRESH1").NeedsHttps);
    }

    [Fact]
    public async Task A_session_for_another_address_sends_nothing()
    {
        var ha = new FakeHa((_, _) => Json(HttpStatusCode.OK, "[]"));
        var http = new HttpClient(ha);
        var session = new HomeAssistantSession(new HomeAssistantAuth(http), "http://other.local:8123", "REFRESH1");
        var client = new HomeAssistantClient(http) { BaseUrl = Base, Session = session, Token = "LONGLIVED" };
        Assert.False(client.Configured);
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetEntitiesAsync());
        Assert.Empty(ha.Requests);
    }

    [Fact]
    public async Task The_registry_socket_gets_the_sessions_access_token()
    {
        var ha = new FakeHa((_, _) => Json(HttpStatusCode.OK, "[]"));
        var http = new HttpClient(ha);
        var session = new HomeAssistantSession(new HomeAssistantAuth(http), Base, "REFRESH1");
        session.Seed(new HaTokens("ACCESS1", "REFRESH1", TimeSpan.FromMinutes(30)));
        string? socketToken = null;
        var client = new HomeAssistantClient(http)
        {
            BaseUrl = Base,
            Session = session,
            FetchRegistry = (_, token, _) =>
            {
                socketToken = token;
                return Task.FromResult(HaRegistryAnswer.Failed);
            }
        };
        await client.GetCatalogAsync();
        Assert.Equal("ACCESS1", socketToken);
    }

    // ---- PKCE (S256) ----

    private static string ParamOf(string url, string name)
    {
        var query = new Uri(url).Query.TrimStart('?').Split('&');
        return Uri.UnescapeDataString(query.Single(p => p.StartsWith(name + "=", StringComparison.Ordinal))[(name.Length + 1)..]);
    }

    [Fact]
    public void The_S256_challenge_matches_the_RFC_7636_example() =>
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", HomeAssistantLogin.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));

    [Fact]
    public void Every_sign_in_sends_a_challenge_and_only_the_callback_gets_its_verifier()
    {
        var login = new HomeAssistantLogin();
        var url = login.Start(Base, generation: 7);
        Assert.Equal("S256", ParamOf(url, "code_challenge_method"));
        var challenge = ParamOf(url, "code_challenge");
        Assert.Matches("^[A-Za-z0-9_-]{43}$", challenge);
        var result = login.Complete(Callback(StateOf(url)), Page);
        Assert.True(result.Ok);
        Assert.NotNull(result.Verifier);
        Assert.True(result.Verifier!.Length >= 43);
        Assert.Matches("^[A-Za-z0-9_-]+$", result.Verifier);
        Assert.Equal(challenge, HomeAssistantLogin.Challenge(result.Verifier));
        // The verifier is not in the page's address and not in the log.
        Assert.DoesNotContain(result.Verifier, url);
        Assert.DoesNotContain(result.Verifier, result.ToString());
        Assert.Equal(7, result.Generation);
        // A new sign-in, a new verifier.
        var again = new HomeAssistantLogin();
        Assert.NotEqual(challenge, ParamOf(again.Start(Base), "code_challenge"));
    }

    [Fact]
    public async Task The_code_is_exchanged_with_the_verifier()
    {
        var ha = new FakeHa((_, _) => Json(HttpStatusCode.OK, CodeAnswer));
        await new HomeAssistantAuth(new HttpClient(ha)).ExchangeCodeAsync(Base, "CODE1", "VERIFIER-0123456789-0123456789-0123456789-xyz");
        Assert.EndsWith("&code_verifier=VERIFIER-0123456789-0123456789-0123456789-xyz", Assert.Single(ha.Requests).Body);
    }

    [Theory]
    [InlineData("invalid_request")]
    [InlineData("invalid_grant")]
    public async Task A_refused_code_asks_to_sign_in_again(string error)
    {
        var ha = new FakeHa((_, _) => Json(HttpStatusCode.BadRequest, $$"""{"error":"{{error}}"}"""));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => new HomeAssistantAuth(new HttpClient(ha)).ExchangeCodeAsync(Base, "CODE1", "V"));
        Assert.Equal(Strings.Get("ha.signInAgain"), ex.Message);
        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
    }

    // ---- A refused sign-in sends nothing more ----

    [Fact]
    public async Task After_a_refused_refresh_token_polling_sends_exactly_one_request()
    {
        var ha = new FakeHa((r, _) => r.RequestUri!.AbsolutePath == "/auth/token"
            ? Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""")
            : Json(HttpStatusCode.OK, "[]"));
        var http = new HttpClient(ha);
        var session = new HomeAssistantSession(new HomeAssistantAuth(http), Base, "REFRESH1");
        var client = new HomeAssistantClient(http) { BaseUrl = Base, Session = session };
        for (var i = 0; i < 10; i++)
        {
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetEntitiesAsync());
            Assert.Equal(Strings.Get("ha.signInAgain"), ex.Message);
        }
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Assert.ThrowsAsync<HttpRequestException>(() => client.ActivateAsync("light.sofa"))));
        Assert.Empty(await client.GetRegistryAsync());
        Assert.True(session.Dead);
        Assert.Single(ha.Requests);
    }

    [Fact]
    public async Task A_refresh_that_fails_otherwise_does_not_kill_the_session()
    {
        var fail = true;
        var ha = new FakeHa((_, _) => fail
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            : Json(HttpStatusCode.OK, """{"access_token":"ACCESS2","expires_in":1800}"""));
        var session = new HomeAssistantSession(new HomeAssistantAuth(new HttpClient(ha)), Base, "REFRESH1");
        await Assert.ThrowsAsync<HttpRequestException>(() => session.AccessTokenAsync());
        Assert.False(session.Dead);
        fail = false;
        Assert.Equal("ACCESS2", await session.AccessTokenAsync());
    }

    // ---- The token goes only to the session's own address ----

    [Fact]
    public async Task An_address_changed_during_the_refresh_gets_no_token()
    {
        HomeAssistantClient? client = null;
        var ha = new FakeHa((r, _) =>
        {
            if (r.RequestUri!.AbsolutePath != "/auth/token") return Json(HttpStatusCode.OK, "[]");
            // While the refresh runs, the address setting moves to another server.
            client!.BaseUrl = "http://evil.local:8123";
            return Json(HttpStatusCode.OK, """{"access_token":"ACCESS2","expires_in":1800}""");
        });
        var http = new HttpClient(ha);
        var session = new HomeAssistantSession(new HomeAssistantAuth(http), Base, "REFRESH1");
        client = new HomeAssistantClient(http) { BaseUrl = Base, Session = session };
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetEntitiesAsync());
        var only = Assert.Single(ha.Requests);
        Assert.Equal("http://ha.local:8123/auth/token", only.Url);
        Assert.DoesNotContain(ha.Requests, r => r.Url.Contains("evil.local", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_session_replaced_during_the_refresh_gets_no_call()
    {
        HomeAssistantClient? client = null;
        HomeAssistantSession? other = null;
        var ha = new FakeHa((r, _) =>
        {
            if (r.RequestUri!.AbsolutePath != "/auth/token") return Json(HttpStatusCode.OK, "[]");
            client!.Session = other;
            return Json(HttpStatusCode.OK, """{"access_token":"ACCESS2","expires_in":1800}""");
        });
        var http = new HttpClient(ha);
        other = new HomeAssistantSession(new HomeAssistantAuth(http), Base, "REFRESH2");
        var session = new HomeAssistantSession(new HomeAssistantAuth(http), Base, "REFRESH1");
        client = new HomeAssistantClient(http) { BaseUrl = Base, Session = session };
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ActivateAsync("light.sofa"));
        Assert.DoesNotContain(ha.Requests, r => r.Url.Contains("/api/", StringComparison.Ordinal));
    }

    // ---- Finishing: kept only if nothing changed, otherwise revoked ----

    private static HaCallback OkCallback(HomeAssistantSignIn signIn) =>
        new(HaCallbackOutcome.Ok, "CODE1", Base, "VERIFIER", signIn.Generation);

    private static FakeHa TokenServer(Action? duringExchange = null) => new((r, body) =>
    {
        if (r.RequestUri!.AbsolutePath == "/auth/token" && body.Contains("grant_type=authorization_code", StringComparison.Ordinal))
        {
            duringExchange?.Invoke();
            return Json(HttpStatusCode.OK, CodeAnswer);
        }
        return new HttpResponseMessage(HttpStatusCode.OK);
    });

    [Fact]
    public async Task A_finished_sign_in_is_saved_and_bumps_the_version()
    {
        var ha = TokenServer();
        var signIn = new HomeAssistantSignIn(new HomeAssistantAuth(new HttpClient(ha)));
        var callback = OkCallback(signIn);
        HomeAssistantSession? saved = null;
        var finish = await signIn.FinishAsync(callback, () => null, s => saved = s);
        Assert.True(finish.Ok);
        Assert.Equal("REFRESH1", saved!.RefreshToken);
        Assert.Equal(Base, saved.BaseUrl);
        Assert.NotEqual(callback.Generation, signIn.Generation);
        Assert.DoesNotContain(ha.Requests, r => r.Url.EndsWith("/auth/revoke", StringComparison.Ordinal));
        Assert.Contains("code_verifier=VERIFIER", ha.Requests[0].Body);
    }

    [Fact]
    public async Task A_sign_out_during_the_exchange_discards_and_revokes_the_result()
    {
        HomeAssistantSignIn? signIn = null;
        var ha = TokenServer(() => signIn!.Changed());
        signIn = new HomeAssistantSignIn(new HomeAssistantAuth(new HttpClient(ha)));
        var committed = false;
        var finish = await signIn.FinishAsync(OkCallback(signIn), () => null, _ => committed = true);
        Assert.Equal(HaFinishOutcome.Stale, finish.Outcome);
        Assert.Equal(Strings.Get("ha.signInStale"), finish.Error);
        Assert.False(committed);
        var revoke = Assert.Single(ha.Requests, r => r.Url == "http://ha.local:8123/auth/revoke");
        Assert.Equal("token=REFRESH1", revoke.Body);
    }

    [Fact]
    public async Task A_sign_in_that_cannot_be_saved_is_revoked()
    {
        var ha = TokenServer();
        var signIn = new HomeAssistantSignIn(new HomeAssistantAuth(new HttpClient(ha)));
        var before = signIn.Generation;
        var finish = await signIn.FinishAsync(OkCallback(signIn), () => null, _ => throw new IOException("disk full"));
        Assert.Equal(HaFinishOutcome.NotSaved, finish.Outcome);
        Assert.Equal(Strings.Get("ha.signInNotSaved"), finish.Error);
        Assert.Equal(before, signIn.Generation);
        Assert.Contains(ha.Requests, r => r.Url.EndsWith("/auth/revoke", StringComparison.Ordinal) && r.Body == "token=REFRESH1");
    }

    [Fact]
    public async Task A_sign_in_finishing_on_a_locked_couch_is_revoked()
    {
        var ha = TokenServer();
        var signIn = new HomeAssistantSignIn(new HomeAssistantAuth(new HttpClient(ha)));
        var committed = false;
        var finish = await signIn.FinishAsync(OkCallback(signIn), () => Strings.Get("ha.signInLocked"), _ => committed = true);
        Assert.Equal(HaFinishOutcome.Blocked, finish.Outcome);
        Assert.False(committed);
        Assert.Contains(ha.Requests, r => r.Url.EndsWith("/auth/revoke", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_refused_code_keeps_nothing_and_revokes_nothing()
    {
        var ha = new FakeHa((_, _) => Json(HttpStatusCode.BadRequest, """{"error":"invalid_request"}"""));
        var signIn = new HomeAssistantSignIn(new HomeAssistantAuth(new HttpClient(ha)));
        var committed = false;
        var finish = await signIn.FinishAsync(OkCallback(signIn), () => null, _ => committed = true);
        Assert.Equal(HaFinishOutcome.Failed, finish.Outcome);
        Assert.Contains(Strings.Get("ha.signInAgain"), finish.Error);
        Assert.DoesNotContain("CODE1", finish.Error);
        Assert.False(committed);
        Assert.Single(ha.Requests);
    }

    // ---- The couch lock ----

    [Theory]
    [InlineData("signInHomeAssistant")]
    [InlineData("signOutHomeAssistant")]
    public void Signing_in_and_out_is_never_open_while_locked(string method)
    {
        Assert.DoesNotContain(method, CouchHud.Core.Guard.LockPolicy.OpenWhileLocked);
        Assert.DoesNotContain(method, CouchHud.Core.Integrations.CaseHud.CaseHudPolicy.OpenWhileLocked);
        Assert.DoesNotContain(method, CouchHud.Core.Integrations.Vpn.VpnPolicy.OpenWhileLocked);
        Assert.DoesNotContain(method, CouchHud.Core.Integrations.Tv.TvPolicy.OpenWhileLocked);
        // Not routed to one of the bridge's own handlers with their own lists either.
        Assert.False(method.StartsWith("tv", StringComparison.Ordinal) || method.StartsWith("vpn", StringComparison.Ordinal)
            || method.StartsWith("caseHud", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("homeAssistantUrl")]
    [InlineData("homeAssistantToken")]
    public void The_Home_Assistant_settings_are_not_harmless(string key) =>
        Assert.DoesNotContain(key, CouchHud.Core.Guard.LockPolicy.HarmlessSettings);
}
