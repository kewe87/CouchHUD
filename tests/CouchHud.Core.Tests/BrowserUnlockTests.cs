using CouchHud.Core.Security;

namespace CouchHud.Core.Tests;

/// <summary>
/// The couch browser's unlock is extended by a person's input only. A page that changes its title, address or sound every few seconds
/// kept the couch unlocked for good in 0.4.1 and 0.4.2; such page events do not reach <see cref="BrowserUnlock.Next"/> at all now.
/// </summary>
public sealed class BrowserUnlockTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 27, 20, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(500);

    /// <summary>Runs the twice-a-second check for <paramref name="span"/>; <paramref name="input"/> says per moment whether a person did something.</summary>
    private static (DateTimeOffset Until, int Warnings, DateTimeOffset End) Run(TimeSpan span, Func<DateTimeOffset, (DateTimeOffset LastInput, bool InputNow)> input)
    {
        // Unlocked with the code and the browser opened at the same moment.
        var until = Start + BrowserUnlock.Extension;
        var warned = false;
        var warnings = 0;
        var now = Start;
        for (; now < Start + span; now += Tick)
        {
            var (last, inputNow) = input(now);
            var d = BrowserUnlock.Next(now, until, last, inputNow, warned);
            until = d.UnlockedUntil;
            warned = d.Warned;
            if (d.Warn) warnings++;
            if (until <= now) break;
        }
        return (until, warnings, now);
    }

    [Fact]
    public void OnlyPageEventsLetTheUnlockRunOut()
    {
        // The page ticks its title every 5 s for 10 minutes: none of it is input, the last input is opening the browser.
        var (until, warnings, end) = Run(TimeSpan.FromMinutes(10), _ => (Start, false));
        Assert.True(until <= end);
        Assert.True(until <= Start + BrowserUnlock.Extension + TimeSpan.FromSeconds(30), $"ran until {until - Start}");
        Assert.Equal(1, warnings);
    }

    [Fact]
    public void ControllerInputExtends()
    {
        // A button every 20 s: still unlocked after 10 minutes, no warning.
        var (until, warnings, end) = Run(TimeSpan.FromMinutes(10), now => (Start + TimeSpan.FromSeconds(Math.Floor((now - Start).TotalSeconds / 20) * 20), false));
        Assert.True(until > end);
        Assert.Equal(0, warnings);
    }

    [Fact]
    public void RealMouseExtends()
    {
        var (until, warnings, end) = Run(TimeSpan.FromMinutes(10), _ => (DateTimeOffset.MinValue, true));
        Assert.True(until > end);
        Assert.Equal(0, warnings);
    }

    [Fact]
    public void InputAfterTheWarningExtendsAndAllowsAnotherWarning()
    {
        var now = Start + TimeSpan.FromSeconds(95);
        var until = Start + BrowserUnlock.Extension;
        var warn = BrowserUnlock.Next(now, until, Start, false, false);
        Assert.True(warn.Warn);
        Assert.False(BrowserUnlock.Next(now + Tick, until, Start, false, warn.Warned).Warn);
        var used = BrowserUnlock.Next(now + Tick, until, now + Tick, false, warn.Warned);
        Assert.Equal(now + Tick + BrowserUnlock.Extension, used.UnlockedUntil);
        Assert.False(used.Warned);
    }

    [Fact]
    public void InputLongerAgoThanTheWindowDoesNotCount()
    {
        var now = Start + TimeSpan.FromSeconds(100);
        var until = Start + BrowserUnlock.Extension;
        var d = BrowserUnlock.Next(now, until, now - BrowserUnlock.InputWindow, false, true);
        Assert.Equal(until, d.UnlockedUntil);
    }

    [Fact]
    public void InputStampedInTheFutureDoesNotCount()
    {
        // age < 0 (a clock jump, a stamp from another clock): never taken as use, the unlock runs out as without input.
        var now = Start + TimeSpan.FromSeconds(100);
        var until = Start + BrowserUnlock.Extension;
        var d = BrowserUnlock.Next(now, until, now + TimeSpan.FromSeconds(10), false, true);
        Assert.Equal(until, d.UnlockedUntil);
        Assert.False(d.Warn);
        var expired = BrowserUnlock.Next(until, until, until + TimeSpan.FromMinutes(5), false, true);
        Assert.Equal(until, expired.UnlockedUntil);
    }

    [Fact]
    public void AnExpiredUnlockIsNeverRevived()
    {
        var now = Start + TimeSpan.FromMinutes(5);
        var until = Start + BrowserUnlock.Extension;
        var d = BrowserUnlock.Next(now, until, now, true, false);
        Assert.Equal(until, d.UnlockedUntil);
        Assert.False(d.Warn);
    }

    [Fact]
    public void TheWarningCountsDownUntilInputAndIsGoneAfterIt()
    {
        var until = Start + BrowserUnlock.Extension;
        // Idle: no warning before the last 30 s.
        var early = BrowserUnlock.Next(Start + TimeSpan.FromSeconds(60), until, Start, false, false);
        Assert.Null(BrowserUnlock.WarningLeft(Start + TimeSpan.FromSeconds(60), early.UnlockedUntil, early.Warned));
        // 30 s before the end: the warning with the time left, counting down on the following checks.
        var at = until - BrowserUnlock.WarnBefore;
        var warn = BrowserUnlock.Next(at, until, Start, false, false);
        Assert.True(warn.Warn);
        Assert.Equal(BrowserUnlock.WarnBefore, BrowserUnlock.WarningLeft(at, warn.UnlockedUntil, warn.Warned));
        var later = BrowserUnlock.Next(at + TimeSpan.FromSeconds(10), warn.UnlockedUntil, Start, false, warn.Warned);
        Assert.False(later.Warn);
        Assert.Equal(TimeSpan.FromSeconds(20), BrowserUnlock.WarningLeft(at + TimeSpan.FromSeconds(10), later.UnlockedUntil, later.Warned));
        // Any key (a real one, or the controller): extended, and the warning is gone.
        var now = at + TimeSpan.FromSeconds(12);
        var pressed = BrowserUnlock.Next(now, later.UnlockedUntil, now, false, later.Warned);
        Assert.False(pressed.Warned);
        Assert.Equal(now + BrowserUnlock.Extension, pressed.UnlockedUntil);
        Assert.Null(BrowserUnlock.WarningLeft(now, pressed.UnlockedUntil, pressed.Warned));
    }

    [Fact]
    public void PageEventsDoNotEndTheWarning()
    {
        // The page goes on playing (title, sound, full screen): none of it is input, the warning stays until the end.
        var until = Start + BrowserUnlock.Extension;
        var at = until - BrowserUnlock.WarnBefore;
        var d = BrowserUnlock.Next(at, until, Start, false, false);
        for (var now = at; now < until; now += Tick)
        {
            d = BrowserUnlock.Next(now, d.UnlockedUntil, Start, false, d.Warned);
            Assert.True(d.Warned);
            Assert.NotNull(BrowserUnlock.WarningLeft(now, d.UnlockedUntil, d.Warned));
        }
        Assert.Null(BrowserUnlock.WarningLeft(until, d.UnlockedUntil, d.Warned));
    }
}
