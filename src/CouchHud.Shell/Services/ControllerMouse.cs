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
    private readonly Func<(double X, double Y, double ScrollY)> _sticks;
    private readonly Timer _timer;
    private double _carryX, _carryY, _carryScroll;

    public ControllerMouse(Func<(double X, double Y, double ScrollY)> sticks)
    {
        _sticks = sticks;
        _timer = new Timer(_ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public bool Active { get; private set; }

    /// <summary>Sticks stay off the cursor (a TV page steers by keys; its buttons are handled by the browser).</summary>
    public bool Paused { get; set; }

    public void Start()
    {
        Active = true;
        _timer.Change(0, 16);
    }

    private long _lastSteer = long.MinValue / 2;

    /// <summary>The stick moved the cursor or scrolled within <paramref name="span"/>: someone is reading the page with the controller.</summary>
    public bool SteeredWithin(TimeSpan span) => Environment.TickCount64 - Interlocked.Read(ref _lastSteer) < span.TotalMilliseconds;

    public void Stop()
    {
        Active = false;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
    }

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

    private void Tick()
    {
        if (Paused) return;
        var (x, y, scroll) = _sticks();
        var (dx, dy) = (Curve(x), Curve(-y));
        if (dx != 0 || dy != 0 || Curve(scroll) != 0) Interlocked.Exchange(ref _lastSteer, Environment.TickCount64);
        // Fast across the screen with the stick pushed, precise near the centre.
        _carryX += dx * 28;
        _carryY += dy * 28;
        _carryScroll += Curve(scroll) * 40;
        var mx = (int)_carryX;
        var my = (int)_carryY;
        var wheel = (int)_carryScroll;
        _carryX -= mx;
        _carryY -= my;
        _carryScroll -= wheel;
        if (mx != 0 || my != 0) Send(MouseMove, mx, my, 0);
        if (wheel != 0) Send(MouseWheel, 0, 0, wheel);
    }

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

    public void Dispose() => _timer.Dispose();
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
