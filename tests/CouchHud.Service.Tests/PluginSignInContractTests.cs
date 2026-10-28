namespace CouchHud.Service.Tests;

/// <summary>
/// The web sign-in of plugins (capability "browser.signin"), pinned from the shell's source: the shell has no test project. Only on the unlocked couch, a host that never resolves, one
/// use, the browser closes on every answer, nothing of the address in a log, and the secrets of plugins encrypted apart.
/// </summary>
public sealed class PluginSignInContractTests
{
    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CouchHud.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repository root not found");
    }

    private static string Text(params string[] path) => File.ReadAllText(Path.Combine([Root(), .. path])).Replace("\r\n", "\n");

    private static string Method(string text, string signature)
    {
        var start = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} not found");
        var end = text.IndexOf("\n    }\n", start, StringComparison.Ordinal);
        return text[start..end];
    }

    [Fact]
    public void Opening_needs_the_unlocked_couch_the_browser_area_and_an_invalid_host()
    {
        var hud = Text("src", "CouchHud.Shell", "HudPluginSignIn.cs");
        var open = Method(hud, "private bool OpenPluginSignIn(");
        // The checks come before anything is stored or opened.
        var firstCheck = open.IndexOf("if (!IsUnlocked || !Areas.On(FeatureAreas.Browser)) return false;", StringComparison.Ordinal);
        Assert.True(firstCheck >= 0);
        Assert.True(firstCheck < open.IndexOf("_pluginSignIn = state", StringComparison.Ordinal));
        Assert.Contains("PluginRedirectHostPattern().IsMatch(redirectHost ?? \"\")", open);
        Assert.Contains("invalid\\z", hud);
        Assert.DoesNotContain("HomeAssistant", open);
        // Another plugin's sign-in is not taken over.
        Assert.Contains("!running.PluginId.Equals(pluginId, StringComparison.OrdinalIgnoreCase)) return false;", open);
        // Opening the page is not a person's use of the browser.
        var code = string.Join('\n', hud.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal) && !l.TrimStart().StartsWith("///", StringComparison.Ordinal)));
        Assert.DoesNotContain("BrowserOpenedByPerson", code);
        Assert.DoesNotContain("_unlockedUntil", code);
    }

    [Fact]
    public void A_redirect_with_the_sign_ins_state_is_taken_once_closes_the_browser_and_is_handed_over_only_unlocked()
    {
        var hud = Text("src", "CouchHud.Shell", "HudPluginSignIn.cs");
        var redirect = Method(hud, "private void OnPluginSignInRedirect(");
        // A stray redirect (no flow, no state, another state) returns before anything is consumed or closed: the page stays.
        var none = redirect.IndexOf("if (state is null) return;", StringComparison.Ordinal);
        var stateCheck = redirect.IndexOf("CryptographicOperations.FixedTimeEquals(", StringComparison.Ordinal);
        var consume = redirect.IndexOf("_pluginSignIn = null;", StringComparison.Ordinal);
        var close = redirect.IndexOf("CloseBrowser(true)", StringComparison.Ordinal);
        var origin = redirect.IndexOf("PluginSignInRules.SameOrigin(pageUrl, state.Root)", StringComparison.Ordinal);
        var locked = redirect.IndexOf("if (Locked)", StringComparison.Ordinal);
        var receiver = redirect.IndexOf("state.Receiver(url, pageUrl)", StringComparison.Ordinal);
        Assert.True(none > 0 && stateCheck > none && consume > stateCheck && close > consume && origin > close && locked > origin && receiver > locked,
            "no flow, state, one use, close, origin, lock check, then the plugin");
        Assert.Contains("|| !CryptographicOperations.FixedTimeEquals", redirect);
        Assert.Contains("return;", redirect[stateCheck..consume]);
        // The state comes from the page's own address, exactly one, URL-safe; without it a sign-in does not even start.
        Assert.Contains("PluginSignInRules.ParsePage(pageUrl) is not { } checkedPage", hud);
        Assert.Contains("var pageState = checkedPage.State;", hud);
        Assert.DoesNotContain("NormalizeBase", hud);
        Assert.Contains("PluginSignInRules.StateOf(url)", hud);
        // The browser ending ends the sign-in and only then the redirect host is an ordinary address again.
        var main = Text("src", "CouchHud.Shell", "Hud.cs");
        var closeBrowser = main[main.IndexOf("private void CloseBrowser(bool bringFront)", StringComparison.Ordinal)..];
        Assert.Contains("EndPluginSignIn();", closeBrowser);
        Assert.Contains("ResetPluginRedirectHost();", closeBrowser);
        Assert.Contains("pane.PluginSignInRedirect += (url, pageUrl) => OnPluginSignInRedirect(pane, url, pageUrl);", main);
        Assert.Contains("StartPluginSignIn();", main);
    }

    [Fact]
    public void The_suggestion_is_asked_for_again_when_the_catalog_loads_and_when_the_couch_unlocks_and_the_host_still_decides()
    {
        var main = Text("src", "CouchHud.Shell", "Hud.cs");
        Assert.Contains("SignedEntriesChanged = () => Ui(() => Emit(\"pluginSuggestionsReady\", null))", main);
        var sync = main[main.IndexOf("private void SyncLockState()", StringComparison.Ordinal)..];
        sync = sync[..sync.IndexOf("plugins.Events.RaiseLockChanged(locked);", StringComparison.Ordinal)];
        Assert.Contains("else if (!first) Ui(() => Emit(\"pluginSuggestionsReady\", null));", sync);
        // The event carries no data; the answer still comes from the host, which gives nothing while locked.
        var get = main[main.IndexOf("public async Task<object> GetPluginSuggestionsAsync()", StringComparison.Ordinal)..];
        Assert.Contains("if (Locked) return new List<object>();", get[..300]);
    }

    [Fact]
    public void The_redirect_host_stays_cancelled_until_the_browser_closes_so_a_late_redirect_never_becomes_a_page()
    {
        var hud = Text("src", "CouchHud.Shell", "HudPluginSignIn.cs");
        // Ending a sign-in (answer, cancel, ran out, failed start) leaves the host registered in the pane.
        var end = Method(hud, "private void EndPluginSignIn(");
        Assert.DoesNotContain("PluginRedirectHost", end);
        // Only ResetPluginRedirectHost clears it, and that is called from CloseBrowser (and a failed open, which closes nothing yet).
        var code = string.Join('\n', hud.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal) && !l.TrimStart().StartsWith("///", StringComparison.Ordinal)));
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(code, @"PluginRedirectHost = null").Count);
        var reset = Method(hud, "private void ResetPluginRedirectHost(");
        Assert.Contains("PluginRedirectHost = null", reset);
        var main = Text("src", "CouchHud.Shell", "Hud.cs");
        var close = main[main.IndexOf("private void CloseBrowser(bool bringFront)", StringComparison.Ordinal)..];
        close = close[..close.IndexOf("\n    }\n", StringComparison.Ordinal)];
        Assert.Contains("ResetPluginRedirectHost();", close);
        // The pane cancels by host, whether or not a flow is running (the HUD only decides what to do with it).
        var pane = Text("src", "CouchHud.Shell", "BrowserPane.cs");
        Assert.Contains("_pluginRedirectHost is { } host && Uri.TryCreate(url, UriKind.Absolute, out var uri)", pane);
        Assert.DoesNotContain("_pluginFlow", pane);
    }

    [Fact]
    public void One_sign_in_at_a_time_and_a_plugin_is_named_when_it_opens_the_browser()
    {
        var hud = Text("src", "CouchHud.Shell", "HudPluginSignIn.cs");
        Assert.Contains("private volatile PluginSignInState? _pluginSignIn;", hud);
        Assert.Contains("lock (_pluginSignInGate)", hud);
        Assert.Contains("Emit(\"toast\", Strings.Get(\"plugin.signInOpened\", pluginName));", hud);
        // The locked and the wrong-page toast are generic plugin texts, not Home Assistant's.
        Assert.DoesNotContain("ha.signInLocked", hud);
        Assert.Contains("plugin.signInLocked", hud);
    }

    [Fact]
    public void The_guide_menu_hides_entries_a_locked_couch_may_not_run_and_the_bridge_leaves_the_decision_to_the_host()
    {
        var main = Text("src", "CouchHud.Shell", "Hud.cs");
        Assert.Contains("plugins.QuickActions.Where(a => PluginPolicy.Shown(Locked, plugins.OriginOf(a.Plugin), a.Action, plugins.LevelOf(a.Plugin, a.Action.Function), ChildNow)).ToList();", main);
        Assert.Contains("ToolCatalog.AllowedWhileLocked(name) || plugins.ToolSpecs.Any(t => t.Name == name && t.AllowedWhileLocked);", main);
        Assert.Contains("hud.ToolAllowedWhileLocked(name)", Text("src", "CouchHud.Shell", "Services", "HudTools.cs"));
        // The decision for a locked run is MayRun (first-party and flagged only); the bridge lets the call through to it.
        var run = main[main.IndexOf("public Task<string?> RunQuickActionAsync(", StringComparison.Ordinal)..];
        run = run[..run.IndexOf("\n    }\n", StringComparison.Ordinal)];
        Assert.Contains("PluginPolicy.MayRun(Locked, plugins.OriginOf(pluginId), action, config.Current.ParentalEnabled, actionLevel, ChildNow)", run);
        Assert.Contains("\"runQuickAction\"", Text("src", "CouchHud.Core", "Guard", "LockPolicy.cs"));
    }

    [Fact]
    public void Nothing_of_the_redirect_address_or_the_code_reaches_a_log()
    {
        var hud = Text("src", "CouchHud.Shell", "HudPluginSignIn.cs");
        foreach (var line in hud.Split('\n').Where(l => l.Contains("Console.", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("{url", line);
            Assert.DoesNotContain("{pageUrl", line);
            Assert.DoesNotContain("{code", line);
            Assert.DoesNotContain("Token", line);
        }
    }

    [Fact]
    public void The_browser_cancels_a_plugins_redirect_before_any_request_in_frames_too()
    {
        var pane = Text("src", "CouchHud.Shell", "BrowserPane.cs");
        var nav = pane[pane.IndexOf("core.NavigationStarting += (_, e) =>", StringComparison.Ordinal)..];
        nav = nav[..nav.IndexOf("\n        };", StringComparison.Ordinal)];
        var check = nav.IndexOf("if (IsPluginRedirect(e.Uri))", StringComparison.Ordinal);
        Assert.True(check > 0);
        // Before the error page, the full-screen handling and every other use of the address.
        Assert.True(check < nav.IndexOf("_errorPagePending", StringComparison.Ordinal));
        Assert.Contains("e.Cancel = true;", nav[check..(check + 200)]);
        Assert.Contains("_cancelled.Add(e.NavigationId);", nav[check..(check + 200)]);
        var frames = pane[pane.IndexOf("core.FrameNavigationStarting += (_, e) =>", StringComparison.Ordinal)..];
        frames = frames[..frames.IndexOf("\n        };", StringComparison.Ordinal)];
        Assert.Contains("if (IsPluginRedirect(e.Uri)) e.Cancel = true;", frames);
        Assert.DoesNotContain("PluginSignInRedirect", frames);
        // Only http(s) to exactly the host the sign-in named.
        Assert.Contains("uri.Scheme is \"http\" or \"https\" && string.Equals(uri.IdnHost.TrimEnd('.'), host, StringComparison.OrdinalIgnoreCase);", pane);
    }

    [Fact]
    public void Plugin_secrets_are_encrypted_for_the_plugin_apart_from_the_programs_own_and_the_seed_never_carries_plain_text()
    {
        var app = Text("src", "CouchHud.Shell", "App.xaml.cs");
        Assert.Contains("ProtectSecret = (plugin, plain) => Secrets.Protect(plain, plugin),", app);
        Assert.Contains("UnprotectSecret = (plugin, stored) => Secrets.Unprotect(stored, plugin),", app);
        Assert.Contains("PluginSettingSeeds.For(config.Current, id, (plugin, stored) => Secrets.Protect(Secrets.Unprotect(stored), plugin))", app);
        var secrets = Text("src", "CouchHud.Shell", "Services", "Secrets.cs");
        Assert.Contains("\"CouchHud:\" + scope.ToLowerInvariant()", secrets);
        // The program's own secrets keep their entropy, so nothing already stored becomes unreadable.
        Assert.Contains("public static string Protect(string plain) => Protect(plain, null);", secrets);
        Assert.Contains("scope is null ? Entropy", secrets);
    }
}
