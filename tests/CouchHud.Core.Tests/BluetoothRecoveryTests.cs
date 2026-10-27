using CouchHud.Core.Config;
using CouchHud.Core.Guard;
using CouchHud.Core.Input;
using Xunit;

namespace CouchHud.Core.Tests;

public class BluetoothRecoveryTests
{
    private static readonly TimeSpan Base = TimeSpan.FromHours(1);

    private static RecoveryAction Act(bool enabled = true, RadioStatus radio = RadioStatus.Off, int paired = 1, int pads = 0, bool other = false,
        RecoveryTrigger trigger = RecoveryTrigger.RadioOff, bool airplane = false) =>
        BluetoothRecovery.Decide(new RecoveryInput(enabled, radio, paired, pads, other, trigger, airplane)).Action;

    [Theory]
    [InlineData(RecoveryTrigger.Startup)]
    [InlineData(RecoveryTrigger.RadioOff)]
    [InlineData(RecoveryTrigger.Retry)]
    public void AirplaneMode_WithKeyboardOrMouse_Countdown_WithoutThem_On(RecoveryTrigger trigger)
    {
        // Meant more often than not: ask whoever has keyboard or mouse; with nothing else at hand it comes back (no lock-out).
        Assert.Equal(RecoveryAction.Countdown, Act(other: true, airplane: true, trigger: trigger));
        Assert.Equal(RecoveryAction.EnableNow, Act(other: false, airplane: true, trigger: trigger));
    }

    [Fact]
    public void CountdownElapsed_SwitchesOn_UnlessAPadCameBack()
    {
        Assert.Equal(RecoveryAction.EnableNow, Act(other: true, airplane: true, trigger: RecoveryTrigger.CountdownElapsed));
        Assert.Equal(RecoveryAction.None, Act(pads: 1, trigger: RecoveryTrigger.CountdownElapsed));
        Assert.Equal(RecoveryAction.None, Act(radio: RadioStatus.On, trigger: RecoveryTrigger.CountdownElapsed));
    }

    [Fact]
    public void Backoff_ForcedOffWindowAfterWindow_GivesUp()
    {
        var b = new RecoveryBackoff();
        var now = Base;
        for (var window = 0; window < RecoveryBackoff.MaxFullWindows; window++)
        {
            for (var i = 0; i < RecoveryBackoff.MaxEnablesPerWindow; i++)
            {
                Assert.True(b.MayTry(now));
                b.Succeeded(now);
                now += TimeSpan.FromSeconds(10);
            }
            Assert.False(b.MayTry(now));
            // Waiting for the window does not count as another full one.
            Assert.False(b.MayTry(now + TimeSpan.FromSeconds(1)));
            now = b.NextTry;
        }
        Assert.True(b.GaveUp);
        Assert.False(b.MayTry(now + TimeSpan.FromDays(1)));
    }

    [Theory]
    [InlineData(RecoveryTrigger.Startup)]
    [InlineData(RecoveryTrigger.RadioOff)]
    [InlineData(RecoveryTrigger.Retry)]
    public void Off_WithPairedController_AndNoPad_SwitchesOn(RecoveryTrigger trigger) =>
        Assert.Equal(RecoveryAction.EnableNow, Act(trigger: trigger));

    [Fact]
    public void SettingOff_DoesNothing()
    {
        foreach (var radio in Enum.GetValues<RadioStatus>())
            foreach (var trigger in Enum.GetValues<RecoveryTrigger>())
                Assert.Equal(RecoveryAction.None, Act(enabled: false, radio: radio, trigger: trigger));
    }

    [Fact]
    public void RadioOn_OrUnknown_DoesNothing()
    {
        Assert.Equal(RecoveryAction.None, Act(radio: RadioStatus.On));
        Assert.Equal(RecoveryAction.None, Act(radio: RadioStatus.Unknown));
    }

    [Theory]
    [InlineData(RadioStatus.NoAdapter)]
    [InlineData(RadioStatus.Disabled)]
    [InlineData(RadioStatus.AccessDenied)]
    public void RadioNotOurs_OnlyLogs(RadioStatus radio)
    {
        foreach (var trigger in Enum.GetValues<RecoveryTrigger>())
            Assert.Equal(RecoveryAction.Log, Act(radio: radio, trigger: trigger));
    }

    [Fact]
    public void NoPairedController_DoesNothing() => Assert.Equal(RecoveryAction.None, Act(paired: 0, trigger: RecoveryTrigger.Startup));

    [Fact]
    public void AnotherPadConnected_DoesNothing()
    {
        Assert.Equal(RecoveryAction.None, Act(pads: 1));
        Assert.Equal(RecoveryAction.None, Act(pads: 2, trigger: RecoveryTrigger.Startup));
    }

