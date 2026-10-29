using CouchHud.Core.Guard;
using CouchHud.Core.Plugins;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>
/// A settings entry that opens one of the plugin's own pages (SettingKind.Page, SDK 4): the host validates the page id against the pages the
/// plugin registered, cleans the label like a page action, decides by the setting gate (function, "settings", admin) and never lets a plugin
/// reach another plugin's page or a core page through it.
/// </summary>
public sealed class PluginSettingPageTests
{
    private static readonly Func<string, bool> Exists = id => id is "devices" or "order" or "guide";

    private static PluginSetting Entry(string? page = "devices", string label = "Choose devices", string? function = null) =>
        new() { Key = "open.devices", Label = label, Kind = SettingKind.Page, Page = page, Function = function };

    private static PluginSettingRules.PageEntry? Check(PluginSetting s, bool firstParty = false, int sdk = 4) => PluginSettingRules.CheckPage(s, firstParty, sdk, Exists, out _);

    private static string Problem(PluginSetting s, bool firstParty = false, int sdk = 4)
    {
        Assert.Null(PluginSettingRules.CheckPage(s, firstParty, sdk, Exists, out var problem));
        return problem!;
    }

    [Fact]
    public void A_valid_entry_keeps_its_cleaned_label_and_the_page()
    {
        var entry = Check(Entry(label: "  Choose   devices "));
        Assert.Equal(new PluginSettingRules.PageEntry("Choose devices", "devices"), entry);
    }

    [Fact]
    public void An_unknown_or_unregistered_page_is_ignored_with_a_reason()
    {
        Assert.Contains("unknown page", Problem(Entry(page: "missing")));
        Assert.Contains("unknown page", Problem(Entry(page: null)));
        Assert.Contains("unknown page", Problem(Entry(page: "")));
    }

    [Fact]
    public void Core_pages_other_plugins_pages_and_addresses_are_never_a_target()
    {
        // The existence check only knows this plugin's own pages; a core page ("$settings") is refused before it is even looked up.
        var looked = new List<string>();
        foreach (var id in new[] { "$settings", "$", "$home", "other.plugin/home", "..\\x", "https://example.com", "file:///c:/x", "javascript:alert(1)", "//evil", "settings", "home" })
        {
            Assert.Null(PluginSettingRules.CheckPage(Entry(page: id), true, 4, p => { looked.Add(p); return Exists(p); }, out var problem));
            Assert.NotNull(problem);
        }
        Assert.DoesNotContain(looked, p => p.StartsWith('$'));
        Assert.Contains("unknown page", Problem(Entry(page: new string('a', 300))));
    }

    [Fact]
    public void Below_sdk_4_the_entry_is_ignored()
    {
        Assert.Equal("needs sdk 4", Problem(Entry(), sdk: 3));
        Assert.Equal("needs sdk 4", Problem(Entry(), sdk: 1));
    }

    [Fact]
    public void Other_setting_kinds_are_no_page_entries_and_take_no_value()
    {
        var toggle = new PluginSetting { Key = "k", Label = "L", Kind = SettingKind.Toggle, Page = "devices" };
        Assert.Equal("not a page entry", Problem(toggle));
        // Nothing can be stored for a page entry, whatever a caller sends.
        foreach (var value in new[] { "true", "x", "", null }) Assert.Null(PluginSettingRules.Check(Entry(), value));
    }

    [Fact]
    public void Labels_follow_the_page_action_name_rules()
    {
        // No emoji or symbols, no look-alike of a core screen; a label that is empty after cleaning drops the entry.
        Assert.Equal("Devices", Check(Entry(label: "🏠 Devices"))?.Label);
        Assert.Equal("empty label", Problem(Entry(label: "🏠🏠")));
        Assert.Equal("empty label", Problem(Entry(label: "   ")));
        // Reserved words (third-party plugins only, as for page actions), also with decoration around them.
        foreach (var reserved in new[] { "Back", "Zurück", "Settings", "Einstellungen", "Home", "Parents' code", "Eltern-Code", "Lock", "Power off", "Back!", "|Home|", "Guide", "OK" })
            Assert.Contains(Problem(Entry(label: reserved)), new[] { "reserved label", "empty label" });
        Assert.NotNull(Check(Entry(label: "Back"), firstParty: true));
        Assert.True(Check(Entry(label: new string('x', 100)))!.Label.Length <= PluginSettingRules.MaxPageLabel);
    }

