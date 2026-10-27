using CouchHud.Privileged;
using CouchHud.Service.Authorization;
using CouchHud.Service.Contracts;
using CouchHud.Service.Logging;
using CouchHud.Service.State;

namespace CouchHud.Service.Dispatch;

/// <summary>What the code and grant operations of package P9 work with.</summary>
/// <param name="IsBlankPassword">Whether an account has no password (its unlock proves nothing, review H1); see <see cref="BlankPasswordCheck"/>.</param>
/// <param name="LegacyLock">
/// Whether the account's lock was taken over from 0.4.0 and not confirmed yet (<see cref="LockView.Legacy"/>); null: never (tests). A failure
/// counts as yes (fail-closed).
/// </param>
/// <param name="EndLegacyLock">Ends that once an administrator confirmed the account's code or mode; true when the lock is no lock of 0.4.0 any more.</param>
public sealed record CodeParts(CodeStore Codes, GrantStore Grants, SessionEvents Sessions, AdminApprovals Approvals, Func<string, bool> IsBlankPassword,
    IEventSink Log, RecentEvents? Events, Func<string, bool>? LegacyLock = null, Func<string, bool>? EndLegacyLock = null);

/// <summary>
/// Package P9 ("Grants mit Code"): EnrollPasscode, ChangePasscode, RemovePasscode, SetLockMode, VerifyCode, RequestWindowsGrant ("unlock"),
/// RequestAdminApproval and CollectAdminApproval, and the state checks they own (<see cref="StateCheck.EnrollAllowed"/>,
/// <see cref="StateCheck.CodeExists"/>, <see cref="StateCheck.LockModeChange"/>). Which kinds of grant count is decided by
/// <see cref="OperationPolicy.AcceptedGrants(ValidatedRequest, LockView)"/> from the account's lock (<see cref="CodeStore.View"/>). The account
/// is always the token's SID; no grant, code or hash ever reaches a log line, and no administrator credential ever reaches the service at all
/// (approvals go through UAC, see <see cref="AdminApprovals"/>).
/// </summary>
public sealed class CodeHandlers(CodeParts parts)
{
    public static CodeHandlers Register(Dictionary<Operation, Handler> handlers, StateChecks checks, CodeParts parts)
    {
        var codes = new CodeHandlers(parts);
        handlers[Operation.EnrollPasscode] = (context, _) => ValueTask.FromResult(codes.Store(context, enroll: true));
        handlers[Operation.ChangePasscode] = (context, _) => ValueTask.FromResult(codes.Store(context, enroll: false));
        handlers[Operation.RemovePasscode] = (context, _) => ValueTask.FromResult(codes.Remove(context));
        handlers[Operation.SetLockMode] = (context, _) => ValueTask.FromResult(codes.SetMode(context));
        handlers[Operation.VerifyCode] = (context, _) => ValueTask.FromResult(codes.Verify(context));
        handlers[Operation.RequestWindowsGrant] = (context, _) => ValueTask.FromResult(codes.UnlockGrant(context.Caller));
        handlers[Operation.RequestAdminApproval] = (context, _) => ValueTask.FromResult(codes.RequestApproval(context));
        handlers[Operation.CollectAdminApproval] = (context, _) => ValueTask.FromResult(codes.CollectApproval(context));
        checks.Register(StateCheck.EnrollAllowed, new StateCheckFunc(codes.EnrollAllowed));
        checks.Register(StateCheck.CodeExists, new StateCheckFunc(codes.CodeExists));
        checks.Register(StateCheck.LockModeChange, new StateCheckFunc(codes.LockModeChange));
        return codes;
    }

    /// <summary>The account's lock, for the dispatcher and the update's session (which grants count), with a lock taken over from 0.4.0.</summary>
    public LockView View(string sid) => parts.Codes.View(sid) with { Legacy = IsLegacy(sid) };

    private bool IsLegacy(string sid)
    {
        try
        {
            return parts.LegacyLock?.Invoke(sid) ?? false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return true;
        }
    }