    [Fact]
    public void SwitchedOffWhileKeyboardOrMouseInUse_Countdown() =>
        Assert.Equal(RecoveryAction.Countdown, Act(other: true));

    [Fact]
    public void AtStartAndAfterCountdown_NoSecondCountdown()
    {
        Assert.Equal(RecoveryAction.EnableNow, Act(other: true, trigger: RecoveryTrigger.Startup));
        Assert.Equal(RecoveryAction.EnableNow, Act(other: true, trigger: RecoveryTrigger.Retry));
    }

    [Fact]
    public void Config_DefaultsOn_AndTheSettingNeedsTheCode()
    {
        Assert.True(new AppConfig().BluetoothAutoOn);
        // A guest on a locked couch cannot switch the safety net off, nor the radio.
        Assert.DoesNotContain("bluetoothAutoOn", LockPolicy.HarmlessSettings);
        Assert.DoesNotContain("setBluetooth", LockPolicy.OpenWhileLocked);
        Assert.Contains("getBluetooth", LockPolicy.OpenWhileLocked);
        Assert.Contains("getPads", LockPolicy.OpenWhileLocked);
    }

    [Fact]
    public void Backoff_FailuresWaitLonger_ThenGiveUp()
    {
        var b = new RecoveryBackoff();
        var now = Base;
        Assert.True(b.MayTry(now));
        b.Failed(now);
        Assert.False(b.MayTry(now + TimeSpan.FromSeconds(29)));
        Assert.True(b.MayTry(now + TimeSpan.FromSeconds(30)));
        now += TimeSpan.FromSeconds(30);
        b.Failed(now);
        Assert.Equal(now + TimeSpan.FromSeconds(60), b.NextTry);
        for (var i = 2; i < RecoveryBackoff.MaxFailures; i++)
        {
            now = b.NextTry;
            Assert.True(b.MayTry(now));
            b.Failed(now);
            Assert.True(b.GaveUp || b.NextTry - now <= RecoveryBackoff.MaxDelay);
        }
        Assert.True(b.GaveUp);
        Assert.False(b.MayTry(now + TimeSpan.FromDays(1)));
    }

    [Fact]
    public void Backoff_SuccessResetsFailures()
    {
        var b = new RecoveryBackoff();
        var now = Base;
        b.Failed(now);
        b.Succeeded(now + TimeSpan.FromMinutes(1));
        Assert.Equal(0, b.Failures);
        Assert.True(b.MayTry(now + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Backoff_SomeoneKeepsSwitchingOff_WaitsInsteadOfLooping()
    {
        var b = new RecoveryBackoff();
        var now = Base;
        for (var i = 0; i < RecoveryBackoff.MaxEnablesPerWindow; i++)
        {
            Assert.True(b.MayTry(now));
            b.Succeeded(now);
            now += TimeSpan.FromSeconds(10);
        }
        Assert.False(b.MayTry(now));
        Assert.False(b.GaveUp);
        Assert.Equal(Base + RecoveryBackoff.Window, b.NextTry);
        Assert.True(b.MayTry(Base + RecoveryBackoff.Window));
    }

    [Theory]
    [InlineData(new[] { "Input.Gaming" }, null, null, null, "Pad", true)]
    [InlineData(null, 5, 2, null, "Some HID", true)]            // peripheral, gamepad
    [InlineData(null, 5, 1, null, "Some HID", true)]            // peripheral, joystick
    [InlineData(null, 5, 0x12, null, "Some HID", true)]         // gamepad with the keyboard bit
    [InlineData(null, 5, 0x10, null, "Some keyboard", false)]   // keyboard
    [InlineData(null, 4, 2, null, "Headset", false)]            // audio
    [InlineData(null, null, null, 0x03C4, "LE pad", true)]      // LE gamepad appearance
    [InlineData(null, null, null, 0x03C1, "LE keyboard", false)]
    [InlineData(new[] { "Audio.Headphone" }, null, null, null, "Xbox Wireless Controller", true)] // the name as the last hint
    [InlineData(new[] { "Audio.Headphone" }, null, null, null, "WH-1000XM4", false)]
    public void ControllerKind(string[]? categories, int? major, int? minor, int? appearance, string name, bool expected) =>
        Assert.Equal(expected, BluetoothDeviceKind.IsController(categories, major, minor, appearance, name));

    [Fact]
    public void Kind_FromName()
    {
        Assert.Equal("controller", BluetoothDeviceKind.Kind("DualSense Wireless Controller"));
        Assert.Equal("controller", BluetoothDeviceKind.Kind("Xbox Wireless Controller"));
        Assert.Equal("headphones", BluetoothDeviceKind.Kind("WH-1000XM4"));
        Assert.Equal("other", BluetoothDeviceKind.Kind(null));
    }
}
