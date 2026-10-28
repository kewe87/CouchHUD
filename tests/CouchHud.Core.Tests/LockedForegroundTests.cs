using CouchHud.Core.Guard;

namespace CouchHud.Core.Tests;

public sealed class LockedForegroundTests
{
    private static readonly ForegroundContext Locked = new(true, GameRunning: false, MouseMode: false, PcMode: false, Browsing: false, LockPrompt: false,
        Locked: true);

    private static LockedWindow Win(string? process, string title = "", bool ownPid = false, bool dialog = false,
        bool ownTree = false, bool gameTree = false, bool fullscreen = false, bool clientTree = false, bool unreadable = false, string? cls = null) =>
        new(process, title, ownPid, dialog, () => ownTree, () => gameTree, () => fullscreen)
        {
            ClientTree = () => clientTree,
            Unreadable = unreadable,
            WindowClass = cls
        };

    private static ForegroundDecision Decide(LockedWindow w, ForegroundContext? ctx = null) => LockedForeground.Decide(ctx ?? Locked, w);

    // ---- CaseHUD's own window (fan control, lighting, design editor): settings, not a game

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void CaseHudWindowIsMinimizedWhileLocked(bool fullscreen, bool unreadable)
    {
        var d = Decide(Win("CaseHUD", fullscreen: fullscreen, unreadable: unreadable) with { PluginRestricted = () => true });
        Assert.Equal(ForegroundAction.Minimize, d.Action);
        Assert.Equal(ForegroundReason.LockedForeign, d.Reason);
    }

    [Fact]
    public void CaseHudIsToldByItsPathNotItsName()
    {
        // A program called CaseHUD.exe somewhere else is judged like any unknown program (could be a game).
        Assert.False(Decide(Win("CaseHUD")).Suppress);
        // The installed CaseHUD under another name is still CaseHUD.
        Assert.True(Decide(Win("renamed") with { PluginRestricted = () => true }).Suppress);
    }

    [Fact]
    public void CaseHudWindowIsLeftAloneWhenUnlockedAndItsFileDialogIsClosed()
    {
        Assert.False(Decide(Win("CaseHUD") with { PluginRestricted = () => true }, Locked with { Locked = false }).Suppress);
        Assert.Equal(ForegroundAction.CloseDialog, Decide(Win("CaseHUD", dialog: true) with { PluginRestricted = () => true }).Action);
        // CouchHud's own windows never count as CaseHUD.
        Assert.False(Decide(Win("CouchHud", ownPid: true) with { PluginRestricted = () => true }).Suppress);
    }

    [Fact]
    public void NothingIsTouchedWhenTheCouchIsNotLocked()
    {
        var d = Decide(Win("notepad"), Locked with { Locked = false });
        Assert.Equal(ForegroundReason.NotLocked, d.Reason);
        Assert.False(d.Suppress);
    }

    [Theory]
    [InlineData("notepad")]
    [InlineData("SystemSettings")]
    [InlineData("ApplicationFrameHost")]
    [InlineData("explorer")]
    [InlineData("cmd")]
    [InlineData("powershell")]
    [InlineData("chrome")]
    [InlineData("msedge")]
    [InlineData("firefox")]
    [InlineData("mmc")]
    [InlineData("regedit")]
    public void ClearlyNotGamesAreMinimized(string process)
    {
        var d = Decide(Win(process));
        Assert.Equal(ForegroundAction.Minimize, d.Action);
        Assert.Equal(ForegroundReason.LockedForeign, d.Reason);
    }

    [Fact]
    public void ConsoleWindowsOfTheOwnHelperTasksAreLeftAlone()
    {
        Assert.False(Decide(Win("reg", cls: "ConsoleWindowClass")).Suppress);
        Assert.False(Decide(Win("sc", cls: "ConsoleWindowClass")).Suppress);
        // Only their console window; every other console stays as it was (reg in some other window is unknown, thus left alone as before).
        Assert.False(Decide(Win("reg", cls: "SomethingElse")).Suppress);
        Assert.True(Decide(Win("cmd", cls: "ConsoleWindowClass")).Suppress);
        Assert.True(Decide(Win("somehost", cls: "ConsoleWindowClass")).Suppress);
    }

    [Fact]
    public void ShellsAreMinimizedEvenFullscreen()
    {
        Assert.True(Decide(Win("explorer", fullscreen: true)).Suppress);
        Assert.True(Decide(Win("chrome", fullscreen: true)).Suppress);
        Assert.True(Decide(Win("somehost", cls: "CabinetWClass")).Suppress);
        Assert.True(Decide(Win("somehost", cls: "ConsoleWindowClass")).Suppress);
    }

