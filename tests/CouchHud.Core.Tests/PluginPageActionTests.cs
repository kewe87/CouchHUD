using CouchHud.Core.Guard;
using CouchHud.Core.Plugins;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>
/// Page actions (SDK 4): a plugin page shows up to three actions as button hints in the footer. The data comes from a plugin, so the host
/// checks the button, the count, the label and the target, and a press passes the gate of a row press.
/// </summary>
public sealed class PluginPageActionTests
{
    private static readonly Func<string, bool> Exists = id => id is "devices" or "connection";

    private static IReadOnlyList<CheckedPageAction> Check(bool firstParty, params PluginPageAction[] actions) => PluginPageActions.Check(actions, firstParty, Exists, 4);

    [Fact]
    public void A_valid_action_becomes_a_view_with_the_button_a_cleaned_label_and_no_target()
    {
        var views = Check(false, PluginPageAction.Open(PluginPageButton.X, "  Choose   devices ", "devices"), PluginPageAction.Run(PluginPageButton.Y, "Anheften", "pin"))
            .Select(a => a.View).ToList();
        Assert.Equal([new PluginActionView("X", "Choose devices", "link"), new PluginActionView("Y", "Anheften", "action")], views);
        // The view carries nothing a page could press with: no page id, no action id, no function.
        Assert.Equal(["Button", "Kind", "Label"], typeof(PluginActionView).GetProperties().Select(p => p.Name).Order().ToArray());
    }

    [Fact]
    public void Only_the_free_buttons_exist_and_a_forged_one_is_refused()
    {
        Assert.Equal(["X", "Y"], Enum.GetNames<PluginPageButton>());
        Assert.Empty(Check(false, new PluginPageAction((PluginPageButton)2, "A") { ActionId = "a" }));
        Assert.Empty(Check(false, new PluginPageAction((PluginPageButton)(-1), "A") { ActionId = "a" }));
        var actions = Check(false, PluginPageAction.Run(PluginPageButton.X, "A", "a"));
        foreach (var bad in new[] { "A", "B", "LB", "RB", "Guide", "x", "0", "1", "X ", "", null, "X,Y", "start" })
            Assert.Null(PluginPageActions.Find(actions, bad));
        Assert.NotNull(PluginPageActions.Find(actions, "X"));
        Assert.Null(PluginPageActions.Find(actions, "Y"));
    }

    [Fact]
    public void Duplicate_buttons_and_extra_actions_are_dropped_the_first_wins()
    {
        var list = Check(false, PluginPageAction.Run(PluginPageButton.X, "First", "one"), PluginPageAction.Run(PluginPageButton.X, "Second", "two"), PluginPageAction.Run(PluginPageButton.Y, "Third", "three"));
        Assert.Equal(["First", "Third"], list.Select(a => a.Label));
        // The cap holds whatever the list looks like (the host never shows more than the limit).
        var many = Enumerable.Range(0, 20).Select(i => PluginPageAction.Run(i % 2 == 0 ? PluginPageButton.X : PluginPageButton.Y, "L" + i, "id" + i)).ToArray();
        Assert.True(Check(false, many).Count <= PluginPageAction.MaxPerPage);
        Assert.Equal(3, PluginPageAction.MaxPerPage);
        Assert.Empty(PluginPageActions.Check(null, false, Exists, 4));
        Assert.Empty(PluginPageActions.Check([null!], false, Exists, 4));
    }

    [Fact]
    public void The_target_is_exactly_one_of_a_page_of_the_same_plugin_or_an_action_id_and_never_an_address()
    {
        Assert.Empty(Check(false, new PluginPageAction(PluginPageButton.X, "None")));
        Assert.Empty(Check(false, new PluginPageAction(PluginPageButton.X, "Both") { PageId = "devices", ActionId = "a" }));
        // A page the plugin does not have (another plugin's page, an address, a path) is no target.
        foreach (var page in new[] { "other.plugin/home", "https://example.com", "http://127.0.0.1/", "file:///c:/x", "javascript:alert(1)", "//evil", "..\\x", "missing" })
            Assert.Empty(Check(false, PluginPageAction.Open(PluginPageButton.X, "Go", page)));
        Assert.Empty(Check(false, PluginPageAction.Run(PluginPageButton.X, "Long", new string('a', PluginRows.MaxId + 1))));
        Assert.Empty(Check(false, new PluginPageAction(PluginPageButton.X, "Fn") { ActionId = "a", Function = new string('f', 500) }));
        Assert.Single(Check(false, PluginPageAction.Open(PluginPageButton.X, "Go", "connection")));
    }

