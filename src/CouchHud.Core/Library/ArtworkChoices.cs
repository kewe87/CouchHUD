using System.Text.Json;
using CouchHud.Sdk;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Library;

/// <summary>One selectable image. Thumb is what the picker shows; Url is what gets stored.</summary>
public sealed record ArtChoice(string Url, string Thumb, string Source)
{
    /// <summary>This is the image the entry shows now (decided by the original address, whatever the UI makes of it).</summary>
    public bool Current { get; init; }
}

/// <summary>All images the lookups know for one entry, per slot.</summary>
public sealed record ArtworkChoices(
    IReadOnlyList<ArtChoice> Cover,
    IReadOnlyList<ArtChoice> Hero,
    IReadOnlyList<ArtChoice> Logo)
{
    /// <summary>Whether a SteamGridDB key is set; without one the picker points to it.</summary>
    public bool HasKey { get; init; }

    /// <summary>Why something is missing (invalid key, offline), for the picker to show.</summary>
    public string? Note { get; init; }
}

public sealed partial class ArtworkResolver
{
    private const int MaxChoices = 24;

    /// <summary>Every candidate from SteamGridDB (with key) and the Steam CDN, for the "change artwork" picker.</summary>
    public async Task<ArtworkChoices> GetChoicesAsync(LibraryItem item, CancellationToken ct = default)
    {
        List<ArtChoice> cover = [], hero = [], logo = [];
        string? note = null;

        if (!Online)
        {
            AddCurrent(cover, item.Art.Cover);
            AddCurrent(hero, item.Art.Hero);
            AddCurrent(logo, item.Art.Logo);
            return new ArtworkChoices(MarkCurrent(cover, item.Art.Cover), MarkCurrent(hero, item.Art.Hero), MarkCurrent(logo, item.Art.Logo)) { HasKey = _apiKey.Length > 0, Note = Strings.Get("artwork.onlineOff") };
        }

        if (_apiKey.Length > 0)
        {
            try
            {
                await CollectSteamGridDb(item, cover, hero, logo, ct);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                note = Strings.Get("artwork.sgdbKeyRejected");
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
            {
                note = Strings.Get("artwork.sgdbDown");
            }
        }

        try
        {
            var appId = await StoreAppIdAsync(item, ct);
            if (appId is not null) AddSteamVariants(appId, cover, hero, logo, await _steamAssets.GetAsync(appId, ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            note ??= Strings.Get("artwork.steamDown");
        }

        // What the entry shows right now (a scanner's local file, say) is a choice too.
        AddCurrent(cover, item.Art.Cover);
        AddCurrent(hero, item.Art.Hero);
        AddCurrent(logo, item.Art.Logo);

        return new ArtworkChoices(MarkCurrent(Distinct(cover), item.Art.Cover), MarkCurrent(Distinct(hero), item.Art.Hero), MarkCurrent(Distinct(logo), item.Art.Logo))
        {
            HasKey = _apiKey.Length > 0,
            Note = note
        };
    }

    /// <summary>The fixed image names the Steam CDN serves for an app id.</summary>
    internal static void AddSteamVariants(string appId, List<ArtChoice> cover, List<ArtChoice> hero, List<ArtChoice> logo, SteamArt? listed = null)
    {
        static ArtChoice Listed(string url) => new(url, url, "Steam");
        // The store's own list (hashed addresses) is what newer apps have; the fixed names only work for older ones.
        if (listed is not null)
        {
            cover.AddRange(listed.Cover.Select(Listed));
            hero.AddRange(listed.Hero.Select(Listed));
            logo.AddRange(listed.Logo.Select(Listed));
            return;
        }
        ArtChoice Steam(string file) => new($"{SteamScanner.Cdn}/{appId}/{file}", $"{SteamScanner.Cdn}/{appId}/{file}", "Steam");
        cover.AddRange([Steam("library_600x900.jpg"), Steam("header.jpg"), Steam("capsule_616x353.jpg")]);
        hero.AddRange([Steam("library_hero.jpg"), Steam("header.jpg")]);
        logo.Add(Steam("logo.png"));
    }

    /// <summary>Reads SteamGridDB's { data: [ { url, thumb } ] } answer.</summary>
    internal static List<ArtChoice> ParseSteamGridDb(JsonElement root)
    {
        var list = new List<ArtChoice>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return list;
        foreach (var entry in data.EnumerateArray())
        {
            var url = entry.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            if (string.IsNullOrEmpty(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;
            var thumb = entry.TryGetProperty("thumb", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() is { Length: > 0 } th ? th : url;
            list.Add(new ArtChoice(url, thumb, "SteamGridDB"));
            if (list.Count >= MaxChoices) break;
        }
        return list;
    }

    private async Task CollectSteamGridDb(LibraryItem item, List<ArtChoice> cover, List<ArtChoice> hero, List<ArtChoice> logo, CancellationToken ct)
    {
        string route;
        if (SteamStore.AppIdOf(item) is { } appId) route = $"steam/{appId}";
        else
        {
            using var search = await GetJson($"{SteamGridDb}/search/autocomplete/{Uri.EscapeDataString(item.Name)}", ct, auth: true);
            if (FirstInData(search.RootElement, "id") is not { } id) return;
            route = $"game/{id.GetRawText()}";
        }

        var dims = item.Type == ItemType.App ? "920x430,460x215" : "600x900,342x482,660x930";
        cover.AddRange(await ListChoices($"{SteamGridDb}/grids/{route}?dimensions={dims}", ct));
        hero.AddRange(await ListChoices($"{SteamGridDb}/heroes/{route}", ct));
        logo.AddRange(await ListChoices($"{SteamGridDb}/logos/{route}", ct));
    }

    private async Task<List<ArtChoice>> ListChoices(string url, CancellationToken ct)
    {
        try
        {
            using var doc = await GetJson(url, ct, auth: true);
            return ParseSteamGridDb(doc.RootElement);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return [];
        }
    }

    private static void AddCurrent(List<ArtChoice> target, IReadOnlyList<string> current)
    {
        if (current.Count > 0) target.Insert(0, new ArtChoice(current[0], current[0], "Aktuell"));
    }

    private static List<ArtChoice> MarkCurrent(List<ArtChoice> list, IReadOnlyList<string> current) =>
        list.Select(c => current.Count > 0 && c.Url == current[0] ? c with { Current = true } : c).ToList();

    private static List<ArtChoice> Distinct(List<ArtChoice> list) => list.DistinctBy(c => c.Url).ToList();
}
