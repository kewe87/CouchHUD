using System.Globalization;
using System.Text;
using CouchHud.Core.Config;
using CouchHud.Sdk;

namespace CouchHud.Core.Plugins;

/// <summary>A plugin tab as the tab bar gets it: only plain, bounded values (a title cut and checked, a picture only as a checked PNG data URI).</summary>
public sealed record PluginTabView(string Plugin, string Id, string PageId, string Title, string Area, string? Glyph, string? Image, bool FirstParty, bool ChildSafe);

/// <summary>
/// The host's rules for tabs that plugins ask for (<see cref="IPluginContext.RegisterTab"/>), as pure functions: what an id, a title, a
/// glyph and a picture may be, which titles are refused because they look like a built-in tab, and which tabs are shown in what order.
/// A plugin only describes a tab; the page behind it is drawn by CouchHUD (ui/views/plugin-page.js) and every press on it goes through the
/// same gates as any plugin page. ui/core/plugin-tabs.js repeats the title, glyph and area rules; ui-logic-check.mjs makes sure both agree.
/// </summary>
public static class PluginTabRules
{
    public const int MaxPerPlugin = 2;
    public const int MaxTotal = 6;
    public const int MaxId = 32;
    /// <summary>What fits the tab bar next to six other tabs.</summary>
    public const int MaxTitle = 14;
    public const int MaxIconSide = 256;

    /// <summary>The glyph shown when a third party picks one that renders as a built-in icon.</summary>
    public const string GenericGlyph = "◆";

    /// <summary>The feature areas a tab may name ("always": never hidden by an area, for CouchHUD's own plugins only).</summary>
    private static readonly string[] Areas = [FeatureAreas.Games, FeatureAreas.Apps, FeatureAreas.Browser, FeatureAreas.News, FeatureAreas.Always];

    /// <summary>
    /// Titles of the built-in tabs and of CouchHUD's own screens, in both languages. A plugin tab with a title that is, or looks like or
    /// contains one of them is refused: it could pass for (or hide behind) a tab it is not. ui/core/plugin-tabs.js has the same list.
    /// </summary>
    public static readonly IReadOnlyList<string> ReservedTitles =
    [
        "Home", "Favoriten", "Favorites", "Spiele", "Games", "Apps", "Browser", "Downloads", "News", "Nachrichten", "Assistent", "Assistant",
        "Einstellungen", "Settings", "Smart Home", "Katalog", "Catalog", "Plugins", "Guide", "Profile", "Profil", "Sperre", "Lock", "Eltern", "Parents",
        "Sicherheit", "Security", "CouchHUD", "Update", "Updates", "Setup"
    ];

    /// <summary>Titles only CouchHUD's own plugins may use (the tab a built-in feature used to have before it became a plugin).</summary>
    public static readonly IReadOnlyList<string> FirstPartyTitles = ["Smart Home"];

    /// <summary>
    /// The glyphs ui/core/glyph-icons.js draws as one of the HUD's own outline icons (lock, gear, bell ...). A third party's tab never carries
    /// one: it would look like a built-in control. A test makes sure the list is the one in glyph-icons.js.
    /// </summary>
    public static readonly IReadOnlyList<string> BuiltInGlyphs =
    [
        "🔇", "🔉", "🖼", "💾", "🎨", "🗑", "🔑", "🔒", "🔓", "📁", "🔔", "🌐", "📸", "🔍", "⌕", "🖵", "🔊", "🔈", "🎮", "⚠", "⛔", "☾", "⏻", "✎", "▶", "⏺",
        "⬇", "⤓", "⬆", "↻", "⟳", "⟲", "↺", "⇄", "★", "☆", "⚙", "⌂", "✦", "▤", "▣", "♪", "ᛒ", "⏲", "▭", "⏸", "+", "◆", "○", "⋯", "🖴", "▦", "📺", "🎧", "🎬", "📜",
        "💡", "🔌", "🌀", "🔘", "🪟", "🪄", "🎵", "🌡️", "🌡", "🤖", "🌤️", "🌤", "☀️", "☀", "💧", "⚡", "🔋", "🚪", "🏃", "⛨"
    ];

