using CouchHud.Core.Config;
using CouchHud.Core.Files;

namespace CouchHud.Core.Tests;

/// <summary>
/// VM test with hard resets right after saving the settings: the content of every temp file was on the disk, the last rename was not.
/// config.json was new and the .bak old (or, the same way, config.json valid but old), with the complete newer temp file next to it,
/// which the next start deleted as "leftover". Now the renames are flushed, and a complete newer temp file is taken instead of deleted.
/// </summary>
public sealed class DurableRenameTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("couchhud-rename-").FullName;
    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(5);

    private string ConfigFile => Path.Combine(_dir, "config.json");

    private string Backup => ConfigFile + ".bak";

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.GetFiles(_dir)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private const string Old = """{"couchLock":"off","parental":{"enabled":false}}""";
    private const string Stricter = """{"couchLock":"code","passcodeHash":"newer","parental":{"enabled":true}}""";

    /// <summary>"This boot" started two minutes ago: temp files older than that are from before the reset.</summary>
    private ConfigStore Store() => new(_dir, 1, Quick, bootTime: () => DateTime.UtcNow - TimeSpan.FromMinutes(2));

    [Fact]
    public void A_complete_newer_temp_file_from_this_boot_is_neither_taken_nor_deleted()
    {
        // It may belong to a save another CouchHUD process is running right now (written, not yet renamed): taking it could lay it
        // over a stricter state saved since. A rename is only lost to a reset, so only files from before the boot count.
        Write(ConfigFile, Old, TimeSpan.FromMinutes(10));
        var running = TempOf(ConfigFile);
        Write(running, Stricter, TimeSpan.FromSeconds(30));
        var earlierThisBoot = TempOf(ConfigFile);
        Write(earlierThisBoot, Stricter, TimeSpan.FromSeconds(90));

        var store = Store();

        Assert.Equal("off", store.Current.CouchLock);
        Assert.Equal(Old, File.ReadAllText(ConfigFile));
        Assert.True(File.Exists(running));
        // Older than a running save can be and not a lost rename: a failed save of this boot, cleaned like before.
        Assert.False(File.Exists(earlierThisBoot));
    }

    [Fact]
    public void A_temp_file_dated_in_the_future_is_never_taken_and_goes()
    {
        Write(ConfigFile, Old, TimeSpan.FromMinutes(10));
        var future = TempOf(ConfigFile);
        File.WriteAllText(future, Stricter);
        File.SetLastWriteTimeUtc(future, DateTime.UtcNow.AddHours(5));
        var futureBak = TempOf(Backup);
        File.WriteAllText(futureBak, Stricter);
        File.SetLastWriteTimeUtc(futureBak, DateTime.UtcNow.AddHours(5));

        var store = Store();

        Assert.Equal("off", store.Current.CouchLock);
        Assert.False(File.Exists(future));
        Assert.False(File.Exists(futureBak));
    }

    [Fact]
    public void A_taken_temp_file_is_removed_at_once_and_does_not_win_again_over_a_later_save()
    {
        Write(ConfigFile, Old, TimeSpan.FromMinutes(10));
        var temp = TempOf(ConfigFile);
        Write(temp, Stricter, TimeSpan.FromMinutes(5));
        var store = Store();
        Assert.False(File.Exists(temp));

        store.UpdateOrThrow(c => c.Skin = "later");
        Assert.Equal("later", Store().Current.Skin);
    }

    private static string TempOf(string file) => file + "." + Guid.NewGuid().ToString("N") + ".tmp";

    private static void Write(string path, string text, TimeSpan age)
    {
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
    }

    [Fact]
    public void A_valid_but_older_config_loses_against_the_complete_temp_file_of_the_last_save()
    {
        Write(ConfigFile, Old, TimeSpan.FromMinutes(10));
        Write(Backup, Old, TimeSpan.FromMinutes(10));
        var temp = TempOf(ConfigFile);
        Write(temp, Stricter, TimeSpan.FromMinutes(5));

        var store = Store();

        // The stricter rule saved right before the reset counts, and it is on the disk now: in config.json and in the .bak.
        Assert.Equal("code", store.Current.CouchLock);
        Assert.True(store.Current.Parental.Enabled);
        Assert.Null(store.Fault);
        Assert.False(store.RestoredFromBackup);
        Assert.Equal("newer", ConfigStore.ParseUsable(File.ReadAllText(ConfigFile)).PasscodeHash);
        Assert.Equal("newer", ConfigStore.ParseUsable(File.ReadAllText(Backup)).PasscodeHash);
        // Taken, so no longer newer than config.json: cleaned up like any old temp file.
        Assert.False(File.Exists(temp));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void An_older_bak_loses_against_the_complete_temp_file_of_its_refresh()
    {
        // What the VM showed: config.json new, the .bak old, its temp file complete and newer.
        Write(ConfigFile, Stricter, TimeSpan.FromMinutes(5));
        Write(Backup, Old, TimeSpan.FromMinutes(10));
        var temp = TempOf(Backup);
        Write(temp, Stricter, TimeSpan.FromMinutes(4));

        var store = Store();

        Assert.Equal("code", store.Current.CouchLock);
        Assert.Equal("newer", ConfigStore.ParseUsable(File.ReadAllText(Backup)).PasscodeHash);
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public void Of_several_newer_temp_files_the_newest_complete_one_wins()
    {
        Write(ConfigFile, Old, TimeSpan.FromMinutes(10));
        Write(TempOf(ConfigFile), """{"couchLock":"code","passcodeHash":"middle"}""", TimeSpan.FromMinutes(6));
        Write(TempOf(ConfigFile), Stricter, TimeSpan.FromMinutes(5));
        // Newest, but cut off while it was written: not a save.
        Write(TempOf(ConfigFile), """{ "couchLock": "code", "passcodeHash": """, TimeSpan.FromMinutes(4));

        var store = Store();

        Assert.Equal("newer", store.Current.PasscodeHash);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void A_newer_temp_file_that_is_cut_off_or_blank_is_no_save_and_goes()
    {
        Write(ConfigFile, Stricter, TimeSpan.FromMinutes(10));
        Write(Backup, Stricter, TimeSpan.FromMinutes(10));
        var cut = TempOf(ConfigFile);
        Write(cut, """{"couchLock":"off","parental":""", TimeSpan.FromMinutes(5));
        var blank = TempOf(Backup);
        Write(blank, "\0\0\0\0", TimeSpan.FromMinutes(5));
        var nul = TempOf(ConfigFile);
        Write(nul, "null", TimeSpan.FromMinutes(5));

        var store = Store();

        Assert.Equal("newer", store.Current.PasscodeHash);
        Assert.Equal(Stricter, File.ReadAllText(ConfigFile));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void An_older_valid_temp_file_does_not_win_over_a_newer_config()
    {
        Write(ConfigFile, Stricter, TimeSpan.FromMinutes(5));
        var older = TempOf(ConfigFile);
        Write(older, Old, TimeSpan.FromMinutes(10));

        var store = Store();

        Assert.Equal("code", store.Current.CouchLock);
        Assert.False(File.Exists(older));
    }

    [Fact]
    public void A_newer_temp_file_that_cannot_be_put_in_place_counts_and_is_kept_for_the_next_start()
    {
        Write(ConfigFile, Old, TimeSpan.FromMinutes(10));
        var temp = TempOf(ConfigFile);
        Write(temp, Stricter, TimeSpan.FromMinutes(5));
        // A read-only config.json cannot be replaced.
        File.SetAttributes(ConfigFile, FileAttributes.ReadOnly);

        var store = Store();

        Assert.Equal("code", store.Current.CouchLock);
        Assert.True(store.Current.Parental.Enabled);
        Assert.True(File.Exists(temp));

        File.SetAttributes(ConfigFile, FileAttributes.Normal);
        var next = Store();
        Assert.Equal("newer", next.Current.PasscodeHash);
        Assert.Equal("newer", ConfigStore.ParseUsable(File.ReadAllText(ConfigFile)).PasscodeHash);
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public void A_young_temp_file_of_a_save_running_in_the_other_process_is_not_touched()
    {
        Write(ConfigFile, Stricter, TimeSpan.FromMinutes(1));
        var running = TempOf(ConfigFile);
        // Held without sharing while it is written (DurableFile.WriteThrough): not read, not taken, not deleted.
        using (var writing = new FileStream(running, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            writing.Write("{\"couchLock\":\"off\""u8);
            var store = Store();
            Assert.Equal("code", store.Current.CouchLock);
        }
        Assert.True(File.Exists(running));
    }

    [Fact]
    public void Saving_still_works_and_flushes_while_a_reader_holds_the_file_open()
    {
        Write(ConfigFile, Stricter, TimeSpan.FromMinutes(1));
        var store = Store();
        // Our own readers share reading, writing and deleting (ConfigStore.ReadShared).
        using (new FileStream(ConfigFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            store.UpdateOrThrow(c => c.Skin = "user:deepsea");
        }
        Assert.Equal("user:deepsea", ConfigStore.ParseUsable(File.ReadAllText(ConfigFile)).Skin);
        Assert.Equal("user:deepsea", ConfigStore.ParseUsable(File.ReadAllText(Backup)).Skin);
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Saving_while_the_file_is_unusable_says_so_and_not_that_another_program_holds_it()
    {
        File.WriteAllText(ConfigFile, "{ broken");
        var store = Store();
        Assert.Equal(ConfigFault.Invalid, store.Fault);

        var ex = Assert.Throws<IOException>(() => store.UpdateOrThrow(c => c.Skin = "user:deepsea"));

        Assert.Equal(CouchHud.Core.Localization.Strings.Get("config.notSavedFault"), ex.Message);
        Assert.Equal("{ broken", File.ReadAllText(ConfigFile));
    }

    [WindowsFact]
    public void The_folder_handle_can_be_flushed()
    {
        Assert.True(DurableFile.FlushFolder(_dir));
    }

    [WindowsFact]
    public void The_swap_flushes_the_renames_with_a_reader_on_the_file_and_on_the_bak()
    {
        var file = Path.Combine(_dir, "playtime.json");
        DurableFile.WriteAllText(file, "first");
        DurableFile.WriteAllText(file, "second");
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (new FileStream(DurableFile.BackupOf(file), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            DurableFile.WriteAllText(file, "third");
            Assert.True(DurableFile.FlushRenames(file));
        }
        // The reader on the .bak blocks only ReplaceFile's step for it; the refresh after the swap replaces the open .bak itself.
        Assert.Equal("third", File.ReadAllText(file));
        Assert.Equal("third", File.ReadAllText(DurableFile.BackupOf(file)));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        // The old "second" went under a fresh name (not without one, see SwapIn) and was removed right after.
        Assert.Empty(Directory.GetFiles(_dir, "*.old"));
        // Without the readers the .bak follows as well.
        DurableFile.WriteAllText(file, "fourth");
        Assert.Equal("fourth", File.ReadAllText(DurableFile.BackupOf(file)));
    }

    [Fact]
    public void A_reader_on_the_target_alone_does_not_stop_the_swap()
    {
        var file = Path.Combine(_dir, "playtime.json");
        DurableFile.WriteAllText(file, "first");
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            DurableFile.WriteAllText(file, "second");
        }
        Assert.Equal("second", File.ReadAllText(file));
        Assert.Equal("second", File.ReadAllText(DurableFile.BackupOf(file)));
    }

    [Fact]
    public void A_config_save_with_a_reader_on_the_bak_still_lands_and_the_bak_catches_up()
    {
        Write(ConfigFile, Stricter, TimeSpan.FromMinutes(1));
        var store = Store();
        store.UpdateOrThrow(c => c.Skin = "first");
        using (new FileStream(Backup, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            store.UpdateOrThrow(c => c.Skin = "user:deepsea");
        }
        Assert.Equal("user:deepsea", ConfigStore.ParseUsable(File.ReadAllText(ConfigFile)).Skin);
        // RefreshBackup replaces the open .bak itself (that works with delete sharing).
        Assert.Equal("user:deepsea", ConfigStore.ParseUsable(File.ReadAllText(Backup)).Skin);
    }

    [Fact]
    public void A_bak_replaced_half_way_takes_the_new_copy_instead_of_losing_both()
    {
        var file = Path.Combine(_dir, "playtime.json");
        DurableFile.WriteAllText(file, "first");
        var backup = DurableFile.BackupOf(file);
        // ReplaceFile without a backup name failing with 1176: the old .bak is gone, the new copy is still under its temp name.
        void HalfWay(string temp, string target, string? _)
        {
            File.Delete(target);
            throw new IOException("Unable to move the replacement file to the file to be replaced.", unchecked((int)0x80070498));
        }
        Assert.True(DurableFile.RefreshBackup(file, "second"u8.ToArray(), HalfWay));
        Assert.Equal("second", File.ReadAllText(backup));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));

        // A failure that left the .bak in place keeps it (the next save tries again) and removes the temp file.
        void Refused(string temp, string target, string? _) => throw new IOException("sharing violation");
        Assert.False(DurableFile.RefreshBackup(file, "third"u8.ToArray(), Refused));
        Assert.Equal("second", File.ReadAllText(backup));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void A_save_removes_old_temp_files_a_reset_left_but_not_young_or_foreign_ones()
    {
        var file = Path.Combine(_dir, "playtime.json");
        string Temp(string middle, TimeSpan age)
        {
            var path = $"{file}.{middle}.tmp";
            File.WriteAllText(path, "{}");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
            return path;
        }
        var oldTemp = Temp(Guid.NewGuid().ToString("N"), TimeSpan.FromMinutes(5));
        var oldBakTemp = Temp("bak." + Guid.NewGuid().ToString("N"), TimeSpan.FromMinutes(5));
        var future = Temp("bak." + Guid.NewGuid().ToString("N"), -TimeSpan.FromDays(1));
        var young = Temp("bak." + Guid.NewGuid().ToString("N"), TimeSpan.Zero);
        var foreign = Temp("mine", TimeSpan.FromMinutes(5));
        var otherFile = Path.Combine(_dir, $"playtime-kid1.json.bak.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(otherFile, "{}");
        File.SetLastWriteTimeUtc(otherFile, DateTime.UtcNow - TimeSpan.FromMinutes(5));

        DurableFile.WriteAllText(file, "saved");

        Assert.False(File.Exists(oldTemp));
        Assert.False(File.Exists(oldBakTemp));
        Assert.False(File.Exists(future));
        // Maybe a save of the other process right now; not a name this class gives; another file's.
        Assert.True(File.Exists(young));
        Assert.True(File.Exists(foreign));
        Assert.True(File.Exists(otherFile));
        Assert.Equal("saved", File.ReadAllText(file));
        Assert.Equal("saved", File.ReadAllText(DurableFile.BackupOf(file)));
    }

    [WindowsFact]
    public void The_rename_is_flushed_even_while_a_reader_holds_the_file_without_write_sharing()
    {
        // The fallback (a handle of the file) would fail here; the folder handle does not collide with it.
        var file = Path.Combine(_dir, "lockout.json");
        File.WriteAllText(file, "x");
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        {
            Assert.True(DurableFile.FlushRenames(file));
        }
    }
}

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows only";
    }
}
