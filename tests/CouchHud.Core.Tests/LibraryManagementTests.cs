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
