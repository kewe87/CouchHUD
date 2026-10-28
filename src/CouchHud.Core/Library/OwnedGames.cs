using CouchHud.Core.Platform;
using CouchHud.Sdk;

namespace CouchHud.Core.Library;

/// <summary>A game the store knows for this account that is not installed on this PC, with the link that asks the store to install it.</summary>
/// <param name="Id">The id the game gets in the library once it is installed ("steam:620", "epic:AppName").</param>
/// <param name="InstallUrl">Built only by <see cref="InstallLinks"/> from checked ids.</param>
public sealed record UninstalledGame(string Id, string Source, string Name, string InstallUrl, Artwork Art);

/// <summary>One store's not installed games, read from what its client keeps on this PC.</summary>
public interface IOwnedGamesSource
{
    string Name { get; }

    IReadOnlyList<UninstalledGame> Scan(CancellationToken ct);
}

/// <summary>
/// Steam games of the signed-in account that are not installed, from Steam's own files only (no web API, no key, no login):
/// <list type="bullet">
/// <item><c>userdata\&lt;account id&gt;\config\localconfig.vdf</c>, section <c>UserLocalConfigStore › Software › Valve › Steam › apps</c>:
/// Steam keeps an entry with <c>LastPlayed</c> / <c>Playtime</c> for every app the account has played. Only entries with one of the two
/// count; that is "played at some point", a strong sign of a licence, not the full list of everything owned (games never started are
/// missing, a free weekend or a refunded game may show up; Steam then shows its store page instead of the install dialog).</item>
/// <item><c>appcache\appinfo.vdf</c> (<see cref="SteamAppInfo"/>) for name and type; only type "game" is listed, an app without a known
/// name is left out rather than shown with a number.</item>
/// </list>
/// The licence cache (<c>appcache\licensecache</c>) is encrypted and undocumented, so it is not used. Correctness before completeness.
/// </summary>
public sealed class SteamOwnedGames(IRegistry registry, string? steamPath = null) : IOwnedGamesSource
{
    /// <summary>localconfig.vdf is a few MB even for large accounts.</summary>
    internal const long MaxLocalConfigBytes = 64L * 1024 * 1024;

    /// <summary>At most this many played apps are looked up (far beyond any real account).</summary>
    internal const int MaxApps = UninstalledLibrary.MaxGames;

    public string Name => "Steam";

    public IReadOnlyList<UninstalledGame> Scan(CancellationToken ct)
    {
        var root = steamPath ?? SteamScanner.FindSteamRoot(registry);
        if (root is null) return [];
        var config = LocalConfigFile(root, registry);
        if (config is null) return [];
        var played = PlayedAppIds(config);
        if (played.Count == 0) return [];
        ct.ThrowIfCancellationRequested();
        var names = SteamAppInfo.Read(Path.Combine(root, "appcache", "appinfo.vdf"), played, ct: ct);
        var list = new List<UninstalledGame>();
        foreach (var (appId, info) in names)
        {
            var id = appId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!info.Type.Equals("game", StringComparison.OrdinalIgnoreCase)) continue;
            if (SteamScanner.IgnoredAppIds.Contains(id)) continue;
            if (SteamScanner.IgnoredNameParts.Any(p => info.Name.Contains(p, StringComparison.OrdinalIgnoreCase))) continue;
            if (InstallLinks.Steam(id) is not { } url) continue;
            list.Add(new UninstalledGame($"steam:{id}", "steam", info.Name, url, SteamScanner.ArtworkFor(root, id)));
        }
        return list;
    }

    /// <summary>
    /// The account's localconfig.vdf: the account Steam reports as signed in (<c>HKCU\Software\Valve\Steam\ActiveProcess › ActiveUser</c>),
    /// else the one whose file was written last. loginusers.vdf (account names) is not read. Folder names must be plain numbers.
    /// </summary>
    internal static string? LocalConfigFile(string root, IRegistry registry)
    {
        var userdata = Path.Combine(root, "userdata");
        if (!Directory.Exists(userdata)) return null;
        string FileOf(string account) => Path.Combine(userdata, account, "config", "localconfig.vdf");
        var active = registry.ReadDword(@"HKCU\Software\Valve\Steam\ActiveProcess", "ActiveUser");
        if (active is > 0)
        {
            var file = FileOf(((uint)active.Value).ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (File.Exists(file)) return file;
        }
        try
        {
            return Directory.EnumerateDirectories(userdata)
                .Select(Path.GetFileName)
                .Where(n => n is { Length: > 0 and <= 10 } && n.All(char.IsAsciiDigit) && n != "0")
                .Select(n => new FileInfo(FileOf(n!)))
                .Where(f => f.Exists)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>App ids with a play record in localconfig.vdf; an unreadable, oversized or broken file gives none.</summary>
    internal static HashSet<uint> PlayedAppIds(string file)
    {
        var result = new HashSet<uint>();
        try
        {
            if (!File.Exists(file)) return result;
            // The limit holds for the read itself (the file may grow after any check of its length).
            return BoundedFile.ReadAllText(file, MaxLocalConfigBytes) is { } text ? PlayedAppIdsFromText(text) : result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            Console.Error.WriteLine($"[steam] localconfig.vdf: {ex.GetType().Name}");
            return result;
        }
    }

    internal static HashSet<uint> PlayedAppIdsFromText(string text)
    {
        var result = new HashSet<uint>();
        var apps = Vdf.Parse(text).GetObject("UserLocalConfigStore")?.GetObject("Software")?.GetObject("Valve")?.GetObject("Steam")?.GetObject("apps");
        if (apps is null) return result;
        foreach (var (key, value) in apps)
        {
            if (value is not VdfObject app || !InstallLinks.IsSteamAppId(key)) continue;
            static bool Positive(string? v) => long.TryParse(v, out var n) && n > 0;
            if (Positive(app.GetString("LastPlayed")) || Positive(app.GetString("Playtime"))) result.Add(uint.Parse(key, System.Globalization.CultureInfo.InvariantCulture));
            if (result.Count >= MaxApps) break;
        }
        return result;
    }
}

/// <summary>
/// Epic games of the account that are not installed, from the launcher's catalog cache (<c>Data\Catalog\catcache.bin</c>, see
/// <see cref="LauncherArtwork.ReadEpicCatalogEntries"/>). The launcher fills it with the catalog items of the account's library; the same
/// file is used for owned games by other open-source tools (e.g. Rai Pal, github.com/Raicuparta/rai-pal, backend/core/src/game_providers/
/// epic_provider.rs). Only items in the category "games", not add-ons, with a Windows release are listed.
/// </summary>
public sealed class EpicOwnedGames(string? catalogFile = null) : IOwnedGamesSource
{
    public string Name => "Epic Games";

    public IReadOnlyList<UninstalledGame> Scan(CancellationToken ct)
    {
        var list = new List<UninstalledGame>();
        foreach (var e in LauncherArtwork.ReadEpicCatalogEntries(catalogFile))
        {
            ct.ThrowIfCancellationRequested();
            if (!e.IsGame || e.IsAddOn || e.AppName is null || string.IsNullOrWhiteSpace(e.Title)) continue;
            if (InstallLinks.Epic(e.Namespace, e.Id, e.AppName) is not { } url) continue;
            list.Add(new UninstalledGame($"epic:{e.AppName}", "epic", SteamAppInfo.Clean(e.Title), url, e.Art));
            if (list.Count >= UninstalledLibrary.MaxGames) break;
        }
        return list;
    }
}
