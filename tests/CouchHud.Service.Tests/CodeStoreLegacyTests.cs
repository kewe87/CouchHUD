using CouchHud.Core.Security;
using CouchHud.Service.State;

namespace CouchHud.Service.Tests;

/// <summary>
/// The code file of CouchHUD 0.4.1 (two top-level fields of the administrator hour) after the update: it loads, is written again
/// without them on the first start, and a file damaged in any other way stays refused. The reset of a damaged file.
/// </summary>
public class CodeStoreLegacyTests
{
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    /// <summary>Exactly the shape a 0.4.1 service wrote: indented, camelCase, the two admin hour fields at the top level.</summary>
    private static string File041(string couchHash, string parentHash, long bootTimeMs, string adminHour = "\"adminHourStartMs\": 3500000,\n  \"adminHourFailures\": 2") => $$"""
        {
          "version": 1,
          "bootTimeMs": {{bootTimeMs}},
          "accounts": {
            "{{Sid}}": {
              "couch": {
                "hash": "{{couchHash}}",
                "failures": 3,
                "lockedUntilMs": 3660000,
                "lockMs": 60000
              },
              "parent": {
                "hash": "{{parentHash}}",
                "failures": 0,
                "lockedUntilMs": 0,
                "lockMs": 0
              },
              "mode": "code"
            }
          },
          {{adminHour}}
        }
        """;

    private static string Write(TempDir dir, string content)
    {
        var file = Path.Combine(dir.Path, CodeStore.FileName);
        File.WriteAllText(file, content);
        return file;
    }

    [Fact]
    public void The_041_file_loads_and_its_codes_verify_with_the_real_hash()
    {
        using var dir = new TempDir();
        Write(dir, File041(Passcode.Hash(Codes.A), Passcode.Hash(Codes.B), bootTimeMs: 1));
        var clock = new FakeClock { Now = TimeSpan.FromMilliseconds(3_700_000) };
        // The real PBKDF2 check, not the test stand-in.
        var store = CodeStore.Open(dir.Path, new CapturingSink(), clock, () => 1);
        Assert.True(store.IsAvailable);
        Assert.False(store.IsDamaged);
        Assert.True(store.Has(Sid, CodeKind.Couch));
        Assert.True(store.Has(Sid, CodeKind.Parent));
        Assert.Equal(CodeCheck.Granted, store.Verify(Sid, CodeKind.Couch, Codes.A).Result);
        Assert.Equal(CodeCheck.Granted, store.Verify(Sid, CodeKind.Parent, Codes.B).Result);
        Assert.Equal(CodeCheck.Wrong, store.Verify(Sid, CodeKind.Parent, Codes.A).Result);
    }

    [Fact]
    public void The_first_load_migrates_and_persists_without_any_change_of_codes()
    {
        using var dir = new TempDir();
        var file = Write(dir, File041(Codes.HashA, Codes.HashB, bootTimeMs: 1));
        var log = new CapturingSink();
        CodeStore.Open(dir.Path, log, new FakeClock(), () => 1, Codes.FastVerify);
        // Written again right away on the first start (same boot, so nothing else would have written it).
        var written = File.ReadAllText(file);
        Assert.DoesNotContain("adminHour", written);
        Assert.Contains(Callers.UserSid, written);
        Assert.Contains("I [codes] code file of an older version migrated", log.Lines);
        Assert.DoesNotContain(log.Lines, l => l.StartsWith("E ", StringComparison.Ordinal));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.tmp"));
        // The next start finds a current file: no second migration, the store works.
        var again = new CapturingSink();
        var store = CodeStore.Open(dir.Path, again, new FakeClock(), () => 1, Codes.FastVerify);
        Assert.True(store.IsAvailable);
        Assert.DoesNotContain(again.Lines, l => l.Contains("migrated", StringComparison.Ordinal));
        Assert.Equal(CodeCheck.Granted, store.Verify(Sid, CodeKind.Parent, Codes.B).Result);
    }

    [Fact]
    public void A_lockout_of_the_041_file_survives_the_migration()
    {
        using var dir = new TempDir();
        Write(dir, File041(Codes.HashA, Codes.HashB, bootTimeMs: 1));
        // The same boot: the lockout until 3 660 000 ms is still running at 3 600 000 ms.
        var store = CodeStore.Open(dir.Path, new CapturingSink(), new FakeClock(), () => 1, Codes.FastVerify);
        var verdict = store.Verify(Sid, CodeKind.Couch, Codes.A);
        Assert.Equal(CodeCheck.LockedOut, verdict.Result);
        // Read back from the migrated file: still locked out and the tries counted.
        var reread = CodeStore.Open(dir.Path, new CapturingSink(), new FakeClock(), () => 1, Codes.FastVerify);
        Assert.Equal(CodeCheck.LockedOut, reread.Verify(Sid, CodeKind.Couch, Codes.A).Result);
        Assert.Contains("\"failures\": 3", File.ReadAllText(Path.Combine(dir.Path, CodeStore.FileName)));
    }

    [Fact]
    public void After_a_restart_the_migration_keeps_the_full_lockout_time()
    {
        using var dir = new TempDir();
        var file = Write(dir, File041(Codes.HashA, Codes.HashB, bootTimeMs: 1));
        var log = new CapturingSink();
        // Another boot: the lockout starts again for its whole length (the clock cannot be compared across boots).
        var store = CodeStore.Open(dir.Path, log, new FakeClock(), () => 999_999_999, Codes.FastVerify);
        Assert.Equal(CodeCheck.LockedOut, store.Verify(Sid, CodeKind.Couch, Codes.A).Result);
        Assert.DoesNotContain("adminHour", File.ReadAllText(file));
        Assert.Contains("I [codes] code file of an older version migrated", log.Lines);
    }

