using CouchHud.Service.Contracts;

namespace CouchHud.ServiceClient.Tests;

/// <summary>The migrations of the local hash and mode and storing a new code, against a fake service that answers like the real one.</summary>
public class CodeFlowTests
{
    private const string Hash = "pbkdf2-sha256$100000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    /// <summary>A service with the codes it has: the first code of an account is open, everything else needs an approval.</summary>
    private sealed class FakeCodes
    {
        public HashSet<string> Has { get; } = [];
        public string Mode { get; set; } = LockModes.Off;
        public List<string> Calls { get; } = [];

        /// <summary>A lock taken over from 0.4.0 that no administrator confirmed: codes and modes only with an approval (security review H-1).</summary>
        public bool Legacy { get; set; }

        public byte[] Answer(byte[] request)
        {
            var op = FakePipeServer.OpOf(request);
            var parsed = new RequestValidator(_ => true).Validate(request).Request!;
            var grant = parsed.Grant is null ? "-" : "g";
            switch (parsed.Args)
            {
                case LockModeArgs mode:
                    lock (Calls) Calls.Add($"{op}:{mode.Mode}{(mode.Hash is null ? "" : "+" + mode.Kind)}:{grant}");
                    if (Legacy) return FakePipeServer.Fail(ErrorCodes.NeedGrant);
                    if (LockModes.Rank(mode.Mode) < LockModes.Rank(Mode) && parsed.Grant is null) return FakePipeServer.Fail(ErrorCodes.NeedGrant);
                    if (mode.Hash is not null && (LockModes.Rank(Mode) > 0 || Has.Count > 0)) return FakePipeServer.Fail(ErrorCodes.Denied);
                    Mode = mode.Mode;
                    if (mode.Hash is not null) Has.Add(CodeKinds.Couch);
                    return FakePipeServer.Ok();
                case PasscodeHashArgs args:
                    lock (Calls) Calls.Add($"{op}:{args.Kind}:{grant}");
                    var kinds = args.Kind == CodeKinds.Both ? new[] { CodeKinds.Couch, CodeKinds.Parent } : [args.Kind];
                    if (Legacy) return FakePipeServer.Fail(ErrorCodes.NeedGrant);
                    if (op == nameof(Operation.EnrollPasscode))
                    {
                        if (kinds.Any(Has.Contains)) return FakePipeServer.Fail(ErrorCodes.Denied);
                        if (Has.Count > 0 && parsed.Grant is null) return FakePipeServer.Fail(ErrorCodes.NeedGrant);
                        Has.UnionWith(kinds);
                        return FakePipeServer.Ok();
                    }
                    if (!kinds.Any(Has.Contains)) return FakePipeServer.Fail(ErrorCodes.Denied);
                    return parsed.Grant is null ? FakePipeServer.Fail(ErrorCodes.NeedGrant) : FakePipeServer.Ok();
                default:
                    return FakePipeServer.Ok();
            }
        }
    }

    private static ServiceOps Ops(FakePipeServer server)
    {
        var scm = FakeScm.RunningHere();
        var client = new ServiceClient(scm, server.Options());
        return new ServiceOps(client, new ServiceProbe(scm, client, new Version(0, 4, 1), (_, _) => Task.CompletedTask), null, (_, _) => Task.CompletedTask);
    }

    [Fact]
    public async Task Without_a_lock_the_migration_enrolls_the_couch_code_alone()
    {
        var codes = new FakeCodes();
        await using var server = new FakePipeServer(codes.Answer);
        Assert.Equal(MigrationResult.Done, await CodeFlow.MigrateAsync(Ops(server), Hash, LockModes.Off));
        Assert.Equal(["EnrollPasscode:couch:-"], codes.Calls);
    }

