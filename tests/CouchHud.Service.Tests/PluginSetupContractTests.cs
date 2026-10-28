namespace CouchHud.Service.Tests;

/// <summary>
/// The host services that plugins use (the setup hand-over, the restriction of a window on the locked couch), and the removal of the core's own
/// Home Assistant, CaseHUD and VPN integrations, pinned from the shell's source (the shell has no test project): the files are gone, the bridge
/// knows none of their calls, the lock lists name none of them, the no-UAC CaseHUD update through the service is gone for good, and nothing
/// of the old integrations is written to the settings any more (only the legacy fields the one-time seed reads are left).
/// </summary>
public sealed class PluginSetupContractTests
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
    public void A_plugins_restriction_of_a_window_on_the_locked_couch_is_a_restriction_and_never_an_allowance()
    {
        var guard = Text("src", "CouchHud.Shell", "Services", "ForegroundGuard.cs");
        Assert.Contains("PluginRestricted = () => PluginRestrictions?.Invoke(pid) == true", guard);
        Assert.DoesNotContain("CaseHud", guard);
        var locked = Text("src", "CouchHud.Core", "Guard", "LockedForeground.cs");
        // The generic rule minimizes (a deny) and sits before the "unreadable, so allow" rule: an elevated restricted window is still minimized.
        var rule = locked.IndexOf("if (w.PluginRestricted()) return new(ForegroundAction.Minimize", StringComparison.Ordinal);
        var unreadable = locked.IndexOf("if (w.Unreadable) return new(ForegroundAction.Allow", StringComparison.Ordinal);
        Assert.True(rule > 0 && unreadable > rule);
        // The plugin names the verified exe; the host takes it only below Program Files (PluginHostServices.Canonical) and only for first-party plugins.
        Assert.Contains("PluginCapabilities.LockRestrict", Text("src", "CouchHud.Core", "Plugins", "PluginPolicy.cs"));
    }

    [Fact]
    public void The_host_hands_a_setup_to_Windows_only_after_its_own_checks()
    {
        var main = Text("src", "CouchHud.Shell", "Hud.cs");
        Assert.Contains("plugins.SetupRunner = RunPluginSetupAsync;", main);
        var setup = Text("src", "CouchHud.Shell", "HudPluginSetup.cs");
        var run = setup[setup.IndexOf("public async Task<bool> RunPluginSetupAsync(", StringComparison.Ordinal)..];
        // The order matters: the unlock and the child check first, then the rules, then the held file, its hash, its real path, and only then the start.
        var order = new[]
        {
            "RefuseWhileConfigFaulted();", "if (Locked) throw new LockedException", "RequireUnlocked();", "ProfileSwitch.IsChild(", "_setupPress.TryConsume(pluginId)", "_setupBusy.WaitAsync(0", "PluginSetupRules.Refuse(",
            "FileShare.Read", "PluginSetupRules.HashMatches(file, sha256)", "PathSafety.RequireSamePath(", "PathSafety.RequireNoReparsePoints(path)", "Process.Start("
        };
        var last = -1;
        foreach (var step in order)
        {
            var at = run.IndexOf(step, StringComparison.Ordinal);
            Assert.True(at > last, $"{step} is missing or out of order");
            last = at;
        }
        Assert.Contains("UseShellExecute = true, Verb = \"runas\"", run);
        Assert.Contains("WorkingDirectory = Environment.SystemDirectory", run);
        // The root is the caller's own folder, and the setup must be alone in it.
        Assert.Contains("Path.Combine(dataDir, \"plugin-data\", pluginId)", run);
        Assert.Contains("PluginSetupRules.FolderHoldsOnlyTheSetup(path)", run);
        // The press: only a row or entry of CouchHUD's own plugin that asks for the parents' code sets the token, after its gates.
        var hud = Text("src", "CouchHud.Shell", "Hud.cs");
        Assert.Contains("var pressed = row!.RequiresParentGrant && rowLevel != PluginLevel.Restricted && PressPaysForSetup(pluginId);", hud);
        Assert.Contains("if (!(action.RequiresParentGrant && actionLevel != PluginLevel.Restricted && PressPaysForSetup(pluginId)))", hud);
        Assert.True(hud.IndexOf("_setupPress.Issue(pluginId)", StringComparison.Ordinal) > hud.IndexOf("PluginPolicy.MayPress(locked, plugins.OriginOf(pluginId), row", StringComparison.Ordinal));
        // Only a first-party plugin that holds setup.run gets a token, and it is gone when the plugin's call returns (in a finally).
        Assert.Contains("grant.Capabilities.Contains(CouchHud.Sdk.PluginCapabilities.SetupRun)", setup);
        Assert.Contains("if (pressed) _setupPress.Clear(pluginId);", hud);
        Assert.Contains("_setupPress.Clear(pluginId);", Method(setup, "private async Task<string?> RunPressedAsync("));
        // The folder is looked at again with the file open and hashed, right before the start.
        var again = run.LastIndexOf("PluginSetupRules.FolderHoldsOnlyTheSetup(path)", StringComparison.Ordinal);
        Assert.True(again > run.IndexOf("PluginSetupRules.HashMatches(file, sha256)", StringComparison.Ordinal) && again < run.IndexOf("Process.Start(", StringComparison.Ordinal));
        Assert.Contains("public Func<string, string, string, string, CancellationToken, Task<bool>>? SetupRunner", Text("src", "CouchHud.Core", "Plugins", "PluginManager.cs"));
        // The file stays open (nobody can write to it) until the setup ended.
        Assert.True(run.IndexOf("using (file)", StringComparison.Ordinal) < run.IndexOf("Process.Start(", StringComparison.Ordinal));
        Assert.Contains("PluginSetupRules.MaxRun", run);
        // Nothing but the plugin's name goes to a log: no path, no hash.
        foreach (var line in run.Split('\n').Where(l => l.Contains("Console.", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("{path}", line);
            Assert.DoesNotContain("{sha256}", line);
        }
    }

    [Fact]
    public void The_core_has_no_Home_Assistant_CaseHUD_or_VPN_integration_left_only_the_seeds_and_the_suggestions()
    {
        var root = Root();
        foreach (var gone in new[]
                 {
                     "src/CouchHud.Core/Integrations/CaseHud", "src/CouchHud.Core/Integrations/Vpn", "src/CouchHud.Core/Integrations/HomeAssistant.cs",
                     "src/CouchHud.Core/Integrations/HomeAssistantAuth.cs", "src/CouchHud.Core/Integrations/HomeAssistantLogin.cs", "src/CouchHud.Core/Integrations/HomeAssistantPolicy.cs",
                     "src/CouchHud.Core/Integrations/HomeAssistantSelection.cs", "src/CouchHud.Core/Integrations/HomeAssistantSignIn.cs", "src/CouchHud.Core/Integrations/HomeAssistantWebSocket.cs",
                     "src/CouchHud.Shell/HudHomeAssistant.cs", "src/CouchHud.Shell/Services/CaseHudManager.cs", "src/CouchHud.Shell/Services/VpnManager.cs",
                     "ui/views/casehud.js", "ui/views/casehud-lighting.js", "ui/views/casehud-settle.js", "ui/core/casehud-rules.js", "ui/views/vpn.js", "ui/mock-casehud.js", "ui/mock-vpn.js"
                 })
        {
            var path = Path.Combine(root, gone.Replace('/', Path.DirectorySeparatorChar));
            Assert.False(File.Exists(path) || Directory.Exists(path), gone);
        }
        var main = Text("src", "CouchHud.Shell", "Hud.cs");
        var bridge = Text("src", "CouchHud.Shell", "UiHost", "UiBridge.cs");
        var lockPolicy = Text("src", "CouchHud.Core", "Guard", "LockPolicy.cs");
        var areas = Text("src", "CouchHud.Core", "Config", "FeatureAreas.cs");
        var tools = Text("src", "CouchHud.Core", "Assistant", "Tools.cs");
        foreach (var (name, text) in new[] { ("Hud.cs", main), ("UiBridge.cs", bridge), ("LockPolicy.cs", lockPolicy), ("FeatureAreas.cs", areas), ("Tools.cs", tools) })
        {
            foreach (var word in new[] { "HomeEntit", "HomeAssistantClient", "RequireCoreHomeAssistant", "CoreHomeAssistantMoved", "home_entit", "caseHud", "CaseHudManager", "CoreCaseHud", "EmitCaseHudState", "vpnConnect", "vpnStatus", "VpnManager", "CoreVpn", "CoreFeatureMoved", "PLUGIN-REMOVE" })
                Assert.False(text.Contains(word, StringComparison.Ordinal), $"{name} still mentions {word}");
        }
        // No settings key and no bridge call of the old integrations is accepted any more (an unknown key or call is refused).
        foreach (var key in new[] { "homeAssistantUrl", "homeAssistantToken", "caseHudGamingLease", "caseHudInstallFps", "vpnInGuide" })
            Assert.DoesNotContain($"\"{key}\"", areas);
        Assert.Contains("throw new InvalidOperationException($\"unknown method {method}\")", bridge);
    }

    [Fact]
    public void The_no_UAC_CaseHUD_update_through_the_service_is_gone_from_every_layer()
    {
        foreach (var (file, words) in new[]
                 {
                     (Path.Combine("src", "CouchHud.Service.Contracts", "Operation.cs"), new[] { "InstallCaseHud" }),
                     (Path.Combine("src", "CouchHud.Service.Contracts", "RequestValidator.cs"), ["InstallCaseHud", "CaseHud"]),
                     (Path.Combine("src", "CouchHud.Service.Contracts", "Dtos.cs"), ["RestartCaseHud"]),
                     (Path.Combine("src", "CouchHud.Service", "Authorization", "OperationPolicy.cs"), ["InstallCaseHud", "CaseHudFirstInstall"]),
                     (Path.Combine("src", "CouchHud.Service", "Authorization", "RateLimiter.cs"), ["InstallCaseHud"]),
                     (Path.Combine("src", "CouchHud.Service", "Dispatch", "HandlersB.cs"), ["InstallCaseHud", "CaseHud"]),
                     (Path.Combine("src", "CouchHud.Service", "Jobs", "JobStore.cs"), ["CaseHud"]),
                     (Path.Combine("src", "CouchHud.Service", "Setup", "SetupPlatform.cs"), ["CaseHud"]),
                     (Path.Combine("src", "CouchHud.Service", "Setup", "SignedSetupPipeline.cs"), ["CaseHud"]),
                     (Path.Combine("src", "CouchHud.Service", "Setup", "SignedSetupVerifier.cs"), ["CaseHud"]),
                     (Path.Combine("src", "CouchHud.ServiceClient", "PrivilegedOps.cs"), ["InstallCaseHud"])
                 })
        {
            var text = Text(file.Split(Path.DirectorySeparatorChar));
            foreach (var word in words) Assert.False(text.Contains(word, StringComparison.Ordinal), $"{file} still mentions {word}");
        }
        // The removal is said plainly in the security notes and in the changelog.
        var security = Text("SECURITY.md");
        var changelog = Text("CHANGELOG.md");
        Assert.Contains("no-UAC", security);
        Assert.DoesNotContain("CASEHUD-PLUGIN-REMOVE", security);
        var section = changelog[changelog.IndexOf("## 0.6.0", StringComparison.Ordinal)..];
        Assert.Contains("no-UAC", section);
        Assert.DoesNotContain("PLUGIN-REMOVE", section);
    }

    [Fact]
    public void The_legacy_settings_are_read_for_the_seeds_and_never_written_by_the_program_any_more()
    {
        var main = Text("src", "CouchHud.Shell", "Hud.cs");
        foreach (var field in new[]
                 {
                     "HomeAssistantUrl", "HomeAssistantToken", "HomeAssistantRefreshToken", "HomeAssistantPinned", "HomeAssistantEntities",
                     "CaseHudGamingLease", "VpnInGuide", "VpnLastCountry"
                 })
        {
            Assert.DoesNotContain($".{field} =", main);
            Assert.DoesNotContain($"c.{field}", main);
        }
        // The seeds read them (that is all that is left of them).
        var seeds = Text("src", "CouchHud.Core", "Plugins", "PluginSuggestions.cs");
        foreach (var field in new[] { "HomeAssistantUrl", "HomeAssistantToken", "HomeAssistantRefreshToken", "HomeAssistantPinned", "HomeAssistantEntities", "CaseHudGamingLease", "VpnInGuide", "VpnLastCountry" })
            Assert.Contains($"config.{field}", seeds);
    }
}