    [Theory]
    [InlineData("\"adminHourStartMs\": 0")]
    [InlineData("\"adminHourFailures\": 5")]
    [InlineData("\"adminHourStartMs\": 0,\n  \"adminHourFailures\": 0")]
    public void One_or_both_legacy_fields_load(string adminHour)
    {
        using var dir = new TempDir();
        var file = Write(dir, File041(Codes.HashA, Codes.HashB, 1, adminHour));
        Assert.True(CodeStore.Open(dir.Path, new CapturingSink(), new FakeClock(), () => 1, Codes.FastVerify).IsAvailable);
        Assert.DoesNotContain("adminHour", File.ReadAllText(file));
    }

    [Theory]
    // Any other unknown field is still a damaged file.
    [InlineData("\"adminHourStartMs\": 0,\n  \"adminHourCount\": 1")]
    [InlineData("\"adminMinuteFailures\": 1")]
    // Negative, fractional, string or boolean values of the legacy fields are refused (null reads as "not there", see the test below).
    [InlineData("\"adminHourStartMs\": -1")]
    [InlineData("\"adminHourFailures\": -3")]
    [InlineData("\"adminHourFailures\": 1.5")]
    [InlineData("\"adminHourFailures\": \"2\"")]
    [InlineData("\"adminHourStartMs\": true")]
    public void A_file_damaged_in_any_other_way_stays_refused_and_untouched(string adminHour)
    {
        using var dir = new TempDir();
        var content = File041(Codes.HashA, Codes.HashB, 1, adminHour);
        var file = Write(dir, content);
        var log = new CapturingSink();
        var store = CodeStore.Open(dir.Path, log, new FakeClock(), () => 1, Codes.FastVerify);
        Assert.False(store.IsAvailable);
        Assert.True(store.IsDamaged);
        Assert.Equal(CodeCheck.Unavailable, store.Verify(Sid, CodeKind.Couch, Codes.A).Result);
        Assert.Equal(content, File.ReadAllText(file));
        Assert.DoesNotContain(log.Lines, l => l.Contains("migrated", StringComparison.Ordinal));
    }

    [Fact]
    public void A_null_legacy_field_is_never_written()
    {
        using var dir = new TempDir();
        var file = Write(dir, File041(Codes.HashA, Codes.HashB, 1, "\"adminHourStartMs\": null"));
        var store = CodeStore.Open(dir.Path, new CapturingSink(), new FakeClock(), () => 1, Codes.FastVerify);
        // null reads as "not there": the store works and nothing of it is written back.
        Assert.True(store.IsAvailable);
        store.Set(Sid, CodeKind.Parent, Codes.HashA, enroll: false);
        Assert.DoesNotContain("adminHour", File.ReadAllText(file));
    }

    [Fact]
    public void A_damaged_file_is_set_aside_and_the_store_starts_empty()
    {
        using var dir = new TempDir();
        var file = Write(dir, "{\"version\":1,\"accounts\":{},\"extra\":1}");
        var log = new CapturingSink();
        var store = CodeStore.Open(dir.Path, log, new FakeClock(), () => 1, Codes.FastVerify);
        Assert.True(store.IsDamaged);
        var when = new DateTimeOffset(2026, 10, 29, 12, 34, 56, 789, TimeSpan.Zero);
        Assert.Equal(CodeReset.Done, store.ResetDamaged(when));
        Assert.True(store.IsAvailable);
        Assert.False(store.IsDamaged);
        Assert.False(store.Has(Sid, CodeKind.Couch));
        Assert.False(store.IsEnrolled(Sid));
        // Kept, not deleted, under a name with the UTC time.
        var aside = file + ".broken-20261029-123456-789";
        Assert.Equal("{\"version\":1,\"accounts\":{},\"extra\":1}", File.ReadAllText(aside));
        Assert.DoesNotContain("extra", File.ReadAllText(file));
        Assert.Contains(log.Lines, l => l.StartsWith("I [codes] the damaged code file was set aside", StringComparison.Ordinal));
        // A working store is not reset again.
        Assert.Equal(CodeReset.NotDamaged, store.ResetDamaged(when.AddSeconds(1)));
        Assert.True(CodeStore.Open(dir.Path, new CapturingSink(), new FakeClock(), () => 1, Codes.FastVerify).IsAvailable);
    }

    [Fact]
    public void A_working_store_and_a_refused_folder_are_never_reset()
    {
        using var dir = new TempDir();
        var store = CodeStore.Open(dir.Path, new CapturingSink(), new FakeClock(), () => 1, Codes.FastVerify);
        store.Set(Sid, CodeKind.Couch, Codes.HashA, enroll: true);
        Assert.False(store.IsDamaged);
        Assert.Equal(CodeReset.NotDamaged, store.ResetDamaged(DateTimeOffset.UtcNow));
        Assert.True(store.Has(Sid, CodeKind.Couch));
        Assert.Empty(Directory.GetFiles(dir.Path, "*.broken-*"));

        var refused = CodeStore.Open(null, new CapturingSink(), new FakeClock(), () => 1);
        Assert.False(refused.IsAvailable);
        Assert.False(refused.IsDamaged);
        Assert.Equal(CodeReset.NotDamaged, refused.ResetDamaged(DateTimeOffset.UtcNow));
        Assert.False(refused.IsAvailable);
    }
}
