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

    /// <summary>Games whose square is looked up in one pass (a big library starts slowly, the rest follows with the next pass); the cache is saved every <see cref="SquareSaveEvery"/> lookups.</summary>
    public int SquareLookupLimit { get; set; } = 60;

    private const int SquareSaveEvery = 20;
    private int _squaresLeft = int.MaxValue;
    private int _squaresDone;

    private bool TakeSquareBudget()
    {
        if (_squaresLeft <= 0) return false;
        _squaresLeft--;
        _squaresDone++;
        return true;
    }

    /// <summary>The square grid sizes asked for (the grids endpoint filters by dimensions).</summary>
    internal const string SquareDimensions = "512x512,1024x1024";
    private static readonly TimeSpan MissRetry = TimeSpan.FromDays(7);

    /// <summary>An entry with a slot that has no image on disk is looked at again after this (longer after fruitless tries).</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromHours(24);

    /// <summary>Most entries that are looked at again per day; the rest wait for the next day.</summary>
    public const int DefaultRecheckLimit = 25;

    /// <summary>After this many fruitless looks in a row an entry is only looked at every <see cref="SlowInterval"/>.</summary>
    private const int SlowAfterTries = 6;

    private static readonly TimeSpan SlowInterval = TimeSpan.FromDays(30);

    /// <summary>Entries remembered for the daily check; the oldest go first.</summary>
    private const int MaxRechecks = 5000;

    /// <summary>A manual reload of one entry is not repeated within this time.</summary>
    public static readonly TimeSpan ReloadGap = TimeSpan.FromMinutes(1);

    private readonly HttpClient _http;
    private readonly string _cacheFile;
    private readonly Dictionary<string, CacheEntry> _cache;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _cacheLock = new();
    private readonly SteamAppIds _ids;
    private readonly SteamAssets _steamAssets;
    private readonly string _recheckFile;
    private readonly Dictionary<string, Recheck> _rechecks;
    private readonly Dictionary<string, DateTimeOffset> _reloads = new();
    private DateOnly _recheckDay;
    private int _rechecksToday;

    /// <summary>The time source; replaced in tests.</summary>
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    /// <summary>Daily limit of re-checks (see <see cref="DefaultRecheckLimit"/>).</summary>
    public int RecheckLimit { get; set; } = DefaultRecheckLimit;

    public ArtworkResolver(HttpClient http, string cacheFile, string? apiKey, ArtworkCache? images = null, SteamAppIds? ids = null, SteamAssets? steamAssets = null)
    {
        _http = http;
        _cacheFile = cacheFile;
        _cache = LoadCache(cacheFile);
        _recheckFile = Path.ChangeExtension(cacheFile, ".rechecks.json");
        _rechecks = LoadRechecks(_recheckFile);
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

    /// <summary>Square is null for lookups from before the slot existed (never asked), an empty list when SteamGridDB has none.</summary>
    internal sealed record CachedArt(List<string> Cover, List<string> Hero, List<string>? Logo = null, List<string>? Square = null);

    internal sealed record CacheEntry(CachedArt? Art, DateTimeOffset Checked);

    /// <summary>When an entry with a missing image was last looked at again, and how often in a row that found nothing new.</summary>
    internal sealed record Recheck(DateTimeOffset Last, int Tries);

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
            i = WithCachedSquare(i);
            if (i.Art.Icon.Count == 0 && HasIconSource(i) && Images is not null && Images.Has(IconKey(i)))
                i = i with { Art = i.Art with { Icon = [ArtworkCache.UrlFor(IconKey(i))] } };
            return i;
        }).ToList();

    /// <summary>The square picture an earlier lookup found, for an entry without one (no network).</summary>
    private LibraryItem WithCachedSquare(LibraryItem item) =>
        item.Art.Square.Count == 0 && CachedLookup(Key(item)) is { Art.Square: { Count: > 0 } square }
            ? item with { Art = item.Art with { Square = square } }
            : item;

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
            _squaresLeft = SquareLookupLimit;
            var savedAt = _squaresDone;

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
                var recheckKey = Key(item);
                if (NeedsArt(item))
                {
                    var key = Key(item);
                    if (CachedLookup(key) is { } cached
                        && (cached.Art is not null || Clock() - cached.Checked < MissRetry))
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
                            lock (_cacheLock) _cache[key] = new CacheEntry(art, Clock());
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

                // Squares: what is known is applied; a cache from before the slot existed is asked once (needs the key).
                item = WithCachedSquare(item);
                if (!lookupsBlocked && Online && _squaresLeft > 0 && _apiKey.Length > 0 && item.Art.Square.Count == 0 && CachedLookup(recheckKey) is { Art.Square: null })
                {
                    await WaitForBackgroundAsync(ct);
                    try
                    {
                        var before = item;
                        item = await FillSquareAsync(item, recheckKey, ct);
                        changed |= !ReferenceEquals(before, item);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
                    {
                        Console.Error.WriteLine($"[artwork] square {item.Name}: {ex.Message}");
                        if (ex is TaskCanceledException or HttpRequestException
                            { StatusCode: System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.ServiceUnavailable })
                        {
                            lookupsBlocked = true;
                        }
                    }
                }

                // A slot without a working image is looked at again about once a day, not only when everything is missing.
                if (Images is not null && CanLookup(item) && HasMissingSlot(item))
                {
                    if (!lookupsBlocked && Online)
                    {
                        var now = Clock();
                        var state = GetRecheck(recheckKey);
                        if (state is null) SetRecheck(recheckKey, new Recheck(now, 0));
                        else if (now >= RecheckDue(recheckKey, state, now) && TakeRecheckBudget(now))
                        {
                            await WaitForBackgroundAsync(ct);
                            var outcome = await RecheckAsync(original, item, recheckKey, state, ct);
                            item = outcome.Item;
                            changed |= outcome.Changed;
                            lookupsBlocked |= outcome.Blocked;
                        }
                    }
                }
                else
                {
                    DropRecheck(recheckKey);
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
                // A closed app keeps what was found so far.
                if (_squaresDone - savedAt >= SquareSaveEvery)
                {
                    savedAt = _squaresDone;
                    SaveCache();
                }
            }
            SaveCache();
            return changed ? result : null;
        }
        finally
        {
            _squaresLeft = int.MaxValue;
            _gate.Release();
        }
    }

    // ----- slots without an image: looked at again -----

    /// <summary>Whether this entry has a store or SteamGridDB to ask at all.</summary>
    private bool CanLookup(LibraryItem item) =>
        item.Type == ItemType.Game || StoreMatches.IsAppId(StoreMatchOf(item.Id)) || _apiKey.Length > 0;

    /// <summary>
    /// A slot counts as missing when no image of it is on disk: it is empty, or none of its downloadable addresses has arrived.
    /// A local file (or an address the UI loads itself) first in the list means the slot is served.
    /// </summary>
    private bool SlotMissing(IReadOnlyList<string> slot)
    {
        if (slot.Count == 0) return true;
        foreach (var url in slot)
        {
            if (!ArtworkSources.IsAllowed(url) || Images!.Has(url)) return false;
        }
        return true;
    }

    private bool CanSupplyHero(LibraryItem item) =>
        SteamStore.AppIdOf(item) is not null || StoreMatches.IsAppId(StoreMatchOf(item.Id)) || _apiKey.Length > 0;

    /// <summary>Cover and banner (hero) always count; the logo only when a real address for it is known (most Steam games have none).</summary>
    private bool HasMissingSlot(LibraryItem item) =>
        SlotMissing(item.Art.Cover)
        // An empty banner counts like an empty logo: only where a source can supply one (a Steam app id or a SteamGridDB key).
        || (item.Art.Hero.Count > 0 || CanSupplyHero(item)) && SlotMissing(item.Art.Hero)
        || (item.Art.Logo.Any(u => ArtworkSources.IsAllowed(u) && !SteamAssets.IsLegacyUrl(u)) && SlotMissing(item.Art.Logo))
        // Squares only come from SteamGridDB: with a key an empty (or not yet arrived) square slot is looked at again.
        // An empty list that was asked for (cache says "none") is an answer; only a square never asked for, or a listed one not on disk, counts. The manual reload still tries again.
        || (_apiKey.Length > 0 && (item.Art.Square.Count > 0 ? SlotMissing(item.Art.Square) : !SquareAsked(item)));

    private bool SquareAsked(LibraryItem item) => CachedLookup(Key(item)) is { Art.Square: not null };

    /// <summary>When an entry is due: a day after the last look, doubled after each fruitless one (up to 8 days), plus a fixed per-entry jitter of up to 4 hours.</summary>
    internal static DateTimeOffset RecheckDue(string key, Recheck state, DateTimeOffset now)
    {
        try
        {
            var jitter = TimeSpan.FromMinutes(Convert.ToInt32(ArtworkCache.HashOf(key)[..4], 16) % 240);
            // A time in the future (damaged file, clock set back) counts as now; after six fruitless tries only monthly.
            var last = state.Last > now ? now : state.Last;
            var wait = state.Tries >= SlowAfterTries ? SlowInterval : RecheckInterval * Math.Pow(2, Math.Clamp(state.Tries, 0, 3));
            return last + wait + jitter;
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException or FormatException)
        {
            return now + RecheckInterval;
        }
    }

    private bool TakeRecheckBudget(DateTimeOffset now)
    {
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        lock (_cacheLock)
        {
            if (day != _recheckDay)
            {
                _recheckDay = day;
                _rechecksToday = 0;
            }
            if (_rechecksToday >= RecheckLimit) return false;
            _rechecksToday++;
            return true;
        }
    }

    private Recheck? GetRecheck(string key)
    {
        lock (_cacheLock) return _rechecks.GetValueOrDefault(key);
    }

    private void SetRecheck(string key, Recheck state)
    {
        lock (_cacheLock) _rechecks[key] = state;
    }

    private void DropRecheck(string key)
    {
        lock (_cacheLock) _rechecks.Remove(key);
    }

    private static bool SameArt(Artwork a, Artwork b) =>
        a.Cover.SequenceEqual(b.Cover) && a.Hero.SequenceEqual(b.Hero) && a.Logo.SequenceEqual(b.Logo) && a.Square.SequenceEqual(b.Square);

    private sealed record RecheckOutcome(LibraryItem Item, bool Changed, bool Blocked);

    /// <summary>
    /// Asks the store again for one entry: the hashed Steam addresses change with every art update, so the candidates are
    /// resolved anew (not just downloaded again). Earlier download failures of the slots that miss are forgotten, so the
    /// next download pass tries them once more. Only fills empty slots; nothing the scanner or the user set is replaced.
    /// </summary>
    private async Task<RecheckOutcome> RecheckAsync(LibraryItem source, LibraryItem current, string key, Recheck previous, CancellationToken ct)
    {
        var now = Clock();
        try
        {
            if (await StoreAppIdAsync(current, ct) is { } appId) await _steamAssets.RefreshAsync(appId, ct);
            var item = WithSteamAssets(source, _steamAssets.Peek);
            var art = item.Type == ItemType.Game || StoreMatches.IsAppId(StoreMatchOf(item.Id)) ? await FromSteamStore(item, ct) : null;
            if (art is null && _apiKey.Length > 0) art = await FromSteamGridDb(item, ct);
            if (art is not null)
            {
                lock (_cacheLock) _cache[key] = new CacheEntry(art, now);
                item = WithArt(item, art);
            }
            item = await FillSquareAsync(item, key, ct, force: true);
            foreach (var slot in new[] { item.Art.Cover, item.Art.Hero, item.Art.Logo, item.Art.Square })
            {
                if (!SlotMissing(slot)) continue;
                foreach (var url in slot.Where(ArtworkSources.IsAllowed)) Images!.ClearFailed(url);
            }
            item = KeepCached(item, key);
            var changed = !SameArt(item.Art, current.Art);
            SetRecheck(key, new Recheck(now, changed ? 0 : previous.Tries + 1));
            return new RecheckOutcome(item, changed, false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // (A cancelled pass is not a failed try: it propagates.) Backs off like a fruitless look; a host that said "slow down" gets no more questions in this pass.
            Console.Error.WriteLine($"[artwork] re-check {current.Name}: {ex.Message}");
            SetRecheck(key, new Recheck(now, previous.Tries + 1));
            var blocked = ex is TaskCanceledException or HttpRequestException
                { StatusCode: System.Net.HttpStatusCode.TooManyRequests or System.Net.HttpStatusCode.ServiceUnavailable };
            return new RecheckOutcome(current, false, blocked);
        }
    }

    public enum ReloadResult { Found, Nothing, Failed, Busy }

    /// <summary>Longest a manual reload waits for a running lookup pass before it says "later".</summary>
    public TimeSpan ReloadWait { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>All manual reloads together: at most this many per <see cref="ReloadWindow"/>.</summary>
    public const int ReloadsPerWindow = 10;

    public static readonly TimeSpan ReloadWindow = TimeSpan.FromMinutes(10);

    private readonly Queue<DateTimeOffset> _reloadTimes = new();
    private int _reloadsWaiting;

    /// <summary>
    /// Manual "reload artwork" for one entry: ignores the cache times, resolves all slots anew and downloads what is missing.
    /// Repeated within <see cref="ReloadGap"/> does nothing new. Works on the automatic art only; the caller applies the user's picks afterwards.
    /// Returns the entry with the new candidates and the verdict.
    /// </summary>
    public async Task<(LibraryItem Item, ReloadResult Result)> ReloadAsync(LibraryItem source, CancellationToken ct = default)
    {
        if (Images is null || !Online || !CanLookup(source)) return (source, ReloadResult.Failed);
        // Like all background work: not while a game runs (a deliberate click still must not disturb it).
        if (!BackgroundAllowed()) return (source, ReloadResult.Busy);
        var key = Key(source);
        var now = Clock();
        lock (_cacheLock)
        {
            if (_reloads.TryGetValue(key, out var last) && now - last < ReloadGap) return (source, ReloadResult.Nothing);
            while (_reloadTimes.Count > 0 && now - _reloadTimes.Peek() >= ReloadWindow) _reloadTimes.Dequeue();
            if (_reloadTimes.Count >= ReloadsPerWindow) return (source, ReloadResult.Busy);
            _reloadTimes.Enqueue(now);
            _reloads[key] = now;
            if (_reloads.Count > 200) foreach (var old in _reloads.Where(r => now - r.Value >= ReloadGap).Select(r => r.Key).ToList()) _reloads.Remove(old);
        }
        // One reload runs, one may wait (a bounded time); every further press is turned away at once.
        if (!await _gate.WaitAsync(0, ct))
        {
            if (Interlocked.Increment(ref _reloadsWaiting) > 1)
            {
                Interlocked.Decrement(ref _reloadsWaiting);
                return (source, ReloadResult.Busy);
            }
            try
            {
                if (!await _gate.WaitAsync(ReloadWait, ct)) return (source, ReloadResult.Busy);
            }
            finally
            {
                Interlocked.Decrement(ref _reloadsWaiting);
            }
        }
        try
        {
            var before = MissingCount(source);
            var item = WithSteamAssets(source, _steamAssets.Peek);
            try
            {
                if (await StoreAppIdAsync(item, ct) is { } appId) await _steamAssets.RefreshAsync(appId, ct);
                item = WithSteamAssets(source, _steamAssets.Peek);
                var art = item.Type == ItemType.Game || StoreMatches.IsAppId(StoreMatchOf(item.Id)) ? await FromSteamStore(item, ct) : null;
                if (art is null && _apiKey.Length > 0) art = await FromSteamGridDb(item, ct);
                if (art is not null)
                {
                    lock (_cacheLock) _cache[key] = new CacheEntry(art, now);
                    item = WithArt(item, art);
                }
                item = KeepCached(item, key);
                item = await FillSquareAsync(item, key, ct, force: true);
                foreach (var url in item.Art.Cover.Concat(item.Art.Hero).Concat(item.Art.Logo).Concat(item.Art.Square).Where(ArtworkSources.IsAllowed)) Images.ClearFailed(url);
                var arrived = await DownloadMissingAsync([item.Art.Cover, item.Art.Hero, item.Art.Logo, item.Art.Square], null, ct);
                SetRecheck(key, new Recheck(now, 0));
                SaveCache();
                var found = arrived > 0 || MissingCount(item) < before || !SameArt(item.Art, source.Art);
                return (item, found ? ReloadResult.Found : ReloadResult.Nothing);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                Console.Error.WriteLine($"[artwork] reload {source.Name}: {ex.Message}");
                return (source, ReloadResult.Failed);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A look that found nothing new keeps what an earlier look found (empty slots only).</summary>
    private LibraryItem KeepCached(LibraryItem item, string key) =>
        CachedLookup(key) is { Art: { } old } ? WithArt(item, old) : item;

    private int MissingCount(LibraryItem item) =>
        new[] { item.Art.Cover, item.Art.Hero, item.Art.Logo, item.Art.Square }.Count(SlotMissing);

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
        Images?.Prune(inUse.SelectMany(i => i.Art.Cover.Concat(i.Art.Hero).Concat(i.Art.Logo).Concat(i.Art.Square).Concat(i.Art.Icon))
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
        // Over the pass's budget: null (not asked yet), asked in a later pass.
        var square = TakeSquareBudget() ? await SquareUrlsAsync($"game/{id}", ct) : null;
        if (cover is null && hero is null && logo is null && (square is null || square.Count == 0)) return null;
        return new CachedArt(cover is null ? [] : [cover], hero is null ? [] : [hero], logo is null ? [] : [logo], square);
    }

    /// <summary>
    /// The first square image of SteamGridDB for a route ("game/&lt;id&gt;" or "steam/&lt;appid&gt;"): square grids (512 and 1024) first, icons if there are none.
    /// Only addresses on an allowed host count; the list has at most one address.
    /// </summary>
    private async Task<List<string>> SquareUrlsAsync(string route, CancellationToken ct)
    {
        var grid = await FirstSquareUrl($"{SteamGridDb}/grids/{route}?dimensions={SquareDimensions}", ct);
        if (grid is not null) return [grid];
        var icon = await FirstSquareUrl($"{SteamGridDb}/icons/{route}", ct);
        return icon is not null ? [icon] : [];
    }

    /// <summary>The first answer entry on an allowed host that is not an .ico file (those are no good as a tile picture); null if none.</summary>
    private async Task<string?> FirstSquareUrl(string url, CancellationToken ct)
    {
        try
        {
            using var doc = await GetJson(url, ct, auth: true);
            return ParseSteamGridDb(doc.RootElement).Select(c => c.Url).FirstOrDefault(ArtworkSources.IsAllowed);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Fills an empty square slot from SteamGridDB (needs the key and the artwork switch). Without force only where an earlier lookup never asked for it
    /// (cache from before the slot existed); the answer, also "none", is kept in the cache entry. A failed lookup (offline, rate limit) leaves everything as it was.
    /// </summary>
    private async Task<LibraryItem> FillSquareAsync(LibraryItem item, string key, CancellationToken ct, bool force = false)
    {
        if (!Online || _apiKey.Length == 0 || item.Art.Square.Count > 0) return item;
        var cached = CachedLookup(key);
        if (!force && (cached?.Art is not { Square: null } || !TakeSquareBudget())) return item;
        string route;
        if (SteamStore.AppIdOf(item) is { } steamId) route = $"steam/{steamId}";
        else if (StoreMatchOf(item.Id) is { } match && StoreMatches.IsAppId(match)) route = $"steam/{match}";
        else
        {
            JsonElement? found = null;
            try
            {
                using var search = await GetJson($"{SteamGridDb}/search/autocomplete/{Uri.EscapeDataString(item.Name)}", ct, auth: true);
                found = FirstInData(search.RootElement, "id")?.Clone();
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
            }
            // SteamGridDB does not know the game: that is an answer ("none"), kept so it is not asked on every pass. Errors above stay unanswered.
            if (found is not { } id)
            {
                SetSquare(key, []);
                return item;
            }
            route = $"game/{id.GetRawText()}";
        }
        var square = await SquareUrlsAsync(route, ct);
        SetSquare(key, square);
        return square.Count == 0 ? item : item with { Art = item.Art with { Square = square } };
    }

    private void SetSquare(string key, List<string> square)
    {
        lock (_cacheLock)
        {
            if (_cache.GetValueOrDefault(key) is { Art: { } art } entry) _cache[key] = entry with { Art = art with { Square = square } };
        }
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
            Logo = item.Art.Logo.Count > 0 ? item.Art.Logo : art.Logo ?? new List<string>(),
            Square = item.Art.Square.Count > 0 ? item.Art.Square : art.Square ?? new List<string>()
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

    /// <summary>The on-disk form of the re-check list: the entries and the day's budget already used.</summary>
    internal sealed record RecheckFile(Dictionary<string, Recheck?>? Entries, string? Day, int Today);

    /// <summary>
    /// Reads the re-check list; whatever is damaged is dropped, never trusted: no null or odd entries, no time in the future,
    /// a bounded number of tries and counter, and a day that does not parse starts the day's budget again.
    /// </summary>
    private Dictionary<string, Recheck> LoadRechecks(string file)
    {
        var result = new Dictionary<string, Recheck>();
        try
        {
            var data = JsonSerializer.Deserialize<RecheckFile>(File.ReadAllText(file));
            if (data is null) return result;
            var now = DateTimeOffset.UtcNow;
            foreach (var (key, state) in data.Entries ?? [])
            {
                if (string.IsNullOrEmpty(key) || key.Length > 512 || state is null) continue;
                var last = state.Last > now ? now : state.Last;
                result[key] = new Recheck(last, Math.Clamp(state.Tries, 0, 100));
                if (result.Count >= MaxRechecks) break;
            }
            if (DateOnly.TryParse(data.Day, System.Globalization.CultureInfo.InvariantCulture, out var day))
            {
                _recheckDay = day;
                _rechecksToday = Math.Clamp(data.Today, 0, 100_000);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            result.Clear();
        }
        return result;
    }

    private void SaveCache()
    {
        try
        {
            string checks;
            lock (_cacheLock)
            {
                // Bounded: the oldest looks are forgotten (those entries start their day again).
                if (_rechecks.Count > MaxRechecks)
                    foreach (var old in _rechecks.OrderBy(r => r.Value.Last).Take(_rechecks.Count - MaxRechecks).Select(r => r.Key).ToList()) _rechecks.Remove(old);
                checks = JsonSerializer.Serialize(new RecheckFile(
                    _rechecks.ToDictionary(r => r.Key, r => (Recheck?)r.Value), _recheckDay.ToString("O", System.Globalization.CultureInfo.InvariantCulture), _rechecksToday));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_recheckFile))!);
            File.WriteAllText(_recheckFile + ".tmp", checks);
            File.Move(_recheckFile + ".tmp", _recheckFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[artwork] could not write re-check list: {ex.Message}");
        }
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
