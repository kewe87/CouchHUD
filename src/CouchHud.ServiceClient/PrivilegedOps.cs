using CouchHud.Service.Contracts;

namespace CouchHud.ServiceClient;

/// <summary>The result of an operation: ok, or the kind of failure and the code behind it.</summary>
public sealed record OpResult(FailureKind Failure, string Code)
{
    public bool Ok => Failure == FailureKind.None;

    /// <summary>The key of the text for the user (see <see cref="ErrorMap.MessageKey"/>).</summary>
    public string MessageKey => ErrorMap.MessageKey(Failure);

    /// <summary>The service could not be used at all (as opposed to: it refused or the operation failed).</summary>
    public bool ServiceUnavailable => ErrorMap.IsUnavailable(Failure);

    public static readonly OpResult Success = new(FailureKind.None, ErrorCodes.Ok);

    public static readonly OpResult NoService = new(FailureKind.NotExpected, "notExpected");

    public static OpResult From(ServiceReply reply) => new(ErrorMap.Classify(reply), reply.Code);

    public override string ToString() => Ok ? "ok" : $"{Failure} ({Code})";
}

/// <summary>The result of a long operation: the start, and how its job ended (null when the service answered without a job).</summary>
/// <see cref="Started"/>: the service accepted the start (a later failure then is about the job or the connection while it ran).
public sealed record JobResult(OpResult Result, JobData? Job, bool Started = false)
{
    /// <summary>Started and, if there was a job, done (not failed).</summary>
    public bool Ok => Result.Ok && (Job is null || Job.State == "done");
}

/// <summary>
/// Every administrator operation of the shell. There is one way: the CouchHUD service (owner decision E11, no runas or scheduled
/// tasks in normal operation). What the service does not do, the shell does not do; the only other way is the emergency repair the
/// user starts by hand (native Windows programs with UAC, see <see cref="EmergencyRepair"/>).
/// </summary>
public interface IPrivilegedOps
{
    /// <summary>The last known state, without asking (for the UI).</summary>
    ServiceStatus LastStatus { get; }

    /// <summary>The state; <paramref name="fresh"/> asks the service now, otherwise a look of the last seconds is enough.</summary>
    Task<ServiceStatus> StatusAsync(bool fresh = false, CancellationToken cancellation = default);

    Task<OpResult> ApplyLockPoliciesAsync(IReadOnlyCollection<string> groups, CancellationToken cancellation = default);
    Task<OpResult> ReleaseLockPoliciesAsync(IReadOnlyCollection<string> groups, CancellationToken cancellation = default);
    Task<OpResult> CleanupPolicyKeysAsync(CancellationToken cancellation = default);

    Task<OpResult> PauseServicesAsync(IReadOnlyCollection<string> names, CancellationToken cancellation = default);
    Task<OpResult> ResumeServicesAsync(IReadOnlyCollection<string> names, CancellationToken cancellation = default);

    /// <summary>The password goes into the request only (wiped after sending), never into a log or onto a command line.</summary>
    Task<OpResult> SetAutoLogonAsync(string password, bool allowPasswordSignIn, CancellationToken cancellation = default);
    Task<OpResult> AutoLogonOffAsync(CancellationToken cancellation = default);

    /// <summary>Everything pending, without ids: what the service's own search finds (all of its last search, if there was one).</summary>
    Task<JobResult> InstallWindowsUpdatesAsync(IProgress<JobData>? progress, CancellationToken cancellation = default);

    /// <summary>
    /// The service searches Windows Update; the job's <see cref="JobData.Updates"/> lists what it found. Ids for
    /// <see cref="InstallWindowsUpdatesAsync(IReadOnlyCollection{string}, IProgress{JobData}?, CancellationToken)"/> must come from
    /// such a search of the service (it refuses any other id).
    /// </summary>
    Task<JobResult> SearchWindowsUpdatesAsync(IProgress<JobData>? progress, CancellationToken cancellation = default);

    /// <summary>Only the updates with these ids, which must be from the service's last <see cref="SearchWindowsUpdatesAsync"/>.</summary>
    Task<JobResult> InstallWindowsUpdatesAsync(IReadOnlyCollection<string> ids, IProgress<JobData>? progress, CancellationToken cancellation = default);

