using CouchHud.Core.Guard;
using CouchHud.Core.Library;
using CouchHud.Core.Parental;
using CouchHud.Core.Security;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>The locked foreground after the audit: "own" is the WebView2 helper only, apps are released explicitly.</summary>
public sealed class LockedOwnTreeTests
{
    private static readonly ForegroundContext Locked = new(true, false, false, false, false, false, Locked: true);

    private static LockedWindow Win(string? process, bool ownTree = false, bool gameTree = false, bool appTree = false, bool clientTree = false) =>
        new(process, "", false, false, () => ownTree, () => gameTree, () => false)
        {
            AppTree = () => appTree,
            ClientTree = () => clientTree
        };

    [Theory]
    [InlineData("steam")]
    [InlineData("steamwebhelper")]
    [InlineData("EpicGamesLauncher")]
    [InlineData("GalaxyClient")]
    [InlineData("msedge")]
    [InlineData("chrome")]
    [InlineData("notepad")]
    [InlineData("explorer")]
    public void WhateverCouchHudStartedIsNotOwnJustForThat(string process)
    {
        var d = LockedForeground.Decide(Locked, Win(process, ownTree: true));
        Assert.True(d.Suppress, process);
    }

    [Fact]
    public void OnlyTheWebViewHelpersOfCouchHudAreOwn()
    {
        Assert.False(LockedForeground.Decide(Locked, Win("msedgewebview2", ownTree: true)).Suppress);
        Assert.Equal(ForegroundReason.OwnWindow, LockedForeground.Decide(Locked, Win("msedgewebview2.exe", ownTree: true)).Reason);
        Assert.True(LockedForeground.IsOwnWebViewHelper("msedgewebview2"));
        Assert.False(LockedForeground.IsOwnWebViewHelper("msedge"));
        // Not a descendant of CouchHud: not own either, and not a game, so unknown (left alone as before).
        Assert.Equal(ForegroundReason.LockedUnknown, LockedForeground.Decide(Locked, Win("msedgewebview2")).Reason);
    }

    [Fact]
    public void TheGamesOwnProcessesStayAlsoWhenTheyAreBrowsersOrClients()
    {
        Assert.False(LockedForeground.Decide(Locked, Win("msedge", gameTree: true)).Suppress);
        Assert.Equal(ForegroundReason.LockedGame, LockedForeground.Decide(Locked, Win("somegame", gameTree: true)).Reason);
    }

    [Fact]
    public void AClientIsAllowedInTheStartPhaseOnlyNotJustBecauseCouchHudStartedIt()
    {
        var starting = Locked with { GameStarting = true, Released = ["steam"] };
        Assert.False(LockedForeground.Decide(starting, Win("steam", ownTree: true)).Suppress);
        // After the start phase the same client, still a child of CouchHud, is held back.
        Assert.True(LockedForeground.Decide(Locked with { Released = ["steam"] }, Win("steam", ownTree: true)).Suppress);
        Assert.True(LockedForeground.Decide(Locked with { GameRunning = true, StrictClients = true }, Win("steam", ownTree: true)).Suppress);
    }

    [Fact]
    public void AnAppTheParentsStartedStaysUntilTheHudIsBack()
    {
        var released = Locked with { AppsReleased = true };
        var d = LockedForeground.Decide(released, Win("msedge", appTree: true));
        Assert.False(d.Suppress);
        Assert.Equal(ForegroundReason.LockedAppReleased, d.Reason);
        // The release ended (ShowHud): the same browser is a known non-game again.
        Assert.True(LockedForeground.Decide(Locked, Win("msedge", appTree: true)).Suppress);
        // Only what the app started, not any other browser window.
        Assert.True(LockedForeground.Decide(released, Win("msedge", appTree: false)).Suppress);
    }
}

public sealed class GameEntryRulesTests
{
    private const string Win = @"C:\Windows";

