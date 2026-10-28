using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CouchHud.Shell.Services;

/// <summary>
/// The controller as mouse and keyboard for windows that aren't made for the couch
/// (a launcher's login, a setup): left stick moves, right stick scrolls, buttons click.
/// </summary>
public sealed class ControllerMouse : IDisposable
{
    private const double DeadZone = 0.18;
    // Full deflection: this many pixels per second at least, or the area's width in about 1.2 seconds on a big screen.
    private const double MinSpeed = 1750;
    // Wheel units per second at full deflection (120 = one notch).
    private const double ScrollSpeed = 2500;
    private readonly Func<(double X, double Y, double ScrollY)> _sticks;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private volatile bool _active;
    private volatile bool _disposed;
    private volatile bool _paused;
    private volatile Bounds? _bounds;
    private double _x, _y, _carryScroll;
    private int _sentX = int.MinValue, _sentY = int.MinValue;

    /// <summary>An area in screen pixels (right and bottom exclusive).</summary>
    public sealed record Bounds(int Left, int Top, int Right, int Bottom)
    {
        public int Width => Right - Left;

        public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
    }

    public ControllerMouse(Func<(double X, double Y, double ScrollY)> sticks)
    {
        _sticks = sticks;
        // Its own thread with a high-resolution timer: a thread-pool timer of 16 ms really ticks every 15.6 or 31.2 ms, and a cursor
        // that moves a fixed step per tick then stutters. Here every step is scaled by the time that really passed.
        _thread = new Thread(Loop) { IsBackground = true, Name = "Controller mouse", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public bool Active => _active;

    /// <summary>Sticks stay off the cursor (a TV page steers by keys; its buttons are handled by the browser).</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            if (!_disposed) _wake.Set();
        }
    }

    /// <summary>
    /// The cursor stays inside this area (the couch browser's page), so a click always lands on the page and the wheel scrolls the
    /// page, never the HUD around it. Null: the whole screen (a launcher's window).
    /// </summary>
    public Bounds? Area
    {
        get => _bounds;
        set
        {
            _bounds = value;
            if (!_disposed) _wake.Set();
        }
    }

    public void Start()
    {
        if (_disposed) return;
        _active = true;
        _wake.Set();
    }

    private long _lastSteer = long.MinValue / 2;

    /// <summary>The stick moved the cursor or scrolled within <paramref name="span"/>: someone is reading the page with the controller.</summary>
    public bool SteeredWithin(TimeSpan span) => Environment.TickCount64 - Interlocked.Read(ref _lastSteer) < span.TotalMilliseconds;

    public void Stop()
    {
        _active = false;
    }

    private void Loop()
    {
        var timer = CreateWaitableTimerExW(IntPtr.Zero, null, CreateWaitableTimerHighResolution, TimerAllAccess);
        try
        {
            Run(timer);
        }
        finally
        {
            if (timer != IntPtr.Zero) CloseHandle(timer);
        }
    }

