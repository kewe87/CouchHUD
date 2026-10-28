using CouchHud.Core.Guard;
using CouchHud.Core.Library;
using CouchHud.Core.Localization;
using CouchHud.Core.Platform;
using CouchHud.Sdk;
using CouchHud.Shell.Services;
using CouchHud.Shell.UiHost;

namespace CouchHud.Shell;

/// <summary>
/// The downloads page (everything that loads or installs, in one place) and the not installed games of Steam and Epic with their
/// install through the store's own link. The rules (parsing, list, states) are in Core: <see cref="DownloadCenter"/>,
/// <see cref="UninstalledLibrary"/>, <see cref="InstallLinks"/>.
/// </summary>
public sealed partial class Hud
{
    private readonly DownloadCenter _downloads = new();
    private UninstalledLibrary? _uninstalledLibrary;
    private int _uninstalledArtRunning;
    /// <summary>The downloads page is open: look more often (until this time).</summary>
    private DateTimeOffset _downloadsWatchedUntil = DateTimeOffset.MinValue;

    private UninstalledLibrary Uninstalled => _uninstalledLibrary ??= new UninstalledLibrary([new SteamOwnedGames(new WindowsRegistry()), new EpicOwnedGames()]);

    /// <summary>The fixed ids of CouchHUD's own activities in the list.</summary>
    private static class DownloadIds
    {
        public const string Windows = "update:windows";
        public const string Apps = "update:apps";
        public const string Self = "couchhud:self";
        public static string Plugin(string kind, string id) => $"catalog:{kind}:{id}";
    }

    private void StartDownloads()
    {
        _downloads.Changed += () => Ui(() => Emit("downloads", DownloadsDto()));
        _downloads.Finished += OnDownloadFinished;
        _ = Task.Run(DownloadPollLoopAsync);
    }

    private object DownloadsDto()
    {
        // With Gaming switched off the page keeps CouchHUD's and Windows' own downloads, the game lines wait hidden.
        if (Areas.Games) return new { entries = _downloads.Entries(), summary = _downloads.Summary() };
        var entries = _downloads.Entries().Where(e => e.Kind != DownloadKind.Game).ToList();
        return new { entries, summary = _downloads.Summary(e => e.Kind != DownloadKind.Game) };
    }

    /// <summary>The list and the badge. The page asks every few seconds while it is open; that also makes the host look more often.</summary>
    public object GetDownloadCenter()
    {
        _downloadsWatchedUntil = DateTimeOffset.UtcNow.AddSeconds(15);
        return DownloadsDto();
    }

    /// <summary>Removes a finished line, or a request the user cancelled in the client; null clears all finished lines.</summary>
    public object DismissDownload(string? id)
    {
        _downloads.Dismiss(id);
        return DownloadsDto();
    }

