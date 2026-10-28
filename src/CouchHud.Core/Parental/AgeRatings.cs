using System.Globalization;
using System.Text.Json;
using CouchHud.Core.Library;

namespace CouchHud.Core.Parental;

/// <summary>
/// Age ratings of several systems (USK, PEGI, ESRB and, as fallbacks only, ClassInd and CERO) from Steam's store answer, mapped to one
/// minimum age, and the system the age limit goes by. Pure logic; the host feeds it the Windows region.
/// <para>
/// Order for one game: the active system's rating; missing there, the strictest rating of the other systems; then Steam's own required
/// age; then unknown (the parents' rule for unknown ages applies). A rating that is present but cannot be read (hostile or broken store
/// data) never counts as a mild one: it makes the step it is in unknown.
/// </para>
/// </summary>
public static class AgeRatings
{
    public const string Auto = "auto";
    public const string Usk = "usk";
    public const string Pegi = "pegi";
    public const string Esrb = "esrb";
    /// <summary>No single system for the region: the strictest rating of all systems a game has counts.</summary>
    public const string Strictest = "strictest";
    public const string ClassInd = "dejus";
    public const string Cero = "cero";
    /// <summary>Steam's own required age (no rating system behind it).</summary>
    public const string Steam = "steam";

    /// <summary>What the parents can choose (config value <c>AgeRatingSystem</c>).</summary>
    public static readonly IReadOnlyList<string> Choices = [Auto, Usk, Pegi, Esrb];

    /// <summary>Every system read from the store; the first three can be the active one, the others only count as a fallback.</summary>
    public static readonly IReadOnlyList<string> Systems = [Usk, Pegi, Esrb, ClassInd, Cero];

    /// <summary>A rating that was there but could not be read: the system's step counts as unknown, never as "all ages".</summary>
    public const int Malformed = -1;

    /// <summary>Highest age kept; a larger number is cut to this, which is still stricter than any rating.</summary>
    public const int MaxAge = 21;

    // PEGI countries (EU and EEA, the UK, Switzerland, Israel and the Balkan states that use it); Germany has the USK.
    private static readonly HashSet<string> PegiRegions = new(StringComparer.OrdinalIgnoreCase)
    {
        "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR", "GR", "HU", "IS", "IE", "IT", "LV", "LI", "LT", "LU", "MT", "NL",
        "NO", "PL", "PT", "RO", "SK", "SI", "ES", "SE", "CH", "GB", "IL", "AL", "BA", "ME", "MK", "RS", "XK", "MC", "SM", "AD", "VA"
    };

    /// <summary>The system for a Windows region (ISO 3166 two letters): DE the USK, US and CA the ESRB, the rest of Europe PEGI, else the strictest.</summary>
    public static string ForRegion(string? region) => region?.Trim().ToUpperInvariant() switch
    {
        "DE" => Usk,
        "US" or "CA" => Esrb,
        { } r when PegiRegions.Contains(r) => Pegi,
        _ => Strictest
    };

    /// <summary>
    /// The system that counts now: an explicit choice as it is, "auto" by the region. Anything else (an edited config.json) is read the
    /// strictest way.
    /// </summary>
    public static string Resolve(string? configured, string? region) => configured switch
    {
        Usk or Pegi or Esrb => configured,
        Auto => ForRegion(region),
        _ => Strictest
    };

    /// <summary>The limits the picker offers for a system (ages; 0, "off", comes on top).</summary>
    public static IReadOnlyList<int> Steps(string system) => system switch
    {
        Usk => [6, 12, 16, 18],
        Pegi => [3, 7, 12, 16, 18],
        // No "AO" step: ESRB's 18 is almost never given, so a limit there would let every M game through. An AO game is above M and
        // stays blocked at every step.
        Esrb => [6, 10, 13, 17],
        _ => [3, 6, 12, 16, 18]
    };

    /// <summary>Every limit any system offers: the values a stored limit may have (0 is off).</summary>
    public static readonly IReadOnlySet<int> AllSteps = new HashSet<int> { 3, 6, 7, 10, 12, 13, 16, 17, 18 };