    [Theory]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"C:\windows\system32\notepad.exe")]
    [InlineData(@"C:\Windows\SysWOW64\mspaint.exe")]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe")]
    [InlineData(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe")]
    [InlineData(@"C:\Tools\powershell.exe")]
    [InlineData(@"D:\x\explorer.exe")]
    [InlineData(@"D:\Steam\steam.exe")]
    [InlineData("")]
    public void ShellsBrowsersWindowsToolsAndClientsAreNeverGames(string path) =>
        Assert.False(GameEntryRules.MayBeGame(path, Win));

    [Theory]
    [InlineData(@"D:\Games\Hades\Hades.exe")]
    [InlineData(@"C:\Program Files\Foo\foo.exe")]
    [InlineData(@"C:\WindowsGames\foo.exe")]
    public void OrdinaryProgramsMayBeGames(string path) => Assert.True(GameEntryRules.MayBeGame(path, Win));

    [Theory]
    [InlineData("https://example.org", false)]
    [InlineData("HTTP://example.org", false)]
    [InlineData("file:///C:/x.exe", false)]
    [InlineData("ms-settings:display", false)]
    [InlineData("shell:appsfolder\\x", false)]
    [InlineData("steam://rungameid/1", true)]
    [InlineData("com.epicgames.launcher://apps/x", true)]
    [InlineData("", false)]
    public void LinksThatAreWebPagesOrWindowsToolsAreNoGames(string url, bool expected) =>
        Assert.Equal(expected, GameEntryRules.MayBeGameUrl(url));

    [Theory]
    [InlineData(@"D:\Games", true)]
    [InlineData(@"D:\", false)]
    [InlineData(@"C:\Windows", false)]
    [InlineData(@"C:\Windows\System32", false)]
    [InlineData(@"C:\Users\player\Games", true)]
    public void AWatchedFolderIsNoWholeDriveAndNoWindowsFolder(string folder, bool expected) =>
        Assert.Equal(expected, GameEntryRules.MayBeGameFolder(folder, Win));
}

public sealed class CrashLoopTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-crashloop-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private CrashTracker Tracker() => new(_dir, now: () => _now);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void ThreeUncleanEndsCloseTogetherAreACrashLoop()
    {
        Assert.False(Tracker().RegisterStart());
        _now = _now.AddMinutes(1);
        Assert.False(Tracker().RegisterStart());
        _now = _now.AddMinutes(1);
        Assert.False(Tracker().RegisterStart());
        _now = _now.AddMinutes(1);
        Assert.True(Tracker().RegisterStart());
    }

    [Fact]
    public void UncleanEndsFarApartDoNotAddUp()
    {
        Tracker().RegisterStart();
        _now = _now.AddHours(1);
        Tracker().RegisterStart();           // counts the first
        _now = _now.AddHours(8);             // the first one is out of the window by now
        Assert.False(Tracker().RegisterStart());
        _now = _now.AddHours(1);
        Assert.False(Tracker().RegisterStart());
        Assert.True(Tracker().Consecutive() <= 2);
    }

    [Fact]
    public void ACountFileFromTheFutureOrWithGarbageCountsForNothing()
    {
        Directory.CreateDirectory(_dir);
        var future = _now.AddDays(3).ToUnixTimeMilliseconds();
        File.WriteAllLines(Path.Combine(_dir, "unclean.count"), [future.ToString(), future.ToString(), "garbage", "-5"]);
        Assert.Equal(0, Tracker().Consecutive());
        Assert.False(Tracker().RegisterStart());
    }

    [Fact]
    public void TheOldBareCounterStillCounts()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "unclean.count"), "2");
        Assert.Equal(2, Tracker().Consecutive());
        File.WriteAllText(Path.Combine(_dir, "running.marker"), "1");
        Assert.True(Tracker().RegisterStart());
    }
}

public sealed class RecoveryRulesTests
{
    [Theory]
    [InlineData("code", true)]
    [InlineData("windows", true)]
    [InlineData("off", false)]
    [InlineData("OFF", false)]
    public void WithTheCouchLockTheRecoveryWaitsForTheWindowsSignIn(string lockMode, bool expected) =>
        Assert.Equal(expected, RecoveryRules.MustAuthenticateFirst(lockMode));

