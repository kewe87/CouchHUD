using System.Text.Json;

namespace CouchHud.Core.Library;

/// <summary>Public Steam store endpoints, no API key needed.</summary>
public static class SteamStore
{
    /// <summary>The store search answers in a few KB; more is not a search result.</summary>
    internal const int MaxSearchBytes = 512 * 1024;

    /// <summary>
    /// App id of the store entry that is the same game by name (see <see cref="NameMatch"/>), or null. Near matches are rejected on
    /// purpose: wrong data is worse than none. Several different entries with that name (a remake and the original) are no answer
    /// either, unless exactly one of them has the very name.
    /// </summary>
    public static async Task<string?> FindAppIdAsync(HttpClient http, string name, CancellationToken ct)
    {
        var hits = (await SearchAsync(http, name, ct)).Where(c => NameMatch.Same(c.Name, name)).DistinctBy(c => c.AppId).ToList();
        if (hits.Count <= 1) return hits.FirstOrDefault()?.AppId;
        var exact = hits.Where(c => NameMatch.Exact(c.Name, name)).ToList();
        return exact.Count == 1 ? exact[0].AppId : null;
    }

    /// <summary>
    /// The store's search: games only (no DLC, soundtracks, bundles), at most <see cref="StoreMatches.MaxResults"/>. The answer is read
    /// up to <see cref="MaxSearchBytes"/>; names are cut to the search's length and stripped of control characters.
    /// </summary>
    public static async Task<IReadOnlyList<StoreCandidate>> SearchAsync(HttpClient http, string query, CancellationToken ct)
    {
        var url = $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(query)}&l=english&cc=US";
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        using var doc = await ReadJsonAsync(response, MaxSearchBytes, ct);
        var result = new List<StoreCandidate>();
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return result;
        foreach (var candidate in items.EnumerateArray())
        {
            // DLC, soundtracks and bundles come back from the search too; only the game itself has the art.
            if (candidate.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() != "app") continue;
            if (!candidate.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number) continue;
            var appId = id.GetRawText();
            if (!StoreMatches.IsAppId(appId)) continue;
            var name = candidate.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? InfoOverrides.Clip(n.GetString(), StoreMatches.MaxQueryLength)
                : null;
            if (name is null) continue;
            result.Add(new StoreCandidate(appId, name));
            if (result.Count == StoreMatches.MaxResults) break;
        }
        return result;
    }

    /// <summary>Parses a JSON answer read up to <paramref name="maxBytes"/>; a longer one is refused instead of buffered.</summary>
    internal static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > maxBytes) throw new HttpRequestException($"answer too large ({response.Content.Headers.ContentLength} bytes)");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes) throw new HttpRequestException($"answer too large (over {maxBytes} bytes)");
            buffer.Write(chunk, 0, read);
        }
        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, cancellationToken: ct);
    }

    /// <summary>App id for library items that come from Steam, null otherwise.</summary>
    public static string? AppIdOf(Sdk.LibraryItem item) =>
        item.Id.StartsWith("steam:", StringComparison.Ordinal) ? item.Id["steam:".Length..] : null;
}

/// <summary>
/// Name to Steam app id, remembered on disk (hits 90 days, misses 7), so each name is searched
/// once however many languages, screens and restarts ask for it.
/// </summary>
public sealed class SteamAppIds
{
    private static readonly TimeSpan HitTtl = TimeSpan.FromDays(90);
    private static readonly TimeSpan MissTtl = TimeSpan.FromDays(7);

    private readonly HttpClient _http;
    private readonly string _file;
    private readonly Dictionary<string, Entry> _cache;
    private readonly object _lock = new();

    internal sealed record Entry(string? AppId, DateTimeOffset Checked);

    public SteamAppIds(HttpClient http, string file)
    {
        _http = http;
        _file = file;
        try
        {
            _cache = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(file)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _cache = new();
        }
    }

    /// <summary>The cached answer without asking: (known, appId). Expired entries count as unknown.</summary>
    public (bool Known, string? AppId) Peek(string name)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(NameMatch.Canonical(name), out var e)
                && DateTimeOffset.UtcNow - e.Checked < (e.AppId is null ? MissTtl : HitTtl))
            {
                return (true, e.AppId);
            }
        }
        return (false, null);
    }

    public async Task<string?> FindAsync(string name, CancellationToken ct)
    {
        // A name that cannot be compared (no Latin letters) is never searched: no answer could be taken anyway.
        if (NameMatch.Canonical(name).Length == 0) return null;
        if (Peek(name) is { Known: true } known) return known.AppId;
        var id = await SteamStore.FindAppIdAsync(_http, name, ct);
        lock (_lock)
        {
            _cache[NameMatch.Canonical(name)] = new Entry(id, DateTimeOffset.UtcNow);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!);
                File.WriteAllText(_file + ".tmp", JsonSerializer.Serialize(_cache));
                File.Move(_file + ".tmp", _file, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[steam] could not write id cache: {ex.Message}");
            }
        }
        return id;
    }
}
