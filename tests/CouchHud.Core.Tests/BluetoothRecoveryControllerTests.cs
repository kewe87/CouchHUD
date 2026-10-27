using CouchHud.Core.Input;
using Xunit;

namespace CouchHud.Core.Tests;

/// <summary>The recovery's timing with a fake radio, a hand-driven clock and a synchronous "UI thread".</summary>
public class BluetoothRecoveryControllerTests
{
    private sealed class FakeRadio : IBluetoothRadio
    {
        public RadioStatus Status = RadioStatus.On;
        public int Paired = 1;
        public bool Airplane;
        public bool FailSwitch;
        public int SwitchOns;
        // Set: StatusAsync waits until the test lets it go (a check still running).
        public TaskCompletionSource<RadioStatus>? Hold;

        public event Action<RadioStatus>? RadioChanged;

        public bool Subscribed => RadioChanged is not null;

        public Task<RadioStatus> StatusAsync() => Hold?.Task ?? Task.FromResult(Status);

        public Task<int> PairedControllersAsync() => Task.FromResult(Paired);

        public Task<bool> AirplaneModeAsync() => Task.FromResult(Airplane);

        public Task SetRadioAsync(bool on)
        {
            if (FailSwitch) return Task.FromException(new InvalidOperationException("switch failed"));
            if (on) SwitchOns++;
            Set(on ? RadioStatus.On : RadioStatus.Off);
            return Task.CompletedTask;
        }

        public void Set(RadioStatus status)
        {
            Status = status;
            RadioChanged?.Invoke(status);
        }
    }

    private sealed class Harness
    {
        public readonly FakeRadio Radio = new();
        public readonly List<string> Phases = [];
        public bool Enabled = true;
        public int Pads;
        public bool OtherInput;
        public TimeSpan Now = TimeSpan.FromHours(1);
        private readonly List<(TimeSpan Due, TaskCompletionSource Done, CancellationToken Token)> _timers = [];
        public readonly BluetoothRecoveryController Controller;
        public int Remembered;

        public Harness()
        {
            // Continuations run inline: everything happens on the test's thread, in order.
            SynchronizationContext.SetSynchronizationContext(null);
            Controller = new BluetoothRecoveryController(Radio, () => Enabled, () => Pads, () => OtherInput,
                (phase, _) => Phases.Add(phase), action => action(), () => Now, Delay, _ => { },
                () => Remembered, count => Remembered = count);
        }

        private Task Delay(TimeSpan wait, CancellationToken token)
        {
            var done = new TaskCompletionSource();
            token.Register(() => done.TrySetCanceled(token));
            _timers.Add((Now + wait, done, token));
            return done.Task;
        }

        /// <summary>Moves the clock on and fires every timer that is due, in order.</summary>
        public void Advance(TimeSpan span)
        {
            var end = Now + span;
            while (true)
            {
                var due = _timers.Where(t => t.Due <= end && !t.Done.Task.IsCompleted).OrderBy(t => t.Due).FirstOrDefault();
                if (due.Done is null) break;
                _timers.Remove(due);
                if (due.Due > Now) Now = due.Due;
                due.Done.TrySetResult();
            }
            Now = end;
        }

        public int OpenTimers => _timers.Count(t => !t.Done.Task.IsCompleted);

        /// <summary>Started with Bluetooth on, then switched off from outside (the quick settings) and the pads dropped out.</summary>
        public void StartedThenSwitchedOff()
        {
            Controller.Start();
            Advance(BluetoothRecoveryController.StartDelay);
            Radio.Set(RadioStatus.Off);
            Advance(BluetoothRecoveryController.Settle);
        }

        public void RunOutCountdown() => Advance(TimeSpan.FromSeconds(BluetoothRecovery.CountdownSeconds));
    }

    [Fact]
    public void Startup_Off_SwitchesOnWithoutAsking()
    {
        var h = new Harness();
        h.Radio.Status = RadioStatus.Off;
        h.OtherInput = true;
        h.Controller.Start();
        Assert.True(h.Radio.Subscribed);
        h.Advance(BluetoothRecoveryController.StartDelay);
        Assert.Equal(1, h.Radio.SwitchOns);
        Assert.Equal(["enabled"], h.Phases);
    }

    [Fact]
    public void SwitchedOffWithoutOtherInput_ComesBackAtOnce()
    {
        var h = new Harness();
        h.StartedThenSwitchedOff();
        Assert.Equal(1, h.Radio.SwitchOns);
        Assert.Equal(["enabled"], h.Phases);
    }

