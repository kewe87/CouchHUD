using CouchHud.Core.Guard;
using CouchHud.Core.Input;
using Xunit;

namespace CouchHud.Core.Tests;

public class BatteryTests
{
    [Theory]
    [InlineData(0, 128, 255)] // desktop: no system battery
    [InlineData(1, 128, 100)]
    [InlineData(1, 128 | 8, 50)] // "no battery" wins over any other flag
    [InlineData(0, 255, 50)] // Windows does not know
    [InlineData(255, 1, 255)] // percent unknown
    [InlineData(0, 1, 101)] // out of range
    public void No_battery_or_an_unknown_value_shows_nothing(byte ac, byte flag, byte percent)
    {
        Assert.Null(BatteryRules.FromPowerStatus(ac, flag, percent));
    }

    [Fact]
    public void Laptop_battery_percent_charging_and_low()
    {
        Assert.Equal(new SystemBattery(78, false, false, false), BatteryRules.FromPowerStatus(0, 1, 78));
        Assert.Equal(new SystemBattery(0, false, false, true), BatteryRules.FromPowerStatus(0, 4, 0));
        Assert.Equal(new SystemBattery(100, false, true, false), BatteryRules.FromPowerStatus(1, 1, 100));
        // At the threshold it is low; one above it is not.
        Assert.True(BatteryRules.FromPowerStatus(0, 2, BatteryRules.LowPercent)!.Low);
        Assert.False(BatteryRules.FromPowerStatus(0, 2, BatteryRules.LowPercent + 1)!.Low);
        // On the charger or charging: never low.
        var charging = BatteryRules.FromPowerStatus(1, 4 | 8, 5)!;
        Assert.True(charging.Charging);
        Assert.True(charging.PluggedIn);
        Assert.False(charging.Low);
        Assert.False(BatteryRules.FromPowerStatus(1, 4, 5)!.Low);
    }

    [Theory]
    [InlineData(1, 0, "wired")]
    [InlineData(1, 3, "wired")]
    [InlineData(0, 3, "unknown")] // disconnected
    [InlineData(0xFF, 3, "unknown")] // unknown type
    [InlineData(2, 0, "empty")]
    [InlineData(2, 1, "low")]
    [InlineData(3, 2, "medium")]
    [InlineData(2, 3, "full")]
    [InlineData(2, 4, "unknown")] // level out of range
    public void XInput_battery_information_maps_to_a_level(byte type, byte level, string expected)
    {
        Assert.Equal(expected, BatteryRules.PadLevel(type, level));
    }

    [Theory]
    [InlineData("empty", 0)]
    [InlineData("low", 1)]
    [InlineData("medium", 2)]
    [InlineData("full", 3)]
    [InlineData("wired", null)]
    [InlineData("unknown", null)]
    [InlineData("", null)]
    public void Levels_are_four_steps_wired_and_unknown_have_none(string level, int? steps)
    {
        Assert.Equal(steps, BatteryRules.Steps(level));
    }

    [Fact]
    public void Bluetooth_percent_is_a_value_from_0_to_100_or_nothing()
    {
        Assert.Equal(0, BatteryRules.BluetoothPercent((byte)0));
        Assert.Equal(100, BatteryRules.BluetoothPercent((byte)100));
        Assert.Equal(55, BatteryRules.BluetoothPercent(55));
        Assert.Equal(55, BatteryRules.BluetoothPercent((uint)55));
        Assert.Null(BatteryRules.BluetoothPercent((byte)101));
        Assert.Null(BatteryRules.BluetoothPercent((byte)255));
        Assert.Null(BatteryRules.BluetoothPercent(-1));
        Assert.Null(BatteryRules.BluetoothPercent(uint.MaxValue));
        Assert.Null(BatteryRules.BluetoothPercent(null));
        Assert.Null(BatteryRules.BluetoothPercent("50"));
        Assert.Null(BatteryRules.BluetoothPercent(50.0));
    }

    [Theory]
    [InlineData(0, "empty")]
    [InlineData(5, "empty")]
    [InlineData(6, "low")]
    [InlineData(BatteryRules.LowPercent, "low")]
    [InlineData(BatteryRules.LowPercent + 1, "medium")]
    [InlineData(70, "medium")]
    [InlineData(71, "full")]
    [InlineData(100, "full")]
    public void Percent_maps_to_a_level_low_exactly_up_to_the_warning(int percent, string expected)
    {
        Assert.Equal(expected, BatteryRules.PercentLevel(percent));
    }