    /// <summary>
    /// An administrator confirmed the account's code or mode (an administrator's approval through UAC, or an elevated administrator; never a
    /// parents' code, 0.4.0 kept that in the config the account writes): the step that confirmed it set exactly the confirmed code (or the
    /// Windows mode) and dropped every other one, and from now on a lock taken over from 0.4.0 is the service's own: its code (or the Windows
    /// sign-in) opens it like any other (security review H-1, second round).
    /// </summary>
    private void ConfirmLegacy(RequestContext context)
    {
        if (!AdminApproved(context)) return;
        if (parts.EndLegacyLock?.Invoke(context.Caller.Sid) == true)
        {
            parts.Log.Info($"[legacy] lock of {context.Caller.Sid} taken over from 0.4.0 confirmed{Approval(context)}");
            parts.Events?.Add(context.Caller.Sid, "lock", "lock of 0.4.0 confirmed");
        }
        else parts.Log.Error($"[legacy] lock of {context.Caller.Sid} of 0.4.0: the confirmation could not be noted or its take-over still waits (asked again)");
    }

    private static bool Approved(RequestContext context) => context.Grant is GrantKinds.Parent or GrantKinds.WindowsAdmin || context.Caller.IsElevatedAdmin;

    /// <summary>An administrator's approval: the only one that confirms a lock of 0.4.0.</summary>
    private static bool AdminApproved(RequestContext context) => context.Grant == GrantKinds.WindowsAdmin || context.Caller.IsElevatedAdmin;

    /// <summary>An administrator approved through UAC (SERVICE_USER_DEFINED_CONTROL, from the SCM's thread): quick, in memory.</summary>
    public void OnControl(int control)
    {
        if (control is < AdminApprovals.FirstControl or > AdminApprovals.LastControl) return;
        parts.Log.Info(parts.Approvals.Approve(control)
            ? $"[grant] administrator approval {control} given (UAC)"
            : $"[grant] control {control} matches no pending approval");
    }

    private static IReadOnlyCollection<CodeKind>? KindsOf(string? kind) => kind switch
    {
        CodeKinds.Couch => [CodeKind.Couch],
        CodeKinds.Parent => [CodeKind.Parent],
        CodeKinds.Both => [CodeKind.Couch, CodeKind.Parent],
        _ => null
    };

    private static IReadOnlyCollection<CodeKind>? KindsOf(ValidatedRequest request) => request.Args switch
    {
        PasscodeHashArgs args => KindsOf(args.Kind),
        CodeKindArgs args => KindsOf(args.Kind),
        _ => null
    };

    /// <summary>
    /// A first code for the caller's own account, never over an existing one (that is ChangePasscode). Free only while the account's couch is
    /// not locked at all (mode off or never set, no code: trust on first use, also the shell's one-time migration of its local hash). Once the
    /// account is protected (a code, or the Windows or code mode, review N1) it needs proof: the Windows unlock for a couch code in the Windows
    /// mode, otherwise a parent's or an administrator's approval (the kinds come from the policy), or an elevated administrator. A lock taken
    /// over from 0.4.0 counts as protected too, also for the code of the shell's config (its user could have written it there): a parent's or
    /// an administrator's approval only (security review H-1).
    /// </summary>
    internal AuthorizationDecision EnrollAllowed(CallerInfo caller, ValidatedRequest request)
    {
        if (request.Args is not PasscodeHashArgs args || KindsOf(args.Kind) is not { } kinds) return AuthorizationDecision.Denied("no code kind");
        if (!parts.Codes.IsAvailable) return AuthorizationDecision.Denied("codes not available");
        if (IsLegacy(caller.Sid)) return LegacyOnlyThroughTheMode;
        if (kinds.Any(k => parts.Codes.Has(caller.Sid, k))) return AuthorizationDecision.Denied("a code of that kind exists already");
        if (View(caller.Sid).Protected && !caller.IsElevatedAdmin) return AuthorizationDecision.GrantNeeded;
        return AuthorizationDecision.Allowed;
    }

    internal AuthorizationDecision CodeExists(CallerInfo caller, ValidatedRequest request)
    {
        if (KindsOf(request) is not { } kinds) return AuthorizationDecision.Denied("no code kind");
        if (!parts.Codes.IsAvailable) return AuthorizationDecision.Denied("codes not available");
        if (IsLegacy(caller.Sid)) return LegacyOnlyThroughTheMode;
        return kinds.Any(k => parts.Codes.Has(caller.Sid, k)) ? AuthorizationDecision.Allowed : AuthorizationDecision.Denied("no code of that kind");
    }

