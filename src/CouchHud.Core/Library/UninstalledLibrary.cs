namespace CouchHud.Core.Library;

/// <summary>One page of the not installed games, with the totals the page needs for its title and filter.</summary>
public sealed record UninstalledPage(int Total, int Offset, IReadOnlyList<UninstalledGame> Items, IReadOnlyDictionary<string, int> Counts, bool Scanning, long? ScannedAt);

/// <summary>
/// The not installed games of all sources, kept in memory and handed out page by page: a library with thousands of owned games
/// never travels to the UI at once. Scans run on the thread pool with a time limit per source; a slow or broken source is skipped
/// for this round, the others still show.
/// </summary>
public sealed class UninstalledLibrary(IEnumerable<IOwnedGamesSource> sources, Func<DateTimeOffset>? now = null)
{
    public const int MaxPageSize = 120;

    /// <summary>At most this many games in the list (all sources together); far beyond any real library.</summary>
    public const int MaxGames = 20_000;

    /// <summary>A new scan at most this often; asking again earlier gives the list as it stands.</summary>
    public TimeSpan MinRefreshInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Longest time one source may take (appinfo.vdf of a big library is the slow one).</summary>
    public TimeSpan SourceTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A worker that is still busy this long after its start (despite the cancel) is logged once; no new scan starts until it ends.</summary>
    public TimeSpan StuckAfter { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Where the stuck note goes (the console by default; tests listen).</summary>
    public Action<string> Log { get; init; } = line => Console.Error.WriteLine(line);

    private readonly List<IOwnedGamesSource> _sources = [.. sources];
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    private readonly object _lock = new();
    private readonly SemaphoreSlim _scan = new(1, 1);
    private IReadOnlyList<UninstalledGame> _all = [];
    private long? _scannedAt;
    private volatile bool _scanning;

    public bool Scanning => _scanning;

    /// <summary>When the last complete scan ended (Unix ms); null before the first one.</summary>
    public long? ScannedAt
    {
        get
        {
            lock (_lock) return _scannedAt;
        }
    }

    public IReadOnlyList<UninstalledGame> All
    {
        get
        {
            lock (_lock) return _all;
        }
    }

    public UninstalledGame? Find(string id)
    {
        lock (_lock) return _all.FirstOrDefault(g => g.Id == id);
    }

    /// <summary>
    /// Reads all sources again and keeps what is not installed (<paramref name="installedIds"/>: the ids of the library). A scan that
    /// is still running (also one whose source overran its time and is still being cancelled) is not started twice, and a new scan
    /// comes at most every <see cref="MinRefreshInterval"/>; in both cases the caller gets the list as it stands.
    /// </summary>
    public async Task<IReadOnlyList<UninstalledGame>> RefreshAsync(IEnumerable<string> installedIds, CancellationToken ct = default)
    {
        var installed = installedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (ScannedAt is { } last && _now().ToUnixTimeMilliseconds() - last < MinRefreshInterval.TotalMilliseconds) return All;
        if (!await _scan.WaitAsync(0, ct)) return All;
        _scanning = true;
        var workers = new List<Task>();
        try
        {
            var results = await Task.WhenAll(_sources.Select(s => ScanOne(s, workers, ct)).ToList());
            var merged = results.SelectMany(r => r)
                .Where(g => !installed.Contains(g.Id))
                .GroupBy(g => g.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.Id, StringComparer.Ordinal)
                .Take(MaxGames)
                .ToList();
            lock (_lock)
            {
                _all = merged;
                _scannedAt = _now().ToUnixTimeMilliseconds();
            }
            return merged;
        }
        finally
        {
            _scanning = false;
            // The next scan may start only when every worker of this one has really ended (a source that overran its time is
            // cancelled, but its thread may still be reading): never two readers of the same big file side by side.
            _ = Task.WhenAll(workers).ContinueWith(_ => _scan.Release(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    /// <summary>A game was installed (or showed up in the library): it leaves the list at once, without a new scan.</summary>
    public void Forget(IEnumerable<string> installedIds)
    {
        var gone = installedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            if (_all.Any(g => gone.Contains(g.Id))) _all = _all.Where(g => !gone.Contains(g.Id)).ToList();
        }
    }

    /// <summary>One page, filtered by source ("steam", "epic", null for all) and a search in the names.</summary>
    public UninstalledPage Page(int offset, int limit, string? source, string? query)
    {
        IReadOnlyList<UninstalledGame> all;
        long? at;
        lock (_lock)
        {
            all = _all;
            at = _scannedAt;
        }
        var counts = all.GroupBy(g => g.Source).ToDictionary(g => g.Key, g => g.Count());
        IEnumerable<UninstalledGame> filtered = all;
        if (!string.IsNullOrEmpty(source)) filtered = filtered.Where(g => g.Source == source);
        var q = query?.Trim();
        if (!string.IsNullOrEmpty(q)) filtered = filtered.Where(g => g.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase));
        var list = filtered as IList<UninstalledGame> ?? filtered.ToList();
        limit = Math.Clamp(limit, 1, MaxPageSize);
        offset = Math.Clamp(offset, 0, Math.Max(0, list.Count - 1));
        return new UninstalledPage(list.Count, offset, list.Skip(offset).Take(limit).ToList(), counts, _scanning, at);
    }

    private async Task<IReadOnlyList<UninstalledGame>> ScanOne(IOwnedGamesSource source, List<Task> workers, CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(SourceTimeout);
        // Off the caller's thread: the files can be large and the UI thread must never wait for them. The worker gets the token and
        // stops at its next check; the token source lives until the worker has ended.
        var worker = Task.Run(() => source.Scan(cts.Token), CancellationToken.None);
        _ = worker.ContinueWith(_ => cts.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        lock (workers) workers.Add(worker);
        // A reader stuck in one long call ignores its token; that blocks every later scan, so it is worth one line in the log.
        _ = Task.WhenAny(worker, Task.Delay(StuckAfter, CancellationToken.None)).ContinueWith(_ =>
        {
            if (!worker.IsCompleted) Log($"[uninstalled] {source.Name}: scan still running after {StuckAfter.TotalSeconds:0}s, no new scan until it ends");
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            return await worker.WaitAsync(SourceTimeout, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var reason = ex is TimeoutException or OperationCanceledException ? $"no answer within {SourceTimeout.TotalSeconds:0}s" : ex.GetType().Name;
            Console.Error.WriteLine($"[uninstalled] {source.Name} skipped: {reason}");
            return [];
        }
    }
}