    Task<OpResult> SetRemoteFirewallAsync(IReadOnlyCollection<int> ports, CancellationToken cancellation = default);
    Task<OpResult> RemoveRemoteFirewallAsync(CancellationToken cancellation = default);
    Task<OpResult> RemoveLegacyFirewallRulesAsync(CancellationToken cancellation = default);

    /// <summary>Streams a downloaded and verified setup; the service verifies it again and starts it (it stops itself and the shell).</summary>
    Task<JobResult> InstallCouchHudUpdateAsync(SetupStreamArgs header, Stream setup, IProgress<JobData>? progress, CancellationToken cancellation = default);

    /// <summary>The old scheduled tasks of this account go (the service deletes them as SYSTEM, only after it proved it works).</summary>
    Task<OpResult> MigrateTasksAsync(CancellationToken cancellation = default);

    /// <summary>The grants the service gave this shell (memory only; see <see cref="GrantCache"/>).</summary>
    GrantCache Cache { get; }

    /// <summary>
    /// The service checks the buttons against the account's code of <paramref name="kind"/>; a right code puts its grant into <see cref="Cache"/>
    /// (a parents' code only with <paramref name="ops"/>: its grant is for those operations, each once; without them it is only checked).
    /// </summary>
    Task<CodeCheck> VerifyCodeAsync(string kind, IReadOnlyList<string> sequence, IReadOnlyList<Operation>? ops = null, CancellationToken cancellation = default);

    /// <summary>A first code of a kind for this account (also the one-time migration of the shell's local hash).</summary>
    Task<OpResult> EnrollPasscodeAsync(string hash, string kind, CancellationToken cancellation = default);

    /// <summary>A new hash for an existing code; the grant of the current code (or a parent's or an administrator's) goes along.</summary>
    Task<OpResult> ChangePasscodeAsync(string hash, string kind, CancellationToken cancellation = default);

    /// <summary>Removes a code (the couch lock or the parental controls go off).</summary>
    Task<OpResult> RemovePasscodeAsync(string kind, CancellationToken cancellation = default);

    /// <summary>
    /// The couch lock mode as the service keeps it (lowering needs the grant of the current mode, a parent's or an administrator's). Switching
    /// the code lock on from no lock carries the code in the same step (<paramref name="hash"/>, <paramref name="kind"/> couch or both).
    /// </summary>
    Task<OpResult> SetLockModeAsync(string mode, string? hash = null, string? kind = null, CancellationToken cancellation = default);

    /// <summary>Which codes the service holds for this account and its lock mode; null when it cannot be asked.</summary>
    Task<HeldCodes?> CodeStatusAsync(CancellationToken cancellation = default);

    /// <summary>Resets a damaged code file (an administrator's approval; refused while the code store is not damaged).</summary>
    Task<OpResult> ResetCodeStoreAsync(CancellationToken cancellation = default);

    /// <summary>A grant for the unlock or sign-in of this session the service saw a moment ago (into <see cref="Cache"/>).</summary>
    Task<OpResult> RequestUnlockGrantAsync(CancellationToken cancellation = default);

    /// <summary>
    /// Registers a pending administrator approval for <paramref name="ops"/> (none: a confirmation only) and returns the control code an
    /// administrator sends through UAC (<c>sc.exe control CouchHud &lt;code&gt;</c>); null when the service did not give one.
    /// </summary>
    Task<(OpResult Result, int? Control)> RequestAdminApprovalAsync(IReadOnlyList<Operation> ops, CancellationToken cancellation = default);

    /// <summary>Picks up the approval: "approved" (its grant goes into <see cref="Cache"/>), "pending" or "none".</summary>
    Task<(OpResult Result, string? State)> CollectAdminApprovalAsync(CancellationToken cancellation = default);

    /// <summary>Uninstall (elevated administrator): every account's policies, keys and old tasks go, paused services start again.</summary>
    Task<OpResult> ReleaseAllAccountsAsync(CancellationToken cancellation = default);

    /// <summary>The state of the last ReleaseAllAccounts from Status ("none", "running", "done", "failed"), null when the service could not tell.</summary>
    Task<string?> ReleaseStatusAsync(CancellationToken cancellation = default);
}

