using CouchHud.Core.Platform;
using CouchHud.Sdk;

namespace CouchHud.Core.Library;

public sealed class SteamScanner(IRegistry registry, string? steamPath = null) : ILibraryProvider, IGameStore
{
    // Things Steam installs that are not games.
    internal static readonly HashSet<string> IgnoredAppIds =
    [
        "228980", // Steamworks Common Redistributables
        "1070560", "1391110", "1628350", // Steam Linux Runtime
        "1493710", "1826330", "2180100", "2805730", "3658110" // Proton
    ];

    internal static readonly string[] IgnoredNameParts = ["redistributable", "steamvr", "proton"];

    public const string Cdn = "https://shared.cloudflare.steamstatic.com/store_item_assets/steam/apps";

    public string Name => "Steam";
    public string Source => "steam";

    public Task<IReadOnlyList<LibraryItem>> GetItemsAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Scan(cancellationToken), cancellationToken);

    private IReadOnlyList<LibraryItem> Scan(CancellationToken ct)
    {
        var root = steamPath ?? FindSteamPath();
        if (root is null) return [];

        var items = new List<LibraryItem>();
        foreach (var library in ReadLibraryFolders(root))
        {
            ct.ThrowIfCancellationRequested();
            var appsDir = Path.Combine(library, "steamapps");
            if (!Directory.Exists(appsDir)) continue;

            foreach (var file in Directory.EnumerateFiles(appsDir, "appmanifest_*.acf"))
            {
                try
                {
                    var m = ParseManifest(File.ReadAllText(file));
                    if (m is null || IgnoredAppIds.Contains(m.Value.AppId)) continue;
                    if (IgnoredNameParts.Any(p => m.Value.Name.Contains(p, StringComparison.OrdinalIgnoreCase))) continue;

                    items.Add(new LibraryItem
                    {
                        Id = $"steam:{m.Value.AppId}",
                        Source = "steam",
                        Name = m.Value.Name,
                        LastPlayed = m.Value.LastPlayed,
                        InstallDir = string.IsNullOrEmpty(m.Value.InstallDir) ? null : Path.Combine(appsDir, "common", m.Value.InstallDir),
                        SizeOnDisk = m.Value.SizeOnDisk,
                        Launch = new LaunchTarget(LaunchKind.Url, $"steam://rungameid/{m.Value.AppId}"),
                        Art = ArtworkFor(root, m.Value.AppId)
                    });
                }
                catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"[steam] {Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }
        return items;
    }

    /// <summary>Games Steam is downloading, updating or has queued, with progress.</summary>
    public IReadOnlyList<SteamDownload> Downloads() => DownloadsAndInstalled().Downloads;

    /// <summary>
    /// One pass over the manifests: what Steam is downloading, and the app ids that are fully installed (the downloads page uses
    /// those to tell a finished installation from a cancelled one). This runs every few seconds while something loads, so it is
    /// bounded: folders on network shares (UNC paths and mapped network drives, also Steam's own folder) are left out, at most
    /// <see cref="MaxPolledManifests"/> manifests, each read only up to <see cref="MaxManifestBytes"/>.
    /// </summary>
    public (IReadOnlyList<SteamDownload> Downloads, IReadOnlySet<string> Installed) DownloadsAndInstalled()
    {
        var installed = new HashSet<string>(StringComparer.Ordinal);
        var root = steamPath ?? FindSteamPath();
        // Steam itself on a network drive: the poll leaves it alone (the library scan still reads it).
        if (root is null || OnNetwork(root)) return ([], installed);
        var list = new List<SteamDownload>();
        var seen = 0;
        foreach (var library in ReadLibraryFolders(root))
        {
            // A share that hangs would hold up every poll; Steam does not install there anyway.
            if (OnNetwork(library)) continue;
            var appsDir = Path.Combine(library, "steamapps");
            IEnumerator<string>? files = null;
            try
            {
                if (!Directory.Exists(appsDir)) continue;
                files = Directory.EnumerateFiles(appsDir, "appmanifest_*.acf").GetEnumerator();
                // MoveNext reads the folder and can fail as well (folder removed or locked while polling).
                while (files.MoveNext())
                {
                    if (++seen > MaxPolledManifests) break;
                    var file = files.Current;
                    try
                    {
                        // A manifest is a few KB; anything far bigger is not Steam's. The limit holds for the read itself.
                        if (BoundedFile.ReadAllText(file, MaxManifestBytes) is not { } text) continue;
                        if (ParseDownload(text) is { } d && !IgnoredAppIds.Contains(d.AppId)) list.Add(d);
                        else if (ParseManifest(text) is { } m) installed.Add(m.AppId);
                    }
                    catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
                    {
                        Console.Error.WriteLine($"[steam] {Path.GetFileName(file)}: {ex.GetType().Name}");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Console.Error.WriteLine($"[steam] library folder: {ex.GetType().Name}");
            }
            finally
            {
                files?.Dispose();
            }
            if (seen > MaxPolledManifests) break;
        }
        return (list.OrderBy(d => d.State == SteamDownloadState.Downloading ? 0 : d.State == SteamDownloadState.Paused ? 1 : 2).ThenBy(d => d.Name).ToList(), installed);
    }

    /// <summary>
    /// A UNC path or a path on a mapped network drive. When Windows cannot tell, the folder counts as local (the poll then still has
    /// the size and count limits).
    /// </summary>
    internal static bool OnNetwork(string path)
    {
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return true;
        try
        {
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    internal const int MaxPolledManifests = 5000;
    internal const long MaxManifestBytes = 1024 * 1024;

    // AppState.StateFlags bits as Steam writes them.
    private const int UpdateRequired = 2, FullyInstalled = 4, UpdateRunning = 256, UpdatePaused = 512, UpdateStarted = 1024, Downloading = 1048576, Staging = 2097152, Committing = 4194304;

    /// <summary>A manifest as a download entry, or null when the game is simply installed.</summary>
    internal static SteamDownload? ParseDownload(string text)
    {
        var state = Vdf.Parse(text).GetObject("AppState");
        var appId = state?.GetString("appid");
        var name = state?.GetString("name");
        if (state is null || string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(name)) return null;
        // Both end up on the screen and in notifications: a plain app id and a clean, short name.
        if (!InstallLinks.IsSteamAppId(appId)) return null;
        name = SteamAppInfo.Clean(name);
        if (name.Length == 0) return null;
        if (!int.TryParse(state.GetString("StateFlags"), out var flags)) return null;
        long Num(string key) => long.TryParse(state.GetString(key), out var v) ? v : 0;
        var toDownload = Num("BytesToDownload");
        var downloaded = Num("BytesDownloaded");

        var busy = (flags & (UpdateRunning | Downloading | Staging | Committing)) != 0;
        var paused = (flags & UpdatePaused) != 0;
        var needsUpdate = (flags & UpdateRequired) != 0 || (flags & UpdateStarted) != 0 || (flags & FullyInstalled) == 0;
        if (!busy && !paused && !needsUpdate) return null;

        var stateKind = busy ? SteamDownloadState.Downloading : paused ? SteamDownloadState.Paused : SteamDownloadState.Queued;
        var progress = toDownload > 0 ? Math.Clamp((double)downloaded / toDownload, 0, 1) : 0;
        return new SteamDownload(appId, name, stateKind, progress, downloaded, toDownload, (flags & FullyInstalled) != 0);
    }

    private string? FindSteamPath() => FindSteamRoot(registry);

    /// <summary>Steam's folder: the registry's SteamPath, else the usual install folders; null without Steam.</summary>
    internal static string? FindSteamRoot(IRegistry registry)
    {
        var candidates = new[]
        {
            registry.ReadString(@"HKCU\Software\Valve\Steam", "SteamPath"),
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam"
        };
        return candidates
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => Path.GetFullPath(p!))
            .FirstOrDefault(p => Directory.Exists(Path.Combine(p, "steamapps")));
    }

    internal static IReadOnlyList<string> ReadLibraryFolders(string steamPath)
    {
        var folders = new List<string> { steamPath };
        var file = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(file)) return folders;

        try
        {
            var root = Vdf.Parse(File.ReadAllText(file)).GetObject("libraryfolders");
            if (root is null) return folders;
            foreach (var (key, entry) in root)
            {
                if (!key.All(char.IsDigit)) continue; // skip "contentstatsid" etc.
                // New format: "0" { "path" "..." }, old format: "1" "D:\SteamLibrary"
                var path = entry is VdfObject o ? o.GetString("path") : entry as string;
                if (string.IsNullOrEmpty(path)) continue;
                var full = Path.GetFullPath(path);
                if (!folders.Contains(full, StringComparer.OrdinalIgnoreCase)) folders.Add(full);
            }
        }
        catch (FormatException ex)
        {
            Console.Error.WriteLine($"[steam] libraryfolders.vdf: {ex.Message}");
        }
        return folders;
    }

    internal static (string AppId, string Name, long LastPlayed, string? InstallDir, long SizeOnDisk)? ParseManifest(string text)
    {
        var state = Vdf.Parse(text).GetObject("AppState");
        var appId = state?.GetString("appid");
        var name = state?.GetString("name");
        if (state is null || string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(name)) return null;

        // StateFlags bit 4 = fully installed; skip anything mid-download or uninstalling.
        if (long.TryParse(state.GetString("StateFlags"), out var flags) && flags != 0 && (flags & 4) == 0) return null;

        long.TryParse(state.GetString("LastPlayed"), out var lastPlayedSeconds);
        long.TryParse(state.GetString("SizeOnDisk"), out var size);
        return (appId, name, lastPlayedSeconds * 1000, state.GetString("installdir"), size);
    }

    /// <summary>Local library cache first (works offline), CDN as fallback.</summary>
    internal static Artwork ArtworkFor(string steamPath, string appId)
    {
        var cache = Path.Combine(steamPath, "appcache", "librarycache");
        IReadOnlyList<string> Pick(string file)
        {
            var local = new[] { Path.Combine(cache, $"{appId}_{file}"), Path.Combine(cache, appId, file) }
                .Where(File.Exists)
                .Select(p => new Uri(p).AbsoluteUri);
            return [.. local, $"{Cdn}/{appId}/{file}"];
        }
        return new Artwork
        {
            Cover = Pick("library_600x900.jpg"),
            Hero = Pick("library_hero.jpg"),
            Logo = Pick("logo.png")
        };
    }
}
