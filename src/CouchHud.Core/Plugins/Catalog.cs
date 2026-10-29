using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Plugins;

/// <summary>Semantic version (major.minor.patch[-pre]); build metadata after '+' is ignored.</summary>
public readonly record struct SemVer(int Major, int Minor, int Patch, string Pre) : IComparable<SemVer>
{
    /// <summary>Optional fourth number (1.2.3.4), as in assembly versions; compared right after the patch.</summary>
    public int Revision { get; init; }

    private static readonly Regex Pattern = new(@"^v?(\d+)\.(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$", RegexOptions.Compiled);

    public static bool TryParse(string? text, out SemVer version)
    {
        version = default;
        var m = Pattern.Match(text?.Trim() ?? "");
        if (!m.Success) return false;
        if (!int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor)) return false;
        var patch = 0;
        if (m.Groups[3].Success && !int.TryParse(m.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out patch)) return false;
        var revision = 0;
        if (m.Groups[4].Success && !int.TryParse(m.Groups[4].Value, NumberStyles.None, CultureInfo.InvariantCulture, out revision)) return false;
        version = new SemVer(major, minor, patch, m.Groups[5].Value) { Revision = revision };
        return true;
    }

    public int CompareTo(SemVer other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        c = Revision.CompareTo(other.Revision);
        if (c != 0) return c;
        // A release is newer than any of its pre-releases.
        if (Pre.Length == 0 || other.Pre.Length == 0) return other.Pre.Length.CompareTo(Pre.Length);
        return ComparePre(Pre, other.Pre);
    }

    private static int ComparePre(string a, string b)
    {
        var pa = a.Split('.');
        var pb = b.Split('.');
        for (var i = 0; i < Math.Min(pa.Length, pb.Length); i++)
        {
            var na = int.TryParse(pa[i], NumberStyles.None, CultureInfo.InvariantCulture, out var x);
            var nb = int.TryParse(pb[i], NumberStyles.None, CultureInfo.InvariantCulture, out var y);
            var c = na && nb ? x.CompareTo(y) : na ? -1 : nb ? 1 : string.CompareOrdinal(pa[i], pb[i]);
            if (c != 0) return c;
        }
        return pa.Length.CompareTo(pb.Length);
    }

    public static bool operator <(SemVer a, SemVer b) => a.CompareTo(b) < 0;
    public static bool operator >(SemVer a, SemVer b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemVer a, SemVer b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemVer a, SemVer b) => a.CompareTo(b) >= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}{(Revision > 0 ? "." + Revision : "")}{(Pre.Length > 0 ? "-" + Pre : "")}";
}

public static class CatalogDefaults
{
    public const string IndexUrl = "https://raw.githubusercontent.com/kewe87/CouchHUD-Catalog/main/index.json";
}

public enum CatalogKind
{
    Plugin,
    Skin
}

/// <summary>One plugin or skin of a catalog index. <see cref="Problem"/> set means it is listed but cannot be installed.</summary>
public sealed record CatalogEntry(
    string Id,
    string Name,
    string Description,
    string? Author,
    CatalogKind Kind,
    string Version,
    string? MinCouchHud,
    string? DownloadUrl,
    string? Sha256,
    string? Homepage,
    string? IconUrl,
    string Folder,
    string Source,
    string? Problem,
    /// <summary>From a source without a valid signature (only possible for sources the user added).</summary>
    bool Unsigned = false)
{
    // ---- Plugin system v3 (registry data model) ----

    /// <summary>
    /// From a catalog the user added besides the official one: always third-party (a statement signed with CouchHUD's key is refused, the
    /// names of CouchHUD's own plugins are reserved), never revokes anything, needs a statement to be installed.
    /// </summary>
    public bool Extra { get; init; }

    /// <summary>The SDK version the plugin needs, when the index says so (the signed statement is what counts at install time). Above <see cref="CouchHud.Sdk.SdkVersion.Current"/>: not installable here.</summary>
    public int? Sdk { get; init; }

    /// <summary>The package statement (Base64 of its exact bytes), its signature and, for a third-party developer, the public key (PEM).</summary>
    public string? Statement { get; init; }

    public string? Signature { get; init; }
    public string? PublisherKey { get; init; }

    /// <summary>What the plugin says it needs (shown before installing; the signed statement is what counts).</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>SPDX licence; only free licences are listed.</summary>
    public string? License { get; init; }

    /// <summary>Offer this plugin once when a program with this name is installed (Windows uninstall entry, DisplayName).</summary>
    public string? SuggestWhenUninstallEntry { get; init; }

    /// <summary>
    /// Offer this plugin once to a household that used a feature of the core before it became a plugin; one of
    /// <see cref="PluginSuggestions.Features"/> (Home Assistant: <c>"homeAssistant"</c>). Anything else is ignored.
    /// </summary>
    public string? SuggestWhenFeature { get; init; }

    // ---- The list and the detail page ----

    /// <summary>The detail page's text (plain text). <see cref="Description"/> is the short line for the list.</summary>
    public string? LongDescription { get; init; }

    /// <summary>
    /// Hash of the icon at <see cref="IconUrl"/> when the entry has no statement (the signed index covers it). With a statement, the
    /// statement's hash counts. An icon without a hash is never shown.
    /// </summary>
    public string? IconSha256 { get; init; }

    /// <summary>Addresses of the screenshots, in the order of the statement's hashes (shown only when their hash checks out).</summary>
    public IReadOnlyList<string> Screenshots { get; init; } = [];

    /// <summary>An https address or a short text.</summary>
    public string? Changelog { get; init; }

    /// <summary>Package size in bytes, as the index says (the statement's counts when there is one).</summary>
    public long? Size { get; init; }
}

