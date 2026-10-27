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
