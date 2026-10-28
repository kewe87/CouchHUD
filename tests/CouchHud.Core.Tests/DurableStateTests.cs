using CouchHud.Core.Config;
using CouchHud.Core.Files;
using CouchHud.Core.Parental;
using CouchHud.Core.Security;

namespace CouchHud.Core.Tests;

/// <summary>
/// VM test of profiles part 2: after a hard reset config.json held only zero bytes. Saves go through a temp file written through to the
/// disk, the file before stays as .bak, and a blank or broken file is replaced by a valid .bak (the broken one kept as .broken-…), never by
/// defaults: without a usable .bak the store stays locked (fail-closed), the lock and the parental controls never switch off by themselves.
/// </summary>
public sealed class DurableStateTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("couchhud-durable-").FullName;
    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(5);

    private string ConfigFile => Path.Combine(_dir, "config.json");

    private string Backup => ConfigFile + ".bak";

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A household with the couch lock, parental controls and a child's rules, saved twice so there is a .bak.</summary>
    private ConfigStore Locked()
    {
        var store = new ConfigStore(_dir, 1, Quick);
        store.UpdateOrThrow(c =>
        {
            c.CouchLock = "code";
            c.PasscodeHash = "hash-of-the-parents";
            c.Parental.Enabled = true;
            c.Parental.WeekdayMinutes = 60;
        });
        store.UpdateOrThrow(c => c.Skin = "user:deepsea");
        return store;
    }

    public static TheoryData<string> Broken => new()
    {
        "",
        "\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0",
        "   \r\n ",
        "null",
        "{ \"couchLock\": \"code\", \"passcodeHash\": "
    };

    [Fact]
    public void A_save_keeps_a_copy_as_bak_and_leaves_no_temp_file()
    {
        Locked();

        Assert.True(File.Exists(Backup));
        var before = ConfigStore.ParseUsable(File.ReadAllText(Backup));
        Assert.Equal("code", before.CouchLock);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Theory]
    [MemberData(nameof(Broken))]
    public void A_blank_or_broken_file_is_replaced_by_the_backup_and_kept_aside(string content)
    {
        Locked();
        File.WriteAllText(ConfigFile, content);

        var store = new ConfigStore(_dir, 1, Quick);

        Assert.Null(store.Fault);
        Assert.True(store.RestoredFromBackup);
        // The lock and the rules are still there: fail-closed through the backup.
        Assert.Equal("code", store.Current.CouchLock);
        Assert.Equal("hash-of-the-parents", store.Current.PasscodeHash);
        Assert.True(store.Current.Parental.Enabled);
        Assert.Equal(60, store.Current.Parental.WeekdayMinutes);
        // The broken file is not overwritten silently: it lies next to it, unchanged.
        var aside = Assert.Single(Directory.GetFiles(_dir, "config.json.broken-*"));
        Assert.Equal(content, File.ReadAllText(aside));
        // config.json holds the backup's content again, and it saves normally.
        Assert.Equal("code", ConfigStore.ParseUsable(File.ReadAllText(ConfigFile)).CouchLock);
        Assert.True(store.Update(c => c.Sounds = false));
        Assert.False(new ConfigStore(_dir, 1, Quick).Current.Sounds);
    }

    [Theory]
    [MemberData(nameof(Broken))]
    public void Without_a_usable_backup_a_broken_file_runs_locked_and_is_left_alone(string content)
    {
        File.WriteAllText(ConfigFile, content);

        var store = new ConfigStore(_dir, 1, Quick);

        Assert.Equal(ConfigFault.Invalid, store.Fault);
        Assert.False(store.RestoredFromBackup);
        // Never "no lock, no parental controls": the stand-in locks and lets no game start.
        Assert.Equal("windows", store.Current.CouchLock);
        Assert.True(store.Current.ParentalEnabled);
        Assert.True(store.Current.Parental.BlockUnknownAge);
        store.Update(c => c.Sounds = false);
        Assert.Equal(content, File.ReadAllText(ConfigFile));
        Assert.Single(Directory.GetFiles(_dir, "config.json.broken-*"));
    }

    [Fact]
    public void A_broken_backup_does_not_help_and_the_store_stays_locked()
    {
        File.WriteAllText(ConfigFile, "\0\0\0\0");
        File.WriteAllText(Backup, "\0\0");

        var store = new ConfigStore(_dir, 1, Quick);

        Assert.Equal(ConfigFault.Invalid, store.Fault);
        Assert.Equal("windows", store.Current.CouchLock);
        Assert.Equal("\0\0\0\0", File.ReadAllText(ConfigFile));
    }

    [Fact]
    public void A_file_lost_between_the_two_renames_of_a_save_comes_back_from_the_backup()
    {
        Locked();
        File.Delete(ConfigFile);

        var store = new ConfigStore(_dir, 1, Quick);

        Assert.Null(store.Fault);
        Assert.True(store.RestoredFromBackup);
        Assert.Equal("code", store.Current.CouchLock);
        Assert.True(File.Exists(ConfigFile));
    }

    [Fact]
    public void A_restored_file_is_not_restored_again_on_the_next_start()
    {
        Locked();
        File.WriteAllText(ConfigFile, "\0\0\0");
        _ = new ConfigStore(_dir, 1, Quick);

        var next = new ConfigStore(_dir, 1, Quick);

        Assert.False(next.RestoredFromBackup);
        Assert.Single(Directory.GetFiles(_dir, "config.json.broken-*"));
    }

    [Fact]
    public void Blank_means_empty_white_space_or_zero_bytes_only()
    {
        Assert.True(DurableFile.IsBlank(null));
        Assert.True(DurableFile.IsBlank(""));
        Assert.True(DurableFile.IsBlank("\0\0\0"));
        Assert.True(DurableFile.IsBlank(" \r\n\t\0"));
        Assert.False(DurableFile.IsBlank("{}"));
        Assert.False(DurableFile.IsBlank("\0x"));
    }

    [Fact]
    public void The_durable_write_refreshes_the_bak_after_every_save_and_reads_it_back_when_the_file_is_blank()
    {
        var file = Path.Combine(_dir, "state.json");
        DurableFile.WriteAllText(file, "first");
        // The good copy exists from the first save on and is never older than the file (playtime.json.bak was seen a save behind).
        Assert.Equal("first", File.ReadAllText(DurableFile.BackupOf(file)));
        DurableFile.WriteAllText(file, "second");
        Assert.Equal("second", File.ReadAllText(file));
        Assert.Equal("second", File.ReadAllText(DurableFile.BackupOf(file)));
        Assert.True(File.GetLastWriteTimeUtc(DurableFile.BackupOf(file)) >= File.GetLastWriteTimeUtc(file));

        File.WriteAllText(file, "\0\0\0\0");
        Assert.Equal("second", DurableFile.ReadValid(file, _ => true, out var fromBackup));
        Assert.True(fromBackup);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void A_wrong_code_counter_full_of_zero_bytes_takes_its_copy()
    {
        var now = DateTimeOffset.UtcNow;
        var file = Path.Combine(_dir, "lockout-profile-abc.json");
        var first = new Lockout(() => now, file);
        for (var i = 0; i < 5; i++) first.Failed();
        first.Failed();
        Assert.True(first.Wait > TimeSpan.Zero);
        Assert.True(File.Exists(DurableFile.BackupOf(file)));

        File.WriteAllText(file, "\0\0\0\0\0\0");
        var reloaded = new Lockout(() => now, file);

        // The copy from before the last wrong code: still waiting, no fresh set of tries.
        Assert.True(reloaded.Wait > TimeSpan.Zero);
    }

    [Fact]
    public void A_wrong_code_counter_that_cannot_be_read_at_all_gives_no_fresh_tries()
    {
        var now = DateTimeOffset.UtcNow;
        var file = Path.Combine(_dir, "lockout-profile-abc.json");
        File.WriteAllText(file, "\0\0\0");

        var lockout = new Lockout(() => now, file);
        Assert.Equal(TimeSpan.Zero, lockout.Wait);
        lockout.Failed();

        // The next wrong code starts the wait at once (not after five more).
        Assert.True(lockout.Wait > TimeSpan.Zero);
    }

    [Fact]
    public void A_play_time_file_full_of_zero_bytes_takes_its_copy_and_is_no_tampering()
    {
        var key = new byte[32];
        var file = Path.Combine(_dir, "playtime.json");
        var today = new DateOnly(2026, 10, 27);
        var ledger = new PlayLedger(file, key);
        ledger.Add(today, "steam:1", 600);
        ledger.Save(today);
        ledger.Add(today, "steam:1", 60);
        ledger.Save(today);

        File.WriteAllText(file, new string('\0', 64));
        var reloaded = new PlayLedger(file, key, expectExisting: true);

        Assert.False(reloaded.Tampered);
        // The copy is refreshed after every save: the last minute is not given back.
        Assert.Equal(660, reloaded.Seconds(today));
    }

    [Fact]
    public void Deleting_a_profile_renames_its_play_time_copy_too()
    {
        var id = "abc123";
        var file = ProfileIds.LedgerFile(_dir, id);
        File.WriteAllText(file, "{}");
        File.WriteAllText(DurableFile.BackupOf(file), "{}");
        var now = new DateTime(2026, 10, 27, 20, 15, 0);

        var target = ProfileIds.RetireLedger(_dir, id, now);

        Assert.Equal(file + ".deleted-20261027-201500", target);
        Assert.False(File.Exists(file));
        Assert.False(File.Exists(DurableFile.BackupOf(file)));
        Assert.True(File.Exists(DurableFile.BackupOf(file) + ".deleted-20261027-201500"));
        // A lone copy (the file itself already gone) goes as well, so no "missing account" is left for the id.
        File.WriteAllText(DurableFile.BackupOf(file), "{}");
        Assert.Null(ProfileIds.RetireLedger(_dir, id, now));
        Assert.False(File.Exists(DurableFile.BackupOf(file)));
        Assert.False(PlayLedger.ShouldExist(file, started: false));
    }

    [Fact]
    public void Parental_controls_switched_on_stay_on_when_the_file_goes_missing()
    {
        var store = new ConfigStore(_dir, 1, Quick);
        store.UpdateOrThrow(c => c.Skin = "user:deepsea");
        store.UpdateOrThrow(c => c.Parental.Enabled = true);
        File.Delete(ConfigFile);

        var next = new ConfigStore(_dir, 1, Quick);

        Assert.True(next.RestoredFromBackup);
        Assert.True(next.Current.Parental.Enabled);
        // The copy is written after every save, so it is the state after the stricter change, not the one before.
        Assert.True(ConfigStore.ParseUsable(File.ReadAllText(Backup)).Parental.Enabled);
    }

    [Fact]
    public void A_newer_temp_file_of_a_cut_off_save_wins_over_the_older_backup()
    {
        Locked();
        File.WriteAllText(Backup, """{"couchLock":"off"}""");
        File.SetLastWriteTimeUtc(Backup, DateTime.UtcNow.AddMinutes(-5));
        var temp = ConfigFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temp, """{"couchLock":"code","passcodeHash":"newer","parental":{"enabled":true}}""");
        File.Delete(ConfigFile);

        var store = new ConfigStore(_dir, 1, Quick);

        Assert.Equal("code", store.Current.CouchLock);
        Assert.Equal("newer", store.Current.PasscodeHash);
        Assert.True(store.Current.Parental.Enabled);
        // The winner became the .bak as well.
        Assert.Equal("newer", ConfigStore.ParseUsable(File.ReadAllText(Backup)).PasscodeHash);
    }

    [Fact]
    public void An_older_or_broken_temp_file_does_not_win_and_old_ones_are_cleaned()
    {
        Locked();
        var old = ConfigFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(old, """{"couchLock":"off"}""");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddMinutes(-10));
        var cut = ConfigFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(cut, "\0\0\0");
        File.SetLastWriteTimeUtc(cut, DateTime.UtcNow.AddMinutes(-10));
        File.Delete(ConfigFile);

        var store = new ConfigStore(_dir, 1, Quick);

        Assert.Equal("code", store.Current.CouchLock);
        Assert.False(File.Exists(old));
        Assert.False(File.Exists(cut));
    }

    [Fact]
    public void The_restore_leaves_a_marker_that_the_hud_reads_once()
    {
        Locked();
        File.WriteAllText(ConfigFile, "\0\0\0");
        _ = new ConfigStore(_dir, 1, Quick); // another CouchHUD process restores first

        var hud = new ConfigStore(_dir, 1, Quick);
        Assert.False(hud.RestoredFromBackup);
        var notice = hud.ReadRestoredNotice();

        Assert.NotNull(notice);
        Assert.Equal("config.json.bak", notice!.Source);
        Assert.StartsWith("config.json.broken-", notice.Broken);
        // It stays until the HUD has shown it.
        Assert.NotNull(hud.ReadRestoredNotice());
        hud.ClearRestoredNotice();
        Assert.Null(hud.ReadRestoredNotice());
    }

    [Fact]
    public void Broken_copies_are_kept_to_the_limit_old_bad_ones_included()
    {
        for (var i = 0; i < ConfigStore.MaxBadCopies + 2; i++)
        {
            var bad = Path.Combine(_dir, $"config.json.{i}.bad");
            File.WriteAllText(bad, "x" + i);
            File.SetLastWriteTimeUtc(bad, DateTime.UtcNow.AddHours(-i - 1));
        }
        Locked();
        File.WriteAllText(ConfigFile, "\0\0");

        _ = new ConfigStore(_dir, 1, Quick);

        var left = Directory.GetFiles(_dir, "config.json.broken-*").Concat(Directory.GetFiles(_dir, "config.json.*.bad")).ToList();
        Assert.Equal(ConfigStore.MaxBadCopies, left.Count);
        Assert.Single(Directory.GetFiles(_dir, "config.json.broken-*"));
    }

    [Fact]
    public void A_wrong_code_counter_holding_only_null_gives_no_fresh_tries()
    {
        var now = DateTimeOffset.UtcNow;
        var file = Path.Combine(_dir, "lockout-profile-abc.json");
        File.WriteAllText(file, "null");

        var lockout = new Lockout(() => now, file);
        lockout.Failed();

        Assert.True(lockout.Wait > TimeSpan.Zero);
    }

    [Fact]
    public void A_play_time_file_holding_only_null_takes_its_copy_or_counts_as_tampering()
    {
        var key = new byte[32];
        var file = Path.Combine(_dir, "playtime.json");
        var today = new DateOnly(2026, 10, 27);
        var ledger = new PlayLedger(file, key);
        ledger.Add(today, "steam:1", 600);
        ledger.Save(today);
        ledger.Add(today, "steam:1", 60);
        ledger.Save(today);

        File.WriteAllText(file, "null");
        var reloaded = new PlayLedger(file, key, expectExisting: true);
        Assert.False(reloaded.Tampered);
        Assert.Equal(660, reloaded.Seconds(today));

        // Without a copy "null" is no empty account: today counts as used up.
        File.Delete(DurableFile.BackupOf(file));
        Assert.True(new PlayLedger(file, key, expectExisting: true).Tampered);
    }

    [Fact]
    public void Peek_reads_without_writing_restoring_or_cleaning_anything()
    {
        Locked();
        File.WriteAllText(ConfigFile, "\0\0\0");
        var leftover = ConfigFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(leftover, "{}");
        File.SetLastWriteTimeUtc(leftover, DateTime.UtcNow.AddHours(-1));
        var before = Directory.GetFiles(_dir).OrderBy(f => f).Select(f => (f, File.ReadAllText(f), File.GetLastWriteTimeUtc(f))).ToList();

        var peeked = ConfigStore.Peek(_dir);

        // The broken file is not replaced, nothing is put aside, no marker, the temp file stays: the .bak was only read.
        Assert.Equal("code", peeked!.CouchLock);
        var after = Directory.GetFiles(_dir).OrderBy(f => f).Select(f => (f, File.ReadAllText(f), File.GetLastWriteTimeUtc(f))).ToList();
        Assert.Equal(before, after);
    }

    [Fact]
    public void Peek_gives_null_for_nothing_usable_and_for_a_missing_folder()
    {
        Assert.Null(ConfigStore.Peek(_dir));
        File.WriteAllText(ConfigFile, "null");
        Assert.Null(ConfigStore.Peek(_dir));
        Assert.Null(ConfigStore.Peek(Path.Combine(_dir, "missing")));
        Assert.Equal("null", File.ReadAllText(ConfigFile));
        Assert.Empty(Directory.GetFiles(_dir, "config.json.broken-*"));
    }

    [JunctionFact]
    public void Peek_does_not_follow_a_link_in_place_of_the_folder()
    {
        Locked();
        var link = Path.Combine(Path.GetTempPath(), "couchhud-peek-" + Guid.NewGuid().ToString("N"));
        Links.Junction(link, _dir);
        try
        {
            Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint));
            Assert.Null(ConfigStore.Peek(link));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void Peek_reads_no_settings_file_beyond_the_cap()
    {
        Locked();
        var text = File.ReadAllText(ConfigFile);
        // Valid JSON just below the cap is read; one byte more of the same and it is passed over (the .bak is read instead).
        var padded = text.TrimEnd().TrimEnd('}') + new string(' ', ConfigStore.MaxPeekBytes - text.Length - 16) + "}";
        File.WriteAllText(ConfigFile, padded);
        Assert.Equal("user:deepsea", ConfigStore.Peek(_dir)!.Skin);
        File.WriteAllText(Backup, text.Replace("user:deepsea", "user:nova"));
        File.WriteAllText(ConfigFile, padded + new string(' ', 32));
        Assert.True(new FileInfo(ConfigFile).Length > ConfigStore.MaxPeekBytes);
        Assert.Equal("user:nova", ConfigStore.Peek(_dir)!.Skin);
        File.Delete(Backup);
        Assert.Null(ConfigStore.Peek(_dir));
    }

    [FileLinkFact]
    public void Peek_does_not_follow_config_json_as_a_link()
    {
        Locked();
        var outside = Path.Combine(_dir, "outside.json");
        // The file the link points to says something else than the .bak (which holds the last saved state, deepsea).
        File.WriteAllText(outside, File.ReadAllText(ConfigFile).Replace("user:deepsea", "user:neoncity"));
        File.Delete(ConfigFile);
        File.CreateSymbolicLink(ConfigFile, outside);
        Assert.True(File.GetAttributes(ConfigFile).HasFlag(FileAttributes.ReparsePoint));
        Assert.Contains("user:neoncity", File.ReadAllText(outside));

        // The link is passed over: the .bak is what is read, never the file the link points to.
        Assert.Equal("user:deepsea", ConfigStore.Peek(_dir)!.Skin);

        File.Delete(Backup);
        Assert.Null(ConfigStore.Peek(_dir));
    }
}

