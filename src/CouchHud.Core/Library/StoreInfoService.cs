using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using CouchHud.Core.Parental;
using CouchHud.Sdk;

namespace CouchHud.Core.Library;

public sealed record StoreInfo(
    string? Description,
    IReadOnlyList<string> Developers,
    IReadOnlyList<string> Publishers,
    string? ReleaseDate,
    IReadOnlyList<string> Genres)
{
    /// <summary>Trailer video URLs (mp4), best first; empty if the store has none.</summary>
    public IReadOnlyList<string> Trailers { get; init; } = [];

    /// <summary>Screenshot URLs in full size.</summary>
    public IReadOnlyList<string> Screenshots { get; init; } = [];

    /// <summary>
    /// The strictest age known over every rating system and Steam's own (an older cache: the USK, else Steam's required age); null when
    /// none is known or one rating was unreadable. What a fresh look-up is compared with (<see cref="StoreInfoService.RatingDropped"/>);
    /// the age limit goes by <see cref="CouchHud.Core.Parental.AgeRatings.Effective(StoreInfo, string)"/>.
    /// </summary>
    public int? RequiredAge { get; init; }

    /// <summary>Every rating system's age ("usk", "pegi", "esrb", "dejus", "cero"; -1 unreadable); null in caches of older versions.</summary>
    public IReadOnlyDictionary<string, int>? Ratings { get; init; }

    /// <summary>Steam's own required age (above 0), null when none.</summary>
    public int? SteamRequiredAge { get; init; }

    /// <summary>The age fields were read (entries cached by older versions lack them and are fetched again).</summary>
    public bool RatingKnown { get; init; }
}

/// <summary>
/// Description, developer, release date and genres from the Steam store, in
/// the active language (German or English) where available. Non-Steam entries are matched by exact name.
/// Results (including "not found") are cached on disk.
/// </summary>
public sealed partial class StoreInfoService
{
    private static readonly TimeSpan HitTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan MissTtl = TimeSpan.FromDays(7);

    private readonly HttpClient _http;
    private readonly string _cacheFile;
    private readonly Dictionary<string, CacheEntry> _cache;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="Source">The lookup it came from (<see cref="StoreLookup.Key"/>); null in caches of older versions, which count as current.</param>
    internal sealed record CacheEntry(StoreInfo? Info, DateTimeOffset Checked, string? Source = null);

    /// <summary>
    /// An answer read for the lookup the entry has now (not for a match the user changed since). An entry of an older cache (no source) was
    /// read for the automatic lookup (Steam's app id or the name): it never stands for a store entry the user picked, nor for "none".
    /// </summary>
    private static bool Current(CacheEntry entry, StoreLookup lookup, LibraryItem item) =>
        entry.Source is null
            ? lookup.AppId is null ? lookup.Name is not null : lookup.AppId == SteamStore.AppIdOf(item)
            : entry.Source == lookup.Key;

    /// <summary>Store details are a few hundred KB at most; more is not read.</summary>
    internal const int MaxDetailsBytes = 4 * 1024 * 1024;

    /// <summary>
    /// What an entry's look-up goes by; the host sets it (<see cref="LibraryService.StoreLookupOf"/>: the user's match, the scanned name).
    /// The default: Steam's app id, else the entry's name.
    /// </summary>
    public Func<LibraryItem, StoreLookup> Lookup { get; set; } = item =>
        SteamStore.AppIdOf(item) is { } id ? new StoreLookup(id, null) : new StoreLookup(null, item.Name);

    /// <summary>
    /// An automatic match by name lost a known rating on a fresh look-up: no hit any more (a stricter match, an ambiguous name), a hit without
    /// a rating, or a milder one. The host keeps the last known rating as the entry's age floor until the parents confirm
    /// (<see cref="LibraryService.KeepAgeFloor"/>): nothing on the store's side makes a game startable that was not before.
    /// </summary>
    public Action<LibraryItem, int>? RatingDropped { get; set; }

