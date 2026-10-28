using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CouchHud.Core.Library;
using CouchHud.Sdk;

namespace CouchHud.Shell.Services;

public static class Launcher
{
    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string? arguments, int options, out uint processId);
        [PreserveSig] int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr itemArray, [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);
        [PreserveSig] int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, IntPtr itemArray, out uint processId);
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager;

    /// <summary>Starts a library entry. Store games go through their launcher's URL scheme.</summary>
    /// <returns>Process id of what was started, 0 when shell execute handed over without one.</returns>
    /// <param name="start">What to start when it differs from the scanned start (the user's launch override, see LaunchOverrides.Effective).</param>
    /// <param name="pinned">The content a batch file was approved with (LaunchOverrides.PinFor); compared again on the handle held while it runs.</param>
    public static int Launch(LibraryItem item, LaunchTarget? start = null, FileFingerprint? pinned = null)
    {
        var target = start ?? item.Launch;
        // Every part of the start is its own field (program, arguments, folder): nothing is parsed by a shell.
        var plan = LaunchOptions.Plan(target);
        // A Store (MSIX) app has no exe to run: Windows' activation manager starts it and tells us the process.
        if (plan.PackagedAppId is { } appId && Activate(appId) is { } activated)
        {
            RegisterOwn(activated);
            return activated;
        }
        // Shell execute so executables that need elevation get their UAC prompt. A batch file's cmd.exe starts directly: the locked couch's
        // DisallowRun list refuses cmd.exe through the shell, and its command line is built and checked by LaunchOptions.ScriptPlan. It gets
        // no console window (LaunchOptions.StartInfo): nothing black in the taskbar next to the game.
        var psi = LaunchOptions.StartInfo(plan);
        // The batch file stays open without write, rename or delete for others until cmd.exe ends: the content checked is the content run.
        var hold = plan.Direct ? LaunchOptions.HoldScript(target.Target, pinned) : null;
        Process? started;
        try
        {
            // What CouchHud starts for the user is not a competitor: the foreground guard leaves it alone.
            started = Process.Start(psi);
        }
        catch
        {
            hold?.Dispose();
            throw;
        }
        OwnLaunches.Register(started);
        var pid = started?.Id ?? 0;
        // Let exactly this process take the foreground; never everyone (ASFW_ANY).
        if (pid != 0) Native.NativeMethods.AllowSetForegroundWindow(pid);
        if (hold is not null && started is not null) _ = ReleaseOnExitAsync(started, hold, new ScriptRun(item.Id, item.Name, pid, Win32ProcessControl.StartOf(pid), DateTimeOffset.UtcNow));
        else
        {
            hold?.Dispose();
            started?.Dispose();
        }
        return pid;
    }

    /// <summary>
    /// Raised when the launcher ended a batch start target's hidden cmd.exe (item name, why): it waited on input nobody can see, or its
    /// game session was over.
    /// </summary>
    public static event Action<string, ScriptStep>? ScriptEnded;

    private static readonly ConcurrentDictionary<int, ScriptRun> Scripts = new();

    /// <summary>A game session of this entry ended: scripts started before it may be ended once nothing runs under them (ScriptWatch).</summary>
    public static void SessionEnded(string itemId, DateTimeOffset sessionStarted)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var run in Scripts.Values) run.SessionEnded(itemId, sessionStarted, now);
    }

    /// <summary>A game session of this entry started (again): its scripts are no longer past their game.</summary>
    public static void SessionStarted(string itemId)
    {
        foreach (var run in Scripts.Values) run.SessionStarted(itemId);
    }

    /// <summary>
    /// CouchHUD quits: a watched cmd.exe with no working child right now waits on input nobody will ever see again, and nothing would
    /// watch it any more; exactly that one is ended (pid and start time). One running a game or a tool is left alone.
    /// </summary>
    public static void EndWaitingScripts()
    {
        foreach (var run in Scripts.Values)
        {
            if (WorkingChildren(run, new char[1024]) == 0 && Win32ProcessControl.Instance.Kill(run.Pid, run.Start))
                Console.WriteLine($"[launch] {run.ItemName}: start script ended on quit");
        }
    }

    private static int WorkingChildren(ScriptRun run, char[] buffer) =>
        ScriptWatch.WorkingChildren(run.Pid, run.Start, OwnLaunches.ParentMap(), Win32ProcessControl.StartOf,
            child => Win32ProcessProbe.ImagePathOf(child, buffer), Environment.GetFolderPath(Environment.SpecialFolder.Windows));

    /// <summary>Lets the batch file go once its cmd.exe has ended; ends exactly that cmd.exe when it waits on input nobody can see.</summary>
    private static async Task ReleaseOnExitAsync(Process process, FileStream hold, ScriptRun run)
    {
        // Without its start time the cmd.exe cannot be told apart from a later process with the same id: then it is only waited for.
        var watched = run.Pid > 0 && run.Start > 0 && Scripts.TryAdd(run.Pid, run);
        try
        {
            if (watched) await WatchAsync(process, run);
            await process.WaitForExitAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"[launch] waiting for the batch file's end: {ex.Message}");
        }
        finally
        {
            if (watched) Scripts.TryRemove(run.Pid, out _);
            hold.Dispose();
            process.Dispose();
        }
    }

    private static async Task WatchAsync(Process process, ScriptRun run)
    {
        var buffer = new char[1024];
        while (true)
        {
            using (var tick = new CancellationTokenSource(ScriptWatch.Interval))
            {
                try
                {
                    await process.WaitForExitAsync(tick.Token);
                    return;
                }
                catch (OperationCanceledException)
                {
                }
            }
            var step = run.Observe(DateTimeOffset.UtcNow, WorkingChildren(run, buffer));
            if (step == ScriptStep.Wait) continue;
            // Exactly this cmd.exe: the start time is compared on the handle that ends it. Its children (a game, a tool) are left alone.
            if (Win32ProcessControl.Instance.Kill(run.Pid, run.Start))
            {
                Console.WriteLine($"[launch] {run.ItemName}: start script ended ({step})");
                ScriptEnded?.Invoke(run.ItemName, step);
            }
            return;
        }
    }

    /// <summary>The activation manager's start; null when it refuses (then the shell is asked the old way).</summary>
    private static int? Activate(string appUserModelId)
    {
        IApplicationActivationManager? manager = null;
        try
        {
            manager = (IApplicationActivationManager)new ApplicationActivationManager();
            var hr = manager.ActivateApplication(appUserModelId, null, 0, out var pid);
            if (hr >= 0) return (int)pid;
            Console.Error.WriteLine($"[launch] activation of {appUserModelId} failed: 0x{hr:X8}, trying the shell");
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Console.Error.WriteLine($"[launch] activation of {appUserModelId}: {ex.Message}, trying the shell");
        }
        finally
        {
            if (manager is not null) Marshal.ReleaseComObject(manager);
        }
        return null;
    }

    private static void RegisterOwn(int pid)
    {
        if (pid <= 0) return;
        try
        {
            using var process = Process.GetProcessById(pid);
            OwnLaunches.Register(process);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // Gone already: nothing to register.
        }
        Native.NativeMethods.AllowSetForegroundWindow(pid);
    }
}
