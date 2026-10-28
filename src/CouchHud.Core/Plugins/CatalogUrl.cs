namespace CouchHud.Core.Plugins;

/// <summary>
/// How the host compares catalog addresses. Scheme, host and (on GitHub) owner/repo do not care about case, because GitHub does not
/// either; everything after that (branch, folder, file name, query) is compared exactly, because there upper and lower case are different
/// files. Anything that does not parse as a plain https address (a user name, a different port, a trailing dot in the host, percent
/// escapes in owner/repo) stays different: what does not match exactly is not treated as the same address.
/// </summary>
public static class CatalogUrl
{
    /// <summary>The hosts where the first two path parts are owner and repository.</summary>
    private static readonly string[] GitHubHosts = ["github.com", "raw.githubusercontent.com"];

    /// <summary>The repositories of the project with the spelling the index uses (the form new entries must have).</summary>
    public static readonly IReadOnlyList<string> CanonicalRepositories = ["kewe87/CouchHUD-Catalog", "kewe87/CouchHUD", "kewe87/CaseHUD"];

    /// <summary>The comparison form of an address, or null when it is not a plain https address.</summary>
    public static string? Normalize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 || uri.Host.Length == 0 || !string.IsNullOrEmpty(uri.Fragment)) return null;
        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath;
        if (GitHubHosts.Contains(host))
        {
            // /owner/repo/rest: the first two parts in lower case, the rest as it is.
            // A percent escape in owner or repo (%6Bewe87) is not folded: such an address is not a plain one and only equals itself.
            var raw = uri.OriginalString.Trim().Split('/', 5);   // "https:", "", host, owner, repo/rest
            if (raw.Length > 3 && raw[3].Contains('%') || raw.Length > 4 && raw[4].Split(['/', '?', '#'])[0].Contains('%')) return null;
            var parts = path.Split('/', 4);   // "", owner, repo, rest
            if (parts.Length >= 3) path = "/" + parts[1].ToLowerInvariant() + "/" + parts[2].ToLowerInvariant() + (parts.Length == 4 ? "/" + parts[3] : "");
        }
        return $"https://{host}:{uri.Port}{TrimDirectorySlash(path)}{uri.Query}";
    }

    /// <summary>For sets and Distinct: two addresses are equal when <see cref="Same"/> says so.</summary>
    public static readonly IEqualityComparer<string> Comparer = new SameComparer();

    private sealed class SameComparer : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) => Same(x, y);
        public int GetHashCode(string s) => (Normalize(s) ?? s.Trim().TrimEnd('/')).GetHashCode(StringComparison.Ordinal);
    }

    /// <summary>Trailing slashes go only after a folder (the last part has no dot): "…/releases/" is "…/releases", but "…/index.json/" stays a different address than "…/index.json".</summary>
    private static string TrimDirectorySlash(string path)
    {
        if (!path.EndsWith('/')) return path;
        var trimmed = path.TrimEnd('/');
        return trimmed[(trimmed.LastIndexOf('/') + 1)..].Contains('.') ? path : trimmed;
    }

    /// <summary>The same address by the rules above. What is not a plain https address (developer sources: loopback http, file:) is only the same when it is written exactly alike.</summary>
    public static bool Same(string? a, string? b) =>
        a is not null && b is not null && (Normalize(a) is { } x && Normalize(b) is { } y
            ? string.Equals(x, y, StringComparison.Ordinal)
            : Normalize(a) is null && Normalize(b) is null && string.Equals(a.Trim().TrimEnd('/'), b.Trim().TrimEnd('/'), StringComparison.Ordinal));

    /// <summary>
    /// Null when a catalog entry may carry the address like this; else why not. Scheme and host in lower case, and an address into one of the
    /// project's own repositories on GitHub with exactly the spelling of <see cref="CanonicalRepositories"/> (the host accepts other cases, the index does not).
    /// </summary>
    public static string? NonCanonicalReason(string url)
    {
        if (Normalize(url) is null) return "not a plain https address";
        if (!url.StartsWith("https://", StringComparison.Ordinal)) return "scheme not written as lower-case https://";
        var uri = new Uri(url);
        var hostEnd = url.IndexOf('/', 8);
        var written = hostEnd < 0 ? url[8..] : url[8..hostEnd];
        if (written != written.ToLowerInvariant()) return "host not in lower case";
        if (!GitHubHosts.Contains(uri.Host)) return null;
        var parts = uri.AbsolutePath.Split('/', 4);
        if (parts.Length < 3) return null;
        var repo = parts[1] + "/" + parts[2];
        var canonical = CanonicalRepositories.FirstOrDefault(r => r.Equals(repo, StringComparison.OrdinalIgnoreCase));
        return canonical is null || canonical == repo ? null : $"owner/repo written '{repo}', must be '{canonical}'";
    }
}