    [Theory]
    [InlineData("somegame", false)]
    [InlineData("somegame", true)]
    [InlineData(null, false)]
    public void ASmallPossibleGameIsNeverMinimized(string? process, bool fullscreen)
    {
        var d = Decide(Win(process, fullscreen: fullscreen));
        Assert.False(d.Suppress);
        Assert.Equal(fullscreen ? ForegroundReason.LockedFullscreen : ForegroundReason.LockedUnknown, d.Reason);
    }

    [Fact]
    public void AProcessWeCannotReadIsLeftAlone()
    {
        var d = Decide(Win("notepad", unreadable: true));
        Assert.False(d.Suppress);
        Assert.Equal(ForegroundReason.LockedUnknown, d.Reason);
    }

    [Theory]
    [InlineData("LockApp")]
    [InlineData("LogonUI")]
    [InlineData("consent")]
    [InlineData("consent.exe")]
    [InlineData("osk")]
    [InlineData("Narrator")]
    [InlineData("Magnify")]
    [InlineData("TextInputHost")]
    [InlineData("ShellHost")]
    [InlineData("SndVol")]
    [InlineData("ctfmon")]
    [InlineData("SecurityHealthSystray")]
    [InlineData("WerFault")]
    public void SignInUacAccessibilityAndFlyoutsAreOnThePositiveList(string process) =>
        Assert.Equal(ForegroundReason.LockedSystem, Decide(Win(process)).Reason);

    [Fact]
    public void OwnWindowsAndTheRunningGameStay()
    {
        Assert.False(Decide(Win("CouchHud", ownPid: true)).Suppress);
        Assert.False(Decide(Win("msedgewebview2", ownTree: true)).Suppress);
        var game = Decide(Win("eldenring", gameTree: true));
        Assert.False(game.Suppress);
        Assert.Equal(ForegroundReason.LockedGame, game.Reason);
        // A browser the game started is part of the game.
        Assert.False(Decide(Win("msedge", gameTree: true)).Suppress);
    }

    [Fact]
    public void ClientIsAllowedOnlyInTheStartPhaseOfItsOwnLaunch()
    {
        var starting = Locked with { GameStarting = true, Released = ["steam"] };
        Assert.False(Decide(Win("steamwebhelper"), starting).Suppress);
        // Another family, or no launch under way, or no release: down.
        Assert.True(Decide(Win("EpicGamesLauncher"), starting).Suppress);
        Assert.True(Decide(Win("steam"), Locked with { Released = ["steam"] }).Suppress);
        Assert.True(Decide(Win("steam"), Locked with { GameStarting = true }).Suppress);
    }

    [Fact]
    public void WhatAReleasedClientStartsIsTheGameInTheStartPhase()
    {
        var starting = Locked with { GameStarting = true, Released = ["steam"] };
        Assert.False(Decide(Win("notepad", clientTree: true), starting).Suppress);
        Assert.False(Decide(Win("chrome", clientTree: true), starting).Suppress);
        // Not in the start phase: the same browser is a known non-game again.
        Assert.True(Decide(Win("chrome", clientTree: true), Locked).Suppress);
    }

    [Fact]
    public void ReleaseEndsWhenTheGameIsThere()
    {
        // The game is up: the start phase is over (GameStarting false), the released family no longer counts.
        var running = Locked with { GameRunning = true, StrictClients = true, GameStarting = false, Released = ["steam"] };
        Assert.True(Decide(Win("steam"), running).Suppress);
    }

    [Fact]
    public void BigPictureIsClosedAndMinimizedOnALockedCouch()
    {
        var d = Decide(Win("steamwebhelper", "Steam Big Picture Mode"));
        Assert.Equal(ForegroundAction.CloseAndMinimize, d.Action);
    }

    [Fact]
    public void CapturingSuspendsTheGuard() =>
        Assert.False(Decide(Win("GameBar"), Locked with { Suspended = true }).Suppress);

    [Fact]
    public void FileDialogsAreClosedEvenInTheGameOrTheBrowserProcess()
    {
        Assert.Equal(ForegroundAction.CloseDialog, Decide(Win("steamwebhelper", dialog: true)).Action);
        Assert.Equal(ForegroundAction.CloseDialog, Decide(Win("msedgewebview2", dialog: true, ownTree: true)).Action);
        Assert.Equal(ForegroundAction.CloseDialog, Decide(Win("eldenring", dialog: true, gameTree: true)).Action);
        // The UAC and sign-in windows are never dialogs to close.
        Assert.False(Decide(Win("consent", dialog: true)).Suppress);
    }

