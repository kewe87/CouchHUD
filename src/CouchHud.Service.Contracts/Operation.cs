namespace CouchHud.Service.Contracts;

/// <summary>
/// Every operation the service knows. Fixed list: the service never runs a command or a path the caller names, it only
/// does these things for the caller's own account. On the wire the enum member's name is used.
/// </summary>
public enum Operation
{
    Hello,
    Status,
    Ping,
    ApplyLockPolicies,
    ReleaseLockPolicies,
    CleanupPolicyKeys,
    PauseServices,
    ResumeServices,
    SetAutoLogon,
    AutoLogonOff,
    InstallWindowsUpdates,
    SetRemoteFirewall,
    RemoveRemoteFirewall,
    RemoveLegacyFirewallRules,
    InstallCouchHudUpdate,
    EnrollPasscode,
    ChangePasscode,
    VerifyCode,
    RequestWindowsGrant,
    GetJob,
    GetRecentEvents,
    MigrateTasks,
    ReleaseAllAccounts,
    /// <summary>Searches Windows Update as a job; its result is the list the ids of <see cref="InstallWindowsUpdates"/> must come from.</summary>
    SearchWindowsUpdates,
    ResetServiceState,
    /// <summary>Removes one of the caller's codes (couch lock or parental controls switched off); needs that code's grant, a parent's or an administrator's.</summary>
    RemovePasscode,
    /// <summary>The caller's couch lock mode (off, windows, code) as the service keeps it; lowering needs proof.</summary>
    SetLockMode,
    /// <summary>Registers a pending administrator approval (the operations it is for) and answers with the control code for <c>sc.exe control</c> (UAC).</summary>
    RequestAdminApproval,
    /// <summary>Picks up the grant of an approval an administrator gave through UAC (the caller's own pending request only).</summary>
    CollectAdminApproval,
    /// <summary>
    /// Sets a damaged code file aside (<c>codes.json.broken-&lt;time&gt;</c>) and starts with no codes: only while the code store is unavailable
    /// because its file is not valid, only with an administrator's approval through UAC or as an elevated administrator.
    /// </summary>
    ResetCodeStore
}