    [Theory]
    [InlineData("\U0001F525 Fire", "Fire")]
    [InlineData("A\u202Eb\u200Dc", "Abc")]
    [InlineData("<b>x</b>", "<b>x</b>")]
    public void The_label_is_cleaned_like_a_page_title(string raw, string expected)
    {
        Assert.Equal(expected, Check(false, PluginPageAction.Run(PluginPageButton.X, raw, "a"))[0].Label);
    }

    [Fact]
    public void The_label_is_short_and_empty_or_deceptive_labels_drop_the_action()
    {
        Assert.True(Check(false, PluginPageAction.Run(PluginPageButton.X, new string('w', 500), "a"))[0].Label.Length <= PluginPageAction.MaxLabel);
        foreach (var raw in new[] { "", "   ", "\U0001F525\U0001F525", "\u200B\u200D", null! })
            Assert.Empty(Check(false, PluginPageAction.Run(PluginPageButton.X, raw, "a")));
        // A third party cannot dress an action as a core screen or mix scripts; CouchHUD's own plugins can.
        Assert.Empty(Check(false, PluginPageAction.Run(PluginPageButton.X, "Einstellungen", "a")));
        Assert.Empty(Check(false, PluginPageAction.Run(PluginPageButton.X, "\u0415instellungen", "a")));
        Assert.Single(Check(true, PluginPageAction.Run(PluginPageButton.X, "Einstellungen", "a")));
    }

    // ---- the press goes through the row gate ----

    private static PluginRow RowOf(PluginPageAction action, bool firstParty = false) => Check(firstParty, action)[0].Row;

    [Fact]
    public void A_third_party_action_is_hidden_on_the_locked_couch_and_a_first_party_one_only_when_it_is_marked()
    {
        var plain = PluginPageAction.Run(PluginPageButton.X, "Do", "do");
        var marked = new PluginPageAction(PluginPageButton.X, "Do") { ActionId = "do", AllowedWhileLocked = true };
        Assert.Equal("lock.unlockFirst", PluginPolicy.MayPress(true, PluginOrigin.ThirdParty, RowOf(marked), false, "p").Refused);
        Assert.Equal("lock.unlockFirst", PluginPolicy.MayPress(true, PluginOrigin.Local, RowOf(marked), false, "p").Refused);
        Assert.Equal("lock.unlockFirst", PluginPolicy.MayPress(true, PluginOrigin.FirstParty, RowOf(plain, true), false, "p").Refused);
        Assert.Null(PluginPolicy.MayPress(true, PluginOrigin.FirstParty, RowOf(marked, true), false, "p").Refused);
        Assert.Null(PluginPolicy.MayPress(false, PluginOrigin.ThirdParty, RowOf(plain), false, "p").Refused);
        // A link opens a page and changes nothing: it never asks for the code.
        Assert.False(PluginPolicy.MayPress(false, PluginOrigin.ThirdParty, RowOf(PluginPageAction.Open(PluginPageButton.X, "Go", "devices")), true, "p").ParentCode);
    }

    [Fact]
    public void The_function_level_hides_an_action_for_a_child_and_asks_the_parents_code_for_an_admin_one()
    {
        var tagged = new PluginPageAction(PluginPageButton.Y, "Anheften") { ActionId = "pin", Function = "pins" };
        var row = RowOf(tagged);
        Assert.Equal("pins", row.Function);
        Assert.Equal("plugin.fn.off", PluginPolicy.MayPress(false, PluginOrigin.ThirdParty, row, true, "p", PluginLevel.Off, childProfile: true).Refused);
        Assert.Equal("plugin.fn.off", PluginPolicy.MayPress(false, PluginOrigin.FirstParty, row, true, "p", PluginLevel.Off, childProfile: false).Refused);
        Assert.Equal("lock.unlockFirst", PluginPolicy.MayPress(true, PluginOrigin.ThirdParty, row, true, "p", PluginLevel.Admin, childProfile: true).Refused);
        var admin = PluginPolicy.MayPress(false, PluginOrigin.ThirdParty, row, true, "p", PluginLevel.Admin, childProfile: true);
        Assert.Null(admin.Refused);
        Assert.True(admin.ParentCode);
        // Restricted: everyone, also on the locked couch, with no code (the owner chose that).
        var restricted = PluginPolicy.MayPress(true, PluginOrigin.ThirdParty, row, true, "p", PluginLevel.Restricted, childProfile: true);
        Assert.Null(restricted.Refused);
        Assert.False(restricted.ParentCode);
    }

