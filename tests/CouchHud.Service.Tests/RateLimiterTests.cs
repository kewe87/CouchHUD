using CouchHud.Service.Authorization;
using CouchHud.Service.Contracts;

namespace CouchHud.Service.Tests;

public class RateLimiterTests
{
    private const string Sid = Callers.UserSid;

    [Fact]
    public void Codes_allow_five_per_minute_then_refill()
    {
        var clock = new FakeClock();
        var limiter = new RateLimiter(clock);
        for (var i = 0; i < 5; i++) Assert.Equal(RateVerdict.Allow, limiter.TryAcquire(Sid, Operation.VerifyCode).Verdict);
        var sixth = limiter.TryAcquire(Sid, Operation.VerifyCode);
        Assert.Equal(RateVerdict.RateLimited, sixth.Verdict);
        Assert.Equal(ErrorCodes.RateLimited, sixth.ErrorCode);
        Assert.True(sixth.RetryAfter > TimeSpan.Zero);

        clock.Advance(TimeSpan.FromSeconds(12));   // one token per 12 s
        Assert.Equal(RateVerdict.Allow, limiter.TryAcquire(Sid, Operation.VerifyCode).Verdict);
        Assert.Equal(RateVerdict.RateLimited, limiter.TryAcquire(Sid, Operation.VerifyCode).Verdict);
    }

    [Fact]
    public void Storing_a_code_does_not_spend_the_budget_of_checking_one()
    {
        // Storing a hash guesses nothing; the HUD stores the parents' code right after an unlock (P9).
        var limiter = new RateLimiter(new FakeClock());
        for (var i = 0; i < 5; i++) limiter.TryAcquire(Sid, Operation.VerifyCode);
        Assert.Equal(RateVerdict.RateLimited, limiter.TryAcquire(Sid, Operation.VerifyCode).Verdict);
        Assert.Equal(RateVerdict.Allow, limiter.TryAcquire(Sid, Operation.EnrollPasscode).Verdict);
        Assert.Equal(RateVerdict.Allow, limiter.TryAcquire(Sid, Operation.ChangePasscode).Verdict);
    }

    [Fact]
    public void Accounts_have_their_own_budgets()
    {
        var limiter = new RateLimiter(new FakeClock());
        for (var i = 0; i < 5; i++) limiter.TryAcquire(Sid, Operation.VerifyCode);
        Assert.Equal(RateVerdict.Allow, limiter.TryAcquire(Callers.OtherSid, Operation.VerifyCode).Verdict);
    }

    [Fact]
    public void Health_is_not_limited()
    {
        var limiter = new RateLimiter(new FakeClock());
        for (var i = 0; i < 1000; i++) Assert.Equal(RateVerdict.Allow, limiter.TryAcquire(Sid, Operation.Ping).Verdict);
        Assert.Equal(0, limiter.TrackedEntries);
    }

    [Fact]
    public void Failures_lock_out_with_a_growing_backoff()
    {
        var clock = new FakeClock();
        var limiter = new RateLimiter(clock);
        limiter.RecordFailure(Sid, Operation.VerifyCode);
        limiter.RecordFailure(Sid, Operation.VerifyCode);
        Assert.Equal(RateVerdict.Allow, limiter.TryAcquire(Sid, Operation.VerifyCode).Verdict);

        limiter.RecordFailure(Sid, Operation.VerifyCode);   // third: 30 s
        var locked = limiter.TryAcquire(Sid, Operation.VerifyCode);
        Assert.Equal(RateVerdict.LockedOut, locked.Verdict);
        Assert.Equal(ErrorCodes.LockedOut, locked.ErrorCode);
        Assert.Equal(TimeSpan.FromSeconds(30), locked.RetryAfter);

        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(RateVerdict.Allow, limiter.TryAcquire(Sid, Operation.VerifyCode).Verdict);
        limiter.RecordFailure(Sid, Operation.VerifyCode);   // fourth: 60 s
        Assert.Equal(TimeSpan.FromSeconds(60), limiter.TryAcquire(Sid, Operation.VerifyCode).RetryAfter);

        for (var i = 0; i < 40; i++) limiter.RecordFailure(Sid, Operation.VerifyCode);
        Assert.Equal(RateLimiter.MaxLockout, limiter.TryAcquire(Sid, Operation.VerifyCode).RetryAfter);
    }

    [Fact]
    public void A_success_ends_the_backoff_and_other_accounts_are_untouched()
    {
        var limiter = new RateLimiter(new FakeClock());
        for (var i = 0; i < 3; i++) limiter.RecordFailure(Sid, Operation.VerifyCode);
        Assert.Equal(RateVerdict.LockedOut, limiter.TryAcquire(Sid, Operation.VerifyCode).Verdict);
        Assert.Equal(RateVerdict.Allow, limiter.TryAcquire(Callers.OtherSid, Operation.VerifyCode).Verdict);
        limiter.RecordSuccess(Sid, Operation.VerifyCode);
        Assert.Equal(RateVerdict.Allow, limiter.TryAcquire(Sid, Operation.VerifyCode).Verdict);
    }

    [Fact]
    public void Flooding_the_table_does_not_lift_a_lockout()
    {
        var limiter = new RateLimiter(new FakeClock());
        for (var i = 0; i < 3; i++) limiter.RecordFailure(Sid, Operation.VerifyCode);
        for (var i = 0; i < RateLimiter.MaxTrackedEntries + 50; i++) limiter.TryAcquire($"S-1-5-21-1-2-3-{2000 + i}", Operation.ApplyLockPolicies);
        Assert.True(limiter.TrackedEntries <= RateLimiter.MaxTrackedEntries);
        Assert.Equal(RateVerdict.LockedOut, limiter.TryAcquire(Sid, Operation.VerifyCode).Verdict);
    }

    [Fact]
    public void A_full_table_of_lockouts_refuses_new_accounts_instead_of_forgetting()
    {
        var limiter = new RateLimiter(new FakeClock());
        for (var i = 0; i < RateLimiter.MaxTrackedEntries; i++)
        {
            for (var f = 0; f < RateLimiter.FreeFailures; f++) limiter.RecordFailure($"S-1-5-21-1-2-3-{5000 + i}", Operation.VerifyCode);
        }
        Assert.Equal(RateLimiter.MaxTrackedEntries, limiter.TrackedEntries);

        var newcomer = limiter.TryAcquire(Sid, Operation.VerifyCode);
        Assert.Equal(RateVerdict.RateLimited, newcomer.Verdict);
        limiter.RecordFailure(Sid, Operation.VerifyCode);   // not trackable: ignored, no exception
        Assert.Equal(RateLimiter.MaxTrackedEntries, limiter.TrackedEntries);
        Assert.Equal(RateVerdict.LockedOut, limiter.TryAcquire("S-1-5-21-1-2-3-5000", Operation.VerifyCode).Verdict);
    }

    [Fact]
    public void Setups_are_six_an_hour()
    {
        var clock = new FakeClock();
        var limiter = new RateLimiter(clock);
        for (var i = 0; i < 6; i++) Assert.Equal(RateVerdict.Allow, limiter.TryAcquire(Sid, Operation.InstallCouchHudUpdate).Verdict);
        Assert.Equal(RateVerdict.RateLimited, limiter.TryAcquire(Sid, Operation.InstallCouchHudUpdate).Verdict);
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(RateVerdict.Allow, limiter.TryAcquire(Sid, Operation.InstallCouchHudUpdate).Verdict);
    }
}
