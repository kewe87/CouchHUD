namespace CouchHud.Service.Tests;

/// <summary>
/// Resume game, the console-like switch and PC mode's way back, pinned from the shell's source (as in ProfileContractTests): a game the
/// parental rules hold is never brought back to the front, a switch in progress is seen by a plain start too, and a failed entry into
/// PC mode never leaves the Guide button waiting for ever.
/// </summary>
public sealed class GameFocusContractTests
{
    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CouchHud.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repository root not found");
    }

    private static string[] Lines(string file) => File.ReadAllText(file).Replace("\r\n", "\n").Split('\n');

    private static bool IsComment(string line) => line.TrimStart().StartsWith("//", StringComparison.Ordinal);

    private static string Body(string declaration)
    {
        var lines = Lines(Path.Combine(Root(), "src", "CouchHud.Shell", "Hud.cs"));
        var start = Array.FindIndex(lines, l => l.StartsWith(declaration, StringComparison.Ordinal));
        Assert.True(start >= 0, $"not found: {declaration}");
        var end = Array.FindIndex(lines, start + 1, l => l == "    }");
        return string.Join('\n', lines[start..end].Where(l => !IsComment(l)));
    }

    [Fact]
    public void Every_resume_refuses_a_game_the_rules_hold()
    {
        var prepare = Body("    private IReadOnlyList<int> PrepareResume()");
        var rule = Body("    private void RequireGameMayBeInFront(GameSession s)");
        Assert.Contains("Parental.Service.Expired", rule);
        Assert.Contains("ParentalEnforced && Parental.BlocksGame(s.Item.Id)", rule);
        var checks = prepare.IndexOf("RequireGameMayBeInFront(s);", StringComparison.Ordinal);
        Assert.True(checks >= 0 && checks < prepare.IndexOf("BeginResume(s)", StringComparison.Ordinal), "checked before the game is unfrozen");
        // The task list's switch to a window follows the same rule for the session's own game.
        var task = Body("    public void SwitchToProcess(string name)");
        Assert.Contains("RequireGameMayBeInFront(game)", task);
        Assert.True(task.IndexOf("RequireGameMayBeInFront", StringComparison.Ordinal) < task.IndexOf("ProcessList.Switch(name)", StringComparison.Ordinal));
        Assert.Contains("PrepareResume()", Body("    public void ResumeGame()"));
        Assert.Contains("PrepareResume()", Body("    public async Task ResumeGameAsync()"));
        // The launch of the running game resumes through the same path.
        var launch = Body("    public LibraryItem Launch(string id, bool byPerson = false)");
        Assert.Contains("ResumeGame();", launch);
        Assert.Contains("switching: Volatile.Read(ref _switching) == 1", launch);
    }

    [Fact]
    public void A_failed_entry_into_pc_mode_lets_the_guide_button_return()
    {
        var enter = Body("    private async Task EnterPcModeCoreAsync()");
        var tryAt = enter.IndexOf("\n        try\n", StringComparison.Ordinal);
        var set = enter.IndexOf("_pcModeEntering = true;", StringComparison.Ordinal);
        Assert.True(tryAt >= 0 && set > tryAt, "set inside the try");
        var fin = enter.IndexOf("\n        finally\n", StringComparison.Ordinal);
        Assert.True(fin > set && enter.IndexOf("_pcModeEntering = false;", fin, StringComparison.Ordinal) > fin, "reset in the finally");
    }

    [Fact]
    public void Pc_mode_hands_the_tap_to_the_game_bar_when_it_owns_the_button()
    {
        var code = File.ReadAllText(Path.Combine(Root(), "src", "CouchHud.Shell", "Hud.cs"));
        Assert.Contains("PcModeReturn.OnGuide(_pcMode, _pcModeEntering, GuideTapEffective && button == \"guide\")", code);
        Assert.Contains("PcModeReturn.NoticeKey(GuideTapEffective)", code);
    }

    [Fact]
    public void A_switch_is_called_off_before_anything_is_ended_by_force()
    {
        var close = Body("    private async Task<LibraryItem> CloseAndLaunchAsync(LibraryItem item, GameSession old, bool byPerson)");
        Assert.Contains("GameSwitchSequence.RunAsync(new SwitchSteps(this, item, old, byPerson)", close);
        var steps = File.ReadAllText(Path.Combine(Root(), "src", "CouchHud.Shell", "Hud.cs")).Replace("\r\n", "\n");
        Assert.Contains("Win32ProcessProbe.QuitAsync(old.StampsOrEmpty, force: false, SwitchGrace, cancel, mayEnd:", steps);
        // The checks run on the UI thread, explicitly.
        Assert.Contains("Application.Current.Dispatcher.InvokeAsync(() => hud.SwitchObstacle(item, old))", steps);
        Assert.Contains("Application.Current.Dispatcher.Invoke(() => hud.SwitchObstacle(item, old))", steps);
        var obstacle = Body("    private string? SwitchObstacle(LibraryItem item, GameSession old)");
        Assert.Contains("RequireLaunchable(item);", obstacle);
        Assert.Contains("catch (Exception ex)", obstacle);
        Assert.Contains("now.Item.Id != old.Item.Id", obstacle);
    }

    private static string Between(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"not found: {from}");
        var end = text.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"not found after it: {to}");
        return text.Substring(start, end - start);
    }

    private const string LookDeclaration = "    private (NewGameFrontStep Step, IReadOnlyList<int>? Pids) LookAtNewGame(string id, GameSession? session, int stamp, System.Runtime.CompilerServices.StrongBox<bool> begun, CancellationToken token)";

    [Fact]
    public void The_switch_starts_the_hand_over_to_the_new_game_after_its_start()
    {
        // The switch starts the game long after the last press, so Windows would leave it behind the HUD: it is brought forward like a resume.
        var code = File.ReadAllText(Path.Combine(Root(), "src", "CouchHud.Shell", "Hud.cs")).Replace("\r\n", "\n");
        var startNew = Between(code, "public LibraryItem StartNew()", "return started;");
        Assert.True(startNew.IndexOf("hud.LaunchCore(item, byPerson)", StringComparison.Ordinal) < startNew.IndexOf("hud.FrontNewGameAfterSwitch(item.Id);", StringComparison.Ordinal));
        // A new start and the shutdown end an earlier hand-over (one loop per switch).
        Assert.Contains("CancelFrontLoop();", Body("    private LibraryItem LaunchCore(LibraryItem item, bool byPerson)"));
        Assert.Contains("CancelFrontLoop();", Between(code, "    public void Quit(bool restarting = false)", "_foreground?.Dispose();"));
        Assert.Contains("Interlocked.Exchange(ref _frontLoop, cts)?.Cancel();", Body("    private void FrontNewGameAfterSwitch(string id)"));
    }

    [Fact]
    public void The_hand_over_runs_the_resume_gate_before_it_activates_and_changes_nothing_while_it_only_looks()
    {
        var loop = Body("    private void FrontNewGameAfterSwitch(string id)");
        var look = Body(LookDeclaration);
        // The same gate as Resume: protected profile and the parental rules, both before a window is looked for or anything is begun.
        var profile = look.IndexOf("RequireProfileChosen();", StringComparison.Ordinal);
        var rules = look.IndexOf("RequireGameMayBeInFront(session!);", StringComparison.Ordinal);
        var window = look.IndexOf("Win32ProcessProbe.HasGameWindow(", StringComparison.Ordinal);
        var decide = look.IndexOf("GameFront.NewGameStep(", StringComparison.Ordinal);
        var begin = look.IndexOf("BeginResume(session!)", StringComparison.Ordinal);
        Assert.True(profile >= 0 && profile < rules && rules < window && window < decide && decide < begin, "gate, window, decision, then the side effects");
        Assert.Contains("catch (InvalidOperationException)", look); // a held profile (ProfileHeldException) is a quiet refusal
        // A cancelled loop does nothing in the look, before the gate and before any side effect.
        Assert.True(look.IndexOf("token.IsCancellationRequested", StringComparison.Ordinal) is >= 0 and var cancelled && cancelled < profile);
        Assert.Contains("LookAtNewGame(id, session, stamp, begun, token)", loop);
        // The activation comes only after the look.
        Assert.True(loop.IndexOf("LookAtNewGame(", StringComparison.Ordinal) < loop.IndexOf("Win32ProcessProbe.ActivateGameAsync(", StringComparison.Ordinal));
        // No side effect in the poll before a window is found: they live in BeginResume, which only the Activate branch reaches.
        foreach (var effect in new[] { "ForgetUnlock", "GamePause.ResumeAll", "PrepareResume", "BeginResume", "_pauseOnActivate" })
        {
            Assert.DoesNotContain(effect, loop);
        }
        var beforeDecision = look[..decide];
        foreach (var effect in new[] { "ForgetUnlock", "GamePause.ResumeAll", "BeginResume", "_pauseOnActivate" })
        {
            Assert.DoesNotContain(effect, beforeDecision);
        }
        Assert.Contains("if (step != NewGameFrontStep.Activate) return (step, null);", look);
        var begun = Body("    private IReadOnlyList<int> BeginResume(GameSession s)");
        Assert.Contains("ForgetUnlock(s);", begun);   // the unlock is dropped; only the note of this very session survives (OpenLaunchNote.AfterForgetUnlock)
        Assert.Contains("GamePause.ResumeAll();", begun);
        // A plain resume is the gate plus the same side effects.
        var prepare = Body("    private IReadOnlyList<int> PrepareResume()");
        Assert.True(prepare.IndexOf("RequireGameMayBeInFront(s);", StringComparison.Ordinal) < prepare.IndexOf("BeginResume(s)", StringComparison.Ordinal));
        // One read of the session serves the id check, the gate and the activation.
        Assert.Equal(1, loop.Split("_sessions.Current").Length - 1);
    }

    [Fact]
    public void Any_way_of_the_hud_to_the_front_ends_the_hand_over_and_the_counter_is_checked_twice()
    {
        var root = Root();
        var code = File.ReadAllText(Path.Combine(root, "src", "CouchHud.Shell", "Hud.cs")).Replace("\r\n", "\n");
        var window = File.ReadAllText(Path.Combine(root, "src", "CouchHud.Shell", "MainWindow.xaml.cs")).Replace("\r\n", "\n");
        // ShowHud and the one place every other path goes through (guard recovery, profile switch, PC mode, upgrade watch ...).
        Assert.Contains("Interlocked.Increment(ref _hudShownCount);", Body("    public void ShowHud(string? target = \"home\")"));
        Assert.Contains("if (personCameBack) Interlocked.Increment(ref _frontRequests);", Between(window, "    public void BringToFront(bool personCameBack = false)", "if (!IsVisible)"));
        // Only the person coming back counts. Person-driven: ShowHud (Guide button, profile switch, PC mode), closing the couch browser, the two
        // unlock confirmations. Automatic ones do not end the hand-over, they may fire while the old game is gone and the new one has no
        // window yet (the foreground guard's recovery, the standby clients, the retry after leaving PC mode, the upgrade watch, the first
        // load); the gate runs before anything is activated, so they cannot make it override a rule.
        Assert.Contains("_window?.BringToFront(personCameBack: true);", Body("    public void ShowHud(string? target = \"home\")"));
        // (the fifth is the one-time counter-move of the watch after the person came back, see WatchAfterReturn)
        Assert.Equal(5, code.Split("BringToFront(personCameBack: true)").Length - 1);
        Assert.Equal(3, code.Split("BringToFront()").Length - 1); // guard recovery, standby clients, retry after PC mode
        Assert.Contains("() => _window?.BringToFront(), OnClientNeedsAttention", code);
        Assert.DoesNotContain("personCameBack", File.ReadAllText(Path.Combine(root, "src", "CouchHud.Shell", "HudUpgradeWatch.cs")));
        Assert.Contains("(_window?.FrontRequests ?? 0)", code);
        Assert.Contains("Volatile.Read(ref _hudShownCount)", code);
        // The stamp is compared in the look (together with the gate) and once more right before the activation.
        var loop = Body("    private void FrontNewGameAfterSwitch(string id)");
        var recheck = loop.IndexOf("FrontStamp != stamp", loop.IndexOf("if (look.Step != NewGameFrontStep.Activate) continue;", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(recheck > loop.IndexOf("LookAtNewGame(", StringComparison.Ordinal) && recheck < loop.IndexOf("Win32ProcessProbe.ActivateGameAsync(", StringComparison.Ordinal));
        Assert.Contains("GameFront.NewGameStep(FrontStamp != stamp,", Body(LookDeclaration));
        // PC mode, the couch browser and the mouse mode stop it, decided in the same UI-thread look as the gate.
        var lookBody = Body(LookDeclaration);
        Assert.Contains("var elsewhere = _pcMode || _browsing || _mouse?.Active == true;", lookBody);
        Assert.Contains("hasWindow, elsewhere);", lookBody);
    }

    [Fact]
    public void After_the_person_came_back_the_game_is_never_activated_again_and_a_stolen_front_is_taken_back_once()
    {
        var code = File.ReadAllText(Path.Combine(Root(), "src", "CouchHud.Shell", "Hud.cs")).Replace("\r\n", "\n");
        var loop = Body("    private void FrontNewGameAfterSwitch(string id)");
        // Every place that sees the person's return leaves for the watch, before any activation.
        var firstReturn = loop.IndexOf("await WatchAfterReturn(id, until, token, windowSeen);", StringComparison.Ordinal);
        Assert.True(firstReturn >= 0 && firstReturn < loop.IndexOf("Win32ProcessProbe.ActivateGameAsync(", StringComparison.Ordinal));
        Assert.True(loop.Split("await WatchAfterReturn(id, until, token, windowSeen);").Length - 1 >= 3);
        var watch = Body("    private async Task WatchAfterReturn(string id, DateTimeOffset until, CancellationToken token, DateTimeOffset? windowSeen)");
        // Never activates, never begins a resume; takes back at most once (the case returns), only via the pure rule.
        foreach (var effect in new[] { "ActivateGameAsync", "BeginResume", "PrepareResume", "ForgetUnlock", "GamePause" })
        {
            Assert.DoesNotContain(effect, watch);
        }
        Assert.Contains("GameFront.WatchStep(look.Elsewhere, look.IsNew, look.HasWindow, since, look.InFront)", watch);
        var takeBack = Between(watch, "case NewGameWatchStep.TakeBack:", "return;");
        Assert.Contains("token.IsCancellationRequested", takeBack);
        Assert.Contains("_window?.BringToFront(personCameBack: true)", watch);
        Assert.Equal(1, watch.Split("BringToFront(").Length - 1);
        var gameMainWindowMember = File.ReadAllText(Path.Combine(Root(), "src", "CouchHud.Shell", "Services", "Win32ProcessProbe.cs"));
        Assert.Contains("public static long GameMainWindow(", gameMainWindowMember);
        var look = Between(code, "    private (bool IsNew, bool HasWindow, bool InFront, bool Elsewhere) LookAtWatch(string id, GameSession? session)", "    /// <summary>One look of the hand-over");
        Assert.Contains("session.Item.Id != id", look);
        // Only the game's main window counts (the one that counts as "has a window"), not a dialog of the same process.
        Assert.Contains("Win32ProcessProbe.GameMainWindow(session.Pids)", look);
        Assert.Contains("GetForegroundWindow().ToInt64() == main", look);
        Assert.DoesNotContain("session.Pids.Contains(pid)", look);
        Assert.Contains("GameFront.PickWindow(", File.ReadAllText(Path.Combine(Root(), "src", "CouchHud.Shell", "Services", "Win32ProcessProbe.cs")));
        // The two seconds start when the loop first saw the window, and the take-back re-checks the token on the UI thread.
        Assert.Contains("DateTimeOffset? windowSince = windowSeen;", watch);
        Assert.Contains("windowSeen ??= DateTimeOffset.UtcNow;", loop);
        var uiLambda = Between(watch, "await Application.Current.Dispatcher.InvokeAsync(() =>\n                    {", "}).Task;");
        Assert.True(uiLambda.IndexOf("token.IsCancellationRequested", StringComparison.Ordinal) < uiLambda.IndexOf("BringToFront(", StringComparison.Ordinal));
        Assert.Contains("_pcMode || _browsing || _mouse?.Active == true", look);
        // A resume (Guide menu, Resume) is the person's own choice of the game: it ends the watch.
        Assert.Contains("CancelFrontLoop();", Body("    public void ResumeGame()"));
        Assert.Contains("CancelFrontLoop();", Body("    public async Task ResumeGameAsync()"));
    }

    [Fact]
    public void The_final_start_of_a_switch_never_goes_through_the_question()
    {
        var code = File.ReadAllText(Path.Combine(Root(), "src", "CouchHud.Shell", "Hud.cs")).Replace("\r\n", "\n");
        Assert.Contains("var started = hud.LaunchCore(item, byPerson);", code);
        // The question refuses while a switch runs; the core of the start has no question at all.
        var core = Body("    private LibraryItem LaunchCore(LibraryItem item, bool byPerson)");
        Assert.DoesNotContain("GameSwitch.Decide", core);
        Assert.DoesNotContain("_switching", core);
        Assert.Contains("Parental.CheckLaunch(item, AgeOf(item), GrantFor(item))", core);
        Assert.Contains("Launcher.Launch(", core);
        Assert.Contains("return LaunchCore(item, byPerson);", Body("    public LibraryItem Launch(string id, bool byPerson = false)"));
        Assert.DoesNotContain("Launch(item.Id", Body("    private async Task<LibraryItem> CloseAndLaunchAsync(LibraryItem item, GameSession old, bool byPerson)"));
    }

    [Fact]
    public void A_failed_look_at_the_resume_is_logged_with_its_real_exception()
    {
        var resume = Body("    public void ResumeGame()");
        Assert.Contains("t.Exception?.GetBaseException().Message", resume);
    }
}
