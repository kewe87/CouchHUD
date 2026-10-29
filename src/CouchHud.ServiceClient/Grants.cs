using System.Text.RegularExpressions;
using CouchHud.Service.Contracts;

namespace CouchHud.ServiceClient;

/// <summary>The kinds of grant the service hands out (P9), as the shell keeps them.</summary>
public enum GrantKind
{
    /// <summary>The couch code (VerifyCode "couch").</summary>
    Couch,

    /// <summary>The caller's own Windows unlock, seen by the service (Windows lock mode only).</summary>
    WindowsUnlock,

    /// <summary>The parents' code (VerifyCode "parent" with operations).</summary>
    Parent,

    /// <summary>An administrator's Windows credential (Windows' own prompt), for named operations.</summary>
    WindowsAdmin
}

/// <summary>Which codes the service holds for the account and its lock mode (nulls: it could not tell).</summary>
/// <param name="LegacyLock">The lock was taken over from 0.4.0 and waits for an administrator's confirmation (security review H-1).</param>
/// <param name="Damaged">The service's code file is damaged: nothing is checked or stored until an administrator resets it.</param>
public sealed record HeldCodes(bool? Couch, bool? Parent, string? LockMode, bool LegacyLock = false, bool Damaged = false);

/// <summary>
/// The grants the service gave this shell, in memory only (never written anywhere, never logged). Two lives, like in the service:
/// <list type="bullet">
/// <item>The unlock's grants (couch code, Windows unlock): the newest per kind, offered to every operation that takes a grant until they
/// run out (a few seconds before the service's end, <see cref="Margin"/>) or the couch locks again (<see cref="ClearUnlock"/>).</item>
/// <item>A parent's or an administrator's approval: only for the operations it was asked for, each once (the service counts the same).</item>
/// </list>
/// </summary>
public sealed class GrantCache(Func<DateTimeOffset>? now = null)
{
    /// <summary>A grant is not offered in its last seconds (the request could arrive after it ran out).</summary>
    public static readonly TimeSpan Margin = TimeSpan.FromSeconds(5);

