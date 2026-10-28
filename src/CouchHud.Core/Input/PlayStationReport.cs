namespace CouchHud.Core.Input;

/// <summary>The PlayStation pads CouchHUD reads natively (HID, read-only).</summary>
public enum PlayStationModel
{
    None,
    DualShock4,
    DualSense
}

/// <summary>One parsed input report: the pad as an XInput-shaped sample and its battery level (the XInput level names).</summary>
public readonly record struct PlayStationState(PadSample Sample, string Battery);

/// <summary>
/// Turns DualShock 4 and DualSense HID input reports into the same logical pad as XInput: Cross = A, Circle = B, Square = X,
/// Triangle = Y, Options = Start, Share/Create = Back, the touchpad click = Back too, PS = Guide, L1/R1 = LB/RB, L3/R3 = LS/RS,
/// L2/R2 as analog triggers, the d-pad hat as the four directions. Only the bytes for buttons, sticks, triggers and battery are
/// read; gyro, accelerometer and touch positions are never looked at. Any report that is too short, has an unknown id or does
/// not fit the transport gives null: no exception, no allocation beyond the result.
/// </summary>
public static class PlayStationReport
{
    public const ushort SonyVendorId = 0x054C;

    /// <summary>A device that declares longer input reports than this is not read at all (the DS4 over Bluetooth declares 547).</summary>
    public const int MaxReportLength = 1024;

    /// <summary>The model of a HID device by vendor and product id; None for anything that is not a known Sony pad.</summary>
    public static PlayStationModel Model(ushort vendorId, ushort productId) => vendorId != SonyVendorId
        ? PlayStationModel.None
        : productId switch
        {
            0x05C4 or 0x09CC or 0x0BA0 => PlayStationModel.DualShock4, // DS4 v1, DS4 v2, the DS4 USB wireless adapter
            0x0CE6 or 0x0DF2 => PlayStationModel.DualSense,           // DualSense, DualSense Edge
            _ => PlayStationModel.None
        };

