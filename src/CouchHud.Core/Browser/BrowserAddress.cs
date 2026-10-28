using System.Globalization;

namespace CouchHud.Core.Browser;

/// <summary>What the address field of the couch browser makes of a typed text.</summary>
/// <param name="Url">The address that is opened (always http or https).</param>
/// <param name="Guessed">Typed without a scheme ("heise.de"): https first, http only when there is no https server (never on certificate errors).</param>
/// <param name="Search">The text is a search, not an address.</param>
public readonly record struct BrowserTarget(string Url, bool Guessed, bool Search);

/// <summary>
/// Typed text becomes an address or a search: "heise.de", "localhost:8080" and "192.168.1.1" open the site, "elden ring tipps",
/// "1.10", an e-mail address or "ms-settings:display" search with DuckDuckGo (no tracking, no account). Only http and https are
/// ever opened; every other scheme (file:, javascript:, ms-settings:, steam: …) is a search text.
/// </summary>
public static class BrowserAddress
{
    public const string Home = "https://duckduckgo.com/";
    private const string SearchPrefix = "https://duckduckgo.com/?q=";
    public const int MaxLength = 2048;

    public static BrowserTarget Parse(string? text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return new(Home, false, false);
        if (text.Length > MaxLength) text = text[..MaxLength];
        if (Uri.TryCreate(text, UriKind.Absolute, out var full) && full.Scheme is "http" or "https" && full.Host.Length > 0 && full.UserInfo.Length == 0)
        {
            return new(full.AbsoluteUri, false, false);
        }
        if (!text.Any(char.IsWhiteSpace) && LooksLikeHost(text) && Uri.TryCreate("https://" + text, UriKind.Absolute, out var guessed) && guessed.UserInfo.Length == 0)
        {
            return new(guessed.AbsoluteUri, true, false);
        }
        // DuckDuckGo reads a leading "!" as a jump to another site (a bang) and a leading "\" as "open the first hit": a search stays a search.
        var query = text.TrimStart('!', '\\', ' ');
        if (query.Length == 0) return new(Home, false, false);
        return new(SearchPrefix + Uri.EscapeDataString(query), false, true);
    }

    /// <summary>A Windows path (\\server\share, C:\folder): the couch browser opens no files and does not send such a path to a search engine either.</summary>
    public static bool IsLocalPath(string? text)
    {
        text = (text ?? "").Trim();
        if (text.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        return text.Length >= 3 && char.IsAsciiLetter(text[0]) && text[1] == ':' && text[2] is '\\' or '/';
    }

    /// <summary>The host part (up to the first / ? or #, without a port) is localhost, an IPv4 or IPv6 address or a domain with a real top-level domain.</summary>
    private static bool LooksLikeHost(string text)
    {
        if (text.StartsWith('/') || text.Contains('@') || text.Contains('\\')) return false;
        var end = text.IndexOfAny(['/', '?', '#']);
        var authority = end < 0 ? text : text[..end];
        if (authority.Length == 0) return false;
        string host;
        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']');
            if (close < 0) return false;
            host = authority[1..close];
            if (!PortOk(authority[(close + 1)..])) return false;
            return System.Net.IPAddress.TryParse(host, out var v6) && v6.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        }
        var colon = authority.IndexOf(':');
        host = colon < 0 ? authority : authority[..colon];
        if (colon >= 0 && !PortOk(authority[colon..])) return false;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        var labels = host.Split('.');
        if (labels.Length == 4 && labels.All(l => l.Length is > 0 and <= 3 && l.All(char.IsAsciiDigit)))
        {
            return labels.All(l => int.Parse(l, CultureInfo.InvariantCulture) <= 255);
        }
        if (labels.Length < 2 || labels.Any(l => l.Length is 0 or > 63)) return false;
        foreach (var label in labels)
        {
            if (label.StartsWith('-') || label.EndsWith('-')) return false;
            if (!label.All(c => char.IsLetterOrDigit(c) || c == '-')) return false;
        }
        var tld = labels[^1];
        // A top-level domain has letters only ("1.10" is a version number, "3.5" a figure); xn-- is the punycode form.
        return tld.Length >= 2 && (tld.All(char.IsLetter) || tld.StartsWith("xn--", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>":8080" or nothing.</summary>
    private static bool PortOk(string rest) =>
        rest.Length == 0 || (rest[0] == ':' && rest.Length is > 1 and <= 6 && rest[1..].All(char.IsAsciiDigit)
            && int.Parse(rest[1..], CultureInfo.InvariantCulture) is > 0 and <= 65535);
}