    private static readonly Regex Format = new(@"^[A-Za-z0-9+/]{43}=\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private sealed record Bound(string Grant, GrantKind Kind, DateTimeOffset Until, List<Operation> Ops);

    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    private readonly Dictionary<GrantKind, (string Grant, DateTimeOffset Until)> _open = [];
    private readonly List<Bound> _bound = [];
    private readonly object _lock = new();

    /// <summary>Keeps a grant the service answered with; false (and nothing kept) for anything that does not look like one.</summary>
    public bool Set(GrantData? data)
    {
        if (data is null || !Format.IsMatch(data.Grant ?? "") || KindOf(data.Kind) is not { } kind || data.ExpiresInSeconds is <= 0 or > 3600) return false;
        var until = _now() + TimeSpan.FromSeconds(data.ExpiresInSeconds) - Margin;
        var bound = kind is GrantKind.Parent or GrantKind.WindowsAdmin;
        List<Operation>? ops = null;
        if (data.Ops is { Length: > 0 } names)
        {
            ops = [];
            foreach (var name in names)
            {
                if (!Enum.TryParse<Operation>(name, out var op) || !RequestValidator.GrantableOperations.Contains(name)) return false;
                ops.Add(op);
            }
        }
        if (bound != ops is not null) return false;
        lock (_lock)
        {
            if (bound) _bound.Add(new Bound(data.Grant!, kind, until, ops!));
            else _open[kind] = (data.Grant!, until);
        }
        return true;
    }

    /// <summary>Every grant goes (a game starts, the HUD is left).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _open.Clear();
            _bound.Clear();
        }
    }

    /// <summary>
    /// The couch locked again: the unlock's grants go. A parent's or an administrator's approval stays for its short life, for the one action
    /// it was asked for (it may be asked while the couch is locked, e.g. the cleanup of stuck restrictions).
    /// </summary>
    public void ClearUnlock()
    {
        lock (_lock) _open.Clear();
    }

    /// <summary>The service refused this grant: an approval is not offered again (the unlock's grants run out on their own).</summary>
    public void Forget(string? grant)
    {
        if (grant is null) return;
        lock (_lock) _bound.RemoveAll(b => b.Grant == grant);
    }

    public bool Has(GrantKind kind)
    {
        lock (_lock)
        {
            var now = _now();
            return kind is GrantKind.Parent or GrantKind.WindowsAdmin
                ? _bound.Any(b => b.Kind == kind && now < b.Until)
                : _open.TryGetValue(kind, out var entry) && now < entry.Until;
        }
    }

    /// <summary>An unlock grant of the kinds, in the order given.</summary>
    public string? Of(params GrantKind[] kinds)
    {
        lock (_lock)
        {
            var now = _now();
            foreach (var kind in kinds)
            {
                if (!_open.TryGetValue(kind, out var entry)) continue;
                if (now < entry.Until) return entry.Grant;
                _open.Remove(kind);
            }
            return null;
        }
    }

    /// <summary>An approval asked for <paramref name="operation"/>: handed out once per use it was asked for.</summary>
    public string? Approval(Operation operation)
    {
        lock (_lock)
        {
            var now = _now();
            _bound.RemoveAll(b => now >= b.Until);
            var bound = _bound.FirstOrDefault(b => b.Ops.Contains(operation));
            if (bound is null) return null;
            bound.Ops.Remove(operation);
            if (bound.Ops.Count == 0) _bound.Remove(bound);
            return bound.Grant;
        }
    }

    /// <summary>
    /// The grant for an operation: an approval asked for it first, else the unlock's (couch code, then Windows unlock); null for operations that
    /// take none. The service decides by the account's lock which kinds count.
    /// </summary>
    public string? For(Operation operation) =>
        TakesGrant(operation) ? Approval(operation) ?? Of(GrantKind.Couch, GrantKind.WindowsUnlock) : null;

    /// <summary>
    /// For storing or removing a code of <paramref name="codeKind"/>: the couch code may go with the unlock's grant (its own code, or the
    /// Windows unlock for a first couch code in the Windows mode; the service decides which counts); the parents' code (or both) only with an
    /// approval.
    /// </summary>
    public string? ForCode(Operation operation, string codeKind) =>
        codeKind == CodeKinds.Couch ? Approval(operation) ?? Of(GrantKind.Couch, GrantKind.WindowsUnlock) : Approval(operation);

    /// <summary>The operations that take a grant at all (the rest never get one sent along).</summary>
    public static bool TakesGrant(Operation operation) => RequestValidator.GrantableOperations.Contains(operation.ToString());

    public static GrantKind? KindOf(string? wire) => wire switch
    {
        GrantKindNames.Couch => GrantKind.Couch,
        GrantKindNames.WindowsUnlock => GrantKind.WindowsUnlock,
        GrantKindNames.Parent => GrantKind.Parent,
        GrantKindNames.WindowsAdmin => GrantKind.WindowsAdmin,
        _ => null
    };
}

/// <summary>The answer to a code check: what the service said, how long to wait, and whether the service could be asked at all.</summary>
public sealed record CodeCheck(OpResult Result, string? Outcome, int WaitSeconds, GrantKind? Kind)
{
    /// <summary>Right, with a grant.</summary>
    public bool Granted => Result.Ok && Outcome == CodeCheckResults.Granted && Kind is not null;

    /// <summary>Right (with or without a grant).</summary>
    public bool Right => Result.Ok && Outcome is CodeCheckResults.Granted or CodeCheckResults.Matched;
}

/// <summary>
/// The shell's way through "needs a parent or an administrator": run the operation, and when the service answers "needGrant", get an approval
/// (the caller decides how: the parents' code where it works, else Windows' administrator prompt) and try exactly once more. Pure.
/// </summary>
public static class GrantFlow
{
    public static async Task<OpResult> WithGrantAsync(Func<Task<OpResult>> operation, Func<Task<bool>> obtainGrant)
    {
        var result = await operation().ConfigureAwait(false);
        if (result.Failure != FailureKind.NeedGrant) return result;
        if (!await obtainGrant().ConfigureAwait(false)) return result;
        return await operation().ConfigureAwait(false);
    }
}

/// <summary>How the one-time migration of the local code ended.</summary>
public enum MigrationResult
{
    /// <summary>The service holds the local code now.</summary>
    Done,
    /// <summary>The service holds a code of its own (or its lock wants an approval): it wins, the user is told.</summary>
    Conflict,
    /// <summary>Nothing to migrate, or the service could not be asked: try again later.</summary>
    NotYet,
    /// <summary>The service's lock was taken over from 0.4.0: the code goes over only with an administrator's approval (asked at the next code entry).</summary>
    NeedsApproval
}

