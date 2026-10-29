using System.Diagnostics;
using System.Reflection;
using CouchHud.Core.Security;
using CouchHud.Core.Startup;
using CouchHud.Privileged;
using CouchHud.Service.Authorization;
using CouchHud.Service.Contracts;
using CouchHud.Service.Dispatch;
using CouchHud.Service.Logging;
using CouchHud.Service.Pipe;
using CouchHud.Service.State;

namespace CouchHud.Service;

/// <summary>
/// The parts of the service wired together, and its life: start the pipe server on a task, stop it within a bound. Separate from
/// <see cref="CouchHudWindowsService"/> so the wiring does not depend on the SCM.
/// </summary>
public sealed class ServiceRuntime
{
    private readonly IEventSink _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly long _startedAt = Stopwatch.GetTimestamp();
    private Task? _server;
    private OperationsA? _operations;
    private GrantStore? _grants;
    private SessionEvents? _sessions;
    private AdminApprovals? _approvals;
    private CodeHandlers? _codeHandlers;

    public ServiceRuntime(IEventSink log) => _log = log;

    /// <summary>Called when the pipe server ends on its own (squatted name, broken pipe): the service must stop with this service-specific exit code.</summary>
    public event Action<int>? Failed;

    public bool UnsafeInstall { get; private set; }

    public void Start()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        _log.Info($"[start] CouchHud.Service {version}");

        // The service only acts when nobody but administrators can replace its files, the DLLs next to it included (design §1).
        UnsafeInstall = !InstallProtection.IsProtected(Environment.ProcessPath, _log, allFiles: true);
        if (UnsafeInstall) _log.Error("[start] the install location is not protected: only Hello, Status and Ping are answered");

        // The SCM gives a service the machine environment only; a variable here was set by an administrator. Still worth a line.
        var variables = Environment.GetEnvironmentVariables().Keys.OfType<string>();
        if (ElevationEnvironment.Offending(variables) is { } variable) _log.Error($"[start] runtime environment variable set: {variable}");

        var clock = StopwatchClock.Instance;
        static long WallMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var events = new RecentEvents(clock, WallMs);
        var denyLog = new DenyLog(_log, clock);
        var state = new ServiceState(version, () => UnsafeInstall, () => (long)Stopwatch.GetElapsedTime(_startedAt).TotalSeconds);
        // The service's own fixed list (not the user's config); the denylist wins either way.
        Func<string, bool> serviceOnList = GamingServices.IsAllowed;
        // The data folder ({app}\service\data, SYSTEM and Administrators only) holds the state and the codes; refused, both are unavailable.
        var folder = OperationsAWiring.EnsureDataFolder(_log);
        var operationsParts = OperationsAWiring.Create(_log, events, serviceOnList, folder);
        // P9: the service keeps the codes and hands out the grants itself (owner decision E1).
        var codes = CodeStore.Open(folder, _log, clock, OperationsAWiring.BootTimeMs);
        var sessionUsers = new WindowsSessions(operationsParts.Accounts);
        // A grant is refused while its session is locked right now (review L2), asked of Windows at every use.
        var grants = _grants = new GrantStore(clock, sessionLocked: sessionUsers.IsLocked);
        _sessions = new SessionEvents(clock, grants, sessionUsers.UserOf, _log);
        var blank = new BlankPasswordCheck(operationsParts.Accounts, operationsParts.Logon);
        var approvals = _approvals = new AdminApprovals(clock);
        // Only administrators may send the control that approves (review 3, item 4): checked on the real access list, fail-closed.
        var dacl = ServiceDacl.Read(ServiceHardening.ServiceName);
        var untrusted = ServiceDacl.UntrustedControllers(dacl);
        if (untrusted is not { Count: 0 })
        {
            approvals.ControlsTrusted = false;
            _log.Error(untrusted is null
                ? "[alarm] the service's access list could not be read: administrator approvals through UAC are ignored"
                : $"[alarm] the service's access list lets {string.Join(", ", untrusted)} send controls: administrator approvals through UAC are ignored until it is repaired");
        }
        var handlers = BasicHandlers.Create(state, events, WallMs);
        var checks = StateChecks.BuiltIn(serviceOnList, codes);
        HandlersB.Register(handlers, checks, HandlersB.CreateDefault(_log, events, clock, codes, grants, _stop.Token, codes.View));
        var operations = _operations = OperationsA.Register(handlers, checks, operationsParts, state);
        // Taking over a lock of 0.4.0 forgets the account's codes and mode: its user may have planted them before (security review H-1).
        operations.ForgetCodes = sid => codes.Replace(sid, null) == CodeSet.Saved;
        // A lock taken over from 0.4.0 (or about to be) takes an administrator's approval until one confirmed the account's code or mode
        // (finding B1, security review H-1).
        var codeHandlers = _codeHandlers = CodeHandlers.Register(handlers, checks, new CodeParts(codes, grants, _sessions, approvals, blank.IsBlank, _log, events,
            operations.IsLegacyLocked, operations.EndLegacyLock));
        var dispatcher = new Dispatcher(new DispatcherParts(
            new RequestValidator(name => ServicePolicy.IsAllowed(name, serviceOnList)),
            handlers,
            checks,
            new RateLimiter(clock),
            grants,
            codes,
            new RunningWork(ServiceHardening.MaxConnectionsPerAccount, ServiceHardening.MaxConnections),
            denyLog,
            events,
            () => UnsafeInstall,
            TimeSpan.FromSeconds(30),
            codeHandlers.View));
        // Status tells the caller its own codes and lock mode (settings page, setup assistant).
        state.CodesLookup = sid => codes.IsAvailable
            ? (codes.Has(sid, CodeKind.Couch), codes.Has(sid, CodeKind.Parent), codes.StoredMode(sid))
            : (null, null, null);
        state.LegacyLookup = operations.IsLegacyLocked;
        state.CodesDamagedLookup = () => codes.IsDamaged;
        // The old SYSTEM tasks of 0.4.0 restricted to administrators before the pipe exists (review M-1): a squatted pipe name stops the
        // service, but not before this. Bounded so the SCM's start does not time out; what is not done by then goes on without the stop's token.
        RestrictLegacyTasks(operations);
        var connections = new ConnectionHandler(dispatcher, new ConnectionQuota(ServiceHardening.MaxConnectionsPerAccount), denyLog, ConnectionLimits.Default);
        var factory = new PipeFactory(Protocol.PipeName, ServiceHardening.PipeSddl, ServiceHardening.MaxPipeInstances);
        var server = new PipeServer(factory, new PipeCallerIdentifier(denyLog), connections, _log, ServiceHardening.MaxConnections);
        _server = Task.Run(async () =>
        {
            try
            {
                await server.RunAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (PipeSquattedException ex)
            {
                _log.Error($"[stop] pipe squatting suspected: {ex.Message}; another process holds the service's pipe name, refusing to run (exit 0x{ServiceHardening.ExitPipeSquatted:X})");
                Failed?.Invoke(ServiceHardening.ExitPipeSquatted);
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log.Error($"[stop] the pipe server failed: {ex.GetType().Name} {ex.Message}");
                Failed?.Invoke(ServiceHardening.ExitPipeFailed);
                return;
            }
            if (!_stop.IsCancellationRequested) Failed?.Invoke(ServiceHardening.ExitPipeFailed);
        });
        // The old SYSTEM tasks of 0.4.0: no user may start them any more, signed-in accounts are migrated (finding B1).
        SweepLegacy(all: true);
    }

