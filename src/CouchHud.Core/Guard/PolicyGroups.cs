using System.Globalization;
using System.Security;
using System.Text;
using CouchHud.Core.Platform;

namespace CouchHud.Core.Guard;

/// <summary>
/// A fixed set of protected policy values that a scheduled task writes for the user (see <see cref="PolicyGroups"/>).
/// </summary>
/// <param name="Id">Short name used in task and file names.</param>
/// <param name="ReleasedWithUnlock">
/// The locked desktop sets the group when the couch locks and releases it when the lock opens. False: another owner looks after
/// it (Task Manager follows the couch security, which stays on while the couch is merely unlocked) and an unlock leaves it alone.
/// </param>
public sealed record PolicyGroup(string Id, IReadOnlyList<GuardChange> Changes, bool ReleasedWithUnlock = true)
{
    /// <summary>The DWORD values: what "applied" and "present" are judged by. The DisallowRun list depends on numbering.</summary>
    public IEnumerable<GuardChange> Dwords => Changes.Where(c => c.Kind == GuardKind.RegistryDword);

    /// <summary>Keys that hold a list of our entries and are removed as a whole when released.</summary>
    public IEnumerable<string> ListKeys => Changes.Where(c => c.Kind == GuardKind.RegistryString).Select(c => c.Location).Distinct(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The Policies keys under HKCU only let administrators write (the user's own processes get read access only), so the
/// locked couch cannot set its policies as a normal user. The one-time administrator confirmation of the setup creates
/// scheduled tasks that run as SYSTEM, one set per user (folder <c>CouchHud\&lt;SID&gt;</c>); each task runs nothing but
/// <c>reg.exe import &lt;fixed file in the protected program folder&gt;</c>, and the file writes into <c>HKEY_USERS\&lt;SID&gt;</c>.
/// The task's security descriptor lets exactly that user (and administrators/SYSTEM) start it, nobody else. SYSTEM instead of the
/// user with "highest privileges": a standard user has no administrator token to elevate to, so such a task would silently do
/// nothing there; and the tasks of several accounts no longer replace each other.
/// The files hold exactly the values below, so what a task can do is fixed: set or remove these few policy values. No parameter
/// comes from outside, CouchHud.exe is not run elevated (a .NET program picks up the user's environment, e.g. startup hooks,
/// which would be an elevation hole), and the files are only as writable as the program folder (which setup checks to be
/// administrator-only).
/// Only values that do not exist yet are taken over (<see cref="Pristine"/>); a value the user or a Group Policy set is
/// never touched, so releasing a group cannot remove anything that was not ours.
/// </summary>
public static class PolicyGroups
{
    public const string TaskFolder = "CouchHud";

    /// <summary>The lock's policies without Task Manager.</summary>
    public static readonly PolicyGroup Lock = new("lock", LockHardening.ProtectedChanges(NullRegistry.Instance));

    /// <summary>
    /// Task Manager off, separate because it follows its own option and its own owner: the couch security sets it for as long as the
    /// couch is locked-and-in-front (docs/GUIDE.md: "im Couch-Modus gesperrt"), also while the 2-minute unlock is open, and releases it in
    /// PC mode, on quit and by the recovery. The unlock of the locked desktop must not take it away.
    /// </summary>
    public static readonly PolicyGroup TaskManager = new("taskmgr", [LockHardening.TaskManagerChange], ReleasedWithUnlock: false);

    public static readonly IReadOnlyList<PolicyGroup> All = [Lock, TaskManager];

    public static PolicyGroup? ById(string id) => All.FirstOrDefault(g => g.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The groups of <paramref name="marked"/> (ids from the mark file) that an unlock gives back.</summary>
    public static IEnumerable<PolicyGroup> ReleasedWithUnlock(IEnumerable<string> marked) =>
        marked.Select(ById).OfType<PolicyGroup>().Where(g => g.ReleasedWithUnlock);

    /// <summary>The name of the group's task in the folder of the user (see <see cref="UserFolder"/>).</summary>
    public static string TaskName(PolicyGroup group, bool apply, string sid) => $@"{UserFolder(sid)}\Policy-{group.Id}-{(apply ? "on" : "off")}";

    /// <summary>Earlier versions: one machine-wide set of tasks directly in the CouchHud folder (the last user's setup replaced the others').</summary>
    public static string LegacyTaskName(PolicyGroup group, bool apply) => $@"{TaskFolder}\Policy-{group.Id}-{(apply ? "on" : "off")}";

    // ---- tasks per user ---------------------------------------------------------------------

    /// <summary>The account SIDs that get tasks: local and domain accounts (S-1-5-21-…) and Entra ID accounts (S-1-12-1-…).</summary>
    public static bool IsUserSid(string? sid) =>
        sid is { Length: > 0 and <= 80 } && System.Text.RegularExpressions.Regex.IsMatch(sid, @"^S-1-(5-21|12-1)(-[0-9]{1,10}){3,4}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>The task folder of one user: <c>CouchHud\&lt;SID&gt;</c>. A setup only touches its own.</summary>
    public static string UserFolder(string sid) => IsUserSid(sid) ? $@"{TaskFolder}\{sid}" : throw new ArgumentException($"not a user SID: {sid}", nameof(sid));

    /// <summary>A task from the earlier, machine-wide naming (<c>CouchHud\Name</c> without a SID folder).</summary>
    public static bool IsLegacyTask(string task)
    {
        var parts = task.Trim('\\').Split('\\');
        return parts.Length == 2 && parts[0].Equals(TaskFolder, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A task in the folder of exactly this user.</summary>
    public static bool IsTaskOf(string task, string sid) =>
        task.Trim('\\').StartsWith(UserFolder(sid) + "\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>A service task: <c>CouchHud\&lt;SID&gt;\Couch-&lt;service&gt;</c> (pause) or <c>PC-…</c> (start again).</summary>
    public static string ServiceTaskName(string sid, bool couch, string service) => $@"{UserFolder(sid)}\{(couch ? "Couch" : "PC")}-{service}";

    public static string FileName(PolicyGroup group, bool apply) => $"{group.Id}-{(apply ? "on" : "off")}.reg";

    // ---- state -------------------------------------------------------------------------------

    /// <summary>None of the group's values exists yet (the list key holds no values either).</summary>
    public static bool Pristine(IRegistry registry, PolicyGroup group) =>
        group.Dwords.All(c => registry.GetValueType(c.Location, c.Name) == RegistryValueType.Missing)
        && group.ListKeys.All(k => registry.ReadValues(k).Count == 0);

    /// <summary>All DWORD values are set as the group wants them.</summary>
    public static bool Applied(IRegistry registry, PolicyGroup group) =>
        group.Dwords.All(c => registry.ReadDword(c.Location, c.Name) is { } v && v.ToString(CultureInfo.InvariantCulture) == c.Value);

    /// <summary>Some value of the group is still there (released = false).</summary>
    public static bool AnyPresent(IRegistry registry, PolicyGroup group) =>
        group.Dwords.Any(c => registry.GetValueType(c.Location, c.Name) != RegistryValueType.Missing)
        || group.ListKeys.Any(k => registry.ReadValues(k).Count > 0);

    // ---- the fixed files and tasks -------------------------------------------------------------

    private const string CurrentUserHive = "HKEY_CURRENT_USER";

    /// <summary>The root of a user's hive as SYSTEM sees it: <c>HKEY_USERS\&lt;SID&gt;</c> (SYSTEM has no HKCU worth writing).</summary>
    public static string UserHive(string sid) => IsUserSid(sid) ? @"HKEY_USERS\" + sid : throw new ArgumentException($"not a user SID: {sid}", nameof(sid));

    private static string Hive(string key, string root) => key.StartsWith(@"HKCU\", StringComparison.OrdinalIgnoreCase) ? root + @"\" + key[5..] : throw new ArgumentException($"only HKCU: {key}");

    private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>
    /// The .reg text (version 5, write it as UTF-16 with BOM) that sets the group's values, below <paramref name="root"/>
    /// (<see cref="UserHive"/> for a task of SYSTEM).
    /// </summary>
    public static string ApplyFile(PolicyGroup group, string root = CurrentUserHive)
    {
        var text = new StringBuilder("Windows Registry Editor Version 5.00\r\n");
        foreach (var key in group.Changes.Select(c => c.Location).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            text.Append("\r\n[").Append(Hive(key, root)).Append("]\r\n");
            foreach (var c in group.Changes.Where(c => c.Location.Equals(key, StringComparison.OrdinalIgnoreCase)))
            {
                text.Append(Quote(c.Name)).Append('=');
                if (c.Kind == GuardKind.RegistryDword) text.Append("dword:").Append(uint.Parse(c.Value, CultureInfo.InvariantCulture).ToString("x8", CultureInfo.InvariantCulture));
                else text.Append(Quote(c.Value));
                text.Append("\r\n");
            }
        }
        return text.ToString();
    }

    /// <summary>The .reg text that removes them again: the values, and the whole list key of the string entries.</summary>
    public static string ReleaseFile(PolicyGroup group, string root = CurrentUserHive)
    {
        var text = new StringBuilder("Windows Registry Editor Version 5.00\r\n");
        var lists = group.ListKeys.ToList();
        foreach (var key in lists) text.Append("\r\n[-").Append(Hive(key, root)).Append("]\r\n");
        foreach (var key in group.Dwords.Select(c => c.Location).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            text.Append("\r\n[").Append(Hive(key, root)).Append("]\r\n");
            foreach (var c in group.Dwords.Where(c => c.Location.Equals(key, StringComparison.OrdinalIgnoreCase))) text.Append(Quote(c.Name)).Append("=-\r\n");
        }
        return text.ToString();
    }

    /// <summary>The account the helper tasks run as: SYSTEM.</summary>
    public const string SystemSid = "S-1-5-18";

    /// <summary>The local Administrators group.</summary>
    public const string AdministratorsSid = "S-1-5-32-544";

    /// <summary>
    /// Who may see and start a helper task: SYSTEM and administrators fully, the one user may read and execute. Without the last
    /// entry only administrators could start a task of SYSTEM, which a standard user is not.
    /// </summary>
    public static string TaskSecurityDescriptor(string sid) =>
        IsUserSid(sid) ? $"D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;{sid})" : throw new ArgumentException($"not a user SID: {sid}", nameof(sid));

    /// <summary>
    /// Does this security descriptor (SDDL) let <paramref name="sid"/> read and execute, i.e. start, the task, and does it deny
    /// nobody that? Read from the Task Scheduler after the setup to see what the task really got.
    /// </summary>
#pragma warning disable CA1416 // Security descriptors are Windows-only; so is everything that asks for one.
    public static bool GrantsStart(string? sddl, string sid)
    {
        if (string.IsNullOrWhiteSpace(sddl)) return false;
        try
        {
            var acl = new System.Security.AccessControl.RawSecurityDescriptor(sddl).DiscretionaryAcl;
            if (acl is null) return false;
            // Starting a task is the execute right: generic (GX) as written in the descriptor, or the file right (0x20) it may be stored as.
            const uint execute = 0x20000000 | 0x20;
            var granted = false;
            foreach (var entry in acl)
            {
                if (entry is not System.Security.AccessControl.CommonAce ace || !string.Equals(ace.SecurityIdentifier.Value, sid, StringComparison.OrdinalIgnoreCase)) continue;
                if (ace.AceType == System.Security.AccessControl.AceType.AccessDenied) return false;
                if (ace.AceType == System.Security.AccessControl.AceType.AccessAllowed && (unchecked((uint)ace.AccessMask) & execute) != 0) granted = true;
            }
            return granted;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// The access list every task of CouchHUD that is left gets (finding B1 of the 0.4.1 acceptance test): SYSTEM and administrators only,
    /// no user may start a task that runs as SYSTEM any more. Whether a descriptor is that already: <see cref="OnlyAdministrators"/>.
    /// </summary>
    public const string AdministratorsOnlyDescriptor = "D:P(A;;FA;;;SY)(A;;FA;;;BA)";

    /// <summary>
    /// True when the descriptor (SDDL) grants nothing to anybody but SYSTEM and the administrators (deny entries take nothing away from
    /// that). Unreadable, empty or without a DACL counts as false (fail-closed: the caller sets it again).
    /// </summary>
    public static bool OnlyAdministrators(string? sddl)
    {
        if (string.IsNullOrWhiteSpace(sddl)) return false;
        try
        {
            var acl = new System.Security.AccessControl.RawSecurityDescriptor(sddl).DiscretionaryAcl;
            if (acl is null) return false;
            foreach (var entry in acl)
            {
                if (entry is not System.Security.AccessControl.KnownAce ace) return false;
                if (ace.AceType is System.Security.AccessControl.AceType.AccessDenied or System.Security.AccessControl.AceType.AccessDeniedObject) continue;
                if (ace.SecurityIdentifier.Value is not (SystemSid or AdministratorsSid)) return false;
            }
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when the descriptor (SDDL) grants SYSTEM and the administrators whatever, <paramref name="sid"/> at most read and start (generic
    /// or as the file rights they are stored as) and nobody anything else (security review M-3). Unreadable or without a DACL: false.
    /// </summary>
    public static bool OnlyUserStart(string? sddl, string sid)
    {
        if (string.IsNullOrWhiteSpace(sddl) || !IsUserSid(sid)) return false;
        // GENERIC_READ | GENERIC_EXECUTE, and FILE_GENERIC_READ | FILE_GENERIC_EXECUTE as the Task Scheduler may store them.
        const uint readStart = 0x80000000 | 0x20000000 | 0x001200A9;
        try
        {
            var acl = new System.Security.AccessControl.RawSecurityDescriptor(sddl).DiscretionaryAcl;
            if (acl is null) return false;
            foreach (var entry in acl)
            {
                if (entry is not System.Security.AccessControl.KnownAce ace) return false;
                if (ace.AceType is System.Security.AccessControl.AceType.AccessDenied or System.Security.AccessControl.AceType.AccessDeniedObject) continue;
                if (ace.SecurityIdentifier.Value is SystemSid or AdministratorsSid) continue;
                if (!string.Equals(ace.SecurityIdentifier.Value, sid, StringComparison.OrdinalIgnoreCase) || (unchecked((uint)ace.AccessMask) & ~readStart) != 0) return false;
            }
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
#pragma warning restore CA1416

    /// <summary>
    /// The task definition: no trigger (started on demand), run as SYSTEM, nothing but the one command, startable by the given
    /// user (<see cref="TaskSecurityDescriptor"/>). The limits make sure it also runs on a laptop on battery.
    /// </summary>
    public static string SystemTaskXml(string sid, string command, string arguments) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>CouchHUD: fixed helper of the locked couch. Started by CouchHUD, runs nothing else.</Description>
            <SecurityDescriptor>{SecurityElement.Escape(TaskSecurityDescriptor(sid))}</SecurityDescriptor>
          </RegistrationInfo>
          <Triggers />
          <Principals>
            <Principal id="Author"><UserId>{SystemSid}</UserId><RunLevel>HighestAvailable</RunLevel></Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>false</StartWhenAvailable>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>true</Hidden>
            <ExecutionTimeLimit>PT1M</ExecutionTimeLimit>
            <Priority>5</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec><Command>{SecurityElement.Escape(command)}</Command><Arguments>{SecurityElement.Escape(arguments)}</Arguments></Exec>
          </Actions>
        </Task>
        """;

    /// <summary>The argument line of a task: import this file.</summary>
    public static string ImportArguments(string regFile) => $"import \"{regFile}\"";

    // ---- automatic sign-in off (recovery) ----------------------------------------------------

    /// <summary>
    /// The recovery cannot write the machine-wide Winlogon key as a normal program, and it must not start an elevated CouchHud.exe
    /// (a .NET program takes startup hooks from the user's environment). Like the policies it asks a fixed task that only imports
    /// <see cref="AutoLogonOffFile"/>: AutoAdminLogon = 0. The stored account and secret stay (Windows ignores them while the switch is
    /// off); the console setup's "off" removes those later and puts back the names Winlogon had.
    /// </summary>
    public static string AutoLogonOffTaskName(string sid) => UserFolder(sid) + @"\Autologon-off";

    public const string AutoLogonOffFileName = "autologon-off.reg";

    public const string WinlogonHive = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";

    public static string AutoLogonOffFile() => "Windows Registry Editor Version 5.00\r\n\r\n[" + WinlogonHive + "]\r\n\"AutoAdminLogon\"=\"0\"\r\n";

    // ---- empty keys the helper created -----------------------------------------------------

    public static string CleanupTaskName(string sid) => UserFolder(sid) + @"\Policy-cleanup";

    public const string CleanupFileName = "cleanup.reg";

    /// <summary>The keys the .reg file of the cleanup task deletes, one per line (read again before the task is started).</summary>
    public const string CleanupKeysFileName = "cleanup.keys";

    private const string PoliciesWindowsKey = @"HKCU\Software\Policies\Microsoft\Windows";

    /// <summary>The keys an import of the helper can create that only administrators can delete again.</summary>
    public static IReadOnlyList<string> CleanupCandidates { get; } =
        LockHardening.TrackedKeys.Where(k => LockHardening.IsProtectedKey(k) || k.Equals(PoliciesWindowsKey, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>The initial state the setup remembers: which of the candidates do not exist yet.</summary>
    public static IReadOnlyList<string> MissingCandidates(IRegistry registry) => CleanupCandidates.Where(k => !registry.KeyExists(k)).ToList();

    /// <summary>Only candidates count; whatever else arrives (the setup gets the list as an argument) is dropped.</summary>
    public static IReadOnlyList<string> ValidCleanupKeys(IEnumerable<string> keys) =>
        CleanupCandidates.Where(c => keys.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// The keys the direct administrator path may remove when empty: the noted ones (the lock created them), and when the note is
    /// missing or has none of the candidates, only the keys of the paths the lock itself writes to (Explorer, System, the Windows
    /// System policy key and the DisallowRun list). Removal is always "only when empty" (<see cref="LockHardening.RemoveEmpty"/>),
    /// so a key with content never goes, and the Windows parent key stays.
    /// </summary>
    public static IReadOnlyList<string> KeysToClean(IEnumerable<string> noted)
    {
        var valid = ValidCleanupKeys(noted);
        return valid.Count > 0 ? valid : [.. LockHardening.ProtectedRoots, LockHardening.DisallowRunKey];
    }

    /// <summary>
    /// The list a setup run leaves behind: what it sees missing now plus what an earlier run already noted. It never shrinks by
    /// itself: a key that exists now may exist because the lock created it, and forgetting it would leave it behind for ever.
    /// (Whether a listed key is really ours is decided when the cleanup runs, see <see cref="CleanupAllowed"/>.)
    /// </summary>
    public static IReadOnlyList<string> MergeCleanupKeys(IEnumerable<string> missingNow, IEnumerable<string> alreadyNoted) =>
        ValidCleanupKeys(missingNow.Concat(alreadyNoted));

    /// <summary>
    /// The noted list covers every candidate that does not exist now. False means the setup has to run once more to note them
    /// (an earlier run saw them present, or noted nothing), whatever else of the setup is in place.
    /// </summary>
    public static bool CleanupNoteCurrent(IRegistry registry, IEnumerable<string> noted)
    {
        var have = noted.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return MissingCandidates(registry).All(have.Contains);
    }

    /// <summary>The .reg text that deletes the listed keys (with whatever is below them: see <see cref="CleanupAllowed"/>).</summary>
    public static string CleanupFile(IEnumerable<string> keys, string root = CurrentUserHive)
    {
        var text = new StringBuilder("Windows Registry Editor Version 5.00\r\n");
        foreach (var key in keys) text.Append("\r\n[-").Append(Hive(key, root)).Append("]\r\n");
        return text.ToString();
    }

    /// <summary>
    /// Whether the cleanup task may run: every listed key was created by us (it did not exist before the first apply:
    /// <paramref name="createdByUs"/>) and is empty now, apart from listed keys below it. A key with a value that is not ours
    /// (or a missing note) stops the whole cleanup, because a deleted key cannot be given back.
    /// </summary>
    public static bool CleanupAllowed(IRegistry registry, IReadOnlyCollection<string> listed, IEnumerable<string> createdByUs)
    {
        if (listed.Count == 0) return false;
        var created = createdByUs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var set = listed.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!set.All(created.Contains)) return false;
        bool Empty(string key) =>
            !registry.KeyExists(key)
            || (registry.ReadValues(key).Count == 0 && registry.SubKeys(key).All(sub => set.Contains(key + "\\" + sub) && Empty(key + "\\" + sub)));
        return set.All(Empty);
    }
}
