using System.Text;
using CouchHud.Core.Security;
using CouchHud.Privileged;
using CouchHud.Service.Authorization;
using CouchHud.Service.Contracts;
using CouchHud.Service.Dispatch;
using CouchHud.Service.State;

namespace CouchHud.Service.Tests;

/// <summary>Codes and hashes for the P9 tests (real PBKDF2 hashes, computed once).</summary>
internal static class Codes
{
    public static readonly string[] A = ["a", "b", "x", "y", "up", "down"];
    public static readonly string[] B = ["lb", "rb", "left", "right", "a", "a"];
    public static readonly string[] Wrong = ["b", "b", "b", "b", "b", "b"];
    public static readonly string HashA = Passcode.Hash(A);
    public static readonly string HashB = Passcode.Hash(B);

    /// <summary>A fast stand-in for PBKDF2 that knows the two hashes above.</summary>
    public static bool FastVerify(IReadOnlyList<string> sequence, string hash) =>
        (hash == HashA && sequence.SequenceEqual(A)) || (hash == HashB && sequence.SequenceEqual(B));
}

/// <summary>The service's code part wired like the service: real code store (temp folder), real grant store, the dispatcher.</summary>
internal sealed class CodeRig : IDisposable
{
    private readonly TempDir _dir = new();

    public FakeClock Clock { get; } = new();
    public CapturingSink Log { get; } = new();
    public CodeStore Codes { get; }
    public GrantStore Grants { get; }
    public SessionEvents Sessions { get; }
    public AdminApprovals Approvals { get; }
    public HashSet<string> BlankPasswords { get; } = [];
    public Harness Harness { get; }
    public Dictionary<int, string> SessionUsers { get; } = new() { [1] = Callers.UserSid, [2] = Callers.OtherSid };
    public string Folder => _dir.Path;

    /// <summary>Windows sessions that are locked right now (what WindowsSessions.IsLocked tells the service).</summary>
    public HashSet<int> LockedSessions { get; } = [];

    /// <summary>Sessions whose lock state Windows cannot tell (IsLocked answers null).</summary>
    public HashSet<int> UnknownSessions { get; } = [];

    /// <summary>Accounts whose lock the service took over from 0.4.0 and nobody confirmed yet (OperationsA.IsLegacyLocked).</summary>
    public HashSet<string> Legacy { get; } = new(StringComparer.Ordinal);

    public CodeHandlers Handlers { get; private set; } = null!;

    /// <summary>Operations the harness answers with "ok" once authorized (the P5 handlers are not part of this rig).</summary>
    /// <param name="codeFile">The code file found at the start (a damaged one, for the reset); none by default.</param>
    public CodeRig(string? codeFile = null)
    {
        if (codeFile is not null) File.WriteAllText(Path.Combine(_dir.Path, CodeStore.FileName), codeFile);
        Codes = CodeStore.Open(_dir.Path, Log, Clock, () => 1_000_000, Tests.Codes.FastVerify);
        Grants = new GrantStore(Clock, sessionLocked: s => UnknownSessions.Contains(s) ? null : LockedSessions.Contains(s));
        Sessions = new SessionEvents(Clock, Grants, s => SessionUsers.GetValueOrDefault(s), Log);
        Approvals = new AdminApprovals(Clock);
        CodeParts parts = new(Codes, Grants, Sessions, Approvals, BlankPasswords.Contains, Log, null, Legacy.Contains, Legacy.Remove);
        Dictionary<Operation, Handler>? table = null;
        // As the service: the dispatcher's lock view is the code handlers' (with a lock taken over from 0.4.0).
        Harness = new Harness(grants: Grants, codeState: Codes, clock: Clock, lockViews: sid => Handlers.View(sid),
            extraHandlers: h =>
            {
                table = h;
                h[Operation.ReleaseLockPolicies] = Harness.Ok;
                h[Operation.SetAutoLogon] = Harness.Ok;
                h[Operation.SetRemoteFirewall] = Harness.Ok;
            },
            extraChecks: c =>
            {
                c.Register(StateCheck.GroupsSetByService, Harness.AllowAll);
                Handlers = CodeHandlers.Register(table!, c, parts);
            });
    }

    public async Task<(Response Response, byte[] Raw)> CallAsync(CallerInfo caller, byte[] request, int advanceSeconds = 1)
    {
        // Inside every rate limit (codes 5 a minute, housekeeping 10): the tests are about the codes' own rules.
        Clock.Advance(TimeSpan.FromSeconds(advanceSeconds));
        var raw = Assert.Single(await Harness.ExchangeAsync(caller, FrameCodec.Encode(request)));
        return (ProtocolJson.DeserializeResponse(Harness.Strip(raw))!, raw);
    }

    public async Task<Response> EnrollAsync(CallerInfo caller, string hash, string kind = CodeKinds.Couch, string? grant = null) =>
        (await CallAsync(caller, ProtocolJson.SerializeRequest(Operation.EnrollPasscode, new PasscodeHashArgs(hash, kind), grant))).Response;

    public async Task<Response> ChangeAsync(CallerInfo caller, string hash, string kind = CodeKinds.Couch, string? grant = null) =>
        (await CallAsync(caller, ProtocolJson.SerializeRequest(Operation.ChangePasscode, new PasscodeHashArgs(hash, kind), grant))).Response;

    public async Task<Response> RemoveAsync(CallerInfo caller, string kind = CodeKinds.Couch, string? grant = null) =>
        (await CallAsync(caller, ProtocolJson.SerializeRequest(Operation.RemovePasscode, new CodeKindArgs(kind), grant))).Response;

    public async Task<Response> ModeAsync(CallerInfo caller, string mode, string? grant = null) =>
        (await CallAsync(caller, ProtocolJson.SerializeRequest(Operation.SetLockMode, new LockModeArgs(mode), grant))).Response;

    /// <summary>The code lock on from no lock, with its code in the same step (what the shell does).</summary>
    public async Task<Response> LockWithCodeAsync(CallerInfo caller, string hash, string kind = CodeKinds.Couch, string? grant = null) =>
        (await CallAsync(caller, ProtocolJson.SerializeRequest(Operation.SetLockMode, new LockModeArgs(LockModes.Code, hash, kind), grant))).Response;

    public async Task<CodeCheckData> VerifyAsync(CallerInfo caller, string[] sequence, string kind = CodeKinds.Couch, params Operation[] ops)
    {
        var (response, raw) = await CallAsync(caller, ProtocolJson.SerializeRequest(Operation.VerifyCode,
            new VerifyCodeArgs(kind, sequence, ops.Length > 0 ? [.. ops.Select(o => o.ToString())] : null)), advanceSeconds: 13);
        Assert.True(response.Ok, response.Code);
        return ProtocolJson.DeserializeResponse<CodeCheckData>(raw)!.Data!;
    }

    public async Task<string?> GrantAsync(CallerInfo caller, string[] sequence, string kind = CodeKinds.Couch, params Operation[] ops) =>
        (await VerifyAsync(caller, sequence, kind, ops)).Grant?.Grant;

    public async Task<(Response Response, GrantData? Grant)> WindowsGrantAsync(CallerInfo caller, RequestWindowsGrantArgs args)
    {
        var (response, raw) = await CallAsync(caller, ProtocolJson.SerializeRequest(Operation.RequestWindowsGrant, args), advanceSeconds: 7);
        return (response, response.Ok ? ProtocolJson.DeserializeResponse<GrantData>(raw)!.Data : null);
    }

    public Task<(Response Response, GrantData? Grant)> UnlockGrantAsync(CallerInfo caller) =>
        WindowsGrantAsync(caller, new RequestWindowsGrantArgs(WindowsGrantScopes.Unlock));

    /// <summary>Asks for an administrator's approval; the control code (null when refused).</summary>
    public async Task<int?> RequestApprovalAsync(CallerInfo caller, params Operation[] ops)
    {
        var (response, raw) = await CallAsync(caller, ProtocolJson.SerializeRequest(Operation.RequestAdminApproval, new AdminApprovalArgs([.. ops.Select(o => o.ToString())])), advanceSeconds: 7);
        return response.Ok ? ProtocolJson.DeserializeResponse<AdminApprovalData>(raw)!.Data!.Control : null;
    }

