using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CouchHud.Core.Localization;
using CouchHud.Sdk;

namespace CouchHud.Core.Plugins;

/// <summary>
/// What a signed plugin package says about itself (the statement). It is signed as exact bytes under the context
/// <see cref="CatalogSignature.PluginContext"/>: with CouchHUD's own key (first-party) or with the developer's own P-256 key, whose
/// public half travels along (third-party, pinned on first install).
/// </summary>
public sealed record PackageStatement(
    string Id,
    string Folder,
    string Version,
    string Sha256,
    int Sdk,
    string? MinCouchHud,
    IReadOnlyList<string> Capabilities,
    string PublisherKey,
    string License,
    string? Source,
    string? Author)
{
    // ---- What the catalog list and detail show (all covered by the signature) ----

    public string? Name { get; init; }

    /// <summary>One line for the list.</summary>
    public string? Summary { get; init; }

    /// <summary>The detail page's text (plain text, never markup).</summary>
    public string? Description { get; init; }

    /// <summary>The icon: a PNG of at most <see cref="PackageMedia.MaxIconBytes"/> with this hash.</summary>
    public PackageMedia? Icon { get; init; }

    /// <summary>Screenshots: PNGs of at most <see cref="PackageMedia.MaxScreenshotBytes"/> each, at most <see cref="PackageMedia.MaxScreenshots"/>.</summary>
    public IReadOnlyList<PackageMedia> Screenshots { get; init; } = [];

    /// <summary>An https address or a short text.</summary>
    public string? Changelog { get; init; }

    /// <summary>Size of the package in bytes.</summary>
    public long? Size { get; init; }
}

/// <summary>An image of a package: its SHA-256 and size, signed with the statement; the bytes come from the catalog and are checked against both.</summary>
public sealed record PackageMedia(string Sha256, long Bytes)
{
    public const long MaxIconBytes = 256 * 1024;
    public const long MaxScreenshotBytes = 1024 * 1024;
    public const int MaxScreenshots = 6;
}

/// <summary>The statement as it travels: its exact bytes, the signature, and (third-party) the public key in PEM.</summary>
public sealed record SignedStatement(byte[] Statement, string? Signature, string? PublisherKeyPem);

/// <summary>A statement whose signature checked out.</summary>
public sealed record VerifiedStatement(PackageStatement Statement, PluginOrigin Origin, string KeyFingerprint)
{
    /// <summary>What the trust store records after the package was installed from it.</summary>
    public PluginTrustEntry ToTrustEntry(string hash = "") => new()
    {
        Hash = hash,
        Origin = Origin,
        PluginId = Statement.Id,
        Version = Statement.Version,
        KeyFingerprint = KeyFingerprint,
        Capabilities = Statement.Capabilities
    };
}

/// <summary>
/// Checks package statements. Fails closed: anything missing, malformed, unsigned, signed in another context, with a key that does not
/// match the pinned one, older than what is installed, revoked, non-free or with a reserved name is refused (a <see cref="CatalogException"/>).
/// </summary>
public static class PackageStatements
{
    public const int MaxStatementBytes = 16 * 1024;
    public const int MaxEnvelopeBytes = 32 * 1024;
    private static readonly Regex HashPattern = new("^[0-9a-f]{64}$", RegexOptions.Compiled);
    private static readonly Regex IdPattern = new(@"^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.Compiled);
    private static readonly Regex FolderPattern = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);