    /// <summary>
    /// A lock of 0.4.0 not confirmed yet: no code is enrolled, changed or removed on its own (security review H-1, third round). The one way
    /// is the administrator's confirmation through the lock mode (<see cref="ConfirmMode"/>), which sets the mode and the code together and
    /// drops everything else. Refused before any grant is asked for, so nobody is sent to UAC for nothing.
    /// </summary>
    private static readonly AuthorizationDecision LegacyOnlyThroughTheMode = AuthorizationDecision.Denied("the lock of 0.4.0 is confirmed through the lock mode first");

    /// <summary>
    /// Raising the lock mode is free; lowering it needs proof (the kinds come from the policy by the current mode). A code may come along only
    /// when the code lock is switched on from no lock at all, and never over a code somebody approved. A lock taken over from 0.4.0 takes a
    /// parent's or an administrator's approval for raising too (the Windows mode would let the user's own sign-in release it, review H-1).
    /// </summary>
    internal AuthorizationDecision LockModeChange(CallerInfo caller, ValidatedRequest request)
    {
        if (request.Args is not LockModeArgs { Mode: var mode } args || LockModes.Rank(mode) < 0) return AuthorizationDecision.Denied("no mode");
        if (!parts.Codes.IsAvailable) return AuthorizationDecision.Denied("codes not available");
        if (IsLegacy(caller.Sid))
        {
            // The confirmation of a lock of 0.4.0 (security review H-1, second round): it starts the account's codes over, so a code may come
            // along whatever was there, and the code lock needs its code in the same step. An administrator only (the policy's kinds).
            if (args.Hash is not null && KindsOf(args.Kind) is null) return AuthorizationDecision.Denied("no code kind");
            // The lock stays a lock: confirmed as the code lock or the Windows lock, never as "no lock" (a release is ReleaseLockPolicies).
            if (LockModes.Rank(mode) <= 0) return AuthorizationDecision.Denied("the lock of 0.4.0 is confirmed as the code or the Windows lock");
            if (mode == LockModes.Code && (args.Hash is null || KindsOf(args.Kind)?.Contains(CodeKind.Couch) != true))
                return AuthorizationDecision.Denied("the code lock of 0.4.0 is confirmed with its code");
            return caller.IsElevatedAdmin ? AuthorizationDecision.Allowed : AuthorizationDecision.GrantNeeded;
        }
        if (args.Hash is not null)
        {
            if (LockModes.Rank(parts.Codes.StoredMode(caller.Sid)) > 0) return AuthorizationDecision.Denied("a code comes along only from no lock");
            // Like EnrollAllowed: never next to any code somebody approved (couch or parents'), whatever kind comes along (review F1).
            if (KindsOf(args.Kind) is null || parts.Codes.HasApproved(caller.Sid, CodeKind.Couch) || parts.Codes.HasApproved(caller.Sid, CodeKind.Parent))
                return AuthorizationDecision.Denied("an approved code exists");
        }
        if (caller.IsElevatedAdmin) return AuthorizationDecision.Allowed;
        var current = parts.Codes.View(caller.Sid).Mode;
        if (LockModes.Rank(mode) >= LockModes.Rank(current)) return AuthorizationDecision.Allowed;
        return AuthorizationDecision.GrantNeeded;
    }

    /// <summary>
    /// The lock mode. From the code lock to the Windows lock the couch code goes in the same step (review M-a): in the Windows mode nothing but
    /// the Windows unlock (or a parent or an administrator) opens the couch.
    /// </summary>
    private byte[] SetMode(RequestContext context)
    {
        var args = context.Request.GetArgs<LockModeArgs>();
        var mode = args.Mode;
        if (IsLegacy(context.Caller.Sid)) return ConfirmMode(context, args);
        var before = parts.Codes.View(context.Caller.Sid);
        var dropCouch = mode == LockModes.Windows && before.HasCouch;
        // A lock switched on from no lock: what was enrolled freely at the open couch goes, unless it comes along now (review 3, item 2).
        var clearOpen = LockModes.Rank(parts.Codes.StoredMode(context.Caller.Sid)) <= 0 && LockModes.Rank(mode) > 0;
        var kinds = args.Hash is null ? null : KindsOf(args.Kind);
        switch (parts.Codes.SetMode(context.Caller.Sid, mode, removeCouch: dropCouch, clearOpen: clearOpen, kinds: kinds, hash: args.Hash))
        {
            case CodeSet.Saved:
                break;
            case CodeSet.Exists:
                return Dispatcher.Fail(ErrorCodes.Denied);
            default:
                return Dispatcher.Fail(ErrorCodes.Unavailable);
        }
        if (before.Mode != mode || dropCouch || kinds is not null)
        {
            parts.Log.Info($"[codes] lock mode {before.Mode} -> {mode}{(dropCouch ? ", couch code removed" : "")}{(kinds is not null ? $", {args.Kind} code set" : "")} for {context.Caller.Describe()}{Approval(context)}");
            parts.Events?.Add(context.Caller.Sid, "lock", $"mode {mode}");
        }
        return ProtocolJson.Serialize(Response.Success());
    }