    [Fact]
    public void Gamepad_battery_report_gives_level_and_percent()
    {
        // Discharging (1) and idle (2): the percent of the full charge, rounded.
        Assert.Equal(new GamepadBattery("medium", 64, false), BatteryRules.FromGamepadReport(640, 1000, 1));
        Assert.Equal(new GamepadBattery("full", 100, false), BatteryRules.FromGamepadReport(1000, 1000, 2));
        Assert.Equal(new GamepadBattery("low", 13, false), BatteryRules.FromGamepadReport(125, 1000, 1));
        Assert.Equal(new GamepadBattery("empty", 0, false), BatteryRules.FromGamepadReport(0, 1000, 1));
        // More than the full charge: full, never above 100.
        Assert.Equal(new GamepadBattery("full", 100, false), BatteryRules.FromGamepadReport(1200, 1000, 1));
        // Charging (3): the percent stays, but on the charger it is never low.
        Assert.Equal(new GamepadBattery("medium", 10, true), BatteryRules.FromGamepadReport(100, 1000, 3));
        Assert.Equal(new GamepadBattery("full", 90, true), BatteryRules.FromGamepadReport(900, 1000, 3));
        // No battery (NotPresent 0): wired, whatever the capacities say.
        Assert.Equal(new GamepadBattery("wired", null, false), BatteryRules.FromGamepadReport(500, 1000, 0));
    }

    [Theory]
    [InlineData(null, 1000, 1)]
    [InlineData(500, null, 1)]
    [InlineData(500, 0, 1)]
    [InlineData(-1, 1000, 1)]
    [InlineData(500, 1000, 4)] // unknown status
    [InlineData(500, 1000, -1)]
    public void Gamepad_battery_report_without_usable_values_is_unknown(int? remaining, int? full, int status)
    {
        Assert.Equal(new GamepadBattery("unknown", null, false), BatteryRules.FromGamepadReport(remaining, full, status));
    }

    private static readonly GamepadBattery Report64 = new("medium", 64, false);
    private static readonly GamepadBattery NoBattery = new("unknown", null, false);

    [Fact]
    public void One_gamepad_report_fills_the_pad_xinput_does_not_know()
    {
        var pads = new[] { new PadBattery(0, "unknown", null, Ms) };
        Assert.Equal(new[] { new PadBattery(0, "medium", 64, Ms, "wgi") }, BatteryRules.FillFromGamepads(pads, [], [Report64]));
        // A Steam virtual pad without a battery report next to it does not count.
        Assert.Equal(new[] { new PadBattery(0, "medium", 64, Ms, "wgi") }, BatteryRules.FillFromGamepads(pads, [], [NoBattery, Report64, new("wired", null, false)]));
    }

    [Fact]
    public void Gamepad_report_prefers_a_slot_that_is_not_steams_virtual_pad()
    {
        var pads = new[] { new PadBattery(0, "unknown", null, Ms), new PadBattery(1, "unknown", null, Ms) };
        Assert.Equal(new[] { new PadBattery(0, "unknown", null, Ms), new PadBattery(1, "medium", 64, Ms, "wgi") }, BatteryRules.FillFromGamepads(pads, [0], [Report64]));
        // Two real pads without a level: which one has the battery is not known.
        Assert.Same(pads, BatteryRules.FillFromGamepads(pads, [], [Report64]));
        // Only Steam's virtual pad there (the real one hidden behind it): it gets the report.
        Assert.Equal(new[] { new PadBattery(2, "medium", 64, null, "wgi") }, BatteryRules.FillFromGamepads([new PadBattery(2, "unknown")], [2], [Report64]));
        // Two Steam virtual pads: ambiguous again.
        Assert.Same(pads, BatteryRules.FillFromGamepads(pads, [0, 1], [Report64]));
        // A Steam virtual pad next to a pad on its cable: most likely that pad seen twice (WGI may say idle, not charging).
        var wiredAndSteam = new[] { new PadBattery(0, "wired"), new PadBattery(1, "unknown") };
        Assert.Same(wiredAndSteam, BatteryRules.FillFromGamepads(wiredAndSteam, [1], [Report64]));
    }

