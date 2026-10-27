using System.Diagnostics;
using System.ServiceProcess;
using CouchHud.Core.Guard;
using CouchHud.Core.Startup;
using CouchHud.Core.Localization;

namespace CouchHud.Shell.Services;

public sealed record ServiceInfo(string Name, string DisplayName, string Status, bool Automatic, bool Suggested);

/// <summary>
/// Pauses chosen Windows services on the couch and starts them again in PC mode, through the CouchHUD service: stopping a service
/// needs administrator rights, and the service does it for exactly the names on its own fixed list (it never stops what is not on
/// it, and resumes only what it paused itself, counting over all sessions that want a service paused).
/// Earlier versions used one scheduled task per service and direction; those are removed through the service's task migration.
/// </summary>
public sealed class ServiceSwitch
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Running and automatic services that may be paused, suggestions first.</summary>
    public static IReadOnlyList<ServiceInfo> List()
    {
        var services = ServiceController.GetServices();
        try
        {
            return services
                .Where(s => GamingServices.IsAllowed(s.ServiceName))
                .Select(s => new ServiceInfo(
                    s.ServiceName,
                    GamingServices.Suggested.TryGetValue(s.ServiceName, out var label) ? label : s.DisplayName,
                    s.Status.ToString().ToLowerInvariant(),
                    SafeStartType(s) == ServiceStartMode.Automatic,
                    GamingServices.Suggested.ContainsKey(s.ServiceName)))
                .Where(s => s.Suggested)
                .OrderByDescending(s => s.Suggested)
                .ThenBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        finally
        {
            foreach (var s in services) s.Dispose();
        }
    }

    /// <summary>
    /// Scheduled tasks of earlier versions in the CouchHud folder that this account can see. Tasks the setup or the service restricted to
    /// SYSTEM and the administrators (0.4.1 on) are invisible to a standard account, so this is false for them; they need nothing from the
    /// shell: the service's own sweeps migrate them (at its start, at each logon while a lock of 0.4.0 waits, when it applies the lock policies).
    /// </summary>
    public static bool HasTaskLeftovers()
    {
        try
        {
            return PrivilegedServices.Scheduler.List().Any();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Anything of the old setup is still there: a task in the CouchHud folder this account can see (see <see cref="HasTaskLeftovers"/>) or
    /// the policy files next to the program (readable for every account, so a lock of 0.4.0 is seen even when its tasks are not).
    /// </summary>
    public static bool HasLeftovers() => HasTaskLeftovers() || Directory.Exists(PrivilegedServices.Policies.PolicyDirectory());

    /// <summary>
    /// The install folder only administrators can write to (read from the ACL, see <see cref="InstallProtection"/>): the service is
    /// only installed there, and the repair script is only started from there.
    /// </summary>
    public static bool InstallFolderIsProtected() => InstallProtection.IsProtected();

    /// <summary>
    /// Brings the chosen services into the couch or PC state through the service. In the background; switches queue up so a quick
    /// couch → PC → couch ends in the right state. Without a usable service nothing happens (the services keep running: that only costs
    /// performance, it opens nothing).
    /// </summary>
    public void Apply(string mode, IReadOnlyCollection<string> installed)
    {
        if (installed.Count == 0) return;
        var names = installed.ToList();
        _ = Task.Run(async () =>
        {
            await _gate.WaitAsync();
            try
            {
                var ops = AdminService.Ops;
                var result = mode == StartupMode.Pc ? await ops.ResumeServicesAsync(names) : await ops.PauseServicesAsync(names);
                Console.WriteLine($"[services] {(mode == StartupMode.Pc ? "resume" : "pause")} {string.Join(',', names)}: {result}");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.Error.WriteLine($"[services] {ex.GetType().Name}");
            }
            finally
            {
                _gate.Release();
            }
        });
    }

    private static ServiceStartMode SafeStartType(ServiceController s)
    {
        try
        {
            return s.StartType;
        }
        catch (InvalidOperationException)
        {
            return ServiceStartMode.Manual;
        }
    }
}

public sealed record ProcessInfo(string Name, string Title, int Count, long MemoryBytes, bool HasWindow, bool IsGame, string DisplayName = "");

/// <summary>A small task manager for the TV: what runs, how much memory, end it.</summary>
public static class ProcessList
{
    // Ending these logs the user out, breaks the session or kills CouchHud's own UI.
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm", "fontdrvhost",
        "sihost", "ctfmon", "conhost", "RuntimeBroker", "taskhostw", "dllhost", "audiodg", "WUDFHost", "Memory Compression",
        "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost", "TextInputHost", "SecurityHealthSystray",
        "CouchHud", "msedgewebview2", "schtasks",
        "explorer", "WidgetBoard", "Widgets", "ShellHost", "ApplicationFrameHost", "LockApp", "SystemSettings", "backgroundTaskHost",
        "PickerHost", "WindowsPackageManagerServer", "WidgetService", "CrossDeviceResume", "DefenderSessionHelper"
    };

    /// <summary>
    /// Windows' own helpers (System32, SysWOW64, SystemApps) without a window: nothing a person on the couch
    /// should see or end. Programs of Windows that have a window (Notepad, Calculator) stay in the list.
    /// </summary>
    internal static bool IsHiddenWindowsHelper(Process p, bool hasWindow)
    {
        if (hasWindow) return false;
        try
        {
            var path = p.MainModule?.FileName;
            return path is not null && IsWindowsHelperPath(path);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    internal static bool IsWindowsHelperPath(string path)
    {
        var windows = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) return false;
        var rest = path[windows.Length..];
        return new[] { "SystemApps", "System32", "SysWOW64" }.Any(d => rest.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<ProcessInfo> List(IReadOnlyCollection<int> gamePids)
    {
        var session = Process.GetCurrentProcess().SessionId;
        var all = Process.GetProcesses();
        try
        {
            return all
                .Where(p => SafeSession(p) == session && !Protected.Contains(p.ProcessName))
                .Where(p => gamePids.Contains(p.Id) || !IsHiddenWindowsHelper(p, SafeTitle(p).Length > 0))
                .GroupBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
                .Select(g => new ProcessInfo(
                    g.Key,
                    g.Select(SafeTitle).FirstOrDefault(t => t.Length > 0) ?? "",
                    g.Count(),
                    g.Sum(SafeMemory),
                    g.Any(p => SafeTitle(p).Length > 0),
                    g.Any(p => gamePids.Contains(p.Id)),
                    DisplayNameOf(g.First(), g.Key)))
                .OrderByDescending(p => p.IsGame)
                .ThenByDescending(p => p.HasWindow)
                .ThenByDescending(p => p.MemoryBytes)
                .ToList();
        }
        finally
        {
            foreach (var p in all) p.Dispose();
        }
    }

    /// <summary>
    /// Closes a program politely first (the window gets WM_CLOSE, so Edge and co. save their state and do not
    /// report an unexpected close) and waits up to <paramref name="grace"/>. Programs without a window cannot be
    /// asked, they are ended right away. <paramref name="force"/> skips the asking. Returns how many processes are left
    /// (more than 0: the program hangs or asks a question, the UI offers to force it).
    /// </summary>
    public static async Task<int> QuitAsync(string name, bool force, TimeSpan? grace = null)
    {
        if (Protected.Contains(name)) throw new InvalidOperationException(Strings.Get("services.protected", name));
        if (force)
        {
            Kill(name);
            return 0;
        }
        var session = Process.GetCurrentProcess().SessionId;
        var mine = Process.GetProcessesByName(name).Where(p => SafeSession(p) == session && !IsHiddenWindowsHelper(p, SafeTitle(p).Length > 0)).ToList();
        try
        {
            if (mine.Count == 0) throw new InvalidOperationException(Strings.Get("services.cannotStop", name));
            var asked = 0;
            foreach (var p in mine)
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero && p.CloseMainWindow()) asked++;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    Console.Error.WriteLine($"[tasks] {name}: {ex.Message}");
                }
            }
            if (asked == 0)
            {
                // Nothing with a window to ask (background program): there is nobody to hurt, end it.
                Kill(name);
                return 0;
            }
            var deadline = DateTime.UtcNow + (grace ?? TimeSpan.FromSeconds(6));
            while (DateTime.UtcNow < deadline && mine.Any(StillRunning)) await Task.Delay(250);
            return mine.Count(StillRunning);
        }
        finally
        {
            foreach (var p in mine) p.Dispose();
        }
    }

    private static bool StillRunning(Process p)
    {
        try
        {
            p.Refresh();
            return !p.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Ends every process with that name in this session, the process tree found by start time (see <see cref="SafeTermination"/>),
    /// never a program of Windows, whatever its name says.
    /// </summary>
    public static int Kill(string name)
    {
        if (Protected.Contains(name)) throw new InvalidOperationException(Strings.Get("services.protected", name));
        var ctl = Win32ProcessControl.Instance;
        var live = ctl.List();
        var stamps = SafeTermination.StampsByName(name, live, ctl);
        var killed = Task.Run(() => SafeTermination.QuitAsync(ctl, stamps, true, TimeSpan.Zero)).GetAwaiter().GetResult().Killed;
        if (killed == 0) throw new InvalidOperationException(Strings.Get("services.cannotStop", name));
        return killed;
    }

    /// <summary>What the program calls itself (file description such as "Microsoft Edge"), else the process name.</summary>
    private static string DisplayNameOf(Process p, string fallback)
    {
        try
        {
            var d = p.MainModule?.FileVersionInfo.FileDescription?.Trim();
            if (!string.IsNullOrEmpty(d)) return d;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // Elevated or protected: the process name has to do.
        }
        return fallback;
    }

    /// <summary>Brings the window of a program to the front.</summary>
    public static bool Switch(string name)
    {
        if (Protected.Contains(name)) return false;
        var session = Process.GetCurrentProcess().SessionId;
        var processes = Process.GetProcessesByName(name);
        try
        {
            var pids = processes.Where(p => SafeSession(p) == session).Select(p => p.Id).ToList();
            return Win32ProcessProbe.Activate(pids);
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }

    private static int SafeSession(Process p)
    {
        try { return p.SessionId; } catch (InvalidOperationException) { return -1; }
    }

    private static string SafeTitle(Process p)
    {
        try { return p.MainWindowTitle; } catch (InvalidOperationException) { return ""; }
    }

    private static long SafeMemory(Process p)
    {
        try { return p.WorkingSet64; } catch (InvalidOperationException) { return 0; }
    }
}