    [Theory]
    [InlineData("code", true, false, true)]
    [InlineData("windows", true, false, true)]
    [InlineData("code", true, true, false)]    // CouchHud is the shell: it locks itself
    [InlineData("code", false, false, false)]  // Windows asks for the password anyway
    [InlineData("off", true, false, false)]
    public void ARunEntryLocksAtSignInWhileLockAndAutoSignInStandWithoutTheShell(string lockMode, bool autoLogon, bool shell, bool expected) =>
        Assert.Equal(expected, RecoveryRules.WantsSignInLock(lockMode, autoLogon, shell));

    [Fact]
    public void ParentalControlsNeedAnAccountWithAPassword()
    {
        Assert.True(RecoveryRules.ParentalControlsAllowed(true));
        Assert.False(RecoveryRules.ParentalControlsAllowed(false));
    }
}

public sealed class ClockAndGrantTests
{
    private sealed class Ticks
    {
        public long Ms = 5_000;
    }

    [Fact]
    public void TheMonotonicClockIgnoresTheSystemClockAfterTheStart()
    {
        var ticks = new Ticks();
        var start = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var clock = new MonotonicClock(start, () => ticks.Ms);
        ticks.Ms += 90_000;
        Assert.Equal(start.AddSeconds(90), clock.UtcNow);
        ticks.Ms -= 200_000; // a counter that went backwards never moves time back
        Assert.Equal(start, clock.UtcNow);
    }

    [Fact]
    public void AUnlockWindowFollowsTheTicksNotTheWallClock()
    {
        var ticks = new Ticks();
        var clock = new MonotonicClock(DateTimeOffset.UtcNow, () => ticks.Ms);
        var until = clock.UtcNow.AddMinutes(2);
        ticks.Ms += 119_000;
        Assert.True(clock.UtcNow < until);
        ticks.Ms += 2_000;
        Assert.False(clock.UtcNow < until);
    }

    private static (ParentGrant Grant, Func<DateTimeOffset> Now, Action<TimeSpan> Advance) NewGrant()
    {
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        return (new ParentGrant(() => now, TimeSpan.FromSeconds(90)), () => now, t => now += t);
    }

    [Fact]
    public void AGrantFitsOnlyItsActionAndEntryAndIsUsedUpOnce()
    {
        var (grant, _, _) = NewGrant();
        grant.Give("launch", "steam:1");
        Assert.False(grant.TryConsume("extend"));
        Assert.False(grant.TryConsume("launch", "steam:2"));
        Assert.True(grant.Fits("launch", "steam:1"));
        Assert.True(grant.TryConsume("launch", "steam:1"));
        Assert.False(grant.TryConsume("launch", "steam:1"));
    }

    [Fact]
    public void AGrantWithoutScopeFitsAnythingOnce()
    {
        var (grant, _, _) = NewGrant();
        grant.Give();
        Assert.True(grant.TryConsume("profile"));
        Assert.False(grant.TryConsume("profile"));
    }

    [Fact]
    public void AGrantRunsOutAndCanBeCleared()
    {
        var (grant, _, advance) = NewGrant();
        grant.Give("extend");
        advance(TimeSpan.FromSeconds(91));
        Assert.False(grant.TryConsume("extend"));
        grant.Give("extend");
        grant.Clear();
        Assert.False(grant.Active);
    }

    [Fact]
    public void ALeftOverGrantOfAFailedStartDoesNotOpenTheNextGame()
    {
        var (grant, _, _) = NewGrant();
        grant.Give("launch", "game:a");
        // game a never started (close the running game first …), the next start is another entry
        Assert.False(grant.TryConsume("launch", "game:b"));
    }

    private static (WindowsUnlockRequest Request, Action<TimeSpan> Advance) NewRequest()
    {
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        return (new WindowsUnlockRequest(() => now), t => now += t);
    }

