using CouchHud.Core.Config;
using CouchHud.Core.Files;
using CouchHud.Core.Library;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

public class LibraryManagementTests
{
    private static string TempDir(string prefix) => Directory.CreateTempSubdirectory(prefix).FullName;

    private static void Touch(string path, int size)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[size]);
    }

    [Fact]
    public void ExeScanner_picks_one_game_exe_per_folder_and_skips_junk()
    {
        var root = TempDir("couchhud-games-");
        Touch(Path.Combine(root, "Hades II", "Hades2.exe"), 5000);
        Touch(Path.Combine(root, "Hades II", "unins000.exe"), 9000);
        Touch(Path.Combine(root, "Hades II", "_CommonRedist", "vcredist_x64.exe"), 20000);
        Touch(Path.Combine(root, "Some_Indie-Game", "bin", "game.exe"), 3000);
        Touch(Path.Combine(root, "Some_Indie-Game", "UnityCrashHandler64.exe"), 4000);
        Touch(Path.Combine(root, "Only Installers", "setup.exe"), 1000);
        Directory.CreateDirectory(Path.Combine(root, "Empty"));

        var found = ExeScanner.Scan(root);

        Assert.Equal(2, found.Count);
        var hades = Assert.Single(found, f => f.Path.EndsWith("Hades2.exe"));
        Assert.Equal("Hades II", hades.Name);
        var indie = Assert.Single(found, f => f.Path.EndsWith("game.exe"));
        Assert.Equal("Some Indie Game", indie.Name);
    }

    [Fact]
    public void ExeScanner_treats_a_folder_with_exes_as_one_game()
    {
        var root = Path.Combine(TempDir("couchhud-game-"), "Celeste");
        Touch(Path.Combine(root, "Celeste.exe"), 100);
        Touch(Path.Combine(root, "Content", "readme.txt"), 10);

        var found = Assert.Single(ExeScanner.Scan(root));
        Assert.EndsWith("Celeste.exe", found.Path);
    }

    [Theory]
    [InlineData("unins000.exe", true)]
    [InlineData("UnityCrashHandler64.exe", true)]
    [InlineData("vc_redist.x64.exe", true)]
    [InlineData("Hades2.exe", false)]
    [InlineData("retroarch.exe", false)]
    public void ExeScanner_junk_names(string name, bool junk) => Assert.Equal(junk, ExeScanner.IsJunk(name));

    [Fact]
    public void FileBrowser_lists_folders_first_and_filters_extensions()
    {
        var root = TempDir("couchhud-browse-");
        Directory.CreateDirectory(Path.Combine(root, "zeta"));
        Directory.CreateDirectory(Path.Combine(root, "Alpha"));
        Touch(Path.Combine(root, "game.exe"), 42);
        Touch(Path.Combine(root, "notes.txt"), 1);

        var all = FileBrowser.List(root);
        Assert.Equal(new[] { "Alpha", "zeta", "game.exe", "notes.txt" }, all.Entries.Select(e => e.Name));
        Assert.Equal(Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar)), all.Parent);

        var exes = FileBrowser.List(root, [".EXE"]);
        Assert.Equal(new[] { "Alpha", "zeta", "game.exe" }, exes.Entries.Select(e => e.Name));
        Assert.Equal(42, exes.Entries.Last().Size);
    }

    [Fact]
    public void FileBrowser_without_path_lists_roots()
    {
        var roots = FileBrowser.List(null);
        Assert.Null(roots.Path);
        Assert.NotEmpty(roots.Entries);
        Assert.Contains(roots.Entries, e => e.Kind == EntryKind.Drive);
    }

    [Fact]
    public async Task Edits_survive_rescans_and_can_be_undone()
    {
        var (library, config, dir) = await NewLibrary();

        library.EditItem("custom:kodi", "Mediacenter", "game", hidden: null);
        var renamed = (await library.ScanAsync()).Single(i => i.Id == "custom:kodi");
        Assert.Equal("Mediacenter", renamed.Name);
        Assert.Equal(ItemType.Game, renamed.Type);

        library.EditItem("custom:kodi", "", "app", hidden: true);
        var reset = library.Find("custom:kodi")!;
        Assert.Equal("Kodi", reset.Name);
        Assert.True(reset.Hidden);

        library.EditItem("custom:kodi", null, null, hidden: false);
        Assert.DoesNotContain(new ConfigStore(dir).Current.Overrides, o => o.Value.Hidden || o.Value.Name is not null && o.Value.Type is null);
        Assert.Throws<ArgumentException>(() => library.EditItem("custom:kodi", null, "music", null));
    }

    [Fact]
    public async Task Custom_entries_can_be_added_and_removed()
    {
        var (library, config, dir) = await NewLibrary();

        library.AddCustomEntries([new CustomEntry { Name = "RetroArch", Type = "game", Path = @"D:\RetroArch\retroarch.exe" }]);
        var items = await library.ScanAsync();
        Assert.Contains(items, i => i.Id == "custom:retroarch" && i.Type == ItemType.Game);

        library.RemoveCustomEntry("custom:retroarch");
        Assert.DoesNotContain(library.Items, i => i.Id == "custom:retroarch");
        Assert.DoesNotContain(new ConfigStore(dir).Current.Apps, a => a.Name == "RetroArch");
        Assert.Throws<KeyNotFoundException>(() => library.RemoveCustomEntry("steam:1"));
        Assert.Throws<ArgumentException>(() => library.AddCustomEntries([new CustomEntry { Name = "" }]));
    }

    [Fact]
    public async Task A_long_entry_name_is_cut_when_added_so_its_id_survives_a_restart()
    {
        var (library, config, dir) = await NewLibrary();
        var longName = new string('a', ConfigLimits.MaxEntryNameLength + 50);

        library.AddCustomEntries([new CustomEntry { Name = longName, Path = @"D:\Bigig.exe" }]);
        var id = (await library.ScanAsync()).Single(i => i.Source == "custom" && i.Name.StartsWith("aaaa")).Id;

        Assert.Equal(ConfigLimits.MaxEntryNameLength, config.Current.Apps.Single(a => a.Name.StartsWith("aaaa")).Name.Length);
        var reloaded = new ConfigStore(dir);
        Assert.Contains(CustomEntries.From(reloaded.Current.Apps), i => i.Id == id);
    }

    [Fact]
    public async Task Spaces_before_a_long_name_do_not_leave_an_empty_entry()
    {
        var (library, config, _) = await NewLibrary();

        library.AddCustomEntries([new CustomEntry { Name = new string(' ', ConfigLimits.MaxEntryNameLength + 5) + "Hidden", Path = @"D:\Big\big.exe" }]);

        var stored = config.Current.Apps.Single(a => a.Name.Contains("Hidden"));
        Assert.Equal("Hidden", stored.Name);
        Assert.Contains(await library.ScanAsync(), i => i.Id == "custom:hidden");
        Assert.Throws<ArgumentException>(() => library.AddCustomEntries([new CustomEntry { Name = new string(' ', 300) + "x", Path = new string('p', ConfigLimits.MaxEntryPathLength + 1) }]));
        Assert.Throws<ArgumentException>(() => library.AddCustomEntries([new CustomEntry { Name = "Args", Path = @"D:\a.exe", Args = [new string('x', ConfigLimits.MaxEntryArgsLength)] }]));
    }

    [Fact]
    public async Task Removal_asks_the_hook_inside_the_update_and_nothing_goes_when_it_refuses()
    {
        var (library, config, _) = await NewLibrary();
        library.AddCustomEntries([new CustomEntry { Name = "Dup", Path = @"D:\a\one.exe" }, new CustomEntry { Name = "dup", Path = @"D:\a\two.exe" }]);
        await library.ScanAsync();
        bool? seen = null;

        Assert.Throws<InvalidOperationException>(() => library.RemoveCustomEntry("custom:dup", (c, item, shifts) => { seen = shifts; throw new InvalidOperationException("grant"); }));

        Assert.True(seen);
        Assert.Equal(2, config.Current.Apps.Count(a => a.Name.ToLowerInvariant() == "dup"));
        // The last duplicate shifts nothing.
        library.RemoveCustomEntry("custom:dup-2", (c, item, shifts) => seen = shifts);
        Assert.False(seen);
        Assert.True(library.RemoveCustomEntry("custom:dup", null) == false);
    }

    [Fact]
    public async Task Removing_the_first_of_two_duplicates_reports_the_shift_and_moves_the_lock()
    {
        var (library, config, _) = await NewLibrary();
        library.AddCustomEntries([new CustomEntry { Name = "Dup", Path = @"D:\a\one.exe" }, new CustomEntry { Name = "dup", Path = @"D:\a\two.exe" }]);
        config.Update(c => c.Parental.LockedItems.Add("custom:dup-2"));
        await library.ScanAsync();

        Assert.True(library.RemoveCustomEntry("custom:dup"));

        Assert.Contains("custom:dup", config.Current.Parental.LockedItems);
        Assert.DoesNotContain("custom:dup-2", config.Current.Parental.LockedItems);
    }

    [Fact]
    public async Task A_rename_and_a_long_override_name_are_capped_and_a_too_long_artwork_link_is_refused()
    {
        var (library, config, _) = await NewLibrary();

        library.EditItem("custom:kodi", new string('n', ConfigLimits.MaxEntryNameLength + 100), null, null);

        Assert.Equal(ConfigLimits.MaxEntryNameLength, config.Current.Overrides["custom:kodi"].Name!.Length);
        Assert.Throws<ArgumentException>(() => ArtworkOverrides.Normalize("https://example.com/" + new string('a', ConfigLimits.MaxEntryUrlLength)));
        Assert.Throws<ArgumentException>(() => library.AddCustomEntries([new CustomEntry { Name = "Img", Path = @"D:\a.exe", Image = "https://x/" + new string('a', ConfigLimits.MaxEntryUrlLength) }]));
    }

    [Fact]
    public async Task After_a_shifted_removal_the_library_has_the_current_ids_at_once()
    {
        var (library, config, _) = await NewLibrary();
        library.AddCustomEntries([new CustomEntry { Name = "Dup", Path = @"D:\a\one.exe" }, new CustomEntry { Name = "dup", Path = @"D:\a\two.exe" }, new CustomEntry { Name = "DUP", Path = @"D:\a\three.exe" }]);
        await library.ScanAsync();

        Assert.True(library.RemoveCustomEntry("custom:dup"));

        Assert.Equal(["custom:dup", "custom:dup-2"], library.Items.Where(i => i.Id.StartsWith("custom:dup")).Select(i => i.Id).Order().ToList());
        Assert.EndsWith("two.exe", library.Find("custom:dup")!.Launch.Target);
        // A quick second removal of "-2" removes the former third entry, as the page meant.
        library.RemoveCustomEntry("custom:dup-2");
        Assert.Single(config.Current.Apps, a => a.Name.ToLowerInvariant() == "dup");
        Assert.EndsWith("two.exe", config.Current.Apps.Single(a => a.Name.ToLowerInvariant() == "dup").Path);
    }

    [Fact]
    public async Task The_removed_entrys_records_go_and_the_shifted_entry_keeps_its_own()
    {
        var (library, config, _) = await NewLibrary();
        library.AddCustomEntries([new CustomEntry { Name = "Dup", Path = @"D:\a\one.exe" }, new CustomEntry { Name = "dup", Path = @"D:\a\two.exe" }]);
        config.Update(c =>
        {
            c.GameProfiles["custom:dup"] = new GameProfile();
            c.GameProfiles["custom:dup-2"] = new GameProfile { Width = 1920 };
            c.Profile(ProfileIds.Default).PlaytimeMinutes["custom:dup"] = 500;
            c.Profile(ProfileIds.Default).PlaytimeMinutes["custom:dup-2"] = 7;
        });
        await library.ScanAsync();

        library.RemoveCustomEntry("custom:dup");

        Assert.Equal(1920, config.Current.GameProfiles["custom:dup"].Width);
        Assert.DoesNotContain("custom:dup-2", config.Current.GameProfiles.Keys);
        Assert.Equal(7, config.Current.Profile(ProfileIds.Default).PlaytimeMinutes["custom:dup"]);
    }

    [Fact]
    public async Task Playtime_adds_up()
    {
        var (library, _, dir) = await NewLibrary();
        library.AddPlaytime("custom:kodi", 30);
        var item = library.AddPlaytime("custom:kodi", 12.5);
        Assert.Equal(42.5, item.PlaytimeMinutes);
        Assert.Equal(42.5, new ConfigStore(dir).Current.PlaytimeMinutes["custom:kodi"]);
    }

    [Fact]
    public async Task A_session_counts_as_last_played_and_as_the_last_session()
    {
        var (library, _, dir) = await NewLibrary();
        var ended = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);
        library.AddPlaytime("custom:kodi", 30, ended);
        var item = library.AddPlaytime("custom:kodi", 12.5, ended);
        Assert.Equal(12.5, item.LastSessionMinutes);
        Assert.Equal(ended.ToUnixTimeMilliseconds(), item.LastPlayed);
        Assert.Equal(12.5, new ConfigStore(dir).Current.LastSessionMinutes["custom:kodi"]);
        Assert.Equal(ended.ToUnixTimeMilliseconds(), new ConfigStore(dir).Current.LastLaunched["custom:kodi"]);
    }

    private static async Task<(LibraryService, ConfigStore, string)> NewLibrary()
    {
        var dir = TempDir("couchhud-lib-");
        var config = new ConfigStore(dir);
        config.Update(c => c.Apps = [new CustomEntry { Name = "Kodi", Type = "app", Url = "https://kodi.tv" }]);
        var artwork = new ArtworkResolver(new HttpClient(new Offline()), Path.Combine(dir, "art.json"), null);
        var library = new LibraryService(config, artwork, []);
        await library.ScanAsync();
        return (library, config, dir);
    }

    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("offline");
    }
}