    [Fact]
    public async Task The_code_lock_migrates_with_its_code_in_one_step()
    {
        var codes = new FakeCodes();
        await using var server = new FakePipeServer(codes.Answer);
        Assert.Equal(MigrationResult.Done, await CodeFlow.MigrateAsync(Ops(server), Hash, LockModes.Code));
        Assert.Equal(["SetLockMode:code+couch:-"], codes.Calls);
        Assert.Equal(LockModes.Code, codes.Mode);
    }

    [Fact]
    public async Task The_windows_lock_migrates_without_a_code_and_a_stricter_service_is_a_conflict()
    {
        var codes = new FakeCodes();
        await using var server = new FakePipeServer(codes.Answer);
        Assert.Equal(MigrationResult.Done, await CodeFlow.MigrateAsync(Ops(server), Hash, LockModes.Windows));
        Assert.Equal(["SetLockMode:windows:-"], codes.Calls);
        // The service holds a lock now: a config that says "code" with its own hash does not get its code in.
        Assert.Equal(MigrationResult.Conflict, await CodeFlow.MigrateAsync(Ops(server), Hash, LockModes.Code));
    }

    [Theory]
    [InlineData(LockModes.Code)]
    [InlineData(LockModes.Off)]
    [InlineData(LockModes.Windows)]
    public async Task A_legacy_lock_takes_nothing_of_the_config_without_an_approval(string mode)
    {
        var codes = new FakeCodes { Legacy = true };
        await using var server = new FakePipeServer(codes.Answer);
        Assert.Equal(MigrationResult.Conflict, await CodeFlow.MigrateAsync(Ops(server), Hash, mode));
        Assert.Empty(codes.Has);
        Assert.Equal(LockModes.Off, codes.Mode);
    }

    [Fact]
    public async Task A_legacy_lock_takes_the_code_of_the_config_once_an_administrator_approved_it()
    {
        var codes = new FakeCodes { Legacy = true };
        var asked = new List<Operation>();
        await using var server = new FakePipeServer(codes.Answer);
        var result = await CodeFlow.MigrateAsync(Ops(server), Hash, LockModes.Code, async (op, step) =>
        {
            asked.Add(op);
            codes.Legacy = false;   // what the administrator's approval does in the service
            return await step();
        });
        Assert.Equal(MigrationResult.Done, result);
        Assert.Equal([Operation.SetLockMode], asked);
        Assert.Contains(CodeKinds.Couch, codes.Has);
    }

    [Fact]
    public async Task A_declined_approval_migrates_nothing()
    {
        var codes = new FakeCodes { Legacy = true };
        await using var server = new FakePipeServer(codes.Answer);
        var result = await CodeFlow.MigrateAsync(Ops(server), Hash, LockModes.Off, (_, step) => step());
        Assert.Equal(MigrationResult.Conflict, result);
        Assert.Empty(codes.Has);
    }

    [Fact]
    public void A_parents_code_the_shell_did_not_set_is_flagged()
    {
        Assert.True(CodeFlow.UnknownParentCode(new HeldCodes(true, true, LockModes.Code), parentalEnabled: false));
        Assert.False(CodeFlow.UnknownParentCode(new HeldCodes(true, true, LockModes.Code), parentalEnabled: true));
        Assert.False(CodeFlow.UnknownParentCode(new HeldCodes(true, false, LockModes.Code), parentalEnabled: false));
        Assert.False(CodeFlow.UnknownParentCode(null, parentalEnabled: false));
    }

