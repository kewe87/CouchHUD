using System.Collections.Concurrent;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using CouchHud.Core.Guard;
using CouchHud.Core.Net;
using CouchHud.Core.Platform;
using CouchHud.Core.Security;
using CouchHud.Privileged;
using CouchHud.Service.Authorization;
using CouchHud.Service.Contracts;
using CouchHud.Service.Logging;
using CouchHud.Service.State;

namespace CouchHud.Service.Dispatch;

/// <summary>What the operations of package P5 work with; every Windows part behind an interface of CouchHud.Privileged.</summary>
/// <param name="AppDirectory">The program folder (<c>{app}</c>, above the service's own folder): the firewall rule's program and the old policy files.</param>
/// <param name="ServiceOnList">The service's own fixed list of pausable services (<c>GamingServices</c>); <see cref="ServicePolicy.Denied"/> wins over it.</param>
/// <param name="ServiceTimeout">How long one service may take to stop or start.</param>
public sealed record OperationsAParts(
    ServiceStateStore State,
    IUserHives Hives,
    IScm Scm,
    IFirewall Firewall,
    IAutoLogon AutoLogon,
    IAccountLookup Accounts,
    ILogonVerifier Logon,
    ITaskScheduler Tasks,
    PolicyInstaller Policies,
    string AppDirectory,
    IEventSink Log,
    RecentEvents? Events,
    Func<string, bool> ServiceOnList,
    Func<DateTimeOffset> Now,
    TimeSpan ServiceTimeout,
    ISessions? Sessions = null);