    [Fact]
    public void AnUnlockAnswersOnlyALockThatCameRightAfterTheRequest()
    {
        var (request, advance) = NewRequest();
        request.Ask();
        advance(TimeSpan.FromSeconds(1));
        request.OnLocked();
        advance(TimeSpan.FromSeconds(20));
        Assert.True(request.OnUnlocked());
        Assert.False(request.OnUnlocked()); // used up
    }

    [Fact]
    public void AnUnlockWithoutOurLockDoesNothing()
    {
        var (request, advance) = NewRequest();
        request.Ask();
        advance(TimeSpan.FromSeconds(5));
        // Windows Hello unlocked a lock that was not ours (no lock event between the request and now).
        Assert.False(request.OnUnlocked());
        Assert.True(request.Pending);
    }

    [Fact]
    public void ALaterLockFromAnotherSourceCancelsTheRequest()
    {
        var (request, advance) = NewRequest();
        request.Ask();
        advance(TimeSpan.FromSeconds(1));
        request.OnLocked();           // ours
        advance(TimeSpan.FromSeconds(30));
        request.OnLocked();           // standby or the guard locked again
        advance(TimeSpan.FromSeconds(1));
        Assert.False(request.OnUnlocked());
        Assert.False(request.Pending);
    }

    [Fact]
    public void TheRequestExpiresAfterThreeMinutes()
    {
        var (request, advance) = NewRequest();
        request.Ask();
        advance(TimeSpan.FromSeconds(1));
        request.OnLocked();
        advance(TimeSpan.FromMinutes(3).Add(TimeSpan.FromSeconds(1)));
        Assert.False(request.OnUnlocked());
        Assert.False(request.Pending);
    }

    [Fact]
    public void ACancelledRequestIsGone()
    {
        var (request, _) = NewRequest();
        request.Ask();
        request.Cancel();
        Assert.False(request.Pending);
    }

    [Fact]
    public void TheWaitAfterWrongCodesIsCappedAndIndependentPerCounter()
    {
        var dir = Path.Combine(Path.GetTempPath(), "couchhud-lockout-" + Guid.NewGuid().ToString("N"));
        try
        {
            var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
            var couch = new Lockout(() => now, Path.Combine(dir, "a.json"));
            var parent = new Lockout(() => now, Path.Combine(dir, "b.json"));
            for (var i = 0; i < 12; i++) couch.Failed();
            Assert.True(couch.Wait > TimeSpan.Zero);
            Assert.Equal(TimeSpan.Zero, parent.Wait); // the parents are not locked out by the child's tries
            Assert.True(couch.Wait <= TimeSpan.FromMinutes(30));
            // A stored time far in the future (clock was turned back before the restart) is capped at load.
            now = now.AddDays(-10);
            var reloaded = new Lockout(() => now, Path.Combine(dir, "a.json"));
            Assert.True(reloaded.Wait <= TimeSpan.FromMinutes(30));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}

public sealed class ParentalAuditTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-parental-audit-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private sealed class Machine
    {
        public DateTimeOffset System = new(2026, 10, 19, 15, 0, 0, TimeSpan.Zero); // a Monday
        public long Mono = 1_000_000;

        public void Advance(TimeSpan t)
        {
            System += t;
            Mono += (long)t.TotalMilliseconds;
        }

        public TrustedClock Clock(DateTimeOffset? persisted = null) => new(() => System, () => Mono, _ => TimeSpan.Zero, persisted);
    }

    [Fact]
    public void ExtraTimeBeforeMidnightStretchesTheWindowButNotTheNextDaysLimits()
    {
        var m = new Machine { System = new DateTimeOffset(2026, 10, 19, 23, 30, 0, TimeSpan.Zero) };
        var config = new ParentalConfig { Enabled = true, WeekdayMinutes = 60, WeekendMinutes = 60, WindowStartMinute = 22 * 60, WindowEndMinute = 60 };
        var ledger = new PlayLedger();
        var s = new ParentalService(() => config, ledger, m.Clock());
        s.Extend(60);                       // 23:30, window now until 02:00
        m.Advance(TimeSpan.FromMinutes(90)); // 01:00 on the next day, past the old end
        s.Tick("g");
        var status = s.Status("g");
        Assert.True(status.InWindow);
        Assert.InRange(status.RemainingSeconds!.Value, 55 * 60, 60 * 60); // the window runs to 02:00
        var newDay = new DateOnly(2026, 10, 20);
        Assert.Equal(0, ledger.BonusMinutes(newDay)); // nothing carried into the new day's own limits
        // The new day's game limit is 45 minutes, not 105: 46 minutes of this game end it.
        config.PerGameMinutes = 45;
        ledger.Add(newDay, "g", 46 * 60);
        var after = s.Status("g");
        Assert.True(after.RemainingSeconds <= 0);
        Assert.Equal("game", after.Limiter);
    }

    [Fact]
    public void TheCarryLeavesAfterTheWindowAndNeverAppliesOutsideIt()
    {
        var m = new Machine { System = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero) };
        var config = new ParentalConfig { Enabled = true, WeekdayMinutes = 0, WindowStartMinute = 22 * 60, WindowEndMinute = 60 };
        var ledger = new PlayLedger();
        ledger.AddBonus(new DateOnly(2026, 10, 19), 60);
        var s = new ParentalService(() => config, ledger, m.Clock());
        Assert.False(s.Status("g").InWindow); // noon: yesterday's bonus does not make this the window
    }

