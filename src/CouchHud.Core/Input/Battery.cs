namespace CouchHud.Core.Input;

/// <summary>The PC's own battery (a laptop or handheld): percent, on the charger, low.</summary>
public sealed record SystemBattery(int Percent, bool Charging, bool PluggedIn, bool Low);

/// <summary>
/// A controller: its slot and the level Windows reports ("wired", "full", "medium", "low", "empty", "unknown"). <see cref="Percent"/>
/// only where a percent is known (an Xbox pad read through Windows.Gaming.Input, <see cref="BatteryRules.FillFromGamepads"/>).
/// </summary>
public sealed record PadBattery(int Index, string Level, int? Percent = null);

/// <summary>
/// The battery report of a Windows.Gaming.Input gamepad (<see cref="BatteryRules.FromGamepadReport"/>): its level, the percent when
/// a battery is there, and whether it charges.
/// </summary>
public sealed record GamepadBattery(string Level, int? Percent, bool Charging);

/// <summary>
/// A connected Bluetooth device that tells Windows its battery percent. <see cref="Id"/> is an opaque key
/// (<see cref="BatteryRules.DeviceKey"/>), never the endpoint id with its Bluetooth addresses.
/// </summary>
public sealed record DeviceBattery(string Id, string Name, string Kind, int Percent);

/// <summary>Everything the top bar and the guide menu show about batteries; <see cref="System"/> is null on a PC without a battery.</summary>
public sealed record BatterySnapshot(SystemBattery? System, IReadOnlyList<PadBattery> Pads, IReadOnlyList<DeviceBattery> Devices)
{
    public static readonly BatterySnapshot Empty = new(null, [], []);
}

/// <summary>
/// Reading battery values from Windows: only plain mapping, no I/O (the Shell reads the hardware and hands the raw values in).
/// Anything outside the documented ranges counts as "not known" and is not shown.
/// </summary>
public static class BatteryRules
{
    /// <summary>At or below this percent a battery counts as low (the warning).</summary>
    public const int LowPercent = 15;

    /// <summary>A low battery is warned about again only after it came back to at least this percent (no toast on every 14/15 % wobble).</summary>
    public const int RecoveredPercent = 20;

    /// <summary>
    /// The number a pad is shown with ("Controller 2"): an XInput pad (slots 0 to 3) its player number, a PlayStation pad read over
    /// HID (slots 4 on) the next numbers after the XInput pads, in its own order (the same as batteryEntries in the UI).
    /// </summary>
    public static int PadNumber(int index, IEnumerable<int> connectedIndexes)
    {
        const int xinputSlots = 4;
        if (index < xinputSlots) return index + 1;
        var all = connectedIndexes.Where(i => i >= 0).Distinct().ToList();
        var after = all.Where(i => i < xinputSlots).Select(i => i + 1).DefaultIfEmpty(0).Max();
        return after + all.Count(i => i >= xinputSlots && i < index) + 1;
    }

    // SYSTEM_POWER_STATUS.BatteryFlag
    private const byte FlagCharging = 8;
    private const byte FlagNoBattery = 128;
    private const byte FlagUnknown = 255;

    /// <summary>
    /// SYSTEM_POWER_STATUS (GetSystemPowerStatus) as the top bar shows it. Null when the PC has no system battery (a desktop: flag 128),
    /// when Windows does not know (flag 255, percent 255) or reports a percent above 100.
    /// </summary>
    public static SystemBattery? FromPowerStatus(byte acLineStatus, byte batteryFlag, byte lifePercent)
    {
        if (batteryFlag == FlagUnknown || (batteryFlag & FlagNoBattery) != 0) return null;
        if (lifePercent > 100) return null;
        var plugged = acLineStatus == 1;
        var charging = (batteryFlag & FlagCharging) != 0;
        return new SystemBattery(lifePercent, charging, plugged, lifePercent <= LowPercent && !plugged && !charging);
    }

    // XINPUT_BATTERY_INFORMATION
    private const byte TypeDisconnected = 0;
    private const byte TypeWired = 1;
    private const byte TypeUnknown = 0xFF;

    /// <summary>XINPUT_BATTERY_INFORMATION as a level name: a wired pad has no battery to show, an unknown type or level says "unknown".</summary>
    public static string PadLevel(byte batteryType, byte batteryLevel) => batteryType switch
    {
        TypeWired => "wired",
        TypeDisconnected or TypeUnknown => "unknown",
        _ => batteryLevel switch { 0 => "empty", 1 => "low", 2 => "medium", 3 => "full", _ => "unknown" }
    };

    // Windows.Gaming.Input BatteryReport.Status (Windows.System.Power.BatteryStatus)
    private const int StatusNotPresent = 0;
    private const int StatusDischarging = 1;
    private const int StatusIdle = 2;
    private const int StatusCharging = 3;

