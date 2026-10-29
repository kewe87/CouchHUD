namespace CouchHud.Core.Notifications;

/// <summary>One entry in the notification center: from Windows (WhatsApp, Discord …) or from CouchHud itself.</summary>
/// <remarks>
/// Security: a notice about the lock, the parental controls, the shell entry, the setup or a damaged configuration. Set by the
/// host only (never from Windows or plugins); the UI shows it even while a page keeps other notifications quiet.
/// </remarks>
public sealed record Notification(
    string Id,
    string Source,
    string App,
    string Title,
    string Body,
    long Time,
    bool Read = false,
    bool Security = false,
    string? Target = null);

/// <summary>
/// Where a notification of CouchHUD itself leads when it is pressed or clicked. A fixed whitelist of screens, never a URL or a path;
/// set by core code only (plugins and Windows notifications have none), and the UI follows nothing else. Opening a target only shows
/// the screen: installing there follows the usual rules (parents' code, locked couch).
/// </summary>
public static class NotificationTargets
{
    public const string Downloads = "downloads";

    /// <summary>The target when it is on the whitelist (exact match), else null.</summary>
    public static string? Normalize(string? target) => target == Downloads ? Downloads : null;
}

/// <summary>
/// The PlayStation-style notification list: newest first, a few dozen at most,
/// duplicates (the same Windows toast seen twice) collapse into one.
/// </summary>
public sealed class NotificationCenter
{
    public const int Capacity = 50;

    private readonly object _lock = new();
    private readonly List<Notification> _items = [];

    public event Action<Notification>? Added;

    public IReadOnlyList<Notification> Items
    {
        get
        {
            lock (_lock) return _items.ToList();
        }
    }

    public int Unread
    {
        get
        {
            lock (_lock) return _items.Count(n => !n.Read);
        }
    }

    /// <summary>Adds unless an entry with the same id exists; returns whether it was new.</summary>
    public bool Add(Notification notification)
    {
        lock (_lock)
        {
            if (_items.Any(n => n.Id == notification.Id)) return false;
            _items.Insert(0, notification);
            if (_items.Count > Capacity) _items.RemoveRange(Capacity, _items.Count - Capacity);
        }
        Added?.Invoke(notification);
        return true;
    }

    public void MarkAllRead()
    {
        lock (_lock)
        {
            for (var i = 0; i < _items.Count; i++) _items[i] = _items[i] with { Read = true };
        }
    }

    public void Clear()
    {
        lock (_lock) _items.Clear();
    }
}
