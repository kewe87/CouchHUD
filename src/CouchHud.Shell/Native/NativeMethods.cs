using System.Runtime.InteropServices;

namespace CouchHud.Shell.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct XInputGamepad
{
    public ushort Buttons;
    public byte LeftTrigger;
    public byte RightTrigger;
    public short ThumbLX;
    public short ThumbLY;
    public short ThumbRX;
    public short ThumbRY;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XInputState
{
    public uint PacketNumber;
    public XInputGamepad Gamepad;
    // XInputGetStateEx writes a few extra bytes on some versions.
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XInputVibration
{
    public ushort LeftMotorSpeed;
    public ushort RightMotorSpeed;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XInputBatteryInformation
{
    public byte BatteryType;
    public byte BatteryLevel;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XInputCapabilities
{
    public byte Type;
    public byte SubType;
    public ushort Flags;
    public XInputGamepad Gamepad;
    public XInputVibration Vibration;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XInputCapabilitiesEx
{
    public XInputCapabilities Capabilities;
    public ushort VendorId;
    public ushort ProductId;
    public ushort RevisionId;
    public uint Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SystemPowerStatus
{
    public byte AcLineStatus;
    public byte BatteryFlag;
    public byte BatteryLifePercent;
    public byte SystemStatusFlag;
    public uint BatteryLifeTime;
    public uint BatteryFullLifeTime;
}

internal static class NativeMethods
{
    /// <summary>Battery and charger state of the PC (no handle, nothing to close).</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    public const uint ErrorSuccess = 0;

    /// <summary>Undocumented variant (ordinal 100) that also reports the Guide button.</summary>
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("xinput1_4.dll", EntryPoint = "#100")]
    public static extern uint XInputGetStateEx(uint userIndex, out XInputState state);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    public static extern uint XInputGetState(uint userIndex, out XInputState state);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("xinput1_4.dll")]
    public static extern uint XInputSetState(uint userIndex, ref XInputVibration vibration);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("xinput1_4.dll")]
    public static extern uint XInputGetBatteryInformation(uint userIndex, byte devType, out XInputBatteryInformation information);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("xinput1_4.dll")]
    public static extern uint XInputGetCapabilities(uint userIndex, uint flags, out XInputCapabilities capabilities);

    /// <summary>Undocumented (ordinal 108): capabilities plus USB vendor/product id. First argument must be 1.</summary>
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("xinput1_4.dll", EntryPoint = "#108")]
    public static extern uint XInputGetCapabilitiesEx(uint reserved, uint userIndex, uint flags, out XInputCapabilitiesEx capabilities);

    public const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageName(IntPtr process, uint flags, char[] exeName, ref uint size);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    public static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [StructLayout(LayoutKind.Sequential)]
    public struct FlashInfo
    {
        public uint Size;
        public IntPtr Hwnd;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    public const uint FlashwStop = 0;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FlashWindowEx(ref FlashInfo info);

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsHungAppWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    public struct NativePoint
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    public const uint MonitorDefaultToPrimary = 1;
    public const uint MonitorDefaultToNearest = 2;
    public const int WmEndSession = 0x0016;
    public const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpShowWindow = 0x0040;
    public static readonly IntPtr HwndTopmost = new(-1), HwndNoTopmost = new(-2);
    public const int WmDisplayChange = 0x007E;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    public static extern int AssocQueryString(uint flags, int str, string assoc, string? extra, System.Text.StringBuilder? output, ref uint outLen);

    public const uint AssocfIsProtocol = 0x1000;
    public const int AssocstrExecutable = 2;

    public const byte VkMenu = 0x12;
    public const uint KeyEventFKeyUp = 0x0002;
    public const int SwRestore = 9;
    public const int SwShowMaximized = 3;
}
