using System.IO.Compression;
using System.Text.Json;
using CouchHud.Core.Localization;
using CouchHud.Core.Plugins;
using CouchHud.Sdk;

namespace CouchHud.Core.Updates;

/// <summary>What the automatic update does with one offered update.</summary>
public enum AutoUpdateVerdict
{
    /// <summary>Not for the automatic update: it keeps asking (warning and the parents' code), as a manual update does.</summary>
    Ask,

    /// <summary>It would apply by itself, but the new version needs capabilities the installed one does not have: only a notice, the person decides.</summary>
    NeedsPermission,

    /// <summary>Downloaded, verified like a manual update and staged for the next start.</summary>
    Apply
}

/// <summary>Everything the decision looks at, taken from the loaded catalog entry, its verified statement and the trust store (no I/O in the decision).</summary>
/// <param name="StatementOrigin">The origin of the verified statement; null for an entry without one or one that did not verify.</param>
/// <param name="InstalledOrigin">The origin recorded for the installed plugin in the trust store (null: unknown).</param>
/// <param name="InstalledCapabilities">What the installed version was approved with; null for one from before the capabilities (the SDK 2 surface).</param>
/// <param name="OfferedCapabilities">What the signed statement of the new version declares.</param>
/// <param name="SkinInUse">The skin is the household's or a profile's current one: it is never swapped under the person's eyes.</param>
/// <param name="HighestSeen">The highest version ever installed (trust store): the offer must be above it as well.</param>
public sealed record AutoUpdateCandidate(
    CatalogKind Kind,
    bool Extra,
    bool Unsigned,
    bool OfficialSource,
    PluginOrigin? StatementOrigin,
    PluginOrigin? InstalledOrigin,
    string? InstalledVersion,
    string? HighestSeen,
    string OfferedVersion,
    string? MinCouchHud,
    int? Sdk,
    SemVer App,
    IReadOnlyCollection<string>? InstalledCapabilities,
    IReadOnlyCollection<string>? OfferedCapabilities,
    bool SkinInUse = false);

/// <summary>
/// The rules of the automatic update of plugins and skins (Settings > Privacy), as pure decisions. Automatic only for what CouchHUD itself
/// signed (the official catalog, a statement with CouchHUD's own key for a plugin; the signed official index for a skin), only for a strictly
/// newer version, only for what this CouchHUD can load, and only when the new version asks for nothing the installed one was not approved
/// with. Everything else keeps asking.
/// </summary>
public static class AutoUpdateRules
{
    public static AutoUpdateVerdict Decide(AutoUpdateCandidate c)
    {
        // Another catalog, an unsigned index, a different source: never by itself.
        if (c.Extra || c.Unsigned || !c.OfficialSource) return AutoUpdateVerdict.Ask;
        // Strictly newer than what is installed and than anything installed before (no downgrade, no re-install of the same version).
        if (!SemVer.TryParse(c.OfferedVersion, out var offered) || !SemVer.TryParse(c.InstalledVersion, out var have) || offered <= have) return AutoUpdateVerdict.Ask;
        if (c.HighestSeen is not null && (!SemVer.TryParse(c.HighestSeen, out var highest) || offered <= highest)) return AutoUpdateVerdict.Ask;
        // Fits this CouchHUD: a missing minimum is fine, an unreadable one is not; the SDK must be one that is implemented here.
        if (c.MinCouchHud is not null && (!SemVer.TryParse(c.MinCouchHud, out var min) || min > c.App)) return AutoUpdateVerdict.Ask;
        if (CatalogRules.NeedsNewerSdk(c.Sdk)) return AutoUpdateVerdict.Ask;

        // A pre-release is never taken by itself from a release.
        if (offered.Pre.Length > 0 && have.Pre.Length == 0) return AutoUpdateVerdict.Ask;

        // A skin is styles and sounds only (no code, no capabilities) from the signed official index; one in use is not swapped.
        if (c.Kind == CatalogKind.Skin) return c.SkinInUse ? AutoUpdateVerdict.Ask : AutoUpdateVerdict.Apply;

        // A plugin: CouchHUD's own key on the statement, and the installed plugin is CouchHUD's own as well.
        if (c.StatementOrigin != PluginOrigin.FirstParty || c.InstalledOrigin != PluginOrigin.FirstParty) return AutoUpdateVerdict.Ask;
        if (c.OfferedCapabilities is null) return AutoUpdateVerdict.Ask;
        return CapabilityCompare.Added(c.InstalledCapabilities ?? PluginPolicy.Legacy, c.OfferedCapabilities).Count == 0
            ? AutoUpdateVerdict.Apply
            : AutoUpdateVerdict.NeedsPermission;
    }

