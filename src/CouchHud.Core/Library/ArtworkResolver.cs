using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using CouchHud.Sdk;

namespace CouchHud.Core.Library;

/// <summary>
/// Finds cover, hero, clear logo and icon art for entries the scanners couldn't provide. Sources, in order:
///  1. What the launchers keep locally (Epic catalog, package logos): already on the item from the scanners
///  2. SteamGridDB (free account, API key in config) - covers nearly everything
///  3. Steam store search by name (no key; see <see cref="NameMatch"/>) - then Steam's CDN artwork
///  4. The exe's own icon (for apps and games started from an exe), extracted locally
/// What stays empty is drawn by the UI as a generated banner.
/// Art the user set themselves always wins; lookups only fill empty slots. Lookups (including misses)
/// and the images themselves are kept on disk, and nothing is asked in the background while a game runs.
/// </summary>
public sealed partial class ArtworkResolver
{
    private const string SteamGridDb = "https://www.steamgriddb.com/api/v2";
    private static readonly TimeSpan MissRetry = TimeSpan.FromDays(7);

    private readonly HttpClient _http;
    private readonly string _cacheFile;
    private readonly Dictionary<string, CacheEntry> _cache;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _cacheLock = new();
    private readonly SteamAppIds _ids;
    private readonly SteamAssets _steamAssets;

    public ArtworkResolver(HttpClient http, string cacheFile, string? apiKey, ArtworkCache? images = null, SteamAppIds? ids = null, SteamAssets? steamAssets = null)
    {
        _http = http;
        _cacheFile = cacheFile;
        _cache = LoadCache(cacheFile);
        DropLegacySteamHits(cacheFile);
        Images = images;
        _ids = ids ?? new SteamAppIds(http, Path.ChangeExtension(cacheFile, ".steamids.json"));
        _steamAssets = steamAssets ?? new SteamAssets(http, Path.ChangeExtension(cacheFile, ".steamassets.json"));
        // Straight to the field: the setter also drops cached misses, which is only right for a key change.
        _apiKey = apiKey?.Trim() ?? "";
    }

    /// <summary>The image cache on disk; null in tests that don't need one.</summary>
    public ArtworkCache? Images { get; }

    /// <summary>Settings switch: false means no artwork lookups and downloads at all (cached images stay in use).</summary>
    public bool Online { get; set; } = true;

    /// <summary>Background work (lookups, downloads) only runs while this says yes: the host says no during a game.</summary>
    public Func<bool> BackgroundAllowed { get; set; } = () => true;

    /// <summary>How long a blocked background pass waits before it asks again; replaced in tests.</summary>
    public Func<CancellationToken, Task> BackgroundWait { get; set; } = ct => Task.Delay(TimeSpan.FromSeconds(30), ct);

    /// <summary>Renders an exe's icon as png (big); null if it has none. Set by the host, which knows Windows.</summary>
    public Func<string, byte[]?>? ExeIcons { get; set; }

    /// <summary>SteamGridDB key; can change at runtime from the settings page.</summary>
    public string ApiKey
    {
        get => _apiKey;
        set
        {
            var key = value?.Trim() ?? "";
            lock (_cacheLock)
            {
                if (key == _apiKey) return;
                _apiKey = key;
                // A new key can find what the Steam store couldn't: forget earlier misses.
                foreach (var miss in _cache.Where(e => e.Value.Art is null).Select(e => e.Key).ToList()) _cache.Remove(miss);
            }
        }
    }

    private volatile string _apiKey = "";

    internal sealed record CachedArt(List<string> Cover, List<string> Hero, List<string>? Logo = null);

    internal sealed record CacheEntry(CachedArt? Art, DateTimeOffset Checked);

    public static string Normalize(string name)
    {
        var s = name.ToLowerInvariant().Replace("™", "").Replace("®", "").Replace("©", "").Replace("&", "and");
        return NonAlphaNumeric().Replace(s, " ").Trim();
    }

    private bool NeedsArt(LibraryItem item) =>
        (item.Art.Cover.Count == 0 && (item.Type == ItemType.Game || _apiKey.Length > 0))
        // Clear logos only come from SteamGridDB for entries that already have a cover.
        || (item.Art.Logo.Count == 0 && _apiKey.Length > 0);

    /// <summary>The store entry the user picked for an entry (an app id or "none"), by item id; null: automatic. Set by the library.</summary>
    public Func<string, string?> StoreMatchOf { get; set; } = _ => null;

