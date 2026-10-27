namespace CouchHud.Core.Input;

/// <summary>What the Bluetooth radio is doing, as far as CouchHUD can tell.</summary>
public enum RadioStatus
{
    /// <summary>Not read (yet), or the read failed.</summary>
    Unknown,
    On,
    /// <summary>Switched off: by the user, the quick settings or airplane mode. Can be switched on again.</summary>
    Off,
    /// <summary>Adapter switched off in the device manager, by a policy or the hardware switch: not ours to change.</summary>
    Disabled,
    /// <summary>No Bluetooth adapter, or no driver for it.</summary>
    NoAdapter,
    /// <summary>Windows does not let this program switch radios (privacy setting, policy).</summary>
    AccessDenied
}

/// <summary>Why the recovery looks at the radio now.</summary>
public enum RecoveryTrigger
{
    /// <summary>CouchHUD started (also after a restart of the PC).</summary>
    Startup,
    /// <summary>The radio went off while CouchHUD runs.</summary>
    RadioOff,
    /// <summary>A failed attempt is due again, or the radio changed to a state other than on and off.</summary>
    Retry,
    /// <summary>The countdown ran out (or "now" was chosen) without "leave it off": on, without a second countdown.</summary>
    CountdownElapsed
}

public enum RecoveryAction
{
    /// <summary>Nothing to do.</summary>
    None,
    /// <summary>Nothing CouchHUD can change: write it to the log (once), no attempt.</summary>
    Log,
    /// <summary>Switch the radio on now.</summary>
    EnableNow,
    /// <summary>Show a countdown that keyboard, mouse or another controller can cancel; then switch on.</summary>
    Countdown
}

/// <param name="Enabled">The setting "Bluetooth für Controller automatisch einschalten".</param>
/// <param name="PairedControllers">Paired Bluetooth devices that are game controllers.</param>
/// <param name="ConnectedPads">Controllers connected right now (XInput); with the radio off none of them uses Bluetooth.</param>
/// <param name="OtherInputRecent">Keyboard or mouse was used a moment ago: someone could have switched it off on purpose.</param>
/// <param name="AirplaneMode">Every radio of the PC is off (airplane mode): on purpose more often than not.</param>
public sealed record RecoveryInput(bool Enabled, RadioStatus Radio, int PairedControllers, int ConnectedPads, bool OtherInputRecent, RecoveryTrigger Trigger,
    bool AirplaneMode = false);

public sealed record RecoveryDecision(RecoveryAction Action, string Reason);

/// <summary>
/// Bluetooth switched off by accident takes the controller with it, and a locked couch without a controller is hard to leave.
/// So CouchHUD switches the radio on again when a Bluetooth controller is paired and no other controller is connected. It only
/// ever switches an existing radio on: no pairing, no discoverability, no other radio. Pure decision, see <see cref="Decide"/>.
/// </summary>
public static class BluetoothRecovery
{
    /// <summary>Seconds of the countdown shown when someone may have switched Bluetooth off on purpose.</summary>
    public const int CountdownSeconds = 10;

    public static RecoveryDecision Decide(RecoveryInput input)
    {
        if (!input.Enabled) return new(RecoveryAction.None, "setting off");
        switch (input.Radio)
        {
            case RadioStatus.On:
                return new(RecoveryAction.None, "radio on");
            case RadioStatus.Unknown:
                return new(RecoveryAction.None, "radio unknown");
            case RadioStatus.NoAdapter:
                return new(RecoveryAction.Log, "no Bluetooth adapter or driver");
            case RadioStatus.Disabled:
                return new(RecoveryAction.Log, "radio disabled by the system (device manager, policy or hardware switch)");
            case RadioStatus.AccessDenied:
                return new(RecoveryAction.Log, "Windows does not allow switching the radio");
        }
        // Off from here on.
        if (input.PairedControllers <= 0) return new(RecoveryAction.None, "no Bluetooth controller paired");
        if (input.ConnectedPads > 0) return new(RecoveryAction.None, "another controller is connected");
        // A countdown that ran out was not cancelled: no second one.
        if (input.Trigger == RecoveryTrigger.CountdownElapsed) return new(RecoveryAction.EnableNow, "countdown not cancelled");
        // Keyboard or mouse at hand: someone can say "leave it off". Without them nobody could get the controller back, so it comes
        // back on at once, airplane mode or not (being locked out weighs more than a radio during a flight).
        if (input.OtherInputRecent && (input.Trigger == RecoveryTrigger.RadioOff || input.AirplaneMode))
            return new(RecoveryAction.Countdown, input.AirplaneMode ? "airplane mode with keyboard or mouse in use" : "switched off while keyboard or mouse were in use");
        return new(RecoveryAction.EnableNow, "Bluetooth controller without radio");
    }
}

