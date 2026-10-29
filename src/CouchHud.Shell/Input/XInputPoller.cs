using CouchHud.Core.Input;
using CouchHud.Shell.Native;

namespace CouchHud.Shell.Input;

/// <summary>
/// Polls XInput on a background thread, together with the PlayStation pads read natively over HID
/// (<see cref="PlayStationHid"/>, slots 4 to 7). Unlike browser gamepad input this keeps working while
/// a game has focus, which is what lets the Guide (or PS) button bring the HUD back from anywhere.
/// All slots go through one <see cref="PadMerger"/>, so a PlayStation pad that Steam Input or
/// DS4Windows also shows as a virtual XInput pad gives every press once. Events are raised on the
/// polling thread.
/// </summary>
public sealed class XInputPoller : IDisposable
{
    private const int PollMs = 16;
    private const int XInputSlots = 4;
    // Querying an empty slot is slow, so only re-check disconnected pads now and then.
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(2);

    private delegate uint GetStateFn(uint index, out XInputState state);

    private readonly GetStateFn? _getState;
    private readonly Pad[] _pads = [new(), new(), new(), new()];
    private readonly PadMerger _merger = new();
    private readonly PlayStationHid _playStation = new();
    private readonly bool[] _playStationConnected = new bool[PlayStationHid.MaxPads];
    private readonly PadKindTracker _kind = new();
    // Which XInput slots only mirror a PlayStation pad read over HID; guarded by _mirrorLock (the poll writes, any thread reads).
    private readonly PadMirrors _mirrors = new(XInputSlots, PlayStationHid.MaxPads);
    private readonly object _mirrorLock = new();
    // Second battery source for Xbox pads XInput knows no battery of (Bluetooth, Steam Input's virtual pad in the slot).
    private readonly GamepadBatteries _gamepads = new();
    private Thread? _thread;
    private volatile bool _running;

    private sealed class Pad
    {
        public bool Connected;
        public volatile bool SteamVirtual;
        public int Vendor = -1;
        public int Product = -1;
        public DateTime LastCheck = DateTime.MinValue;
    }

    public XInputPoller()
    {
        _getState = Probe();
        _merger.MirrorDetected += (a, b) =>
            Console.Error.WriteLine($"[input] slots {a} and {b} mirror each other (likely a virtual pad from Steam Input/DS4Windows)");
    }

    public event Action<string>? Down;
    public event Action<string>? Up;
    /// <summary>Slot, connected, and whether a new pad is likely the mirror of one that just connected (no toast for it).</summary>
    public event Action<int, bool, bool>? ConnectionChanged;

    /// <summary>The kind of pad used last changed ("xbox" or "playstation"), for the button symbols on screen.</summary>
    public event Action<string>? KindChanged;

    /// <summary>"xbox" or "playstation": the kind of pad that was pressed last (a generic pad counts as Xbox).</summary>
    public string Kind => _kind.Current;

    // Without XInput the page reads pads through the browser's Gamepad API instead (Xbox and PlayStation alike), so the
    // PlayStation pads are then not read here either: never two readers for one pad.
    public bool Available => _getState is not null;