/// <summary>A revoked plugin (only from the signed default index): these versions (all when empty) or everything signed with this key.</summary>
public sealed record CatalogRevocation(string Id, IReadOnlyList<string> Versions, string? KeyFingerprint, string Reason)
{
    public bool Matches(string pluginId, string? version, string? keyFingerprint) =>
        (pluginId.Equals(Id, StringComparison.OrdinalIgnoreCase) && (Versions.Count == 0 || (version is not null && Versions.Contains(version))))
        || (KeyFingerprint is not null && keyFingerprint is not null && KeyFingerprint.Equals(keyFingerprint, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A parsed index: entries, revocations and the serial (see <see cref="CatalogParser.ReadSerial"/>).</summary>
public sealed record CatalogIndex(IReadOnlyList<CatalogEntry> Entries, IReadOnlyList<CatalogRevocation> Revoked, long Serial = 0);

/// <summary>Status of a catalog entry relative to what is installed and which CouchHud runs.</summary>
public enum CatalogStatus
{
    NotInstalled,
    Installed,
    UpdateAvailable,
    /// <summary>Needs a newer CouchHud (minCouchHud) or has a problem; cannot be installed.</summary>
    Unavailable
}

public sealed class CatalogException(string message) : Exception(message);

/// <summary>Parses the index.json format described in docs/PLUGINS.md. Broken entries are skipped, a broken index throws.</summary>
public static class CatalogParser
{
    public const int MaxEntries = 500;
    private static readonly Regex IdPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex HashPattern = new(@"^[0-9a-fA-F]{64}$", RegexOptions.Compiled);

    /// <param name="allowLocal">Developer switch: also accept http://127.0.0.1, localhost and file: URLs.</param>
    /// <param name="language">"de" or "en"; falls back to the other language.</param>
    public static IReadOnlyList<CatalogEntry> Parse(string json, string source, string language = "de", bool allowLocal = false) =>
        ParseIndex(json, source, language, allowLocal).Entries;

    /// <summary>Entries and the "revoked" list of an index.</summary>
    public static CatalogIndex ParseIndex(string json, string source, string language = "de", bool allowLocal = false)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 16 });
        }
        catch (JsonException ex)
        {
            throw new CatalogException(Strings.Get("catalog.invalidJson", ex.Message));
        }