    /// <summary>A percent as one of the XInput levels: empty up to 5 %, low up to <see cref="LowPercent"/> (the warning), medium up to 70 %, full above.</summary>
    public static string PercentLevel(int percent) => percent switch
    {
        <= 5 => "empty",
        <= LowPercent => "low",
        <= 70 => "medium",
        _ => "full"
    };

    /// <summary>
    /// A Windows.Gaming.Input BatteryReport (Gamepad.TryGetBatteryReport): no battery (status NotPresent) is "wired", a remaining and
    /// a full charge capacity in mWh give the percent (capped at 100: a battery reporting more than its full charge is full). A pad on
    /// its charger is never low or empty: it is not about to die, the percent still says how far it is. Anything else (no capacities,
    /// a full charge of 0, an unknown status) is "unknown" without a percent.
    /// </summary>
    public static GamepadBattery FromGamepadReport(int? remainingMwh, int? fullChargeMwh, int status)
    {
        if (status == StatusNotPresent) return new GamepadBattery("wired", null, false);
        if (status is not (StatusDischarging or StatusIdle or StatusCharging)) return new GamepadBattery("unknown", null, false);
        if (remainingMwh is not { } remaining || fullChargeMwh is not { } full || remaining < 0 || full <= 0)
            return new GamepadBattery("unknown", null, false);
        var percent = (int)Math.Min(100, Math.Round(remaining * 100.0 / full, MidpointRounding.AwayFromZero));
        var charging = status == StatusCharging;
        var level = PercentLevel(percent);
        if (charging && PadLow(level)) level = "medium";
        return new GamepadBattery(level, percent, charging);
    }

    /// <summary>
    /// Fills in an XInput pad whose battery XInput does not know (Steam Input's virtual pad in its slot, or an Xbox pad over Bluetooth
    /// XInput says nothing about) from Windows.Gaming.Input. Which XInput slot is which WGI gamepad Windows does not tell, so only the
    /// unambiguous case is filled: exactly one WGI gamepad has a battery percent, no XInput pad (slots 0 to 3) knows its own battery
    /// level (that one would be the same pad seen twice), and a charging report is not taken while an XInput pad says wired (most
    /// likely the same pad on its cable). The report then goes to the one XInput pad with "unknown" that is not Steam's virtual pad;
    /// only when there is none, to the one Steam virtual slot with "unknown", and not while a pad says wired (the same pad on its
    /// cable, seen twice). Two or more candidates of the same kind: nothing is filled. PlayStation pads read over HID (slots 4 on) are never touched: they bring their own battery, and WGI has
    /// none for them (their Steam or DS4Windows mirror is a gamepad without a battery report).
    /// </summary>
    /// <param name="pads">The pads as listed (mirrors already left out).</param>
    /// <param name="steamVirtual">XInput slots that are Steam's virtual pad (vendor 0x28DE).</param>
    /// <param name="gamepads">The battery of every WGI gamepad (<see cref="FromGamepadReport"/>).</param>
    public static IReadOnlyList<PadBattery> FillFromGamepads(IReadOnlyList<PadBattery> pads, IReadOnlyCollection<int> steamVirtual, IReadOnlyList<GamepadBattery> gamepads)
    {
        const int xinputSlots = 4;
        var usable = gamepads.Where(g => g.Percent is not null).ToList();
        if (usable.Count != 1) return pads;
        var report = usable[0];
        var xinput = pads.Where(p => p.Index is >= 0 and < xinputSlots).ToList();
        if (xinput.Any(p => Steps(p.Level) is not null)) return pads;
        if (report.Charging && xinput.Any(p => p.Level == "wired")) return pads;
        var unknown = xinput.Where(p => p.Level == "unknown").ToList();
        var real = unknown.Where(p => !steamVirtual.Contains(p.Index)).ToList();
        PadBattery target;
        if (real.Count == 1) target = real[0];
        else if (real.Count > 1) return pads;
        else
        {
            // Only Steam virtual slots: one, and no pad on a cable beside it (that pad would be seen twice; WGI may say idle there,
            // not charging).
            var steam = unknown.Where(p => steamVirtual.Contains(p.Index)).ToList();
            if (steam.Count != 1 || xinput.Any(p => p.Level == "wired")) return pads;
            target = steam[0];
        }
        return pads.Select(p => ReferenceEquals(p, target) ? p with { Level = report.Level, Percent = report.Percent } : p).ToList();
    }

    /// <summary>The 4-step icon of a pad's level: 0 (empty) to 3 (full); null for wired and unknown (no icon).</summary>
    public static int? Steps(string level) => level switch
    {
        "empty" => 0,
        "low" => 1,
        "medium" => 2,
        "full" => 3,
        _ => null
    };

