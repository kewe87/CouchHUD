namespace CouchHud.Core.Tests;

/// <summary>A clock that only moves when the test advances it; its timers fire in <see cref="Advance"/>, on the caller's thread.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>How many timers are armed right now (a fired or disposed timer is gone), so a test can tell when work that owned timers is over.</summary>
    public int PendingTimers
    {
        get
        {
            lock (_lock) return _timers.Count;
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock) return _now;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves the clock and fires every timer that is due (a timer with a period fires again each time it is due).</summary>
    public void Advance(TimeSpan by)
    {
        var target = GetUtcNow() + by;
        while (true)
        {
            ManualTimer? next;
            lock (_lock)
            {
                next = _timers.Where(t => t.Due is { } d && d <= target).OrderBy(t => t.Due).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }
                if (next.Due > _now) _now = next.Due!.Value;
                next.Reschedule(_now);
            }
            next.Fire();
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;
        public DateTimeOffset? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._lock)
            {
                _period = period;
                if (dueTime == Timeout.InfiniteTimeSpan)
                {
                    Due = null;
                    owner._timers.Remove(this);
                }
                else
                {
                    Due = owner._now + dueTime;
                    if (!owner._timers.Contains(this)) owner._timers.Add(this);
                }
            }
            return true;
        }

        public void Reschedule(DateTimeOffset now)
        {
            if (_period > TimeSpan.Zero) Due = now + _period;
            else
            {
                Due = null;
                owner._timers.Remove(this);
            }
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._lock)
            {
                Due = null;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