        using (doc)
        {
            var root = doc.RootElement;
            var items = root.ValueKind == JsonValueKind.Array ? root
                : root.ValueKind == JsonValueKind.Object && root.TryGetProperty("entries", out var e) && e.ValueKind == JsonValueKind.Array ? e
                : throw new CatalogException(Strings.Get("catalog.noEntries"));

            var result = new List<CatalogEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items.EnumerateArray())
            {
                if (result.Count >= MaxEntries) break;
                var entry = ParseEntry(item, source, language, allowLocal);
                if (entry is not null && seen.Add($"{entry.Kind}:{entry.Id}")) result.Add(entry);
            }
            var revoked = new List<CatalogRevocation>();
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("revoked", out var r) && r.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in r.EnumerateArray())
                {
                    if (revoked.Count >= MaxEntries) break;
                    if (ParseRevocation(item) is { } rev) revoked.Add(rev);
                }
            }
            return new CatalogIndex(result, revoked, SerialOf(root));
        }
    }

    /// <summary>
    /// The "serial" of an index: a whole number that rises with every change of the file (replay protection, see SECURITY.md). Missing, not a whole
    /// number or negative counts as 0. An older client ignores the field.
    /// </summary>
    public static long ReadSerial(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 16 });
            return SerialOf(doc.RootElement);
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static long SerialOf(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("serial", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var n) && n > 0 ? n : 0;

    private static CatalogEntry? ParseEntry(JsonElement item, string source, string language, bool allowLocal)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        var id = Str(item, "id");
        // "names": { "de": "…", "en": "…" } is shown in the current language; older clients read "name" (plain text) as before. One clean line, at most 60 characters.
        var name = Skins.SkinCatalog.CleanText(Localized(item, "names", language) ?? Localized(item, "name", language), 60);
        CatalogKind? kind = Str(item, "type")?.ToLowerInvariant() switch
        {
            "plugin" => CatalogKind.Plugin,
            "skin" => CatalogKind.Skin,
            _ => null
        };
        var version = Str(item, "version");
        if (id is null || !IdPattern.IsMatch(id) || name is null || kind is null || !SemVer.TryParse(version, out _)) return null;

        var folder = Str(item, "folder") ?? id;
        var min = Str(item, "minCouchHud");
        var url = Str(item, "download");
        var hash = Str(item, "sha256");

        string? problem = null;
        if (!IdPattern.IsMatch(folder)) problem = Strings.Get("catalog.badFolder");
        else if (min is not null && !SemVer.TryParse(min, out _)) problem = Strings.Get("catalog.badMin");
        else if (url is null || !IsAllowedUrl(url, allowLocal)) problem = Strings.Get("catalog.badUrl");
        else if (hash is null || !HashPattern.IsMatch(hash)) problem = Strings.Get("catalog.noChecksum");

        // Every add-on stays free of charge: a paid marker or a licence that is not free makes the entry uninstallable.
        var license = Str(item, "license");
        if (problem is null)
        {
            foreach (var p in item.EnumerateObject())
            {
                if (FreeLicenses.IsPaidMarker(p.Name)) problem = Strings.Get("plugin.statement.notFree");
            }
            if (license is not null && !FreeLicenses.IsFree(license)) problem = Strings.Get("plugin.statement.notFree");
        }
        var statement = Str(item, "statement");
        if (problem is null && statement is not null && (Str(item, "signature") is null || statement.Length > PackageStatements.MaxStatementBytes * 2))
            problem = Strings.Get("plugin.statement.bad");
        var caps = new List<string>();
        if (item.TryGetProperty("capabilities", out var c) && c.ValueKind == JsonValueKind.Array)
        {
            foreach (var cap in c.EnumerateArray())
            {
                if (cap.ValueKind == JsonValueKind.String && caps.Count < 32 && cap.GetString() is { } s && CouchHud.Sdk.PluginCapabilities.All.Contains(s) && !caps.Contains(s))
                    caps.Add(s);
            }
        }
        string? suggest = null;
        if (item.TryGetProperty("suggestWhen", out var sw) && sw.ValueKind == JsonValueKind.Object && Str(sw, "uninstallEntry") is { Length: >= 3 and <= 100 } u)
            suggest = u;
        string? suggestFeature = null;
        if (item.TryGetProperty("suggestWhen", out var sf) && sf.ValueKind == JsonValueKind.Object && Str(sf, "feature") is { } feature && PluginSuggestions.Features.Contains(feature))
            suggestFeature = feature;

        var homepage = Str(item, "homepage");
        var icon = Str(item, "icon");
        return new CatalogEntry(
            id, name, Localized(item, "description", language) ?? "", Str(item, "author"), kind.Value, version!, min,
            problem is null ? url : null, hash?.ToLowerInvariant(),
            homepage is not null && IsAllowedUrl(homepage, false) ? homepage : null,
            icon is not null && IsAllowedUrl(icon, allowLocal) ? icon : null,
            folder, source, problem)
        {
            // Missing: no statement about it. Present but malformed (text, fraction, below 1, above 1000): counts as newer than any CouchHUD, so it is not installable.
            Sdk = !item.TryGetProperty("sdk", out var sdkEl) ? null
                : sdkEl.ValueKind == JsonValueKind.Number && sdkEl.TryGetInt32(out var sdkNumber) && sdkNumber is >= 1 and <= 1000 ? sdkNumber : int.MaxValue,
            Statement = statement,
            Signature = Str(item, "signature"),
            PublisherKey = Str(item, "publisherKey"),
            Capabilities = caps,
            License = license,
            SuggestWhenUninstallEntry = suggest,
            SuggestWhenFeature = suggestFeature,
            LongDescription = Localized(item, "longDescription", language) is { Length: <= 4000 } ld ? ld : null,
            IconSha256 = Str(item, "iconSha256") is { } ih && HashPattern.IsMatch(ih) ? ih.ToLowerInvariant() : null,
            Screenshots = Urls(item, "screenshots", allowLocal),
            Changelog = Str(item, "changelog") is { Length: <= 4000 } cl ? cl : null,
            Size = item.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number && sz.TryGetInt64(out var n) && n > 0 ? n : null
        };
    }

    private static IReadOnlyList<string> Urls(JsonElement item, string name, bool allowLocal)
    {
        var list = new List<string>();
        if (!item.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Array) return list;
        foreach (var u in a.EnumerateArray())
        {
            if (list.Count >= PackageMedia.MaxScreenshots) break;
            if (u.ValueKind == JsonValueKind.String && u.GetString() is { } s && IsAllowedUrl(s, allowLocal)) list.Add(s);
        }
        return list;
    }

    private static CatalogRevocation? ParseRevocation(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        var id = Str(item, "id");
        var key = Str(item, "keyFp")?.ToLowerInvariant();
        if ((id is null || !IdPattern.IsMatch(id)) && (key is null || !HashPattern.IsMatch(key))) return null;
        var versions = new List<string>();
        if (item.TryGetProperty("versions", out var v) && v.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in v.EnumerateArray())
            {
                if (x.ValueKind == JsonValueKind.String && SemVer.TryParse(x.GetString(), out _)) versions.Add(x.GetString()!);
            }
        }
        return new CatalogRevocation(id ?? "", versions, key is not null && HashPattern.IsMatch(key) ? key : null, Str(item, "reason") ?? "");
    }

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;

    /// <summary>"name" or "description": "text" or { "de": "…", "en": "…" }.</summary>
    private static string? Localized(JsonElement o, string name, string language)
    {
        if (!o.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String) return v.GetString();
        if (v.ValueKind != JsonValueKind.Object) return null;
        var other = language == "de" ? "en" : "de";
        return Str(v, language) ?? Str(v, other);
    }

    /// <summary>https only. With <paramref name="allowLocal"/> also loopback http and file: (developer switch).</summary>
    public static bool IsAllowedUrl(string url, bool allowLocal)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme == Uri.UriSchemeHttps) return uri.UserInfo.Length == 0;
        if (!allowLocal) return false;
        // file: only on local drives; \\server\share (UNC) would reach out to the network.
        if (uri.Scheme == Uri.UriSchemeFile) return !uri.IsUnc && uri.Host.Length == 0;
        return uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
    }
}