    /// <summary>The strictest rating an earlier automatic look-up by name found for the entry (either language), null when none was known.</summary>
    private int? KnownAutomaticAge(string itemId)
    {
        int? age = null;
        foreach (var lang in new[] { "de", "en" })
            if (_cache.TryGetValue(KeyOf(itemId, lang), out var e) && (e.Source is null || e.Source.StartsWith("name:", StringComparison.Ordinal))
                && e.Info is { RatingKnown: true } known)
                age = Math.Max(age ?? 0, known.RequiredAge ?? 0);
        return age;
    }

    private readonly Func<string> _language;
    private readonly SteamAppIds _ids;

    /// <param name="language">"de" or "en" (the store texts come in that language); German when not given.</param>
    public StoreInfoService(HttpClient http, string cacheFile, Func<string>? language = null, SteamAppIds? ids = null)
    {
        _language = language ?? (() => "de");
        _http = http;
        _ids = ids ?? new SteamAppIds(http, Path.ChangeExtension(cacheFile, ".steamids.json"));
        _cacheFile = cacheFile;
        _cache = LoadCache(cacheFile);
    }

    /// <summary>German entries keep the plain id (older caches), English ones get a suffix: each language has its own cache.</summary>
    internal static string KeyOf(string itemId, string language) => language == "en" ? itemId + "@en" : itemId;

    /// <summary>What is cached already, without asking the store (null when unknown). Falls back to the other language (good enough for ratings).</summary>
    public StoreInfo? Cached(LibraryItem item)
    {
        try
        {
            var lang = _language();
            var lookup = Lookup(item);
            if (_cache.TryGetValue(KeyOf(item.Id, lang), out var e) && e.Info is not null && Current(e, lookup, item)) return e.Info;
            return _cache.TryGetValue(KeyOf(item.Id, lang == "en" ? "de" : "en"), out var other) && Current(other, lookup, item) ? other.Info : null;
        }
        catch (InvalidOperationException)
        {
            return null; // a write is in progress
        }
    }

    /// <summary>
    /// The store answered for the entry's lookup as it is now, in the active language (a hit or a "not found"). False after a look-up that
    /// failed (offline, the store's rate limit, a timeout): then "no description" is not the store's answer yet, and the page asks again.
    /// </summary>
    public bool HasAnswer(LibraryItem item)
    {
        try
        {
            return _cache.TryGetValue(KeyOf(item.Id, _language()), out var e) && Current(e, Lookup(item), item);
        }
        catch (InvalidOperationException)
        {
            return false; // a write is in progress
        }
    }

