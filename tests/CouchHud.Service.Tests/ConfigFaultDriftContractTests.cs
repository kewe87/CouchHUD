using System.Text.RegularExpressions;

namespace CouchHud.Service.Tests;

/// <summary>
/// Codes, lock mode, parental controls, shell and the service's state on the stand-in of broken settings (security re-review L-b), pinned
/// from the shell's source (the shell has no test project): the config refuses every change then, so a call that went to the service
/// first would leave service and config apart and report success. Each entry point refuses up front, before any permission is used up
/// and before any service call, with an error the page shows; the safe direction (automatic sign-in off) stays open; and a setup hold
/// from before the fault does not keep the couch open.
/// </summary>
public sealed class ConfigFaultDriftContractTests
{
    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CouchHud.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repository root not found");
    }

    private static string Shell(params string[] path) =>
        File.ReadAllText(Path.Combine([Root(), "src", "CouchHud.Shell", .. path])).Replace("\r\n", "\n");

    private static string Hud() => Shell("Hud.cs");

    /// <summary>The body of the member declared by <paramref name="signature"/>: from it to the next line closing at its indent.</summary>
    private static string Body(string text, string signature, string close = "\n    }\n")
    {
        var start = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} not found");
        var end = text.IndexOf(close, start, StringComparison.Ordinal);
        Assert.True(end > start, $"end of {signature} not found");
        return text[start..end];
    }

    public static TheoryData<string> EntryPoints() =>
    [
        "public async Task<object> SetPasscodeAsync(string[] sequence)",
        "public async Task<object> SetParentCodeAsync(string[] sequence)",
        "public async Task<object> SetCouchLockAsync(string mode, string[]? sequence = null)",
        "public async Task<object> SetParentalAsync(string key, System.Text.Json.JsonElement value)",
        "public object SetParental(string key, System.Text.Json.JsonElement value)",
        "public object SetParentalItemLock(string id, bool locked)",
        "public object ParentalAdoptZone()",
        "public bool SetShell(bool install)",
        "public object SetIntegrationMode(string mode)",
        "public async Task SetupServiceTaskAsync(bool policies = true)",
        "public object SetBlockTaskManager(bool on)",
        "public async Task<object> EnableAutoLogonAsync(string password, bool acceptNoLock)",
        "public object SetFeatureAreas(IReadOnlyList<string> enabled)",
        "public void SetPluginEnabled(string folder, bool enabled)",
        "public string InstallPackage(string zipPath, string kind)",
        "public object ApprovePlugin(string folder, string shownHash)",
        "public object AcceptRevokedPlugin(string folder)",
        "public object ConfirmPluginExe(string folder, string exe)",
        "public object AddCatalogSource(string url, string? publisherKey, string? name, bool acknowledgedTrust)",
        "public object RemoveCatalogSource(string url)",
        "public object UninstallPlugin(string folder)",
        "public async Task<object> InstallCatalogEntryAsync(string id, string kind)",
        "private ParentTicket? RequirePluginChange(PluginChange change, PluginOrigin origin, IReadOnlyCollection<string>? capabilities, string item, bool addsCapabilities = false)",
    ];

    [Theory]
    [InlineData("public async Task<object> ActivatePluginRowAsync(string pluginId, string pageId, string rowId)")]
    [InlineData("public async Task<object> ChangePluginRowAsync(string pluginId, string pageId, string rowId, string value)")]
    [InlineData("public Task<string?> RunQuickActionAsync(string pluginId, string actionId)")]
    public void A_plugin_row_that_needs_the_parents_code_is_refused_on_the_stand_in_before_the_grant_is_looked_at(string signature)
    {
        var body = Body(Hud(), signature);
        var refuse = body.IndexOf("RefuseWhileConfigFaulted();", StringComparison.Ordinal);
        var grant = body.IndexOf("CheckParentGrant(", StringComparison.Ordinal);
        Assert.True(refuse >= 0 && grant > refuse, "the refusal comes before the parents' grant");
    }

    /// <summary>
    /// A plugin row or quick action uses the parents' grant up BEFORE the plugin's code runs, on purpose (SECURITY.md, Plugins): the host
    /// cannot tell whether a plugin did what the parents allowed, and a grant left over could pay for a second press. Not "fixed" later: this
    /// pins the order, and that the refusals (locked, config fault, unknown value) all come before the grant is touched.
    /// </summary>
    [Theory]
    [InlineData("public async Task<object> ActivatePluginRowAsync(string pluginId, string pageId, string rowId)", "page.Activate(")]
    [InlineData("public async Task<object> ChangePluginRowAsync(string pluginId, string pageId, string rowId, string value)", "page.Change(")]
    [InlineData("public Task<string?> RunQuickActionAsync(string pluginId, string actionId)", "action.Run")]
    public void The_parents_grant_for_a_plugin_row_is_used_up_before_the_plugin_code_runs_on_purpose(string signature, string pluginCall)
    {
        var body = Body(Hud(), signature);
        var useUp = body.IndexOf("UseUp(CheckParentGrant(", StringComparison.Ordinal);
        var call = body.IndexOf(pluginCall, StringComparison.Ordinal);
        Assert.True(useUp >= 0 && call > useUp, "the grant is used up before the plugin's code runs");
        Assert.True(body.IndexOf("MayPress(", StringComparison.Ordinal) is var gate && (gate < 0 ? body.IndexOf("MayRun(", StringComparison.Ordinal) : gate) is var decided && decided >= 0 && decided < useUp, "the host's own checks come first");
        // Only one use per press, and none after the plugin ran.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(body, @"UseUp\("));
    }

    /// <summary>
    /// Catalogs of other developers: the one gate (CatalogSourceRules.Decide, tested in the core) comes first, the list is saved with
    /// UpdateOrThrow (a refused save reports nothing) and the parents' code is used up only after that; the bridge passes the "I trust this
    /// developer" confirmation only when the page sent an explicit true.
    /// </summary>
    [Theory]
    [InlineData("public object AddCatalogSource(string url, string? publisherKey, string? name, bool acknowledgedTrust)")]
    [InlineData("public object RemoveCatalogSource(string url)")]
    public void A_catalog_source_change_goes_through_the_one_gate_and_uses_the_grant_up_after_the_save(string signature)
    {
        var body = Body(Hud(), signature);
        var gate = body.IndexOf("RequireSourceChange(", StringComparison.Ordinal);
        var save = body.IndexOf("config.UpdateOrThrow(", StringComparison.Ordinal);
        var useUp = body.IndexOf("UseUp(", StringComparison.Ordinal);
        Assert.True(gate >= 0 && save > gate && useUp > save, "gate, then save, then use the grant up");
        Assert.DoesNotContain("config.Update(", body);
        var requirement = Body(Hud(), "private ParentTicket? RequireSourceChange(");
        Assert.Contains("CatalogSourceRules.Decide(", requirement);
        Assert.Contains("PluginCaller.Hud", requirement);
    }

    /// <summary>
    /// Adding or removing a catalog forgets the loaded list and its icons before the grant is used up (nothing is installed from, or shown of,
    /// the old list); a skin of such a catalog goes through the third-party gate; the icons of such an entry are fetched only with a statement
    /// that checks out, within a time and an entry limit.
    /// </summary>
    [Fact]
    public void A_catalog_change_forgets_the_old_list_and_extra_media_and_skins_are_held_to_the_third_party_rules()
    {
        foreach (var signature in new[] { "public object AddCatalogSource(string url, string? publisherKey, string? name, bool acknowledgedTrust)", "public object RemoveCatalogSource(string url)" })
        {
            var body = Body(Hud(), signature);
            Assert.True(body.IndexOf("Catalog.Invalidate()", StringComparison.Ordinal) is var forget and > 0 && forget < body.IndexOf("UseUp(", StringComparison.Ordinal), "forget before the grant is used up");
            Assert.True(body.IndexOf("config.UpdateOrThrow(", StringComparison.Ordinal) < body.IndexOf("Catalog.Invalidate()", StringComparison.Ordinal));
        }
        var manager = Shell("Services", "CatalogManager.cs");
        var invalidate = Body(manager, "public void Invalidate()");
        Assert.Contains("_last = null;", invalidate);
        Assert.Contains("_icons.Clear()", invalidate);
        var icons = Body(manager, "private async Task LoadIconsAsync(CatalogResult result, int generation)");
        Assert.Contains("e.Extra && (verified is null ||", icons);
        Assert.Contains("TimeSpan.FromSeconds(15)", icons);
        Assert.Contains(".Take(100)", icons);
        var install = Body(Hud(), "public async Task<object> InstallCatalogEntryAsync(string id, string kind)");
        Assert.Contains("kind == \"skin\" && await Catalog.IsExtraAsync(id, kind)", install);
        Assert.Contains("early: () => RequirePluginChange(PluginChange.Install, PluginOrigin.ThirdParty", install);
    }

    [Fact]
    public void The_bridge_passes_the_trust_confirmation_only_for_an_explicit_true_and_has_no_other_way_to_add_a_catalog()
    {
        var bridge = Shell("UiHost", "UiBridge.cs");
        var add = bridge[bridge.IndexOf("case \"addCatalogSource\":", StringComparison.Ordinal)..];
        add = add[..add.IndexOf("case \"removeCatalogSource\":", StringComparison.Ordinal)];
        Assert.Contains("Arg(3).ValueKind == JsonValueKind.True", add);
        // Neither the phone remote, MCP nor the assistant knows these calls.
        foreach (var name in new[] { "AddCatalogSource", "RemoveCatalogSource" })
        {
            var users = Directory.EnumerateFiles(Path.Combine(Root(), "src", "CouchHud.Shell"), "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
                .Where(f => File.ReadAllText(f).Contains("." + name + "(", StringComparison.Ordinal)).Select(Path.GetFileName).ToList();
            Assert.Equal(["UiBridge.cs"], users);
        }
    }

    [Fact]
    public void Switching_a_plugin_on_or_off_never_reports_what_was_not_saved()
    {
        var body = Body(Hud(), "public void SetPluginEnabled(string folder, bool enabled)");
        Assert.Contains("config.UpdateOrThrow(", body);
        Assert.DoesNotContain("config.Update(", body);
        // The grant is used up only after the save.
        Assert.True(body.IndexOf("UseUp(", StringComparison.Ordinal) > body.IndexOf("config.UpdateOrThrow(", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(EntryPoints))]
    public void The_entry_point_refuses_on_the_stand_in_before_anything_else(string signature)
    {
        var body = Body(Hud(), signature);
        // The first statement: before the permission (a parents' grant is not used up for nothing) and before any service call.
        var first = body.Split('\n').Skip(2).First(l => l.Trim().Length > 0).Trim();
        Assert.Equal("RefuseWhileConfigFaulted();", first);
    }

    [Fact]
    public void Automatic_sign_in_can_be_switched_off_on_the_stand_in_but_not_on()
    {
        // "off" is the safe direction and a step of the way back; "allow" (part of switching it on) is refused like the rest.
        var body = Body(Hud(), "public async Task<object> SetAutoLogonAsync(string mode)");
        var allow = body.IndexOf("if (mode == \"allow\")", StringComparison.Ordinal);
        var refuse = body.IndexOf("RefuseWhileConfigFaulted();", StringComparison.Ordinal);
        var turnOff = body.IndexOf("AutoLogon.TurnOffAsync(", StringComparison.Ordinal);
        Assert.True(allow >= 0 && refuse > allow && turnOff > refuse, "the refusal belongs to the allow branch only");
        Assert.Single(Regex.Matches(body, "RefuseWhileConfigFaulted"));
    }

    [Fact]
    public void The_refusal_is_an_error_the_page_shows()
    {
        var refuse = Body(Hud(), "private void RefuseWhileConfigFaulted()");
        var line = refuse.Split('\n').Single(l => l.Contains("config.Fault is not null", StringComparison.Ordinal));
        Assert.Contains("throw new InvalidOperationException(Strings.Get(\"config.notSavedFault\"))", line);
        Assert.DoesNotContain("LockedException", line);
    }

    [Fact]
    public void Every_service_write_of_codes_or_mode_sits_behind_the_refusal()
    {
        var hud = Hud();
        // Every public member that stores, removes or switches codes or the lock mode in the service. The confirmations of a lock of
        // 0.4.0 (at the unlock, which must stay possible) check the fault themselves, see below.
        foreach (Match call in Regex.Matches(hud, @"ServiceCodes\.(StoreAsync|SetModeAsync|RemoveAsync)\("))
        {
            var member = hud.LastIndexOf("\n    public ", call.Index, StringComparison.Ordinal);
            Assert.True(member >= 0);
            var signatureEnd = hud.IndexOf('\n', member + 1);
            var signature = hud[(member + 1)..signatureEnd].Trim();
            var body = Body(hud, signature);
            Assert.True(call.Index < member + body.Length, $"{call.Value} is not inside {signature}");
            Assert.Contains("RefuseWhileConfigFaulted();", body[..(call.Index - member)]);
        }
    }

    [Fact]
    public void The_legacy_lock_confirmations_change_nothing_in_the_service_on_the_stand_in()
    {
        var codes = Shell("Services", "ServiceCodes.cs");
        var confirmations = Regex.Matches(codes, @"public static async Task<\w+> (ConfirmLegacy\w*Async)\(([^)]*)\)");
        Assert.True(confirmations.Count >= 2, "the confirmations of a lock of 0.4.0 were not found");
        foreach (Match confirmation in confirmations)
        {
            // Each takes the config store and returns before the service is asked while the settings are unusable.
            Assert.StartsWith("ConfigStore config", confirmation.Groups[2].Value);
            var body = Body(codes, confirmation.Value);
            var fault = body.IndexOf("if (config.Fault is not null) return", StringComparison.Ordinal);
            var service = body.IndexOf("AdminService.Ops.", StringComparison.Ordinal);
            Assert.True(fault >= 0 && service > fault, $"{confirmation.Groups[1].Value}: the fault check must come before the service call");
        }
        // The HUD hands its config store over at every call.
        var hud = Hud();
        foreach (Match call in Regex.Matches(hud, @"ServiceCodes\.(ConfirmLegacy\w*Async)\(([^)]*)\)"))
            Assert.StartsWith("config", call.Groups[2].Value);
    }

    [Fact]
    public void The_shell_calls_the_service_s_setters_only_on_paths_checked_against_the_stand_in()
    {
        // Every direct call of a setter or remover of the service in the shell, by file. ServiceCodes: its public callers in Hud.cs refuse
        // first, its confirmations check the fault (above). AutoLogon: switched on only behind the refusal, off is the safe direction.
        // RemoteHost: the remote firewall, no code, mode or lock state. A new call fails here until it is looked at against the stand-in.
        var allowed = new Dictionary<string, string[]>
        {
            ["ServiceCodes.cs"] = ["SetLockModeAsync", "RemovePasscodeAsync"],
            ["AutoLogon.cs"] = ["SetAutoLogonAsync"],
            ["RemoteHost.cs"] = ["SetRemoteFirewallAsync", "RemoveLegacyFirewallRulesAsync", "RemoveRemoteFirewallAsync"],
        };
        var dir = Path.Combine(Root(), "src", "CouchHud.Shell");
        var seen = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            var sep = Path.DirectorySeparatorChar;
            if (file.Contains($"{sep}obj{sep}", StringComparison.Ordinal) || file.Contains($"{sep}bin{sep}", StringComparison.Ordinal)) continue;
            var name = Path.GetFileName(file);
            foreach (Match call in Regex.Matches(File.ReadAllText(file), @"\bOps\.((?:Set|Remove)\w*Async)\("))
            {
                seen++;
                Assert.True(allowed.TryGetValue(name, out var ok) && ok.Contains(call.Groups[1].Value),
                    $"{name}: Ops.{call.Groups[1].Value} is not on the list of service writes checked against the stand-in");
            }
        }
        Assert.True(seen > 0, "no service setter found: the pattern no longer fits the shell's calls");
    }

    [Fact]
    public void A_setup_hold_from_before_the_fault_does_not_keep_the_couch_open()
    {
        var unlocked = Hud().Split('\n').Single(l => l.Contains("private bool IsUnlocked =>", StringComparison.Ordinal));
        Assert.Contains("(config.Fault is null && MonoNow() < _setupHoldUntil)", unlocked);
    }

    [Fact]
    public void Choosing_a_service_to_pause_never_reports_what_was_not_saved()
    {
        var body = Body(Hud(), "public bool ToggleCouchService(string name)");
        Assert.Contains("config.UpdateOrThrow(", body);
        Assert.DoesNotMatch(new Regex(@"\bconfig\.Update\("), body);
    }
}