    /// <summary>Is this skin folder the one any of these skin ids (household and profiles, as saved) points to? Moved skins are mapped like the page does.</summary>
    public static bool IsSkinInUse(string folder, IEnumerable<string?> skinIds) =>
        skinIds.Any(id => id is not null && string.Equals(CouchHud.Core.Skins.SkinCatalog.MigrateSkinId(id), CouchHud.Core.Skins.SkinCatalog.UserId(folder), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The install's own gate in the automatic case, asked once the statement checked out: only an update (never a first install) from
    /// CouchHUD's own catalog without new capabilities, and for a plugin only the very statement that was judged (same hash and version),
    /// signed with CouchHUD's key. Anything else is refused, so a list that changed between the decision and the install cannot slip through.
    /// </summary>
    public static bool Approves(PluginInstallRequest request, VerifiedStatement? judged) =>
        !request.Entry.Extra && !request.AddsCapabilities && request.IsUpdate
        && (request.Entry.Kind == CatalogKind.Skin
            || (judged is not null && request.Statement is { } now && now.Origin == PluginOrigin.FirstParty
                && now.Statement.Sha256.Equals(judged.Statement.Sha256, StringComparison.OrdinalIgnoreCase)
                && now.Statement.Version == judged.Statement.Version));

    /// <summary>
    /// The downloaded package against its signed statement: the plugin.json inside must not declare more than the statement (a hidden
    /// escalation), must be the same plugin id, and must not ask for a newer SDK. Throws a <see cref="CatalogException"/>; nothing is
    /// installed then. A package without plugin.json declares nothing, which is fine.
    /// </summary>
    public static void CheckPackageAgainstStatement(Stream zip, PackageStatement statement)
    {
        var manifest = ReadManifest(zip);
        if (manifest is null) return;
        var problem = ManifestProblem(manifest, statement);
        if (problem is not null) throw new CatalogException(Strings.Get("autoupdate.manifestMismatch", problem));
    }

    /// <summary>What is wrong with the manifest against the statement, or null. Pure.</summary>
    public static string? ManifestProblem(PluginManifest manifest, PackageStatement statement)
    {
        if (manifest.Id is { Length: > 0 } id && !id.Equals(statement.Id, StringComparison.OrdinalIgnoreCase)) return "id";
        if (manifest.Sdk > statement.Sdk) return "sdk";
        var more = CapabilityCompare.Added(statement.Capabilities, manifest.Capabilities ?? []);
        return more.Count > 0 ? string.Join(", ", more) : null;
    }

    private const int MaxManifestBytes = 64 * 1024;

    /// <summary>plugin.json at the root of the package or inside its one top folder; null when there is none or it cannot be read (then it declares nothing).</summary>
    public static PluginManifest? ReadManifest(Stream zip)
    {
        zip.Position = 0;
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        var files = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
        var tops = files.Select(e => e.FullName.Replace('\\', '/').Split('/')).Select(p => p.Length > 1 ? p[0] : "").Distinct().ToList();
        var prefix = tops.Count == 1 && tops[0].Length > 0 ? tops[0] + "/" : "";
        // Names that try to leave the package, and more than one plugin.json (or one differing only in case): which one counts would be a guess.
        foreach (var f in files)
        {
            var name = f.FullName.Replace('\\', '/');
            if (name.StartsWith('/') || name.Contains(':') || name.Split('/').Any(part => part == ".." || part == ".")) throw new CatalogException(Strings.Get("autoupdate.manifestMismatch", "path"));
        }
        var matches = files.Where(e => e.FullName.Replace('\\', '/').Equals(prefix + "plugin.json", StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count > 1) throw new CatalogException(Strings.Get("autoupdate.manifestMismatch", "plugin.json"));
        var entry = matches.FirstOrDefault();
        if (entry is null) return null;
        if (entry.Length > MaxManifestBytes) throw new CatalogException(Strings.Get("autoupdate.manifestMismatch", "size"));
        try
        {
            using var stream = entry.Open();
            using var limited = new MemoryStream();
            var buffer = new byte[4096];
            int n;
            while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (limited.Length + n > MaxManifestBytes) throw new CatalogException(Strings.Get("autoupdate.manifestMismatch", "size"));
                limited.Write(buffer, 0, n);
            }
            return JsonSerializer.Deserialize<PluginManifest>(limited.ToArray(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            // A manifest the host cannot read declares nothing; the signed statement counts.
            return null;
        }
    }
}

/// <summary>Compares capability lists: what an update asks for beyond what the installed version has.</summary>
public static class CapabilityCompare
{
    /// <summary>
    /// The capabilities of <paramref name="offered"/> that <paramref name="installed"/> does not cover. Exact, case-sensitive names (a
    /// renamed one is a new one, an unknown one counts as new). One exception, in the direction of less: network:internet ("the internet or
    /// the home network") covers network:lan, while network:lan never covers network:internet.
    /// </summary>
    public static IReadOnlyList<string> Added(IEnumerable<string> installed, IEnumerable<string> offered)
    {
        var have = new HashSet<string>(installed.Where(c => c is not null), StringComparer.Ordinal);
        var added = new List<string>();
        foreach (var c in offered.Where(c => c is not null).Distinct(StringComparer.Ordinal))
        {
            if (!PluginCapabilities.All.Contains(c, StringComparer.Ordinal)) { added.Add(c); continue; }
            if (have.Contains(c)) continue;
            if (c == PluginCapabilities.NetworkLan && have.Contains(PluginCapabilities.NetworkInternet)) continue;
            added.Add(c);
        }
        return added;
    }
}

/// <summary>What one package of an automatic pass came to.</summary>
public enum AutoStep { Skipped, Applied, NeedsPermission }

/// <summary>
/// The loop of the automatic pass, apart from the host: one package after the other, the guard asked again before each, and an unexpected
/// failure of one package logged and passed over, never losing what the others already did.
/// </summary>
public static class AutoUpdateBatch
{
    public static async Task<(List<T> Applied, List<T> NeedsPermission)> RunAsync<T>(IEnumerable<T> items, Func<bool> allowed, Func<T, Task<AutoStep>> step, Action<T, Exception>? log = null)
    {
        var applied = new List<T>();
        var permission = new List<T>();
        foreach (var item in items)
        {
            if (!allowed()) break;
            try
            {
                switch (await step(item))
                {
                    case AutoStep.Applied: applied.Add(item); break;
                    case AutoStep.NeedsPermission: permission.Add(item); break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log?.Invoke(item, ex);
            }
        }
        return (applied, permission);
    }
}
