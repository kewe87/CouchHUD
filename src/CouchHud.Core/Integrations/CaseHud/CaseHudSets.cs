using System.Text.Json;

namespace CouchHud.Core.Integrations.CaseHud;

/// <summary>What goes on to POST /sets/apply after <see cref="CaseHudSets.Check"/>. <see cref="Panels"/> null means all displays.</summary>
public sealed record CaseHudSetApply(string Set, string Theme, IReadOnlyList<string>? Panels);

/// <summary>
/// The check of "style for all displays" from the page: only a style CaseHUD lists (GET /sets) in a theme a display can have
/// (<see cref="CaseHudPanelPatch.Themes"/>), and only on displays CaseHUD knows. The page cannot pass anything else on to CaseHUD.
/// </summary>
public static class CaseHudSets
{
    /// <summary>At most this many display ids in one call.</summary>
    public const int MaxPanels = 64;

    /// <param name="panels">The page's list of display ids; anything but a non-empty list means all displays.</param>
    /// <param name="sets">What GET /sets returned.</param>
    /// <param name="knownPanels">The ids of the displays CaseHUD reported; needed only when <paramref name="panels"/> is a non-empty list.</param>
    /// <exception cref="ArgumentException">Unknown theme or style, too many ids, an id that is no string, or no known display among them.</exception>
    public static CaseHudSetApply Check(string set, string theme, JsonElement panels, IEnumerable<CaseHudSet> sets, IEnumerable<string>? knownPanels)
    {
        if (!CaseHudPanelPatch.Themes.Contains(theme)) throw new ArgumentException("set: unknown theme");
        if (!sets.Any(s => string.Equals(s.Set, set, StringComparison.Ordinal) && string.Equals(s.Theme, theme, StringComparison.Ordinal)))
            throw new ArgumentException("set: unknown style");
        if (panels.ValueKind != JsonValueKind.Array || panels.GetArrayLength() == 0) return new CaseHudSetApply(set, theme, null);
        if (panels.GetArrayLength() > MaxPanels) throw new ArgumentException($"set: at most {MaxPanels} displays");
        if (panels.EnumerateArray().Any(p => p.ValueKind != JsonValueKind.String)) throw new ArgumentException("set: display ids must be strings");
        var known = new HashSet<string>(knownPanels ?? [], StringComparer.Ordinal);
        var ids = panels.EnumerateArray().Select(p => p.GetString()!).Where(known.Contains).Distinct(StringComparer.Ordinal).ToList();
        // A list without a known display must not turn into "all displays".
        if (ids.Count == 0) throw new ArgumentException("set: no known display");
        return new CaseHudSetApply(set, theme, ids);
    }
}