    private void Run(IntPtr timer)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var last = clock.Elapsed;
        var wasActive = false;
        while (!_disposed)
        {
            // Stopped, or paused (a TV page steers by keys): sleep until something changes, no timer ticking for nothing.
            if (!_active || _paused)
            {
                wasActive = false;
                _wake.WaitOne();
                continue;
            }
            if (!wasActive)
            {
                wasActive = true;
                last = clock.Elapsed;
                _sentX = _sentY = int.MinValue;
            }
            // About 125 steps a second.
            if (timer != IntPtr.Zero)
            {
                var due = -80_000L; // 8 ms, relative, in 100 ns units
                if (SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false)) WaitForSingleObject(timer, 100);
                else Thread.Sleep(8);
            }
            else
            {
                Thread.Sleep(8);
            }
            var now = clock.Elapsed;
            var seconds = Math.Clamp((now - last).TotalSeconds, 0, 0.05);
            last = now;
            try
            {
                Tick(seconds);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[mouse] {ex.Message}");
            }
        }
    }

    private void Tick(double seconds)
    {
        if (_paused) return;
        var area = _bounds;
        if (!GetCursorPos(out var cursor)) return;
        // The real mouse moved the cursor (or this is the first step): go on from where it is.
        if (_sentX == int.MinValue || Math.Abs(cursor.X - _sentX) > 2 || Math.Abs(cursor.Y - _sentY) > 2)
        {
            _x = cursor.X + 0.5;
            _y = cursor.Y + 0.5;
        }
        var (x, y, scroll) = _sticks();
        var (dx, dy) = (Curve(x), Curve(-y));
        var screen = Screen();
        var speed = Math.Max(MinSpeed, (area?.Width ?? screen.Width) / 1.2);
        _x += dx * speed * seconds;
        _y += dy * speed * seconds;
        // While the stick moves it: inside the area by a pixel, so the click never hits its border (a real mouse stays free).
        if (dx != 0 || dy != 0)
        {
            var limit = area ?? screen;
            _x = Math.Clamp(_x, limit.Left + 1, limit.Right - 2);
            _y = Math.Clamp(_y, limit.Top + 1, limit.Bottom - 2);
        }
        var px = (int)Math.Floor(_x);
        var py = (int)Math.Floor(_y);
        if (px != cursor.X || py != cursor.Y) MoveCursor(px, py);
        if (dx != 0 || dy != 0 || Curve(scroll) != 0) Interlocked.Exchange(ref _lastSteer, Environment.TickCount64);
        _sentX = px;
        _sentY = py;
        _carryScroll += Curve(scroll) * ScrollSpeed * seconds;
        var wheel = (int)_carryScroll;
        _carryScroll -= wheel;
        // The wheel only where the cursor is allowed (the page): a real mouse that moved it off the page does not scroll the HUD or another program.
        if (wheel != 0 && (area is null || area.Contains(px, py))) Send(MouseWheel, 0, 0, wheel);
    }

    /// <summary>Moves the cursor, remembered as CouchHUD's own input (like every SendInput): it never counts as someone at the PC.</summary>
    private static void MoveCursor(int x, int y)
    {
        KeyboardInput.MarkInjected();
        SetCursorPos(x, y);
        KeyboardInput.MarkInjected();
    }

    /// <summary>Where the cursor is, in the same coordinates as <see cref="Area"/> and SetCursorPos (this process's DPI view).</summary>
    public static bool CursorPosition(out int x, out int y)
    {
        var ok = GetCursorPos(out var p);
        (x, y) = (p.X, p.Y);
        return ok;
    }

    private static Bounds Screen()
    {
        var left = GetSystemMetrics(SmXVirtualScreen);
        var top = GetSystemMetrics(SmYVirtualScreen);
        return new Bounds(left, top, left + Math.Max(1, GetSystemMetrics(SmCxVirtualScreen)), top + Math.Max(1, GetSystemMetrics(SmCyVirtualScreen)));
    }

    /// <summary>Puts the cursor in the middle of the area when it is outside (the controller just went to the page). True: it was moved.</summary>
    public bool CenterIfOutside()
    {
        if (_bounds is not { } area || !GetCursorPos(out var cursor) || area.Contains(cursor.X, cursor.Y)) return false;
        MoveCursor((area.Left + area.Right) / 2, (area.Top + area.Bottom) / 2);
        return true;
    }

    private const int SmXVirtualScreen = 76, SmYVirtualScreen = 77, SmCxVirtualScreen = 78, SmCyVirtualScreen = 79;
    private const uint CreateWaitableTimerHighResolution = 0x00000002, TimerAllAccess = 0x1F0003;

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CursorPoint point);

    // SetCursorPos, not an absolute SendInput: it works in the same (DPI-virtualized) coordinates as GetCursorPos and the window's
    // PointToScreen, so the cursor lands on the pixel the area says, on every monitor; and no pointer acceleration applies.
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerExW(IntPtr attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(IntPtr timer, ref long dueTime, int period, IntPtr completion, IntPtr arg, [MarshalAs(UnmanagedType.Bool)] bool resume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    /// <summary>Buttons in mouse mode. Returns true when handled.</summary>
    public bool Button(string button)
    {
        if (!Active) return false;
        switch (button)
        {
            case "a": Click(MouseLeftDown, MouseLeftUp); return true;
            case "b": Click(MouseRightDown, MouseRightUp); return true;
            case "x": OpenTouchKeyboard(); return true;
            case "y": KeyboardInput.Key(0x0D); return true; // Enter
            case "start": KeyboardInput.Key(0x1B); return true; // Escape
            case "lb": KeyboardInput.Key(0x09, shift: true); return true; // Shift+Tab
            case "rb": KeyboardInput.Key(0x09); return true; // Tab
            case "up": KeyboardInput.Key(0x26); return true;
            case "down": KeyboardInput.Key(0x28); return true;
            case "left": KeyboardInput.Key(0x25); return true;
            case "right": KeyboardInput.Key(0x27); return true;
            default: return false;
        }
    }

    // Fast across the screen with the stick pushed, precise near the centre.
    private static double Curve(double v)
    {
        var a = Math.Abs(v);
        if (a < DeadZone) return 0;
        var t = (a - DeadZone) / (1 - DeadZone);
        return Math.Sign(v) * t * t;
    }

    /// <summary>The Windows touch keyboard (osk.exe as fallback) for typing into the window.</summary>
    private static void OpenTouchKeyboard()
    {
        var tabTip = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "microsoft shared", "ink", "TabTip.exe");
        try
        {
            Process.Start(new ProcessStartInfo(File.Exists(tabTip) ? tabTip : "osk.exe") { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Console.Error.WriteLine($"[mouse] keyboard: {ex.Message}");
        }
    }

    public static void LeftClick() => Click(MouseLeftDown, MouseLeftUp);

    private static void Click(uint down, uint up)
    {
        Send(down, 0, 0, 0);
        Send(up, 0, 0, 0);
    }

    private const uint MouseMove = 0x0001, MouseLeftDown = 0x0002, MouseLeftUp = 0x0004, MouseRightDown = 0x0008, MouseRightUp = 0x0010, MouseWheel = 0x0800;

    private static void Send(uint flags, int dx, int dy, int data)
    {
        var input = new KeyboardInput.INPUT { type = 0, u = new KeyboardInput.InputUnion { mi = new KeyboardInput.MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags } } };
        KeyboardInput.SendInput(1, [input], Marshal.SizeOf<KeyboardInput.INPUT>());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _active = false;
        _wake.Set();
        // The loop ends within one step (at most the 100 ms timer wait); only then is the event gone.
        if (Thread.CurrentThread != _thread && _thread.Join(TimeSpan.FromSeconds(1))) _wake.Dispose();
    }
}

/// <summary>Keyboard input into whatever window has the focus (phone typing into a login field).</summary>
public static class KeyboardInput
{
    private const uint KeyUp = 0x0002, Unicode = 0x0004;

    /// <summary>Types text as Unicode key strokes; works for any keyboard layout.</summary>
    public static void Type(string text)
    {
        var inputs = new List<INPUT>();
        foreach (var c in text)
        {
            if (c == '\n')
            {
                inputs.AddRange(KeyStrokes(0x0D, false));
                continue;
            }
            inputs.Add(new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = Unicode } } });
            inputs.Add(new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = Unicode | KeyUp } } });
        }
        if (inputs.Count > 0) SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    public static void Key(ushort vk, bool shift = false)
    {
        var inputs = KeyStrokes(vk, shift);
        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    private static List<INPUT> KeyStrokes(ushort vk, bool shift)
    {
        const ushort VkShift = 0x10;
        var list = new List<INPUT>();
        if (shift) list.Add(new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = VkShift } } });
        list.Add(new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk } } });
        list.Add(new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = KeyUp } } });
        if (shift) list.Add(new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = VkShift, dwFlags = KeyUp } } });
        return list;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    // Sized by the biggest member (MOUSEINPUT) so INPUT has the size SendInput expects.
    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public int mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static extern uint SendInputNative(uint count, INPUT[] inputs, int size);

    private static long _lastInjected = long.MinValue / 2;

    /// <summary>Environment.TickCount64 of CouchHUD's last own key or mouse input (it shows in Windows' "last input" too).</summary>
    public static long LastInjected => Interlocked.Read(ref _lastInjected);

    /// <summary>Call right before input CouchHUD makes up itself (keybd_event and the like).</summary>
    public static void MarkInjected() => Interlocked.Exchange(ref _lastInjected, Environment.TickCount64);

    /// <summary>SendInput, remembered as CouchHUD's own input.</summary>
    public static uint SendInput(uint count, INPUT[] inputs, int size)
    {
        MarkInjected();
        var sent = SendInputNative(count, inputs, size);
        MarkInjected();
        return sent;
    }
}