    [Fact]
    public void Gamepad_report_is_not_used_when_it_is_ambiguous()
    {
        var unknown = new[] { new PadBattery(0, "unknown") };
        // Two reports with a battery: which is which is not known.
        Assert.Same(unknown, BatteryRules.FillFromGamepads(unknown, [], [Report64, new("full", 90, false)]));
        // None.
        Assert.Same(unknown, BatteryRules.FillFromGamepads(unknown, [], [NoBattery]));
        Assert.Same(unknown, BatteryRules.FillFromGamepads(unknown, [], []));
        // An XInput pad knows its own battery: the report is most likely that pad seen twice.
        var known = new[] { new PadBattery(0, "full"), new PadBattery(1, "unknown") };
        Assert.Same(known, BatteryRules.FillFromGamepads(known, [], [Report64]));
        // A charging report while an XInput pad says wired: that pad on its cable.
        var wired = new[] { new PadBattery(0, "wired"), new PadBattery(1, "unknown", null, Ms) };
        Assert.Same(wired, BatteryRules.FillFromGamepads(wired, [], [new("medium", 50, true)]));
        // Not charging, next to a wired pad without a battery: the unknown pad is the one with the battery.
        Assert.Equal(new[] { new PadBattery(0, "wired"), new PadBattery(1, "medium", 64, Ms, "wgi") }, BatteryRules.FillFromGamepads(wired, [], [Report64]));
    }

    [Fact]
    public void Gamepad_report_never_touches_playstation_pads()
    {
        // A PlayStation pad read over HID with an unknown level stays unknown; its own level stays its own.
        var pads = new[] { new PadBattery(4, "unknown"), new PadBattery(5, "low") };
        Assert.Same(pads, BatteryRules.FillFromGamepads(pads, [], [Report64]));
        var mixed = new[] { new PadBattery(0, "unknown", null, Ms), new PadBattery(4, "full") };
        Assert.Equal(new[] { new PadBattery(0, "medium", 64, Ms, "wgi"), new PadBattery(4, "full") }, BatteryRules.FillFromGamepads(mixed, [], [Report64]));
    }

    private const int Asus = 0x0B05;
    private const int Ms = 0x045E;

    [Fact]
    public void Windows_percent_beats_a_coarse_xinput_level_for_the_same_single_pad()
    {
        var pads = new[] { new PadBattery(0, "low", null, Ms) };
        Assert.Equal(new[] { new PadBattery(0, "full", 80, Ms, "wgi") }, BatteryRules.FillFromGamepads(pads, [], [new("full", 80, false)]));
        // Two XInput pads or two gamepads: which is which is not known, the coarse level stays.
        var two = new[] { new PadBattery(0, "low", null, Ms), new PadBattery(1, "full", null, Ms) };
        Assert.Same(two, BatteryRules.FillFromGamepads(two, [], [new("full", 80, false)]));
        Assert.Same(pads, BatteryRules.FillFromGamepads(pads, [], [new("full", 80, false), NoBattery]));
    }

    [Fact]
    public void Wgi_percent_is_never_taken_for_a_third_party_pad()
    {
        // The reported case: an ASUS pad, XInput says low, WGI has one gamepad with a bogus 10 %: nothing is shown.
        var low = new[] { new PadBattery(0, "low", null, Asus, "xinput") };
        Assert.Same(low, BatteryRules.FillFromGamepads(low, [], [new("low", 10, false)]));
        var pad = Assert.Single(BatteryRules.DistrustCoarse(BatteryRules.FillFromGamepads(low, [], [new("low", 10, false)])));
        Assert.Null(pad.Percent);
        Assert.Equal("unknown", pad.Level);
        var unknown = new[] { new PadBattery(0, "unknown", null, Asus) };
        Assert.Same(unknown, BatteryRules.FillFromGamepads(unknown, [], [new("low", 10, false)]));
        var noVendor = new[] { new PadBattery(0, "unknown") };
        Assert.Same(noVendor, BatteryRules.FillFromGamepads(noVendor, [], [Report64]));
        var both = new[] { new PadBattery(0, "unknown", null, Asus), new PadBattery(1, "unknown", null, Ms) };
        Assert.Same(both, BatteryRules.FillFromGamepads(both, [], [Report64]));
    }

