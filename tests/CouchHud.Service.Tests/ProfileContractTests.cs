using System.Text.RegularExpressions;

namespace CouchHud.Service.Tests;

/// <summary>
/// Profiles part 2, pinned from the shell's source (as in BrowserUnlockContractTests and BrowserRecentContractTests): only the HUD's own
/// page reaches the profile calls (not the phone remote, MCP, the assistant or a plugin), the phone's keys do not count on a profile
/// screen, every change needs the parents' code, and a switch rebuilds the parental controller and drops every release of the old profile.
/// </summary>
public sealed class ProfileContractTests
{
    private static readonly string[] Calls =
    [
        "getProfiles", "switchProfile", "profileScreen", "profileIdle", "addProfile", "renameProfile", "setProfileLook",
        "setProfileRestricted", "setProfileCode", "removeProfile", "getProfileRules", "setProfileRule"
    ];

    private static readonly string[] HostMethods =
    [
        "SwitchProfile", "AddProfile", "RenameProfile", "SetProfileLook", "SetProfileRestricted", "SetProfileCode", "RemoveProfile",
        "SetProfileRule", "SetProfileScreen", "ActivateProfile", "SwitchActiveProfile"
    ];

    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CouchHud.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repository root not found");
    }

    private static string[] Lines(string file) => File.ReadAllText(file).Replace("\r\n", "\n").Split('\n');

    private static bool IsComment(string line) => line.TrimStart().StartsWith("//", StringComparison.Ordinal) || line.TrimStart().StartsWith("///", StringComparison.Ordinal);

    private static string Code(params string[] path) => string.Join('\n', Lines(Path.Combine([Root(), .. path])).Where(l => !IsComment(l)));

    private static IEnumerable<(string Rel, string Code)> Sources(params string[] path)
    {
        var sep = Path.DirectorySeparatorChar;
        var root = Path.Combine([Root(), .. path]);
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal))
            .Select(f => (Path.GetRelativePath(Path.Combine(Root(), "src"), f), string.Join('\n', Lines(f).Where(l => !IsComment(l)))));
    }

    /// <summary>The body of a 4-space-indented member, without comment lines.</summary>
    private static string Body(string file, string declaration)
    {
        var lines = Lines(Path.Combine(Root(), "src", "CouchHud.Shell", file));
        var start = Array.FindIndex(lines, l => l.StartsWith(declaration, StringComparison.Ordinal));
        Assert.True(start >= 0, $"not found: {declaration}");
        var end = Array.FindIndex(lines, start + 1, l => l == "    }" || l.StartsWith("    }", StringComparison.Ordinal) && l.Trim() == "});");
        return string.Join('\n', lines[start..end].Where(l => !IsComment(l)));
    }

    [Fact]
    public void Only_the_ui_bridge_names_the_profile_calls()
    {
        foreach (var (rel, code) in Sources("src"))
        {
            if (rel is "CouchHud.Shell\\UiHost\\UiBridge.cs" or "CouchHud.Shell/UiHost/UiBridge.cs") continue;
            if (rel.EndsWith("ProfileSwitch.cs", StringComparison.Ordinal)) continue;
            // FeatureAreas.cs only lists every bridge call under its area (or "always"); it calls none of them.
            if (rel.EndsWith("FeatureAreas.cs", StringComparison.Ordinal)) continue;
            foreach (var call in Calls)
                Assert.False(code.Contains($"\"{call}\"", StringComparison.Ordinal), $"{rel} names the bridge call {call}");
        }
        var bridge = Code("src", "CouchHud.Shell", "UiHost", "UiBridge.cs");
        foreach (var call in Calls) Assert.Contains($"case \"{call}\":", bridge);
    }

    /// <summary>The host's profile operations, called on an object: only the bridge calls them (review N7, searched in every file of src/).</summary>
    private static readonly string[] Operations =
    [
        "SwitchProfile", "AddProfile", "RenameProfile", "SetProfileLook", "SetProfileRestricted", "SetProfileCode", "RemoveProfile",
        "SetProfileRule", "GetProfileRules", "GetProfiles", "SetProfileScreen", "ProfileIdle"
    ];

    [Fact]
    public void The_profile_operations_are_called_only_from_the_ui_bridge()
    {
        var call = new Regex($@"(?<recv>\w+)\s*\.\s*(?<op>{string.Join('|', Operations)})\s*\(");
        foreach (var (rel, code) in Sources("src"))
        {
            var file = Path.GetFileName(rel);
            if (file == "UiBridge.cs") continue;
            foreach (Match m in call.Matches(code))
            {
                // The configuration's own methods of the same name, inside the host's checked operations (config.UpdateOrThrow(c => c.AddProfile(…))).
                if (file is "HudProfiles.cs" or "AppConfig.cs" && m.Groups["recv"].Value == "c") continue;
                Assert.Fail($"{rel} calls {m.Groups["recv"].Value}.{m.Groups["op"].Value}(…)");
            }
        }
        var bridge = Code("src", "CouchHud.Shell", "UiHost", "UiBridge.cs");
        foreach (var op in Operations) Assert.Matches($@"hud\.{op}\(", bridge);
    }

    [Fact]
    public void A_protected_profile_waiting_for_its_code_gets_only_the_pickers_list()
    {
        // Before the couch lock is looked at (review M1).
        var bridge = Code("src", "CouchHud.Shell", "UiHost", "UiBridge.cs");
        var heldAt = bridge.IndexOf("!ProfilePolicy.OpenWhileHeld.Contains(method) && hud.ProfileHeld", StringComparison.Ordinal);
        var locked = bridge.IndexOf("if (hud.Locked && !OpenWhileLocked.Contains(method)", StringComparison.Ordinal);
        var prefixes = bridge.IndexOf("method.StartsWith(\"tv\"", StringComparison.Ordinal);
        Assert.True(heldAt >= 0 && locked > heldAt && prefixes > heldAt, "the held check comes first");
        Assert.Contains("throw new ProfileHeldException(", bridge);
        var policy = Code("src", "CouchHud.Core", "Config", "ProfileSwitch.cs");
        var list = Regex.Match(policy, @"OpenWhileHeld = new HashSet<string>\(OpenWhileLocked\)\s*\{(?<list>[^}]*)\}").Groups["list"].Value;
        var held = Regex.Matches(list, "\"(\\w+)\"").Select(m => m.Groups[1].Value).ToHashSet();
        Assert.Contains("parentalAuth", held);
        foreach (var refused in new[] { "launch", "resumeGame", "getLibrary", "getCollections", "getSettings", "setSetting", "openUrl", "askAssistant", "enterPcMode", "unlock", "getBrowser", "browserCommand", "toggleFavorite", "setParental", "parentalExtend" })
            Assert.DoesNotContain(refused, held);
        Assert.DoesNotContain(held, m => m.StartsWith("caseHud", StringComparison.Ordinal) || m.StartsWith("vpn", StringComparison.Ordinal) || m.StartsWith("tv", StringComparison.Ordinal));
        // Starting, resuming, the assistant and MCP refuse too.
        Assert.Contains("RequireProfileChosen();", Body("Hud.cs", "    public void ResumeGame()"));
        var tools = Code("src", "CouchHud.Shell", "Services", "HudTools.cs");
        var guard = tools.IndexOf("hud.RequireProfileChosen();", StringComparison.Ordinal);
        Assert.True(guard >= 0 && guard < tools.IndexOf("object? result = name switch", StringComparison.Ordinal), "the tools refuse before anything runs");
        Assert.Contains("ProfileHeldException", Body("HudProfiles.cs", "    public void RequireProfileChosen()"));
    }

    [Fact]
    public void A_childs_code_is_replaced_only_by_the_parents_code_never_by_the_unlocked_couch()
    {
        // Review M2.
        var body = Body("HudProfiles.cs", "    public object SwitchProfile(");
        var start = body.IndexOf("case SwitchNeed.ProfileCode:", StringComparison.Ordinal);
        Assert.True(start >= 0);
        var branch = body[start..];
        Assert.Contains("if (HasParentCode && _grant.TryConsume(ProfileSwitch.SwitchAction, id)) break;", branch);
        Assert.DoesNotContain("IsUnlocked", branch);
        Assert.DoesNotContain("ParentGrantForSwitch", branch);
    }

    [Fact]
    public void The_page_keeps_everything_else_out_of_a_required_picker()
    {
        string Ui(params string[] path) => File.ReadAllText(Path.Combine([Root(), "ui", .. path])).Replace("\r\n", "\n");
        Assert.Contains("if (state.pickerHold) return;", Ui("components", "quick-menu.js"));
        Assert.Contains("if (state.pickerHold) return;", Ui("app.js"));
        Assert.Contains("if (state.pickerHold && page.id !== 'profile-picker') return;", Ui("core", "nav.js"));
        // Review M2 in the page: the parents' way out of a child's code only with a parents' code.
        var profiles = Ui("components", "profiles.js");
        Assert.Contains("const parentWay = !!status?.hasParentCode;", profiles);
        Assert.Contains("if (!parentWay) return undefined;", profiles);
    }

    [Fact]
    public void Codes_of_children_and_parents_never_match()
    {
        // Review N4.
        Assert.Contains("profiles.codeIsParent", Body("HudProfiles.cs", "    public object SetProfileCode("));
        foreach (var declaration in new[] { "    public async Task<object> SetParentCodeAsync(", "    public async Task<object> SetPasscodeAsync(", "    public async Task<object> SetCouchLockAsync(" })
        {
            var body = Body("Hud.cs", declaration);
            var refuse = body.IndexOf("RefuseChildCode(sequence);", StringComparison.Ordinal);
            Assert.True(refuse >= 0, declaration);
            // Review NEU-2: the comparison with the children's codes only after the permission, never as an oracle on a locked couch
            // without the children's wrong-code brake.
            var grant = body.IndexOf("RequireParentGrant(", StringComparison.Ordinal);
            var unlocked = body.IndexOf("RequireUnlocked();", StringComparison.Ordinal);
            Assert.True(grant >= 0 && grant < refuse, $"{declaration}: RequireParentGrant before RefuseChildCode");
            Assert.True(unlocked >= 0 && unlocked < refuse, $"{declaration}: RequireUnlocked before RefuseChildCode");
        }
        // Nowhere else: every comparison with the children's codes is one of these three, behind the permission.
        var calls = Sources("src").Sum(s => Regex.Matches(s.Code, @"(?<!void )RefuseChildCode\(").Count);
        Assert.Equal(3, calls);
    }

    [Fact]
    public void A_child_cannot_switch_off_the_way_back_to_the_picker()
    {
        // Review NEU-1 (b): with parental controls and several profiles, screensaver and display off are the parents'.
        Assert.Contains("LockPolicy.IsHarmlessSetting(Arg(0).GetString()!, hud.ProfilesGuarded)", Code("src", "CouchHud.Shell", "UiHost", "UiBridge.cs"));
        Assert.DoesNotContain("HarmlessSettings.Contains(", Code("src", "CouchHud.Shell", "UiHost", "UiBridge.cs"));
        var settings = Body("Hud.cs", "    public async Task SetSettingAsync(");
        foreach (var key in new[] { "screensaverMinutes", "displayOffMinutes" })
        {
            var at = settings.IndexOf($"case \"{key}\":", StringComparison.Ordinal);
            Assert.True(at >= 0, key);
            var guard = settings.IndexOf("if (ProfilesGuarded) RequireParentGrant(\"idleSetting\", key);", at, StringComparison.Ordinal);
            var write = settings.IndexOf("config.UpdateOrThrow(", at, StringComparison.Ordinal);
            Assert.True(guard > at && guard < write, $"{key}: the parents' grant before the write");
        }
        // (a) the host's own threshold: on the session timer, the controller and the end of a game start it again.
        var hud = Code("src", "CouchHud.Shell", "Hud.cs");
        Assert.Contains("ProfileIdleTick();", hud);
        Assert.Contains("ProfileActivity();", Body("Hud.cs", "    private void OnPadDown("));
        var tick = Body("HudProfiles.cs", "    private void ProfileIdleTick(");
        Assert.Contains("ProfileIdleClock.Due(", tick);
        Assert.Contains("RealInputWithin(", tick);
        Assert.Contains("AnyGameRunning", tick);
        // Fixed: no setting, nothing from the page or the tools changes the threshold.
        Assert.DoesNotContain("ProfileIdleClock", Code("src", "CouchHud.Shell", "UiHost", "UiBridge.cs"));
        Assert.Contains("public static readonly TimeSpan Threshold", Code("src", "CouchHud.Core", "Config", "ProfileSwitch.cs"));
    }

    [Fact]
    public void Remote_mcp_the_assistant_and_plugins_cannot_switch_create_or_set_codes()
    {
        var outside = new[]
        {
            ("CouchHud.Shell", "Services", "RemoteHost.cs"),
            ("CouchHud.Shell", "Services", "McpHost.cs"),
            ("CouchHud.Shell", "Services", "HudTools.cs"),
            ("CouchHud.Core", "Assistant", "Tools.cs"),
            ("CouchHud.Core", "Assistant", "McpServer.cs"),
            ("CouchHud.Core", "Assistant", "AssistantSession.cs"),
            ("CouchHud.Core", "Plugins", "PluginManager.cs")
        };
        foreach (var (project, folder, file) in outside)
        {
            var code = Code("src", project, folder, file);
            foreach (var method in HostMethods)
                Assert.False(Regex.IsMatch(code, $@"\b{method}\s*\("), $"{file} calls {method}");
            Assert.DoesNotContain("ProfileCode", code);
            Assert.DoesNotContain("ActiveProfileId", code);
        }
        foreach (var (rel, code) in Sources("src", "CouchHud.Sdk"))
            Assert.False(code.Contains("Profile", StringComparison.Ordinal), $"the SDK names profiles: {rel}");
    }

    [Fact]
    public void The_phones_keys_and_text_do_not_count_on_a_profile_screen()
    {
        // The host knows a pending picker itself (review N2), the UI's flag only adds the management page.
        var button = Body("Hud.cs", "    void IRemoteTarget.Button(string button)");
        var guard = button.IndexOf("if (_profileScreen || ProfilePickerPending) return;", StringComparison.Ordinal);
        var input = button.IndexOf("Emit(\"input\"", StringComparison.Ordinal);
        Assert.True(guard >= 0 && input > guard, "the profile screen guard comes before any key reaches the HUD");
        var text = Body("Hud.cs", "    async Task<string?> IRemoteTarget.TextAsync(string text, bool submit)");
        var textGuard = text.IndexOf("_profileScreen || ProfilePickerPending", StringComparison.Ordinal);
        var emit = text.IndexOf("Emit(\"remote\"", StringComparison.Ordinal);
        Assert.True(textGuard >= 0 && emit > textGuard, "no phone text into a profile screen");
    }

    [Fact]
    public void Every_change_needs_the_parents_code_for_it()
    {
        foreach (var declaration in new[]
                 {
                     "    public object AddProfile(", "    public object RenameProfile(", "    public object SetProfileLook(",
                     "    public object SetProfileRestricted(", "    public object SetProfileCode(", "    public object RemoveProfile(",
                     "    public object SetProfileRule("
                 })
        {
            var body = Body("HudProfiles.cs", declaration);
            var grant = body.IndexOf("RequireProfileManage(", StringComparison.Ordinal);
            var update = body.IndexOf("config.UpdateOrThrow(", StringComparison.Ordinal) is var u and >= 0 ? u : body.IndexOf("ApplyProfileRule(", StringComparison.Ordinal);
            Assert.True(grant >= 0 && update > grant, $"{declaration.Trim()}: the parents' grant before the change");
        }
        Assert.Contains("RequireParentGrant(ProfileSwitch.ManageAction, id);", Body("HudProfiles.cs", "    private void RequireProfileManage("));
        // Not open at the locked couch: only the picker is.
        var policy = Code("src", "CouchHud.Core", "Config", "ProfileSwitch.cs");
        var open = Regex.Match(policy, @"OpenWhileLocked = new HashSet<string> \{(?<list>[^}]*)\}").Groups["list"].Value;
        Assert.Equal(["getProfiles", "profileIdle", "profileScreen", "switchProfile"], Regex.Matches(open, "\"(\\w+)\"").Select(m => m.Groups[1].Value).Order());
        var lockPolicy = Code("src", "CouchHud.Core", "Guard", "LockPolicy.cs");
        foreach (var call in Calls) Assert.DoesNotContain($"\"{call}\"", lockPolicy);
    }

    [Fact]
    public void A_childs_code_opens_its_own_profile_and_nothing_else()
    {
        // The children's codes are checked in exactly one place, against the target profile's hash, with its own lockout.
        var verify = new List<string>();
        foreach (var (rel, code) in Sources("src", "CouchHud.Shell"))
            if (code.Contains(".CodeHash", StringComparison.Ordinal)) verify.Add(rel);
        Assert.Equal([Path.Combine("CouchHud.Shell", "HudProfiles.cs")], verify);
        var body = Body("HudProfiles.cs", "    public object SwitchProfile(");
        Assert.Contains("Passcode.Verify(code, config.Current.Profile(id).CodeHash)", body);
        Assert.Contains("var lockout = ProfileLockout(id);", body);
        Assert.True(body.IndexOf("lockout.Wait", StringComparison.Ordinal) < body.IndexOf("Passcode.Verify(", StringComparison.Ordinal), "the lockout before the check");
        // It never gives the parents' grant.
        Assert.DoesNotContain("_grant.Give", Code("src", "CouchHud.Shell", "HudProfiles.cs"));
    }

    [Fact]
    public void A_switch_rebuilds_the_controller_and_drops_every_release()
    {
        var body = Body("HudProfiles.cs", "    private void ActivateProfile(");
        // Review N1/M3: everything of the old profile goes first (grants, recent sites, browser, the unlock with the setup hold and the
        // service's cache through ForgetUnlock), then the switch, then the controller for the new profile.
        string[] order =
        [
            "_parentalController?.Service.Flush();", "_grant.Clear();", "_browserRecentShown = false;", "CloseBrowser(",
            "_setupHoldUntil = DateTimeOffset.MinValue;", "ForgetUnlock();", "c.SwitchActiveProfile(id)", "_parentalController = null;",
            "_ = Parental;", "Strings.Use(", "library.ReapplyUserState();"
        ];
        var at = -1;
        foreach (var step in order)
        {
            var next = body.IndexOf(step, StringComparison.Ordinal);
            Assert.True(next > at, $"{step} missing or out of order");
            at = next;
        }
        // The switch happens only through SwitchProfile, after its decision, and never while a game runs.
        var members = Sources("src", "CouchHud.Shell").Where(s => Regex.IsMatch(s.Code, @"\bActivateProfile\(id\)")).Select(s => s.Rel).ToList();
        Assert.Equal([Path.Combine("CouchHud.Shell", "HudProfiles.cs")], members);
        var sw = Body("HudProfiles.cs", "    public object SwitchProfile(");
        Assert.True(sw.IndexOf("DecideSwitch(id)", StringComparison.Ordinal) < sw.IndexOf("ActivateProfile(id)", StringComparison.Ordinal));
        Assert.True(sw.IndexOf("if (AnyGameRunning) throw", StringComparison.Ordinal) < sw.IndexOf("ActivateProfile(id)", StringComparison.Ordinal));
    }

    [Fact]
    public void A_protected_profile_goes_back_to_the_picker_after_the_lock_and_the_display()
    {
        // Review N3: not on every lock (game start, back to the HUD, the end of the two-minute unlock), never during a game.
        Assert.DoesNotContain("ProfileIdle(", Body("Hud.cs", "    private void SyncLockState()"));
        Assert.Contains("ProfileIdle();", Body("Hud.cs", "    public void DisplayOff()"));
        Assert.Contains("if (AnyGameRunning) return;", Body("HudProfiles.cs", "    public void ProfileIdle()"));
        // ForgetUnlock really drops the unlock and the service's cache (what the switch relies on).
        var forget = Body("Hud.cs", "    private void ForgetUnlock()");
        Assert.Contains("_unlockedUntil = DateTimeOffset.MinValue;", forget);
        Assert.Contains("AdminService.Ops.Cache.Clear();", forget);
        // A protected profile waiting for its code starts nothing (MCP and the assistant start through Launch too).
        Assert.Contains("RequireProfileChosen();", Body("Hud.cs", "    public LibraryItem Launch(string id, bool byPerson = false)"));
    }

    [Fact]
    public void Going_back_to_the_picker_drops_the_unlock_and_shows_nothing_of_the_waiting_profile()
    {
        // VM test of part 2: the couch stayed unlocked behind the picker and getSettings handed out the MCP token.
        var idle = Body("HudProfiles.cs", "    public void ProfileIdle()");
        var chosen = idle.IndexOf("_profileChosen = false;", StringComparison.Ordinal);
        Assert.True(chosen >= 0);
        foreach (var step in new[] { "_setupHoldUntil = DateTimeOffset.MinValue;", "ForgetUnlock();", "UseLookLanguage();" })
            Assert.True(idle.IndexOf(step, StringComparison.Ordinal) > chosen, $"{step} missing after the picker came back");
        Assert.True(idle.IndexOf("ForgetUnlock();", StringComparison.Ordinal) < idle.IndexOf("Emit(\"profiles\"", StringComparison.Ordinal));
        // The MCP token never while locked or while the picker waits.
        Assert.Contains("mcpToken = forUi && !Locked && !ProfileHeld ?", Body("Hud.cs", "    public object GetSettings(bool forUi = true)"));
        // The page's look and the parental status: the household's, no remaining time of the waiting profile.
        var info = Body("Hud.cs", "    public object SystemInfo() => new");
        Assert.Contains("skin = LookProfile.Skin", info);
        Assert.Contains("languageSetting = LookProfile.Language", info);
        Assert.DoesNotContain("ActiveProfile", info);
        var parental = Body("Hud.cs", "    public object GetParental()");
        var held = parental.IndexOf("if (ProfileHeld)", StringComparison.Ordinal);
        Assert.True(held >= 0 && held < parental.IndexOf("remainingSeconds", StringComparison.Ordinal), "the held answer comes first");
        Assert.Contains("ProfileSwitch.Look(config.Current, ProfileHeld)", Code("src", "CouchHud.Shell", "HudProfiles.cs"));
    }

    [Fact]
    public void A_question_on_the_tv_is_not_asked_while_the_picker_waits()
    {
        // VM test of part 2: MCP's launch asked, the page turned it down, and the client waited for its own timeout.
        foreach (var declaration in new[] { "    private async Task<bool> ConfirmExternalAsync(", "    private Task<bool> ConfirmAsync(" })
        {
            var body = Body("Hud.cs", declaration);
            var guard = body.IndexOf("RequireProfileChosen();", StringComparison.Ordinal);
            Assert.True(guard >= 0 && guard < body.IndexOf("RequestConfirmation(", StringComparison.Ordinal), $"{declaration.Trim()} asks before it refuses");
        }
    }

    [Fact]
    public void A_refused_profile_step_is_one_log_line()
    {
        var bridge = Code("src", "CouchHud.Shell", "UiHost", "UiBridge.cs");
        var refused = bridge.IndexOf("catch (Exception ex) when (ProfilePolicy.All.Contains(method)", StringComparison.Ordinal);
        Assert.True(refused >= 0 && refused < bridge.IndexOf("[bridge] {method} failed: {ex}", StringComparison.Ordinal));
        Assert.Contains("Console.WriteLine($\"[bridge] {method} refused: {ex.Message}\");", bridge);
    }

    [Fact]
    public void A_new_name_is_refused_when_too_long_never_cut()
    {
        foreach (var declaration in new[] { "    public object AddProfile(", "    public object RenameProfile(" })
        {
            var body = Body("HudProfiles.cs", declaration);
            Assert.Contains("ProfileNames.Accept(name)", body);
            Assert.DoesNotContain("ProfileNames.Clean(", body);
        }
    }
}
