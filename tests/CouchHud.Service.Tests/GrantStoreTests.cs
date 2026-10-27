using CouchHud.Privileged;
using CouchHud.Service.Authorization;
using CouchHud.Service.Contracts;

namespace CouchHud.Service.Tests;

internal static class GrantUse
{
    public static bool Use(this GrantStore store, CallerInfo caller, string grant, Operation op, GrantKinds accepted) => store.TryConsume(caller, grant, op, accepted, out _);
}

/// <summary>P9: the grant table. Bound to SID + logon + session, hashed, expiring, revoked with the session, approvals single-use per operation, bounded.</summary>
public class GrantStoreTests
{
    private static readonly System.Text.RegularExpressions.Regex WireFormat = new(@"^[A-Za-z0-9+/]{43}=\z");

    private static (GrantStore Store, FakeClock Clock) New(Func<int, bool?>? locked = null)
    {
        var clock = new FakeClock();
        return (new GrantStore(clock, sessionLocked: locked), clock);
    }

    [Fact]
    public void A_grant_is_32_random_bytes_in_the_wire_format_and_only_its_hash_is_kept()
    {
        var (store, _) = New();
        var issued = store.Issue(Callers.User(), GrantKinds.Couch)!;
        Assert.Matches(WireFormat, issued.Grant);
        Assert.Equal(32, Convert.FromBase64String(issued.Grant).Length);
        Assert.Single(store.StoredKeys);
        Assert.DoesNotContain(store.StoredKeys, k => k.Contains(issued.Grant, StringComparison.Ordinal)
            || k.Contains(Convert.ToHexString(Convert.FromBase64String(issued.Grant)), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(issued.Grant, issued.ToString());
        Assert.DoesNotContain(issued.Grant, issued.ToData().ToString());
        Assert.NotEqual(issued.Grant, store.Issue(Callers.User(), GrantKinds.Couch)!.Grant);
    }

    [Fact]
    public void Valid_for_the_same_account_logon_and_session_only()
    {
        var (store, _) = New();
        var caller = Callers.User();
        var grant = store.Issue(caller, GrantKinds.Couch)!.Grant;
        Assert.True(store.TryConsume(caller, grant, Operation.ReleaseLockPolicies, GrantKinds.Couch, out var used));
        Assert.Equal(GrantKinds.Couch, used);
        Assert.False(store.Use(Callers.User(Callers.OtherSid), grant, Operation.ReleaseLockPolicies, GrantKinds.Any));
        Assert.False(store.Use(caller with { LogonId = caller.LogonId + 1 }, grant, Operation.ReleaseLockPolicies, GrantKinds.Any));
        Assert.False(store.Use(caller with { PipeSessionId = 2, TokenSessionId = 2 }, grant, Operation.ReleaseLockPolicies, GrantKinds.Any));
        // Another process of the same logon is the same caller for the service (bound to logon, never to a PID).
        Assert.True(store.Use(caller with { ProcessId = 999 }, grant, Operation.ReleaseLockPolicies, GrantKinds.Any));
    }

    [Fact]
    public void No_logon_session_no_grant()
    {
        var (store, _) = New();
        Assert.Null(store.Issue(Callers.User() with { LogonId = 0 }, GrantKinds.Couch));
        var grant = store.Issue(Callers.User(), GrantKinds.Couch)!.Grant;
        Assert.False(store.Use(Callers.User() with { LogonId = 0 }, grant, Operation.ReleaseLockPolicies, GrantKinds.Any));
    }

    [Fact]
    public void Only_single_kinds_and_approvals_only_for_named_operations()
    {
        var (store, _) = New();
        Assert.Null(store.Issue(Callers.User(), GrantKinds.Any));
        Assert.Null(store.Issue(Callers.User(), GrantKinds.ParentOrAdmin, [Operation.SetAutoLogon]));
        Assert.Null(store.Issue(Callers.User(), GrantKinds.None));
        Assert.Null(store.Issue(Callers.User(), GrantKinds.Parent));
        Assert.Null(store.Issue(Callers.User(), GrantKinds.WindowsAdmin, []));
        Assert.Null(store.Issue(Callers.User(), GrantKinds.Couch, [Operation.SetAutoLogon]));
        Assert.Null(store.Issue(Callers.User(), GrantKinds.Parent, [Operation.SetAutoLogon, Operation.SetAutoLogon, Operation.SetAutoLogon, Operation.SetAutoLogon, Operation.SetAutoLogon]));
    }

    [Fact]
    public void A_grant_of_the_wrong_kind_is_refused_and_not_used_up()
    {
        var (store, _) = New();
        var caller = Callers.User();
        var grant = store.Issue(caller, GrantKinds.WindowsUnlock)!.Grant;
        for (var i = 0; i < GrantStore.MaxUses + 5; i++) Assert.False(store.Use(caller, grant, Operation.SetAutoLogon, GrantKinds.ParentOrAdmin));
        Assert.True(store.Use(caller, grant, Operation.ReleaseLockPolicies, GrantKinds.WindowsUnlock));
    }

    [Fact]
    public void An_approval_counts_once_per_named_operation_and_never_for_another()
    {
        var (store, _) = New();
        var caller = Callers.User();
        var grant = store.Issue(caller, GrantKinds.WindowsAdmin, [Operation.ReleaseLockPolicies, Operation.ReleaseLockPolicies, Operation.CleanupPolicyKeys])!.Grant;
        // Another operation, or the wrong kind: refused and not used up.
        Assert.False(store.Use(caller, grant, Operation.SetAutoLogon, GrantKinds.ParentOrAdmin));
        Assert.False(store.Use(caller, grant, Operation.ReleaseLockPolicies, GrantKinds.Couch));
        Assert.True(store.Use(caller, grant, Operation.ReleaseLockPolicies, GrantKinds.ParentOrAdmin));
        Assert.True(store.Use(caller, grant, Operation.ReleaseLockPolicies, GrantKinds.ParentOrAdmin));
        Assert.False(store.Use(caller, grant, Operation.ReleaseLockPolicies, GrantKinds.ParentOrAdmin));
        Assert.True(store.Use(caller, grant, Operation.CleanupPolicyKeys, GrantKinds.ParentOrAdmin));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void Approvals_live_30_seconds_the_unlock_2_minutes()
    {
        var (store, clock) = New();
        var caller = Callers.User();
        var parent = store.Issue(caller, GrantKinds.Parent, [Operation.SetAutoLogon])!;
        var couch = store.Issue(caller, GrantKinds.Couch)!;
        Assert.Equal(30, parent.ToData().ExpiresInSeconds);
        Assert.Equal(["SetAutoLogon"], parent.ToData().Ops);
        Assert.Equal(120, couch.ToData().ExpiresInSeconds);
        clock.Advance(GrantStore.BoundLifetime);
        Assert.False(store.Use(caller, parent.Grant, Operation.SetAutoLogon, GrantKinds.ParentOrAdmin));
        clock.Advance(GrantStore.Lifetime - GrantStore.BoundLifetime - TimeSpan.FromSeconds(1));
        Assert.True(store.Use(caller, couch.Grant, Operation.ReleaseLockPolicies, GrantKinds.Couch));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(store.Use(caller, couch.Grant, Operation.ReleaseLockPolicies, GrantKinds.Couch));
    }

    [Fact]
    public void The_unlock_is_usable_again_until_the_cap_then_gone()
    {
        var (store, _) = New();
        var caller = Callers.User();
        var grant = store.Issue(caller, GrantKinds.Couch)!.Grant;
        for (var i = 0; i < GrantStore.MaxUses; i++) Assert.True(store.Use(caller, grant, Operation.ReleaseLockPolicies, GrantKinds.Couch), $"use {i}");
        Assert.False(store.Use(caller, grant, Operation.ReleaseLockPolicies, GrantKinds.Couch));
        Assert.Equal(0, store.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void Nothing_counts_while_the_session_is_locked_or_cannot_be_told(bool? locked)
    {
        bool? state = false;
        var (store, _) = New(_ => state);
        var caller = Callers.User();
        var grant = store.Issue(caller, GrantKinds.Parent, [Operation.SetAutoLogon])!.Grant;
        state = locked;
        Assert.False(store.Use(caller, grant, Operation.SetAutoLogon, GrantKinds.ParentOrAdmin));
        // Not used up: once the session is unlocked again (and the grant still alive) it counts.
        state = false;
        Assert.True(store.Use(caller, grant, Operation.SetAutoLogon, GrantKinds.ParentOrAdmin));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public void No_grant_is_issued_while_the_session_is_locked_or_cannot_be_told(bool? locked)
    {
        // Finding B2 of the 0.4.1 acceptance test: nobody at the couch could use it.
        var (store, _) = New(_ => locked);
        var caller = Callers.User();
        Assert.Null(store.Issue(caller, GrantKinds.Couch));
        Assert.Null(store.Issue(caller, GrantKinds.WindowsUnlock));
        Assert.Null(store.Issue(caller, GrantKinds.Parent, [Operation.SetAutoLogon]));
        Assert.Equal(0, store.Count);
        Assert.True(store.IsSessionLocked(caller.TokenSessionId));
    }

    [Fact]
    public void Revoking_a_session_ends_its_grants_only()
    {
        var (store, _) = New();
        var one = Callers.User();
        var two = Callers.User(Callers.OtherSid) with { PipeSessionId = 2, TokenSessionId = 2, LogonId = 77 };
        var a = store.Issue(one, GrantKinds.Couch)!.Grant;
        var b = store.Issue(two, GrantKinds.Couch)!.Grant;
        Assert.Equal(1, store.RevokeSession(1));
        Assert.False(store.Use(one, a, Operation.ReleaseLockPolicies, GrantKinds.Couch));
        Assert.True(store.Use(two, b, Operation.ReleaseLockPolicies, GrantKinds.Couch));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64 at all")]
    [InlineData("AAAA")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    public void Garbage_and_unknown_grants_are_refused(string grant)
    {
        var (store, _) = New();
        store.Issue(Callers.User(), GrantKinds.Couch);
        Assert.False(store.Use(Callers.User(), grant, Operation.ReleaseLockPolicies, GrantKinds.Any));
    }

    [Fact]
    public void The_table_is_bounded_per_account_and_in_total()
    {
        var (store, clock) = New();
        var caller = Callers.User();
        var first = store.Issue(caller, GrantKinds.Couch)!.Grant;
        for (var i = 0; i < GrantStore.MaxPerAccount; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            store.Issue(caller, GrantKinds.Couch);
        }
        Assert.Equal(GrantStore.MaxPerAccount, store.Count);
        Assert.False(store.Use(caller, first, Operation.ReleaseLockPolicies, GrantKinds.Any));
        for (var i = 0; i < GrantStore.MaxTotal * 2; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            store.Issue(Callers.User($"S-1-5-21-1111111111-2222222222-3333333333-{2000 + i}"), GrantKinds.Couch);
        }
        Assert.Equal(GrantStore.MaxTotal, store.Count);
    }
}

/// <summary>P9: what the service saw of unlocks, and what a lock, a disconnect or a sign-out ends.</summary>
public class SessionEventsTests
{
    private sealed class Rig
    {
        public FakeClock Clock { get; } = new();
        public GrantStore Grants { get; }
        public SessionEvents Events { get; }
        public Dictionary<int, string> Users { get; } = new() { [1] = Callers.UserSid, [2] = Callers.OtherSid };
        public CapturingSink Log { get; } = new();

        public Rig()
        {
            Grants = new GrantStore(Clock);
            Events = new SessionEvents(Clock, Grants, s => Users.GetValueOrDefault(s), Log);
        }
    }

    [Fact]
    public void An_unlock_of_the_callers_own_session_gives_one_grant()
    {
        var rig = new Rig();
        Assert.False(rig.Events.TryTake(Callers.User()));
        rig.Events.OnChange(SessionChange.Unlock, 1);
        Assert.True(rig.Events.TryTake(Callers.User()));
        Assert.False(rig.Events.TryTake(Callers.User()));
    }

    [Fact]
    public void A_sign_in_gives_nothing_automatic_or_not()
    {
        var rig = new Rig();
        rig.Events.OnChange(SessionChange.Logon, 1);
        Assert.False(rig.Events.TryTake(Callers.User()));
    }

    [Fact]
    public void Only_within_the_window()
    {
        var rig = new Rig();
        rig.Events.OnChange(SessionChange.Unlock, 1);
        rig.Clock.Advance(SessionEvents.Window + TimeSpan.FromSeconds(1));
        Assert.False(rig.Events.TryTake(Callers.User()));
    }

    [Fact]
    public void Not_for_another_session_and_not_for_another_account_in_the_session()
    {
        var rig = new Rig();
        rig.Events.OnChange(SessionChange.Unlock, 2);
        Assert.False(rig.Events.TryTake(Callers.User()));
        Assert.False(rig.Events.TryTake(Callers.User() with { PipeSessionId = 2, TokenSessionId = 2 }));
        Assert.True(rig.Events.TryTake(Callers.User(Callers.OtherSid) with { PipeSessionId = 2, TokenSessionId = 2 }));
    }

    [Theory]
    [InlineData(SessionChange.Lock)]
    [InlineData(SessionChange.Logoff)]
    [InlineData(SessionChange.Disconnect)]
    public void Lock_sign_out_and_disconnect_end_the_unlock_and_every_grant_of_the_session(SessionChange change)
    {
        var rig = new Rig();
        var caller = Callers.User();
        var grant = rig.Grants.Issue(caller, GrantKinds.Parent, [Operation.SetAutoLogon])!.Grant;
        var other = Callers.User(Callers.OtherSid) with { PipeSessionId = 2, TokenSessionId = 2 };
        var otherGrant = rig.Grants.Issue(other, GrantKinds.Couch)!.Grant;
        rig.Events.OnChange(SessionChange.Unlock, 1);
        rig.Events.OnChange(change, 1);
        Assert.False(rig.Events.TryTake(caller));
        Assert.False(rig.Grants.Use(caller, grant, Operation.SetAutoLogon, GrantKinds.ParentOrAdmin));
        Assert.True(rig.Grants.Use(other, otherGrant, Operation.ReleaseLockPolicies, GrantKinds.Couch));
    }

    [Fact]
    public void Unknown_session_user_gives_nothing()
    {
        var rig = new Rig();
        rig.Users.Remove(1);
        rig.Events.OnChange(SessionChange.Unlock, 1);
        Assert.False(rig.Events.TryTake(Callers.User()));
    }

    [Fact]
    public void The_scm_reasons_map_to_the_changes_grants_care_about()
    {
        Assert.Equal(SessionChange.Unlock, CouchHudWindowsService.Map(System.ServiceProcess.SessionChangeReason.SessionUnlock));
        Assert.Equal(SessionChange.Logon, CouchHudWindowsService.Map(System.ServiceProcess.SessionChangeReason.SessionLogon));
        Assert.Equal(SessionChange.Lock, CouchHudWindowsService.Map(System.ServiceProcess.SessionChangeReason.SessionLock));
        Assert.Equal(SessionChange.Logoff, CouchHudWindowsService.Map(System.ServiceProcess.SessionChangeReason.SessionLogoff));
        Assert.Equal(SessionChange.Disconnect, CouchHudWindowsService.Map(System.ServiceProcess.SessionChangeReason.ConsoleDisconnect));
        Assert.Equal(SessionChange.Disconnect, CouchHudWindowsService.Map(System.ServiceProcess.SessionChangeReason.RemoteDisconnect));
        Assert.Equal(SessionChange.Other, CouchHudWindowsService.Map(System.ServiceProcess.SessionChangeReason.ConsoleConnect));
    }
}

/// <summary>Review H1: an account without a password proves nothing by unlocking.</summary>
public class BlankPasswordCheckTests
{
    private sealed class AnswerLogon(int error) : ILogonVerifier
    {
        public List<string> Passwords { get; } = [];

        public LogonResult Verify(string user, string? domain, string password)
        {
            Passwords.Add(password);
            return new LogonResult(error, error == 0 ? Callers.UserSid : null);
        }
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(BlankPasswordCheck.ErrorAccountRestriction, true)]
    [InlineData(LogonErrors.LogonFailure, false)]
    [InlineData(LogonErrors.LogonTypeNotGranted, true)]
    public void Only_a_wrong_empty_password_means_there_is_one(int error, bool blank)
    {
        var logon = new AnswerLogon(error);
        var check = new BlankPasswordCheck(new FakeAccounts(), logon);
        Assert.Equal(blank, check.IsBlank(Callers.UserSid));
        Assert.Equal(blank, check.IsBlank(Callers.UserSid));
        // Asked once and kept; with an empty password only.
        Assert.Equal([""], logon.Passwords);
    }

    [Fact]
    public void An_account_that_cannot_be_resolved_counts_as_blank()
    {
        Assert.True(new BlankPasswordCheck(new FakeAccounts(), new AnswerLogon(LogonErrors.LogonFailure)).IsBlank("S-1-5-21-1-2-3-9999"));
    }

    [Fact]
    public void The_admin_check_needs_no_tcb_privilege()
    {
        // WindowsAdminVerifier reads the elevated linked token as an identification token, which is all a service without SeTcbPrivilege
        // gets, and all CheckTokenMembership needs (review L4). The service must stay without SeTcb.
        Assert.DoesNotContain("SeTcbPrivilege", ServiceHardening.RequiredPrivileges);
        Assert.DoesNotContain("SeDebugPrivilege", ServiceHardening.RequiredPrivileges);
    }
}
