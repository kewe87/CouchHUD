using CouchHud.Core.Guard;
using CouchHud.ServiceClient;
using Microsoft.Win32;

namespace CouchHud.Shell.Services;

/// <summary>
/// Writes the protected policy values of the locked couch (see <see cref="PolicyGroups"/>) through the CouchHUD service: the
/// Policies keys under HKCU only let administrators write, and the service (LocalSystem) sets exactly the fixed values of a group
/// for the caller's own account (the account comes from the pipe's token, never from the request).
/// CouchHud asks for a state per group (<see cref="Want"/>); a worker asks the service and waits until the registry shows the value
/// (or no longer does): an "ok" that the registry does not confirm counts as a failure. A mark file remembers which groups were set,
/// so exactly those are released, also after a crash or from the Run entry of the next sign-in.
/// Fail-closed: without a usable service nothing is released (the policies stay set), and a lock that cannot set them is reported
/// ("none" in <see cref="Mode"/>, the service status in the UI, the Windows lock after the grace in the Hud) instead of quietly
/// running without them. There is no other way (no scheduled task, no elevated helper).
/// </summary>
public static class PolicyHelper
{
    private const string MarkFile = "guard-locked.helper";
    private static readonly TimeSpan ApplyTimeout = TimeSpan.FromSeconds(10);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, bool> Desired = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Said = new(StringComparer.OrdinalIgnoreCase);
    private static Task? _worker;
    private static int _version;
    private static Action? _whenReleased;
    private static string _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CouchHud");

    private static (DateTime At, string Mode) _mode = (DateTime.MinValue, "none");
    private static volatile bool _helperFailed;
    private static string? _sid;
    // The way back (or a cleanup) is running: nothing may set a group while the tasks that release them are about to go.
    private static int _frozen;

    /// <summary>The SID of the signed-in user: whose tasks these are.</summary>
    public static string Sid
    {
        get
        {
            if (_sid is not null) return _sid;
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return _sid = identity.User?.Value ?? throw new InvalidOperationException("no user SID");
        }
    }

    public static string AutoLogonOffTask() => PolicyGroups.AutoLogonOffTaskName(Sid);

    private static void HelperFailed(OpResult result)
    {
        _helperFailed = true;
        lock (Gate) _failingSince ??= DateTime.UtcNow;
        ForgetMode();
        // The watch asks again every ten seconds while the setup is half done: the same answer is written once, then every ten minutes.
        var text = $"[lock] policy service call: {result}";
        if (CallLog.Allow(text, DateTime.UtcNow, out var left)) Console.Error.WriteLine(left > 0 ? $"{text} (repeated {left} times)" : text);
    }

    private static readonly CouchHud.Core.Guard.LogThrottle CallLog = new(TimeSpan.FromMinutes(10));

    /// <summary>The last try through the service did not work (the Hud tries again once the service is healthy).</summary>
    public static bool Failed => _helperFailed;

    private static DateTime? _failingSince;

    /// <summary>
    /// Since when setting the policies has not worked (refused, not applied, busy, rate limited, no service); null once an apply
    /// worked. Unlike <see cref="Failed"/> a retry does not reset it: the Hud's watch counts its grace from here (review M1).
    /// </summary>
    public static DateTime? FailingSince
    {
        get { lock (Gate) return _failingSince; }
    }

    /// <summary>The service is back: bring the registry to the wanted state again (what failed before is tried once more).</summary>
    public static void Retry()
    {
        lock (Gate)
        {
            if (Desired.Count == 0) return;
            _version++;
            _worker ??= Task.Run(Reconcile);
        }
    }

    // ---- asking for a state ---------------------------------------------------------------------

    /// <summary>The group should be applied (true) or released (false). Returns at once; the worker does it.</summary>
    public static void Want(PolicyGroup group, bool on, string? dataDir = null)
    {
        lock (Gate)
        {
            if (dataDir is not null) _dir = dataDir;
            if (on && Volatile.Read(ref _frozen) > 0)
            {
                Console.WriteLine($"[lock] policies '{group.Id}' not set: the setup is being removed");
                return;
            }
            Desired[group.Id] = on;
            _version++;
            _worker ??= Task.Run(Reconcile);
        }
    }

