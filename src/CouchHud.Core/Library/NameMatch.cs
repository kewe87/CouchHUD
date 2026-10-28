using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CouchHud.Core.Library;

/// <summary>
/// Decides whether two game names are the same game. Conservative on purpose: a wrong cover is worse
/// than none. Equal after dropping accents, trademarks, a trailing year, edition suffixes ("Game of the
/// Year Edition", "GOTY", "Deluxe Edition", ...) and writing roman numerals II-IX as digits.
/// "Remastered", numbers, and subtitles are kept: those are different products.
/// </summary>
public static partial class NameMatch
{
    private static readonly Dictionary<string, string> Roman = new()
    {
        ["ii"] = "2", ["iii"] = "3", ["iv"] = "4", ["vi"] = "6", ["vii"] = "7", ["viii"] = "8", ["ix"] = "9"
    };

    // Matched only at the very end of the name.
    private static readonly string[] EditionSuffixes =
    [
        "game of the year edition", "goty edition", "goty", "game of the year",
        "digital deluxe edition", "ultimate edition", "definitive edition", "complete edition",
        "deluxe edition", "enhanced edition", "standard edition", "gold edition", "special edition",
        "collectors edition", "anniversary edition", "premium edition", "legendary edition",
        "digital edition", "director s cut", "directors cut"
    ];

    /// <summary>Latin letters that no decomposition splits into a base letter.</summary>
    private static readonly Dictionary<char, string> Letters = new()
    {
        ['ß'] = "ss", ['æ'] = "ae", ['œ'] = "oe", ['ø'] = "o", ['ł'] = "l", ['đ'] = "d", ['þ'] = "th", ['ð'] = "d", ['ı'] = "i"
    };

    /// <summary>
    /// The name reduced to what makes the game: see the class. Empty when the name has letters outside the Latin alphabet (Japanese,
    /// Cyrillic …): the store search answers in English, so such a name is never compared, rather than compared by what is left of it.
    /// </summary>
    public static string Canonical(string name)
    {
        // Marks first (compatibility decomposition turns ™ into "TM"), then KD: "Ⅱ" becomes "II", full-width letters plain ones.
        var s = name.Replace("™", " ").Replace("®", " ").Replace("©", " ").Replace("℠", " ").Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(s.Length);
        foreach (var raw in s)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(raw) == UnicodeCategory.NonSpacingMark) continue;
            var c = char.ToLowerInvariant(raw);
            if (Letters.TryGetValue(c, out var latin)) sb.Append(latin);
            else if (char.IsLetter(c) && c is not (>= 'a' and <= 'z')) return "";
            else sb.Append(c);
        }
        s = sb.ToString().Replace("&", " and ");
        s = Year().Replace(s, " ");
        s = NonAlphaNumeric().Replace(s, " ").Trim();

        // "the" in front doesn't make a different game.
        if (s.StartsWith("the ", StringComparison.Ordinal)) s = s[4..];
        // Edition words can stack ("Deluxe Edition GOTY"): peel until nothing changes.
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var suffix in EditionSuffixes)
            {
                if (s.Length > suffix.Length && s.EndsWith(" " + suffix, StringComparison.Ordinal))
                {
                    s = s[..^(suffix.Length + 1)].TrimEnd();
                    changed = true;
                }
            }
        }

        // Not the first token ("II" at the start is rarely a number); "I", "V" and "X" stay as they are.
        var tokens = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i < tokens.Length; i++)
        {
            if (Roman.TryGetValue(tokens[i], out var digit)) tokens[i] = digit;
        }
        return string.Join(' ', tokens);
    }

    /// <summary>
    /// The same game: equal canonical names with at least one letter (a bare "2" says nothing), and not two different years in brackets
    /// ("Doom (1993)" is not "Doom (2016)"; one year against none still matches).
    /// </summary>
    public static bool Same(string a, string b)
    {
        var x = Canonical(a);
        if (x.Length == 0 || !x.Any(char.IsAsciiLetter) || x != Canonical(b)) return false;
        return YearOf(a) is not { } ya || YearOf(b) is not { } yb || ya == yb;
    }

    /// <summary>The very same name, only case, trademark signs and spacing apart: decides between several entries that are <see cref="Same"/>.</summary>
    public static bool Exact(string a, string b)
    {
        static string Plain(string s) => string.Join(' ', s.Replace("™", " ").Replace("®", " ").Replace("©", " ").Replace("℠", " ")
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        return Plain(a).Length > 0 && Plain(a) == Plain(b);
    }

    private static string? YearOf(string name) => Year().Match(name) is { Success: true } m ? m.Value : null;

    [GeneratedRegex(@"\((?:19|20)\d\d\)")]
    private static partial Regex Year();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphaNumeric();
}