    [Fact]
    public void AClockTurnedForwardAcrossARestartIsNotBelievedUntilConfirmed()
    {
        var m = new Machine();
        var persisted = m.System;
        m.Advance(TimeSpan.FromDays(1)); // system clock one day ahead after the "restart"
        var clock = m.Clock(persisted);
        Assert.True(clock.Jumped);
        Assert.Equal(persisted, clock.UtcNow);
        Assert.Equal(new DateOnly(2026, 10, 19), clock.Today);
        clock.AcceptSystemTime(); // the web server's date agrees
        Assert.False(clock.Jumped);
        Assert.Equal(new DateOnly(2026, 10, 20), clock.Today);
    }

    [Fact]
    public void AClockThatAgreesAtTheStartIsTakenAndOneSetBackStaysAhead()
    {
        var m = new Machine();
        var normal = m.Clock(m.System - TimeSpan.FromSeconds(40));
        Assert.False(normal.Jumped);
        var behind = m.Clock(m.System + TimeSpan.FromHours(3)); // persisted is ahead of the (set back) system clock
        Assert.Equal(m.System + TimeSpan.FromHours(3), behind.UtcNow);
        Assert.Equal(m.System, m.Clock().UtcNow); // first run: the system clock
    }

    [Fact]
    public void TheLedgerIsWrittenThroughAndKeepsACopyOfTheStateBefore()
    {
        var file = Path.Combine(_dir, "playtime.json");
        var key = new byte[] { 9, 8, 7, 6, 5, 4, 3, 2, 1, 1, 2, 3, 4, 5, 6, 7 };
        var day = new DateOnly(2026, 10, 19);
        var a = new PlayLedger(file, key);
        a.Add(day, "g", 600);
        a.Save(day);
        a.Add(day, "g", 600);
        a.Save(day);
        Assert.True(File.Exists(file + ".bak"));
        Assert.False(File.Exists(file + ".tmp"));
        Assert.Equal(1200, new PlayLedger(file, key, expectExisting: true).Seconds(day), 3);
    }