    public async Task<StoreInfo?> GetAsync(LibraryItem item, CancellationToken ct = default)
    {
        var language = _language();
        var key = KeyOf(item.Id, language);
        await _gate.WaitAsync(ct);
        try
        {
            // The lookup as it is now, behind the gate: a call that waited while the user changed the match asks for the new entry, and never
            // writes an answer for the old one over the new one's.
            var lookup = Lookup(item);
            if (_cache.TryGetValue(key, out var cached) && !Current(cached, lookup, item)) cached = null;
            if (cached is not null
                && (cached.Info is null || cached.Info.RatingKnown)
                && DateTimeOffset.UtcNow - cached.Checked < (cached.Info is null ? MissTtl : HitTtl))
            {
                return cached.Info;
            }

            StoreInfo? info;
            try
            {
                var appId = lookup.AppId ?? (lookup.Name is { } name ? await _ids.FindAsync(name, ct) : null);
                info = appId is null ? null : await FetchDetails(appId, language, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                // Offline: show what we have, try again next time.
                Console.Error.WriteLine($"[store] {item.Name}: {ex.Message}");
                return cached?.Info;
            }

            // An automatic match that loses a known rating: the old one stays as the floor (before the cache forgets it).
            if (lookup.AppId is null && lookup.Name is not null && KnownAutomaticAge(item.Id) is { } before and > 0
                && (info is not { RatingKnown: true } now || (now.RequiredAge ?? 0) < before))
            {
                try
                {
                    RatingDropped?.Invoke(item, before);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException)
                {
                    // Not kept: the answer is not taken either, the old one stays cached and counts.
                    Console.Error.WriteLine($"[store] {item.Name}: rating floor not kept: {ex.Message}");
                    return cached?.Info;
                }
            }
            _cache[key] = new CacheEntry(info, DateTimeOffset.UtcNow, lookup.Key);
            SaveCache();
            return info;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<StoreInfo?> FetchDetails(string appId, string language, CancellationToken ct)
    {
        var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&{(language == "en" ? "l=english&cc=US" : "l=german&cc=DE")}";
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        using var doc = await SteamStore.ReadJsonAsync(response, MaxDetailsBytes, ct);
        return Parse(doc.RootElement, appId);
    }

    /// <summary>The store's search for the edit menu ("choose a store entry"): the same endpoint and client as the automatic match.</summary>
    public Task<IReadOnlyList<StoreCandidate>> SearchAsync(string query, CancellationToken ct = default) =>
        SteamStore.SearchAsync(_http, StoreMatches.CheckQuery(query), ct);

    internal static StoreInfo? Parse(JsonElement root, string appId)
    {
        if (!root.TryGetProperty(appId, out var entry)
            || !entry.TryGetProperty("success", out var ok) || ok.ValueKind != JsonValueKind.True
            || !entry.TryGetProperty("data", out var data))
        {
            return null;
        }

        string? Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        IReadOnlyList<string> Strings(string name) =>
            data.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
                ? arr.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList()
                : [];

        var genres = data.TryGetProperty("genres", out var g) && g.ValueKind == JsonValueKind.Array
            ? g.EnumerateArray().Select(x => Str(x, "description")).OfType<string>().ToList()
            : [];
        var release = data.TryGetProperty("release_date", out var r) ? Str(r, "date") : null;

        var ratings = AgeRatings.Parse(data);
        var steamAge = AgeRatings.ParseRequiredAge(data);
        return new StoreInfo(
            CleanText(Str(data, "short_description")),
            Strings("developers"),
            Strings("publishers"),
            string.IsNullOrWhiteSpace(release) ? null : release,
            genres)
        {
            RatingKnown = true,
            RequiredAge = AgeRatings.StrictestAge(ratings, steamAge),
            Ratings = ratings,
            SteamRequiredAge = steamAge,
            Trailers = ParseTrailers(data),
            Screenshots = data.TryGetProperty("screenshots", out var shots) && shots.ValueKind == JsonValueKind.Array
                ? shots.EnumerateArray().Select(x => Str(x, "path_full")).OfType<string>().Take(8).ToList()
                : []
        };
    }

    /// <summary>One mp4 per trailer, preferring the highest quality the store offers.</summary>
    private static List<string> ParseTrailers(JsonElement data)
    {
        var result = new List<string>();
        if (!data.TryGetProperty("movies", out var movies) || movies.ValueKind != JsonValueKind.Array) return result;
        foreach (var movie in movies.EnumerateArray())
        {
            if (!movie.TryGetProperty("mp4", out var mp4) || mp4.ValueKind != JsonValueKind.Object) continue;
            foreach (var quality in new[] { "max", "480" })
            {
                if (mp4.TryGetProperty(quality, out var url) && url.ValueKind == JsonValueKind.String && url.GetString() is { Length: > 0 } u)
                {
                    result.Add(u.Replace("http://", "https://", StringComparison.Ordinal));
                    break;
                }
            }
            if (result.Count == 3) break;
        }
        return result;
    }

    /// <summary>Store texts contain HTML entities and the odd tag.</summary>
    internal static string? CleanText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var text = WebUtility.HtmlDecode(Tags().Replace(html, " "));
        return Whitespace().Replace(text, " ").Trim();
    }

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
            File.WriteAllText(_cacheFile + ".tmp", JsonSerializer.Serialize(_cache));
            File.Move(_cacheFile + ".tmp", _cacheFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[store] could not write cache: {ex.Message}");
        }
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