    /// <summary>The fields a statement may have; anything else (a price, a licence key …) is refused.</summary>
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "id", "folder", "version", "sha256", "sdk", "minCouchHud", "capabilities", "publisherKey", "license", "source", "author",
        "name", "summary", "description", "icon", "screenshots", "changelog", "size"
    };

    /// <summary>Lower-case hex SHA-256 of the public key (SubjectPublicKeyInfo DER). Throws for anything but a P-256 key.</summary>
    public static string Fingerprint(string publicKeyPem)
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(publicKeyPem);
        if (key.KeySize != 256) throw new CryptographicException("not P-256");
        return Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
    }

    /// <summary>The short code shown in the warning dialog ("ab12-cd34-ef56-7890").</summary>
    public static string ShortFingerprint(string fingerprint) =>
        fingerprint.Length >= 16 ? $"{fingerprint[..4]}-{fingerprint[4..8]}-{fingerprint[8..12]}-{fingerprint[12..16]}" : fingerprint;

    /// <summary>
    /// The envelope next to a .zip (<c>&lt;package&gt;.zip.sig</c>) or inside a catalog entry:
    /// <c>{ "statement": base64, "signature": base64, "publisherKey": PEM }</c>.
    /// </summary>
    public static SignedStatement ParseEnvelope(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxEnvelopeBytes) throw new CatalogException(Strings.Get("plugin.statement.bad"));
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new CatalogException(Strings.Get("plugin.statement.bad"));
            return FromFields(Text(root, "statement"), Text(root, "signature"), Text(root, "publisherKey"))
                   ?? throw new CatalogException(Strings.Get("plugin.statement.bad"));
        }
        catch (JsonException)
        {
            throw new CatalogException(Strings.Get("plugin.statement.bad"));
        }
    }

    /// <summary>From the fields of a catalog entry; null without a statement.</summary>
    public static SignedStatement? FromFields(string? statementBase64, string? signature, string? publisherKeyPem)
    {
        if (statementBase64 is null) return null;
        try
        {
            var bytes = Convert.FromBase64String(statementBase64);
            if (bytes.Length > MaxStatementBytes) throw new CatalogException(Strings.Get("plugin.statement.bad"));
            return new SignedStatement(bytes, signature, publisherKeyPem);
        }
        catch (FormatException)
        {
            throw new CatalogException(Strings.Get("plugin.statement.bad"));
        }
    }

    private static string? Text(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>
    /// Verifies the signature and reads the statement. First-party when it verifies against CouchHUD's key; else third-party when it
    /// verifies against the key that came along (which must be the one the statement names). Never both, never neither.
    /// </summary>
    /// <param name="builtInKeyPem">CouchHUD's key (tests pass their own).</param>
    /// <param name="firstPartyIds">Ids of CouchHUD's own plugins (from the signed index): reserved for third-party developers.</param>
    /// <param name="allowFirstParty">False for a package from a catalog the user added: only CouchHUD's own catalog may hand out CouchHUD's own plugins.</param>
    public static VerifiedStatement Verify(SignedStatement signed, string? builtInKeyPem = null, IEnumerable<string>? firstPartyIds = null, bool allowFirstParty = true)
    {
        if (signed.Statement.Length is 0 or > MaxStatementBytes) throw new CatalogException(Strings.Get("plugin.statement.bad"));
        var builtIn = builtInKeyPem ?? CatalogSignature.PublicKeyPem;
        PluginOrigin origin;
        string fingerprint;
        if (CatalogSignature.Verify(signed.Statement, signed.Signature, builtIn, CatalogSignature.PluginContext))
        {
            if (!allowFirstParty) throw new CatalogException(Strings.Get("catalog.source.officialOnly"));
            origin = PluginOrigin.FirstParty;
            fingerprint = Fingerprint(builtIn);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(signed.PublisherKeyPem)) throw new CatalogException(Strings.Get("plugin.statement.badSignature"));
            try
            {
                fingerprint = Fingerprint(signed.PublisherKeyPem);
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            {
                throw new CatalogException(Strings.Get("plugin.statement.badSignature"));
            }
            // CouchHUD's own key never signs as a "third party", and a third party cannot borrow it.
            if (fingerprint == Fingerprint(builtIn)) throw new CatalogException(Strings.Get("plugin.statement.badSignature"));
            if (!CatalogSignature.Verify(signed.Statement, signed.Signature, signed.PublisherKeyPem, CatalogSignature.PluginContext))
                throw new CatalogException(Strings.Get("plugin.statement.badSignature"));
            origin = PluginOrigin.ThirdParty;
        }

        var statement = Parse(signed.Statement);
        // The statement names its key: a signature by another key (even a valid one) does not make it this publisher's package.
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(statement.PublisherKey), Encoding.ASCII.GetBytes(fingerprint)))
            throw new CatalogException(Strings.Get("plugin.statement.keyMismatch"));
        if (PluginPolicy.DeclaredMessage(origin, statement.Capabilities) is { } capProblem) throw new CatalogException(capProblem);
        if (origin == PluginOrigin.ThirdParty && (PluginNames.IsReserved(statement.Id, firstPartyIds) || PluginNames.IsReserved(statement.Folder, firstPartyIds)))
            throw new CatalogException(Strings.Get("plugin.statement.reserved"));
        return new VerifiedStatement(statement, origin, fingerprint);
    }

    /// <summary>Strict parsing: every field checked, unknown fields (a price, a licence key) refused, the licence must be free.</summary>
    internal static PackageStatement Parse(byte[] bytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Bad();
            foreach (var p in root.EnumerateObject())
            {
                if (!Fields.Contains(p.Name)) throw new CatalogException(Strings.Get(FreeLicenses.IsPaidMarker(p.Name) ? "plugin.statement.notFree" : "plugin.statement.bad"));
            }
            var id = Text(root, "id");
            var folder = Text(root, "folder");
            var version = Text(root, "version");
            var sha = Text(root, "sha256");
            var key = Text(root, "publisherKey");
            var license = Text(root, "license");
            var min = Text(root, "minCouchHud");
            if (id is null || !IdPattern.IsMatch(id) || folder is null || !FolderPattern.IsMatch(folder) || !SemVer.TryParse(version, out _)
                || sha is null || !HashPattern.IsMatch(sha) || key is null || !HashPattern.IsMatch(key)
                || (min is not null && !SemVer.TryParse(min, out _))
                || !root.TryGetProperty("sdk", out var sdkEl) || sdkEl.ValueKind != JsonValueKind.Number || !sdkEl.TryGetInt32(out var sdk) || sdk < 1)
                throw Bad();
            if (license is null || !FreeLicenses.IsFree(license)) throw new CatalogException(Strings.Get("plugin.statement.notFree"));
            var caps = new List<string>();
            if (root.TryGetProperty("capabilities", out var c))
            {
                if (c.ValueKind != JsonValueKind.Array) throw Bad();
                foreach (var item in c.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || caps.Count >= 32) throw Bad();
                    var cap = item.GetString()!;
                    if (!caps.Contains(cap, StringComparer.Ordinal)) caps.Add(cap);
                }
            }
            var source = Text(root, "source");
            if (source is not null && !CatalogParser.IsAllowedUrl(source, false)) throw Bad();
            var author = Text(root, "author");
            if (author is { Length: > 100 }) throw Bad();
            var name = Text(root, "name");
            var summary = Text(root, "summary");
            var description = Text(root, "description");
            var changelog = Text(root, "changelog");
            if (name is { Length: > 60 } || summary is { Length: > 160 } || description is { Length: > 4000 } || changelog is { Length: > 4000 }) throw Bad();
            var icon = root.TryGetProperty("icon", out var iconEl) ? Media(iconEl, PackageMedia.MaxIconBytes) : null;
            var shots = new List<PackageMedia>();
            if (root.TryGetProperty("screenshots", out var shotsEl))
            {
                if (shotsEl.ValueKind != JsonValueKind.Array || shotsEl.GetArrayLength() > PackageMedia.MaxScreenshots) throw Bad();
                foreach (var s in shotsEl.EnumerateArray()) shots.Add(Media(s, PackageMedia.MaxScreenshotBytes));
            }
            long? size = null;
            if (root.TryGetProperty("size", out var sizeEl))
            {
                if (sizeEl.ValueKind != JsonValueKind.Number || !sizeEl.TryGetInt64(out var sz) || sz <= 0 || sz > CatalogService.MaxPackageBytes) throw Bad();
                size = sz;
            }
            return new PackageStatement(id, folder, version!, sha, sdk, min, caps, key, license, source, author)
            {
                Name = name, Summary = summary, Description = description, Icon = icon, Screenshots = shots, Changelog = changelog, Size = size
            };
        }
        catch (JsonException)
        {
            throw Bad();
        }

        static CatalogException Bad() => new(Strings.Get("plugin.statement.bad"));

        static PackageMedia Media(JsonElement e, long max)
        {
            if (e.ValueKind != JsonValueKind.Object || Text(e, "sha256") is not { } h || !HashPattern.IsMatch(h)
                || !e.TryGetProperty("bytes", out var b) || b.ValueKind != JsonValueKind.Number || !b.TryGetInt64(out var n) || n <= 0 || n > max)
                throw Bad();
            return new PackageMedia(h, n);
        }
    }

    /// <summary>The statement belongs to this package: same id, folder, version and file hash (the catalog entry may be null for a .zip).</summary>
    public static void CheckMatches(VerifiedStatement verified, string zipSha256, CatalogEntry? entry = null)
    {
        var s = verified.Statement;
        if (!s.Sha256.Equals(zipSha256, StringComparison.OrdinalIgnoreCase)) throw new CatalogException(Strings.Get("plugin.statement.mismatch"));
        if (entry is null) return;
        if (!s.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase) || !s.Folder.Equals(entry.Folder, StringComparison.OrdinalIgnoreCase)
            || s.Version != entry.Version || !s.Sha256.Equals(entry.Sha256 ?? "", StringComparison.OrdinalIgnoreCase))
            throw new CatalogException(Strings.Get("plugin.statement.mismatch"));
    }

    /// <summary>
    /// Against what is installed: the key pinned for this plugin id must be the same (a change is refused, never re-pinned; the way out is
    /// to remove the plugin and install it again, with the warning again), no version below one installed before (downgrade), and the
    /// folder must not belong to another plugin id. Revocation is checked by the caller with the index's list.
    /// </summary>
    public static void CheckAgainstInstalled(VerifiedStatement verified, PluginTrust trust)
    {
        var s = verified.Statement;
        foreach (var (_, entry) in trust.ForId(s.Id))
        {
            if (entry.KeyFingerprint is null)
            {
                // Installed by hand or from before statements: only CouchHUD's own key may take that over (an update of its plugin).
                if (verified.Origin != PluginOrigin.FirstParty) throw new CatalogException(Strings.Get("plugin.statement.keyChanged"));
            }
            else if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(entry.KeyFingerprint), Encoding.ASCII.GetBytes(verified.KeyFingerprint)))
            {
                throw new CatalogException(Strings.Get("plugin.statement.keyChanged"));
            }
            if (SemVer.TryParse(entry.HighestVersion ?? entry.Version, out var have) && SemVer.TryParse(s.Version, out var offered) && offered < have)
                throw new CatalogException(Strings.Get("plugin.statement.downgrade"));
        }
        if (trust.Entry(s.Folder) is { } byFolder && byFolder.PluginId is { } other && !other.Equals(s.Id, StringComparison.OrdinalIgnoreCase))
            throw new CatalogException(Strings.Get("plugin.statement.keyChanged"));
        if (trust.Entry(s.Folder) is { PluginId: null, KeyFingerprint: null } && verified.Origin != PluginOrigin.FirstParty)
            throw new CatalogException(Strings.Get("plugin.statement.keyChanged"));
    }

    /// <summary>SHA-256 of a file, lower-case hex.</summary>
    public static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}

