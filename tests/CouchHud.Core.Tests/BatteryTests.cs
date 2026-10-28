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
        var pads = new[] { new PadBattery(0, "unknown") };
        Assert.Equal(new[] { new PadBattery(0, "medium", 64) }, BatteryRules.FillFromGamepads(pads, [], [Report64]));
        // A Steam virtual pad without a battery report next to it does not count.
        Assert.Equal(new[] { new PadBattery(0, "medium", 64) }, BatteryRules.FillFromGamepads(pads, [], [NoBattery, Report64, new("wired", null, false)]));
    }

    [Fact]
    public void Gamepad_report_prefers_a_slot_that_is_not_steams_virtual_pad()
    {
        var pads = new[] { new PadBattery(0, "unknown"), new PadBattery(1, "unknown") };
        Assert.Equal(new[] { new PadBattery(0, "unknown"), new PadBattery(1, "medium", 64) }, BatteryRules.FillFromGamepads(pads, [0], [Report64]));
        // Two real pads without a level: which one has the battery is not known.
        Assert.Same(pads, BatteryRules.FillFromGamepads(pads, [], [Report64]));
        // Only Steam's virtual pad there (the real one hidden behind it): it gets the report.
        Assert.Equal(new[] { new PadBattery(2, "medium", 64) }, BatteryRules.FillFromGamepads([new PadBattery(2, "unknown")], [2], [Report64]));
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
        var wired = new[] { new PadBattery(0, "wired"), new PadBattery(1, "unknown") };
        Assert.Same(wired, BatteryRules.FillFromGamepads(wired, [], [new("medium", 50, true)]));
        // Not charging, next to a wired pad without a battery: the unknown pad is the one with the battery.
        Assert.Equal(new[] { new PadBattery(0, "wired"), new PadBattery(1, "medium", 64) }, BatteryRules.FillFromGamepads(wired, [], [Report64]));
    }

    [Fact]
    public void Gamepad_report_never_touches_playstation_pads()
    {
        // A PlayStation pad read over HID with an unknown level stays unknown; its own level stays its own.
        var pads = new[] { new PadBattery(4, "unknown"), new PadBattery(5, "low") };
        Assert.Same(pads, BatteryRules.FillFromGamepads(pads, [], [Report64]));
        var mixed = new[] { new PadBattery(0, "unknown"), new PadBattery(4, "full") };
        Assert.Equal(new[] { new PadBattery(0, "medium", 64), new PadBattery(4, "full") }, BatteryRules.FillFromGamepads(mixed, [], [Report64]));
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
