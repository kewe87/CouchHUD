using System.Net;
using CouchHud.Core.Config;
using CouchHud.Core.Library;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>Picking an online image: it is on disk before the pick is stored, and a failed download changes nothing.</summary>
public class ArtworkPickDownloadTests
{
    private const string Logo = "https://cdn2.steamgriddb.com/logo/b62701c46b9344eb70afe729bac54fa6.png";
    private const string Hero = "https://cdn2.steamgriddb.com/hero/aaaa.png";

    private static LibraryItem Game() => new()
    {
        Id = "steam:1", Source = "test", Name = "Control Resonant", Type = ItemType.Game,
        Launch = new LaunchTarget(LaunchKind.Url, "https://example.com"),
        Art = new Artwork { Logo = ["https://cdn.steamstatic.com/old.png"] }
    };

    private sealed class Provider(LibraryItem item) : ILibraryProvider
    {
        public string Name => "test";
        public Task<IReadOnlyList<LibraryItem>> GetItemsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<LibraryItem>>([item]);
    }

    private sealed class ImageHost(HashSet<string> missing) : HttpMessageHandler
    {
        public Dictionary<string, TaskCompletionSource> Gates { get; } = new();
        public Dictionary<string, string> Redirects { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (Gates.TryGetValue(url, out var gate)) await gate.Task;
            if (Redirects.TryGetValue(url, out var to))
            {
                var r = new HttpResponseMessage(HttpStatusCode.Found);
                r.Headers.Location = new Uri(to);
                return r;
            }
            return Respond(url);
        }

