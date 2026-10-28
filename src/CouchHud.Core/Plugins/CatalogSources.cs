using System.Security.Cryptography;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Plugins;

/// <summary>
/// A catalog the user added besides CouchHUD's own. Everything in it is third-party: its plugins carry the developer's own key and are
/// never CouchHUD's own, whatever the index says. The official catalog is not an entry of this kind: it is built in and cannot be removed.
/// </summary>
public sealed class CatalogSourceEntry
{
    /// <summary>The address of index.json (https only).</summary>
    public string Url { get; set; } = "";

    /// <summary>
    /// The public key (PEM, ECDSA P-256) the index is signed with, pinned at the time of adding. With it the index is verified like the
    /// official one (index.json.sig); without it the index is unsigned and says so. A package is still checked against its own statement.
    /// </summary>
    public string? PublisherKey { get; set; }

    /// <summary>A name for the list (at most 60 characters), shown next to the address.</summary>
    public string? Name { get; set; }

    public DateTimeOffset Added { get; set; }

    /// <summary>Set when the stored name did not pass the rules (it is dropped; the list shows the address and a marker instead).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool NameNotAllowed { get; set; }
}

public enum CatalogSourceChange
{
    Add,
    Remove
}

/// <summary>
/// The rules for catalog sources besides the official one. The official catalog
/// (<see cref="CatalogDefaults.IndexUrl"/>, signed with CouchHUD's key) is always there and can neither be removed nor switched off; any
/// other source is added only after the user confirmed that they trust its developer, because a plugin from it runs with the user's rights.
/// </summary>
public static class CatalogSourceRules
{
    /// <summary>How many extra sources there can be.</summary>
    public const int MaxSources = 8;

    /// <summary>An index of an extra source is read up to this size (the official one up to <see cref="CatalogService.MaxIndexBytes"/>).</summary>
    public const long MaxIndexBytes = 512 * 1024;

    /// <summary>Entries read from one extra source; the rest is ignored.</summary>
    public const int MaxEntries = 200;

    public const int MaxUrlLength = 300;
    public const int MaxNameLength = 60;
    public const int MaxKeyLength = 1024;

    /// <summary>The official catalog: built in, signed with CouchHUD's key, always there.</summary>
    public static string OfficialUrl => CatalogDefaults.IndexUrl;

    public static bool IsOfficial(string? url) => url is not null && Same(url, CatalogDefaults.IndexUrl);

    private static bool Same(string a, string b) => CatalogUrl.Same(a, b);