/// <summary>Links for the tests: a junction needs no rights, a symbolic link to a file needs developer mode or elevation.</summary>
internal static class Links
{
    /// <summary>A junction (directory mount point) at <paramref name="link"/> to <paramref name="target"/>, through mklink /J.</summary>
    public static void Junction(string link, string target)
    {
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in new[] { "/d", "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !Directory.Exists(link))
            throw new IOException($"mklink /J failed ({process.ExitCode}): {(output + error.Result).Trim()}");
    }

    public static readonly bool JunctionsWork = Probe(dir =>
    {
        var link = Path.Combine(dir, "junction");
        var target = Directory.CreateDirectory(Path.Combine(dir, "target")).FullName;
        Junction(link, target);
        Directory.Delete(link);
    });

    public static readonly bool FileLinksWork = Probe(dir =>
    {
        var target = Path.Combine(dir, "target");
        File.WriteAllText(target, "");
        File.CreateSymbolicLink(Path.Combine(dir, "link"), target);
    });

    private static bool Probe(Action<string> create)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var dir = Directory.CreateTempSubdirectory("couchhud-link-probe-").FullName;
        try
        {
            create(dir);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

/// <summary>A fact shown as skipped (not silently green) when no junction can be made here.</summary>
public sealed class JunctionFactAttribute : FactAttribute
{
    public JunctionFactAttribute()
    {
        if (!Links.JunctionsWork) Skip = "no junction could be made in the temp folder";
    }
}

/// <summary>A fact shown as skipped (not silently green) without the right to make symbolic links (developer mode or elevation).</summary>
public sealed class FileLinkFactAttribute : FactAttribute
{
    public FileLinkFactAttribute()
    {
        if (!Links.FileLinksWork) Skip = "symbolic links to files need developer mode or an elevated shell";
    }
}