    [Fact]
    public void Asus_pad_with_nothing_from_wgi_and_xinput_low_shows_nothing()
    {
        var pads = new[] { new PadBattery(0, "low", null, Asus, "xinput") };
        var shown = BatteryRules.DistrustCoarse(BatteryRules.FillFromGamepads(pads, [], [NoBattery]));
        var (matched, devices) = BatteryRules.MatchBluetooth(shown, [], new Dictionary<int, IReadOnlyList<string>>());
        var result = Assert.Single(matched);
        Assert.Null(result.Percent);
        Assert.Null(BatteryRules.Steps(result.Level));
        Assert.DoesNotContain(LowBatteryLatch.Readings(new BatterySnapshot(null, matched, devices)), x => x.Low);
    }

    [Fact]
    public void Coarse_low_of_a_non_microsoft_pad_is_not_believed_alone()
    {
        var pads = new[] { new PadBattery(0, "low", null, Asus), new PadBattery(1, "empty", null, Asus), new PadBattery(2, "low", null, Ms), new PadBattery(3, "low"), new PadBattery(4, "low") };
        var result = BatteryRules.DistrustCoarse(pads);
        Assert.Equal(["unknown", "unknown", "low", "low", "low"], result.Select(p => p.Level));
        // With a percent the pad keeps it.
        var withPercent = new[] { new PadBattery(0, "low", 10, Asus) };
        Assert.Equal("low", BatteryRules.DistrustCoarse(withPercent)[0].Level);
        // And an unbelieved pad does not fire the low warning.
        var snapshot = new BatterySnapshot(null, BatteryRules.DistrustCoarse([new PadBattery(0, "low", null, Asus)]), []);
        Assert.DoesNotContain(LowBatteryLatch.Readings(snapshot), r => r.Low);
    }

    // The owner's pad: ROG RAIKIRI PRO over Bluetooth LE, 80 %; a second paired Xbox pad reports 79.
    private const string RogAddress = "C15AF9554702";
    private const string XboxAddress = "3CFA06452ED8";
    private const string HidNode = @"BTHLEDEVICE\{00001812-0000-1000-8000-00805f9b34fb}_DEV_VID&020B05_PID&1ABD_REV&0100_C15AF9554702\8&1d2e&0&0020";
    private const string HidChild = @"HID\{00001812-0000-1000-8000-00805f9b34fb}&DEV&VID_0B05&PID_1ABD&REV_0100&C15AF9554702&IG_00\9&2a&0&0000";
    private const string BatteryServiceNode = @"BTHLEDEVICE\{0000180f-0000-1000-8000-00805f9b34fb}_DEV_VID&020B05_PID&1ABD_REV&0100_C15AF9554702\8&1d2e&0&0010";
    private static readonly DeviceBattery Rog = new("a", "ROG RAIKIRI PRO", "controller", 80) { Address = RogAddress };
    private static readonly DeviceBattery XboxBt = new("b", "Xbox Wireless Controller", "controller", 79) { Address = XboxAddress };
    private const int AsusPid = 0x1ABD;
    private const string HidNodeReal = @"BTHLEDevice\{00001812-0000-1000-8000-00805f9b34fb}_Dev_VID&020b05_PID&1abd_REV&0100_c15af9554702\e&79595ef&0&0001";
    private static IReadOnlyDictionary<int, IReadOnlyList<string>> Hid(int vendor, int product, params string[] addresses) =>
        new Dictionary<int, IReadOnlyList<string>> { [BatteryRules.PadKey(vendor, product)] = addresses };

    [Fact]
    public void Hid_instance_ids_give_vendor_and_bluetooth_address()
    {
        Assert.Equal((Asus, AsusPid, RogAddress), BatteryRules.HidPadOfInstance(HidNode));
        Assert.Equal((Asus, AsusPid, RogAddress), BatteryRules.HidPadOfInstance(HidChild));
        Assert.Null(BatteryRules.HidPadOfInstance(BatteryServiceNode));
        Assert.Null(BatteryRules.HidPadOfInstance(@"HID\VID_0B05&PID_1ABD\1"));
        Assert.Null(BatteryRules.HidPadOfInstance(null));
        Assert.Null(BatteryRules.HidPadOfInstance(new string('A', 400)));
        Assert.Equal(RogAddress, BatteryRules.AddressOfEndpoint("BluetoothLE#BluetoothLE5c:3a:45:00:11:22-c1:5a:f9:55:47:02"));
        Assert.Null(BatteryRules.AddressOfEndpoint("BluetoothLE#BluetoothLE5c:3a:45:00:11:22"));
        Assert.Null(BatteryRules.AddressOfEndpoint(null));
    }

