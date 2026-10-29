using System.Net;
using System.Text;
using System.Text.Json;
using CouchHud.Core.Library;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

public class SteamAssetsTests
{
    private const string Hashed = "https://shared.steamstatic.com/store_item_assets/steam/apps/3905450";
    private const string Legacy = "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps";

    private const string WheelMates = """
        { "response": { "store_items": [ {
            "item_type": 0, "id": 3905450, "success": 1, "visible": true, "name": "WheelMates", "appid": 3905450,
            "assets": {
              "asset_url_format": "steam/apps/3905450/${FILENAME}?t=1760000000",
              "main_capsule": "aa11/capsule_616x353.jpg",
              "header": "bb22/header.jpg",
              "library_capsule": "cc33/library_capsule.jpg",
              "library_capsule_2x": "cc33/library_capsule_2x.jpg",
              "library_hero": "dd44/library_hero.jpg",
              "library_hero_2x": "dd44/library_hero_2x.jpg",
              "library_logo": "ee55/logo.png",
              "library_logo_2x": "ee55/logo_2x.png"
            } } ] } }
        """;

    private static string Dir() => Directory.CreateTempSubdirectory("couchhud-assets-").FullName;

    private sealed class Routes(Func<HttpRequestMessage, string?> answer) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.ToString());
            var json = answer(request);
            return Task.FromResult(json is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private static LibraryItem SteamItem(string appId, params string[] localCover) => new()
    {
        Id = $"steam:{appId}",
        Source = "steam",
        Name = "WheelMates",
        Launch = new LaunchTarget(LaunchKind.Url, $"steam://rungameid/{appId}"),
        Art = new Artwork
        {
            Cover = [.. localCover, $"{Legacy}/{appId}/library_600x900.jpg"],
            Hero = [$"{Legacy}/{appId}/library_hero.jpg"],
            Logo = [$"{Legacy}/{appId}/logo.png"]
        }
    };

    [Fact]
    public void Parse_builds_full_addresses_from_the_url_format_best_first()
    {
        using var doc = JsonDocument.Parse(WheelMates);

        var art = SteamAssets.Parse(doc.RootElement)["3905450"];

        Assert.Equal($"{Hashed}/cc33/library_capsule_2x.jpg?t=1760000000", art.Cover[0]);
        Assert.Equal($"{Hashed}/cc33/library_capsule.jpg?t=1760000000", art.Cover[1]);
        Assert.Equal($"{Hashed}/bb22/header.jpg?t=1760000000", art.Cover[^1]);
        Assert.Equal($"{Hashed}/dd44/library_hero_2x.jpg?t=1760000000", art.Hero[0]);
        Assert.Equal($"{Hashed}/ee55/logo_2x.png?t=1760000000", art.Logo[0]);
        Assert.All(art.Cover.Concat(art.Hero).Concat(art.Logo), u => Assert.True(ArtworkSources.IsAllowed(u)));
    }

    [Fact]
    public void Parse_skips_apps_without_assets_or_with_odd_formats()
    {
        using var doc = JsonDocument.Parse("""
            { "response": { "store_items": [
              { "appid": 1, "success": 2 },
              { "appid": 2, "assets": { "asset_url_format": "steam/apps/2/nofilename", "header": "x.jpg" } },
              { "appid": 3, "assets": { "asset_url_format": "steam/apps/3/${FILENAME}", "header": "../../evil.jpg" } } ] } }
            """);

        Assert.Empty(SteamAssets.Parse(doc.RootElement));
        using var empty = JsonDocument.Parse("{}");
        Assert.Empty(SteamAssets.Parse(empty.RootElement));
    }

    [Theory]
    [InlineData("https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps/1/library_600x900.jpg", true)]
    [InlineData("https://shared.steamstatic.com/store_item_assets/steam/apps/1/logo.png?t=5", true)]
    [InlineData("https://shared.steamstatic.com/store_item_assets/steam/apps/1/abc123/library_capsule.jpg?t=5", false)]
    [InlineData("file:///C:/Steam/appcache/librarycache/1_library_600x900.jpg", false)]
    public void Legacy_addresses_are_the_ones_without_a_hash_folder(string url, bool legacy) =>
        Assert.Equal(legacy, SteamAssets.IsLegacyUrl(url));

    [Fact]
    public async Task Requests_go_out_in_batches_of_50_and_are_asked_only_once()
    {
        var routes = new Routes(_ => """{ "response": { "store_items": [] } }""");
        var assets = new SteamAssets(new HttpClient(routes), Path.Combine(Dir(), "a.json"));
        var ids = Enumerable.Range(1000, 120).Select(i => i.ToString()).ToList();

        await assets.EnsureAsync(ids, default);
        Assert.Equal(3, routes.Requests.Count);
        Assert.All(routes.Requests, r => Assert.StartsWith(SteamAssets.Endpoint, r));
        Assert.Equal(50, Count(routes.Requests[0]));
        Assert.Equal(50, Count(routes.Requests[1]));
        Assert.Equal(20, Count(routes.Requests[2]));

        // Apps without assets are remembered too (as misses): nothing is asked again.
        await assets.EnsureAsync(ids.Take(60), default);
        Assert.Equal(3, routes.Requests.Count);

        static int Count(string url) => Uri.UnescapeDataString(url).Split("\"appid\"").Length - 1;
    }

    [Fact]
    public async Task Answers_are_kept_on_disk_for_30_days_and_misses_for_7()
    {
        var file = Path.Combine(Dir(), "a.json");
        var clock = DateTimeOffset.UtcNow;
        var routes = new Routes(_ => WheelMates);
        var first = new SteamAssets(new HttpClient(routes), file, () => clock);
        await first.EnsureAsync(["3905450", "42"], default);
        Assert.Single(routes.Requests);

        var later = new SteamAssets(new HttpClient(routes), file, () => clock.AddDays(8));
        Assert.True(later.Peek("3905450").Known);   // hit: 30 days
        Assert.False(later.Peek("42").Known);       // miss: 7 days
        Assert.False(new SteamAssets(new HttpClient(routes), file, () => clock.AddDays(31)).Peek("3905450").Known);
    }

    [Fact]
    public async Task A_steam_game_gets_the_store_addresses_after_local_files_and_before_the_old_names()
    {
        var dir = Dir();
        var routes = new Routes(_ => WheelMates);
        var resolver = new ArtworkResolver(new HttpClient(routes), Path.Combine(dir, "art.json"), null);
        var item = SteamItem("3905450", "file:///C:/Steam/appcache/librarycache/3905450_library_600x900.jpg");

        var result = await resolver.ResolveAsync([item]);

        Assert.NotNull(result);
        var cover = result[0].Art.Cover;
        Assert.StartsWith("file:///", cover[0]);
        Assert.Equal($"{Hashed}/cc33/library_capsule_2x.jpg?t=1760000000", cover[1]);
        Assert.Equal($"{Legacy}/3905450/library_600x900.jpg", cover[^1]);
        Assert.Equal($"{Hashed}/dd44/library_hero_2x.jpg?t=1760000000", result[0].Art.Hero[0]);
        Assert.Equal($"{Legacy}/3905450/logo.png", result[0].Art.Logo[^1]);

        // Next start: no network needed, the cached answer is applied.
        var offline = new ArtworkResolver(new HttpClient(new Routes(_ => null)), Path.Combine(dir, "art.json"), null);
        Assert.Equal(cover, offline.ApplyCached([SteamItem("3905450", "file:///C:/Steam/appcache/librarycache/3905450_library_600x900.jpg")])[0].Art.Cover);
    }

    [Fact]
    public async Task Many_steam_games_share_one_request()
    {
        var routes = new Routes(_ => """{ "response": { "store_items": [] } }""");
        var resolver = new ArtworkResolver(new HttpClient(routes), Path.Combine(Dir(), "art.json"), null);

        await resolver.ResolveAsync(Enumerable.Range(100, 30).Select(i => SteamItem(i.ToString())).ToList());

        Assert.Single(routes.Requests, r => r.StartsWith(SteamAssets.Endpoint));
    }

    [Fact]
    public async Task A_game_from_a_folder_found_by_name_gets_hashed_addresses_first_then_the_old_names()
    {
        var routes = new Routes(r => r.RequestUri!.ToString().Contains("storesearch")
            ? """{ "items": [ { "id": 3905450, "name": "WheelMates", "type": "app" } ] }"""
            : WheelMates);
        var resolver = new ArtworkResolver(new HttpClient(routes), Path.Combine(Dir(), "art.json"), "sgdb-key");
        var folderGame = new LibraryItem { Id = "folder:wheelmates", Source = "folder", Name = "WheelMates", Launch = new LaunchTarget(LaunchKind.Exe, "C:\\Games\\WheelMates.exe") };

        var result = await resolver.ResolveAsync([folderGame]);

        Assert.NotNull(result);
        Assert.Equal($"{Hashed}/cc33/library_capsule_2x.jpg?t=1760000000", result[0].Art.Cover[0]);
        Assert.Equal($"{Legacy}/3905450/library_600x900.jpg", result[0].Art.Cover[^1]);
        // Steam answered, so SteamGridDB (key set) was not needed for cover, banner or logo; only the square (Steam has none) is asked there.
        Assert.DoesNotContain(routes.Requests, r => r.Contains("heroes/") || r.Contains("logos/") || r.Contains("dimensions=600x900"));
        // Everything that went to SteamGridDB was about the square (the name search first, then grids or icons).
        Assert.All(routes.Requests.Where(r => r.Contains("steamgriddb")), r => Assert.True(r.Contains("autocomplete") || r.Contains("dimensions=512x512") || r.Contains("/icons/"), r));
    }

    [Fact]
    public async Task SteamGridDb_is_the_last_source_when_steam_knows_nothing()
    {
        var routes = new Routes(r =>
        {
            var url = r.RequestUri!.ToString();
            if (url.Contains("storesearch")) return """{ "items": [] }""";
            if (url.Contains("autocomplete")) return """{ "data": [ { "id": 7 } ] }""";
            if (url.Contains("grids/")) return """{ "data": [ { "url": "https://cdn2.steamgriddb.com/grid/a.png" } ] }""";
            return null;
        });
        var resolver = new ArtworkResolver(new HttpClient(routes), Path.Combine(Dir(), "art.json"), "sgdb-key");
        var game = new LibraryItem { Id = "folder:x", Source = "folder", Name = "Unknown Game", Launch = new LaunchTarget(LaunchKind.Exe, "C:\\Games\\x.exe") };

        var result = await resolver.ResolveAsync([game]);

        Assert.Equal("https://cdn2.steamgriddb.com/grid/a.png", result![0].Art.Cover[0]);
    }

    [Fact]
    public void Failures_of_the_old_steam_addresses_are_forgotten_once_and_everything_else_stays()
    {
        var dir = Dir();
        var legacy = $"{Legacy}/3905450/library_600x900.jpg";
        var hashed = $"{Hashed}/cc33/library_capsule.jpg?t=1";
        var other = "https://cdn2.steamgriddb.com/grid/gone.png";
        File.WriteAllText(Path.Combine(dir, "failed.json"),
            JsonSerializer.Serialize(new Dictionary<string, DateTimeOffset> { [legacy] = DateTimeOffset.UtcNow, [hashed] = DateTimeOffset.UtcNow, [other] = DateTimeOffset.UtcNow }));

        var cache = new ArtworkCache(dir);
        Assert.False(cache.RecentlyFailed(legacy));
        Assert.True(cache.RecentlyFailed(hashed));
        Assert.True(cache.RecentlyFailed(other));

        // The old address fails again (it really is gone for this game): that verdict is now kept, not migrated away again.
        cache.MarkFailed(legacy);
        cache.Flush();
        Assert.True(new ArtworkCache(dir).RecentlyFailed(legacy));
    }

    [Fact]
    public async Task Lookups_that_only_hold_old_steam_addresses_are_dropped_once()
    {
        var dir = Dir();
        var file = Path.Combine(dir, "art.json");
        var old = new Dictionary<string, object>
        {
            ["game:wheelmates"] = new { Art = new { Cover = new[] { $"{Legacy}/3905450/library_600x900.jpg" }, Hero = new[] { $"{Legacy}/3905450/library_hero.jpg" }, Logo = new[] { $"{Legacy}/3905450/logo.png" } }, Checked = DateTimeOffset.UtcNow },
            ["game:kodi"] = new { Art = new { Cover = new[] { "https://cdn2.steamgriddb.com/grid/k.png" }, Hero = Array.Empty<string>(), Logo = Array.Empty<string>() }, Checked = DateTimeOffset.UtcNow }
        };
        File.WriteAllText(file, JsonSerializer.Serialize(old));
        var routes = new Routes(r => r.RequestUri!.ToString().Contains("storesearch")
            ? """{ "items": [ { "id": 3905450, "name": "WheelMates", "type": "app" } ] }"""
            : WheelMates);
        var resolver = new ArtworkResolver(new HttpClient(routes), file, null);
        LibraryItem Game(string name) => new() { Id = $"folder:{name}", Source = "folder", Name = name, Launch = new LaunchTarget(LaunchKind.Exe, $"C:\\Games\\{name}.exe") };

        var result = await resolver.ResolveAsync([Game("WheelMates"), Game("Kodi")]);

        Assert.StartsWith(Hashed, result![0].Art.Cover[0]);     // looked up again
        Assert.Equal("https://cdn2.steamgriddb.com/grid/k.png", result[1].Art.Cover[0]);    // kept
    }
}