    /// <summary>
    /// Releases every group the helper set (the mark file knows which), e.g. at the start after a crash; with
    /// <paramref name="onlyWithUnlock"/> only those the locked desktop owns (the unlock leaves Task Manager to the couch security).
    /// <paramref name="whenReleased"/> runs once the worker is done and nothing is marked any more.
    /// </summary>
    public static void ReleaseMarked(string dataDir, Action? whenReleased = null, bool onlyWithUnlock = false)
    {
        var marked = Marked(dataDir);
        var groups = onlyWithUnlock ? PolicyGroups.ReleasedWithUnlock(marked).ToList() : marked.Select(PolicyGroups.ById).OfType<PolicyGroup>().ToList();
        if (groups.Count == 0) return;
        lock (Gate) _whenReleased = whenReleased ?? _whenReleased;
        foreach (var group in groups) Want(group, false, dataDir);
    }

    public static bool HasMarks(string dataDir) => Marked(dataDir).Count > 0;

    /// <summary>
    /// While the returned handle lives, no group is set (the way back, a cleanup): the tasks that release them are about to be
    /// deleted, and a lock that closes in the middle of a slow administrator prompt must not set the policies again behind them.
    /// </summary>
    public static IDisposable Freeze()
    {
        Interlocked.Increment(ref _frozen);
        return new Frozen();
    }

