using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using CouchHud.Core.Guard;
using CouchHud.Core.Platform;
using CouchHud.Core.Startup;
using CouchHud.Core.Updates;
using Microsoft.Win32;

namespace CouchHud.Shell.Services;

/// <summary>
/// Run by the uninstaller (elevated, from the protected install folder) before the files
/// go away: puts back everything CouchHud changed in Windows, so no black screen, no
/// missing Task Manager and no orphaned tasks remain. Never fails the uninstall.
/// </summary>
public static class UninstallCleanup
{
    public const string Switch = "--uninstall-cleanup";

    // Accounts whose lock policies were still in the registry after the direct release: their helper tasks must not be deleted.
    private static readonly HashSet<string> StuckAccounts = new(StringComparer.OrdinalIgnoreCase);

    public static int Run()
    {
        Step("processes", StopOtherInstances);
        Step("shell", () =>
        {
            var registry = new WindowsRegistry();
            var shell = new ShellManager(registry);
            // Only a value that starts CouchHud: a different shell the user chose stays.
            if (ShellRegistration.NamesCouchHud(shell.ShellValue())) shell.UninstallAsShell();
            registry.DeleteValue(@"HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce", "CouchHudRestoreShell");
        });
        // Elevated, possibly as another account than the one that uses CouchHud: HKCU above is not theirs.
        Step("shell of all users", RemoveShellFromLoadedUsers);
        // Only what CouchHud switched on itself; a sign-in set up by hand stays.
        Step("auto sign-in", () => AutoLogon.RemoveIfSetUpByCouchHud(ConfigSaysAutoLogon()));
        Step("task manager", () => TaskManagerPolicy.Apply(false));
        // Policies and shortcuts of the locked couch: the snapshot puts the account's own values back.
        // The state files are the user's, an administrator restores only what ElevatedGuard accepts from them.
        Step("locked couch policies", () =>
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CouchHud");
            if (!SafeFolder(dir, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))) return;
            LockedDesktop.Release(dir, wait: true, elevated: true);
        });
        Step("run entry", LockedDesktop.RemoveReleaseEntry);
        // Steam's own files are not for an administrator process (see ElevatedGuard): CouchHud puts those back as the user, on
        // quit. Here only the entry that would start the files being deleted goes.
        Step("steam guard run entry", SteamGuardService.RemoveRunEntry);
        Step("sign-in lock entry", SignInLock.RemoveEntry);
        Step("services", StartPausedServices);
        // CouchHud.Service: disabled (no SCM restart, no start by a user), stopped, deleted. Its data ({app}\service\data) goes with
        // the setup's files; here only a stale ProgramData\CouchHud\Service of development builds, and only when it is protected.
        // The setup's uninstall removes the service natively if this did not work (CouchHud.iss, usPostUninstall); the event source goes
        // with the setup's registry entries. Unknown service state (access denied): Remove is false and nothing else is touched.
        // First, while the service still runs: it releases every account it holds something for (signed-out accounts' hives included)
        // and starts the services it paused. This process is an elevated administrator, which ReleaseAllAccounts requires.
        Step("service release", ReleaseThroughService);
        Step("service", () =>
        {
            if (ServiceRemoval.ForThisMachine(PrivilegedServices.Processes, PrivilegedServices.Events).Remove())
                ServiceDataCleanup.Delete(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), PrivilegedServices.Events);
        });
        Step("tasks",() => PrivilegedServices.TaskSetup.RemoveAllTasks(StuckAccounts));
        // The task folders that are empty now (\CouchHud\<SID>, \CouchHud): only those, never one with a task left (a stuck account's).
        Step("task folders", () => CouchHud.Privileged.TaskFolderCleanup.Run(PrivilegedServices.Events));
        Step("firewall", Firewall.Remove);
        // CouchHud took the Xbox button from the Game Bar: put back what was there (snapshot), else Windows' default.
        Step("game bar", RestoreGameBar);
        return 0;
    }

    /// <summary>
    /// Runs as whoever started the uninstaller (maybe an administrator account, not the CouchHud user):
    /// only that account's HKCU and data folder are reachable. Do what is possible and say so.
    /// </summary>
    private static void RestoreGameBar()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CouchHud");
        if (SafeFolder(dir, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))) RestoreGameBarOf(dir, null);
    }

    /// <summary>
    /// The data folder is the user's: with a junction or symbolic link in it (or in its place) an administrator process would
    /// read and delete wherever that points. Then the folder is left alone and the uninstall says so.
    /// </summary>
    private static bool SafeFolder(string dir, string? profile)
    {
        if (CouchHud.Core.Guard.ElevatedGuard.IsSafeUserFolder(dir, profile)) return true;
        Console.Error.WriteLine($"[uninstall] {dir} contains a link (junction or symbolic link) and is left alone");
        return false;
    }

    /// <summary>The game bar of one account: <paramref name="hive"/> null is the account of this process, else a name below HKEY_USERS.</summary>
    private static void RestoreGameBarOf(string dir, string? hive)
    {
        var registry = new WindowsRegistry(hive);
        // guard.json is the user's file: an administrator takes only the Game Bar values from it (see ElevatedGuard).
        var snapshot = new CouchHud.Core.Guard.GuardSnapshot(registry, new CouchHud.Core.Guard.FileVdfStore(), Path.Combine(dir, "guard.json"),
            message => Console.Error.WriteLine($"[uninstall] game bar: {message}"), CouchHud.Core.Guard.ElevatedGuard.Accepts);
        var hadSnapshot = snapshot.HasPending;
        var failed = false;
        if (hadSnapshot)
        {
            failed = snapshot.Restore().Count > 0;
            if (failed) Console.Error.WriteLine("[uninstall] game bar: snapshot could not be restored completely, using the Windows default");
        }
        // Older versions switched it off without a backup; a config of this account and a 0 are the sign.
        var legacyZero = File.Exists(Path.Combine(dir, "config.json"))
            && registry.ReadDword(CouchHud.Core.Guard.GameBarGuard.Key, CouchHud.Core.Guard.GameBarGuard.Value) == 0;
        if (!failed && !legacyZero)
        {
            if (!hadSnapshot) Console.WriteLine("[uninstall] game bar: nothing to restore for this account");
            return;
        }
        // Windows' default is "the Xbox button opens the Game Bar": no value.
        registry.DeleteValue(CouchHud.Core.Guard.GameBarGuard.Key, CouchHud.Core.Guard.GameBarGuard.Value);
    }

    private static bool ConfigSaysAutoLogon()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CouchHud");
        if (!SafeFolder(dir, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))) return false;
        return File.Exists(Path.Combine(dir, "config.json")) && new CouchHud.Core.Config.ConfigStore(dir).Current.AutoLogonConfigured;
    }

    /// <summary>
    /// ReleaseAllAccounts through the verified pipe; when the answer does not come in time (the job runs on in the service), Status is
    /// asked until the release says "done" or "failed", at most <see cref="CouchHud.ServiceClient.UninstallRelease.DefaultCap"/>. Without a
    /// running service nothing happens here (the steps around it do what they can directly).
    /// </summary>
    private static void ReleaseThroughService()
    {
        var ops = AdminService.Ops;
        var outcome = Task.Run(() => CouchHud.ServiceClient.UninstallRelease.RunAsync(
            () => ops.ReleaseAllAccountsAsync(), () => ops.ReleaseStatusAsync(), delay => Task.Delay(delay), () => DateTimeOffset.UtcNow,
            CouchHud.ServiceClient.UninstallRelease.DefaultCap)).GetAwaiter().GetResult();
        Console.WriteLine($"[uninstall] release of every account through the service: {outcome}");
    }

    private static void Step(string name, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[uninstall] {name}: {ex.Message}");
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegLoadKey(IntPtr hKey, string subKey, string file);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegUnLoadKey(IntPtr hKey, string subKey);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges
    {
        public int Count;
        public long Luid;
        public int Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state, int length, IntPtr previous, IntPtr returnLength);

    private static readonly IntPtr HkeyUsers = new(unchecked((int)0x80000003));

    private static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle, 0x28, out var token)) return;
        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid)) return;
            var state = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
            AdjustTokenPrivileges(token, false, ref state, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            Native.NativeMethods.CloseHandle(token);
        }
    }

    /// <summary>
    /// Every user's shell value that starts a CouchHud.exe goes: first in the loaded hives
    /// (HKU\SID), then in the hives of users who are not signed in, which are loaded from their
    /// NTUSER.DAT for a moment (backup/restore privilege, elevated).
    /// </summary>
    private static void RemoveShellFromLoadedUsers()
    {
        var loaded = Registry.Users.GetSubKeyNames();
        foreach (var sid in loaded)
        {
            // "_Classes" hives and the default profile have no Winlogon shell of a person.
            if (sid.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase) || sid == ".DEFAULT") continue;
            RemoveShellIn(sid);
            CleanAccount(sid, ProfilePath(sid));
        }

        EnablePrivilege("SeBackupPrivilege");
        EnablePrivilege("SeRestorePrivilege");
        using var profiles = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
        foreach (var sid in profiles?.GetSubKeyNames() ?? [])
        {
            if (loaded.Contains(sid, StringComparer.OrdinalIgnoreCase) || !sid.StartsWith("S-1-5-21-", StringComparison.Ordinal)) continue;
            using var profile = profiles!.OpenSubKey(sid);
            var path = profile?.GetValue("ProfileImagePath") as string;
            if (path is null) continue;
            var dat = Path.Combine(Environment.ExpandEnvironmentVariables(path), "NTUSER.DAT");
            if (!File.Exists(dat)) continue;
            var temp = "CouchHudCleanup_" + sid;
            var result = RegLoadKey(HkeyUsers, temp, dat);
            if (result != 0)
            {
                Console.Error.WriteLine($"[uninstall] could not load the hive of {sid}: {result}");
                continue;
            }
            try
            {
                RemoveShellIn(temp);
                CleanAccount(temp, path);
            }
            finally
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                var unload = RegUnLoadKey(HkeyUsers, temp);
                if (unload != 0) Console.Error.WriteLine($"[uninstall] could not unload the hive of {sid}: {unload}");
            }
        }
    }

    private static string? ProfilePath(string sid)
    {
        using var profile = Registry.LocalMachine.OpenSubKey($@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\{sid}");
        return profile?.GetValue("ProfileImagePath") as string;
    }

    /// <summary>
    /// What the lock and the Game Bar change in an account's registry stays when CouchHud is removed from another account (the
    /// release runs in the account itself). Where the account has CouchHud's data folder, the fixed values go back, the Run entry
    /// of the release goes, and so does the Game Bar change. Only accounts with the data folder are touched.
    /// </summary>
    private static void CleanAccount(string hive, string? profilePath)
    {
        if (profilePath is null) return;
        var profile = Environment.ExpandEnvironmentVariables(profilePath);
        var dir = Path.Combine(profile, "AppData", "Roaming", "CouchHud");
        // Directly, whatever the data folder says: only the fixed values with our value and our programs in the list (the admin side of
        // the rule that a helper task goes only when everything it releases is released).
        Step($"lock policies of {hive}", () =>
        {
            var left = PolicyRelease.RemoveOurs(new WindowsRegistry(hive));
            if (left.Count == 0) return;
            var sid = hive.StartsWith("CouchHudCleanup_", StringComparison.Ordinal) ? hive["CouchHudCleanup_".Length..] : hive;
            StuckAccounts.Add(sid);
            Console.Error.WriteLine($"[uninstall] policies of {hive} stay: {string.Join(", ", left)}");
        });
        // The Run entries are registry only: they go whatever the folder looks like.
        Step($"run entries of {hive}", () => LockedDesktop.RemoveRunEntriesOf(hive));
        if (!Directory.Exists(dir) || !SafeFolder(dir, profile)) return;
        Step($"policies of {hive}", () => LockedDesktop.ReleaseOfOtherAccount(dir, hive));
        Step($"game bar of {hive}", () => RestoreGameBarOf(dir, hive));
    }

    /// <summary>The shell value below HKU\hive, if it starts CouchHud.</summary>
    private static void RemoveShellIn(string hive)
    {
        try
        {
            using var key = Registry.Users.OpenSubKey($@"{hive}\Software\Microsoft\Windows NT\CurrentVersion\Winlogon", writable: true);
            if (key?.GetValue("Shell") is not string shell || !ShellRegistration.NamesCouchHud(shell)) return;
            key.DeleteValue("Shell", throwOnMissingValue: false);
            using var runOnce = Registry.Users.OpenSubKey($@"{hive}\Software\Microsoft\Windows\CurrentVersion\RunOnce", writable: true);
            runOnce?.DeleteValue("CouchHudRestoreShell", throwOnMissingValue: false);
            Console.WriteLine($"[uninstall] removed the shell value of {hive}: {shell}");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Console.Error.WriteLine($"[uninstall] shell of {hive}: {ex.Message}");
        }
    }

    /// <summary>The HUD and its guard.</summary>
    private static void StopOtherInstances()
    {
        // Tells the guard that this is on purpose: no Windows lock, no restart (see Guard.Run).
        UpdateMarker.Write(AppContext.BaseDirectory, Environment.ProcessId, DateTimeOffset.UtcNow);
        for (var round = 0; round < 3; round++)
        {
            var others = Process.GetProcessesByName("CouchHud").Where(p => p.Id != Environment.ProcessId).ToList();
            if (others.Count == 0) return;
            foreach (var p in others)
            {
                using (p)
                {
                    try
                    {
                        p.Kill();
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                    }
                }
            }
            Thread.Sleep(500);
        }
    }

    /// <summary>Services CouchHud may have paused come back if they normally run.</summary>
    private static void StartPausedServices()
    {
        foreach (var name in GamingServices.Suggested.Keys)
        {
            try
            {
                using var service = new ServiceController(name);
                if (service.Status == ServiceControllerStatus.Stopped && service.StartType == ServiceStartMode.Automatic) service.Start();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // Not installed on this PC.
            }
        }
    }
}