    /// <summary>A picked store entry has its own cache line, so changing the pick never reuses the art of the old one.</summary>
    private string Key(LibraryItem item) => StoreMatchOf(item.Id) is { } match && (match == StoreMatches.None || StoreMatches.IsAppId(match))
        ? $"{item.Type}:store:{match}".ToLowerInvariant()
        : $"{item.Type}:{Normalize(item.Name)}".ToLowerInvariant();

    /// <summary>
    /// The Steam app id the art comes from: Steam's own, the picked one, none when "none" was picked, else by name (games only:
    /// apps of the same name are too often something else).
    /// </summary>
    private async Task<string?> StoreAppIdAsync(LibraryItem item, CancellationToken ct)
    {
        if (SteamStore.AppIdOf(item) is { } steam) return steam;
        var match = StoreMatchOf(item.Id);
        if (match == StoreMatches.None) return null;
        if (StoreMatches.IsAppId(match)) return match;
        return item.Type == ItemType.Game ? await _ids.FindAsync(item.Name, ct) : null;
    }

    /// <summary>Entries started from an exe get its icon as the last resort (and as the face of the generated banner).</summary>
    private bool NeedsIcon(LibraryItem item) =>
        ExeIcons is not null && item.Art.Icon.Count == 0 && HasIconSource(item);

    /// <summary>An exe with a path, or a Store app of the apps folder (the shell draws its icon from the same name).</summary>
    private static bool HasIconSource(LibraryItem item) =>
        item.Launch is { Kind: LaunchKind.Exe } && Path.IsPathRooted(item.Launch.Target)
        || item.Launch is { Kind: LaunchKind.Url } && InstalledApps.IdOf(item.Launch.Target) is not null;

    /// <summary>One icon per exe and version of it: a game that is updated gets its new icon.</summary>
    private static string IconKey(LibraryItem item)
    {
        long written = 0;
        try
        {
            if (item.Launch.Kind == LaunchKind.Exe) written = File.GetLastWriteTimeUtc(item.Launch.Target).Ticks;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }
        return $"exeicon:{item.Launch.Target.ToLowerInvariant()}|{written}";
    }

    /// <summary>Applies what's already cached, without network access.</summary>
    public IReadOnlyList<LibraryItem> ApplyCached(IReadOnlyList<LibraryItem> items) =>
        items.Select(i =>
        {
            i = WithSteamAssets(i, _steamAssets.Peek);
            if (NeedsArt(i) && CachedLookup(Key(i)) is { Art: not null } hit) i = WithArt(i, hit.Art);
            if (i.Art.Icon.Count == 0 && HasIconSource(i) && Images is not null && Images.Has(IconKey(i)))
                i = i with { Art = i.Art with { Icon = [ArtworkCache.UrlFor(IconKey(i))] } };
            return i;
        }).ToList();

    private CacheEntry? CachedLookup(string key)
    {
        lock (_cacheLock) return _cache.GetValueOrDefault(key);
    }

    private async Task WaitForBackgroundAsync(CancellationToken ct)
    {
        while (!BackgroundAllowed()) await BackgroundWait(ct);
    }

