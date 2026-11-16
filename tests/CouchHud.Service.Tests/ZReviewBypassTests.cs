using CouchHud.Service.Authorization;
using CouchHud.Service.Contracts;
using CouchHud.Service.State;

namespace CouchHud.Service.Tests;

/// <summary>
/// The second P9 review's proofs of concept (N1, M-b), turned around: each route must now be refused.
/// </summary>
public sealed class ZReviewBypassTests
{
    private static CallerInfo User => Callers.User();

    [Fact]
    public async Task Windows_mode_raise_to_code_without_code_releases_nothing()
    {
        using var rig = new CodeRig();
        Assert.True((await rig.ModeAsync(User, LockModes.Windows)).Ok);
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.ReleaseAsync(User, null)).Code);
        // Route A: "code" without any code was unprotected; now it fails closed (only a parent or an administrator opens it).
        Assert.True((await rig.ModeAsync(User, LockModes.Code)).Ok);
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.ReleaseAsync(User, null)).Code);
        Assert.True((await rig.ReleaseAsync(User, await rig.AdminGrantAsync(User, Operation.ReleaseLockPolicies))).Ok);
    }

    [Fact]
    public async Task Windows_mode_self_enrolled_couch_code_is_refused()
    {
        using var rig = new CodeRig();
        Assert.True((await rig.ModeAsync(User, LockModes.Windows)).Ok);
        // Route B: the first couch code in the Windows mode is no free enrollment any more.
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.EnrollAsync(User, Codes.HashA)).Code);
        Assert.False(rig.Codes.Has(Callers.UserSid, CodeKind.Couch));
        // And a couch grant counts for nothing in the Windows mode anyway.
        rig.Codes.Set(Callers.UserSid, CodeKind.Couch, Codes.HashA, enroll: true);
        var g = await rig.GrantAsync(User, Codes.A);
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.ReleaseAsync(User, g)).Code);
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.ModeAsync(User, LockModes.Off, g)).Code);
    }

    [Fact]
    public async Task No_couch_code_comes_along_with_the_lock_next_to_an_approved_parents_code()
    {
        using var rig = new CodeRig();
        // Review F1: an administrator set the parents' code; the child switches the code lock on with a couch code of its own.
        Assert.True((await rig.EnrollAsync(Callers.ElevatedAdmin(), Codes.HashB, CodeKinds.Parent)).Ok);
        Assert.Equal(ErrorCodes.Denied, (await rig.LockWithCodeAsync(User, Codes.HashA)).Code);
        Assert.False(rig.Codes.Has(Callers.UserSid, CodeKind.Couch));
    }

    [Fact]
    public void The_store_refuses_a_code_along_with_the_lock_by_itself()
    {
        // Review F2: checked again under the store's own lock, whatever the state check said before.
        using var dir = new TempDir();
        var store = CodeStore.Open(dir.Path, new CapturingSink(), new FakeClock(), () => 1, Codes.FastVerify);
        store.Set(Callers.UserSid, CodeKind.Parent, Codes.HashB, enroll: true);
        Assert.Equal(CodeSet.Exists, store.SetMode(Callers.UserSid, LockModes.Code, kinds: [CodeKind.Couch], hash: Codes.HashA));
        Assert.False(store.Has(Callers.UserSid, CodeKind.Couch));
        store.SetMode(Callers.OtherSid, LockModes.Windows);
        Assert.Equal(CodeSet.Exists, store.SetMode(Callers.OtherSid, LockModes.Code, kinds: [CodeKind.Couch], hash: Codes.HashA));
        // From no lock with only open codes: taken.
        store.Set("S-1-5-21-1111111111-2222222222-3333333333-1003", CodeKind.Couch, Codes.HashB, enroll: true, open: true);
        Assert.Equal(CodeSet.Saved, store.SetMode("S-1-5-21-1111111111-2222222222-3333333333-1003", LockModes.Code, clearOpen: true, kinds: [CodeKind.Couch], hash: Codes.HashA));
    }

    [Fact]
    public void A_free_enrollment_is_refused_by_the_store_once_a_lock_or_an_approved_code_is_there()
    {
        using var dir = new TempDir();
        var store = CodeStore.Open(dir.Path, new CapturingSink(), new FakeClock(), () => 1, Codes.FastVerify);
        store.SetMode(Callers.UserSid, LockModes.Windows);
        Assert.Equal(CodeSet.Exists, store.Set(Callers.UserSid, CodeKind.Couch, Codes.HashA, enroll: true, open: true));
        store.Set(Callers.OtherSid, CodeKind.Parent, Codes.HashB, enroll: true);
        Assert.Equal(CodeSet.Exists, store.Set(Callers.OtherSid, CodeKind.Couch, Codes.HashA, enroll: true, open: true));
        // An approved enrollment (with a grant) is not "open" and is not refused here.
        Assert.Equal(CodeSet.Saved, store.Set(Callers.UserSid, CodeKind.Couch, Codes.HashA, enroll: true));
        // At an open couch the free one is taken.
        Assert.Equal(CodeSet.Saved, store.Set("S-1-5-21-1111111111-2222222222-3333333333-1003", CodeKind.Couch, Codes.HashA, enroll: true, open: true));
    }

    [Fact]
    public void The_parental_chain_and_the_lock_approval_name_only_what_they_change()
    {
        // Review F4 (the shell has no test project: pinned from its source).
        var hud = File.ReadAllText(Path.Combine(Root(), "src", "CouchHud.Shell", "Hud.cs"));
        Assert.Contains("\"lockMode\" => [ServiceOperation.SetLockMode, ServiceOperation.RemovePasscode],", hud);
        Assert.Contains("\"lockModeCode\" => [ServiceOperation.SetLockMode, ServiceOperation.EnrollPasscode, ServiceOperation.ChangePasscode],", hud);
        Assert.Contains("RequireParentGrant(sequence is null ? \"lockMode\" : \"lockModeCode\")", hud);
        var forget = hud[hud.IndexOf("private void ForgetUnlock(GameSession? resuming = null)", StringComparison.Ordinal)..];
        Assert.Contains("_parentCodeChainUntil = DateTimeOffset.MinValue;", forget[..forget.IndexOf('}')]);
        var chain = hud[hud.IndexOf("public async Task<object> SetParentCodeAsync", StringComparison.Ordinal)..];
        chain = chain[..chain.IndexOf("Passcode.Validate", StringComparison.Ordinal)];
        Assert.Contains("RequireParentGrant(\"passcode\")", chain);
        Assert.Contains("_parentCodeChainUntil = DateTimeOffset.MinValue;", chain);
    }

    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CouchHud.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repository root not found");
    }

    [Fact]
    public async Task Couch_code_makes_no_parent_code_and_no_parent_grant_for_autologon()
    {
        using var rig = new CodeRig();
        Assert.True((await rig.EnrollAsync(User, Codes.HashA)).Ok);
        // The old path (parents' code from the couch code's buttons) is gone; a hash next to the couch code needs a parent or an administrator.
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.EnrollAsync(User, Codes.HashA, CodeKinds.Parent, await rig.GrantAsync(User, Codes.A))).Code);
        Assert.Equal(CodeCheckResults.NoCode, (await rig.VerifyAsync(User, Codes.A, CodeKinds.Parent, Operation.SetAutoLogon)).Result);
        Assert.Equal(ErrorCodes.NeedGrant, (await rig.AutoLogonAsync(User, await rig.GrantAsync(User, Codes.A))).Code);
    }
}