public static class CatalogRules
{
    /// <summary>The CouchHud version this assembly belongs to (all projects share Directory.Build.props).</summary>
    public static SemVer AppVersion
    {
        get
        {
            var assembly = typeof(CatalogRules).Assembly;
            // The informational version keeps a pre-release tag (0.4.0-beta.1) that the assembly version loses.
            var info = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion;
            if (SemVer.TryParse(info, out var v)) return v;
            return SemVer.TryParse(assembly.GetName().Version?.ToString(3), out v) ? v : default;
        }
    }

    public static bool IsCompatible(CatalogEntry entry, SemVer app) =>
        (entry.MinCouchHud is null || (SemVer.TryParse(entry.MinCouchHud, out var min) && min <= app)) && !NeedsNewerSdk(entry.Sdk);

    /// <summary>The package needs a newer plugin SDK than this CouchHUD implements (it could not load; an older host never offers it).</summary>
    public static bool NeedsNewerSdk(int? sdk) => sdk is { } needed && needed > CouchHud.Sdk.SdkVersion.Current;

    /// <summary>An installed package of unknown version counts as up to date.</summary>
    public static CatalogStatus StatusOf(CatalogEntry entry, bool installed, string? installedVersion, SemVer app)
    {
        if (entry.Problem is not null || !IsCompatible(entry, app))
        {
            return installed ? CatalogStatus.Installed : CatalogStatus.Unavailable;
        }
        if (!installed) return CatalogStatus.NotInstalled;
        return SemVer.TryParse(installedVersion, out var have) && SemVer.TryParse(entry.Version, out var offered) && offered > have
            ? CatalogStatus.UpdateAvailable
            : CatalogStatus.Installed;
    }

