using CouchHud.Core.Config;
using CouchHud.Core.Library;
using CouchHud.Core.Parental;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>The Favorites tab (formerly Home): the three modes, order, switched off areas, the child's filter and the migration.</summary>
public class FavoritesShelfTests
{
    private static LibraryItem Item(string id, ItemType type = ItemType.Game, long played = 0, bool hidden = false, string target = "C:\\x\\a.exe") =>
        new() { Id = id, Source = "custom", Name = id, Type = type, LastPlayed = played, Hidden = hidden, Launch = new LaunchTarget(LaunchKind.Exe, target) };

    private static List<string> Ids(IEnumerable<ShelfEntry> e) => e.Select(x => x.Id).ToList();

    private static readonly Bookmark[] Marks = [new() { Name = "Tube", Url = "https://tube.example/" }];

    private static List<ShelfEntry> Build(IEnumerable<LibraryItem> lib, string[] favs, string mode, bool sites = false, AreaSet? areas = null, Func<LibraryItem, bool>? allowed = null) =>
        FavoritesShelf.Build(lib, favs, Marks, mode, sites, areas ?? AreaSet.AllOn, allowed ?? (_ => true));

    private static readonly LibraryItem[] Lib =
    [
        Item("g1", played: 100), Item("g2", played: 300), Item("a1", ItemType.App, played: 200), Item("g3"), Item("a2", ItemType.App)
    ];

    [Fact]
    public void Favorites_mode_shows_only_favorites_in_the_order_they_were_marked_for_games_and_apps()
    {
        var shelf = Build(Lib, ["a2", "g1"], "favorites");
        Assert.Equal(["a2", "g1"], Ids(shelf));
        Assert.All(shelf, e => Assert.True(e.Favorite));
    }

    [Fact]
    public void Recent_mode_shows_the_entries_used_last_newest_first_and_never_the_unplayed()
    {
        Assert.Equal(["g2", "a1", "g1"], Ids(Build(Lib, ["g3"], "recent")));
    }

    [Fact]
    public void Combined_mode_puts_favorites_first_then_the_recent_ones_that_are_no_favorites()
    {
        var shelf = Build(Lib, ["g1", "g3"], "combined");
        Assert.Equal(["g1", "g3", "g2", "a1"], Ids(shelf));
        Assert.Equal([true, true, false, false], shelf.Select(e => e.Favorite));
    }

    [Fact]
    public void An_unknown_mode_is_the_combined_view_and_the_default_is_combined()
    {
        Assert.Equal("combined", FavoritesShelf.NormalizeMode(null));
        Assert.Equal("combined", FavoritesShelf.NormalizeMode("everything"));
        Assert.Equal("combined", new AppConfig().FavoritesMode);
        Assert.Equal(Ids(Build(Lib, ["g1"], "combined")), Ids(Build(Lib, ["g1"], "nonsense")));
    }

    [Fact]
    public void The_recent_part_is_bounded()
    {
        var many = Enumerable.Range(1, 60).Select(i => Item("g" + i, played: i)).ToList();
        Assert.Equal(FavoritesShelf.MaxRecent, Build(many, [], "recent").Count);
    }

    [Fact]
    public void Hidden_entries_and_ids_that_are_not_in_the_library_never_show()
    {
        var lib = new[] { Item("g1", played: 5, hidden: true), Item("g2", played: 4) };
        Assert.Equal(["g2"], Ids(Build(lib, ["g1", "ghost", "g2"], "combined")));
    }

    [Fact]
    public void A_switched_off_area_takes_its_entries_out_of_every_mode()
    {
        var noGames = new AreaSet([FeatureAreas.Games]);
        foreach (var mode in new[] { "favorites", "recent", "combined" })
            Assert.All(Build(Lib, ["g1", "a2"], mode, areas: noGames), e => Assert.StartsWith("a", e.Id));
        var noApps = new AreaSet([FeatureAreas.Apps]);
        foreach (var mode in new[] { "favorites", "recent", "combined" })
            Assert.All(Build(Lib, ["g1", "a2"], mode, areas: noApps), e => Assert.StartsWith("g", e.Id));
    }

    [Fact]
    public void A_web_entry_needs_the_browser_area_as_well()
    {
        var tube = Item("a-web", ItemType.App, played: 9, target: "https://tube.example/") with { Launch = new LaunchTarget(LaunchKind.Url, "https://tube.example/") };
        Assert.Contains("a-web", Ids(Build([tube], ["a-web"], "combined")));
        Assert.Empty(Build([tube], ["a-web"], "combined", areas: new AreaSet([FeatureAreas.Browser])));
    }

    [Fact]
    public void Websites_show_only_when_switched_on_with_the_browser_and_only_as_known_bookmarks()
    {
        var favs = new[] { FavoritesShelf.SiteId("https://tube.example/"), FavoritesShelf.SiteId("https://gone.example/"), "g1" };
        Assert.Equal(["g1"], Ids(Build(Lib, favs, "favorites", sites: false)));
        var shelf = Build(Lib, favs, "favorites", sites: true);
        Assert.Equal(["g1", favs[0]], Ids(shelf));
        Assert.Equal("Tube", shelf[1].Name);
        Assert.Equal("https://tube.example/", shelf[1].Url);
        Assert.Equal(["g1"], Ids(Build(Lib, favs, "favorites", sites: true, areas: new AreaSet([FeatureAreas.Browser]))));
        Assert.DoesNotContain(Build(Lib, favs, "recent", sites: true), e => e.Kind == "site");
    }

