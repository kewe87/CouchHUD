using CouchHud.Core.Config;
using CouchHud.Core.Library;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

public class CustomEntryLocksTests
{
    private static CustomEntry E(string name) => new() { Name = name, Path = @"D:\a\" + name + ".exe" };

    private static IReadOnlyList<LibraryItem> Items(AppConfig c) => CustomEntries.From(c.Apps);

    private static List<string> Ids(AppConfig c) => Items(c).Select(i => i.Id).ToList();

    private static void Remove(AppConfig c, int index)
    {
        var before = Items(c);
        c.Apps.Remove(c.Apps[index]);
        CustomEntryLocks.Rekey(c, before, index, Items(c));
    }

    private static AppConfig Config(params string[] names)
    {
        var c = new AppConfig { Apps = [.. names.Select(E)] };
        c.LockedItemsAllChildren = [];
        return c;
    }

    [Fact]
    public void The_lock_of_a_later_duplicate_moves_to_the_id_it_takes_over()
    {
        var c = Config("Kodi", "kodi");
        c.AddProfile("Kid");
        c.Profile(ProfileIds.Default).Rules.LockedItems.Add("custom:kodi-2");

        Remove(c, 0);

        Assert.Equal(["custom:kodi"], Ids(c));
        Assert.Equal(["custom:kodi"], c.Profile(ProfileIds.Default).Rules.LockedItems);
    }

    [Fact]
    public void A_chain_of_three_duplicates_keeps_every_lock_with_its_entry_in_every_list()
    {
        var c = Config("Kodi", "KODI", "kodi");
        var kid = c.AddProfile("Kid");
        c.Profile(kid.Id).Rules.LockedItems.Add("custom:kodi-3");
        c.LockedItemsAllChildren!.Add("custom:kodi-2");

        Remove(c, 0);

        Assert.Equal(["custom:kodi", "custom:kodi-2"], Ids(c));
        Assert.Contains("custom:kodi-2", c.Profile(kid.Id).Rules.LockedItems);
        Assert.DoesNotContain("custom:kodi-3", c.Profile(kid.Id).Rules.LockedItems);
        Assert.Contains("custom:kodi", c.LockedItemsAllChildren);
        Assert.DoesNotContain("custom:kodi-3", c.LockedItemsAllChildren);
    }

    [Fact]
    public void The_locks_of_a_profile_the_app_ignores_move_too()
    {
        var c = Config("Kodi", "kodi");
        var kid = c.AddProfile("Kid");
        c.Profile(kid.Id).Rules.LockedItems.Add("custom:kodi-2");
        // A damaged entry: AllProfiles leaves it out.
        c.Profiles![0].Data!.Settings = null!;
        Assert.DoesNotContain(c.AllProfiles, p => p.Id == kid.Id);

        Remove(c, 0);

        Assert.Equal(["custom:kodi"], c.Profiles[0].Data!.Rules.LockedItems);
    }

    [Fact]
    public void A_lock_never_falls_away_from_an_entry_that_still_exists()
    {
        var c = Config("Kodi", "kodi", "Plex");
        c.Profile(ProfileIds.Default).Rules.LockedItems.AddRange(["custom:kodi", "custom:plex"]);

        Remove(c, 1);

        Assert.Equal(["custom:kodi", "custom:plex"], Ids(c));
        Assert.Contains("custom:kodi", c.Profile(ProfileIds.Default).Rules.LockedItems);
        Assert.Contains("custom:plex", c.Profile(ProfileIds.Default).Rules.LockedItems);
    }

    [Fact]
    public void The_lock_of_a_removed_entry_that_no_entry_carries_goes_so_a_new_one_does_not_inherit_it()
    {
        var c = Config("Kodi", "Plex");
        c.LockedItemsAllChildren!.Add("custom:plex");
        c.Profile(ProfileIds.Default).Rules.LockedItems.Add("custom:plex");

        Remove(c, 1);

        Assert.Empty(c.LockedItemsAllChildren);
        Assert.Empty(c.Profile(ProfileIds.Default).Rules.LockedItems);
    }

    [Fact]
    public void A_shifted_entry_takes_its_hidden_mark_overrides_favorite_and_play_time_along()
    {
        var c = Config("Kodi", "kodi", "KODI");
        var kid = c.AddProfile("Kid");
        c.Ignored = [new IgnoredEntry { Keys = ["id:custom:kodi-2"], Name = "kodi" }];
        c.Overrides["custom:kodi-2"] = new ItemOverride { Name = "Second", Hidden = true };
        c.Profile(kid.Id).Favorites.Add("custom:kodi-3");
        c.Profile(kid.Id).PlaytimeMinutes["custom:kodi-3"] = 42;
        c.Profile(kid.Id).Collections.Add(new ItemCollection { Id = "c1", Name = "x", Items = ["custom:kodi-2"] });

        Remove(c, 0);

        Assert.Equal(["id:custom:kodi"], c.Ignored[0].Keys);
        Assert.True(c.Overrides["custom:kodi"].Hidden);
        Assert.Equal("Second", c.Overrides["custom:kodi"].Name);
        Assert.DoesNotContain("custom:kodi-2", c.Overrides.Keys);
        Assert.Equal(["custom:kodi-2"], c.Profile(kid.Id).Favorites);
        Assert.Equal(42, c.Profile(kid.Id).PlaytimeMinutes["custom:kodi-2"]);
        Assert.Equal(["custom:kodi"], c.Profile(kid.Id).Collections[0].Items);
    }

    [Fact]
    public void The_play_time_key_of_an_entry_without_a_folder_follows_it_in_the_ledger_log()
    {
        var c = new AppConfig { Apps = [new CustomEntry { Name = "Site", Url = "https://a.example" }, new CustomEntry { Name = "site", Url = "https://b.example" }] };
        var before = Items(c);
        c.Apps.RemoveAt(0);

        CustomEntryLocks.Rekey(c, before, 0, Items(c));

        Assert.Contains(c.LimitKeyMoves, m => m.From == "custom:site-2" && m.To == "custom:site");
    }

    [Fact]
    public void Shifts_lists_the_entries_whose_id_changes_and_nothing_for_the_last_one()
    {
        Assert.Equal([("b-2", "b")], CustomEntryLocks.Shifts(["a", "b", "b-2"], 1, ["a", "b"]));
        Assert.Empty(CustomEntryLocks.Shifts(["a", "b", "b-2"], 2, ["a", "b"]));
    }

    private static LibraryItem Item(string id) => new() { Id = id, Name = id, Source = "custom", Launch = new LaunchTarget(LaunchKind.Exe, @"D:\a\" + id + ".exe") };

    [Fact]
    public void Locked_is_judged_as_the_parental_service_judges_it()
    {
        var item = Item("custom:kodi");
        var c = Config("Kodi");
        var kid = c.AddProfile("Kid");
        Assert.False(CustomEntryLocks.IsLocked(c, item, [item]));

        c.Profile(kid.Id).Rules.LockedItems.Add("custom:kodi");
        Assert.True(CustomEntryLocks.IsLocked(c, item, [item]));
        c.Profile(kid.Id).Rules.LockedItems.Clear();

        c.Profile(kid.Id).Rules.LockEverything = true;
        Assert.True(CustomEntryLocks.IsLocked(c, item, [item]));
        c.Profile(kid.Id).Rules.LockEverything = false;

        c.LockedItemsAllChildren = null;
        Assert.True(CustomEntryLocks.IsLocked(c, item, [item]));
        c.LockedItemsAllChildren = [];
        c.LockedItemsAllChildrenUnreadable = true;
        Assert.True(CustomEntryLocks.IsLocked(c, item, [item]));
        c.LockedItemsAllChildrenUnreadable = false;

        c.LockedItemsAllChildren = ["custom:kodi"];
        Assert.True(CustomEntryLocks.IsLocked(c, item, [item]));
    }

    [Fact]
    public void A_lock_in_a_profile_the_app_ignores_still_counts()
    {
        var item = Item("custom:kodi");
        var c = Config("Kodi");
        var kid = c.AddProfile("Kid");
        c.Profile(kid.Id).Rules.LockedItems.Add("custom:kodi");
        c.Profiles![0].Data!.Settings = null!;

        Assert.DoesNotContain(c.AllProfiles, p => p.Id == kid.Id);
        Assert.True(CustomEntryLocks.IsLocked(c, item, [item]));
    }
}
