using System.Globalization;
using CouchHud.Core.Config;
using CouchHud.Core.Localization;
using CouchHud.Sdk;

namespace CouchHud.Core.Library;

/// <summary>What the store look-up of an entry goes by: a Steam app id, a name to search for, or nothing (both null).</summary>
public readonly record struct StoreLookup(string? AppId, string? Name)
{
    /// <summary>Stored with a cached answer: an answer for another lookup (the match was changed) is not used.</summary>
    public string Key => AppId is { } a ? "app:" + a : Name is { } n ? "name:" + NameMatch.Canonical(n) : "none";
}

/// <summary>One hit of the store search for the edit menu.</summary>
public sealed record StoreCandidate(string AppId, string Name);

/// <summary>What the edit menu shows about an entry's store match and texts.</summary>
/// <param name="Mode">"steam" (Steam's own entry, fixed), "auto" (by the scanned name), "manual" (an app id the user picked), "none".</param>
/// <param name="AgeFloor">The stricter rating of an earlier match that still counts until the parents confirm the change.</param>
public sealed record StoreMatchView(string Mode, string? AppId, string? Name, int? AgeFloor);

/// <summary>
/// The store entry an entry is matched to. Steam's own games always go by their app id; every other entry by the name it was scanned
/// with (never the name the user gave it: renaming must not change the age rating), unless the user picked an entry or "none".
/// </summary>
public static class StoreMatches
{
    public const string None = "none";
    public const int MaxQueryLength = 100;
    public const int MaxResults = 10;

    /// <summary>A Steam app id as the store writes it: digits, no leading zero, at most 10.</summary>
    public static bool IsAppId(string? text) =>
        text is { Length: > 0 and <= 10 } && text[0] != '0' && text.All(char.IsAsciiDigit) && long.Parse(text, CultureInfo.InvariantCulture) <= uint.MaxValue;

    /// <summary>Steam's own entries have their app id; nothing to choose there.</summary>
    public static bool CanChoose(LibraryItem item) => SteamStore.AppIdOf(item) is null;

    /// <summary>A match from the bridge: null (automatic), "none" or an app id; anything else is refused.</summary>
    public static string? Validate(string? match)
    {
        if (match is null) return null;
        var text = match.Trim();
        if (text == None || IsAppId(text)) return text;
        throw new ArgumentException(Strings.Get("store.matchInvalid"));
    }

    /// <summary>A search the user typed: trimmed, at most <see cref="MaxQueryLength"/> characters, no control characters.</summary>
    public static string CheckQuery(string? query)
    {
        var text = query?.Trim() ?? "";
        if (text.Length == 0 || text.Length > MaxQueryLength || !InfoOverrides.IsPlain(text)) throw new ArgumentException(Strings.Get("store.queryInvalid"));
        return text;
    }

    /// <summary>The lookup for an entry; an override that is not a valid match (a hand-edited config) counts as automatic.</summary>
    public static StoreLookup Of(LibraryItem item, ItemOverride? o, string scannedName)
    {
        if (SteamStore.AppIdOf(item) is { } steam) return new StoreLookup(steam, null);
        if (o?.StoreMatch == None) return new StoreLookup(null, null);
        if (IsAppId(o?.StoreMatch)) return new StoreLookup(o!.StoreMatch, null);
        return new StoreLookup(null, scannedName);
    }

    public static StoreMatchView View(LibraryItem item, ItemOverride? o) =>
        SteamStore.AppIdOf(item) is { } steam ? new StoreMatchView("steam", steam, null, null)
        : o?.StoreMatch == None ? new StoreMatchView("none", null, null, o.StoreAgeFloor)
        : IsAppId(o?.StoreMatch) ? new StoreMatchView("manual", o!.StoreMatch, o.StoreMatchName, o.StoreAgeFloor)
        : new StoreMatchView("auto", null, null, o?.StoreAgeFloor);

    /// <summary>
    /// Sets the match. <paramref name="previousAge"/> is the rating that counted before; unless the parents confirmed the change
    /// (<paramref name="confirmed"/>), the stricter of it and an earlier pending one stays as the floor. Confirmed, the floor goes.
    /// </summary>
    public static void Apply(ItemOverride o, string? match, string? name, int? previousAge, bool confirmed)
    {
        match = Validate(match);
        o.StoreMatch = match;
        o.StoreMatchName = IsAppId(match) ? InfoOverrides.Clip(name, MaxQueryLength) : null;
        if (confirmed) o.StoreAgeFloor = null;
        else if (previousAge is > 0 && previousAge > (o.StoreAgeFloor ?? 0)) o.StoreAgeFloor = previousAge;
    }
}