    /// <summary>How long the start waits for the restriction of the old tasks at most.</summary>
    private static readonly TimeSpan RestrictWait = TimeSpan.FromSeconds(15);

    private void RestrictLegacyTasks(OperationsA operations)
    {
        var restrict = Task.Run(() =>
        {
            try
            {
                operations.RestrictLegacyTasks();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log.Error($"[legacy] restriction of the old tasks: {ex.GetType().Name}");
            }
        });
        if (!restrict.Wait(RestrictWait)) _log.Error("[legacy] the restriction of the old tasks takes long: it goes on while the service starts");
    }

    /// <summary>The delays after a sign-in before the sweep looks again (the hive of the new session is loaded by then, usually at the first).</summary>
    private static readonly TimeSpan[] LegacyRetries = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];

    /// <summary>
    /// The sweep of the old SYSTEM tasks (<see cref="OperationsA.SweepLegacyTasks"/>) off the SCM's thread, bounded by the service's stop:
    /// at start once; after a sign-in while accounts wait for theirs or the start's sweep has not ended yet (review N-4), a few times, each
    /// one after a sweep that still runs.
    /// </summary>
    private void SweepLegacy(bool all)
    {
        if (_operations is not { } operations) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if (all)
                {
                    operations.SweepLegacyTasks(_stop.Token);
                    return;
                }
                foreach (var delay in LegacyRetries)
                {
                    if (operations.LegacySwept && operations.LegacyPending.Count == 0) return;
                    await Task.Delay(delay, _stop.Token).ConfigureAwait(false);
                    operations.SweepLegacyTasks(_stop.Token, wait: true);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log.Error($"[legacy] sweep of the old tasks: {ex.GetType().Name}");
            }
        });
    }

    /// <summary>
    /// SERVICE_CONTROL_SESSIONCHANGE (from the SCM's thread, so only in-memory work here): sign-ins and unlocks are noted for the Windows
    /// grants, a lock, a disconnect or a sign-out ends every grant of the session at once.
    /// </summary>
    public void SessionChanged(SessionChange change, int sessionId)
    {
        try
        {
            _sessions?.OnChange(change, sessionId);
            if (change is SessionChange.Lock or SessionChange.Logoff or SessionChange.Disconnect) _approvals?.RevokeSession(sessionId);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Error($"[grant] session change {change} of session {sessionId}: {ex.GetType().Name}");
        }
        if (change == SessionChange.Logoff) SessionLoggedOff(sessionId);
        if (change == SessionChange.Logon) SweepLegacy(all: false);
    }

    /// <summary>
    /// A user-defined control (128 to 255) from the SCM: only administrators may send one (the service's access list), here through the
    /// UAC-elevated <c>sc.exe control</c> of an administrator's approval (P9 review M-c). From the SCM's thread: in memory only.
    /// </summary>
    public void CustomCommand(int command)
    {
        try
        {
            _codeHandlers?.OnControl(command);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.Error($"[grant] control {command}: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// A session logged off: the holds of accounts without a session left end (off the SCM's thread; a short pause first, so the
    /// leaving session is gone from the list, and bounded by the service's stop).
    /// </summary>
    public void SessionLoggedOff(int sessionId)
    {
        if (_operations is not { } operations) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), _stop.Token).ConfigureAwait(false);
                operations.OnSessionLogoff(sessionId, _stop.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log.Error($"[services] sign-out of session {sessionId}: {ex.GetType().Name}");
            }
        });
    }

    /// <summary>Stops accepting, lets running requests end (bounded), returns.</summary>
    public void Stop(TimeSpan timeout)
    {
        _stop.Cancel();
        _operations?.Stop();
        _grants?.Clear();
        try
        {
            _server?.Wait(timeout);
        }
        catch (AggregateException)
        {
        }
        _log.Info("[stop] CouchHud.Service stopped");
    }
}
