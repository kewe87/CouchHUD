namespace CouchHud.Core.Updates;

/// <summary>
/// How CouchHUD starts itself again ("Restart CouchHUD"): a fixed operation, nothing from the UI. The new process is the same program
/// (the path of the running process) with the same arguments, and a note which process it waits for before it takes the single-instance
/// lock (the old one, which exits right after the new one started). Pure rules, no I/O.
/// </summary>
public sealed record RestartPlan(string FileName, IReadOnlyList<string> Arguments, int WaitForPid)
{
    /// <summary>Environment variable of the new process: the process id of the instance it waits for.</summary>
    public const string WaitVariable = "COUCHHUD_RESTART_AFTER_PID";

    /// <summary>The longest the new process waits for the old one (then it starts like any second start: it wakes the running one and ends).</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The plan, or null when a restart is not possible: no path, a relative path, not an .exe, the file is gone, or the process is a
    /// generic host (dotnet.exe from a development run) that would need other arguments.
    /// </summary>
    public static RestartPlan? Build(string? processPath, IEnumerable<string> arguments, int ownPid, Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(processPath) || !Path.IsPathRooted(processPath)) return null;
        if (!processPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        if (string.Equals(Path.GetFileName(processPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase)) return null;
        if (ownPid <= 0 || !fileExists(processPath)) return null;
        return new RestartPlan(processPath, arguments.ToList(), ownPid);
    }

    /// <summary>The process id the environment asks to wait for: a positive number that is not this process; anything else is no wait.</summary>
    public static int? ParseWait(string? value, int ownPid) =>
        int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid) && pid > 0 && pid != ownPid ? pid : null;
}
