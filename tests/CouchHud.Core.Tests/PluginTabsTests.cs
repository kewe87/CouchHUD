using CouchHud.Core.Config;
using CouchHud.Core.Guard;
using CouchHud.Core.Plugins;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>
/// Main tabs that plugins ask for (capability "tabs"): the rules for ids, titles, glyphs and pictures, names that look like a built-in tab,
/// the bounds, the order, the areas and the child profile, and the gate on the context member. Mostly negative cases.
/// </summary>
public sealed class PluginTabsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-tabs-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] TrustKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    public PluginTabsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static PluginTab Tab(string id = "main", string title = "Lichter", string page = "p", string area = "apps") =>
        new(id, page, title, title) { Area = area };

    private static PluginPage Page(string id = "p", bool locked = false) =>
        new() { Id = id, Title = "Seite", Rows = _ => Task.FromResult<IReadOnlyList<PluginRow>>([]), AllowedWhileLocked = locked };

    private static readonly IReadOnlySet<string> TabsCap = new HashSet<string> { PluginCapabilities.Tabs };

    private static PluginTabRules.Registered Reg(string plugin, PluginTab tab, PluginOrigin origin = PluginOrigin.ThirdParty, IReadOnlySet<string>? caps = null) =>
        new(plugin, origin, caps ?? TabsCap, tab);

    private static IReadOnlyList<PluginTabView> Resolve(IEnumerable<PluginTabRules.Registered> tabs, AreaSet? areas = null, bool child = false, string language = "de",
        Func<string, bool>? loaded = null, Func<string, string, bool>? pageExists = null, bool locked = false) =>
        PluginTabRules.Resolve(tabs, loaded ?? (_ => true), pageExists ?? ((_, _) => true), language, areas ?? AreaSet.AllOn, child, locked);

    private IPluginContext Context(PluginOrigin origin, out PluginManager manager, params string[] caps)
    {
        manager = new PluginManager([(Path.Combine(_dir, "plugins"), true)], _dir, _ => true) { Trust = new PluginTrust(_dir, TrustKey) };
        return manager.ContextFor("tool", new PluginManager.PluginGrant("tool", origin, new HashSet<string>(caps)), markLoaded: true);
    }

    // ---- The gate on the context member ----

    [Fact]
    public void Registering_a_tab_needs_the_tabs_capability()
    {
        var ctx = Context(PluginOrigin.ThirdParty, out var manager, PluginCapabilities.Pages);
        ctx.RegisterPage(Page());
        Assert.Throws<PluginCapabilityException>(() => ctx.RegisterTab(Tab()));
        Assert.Empty(manager.ResolveTabs(AreaSet.AllOn, false));
    }

    [Fact]
    public void The_gate_table_names_the_tabs_capability_for_the_member_and_the_capability_is_known()
    {
        Assert.Equal(PluginCapabilities.Tabs, PluginPolicy.ContextMembers["RegisterTab"]);
        Assert.Contains(PluginCapabilities.Tabs, PluginCapabilities.All);
        Assert.Null(PluginPolicy.CheckDeclared(PluginOrigin.ThirdParty, [PluginCapabilities.Tabs]));
        Assert.Null(PluginPolicy.CheckDeclared(PluginOrigin.FirstParty, [PluginCapabilities.Tabs]));
        Assert.DoesNotContain(PluginCapabilities.Tabs, PluginPolicy.Legacy);
        Assert.DoesNotContain(PluginCapabilities.Tabs, PluginPolicy.FirstPartyOnly);
    }

    [Fact]
    public void A_plugin_from_before_the_capabilities_never_gets_tabs()
    {
        // Declared nothing and SDK 2: the SDK 2 surface only. And a plugin that was installed without the capability never gains it.
        Assert.DoesNotContain(PluginCapabilities.Tabs, PluginPolicy.Granted(PluginOrigin.FirstParty, null, 2));
        Assert.DoesNotContain(PluginCapabilities.Tabs, PluginPolicy.Granted(PluginOrigin.ThirdParty, [PluginCapabilities.Tabs, PluginCapabilities.Pages], 3, [PluginCapabilities.Pages]));
        Assert.Contains(PluginCapabilities.Tabs, PluginPolicy.Granted(PluginOrigin.ThirdParty, [PluginCapabilities.Tabs], 3, [PluginCapabilities.Tabs]));
    }

    [Fact]
    public void A_registered_tab_shows_while_the_plugin_is_loaded_with_its_page_and_not_otherwise()
    {
        var ctx = Context(PluginOrigin.ThirdParty, out var manager, PluginCapabilities.Pages, PluginCapabilities.Tabs);
        ctx.RegisterTab(Tab());
        // The page is not registered (yet): no tab.
        Assert.Empty(manager.ResolveTabs(AreaSet.AllOn, false));
        ctx.RegisterPage(Page());
        var tabs = manager.ResolveTabs(AreaSet.AllOn, false);
        var tab = Assert.Single(tabs);
        Assert.Equal(("tool", "main", "p"), (tab.Plugin, tab.Id, tab.PageId));
        Assert.False(tab.FirstParty);
        // The plugin is off or not loaded: the same list, but the plugin is not loaded.
        var absent = new PluginManager([(Path.Combine(_dir, "plugins"), true)], _dir, _ => true);
        Assert.Empty(absent.ResolveTabs(AreaSet.AllOn, false));
        Assert.Empty(Resolve([Reg("tool", Tab())], loaded: _ => false));
    }

    [Fact]
    public void A_tab_whose_capability_is_missing_in_the_grant_is_left_out_even_if_it_is_in_the_list()
    {
        Assert.Empty(Resolve([Reg("tool", Tab(), caps: new HashSet<string> { PluginCapabilities.Pages })]));
        Assert.Single(Resolve([Reg("tool", Tab())]));
    }

    [Fact]
    public void The_registered_tab_changed_event_fires_and_an_id_registered_again_replaces_the_earlier_one()
    {
        var ctx = Context(PluginOrigin.ThirdParty, out var manager, PluginCapabilities.Pages, PluginCapabilities.Tabs);
        var raised = 0;
        manager.TabsChanged += () => raised++;
        ctx.RegisterPage(Page());
        ctx.RegisterTab(Tab(title: "Alt"));
        ctx.RegisterTab(Tab(title: "Neu"));
        Assert.Equal(2, raised);
        Assert.Equal("Neu", Assert.Single(manager.ResolveTabs(AreaSet.AllOn, false)).Title);
    }

    // ---- Bounds ----

    [Fact]
    public void A_plugin_may_register_two_tabs_and_the_third_is_refused()
    {
        var ctx = Context(PluginOrigin.ThirdParty, out var manager, PluginCapabilities.Pages, PluginCapabilities.Tabs);
        ctx.RegisterPage(Page());
        ctx.RegisterTab(Tab("a", "Alpha"));
        ctx.RegisterTab(Tab("b", "Beta"));
        ctx.RegisterTab(Tab("c", "Gamma"));
        Assert.Equal(["Alpha", "Beta"], manager.ResolveTabs(AreaSet.AllOn, false).Select(t => t.Title));
        // Replacing one of the two is fine.
        ctx.RegisterTab(Tab("b", "Delta"));
        Assert.Equal(["Alpha", "Delta"], manager.ResolveTabs(AreaSet.AllOn, false).Select(t => t.Title));
    }

    [Fact]
    public void Resolve_cuts_a_plugin_to_two_tabs_and_all_plugins_to_six()
    {
        var many = Enumerable.Range(0, 5).SelectMany(p => Enumerable.Range(0, 4).Select(i => Reg($"plug{p}", Tab($"t{i}", $"Name{p}x{i}")))).ToList();
        var shown = Resolve(many);
        Assert.Equal(PluginTabRules.MaxTotal, shown.Count);
        Assert.All(shown.GroupBy(s => s.Plugin), g => Assert.True(g.Count() <= PluginTabRules.MaxPerPlugin));
    }

    [Fact]
    public void Own_plugins_come_first_so_others_cannot_push_them_out_and_the_order_is_stable()
    {
        var tabs = new List<PluginTabRules.Registered>();
        for (var i = 0; i < 4; i++) tabs.Add(Reg($"other{i}", Tab("m", $"Fremd{i}")));
        tabs.Add(Reg("own", Tab("m", "Eigen"), PluginOrigin.FirstParty));
        var shown = Resolve(tabs);
        Assert.Equal(["Eigen", "Fremd0", "Fremd1", "Fremd2", "Fremd3"], shown.Select(s => s.Title));
        Assert.Equal(shown.Select(s => s.Title), Resolve(tabs).Select(s => s.Title));
        Assert.True(shown[0].FirstParty);
    }

    [Fact]
    public void A_second_tab_with_the_same_look_of_title_is_dropped()
    {
        var shown = Resolve([Reg("a", Tab(title: "Lichter")), Reg("b", Tab(title: "L1chter")), Reg("c", Tab(title: "Lichter 2"))]);
        Assert.Equal(["Lichter", "Lichter 2"], shown.Select(s => s.Title));
    }

    // ---- Ids, titles, glyphs, pictures ----

    [Theory]
    [InlineData("main", true)]
    [InlineData("a", true)]
    [InlineData("smart-home_2", true)]
    [InlineData("", false)]
    [InlineData("Main", false)]
    [InlineData("-x", false)]
    [InlineData("a b", false)]
    [InlineData("../x", false)]
    [InlineData("ä", false)]
    public void Ids_are_short_plain_lower_case(string id, bool ok) => Assert.Equal(ok, PluginTabRules.IdOk(id));

    [Fact]
    public void An_id_over_the_limit_is_refused()
    {
        Assert.True(PluginTabRules.IdOk(new string('a', PluginTabRules.MaxId)));
        Assert.False(PluginTabRules.IdOk(new string('a', PluginTabRules.MaxId + 1)));
    }

    [Theory]
    [InlineData("Lichter", "Lichter")]
    [InlineData("  Smart   Licht ", "Smart Licht")]
    [InlineData("Übersicht", "Übersicht")]
    [InlineData("R&B", "R&B")]
    [InlineData("Küche", "Küche")]
    [InlineData("Übersicht", "Übersicht")]
    [InlineData("Größe", "Größe")]
    [InlineData("Café", "Café")]
    public void Plain_titles_pass(string raw, string expected) => Assert.Equal(expected, PluginTabRules.CleanTitle(raw));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ho\u0000me")]
    [InlineData("Licht‮etsil")]
    [InlineData("Licht​")]
    [InlineData("Li\ncht")]
    [InlineData("<b>Licht</b>")]
    [InlineData("Лампа")]
    [InlineData("Αpps")] // a Greek capital alpha
    [InlineData("Licht́́́")]
    [InlineData("💡 Licht")]
    [InlineData("Dies ist ein viel zu langer Titel")]
    [InlineData("Fünfzehn Zeiche")]
    [InlineData("Sett\u0131ngs")]
    [InlineData("Ho\u01C0me")]
    [InlineData("Brow\u0131er")]
    [InlineData("\u00B5")]
    [InlineData("A\u00D7B")]
    public void Odd_titles_are_refused(string raw) => Assert.Null(PluginTabRules.CleanTitle(raw));

    [Theory]
    [InlineData("Home")]
    [InlineData("home")]
    [InlineData("H0me")]
    [InlineData("Hom e")]
    [InlineData("Favoriten")]
    [InlineData("Favorites")]
    [InlineData("Spiele")]
    [InlineData("Games")]
    [InlineData("G@mes")]
    [InlineData("Apps")]
    [InlineData("Browser")]
    [InlineData("Br0wser")]
    [InlineData("Settings")]
    [InlineData("Einstellungen")]
    [InlineData("Downloads")]
    [InlineData("News")]
    [InlineData("Assistent")]
    [InlineData("Smart Home")]
    [InlineData("Apps Hub")]
    [InlineData("Meine Einstellungen")]
    [InlineData("CouchHUD")]
    [InlineData("Couch HUD Pro")]
    [InlineData("CaseHUD")]
    [InlineData("Eltern")]
    public void A_title_that_is_or_looks_like_a_built_in_tab_is_refused_for_a_third_party(string title)
    {
        Assert.NotNull(PluginTabRules.Problem(Tab(title: title), firstParty: false));
        Assert.Empty(Resolve([Reg("evil", Tab(title: title))]));
    }

    [Theory]
    [InlineData("Küche")]
    [InlineData("Übersicht")]
    [InlineData("Größe")]
    [InlineData("Café")]
    [InlineData("Ärger")]
    public void German_letters_are_fine_for_a_third_party(string title)
    {
        Assert.Null(PluginTabRules.Problem(Tab(title: title), firstParty: false));
        Assert.Equal(title, Assert.Single(Resolve([Reg("p", Tab(title: title))])).Title);
    }

    [Theory]
    [InlineData("Sett\u0131ngs")]
    [InlineData("Settıngs")]
    [InlineData("Setti\u0144gs")]
    [InlineData("\u0141ock")]
    [InlineData("Hom\u01C0")]
    [InlineData("\u01C0Home")]
    [InlineData("Brøwser")]
    [InlineData("Ässistent")]
    [InlineData("Spiéle")]
    [InlineData("Einstellüngen")]
    [InlineData("Àpps")]
    public void Look_alikes_made_of_other_letters_never_pass_for_a_third_party(string title)
    {
        Assert.NotNull(PluginTabRules.Problem(Tab(title: title), firstParty: false));
        Assert.Empty(Resolve([Reg("evil", Tab(title: title))]));
    }

    [Fact]
    public void The_look_folds_umlauts_and_accents_like_the_ui_does()
    {
        Assert.Equal("kuche", PluginTabRules.Look("Küche"));
        Assert.Equal("uberslcht", PluginTabRules.Look("Übersicht")); // i looks like l
        Assert.Equal("grosse", PluginTabRules.Look("Größe"));
        Assert.Equal("cafe", PluginTabRules.Look("Café"));
        Assert.Equal(PluginTabRules.Look("Apps"), PluginTabRules.Look("Àpps"));
        Assert.Equal(PluginTabRules.Look("Browser"), PluginTabRules.Look("Brøwser"));
        Assert.True(PluginTabRules.IsReservedTitle("Àpps", false));
        Assert.True(PluginTabRules.IsReservedTitle("Brøwser", false));
        Assert.True(PluginTabRules.IsReservedTitle("Einstellüngen", false));
    }

    [Fact]
    public void Only_a_first_party_plugin_may_use_the_title_of_a_feature_that_became_a_plugin()
    {
        Assert.NotNull(PluginTabRules.Problem(Tab(title: "Smart Home"), firstParty: false));
        Assert.Null(PluginTabRules.Problem(Tab(title: "Smart Home"), firstParty: true));
        Assert.Null(PluginTabRules.Problem(Tab(title: "SmartHome"), firstParty: true));
        // But a first-party plugin does not get the other built-in titles.
        Assert.NotNull(PluginTabRules.Problem(Tab(title: "Settings"), firstParty: true));
        Assert.NotNull(PluginTabRules.Problem(Tab(title: "Home"), firstParty: true));
        var shown = Resolve([Reg("homeassistant", Tab("home", "Smart Home", "p", "always"), PluginOrigin.FirstParty)]);
        Assert.Equal("Smart Home", Assert.Single(shown).Title);
        // A third party cannot sit next to it with a look-alike.
        Assert.Single(Resolve([Reg("homeassistant", Tab("home", "Smart Home"), PluginOrigin.FirstParty), Reg("evil", Tab("x", "Smart H0me"))]));
    }

    [Fact]
    public void A_registration_with_a_bad_id_page_or_title_is_refused_and_a_plugin_with_both_languages_needs_both_clean()
    {
        var ctx = Context(PluginOrigin.ThirdParty, out var manager, PluginCapabilities.Pages, PluginCapabilities.Tabs);
        ctx.RegisterPage(Page());
        ctx.RegisterTab(Tab("BAD ID"));
        ctx.RegisterTab(Tab(page: ""));
        ctx.RegisterTab(Tab(title: "Home"));
        ctx.RegisterTab(new PluginTab("x", "p", "Lichter", "Games"));
        ctx.RegisterTab(new PluginTab("y", "p", "", ""));
        ctx.RegisterTab(null!);
        Assert.Empty(manager.ResolveTabs(AreaSet.AllOn, false));
        // One language empty is fine: the other one is used.
        ctx.RegisterTab(new PluginTab("ok", "p", "", "Lights"));
        Assert.Equal("Lights", Assert.Single(manager.ResolveTabs(AreaSet.AllOn, false)).Title);
    }

    [Fact]
    public void The_title_follows_the_language_and_falls_back_to_the_other()
    {
        var both = new PluginTab("a", "p", "Lichter", "Lights");
        Assert.Equal("Lichter", PluginTabRules.TitleFor(both, "de"));
        Assert.Equal("Lights", PluginTabRules.TitleFor(both, "en"));
        Assert.Equal("Lights", PluginTabRules.TitleFor(new PluginTab("a", "p", "", "Lights"), "de"));
        Assert.Equal("Lichter", Resolve([Reg("p", both)], language: "de").Single().Title);
        Assert.Equal("Lights", Resolve([Reg("p", both)], language: "en").Single().Title);
    }

    [Theory]
    [InlineData("💡")]
    [InlineData("🌡️")]
    [InlineData("◆")]
    [InlineData("→")]
    [InlineData("+")]
    [InlineData("🏠")]
    [InlineData("👍🏽")]
    public void One_symbol_is_a_glyph(string glyph) => Assert.Equal(glyph, PluginTabRules.CleanGlyph(glyph));

    [Theory]
    [InlineData("12345")]
    [InlineData("A")]
    [InlineData("7")]
    [InlineData("ab")]
    [InlineData("💡💡")]
    [InlineData("💡x")]
    [InlineData("\u0007")]
    [InlineData("\u202E")]
    [InlineData("\u200D")]
    [InlineData("💡\u200D🔒")]
    [InlineData("a\u0301")]
    [InlineData("\u0301")]
    [InlineData("\u05D0")]
    [InlineData(" ")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_but_one_symbol_is_not_a_glyph(string? glyph) => Assert.Null(PluginTabRules.CleanGlyph(glyph));

    [Theory]
    [InlineData("🔒")]
    [InlineData("⚙")]
    [InlineData("🔔")]
    [InlineData("🌡️")]
    [InlineData("★")]
    [InlineData("🗑")]
    public void A_third_party_cannot_wear_a_built_in_icon_but_CouchHUD_can(string glyph)
    {
        Assert.Equal(PluginTabRules.GenericGlyph, PluginTabRules.GlyphFor(glyph, firstParty: false));
        Assert.Equal(glyph, PluginTabRules.GlyphFor(glyph, firstParty: true));
        Assert.Equal(PluginTabRules.GenericGlyph, Resolve([Reg("p", Tab() with { Glyph = glyph })]).Single().Glyph);
        Assert.Equal(glyph, Resolve([Reg("own", Tab() with { Glyph = glyph }, PluginOrigin.FirstParty)]).Single().Glyph);
    }

    [Fact]
    public void A_free_glyph_stays_for_a_third_party_and_a_bad_one_is_dropped()
    {
        Assert.Equal("🏠", PluginTabRules.GlyphFor("🏠", false));
        Assert.Null(PluginTabRules.GlyphFor("abc", false));
        Assert.Null(PluginTabRules.GlyphFor(null, false));
    }

    private static byte[] Png(int size, uint width, uint height)
    {
        var b = new byte[size];
        new Random(size).NextBytes(b);
        byte[] magic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        magic.CopyTo(b, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), 13);
        "IHDR"u8.CopyTo(b.AsSpan(12));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), height);
        return b;
    }

    [Fact]
    public void A_picture_must_be_a_small_png()
    {
        Assert.StartsWith("data:image/png;base64,", PluginTabRules.IconUri(Png(200, 48, 48)));
        Assert.Null(PluginTabRules.IconUri(null));
        Assert.Null(PluginTabRules.IconUri([]));
        Assert.Null(PluginTabRules.IconUri("<svg onload=alert(1)/>"u8.ToArray()));
        Assert.Null(PluginTabRules.IconUri(Png(PluginTab.MaxIconBytes + 1, 48, 48)));
        Assert.Null(PluginTabRules.IconUri(Png(200, 257, 48)));
        Assert.Null(PluginTabRules.IconUri(Png(200, 48, 4096)));
        Assert.Null(PluginTabRules.IconUri(Png(200, 0, 48)));
        // A bad picture costs the picture, not the tab.
        var shown = Resolve([Reg("p", Tab() with { Icon = "<svg/>"u8.ToArray(), Glyph = "💡" })]).Single();
        Assert.Null(shown.Image);
        Assert.Equal(PluginTabRules.GenericGlyph, shown.Glyph); // a third party's bulb is a built-in icon
    }

    // ---- Areas and profile ----

    [Theory]
    [InlineData("games", true, "games")]
    [InlineData("Apps", false, "apps")]
    [InlineData("browser", false, "browser")]
    [InlineData("news", true, "news")]
    [InlineData("always", true, "always")]
    [InlineData("always", false, "apps")]
    [InlineData("ALWAYS", false, "apps")]
    [InlineData("", true, "apps")]
    [InlineData("bogus", true, "apps")]
    [InlineData(null, false, "apps")]
    public void The_area_is_one_of_the_known_ones_defaults_to_apps_and_always_is_first_party_only(string? declared, bool firstParty, string expected) =>
        Assert.Equal(expected, PluginTabRules.AreaOf(declared, firstParty));

    [Fact]
    public void A_tab_is_hidden_when_its_area_is_off_and_an_always_tab_of_a_first_party_never()
    {
        var tabs = new[]
        {
            Reg("a", Tab("t", "Alpha", area: "apps")), Reg("b", Tab("t", "Beta", area: "games")), Reg("c", Tab("t", "Gamma", area: "browser")),
            Reg("d", Tab("t", "Delta", area: "always"), PluginOrigin.FirstParty), Reg("e", Tab("t", "Eps", area: "nonsense"))
        };
        Assert.Equal(["Delta", "Alpha", "Beta", "Gamma", "Eps"], Resolve(tabs).Select(s => s.Title));
        Assert.Equal(["Delta", "Beta", "Gamma"], Resolve(tabs, new AreaSet([FeatureAreas.Apps])).Select(s => s.Title));
        Assert.Equal(["Delta", "Alpha", "Gamma", "Eps"], Resolve(tabs, new AreaSet([FeatureAreas.Games])).Select(s => s.Title));
        Assert.Equal(["Delta", "Alpha", "Beta", "Eps"], Resolve(tabs, new AreaSet([FeatureAreas.Browser])).Select(s => s.Title));
        // The default area (nothing declared) is Apps: off with Apps.
        Assert.Empty(Resolve([Reg("a", new PluginTab("t", "p", "Alpha", "Alpha"))], new AreaSet([FeatureAreas.Apps])));
    }

    [Fact]
    public void A_third_party_that_says_always_is_hidden_when_apps_is_off_and_a_first_party_is_not()
    {
        var evil = Reg("evil", Tab("t", "Sneaky", area: "always"));
        var own = Reg("own", Tab("t", "Eigen", area: "always"), PluginOrigin.FirstParty);
        // Every area but Browser is off: the "always" of the third party counts as Apps and is gone, the first party's stays.
        var onlyBrowser = new AreaSet([FeatureAreas.Games, FeatureAreas.Apps, FeatureAreas.News]);
        Assert.Equal(["Eigen"], Resolve([evil, own], onlyBrowser).Select(s => s.Title));
        Assert.Equal("apps", Resolve([evil]).Single().Area);
        // Every area off by hand counts as every area on (a launcher needs one): the third party shows again, as Apps.
        var allOff = new AreaSet([FeatureAreas.Games, FeatureAreas.Apps, FeatureAreas.Browser, FeatureAreas.News]);
        Assert.Equal(2, Resolve([evil, own], allOff).Count);
    }

    [Fact]
    public void A_child_profile_sees_no_plugin_tabs_except_those_a_first_party_marks_child_safe()
    {
        var tabs = new[]
        {
            Reg("a", Tab("t", "Alpha")), Reg("own", Tab("t", "Eigen"), PluginOrigin.FirstParty),
            Reg("home", Tab("t", "Smart Home") with { ChildSafe = true }, PluginOrigin.FirstParty)
        };
        Assert.Equal(3, Resolve(tabs).Count);
        // Both directions: a first-party tab without the flag is hidden, with it it shows; nobody else shows.
        Assert.Equal(["Smart Home"], Resolve(tabs, child: true).Select(s => s.Title));
        Assert.True(Resolve(tabs, child: true).Single().ChildSafe);
    }

    [Fact]
    public void ChildSafe_is_ignored_for_a_third_party_and_for_a_local_plugin()
    {
        foreach (var origin in new[] { PluginOrigin.ThirdParty, PluginOrigin.Local })
        {
            var tab = Reg("evil", Tab("t", "Alpha") with { ChildSafe = true }, origin);
            Assert.Empty(Resolve([tab], child: true));
            Assert.False(Resolve([tab]).Single().ChildSafe);
        }
    }

    [Fact]
    public void On_the_locked_couch_only_first_party_tabs_stay()
    {
        var tabs = new[] { Reg("a", Tab("t", "Alpha")), Reg("own", Tab("t", "Eigen"), PluginOrigin.FirstParty) };
        Assert.Equal(["Eigen", "Alpha"], Resolve(tabs).Select(s => s.Title));
        Assert.Equal(["Eigen"], Resolve(tabs, locked: true).Select(s => s.Title));
        // Locked and a child: nothing unless it is first party and child safe.
        Assert.Empty(Resolve(tabs, child: true, locked: true));
        var safe = Reg("home", Tab("t", "Smart Home") with { ChildSafe = true }, PluginOrigin.FirstParty);
        Assert.Single(Resolve([safe], child: true, locked: true));
    }

    [Fact]
    public void A_plugin_that_was_switched_off_or_removed_loses_its_tab_at_once_while_it_stays_loaded()
    {
        var enabled = true;
        var manager = new PluginManager([(Path.Combine(_dir, "plugins"), true)], _dir, _ => enabled) { Trust = new PluginTrust(_dir, TrustKey) };
        var ctx = manager.ContextFor("tool", new PluginManager.PluginGrant("tool", PluginOrigin.ThirdParty, new HashSet<string> { PluginCapabilities.Pages, PluginCapabilities.Tabs }), markLoaded: true);
        ctx.RegisterPage(Page());
        ctx.RegisterTab(Tab());
        Assert.Single(manager.ResolveTabs(AreaSet.AllOn, false));
        var raised = 0;
        manager.TabsChanged += () => raised++;
        enabled = false;
        manager.RaiseTabsChanged();
        Assert.Equal(1, raised);
        Assert.Empty(manager.ResolveTabs(AreaSet.AllOn, false));
        enabled = true;
        Assert.Single(manager.ResolveTabs(AreaSet.AllOn, false));
        manager.Trust!.MarkRemoved("tool");
        Assert.Empty(manager.ResolveTabs(AreaSet.AllOn, false));
    }

    [Fact]
    public void The_manager_applies_area_and_child_to_what_a_plugin_registered()
    {
        var ctx = Context(PluginOrigin.ThirdParty, out var manager, PluginCapabilities.Pages, PluginCapabilities.Tabs);
        ctx.RegisterPage(Page());
        ctx.RegisterTab(Tab(area: "games"));
        Assert.Single(manager.ResolveTabs(AreaSet.AllOn, false));
        Assert.Empty(manager.ResolveTabs(new AreaSet([FeatureAreas.Games]), false));
        Assert.Empty(manager.ResolveTabs(AreaSet.AllOn, true));
    }

    // ---- The locked couch ----

    [Fact]
    public void The_bridge_call_for_tabs_is_open_while_locked_and_the_page_keeps_its_own_lock_rules()
    {
        Assert.Contains("getPluginTabs", LockPolicy.OpenWhileLocked);
        Assert.Contains("getPluginTabs", FeatureAreas.AlwaysMethods);
        Assert.False(PluginPolicy.MayView(true, Page("p", locked: false)));
        Assert.True(PluginPolicy.MayView(true, Page("p", locked: true)));
        // A row of a third-party plugin is never pressable on the locked couch, even if it is shown.
        var row = new PluginRow("r", "Row") { AllowedWhileLocked = true };
        Assert.Equal("lock.unlockFirst", PluginPolicy.MayPress(true, PluginOrigin.ThirdParty, row, false, "a").Refused);
    }

    // ---- A tab can only show a page of its own plugin ----

    [Fact]
    public void A_tab_never_shows_a_page_of_another_plugin()
    {
        var tabs = new[] { Reg("evil", Tab(page: "secret")) };
        // The page "secret" exists for another plugin only.
        Assert.Empty(Resolve(tabs, pageExists: (plugin, page) => plugin == "other" && page == "secret"));
        Assert.Single(Resolve(tabs, pageExists: (plugin, page) => plugin == "evil" && page == "secret"));
    }
}