    [Fact]
    public void The_code_lock_without_a_new_code_needs_a_couch_code_that_survives_the_switch()
    {
        // From no lock the service drops the code enrolled at the open couch (review 3, item 2): "keep the code" would lock everybody out.
        Assert.False(CodeFlow.CodeLockKeepsCode(new HeldCodes(true, false, LockModes.Off)));
        Assert.False(CodeFlow.CodeLockKeepsCode(new HeldCodes(true, false, null)));
        // A lock is on and the service holds the couch code: it stays.
        Assert.True(CodeFlow.CodeLockKeepsCode(new HeldCodes(true, false, LockModes.Windows)));
        Assert.True(CodeFlow.CodeLockKeepsCode(new HeldCodes(true, false, LockModes.Code)));
        // The Windows lock dropped the couch code, nothing known, or a lock of 0.4.0 not confirmed yet: no.
        Assert.False(CodeFlow.CodeLockKeepsCode(new HeldCodes(false, false, LockModes.Windows)));
        Assert.False(CodeFlow.CodeLockKeepsCode(new HeldCodes(null, null, LockModes.Windows)));
        Assert.False(CodeFlow.CodeLockKeepsCode(null));
        Assert.False(CodeFlow.CodeLockKeepsCode(new HeldCodes(true, false, LockModes.Code, LegacyLock: true)));
    }

    [Fact]
    public void A_couch_code_from_before_applies_again_without_a_new_one()
    {
        // The account went back to the app mode, the service kept its mode and its couch code (0.6.1 owner report): raising is free, no hash.
        Assert.Equal(CodeFlow.CodeLockPlan.KeepHeldCode, CodeFlow.PlanCodeLock(new HeldCodes(true, false, LockModes.Code)));
        Assert.Equal(CodeFlow.CodeLockPlan.KeepHeldCode, CodeFlow.PlanCodeLock(new HeldCodes(true, true, LockModes.Code)));
        Assert.True(CodeFlow.CodeLockKeepsCode(new HeldCodes(true, true, LockModes.Code)));
    }

    [Fact]
    public void A_new_code_never_comes_along_next_to_a_held_parents_code_or_a_lock_without_a_couch_code()
    {
        // Only a parents' code held: the couch code is stored first with the parents' (or an administrator's) approval, then the mode is raised.
        Assert.Equal(CodeFlow.CodeLockPlan.StoreThenSwitch, CodeFlow.PlanCodeLock(new HeldCodes(false, true, LockModes.Code)));
        Assert.Equal(CodeFlow.CodeLockPlan.StoreThenSwitch, CodeFlow.PlanCodeLock(new HeldCodes(false, true, LockModes.Off)));
        Assert.Equal(CodeFlow.CodeLockPlan.StoreThenSwitch, CodeFlow.PlanCodeLock(new HeldCodes(false, true, null)));
        // A lock is on and no couch code is held (the code lost): a hash along is refused ("only from no lock"), so store first.
        Assert.Equal(CodeFlow.CodeLockPlan.StoreThenSwitch, CodeFlow.PlanCodeLock(new HeldCodes(false, false, LockModes.Windows)));
        Assert.Equal(CodeFlow.CodeLockPlan.StoreThenSwitch, CodeFlow.PlanCodeLock(new HeldCodes(null, null, LockModes.Code)));
    }

    [Fact]
    public void Nothing_held_unknown_or_a_legacy_lock_keep_the_default_flow()
    {
        Assert.Equal(CodeFlow.CodeLockPlan.Default, CodeFlow.PlanCodeLock(new HeldCodes(false, false, LockModes.Off)));
        Assert.Equal(CodeFlow.CodeLockPlan.Default, CodeFlow.PlanCodeLock(new HeldCodes(false, false, null)));
        // A couch code at the open couch (mode off): the switch drops it, a new code comes along as before.
        Assert.Equal(CodeFlow.CodeLockPlan.Default, CodeFlow.PlanCodeLock(new HeldCodes(true, false, LockModes.Off)));
        Assert.Equal(CodeFlow.CodeLockPlan.Default, CodeFlow.PlanCodeLock(null));
        // A lock of 0.4.0 waits for an administrator's confirmation: never the keep or store paths.
        Assert.Equal(CodeFlow.CodeLockPlan.Default, CodeFlow.PlanCodeLock(new HeldCodes(true, true, LockModes.Code, LegacyLock: true)));
    }