    /// <summary>
    /// The value of DEVPKEY_Bluetooth_Battery ({104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2), a byte from 0 to 100. Anything else (missing,
    /// another type, out of range) is null: the device then shows no battery.
    /// </summary>
    public static int? BluetoothPercent(object? value)
    {
        long? n = value switch
        {
            byte b => b,
            sbyte sb => sb,
            ushort u => u,
            short s => s,
            uint ui => ui,
            int i => i,
            _ => null
        };
        return n is >= 0 and <= 100 ? (int)n : null;
    }

    /// <summary>Whether a pad's level is a warning (low or empty).</summary>
    public static bool PadLow(string level) => level is "low" or "empty";

    /// <summary>
    /// The key a Bluetooth device goes to the UI under: a short SHA-256 of its endpoint id. The id holds the PC's and the device's
    /// Bluetooth addresses; the UI only needs something stable to tell devices apart.
    /// </summary>
    public static string DeviceKey(string endpointId) =>
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(endpointId)))[..16];
}

/// <summary>
/// At most one read in flight: a read that outlives its timeout is left running, and until it ends no new one starts (the caller keeps
/// its last values). A hung device query cannot pile up another one on every poll. Not thread-safe: one caller at a time.
/// </summary>
public sealed class SingleFlight<T> where T : class
{
    private Task<T>? _inFlight;

    /// <summary>Whether an earlier read is still running.</summary>
    public bool Busy => _inFlight is { IsCompleted: false };

    /// <summary>The result, or null when an earlier read is still running or this one did not finish within <paramref name="timeout"/>.</summary>
    public async Task<T?> RunAsync(Func<Task<T>> start, TimeSpan timeout)
    {
        if (Busy) return null;
        var task = start();
        _inFlight = task;
        try
        {
            return await task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }
}

/// <summary>
/// "Battery low" once per drop: a battery is warned about when it becomes low and only again after it recovered (charged to
/// <see cref="BatteryRules.RecoveredPercent"/>, on the charger, a pad back at medium or full) or after it was gone (a pad that was
/// switched off may be another pad when it comes back). On top of that each key is warned at most once per <see cref="Cooldown"/>,
/// remembered even while it is gone: a pad that keeps dropping its connection or a device whose reported level swings does not toast
/// on every poll. A drop inside the cooldown is latched silently. Not thread-safe: one caller (the battery monitor's poll).
/// </summary>
/// <param name="clockMs">Milliseconds of a monotonic clock (tests pass their own); Environment.TickCount64 by default.</param>
public sealed class LowBatteryLatch(Func<long>? clockMs = null)
{
    /// <summary>The least time between two warnings about the same battery.</summary>
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);

    private readonly Func<long> _clock = clockMs ?? (() => Environment.TickCount64);
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastWarning = new(StringComparer.Ordinal);

    /// <summary>One reading per battery present now: key, low now, recovered now. Returns the keys to warn about, in the given order.</summary>
    public IReadOnlyList<string> Update(IEnumerable<(string Key, bool Low, bool Recovered)> readings)
    {
        var now = _clock();
        var cooldown = (long)Cooldown.TotalMilliseconds;
        var present = new HashSet<string>(StringComparer.Ordinal);
        var warn = new List<string>();
        foreach (var (key, low, recovered) in readings)
        {
            if (!present.Add(key)) continue;
            if (low)
            {
                if (!_warned.Add(key)) continue;
                if (_lastWarning.TryGetValue(key, out var last) && now - last < cooldown) continue;
                _lastWarning[key] = now;
                warn.Add(key);
            }
            else if (recovered)
            {
                _warned.Remove(key);
            }
        }
        _warned.RemoveWhere(k => !present.Contains(k));
        // Expired cooldowns are dropped, so the table stays small with many devices coming and going.
        foreach (var stale in _lastWarning.Where(e => now - e.Value >= cooldown).Select(e => e.Key).ToList())
            _lastWarning.Remove(stale);
        return warn;
    }

    /// <summary>The readings of a snapshot, keyed "system", "pad:N" and "bt:ID".</summary>
    public static IEnumerable<(string Key, bool Low, bool Recovered)> Readings(BatterySnapshot snapshot)
    {
        if (snapshot.System is { } s)
            yield return ("system", s.Low, s.PluggedIn || s.Charging || s.Percent >= BatteryRules.RecoveredPercent);
        foreach (var p in snapshot.Pads)
            yield return ($"pad:{p.Index}", BatteryRules.PadLow(p.Level), p.Level is "medium" or "full" or "wired");
        foreach (var d in snapshot.Devices)
            yield return ($"bt:{d.Id}", d.Percent <= BatteryRules.LowPercent, d.Percent >= BatteryRules.RecoveredPercent);
    }
}
