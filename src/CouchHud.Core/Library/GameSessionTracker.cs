using CouchHud.Core.Guard;
using CouchHud.Sdk;

namespace CouchHud.Core.Library;

/// <summary>What the tracker needs to know about the machine; implemented with the Win32 API in the shell.</summary>
public interface IProcessProbe
{
    /// <summary>Running processes with their executable path (null where Windows won't tell us).</summary>
    IReadOnlyList<(int Pid, string? Path)> Snapshot();

    /// <summary>The app id Steam reports as running, 0 if none.</summary>
    int SteamRunningAppId();

    /// <summary>Process id to parent process id, to follow what a game's launcher starts. Empty where unknown.</summary>
    IReadOnlyDictionary<int, int> ParentMap() => new Dictionary<int, int>();

    /// <summary>Process id to creation time (FILETIME ticks): with the image it tells a process from a later one that got the same id. Empty where unknown.</summary>
    IReadOnlyDictionary<int, long> StartTimes() => new Dictionary<int, long>();
}

/// <param name="External">Found running without a start through CouchHud (Steam's own client, a launcher, a start before CouchHud): the launch checks did not see it.</param>
/// <param name="Stamps">Image and start time of the processes when they were seen: what ending the game checks against (a pid alone may belong to something else by then).</param>
public sealed record GameSession(LibraryItem Item, DateTimeOffset Started, IReadOnlyList<int> Pids, bool External = false, IReadOnlyDictionary<int, ProcessStamp>? Stamps = null)
{
    public IReadOnlyDictionary<int, ProcessStamp> StampsOrEmpty => Stamps ?? new Dictionary<int, ProcessStamp>();

    public double Minutes(DateTimeOffset now) => Math.Max(0, (now - Started).TotalMinutes);
}

/// <summary>
/// Knows which launched game is running, for how long, and which processes
/// belong to it. Steam games are recognised through Steam's own "running app"
/// value (only while Steam itself runs); everything else through processes inside the install folder
/// and, once seen, everything those start (a launcher that hands over to the real game and ends).
/// Call <see cref="Tick"/> every few seconds (any thread; the state is guarded, the events are raised outside the lock).
/// </summary>
public sealed class GameSessionTracker(IProcessProbe probe)
{
    /// <summary>A launch that never shows a process within this time is given up.</summary>
    public TimeSpan StartGrace { get; init; } = TimeSpan.FromMinutes(3);

    private readonly object _gate = new();
    private LibraryItem? _pending;
    private DateTimeOffset _pendingSince;
    private GameSession? _current;
    private IReadOnlyList<GameSession> _others = [];
    // Processes of the current session with their image path: a pid that comes back with another path is a reused one.
    private Dictionary<int, ProcessStamp> _known = [];
    // Since when only Steam vouches for the game (no process of it is left).
    private DateTimeOffset? _steamOnlySince;

    /// <summary>Steam still names the app but no process of the game is left: believed this long, then the entry ends.</summary>
    public TimeSpan SteamOnlyGrace { get; init; } = TimeSpan.FromSeconds(45);

    public GameSession? Current
    {
        get { lock (_gate) return _current; }
    }

    /// <summary>
    /// When set, other library games that run (started outside CouchHud, before it, or next to the current one)
    /// are found too: the first one becomes the session if there is none, the rest are listed in <see cref="Others"/>.
    /// </summary>
    public Func<IReadOnlyCollection<LibraryItem>?>? Library { get; set; }

    public IReadOnlyList<GameSession> Others
    {
        get { lock (_gate) return _others; }
    }

    /// <summary>CouchHud can tell when this entry runs (Steam id or an install folder to look for processes in).</summary>
    public static bool CanDetect(LibraryItem item) => SteamStore.AppIdOf(item) is not null || InstallRoot(item) is not null;

    /// <summary>Raised when a game shows up as running.</summary>
    public event Action<GameSession>? Started;

    /// <summary>Raised when the running game is gone; carries the played minutes.</summary>
    public event Action<GameSession, double>? Ended;

    /// <summary>Raised when another library game shows up next to the current one (started outside CouchHud).</summary>
    public event Action<GameSession>? OtherStarted;

    /// <summary>Raised when such another game is gone (not when it became the current session).</summary>
    public event Action<GameSession>? OtherEnded;

    public void Launched(LibraryItem item, DateTimeOffset now)
    {
        lock (_gate)
        {
            _pending = item;
            _pendingSince = now;
        }
    }

