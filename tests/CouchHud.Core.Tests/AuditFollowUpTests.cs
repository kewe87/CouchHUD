using System.Security.Cryptography;
using CouchHud.Core.Config;
using CouchHud.Core.Guard;
using CouchHud.Core.Plugins;
using CouchHud.Core.Remote;
using CouchHud.Core.Security;

namespace CouchHud.Core.Tests;

/// <summary>Follow-ups of the review of "Fixes from the combination audit".</summary>
public class AuditFollowUpTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("couchhud-followup-").FullName;

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

    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(5);
    private string ConfigFile => Path.Combine(_dir, "config.json");

    // ---- 1: recovery step one ----

    [Fact]
    public void Locking_is_tried_again_and_succeeds_on_a_later_try()
    {
        var calls = 0;
        var pauses = 0;

        var ok = RecoveryRules.Repeat(() => ++calls == 3, 4, () => pauses++);

        Assert.True(ok);
        Assert.Equal(3, calls);
        Assert.Equal(2, pauses);
    }

    [Fact]
    public void Locking_that_never_works_gives_up_after_the_last_try_without_a_pause_behind_it()
    {
        var calls = 0;
        var pauses = 0;

        var ok = RecoveryRules.Repeat(() => { calls++; return false; }, 4, () => pauses++);

        Assert.False(ok);
        Assert.Equal(4, calls);
        Assert.Equal(3, pauses);
    }

    [Fact]
    public void A_crash_loop_whose_recovery_could_not_run_counts_again_at_the_next_start()
    {
        var now = DateTimeOffset.UtcNow;
        var tracker = new CrashTracker(_dir, now: () => now);
        // The first start leaves the marker, each of the next three finds the previous run unclean.
        tracker.RegisterStart();
        tracker.RegisterStart();
        tracker.RegisterStart();
        Assert.True(tracker.RegisterStart());
        // The loop reset the counting; the recovery could not run, so it is given back.
        Assert.Equal(0, tracker.Consecutive());

        tracker.Rearm();

        now = now.AddMinutes(1);
        Assert.True(new CrashTracker(_dir, now: () => now).RegisterStart());
    }

    [Fact]
    public void A_healthy_run_after_the_failed_recovery_clears_the_given_back_count()
    {
        var tracker = new CrashTracker(_dir);
        tracker.Rearm();

        tracker.MarkHealthy();
        tracker.MarkClean();

        Assert.False(new CrashTracker(_dir).RegisterStart());
    }

    [Fact]
    public void Guard_restarts_given_back_hit_the_limit_again()
    {
        var now = DateTimeOffset.UtcNow;
        var restarts = new GuardRestarts(_dir, () => now);
        Assert.True(restarts.TryRegister());
        Assert.True(restarts.TryRegister());
        Assert.True(restarts.TryRegister());
        Assert.False(restarts.TryRegister());

        restarts.Rearm();

        Assert.False(restarts.TryRegister());
    }

    // ---- 3: lock only at an automatic sign-in ----

    [Theory]
    [InlineData(1, true)]
    [InlineData(5, true)]
    [InlineData(19, true)]
    [InlineData(25, false)]
    [InlineData(90, false)]
    public void A_session_whose_logon_follows_its_start_at_once_was_signed_in_automatically(int secondsBetween, bool automatic)
    {
        var connect = new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc);

        Assert.Equal(automatic, RecoveryRules.LooksLikeAutoSignIn(connect, connect.AddSeconds(secondsBetween)));
    }

    [Fact]
    public void A_reconnect_of_an_older_session_is_no_sign_in()
    {
        var logon = new DateTime(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc);

        Assert.False(RecoveryRules.LooksLikeAutoSignIn(logon.AddHours(3), logon));
    }

    // ---- 5: the task's access list ----

    [Fact]
    public void The_access_list_of_the_task_lets_its_user_start_it()
    {
        const string sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

        Assert.True(PolicyGroups.GrantsStart(PolicyGroups.TaskSecurityDescriptor(sid), sid));
        Assert.False(PolicyGroups.GrantsStart(PolicyGroups.TaskSecurityDescriptor(sid), "S-1-5-21-1111111111-2222222222-3333333333-1002"));
    }

    [Theory]
    [InlineData("D:P(A;;FA;;;SY)(A;;FA;;;BA)")]
    [InlineData("")]
    [InlineData("not an sddl")]
    [InlineData("D:P(A;;FA;;;SY)(D;;GX;;;S-1-5-21-1111111111-2222222222-3333333333-1001)(A;;GRGX;;;S-1-5-21-1111111111-2222222222-3333333333-1001)")]
    public void An_access_list_without_the_start_right_of_the_user_is_not_enough(string sddl)
    {
        Assert.False(PolicyGroups.GrantsStart(sddl, "S-1-5-21-1111111111-2222222222-3333333333-1001"));
    }

    [Fact]
    public void The_stored_form_of_the_start_right_counts_too()
    {
        // 0x120089 | 0x1200A0: the file rights Task Scheduler may translate GRGX into.
        Assert.True(PolicyGroups.GrantsStart("D:P(A;;0x1200a9;;;S-1-5-21-1111111111-2222222222-3333333333-1001)", "S-1-5-21-1111111111-2222222222-3333333333-1001"));
    }

    // ---- 7, 8: configuration in the fault state ----

    [Fact]
    public void A_broken_configuration_blocks_every_game_for_lack_of_the_parents_code()
    {
        var fault = ConfigStore.FaultConfig();

        Assert.True(fault.Parental.Enabled);
        Assert.True(fault.Parental.MaxAge > 0);
        Assert.True(fault.Parental.BlockUnknownAge);
        Assert.Equal("windows", fault.CouchLock);
        Assert.Equal("", fault.PasscodeHash);
    }

    [Fact]
    public void An_unreadable_file_is_tried_again_in_the_background()
    {
        File.WriteAllText(ConfigFile, """{"couchLock":"code","passcodeHash":"hash"}""");
        ConfigStore store;
        using (new FileStream(ConfigFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store = new ConfigStore(_dir, 1, Quick, unreadableRetry: TimeSpan.FromMilliseconds(40));
            Assert.Equal(ConfigFault.Unreadable, store.Fault);
        }

        var until = DateTime.UtcNow.AddSeconds(5);
        while (store.Fault is not null && DateTime.UtcNow < until) Thread.Sleep(20);

        Assert.Null(store.Fault);
        Assert.Equal("code", store.Current.CouchLock);
    }

    [Fact]
    public void The_recovery_fields_survive_a_broken_file_and_come_back_with_it()
    {
        File.WriteAllText(ConfigFile, "{ broken");
        var damaged = File.ReadAllText(ConfigFile);
        var store = new ConfigStore(_dir, 1, Quick);

        store.Update(c =>
        {
            c.ShellRemovedReason = "emergency key";
            c.PendingShellRestore = true;
        });

        // The main file is untouched, the two fields are on the side and found by the next start.
        Assert.Equal(damaged, File.ReadAllText(ConfigFile));
        Assert.True(File.Exists(store.RecoveryPath));
        var next = new ConfigStore(_dir, 1, Quick);
        Assert.Equal(ConfigFault.Invalid, next.Fault);
        Assert.Equal("emergency key", next.Current.ShellRemovedReason);
        Assert.True(next.Current.PendingShellRestore);

        // The file is repaired: the fields go into it, the side file goes.
        File.WriteAllText(ConfigFile, """{"couchLock":"code"}""");
        var repaired = new ConfigStore(_dir, 1, Quick);
        Assert.Null(repaired.Fault);
        Assert.Equal("emergency key", repaired.Current.ShellRemovedReason);
        Assert.False(File.Exists(store.RecoveryPath));
        Assert.Contains("emergency key", File.ReadAllText(ConfigFile));
    }

    [Fact]
    public void The_same_broken_content_is_copied_once_and_at_most_five_copies_stay()
    {
        File.WriteAllText(ConfigFile, "{ broken");
        for (var i = 0; i < 4; i++) _ = new ConfigStore(_dir, 1, Quick);
        Assert.Single(Directory.GetFiles(_dir, "config.json.broken-*"));

        for (var i = 0; i < 9; i++)
        {
            File.WriteAllText(ConfigFile, "{ broken " + i);
            _ = new ConfigStore(_dir, 1, Quick);
            Thread.Sleep(5);
        }

        Assert.Equal(ConfigStore.MaxBadCopies, Directory.GetFiles(_dir, "config.json.broken-*").Length);
    }

    [Fact]
    public void Saving_uses_a_temp_file_with_a_fresh_name_and_leaves_none_behind()
    {
        var store = new ConfigStore(_dir, 1, Quick);

        store.Update(c => c.Sounds = false);
        store.Update(c => c.Sounds = true);

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    // ---- 15: paired phones from older configs ----

    [Fact]
    public void A_phone_paired_before_the_use_time_existed_counts_as_used_when_the_config_is_loaded()
    {
        var longAgo = DateTimeOffset.UtcNow.AddDays(-400);
        File.WriteAllText(ConfigFile, $$"""{"remoteDevices":[{"name":"Phone","tokenHash":"abc","paired":"{{longAgo:O}}"}]}""");

        var store = new ConfigStore(_dir, 1, Quick);

        var device = Assert.Single(store.Current.RemoteDevices);
        Assert.NotNull(device.LastUsed);
        Assert.True(DateTimeOffset.UtcNow - device.LastUsed!.Value < TimeSpan.FromMinutes(1));
    }

    // ---- 9: the list of approved plugins ----

    private string MakePlugin(string folder)
    {
        var dir = Path.Combine(_dir, "plugins", folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, folder + ".dll"), "dll");
        return dir;
    }

    private static byte[] Key() => RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void Windows_leftovers_do_not_change_the_hash_of_a_plugin()
    {
        var dir = MakePlugin("Hallo");
        var before = PluginTrust.HashFolder(dir);

        File.WriteAllText(Path.Combine(dir, "desktop.ini"), "[.ShellClassInfo]");
        File.WriteAllText(Path.Combine(dir, "Thumbs.db"), "x");
        File.WriteAllText(Path.Combine(dir, "download.TMP"), "x");

        Assert.Equal(before, PluginTrust.HashFolder(dir));
        File.WriteAllText(Path.Combine(dir, "other.dll"), "x");
        Assert.NotEqual(before, PluginTrust.HashFolder(dir));
    }

    [Fact]
    public void The_signed_list_is_read_back()
    {
        var key = Key();
        var dir = MakePlugin("Hallo");
        new PluginTrust(_dir, key).Trust("Hallo", dir);

        Assert.Equal(PluginTrustState.Trusted, new PluginTrust(_dir, key).Check("Hallo", dir));
        Assert.Equal(PluginTrustState.Unknown, new PluginTrust(_dir, key).Check("Other", dir));
    }

    [Fact]
    public void A_list_that_was_edited_by_hand_is_a_fault_and_is_not_overwritten()
    {
        var key = Key();
        var dir = MakePlugin("Hallo");
        var planted = MakePlugin("Planted");
        new PluginTrust(_dir, key).Trust("Hallo", dir);
        var file = Path.Combine(_dir, "plugin-trust.json");
        var text = File.ReadAllText(file);
        // A program that only writes files adds its own plugin with the right hash, but cannot sign it.
        var hash = PluginTrust.HashFolder(planted);
        File.WriteAllText(file, text.Replace("\"Entries\":{", $"\"Entries\":{{\"Planted\":\"{hash}\","));
        var edited = File.ReadAllText(file);

        var trust = new PluginTrust(_dir, key);

        Assert.Equal(PluginTrustState.Fault, trust.Check("Planted", planted));
        Assert.Equal(PluginTrustState.Fault, trust.Check("Hallo", dir));
        Assert.True(trust.IsFaulty);
        Assert.Throws<InvalidOperationException>(() => trust.Trust("Hallo", dir));
        Assert.False(trust.AdoptExisting(Path.Combine(_dir, "plugins")));
        Assert.Equal(edited, File.ReadAllText(file));
    }

    [Fact]
    public void A_list_of_the_old_unsigned_kind_is_a_fault_when_a_key_exists()
    {
        var dir = MakePlugin("Hallo");
        File.WriteAllText(Path.Combine(_dir, "plugin-trust.json"), $$"""{"Hallo":"{{PluginTrust.HashFolder(dir)}}"}""");

        Assert.Equal(PluginTrustState.Fault, new PluginTrust(_dir, Key()).Check("Hallo", dir));
    }

    [Fact]
    public void Garbage_in_place_of_the_list_is_a_fault()
    {
        var dir = MakePlugin("Hallo");
        File.WriteAllText(Path.Combine(_dir, "plugin-trust.json"), "\0\0\0 not json");

        Assert.Equal(PluginTrustState.Fault, new PluginTrust(_dir, Key()).Check("Hallo", dir));
    }

    [Fact]
    public void A_list_signed_with_another_key_is_a_fault()
    {
        var dir = MakePlugin("Hallo");
        new PluginTrust(_dir, Key()).Trust("Hallo", dir);

        Assert.Equal(PluginTrustState.Fault, new PluginTrust(_dir, Key()).Check("Hallo", dir));
    }

    // ---- 10: the phone types only where the field is known ----

    [Theory]
    [InlineData("steamwebhelper", false)]
    [InlineData("steam", false)]
    [InlineData("EpicGamesLauncher", false)]
    [InlineData("chrome", false)]
    [InlineData("msedge", false)]
    [InlineData("Firefox", false)]
    public void In_a_launcher_or_browser_an_unknown_field_is_not_typed_into(string process, bool own)
    {
        Assert.True(SecretFields.CannotBeVerified(new FocusedField(null, ["Username"]), process, own));
    }

    [Fact]
    public void A_field_that_says_it_is_no_password_box_is_fine_in_a_launcher_too()
    {
        Assert.False(SecretFields.CannotBeVerified(new FocusedField(false, ["Username"]), "steamwebhelper", ownProcess: false));
    }

    [Fact]
    public void CouchHuds_own_browser_needs_a_known_field_as_well()
    {
        Assert.True(SecretFields.CannotBeVerified(new FocusedField(null, []), "CouchHud", ownProcess: true));
        Assert.False(SecretFields.CannotBeVerified(new FocusedField(false, []), "CouchHud", ownProcess: true));
    }

    [Theory]
    [InlineData("Game")]
    [InlineData("notepad")]
    [InlineData(null)]
    public void Games_and_other_programs_stay_allowed_with_an_unknown_field(string? process)
    {
        Assert.False(SecretFields.CannotBeVerified(new FocusedField(null, []), process, ownProcess: false));
    }

    // ---- 11: the WebView2 helpers ----

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\130.0.1\msedgewebview2.exe", true)]
    [InlineData(@"C:\Program Files (x86)\Microsoft\Edge\Application\130.0.1\msedgewebview2.exe", true)]
    [InlineData(@"C:\Users\x\Downloads\msedgewebview2.exe", false)]
    [InlineData(@"C:\Program Files (x86)\Microsoft\EdgeWebView\Application\130.0.1\other.exe", false)]
    [InlineData(null, false)]
    public void Only_the_runtimes_own_program_counts_as_a_webview_helper_by_its_path(string? path, bool expected)
    {
        Assert.Equal(expected, LockedForeground.IsWebViewRuntimePath(path));
    }

    [Fact]
    public void A_program_that_only_carries_the_name_of_the_helper_is_not_one_of_ours()
    {
        var context = new ForegroundContext(true, false, false, false, false, false, Locked: true);
        LockedWindow Window(bool genuine) => new("msedgewebview2", "x", false, false, OwnTree: () => true, GameTree: () => false, Fullscreen: () => false)
        {
            WebViewGenuine = () => genuine
        };

        Assert.Equal(ForegroundReason.OwnWindow, LockedForeground.Decide(context, Window(true)).Reason);
        Assert.NotEqual(ForegroundReason.OwnWindow, LockedForeground.Decide(context, Window(false)).Reason);
    }

    // ---- 14: elevated uninstall and links in the user's folder ----

    [Fact]
    public void A_plain_data_folder_is_safe_for_the_elevated_uninstall()
    {
        File.WriteAllText(Path.Combine(_dir, "guard.json"), "{}");

        Assert.True(ElevatedGuard.IsSafeUserFolder(_dir, Path.GetDirectoryName(_dir)));
        Assert.True(ElevatedGuard.IsSafeUserFolder(Path.Combine(_dir, "missing"), Path.GetDirectoryName(_dir)));
    }

    [Fact]
    public void A_data_folder_that_is_a_link_is_left_alone()
    {
        var target = Directory.CreateTempSubdirectory("couchhud-target-").FullName;
        var link = Path.Combine(_dir, "CouchHud");
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // No right to make links here (needs developer mode or administrator): nothing to test on this machine.
            Directory.Delete(target, true);
            return;
        }
        try
        {
            Assert.False(ElevatedGuard.IsSafeUserFolder(link, _dir));
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(target, true);
        }
    }

    [Fact]
    public void A_state_file_that_is_a_link_is_left_alone()
    {
        var target = Path.Combine(_dir, "elsewhere.txt");
        File.WriteAllText(target, "x");
        var data = Path.Combine(_dir, "CouchHud");
        Directory.CreateDirectory(data);
        try
        {
            File.CreateSymbolicLink(Path.Combine(data, "guard.json"), target);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return;
        }

        Assert.False(ElevatedGuard.IsSafeUserFolder(data, _dir));
    }
}