    [Fact]
    public void A_refusal_names_the_held_code_only_from_the_held_state()
    {
        Assert.Equal("codeHeld", CodeFlow.RefusalHint(new HeldCodes(true, false, LockModes.Code)));
        Assert.Equal("codeHeld", CodeFlow.RefusalHint(new HeldCodes(true, true, LockModes.Off)));
        Assert.Equal("parentHeld", CodeFlow.RefusalHint(new HeldCodes(false, true, LockModes.Code)));
        Assert.Null(CodeFlow.RefusalHint(new HeldCodes(false, false, LockModes.Code)));
        Assert.Null(CodeFlow.RefusalHint(null));
        Assert.Null(CodeFlow.RefusalHint(new HeldCodes(true, true, LockModes.Code, LegacyLock: true)));
        Assert.Equal("storeDamaged", CodeFlow.RefusalHint(new HeldCodes(true, true, LockModes.Code, Damaged: true)));
    }

    [Fact]
    public void A_refusal_is_put_down_to_a_damaged_code_store_only_when_the_service_says_so()
    {
        var damaged = new HeldCodes(true, true, LockModes.Code, Damaged: true);
        Assert.True(CodeFlow.RefusedForDamage(new OpResult(FailureKind.Refused, ErrorCodes.Denied), damaged));
        Assert.True(CodeFlow.RefusedForDamage(new OpResult(FailureKind.Refused, ErrorCodes.Unavailable), damaged));
        // Not damaged, unknown, a success or another failure: the usual message.
        Assert.False(CodeFlow.RefusedForDamage(new OpResult(FailureKind.Refused, ErrorCodes.Denied), new HeldCodes(true, true, LockModes.Code)));
        Assert.False(CodeFlow.RefusedForDamage(new OpResult(FailureKind.Refused, ErrorCodes.Denied), null));
        Assert.False(CodeFlow.RefusedForDamage(OpResult.Success, damaged));
        Assert.False(CodeFlow.RefusedForDamage(new OpResult(FailureKind.NotRunning, ErrorCodes.Denied) with { Code = "busy" }, damaged));
        // Never taken for "the code is stored already" (the keep path).
        Assert.Equal(CodeFlow.TogetherOutcome.Refused, CodeFlow.ClassifyTogether(new OpResult(FailureKind.Refused, ErrorCodes.Denied), damaged));
    }

    /// <summary>The service as the shell sees it around storing a code and raising the mode; the raise drops what was enrolled at the open couch.</summary>
    private sealed class StoreRaiseFake
    {
        public bool OpenCouch { get; set; }
        public bool ApprovedStays { get; set; } = true;
        public bool Couch { get; set; }
        public bool Parent { get; set; }
        public string Mode { get; set; } = LockModes.Off;
        public int Stores { get; set; }
        public bool RefuseSecondStore { get; set; }
        public int NoAnswers { get; set; }
        public List<string> Log { get; } = [];

        public Task Store()
        {
            Stores++;
            if (Stores > 1 && RefuseSecondStore) throw new InvalidOperationException("refused");
            Couch = true;
            // Stored at the open couch (mode off) the code is an open one; with a mode on it is an approved one.
            OpenCouch = Mode == LockModes.Off;
            return Task.CompletedTask;
        }

        public Task Raise()
        {
            Mode = LockModes.Code;
            // The service's SetMode drops every open code: the one just stored and an open parents' code.
            if (OpenCouch) { Couch = false; Parent = false; OpenCouch = false; }
            return Task.CompletedTask;
        }

        public Task<HeldCodes?> Status()
        {
            if (NoAnswers > 0) { NoAnswers--; return Task.FromResult<HeldCodes?>(null); }
            return Task.FromResult<HeldCodes?>(new HeldCodes(Couch, Parent, Mode));
        }
    }