    /// <summary>
    /// Whether a HID device is connected over Bluetooth: its path names the Bluetooth HID service, or its input reports are longer
    /// than the 64 bytes of USB (DS4: 547, DualSense: 78).
    /// </summary>
    public static bool IsBluetooth(string? devicePath, int inputReportLength) =>
        inputReportLength > 64 || (devicePath?.Contains("{00001124-0000-1000-8000-00805f9b34fb}", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// Whether a HID device path names Sony's vendor id (USB "vid_054c", Bluetooth "vid&amp;0002054c"), so that no other HID device
    /// (keyboard, mouse, touchpad) is opened for a probe.
    /// </summary>
    public static bool IsSonyPath(string? devicePath) => devicePath is not null
        && (devicePath.Contains("vid_054c", StringComparison.OrdinalIgnoreCase) || devicePath.Contains("&0002054c", StringComparison.OrdinalIgnoreCase));

    /// <summary>The DS4 USB wireless adapter: it stays connected without a pad and then sends empty reports.</summary>
    public const ushort DualShock4DongleProductId = 0x0BA0;

    /// <summary>
    /// Whether a report says "no pad here": the DS4 wireless adapter without a paired pad (bit 2 of byte 31, as hid-sony and SDL
    /// read it), or a report whose data is all zero (a real pad always has its sticks near the middle, 0x80).
    /// </summary>
    public static bool IsEmpty(bool dongle, ReadOnlySpan<byte> report)
    {
        if (dongle && report.Length > 31 && report[0] == 0x01 && (report[31] & 0x04) != 0) return true;
        if (report.Length < 2) return true;
        var data = report[1..Math.Min(report.Length, 64)];
        return !data.ContainsAnyExcept((byte)0);
    }

    /// <summary>
    /// Parses one input report as Windows' ReadFile returns it (report id first, padded to the declared length);
    /// <paramref name="dongle"/> for the DS4 wireless adapter (<see cref="DualShock4DongleProductId"/>). Null for an empty report
    /// (<see cref="IsEmpty"/>) as for one that does not fit.
    /// </summary>
    public static PlayStationState? Parse(PlayStationModel model, bool bluetooth, ReadOnlySpan<byte> report, bool dongle = false)
    {
        if (report.Length < 10 || report.Length > MaxReportLength) return null;
        if (IsEmpty(dongle, report)) return null;
        var id = report[0];
        return model switch
        {
            PlayStationModel.DualShock4 => ParseDualShock4(id, bluetooth, report),
            PlayStationModel.DualSense => ParseDualSense(id, bluetooth, report),
            _ => null
        };
    }

    // hid-playstation's dualshock4_input_report_common: x y rx ry buttons[3] z rz ... status[0] at +29.
    private static PlayStationState? ParseDualShock4(byte id, bool bluetooth, ReadOnlySpan<byte> r)
    {
        if (id == 0x01 && !bluetooth && r.Length >= 64)
            return new(Sample(r[1], r[2], r[3], r[4], r[5], r[6], r[7], r[8], r[9]), DualShock4Battery(r[30]));
        // Bluetooth before anyone asked for the full report: the same first bytes, no battery.
        if (id == 0x01 && bluetooth)
            return new(Sample(r[1], r[2], r[3], r[4], r[5], r[6], r[7], r[8], r[9]), "unknown");
        // Bluetooth full report (after Steam, a game or the driver switched the pad over): two more header bytes.
        if (id == 0x11 && bluetooth && r.Length >= 78)
            return new(Sample(r[3], r[4], r[5], r[6], r[7], r[8], r[9], r[10], r[11]), DualShock4Battery(r[32]));
        return null;
    }

    // hid-playstation's dualsense_input_report: x y rx ry z rz seq buttons[4] ... status at +52.
    private static PlayStationState? ParseDualSense(byte id, bool bluetooth, ReadOnlySpan<byte> r)
    {
        if (id == 0x01 && !bluetooth && r.Length >= 64)
            return new(Sample(r[1], r[2], r[3], r[4], r[8], r[9], r[10], r[5], r[6]), DualSenseBattery(r[53]));
        // Bluetooth simple report: laid out like the DualShock 4's, no battery.
        if (id == 0x01 && bluetooth)
            return new(Sample(r[1], r[2], r[3], r[4], r[5], r[6], r[7], r[8], r[9]), "unknown");
        if (id == 0x31 && bluetooth && r.Length >= 78)
            return new(Sample(r[2], r[3], r[4], r[5], r[9], r[10], r[11], r[6], r[7]), DualSenseBattery(r[54]));
        return null;
    }

    private const ushort Up = 0x0001, Down = 0x0002, Left = 0x0004, Right = 0x0008, Start = 0x0010, Back = 0x0020,
        LeftThumb = 0x0040, RightThumb = 0x0080, LeftShoulder = 0x0100, RightShoulder = 0x0200, Guide = 0x0400,
        A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;

    // The hat: 0 = north, clockwise in steps of 45°, 8 and above = released.
    private static readonly ushort[] Hat = [Up, Up | Right, Right, Down | Right, Down, Down | Left, Left, Up | Left];

    /// <summary>The three button bytes the DS4 and the DualSense share, as XInput button bits.</summary>
    public static ushort Buttons(byte b0, byte b1, byte b2)
    {
        var hat = b0 & 0x0F;
        var buttons = hat < Hat.Length ? Hat[hat] : (ushort)0;
        if ((b0 & 0x10) != 0) buttons |= X;             // Square
        if ((b0 & 0x20) != 0) buttons |= A;             // Cross
        if ((b0 & 0x40) != 0) buttons |= B;             // Circle
        if ((b0 & 0x80) != 0) buttons |= Y;             // Triangle
        if ((b1 & 0x01) != 0) buttons |= LeftShoulder;  // L1
        if ((b1 & 0x02) != 0) buttons |= RightShoulder; // R1
        if ((b1 & 0x10) != 0) buttons |= Back;          // Share / Create
        if ((b1 & 0x20) != 0) buttons |= Start;         // Options
        if ((b1 & 0x40) != 0) buttons |= LeftThumb;     // L3
        if ((b1 & 0x80) != 0) buttons |= RightThumb;    // R3
        if ((b2 & 0x01) != 0) buttons |= Guide;         // PS
        if ((b2 & 0x02) != 0) buttons |= Back;          // touchpad click
        return buttons;
    }

    // 0..255 with 128 in the middle and 0 at the top, to XInput's -32768..32767 with up positive.
    private static short Axis(byte v) => (short)((v - 128) << 8);
    private static short AxisInverted(byte v) => (short)((127 - v) << 8);

    private static PadSample Sample(byte lx, byte ly, byte rx, byte ry, byte b0, byte b1, byte b2, byte l2, byte r2) =>
        new(Buttons(b0, b1, b2), l2, r2, Axis(lx), AxisInverted(ly), Axis(rx), AxisInverted(ry));

    /// <summary>
    /// The DS4's status byte: the low nibble is the charge (0..10, 11 = full on the cable), bit 4 the cable. On the cable the pad
    /// counts as "wired" like an XInput pad; otherwise the charge as the four XInput levels.
    /// </summary>
    public static string DualShock4Battery(byte status)
    {
        if ((status & 0x10) != 0) return "wired";
        return Level(status & 0x0F);
    }

    /// <summary>
    /// The DualSense's status byte: the low nibble the charge (0..10), the high nibble the state (0 = on battery, 1 = charging,
    /// 2 = full; anything else is an error). Charging or full means a cable: "wired".
    /// </summary>
    public static string DualSenseBattery(byte status) => (status >> 4) switch
    {
        0 => Level(status & 0x0F),
        1 or 2 => "wired",
        _ => "unknown"
    };

    // Charge in tenths (0 = below 10 %) to the XInput levels; 1 is about 15 %, the low warning.
    private static string Level(int tenths) => tenths switch
    {
        0 => "empty",
        1 => "low",
        >= 2 and <= 5 => "medium",
        >= 6 and <= 10 => "full",
        _ => "unknown"
    };
}

/// <summary>
/// Which kind of pad was used last, for the button symbols on screen: a new press on a PlayStation pad switches to
/// "playstation", one on an XInput pad to "xbox". Steam Input and DS4Windows mirror a PlayStation pad as a virtual XInput pad a
/// few milliseconds later; a press on an XInput pad within <see cref="MirrorWindowMs"/> after a PlayStation press is that mirror
/// and does not switch back. Not thread-safe: one caller (the polling thread).
/// </summary>
public sealed class PadKindTracker(Func<long>? clockMs = null)
{
    public const string Xbox = "xbox";
    public const string PlayStation = "playstation";
    public const int MirrorWindowMs = 250;

    private readonly Func<long> _clock = clockMs ?? (() => Environment.TickCount64);
    private long _lastPlayStation = long.MinValue / 2;

    public string Current { get; private set; } = Xbox;

    /// <summary>One poll: whether a PlayStation pad and whether an XInput pad saw a new press. True when the kind changed.</summary>
    public bool Pressed(bool playStation, bool xinput)
    {
        var now = _clock();
        var next = Current;
        if (playStation)
        {
            _lastPlayStation = now;
            next = PlayStation;
        }
        else if (xinput && now - _lastPlayStation > MirrorWindowMs)
        {
            next = Xbox;
        }
        if (next == Current) return false;
        Current = next;
        return true;
    }
}