    /// <summary>
    /// The administrator's confirmation of a lock of 0.4.0 through the lock mode (security review H-1, second round): the account's codes start
    /// over with exactly what comes along (the code lock: the code just entered; the Windows lock: no couch code), whatever the account held
    /// before (planted or not) goes in the same write, and the lock is the service's own from then on.
    /// </summary>
    private byte[] ConfirmMode(RequestContext context, LockModeArgs args)
    {
        if (!AdminApproved(context)) return Dispatcher.Fail(ErrorCodes.NeedGrant);
        if (LockModes.Rank(args.Mode) <= 0) return Dispatcher.Fail(ErrorCodes.Denied);
        var kinds = args.Hash is null ? null : KindsOf(args.Kind)?.Where(k => args.Mode != LockModes.Windows || k != CodeKind.Couch).ToList();
        var code = kinds is { Count: > 0 };
        switch (parts.Codes.Replace(context.Caller.Sid, args.Mode, code ? kinds : null, code ? args.Hash : null))
        {
            case CodeSet.Saved:
                break;
            case CodeSet.BadHash:
                return Dispatcher.Fail(ErrorCodes.BadArgument);
            default:
                return Dispatcher.Fail(ErrorCodes.Unavailable);
        }
        parts.Log.Info($"[codes] lock mode {args.Mode}{(code ? $", {args.Kind} code set" : "")}, every other code dropped, for {context.Caller.Describe()}{Approval(context)}");
        parts.Events?.Add(context.Caller.Sid, "lock", $"mode {args.Mode}");
        ConfirmLegacy(context);
        return ProtocolJson.Serialize(Response.Success());
    }

    /// <summary>Removes one of the caller's codes (the table and the state check made sure it exists and the right approval came along).</summary>
    private byte[] Remove(RequestContext context)
    {
        var sid = context.Caller.Sid;
        var args = context.Request.GetArgs<CodeKindArgs>();
        if (CodeStore.KindOf(args.Kind) is not { } kind) return Dispatcher.Fail(ErrorCodes.BadArgument);
        if (IsLegacy(sid)) return Dispatcher.Fail(ErrorCodes.Denied);
        switch (parts.Codes.Remove(sid, kind))
        {
            case CodeSet.Saved:
                parts.Log.Info($"[codes] {args.Kind} code removed for {context.Caller.Describe()}{Approval(context)}");
                parts.Events?.Add(sid, "code", $"{args.Kind} code removed");
                return ProtocolJson.Serialize(Response.Success());
            case CodeSet.Missing:
                return Dispatcher.Fail(ErrorCodes.NotFound);
            default:
                return Dispatcher.Fail(ErrorCodes.Unavailable);
        }
    }

    /// <summary>
    /// Stores a code (couch, parents' or both). A code set with a parent's or an administrator's approval replaces whatever the other kind
    /// was, unless both are set in the same step (review M4): a code planted next to it does not survive the parents' reset.
    /// </summary>
    private byte[] Store(RequestContext context, bool enroll)
    {
        var sid = context.Caller.Sid;
        var args = context.Request.GetArgs<PasscodeHashArgs>();
        if (KindsOf(args.Kind) is not { } kinds) return Dispatcher.Fail(ErrorCodes.BadArgument);
        var approved = Approved(context);
        // A lock of 0.4.0 changed in between (the state check refused it already): only through the lock mode (security review H-1).
        if (IsLegacy(sid)) return Dispatcher.Fail(ErrorCodes.Denied);
        // Enrolled without any grant: the free first code at an open couch (it does not survive a lock being switched on, review 3).
        var open = enroll && context.Grant == GrantKinds.None && !context.Caller.IsElevatedAdmin;
        var result = parts.Codes.Set(sid, kinds, args.Hash, enroll, clearOther: approved && kinds.Count == 1, open: open);
        var what = $"{args.Kind} code {(enroll ? "enrolled" : "changed")}";
        switch (result)
        {
            case CodeSet.Saved:
                parts.Log.Info($"[codes] {what} for {context.Caller.Describe()}{Approval(context)}{(approved && kinds.Count == 1 ? ", the other kind cleared" : "")}");
                parts.Events?.Add(sid, "code", what);
                return ProtocolJson.Serialize(Response.Success());
            case CodeSet.Exists:
                return Dispatcher.Fail(ErrorCodes.Denied);
            case CodeSet.Missing:
                return Dispatcher.Fail(ErrorCodes.NotFound);
            case CodeSet.BadHash:
                return Dispatcher.Fail(ErrorCodes.BadArgument);
            default:
                return Dispatcher.Fail(ErrorCodes.Unavailable);
        }
    }

