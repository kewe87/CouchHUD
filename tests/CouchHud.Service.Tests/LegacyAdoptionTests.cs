using CouchHud.Core.Guard;
using CouchHud.Core.Platform;
using CouchHud.Service.Authorization;
using CouchHud.Service.Contracts;
using CouchHud.Service.State;

namespace CouchHud.Service.Tests;

/// <summary>
/// Finding B1 of the 0.4.1 acceptance test: the SYSTEM tasks of 0.4.0 (startable by their user) must not survive an update, and the lock
/// they set must neither go away nor become releasable without proof.
/// </summary>
public class LegacyAdoptionTests
{
    private static readonly string Grant = Convert.ToBase64String(new byte[32]);

    private static string Task(string sid, string leaf) => $@"CouchHud\{sid}\{leaf}";

    private static readonly string[] Leaves = ["Policy-lock-on", "Policy-lock-off", "Policy-taskmgr-on", "Policy-taskmgr-off", "Policy-cleanup", "Couch-WSearch", "Autologon-off"];

    /// <summary>What 0.4.0 left for an account: its tasks, its files in the program folder and (when <paramref name="locked"/>) the lock in its hive.</summary>
    private static void OldSetup(OpsHarness ops, string sid, bool locked, IEnumerable<string>? leaves = null, bool releaseFile = true)
    {
        foreach (var leaf in leaves ?? Leaves) ops.Tasks.Tasks.Add(Task(sid, leaf));
        var dir = Path.Combine(ops.AppDir, "policy", sid);
        Directory.CreateDirectory(dir);
        if (releaseFile) File.WriteAllText(Path.Combine(dir, "lock-off.reg"), "x");
        File.WriteAllLines(Path.Combine(dir, PolicyGroups.CleanupKeysFileName), [LockHardening.ExplorerKey]);
        if (locked) SetLock(ops.Hives.Loaded.TryGetValue(sid, out var r) ? r : ops.Hives.OnDisk[sid]);
    }

    private static void SetLock(IRegistry registry)
    {
        foreach (var c in PolicyGroups.Lock.Changes)
        {
            if (c.Kind == GuardKind.RegistryDword) registry.WriteDword(c.Location, c.Name, int.Parse(c.Value, System.Globalization.CultureInfo.InvariantCulture));
            else registry.WriteString(c.Location, c.Name, c.Value);
        }
    }

    /// <summary>
    /// Every task the old setup left is restricted, but an account's Autologon-off (review M-3: it only switches the automatic sign-in off),
    /// which gets exactly SYSTEM, administrators and its own user's start right.
    /// </summary>
    private static void AssertRestrictedButAutoLogonOff(OpsHarness ops)
    {
        Assert.All(ops.Tasks.Tasks.Where(t => !LegacyAdoption.IsAutoLogonOff(t)), t => Assert.Contains(t, ops.Tasks.Restricted));
        Assert.DoesNotContain(ops.Tasks.Restricted, LegacyAdoption.IsAutoLogonOff);
        Assert.All(ops.Tasks.Tasks.Where(LegacyAdoption.IsAutoLogonOff), t => Assert.Equal(LegacyAdoption.AccountOf(t), ops.Tasks.UserStartOnly[t]));
    }

    private static void SignOut(OpsHarness ops, string sid)
    {
        ops.Hives.OnDisk[sid] = ops.Hives.Loaded[sid];
        ops.Hives.Loaded.Remove(sid);
    }

    private static void SignIn(OpsHarness ops, string sid)
    {
        ops.Hives.Loaded[sid] = ops.Hives.OnDisk[sid];
        ops.Hives.OnDisk.Remove(sid);
    }

    // ---- the plan (pure) ----

