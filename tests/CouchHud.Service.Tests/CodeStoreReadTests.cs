using CouchHud.Service.State;

namespace CouchHud.Service.Tests;

/// <summary>
/// The code file held by another program (antivirus, backup, indexer) is not damaged: tried again, recovered without a restart, never reset.
/// The reset reads the file again before it moves it, never takes a taken name, and never follows or moves a folder or a link. A lockout of
/// an earlier boot never gets this boot's time when its write fails.
/// </summary>
public class CodeStoreReadTests
{
    private const string Sid = Callers.UserSid;
    private const string Damaged = "{\"version\":1,\"accounts\":{},\"extra\":1}";

    private static string ValidFile(long bootTimeMs = 1, long lockedUntilMs = 0, long lockMs = 0, int failures = 0) => $$"""
        {
          "version": 1,
          "bootTimeMs": {{bootTimeMs}},
          "accounts": {
            "{{Sid}}": {
              "couch": { "hash": "{{Codes.HashA}}", "failures": {{failures}}, "lockedUntilMs": {{lockedUntilMs}}, "lockMs": {{lockMs}} },
              "parent": { "hash": "{{Codes.HashB}}" },
              "mode": "code"
            }
          }
        }
        """;

    private static FileStream Hold(string file) => new(file, FileMode.Open, FileAccess.Read, FileShare.None);

    private static string[] SetAside(string folder) => Directory.GetFileSystemEntries(folder, CodeStore.FileName + ".broken-*");