    /// <summary>Looks up everything still missing. Returns null if nothing new was found.</summary>
    public async Task<IReadOnlyList<LibraryItem>?> ResolveAsync(IReadOnlyList<LibraryItem> items, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var changed = false;
            var lookupsBlocked = !Online;
            var result = new List<LibraryItem>(items.Count);

            // Steam's own games first: their images live under hashed addresses that only the store lists.
            if (!lookupsBlocked)
            {
                var wanted = items.Select(SteamStore.AppIdOf).Where(id => id is not null && !_steamAssets.Peek(id).Known).Select(id => id!).ToList();
                if (wanted.Count > 0 && items.Any(i => SteamStore.AppIdOf(i) is not null && HasLegacySteamUrl(i)))
                {
                    await WaitForBackgroundAsync(ct);
                    try
                    {
                        await _steamAssets.EnsureAsync(wanted.Where(id => items.Any(i => SteamStore.AppIdOf(i) == id && HasLegacySteamUrl(i))), ct);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
                    {
                        Console.Error.WriteLine($"[artwork] steam assets: {ex.Message}");
                        if (ex is TaskCanceledException or HttpRequestException
                            { StatusCode: System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.ServiceUnavailable })
                        {
                            lookupsBlocked = true;
                        }
                    }
                }
            }

            foreach (var original in items)
            {
                var item = WithSteamAssets(original, _steamAssets.Peek);
                if (!ReferenceEquals(item, original)) changed = true;
                if (NeedsArt(item))
                {
                    var key = Key(item);
                    if (CachedLookup(key) is { } cached
                        && (cached.Art is not null || DateTimeOffset.UtcNow - cached.Checked < MissRetry))
                    {
                        if (cached.Art is not null) item = WithArt(item, cached.Art);
                    }
                    else if (!lookupsBlocked)
                    {
                        await WaitForBackgroundAsync(ct);
                        try
                        {
                            // Steam's store first (no key, official images), SteamGridDB for what it doesn't have.
                            var art = item.Type == ItemType.Game || StoreMatches.IsAppId(StoreMatchOf(item.Id)) ? await FromSteamStore(item, ct) : null;
                            if (art is null && _apiKey.Length > 0) art = await FromSteamGridDb(item, ct);
                            lock (_cacheLock) _cache[key] = new CacheEntry(art, DateTimeOffset.UtcNow);
                            if (art is not null)
                            {
                                changed = true;
                                item = WithArt(item, art);
                            }
                        }
                        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
                        {
                            // Offline or rate limited: try again next time, don't cache a miss.
                            Console.Error.WriteLine($"[artwork] {item.Name}: {ex.Message}");
                            // A host that said "slow down" or is paused gets no more questions in this pass.
                            if (ex is TaskCanceledException or HttpRequestException
                                { StatusCode: System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.ServiceUnavailable })
                            {
                                lookupsBlocked = true;
                            }
                        }
                    }
                }

                if (NeedsIcon(item) && item.Art.Cover.Count == 0 && Images is not null && !Images.Has(IconKey(item)))
                {
                    await WaitForBackgroundAsync(ct);
                }
                if (NeedsIcon(item) && item.Art.Cover.Count == 0 && ExtractIcon(item) is { } icon)
                {
                    item = item with { Art = item.Art with { Icon = [icon] } };
                    changed = true;
                }
                result.Add(item);
            }
            SaveCache();
            return changed ? result : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The exe's icon into the image cache (once; a file without icon is not tried again for a week).</summary>
    private string? ExtractIcon(LibraryItem item)
    {
        if (Images is null || ExeIcons is null) return null;
        var key = IconKey(item);
        if (Images.Has(key)) return ArtworkCache.UrlFor(key);
        if (Images.RecentlyFailed(key)) return null;
        // A file that is gone (drive not mounted, game being updated) is not a verdict: ask again next time.
        if (item.Launch.Kind == LaunchKind.Exe && !File.Exists(item.Launch.Target)) return null;
        try
        {
            if (ExeIcons(item.Launch.Target) is { Length: > 0 } png)
            {
                switch (Images.TryStore(key, png))
                {
                    case ArtworkCache.StoreResult.Stored: return ArtworkCache.UrlFor(key);
                    case ArtworkCache.StoreResult.Error: return null;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // The shell or the disk hiccuped: also not a verdict.
            Console.Error.WriteLine($"[artwork] icon of {item.Name}: {ex.Message}");
            return null;
        }
        Images.MarkFailed(key);
        return null;
    }

    // ----- images: download once, then serve from disk -----

    private enum Fetch { Have, Downloaded, Failed, Transient }

    /// <summary>
    /// Downloads the first usable https image of each slot that isn't on disk yet (slots are candidate
    /// lists in order; a local file as the first candidate means nothing to do). Gentle: sequential, behind
    /// the polite handler, stops when a game starts, and gives up after repeated transient errors.
    /// Returns how many images arrived. <paramref name="progress"/> is called every few downloads.
    /// </summary>
    public async Task<int> DownloadMissingAsync(IEnumerable<IReadOnlyList<string>> slots, Action? progress = null, CancellationToken ct = default)
    {
        if (Images is null || !Online) return 0;
        var downloaded = 0;
        var transient = 0;
        foreach (var slot in slots)
        {
            if (slot.Count == 0 || !ArtworkSources.IsAllowed(slot[0])) continue;
            foreach (var url in slot)
            {
                // A local file, or an address the UI loads itself, ends the list: nothing below it is shown anyway.
                if (!ArtworkSources.IsAllowed(url)) break;
                if (Images.Has(url)) break;
                if (Images.RecentlyFailed(url)) continue;
                await WaitForBackgroundAsync(ct);
                var outcome = await FetchImageAsync(url, ct);
                if (outcome == Fetch.Downloaded)
                {
                    transient = 0;
                    if (++downloaded % 20 == 0) progress?.Invoke();
                }
                if (outcome is Fetch.Have or Fetch.Downloaded) break;
                if (outcome == Fetch.Transient) break;
            }
            // Three in a row: offline or the host is unhappy; the next scan tries again.
            if (transient >= 3) break;
        }
        return downloaded;

        async Task<Fetch> FetchImageAsync(string url, CancellationToken token)
        {
            var outcome = await FetchOneAsync(url, token);
            transient = outcome == Fetch.Transient ? transient + 1 : outcome == Fetch.Failed ? transient : 0;
            return outcome;
        }
    }

    /// <summary>Caches one image the user just picked (a deliberate click: not held back by the background rules).</summary>
    public async Task<bool> CacheNowAsync(string url, CancellationToken ct = default)
    {
        if (Images is null || !ArtworkSources.IsAllowed(url)) return false;
        if (Images.Has(url)) return true;
        return await FetchOneAsync(url, ct, force: true) == Fetch.Downloaded;
    }

    /// <summary>Longest time one image may take, from the first byte sent to the last received (the HttpClient timeout only covers the headers).</summary>
    public TimeSpan DownloadTimeout { get; set; } = TimeSpan.FromSeconds(30);

    private const int MaxRedirects = 3;

    private async Task<Fetch> FetchOneAsync(string url, CancellationToken ct, bool force = false)
    {
        var images = Images!;
        if (images.Has(url)) return Fetch.Have;
        if (!force && images.RecentlyFailed(url)) return Fetch.Failed;
        if (!ArtworkSources.IsAllowed(url))
        {
            images.MarkFailed(url);
            return Fetch.Failed;
        }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(DownloadTimeout);
        try
        {
            var current = new Uri(url);
            HttpResponseMessage response;
            // Redirects are followed by hand (the handler doesn't): few, and each target is checked like the first address.
            for (var hop = 0; ; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
                if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) break;
                var target = response.Headers.Location is { } location ? (location.IsAbsoluteUri ? location : new Uri(current, location)) : null;
                response.Dispose();
                if (target is null || hop >= MaxRedirects || !ArtworkSources.IsAllowed(target))
                {
                    images.MarkFailed(url);
                    return Fetch.Failed;
                }
                current = target;
            }

            using (response)
            {
                var code = (int)response.StatusCode;
                if (code is 404 or 410 or 403 or 400)
                {
                    images.MarkFailed(url);
                    return Fetch.Failed;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > ArtworkCache.MaxImageBytes)
                {
                    images.MarkFailed(url);
                    return Fetch.Failed;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(limit.Token);
                var data = await ReadLimitedAsync(stream, ArtworkCache.MaxImageBytes, limit.Token);
                // What it is, the bytes say (the content type is only the server's claim).
                switch (data is null ? ArtworkCache.StoreResult.Rejected : images.TryStore(url, data))
                {
                    case ArtworkCache.StoreResult.Stored: return Fetch.Downloaded;
                    case ArtworkCache.StoreResult.Error: return Fetch.Transient;
                    default:
                        images.MarkFailed(url);
                        return Fetch.Failed;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            Console.Error.WriteLine($"[artwork] {url}: {ex.Message}");
            return Fetch.Transient;
        }
    }

    private static async Task<byte[]?> ReadLimitedAsync(Stream stream, long limit, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>Source addresses of everything the library shows now, so a cleanup never removes what is in use.</summary>
    public void PruneImages(IEnumerable<LibraryItem> inUse)
    {
        Images?.Prune(inUse.SelectMany(i => i.Art.Cover.Concat(i.Art.Hero).Concat(i.Art.Logo).Concat(i.Art.Icon))
            .Concat(inUse.Where(i => i.Launch.Kind == LaunchKind.Exe).Select(IconKey)));
    }

    // ----- lookups -----

    private async Task<CachedArt?> FromSteamGridDb(LibraryItem item, CancellationToken ct)
    {
        using var search = await GetJson($"{SteamGridDb}/search/autocomplete/{Uri.EscapeDataString(item.Name)}", ct, auth: true);
        if (FirstInData(search.RootElement, "id") is not { } idElement) return null;
        var id = idElement.GetRawText();

        var dims = item.Type == ItemType.App ? "920x430,460x215" : "600x900";
        var cover = await FirstUrl($"{SteamGridDb}/grids/game/{id}?dimensions={dims}", ct);
        var hero = await FirstUrl($"{SteamGridDb}/heroes/game/{id}", ct);
        var logo = await FirstUrl($"{SteamGridDb}/logos/game/{id}", ct);
        if (cover is null && hero is null && logo is null) return null;
        return new CachedArt(cover is null ? [] : [cover], hero is null ? [] : [hero], logo is null ? [] : [logo]);
    }

    private async Task<string?> FirstUrl(string url, CancellationToken ct)
    {
        try
        {
            using var doc = await GetJson(url, ct, auth: true);
            return FirstInData(doc.RootElement, "url")?.GetString();
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Only "there is nothing" counts as a miss; 429/5xx bubble up and are retried next time.
            return null;
        }
    }

    private static JsonElement? FirstInData(JsonElement root, string property)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
        foreach (var entry in data.EnumerateArray())
        {
            if (entry.TryGetProperty(property, out var value)) return value;
        }
        return null;
    }

    private async Task<CachedArt?> FromSteamStore(LibraryItem item, CancellationToken ct)
    {
        var appId = await StoreAppIdAsync(item, ct);
        if (appId is null) return null;
        var found = await _steamAssets.GetAsync(appId, ct);
        // The old fixed names come last: older apps still have them, newer ones only the hashed addresses.
        return new CachedArt(
            [.. found?.Cover ?? [], $"{SteamScanner.Cdn}/{appId}/library_600x900.jpg"],
            [.. found?.Hero ?? [], $"{SteamScanner.Cdn}/{appId}/library_hero.jpg"],
            [.. found?.Logo ?? [], $"{SteamScanner.Cdn}/{appId}/logo.png"]);
    }

    private static bool HasLegacySteamUrl(LibraryItem item) =>
        item.Art.Cover.Concat(item.Art.Hero).Concat(item.Art.Logo).Any(SteamAssets.IsLegacyUrl);

    /// <summary>
    /// A Steam game whose images are only known by the old fixed names gets the store's real addresses in
    /// front of them (local files stay first, the old names stay as the last try). Same item if nothing changes.
    /// </summary>
    internal static LibraryItem WithSteamAssets(LibraryItem item, Func<string, (bool Known, SteamArt? Art)> lookup)
    {
        if (SteamStore.AppIdOf(item) is not { } appId || !HasLegacySteamUrl(item)) return item;
        if (lookup(appId) is not { Known: true, Art: { } art }) return item;

        List<string> Merge(IReadOnlyList<string> current, List<string> fresh) =>
            current.Where(u => !SteamAssets.IsLegacyUrl(u)).Concat(fresh).Concat(current.Where(SteamAssets.IsLegacyUrl)).Distinct().ToList();

        var merged = item.Art with { Cover = Merge(item.Art.Cover, art.Cover), Hero = Merge(item.Art.Hero, art.Hero), Logo = Merge(item.Art.Logo, art.Logo) };
        return merged.Cover.SequenceEqual(item.Art.Cover) && merged.Hero.SequenceEqual(item.Art.Hero) && merged.Logo.SequenceEqual(item.Art.Logo)
            ? item
            : item with { Art = merged };
    }

    /// <summary>
    /// Once: lookups from before the hashed addresses that only hold the old fixed names are forgotten, so the
    /// games they belong to are looked up again. Everything else in the cache stays.
    /// </summary>
    private void DropLegacySteamHits(string cacheFile)
    {
        var marker = cacheFile + ".steamhash";
        if (File.Exists(marker)) return;
        var stale = _cache.Where(e => e.Value.Art is { } a
            && a.Cover.Concat(a.Hero).Concat(a.Logo ?? []).All(SteamAssets.IsLegacyUrl)).Select(e => e.Key).ToList();
        foreach (var key in stale) _cache.Remove(key);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cacheFile))!);
            File.WriteAllText(marker, "1");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[artwork] could not write migration marker: {ex.Message}");
        }
    }

    private async Task<JsonDocument> GetJson(string url, CancellationToken ct, bool auth)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (auth) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static LibraryItem WithArt(LibraryItem item, CachedArt art) => item with
    {
        Art = item.Art with
        {
            Cover = item.Art.Cover.Count > 0 ? item.Art.Cover : art.Cover,
            Hero = item.Art.Hero.Count > 0 ? item.Art.Hero : art.Hero,
            Logo = item.Art.Logo.Count > 0 ? item.Art.Logo : art.Logo ?? new List<string>()
        }
    };

    private static Dictionary<string, CacheEntry> LoadCache(string file)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(file)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
            // Temp file and move: a crash in between never leaves half a cache behind.
            string json;
            lock (_cacheLock) json = JsonSerializer.Serialize(_cache);
            File.WriteAllText(_cacheFile + ".tmp", json);
            File.Move(_cacheFile + ".tmp", _cacheFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[artwork] could not write cache: {ex.Message}");
        }
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphaNumeric();
}