    [Fact]
    public void Owner_pad_gets_exactly_its_own_bluetooth_battery_even_with_a_second_connected_pad()
    {
        var pads = BatteryRules.DistrustCoarse([new PadBattery(0, "low", null, Asus, "xinput", AsusPid)]);
        var hid = Hid(Asus, AsusPid, RogAddress);
        var (matched, devices) = BatteryRules.MatchBluetooth(pads, [XboxBt, Rog], hid);
        Assert.Equal(new PadBattery(0, "full", 80, Asus, "bluetooth", AsusPid), Assert.Single(matched));
        Assert.Equal([XboxBt], devices);
        Assert.DoesNotContain(LowBatteryLatch.Readings(new BatterySnapshot(null, matched, devices)), x => x.Low);
        // A paired but disconnected ROG pad is not in the device list: no percent, and the Xbox pad's 79 is not taken.
        var (none, _) = BatteryRules.MatchBluetooth(pads, [XboxBt], hid);
        Assert.Null(Assert.Single(none).Percent);
    }

    [Fact]
    public void Bluetooth_match_is_exact()
    {
        var headset = new DeviceBattery("h", "Headset", "headphones", 5) { Address = "AAAAAAAAAAAA" };
        var onlyAsus = new[] { new PadBattery(0, "unknown", null, Asus, null, AsusPid) };
        // Without address information a non-Microsoft pad matches nothing, whatever the name says.
        Assert.Null(BatteryRules.MatchBluetooth(onlyAsus, [Rog]).Pads[0].Percent);
        Assert.Null(BatteryRules.MatchBluetooth(onlyAsus, [Rog], new Dictionary<int, IReadOnlyList<string>>()).Pads[0].Percent);
        // Two HID addresses of one vendor (two such pads): ambiguous.
        Assert.Null(BatteryRules.MatchBluetooth(onlyAsus, [Rog], Hid(Asus, AsusPid, RogAddress, "112233445566")).Pads[0].Percent);
        // The address belongs to another device than the listed ones; a headset is no partner; names are not enough.
        Assert.Null(BatteryRules.MatchBluetooth(onlyAsus, [XboxBt, headset], Hid(Asus, AsusPid, RogAddress)).Pads[0].Percent);
        Assert.Null(BatteryRules.MatchBluetooth(onlyAsus, [new DeviceBattery("n", "ROG RAIKIRI PRO", "controller", 10)], Hid(Asus, AsusPid, RogAddress)).Pads[0].Percent);
        // Wired, Steam's virtual pad, unknown vendor and PlayStation slots are never matched.
        var hid = Hid(Asus, AsusPid, RogAddress);
        Assert.Null(BatteryRules.MatchBluetooth([new PadBattery(0, "wired", null, Asus, null, AsusPid)], [Rog], hid).Pads[0].Percent);
        Assert.Null(BatteryRules.MatchBluetooth([new PadBattery(0, "unknown", null, 0x28DE)], [Rog], hid).Pads[0].Percent);
        Assert.Null(BatteryRules.MatchBluetooth([new PadBattery(0, "unknown")], [Rog], hid).Pads[0].Percent);
        Assert.Null(BatteryRules.MatchBluetooth([new PadBattery(4, "unknown", null, Asus, null, AsusPid)], [Rog], hid).Pads[0].Percent);
        // Microsoft pads: the address when known, otherwise the "Xbox ..." name; never another vendor's device.
        var ms = new[] { new PadBattery(0, "medium", null, Ms, null, 0x02FD) };
        Assert.Equal(79, BatteryRules.MatchBluetooth(ms, [Rog, XboxBt]).Pads[0].Percent);
        Assert.Null(BatteryRules.MatchBluetooth(ms, [Rog]).Pads[0].Percent);
        Assert.Equal(79, BatteryRules.MatchBluetooth(ms, [Rog, XboxBt], Hid(Ms, 0x02FD, XboxAddress)).Pads[0].Percent);
        // Two Xbox pads without an address: ambiguous.
        var xbox2 = new DeviceBattery("c", "Xbox Wireless Controller", "controller", 30);
        Assert.Null(BatteryRules.MatchBluetooth(ms, [XboxBt, xbox2]).Pads[0].Percent);
    }

