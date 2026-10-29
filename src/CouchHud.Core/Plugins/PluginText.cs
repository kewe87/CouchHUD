using System.Globalization;
using System.Text;

namespace CouchHud.Core.Plugins;

/// <summary>
/// Plain text for the titles a plugin provides (tab, page and plugin name): the core draws every plugin title like its own, so
/// decoration is stripped here, wherever the title enters: emoji and pictographs, symbols, flags, skin tones, variation selectors, keycaps,
/// zero-width joiners and other invisible format characters, bidi overrides and isolates (a title that reads backwards or hides what it
/// says), control characters, private use and unassigned characters. Whitespace collapses, the length is capped (the UI adds the ellipsis
/// in CSS). Letters of any language, digits and ASCII punctuation stay. Row icons inside plugin pages are a different thing and stay.
/// </summary>
public static class PluginText
{
    /// <summary>The longest plugin name or page title (characters, not bytes).</summary>
    public const int MaxTitle = 60;

    /// <summary>The cleaned text; empty when nothing is left.</summary>
    public static string Strip(string? raw, int max = MaxTitle)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        // A text far beyond any title is not looked through at all.
        var text = (raw.Length > max * 8 ? raw[..(max * 8)] : raw).Normalize(NormalizationForm.FormC);
        var sb = new StringBuilder();
        var lastSpace = true;
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsSeparator(rune))
            {
                if (!lastSpace) sb.Append(' ');
                lastSpace = true;
                continue;
            }
            if (!Keep(rune)) continue;
            sb.Append(rune.ToString());
            lastSpace = false;
        }
        var result = sb.ToString().TrimEnd();
        return Cap(result, max);
    }

    /// <summary>The cleaned text, or <paramref name="fallback"/> (cleaned too) when nothing is left.</summary>
    public static string Title(string? raw, string? fallback, int max = MaxTitle)
    {
        var text = Strip(raw, max);
        return text.Length > 0 ? text : Strip(fallback, max);
    }

    /// <summary>
    /// <see cref="Title"/> for a title a plugin shows next to CouchHUD's own pages (page titles, the plugin name). A plugin that is not
    /// CouchHUD's own gets the fallback instead when the title mixes Latin with Greek or Cyrillic letters (a look-alike spelling such as
    /// "Еinstеllungen") or is exactly the name of a built-in screen ("Einstellungen", "Settings" … also spelled with digits or separators).
    /// </summary>
    public static string Safe(string? raw, string? fallback, bool firstParty, int max = MaxTitle)
    {
        var title = Title(raw, fallback, max);
        if (firstParty) return title;
        var plain = Strip(fallback, max);
        // Compatibility forms first (fullwidth and mathematical letters become plain ones), then look-alike letters of other scripts.
        var folded = title.Normalize(NormalizationForm.FormKC);
        if (MixedScripts(folded)) return plain;
        var look = PluginTabRules.Look(ToLatin(folded));
        return look.Length > 0 && PluginTabRules.ReservedTitles.Any(r => PluginTabRules.Look(r) == look) ? plain : title;
    }

    /// <summary>The look of a text for comparing with a fixed list of names: compatibility forms folded, look-alike letters made Latin, then the tab-title skeleton (case, separators, 0/o ...).</summary>
    public static string Look(string? text) => PluginTabRules.Look(ToLatin((text ?? "").Normalize(NormalizationForm.FormKC)));

    // Letters of other scripts that are drawn like a Latin letter (lower case; the text is lower-cased first).
    private static readonly Dictionary<char, char> Confusables = new()
    {
        ['а'] = 'a', ['е'] = 'e', ['о'] = 'o', ['р'] = 'p', ['с'] = 'c', ['х'] = 'x', ['у'] = 'y', ['ѕ'] = 's', ['і'] = 'i', ['ј'] = 'j',
        ['һ'] = 'h', ['к'] = 'k', ['м'] = 'm', ['т'] = 't', ['в'] = 'b', ['н'] = 'h',
        ['ο'] = 'o', ['ν'] = 'v', ['ι'] = 'i', ['α'] = 'a', ['τ'] = 't', ['κ'] = 'k', ['ρ'] = 'p', ['υ'] = 'u', ['ε'] = 'e',
        ['ɡ'] = 'g', ['ı'] = 'i',
    };

    /// <summary>The text in lower case with look-alike letters of Cyrillic, Greek and IPA replaced by the Latin letter they resemble.</summary>
    private static string ToLatin(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant()) sb.Append(Confusables.TryGetValue(c, out var latin) ? latin : c);
        return sb.ToString();
    }

    /// <summary>
    /// Whether a text has letters of Latin and of any other script (Greek, Cyrillic, Armenian, Cherokee, IPA extensions …): the spelling
    /// of a look-alike, not of a language. Latin means the Basic Latin, Latin-1 and Latin Extended blocks.
    /// </summary>
    public static bool MixedScripts(string text)
    {
        bool latin = false, other = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (!Rune.IsLetter(rune)) continue;
            var v = rune.Value;
            if (v < 0x250 || v is >= 0x1E00 and <= 0x1EFF) latin = true;
            else other = true;
        }
        return latin && other;
    }

    private static string Cap(string text, int max)
    {
        var count = 0;
        var index = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (count == max) return text[..index].TrimEnd();
            count++;
            index += rune.Utf16SequenceLength;
        }
        return text;
    }

    private static bool IsSeparator(Rune r)
    {
        if (r.Value is ' ' or '\t' or '\r' or '\n') return true;
        return Rune.GetUnicodeCategory(r) is UnicodeCategory.SpaceSeparator or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
    }

    private static bool IsDecorMark(Rune r) => r.Value is (>= 0xFE00 and <= 0xFE0F) or (>= 0xE0100 and <= 0xE01EF) or 0x20E3;

    private static bool Keep(Rune r)
    {
        if (r.Value < 128) return !char.IsControl((char)r.Value);
        return Rune.GetUnicodeCategory(r) switch
        {
            UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber
                or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber => true,
            UnicodeCategory.DashPunctuation or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation
                or UnicodeCategory.InitialQuotePunctuation or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation
                or UnicodeCategory.ConnectorPunctuation or UnicodeCategory.CurrencySymbol => true,
            // Combining marks stay (accents and vowel signs of other scripts), except variation selectors and the keycap mark.
            UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark => !IsDecorMark(r),
            // Symbols (emoji, arrows, shapes, math, modifiers), format (ZWJ, ZWNJ, bidi, tags), control, surrogates, private use, unassigned.
            _ => false
        };
    }
}