/// <summary>
/// The codes between the shell and the service (P9), without the UI: the one-time migrations of the shell's local hash and lock mode, and
/// storing a new code.
/// </summary>
public static class CodeFlow
{
    /// <summary>
    /// The one-time migration (trust on first use) of the couch code and the lock mode, in the steps the service takes from an open couch:
    /// the code lock with its code in one step (a code enrolled alone would not survive the lock being switched on); the Windows lock alone
    /// (it needs no code); no lock: the code alone. Only a hash in the format of <c>Passcode.Hash</c> is sent. "Conflict": the service
    /// already holds a code or a lock of its own: it wins, and the shell says so instead of pretending it took the local one (review L-c).
    /// The parents' code is never migrated from the hash: it is set as "both" with the next new code, or by a parent or an administrator.
    /// <paramref name="withApproval"/>: when the service wants an approval for the step (a lock taken over from 0.4.0, security review H-1),
    /// the caller gets it this way (an administrator through UAC); without it the step is not taken ("conflict").
    /// </summary>
    public static async Task<MigrationResult> MigrateAsync(IPrivilegedOps ops, string? localHash, string mode,
        Func<Operation, Func<Task<OpResult>>, Task<OpResult>>? withApproval = null)
    {
        var hash = RequestValidator.IsPasscodeHash(localHash) ? localHash : null;
        Operation operation;
        Func<Task<OpResult>> step;
        switch (mode)
        {
            case LockModes.Code when hash is not null:
                (operation, step) = (Operation.SetLockMode, () => ops.SetLockModeAsync(LockModes.Code, hash, CodeKinds.Couch));
                break;
            case LockModes.Windows:
                (operation, step) = (Operation.SetLockMode, () => ops.SetLockModeAsync(LockModes.Windows));
                break;
            default:
                if (hash is null) return MigrationResult.NotYet;
                (operation, step) = (Operation.EnrollPasscode, () => ops.EnrollPasscodeAsync(hash, CodeKinds.Couch));
                break;
        }
        var result = await step().ConfigureAwait(false);
        if (result.Failure == FailureKind.NeedGrant && withApproval is not null) result = await withApproval(operation, step).ConfigureAwait(false);
        if (result.Ok) return MigrationResult.Done;
        return result.Code == ErrorCodes.Denied || result.Failure == FailureKind.NeedGrant ? MigrationResult.Conflict : MigrationResult.NotYet;
    }

    /// <summary>
    /// The service holds a parents' code the shell did not set (no parental controls here): a code someone else enrolled, the shell warns
    /// (review 3, item 2).
    /// </summary>
    public static bool UnknownParentCode(HeldCodes? held, bool parentalEnabled) => held?.Parent == true && !parentalEnabled;

    /// <summary>
    /// The code lock may be switched on without a new code only when the service holds a couch code that survives the switch: a lock is on
    /// already (from no lock, every code enrolled freely at the open couch goes with the switch, review 3, item 2). Otherwise the mode would
    /// be the code lock with no code to open it, and only an administrator could help. Not known (null) or a lock of 0.4.0: no.
    /// </summary>
    public static bool CodeLockKeepsCode(HeldCodes? held) => held is { Couch: true, LegacyLock: false } && LockModes.Rank(held.LockMode) > 0;

    /// <summary>How the code lock is switched on (<see cref="PlanCodeLock"/>).</summary>
    public enum CodeLockPlan
    {
        /// <summary>Nothing special is held: a new code comes along with the switch in one step (from no lock), or the shell's old paths apply.</summary>
        Default,
        /// <summary>The service holds a couch code under a lock mode that is on: the switch needs no code, the code from before applies again.</summary>
        KeepHeldCode,
        /// <summary>
        /// A hash along with the switch would be refused (a lock mode is on, or a parents' code is held and may be an approved one): a code that
        /// comes along is stored first, with the approval the service asks for (the old code, the parents' code or an administrator), then the mode is raised.
        /// </summary>
        StoreThenSwitch
    }

    /// <summary>
    /// What the service's state means for switching on the code lock (the service's own rule stays as strict as it is: a code never comes along
    /// next to one somebody approved, see <c>LockModeChange</c>). A lock of 0.4.0 not confirmed yet and an unknown state keep the default.
    /// </summary>
    public static CodeLockPlan PlanCodeLock(HeldCodes? held)
    {
        if (held is null or { LegacyLock: true }) return CodeLockPlan.Default;
        if (LockModes.Rank(held.LockMode) > 0) return held.Couch == true ? CodeLockPlan.KeepHeldCode : CodeLockPlan.StoreThenSwitch;
        return held.Parent == true ? CodeLockPlan.StoreThenSwitch : CodeLockPlan.Default;
    }

