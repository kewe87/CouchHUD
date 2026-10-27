using CouchHud.Core.Localization;
using CouchHud.Core.Platform;

namespace CouchHud.Core.Integrations.CaseHud;

/// <summary>
/// Finds CaseHUD: the running instance through its api.json (which can be stale after a crash, so the process behind
/// the pid must really run the announced exe) and the installation through its uninstall entry.
/// </summary>
public sealed class CaseHudDiscovery(string apiJsonPath, Func<int, string?> processImage, IRegistry registry, Func<string, bool>? fileExists = null, Func<string, string>? canonicalPath = null)
{
    /// <summary>
    /// When set, the running process must be the installed CaseHUD: its image has to be the exe this returns (null or empty = not
    /// installed = nothing matches). A program that merely writes a convincing api.json is not CaseHUD. Left unset for tests and
    /// for a CaseHUD started by hand from another folder.
    /// </summary>
    public Func<string?>? ExpectedExe { get; set; }

    /// <summary>Remembers per CaseHUD.exe build whether it knows <c>--background</c>; unset = every start probes.</summary>
    public CaseHudCapabilityStore? Capabilities { get; set; }

    public const int StartWaitSeconds = 15;

    /// <summary>
    /// When set, the process named in api.json or instance.pid must run in <see cref="CurrentSession"/> and must have started before the
    /// file was last written (<see cref="StartTolerance"/> for the clock), else the file is stale (PID reused) or meant for another
    /// session. Unknown facts (process gone, no access) count as not matching. Left unset in tests that do not look at it.
    /// </summary>
    public Func<int, CaseHudProcessFacts?>? ProcessFacts { get; set; }

    /// <summary>The session of this CouchHUD (Windows session id).</summary>
    public int CurrentSession { get; set; }

    /// <summary>Last write time (UTC) of a file; replaceable for tests.</summary>
    public Func<string, DateTime> LastWriteUtc { get; set; } = File.GetLastWriteTimeUtc;

    public static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(1);

    /// <summary>How long a started CaseHUD whose instance.pid is there may take to write its api.json before the API counts as off.</summary>
    public static readonly TimeSpan ApiOffGrace = TimeSpan.FromSeconds(4);

    private readonly Func<string, bool> _exists = fileExists ?? File.Exists;

