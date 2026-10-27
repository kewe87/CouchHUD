using CouchHud.Core.Security;

namespace CouchHud.Core.Tests;

public class PasscodeTests
{
    private static readonly string[] Code = ["a", "x", "up", "lb", "b", "down"];

    [Fact]
    public void Right_code_verifies()
    {
        var stored = Passcode.Hash(Code);
        Assert.True(Passcode.Verify(Code, stored));
        Assert.DoesNotContain("a,x", stored);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(Passcode.HeldByService)]
    [InlineData("pbkdf2-sha256$broken")]
    [InlineData("a,x,up,lb,b,down")]
    public void Nothing_stored_or_only_the_marker_is_no_match_for_the_old_code(string? stored)
    {
        // Review 4: the confirmation of a lock of 0.4.0 without a real stored hash asks first, like a code that does not match.
        Assert.False(Passcode.MatchesLocal(Code, stored));
    }

    [Fact]
    public void Only_the_stored_hash_of_the_code_entered_is_a_match()
    {
        var stored = Passcode.Hash(Code);
        Assert.True(Passcode.MatchesLocal(Code, stored));
        Assert.False(Passcode.MatchesLocal(["b", "b", "b", "b", "b", "b"], stored));
    }

    [Fact]
    public void The_service_marker_is_no_hash_and_never_verifies()
    {
        // P9: once the CouchHUD service holds the code, the config keeps only this marker (no hash to guess offline).
        Assert.False(Passcode.IsLocalHash(Passcode.HeldByService));
        Assert.False(Passcode.IsLocalHash(""));
        Assert.False(Passcode.IsLocalHash(null));
        Assert.True(Passcode.IsLocalHash(Passcode.Hash(Code)));
        Assert.False(Passcode.Verify(Code, Passcode.HeldByService));
    }

    [Fact]
    public void Wrong_order_or_garbage_fails()
    {
        var stored = Passcode.Hash(Code);
        Assert.False(Passcode.Verify(["x", "a", "up", "lb", "b", "down"], stored));
        Assert.False(Passcode.Verify(Code, null));
        Assert.False(Passcode.Verify(Code, "garbage"));
    }

    [Fact]
    public void Same_code_gets_different_salts() => Assert.NotEqual(Passcode.Hash(Code), Passcode.Hash(Code));

    [Theory]
    [InlineData("a,b,x,y,a")]
    [InlineData("a,b,x,y,a,b,x,y,a")]
    [InlineData("a,b,x,y,a,start")]
    public void Rejects_bad_sequences(string sequence) => Assert.Throws<ArgumentException>(() => Passcode.Hash(sequence.Split(',')));

    [Fact]
    public void Lockout_starts_after_five_misses_and_doubles()
    {
        var now = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
        var lockout = new Lockout(() => now);
        for (var i = 0; i < 4; i++) lockout.Failed();
        Assert.Equal(TimeSpan.Zero, lockout.Wait);
        lockout.Failed();
        Assert.Equal(TimeSpan.FromSeconds(30), lockout.Wait);
        now = now.AddSeconds(31);
        Assert.Equal(TimeSpan.Zero, lockout.Wait);
        lockout.Failed();
        Assert.Equal(TimeSpan.FromSeconds(60), lockout.Wait);
        lockout.Succeeded();
        Assert.Equal(TimeSpan.Zero, lockout.Wait);
    }

    [Fact]
    public void Lockout_survives_a_restart()
    {
        var file = Path.Combine(Path.GetTempPath(), $"couchhud-lockout-{Guid.NewGuid():N}.json");
        var now = DateTimeOffset.Parse("2026-01-01T12:00:00Z");
        try
        {
            var first = new Lockout(() => now, file);
            for (var i = 0; i < 5; i++) first.Failed();
            var second = new Lockout(() => now, file);
            Assert.Equal(TimeSpan.FromSeconds(30), second.Wait);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
