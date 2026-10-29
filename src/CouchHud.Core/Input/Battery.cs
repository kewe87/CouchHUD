namespace CouchHud.Core.Input;

/// <summary>The PC's own battery (a laptop or handheld): percent, on the charger, low.</summary>
public sealed record SystemBattery(int Percent, bool Charging, bool PluggedIn, bool Low);

/// <summary>
/// A controller: its slot and the level Windows reports ("wired", "full", "medium", "low", "empty", "unknown"). <see cref="Percent"/>
/// only where a percent is known (an Xbox pad read through Windows.Gaming.Input, <see cref="BatteryRules.FillFromGamepads"/>, or a
/// Bluetooth battery that clearly belongs to it, <see cref="BatteryRules.MatchBluetooth"/>). <see cref="Vendor"/> is the USB vendor id
/// XInput reports for the slot (null when not known).
/// </summary>
public sealed record PadBattery(int Index, string Level, int? Percent = null, int? Vendor = null, string? Source = null, int? Product = null);

/// <summary>The names of the sources a pad's battery can come from (<see cref="PadBattery.Source"/>); the log says them, nothing else uses them.</summary>
public static class BatterySources
{
    public const string XInput = "xinput";
    public const string Wgi = "wgi";
    public const string Bluetooth = "bluetooth";
    public const string Hid = "hid";
    public const string None = "none";
}

/// <summary>
/// The battery report of a Windows.Gaming.Input gamepad (<see cref="BatteryRules.FromGamepadReport"/>): its level, the percent when
/// a battery is there, and whether it charges.
/// </summary>
public sealed record GamepadBattery(string Level, int? Percent, bool Charging);

