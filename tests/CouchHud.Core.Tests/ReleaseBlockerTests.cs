using CouchHud.Core.Config;
using CouchHud.Core.Guard;
using CouchHud.Core.Net;
using CouchHud.Core.Security;
using CouchHud.Core.Startup;

namespace CouchHud.Core.Tests;

/// <summary>A session source the test drives by hand: events, the polled lock state, or both.</summary>
internal sealed class FakeSession : ISessionSource
{
    public event Action<bool>? LockChanged;

    /// <summary>What the poll answers right now; a script entry per call wins over it.</summary>
    public bool? State { get; set; }

    public Queue<bool?> Script { get; } = new();
    public bool PollThrows { get; set; }
    public int Polls { get; private set; }

    public bool? IsLocked()
    {
        Polls++;
        if (PollThrows) throw new InvalidOperationException("no session information");
        return Script.Count > 0 ? Script.Dequeue() : State;
    }

    public void Raise(bool locked) => LockChanged?.Invoke(locked);
}

/// <summary>Recovery variant B: the wait for the Windows sign-in must end when the account is back, whatever delivers the news.</summary>
public sealed class SignInWaitTests
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(20);

    /// <summary>A clock the fake sleep moves.</summary>
    private sealed class Clock
    {
        public DateTime Now { get; private set; } = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        public int Sleeps { get; private set; }

        public void Sleep(TimeSpan span)
        {
            Now += span;
            Sleeps++;
        }
    }

    private static SignInWaitResult Wait(FakeSession session, Clock clock, Action<int>? onSleep = null, CancellationToken cancel = default) =>
        SignInWait.Wait(session, () => clock.Now, span =>
        {
            clock.Sleep(span);
            onSleep?.Invoke(clock.Sleeps);
        }, Poll, Grace, cancel);

    [Fact]
    public void The_unlock_event_after_the_lock_event_ends_the_wait()
    {
        var session = new FakeSession { State = null };
        var clock = new Clock();

        var result = Wait(session, clock, n =>
        {
            if (n == 1) session.Raise(true);
            if (n == 3) session.Raise(false);
        });

        Assert.Equal(SignInWaitResult.SignedIn, result);
    }

    [Fact]
    public void Without_any_event_the_poll_alone_sees_the_sign_in()
    {
        // The regression: the events never arrive (their thread is blocked), the state of the session still tells.
        var session = new FakeSession();
        session.Script.Enqueue(null);
        session.Script.Enqueue(true);
        session.Script.Enqueue(true);
        session.Script.Enqueue(true);
        session.State = false;
        var clock = new Clock();

        Assert.Equal(SignInWaitResult.SignedIn, Wait(session, clock));
        Assert.True(clock.Sleeps >= 4);
    }

    [Fact]
    public void Unlocked_right_after_LockWorkStation_is_not_a_sign_in()
    {
        // LockWorkStation returns before the lock screen is up: "not locked" at first means nothing.
        var session = new FakeSession { State = false };
        var clock = new Clock();

        var result = Wait(session, clock, n =>
        {
            if (n == 5) session.State = true;
            if (n == 9) session.State = false;
        });

        Assert.Equal(SignInWaitResult.SignedIn, result);
        Assert.True(clock.Sleeps >= 9);
    }

    [Fact]
    public void A_lock_that_never_shows_ends_as_never_locked_and_not_as_signed_in()
    {
        var session = new FakeSession { State = false };
        var clock = new Clock();

        Assert.Equal(SignInWaitResult.NeverLocked, Wait(session, clock));
        Assert.True(clock.Now - new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc) >= Grace);
    }

    [Fact]
    public void An_unreadable_state_with_events_only_still_works()
    {
        var session = new FakeSession { PollThrows = true };
        var clock = new Clock();

        var result = Wait(session, clock, n =>
        {
            if (n == 2) session.Raise(true);
            if (n == 4) session.Raise(false);
        });

        Assert.Equal(SignInWaitResult.SignedIn, result);
    }

    [Fact]
    public void An_unlock_event_before_any_lock_event_does_not_count()
    {
        var session = new FakeSession { State = null };
        var clock = new Clock();

        var result = Wait(session, clock, n =>
        {
            if (n == 1) session.Raise(false);
        });

        Assert.Equal(SignInWaitResult.NeverLocked, result);
    }

    [Fact]
    public void A_lock_and_an_unlock_between_two_polls_are_both_remembered()
    {
        var session = new FakeSession { State = null };
        var clock = new Clock();

        var result = Wait(session, clock, n =>
        {
            if (n == 1)
            {
                session.Raise(true);
                session.Raise(false);
            }
        });

        Assert.Equal(SignInWaitResult.SignedIn, result);
    }

    [Fact]
    public void Cancelling_ends_the_wait()
    {
        var session = new FakeSession { State = true };
        var clock = new Clock();
        using var cancel = new CancellationTokenSource();

        var result = Wait(session, clock, n =>
        {
            if (n == 3) cancel.Cancel();
        }, cancel.Token);

        Assert.Equal(SignInWaitResult.Cancelled, result);
    }

    [Fact]
    public void The_subscription_is_given_back_when_the_wait_ends()
    {
        var session = new FakeSession { State = true };
        var clock = new Clock();
        var seen = 0;
        session.LockChanged += _ => seen++;

        Wait(session, clock, n =>
        {
            if (n == 1) session.State = false;
        });
        session.Raise(true);

        // Only the test's own handler is left.
        Assert.Equal(1, seen);
    }

    [Fact]
    public async Task The_wait_does_not_block_on_a_thread_that_cannot_deliver_events()
    {
        // The wait runs on a thread of its own and ends although no event is ever raised on any other thread.
        var session = new FakeSession { State = true };
        var result = Task.Run(() =>
        {
            var calls = 0;
            return SignInWait.Wait(session, () => DateTime.UtcNow, _ =>
            {
                if (++calls == 3) session.State = false;
            }, TimeSpan.Zero, TimeSpan.FromMinutes(1));
        });

        Assert.Equal(SignInWaitResult.SignedIn, await result.WaitAsync(TimeSpan.FromSeconds(10)));
    }
}

