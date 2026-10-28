namespace CouchHud.Core.Plugins;

/// <summary>Pure rules of the sign-in a plugin starts in the couch browser (capability "browser.signin"); the shell applies them.</summary>
public static class PluginSignInRules
{
    /// <summary>
    /// The state parameter of a sign-in address: exactly one, URL-safe characters, 1 to 256 of them (after percent-decoding); null for
    /// anything else (none, a second one, a bad escape, wrong characters, too long, an address that is not absolute, a query over 4096).
    /// A state hidden in the fragment does not count: only the query is read.
    /// </summary>
    public static string? StateOf(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Query.Length is 0 or > 4096) return null;
        string? state = null;
        foreach (var part in uri.Query.TrimStart('?').Split('&'))
        {
            var eq = part.IndexOf('=');
            // The name is decoded too: "%73tate" is a state as far as a server that decodes names is concerned.
            if (Uri.UnescapeDataString(eq < 0 ? part : part[..eq]) != "state") continue;
            if (state is not null) return null;
            state = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..]);
        }
        return state is { Length: > 0 and <= 256 } && state.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '.' or '_' or '~') ? state : null;
    }

    /// <summary>
    /// The address of a sign-in page as a root: absolute http(s), no user info, query or fragment, without a trailing slash. Null for
    /// anything else.
    /// </summary>
    public static string? NormalizeBase(string? baseUrl)
    {
        var text = (baseUrl ?? "").Trim().TrimEnd('/');
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.Host.Length == 0) return null;
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    /// <summary>A checked sign-in page: its origin (the root later compared with the page a redirect came from) and its state.</summary>
    public sealed record SignInPage(string Root, string State);

    /// <summary>
    /// Checks the address of a plugin's sign-in page, which by nature carries a query (response_type, client_id, redirect_uri, state):
    /// absolute http(s), at most 2048 characters, no control characters, no user info, no fragment, exactly one valid state. The root is
    /// the origin of the page (scheme, host, port, no trailing slash), the same thing <see cref="SameOrigin"/> compares. Null for anything else.
    /// </summary>
    public static SignInPage? ParsePage(string? pageUrl)
    {
        if (string.IsNullOrEmpty(pageUrl) || pageUrl.Length > 2048 || pageUrl.Any(char.IsControl) || pageUrl.Contains('#') || pageUrl != pageUrl.Trim()) return null;
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || uri.Host.Length == 0) return null;
        if (StateOf(pageUrl) is not { } state) return null;
        return new SignInPage(uri.GetLeftPart(UriPartial.Authority).TrimEnd('/'), state);
    }

    /// <summary>Same scheme, host and port: the page a redirect came from is the page of the sign-in.</summary>
    public static bool SameOrigin(string? url, string? baseUrl) =>
        Uri.TryCreate(url, UriKind.Absolute, out var a)
        && Uri.TryCreate(baseUrl, UriKind.Absolute, out var b)
        && string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.IdnHost.TrimEnd('.'), b.IdnHost.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;

    /// <summary>
    /// The address shown in the bar while a sign-in runs: the page without query and fragment (the state stays off the screen and out
    /// of the recent sites). Other addresses unchanged.
    /// </summary>
    public static string Shown(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.GetLeftPart(UriPartial.Path) : url;
}