    private sealed class Frozen : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) Interlocked.Decrement(ref _frozen);
        }
    }

    /// <summary>
    /// Gives back every group (also one the mark file forgot) through its release task and checks the registry afterwards: the verdict
    /// says what is left. Waits for the worker first. The caller decides about deleting tasks by <see cref="PolicyRelease.TasksMayGo"/>.
    /// </summary>
    public static PolicyRelease.Verdict ReleaseAllVerified(string dataDir, TimeSpan? timeout = null)
    {
        lock (Gate) _dir = dataDir;
        var registry = new WindowsRegistry();
        WaitIdle(timeout ?? TimeSpan.FromSeconds(20));
        foreach (var group in PolicyGroups.All)
        {
            try
            {
                if (PolicyRelease.OursPresent(registry, group).Count > 0)
                {
                    if (Run(group, apply: false) is { Ok: false } refused)
                    {
                        Say(group.Id + "-release", $"[lock] policies '{group.Id}' stay: the service did not release them ({refused}); they go with the next try");
                        continue;
                    }
                    if (!WaitFor(() => PolicyRelease.OursPresent(registry, group).Count == 0))
                    {
                        Console.Error.WriteLine($"[lock] policies '{group.Id}': the service said released, the registry does not show it");
                        continue;
                    }
                    Console.WriteLine($"[lock] policies '{group.Id}' released through the service");
                }
                // Gone from the registry: nothing to remember any more.
                Mark(group.Id, false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.Error.WriteLine($"[lock] policy helper {group.Id}: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return PolicyRelease.Verdict.Check(registry, Marked(dataDir));
    }

    /// <summary>
    /// Policies with our values that nothing explains (no mark, no lock meant to be on, no state of the snapshot), with a sign that
    /// CouchHud set them (its Run entry for the release, the note of keys it created, or the tasks' folder): the leftover of a way
    /// back that deleted the tasks too early. Empty when all is as it should be.
    /// </summary>
    public static IReadOnlyList<PolicyGroup> StuckGroups(string dataDir, IReadOnlyCollection<string> expectedOn, bool snapshotPending)
    {
        if (snapshotPending) return [];
        var registry = new WindowsRegistry();
        var stuck = PolicyRelease.Stuck(registry, Marked(dataDir), expectedOn);
        if (stuck.Count == 0) return stuck;
        var evidence = registry.ReadString(LockedDesktop.RunKeyPath, LockedDesktop.RunValueName) is not null
                       || File.Exists(Path.Combine(dataDir, LockedDesktop.KeysFileName))
                       || ServiceSwitch.HasLeftovers();
        return evidence ? stuck : [];
    }

    /// <summary>Waits until the worker has nothing left to do; false when the time is up.</summary>
    public static bool WaitIdle(TimeSpan timeout)
    {
        Task? worker;
        lock (Gate) worker = _worker;
        return worker is null || worker.Wait(timeout);
    }

    private static void Reconcile()
    {
        while (true)
        {
            Dictionary<string, bool> work;
            int version;
            lock (Gate)
            {
                work = new Dictionary<string, bool>(Desired);
                version = _version;
            }
            foreach (var (id, on) in work)
            {
                if (PolicyGroups.ById(id) is not { } group) continue;
                try
                {
                    Step(group, on);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Console.Error.WriteLine($"[lock] policy helper {id}: {ex.GetType().Name}: {ex.Message}");
                }
            }
            Action? done = null;
            lock (Gate)
            {
                if (version != _version) continue;
                _worker = null;
                if (Marked(_dir).Count == 0 && !Desired.Values.Any(on => on))
                {
                    done = _whenReleased;
                    _whenReleased = null;
                }
            }
            try
            {
                done?.Invoke();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.Error.WriteLine($"[lock] policy helper: {ex.Message}");
            }
            return;
        }
    }

    private static void Step(PolicyGroup group, bool on)
    {
        var registry = new WindowsRegistry();
        if (on)
        {
            if (Volatile.Read(ref _frozen) > 0) return;
            if (PolicyGroups.Applied(registry, group))
            {
                lock (Gate) _failingSince = null;
                return;
            }
            if (!PolicyGroups.Pristine(registry, group))
            {
                // A value of the user or of a Group Policy: not ours to set, and not ours to remove later.
                Say(group.Id + "-exists", $"[lock] policies '{group.Id}' not set: the account has values there already, they stay as they are");
                return;
            }
            var before = Marked(_dir);
            Mark(group.Id, true);
            if (Run(group, apply: true) is { Ok: false } refused)
            {
                if (!before.Contains(group.Id, StringComparer.OrdinalIgnoreCase)) Mark(group.Id, false);
                HelperFailed(refused);
                Say(group.Id + "-missing", $"[lock] policies '{group.Id}' not active: the CouchHUD service did not set them ({refused})");
                return;
            }
            if (WaitFor(() => PolicyGroups.Applied(registry, group)))
            {
                _helperFailed = false;
                lock (Gate) _failingSince = null;
                Console.WriteLine($"[lock] policies '{group.Id}' set through the service");
            }
            else
            {
                // "ok" without the values in the registry: the service did not do what it said. Shown as not active, never as set.
                HelperFailed(new OpResult(FailureKind.Inconsistent, "notApplied"));
                Console.Error.WriteLine($"[lock] policies '{group.Id}': the service said set, the registry does not show it; shown as not active");
            }
            return;
        }
        if (!Marked(_dir).Contains(group.Id, StringComparer.OrdinalIgnoreCase)) return;
        if (PolicyGroups.AnyPresent(registry, group))
        {
            // Fail-closed: without the service the policies stay set (the mark stays, the next try or the repair releases them).
            if (Run(group, apply: false) is { Ok: false } refused)
            {
                HelperFailed(refused);
                Say(group.Id + "-release", $"[lock] policies '{group.Id}' stay: the service did not release them ({refused}); they go with the next try");
                return;
            }
            if (!WaitFor(() => !PolicyGroups.AnyPresent(registry, group)))
            {
                Console.Error.WriteLine($"[lock] policies '{group.Id}': the service said released, the registry does not show it");
                return;
            }
            Console.WriteLine($"[lock] policies '{group.Id}' released through the service");
        }
        Mark(group.Id, false);
    }

    /// <summary>The one way to the protected values: the service, for this account (the service takes the account from the token).</summary>
    private static OpResult Run(PolicyGroup group, bool apply)
    {
        var ops = AdminService.Ops;
        // A background thread (the worker): no synchronization context to deadlock on.
        return (apply ? ops.ApplyLockPoliciesAsync([group.Id]) : ops.ReleaseLockPoliciesAsync([group.Id])).GetAwaiter().GetResult();
    }

    /// <summary>
    /// After the last group is released: the empty Policies keys that the lock created go too. The service removes only keys from its
    /// own record of what it created for this account (and only empty ones); <paramref name="createdByUs"/> are the keys that did not
    /// exist before the first apply, so nothing is asked when none of them is left.
    /// </summary>
    public static void CleanupKeys(IReadOnlyCollection<string> createdByUs)
    {
        try
        {
            var registry = new WindowsRegistry();
            if (createdByUs.Count == 0 || createdByUs.Where(LockHardening.IsProtectedKey).All(k => !registry.KeyExists(k))) return;
            var result = AdminService.Ops.CleanupPolicyKeysAsync().GetAwaiter().GetResult();
            if (!result.Ok) Say("cleanup", $"[lock] empty policy keys stay: the service did not remove them ({result})");
            else Console.WriteLine("[lock] empty policy keys removed through the service");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Console.Error.WriteLine($"[lock] policy key cleanup: {ex.Message}");
        }
    }

    private static bool WaitFor(Func<bool> done)
    {
        var until = DateTime.UtcNow + ApplyTimeout;
        while (!done())
        {
            if (DateTime.UtcNow >= until) return false;
            Thread.Sleep(100);
        }
        return true;
    }

    /// <summary>The same remark only once per run of CouchHud: the lock checks its state all the time.</summary>
    private static void Say(string key, string message)
    {
        lock (Gate)
        {
            if (!Said.Add(key)) return;
        }
        Console.Error.WriteLine(message);
    }

    // ---- the mark file ------------------------------------------------------------------------

    internal static HashSet<string> Marked(string dir)
    {
        try
        {
            var file = Path.Combine(dir, MarkFile);
            return File.Exists(file) ? File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void Mark(string id, bool on)
    {
        lock (Gate)
        {
            try
            {
                var ids = Marked(_dir);
                if (on ? !ids.Add(id) : !ids.Remove(id)) return;
                var file = Path.Combine(_dir, MarkFile);
                if (ids.Count == 0)
                {
                    File.Delete(file);
                    return;
                }
                Directory.CreateDirectory(_dir);
                File.WriteAllLines(file, ids);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[lock] helper mark: {ex.Message}");
            }
        }
    }

    // ---- what is possible here ----------------------------------------------------------------

    /// <summary>
    /// "direct": the account may write the policy keys itself (no service needed). "service": the CouchHUD service runs and the last
    /// try through it worked. "none": the policies of the locked couch are not active (the Settings and the list of changes say so,
    /// with the state of the service). Cached for a minute; the service's state is looked at in the background.
    /// </summary>
    public static string Mode()
    {
        lock (Gate)
        {
            if (DateTime.UtcNow - _mode.At < TimeSpan.FromMinutes(1)) return _mode.Mode;
        }
        var mode = CanWriteDirectly() ? "direct"
            : !_helperFailed && AdminService.Current.Health == ServiceHealth.Healthy ? "service" : "none";
        lock (Gate) _mode = (DateTime.UtcNow, mode);
        return mode;
    }

    /// <summary>A change (service state, setup, removal) makes the next <see cref="Mode"/> ask again.</summary>
    public static void ForgetMode()
    {
        lock (Gate) _mode = (DateTime.MinValue, _mode.Mode);
    }

    /// <summary>The service is new or back: a failure from before does not count any more.</summary>
    public static void ForgetFailure()
    {
        _helperFailed = false;
        ForgetMode();
    }

    private static bool CanWriteDirectly()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies", writable: true);
            return key is not null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>The service can set the lock's policies for this account (it runs and answers as itself).</summary>
    public static bool Installed() => AdminService.Current.Health == ServiceHealth.Healthy;

    /// <summary>
    /// The reserve task of earlier versions that switches the automatic sign-in off (kept as a reserve for the recovery, owner decision
    /// E5: it only works in the safe direction). Removed by the service's task migration in a later version.
    /// </summary>
    public static bool AutoLogonOffInstalled() => PrivilegedServices.Scheduler.Query(AutoLogonOffTask()) == 0;
}