    [Fact]
    public void Same_vendor_other_product_neither_blocks_nor_steals_the_match()
    {
        var pads = new[] { new PadBattery(0, "unknown", null, Asus, null, AsusPid) };
        // A same-vendor keyboard (other PID) with its own address does not make the pad ambiguous.
        var map = new Dictionary<int, IReadOnlyList<string>>
        {
            [BatteryRules.PadKey(Asus, AsusPid)] = [RogAddress],
            [BatteryRules.PadKey(Asus, 0x1866)] = ["AABBCCDDEEFF"]
        };
        Assert.Equal(80, BatteryRules.MatchBluetooth(pads, [Rog], map).Pads[0].Percent);
        // A pad whose product is not in the map, or not known, gets nothing.
        var otherPid = new[] { new PadBattery(0, "unknown", null, Asus, null, 0x1866) };
        Assert.Null(BatteryRules.MatchBluetooth(otherPid, [Rog], Hid(Asus, AsusPid, RogAddress)).Pads[0].Percent);
        var noPid = new[] { new PadBattery(0, "unknown", null, Asus) };
        Assert.Null(BatteryRules.MatchBluetooth(noPid, [Rog], Hid(Asus, AsusPid, RogAddress)).Pads[0].Percent);
    }

    [Fact]
    public void Hid_node_query_uses_the_device_instance_id_property_and_real_ids_parse()
    {
        // System.Devices.InstanceId does not support starts-with (ArgumentException); DeviceInstanceId does.
        Assert.Equal("System.Devices.DeviceInstanceId:~<\"BTHLEDEVICE\\{00001812\"", BatteryRules.HidNodeQuery);
        Assert.Equal((Asus, AsusPid, RogAddress), BatteryRules.HidPadOfInstance(HidNodeReal));
        Assert.Null(BatteryRules.AddressOfEndpoint(new string('a', 300) + "-c1:5a:f9:55:47:02"));
    }

    [Fact]
    public void A_spoofed_bluetooth_name_cannot_hide_a_low_microsoft_pad_by_vendor_mismatch()
    {
        var pads = new[] { new PadBattery(0, "low", null, Ms) };
        var (p, _) = BatteryRules.MatchBluetooth(pads, [new DeviceBattery("x", "ROG fake", "controller", 100)]);
        Assert.Equal("low", p[0].Level);
    }

    [Fact]
    public void Battery_source_log_says_one_line_per_pad_when_source_or_value_changes()
    {
        var log = new BatterySourceLog();
        Assert.Equal(["[battery] pad 0 source=wgi 87%", "[battery] pad 1 source=none"],
            log.Update([new PadBattery(0, "full", 87, Ms, "wgi"), new PadBattery(1, "unknown", null, Asus)]));
        Assert.Empty(log.Update([new PadBattery(0, "full", 87, Ms, "wgi"), new PadBattery(1, "unknown", null, Asus)]));
        Assert.Equal(["[battery] pad 0 source=wgi 86%"], log.Update([new PadBattery(0, "full", 86, Ms, "wgi"), new PadBattery(1, "unknown", null, Asus)]));
        Assert.Equal(["[battery] pad 1 source=bluetooth 40%"], log.Update([new PadBattery(0, "full", 86, Ms, "wgi"), new PadBattery(1, "medium", 40, Asus, "bluetooth")]));
        Assert.Equal(["[battery] pad 4 source=hid level=low"], log.Update([new PadBattery(4, "low", null, null, "hid")]));
        Assert.Equal(["[battery] pad 2 source=xinput level=wired"], log.Update([new PadBattery(2, "wired", null, Ms, "xinput")]));
        Assert.Equal(["[battery] pad 4 source=hid level=low"], log.Update([new PadBattery(4, "low", null, null, "hid")]));
    }

