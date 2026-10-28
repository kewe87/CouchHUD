using System.Net;
using System.Text.Json;
using CouchHud.Core.Config;
using CouchHud.Core.Library;
using CouchHud.Core.Platform;
using CouchHud.Core.Plugins;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>Regression tests for the review findings: nothing here may lose data or hang the shell.</summary>
public class RobustnessTests
{
    private static string TempDir(string prefix) => Directory.CreateTempSubdirectory(prefix).FullName;

    [Fact]
    public void Broken_config_is_backed_up_before_it_gets_replaced()
    {
        var dir = TempDir("couchhud-cfg-");
        var file = Path.Combine(dir, "config.json");
        File.WriteAllText(file, """{ "skin": "nova", "apps": [ { "name": "Kodi", } """);

        var store = new ConfigStore(dir);
        store.Update(c => c.Favorites.Add("steam:1"));

        var backup = Assert.Single(Directory.GetFiles(dir, "config.json.broken-*"));
        Assert.Contains("Kodi", File.ReadAllText(backup));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }

    [Fact]
    public void External_edits_survive_the_next_save()
    {
        var dir = TempDir("couchhud-cfg-");
        var store = new ConfigStore(dir);

        // User edits the file while CouchHud runs.
        var edited = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(store.FilePath), ConfigStore.JsonOptions)!;
        edited.SteamGridDbApiKey = "secret";
        File.WriteAllText(store.FilePath, JsonSerializer.Serialize(edited, ConfigStore.JsonOptions));
        File.SetLastWriteTimeUtc(store.FilePath, DateTime.UtcNow.AddSeconds(5));

        store.Update(c => c.Favorites.Add("steam:1"));

        var saved = new ConfigStore(dir).Current;
        Assert.Equal("secret", saved.SteamGridDbApiKey);
        Assert.Contains("steam:1", saved.Favorites);
    }

    [Fact]
    public async Task Hanging_or_cancelled_providers_are_skipped()
    {
        var dir = TempDir("couchhud-lib-");
        var config = new ConfigStore(dir);
        config.Update(c => c.Apps = []);
        var artwork = new ArtworkResolver(new HttpClient(new Offline()), Path.Combine(dir, "art.json"), null);
        var library = new LibraryService(config, artwork, [new Hanging(), new TimesOut(), new Works()])
        {
            ProviderTimeout = TimeSpan.FromMilliseconds(300)
        };

        var items = await library.ScanAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("ok:1", Assert.Single(items).Id);
    }

    [Fact]
    public void Custom_entries_never_collide_or_vanish()
    {
        var items = CustomEntries.From(
        [
            new CustomEntry { Name = "Kodi", Url = "https://a" },
            new CustomEntry { Name = "kodi", Url = "https://b" },
            new CustomEntry { Name = "ファイナルファンタジー", Url = "https://c" },
            new CustomEntry { Name = "ドラゴンクエスト", Url = "https://d" }
        ]);

        Assert.Equal(4, items.Select(i => i.Id).Distinct().Count());
        Assert.Equal("custom:kodi", items[0].Id);
        Assert.Equal("custom:kodi-2", items[1].Id);
    }

    [Fact]
    public void Epic_ignores_odd_category_values()
    {
        using var doc = JsonDocument.Parse("""
            { "DisplayName": "X", "AppName": "X", "AppCategories": [ 7, null, "games" ] }
            """);
        Assert.NotNull(EpicScanner.ParseItem(doc.RootElement));
    }

    [Fact]
    public async Task Artwork_server_errors_are_not_cached_as_misses()
    {
        var cache = Path.Combine(TempDir("couchhud-art-"), "cache.json");
        var handler = new Routes(url =>
            url.Contains("autocomplete") ? (HttpStatusCode.OK, """{ "data": [ { "id": 1 } ] }""")
            : (HttpStatusCode.TooManyRequests, "{}"));
        var item = new LibraryItem
        {
            Id = "custom:kodi",
            Source = "custom",
            Name = "Kodi",
            Type = ItemType.App,
            Launch = new LaunchTarget(LaunchKind.Url, "x://")
        };

        await new ArtworkResolver(new HttpClient(handler), cache, "key").ResolveAsync([item]);

        Assert.Equal("{}", File.ReadAllText(cache));
    }

    [Theory]
    [InlineData("simple", "simple")]
    [InlineData("with space", "\"with space\"")]
    [InlineData("C:\\a b\\", "\"C:\\a b\\\\\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("", "\"\"")]
    public void CommandLine_quote_round_trips(string arg, string expected) =>
        Assert.Equal(expected, CommandLine.Quote(arg));

    [Fact]
    public void Plugin_entry_assembly_is_the_folder_name_or_the_one_with_deps()
    {
        var root = TempDir("couchhud-plugins-");
        var named = Directory.CreateDirectory(Path.Combine(root, "Kodi")).FullName;
        File.WriteAllText(Path.Combine(named, "Kodi.dll"), "");
        File.WriteAllText(Path.Combine(named, "Newtonsoft.Json.dll"), "");

        var published = Directory.CreateDirectory(Path.Combine(root, "jellyfin")).FullName;
        File.WriteAllText(Path.Combine(published, "CouchHud.Jellyfin.dll"), "");
        File.WriteAllText(Path.Combine(published, "CouchHud.Jellyfin.deps.json"), "{}");
        File.WriteAllText(Path.Combine(published, "Helper.dll"), "");

        var unclear = Directory.CreateDirectory(Path.Combine(root, "mystery")).FullName;
        File.WriteAllText(Path.Combine(unclear, "A.dll"), "");
        File.WriteAllText(Path.Combine(unclear, "B.dll"), "");

        Assert.Equal(Path.Combine(named, "Kodi.dll"), PluginManager.FindEntryAssembly(named));
        Assert.Equal(Path.Combine(published, "CouchHud.Jellyfin.dll"), PluginManager.FindEntryAssembly(published));
        Assert.Null(PluginManager.FindEntryAssembly(unclear));
    }

    private static LibraryItem Item(string id) => new()
    {
        Id = id,
        Source = "test",
        Name = id,
        Launch = new LaunchTarget(LaunchKind.Url, "x://"),
        Art = new Artwork { Cover = ["x.jpg"] }
    };

    private sealed class Hanging : ILibraryProvider
    {
        public string Name => "hanging";

        // Blocks the calling thread before even returning a task (longer than the timeout).
        public Task<IReadOnlyList<LibraryItem>> GetItemsAsync(CancellationToken ct)
        {
            Thread.Sleep(5000);
            return Task.FromResult<IReadOnlyList<LibraryItem>>([]);
        }
    }

    private sealed class TimesOut : ILibraryProvider
    {
        public string Name => "timeout";

        public Task<IReadOnlyList<LibraryItem>> GetItemsAsync(CancellationToken ct) =>
            throw new TaskCanceledException("HttpClient timeout");
    }

    private sealed class Works : ILibraryProvider
    {
        public string Name => "works";

        public Task<IReadOnlyList<LibraryItem>> GetItemsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LibraryItem>>([Item("ok:1")]);
    }

    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("offline");
    }

    private sealed class Routes(Func<string, (HttpStatusCode Status, string Json)> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var (status, json) = respond(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json) });
        }
    }
}
