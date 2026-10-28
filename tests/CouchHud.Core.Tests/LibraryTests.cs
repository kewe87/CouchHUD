using System.Text.Json;
using CouchHud.Core.Config;
using CouchHud.Core.Library;
using CouchHud.Core.Platform;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

public class LibraryTests
{
    private static string Fixtures => Path.Combine(AppContext.BaseDirectory, "Fixtures");

    [Fact]
    public void Vdf_handles_nesting_escapes_comments_and_case()
    {
        var data = Vdf.Parse("""
            // comment
            "Root" {
              "Name" "say \"hi\""
              "Child" { "a" "1" }
              unquoted value
            }
            """);

        var root = data.GetObject("root")!;
        Assert.Equal("say \"hi\"", root.GetString("NAME"));
        Assert.Equal("1", root.GetObject("child")!.GetString("a"));
        Assert.Equal("value", root.GetString("unquoted"));
    }

    [Fact]
    public void Vdf_rejects_truncated_input()
    {
        var ex = Assert.Throws<FormatException>(() => Vdf.Parse("\"a\" { \"b\" \"c\""));
        Assert.Contains("unexpected end", ex.Message);
    }

    [Fact]
    public async Task Steam_reads_all_library_folders_and_skips_non_games()
    {
        // libraryfolders.vdf needs absolute paths, so build it in a temp copy.
        var tmp = Directory.CreateTempSubdirectory("couchhud-").FullName;
        CopyDirectory(Fixtures, tmp);
        var steamPath = Path.Combine(tmp, "steam");
        var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        static string Esc(string p) => p.Replace("\\", "\\\\");
        File.WriteAllText(vdf, File.ReadAllText(vdf)
            .Replace("__STEAM__", Esc(steamPath))
            .Replace("__LIB2__", Esc(Path.Combine(tmp, "lib2"))));

        var games = await new SteamScanner(NullRegistry.Instance, steamPath).GetItemsAsync(default);
        var byId = games.ToDictionary(g => g.Id);

        Assert.Equal(new[] { "steam:1245620", "steam:413150" }, byId.Keys.Order());
        Assert.Equal("ELDEN RING", byId["steam:1245620"].Name);
        Assert.Equal(1700000000L * 1000, byId["steam:1245620"].LastPlayed);
        Assert.Equal("steam://rungameid/1245620", byId["steam:1245620"].Launch.Target);
        Assert.Equal("Stardew \"Valley\"", byId["steam:413150"].Name);
        Assert.EndsWith("413150/library_600x900.jpg", byId["steam:413150"].Art.Cover[^1]);
    }

    [Fact]
    public async Task Epic_keeps_games_and_drops_dlc_and_broken_manifests()
    {
        var games = await new EpicScanner(Path.Combine(Fixtures, "epic")).GetItemsAsync(default);

        var game = Assert.Single(games);
        Assert.Equal("Fortnite", game.Name);
        Assert.Equal(
            "com.epicgames.launcher://apps/fn%3A4fe75bbc5a674f4f9b356b5c90567da5%3AFortnite?action=launch&silent=true",
            game.Launch.Target);
    }

    [Fact]
    public async Task Epic_returns_nothing_when_not_installed()
    {
        Assert.Empty(await new EpicScanner(Path.Combine(Fixtures, "nope")).GetItemsAsync(default));
    }

