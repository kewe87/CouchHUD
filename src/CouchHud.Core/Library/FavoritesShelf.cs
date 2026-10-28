using CouchHud.Core.Config;
using CouchHud.Sdk;

namespace CouchHud.Core.Library;

/// <summary>One tile of the Favorites shelf: a library entry (Id) or a website (Url).</summary>
public sealed record ShelfEntry(string Kind, string Id, string Name, string? Url, bool Favorite);

/// <summary>
/// The rules of the Favorites tab (formerly Home): which entries show and in which order. Pure (no store, no UI), so the order, the
/// three modes, the switched off areas and the child's filter are tested directly. The host passes what a person may see: the shelf
/// never invents a right of its own, it only narrows down what the library already shows.
/// </summary>
public static class FavoritesShelf
{
    public const string ModeFavorites = "favorites";
    public const string ModeRecent = "recent";
    public const string ModeCombined = "combined";

    /// <summary>Favorites kept per person (library entries and websites together).</summary>
    public const int MaxFavorites = 200;

    /// <summary>"Recently used" entries shown (apart from the favorites).</summary>
    public const int MaxRecent = 24;

    /// <summary>Prefix of a favorite website in the favorites list (the rest is the bookmark's address).</summary>
    public const string SitePrefix = "site:";

    private const int MaxIdLength = 4200;

    /// <summary>A known mode; anything else (hand-edited file, older config) is the default, the combined view.</summary>
    public static string NormalizeMode(string? mode) => mode switch
    {
        ModeFavorites => ModeFavorites,
        ModeRecent => ModeRecent,
        _ => ModeCombined
    };

    public static string SiteId(string url) => SitePrefix + url;

    public static bool IsSiteId(string? id) => id is not null && id.StartsWith(SitePrefix, StringComparison.Ordinal);

    /// <summary>The address of a favorite website id, or null when the id is no website or the address is no web address.</summary>
    public static string? SiteUrl(string? id) =>
        IsSiteId(id) && WebLinks.TryWebUrl(id![SitePrefix.Length..], out _) ? id![SitePrefix.Length..].Trim() : null;

    /// <summary>
    /// A favorites list as it may be kept: no empty, over-long or duplicate ids, no website id with a non-web address. Order is kept.
    /// Never shortens a valid list: the <see cref="MaxFavorites"/> bound applies when adding (<see cref="TryAdd"/>), not when loading.
    /// </summary>
    public static List<string> Clean(IEnumerable<string>? favorites)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var id in favorites ?? [])
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > MaxIdLength || id.Any(char.IsControl)) continue;
            if (IsSiteId(id) && SiteUrl(id) is null) continue;
            if (!seen.Add(id)) continue;
            result.Add(id);
        }
        return result;
    }

    /// <summary>Adds an id as a favorite; false when the list is full (the caller tells the person).</summary>
    public static bool TryAdd(List<string> favorites, string id)
    {
        if (favorites.Contains(id)) return true;
        if (favorites.Count >= MaxFavorites) return false;
        favorites.Add(id);
        return true;
    }

    /// <summary>
    /// The entries of the shelf in order. Favorites come in the order they were marked; "recent" are the entries used last, newest
    /// first. <paramref name="allowed"/> is the host's last word on one entry (a child's age limit, locked entries): an entry it
    /// refuses never shows, favorite or not. Websites show only with <paramref name="sites"/> and the browser area on.
    /// </summary>
    public static List<ShelfEntry> Build(
        IEnumerable<LibraryItem> library, IReadOnlyCollection<string> favorites, IEnumerable<Bookmark> bookmarks,
        string mode, bool sites, AreaSet areas, Func<LibraryItem, bool> allowed, int recentLimit = MaxRecent)
    {
        mode = NormalizeMode(mode);
        var shown = library.Where(i => !i.Hidden && areas.Shows(i) && allowed(i)).ToList();
        var byId = shown.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var result = new List<ShelfEntry>();
        var taken = new HashSet<string>(StringComparer.Ordinal);

        if (mode != ModeRecent)
        {
            foreach (var id in favorites)
            {
                if (byId.TryGetValue(id, out var item) && taken.Add(id)) result.Add(Of(item, favorite: true));
            }
            if (sites && areas.Browser)
            {
                var list = bookmarks.ToList();
                foreach (var id in favorites)
                {
                    var url = SiteUrl(id);
                    if (url is null || !taken.Add(id)) continue;
                    var mark = list.FirstOrDefault(b => b.Url == url);
                    if (mark is null) { taken.Remove(id); continue; }
                    result.Add(new ShelfEntry("site", id, string.IsNullOrWhiteSpace(mark.Name) ? url : mark.Name, url, true));
                }
            }
        }
        if (mode != ModeFavorites)
        {
            var favs = favorites.ToHashSet(StringComparer.Ordinal);
            var recent = shown.Where(i => i.LastPlayed > 0 && (mode == ModeRecent || !favs.Contains(i.Id)))
                .OrderByDescending(i => i.LastPlayed).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .Take(recentLimit);
            foreach (var item in recent)
            {
                if (taken.Add(item.Id)) result.Add(Of(item, favs.Contains(item.Id)));
            }
        }
        return result;
    }

    /// <summary>
    /// Whether the person on the couch may see this entry in the shelf: with parental controls on, not what needs the parents' code
    /// because it is on their list or over the age limit (an unknown age counts as over). The time rules and "cannot be tracked" do not
    /// hide an entry; the gate before a start still applies to everything. <paramref name="age"/> is the cached verdict age, never a lookup.
    /// </summary>
    public static bool ChildAllows(Parental.ParentalConfig rules, LibraryItem item, IEnumerable<LibraryItem> all, int? age) =>
        Parental.ParentalService.CodeReason(rules, item, all, age) is not ("locked" or "age" or "unknown-age");

    private static ShelfEntry Of(LibraryItem item, bool favorite) => new("item", item.Id, item.Name, null, favorite);
}