/// <summary>
/// A connected Bluetooth device that tells Windows its battery percent. <see cref="Id"/> is an opaque key
/// (<see cref="BatteryRules.DeviceKey"/>), never the endpoint id with its Bluetooth addresses.
/// </summary>
public sealed record DeviceBattery(string Id, string Name, string Kind, int Percent)
{
    /// <summary>
    /// The device's Bluetooth address (12 upper-case hex digits, <see cref="BatteryRules.AddressOfEndpoint"/>), only to match it with a
    /// pad's HID node; never sent to the UI.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Address { get; init; }
}

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
        // One XInput pad with only a coarse level and one WGI gamepad in all (every XInput pad is a WGI gamepad too): the same pad,
        // and Windows' percent wins over the coarse level (some third-party pads report "low" to XInput while nearly full).
        if (gamepads.Count == 1 && xinput.Count == 1 && xinput[0].Percent is null && Steps(xinput[0].Level) is not null)
        {
            // Only a Microsoft pad's report is trusted: a third-party pad's report through its own driver can be anything (an ASUS pad
            // showed a fixed 10 %), and Windows does not say which pad a report belongs to beyond "the only one".
            if (xinput[0].Vendor != MicrosoftVendor) return pads;
            return pads.Select(p => ReferenceEquals(p, xinput[0]) ? p with { Level = report.Level, Percent = report.Percent, Source = BatterySources.Wgi } : p).ToList();
        }
        if (xinput.Any(p => Steps(p.Level) is not null)) return pads;
        if (report.Charging && xinput.Any(p => p.Level == "wired")) return pads;
        var unknown = xinput.Where(p => p.Level == "unknown").ToList();
        // A third-party pad (or one whose vendor is not known) is never filled from WGI, see above; Steam's virtual pad is the exception
        // (the report is the pad behind it).
        var real = unknown.Where(p => !steamVirtual.Contains(p.Index)).ToList();
        var others = real.Count;
        real = real.Where(p => p.Vendor == MicrosoftVendor).ToList();
        if (others > real.Count) return pads;
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
        return pads.Select(p => ReferenceEquals(p, target) ? p with { Level = report.Level, Percent = report.Percent, Source = BatterySources.Wgi } : p).ToList();
    }

    /// <summary>USB vendor id of Microsoft: only an Xbox pad's own XInput battery level is trusted as it is.</summary>
    public const int MicrosoftVendor = 0x045E;

    private const int SteamVendor = 0x28DE;

    /// <summary>
    /// A coarse XInput "low" or "empty" of a pad that is not from Microsoft is not believed on its own (some third-party pads report
    /// "low" all the time): it becomes "unknown", shown without a level and never warned about. A pad without a known vendor keeps its
    /// level (nothing to doubt it with), and a pad with a percent keeps both (the percent says it).
    /// </summary>
    public static IReadOnlyList<PadBattery> DistrustCoarse(IReadOnlyList<PadBattery> pads) =>
        pads.Select(p => p.Index is >= 0 and < 4 && p.Percent is null && PadLow(p.Level) && p.Vendor is { } v && v != MicrosoftVendor
            ? p with { Level = "unknown" } : p).ToList();

    /// <summary>The AQS query for the HID service nodes of Bluetooth LE devices (property DeviceInstanceId: InstanceId does not support starts-with).</summary>
    public const string HidNodeQuery = "System.Devices.DeviceInstanceId:~<\"BTHLEDEVICE\\{00001812\"";

    /// <summary>The key of <c>hidAddresses</c> in <see cref="MatchBluetooth"/>: USB vendor and product id.</summary>
    public static int PadKey(int vendor, int product) => (vendor << 16) | (product & 0xFFFF);

    /// <summary>
    /// The Bluetooth address of a device endpoint id ("BluetoothLE#BluetoothLE5c:3a:45:00:11:22-c1:5a:f9:55:47:02": the part after the
    /// PC's own address) as 12 upper-case hex digits; null when the id has no such part.
    /// </summary>
    public static string? AddressOfEndpoint(string? endpointId)
    {
        if (endpointId is null || endpointId.Length > 200) return null;
        var m = System.Text.RegularExpressions.Regex.Match(endpointId, @"-(?<a>[0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){5})$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(50));
        return m.Success ? m.Groups["a"].Value.Replace(":", "").ToUpperInvariant() : null;
    }

    /// <summary>
    /// The USB vendor id and the Bluetooth address of a gamepad's HID node on Bluetooth LE: the BTHLEDEVICE node of the HID service
    /// (00001812) "..._DEV_VID&amp;020B05_PID&amp;1ABD_REV&amp;0100_C15AF9554702" or the HID child "HID\{00001812-...}&amp;DEV&amp;VID_0B05&amp;PID_1ABD&amp;REV_0100&amp;C15AF9554702&amp;IG_00"
    /// (IG_ marks an XInput-compatible gamepad). Null for anything else (the battery service node, other devices, bad ids).
    /// </summary>
    public static (int Vendor, int Product, string Address)? HidPadOfInstance(string? instanceId)
    {
        if (instanceId is null || instanceId.Length > 300 || !instanceId.Contains("00001812-", StringComparison.OrdinalIgnoreCase)) return null;
        var timeout = TimeSpan.FromMilliseconds(50);
        var m = System.Text.RegularExpressions.Regex.Match(instanceId, @"^BTHLEDEVICE\\\{00001812-[0-9A-Fa-f-]+\}_DEV_VID&02(?<v>[0-9A-Fa-f]{4})_PID&(?<p>[0-9A-Fa-f]{4})_REV&[0-9A-Fa-f]{4}_(?<a>[0-9A-Fa-f]{12})(\\|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase, timeout);
        if (!m.Success)
            m = System.Text.RegularExpressions.Regex.Match(instanceId, @"^HID\\\{00001812-[0-9A-Fa-f-]+\}&DEV&VID_(?<v>[0-9A-Fa-f]{4})&PID_(?<p>[0-9A-Fa-f]{4})&REV_[0-9A-Fa-f]{4}&(?<a>[0-9A-Fa-f]{12})&IG_[0-9A-Fa-f]{2}(\\|$)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase, timeout);
        return m.Success ? (Convert.ToInt32(m.Groups["v"].Value, 16), Convert.ToInt32(m.Groups["p"].Value, 16), m.Groups["a"].Value.ToUpperInvariant()) : null;
    }

    private static bool XboxName(string name) => name.TrimStart().StartsWith("Xbox", StringComparison.OrdinalIgnoreCase);

    private static bool SonyName(string name) =>
        name.Contains("Wireless Controller", StringComparison.OrdinalIgnoreCase) || name.Contains("DualSense", StringComparison.OrdinalIgnoreCase)
        || name.Contains("DualShock", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Windows knows the real battery of a Bluetooth controller (DEVPKEY_Bluetooth_Battery); XInput only a coarse level. A Bluetooth
    /// controller's percent goes to an XInput pad only when the two clearly belong together: the pad (slot 0 to 3, no percent yet, not
    /// wired, not Steam's virtual pad, vendor known) and the device (kind "controller") must be each other's only possible partner. A
    /// Microsoft pad can only be an "Xbox ..." device; any other vendor's pad only a device that is neither named "Xbox ..." nor looks
    /// like a PlayStation pad (those bring their own battery). Two candidates on either side: nothing is matched. A matched device
    /// leaves the device list (the pad carries its percent, so it is shown and warned about once). The pad's level becomes
    /// <see cref="PercentLevel"/> of the percent.
    /// </summary>
    public static (IReadOnlyList<PadBattery> Pads, IReadOnlyList<DeviceBattery> Devices) MatchBluetooth(IReadOnlyList<PadBattery> pads, IReadOnlyList<DeviceBattery> devices,
        IReadOnlyDictionary<int, IReadOnlyList<string>>? hidAddresses = null)
    {
        var candidates = pads.Where(p => p.Index is >= 0 and < 4 && p.Percent is null && p.Level != "wired" && p.Vendor is not null and not SteamVendor).ToList();
        var controllers = devices.Where(d => d.Kind == "controller").ToList();
        if (candidates.Count == 0 || controllers.Count == 0) return (pads, devices);
        // Exact: the pad's HID node of that vendor and product sits on exactly one Bluetooth address (hidAddresses), and that connected device is the
        // one. A non-Microsoft pad is matched only that way (a name proves nothing about which pad it is); a Microsoft pad also by its
        // "Xbox ..." name when no address is known.
        bool Exact(PadBattery p, out IReadOnlyList<string> addresses)
        {
            addresses = [];
            return hidAddresses is not null && p.Vendor is { } v && p.Product is { } pr && hidAddresses.TryGetValue(PadKey(v, pr), out addresses!) && addresses.Count > 0;
        }
        bool Fits(PadBattery p, DeviceBattery d)
        {
            if (Exact(p, out var addresses)) return addresses.Count == 1 && d.Address is not null && string.Equals(d.Address, addresses[0], StringComparison.OrdinalIgnoreCase);
            return p.Vendor == MicrosoftVendor && XboxName(d.Name);
        }
        var matched = new Dictionary<PadBattery, DeviceBattery>();
        foreach (var p in candidates)
        {
            var fits = controllers.Where(d => Fits(p, d)).ToList();
            if (fits.Count != 1) continue;
            if (candidates.Count(c => Fits(c, fits[0])) != 1) continue;
            matched[p] = fits[0];
        }
        if (matched.Count == 0) return (pads, devices);
        var newPads = pads.Select(p => matched.TryGetValue(p, out var d) ? p with { Level = PercentLevel(d.Percent), Percent = d.Percent, Source = BatterySources.Bluetooth } : p).ToList();
        var used = matched.Values.Select(d => d.Id).ToHashSet();
        return (newPads, devices.Where(d => !used.Contains(d.Id)).ToList());
    }

    /// <summary>
    /// The pads as they are connected right now (<paramref name="live"/>, so an unplugged pad is gone at once) with the level and percent of
    /// the battery snapshot for a pad that is in it (a Bluetooth percent that was matched to it).
    /// </summary>
    public static IReadOnlyList<PadBattery> LiveWithSnapshot(IReadOnlyList<PadBattery> live, IReadOnlyList<PadBattery> snapshot) =>
        live.Select(p => snapshot.FirstOrDefault(x => x.Index == p.Index) is { } known ? p with { Level = known.Level, Percent = known.Percent, Source = known.Source } : p).ToList();

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
/// One log line per pad when the source or the value of its battery changes (the next report then says where a number came from).
/// Only the slot, the source name and the level or percent: no device names. Not thread-safe: one caller (the battery monitor's poll).
/// </summary>
public sealed class BatterySourceLog
{
    private readonly Dictionary<int, string> _last = [];

    /// <summary>The line of a pad: "[battery] pad 0 source=wgi 87%", "source=xinput level=full", "source=none".</summary>
    public static string Describe(PadBattery p)
    {
        if (p.Percent is { } percent) return $"[battery] pad {p.Index} source={p.Source ?? "unknown"} {percent}%";
        var source = p.Source ?? (Steps(p.Level) is not null || p.Level == "wired" ? BatterySources.XInput : BatterySources.None);
        return source == BatterySources.None ? $"[battery] pad {p.Index} source=none" : $"[battery] pad {p.Index} source={source} level={p.Level}";
    }

    /// <summary>The lines for pads whose line changed since the last call; a pad that went away is forgotten (it logs again when it returns).</summary>
    public IReadOnlyList<string> Update(IReadOnlyList<PadBattery> pads)
    {
        var lines = new List<string>();
        foreach (var p in pads)
        {
            var line = Describe(p);
            if (_last.TryGetValue(p.Index, out var old) && old == line) continue;
            _last[p.Index] = line;
            lines.Add(line);
        }
        foreach (var gone in _last.Keys.Where(k => pads.All(p => p.Index != k)).ToList()) _last.Remove(gone);
        return lines;
    }

    private static int? Steps(string level) => BatteryRules.Steps(level);
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