    [Theory]
    [InlineData("lock-off.reg")]
    [InlineData("lock-on.reg")]
    public void A_lock_whose_files_the_old_setup_wrote_for_this_account_is_adopted(string file)
    {
        var registry = new MemoryRegistry();
        SetLock(registry);
        var plan = LegacyAdoption.Plan([], name => name == file, registry);
        Assert.Equal(["lock"], plan.Adopt);
        Assert.Empty(plan.Foreign);
    }

    [Fact]
    public void Values_without_the_old_files_are_foreign()
    {
        var registry = new MemoryRegistry();
        SetLock(registry);
        // The files of another group explain nothing about this one.
        var plan = LegacyAdoption.Plan([], name => name.StartsWith("taskmgr-", StringComparison.Ordinal), registry);
        Assert.Empty(plan.Adopt);
        Assert.Equal(["lock"], plan.Foreign);
    }

    [Fact]
    public void Nothing_is_planned_for_a_group_the_service_holds_or_one_without_values()
    {
        var registry = new MemoryRegistry();
        Assert.Same(AdoptionPlan.Nothing, LegacyAdoption.Plan([], _ => true, registry));
        SetLock(registry);
        Assert.Same(AdoptionPlan.Nothing, LegacyAdoption.Plan(["lock"], _ => true, registry));
    }

    [Fact]
    public void A_task_is_no_evidence_since_a_standard_user_can_create_one()
    {
        // A child creates CouchHud\<its SID>\Policy-lock-off itself (running as itself) next to lock values a parent set some other way.
        using var ops = new OpsHarness();
        ops.Tasks.Tasks.Add(Task(Callers.UserSid, "Policy-lock-off"));
        ops.Tasks.Tasks.Add(Task(Callers.UserSid, "Policy-lock-on"));
        SetLock(ops.Hives.Loaded[Callers.UserSid]);

        ops.Operations.SweepLegacyTasks(CancellationToken.None);

        Assert.False(ops.State.Accounts.TryGetValue(Callers.UserSid, out var account) && account.Groups.Count > 0);
        Assert.Empty(ops.Tasks.Deleted);
        Assert.True(PolicyGroups.Applied(ops.Hives.Loaded[Callers.UserSid], PolicyGroups.Lock));
    }

    [Theory]
    [InlineData(@"CouchHud\S-1-5-21-1111111111-2222222222-3333333333-1001\Policy-lock-off", "S-1-5-21-1111111111-2222222222-3333333333-1001")]
    [InlineData(@"\CouchHud\S-1-5-21-1111111111-2222222222-3333333333-1001\Autologon-off", "S-1-5-21-1111111111-2222222222-3333333333-1001")]
    [InlineData(@"CouchHud\Policy-lock-off", null)]
    [InlineData(@"CouchHud\S-1-5-18\Policy-lock-off", null)]
    [InlineData(@"Other\S-1-5-21-1111111111-2222222222-3333333333-1001\Policy-lock-off", null)]
    [InlineData(@"CouchHud\S-1-5-21-1111111111-2222222222-3333333333-1001\a\b", null)]
    public void The_account_of_a_task_is_its_folder(string task, string? sid) => Assert.Equal(sid, LegacyAdoption.AccountOf(task));

    [Fact]
    public void Every_task_in_the_CouchHud_folder_is_restricted_but_an_accounts_Autologon_off()
    {
        string[] tasks = [Task(Callers.UserSid, "Policy-lock-off"), Task(Callers.UserSid, "Autologon-off"), @"CouchHud\Policy-lock-off", @"CouchHud\Autologon-off", @"Other\Task"];
        Assert.Equal([tasks[0], tasks[2], tasks[3]], LegacyAdoption.ToRestrict(tasks));
    }