/// <summary>Which texts of an entry change: a field set to a value replaces the store's, set to null goes back to it; not set stays.</summary>
public sealed record InfoChange
{
    public bool SetDescription { get; init; }
    public string? Description { get; init; }
    public bool SetDevelopers { get; init; }
    public string? Developers { get; init; }
    public bool SetPublishers { get; init; }
    public string? Publishers { get; init; }
    public bool SetReleaseDate { get; init; }
    public string? ReleaseDate { get; init; }
    public bool SetGenres { get; init; }
    public string? Genres { get; init; }
}

/// <summary>What the edit menu shows: the texts as the detail page shows them, which of them are the user's, and the store match.</summary>
public sealed record ItemInfoView(
    string? Description, IReadOnlyList<string> Developers, IReadOnlyList<string> Publishers, string? ReleaseDate, IReadOnlyList<string> Genres,
    IReadOnlyList<string> Custom, StoreMatchView Store);

/// <summary>
/// Own texts for an entry (description, developer, publisher, release date, genres). Plain text in bounds: no control or direction characters,
/// limited lengths; the page shows them as text, never as markup. They change nothing but what is shown (the age rating is the store's).
/// </summary>
public static class InfoOverrides
{
    public const int MaxDescription = 1000;
    public const int MaxDate = 40;
    public const int MaxListEntry = 80;
    public const int MaxListEntries = 8;

    /// <summary>No control characters and no direction or invisible formatting characters (which could make a text read differently).</summary>
    public static bool IsPlain(string text) =>
        !text.Any(c => char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format);

    internal static string? Clip(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var plain = new string(text.Where(c => IsPlain(c.ToString())).ToArray()).Trim();
        return plain.Length == 0 ? null : plain.Length > max ? plain[..max] : plain;
    }

    private static string? Text(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.Length > max || !IsPlain(text)) throw new ArgumentException(Strings.Get("info.invalid", max));
        return text;
    }

    /// <summary>"Studio A, Studio B" as a list: split at commas and semicolons, empty parts dropped.</summary>
    public static List<string>? List(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var parts = value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (parts.Count == 0) return null;
        if (parts.Count > MaxListEntries || parts.Any(p => p.Length > MaxListEntry || !IsPlain(p))) throw new ArgumentException(Strings.Get("info.listInvalid", MaxListEntries, MaxListEntry));
        return parts;
    }

    /// <summary>Checks every field first, then sets: a refused field changes nothing.</summary>
    public static void Apply(ItemOverride o, InfoChange change)
    {
        var description = change.SetDescription ? Text(change.Description, MaxDescription) : o.Description;
        var developers = change.SetDevelopers ? List(change.Developers) : o.Developers;
        var publishers = change.SetPublishers ? List(change.Publishers) : o.Publishers;
        var date = change.SetReleaseDate ? Text(change.ReleaseDate, MaxDate) : o.ReleaseDate;
        var genres = change.SetGenres ? List(change.Genres) : o.Genres;
        o.Description = description;
        o.Developers = developers;
        o.Publishers = publishers;
        o.ReleaseDate = date;
        o.Genres = genres;
    }

    /// <summary>The fields the user set, by name, for the edit menu.</summary>
    public static IReadOnlyList<string> Custom(ItemOverride? o)
    {
        if (o is null) return [];
        var list = new List<string>();
        if (o.Description is not null) list.Add("description");
        if (o.Developers is not null) list.Add("developers");
        if (o.Publishers is not null) list.Add("publishers");
        if (o.ReleaseDate is not null) list.Add("releaseDate");
        if (o.Genres is not null) list.Add("genres");
        return list;
    }

    /// <summary>
    /// The store's info with the user's texts on top. The rating, trailers and screenshots stay the store's. Values from the file are bounded
    /// again (a hand-edited config). Null when there is neither.
    /// </summary>
    public static StoreInfo? Merge(StoreInfo? info, ItemOverride? o)
    {
        if (o is null || !o.HasInfo) return info;
        static IReadOnlyList<string>? Entries(List<string>? list) =>
            list?.Select(e => Clip(e, MaxListEntry)).OfType<string>().Take(MaxListEntries).ToList();
        var merged = info ?? new StoreInfo(null, [], [], null, []);
        return merged with
        {
            Description = o.Description is not null ? Clip(o.Description, MaxDescription) : merged.Description,
            Developers = Entries(o.Developers) ?? merged.Developers,
            Publishers = Entries(o.Publishers) ?? merged.Publishers,
            ReleaseDate = o.ReleaseDate is not null ? Clip(o.ReleaseDate, MaxDate) : merged.ReleaseDate,
            Genres = Entries(o.Genres) ?? merged.Genres
        };
    }
}
