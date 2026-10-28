using System.Security.Cryptography;
using CouchHud.Service.Authorization;
using CouchHud.Service.Contracts;

namespace CouchHud.Service.Jobs;

/// <summary>
/// A long operation (Windows Update, a setup) that runs on after its request was answered. Only the account that started it reads it
/// (<see cref="StateCheck.JobOwner"/>, an elevated administrator too). Thread-safe: the worker writes, <see cref="Operation.GetJob"/> reads.
/// </summary>
public sealed class Job
{
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";

    private readonly Lock _lock = new();
    private string _state = Running;
    private int _percent;
    private string? _message;
    private int _installed;
    private bool _rebootRequired;
    private UpdateEntry[]? _updates;
    private bool _restartCaseHud;
    private long _bytesDone;
    private long _bytesTotal;

    internal Job(string id, string ownerSid, string kind)
    {
        Id = id;
        OwnerSid = ownerSid;
        Kind = kind;
    }

    /// <summary>32 lower-case hex digits (16 random bytes): not guessable, and the shape the validator accepts.</summary>
    public string Id { get; }

    /// <summary>The SID from the token of the caller that started the job.</summary>
    public string OwnerSid { get; }

    public string Kind { get; }

    /// <summary>When it ended (monotonic clock), null while it runs.</summary>
    public TimeSpan? EndedAt { get; private set; }

    public bool IsRunning
    {
        get { lock (_lock) return _state == Running; }
    }

    public void Progress(int percent, string? message = null)
    {
        lock (_lock)
        {
            if (_state != Running) return;
            _percent = Math.Clamp(percent, _percent, 100);   // never backwards
            if (message is not null) _message = message;
        }
    }

    /// <summary>Byte counters of a running download (done, total); (0, 0) clears them. Kept sane: done within 0..total.</summary>
    public void Bytes(long done, long total)
    {
        lock (_lock)
        {
            if (_state != Running) return;
            _bytesTotal = Math.Max(0, total);
            _bytesDone = _bytesTotal == 0 ? 0 : Math.Clamp(done, 0, _bytesTotal);
        }
    }

    public void Complete(TimeSpan now, string? message = null, int installed = 0, bool rebootRequired = false, UpdateEntry[]? updates = null, bool restartCaseHud = false) =>
        End(() =>
        {
            _state = Done;
            _percent = 100;
            _message = message;
            _installed = installed;
            _rebootRequired = rebootRequired;
            _updates = updates;
            _restartCaseHud = restartCaseHud;
            EndedAt = now;
        });

    public void Fail(TimeSpan now, string message, int installed = 0, bool rebootRequired = false, bool restartCaseHud = false) =>
        End(() =>
        {
            _state = Failed;
            _message = message;
            _installed = installed;
            _rebootRequired = rebootRequired;
            _restartCaseHud = restartCaseHud;
            EndedAt = now;
        });

    private bool _holding;
    private Action? _deferred;

    /// <summary>
    /// While held, the end of the job (the first Complete or Fail) is kept back and shown when the hold ends: the worker ends its hold
    /// after it gave back its slot and files, so a client that reads "done" can start the next job at once.
    /// </summary>
    public IDisposable HoldEnd()
    {
        lock (_lock) _holding = true;
        return new EndHold(this);
    }

    private void End(Action apply)
    {
        lock (_lock)
        {
            if (_state != Running || _deferred is not null) return;   // the first end counts
            if (_holding) _deferred = apply;
            else apply();
        }
    }

    private void Release()
    {
        lock (_lock)
        {
            _holding = false;
            var deferred = _deferred;
            _deferred = null;
            deferred?.Invoke();
        }
    }

    private sealed class EndHold(Job job) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) job.Release();
        }
    }

    public JobData Snapshot()
    {
        lock (_lock) return new JobData(Id, _state, _percent, _message, _installed, _rebootRequired, _updates, _restartCaseHud,
            _state == Running ? _bytesDone : 0, _state == Running ? _bytesTotal : 0);
    }
}

/// <summary>
/// The jobs of the service, in memory (a restart of the service ends them anyway). Bounded: at most <see cref="MaxJobs"/>; finished ones
/// are kept <see cref="KeepFinished"/> for the client to read the result, and make room for new ones before that when needed.
/// </summary>
public sealed class JobStore(IMonotonicClock clock)
{
    public const int MaxJobs = 64;
    public static readonly TimeSpan KeepFinished = TimeSpan.FromHours(1);

    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public IMonotonicClock Clock { get; } = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>A new running job for the account, or null when the store is full of running jobs.</summary>
    public Job? Create(string ownerSid, string kind)
    {
        lock (_lock)
        {
            var now = Clock.Now;
            foreach (var stale in _jobs.Values.Where(j => j.EndedAt is { } ended && now - ended >= KeepFinished).Select(j => j.Id).ToList()) _jobs.Remove(stale);
            if (_jobs.Count >= MaxJobs)
            {
                var oldest = _jobs.Values.Where(j => j.EndedAt is not null).OrderBy(j => j.EndedAt).FirstOrDefault();
                if (oldest is null) return null;
                _jobs.Remove(oldest.Id);
            }
            var job = new Job(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), ownerSid, kind);
            _jobs[job.Id] = job;
            return job;
        }
    }

    /// <summary>A job of an earlier instance of the service (<see cref="JobJournal"/>), under its old id; null when the id is taken or there is no room.</summary>
    public Job? Restore(string id, string ownerSid, string kind)
    {
        lock (_lock)
        {
            if (_jobs.ContainsKey(id) || _jobs.Count >= MaxJobs) return null;
            var job = new Job(id, ownerSid, kind);
            _jobs[id] = job;
            return job;
        }
    }

    public Job? Find(string id)
    {
        lock (_lock) return _jobs.GetValueOrDefault(id);
    }

    /// <summary>The job if the caller may read it: its own, or the caller is an elevated administrator. Null otherwise (also for an unknown id: no oracle).</summary>
    public Job? FindFor(CallerInfo caller, string id) =>
        Find(id) is { } job && (string.Equals(job.OwnerSid, caller.Sid, StringComparison.Ordinal) || caller.IsElevatedAdmin) ? job : null;
}

/// <summary>One thing at a time, machine-wide (one Windows Update job, one setup): a lease, or null when it is taken.</summary>
public sealed class ExclusiveSlot
{
    private int _taken;

    public bool IsTaken => Volatile.Read(ref _taken) == 1;

    public IDisposable? TryEnter() => Interlocked.CompareExchange(ref _taken, 1, 0) == 0 ? new Lease(this) : null;

    private sealed class Lease(ExclusiveSlot owner) : IDisposable
    {
        private int _ended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0) Volatile.Write(ref owner._taken, 0);
        }
    }
}

/// <summary>At most <c>max</c> at a time, machine-wide: a lease, or null when all are taken.</summary>
public sealed class CountedSlot(int max)
{
    private int _taken;

    public int Taken => Volatile.Read(ref _taken);

    public IDisposable? TryEnter()
    {
        while (true)
        {
            var now = Volatile.Read(ref _taken);
            if (now >= max) return null;
            if (Interlocked.CompareExchange(ref _taken, now + 1, now) == now) return new Lease(this);
        }
    }

    private sealed class Lease(CountedSlot owner) : IDisposable
    {
        private int _ended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0) Interlocked.Decrement(ref owner._taken);
        }
    }
}
