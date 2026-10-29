namespace CouchHud.Core.Updates;

/// <summary>
/// When the quiet background update checks (CouchHUD's own release, the plugin catalog) run: soon after the start (the network may not be
/// ready right at logon, so one retry a few minutes later after a failure, then back to the normal rhythm), every 12 hours, and when
/// the user opens the Downloads page or the update settings and the last good check is older than an hour (at most one attempt per
/// <see cref="OpenCooldown"/>, so a page opened over and over, or a failing network, never hammers GitHub). Pure timing, no I/O; the
/// clock is injectable. Thread-safe.
/// </summary>
public sealed class UpdateCheckSchedule(Func<DateTimeOffset>? clock = null)
{
    /// <summary>The first background check after the start.</summary>
    public static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(30);

    /// <summary>The one retry after a failed check (no network yet).</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(3);

    /// <summary>The normal rhythm between two background checks.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(12);

    /// <summary>A page that is opened asks again only when the last good check is older than this.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    /// <summary>The least time between two checks started by opening a page (and after a check of any kind).</summary>
    public static readonly TimeSpan OpenCooldown = TimeSpan.FromMinutes(5);

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly object _lock = new();
    private DateTimeOffset? _lastAttempt;
    private DateTimeOffset? _lastSuccess;
    private int _failures;

    /// <summary>The wait before the next background check, from the first one on: 30 s.</summary>
    public TimeSpan First => FirstDelay;

    /// <summary>Notes that a pass was started (also one that was skipped for a running game or a switched-off setting): the open-page cooldown counts from it.</summary>
    public void Attempted()
    {
        lock (_lock) _lastAttempt = _clock();
    }

    /// <summary>Notes a check that really ran (not one skipped for a running game or a switched-off setting).</summary>
    public void Record(bool succeeded)
    {
        lock (_lock)
        {
            var now = _clock();
            _lastAttempt = now;
            if (succeeded)
            {
                _lastSuccess = now;
                _failures = 0;
            }
            else
            {
                _failures++;
            }
        }
    }

    /// <summary>
    /// The wait after a background pass. A failure is retried once after <see cref="RetryDelay"/>; a second failure in a row goes back to
    /// <see cref="Interval"/>. A pass that did not run (a game was running, checks are off) looks again after the retry delay.
    /// </summary>
    public TimeSpan NextDelay(bool ran)
    {
        lock (_lock)
        {
            if (!ran) return RetryDelay;
            if (_failures == 1) return RetryDelay;
            if (_failures >= 2) _failures = 0;
            return Interval;
        }
    }

    /// <summary>Whether opening the Downloads page or the update settings should start a non-manual check now.</summary>
    public bool DueOnOpen()
    {
        lock (_lock)
        {
            var now = _clock();
            if (_lastAttempt is { } attempt && now - attempt < OpenCooldown) return false;
            return _lastSuccess is not { } ok || now - ok >= StaleAfter;
        }
    }
}