    [Fact]
    public void ATornFileFallsBackToTheCopyButAChangedOneIsStillTampering()
    {
        var file = Path.Combine(_dir, "playtime.json");
        var key = new byte[] { 9, 8, 7, 6, 5, 4, 3, 2, 1, 1, 2, 3, 4, 5, 6, 7 };
        var day = new DateOnly(2026, 10, 19);
        var ledger = new PlayLedger(file, key);
        ledger.Add(day, "g", 600);
        ledger.Save(day);
        ledger.Add(day, "g", 300);
        ledger.Save(day); // the .bak holds the 900 s state as well (refreshed after every save)

        // A power cut left half a file: the signed copy is used, with the last save, the day is not "used up".
        File.WriteAllText(file, "{\"Days\":{\"2026-10-19\":");
        var recovered = new PlayLedger(file, key, expectExisting: true);
        Assert.False(recovered.Tampered);
        Assert.Equal(900, recovered.Seconds(day), 3);

        // A complete file with a changed number is tampering, the copy does not save the child here.
        ledger.Save(day);
        File.WriteAllText(file, File.ReadAllText(file).Replace("900", "1"));
        Assert.True(new PlayLedger(file, key, expectExisting: true).Tampered);
    }

    [Fact]
    public void ADeletedFileWithoutACopyIsStillTampering()
    {
        var file = Path.Combine(_dir, "playtime.json");
        var key = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
        Assert.True(new PlayLedger(file, key, expectExisting: true).Tampered);
        Assert.False(new PlayLedger(file, key, expectExisting: false).Tampered);
    }
}

public sealed class SessionTrackerAuditTests
{
    private static readonly string Games = Path.Combine(Path.GetTempPath(), "couchhud-audit", "Games");

    private sealed class FakeProbe : IProcessProbe
    {
        public List<(int, string?)> Processes { get; } = [];
        public Dictionary<int, int> Parents { get; } = [];
        public int SteamApp { get; set; }

        public IReadOnlyList<(int Pid, string? Path)> Snapshot() => Processes.ToList();

        public int SteamRunningAppId() => SteamApp;

        public IReadOnlyDictionary<int, int> ParentMap() => Parents;
    }

    private static LibraryItem Game(string id, string? dir, string target = "x://") => new()
    {
        Id = id,
        Source = id.Split(':')[0],
        Name = id,
        InstallDir = dir,
        Launch = new LaunchTarget(LaunchKind.Url, target)
    };

    [Fact]
    public void AStaleSteamValueWithoutSteamIsNoSession()
    {
        var probe = new FakeProbe { SteamApp = 1245620 };
        var tracker = new GameSessionTracker(probe);
        var t0 = DateTimeOffset.UtcNow;
        tracker.Launched(Game("steam:1245620", Path.Combine(Games, "ELDEN RING")), t0);
        tracker.Tick(t0.AddSeconds(5));
        Assert.Null(tracker.Current); // no steam.exe: the value is left over from a crash

        probe.Processes.Add((4, Path.Combine("C:", "Program Files (x86)", "Steam", "steam.exe")));
        tracker.Tick(t0.AddSeconds(10));
        Assert.NotNull(tracker.Current);
    }

    [Fact]
    public void AStaleSteamValueDoesNotTurnALibraryScanIntoASession()
    {
        var probe = new FakeProbe { SteamApp = 77 };
        var tracker = new GameSessionTracker(probe) { Library = () => [Game("steam:77", Path.Combine(Games, "Foo"))] };
        tracker.Tick(DateTimeOffset.UtcNow);
        Assert.Null(tracker.Current);
        Assert.Empty(tracker.Others);
    }

    [Fact]
    public void WhatALauncherStartsKeepsTheSessionAliveAfterTheLauncherEnds()
    {
        var probe = new FakeProbe();
        var tracker = new GameSessionTracker(probe);
        var ended = new List<double>();
        tracker.Ended += (_, m) => ended.Add(m);
        var t0 = DateTimeOffset.UtcNow;
        var launcher = Path.Combine(Games, "Mine", "launcher.exe");
        var java = Path.Combine(Path.GetTempPath(), "couchhud-audit", "runtime", "javaw.exe");
        tracker.Launched(Game("custom:mine", Path.Combine(Games, "Mine")), t0);
        probe.Processes.Add((10, launcher));
        probe.Processes.Add((11, java));
        probe.Parents[11] = 10;
        tracker.Tick(t0.AddSeconds(5));
        Assert.Equal([10, 11], tracker.Current!.Pids.OrderBy(p => p));

        probe.Processes.RemoveAt(0); // the launcher ended, java runs on
        tracker.Tick(t0.AddSeconds(10));
        Assert.NotNull(tracker.Current);
        Assert.Equal([11], tracker.Current!.Pids);
        Assert.Empty(ended);

        probe.Processes.Clear();
        tracker.Tick(t0.AddSeconds(15));
        Assert.Null(tracker.Current);
        Assert.Single(ended);
    }

