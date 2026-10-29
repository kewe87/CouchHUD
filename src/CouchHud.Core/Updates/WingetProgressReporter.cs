namespace CouchHud.Core.Updates;

/// <summary>Where an app upgrade stands: package <paramref name="Index"/> of <paramref name="Total"/>, its name (capped), and what winget does with it.</summary>
public sealed record AppUpgradeProgress(int Index, int Total, string Name, WingetPhase Phase, long BytesDone, long BytesTotal, double? Percent);

/// <summary>
/// Feeds winget's output to a <see cref="WingetProgressParser"/> and reports the result, at most four times a second for the same
/// package and phase (a change of package or phase goes out at once). A frame dropped by that limit is sent when the window ends
/// (trailing edge), so the display is never left stale. Names come from the known pending list by id; an unknown id is
/// shown as it is, capped at <see cref="MaxNameLength"/> (never inside a surrogate pair), because winget's text is untrusted. With a
/// single id the total is 1; with all packages it is winget's own counter, else the number of known pending apps.
/// Dispose when the run ends: no report goes out afterwards.
/// </summary>
/// <param name="schedule">Runs the action once after the delay (tests pass their own); by default a timer.</param>
public sealed class WingetProgressReporter(string? singleId, IReadOnlyDictionary<string, string> knownNames, int pendingCount, Action<AppUpgradeProgress> report,
    Func<DateTime>? clock = null, Action<TimeSpan, Action>? schedule = null) : IDisposable
{
    public const int MaxNameLength = 80;
    public static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(250);

    private readonly object _lock = new();
    private readonly WingetProgressParser _parser = new();
    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);
    private (int Index, WingetPhase Phase)? _last;
    private DateTime _lastAt = DateTime.MinValue;
    private AppUpgradeProgress? _pending;
    private bool _timerSet;
    private bool _disposed;
    private Timer? _timer;

    /// <summary>A piece of output; an empty piece means the output ended.</summary>
    public void Feed(ReadOnlySpan<char> chunk)
    {
        lock (_lock)
        {
            if (_disposed) return;
            var changed = chunk.IsEmpty ? _parser.Flush() : _parser.Feed(chunk);
            if (!changed || _parser.Current is not { } c) return;
            var name = knownNames.TryGetValue(c.Id ?? "", out var known) ? known : c.Id ?? "";
            var total = Math.Max(singleId is not null ? 1 : c.Total > 0 ? c.Total : Math.Max(1, pendingCount), c.Index);
            var p = new AppUpgradeProgress(c.Index, total, Cut(name, MaxNameLength), c.Phase, c.BytesDone, c.BytesTotal, c.Percent);
            var now = _clock();
            if (_last == (c.Index, c.Phase) && now - _lastAt < Gap)
            {
                _pending = p;
                if (!_timerSet)
                {
                    _timerSet = true;
                    Schedule(Gap - (now - _lastAt));
                }
                return;
            }
            Send(p, now);
        }
    }

    private void Send(AppUpgradeProgress p, DateTime now)
    {
        _pending = null;
        _last = (p.Index, p.Phase);
        _lastAt = now;
        report(p);
    }

    private void Schedule(TimeSpan delay)
    {
        if (schedule is not null) schedule(delay, Tick);
        else _timer = new Timer(_ => Tick(), null, delay < TimeSpan.Zero ? TimeSpan.Zero : delay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The window ended: the latest dropped state goes out now.</summary>
    private void Tick()
    {
        lock (_lock)
        {
            _timerSet = false;
            if (_disposed || _pending is not { } p) return;
            // On a timer thread nothing may escape: an exception there would end the process.
            try { Send(p, _clock()); } catch { }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _pending = null;
            _timer?.Dispose();
        }
    }

    /// <summary>At most <paramref name="max"/> characters, without leaving half of a surrogate pair at the end.</summary>
    internal static string Cut(string s, int max)
    {
        if (s.Length <= max) return s;
        return char.IsHighSurrogate(s[max - 1]) ? s[..(max - 1)] : s[..max];
    }
}