    public void Start()
    {
        if (!Available || _running) return;
        _running = true;
        _playStation.Start();
        _thread = new Thread(Loop) { IsBackground = true, Name = "XInput", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Dispose()
    {
        _running = false;
        _thread?.Join(200);
        _playStation.Dispose();
        _gamepads.Dispose();
    }

    /// <summary>
    /// Connected pads with their battery state ("wired", "full", "medium", "low", "empty", "unknown"): XInput slots 0 to 3,
    /// PlayStation pads read over HID 4 to 7. An XInput slot known to mirror one of those (Steam Input, DS4Windows) is left out:
    /// the PlayStation pad itself is listed with its real battery. So is a mirror whose pad went away, until it presses something.
    /// An XInput pad without a known battery may get one, with a percent, from Windows.Gaming.Input
    /// (<see cref="BatteryRules.FillFromGamepads"/>: only when it is unambiguous which pad the report belongs to).
    /// </summary>
    public IReadOnlyList<(int Index, string Battery, int? Percent, int? Vendor, string? Source, int? Product)> Pads()
    {
        var result = new List<PadBattery>();
        var steamVirtual = new List<int>();
        for (var i = 0; i < _pads.Length; i++)
        {
            if (!_pads[i].Connected) continue;
            bool mirror;
            lock (_mirrorLock) mirror = _mirrors.IsPhantom(i);
            if (mirror) continue;
            var battery = "unknown";
            try
            {
                if (NativeMethods.XInputGetBatteryInformation((uint)i, 0, out var info) == NativeMethods.ErrorSuccess)
                {
                    battery = BatteryRules.PadLevel(info.BatteryType, info.BatteryLevel);
                }
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
            {
            }
            result.Add(new PadBattery(i, battery, null, _pads[i].Vendor is >= 0 and var vendor ? vendor : null, BatteryRules.Steps(battery) is not null || battery == "wired" ? BatterySources.XInput : null, _pads[i].Product is >= 0 and var product ? product : null));
            if (_pads[i].SteamVirtual) steamVirtual.Add(i);
        }
        var playStation = _playStation.Snapshot();
        for (var i = 0; i < playStation.Length; i++)
        {
            if (playStation[i].Battery is { } level) result.Add(new PadBattery(XInputSlots + i, level, null, null, BatterySources.Hid));
        }
        // WGI is only asked when an XInput pad needs it.
        var filled = result.Any(p => p.Index < XInputSlots && p.Level != "wired")
            ? BatteryRules.FillFromGamepads(result, steamVirtual, _gamepads.Reports())
            : result;
        // What XInput still says only coarsely (a third-party pad's "low") is not believed without a percent.
        return BatteryRules.DistrustCoarse(filled).Select(p => (p.Index, p.Level, p.Percent, p.Vendor, p.Source, p.Product)).ToList();
    }

    /// <summary>
    /// Physical pads connected, each pad once even with its Steam Input or DS4Windows mirror; errs low while a mirror is not yet
    /// recognized (see <see cref="PadMirrors.PhysicalCount"/>). For decisions such as "another controller is connected".
    /// </summary>
    public int PhysicalPadCount()
    {
        lock (_mirrorLock) return _mirrors.PhysicalCount();
    }

    /// <summary>
    /// Short vibration on all connected XInput pads; strength 0..1. PlayStation pads read over HID are never written to (read-only
    /// by design); through Steam Input or DS4Windows their virtual XInput pad rumbles them.
    /// </summary>
    public void Rumble(double strength, int milliseconds)
    {
        if (_getState is null) return;
        var speed = (ushort)(Math.Clamp(strength, 0, 1) * ushort.MaxValue);
        var connected = Enumerable.Range(0, _pads.Length).Where(i => _pads[i].Connected).ToList();
        if (connected.Count == 0) return;
        try
        {
            foreach (var i in connected)
            {
                var on = new XInputVibration { LeftMotorSpeed = (ushort)(speed / 2), RightMotorSpeed = speed };
                NativeMethods.XInputSetState((uint)i, ref on);
            }
            Task.Delay(milliseconds).ContinueWith(_ =>
            {
                foreach (var i in connected)
                {
                    var off = new XInputVibration();
                    NativeMethods.XInputSetState((uint)i, ref off);
                }
            }, TaskScheduler.Default);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
        }
    }

    /// <summary>Left stick and right stick Y of the pad that moves most, -1..1 (for the mouse mode).</summary>
    public (double X, double Y, double ScrollY) Sticks() => _merger.Sticks();

    public bool IsPressed(string button) => _merger.IsPressed(button);

    /// <summary>The direction is held by the left stick only (no d-pad); read it in the Down handler, on the polling thread.</summary>
    public bool FromStickOnly(string button) => _merger.FromStickOnly(button);

    private static GetStateFn? Probe()
    {
        try
        {
            NativeMethods.XInputGetStateEx(0, out _);
            return NativeMethods.XInputGetStateEx;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
        }
        try
        {
            NativeMethods.XInputGetState(0, out _);
            return NativeMethods.XInputGetState;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            Console.Error.WriteLine("[xinput] not available");
            return null;
        }
    }

    private void Loop()
    {
        // Early, on this background thread: Windows fills its gamepad list a moment after the first access.
        _gamepads.Start();
        while (_running)
        {
            try
            {
                Poll();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[xinput] {ex}");
            }
            Thread.Sleep(PollMs);
        }
    }

    private void Poll()
    {
        var now = DateTime.UtcNow;
        var samples = new PadSample?[XInputSlots + PlayStationHid.MaxPads];
        for (var i = 0; i < _pads.Length && _getState is not null; i++)
        {
            var pad = _pads[i];
            if (!pad.Connected && now - pad.LastCheck < ReconnectInterval) continue;
            pad.LastCheck = now;

            if (_getState((uint)i, out var state) != NativeMethods.ErrorSuccess)
            {
                if (!pad.Connected) continue;
                pad.Connected = false;
                Console.Error.WriteLine($"[input] pad {i} disconnected");
                pendingConnection.Add((i, false, LikelyMirror(xinput: true, connected: false)));
                continue;
            }
            if (!pad.Connected)
            {
                // Steam's virtual pad is known before the slot counts as connected (Pads() reads both from any thread).
                var vendor = VendorOf(i);
                var steamVirtual = vendor == 0x28DE;
                pad.Vendor = vendor;
                pad.Product = ProductOf(i);
                pad.SteamVirtual = steamVirtual;
                lock (_mirrorLock) _mirrors.MarkSteamVirtual(i, steamVirtual);
                pad.Connected = true;
                Console.Error.WriteLine($"[input] pad {i} connected, {Describe(i)}");
                pendingConnection.Add((i, true, LikelyMirror(xinput: true, connected: true)));
            }
            var g = state.Gamepad;
            samples[i] = new PadSample(g.Buttons, g.LeftTrigger, g.RightTrigger, g.ThumbLX, g.ThumbLY, g.ThumbRX, g.ThumbRY);
        }

        var playStation = _playStation.Snapshot();
        for (var i = 0; i < playStation.Length; i++)
        {
            samples[XInputSlots + i] = playStation[i].Sample;
            var connected = playStation[i].Battery is not null;
            if (connected == _playStationConnected[i]) continue;
            _playStationConnected[i] = connected;
            Console.Error.WriteLine(connected
                ? $"[input] pad {XInputSlots + i} connected, {_playStation.Describe(i)}, battery {playStation[i].Battery}"
                : $"[input] pad {XInputSlots + i} disconnected");
            pendingConnection.Add((XInputSlots + i, connected, LikelyMirror(xinput: false, connected)));
        }

        // Disconnected pads are null, so their held buttons are released through the merger.
        var events = _merger.Update(samples);
        // The kind of pad pressed last decides the button symbols; told before the events, so a press already shows the right ones.
        var pressed = _merger.PressedSlots;
        var connectedSlots = new bool[XInputSlots + PlayStationHid.MaxPads];
        for (var i = 0; i < XInputSlots; i++) connectedSlots[i] = _pads[i].Connected;
        for (var i = 0; i < PlayStationHid.MaxPads; i++) connectedSlots[XInputSlots + i] = _playStationConnected[i];
        var holds = new bool[connectedSlots.Length];
        var unsettled = _playStation.Unsettled;
        for (var i = 0; i < holds.Length; i++) holds[i] = _merger.Holds(i);
        bool xinputPressed;
        lock (_mirrorLock)
        {
            _mirrors.HidUnsettled = unsettled;
            _mirrors.Update(connectedSlots, _merger.NewPresses, holds);
            // A slot known to mirror a PlayStation pad (an activator pressing on its own) does not switch the symbols to Xbox.
            xinputPressed = pressed.Any(s => s < XInputSlots && !_mirrors.IsMirror(s));
        }
        if (_kind.Pressed(pressed.Any(s => s >= XInputSlots), xinputPressed)) KindChanged?.Invoke(_kind.Current);
        foreach (var (button, down) in events)
        {
            if (down) Down?.Invoke(button);
            else Up?.Invoke(button);
        }
        foreach (var (slot, connected, likelyMirror) in pendingConnection) ConnectionChanged?.Invoke(slot, connected, likelyMirror);
        pendingConnection.Clear();
    }

    // A pad and its Steam Input or DS4Windows mirror connect (and go) within this time of each other. A heuristic: a real Xbox
    // pad plugged in within 1.5 s of a PlayStation pad gets no toast of its own either (the other one's toast still shows).
    private const long MirrorConnectMs = 1500;
    // Last connect and disconnect per kind: [xinput ? 0 : 1, connected ? 0 : 1].
    private readonly long[,] _lastConnection = { { long.MinValue / 2, long.MinValue / 2 }, { long.MinValue / 2, long.MinValue / 2 } };

    /// <summary>
    /// Whether a connect or disconnect comes within <see cref="MirrorConnectMs"/> of the same change on a pad of the other kind
    /// (XInput and PlayStation): most likely a pad and its mirror, so it gets no toast of its own. The UI still says when no
    /// controller is left at all.
    /// </summary>
    private bool LikelyMirror(bool xinput, bool connected)
    {
        var tick = Environment.TickCount64;
        var change = connected ? 0 : 1;
        var quiet = tick - _lastConnection[xinput ? 1 : 0, change] <= MirrorConnectMs;
        _lastConnection[xinput ? 0 : 1, change] = tick;
        return quiet;
    }
    private readonly List<(int, bool, bool)> pendingConnection = [];

    /// <summary>The USB vendor id XInput reports for a slot, or -1 when it is not known.</summary>
    private static int VendorOf(int slot)
    {
        try
        {
            return NativeMethods.XInputGetCapabilitiesEx(1, (uint)slot, 0, out var caps) == NativeMethods.ErrorSuccess ? caps.VendorId : -1;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return -1;
        }
    }

    /// <summary>The USB product id XInput reports for a slot, or -1 when it is not known.</summary>
    private static int ProductOf(int slot)
    {
        try
        {
            return NativeMethods.XInputGetCapabilitiesEx(1, (uint)slot, 0, out var caps) == NativeMethods.ErrorSuccess ? caps.ProductId : -1;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            return -1;
        }
    }

    private string Describe(int slot)
    {
        var text = "type unknown";
        try
        {
            if (NativeMethods.XInputGetCapabilities((uint)slot, 0, out var caps) == NativeMethods.ErrorSuccess)
            {
                text = $"type {caps.SubType} (flags 0x{caps.Flags:X4})";
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
        }
        try
        {
            if (NativeMethods.XInputGetCapabilitiesEx(1, (uint)slot, 0, out var ex2) == NativeMethods.ErrorSuccess)
            {
                text += $", vid 0x{ex2.VendorId:X4} pid 0x{ex2.ProductId:X4}";
                if (ex2.VendorId == 0x28DE) text += " (Steam virtual pad)";
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
        }
        var battery = Pads().FirstOrDefault(p => p.Index == slot).Battery ?? "unknown";
        return $"{text}, battery {battery}";
    }
}
