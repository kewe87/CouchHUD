using CouchHud.Service.Contracts;

namespace CouchHud.Service.Authorization;

/// <summary>What a caller needs for an operation (on top of passing <see cref="Authorizer.CheckCaller"/>).</summary>
public enum Requirement
{
    /// <summary>Any interactive caller; the operation touches no account (health and status).</summary>
    Anyone,

    /// <summary>Any interactive caller, for its own account only: the account is the token's SID, never an argument.</summary>
    OwnAccount,

    /// <summary>A valid grant (code entry or Windows unlock, P9) or an elevated administrator.</summary>
    GrantOrAdmin,

    /// <summary>
    /// A grant that a parent or an administrator confirmed (<see cref="GrantKinds.ParentOrAdmin"/>: parent code or an administrator's
    /// approval through UAC, never the caller's own unlock or the couch code), or an elevated administrator.
    /// </summary>
    ParentGrantOrAdmin,

    /// <summary>Like <see cref="GrantOrAdmin"/> once a code is enrolled for the account; before that any caller for its own account (parity with the old "-off" tasks).</summary>
    GrantOrAdminOnceCodeEnrolled,

    /// <summary>An elevated administrator only.</summary>
    Admin
}

/// <summary>
/// A check that depends on state the table cannot see (what the service set, who owns the autologon, whether a code exists). The
/// dispatcher runs it after the table; a check with no implementation registered (<see cref="StateChecks"/>) denies (fail-closed),
/// so an operation whose state arrives with a later package stays closed until that package registers the check.
/// </summary>
public enum StateCheck
{
    None,
    /// <summary>PauseServices: every name on the service's fixed list and not on <see cref="ServicePolicy.Denied"/>.</summary>
    ServiceAllowlist,
    /// <summary>ResumeServices: only services this service paused (and still on the list), StartType Automatic. P5.</summary>
    PausedByService,
    /// <summary>ReleaseLockPolicies: only groups the service itself set for this SID. P5.</summary>
    GroupsSetByService,
    /// <summary>CleanupPolicyKeys: only keys from the service's own state for this SID. P5.</summary>
    KeysOwnedByService,
    /// <summary>AutoLogonOff: the autologon belongs to the caller, or the caller is an elevated administrator. P5.</summary>
    AutoLogonOwner,
    /// <summary>InstallWindowsUpdates: ids only from the service's own last search; without ids (all) no state is needed.</summary>
    UpdateIdsFromSearch,
    /// <summary>InstallCouchHudUpdate: the verifying setup pipeline is in place (signature, hash, newer version). P6.</summary>
    SignedSetupPipeline,
    /// <summary>
    /// EnrollPasscode: only while the SID has no code of that kind (one first enrollment per SID and kind; the shell's migration of its
    /// local hash is the first enrollment of the couch code). A second kind next to an existing code needs a parent's or an
    /// administrator's approval or an elevated administrator; a first parents' code may instead come as the buttons of the couch code,
    /// which the service checks against its couch hash (the parents' code then is the couch code). P9, review M1.
    /// </summary>
    EnrollAllowed,
    /// <summary>SetLockMode: raising the mode is free for the own account; lowering it needs proof (see <see cref="OperationPolicy.AcceptedGrants(ValidatedRequest, LockView)"/>). P9 review H1.</summary>
    LockModeChange,
    /// <summary>ChangePasscode and RemovePasscode: the SID has a code of that kind to change or remove. P9.</summary>
    CodeExists,
    /// <summary>GetJob: the job belongs to the caller's SID, or the caller is an elevated administrator. P6.</summary>
    JobOwner
}

/// <summary>One rule: the requirement from the table, and the state checks the dispatcher runs on top (all must pass).</summary>
public sealed record Rule(Requirement Requirement, params StateCheck[] Checks);