/// <summary>
/// Names a third-party developer may not use: CouchHUD's own plugins, anything starting with "couchhud", and names that only look
/// different (0/o, 1/l/i, rn/m, separators …).
/// </summary>
public static class PluginNames
{
    /// <summary>
    /// CouchHUD's own plugin names, fixed in the program: reserved for third parties also when the official index is not loaded or cached
    /// (the index adds the ids it lists).
    /// </summary>
    public static readonly IReadOnlyList<string> Reserved = ["casehud", "wand", "couchhud", "nordvpn", "vpn", "homeassistant", "home-assistant", "stardust", "tvcontrol", "webostv"];

    /// <summary>The look of a name: lower case, separators gone, look-alike letters folded.</summary>
    public static string Skeleton(string name)
    {
        var s = new StringBuilder();
        foreach (var ch in name.ToLowerInvariant())
        {
            if (ch > 127) return "\u0000"; // non-ASCII never passes as anything
            var c = ch switch
            {
                '0' => 'o',
                '1' or 'i' or '|' or '!' => 'l',
                '3' => 'e',
                '4' or '@' => 'a',
                '5' or '$' => 's',
                '7' => 't',
                '8' => 'b',
                _ => ch
            };
            if (char.IsAsciiLetterOrDigit(c)) s.Append(c);
        }
        return s.ToString().Replace("rn", "m").Replace("vv", "w").Replace("cl", "d");
    }