    /// <summary>
    /// The buttons against the account's code. A right couch code gives a couch grant (the 2-minute unlock); a right parents' code gives a
    /// parent's grant only for the operations the request names (each once, review M3), without any it only answers "matched".
    /// </summary>
    private byte[] Verify(RequestContext context)
    {
        var caller = context.Caller;
        var args = context.Request.GetArgs<VerifyCodeArgs>();
        if (CodeStore.KindOf(args.Kind) is not { } kind) return Dispatcher.Fail(ErrorCodes.BadArgument);
        if (parts.Grants.SessionLockState(caller.TokenSessionId) is var locked && locked != false)
        {
            // Nobody at the couch can use what a right code gives while the session is locked (finding B2): no grant, and the buttons are
            // not even checked (no answer about the code). A session Windows reports as locked counts the try towards the lockout like a
            // wrong one; one whose state cannot be told is refused without counting (security review N-1).
            if (locked == true)
            {
                var refused = parts.Codes.CountRefused(caller.Sid, kind);
                parts.Log.Info($"[codes] {args.Kind} code refused for {caller.Describe()}: the session is locked ({refused.Result})");
            }
            else parts.Log.Error($"[codes] {args.Kind} code refused for {caller.Describe()}: whether the session is locked cannot be told");
            parts.Events?.Add(caller.Sid, "code", $"{args.Kind} code refused: session locked");
            return Dispatcher.Fail(ErrorCodes.Denied);
        }
        var verdict = parts.Codes.Verify(caller.Sid, kind, args.Sequence);
        switch (verdict.Result)
        {
            case CodeCheck.Granted when kind == CodeKind.Parent && args.Ops is null:
                return Answer(new CodeCheckData(CodeCheckResults.Matched));
            case CodeCheck.Granted:
                var grantKind = kind == CodeKind.Parent ? GrantKinds.Parent : GrantKinds.Couch;
                if (parts.Grants.Issue(caller, grantKind, GrantStore.OpsOf(args.Ops)) is not { } issued) return Dispatcher.Fail(ErrorCodes.Denied);
                parts.Log.Info($"[grant] {GrantStore.NameOf(grantKind)} for {caller.Describe()} (code{(args.Ops is { } ops ? ", for " + string.Join('/', ops) : "")})");
                parts.Events?.Add(caller.Sid, "grant", GrantStore.NameOf(grantKind));
                return Answer(new CodeCheckData(CodeCheckResults.Granted, 0, issued.ToData()));
            case CodeCheck.Wrong:
                parts.Log.Info($"[codes] wrong {args.Kind} code for {caller.Describe()}");
                parts.Events?.Add(caller.Sid, "code", $"wrong {args.Kind} code");
                return Answer(new CodeCheckData(CodeCheckResults.Wrong, Seconds(verdict.Wait)));
            case CodeCheck.LockedOut:
                return Answer(new CodeCheckData(CodeCheckResults.LockedOut, Math.Max(1, Seconds(verdict.Wait))));
            case CodeCheck.NoCode:
                return Answer(new CodeCheckData(CodeCheckResults.NoCode));
            default:
                return Dispatcher.Fail(ErrorCodes.Unavailable);
        }
    }

