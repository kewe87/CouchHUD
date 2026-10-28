using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using CouchHud.Sdk;

namespace CouchHud.Core.Library;

/// <summary>One item of Epic's catalog cache.</summary>
/// <param name="AppName">The app name of the Windows release (what the launcher installs); null without one.</param>
public sealed record EpicCatalogEntry(string Id, string? Namespace, string? Title, string? AppName, bool IsGame, bool IsAddOn, Artwork Art);

/// <summary>
/// Artwork the game launchers already keep on this PC, found without any lookup and without a login.
/// Everything here is read-only and tolerant: a missing or unexpected file simply means no art.
/// </summary>
public static class LauncherArtwork
{
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".webp"];

    // ----- Epic -----

    public static string DefaultEpicCatalog { get; } = Path.Combine(
        Environment.GetEnvironmentVariable("ProgramData") ?? @"C:\ProgramData",
        "Epic", "EpicGamesLauncher", "Data", "Catalog", "catcache.bin");

    /// <summary>
    /// The launcher's catalog cache (base64 of a JSON list) lists every owned game with its key images.
    /// Result: catalog item id to art (https addresses on Epic's CDN; they go through the image cache).
    /// </summary>
    public static Dictionary<string, Artwork> ReadEpicCatalog(string? file = null)
    {
        var result = new Dictionary<string, Artwork>(StringComparer.Ordinal);
        foreach (var e in ReadEpicCatalogEntries(file))
        {
            if (e.Art.Cover.Count + e.Art.Hero.Count + e.Art.Logo.Count > 0) result.TryAdd(e.Id, e.Art);
        }
        return result;
    }

    /// <summary>
    /// All items of the launcher's catalog cache: id, namespace, title, the app name of the Windows release, whether it is a game or
    /// an add-on, and its key images. Somebody else's file in a folder every user may write: size-limited, parsed once per version,
    /// a broken file gives an empty list, never an exception.
    /// </summary>
    public static IReadOnlyList<EpicCatalogEntry> ReadEpicCatalogEntries(string? file = null)
    {
        try
        {
            file ??= DefaultEpicCatalog;
            var info = new FileInfo(file);
            if (!info.Exists) return [];
            // Somebody else's file: never read an absurd one, and parse each version of it only once.
            if (info.Length > MaxCatalogBytes)
            {
                Console.Error.WriteLine($"[epic] catalog cache is {info.Length / (1024 * 1024)} MB, ignored");
                return [];
            }
            lock (CatalogMemo)
            {
                if (CatalogMemo.TryGetValue(file, out var memo) && memo.Written == info.LastWriteTimeUtc && memo.Size == info.Length)
                    return memo.Result;
            }
            var text = File.ReadAllText(file).Trim();
            var json = text.StartsWith('[') ? text : Encoding.UTF8.GetString(Convert.FromBase64String(text));
            using var doc = JsonDocument.Parse(json);
            var result = new List<EpicCatalogEntry>();
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var id = Str(entry, "id");
                if (id is null) continue;
                var byType = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (entry.TryGetProperty("keyImages", out var images) && images.ValueKind == JsonValueKind.Array)
                {
                    foreach (var image in images.EnumerateArray())
                    {
                        var type = Str(image, "type");
                        var url = Str(image, "url");
                        if (type is not null && url is not null && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) byType.TryAdd(type, url);
                    }
                }
                IReadOnlyList<string> First(params string[] types) =>
                    types.Select(t => byType.GetValueOrDefault(t)).OfType<string>().Take(1).ToList();
                var art = new Artwork
                {
                    Cover = First("DieselGameBoxTall", "OfferImageTall", "Thumbnail"),
                    Hero = First("DieselGameBox", "OfferImageWide", "DieselStoreFrontWide"),
                    Logo = First("DieselGameBoxLogo")
                };
                var categories = entry.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array
                    ? cats.EnumerateArray().Select(c => Str(c, "path")).OfType<string>().ToList()
                    : [];
                // DLCs point to their game; add-ons carry the category "addons".
                var addOn = entry.TryGetProperty("mainGameItem", out var main) && main.ValueKind == JsonValueKind.Object
                    || categories.Any(c => c.StartsWith("addons", StringComparison.OrdinalIgnoreCase));
                result.Add(new EpicCatalogEntry(id, Str(entry, "namespace"), Str(entry, "title"), WindowsAppName(entry),
                    categories.Contains("games", StringComparer.OrdinalIgnoreCase), addOn, art));
            }
            var frozen = result.ToArray();
            lock (CatalogMemo) CatalogMemo[file] = (info.LastWriteTimeUtc, info.Length, frozen);
            return frozen;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Truncated, rewritten while we read, odd encoding, deeper than the parser allows: nothing from here, never a crash.
            Console.Error.WriteLine($"[epic] catalog cache: {ex.GetType().Name}: {ex.Message}");
            return [];
        }
    }

    /// <summary>The app name of the release for Windows (releaseInfo[].appId with "Windows" in its platforms).</summary>
    private static string? WindowsAppName(JsonElement entry)
    {
        if (!entry.TryGetProperty("releaseInfo", out var releases) || releases.ValueKind != JsonValueKind.Array) return null;
        foreach (var r in releases.EnumerateArray())
        {
            if (r.ValueKind != JsonValueKind.Object) continue;
            var windows = r.TryGetProperty("platform", out var p) && p.ValueKind == JsonValueKind.Array
                && p.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && string.Equals(x.GetString(), "Windows", StringComparison.OrdinalIgnoreCase));
            if (windows && Str(r, "appId") is { } app) return app;
        }
        return null;
    }

    /// <summary>The launcher's catalog is megabytes; a file beyond this is not the launcher's.</summary>
    internal const long MaxCatalogBytes = 128L * 1024 * 1024;

    private static readonly Dictionary<string, (DateTime Written, long Size, IReadOnlyList<EpicCatalogEntry> Result)> CatalogMemo = new(StringComparer.OrdinalIgnoreCase);

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ----- Microsoft Store / Xbox -----

    /// <summary>
    /// Logos from a package folder: MicrosoftGame.config (games) and AppxManifest.xml. Wide and splash
    /// images become the banner, square logos the cover and icon. Paths are kept inside the package folder.
    /// </summary>
    public static Artwork FromPackage(string installDir)
    {
        try
        {
            var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in new[] { "MicrosoftGame.config", "AppxManifest.xml" })
            {
                var path = Path.Combine(installDir, name);
                if (!File.Exists(path)) continue;
                if (new FileInfo(path).Length > 2 * 1024 * 1024) continue; // a manifest is a few KB
                // No DTD: nothing from a package file may pull in entities or other files.
                using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                var root = XDocument.Load(reader).Root;
                if (root is null) continue;
                foreach (var element in root.Descendants())
                {
                    if (element.Name.LocalName is not ("ShellVisuals" or "VisualElements" or "DefaultTile")) continue;
                    foreach (var a in element.Attributes()) attrs.TryAdd(a.Name.LocalName, a.Value);
                }
            }

            string? Pick(params string[] keys) =>
                keys.Where(attrs.ContainsKey).Select(k => ResolveAsset(installDir, attrs[k])).FirstOrDefault(p => p is not null);
            static IReadOnlyList<string> One(string? p) => p is null ? [] : [new Uri(p).AbsoluteUri];

            return new Artwork
            {
                Cover = One(Pick("Square480x480Logo", "Square310x310Logo", "Square150x150Logo", "StoreLogo")),
                Hero = One(Pick("SplashScreenImage", "Wide310x150Logo")),
                Icon = One(Pick("Square150x150Logo", "Square480x480Logo", "Square44x44Logo", "Square71x71Logo"))
            };
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException or ArgumentException or NotSupportedException or UriFormatException)
        {
            return Artwork.Empty;
        }
    }

    /// <summary>
    /// Manifest paths name "Assets\Logo.png" but the files are "Logo.scale-200.png" or similar:
    /// the exact file, else the largest variant next to it. Null for paths that leave the folder.
    /// </summary>
    internal static string? ResolveAsset(string dir, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        var root = Path.GetFullPath(dir).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        var ext = Path.GetExtension(full);
        if (!ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return null;
        if (File.Exists(full)) return full;

        var folder = Path.GetDirectoryName(full);
        if (folder is null || !Directory.Exists(folder)) return null;
        var stem = Path.GetFileNameWithoutExtension(full);
        return Directory.EnumerateFiles(folder, stem + ".*" + ext)
            .Where(f => f.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => new FileInfo(f).Length)
            .FirstOrDefault();
    }

    // ----- Ubisoft Connect -----

    /// <summary>Ubisoft Connect keeps downloaded game images in its cache folder, named with the game's launcher id.</summary>
    public static Artwork FromUbisoftCache(string? launcherDir, string launcherId)
    {
        try
        {
            var assets = launcherDir is null ? null : Path.Combine(launcherDir, "cache", "assets");
            if (assets is null || !Directory.Exists(assets) || launcherId.Length == 0) return Artwork.Empty;
            var files = Directory.EnumerateFiles(assets, $"*{launcherId}*")
                .Where(f => ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderByDescending(f => new FileInfo(f).Length)
                .Select(f => new Uri(f).AbsoluteUri)
                .ToList();
            return files.Count == 0 ? Artwork.Empty : new Artwork { Cover = files.Take(1).ToList() };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Artwork.Empty;
        }
    }
}
