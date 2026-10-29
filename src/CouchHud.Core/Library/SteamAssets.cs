using System.Text.Json;
using System.Text.RegularExpressions;

namespace CouchHud.Core.Library;

/// <summary>Image addresses of one Steam app, best first per slot, as the store lists them.</summary>
public sealed record SteamArt(List<string> Cover, List<string> Hero, List<string> Logo)
{
    public bool IsEmpty => Cover.Count == 0 && Hero.Count == 0 && Logo.Count == 0;
}

/// <summary>
/// Newer Steam apps only have their images under hashed paths (.../apps/id/hash/library_capsule.jpg); the old
/// fixed names answer 404. The store's own GetItems service lists the real addresses, for many apps per request,
/// no key needed. Answers are kept on disk (hits 30 days, apps without assets 7), so each app is asked once.
/// </summary>
public sealed partial class SteamAssets
{
    public const int BatchSize = 50;
    public const string Endpoint = "https://api.steampowered.com/IStoreBrowseService/GetItems/v1";
    private const string AssetBase = "https://shared.steamstatic.com/store_item_assets/";
    private static readonly TimeSpan HitTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan MissTtl = TimeSpan.FromDays(7);

    private readonly HttpClient _http;
    private readonly string _file;
    private readonly Dictionary<string, Entry> _cache;
    private readonly object _lock = new();
    private readonly Func<DateTimeOffset> _now;

    internal sealed record Entry(SteamArt? Art, DateTimeOffset Checked);

    public SteamAssets(HttpClient http, string file, Func<DateTimeOffset>? now = null)
    {
        _http = http;
        _file = file;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        try
        {
            _cache = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(file)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _cache = new();
        }
    }

    /// <summary>True for the old fixed-name addresses (.../apps/id/file, no hash folder), which newer apps don't have.</summary>
    public static bool IsLegacyUrl(string url) => LegacyUrl().IsMatch(url);

    [GeneratedRegex(@"/store_item_assets/steam/apps/\d+/[^/?#]+(\?.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex LegacyUrl();

    /// <summary>The cached answer without asking: (known, art). Expired entries count as unknown.</summary>
    public (bool Known, SteamArt? Art) Peek(string appId)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(appId, out var e) && _now() - e.Checked < (e.Art is null ? MissTtl : HitTtl)) return (true, e.Art);
        }
        return (false, null);
    }

    /// <summary>Asks for every app id not known yet, 50 per request, and remembers the answers.</summary>
    public async Task EnsureAsync(IEnumerable<string> appIds, CancellationToken ct)
    {
        var todo = appIds.Where(id => id.Length > 0 && id.All(char.IsAsciiDigit)).Distinct().Where(id => !Peek(id).Known).ToList();
        for (var i = 0; i < todo.Count; i += BatchSize)
        {
            var batch = todo.Skip(i).Take(BatchSize).ToList();
            var found = await FetchAsync(batch, ct);
            lock (_lock)
            {
                foreach (var id in batch) _cache[id] = new Entry(found.GetValueOrDefault(id), _now());
            }
            Save();
        }
    }

    /// <summary>
    /// Asks for one app again whatever the cache says (the hashed addresses change with art updates). An app the store
    /// now lists no assets for keeps what it had; one that never had any stays a recorded miss.
    /// </summary>
    public async Task RefreshAsync(string appId, CancellationToken ct)
    {
        if (appId.Length == 0 || !appId.All(char.IsAsciiDigit)) return;
        Dictionary<string, SteamArt> found;
        try
        {
            found = await FetchAsync([appId], ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.BadRequest)
        {
            return;
        }
        lock (_lock)
        {
            if (found.TryGetValue(appId, out var art)) _cache[appId] = new Entry(art, _now());
            else if (!_cache.TryGetValue(appId, out var old) || old.Art is null) _cache[appId] = new Entry(null, _now());
        }
        Save();
    }

    /// <summary>The art of one app: from the cache, or asked for.</summary>
    public async Task<SteamArt?> GetAsync(string appId, CancellationToken ct)
    {
        try
        {
            await EnsureAsync([appId], ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.BadRequest)
        {
            // The service itself is not there (not an outage worth retrying): the old fixed names are all that's left.
            return null;
        }
        return Peek(appId).Art;
    }

    private async Task<Dictionary<string, SteamArt>> FetchAsync(List<string> batch, CancellationToken ct)
    {
        var input = JsonSerializer.Serialize(new
        {
            ids = batch.Select(id => new { appid = long.Parse(id) }),
            context = new { language = "english", country_code = "US" },
            data_request = new { include_assets = true }
        });
        using var response = await _http.GetAsync($"{Endpoint}?input_json={Uri.EscapeDataString(input)}", ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return Parse(doc.RootElement);
    }

    /// <summary>Reads { response: { store_items: [ { appid, assets: { asset_url_format, library_capsule, ... } } ] } }.</summary>
    internal static Dictionary<string, SteamArt> Parse(JsonElement root)
    {
        var result = new Dictionary<string, SteamArt>();
        if (!root.TryGetProperty("response", out var response) || !response.TryGetProperty("store_items", out var items)
            || items.ValueKind != JsonValueKind.Array) return result;

        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("appid", out var idElement) || !idElement.TryGetInt64(out var id)) continue;
            if (!item.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Object) continue;
            var format = Str(assets, "asset_url_format");
            if (format is null || !format.Contains("${FILENAME}", StringComparison.Ordinal)) continue;

            string? Url(string name) => Str(assets, name) is { } file ? AssetBase + format.Replace("${FILENAME}", file) : null;
            List<string> Of(params string[] names) => names.Select(Url).Where(u => u is not null).Select(u => u!).Distinct().ToList();

            var art = new SteamArt(
                Of("library_capsule_2x", "library_capsule", "header"),
                Of("library_hero_2x", "library_hero", "header"),
                Of("library_logo_2x", "library_logo"));
            if (!art.IsEmpty) result[id.ToString()] = art;
        }
        return result;
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s && !s.Contains("..", StringComparison.Ordinal) ? s : null;

    private void Save()
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!);
                File.WriteAllText(_file + ".tmp", JsonSerializer.Serialize(_cache));
                File.Move(_file + ".tmp", _file, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[steam] could not write asset cache: {ex.Message}");
            }
        }
    }
}
