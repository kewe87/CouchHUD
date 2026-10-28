using CouchHud.Core.Input;
using CouchHud.Core.Localization;
using CouchHud.Shell.Native;

namespace CouchHud.Shell.Services;

/// <summary>
/// Battery levels for the top bar and the guide menu: the PC's own battery (laptops, handhelds), XInput controllers and connected
/// Bluetooth devices that report a percent. Read-only hardware queries on a thread pool thread, once a minute and when Windows reports a
/// power change (charger in or out); never on the UI thread. "Battery low" is said once per drop (<see cref="LowBatteryLatch"/>).
/// </summary>
/// <param name="pads">The connected pads with their level and, where known, a percent (XInputPoller.Pads).</param>
/// <param name="bluetooth">The connected Bluetooth devices with a battery percent.</param>
/// <param name="changed">The values changed; raised on a thread pool thread.</param>
/// <param name="warn">A localized "battery low" text; raised on a thread pool thread.</param>
public sealed class BatteryMonitor(
    Func<IReadOnlyList<(int Index, string Battery, int? Percent)>> pads,
    Func<Task<IReadOnlyList<DeviceBattery>>> bluetooth,
    Action<BatterySnapshot> changed,
    Action<string> warn) : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    // Bluetooth reads go through the device broker; a hanging one must not hold the next polls, and no second one starts beside it.
    private static readonly TimeSpan BluetoothTimeout = TimeSpan.FromSeconds(10);

    private readonly LowBatteryLatch _latch = new();
    private readonly SingleFlight<IReadOnlyList<DeviceBattery>> _bluetoothRead = new();
    private Timer? _timer;
    private int _busy;
    private volatile bool _disposed;
    private BatterySnapshot _current = BatterySnapshot.Empty;
    private string _signature = "";
    private long _polledAt = long.MinValue / 2;

    /// <summary>The last values read (empty before the first poll).</summary>
    public BatterySnapshot Current => Volatile.Read(ref _current);

    public void Start()
    {
        _timer = new Timer(_ => Refresh(), null, TimeSpan.FromSeconds(2), Interval);
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    /// <summary>Reads again soon unless the last read is younger than <paramref name="maxAge"/> (the guide menu opens).</summary>
    public void RefreshIfOlder(TimeSpan maxAge)
    {
        if (Environment.TickCount64 - Interlocked.Read(ref _polledAt) >= maxAge.TotalMilliseconds) Refresh();
    }

    /// <summary>Reads again now (a controller came or went), off the calling thread.</summary>
    public void Refresh()
    {
        if (_disposed) return;
        _ = Task.Run(PollAsync);
    }

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.StatusChange) Refresh();
    }

    private async Task PollAsync()
    {
        if (_disposed || Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            Interlocked.Exchange(ref _polledAt, Environment.TickCount64);
            var system = ReadSystem();
            var padList = pads().Select(p => new PadBattery(p.Index, p.Battery, p.Percent)).ToList();
            IReadOnlyList<DeviceBattery> devices;
            try
            {
                // Null: the last read still hangs or this one timed out; keep what was known.
                devices = await _bluetoothRead.RunAsync(bluetooth, BluetoothTimeout) ?? Current.Devices;
            }
            catch (Exception ex)
            {
                // Keep what was known: a failed read is no reason to drop a device (and to warn again when it comes back).
                Console.Error.WriteLine($"[battery] bluetooth: {ex.Message}");
                devices = Current.Devices;
            }
            if (_disposed) return;
            var snapshot = new BatterySnapshot(system, padList, devices);
            var low = _latch.Update(LowBatteryLatch.Readings(snapshot));
            var signature = Signature(snapshot);
            Volatile.Write(ref _current, snapshot);
            if (signature != _signature)
            {
                _signature = signature;
                changed(snapshot);
            }
            foreach (var key in low)
            {
                if (Message(key, snapshot) is { } text) warn(text);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[battery] {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    /// <summary>The PC's battery, or null on a desktop (or when Windows does not answer).</summary>
    private static SystemBattery? ReadSystem()
    {
        try
        {
            return NativeMethods.GetSystemPowerStatus(out var s) ? BatteryRules.FromPowerStatus(s.AcLineStatus, s.BatteryFlag, s.BatteryLifePercent) : null;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }

    private static string Signature(BatterySnapshot s) =>
        $"{s.System}|{string.Join(";", s.Pads.Select(p => $"{p.Index}:{p.Level}:{p.Percent}"))}|{string.Join(";", s.Devices.Select(d => $"{d.Id}:{d.Name}:{d.Percent}"))}";

    private static string? Message(string key, BatterySnapshot s)
    {
        if (key == "system") return s.System is { } sys ? Strings.Get("battery.lowSystem", sys.Percent) : null;
        if (key.StartsWith("pad:", StringComparison.Ordinal) && int.TryParse(key.AsSpan(4), out var index)) return Strings.Get("battery.lowPad", BatteryRules.PadNumber(index, s.Pads.Select(p => p.Index)));
        if (key.StartsWith("bt:", StringComparison.Ordinal))
        {
            var id = key[3..];
            return s.Devices.FirstOrDefault(d => d.Id == id) is { } d ? Strings.Get("battery.lowDevice", d.Name, d.Percent) : null;
        }
        return null;
    }

    public void Dispose()
    {
        _disposed = true;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _timer?.Dispose();
        _timer = null;
    }
}