    [Fact]
    public void A_third_party_action_that_changes_something_always_needs_the_code_under_parental_controls_whatever_it_says()
    {
        var says = new PluginPageAction(PluginPageButton.X, "Do") { ActionId = "do", RequiresParentGrant = false };
        Assert.True(PluginPolicy.MayPress(false, PluginOrigin.ThirdParty, RowOf(says), true, "p").ParentCode);
        Assert.False(PluginPolicy.MayPress(false, PluginOrigin.ThirdParty, RowOf(says), false, "p").ParentCode);
        Assert.False(PluginPolicy.MayPress(false, PluginOrigin.FirstParty, RowOf(says, true), true, "p").ParentCode);
        var asks = new PluginPageAction(PluginPageButton.X, "Do") { ActionId = "do", RequiresParentGrant = true };
        Assert.True(PluginPolicy.MayPress(false, PluginOrigin.FirstParty, RowOf(asks, true), true, "p").ParentCode);
    }

    // ---- SDK 4 is required ----

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_plugin_that_declared_less_than_sdk_4_has_no_actions_for_any_origin(int sdk)
    {
        var action = PluginPageAction.Run(PluginPageButton.X, "Do", "do");
        Assert.Empty(PluginPageActions.Check([action], true, Exists, sdk));
        Assert.Empty(PluginPageActions.Check([action], false, Exists, sdk));
        Assert.Single(PluginPageActions.Check([action], false, Exists, 4));
        Assert.Single(PluginPageActions.Check([action], false, Exists, 5));
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("couchhud-actions-").FullName;

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void The_host_knows_the_sdk_the_plugin_declared_and_both_hud_paths_use_it()
    {
        using var dir = new TempDir();
        var m = new PluginManager([(System.IO.Path.Combine(dir.Path, "plugins"), true)], dir.Path, _ => true);
        m.ContextFor("old", new PluginManager.PluginGrant("old", PluginOrigin.FirstParty, new HashSet<string> { PluginCapabilities.Pages }, 3), markLoaded: true);
        m.ContextFor("new", new PluginManager.PluginGrant("new", PluginOrigin.FirstParty, new HashSet<string> { PluginCapabilities.Pages }, 4), markLoaded: true);
        Assert.Equal(3, m.SdkOf("old"));
        Assert.Equal(4, m.SdkOf("new"));
        Assert.Equal(1, m.SdkOf("not-loaded"));
        var hud = ReadSource(System.IO.Path.Combine(RepoRoot(), "src", "CouchHud.Shell", "Hud.cs"));
        // Shown and pressed both go through the one helper that passes the declared SDK.
        Assert.Contains("plugins.SdkOf(pluginId)", Body(hud, "private IReadOnlyList<CheckedPageAction> CheckedPageActions("));
        Assert.Contains("CheckedPageActions(pluginId, page)", Body(hud, "private IReadOnlyList<CheckedPageAction> VisiblePageActions("));
        Assert.Contains("PluginPageActions.Find(CheckedPageActions(pluginId, page), button)", Body(hud, "public async Task<object> ActivatePluginActionAsync("));
    }

    [Fact]
    public void A_paused_plugin_has_no_page_so_its_actions_are_neither_shown_nor_pressed()
    {
        using var dir = new TempDir();
        var offline = false;
        var m = new PluginManager([(System.IO.Path.Combine(dir.Path, "plugins"), true)], dir.Path, _ => true) { OfflineNow = () => offline };
        var caps = new HashSet<string> { PluginCapabilities.Pages, PluginCapabilities.NetworkInternet };
        var ctx = m.ContextFor("net", new PluginManager.PluginGrant("net", PluginOrigin.FirstParty, caps, 4), markLoaded: true);
        ctx.RegisterPage(new PluginPage { Id = "pg", Title = "P", Rows = _ => Task.FromResult<IReadOnlyList<PluginRow>>([]), Actions = [PluginPageAction.Run(PluginPageButton.X, "Do", "do")] });
        Assert.NotNull(m.FindPage("net", "pg"));
        offline = true;
        // Both Hud paths (page read and press) start with the fenced FindPage: no page, so no actions, and a press is refused as "page not found".
        Assert.Null(m.FindPage("net", "pg"));
        var hud = ReadSource(System.IO.Path.Combine(RepoRoot(), "src", "CouchHud.Shell", "Hud.cs"));
        Assert.Contains("plugins.FindPage(pluginId, pageId) ?? throw", Body(hud, "public async Task<object> ActivatePluginActionAsync("));
        Assert.Contains("plugins.FindPage(pluginId, pageId) ?? throw", Body(hud, "public async Task<object> GetPluginPageAsync("));
        // A link target on a paused plugin does not exist either (the existence check is the same fenced lookup).
        Assert.Empty(PluginPageActions.Check([PluginPageAction.Open(PluginPageButton.X, "Go", "pg")], false, id => m.FindPage("net", id) is not null, 4));
        offline = false;
        Assert.NotNull(m.FindPage("net", "pg"));
    }

    // ---- labels that look like system functions ----

    [Theory]
    [InlineData("Zurück")]
    [InlineData("zurueck")]
    [InlineData("BACK")]
    [InlineData("Ausschalten")]
    [InlineData("Power off")]
    [InlineData("power-off")]
    [InlineData("Eltern-Code")]
    [InlineData("Parents' code")]
    [InlineData("Menü")]
    [InlineData("Menu")]
    [InlineData("Startseite")]
    [InlineData("Home")]
    [InlineData("Einstellungen")]
    [InlineData("Settings")]
    [InlineData("Sеttings")]
    [InlineData("Ｂack")]
    public void A_third_party_action_cannot_be_named_like_a_system_function(string label)
    {
        Assert.Empty(Check(false, PluginPageAction.Run(PluginPageButton.X, label, "a")));
    }

    [Theory]
    [InlineData("Back!")]
    [InlineData("Zurück!!")]
    [InlineData("|Home|")]
    [InlineData("Exit$")]
    [InlineData("@Menu")]
    [InlineData("Settings...")]
    public void Signs_around_a_system_name_do_not_get_it_past_the_deny_list(string label)
    {
        Assert.Empty(Check(false, PluginPageAction.Run(PluginPageButton.X, label, "a")));
    }

    [Fact]
    public void System_names_stay_free_for_CouchHuds_own_plugins_and_ordinary_labels_pass()
    {
        Assert.Single(Check(true, PluginPageAction.Run(PluginPageButton.X, "Zurück", "a")));
        foreach (var ok in new[] { "Choose devices", "Pin to Guide menu", "Geräte wählen", "Home Assistant verbinden", "Back up scenes", "Reihenfolge" })
            Assert.Single(Check(false, PluginPageAction.Run(PluginPageButton.X, ok, "a")));
    }

    // ---- the install check ----

    [Fact]
    public void The_lint_flags_page_actions_in_code_that_declares_less_than_sdk_4()
    {
        // This test assembly itself uses PluginPageAction and the Actions setter, like a plugin would.
        var image = File.ReadAllBytes(typeof(PluginPageActionTests).Assembly.Location);
        var found = PluginLint.Scan(image, "t.dll");
        Assert.Contains(found, f => f.Capability == PluginLint.SdkActions);
        Assert.Contains(PluginLint.Evaluate(found, PluginOrigin.FirstParty, [], 3), f => f.Capability == PluginLint.SdkActions);
        Assert.Contains(PluginLint.Evaluate(found, PluginOrigin.ThirdParty, [], 1), f => f.Capability == PluginLint.SdkActions);
        Assert.DoesNotContain(PluginLint.Evaluate(found, PluginOrigin.FirstParty, [], 4), f => f.Capability == PluginLint.SdkActions);
        // Unknown SDK (a caller that has none): not flagged, the host still ignores the actions at run time.
        Assert.DoesNotContain(PluginLint.Evaluate(found, PluginOrigin.FirstParty, []), f => f.Capability == PluginLint.SdkActions);
    }

    // ---- the catalog and the SDK version ----

    private const string Sha = "0000000000000000000000000000000000000000000000000000000000000000";

    [Fact]
    public void A_catalog_entry_for_a_newer_sdk_is_not_compatible_and_says_why()
    {
        var newer = SdkVersion.Current + 1;
        var entries = CatalogParser.Parse($$"""
            [{"id":"newer","name":"Newer","type":"plugin","version":"1.0.0","sdk":{{newer}},"download":"https://x.example/n.zip","sha256":"{{Sha}}"},
             {"id":"ok","name":"Ok","type":"plugin","version":"1.0.0","sdk":{{SdkVersion.Current}},"download":"https://x.example/o.zip","sha256":"{{Sha}}"},
             {"id":"none","name":"None","type":"plugin","version":"1.0.0","download":"https://x.example/p.zip","sha256":"{{Sha}}"},
             {"id":"odd","name":"Odd","type":"plugin","version":"1.0.0","sdk":"four","download":"https://x.example/q.zip","sha256":"{{Sha}}"},
             {"id":"frac","name":"Frac","type":"plugin","version":"1.0.0","sdk":4.5,"download":"https://x.example/f.zip","sha256":"{{Sha}}"},
             {"id":"neg","name":"Neg","type":"plugin","version":"1.0.0","sdk":-1,"download":"https://x.example/g.zip","sha256":"{{Sha}}"},
             {"id":"huge","name":"Huge","type":"plugin","version":"1.0.0","sdk":1001,"download":"https://x.example/h.zip","sha256":"{{Sha}}"}]
            """, "s");
        Assert.True(SemVer.TryParse("9.0.0", out var app));
        var byId = entries.ToDictionary(e => e.Id);
        Assert.False(CatalogRules.IsCompatible(byId["newer"], app));
        Assert.Equal(CatalogStatus.Unavailable, CatalogRules.StatusOf(byId["newer"], false, null, app));
        Assert.Contains(newer.ToString(), CatalogRules.Reason(byId["newer"], app));
        Assert.True(CatalogRules.IsCompatible(byId["ok"], app));
        Assert.True(CatalogRules.IsCompatible(byId["none"], app));
        // Present but malformed counts as newer than any CouchHUD; only a missing value is "no statement".
        Assert.Null(byId["none"].Sdk);
        Assert.Equal(int.MaxValue, byId["odd"].Sdk);
        Assert.False(CatalogRules.IsCompatible(byId["odd"], app));
        Assert.Equal(CatalogStatus.Unavailable, CatalogRules.StatusOf(byId["odd"], false, null, app));
        foreach (var bad in new[] { "frac", "neg", "huge" }) Assert.False(CatalogRules.IsCompatible(byId[bad], app));
        Assert.Null(CatalogRules.Reason(byId["ok"], app));
        Assert.True(CatalogRules.NeedsNewerSdk(newer));
        Assert.False(CatalogRules.NeedsNewerSdk(SdkVersion.Current));
        Assert.False(CatalogRules.NeedsNewerSdk(null));
    }

    [Fact]
    public async Task Installing_an_entry_for_a_newer_sdk_is_refused_before_anything_is_downloaded()
    {
        var newer = SdkVersion.Current + 1;
        var entry = CatalogParser.Parse($$"""[{"id":"newer","name":"Newer","type":"plugin","version":"1.0.0","sdk":{{newer}},"download":"https://x.example/n.zip","sha256":"{{Sha}}"}]""", "s")[0];
        using var dir = new TempDir();
        using var http = new HttpClient();
        var service = new CatalogService(http, dir.Path, () => [], () => false);
        Assert.True(SemVer.TryParse("9.0.0", out var app));
        var ex = await Assert.ThrowsAsync<CatalogException>(() => service.InstallAsync(entry, app));
        Assert.Contains(newer.ToString(), ex.Message);
    }

    [Fact]
    public void The_signed_statements_sdk_is_checked_against_the_running_one_when_it_is_verified()
    {
        var core = ReadSource(System.IO.Path.Combine(RepoRoot(), "src", "CouchHud.Core", "Plugins", "CatalogService.cs"));
        var verify = Body(core, "public VerifiedStatement? VerifyEntry(");
        var check = verify.IndexOf("CatalogRules.NeedsNewerSdk(verified.Statement.Sdk)", StringComparison.Ordinal);
        Assert.True(check >= 0 && check > verify.IndexOf("PackageStatements.Verify(", StringComparison.Ordinal));
        var shell = ReadSource(System.IO.Path.Combine(RepoRoot(), "src", "CouchHud.Shell", "Services", "CatalogManager.cs"));
        Assert.Contains("CatalogRules.NeedsNewerSdk(verified?.Statement.Sdk)", shell);
    }

    // ---- the host applies the same path as a row press (source pins, like the other plugin gates) ----

    private static string ReadSource(string path) => File.ReadAllText(path).Replace("\r\n", "\n");

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CouchHud.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repo root not found");
    }