/// <summary>
/// Keeps the recovery from fighting the user or a broken adapter, on a monotonic clock (time since some start, never the wall clock):
/// failed attempts wait longer each time (30 s up to 15 min) and after <see cref="MaxFailures"/> failures in a row it gives up until
/// the next start. At most <see cref="MaxEnablesPerWindow"/> switch-ons within <see cref="Window"/>: a full window waits for the next
/// one, and the <see cref="MaxFullWindows"/>-th full window in a row (something keeps forcing Bluetooth off) gives up until the next
/// start. Not thread-safe; the caller keeps it on one thread.
/// </summary>
public sealed class RecoveryBackoff
{
    public const int MaxFailures = 6;
    public const int MaxEnablesPerWindow = 3;
    public const int MaxFullWindows = 2;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMinutes(15);

    private readonly List<TimeSpan> _enables = [];
    private int _failures;
    private int _fullWindows;
    private TimeSpan _lastFullWindow = TimeSpan.MinValue;
    private TimeSpan _notBefore = TimeSpan.MinValue;

    /// <summary>True once the recovery gave up for this run (failures in a row, or forced off window after window).</summary>
    public bool GaveUp { get; private set; }

    public int Failures => _failures;

    /// <summary>May an attempt start now?</summary>
    public bool MayTry(TimeSpan now)
    {
        if (GaveUp) return false;
        if (now < _notBefore) return false;
        _enables.RemoveAll(t => now - t >= Window);
        if (_enables.Count < MaxEnablesPerWindow) return true;
        // A quiet stretch since the last full window: that one does not count any more.
        if (_fullWindows > 0 && now - _lastFullWindow >= Window * 3) _fullWindows = 0;
        _fullWindows++;
        _lastFullWindow = now;
        if (_fullWindows >= MaxFullWindows)
        {
            GaveUp = true;
            return false;
        }
        // Wait for the window to pass, then a fresh one: never a tight loop.
        _notBefore = _enables.Min() + Window;
        _enables.Clear();
        return false;
    }

    /// <summary>When the next attempt may run (after a failure, or once the window has passed).</summary>
    public TimeSpan NextTry => _notBefore;

    public void Succeeded(TimeSpan now)
    {
        _failures = 0;
        _notBefore = TimeSpan.MinValue;
        _enables.Add(now);
    }

    public void Failed(TimeSpan now)
    {
        _failures++;
        if (_failures >= MaxFailures)
        {
            GaveUp = true;
            return;
        }
        var delay = TimeSpan.FromTicks(Math.Min(MaxDelay.Ticks, FirstDelay.Ticks * (1L << Math.Min(_failures - 1, 10))));
        _notBefore = now + delay;
    }
}

/// <summary>Is a paired Bluetooth device a game controller? From what Windows knows about it, the name as the last hint.</summary>
public static class BluetoothDeviceKind
{
    /// <summary>Bluetooth LE appearance values (GAP): generic HID 0x03C0, joystick 0x03C3, gamepad 0x03C4.</summary>
    private const int LeJoystick = 0x03C3, LeGamepad = 0x03C4;

    /// <param name="categories">System.Devices.Aep.Category (e.g. "Input.Gaming").</param>
    /// <param name="codMajor">Bluetooth classic major device class (5 = peripheral).</param>
    /// <param name="codMinor">Bluetooth classic minor device class (6 bits; for a peripheral the low 4 bits: 1 joystick, 2 gamepad).</param>
    /// <param name="leAppearance">Bluetooth LE appearance.</param>
    public static bool IsController(IEnumerable<string>? categories, int? codMajor, int? codMinor, int? leAppearance, string? name)
    {
        if (categories?.Any(c => c is not null && (c.StartsWith("Input.Gaming", StringComparison.OrdinalIgnoreCase)
                                                   || c.StartsWith("Input.Gamepad", StringComparison.OrdinalIgnoreCase)
                                                   || c.StartsWith("Input.Joystick", StringComparison.OrdinalIgnoreCase))) == true) return true;
        if (codMajor == 5 && codMinor is { } minor && (minor & 0x0F) is 1 or 2) return true;
        if (leAppearance is LeJoystick or LeGamepad) return true;
        return Kind(name) == "controller";
    }

    /// <summary>A rough icon category from the name: good enough for a list on the TV.</summary>
    public static string Kind(string? name)
    {
        var n = (name ?? "").ToLowerInvariant();
        if (n.Contains("controller") || n.Contains("gamepad") || n.Contains("dualsense") || n.Contains("dualshock") || n.Contains("8bitdo") || n.Contains("joy-con")) return "controller";
        if (n.Contains("headset") || n.Contains("buds") || n.Contains("airpods") || n.Contains("wh-") || n.Contains("wf-") || n.Contains("headphone") || n.Contains("kopfhörer") || n.Contains("bose") || n.Contains("jbl") || n.Contains("sennheiser")) return "headphones";
        if (n.Contains("keyboard") || n.Contains("tastatur")) return "keyboard";
        if (n.Contains("mouse") || n.Contains("maus")) return "mouse";
        if (n.Contains("speaker") || n.Contains("soundbar") || n.Contains("box")) return "speaker";
        if (n.Contains("phone") || n.Contains("iphone") || n.Contains("galaxy") || n.Contains("pixel")) return "phone";
        return "other";
    }
}
