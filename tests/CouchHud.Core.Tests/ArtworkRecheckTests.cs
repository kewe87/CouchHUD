using System.Net;
using System.Text;
using System.Text.Json;
using CouchHud.Core.Library;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>
/// Slots without a working image are looked at again about once a day (fake clock, fake store and CDN): a partial hit is re-resolved,
/// a complete entry is left alone, errors back off, 404s never count as images, and the daily cap holds.
/// </summary>
public class ArtworkRecheckTests
{
    private const string Legacy = "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps";
    private static readonly DateTimeOffset Start = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Steam's store listing (hashed addresses, changeable) and its CDN (only the addresses in <see cref="Live"/> exist).</summary>
    private sealed class FakeSteam : HttpMessageHandler
    {
        public string Hash = "aaaa";
        public bool StoreFails;
        public HashSet<string> Live = [];
        public List<string> StoreCalls = [];
        /// <summary>Store listing requests that reached the handler, counted before a hold (StoreCalls only grows once one is answered).</summary>
        public int StoreArrivals;
        public List<string> ImageCalls = [];

        public static string Hashed(string appId, string hash, string file) =>
            $"https://shared.steamstatic.com/store_item_assets/steam/apps/{appId}/{hash}/{file}?t=1";

        /// <summary>While set, answers of the store listing wait for it (a lookup pass that takes long).</summary>
        public TaskCompletionSource? Hold;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var wait = Hold;
            if (request.RequestUri!.ToString().StartsWith(SteamAssets.Endpoint, StringComparison.Ordinal)) Interlocked.Increment(ref StoreArrivals);
            if (wait is not null && request.RequestUri!.ToString().StartsWith(SteamAssets.Endpoint, StringComparison.Ordinal)) await wait.Task.WaitAsync(ct);
            return await Answer(request);
        }