    public async Task<AdminApprovalResult> CollectApprovalAsync(CallerInfo caller)
    {
        var (response, raw) = await CallAsync(caller, ProtocolJson.SerializeRequest(Operation.CollectAdminApproval), advanceSeconds: 7);
        Assert.True(response.Ok, response.Code);
        return ProtocolJson.DeserializeResponse<AdminApprovalResult>(raw)!.Data!;
    }

    /// <summary>The whole UAC round: ask, an administrator sends the control (the SCM, here direct), pick up the grant.</summary>
    public async Task<string?> AdminGrantAsync(CallerInfo caller, params Operation[] ops)
    {
        var control = await RequestApprovalAsync(caller, ops);
        Assert.NotNull(control);
        Assert.True(Approvals.Approve(control!.Value));
        return (await CollectApprovalAsync(caller)).Grant?.Grant;
    }

    public async Task<Response> ReleaseAsync(CallerInfo caller, string? grant) =>
        (await CallAsync(caller, ProtocolJson.SerializeRequest(Operation.ReleaseLockPolicies, new ReleaseLockPoliciesArgs(["lock"]), grant))).Response;

    public async Task<Response> AutoLogonAsync(CallerInfo caller, string? grant) =>
        (await CallAsync(caller, ProtocolJson.SerializeRequest(Operation.SetAutoLogon, new SetAutoLogonArgs("pw"), grant))).Response;

    /// <summary>The caller in the Windows lock mode with a real unlock of its session, and the grant for it.</summary>
    public async Task<string> WindowsUnlockAsync(CallerInfo caller)
    {
        Assert.True((await ModeAsync(caller, LockModes.Windows)).Ok);
        Sessions.OnChange(SessionChange.Unlock, caller.TokenSessionId);
        var (response, grant) = await UnlockGrantAsync(caller);
        Assert.True(response.Ok, response.Code);
        return grant!.Grant;
    }

    public void Dispose() => _dir.Dispose();
}

/// <summary>P9 end to end through the dispatcher: codes, lock modes, grants and how they open operations (with the review's H1–M4).</summary>
public class CodeOperationsTests
{
    private static CallerInfo User => Callers.User();

    private static readonly CallerInfo Other = Callers.User(Callers.OtherSid) with { PipeSessionId = 2, TokenSessionId = 2, LogonId = 99 };

    // ---- a locked session (finding B2 of the 0.4.1 acceptance test) ------------------------------------------

    [Fact]
    public async Task A_right_code_gives_no_grant_while_the_session_is_locked_but_the_try_counts()
    {
        using var rig = new CodeRig();
        Assert.True((await rig.EnrollAsync(User, Codes.HashA)).Ok);
        rig.LockedSessions.Add(User.TokenSessionId);

        // The right code: refused, no grant, nothing told about the code.
        var (response, _) = await rig.CallAsync(User, ProtocolJson.SerializeRequest(Operation.VerifyCode, new VerifyCodeArgs(CodeKinds.Couch, Codes.A, null)), advanceSeconds: 13);
        Assert.Equal(ErrorCodes.Denied, response.Code);
        Assert.Equal(0, rig.Grants.Count);

        // Every refused try counts: after the free ones the code is locked out, also for the right code once the session is unlocked.
        for (var i = 1; i < CodeStore.FreeFailures; i++)
        {
            await rig.CallAsync(User, ProtocolJson.SerializeRequest(Operation.VerifyCode, new VerifyCodeArgs(CodeKinds.Couch, Codes.A, null)), advanceSeconds: 13);
        }
        rig.LockedSessions.Clear();
        var after = await rig.VerifyAsync(User, Codes.A);
        Assert.Equal(CodeCheckResults.LockedOut, after.Result);
        Assert.Null(after.Grant);
    }

    [Fact]
    public async Task A_session_whose_lock_state_is_unknown_gets_no_grant_and_the_try_does_not_count()
    {
        // Review N-1: refused (fail-closed), but not counted: Windows not answering is no wrong code.
        using var rig = new CodeRig();
        Assert.True((await rig.EnrollAsync(User, Codes.HashA)).Ok);
        rig.UnknownSessions.Add(User.TokenSessionId);
        for (var i = 0; i < CodeStore.FreeFailures + 2; i++)
        {
            var (response, _) = await rig.CallAsync(User, ProtocolJson.SerializeRequest(Operation.VerifyCode, new VerifyCodeArgs(CodeKinds.Couch, Codes.A, null)), advanceSeconds: 13);
            Assert.Equal(ErrorCodes.Denied, response.Code);
        }
        Assert.Equal(0, rig.Grants.Count);
        rig.UnknownSessions.Clear();
        Assert.NotNull(await rig.GrantAsync(User, Codes.A));
    }

    [Fact]
    public async Task A_locked_session_does_not_use_up_its_Windows_unlock_or_its_administrators_approval()
    {
        // Review N-2: refused before the proof is taken, so it is still there after the unlock.
        using var rig = new CodeRig();
        Assert.True((await rig.ModeAsync(User, LockModes.Windows)).Ok);
        rig.Sessions.OnChange(SessionChange.Unlock, User.TokenSessionId);
        var control = await rig.RequestApprovalAsync(User, Operation.SetAutoLogon);
        Assert.True(rig.Approvals.Approve(control!.Value));

        rig.LockedSessions.Add(User.TokenSessionId);
        Assert.Equal(ErrorCodes.Denied, (await rig.UnlockGrantAsync(User)).Response.Code);
        var (collect, _) = await rig.CallAsync(User, ProtocolJson.SerializeRequest(Operation.CollectAdminApproval), advanceSeconds: 7);
        Assert.Equal(ErrorCodes.Denied, collect.Code);

        rig.LockedSessions.Clear();
        Assert.True((await rig.UnlockGrantAsync(User)).Response.Ok);
        Assert.NotNull((await rig.CollectApprovalAsync(User)).Grant);
    }

    [Fact]
    public async Task Once_the_session_is_unlocked_the_right_code_gives_its_grant_again()
    {
        using var rig = new CodeRig();
        Assert.True((await rig.EnrollAsync(User, Codes.HashA)).Ok);
        rig.LockedSessions.Add(User.TokenSessionId);
        await rig.CallAsync(User, ProtocolJson.SerializeRequest(Operation.VerifyCode, new VerifyCodeArgs(CodeKinds.Couch, Codes.A, null)), advanceSeconds: 13);
        rig.LockedSessions.Clear();
        Assert.NotNull(await rig.GrantAsync(User, Codes.A));
    }

    // ---- a lock of 0.4.0 (security review H-1 of finding B1, both rounds) -----------------------------------

