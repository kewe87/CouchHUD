using System.Diagnostics;
using CouchHud.Service.Contracts;

namespace CouchHud.Service.Authorization;

/// <summary>A clock that only goes forward (not the wall clock, which a user or NTP can set back).</summary>
public interface IMonotonicClock
{
    TimeSpan Now { get; }
}

public sealed class StopwatchClock : IMonotonicClock
{
    public static readonly StopwatchClock Instance = new();

    public TimeSpan Now => Stopwatch.GetElapsedTime(0);
}

/// <summary>The groups of operations that share a budget per account.</summary>
public enum RateBucket
{
    None,
    /// <summary>Code and passcode operations: few attempts, and failures lock out with a growing backoff.</summary>
    Codes,
    /// <summary>Password checks (SetAutoLogon): like codes.</summary>
    Password,
    Policies,
    Services,
    Firewall,
    Setups,
    /// <summary>Setup streams that broke or carried the wrong bytes (no operation maps here; the setup pipeline spends from it).</summary>
    SetupFailures,
    WindowsUpdates,
    /// <summary>Searching Windows Update (only reads, but takes the one Windows Update slot for seconds to minutes).</summary>
    UpdateSearch,
    Grants,
    /// <summary>Asking for an administrator's approval (each one shows a UAC prompt after it).</summary>
    Approvals,
    /// <summary>Picking an approval up: polled for a few seconds after the UAC prompt, so its own, roomier budget.</summary>
    ApprovalPickup,
    Housekeeping,
    /// <summary>
    /// The reset of a damaged code store (ResetCodeStore): its own small budget, so refused calls never use up the housekeeping budget the
    /// codes and the lock mode need.
    /// </summary>
    CodeStoreReset
}

public enum RateVerdict
{
    Allow,
    RateLimited,
    LockedOut
}

public sealed record RateDecision(RateVerdict Verdict, TimeSpan RetryAfter = default)
{
    public static readonly RateDecision Allowed = new(RateVerdict.Allow);

    public string? ErrorCode => Verdict switch
    {
        RateVerdict.RateLimited => ErrorCodes.RateLimited,
        RateVerdict.LockedOut => ErrorCodes.LockedOut,
        _ => null
    };
}

/// <summary>
/// Token buckets per account SID and bucket (design §3 "Rate-Limits"), plus an in-memory failure backoff for passwords. The codes'
/// own lockout, kept across restarts, is the code store's (<see cref="State.CodeStore"/>, in the protected data folder); an
/// administrator's approval goes through UAC and has no credential to guess here. The SIDs come from tokens, but the number of
/// tracked entries is capped anyway.
/// </summary>
public sealed class RateLimiter(IMonotonicClock clock)
{
    /// <summary>Failures that are free before the backoff starts.</summary>
    public const int FreeFailures = 3;
    public static readonly TimeSpan FirstLockout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxLockout = TimeSpan.FromHours(1);
    public const int MaxTrackedEntries = 4096;

    private sealed record Limit(int Capacity, TimeSpan Window);

    private static readonly IReadOnlyDictionary<RateBucket, Limit> Limits = new Dictionary<RateBucket, Limit>
    {
        [RateBucket.Codes] = new(5, TimeSpan.FromMinutes(1)),
        [RateBucket.Password] = new(5, TimeSpan.FromMinutes(1)),
        [RateBucket.Policies] = new(30, TimeSpan.FromMinutes(1)),
        [RateBucket.Services] = new(10, TimeSpan.FromMinutes(1)),
        [RateBucket.Firewall] = new(10, TimeSpan.FromMinutes(1)),
        [RateBucket.Setups] = new(6, TimeSpan.FromHours(1)),
        [RateBucket.SetupFailures] = new(2, TimeSpan.FromHours(1)),
        [RateBucket.WindowsUpdates] = new(6, TimeSpan.FromHours(1)),
        [RateBucket.UpdateSearch] = new(20, TimeSpan.FromHours(1)),
        [RateBucket.Grants] = new(10, TimeSpan.FromMinutes(1)),
        [RateBucket.Approvals] = new(6, TimeSpan.FromMinutes(1)),
        [RateBucket.ApprovalPickup] = new(60, TimeSpan.FromMinutes(1)),
        [RateBucket.Housekeeping] = new(10, TimeSpan.FromMinutes(1)),
        [RateBucket.CodeStoreReset] = new(3, TimeSpan.FromMinutes(1)),
    };

    public static RateBucket BucketFor(Operation operation) => operation switch
    {
        // Only the check of a code: storing a hash guesses nothing (and the HUD stores the parents' code right after an unlock).
        Operation.VerifyCode => RateBucket.Codes,
        Operation.SetAutoLogon => RateBucket.Password,
        Operation.ApplyLockPolicies or Operation.ReleaseLockPolicies or Operation.CleanupPolicyKeys => RateBucket.Policies,
        Operation.PauseServices or Operation.ResumeServices or Operation.AutoLogonOff => RateBucket.Services,
        Operation.SetRemoteFirewall or Operation.RemoveRemoteFirewall or Operation.RemoveLegacyFirewallRules => RateBucket.Firewall,
        Operation.InstallCouchHudUpdate => RateBucket.Setups,
        Operation.InstallWindowsUpdates => RateBucket.WindowsUpdates,
        Operation.SearchWindowsUpdates => RateBucket.UpdateSearch,
        Operation.RequestWindowsGrant => RateBucket.Grants,
        Operation.RequestAdminApproval => RateBucket.Approvals,
        Operation.CollectAdminApproval => RateBucket.ApprovalPickup,
        Operation.ResetCodeStore => RateBucket.CodeStoreReset,
        Operation.MigrateTasks or Operation.ReleaseAllAccounts or Operation.ResetServiceState or Operation.EnrollPasscode or Operation.ChangePasscode or Operation.RemovePasscode
            or Operation.SetLockMode => RateBucket.Housekeeping,
        // Hello, Status, Ping, GetJob, GetRecentEvents: cheap and read-only; the per-account connection quota bounds them.
        _ => RateBucket.None
    };

