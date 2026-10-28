namespace CouchHud.Service.Tests;

/// <summary>
/// "Sign in with Home Assistant", pinned from the shell's source (the shell has no test project, as in BrowserUnlockContractTests): the
/// couch browser cancels the redirect before anything else sees it, frames never load it, the tokens go through Home Assistant's own
/// HttpClient (no redirects, the system's TLS check), opening the sign-in page is not a person's use of the browser, and the bridge
/// keeps both calls behind the couch lock.
/// </summary>
public sealed class HomeAssistantSignInContractTests
{
    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CouchHud.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repository root not found");
    }

    private static string Text(params string[] path) => File.ReadAllText(Path.Combine([Root(), .. path])).Replace("\r\n", "\n");

    /// <summary>The body of the handler attached with <paramref name="hook"/> (up to the closing "        };").</summary>
    private static string Handler(string text, string hook)
    {
        var start = text.IndexOf(hook, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{hook} not found");
        var end = text.IndexOf("\n        };", start, StringComparison.Ordinal);
        return text[start..end];
    }

    [Fact]
    public void The_browser_cancels_the_redirect_first_and_in_frames_too()
    {
        var pane = Text("src", "CouchHud.Shell", "BrowserPane.cs");
        var top = Handler(pane, "core.NavigationStarting += (_, e) =>");
        var lines = top.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal)).ToList();
        // First statement of the handler: the check, then cancel, before the address is noted anywhere (_lastUrl, error page …).
        Assert.Equal("if (HomeAssistantLogin.IsRedirect(e.Uri))", lines[2]);
        Assert.Equal("e.Cancel = true;", lines[4]);
        var frames = Handler(pane, "core.FrameNavigationStarting += (_, e) =>");
        Assert.Contains("HomeAssistantLogin.IsRedirect(e.Uri)) e.Cancel = true;", frames);
        Assert.DoesNotContain("SignInRedirect", frames);
        // Defense in depth: every request to the redirect host that got past the checks is answered in the browser with 403.
        Assert.Contains("BlockRequests(core, \"*://\" + HomeAssistantLogin.RedirectHost + \"/*\");", pane);
        // Workers too, where the runtime allows it.
        Assert.Contains("core.AddWebResourceRequestedFilter(pattern, CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);", pane);
        var requests = Handler(pane, "core.WebResourceRequested += (_, e) =>");
        Assert.Contains("if (HomeAssistantLogin.IsRedirect(e.Request.Uri))", requests);
        Assert.Contains("e.Response = core.Environment.CreateWebResourceResponse(null, 403, \"Forbidden\", \"\");", requests);
    }

    [Fact]
    public void A_sign_in_to_another_server_drops_the_old_choice_and_pins_and_a_failed_start_its_page()
    {
        var hud = Text("src", "CouchHud.Shell", "HudHomeAssistant.cs");
        var commit = hud[hud.IndexOf("private void CommitHaSession", StringComparison.Ordinal)..];
        commit = commit[..commit.IndexOf("\n    }\n", StringComparison.Ordinal)];
        var moved = commit[commit.IndexOf("if (moved)\n", StringComparison.Ordinal)..];
        moved = moved[..moved.IndexOf("            }", StringComparison.Ordinal)];
        Assert.Contains("c.HomeAssistantEntities = [];", moved);
        Assert.Contains("c.HomeAssistantPinned = [];", moved);
        var signIn = hud[hud.IndexOf("public async Task<object> SignInHomeAssistantAsync", StringComparison.Ordinal)..];
        var failed = signIn[signIn.IndexOf("catch\n", StringComparison.Ordinal)..];
        failed = failed[..failed.IndexOf("throw;", StringComparison.Ordinal)];
        Assert.Contains("_haSignInPage = null;", failed);
    }

    [Fact]
    public void Every_answer_of_the_sign_in_closes_its_page()
    {
        var hud = Text("src", "CouchHud.Shell", "HudHomeAssistant.cs");
        var redirect = hud[hud.IndexOf("private void OnHaSignInRedirect", StringComparison.Ordinal)..];
        redirect = redirect[..redirect.IndexOf("\n    }\n", StringComparison.Ordinal)];
        // Only a callback that is not this sign-in's leaves the page; everything else (code, refusal, wrong page, too late) closes it
        // before anything else happens, so the address with the state leaves the bar and the back list.
        var ignore = redirect.IndexOf("HaCallbackOutcome.NoFlow or HaCallbackOutcome.WrongState or HaCallbackOutcome.Malformed) return;", StringComparison.Ordinal);
        var close = redirect.IndexOf("if (_browser == pane) CloseBrowser(true);", StringComparison.Ordinal);
        Assert.True(ignore > 0 && close > ignore);
        Assert.True(close < redirect.IndexOf("HaCallbackOutcome.Expired", StringComparison.Ordinal));
        // The bar and the recent sites go by the sign-in page, which ends only with the browser (not with the sign-in's expiry).
        var main = Text("src", "CouchHud.Shell", "Hud.cs");
        Assert.Contains("signIn = _haSignInPage is not null,", main);
        Assert.Contains("return _haSignInPage is { } root && HomeAssistantLogin.SameOrigin(url, root) ? HomeAssistantLogin.Shown(url) : url;", main);
        Assert.Contains("_haSignInPage = null;", main[main.IndexOf("private void CloseBrowser(bool bringFront)", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void Tokens_go_only_through_Home_Assistants_own_client()
    {
        var hud = Text("src", "CouchHud.Shell", "HudHomeAssistant.cs");
        Assert.Contains("private static readonly HttpClient HaHttp = HomeAssistantClient.CreateHttpClient();", hud);
        Assert.Contains("private static readonly HomeAssistantAuth HaAuth = new(HaHttp);", hud);
        Assert.Contains("new(HaHttp)", Text("src", "CouchHud.Shell", "Hud.cs"));
        foreach (var file in new[] { hud, Text("src", "CouchHud.Core", "Integrations", "HomeAssistant.cs"), Text("src", "CouchHud.Core", "Integrations", "HomeAssistantAuth.cs") })
        {
            Assert.DoesNotContain("ServerCertificateCustomValidationCallback", file);
            Assert.DoesNotContain("AllowAutoRedirect = true", file);
        }
        // Nothing of the code or a token in the log: the redirect address, the code and the tokens are never formatted into a line.
        foreach (var line in hud.Split('\n').Where(l => l.Contains("Console.", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("{url", line);
            Assert.DoesNotContain("{code", line);
            Assert.DoesNotContain("{pageUrl", line);
            Assert.DoesNotContain("Token", line);
        }
    }

    [Fact]
    public void Opening_the_sign_in_page_does_not_count_as_a_person_and_needs_the_unlock()
    {
        var hud = Text("src", "CouchHud.Shell", "HudHomeAssistant.cs");
        var code = string.Join('\n', hud.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        Assert.DoesNotContain("BrowserOpenedByPerson", code);
        Assert.DoesNotContain("_browserInput", code);
        Assert.DoesNotContain("_unlockedUntil", code);
        var signIn = hud[hud.IndexOf("public async Task<object> SignInHomeAssistantAsync", StringComparison.Ordinal)..];
        Assert.StartsWith("public async Task<object> SignInHomeAssistantAsync(string? url)\n    {\n        RequireUnlocked();", signIn);
        var signOut = hud[hud.IndexOf("public async Task<object> SignOutHomeAssistantAsync", StringComparison.Ordinal)..];
        Assert.StartsWith("public async Task<object> SignOutHomeAssistantAsync()\n    {\n        RequireUnlocked();", signOut);
        // The redirect is taken only on an unlocked couch, and checked again after the exchange.
        Assert.Contains("_ when Locked => Strings.Get(\"ha.signInLocked\")", hud);
        Assert.Contains("HaSignIn.FinishAsync(callback, () => Locked ? Strings.Get(\"ha.signInLocked\") : null, CommitHaSession)", hud);
        // The sign-in starts with the connection's version; every change of the connection bumps it (a late result is revoked).
        Assert.Contains("_haLogin.Start(root, HaSignIn.Generation)", hud);
        foreach (var change in new[] { "SignOutHomeAssistantAsync()", "void SetHomeAssistantUrl(", "void SetHomeAssistantToken(" })
        {
            var body = hud[hud.IndexOf(change, StringComparison.Ordinal)..];
            body = body[..body.IndexOf("\n    }\n", StringComparison.Ordinal)];
            Assert.Contains("HaSignIn.Changed();", body);
        }
        // Neither call is open on a locked couch.
        var policy = Text("src", "CouchHud.Core", "Guard", "LockPolicy.cs");
        Assert.DoesNotContain("HomeAssistant\"", policy.Replace("testHomeAssistant\"", ""));
        Assert.DoesNotContain("homeAssistantToken", policy);
        Assert.DoesNotContain("homeAssistantUrl", policy);
    }
}
