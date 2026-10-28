using System.Text.Json;
using CouchHud.Core.Assistant;
using CouchHud.Core.Config;
using CouchHud.Core.Guard;
using CouchHud.Core.Plugins;
using CouchHud.Core.Security;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>
/// Plugin functions: a plugin declares named functions and tags its items; the owner chooses per function admin only, restricted too, or nobody.
/// The host enforces it before it shows or runs anything tagged. Old plugins (no functions) behave as before; unknown ids and damaged config
/// fail closed; a plugin can never loosen anything by itself; changing a level is a gated change of protection.
/// </summary>
public sealed class PluginFunctionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-fn-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] TrustKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    public PluginFunctionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static readonly IReadOnlySet<string> Caps = new HashSet<string>
        { PluginCapabilities.Pages, PluginCapabilities.QuickActions, PluginCapabilities.Assistant, PluginCapabilities.Tabs };

    private IPluginContext Context(PluginOrigin origin, out PluginManager manager, Dictionary<string, string>? stored = null)
    {
        var m = new PluginManager([(Path.Combine(_dir, "plugins"), true)], _dir, _ => true) { Trust = new PluginTrust(_dir, TrustKey) };
        m.StoredLevel = (_, fn) => stored is not null && stored.TryGetValue(fn, out var v) ? v : null;
        manager = m;
        return m.ContextFor("tool", new PluginManager.PluginGrant("tool", origin, Caps), markLoaded: true);
    }

    private static PluginFunction Fn(string id, PluginLevel suggested = PluginLevel.Admin) => new(id, "Deutsch", "English") { Suggested = suggested };

    private static PluginRow Row(string id = "r", string? fn = null, bool locked = false, bool grant = false) =>
        new(id, "Zeile") { Function = fn, AllowedWhileLocked = locked, RequiresParentGrant = grant };

    private static QuickAction Quick(string? fn = null, bool locked = false, bool grant = false) =>
        new() { Id = "q", Label = "Q", Run = _ => Task.FromResult<string?>(null), Function = fn, AllowedWhileLocked = locked, RequiresParentGrant = grant };

    // ---- Levels x (unlocked, locked, child profile) ----

    [Theory]
    [InlineData(PluginLevel.Admin, false, false, null, false)]
    [InlineData(PluginLevel.Admin, true, false, "lock.unlockFirst", false)]
    [InlineData(PluginLevel.Admin, false, true, null, true)]
    [InlineData(PluginLevel.Admin, true, true, "lock.unlockFirst", false)]
    [InlineData(PluginLevel.Restricted, false, false, null, false)]
    [InlineData(PluginLevel.Restricted, true, false, null, false)]
    [InlineData(PluginLevel.Restricted, false, true, null, false)]
    [InlineData(PluginLevel.Restricted, true, true, null, false)]
    [InlineData(PluginLevel.Off, false, false, "plugin.fn.off", false)]
    [InlineData(PluginLevel.Off, true, false, "plugin.fn.off", false)]
    [InlineData(PluginLevel.Off, false, true, "plugin.fn.off", false)]
    [InlineData(PluginLevel.Off, true, true, "plugin.fn.off", false)]
    public void Each_level_in_each_state_for_pages_rows_entries_and_tabs(PluginLevel level, bool locked, bool child, string? why, bool parentCode)
    {
        var access = PluginFunctionRules.Access(level, locked, child);
        Assert.Equal(why, access.Refused);
        Assert.Equal(parentCode, access.ParentCode);
        var page = new PluginPage { Id = "p", Title = "t", Rows = _ => Task.FromResult<IReadOnlyList<PluginRow>>([]) };
        Assert.Equal(why is null, PluginPolicy.MayView(locked, page, level, child));
        Assert.Equal(parentCode, PluginPolicy.ViewNeedsParentCode(locked, level, child));
        // Even with parental controls off: a child's profile asks for the parents' code on an admin function.
        var press = PluginPolicy.MayPress(locked, PluginOrigin.FirstParty, Row(), false, "x", level, child);
        Assert.Equal(why, press.Refused);
        Assert.Equal(parentCode, press.ParentCode);
        var run = PluginPolicy.MayRun(locked, PluginOrigin.FirstParty, Quick(), false, level, child);
        Assert.Equal(why, run.Refused);
        Assert.Equal(parentCode, run.ParentCode);
        Assert.Equal(why is null, PluginPolicy.Shown(locked, PluginOrigin.FirstParty, Quick(), level, child));
        Assert.Equal(why is null ? 1 : 0, PluginPolicy.Visible(locked, child, [Row()], _ => level).Count);
    }

    [Theory]
    [InlineData(PluginLevel.Admin, false, false, null)]
    [InlineData(PluginLevel.Admin, true, false, "lock.unlockFirst")]
    [InlineData(PluginLevel.Admin, false, true, "plugin.gate.child")]
    [InlineData(PluginLevel.Restricted, true, true, null)]
    [InlineData(PluginLevel.Off, false, false, "plugin.fn.off")]
    public void A_tool_cannot_be_asked_for_a_code_so_admin_in_a_child_profile_is_refused(PluginLevel level, bool locked, bool child, string? why) =>
        Assert.Equal(why, PluginFunctionRules.Refusal(level, locked, child));

    [Fact]
    public void A_link_row_in_a_child_profile_changes_nothing_and_asks_for_no_code()
    {
        var link = new PluginRow("l", "x") { Kind = new PluginRowKind.Link("p") };
        var press = PluginPolicy.MayPress(false, PluginOrigin.FirstParty, link, true, "x", PluginLevel.Admin, true);
        Assert.Null(press.Refused);
        Assert.False(press.ParentCode);
    }

    [Theory]
    [InlineData(PluginOrigin.FirstParty)]
    [InlineData(PluginOrigin.ThirdParty)]
    [InlineData(PluginOrigin.Local)]
    public void Restricted_works_for_every_origin_when_the_owner_chose_it_and_the_plugins_own_flags_do_not_matter(PluginOrigin origin)
    {
        // The plugin's flags (AllowedWhileLocked false) cannot make a restricted row stricter, and a flag set to true cannot loosen admin.
        Assert.Null(PluginPolicy.MayPress(locked: true, origin, Row(locked: false), true, "x", PluginLevel.Restricted, childProfile: true).Refused);
        Assert.NotNull(PluginPolicy.MayPress(locked: true, origin, Row(locked: true), true, "x", PluginLevel.Admin, childProfile: false).Refused);
        Assert.NotNull(PluginPolicy.MayRun(locked: true, origin, Quick(locked: true), true, PluginLevel.Admin).Refused);
    }

    [Fact]
    public void Admin_keeps_the_parents_code_rules_restricted_asks_for_none()
    {
        // Admin: as today (a row that asks, and every row of a third party that changes something, under parental controls).
        Assert.True(PluginPolicy.MayPress(false, PluginOrigin.FirstParty, Row(grant: true), true, "x", PluginLevel.Admin).ParentCode);
        Assert.False(PluginPolicy.MayPress(false, PluginOrigin.FirstParty, Row(grant: false), true, "x", PluginLevel.Admin).ParentCode);
        Assert.True(PluginPolicy.MayPress(false, PluginOrigin.ThirdParty, Row(), true, "x", PluginLevel.Admin).ParentCode);
        Assert.False(PluginPolicy.MayPress(false, PluginOrigin.ThirdParty, Row(), false, "x", PluginLevel.Admin).ParentCode);
        Assert.True(PluginPolicy.MayRun(false, PluginOrigin.ThirdParty, Quick(), true, PluginLevel.Admin).ParentCode);
        // Restricted: everyone, no code (the owner chose it); a child cannot be asked for a parents' code.
        Assert.False(PluginPolicy.MayPress(false, PluginOrigin.ThirdParty, Row(grant: true), true, "x", PluginLevel.Restricted).ParentCode);
        Assert.False(PluginPolicy.MayRun(false, PluginOrigin.ThirdParty, Quick(grant: true), true, PluginLevel.Restricted).ParentCode);
    }

    [Fact]
    public void Untagged_items_behave_exactly_as_before()
    {
        // No level: the old rules, with every old argument shape.
        var page = new PluginPage { Id = "p", Title = "t", Rows = _ => Task.FromResult<IReadOnlyList<PluginRow>>([]) };
        Assert.False(PluginPolicy.MayView(true, page));
        Assert.True(PluginPolicy.MayView(false, page));
        Assert.True(PluginPolicy.MayView(true, new PluginPage { Id = "p", Title = "t", AllowedWhileLocked = true, Rows = page.Rows }));
        Assert.Null(PluginPolicy.MayPress(true, PluginOrigin.FirstParty, Row(locked: true), false, "x").Refused);
        Assert.NotNull(PluginPolicy.MayPress(true, PluginOrigin.ThirdParty, Row(locked: true), false, "x").Refused);
        Assert.NotNull(PluginPolicy.MayRun(true, PluginOrigin.FirstParty, Quick(), false).Refused);
        Assert.True(PluginPolicy.Shown(true, PluginOrigin.FirstParty, Quick(locked: true)));
        Assert.False(PluginPolicy.Shown(true, PluginOrigin.ThirdParty, Quick(locked: true)));
        // A child profile alone changes nothing for an untagged item (the old behaviour).
        Assert.Null(PluginPolicy.MayPress(false, PluginOrigin.FirstParty, Row(), false, "x", null, childProfile: true).Refused);
        Assert.Single(PluginPolicy.Visible(true, false, [Row(locked: true), Row()], _ => null));
    }

    [Fact]
    public void A_plugin_without_functions_has_no_level_at_all()
    {
        var ctx = Context(PluginOrigin.FirstParty, out var manager);
        ctx.RegisterPage(new PluginPage { Id = "p", Title = "t", AllowedWhileLocked = true, Rows = _ => Task.FromResult<IReadOnlyList<PluginRow>>([]) });
        Assert.Null(manager.LevelOf("tool", null));
        Assert.Null(manager.LevelOf("tool", ""));
        Assert.Empty(manager.Functions);
    }

    // ---- Defaults, suggestions and the stored choice ----

    [Fact]
    public void The_default_is_the_plugins_suggestion_but_only_CouchHUDs_own_plugins_may_suggest_more_than_admin()
    {
        Assert.Equal(PluginLevel.Restricted, PluginFunctionRules.Effective(Fn("a", PluginLevel.Restricted), null, own: true));
        Assert.Equal(PluginLevel.Admin, PluginFunctionRules.Effective(Fn("a", PluginLevel.Restricted), null, own: false));
        Assert.Equal(PluginLevel.Admin, PluginFunctionRules.Effective(Fn("a", PluginLevel.Admin), null, own: true));
        // A plugin cannot switch itself off or others' use either (Off as a suggestion is no more than admin).
        Assert.Equal(PluginLevel.Admin, PluginFunctionRules.Effective(Fn("a", PluginLevel.Off), null, own: true));
        Assert.Equal(PluginLevel.Admin, PluginFunctionRules.Effective(Fn("a", PluginLevel.Off), null, own: false));
    }

    [Theory]
    [InlineData("admin", PluginLevel.Admin)]
    [InlineData("restricted", PluginLevel.Restricted)]
    [InlineData("off", PluginLevel.Off)]
    public void The_stored_choice_wins_over_the_suggestion_for_every_origin(string stored, PluginLevel expected)
    {
        foreach (var own in new[] { true, false })
            Assert.Equal(expected, PluginFunctionRules.Effective(Fn("a", PluginLevel.Restricted), stored, own));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Kind")]
    [InlineData("child")]
    [InlineData("ADMIN ")]
    [InlineData("2")]
    [InlineData("restricted,off")]
    public void A_damaged_stored_value_falls_back_to_the_default_never_looser(string stored)
    {
        Assert.Equal(PluginLevel.Admin, PluginFunctionRules.Effective(Fn("a"), stored, own: true));
        Assert.Equal(PluginLevel.Restricted, PluginFunctionRules.Effective(Fn("a", PluginLevel.Restricted), stored, own: true));
        Assert.Equal(PluginLevel.Admin, PluginFunctionRules.Effective(Fn("a", PluginLevel.Restricted), stored, own: false));
    }

    [Fact]
    public void An_unknown_function_id_fails_closed_to_admin_even_with_a_stored_choice()
    {
        var ctx = Context(PluginOrigin.FirstParty, out var manager, new() { ["ghost"] = "restricted", ["known"] = "restricted" });
        ctx.RegisterFunctions(Fn("known"));
        Assert.Equal(PluginLevel.Restricted, manager.LevelOf("tool", "known"));
        Assert.Equal(PluginLevel.Admin, manager.LevelOf("tool", "ghost"));
        Assert.Equal(PluginLevel.Admin, manager.LevelOf("tool", "never-declared"));
        // A row that names it is therefore not available on the locked couch.
        Assert.NotNull(PluginPolicy.MayPress(true, PluginOrigin.FirstParty, Row(fn: "ghost", locked: true), false, "tool", manager.LevelOf("tool", "ghost")).Refused);
    }

    [Fact]
    public void A_store_that_throws_counts_as_nothing_stored()
    {
        var ctx = Context(PluginOrigin.FirstParty, out var manager);
        manager.StoredLevel = (_, _) => throw new InvalidOperationException("broken");
        ctx.RegisterFunctions(Fn("a", PluginLevel.Restricted), Fn("b"));
        Assert.Equal(PluginLevel.Restricted, manager.LevelOf("tool", "a"));
        Assert.Equal(PluginLevel.Admin, manager.LevelOf("tool", "b"));
    }

    [Fact]
    public void Config_tampering_a_null_map_a_null_inner_map_wrong_case_and_junk_never_loosens()
    {
        Assert.Null(PluginFunctionRules.Stored(null, "p", "f"));
        var map = new Dictionary<string, Dictionary<string, string>> { ["p"] = null!, ["q"] = new() { ["F"] = "restricted" } };
        Assert.Null(PluginFunctionRules.Stored(map, "p", "f"));
        Assert.Equal("restricted", PluginFunctionRules.Stored(map, "Q", "f"));
        // And through the real JSON path: nulls and wrong types do not even load as a loosening.
        var cfg = ConfigStore.Parse("""{"PluginFunctionLevels":{"p":null,"q":{"f":"restricted","g":null}}}""");
        Assert.NotNull(cfg);
        Assert.Null(PluginFunctionRules.Stored(cfg!.PluginFunctionLevels, "p", "f"));
        Assert.Null(PluginFunctionRules.ParseLevel(PluginFunctionRules.Stored(cfg.PluginFunctionLevels, "q", "g")));
        Assert.Empty(ConfigStore.Parse("{}")!.PluginFunctionLevels);
    }

    // ---- Declaring functions ----

    [Theory]
    [InlineData("a", true)]
    [InlineData("light-1_x", true)]
    [InlineData("", false)]
    [InlineData("Upper", false)]
    [InlineData("a b", false)]
    [InlineData("ä", false)]
    [InlineData("this-id-is-far-too-long-for-the-host-ok", false)]
    public void Function_ids_are_checked(string id, bool ok)
    {
        var ctx = Context(PluginOrigin.ThirdParty, out var manager);
        ctx.RegisterFunctions(new PluginFunction(id, "x", "y"));
        Assert.Equal(ok, manager.Functions.Count == 1);
    }

    [Fact]
    public void Declaring_is_bounded_cleaned_and_a_second_declaration_replaces_the_first()
    {
        var ctx = Context(PluginOrigin.ThirdParty, out var manager);
        ctx.RegisterFunctions(Enumerable.Range(0, 30).Select(i => new PluginFunction("f" + i, "x", "y")).ToArray());
        Assert.Equal(PluginFunctionRules.MaxPerPlugin, manager.Functions.Count);
        ctx.RegisterFunctions(new PluginFunction("f0", "neu\u0007", "") { Suggested = PluginLevel.Restricted });
        var f0 = manager.Functions.Single(f => f.Function.Id == "f0").Function;
        Assert.Equal("neu", f0.LabelDe);
        Assert.Equal("neu", f0.LabelEn);
        ctx.RegisterFunctions(new PluginFunction("f1", new string('x', 500), "y"));
        Assert.Equal(PluginFunctionRules.MaxLabel, manager.Functions.Single(f => f.Function.Id == "f1").Function.LabelDe.Length);
        ctx.RegisterFunctions(new PluginFunction("empty", "  ", "\n"));
        Assert.DoesNotContain(manager.Functions, f => f.Function.Id == "empty");
    }

    [Fact]
    public void A_context_member_for_functions_is_in_the_gate_table_and_needs_no_capability()
    {
        Assert.True(PluginPolicy.ContextMembers.TryGetValue("RegisterFunctions", out var cap));
        Assert.Null(cap);
    }

    // ---- Tabs, tools ----

    [Fact]
    public void Tabs_follow_the_level_of_their_function()
    {
        var tab = new PluginTab("t", "p", "Titel", "Title") { Function = "f" };
        IReadOnlyList<PluginTabView> Resolve(PluginOrigin origin, PluginLevel? level, bool locked, bool child) =>
            PluginTabRules.Resolve([new("tool", origin, Caps, tab)], _ => true, (_, _) => true, "de", AreaSet.AllOn, child, locked, (_, _) => level);
        // Restricted: visible on the locked couch and in a child profile, for any origin (the owner chose it).
        Assert.Single(Resolve(PluginOrigin.ThirdParty, PluginLevel.Restricted, locked: true, child: true));
        Assert.True(Resolve(PluginOrigin.ThirdParty, PluginLevel.Restricted, true, true)[0].ChildSafe);
        // Admin: not on the locked couch; in a child profile the tab shows and opening its page asks for the parents' code. The tab's own ChildSafe flag does not help.
        Assert.Single(Resolve(PluginOrigin.FirstParty, PluginLevel.Admin, false, false));
        Assert.Empty(Resolve(PluginOrigin.FirstParty, PluginLevel.Admin, true, false));
        Assert.Single(Resolve(PluginOrigin.FirstParty, PluginLevel.Admin, false, true));
        Assert.Empty(Resolve(PluginOrigin.FirstParty, PluginLevel.Off, false, false));
        // No level (untagged or no answer): the old rules (third party never locked, nobody in a child profile without ChildSafe).
        Assert.Empty(Resolve(PluginOrigin.ThirdParty, null, true, false));
        Assert.Empty(Resolve(PluginOrigin.FirstParty, null, false, true));
    }

    [Fact]
    public void The_manager_lists_tabs_by_their_function_level()
    {
        var ctx = Context(PluginOrigin.FirstParty, out var manager);
        ctx.RegisterFunctions(Fn("f", PluginLevel.Restricted));
        ctx.RegisterPage(new PluginPage { Id = "p", Title = "t", Rows = _ => Task.FromResult<IReadOnlyList<PluginRow>>([]) });
        ctx.RegisterTab(new PluginTab("t", "p", "Titel", "Title") { Function = "f" });
        manager.IsLocked = () => true;
        Assert.Single(manager.ResolveTabs(AreaSet.AllOn, childProfile: true));
        manager.StoredLevel = (_, _) => "off";
        Assert.Empty(manager.ResolveTabs(AreaSet.AllOn, false));
        manager.StoredLevel = (_, _) => "admin";
        Assert.Empty(manager.ResolveTabs(AreaSet.AllOn, false));
        manager.IsLocked = () => false;
        Assert.Single(manager.ResolveTabs(AreaSet.AllOn, false));
    }

    [Fact]
    public async Task Off_hides_a_tool_and_refuses_the_call_restricted_allows_it_locked_admin_does_not()
    {
        var stored = new Dictionary<string, string>();
        var ctx = Context(PluginOrigin.FirstParty, out var manager, stored);
        ctx.RegisterFunctions(Fn("f", PluginLevel.Restricted));
        ctx.RegisterTool(new PluginTool { Name = "act", Description = "x", Function = "f", Run = (_, _) => Task.FromResult("ran") });
        ctx.RegisterTool(new PluginTool { Name = "old", Description = "x", AllowedWhileLocked = true, Run = (_, _) => Task.FromResult("ran") });
        var args = JsonDocument.Parse("{}").RootElement;

        Assert.True(manager.ToolSpecs.Single(t => t.Name == "plugin_tool_act").AllowedWhileLocked);
        Assert.Equal("ran", await manager.CallToolAsync("plugin_tool_act", args));

        stored["f"] = "admin";
        Assert.False(manager.ToolSpecs.Single(t => t.Name == "plugin_tool_act").AllowedWhileLocked);

        stored["f"] = "off";
        Assert.DoesNotContain(manager.ToolSpecs, t => t.Name == "plugin_tool_act");
        await Assert.ThrowsAsync<ArgumentException>(() => manager.CallToolAsync("plugin_tool_act", args));
        // The untagged tool is untouched by all of this.
        Assert.True(manager.ToolSpecs.Single(t => t.Name == "plugin_tool_old").AllowedWhileLocked);
        Assert.Equal("ran", await manager.CallToolAsync("plugin_tool_old", args));
    }

    [Fact]
    public void A_third_party_tool_tagged_restricted_by_its_own_suggestion_stays_off_the_locked_couch()
    {
        var ctx = Context(PluginOrigin.ThirdParty, out var manager);
        ctx.RegisterFunctions(Fn("f", PluginLevel.Restricted));
        ctx.RegisterTool(new PluginTool { Name = "act", Description = "x", Function = "f", AllowedWhileLocked = true, Run = (_, _) => Task.FromResult("") });
        Assert.False(manager.ToolSpecs.Single().AllowedWhileLocked);
    }

    [Fact]
    public void A_row_names_its_own_function_or_inherits_the_pages_and_hides_by_it()
    {
        var rows = new[] { Row("a"), Row("b", fn: "open"), Row("c", fn: "closed") };
        var levels = new Dictionary<string, PluginLevel> { ["open"] = PluginLevel.Restricted, ["closed"] = PluginLevel.Admin, ["page"] = PluginLevel.Restricted };
        PluginLevel? LevelOf(PluginRow r) => levels.TryGetValue(r.Function ?? "page", out var l) ? l : null;
        var shown = PluginPolicy.Visible(locked: true, childProfile: false, rows, LevelOf);
        Assert.Equal(["a", "b"], shown.Select(r => r.Id));
    }

    // ---- Changing a level is a gated change of protection ----

    private static PluginPolicy.GateContext Gate(PluginCaller caller = PluginCaller.Hud, bool locked = false, bool child = false, bool parental = false) =>
        new(caller, locked, child, ExpertMode: true, DeveloperSwitch: false, ParentalEnabled: parental);

    [Theory]
    [InlineData(false, false, false, null)]
    [InlineData(false, false, true, null)]
    [InlineData(true, false, false, "lock.unlockFirst")]
    [InlineData(true, false, true, "lock.unlockFirst")]
    [InlineData(false, true, false, "plugin.gate.child")]
    [InlineData(false, true, true, "plugin.gate.child")]
    public void Changing_a_level_needs_the_unlocked_couch_and_no_child_profile_and_always_a_parents_grant(bool locked, bool child, bool parental, string? why)
    {
        var d = PluginPolicy.DecideLevelChange(Gate(locked: locked, child: child, parental: parental));
        Assert.Equal(why, d.Refused);
        Assert.Equal(why is null, d.ParentCode);
        Assert.Equal("pluginFunction", d.ParentAction);
    }

    [Theory]
    [InlineData(PluginCaller.Remote)]
    [InlineData(PluginCaller.Mcp)]
    [InlineData(PluginCaller.Assistant)]
    [InlineData(PluginCaller.Plugin)]
    public void Only_the_HUDs_own_page_changes_a_level(PluginCaller caller) =>
        Assert.Equal("plugin.gate.hudOnly", PluginPolicy.DecideLevelChange(Gate(caller)).Refused);

    [Fact]
    public void Raising_a_level_takes_the_couch_code_only_without_parental_controls_and_without_a_parents_code()
    {
        // pluginFunction is on the HUD-only list: the couch code stands in only while parental controls are off AND the service is freshly known
        // to hold no parents' code. Otherwise (parental on, a parents' code held, or the state unknown) it is the parents' code.
        Assert.Contains("pluginFunction", ParentCodeKind.HudOnlyActions);
        Assert.Equal(ParentCheckKind.Couch, ParentCodeKind.For(false, "pluginFunction", serviceParentKnownAbsent: true));
        Assert.Equal(ParentCheckKind.Parent, ParentCodeKind.For(false, "pluginFunction", serviceParentKnownAbsent: false));
        Assert.Equal(ParentCheckKind.Parent, ParentCodeKind.For(true, "pluginFunction", serviceParentKnownAbsent: true));
        // Showing and using a function stays the parents' code, and nothing else sneaks in under a similar name.
        Assert.DoesNotContain("pluginFunctionLevel", ParentCodeKind.HudOnlyActions);
        foreach (var action in new[] { "pluginPage", "pluginRow", "pluginAction" }) Assert.DoesNotContain(action, ParentCodeKind.HudOnlyActions);
    }

    [Fact]
    public void The_restricted_level_is_only_meaningful_with_the_lock_or_parental_controls()
    {
        Assert.False(PluginFunctionRules.RestrictionsPossible("off", false));
        Assert.True(PluginFunctionRules.RestrictionsPossible("code", false));
        Assert.True(PluginFunctionRules.RestrictionsPossible("windows", false));
        Assert.True(PluginFunctionRules.RestrictionsPossible("off", true));
    }

    // ---- The shell wiring (contract tests on the source, as for the other gates) ----

    /// <summary>Source files as one text with LF line ends: a checkout with core.autocrlf has CRLF, and the helpers below cut at "\n".</summary>
    private static string ReadSource(string path) => File.ReadAllText(path).Replace("\r\n", "\n");

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CouchHud.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    private static string Hud() => ReadSource(Path.Combine(RepoRoot(), "src", "CouchHud.Shell", "Hud.cs"));

    private static string Body(string source, string signature)
    {
        var at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, signature);
        var rest = source[at..];
        var end = rest.IndexOf("\n    }\n", StringComparison.Ordinal);
        return end < 0 ? rest : rest[..end];
    }

    [Fact]
    public void Setting_a_level_goes_through_the_gate_the_grant_and_the_known_functions_only()
    {
        var body = Body(Hud(), "public object SetPluginFunctionLevel(");
        Assert.Contains("RefuseWhileConfigFaulted();", body);
        Assert.Contains("PluginPolicy.DecideLevelChange(PluginGateContext())", body);
        Assert.Contains("plugins.TryCanonicalFunction(pluginId, functionId, out var plugin, out var function)", body);
        Assert.Contains("PluginFunctionRules.ParseLevel(level)", body);
        // The grant is checked before the save and used up after it; it is fixed to this plugin and function.
        Assert.Contains("CheckParentGrant(decision.ParentAction, $\"{plugin}/{function}\")", body);
        Assert.True(body.IndexOf("CheckParentGrant", StringComparison.Ordinal) < body.IndexOf("config.UpdateOrThrow", StringComparison.Ordinal));
        Assert.True(body.IndexOf("config.UpdateOrThrow", StringComparison.Ordinal) < body.IndexOf("UseUp(ticket)", StringComparison.Ordinal));
    }

    [Fact]
    public void The_bridge_method_is_not_on_the_locked_couch_list_nor_any_remote_list()
    {
        Assert.DoesNotContain("setPluginFunctionLevel", Guard.LockPolicy.OpenWhileLocked);
        foreach (var file in new[] { "Remote", "Mcp" })
            foreach (var f in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories).Where(f => f.Contains(file, StringComparison.Ordinal) && !f.Contains("Tests") && !f.Contains("obj")))
                Assert.DoesNotContain("setPluginFunctionLevel", ReadSource(f));
    }

    [Fact]
    public void Uninstalling_a_plugin_forgets_its_levels()
    {
        var body = Body(Hud(), "public object UninstallPlugin(");
        Assert.Contains("ForgetFunctionLevels(ids)", body);
        var forget = Body(Hud(), "private void ForgetFunctionLevels(");
        Assert.Contains("c.PluginFunctionLevels.Remove(key)", forget);
    }

    [Fact]
    public void Every_shell_path_that_shows_or_runs_a_tagged_item_asks_the_level()
    {
        var hud = Hud();
        Assert.Contains("RequirePageView(pluginId, page, withGrant: true);", Body(hud, "public async Task<object> GetPluginPageAsync("));
        Assert.Contains("RequirePageView(pluginId, page);", Body(hud, "public async Task<object> ActivatePluginRowAsync("));
        Assert.Contains("plugins.LevelOf(pluginId, row?.Function ?? page.Function)", Body(hud, "public async Task<object> ActivatePluginRowAsync("));
        Assert.Contains("plugins.LevelOf(pluginId, row?.Function ?? page.Function)", Body(hud, "public async Task<object> ChangePluginRowAsync("));
        Assert.Contains("plugins.LevelOf(a.Plugin, a.Action.Function)", Body(hud, "public async Task<object> GetQuickActionsAsync("));
        Assert.Contains("plugins.LevelOf(pluginId, action.Function)", Body(hud, "public Task<string?> RunQuickActionAsync("));
        // A row the owner opened to everyone never carries the setup press.
        Assert.Contains("rowLevel != PluginLevel.Restricted", Body(hud, "public async Task<object> ActivatePluginRowAsync("));
        Assert.Contains("actionLevel != PluginLevel.Restricted", Body(hud, "public Task<string?> RunQuickActionAsync("));
    }

    [Fact]
    public void The_manager_tool_call_checks_the_level_itself()
    {
        var src = ReadSource(Path.Combine(RepoRoot(), "src", "CouchHud.Core", "Plugins", "PluginManager.cs"));
        Assert.Contains("LevelOf(tool.Plugin, tool.Tool.Function) is { } toolLevel", src);
        // The tool list the assistant and MCP see is the computed one.
        Assert.Contains("ToolSpecNow(t.Plugin, t.Tool, t.Spec)", src);
        Assert.NotNull(typeof(ToolSpec).GetProperty(nameof(ToolSpec.AllowedWhileLocked)));
    }

    // ---- Review follow-ups ----

    private static ParentGrant Grant(Func<DateTimeOffset> now) => new(now, TimeSpan.FromSeconds(90));

    [Fact]
    public void A_child_with_an_admin_function_needs_a_grant_for_exactly_this_plugin_function_and_item_and_it_is_used_once()
    {
        var now = DateTimeOffset.UtcNow;
        var grant = Grant(() => now);
        var press = PluginPolicy.MayPress(false, PluginOrigin.FirstParty, Row("r"), false, "tool", PluginLevel.Admin, childProfile: true);
        Assert.True(press.ParentCode);
        Assert.Equal("pluginRow", press.ParentAction);
        // Without the code: nothing fits.
        Assert.False(grant.Fits(press.ParentAction, "tool/r"));
        // With the parents' code (the page asked for exactly this action and item): allowed once, then gone.
        grant.Give(press.ParentAction, "tool/r");
        Assert.False(grant.Fits(press.ParentAction, "tool/other"));
        Assert.False(grant.Fits("pluginAction", "tool/r"));
        Assert.True(grant.TryConsume(press.ParentAction, "tool/r"));
        Assert.False(grant.Fits(press.ParentAction, "tool/r"));
        Assert.False(grant.TryConsume(press.ParentAction, "tool/r"));
        // A grant runs out.
        grant.Give("pluginPage", "tool/p");
        now += TimeSpan.FromSeconds(91);
        Assert.False(grant.Fits("pluginPage", "tool/p"));
    }

    [Theory]
    [InlineData("pluginPage")]
    [InlineData("pluginRow")]
    [InlineData("pluginAction")]
    public void The_couch_code_never_stands_in_for_the_parents_code_on_these_grants(string action)
    {
        Assert.DoesNotContain(action, ParentCodeKind.HudOnlyActions);
        Assert.Equal(ParentCheckKind.Parent, ParentCodeKind.For(false, action, serviceParentKnownAbsent: true));
        Assert.Equal(ParentCheckKind.Parent, ParentCodeKind.For(true, action, serviceParentKnownAbsent: true));
    }

    [Fact]
    public void The_host_asks_the_grant_for_the_page_the_row_and_the_entry_and_tells_the_page_what_to_ask_for()
    {
        var hud = Hud();
        // A missing grant is a ParentCodeNeededException (action and item), which the bridge hands to the page.
        Assert.Contains("throw new ParentCodeNeededException(Strings.Get(\"parental.codeRequired\"), action, item)", ReadSource(Path.Combine(RepoRoot(), "src", "CouchHud.Core", "Security", "ParentGate.cs")));
        var bridge = ReadSource(Path.Combine(RepoRoot(), "src", "CouchHud.Shell", "UiHost", "UiBridge.cs"));
        Assert.Contains("catch (ParentCodeNeededException ex)", bridge);
        Assert.Contains("CheckParentGrant(\"pluginPage\", $\"{pluginId}/{page.Id}\"", Body(hud, "private ParentTicket? RequirePageView("));
        Assert.Contains("UseUp(pageTicket);", Body(hud, "public async Task<object> GetPluginPageAsync("));
        // Presses and entries use the decision's own action and item (pluginRow / pluginAction, plugin/row) and use the grant up before the plugin runs.
        Assert.Contains("CheckParentGrant(decision.ParentAction, $\"{pluginId}/{pageId}/{rowId}\"", Body(hud, "public async Task<object> ActivatePluginRowAsync("));
        Assert.Contains("CheckParentGrant(decision.ParentAction, $\"{pluginId}/{actionId}\"", Body(hud, "public Task<string?> RunQuickActionAsync("));
        Assert.Contains("needsParentCode", Body(hud, "public object GetPluginGate("));
    }

    [Fact]
    public async Task Tools_are_hidden_and_refused_for_admin_in_a_child_profile_and_off_for_everybody()
    {
        var stored = new Dictionary<string, string> { ["f"] = "admin" };
        var ctx = Context(PluginOrigin.FirstParty, out var manager, stored);
        var child = false;
        manager.IsChildProfile = () => child;
        ctx.RegisterFunctions(Fn("f", PluginLevel.Restricted));
        ctx.RegisterTool(new PluginTool { Name = "act", Description = "x", Function = "f", Run = (_, _) => Task.FromResult("ran") });
        var args = JsonDocument.Parse("{}").RootElement;
        Assert.Equal("ran", await manager.CallToolAsync("plugin_tool_act", args));
        child = true;
        Assert.DoesNotContain(manager.ToolSpecs, t => t.Name == "plugin_tool_act");
        await Assert.ThrowsAsync<ArgumentException>(() => manager.CallToolAsync("plugin_tool_act", args));
        // Restricted: a child may use it.
        stored["f"] = "restricted";
        Assert.Contains(manager.ToolSpecs, t => t.Name == "plugin_tool_act");
        Assert.Equal("ran", await manager.CallToolAsync("plugin_tool_act", args));
        stored["f"] = "off";
        await Assert.ThrowsAsync<ArgumentException>(() => manager.CallToolAsync("plugin_tool_act", args));
    }

    [Fact]
    public void Setting_a_level_writes_under_the_declared_ids_and_removes_case_variants()
    {
        var map = new Dictionary<string, Dictionary<string, string>>
        {
            ["HomeAssistant"] = new() { ["Actions"] = "restricted", ["setup"] = "off" },
            ["homeassistant"] = new() { ["ACTIONS"] = "off" }
        };
        PluginFunctionRules.Set(map, "homeassistant", "actions", PluginLevel.Admin);
        Assert.Equal(["homeassistant"], map.Keys);
        Assert.Equal("admin", map["homeassistant"]["actions"]);
        Assert.Equal("off", map["homeassistant"]["setup"]);
        Assert.Single(map["homeassistant"].Keys, k => k.Equals("actions", StringComparison.OrdinalIgnoreCase));
        var body = Body(Hud(), "public object SetPluginFunctionLevel(");
        Assert.Contains("TryCanonicalFunction(pluginId, functionId, out var plugin, out var function)", body);
        Assert.Contains("PluginFunctionRules.Set(c.PluginFunctionLevels, plugin, function, chosen)", body);
    }

    [Fact]
    public void A_stored_value_in_two_cases_counts_as_the_strictest()
    {
        var map = new Dictionary<string, Dictionary<string, string>>
        {
            ["p"] = new() { ["F"] = "restricted", ["f"] = "admin", ["g"] = "restricted" },
            ["P"] = new() { ["G"] = "off" }
        };
        Assert.Equal("admin", PluginFunctionRules.Stored(map, "p", "f"));
        Assert.Equal("off", PluginFunctionRules.Stored(map, "p", "g"));
        // Junk next to a valid one does not hide it.
        map["p"]["x"] = "junk";
        Assert.Null(PluginFunctionRules.Stored(map, "p", "x"));
    }

    [Fact]
    public void Stored_levels_of_plugins_that_are_not_there_are_dropped_and_a_refused_uninstall_keeps_them()
    {
        var map = new Dictionary<string, Dictionary<string, string>>
        {
            ["here"] = new() { ["f"] = "restricted" },
            ["Gone"] = new() { ["f"] = "restricted" }
        };
        Assert.True(PluginFunctionRules.Prune(map, ["HERE", "other"]));
        Assert.Equal(["here"], map.Keys);
        Assert.False(PluginFunctionRules.Prune(map, ["here"]));
        Assert.False(PluginFunctionRules.Prune(null, ["here"]));
        var hud = Hud();
        Assert.Contains("if (gone) ForgetFunctionLevels(ids);", Body(hud, "public object UninstallPlugin("));
        Assert.Contains("plugins.Loaded.ContinueWith(_ => Ui(PruneFunctionLevels)", hud);
        Assert.Contains("PluginFunctionRules.Prune(c.PluginFunctionLevels, present)", Body(hud, "private void PruneFunctionLevels("));
    }

    [Fact]
    public void An_item_that_names_an_undeclared_function_is_logged_once_and_counts_as_admin()
    {
        var ctx = Context(PluginOrigin.FirstParty, out var manager);
        ctx.RegisterFunctions(Fn("known"));
        var original = Console.Error;
        var sink = new StringWriter();
        Console.SetError(sink);
        try
        {
            for (var i = 0; i < 5; i++) Assert.Equal(PluginLevel.Admin, manager.LevelOf("tool", "ghost\u0007-" + Guid.NewGuid().ToString("N")[..0] + "x"));
            Assert.Equal(PluginLevel.Admin, manager.LevelOf("tool", "known"));
        }
        finally
        {
            Console.SetError(original);
        }
        var lines = sink.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        Assert.Contains("never declared", lines[0]);
        Assert.DoesNotContain('\u0007', lines[0]);
    }

    // ---- Page visits (the parents' code for an admin page in a child's profile opens that page for a while) ----

    private sealed class Clock
    {
        public DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    }

    [Fact]
    public void A_visit_lets_the_same_page_through_for_at_most_five_minutes_and_nothing_else()
    {
        var clock = new Clock();
        var visits = new PluginVisits(() => clock.Now);
        Assert.False(visits.Active("tool", "setup", "kid"));
        visits.Begin("tool", "setup", "kid");
        Assert.True(visits.Active("tool", "setup", "kid"));
        // Bound to plugin, page and profile.
        Assert.False(visits.Active("tool", "other", "kid"));
        Assert.False(visits.Active("other", "setup", "kid"));
        Assert.False(visits.Active("tool", "setup", "sibling"));
        clock.Now += TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59);
        Assert.True(visits.Active("tool", "setup", "kid"));
        clock.Now += TimeSpan.FromSeconds(2);
        Assert.False(visits.Active("tool", "setup", "kid"));
        // Run out is run out: it does not come back.
        clock.Now -= TimeSpan.FromMinutes(1);
        Assert.False(visits.Active("tool", "setup", "kid"));
        Assert.Equal(TimeSpan.FromMinutes(5), PluginVisits.DefaultLifetime);
    }

    [Fact]
    public void A_visit_ends_when_the_page_is_left_and_on_a_profile_switch_the_lock_or_a_game_start()
    {
        var clock = new Clock();
        var visits = new PluginVisits(() => clock.Now);
        visits.Begin("tool", "a", "kid");
        visits.Begin("tool", "b", "kid");
        visits.End("tool", "a");
        Assert.False(visits.Active("tool", "a", "kid"));
        Assert.True(visits.Active("tool", "b", "kid"));
        // Profile switch, lock and game start all clear (the Hud calls Clear from ForgetUnlock and when the lock engages).
        visits.Clear();
        Assert.False(visits.Active("tool", "b", "kid"));
        var hud = Hud();
        Assert.Contains("EndPluginVisits();", Body(hud, "private void ForgetUnlock()"));
        Assert.Contains("if (locked) EndPluginVisits();", hud);
        // Leaving the page: the page tells the host, which only ever ends a visit.
        var ui = ReadSource(Path.Combine(RepoRoot(), "ui", "views", "plugin-page.js"));
        Assert.Contains("api.endPluginVisit?.(plugin.id, pageInfo.id)", ui);
        Assert.Contains("endVisit();", ui);
    }

    [Fact]
    public void A_reload_of_the_open_page_does_not_ask_again_and_the_rows_of_that_page_need_no_new_code()
    {
        var hud = Hud();
        var view = Body(hud, "private ParentTicket? RequirePageView(");
        // The reload finds the active visit before it asks for a grant.
        Assert.True(view.IndexOf("VisitActive(pluginId, page.Id)", StringComparison.Ordinal) is var visit and >= 0
            && visit < view.IndexOf("CheckParentGrant(\"pluginPage\"", StringComparison.Ordinal));
        // The page that opened with the code starts the visit after the grant is used up.
        var open = Body(hud, "public async Task<object> GetPluginPageAsync(");
        Assert.True(open.IndexOf("UseUp(pageTicket);", StringComparison.Ordinal) < open.IndexOf("BeginVisit(pluginId, pageId)", StringComparison.Ordinal));
        // Rows and steppers of the visited page skip their own code, but only a row of an admin function (CoveredByVisit); anything else asks per press.
        Assert.Contains("rowLevel == PluginLevel.Admin && VisitActive(pluginId, pageId)", hud);
        Assert.Contains("CoveredByVisit(pluginId, pageId, rowLevel)", Body(hud, "public async Task<object> ActivatePluginRowAsync("));
        Assert.Contains("CoveredByVisit(pluginId, pageId, rowLevelChange)", Body(hud, "public async Task<object> ChangePluginRowAsync("));
        // A guide entry has no page: it asks once per press.
        Assert.DoesNotContain("CoveredByVisit", Body(hud, "public Task<string?> RunQuickActionAsync("));
        // Visits exist only in a child's profile on the unlocked couch.
        Assert.Contains("!Locked && ChildNow && _visits.Active(", hud);
        // The page does not ask again when it only reloads.
        var ui = ReadSource(Path.Combine(RepoRoot(), "ui", "views", "plugin-page.js"));
        Assert.Contains("const readOnce = () => (data ? read() : withParentGrant(read));", ui);
    }

    [Fact]
    public void Grant_items_are_scoped_to_plugin_page_and_row_so_a_grant_cannot_cross_pages()
    {
        var now = DateTimeOffset.UtcNow;
        var grant = Grant(() => now);
        grant.Give("pluginRow", "tool/pageA/r1");
        Assert.False(grant.Fits("pluginRow", "tool/pageB/r1"));
        Assert.False(grant.Fits("pluginRow", "tool/pageA/r2"));
        Assert.True(grant.TryConsume("pluginRow", "tool/pageA/r1"));
        Assert.False(grant.TryConsume("pluginRow", "tool/pageA/r1"));
        var hud = Hud();
        Assert.Contains("$\"{pluginId}/{pageId}/{rowId}\"", Body(hud, "public async Task<object> ChangePluginRowAsync("));
    }

    [Fact]
    public void A_grant_that_is_gone_is_no_grant_one_code_never_pays_for_two_executions()
    {
        var hud = Hud();
        Assert.Contains("private void UseUp(ParentTicket? ticket) => Gate.Use(ticket);", hud);
        var gate = ReadSource(Path.Combine(RepoRoot(), "src", "CouchHud.Core", "Security", "ParentGate.cs"));
        var useUp = Body(gate, "public void Use(ParentTicket? ticket)");
        Assert.Contains("!grant.TryConsume(t.Action, t.Item)", useUp);
        Assert.Contains("throw new ParentCodeNeededException", useUp);
    }

    [Fact]
    public void The_prompt_names_the_plugin_and_the_function_in_both_languages()
    {
        foreach (var language in new[] { "de", "en" })
        {
            var text = CouchHud.Core.Localization.Strings.GetIn(language, "plugin.fn.needCode", "Plugin", "Funktion");
            Assert.Contains("Plugin", text, StringComparison.Ordinal);
            Assert.Contains("Funktion", text, StringComparison.Ordinal);
        }
        var hud = Hud();
        Assert.Contains("Strings.Get(\"plugin.fn.needCode\", name,", hud);
        var bridge = ReadSource(Path.Combine(RepoRoot(), "src", "CouchHud.Shell", "UiHost", "UiBridge.cs"));
        Assert.Contains("reason = ex.Reason", bridge);
    }

    // ---- Which plugin ids are still installed (the owner's choices survive a plugin that is off, waits for approval or failed) ----

    private PluginManager FolderManager(out string pluginsDir)
    {
        pluginsDir = Path.Combine(_dir, "plugins");
        Directory.CreateDirectory(pluginsDir);
        var manager = new PluginManager([(pluginsDir, true)], _dir, _ => true) { Trust = new PluginTrust(_dir, TrustKey) };
        // The list exists (an installation that has been used): a missing list next to plugin folders is treated as unknown, not as empty.
        manager.Trust!.ClearRemoved("seed");
        return manager;
    }

    private static void Folder(string pluginsDir, string folder, string? manifestId)
    {
        var dir = Path.Combine(pluginsDir, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), manifestId is null ? "{}" : "{\"id\":\"" + manifestId + "\",\"name\":\"x\"}");
    }

    [Fact]
    public void A_plugin_that_did_not_load_keeps_the_owners_choices_under_its_real_id()
    {
        var m = FolderManager(out var dir);
        // Folders whose names are not the plugin ids: switched off, waiting for approval, failed to load. None of them ever loaded here.
        Folder(dir, "Disabled.Folder", "disabledplugin");
        Folder(dir, "NeedsApproval.Folder", "approvalplugin");
        Folder(dir, "Broken.Folder", "brokenplugin");
        var installed = m.InstalledPluginIds();
        foreach (var id in new[] { "disabledplugin", "approvalplugin", "brokenplugin", "Disabled.Folder" }) Assert.Contains(id, installed);
        var map = new Dictionary<string, Dictionary<string, string>>
        {
            ["disabledplugin"] = new() { ["f"] = "off" },
            ["approvalplugin"] = new() { ["f"] = "off" },
            ["brokenplugin"] = new() { ["f"] = "off" },
            ["gone"] = new() { ["f"] = "restricted" }
        };
        Assert.True(PluginFunctionRules.Prune(map, installed));
        Assert.Equal(["approvalplugin", "brokenplugin", "disabledplugin"], map.Keys.Order());
        Assert.Equal("off", map["disabledplugin"]["f"]);
    }

    [Fact]
    public void A_plugin_whose_folder_is_gone_or_marked_removed_loses_its_choices_and_a_trust_entry_id_counts()
    {
        var m = FolderManager(out var dir);
        Folder(dir, "Still.There", null);
        Folder(dir, "Marked.Removed", "markedplugin");
        m.Trust!.MarkRemoved("Marked.Removed");
        var installed = m.InstalledPluginIds();
        Assert.DoesNotContain("markedplugin", installed);
        Assert.Contains("Still.There", installed);
        var map = new Dictionary<string, Dictionary<string, string>> { ["markedplugin"] = new() { ["f"] = "off" }, ["neverhere"] = new() { ["f"] = "off" } };
        Assert.True(PluginFunctionRules.Prune(map, installed));
        Assert.Empty(map);
        // Uninstalling forgets by every id the folder was known by, also for a plugin that never loaded.
        Folder(dir, "Old.Folder", "oldplugin");
        Assert.Contains("oldplugin", m.PluginIdsOf("Old.Folder"));
        Assert.Contains("Old.Folder", m.PluginIdsOf("Old.Folder"));
        var hud = Hud();
        Assert.Contains("plugins.PluginIdsToForget(folder)", Body(hud, "public object UninstallPlugin("));
        Assert.Contains("plugins.InstalledPluginIds()", Body(hud, "private void PruneFunctionLevels("));
    }

    // ---- Follow-ups: the grant of an install is used up before the work, reserved ids ----

    [Fact]
    public void The_code_for_an_install_is_used_up_before_the_download_or_the_unpacking_starts()
    {
        var hud = Hud();
        // A catalog install runs through CatalogInstallFlow (behaviour tested below): the gate callback checks and uses the grant up, once; the early
        // look at an added catalog's skin only checks. Nothing uses the grant up after the download.
        var catalog = Body(hud, "public async Task<object> InstallCatalogEntryAsync(");
        Assert.Contains("CatalogInstallFlow.RunAsync(extraSkin,", catalog);
        Assert.Contains("early: () => RequirePluginChange(PluginChange.Install, PluginOrigin.ThirdParty, null, id)", catalog);
        Assert.Contains("gate: request => UseUp(RequirePluginChange(", catalog);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(catalog, @"UseUp\("));
        Assert.DoesNotContain("UseUp", catalog[catalog.IndexOf("return new { message, restartNeeded, state };", StringComparison.Ordinal)..]);
        // A package from a file: after the gates and the lint, before the unpacking.
        var package = Body(hud, "public string InstallPackage(");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(package, @"UseUp\("));
        Assert.True(package.IndexOf("PluginLint.CheckZip(", StringComparison.Ordinal) < package.IndexOf("UseUp(ticket);", StringComparison.Ordinal));
        Assert.True(package.IndexOf("UseUp(ticket);", StringComparison.Ordinal) < package.IndexOf("PackageInstaller.Install(", StringComparison.Ordinal));
        // And it still refuses when the grant is gone (fail closed): one code, one install.
        Assert.Contains("private void UseUp(ParentTicket? ticket) => Gate.Use(ticket);", hud);
    }

    [Fact]
    public void A_folder_of_someone_else_that_claims_a_reserved_id_neither_keeps_the_owners_levels_alive_nor_wipes_them()
    {
        var m = FolderManager(out var dir);
        // A folder that is not CouchHUD's own (no trust entry, or a third party's) calls itself "homeassistant", by name or in its plugin.json.
        Folder(dir, "homeassistant", "homeassistant");
        Folder(dir, "Impostor", "homeassistant");
        Assert.DoesNotContain("homeassistant", m.PluginIdsOf("Impostor"));
        Assert.DoesNotContain("homeassistant", m.PluginIdsOf("homeassistant"));
        Assert.DoesNotContain("homeassistant", m.InstalledPluginIds());
        // So the real one's choices are not kept alive by the impostor (the real one is not installed here) ...
        var map = new Dictionary<string, Dictionary<string, string>> { ["homeassistant"] = new() { ["actions"] = "off" } };
        Assert.True(PluginFunctionRules.Prune(map, m.InstalledPluginIds()));
        // ... and removing the impostor does not name the id to forget.
        Assert.DoesNotContain("homeassistant", m.PluginIdsToForget("Impostor"));
        // CouchHUD's own plugin (recorded as first party in the trust list) does hold the id.
        var real = Path.Combine(dir, "CouchHud.Plugin.HomeAssistant");
        Folder(dir, "CouchHud.Plugin.HomeAssistant", "homeassistant");
        m.Trust!.Trust("CouchHud.Plugin.HomeAssistant", real, new PluginTrustEntry { Hash = new string('a', 64), Origin = PluginOrigin.FirstParty, PluginId = "homeassistant" });
        Assert.Contains("homeassistant", m.InstalledPluginIds());
        Assert.Contains("homeassistant", m.PluginIdsToForget("CouchHud.Plugin.HomeAssistant"));
        // Removing the impostor still leaves the real one's id alone.
        Assert.DoesNotContain("homeassistant", m.PluginIdsToForget("Impostor"));
    }

    [Fact]
    public void An_id_that_another_installed_folder_also_holds_is_not_forgotten()
    {
        var m = FolderManager(out var dir);
        Folder(dir, "One", "sharedplugin");
        Folder(dir, "Two", "sharedplugin");
        Assert.DoesNotContain("sharedplugin", m.PluginIdsToForget("One"));
        Assert.Contains("One", m.PluginIdsToForget("One"));
        // The other one is marked for removal: now the first holds the id alone.
        m.Trust!.MarkRemoved("Two");
        Assert.Contains("sharedplugin", m.PluginIdsToForget("One"));
        Assert.Contains("plugins.PluginIdsToForget(folder)", Body(Hud(), "public object UninstallPlugin("));
    }

    // ---- Behaviour: one grant, checked and used up once, in a catalog install ----

    private static (ParentGate Gate, ParentGrant Grant, int[] Unlocks) GateWith(bool parentsCode, Func<DateTimeOffset>? now = null)
    {
        var grant = new ParentGrant(now ?? (() => DateTimeOffset.UtcNow), TimeSpan.FromSeconds(90));
        var unlocks = new int[1];
        return (new ParentGate(grant, () => parentsCode, () => unlocks[0]++), grant, unlocks);
    }

    // The gate of the Hud does not look into the request; the catalog service's own tests cover what it contains.
    private static PluginInstallRequest Request(CatalogKind kind, bool extra) => null!;

    /// <summary>The install as the Hud runs it: the same early look and the same gate (check, then use up) over one grant.</summary>
    private static Task<int> RunInstall(ParentGate gate, bool extraSkin, CatalogKind kind, Action<Func<PluginInstallRequest, bool>>? asks = null, bool extra = true)
    {
        const string action = "pluginInstall";
        return CatalogInstallFlow.RunAsync(extraSkin,
            early: () => gate.Check(action, "x"),
            gate: _ => gate.Use(gate.Check(action, "x")),
            install: approve =>
            {
                // The catalog service asks the approval once for a third-party skin or a plugin, and not at all for CouchHUD's own skin.
                if (asks is null) approve(Request(kind, extra)); else asks(approve);
                return Task.FromResult(1);
            });
    }

    [Fact]
    public async Task A_third_party_skin_with_one_parents_code_installs_and_the_grant_is_used_exactly_once()
    {
        var (gate, grant, _) = GateWith(parentsCode: true);
        grant.Give("pluginInstall", "x");
        Assert.Equal(1, await RunInstall(gate, extraSkin: true, CatalogKind.Skin));
        // Used up exactly once: gone now, and a second install needs a second code.
        Assert.False(grant.Fits("pluginInstall", "x"));
        await Assert.ThrowsAsync<ParentCodeNeededException>(() => RunInstall(gate, extraSkin: true, CatalogKind.Skin));
    }

    [Fact]
    public async Task A_third_party_skin_without_a_grant_is_refused_before_anything_is_asked_or_downloaded()
    {
        var (gate, _, _) = GateWith(parentsCode: true);
        var asked = false;
        var ex = await Assert.ThrowsAsync<ParentCodeNeededException>(() => RunInstall(gate, extraSkin: true, CatalogKind.Skin, asks: _ => asked = true));
        Assert.Equal("pluginInstall", ex.Action);
        Assert.False(asked);
    }

    [Fact]
    public async Task A_plugin_and_a_first_party_skin_use_the_grant_once_or_not_at_all()
    {
        var (gate, grant, _) = GateWith(parentsCode: true);
        // A plugin: no early look, the callback checks and uses the grant up once.
        grant.Give("pluginInstall", "x");
        Assert.Equal(1, await RunInstall(gate, extraSkin: false, CatalogKind.Plugin));
        Assert.False(grant.Fits("pluginInstall", "x"));
        await Assert.ThrowsAsync<ParentCodeNeededException>(() => RunInstall(gate, extraSkin: false, CatalogKind.Plugin));
        // CouchHUD's own skin: the catalog service never asks the approval, so a grant lying around is not touched (and none is needed here).
        grant.Give("pluginInstall", "x");
        Assert.Equal(1, await RunInstall(gate, extraSkin: false, CatalogKind.Skin, asks: _ => { }));
        Assert.True(grant.Fits("pluginInstall", "x"));
    }

    [Fact]
    public async Task Without_a_parents_code_the_unlock_is_the_gate_and_a_grant_that_ran_out_refuses()
    {
        var (gate, _, unlocks) = GateWith(parentsCode: false);
        Assert.Equal(1, await RunInstall(gate, extraSkin: true, CatalogKind.Skin));
        Assert.True(unlocks[0] >= 2); // the early look and the gate both ask for the unlock, nothing is consumed
        // A grant that ran out before the gate (a slow earlier step) refuses instead of installing.
        var clock = new Clock();
        var (slow, slowGrant, _) = GateWith(parentsCode: true, () => clock.Now);
        slowGrant.Give("pluginInstall", "x");
        var ticket = slow.Check("pluginInstall", "x");
        clock.Now += TimeSpan.FromSeconds(91);
        Assert.Throws<ParentCodeNeededException>(() => slow.Use(ticket));
    }

    // ---- The trust list that cannot be read: nothing is forgotten on that basis ----

    [Fact]
    public void An_unreadable_trust_list_means_nothing_is_pruned_and_nothing_is_forgotten()
    {
        var m = FolderManager(out var dir);
        Folder(dir, "CouchHud.Plugin.HomeAssistant", "homeassistant");
        m.Trust!.Trust("CouchHud.Plugin.HomeAssistant", Path.Combine(dir, "CouchHud.Plugin.HomeAssistant"),
            new PluginTrustEntry { Hash = new string('a', 64), Origin = PluginOrigin.FirstParty, PluginId = "homeassistant" });
        Assert.Contains("homeassistant", m.InstalledPluginIds());
        // Damaged (or locked, or tampered with): the origin of the folder is unknown, so reserved ids cannot be told from CouchHUD's own.
        File.WriteAllText(Path.Combine(_dir, "plugin-trust.json"), "{ not the list");
        Assert.False(m.Trust.Readable);
        Assert.Throws<IOException>(() => m.InstalledPluginIds());
        Assert.Throws<IOException>(() => m.PluginIdsToForget("CouchHud.Plugin.HomeAssistant"));
        // The Hud keeps every choice then (the owner's "nobody" does not fall back to the plugin's weaker suggestion).
        var hud = Hud();
        var prune = Body(hud, "private void PruneFunctionLevels(");
        Assert.Contains("plugins.InstalledPluginIds()", prune);
        Assert.Contains("catch (Exception ex) when (ex is InvalidOperationException or IOException", prune);
        var uninstall = Body(hud, "public object UninstallPlugin(");
        Assert.Contains("catch (IOException)", uninstall);
        Assert.True(uninstall.IndexOf("ids = [];", StringComparison.Ordinal) > uninstall.IndexOf("catch (IOException)", StringComparison.Ordinal));
        // No store at all is a fresh install, not a damaged one.
        File.Delete(Path.Combine(_dir, "plugin-trust.json"));
        Assert.True(m.Trust.Readable);
    }

    [Fact]
    public void A_deleted_trust_list_next_to_plugin_folders_prunes_and_forgets_nothing()
    {
        var m = FolderManager(out var dir);
        Folder(dir, "CouchHud.Plugin.HomeAssistant", "homeassistant");
        m.Trust!.Trust("CouchHud.Plugin.HomeAssistant", Path.Combine(dir, "CouchHud.Plugin.HomeAssistant"),
            new PluginTrustEntry { Hash = new string('a', 64), Origin = PluginOrigin.FirstParty, PluginId = "homeassistant" });
        Assert.Contains("homeassistant", m.InstalledPluginIds());
        // Somebody deletes the list: it now reads as an empty one, the first-party origin of the folder is gone, and the reserved id would drop.
        File.Delete(Path.Combine(_dir, "plugin-trust.json"));
        Assert.True(m.Trust.Readable);
        Assert.False(m.Trust.HasStore);
        Assert.Throws<IOException>(() => m.InstalledPluginIds());
        Assert.Throws<IOException>(() => m.PluginIdsToForget("CouchHud.Plugin.HomeAssistant"));
        // A fresh installation (no list and no plugin folders) is not a damaged one.
        var fresh = new PluginManager([(Path.Combine(_dir, "empty"), true)], _dir, _ => true) { Trust = m.Trust };
        Assert.Empty(fresh.InstalledPluginIds());
    }
}
