using CouchHud.Core.Input;
using Windows.Gaming.Input;

namespace CouchHud.Shell.Input;

/// <summary>
/// Battery reports of Xbox pads through Windows.Gaming.Input, the second source next to XInput: an Xbox pad over Bluetooth, or one
/// that Steam Input puts behind its virtual pad, often has no battery in XInput but one here. Read-only. Windows fills
/// <see cref="Gamepad.Gamepads"/> asynchronously after the first access, so <see cref="Start"/> subscribes early; any thread will do.
/// Every WGI failure (no WinRT, COMException) is logged once and leaves an empty list: XInput keeps working on its own.
/// </summary>
public sealed class GamepadBatteries : IDisposable
{
    // Start and Dispose may run on different threads (the poll thread starts, the owner disposes): one at a time.
    private readonly object _lock = new();
    private volatile bool _started;
    private bool _disposed;
    private bool _warned;

    public void Start()
    {
        lock (_lock)
        {
            if (_started || _disposed) return;
            var subscribed = false;
            try
            {
                Gamepad.GamepadAdded += OnAdded;
                Gamepad.GamepadRemoved += OnRemoved;
                subscribed = true;
                _ = Gamepad.Gamepads.Count;
                _started = true;
            }
            catch (Exception ex)
            {
                Warn(ex);
                if (subscribed) Unsubscribe();
            }
        }
    }

    /// <summary>The battery of every gamepad Windows.Gaming.Input knows right now; empty when WGI is not there.</summary>
    public IReadOnlyList<GamepadBattery> Reports()
    {
        if (!_started) return [];
        var result = new List<GamepadBattery>();
        try
        {
            foreach (var pad in Gamepad.Gamepads)
            {
                try
                {
                    if (pad.TryGetBatteryReport() is not { } r) continue;
                    result.Add(BatteryRules.FromGamepadReport(r.RemainingCapacityInMilliwattHours, r.FullChargeCapacityInMilliwattHours, (int)r.Status));
                }
                catch (Exception ex)
                {
                    // One pad that went away mid-read: the others still count.
                    Warn(ex);
                }
            }
        }
        catch (Exception ex)
        {
            Warn(ex);
            return [];
        }
        return result;
    }

    private static void OnAdded(object? sender, Gamepad e) => Console.Error.WriteLine("[input] wgi gamepad added");

    private static void OnRemoved(object? sender, Gamepad e) => Console.Error.WriteLine("[input] wgi gamepad removed");

    private void Unsubscribe()
    {
        try
        {
            Gamepad.GamepadAdded -= OnAdded;
            Gamepad.GamepadRemoved -= OnRemoved;
        }
        catch (Exception ex)
        {
            Warn(ex);
        }
    }

    private void Warn(Exception ex)
    {
        if (_warned) return;
        _warned = true;
        Console.Error.WriteLine($"[input] wgi battery: {ex.GetType().Name} {ex.Message}");
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            if (!_started) return;
            _started = false;
            Unsubscribe();
        }
    }
}