/// <summary>
/// <see cref="IPrivilegedOps"/> over the pipe: the router. Per call it decides: no service by design → <see cref="OpResult.NoService"/>
/// without a connection; otherwise the checked call (<see cref="ServiceClient"/>), one retry after "busy"/"rate limited", the grant from
/// <see cref="Grants"/> where the operation can need one (P9), and every answer updates the known state: squatting or a contradicting
/// answer turns it to alarm at once, an unreachable service to its state, so the fail-closed rules see it without waiting for the next probe.
/// </summary>
public sealed class ServiceOps : IPrivilegedOps
{
    // InstallCouchHudUpdate carries the unlock's grant so the update keeps the session (KEEPSESSION). The codes pick theirs themselves.
    private static readonly HashSet<Operation> GrantOperations =
        [Operation.ReleaseLockPolicies, Operation.CleanupPolicyKeys, Operation.SetRemoteFirewall, Operation.SetAutoLogon, Operation.InstallCouchHudUpdate];

    private readonly ServiceClient _client;
    private readonly ServiceProbe? _probe;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _probing = new(1, 1);
    private readonly object _lock = new();
    private ServiceStatus _last = ServiceStatus.Unknown;
    private bool _probedOnce;

    /// <param name="probe">Null: no service by design (unprotected copy); every operation answers <see cref="OpResult.NoService"/>.</param>
    public ServiceOps(ServiceClient client, ServiceProbe? probe, Func<DateTimeOffset>? now = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _client = client;
        _probe = probe;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
        if (probe is null) _last = new ServiceStatus(ServiceState.NotExpected, null, _now());
        Cache = new GrantCache(_now);
        Grants = (op, _) => Task.FromResult(Cache.For(op));
    }

    public GrantCache Cache { get; }

    /// <summary>How long a look at the service counts as current.</summary>
    public static readonly TimeSpan StatusMaxAge = TimeSpan.FromSeconds(15);

    /// <summary>How often a long job is asked about.</summary>
    public static readonly TimeSpan JobPoll = TimeSpan.FromSeconds(2);

    /// <summary>The grant for an operation that can need one; by default the newest fitting one from <see cref="Cache"/>.</summary>
    public Func<Operation, CancellationToken, Task<string?>>? Grants { get; set; }

    /// <summary>Raised when an answer showed squatting or a contradiction (the shell locks Windows and alarms).</summary>
    public event Action<ServiceStatus>? Alarm;

    public bool Expected => _probe is not null;

    public ServiceStatus LastStatus
    {
        get { lock (_lock) return _last; }
    }