    /// <summary>%APPDATA%\casehud\api.json.</summary>
    public static string DefaultApiJsonPath() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "casehud", "api.json");

    /// <summary>The running CaseHUD, or null (no file, broken file, wrong API version, dead pid, pid runs another program).</summary>
    public CaseHudApiInfo? ReadRunning() => ReadApi().Info;

    /// <summary>
    /// api.json read as untrusted input (<see cref="CaseHudFiles.ParseApiJson(ReadOnlySpan{byte})"/>), then its process checked.
    /// <see cref="CaseHudFileRead.Invalid"/> also covers a file caught half written; the caller may look again a moment later.
    /// </summary>
    public (CaseHudFileRead Read, CaseHudApiInfo? Info) ReadApi()
    {
        var bytes = CaseHudFiles.ReadSmall(apiJsonPath, CaseHudFiles.MaxApiJsonBytes, out var exists);
        if (!exists) return (CaseHudFileRead.Missing, null);
        var info = bytes is null ? null : CaseHudFiles.ParseApiJson(bytes);
        if (info is null) return (CaseHudFileRead.Invalid, null);
        var image = processImage(info.Pid);
        if (image is null) return (CaseHudFileRead.Valid, null);
        try
        {
            if (!SamePath(image, info.Exe)) return (CaseHudFileRead.Valid, null);
            if (ExpectedExe is { } expected && (expected() is not { Length: > 0 } installed || !SamePath(image, installed))) return (CaseHudFileRead.Valid, null);
            return (CaseHudFileRead.Valid, Fresh(info.Pid, apiJsonPath) ? info : null);
        }
        catch (ArgumentException)
        {
            return (CaseHudFileRead.Valid, null);
        }
    }

    /// <summary>%APPDATA%\casehud\instance.pid, next to api.json.</summary>
    public string InstancePidPath => Path.Combine(Path.GetDirectoryName(apiJsonPath) ?? "", CaseHudFiles.PidFileName);

    /// <summary>The folder CaseHUD keeps both files in.</summary>
    public string DataFolder => Path.GetDirectoryName(apiJsonPath) ?? "";

    /// <summary>
    /// The PID of the running CaseHUD from instance.pid (written by builds with the API switch, whether the API is on or not), or null.
    /// PIDs are reused: it only counts when that process's image is the installed CaseHUD.exe (<see cref="ExpectedExe"/>, else the
    /// install entry). Without a known installation nothing counts.
    /// </summary>
    public int? ReadInstance()
    {
        var bytes = CaseHudFiles.ReadSmall(InstancePidPath, CaseHudFiles.MaxPidFileBytes, out _);
        if (bytes is null || CaseHudFiles.ParsePid(bytes) is not { } pid) return null;
        var installed = ExpectedExe is { } expected ? expected() : FindInstall()?.Exe;
        if (string.IsNullOrEmpty(installed)) return null;
        var image = processImage(pid);
        if (image is null) return null;
        try
        {
            return SamePath(image, installed) && Fresh(pid, InstancePidPath) ? pid : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The process runs in this session and started before <paramref name="file"/> was written (see <see cref="ProcessFacts"/>).</summary>
    private bool Fresh(int pid, string file)
    {
        if (ProcessFacts is not { } facts) return true;
        if (facts(pid) is not { } f || f.SessionId != CurrentSession) return false;
        DateTime written;
        try { written = LastWriteUtc(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
        return f.StartUtc <= written + StartTolerance;
    }

    /// <summary>Same file; with <c>canonicalPath</c> (the shell resolves links and 8.3 names through the file handle) not just the same text.</summary>
    private bool SamePath(string a, string b)
    {
        string Normal(string path)
        {
            var full = Path.GetFullPath(path);
            return canonicalPath?.Invoke(full) ?? full;
        }
        return string.Equals(Normal(a), Normal(b), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The installation (uninstall entry with an existing exe), or null.</summary>
    public CaseHudInstall? FindInstall()
    {
        var values = registry.ReadValues(CaseHudInfo.UninstallKey);
        if (!values.TryGetValue("InstallLocation", out var dir) || string.IsNullOrWhiteSpace(dir)) return null;
        string exe;
        try { exe = Path.Combine(dir.Trim().Trim('"'), CaseHudInfo.ExeName); }
        catch (ArgumentException) { return null; }
        if (!_exists(exe)) return null;
        values.TryGetValue("DisplayVersion", out var version);
        return new CaseHudInstall(exe, Path.GetDirectoryName(exe)!, version ?? "");
    }

    /// <summary>
    /// Starts <c>CaseHUD.exe --background</c> (with <paramref name="noTray"/> also <c>--no-tray</c>: there is no taskbar to put an
    /// icon on; a running instance just keeps running) and waits up to 15 s for a valid api.json. <paramref name="start"/> starts
    /// the program and returns its exit code when it ended within about a second, otherwise null.
    /// Capability is found out by trying, not read off the version: a build that ends at once with code 2 on every
    /// <c>--background</c> form does not know the flag, counts as old (remembered for this exe build), is only started with
    /// <c>--minimized</c> and the caller is told to update. A build whose api.json appears is remembered as capable.
    /// </summary>
    public async Task<CaseHudApiInfo> StartAsync(CaseHudInstall install, Func<string, string, int?> start, Func<TimeSpan, CancellationToken, Task>? delay = null, bool noTray = false, CancellationToken ct = default)
    {
        if (ReadRunning() is { } running) return running;
        // Running with the API switched off: starting it again changes nothing, only CaseHUD's own setting does.
        if (ReadInstance() is not null) throw CaseHudState.ApiOff();
        if (Capabilities?.Get(install) == CaseHudCapability.Old) return OldBuild(install, start);
        var attempts = noTray ? new[] { "--background --no-tray", "--background" } : ["--background"];
        var unknownFlag = true;
        foreach (var arguments in attempts)
        {
            var exit = Launch(start, install.Exe, arguments);
            // Ended at once without announcing itself: the next form follows (a build that only lacks --no-tray still gets --background).
            if (exit is not null && ReadRunning() is null)
            {
                unknownFlag &= exit == UnknownFlagExitCode;
                continue;
            }
            unknownFlag = false;
            break;
        }
        if (unknownFlag) return OldBuild(install, start);
        delay ??= Task.Delay;
        var step = TimeSpan.FromMilliseconds(250);
        for (var waited = TimeSpan.Zero; waited < TimeSpan.FromSeconds(StartWaitSeconds); waited += step)
        {
            await delay(step, ct);
            if (ReadRunning() is { } info)
            {
                Capabilities?.Set(install, capable: true);
                return info;
            }
            // Up and running (instance.pid only exists in builds that know --background) but no api.json after a few seconds:
            // the API is off. Saying so now beats a timeout.
            if (waited >= ApiOffGrace && ReadInstance() is not null)
            {
                Capabilities?.Set(install, capable: true);
                throw CaseHudState.ApiOff();
            }
        }
        throw new CaseHudException("start_timeout", Strings.Get("casehud.startTimeout"));
    }

    /// <summary>Exit code of a program that is given a flag it does not know.</summary>
    public const int UnknownFlagExitCode = 2;

    private CaseHudApiInfo OldBuild(CaseHudInstall install, Func<string, string, int?> start)
    {
        Capabilities?.Set(install, capable: false);
        Launch(start, install.Exe, "--minimized");
        throw new CaseHudException("needs_update", Strings.Get("casehud.needsUpdate"));
    }

    /// <summary>One start; a program that cannot be started counts as ended at once (-1).</summary>
    private static int? Launch(Func<string, string, int?> start, string exe, string arguments)
    {
        try
        {
            return start(exe, arguments);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return -1;
        }
    }
}