    public static bool IsKnownArea(string? area) => area is not null && Areas.Contains(area, StringComparer.Ordinal);

    /// <summary>
    /// The area to use: the one named, else "apps". "always" only for CouchHUD's own plugins; for anyone else it counts as "apps", so a
    /// switched off area always hides a third party's tab.
    /// </summary>
    public static string AreaOf(string? area, bool firstParty)
    {
        var a = area?.Trim().ToLowerInvariant();
        if (a is null || !IsKnownArea(a)) return FeatureAreas.Apps;
        return a == FeatureAreas.Always && !firstParty ? FeatureAreas.Apps : a;
    }

    /// <summary>An id: lower case letters, digits, "-" and "_", starting with a letter or digit, at most <see cref="MaxId"/>.</summary>
    public static bool IdOk(string? id) =>
        id is { Length: > 0 and <= MaxId } && char.IsAsciiLetterOrDigit(id[0]) && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_');

    /// <summary>A letter of the Latin-1 supplement that has a plain base letter (or a known pair): German umlauts, ß and the usual accents.</summary>
    private static bool IsLatin1Letter(char c) => c is >= 'À' and <= 'ÿ' && c is not ('×' or '÷') && char.IsLetter(c);

    /// <summary>
    /// A title the tab bar may show: whitespace collapsed, 1 to <see cref="MaxTitle"/> characters, ASCII letters and digits, the letters of
    /// Latin-1 (ä ö ü ß é …) and a few signs ("-", "&amp;", ".", "'", "+", "/") only. No control or invisible characters, no combining
    /// marks, no other letters (ı, ł, ǀ, Greek, Cyrillic … cannot pass for a built-in title). Null when it does not pass.
    /// </summary>
    public static string? CleanTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaxTitle * 4) return null;
        // Decoration (emoji, symbols, joiners, bidi and invisible characters) is stripped first; what is left must pass the rules below.
        var text = PluginText.Strip(raw, MaxTitle * 4);
        if (text.Length == 0) return null;
        var sb = new StringBuilder();
        var lastSpace = true;
        foreach (var c in text.Trim())
        {
            if (c == ' ' || c == '\t' || c == ' ')
            {
                if (!lastSpace) sb.Append(' ');
                lastSpace = true;
                continue;
            }
            var ok = c < 128 ? char.IsAsciiLetterOrDigit(c) || c is '-' or '&' or '.' or '\'' or '+' or '/' : IsLatin1Letter(c);
            if (!ok) return null;
            sb.Append(c);
            lastSpace = false;
        }
        var title = sb.ToString().TrimEnd();
        return title.Length is > 0 and <= MaxTitle ? title : null;
    }

    /// <summary>
    /// The title in plain ASCII for comparing: lower case, diacritics removed (ü → u), ß → ss, æ → ae, ø → o, ð → d, þ → p, any other
    /// character above ASCII → "x" (so it can never match a built-in title).
    /// </summary>
    public static string AsciiFold(string title)
    {
        var sb = new StringBuilder();
        foreach (var c in title.ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            switch (c)
            {
                case 'ß': sb.Append("ss"); break;
                case 'æ': sb.Append("ae"); break;
                case 'ø': sb.Append('o'); break;
                case 'ð': sb.Append('d'); break;
                case 'þ': sb.Append('p'); break;
                default: sb.Append(c < 128 ? c : 'x'); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>The look of a title for comparing: <see cref="AsciiFold"/>, then <see cref="PluginNames.Skeleton"/> (0/o, 1/l/i, rn/m, separators …).</summary>
    public static string Look(string title) => PluginNames.Skeleton(AsciiFold(title));

    /// <summary>Whether a title is, looks like or contains a built-in title (or a name reserved for CouchHUD's own plugins).</summary>
    public static bool IsReservedTitle(string title, bool firstParty)
    {
        var look = Look(title);
        if (look.Length == 0) return true;
        if (firstParty && FirstPartyTitles.Any(f => Look(f) == look)) return false;
        foreach (var reserved in ReservedTitles)
        {
            var r = Look(reserved);
            if (look == r || (r.Length >= 4 && look.Contains(r, StringComparison.Ordinal))) return true;
        }
        // Looks like one of CouchHUD's own plugins' names ("CaseHUD", "Wand", ...): never for a plugin that is not CouchHUD's own.
        return !firstParty && PluginNames.IsReserved(AsciiFold(title));
    }

    private static string StripVariation(string glyph) => glyph.Replace("️", "").Replace("︎", "");

    /// <summary>
    /// A glyph: exactly one grapheme whose base is a symbol (categories So, Sm, Sk: emoji, arrows, shapes), optionally with a variation
    /// selector or an emoji skin tone. No letters, digits, marks, joiners or direction characters. Null otherwise.
    /// </summary>
    public static string? CleanGlyph(string? glyph)
    {
        if (string.IsNullOrEmpty(glyph) || glyph.Length > 16) return null;
        var info = new StringInfo(glyph);
        if (info.LengthInTextElements != 1) return null;
        var runes = glyph.EnumerateRunes().ToList();
        if (runes.Count is 0 or > 3) return null;
        var baseCat = Rune.GetUnicodeCategory(runes[0]);
        if (baseCat is not (UnicodeCategory.OtherSymbol or UnicodeCategory.MathSymbol or UnicodeCategory.ModifierSymbol)) return null;
        foreach (var r in runes.Skip(1))
        {
            var isVariation = r.Value is 0xFE0F or 0xFE0E;
            var isTone = r.Value is >= 0x1F3FB and <= 0x1F3FF;
            if (!isVariation && !isTone) return null;
        }
        return glyph;
    }

    /// <summary>
    /// The glyph to show: <see cref="CleanGlyph"/>; for anyone but CouchHUD's own plugins a glyph that renders as a built-in icon becomes the
    /// generic <see cref="GenericGlyph"/>, so a tab cannot wear the lock or the gear.
    /// </summary>
    public static string? GlyphFor(string? glyph, bool firstParty)
    {
        if (CleanGlyph(glyph) is not { } clean) return null;
        if (firstParty) return clean;
        var plain = StripVariation(clean);
        return BuiltInGlyphs.Any(b => StripVariation(b) == plain) ? GenericGlyph : clean;
    }

    /// <summary>A PNG of at most <see cref="PluginTab.MaxIconBytes"/> and <see cref="MaxIconSide"/> pixels a side as a data URI; anything else is dropped.</summary>
    public static string? IconUri(byte[]? image)
    {
        if (image is null || image.Length == 0 || image.Length > PluginTab.MaxIconBytes) return null;
        if (CatalogMediaCache.PngSize(image) is not { } size || size.Width is < 1 or > MaxIconSide || size.Height is < 1 or > MaxIconSide) return null;
        return "data:image/png;base64," + Convert.ToBase64String(image);
    }

    /// <summary>
    /// Why a registration is refused (a short English reason for the log), or null when it passes. The pages are not looked at here: the
    /// plugin may register its tab before its page.
    /// </summary>
    public static string? Problem(PluginTab? tab, bool firstParty)
    {
        if (tab is null) return "no tab";
        if (!IdOk(tab.Id)) return "id";
        if (tab.PageId is not { Length: > 0 and <= PluginRows.MaxId }) return "page id";
        var any = false;
        foreach (var raw in new[] { tab.TitleDe, tab.TitleEn })
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var title = CleanTitle(raw);
            if (title is null) return "title";
            if (IsReservedTitle(title, firstParty)) return "reserved title";
            any = true;
        }
        return any ? null : "title";
    }

    /// <summary>The title in <paramref name="language"/> (falls back to the other language); null when none passes.</summary>
    public static string? TitleFor(PluginTab tab, string language)
    {
        var first = CleanTitle(language == "en" ? tab.TitleEn : tab.TitleDe);
        return first ?? CleanTitle(language == "en" ? tab.TitleDe : tab.TitleEn);
    }

    /// <summary>One registered tab as the manager keeps it.</summary>
    public sealed record Registered(string Plugin, PluginOrigin Origin, IReadOnlySet<string> Capabilities, PluginTab Tab);

    /// <summary>
    /// The tabs to show now, in the order of the tab bar (after the built-in tabs). Only plugins that are loaded, still enabled and trusted
    /// (<paramref name="loaded"/>, asked now) and hold the capability; only tabs that pass every check again, whose page exists and whose
    /// area is on ("always" only counts for CouchHUD's own plugins). In a child's profile only CouchHUD's own plugins' tabs that say
    /// <see cref="PluginTab.ChildSafe"/> (the page behind them stays behind the usual gates); on the locked couch only CouchHUD's own
    /// plugins' tabs. CouchHUD's own plugins come first, then the others, each in the order they registered; a title already taken by an
    /// earlier tab is dropped; at most <see cref="MaxPerPlugin"/> per plugin and <see cref="MaxTotal"/> in all.
    /// </summary>
    public static IReadOnlyList<PluginTabView> Resolve(IEnumerable<Registered> registered, Func<string, bool> loaded, Func<string, string, bool> pageExists,
        string language, AreaSet areas, bool childProfile, bool locked = false, Func<string, string?, PluginLevel?>? levelOf = null)
    {
        var result = new List<PluginTabView>();
        var seenTitles = new HashSet<string>(StringComparer.Ordinal);
        var perPlugin = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var ordered = registered.Select((r, i) => (r, i)).OrderBy(x => PluginPolicy.IsOwn(x.r.Origin) ? 0 : 1).ThenBy(x => x.i).Select(x => x.r);
        foreach (var r in ordered)
        {
            if (result.Count >= MaxTotal) break;
            var own = PluginPolicy.IsOwn(r.Origin);
            if (!r.Capabilities.Contains(PluginCapabilities.Tabs) || !loaded(r.Plugin)) continue;
            // A tab of a function follows the owner's level (the tab's own flags only count for an untagged tab): off hides it, admin keeps it
            // off the locked couch and out of a child's profile, restricted shows it there too.
            var level = levelOf?.Invoke(r.Plugin, r.Tab.Function);
            if (level is { } lv)
            {
                // Admin in a child's profile still shows the tab: opening its page asks for the parents' code.
                if (!PluginFunctionRules.Access(lv, locked, childProfile).Allowed) continue;
            }
            else
            {
                if (locked && !own) continue;
                if (childProfile && !(own && r.Tab.ChildSafe)) continue;
            }
            if (Problem(r.Tab, own) is not null || !pageExists(r.Plugin, r.Tab.PageId)) continue;
            var area = AreaOf(r.Tab.Area, own);
            if (!areas.On(area)) continue;
            if (TitleFor(r.Tab, language) is not { } title) continue;
            if (perPlugin.GetValueOrDefault(r.Plugin) >= MaxPerPlugin) continue;
            if (!seenTitles.Add(Look(title))) continue;
            perPlugin[r.Plugin] = perPlugin.GetValueOrDefault(r.Plugin) + 1;
            result.Add(new PluginTabView(r.Plugin, r.Tab.Id, r.Tab.PageId, title, area, GlyphFor(r.Tab.Glyph, own), IconUri(r.Tab.Icon), own, level == PluginLevel.Restricted || (level is null && own && r.Tab.ChildSafe)));
        }
        return result;
    }
}