    public async Task<ServiceStatus> StatusAsync(bool fresh = false, CancellationToken cancellation = default)
    {
        if (_probe is null) return LastStatus;
        var last = LastStatus;
        if (!fresh && last.State != ServiceState.Unknown && _now() - last.At < StatusMaxAge) return last;
        await _probing.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            last = LastStatus;
            if (!fresh && last.State != ServiceState.Unknown && _now() - last.At < StatusMaxAge) return last;
            // The first look may wait for a service that is still starting (boot); later ones only briefly.
            var status = await _probe.ProbeAsync(startIfStopped: true, _probedOnce ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(10), _now, cancellation).ConfigureAwait(false);
            _probedOnce = true;
            Remember(status);
            return status;
        }
        finally
        {
            _probing.Release();
        }
    }

    private void Remember(ServiceStatus status)
    {
        ServiceStatus before;
        lock (_lock)
        {
            before = _last;
            _last = status;
        }
        if (status.Health == ServiceHealth.Alarm && before.State != status.State) Alarm?.Invoke(status);
    }

    /// <summary>What an answer says about the service itself (beyond the operation).</summary>
    private void Learn(ServiceReply reply)
    {
        var state = reply.Outcome switch
        {
            CallOutcome.Squatted => ServiceState.Squatted,
            CallOutcome.Inconsistent => ServiceState.Inconsistent,
            CallOutcome.NotInstalled => ServiceState.NotInstalled,
            CallOutcome.NotRunning => ServiceState.Stopped,
            CallOutcome.Unreachable => ServiceState.NotResponding,
            _ => (ServiceState?)null
        };
        if (state is { } known)
        {
            Remember(new ServiceStatus(known, null, _now()));
            return;
        }
        // It answered in protocol: if the last look said otherwise, the next status question looks again.
        lock (_lock)
        {
            if (_last.State != ServiceState.Running && _last.State != ServiceState.NotExpected) _last = _last with { At = DateTimeOffset.MinValue };
        }
    }

    private async Task<OpResult> RunAsync(Operation op, Func<string?, Task<ServiceReply>> call, CancellationToken cancellation)
    {
        var (result, _) = await RunWithReplyAsync(op, call, cancellation).ConfigureAwait(false);
        return result;
    }

    private Task<(OpResult Result, ServiceReply? Reply)> RunWithReplyAsync(Operation op, Func<string?, Task<ServiceReply>> call, CancellationToken cancellation) =>
        RunWithReplyAsync(op, call, null, cancellation);

    /// <param name="explicitGrant">For the codes: the grant they picked themselves (null: by <see cref="Grants"/> for the operations that take one).</param>
    private async Task<(OpResult Result, ServiceReply? Reply)> RunWithReplyAsync(Operation op, Func<string?, Task<ServiceReply>> call, Func<string?>? explicitGrant, CancellationToken cancellation)
    {
        if (_probe is null) return (OpResult.NoService, null);
        var grant = explicitGrant is not null ? explicitGrant()
            : Grants is { } grants && GrantOperations.Contains(op) ? await grants(op, cancellation).ConfigureAwait(false) : null;
        var reply = await call(grant).ConfigureAwait(false);
        if (ErrorMap.Classify(reply) == FailureKind.Retry)
        {
            await _delay(TimeSpan.FromSeconds(1), cancellation).ConfigureAwait(false);
            reply = await call(grant).ConfigureAwait(false);
        }
        Learn(reply);
        // A grant the service no longer takes (expired, the session was locked in between) is not offered again.
        if (grant is not null && reply.Code == ErrorCodes.NeedGrant) Cache.Forget(grant);
        return (OpResult.From(reply), reply);
    }

    private async Task<JobResult> RunJobAsync(Operation op, Func<string?, Task<ServiceReply>> start, IProgress<JobData>? progress, TimeSpan maxWait, CancellationToken cancellation)
    {
        var (result, reply) = await RunWithReplyAsync(op, start, cancellation).ConfigureAwait(false);
        if (!result.Ok || reply is null) return new JobResult(result, null);
        var started = reply.DataAs(ProtocolJsonContext.Default.JobStartedData);
        if (started is null) return new JobResult(result, null, true);
        var until = _now() + maxWait;
        while (true)
        {
            var poll = await _client.CallAsync(Operation.GetJob, new GetJobArgs(started.JobId), cancellation: cancellation).ConfigureAwait(false);
            Learn(poll);
            if (!poll.Ok) return new JobResult(OpResult.From(poll), null, true);
            var job = poll.DataAs(ProtocolJsonContext.Default.JobData);
            if (job is null || job.JobId != started.JobId) return new JobResult(new OpResult(FailureKind.Inconsistent, "inconsistent"), null, true);
            progress?.Report(job);
            if (job.State is "done" or "failed") return new JobResult(result, job, true);
            if (job.State != "running") return new JobResult(new OpResult(FailureKind.Inconsistent, "inconsistent"), job, true);
            if (_now() >= until) return new JobResult(new OpResult(FailureKind.Timeout, ErrorCodes.Timeout), job, true);
            await _delay(JobPoll, cancellation).ConfigureAwait(false);
        }
    }

    // A service handler has 30 s (its own limit); an answer may take a little longer than that.
    private static readonly TimeSpan HandlerWait = TimeSpan.FromSeconds(35);

    public Task<OpResult> ApplyLockPoliciesAsync(IReadOnlyCollection<string> groups, CancellationToken cancellation = default) =>
        RunAsync(Operation.ApplyLockPolicies, g => _client.CallAsync(Operation.ApplyLockPolicies, new ApplyLockPoliciesArgs([.. groups]), g, HandlerWait, cancellation), cancellation);

    public Task<OpResult> ReleaseLockPoliciesAsync(IReadOnlyCollection<string> groups, CancellationToken cancellation = default) =>
        RunAsync(Operation.ReleaseLockPolicies, g => _client.CallAsync(Operation.ReleaseLockPolicies, new ReleaseLockPoliciesArgs([.. groups]), g, HandlerWait, cancellation), cancellation);

    public Task<OpResult> CleanupPolicyKeysAsync(CancellationToken cancellation = default) =>
        RunAsync(Operation.CleanupPolicyKeys, g => _client.CallAsync(Operation.CleanupPolicyKeys, g, HandlerWait, cancellation), cancellation);

    public Task<OpResult> PauseServicesAsync(IReadOnlyCollection<string> names, CancellationToken cancellation = default) =>
        RunAsync(Operation.PauseServices, g => _client.CallAsync(Operation.PauseServices, new ServicesArgs([.. names]), g, HandlerWait, cancellation), cancellation);

    public Task<OpResult> ResumeServicesAsync(IReadOnlyCollection<string> names, CancellationToken cancellation = default) =>
        RunAsync(Operation.ResumeServices, g => _client.CallAsync(Operation.ResumeServices, new ServicesArgs([.. names]), g, HandlerWait, cancellation), cancellation);

    public Task<OpResult> SetAutoLogonAsync(string password, bool allowPasswordSignIn, CancellationToken cancellation = default) =>
        RunAsync(Operation.SetAutoLogon, g => _client.CallAsync(Operation.SetAutoLogon, new SetAutoLogonArgs(password, allowPasswordSignIn), g, HandlerWait, cancellation), cancellation);

    public Task<OpResult> AutoLogonOffAsync(CancellationToken cancellation = default) =>
        RunAsync(Operation.AutoLogonOff, g => _client.CallAsync(Operation.AutoLogonOff, g, HandlerWait, cancellation), cancellation);

    public Task<JobResult> InstallWindowsUpdatesAsync(IProgress<JobData>? progress, CancellationToken cancellation = default) =>
        RunJobAsync(Operation.InstallWindowsUpdates, g => _client.CallAsync(Operation.InstallWindowsUpdates, g, HandlerWait, cancellation), progress, TimeSpan.FromHours(4), cancellation);

    public Task<JobResult> SearchWindowsUpdatesAsync(IProgress<JobData>? progress, CancellationToken cancellation = default) =>
        RunJobAsync(Operation.SearchWindowsUpdates, g => _client.CallAsync(Operation.SearchWindowsUpdates, g, HandlerWait, cancellation), progress, TimeSpan.FromMinutes(30), cancellation);

    public Task<JobResult> InstallWindowsUpdatesAsync(IReadOnlyCollection<string> ids, IProgress<JobData>? progress, CancellationToken cancellation = default)
    {
        // An empty list is no request at all (the service would read "all"): nothing to do.
        if (ids.Count == 0) return Task.FromResult(new JobResult(new OpResult(FailureKind.None, ErrorCodes.Ok), null));
        return RunJobAsync(Operation.InstallWindowsUpdates,
            g => _client.CallAsync(Operation.InstallWindowsUpdates, new InstallWindowsUpdatesArgs([.. ids]), g, HandlerWait, cancellation), progress, TimeSpan.FromHours(4), cancellation);
    }

    public Task<OpResult> SetRemoteFirewallAsync(IReadOnlyCollection<int> ports, CancellationToken cancellation = default) =>
        RunAsync(Operation.SetRemoteFirewall, g => _client.CallAsync(Operation.SetRemoteFirewall, new SetRemoteFirewallArgs([.. ports]), g, HandlerWait, cancellation), cancellation);

    public Task<OpResult> RemoveRemoteFirewallAsync(CancellationToken cancellation = default) =>
        RunAsync(Operation.RemoveRemoteFirewall, g => _client.CallAsync(Operation.RemoveRemoteFirewall, g, HandlerWait, cancellation), cancellation);

    public Task<OpResult> RemoveLegacyFirewallRulesAsync(CancellationToken cancellation = default) =>
        RunAsync(Operation.RemoveLegacyFirewallRules, g => _client.CallAsync(Operation.RemoveLegacyFirewallRules, g, HandlerWait, cancellation), cancellation);

    public Task<JobResult> InstallCouchHudUpdateAsync(SetupStreamArgs header, Stream setup, IProgress<JobData>? progress, CancellationToken cancellation = default) =>
        RunJobAsync(Operation.InstallCouchHudUpdate, g => _client.StreamAsync(Operation.InstallCouchHudUpdate, header, Rewound(setup), g, cancellation), progress, TimeSpan.FromMinutes(15), cancellation);

    public Task<OpResult> MigrateTasksAsync(CancellationToken cancellation = default) =>
        RunAsync(Operation.MigrateTasks, g => _client.CallAsync(Operation.MigrateTasks, g, HandlerWait, cancellation), cancellation);

    // ---- codes and grants (P9) ----------------------------------------------------------------------

    public async Task<CodeCheck> VerifyCodeAsync(string kind, IReadOnlyList<string> sequence, IReadOnlyList<Operation>? ops = null, CancellationToken cancellation = default)
    {
        var args = new VerifyCodeArgs(kind, [.. sequence], ops is { Count: > 0 } ? [.. ops.Select(o => o.ToString())] : null);
        var (result, reply) = await RunWithReplyAsync(Operation.VerifyCode,
            g => _client.CallAsync(Operation.VerifyCode, args, g, HandlerWait, cancellation), () => null, cancellation).ConfigureAwait(false);
        if (!result.Ok || reply is null) return new CodeCheck(result, null, 0, null);
        var data = reply.DataAs(ProtocolJsonContext.Default.CodeCheckData);
        var expectGrant = kind == CodeKinds.Couch || args.Ops is not null;
        if (data is null || data.Result is not (CodeCheckResults.Granted or CodeCheckResults.Matched or CodeCheckResults.Wrong or CodeCheckResults.LockedOut or CodeCheckResults.NoCode)
            || (data.Result == CodeCheckResults.Granted) != (data.Grant is not null)
            || (data.Result == CodeCheckResults.Granted && !expectGrant) || (data.Result == CodeCheckResults.Matched && expectGrant))
        {
            return new CodeCheck(new OpResult(FailureKind.Inconsistent, "inconsistent"), null, 0, null);
        }
        GrantKind? granted = null;
        if (data.Grant is { } grant)
        {
            // A grant of another kind than the code asked for is a service that contradicts itself.
            var expected = kind == CodeKinds.Parent ? GrantKind.Parent : GrantKind.Couch;
            if (GrantCache.KindOf(grant.Kind) != expected || !Cache.Set(grant)) return new CodeCheck(new OpResult(FailureKind.Inconsistent, "inconsistent"), null, 0, null);
            granted = expected;
        }
        return new CodeCheck(result, data.Result, Math.Clamp(data.WaitSeconds, 0, 24 * 3600), granted);
    }

    public Task<OpResult> EnrollPasscodeAsync(string hash, string kind, CancellationToken cancellation = default) =>
        RunCodeAsync(Operation.EnrollPasscode, new PasscodeHashArgs(hash, kind), () => Cache.ForCode(Operation.EnrollPasscode, kind), cancellation);

    public Task<OpResult> ChangePasscodeAsync(string hash, string kind, CancellationToken cancellation = default) =>
        RunCodeAsync(Operation.ChangePasscode, new PasscodeHashArgs(hash, kind), () => Cache.ForCode(Operation.ChangePasscode, kind), cancellation);

    public async Task<OpResult> RemovePasscodeAsync(string kind, CancellationToken cancellation = default)
    {
        var (result, _) = await RunWithReplyAsync(Operation.RemovePasscode,
            g => _client.CallAsync(Operation.RemovePasscode, new CodeKindArgs(kind), g, HandlerWait, cancellation), () => Cache.ForCode(Operation.RemovePasscode, kind), cancellation).ConfigureAwait(false);
        return result;
    }

    public async Task<OpResult> SetLockModeAsync(string mode, string? hash = null, string? kind = null, CancellationToken cancellation = default)
    {
        var (result, _) = await RunWithReplyAsync(Operation.SetLockMode,
            g => _client.CallAsync(Operation.SetLockMode, new LockModeArgs(mode, hash, hash is null ? null : kind ?? CodeKinds.Couch), g, HandlerWait, cancellation),
            () => Cache.For(Operation.SetLockMode), cancellation).ConfigureAwait(false);
        return result;
    }

    private async Task<OpResult> RunCodeAsync(Operation op, PasscodeHashArgs args, Func<string?> grant, CancellationToken cancellation)
    {
        var (result, _) = await RunWithReplyAsync(op, g => _client.CallAsync(op, args, g, HandlerWait, cancellation), grant, cancellation).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// The administrator's reset of a damaged code file (the service sets it aside and starts with no codes). Needs an administrator's approval
    /// (UAC, see the shell's grant flow); refused while the code store is not damaged.
    /// </summary>
    public async Task<OpResult> ResetCodeStoreAsync(CancellationToken cancellation = default)
    {
        var (result, _) = await RunWithReplyAsync(Operation.ResetCodeStore,
            g => _client.CallAsync(Operation.ResetCodeStore, g, HandlerWait, cancellation), () => Cache.Approval(Operation.ResetCodeStore), cancellation).ConfigureAwait(false);
        return result;
    }

    public async Task<HeldCodes?> CodeStatusAsync(CancellationToken cancellation = default)
    {
        if (_probe is null) return null;
        var reply = await _client.CallAsync(Operation.Status, cancellation: cancellation).ConfigureAwait(false);
        Learn(reply);
        if (!reply.Ok || reply.DataAs(ProtocolJsonContext.Default.StatusData) is not { } status) return null;
        return new HeldCodes(status.CouchCode, status.ParentCode, status.LockMode, status.LegacyLock == true, status.CodesDamaged == true);
    }

    public async Task<OpResult> RequestUnlockGrantAsync(CancellationToken cancellation = default)
    {
        var (result, reply) = await RunWithReplyAsync(Operation.RequestWindowsGrant,
            g => _client.CallAsync(Operation.RequestWindowsGrant, new RequestWindowsGrantArgs(WindowsGrantScopes.Unlock), g, HandlerWait, cancellation), () => null, cancellation).ConfigureAwait(false);
        if (!result.Ok || reply is null) return result;
        var grant = reply.DataAs(ProtocolJsonContext.Default.GrantData);
        if (grant is null || GrantCache.KindOf(grant.Kind) != GrantKind.WindowsUnlock || !Cache.Set(grant)) return new OpResult(FailureKind.Inconsistent, "inconsistent");
        return result;
    }

    public async Task<(OpResult Result, int? Control)> RequestAdminApprovalAsync(IReadOnlyList<Operation> ops, CancellationToken cancellation = default)
    {
        var args = new AdminApprovalArgs([.. ops.Select(o => o.ToString())]);
        var (result, reply) = await RunWithReplyAsync(Operation.RequestAdminApproval,
            g => _client.CallAsync(Operation.RequestAdminApproval, args, g, HandlerWait, cancellation), () => null, cancellation).ConfigureAwait(false);
        if (!result.Ok || reply is null) return (result, null);
        var data = reply.DataAs(ProtocolJsonContext.Default.AdminApprovalData);
        // Only the codes SERVICE_USER_DEFINED_CONTROL allows; anything else is a service that contradicts itself.
        if (data is not { Control: >= 128 and <= 255 }) return (new OpResult(FailureKind.Inconsistent, "inconsistent"), null);
        return (result, data.Control);
    }

    public async Task<(OpResult Result, string? State)> CollectAdminApprovalAsync(CancellationToken cancellation = default)
    {
        var (result, reply) = await RunWithReplyAsync(Operation.CollectAdminApproval,
            g => _client.CallAsync(Operation.CollectAdminApproval, g, HandlerWait, cancellation), () => null, cancellation).ConfigureAwait(false);
        if (!result.Ok || reply is null) return (result, null);
        var data = reply.DataAs(ProtocolJsonContext.Default.AdminApprovalResult);
        if (data is null || data.State is not ("approved" or "pending" or "none")) return (new OpResult(FailureKind.Inconsistent, "inconsistent"), null);
        if (data.Grant is { } grant && (data.State != "approved" || GrantCache.KindOf(grant.Kind) != GrantKind.WindowsAdmin || !Cache.Set(grant)))
        {
            return (new OpResult(FailureKind.Inconsistent, "inconsistent"), null);
        }
        return (result, data.State);
    }

    // The release of every account may take minutes (hives of signed-out accounts are loaded); the service's own limit decides.
    private static readonly TimeSpan ReleaseWait = TimeSpan.FromSeconds(40);

    public Task<OpResult> ReleaseAllAccountsAsync(CancellationToken cancellation = default) =>
        RunAsync(Operation.ReleaseAllAccounts, g => _client.CallAsync(Operation.ReleaseAllAccounts, g, ReleaseWait, cancellation), cancellation);

    public async Task<string?> ReleaseStatusAsync(CancellationToken cancellation = default)
    {
        if (_probe is null) return null;
        var reply = await _client.CallAsync(Operation.Status, cancellation: cancellation).ConfigureAwait(false);
        Learn(reply);
        return reply.Ok ? reply.DataAs(ProtocolJsonContext.Default.StatusData)?.Release : null;
    }

    /// <summary>A retry sends the setup from its start again.</summary>
    private static Stream Rewound(Stream setup)
    {
        if (setup.CanSeek) setup.Position = 0;
        return setup;
    }
}