    [Fact]
    public void Pad_list_follows_the_live_pads_and_only_takes_values_from_the_snapshot_for_a_pad_in_it()
    {
        var snapshot = new[] { new PadBattery(0, "full", 80, 0x0B05) };
        // The last pad was unplugged: nothing is reported, whatever the snapshot still says.
        Assert.Empty(BatteryRules.LiveWithSnapshot([], snapshot));
        // A pad that is in the snapshot gets its level and percent; another one keeps its own.
        var live = new[] { new PadBattery(0, "unknown", null, 0x0B05), new PadBattery(1, "medium", null, 0x045E) };
        var merged = BatteryRules.LiveWithSnapshot(live, snapshot);
        Assert.Equal(new PadBattery(0, "full", 80, 0x0B05), merged[0]);
        Assert.Equal(live[1], merged[1]);
    }

    private static BatterySnapshot Snap(int? system = null, string? pad = null, int? bt = null, bool plugged = false) => new(
        system is { } s ? BatteryRules.FromPowerStatus((byte)(plugged ? 1 : 0), 1, (byte)s) : null,
        pad is null ? [] : [new PadBattery(0, pad)],
        bt is { } b ? [new DeviceBattery("dev", "Pad", "controller", b)] : []);

    private static readonly long CooldownMs = (long)LowBatteryLatch.Cooldown.TotalMilliseconds;

    [Fact]
    public void Low_battery_is_warned_once_per_drop()
    {
        long now = 0;
        var latch = new LowBatteryLatch(() => now);
        Assert.Empty(latch.Update(LowBatteryLatch.Readings(Snap(system: 50))));
        Assert.Equal(new[] { "system" }, latch.Update(LowBatteryLatch.Readings(Snap(system: 15))));
        // Still low, lower, wobbling around the threshold: no second warning.
        Assert.Empty(latch.Update(LowBatteryLatch.Readings(Snap(system: 10))));
        Assert.Empty(latch.Update(LowBatteryLatch.Readings(Snap(system: 16))));
        Assert.Empty(latch.Update(LowBatteryLatch.Readings(Snap(system: 14))));
        // Charged back to the recovered level: the next drop (after the cooldown) warns again.
        Assert.Empty(latch.Update(LowBatteryLatch.Readings(Snap(system: BatteryRules.RecoveredPercent))));
        now += CooldownMs;
        Assert.Equal(new[] { "system" }, latch.Update(LowBatteryLatch.Readings(Snap(system: 15))));
        // On the charger counts as recovered.
        Assert.Empty(latch.Update(LowBatteryLatch.Readings(Snap(system: 15, plugged: true))));
        now += CooldownMs;
        Assert.Equal(new[] { "system" }, latch.Update(LowBatteryLatch.Readings(Snap(system: 14))));
    }

    [Fact]
    public void Each_controller_is_warned_once_and_again_after_it_was_gone()
    {
        long now = 0;
        var latch = new LowBatteryLatch(() => now);
        Assert.Equal(new[] { "pad:0", "bt:dev" }, latch.Update(LowBatteryLatch.Readings(Snap(pad: "low", bt: 10))));
        Assert.Empty(latch.Update(LowBatteryLatch.Readings(Snap(pad: "empty", bt: 5))));
        // An unknown level is neither low nor recovered: no warning, and the drop is not forgotten.
        Assert.Empty(latch.Update(LowBatteryLatch.Readings(Snap(pad: "unknown", bt: 5))));
        Assert.Empty(latch.Update(LowBatteryLatch.Readings(Snap(pad: "low", bt: 5))));
        // The pad was switched off (or another pad takes the slot): after the cooldown it is warned about again.
        now += CooldownMs;
        Assert.Empty(latch.Update(LowBatteryLatch.Readings(Snap(bt: 5))));
        Assert.Equal(new[] { "pad:0" }, latch.Update(LowBatteryLatch.Readings(Snap(pad: "low", bt: 5))));
        // A new set of batteries brings the pad back to medium: recovered.
        Assert.Empty(latch.Update(LowBatteryLatch.Readings(Snap(pad: "medium", bt: 30))));
        now += CooldownMs;
        Assert.Equal(new[] { "pad:0", "bt:dev" }, latch.Update(LowBatteryLatch.Readings(Snap(pad: "low", bt: 15))));
    }