    [Fact]
    public void The_gate_is_the_one_of_a_setting_function_then_settings_then_admin()
    {
        static bool Declared(string id) => id == "settings";
        Assert.Equal("settings", PluginSettingRules.FunctionOf(Entry(), Declared));
        Assert.Equal("tv", PluginSettingRules.FunctionOf(Entry(function: "tv"), Declared));
        Assert.Null(PluginSettingRules.FunctionOf(Entry(), _ => false));
        // Admin on the locked couch: unlock first; in a child's profile the parents' code once (a visit); nobody is nobody.
        Assert.Equal("lock.unlockFirst", PluginSettingRules.Refusal(PluginLevel.Admin, locked: true, childProfile: false, visitActive: false));
        Assert.Equal(PluginSettingRules.NeedVisit, PluginSettingRules.Refusal(PluginLevel.Admin, false, true, false));
        Assert.Null(PluginSettingRules.Refusal(PluginLevel.Admin, false, true, true));
        Assert.Equal("plugin.fn.off", PluginSettingRules.Refusal(PluginLevel.Off, false, false, true));
        Assert.Null(PluginSettingRules.Refusal(PluginLevel.Restricted, true, false, false));
    }

    // ---- the host side, pinned in the source like the page actions

    private static string ReadSource(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot(), .. parts])).Replace("\r\n", "\n");

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
    public void Opening_looks_the_entry_up_fresh_checks_the_page_and_the_setting_gate_in_the_host()
    {
        var hud = ReadSource("src", "CouchHud.Shell", "Hud.cs");
        var open = Body(hud, "public object OpenPluginSettingPage(");
        // Looked up by key among the plugin's registered settings, of kind Page (never taken from what the UI sent besides the key).
        Assert.Contains("plugins.Settings.FirstOrDefault(s => s.Plugin == pluginId && s.Setting.Key == key && s.Setting.Kind == SettingKind.Page)", open);
        // The page id comes from the registered setting and is looked up among this plugin's pages only.
        Assert.Contains("SettingPageEntry(pluginId, found.Setting)", open);
        Assert.Contains("plugins.FindPage(pluginId, entry.PageId)", open);
        // The gate: the same function/level/lock/visit decision as changing a setting, then the parents' code once for a child's profile.
        Assert.Contains("SettingGateRefusal(pluginId, found.Setting, out _)", open);
        Assert.Contains("RefuseWhileConfigFaulted()", open);
        // The gate runs before the page is looked at, so a locked or child caller gets the same answer whether or not the page exists.
        Assert.True(open.IndexOf("SettingGateRefusal(", StringComparison.Ordinal) < open.IndexOf("SettingPageEntry(", StringComparison.Ordinal));
        Assert.True(open.IndexOf("SettingGateRefusal(", StringComparison.Ordinal) < open.IndexOf("plugins.FindPage(", StringComparison.Ordinal));
        // Exactly one prompt and the right scope: opening asks for no code and begins no visit; only the page's own view check does, for its own id.
        Assert.DoesNotContain("CheckParentGrant", open);
        Assert.DoesNotContain("UseUp", open);
        Assert.DoesNotContain("BeginVisit", open);
        Assert.DoesNotContain("SettingsPageId", open);
        var read = Body(hud, "public async Task<object> GetPluginPageAsync(");
        Assert.Contains("RequirePageView(pluginId, page, withGrant: true)", read);
        Assert.Contains("BeginVisit(pluginId, pageId)", read);
        var gate = Body(hud, "private string? SettingGateRefusal(");
        Assert.Contains("PluginSettingRules.FunctionOf(", gate);
        Assert.Contains("PluginSettingRules.Refusal(plugins.LevelOf(pluginId, function), Locked, ChildNow, visit)", gate);
        // The page itself is decided again when it is read (nothing about the open skips the page's own view check).
        Assert.Contains("RequirePageView(pluginId, page, withGrant: true)", Body(hud, "public async Task<object> GetPluginPageAsync("));
    }

    [Fact]
    public void The_list_shows_only_entries_the_host_accepts_and_the_lock_does_not_refuse_for_good()
    {
        var hud = ReadSource("src", "CouchHud.Shell", "Hud.cs");
        var list = Body(hud, "public object GetPlugins()");
        Assert.Contains("SettingPageEntry(p.Id, x.Setting)", list);
        Assert.Contains("SettingPageShown(p.Id, x.Item.Setting)", list);
        Assert.Contains("SettingGateRefusal(pluginId, setting, out _) is null or PluginSettingRules.NeedVisit", hud);
        // The entry says its kind and its cleaned label, never the page id (the UI cannot name a page through it).
        Assert.Contains("Label = x.Entry?.Label ?? x.Item.Setting.Label", list);
        Assert.DoesNotContain("x.Item.Setting.Page", list);
        // An ignored entry is told to the log.
        Assert.Contains("settings entry '{PluginSettingRules.LogText(setting.Key)}' ignored", hud);
        // Setting a value on a page entry is refused by the value check (Check returns null for the kind).
        Assert.Contains("PluginSettingRules.Check(found.Setting, value) ?? throw", Body(hud, "public void SetPluginSetting("));
    }

    [Fact]
    public void The_bridge_lets_the_press_through_the_lock_only_to_be_decided_by_the_host_and_no_remote_list_has_it()
    {
        Assert.Contains("openPluginSettingPage", LockPolicy.OpenWhileLocked);
        Assert.Contains("\"openPluginSettingPage\"", ReadSource("src", "CouchHud.Core", "Config", "FeatureAreas.cs"));
        Assert.Contains("hud.OpenPluginSettingPage(StringArg(0), StringArg(1))", ReadSource("src", "CouchHud.Shell", "UiHost", "UiBridge.cs"));
        // Not offered to the assistant, MCP or the phone remote: only the HUD's own page calls it.
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (name is "UiBridge.cs" or "FeatureAreas.cs" or "LockPolicy.cs" or "Hud.cs") continue;
            Assert.DoesNotContain("openPluginSettingPage", File.ReadAllText(file));
            Assert.DoesNotContain("OpenPluginSettingPage", File.ReadAllText(file));
        }
    }

    [Fact]
    public void A_visit_of_the_opened_page_does_not_cover_the_settings_of_the_plugin()
    {
        var now = DateTimeOffset.UnixEpoch;
        var visits = new PluginVisits(() => now);
        // What opening the page in a child's profile leaves behind: a visit of that page id only.
        visits.Begin("ha", "devices", "kid");
        Assert.True(visits.Active("ha", "devices", "kid"));
        Assert.False(visits.Active("ha", PluginSettingRules.SettingsPageId, "kid"));
        Assert.False(visits.Active("ha", "order", "kid"));
        // So a setting without a function (the address, the token) still asks: no visit of "$settings" and no page of its function.
        var url = new PluginSetting { Key = "url", Label = "Address", Kind = SettingKind.Text };
        Assert.Null(PluginSettingRules.FunctionOf(url, _ => false));
        Assert.Equal(PluginSettingRules.NeedVisit, PluginSettingRules.Refusal(PluginLevel.Admin, false, true, visits.Active("ha", PluginSettingRules.SettingsPageId, "kid")));
    }

    [Fact]
    public void Settings_per_plugin_are_capped_and_the_log_text_is_clean_and_short()
    {
        Assert.Equal(64, PluginSettingRules.MaxSettingsPerPlugin);
        Assert.Equal("evil name", PluginSettingRules.LogText("evil\r\n\u001b[31m name".Replace("\u001b[31m", "")));
        Assert.DoesNotContain('\n', PluginSettingRules.LogText("a\nb\rc\0d"));
        Assert.Equal(60, PluginSettingRules.LogText(new string('x', 500)).Length);
        Assert.Equal("", PluginSettingRules.LogText(null));
        // The unknown-page reason carries the cleaned, short id only.
        PluginSettingRules.CheckPage(Entry(page: "bad\nid" + new string('y', 200)), false, 4, _ => false, out var problem);
        Assert.DoesNotContain('\n', problem);
        Assert.True(problem!.Length < 100);
        var manager = ReadSource("src", "CouchHud.Core", "Plugins", "PluginManager.cs");
        Assert.Contains("PluginSettingRules.MaxSettingsPerPlugin", Body(manager, "public void RegisterSettings("));
        var hud = ReadSource("src", "CouchHud.Shell", "Hud.cs");
        Assert.Contains("PluginSettingRules.LogText(pluginId)", Body(hud, "private PluginSettingRules.PageEntry? SettingPageEntry("));
        Assert.Contains("PluginSettingRules.LogText(setting.Key)", Body(hud, "private PluginSettingRules.PageEntry? SettingPageEntry("));
    }

    [Fact]
    public void The_new_kind_and_property_are_additive_inside_sdk_4()
    {
        Assert.Equal(4, SdkVersion.Current);
        // Existing kinds keep their numbers (stored nowhere, but plugins compiled against SDK 4 before the change keep working).
        Assert.Equal(0, (int)SettingKind.Toggle);
        Assert.Equal(1, (int)SettingKind.Text);
        Assert.Equal(2, (int)SettingKind.Secret);
        Assert.Equal(3, (int)SettingKind.Choice);
        Assert.Equal(4, (int)SettingKind.Page);
        Assert.Null(new PluginSetting { Key = "k", Label = "L" }.Page);
    }
}
