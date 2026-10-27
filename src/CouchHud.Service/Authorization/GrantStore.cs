using System.Security.Cryptography;
using CouchHud.Service.Contracts;

namespace CouchHud.Service.Authorization;

/// <summary>
/// The grants of P9, in memory only (a restart of the service ends every grant). A grant is 32 random bytes; the store keeps only their
/// SHA-256, so not even a dump of the service holds a usable grant. Each is bound to the caller's account SID, its logon session
/// (<see cref="CallerInfo.LogonId"/>) and its Windows session, never to a process id (review P3/P4, M4).
/// </summary>
/// <remarks>
/// Two lives (P9 review M3):
/// <list type="bullet">
/// <item>Couch code and Windows unlock (the couch is open): usable again until it expires (<see cref="Lifetime"/>, the HUD's 2-minute unlock
/// window), at most <see cref="MaxUses"/> times. Unlocking releases two policy groups and cleans their keys; a request is retried after "busy".</item>
/// <item>Parents' code and administrator (an approval of something specific): only for the operations named when it was asked for, each once,
/// within <see cref="BoundLifetime"/>.</item>
/// </list>
/// A use only counts when kind and operation fit: a grant offered where its kind or operation does not count is refused and not used up.
/// Every grant of a Windows session ends when the session is locked, disconnected or signed out (<see cref="RevokeSession"/>), and none is
/// accepted while the session is locked right now (<paramref name="sessionLocked"/>; asked of Windows at use, review L2). The table is
/// bounded: per account (<see cref="MaxPerAccount"/>, the oldest goes) and in total (<see cref="MaxTotal"/>).
/// </remarks>
/// <param name="sessionLocked">Whether a Windows session is locked right now; null when it cannot be told (then refused: fail-closed). Null function: not checked (tests).</param>
public sealed class GrantStore(IMonotonicClock clock, Func<int, byte[]>? random = null, Func<int, bool?>? sessionLocked = null) : IGrantStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan BoundLifetime = TimeSpan.FromSeconds(30);
    public const int MaxUses = 32;
    public const int MaxPerAccount = 8;
    public const int MaxTotal = 256;
    public const int GrantBytes = 32;

    private sealed class Entry
    {
        public required string Sid;
        public required long LogonId;
        public required int Session;
        public required GrantKinds Kind;
        public required TimeSpan Issued;
        public required TimeSpan Expires;
        /// <summary>The operations a bound grant is still good for, one use each; null for an unbound grant.</summary>
        public List<Operation>? Ops;
        public int Uses;
    }

    private readonly IMonotonicClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly Func<int, byte[]> _random = random ?? RandomNumberGenerator.GetBytes;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>Whether a kind is an approval for named operations (parent, administrator) rather than an open couch.</summary>
    public static bool IsBound(GrantKinds kind) => kind is GrantKinds.Parent or GrantKinds.WindowsAdmin;

    /// <summary>
    /// Whether the Windows session is locked right now, or that cannot be told (fail-closed). False when no check is wired (tests).
    /// </summary>
    public bool IsSessionLocked(int session) => SessionLockState(session) != false;

    /// <summary>Whether the session is locked: true, false, or null when Windows cannot tell (without a check configured: false).</summary>
    public bool? SessionLockState(int session) => sessionLocked is null ? false : sessionLocked(session);

    /// <summary>
    /// A new grant of exactly one kind for this caller, or null when the caller has no logon session to bind it to, its session is locked
    /// right now (nobody at the couch could use it; finding B2 of the 0.4.1 acceptance test), or a parent's or an administrator's grant
    /// comes without operations (or a couch or unlock grant with some).
    /// </summary>
    public IssuedGrant? Issue(CallerInfo caller, GrantKinds kind, IReadOnlyList<Operation>? ops = null)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.LogonId == 0 || caller.TokenSessionId <= 0 || caller.TokenSessionId != caller.PipeSessionId) return null;
        if (kind is not (GrantKinds.Couch or GrantKinds.WindowsUnlock or GrantKinds.Parent or GrantKinds.WindowsAdmin)) return null;
        if (IsBound(kind) != ops is { Count: > 0 } || ops is { Count: > RequestValidator.MaxGrantOps }) return null;
        if (IsSessionLocked(caller.TokenSessionId)) return null;
        var bytes = _random(GrantBytes);
        if (bytes.Length != GrantBytes) throw new InvalidOperationException("random source gave the wrong length");
        try
        {
            var text = Convert.ToBase64String(bytes);
            var key = Key(bytes);
            var lifetime = IsBound(kind) ? BoundLifetime : Lifetime;
            lock (_lock)
            {
                var now = _clock.Now;
                DropExpired(now);
                // Bounded per account and in total: the oldest of the account first, then the oldest of all.
                while (_entries.Count(e => e.Value.Sid == caller.Sid) >= MaxPerAccount) Remove(Oldest(e => e.Sid == caller.Sid));
                while (_entries.Count >= MaxTotal) Remove(Oldest(_ => true));
                _entries[key] = new Entry
                {
                    Sid = caller.Sid,
                    LogonId = caller.LogonId,
                    Session = caller.TokenSessionId,
                    Kind = kind,
                    Issued = now,
                    Expires = now + lifetime,
                    Ops = ops is null ? null : [.. ops]
                };
            }
            return new IssuedGrant(text, kind, lifetime, ops);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public bool TryConsume(CallerInfo caller, string grant, Operation operation, GrantKinds accepted, out GrantKinds used)
    {
        used = GrantKinds.None;
        if (caller is null || caller.LogonId == 0 || accepted == GrantKinds.None || string.IsNullOrEmpty(grant)) return false;
        Span<byte> bytes = stackalloc byte[GrantBytes + 3];
        if (!Convert.TryFromBase64String(grant, bytes, out var written) || written != GrantBytes) return false;
        var key = Key(bytes[..GrantBytes]);
        CryptographicOperations.ZeroMemory(bytes);
        Entry? entry;
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out entry)) return false;
            if (_clock.Now >= entry.Expires)
            {
                _entries.Remove(key);
                return false;
            }
            // Another account, another sign-in of the same account, or another Windows session: not this caller's grant.
            if (!string.Equals(entry.Sid, caller.Sid, StringComparison.Ordinal) || entry.LogonId != caller.LogonId
                || entry.Session != caller.TokenSessionId || caller.TokenSessionId != caller.PipeSessionId) return false;
            // The wrong kind, or an operation the approval was not for: refused, and nothing is used up.
            if ((entry.Kind & accepted) == 0) return false;
            if (entry.Ops is { } ops && !ops.Contains(operation)) return false;
        }
        // Outside the lock (it asks Windows): a session that is locked right now has nobody at the couch who earned the grant.
        if (IsSessionLocked(entry.Session)) return false;
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out var still) || !ReferenceEquals(still, entry)) return false;
            if (entry.Ops is { } ops)
            {
                if (!ops.Remove(operation)) return false;
                if (ops.Count == 0) _entries.Remove(key);
            }
            else
            {
                entry.Uses++;
                if (entry.Uses >= MaxUses) _entries.Remove(key);
            }
            used = entry.Kind;
            return true;
        }
    }

    /// <summary>Every grant of a Windows session ends (lock, disconnect, sign-out). Returns how many went.</summary>
    public int RevokeSession(int sessionId)
    {
        lock (_lock)
        {
            var keys = _entries.Where(e => e.Value.Session == sessionId).Select(e => e.Key).ToList();
            foreach (var key in keys) _entries.Remove(key);
            return keys.Count;
        }
    }

    /// <summary>Every grant there is (the service stops; tests).</summary>
    public void Clear()
    {
        lock (_lock) _entries.Clear();
    }

    internal int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    /// <summary>What the table holds as keys (tests: never the grant itself).</summary>
    internal IReadOnlyList<string> StoredKeys
    {
        get { lock (_lock) return [.. _entries.Keys]; }
    }

    private static string Key(ReadOnlySpan<byte> grant) => Convert.ToHexString(SHA256.HashData(grant));

    private void DropExpired(TimeSpan now)
    {
        foreach (var key in _entries.Where(e => now >= e.Value.Expires).Select(e => e.Key).ToList()) _entries.Remove(key);
    }

    private string? Oldest(Func<Entry, bool> which) =>
        _entries.Where(e => which(e.Value)).OrderBy(e => e.Value.Issued).Select(e => e.Key).FirstOrDefault();

    private void Remove(string? key)
    {
        if (key is null) throw new InvalidOperationException("grant table bound without a candidate");
        _entries.Remove(key);
    }

    /// <summary>The wire name of a kind.</summary>
    public static string NameOf(GrantKinds kind) => kind switch
    {
        GrantKinds.Couch => GrantKindNames.Couch,
        GrantKinds.WindowsUnlock => GrantKindNames.WindowsUnlock,
        GrantKinds.Parent => GrantKindNames.Parent,
        GrantKinds.WindowsAdmin => GrantKindNames.WindowsAdmin,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>Operation names from a request (already checked by the validator against <see cref="RequestValidator.GrantableOperations"/>).</summary>
    public static IReadOnlyList<Operation>? OpsOf(string[]? names) => names?.Select(Enum.Parse<Operation>).ToList();
}

/// <summary>A grant as it goes to the caller (the only copy of its text; the store keeps the hash).</summary>
public sealed record IssuedGrant(string Grant, GrantKinds Kind, TimeSpan Lifetime, IReadOnlyList<Operation>? Ops = null)
{
    public GrantData ToData() => new(Grant, GrantStore.NameOf(Kind), (int)Lifetime.TotalSeconds, Ops?.Select(o => o.ToString()).ToArray());

    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Grant = ***, Kind = ").Append(Kind);
        return true;
    }
}