    /// <summary>
    /// Steam's manifests every 30 s (every 5 s while something loads, a request waits or the page is open), Epic's only while a request
    /// waits for it. Reading a few hundred small files: on the thread pool, never on the UI thread.
    /// </summary>
    private async Task DownloadPollLoopAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(20));
        while (true)
        {
            try
            {
                PollDownloads();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[downloads] poll failed: {ex.GetType().Name}: {ex.Message}");
            }
            var busy = _downloads.Summary().Active > 0 || _downloads.HasRequests || DateTimeOffset.UtcNow < _downloadsWatchedUntil;
            await Task.Delay(busy ? TimeSpan.FromSeconds(5) : TimeSpan.FromSeconds(30));
        }
    }

    private void PollDownloads()
    {
        // Game downloads belong to Gaming (FeatureAreas "gameDownloads"): nothing read while it is switched off.
        if (!Areas.Runs("gameDownloads")) return;
        var (steam, steamInstalled) = new SteamScanner(new WindowsRegistry()).DownloadsAndInstalled();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        static string AppIdOf(string id) => id.StartsWith("steam:", StringComparison.Ordinal) ? id["steam:".Length..] : "";
        _downloads.ApplySnapshot("steam", steam.Select(d => DownloadCenter.FromSteam(d, now)), id => steamInstalled.Contains(AppIdOf(id)));
        if (!_downloads.HasRequests)
        {
            _downloads.CheckRequests(_ => false);
            return;
        }
        var epic = EpicScanner.InstallState();
        _downloads.CheckRequests(
            id => id.StartsWith("steam:", StringComparison.Ordinal) ? steamInstalled.Contains(AppIdOf(id)) : epic.Installed.Contains(id),
            id => epic.Incomplete.Contains(id));
    }

    /// <summary>
    /// A game that finished gets a notification and goes into the library (a rescan), out of the not installed list. CouchHUD's own
    /// activities already end with their own message (toast); their line stays on the page as done or failed.
    /// </summary>
    private void OnDownloadFinished(DownloadEntry entry)
    {
        if (entry.Kind != DownloadKind.Game || entry.State != DownloadState.Done || !Areas.Games) return;
        Ui(() => PostNotification(Strings.Ref("downloads.app"), Strings.Ref(entry.IsUpdate ? "downloads.updated" : "downloads.installed", entry.Name),
            Strings.Ref(entry.IsUpdate ? "downloads.updatedBody" : "downloads.installedBody")));
        if (entry.IsUpdate) return;
        _uninstalledLibrary?.Forget([entry.Id]);
        _ = library.ScanAsync().ContinueWith(_ => Ui(() => Emit("library", UiBridge.ToDto(Shown(library.Items)))), TaskScheduler.Default);
    }

    /// <summary>
    /// Cancels a running download of CouchHUD's own (today: its setup, before the hand-over to the service). The bridge refuses it on a
    /// locked couch, and it is checked here again. No parents' code: cancelling only leaves CouchHUD on its current version, as not
    /// starting the update would; starting needs no code either. Windows updates and winget are not cancelled from here (see the page).
    /// </summary>
    public object CancelDownload(string id)
    {
        if (Locked) throw new LockedException(Strings.Get("lock.unlockFirst"));
        RequireProfileChosen();
        var cancelled = id switch
        {
            DownloadIds.Self => SelfUpdate.CancelDownload(),
            _ => false
        };
        if (!cancelled) throw new InvalidOperationException(Strings.Get("downloads.notCancellable"));
        Console.WriteLine($"[downloads] cancel asked for {id}");
        return DownloadsDto();
    }

    /// <summary>
    /// Opens a client's own download page through its fixed link (<see cref="InstallLinks.ClientDownloads"/>; Steam only), where pausing
    /// and resuming really work. Not on a locked couch (the bridge refuses before, checked here again); with parental controls on only with
    /// the parents' code: the client's window is a way to games outside CouchHUD's rules, as its install dialog is.
    /// </summary>
    public object OpenClientDownloads(string source)
    {
        var url = InstallLinks.ClientDownloads(source) ?? throw new ArgumentException(Strings.Get("downloads.noClientLink"));
        if (Locked) throw new LockedException(Strings.Get("lock.unlockFirst"));
        RequireProfileChosen();
        if (config.Current.ParentalEnabled) RequireParentGrant("openClientDownloads", source);
        var item = new LibraryItem { Id = $"{source}:downloads", Source = source, Name = source, Launch = new LaunchTarget(LaunchKind.Url, url) };
        _fgState.Release(ForegroundPolicy.FamilyOfLaunch(item.Source, item.Launch.Kind, item.Launch.Target), DateTimeOffset.UtcNow);
        StartClientForLaunch(item);
        Launcher.Launch(item);
        Console.WriteLine($"[downloads] opened the download page of {source}");
        return DownloadsDto();
    }

    // -----------------------------------------------------------------------
    // Not installed games

    /// <summary>
    /// One page of the not installed games. Until a scan has ended, and on <paramref name="refresh"/> (not on a locked couch; at most every
    /// 30 s, see <see cref="UninstalledLibrary.MinRefreshInterval"/>), the stores' files are read on the thread pool. A call while a scan
    /// runs gets the list as it stands with <c>scanning</c> set, and the page asks again;
    /// the covers of the page are fetched into the image cache in the background, as for the library (the UI never loads from the net).
    /// </summary>
    public async Task<object> GetUninstalledAsync(int offset, int limit, string? source, string? query, bool refresh)
    {
        if (source is not (null or "" or "steam" or "epic")) throw new ArgumentException(Strings.Get("uninstalled.badSource"));
        if (query is { Length: > 100 }) query = query[..100];
        if ((refresh && !Locked) || Uninstalled.ScannedAt is null) await Uninstalled.RefreshAsync(library.Items.Select(i => i.Id));
        Uninstalled.Forget(library.Items.Select(i => i.Id));
        var page = Uninstalled.Page(offset, limit, source, query);
        _ = FetchUninstalledArtAsync(page.Items);
        return new
        {
            total = page.Total,
            offset = page.Offset,
            items = page.Items.Select(g => new { id = g.Id, source = g.Source, name = g.Name, art = UiBridge.ToDto(g.Art) }),
            counts = page.Counts,
            scanning = page.Scanning,
            scannedAt = page.ScannedAt
        };
    }

    /// <summary>Covers of one page into the image cache, one after the other; the page hears about it and asks again.</summary>
    private async Task FetchUninstalledArtAsync(IReadOnlyList<UninstalledGame> games)
    {
        if (Interlocked.Exchange(ref _uninstalledArtRunning, 1) == 1) return;
        try
        {
            var arrived = await artwork.DownloadMissingAsync(games.Select(g => g.Art.Cover).ToList(), () => Ui(() => Emit("uninstalledArt", null)));
            if (arrived > 0) Ui(() => Emit("uninstalledArt", null));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[uninstalled] covers: {ex.GetType().Name}");
        }
        finally
        {
            Interlocked.Exchange(ref _uninstalledArtRunning, 0);
        }
    }

    /// <summary>
    /// Asks the store's client to install a game, through its fixed link (<see cref="InstallLinks"/>), never anything else. Not on a locked
    /// couch (the bridge refuses before, checked here again), and with parental controls on only with the parents' code for exactly this
    /// game: blocked and age-restricted games stay out of reach of a child, and the client's dialog is not a place where the age limit
    /// could be checked.
    /// </summary>
    public object InstallGame(string id)
    {
        var game = Uninstalled.Find(id) ?? throw new KeyNotFoundException(Strings.Get("uninstalled.unknown"));
        if (Locked) throw new LockedException(Strings.Get("lock.unlockFirst"));
        if (config.Current.ParentalEnabled) RequireParentGrant("installGame", game.Id);
        if (!InstallLinks.IsInstallUrl(game.InstallUrl)) throw new InvalidOperationException(Strings.Get("uninstalled.badLink"));
        var item = new LibraryItem { Id = game.Id, Source = game.Source, Name = game.Name, Launch = new LaunchTarget(LaunchKind.Url, game.InstallUrl) };
        // The client's install dialog has to come to the front (the foreground guard lets it until the user is back in the HUD).
        _fgState.Release(ForegroundPolicy.FamilyOfLaunch(item.Source, item.Launch.Kind, item.Launch.Target), DateTimeOffset.UtcNow);
        StartClientForLaunch(item);
        Launcher.Launch(item);
        Console.WriteLine($"[uninstalled] install asked for {game.Id}");
        _downloads.Request(game.Id, game.Source, game.Name, reportsProgress: game.Source == "steam");
        return DownloadsDto();
    }

    // -----------------------------------------------------------------------
    // CouchHUD's own activities as lines of the page

    /// <summary>
    /// Runs <paramref name="work"/> as a line of the downloads page. The line begins only when the work calls its <c>started</c>
    /// action, i.e. once it holds its own gate: a second call turned away as busy leaves the running line alone. A begun line ends as
    /// done, or as failed with the reason.
    /// </summary>
    private async Task<T> TrackAsync<T>(string id, DownloadKind kind, string source, string name, Func<Action, Task<T>> work, Func<T, bool>? ok = null)
    {
        var begun = false;
        try
        {
            var result = await work(() =>
            {
                begun = true;
                _downloads.Begin(id, kind, source, name);
            });
            if (begun) _downloads.Complete(id, ok?.Invoke(result) ?? true, result as string);
            return result;
        }
        catch (Exception ex) when (begun)
        {
            _downloads.Complete(id, false, ex.Message);
            throw;
        }
    }
}