    [Fact]
    public void AReusedPidWithAnotherImageIsNotTheGame()
    {
        var probe = new FakeProbe();
        var tracker = new GameSessionTracker(probe);
        var t0 = DateTimeOffset.UtcNow;
        tracker.Launched(Game("custom:mine", Path.Combine(Games, "Mine")), t0);
        probe.Processes.Add((10, Path.Combine(Games, "Mine", "launcher.exe")));
        probe.Processes.Add((11, Path.Combine(Path.GetTempPath(), "x", "javaw.exe")));
        probe.Parents[11] = 10;
        tracker.Tick(t0.AddSeconds(5));
        probe.Processes.Clear();
        probe.Processes.Add((11, Path.Combine(Path.GetTempPath(), "other", "notepad.exe"))); // the pid came back as something else
        tracker.Tick(t0.AddSeconds(10));
        Assert.Null(tracker.Current);
    }

    [Fact]
    public void ALaunchThroughCouchHudIsNotExternalButAFoundGameIs()
    {
        var probe = new FakeProbe();
        var a = Game("custom:a", Path.Combine(Games, "A"));
        var b = Game("custom:b", Path.Combine(Games, "B"));
        var tracker = new GameSessionTracker(probe) { Library = () => [a, b] };
        var started = new List<GameSession>();
        var others = new List<GameSession>();
        tracker.Started += started.Add;
        tracker.OtherStarted += others.Add;
        var t0 = DateTimeOffset.UtcNow;
        tracker.Launched(a, t0);
        probe.Processes.Add((1, Path.Combine(Games, "A", "a.exe")));
        tracker.Tick(t0.AddSeconds(3));
        Assert.False(Assert.Single(started).External);
        Assert.Empty(others);

        probe.Processes.Add((2, Path.Combine(Games, "B", "b.exe"))); // started by the client, not by CouchHud
        tracker.Tick(t0.AddSeconds(6));
        Assert.True(Assert.Single(others).External);
        tracker.Tick(t0.AddSeconds(9));
        Assert.Single(others); // told once

        var c = Game("custom:c", Path.Combine(Games, "C"));
        var lonely = new GameSessionTracker(new FakeProbe { Processes = { (3, Path.Combine(Games, "C", "c.exe")) } }) { Library = () => [c] };
        var adopted = new List<GameSession>();
        lonely.Started += adopted.Add;
        lonely.Tick(t0);
        Assert.True(Assert.Single(adopted).External);
    }

    [Fact]
    public void TheTrackerSurvivesTicksAndLaunchesFromTwoThreads()
    {
        var probe = new FakeProbe();
        probe.Processes.Add((1, Path.Combine(Games, "A", "a.exe")));
        var a = Game("custom:a", Path.Combine(Games, "A"));
        var tracker = new GameSessionTracker(probe) { Library = () => [a] };
        var t0 = DateTimeOffset.UtcNow;
        var errors = new List<Exception>();
        var ticker = Task.Run(() =>
        {
            try
            {
                for (var i = 0; i < 200; i++) tracker.Tick(t0.AddSeconds(i));
            }
            catch (Exception ex)
            {
                lock (errors) errors.Add(ex);
            }
        });
        for (var i = 0; i < 200; i++)
        {
            tracker.Launched(a, t0.AddSeconds(i));
            _ = tracker.IsPending(t0);
            _ = tracker.Current;
            _ = tracker.Others;
        }
        ticker.Wait();
        Assert.Empty(errors);
    }
}