    [Fact]
    public async Task A_legacy_lock_takes_no_free_first_code_and_no_couch_grant()
    {
        using var rig = new CodeRig();
        rig.Legacy.Add(Callers.UserSid);
        // The free first code at an open couch, and the config's code taken over on trust: not while the lock of 0.4.0 is not confirmed.
        // A code on its own is refused outright (only the lock mode confirms, third review), the code lock needs an administrator.
        Assert.Equal(ErrorCodes.Denied, (await rig.EnrollAsync(User, Codes.HashA)).Code);
        Assert.Equal(ErrorCodes.Denied, (await rig.EnrollAsync(User, Codes.HashA, CodeKinds.Parent)).Code);
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.LockWithCodeAsync(User, Codes.HashA)).Code);
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.LockWithCodeAsync(User, Codes.HashA, CodeKinds.Both)).Code);
        Assert.False(rig.Codes.Has(Callers.UserSid, CodeKind.Couch));
        Assert.False(rig.Codes.Has(Callers.UserSid, CodeKind.Parent));
        Assert.Contains(Callers.UserSid, rig.Legacy);
    }

    [Fact]
    public async Task A_legacy_lock_takes_no_mode_change_without_an_approval_not_even_a_raise()
    {
        // The Windows mode set by the child would let its own sign-in release the lock.
        using var rig = new CodeRig();
        rig.Legacy.Add(Callers.UserSid);
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.ModeAsync(User, LockModes.Windows)).Code);
        Assert.Contains(Callers.UserSid, rig.Legacy);
    }

    [Fact]
    public async Task A_legacy_lock_is_never_confirmed_as_no_lock_not_even_by_an_administrator()
    {
        // Third review: the confirmation keeps a lock (code or Windows); dropping it is the release, not a confirmation. Refused before any
        // grant is asked for, so nobody is sent to UAC for it.
        using var rig = new CodeRig();
        Assert.True((await rig.LockWithCodeAsync(User, Codes.HashA, CodeKinds.Both)).Ok);
        rig.Legacy.Add(Callers.UserSid);
        Assert.Equal(ErrorCodes.Denied, (await rig.ModeAsync(User, LockModes.Off)).Code);
        var admin = await rig.AdminGrantAsync(User, Operation.SetLockMode);
        Assert.Equal(ErrorCodes.Denied, (await rig.ModeAsync(User, LockModes.Off, admin)).Code);
        Assert.Equal(ErrorCodes.Denied, (await rig.ModeAsync(Callers.ElevatedAdmin(), LockModes.Off)).Code);
        Assert.Contains(Callers.UserSid, rig.Legacy);
        Assert.Equal(LockModes.Code, rig.Codes.View(Callers.UserSid).Mode);
        // The administrator's grant was not used up: the real confirmation still works with it.
        Assert.True((await rig.ModeAsync(User, LockModes.Windows, admin)).Ok);
        Assert.DoesNotContain(Callers.UserSid, rig.Legacy);
    }

    [Fact]
    public async Task The_code_lock_of_a_legacy_lock_is_confirmed_only_with_its_code()
    {
        // Confirming the code lock without a code would leave whatever was there: refused even for an administrator.
        using var rig = new CodeRig();
        rig.Legacy.Add(Callers.UserSid);
        Assert.Equal(ErrorCodes.Denied, (await rig.ModeAsync(User, LockModes.Code)).Code);
        Assert.Equal(ErrorCodes.Denied, (await rig.ModeAsync(Callers.ElevatedAdmin(), LockModes.Code)).Code);
        Assert.Contains(Callers.UserSid, rig.Legacy);
    }

    [Fact]
    public async Task Codes_planted_before_the_take_over_release_nothing_with_a_couch_or_a_parents_grant()
    {
        // Review H-1, second round: before the take-over the account was open; the child set the code lock with both codes (a raise from no lock,
        // free) and so had a parents' code of its own. After the take-over neither code's grant counts for anything.
        using var rig = new CodeRig();
        Assert.True((await rig.LockWithCodeAsync(User, Codes.HashA, CodeKinds.Both)).Ok);
        rig.Legacy.Add(Callers.UserSid);

        var couch = await rig.GrantAsync(User, Codes.A);
        var parent = await rig.GrantAsync(User, Codes.A, CodeKinds.Parent, Operation.ReleaseLockPolicies, Operation.SetLockMode, Operation.ChangePasscode, Operation.RemovePasscode);
        Assert.NotNull(couch);
        Assert.NotNull(parent);
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.ReleaseAsync(User, couch)).Code);
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.ReleaseAsync(User, parent)).Code);
        // Nor does the parents' grant confirm the lock (with a code of the child's choice), change or remove a code.
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.LockWithCodeAsync(User, Codes.HashB, grant: parent)).Code);
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.ModeAsync(User, LockModes.Windows, parent)).Code);
        Assert.Equal(ErrorCodes.Denied, (await rig.ChangeAsync(User, Codes.HashB, grant: couch)).Code);
        Assert.Equal(ErrorCodes.Denied, (await rig.RemoveAsync(User, CodeKinds.Parent, grant: parent)).Code);
        Assert.Contains(Callers.UserSid, rig.Legacy);
    }

    [Fact]
    public async Task A_parents_grant_gets_nothing_else_of_a_legacy_lock_either()
    {
        using var rig = new CodeRig();
        Assert.True((await rig.EnrollAsync(User, Codes.HashA, CodeKinds.Parent)).Ok);
        rig.Legacy.Add(Callers.UserSid);
        var parent = await rig.GrantAsync(User, Codes.A, CodeKinds.Parent, Operation.SetAutoLogon);
        Assert.NotNull(parent);
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.AutoLogonAsync(User, parent)).Code);
    }

    [Fact]
    public async Task The_windows_unlock_does_not_release_a_legacy_lock()
    {
        using var rig = new CodeRig();
        var unlock = await rig.WindowsUnlockAsync(User);   // the Windows mode, set before the lock was taken over
        Assert.True((await rig.EnrollAsync(User, Codes.HashA, grant: unlock)).Ok);   // the release needs a grant at all
        rig.Legacy.Add(Callers.UserSid);
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.ReleaseAsync(User, unlock)).Code);
    }

    [Fact]
    public async Task An_administrators_confirmation_sets_exactly_the_code_entered_and_every_older_code_is_void()
    {
        using var rig = new CodeRig();
        Assert.True((await rig.LockWithCodeAsync(User, Codes.HashB, CodeKinds.Both)).Ok);   // planted before the take-over
        rig.Legacy.Add(Callers.UserSid);
        var admin = await rig.AdminGrantAsync(User, Operation.SetLockMode);
        var response = await rig.LockWithCodeAsync(User, Codes.HashA, grant: admin);
        Assert.True(response.Ok, response.Code);
        Assert.DoesNotContain(Callers.UserSid, rig.Legacy);
        Assert.Contains(rig.Log.Lines, l => l.Contains("[legacy] lock of", StringComparison.Ordinal) && l.Contains("confirmed", StringComparison.Ordinal));

        // Only the confirmed code counts now: the planted couch and parents' codes are gone.
        Assert.False(rig.Codes.Has(Callers.UserSid, CodeKind.Parent));
        Assert.Equal(CodeCheckResults.Wrong, (await rig.VerifyAsync(User, Codes.B)).Result);
        Assert.Equal(CodeCheckResults.NoCode, (await rig.VerifyAsync(User, Codes.B, CodeKinds.Parent)).Result);
        Assert.Equal(LockModes.Code, rig.Codes.View(Callers.UserSid).Mode);
        // From now on the confirmed couch code releases as always.
        var couch = await rig.GrantAsync(User, Codes.A);
        Assert.True((await rig.ReleaseAsync(User, couch)).Ok);
    }

    [Fact]
    public async Task An_administrators_confirmation_of_the_windows_mode_drops_every_code()
    {
        using var rig = new CodeRig();
        Assert.True((await rig.LockWithCodeAsync(User, Codes.HashB, CodeKinds.Both)).Ok);
        rig.Legacy.Add(Callers.UserSid);
        var admin = await rig.AdminGrantAsync(User, Operation.SetLockMode);
        Assert.True((await rig.ModeAsync(User, LockModes.Windows, admin)).Ok);
        Assert.DoesNotContain(Callers.UserSid, rig.Legacy);
        Assert.False(rig.Codes.Has(Callers.UserSid, CodeKind.Couch));
        Assert.False(rig.Codes.Has(Callers.UserSid, CodeKind.Parent));
        Assert.Equal(LockModes.Windows, rig.Codes.View(Callers.UserSid).Mode);
        // The Windows sign-in is the lock's proof now.
        rig.Sessions.OnChange(SessionChange.Unlock, User.TokenSessionId);
        var (unlock, grant) = await rig.UnlockGrantAsync(User);
        Assert.True(unlock.Ok, unlock.Code);
        Assert.True((await rig.ReleaseAsync(User, grant!.Grant)).Ok);
    }

    [Fact]
    public async Task A_legacy_lock_is_confirmed_only_through_the_lock_mode_not_with_a_single_code()
    {
        // Third review: a code enrolled, changed or removed on its own would confirm the lock but keep its mode (and the other kind of code)
        // as they were. Refused even with an administrator's approval; the confirmation is SetLockMode, which replaces everything.
        using var rig = new CodeRig();
        Assert.True((await rig.EnrollAsync(User, Codes.HashB, CodeKinds.Parent)).Ok);   // planted before
        rig.Legacy.Add(Callers.UserSid);
        var admin = await rig.AdminGrantAsync(User, Operation.EnrollPasscode, Operation.ChangePasscode, Operation.RemovePasscode);
        Assert.Equal(ErrorCodes.Denied, (await rig.EnrollAsync(User, Codes.HashA, grant: admin)).Code);
        Assert.Equal(ErrorCodes.Denied, (await rig.ChangeAsync(User, Codes.HashA, CodeKinds.Parent, admin)).Code);
        Assert.Equal(ErrorCodes.Denied, (await rig.RemoveAsync(User, CodeKinds.Parent, grant: admin)).Code);
        Assert.Equal(ErrorCodes.Denied, (await rig.EnrollAsync(Callers.ElevatedAdmin(), Codes.HashA)).Code);
        Assert.Contains(Callers.UserSid, rig.Legacy);
        Assert.False(rig.Codes.Has(Callers.UserSid, CodeKind.Couch));
        Assert.True(rig.Codes.Has(Callers.UserSid, CodeKind.Parent));
    }

    [Fact]
    public async Task A_parents_grant_does_not_confirm_a_legacy_lock()
    {
        using var rig = new CodeRig();
        Assert.True((await rig.EnrollAsync(User, Codes.HashA, CodeKinds.Both)).Ok);
        rig.Legacy.Add(Callers.UserSid);
        var parent = await rig.GrantAsync(User, Codes.A, CodeKinds.Parent, Operation.SetLockMode, Operation.ChangePasscode);
        Assert.NotNull(parent);
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.ModeAsync(User, LockModes.Windows, parent)).Code);
        Assert.Equal(ErrorCodes.Denied, (await rig.ModeAsync(User, LockModes.Off, parent)).Code);
        Assert.Equal(ErrorCodes.Denied, (await rig.ChangeAsync(User, Codes.HashB, CodeKinds.Couch, parent)).Code);
        Assert.Contains(Callers.UserSid, rig.Legacy);
    }

    [Fact]
    public async Task An_elevated_administrator_confirms_with_the_code_and_without_a_grant()
    {
        using var rig = new CodeRig();
        rig.Legacy.Add(Callers.UserSid);
        Assert.True((await rig.LockWithCodeAsync(Callers.ElevatedAdmin(), Codes.HashA)).Ok);
        Assert.DoesNotContain(Callers.UserSid, rig.Legacy);
    }

    [Fact]
    public void A_failing_legacy_lookup_counts_as_a_legacy_lock()
    {
        using var dir = new TempDir();
        var clock = new FakeClock();
        var log = new CapturingSink();
        var codes = CodeStore.Open(dir.Path, log, clock, () => 1_000_000, Codes.FastVerify);
        var grants = new GrantStore(clock);
        var handlers = new CodeHandlers(new CodeParts(codes, grants, new SessionEvents(clock, grants, _ => null, log), new AdminApprovals(clock), _ => false, log, null,
            _ => throw new IOException("state")));
        var view = handlers.View(Callers.UserSid);
        Assert.True(view.Legacy);
        Assert.True(view.Protected);
        Assert.Equal(GrantKinds.WindowsAdmin, OperationPolicy.AcceptedGrants(new ValidatedRequest(Operation.ReleaseLockPolicies, null, null), view));
        Assert.Equal(GrantKinds.WindowsAdmin, OperationPolicy.AcceptedGrants(new ValidatedRequest(Operation.SetAutoLogon, null, null), view));
    }

    [Fact]
    public void Forgetting_an_account_drops_both_codes_and_the_mode_and_replacing_sets_exactly_one()
    {
        using var dir = new TempDir();
        var codes = CodeStore.Open(dir.Path, new CapturingSink(), new FakeClock(), () => 1_000_000, Codes.FastVerify);
        Assert.Equal(CodeSet.Saved, codes.SetMode(Callers.UserSid, LockModes.Code, kinds: [CodeKind.Couch, CodeKind.Parent], hash: Codes.HashB));
        Assert.Equal(CodeSet.Saved, codes.Replace(Callers.UserSid, null));
        Assert.False(codes.Has(Callers.UserSid, CodeKind.Couch));
        Assert.False(codes.Has(Callers.UserSid, CodeKind.Parent));
        Assert.Null(codes.StoredMode(Callers.UserSid));
        Assert.Equal(CodeSet.Saved, codes.Replace(Callers.UserSid, null));   // nothing there: done as well

        Assert.Equal(CodeSet.Saved, codes.SetMode(Callers.UserSid, LockModes.Code, kinds: [CodeKind.Couch, CodeKind.Parent], hash: Codes.HashB));
        Assert.Equal(CodeSet.Saved, codes.Replace(Callers.UserSid, LockModes.Code, [CodeKind.Couch], Codes.HashA));
        Assert.True(codes.HasApproved(Callers.UserSid, CodeKind.Couch));
        Assert.False(codes.Has(Callers.UserSid, CodeKind.Parent));
        Assert.Equal(CodeSet.BadHash, codes.Replace(Callers.UserSid, LockModes.Code, [CodeKind.Couch], "nope"));
    }

    // ---- enrollment, change, removal ----------------------------------------------------------------------

    [Fact]
    public async Task Without_a_lock_releases_are_open_and_after_enrollment_they_need_the_code()
    {
        using var rig = new CodeRig();
        Assert.True((await rig.ReleaseAsync(User, null)).Ok);
        Assert.True((await rig.EnrollAsync(User, Codes.HashA)).Ok);
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.ReleaseAsync(User, null)).Code);
        var grant = await rig.GrantAsync(User, Codes.A);
        Assert.True((await rig.ReleaseAsync(User, grant)).Ok);
        Assert.True((await rig.ReleaseAsync(User, grant)).Ok);
    }

    [Fact]
    public async Task The_first_enrollment_is_once_and_never_over_an_existing_code()
    {
        using var rig = new CodeRig();
        Assert.True((await rig.EnrollAsync(User, Codes.HashA)).Ok);
        Assert.Equal(ErrorCodes.Denied, (await rig.EnrollAsync(User, Codes.HashB)).Code);
        Assert.Equal(ErrorCodes.Denied, (await rig.EnrollAsync(User, Codes.HashB, grant: await rig.GrantAsync(User, Codes.A))).Code);
        Assert.True((await rig.EnrollAsync(Other, Codes.HashB)).Ok);
        Assert.Equal(CodeCheckResults.Granted, (await rig.VerifyAsync(User, Codes.A)).Result);
    }

    [Fact]
    public async Task Changing_the_couch_code_takes_its_own_grant_but_never_the_windows_unlock()
    {
        using var rig = new CodeRig();
        Assert.True((await rig.EnrollAsync(User, Codes.HashA)).Ok);
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.ChangeAsync(User, Codes.HashB)).Code);
        Assert.True((await rig.ChangeAsync(User, Codes.HashB, grant: await rig.GrantAsync(User, Codes.A))).Ok);
        Assert.Equal(CodeCheckResults.Granted, (await rig.VerifyAsync(User, Codes.B)).Result);
        // In the Windows mode (the couch code went with the switch) the unlock adds a couch code, but never changes or removes one.
        var other = await rig.WindowsUnlockAsync(Other);
        Assert.True((await rig.EnrollAsync(Other, Codes.HashA, grant: other)).Ok);
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.ChangeAsync(Other, Codes.HashB, grant: other)).Code);
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.RemoveAsync(Other, grant: other)).Code);
    }

    [Fact]
    public async Task A_first_parents_code_next_to_the_couch_code_needs_a_parent_or_an_administrator_not_the_couch_code()
    {
        using var rig = new CodeRig();
        Assert.True((await rig.EnrollAsync(User, Codes.HashA)).Ok);
        var couch = await rig.GrantAsync(User, Codes.A);
        // Review M1: the couch grant is not enough for a parents' code.
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.EnrollAsync(User, Codes.HashB, CodeKinds.Parent, couch)).Code);
        var admin = await rig.AdminGrantAsync(User, Operation.EnrollPasscode);
        Assert.True((await rig.EnrollAsync(User, Codes.HashB, CodeKinds.Parent, admin)).Ok);
        // With the administrator's approval the couch code next to it (perhaps planted) went (review M4).
        Assert.False(rig.Codes.Has(Callers.UserSid, CodeKind.Couch));
    }

    [Fact]
    public void There_is_no_parents_code_from_the_couch_codes_buttons_any_more()
    {
        // Review M-b: a couch code alone never makes a parents' code (the old "sequence" path is refused by the validator).
        var json = """{"v":1,"op":"EnrollPasscode","args":{"kind":"parent","sequence":["a","b","x","y","up","down"]}}""";
        Assert.False(new RequestValidator(_ => false).Validate(Encoding.UTF8.GetBytes(json)).Ok);
    }

    [Fact]
    public async Task Both_codes_at_once_and_a_change_of_both_needs_the_parents()
    {
        using var rig = new CodeRig();
        Assert.True((await rig.EnrollAsync(User, Codes.HashA, CodeKinds.Both)).Ok);
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.ChangeAsync(User, Codes.HashB, CodeKinds.Both, await rig.GrantAsync(User, Codes.A))).Code);
        var parent = await rig.GrantAsync(User, Codes.A, CodeKinds.Parent, Operation.ChangePasscode);
        Assert.True((await rig.ChangeAsync(User, Codes.HashB, CodeKinds.Both, parent)).Ok);
        Assert.Equal(CodeCheckResults.Granted, (await rig.VerifyAsync(User, Codes.B)).Result);
        Assert.Equal(CodeCheckResults.Matched, (await rig.VerifyAsync(User, Codes.B, CodeKinds.Parent)).Result);
    }

    [Fact]
    public async Task A_code_reset_with_the_parents_approval_clears_the_other_kind()
    {
        using var rig = new CodeRig();
        Assert.True((await rig.EnrollAsync(User, Codes.HashA, CodeKinds.Both)).Ok);
        // The couch code alone, changed with the parents' approval: the parents' code (perhaps planted) goes (review M4).
        var parent = await rig.GrantAsync(User, Codes.A, CodeKinds.Parent, Operation.ChangePasscode);
        Assert.True((await rig.ChangeAsync(User, Codes.HashB, CodeKinds.Couch, parent)).Ok);
        Assert.False(rig.Codes.Has(Callers.UserSid, CodeKind.Parent));
        // With the couch code's own grant nothing else is touched.
        Assert.True((await rig.ChangeAsync(User, Codes.HashA, CodeKinds.Couch, await rig.GrantAsync(User, Codes.B))).Ok);
        Assert.False(rig.Codes.Has(Callers.UserSid, CodeKind.Parent));
        Assert.True(rig.Codes.Has(Callers.UserSid, CodeKind.Couch));
    }

    [Fact]
    public async Task Removing_the_couch_code_needs_its_grant_and_opens_the_account_again()
    {
        using var rig = new CodeRig();
        Assert.True((await rig.EnrollAsync(User, Codes.HashA)).Ok);
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.RemoveAsync(User)).Code);
        Assert.True((await rig.RemoveAsync(User, grant: await rig.GrantAsync(User, Codes.A))).Ok);
        Assert.Equal(CodeCheckResults.NoCode, (await rig.VerifyAsync(User, Codes.A)).Result);
        Assert.True((await rig.EnrollAsync(User, Codes.HashB)).Ok);
        Assert.Equal(ErrorCodes.Denied, (await rig.RemoveAsync(Callers.ElevatedAdmin(), CodeKinds.Parent)).Code);
    }

    [Fact]
    public async Task With_a_parents_code_the_couch_code_goes_only_with_the_parents_approval()
    {
        using var rig = new CodeRig();
        Assert.True((await rig.EnrollAsync(User, Codes.HashA, CodeKinds.Both)).Ok);
        var couch = await rig.GrantAsync(User, Codes.A);
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.RemoveAsync(User, grant: couch)).Code);
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.RemoveAsync(User, CodeKinds.Parent, couch)).Code);
        // An approval for another operation does not count either, and is not used up.
        var forAutoLogon = await rig.GrantAsync(User, Codes.A, CodeKinds.Parent, Operation.SetAutoLogon);
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.RemoveAsync(User, CodeKinds.Parent, forAutoLogon)).Code);
        var parent = await rig.GrantAsync(User, Codes.A, CodeKinds.Parent, Operation.RemovePasscode, Operation.RemovePasscode);
        Assert.True((await rig.RemoveAsync(User, grant: parent)).Ok);
        Assert.True((await rig.RemoveAsync(User, CodeKinds.Parent, parent)).Ok);
        Assert.False(rig.Codes.IsEnrolled(Callers.UserSid));
        Assert.True((await rig.AutoLogonAsync(User, forAutoLogon)).Ok);
    }

    [Fact]
    public async Task An_elevated_administrator_removes_any_code()
    {
        using var rig = new CodeRig();
        Assert.True((await rig.EnrollAsync(User, Codes.HashA, CodeKinds.Both)).Ok);
        Assert.True((await rig.RemoveAsync(Callers.ElevatedAdmin())).Ok);
        Assert.True((await rig.RemoveAsync(Callers.ElevatedAdmin(), CodeKinds.Parent)).Ok);
    }

    [Fact]
    public async Task Change_of_a_code_that_does_not_exist_is_refused()
    {
        using var rig = new CodeRig();
        Assert.Equal(ErrorCodes.Denied, (await rig.ChangeAsync(Callers.ElevatedAdmin(), Codes.HashA)).Code);
    }

    // ---- the lock mode (review H1) ----------------------------------------------------------------------------

    [Fact]
    public async Task Raising_the_lock_mode_is_free_lowering_needs_the_modes_own_proof()
    {
        using var rig = new CodeRig();
        // The shell's order: the first code while the lock is off (free), then the mode up.
        Assert.True((await rig.LockWithCodeAsync(User, Codes.HashA)).Ok);
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.ModeAsync(User, LockModes.Off)).Code);
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.ModeAsync(User, LockModes.Windows)).Code);
        Assert.True((await rig.ModeAsync(User, LockModes.Windows, await rig.GrantAsync(User, Codes.A))).Ok);
        // To the Windows lock the couch code went in the same step (review M-a).
        Assert.False(rig.Codes.Has(Callers.UserSid, CodeKind.Couch));
        // In the Windows mode its own unlock lowers it; a couch code would not.
        rig.Sessions.OnChange(SessionChange.Unlock, 1);
        var (_, unlock) = await rig.UnlockGrantAsync(User);
        Assert.True((await rig.ModeAsync(User, LockModes.Off, unlock!.Grant)).Ok);
        Assert.Equal(LockModes.Off, rig.Codes.View(Callers.UserSid).Mode);
    }

    [Fact]
    public async Task A_code_that_came_first_counts_as_the_code_lock()
    {
        using var rig = new CodeRig();
        // An approved code (here an administrator's) that came before any mode: it counts as the code lock; a freely enrolled one would not.
        Assert.True((await rig.EnrollAsync(Callers.ElevatedAdmin(), Codes.HashA)).Ok);
        // The mode was never set: "off" would lower the code lock.
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.ModeAsync(User, LockModes.Off)).Code);
        Assert.True((await rig.ModeAsync(Callers.ElevatedAdmin(), LockModes.Off)).Ok);
    }

    [Fact]
    public async Task With_a_parents_code_only_the_parents_lower_the_lock()
    {
        using var rig = new CodeRig();
        Assert.True((await rig.LockWithCodeAsync(User, Codes.HashA, CodeKinds.Both)).Ok);
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.ModeAsync(User, LockModes.Windows, await rig.GrantAsync(User, Codes.A))).Code);
        Assert.True((await rig.ModeAsync(User, LockModes.Windows, await rig.GrantAsync(User, Codes.A, CodeKinds.Parent, Operation.SetLockMode))).Ok);
    }

    [Fact]
    public async Task No_windows_unlock_grant_outside_the_windows_mode()
    {
        using var rig = new CodeRig();
        rig.Sessions.OnChange(SessionChange.Unlock, 1);
        Assert.Equal(ErrorCodes.Denied, (await rig.UnlockGrantAsync(User)).Response.Code);
        Assert.True((await rig.EnrollAsync(User, Codes.HashA)).Ok);
        Assert.Equal(ErrorCodes.Denied, (await rig.UnlockGrantAsync(User)).Response.Code);
        // The refused request did not use the unlock up.
        Assert.True((await rig.ModeAsync(Callers.ElevatedAdmin(), LockModes.Windows)).Ok);
        Assert.True((await rig.UnlockGrantAsync(User)).Response.Ok);
    }

    [Fact]
    public async Task A_windows_unlock_grant_counts_only_in_the_windows_mode_never_while_the_code_is_the_lock()
    {
        using var rig = new CodeRig();
        var unlock = await rig.WindowsUnlockAsync(User);
        // The Windows lock without a code protects the couch: a release needs the unlock.
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.ReleaseAsync(User, null)).Code);
        Assert.True((await rig.ReleaseAsync(User, unlock)).Ok);
        // A couch code set in the Windows mode (with the unlock), then the lock becomes the code: that unlock no longer stands in for it.
        Assert.True((await rig.EnrollAsync(User, Codes.HashA, grant: unlock)).Ok);
        Assert.True((await rig.ModeAsync(User, LockModes.Code)).Ok);
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.ReleaseAsync(User, unlock)).Code);
        // And never for a parent-only operation.
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.AutoLogonAsync(User, unlock)).Code);
    }

    [Fact]
    public async Task In_the_windows_mode_a_couch_grant_counts_for_nothing()
    {
        using var rig = new CodeRig();
        Assert.True((await rig.EnrollAsync(User, Codes.HashA)).Ok);
        var couch = await rig.GrantAsync(User, Codes.A);
        // An administrator switches the lock to Windows (the couch code goes with it); an old couch grant opens nothing there.
        Assert.True((await rig.ModeAsync(Callers.ElevatedAdmin(), LockModes.Windows)).Ok);
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.ReleaseAsync(User, couch)).Code);
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.ModeAsync(User, LockModes.Off, couch)).Code);
    }

    [Fact]
    public async Task No_windows_unlock_grant_for_an_account_without_a_password()
    {
        using var rig = new CodeRig();
        rig.BlankPasswords.Add(Callers.UserSid);
        Assert.True((await rig.ModeAsync(User, LockModes.Windows)).Ok);
        rig.Sessions.OnChange(SessionChange.Unlock, 1);
        Assert.Equal(ErrorCodes.Denied, (await rig.UnlockGrantAsync(User)).Response.Code);
    }

    [Fact]
    public async Task A_sign_in_gives_no_grant_even_in_the_windows_mode()
    {
        using var rig = new CodeRig();
        Assert.True((await rig.ModeAsync(User, LockModes.Windows)).Ok);
        rig.Sessions.OnChange(SessionChange.Logon, 1);
        Assert.Equal(ErrorCodes.NotFound, (await rig.UnlockGrantAsync(User)).Response.Code);
    }

    [Fact]
    public void The_accepted_kinds_are_explicit_and_follow_the_mode()
    {
        var release = new ValidatedRequest(Operation.ReleaseLockPolicies, null, new ReleaseLockPoliciesArgs(["lock"]));
        var code = new LockView(LockModes.Code, true, false);
        var windows = new LockView(LockModes.Windows, false, false);
        Assert.Equal(GrantKinds.Couch | GrantKinds.ParentOrAdmin, OperationPolicy.AcceptedGrants(release, code));
        Assert.Equal(GrantKinds.WindowsUnlock | GrantKinds.ParentOrAdmin, OperationPolicy.AcceptedGrants(release, windows));
        Assert.Equal(GrantKinds.Couch | GrantKinds.ParentOrAdmin, OperationPolicy.AcceptedGrants(release, LockView.Closed));
        // No request accepts "any kind" in a code or unknown mode, and no code operation ever takes the Windows unlock.
        foreach (var op in Enum.GetValues<Operation>())
        {
            var request = new ValidatedRequest(op, null, op switch
            {
                Operation.ChangePasscode or Operation.EnrollPasscode => new PasscodeHashArgs(Codes.HashA, CodeKinds.Couch),
                Operation.RemovePasscode => new CodeKindArgs(CodeKinds.Couch),
                Operation.SetLockMode => new LockModeArgs(LockModes.Off),
                _ => null
            });
            Assert.Equal(GrantKinds.None, OperationPolicy.AcceptedGrants(request, code) & GrantKinds.WindowsUnlock);
            // In the Windows mode a couch grant counts for nothing (review M-a), and the unlock never changes or removes a code.
            Assert.Equal(GrantKinds.None, OperationPolicy.AcceptedGrants(request, windows) & GrantKinds.Couch);
            if (op is Operation.ChangePasscode or Operation.RemovePasscode or Operation.SetAutoLogon)
                Assert.Equal(GrantKinds.None, OperationPolicy.AcceptedGrants(request, windows) & GrantKinds.WindowsUnlock);
        }
    }

    [Fact]
    public async Task When_the_lock_cannot_be_read_the_windows_unlock_counts_nowhere()
    {
        var harness = new Harness(grants: new FakeGrants(Grant32, GrantKinds.WindowsUnlock), codeEnrolled: true,
            extraHandlers: h => h[Operation.ReleaseLockPolicies] = Harness.Ok,
            extraChecks: c => c.Register(StateCheck.GroupsSetByService, Harness.AllowAll),
            lockViews: _ => throw new InvalidOperationException());
        var request = ProtocolJson.SerializeRequest(Operation.ReleaseLockPolicies, new ReleaseLockPoliciesArgs(["lock"]), Grant32);
        Assert.Equal(ErrorCodes.NeedGrant, (await harness.OneAsync(User, request)).Code);
    }

    private static readonly string Grant32 = Convert.ToBase64String(new byte[32]);

    // ---- VerifyCode and the parents' grants (review M3) ---------------------------------------------------------

    [Fact]
    public async Task A_parents_code_without_operations_is_only_checked_with_them_it_counts_once_for_those()
    {
        using var rig = new CodeRig();
        Assert.True((await rig.EnrollAsync(User, Codes.HashA, CodeKinds.Both)).Ok);
        var matched = await rig.VerifyAsync(User, Codes.A, CodeKinds.Parent);
        Assert.Equal(CodeCheckResults.Matched, matched.Result);
        Assert.Null(matched.Grant);
        var parent = await rig.VerifyAsync(User, Codes.A, CodeKinds.Parent, Operation.SetAutoLogon);
        Assert.Equal(GrantKindNames.Parent, parent.Grant!.Kind);
        Assert.Equal(["SetAutoLogon"], parent.Grant.Ops);
        Assert.Equal(30, parent.Grant.ExpiresInSeconds);
        Assert.True((await rig.AutoLogonAsync(User, parent.Grant.Grant)).Ok);
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.AutoLogonAsync(User, parent.Grant.Grant)).Code);
        // The couch code is no parent's approval.
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.AutoLogonAsync(User, await rig.GrantAsync(User, Codes.A))).Code);
    }

    [Fact]
    public async Task No_parents_code_no_parent_grant()
    {
        using var rig = new CodeRig();
        Assert.True((await rig.EnrollAsync(User, Codes.HashA)).Ok);
        var answer = await rig.VerifyAsync(User, Codes.A, CodeKinds.Parent, Operation.SetAutoLogon);
        Assert.Equal(CodeCheckResults.NoCode, answer.Result);
        Assert.Null(answer.Grant);
    }

    [Fact]
    public async Task Wrong_codes_lock_out_with_a_growing_wait_and_the_two_codes_count_apart()
    {
        using var rig = new CodeRig();
        // Two different codes, set directly (through the operations an approval would clear the other kind, review M4).
        rig.Codes.Set(Callers.UserSid, CodeKind.Couch, Codes.HashA, enroll: true);
        rig.Codes.Set(Callers.UserSid, CodeKind.Parent, Codes.HashB, enroll: true);
        for (var i = 1; i < CodeStore.FreeFailures; i++) Assert.Equal(0, (await rig.VerifyAsync(User, Codes.Wrong, CodeKinds.Parent)).WaitSeconds);
        var fifth = await rig.VerifyAsync(User, Codes.Wrong, CodeKinds.Parent);
        Assert.Equal(CodeCheckResults.Wrong, fifth.Result);
        Assert.InRange(fifth.WaitSeconds, 1, 30);
        Assert.Equal(CodeCheckResults.LockedOut, (await rig.VerifyAsync(User, Codes.B, CodeKinds.Parent)).Result);
        // A child hammering the parents' code does not shut the couch.
        Assert.Equal(CodeCheckResults.Granted, (await rig.VerifyAsync(User, Codes.A)).Result);
        rig.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(CodeCheckResults.Matched, (await rig.VerifyAsync(User, Codes.B, CodeKinds.Parent)).Result);
    }

    [Fact]
    public void The_wait_doubles_and_is_capped()
    {
        Assert.Equal(TimeSpan.Zero, CodeStore.LockFor(CodeStore.FreeFailures - 1));
        Assert.Equal(TimeSpan.FromSeconds(30), CodeStore.LockFor(CodeStore.FreeFailures));
        Assert.Equal(TimeSpan.FromSeconds(60), CodeStore.LockFor(CodeStore.FreeFailures + 1));
        Assert.Equal(CodeStore.MaxLock, CodeStore.LockFor(CodeStore.FreeFailures + 50));
    }

    [Fact]
    public async Task Every_grant_of_the_session_ends_with_a_lock()
    {
        using var rig = new CodeRig();
        Assert.True((await rig.EnrollAsync(User, Codes.HashA)).Ok);
        var grant = await rig.GrantAsync(User, Codes.A);
        rig.Sessions.OnChange(SessionChange.Lock, 1);
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.ReleaseAsync(User, grant)).Code);
    }

    [Fact]
    public async Task A_grant_of_another_account_or_logon_is_refused()
    {
        using var rig = new CodeRig();
        Assert.True((await rig.EnrollAsync(User, Codes.HashA)).Ok);
        Assert.True((await rig.EnrollAsync(Other, Codes.HashB)).Ok);
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.ReleaseAsync(User, await rig.GrantAsync(Other, Codes.B))).Code);
        var mine = await rig.GrantAsync(User, Codes.A);
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.ReleaseAsync(User with { LogonId = 5 }, mine)).Code);
    }

    // ---- the administrator's credential -----------------------------------------------------------------------------

    [Fact]
    public async Task An_administrators_approval_through_uac_gives_the_caller_a_single_use_grant_for_the_named_operations()
    {
        using var rig = new CodeRig();
        Assert.True((await rig.EnrollAsync(User, Codes.HashA)).Ok);
        var control = await rig.RequestApprovalAsync(User, Operation.SetAutoLogon);
        Assert.InRange(control!.Value, AdminApprovals.FirstControl, AdminApprovals.LastControl);
        Assert.Equal("pending", (await rig.CollectApprovalAsync(User)).State);
        // The control comes only from an administrator through the SCM (here: direct).
        Assert.True(rig.Approvals.Approve(control.Value));
        // Another account or another sign-in in the session gets nothing, and does not take it away.
        Assert.Equal("none", (await rig.CollectApprovalAsync(User with { LogonId = 5 })).State);
        var result = await rig.CollectApprovalAsync(User);
        Assert.Equal("approved", result.State);
        Assert.Equal(GrantKindNames.WindowsAdmin, result.Grant!.Kind);
        Assert.Equal(["SetAutoLogon"], result.Grant.Ops);
        // Once: picked up, it is gone.
        Assert.Equal("none", (await rig.CollectApprovalAsync(User)).State);
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.ReleaseAsync(User, result.Grant.Grant)).Code);
        Assert.True((await rig.AutoLogonAsync(User, result.Grant.Grant)).Ok);
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.AutoLogonAsync(User, result.Grant.Grant)).Code);
        Assert.DoesNotContain(result.Grant.Grant, rig.Log.All + rig.Harness.Log.All);
    }

    [Fact]
    public async Task A_confirmation_only_gives_no_grant()
    {
        using var rig = new CodeRig();
        var control = await rig.RequestApprovalAsync(User);
        Assert.True(rig.Approvals.Approve(control!.Value));
        var result = await rig.CollectApprovalAsync(User);
        Assert.Equal("approved", result.State);
        Assert.Null(result.Grant);
    }

    [Fact]
    public async Task One_open_approval_per_session_used_once_and_never_the_same_code_again()
    {
        using var rig = new CodeRig();
        var first = await rig.RequestApprovalAsync(User, Operation.SetAutoLogon);
        // Open: a second request of the session is refused (nobody swaps the operations after the HUD's notice).
        Assert.Null(await rig.RequestApprovalAsync(User, Operation.SetLockMode));
        Assert.True(rig.Approvals.Approve(first!.Value));
        Assert.False(rig.Approvals.Approve(first.Value));
        Assert.Null(await rig.RequestApprovalAsync(User, Operation.SetLockMode));
        Assert.Equal(["SetAutoLogon"], (await rig.CollectApprovalAsync(User)).Grant!.Ops);
        // Picked up: the next request may come, and never with the code of the last one (an old prompt approves nothing new).
        for (var i = 0; i < 20; i++)
        {
            var next = await rig.RequestApprovalAsync(User, Operation.SetLockMode);
            Assert.NotEqual(first, next);
            rig.Clock.Advance(AdminApprovals.Lifetime);
            Assert.Equal("none", (await rig.CollectApprovalAsync(User)).State);
            first = next;
        }
    }

    [Fact]
    public void A_replaced_code_is_never_given_out_again_even_by_chance()
    {
        // A random source that always proposes the same code first: the session's last one is skipped.
        var codes = new Queue<int>([200, 200, 201]);
        var approvals = new AdminApprovals(new FakeClock(), () => codes.Count > 0 ? codes.Dequeue() : 202);
        Assert.Equal(200, approvals.Register(User, [Operation.SetAutoLogon]));
        approvals.RevokeSession(1);
        Assert.Equal(201, approvals.Register(User, [Operation.SetAutoLogon]));
    }

    [Fact]
    public void Controls_are_ignored_when_the_services_access_list_is_not_trusted()
    {
        var approvals = new AdminApprovals(new FakeClock());
        var control = approvals.Register(User, [Operation.SetAutoLogon]);
        approvals.ControlsTrusted = false;
        Assert.False(approvals.Approve(control!.Value));
    }

    [Fact]
    public void Approvals_and_their_pickup_have_their_own_budgets()
    {
        Assert.Equal(RateBucket.Approvals, RateLimiter.BucketFor(Operation.RequestAdminApproval));
        Assert.Equal(RateBucket.ApprovalPickup, RateLimiter.BucketFor(Operation.CollectAdminApproval));
        Assert.Equal(RateBucket.Grants, RateLimiter.BucketFor(Operation.RequestWindowsGrant));
        var limiter = new RateLimiter(new FakeClock());
        // The shell polls the pickup for a few seconds after the prompt, several times in a row: well inside its budget.
        for (var i = 0; i < 30; i++) Assert.Equal(RateVerdict.Allow, limiter.TryAcquire(Callers.UserSid, Operation.CollectAdminApproval).Verdict);
        for (var i = 0; i < 10; i++) limiter.TryAcquire(Callers.UserSid, Operation.RequestWindowsGrant);
        Assert.Equal(RateVerdict.Allow, limiter.TryAcquire(Callers.UserSid, Operation.RequestAdminApproval).Verdict);
    }

    // ---- codes enrolled at an open couch (review 3, item 2) --------------------------------------------------------

    [Fact]
    public async Task Codes_enrolled_at_an_open_couch_do_not_survive_a_lock_switched_on()
    {
        using var rig = new CodeRig();
        // A child plants both codes while no lock is on.
        Assert.True((await rig.EnrollAsync(User, Codes.HashB, CodeKinds.Both)).Ok);
        // The parents switch the Windows lock on: the planted codes go.
        Assert.True((await rig.ModeAsync(User, LockModes.Windows)).Ok);
        Assert.False(rig.Codes.Has(Callers.UserSid, CodeKind.Parent));
        Assert.False(rig.Codes.Has(Callers.UserSid, CodeKind.Couch));
    }

    [Fact]
    public async Task The_code_set_with_the_switch_stays_and_replaces_a_planted_one()
    {
        using var rig = new CodeRig();
        Assert.True((await rig.EnrollAsync(User, Codes.HashB, CodeKinds.Parent)).Ok);
        Assert.True((await rig.LockWithCodeAsync(User, Codes.HashA)).Ok);
        Assert.False(rig.Codes.Has(Callers.UserSid, CodeKind.Parent));
        Assert.Equal(CodeCheckResults.Granted, (await rig.VerifyAsync(User, Codes.A)).Result);
        // A code comes along only from no lock: with the lock on it is refused.
        Assert.Equal(ErrorCodes.Denied, (await rig.LockWithCodeAsync(User, Codes.HashB)).Code);
    }

    [Fact]
    public async Task An_approved_code_survives_the_switch_and_cannot_be_replaced_by_it()
    {
        using var rig = new CodeRig();
        Assert.True((await rig.EnrollAsync(Callers.ElevatedAdmin(), Codes.HashA)).Ok);
        Assert.Equal(ErrorCodes.Denied, (await rig.LockWithCodeAsync(User, Codes.HashB)).Code);
        Assert.True((await rig.ModeAsync(User, LockModes.Code)).Ok);
        Assert.True(rig.Codes.Has(Callers.UserSid, CodeKind.Couch));
    }

    [Theory]
    [InlineData("""{"v":1,"op":"SetLockMode","args":{"mode":"windows","hash":"pbkdf2-sha256$100000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=","kind":"couch"}}""")]
    [InlineData("""{"v":1,"op":"SetLockMode","args":{"mode":"code","hash":"pbkdf2-sha256$100000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="}}""")]
    [InlineData("""{"v":1,"op":"SetLockMode","args":{"mode":"code","hash":"x","kind":"couch"}}""")]
    [InlineData("""{"v":1,"op":"SetLockMode","args":{"mode":"code","hash":"pbkdf2-sha256$100000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=","kind":"parent"}}""")]
    public void A_code_comes_along_only_with_the_code_lock(string json) =>
        Assert.False(new RequestValidator(_ => false).Validate(Encoding.UTF8.GetBytes(json)).Ok);

    [Fact]
    public async Task A_lock_ends_a_pending_approval()
    {
        using var rig = new CodeRig();
        var control = await rig.RequestApprovalAsync(User, Operation.SetAutoLogon);
        rig.Approvals.RevokeSession(1);
        Assert.False(rig.Approvals.Approve(control!.Value));
    }

    [Fact]
    public async Task Approvals_of_two_sessions_have_their_own_codes()
    {
        using var rig = new CodeRig();
        var mine = await rig.RequestApprovalAsync(User, Operation.SetAutoLogon);
        var theirs = await rig.RequestApprovalAsync(Other, Operation.SetLockMode);
        Assert.NotEqual(mine, theirs);
        Assert.True(rig.Approvals.Approve(theirs!.Value));
        Assert.Equal("pending", (await rig.CollectApprovalAsync(User)).State);
        Assert.Equal("approved", (await rig.CollectApprovalAsync(Other)).State);
    }

    [Theory]
    [InlineData("""{"v":1,"op":"RequestAdminApproval","args":{"ops":["Hello"]}}""")]
    [InlineData("""{"v":1,"op":"RequestAdminApproval","args":{}}""")]
    [InlineData("""{"v":1,"op":"RequestWindowsGrant","args":{"scope":"admin","name":"Admin","password":"eA==","ops":["SetAutoLogon"]}}""")]
    [InlineData("""{"v":1,"op":"CollectAdminApproval","args":{"grant":"x"}}""")]
    public void The_admin_path_takes_no_credential_and_only_grantable_operations(string json) =>
        Assert.False(new RequestValidator(_ => false).Validate(Encoding.UTF8.GetBytes(json)).Ok);

    [Fact]
    public async Task Elevated_administrators_need_no_grant_for_parent_operations()
    {
        using var rig = new CodeRig();
        Assert.True((await rig.EnrollAsync(User, Codes.HashA)).Ok);
        Assert.True((await rig.AutoLogonAsync(Callers.ElevatedAdmin(), null)).Ok);
    }

    [Fact]
    public async Task Codes_and_sequences_never_reach_the_log()
    {
        using var rig = new CodeRig();
        Assert.True((await rig.EnrollAsync(User, Codes.HashA)).Ok);
        await rig.VerifyAsync(User, Codes.Wrong);
        await rig.VerifyAsync(User, Codes.A);
        var all = rig.Log.All + rig.Harness.Log.All;
        Assert.DoesNotContain("pbkdf2", all);
        Assert.DoesNotContain(string.Join(",", Codes.A), all);
        Assert.DoesNotContain("\"up\"", all);
    }

    [Fact]
    public async Task Status_tells_the_caller_its_own_codes_and_mode_only()
    {
        using var rig = new CodeRig();
        Assert.True((await rig.LockWithCodeAsync(User, Codes.HashA)).Ok);
        rig.Harness.State.CodesLookup = sid => (rig.Codes.Has(sid, CodeKind.Couch), rig.Codes.Has(sid, CodeKind.Parent), rig.Codes.StoredMode(sid));
        var (_, raw) = await rig.CallAsync(User, ProtocolJson.SerializeRequest(Operation.Status));
        var status = ProtocolJson.DeserializeResponse<StatusData>(raw)!.Data!;
        Assert.Equal((true, false, LockModes.Code), (status.CouchCode, status.ParentCode, status.LockMode));
        var (_, other) = await rig.CallAsync(Other, ProtocolJson.SerializeRequest(Operation.Status));
        var theirs = ProtocolJson.DeserializeResponse<StatusData>(other)!.Data!;
        Assert.Equal((false, false, (string?)null), (theirs.CouchCode, theirs.ParentCode, theirs.LockMode));
    }

    [Theory]
    [InlineData("""{"v":1,"op":"RemovePasscode"}""")]
    [InlineData("""{"v":1,"op":"RemovePasscode","args":{"kind":"admin"}}""")]
    [InlineData("""{"v":1,"op":"RemovePasscode","args":{"kind":"couch","hash":"x"}}""")]
    public void RemovePasscode_takes_exactly_a_kind(string json) =>
        Assert.False(new RequestValidator(_ => false).Validate(Encoding.UTF8.GetBytes(json)).Ok);

    [Fact]
    public void Storing_removing_and_the_mode_are_rate_limited_like_housekeeping()
    {
        Assert.Equal(RateBucket.Housekeeping, RateLimiter.BucketFor(Operation.RemovePasscode));
        Assert.Equal(RateBucket.Housekeeping, RateLimiter.BucketFor(Operation.SetLockMode));
    }
}