    [Fact]
    public void A_file_held_at_the_start_is_a_read_error_not_damage_and_recovers_without_a_restart()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, CodeStore.FileName);
        File.WriteAllText(file, ValidFile());
        var clock = new FakeClock();
        var log = new CapturingSink();
        CodeStore store;
        using (Hold(file))
        {
            store = CodeStore.Open(dir.Path, log, clock, () => 1, Codes.FastVerify);
            Assert.False(store.IsAvailable);
            Assert.False(store.IsDamaged);
            Assert.True(store.IsReadError);
            Assert.Equal(CodeCheck.Unavailable, store.Verify(Sid, CodeKind.Couch, Codes.A).Result);
            // Fail-closed meanwhile, and no reset offered or done.
            Assert.True(store.IsEnrolled(Sid));
            Assert.Equal(CodeReset.NotDamaged, store.ResetDamaged(DateTimeOffset.UtcNow));
        }
        Assert.Contains(log.Lines, l => l.StartsWith("E [codes] the code file cannot be read right now", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Lines, l => l.Contains("not valid", StringComparison.Ordinal));
        Assert.Empty(SetAside(dir.Path));
        // Free again, but not tried before the next retry is due.
        Assert.False(store.IsAvailable);
        clock.Advance(CodeStore.ReadRetryEvery);
        Assert.True(store.IsAvailable);
        Assert.False(store.IsReadError);
        Assert.Equal(CodeCheck.Granted, store.Verify(Sid, CodeKind.Couch, Codes.A).Result);
        Assert.Contains("I [codes] the code file can be read again: codes are checked again", log.Lines);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task A_file_held_only_for_a_moment_is_read_by_the_retries_at_the_start()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, CodeStore.FileName);
        File.WriteAllText(file, ValidFile());
        var held = Hold(file);
        var release = Task.Run(async () =>
        {
            await Task.Delay(20);
            held.Dispose();
        });
        var store = CodeStore.Open(dir.Path, new CapturingSink(), new FakeClock(), () => 1, Codes.FastVerify);
        await release;
        Assert.True(store.IsAvailable);
        Assert.True(store.Has(Sid, CodeKind.Parent));
    }

    [Fact]
    public void A_file_that_was_held_and_is_invalid_when_read_again_counts_as_damaged()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, CodeStore.FileName);
        File.WriteAllText(file, Damaged);
        var clock = new FakeClock();
        CodeStore store;
        using (Hold(file)) store = CodeStore.Open(dir.Path, new CapturingSink(), clock, () => 1, Codes.FastVerify);
        Assert.False(store.IsDamaged);
        clock.Advance(CodeStore.ReadRetryEvery);
        Assert.True(store.IsDamaged);
        Assert.False(store.IsAvailable);
    }

    [Fact]
    public void The_reset_reads_the_file_again_and_loads_it_when_it_is_valid_now()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, CodeStore.FileName);
        File.WriteAllText(file, Damaged);
        var log = new CapturingSink();
        var store = CodeStore.Open(dir.Path, log, new FakeClock(), () => 1, Codes.FastVerify);
        Assert.True(store.IsDamaged);
        File.WriteAllText(file, ValidFile());
        Assert.Equal(CodeReset.Recovered, store.ResetDamaged(DateTimeOffset.UtcNow));
        Assert.True(store.IsAvailable);
        Assert.True(store.Has(Sid, CodeKind.Couch));
        Assert.Empty(SetAside(dir.Path));
        Assert.Contains("I [codes] the code file is valid now: loaded, not set aside", log.Lines);
    }

    [Fact]
    public void The_reset_never_moves_a_file_it_cannot_read_right_now()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, CodeStore.FileName);
        File.WriteAllText(file, Damaged);
        var clock = new FakeClock();
        var store = CodeStore.Open(dir.Path, new CapturingSink(), clock, () => 1, Codes.FastVerify);
        Assert.True(store.IsDamaged);
        using (Hold(file)) Assert.Equal(CodeReset.Failed, store.ResetDamaged(DateTimeOffset.UtcNow));
        Assert.Empty(SetAside(dir.Path));
        Assert.Equal(Damaged, File.ReadAllText(file));
        Assert.False(store.IsDamaged);
        Assert.True(store.IsReadError);
        // Read again later: still invalid, damaged again, and the reset works then.
        clock.Advance(CodeStore.ReadRetryEvery);
        Assert.True(store.IsDamaged);
        Assert.Equal(CodeReset.Done, store.ResetDamaged(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void A_taken_set_aside_name_is_never_overwritten()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, CodeStore.FileName);
        File.WriteAllText(file, Damaged);
        var when = new DateTimeOffset(2026, 10, 29, 1, 2, 3, 4, TimeSpan.Zero);
        var taken = file + ".broken-20261029-010203-004";
        File.WriteAllText(taken, "earlier");
        Directory.CreateDirectory(taken + "-1");
        var store = CodeStore.Open(dir.Path, new CapturingSink(), new FakeClock(), () => 1, Codes.FastVerify);
        Assert.Equal(CodeReset.Done, store.ResetDamaged(when));
        Assert.Equal("earlier", File.ReadAllText(taken));
        Assert.True(Directory.Exists(taken + "-1"));
        Assert.Equal(Damaged, File.ReadAllText(taken + "-2"));
        Assert.True(store.IsAvailable);
    }

    [Fact]
    public void A_folder_named_like_the_code_file_is_never_reset_or_moved()
    {
        using var dir = new TempDir();
        var folder = Path.Combine(dir.Path, CodeStore.FileName);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "inside.txt"), "x");
        var log = new CapturingSink();
        var store = CodeStore.Open(dir.Path, log, new FakeClock(), () => 1, Codes.FastVerify);
        Assert.False(store.IsAvailable);
        Assert.False(store.IsDamaged);
        Assert.False(store.IsReadError);
        Assert.True(store.IsEnrolled(Sid));
        Assert.Equal(CodeReset.NotDamaged, store.ResetDamaged(DateTimeOffset.UtcNow));
        Assert.Equal("x", File.ReadAllText(Path.Combine(folder, "inside.txt")));
        Assert.Empty(SetAside(dir.Path));
        Assert.Contains(log.Lines, l => l.Contains("taken by a folder", StringComparison.Ordinal));
    }

    [Fact]
    public void A_link_named_like_the_code_file_is_never_followed_reset_or_moved()
    {
        using var dir = new TempDir();
        using var elsewhere = new TempDir();
        var target = Path.Combine(elsewhere.Path, "target.json");
        File.WriteAllText(target, ValidFile());
        var link = Path.Combine(dir.Path, CodeStore.FileName);
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Creating a link needs a privilege this account may not have: nothing to check then.
            return;
        }
        var store = CodeStore.Open(dir.Path, new CapturingSink(), new FakeClock(), () => 1, Codes.FastVerify);
        // A valid file behind the link is not read.
        Assert.False(store.IsAvailable);
        Assert.False(store.IsDamaged);
        Assert.Equal(CodeReset.NotDamaged, store.ResetDamaged(DateTimeOffset.UtcNow));
        Assert.True(new FileInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.Equal(ValidFile(), File.ReadAllText(target));
        Assert.Empty(SetAside(dir.Path));
    }

    [Fact]
    public void A_missing_file_that_cannot_be_confirmed_is_a_read_error_never_an_empty_store()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, CodeStore.FileName);
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var folder = new DirectoryInfo(dir.Path);
        var deny = new System.Security.AccessControl.FileSystemAccessRule(me, System.Security.AccessControl.FileSystemRights.ListDirectory,
            System.Security.AccessControl.AccessControlType.Deny);
        var acl = folder.GetAccessControl();
        acl.AddAccessRule(deny);
        folder.SetAccessControl(acl);
        var clock = new FakeClock();
        CodeStore store;
        try
        {
            // The folder cannot be listed: "no file" is not confirmed, so the store stays closed and writes nothing.
            store = CodeStore.Open(dir.Path, new CapturingSink(), clock, () => 1, Codes.FastVerify);
            Assert.True(store.IsReadError);
            Assert.False(store.IsAvailable);
            Assert.True(store.IsEnrolled(Sid));
            Assert.Equal(CodeSet.Unavailable, store.Set(Sid, CodeKind.Parent, Codes.HashA, enroll: false));
            Assert.Equal(CodeReset.NotDamaged, store.ResetDamaged(DateTimeOffset.UtcNow));
            Assert.False(File.Exists(file));
        }
        finally
        {
            acl = folder.GetAccessControl();
            acl.RemoveAccessRule(deny);
            folder.SetAccessControl(acl);
        }
        // Listable again: the absence is confirmed on the next retry and the store works, empty.
        clock.Advance(CodeStore.ReadRetryEvery);
        Assert.True(store.IsAvailable);
        Assert.False(store.IsEnrolled(Sid));
    }

    [Fact]
    public void A_missing_data_folder_is_a_read_error_never_an_empty_store()
    {
        using var dir = new TempDir();
        var gone = Path.Combine(dir.Path, "gone");
        var store = CodeStore.Open(gone, new CapturingSink(), new FakeClock(), () => 1, Codes.FastVerify);
        Assert.True(store.IsReadError);
        Assert.False(store.IsAvailable);
        Assert.True(store.IsEnrolled(Sid));
        Assert.False(Directory.Exists(gone));
    }

    [Fact]
    public void A_folder_removed_by_an_administrator_needs_no_restart()
    {
        using var dir = new TempDir();
        var folder = Path.Combine(dir.Path, CodeStore.FileName);
        Directory.CreateDirectory(folder);
        var clock = new FakeClock();
        var log = new CapturingSink();
        var store = CodeStore.Open(dir.Path, log, clock, () => 1, Codes.FastVerify);
        Assert.False(store.IsAvailable);
        // Still there on the next retry: still closed, and said only once.
        clock.Advance(CodeStore.ReadRetryEvery);
        Assert.False(store.IsAvailable);
        Assert.Single(log.Lines, l => l.Contains("taken by a folder", StringComparison.Ordinal));
        Directory.Delete(folder);
        Assert.False(store.IsAvailable);
        clock.Advance(CodeStore.ReadRetryEvery);
        Assert.True(store.IsAvailable);
        Assert.Equal(CodeSet.Saved, store.Set(Sid, CodeKind.Couch, Codes.HashA, enroll: true));
        Assert.True(File.Exists(folder));
    }

    [Fact]
    public void A_lockout_of_an_earlier_boot_never_gets_this_boots_time_when_its_write_fails()
    {
        using var dir = new TempDir();
        var file = Path.Combine(dir.Path, CodeStore.FileName);
        // Written in boot 1: locked until 3 660 000 ms of that boot's clock, 60 s long.
        File.WriteAllText(file, ValidFile(bootTimeMs: 1, lockedUntilMs: 3_660_000, lockMs: 60_000, failures: 6));
        // The write at the start fails (the temporary file's name is taken by a folder).
        Directory.CreateDirectory(file + ".tmp");
        var clock = new FakeClock { Now = TimeSpan.FromSeconds(10) };
        var log = new CapturingSink();
        var store = CodeStore.Open(dir.Path, log, clock, () => 999_999_999, Codes.FastVerify);
        Assert.True(store.IsAvailable);
        Assert.Contains(log.Lines, l => l.StartsWith("E [codes] not saved", StringComparison.Ordinal));
        // The carried lockout holds in memory: 10 s + 60 s of this boot, not the earlier boot's 3 660 000.
        var verdict = store.Verify(Sid, CodeKind.Couch, Codes.A);
        Assert.Equal(CodeCheck.LockedOut, verdict.Result);
        Assert.True(verdict.Wait <= TimeSpan.FromSeconds(60), verdict.Wait.ToString());
        // The next write (any change) carries this boot's lockout, never the earlier boot's time under this boot's stamp.
        Directory.Delete(file + ".tmp");
        Assert.Equal(CodeSet.Saved, store.Set(Sid, CodeKind.Parent, Codes.HashA, enroll: false));
        var written = File.ReadAllText(file);
        Assert.Contains("\"lockedUntilMs\": 70000", written);
        Assert.DoesNotContain("3660000", written);
        Assert.Contains("\"bootTimeMs\": 999999999", written);
    }
}
