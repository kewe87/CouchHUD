using CouchHud.Core.Plugins;
using CouchHud.Core.Security;
using CouchHud.Core.Updates;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;

namespace CouchHud.Shell.Services;

/// <summary>
/// The machine-wide WebView2 runtime folder for CoreWebView2Environment.CreateAsync (HUD and couch browser), see
/// <see cref="WebViewRuntime"/>. Null means the loader's own choice; then the runtime actually used is checked and, when it lies outside
/// Program Files, <see cref="Unprotected"/> makes the lock settings warn. An unprotected runtime alone never stops the start (on a locked
/// couch that would only end in the desktop fallback). Only when a protected runtime is there and will not start while a lock is on,
/// <see cref="StartAsync"/> fails closed instead of falling back to the loader's choice.
/// </summary>
public static class WebViewRuntimeLocator
{
    /// <summary>The HUD or the couch browser runs on a runtime outside Program Files, or one that could not be checked: the lock settings warn.</summary>
    public static bool Unprotected { get; private set; }

    public static string? MachineFolder(string logTag)
    {
        var choice = WebViewRuntime.Choose(Registrations(), TrustedCliRunner.TrustedRoots(), File.Exists, TrustedCliRunner.CanonicalPath, HasReparsePoint);
        if (choice.Folder is { } folder) Console.WriteLine($"[{logTag}] {choice.Reason}: {folder}");
        else Console.Error.WriteLine($"[{logTag}] {choice.Reason}, the WebView2 loader chooses the runtime");
        return choice.Folder;
    }

    /// <summary>
    /// Starts a web view (Create + Ensure) following <see cref="RuntimeStartPolicy"/>: the machine-wide runtime first, once more with a
    /// fresh choice when the browser does not start (an update race), a profile last used by a newer runtime put aside once (renamed,
    /// never deleted); then, with a lock or parental controls on (<paramref name="strict"/>) and a protected runtime there, fail closed
    /// (the caller's desktop fallback locks Windows), otherwise the loader's choice once, with a warning. Each attempt gets a fresh
    /// control from <paramref name="freshControl"/> (a web view whose start failed is not used again); the one that started is returned.
    /// </summary>
    public static async Task<WebView2> StartAsync(Func<WebView2> freshControl, string userDataFolder, Func<CoreWebView2EnvironmentOptions> options,
        bool strict, string logTag)
    {
        var state = new RuntimeStartState(strict, MachineFolder(logTag), 0, false, false, false);
        Exception? last = null;
        // The environment of the last failed attempt (its browser process may still hold the profile) and a control made in advance.
        CoreWebView2Environment? failedEnv = null;
        WebView2? next = null;
        while (true)
        {
            var action = RuntimeStartPolicy.Next(state);
            switch (action)
            {
                case RuntimeStartAction.Fail:
                    throw last ?? new InvalidOperationException("WebView2 did not start");
                case RuntimeStartAction.FailClosed:
                    Console.Error.WriteLine($"[{logTag}] the machine-wide runtime does not start and a lock or parental controls are on: not falling back to another runtime (fail closed)");
                    throw new InvalidOperationException("WebView2 did not start on the machine-wide runtime (fail closed)", last);
                case RuntimeStartAction.SetAsideProfile:
                    // The failed control goes first (freshControl disposes it and puts an unstarted one in its place), then its browser
                    // process gets a moment to end: a profile still open in it cannot be renamed.
                    var exited = BrowserExited(failedEnv);
                    next = freshControl();
                    await WaitBounded(exited, ProcessExitWait, logTag);
                    SetAsideProfile(userDataFolder, logTag);
                    state = state with { ProfileSetAside = true, ProfileNewer = false, MachineFolder = MachineFolder(logTag) };
                    continue;
            }
            var folder = action == RuntimeStartAction.Machine ? state.MachineFolder : null;
            if (action == RuntimeStartAction.Loader && state.MachineFailures > 0)
                Console.Error.WriteLine($"[{logTag}] the machine-wide runtime does not start, no lock is on: trying the loader's choice once");
            var control = next ?? freshControl();
            next = null;
            failedEnv = null;
            CoreWebView2Environment? env = null;
            try
            {
                env = await CoreWebView2Environment.CreateAsync(folder, userDataFolder, options());
                await control.EnsureCoreWebView2Async(env);
                CheckRunning(control.CoreWebView2, logTag);
                return control;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                last = ex;
                failedEnv = env;
                Console.Error.WriteLine($"[{logTag}] web view did not start on {folder ?? "the loader's runtime"} ({ex.GetType().Name}: {ex.Message})");
                if (action == RuntimeStartAction.Loader)
                {
                    state = state with { LoaderTried = true };
                    continue;
                }
                var fresh = MachineFolder(logTag);
                state = state with
                {
                    MachineFailures = state.MachineFailures + 1,
                    MachineFolder = fresh,
                    ProfileNewer = RuntimeStartPolicy.ProfileNewer(LastVersion(userDataFolder), fresh)
                };
            }
        }
    }

    /// <summary>How long a failed start's browser process may take to end before the profile is renamed anyway (bounded).</summary>
    private static readonly TimeSpan ProcessExitWait = TimeSpan.FromSeconds(3);

    /// <summary>Completes when the environment's browser process has ended; null environment (none was created): already done.</summary>
    private static Task BrowserExited(CoreWebView2Environment? env)
    {
        if (env is null) return Task.CompletedTask;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            env.BrowserProcessExited += (_, _) => done.TrySetResult();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or NotImplementedException)
        {
            return Task.CompletedTask;
        }
        return done.Task;
    }

