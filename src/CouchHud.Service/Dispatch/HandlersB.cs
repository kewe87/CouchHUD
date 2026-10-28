using CouchHud.Privileged;
using CouchHud.Service.Authorization;
using CouchHud.Service.Contracts;
using CouchHud.Service.Jobs;
using CouchHud.Service.Logging;
using CouchHud.Service.Setup;
using CouchHud.Service.Updates;

namespace CouchHud.Service.Dispatch;

/// <summary>The parts behind the operations of package P6 (Windows Update, signed setups, jobs).</summary>
public sealed record HandlersBParts(WindowsUpdateJobs WindowsUpdate, UpdateSearchCache SearchCache, SignedSetupPipeline Setups, JobStore Jobs);

/// <summary>
/// Package P6 ("Operationen B"): Windows Update search and installation as jobs, the signed-setup pipeline for CouchHUD,
/// GetJob, and the state checks these operations need (<see cref="StateCheck.UpdateIdsFromSearch"/>, <see cref="StateCheck.SignedSetupPipeline"/>,
/// <see cref="StateCheck.JobOwner"/>). The rate limits come from <see cref="RateLimiter.BucketFor"/>
/// (setups 6/h, Windows Update installations 6/h, searches 20/h) and are applied by the dispatcher before a handler runs.
/// </summary>
public static class HandlersB
{
    /// <summary>The real parts: COM Windows Update, the checked data folder below the service (<c>{app}\service\data</c>), setups started as SYSTEM.</summary>
    public static HandlersBParts CreateDefault(IEventSink log, RecentEvents events, IMonotonicClock clock, ICodeState codes, IGrantStore grants, CancellationToken stopping,
        Func<string, LockView>? views = null)
    {
        var jobs = new JobStore(clock);
        var cache = new UpdateSearchCache();
        var windowsUpdate = new WindowsUpdateJobs(new WindowsUpdateSession(), jobs, cache, log, events, stopping);
        var staging = new StagingArea(AppContext.BaseDirectory, new WindowsFolderSecurity(), log);
        var dataFolder = staging.DataRoot;
        var journal = new JobJournal(Path.Combine(dataFolder, JobJournal.FolderName));
        var setups = new SignedSetupPipeline(new SignedSetupVerifier(), InstalledVersions.ForService(), staging, new SystemSetupStarter(), jobs, log, events,
            journal: journal, keepSession: new KeepSessionPolicy(codes, grants, views));
        // A CouchHUD setup stopped the instance that started it: its job is answered by this one.
        try
        {
            var recovered = setups.RecoverJobs();
            if (recovered > 0) log.Info($"[setup] {recovered} setup job(s) of the previous instance recovered");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Error($"[setup] jobs of the previous instance not read: {ex.GetType().Name}");
        }
        return new HandlersBParts(windowsUpdate, cache, setups, jobs);
    }

    public static void Register(Dictionary<Operation, Handler> handlers, StateChecks checks, HandlersBParts parts)
    {
        RegisterHandlers(handlers, parts);
        RegisterChecks(checks, parts);
    }

    public static void RegisterHandlers(Dictionary<Operation, Handler> handlers, HandlersBParts parts)
    {
        handlers[Operation.SearchWindowsUpdates] = (context, _) => ValueTask.FromResult(parts.WindowsUpdate.StartSearch(context.Caller));
        handlers[Operation.InstallWindowsUpdates] = (context, _) =>
            ValueTask.FromResult(parts.WindowsUpdate.StartInstall(context.Caller, context.Request.GetArgs<InstallWindowsUpdatesArgs>().Ids));
        handlers[Operation.InstallCouchHudUpdate] = (context, _) => ValueTask.FromResult(parts.Setups.Begin(context, SetupKind.CouchHud));
        handlers[Operation.GetJob] = (context, _) =>
        {
            // The owner check ran as a state check; asked again here, so a handler never answers for a job that is not the caller's.
            var job = parts.Jobs.FindFor(context.Caller, context.Request.GetArgs<GetJobArgs>().JobId);
            return ValueTask.FromResult(job is null ? Dispatcher.Fail(ErrorCodes.NotFound) : ProtocolJson.Serialize(Response<JobData>.Success(job.Snapshot())));
        };
    }

    public static void RegisterChecks(StateChecks checks, HandlersBParts parts)
    {
        checks.Register(StateCheck.UpdateIdsFromSearch, new StateCheckFunc((_, request) => request.Args switch
        {
            InstallWindowsUpdatesArgs { Ids: null } => AuthorizationDecision.Allowed,
            InstallWindowsUpdatesArgs { Ids: { } ids } when parts.SearchCache.ContainsAll(ids) => AuthorizationDecision.Allowed,
            _ => AuthorizationDecision.Denied("update ids not from the service's last search")
        }));
        checks.Register(StateCheck.SignedSetupPipeline, new StateCheckFunc((_, _) =>
            parts.Setups.Available ? AuthorizationDecision.Allowed : AuthorizationDecision.Denied("setup staging not available")));
        checks.Register(StateCheck.JobOwner, new StateCheckFunc((caller, request) =>
            request.Args is GetJobArgs { JobId: { } id } && parts.Jobs.FindFor(caller, id) is not null
                ? AuthorizationDecision.Allowed
                : AuthorizationDecision.Denied("not the caller's job")));
    }
}
