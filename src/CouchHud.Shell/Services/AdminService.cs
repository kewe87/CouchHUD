using System.Diagnostics;
using CouchHud.Core.Guard;
using CouchHud.Core.Localization;
using CouchHud.Core.Plugins;
using CouchHud.Service.Contracts;
using CouchHud.ServiceClient;
using PipeClient = CouchHud.ServiceClient.ServiceClient;

namespace CouchHud.Shell.Services;

/// <summary>
/// The shell's one way to administrator rights: the CouchHUD service (owner decision E11). No elevated CouchHud.exe, no scheduled
/// task of the shell, no runas in normal operation. A copy that is not installed into a protected folder has no service by design:
/// every privileged operation then answers "not available" (nothing is tried elsewhere).
/// The only exceptions are the two repairs the user starts by hand, each with Windows' own UAC prompt and a native program:
/// <see cref="RepairAsync"/> (the installer's repair script) and <see cref="CleanupStuckPoliciesAsync"/> (reg.exe for a fixed list
/// of values, only for what the service cannot release itself).
/// </summary>
public static class AdminService
{
    private static readonly Lazy<ServiceOps> Lazy = new(Create);
    private static readonly RepairGate Repairs = new(RepairGate.DefaultCooldown);
    private static long _restartExpectedUntil;

    public static ServiceOps Ops => Lazy.Value;

    private static ServiceOps Create()
    {
        var scm = WindowsServiceControl.Default;
        var client = new PipeClient(scm);
        var v = CatalogRules.AppVersion;
        // The installed copy has a service; a dev build or an unpacked copy would never find one and must not wait for it. A service
        // that is registered counts as expected whatever the folder says (review L2): its absence is then a problem, never "by design".
        var expected = FailClosed.ServiceExpected(InstallProtection.IsProtected(), scm.Query());
        var probe = expected ? new ServiceProbe(scm, client, new Version(v.Major, v.Minor, v.Patch)) : null;
        var ops = new ServiceOps(client, probe);
        ops.Alarm += status => Console.Error.WriteLine($"[service] ALARM: {status.Name} (pipe held by another process or answers against the protocol)");
        return ops;
    }

    /// <summary>The state right now from the cache, refreshed in the background when it is old.</summary>
    public static ServiceStatus Current
    {
        get
        {
            _ = RefreshInBackground();
            return Ops.LastStatus;
        }
    }

