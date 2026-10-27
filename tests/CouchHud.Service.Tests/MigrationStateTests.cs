using System.Diagnostics;
using CouchHud.Core.Guard;
using CouchHud.Privileged;
using CouchHud.Service.Contracts;
using CouchHud.Service.Dispatch;
using CouchHud.Service.State;

namespace CouchHud.Service.Tests;

/// <summary>MigrateTasks, ReleaseAllAccounts, the state file and its folder.</summary>
public class MigrationStateTests
{
    private static string Folder(string sid) => $@"CouchHud\{sid}";

    private static void OldSetup(OpsHarness ops, string sid)
    {
        foreach (var leaf in new[] { "Policy-lock-on", "Policy-lock-off", "Policy-taskmgr-on", "Policy-taskmgr-off", "Policy-cleanup", "Couch-WSearch", "PC-WSearch", "Autologon-off" })
        {
            ops.Tasks.Tasks.Add($@"{Folder(sid)}\{leaf}");
        }
        var dir = Path.Combine(ops.AppDir, "policy", sid);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "lock-on.reg"), "x");
        File.WriteAllLines(Path.Combine(dir, PolicyGroups.CleanupKeysFileName), [LockHardening.ExplorerKey, @"HKCU\Software\Evil"]);
        File.WriteAllText(Path.Combine(ops.AppDir, "policy", PolicyGroups.AutoLogonOffFileName), "x");
    }

    [Fact]
    public async Task Migration_removes_only_the_callers_replaced_tasks_and_files()
    {
        using var ops = new OpsHarness();
        OldSetup(ops, Callers.UserSid);
        OldSetup(ops, Callers.OtherSid);
        ops.Tasks.Tasks.Add(@"CouchHud\Policy-lock-on");   // legacy, machine-wide
        ops.Tasks.Tasks.Add($@"CouchHud\{Callers.UserSid}\Something-else");

        var response = await ops.Call<MigrateTasksData>(Callers.User(), Operation.MigrateTasks);
        Assert.True(response.Ok, response.Code);
        Assert.Equal(7, response.Data!.Removed);
        Assert.All(ops.Tasks.Deleted, t => Assert.StartsWith(Folder(Callers.UserSid) + @"\", t));
        Assert.Contains($@"{Folder(Callers.UserSid)}\Autologon-off", ops.Tasks.Tasks);
        Assert.Contains($@"CouchHud\{Callers.UserSid}\Something-else", ops.Tasks.Tasks);
        Assert.Contains(@"CouchHud\Policy-lock-on", ops.Tasks.Tasks);
        Assert.Equal(8, ops.Tasks.Tasks.Count(t => t.StartsWith(Folder(Callers.OtherSid), StringComparison.Ordinal)));

        Assert.False(Directory.Exists(Path.Combine(ops.AppDir, "policy", Callers.UserSid)));
        Assert.True(Directory.Exists(Path.Combine(ops.AppDir, "policy", Callers.OtherSid)));
        Assert.True(File.Exists(Path.Combine(ops.AppDir, "policy", PolicyGroups.AutoLogonOffFileName)));

        var account = ops.State.Accounts[Callers.UserSid];
        Assert.True(account.Migrated);
        // The noted key is taken over; whatever else was in the file is not a candidate and is dropped.
        Assert.Equal([LockHardening.ExplorerKey], account.CreatedKeys);

        // Again: nothing more to do.
        var again = await ops.Call<MigrateTasksData>(Callers.User(), Operation.MigrateTasks);
        Assert.True(again.Ok);
        Assert.Equal(0, again.Data!.Removed);
    }

    private static void SetLock(OpsHarness ops, string sid)
    {
        var mine = ops.Hives.For(sid);
        foreach (var c in PolicyGroups.Lock.Changes)
        {
            if (c.Kind == Core.Guard.GuardKind.RegistryDword) mine.WriteDword(c.Location, c.Name, int.Parse(c.Value));
            else mine.WriteString(c.Location, c.Name, c.Value);
        }
    }

    [Fact]
    public async Task Migration_takes_over_the_lock_the_old_tasks_of_the_account_set()
    {
        using var ops = new OpsHarness();
        OldSetup(ops, Callers.UserSid);
        // The old task set the lock: it stays on and becomes the service's before the tasks go (finding B1 of the 0.4.1 acceptance test).
        SetLock(ops, Callers.UserSid);
        var response = await ops.Call<MigrateTasksData>(Callers.User(), Operation.MigrateTasks);
        Assert.True(response.Ok, response.Code);
        Assert.Equal(7, response.Data!.Removed);
        Assert.True(PolicyGroups.Applied(ops.Hives.Loaded[Callers.UserSid], PolicyGroups.Lock));
        Assert.Equal(["lock"], ops.State.Accounts[Callers.UserSid].Groups);
        Assert.True(ops.State.Accounts[Callers.UserSid].LegacyLock);
    }

    [Fact]
    public async Task Migration_is_refused_while_lock_values_nothing_of_the_old_setup_explains_are_present()
    {
        using var ops = new OpsHarness();
        // Only service tasks and no lock files: the lock values were set by someone else.
        ops.Tasks.Tasks.Add($@"{Folder(Callers.UserSid)}\Couch-WSearch");
        Directory.CreateDirectory(Path.Combine(ops.AppDir, "policy", Callers.UserSid));
        SetLock(ops, Callers.UserSid);
        Assert.Equal(ErrorCodes.Failed, (await ops.Call(Callers.User(), Operation.MigrateTasks)).Code);
        Assert.Empty(ops.Tasks.Deleted);
        Assert.True(Directory.Exists(Path.Combine(ops.AppDir, "policy", Callers.UserSid)));
        Assert.False(ops.State.Accounts.TryGetValue(Callers.UserSid, out var account) && account.Groups.Count > 0);
    }

    [Theory]
    [InlineData(@"CouchHud\S-1-5-21-1111111111-2222222222-3333333333-1001\Policy-lock-on", true)]
    [InlineData(@"\CouchHud\S-1-5-21-1111111111-2222222222-3333333333-1001\PC-SysMain", true)]
    [InlineData(@"CouchHud\S-1-5-21-1111111111-2222222222-3333333333-1001\Autologon-off", false)]
    [InlineData(@"CouchHud\S-1-5-21-1111111111-2222222222-3333333333-1002\Policy-lock-on", false)]
    [InlineData(@"CouchHud\Policy-lock-on", false)]
    [InlineData(@"Other\S-1-5-21-1111111111-2222222222-3333333333-1001\Policy-lock-on", false)]
    [InlineData(@"CouchHud\S-1-5-21-1111111111-2222222222-3333333333-1001\sub\Policy-lock-on", false)]
    [InlineData(@"CouchHud\S-1-5-21-1111111111-2222222222-3333333333-1001\Policy-lock-on ", false)]
    [InlineData(@"CouchHud\S-1-5-21-1111111111-2222222222-3333333333-1001\Couch-a b", false)]
    public void Only_the_callers_replaced_tasks_are_migratable(string task, bool expected) =>
        Assert.Equal(expected, OperationsA.IsMigratableTask(task, Callers.UserSid));

    [Fact]
    public async Task ReleaseAllAccounts_is_for_administrators_and_releases_signed_out_accounts_too()
    {
        using var ops = new OpsHarness();
        ops.Scm.Running.Add("WSearch");
        await ops.Call(Callers.User(), Operation.ApplyLockPolicies, new ApplyLockPoliciesArgs(["lock"]));
        await ops.Call(Callers.User(Callers.OtherSid), Operation.ApplyLockPolicies, new ApplyLockPoliciesArgs(["taskmgr"]));
        await ops.Call(Callers.User(), Operation.PauseServices, new ServicesArgs(["WSearch"]));
        // The other account signs out: its hive is only on disk now.
        ops.Hives.OnDisk[Callers.OtherSid] = ops.Hives.Loaded[Callers.OtherSid];
        ops.Hives.Loaded.Remove(Callers.OtherSid);

        Assert.Equal(ErrorCodes.Denied, (await ops.Call(Callers.User(), Operation.ReleaseAllAccounts)).Code);

        var response = await ops.Call<ReleaseAllAccountsData>(Callers.ElevatedAdmin(), Operation.ReleaseAllAccounts);
        Assert.True(response.Ok, response.Code);
        Assert.Equal(new[] { Callers.UserSid, Callers.OtherSid }.Order(), response.Data!.Released.Order());
        Assert.Empty(response.Data.Left);
        Assert.Equal([Callers.OtherSid], ops.Hives.LoadedFromDisk);
        Assert.Empty(PolicyRelease.OursPresent(ops.Hives.Loaded[Callers.UserSid], PolicyGroups.Lock));
        Assert.Empty(PolicyRelease.OursPresent(ops.Hives.OnDisk[Callers.OtherSid], PolicyGroups.TaskManager));
        Assert.Contains("WSearch", ops.Scm.Running);
        Assert.Empty(ops.State.Paused);
    }

    [Fact]
    public async Task ReleaseAllAccounts_names_an_account_whose_hive_cannot_be_opened()
    {
        using var ops = new OpsHarness();
        await ops.Call(Callers.User(Callers.OtherSid), Operation.ApplyLockPolicies, new ApplyLockPoliciesArgs(["taskmgr"]));
        ops.Hives.Loaded.Remove(Callers.OtherSid);
        var response = await ops.Call<ReleaseAllAccountsData>(Callers.ElevatedAdmin(), Operation.ReleaseAllAccounts);
        Assert.Equal([Callers.OtherSid], response.Data!.Left);
        Assert.Equal(["taskmgr"], ops.State.Accounts[Callers.OtherSid].Groups);
    }

    // ---- the state file ----


    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"version":2,"bootTimeMs":0,"accounts":{},"paused":{}}""")]
    [InlineData("""{"version":1,"bootTimeMs":0,"accounts":{},"paused":{},"extra":1}""")]
    [InlineData("""{"version":1,"bootTimeMs":0,"accounts":{"S-1-5-18":{"groups":[],"createdKeys":[]}},"paused":{}}""")]
    [InlineData("""{"version":1,"bootTimeMs":0,"accounts":{"S-1-5-21-1-2-3-1001":{"groups":["all"],"createdKeys":[]}},"paused":{}}""")]
    [InlineData("""{"version":1,"bootTimeMs":0,"accounts":{"S-1-5-21-1-2-3-1001":{"groups":[],"createdKeys":["HKCU\\Software"]}},"paused":{}}""")]
    [InlineData("""{"version":1,"bootTimeMs":0,"accounts":{},"paused":{"WinDefend":{"holders":["S-1-5-21-1-2-3-1001"],"stoppedByService":true}}}""")]
    [InlineData("""{"version":1,"bootTimeMs":0,"accounts":{},"paused":{"Dnscache":{"holders":["S-1-5-21-1-2-3-1001"],"stoppedByService":true}}}""")]
    [InlineData("""{"version":1,"bootTimeMs":0,"accounts":{},"paused":{},"autoLogonOwner":"S-1-5-18"}""")]
    [InlineData("""{"version":1,"bootTimeMs":0,"accounts":{},"paused":{},"firewallPorts":[80]}""")]
    [InlineData("""{"version":1,"bootTimeMs":0,"accounts":{},"paused":{"WSearch":{"holders":["S-1-5-21-1-2-3-1001"],"stoppedByService":true},"wsearch":{"holders":["S-1-5-21-1-2-3-1001"],"stoppedByService":true}}}""")]
    public async Task A_corrupt_state_file_refuses_everything_that_depends_on_it_and_stays_as_it_is(string content)
    {
        using var ops = new OpsHarness(prepareState: folder => File.WriteAllText(Path.Combine(folder, ServiceStateStore.FileName), content));
        ops.Scm.Running.Add("WSearch");
        Assert.False(ops.Store.IsAvailable);
        Assert.False((await ops.Call(Callers.User(), Operation.ApplyLockPolicies, new ApplyLockPoliciesArgs(["lock"]))).Ok);
        Assert.False((await ops.Call(Callers.User(), Operation.PauseServices, new ServicesArgs(["WSearch"]))).Ok);
        Assert.Equal(ErrorCodes.Denied, (await ops.Call(Callers.User(), Operation.ResumeServices, new ServicesArgs(["WSearch"]))).Code);
        Assert.Equal(ErrorCodes.Denied, (await ops.Call(Callers.ElevatedAdmin(), Operation.ReleaseLockPolicies, new ReleaseLockPoliciesArgs(["lock"]))).Code);
        Assert.Equal(ErrorCodes.Denied, (await ops.Call(Callers.ElevatedAdmin(), Operation.AutoLogonOff)).Code);
        Assert.False((await ops.Call(Callers.User(), Operation.MigrateTasks)).Ok);
        Assert.False((await ops.Call(Callers.ElevatedAdmin(), Operation.ReleaseAllAccounts)).Ok);
        Assert.Empty(ops.Hives.For(Callers.UserSid).Data);
        Assert.Empty(ops.Scm.Calls);
        Assert.Equal(content, File.ReadAllText(Path.Combine(ops.StateFolder, ServiceStateStore.FileName)));
        Assert.Contains("not valid", ops.OpsLog.All);
    }

    [Fact]
    public async Task Without_a_trusted_folder_nothing_that_needs_state_runs()
    {
        using var ops = new OpsHarness(stateFolder: false);
        Assert.Equal(ErrorCodes.Unavailable, (await ops.Call(Callers.User(), Operation.ApplyLockPolicies, new ApplyLockPoliciesArgs(["lock"]))).Code);
        Assert.Empty(ops.Hives.For(Callers.UserSid).Data);
    }

    [Fact]
    public async Task The_state_is_written_atomically_and_reads_back()
    {
        using var ops = new OpsHarness();
        await ops.Call(Callers.User(), Operation.ApplyLockPolicies, new ApplyLockPoliciesArgs(["lock"]));
        Assert.Equal([ServiceStateStore.FileName], Directory.GetFiles(ops.StateFolder).Select(Path.GetFileName));
        var reopened = ServiceStateStore.Open(ops.StateFolder, new CapturingSink(), () => ops.BootTime, OpsHarness.OnServiceList);
        Assert.True(reopened.IsAvailable);
        Assert.Equal(["lock"], reopened.Snapshot()!.Accounts[Callers.UserSid].Groups);
    }

    [Fact]
    public void A_change_that_would_make_the_state_invalid_is_not_written()
    {
        using var ops = new OpsHarness();
        var result = ops.Store.Update(s =>
        {
            s.AutoLogonOwner = "S-1-5-18";
            return true;
        });
        Assert.Equal(StateUpdate.WriteFailed, result);
        Assert.Null(ops.State.AutoLogonOwner);
        Assert.False(File.Exists(Path.Combine(ops.StateFolder, ServiceStateStore.FileName)));
    }

    // ---- the data folder chain ({app}\service and {app}\service\data) ----

    private static FolderFacts ProgramFilesLike(params AceFacts[] extra) => new(true, false, ProtectedFolderRules.AdministratorsSid, false,
    [
        new(ProtectedFolderRules.SystemSid, true, 0x1F01FF, false),
        new(ProtectedFolderRules.AdministratorsSid, true, 0x1F01FF, false),
        new(ProtectedFolderRules.TrustedInstallerSid, true, 0x1301BF, false),
        new("S-1-3-0", true, 0x10000000, true),            // CREATOR OWNER, inherit only
        new("S-1-5-32-545", true, 0x1200a9, false),        // Users: read and execute
        new("S-1-15-2-1", true, 0x1200a9, false),          // ALL APPLICATION PACKAGES: read and execute
        .. extra
    ]);

    [Fact]
    public void A_folder_of_the_chain_must_be_owned_by_administrators_and_writable_by_nobody_else()
    {
        Assert.Null(ProtectedFolderRules.Problem(ProgramFilesLike()));
        Assert.Null(ProtectedFolderRules.Problem(ProgramFilesLike() with { OwnerSid = ProtectedFolderRules.SystemSid }));
        Assert.Null(ProtectedFolderRules.Problem(ProgramFilesLike() with { OwnerSid = ProtectedFolderRules.TrustedInstallerSid }));
        Assert.Equal("missing", ProtectedFolderRules.Problem(FolderFacts.Missing));
        Assert.Equal("reparse point", ProtectedFolderRules.Problem(ProgramFilesLike() with { IsReparsePoint = true }));
        Assert.Equal("foreign owner", ProtectedFolderRules.Problem(ProgramFilesLike() with { OwnerSid = Callers.UserSid }));
        Assert.Equal("foreign owner", ProtectedFolderRules.Problem(ProgramFilesLike() with { OwnerSid = null }));
        // Any write right of anyone else: add a file, add a folder, write attributes, delete, delete child, change the list or the owner.
        foreach (var mask in new[] { 0x2, 0x4, 0x10, 0x40, 0x100, 0x10000, 0x40000, 0x80000, 0x40000000, 0x10000000, 0x1301BF })
        {
            Assert.Equal("others may write", ProtectedFolderRules.Problem(ProgramFilesLike(new AceFacts("S-1-5-11", true, mask, false))));
        }
        // A deny entry takes nothing from the administrators.
        Assert.Null(ProtectedFolderRules.Problem(ProgramFilesLike(new AceFacts("S-1-1-0", false, 0x1F01FF, false))));
    }

    [Fact]
    public void The_data_folder_lives_next_to_the_service()
    {
        var serviceDir = OperationsAWiring.ServiceDirectory(@"C:\Program Files\CouchHUD\service\");
        Assert.Equal(@"C:\Program Files\CouchHUD\service", serviceDir);
        Assert.Equal(@"C:\Program Files\CouchHUD\service\data", ServiceInstall.DataFolder(serviceDir));
    }

    [Fact]
    public void A_service_folder_of_the_user_is_refused_with_an_alarm_naming_it()
    {
        using var temp = new TempDir();
        var sink = new CapturingSink();
        Assert.Null(new WindowsProtectedFolder(temp.Path, "data", sink).Ensure());
        Assert.Contains("[alarm]", sink.All);
        Assert.Contains(temp.Path, sink.All);
        // Nothing was created below it.
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "data")));
    }

    [Fact]
    public void A_junction_in_the_chain_is_refused()
    {
        using var temp = new TempDir();
        var target = Path.Combine(temp.Path, "target");
        Directory.CreateDirectory(Path.Combine(target, "data"));
        var link = Path.Combine(temp.Path, "link");
        if (!MakeJunction(link, target)) return;   // no cmd.exe: nothing to check here
        Assert.True(WindowsProtectedFolder.Read(link).IsReparsePoint);
        Assert.Equal("reparse point", ProtectedFolderRules.Problem(WindowsProtectedFolder.Read(link)));
        var sink = new CapturingSink();
        Assert.Null(new WindowsProtectedFolder(link, "data", sink).Ensure());
        Assert.Contains("reparse point", sink.All);
        Assert.Contains(link, sink.All);
        Directory.Delete(link);
    }

    private static bool MakeJunction(string link, string target)
    {
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        if (!File.Exists(cmd)) return false;
        using var process = Process.Start(new ProcessStartInfo(cmd) { ArgumentList = { "/c", "mklink", "/J", link, target }, CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        process.WaitForExit(10_000);
        return process.ExitCode == 0 && Directory.Exists(link);
    }
}