    [Fact]
    public void Custom_entries_map_exe_and_url()
    {
        var exe = Path.Combine(Path.GetTempPath(), "Kodi", "kodi.exe");
        var items = CustomEntries.From(
        [
            new CustomEntry { Name = "Kodi", Path = exe, Image = "https://example.com/kodi.png" },
            new CustomEntry { Name = "YouTube", Url = "https://www.youtube.com/tv", Type = "app" },
            new CustomEntry { Name = "Retro", Type = "game", Path = exe, Args = ["-f"] },
            new CustomEntry { Name = "invalid" }
        ]);

        Assert.Equal(3, items.Count);
        Assert.Equal(LaunchKind.Exe, items[0].Launch.Kind);
        Assert.Equal(ItemType.App, items[0].Type);
        Assert.Equal(Path.GetDirectoryName(exe), items[0].Launch.WorkingDirectory);
        Assert.Equal(new[] { "https://example.com/kodi.png" }, items[0].Art.Cover);
        Assert.Equal("custom:youtube", items[1].Id);
        Assert.Equal(ItemType.Game, items[2].Type);
        Assert.Equal(new[] { "-f" }, items[2].Launch.Args);
    }

    [Fact]
    public void Custom_local_image_becomes_file_url()
    {
        var image = Path.Combine(Path.GetTempPath(), "cover art.png");
        Assert.Equal(new Uri(image).AbsoluteUri, CustomEntries.ToUrl(image));
    }

    [Fact]
    public async Task Library_merges_providers_and_keeps_user_state()
    {
        var dir = Directory.CreateTempSubdirectory("couchhud-cfg-").FullName;
        var config = new ConfigStore(dir);
        config.Update(c =>
        {
            c.Apps = [];
            c.Favorites = ["fake:b"];
            c.LastLaunched = new() { ["fake:a"] = 42 };
        });
        var artwork = new ArtworkResolver(new HttpClient(new NoNetwork()), Path.Combine(dir, "art.json"), null);
        var library = new LibraryService(config, artwork, [new FakeProvider(), new BrokenProvider()]);

        var items = await library.ScanAsync();

        Assert.Equal(new[] { "fake:a", "fake:b" }, items.Select(i => i.Id));
        Assert.Equal(42, items[0].LastPlayed);
        Assert.True(items[1].Favorite);

        Assert.False(library.ToggleFavorite("fake:b"));
        Assert.True(library.ToggleFavorite("fake:a"));
        Assert.Equal(new[] { "fake:a" }, new ConfigStore(dir).Current.Favorites);

        var launched = library.MarkLaunched("fake:b");
        Assert.True(launched.LastPlayed > 42);
    }

    [Fact]
    public void Config_survives_broken_json_and_round_trips()
    {
        var dir = Directory.CreateTempSubdirectory("couchhud-cfg-").FullName;
        File.WriteAllText(Path.Combine(dir, "config.json"), "{ broken");
        Assert.Equal("default", new ConfigStore(dir).Current.Skin);

        // Starting over means the whole folder: the copy of the broken file alone keeps it locked (ConfigFault.Missing).
        File.Delete(Path.Combine(dir, "config.json"));
        Assert.Equal(ConfigFault.Missing, new ConfigStore(dir).Fault);
        foreach (var file in Directory.GetFiles(dir)) File.Delete(file);
        new ConfigStore(dir).Update(c => c.Skin = "user:nova");
        var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "config.json")));
        Assert.Equal("user:nova", json.RootElement.GetProperty("skin").GetString());
        Assert.Equal("user:nova", new ConfigStore(dir).Current.Skin);
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(source, target));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, target), overwrite: true);
    }

    private sealed class FakeProvider : ILibraryProvider
    {
        public string Name => "fake";

        public Task<IReadOnlyList<LibraryItem>> GetItemsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LibraryItem>>(
            [
                Item("fake:a"),
                Item("fake:b"),
                Item("fake:a") // duplicate id, first wins
            ]);

        private static LibraryItem Item(string id) => new()
        {
            Id = id,
            Source = "fake",
            Name = id,
            Launch = new LaunchTarget(LaunchKind.Url, "https://example.com"),
            Art = new Artwork { Cover = ["x.jpg"] }
        };
    }

    private sealed class BrokenProvider : ILibraryProvider
    {
        public string Name => "broken";

        public Task<IReadOnlyList<LibraryItem>> GetItemsAsync(CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("offline");
    }
}