    [Fact]
    public async Task An_open_parents_code_held_does_not_end_in_a_lock_without_a_couch_code()
    {
        // Lock off, the service holds a parents' code set freely at the open couch: the plan is store-then-switch (it cannot tell open from approved).
        var service = new StoreRaiseFake { Parent = true };
        Assert.Equal(CodeFlow.CodeLockPlan.StoreThenSwitch, CodeFlow.PlanCodeLock(await service.Status()));
        var outcome = await CodeFlow.StoreThenRaiseAsync(LockModes.Code, service.Store, service.Raise, service.Status, service.Log.Add);
        // The first store went away with the raise; it is stored once more under the mode that is on, and then it stays.
        Assert.Equal(CodeFlow.StoreRaiseOutcome.Done, outcome);
        Assert.Equal(2, service.Stores);
        Assert.True(service.Couch);
        Assert.Equal(LockModes.Code, service.Mode);
        Assert.NotEmpty(service.Log);
    }

    [Fact]
    public async Task Under_the_windows_mode_the_couch_code_is_never_stored_again()
    {
        // The service drops the couch code under the Windows mode on purpose: no status check, no second store.
        var service = new StoreRaiseFake { Parent = true };
        var asked = 0;
        var outcome = await CodeFlow.StoreThenRaiseAsync(LockModes.Windows, service.Store, service.Raise, () => { asked++; return service.Status(); });
        Assert.Equal(CodeFlow.StoreRaiseOutcome.Done, outcome);
        Assert.Equal(1, service.Stores);
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task A_code_that_stays_after_the_raise_is_stored_once()
    {
        var service = new StoreRaiseFake { Mode = LockModes.Windows };
        Assert.Equal(CodeFlow.StoreRaiseOutcome.Done, await CodeFlow.StoreThenRaiseAsync(LockModes.Code, service.Store, service.Raise, service.Status));
        Assert.Equal(1, service.Stores);
    }

    [Fact]
    public async Task A_code_that_is_missing_after_the_second_store_is_reported_so_the_config_stays_unchanged()
    {
        var service = new StoreRaiseFake { Parent = true, RefuseSecondStore = true };
        Assert.Equal(CodeFlow.StoreRaiseOutcome.CodeMissing, await CodeFlow.StoreThenRaiseAsync(LockModes.Code, service.Store, service.Raise, service.Status));
        Assert.False(service.Couch);
        // Never "Done": the caller does not take the code lock over.
        var silent = new StoreRaiseFake { Parent = true };
        Assert.Equal(CodeFlow.StoreRaiseOutcome.CodeMissing, await CodeFlow.StoreThenRaiseAsync(LockModes.Code, silent.Store, silent.Raise,
            () => Task.FromResult<HeldCodes?>(new HeldCodes(false, false, LockModes.Code))));
    }

    [Fact]
    public async Task No_answer_afterwards_is_unconfirmed_and_a_refused_store_or_raise_throws_before_anything_else()
    {
        var quiet = new StoreRaiseFake { NoAnswers = 2 };
        Assert.Equal(CodeFlow.StoreRaiseOutcome.Unconfirmed, await CodeFlow.StoreThenRaiseAsync(LockModes.Code, quiet.Store, quiet.Raise, quiet.Status));
        var raised = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => CodeFlow.StoreThenRaiseAsync(LockModes.Code, 
            () => throw new InvalidOperationException("denied"), () => { raised = true; return Task.CompletedTask; }, () => Task.FromResult<HeldCodes?>(null)));
        Assert.False(raised);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CodeFlow.StoreThenRaiseAsync(LockModes.Code, 
            () => Task.CompletedTask, () => throw new InvalidOperationException("denied"), () => Task.FromResult<HeldCodes?>(null)));
    }

    [Fact]
    public async Task Shell_mode_with_an_approved_code_the_plan_cannot_see_stores_then_raises_and_ends_in_the_code_lock()
    {
        // Regression (owner report 0.6.2): lock off in the settings, the service holds an approved couch code from an interrupted first attempt.
        // The plan says "default", the one-step switch is refused as "an approved code exists" and Shell mode could never be set up.
        var service = new StoreRaiseFake { Couch = true, Mode = LockModes.Off, OpenCouch = false };
        Assert.Equal(CodeFlow.CodeLockPlan.Default, CodeFlow.PlanCodeLock(await service.Status()));
        var together = 0;
        var outcome = await CodeFlow.SwitchOnWithCodeAsync(LockModes.Code, () => { together++; return Task.FromResult(false); },
            service.Store, service.Raise, service.Status, service.Log.Add);
        Assert.Equal(CodeFlow.StoreRaiseOutcome.Done, outcome);
        Assert.Equal(1, together);
        Assert.Equal(LockModes.Code, service.Mode);
        Assert.True(service.Couch);
    }

    [Fact]
    public async Task The_one_step_switch_is_used_when_the_service_takes_it_and_a_refused_store_still_fails_without_raising()
    {
        var service = new StoreRaiseFake();
        var stored = false;
        Assert.Equal(CodeFlow.StoreRaiseOutcome.Done, await CodeFlow.SwitchOnWithCodeAsync(LockModes.Code, () => Task.FromResult(true),
            () => { stored = true; return Task.CompletedTask; }, service.Raise, service.Status));
        Assert.False(stored);
        // A wrong or missing approval: the store is refused, the mode is never raised, the exception reaches the caller (the config stays).
        var raised = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => CodeFlow.SwitchOnWithCodeAsync(LockModes.Code, () => Task.FromResult(false),
            () => throw new InvalidOperationException("denied"), () => { raised = true; return Task.CompletedTask; }, service.Status));
        Assert.False(raised);
        // Any other refusal of the one-step switch throws and never falls back.
        var fell = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => CodeFlow.SwitchOnWithCodeAsync(LockModes.Code, () => throw new InvalidOperationException("no"),
            () => { fell = true; return Task.CompletedTask; }, service.Raise, service.Status));
        Assert.False(fell);
    }

    [Fact]
    public void Only_a_denial_with_a_held_code_falls_back_everything_else_is_refused()
    {
        var denied = new OpResult(FailureKind.Refused, ErrorCodes.Denied);
        // Denied and the service holds an approved code or a lock mode that is on: the approved way.
        Assert.Equal(CodeFlow.TogetherOutcome.Fallback, CodeFlow.ClassifyTogether(denied, new HeldCodes(true, false, LockModes.Off)));
        Assert.Equal(CodeFlow.TogetherOutcome.Fallback, CodeFlow.ClassifyTogether(denied, new HeldCodes(false, true, LockModes.Off)));
        Assert.Equal(CodeFlow.TogetherOutcome.Fallback, CodeFlow.ClassifyTogether(denied, new HeldCodes(false, false, LockModes.Windows)));
        Assert.Equal(CodeFlow.TogetherOutcome.Done, CodeFlow.ClassifyTogether(OpResult.Success, null));
        // Denied with nothing held, no answer, or a lock of 0.4.0 not confirmed: refused (fail closed).
        Assert.Equal(CodeFlow.TogetherOutcome.Refused, CodeFlow.ClassifyTogether(denied, new HeldCodes(false, false, LockModes.Off)));
        Assert.Equal(CodeFlow.TogetherOutcome.Refused, CodeFlow.ClassifyTogether(denied, null));
        Assert.Equal(CodeFlow.TogetherOutcome.Refused, CodeFlow.ClassifyTogether(denied, new HeldCodes(true, true, LockModes.Code, LegacyLock: true)));
        // A wrong code, a lockout, a missing grant, an unavailable service: never a fallback, whatever is held.
        var held = new HeldCodes(true, true, LockModes.Code);
        foreach (var code in new[] { ErrorCodes.BadCode, ErrorCodes.LockedOut, ErrorCodes.NeedGrant, ErrorCodes.Unavailable, ErrorCodes.RateLimited })
            Assert.Equal(CodeFlow.TogetherOutcome.Refused, CodeFlow.ClassifyTogether(new OpResult(FailureKind.Refused, code), held));
    }

    [Fact]
    public async Task A_service_with_its_own_code_is_a_conflict_not_a_success()
    {
        var codes = new FakeCodes();
        codes.Has.Add(CodeKinds.Couch);
        await using var server = new FakePipeServer(codes.Answer);
        Assert.Equal(MigrationResult.Conflict, await CodeFlow.MigrateAsync(Ops(server), Hash, LockModes.Off));
    }

    [Theory]
    [InlineData("")]
    [InlineData("service")]
    [InlineData("pbkdf2-sha256$1$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    public async Task Nothing_that_is_not_a_hash_of_ours_is_migrated(string local)
    {
        var codes = new FakeCodes();
        await using var server = new FakePipeServer(codes.Answer);
        Assert.Equal(MigrationResult.NotYet, await CodeFlow.MigrateAsync(Ops(server), local, LockModes.Off));
        Assert.Empty(codes.Calls);
    }

    [Fact]
    public async Task Without_parental_controls_the_couch_code_alone_enrolled_then_changed_with_its_grant()
    {
        var codes = new FakeCodes();
        await using var server = new FakePipeServer(codes.Answer);
        var ops = Ops(server);
        var asked = new List<Operation>();
        Task<OpResult> WithGrant(Operation op, Func<Task<OpResult>> call) => GrantFlow.WithGrantAsync(call, () =>
        {
            asked.Add(op);
            ops.Cache.Set(new GrantData(Convert.ToBase64String(new byte[32]), GrantKindNames.WindowsAdmin, 30, [op.ToString()]));
            return Task.FromResult(true);
        });
        Assert.True((await CodeFlow.StoreAsync(ops, Hash, parental: false, WithGrant)).Ok);
        Assert.Empty(asked);
        // The couch grant of the unlock changes it without asking anybody.
        ops.Cache.Set(new GrantData(Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()), GrantKindNames.Couch, 120));
        Assert.True((await CodeFlow.StoreAsync(ops, Hash, parental: false, WithGrant)).Ok);
        Assert.Empty(asked);
        Assert.Contains("ChangePasscode:couch:g", codes.Calls);
    }

    [Fact]
    public async Task With_parental_controls_both_codes_in_one_step_and_a_change_needs_the_parents()
    {
        var codes = new FakeCodes();
        await using var server = new FakePipeServer(codes.Answer);
        var ops = Ops(server);
        var asked = new List<Operation>();
        Task<OpResult> WithGrant(Operation op, Func<Task<OpResult>> call) => GrantFlow.WithGrantAsync(call, () =>
        {
            asked.Add(op);
            ops.Cache.Set(new GrantData(Convert.ToBase64String(new byte[32]), GrantKindNames.WindowsAdmin, 30, [op.ToString()]));
            return Task.FromResult(true);
        });
        Assert.True((await CodeFlow.StoreAsync(ops, Hash, parental: true, WithGrant)).Ok);
        Assert.Equal(["EnrollPasscode:both:-"], codes.Calls);
        // Even with the couch grant at hand, the pair changes only with a parent's (here: the administrator's fallback) approval.
        ops.Cache.Set(new GrantData(Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray()), GrantKindNames.Couch, 120));
        Assert.True((await CodeFlow.StoreAsync(ops, Hash, parental: true, WithGrant)).Ok);
        Assert.Equal([Operation.ChangePasscode], asked);
        Assert.Equal("ChangePasscode:both:g", codes.Calls[^1]);
    }

    [Fact]
    public async Task A_refused_approval_stops_with_the_refusal()
    {
        var codes = new FakeCodes();
        codes.Has.Add(CodeKinds.Couch);
        await using var server = new FakePipeServer(codes.Answer);
        var result = await CodeFlow.StoreAsync(Ops(server), Hash, parental: false, (_, call) => GrantFlow.WithGrantAsync(call, () => Task.FromResult(false)));
        Assert.Equal(FailureKind.NeedGrant, result.Failure);
    }
}
