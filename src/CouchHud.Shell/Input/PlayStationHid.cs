using System.Runtime.InteropServices;
using CouchHud.Core.Input;
using Microsoft.Win32.SafeHandles;

namespace CouchHud.Shell.Input;

/// <summary>
/// Reads DualShock 4 and DualSense pads (USB and Bluetooth) natively over HID, without Steam Input or DS4Windows. Read-only and
/// shared: every pad is opened with GENERIC_READ only and FILE_SHARE_READ | FILE_SHARE_WRITE, so Steam, DS4Windows and games keep
/// reading (and writing to) the same pad; nothing is ever written to it, no feature report is sent or requested, no driver
/// is needed and no administrator rights. A pad another program opened exclusively is simply not seen.
/// <para>
/// Why plain HID reads and not Raw Input or Windows.Gaming.Input: Raw Input needs a message window with RIDEV_INPUTSINK and
/// would deliver every HID report of the system to the UI thread; RawGameController maps the buttons by index per driver,
/// gives no battery for these pads and is focus-bound in some setups. A ReadFile per pad on its own thread works with a game
/// in front, which is what the PS button as Guide needs.
/// </para>
/// One thread looks for pads every two seconds; each pad has a reader thread that keeps its latest state.
/// The poller takes the states with <see cref="Snapshot"/>. Report bytes stay in native memory of the reader and are never
/// logged or kept: only buttons, sticks, triggers and the battery level leave the parser.
/// </summary>
internal sealed class PlayStationHid : IDisposable
{
    public const int MaxPads = 4;
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(2);

    private readonly Pad?[] _slots = new Pad?[MaxPads];
    private readonly object _lock = new();
    private readonly HashSet<string> _refused = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _notPads = new(StringComparer.OrdinalIgnoreCase); // scan thread only
    // A pad whose reads keep failing while Windows still lists it: reopened after 2 s, 4 s, 8 s … at most 60 s (under _lock).
    private readonly Dictionary<string, (int Failures, long RetryAt)> _backoff = new(StringComparer.OrdinalIgnoreCase);
    private const int BackoffMaxMs = 60_000;
    private Thread? _scanner;
    private volatile bool _running;

    /// <summary>A pad that sent no report for this long has its buttons and sticks released (a held stick must not repeat on).</summary>
    public const int StaleMs = 500;

    private sealed class Pad(string path, PlayStationModel model, bool bluetooth, bool dongle, SafeFileHandle handle, int reportLength)
    {
        public readonly string Path = path;
        public readonly PlayStationModel Model = model;
        public readonly bool Bluetooth = bluetooth;
        public readonly bool Dongle = dongle;
        public readonly SafeFileHandle Handle = handle;
        public readonly int ReportLength = reportLength;
        public PadSample? Sample;
        public string Battery = "unknown";
        // Only after a report with a pad in it: the DS4 wireless adapter without a pad is no controller.
        public bool Present;
        public long LastReport;
        public volatile bool Gone;
        // Whether this open delivered any report with a pad in it (then a failure afterwards starts the backoff over).
        public bool Delivered;
    }

