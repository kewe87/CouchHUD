using CouchHud.Core.Security;

namespace CouchHud.Core.Tests;

public class AutoLogonAccountTests
{
    [Fact]
    public void Parse_splits_domain_and_user()
    {
        Assert.Equal(new LogonAccount("player", "PC1"), AutoLogonAccount.Parse(@"PC1\player", "X"));
        Assert.Equal(new LogonAccount("player", "PC1"), AutoLogonAccount.Parse("player", "PC1"));
        Assert.Equal(new LogonAccount("a@b.de", ""), AutoLogonAccount.Parse("a@b.de", "PC1"));
        Assert.Equal(new LogonAccount("", "PC1"), AutoLogonAccount.Parse(null, "PC1"));
    }

    [Fact]
    public void Candidates_add_the_upn_once()
    {
        var primary = new LogonAccount("player", "PC1");
        Assert.Single(AutoLogonAccount.Candidates(primary, null));
        Assert.Single(AutoLogonAccount.Candidates(primary, ""));
        var two = AutoLogonAccount.Candidates(primary, "k@mail.de");
        Assert.Equal(2, two.Count);
        Assert.Equal(new LogonAccount("k@mail.de", ""), two[1]);
    }

    [Theory]
    [InlineData("player", "PC1", true)]
    [InlineData("PLAYER", "pc1", true)]
    [InlineData("player", ".", true)]
    [InlineData("player", "", true)]
    [InlineData(@"PC1\player", "", true)]
    [InlineData("anna", "PC1", false)]
    [InlineData("player", "OTHERPC", false)]
    [InlineData("k@mail.de", "MicrosoftAccount", true)]
    [InlineData("", "PC1", false)]
    public void IsSameAccount_compares_the_signed_in_user(string user, string domain, bool expected) =>
        Assert.Equal(expected, AutoLogonAccount.IsSameAccount(user, domain, new LogonAccount("player", "PC1"), "k@mail.de", "PC1"));

    [Fact]
    public void Evaluate_covers_the_states()
    {
        var me = new LogonAccount("player", "PC1");
        Assert.Equal(AutoLogonState.Off, AutoLogonAccount.Evaluate(false, "", "", false, me, null, "PC1").State);
        Assert.Equal(AutoLogonState.Lost, AutoLogonAccount.Evaluate(false, "", "", true, me, null, "PC1").State);
        var ours = AutoLogonAccount.Evaluate(true, "player", "PC1", true, me, null, "PC1");
        Assert.Equal(AutoLogonState.CouchHud, ours.State);
        Assert.False(ours.OtherUser);
        Assert.Equal("player", ours.User);
        var foreign = AutoLogonAccount.Evaluate(true, "anna", "PC1", false, me, null, "PC1");
        Assert.Equal(AutoLogonState.External, foreign.State);
        Assert.True(foreign.OtherUser);
        Assert.Equal(@"CORP\anna", AutoLogonAccount.Evaluate(true, "anna", "CORP", true, me, null, "PC1").User);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("x", true)]
    public void Empty_password_is_not_usable(string? password, bool expected) =>
        Assert.Equal(expected, AutoLogonAccount.IsUsablePassword(password));

    [Fact]
    public void Switching_off_what_is_off_already_is_no_error()
    {
        // Off with nothing of CouchHUD's stored, refused by the service (nothing of the caller's), still off: met, not refused (VM test 0.5).
        Assert.True(AutoLogonAccount.AlreadyOff(AutoLogonState.Off, refusedByService: true, enabledAfter: false));
        // Lost (Windows switched it off, CouchHUD's marker and maybe the stored password remain): a refusal stays an error.
        Assert.False(AutoLogonAccount.AlreadyOff(AutoLogonState.Lost, refusedByService: true, enabledAfter: false));
        // On: a refusal stays a refusal; any other failure is not "off already" either.
        Assert.False(AutoLogonAccount.AlreadyOff(AutoLogonState.CouchHud, refusedByService: true, enabledAfter: false));
        Assert.False(AutoLogonAccount.AlreadyOff(AutoLogonState.External, refusedByService: true, enabledAfter: false));
        Assert.False(AutoLogonAccount.AlreadyOff(AutoLogonState.Off, refusedByService: false, enabledAfter: false));
        // Switched on meanwhile (Windows asked again after the refusal): not off.
        Assert.False(AutoLogonAccount.AlreadyOff(AutoLogonState.Off, refusedByService: true, enabledAfter: true));
    }
}

public class PasswordAttemptLimiterTests
{
    [Fact]
    public void Third_failure_forces_a_pause_that_ends()
    {
        var time = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var limiter = new PasswordAttemptLimiter(() => time);
        limiter.Failed();
        limiter.Failed();
        Assert.Equal(TimeSpan.Zero, limiter.Wait);
        limiter.Failed();
        Assert.Equal(TimeSpan.FromSeconds(30), limiter.Wait);
        time = time.AddSeconds(31);
        Assert.Equal(TimeSpan.Zero, limiter.Wait);
    }

    [Fact]
    public void Success_resets_the_count()
    {
        var limiter = new PasswordAttemptLimiter(() => DateTimeOffset.UnixEpoch);
        limiter.Failed();
        limiter.Failed();
        limiter.Succeeded();
        limiter.Failed();
        Assert.Equal(TimeSpan.Zero, limiter.Wait);
    }
}