    private static async Task RefreshInBackground()
    {
        try
        {
            await Ops.StatusAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[service] status: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// The service is about to stop on purpose (a CouchHUD update it runs stops it): for a while that is no reason for alarm or a
    /// Windows lock.
    /// </summary>
    public static void ExpectRestart(TimeSpan duration) =>
        Interlocked.Exchange(ref _restartExpectedUntil, Environment.TickCount64 + (long)duration.TotalMilliseconds);

    public static bool RestartExpected => Environment.TickCount64 < Interlocked.Read(ref _restartExpectedUntil);

    /// <summary>
    /// A look at a healthy service of a newer version than this shell: the update it ran is done, the expected restart is over
    /// (review L7), so an outage from now on counts again.
    /// </summary>
    public static void SawService(ServiceStatus status)
    {
        if (!RestartExpected || status.Health != ServiceHealth.Healthy) return;
        var v = CatalogRules.AppVersion;
        if (ServiceProbe.IsNewer(status.Version, new Version(v.Major, v.Minor, v.Patch))) ExpectRestart(TimeSpan.Zero);
    }

    /// <summary>
    /// A setup is replacing CouchHUD: only the marker in the program folder counts here (only administrators can write it, review M3);
    /// the one in the user's data folder could be planted by any program of the user to hide an outage of the service.
    /// </summary>
    public static bool SetupRunning() =>
        CouchHud.Core.Updates.UpdateMarker.IsActive(AppContext.BaseDirectory, DateTimeOffset.UtcNow, SetupProbe.ProcessAlive);

    /// <summary>
    /// Whether --dev may relax the lock (the fail-closed unlock rule and the service watch): only for a copy without a service, never
    /// for the installed one (review L5).
    /// </summary>
    public static bool DevMayRelax(bool dev) => FailClosed.DevMayRelax(dev, Ops.Expected);

    /// <summary>A failed operation as an exception with the text for the user.</summary>
    public static void ThrowIfFailed(OpResult result)
    {
        if (!result.Ok) throw new InvalidOperationException(Message(result));
    }

    public static string Message(OpResult result) => Strings.Get(result.MessageKey);

    /// <summary>What the UI shows about the service (transparency list, settings).</summary>
    public static object View(ServiceStatus status) => new
    {
        state = status.Name,
        label = Strings.Get(ServiceTexts.StateKey(status.State)),
        version = status.Version,
        expected = Ops.Expected,
        healthy = status.Health == ServiceHealth.Healthy,
        alarm = status.Health == ServiceHealth.Alarm,
        // Something is wrong with a service that should be there: the UI warns (also in simple mode) and offers the repair.
        problem = FailClosed.OfferRepair(status.State),
        repair = FailClosed.OfferRepair(status.State),
        repairScript = File.Exists(Path.Combine(AppContext.BaseDirectory, EmergencyRepair.RepairScriptName))
    };

    // -----------------------------------------------------------------------
    // Grants of a parent or an administrator (P9)

    /// <summary>
    /// Says clearly what an administrator is about to approve, before Windows' UAC prompt appears (the HUD shows it and waits a moment).
    /// Without a HUD (the sign-in's run entry) nothing is shown beyond the prompt itself.
    /// </summary>
    public static Func<IReadOnlyList<Operation>, string?, Task> AnnounceApproval { get; set; } = (_, _) => Task.CompletedTask;

    /// <summary>
    /// "Needs a parent or an administrator", the fallback where the parents' code did not do (no parents' code, locked out, or a planted one),
    /// through UAC only (P9 review M-c): the service registers what is to be approved (<paramref name="ops"/>, each once; none: a confirmation
    /// only) and gives a control code; the HUD says what is approved; <c>sc.exe control CouchHud &lt;code&gt;</c> starts with Windows' own UAC
    /// prompt (consent.exe on the secure desktop), so the administrator's credential never passes through CouchHUD; the service then hands
    /// this account the grant. False when the prompt was declined or nothing came of it.
    /// </summary>
    /// <param name="reason">Why, when the operations alone do not say it ("legacy": the lock of 0.4.0 is confirmed once).</param>
    public static async Task<bool> ObtainAdminGrantAsync(IReadOnlyList<Operation> ops, string? reason = null)
    {
        if (!Ops.Expected) return false;
        var (request, control) = await Ops.RequestAdminApprovalAsync(ops).ConfigureAwait(false);
        // One approval at a time per session: while one is open the service says "busy"; wait for it to end (at most its lifetime).
        for (var waited = 0; request.Failure == FailureKind.Retry && waited < 65; waited += 3)
        {
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            (request, control) = await Ops.RequestAdminApprovalAsync(ops).ConfigureAwait(false);
        }
        if (!request.Ok || control is not { } code) throw new InvalidOperationException(Message(request));
        await AnnounceApproval(ops, reason).ConfigureAwait(false);
        var launch = EmergencyRepair.AdminApproval(Environment.SystemDirectory, WindowsServiceControl.DefaultServiceName, code);
        try
        {
            await RunElevatedAsync(launch, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"[admin] approval not given: {ex.Message}");
            return false;
        }
        // The SCM hands the control to the service on its own thread: a moment until it is noted.
        for (var i = 0; i < 10; i++)
        {
            var (collect, state) = await Ops.CollectAdminApprovalAsync().ConfigureAwait(false);
            if (!collect.Ok) throw new InvalidOperationException(Message(collect));
            if (state == "approved") return true;
            if (state != "pending") return false;
            await Task.Delay(300).ConfigureAwait(false);
        }
        return false;
    }

    /// <summary>Runs an operation; when the service wants a parent's or an administrator's approval it does not have yet, asks an administrator once for it and tries again.</summary>
    public static Task<OpResult> WithGrantAsync(Operation op, Func<Task<OpResult>> operation) => WithGrantAsync([op], operation);

    /// <summary>Like <see cref="WithGrantAsync(Operation, Func{Task{OpResult}})"/>, with one approval for several uses (e.g. release and key cleanup).</summary>
    public static Task<OpResult> WithGrantAsync(IReadOnlyList<Operation> ops, Func<Task<OpResult>> operation) =>
        GrantFlow.WithGrantAsync(operation, () => ObtainAdminGrantAsync(ops));

    /// <summary>
    /// The one-time confirmation of a lock of 0.4.0 (security review H-1): an administrator confirms exactly what <paramref name="reason"/>
    /// names (<see cref="LegacyCodeReason"/>: the code just entered becomes the lock's only code; <see cref="LegacyWindowsReason"/>: the
    /// Windows sign-in is the lock), and the HUD says that and why first.
    /// </summary>
    public static Task<OpResult> WithLegacyApprovalAsync(Operation op, string reason, Func<Task<OpResult>> operation) =>
        GrantFlow.WithGrantAsync(operation, () => ObtainAdminGrantAsync([op], reason));

    public const string LegacyCodeReason = "legacyCode";
    public const string LegacyWindowsReason = "legacyWindows";
    /// <summary>The code just entered does not match the config's code of 0.4.0, and the person chose to have it confirmed as the new one.</summary>
    public const string LegacyNewCodeReason = "legacyNewCode";

    /// <summary>
    /// Removes the scheduled tasks of earlier versions through the service (it deletes them as SYSTEM, after its own proof). Only a fallback
    /// for what the shell can see: tasks the setup or the service already restricted to SYSTEM and the administrators are invisible to a
    /// standard account's <c>schtasks /query</c>, so <see cref="ServiceSwitch.HasTaskLeftovers"/> misses them; those the service's own
    /// sweeps migrate (at its start, at each logon while a lock of 0.4.0 waits, and when it applies the lock policies).
    /// </summary>
    public static async Task MigrateTasksIfAnyAsync()
    {
        if (!Ops.Expected || !ServiceSwitch.HasTaskLeftovers()) return;
        var result = await Ops.MigrateTasksAsync().ConfigureAwait(false);
        Console.WriteLine($"[service] old tasks: {result}");
    }

    // -----------------------------------------------------------------------
    // The two repairs by hand (UAC, native programs)

    /// <summary>
    /// "Repair the service": the installer's repair script elevated (one UAC prompt), then a fresh look. Only when the state calls for it
    /// and not more than once a minute (the bridge call is open while locked), only from the protected program folder.
    /// The elevated cmd.exe gets the environment of the confirming administrator's profile (HKCU\Environment of that account can
    /// override variables such as windir, SystemRoot or PATHEXT): nothing here and nothing in the script depends on it. The script is
    /// written by the setup with absolute paths and no variables, cmd.exe is started by its absolute path with /d (no AutoRun), in
    /// System32 as the working folder.
    /// </summary>
    public static async Task<ServiceStatus> RepairAsync()
    {
        var before = await Ops.StatusAsync(fresh: true).ConfigureAwait(false);
        switch (Repairs.TryBegin(DateTimeOffset.UtcNow, before.State))
        {
            case RepairVerdict.NotNeeded:
                throw new InvalidOperationException(Strings.Get("service.repair.notNeeded"));
            case RepairVerdict.CoolingDown:
                throw new InvalidOperationException(Strings.Get("service.repair.wait"));
        }
        var dir = AppContext.BaseDirectory;
        if (!File.Exists(Path.Combine(dir, EmergencyRepair.RepairScriptName))) throw new InvalidOperationException(Strings.Get("service.repair.missing"));
        // The script runs elevated from here: only from a folder no normal program can change.
        if (!ServiceSwitch.InstallFolderIsProtected()) throw new InvalidOperationException(Strings.Get("services.needProtected"));
        var launch = EmergencyRepair.RepairService(Environment.SystemDirectory, Path.TrimEndingDirectorySeparator(dir));
        var exit = await RunElevatedAsync(launch, TimeSpan.FromMinutes(5)).ConfigureAwait(false);
        Console.WriteLine($"[service] repair script ended with {exit}");
        var status = await Ops.StatusAsync(fresh: true).ConfigureAwait(false);
        if (status.Health != ServiceHealth.Healthy) throw new InvalidOperationException(Strings.Get("service.repair.failed", Strings.Get(ServiceTexts.StateKey(status.State))));
        return status;
    }

    /// <summary>
    /// "Windows restrictions stuck": first the service releases what it set (and cleans its keys); what is still there afterwards
    /// (values the service does not know, or no service) goes with reg.exe for exactly the lock's values in this account's hive (one
    /// UAC prompt). Throws with the reason when values stay.
    /// </summary>
    public static async Task CleanupStuckPoliciesAsync(string dataDir)
    {
        var registry = new WindowsRegistry();
        var status = await Ops.StatusAsync(fresh: true).ConfigureAwait(false);
        if (status.Health == ServiceHealth.Healthy)
        {
            var present = PolicyGroups.All.Where(g => PolicyGroups.AnyPresent(registry, g)).Select(g => g.Id).ToList();
            // Without a grant of this account (another person at the couch, no code entered): one administrator's approval for both steps.
            if (present.Count > 0)
            {
                var release = await WithGrantAsync([Operation.ReleaseLockPolicies, Operation.CleanupPolicyKeys], () => Ops.ReleaseLockPoliciesAsync(present)).ConfigureAwait(false);
                Console.WriteLine($"[lock] stuck policies, release through the service: {release}");
            }
            await WithGrantAsync(Operation.CleanupPolicyKeys, () => Ops.CleanupPolicyKeysAsync()).ConfigureAwait(false);
        }
        // Only groups CouchHUD has evidence for: marked as set, or stuck with a sign that CouchHUD set them (review L6).
        var groups = PolicyHelper.Marked(dataDir).Concat(PolicyHelper.StuckGroups(dataDir, [], snapshotPending: false).Select(g => g.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var values = EmergencyRepair.StuckValues(registry, groups);
        if (values.Count == 0) return;
        // An alarm (someone holds the service's pipe) is exactly when the repair by hand is needed: it does not use the pipe.
        Console.Error.WriteLine($"[lock] {values.Count} values of the lock stay, removing them with reg.exe (administrator confirmation)");
        var launch = EmergencyRepair.StuckPolicyCleanup(Environment.SystemDirectory, PolicyHelper.Sid, values)!;
        await RunElevatedAsync(launch, TimeSpan.FromMinutes(2)).ConfigureAwait(false);
        var left = EmergencyRepair.StuckValues(new WindowsRegistry(), groups);
        if (left.Count > 0) throw new InvalidOperationException(Strings.Get("service.stuck.left", left.Count));
    }

    /// <summary>Starts a native Windows program with UAC and waits; "declined" when the prompt was refused.</summary>
    private static async Task<int> RunElevatedAsync(ElevatedLaunch launch, TimeSpan timeout)
    {
        Process process;
        try
        {
            process = Process.Start(new ProcessStartInfo(launch.FileName, launch.Arguments)
                      {
                          UseShellExecute = true,
                          Verb = "runas",
                          WindowStyle = ProcessWindowStyle.Hidden,
                          // Not the user's current folder (cmd.exe looks there first for what it starts by a bare name).
                          WorkingDirectory = launch.WorkingDirectory
                      })
                      ?? throw new InvalidOperationException(Strings.Get("service.repair.notStarted"));
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException(Strings.Get("admin.declined"));
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException(Strings.Get("service.repair.notStarted") + $" ({ex.NativeErrorCode})");
        }
        using (process)
        {
            using var limit = new CancellationTokenSource(timeout);
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new InvalidOperationException(Strings.Get("service.repair.slow"));
            }
            return process.ExitCode;
        }
    }
}