    [Theory]
    [InlineData("#32770", "Öffnen", new[] { "SHELLDLL_DefView" }, true)]
    [InlineData("#32770", "Speichern unter", new[] { "DUIViewWndClassName", "Button" }, true)]
    [InlineData("#32770", "Ordner suchen", new[] { "SHBrowseForFolder ShellNameSpace Control" }, true)]
    [InlineData("#32770", "Drucken", new[] { "Button" }, true)]
    [InlineData("#32770", "Print", new string[0], true)]
    [InlineData("#32770", "Imprimer", new[] { "PrintUIToolbarWindow32" }, true)]
    [InlineData("#32770", "Imprimer", new[] { "SysTabControl32", "SysListView32", "Button" }, true)]
    [InlineData("#32770", "Fehler", new[] { "Button", "Static" }, false)]
    [InlineData("#32770", "Optionen", new[] { "SysTabControl32", "Button" }, false)]
    [InlineData("Notepad", "Drucken", new[] { "SHELLDLL_DefView" }, false)]
    [InlineData(null, null, new string[0], false)]
    public void RecognisesStandardFileAndPrintDialogs(string? windowClass, string? title, string[] children, bool expected) =>
        Assert.Equal(expected, LockedForeground.IsFileOrPrintDialog(windowClass, title, children));

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, false, false)]
    public void ExplorersEndOnlyWithoutTaskbarOnALockedShell(bool locked, bool asShell, bool tray, bool expected) =>
        Assert.Equal(expected, LockedForeground.ShouldEndExplorers(locked, asShell, tray));

    [Fact]
    public void ExplorerIsNeverAGame() => Assert.True(LockedForeground.IsNotGame("explorer.exe"));

    [Theory]
    [InlineData(true, false, false, false, true, true)]
    [InlineData(true, false, false, false, false, false)]
    [InlineData(true, true, false, false, true, false)]
    [InlineData(false, false, false, false, true, false)]
    [InlineData(true, false, true, true, false, true)]
    public void LockedCouchTakesTheXboxButtonWhateverTheOptionsSay(bool full, bool pc, bool suppress, bool tap, bool locked, bool expected) =>
        Assert.Equal(expected, GameBarGuard.ShouldApply(full, pc, suppress, tap, locked));
}

public sealed class ProcessTreeTests
{
    // 10 (game) <- 20 <- 30; a parent starts before its child (start time = pid) except where a test says otherwise.
    private static readonly Dictionary<int, int> Parents = new() { [30] = 20, [20] = 10, [10] = 1 };

    private static DateTime? Start(int pid) => DateTime.UnixEpoch.AddSeconds(pid);

    private static int? Parent(int pid) => Parents.TryGetValue(pid, out var p) ? p : null;

    [Fact]
    public void FindsAnAncestorAndTheProcessItself()
    {
        Assert.True(ProcessTree.DescendsFrom(30, p => p == 10, Parent, Start));
        Assert.True(ProcessTree.DescendsFrom(10, p => p == 10, Parent, Start));
        Assert.False(ProcessTree.DescendsFrom(30, p => p == 99, Parent, Start));
    }

    [Fact]
    public void TheProcessItselfCountsEvenWhenItsStartTimeCannotBeRead()
    {
        // An elevated or protected game process: no start time, but its pid is in the game's list.
        Assert.True(ProcessTree.DescendsFrom(10, p => p == 10, Parent, _ => null));
    }

    [Fact]
    public void AnUnreadableStartTimeEndsTheClimb()
    {
        Assert.False(ProcessTree.DescendsFrom(30, p => p == 10, Parent, pid => pid == 20 ? null : Start(pid)));
        Assert.False(ProcessTree.DescendsFrom(30, p => p == 10, Parent, _ => null));
    }

    [Fact]
    public void AParentThatStartedAfterItsChildIsAReusedPid() =>
        Assert.False(ProcessTree.DescendsFrom(30, p => p == 10, Parent, pid => pid == 20 ? DateTime.UnixEpoch.AddSeconds(500) : Start(pid)));

    [Fact]
    public void StopsAtTheDepthLimit() =>
        Assert.False(ProcessTree.DescendsFrom(30, p => p == 10, Parent, Start, maxDepth: 2));
}
