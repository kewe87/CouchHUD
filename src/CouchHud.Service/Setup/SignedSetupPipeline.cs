using CouchHud.Privileged;
using CouchHud.Service.Authorization;
using CouchHud.Service.Contracts;
using CouchHud.Service.Dispatch;
using CouchHud.Service.Jobs;
using CouchHud.Service.Logging;

namespace CouchHud.Service.Setup;

/// <summary>
/// Installs a signed CouchHUD setup for a standard user without UAC (design §3 "Setups ohne TOCTOU", P0 spike, E7):
/// <list type="number">
/// <item>Header: signature, signed name, version newer than installed (<see cref="SignedSetupVerifier"/>); one setup at a time machine-wide.</item>
/// <item>"streamReady", then the bytes (<see cref="StreamCodec"/>: exact size, at most 400 MB, time limits) into the protected staging
/// folder under the signed name (<see cref="StagingArea"/>).</item>
/// <item>Sealed: held open for reading only, size, SHA-256, real path and PE checked on that handle; the answer is a job id.</item>
/// <item>Job: the hash once more right before the start, then the setup as SYSTEM with fixed arguments from the held path.</item>
/// </list>
/// Only CouchHUD's own update goes this way: a plugin never gets a privileged power, and the former route for CaseHUD updates is gone.
/// </summary>
public sealed class SignedSetupPipeline(
    SignedSetupVerifier verifier,
    IInstalledVersions installed,
    StagingArea staging,
    ISetupStarter starter,
    JobStore jobs,
    IEventSink log,
    RecentEvents events,
    TimeSpan? setupTimeout = null,
    TimeSpan? streamInactivity = null,
    JobJournal? journal = null,
    KeepSessionPolicy? keepSession = null)
{
    /// <summary>
    /// CouchHUD as SYSTEM (P0 spike): without "!autostart" the task choice stored from the last interactive setup would write the Run
    /// entry into the hive of SYSTEM (HKU\S-1-5-18).
    /// </summary>
    public const string CouchHudArguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /MERGETASKS=!autostart";

    /// <summary>Tells the setup that the service runs it (P8 reads it with {param:SERVICEUPDATE|0}; Inno ignores switches it does not know).</summary>
    public const string ServiceUpdateSwitch = "/SERVICEUPDATE=1";

    /// <summary>
    /// The session of the user who asked for the update: it is not disconnected. P8 passes it to
    /// <c>installer\stop-couchhud.ps1 -KeepSession &lt;id&gt;</c> (the script disconnects every other session; run by the service it runs
    /// in session 0, so "its own" session is not the user's).
    /// <para>
    /// Rule (P8 review M2): the setup ends every CouchHUD process, the lock overlay and the guard included. A session that is kept while
    /// its couch is locked would give a locked child a usable desktop for the length of the update. So the argument is only passed when
    /// <see cref="KeepSessionPolicy"/> says the couch cannot be locked (no code enrolled), or the caller is an elevated administrator
    /// or brings a valid grant. Otherwise it is left out: the session is disconnected like every other one and sits behind the Windows
    /// sign-in until the shell is back. (Left out rather than refused: until P9 the service cannot see the lock state and assumes a
    /// code, so refusing would stop every update by a standard user.)
    /// </para>
    /// </summary>
    public const string KeepSessionArgument = "/KEEPSESSION=";

    /// <summary>Inno's log of the CouchHUD setup, into the service's logs folder (only SYSTEM and Administrators can read it).</summary>
    public const string LogSwitch = "/LOG=";

    /// <summary>The CouchHUD setup's exit code (P8): the files were installed, but the service did not come up again.</summary>
    public const int ExitServiceNotStarted = 20;

    /// <summary>The largest session id that is passed on (P8 reads at most five digits).</summary>
    public const int MaxSessionId = 99999;

    public const string CouchHudJob = "couchHudSetup";

    public static readonly TimeSpan DefaultSetupTimeout = TimeSpan.FromMinutes(20);

    /// <summary>The one setup that is sealed, started and running, machine-wide.</summary>
    private readonly ExclusiveSlot _slot = new();

    /// <summary>Streams that are being received at the same time, machine-wide (each into its own staging folder).</summary>
    public const int MaxConcurrentStreams = 2;

    private readonly CountedSlot _streams = new(MaxConcurrentStreams);

    /// <summary>Failed or broken streams per account (<see cref="RateBucket.SetupFailures"/>: 2 an hour), on top of the 6 setups an hour.</summary>
    private readonly RateLimiter _failures = new(jobs.Clock);

    /// <summary>The slowest sender of a setup that is waited for: 8 MiB/s (a local pipe; a 200 MB setup takes about half a minute at that rate).</summary>
    public const int MinSetupBytesPerSecond = 8 * 1024 * 1024;

    /// <summary>Added to the throughput-based deadline for the start of the stream.</summary>
    public static readonly TimeSpan StreamGrace = TimeSpan.FromSeconds(10);

    /// <summary>The longest pause between two chunks.</summary>
    public static readonly TimeSpan StreamInactivityDefault = TimeSpan.FromSeconds(10);

    public bool Busy => _slot.IsTaken;

    /// <summary>
    /// The fixed command line. <paramref name="keepSession"/>: the session to keep (see <see cref="KeepSessionArgument"/>), a number from
    /// the caller's token (1 to <see cref="MaxSessionId"/>, else null: not started), or null to keep none. The log file is the service's
    /// own path (built from the data folder and a version of digits and dots; a quote in it is refused).
    /// </summary>
    public static string? ArgumentsFor(SetupKind kind, int? keepSession, string? logFile = null)
    {
        if (keepSession is < 1 or > MaxSessionId) return null;
        var arguments = $"{CouchHudArguments} {ServiceUpdateSwitch}";
        if (keepSession is { } session) arguments += $" {KeepSessionArgument}{session.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        if (logFile is not null)
        {
            if (logFile.Contains('"') || !Path.IsPathFullyQualified(logFile)) return null;
            arguments += $" {LogSwitch}\"{logFile}\"";
        }
        return arguments;
    }

    /// <summary>
    /// After a start of the service: the CouchHUD setups an earlier instance started (the setup stopped that instance) become jobs again
    /// under their old id and owner. The outcome is read off what is installed now: the setup's version (or newer), or not.
    /// </summary>
    public int RecoverJobs()
    {
        // Records are only read from the checked data folder (administrators only); a folder that fails the check is not read at all.
        if (journal is null || staging.Prepare() is not null) return 0;
        var restored = 0;
        foreach (var record in journal.TakeAll())
        {
            // Only CouchHUD setups stop the service; anything else in the journal is not ours to answer for.
            if (record.Kind != CouchHudJob) continue;
            if (jobs.Restore(record.Id, record.OwnerSid, record.Kind) is not { } job) continue;
            var target = Core.Plugins.SemVer.TryParse(record.Version, out var v) ? v : default;
            var now = installed.CouchHud();
            if (now is { } current && current >= target)
            {
                job.Complete(jobs.Clock.Now, "installed");
                log.Info($"[setup] CouchHUD {record.Version} of sid={record.OwnerSid}: installed (found after the restart of the service)");
            }
            else
            {
                job.Fail(jobs.Clock.Now, "notInstalled");
                log.Error($"[setup] CouchHUD {record.Version} of sid={record.OwnerSid}: not installed (installed: {now?.ToString() ?? "unknown"}), log {record.LogFile ?? "none"}");
            }
            restored++;
        }
        return restored;
    }

    /// <summary>
    /// For <see cref="StateCheck.SignedSetupPipeline"/>: the cached outcome of the last check of the data folder (no file system work
    /// during the authorization). <see cref="Begin"/> checks the folders in full again.
    /// </summary>
    public bool Available => staging.Ready;

    /// <summary>
    /// The handler of InstallCouchHudUpdate: "streamReady" with a receiver, or a refusal. The stream goes into its own
    /// staging folder first (at most <see cref="MaxConcurrentStreams"/> at a time); the one setup slot is taken only for seal and start.
    /// </summary>
    public byte[] Begin(RequestContext context, SetupKind kind)
    {
        var caller = context.Caller;
        var args = context.Request.GetArgs<SetupStreamArgs>();
        if (!_failures.HasBudget(caller.Sid, RateBucket.SetupFailures)) return Refuse(caller, kind, ErrorCodes.RateLimited, "too many failed setup streams");
        var current = installed.CouchHud();
        var verdict = verifier.Verify(kind, args, current);
        if (!verdict.Ok) return Refuse(caller, kind, verdict.Code, verdict.Reason ?? "refused");
        var plan = verdict.Plan!;

        IDisposable? streamLease = _streams.TryEnter();
        if (streamLease is null) return Refuse(caller, kind, ErrorCodes.Busy, "too many setup streams");
        StagedFile? staged = null;
        try
        {
            try
            {
                staged = staging.Create(plan.FileName);
            }
            catch (IOException)
            {
                return Refuse(caller, kind, ErrorCodes.Unavailable, "staging refused (see the alarm)");
            }
            // Decided now, with the request (and its grant) at hand; only CouchHUD setups disconnect sessions.
            int? keep = kind == SetupKind.CouchHud && (keepSession ?? KeepSessionPolicy.Never).MayKeep(caller, context.Request) ? caller.PipeSessionId : null;
            context.AcceptStream(new Receiver(this, caller, plan, staged, streamLease, keep));
            staged = null;
            streamLease = null;
            return ProtocolJson.Serialize(new Response(Protocol.Version, true, ErrorCodes.StreamReady));
        }
        finally
        {
            staged?.Dispose();
            streamLease?.Dispose();
        }
    }

    private byte[] Refuse(CallerInfo caller, SetupKind kind, string code, string reason)
    {
        log.Error($"[setup] {kind} refused for sid={caller.Sid}: {reason}");
        events.Add(caller.Sid, "setup", $"{kind}: {code}");
        return Dispatcher.Fail(code);
    }

    /// <summary>A stream that broke or carried the wrong bytes costs from the account's small failure budget (2 an hour).</summary>
    private byte[] RefuseStream(CallerInfo caller, SetupKind kind, string code, string reason)
    {
        _failures.Spend(caller.Sid, RateBucket.SetupFailures);
        return Refuse(caller, kind, code, reason);
    }

    /// <summary>The whole stream may take this long: <see cref="MinSetupBytesPerSecond"/> plus a short start (it is a local pipe).</summary>
    public static TimeSpan StreamDeadline(long size) => StreamGrace + TimeSpan.FromSeconds((double)size / MinSetupBytesPerSecond);

    /// <summary>Reads the bytes, takes the setup slot, seals the file and hands it to the job (which then owns file and slot).</summary>
    private sealed class Receiver(SignedSetupPipeline owner, CallerInfo caller, SetupPlan plan, StagedFile staged, IDisposable streamLease, int? keepSession) : StreamReceiver
    {
        private int _handedOver;
        private int _disposed;

        public override async Task<byte[]> ReceiveAsync(Stream pipe, CancellationToken cancellation)
        {
            try
            {
                await StreamCodec.ReadAsync(pipe, staged.Writer, plan.Size, owner.StreamInactivity ?? StreamInactivityDefault, cancellation, StreamDeadline(plan.Size)).ConfigureAwait(false);
            }
            catch (FrameException ex)
            {
                return owner.RefuseStream(caller, plan.Kind, ex.Code, $"stream: {ex.Code}");
            }
            catch (IOException)
            {
                return owner.RefuseStream(caller, plan.Kind, ErrorCodes.Truncated, "stream: the client went away");
            }

            if (owner._slot.TryEnter() is not { } slot) return owner.Refuse(caller, plan.Kind, ErrorCodes.Busy, "another setup runs");
            var handed = false;
            try
            {
                try
                {
                    staged.Seal(plan.Size, plan.Sha256);
                }
                catch (SetupRejectedException ex)
                {
                    return owner.RefuseStream(caller, plan.Kind, ex.Code, $"staged file: {ex.Message}");
                }
                catch (IOException ex)
                {
                    return owner.RefuseStream(caller, plan.Kind, ErrorCodes.VerificationFailed, $"staged file: {ex.GetType().Name}");
                }

                if (owner.Jobs.Create(caller.Sid, CouchHudJob) is not { } job)
                {
                    return owner.Refuse(caller, plan.Kind, ErrorCodes.Busy, "no room for a job");
                }
                Interlocked.Exchange(ref _handedOver, 1);
                handed = true;
                streamLease.Dispose();
                owner.StartJob(job, caller, plan, staged, slot, keepSession);
                return ProtocolJson.Serialize(Response<JobStartedData>.Success(new JobStartedData(job.Id)));
            }
            finally
            {
                if (!handed) slot.Dispose();
            }
        }

        public override void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1 || Volatile.Read(ref _handedOver) == 1) return;
            staged.Dispose();
            streamLease.Dispose();
        }
    }

    private TimeSpan? StreamInactivity => streamInactivity;

    private JobStore Jobs => jobs;

    private void StartJob(Job job, CallerInfo caller, SetupPlan plan, StagedFile staged, IDisposable lease, int? keepSession)
    {
        job.Progress(5, "verified");
        var hold = job.HoldEnd();
        _ = Task.Factory.StartNew(() =>
        {
            try
            {
                RunSetup(job, caller, plan, staged, keepSession);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                log.Error($"[setup] {plan.Kind} {plan.Version} failed: {ex.GetType().Name}");
                job.Fail(jobs.Clock.Now, "error");
            }
            finally
            {
                staged.Dispose();
                lease.Dispose();
                hold.Dispose();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void RunSetup(Job job, CallerInfo caller, SetupPlan plan, StagedFile staged, int? keepSession)
    {
        // TOCTOU: the hash on the held handle (nobody can write or swap the file while it is held) right before the start, and the
        // start from exactly that path.
        try
        {
            staged.Reverify(plan.Sha256);
        }
        catch (SetupRejectedException ex)
        {
            log.Error($"[setup] {plan.Kind} {plan.Version} changed before the start: {ex.Message}");
            job.Fail(jobs.Clock.Now, "verificationFailed");
            return;
        }
        string? logFile = null;
        if (plan.Kind == SetupKind.CouchHud)
        {
            try
            {
                logFile = Path.Combine(staging.EnsureFolder(staging.SetupLogRoot), $"setup-{plan.Version}.log");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                log.Error($"[setup] no log folder: {ex.GetType().Name}");
            }
        }
        if (ArgumentsFor(plan.Kind, keepSession, logFile) is not { } arguments)
        {
            job.Fail(jobs.Clock.Now, "badSession");
            return;
        }

        // Inno's {tmp} follows TEMP/TMP: a fresh folder below the checked staging folder, not SYSTEM's %TEMP% (C:\Windows\Temp on
        // older builds, where users can create files). The environment is built, not inherited (SetupEnvironment).
        string temp;
        try
        {
            temp = staging.CreateTempFolder(staged);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Error($"[alarm] no safe temp folder for the setup: {ex.Message}");
            job.Fail(jobs.Clock.Now, "notStarted");
            return;
        }

        // The setup stops (and disables) this service on its way: the instance it starts afterwards answers for the job. Every end this
        // instance sees removes the record again (the finally below); only a stop of this process leaves it for the next one.
        var journaled = false;
        if (plan.Kind == SetupKind.CouchHud && journal is not null)
        {
            try
            {
                staging.EnsureFolder(journal.Folder);
                journal.Write(new JournaledJob(job.Id, caller.Sid, job.Kind, plan.Version.ToString(), journal.Now, logFile));
                journaled = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or Core.Plugins.CatalogException)
            {
                log.Error($"[setup] job not journaled: {ex.GetType().Name}");
            }
        }
        try
        {
            log.Info($"[setup] {plan.Kind} {plan.Version} sha256={plan.Sha256} authenticode={staged.AuthenticodeSubject()} for sid={caller.Sid} session={caller.PipeSessionId} keep={(keepSession is null ? "no" : "yes")}: starting");
            events.Add(caller.Sid, "setup", $"{plan.Kind} {plan.Version} started");
            using var process = starter.Start(staged.Path, arguments, staged.Folder, SetupEnvironment.Build(temp));
            if (process is null)
            {
                job.Fail(jobs.Clock.Now, "notStarted");
                return;
            }
            var image = process.ImagePath;
            if (image is not null && !string.Equals(Core.Updates.PathSafety.StripPrefix(image), staged.Path, StringComparison.OrdinalIgnoreCase))
            {
                log.Error("[setup] the setup process runs another image, ended");
                EndAndWait(process);
                job.Fail(jobs.Clock.Now, "wrongImage");
                return;
            }
            // A CouchHUD setup stops this service on its way (and starts it again); the job ends with the process then.
            job.Progress(30, "running");
            if (!process.WaitForExit(setupTimeout ?? DefaultSetupTimeout))
            {
                // The slot stays taken until the setup (and what it started) is really gone: no second setup next to a hanging one.
                log.Error($"[setup] {plan.Kind} {plan.Version} did not end in time: ended with its children");
                EndAndWait(process);
                job.Fail(jobs.Clock.Now, "slow");
                return;
            }
            var code = process.ExitCode;
            log.Info($"[setup] {plan.Kind} {plan.Version} ended with {code}");
            events.Add(caller.Sid, "setup", $"{plan.Kind} {plan.Version} exit {code}");
            if (code == 0) job.Complete(jobs.Clock.Now, "installed");
            else if (code == ExitServiceNotStarted && plan.Kind == SetupKind.CouchHud) job.Fail(jobs.Clock.Now, "serviceNotStarted");
            else job.Fail(jobs.Clock.Now, $"exitCode:{code}");
        }
        finally
        {
            if (journaled) journal!.Delete(job.Id);
        }
    }

    private static void EndAndWait(ISetupProcess process)
    {
        process.Kill();
        process.WaitForExit(Timeout.InfiniteTimeSpan);
    }
}
