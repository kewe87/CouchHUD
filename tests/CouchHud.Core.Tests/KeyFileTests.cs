using CouchHud.Core.Parental;
using System.Text;
using CouchHud.Core.Files;

namespace CouchHud.Core.Tests;

public sealed class KeyFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "keyfile-" + Guid.NewGuid().ToString("N"));
    private string KeyPath => Path.Combine(_dir, "parental.key");

    public KeyFileTests() => Directory.CreateDirectory(_dir);
    public void Dispose() => Directory.Delete(_dir, true);

    // Stand-ins for DPAPI: "dpapi:" + text; anything else cannot be decrypted (returns "" like Secrets.Unprotect).
    private static string Protect(string s) => "dpapi:" + s;
    private static string Unprotect(string s) => s.StartsWith("dpapi:", StringComparison.Ordinal) ? s[6..] : "";

    [Fact]
    public void An_absent_file_gets_a_new_key()
    {
        var (key, state) = KeyFile.Load(KeyPath, Protect, Unprotect);
        Assert.Equal(KeyFileState.Created, state);
        Assert.Equal(32, key!.Length);
        Assert.True(File.Exists(KeyPath));
    }

    [Fact]
    public void A_readable_file_gives_the_same_key_every_time()
    {
        var (first, _) = KeyFile.Load(KeyPath, Protect, Unprotect);
        var (second, state) = KeyFile.Load(KeyPath, Protect, Unprotect);
        Assert.Equal(KeyFileState.Read, state);
        Assert.Equal(first, second);
    }

    [Fact]
    public void An_unreadable_file_is_not_overwritten_and_gives_no_key()
    {
        var foreign = Encoding.UTF8.GetBytes("dpapi:AAAA-belongs-to-another-profile");
        File.WriteAllBytes(KeyPath, foreign);
        var (key, state) = KeyFile.Load(KeyPath, Protect, Unprotect);
        Assert.Null(key);
        Assert.Equal(KeyFileState.Unreadable, state);
        Assert.Equal(foreign, File.ReadAllBytes(KeyPath));
        // asked again: still the same, still untouched
        Assert.Equal(KeyFileState.Unreadable, KeyFile.Load(KeyPath, Protect, Unprotect).State);
        Assert.Equal(foreign, File.ReadAllBytes(KeyPath));
    }

    [Fact]
    public void A_file_that_decrypts_to_no_key_is_left_alone()
    {
        File.WriteAllText(KeyPath, "dpapi:***not base64***");
        var before = File.ReadAllBytes(KeyPath);
        var (key, state) = KeyFile.Load(KeyPath, Protect, Unprotect);
        Assert.Null(key);
        Assert.Equal(KeyFileState.Unreadable, state);
        Assert.Equal(before, File.ReadAllBytes(KeyPath));
    }

    [Fact]
    public void An_empty_file_holds_no_key_and_is_filled()
    {
        File.WriteAllText(KeyPath, "");
        var (key, state) = KeyFile.Load(KeyPath, Protect, Unprotect);
        Assert.Equal(KeyFileState.Created, state);
        Assert.NotNull(key);
    }

    [Fact]
    public void A_whitespace_only_file_holds_no_key_and_is_filled()
    {
        File.WriteAllText(KeyPath, "  " + (char)10);
        var (key, state) = KeyFile.Load(KeyPath, Protect, Unprotect);
        Assert.Equal(KeyFileState.Created, state);
        Assert.NotNull(key);
    }

    [Fact]
    public void A_prefixed_file_that_decrypts_to_nothing_is_unreadable_and_untouched()
    {
        File.WriteAllText(KeyPath, "dpapi:");
        var before = File.ReadAllBytes(KeyPath);
        var (key, state) = KeyFile.Load(KeyPath, Protect, _ => "");
        Assert.Null(key);
        Assert.Equal(KeyFileState.Unreadable, state);
        Assert.Equal(before, File.ReadAllBytes(KeyPath));
    }

    [Fact]
    public void A_plaintext_key_without_the_DPAPI_prefix_is_refused_and_untouched()
    {
        File.WriteAllText(KeyPath, Convert.ToBase64String(new byte[32]));
        var before = File.ReadAllBytes(KeyPath);
        // an unprotect that passes plain values through, like Secrets.Unprotect
        var (key, state) = KeyFile.Load(KeyPath, Protect, s => s);
        Assert.Null(key);
        Assert.Equal(KeyFileState.Unreadable, state);
        Assert.Equal(before, File.ReadAllBytes(KeyPath));
    }

    [Fact]
    public void A_locked_file_fails_without_changing_it()
    {
        var (_, _) = KeyFile.Load(KeyPath, Protect, Unprotect);
        var before = File.ReadAllBytes(KeyPath);
        using (new FileStream(KeyPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var (key, state) = KeyFile.Load(KeyPath, Protect, Unprotect);
            Assert.Null(key);
            Assert.Equal(KeyFileState.Failed, state);
        }
        Assert.Equal(before, File.ReadAllBytes(KeyPath));
    }

    [Fact]
    public void A_protect_that_throws_fails_and_leaves_no_file()
    {
        var (key, state) = KeyFile.Load(KeyPath, _ => throw new System.Security.Cryptography.CryptographicException("no DPAPI"), Unprotect);
        Assert.Null(key);
        Assert.Equal(KeyFileState.Failed, state);
        Assert.Empty(Directory.GetFileSystemEntries(_dir));
    }

    [Fact]
    public void A_ledger_signed_with_another_key_is_tampered_but_without_any_key_nothing_is_checked()
    {
        var ledgerFile = Path.Combine(_dir, "playtime.json");
        var keyA = new byte[32]; keyA[0] = 1;
        var day = new DateOnly(2026, 10, 28);
        var ledger = new PlayLedger(ledgerFile, keyA);
        ledger.Add(day, "g", 600);
        ledger.Save(day);
        Assert.False(new PlayLedger(ledgerFile, keyA, expectExisting: true).Tampered);
        // the process-only secret used while the key file is unreadable
        var ephemeral = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        Assert.True(new PlayLedger(ledgerFile, ephemeral, expectExisting: true).Tampered);
        // A null key skips the check entirely: this is why the shell must never hand null to a ledger (Hud.ParentalKey falls back to the ephemeral secret).
        Assert.False(new PlayLedger(ledgerFile, null, expectExisting: true).Tampered);
    }

    [Fact]
    public void A_failure_that_goes_away_is_retried_and_gives_the_same_key()
    {
        var (first, _) = KeyFile.Load(KeyPath, Protect, Unprotect);
        var calls = 0;
        var sleeps = new List<TimeSpan>();
        var (key, state) = KeyFile.LoadWithRetry(KeyPath, Protect, s => ++calls <= 2 ? throw new IOException("locked") : Unprotect(s), 5, TimeSpan.FromMilliseconds(200), sleeps.Add);
        Assert.Equal(KeyFileState.Read, state);
        Assert.Equal(first, key);
        Assert.Equal(2, sleeps.Count);
    }

    [Fact]
    public void A_lasting_failure_stops_after_the_attempts_and_unreadable_is_not_retried()
    {
        KeyFile.Load(KeyPath, Protect, Unprotect);
        var calls = 0;
        var sleeps = 0;
        var failed = KeyFile.LoadWithRetry(KeyPath, Protect, _ => { calls++; throw new IOException("locked"); }, 5, TimeSpan.FromMilliseconds(200), _ => sleeps++);
        Assert.Equal(KeyFileState.Failed, failed.State);
        Assert.Equal(5, calls);
        Assert.Equal(4, sleeps);

        calls = 0; sleeps = 0;
        var unreadable = KeyFile.LoadWithRetry(KeyPath, Protect, _ => { calls++; return ""; }, 5, TimeSpan.FromMilliseconds(200), _ => sleeps++);
        Assert.Equal(KeyFileState.Unreadable, unreadable.State);
        Assert.Equal(1, calls);
        Assert.Equal(0, sleeps);
    }

    [Fact]
    public void A_ledger_that_does_not_persist_keeps_the_signed_file_as_it_is()
    {
        var ledgerFile = Path.Combine(_dir, "playtime.json");
        var keyA = new byte[32]; keyA[0] = 1;
        var day = new DateOnly(2026, 10, 28);
        var real = new PlayLedger(ledgerFile, keyA);
        real.Add(day, "g", 600);
        real.Save(day);
        var before = File.ReadAllBytes(ledgerFile);

        var memoryOnly = new PlayLedger(ledgerFile, System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), expectExisting: true, persist: false);
        Assert.True(memoryOnly.Tampered);
        memoryOnly.Add(day, "g", 60);
        memoryOnly.Save(day);
        Assert.Equal(before, File.ReadAllBytes(ledgerFile));
        Assert.Equal(60, memoryOnly.Seconds(day));
        // with the real key again, the file is intact
        Assert.False(new PlayLedger(ledgerFile, keyA, expectExisting: true).Tampered);
    }
}
