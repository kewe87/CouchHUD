using CouchHud.Core.Input;
using Xunit;

namespace CouchHud.Core.Tests;

public class PlayStationReportTests
{
    private const ushort Up = 0x0001, Down = 0x0002, Left = 0x0004, Right = 0x0008, Start = 0x0010, Back = 0x0020,
        Ls = 0x0040, Rs = 0x0080, Lb = 0x0100, Rb = 0x0200, Guide = 0x0400, A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;

    private static byte[] Hex(string hex, int padTo = 0)
    {
        var bytes = Convert.FromHexString(hex.Replace(" ", ""));
        if (padTo <= bytes.Length) return bytes;
        var padded = new byte[padTo];
        bytes.CopyTo(padded, 0);
        return padded;
    }

    // DualShock 4 (CUH-ZCT2) over USB, report 0x01, 64 bytes as ReadFile returns it: sticks, Cross + d-pad up, Options, PS,
    // L2 fully pressed, gyro/accel/touch bytes filled (ignored), status 0x08 at byte 30 (on battery, 8 tenths).
    private const string Ds4Usb =
        "01 00 00 80 ff 20 20 01 ff 00 a1 2b 15 fe ff 03 00 f0 ff 1e 07 2c 20 ec 01 00 00 00 00 00" +
        "08 00 00 00 00 80 00 00 00 80 00 00 00 00 80 00 00 00 00 80 00 00 00 00 00 00 00 00 00 00 00 00 00 00";

    // DualShock 4 over Bluetooth, full report 0x11 (two header bytes), padded to the 547 bytes Windows declares; Square + Circle,
    // d-pad down-left (hat 5), L1, R3, Share; status 0x1b at byte 32: on the cable.
    private const string Ds4Bt =
        "11 c0 00 80 80 80 80 55 91 80 00 00 6a 2d 1b 02 00 fa ff 01 00 3e 02 b5 1f 9e 07 00 00 00 00 00" +
        "1b 00 00 01 00 00 80 00 00 00 80 00 00 00 00 80 00 00 00 00 80 00 00 00 00 00 00 00 00 00 00 00" +
        "00 00 00 00 00 00 00 00 00 00 a7 4c 2f 91";

    // DualShock 4 over Bluetooth before anything asked for the full report: short 0x01, d-pad released, Triangle, R1.
    private const string Ds4BtShort = "01 80 80 80 80 88 02 04 00 00";

    // DualSense over USB, report 0x01: right stick up, triggers, seq, Triangle (hat 8 = released), Create, touchpad click,
    // gyro/accel/touch (ignored); status 0x03 at byte 53: on battery, 3 tenths.
    private const string DualSenseUsb =
        "01 80 80 80 00 10 e0 2a 88 10 02 00 31 a0 4b 3c ff ff 06 00 f4 ff 1a 00 6c 1f 4e 07 10 3e 8f 00" +
        "0b 7f 21 6a 11 80 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 03 00 00 00 00 00 00 00 00 00 00";

    // DualSense over Bluetooth, full report 0x31 (one tag byte), 78 bytes: Cross, Options, L3, PS, d-pad right; status 0x21: full.
    private const string DualSenseBt =
        "31 10 7f 81 80 80 00 00 07 22 60 01 00 a3 87 10 3a 02 00 fc ff fb ff 23 00 8a 1f 21 08 33 52 0c" +
        "00 2e 8b 00 31 40 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 21 00 00 00 00 00 00 00 00 00" +
        "00 00 00 00 00 00 00 00 00 00 5c 2f 00 00";

    // DualSense over Bluetooth in its simple mode: report 0x01 laid out like the DS4's, padded to 78. Circle, Options, PS.
    private const string DualSenseBtSimple = "01 80 80 80 80 48 20 01 00 00";