    private sealed class Entry
    {
        public double Tokens;
        public TimeSpan Refilled;
        public int Failures;
        public TimeSpan LockedUntil;
        public TimeSpan LastUsed;
    }

    private readonly IMonotonicClock _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    private readonly Dictionary<(string Sid, RateBucket Bucket), Entry> _entries = [];
    private readonly Lock _lock = new();

    /// <summary>Takes one attempt from the account's budget for the operation, or says why not.</summary>
    public RateDecision TryAcquire(string sid, Operation operation)
    {
        var bucket = BucketFor(operation);
        if (bucket == RateBucket.None) return RateDecision.Allowed;
        var limit = Limits[bucket];
        lock (_lock)
        {
            var now = _clock.Now;
            // A full table where nothing may be evicted (every entry locked out): refuse rather than forget a lockout.
            if (Get(sid, bucket, limit, now) is not { } entry) return new RateDecision(RateVerdict.RateLimited, limit.Window / limit.Capacity);
            if (entry.LockedUntil > now) return new RateDecision(RateVerdict.LockedOut, entry.LockedUntil - now);
            Refill(entry, limit, now);
            if (entry.Tokens < 1)
            {
                var perToken = limit.Window / limit.Capacity;
                return new RateDecision(RateVerdict.RateLimited, perToken * (1 - entry.Tokens));
            }
            entry.Tokens -= 1;
            return RateDecision.Allowed;
        }
    }

    /// <summary>Whether the account has at least one attempt left in <paramref name="bucket"/> (nothing is taken).</summary>
    public bool HasBudget(string sid, RateBucket bucket)
    {
        if (!Limits.TryGetValue(bucket, out var limit)) return true;
        lock (_lock)
        {
            var now = _clock.Now;
            if (Get(sid, bucket, limit, now) is not { } entry) return false;
            if (entry.LockedUntil > now) return false;
            Refill(entry, limit, now);
            return entry.Tokens >= 1;
        }
    }

    /// <summary>Takes one attempt from <paramref name="bucket"/> after the fact (a failure that costs); never below zero.</summary>
    public void Spend(string sid, RateBucket bucket)
    {
        if (!Limits.TryGetValue(bucket, out var limit)) return;
        lock (_lock)
        {
            var now = _clock.Now;
            if (Get(sid, bucket, limit, now) is not { } entry) return;
            Refill(entry, limit, now);
            entry.Tokens = Math.Max(0, entry.Tokens - 1);
        }
    }

    /// <summary>A wrong code or password: after <see cref="FreeFailures"/> the account is locked out, twice as long each time (capped).</summary>
    public void RecordFailure(string sid, Operation operation)
    {
        var bucket = BucketFor(operation);
        if (bucket == RateBucket.None) return;
        lock (_lock)
        {
            var now = _clock.Now;
            // Not trackable: TryAcquire refuses such an account anyway.
            if (Get(sid, bucket, Limits[bucket], now) is not { } entry) return;
            entry.Failures++;
            if (entry.Failures >= FreeFailures)
            {
                var doublings = Math.Min(entry.Failures - FreeFailures, 20);
                var lockout = TimeSpan.FromTicks(Math.Min(FirstLockout.Ticks << doublings, MaxLockout.Ticks));
                entry.LockedUntil = now + lockout;
            }
        }
    }

    /// <summary>A right code or password ends the backoff.</summary>
    public void RecordSuccess(string sid, Operation operation)
    {
        var bucket = BucketFor(operation);
        if (bucket == RateBucket.None) return;
        lock (_lock)
        {
            if (_entries.TryGetValue((sid, bucket), out var entry))
            {
                entry.Failures = 0;
                entry.LockedUntil = TimeSpan.Zero;
            }
        }
    }

    private Entry? Get(string sid, RateBucket bucket, Limit limit, TimeSpan now)
    {
        if (!_entries.TryGetValue((sid, bucket), out var entry))
        {
            if (_entries.Count >= MaxTrackedEntries && !Evict(now)) return null;
            entry = new Entry { Tokens = limit.Capacity, Refilled = now };
            _entries[(sid, bucket)] = entry;
        }
        entry.LastUsed = now;
        return entry;
    }

    private static void Refill(Entry entry, Limit limit, TimeSpan now)
    {
        var elapsed = now - entry.Refilled;
        if (elapsed <= TimeSpan.Zero) return;
        entry.Tokens = Math.Min(limit.Capacity, entry.Tokens + elapsed / limit.Window * limit.Capacity);
        entry.Refilled = now;
    }

    // Only entries without a running lockout go (a lockout must not be escaped by flooding the table); then the least recently used.
    private bool Evict(TimeSpan now)
    {
        var candidates = _entries.Where(e => e.Value.LockedUntil <= now).OrderBy(e => e.Value.LastUsed).Select(e => e.Key).Take(1).ToList();
        return candidates.Count == 1 && _entries.Remove(candidates[0]);
    }

    internal int TrackedEntries
    {
        get { lock (_lock) return _entries.Count; }
    }
}