    private static string Body(string source, string signature)
    {
        var at = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(at >= 0, signature);
        var rest = source[at..];
        var end = rest.IndexOf("\n    }\n", StringComparison.Ordinal);
        return end < 0 ? rest : rest[..end];
    }

    [Fact]
    public void The_host_presses_an_action_through_the_row_gate_and_looks_it_up_fresh()
    {
        var hud = ReadSource(Path.Combine(RepoRoot(), "src", "CouchHud.Shell", "Hud.cs"));
        var press = Body(hud, "public async Task<object> ActivatePluginActionAsync(");
        Assert.Contains("RequirePageView(pluginId, page);", press);
        // The action comes from the page's registered list, checked again, found by its button; nothing else the page sent is trusted.
        Assert.Contains("PluginPageActions.Find(CheckedPageActions(pluginId, page), button)", press);
        Assert.Contains("PluginPolicy.MayPress(locked, plugins.OriginOf(pluginId), row", press);
        Assert.Contains("plugins.LevelOf(pluginId, row?.Function ?? page.Function)", press);
        // Refusals, then the config-fault check, then the grant, then the plugin's code; one use per press.
        var gate = press.IndexOf("var decision = PluginPolicy.MayPress(", StringComparison.Ordinal);
        var fault = press.IndexOf("RefuseWhileConfigFaulted();", StringComparison.Ordinal);
        var grant = press.IndexOf("UseUp(CheckParentGrant(", StringComparison.Ordinal);
        var call = press.IndexOf("page.Activate(actionId", StringComparison.Ordinal);
        Assert.True(gate >= 0 && fault > gate && grant > fault && call > grant, "gate, config fault, grant, then the plugin");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(press, @"UseUp\("));
        Assert.Contains("rowLevel != PluginLevel.Restricted", press);
        // A link is opened by the host (a page of the same plugin, viewed with the same check), the plugin's code is not called for it.
        Assert.Contains("PluginRows.LinkTarget(row, id => plugins.FindPage(pluginId, id) is not null)", press);
        Assert.Contains("RequirePageView(pluginId, next);", press);
    }

    [Fact]
    public void The_page_shows_only_the_actions_the_press_gate_would_let_through()
    {
        var hud = ReadSource(Path.Combine(RepoRoot(), "src", "CouchHud.Shell", "Hud.cs"));
        var visible = Body(hud, "private IReadOnlyList<CheckedPageAction> VisiblePageActions(");
        Assert.Contains("CheckedPageActions(pluginId, page)", visible);
        Assert.Contains("PluginPolicy.MayPress(locked, origin, a.Row", visible);
        Assert.Contains("ChildNow", visible);
        Assert.Contains("actions = VisiblePageActions(pluginId, page, locked).Select(a => a.View)", Body(hud, "public async Task<object> GetPluginPageAsync("));
    }

    [Fact]
    public void The_bridge_lets_the_press_through_the_lock_only_to_be_decided_by_the_host_and_the_area_list_knows_it()
    {
        Assert.Contains("activatePluginAction", LockPolicy.OpenWhileLocked);
        Assert.Contains("activatePluginAction", ReadSource(Path.Combine(RepoRoot(), "src", "CouchHud.Core", "Config", "FeatureAreas.cs")));
        var bridge = ReadSource(Path.Combine(RepoRoot(), "src", "CouchHud.Shell", "UiHost", "UiBridge.cs"));
        Assert.Contains("hud.ActivatePluginActionAsync(StringArg(0), StringArg(1), StringArg(2))", bridge);
    }

    [Fact]
    public void An_old_plugin_without_actions_is_unchanged()
    {
        var page = new PluginPage { Id = "p", Title = "T", Rows = _ => Task.FromResult<IReadOnlyList<PluginRow>>([]) };
        Assert.Empty(page.Actions);
        Assert.Empty(PluginPageActions.Check(page.Actions, false, Exists, 4));
    }
}