/// <summary>The rule: a helper task goes only when everything it can release is verifiably released.</summary>
public sealed class PolicyReleaseRuleTests
{
    private static FakeRegistry Locked(bool taskManager = false)
    {
        var registry = new FakeRegistry();
        foreach (var c in PolicyGroups.Lock.Dwords) registry.WriteDword(c.Location, c.Name, int.Parse(c.Value));
        registry.Values[LockHardening.DisallowRunKey] = new() { ["1"] = "cmd.exe", ["2"] = "powershell.exe", ["3"] = "regedit.exe" };
        if (taskManager) registry.WriteDword(LockHardening.TaskManagerChange.Location, LockHardening.TaskManagerChange.Name, 1);
        return registry;
    }

    [Fact]
    public void Our_values_are_found_by_name_and_exact_value()
    {
        var registry = Locked();

        var found = PolicyRelease.OursPresent(registry, PolicyGroups.Lock);

        Assert.Contains(found, f => f.EndsWith(@"\NoRun"));
        Assert.Contains(found, f => f.EndsWith(@"\NoControlPanel"));
        Assert.Contains(found, f => f.EndsWith(@"\DisableCMD"));
        Assert.Contains(found, f => f.EndsWith(@"\1"));
        Assert.Contains(PolicyRelease.GroupsWithOurs(registry), g => g.Id == "lock");
        Assert.DoesNotContain(PolicyRelease.GroupsWithOurs(registry), g => g.Id == "taskmgr");
    }

    [Fact]
    public void A_value_with_another_content_is_not_ours()
    {
        var registry = new FakeRegistry();
        registry.WriteDword(LockHardening.ExplorerKey, "NoRun", 0);
        registry.WriteDword(LockHardening.WindowsSystemKey, "DisableCMD", 1);
        registry.Values[LockHardening.DisallowRunKey] = new() { ["1"] = "mybackup.exe" };

        Assert.False(PolicyRelease.AnyOurs(registry));
    }

