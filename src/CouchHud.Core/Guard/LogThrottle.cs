namespace CouchHud.Core.Guard;

/// <summary>
/// Keeps a message that repeats on a timer from filling the log: the same text is written again only after the interval, and then says how
/// many times it was left out. A different text always passes at once. Thread-safe; the clock is passed in.
/// </summary>
public sealed class LogThrottle(TimeSpan interval)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTime Last, int Left)> _seen = new(StringComparer.Ordinal);

    /// <param name="suppressed">How many identical messages were left out since the last one written.</param>
    public bool Allow(string message, DateTime now, out int suppressed)
    {
        lock (_gate)
        {
            if (_seen.TryGetValue(message, out var entry) && now >= entry.Last && now - entry.Last < interval)
            {
                _seen[message] = (entry.Last, entry.Left + 1);
                suppressed = 0;
                return false;
            }
            suppressed = _seen.TryGetValue(message, out var old) ? old.Left : 0;
            _seen[message] = (now, 0);
            // Only a handful of different texts exist, but the table never grows without a limit.
            if (_seen.Count > 64)
                foreach (var key in _seen.Where(p => now - p.Value.Last > interval).Select(p => p.Key).ToList()) _seen.Remove(key);
            return true;
        }
    }
}
