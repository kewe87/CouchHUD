using System.Net;
using System.Text.RegularExpressions;
using CouchHud.Core.Assistant;
using CouchHud.Core.Config;
using CouchHud.Core.Guard;
using CouchHud.Core.Library;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>
/// Switchable feature areas (Games, Apps, Browser, News): the defaults, the refusals and the one map of what belongs where. The map is
/// checked against the shell's source, so a new setting key or bridge call without an area fails here.
/// </summary>
public class FeatureAreasTests
{
    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CouchHud.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repository root not found");
    }

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([Root(), .. parts])).Replace("\r\n", "\n");

    private static LibraryItem Item(string id, ItemType type, LaunchTarget launch) =>
        new() { Id = id, Source = "custom", Name = id, Type = type, Launch = launch };

    // ----- config -----

    [Fact]
    public void Everything_is_on_by_default_and_for_a_config_without_the_list()
    {
        Assert.Empty(new AppConfig().DisabledAreas);
        Assert.True(FeatureAreas.From(new AppConfig()).AllEnabled);
        var dir = Directory.CreateTempSubdirectory("couchhud-areas-").FullName;
        File.WriteAllText(Path.Combine(dir, "config.json"), """{ "setupWizardDone": true }""");
        var areas = FeatureAreas.From(new ConfigStore(dir).Current);
        Assert.Equal(FeatureAreas.All, areas.Enabled);
    }

    [Fact]
    public void Unknown_names_in_the_config_are_ignored_and_the_choice_survives_a_restart()
    {
        var set = new AreaSet(["games", "spaceships", null!, "NEWS"]);
        Assert.False(set.Games);
        Assert.True(set.News); // names are exact: "NEWS" is no area
        Assert.True(set.Apps && set.Browser);

        var dir = Directory.CreateTempSubdirectory("couchhud-areas-").FullName;
        new ConfigStore(dir).Update(c => c.DisabledAreas = FeatureAreas.DisabledFor(["apps", "browser"]));
        var again = FeatureAreas.From(new ConfigStore(dir).Current);
        Assert.Equal(["apps", "browser"], again.Enabled);
        Assert.False(again.Games);
        Assert.False(again.News);
    }

    [Fact]
    public void At_least_one_launch_area_stays_on_and_unknown_areas_are_refused()
    {
        Assert.Throws<ArgumentException>(() => FeatureAreas.DisabledFor([]));
        Assert.Throws<ArgumentException>(() => FeatureAreas.DisabledFor(["news"]));
        Assert.Throws<ArgumentException>(() => FeatureAreas.DisabledFor(["games", "everything"]));
        Assert.Equal(["apps", "news"], FeatureAreas.DisabledFor(["browser", "games"]));
        Assert.Empty(FeatureAreas.DisabledFor(["news", "browser", "apps", "games", "games"]));
    }

    // ----- entries -----

    [Fact]
    public void An_entry_needs_its_type_and_a_web_page_or_a_browser_the_browser_too()
    {
        var game = Item("steam:1", ItemType.Game, new LaunchTarget(LaunchKind.Url, "steam://rungameid/1"));
        var app = Item("custom:notes", ItemType.App, new LaunchTarget(LaunchKind.Exe, @"C:\Tools\notes.exe"));
        var site = Item("custom:tube", ItemType.App, new LaunchTarget(LaunchKind.Url, "https://video.example") { Embedded = true });
        var chrome = Item("custom:chrome", ItemType.App, new LaunchTarget(LaunchKind.Exe, @"""C:\Program Files\Google\Chrome\Application\chrome.exe"""));

        var noGames = new AreaSet(["games"]);
        Assert.False(noGames.Shows(game));
        Assert.True(noGames.Shows(app) && noGames.Shows(site) && noGames.Shows(chrome));

        var noBrowser = new AreaSet(["browser"]);
        Assert.True(noBrowser.Shows(game) && noBrowser.Shows(app));
        Assert.False(noBrowser.Shows(site));
        Assert.False(noBrowser.Shows(chrome));

        var noApps = new AreaSet(["apps"]);
        Assert.True(noApps.Shows(game));
        Assert.False(noApps.Shows(app) || noApps.Shows(site));

        Assert.True(LockedForeground.IsBrowser("msedge.exe"));
        Assert.False(LockedForeground.IsBrowser("notes"));
        Assert.False(FeatureAreas.NeedsBrowser(new LaunchTarget(LaunchKind.Exe, "<>|bad")));
    }

    [Fact]
    public void Calls_tools_settings_and_background_work_of_an_area_that_is_off_are_refused()
    {
        var set = new AreaSet(["games", "news", "browser"]);
        Assert.False(set.AllowsMethod("backupSaves"));
        Assert.False(set.AllowsMethod("getUninstalled"));
        Assert.False(set.AllowsMethod("openBrowser"));
        Assert.False(set.AllowsMethod("openUrl"));
        Assert.False(set.AllowsMethod("getNews"));
        Assert.True(set.AllowsMethod("getSettings"));
        Assert.True(set.AllowsMethod("setParental"));
        Assert.True(set.AllowsMethod("quitGame"));
        Assert.False(set.AllowsSetting("steamGridDbApiKey"));
        Assert.False(set.AllowsSetting("saves.autoBackup"));
        Assert.False(set.AllowsSetting("maximizeBrowser"));
        Assert.False(set.AllowsSetting("browserHistory"));
        Assert.True(set.AllowsSetting("language"));
        Assert.False(set.AllowsTool("get_news"));
        Assert.True(set.AllowsTool("launch"));
        foreach (var service in new[] { "gameStoreScan", "gameArtwork", "saveAutoBackup", "gameDownloads", "launcherStandby", "steamGuardSettings", "couchBrowser", "newsFeeds" })
            Assert.False(set.Runs(service), service);
        Assert.True(set.Runs("installedApps"));
        Assert.True(AreaSet.AllOn.Runs("gameStoreScan"));
    }

    // ----- the switch itself -----

    [Fact]
    public void Switching_areas_is_never_open_on_a_locked_couch_nor_for_the_assistant_and_MCP()
    {
        Assert.DoesNotContain("setFeatureAreas", LockPolicy.OpenWhileLocked);
        Assert.False(LockPolicy.IsHarmlessSetting("disabledAreas", profilesGuarded: false));
        Assert.DoesNotContain(ToolCatalog.SettingKeys, k => k.Contains("area", StringComparison.OrdinalIgnoreCase));
        var hud = Source("src", "CouchHud.Shell", "Hud.cs");
        var method = hud[hud.IndexOf("public object SetFeatureAreas(", StringComparison.Ordinal)..];
        method = method[..method.IndexOf("\n    }\n", StringComparison.Ordinal)];
        Assert.Contains("if (Locked) throw new LockedException", method);
        // Broken settings: refused first, before anything is checked or used up.
        Assert.Equal("RefuseWhileConfigFaulted();", method.Split('\n').Skip(2).First(l => l.Trim().Length > 0).Trim());
        // The parents' grant is checked before the change and used up only after the save worked (a refused save keeps it).
        var fits = method.IndexOf("_grant.Fits(\"areas\")", StringComparison.Ordinal);
        var save = method.IndexOf("config.UpdateOrThrow(", StringComparison.Ordinal);
        var consume = method.IndexOf("_grant.TryConsume(\"areas\")", StringComparison.Ordinal);
        Assert.True(fits > 0 && save > fits && consume > save, "grant checked, saved, then used up");
        Assert.DoesNotContain("config.Update(", method);
        // The tools of the assistant and MCP do not know the method.
        Assert.DoesNotContain("setFeatureAreas", Source("src", "CouchHud.Shell", "Services", "HudTools.cs"));
    }

    // ----- the map against the source -----

    [Fact]
    public void Every_setting_key_of_the_shell_has_an_area()
    {
        var hud = Source("src", "CouchHud.Shell", "Hud.cs");
        var start = hud.IndexOf("public async Task SetSettingAsync(string key", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = hud[start..hud.IndexOf("\n    }\n", start, StringComparison.Ordinal)];
        var keys = Regex.Matches(body, "^            case \"(\\w+)\":", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        Assert.True(keys.Count > 40, $"only {keys.Count} keys found");
        foreach (var key in keys) Assert.True(FeatureAreas.SettingKeys.ContainsKey(key), $"setting key {key} has no area in FeatureAreas.SettingKeys");
        // And no stale lines: every key of the map is a real one.
        foreach (var key in FeatureAreas.SettingKeys.Keys) Assert.Contains(key, keys);
    }

    [Fact]
    public void Every_save_setting_has_an_area_and_every_mapped_bridge_call_exists()
    {
        var bridge = Source("src", "CouchHud.Shell", "UiHost", "UiBridge.cs");
        var start = bridge.IndexOf("case \"setSaveSetting\":", StringComparison.Ordinal);
        var body = bridge[(start + 10)..bridge.IndexOf("return hud.Saves.GetSettings();", start, StringComparison.Ordinal)];
        var keys = Regex.Matches(body, "case \"(\\w+)\":").Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(keys);
        foreach (var key in keys) Assert.True(FeatureAreas.SaveSettingKeys.ContainsKey(key), $"save setting {key} has no area");
        foreach (var method in FeatureAreas.BridgeMethods.Keys.Concat(FeatureAreas.ItemMethods).Concat(FeatureAreas.AlwaysMethods))
            Assert.Contains($"case \"{method}\":", bridge);
        Assert.Contains("hud.Areas", bridge);
    }

    [Fact]
    public void Every_bridge_call_has_an_area_or_is_always_there()
    {
        var bridge = Source("src", "CouchHud.Shell", "UiHost", "UiBridge.cs");
        var start = bridge.IndexOf("private async Task<object?> DispatchAsync(", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = bridge[start..bridge.IndexOf("\n    }\n", start, StringComparison.Ordinal)];
        var calls = Regex.Matches(body, "^            case \"(\\w+)\":", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        Assert.True(calls.Count > 150, $"only {calls.Count} calls found");
        // Several labels on one line would slip past the anchored pattern above.
        Assert.Empty(Regex.Matches(body, "case \"\\w+\":[^\\n]*case \"\\w+\":"));
        foreach (var call in calls)
        {
            var mapped = FeatureAreas.BridgeMethods.ContainsKey(call);
            var always = FeatureAreas.AlwaysMethods.Contains(call);
            Assert.True(mapped ^ always, mapped ? $"{call} is both in an area and always" : $"bridge call {call} has no area: add it to FeatureAreas.BridgeMethods or AlwaysMethods");
        }
        // The calls of one entry are always there as calls (the entry decides) unless the whole call belongs to an area.
        foreach (var call in FeatureAreas.ItemMethods) Assert.Contains(call, calls);
        // The browser's window calls and the newer game and app calls are mapped.
        foreach (var (call, area) in new[] { ("getDownloads", "games"), ("scanFolder", "games"), ("getAppIcons", "apps"), ("browserLayout", "browser"), ("browserFocus", "browser"), ("browserDialogAnswer", "browser") })
            Assert.Equal(area, FeatureAreas.BridgeMethods[call]);
    }

    [Fact]
    public void A_list_that_leaves_no_launch_area_on_counts_as_everything_on()
    {
        Assert.True(new AreaSet(["games", "apps", "browser"]).AllEnabled);
        Assert.True(new AreaSet(["games", "apps", "browser", "news"]).AllEnabled);
        Assert.False(new AreaSet(["games", "apps"]).AllEnabled);
        Assert.Equal(["browser", "news"], new AreaSet(["games", "apps"]).Enabled);
    }

    [Fact]
    public void The_assistant_and_MCP_cannot_change_a_setting_of_an_area_that_is_off()
    {
        var tools = Source("src", "CouchHud.Shell", "Services", "HudTools.cs");
        Assert.Contains("name == \"set_setting\" && Opt(args, \"key\") is { } settingKey && !hud.Areas.AllowsSetting(settingKey)", tools);
        // The check comes before the tool runs.
        Assert.True(tools.IndexOf("!hud.Areas.AllowsSetting(settingKey)", StringComparison.Ordinal) < tools.IndexOf("\"set_setting\" => await SetSettingAsync(args)", StringComparison.Ordinal));
        Assert.False(new AreaSet(["games"]).AllowsSetting("trailerPreviews"));
    }

    [Fact]
    public void Every_library_emit_of_the_shell_goes_through_Shown()
    {
        var shell = Path.Combine(Root(), "src", "CouchHud.Shell");
        var emits = 0;
        foreach (var file in Directory.EnumerateFiles(shell, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(file), "Emit\\(\"library\",\\s*([^\\n]*)"))
            {
                emits++;
                Assert.True(m.Groups[1].Value.StartsWith("UiBridge.ToDto(Shown(", StringComparison.Ordinal),
                    $"{Path.GetFileName(file)}: Emit(\"library\", {m.Groups[1].Value} must filter with Shown(...)");
            }
        }
        Assert.True(emits >= 5, $"only {emits} library emits found");
    }

    // ----- the library -----

    [Fact]
    public async Task A_store_entry_marked_as_app_keeps_its_store_read_with_Gaming_off()
    {
        var dir = Directory.CreateTempSubdirectory("couchhud-areas-").FullName;
        var config = new ConfigStore(dir);
        var steam = new Store("steam", Item("steam:1", ItemType.Game, new LaunchTarget(LaunchKind.Url, "steam://rungameid/1")),
            Item("steam:2", ItemType.Game, new LaunchTarget(LaunchKind.Url, "steam://rungameid/2")));
        var gog = new Store("gog", Item("gog:1", ItemType.Game, new LaunchTarget(LaunchKind.Exe, @"C:\Games\a.exe")));
        var service = new LibraryService(config, new ArtworkResolver(new HttpClient(new NotFound()), Path.Combine(dir, "cache.json"), null), [steam, gog]);
        service.SkipProvider = p => p is IGameStore store && !FeatureAreas.ReadsStore(config.Current, store.Source);
        config.Update(c =>
        {
            c.DisabledAreas = FeatureAreas.DisabledFor(["apps", "browser", "news"]);
            c.Overrides["steam:2"] = new ItemOverride { Type = "app" };
            // A game typed as game, or an app override of another store, does not open a store.
            c.Overrides["gog:1"] = new ItemOverride { Type = "game" };
            c.Overrides["steamx:9"] = new ItemOverride { Type = "app" };
        });

        Assert.True(FeatureAreas.ReadsStore(config.Current, "steam"));
        Assert.False(FeatureAreas.ReadsStore(config.Current, "gog"));
        var items = await service.ScanAsync();
        Assert.Equal(1, steam.Reads);
        Assert.Equal(0, gog.Reads);
        // Only the entry typed as app is shown: AreasOf decides per entry.
        var areas = FeatureAreas.From(config.Current);
        var shown = items.Where(i => i.Id.StartsWith("steam:", StringComparison.Ordinal) || i.Id.StartsWith("gog:", StringComparison.Ordinal)).Where(areas.Shows).ToList();
        Assert.Contains(items, i => i.Id == "steam:1");
        Assert.Equal("steam:2", Assert.Single(shown).Id);
        Assert.Equal(ItemType.App, shown[0].Type);

        // Without the override the store is skipped again; parental controls or Gaming on read every store.
        config.Update(c => c.Overrides.Remove("steam:2"));
        Assert.False(FeatureAreas.ReadsStore(config.Current, "steam"));
        config.Update(c => c.ParentalEnabled = true);
        Assert.True(FeatureAreas.ReadsStore(config.Current, "gog"));
        config.Update(c => { c.ParentalEnabled = false; c.DisabledAreas = []; });
        Assert.True(FeatureAreas.ReadsStore(config.Current, "gog"));
    }

    [Fact]
    public async Task A_skipped_store_is_not_read_and_its_entries_come_back_with_the_next_full_scan()
    {
        var dir = Directory.CreateTempSubdirectory("couchhud-areas-").FullName;
        var config = new ConfigStore(dir);
        var store = new Store(Item("steam:1", ItemType.Game, new LaunchTarget(LaunchKind.Url, "steam://rungameid/1")));
        var service = new LibraryService(config, new ArtworkResolver(new HttpClient(new NotFound()), Path.Combine(dir, "cache.json"), null), [store]);
        await service.ScanAsync();
        service.ToggleFavorite("steam:1");
        Assert.Equal(1, store.Reads);

        // Gaming off: the store is left alone, the favourite stays in the config.
        var gamesOff = false;
        service.SkipProvider = p => p is IGameStore && gamesOff;
        gamesOff = true;
        Assert.DoesNotContain(await service.ScanAsync(), i => i.Id == "steam:1");
        Assert.Equal(1, store.Reads);
        Assert.Contains("steam:1", config.Current.ActiveProfile.Favorites);

        // Back on: read again, with the favourite.
        gamesOff = false;
        var back = await service.ScanAsync();
        Assert.Equal(2, store.Reads);
        Assert.True(Assert.Single(back, i => i.Id == "steam:1").Favorite);
    }

    [Fact]
    public async Task A_scan_that_finishes_after_a_newer_one_started_is_dropped()
    {
        var dir = Directory.CreateTempSubdirectory("couchhud-areas-").FullName;
        var config = new ConfigStore(dir);
        var gate = new TaskCompletionSource();
        var store = new Store(Item("steam:1", ItemType.Game, new LaunchTarget(LaunchKind.Url, "steam://rungameid/1"))) { Gate = gate.Task };
        var service = new LibraryService(config, new ArtworkResolver(new HttpClient(new NotFound()), Path.Combine(dir, "cache.json"), null), [store]);
        var gamesOff = false;
        service.SkipProvider = p => p is IGameStore && gamesOff;

        // A scan with Gaming on waits for the store; Gaming goes off and a new scan (without the store) finishes first.
        var old = service.ScanAsync();
        gamesOff = true;
        Assert.DoesNotContain(await service.ScanAsync(), i => i.Id == "steam:1");
        // The old scan comes back late: it must not bring the game back.
        gate.SetResult();
        Assert.DoesNotContain(await old, i => i.Id == "steam:1");
        Assert.DoesNotContain(service.Items, i => i.Id == "steam:1");
    }

    private sealed class Store(string source, params LibraryItem[] items) : ILibraryProvider, IGameStore
    {
        public Store(params LibraryItem[] items) : this("steam", items) { }

        public int Reads { get; private set; }
        public string Name => "store";
        public string Source => source;

        public Task Gate { get; init; } = Task.CompletedTask;

        public async Task<IReadOnlyList<LibraryItem>> GetItemsAsync(CancellationToken ct)
        {
            Reads++;
            await Gate;
            return items;
        }
    }

    private sealed class NotFound : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