/// <summary>Starts game clients quietly and gets their windows out of the way.</summary>
public static class LauncherControl
{
    public static bool IsRunning(string process)
    {
        var list = Process.GetProcessesByName(process);
        try
        {
            return list.Length > 0;
        }
        finally
        {
            foreach (var p in list) p.Dispose();
        }
    }

    /// <summary>Asks the main windows of the process to close (like the close button): nothing is ended by force. Returns whether a process was found.</summary>
    public static bool CloseWindows(string process, string exePath)
    {
        // Only this session's processes with this image, never anything protected (see SafeTermination).
        var control = Win32ProcessControl.Instance;
        var stamps = CouchHud.Core.Guard.SafeTermination.StampsByName(process, control.List(), control)
            .Where(s => string.Equals(s.Value.Path, exePath, StringComparison.OrdinalIgnoreCase));
        var any = false;
        foreach (var (pid, stamp) in stamps)
        {
            any = true;
            control.RequestClose(pid, stamp.Start);
        }
        return any;
    }

    public static void Start(string exe, string args, bool minimized)
    {
        Process.Start(new ProcessStartInfo(exe, args)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            WindowStyle = minimized ? ProcessWindowStyle.Minimized : ProcessWindowStyle.Normal
        })?.Dispose();
    }

    /// <summary>Processes whose windows count as the client's own: its image name, plus the web helpers some clients show their window through.</summary>
    public static IReadOnlyList<string> ClientProcesses(string exe)
    {
        var name = Path.GetFileNameWithoutExtension(exe);
        return name.Equals("steam", StringComparison.OrdinalIgnoreCase) ? [name, "steamwebhelper"] : [name];
    }

    /// <summary>
    /// A client that runs already (Steam with -silent, minimized by the guard) ignores a second start: its window is looked for,
    /// restored and put in front. True when a window of these processes was found and asked to come forward.
    /// </summary>
    public static bool BringClientToFront(IEnumerable<string> processes)
    {
        var pids = new HashSet<int>();
        foreach (var name in processes)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    pids.Add(p.Id);
                    Native.NativeMethods.AllowSetForegroundWindow(p.Id);
                }
            }
        }
        return pids.Count > 0 && Win32ProcessProbe.Activate(pids);
    }

    /// <summary>The process that owns the foreground window right now (0: none).</summary>
    public static int ForegroundPid()
    {
        var hwnd = Native.NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return 0;
        Native.NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        return (int)pid;
    }

    /// <summary>Minimizes every visible top-level window of these processes (launchers that ignore "start minimized").</summary>
    public static void MinimizeWindows(IEnumerable<string> processes)
    {
        var ids = new HashSet<uint>();
        foreach (var name in processes)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p) ids.Add((uint)p.Id);
            }
        }
        if (ids.Count == 0) return;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (ids.Contains(pid) && IsWindowVisible(hwnd)) ShowWindow(hwnd, 6); // SW_MINIMIZE
            return true;
        }, IntPtr.Zero);
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);
}