    [Fact]
    public void DualShock4Usb_MapsButtonsSticksTriggersAndBattery()
    {
        var s = PlayStationReport.Parse(PlayStationModel.DualShock4, false, Hex(Ds4Usb));
        Assert.NotNull(s);
        var (sample, battery) = s.Value;
        Assert.Equal(A | Up | Start | Guide, sample.Buttons);
        Assert.Equal(-32768, sample.LX);
        Assert.Equal(32512, sample.LY); // 0 is up on the pad, positive is up in XInput
        Assert.Equal(-32768 + 0x80 * 256, sample.RX);
        Assert.Equal(-32768, sample.RY);
        Assert.Equal(255, sample.LeftTrigger);
        Assert.Equal(0, sample.RightTrigger);
        Assert.Equal("full", battery);
        Assert.Equal(["a", "guide", "left", "lt", "start", "up"], PadMerger.ToPressed(sample).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void DualShock4Bluetooth_FullReport_OnTheCable()
    {
        var s = PlayStationReport.Parse(PlayStationModel.DualShock4, true, Hex(Ds4Bt, 547));
        Assert.NotNull(s);
        Assert.Equal(X | B | Down | Left | Lb | Rs | Back, s.Value.Sample.Buttons);
        Assert.Equal("wired", s.Value.Battery);
    }

    [Fact]
    public void DualShock4Bluetooth_ShortReport_HasButtonsButNoBattery()
    {
        var s = PlayStationReport.Parse(PlayStationModel.DualShock4, true, Hex(Ds4BtShort, 547));
        Assert.NotNull(s);
        Assert.Equal(Y | Rb, s.Value.Sample.Buttons);
        Assert.Equal(0, s.Value.Sample.LX);
        Assert.Equal(-256, s.Value.Sample.LY);
        Assert.Equal("unknown", s.Value.Battery);
    }

    [Fact]
    public void DualSenseUsb_MapsCreateAndTouchpadToBack()
    {
        var s = PlayStationReport.Parse(PlayStationModel.DualSense, false, Hex(DualSenseUsb));
        Assert.NotNull(s);
        var (sample, battery) = s.Value;
        Assert.Equal(Y | Back, sample.Buttons);
        Assert.Equal(32512, sample.RY);
        Assert.Equal(0x10, sample.LeftTrigger);
        Assert.Equal(0xE0, sample.RightTrigger);
        Assert.Equal("medium", battery);
    }

    [Fact]
    public void DualSenseBluetooth_FullReport()
    {
        var s = PlayStationReport.Parse(PlayStationModel.DualSense, true, Hex(DualSenseBt));
        Assert.NotNull(s);
        Assert.Equal(A | Right | Start | Ls | Guide, s.Value.Sample.Buttons);
        Assert.Equal(-256, s.Value.Sample.LX);
        Assert.Equal("wired", s.Value.Battery);
    }

    [Fact]
    public void DualSenseBluetooth_SimpleReport()
    {
        var s = PlayStationReport.Parse(PlayStationModel.DualSense, true, Hex(DualSenseBtSimple, 78));
        Assert.NotNull(s);
        Assert.Equal(B | Start | Guide, s.Value.Sample.Buttons);
        Assert.Equal("unknown", s.Value.Battery);
    }

    [Theory]
    [InlineData(0, Up)]
    [InlineData(1, Up | Right)]
    [InlineData(2, Right)]
    [InlineData(3, Down | Right)]
    [InlineData(4, Down)]
    [InlineData(5, Down | Left)]
    [InlineData(6, Left)]
    [InlineData(7, Up | Left)]
    [InlineData(8, 0)]
    [InlineData(15, 0)]
    public void Hat_IsTheFourDirections(int hat, int expected) =>
        Assert.Equal((ushort)expected, PlayStationReport.Buttons((byte)hat, 0, 0));

    [Fact]
    public void Mapping_IsTheXInputLayout()
    {
        Assert.Equal(X, PlayStationReport.Buttons(0x18, 0, 0));        // Square
        Assert.Equal(A, PlayStationReport.Buttons(0x28, 0, 0));        // Cross
        Assert.Equal(B, PlayStationReport.Buttons(0x48, 0, 0));        // Circle
        Assert.Equal(Y, PlayStationReport.Buttons(0x88, 0, 0));        // Triangle
        Assert.Equal(Lb, PlayStationReport.Buttons(0x08, 0x01, 0));    // L1
        Assert.Equal(Rb, PlayStationReport.Buttons(0x08, 0x02, 0));    // R1
        Assert.Equal(0, PlayStationReport.Buttons(0x08, 0x0C, 0));     // L2/R2 digital bits: the analog triggers count
        Assert.Equal(Back, PlayStationReport.Buttons(0x08, 0x10, 0));  // Share / Create
        Assert.Equal(Start, PlayStationReport.Buttons(0x08, 0x20, 0)); // Options
        Assert.Equal(Ls, PlayStationReport.Buttons(0x08, 0x40, 0));    // L3
        Assert.Equal(Rs, PlayStationReport.Buttons(0x08, 0x80, 0));    // R3
        Assert.Equal(Guide, PlayStationReport.Buttons(0x08, 0, 0x01)); // PS
        Assert.Equal(Back, PlayStationReport.Buttons(0x08, 0, 0x02));  // touchpad click
        Assert.Equal(0, PlayStationReport.Buttons(0x08, 0, 0xFC));     // mic mute and the report counter: nothing
    }

    [Fact]
    public void PsAndTriangle_IsTheScreenshotChord()
    {
        var sample = PlayStationReport.Parse(PlayStationModel.DualSense, true, Hex("01 80 80 80 80 88 00 01 00 00"))!.Value.Sample;
        var pressed = PadMerger.ToPressed(sample);
        Assert.Contains("guide", pressed);
        Assert.Contains("y", pressed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("01")]
    [InlineData("01 80 80 80 80 08 00 00 00")]          // one byte short
    [InlineData("05 80 80 80 80 08 00 00 00 00 00")]    // an output/feature id, not input
    [InlineData("11 c0 00 80 80 80 80 08 00 00 00 00")] // 0x11 shorter than 78
    public void Malformed_GivesNull(string hex)
    {
        var bytes = Hex(hex);
        Assert.Null(PlayStationReport.Parse(PlayStationModel.DualShock4, true, bytes));
        Assert.Null(PlayStationReport.Parse(PlayStationModel.DualSense, true, bytes));
        Assert.Null(PlayStationReport.Parse(PlayStationModel.DualSense, false, bytes));
    }

    [Fact]
    public void WrongTransport_GivesNull()
    {
        // A Bluetooth-only report id on USB, and a short 0x01 on USB (USB reports are always 64 bytes).
        Assert.Null(PlayStationReport.Parse(PlayStationModel.DualShock4, false, Hex(Ds4Bt, 547)));
        Assert.Null(PlayStationReport.Parse(PlayStationModel.DualSense, false, Hex(DualSenseBt)));
        Assert.Null(PlayStationReport.Parse(PlayStationModel.DualShock4, false, Hex(Ds4BtShort)));
        Assert.Null(PlayStationReport.Parse(PlayStationModel.None, false, Hex(Ds4Usb)));
    }

    [Fact]
    public void Oversized_GivesNull()
    {
        var big = new byte[PlayStationReport.MaxReportLength + 1];
        big[0] = 0x01;
        Assert.Null(PlayStationReport.Parse(PlayStationModel.DualShock4, false, big));
    }

    [Fact]
    public void RandomBytes_NeverThrow()
    {
        var random = new Random(4711);
        for (var i = 0; i < 20000; i++)
        {
            var bytes = new byte[random.Next(0, 600)];
            random.NextBytes(bytes);
            if (bytes.Length > 0 && random.Next(2) == 0) bytes[0] = (byte)(random.Next(2) == 0 ? 0x01 : random.Next(2) == 0 ? 0x11 : 0x31);
            foreach (var model in new[] { PlayStationModel.DualShock4, PlayStationModel.DualSense })
            {
                var s = PlayStationReport.Parse(model, random.Next(2) == 0, bytes);
                if (s is { } state) Assert.Contains(state.Battery, new[] { "wired", "full", "medium", "low", "empty", "unknown" });
            }
        }
    }

    [Theory]
    [InlineData(0x054C, 0x05C4, PlayStationModel.DualShock4)]
    [InlineData(0x054C, 0x09CC, PlayStationModel.DualShock4)]
    [InlineData(0x054C, 0x0BA0, PlayStationModel.DualShock4)]
    [InlineData(0x054C, 0x0CE6, PlayStationModel.DualSense)]
    [InlineData(0x054C, 0x0DF2, PlayStationModel.DualSense)]
    [InlineData(0x054C, 0x0268, PlayStationModel.None)] // DualShock 3: not read
    [InlineData(0x045E, 0x0B13, PlayStationModel.None)] // Xbox Wireless Controller
    [InlineData(0x28DE, 0x05C4, PlayStationModel.None)] // a Valve id with a Sony product id
    public void Model_ByVendorAndProduct(int vendor, int product, PlayStationModel expected) =>
        Assert.Equal(expected, PlayStationReport.Model((ushort)vendor, (ushort)product));

    [Fact]
    public void Bluetooth_ByPathOrReportLength()
    {
        Assert.True(PlayStationReport.IsBluetooth(@"\\?\hid#{00001124-0000-1000-8000-00805f9b34fb}_vid&0002054c_pid&09cc#8&1&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", 64));
        Assert.True(PlayStationReport.IsBluetooth(@"\\?\hid#whatever", 547));
        Assert.True(PlayStationReport.IsBluetooth(null, 78));
        Assert.False(PlayStationReport.IsBluetooth(@"\\?\hid#vid_054c&pid_0ce6&mi_03#7&2b0&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", 64));
    }

    [Theory]
    [InlineData(0x00, "empty")]
    [InlineData(0x01, "low")]
    [InlineData(0x05, "medium")]
    [InlineData(0x06, "full")]
    [InlineData(0x0A, "full")]
    [InlineData(0x0B, "unknown")]
    [InlineData(0x10, "wired")]
    [InlineData(0x1B, "wired")]
    public void DualShock4Battery_Levels(int status, string expected) =>
        Assert.Equal(expected, PlayStationReport.DualShock4Battery((byte)status));

    [Theory]
    [InlineData(0x00, "empty")]
    [InlineData(0x01, "low")]
    [InlineData(0x04, "medium")]
    [InlineData(0x08, "full")]
    [InlineData(0x0F, "unknown")]
    [InlineData(0x15, "wired")]
    [InlineData(0x2A, "wired")]
    [InlineData(0xA0, "unknown")]
    [InlineData(0xF5, "unknown")]
    public void DualSenseBattery_Levels(int status, string expected) =>
        Assert.Equal(expected, PlayStationReport.DualSenseBattery((byte)status));

    [Fact]
    public void BatteryLevels_FeedTheExistingLowWarning()
    {
        Assert.True(BatteryRules.PadLow(PlayStationReport.DualSenseBattery(0x01)));
        Assert.False(BatteryRules.PadLow(PlayStationReport.DualShock4Battery(0x18)));
    }
}

public class PlayStationMergeTests
{
    private const ushort A = 0x1000, Guide = 0x0400, Y = 0x8000;

    private static PadSample? P(ushort buttons = 0) => new PadSample(buttons, 0, 0, 0, 0, 0, 0);

    private static string Ev(IReadOnlyList<(string Button, bool Down)> e) => string.Join(",", e.Select(x => (x.Down ? "+" : "-") + x.Button));

    // Slots as the poller fills them: 0..3 XInput, 4..7 PlayStation over HID.
    private static PadSample?[] Slots(PadSample? xinput0 = null, PadSample? hid4 = null)
    {
        var slots = new PadSample?[8];
        slots[0] = xinput0;
        slots[4] = hid4;
        return slots;
    }

    [Fact]
    public void SteamMirrorOfAHidPad_GivesEveryPressOnce_EvenWithLag()
    {
        var m = new PadMerger();
        Assert.Empty(m.Update(Slots(P(), P())));
        // The HID pad sees Cross first, Steam's virtual XInput pad one poll later; released in the same order.
        Assert.Equal("+a", Ev(m.Update(Slots(P(), P(A)))));
        Assert.Empty(m.Update(Slots(P(A), P(A))));
        Assert.Empty(m.Update(Slots(P(A), P())));
        Assert.Equal("-a", Ev(m.Update(Slots(P(), P()))));
    }

    [Fact]
    public void MirrorReportsTheSameChangeOnce()
    {
        var m = new PadMerger();
        var mirrors = new List<(int, int)>();
        m.MirrorDetected += (a, b) => mirrors.Add((a, b));
        m.Update(Slots(P(), P()));
        Assert.Equal("+guide", Ev(m.Update(Slots(P(Guide), P(Guide)))));
        Assert.Equal([(0, 4)], mirrors);
        Assert.Equal("+y", Ev(m.Update(Slots(P(Guide | Y), P(Guide | Y)))));
        Assert.True(m.IsPressed("guide") && m.IsPressed("y"));
    }

    [Fact]
    public void PressedSlots_NameWhereANewPressHappened()
    {
        var m = new PadMerger();
        m.Update(Slots(P(), P()));
        m.Update(Slots(P(), P(A)));
        Assert.Equal([4], m.PressedSlots);
        m.Update(Slots(P(A), P(A)));
        Assert.Equal([0], m.PressedSlots);
        m.Update(Slots(P(A), P(A)));
        Assert.Empty(m.PressedSlots);
    }
}

public class PadKindTrackerTests
{
    [Fact]
    public void FollowsThePadPressedLast_IgnoringTheMirror()
    {
        long now = 1000;
        var k = new PadKindTracker(() => now);
        Assert.Equal(PadKindTracker.Xbox, k.Current);
        Assert.False(k.Pressed(false, false));
        Assert.True(k.Pressed(true, false));
        Assert.Equal(PadKindTracker.PlayStation, k.Current);
        // Steam's virtual XInput pad repeats the press a moment later: still PlayStation.
        now += 40;
        Assert.False(k.Pressed(false, true));
        now += PadKindTracker.MirrorWindowMs;
        Assert.False(k.Pressed(true, true));
        // A real Xbox pad pressed well after the last PlayStation press switches back.
        now += PadKindTracker.MirrorWindowMs + 1;
        Assert.True(k.Pressed(false, true));
        Assert.Equal(PadKindTracker.Xbox, k.Current);
    }
}

public class PlayStationEmptyReportTests
{
    // The DS4 wireless adapter with a pad: a normal USB report (byte 31 without bit 2).
    private static byte[] DongleWithPad()
    {
        var r = new byte[64];
        r[0] = 0x01;
        r[1] = r[2] = r[3] = r[4] = 0x80;
        r[5] = 0x08; // hat released
        r[30] = 0x08;
        return r;
    }

    [Fact]
    public void DongleWithoutPad_IsNoPad()
    {
        // No pad paired: zeroed sticks and hat 0 (would be "up" and "left") with bit 2 of byte 31 set.
        var r = new byte[64];
        r[0] = 0x01;
        r[31] = 0x04;
        Assert.True(PlayStationReport.IsEmpty(true, r));
        Assert.Null(PlayStationReport.Parse(PlayStationModel.DualShock4, false, r, dongle: true));
        // Even when some other bytes are not zero.
        r[30] = 0x05;
        r[10] = 0x33;
        Assert.Null(PlayStationReport.Parse(PlayStationModel.DualShock4, false, r, dongle: true));
    }

    [Fact]
    public void DongleWithPad_Parses()
    {
        var r = DongleWithPad();
        Assert.False(PlayStationReport.IsEmpty(true, r));
        var s = PlayStationReport.Parse(PlayStationModel.DualShock4, false, r, dongle: true);
        Assert.NotNull(s);
        Assert.Empty(PadMerger.ToPressed(s!.Value.Sample));
        Assert.Equal("full", s.Value.Battery);
    }

    [Fact]
    public void Bit2OfByte31_OnlyMeansNoPadOnTheDongle()
    {
        var r = DongleWithPad();
        r[31] = 0x04; // a touch byte on a cabled pad
        Assert.False(PlayStationReport.IsEmpty(false, r));
        Assert.NotNull(PlayStationReport.Parse(PlayStationModel.DualShock4, false, r));
    }

    [Theory]
    [InlineData(64, false)]
    [InlineData(78, true)]
    [InlineData(10, true)]
    public void AllZeroPayload_IsNoPad(int length, bool bluetooth)
    {
        foreach (var id in new byte[] { 0x01, 0x11, 0x31 })
        {
            var r = new byte[length];
            r[0] = id;
            Assert.True(PlayStationReport.IsEmpty(false, r));
            Assert.Null(PlayStationReport.Parse(PlayStationModel.DualShock4, bluetooth, r));
            Assert.Null(PlayStationReport.Parse(PlayStationModel.DualSense, bluetooth, r));
        }
    }

    [Theory]
    [InlineData(@"\\?\hid#vid_054c&pid_09cc&mi_03#8&2a2e9a4b&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", true)]
    [InlineData(@"\\?\HID#{00001124-0000-1000-8000-00805F9B34FB}_VID&0002054C_PID&0CE6#9&1d6b3c&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}", true)]
    [InlineData(@"\\?\hid#vid_045e&pid_0b13&ig_00#8&1#{4d1e55b2-f16f-11cf-88cb-001111000030}", false)]
    [InlineData(@"\\?\hid#vid_046d&pid_c52b&mi_02&col01#8&1#{4d1e55b2-f16f-11cf-88cb-001111000030}", false)]
    [InlineData(null, false)]
    public void OnlySonyPathsAreProbed(string? path, bool sony) => Assert.Equal(sony, PlayStationReport.IsSonyPath(path));
}