/// <summary>
/// Who may call what (design §3, owner decisions E1 and E10, and the decisions after the security review of P3/P4):
/// <list type="bullet">
/// <item>SetAutoLogon is for the caller's own account with password proof plus the approval of a parent or an administrator:
/// a grant of kind <see cref="GrantKinds.ParentOrAdmin"/> (P9) or an elevated administrator. So a parent can approve a child's own autologon without the token being the parent's.</item>
/// <item>PauseServices stays for any interactive caller, but only for the fixed list, and <see cref="ServicePolicy.Denied"/> wins over any list.</item>
/// <item>RDP sessions count as interactive.</item>
/// </list>
/// The one place for it: every <see cref="Operation"/> has an entry (a test enumerates them), an operation without one is denied.
/// The account an operation acts on is always the token's SID; no argument names an account.
/// </summary>
public static class OperationPolicy
{
    public static readonly IReadOnlyDictionary<Operation, Rule> Table = new Dictionary<Operation, Rule>
    {
        [Operation.Hello] = new(Requirement.Anyone),
        [Operation.Status] = new(Requirement.Anyone),
        [Operation.Ping] = new(Requirement.Anyone),

        // Towards "safer": anyone for their own account.
        [Operation.ApplyLockPolicies] = new(Requirement.OwnAccount),
        [Operation.ReleaseLockPolicies] = new(Requirement.GrantOrAdminOnceCodeEnrolled, StateCheck.GroupsSetByService),
        [Operation.CleanupPolicyKeys] = new(Requirement.GrantOrAdminOnceCodeEnrolled, StateCheck.KeysOwnedByService),

        [Operation.PauseServices] = new(Requirement.OwnAccount, StateCheck.ServiceAllowlist),
        [Operation.ResumeServices] = new(Requirement.OwnAccount, StateCheck.ServiceAllowlist, StateCheck.PausedByService),

        // Machine-wide (Winlogon, LSA secret), allowPasswordSignIn included: a grant a parent or an administrator confirmed (P9), or
        // an elevated administrator; the caller's own unlock or the couch code is not enough. Always for the caller's own account: the
        // handler checks that the LogonUser token SID equals the caller SID. Until P9 the grant store refuses every grant.
        [Operation.SetAutoLogon] = new(Requirement.ParentGrantOrAdmin),
        [Operation.AutoLogonOff] = new(Requirement.OwnAccount, StateCheck.AutoLogonOwner),

        // Machine-wide, but towards "safer" (no reboot is started); one Windows Update job at a time (P6).
        [Operation.SearchWindowsUpdates] = new(Requirement.OwnAccount),
        [Operation.InstallWindowsUpdates] = new(Requirement.OwnAccount, StateCheck.UpdateIdsFromSearch),

        // Rule name and program are fixed in the handler; the ports are checked by the validator.
        [Operation.SetRemoteFirewall] = new(Requirement.GrantOrAdmin),
        [Operation.RemoveRemoteFirewall] = new(Requirement.OwnAccount),
        [Operation.RemoveLegacyFirewallRules] = new(Requirement.OwnAccount),

        [Operation.InstallCouchHudUpdate] = new(Requirement.OwnAccount, StateCheck.SignedSetupPipeline),

        [Operation.EnrollPasscode] = new(Requirement.OwnAccount, StateCheck.EnrollAllowed),
        // The current code (a grant of that code), a parent's or an administrator's approval, or an elevated administrator; the kinds
        // per code are in AcceptedGrants(request).
        [Operation.ChangePasscode] = new(Requirement.GrantOrAdmin, StateCheck.CodeExists),
        // Like a change: the code's own grant, a parent's or an administrator's (the couch code next to a parents' code only the latter
        // two, see CodeHandlers.AcceptedGrants).
        [Operation.RemovePasscode] = new(Requirement.GrantOrAdmin, StateCheck.CodeExists),
        // Raising: anyone for their own account; lowering: the state check asks for the mode's own proof or a parent's or an administrator's.
        [Operation.SetLockMode] = new(Requirement.OwnAccount, StateCheck.LockModeChange),
        // Attempts are limited by the rate limiter's Codes bucket with failure backoff.
        [Operation.VerifyCode] = new(Requirement.OwnAccount),
        [Operation.RequestWindowsGrant] = new(Requirement.OwnAccount),
        // Asking for an administrator's approval and picking it up: for the own account; the approval itself only comes through UAC.
        [Operation.RequestAdminApproval] = new(Requirement.OwnAccount),
        [Operation.CollectAdminApproval] = new(Requirement.OwnAccount),

        [Operation.GetJob] = new(Requirement.OwnAccount, StateCheck.JobOwner),
        // No arguments name an account: the handler reads the caller SID's events / task folder only.
        [Operation.GetRecentEvents] = new(Requirement.OwnAccount),
        [Operation.MigrateTasks] = new(Requirement.OwnAccount),

        [Operation.ReleaseAllAccounts] = new(Requirement.Admin),
        // Sets a damaged state file aside and starts fresh (after releasing what can still be identified, in every account): machine-wide,
        // so an elevated administrator only; no grant of any kind.
        [Operation.ResetServiceState] = new(Requirement.Admin),
    };

    /// <summary>
    /// The kinds of grant the operation accepts when it needs one, without knowing the account's lock (none for an operation without a
    /// rule or without grants). Never the Windows unlock: that counts only for an account in the Windows lock mode
    /// (<see cref="AcceptedGrants(ValidatedRequest, LockView)"/>).
    /// </summary>
    public static GrantKinds AcceptedGrants(Operation operation) => AcceptedGrants(new ValidatedRequest(operation, null, null), LockView.Closed);