    /// <summary>A Sony pad is listed but waits for its reopen: its mirror may press while CouchHUD cannot see the pad.</summary>
    public bool Unsettled
    {
        get
        {
            lock (_lock) return _backoff.Keys.Any(path => !_slots.Any(p => p is { Gone: false } && string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)));
        }
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _scanner = new Thread(ScanLoop) { IsBackground = true, Name = "PS-HID scan" };
        _scanner.Start();
    }

    public void Dispose()
    {
        _running = false;
        _scanner?.Join(500);
    }

    /// <summary>
    /// The latest state of each slot and its battery level: (null, null) without a pad, a null sample while the pad sent nothing
    /// for <see cref="StaleMs"/> (everything released, the pad still counts as connected).
    /// </summary>
    public (PadSample? Sample, string? Battery)[] Snapshot()
    {
        var result = new (PadSample?, string?)[MaxPads];
        var now = Environment.TickCount64;
        lock (_lock)
        {
            for (var i = 0; i < MaxPads; i++)
            {
                if (_slots[i] is { Gone: false, Present: true } pad)
                    result[i] = (now - pad.LastReport <= StaleMs ? pad.Sample : null, pad.Battery);
            }
        }
        return result;
    }

    /// <summary>The model and transport of a slot for the log ("DualSense, Bluetooth").</summary>
    public string Describe(int slot)
    {
        lock (_lock)
        {
            return _slots[slot] is { } p ? $"{p.Model}, {(p.Bluetooth ? "Bluetooth" : "USB")}" : "none";
        }
    }

    private void ScanLoop()
    {
        while (_running)
        {
            try
            {
                Scan();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ps-hid] scan: {ex.Message}");
            }
            for (var waited = TimeSpan.Zero; _running && waited < ScanInterval; waited += TimeSpan.FromMilliseconds(250))
                Thread.Sleep(250);
        }
    }

    private void Scan()
    {
        HashSet<string> open;
        var paths = Hid.DevicePaths().ToList();
        var now = Environment.TickCount64;
        lock (_lock)
        {
            for (var i = 0; i < MaxPads; i++)
            {
                if (_slots[i] is { Gone: true }) _slots[i] = null;
            }
            open = _slots.Where(p => p is not null).Select(p => p!.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // A device that went away starts fresh when it comes back.
            var listed = paths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var gone in _backoff.Keys.Where(p => !listed.Contains(p)).ToList()) _backoff.Remove(gone);
        }
        foreach (var path in paths)
        {
            // Keyboard, mouse, touchpad and every other HID device are never opened, not even for a probe: only paths that name
            // Sony's vendor id, and a Sony device that is no known pad is remembered and not probed again.
            if (open.Contains(path) || !PlayStationReport.IsSonyPath(path) || _notPads.Contains(path)) continue;
            lock (_lock)
            {
                if (_backoff.TryGetValue(path, out var wait) && now < wait.RetryAt) continue;
            }
            int free;
            lock (_lock) free = Array.IndexOf(_slots, null);
            if (free < 0) return;
            if (TryOpen(path) is not { } pad) continue;
            lock (_lock) _slots[free] = pad;
            Console.Error.WriteLine($"[ps-hid] {pad.Model} connected over {(pad.Bluetooth ? "Bluetooth" : "USB")}");
            new Thread(() => ReadLoop(pad)) { IsBackground = true, Name = "PS-HID read", Priority = ThreadPriority.AboveNormal }.Start();
        }
    }

    private Pad? TryOpen(string path)
    {
        // First without any access right: enough for the vendor and product id, opens even a pad held exclusively elsewhere.
        PlayStationModel model;
        bool dongle;
        using (var probe = Hid.CreateFileW(path, 0, Hid.FileShareRead | Hid.FileShareWrite, IntPtr.Zero, Hid.OpenExisting, 0, IntPtr.Zero))
        {
            if (probe.IsInvalid) return null;
            var attributes = new Hid.Attributes { Size = Marshal.SizeOf<Hid.Attributes>() };
            if (!Hid.HidD_GetAttributes(probe, ref attributes)) return null;
            model = PlayStationReport.Model(attributes.VendorId, attributes.ProductId);
            dongle = attributes.ProductId == PlayStationReport.DualShock4DongleProductId;
        }
        if (model == PlayStationModel.None)
        {
            if (_notPads.Count < 256) _notPads.Add(path);
            return null;
        }

        var handle = Hid.CreateFileW(path, Hid.GenericRead, Hid.FileShareRead | Hid.FileShareWrite, IntPtr.Zero, Hid.OpenExisting, Hid.FileFlagOverlapped, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            // Opened exclusively by another program (DS4Windows' exclusive mode): leave it to that one, say so once.
            if (_refused.Add(path)) Console.Error.WriteLine($"[ps-hid] {model} not readable (error {Marshal.GetLastPInvokeError()}), used by another program");
            handle.Dispose();
            return null;
        }
        var length = Hid.InputReportLength(handle);
        if (length < 10 || length > PlayStationReport.MaxReportLength)
        {
            handle.Dispose();
            return null;
        }
        _refused.Remove(path);
        return new Pad(path, model, PlayStationReport.IsBluetooth(path, length), dongle, handle, length);
    }

    private unsafe void ReadLoop(Pad pad)
    {
        // One fixed buffer per pad (its declared report length, at most MaxReportLength) and one OVERLAPPED, both freed on the way out.
        var buffer = (byte*)NativeMemory.Alloc((nuint)pad.ReportLength);
        var overlapped = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
        using var done = new ManualResetEvent(false);
        var reason = "";
        // A read was handed to the driver and has not been collected: buffer, OVERLAPPED and event are the driver's until it ended
        // (audit 0.5 A1). Every way out cancels it and waits for it first, see finally.
        var pending = false;
        try
        {
            while (_running)
            {
                done.Reset();
                *overlapped = default;
                overlapped->EventHandle = done.SafeWaitHandle.DangerousGetHandle();
                if (!Hid.ReadFile(pad.Handle, buffer, (uint)pad.ReportLength, IntPtr.Zero, overlapped)
                    && Marshal.GetLastPInvokeError() is var readError && readError != Hid.ErrorIoPending)
                {
                    reason = $"error {readError}";
                    break;
                }
                pending = true;
                uint read;
                var completed = false;
                while (true)
                {
                    if (Hid.GetOverlappedResultEx(pad.Handle, overlapped, out read, 250, false))
                    {
                        pending = false;
                        completed = true;
                        break;
                    }
                    if (Marshal.GetLastPInvokeError() is var waitError && waitError != Hid.WaitTimeout)
                    {
                        reason = $"error {waitError}";
                        break;
                    }
                    if (_running) continue;
                    // Shutting down: the read is cancelled and waited for in finally, before the buffer is freed.
                    break;
                }
                if (!completed) break;
                var report = new ReadOnlySpan<byte>(buffer, (int)Math.Min(read, (uint)pad.ReportLength));
                if (PlayStationReport.IsEmpty(pad.Dongle, report))
                {
                    // The wireless adapter without its pad (or a pad that went away behind it): no controller here.
                    lock (_lock)
                    {
                        pad.Present = false;
                        pad.Sample = null;
                        pad.Battery = "unknown";
                    }
                    continue;
                }
                var state = PlayStationReport.Parse(pad.Model, pad.Bluetooth, report, pad.Dongle);
                if (state is not { } s) continue;
                lock (_lock)
                {
                    pad.Present = true;
                    pad.Delivered = true;
                    pad.LastReport = Environment.TickCount64;
                    pad.Sample = s.Sample;
                    // A Bluetooth pad in its short report mode says nothing about the battery: keep what a full report said before.
                    if (s.Battery != "unknown" || !pad.Bluetooth) pad.Battery = s.Battery;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ps-hid] read: {ex.Message}");
            reason = ex.Message;
        }
        finally
        {
            // An error while waiting (a pad pulled out), an exception or the shutdown: the read may still be with the driver. It is
            // cancelled and waited for until the driver let go of the buffer; only then is anything freed.
            var stillPending = pending && !CancelAndWait(pad.Handle, overlapped);
            pad.Handle.Dispose();
            if (stillPending)
            {
                // Never seen; a few bytes lost are better than a driver writing into freed memory.
                Console.Error.WriteLine("[ps-hid] read did not end, its buffer is kept");
            }
            else
            {
                NativeMemory.Free(overlapped);
                NativeMemory.Free(buffer);
            }
            if (_running) Backoff(pad, reason);
            pad.Gone = true;
        }
    }

    /// <summary>NTSTATUS STATUS_PENDING in OVERLAPPED.Internal while an I/O has not ended.</summary>
    private const long StatusPending = 0x103;

    /// <summary>
    /// Cancels a read that is with the driver and waits until it has ended (completed, failed or cancelled). False when it still
    /// looks pending afterwards: its memory must then not be freed.
    /// </summary>
    private static unsafe bool CancelAndWait(SafeFileHandle handle, NativeOverlapped* overlapped)
    {
        // ERROR_NOT_FOUND: it had already ended. Either way GetOverlappedResult waits for the end on the read's own event.
        Hid.CancelIoEx(handle, overlapped);
        Hid.GetOverlappedResult(handle, overlapped, out _, true);
        return (long)overlapped->InternalLow != StatusPending;
    }

    /// <summary>
    /// After a read loop ended: while Windows still lists the pad (a gone device is just logged as gone), it is reopened only
    /// after 2 s, then 4 s, 8 s … at most 60 s while its reads keep failing; a run with reports starts over at 2 s. Logged once per step.
    /// </summary>
    private void Backoff(Pad pad, string reason)
    {
        static int DelayMs(int failures) => (int)Math.Min(BackoffMaxMs, 2000L << Math.Min(failures - 1, 10));
        bool listed;
        try
        {
            listed = Hid.DevicePaths().Any(p => string.Equals(p, pad.Path, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ps-hid] list: {ex.Message}");
            listed = true;
        }
        if (!listed)
        {
            // Unplugged or switched off: nothing to wait for, it starts fresh when it comes back.
            lock (_lock) _backoff.Remove(pad.Path);
            Console.Error.WriteLine($"[ps-hid] {pad.Model} disconnected (device gone)");
            return;
        }
        int delay;
        lock (_lock)
        {
            var known = _backoff.TryGetValue(pad.Path, out var before);
            var failures = pad.Delivered || !known ? 1 : before.Failures + 1;
            delay = DelayMs(failures);
            _backoff[pad.Path] = (failures, Environment.TickCount64 + delay);
            if (known && DelayMs(before.Failures) == delay) return;
        }
        Console.Error.WriteLine($"[ps-hid] {pad.Model} reads fail ({(reason.Length > 0 ? reason : "no reason given")}), next try in {delay / 1000} s");
    }

    private static unsafe class Hid
    {
        public const uint GenericRead = 0x80000000;
        public const uint FileShareRead = 0x1, FileShareWrite = 0x2;
        public const uint OpenExisting = 3;
        public const uint FileFlagOverlapped = 0x40000000;
        public const int ErrorIoPending = 997;
        public const int WaitTimeout = 258;
        private const uint DigcfPresent = 0x2, DigcfDeviceInterface = 0x10;
        private const int ErrorNoMoreItems = 259;
        private const int HidpStatusSuccess = 0x00110000;

        [StructLayout(LayoutKind.Sequential)]
        public struct Attributes
        {
            public int Size;
            public ushort VendorId;
            public ushort ProductId;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceInterfaceData
        {
            public int Size;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Caps
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            public fixed ushort Reserved[17];
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        /// <summary>The device paths of all present HID interfaces.</summary>
        public static List<string> DevicePaths()
        {
            var paths = new List<string>();
            HidD_GetHidGuid(out var guid);
            var set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return paths;
            try
            {
                // A sane upper bound: a PC does not have thousands of HID interfaces.
                for (uint index = 0; index < 512; index++)
                {
                    var data = new DeviceInterfaceData { Size = Marshal.SizeOf<DeviceInterfaceData>() };
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref data))
                    {
                        if (Marshal.GetLastPInvokeError() == ErrorNoMoreItems) break;
                        continue;
                    }
                    SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out var size, IntPtr.Zero);
                    if (size < 8 || size > 4096) continue;
                    var detail = Marshal.AllocHGlobal((int)size);
                    try
                    {
                        // SP_DEVICE_INTERFACE_DETAIL_DATA_W.cbSize: 8 on 64-bit, 6 on 32-bit (DWORD plus one WCHAR, aligned).
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, size, out _, IntPtr.Zero)) continue;
                        var path = Marshal.PtrToStringUni(detail + 4, ((int)size - 4) / 2).TrimEnd('\0');
                        if (path.Length > 0) paths.Add(path);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
            return paths;
        }

        /// <summary>The declared length of the device's input reports (with the report id), 0 when unknown.</summary>
        public static int InputReportLength(SafeFileHandle handle)
        {
            if (!HidD_GetPreparsedData(handle, out var preparsed)) return 0;
            try
            {
                return HidP_GetCaps(preparsed, out var caps) == HidpStatusSuccess ? caps.InputReportByteLength : 0;
            }
            finally
            {
                HidD_FreePreparsedData(preparsed);
            }
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ReadFile(SafeFileHandle handle, byte* buffer, uint toRead, IntPtr read, NativeOverlapped* overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetOverlappedResultEx(SafeFileHandle handle, NativeOverlapped* overlapped, out uint transferred, uint milliseconds, [MarshalAs(UnmanagedType.Bool)] bool alertable);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetOverlappedResult(SafeFileHandle handle, NativeOverlapped* overlapped, out uint transferred, [MarshalAs(UnmanagedType.Bool)] bool wait);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CancelIoEx(SafeFileHandle handle, NativeOverlapped* overlapped);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("hid.dll")]
        private static extern void HidD_GetHidGuid(out Guid guid);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        public static extern bool HidD_GetAttributes(SafeFileHandle handle, ref Attributes attributes);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("hid.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr preparsed);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("hid.dll")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool HidD_FreePreparsedData(IntPtr preparsed);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("hid.dll")]
        private static extern int HidP_GetCaps(IntPtr preparsed, out Caps caps);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr deviceInfo, ref Guid classGuid, uint index, ref DeviceInterfaceData data);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref DeviceInterfaceData data, IntPtr detail, uint detailSize, out uint requiredSize, IntPtr deviceInfo);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    }
}