    /// <summary>
    /// The caller's own session was unlocked a moment ago (seen by the service, see <see cref="SessionEvents"/>). Only for an account whose
    /// lock is the Windows sign-in (in the code mode the code is the lock, review H1), and never for an account without a password (anyone
    /// unlocks that with a click). Checked before the unlock is taken, so a refused request does not use it up.
    /// </summary>
    private byte[] UnlockGrant(CallerInfo caller)
    {
        if (parts.Codes.View(caller.Sid).Mode != LockModes.Windows) return Dispatcher.Fail(ErrorCodes.Denied);
        if (parts.IsBlankPassword(caller.Sid))
        {
            parts.Log.Error($"[grant] no Windows-unlock grant for {caller.Describe()}: the account has no password");
            return Dispatcher.Fail(ErrorCodes.Denied);
        }
        // A locked session gets no grant: refused before the unlock is taken, so it is not used up (security review N-2).
        if (parts.Grants.IsSessionLocked(caller.TokenSessionId)) return Dispatcher.Fail(ErrorCodes.Denied);
        if (!parts.Sessions.TryTake(caller)) return Dispatcher.Fail(ErrorCodes.NotFound);
        if (parts.Grants.Issue(caller, GrantKinds.WindowsUnlock) is not { } issued) return Dispatcher.Fail(ErrorCodes.Denied);
        parts.Log.Info($"[grant] {GrantKindNames.WindowsUnlock} for {caller.Describe()}");
        parts.Events?.Add(caller.Sid, "grant", GrantKindNames.WindowsUnlock);
        return ProtocolJson.Serialize(Response<GrantData>.Success(issued.ToData()));
    }

    /// <summary>A pending administrator approval for the caller's session (see <see cref="AdminApprovals"/>): the control code for <c>sc.exe control</c>.</summary>
    private byte[] RequestApproval(RequestContext context)
    {
        var caller = context.Caller;
        if (parts.Approvals.IsOpen(caller.TokenSessionId)) return Dispatcher.Fail(ErrorCodes.Busy);
        var ops = GrantStore.OpsOf(context.Request.GetArgs<AdminApprovalArgs>().Ops) ?? [];
        if (parts.Approvals.Register(caller, ops) is not { } control) return Dispatcher.Fail(ErrorCodes.Busy);
        parts.Log.Info($"[grant] administrator approval {control} asked by {caller.Describe()} for {(ops.Count == 0 ? "a confirmation only" : string.Join('/', ops))}");
        return ProtocolJson.Serialize(Response<AdminApprovalData>.Success(new AdminApprovalData(control, (int)AdminApprovals.Lifetime.TotalSeconds)));
    }

    /// <summary>The caller's own approval, once an administrator gave it: a WindowsAdmin grant for exactly the operations asked for.</summary>
    private byte[] CollectApproval(RequestContext context)
    {
        var caller = context.Caller;
        // A locked session gets no grant: refused before the approval is collected, so it stays for after the unlock (security review N-2).
        if (parts.Grants.IsSessionLocked(caller.TokenSessionId)) return Dispatcher.Fail(ErrorCodes.Denied);
        var (state, ops) = parts.Approvals.Collect(caller);
        switch (state)
        {
            case AdminApprovals.State.Pending:
                return Answer(new AdminApprovalResult("pending"));
            case AdminApprovals.State.None:
                return Answer(new AdminApprovalResult("none"));
        }
        parts.Events?.Add(caller.Sid, "grant", "administrator approval");
        if (ops.Count == 0)
        {
            parts.Log.Info($"[grant] administrator confirmation for {caller.Describe()}");
            return Answer(new AdminApprovalResult("approved"));
        }
        if (parts.Grants.Issue(caller, GrantKinds.WindowsAdmin, ops) is not { } issued) return Dispatcher.Fail(ErrorCodes.Denied);
        parts.Log.Info($"[grant] {GrantKindNames.WindowsAdmin} for {caller.Describe()}, for {string.Join('/', ops)} (UAC)");
        return Answer(new AdminApprovalResult("approved", issued.ToData()));
    }

    private static string Approval(RequestContext context) =>
        context.Caller.IsElevatedAdmin ? " (elevated administrator)" : context.Grant == GrantKinds.None ? "" : $" ({GrantStore.NameOf(context.Grant)} grant)";

    private static byte[] Answer(CodeCheckData data) => ProtocolJson.Serialize(Response<CodeCheckData>.Success(data));

    private static byte[] Answer(AdminApprovalResult data) => ProtocolJson.Serialize(Response<AdminApprovalResult>.Success(data));

    private static int Seconds(TimeSpan wait) => wait <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(wait.TotalSeconds);
}