    /// <summary>How <see cref="StoreThenRaiseAsync"/> ended.</summary>
    public enum StoreRaiseOutcome
    {
        /// <summary>The service holds the couch code under the raised mode.</summary>
        Done,
        /// <summary>The service did not answer when asked afterwards: not confirmed.</summary>
        Unconfirmed,
        /// <summary>The mode was raised but the service holds no couch code, also not after storing it once more.</summary>
        CodeMissing
    }

    /// <summary>
    /// Stores a couch code, raises the lock mode and looks at the service again. The service drops codes that were enrolled at an open couch
    /// when the mode is raised (a held parents' code may be one of them, the service cannot tell the shell), so the new code can be gone after
    /// the raise. Then it is stored once more (the mode is on, so it is an approved code that stays); if it is still missing the outcome says
    /// so and the caller must not take the lock over in its config (fail closed). A refused store or raise throws before anything else happens.
    /// </summary>
    public static async Task<StoreRaiseOutcome> StoreThenRaiseAsync(string mode, Func<Task> store, Func<Task> raise, Func<Task<HeldCodes?>> status, Action<string>? log = null)
    {
        await store().ConfigureAwait(false);
        await raise().ConfigureAwait(false);
        // Only the code lock keeps a couch code; under any other mode the service drops it on purpose, so it is never stored again there.
        if (mode != LockModes.Code) return StoreRaiseOutcome.Done;
        var after = await status().ConfigureAwait(false) ?? await status().ConfigureAwait(false);
        if (after is null) return StoreRaiseOutcome.Unconfirmed;
        if (after.Couch == true) return StoreRaiseOutcome.Done;
        log?.Invoke("the mode was raised but the couch code is gone (dropped with the open codes); storing it once more");
        try { await store().ConfigureAwait(false); }
        catch (Exception ex) { log?.Invoke($"storing again failed: {ex.GetType().Name}"); return StoreRaiseOutcome.CodeMissing; }
        after = await status().ConfigureAwait(false) ?? await status().ConfigureAwait(false);
        return after is null ? StoreRaiseOutcome.Unconfirmed : after.Couch == true ? StoreRaiseOutcome.Done : StoreRaiseOutcome.CodeMissing;
    }

    /// <summary>What to do with the service's answer to the one-step switch (mode and new code together).</summary>
    public enum TogetherOutcome
    {
        /// <summary>The service took it.</summary>
        Done,
        /// <summary>Refused only because an approved code is held: use the approved way (store, then raise).</summary>
        Fallback,
        /// <summary>Any other refusal (wrong code, locked out, a grant missing, unavailable, denied with nothing held): the caller fails with the reason.</summary>
        Refused
    }

    /// <summary>
    /// <paramref name="held"/> is what the service holds right now, asked for only after a "denied" (null otherwise, or when it did not answer).
    /// Only "denied" with a held couch or parents' code, or a lock mode that is on, falls back; a lock of 0.4.0 not confirmed yet never does.
    /// </summary>
    public static TogetherOutcome ClassifyTogether(OpResult result, HeldCodes? held)
    {
        if (result.Ok) return TogetherOutcome.Done;
        if (result.Code != ErrorCodes.Denied || held is null or { LegacyLock: true } or { Damaged: true }) return TogetherOutcome.Refused;
        return held.Couch == true || held.Parent == true || LockModes.Rank(held.LockMode) > 0 ? TogetherOutcome.Fallback : TogetherOutcome.Refused;
    }

