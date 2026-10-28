namespace CouchHud.Core.Plugins;

/// <summary>
/// Page visits: the parents' code for an admin page of a plugin in a child's profile opens that page for a while. The page and its rows stay
/// allowed while the page stays open, at most <see cref="Lifetime"/>. Host-side state, bound to plugin, page and profile, so it cannot be used
/// for another page, another plugin or another profile; the page can only end a visit (leaving it), never start or extend one. A profile switch,
/// the lock and a game start clear everything.
/// </summary>
public sealed class PluginVisits(Func<DateTimeOffset> now, TimeSpan? lifetime = null)
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(5);

    private readonly TimeSpan _lifetime = lifetime ?? DefaultLifetime;
    private readonly Dictionary<(string Plugin, string Page), (string Profile, DateTimeOffset Until)> _visits = new(PairComparer.Instance);
    private readonly object _gate = new();

    public TimeSpan Lifetime => _lifetime;

    public void Begin(string plugin, string page, string profile)
    {
        lock (_gate) _visits[(plugin, page)] = (profile, now() + _lifetime);
    }

    /// <summary>Is a visit of exactly this plugin, page and profile on (not run out)? A visit that ran out is dropped; one of another profile does not count.</summary>
    public bool Active(string plugin, string page, string profile)
    {
        lock (_gate)
        {
            if (!_visits.TryGetValue((plugin, page), out var visit)) return false;
            if (now() >= visit.Until)
            {
                _visits.Remove((plugin, page));
                return false;
            }
            return visit.Profile == profile;
        }
    }

    public void End(string plugin, string page)
    {
        lock (_gate) _visits.Remove((plugin, page));
    }

    public void Clear()
    {
        lock (_gate) _visits.Clear();
    }

    private sealed class PairComparer : IEqualityComparer<(string Plugin, string Page)>
    {
        public static readonly PairComparer Instance = new();

        public bool Equals((string Plugin, string Page) x, (string Plugin, string Page) y) =>
            string.Equals(x.Plugin, y.Plugin, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Page, y.Page, StringComparison.Ordinal);

        public int GetHashCode((string Plugin, string Page) pair) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(pair.Plugin), pair.Page);
    }
}