    [Theory]
    [InlineData(@"\CouchHud\S-1-5-21-1111111111-2222222222-3333333333-1001\Autologon-off", true)]
    [InlineData(@"CouchHud\S-1-5-21-1111111111-2222222222-3333333333-1001\autologon-OFF", true)]
    [InlineData(@"CouchHud\Autologon-off", false)]
    [InlineData(@"CouchHud\S-1-5-21-1111111111-2222222222-3333333333-1001\Autologon-off2", false)]
    [InlineData(@"CouchHud\S-1-5-21-1111111111-2222222222-3333333333-1001\Policy-lock-off", false)]
    [InlineData(@"CouchHud\S-1-5-18\Autologon-off", false)]
    [InlineData(@"Other\S-1-5-21-1111111111-2222222222-3333333333-1001\Autologon-off", false)]
    public void Only_an_accounts_own_Autologon_off_keeps_its_start_right(string task, bool expected) =>
        Assert.Equal(expected, LegacyAdoption.IsAutoLogonOff(task));

    [Fact]
    public void Restricting_all_goes_on_past_a_task_that_fails_or_throws()
    {
        var tasks = new FakeTasks();
        tasks.Tasks.AddRange([Task(Callers.UserSid, "Policy-lock-off"), Task(Callers.UserSid, "Policy-lock-on"), @"CouchHud\Policy-lock-off"]);
        tasks.FailRestrict.Add(Task(Callers.UserSid, "Policy-lock-off"));
        tasks.ThrowRestrict.Add(Task(Callers.UserSid, "Policy-lock-on"));
        var log = new CapturingSink();

        Assert.Equal(2, LegacyAdoption.RestrictAll(tasks, log));
        Assert.Contains(@"CouchHud\Policy-lock-off", tasks.Restricted);
        Assert.Contains(log.Lines, l => l.Contains("2 old tasks could not be restricted", StringComparison.Ordinal));
    }

    [Fact]
    public void Restricting_all_without_a_task_list_says_so()
    {
        var tasks = new FakeTasks { ThrowList = true };
        var log = new CapturingSink();
        Assert.Equal(-1, LegacyAdoption.RestrictAll(tasks, log));
        Assert.Contains(log.Lines, l => l.Contains("could not be listed", StringComparison.Ordinal));
    }

    [Fact]
    public void The_sweep_goes_on_past_an_account_that_fails()
    {
        using var ops = new OpsHarness();
        OldSetup(ops, Callers.UserSid, locked: true);
        OldSetup(ops, Callers.OtherSid, locked: true);
        ops.Hives.ThrowOpen.Add(Callers.UserSid);

        Assert.True(ops.Operations.SweepLegacyTasks(CancellationToken.None));

        Assert.Contains(Callers.UserSid, ops.Operations.LegacyPending);
        Assert.True(ops.State.Accounts[Callers.OtherSid].LegacyLock);
        Assert.True(ops.Operations.LegacySwept);
    }

    [Fact]
    public void A_sweep_with_wait_runs_after_the_one_that_holds_the_gate()
    {
        using var ops = new OpsHarness();
        OldSetup(ops, Callers.UserSid, locked: true);
        Assert.False(ops.Operations.LegacySwept);
        Assert.True(ops.Operations.SweepLegacyTasks(CancellationToken.None, wait: true));
        Assert.True(ops.Operations.LegacySwept);
        Assert.Equal(0, ops.Operations.RestrictLegacyTasks());
    }