    [Fact]
    public void CountdownDuringCodeEntry_NoAnswer_SwitchesOnAfterIt()
    {
        // Keyboard in use, the countdown is up; the UI shows only the hint in the code prompt and nobody answers.
        var h = new Harness { OtherInput = true };
        h.StartedThenSwitchedOff();
        Assert.Equal(["countdown"], h.Phases);
        Assert.True(h.Controller.CountdownRunning);
        h.Advance(TimeSpan.FromSeconds(BluetoothRecovery.CountdownSeconds - 1));
        Assert.Equal(0, h.Radio.SwitchOns);
        h.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, h.Radio.SwitchOns);
        Assert.False(h.Controller.CountdownRunning);
        Assert.Equal(["countdown", "enabled"], h.Phases);
    }

    [Fact]
    public void LeaveOff_KeepsItOff_UntilItIsOnAgain()
    {
        var h = new Harness { OtherInput = true };
        h.StartedThenSwitchedOff();
        Assert.True(h.Controller.Decline());
        Assert.True(h.Controller.Paused);
        Assert.Equal(["countdown", "idle"], h.Phases);
        h.RunOutCountdown();
        h.Radio.Set(RadioStatus.Off);
        h.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(0, h.Radio.SwitchOns);
        // Switched on by hand: the next switch-off is watched again.
        h.Radio.Set(RadioStatus.On);
        Assert.False(h.Controller.Paused);
        h.OtherInput = false;
        h.Radio.Set(RadioStatus.Off);
        h.Advance(BluetoothRecoveryController.Settle);
        Assert.Equal(1, h.Radio.SwitchOns);
    }

    [Fact]
    public void LeaveOff_AfterTheCountdownRanOut_ReturnsFalse_AndTheRadioIsOn()
    {
        var h = new Harness { OtherInput = true };
        h.StartedThenSwitchedOff();
        h.RunOutCountdown();
        Assert.False(h.Controller.Decline());
        Assert.False(h.Controller.Paused);
        Assert.Equal(RadioStatus.On, h.Radio.Status);
    }

    [Fact]
    public void Now_SwitchesOnAtOnce()
    {
        var h = new Harness { OtherInput = true };
        h.StartedThenSwitchedOff();
        h.Controller.SkipCountdown();
        Assert.Equal(1, h.Radio.SwitchOns);
        Assert.Equal(["countdown", "enabled"], h.Phases);
        h.RunOutCountdown();
        Assert.Equal(1, h.Radio.SwitchOns);
    }

    [Fact]
    public void UsbPadDuringCountdown_StaysOff_AndTheCountdownCloses()
    {
        var h = new Harness { OtherInput = true };
        h.StartedThenSwitchedOff();
        h.Pads = 1;
        h.RunOutCountdown();
        Assert.Equal(0, h.Radio.SwitchOns);
        Assert.Equal(["countdown", "idle"], h.Phases);
        Assert.False(h.Controller.CountdownRunning);
    }

    [Fact]
    public void SettingOffDuringCountdown_ClosesIt()
    {
        var h = new Harness { OtherInput = true };
        h.StartedThenSwitchedOff();
        h.Enabled = false;
        h.RunOutCountdown();
        Assert.Equal(0, h.Radio.SwitchOns);
        Assert.Equal(["countdown", "idle"], h.Phases);
    }

    [Fact]
    public void SwitchFailsAfterCountdown_SaysSo_AndTriesAgainLater()
    {
        var h = new Harness { OtherInput = true };
        h.StartedThenSwitchedOff();
        h.Radio.FailSwitch = true;
        h.RunOutCountdown();
        Assert.Equal(["countdown", "failed"], h.Phases);
        h.Radio.FailSwitch = false;
        h.OtherInput = false;
        h.Advance(RecoveryBackoff.FirstDelay);
        Assert.Equal(1, h.Radio.SwitchOns);
        Assert.Equal(["countdown", "failed", "enabled"], h.Phases);
    }

    [Fact]
    public void RadioOnDuringCountdown_ClosesIt()
    {
        var h = new Harness { OtherInput = true };
        h.StartedThenSwitchedOff();
        h.Radio.Set(RadioStatus.On);
        Assert.Equal(["countdown", "idle"], h.Phases);
        h.RunOutCountdown();
        Assert.Equal(0, h.Radio.SwitchOns);
    }

    [Fact]
    public void AirplaneMode_WithKeyboard_AsksFirst_EvenAtStart()
    {
        var h = new Harness { OtherInput = true };
        h.Radio.Status = RadioStatus.Off;
        h.Radio.Airplane = true;
        h.Controller.Start();
        h.Advance(BluetoothRecoveryController.StartDelay);
        Assert.Equal(["countdown"], h.Phases);
        Assert.True(h.Controller.Decline());
        Assert.Equal(0, h.Radio.SwitchOns);
    }

    [Fact]
    public void AirplaneMode_WithoutOtherInput_SwitchesOn()
    {
        var h = new Harness();
        h.Radio.Airplane = true;
        h.StartedThenSwitchedOff();
        Assert.Equal(1, h.Radio.SwitchOns);
        Assert.Equal(["enabled"], h.Phases);
    }

    [Fact]
    public void ForcedOffAgainAndAgain_GivesUpAfterTwoFullWindows()
    {
        var h = new Harness();
        h.Controller.Start();
        h.Advance(BluetoothRecoveryController.StartDelay);
        for (var i = 0; i < 20; i++)
        {
            h.Radio.Set(RadioStatus.Off);
            h.Advance(TimeSpan.FromMinutes(1));
            if (h.Controller.Paused) break;
        }
        Assert.True(h.Controller.Paused);
        Assert.Equal(RecoveryBackoff.MaxEnablesPerWindow * RecoveryBackoff.MaxFullWindows, h.Radio.SwitchOns);
        Assert.Equal("paused", h.Phases[^1]);
        h.Radio.Set(RadioStatus.Off);
        h.Advance(TimeSpan.FromHours(2));
        Assert.Equal(RecoveryBackoff.MaxEnablesPerWindow * RecoveryBackoff.MaxFullWindows, h.Radio.SwitchOns);
    }

    [Fact]
    public void SlowWindows_TheEndedCountdownStillSwitchesOn_Once()
    {
        var h = new Harness { OtherInput = true };
        h.StartedThenSwitchedOff();
        // Windows is slow to answer after "now"; the countdown running out meanwhile changes nothing.
        var hold = new TaskCompletionSource<RadioStatus>();
        h.Radio.Hold = hold;
        h.Controller.SkipCountdown();
        h.RunOutCountdown();
        Assert.Equal(0, h.Radio.SwitchOns);
        h.Radio.Hold = null;
        hold.SetResult(RadioStatus.Off);
        Assert.Equal(1, h.Radio.SwitchOns);
        Assert.Equal(["countdown", "enabled"], h.Phases);
    }

    [Fact]
    public void RerunWhileBusy_RunsOnceMore()
    {
        var h = new Harness();
        h.Radio.Status = RadioStatus.On;
        h.Controller.Start();
        var hold = new TaskCompletionSource<RadioStatus>();
        h.Radio.Hold = hold;
        h.Advance(BluetoothRecoveryController.StartDelay);
        // The start check waits; meanwhile the radio goes off.
        h.Radio.Status = RadioStatus.Off;
        h.Radio.Hold = null;
        h.Radio.Set(RadioStatus.Off);
        h.Advance(BluetoothRecoveryController.Settle);
        Assert.Equal(0, h.Radio.SwitchOns);
        hold.SetResult(RadioStatus.On);
        Assert.Equal(1, h.Radio.SwitchOns);
    }

    [Fact]
    public void UserSwitchesOffInCouchHud_WithOtherInput_StaysOff()
    {
        var h = new Harness();
        h.Controller.Start();
        h.Advance(BluetoothRecoveryController.StartDelay);
        Assert.True(h.Controller.UserSwitchingOff(otherInput: true));
        h.Radio.Set(RadioStatus.Off);
        h.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(0, h.Radio.SwitchOns);
        Assert.True(h.Controller.Paused);
    }

    [Fact]
    public void UserSwitchesOffInCouchHud_WithoutOtherInput_ComesBack()
    {
        var h = new Harness();
        h.Controller.Start();
        h.Advance(BluetoothRecoveryController.StartDelay);
        Assert.False(h.Controller.UserSwitchingOff(otherInput: false));
        h.Radio.Set(RadioStatus.Off);
        h.Advance(BluetoothRecoveryController.Settle);
        Assert.Equal(1, h.Radio.SwitchOns);
    }

    [Fact]
    public void UserSwitchOffFails_NothingLeftOff()
    {
        var h = new Harness();
        h.Controller.Start();
        Assert.True(h.Controller.UserSwitchingOff(otherInput: true));
        h.Controller.UserSwitchOffFailed();
        Assert.False(h.Controller.Paused);
    }

    [Fact]
    public void Dispose_StopsListening_AndPendingTimersDoNothing()
    {
        var h = new Harness { OtherInput = true };
        h.StartedThenSwitchedOff();
        h.Controller.Dispose();
        Assert.False(h.Radio.Subscribed);
        h.RunOutCountdown();
        h.Radio.Set(RadioStatus.Off);
        h.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, h.Radio.SwitchOns);
        Assert.Equal(["countdown"], h.Phases);
    }

    [Fact]
    public void PairedControllersAreRemembered_WhileOn_AndStandIn_WhenWindowsListsNoneWithTheRadioOff()
    {
        var h = new Harness();
        h.Controller.Start();
        Assert.Equal(1, h.Remembered);
        h.Advance(BluetoothRecoveryController.StartDelay);
        // Radio off: Windows lists no paired controller any more, the remembered one counts.
        h.Radio.Paired = 0;
        h.Radio.Set(RadioStatus.Off);
        h.Advance(BluetoothRecoveryController.Settle);
        Assert.Equal(1, h.Radio.SwitchOns);
    }

    [Fact]
    public void AfterARestartWithTheRadioOff_TheRememberedControllerCounts()
    {
        var h = new Harness { Remembered = 1 };
        h.Radio.Status = RadioStatus.Off;
        h.Radio.Paired = 0;
        h.Controller.Start();
        h.Advance(BluetoothRecoveryController.StartDelay);
        Assert.Equal(1, h.Radio.SwitchOns);
    }

    [Fact]
    public void NoControllerEverSeen_StaysOff()
    {
        var h = new Harness();
        h.Radio.Status = RadioStatus.Off;
        h.Radio.Paired = 0;
        h.Controller.Start();
        h.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, h.Radio.SwitchOns);
        Assert.Empty(h.Phases);
    }
}