    /// <summary>The value a stored limit is used with: 0 off, a step as it is, anything else (an edited file) the strictest step there is.</summary>
    public static int NormalizeLimit(int stored) => stored == 0 ? 0 : AllSteps.Contains(stored) ? stored : AllSteps.Min();

    /// <summary>
    /// The limit a stored value counts as in <paramref name="system"/>: above the system's highest step it is that step (a stored 18 under
    /// the ESRB is M, 17), so a limit saved under another system never lets that system's top step through. 0 stays off.
    /// </summary>
    public static int LimitFor(int stored, string system)
    {
        var limit = NormalizeLimit(stored);
        return limit == 0 ? 0 : Math.Min(limit, Steps(system).Max());
    }

    /// <summary>
    /// The systems a game is judged by. Normally the active one. Under "auto" the region decides, and a child can change the Windows region
    /// without admin rights: when it now resolves to another system than the one the parents saved the limit under
    /// (<paramref name="saved"/>), both count and the stricter verdict wins, until the parents save again.
    /// </summary>
    public static IReadOnlyList<string> JudgedSystems(string? configured, string active, string? saved)
    {
        if (configured != Auto) return [active];
        var normalized = saved?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized)) return [active];
        // An edited value that names no system is read the strictest way, as in Resolve.
        if (normalized is not (Usk or Pegi or Esrb or Strictest)) normalized = Strictest;
        return normalized != active ? [active, normalized] : [active];
    }

    /// <summary>
    /// The saved system (<see cref="CouchHud.Core.Config.AppConfig.AgeRatingSystemResolved"/>) after the parents saved an age limit under
    /// <paramref name="active"/>: the first limit records the system; later ones keep what was recorded (audit 0.5 L2). Otherwise a
    /// child who switched the Windows region before the parents edited any limit would drop the old system from
    /// <see cref="JudgedSystems"/>. Only an explicit choice of the rating system by the parents moves it (SetRatingSystem).
    /// </summary>
    public static string SavedSystemAfterLimit(string? saved, string active) => string.IsNullOrWhiteSpace(saved) ? active : saved;

    /// <summary>
    /// Whether choosing a rating system moves the limits (<see cref="ConvertLimit"/>): not when they are already saved under
    /// <paramref name="target"/> (re-picking "auto" only re-records the system); always when nothing or something unknown was recorded.
    /// </summary>
    public static bool LimitsMove(string? saved, string target) => !string.Equals(saved, target, StringComparison.Ordinal);

    /// <summary>
    /// The age a store re-match keeps as its floor until the parents confirm: the strictest known over every system and Steam's own
    /// (<see cref="StoreInfo.RequiredAge"/>), at least <paramref name="known"/>. Not just the active system's: after a region switch
    /// a harmless re-match must not free a game another system blocks.
    /// </summary>
    public static int? KeptAge(StoreInfo? info, int? known)
    {
        int? strictest = info is { RatingKnown: true, RequiredAge: { } age and >= 0 } ? Math.Min(age, MaxAge) : null;
        return strictest is { } s && (known is not { } k || s > k) ? s : known;
    }

    /// <summary>
    /// One age for the limit check from the game's age in each judged system (null: unknown there), on the scale of the stored limit:
    /// at or above <paramref name="storedLimit"/> when any system blocks it (its age against <see cref="LimitFor"/>), else the highest
    /// known age (below the limit); null when an unknown age decides (with <paramref name="blockUnknown"/>) or none is known.
    /// </summary>
    public static int? VerdictAge(IReadOnlyList<(string System, int? Age)> views, int storedLimit, bool blockUnknown)
    {
        var limit = NormalizeLimit(storedLimit);
        if (views.Count == 0) return null;
        if (limit == 0) return views[0].Age;
        var blocking = views.Where(v => v.Age is { } a && a >= LimitFor(limit, v.System)).Select(v => Math.Max(v.Age!.Value, limit)).ToList();
        if (blocking.Count > 0) return blocking.Max();
        if (blockUnknown && views.Any(v => v.Age is null)) return null;
        var known = views.Where(v => v.Age is not null).Select(v => v.Age!.Value).ToList();
        return known.Count > 0 ? known.Max() : null;
    }

    /// <summary>
    /// The limit after a switch to <paramref name="system"/>: the nearest of its steps at or below the old age, so the limit never gets
    /// looser. Below its lowest step the old age stays (it is stricter than every step); off stays off.
    /// </summary>
    public static int ConvertLimit(int limit, string system)
    {
        var old = NormalizeLimit(limit);
        if (old == 0) return 0;
        var lower = Steps(system).Where(s => s <= old).ToList();
        return lower.Count > 0 ? lower.Max() : old;
    }

    /// <summary>The label of a limit in a system ("USK 12", "PEGI 7", "ESRB T"); null for a value that is no step of it (shown as an age).</summary>
    public static string? LimitLabel(string system, int age) =>
        system is Usk or Pegi or Esrb && Steps(system).Contains(age) ? Label(system, age) : null;

    /// <summary>The label of a rating ("USK 12", "PEGI 16", "ESRB E10+", "CERO D", "ClassInd 14"); null for Steam's age or a strictest pick.</summary>
    public static string? Label(string system, int age) => system switch
    {
        Usk => $"USK {age}",
        Pegi => $"PEGI {age}",
        Esrb => EsrbCodes.FirstOrDefault(c => c.Value == age).Key is { } code ? "ESRB " + code.ToUpperInvariant().Replace("E10", "E10+", StringComparison.Ordinal) : null,
        Cero => CeroCodes.FirstOrDefault(c => c.Value == age).Key is { } cero ? "CERO " + cero.ToUpperInvariant() : null,
        ClassInd => age == 0 ? "ClassInd L" : $"ClassInd {age}",
        _ => null
    };

    private static readonly Dictionary<string, int> EsrbCodes = new() { ["ec"] = 3, ["e"] = 6, ["e10"] = 10, ["t"] = 13, ["m"] = 17, ["ao"] = 18 };
    private static readonly Dictionary<string, int> CeroCodes = new() { ["a"] = 0, ["b"] = 12, ["c"] = 15, ["d"] = 17, ["z"] = 18 };

    /// <summary>
    /// One system's rating from the store: the age, <see cref="Malformed"/> for something that cannot be read, null for none given
    /// (ESRB's "rating pending" too).
    /// </summary>
    public static int? ParseCode(string system, JsonElement rating)
    {
        string? text = rating.ValueKind switch
        {
            JsonValueKind.String => rating.GetString(),
            JsonValueKind.Number => rating.TryGetInt32(out var n) ? n.ToString(CultureInfo.InvariantCulture) : "",
            JsonValueKind.Null => null,
            _ => ""
        };
        if (text is null) return null;
        var code = text.Trim().ToLowerInvariant().Replace(" ", "", StringComparison.Ordinal).TrimEnd('+');
        if (system is Esrb or Cero) code = code.Replace("-", "", StringComparison.Ordinal);
        if (code.Length == 0) return Malformed;
        switch (system)
        {
            case Esrb:
                if (code is "rp") return null;
                return EsrbCodes.TryGetValue(code, out var esrb) ? esrb : Malformed;
            case Cero:
                return CeroCodes.TryGetValue(code, out var cero) ? cero : Malformed;
            case ClassInd when code is "l" or "al":
                return 0;
            default:
                // USK, PEGI, ClassInd: a whole number of years, nothing else.
                if (code.Length > 3 || !int.TryParse(code, NumberStyles.None, CultureInfo.InvariantCulture, out var age)) return Malformed;
                return Math.Min(age, MaxAge);
        }
    }

    /// <summary>
    /// Every rating Steam's appdetails list ("ratings": {"usk": {"rating": "12"}, "esrb": {"rating": "t"} …}). A system whose entry is
    /// there but not readable is kept as <see cref="Malformed"/>; a "ratings" that is no object gives nothing.
    /// </summary>
    public static Dictionary<string, int> Parse(JsonElement data)
    {
        var result = new Dictionary<string, int>();
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("ratings", out var ratings) || ratings.ValueKind != JsonValueKind.Object) return result;
        foreach (var system in Systems)
        {
            if (!ratings.TryGetProperty(system, out var entry) || entry.ValueKind == JsonValueKind.Null) continue;
            if (entry.ValueKind != JsonValueKind.Object)
            {
                result[system] = Malformed;
                continue;
            }
            if (!entry.TryGetProperty("rating", out var rating))
            {
                result[system] = Malformed;
                continue;
            }
            if (ParseCode(system, rating) is { } age) result[system] = age;
        }
        return result;
    }

    /// <summary>Steam's own required age: a whole number above 0 (cut to <see cref="MaxAge"/>), else none.</summary>
    public static int? ParseRequiredAge(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("required_age", out var age)) return null;
        var n = age.ValueKind switch
        {
            JsonValueKind.Number when age.TryGetInt32(out var v) => v,
            JsonValueKind.String when int.TryParse(age.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var v) => v,
            _ => 0
        };
        return n > 0 ? Math.Min(n, MaxAge) : null;
    }

    /// <summary>The age a game counts with and where it came from (a rating system, <see cref="Steam"/>, or null for an older cache).</summary>
    public sealed record Rating(int Age, string? System);

    /// <summary>
    /// The age that counts for <paramref name="system"/> (see the order above); null when unknown. A cache of an older version (no
    /// per-system ratings) keeps its one age (the USK, else Steam's).
    /// </summary>
    public static Rating? Effective(StoreInfo info, string system)
    {
        if (info.Ratings is null) return info.RequiredAge is { } legacy and >= 0 ? new Rating(Math.Min(legacy, MaxAge), null) : null;
        return Effective(info.Ratings, info.SteamRequiredAge, system);
    }

    public static Rating? Effective(IReadOnlyDictionary<string, int> ratings, int? requiredAge, string system)
    {
        // Only the systems this class reads; a value from an edited cache below 0 counts as unreadable.
        var known = ratings.Where(r => Systems.Contains(r.Key)).ToDictionary(r => r.Key, r => r.Value < 0 ? Malformed : Math.Min(r.Value, MaxAge));
        if (system is Usk or Pegi or Esrb && known.TryGetValue(system, out var own))
            return own == Malformed ? null : new Rating(own, system);
        var others = known.Where(r => r.Key != system).ToList();
        if (others.Count > 0)
        {
            // One unreadable rating could have been the strictest: then the age is unknown.
            if (others.Any(r => r.Value == Malformed)) return null;
            var strictest = others.OrderByDescending(r => r.Value).ThenBy(r => Systems.ToList().IndexOf(r.Key)).First();
            // Steam's own required age counts too, as in StrictestAge: above every other rating it wins.
            return requiredAge is { } gate and > 0 && Math.Min(gate, MaxAge) > strictest.Value
                ? new Rating(Math.Min(gate, MaxAge), Steam)
                : new Rating(strictest.Value, strictest.Key);
        }
        return requiredAge is { } steam and > 0 ? new Rating(Math.Min(steam, MaxAge), Steam) : null;
    }

    /// <summary>The strictest known age over every system and Steam's own; null when any rating is unreadable or none is known.</summary>
    public static int? StrictestAge(IReadOnlyDictionary<string, int> ratings, int? requiredAge)
    {
        if (ratings.Values.Any(v => v < 0)) return null;
        var ages = ratings.Where(r => Systems.Contains(r.Key)).Select(r => Math.Min(r.Value, MaxAge)).ToList();
        if (requiredAge is { } steam and > 0) ages.Add(Math.Min(steam, MaxAge));
        return ages.Count > 0 ? ages.Max() : null;
    }
}
