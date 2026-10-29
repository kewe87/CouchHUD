namespace CouchHud.Core.Library;

/// <summary>What is loading: a game, an update of Windows or of an app, CouchHUD itself, a plugin or skin.</summary>
public enum DownloadKind
{
    Game,
    Windows,
    App,
    CouchHud,
    Plugin
}

public enum DownloadState
{
    /// <summary>Asked for, the client has not taken it up yet (Steam's install dialog is open).</summary>
    Waiting,
    Downloading,
    Installing,
    Paused,
    /// <summary>In the client's queue (an update Steam has not started).</summary>
    Queued,
    /// <summary>Runs in a client that does not tell its progress (Epic): no percentage, no guess.</summary>
    InClient,
    Done,
    Failed
}

/// <summary>Which package of several a run is at (app updates): "App 1 of 2", the name and what happens to it ("prepare", "download", "install").</summary>
public sealed record UpdateStep(int Index, int Total, string? Name, string Phase);

/// <summary>One line of the downloads page.</summary>
/// <param name="Progress">0..1 when the source really knows it; null otherwise (the page then shows no bar).</param>
/// <param name="Detail">A text of the source (state of a setup, result), already in the UI language; may be null.</param>
/// <param name="Step">An app update with several packages: which one is running now; null for everything else.</param>
/// <param name="Cancellable">The running work can be cancelled right now (CouchHUD's own download, before the hand-over); never a
/// finished line.</param>
public sealed record DownloadEntry(
    string Id,
    DownloadKind Kind,
    string Source,
    string Name,
    DownloadState State,
    double? Progress,
    long BytesDone,
    long BytesTotal,
    bool IsUpdate,
    string? Detail,
    long UpdatedAt,
    bool Cancellable = false,
    UpdateStep? Step = null)
{
    public bool Active => State is DownloadState.Waiting or DownloadState.Downloading or DownloadState.Installing or DownloadState.InClient;

    public bool Finished => State is DownloadState.Done or DownloadState.Failed;
}

/// <summary>The badge: how many things load right now and their common progress (null when none of them knows one).</summary>
public sealed record DownloadSummary(int Active, int Finished, double? Progress);

/// <summary>
/// The central downloads list. It gathers three kinds of things:
/// <list type="bullet">
/// <item>snapshots that are polled from a client (Steam's manifests): a line that disappears after it was seen loading, and whose game
/// is installed now, is finished; one that disappears otherwise was cancelled and simply goes;</item>
/// <item>activities CouchHUD runs itself (Windows Update, winget, its own update, a plugin): begun, reported and ended by the
/// code that runs them;</item>
/// <item>installs the user asked for in a client (a store link): waiting until the client shows them, or, for a client without readable
/// progress, "runs in the client" until the game is installed.</item>
/// </list>
/// Finished lines stay for a day (at most <see cref="MaxFinished"/>) or until the user removes them. Thread-safe; the events come
/// from the thread that made the change.
/// </summary>
public sealed class DownloadCenter(Func<DateTimeOffset>? now = null)
{
    public const int MaxFinished = 20;

    /// <summary>Steam's dialog was not confirmed in this time: the request is dropped without a message.</summary>
    public static readonly TimeSpan WaitingTimeout = TimeSpan.FromMinutes(15);

    /// <summary>A client without progress: after this the line goes (the user may have cancelled in the client).</summary>
    public static readonly TimeSpan InClientTimeout = TimeSpan.FromHours(24);

    public static readonly TimeSpan KeepFinished = TimeSpan.FromHours(24);

    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    private readonly object _lock = new();
    private readonly Dictionary<string, Dictionary<string, DownloadEntry>> _polled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DownloadEntry> _activities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (DownloadEntry Entry, DateTimeOffset Since)> _requested = new(StringComparer.Ordinal);
    private readonly List<DownloadEntry> _finished = [];
    /// <summary>Lines of a polled source that were seen really loading (not just queued): only those may end with a message.</summary>
    private readonly HashSet<string> _seenActive = new(StringComparer.Ordinal);

    /// <summary>Something changed (a new line, progress, an end).</summary>
    public event Action? Changed;

    /// <summary>A line ended (installed, updated, failed). Raised once per line.</summary>
    public event Action<DownloadEntry>? Finished;

    private long Stamp => _now().ToUnixTimeMilliseconds();

    // ---------------------------------------------------------------- polled sources