/// <summary>
/// The operations of package P5 ("Operationen A"): lock policies, cleanup of keys, pausing services, automatic sign-in, the remote's
/// firewall rule and the migration away from the old SYSTEM tasks. Registered next to the basic handlers with one line
/// (<see cref="Register"/>), together with the state checks they own.
/// </summary>
/// <remarks>
/// Rules for every handler here: the account is <c>Caller.Sid</c> from the token, never an argument; names come from fixed lists
/// (groups, the service list, the rule name, task names matched against a pattern); what the service changes is first noted in its
/// state, so it only ever undoes what it did; a repeated call changes nothing more; no password reaches a log; work per account and
/// per area is serialized with gates that honour the cancellation token.
/// </remarks>
public sealed class OperationsA(OperationsAParts parts)
{
    private static readonly Regex TaskLeaf = new(@"^(Policy-(lock|taskmgr)-(on|off)|Policy-cleanup|(Couch|PC)-[A-Za-z0-9_.\-]{1,80})\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _accountGates = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _servicesGate = new(1, 1);
    private readonly SemaphoreSlim _autoLogonGate = new(1, 1);
    private readonly SemaphoreSlim _firewallGate = new(1, 1);
    private readonly ConcurrentDictionary<string, PasswordAttemptLimiter> _passwordAttempts = new(StringComparer.Ordinal);
    private volatile string _release = "none";

    /// <summary>The handlers and state checks of P5, added to the shared registry of the service.</summary>
    public static OperationsA Register(Dictionary<Operation, Handler> handlers, StateChecks checks, OperationsAParts parts, ServiceState? serviceState = null)
    {
        var operations = new OperationsA(parts);
        operations.AddTo(handlers, checks);
        operations.Start();
        if (serviceState is not null)
        {
            serviceState.MigrationLookup = operations.MigrationOf;
            serviceState.ReleaseLookup = () => operations.ReleaseStatus;
        }
        return operations;
    }

    /// <summary>
    /// Once at start, before any request: the automatic sign-in that CouchHUD 0.4.0 set up (its machine-wide marker, written by an
    /// administrator) gets its account as owner in the state, so the state check only has to read the state.
    /// </summary>
    public void Start()
    {
        if (parts.State.Current is not { AutoLogonOwner: null }) return;
        if (parts.AutoLogon.SetUpBy() is not { } marker || parts.Accounts.SidOf(marker) is not { } sid || !ServiceStateStore.IsUserSid(sid)) return;
        if (parts.State.Update(s =>
            {
                if (s.AutoLogonOwner is not null) return false;
                s.AutoLogonOwner = sid;
                return true;
            }) == StateUpdate.Saved)
        {
            parts.Log.Info($"[autologon] set up by an earlier version for {sid}: noted as its owner");
        }
    }

    /// <summary>The task migration of one account for Hello and Status: "migrated", "notMigrated", or "unavailable" without a valid state.</summary>
    public string MigrationOf(string sid)
    {
        if (parts.State.Current is not { } state) return "unavailable";
        return state.Accounts.TryGetValue(sid, out var account) && account.Migrated ? "migrated" : "notMigrated";
    }

    /// <summary>
    /// The last ReleaseAllAccounts or ResetServiceState: "none", "running", "done" or "failed" (some account left). Those may run past
    /// the handler's time limit (the dispatcher answers "timeout" but the work goes on, holding its work slot); callers poll Status.
    /// </summary>
    public string ReleaseStatus => _release;

    public void AddTo(Dictionary<Operation, Handler> handlers, StateChecks checks)
    {
        AddHandlers(handlers);
        AddChecks(checks);
    }

    public void AddHandlers(Dictionary<Operation, Handler> handlers)
    {
        handlers[Operation.ApplyLockPolicies] = Sync(ApplyLockPolicies);
        handlers[Operation.ReleaseLockPolicies] = Sync(ReleaseLockPolicies);
        handlers[Operation.CleanupPolicyKeys] = Sync(CleanupPolicyKeys);
        handlers[Operation.PauseServices] = Sync(PauseServices);
        handlers[Operation.ResumeServices] = Sync(ResumeServices);
        handlers[Operation.SetAutoLogon] = Sync(SetAutoLogon);
        handlers[Operation.AutoLogonOff] = Sync(AutoLogonOff);
        handlers[Operation.SetRemoteFirewall] = Sync(SetRemoteFirewall);
        handlers[Operation.RemoveRemoteFirewall] = Sync(RemoveRemoteFirewall);
        handlers[Operation.RemoveLegacyFirewallRules] = Sync(RemoveLegacyFirewallRules);
        handlers[Operation.MigrateTasks] = Sync(MigrateTasks);
        handlers[Operation.ReleaseAllAccounts] = Sync(ReleaseAllAccounts);
        handlers[Operation.ResetServiceState] = Sync(ResetServiceState);
    }

    public void AddChecks(StateChecks checks)
    {
        checks.Register(StateCheck.GroupsSetByService, new StateCheckFunc(GroupsSetByService));
        checks.Register(StateCheck.KeysOwnedByService, new StateCheckFunc(KeysOwnedByService));
        checks.Register(StateCheck.PausedByService, new StateCheckFunc(PausedByService));
        checks.Register(StateCheck.AutoLogonOwner, new StateCheckFunc(AutoLogonOwner));
    }

    /// <summary>
    /// Runs a handler; a registry key or file the service refuses to touch (a link, an unprotected Policies key, access denied) ends
    /// it with "failed" and a log line instead of an internal error.
    /// </summary>
    private Handler Sync(Func<RequestContext, CancellationToken, byte[]> work) => (context, cancellation) =>
    {
        try
        {
            return ValueTask.FromResult(work(context, cancellation));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            parts.Log.Error($"[refused] {context.Request.Operation} for {context.Caller.Sid}: {ex.Message}");
            return ValueTask.FromResult(Fail(ErrorCodes.Failed));
        }
    };

    // ---- state checks: they read the cached state only (no copy, no file, no registry) ---------------

    /// <summary>ReleaseLockPolicies: every group asked for is one the service set for this SID.</summary>
    internal AuthorizationDecision GroupsSetByService(CallerInfo caller, ValidatedRequest request)
    {
        if (parts.State.Current is not { } state) return AuthorizationDecision.Denied("state unavailable");
        if (request.Args is not ReleaseLockPoliciesArgs { Groups: { Length: > 0 } groups }) return AuthorizationDecision.Denied("no groups");
        var owned = state.Accounts.TryGetValue(caller.Sid, out var account) ? account.Groups : [];
        if (!groups.All(g => owned.Contains(g, StringComparer.Ordinal))) return AuthorizationDecision.Denied("group not set by the service");
        return LegacyProof(caller);
    }

    /// <summary>
    /// A lock of 0.4.0 that no administrator confirmed yet (<see cref="IsLegacyLocked"/>): whatever code the account holds may be one its own
    /// user planted (the shell's config was the user's to write, and enrolling at an open couch is free), so the table's "no code enrolled" or
    /// "the couch code" is not enough. A grant then, and only an administrator's counts (<see cref="LockView.Legacy"/>); or an elevated
    /// administrator. Once an administrator confirmed the account's code or mode, the table decides as always (security review H-1).
    /// </summary>
    private AuthorizationDecision LegacyProof(CallerInfo caller) =>
        caller.IsElevatedAdmin || !IsLegacyLocked(caller.Sid) ? AuthorizationDecision.Allowed : AuthorizationDecision.GrantNeeded;

    /// <summary>
    /// Whether the account's lock is one of 0.4.0 that no administrator confirmed yet (security review H-1, second round): taken over
    /// (<see cref="AccountState.LegacyLock"/>), or about to be, before the take-over even ran: the account waits for its migration
    /// (<see cref="LegacyPending"/>), or the old setup's .reg files of the account are still in the protected program folder (the migration
    /// removes them only after the take-over). Without a state the files decide. A failure counts as yes (fail-closed, see CodeHandlers).
    /// So nothing the account's user sets in the seconds between the service's start and the take-over is trusted afterwards.
    /// </summary>
    public bool IsLegacyLocked(string sid)
    {
        if (parts.State.Current is { } state && state.Accounts.TryGetValue(sid, out var account) && account.LegacyLock) return true;
        if (!ServiceStateStore.IsUserSid(sid)) return false;
        if (_legacyPending.ContainsKey(sid)) return true;
        var directory = parts.Policies.UserPolicyDirectory(sid);
        return PolicyGroups.All.SelectMany(LegacyAdoption.EvidenceFiles).Any(name => File.Exists(Path.Combine(directory, name)));
    }

    /// <summary>
    /// An administrator confirmed the account's code or mode: its lock taken over from 0.4.0 is the service's own now. True when the account's
    /// lock is no lock of 0.4.0 any more; false while the take-over itself still waits (the migration then asks for the confirmation again).
    /// </summary>
    public bool EndLegacyLock(string sid)
    {
        parts.State.Update(s =>
        {
            if (!s.Accounts.TryGetValue(sid, out var account) || !account.LegacyLock) return false;
            account.LegacyLock = false;
            return true;
        });
        return !IsLegacyLocked(sid);
    }

    /// <summary>
    /// Forgets everything the account held in the code store (both codes and the mode), when its lock of 0.4.0 is taken over (security review
    /// H-1, second round): the codes may have been planted by the account's user before. Set by the service; null: nothing to forget (tests).
    /// </summary>
    public Func<string, bool>? ForgetCodes { get; set; }

    /// <summary>CleanupPolicyKeys: the service noted keys it created for this SID.</summary>
    internal AuthorizationDecision KeysOwnedByService(CallerInfo caller, ValidatedRequest request)
    {
        if (parts.State.Current is not { } state) return AuthorizationDecision.Denied("state unavailable");
        return state.Accounts.TryGetValue(caller.Sid, out var account) && account.CreatedKeys.Count > 0
            ? LegacyProof(caller)
            : AuthorizationDecision.Denied("no keys of the service");
    }

    /// <summary>ResumeServices: the caller's SID holds every one of the services (it paused them through the service).</summary>
    internal AuthorizationDecision PausedByService(CallerInfo caller, ValidatedRequest request)
    {
        if (parts.State.Current is not { } state) return AuthorizationDecision.Denied("state unavailable");
        if (request.Args is not ServicesArgs { Names: { Length: > 0 } names }) return AuthorizationDecision.Denied("no names");
        return names.All(n => state.Paused.TryGetValue(n, out var entry) && entry.Holders.Contains(caller.Sid, StringComparer.Ordinal))
            ? AuthorizationDecision.Allowed
            : AuthorizationDecision.Denied("service not paused by this account");
    }

    /// <summary>
    /// AutoLogonOff: an elevated administrator, or the account that owns the automatic sign-in in the state (set up through the
    /// service, or taken over from the marker of an earlier version at start, see <see cref="Start"/>). The handler checks again.
    /// </summary>
    internal AuthorizationDecision AutoLogonOwner(CallerInfo caller, ValidatedRequest request)
    {
        if (parts.State.Current is not { } state) return AuthorizationDecision.Denied("state unavailable");
        return MayTurnOff(state, caller) ? AuthorizationDecision.Allowed : AuthorizationDecision.Denied("autologon not owned by the caller");
    }

    private static bool MayTurnOff(StateData state, CallerInfo caller) =>
        caller.IsElevatedAdmin || (state.AutoLogonOwner is { } owner && owner.Equals(caller.Sid, StringComparison.Ordinal));

    // ---- lock policies ---------------------------------------------------------------------------

    /// <summary>
    /// Sets the groups in the caller's hive. A group is taken over only when none of its values exists yet (<see cref="PolicyGroups.Pristine"/>);
    /// the group and the Policies keys that do not exist yet are noted first, then the fixed values are written and checked. A group that
    /// is set already (by the service: nothing to do; by someone else: not ours) is left as it is. The answer names the groups the
    /// service holds set now.
    /// </summary>
    private byte[] ApplyLockPolicies(RequestContext context, CancellationToken cancellation)
    {
        var sid = context.Caller.Sid;
        var groups = Groups(context.Request.GetArgs<ApplyLockPoliciesArgs>().Groups);
        using var gate = Enter(AccountGate(sid), cancellation);
        if (parts.State.Snapshot() is not { } state) return Fail(ErrorCodes.Unavailable);
        using var hive = parts.Hives.OpenLoaded(sid);
        if (hive is null) return Fail(ErrorCodes.Unavailable);
        var registry = hive.Registry;
        if (_legacyPending.ContainsKey(sid))
        {
            // Old tasks waited for this sign-in: take over their lock first, so it is held instead of "set by someone else".
            Migrate(sid, registry, cancellation);
            if (parts.State.Snapshot() is not { } migrated) return Fail(ErrorCodes.Unavailable);
            state = migrated;
        }
        var held = new List<string>();
        foreach (var group in groups)
        {
            cancellation.ThrowIfCancellationRequested();
            var owned = state.Accounts.TryGetValue(sid, out var account) && account.Groups.Contains(group.Id, StringComparer.Ordinal);
            if (owned && PolicyGroups.Applied(registry, group) && ListComplete(registry, group))
            {
                held.Add(group.Id);
                continue;
            }
            if (!owned && !PolicyGroups.Pristine(registry, group))
            {
                // Set by someone else (a Group Policy, an administrator, the old tasks): not ours to set, and not ours to remove later.
                parts.Log.Info($"[policies] {group.Id} of {sid} not set: the account has values there already, they stay as they are");
                continue;
            }
            if (!owned)
            {
                var missing = PolicyGroups.MissingCandidates(registry);
                var noted = parts.State.Update(s =>
                {
                    var a = s.Account(sid);
                    a.Groups.Add(group.Id);
                    a.CreatedKeys = PolicyGroups.MergeCleanupKeys(missing, a.CreatedKeys).ToList();
                    return true;
                });
                if (noted != StateUpdate.Saved) return Fail(ErrorCodes.Unavailable);
                state = parts.State.Snapshot()!;
            }
            if (!Write(registry, group) || !PolicyGroups.Applied(registry, group))
            {
                // Half set: take back exactly what is ours, and forget the group only when that worked.
                var left = PolicyRelease.RemoveOurs(registry, [group]);
                if (left.Count == 0) Forget(sid, group.Id);
                parts.Log.Error($"[policies] {group.Id} of {sid} could not be set");
                return Fail(ErrorCodes.Failed);
            }
            held.Add(group.Id);
            parts.Log.Info($"[policies] {group.Id} set for {sid}");
            parts.Events?.Add(sid, "policies", $"set: {group.Id}");
        }
        return Ok(new LockPoliciesData([.. held]));
    }

    /// <summary>The group's list key holds all our programs (a group of only DWORDs: always).</summary>
    private static bool ListComplete(IRegistry registry, PolicyGroup group) =>
        group.Changes.Where(c => c.Kind == GuardKind.RegistryString).All(c => registry.ReadValues(c.Location).Values.Any(v => v.Trim().Equals(c.Value, StringComparison.OrdinalIgnoreCase)));

    private static bool Write(IRegistry registry, PolicyGroup group)
    {
        try
        {
            foreach (var change in group.Changes)
            {
                if (change.Kind == GuardKind.RegistryDword) registry.WriteDword(change.Location, change.Name, int.Parse(change.Value, System.Globalization.CultureInfo.InvariantCulture));
                else if (change.Kind == GuardKind.RegistryString) registry.WriteString(change.Location, change.Name, change.Value);
            }
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or ArgumentException)
        {
            return false;
        }
    }

    private void Forget(string sid, string groupId) => parts.State.Update(s =>
    {
        if (!s.Accounts.TryGetValue(sid, out var a) || !a.Groups.Remove(groupId)) return false;
        s.Tidy(sid);
        return true;
    });

    /// <summary>
    /// Takes the groups out of the caller's hive: only the fixed values with exactly the value the service wrote and our programs in
    /// the DisallowRun list (<see cref="PolicyRelease.RemoveOurs"/>). A group is forgotten when nothing of it is left.
    /// </summary>
    private byte[] ReleaseLockPolicies(RequestContext context, CancellationToken cancellation)
    {
        var sid = context.Caller.Sid;
        var groups = Groups(context.Request.GetArgs<ReleaseLockPoliciesArgs>().Groups);
        using var gate = Enter(AccountGate(sid), cancellation);
        if (parts.State.Snapshot() is not { } state) return Fail(ErrorCodes.Unavailable);
        var owned = state.Accounts.TryGetValue(sid, out var account) ? account.Groups : [];
        // The state check passed before the gate; another request may have released in between.
        groups = groups.Where(g => owned.Contains(g.Id, StringComparer.Ordinal)).ToList();
        if (groups.Count == 0) return Ok(new LockPoliciesData([]));
        using var hive = parts.Hives.OpenLoaded(sid);
        if (hive is null) return Fail(ErrorCodes.Unavailable);
        var released = new List<string>();
        var failed = false;
        foreach (var group in groups)
        {
            cancellation.ThrowIfCancellationRequested();
            if (PolicyRelease.RemoveOurs(hive.Registry, [group]).Count > 0)
            {
                parts.Log.Error($"[policies] {group.Id} of {sid}: values stay");
                failed = true;
                continue;
            }
            if (parts.State.Update(s =>
                {
                    if (!s.Accounts.TryGetValue(sid, out var a) || !a.Groups.Remove(group.Id)) return false;
                    s.Tidy(sid);
                    return true;
                }) is StateUpdate.WriteFailed or StateUpdate.Unavailable)
            {
                failed = true;
                continue;
            }
            released.Add(group.Id);
            parts.Log.Info($"[policies] {group.Id} released for {sid}");
            parts.Events?.Add(sid, "policies", $"released: {group.Id}");
        }
        return failed ? Fail(ErrorCodes.Failed) : Ok(new LockPoliciesData([.. released]));
    }

    /// <summary>
    /// Removes the Policies keys the service created for the caller, once no group is held any more and only when every one is
    /// empty apart from other such keys (<see cref="PolicyGroups.CleanupAllowed"/>): a deleted key cannot be given back.
    /// </summary>
    private byte[] CleanupPolicyKeys(RequestContext context, CancellationToken cancellation)
    {
        var sid = context.Caller.Sid;
        using var gate = Enter(AccountGate(sid), cancellation);
        if (parts.State.Snapshot() is not { } state) return Fail(ErrorCodes.Unavailable);
        if (!state.Accounts.TryGetValue(sid, out var account) || account.CreatedKeys.Count == 0) return Ok();
        if (account.Groups.Count > 0) return Fail(ErrorCodes.Failed);
        using var hive = parts.Hives.OpenLoaded(sid);
        if (hive is null) return Fail(ErrorCodes.Unavailable);
        return CleanupKeys(sid, hive.Registry, account.CreatedKeys) ? Ok() : Fail(ErrorCodes.Failed);
    }

    /// <summary>Deletes the listed keys when allowed and forgets the ones that are gone. True when none is left.</summary>
    private bool CleanupKeys(string sid, IRegistry registry, IReadOnlyList<string> created)
    {
        var existing = created.Where(registry.KeyExists).ToList();
        if (existing.Count > 0)
        {
            if (!PolicyGroups.CleanupAllowed(registry, existing, created))
            {
                parts.Log.Info($"[policies] keys of {sid} stay: something else lives in them");
                return false;
            }
            LockHardening.RemoveEmpty(registry, existing);
        }
        var gone = created.Where(k => !registry.KeyExists(k)).ToList();
        var saved = parts.State.Update(s =>
        {
            if (!s.Accounts.TryGetValue(sid, out var a)) return false;
            var before = a.CreatedKeys.Count;
            a.CreatedKeys.RemoveAll(k => gone.Contains(k, StringComparer.OrdinalIgnoreCase));
            s.Tidy(sid);
            return a.CreatedKeys.Count != before;
        });
        if (saved is StateUpdate.WriteFailed or StateUpdate.Unavailable) return false;
        if (gone.Count > 0) parts.Log.Info($"[policies] {gone.Count} empty keys of {sid} removed");
        return gone.Count == created.Count;
    }

    private static List<PolicyGroup> Groups(string[] ids) => ids.Select(PolicyGroups.ById).OfType<PolicyGroup>().ToList();

    // ---- services --------------------------------------------------------------------------------

    /// <summary>
    /// Pauses the services for the caller's account: the account is added to the service's holders (a set: pausing twice counts once),
    /// and a running service is stopped. The state notes the stop before it happens, so a resume only starts what the service stopped.
    /// </summary>
    private byte[] PauseServices(RequestContext context, CancellationToken cancellation)
    {
        var sid = context.Caller.Sid;
        var names = context.Request.GetArgs<ServicesArgs>().Names;
        if (!names.All(n => ServicePolicy.IsAllowed(n, parts.ServiceOnList))) return Fail(ErrorCodes.Denied);
        using var gate = Enter(_servicesGate, cancellation);
        var held = new List<string>();
        var failed = false;
        foreach (var name in names)
        {
            cancellation.ThrowIfCancellationRequested();
            if (parts.State.Snapshot() is not { } state) return Fail(ErrorCodes.Unavailable);
            var before = state.Paused.TryGetValue(name, out var entry) ? entry : null;
            var running = parts.Scm.IsRunning(name) == true;
            var noted = parts.State.Update(s =>
            {
                if (!s.Paused.TryGetValue(name, out var e)) s.Paused[name] = e = new PausedService();
                var changed = false;
                if (!e.Holders.Contains(sid, StringComparer.Ordinal))
                {
                    e.Holders.Add(sid);
                    changed = true;
                }
                if (running && !e.StoppedByService)
                {
                    e.StoppedByService = true;
                    changed = true;
                }
                return changed;
            });
            if (noted is StateUpdate.Unavailable or StateUpdate.WriteFailed) return Fail(ErrorCodes.Unavailable);
            if (running && !parts.Scm.Stop(name, parts.ServiceTimeout))
            {
                parts.Log.Error($"[services] {name} did not stop");
                // Back to what was noted before this request.
                parts.State.Update(s =>
                {
                    if (before is null) s.Paused.Remove(name);
                    else s.Paused[name] = before;
                    return true;
                });
                failed = true;
                continue;
            }
            held.Add(name);
            if (running) parts.Log.Info($"[services] {name} paused for {sid}");
        }
        if (held.Count > 0) parts.Events?.Add(sid, "services", $"paused: {string.Join(", ", held)}");
        return failed ? Fail(ErrorCodes.Failed) : Ok(new ServicesData([.. held]));
    }

    /// <summary>
    /// Gives the caller's hold on the services back. The last holder's resume starts the service again, but only when the service
    /// stopped it and it normally starts with Windows; a start that fails keeps the hold, so the resume can be tried again.
    /// </summary>
    private byte[] ResumeServices(RequestContext context, CancellationToken cancellation)
    {
        var sid = context.Caller.Sid;
        var names = context.Request.GetArgs<ServicesArgs>().Names;
        if (!names.All(n => ServicePolicy.IsAllowed(n, parts.ServiceOnList))) return Fail(ErrorCodes.Denied);
        using var gate = Enter(_servicesGate, cancellation);
        var resumed = new List<string>();
        var failed = false;
        foreach (var name in names)
        {
            cancellation.ThrowIfCancellationRequested();
            if (parts.State.Snapshot() is not { } state) return Fail(ErrorCodes.Unavailable);
            if (!state.Paused.TryGetValue(name, out var entry) || !entry.Holders.Contains(sid, StringComparer.Ordinal)) continue;
            var last = entry.Holders.Count == 1;
            if (last && entry.StoppedByService && parts.Scm.GetStartType(name) == ServiceStartMode.Automatic && !parts.Scm.Start(name, parts.ServiceTimeout))
            {
                parts.Log.Error($"[services] {name} did not start again; the hold of {sid} stays");
                failed = true;
                continue;
            }
            var saved = parts.State.Update(s =>
            {
                if (!s.Paused.TryGetValue(name, out var e) || !e.Holders.Remove(sid)) return false;
                if (e.Holders.Count == 0) s.Paused.Remove(name);
                return true;
            });
            if (saved is StateUpdate.Unavailable or StateUpdate.WriteFailed)
            {
                failed = true;
                continue;
            }
            resumed.Add(name);
            if (last) parts.Log.Info($"[services] {name} resumed by {sid}");
        }
        if (resumed.Count > 0) parts.Events?.Add(sid, "services", $"resumed: {string.Join(", ", resumed)}");
        return failed ? Fail(ErrorCodes.Failed) : Ok(new ServicesData([.. resumed]));
    }

    // ---- automatic sign-in -----------------------------------------------------------------------

    /// <summary>
    /// The automatic sign-in for the caller's own account (with a grant or as an elevated administrator, see the table). The account comes from the
    /// token's SID; the password is checked with LogonUser and the logon token's SID must be the caller's. Wrong passwords count
    /// towards a pause per account, as in the shell. The owner is noted before Winlogon is changed and forgotten again on failure.
    /// </summary>
    private byte[] SetAutoLogon(RequestContext context, CancellationToken cancellation)
    {
        var sid = context.Caller.Sid;
        // The password arrives as an immutable .NET string (read by the validator from the wiped message buffer): it cannot be
        // cleared and lives until the garbage collector takes it. Accepted (owner decision); only the char[] copy for LSA is wiped.
        var args = context.Request.GetArgs<SetAutoLogonArgs>();
        using var gate = Enter(_autoLogonGate, cancellation);
        if (parts.State.Snapshot() is not { } state) return Fail(ErrorCodes.Unavailable);
        if (parts.Accounts.Lookup(sid) is not { } account) return Fail(ErrorCodes.Failed);
        var attempts = _passwordAttempts.GetOrAdd(sid, _ => new PasswordAttemptLimiter(parts.Now));
        if (attempts.Wait > TimeSpan.Zero) return Fail(ErrorCodes.RateLimited);
        var logon = parts.Logon.Verify(account.User, account.Domain.Length == 0 ? null : account.Domain, args.Password);
        if (!logon.Succeeded)
        {
            if (logon.Error == LogonErrors.LogonFailure) attempts.Failed();
            parts.Log.Error($"[autologon] password check for {sid} failed: Windows error {logon.Error}");
            return Fail(ErrorCodes.VerificationFailed);
        }
        attempts.Succeeded();
        if (!string.Equals(logon.Sid, sid, StringComparison.OrdinalIgnoreCase))
        {
            parts.Log.Error($"[autologon] the password check of {sid} signed in another account: refused");
            return Fail(ErrorCodes.Denied);
        }
        var previousOwner = state.AutoLogonOwner;
        if (parts.State.Update(s =>
            {
                s.AutoLogonOwner = sid;
                return true;
            }) != StateUpdate.Saved) return Fail(ErrorCodes.Unavailable);
        var password = args.Password.ToCharArray();
        var done = false;
        try
        {
            var code = parts.AutoLogon.TurnOn(password, account.User, account.Domain);
            if (code != 0)
            {
                parts.Log.Error($"[autologon] not set for {sid}: code {code}");
                return Fail(ErrorCodes.Failed);
            }
            if (args.AllowPasswordSignIn) parts.AutoLogon.AllowPasswordSignIn();
            done = true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            parts.Log.Error($"[autologon] not set for {sid}: {ex.GetType().Name}");
            return Fail(ErrorCodes.Failed);
        }
        finally
        {
            Array.Clear(password);
            // Any way out that is not success (a failure code, any exception, a cancellation): the earlier owner again.
            if (!done) RestoreOwner(previousOwner);
        }
        parts.Log.Info($"[autologon] on for {sid}");
        parts.Events?.Add(sid, "autologon", "on");
        return Ok();
    }

    private void RestoreOwner(string? owner) => parts.State.Update(s =>
    {
        s.AutoLogonOwner = owner;
        return true;
    });

    /// <summary>Switches the automatic sign-in off (the owner or an administrator, see <see cref="AutoLogonOwner"/>) and forgets the owner.</summary>
    private byte[] AutoLogonOff(RequestContext context, CancellationToken cancellation)
    {
        var sid = context.Caller.Sid;
        using var gate = Enter(_autoLogonGate, cancellation);
        if (parts.State.Current is not { } state) return Fail(ErrorCodes.Unavailable);
        // Checked again inside the gate: the owner may have changed since the state check ran.
        if (!MayTurnOff(state, context.Caller)) return Fail(ErrorCodes.Denied);
        try
        {
            if (parts.AutoLogon.TurnOff() != 0) return Fail(ErrorCodes.Failed);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            parts.Log.Error($"[autologon] not switched off: {ex.GetType().Name}");
            return Fail(ErrorCodes.Failed);
        }
        RestoreOwner(null);
        parts.Log.Info($"[autologon] off by {sid}");
        parts.Events?.Add(sid, "autologon", "off");
        return Ok();
    }

    // ---- firewall --------------------------------------------------------------------------------

    private string ProgramPath => Path.Combine(parts.AppDirectory, "CouchHud.exe");

    /// <summary>
    /// Opens the remote's ports: the rule's name, program, protocol and scope are fixed (<see cref="FirewallRule"/>), the ports passed
    /// the validator (TCP, 1024–65535, at most two). A rule of our name that is not an inbound allow rule is replaced; the verifiably
    /// ours of the earlier name go.
    /// </summary>
    private byte[] SetRemoteFirewall(RequestContext context, CancellationToken cancellation)
    {
        var sid = context.Caller.Sid;
        var ports = context.Request.GetArgs<SetRemoteFirewallArgs>().Ports;
        if (ports is not { Length: >= 1 and <= RequestValidator.MaxPorts } || ports.Any(p => p is < RequestValidator.MinPort or > 65535)) return Fail(ErrorCodes.BadArgument);
        using var gate = Enter(_firewallGate, cancellation);
        if (!parts.State.IsAvailable) return Fail(ErrorCodes.Unavailable);
        var exe = ProgramPath;
        var rules = parts.Firewall.Rules();
        var exists = rules is null ? parts.Firewall.Netsh(FirewallRule.ShowArgs()) == 0 : FirewallRule.Exists(rules);
        if (rules is not null && FirewallRule.HasWrongRule(rules))
        {
            if (parts.Firewall.Netsh(FirewallRule.DeleteArgs()) != 0) return Fail(ErrorCodes.Failed);
            exists = false;
        }
        // Noted first: whatever happens next, the service knows the rule may be there and takes it away again.
        if (parts.State.Update(s =>
            {
                s.FirewallPorts = [.. ports];
                return true;
            }) != StateUpdate.Saved) return Fail(ErrorCodes.Unavailable);
        cancellation.ThrowIfCancellationRequested();
        if (parts.Firewall.Netsh(FirewallRule.AllowArgs(exists, exe, ports)) != 0)
        {
            parts.Log.Error("[firewall] the rule could not be set");
            return Fail(ErrorCodes.Failed);
        }
        if (rules is not null)
        {
            foreach (var legacy in FirewallRule.OurLegacyRules(rules, exe, [.. ports, .. FirewallOps.DefaultPorts])) parts.Firewall.Netsh(FirewallRule.DeleteLegacyArgs(legacy));
        }
        parts.Log.Info($"[firewall] remote ports {string.Join(",", ports)} open, asked by {sid}");
        parts.Events?.Add(sid, "firewall", $"open: {string.Join(",", ports)}");
        return Ok();
    }

    /// <summary>Removes the remote's rule (by its own name only) and the verifiably ours of the earlier name.</summary>
    private byte[] RemoveRemoteFirewall(RequestContext context, CancellationToken cancellation)
    {
        var sid = context.Caller.Sid;
        using var gate = Enter(_firewallGate, cancellation);
        if (!parts.State.IsAvailable) return Fail(ErrorCodes.Unavailable);
        var rules = parts.Firewall.Rules();
        if (rules is null || FirewallRule.Exists(rules))
        {
            parts.Firewall.Netsh(FirewallRule.DeleteArgs());
        }
        var stays = rules is null ? parts.Firewall.Netsh(FirewallRule.ShowArgs()) == 0 : parts.Firewall.Rules() is { } after && FirewallRule.Exists(after);
        if (stays) return Fail(ErrorCodes.Failed);
        if (rules is not null) RemoveLegacy(rules);
        parts.State.Update(s =>
        {
            if (s.FirewallPorts is null) return false;
            s.FirewallPorts = null;
            return true;
        });
        parts.Log.Info($"[firewall] remote rule removed, asked by {sid}");
        parts.Events?.Add(sid, "firewall", "closed");
        return Ok();
    }

    /// <summary>Only the rules of the earlier name that are verifiably ours; never by the name alone (Windows' own rules carry it too).</summary>
    private byte[] RemoveLegacyFirewallRules(RequestContext context, CancellationToken cancellation)
    {
        using var gate = Enter(_firewallGate, cancellation);
        if (parts.Firewall.Rules() is not { } rules) return Fail(ErrorCodes.Unavailable);
        return RemoveLegacy(rules) ? Ok() : Fail(ErrorCodes.Failed);
    }

    private bool RemoveLegacy(IReadOnlyList<FirewallRuleInfo> rules)
    {
        var ok = true;
        foreach (var legacy in FirewallRule.OurLegacyRules(rules, ProgramPath, FirewallOps.DefaultPorts))
        {
            if (parts.Firewall.Netsh(FirewallRule.DeleteLegacyArgs(legacy)) != 0) ok = false;
        }
        return ok;
    }

    // ---- migration away from the SYSTEM tasks ------------------------------------------------------

    /// <summary>
    /// Removes the caller's own old SYSTEM tasks (<c>CouchHud\&lt;SID&gt;\Policy-*</c>, <c>Couch-*</c>, <c>PC-*</c>) and its policy files.
    /// <c>Autologon-off</c> stays as the recovery's reserve (its user may still start it: it only switches the automatic sign-in off, see <see cref="LegacyAdoption.IsAutoLogonOff"/>). Lock values
    /// the old setup of this account set are taken over first (<see cref="Adopt"/>); refused while the account has lock values nothing of the
    /// old setup explains. The keys the old setup noted as created by the lock are taken over.
    /// </summary>
    private byte[] MigrateTasks(RequestContext context, CancellationToken cancellation)
    {
        var sid = context.Caller.Sid;
        using var gate = Enter(AccountGate(sid), cancellation);
        if (!parts.State.IsAvailable) return Fail(ErrorCodes.Unavailable);
        using var hive = parts.Hives.OpenLoaded(sid);
        if (hive is null) return Fail(ErrorCodes.Unavailable);
        var (ok, removed) = Migrate(sid, hive.Registry, cancellation);
        if (!ok) return Fail(ErrorCodes.Failed);
        parts.Events?.Add(sid, "migration", $"old tasks removed: {removed}");
        return Ok(new MigrateTasksData(removed));
    }

    /// <summary>
    /// The migration of one account; the caller holds the account's gate and has its hive open. First the lock values of the old setup become
    /// the service's (noted before anything is deleted, so no lock goes away unnoticed), then the old tasks go, then their files.
    /// </summary>
    private (bool Ok, int Removed) Migrate(string sid, IRegistry registry, CancellationToken cancellation)
    {
        if (!Adopt(sid, registry)) return (false, 0);
        var all = parts.Tasks.List();
        var noted = PolicyGroups.ValidCleanupKeys(PolicyInstaller.ReadNotedKeys(parts.Policies.UserPolicyDirectory(sid)));
        var tasks = all.Where(t => IsMigratableTask(t, sid)).ToList();
        var removed = 0;
        foreach (var task in tasks)
        {
            cancellation.ThrowIfCancellationRequested();
            if (parts.Tasks.Delete(task) == 0) removed++;
        }
        if (removed != tasks.Count)
        {
            parts.Log.Error($"[migrate] {tasks.Count - removed} tasks of {sid} could not be removed");
            return (false, removed);
        }
        // The files only after the tasks that import them.
        if (!parts.Policies.RemoveUserFiles(sid)) return (false, removed);
        var saved = parts.State.Update(s =>
        {
            var a = s.Account(sid);
            a.CreatedKeys = PolicyGroups.MergeCleanupKeys(noted, a.CreatedKeys).ToList();
            a.Migrated = true;
            return true;
        });
        if (saved != StateUpdate.Saved) return (false, removed);
        _legacyPending.TryRemove(sid, out _);
        parts.Log.Info($"[migrate] {removed} old tasks of {sid} removed");
        return (true, removed);
    }

    /// <summary>
    /// Takes over the lock values that the old setup of this account set (<see cref="LegacyAdoption.Plan"/>: evidence only from what an
    /// administrator wrote, the account's .reg files in the protected program folder): the groups, the keys the old setup
    /// noted and <see cref="AccountState.LegacyLock"/> go into the state in one write. False when values are there that nothing explains
    /// (they and the old tasks stay) or the state could not be written. The caller holds the account's gate.
    /// </summary>
    private bool Adopt(string sid, IRegistry registry)
    {
        if (parts.State.Snapshot() is not { } state) return false;
        var owned = state.Accounts.TryGetValue(sid, out var account) ? account.Groups : [];
        var directory = parts.Policies.UserPolicyDirectory(sid);
        var plan = LegacyAdoption.Plan(owned, name => File.Exists(Path.Combine(directory, name)), registry);
        if (plan.Foreign.Count > 0)
        {
            parts.Log.Error($"[migrate] tasks of {sid} stay: lock values that neither the service nor the old setup of this account set are present ({string.Join(", ", plan.Foreign)})");
            return false;
        }
        if (plan.Adopt.Count == 0) return true;
        var noted = PolicyGroups.ValidCleanupKeys(PolicyInstaller.ReadNotedKeys(directory));
        var saved = parts.State.Update(s =>
        {
            var a = s.Account(sid);
            foreach (var group in plan.Adopt.Where(g => !a.Groups.Contains(g, StringComparer.Ordinal))) a.Groups.Add(group);
            a.CreatedKeys = PolicyGroups.MergeCleanupKeys(noted, a.CreatedKeys).ToList();
            a.LegacyLock = true;
            return true;
        });
        if (saved != StateUpdate.Saved)
        {
            parts.Log.Error($"[legacy] lock of {sid} not taken over: the state could not be written");
            return false;
        }
        parts.Log.Info($"[legacy] lock of {sid} set by an earlier version taken over: {string.Join(", ", plan.Adopt)}");
        // What the account held before (codes, mode) may be its user's own: forgotten, only an administrator's confirmation sets the code again.
        if (ForgetCodes is { } forget && !forget(sid)) parts.Log.Error($"[legacy] the codes of {sid} could not be forgotten: they count for nothing until an administrator confirms the lock");
        parts.Events?.Add(sid, "migration", $"lock taken over: {string.Join(", ", plan.Adopt)}");
        return true;
    }

    // ---- the old SYSTEM tasks of 0.4.0 (finding B1 of the 0.4.1 acceptance test) ------------------------

    private readonly ConcurrentDictionary<string, byte> _legacyPending = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _sweepGate = new(1, 1);

    private volatile bool _sweptOnce;

    /// <summary>Accounts with old tasks whose hive was not loaded at the last sweep (they wait for their sign-in).</summary>
    public IReadOnlyCollection<string> LegacyPending => _legacyPending.Keys.Order(StringComparer.Ordinal).ToList();

    /// <summary>Whether a sweep ran to its end at least once (until then a sign-in sweeps anyway, review N-4).</summary>
    public bool LegacySwept => _sweptOnce;

    /// <summary>
    /// Only the restriction of <see cref="SweepLegacyTasks"/> (<see cref="LegacyAdoption.RestrictAll"/>): no state, no hive, nothing removed.
    /// The service runs it before its pipe exists, so that no squatted pipe name or early stop leaves an old task startable (review M-1).
    /// </summary>
    public int RestrictLegacyTasks() => LegacyAdoption.RestrictAll(parts.Tasks, parts.Log);

    /// <summary>
    /// At start and after a sign-in, in the background: every task in the CouchHud folder but an account's Autologon-off is set to SYSTEM and administrators only, so no
    /// user can start an old release task any more (the first step, for every account, signed in or not). Then each account with old tasks
    /// whose hive is loaded is migrated (<see cref="Migrate"/>: lock taken over first, then tasks and files removed); an account that is not
    /// signed in keeps its restricted tasks as the evidence for later and waits (its hive is never loaded here: that could block its sign-in).
    /// Its sign-in, its next ApplyLockPolicies or MigrateTasks finish it. Repeating it changes nothing more. False when a sweep runs already
    /// (<paramref name="wait"/>: waits for it to end and sweeps again, bounded by the token).
    /// </summary>
    public bool SweepLegacyTasks(CancellationToken cancellation, bool wait = false)
    {
        if (wait) _sweepGate.Wait(cancellation);
        else if (!_sweepGate.Wait(0, CancellationToken.None)) return false;
        try
        {
            // The restriction first and also without a state: it only ever takes a right away. Each task and each account on its own: one
            // that fails leaves the others to do (review N-3).
            IReadOnlyList<string> tasks;
            try
            {
                tasks = parts.Tasks.List();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                parts.Log.Error($"[legacy] the old tasks could not be listed: {ex.GetType().Name}");
                return true;
            }
            LegacyAdoption.RestrictAll(parts.Tasks, parts.Log, tasks);
            if (!parts.State.IsAvailable) return true;
            foreach (var sid in LegacyAdoption.Accounts(tasks).Where(sid => tasks.Any(t => IsMigratableTask(t, sid))))
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    SweepAccount(sid, cancellation);
                }
                catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
                {
                    _legacyPending.TryAdd(sid, 0);
                    parts.Log.Error($"[legacy] old tasks of {sid}: {ex.GetType().Name}");
                }
            }
            _sweptOnce = true;
            return true;
        }
        finally
        {
            _sweepGate.Release();
        }
    }

    private void SweepAccount(string sid, CancellationToken cancellation)
    {
        using var gate = Enter(AccountGate(sid), cancellation);
        using var hive = parts.Hives.OpenLoaded(sid);
        if (hive is null)
        {
            if (_legacyPending.TryAdd(sid, 0)) parts.Log.Info($"[legacy] {sid} is not signed in: its old tasks stay restricted until its sign-in");
            return;
        }
        var (ok, removed) = Migrate(sid, hive.Registry, cancellation);
        if (ok) parts.Events?.Add(sid, "migration", $"old tasks removed: {removed}");
        else _legacyPending.TryAdd(sid, 0);
    }

    /// <summary>A task of exactly this account's folder that the service replaces (not Autologon-off, nothing of another account, no legacy task).</summary>
    internal static bool IsMigratableTask(string task, string sid)
    {
        var parts = task.Trim('\\').Split('\\');
        return parts.Length == 3
               && parts[0].Equals(PolicyGroups.TaskFolder, StringComparison.OrdinalIgnoreCase)
               && parts[1].Equals(sid, StringComparison.OrdinalIgnoreCase)
               && TaskLeaf.IsMatch(parts[2]);
    }


    // ---- releasing every account ------------------------------------------------------------------

    /// <summary>How long a release of every account may take at most (it runs on past the handler's time limit, see <see cref="ReleaseStatus"/>).</summary>
    public static readonly TimeSpan ReleaseJobLimit = TimeSpan.FromMinutes(10);

    private readonly Lock _jobLock = new();
    private readonly CancellationTokenSource _stopping = new();
    private Task<byte[]>? _job;
    private Operation _jobKind;

    /// <summary>The service stops: a running release job is cancelled.</summary>
    public void Stop() => _stopping.Cancel();

    /// <summary>
    /// ReleaseAllAccounts and ResetServiceState run as one job at a time, apart from the request: the caller waits for it as long as
    /// its request may take; when that time is up (the dispatcher answers "timeout"), the job goes on, bounded by
    /// <see cref="ReleaseJobLimit"/> and the service's stop, and <see cref="ReleaseStatus"/> (in Status) tells when it is done. A second
    /// request of the same kind waits for the running job; one of the other kind is "busy".
    /// </summary>
    private byte[] RunReleaseJob(Operation kind, Func<CancellationToken, byte[]> work, CancellationToken cancellation)
    {
        Task<byte[]> job;
        lock (_jobLock)
        {
            if (_job is { IsCompleted: false })
            {
                if (_jobKind != kind) return Fail(ErrorCodes.Busy);
                job = _job;
            }
            else
            {
                _jobKind = kind;
                _release = "running";
                job = _job = Task.Run(() =>
                {
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                    limit.CancelAfter(ReleaseJobLimit);
                    try
                    {
                        return work(limit.Token);
                    }
                    catch (Exception ex) when (ex is OperationCanceledException or UnauthorizedAccessException or System.Security.SecurityException or IOException)
                    {
                        _release = "failed";
                        parts.Log.Error($"[release-all] {kind} ended early: {ex.GetType().Name}");
                        return Fail(ErrorCodes.Failed);
                    }
                }, CancellationToken.None);
            }
        }
        // The caller's time: when it is up this throws (the dispatcher answers "timeout"), and the job goes on.
        job.Wait(cancellation);
        return job.Result;
    }

    /// <summary>
    /// Uninstall (an elevated administrator): for every account the service holds something for, and every account with a task folder,
    /// the groups the service set are released (hives of accounts that are not signed in are loaded), its keys cleaned up and the old
    /// tasks of the account removed; then every service it paused is started again where it stopped it. Accounts where that did not
    /// work fully are named in <c>Left</c>. May run past the handler's time limit (see <see cref="RunReleaseJob"/>).
    /// </summary>
    private byte[] ReleaseAllAccounts(RequestContext context, CancellationToken cancellation)
    {
        if (parts.State.Current is null) return Fail(ErrorCodes.Unavailable);
        var who = context.Caller.Sid;
        return RunReleaseJob(Operation.ReleaseAllAccounts, token =>
        {
            var released = new List<string>();
            var left = new List<string>();
            foreach (var sid in KnownAccounts())
            {
                token.ThrowIfCancellationRequested();
                using var gate = Enter(AccountGate(sid), token);
                if (ReleaseAccount(sid, token)) released.Add(sid);
                else left.Add(sid);
            }
            using (Enter(_servicesGate, token))
            {
                // Left names accounts only; a service that did not start again is in the log (and stays noted for the next try).
                if (!ResumeAll(token)) parts.Log.Error("[release-all] some paused services did not start again");
            }
            _release = left.Count == 0 ? "done" : "failed";
            parts.Log.Info($"[release-all] released {released.Count}, left {left.Count}, asked by {who}");
            return Ok(new ReleaseAllAccountsData([.. released], [.. left]));
        }, cancellation);
    }

    /// <summary>The accounts the service knows: those in its state and those with a task folder of the old helper tasks.</summary>
    private List<string> KnownAccounts() =>
        (parts.State.Current?.Accounts.Keys ?? Enumerable.Empty<string>())
            .Concat(parts.Tasks.List().Select(TaskAccount).OfType<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(ServiceStateStore.IsUserSid)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// The way out of a damaged state, for an elevated administrator only: first everything that can still be identified is released,
    /// so nothing stays locked for ever, then the service starts with an empty state. The whole job holds every gate (services,
    /// automatic sign-in, firewall and every known account), and the check that nothing is held any more and the swap to the empty
    /// state happen under the state's one lock (<see cref="ServiceStateStore.Reset"/>).
    /// <list type="bullet">
    /// <item>Valid state: like ReleaseAllAccounts; the reset happens only when no account holds a group and no service is paused any
    /// more (otherwise the service would forget values it still holds), else "failed" and the state stays.</item>
    /// <item>Damaged state: ownership is unknown. For the account SIDs that can still be read from the file (validated like a good
    /// state), the fixed lock values are removed (<see cref="PolicyRelease.RemoveOurs"/>, hives loaded when needed); the services named
    /// in it that start with Windows and are stopped are started. The file is then kept as <c>state.corrupt-&lt;time&gt;.json</c>.</item>
    /// </list>
    /// </summary>
    private byte[] ResetServiceState(RequestContext context, CancellationToken cancellation)
    {
        if (parts.State.Current is null && !parts.State.IsCorrupt) return Fail(ErrorCodes.Unavailable);
        var who = context.Caller.Sid;
        return RunReleaseJob(Operation.ResetServiceState, token =>
        {
            var corrupt = parts.State.Current is null;
            (IReadOnlyList<string> salvagedSids, IReadOnlyList<string> salvagedServices) = corrupt ? parts.State.Salvage() : (Array.Empty<string>(), Array.Empty<string>());
            var sids = corrupt ? salvagedSids.Order(StringComparer.Ordinal).ToList() : KnownAccounts();
            var held = new List<Releaser>();
            try
            {
                // Every gate, always in this order: no other handler holds more than one, so nothing can wait in a circle.
                held.Add(Enter(_servicesGate, token));
                held.Add(Enter(_autoLogonGate, token));
                held.Add(Enter(_firewallGate, token));
                foreach (var sid in sids) held.Add(Enter(AccountGate(sid), token));

                var released = new List<string>();
                var left = new List<string>();
                foreach (var sid in sids)
                {
                    token.ThrowIfCancellationRequested();
                    var ok = corrupt ? ReleaseUnknown(sid) : ReleaseAccount(sid, token);
                    (ok ? released : left).Add(sid);
                }
                if (corrupt) StartAgain(salvagedServices, token);
                else if (!ResumeAll(token)) parts.Log.Error("[reset] some paused services did not start again");

                var outcome = parts.State.Reset(parts.Now(), s => s.Accounts.Values.All(a => a.Groups.Count == 0) && s.Paused.Count == 0);
                if (outcome is ResetOutcome.Refused or ResetOutcome.Failed)
                {
                    _release = "failed";
                    parts.Log.Error($"[reset] state kept ({outcome}): {left.Count} accounts could not be released, asked by {who}");
                    return Fail(ErrorCodes.Failed);
                }
                _release = left.Count == 0 ? "done" : "failed";
                parts.Log.Info($"[reset] state reset ({outcome}, released {released.Count}, left {left.Count}), asked by {who}");
                return Ok(new ResetServiceStateData(outcome == ResetOutcome.CorruptKept, [.. released], [.. left]));
            }
            finally
            {
                for (var i = held.Count - 1; i >= 0; i--) held[i].Dispose();
            }
        }, cancellation);
    }

    /// <summary>A damaged state named the account: the fixed lock values go (ownership is unknown, an administrator asked for it).</summary>
    private bool ReleaseUnknown(string sid)
    {
        using var hive = parts.Hives.OpenOrLoad(sid);
        return hive is not null && PolicyRelease.RemoveOurs(hive.Registry).Count == 0;
    }

    private void StartAgain(IEnumerable<string> services, CancellationToken token)
    {
        foreach (var name in services)
        {
            token.ThrowIfCancellationRequested();
            if (!ServicePolicy.IsAllowed(name, parts.ServiceOnList) || parts.Scm.GetStartType(name) != ServiceStartMode.Automatic || parts.Scm.IsRunning(name) != false) continue;
            if (!parts.Scm.Start(name, parts.ServiceTimeout)) parts.Log.Error($"[reset] {name} did not start again");
        }
    }

    /// <summary>Releases one account; the caller holds the account's gate.</summary>
    private bool ReleaseAccount(string sid, CancellationToken cancellation)
    {
        using var hive = parts.Hives.OpenOrLoad(sid);
        if (hive is null)
        {
            parts.Log.Error($"[release-all] hive of {sid} not available");
            return false;
        }
        var registry = hive.Registry;
        // A lock of the old setup is released like one of the service's (taken over first), never left behind as "done".
        if (!Adopt(sid, registry)) return false;
        var owned = parts.State.Current?.Accounts.GetValueOrDefault(sid)?.Groups ?? [];
        var groups = Groups([.. owned]);
        if (groups.Count > 0)
        {
            if (PolicyRelease.RemoveOurs(registry, groups).Count > 0) return false;
            if (parts.State.Update(s =>
                {
                    if (!s.Accounts.TryGetValue(sid, out var a)) return false;
                    a.Groups.Clear();
                    s.Tidy(sid);
                    return true;
                }) is StateUpdate.Unavailable or StateUpdate.WriteFailed) return false;
        }
        var created = parts.State.Current?.Accounts.GetValueOrDefault(sid)?.CreatedKeys.ToList() ?? [];
        if (created.Count > 0) CleanupKeys(sid, registry, created);
        var (ok, _) = Migrate(sid, registry, cancellation);
        return ok;
    }

    private static string? TaskAccount(string task)
    {
        var parts = task.Trim('\\').Split('\\');
        return parts.Length == 3 && parts[0].Equals(PolicyGroups.TaskFolder, StringComparison.OrdinalIgnoreCase) ? parts[1] : null;
    }

    /// <summary>Starts again what the service stopped and forgets every pause; the caller holds the services gate.</summary>
    private bool ResumeAll(CancellationToken cancellation)
    {
        if (parts.State.Current is not { } state) return false;
        var ok = true;
        foreach (var (name, entry) in state.Paused)
        {
            cancellation.ThrowIfCancellationRequested();
            if (entry.StoppedByService && ServicePolicy.IsAllowed(name, parts.ServiceOnList)
                && parts.Scm.GetStartType(name) == ServiceStartMode.Automatic && !parts.Scm.Start(name, parts.ServiceTimeout))
            {
                ok = false;
                continue;
            }
            parts.State.Update(s => s.Paused.Remove(name));
        }
        return ok;
    }

    // ---- sign-out: an account's holds on services end ------------------------------------------------

    /// <summary>
    /// A session logged off (SERVICE_CONTROL_SESSIONCHANGE, WTS_SESSION_LOGOFF): every account that holds a pause but has no session
    /// left (the leaving one not counted) gives its holds back, as if it had resumed; the last holder's release starts the service
    /// again where the service stopped it. When the sessions cannot be read, nothing changes.
    /// </summary>
    public void OnSessionLogoff(int sessionId, CancellationToken cancellation)
    {
        if (parts.Sessions is null) return;
        using var gate = Enter(_servicesGate, cancellation);
        if (parts.State.Current is not { } state || state.Paused.Count == 0) return;
        if (parts.Sessions.SignedInSids(sessionId) is not { } signedIn) return;
        foreach (var (name, entry) in state.Paused)
        {
            cancellation.ThrowIfCancellationRequested();
            var gone = entry.Holders.Where(h => !signedIn.Contains(h)).ToList();
            if (gone.Count == 0) continue;
            var last = gone.Count == entry.Holders.Count;
            if (last && entry.StoppedByService && ServicePolicy.IsAllowed(name, parts.ServiceOnList)
                && parts.Scm.GetStartType(name) == ServiceStartMode.Automatic && !parts.Scm.Start(name, parts.ServiceTimeout))
            {
                parts.Log.Error($"[services] {name} did not start again after the sign-out of its holders");
            }
            parts.State.Update(s =>
            {
                if (!s.Paused.TryGetValue(name, out var e)) return false;
                e.Holders.RemoveAll(gone.Contains);
                if (e.Holders.Count == 0) s.Paused.Remove(name);
                return true;
            });
            parts.Log.Info($"[services] {name}: holds of signed-out accounts dropped ({gone.Count})");
        }
    }


    // ---- helpers ---------------------------------------------------------------------------------

    private SemaphoreSlim AccountGate(string sid) => _accountGates.GetOrAdd(sid, _ => new SemaphoreSlim(1, 1));

    /// <summary>Waits for the gate (or the cancellation) and releases it on dispose.</summary>
    private static Releaser Enter(SemaphoreSlim gate, CancellationToken cancellation)
    {
        gate.Wait(cancellation);
        return new Releaser(gate);
    }

    private readonly struct Releaser(SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }

    private static byte[] Ok() => ProtocolJson.Serialize(Response.Success());

    private static byte[] Ok<T>(T data) => ProtocolJson.Serialize(Response<T>.Success(data));

    private static byte[] Fail(string code) => Dispatcher.Fail(code);
}