    /// <summary>
    /// Switches the code lock on from no lock. First the one step (mode and code together: <paramref name="together"/> returns false when the
    /// service refused it because an approved code is already there, e.g. one a first attempt stored before its raise failed, and the
    /// plan could not see that); then the approved way, <see cref="StoreThenRaiseAsync"/> (change with the approval the service asks for,
    /// then raise). A refusal for any other reason throws in <paramref name="together"/>. Nothing is weakened: the fallback is the same
    /// store with the same grants a lock that is on already uses.
    /// </summary>
    public static async Task<StoreRaiseOutcome> SwitchOnWithCodeAsync(string mode, Func<Task<bool>> together, Func<Task> store, Func<Task> raise,
        Func<Task<HeldCodes?>> status, Action<string>? log = null)
    {
        if (await together().ConfigureAwait(false)) return StoreRaiseOutcome.Done;
        log?.Invoke("the service holds an approved code already; storing the new one with the approval it asks for, then raising the mode");
        return await StoreThenRaiseAsync(mode, store, raise, status, log).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether a refused code operation (<paramref name="result"/>) was refused because the service's code file is damaged
    /// (<paramref name="held"/>, asked after the refusal): "denied" or "unavailable" while the service reports the damage. The HUD then says
    /// so plainly and offers the administrator's reset instead of a generic refusal.
    /// </summary>
    public static bool RefusedForDamage(OpResult result, HeldCodes? held) =>
        !result.Ok && result.Code is ErrorCodes.Denied or ErrorCodes.Unavailable && held is { Damaged: true };

    /// <summary>
    /// Why a code that came along with the lock switch was probably refused, from what the service holds: "codeHeld" (a couch code is still
    /// stored), "parentHeld" (a parents' code is), else null. Reveals nothing the settings page does not show anyway (serviceCodes).
    /// </summary>
    public static string? RefusalHint(HeldCodes? held) => held switch
    {
        null or { LegacyLock: true } => null,
        { Damaged: true } => "storeDamaged",
        { Couch: true } => "codeHeld",
        { Parent: true } => "parentHeld",
        _ => null
    };

    /// <summary>
    /// A new code. With parental controls as couch and parents' code in one step ("both": the HUD uses one code for both, and nothing planted
    /// next to it survives); a first one is enrolled, an existing pair changed with the parents' approval. Without parental controls the couch
    /// code alone: enrolled when it is the first, otherwise changed with its own grant. <paramref name="withGrant"/> asks for an approval for the
    /// named operation when the service still wants one.
    /// </summary>
    public static async Task<OpResult> StoreAsync(IPrivilegedOps ops, string hash, bool parental,
        Func<Operation, Func<Task<OpResult>>, Task<OpResult>> withGrant)
    {
        var kind = parental ? CodeKinds.Both : CodeKinds.Couch;
        var result = await ops.EnrollPasscodeAsync(hash, kind).ConfigureAwait(false);
        // "denied": a code exists, so it is a change.
        if (result.Code == ErrorCodes.Denied) return await withGrant(Operation.ChangePasscode, () => ops.ChangePasscodeAsync(hash, kind)).ConfigureAwait(false);
        if (result.Failure == FailureKind.NeedGrant) return await withGrant(Operation.EnrollPasscode, () => ops.EnrollPasscodeAsync(hash, kind)).ConfigureAwait(false);
        return result;
    }
}

/// <summary>
/// The uninstall's first step with a running service (an elevated administrator): ReleaseAllAccounts, and when its answer did not come in
/// time (the job runs on in the service), Status until the release reports "done" or "failed", at most <paramref name="cap"/> long.
/// </summary>
public static class UninstallRelease
{
    public static readonly TimeSpan DefaultCap = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);

    /// <returns>
    /// "done", "failed", "noService" (none installed, or none by design), "notRunning" (installed, stopped), "unreachable" (registered and
    /// running, but the pipe could not be opened: e.g. a caller from a network logon, whom the pipe's access list refuses on purpose),
    /// "refused" (the service said no) or "timeout" (still running at the cap). The log names the real reason, so a release that did not
    /// happen is never taken for "there was no service" (VM test 0.5).
    /// </returns>
    public static async Task<string> RunAsync(Func<Task<OpResult>> release, Func<Task<string?>> releaseStatus, Func<TimeSpan, Task> delay,
        Func<DateTimeOffset> now, TimeSpan cap)
    {
        var started = now();
        var result = await release().ConfigureAwait(false);
        if (result.Ok) return "done";
        if (result.Failure is FailureKind.NotExpected or FailureKind.NotInstalled) return "noService";
        if (result.Failure == FailureKind.NotRunning) return "notRunning";
        if (result.Failure == FailureKind.Unreachable) return "unreachable";
        if (result.Failure is FailureKind.Squatted or FailureKind.Inconsistent) return "refused";
        // "failed": the job ended and some account is left; "timeout" (or busy): the job may still run.
        if (result.Failure is not (FailureKind.Timeout or FailureKind.Failed or FailureKind.Retry)) return "refused";
        while (true)
        {
            var status = await releaseStatus().ConfigureAwait(false);
            if (status is "done" or "failed") return status;
            if (status is null or "none") return result.Failure == FailureKind.Failed ? "failed" : "refused";
            if (now() - started >= cap) return "timeout";
            await delay(Poll).ConfigureAwait(false);
        }
    }
}