    /// <summary>
    /// Null when the source may be added, else a text key. Only https, no user name in the address, not the official catalog, not twice,
    /// a name and key of sensible size, a key that is a P-256 public key and not CouchHUD's own, and not more than <see cref="MaxSources"/>.
    /// </summary>
    public static string? CheckNew(string? url, string? publisherKey, string? name, IReadOnlyList<CatalogSourceEntry> current)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxUrlLength || !CatalogParser.IsAllowedUrl(url.Trim(), false)) return "catalog.source.httpsOnly";
        var uri = new Uri(url.Trim());
        if (uri.Host.Length == 0 || !string.IsNullOrEmpty(uri.Fragment)) return "catalog.source.httpsOnly";
        // Not an address inside the user's own network (an IP literal that is private, loopback, link-local or CGNAT, localhost, a name without a dot).
        if (!CatalogHosts.IsPublicHost(url.Trim())) return "catalog.source.privateHost";
        if (IsOfficial(url)) return "catalog.source.isOfficial";
        if (current.Any(s => Same(s.Url, url))) return "catalog.source.twice";
        if (current.Count >= MaxSources) return "catalog.source.tooMany";
        if (name is { Length: > MaxNameLength } || (name is not null && !PlainName(name))) return "catalog.source.badName";
        if (name is not null && Imitates(name)) return "catalog.source.nameImitates";
        if (!string.IsNullOrWhiteSpace(publisherKey))
        {
            if (publisherKey.Length > MaxKeyLength) return "catalog.source.badKey";
            try
            {
                if (PackageStatements.Fingerprint(publisherKey) == PackageStatements.Fingerprint(CatalogSignature.PublicKeyPem)) return "catalog.source.badKey";
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            {
                return "catalog.source.badKey";
            }
        }
        return null;
    }

    /// <summary>
    /// The sources of the old developer list: the official catalog first, always (whatever config.json lists), then, only with the developer
    /// switch, the further entries of <paramref name="developerList"/>. Catalogs of other developers are extra sources, not part of this.
    /// </summary>
    public static IReadOnlyList<string> OfficialAnd(IEnumerable<string>? developerList, bool developerSwitch) =>
        [CatalogDefaults.IndexUrl, .. developerSwitch ? (developerList ?? []).Where(s => !IsOfficial(s)) : []];

    /// <summary>Letters, digits, punctuation and plain spaces of the Latin alphabets only: no control, bidi, zero-width or other invisible marks, no look-alike scripts.</summary>
    public static bool PlainName(string name)
    {
        foreach (var ch in name)
        {
            var cat = char.GetUnicodeCategory(ch);
            if (cat is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator
                or System.Globalization.UnicodeCategory.ParagraphSeparator or System.Globalization.UnicodeCategory.PrivateUse or System.Globalization.UnicodeCategory.Surrogate
                or System.Globalization.UnicodeCategory.OtherNotAssigned or System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.EnclosingMark) return false;
            if (cat == System.Globalization.UnicodeCategory.SpaceSeparator && ch != ' ') return false;
            // Latin letters (up to Latin Extended-B, and Latin Extended Additional for Vietnamese and the like); other scripts look like Latin ones.
            if (char.IsLetter(ch) && ch > '\u024F' && ch is < '\u1E00' or > '\u1EFF') return false;
        }
        return true;
    }

    /// <summary>The name poses as CouchHUD or as an official catalog (case, look-alike letters and separators do not help).</summary>
    public static bool Imitates(string name)
    {
        var folded = new string(name.Normalize(System.Text.NormalizationForm.FormD).Where(c => char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark).ToArray());
        var skeleton = PluginNames.Skeleton(folded);
        return new[] { "couchhud", "offiziell", "official", "kewe87", "verified", "geprueft" }.Any(w => skeleton.Contains(PluginNames.Skeleton(w), StringComparison.Ordinal));
    }

    /// <summary>
    /// The extra sources that are really used: the ones a hand-edited config.json cannot smuggle past the rules (http, the official one,
    /// twice, a bad key, too many) are left out. Fail closed: what does not check out is not fetched.
    /// </summary>
    public static IReadOnlyList<CatalogSourceEntry> Effective(IEnumerable<CatalogSourceEntry>? configured)
    {
        var kept = new List<CatalogSourceEntry>();
        foreach (var source in configured ?? [])
        {
            if (source is null) continue;
            var problem = CheckNew(source.Url, source.PublisherKey, source.Name, kept);
            if (problem is null) kept.Add(source);
            else if (problem is "catalog.source.badName" or "catalog.source.nameImitates" && CheckNew(source.Url, source.PublisherKey, null, kept) is null)
            {
                // Stored by an older version (or edited by hand) with a name the rules do not allow: the catalog stays, listed by its address with
                // a marker instead of the name; it never shows as the name it wanted.
                kept.Add(new CatalogSourceEntry { Url = source.Url, PublisherKey = source.PublisherKey, Name = null, Added = source.Added, NameNotAllowed = true });
            }
        }
        return kept;
    }

    /// <summary>
    /// May this change go ahead? The same walls as installing a third-party plugin, and more: only from the HUD itself, never on the locked
    /// couch or in a child's profile, expert mode, the parents' code (the couch code without parental controls), and for adding the
    /// explicit confirmation that the user trusts the developer. Removing the official source is refused whatever else is true.
    /// </summary>
    /// <param name="acknowledged">The user confirmed "I trust this developer" in the dialog that shows the warning (<c>catalog.source.warning</c>).</param>
    public static PluginPolicy.GateDecision Decide(PluginPolicy.GateContext ctx, CatalogSourceChange change, string? url, string? publisherKey, string? name,
        bool acknowledged, IReadOnlyList<CatalogSourceEntry> current)
    {
        var action = "catalogSource" + change;
        PluginPolicy.GateDecision No(string key) => new(key, false, action);
        if (ctx.Caller != PluginCaller.Hud) return No("plugin.gate.hudOnly");
        if (ctx.Locked) return No("lock.unlockFirst");
        if (ctx.ChildProfile) return No("plugin.gate.child");
        if (change == CatalogSourceChange.Remove)
        {
            if (IsOfficial(url)) return No("catalog.source.officialStays");
            if (!ctx.ExpertMode) return No("catalog.source.expert");
            if (string.IsNullOrWhiteSpace(url) || !current.Any(s => Same(s.Url, url))) return No("catalog.source.unknown");
            return new PluginPolicy.GateDecision(null, true, action);
        }
        if (!ctx.ExpertMode) return No("catalog.source.expert");
        if (!acknowledged) return No("catalog.source.ackNeeded");
        if (CheckNew(url, publisherKey, name, current) is { } problem) return No(problem);
        return new PluginPolicy.GateDecision(null, true, action);
    }

    /// <summary>The list after the change; the caller saves it (and uses up the parents' code afterwards).</summary>
    public static List<CatalogSourceEntry> Apply(IReadOnlyList<CatalogSourceEntry> current, CatalogSourceChange change, string url, string? publisherKey, string? name, DateTimeOffset now)
    {
        var list = current.ToList();
        if (change == CatalogSourceChange.Remove)
        {
            list.RemoveAll(s => Same(s.Url, url));
            return list;
        }
        list.Add(new CatalogSourceEntry
        {
            Url = url.Trim(),
            PublisherKey = string.IsNullOrWhiteSpace(publisherKey) ? null : publisherKey.Trim(),
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
            Added = now
        });
        return list;
    }

    /// <summary>The short code of a pinned key for the list ("ab12-cd34-ef56-7890"); null when there is none.</summary>
    public static string? KeyCode(CatalogSourceEntry source)
    {
        if (string.IsNullOrWhiteSpace(source.PublisherKey)) return null;
        try
        {
            return PackageStatements.ShortFingerprint(PackageStatements.Fingerprint(source.PublisherKey));
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return null;
        }
    }
}