    [Fact]
    public void Removing_ours_leaves_everything_else_alone()
    {
        var registry = Locked();
        registry.WriteDword(LockHardening.ExplorerKey, "NoDrives", 4);
        registry.Values[LockHardening.DisallowRunKey] = new() { ["1"] = "cmd.exe", ["2"] = "mybackup.exe", ["3"] = "regedit.exe" };
        registry.WriteString(@"HKCU\Software\Other", "Setting", "x");

        var left = PolicyRelease.RemoveOurs(registry);

        Assert.Empty(left);
        Assert.False(PolicyRelease.AnyOurs(registry));
        Assert.Equal(4, registry.ReadDword(LockHardening.ExplorerKey, "NoDrives"));
        Assert.Equal(["2"], registry.ReadValues(LockHardening.DisallowRunKey).Keys.ToArray());
        Assert.Equal("mybackup.exe", registry.ReadValues(LockHardening.DisallowRunKey)["2"]);
        Assert.Equal("x", registry.ReadString(@"HKCU\Software\Other", "Setting"));
    }

    [Fact]
    public void Removing_ours_includes_task_manager_and_only_its_fixed_value()
    {
        var registry = Locked(taskManager: true);
        registry.WriteDword(LockHardening.SystemKey, "DisableLockWorkstation", 1);

        PolicyRelease.RemoveOurs(registry);

        Assert.Null(registry.ReadDword(LockHardening.TaskManagerChange.Location, LockHardening.TaskManagerChange.Name));
        Assert.Equal(1, registry.ReadDword(LockHardening.SystemKey, "DisableLockWorkstation"));
    }

    [Fact]
    public void An_emptied_disallow_list_key_goes_but_a_list_with_foreign_entries_stays()
    {
        var registry = Locked();

        PolicyRelease.RemoveOurs(registry);

        Assert.False(registry.KeyExists(LockHardening.DisallowRunKey));
    }

    [Fact]
    public void Tasks_may_go_only_with_a_clear_registry_and_empty_marks()
    {
        var registry = Locked();

        var withValues = PolicyRelease.Verdict.Check(registry, []);
        var withMark = PolicyRelease.Verdict.Check(new FakeRegistry(), ["lock"]);
        PolicyRelease.RemoveOurs(registry);
        var clear = PolicyRelease.Verdict.Check(registry, []);

        Assert.False(PolicyRelease.TasksMayGo(withValues));
        Assert.False(PolicyRelease.TasksMayGo(withMark));
        Assert.False(PolicyRelease.TasksMayGo(null));
        Assert.True(PolicyRelease.TasksMayGo(clear));
        Assert.NotEmpty(withValues.Remaining);
    }

    private static readonly string[] Tasks =
    [
        @"CouchHud\S-1-5-21-1-2-3-1001\Policy-lock-on", @"CouchHud\S-1-5-21-1-2-3-1001\Policy-lock-off",
        @"CouchHud\S-1-5-21-1-2-3-1001\Policy-taskmgr-on", @"CouchHud\S-1-5-21-1-2-3-1001\Policy-taskmgr-off",
        @"CouchHud\S-1-5-21-1-2-3-1001\Policy-cleanup", @"CouchHud\S-1-5-21-1-2-3-1001\Autologon-off",
        @"CouchHud\S-1-5-21-1-2-3-1001\Couch-WSearch", @"CouchHud\S-1-5-21-1-2-3-1001\PC-WSearch"
    ];

    [Fact]
    public void The_way_back_deletes_nothing_while_something_of_the_lock_stays()
    {
        Assert.Empty(PolicyRelease.TasksToDelete(Tasks, replacing: false, released: false));
    }

    [Fact]
    public void The_way_back_deletes_all_tasks_once_released()
    {
        Assert.Equal(Tasks.Length, PolicyRelease.TasksToDelete(Tasks, replacing: false, released: true).Count);
    }

    [Fact]
    public void A_setup_replaces_the_policy_tasks_in_place_and_deletes_service_tasks_only()
    {
        var gone = PolicyRelease.TasksToDelete(Tasks, replacing: true, released: false);

        Assert.Equal(2, gone.Count);
        Assert.All(gone, t => Assert.False(PolicyRelease.IsPolicyTask(t)));
    }