    public static bool IsReserved(string name, IEnumerable<string>? firstPartyIds = null)
    {
        var skeleton = Skeleton(name);
        if (skeleton.Length == 0 || skeleton.Contains('\u0000')) return true;
        if (skeleton.StartsWith(Skeleton("couchhud"), StringComparison.Ordinal)) return true;
        foreach (var reserved in Reserved.Concat(firstPartyIds ?? []))
        {
            var r = Skeleton(reserved);
            if (skeleton == r) return true;
            // "casehud-pro", "my_casehud": a reserved name inside counts too.
            if (r.Length >= 5 && skeleton.Contains(r, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}

/// <summary>
/// CouchHUD and every add-on stay free of charge: only free (OSI-approved) licences, and no paid markers at all.
/// </summary>
public static class FreeLicenses
{
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "MIT", "MIT-0", "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "0BSD", "ISC", "Zlib", "Unlicense", "MPL-2.0", "EUPL-1.2",
        "GPL-2.0-only", "GPL-2.0-or-later", "GPL-3.0-only", "GPL-3.0-or-later", "LGPL-2.1-only", "LGPL-2.1-or-later",
        "LGPL-3.0-only", "LGPL-3.0-or-later", "AGPL-3.0-only", "AGPL-3.0-or-later", "MS-PL", "BSL-1.0", "Artistic-2.0"
    };

    private static readonly HashSet<string> Exceptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "Classpath-exception-2.0", "LLVM-exception", "GCC-exception-3.1"
    };

    /// <summary>Keys that say money is involved: a statement or catalog entry with any of them is refused.</summary>
    private static readonly string[] PaidMarkers =
        ["price", "paid", "purchase", "inapppurchase", "subscription", "licensekey", "trial", "cost", "payment", "premium", "donationrequired"];

    public static bool IsPaidMarker(string key)
    {
        var k = key.Replace("_", "").Replace("-", "").ToLowerInvariant();
        return PaidMarkers.Any(m => k.Contains(m, StringComparison.Ordinal));
    }

    /// <summary>An SPDX id from the list, or an expression of them with OR, AND and WITH (a known exception); nothing else.</summary>
    public static bool IsFree(string? spdx)
    {
        if (string.IsNullOrWhiteSpace(spdx) || spdx.Length > 200) return false;
        var tokens = spdx.Replace("(", " ").Replace(")", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var expectLicense = true;
        for (var i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i];
            if (expectLicense)
            {
                if (!Allowed.Contains(t)) return false;
                expectLicense = false;
            }
            else if (t is "OR" or "AND") expectLicense = true;
            else if (t == "WITH")
            {
                if (++i >= tokens.Length || !Exceptions.Contains(tokens[i])) return false;
            }
            else return false;
        }
        return !expectLicense;
    }
}