    [Fact]
    public void A_child_never_sees_locked_entries_or_entries_over_the_age_limit_even_as_favorites()
    {
        var rules = new ParentalConfig { Enabled = true, MaxAge = 12, BlockUnknownAge = true, LockedItems = ["g2"] };
        var ages = new Dictionary<string, int?> { ["g1"] = 7, ["g2"] = 3, ["g3"] = 18 };
        bool Allowed(LibraryItem i) => FavoritesShelf.ChildAllows(rules, i, Lib, i.Type == ItemType.Game ? ages.GetValueOrDefault(i.Id) : null);
        foreach (var mode in new[] { "favorites", "recent", "combined" })
        {
            var shown = Ids(Build(Lib, ["g1", "g2", "g3", "a1"], mode, allowed: Allowed));
            Assert.DoesNotContain("g2", shown);   // locked by the parents
            Assert.DoesNotContain("g3", shown);   // over the age limit
        }
        Assert.Contains("g1", Ids(Build(Lib, ["g1"], "favorites", allowed: Allowed)));
        // Unknown age with the strict setting counts as over the limit.
        Assert.False(FavoritesShelf.ChildAllows(rules, Item("g9"), Lib, null));
        // Time rules do not hide anything, and without parental controls everything passes.
        Assert.True(FavoritesShelf.ChildAllows(new ParentalConfig { Enabled = false, LockedItems = ["g1"] }, Item("g1"), Lib, 18));
    }

    [Fact]
    public void Marking_a_hidden_entry_as_favorite_does_not_make_it_show()
    {
        var rules = new ParentalConfig { Enabled = true, LockedItems = ["g1"] };
        var shelf = Build(Lib, ["g1"], "combined", allowed: i => FavoritesShelf.ChildAllows(rules, i, Lib, null));
        Assert.DoesNotContain("g1", Ids(shelf));
    }

    // ----- the list that is kept -----

    [Fact]
    public void The_favorites_list_is_cleaned_bounded_and_validated()
    {
        var dirty = new List<string> { "g1", "g1", "", "  ", "site:javascript:alert(1)", "site:https://user@evil.example/", "site:https://ok.example/", "bad\nid", new string('x', 5000) };
        dirty.AddRange(Enumerable.Range(0, 300).Select(i => "n" + i));
        var clean = FavoritesShelf.Clean(dirty);
        Assert.Equal(2 + 300, clean.Count);   // valid entries are never cut on load; the bound is for adding
        Assert.Equal(["g1", "site:https://ok.example/"], clean.Take(2));
        Assert.DoesNotContain(clean, id => id.Contains('\n') || id.StartsWith("site:javascript") || id.Contains('@'));
        Assert.Empty(FavoritesShelf.Clean(null));
    }

    [Fact]
    public void Adding_a_favorite_stops_at_the_bound_and_adding_twice_keeps_one()
    {
        var list = new List<string>();
        Assert.True(FavoritesShelf.TryAdd(list, "a"));
        Assert.True(FavoritesShelf.TryAdd(list, "a"));
        Assert.Single(list);
        for (var i = 0; list.Count < FavoritesShelf.MaxFavorites; i++) list.Add("x" + i);
        Assert.False(FavoritesShelf.TryAdd(list, "one-too-many"));
        Assert.Equal(FavoritesShelf.MaxFavorites, list.Count);
    }

    // ----- migration -----

    [Fact]
    public void An_existing_config_lands_on_the_combined_view_and_keeps_its_favorites_and_home_collections()
    {
        var config = ConfigStore.Parse("""
            { "favorites": ["g1", "g1", "a1"], "collections": [{ "id": "c1", "name": "Couch", "items": ["g1"], "onHome": true }] }
            """)!;
        Assert.Equal("combined", config.FavoritesMode);
        Assert.False(config.FavoritesSites);
        Assert.Equal(["g1", "a1"], config.Favorites);
        Assert.True(config.Collections.Single().OnHome);
    }

    [Fact]
    public void A_broken_mode_or_favorites_list_in_the_file_is_repaired_for_every_profile()
    {
        var config = ConfigStore.Parse("""
            { "favoritesMode": "weird", "favorites": ["", "g1"],
              "profiles": [{ "id": "kid", "name": "Kid", "avatar": "", "color": "", "data": { "favorites": ["a", "a", "site:ftp://x/"] } }] }
            """)!;
        Assert.Equal("combined", config.FavoritesMode);
        Assert.Equal(["g1"], config.Favorites);
        Assert.Equal(["a"], config.Profiles!.Single().Data!.Favorites);
    }

    [Fact]
    public void The_chosen_mode_and_the_websites_switch_survive_a_save()
    {
        var config = ConfigStore.Parse("""{ "favoritesMode": "recent", "favoritesSites": true }""")!;
        Assert.Equal("recent", config.FavoritesMode);
        Assert.True(config.FavoritesSites);
    }

    [Fact]
    public void The_new_settings_and_calls_have_their_areas_and_work_on_the_locked_couch_where_harmless()
    {
        Assert.Equal(FeatureAreas.Browser, FeatureAreas.AreaOfSetting("favoritesSites"));
        Assert.Equal(FeatureAreas.Always, FeatureAreas.AreaOfSetting("favoritesMode"));
        Assert.False(new AreaSet([FeatureAreas.Browser]).AllowsSetting("favoritesSites"));
        // Taking a favorite website off works with the browser area off; adding is refused inside the host (Hud.ToggleFavoriteSite).
        Assert.True(new AreaSet([FeatureAreas.Browser]).AllowsMethod("toggleFavoriteSite"));
        Assert.False(FeatureAreas.BridgeMethods.ContainsKey("toggleFavoriteSite"));
        Assert.True(CouchHud.Core.Guard.LockPolicy.OpenWhileLocked.Contains("toggleFavoriteSite"));
        Assert.False(CouchHud.Core.Guard.LockPolicy.OpenWhileLocked.Contains("favoritesSites"));
    }
}