    [Fact]
    public void A_flapping_pad_or_a_swinging_level_toasts_at_most_once_per_cooldown()
    {
        long now = 0;
        var latch = new LowBatteryLatch(() => now);
        Assert.Equal(new[] { "pad:0", "bt:dev" }, latch.Update(LowBatteryLatch.Readings(Snap(pad: "low", bt: 10))));
        // The pad drops its connection and comes back, the device swings between 10 and 25 %: every poll for nine minutes.
        for (var i = 0; i < 9; i++)
        {
            now += 60_000;
            Assert.Empty(latch.Update(LowBatteryLatch.Readings(Snap(bt: 25))));
            Assert.Empty(latch.Update(LowBatteryLatch.Readings(Snap(pad: "low", bt: 10))));
        }
        // Gone for a while (the cooldown survives absence), back just before it ends: still quiet.
        Assert.Empty(latch.Update(LowBatteryLatch.Readings(BatterySnapshot.Empty)));
        now = CooldownMs - 1;
        Assert.Empty(latch.Update(LowBatteryLatch.Readings(Snap(pad: "low", bt: 10))));
        // A drop latched silently inside the cooldown does not toast later while it stays low...
        now = CooldownMs + 1;
        Assert.Empty(latch.Update(LowBatteryLatch.Readings(Snap(pad: "low", bt: 10))));
        // ...but the next real drop after the cooldown does.
        Assert.Empty(latch.Update(LowBatteryLatch.Readings(Snap(pad: "medium", bt: 30))));
        Assert.Equal(new[] { "pad:0", "bt:dev" }, latch.Update(LowBatteryLatch.Readings(Snap(pad: "low", bt: 10))));
    }

    [Fact]
    public void The_cooldown_is_per_battery()
    {
        long now = 0;
        var latch = new LowBatteryLatch(() => now);
        Assert.Equal(new[] { "pad:0" }, latch.Update(LowBatteryLatch.Readings(Snap(pad: "low"))));
        now += 1000;
        Assert.Equal(new[] { "system" }, latch.Update(LowBatteryLatch.Readings(Snap(system: 10, pad: "low"))));
    }

    [Fact]
    public void A_device_goes_to_the_ui_under_an_opaque_key()
    {
        const string id = "Bluetooth#Bluetooth00:1a:7d:da:71:13-98:b6:e9:00:11:22";
        var key = BatteryRules.DeviceKey(id);
        Assert.Equal(16, key.Length);
        Assert.Matches("^[0-9a-f]{16}$", key);
        Assert.Equal(key, BatteryRules.DeviceKey(id));
        Assert.NotEqual(key, BatteryRules.DeviceKey(id.Replace("22", "23")));
        Assert.DoesNotContain(":", key);
    }

    [Fact]
    public async Task A_hung_read_is_not_started_again_until_it_ends()
    {
        var flight = new SingleFlight<string>();
        var hung = new TaskCompletionSource<string>();
        var starts = 0;
        Task<string> Start() { starts++; return hung.Task; }

        Assert.Null(await flight.RunAsync(Start, TimeSpan.FromMilliseconds(30)));
        Assert.True(flight.Busy);
        // Every later poll while it hangs: no new read, nothing waits.
        for (var i = 0; i < 5; i++) Assert.Null(await flight.RunAsync(Start, TimeSpan.FromSeconds(30)));
        Assert.Equal(1, starts);

        hung.SetResult("late");
        Assert.False(flight.Busy);
        Assert.Equal("fresh", await flight.RunAsync(() => { starts++; return Task.FromResult("fresh"); }, TimeSpan.FromSeconds(1)));
        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task A_failed_read_ends_the_flight_and_reports_the_error()
    {
        var flight = new SingleFlight<string>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => flight.RunAsync(() => Task.FromException<string>(new InvalidOperationException()), TimeSpan.FromSeconds(1)));
        Assert.False(flight.Busy);
        Assert.Equal("ok", await flight.RunAsync(() => Task.FromResult("ok"), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Wired_pads_and_desktops_never_warn()
    {
        var latch = new LowBatteryLatch();
        Assert.Empty(latch.Update(LowBatteryLatch.Readings(Snap(pad: "wired"))));
        Assert.Empty(latch.Update(LowBatteryLatch.Readings(BatterySnapshot.Empty)));
    }

    [Fact]
    public void Battery_levels_are_open_to_a_guest_and_at_the_profile_picker()
    {
        // Read-only, like getPads: the top bar shows them on a locked couch and at the profile picker.
        Assert.Contains("getBattery", LockPolicy.OpenWhileLocked);
        Assert.Contains("getBattery", CouchHud.Core.Config.ProfilePolicy.OpenWhileHeld);
    }
}
