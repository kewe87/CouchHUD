using CouchHud.Service.Authorization;
using CouchHud.Service.Contracts;

namespace CouchHud.Service.Tests;

/// <summary>The checks that depend on state: they run for every operation that names them, and a missing one denies.</summary>
public class StateCheckTests
{
    private static readonly StateChecks Builtin = StateChecks.BuiltIn(n => n == "GamingServices", AssumeCodeEnrolled.Instance);

    private static ValidatedRequest Req(Operation op, object? args = null, string? grant = null) => new(op, grant, args);

    /// <summary>A request with valid arguments for each operation that has a state check.</summary>
    public static TheoryData<Operation, object?> GuardedRequests() => new()
    {
        { Operation.ResumeServices, new ServicesArgs(["GamingServices"]) },
        { Operation.ReleaseLockPolicies, new ReleaseLockPoliciesArgs(["lock"]) },
        { Operation.CleanupPolicyKeys, null },
        { Operation.AutoLogonOff, null },
        { Operation.InstallWindowsUpdates, new InstallWindowsUpdatesArgs([Guid.NewGuid().ToString("D")]) },
        { Operation.InstallCouchHudUpdate, new SetupStreamArgs("CouchHud-Setup-0.4.2.exe", 10, "AA==", "AA==") },
        { Operation.EnrollPasscode, new PasscodeHashArgs("x") },
        { Operation.GetJob, new GetJobArgs("0123456789abcdef0123456789abcdef") },
        { Operation.PauseServices, new ServicesArgs(["WinDefend"]) },
    };

    [Theory]
    [MemberData(nameof(GuardedRequests))]
    public void Until_the_state_exists_these_are_denied_to_a_user_even_where_the_table_allows(Operation op, object? args)
    {
        // codeEnrolled: false makes the table allow release and cleanup (parity), so only the state check stands in the way.
        var decision = Authorizer.Authorize(Callers.User(), Req(op, args), codeEnrolled: false, Builtin);
        Assert.Equal(Verdict.Deny, decision.Verdict);
    }

    [Fact]
    public void Every_check_in_the_table_is_built_in_or_denies_until_registered()
    {
        foreach (var (op, rule) in OperationPolicy.Table)
        {
            foreach (var check in rule.Checks.Where(c => c != StateCheck.None && !Builtin.IsRegistered(c)))
            {
                var empty = new StateChecks();
                var decision = empty.Run(new Rule(Requirement.OwnAccount, check), Callers.ElevatedAdmin(), Req(op));
                Assert.True(decision.Verdict == Verdict.Deny, $"{op}/{check}");
            }
        }
        // The ones available now.
        Assert.True(Builtin.IsRegistered(StateCheck.ServiceAllowlist));
        Assert.True(Builtin.IsRegistered(StateCheck.EnrollAllowed));
        Assert.True(Builtin.IsRegistered(StateCheck.UpdateIdsFromSearch));
    }

    [Fact]
    public void A_registered_check_opens_the_operation_and_a_throwing_one_denies()
    {
        var checks = StateChecks.BuiltIn(_ => false, AssumeCodeEnrolled.Instance).Register(StateCheck.JobOwner, Harness.AllowAll);
        Assert.Equal(Verdict.Allow, Authorizer.Authorize(Callers.User(), Req(Operation.GetJob, new GetJobArgs("0")), false, checks).Verdict);
        checks.Register(StateCheck.JobOwner, new StateCheckFunc((_, _) => throw new InvalidOperationException()));
        Assert.Equal(Verdict.Deny, Authorizer.Authorize(Callers.User(), Req(Operation.GetJob, new GetJobArgs("0")), false, checks).Verdict);
    }

    [Fact]
    public void A_check_that_asks_for_a_grant_makes_the_request_need_one()
    {
        var checks = new StateChecks()
            .Register(StateCheck.SignedSetupPipeline, new StateCheckFunc((c, _) => c.IsElevatedAdmin ? AuthorizationDecision.Allowed : AuthorizationDecision.GrantNeeded));
        var request = Req(Operation.InstallCouchHudUpdate, new SetupStreamArgs("CouchHud-Setup-0.4.2.exe", 10, "AA==", "AA=="));
        Assert.Equal(Verdict.NeedGrant, Authorizer.Authorize(Callers.User(), request, false, checks).Verdict);
        Assert.Equal(Verdict.Allow, Authorizer.Authorize(Callers.ElevatedAdmin(), request, false, checks).Verdict);
    }