    [Theory]
    [InlineData(@"CouchHud\S-1-5-21-1-2-3-1001\Policy-lock-off", true)]
    [InlineData(@"CouchHud\S-1-5-21-1-2-3-1001\Policy-cleanup", true)]
    [InlineData(@"CouchHud\S-1-5-21-1-2-3-1001\Autologon-off", true)]
    [InlineData(@"CouchHud\Policy-lock-on", true)]
    [InlineData(@"CouchHud\S-1-5-21-1-2-3-1001\Couch-WSearch", false)]
    public void Policy_tasks_are_told_from_service_tasks(string task, bool policy) => Assert.Equal(policy, PolicyRelease.IsPolicyTask(task));

    [Fact]
    public void Our_values_without_a_mark_and_without_a_lock_are_stuck()
    {
        var registry = Locked();

        Assert.Single(PolicyRelease.Stuck(registry, [], []));
        // Marked by the helper, or meant to be on right now: explained, not stuck.
        Assert.Empty(PolicyRelease.Stuck(registry, ["lock"], []));
        Assert.Empty(PolicyRelease.Stuck(registry, [], ["lock"]));
        Assert.Empty(PolicyRelease.Stuck(new FakeRegistry(), [], []));
    }

    [Fact]
    public void Task_manager_is_stuck_on_its_own_and_not_explained_by_the_lock_group()
    {
        var registry = Locked(taskManager: true);

        var stuck = PolicyRelease.Stuck(registry, [], ["lock"]);

        Assert.Equal(["taskmgr"], stuck.Select(g => g.Id));
    }

    [Fact]
    public void The_exit_code_for_stuck_policies_has_a_message()
    {
        Assert.Equal("services.setupPoliciesStay", SetupExit.MessageKey(SetupExit.PoliciesStay));
        Assert.NotEqual(SetupExit.TaskFailed, SetupExit.PoliciesStay);
    }

    [Fact]
    public void The_way_back_page_releases_the_policies_before_it_deletes_the_tasks()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CouchHud.slnx"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var js = File.ReadAllText(Path.Combine(dir!, "ui", "views", "console-setup.js"));
        var revert = js[js.IndexOf("function revertSteps()", StringComparison.Ordinal)..];

        var release = revert.IndexOf("id: 'release'", StringComparison.Ordinal);
        var services = revert.IndexOf("id: 'services'", StringComparison.Ordinal);

        Assert.True(release >= 0 && services > release, "the release step must come before the step that deletes the tasks");
        Assert.Contains("api.releaseLockPolicies()", revert);
    }
}