    /// <summary>
    /// The kinds of grant one request accepts, listed explicitly by the account's lock (P9 reviews H1, N1, M-a): no "any kind".
    /// <list type="bullet">
    /// <item>The couch's own proof is the mode's: the couch code in the code mode (and while the mode is off or unknown), the caller's own
    /// Windows unlock in the Windows mode. In the Windows mode a couch grant counts for nothing; in the code mode the Windows unlock never.</item>
    /// <item>A parent's or an administrator's approval counts everywhere a grant is asked for.</item>
    /// <item>The codes: the Windows unlock never changes or removes one, and only adds a couch code in the Windows mode (for a later switch to
    /// the code lock). The couch code changes or removes itself only while there is no parents' code and the mode is not Windows; the parents'
    /// code (also both at once), a first code next to another or in a code lock, and the parent-only operations take a parent's or an
    /// administrator's approval.</item>
    /// <item>Lowering the lock mode: the current mode's own proof, or a parent's or an administrator's; with a parents' code only the latter.</item>
    /// </list>
    /// </summary>
    public static GrantKinds AcceptedGrants(ValidatedRequest request, LockView view)
    {
        if (!Table.TryGetValue(request.Operation, out var rule)) return GrantKinds.None;
        var accepted = AcceptedByLock(request, view, rule);
        // A lock of 0.4.0 not confirmed yet: nothing the account's own user may have set counts, the parents' code neither (0.4.0 kept it in
        // the config the account writes): an administrator's approval only (security review H-1, second round).
        if (!view.Legacy) return accepted;
        return LegacyApprovalOnly.Contains(request.Operation) ? GrantKinds.WindowsAdmin : accepted & ~GrantKinds.Parent;
    }

    private static GrantKinds AcceptedByLock(ValidatedRequest request, LockView view, Rule rule)
    {
        var windows = view.Mode == LockModes.Windows;
        var couchProof = (windows ? GrantKinds.WindowsUnlock : GrantKinds.Couch) | GrantKinds.ParentOrAdmin;
        var couchCode = view.HasParent || windows ? GrantKinds.ParentOrAdmin : GrantKinds.Couch | GrantKinds.ParentOrAdmin;
        return request switch
        {
            { Operation: Operation.ChangePasscode or Operation.RemovePasscode, Args: PasscodeHashArgs { Kind: CodeKinds.Couch } or CodeKindArgs { Kind: CodeKinds.Couch } } => couchCode,
            { Operation: Operation.EnrollPasscode, Args: PasscodeHashArgs { Kind: CodeKinds.Couch } } when windows && !view.HasParent => GrantKinds.WindowsUnlock | GrantKinds.ParentOrAdmin,
            { Operation: Operation.ChangePasscode or Operation.RemovePasscode or Operation.EnrollPasscode } => GrantKinds.ParentOrAdmin,
            { Operation: Operation.SetLockMode } => view.HasParent ? GrantKinds.ParentOrAdmin : view.Mode switch
            {
                LockModes.Code => GrantKinds.Couch | GrantKinds.ParentOrAdmin,
                LockModes.Windows => GrantKinds.WindowsUnlock | GrantKinds.ParentOrAdmin,
                _ => GrantKinds.ParentOrAdmin
            },
            _ => rule.Requirement switch
            {
                Requirement.ParentGrantOrAdmin => GrantKinds.ParentOrAdmin,
                Requirement.GrantOrAdmin or Requirement.GrantOrAdminOnceCodeEnrolled => couchProof,
                // A state check may ask for a grant on an operation whose table entry needs none: the couch's proof.
                Requirement.OwnAccount => couchProof,
                _ => GrantKinds.None
            }
        };
    }

    /// <summary>
    /// What takes an administrator's approval only while the account's lock is one of 0.4.0 not confirmed yet (<see cref="LockView.Legacy"/>):
    /// the codes, the lock mode and the lock's release.
    /// </summary>
    public static readonly IReadOnlySet<Operation> LegacyApprovalOnly = new HashSet<Operation>
    {
        Operation.EnrollPasscode, Operation.ChangePasscode, Operation.RemovePasscode, Operation.SetLockMode,
        Operation.ReleaseLockPolicies, Operation.CleanupPolicyKeys
    };

    /// <summary>The rule for a request, null when the operation has none (deny).</summary>
    public static Rule? For(ValidatedRequest request) => Table.TryGetValue(request.Operation, out var rule) ? rule : null;
}