    /// <summary>
    /// The current lines of one polled source (e.g. "steam"). <paramref name="isInstalled"/> tells for a line that is gone whether its game is
    /// installed now. A request for the same id (<see cref="Request"/>) is taken over by the client's line.
    /// </summary>
    public void ApplySnapshot(string source, IEnumerable<DownloadEntry> entries, Func<string, bool> isInstalled)
    {
        var ended = new List<DownloadEntry>();
        var changed = false;
        lock (_lock)
        {
            var fresh = entries.GroupBy(e => e.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var before = _polled.GetValueOrDefault(source) ?? new Dictionary<string, DownloadEntry>(StringComparer.Ordinal);
            foreach (var (id, old) in before)
            {
                if (fresh.ContainsKey(id)) continue;
                changed = true;
                var wasActive = _seenActive.Remove(id);
                if (wasActive && isInstalled(id)) ended.Add(Finish(old with { State = DownloadState.Done, Progress = 1, Detail = null }));
            }
            foreach (var (id, e) in fresh)
            {
                if (!before.TryGetValue(id, out var old) || old with { UpdatedAt = 0 } != e with { UpdatedAt = 0 }) changed = true;
                if (e.State is DownloadState.Downloading or DownloadState.Installing) _seenActive.Add(id);
                // The client took the request up (Steam's dialog was confirmed): its own line shows it from now on.
                if (_requested.Remove(id))
                {
                    changed = true;
                    _seenActive.Add(id);
                }
            }
            _polled[source] = fresh;
        }
        foreach (var e in ended) Finished?.Invoke(e);
        if (changed || ended.Count > 0) Changed?.Invoke();
    }

    // ---------------------------------------------------------------- own activities

    /// <summary>CouchHUD starts something itself. A second begin of the same id updates the line.</summary>
    public void Begin(string id, DownloadKind kind, string source, string name, bool isUpdate = true, string? detail = null)
    {
        lock (_lock)
        {
            _activities[id] = new DownloadEntry(id, kind, source, name, DownloadState.Downloading, null, 0, 0, isUpdate, detail, Stamp);
            _finished.RemoveAll(f => f.Id == id);
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Progress of an activity; unknown ids are ignored (the activity ended in between). <paramref name="bytesTotal"/> only from the
    /// source's real counters (never a guess such as an update's largest package); 0 clears the bytes, done is kept within 0..total.
    /// </summary>
    public void Report(string id, double? progress = null, string? detail = null, DownloadState? state = null, long? bytesDone = null, long? bytesTotal = null,
        bool? cancellable = null, UpdateStep? step = null, bool unknownProgress = false)
    {
        lock (_lock)
        {
            if (!_activities.TryGetValue(id, out var e)) return;
            _activities[id] = e with
            {
                Progress = progress is { } p ? Math.Clamp(p, 0, 1) : unknownProgress ? null : e.Progress,
                Step = step ?? e.Step,
                Detail = detail ?? e.Detail,
                State = state ?? e.State,
                BytesTotal = bytesTotal is { } t ? Math.Max(0, t) : e.BytesTotal,
                BytesDone = bytesTotal is { } t2 ? (t2 > 0 ? Math.Clamp(bytesDone ?? 0, 0, t2) : 0) : e.BytesDone,
                Cancellable = cancellable ?? e.Cancellable,
                UpdatedAt = Stamp
            };
        }
        Changed?.Invoke();
    }

    /// <summary>The activity ended; <paramref name="ok"/> false keeps it as failed with the reason.</summary>
    public void Complete(string id, bool ok, string? detail = null)
    {
        DownloadEntry? done = null;
        lock (_lock)
        {
            if (!_activities.Remove(id, out var e)) return;
            done = Finish(e with { State = ok ? DownloadState.Done : DownloadState.Failed, Progress = ok ? 1 : e.Progress, Detail = detail ?? e.Detail, Step = null });
        }
        Finished?.Invoke(done);
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- installs asked for in a client

    /// <summary>
    /// The user asked a client to install a game. <paramref name="reportsProgress"/>: the client's progress can be read (Steam), so the line
    /// waits for the client's own line; otherwise it shows "runs in the client" until the game is installed.
    /// </summary>
    public void Request(string id, string source, string name, bool reportsProgress)
    {
        lock (_lock)
        {
            // Already loading in the client: nothing to wait for.
            if (_polled.Values.Any(p => p.ContainsKey(id))) return;
            var state = reportsProgress ? DownloadState.Waiting : DownloadState.InClient;
            _requested[id] = (new DownloadEntry(id, DownloadKind.Game, source, SteamAppInfo.Clean(name), state, null, 0, 0, false, null, Stamp), _now());
            _finished.RemoveAll(f => f.Id == id);
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// A regular look at the requests: installed ones are finished (with a message), ones the client never took up go after their time.
    /// <paramref name="installing"/> marks a client-only request as being installed (e.g. Epic's manifest of an incomplete install).
    /// </summary>
    public void CheckRequests(Func<string, bool> isInstalled, Func<string, bool>? installing = null)
    {
        var ended = new List<DownloadEntry>();
        var changed = false;
        lock (_lock)
        {
            var now = _now();
            foreach (var (id, (entry, since)) in _requested.ToList())
            {
                if (isInstalled(id))
                {
                    _requested.Remove(id);
                    ended.Add(Finish(entry with { State = DownloadState.Done, Progress = 1 }));
                    continue;
                }
                var limit = entry.State == DownloadState.Waiting ? WaitingTimeout : InClientTimeout;
                if (now - since > limit)
                {
                    _requested.Remove(id);
                    changed = true;
                    continue;
                }
                // The client wrote that it installs (no progress in it): "being installed in the client", still without a percentage.
                if (entry.State == DownloadState.InClient && installing?.Invoke(id) == true)
                {
                    _requested[id] = (entry with { State = DownloadState.Installing, UpdatedAt = Stamp }, since);
                    changed = true;
                }
            }
            changed |= PruneFinished(now);
        }
        foreach (var e in ended) Finished?.Invoke(e);
        if (changed || ended.Count > 0) Changed?.Invoke();
    }

    public bool HasRequests
    {
        get
        {
            lock (_lock) return _requested.Count > 0;
        }
    }

    // ---------------------------------------------------------------- the list

    /// <summary>Removes a finished line or a request (the user cancelled it in the client); null removes all finished lines.</summary>
    public bool Dismiss(string? id)
    {
        bool removed;
        lock (_lock)
        {
            if (id is null)
            {
                removed = _finished.Count > 0;
                _finished.Clear();
            }
            else
            {
                removed = _finished.RemoveAll(f => f.Id == id) > 0 | _requested.Remove(id);
            }
        }
        if (removed) Changed?.Invoke();
        return removed;
    }

    /// <summary>Everything, loading first (downloading, installing, waiting, in a client, paused, queued), then finished, newest first.</summary>
    public IReadOnlyList<DownloadEntry> Entries()
    {
        lock (_lock)
        {
            PruneFinished(_now());
            var running = _activities.Values
                .Concat(_requested.Values.Select(r => r.Entry))
                .Concat(_polled.Values.SelectMany(p => p.Values))
                .GroupBy(e => e.Id, StringComparer.Ordinal).Select(g => g.First())
                .OrderBy(e => Rank(e.State)).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase);
            return [.. running, .. _finished.OrderByDescending(f => f.UpdatedAt)];
        }
    }

    /// <param name="shown">Only these lines count (with Gaming switched off the game lines are left out).</param>
    public DownloadSummary Summary(Func<DownloadEntry, bool>? shown = null)
    {
        var list = shown is null ? Entries() : Entries().Where(shown).ToList();
        var active = list.Where(e => e.Active).ToList();
        var known = active.Where(e => e.Progress is not null).ToList();
        return new DownloadSummary(active.Count, list.Count(e => e.Finished), known.Count == 0 ? null : known.Average(e => e.Progress!.Value));
    }

    private static int Rank(DownloadState s) => s switch
    {
        DownloadState.Downloading => 0,
        DownloadState.Installing => 1,
        DownloadState.Waiting => 2,
        DownloadState.InClient => 3,
        DownloadState.Paused => 4,
        DownloadState.Queued => 5,
        _ => 6
    };

    private DownloadEntry Finish(DownloadEntry e)
    {
        var done = e with { UpdatedAt = Stamp, Cancellable = false };
        _finished.RemoveAll(f => f.Id == e.Id);
        _finished.Add(done);
        if (_finished.Count > MaxFinished) _finished.RemoveRange(0, _finished.Count - MaxFinished);
        return done;
    }

    private bool PruneFinished(DateTimeOffset now)
    {
        var limit = (now - KeepFinished).ToUnixTimeMilliseconds();
        return _finished.RemoveAll(f => f.UpdatedAt < limit) > 0;
    }

    /// <summary>A Steam manifest as a line of the page.</summary>
    public static DownloadEntry FromSteam(SteamDownload d, long now) => new(
        $"steam:{d.AppId}", DownloadKind.Game, "steam", SteamAppInfo.Clean(d.Name),
        d.State switch
        {
            SteamDownloadState.Downloading => DownloadState.Downloading,
            SteamDownloadState.Paused => DownloadState.Paused,
            _ => DownloadState.Queued
        },
        d.BytesTotal > 0 ? d.Progress : null, d.BytesDone, d.BytesTotal, d.IsUpdate, null, now);
}