    [Theory]
    [InlineData("D:P(A;;FA;;;SY)(A;;FA;;;BA)", true)]
    [InlineData("D:(A;;FA;;;SY)(A;;FA;;;BA)", true)]
    [InlineData("D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;0x1200a9;;;S-1-5-21-1111111111-2222222222-3333333333-1001)", false)]
    [InlineData("D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;GR;;;BU)", false)]
    [InlineData("D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;WD)", false)]
    [InlineData("D:(D;;FA;;;WD)(A;;FA;;;SY)", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("not a descriptor", false)]
    public void Only_SYSTEM_and_administrators_count_as_restricted(string? sddl, bool expected) =>
        Assert.Equal(expected, PolicyGroups.OnlyAdministrators(sddl));

    [Fact]
    public void The_old_descriptor_with_the_users_start_right_is_not_restricted_and_the_new_one_is() =>
        Assert.True(!PolicyGroups.OnlyAdministrators(PolicyGroups.TaskSecurityDescriptor(Callers.UserSid)) && PolicyGroups.OnlyAdministrators(PolicyGroups.AdministratorsOnlyDescriptor));

    // ---- the sweep ----

    [Fact]
    public void The_sweep_restricts_every_task_and_takes_over_the_lock_of_a_signed_in_account_before_removing_its_tasks()
    {
        using var ops = new OpsHarness();
        OldSetup(ops, Callers.UserSid, locked: true);
        ops.Tasks.Tasks.Add(@"CouchHud\Policy-lock-off");   // machine-wide, an earlier version

        Assert.True(ops.Operations.SweepLegacyTasks(CancellationToken.None));

        // Restricted first, all of them (the reserve and the machine-wide one stay, administrators only).
        Assert.Contains(@"CouchHud\Policy-lock-off", ops.Tasks.Restricted);
        Assert.Contains(Task(Callers.UserSid, "Policy-lock-off"), ops.Tasks.Restricted);
        Assert.Equal([@"CouchHud\Policy-lock-off", Task(Callers.UserSid, "Autologon-off")], ops.Tasks.Tasks.Order(StringComparer.Ordinal));
        // The lock is still on and now the service's.
        Assert.True(PolicyGroups.Applied(ops.Hives.Loaded[Callers.UserSid], PolicyGroups.Lock));
        var account = ops.State.Accounts[Callers.UserSid];
        Assert.Equal(["lock"], account.Groups);
        Assert.True(account.LegacyLock);
        Assert.True(account.Migrated);
        Assert.Contains(LockHardening.ExplorerKey, account.CreatedKeys);
        Assert.False(Directory.Exists(Path.Combine(ops.AppDir, "policy", Callers.UserSid)));
        Assert.Contains(ops.OpsLog.Lines, l => l.Contains("[legacy] lock of", StringComparison.Ordinal));

        // Again: nothing more to do, nothing changes.
        var before = ops.Tasks.Deleted.Count;
        Assert.True(ops.Operations.SweepLegacyTasks(CancellationToken.None));
        Assert.Equal(before, ops.Tasks.Deleted.Count);
        Assert.Equal(["lock"], ops.State.Accounts[Callers.UserSid].Groups);
    }

    [Fact]
    public async Task A_signed_out_account_keeps_its_lock_and_restricted_tasks_until_it_signs_in()
    {
        using var ops = new OpsHarness();
        SignOut(ops, Callers.OtherSid);
        OldSetup(ops, Callers.OtherSid, locked: true);

        ops.Operations.SweepLegacyTasks(CancellationToken.None);

        // Its hive is not loaded by the sweep (that could block its sign-in); its tasks stay, but no user may start them.
        Assert.Empty(ops.Hives.LoadedFromDisk);
        Assert.Equal([Callers.OtherSid], ops.Operations.LegacyPending);
        AssertRestrictedButAutoLogonOff(ops);
        Assert.Empty(ops.Tasks.Deleted);
        Assert.False(ops.State.Accounts.ContainsKey(Callers.OtherSid));

        // It signs in; the shell sets its lock as always: the old lock is taken over instead of "set by someone else".
        SignIn(ops, Callers.OtherSid);
        var applied = await ops.Call<LockPoliciesData>(Callers.User(Callers.OtherSid), Operation.ApplyLockPolicies, new ApplyLockPoliciesArgs(["lock"]));
        Assert.True(applied.Ok, applied.Code);
        Assert.Equal(["lock"], applied.Data!.Groups);
        Assert.True(ops.State.Accounts[Callers.OtherSid].LegacyLock);
        Assert.Empty(ops.Operations.LegacyPending);
        Assert.DoesNotContain(Task(Callers.OtherSid, "Policy-lock-off"), ops.Tasks.Tasks);
        Assert.True(PolicyGroups.Applied(ops.Hives.Loaded[Callers.OtherSid], PolicyGroups.Lock));
    }

    [Fact]
    public void The_sweep_after_a_sign_in_finishes_a_waiting_account()
    {
        using var ops = new OpsHarness();
        SignOut(ops, Callers.OtherSid);
        OldSetup(ops, Callers.OtherSid, locked: true);
        ops.Operations.SweepLegacyTasks(CancellationToken.None);
        SignIn(ops, Callers.OtherSid);

        ops.Operations.SweepLegacyTasks(CancellationToken.None);

        Assert.Empty(ops.Operations.LegacyPending);
        Assert.Equal(["lock"], ops.State.Accounts[Callers.OtherSid].Groups);
        Assert.True(PolicyGroups.Applied(ops.Hives.Loaded[Callers.OtherSid], PolicyGroups.Lock));
    }

    [Fact]
    public void Values_without_evidence_are_not_taken_over_and_the_tasks_stay_restricted()
    {
        using var ops = new OpsHarness();
        // Only service tasks of the old setup and no release file: the lock values came from somewhere else (an administrator, a policy).
        OldSetup(ops, Callers.UserSid, locked: true, leaves: ["Couch-WSearch", "Autologon-off"], releaseFile: false);

        ops.Operations.SweepLegacyTasks(CancellationToken.None);

        Assert.False(ops.State.Accounts.TryGetValue(Callers.UserSid, out var account) && account.Groups.Count > 0);
        Assert.Empty(ops.Tasks.Deleted);
        AssertRestrictedButAutoLogonOff(ops);
        Assert.True(PolicyGroups.Applied(ops.Hives.Loaded[Callers.UserSid], PolicyGroups.Lock));
        Assert.Contains(ops.OpsLog.Lines, l => l.Contains("neither the service nor the old setup", StringComparison.Ordinal));
    }

    [Fact]
    public void A_task_that_cannot_be_restricted_is_named_in_the_log()
    {
        using var ops = new OpsHarness();
        SignOut(ops, Callers.OtherSid);
        OldSetup(ops, Callers.OtherSid, locked: true);
        ops.Tasks.FailRestrict.Add(Task(Callers.OtherSid, "Policy-lock-off"));
        ops.Operations.SweepLegacyTasks(CancellationToken.None);
        Assert.Contains(ops.OpsLog.Lines, l => l.Contains("could not be restricted", StringComparison.Ordinal));
    }

    // ---- releasing an adopted lock ----

    [Fact]
    public async Task An_adopted_lock_needs_a_grant_while_the_accounts_code_is_not_with_the_service()
    {
        using var ops = new OpsHarness();
        OldSetup(ops, Callers.UserSid, locked: true);
        ops.Operations.SweepLegacyTasks(CancellationToken.None);

        // No code enrolled with the service: the table alone would let it go; the old lock did not.
        Assert.Equal(ErrorCodes.NeedGrant, (await ops.Call(Callers.User(), Operation.ReleaseLockPolicies, new ReleaseLockPoliciesArgs(["lock"]))).Code);
        Assert.Equal(ErrorCodes.NeedGrant, (await ops.Call(Callers.User(), Operation.CleanupPolicyKeys)).Code);
        Assert.True(PolicyGroups.Applied(ops.Hives.Loaded[Callers.UserSid], PolicyGroups.Lock));

        // An elevated administrator may.
        var admin = await ops.Call(Callers.ElevatedAdmin(), Operation.ReleaseLockPolicies, new ReleaseLockPoliciesArgs(["lock"]));
        Assert.True(admin.Ok, admin.Code);
        Assert.Empty(PolicyRelease.OursPresent(ops.Hives.Loaded[Callers.UserSid], PolicyGroups.Lock));
        Assert.False(ops.State.Accounts[Callers.UserSid].LegacyLock);
    }

    [Fact]
    public async Task An_adopted_lock_goes_with_an_administrators_approval()
    {
        using var ops = new OpsHarness(grants: new ValidGrant(Grant, GrantKinds.WindowsAdmin));
        OldSetup(ops, Callers.UserSid, locked: true);
        ops.Operations.SweepLegacyTasks(CancellationToken.None);
        var response = await ops.Call(Callers.User(), Operation.ReleaseLockPolicies, new ReleaseLockPoliciesArgs(["lock"]), Grant);
        Assert.True(response.Ok, response.Code);
        Assert.Empty(PolicyRelease.OursPresent(ops.Hives.Loaded[Callers.UserSid], PolicyGroups.Lock));
    }

    [Fact]
    public async Task A_parents_grant_does_not_release_an_adopted_lock()
    {
        // Review H-1, second round: the parents' code of 0.4.0 was in the config the child writes, and one set through the pipe before the
        // take-over is the child's own.
        using var ops = new OpsHarness(codeEnrolled: true, grants: new ValidGrant(Grant, GrantKinds.Parent));
        OldSetup(ops, Callers.UserSid, locked: true);
        ops.Operations.SweepLegacyTasks(CancellationToken.None);
        Assert.Equal(ErrorCodes.NeedGrant, (await ops.Call(Callers.User(), Operation.ReleaseLockPolicies, new ReleaseLockPoliciesArgs(["lock"]), Grant)).Code);
        Assert.Equal(ErrorCodes.NeedGrant, (await ops.Call(Callers.User(), Operation.CleanupPolicyKeys, null, Grant)).Code);
        Assert.True(PolicyGroups.Applied(ops.Hives.Loaded[Callers.UserSid], PolicyGroups.Lock));
    }

    [Fact]
    public void An_account_counts_as_a_lock_of_0_4_0_already_before_its_take_over()
    {
        // Review H-1, second round: between the service's start and the take-over the account must not be open for codes or modes.
        using var ops = new OpsHarness();
        OldSetup(ops, Callers.UserSid, locked: true);
        SignOut(ops, Callers.OtherSid);
        OldSetup(ops, Callers.OtherSid, locked: true);
        // The old files of the setup are there: before any sweep.
        Assert.True(ops.Operations.IsLegacyLocked(Callers.UserSid));
        Assert.True(ops.Operations.IsLegacyLocked(Callers.OtherSid));

        ops.Operations.SweepLegacyTasks(CancellationToken.None);
        // Taken over (signed in) or waiting for its sign-in (pending), both still count.
        Assert.True(ops.State.Accounts[Callers.UserSid].LegacyLock);
        Assert.Contains(Callers.OtherSid, ops.Operations.LegacyPending);
        Assert.True(ops.Operations.IsLegacyLocked(Callers.UserSid));
        Assert.True(ops.Operations.IsLegacyLocked(Callers.OtherSid));
        Assert.False(ops.Operations.IsLegacyLocked("S-1-5-21-1111111111-2222222222-3333333333-1099"));
    }

    [Fact]
    public void A_waiting_account_counts_even_when_its_files_are_gone()
    {
        using var ops = new OpsHarness();
        SignOut(ops, Callers.OtherSid);
        OldSetup(ops, Callers.OtherSid, locked: true);
        ops.Operations.SweepLegacyTasks(CancellationToken.None);
        Directory.Delete(Path.Combine(ops.AppDir, "policy", Callers.OtherSid), recursive: true);
        Assert.Contains(Callers.OtherSid, ops.Operations.LegacyPending);
        Assert.True(ops.Operations.IsLegacyLocked(Callers.OtherSid));
    }

    [Fact]
    public void Without_a_state_the_old_files_decide()
    {
        using var ops = new OpsHarness(stateFolder: false);
        Assert.Null(ops.Store.Current);
        Assert.False(ops.Operations.IsLegacyLocked(Callers.UserSid));
        OldSetup(ops, Callers.UserSid, locked: true);
        Assert.True(ops.Operations.IsLegacyLocked(Callers.UserSid));
    }

    [Fact]
    public void The_take_over_forgets_the_accounts_codes_and_mode()
    {
        using var ops = new OpsHarness();
        var forgotten = new List<string>();
        ops.Operations.ForgetCodes = sid =>
        {
            forgotten.Add(sid);
            return true;
        };
        OldSetup(ops, Callers.UserSid, locked: true);
        OldSetup(ops, Callers.OtherSid, locked: false);
        ops.Operations.SweepLegacyTasks(CancellationToken.None);
        // Only the account whose lock was taken over; one without a lock of 0.4.0 keeps what it has.
        Assert.Equal([Callers.UserSid], forgotten);
        Assert.False(ops.Operations.IsLegacyLocked(Callers.OtherSid));
    }

    [Fact]
    public void A_take_over_whose_codes_cannot_be_forgotten_says_so_and_stays_a_lock_of_0_4_0()
    {
        using var ops = new OpsHarness();
        ops.Operations.ForgetCodes = _ => false;
        OldSetup(ops, Callers.UserSid, locked: true);
        ops.Operations.SweepLegacyTasks(CancellationToken.None);
        Assert.Contains(ops.OpsLog.Lines, l => l.Contains("could not be forgotten", StringComparison.Ordinal));
        Assert.True(ops.Operations.IsLegacyLocked(Callers.UserSid));
    }

    [Theory]
    [InlineData(GrantKinds.Couch)]
    [InlineData(GrantKinds.WindowsUnlock)]
    public async Task The_couchs_own_proof_does_not_release_an_adopted_lock_until_it_is_confirmed(GrantKinds kind)
    {
        // Review H-1: a code with the service (planted through the free first enrollment or the config) or the Windows mode set by the user
        // must not be enough while the lock of 0.4.0 is not confirmed by a parent or an administrator.
        using var ops = new OpsHarness(codeEnrolled: true, grants: new ValidGrant(Grant, kind));
        OldSetup(ops, Callers.UserSid, locked: true);
        ops.Operations.SweepLegacyTasks(CancellationToken.None);

        Assert.Equal(ErrorCodes.NeedGrant, (await ops.Call(Callers.User(), Operation.ReleaseLockPolicies, new ReleaseLockPoliciesArgs(["lock"]), Grant)).Code);
        Assert.Equal(ErrorCodes.NeedGrant, (await ops.Call(Callers.User(), Operation.CleanupPolicyKeys, null, Grant)).Code);
        Assert.True(PolicyGroups.Applied(ops.Hives.Loaded[Callers.UserSid], PolicyGroups.Lock));

        // Confirmed (a parent's or an administrator's approval of the code or the mode, see CodeHandlers): the couch's proof as always.
        Assert.True(ops.Operations.EndLegacyLock(Callers.UserSid));
        Assert.False(ops.Operations.IsLegacyLocked(Callers.UserSid));
        if (kind == GrantKinds.Couch)
        {
            var response = await ops.Call(Callers.User(), Operation.ReleaseLockPolicies, new ReleaseLockPoliciesArgs(["lock"]), Grant);
            Assert.True(response.Ok, response.Code);
            Assert.Empty(PolicyRelease.OursPresent(ops.Hives.Loaded[Callers.UserSid], PolicyGroups.Lock));
        }
    }

    [Fact]
    public void Ending_a_legacy_lock_changes_nothing_for_an_account_without_one()
    {
        using var ops = new OpsHarness();
        Assert.False(ops.Operations.IsLegacyLocked(Callers.UserSid));
        Assert.True(ops.Operations.EndLegacyLock(Callers.UserSid));
        Assert.False(ops.State.Accounts.ContainsKey(Callers.UserSid));
    }

    [Fact]
    public void A_confirmation_before_the_take_over_does_not_end_it()
    {
        // The old files are still there: the take-over has yet to come (and forgets the codes then), so it is asked again afterwards.
        using var ops = new OpsHarness();
        SignOut(ops, Callers.UserSid);
        OldSetup(ops, Callers.UserSid, locked: true);
        Assert.False(ops.Operations.EndLegacyLock(Callers.UserSid));
        Assert.True(ops.Operations.IsLegacyLocked(Callers.UserSid));
    }

    [Fact]
    public void The_Autologon_off_task_gets_exactly_its_users_start_right()
    {
        using var ops = new OpsHarness();
        SignOut(ops, Callers.OtherSid);
        OldSetup(ops, Callers.OtherSid, locked: true);
        Assert.Equal(0, ops.Operations.RestrictLegacyTasks());
        Assert.Equal(Callers.OtherSid, ops.Tasks.UserStartOnly[Task(Callers.OtherSid, "Autologon-off")]);
        ops.Tasks.FailRestrict.Add(Task(Callers.OtherSid, "Autologon-off"));
        Assert.Equal(1, ops.Operations.RestrictLegacyTasks());
    }

    [Theory]
    [InlineData("D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;S-1-5-21-1111111111-2222222222-3333333333-1001)", true)]
    [InlineData("D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FRFX;;;S-1-5-21-1111111111-2222222222-3333333333-1001)", true)]
    [InlineData("D:P(A;;FA;;;SY)(A;;FA;;;BA)", true)]
    [InlineData("D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;S-1-5-21-1111111111-2222222222-3333333333-1001)", false)]
    [InlineData("D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGWGX;;;S-1-5-21-1111111111-2222222222-3333333333-1001)", false)]
    [InlineData("D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;S-1-5-21-1111111111-2222222222-3333333333-1002)", false)]
    [InlineData("D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;BU)", false)]
    [InlineData(null, false)]
    public void Only_SYSTEM_administrators_and_the_users_start_right_count_as_user_start_only(string? sddl, bool expected) =>
        Assert.Equal(expected, PolicyGroups.OnlyUserStart(sddl, "S-1-5-21-1111111111-2222222222-3333333333-1001"));

    [Fact]
    public async Task Status_says_whether_the_callers_lock_waits_for_its_confirmation()
    {
        using var ops = new OpsHarness();
        ops.Harness.State.LegacyLookup = ops.Operations.IsLegacyLocked;
        OldSetup(ops, Callers.UserSid, locked: true);
        ops.Operations.SweepLegacyTasks(CancellationToken.None);
        Assert.True((await ops.Call<StatusData>(Callers.User(), Operation.Status)).Data!.LegacyLock);
        Assert.Null((await ops.Call<StatusData>(Callers.User(Callers.OtherSid), Operation.Status)).Data!.LegacyLock);
        ops.Operations.EndLegacyLock(Callers.UserSid);
        Assert.Null((await ops.Call<StatusData>(Callers.User(), Operation.Status)).Data!.LegacyLock);
    }

    [Fact]
    public async Task Uninstall_releases_an_old_lock_that_was_not_taken_over_yet()
    {
        using var ops = new OpsHarness();
        SignOut(ops, Callers.OtherSid);
        OldSetup(ops, Callers.OtherSid, locked: true);

        var response = await ops.Call<ReleaseAllAccountsData>(Callers.ElevatedAdmin(), Operation.ReleaseAllAccounts);

        Assert.True(response.Ok, response.Code);
        Assert.Contains(Callers.OtherSid, response.Data!.Released);
        Assert.Empty(PolicyRelease.OursPresent(ops.Hives.OnDisk[Callers.OtherSid], PolicyGroups.Lock));
        Assert.DoesNotContain(Task(Callers.OtherSid, "Policy-lock-off"), ops.Tasks.Tasks);
    }
}