    [Fact]
    public void A_table_denial_wins_over_any_check()
    {
        var checks = new StateChecks();
        Assert.Equal(Verdict.Deny, Authorizer.Authorize(Callers.User(), Req(Operation.ReleaseAllAccounts), false, checks).Verdict);
        Assert.Equal(Verdict.Deny, Authorizer.Authorize(Callers.User() with { IntegrityLevel = IntegrityLevels.Low }, Req(Operation.Ping), false, checks).Verdict);
    }

    [Fact]
    public void Pause_is_allowed_only_for_listed_services()
    {
        Assert.Equal(Verdict.Allow, Authorizer.Authorize(Callers.User(), Req(Operation.PauseServices, new ServicesArgs(["GamingServices"])), false, Builtin).Verdict);
        Assert.Equal(Verdict.Deny, Authorizer.Authorize(Callers.User(), Req(Operation.PauseServices, new ServicesArgs(["Spooler"])), false, Builtin).Verdict);
        Assert.Equal(Verdict.Deny, Authorizer.Authorize(Callers.User(), Req(Operation.PauseServices, new ServicesArgs(["GamingServices", "Spooler"])), false, Builtin).Verdict);
        Assert.Equal(Verdict.Deny, Authorizer.Authorize(Callers.User(), Req(Operation.PauseServices, new ServicesArgs([])), false, Builtin).Verdict);
    }

    [Theory]
    [InlineData("WinDefend")]
    [InlineData("windefend")]
    [InlineData("WdNisSvc")]
    [InlineData("Sense")]
    [InlineData("wuauserv")]
    [InlineData("UsoSvc")]
    [InlineData("mpssvc")]
    [InlineData("BFE")]
    [InlineData("EventLog")]
    [InlineData("CouchHud")]
    [InlineData("CouchHud.Service")]
    public void The_denylist_wins_over_any_allowlist(string name)
    {
        Assert.False(ServicePolicy.IsAllowed(name, _ => true));
        var everything = StateChecks.BuiltIn(_ => true, AssumeCodeEnrolled.Instance);
        Assert.Equal(Verdict.Deny, Authorizer.Authorize(Callers.ElevatedAdmin(), Req(Operation.PauseServices, new ServicesArgs([name])), false, everything).Verdict);
        Assert.Equal(Verdict.Deny, Authorizer.Authorize(Callers.ElevatedAdmin(), Req(Operation.ResumeServices, new ServicesArgs([name])), false, everything).Verdict);
    }

    [Fact]
    public async Task The_denylist_also_stops_the_validator_in_the_service_wiring()
    {
        var harness = new Harness(onServiceList: _ => true, extraHandlers: h => h[Operation.PauseServices] = Harness.Ok);
        var denied = await harness.OneAsync(Callers.User(), System.Text.Encoding.UTF8.GetBytes("""{"v":1,"op":"PauseServices","args":{"names":["WinDefend"]}}"""));
        Assert.Equal(ErrorCodes.BadArgument, denied.Code);
        var allowed = await harness.OneAsync(Callers.User(), System.Text.Encoding.UTF8.GetBytes("""{"v":1,"op":"PauseServices","args":{"names":["GamingServices"]}}"""));
        Assert.True(allowed.Ok);
    }

    [Fact]
    public void Enroll_is_allowed_only_while_no_code_exists()
    {
        var none = StateChecks.BuiltIn(_ => false, new FixedCodeState(false));
        var request = Req(Operation.EnrollPasscode, new PasscodeHashArgs("x"));
        Assert.Equal(Verdict.Allow, Authorizer.Authorize(Callers.User(), request, false, none).Verdict);
        Assert.Equal(Verdict.Deny, Authorizer.Authorize(Callers.User(), request, true, StateChecks.BuiltIn(_ => false, new FixedCodeState(true))).Verdict);
    }

    [Fact]
    public void Windows_updates_without_ids_need_no_search_state()
    {
        Assert.Equal(Verdict.Allow, Authorizer.Authorize(Callers.User(), Req(Operation.InstallWindowsUpdates, new InstallWindowsUpdatesArgs()), false, Builtin).Verdict);
        Assert.Equal(Verdict.Deny, Authorizer.Authorize(Callers.User(), Req(Operation.InstallWindowsUpdates, new InstallWindowsUpdatesArgs([Guid.Empty.ToString("D")])), false, Builtin).Verdict);
    }
}