        private HttpResponseMessage Respond(string url)
        {
            if (missing.Contains(url) || !url.Contains("steamgriddb")) return new HttpResponseMessage(HttpStatusCode.NotFound);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ArtworkCacheTests.Png()) };
        }
    }

    private static async Task<(LibraryService Service, ArtworkCache Cache, ConfigStore Config, ArtworkResolver Resolver)> Setup(params string[] missing)
        => await SetupWith(new ImageHost([.. missing]));

    private static async Task<(LibraryService Service, ArtworkCache Cache, ConfigStore Config, ArtworkResolver Resolver)> SetupWith(ImageHost host)
    {
        var dir = Directory.CreateTempSubdirectory("couchhud-pickdl-").FullName;
        var config = new ConfigStore(dir);
        var cache = new ArtworkCache(Path.Combine(dir, "art"));
        var resolver = new ArtworkResolver(new HttpClient(host), Path.Combine(dir, "cache.json"), null, cache);
        var service = new LibraryService(config, resolver, [new Provider(Game())]);
        await service.ScanAsync();
        return (service, cache, config, resolver);
    }

    [Theory]
    [InlineData("logo", Logo)]
    [InlineData("hero", Hero)]
    public async Task A_pick_is_downloaded_first_stored_and_survives_a_rescan(string slot, string url)
    {
        var (service, cache, config, _) = await Setup();

        var item = await service.SetArtworkAsync("steam:1", slot, url);

        var shown = slot == "logo" ? item.Art.Logo : item.Art.Hero;
        Assert.Equal(url, shown[0]);
        Assert.True(cache.Has(url));
        Assert.True(File.Exists(cache.PathOf(url)));
        Assert.Equal(ArtworkCache.UrlFor(url), ArtworkSources.CacheUrl(shown[0]));
        var rescanned = await service.ScanAsync();
        Assert.Equal(url, (slot == "logo" ? rescanned[0].Art.Logo : rescanned[0].Art.Hero)[0]);
        Assert.Equal(url, slot == "logo" ? config.Current.Overrides["steam:1"].Logo : config.Current.Overrides["steam:1"].Hero);
    }

    [Fact]
    public async Task A_pick_survives_the_recheck_and_reload_passes()
    {
        var (service, cache, _, _) = await Setup();
        await service.SetArtworkAsync("steam:1", "logo", Logo);

        await service.ReloadArtworkAsync("steam:1");
        await service.ScanAsync();

        Assert.Equal(Logo, service.Find("steam:1")!.Art.Logo[0]);
        Assert.True(cache.Has(Logo));
    }

    [Fact]
    public async Task A_failed_download_changes_nothing_and_says_so()
    {
        var (service, cache, config, _) = await Setup(Logo);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetArtworkAsync("steam:1", "logo", Logo));

        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.False(cache.Has(Logo));
        Assert.Equal("https://cdn.steamstatic.com/old.png", service.Find("steam:1")!.Art.Logo[0]);
        Assert.Empty(config.Current.Overrides);
    }

    [Fact]
    public async Task Changed_fires_once_per_pick()
    {
        var (service, _, _, _) = await Setup();
        var fired = 0;
        service.Changed += _ => Interlocked.Increment(ref fired);

        await service.SetArtworkAsync("steam:1", "logo", Logo);
        await Task.Delay(300);

        Assert.Equal(1, fired);
    }

    [Fact]
    public async Task Reset_still_works_and_a_foreign_host_is_stored_without_a_download()
    {
        var (service, cache, config, _) = await Setup();

        var foreign = await service.SetArtworkAsync("steam:1", "logo", "https://example.org/own.png");
        Assert.Equal("https://example.org/own.png", foreign.Art.Logo[0]);
        Assert.False(cache.Has("https://example.org/own.png"));

        var reset = await service.SetArtworkAsync("steam:1", "logo", null);
        Assert.Equal("https://cdn.steamstatic.com/old.png", reset.Art.Logo[0]);
        Assert.Empty(config.Current.Overrides);
    }

    [Fact]
    public async Task A_redirect_to_a_foreign_host_fails_and_stores_nothing()
    {
        var host = new ImageHost([]);
        host.Redirects[Logo] = "https://evil.example.org/x.png";
        var (service, cache, config, _) = await SetupWith(host);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetArtworkAsync("steam:1", "logo", Logo));

        Assert.False(cache.Has(Logo));
        Assert.Empty(config.Current.Overrides);
    }

    [Fact]
    public async Task The_lock_is_checked_again_after_the_download()
    {
        var (service, _, config, _) = await Setup();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.SetArtworkAsync("steam:1", "logo", Logo, () => throw new UnauthorizedAccessException()));

        Assert.Empty(config.Current.Overrides);
    }

    [Fact]
    public async Task Of_two_picks_for_one_slot_the_last_chosen_wins()
    {
        var host = new ImageHost([]);
        var gate = host.Gates[Logo] = new TaskCompletionSource();
        var (service, _, config, _) = await SetupWith(host);
        const string second = "https://cdn2.steamgriddb.com/logo/second.png";

        var first = service.SetArtworkAsync("steam:1", "logo", Logo);
        await service.SetArtworkAsync("steam:1", "logo", second);
        gate.SetResult();
        await Assert.ThrowsAsync<PickSupersededException>(() => first);

        Assert.Equal(second, config.Current.Overrides["steam:1"].Logo);
        Assert.Equal(second, service.Find("steam:1")!.Art.Logo[0]);
    }

    [Fact]
    public async Task An_older_pick_past_its_download_cannot_overwrite_a_newer_stored_one()
    {
        var (service, _, config, _) = await Setup();
        const string newer = "https://cdn2.steamgriddb.com/logo/newer.png";
        using var inCheck = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var older = Task.Run(() => service.SetArtworkAsync("steam:1", "logo", Logo, () => { inCheck.Set(); release.Wait(); }));
        inCheck.Wait();
        await service.SetArtworkAsync("steam:1", "logo", newer);
        release.Set();

        await Assert.ThrowsAsync<PickSupersededException>(() => older);
        Assert.Equal(newer, config.Current.Overrides["steam:1"].Logo);
    }

    [Fact]
    public async Task A_failed_newer_pick_does_not_drop_an_older_good_one()
    {
        var host = new ImageHost(["https://cdn2.steamgriddb.com/logo/bad.png"]);
        var gate = host.Gates[Logo] = new TaskCompletionSource();
        var (service, _, config, _) = await SetupWith(host);

        var older = service.SetArtworkAsync("steam:1", "logo", Logo);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetArtworkAsync("steam:1", "logo", "https://cdn2.steamgriddb.com/logo/bad.png"));
        gate.SetResult();
        await older;

        Assert.Equal(Logo, config.Current.Overrides["steam:1"].Logo);
    }
}
