namespace CouchHud.Service.Contracts;

// ---- arguments, one type per operation (the operations without one take none) ---------------------

/// <summary>ApplyLockPolicies: the groups to set, a subset of <see cref="RequestValidator.PolicyGroupIds"/>.</summary>
public sealed record ApplyLockPoliciesArgs(string[] Groups);

/// <summary>ReleaseLockPolicies: the groups to give back.</summary>
public sealed record ReleaseLockPoliciesArgs(string[] Groups);

/// <summary>PauseServices and ResumeServices: service names (the service checks them against its own fixed list again).</summary>
public sealed record ServicesArgs(string[] Names);

/// <summary>
/// SetAutoLogon: the password goes only into this message (never on a command line, never into the log). The account is the
/// caller's; the service takes it from the token, not from the request.
/// </summary>
public sealed record SetAutoLogonArgs(string Password, bool AllowPasswordSignIn = false)
{
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Password = ***, AllowPasswordSignIn = ").Append(AllowPasswordSignIn);
        return true;
    }
}

/// <summary>InstallWindowsUpdates: the update ids from the service's last search, none for all of them.</summary>
public sealed record InstallWindowsUpdatesArgs(string[]? Ids = null);

/// <summary>SetRemoteFirewall: the TCP ports of the remote and the MCP server.</summary>
public sealed record SetRemoteFirewallArgs(int[] Ports);

/// <summary>
/// InstallCouchHudUpdate: the header of a stream. After "streamReady" the client sends the setup bytes as chunks
/// (see <see cref="StreamCodec"/>), exactly <see cref="Size"/> of them. <see cref="ChecksumFile"/> is the checksum file's bytes in
/// base64; <see cref="Signature"/> is the content of the signature file as it is published (one base64 line). The service takes the
/// setup's name and version from the signed checksum file, never from <see cref="FileName"/>: a name that differs is refused.
/// </summary>
public sealed record SetupStreamArgs(string FileName, long Size, string ChecksumFile, string Signature);

/// <summary>
/// EnrollPasscode and ChangePasscode: the PBKDF2 hash as <c>Passcode.Hash</c> makes it, and which code it is (<see cref="CodeKinds"/>:
/// couch, parent, or both at once). The service keeps its own copy per account and checks codes itself (P9).
/// </summary>
public sealed record PasscodeHashArgs(string Hash, string Kind = CodeKinds.Couch)
{
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Hash = ***, Kind = ").Append(Kind);
        return true;
    }
}

/// <summary>
/// SetLockMode: the caller's couch lock mode as the service keeps it (see <see cref="LockModes"/>). Switching the code lock on from no lock
/// at all carries the code in the same step (<see cref="Hash"/>, <see cref="Kind"/> couch or both): codes enrolled while no lock was on are
/// cleared by that switch unless they come along here (review 3, item 2).
/// </summary>
public sealed record LockModeArgs(string Mode, string? Hash = null, string? Kind = null)
{
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Mode = ").Append(Mode).Append(", Hash = ").Append(Hash is null ? "none" : "***").Append(", Kind = ").Append(Kind ?? "none");
        return true;
    }
}

/// <summary>The couch lock modes, weakest first: raising is free, lowering needs the mode's own proof or a parent's or an administrator's.</summary>
public static class LockModes
{
    public const string Off = "off";
    public const string Windows = "windows";
    public const string Code = "code";

    public static int Rank(string? mode) => mode switch { Off => 0, Windows => 1, Code => 2, _ => -1 };
}

/// <summary>RemovePasscode: which of the caller's codes goes (<see cref="CodeKinds.Couch"/> or <see cref="CodeKinds.Parent"/>).</summary>
public sealed record CodeKindArgs(string Kind);

/// <summary>The two codes the service keeps per account.</summary>
public static class CodeKinds
{
    /// <summary>The couch code (opens the couch lock).</summary>
    public const string Couch = "couch";

    /// <summary>The parents' code (parental controls; approves what a parent must approve).</summary>
    public const string Parent = "parent";

    /// <summary>Both at once, to the same hash (the HUD uses one code for both while parental controls are on).</summary>
    public const string Both = "both";
}

/// <summary>
/// VerifyCode: which code ("couch" or "parent") and the buttons that were pressed. <see cref="Ops"/> (parents' code only): the operations
/// the parent's grant is for, each usable once (P9 review M3); without them a right parents' code only answers "matched", no grant.
/// </summary>
public sealed record VerifyCodeArgs(string Kind, string[] Sequence, string[]? Ops = null)
{
    // The buttons are the code itself.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Kind = ").Append(Kind).Append(", Sequence = ***, Ops = ").Append(Ops is null ? "none" : string.Join('/', Ops));
        return true;
    }
}

/// <summary>
/// RequestWindowsGrant: <see cref="Scope"/> "unlock" only: the service saw the caller's own session being unlocked a moment ago (Windows lock
/// mode only). Nothing else comes along; an administrator's approval goes through UAC (<see cref="Operation.RequestAdminApproval"/>).
/// </summary>
public sealed record RequestWindowsGrantArgs(string Scope);

/// <summary>The scopes of <see cref="RequestWindowsGrantArgs"/>.</summary>
public static class WindowsGrantScopes
{
    public const string Unlock = "unlock";
}

/// <summary>
/// RequestAdminApproval: the operations an administrator's approval is asked for (each use once; none: only "an administrator confirmed",
/// no grant). The service answers with a control code; an administrator sends it to the service through UAC (<c>sc.exe control</c>), and
/// CollectAdminApproval then hands this caller the grant.
/// </summary>
public sealed record AdminApprovalArgs(string[] Ops);