    private static async Task WaitBounded(Task task, TimeSpan limit, string logTag)
    {
        if (task.IsCompleted) return;
        if (await Task.WhenAny(task, Task.Delay(limit)) != task)
            Console.Error.WriteLine($"[{logTag}] browser process of the failed start still running after {limit.TotalSeconds:0} s, putting the profile aside anyway");
    }

    /// <summary>The runtime version that used the profile last (<c>EBWebView\Last Version</c>), or null.</summary>
    private static string? LastVersion(string userDataFolder)
    {
        try
        {
            var file = new FileInfo(Path.Combine(userDataFolder, "EBWebView", "Last Version"));
            return file.Exists && file.Length < 64 ? File.ReadAllText(file.FullName) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Renames the profile (never deletes it: the page's local storage and the user's sign-ins stay recoverable next to it). Only when the
    /// start failed and the profile's "Last Version" is newer than the machine-wide runtime, i.e. the cause is clear.
    /// </summary>
    private static void SetAsideProfile(string userDataFolder, string logTag)
    {
        try
        {
            if (!Directory.Exists(userDataFolder)) return;
            if (!MakeRoomForSetAside(userDataFolder, logTag))
            {
                Console.Error.WriteLine($"[{logTag}] profile from a newer WebView2 runtime not put aside: older ones could not be removed");
                return;
            }
            var target = RuntimeStartPolicy.SetAsideName(userDataFolder, DateTime.Now);
            Directory.Move(userDataFolder, target);
            Console.Error.WriteLine($"[{logTag}] profile from a newer WebView2 runtime put aside: {target}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[{logTag}] profile from a newer WebView2 runtime could not be put aside: {ex.Message}");
        }
    }

    /// <summary>
    /// Removes the oldest profiles put aside earlier so that at most <see cref="RuntimeStartPolicy.SetAsideKept"/> remain with the new one
    /// (each start with a newer profile would otherwise leave another copy). Our own folders in our own profile; one that is a junction
    /// or symbolic link, or lies on such a path, is never deleted, only logged. False: still too many, so nothing is put aside.
    /// </summary>
    private static bool MakeRoomForSetAside(string userDataFolder, string logTag)
    {
        var trimmed = userDataFolder.TrimEnd('\\', '/');
        var parent = Path.GetDirectoryName(trimmed);
        if (parent is null) return false;
        var siblings = Directory.GetDirectories(parent, RuntimeStartPolicy.SetAsidePattern(trimmed));
        var blocked = false;
        foreach (var old in RuntimeStartPolicy.SetAsideToRemove(trimmed, siblings))
        {
            try
            {
                if (File.GetAttributes(old).HasFlag(FileAttributes.ReparsePoint) || HasReparsePoint(old))
                {
                    Console.Error.WriteLine($"[{logTag}] old profile put aside is a link or lies behind one, not deleted: {old}");
                    blocked = true;
                    continue;
                }
                Directory.Delete(old, recursive: true);
                Console.WriteLine($"[{logTag}] oldest profile put aside removed: {old}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[{logTag}] old profile put aside could not be removed: {old} ({ex.Message})");
                blocked = true;
            }
        }
        return !blocked;
    }

    // The worst result per web view ("web" for the HUD, "browser" for the couch browser): one protected view must not hide another.
    private static readonly Dictionary<string, RuntimeCheck> Checks = [];

    private enum RuntimeCheck
    {
        Protected,
        Unprotected,
        Unchecked
    }

    /// <summary>Some web view could not be checked (path unknown): the warning says "cannot be checked".</summary>
    public static bool Unchecked
    {
        get { lock (Checks) return Checks.Values.Any(c => c == RuntimeCheck.Unchecked); }
    }

    /// <summary>
    /// Checks the runtime a started web view really uses (its browser process). Outside Program Files, or not to be told: <see cref="Unprotected"/>.
    /// </summary>
    public static void CheckRunning(CoreWebView2 core, string logTag)
    {
        RuntimeCheck result;
        try
        {
            using var browser = System.Diagnostics.Process.GetProcessById((int)core.BrowserProcessId);
            var exe = SetupProbe.ImageOf(browser);
            if (exe is null)
            {
                Console.Error.WriteLine($"[{logTag}] runtime cannot be checked (path unknown)");
                result = RuntimeCheck.Unchecked;
            }
            else if (BrowserArguments.InProtectedFolder(exe, TrustedCliRunner.TrustedRoots()))
            {
                Console.WriteLine($"[{logTag}] runtime {exe}");
                result = RuntimeCheck.Protected;
            }
            else
            {
                Console.Error.WriteLine($"[{logTag}] runtime outside Program Files (per-user install?), run the setup as administrator again: {exe}");
                result = RuntimeCheck.Unprotected;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[{logTag}] runtime cannot be checked: {ex.Message}");
            result = RuntimeCheck.Unchecked;
        }
        lock (Checks)
        {
            Checks[logTag] = result;
            Unprotected = Checks.Values.Any(c => c != RuntimeCheck.Protected);
        }
    }

    private static List<RuntimeRegistration> Registrations()
    {
        var found = new List<RuntimeRegistration>();
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = hklm.OpenSubKey(WebViewRuntime.ClientKey);
                if (key is null) continue;
                found.Add(new RuntimeRegistration(key.GetValue("location") as string, key.GetValue("pv") as string));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                Console.Error.WriteLine($"[web] runtime registration unreadable: {ex.Message}");
            }
        }
        return found;
    }

    private static bool HasReparsePoint(string path)
    {
        try
        {
            PathSafety.RequireNoReparsePoints(path);
            return false;
        }
        catch (CatalogException)
        {
            return true;
        }
    }
}
