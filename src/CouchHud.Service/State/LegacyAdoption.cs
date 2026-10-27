using CouchHud.Core.Guard;
using CouchHud.Core.Platform;
using CouchHud.Privileged;

namespace CouchHud.Service.State;

/// <summary>
/// What to do with the lock of an account that CouchHUD 0.4.0 set through its SYSTEM tasks (<c>CouchHud\&lt;SID&gt;\Policy-*</c>):
/// <paramref name="Adopt"/>: groups whose values are there and that the old setup of exactly this account can explain, the service takes
/// them over as its own (released later by the normal code or grant path); <paramref name="Foreign"/>: values that nothing of the old setup
/// explains (a Group Policy, an administrator): never taken over, and the old tasks then stay.
/// </summary>
public sealed record AdoptionPlan(IReadOnlyList<string> Adopt, IReadOnlyList<string> Foreign)
{
    public static readonly AdoptionPlan Nothing = new([], []);
}

/// <summary>
/// The pure rules of the migration away from the old SYSTEM tasks (finding B1 of the 0.4.1 acceptance test): which tasks are there per
/// account, which lock groups the service may adopt, which tasks are only restricted. No Windows call here: the inventory (task names, which
/// old .reg files exist) and the registry come in from outside.
/// </summary>
/// <remarks>
/// Evidence that a group was set by the old setup of an account is only what an administrator wrote: the .reg files of that group in the
/// account's folder of the protected program folder (<c>{app}\policy\&lt;SID&gt;\&lt;group&gt;-on|off.reg</c>, which 0.4.0 wrote next to every
/// pair of its tasks). Not the tasks themselves: a standard user may create a task of its own in a folder of the Task Scheduler. Nothing a
/// standard user can write (a task, the shell's config, its mark file) counts: otherwise a child could make the service adopt values an
/// administrator set some other way and then release them.
/// </remarks>
public static class LegacyAdoption
{
    /// <summary>The account folder of a task (<c>CouchHud\&lt;SID&gt;\Name</c>), null for anything else (the machine-wide tasks of earlier versions).</summary>
    public static string? AccountOf(string task)
    {
        var parts = task.Trim('\\').Split('\\');
        return parts.Length == 3 && parts[0].Equals(PolicyGroups.TaskFolder, StringComparison.OrdinalIgnoreCase) && ServiceStateStore.IsUserSid(parts[1]) ? parts[1] : null;
    }

    /// <summary>The accounts with tasks of the old setup, each once, in a fixed order.</summary>
    public static IReadOnlyList<string> Accounts(IEnumerable<string> tasks) =>
        tasks.Select(AccountOf).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Every task in the CouchHud folder (any account's, the machine-wide ones of earlier versions too): no user may start any of them, but
    /// an account's <c>Autologon-off</c> (<see cref="IsAutoLogonOff"/>).
    /// </summary>
    public static IReadOnlyList<string> ToRestrict(IEnumerable<string> tasks) =>
        tasks.Where(t => t.Trim('\\').StartsWith(PolicyGroups.TaskFolder + "\\", StringComparison.OrdinalIgnoreCase) && !IsAutoLogonOff(t))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// An account's <c>CouchHud\&lt;SID&gt;\Autologon-off</c>: its user keeps the start right (security review M-3). It only ever switches the
    /// automatic sign-in off: 0.4.0 created it as SYSTEM with the fixed action <c>reg.exe import "{app}\policy\autologon-off.reg"</c> (a file
    /// of the protected program folder that sets AutoAdminLogon to 0), no argument from anything a user writes, and gave the user read and
    /// start only (GRGX), no right to change it. The recovery of a shell without a service falls back on it.
    /// </summary>
    public static bool IsAutoLogonOff(string task) =>
        AccountOf(task) is { } sid && task.Trim('\\').Equals(PolicyGroups.AutoLogonOffTaskName(sid), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Restricts every task of <see cref="ToRestrict"/> to SYSTEM and administrators; each on its own (one that fails does not stop the
    /// others). The number left open, after a line in the log. Used by the service at start and by the setup (the service program with
    /// <c>--restrict-legacy-tasks</c>) before the new service runs, so not even a squatted pipe leaves an old task startable (review M-1).
    /// </summary>
    public static int RestrictAll(ITaskScheduler scheduler, IEventSink log, IReadOnlyList<string>? tasks = null)
    {
        IReadOnlyList<string> all;
        try
        {
            all = tasks ?? scheduler.List();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.Error($"[legacy] the old tasks could not be listed: {ex.GetType().Name}");
            return -1;
        }
        var open = 0;
        foreach (var task in ToRestrict(all))
        {
            try
            {
                if (!scheduler.RestrictToAdministrators(task)) open++;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                log.Error($"[legacy] task {task}: {ex.GetType().Name}");
                open++;
            }
        }
        // The Autologon-off of an account keeps its user's start right, but not unchecked: exactly SYSTEM and administrators plus read and
        // start for that one user, whatever an administrator may have added since (security review M-3).
        foreach (var task in all.Where(IsAutoLogonOff).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!scheduler.RestrictToUserStart(task, AccountOf(task)!)) open++;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                log.Error($"[legacy] task {task}: {ex.GetType().Name}");
                open++;
            }
        }
        if (open > 0) log.Error($"[legacy] {open} old tasks could not be restricted to administrators");
        return open;
    }

    /// <summary>The old .reg files of a group (<c>&lt;group&gt;-off.reg</c>, <c>&lt;group&gt;-on.reg</c>): either one is the evidence.</summary>
    public static IReadOnlyList<string> EvidenceFiles(PolicyGroup group) => [PolicyGroups.FileName(group, apply: false), PolicyGroups.FileName(group, apply: true)];

    /// <summary>
    /// The plan for one account whose hive is open: a group with some of our values that the service does not hold yet is adopted when the
    /// old setup of this account explains it (<paramref name="fileExists"/>: one of its <see cref="EvidenceFiles"/> exists in the account's
    /// folder of the protected program folder), else it is foreign. A group without our values, or one the service holds already, needs
    /// nothing. Repeating it after an adoption plans nothing (idempotent).
    /// </summary>
    public static AdoptionPlan Plan(IReadOnlyCollection<string> owned, Func<string, bool> fileExists, IRegistry registry)
    {
        var adopt = new List<string>();
        var foreign = new List<string>();
        foreach (var group in PolicyGroups.All)
        {
            if (owned.Contains(group.Id, StringComparer.Ordinal) || PolicyRelease.OursPresent(registry, group).Count == 0) continue;
            (EvidenceFiles(group).Any(fileExists) ? adopt : foreign).Add(group.Id);
        }
        return adopt.Count == 0 && foreign.Count == 0 ? AdoptionPlan.Nothing : new AdoptionPlan(adopt, foreign);
    }
}