    /// <summary>A launch was started and its game has not shown up yet (within <see cref="StartGrace"/>).</summary>
    public bool IsPending(DateTimeOffset now)
    {
        lock (_gate) return _current is null && _pending is not null && now - _pendingSince <= StartGrace;
    }

    public void Tick(DateTimeOffset now)
    {
        var items = Library?.Invoke();
        bool need;
        lock (_gate) need = _current is not null || _pending is not null;
        if (!need && items is null)
        {
            lock (_gate) _others = [];
            return;
        }
        // The slow part (a look at every process) happens outside the lock: the UI thread asks the tracker too.
        var snapshot = probe.Snapshot();
        var parents = probe.ParentMap();
        var starts = probe.StartTimes();
        var steam = probe.SteamRunningAppId();
        var raise = new List<Action>();
        lock (_gate)
        {
            TickCurrent(now, snapshot, parents, starts, steam, raise);
            ScanOthers(now, items, snapshot, parents, starts, steam, raise);
        }
        foreach (var action in raise) action();
    }

    private void ScanOthers(DateTimeOffset now, IReadOnlyCollection<LibraryItem>? items, IReadOnlyList<(int Pid, string? Path)> snapshot,
        IReadOnlyDictionary<int, int> parents, IReadOnlyDictionary<int, long> starts, int steam, List<Action> raise)
    {
        var before = _others;
        if (items is null)
        {
            _others = [];
            foreach (var gone in before) raise.Add(() => OtherEnded?.Invoke(gone));
            return;
        }
        var found = new List<GameSession>();
        var foundKnown = new Dictionary<string, Dictionary<int, ProcessStamp>>();
        foreach (var item in items.Where(i => i.Type == ItemType.Game))
        {
            if (_current is { } cur && (cur.Item.Id == item.Id || SameGame(cur.Item, item))) continue;
            if (found.Any(f => SameGame(f.Item, item))) continue;
            var (pids, known) = FindPids(item, snapshot, parents, starts, steam, null);
            if (pids.Count == 0) continue;
            var earlier = before.FirstOrDefault(b => b.Item.Id == item.Id);
            found.Add(new GameSession(item, earlier?.Started ?? now, pids, External: true, Stamps: known));
            foundKnown[item.Id] = known;
        }
        foreach (var gone in before.Where(b => !found.Any(f => f.Item.Id == b.Item.Id))) raise.Add(() => OtherEnded?.Invoke(gone));
        if (_current is null && _pending is null && found.Count > 0)
        {
            var adopted = found[0];
            found.RemoveAt(0);
            _current = adopted;
            _known = foundKnown[adopted.Item.Id];
            raise.Add(() => Started?.Invoke(adopted));
        }
        _others = found;
        foreach (var fresh in found.Where(f => !before.Any(b => b.Item.Id == f.Item.Id))) raise.Add(() => OtherStarted?.Invoke(fresh));
    }

    private static bool SameGame(LibraryItem a, LibraryItem b) => Parental.ParentalService.LimitKey(a) == Parental.ParentalService.LimitKey(b);

    private void TickCurrent(DateTimeOffset now, IReadOnlyList<(int Pid, string? Path)> snapshot, IReadOnlyDictionary<int, int> parents, IReadOnlyDictionary<int, long> starts, int steam, List<Action> raise)
    {
        if (_current is { } session)
        {
            var (pids, known) = FindPids(session.Item, snapshot, parents, starts, steam, _known);
            // Only Steam's word is left although real processes of the game were seen: Steam lets go of the value within moments
            // after a game ends, one that stays is stale (a crashed client) and must not keep the entry on "running".
            var steamOnly = pids.Count > 0 && pids.All(p => p == 0) && _known.Count > 0;
            if (steamOnly) _steamOnlySince ??= now;
            else _steamOnlySince = null;
            if (steamOnly && now - _steamOnlySince >= SteamOnlyGrace) pids = [];
            if (pids.Count == 0)
            {
                _current = null;
                _known = [];
                _steamOnlySince = null;
                raise.Add(() => Ended?.Invoke(session, session.Minutes(now)));
            }
            else
            {
                if (!steamOnly) _known = known;
                _current = session with { Pids = pids, Stamps = _known };
            }
            return;
        }

        if (_pending is not { } pending) return;
        var (found, foundKnown) = FindPids(pending, snapshot, parents, starts, steam, null);
        if (found.Count > 0)
        {
            var started = new GameSession(pending, now, found, Stamps: foundKnown);
            _current = started;
            _known = foundKnown;
            _pending = null;
            raise.Add(() => Started?.Invoke(started));
        }
        else if (now - _pendingSince > StartGrace)
        {
            _pending = null;
        }
    }