    /// <summary>
    /// Updates only come from the source a package was installed from. Bundled and hand-installed plugins
    /// (no record) can only be updated from the default source.
    /// </summary>
    public static string? OriginProblem(CatalogEntry entry, bool installed, string? recordSource)
    {
        if (!installed) return null;
        var origin = string.IsNullOrEmpty(recordSource) ? CatalogDefaults.IndexUrl : recordSource;
        return CatalogUrl.Same(entry.Source, origin) ? null : Strings.Get("catalog.otherSource");
    }

    public static string? Reason(CatalogEntry entry, SemVer app) =>
        entry.Problem ?? (NeedsNewerSdk(entry.Sdk) ? Strings.Get("catalog.needsSdk", entry.Sdk)
            : IsCompatible(entry, app) ? null : Strings.Get("catalog.needsVersion", entry.MinCouchHud, app));

    /// <summary>The hash check is mandatory: no expected hash, or a different one, means the file is rejected.</summary>
    public static void VerifySha256(string file, string? expected)
    {
        if (expected is null || !Regex.IsMatch(expected, "^[0-9a-fA-F]{64}$")) throw new CatalogException(Strings.Get("catalog.noChecksumInstall"));
        using var stream = File.OpenRead(file);
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new CatalogException(Strings.Get("catalog.checksumMismatch"));
    }
}

/// <summary>
/// The index of the default source is signed (ECDSA P-256 over SHA-256, IEEE P1363 signature, Base64 in
/// index.json.sig, over "couchhud-catalog-v1\n" followed by the exact bytes of index.json). The public key is part of the app; the private key
/// never leaves the maintainer's user profile (scripts/catalog-sign.ps1).
/// </summary>
public static class CatalogSignature
{
    public const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEKAXgwgjwj5Rjl37hmf04Cj+/vP8S
        JIKCCqRKrBy7MlNVIntuH2DjOkZMiobyQwlIZ3A20ypGgx77rtHg8vs88Q==
        -----END PUBLIC KEY-----
        """;

    public static string SigUrl(string indexUrl) => indexUrl + ".sig";

    /// <summary>
    /// Domain separation: the one key signs different things, and a signature only counts for the context
    /// it was made for (the signed bytes are the context line followed by the data). A signed catalog index
    /// can therefore never pass as a release checksum file or the other way round.
    /// </summary>
    public const string CatalogContext = "couchhud-catalog-v1\n";
    public const string ReleaseContext = "couchhud-release-v1\n";

    /// <summary>A plugin package statement (signed by CouchHUD's key for its own plugins, or by a third-party developer's key).</summary>
    public const string PluginContext = "couchhud-plugin-v1\n";

    /// <summary>The bytes that are actually signed.</summary>
    public static byte[] WithContext(string context, byte[] data) => [.. System.Text.Encoding.ASCII.GetBytes(context), .. data];

    /// <param name="format">The catalog uses IEEE P1363; CaseHUD releases use ASN.1 DER.</param>
    public static bool Verify(byte[] data, string? signatureBase64, string? publicKeyPem = null, string context = CatalogContext,
        DSASignatureFormat format = DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
    {
        if (string.IsNullOrWhiteSpace(signatureBase64)) return false;
        try
        {
            var signature = Convert.FromBase64String(signatureBase64.Trim());
            using var key = System.Security.Cryptography.ECDsa.Create();
            key.ImportFromPem(publicKeyPem ?? PublicKeyPem);
            return key.VerifyData(WithContext(context, data), signature, HashAlgorithmName.SHA256, format);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }
}