        private Task<HttpResponseMessage> Answer(HttpRequestMessage request)
        {
            var url = request.RequestUri!.ToString();
            if (url.StartsWith(SteamAssets.Endpoint, StringComparison.Ordinal))
            {
                var input = JsonDocument.Parse(request.RequestUri.Query.Length > 0
                    ? Uri.UnescapeDataString(request.RequestUri.Query.Split("input_json=")[1]) : "{}");
                var ids = input.RootElement.GetProperty("ids").EnumerateArray().Select(e => e.GetProperty("appid").GetInt64()).ToList();
                StoreCalls.Add(string.Join(",", ids));
                if (StoreFails) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                var items = string.Join(",", ids.Select(id => $$"""
                    { "appid": {{id}}, "assets": { "asset_url_format": "steam/apps/{{id}}/${FILENAME}?t=1",
                      "library_capsule": "{{Hash}}/library_capsule.jpg", "library_hero": "{{Hash}}/library_hero.jpg" } }
                    """));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""{ "response": { "store_items": [ {{items}} ] } }""", Encoding.UTF8, "application/json")
                });
            }
            ImageCalls.Add(url);
            if (!Live.Contains(url)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ArtworkCacheTests.Png()) };
            response.Content.Headers.ContentType = new("image/png");
            return Task.FromResult(response);
        }

        public void GoLive(string appId, string hash)
        {
            Live.Add(Hashed(appId, hash, "library_capsule.jpg"));
            Live.Add(Hashed(appId, hash, "library_hero.jpg"));
        }
    }

    /// <summary>Waits for a condition and fails the test after five seconds instead of hanging.</summary>
    private static async Task WaitUntil(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new Xunit.Sdk.XunitException("Timed out after 5 s waiting for the condition.");
            await Task.Delay(10);
        }
    }

    private sealed class Rig
    {
        public DateTimeOffset Now = Start;
        public readonly FakeSteam Steam = new();
        public readonly ArtworkResolver Resolver;
        public readonly ArtworkCache Images;

        public readonly string Dir;

        public Rig(string? existing = null, DateTimeOffset? now = null)
        {
            Now = now ?? Start;
            var dir = Dir = existing ?? Directory.CreateTempSubdirectory("couchhud-recheck-").FullName;
            var http = new HttpClient(Steam);
            Images = new ArtworkCache(Path.Combine(dir, "img"), utcNow: () => Now.UtcDateTime);
            var assets = new SteamAssets(http, Path.Combine(dir, "assets.json"), () => Now);
            Resolver = new ArtworkResolver(http, Path.Combine(dir, "cache.json"), null, Images, new SteamAppIds(http, Path.Combine(dir, "ids.json")), assets)
            {
                Clock = () => Now
            };
        }

        public static LibraryItem Game(string appId) => new()
        {
            Id = $"steam:{appId}",
            Source = "steam",
            Name = "Game " + appId,
            Type = ItemType.Game,
            Launch = new LaunchTarget(LaunchKind.Url, "steam://run/" + appId),
            Art = new Artwork
            {
                Cover = [$"{Legacy}/{appId}/library_600x900.jpg"],
                Hero = [$"{Legacy}/{appId}/library_hero.jpg"]
            }
        };

        /// <summary>One scan: lookup, then the downloads, like the library does.</summary>
        public async Task<(IReadOnlyList<LibraryItem> Items, int Arrived)> PassAsync(params LibraryItem[] items)
        {
            var result = await Resolver.ResolveAsync(items) ?? items;
            var arrived = await Resolver.DownloadMissingAsync(result.SelectMany(i => new[] { i.Art.Cover, i.Art.Hero, i.Art.Logo }).ToList());
            return (result, arrived);
        }

        public bool HeroOnDisk(LibraryItem item) => item.Art.Hero.Any(Images.Has);
    }

    [Fact]
    public async Task A_partial_hit_is_resolved_again_after_a_day_with_the_new_hash()
    {
        var rig = new Rig();
        var game = Rig.Game("3669870");
        // The listing exists but its images are not on the CDN yet: every address 404s and no slot has an image.
        var (first, arrived) = await rig.PassAsync(game);
        Assert.Equal(0, arrived);
        Assert.False(rig.HeroOnDisk(first[0]));
        Assert.Single(rig.Steam.StoreCalls);

        // The store publishes new files under a new hash.
        rig.Steam.Hash = "bbbb";
        rig.Steam.GoLive("3669870", "bbbb");

        // Not due after a few hours: no new question, no new download.
        rig.Now = Start.AddHours(12);
        await rig.PassAsync(game);
        Assert.Single(rig.Steam.StoreCalls);

        // Due after a day (plus at most the jitter): the candidates are asked for again, not only downloaded again.
        rig.Now = Start.AddHours(24 + 5);
        var (second, got) = await rig.PassAsync(game);
        Assert.Equal(2, rig.Steam.StoreCalls.Count);
        Assert.Contains(FakeSteam.Hashed("3669870", "bbbb", "library_hero.jpg"), second[0].Art.Hero);
        Assert.True(got >= 2);
        Assert.True(rig.HeroOnDisk(second[0]));
    }

    [Fact]
    public async Task A_complete_entry_is_not_looked_at_again()
    {
        var rig = new Rig();
        rig.Steam.GoLive("3669870", "aaaa");
        var game = Rig.Game("3669870");
        var (first, arrived) = await rig.PassAsync(game);
        Assert.True(arrived >= 2);
        Assert.True(rig.HeroOnDisk(first[0]));
        var storeCalls = rig.Steam.StoreCalls.Count;
        var imageCalls = rig.Steam.ImageCalls.Count;

        rig.Now = Start.AddDays(10);
        await rig.PassAsync(game);

        Assert.Equal(storeCalls, rig.Steam.StoreCalls.Count);
        Assert.Equal(imageCalls, rig.Steam.ImageCalls.Count);
    }

    [Fact]
    public async Task Errors_back_off_instead_of_asking_every_day()
    {
        var rig = new Rig();
        var game = Rig.Game("3669870");
        await rig.PassAsync(game);
        Assert.Single(rig.Steam.StoreCalls);

        rig.Steam.StoreFails = true;
        rig.Now = Start.AddHours(30);
        await rig.PassAsync(game);
        Assert.Equal(2, rig.Steam.StoreCalls.Count);

        // The next try waits two days (plus jitter), not one.
        rig.Now = Start.AddHours(30 + 30);
        await rig.PassAsync(game);
        Assert.Equal(2, rig.Steam.StoreCalls.Count);

        rig.Now = Start.AddHours(30 + 53);
        await rig.PassAsync(game);
        Assert.Equal(3, rig.Steam.StoreCalls.Count);
    }

    [Fact]
    public async Task Missing_files_are_never_stored_as_images()
    {
        var rig = new Rig();
        var game = Rig.Game("3669870");
        var (items, arrived) = await rig.PassAsync(game);

        Assert.Equal(0, arrived);
        Assert.Equal(0, rig.Images.Count);
        foreach (var url in items[0].Art.Cover.Concat(items[0].Art.Hero).Where(ArtworkSources.IsAllowed))
        {
            Assert.False(rig.Images.Has(url));
            Assert.True(rig.Images.RecentlyFailed(url));
        }
    }

    [Fact]
    public async Task At_most_the_daily_limit_of_entries_is_looked_at_again()
    {
        var rig = new Rig { };
        rig.Resolver.RecheckLimit = 2;
        var games = Enumerable.Range(1, 5).Select(i => Rig.Game((1000 + i).ToString())).ToArray();
        await rig.PassAsync(games);
        var before = rig.Steam.StoreCalls.Count;

        rig.Now = Start.AddHours(30);
        await rig.PassAsync(games);
        Assert.Equal(2, rig.Steam.StoreCalls.Count - before);

        // The same day, again: the budget is used up.
        rig.Now = Start.AddHours(31);
        await rig.PassAsync(games);
        Assert.Equal(2, rig.Steam.StoreCalls.Count - before);
    }

    [Fact]
    public async Task Manual_reload_ignores_the_cache_times_but_not_the_one_minute_gap()
    {
        var rig = new Rig();
        var game = Rig.Game("3669870");
        await rig.PassAsync(game);
        rig.Steam.Hash = "cccc";
        rig.Steam.GoLive("3669870", "cccc");
        var calls = rig.Steam.StoreCalls.Count;

        rig.Now = Start.AddMinutes(5);
        var (item, result) = await rig.Resolver.ReloadAsync(game);
        Assert.Equal(ArtworkResolver.ReloadResult.Found, result);
        Assert.Equal(calls + 1, rig.Steam.StoreCalls.Count);
        Assert.True(rig.HeroOnDisk(item));

        // Pressed again right away: nothing new is asked.
        rig.Now = Start.AddMinutes(5).AddSeconds(20);
        var (_, again) = await rig.Resolver.ReloadAsync(game);
        Assert.Equal(ArtworkResolver.ReloadResult.Nothing, again);
        Assert.Equal(calls + 1, rig.Steam.StoreCalls.Count);
    }

    [Fact]
    public async Task Manual_reload_does_not_touch_other_hosts_and_reports_offline_as_failed()
    {
        var rig = new Rig { };
        rig.Resolver.Online = false;
        var (_, offline) = await rig.Resolver.ReloadAsync(Rig.Game("3669870"));
        Assert.Equal(ArtworkResolver.ReloadResult.Failed, offline);

        rig.Resolver.Online = true;
        rig.Steam.StoreFails = true;
        var (_, failed) = await rig.Resolver.ReloadAsync(Rig.Game("42"));
        Assert.Equal(ArtworkResolver.ReloadResult.Failed, failed);
        // Everything that was asked is on Steam's own hosts.
        Assert.All(rig.Steam.ImageCalls, u => Assert.True(ArtworkSources.IsAllowed(u)));
    }

    // ----- review fixes -----

    [Fact]
    public async Task Manual_reload_is_not_possible_while_a_game_runs()
    {
        var rig = new Rig();
        rig.Resolver.BackgroundAllowed = () => false;
        var (_, result) = await rig.Resolver.ReloadAsync(Rig.Game("3669870"));
        Assert.Equal(ArtworkResolver.ReloadResult.Busy, result);
        Assert.Empty(rig.Steam.StoreCalls);
        Assert.Empty(rig.Steam.ImageCalls);
    }

    [Fact]
    public async Task A_reload_that_waits_too_long_for_a_running_pass_says_later()
    {
        var rig = new Rig();
        rig.Resolver.ReloadWait = TimeSpan.FromMilliseconds(150);
        rig.Steam.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = rig.Resolver.ReloadAsync(Rig.Game("1"));
        await WaitUntil(() => Volatile.Read(ref rig.Steam.StoreArrivals) >= 1);

        var (_, result) = await rig.Resolver.ReloadAsync(Rig.Game("2"));
        Assert.Equal(ArtworkResolver.ReloadResult.Busy, result);

        rig.Steam.Hold.SetResult();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Only_one_reload_waits_in_the_queue_and_the_rest_are_turned_away_at_once()
    {
        var rig = new Rig();
        rig.Resolver.ReloadWait = TimeSpan.FromSeconds(20);
        rig.Steam.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = rig.Resolver.ReloadAsync(Rig.Game("1"));
        await WaitUntil(() => Volatile.Read(ref rig.Steam.StoreArrivals) >= 1);
        var waiting = rig.Resolver.ReloadAsync(Rig.Game("2"));
        await Task.Delay(100);

        var (_, third) = await rig.Resolver.ReloadAsync(Rig.Game("3"));
        Assert.Equal(ArtworkResolver.ReloadResult.Busy, third);

        rig.Steam.Hold.SetResult();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotEqual(ArtworkResolver.ReloadResult.Busy, (await waiting.WaitAsync(TimeSpan.FromSeconds(5))).Result);
    }

    [Fact]
    public async Task All_manual_reloads_together_are_capped_per_ten_minutes()
    {
        var rig = new Rig();
        rig.Steam.StoreFails = true;
        for (var i = 1; i <= ArtworkResolver.ReloadsPerWindow; i++)
        {
            var (_, ok) = await rig.Resolver.ReloadAsync(Rig.Game((2000 + i).ToString()));
            Assert.NotEqual(ArtworkResolver.ReloadResult.Busy, ok);
        }
        var (_, over) = await rig.Resolver.ReloadAsync(Rig.Game("3000"));
        Assert.Equal(ArtworkResolver.ReloadResult.Busy, over);

        rig.Now = Start.AddMinutes(11);
        var (_, later) = await rig.Resolver.ReloadAsync(Rig.Game("3000"));
        Assert.NotEqual(ArtworkResolver.ReloadResult.Busy, later);
    }

    [Fact]
    public async Task An_empty_banner_only_counts_where_a_source_can_supply_one()
    {
        var rig = new Rig();
        // A game without Steam id and without key: its cover is a local file, nothing can supply a banner.
        var own = new LibraryItem
        {
            Id = "folder:x", Source = "folder", Name = "Own game", Type = ItemType.Game,
            Launch = new LaunchTarget(LaunchKind.Url, "https://example.com"),
            Art = new Artwork { Cover = ["C:\\games\\own.png"] }
        };
        await rig.PassAsync(own);
        rig.Now = Start.AddDays(3);
        await rig.PassAsync(own);
        Assert.Empty(rig.Steam.StoreCalls);
        Assert.Empty(rig.Steam.ImageCalls);

        // A Steam game with the same gap does count (it has an id to ask with).
        var steam = Rig.Game("77") with { Art = new Artwork { Cover = ["C:\\games\\c.png"] } };
        await rig.PassAsync(steam);
        rig.Now = rig.Now.AddHours(30);
        await rig.PassAsync(steam);
        Assert.NotEmpty(rig.Steam.StoreCalls);
    }

    [Fact]
    public void After_six_fruitless_tries_an_entry_is_only_looked_at_monthly()
    {
        var now = Start;
        var due5 = ArtworkResolver.RecheckDue("game:x", new ArtworkResolver.Recheck(now, 5), now);
        var due6 = ArtworkResolver.RecheckDue("game:x", new ArtworkResolver.Recheck(now, 6), now);
        Assert.True(due5 < now.AddDays(9));
        Assert.True(due6 >= now.AddDays(30));
    }

    [Fact]
    public void A_damaged_time_never_throws_and_never_waits_forever()
    {
        var now = Start;
        foreach (var last in new[] { DateTimeOffset.MaxValue, DateTimeOffset.MinValue, now.AddYears(500) })
        {
            var due = ArtworkResolver.RecheckDue("game:x", new ArtworkResolver.Recheck(last, int.MaxValue), now);
            Assert.True(due <= now.AddDays(31));
        }
        // Negative tries count as none.
        Assert.True(ArtworkResolver.RecheckDue("game:x", new ArtworkResolver.Recheck(now, -5), now) <= now.AddDays(2));
    }

    [Fact]
    public async Task A_damaged_recheck_file_is_dropped_not_trusted()
    {
        var dir = Directory.CreateTempSubdirectory("couchhud-recheck-").FullName;
        var file = Path.Combine(dir, "cache.rechecks.json");
        File.WriteAllText(file, """{ "Entries": { "a": null, "game:game 3669870": { "Last": "2999-01-01T00:00:00+00:00", "Tries": 99999 }, "": { "Last": "2020-01-01T00:00:00+00:00", "Tries": 1 } }, "Day": "nonsense", "Today": -5 }""");
        var rig = new Rig(dir);
        await rig.PassAsync(Rig.Game("3669870"));

        var saved = File.ReadAllText(file);
        Assert.DoesNotContain("\"a\"", saved);
        Assert.DoesNotContain("2999", saved);
        Assert.DoesNotContain("99999", saved);

        File.WriteAllText(file, "not json at all {{{");
        var again = new Rig(dir);
        await again.PassAsync(Rig.Game("3669870"));
    }

    [Fact]
    public async Task The_days_budget_survives_a_restart()
    {
        var first = new Rig();
        first.Resolver.RecheckLimit = 1;
        var games = new[] { Rig.Game("11"), Rig.Game("12") };
        await first.PassAsync(games);
        first.Now = Start.AddHours(30);
        var before = first.Steam.StoreCalls.Count;
        await first.PassAsync(games);
        Assert.Equal(1, first.Steam.StoreCalls.Count - before);

        // A new process the same day: the budget is already used up.
        var second = new Rig(first.Dir, Start.AddHours(31));
        second.Resolver.RecheckLimit = 1;
        await second.PassAsync(games);
        Assert.Empty(second.Steam.StoreCalls);
    }

    [Fact]
    public async Task A_cancelled_pass_is_not_a_failed_try()
    {
        var rig = new Rig();
        var game = Rig.Game("3669870");
        await rig.PassAsync(game);
        rig.Now = Start.AddHours(30);
        rig.Steam.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        var pass = rig.Resolver.ResolveAsync([game], cts.Token);
        await WaitUntil(() => Volatile.Read(ref rig.Steam.StoreArrivals) >= 2);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pass.WaitAsync(TimeSpan.FromSeconds(5)));
        rig.Steam.Hold.SetResult();

        // The entry is still due right away: nothing was counted against it.
        rig.Steam.Hold = null;
        await rig.PassAsync(game);
        // First pass 1, the cancelled one never got an answer, the retry 1: the cancelled pass left the entry due.
        Assert.Equal(2, rig.Steam.StoreCalls.Count);
        Assert.Equal(3, rig.Steam.StoreArrivals);
    }
}
