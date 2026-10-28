using System.Text.RegularExpressions;
using CouchHud.Core.Library;

namespace CouchHud.Core.Tests;

/// <summary>The watch of a batch start target's hidden cmd.exe: when it is ended, and which processes count as its working children.</summary>
public class ScriptWatchTests
{
    private const string Windows = @"C:\Windows";
    private const int Cmd = 4000;
    private const long CmdStart = 1_000_000;
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 20, 0, 0, TimeSpan.Zero);

    private static ScriptRun Run(DateTimeOffset? started = null) => new("game", "Game", Cmd, CmdStart, started ?? T0);

    [Fact]
    public void Only_three_empty_checks_in_a_row_after_the_grace_end_a_script_as_waiting()
    {
        Assert.Equal(ScriptStep.Wait, ScriptWatch.Decide(TimeSpan.FromMinutes(5), false, 1));
        Assert.Equal(ScriptStep.Wait, ScriptWatch.Decide(TimeSpan.FromMinutes(5), false, ScriptWatch.EmptyChecks - 1));
        Assert.Equal(ScriptStep.EndWaiting, ScriptWatch.Decide(TimeSpan.FromMinutes(5), false, ScriptWatch.EmptyChecks));
        Assert.Equal(ScriptStep.Wait, ScriptWatch.Decide(ScriptWatch.Grace - TimeSpan.FromSeconds(1), false, 10));
        Assert.Equal(ScriptStep.EndWaiting, ScriptWatch.Decide(ScriptWatch.Grace, false, ScriptWatch.EmptyChecks));
    }

    [Fact]
    public void A_polling_wrapper_with_short_gaps_between_its_tools_is_never_ended()
    {
        var run = Run();
        // tasklist / timeout in a loop: now and then one sample falls between two tools.
        var children = new[] { 1, 0, 1, 1, 0, 0, 1, 0, 1, 0, 0, 1 };
        for (var i = 0; i < children.Length; i++)
            Assert.Equal(ScriptStep.Wait, run.Observe(T0 + TimeSpan.FromMinutes(1) + ScriptWatch.Interval * i, children[i]));
        Assert.Equal(ScriptStep.Wait, run.Observe(T0 + TimeSpan.FromMinutes(3), 0));
        Assert.Equal(ScriptStep.Wait, run.Observe(T0 + TimeSpan.FromMinutes(3), 0));
        Assert.Equal(ScriptStep.EndWaiting, run.Observe(T0 + TimeSpan.FromMinutes(3), 0));
    }

    [Fact]
    public void A_script_that_runs_the_game_in_the_foreground_stays_even_when_the_session_is_over()
    {
        var run = Run();
        run.SessionEnded("game", T0 + TimeSpan.FromSeconds(4), T0 + TimeSpan.FromSeconds(20));
        Assert.True(run.SessionOver);
        // Something still runs under it (the game's last process, a copy after the game): never ended for the session alone.
        for (var i = 0; i < 10; i++) Assert.Equal(ScriptStep.Wait, run.Observe(T0 + TimeSpan.FromSeconds(21 + i), 1));
    }

    [Fact]
    public void A_session_over_ends_an_empty_script_before_the_grace_only_after_three_empty_checks()
    {
        var run = Run();
        run.SessionEnded("game", T0 + TimeSpan.FromSeconds(2), T0 + TimeSpan.FromSeconds(10));
        Assert.Equal(ScriptStep.Wait, run.Observe(T0 + TimeSpan.FromSeconds(11), 0));
        Assert.Equal(ScriptStep.Wait, run.Observe(T0 + TimeSpan.FromSeconds(16), 0));
        Assert.Equal(ScriptStep.EndSessionOver, run.Observe(T0 + TimeSpan.FromSeconds(21), 0));
    }

    [Fact]
    public void Only_a_session_that_started_after_the_script_counts_and_a_new_session_resets_it()
    {
        var run = Run();
        // An older session of the same entry (started before this script) ends: this script's game is still ahead.
        run.SessionEnded("game", T0 - TimeSpan.FromMinutes(5), T0 + TimeSpan.FromSeconds(3));
        Assert.False(run.SessionOver);
        // Another entry's session: not this script's.
        run.SessionEnded("other", T0 + TimeSpan.FromSeconds(1), T0 + TimeSpan.FromSeconds(3));
        Assert.False(run.SessionOver);
        run.SessionEnded("game", T0 + TimeSpan.FromSeconds(1), T0 + TimeSpan.FromSeconds(9));
        Assert.True(run.SessionOver);
        // The script starts the game again (a loop, a restart): the session is running again.
        run.SessionStarted("other");
        Assert.True(run.SessionOver);
        run.SessionStarted("game");
        Assert.False(run.SessionOver);
        for (var i = 0; i < 5; i++) Assert.Equal(ScriptStep.Wait, run.Observe(T0 + TimeSpan.FromSeconds(10 + i), 0));
    }

    [Fact]
    public void Only_children_created_after_the_cmd_count_and_console_host_and_choice_are_no_work()
    {
        var parents = new Dictionary<int, int>
        {
            [Cmd] = 100, // the cmd.exe itself
            [10] = Cmd, // conhost.exe of the hidden console
            [11] = Cmd, // choice.exe waiting on input
            [12] = 7, // someone else's
            [13] = Cmd, // older than the cmd: its parent id names a dead process whose number came back
        };
        var starts = new Dictionary<int, long> { [10] = CmdStart + 1, [11] = CmdStart + 2, [12] = CmdStart + 3, [13] = CmdStart - 50 };
        var images = new Dictionary<int, string>
        {
            [10] = @"C:\Windows\System32\conhost.exe",
            [11] = @"C:\Windows\SysWOW64\CHOICE.EXE",
            [12] = @"D:\Games\X\game.exe",
            [13] = @"D:\Games\X\game.exe",
        };
        long StartOf(int pid) => starts.GetValueOrDefault(pid);
        string? ImageOf(int pid) => images.GetValueOrDefault(pid);
        Assert.Equal(0, ScriptWatch.WorkingChildren(Cmd, CmdStart, parents, StartOf, ImageOf, Windows));

        // The game started in the foreground, and timeout.exe, are work.
        parents[20] = Cmd;
        starts[20] = CmdStart + 10;
        images[20] = @"D:\Games\X\game.exe";
        parents[21] = Cmd;
        starts[21] = CmdStart + 11;
        images[21] = @"C:\Windows\System32\timeout.exe";
        Assert.Equal(2, ScriptWatch.WorkingChildren(Cmd, CmdStart, parents, StartOf, ImageOf, Windows));
    }

    [Fact]
    public void A_doubt_never_ends_a_script()
    {
        var parents = new Dictionary<int, int> { [30] = Cmd, [31] = Cmd };
        // 30: start time unreadable; 31: image unreadable.
        var starts = new Dictionary<int, long> { [31] = CmdStart + 1 };
        Assert.Equal(2, ScriptWatch.WorkingChildren(Cmd, CmdStart, parents, p => starts.GetValueOrDefault(p), _ => null, Windows));
        // conhost.exe by name only, outside System32/SysWOW64, is work.
        Assert.False(ScriptWatch.IsNoWork(@"D:\Games\X\conhost.exe", Windows));
        Assert.False(ScriptWatch.IsNoWork(@"C:\Windows\conhost.exe", Windows));
        Assert.True(ScriptWatch.IsNoWork(@"C:\Windows\System32\conhost.exe", Windows));
        Assert.False(ScriptWatch.IsNoWork(null, Windows));
        Assert.False(ScriptWatch.IsNoWork(@"C:\Windows\System32\choice.exe", ""));
    }

    private static string Source(params string[] path)
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CouchHud.slnx"))) root = Path.GetDirectoryName(root)!;
        // Comments do not count.
        return string.Join('\n', File.ReadAllLines(Path.Combine([root, .. path])).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
    }

    [Fact]
    public void The_launcher_ends_only_the_exact_cmd_it_started_and_never_by_name()
    {
        var launcher = Source("src", "CouchHud.Shell", "Services", "Launcher.cs");
        // Every kill in the launcher is the checked one, by the run's pid and start time.
        var kills = Regex.Matches(launcher, @"([\w.]*)\.Kill\s*\(([^)]*)\)");
        Assert.NotEmpty(kills);
        foreach (Match kill in kills)
        {
            Assert.EndsWith("Win32ProcessControl.Instance", kill.Groups[1].Value);
            Assert.Equal("run.Pid, run.Start", Regex.Replace(kill.Groups[2].Value, @"\s+", " ").Trim());
        }
        Assert.DoesNotContain("GetProcessesByName", launcher);
        Assert.Matches(@"Win32ProcessControl\.StartOf\(\s*pid\s*\)", launcher);
        // The session events and the quit reach the watch.
        var hud = Source("src", "CouchHud.Shell", "Hud.cs");
        Assert.Matches(@"Launcher\.SessionEnded\(\s*s\.Item\.Id\s*,\s*s\.Started\s*\)", hud);
        Assert.Matches(@"Launcher\.SessionStarted\(\s*s\.Item\.Id\s*\)", hud);
        Assert.Matches(@"Launcher\.EndWaitingScripts\(\s*\)", hud);
    }
}
