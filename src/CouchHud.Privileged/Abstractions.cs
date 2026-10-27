using System.ServiceProcess;
using CouchHud.Core.Net;

namespace CouchHud.Privileged;

// The edges of the administrator code to Windows. Everything that touches the machine (LSA, LogonUser, the service control
// manager, Windows Update, the firewall, processes, the Task Scheduler, the event log) sits behind one of these, so the logic in
// between can be driven by fakes and the service can swap an implementation (e.g. the Windows event log for the console).

/// <summary>Where the administrator code reports: the console of the shell's helpers today, the event log and a file in the service.</summary>
public interface IEventSink
{
    /// <summary>What happened (a policy set, a task created).</summary>
    void Info(string message);

    /// <summary>Why something did not happen or went differently.</summary>
    void Error(string message);
}

/// <summary>The LSA secret "DefaultPassword" that Winlogon reads for the automatic sign-in.</summary>
public interface ILsaSecrets
{
    /// <summary>Stores the password; false when Windows refused.</summary>
    bool SetDefaultPassword(char[] password);

    /// <summary>Deletes the secret (a missing one counts as deleted); false when Windows refused.</summary>
    bool DeleteDefaultPassword();
}

/// <summary>The outcome of a password check: 0 and the SID of the logon token, or the Win32 error.</summary>
public sealed record LogonResult(int Error, string? Sid)
{
    public bool Succeeded => Error == 0;
}

/// <summary>Checks a password with LogonUser.</summary>
public interface ILogonVerifier
{
    /// <summary>
    /// An interactive logon for the pair. On success the result carries the SID of the user the token belongs to: a caller (the service)
    /// compares it with the SID it knows the requester has, since "user" may spell some other account (or an alias of one).
    /// </summary>
    LogonResult Verify(string user, string? domain, string password);
}

public static class LogonErrors
{
    public const int LogonFailure = 1326;
    public const int LogonTypeNotGranted = 1385;
}

/// <summary>The service control manager.</summary>
public interface IScm
{
    /// <summary>The start type of the service; <see cref="ServiceStartMode.Manual"/> when it cannot be read (or does not exist).</summary>
    ServiceStartMode GetStartType(string name);

    /// <summary>Whether the service runs (or is starting); false when stopped or stopping, null when unknown (not installed, not readable, not on <c>GamingServices</c>).</summary>
    bool? IsRunning(string name);

    /// <summary>Stops the service and waits for it (true when it is stopped afterwards, also when it was before). False for a service that is not on <c>GamingServices</c>.</summary>
    bool Stop(string name, TimeSpan timeout);

    /// <summary>Starts the service and waits for it (true when it runs afterwards, also when it did before).</summary>
    bool Start(string name, TimeSpan timeout);
}

/// <summary>Windows Update Agent: the installation of everything pending.</summary>
public interface IWindowsUpdate
{
    WindowsUpdateResult InstallAll();
}

/// <summary>The Windows firewall: its rules and netsh with fixed argument lists.</summary>
public interface IFirewall
{
    /// <summary>The rules from the firewall API; null when it is not available.</summary>
    IReadOnlyList<FirewallRuleInfo>? Rules();

    /// <summary>netsh with the arguments (see <see cref="FirewallRule"/> for the lists); its exit code, -1 when it did not run.</summary>
    int Netsh(IReadOnlyList<string> arguments);
}

/// <summary>What a finished process left behind. ExitCode is -1 when it could not be started or did not end in time.</summary>
public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

/// <summary>Starts programs of Windows with fixed arguments (no shell, no window).</summary>
public interface IProcessLauncher
{
    /// <summary>Runs the program and waits up to <paramref name="timeout"/> (output captured).</summary>
    ProcessResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout);

    /// <summary>Starts the program and does not wait; its process id, null when it did not start.</summary>
    int? StartDetached(string fileName, IReadOnlyList<string> arguments);
}

/// <summary>The Task Scheduler, tasks by their full name (<c>CouchHud\&lt;SID&gt;\Name</c>).</summary>
public interface ITaskScheduler
{
    /// <summary>Starts the task; the exit code of schtasks (0: started).</summary>
    int Run(string taskName);

    /// <summary>0 when the task exists.</summary>
    int Query(string taskName);

    int Delete(string taskName);

    /// <summary>Creates (or replaces) a task from an XML file.</summary>
    int Create(string taskName, string xmlFile);

    /// <summary>All tasks in the CouchHud folder and below (every user's), including the legacy ones.</summary>
    IReadOnlyList<string> List();

    /// <summary>Makes sure the task's access list lets <paramref name="sid"/> start it; true when it does afterwards.</summary>
    bool EnsureStartRight(string taskName, string sid);

    /// <summary>
    /// Sets the task's access list to SYSTEM and administrators only (<see cref="CouchHud.Core.Guard.PolicyGroups.AdministratorsOnlyDescriptor"/>),
    /// so no user can start it any more; true when the Task Scheduler shows exactly that afterwards (also when it was so already).
    /// </summary>
    bool RestrictToAdministrators(string taskName);

    /// <summary>
    /// Sets the task's access list to exactly <see cref="CouchHud.Core.Guard.PolicyGroups.TaskSecurityDescriptor"/> of <paramref name="sid"/>:
    /// SYSTEM and administrators fully, the user read and start only, nobody else (the Autologon-off of 0.4.0, security review M-3); true when
    /// the Task Scheduler shows that afterwards (<see cref="CouchHud.Core.Guard.PolicyGroups.OnlyUserStart"/>, also when it was so already).
    /// </summary>
    bool RestrictToUserStart(string taskName, string sid);
}
