using CouchHud.Service.Contracts;

namespace CouchHud.Service.Authorization;

/// <summary>
/// The grants the service hands out after a code entry, a Windows unlock or an administrator's approval through UAC (P9, see
/// <see cref="GrantStore"/>). A grant is bound to the account SID, the logon session (<see cref="CallerInfo.LogonId"/>,
/// TokenStatistics.AuthenticationId) and the session id, never to the process id or the image: PIDs are reused, and every process
/// of the same logon can talk to the pipe anyway. No grant is issued or accepted when <see cref="CallerInfo.LogonId"/> is 0.
/// </summary>
public interface IGrantStore
{
    /// <summary>
    /// True when <paramref name="grant"/> is valid for this caller and operation and its kind is one of <paramref name="accepted"/>; the use
    /// is counted (a parent's or an administrator's grant is used up for that operation). A grant of another kind, or for another operation,
    /// is not valid and is not used up. <paramref name="used"/>: the kind of the grant that counted (None when false).
    /// </summary>
    bool TryConsume(CallerInfo caller, string grant, Operation operation, GrantKinds accepted, out GrantKinds used);
}

/// <summary>How a grant was earned (P9 records it with the grant). Who confirmed decides what it may approve.</summary>
[Flags]
public enum GrantKinds
{
    None = 0,
    /// <summary>The couch code (the person at the couch, often the child).</summary>
    Couch = 1,
    /// <summary>The caller's own Windows unlock (WTS_SESSION_UNLOCK / a sign-in of the caller's own account).</summary>
    WindowsUnlock = 2,
    /// <summary>The parent code.</summary>
    Parent = 4,
    /// <summary>An administrator's approval through UAC (sc.exe control, see AdminApprovals), never the caller's own unlock.</summary>
    WindowsAdmin = 8,

    /// <summary>Confirmed by a parent or an administrator: never by the caller alone.</summary>
    ParentOrAdmin = Parent | WindowsAdmin,
    Any = Couch | WindowsUnlock | Parent | WindowsAdmin
}

/// <summary>No grant is ever valid (fail-closed): for <see cref="Setup.KeepSessionPolicy.Never"/> and tests; the service uses <see cref="GrantStore"/>.</summary>
public sealed class NoGrants : IGrantStore
{
    public static readonly NoGrants Instance = new();

    public bool TryConsume(CallerInfo caller, string grant, Operation operation, GrantKinds accepted, out GrantKinds used)
    {
        used = GrantKinds.None;
        return false;
    }
}

/// <summary>
/// What the service knows of one account's couch lock (P9 review H1): its mode (off, windows, code; an unknown mode counts as "code" when a
/// couch code exists, else as "off") and which codes it holds. Decides which kinds of grant count for the account.
/// </summary>
public sealed record LockView(string Mode, bool HasCouch, bool HasParent)
{
    /// <summary>When nothing can be read: the strictest view (no Windows unlock counts, parent-only where a parents' code would matter).</summary>
    public static readonly LockView Closed = new(LockModes.Code, true, true);

    /// <summary>
    /// Whether the account's couch can be locked: any lock mode but "off", or any code. The code mode without a code is protected too
    /// (fail-closed, review N1): only a parent or an administrator opens it then.
    /// </summary>
    public bool Protected => Mode != LockModes.Off || HasCouch || HasParent || Legacy;

    /// <summary>
    /// A lock of 0.4.0 that the service takes over or took over (<see cref="State.AccountState.LegacyLock"/>, and before that while the
    /// account's old files or tasks wait for the migration) and that no administrator has confirmed yet: whatever code or mode the account
    /// holds may have been planted by its own user, and so may its parents' code (0.4.0 kept it in the config the account writes). The codes,
    /// the mode and the lock's release take an administrator's approval only, no grant of a parents' code counts at all (security review H-1).
    /// </summary>
    public bool Legacy { get; init; }
}

/// <summary>Whether a code (couch or parent) is enrolled for an account. Decides the parity rule of <see cref="Requirement.GrantOrAdminOnceCodeEnrolled"/>.</summary>
public interface ICodeState
{
    bool IsEnrolled(string sid);
}

/// <summary>Assumes a code is enrolled everywhere (fail-closed: releases need a grant or an administrator): for tests; the service uses <see cref="State.CodeStore"/>.</summary>
public sealed class AssumeCodeEnrolled : ICodeState
{
    public static readonly AssumeCodeEnrolled Instance = new();

    public bool IsEnrolled(string sid) => true;
}