    /// <summary>Steam's "running app" value is stale after a crash or a power cut: only believed while steam.exe runs.</summary>
    private static bool SteamAlive(IReadOnlyList<(int Pid, string? Path)> snapshot) =>
        snapshot.Any(p => p.Path is not null && string.Equals(Path.GetFileName(p.Path), "steam.exe", StringComparison.OrdinalIgnoreCase));

    private static (List<int> Pids, Dictionary<int, ProcessStamp> Known) FindPids(LibraryItem item, IReadOnlyList<(int Pid, string? Path)> snapshot,
        IReadOnlyDictionary<int, int> parents, IReadOnlyDictionary<int, long> starts, int steamApp, Dictionary<int, ProcessStamp>? known)
    {
        var steamId = SteamStore.AppIdOf(item);
        var steamRunning = steamId is not null && steamApp.ToString() == steamId && SteamAlive(snapshot);

        var dir = InstallRoot(item);
        var set = new Dictionary<int, ProcessStamp>();
        ProcessStamp Stamp(int pid, string? path) => new(path, starts.TryGetValue(pid, out var t) ? t : 0);
        if (dir is not null)
        {
            foreach (var p in snapshot.Where(p => p.Path is not null && p.Path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)
                && !SafeTermination.IsSystemPath(p.Path, WindowsDir))) set[p.Pid] = Stamp(p.Pid, p.Path);
        }
        // Seen before and still there with the same image: a launcher in the folder that handed over to the real game
        // (Java, a runtime elsewhere) and ended is no reason to stop counting.
        if (known is { Count: > 0 })
        {
            foreach (var p in snapshot)
            {
                // Same image and same start: a pid that came back with another start is a different process.
                if (known.TryGetValue(p.Pid, out var was) && string.Equals(was.Path, p.Path, StringComparison.OrdinalIgnoreCase)
                    && was.Start == Stamp(p.Pid, p.Path).Start) set[p.Pid] = was;
            }
        }
        // Everything those start belongs to the game, however deep.
        if (set.Count > 0 && parents.Count > 0)
        {
            for (var added = true; added;)
            {
                added = false;
                foreach (var p in snapshot)
                {
                    if (set.ContainsKey(p.Pid) || !parents.TryGetValue(p.Pid, out var parent) || parent == p.Pid || !set.TryGetValue(parent, out var parentStamp)) continue;
                    // A child is never older than its parent: otherwise the parent id belongs to a process that reused the number.
                    var child = Stamp(p.Pid, p.Path);
                    if (parentStamp.Start > 0 && child.Start > 0 && child.Start < parentStamp.Start) continue;
                    // Windows' own programs that a game starts (cmd, conhost, werfault) are not part of the game.
                    if (SafeTermination.IsSystemPath(p.Path, WindowsDir)) continue;
                    set[p.Pid] = child;
                    added = true;
                }
            }
        }
        var pids = set.Keys.ToList();
        // Steam knows a game runs even when we can't see its process path (anti-cheat, elevation).
        if (steamRunning && pids.Count == 0) pids.Add(0);
        return (pids, set);
    }

    private static string WindowsDir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>The install folder with a trailing separator, so "C:\Games\Foo" never matches "C:\Games\Foobar".</summary>
    internal static string? InstallRoot(LibraryItem item)
    {
        var dir = item.InstallDir;
        if (string.IsNullOrWhiteSpace(dir)) return null;
        // Never treat a whole drive or Windows' own folders (System32 …) as a game folder: every system process would belong to it.
        var full = Path.GetFullPath(dir).TrimEnd('\\', '/');
        if (full.Length <= 3 || full.Equals(WindowsDir, StringComparison.OrdinalIgnoreCase)
            || SafeTermination.IsSystemPath(full + Path.DirectorySeparatorChar, WindowsDir)) return null;
        // Nor the program folders themselves: that would be every installed program.
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.UserProfile })
        {
            var special = Environment.GetFolderPath(folder).TrimEnd('\\', '/');
            if (special.Length > 0 && full.Equals(special, StringComparison.OrdinalIgnoreCase)) return null;
        }
        return full + Path.DirectorySeparatorChar;
    }
}