/// <summary>config.json is saved again and again when another process holds it for a moment; a real failure is reported.</summary>
public sealed class ConfigSaveRetryTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("couchhud-save-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ConfigStore Store(Action<string, string>? move = null)
    {
        var store = new ConfigStore(_dir);
        store.SavePause = TimeSpan.Zero;
        store.Pause = _ => { };
        if (move is not null) store.MoveOver = move;
        return store;
    }

    [Fact]
    public void A_denied_swap_is_tried_again_and_the_change_is_saved()
    {
        var failures = 3;
        var store = Store((from, to) =>
        {
            if (failures-- > 0) throw new UnauthorizedAccessException("Access to the path is denied.");
            File.Move(from, to, overwrite: true);
        });

        var saved = store.Update(c => c.Skin = "user:deepsea");

        Assert.True(saved);
        Assert.Null(store.LastSaveError);
        Assert.Equal("user:deepsea", new ConfigStore(_dir).Current.Skin);
    }

    [Fact]
    public void A_swap_that_never_works_is_reported_and_leaves_no_temp_file()
    {
        var attempts = 0;
        var store = Store((_, _) =>
        {
            attempts++;
            throw new IOException("The process cannot access the file.");
        });
        store.SaveAttempts = 5;

        Assert.False(store.Update(c => c.Skin = "user:deepsea"));
        Assert.Equal(5, attempts);
        Assert.NotNull(store.LastSaveError);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void UpdateOrThrow_tells_the_caller_that_nothing_was_saved()
    {
        var store = Store((_, _) => throw new UnauthorizedAccessException("Access denied"));
        store.SaveAttempts = 2;

        var ex = Assert.Throws<IOException>(() => store.UpdateOrThrow(c => c.Skin = "user:deepsea"));

        Assert.Contains("Access denied", ex.Message);
    }

    [Fact]
    public void UpdateOrThrow_is_quiet_when_it_saved()
    {
        var store = Store();

        store.UpdateOrThrow(c => c.Skin = "user:greenlight");

        Assert.Equal("user:greenlight", new ConfigStore(_dir).Current.Skin);
    }

    [Fact]
    public void The_pause_between_the_tries_grows()
    {
        var pauses = new List<TimeSpan>();
        var failures = 3;
        var store = Store((from, to) =>
        {
            if (failures-- > 0) throw new IOException("busy");
            File.Move(from, to, overwrite: true);
        });
        store.SavePause = TimeSpan.FromMilliseconds(10);
        store.Pause = pauses.Add;

        Assert.True(store.Update(c => c.Skin = "user:deepsea"));

        Assert.Equal([TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(30)], pauses);
    }

    [Fact]
    public void A_reader_of_another_process_no_longer_blocks_the_swap()
    {
        // Our own reads share the right to replace the file; File.ReadAllText did not.
        var first = new ConfigStore(_dir);
        first.Update(c => c.Skin = "a");
        using var holder = new FileStream(Path.Combine(_dir, "config.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        Assert.True(first.Update(c => c.Skin = "b"));
    }

    [Fact]
    public void A_reader_that_blocks_the_swap_for_good_is_reported_not_swallowed()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = Store();
        store.Update(c => c.Skin = "a");
        store.SaveAttempts = 3;
        using var holder = new FileStream(Path.Combine(_dir, "config.json"), FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.False(store.Update(c => c.Skin = "b"));
        Assert.Throws<IOException>(() => store.UpdateOrThrow(c => c.Skin = "c"));
    }

    [Fact]
    public void An_unusable_file_is_not_saved_over_and_reports_it()
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"), "{ broken");
        var store = new ConfigStore(_dir, 1, TimeSpan.Zero);

        Assert.False(store.Update(c => c.Skin = "user:deepsea"));
        Assert.Throws<IOException>(() => store.UpdateOrThrow(c => c.Skin = "user:deepsea"));
    }
}

/// <summary>The firewall rule has a name of its own; Windows' rules of the program's name are never touched.</summary>
public sealed class FirewallNameTests
{
    private const string Exe = @"C:\Program Files\CouchHud\CouchHud.exe";

    private static FirewallRuleInfo Ours(string name = FirewallRule.Name) =>
        new(name, Allow: true, Inbound: true, Enabled: true, Exe, "8766,8765", 6, "LocalSubnet");

    private static FirewallRuleInfo WindowsBlock(int protocol) =>
        new("CouchHUD", Allow: false, Inbound: true, Enabled: true, Exe, "*", protocol, "*");

    [Fact]
    public void Every_command_uses_the_unique_name_only()
    {
        Assert.Equal("CouchHUD Remote (CouchHUD)", FirewallRule.Name);
        Assert.NotEqual(FirewallRule.LegacyName, FirewallRule.Name, StringComparer.OrdinalIgnoreCase);
        var all = new[] { FirewallRule.AddArgs(Exe, [8766]), FirewallRule.AllowArgs(true, Exe, [8766]), FirewallRule.AllowArgs(false, Exe, [8766]), FirewallRule.DeleteArgs(), FirewallRule.ShowArgs() };
        foreach (var args in all)
        {
            Assert.Contains($"name={FirewallRule.Name}", args);
            Assert.DoesNotContain("name=CouchHud", args, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_rule_is_an_inbound_allow_rule_in_both_forms()
    {
        Assert.Contains("action=allow", FirewallRule.AddArgs(Exe, [8766]));
        Assert.Contains("dir=in", FirewallRule.AddArgs(Exe, [8766]));
        // A rule that was turned into something else is set back to allow when it is changed.
        Assert.Contains("action=allow", FirewallRule.AllowArgs(true, Exe, [8766]));
    }

    [Fact]
    public void A_rule_of_our_name_that_does_not_allow_is_a_wrong_rule()
    {
        var blocking = Ours() with { Allow = false };

        Assert.True(FirewallRule.Exists([blocking]));
        Assert.False(FirewallRule.IsAllowRule([blocking]));
        Assert.True(FirewallRule.HasWrongRule([blocking]));
        Assert.True(FirewallRule.IsAllowRule([Ours()]));
        Assert.False(FirewallRule.HasWrongRule([Ours()]));
    }

    [Fact]
    public void Windows_rules_of_the_program_name_are_not_ours_even_though_netsh_finds_them_by_name()
    {
        // Windows' rules are called "CouchHUD", netsh ignores the case: neither our existence check nor our delete may see them.
        Assert.False(FirewallRule.Exists([WindowsBlock(6), WindowsBlock(17)]));
        Assert.Empty(FirewallRule.OurLegacyRules([WindowsBlock(6), WindowsBlock(17)], Exe, [8766, 8765]));
    }

    [Fact]
    public void An_old_rule_of_ours_is_recognised_by_allow_subnet_ports_and_program()
    {
        var old = Ours(FirewallRule.LegacyName);

        Assert.Single(FirewallRule.OurLegacyRules([old], Exe, [8766, 8765]));
        // The ports changed meanwhile, the program still tells.
        Assert.Single(FirewallRule.OurLegacyRules([old with { LocalPorts = "9000" }], Exe, [8766]));
        // Other program and other ports: not provably ours.
        Assert.Empty(FirewallRule.OurLegacyRules([old with { LocalPorts = "9000", Program = @"C:\Other\Other.exe" }], Exe, [8766]));
    }

    [Theory]
    [InlineData(false, true, 6, "LocalSubnet", "8766")]
    [InlineData(true, false, 6, "LocalSubnet", "8766")]
    [InlineData(true, true, 17, "LocalSubnet", "8766")]
    [InlineData(true, true, 6, "*", "8766")]
    [InlineData(true, true, 6, "LocalSubnet", "*")]
    [InlineData(true, true, 6, "LocalSubnet", "")]
    [InlineData(true, true, 6, "LocalSubnet", "80")]
    public void An_old_rule_that_is_a_block_rule_or_wide_open_or_not_ours_stays(bool allow, bool inbound, int protocol, string remote, string ports)
    {
        var rule = new FirewallRuleInfo(FirewallRule.LegacyName, allow, inbound, true, Exe, ports, protocol, remote);

        Assert.False(FirewallRule.IsOurLegacyRule(rule, Exe, [8766, 8765]));
    }

    [Fact]
    public void Deleting_an_old_rule_names_the_program_and_its_ports_so_a_block_rule_of_the_same_name_does_not_match()
    {
        var args = FirewallRule.DeleteLegacyArgs(Ours(FirewallRule.LegacyName));

        Assert.Contains("name=CouchHud", args);
        Assert.Contains("localport=8766,8765", args);
        Assert.Contains("protocol=TCP", args);
        Assert.Contains("dir=in", args);
        Assert.Contains("remoteip=localsubnet", args);
        Assert.Contains($"program={Exe}", args);
    }

    [Fact]
    public void Windows_block_rules_for_the_program_are_reported()
    {
        var blocking = FirewallRule.BlockingRules([Ours(), WindowsBlock(6), WindowsBlock(17), WindowsBlock(6) with { Enabled = false },
            WindowsBlock(6) with { Program = @"C:\Other\Other.exe" }, WindowsBlock(6) with { Inbound = false }], Exe);

        Assert.Equal(2, blocking.Count);
        Assert.All(blocking, r => Assert.False(r.Allow));
        Assert.Empty(FirewallRule.BlockingRules([Ours()], Exe));
    }

    [Fact]
    public void The_program_path_is_compared_without_case_and_quotes()
    {
        var blocking = FirewallRule.BlockingRules([WindowsBlock(6) with { Program = "\"c:\\program files\\couchhud\\couchhud.exe\"" }], Exe);

        Assert.Single(blocking);
    }
}

/// <summary>Mouse mode of a launcher, the lock closes: the screen must not stay black. A stubborn locked window is met with backoff.</summary>
public sealed class LockedMouseAndBackoffTests
{
    private static ForegroundContext Context(bool locked, bool mouse, bool pc = false, bool browsing = false) =>
        new(true, false, mouse, pc, browsing, false, Locked: locked);

    [Fact]
    public void The_mouse_mode_ends_when_the_lock_closes()
    {
        Assert.True(ForegroundPolicy.ShouldEndMouseMode(Context(locked: true, mouse: true)));
    }

    [Theory]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(true, true, false, true)]
    public void Unlocked_without_mouse_mode_in_pc_mode_or_in_the_browser_nothing_ends(bool locked, bool mouse, bool pc, bool browsing)
    {
        Assert.False(ForegroundPolicy.ShouldEndMouseMode(Context(locked, mouse, pc, browsing)));
    }

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static ForegroundState LockedState() => new() { MaxAttempts = 6, QuietTime = TimeSpan.FromSeconds(16), BackoffStart = TimeSpan.FromSeconds(2) };

    [Fact]
    public void Giving_up_on_a_locked_window_is_never_for_good()
    {
        var state = LockedState();
        for (var i = 0; i < 6; i++) Assert.Equal(BackoffAction.Act, state.Attempt("steam", T0.AddMilliseconds(i * 100)));

        Assert.Equal(BackoffAction.GiveUp, state.Attempt("steam", T0.AddSeconds(1)));
        Assert.Equal(BackoffAction.Quiet, state.Attempt("steam", T0.AddSeconds(2)));
        // After the short quiet time the guard looks again (and gives up again at once if the window insists).
        Assert.Equal(BackoffAction.GiveUp, state.Attempt("steam", T0.AddSeconds(4)));
    }

    [Fact]
    public void The_quiet_time_doubles_with_every_give_up_up_to_the_cap()
    {
        var state = new ForegroundState { MaxAttempts = 6, QuietTime = TimeSpan.FromSeconds(16), BackoffStart = TimeSpan.FromSeconds(2), AttemptWindow = TimeSpan.FromHours(1) };
        for (var i = 0; i < 6; i++) state.Attempt("steam", T0);
        var times = new List<TimeSpan>();
        var now = T0;
        for (var round = 0; round < 6; round++)
        {
            Assert.Equal(BackoffAction.GiveUp, state.Attempt("steam", now));
            times.Add(state.QuietFor("steam"));
            now += state.QuietFor("steam");
        }

        Assert.Equal([2, 4, 8, 16, 16, 16], times.Select(t => (int)t.TotalSeconds));
    }

    [Fact]
    public void A_long_calm_starts_the_backoff_again()
    {
        var state = LockedState();
        for (var i = 0; i < 6; i++) state.Attempt("steam", T0);
        state.Attempt("steam", T0);
        state.Attempt("steam", T0.AddSeconds(3));
        Assert.True(state.QuietFor("steam") > TimeSpan.FromSeconds(2));

        var later = T0.AddMinutes(10);
        for (var i = 0; i < 6; i++) Assert.Equal(BackoffAction.Act, state.Attempt("steam", later));
        state.Attempt("steam", later);

        Assert.Equal(TimeSpan.FromSeconds(2), state.QuietFor("steam"));
    }

    [Fact]
    public void A_window_that_cannot_be_minimized_is_met_again_after_a_short_pause_when_locked()
    {
        var state = LockedState();

        Assert.Equal(BackoffAction.GiveUp, state.Failed("steam", T0));
        Assert.Equal(BackoffAction.Quiet, state.Failed("steam", T0.AddSeconds(1)));
        Assert.Equal(BackoffAction.GiveUp, state.Failed("steam", T0.AddSeconds(3)));
    }

    [Fact]
    public void The_couch_state_without_backoff_keeps_its_fixed_quiet_time()
    {
        var state = new ForegroundState();
        state.Attempt("steam", T0);
        state.Attempt("steam", T0);

        Assert.Equal(BackoffAction.GiveUp, state.Attempt("steam", T0));
        Assert.Equal(BackoffAction.Quiet, state.Attempt("steam", T0.AddMinutes(4)));
        Assert.Equal(TimeSpan.FromMinutes(5), state.QuietFor("steam"));
    }
}
