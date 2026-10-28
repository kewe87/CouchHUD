using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using CouchHud.Core.Guard;
using CouchHud.Shell.Native;

namespace CouchHud.Shell.Services;

/// <summary>
/// On the couch CouchHud is in charge: when a game client (Steam, Epic …) or a listed nuisance
/// takes the foreground on its own, its window is minimized and the HUD comes back. What is allowed and when is
/// decided by <see cref="ForegroundPolicy"/>; clients the user's own action needs (game start, login) are let through.
/// Needs a message pump: create and Start on the UI thread.
/// </summary>
public sealed class ForegroundGuard : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0x0000;
    private const int SwMinimize = 6;
    private const uint WmClose = 0x0010;

    // Shell and system windows that cover the screen without being a game. Explorer stays on this list:
    // its windows (file manager) are never "the game", and on a locked couch they are minimized (or ended, see EndStrayExplorers).
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "ApplicationFrameHost", "SearchHost", "ShellExperienceHost", "LockApp", "TextInputHost", "StartMenuExperienceHost"
    };

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Func<ForegroundContext> _context;
    private readonly Action _bringHud;
    private readonly Action<string> _log;
    private readonly Action<string, string> _needsAttention;
    private readonly ForegroundState _state;
    // Locked couch: more windows are suppressed, and a window that keeps coming back is fought for longer.
    // Locked, giving up is never for good: after a give-up the HUD is pulled forward over the window that insists, and the guard tries
    // again after 2, 4, 8 … seconds (at most the quiet time), not after a fixed long pause.
    private readonly ForegroundState _lockedState = new() { MaxAttempts = 6, QuietTime = TimeSpan.FromSeconds(16), BackoffStart = TimeSpan.FromSeconds(2) };
    // The HUD is pulled forward over a stubborn locked window at most every 3 s; the note about it is shown at most every 5 minutes.
    private readonly ActionThrottle _lockedFront = new(TimeSpan.FromSeconds(3));
    private readonly ActionThrottle _lockedNote = new(TimeSpan.FromMinutes(5));
    private readonly Func<IReadOnlyCollection<int>> _gamePids;
    // One action per window per second; the HUD is not pulled forward more often than every 700 ms.
    private readonly ActionThrottle _perWindow = new(TimeSpan.FromSeconds(1));
    private readonly ActionThrottle _hudFront = new(TimeSpan.FromMilliseconds(700));
    // Kept in a field: a collected delegate would crash the process on the next event.
    private readonly WinEventProc _callback;
    private IntPtr _hook;
    // A full-screen window of an unlisted process was in front last (a game started through a client, say).
    private int _foreignFullscreenPid;

    // The mouse mode of a launcher that was opened unlocked ends when the lock closes again (see AfterMinimizeAsync).
    private readonly Action? _endMouseMode;

    /// <summary>Programs first-party plugins restrict on the locked couch (lock.restrict), by process id.</summary>
    public Func<int, bool>? PluginRestrictions { get; init; }

    public ForegroundGuard(Func<ForegroundContext> context, ForegroundState state, Action bringHud, Action<string, string> needsAttention, Action<string> log,
        Func<IReadOnlyCollection<int>>? gamePids = null, Action? endMouseMode = null)
    {
        _endMouseMode = endMouseMode;
        _gamePids = gamePids ?? (() => []);
        _context = context;
        _state = state;
        _bringHud = bringHud;
        _needsAttention = needsAttention;
        _log = log;
        _callback = OnEvent;
    }

    public void Start()
    {
        if (_hook != IntPtr.Zero) return;
        _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, _callback, 0, 0, WineventOutOfContext);
        if (_hook == IntPtr.Zero) _log("[foreground] hook failed");
    }

    public void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    public void Dispose() => Stop();

    // Short: the real work happens on the dispatcher, not inside the event.
    private void OnEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero || idObject != 0) return;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, () => Handle(hwnd));
    }

    private void Handle(IntPtr hwnd)
    {
        try
        {
            // The event is old by now: only a window that still is in front counts.
            if (NativeMethods.GetForegroundWindow() != hwnd) return;
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd)) return;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0) return;
            var own = pid == (uint)Environment.ProcessId;
            if (own)
            {
                _foreignFullscreenPid = 0; // back in the HUD
                return;
            }
            var name = ProcessName((int)pid);
            var lockedContext = _context();
            if (lockedContext.Locked)
            {
                HandleLocked(hwnd, (int)pid, name, lockedContext);
                return;
            }
            if (ForegroundPolicy.Classify(name) is null)
            {
                if (name is not null && !NotGames.Contains(name) && IsFullscreen(hwnd)) _foreignFullscreenPid = (int)pid;
                return;
            }

            var context = _context();
            if (!context.GameRunning && ForeignGameAlive()) context = context with { GameRunning = true };
            var title = Title(hwnd);
            var decision = ForegroundPolicy.Decide(context, name, title, () => OwnLaunches.Contains((int)pid));
            if (!decision.Suppress) return;
            if (!_perWindow.TryAcquire(hwnd.ToInt64(), DateTimeOffset.UtcNow)) return;

            var key = name ?? "?";
            switch (_state.Attempt(key, DateTimeOffset.UtcNow))
            {
                case BackoffAction.Quiet:
                    return;
                case BackoffAction.GiveUp:
                    GiveUp(decision.Family, key);
                    return;
            }

            _log($"[foreground] {decision.Action} {name} \"{title}\"");
            if (decision.Action == ForegroundAction.CloseAndMinimize) PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
            ShowWindowAsync(hwnd, SwMinimize);
            _ = AfterMinimizeAsync(hwnd, decision.Family, key);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Whatever went wrong with one window must never take the dispatcher (and the HUD) down.
            _log($"[foreground] {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>The program lies in the WebView2 runtime's folder, or CouchHud itself is its parent (see <see cref="LockedWindow.WebViewGenuine"/>).</summary>
    private static bool IsGenuineWebView(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (LockedForeground.IsWebViewRuntimePath(p.MainModule?.FileName)) return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // The program path is not readable: the parent decides.
        }
        return OwnLaunches.ParentMap().TryGetValue(pid, out var parent) && parent == Environment.ProcessId;
    }

    /// <summary>
    /// The locked couch: <see cref="LockedForeground"/> decides from a positive list. A foreign window is minimized
    /// (and the HUD comes back unless a game runs), a standard file or print dialog is asked to close. Never ends a process.
    /// </summary>
    private void HandleLocked(IntPtr hwnd, int pid, string? name, ForegroundContext context)
    {
        if (string.Equals(name, "explorer", StringComparison.OrdinalIgnoreCase)) EndStrayExplorers(hwnd, context);
        var title = Title(hwnd);
        var windowClass = ClassName(hwnd);
        // Child windows are only listed for dialog boxes, the one class that can be a file dialog.
        var dialog = windowClass == "#32770" && LockedForeground.IsFileOrPrintDialog(windowClass, title, ChildClasses(hwnd));
        var window = new LockedWindow(name, title, pid == Environment.ProcessId, dialog,
            OwnTree: () => OwnLaunches.DescendsFrom(pid, p => p == Environment.ProcessId),
            GameTree: () => InGameTree(pid, context),
            Fullscreen: () => IsFullscreen(hwnd))
        {
            // No name or no start time: gone, protected or elevated. Better to leave it than to fight a window we cannot judge.
            Unreadable = name is null || !OwnLaunches.CanRead(pid),
            WindowClass = windowClass,
            ClientTree = () => InReleasedClientTree(pid, context),
            WebViewGenuine = () => IsGenuineWebView(pid),
            // Only counts while the parents' release is on (ForegroundContext.AppsReleased).
            AppTree = () => OwnLaunches.Contains(pid),
            PluginRestricted = () => PluginRestrictions?.Invoke(pid) == true
        };
        var decision = LockedForeground.Decide(context, window);
        if (!decision.Suppress)
        {
            // Same bookkeeping as for the couch: a full-screen window of an unlisted program may be a game started elsewhere.
            if (decision.Reason == ForegroundReason.LockedFullscreen) _foreignFullscreenPid = pid;
            return;
        }
        if (!_perWindow.TryAcquire(hwnd.ToInt64(), DateTimeOffset.UtcNow)) return;

        var key = name ?? "?";
        switch (_lockedState.Attempt(key, DateTimeOffset.UtcNow))
        {
            case BackoffAction.Quiet:
                // Not fighting for the moment, but not giving the screen away either: the HUD goes in front of the window.
                PullHudForward(context);
                return;
            case BackoffAction.GiveUp:
                GiveUp(decision.Family, name ?? "?", locked: true);
                PullHudForward(context);
                return;
        }
        _log($"[foreground] locked: {decision.Action} {name} \"{title}\" ({decision.Reason})");
        if (decision.Action is ForegroundAction.CloseDialog or ForegroundAction.CloseAndMinimize) PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
        // A dialog gets a moment to close by itself; a window that stays is minimized by the check below.
        if (decision.Action != ForegroundAction.CloseDialog) ShowWindowAsync(hwnd, SwMinimize);
        _ = AfterMinimizeAsync(hwnd, decision.Family, key, _lockedState, minimizeIfStays: decision.Action == ForegroundAction.CloseDialog);
    }

    /// <summary>The window's process is part of the running game (or, in the start phase, was started by CouchHud).</summary>
    private bool InGameTree(int pid, ForegroundContext context)
    {
        var pids = _gamePids();
        if (pids.Count > 0 && OwnLaunches.DescendsFrom(pid, p => pids.Contains(p))) return true;
        return context.GameStarting && OwnLaunches.Contains(pid);
    }

    /// <summary>Start phase: the process descends from a client family that this launch released (a game Steam or Epic starts).</summary>
    private static bool InReleasedClientTree(int pid, ForegroundContext context)
    {
        if (context.Released is not { Count: > 0 } released) return false;
        return OwnLaunches.DescendsFrom(pid, p => ForegroundPolicy.Classify(ProcessName(p)) is { } family && released.Contains(family));
    }

    /// <summary>
    /// CouchHud is the shell, the couch is locked and there is no taskbar: an explorer window is only a file manager
    /// that a guest could reach through a client's "show in folder". Asked to close first, then explorer.exe of this
    /// session is ended (the only process ever ended here), off the UI thread. Only on the focus event, never on a timer.
    /// </summary>
    private void EndStrayExplorers(IntPtr hwnd, ForegroundContext context)
    {
        var hasTray = NativeMethods.FindWindow("Shell_TrayWnd", null) != IntPtr.Zero;
        if (!LockedForeground.ShouldEndExplorers(context.Locked, context.AsShell, hasTray)) return;
        PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500);
                // The tray may have come up in the meantime (PC mode, a restarted desktop): then it is not a stray.
                if (NativeMethods.FindWindow("Shell_TrayWnd", null) != IntPtr.Zero) return;
                var session = Process.GetCurrentProcess().SessionId;
                foreach (var p in Process.GetProcessesByName("explorer"))
                {
                    try
                    {
                        if (p.SessionId != session) continue;
                        _log($"[foreground] locked: ending explorer ({p.Id}), no taskbar");
                        p.Kill();
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
                    {
                        _log($"[foreground] explorer: {ex.Message}");
                    }
                    finally
                    {
                        p.Dispose();
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log($"[foreground] explorer: {ex.Message}");
            }
        });
    }

    /// <summary>Checks that the window really went away (an elevated one ignores us); only then the HUD comes back.</summary>
    private async Task AfterMinimizeAsync(IntPtr hwnd, string? family, string key, ForegroundState? state = null, bool minimizeIfStays = false)
    {
        state ??= _state;
        try
        {
            await Task.Delay(300);
            if (minimizeIfStays && IsWindow(hwnd) && IsWindowVisible(hwnd) && !IsIconic(hwnd))
            {
                ShowWindowAsync(hwnd, SwMinimize);
                await Task.Delay(300);
            }
            if (IsWindow(hwnd) && IsWindowVisible(hwnd) && !IsIconic(hwnd))
            {
                if (state.Failed(key, DateTimeOffset.UtcNow) == BackoffAction.GiveUp) GiveUp(family, key, locked: ReferenceEquals(state, _lockedState));
                if (ReferenceEquals(state, _lockedState)) PullHudForward(_context());
                return;
            }
            // A game may have started in the meantime: never pull the HUD over it.
            var context = _context();
            if (ForegroundPolicy.ShouldEndMouseMode(context) && _endMouseMode is not null)
            {
                // A launcher was opened while unlocked (mouse mode, HUD hidden) and the lock closed again: the foreign window was
                // just minimized, and the HUD would never come back because the mouse mode "owns" the screen. A locked couch has no mouse mode.
                _log("[foreground] locked: ending the mouse mode, the HUD comes back");
                _endMouseMode();
                context = _context();
            }
            if (context.GameRunning || context.MouseMode || context.PcMode || context.Browsing || context.Suspended) return;
            if (_hudFront.TryAcquire(0, DateTimeOffset.UtcNow)) _bringHud();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _log($"[foreground] {ex.Message}");
        }
    }

    private void GiveUp(string? family, string process, bool locked = false)
    {
        _log(locked ? $"[foreground] locked: pausing the fight with {process} (it comes back later, the HUD stays in front)" : $"[foreground] giving up on {process}");
        if (locked && !_lockedNote.TryAcquire(process.GetHashCode(), DateTimeOffset.UtcNow)) return;
        _needsAttention(family ?? "", process);
    }

    /// <summary>Locked and a foreign window insists on the foreground: the HUD goes in front of it (not while a game runs or in PC mode).</summary>
    private void PullHudForward(ForegroundContext context)
    {
        if (context.GameRunning || context.PcMode || context.Browsing || context.Suspended) return;
        if (_lockedFront.TryAcquire(0, DateTimeOffset.UtcNow)) _bringHud();
    }

    private bool ForeignGameAlive()
    {
        if (_foreignFullscreenPid == 0) return false;
        try
        {
            using var p = Process.GetProcessById(_foreignFullscreenPid);
            if (!p.HasExited) return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
        }
        _foreignFullscreenPid = 0;
        return false;
    }

    private static bool IsFullscreen(IntPtr hwnd)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out var r)) return false;
        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info)) return false;
        return r.Left <= info.Monitor.Left && r.Top <= info.Monitor.Top && r.Right >= info.Monitor.Right && r.Bottom >= info.Monitor.Bottom;
    }

    private static string? ProcessName(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    private static List<string> ChildClasses(IntPtr hwnd)
    {
        var found = new List<string>();
        EnumChildWindows(hwnd, (child, _) =>
        {
            found.Add(ClassName(child));
            return found.Count < 200;
        }, IntPtr.Zero);
        return found;
    }

    private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, EnumChildProc callback, IntPtr lParam);

    private static string Title(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return GetWindowText(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint pid, uint thread, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}

/// <summary>
/// Programs CouchHud started for the user (Apps tab, own entries): they, and whatever they start,
/// may take the foreground even if their name is on the guard's list. Identified by PID and start time
/// so a reused PID is never mistaken for them.
/// </summary>
public static class OwnLaunches
{
    private static readonly Dictionary<int, DateTime> Started = [];

    public static void Register(Process? process)
    {
        if (process is null) return;
        try
        {
            var time = process.StartTime.ToUniversalTime();
            lock (Started)
            {
                Started[process.Id] = time;
                if (Started.Count > 64) Prune();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already gone or not accessible: nothing to track.
        }
    }

    /// <summary>The process, or one of its ancestors, was started by CouchHud.</summary>
    public static bool Contains(int pid)
    {
        lock (Started)
        {
            if (Started.Count == 0) return false;
        }
        var parents = ParentMap();
        DateTime? childStart = StartOf(pid);
        for (var depth = 0; depth < 12 && pid > 0 && childStart is { } cs; depth++)
        {
            lock (Started)
            {
                if (Started.TryGetValue(pid, out var registered) && Math.Abs((registered - cs).TotalSeconds) < 2) return true;
            }
            if (!parents.TryGetValue(pid, out var parent)) break;
            // A parent that started after its child is a reused PID, not the parent.
            if (StartOf(parent) is not { } ps || ps > cs) break;
            pid = parent;
            childStart = ps;
        }
        return false;
    }

    /// <summary>The process, or one of its ancestors, satisfies the test (a game's helper, a WebView2 process of CouchHud).</summary>
    public static bool DescendsFrom(int pid, Func<int, bool> isAncestor)
    {
        var parents = ParentMap();
        return ProcessTree.DescendsFrom(pid, isAncestor, p => parents.TryGetValue(p, out var parent) ? parent : null, StartOf);
    }

    /// <summary>Windows tells us when the process started: it exists and is not protected from us.</summary>
    public static bool CanRead(int pid) => StartOf(pid) is not null;

    private static void Prune()
    {
        foreach (var pid in Started.Keys.ToList())
        {
            if (StartOf(pid) is not { } t || Math.Abs((t - Started[pid]).TotalSeconds) >= 2) Started.Remove(pid);
        }
    }

    private static DateTime? StartOf(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.StartTime.ToUniversalTime();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    public static Dictionary<int, int> ParentMap()
    {
        var map = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(0x2, 0); // TH32CS_SNAPPROCESS
        if (snapshot == new IntPtr(-1)) return map;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (var ok = Process32First(snapshot, ref entry); ok; ok = Process32Next(snapshot, ref entry))
            {
                map[(int)entry.ProcessId] = (int)entry.ParentProcessId;
            }
        }
        finally
        {
            NativeCloseHandle(snapshot);
        }
        return map;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", EntryPoint = "CloseHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeCloseHandle(IntPtr handle);
}