/// <summary>The control code (128 to 255) to send with <c>sc.exe control CouchHud &lt;code&gt;</c>, and how long the request waits for it.</summary>
public sealed record AdminApprovalData(int Control, int ExpiresInSeconds);

/// <summary>
/// CollectAdminApproval: <see cref="State"/> "approved" (with <see cref="Grant"/> when operations were asked for), "pending" (not yet) or
/// "none" (nothing asked for, expired, or replaced by a newer request).
/// </summary>
public sealed record AdminApprovalResult(string State, GrantData? Grant = null);

/// <summary>GetJob: the id from a long operation.</summary>
public sealed record GetJobArgs(string JobId);

/// <summary>GetRecentEvents: how many of the caller's own events (newest last).</summary>
public sealed record GetRecentEventsArgs(int Count = 50);

// ---- data of the answers ---------------------------------------------------------------------------

public sealed record HelloData(int Protocol, string ServiceVersion, string[] Capabilities, bool UnsafeInstall, string Migration);

/// <param name="Release">The last ReleaseAllAccounts / ResetServiceState: "none", "running", "done" or "failed". Those may run on after their request timed out; poll this.</param>
/// <param name="CouchCode">Whether the service holds a couch code for the caller (null: it cannot tell).</param>
/// <param name="ParentCode">Whether the service holds a parents' code for the caller (null: it cannot tell).</param>
/// <param name="LockMode">The caller's lock mode as the service keeps it ("off", "code", "windows"; null: not set yet or unknown).</param>
/// <param name="LegacyLock">True while the caller's lock was taken over from 0.4.0 and waits for an administrator's confirmation; else left out.</param>
public sealed record StatusData(string ServiceVersion, bool UnsafeInstall, string Migration, long UptimeSeconds, string? Release = null,
    bool? CouchCode = null, bool? ParentCode = null, string? LockMode = null, bool? LegacyLock = null);

public sealed record PingData(long ServerTimeMs);

/// <summary>The groups an Apply or a Release dealt with.</summary>
public sealed record LockPoliciesData(string[] Groups);

public sealed record ServicesData(string[] Names);

/// <summary>A long operation runs on; ask <see cref="Operation.GetJob"/> for how far it is.</summary>
public sealed record JobStartedData(string JobId);

/// <summary>
/// State is "running", "done" or "failed". <see cref="Message"/> is a short code for the client to translate (e.g. "searching",
/// "installing", "exitCode:5"). <see cref="Updates"/>: what a <see cref="Operation.SearchWindowsUpdates"/> job found.
/// <see cref="BytesDone"/> / <see cref="BytesTotal"/>: what a Windows Update download really loads (the
/// agent's IDownloadProgress), both 0 when unknown or outside the download phase.
/// </summary>
public sealed record JobData(string JobId, string State, int Percent, string? Message, int Installed = 0, bool RebootRequired = false,
    UpdateEntry[]? Updates = null, long BytesDone = 0, long BytesTotal = 0);

/// <summary>
/// One pending Windows update from the service's own search: its UpdateID (a GUID), the title and the largest download (the full package
/// with every variant; not what Windows really loads, never shown as a size).
/// </summary>
public sealed record UpdateEntry(string Id, string Title, long MaxDownloadBytes);

/// <summary>
/// A grant: 32 random bytes in base64, only for the caller, bound to its account, its logon session and its Windows session (never to a
/// process). <see cref="Kind"/> says who confirmed it (see <see cref="GrantKindNames"/>). A couch or Windows-unlock grant can be used again
/// until it expires or the session is locked, disconnected or signed out; a parent's or an administrator's grant only for <see cref="Ops"/>,
/// each once, within a short time.
/// </summary>
public sealed record GrantData(string Grant, string Kind, int ExpiresInSeconds, string[]? Ops = null)
{
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Grant = ***, Kind = ").Append(Kind).Append(", ExpiresInSeconds = ").Append(ExpiresInSeconds).Append(", Ops = ").Append(Ops is null ? "none" : string.Join('/', Ops));
        return true;
    }
}

/// <summary>The kinds of grant on the wire.</summary>
public static class GrantKindNames
{
    public const string Couch = "couch";
    public const string WindowsUnlock = "windowsUnlock";
    public const string Parent = "parent";
    public const string WindowsAdmin = "windowsAdmin";
}

/// <summary>
/// The answer of VerifyCode: <see cref="Result"/> is "granted" (with <see cref="Grant"/>), "wrong", "lockedOut" or "noCode" (the
/// service keeps no code of that kind for the account). <see cref="WaitSeconds"/>: how long the next try has to wait (0: at once).
/// </summary>
public sealed record CodeCheckData(string Result, int WaitSeconds = 0, GrantData? Grant = null);

/// <summary>The results of <see cref="CodeCheckData"/>.</summary>
public static class CodeCheckResults
{
    public const string Granted = "granted";
    public const string Wrong = "wrong";
    public const string LockedOut = "lockedOut";
    public const string NoCode = "noCode";
    /// <summary>A right parents' code asked without operations: checked, no grant.</summary>
    public const string Matched = "matched";
}

public sealed record EventEntry(long TimeMs, string Kind, string Message);

public sealed record EventsData(EventEntry[] Events);

public sealed record MigrateTasksData(int Removed);

public sealed record ReleaseAllAccountsData(string[] Released, string[] Left);

/// <summary>ResetServiceState: whether a damaged state file was set aside (kept for diagnosis), and the accounts released before the fresh start.</summary>
public sealed record ResetServiceStateData(bool CorruptFileKept, string[] Released, string[] Left);
