using System.Net;
using System.Text;
using CouchHud.Core.Config;
using CouchHud.Core.Library;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>
/// The artwork slot "square" (SteamGridDB square grids, then icons): lookup, cache, re-check, pick and reset, the picker's choices,
/// and the security edges (foreign hosts, redirects, oversized and non-image files, the lock, the key). Fake HTTP only.
/// </summary>
public class ArtworkSquareTests
{
    private const string Key = "sgdb-secret-key-1234567890";
    private const string Grid = "https://cdn2.steamgriddb.com/grid/square-a.png";
    private const string Icon = "https://cdn2.steamgriddb.com/icon/icon-a.png";
    private static readonly DateTimeOffset Start = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static string Dir() => Directory.CreateTempSubdirectory("couchhud-square-").FullName;

    private static LibraryItem Game(string name = "Control Resonant", string id = "folder:control", Artwork? art = null) => new()
    {
        Id = id, Source = "folder", Name = name, Type = ItemType.Game,
        Launch = new LaunchTarget(LaunchKind.Exe, "C:\\Games\\control.exe"),
        Art = art ?? Artwork.Empty
    };

    /// <summary>SteamGridDB and Steam's store search, answered from lambdas; images answer with a small png from an allowed host only.</summary>
    private sealed class Host : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public string? SquareGrids = $$"""{ "data": [ { "url": "{{Grid}}", "thumb": "{{Grid}}" } ] }""";
        public string? Icons = $$"""{ "data": [ { "url": "{{Icon}}", "thumb": "{{Icon}}" } ] }""";
        public string CoverGrids = """{ "data": [ { "url": "https://cdn2.steamgriddb.com/grid/cover.png" } ] }""";
        public Dictionary<string, string> Redirects { get; } = new();
        public Func<string, HttpResponseMessage?>? Image;
        public string SearchJson = """{ "data": [ { "id": 7 } ] }""";
        public HttpStatusCode? SearchStatus;
        public Action<string>? Seen;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var url = request.RequestUri!.ToString();
            Seen?.Invoke(url);
            if (Redirects.TryGetValue(url, out var to))
            {
                var r = new HttpResponseMessage(HttpStatusCode.Found);
                r.Headers.Location = new Uri(to);
                return Task.FromResult(r);
            }
            if (url.Contains("storesearch")) return Task.FromResult(Json("""{ "items": [] }"""));
            if (url.Contains("autocomplete")) return Task.FromResult(SearchStatus is { } status ? new HttpResponseMessage(status) : Json(SearchJson));
            if (url.Contains("/grids/") && url.Contains("512x512")) return Task.FromResult(SquareGrids is null ? NotFound() : Json(SquareGrids));
            if (url.Contains("/grids/")) return Task.FromResult(Json(CoverGrids));
            if (url.Contains("/icons/")) return Task.FromResult(Icons is null ? NotFound() : Json(Icons));
            if (url.Contains("/heroes/") || url.Contains("/logos/")) return Task.FromResult(NotFound());
            if (Image?.Invoke(url) is { } custom) return Task.FromResult(custom);
            if (url.Contains("steamgriddb")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ArtworkCacheTests.Png()) });
            return Task.FromResult(NotFound());
        }

        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound) { Content = new StringContent("{}") };

        public IEnumerable<string> Urls => Requests.Select(r => r.RequestUri!.ToString());
    }

    private static ArtworkResolver Resolver(Host host, string dir, string? key = Key, ArtworkCache? images = null) =>
        new(new HttpClient(host), Path.Combine(dir, "cache.json"), key, images);

    // ----- lookup: square grids, then icons -----

    [Fact]
    public async Task The_square_grid_is_asked_with_both_dimensions_and_wins_over_the_icon()
    {
        var host = new Host();
        var result = await Resolver(host, Dir()).ResolveAsync([Game()]);

        Assert.Equal([Grid], result![0].Art.Square);
        Assert.Contains(host.Urls, u => u.Contains("/grids/game/7?dimensions=512x512,1024x1024"));
        // The cover is still asked its own way, and the icon is only the second choice.
        Assert.DoesNotContain(host.Urls, u => u.Contains("/icons/"));
    }

    [Fact]
    public async Task Without_a_square_grid_the_icon_is_used()
    {
        var host = new Host { SquareGrids = """{ "data": [] }""" };
        var result = await Resolver(host, Dir()).ResolveAsync([Game()]);

        Assert.Equal([Icon], result![0].Art.Square);
    }

    [Fact]
    public async Task Without_grid_and_icon_the_square_stays_empty_and_the_rest_is_found()
    {
        var host = new Host { SquareGrids = null, Icons = null };
        var result = await Resolver(host, Dir()).ResolveAsync([Game()]);

        Assert.Empty(result![0].Art.Square);
        Assert.Single(result[0].Art.Cover);
    }

    [Fact]
    public async Task A_square_on_a_foreign_host_is_dropped()
    {
        var host = new Host
        {
            SquareGrids = """{ "data": [ { "url": "https://evil.example.org/s.png" } ] }""",
            Icons = """{ "data": [ { "url": "http://cdn2.steamgriddb.com/icon/plain-http.png" } ] }"""
        };
        var result = await Resolver(host, Dir()).ResolveAsync([Game()]);

        Assert.Empty(result![0].Art.Square);
    }

    [Fact]
    public async Task A_steam_game_asks_the_steam_route_for_its_square()
    {
        var host = new Host();
        var steam = Game("Hades", "steam:1145360") with { Source = "steam", Launch = new LaunchTarget(LaunchKind.Url, "steam://rungameid/1145360") };
        var result = await Resolver(host, Dir()).ResolveAsync([steam]);

        Assert.Equal([Grid], result![0].Art.Square);
        Assert.Contains(host.Urls, u => u.Contains("/grids/steam/1145360?dimensions=512x512,1024x1024"));
    }

    [Fact]
    public async Task Without_a_key_or_with_the_artwork_switch_off_no_square_is_asked()
    {
        var noKey = new Host();
        await Resolver(noKey, Dir(), key: null).ResolveAsync([Game()]);
        Assert.DoesNotContain(noKey.Urls, u => u.Contains("steamgriddb"));

        var off = new Host();
        var resolver = Resolver(off, Dir());
        resolver.Online = false;
        Assert.Null(await resolver.ResolveAsync([Game()]));
        Assert.Empty(off.Requests);
    }

    // ----- cache -----

    [Fact]
    public async Task A_square_is_cached_and_applies_without_the_network()
    {
        var dir = Dir();
        await Resolver(new Host(), dir).ResolveAsync([Game()]);

        var offline = Resolver(new Host(), dir);
        var item = offline.ApplyCached([Game()])[0];

        Assert.Equal([Grid], item.Art.Square);
    }

    [Fact]
    public async Task An_old_cache_without_square_loads_and_the_square_is_asked_for_once()
    {
        var dir = Dir();
        // The shape of 0.6.1: no Logo, no Square in the entry.
        File.WriteAllText(Path.Combine(dir, "cache.json"),
            """{ "game:control resonant": { "Art": { "Cover": ["https://cdn2.steamgriddb.com/grid/old.png"], "Hero": [] }, "Checked": "2026-10-20T10:00:00+00:00" } }""");
        var host = new Host();
        var resolver = Resolver(host, dir);

        var cached = resolver.ApplyCached([Game()])[0];
        Assert.Equal(["https://cdn2.steamgriddb.com/grid/old.png"], cached.Art.Cover);
        Assert.Empty(cached.Art.Square);

        var result = await resolver.ResolveAsync([Game()]);
        Assert.Equal([Grid], result![0].Art.Square);
        Assert.Equal(["https://cdn2.steamgriddb.com/grid/old.png"], result[0].Art.Cover);

        // Written back: the next start applies it offline and does not ask again.
        var again = Resolver(host, dir);
        Assert.Equal([Grid], again.ApplyCached([Game()])[0].Art.Square);
        var before = host.Requests.Count;
        await again.ResolveAsync([Game()]);
        Assert.Equal(before, host.Requests.Count);
    }

    [Fact]
    public async Task A_broken_cache_file_starts_empty()
    {
        var dir = Dir();
        File.WriteAllText(Path.Combine(dir, "cache.json"), """{ "game:x": { "Art": { "Cover": 5, "Square": "no" } } """);
        var result = await Resolver(new Host(), dir).ResolveAsync([Game()]);

        Assert.Equal([Grid], result![0].Art.Square);
    }

    [Fact]
    public async Task A_square_the_scanner_or_user_set_is_not_replaced()
    {
        var mine = Game(art: new Artwork { Square = ["file:///C:/my/square.png"] });
        var result = await Resolver(new Host(), Dir()).ResolveAsync([mine]);

        Assert.Equal(["file:///C:/my/square.png"], result![0].Art.Square);
    }

    // ----- re-check -----

    [Fact]
    public async Task The_daily_recheck_looks_for_a_missing_square_too()
    {
        var dir = Dir();
        var now = Start;
        var cache = new ArtworkCache(Path.Combine(dir, "img"));
        var host = new Host();
        // Everything else is served by local files, only the square is missing.
        var local = new Artwork { Cover = ["file:///C:/c.png"], Hero = ["file:///C:/h.png"], Logo = ["file:///C:/l.png"] };
        var resolver = Resolver(host, dir, images: cache);
        resolver.Clock = () => now;

        var first = await resolver.ResolveAsync([Game(art: local)]);
        Assert.Empty(first?[0].Art.Square ?? []);
        now = Start.AddDays(3);
        var second = await resolver.ResolveAsync([Game(art: local)]);

        Assert.NotNull(second);
        Assert.Equal([Grid], second[0].Art.Square);
    }

    [Fact]
    public async Task The_recheck_leaves_an_entry_with_a_square_on_disk_alone()
    {
        var dir = Dir();
        var now = Start;
        var cache = new ArtworkCache(Path.Combine(dir, "img"));
        cache.Store(Grid, ArtworkCacheTests.Png());
        var host = new Host();
        var done = Game(art: new Artwork { Cover = ["file:///C:/c.png"], Hero = ["file:///C:/h.png"], Logo = ["file:///C:/l.png"], Square = [Grid] });
        var resolver = Resolver(host, dir, images: cache);
        resolver.Clock = () => now;

        await resolver.ResolveAsync([done]);
        now = Start.AddDays(3);
        await resolver.ResolveAsync([done]);

        Assert.Empty(host.Requests);
    }

    [Fact]
    public async Task A_square_that_is_listed_but_not_downloaded_is_a_missing_slot_and_downloads_with_the_others()
    {
        var dir = Dir();
        var cache = new ArtworkCache(Path.Combine(dir, "img"));
        var host = new Host();
        var resolver = Resolver(host, dir, images: cache);

        var arrived = await resolver.DownloadMissingAsync([[Grid]]);

        Assert.Equal(1, arrived);
        Assert.True(cache.Has(Grid));
        Assert.DoesNotContain(host.Requests.Where(r => r.RequestUri!.ToString() == Grid), r => r.Headers.Authorization is not null);
    }

    // ----- overrides: pick and reset -----

    [Fact]
    public void The_square_override_goes_first_and_reset_removes_it()
    {
        var o = new ItemOverride();
        ArtworkOverrides.Set(o, "square", "https://cdn2.steamgriddb.com/grid/mine.png");

        Assert.False(o.IsEmpty);
        var art = ArtworkOverrides.Apply(new Artwork { Square = [Grid] }, o);
        Assert.Equal(["https://cdn2.steamgriddb.com/grid/mine.png", Grid], art.Square);

        ArtworkOverrides.Set(o, "square", null);
        Assert.True(o.IsEmpty);
        Assert.Equal([Grid], ArtworkOverrides.Apply(new Artwork { Square = [Grid] }, o).Square);
    }

    [Theory]
    [InlineData("Square")]
    [InlineData("squares")]
    [InlineData("../../square")]
    [InlineData("")]
    public void Other_slot_names_are_refused(string slot) =>
        Assert.Throws<ArgumentException>(() => ArtworkOverrides.Set(new ItemOverride(), slot, "https://cdn2.steamgriddb.com/x.png"));

    [Theory]
    [InlineData("\\\\evil\\share\\square.png")]
    [InlineData("//evil/share/square.png")]
    [InlineData("\\\\?\\C:\\square.png")]
    [InlineData("ftp://cdn2.steamgriddb.com/square.png")]
    [InlineData("square.png")]
    public void A_square_pick_from_a_share_a_device_or_a_relative_path_is_refused(string value) =>
        Assert.ThrowsAny<Exception>(() => ArtworkOverrides.Normalize(value));

    // ----- SetArtworkAsync: download before store -----

    private sealed class Provider(LibraryItem item) : ILibraryProvider
    {
        public string Name => "test";
        public Task<IReadOnlyList<LibraryItem>> GetItemsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<LibraryItem>>([item]);
    }

    private static async Task<(LibraryService Service, ArtworkCache Cache, ConfigStore Config)> Setup(Host host)
    {
        var dir = Dir();
        var config = new ConfigStore(dir);
        var cache = new ArtworkCache(Path.Combine(dir, "art"));
        var resolver = new ArtworkResolver(new HttpClient(host), Path.Combine(dir, "cache.json"), null, cache);
        var service = new LibraryService(config, resolver, [new Provider(Game(art: new Artwork { Cover = ["file:///C:/c.png"] }))]);
        await service.ScanAsync();
        return (service, cache, config);
    }

    [Fact]
    public async Task A_square_pick_is_downloaded_first_stored_and_survives_a_rescan()
    {
        var (service, cache, config) = await Setup(new Host());

        var item = await service.SetArtworkAsync("folder:control", "square", Grid);

        Assert.Equal(Grid, item.Art.Square[0]);
        Assert.True(cache.Has(Grid));
        Assert.Equal(ArtworkCache.UrlFor(Grid), ArtworkSources.CacheUrl(item.Art.Square[0]));
        Assert.Equal(Grid, config.Current.Overrides["folder:control"].Square);
        Assert.Equal(Grid, (await service.ScanAsync())[0].Art.Square[0]);
        // The other slots are untouched.
        Assert.Equal(["file:///C:/c.png"], item.Art.Cover);
    }

    [Fact]
    public async Task A_square_reset_goes_back_to_automatic()
    {
        var (service, _, config) = await Setup(new Host());
        await service.SetArtworkAsync("folder:control", "square", Grid);

        var reset = await service.SetArtworkAsync("folder:control", "square", null);

        Assert.DoesNotContain(Grid, reset.Art.Square);
        Assert.Empty(config.Current.Overrides);
    }

    [Fact]
    public async Task A_failed_square_download_changes_nothing()
    {
        var host = new Host { Image = u => u == Grid ? new HttpResponseMessage(HttpStatusCode.NotFound) : null };
        var (service, cache, config) = await Setup(host);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetArtworkAsync("folder:control", "square", Grid));

        Assert.False(cache.Has(Grid));
        Assert.Empty(config.Current.Overrides);
        Assert.Empty(service.Find("folder:control")!.Art.Square);
    }

    [Fact]
    public async Task A_square_pick_redirected_to_a_foreign_host_fails_and_the_foreign_host_is_never_asked()
    {
        var host = new Host();
        host.Redirects[Grid] = "https://evil.example.org/s.png";
        var (service, cache, config) = await Setup(host);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetArtworkAsync("folder:control", "square", Grid));

        Assert.False(cache.Has(Grid));
        Assert.Empty(config.Current.Overrides);
        Assert.DoesNotContain(host.Urls, u => u.Contains("evil.example.org"));
    }

    [Fact]
    public async Task A_square_pick_redirected_to_a_private_address_is_refused()
    {
        var host = new Host();
        host.Redirects[Grid] = "https://127.0.0.1/s.png";
        var (service, _, config) = await Setup(host);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetArtworkAsync("folder:control", "square", Grid));

        Assert.Empty(config.Current.Overrides);
        Assert.DoesNotContain(host.Urls, u => u.Contains("127.0.0.1"));
    }

    [Fact]
    public async Task An_oversized_square_is_refused_by_header_and_by_content()
    {
        var big = new byte[(int)ArtworkCache.MaxImageBytes + 1024];
        var host = new Host { Image = u => u == Grid ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(big) } : null };
        var (service, cache, config) = await Setup(host);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetArtworkAsync("folder:control", "square", Grid));

        Assert.False(cache.Has(Grid));
        Assert.Empty(config.Current.Overrides);
    }

    [Fact]
    public async Task A_square_that_is_a_web_page_and_not_an_image_is_refused()
    {
        var host = new Host { Image = u => u == Grid ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html><script>alert(1)</script></html>", Encoding.UTF8, "image/png") } : null };
        var (service, cache, config) = await Setup(host);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetArtworkAsync("folder:control", "square", Grid));

        Assert.False(cache.Has(Grid));
        Assert.Empty(config.Current.Overrides);
    }

    [Fact]
    public async Task A_locked_couch_cannot_pick_a_square()
    {
        var (service, _, config) = await Setup(new Host());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.SetArtworkAsync("folder:control", "square", Grid, () => throw new UnauthorizedAccessException()));

        Assert.Empty(config.Current.Overrides);
        Assert.Empty(service.Find("folder:control")!.Art.Square);
    }

    [Fact]
    public async Task The_square_pick_of_an_unknown_entry_is_refused()
    {
        var (service, _, _) = await Setup(new Host());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.SetArtworkAsync("nope", "square", Grid));
    }

    // ----- the picker's choices -----

    [Fact]
    public async Task The_picker_lists_square_grids_first_then_icons_and_marks_the_current_one()
    {
        var host = new Host();
        var item = Game(art: new Artwork { Square = [Icon] });
        var choices = await Resolver(host, Dir()).GetChoicesAsync(item);

        var urls = choices.Square.Select(c => c.Url).ToList();
        Assert.Equal(Icon, urls[0]); // what the entry shows now comes first
        Assert.Contains(Grid, urls);
        Assert.True(choices.Square.Single(c => c.Url == Icon).Current);
        Assert.False(choices.Square.Single(c => c.Url == Grid).Current);
        Assert.True(urls.IndexOf(Grid) < urls.LastIndexOf(Icon) || urls.Count(u => u == Icon) == 1);
        Assert.All(choices.Square, c => Assert.DoesNotContain(Key, c.Url + c.Thumb + c.Source));
    }

    [Fact]
    public async Task The_picker_offline_asks_nothing_and_still_shows_the_current_square()
    {
        var host = new Host();
        var resolver = Resolver(host, Dir());
        resolver.Online = false;

        var choices = await resolver.GetChoicesAsync(Game(art: new Artwork { Square = [Grid] }));

        Assert.Empty(host.Requests);
        Assert.Equal([Grid], choices.Square.Select(c => c.Url));
        Assert.False(string.IsNullOrEmpty(choices.Note));
    }

    // ----- the key -----

    [Fact]
    public async Task The_key_goes_to_the_steamgriddb_api_only_and_never_to_disk()
    {
        var dir = Dir();
        var cache = new ArtworkCache(Path.Combine(dir, "img"));
        var host = new Host();
        var resolver = Resolver(host, dir, images: cache);
        var result = await resolver.ResolveAsync([Game()]);
        await resolver.DownloadMissingAsync([result![0].Art.Square]);
        await resolver.GetChoicesAsync(result[0]);

        foreach (var request in host.Requests)
        {
            var url = request.RequestUri!.ToString();
            var hasAuth = request.Headers.Authorization is not null;
            Assert.Equal(url.StartsWith("https://www.steamgriddb.com/api/v2/", StringComparison.Ordinal), hasAuth);
            Assert.DoesNotContain(Key, url);
        }
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain(Key, File.ReadAllText(file, Encoding.Latin1));
    }

    // ----- burst control, answers that stay answers, icons -----

    private static string OldCacheFor(params string[] names) =>
        "{" + string.Join(",", names.Select(n => $$"""
            "game:{{n.ToLowerInvariant()}}": { "Art": { "Cover": ["https://cdn2.steamgriddb.com/grid/old.png"], "Hero": [], "Logo": [] }, "Checked": "2026-10-20T10:00:00+00:00" }
            """)) + "}";

    [Fact]
    public async Task One_pass_looks_up_at_most_the_limit_of_squares_saves_on_the_way_and_the_next_pass_goes_on()
    {
        var dir = Dir();
        var host = new Host();
        var items = Enumerable.Range(0, 70).Select(i => Game($"Game {i}", $"folder:g{i}")).ToList();
        var squareAsks = 0;
        string? savedMidway = null;
        host.Seen = url =>
        {
            if (!url.Contains("512x512")) return;
            // Right before the 45th lookup the first 40 are on disk already (saved every 20), not only at the end of the pass.
            if (++squareAsks == 45) savedMidway = File.ReadAllText(Path.Combine(dir, "cache.json"));
        };
        var resolver = Resolver(host, dir);

        await resolver.ResolveAsync(items);
        Assert.Equal(60, squareAsks);
        Assert.NotNull(savedMidway);
        Assert.Contains("game:game 39", savedMidway);

        var second = await resolver.ResolveAsync(items);
        Assert.Equal(70, squareAsks);
        Assert.All(second!, i => Assert.Equal([Grid], i.Art.Square));
    }

    [Fact]
    public async Task A_game_steamgriddb_does_not_know_is_asked_once_and_an_error_stays_unanswered()
    {
        var dir = Dir();
        File.WriteAllText(Path.Combine(dir, "cache.json"), OldCacheFor("Control Resonant"));
        var host = new Host { SearchJson = """{ "data": [] }""" };

        await Resolver(host, dir).ResolveAsync([Game()]);
        Assert.Contains("\"Square\":[]", File.ReadAllText(Path.Combine(dir, "cache.json")));
        var asked = host.Requests.Count;
        Assert.True(asked > 0);

        await Resolver(host, dir).ResolveAsync([Game()]);
        Assert.Equal(asked, host.Requests.Count);

        // Rate limit: nothing is concluded, the next pass asks again.
        var dir2 = Dir();
        File.WriteAllText(Path.Combine(dir2, "cache.json"), OldCacheFor("Control Resonant"));
        var limited = new Host { SearchStatus = HttpStatusCode.TooManyRequests };
        await Resolver(limited, dir2).ResolveAsync([Game()]);
        Assert.DoesNotContain("\"Square\":[]", File.ReadAllText(Path.Combine(dir2, "cache.json")));
        var before = limited.Requests.Count;
        await Resolver(limited, dir2).ResolveAsync([Game()]);
        Assert.True(limited.Requests.Count > before);
    }

    [Fact]
    public async Task An_empty_square_that_was_asked_is_no_missing_slot_for_the_recheck_but_a_reload_tries_again()
    {
        var dir = Dir();
        var now = Start;
        var cache = new ArtworkCache(Path.Combine(dir, "img"));
        File.WriteAllText(Path.Combine(dir, "cache.json"),
            """{ "game:control resonant": { "Art": { "Cover": ["https://cdn2.steamgriddb.com/grid/old.png"], "Hero": [], "Logo": [], "Square": [] }, "Checked": "2026-10-20T10:00:00+00:00" } }""");
        var host = new Host();
        var local = new Artwork { Cover = ["file:///C:/c.png"], Hero = ["file:///C:/h.png"], Logo = ["file:///C:/l.png"] };
        var resolver = Resolver(host, dir, images: cache);
        resolver.Clock = () => now;

        await resolver.ResolveAsync([Game(art: local)]);
        now = Start.AddDays(40);
        await resolver.ResolveAsync([Game(art: local)]);
        Assert.Empty(host.Requests);

        var (item, _) = await resolver.ReloadAsync(Game(art: local));
        Assert.Contains(host.Urls, u => u.Contains("512x512"));
        Assert.Equal([Grid], item.Art.Square);
    }

    [Fact]
    public async Task Ico_icons_are_skipped_by_address_and_by_mime_in_the_fallback_and_in_the_picker()
    {
        var ico = "https://cdn2.steamgriddb.com/icon/a.ico";
        var host = new Host
        {
            SquareGrids = """{ "data": [] }""",
            Icons = $$"""{ "data": [ { "url": "{{ico}}", "thumb": "{{ico}}" }, { "url": "https://cdn2.steamgriddb.com/icon/b.png", "mime": "image/vnd.microsoft.icon" }, { "url": "{{Icon}}", "mime": "image/png" } ] }"""
        };

        var result = await Resolver(host, Dir()).ResolveAsync([Game()]);
        Assert.Equal([Icon], result![0].Art.Square);

        var choices = await Resolver(host, Dir()).GetChoicesAsync(Game());
        Assert.Equal([Icon], choices.Square.Select(c => c.Url));

        var onlyIco = new Host { SquareGrids = """{ "data": [] }""", Icons = $$"""{ "data": [ { "url": "{{ico}}" } ] }""" };
        Assert.Empty((await Resolver(onlyIco, Dir()).ResolveAsync([Game()]))![0].Art.Square);
    }
}
