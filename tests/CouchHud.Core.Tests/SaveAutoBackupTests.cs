using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using CouchHud.Core.Config;
using CouchHud.Core.Guard;
using CouchHud.Core.Saves;

namespace CouchHud.Core.Tests;

/// <summary>
/// The automatic save backup after a game (0.5, item 4) and the path safety of backup and restore: limits, rotation, links planted in
/// save folders, other users' profiles and manipulated backups.
/// </summary>
public sealed class SaveAutoBackupTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "couchhud-autosave-" + Guid.NewGuid().ToString("N"));
    private static readonly GameRef Game = new("Real Game", "steam:4242");

    public SaveAutoBackupTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try
        {
            // Junctions first, as links only (their targets must survive, and Delete would otherwise walk into them).
            foreach (var d in Directory.EnumerateDirectories(_tmp, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            {
                if (File.GetAttributes(d).HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(d);
            }
            Directory.Delete(_tmp, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private SaveContext Ctx(string? installDir = null, bool profiles = false) => new()
    {
        Home = Path.Combine(_tmp, "home"),
        WinAppData = Path.Combine(_tmp, "home", "AppData", "Roaming"),
        WinLocalAppData = Path.Combine(_tmp, "home", "AppData", "Local"),
        WinLocalAppDataLow = Path.Combine(_tmp, "home", "AppData", "LocalLow"),
        WinDocuments = Path.Combine(_tmp, "home", "Documents"),
        WinPublic = Path.Combine(_tmp, "pub"),
        WinProgramData = Path.Combine(_tmp, "pd"),
        WinDir = Path.Combine(_tmp, "win"),
        UserName = "tester",
        InstallDir = installDir,
        Store = "steam",
        SteamUserIds = ["111"],
        ProfilesDir = profiles ? _tmp : ""
    };

    private static ManifestEntry Entry(params string[] paths) =>
        new("Real Game") { Files = paths.Select(p => new ManifestPath(p, ["save"], [])).ToList() };

    private static ManifestEntry SavesEntry() => Entry("<winAppData>/RealGame/saves");

    private static string Saves(SaveContext c) => Path.Combine(c.WinAppData, "RealGame", "saves");

    private static void Write(string path, string content = "data")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private string BackupRoot => Path.Combine(_tmp, "backups");

    private SaveBackupService Service(int keep = 5, int autoKeep = 2, DateTime? start = null)
    {
        var t = start ?? new DateTime(2026, 1, 1, 10, 0, 0);
        return new SaveBackupService(BackupRoot, keep, autoKeep) { Now = () => t = t.AddMinutes(1) };
    }

    private static AutoBackupLimits Limits(long game = 1024 * 1024, long total = 64 * 1024 * 1024, int files = 100) =>
        new(game, total, files, TimeSpan.FromMinutes(1));

    /// <summary>Junctions need no privileges (mklink /J); elsewhere a symbolic link. False: the system can't make one, nothing to check.</summary>
    private static bool TryLink(string link, string target)
    {
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        if (OperatingSystem.IsWindows())
        {
            using var p = Process.Start(new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/c", "mklink", "/J", link, target }, CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
            p!.WaitForExit();
            return p.ExitCode == 0 && Directory.Exists(link);
        }
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // --- Automatic backup: limits and rotation -------------------------------

    [Fact]
    public void Automatic_backups_rotate_apart_from_the_ones_made_by_hand()
    {
        var ctx = Ctx();
        var entry = SavesEntry();
        var svc = Service(keep: 2, autoKeep: 2);
        Write(Path.Combine(Saves(ctx), "a.sav"), "manual");
        var manual = svc.Backup(Game, SaveLocator.Scan(entry, ctx)).Id!;

        var autos = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            Write(Path.Combine(Saves(ctx), "a.sav"), "auto" + i);
            File.SetLastWriteTimeUtc(Path.Combine(Saves(ctx), "a.sav"), new DateTime(2026, 1, 2, 0, i, 0, DateTimeKind.Utc));
            var r = SaveGameService.AutoBackupCore(svc, Game, entry, ctx, Limits(), CancellationToken.None);
            Assert.Equal(AutoBackupOutcome.Done, r.Outcome);
            Assert.False(r.Notify);
            autos.Add(r.BackupId!);
        }

        var left = svc.List(Game);
        Assert.Contains(left, b => b.Id == manual && !b.Auto);
        Assert.Equal(autos.TakeLast(2).Reverse(), left.Where(b => b.Auto).Select(b => b.Id));
        Assert.All(left.Where(b => b.Auto), b => Assert.Contains("_auto", b.Id));
    }

    [Fact]
    public void Nothing_written_since_the_last_backup_makes_no_new_version()
    {
        var ctx = Ctx();
        var entry = SavesEntry();
        var svc = Service();
        Write(Path.Combine(Saves(ctx), "a.sav"));
        File.SetLastWriteTimeUtc(Path.Combine(Saves(ctx), "a.sav"), new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(AutoBackupOutcome.Done, SaveGameService.AutoBackupCore(svc, Game, entry, ctx, Limits(), CancellationToken.None).Outcome);

        var again = SaveGameService.AutoBackupCore(svc, Game, entry, ctx, Limits(), CancellationToken.None);

        Assert.Equal(AutoBackupOutcome.Unchanged, again.Outcome);
        Assert.False(again.Notify);
        Assert.Single(svc.List(Game));
    }

    [Fact]
    public void Saves_over_the_per_game_limit_are_not_copied_and_are_told()
    {
        var ctx = Ctx();
        var svc = Service();
        Write(Path.Combine(Saves(ctx), "big.sav"), new string('x', 4096));

        var r = SaveGameService.AutoBackupCore(svc, Game, SavesEntry(), ctx, Limits(game: 1000), CancellationToken.None);

        Assert.Equal(AutoBackupOutcome.GameTooBig, r.Outcome);
        Assert.True(r.Notify);
        Assert.False(Directory.Exists(svc.GameDirectory(Game)) && Directory.EnumerateFiles(svc.GameDirectory(Game)).Any());
    }

    [Fact]
    public void A_folder_with_more_files_than_the_limit_is_too_big()
    {
        var ctx = Ctx();
        for (var i = 0; i < 12; i++) Write(Path.Combine(Saves(ctx), $"s{i}.sav"));

        var r = SaveGameService.AutoBackupCore(Service(), Game, SavesEntry(), ctx, Limits(files: 10), CancellationToken.None);

        Assert.Equal(AutoBackupOutcome.GameTooBig, r.Outcome);
    }

    [Fact]
    public void The_total_limit_stops_automatic_backups_but_counts_what_rotation_frees()
    {
        var ctx = Ctx();
        var entry = SavesEntry();
        var svc = Service(autoKeep: 1);
        // Hardly compressible, so the ZIPs are about as big as the saves.
        var random = new byte[20_000];
        new Random(4).NextBytes(random);
        Directory.CreateDirectory(Saves(ctx));
        File.WriteAllBytes(Path.Combine(Saves(ctx), "a.sav"), random);
        Assert.Equal(AutoBackupOutcome.Done, SaveGameService.AutoBackupCore(svc, Game, entry, ctx, Limits(total: 50_000), CancellationToken.None).Outcome);

        // Next version: the one it replaces is freed, so it still fits.
        File.SetLastWriteTimeUtc(Path.Combine(Saves(ctx), "a.sav"), DateTime.UtcNow.AddDays(1));
        Assert.Equal(AutoBackupOutcome.Done, SaveGameService.AutoBackupCore(svc, Game, entry, ctx, Limits(total: 50_000), CancellationToken.None).Outcome);

        // Another game would not fit next to it.
        var other = new GameRef("Other Game", "steam:5");
        File.SetLastWriteTimeUtc(Path.Combine(Saves(ctx), "a.sav"), DateTime.UtcNow.AddDays(2));
        var full = SaveGameService.AutoBackupCore(svc, other, entry, ctx, Limits(total: 30_000), CancellationToken.None);
        Assert.Equal(AutoBackupOutcome.TotalFull, full.Outcome);
        Assert.True(full.Notify);
        Assert.Empty(svc.List(other));
    }

    [Fact]
    public void A_stopped_backup_leaves_nothing_behind()
    {
        var ctx = Ctx();
        Write(Path.Combine(Saves(ctx), "a.sav"));
        var svc = Service();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var r = svc.Backup(Game, SaveLocator.Scan(SavesEntry(), ctx), new BackupOptions(Auto: true, Cancel: cts.Token));

        Assert.True(r.Cancelled);
        Assert.Null(r.Id);
        Assert.Empty(Directory.EnumerateFiles(svc.GameDirectory(Game)));
        // The scan stops first (AutoBackupAsync turns it into "timed out").
        Assert.Throws<OperationCanceledException>(() => SaveGameService.AutoBackupCore(svc, Game, SavesEntry(), ctx, Limits(), cts.Token));
    }

    [Fact]
    public void The_scan_honours_the_time_limit()
    {
        var ctx = Ctx();
        Write(Path.Combine(Saves(ctx), "a.sav"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => SaveLocator.Scan(SavesEntry(), ctx, 100, cts.Token));
    }

    [Fact]
    public void A_folder_that_grows_while_it_is_copied_ends_the_backup()
    {
        var ctx = Ctx();
        Write(Path.Combine(Saves(ctx), "a.sav"), new string('a', 3000));
        var scan = SaveLocator.Scan(SavesEntry(), ctx);
        // The scan saw 3000 bytes; the file now has 30000 (a game or a mod keeps writing).
        File.WriteAllText(Path.Combine(Saves(ctx), "a.sav"), new string('a', 30000));
        var svc = Service();

        var r = svc.Backup(Game, scan, new BackupOptions(Auto: true, MaxBytes: 10000));

        Assert.True(r.LimitReached);
        Assert.Null(r.Id);
        Assert.Empty(Directory.EnumerateFiles(svc.GameDirectory(Game)));
    }

    [Fact]
    public void Copying_stops_at_the_limit()
    {
        using var source = new MemoryStream(new byte[5000]);
        using var target = new MemoryStream();
        Assert.Throws<BackupLimitException>(() => SaveBackupService.CopyLimited(source, target, 0, 4000, CancellationToken.None));
        Assert.True(target.Length <= 4000);
    }

    [Fact]
    public void Incomplete_automatic_backups_do_not_pile_up()
    {
        var ctx = Ctx();
        var entry = SavesEntry();
        var svc = Service(autoKeep: 2);
        Write(Path.Combine(Saves(ctx), "a.sav"));
        for (var i = 0; i < 5; i++)
        {
            var scan = SaveLocator.Scan(entry, ctx) with { Errors = ["unreadable"] };
            Assert.False(svc.Backup(Game, scan, new BackupOptions(Auto: true)).Complete);
        }
        Assert.Equal(2, svc.List(Game).Count(b => b is { Auto: true, Complete: false }));
    }

    [Fact]
    public void Limits_come_from_the_settings_and_stay_in_range()
    {
        var limits = AutoBackupLimits.From(new AppConfig { AutoBackupMaxGameMb = 1, AutoBackupMaxTotalMb = int.MaxValue });
        Assert.Equal(SaveGameService.MinGameMb * 1_000_000L, limits.MaxGameBytes);
        Assert.Equal(SaveGameService.MaxTotalMb * 1_000_000L, limits.MaxTotalBytes);
        Assert.Equal(AutoBackupLimits.DefaultTimeout, limits.Timeout);
        Assert.True(new AppConfig().AutoBackupSaves);
    }

    [Theory]
    [InlineData(AutoBackupOutcome.Done, false)]
    [InlineData(AutoBackupOutcome.Unchanged, false)]
    [InlineData(AutoBackupOutcome.Skipped, false)]
    [InlineData(AutoBackupOutcome.GameTooBig, true)]
    [InlineData(AutoBackupOutcome.TotalFull, true)]
    [InlineData(AutoBackupOutcome.TimedOut, true)]
    [InlineData(AutoBackupOutcome.Incomplete, true)]
    [InlineData(AutoBackupOutcome.Failed, true)]
    [InlineData(AutoBackupOutcome.OnlineOnly, false)]
    public void Only_failures_and_limits_are_told(AutoBackupOutcome outcome, bool told) =>
        Assert.Equal(told, new AutoBackupResult(outcome).Notify);

    [Theory]
    [InlineData("getSaveInfo")]
    [InlineData("backupSaves")]
    [InlineData("restoreSaves")]
    [InlineData("deleteSaveBackup")]
    [InlineData("getSaveSettings")]
    [InlineData("setSaveSetting")]
    public void Save_game_calls_need_the_unlock(string method) => Assert.DoesNotContain(method, LockPolicy.OpenWhileLocked);

    // --- Paths: links, other profiles, the backup folder ------------------------

    [Fact]
    public void A_junction_in_a_save_folder_is_not_followed_by_the_backup()
    {
        var ctx = Ctx();
        var secret = Path.Combine(_tmp, "elsewhere");
        Write(Path.Combine(secret, "private.txt"), "secret");
        Write(Path.Combine(Saves(ctx), "a.sav"));
        if (!TryLink(Path.Combine(Saves(ctx), "linked"), secret)) return;

        // Also when the link is named directly by the manifest (the enumeration's own skip does not see the start folder).
        var scan = SaveLocator.Scan(Entry("<winAppData>/RealGame/saves", "<winAppData>/RealGame/saves/linked"), ctx);

        Assert.DoesNotContain(scan.Files, f => f.FullPath.Contains("private.txt"));
        Assert.Contains(scan.Files, f => f.Rel.EndsWith("a.sav"));
        Assert.Contains(scan.Errors, e => e.Contains("private.txt"));
    }

    [Fact]
    public void A_save_folder_that_is_itself_a_junction_is_not_backed_up()
    {
        var ctx = Ctx();
        var secret = Path.Combine(_tmp, "elsewhere");
        Write(Path.Combine(secret, "private.txt"), "secret");
        if (!TryLink(Saves(ctx), secret)) return;

        var scan = SaveLocator.Scan(SavesEntry(), ctx);

        Assert.Empty(scan.Files);
    }

    [Fact]
    public void Another_users_profile_is_neither_read_nor_written()
    {
        var install = Path.Combine(_tmp, "other", "Games", "RealGame");
        var ctx = Ctx(install, profiles: true);
        Write(Path.Combine(install, "save.dat"), "theirs");
        Write(Path.Combine(Saves(ctx), "a.sav"));
        var entry = Entry("<base>/save.dat", "<winAppData>/RealGame/saves");

        var scan = SaveLocator.Scan(entry, ctx);
        Assert.DoesNotContain(scan.Files, f => f.FullPath.EndsWith("save.dat"));
        Assert.Contains(scan.Files, f => f.FullPath.EndsWith("a.sav"));

        Assert.True(SavePathGuard.IsInForeignProfile(Path.Combine(_tmp, "other", "x"), ctx));
        Assert.False(SavePathGuard.IsInForeignProfile(Path.Combine(_tmp, "home", "x"), ctx));
        Assert.False(SavePathGuard.IsInForeignProfile(Path.Combine(_tmp, "pub", "x"), ctx));
    }

    [Fact]
    public void A_backup_folder_behind_a_junction_gets_nothing()
    {
        var ctx = Ctx();
        Write(Path.Combine(Saves(ctx), "a.sav"));
        var elsewhere = Path.Combine(_tmp, "elsewhere");
        var svc = Service();
        Directory.CreateDirectory(BackupRoot);
        if (!TryLink(svc.GameDirectory(Game), elsewhere)) return;

        var r = svc.Backup(Game, SaveLocator.Scan(SavesEntry(), ctx));

        Assert.Null(r.Id);
        Assert.Empty(Directory.EnumerateFileSystemEntries(elsewhere));
        Assert.Throws<IOException>(() => svc.Delete(Game, "2026-01-01_10-00.zip"));
    }

    // --- Restore of manipulated backups ----------------------------------------

    private string Forge(SaveBackupService svc, Action<ZipArchive, List<object>> fill)
    {
        var dir = svc.GameDirectory(Game);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "2026-01-01_00-00.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var entries = new List<object>();
            fill(zip, entries);
            using var m = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            m.Write(JsonSerializer.Serialize(new { version = 1, game = "Real Game", createdUtc = DateTime.UtcNow, entries }));
        }
        return Path.GetFileName(path);
    }

    private static void Add(ZipArchive zip, List<object> entries, string root, string rel, string content = "evil", int? unixMode = null)
    {
        var name = $"files/{root}/{rel}";
        var e = zip.CreateEntry(name);
        if (unixMode is { } mode) e.ExternalAttributes = mode << 16;
        using (var w = new StreamWriter(e.Open())) w.Write(content);
        entries.Add(new { zip = name, root, rel, size = content.Length, modifiedUtc = DateTime.UtcNow });
    }

    [Fact]
    public void A_restore_never_writes_through_a_junction_planted_in_the_save_folder()
    {
        var ctx = Ctx();
        var svc = Service();
        var outside = Path.Combine(_tmp, "elsewhere");
        Directory.CreateDirectory(Saves(ctx));
        if (!TryLink(Path.Combine(Saves(ctx), "sub"), outside)) return;
        var id = Forge(svc, (zip, list) =>
        {
            Add(zip, list, "winAppData", "RealGame/saves/sub/payload.txt");
            Add(zip, list, "winAppData", "RealGame/saves/ok.sav", "fine");
        });

        var r = svc.Restore(Game, id, SavesEntry(), ctx);

        Assert.False(File.Exists(Path.Combine(outside, "payload.txt")));
        Assert.Equal(1, r.Restored);
        Assert.Contains(r.Errors, e => e.Contains("payload.txt"));
        Assert.Equal("fine", File.ReadAllText(Path.Combine(Saves(ctx), "ok.sav")));
    }

    [Fact]
    public void A_restore_never_writes_into_another_profile()
    {
        var install = Path.Combine(_tmp, "other", "Games", "RealGame");
        var ctx = Ctx(install, profiles: true);
        var svc = Service();
        var id = Forge(svc, (zip, list) => Add(zip, list, "base", "save.dat"));

        Assert.Throws<InvalidDataException>(() => svc.Restore(Game, id, Entry("<base>/save.dat"), ctx));
        Assert.False(File.Exists(Path.Combine(install, "save.dat")));
    }

    [Fact]
    public void Links_and_folders_stored_in_a_backup_are_not_restored()
    {
        var ctx = Ctx();
        var svc = Service();
        var id = Forge(svc, (zip, list) =>
        {
            // A Unix symbolic link (mode 0120777) whose "content" is a path elsewhere.
            Add(zip, list, "winAppData", "RealGame/saves/link.sav", "../../../../evil", unixMode: 0xA1FF);
            Add(zip, list, "winAppData", "RealGame/saves/ok.sav", "fine");
        });

        var r = svc.Restore(Game, id, SavesEntry(), ctx);

        Assert.Equal(1, r.Restored);
        Assert.False(File.Exists(Path.Combine(Saves(ctx), "link.sav")));
        Assert.Equal("fine", File.ReadAllText(Path.Combine(Saves(ctx), "ok.sav")));
    }

    [Fact]
    public void A_backup_naming_the_same_target_twice_restores_it_once()
    {
        var ctx = Ctx();
        var svc = Service();
        var id = Forge(svc, (zip, list) =>
        {
            Add(zip, list, "winAppData", "RealGame/saves/a.sav", "first");
            list.Add(new { zip = "files/winAppData/RealGame/saves/a.sav", root = "winAppData", rel = "RealGame/saves/A.sav", size = 4, modifiedUtc = DateTime.UtcNow });
        });

        var r = svc.Restore(Game, id, SavesEntry(), ctx);

        Assert.Equal(1, r.Restored);
        Assert.Single(r.Errors);
    }

    [Fact]
    public void A_restore_takes_a_safety_backup_of_the_current_state_first()
    {
        var ctx = Ctx();
        var entry = SavesEntry();
        var svc = Service();
        Write(Path.Combine(Saves(ctx), "a.sav"), "old");
        File.SetLastWriteTimeUtc(Path.Combine(Saves(ctx), "a.sav"), new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var auto = SaveGameService.AutoBackupCore(svc, Game, entry, ctx, Limits(), CancellationToken.None).BackupId!;
        File.WriteAllText(Path.Combine(Saves(ctx), "a.sav"), "current");

        var r = svc.Restore(Game, auto, entry, ctx);

        Assert.Equal("old", File.ReadAllText(Path.Combine(Saves(ctx), "a.sav")));
        Assert.NotNull(r.SafetyBackupId);
        var back = svc.Restore(Game, r.SafetyBackupId!, entry, ctx);
        Assert.Empty(back.Errors);
        Assert.Equal("current", File.ReadAllText(Path.Combine(Saves(ctx), "a.sav")));
    }

    [Fact]
    public void Reparse_check_stays_below_its_root()
    {
        Assert.True(SavePathGuard.HasReparsePointBelow(Path.Combine(_tmp, "a"), Path.Combine(_tmp, "b", "x")));
        Assert.True(SavePathGuard.HasReparsePointBelow(Path.Combine(_tmp, "a"), Path.Combine(_tmp, "a", "..", "b")));
        Assert.False(SavePathGuard.HasReparsePointBelow(Path.Combine(_tmp, "a"), Path.Combine(_tmp, "a", "not", "there", "yet.sav")));
    }

    [Fact]
    public void A_network_drive_is_no_backup_folder()
    {
        var svc = new SaveGameService(new ConfigStore(Path.Combine(_tmp, "cfg")), _ => null, _ => 0,
            new ManifestStore(new HttpClient(), Path.Combine(_tmp, "m")), Path.Combine(_tmp, "data"), () => null, _ => false)
        {
            IsNetworkDrive = root => root.StartsWith('Z')
        };
        Assert.Throws<ArgumentException>(() => svc.SetBackupDir(@"Z:\Backups"));
        Assert.Throws<ArgumentException>(() => svc.SetBackupDir(@"\\server\share"));
        svc.SetAutoMaxGameMb(1);
        svc.SetAutoKeep(999);
        var s = svc.GetSettings();
        Assert.Equal(SaveGameService.MinGameMb, s.AutoMaxGameMb);
        Assert.Equal(SaveGameService.MaxAutoKeep, s.AutoKeep);
        Assert.True(s.AutoBackup);
        svc.SetAutoBackup(false);
        Assert.False(svc.GetSettings().AutoBackup);
    }

    // --- Review fixes: cloud files, settings, final paths, names, limits ----------

    [Theory]
    [InlineData(0xA0000003u, true)] // mount point / junction
    [InlineData(0xA000000Cu, true)] // symbolic link
    [InlineData(0xA000001Du, true)] // any other name surrogate (here: LX symlink)
    [InlineData(0x80000023u, true)] // AF_UNIX socket
    [InlineData(0x9000001Au, false)] // OneDrive / cloud files
    [InlineData(0x9000601Au, false)] // cloud files, other provider number
    [InlineData(0x80000013u, false)] // deduplication
    [InlineData(0x80000017u, false)] // WOF (compressed)
    public void Only_name_surrogates_redirect(uint tag, bool redirects) => Assert.Equal(redirects, SavePathGuard.IsRedirectingTag(tag));

    [Fact]
    public void A_junction_redirects_a_plain_folder_does_not()
    {
        var plain = Path.Combine(_tmp, "plain");
        Directory.CreateDirectory(plain);
        Assert.False(SavePathGuard.IsRedirect(plain));
        Assert.False(SavePathGuard.IsRedirect(Path.Combine(_tmp, "not-there")));
        if (!TryLink(Path.Combine(_tmp, "link"), Path.Combine(_tmp, "target"))) return;
        Assert.True(SavePathGuard.IsRedirect(Path.Combine(_tmp, "link")));
    }

    [Theory]
    [InlineData((FileAttributes)0x00400000, null, true)] // RECALL_ON_DATA_ACCESS: OneDrive "online only"
    [InlineData((FileAttributes)0x00040000, null, true)] // RECALL_ON_OPEN
    [InlineData(FileAttributes.Offline | FileAttributes.ReparsePoint, 0x9000001Au, true)] // OFFLINE on a cloud file (IO_REPARSE_TAG_CLOUD)
    [InlineData(FileAttributes.Offline | FileAttributes.ReparsePoint, 0x9000601Au, true)] // CLOUD_6 (OneDrive)
    [InlineData(FileAttributes.Offline, null, false)] // OFFLINE alone: any program can set it
    [InlineData(FileAttributes.Offline | FileAttributes.ReparsePoint, 0xA000000Cu, false)] // OFFLINE on a symbolic link
    [InlineData(FileAttributes.Offline | FileAttributes.ReparsePoint, 0x80000013u, false)] // OFFLINE on a deduplicated file
    [InlineData(FileAttributes.Archive | FileAttributes.ReparsePoint, 0x9000001Au, false)] // a cloud file that is on this PC
    [InlineData(FileAttributes.Normal, null, false)]
    public void Only_files_held_online_need_a_download(FileAttributes attributes, uint? tag, bool recall) =>
        Assert.Equal(recall, SavePathGuard.NeedsRecall(attributes, tag));

    [Fact]
    public void The_offline_attribute_alone_does_not_stop_the_automatic_backup()
    {
        var ctx = Ctx();
        var entry = SavesEntry();
        Write(Path.Combine(Saves(ctx), "local.sav"));
        var marked = Path.Combine(Saves(ctx), "marked.sav");
        Write(marked);
        File.SetAttributes(marked, File.GetAttributes(marked) | FileAttributes.Offline);
        try
        {
            Assert.Empty(SaveLocator.Scan(entry, ctx, avoidRecall: true).SkippedOnline);

            var r = SaveGameService.AutoBackupCore(Service(), Game, entry, ctx, Limits(), CancellationToken.None);

            Assert.Equal(AutoBackupOutcome.Done, r.Outcome);
            Assert.Equal(2, Service().List(Game).Single().FileCount);
        }
        finally
        {
            File.SetAttributes(marked, File.GetAttributes(marked) & ~FileAttributes.Offline);
        }
    }

    [Fact]
    public void The_automatic_backup_leaves_files_held_online_alone_a_backup_by_hand_reads_them()
    {
        var online = Path.Combine(Saves(Ctx()), "online.sav");
        // A plain test file cannot be made a cloud placeholder: the context says which file is held online.
        var ctx = Ctx() with { HeldOnline = f => string.Equals(f.FullName, online, StringComparison.OrdinalIgnoreCase) };
        var entry = SavesEntry();
        Write(Path.Combine(Saves(ctx), "local.sav"));
        Write(online);

        var auto = SaveLocator.Scan(entry, ctx, avoidRecall: true);
        Assert.Single(auto.Files);
        Assert.Equal(online, Assert.Single(auto.SkippedOnline));
        Assert.Empty(auto.Errors);
        Assert.Equal(2, SaveLocator.Scan(entry, ctx).Files.Count);

        // A complete automatic backup from before must not be pushed out by one missing the online file.
        var svc = Service(autoKeep: 1);
        var good = SaveGameService.AutoBackupCore(svc, Game, entry, Ctx(), Limits(), CancellationToken.None);
        Assert.Equal(AutoBackupOutcome.Done, good.Outcome);
        File.WriteAllText(Path.Combine(Saves(ctx), "local.sav"), "newer");

        var r = SaveGameService.AutoBackupCore(svc, Game, entry, ctx, Limits(), CancellationToken.None);

        Assert.Equal(AutoBackupOutcome.OnlineOnly, r.Outcome);
        Assert.Equal("1", r.Detail);
        Assert.False(r.Notify);
        Assert.Equal(good.BackupId, Assert.Single(svc.List(Game)).Id);
    }

    [Fact]
    public void A_backup_by_hand_of_unchanged_saves_makes_no_new_version()
    {
        var ctx = Ctx();
        Write(Path.Combine(Saves(ctx), "a.sav"));
        File.SetLastWriteTimeUtc(Path.Combine(Saves(ctx), "a.sav"), new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var svc = Service();
        var first = SaveGameService.ManualBackupCore(svc, Game, SaveLocator.Scan(SavesEntry(), ctx), long.MaxValue);
        Assert.False(first.Unchanged);

        var again = SaveGameService.ManualBackupCore(svc, Game, SaveLocator.Scan(SavesEntry(), ctx), long.MaxValue);

        Assert.True(again.Unchanged);
        Assert.Equal(first.Id, again.Id);
        Assert.Single(svc.List(Game));
        File.WriteAllText(Path.Combine(Saves(ctx), "a.sav"), "changed");
        Assert.False(SaveGameService.ManualBackupCore(svc, Game, SaveLocator.Scan(SavesEntry(), ctx), long.MaxValue).Unchanged);
    }

    [ShortNamesFact]
    public void A_short_name_is_the_same_file_as_its_long_name()
    {
        var root = Path.Combine(_tmp, "root");
        var file = Path.Combine(root, "A Rather Long Folder Name", "x.sav");
        Write(file);
        var shortPath = ShortNames.Of(file);
        Assert.Contains('~', shortPath);
        Assert.Equal(SaveLocator.Normalize(Path.GetFullPath(file)), SaveLocator.Normalize(NativeFiles.LongName(shortPath)), ignoreCase: true);
        using var fs = File.OpenRead(file);
        Assert.True(SavePathGuard.IsFinalPath(fs.SafeFileHandle, root, shortPath));
    }

    [ShortNamesFact]
    public void A_root_with_a_short_name_is_the_same_folder_as_its_long_name()
    {
        var root = Path.Combine(_tmp, "A Rather Long Root Folder");
        var file = Path.Combine(root, "saves", "x.sav");
        Write(file);
        var shortRoot = ShortNames.Of(root);
        Assert.Contains('~', shortRoot);
        using var fs = File.OpenRead(file);
        Assert.True(SavePathGuard.IsFinalPath(fs.SafeFileHandle, shortRoot, Path.Combine(shortRoot, "saves", "x.sav")));
        Assert.False(SavePathGuard.IsFinalPath(fs.SafeFileHandle, shortRoot, Path.Combine(shortRoot, "saves", "y.sav")));
    }

    [ShortNamesFact]
    public void A_backup_folder_given_by_its_short_name_is_still_found_in_the_save_places()
    {
        var ctx = Ctx();
        var inSaves = Path.Combine(ctx.Home, "Saved Games", "Couch Backups Folder");
        Directory.CreateDirectory(inSaves);
        var shortDir = ShortNames.Of(inSaves);
        Assert.Contains('~', shortDir);
        Assert.True(SaveGameService.IsInSaveRoot(shortDir, ctx));
        Assert.True(SaveGameService.IsInSaveRoot(Path.Combine(shortDir, "new folder"), ctx));

        // The scan leaves out the backups also when the backup folder is given by its 8.3 name.
        var backups = Path.Combine(Saves(ctx), "Couch Backups Folder");
        Write(Path.Combine(backups, "old.zip"));
        Write(Path.Combine(Saves(ctx), "a.sav"));
        var scan = SaveLocator.Scan(SavesEntry(), ctx with { BackupDir = ShortNames.Of(backups) });
        Assert.Equal("a.sav", Path.GetFileName(Assert.Single(scan.Files).FullPath));

        // And a restore never writes into it.
        var svc = new SaveBackupService(ShortNames.Of(backups), 5);
        Assert.True(svc.InBackupRoot(Path.Combine(backups, "x.sav")));
        Assert.False(svc.InBackupRoot(Path.Combine(Saves(ctx), "x.sav")));
    }

    [Fact]
    public void A_restore_never_writes_into_the_backup_folder()
    {
        var ctx = Ctx();
        var svc = new SaveBackupService(Path.Combine(Saves(ctx), "bk"), 5);
        var id = Forge(svc, (zip, list) =>
        {
            Add(zip, list, "winAppData", "RealGame/saves/bk/planted.sav", "evil");
            Add(zip, list, "winAppData", "RealGame/saves/ok.sav", "fine");
        });

        var r = svc.Restore(Game, id, SavesEntry(), ctx);

        Assert.Equal(1, r.Restored);
        Assert.Contains(r.Errors, e => e.Contains("planted.sav", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(Saves(ctx), "bk", "planted.sav")));
        Assert.Equal("fine", File.ReadAllText(Path.Combine(Saves(ctx), "ok.sav")));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [Fact]
    public void A_hard_linked_file_makes_the_backup_incomplete_and_keeps_the_complete_one()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ctx = Ctx();
        var entry = SavesEntry();
        var save = Path.Combine(Saves(ctx), "a.sav");
        Write(save);
        Write(Path.Combine(Saves(ctx), "b.sav"));
        var svc = Service(autoKeep: 1);
        var good = SaveGameService.AutoBackupCore(svc, Game, entry, ctx, Limits(), CancellationToken.None);
        Assert.Equal(AutoBackupOutcome.Done, good.Outcome);
        File.WriteAllText(save, "newer");
        Assert.True(CreateHardLinkW(Path.Combine(_tmp, "elsewhere.dat"), save, IntPtr.Zero));

        var r = SaveGameService.AutoBackupCore(svc, Game, entry, ctx, Limits(), CancellationToken.None);

        Assert.Equal(AutoBackupOutcome.Incomplete, r.Outcome);
        var list = svc.List(Game);
        Assert.False(list.Single(b => b.Id == r.BackupId).Complete);
        Assert.True(list.Single(b => b.Id == good.BackupId).Complete);
    }

    [Fact]
    public void The_backup_folder_is_never_part_of_a_save()
    {
        var ctx = Ctx() with { BackupDir = Path.Combine(_tmp, "home", "AppData", "Roaming", "RealGame", "saves", "backups") };
        Write(Path.Combine(Saves(ctx), "a.sav"));
        Write(Path.Combine(ctx.BackupDir, "Real Game [steam_4242]", "old.zip"));

        var scan = SaveLocator.Scan(SavesEntry(), ctx);

        Assert.Equal("a.sav", Path.GetFileName(Assert.Single(scan.Files).FullPath));
    }

    [Fact]
    public void A_backup_folder_inside_the_save_places_is_refused()
    {
        var ctx = Ctx() with { SteamRoot = Path.Combine(_tmp, "steam") };
        Assert.True(SaveGameService.IsInSaveRoot(Path.Combine(ctx.WinDocuments, "My Games", "Backups"), ctx));
        Assert.True(SaveGameService.IsInSaveRoot(Path.Combine(ctx.Home, "Saved Games"), ctx));
        Assert.True(SaveGameService.IsInSaveRoot(Path.Combine(ctx.WinLocalAppDataLow, "x"), ctx));
        Assert.True(SaveGameService.IsInSaveRoot(Path.Combine(_tmp, "steam", "userdata", "b"), ctx));
        Assert.False(SaveGameService.IsInSaveRoot(Path.Combine(_tmp, "D", "Backups"), ctx));
        Assert.False(SaveGameService.IsInSaveRoot(Path.Combine(ctx.WinDocuments, "Backups"), ctx));
    }

    [Fact]
    public void A_backup_by_hand_keeps_the_limit_per_game()
    {
        var ctx = Ctx();
        Write(Path.Combine(Saves(ctx), "a.sav"), new string('x', 4096));
        var svc = Service();

        var r = SaveGameService.ManualBackupCore(svc, Game, SaveLocator.Scan(SavesEntry(), ctx), 1024);

        Assert.Null(r.Id);
        Assert.True(r.LimitReached);
        Assert.Empty(svc.List(Game));
        Assert.NotNull(SaveGameService.ManualBackupCore(svc, Game, SaveLocator.Scan(SavesEntry(), ctx), 1024 * 1024).Id);
    }

    [Fact]
    public void The_final_path_of_an_open_file_shows_a_link_on_the_way()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(_tmp, "root");
        var file = Path.Combine(root, "a", "x.sav");
        Write(file);
        using (var fs = File.OpenRead(file)) Assert.True(SavePathGuard.IsFinalPath(fs.SafeFileHandle, root, file));

        var outside = Path.Combine(_tmp, "outside");
        Write(Path.Combine(outside, "x.sav"));
        if (!TryLink(Path.Combine(root, "b"), outside)) return;
        var through = Path.Combine(root, "b", "x.sav");
        using var fs2 = File.OpenRead(through);
        Assert.False(SavePathGuard.IsFinalPath(fs2.SafeFileHandle, root, through));
    }

    [Fact]
    public void A_file_with_several_hard_links_is_left_out()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ctx = Ctx();
        var elsewhere = Path.Combine(_tmp, "elsewhere", "secret.txt");
        Write(elsewhere, "secret");
        Write(Path.Combine(Saves(ctx), "a.sav"));
        var link = Path.Combine(Saves(ctx), "b.sav");
        using (var p = Process.Start(new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/c", "mklink", "/H", link, elsewhere }, CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true }))
        {
            p!.WaitForExit();
            if (p.ExitCode != 0) return;
        }

        var r = Service().Backup(Game, SaveLocator.Scan(SavesEntry(), ctx));

        Assert.Equal(1, r.FileCount);
        Assert.False(r.Complete);
        Assert.Contains(r.Errors, e => e.Contains("b.sav"));
    }

    [Fact]
    public void A_backup_folder_below_a_junction_further_up_gets_nothing()
    {
        var ctx = Ctx();
        Write(Path.Combine(Saves(ctx), "a.sav"));
        var real = Path.Combine(_tmp, "real");
        var link = Path.Combine(_tmp, "jump");
        if (!TryLink(link, real)) return;
        var svc = new SaveBackupService(Path.Combine(link, "deeper", "backups"), 5);

        var r = svc.Backup(Game, SaveLocator.Scan(SavesEntry(), ctx));

        Assert.Null(r.Id);
        Assert.False(Directory.Exists(Path.Combine(real, "deeper", "backups", SaveBackupService.FolderName(Game))));
    }

    [Theory]
    [InlineData("NUL")]
    [InlineData("nul.sav")]
    [InlineData("CON")]
    [InlineData("com1.dat")]
    [InlineData("LPT9")]
    [InlineData("CONOUT$")]
    [InlineData("AUX .txt")]
    [InlineData("save.")]
    [InlineData("save ")]
    public void Device_names_and_trailing_dots_are_never_restored(string name)
    {
        Assert.True(SaveBackupService.IsIllegalPart(name));
        var item = new SaveBackupService.ZipEntryInfo("files/x", "winAppData", $"RealGame/saves/{name}", 1, DateTime.UtcNow);
        Assert.Throws<InvalidDataException>(() => SaveBackupService.ResolveTarget(item, Ctx(), SaveLocator.ResolvePatterns(SavesEntry(), Ctx())));
    }

    [Theory]
    [InlineData("console.sav")]
    [InlineData("COM10")]
    [InlineData("null.dat")]
    [InlineData(".hidden")]
    public void Ordinary_names_stay_allowed(string name) => Assert.False(SaveBackupService.IsIllegalPart(name));

    [Fact]
    public void A_backup_listing_more_files_than_a_scan_reads_is_refused()
    {
        var svc = Service();
        var id = Forge(svc, (zip, list) =>
        {
            Add(zip, list, "winAppData", "RealGame/saves/a.sav");
            for (var i = 0; i < SaveLocator.MaxFiles; i++) list.Add(new { zip = "files/winAppData/RealGame/saves/a.sav", root = "winAppData", rel = $"RealGame/saves/{i}.sav", size = 1, modifiedUtc = DateTime.UtcNow });
        });

        Assert.Throws<InvalidDataException>(() => svc.Restore(Game, id, SavesEntry(), Ctx()));
        Assert.False(Directory.Exists(Saves(Ctx())));
    }

    [Fact]
    public void A_failed_restore_takes_back_the_folders_it_created_and_leaves_no_temp_file()
    {
        var ctx = Ctx();
        var writes = 0;
        var svc = new SaveBackupService(BackupRoot, 5)
        {
            BeforeWrite = _ =>
            {
                if (++writes == 2) throw new IOException("disk gone");
            }
        };
        Write(Path.Combine(Saves(ctx), "keep.sav"), "current");
        var id = Forge(svc, (zip, list) =>
        {
            Add(zip, list, "winAppData", "RealGame/saves/new/deep/a.sav", "a");
            Add(zip, list, "winAppData", "RealGame/saves/b.sav", "b");
        });

        var r = svc.Restore(Game, id, SavesEntry(), ctx);

        Assert.True(r.RolledBack);
        Assert.False(Directory.Exists(Path.Combine(Saves(ctx), "new")));
        Assert.Equal("current", File.ReadAllText(Path.Combine(Saves(ctx), "keep.sav")));
        Assert.Empty(Directory.EnumerateFiles(Saves(ctx), "*" + SaveLocator.TempSuffix, SearchOption.AllDirectories));
    }

    [Fact]
    public void Rotation_deletes_only_backups_it_can_read()
    {
        var ctx = Ctx();
        var svc = Service(keep: 1);
        var dir = svc.GameDirectory(Game);
        Directory.CreateDirectory(dir);
        var stranger = Path.Combine(dir, "2020-01-01_00-00.zip");
        File.WriteAllText(stranger, "not a backup of CouchHUD");
        for (var i = 0; i < 3; i++)
        {
            Write(Path.Combine(Saves(ctx), "a.sav"), "v" + i);
            Assert.NotNull(svc.Backup(Game, SaveLocator.Scan(SavesEntry(), ctx)).Id);
        }

        Assert.True(File.Exists(stranger));
        Assert.Single(svc.List(Game), b => b.Verified);
    }

    [Fact]
    public async Task Switched_off_the_automatic_backup_does_nothing()
    {
        var store = new ConfigStore(Path.Combine(_tmp, "cfg2"));
        store.Update(c => c.AutoBackupSaves = false);
        var svc = new SaveGameService(store, _ => throw new InvalidOperationException("not asked"), _ => 0,
            new ManifestStore(new HttpClient(), Path.Combine(_tmp, "m")), Path.Combine(_tmp, "data"), () => null, _ => false);

        var r = await svc.AutoBackupAsync("steam:1", Limits());

        Assert.Equal(AutoBackupOutcome.Skipped, r.Outcome);
    }

    private static readonly DateTime Old = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private BackupResult ByHand(SaveBackupService svc, SaveContext ctx) =>
        SaveGameService.ManualBackupCore(svc, Game, SaveLocator.Scan(SavesEntry(), ctx), long.MaxValue);

    [Fact]
    public void Saves_copied_in_with_an_old_time_stamp_are_backed_up_again()
    {
        var ctx = Ctx();
        var save = Path.Combine(Saves(ctx), "a.sav");
        Write(save, "first");
        File.SetLastWriteTimeUtc(save, Old);
        var svc = Service();
        var first = ByHand(svc, ctx);

        // Same name, same count, an older time stamp, other content of the same size (copied in from a USB stick).
        File.WriteAllText(save, "other");
        File.SetLastWriteTimeUtc(save, Old.AddDays(-30));
        var copied = ByHand(svc, ctx);
        Assert.False(copied.Unchanged);
        Assert.NotEqual(first.Id, copied.Id);

        // Same time stamp as in the backup, but another size.
        File.WriteAllText(save, "longer content");
        File.SetLastWriteTimeUtc(save, Old.AddDays(-30));
        var bigger = ByHand(svc, ctx);
        Assert.False(bigger.Unchanged);
        Assert.Equal(3, svc.List(Game).Count);

        // Exactly the files of the last backup: no new version.
        var same = ByHand(svc, ctx);
        Assert.True(same.Unchanged);
        Assert.Equal(bigger.Id, same.Id);
    }

    [Fact]
    public void After_restoring_an_older_backup_a_backup_by_hand_is_not_the_newer_one()
    {
        var ctx = Ctx();
        var entry = SavesEntry();
        var save = Path.Combine(Saves(ctx), "a.sav");
        Write(save, "state B");
        File.SetLastWriteTimeUtc(save, Old);
        var svc = Service();
        var b = ByHand(svc, ctx);
        File.WriteAllText(save, "state A, newer");
        var a = ByHand(svc, ctx);
        Assert.NotEqual(b.Id, a.Id);

        svc.Restore(Game, b.Id!, entry, ctx);
        Assert.Equal("state B", File.ReadAllText(save));
        Assert.Equal(Old, File.GetLastWriteTimeUtc(save));

        var now = ByHand(svc, ctx);

        Assert.False(now.Unchanged);
        Assert.NotEqual(a.Id, now.Id);
        Assert.NotEqual(b.Id, now.Id);
    }

    [Fact]
    public void An_automatic_backup_is_no_reference_for_a_backup_by_hand()
    {
        var ctx = Ctx();
        Write(Path.Combine(Saves(ctx), "a.sav"));
        var svc = Service();
        var auto = SaveGameService.AutoBackupCore(svc, Game, SavesEntry(), ctx, Limits(), CancellationToken.None);
        Assert.Equal(AutoBackupOutcome.Done, auto.Outcome);

        var byHand = ByHand(svc, ctx);

        Assert.False(byHand.Unchanged);
        Assert.NotEqual(auto.BackupId, byHand.Id);
        Assert.False(svc.List(Game).Single(x => x.Id == byHand.Id).Auto);
        // The automatic backup in turn sees the backup by hand as unchanged state.
        Assert.Equal(AutoBackupOutcome.Unchanged, SaveGameService.AutoBackupCore(svc, Game, SavesEntry(), ctx, Limits(), CancellationToken.None).Outcome);
    }

    [Fact]
    public void Backing_up_unchanged_saves_again_and_again_rotates_no_older_state_away()
    {
        var ctx = Ctx();
        var save = Path.Combine(Saves(ctx), "a.sav");
        Write(save, "old state");
        File.SetLastWriteTimeUtc(save, Old);
        var svc = Service(keep: 2);
        var older = ByHand(svc, ctx);
        File.WriteAllText(save, "current state");
        var current = ByHand(svc, ctx);

        // "Uninstall → back up → cancel", five times.
        for (var i = 0; i < 5; i++) Assert.True(ByHand(svc, ctx).Unchanged);

        var ids = svc.List(Game).Select(b => b.Id).ToList();
        Assert.Equal(2, ids.Count);
        Assert.Contains(older.Id, ids);
        Assert.Contains(current.Id, ids);
    }

    [Fact]
    public void Incomplete_backups_by_hand_have_a_limit_and_push_out_no_complete_one()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ctx = Ctx();
        Write(Path.Combine(Saves(ctx), "a.sav"));
        Write(Path.Combine(Saves(ctx), "b.sav"));
        var svc = Service(keep: 2);
        var complete = ByHand(svc, ctx);
        Assert.True(complete.Complete);
        // A planted hard link: every further backup by hand is incomplete (and so never "unchanged").
        Assert.True(CreateHardLinkW(Path.Combine(_tmp, "elsewhere.dat"), Path.Combine(Saves(ctx), "a.sav"), IntPtr.Zero));

        for (var i = 0; i < 6; i++)
        {
            File.WriteAllText(Path.Combine(Saves(ctx), "b.sav"), "state " + i);
            Assert.False(ByHand(svc, ctx).Complete);
        }

        var list = svc.List(Game);
        Assert.Equal(2, list.Count(b => !b.Complete));
        Assert.Contains(list, b => b.Id == complete.Id && b.Complete);
    }

    [Fact]
    public void Backing_up_the_same_incomplete_state_again_and_again_rotates_no_older_backup_away()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ctx = Ctx();
        Write(Path.Combine(Saves(ctx), "a.sav"));
        Write(Path.Combine(Saves(ctx), "b.sav"));
        var svc = Service(keep: 2);
        var complete = ByHand(svc, ctx);
        Assert.True(complete.Complete);
        // A planted hard link: every backup by hand from now on is incomplete.
        Assert.True(CreateHardLinkW(Path.Combine(_tmp, "elsewhere.dat"), Path.Combine(Saves(ctx), "a.sav"), IntPtr.Zero));
        File.WriteAllText(Path.Combine(Saves(ctx), "b.sav"), "state 1");
        var first = ByHand(svc, ctx);
        File.WriteAllText(Path.Combine(Saves(ctx), "b.sav"), "state 2");
        var second = ByHand(svc, ctx);
        Assert.False(first.Complete || first.Unchanged);
        Assert.False(second.Complete || second.Unchanged);

        // "Back up now" again and again without a change: the newest incomplete backup is the answer, nothing is rotated.
        for (var i = 0; i < 5; i++)
        {
            var again = ByHand(svc, ctx);
            Assert.True(again.Unchanged);
            Assert.False(again.Complete);
            Assert.Equal(second.Id, again.Id);
        }
        var ids = svc.List(Game).Select(b => b.Id).ToList();
        Assert.Equal(3, ids.Count);
        Assert.Contains(complete.Id, ids);
        Assert.Contains(first.Id, ids);
        Assert.Contains(second.Id, ids);
        Assert.Empty(Directory.EnumerateFiles(BackupRoot, "*.part", SearchOption.AllDirectories));

        // A real change is a new version again (and the rotation of incomplete ones applies as before).
        File.WriteAllText(Path.Combine(Saves(ctx), "b.sav"), "state 3");
        var changed = ByHand(svc, ctx);
        Assert.False(changed.Unchanged);
        Assert.NotEqual(second.Id, changed.Id);
        Assert.Contains(svc.List(Game), b => b.Id == complete.Id && b.Complete);
    }

    [Fact]
    public void The_same_incomplete_state_after_every_game_rotates_no_older_automatic_backup_away_and_stays_quiet()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ctx = Ctx();
        var entry = SavesEntry();
        Write(Path.Combine(Saves(ctx), "a.sav"));
        Write(Path.Combine(Saves(ctx), "b.sav"));
        var svc = Service(keep: 2, autoKeep: 2);
        // A planted hard link: every automatic backup from now on is incomplete.
        Assert.True(CreateHardLinkW(Path.Combine(_tmp, "elsewhere.dat"), Path.Combine(Saves(ctx), "a.sav"), IntPtr.Zero));
        AutoBackupResult Run() => SaveGameService.AutoBackupCore(svc, Game, entry, ctx, Limits(), CancellationToken.None);
        File.WriteAllText(Path.Combine(Saves(ctx), "b.sav"), "state 1");
        var first = Run();
        File.WriteAllText(Path.Combine(Saves(ctx), "b.sav"), "state 2");
        var second = Run();
        Assert.Equal(AutoBackupOutcome.Incomplete, first.Outcome);
        Assert.Equal(AutoBackupOutcome.Incomplete, second.Outcome);

        // Game after game without a change: the newest incomplete backup holds, nothing is rotated, nothing is told.
        for (var i = 0; i < 5; i++)
        {
            var again = Run();
            Assert.Equal(AutoBackupOutcome.Unchanged, again.Outcome);
            Assert.Equal(second.BackupId, again.BackupId);
            Assert.False(again.Notify);
        }
        var ids = svc.List(Game).Select(b => b.Id).ToList();
        Assert.Equal(2, ids.Count);
        Assert.Contains(first.BackupId, ids);
        Assert.Contains(second.BackupId, ids);
        Assert.Empty(Directory.EnumerateFiles(BackupRoot, "*.part", SearchOption.AllDirectories));

        // A real change is a new (told) incomplete backup again.
        File.WriteAllText(Path.Combine(Saves(ctx), "b.sav"), "state 3");
        var changed = Run();
        Assert.Equal(AutoBackupOutcome.Incomplete, changed.Outcome);
        Assert.NotEqual(second.BackupId, changed.BackupId);
    }

    [Fact]
    public void Same_files_needs_the_same_set_size_and_change_time()
    {
        var t = new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        List<SaveBackupService.ZipEntryInfo> manifest = [new("files/r/a", "r", "a", 3, t), new("files/r/b", "r", "b", 4, t)];
        Assert.True(SaveBackupService.SameFiles(manifest, [("r", "b", 4L, t), ("r", "a", 3L, t)]));
        Assert.False(SaveBackupService.SameFiles(manifest, [("r", "a", 3L, t)]), "a file less");
        Assert.False(SaveBackupService.SameFiles(manifest, [("r", "a", 3L, t), ("r", "b", 4L, t), ("r", "c", 1L, t)]), "a file more");
        Assert.False(SaveBackupService.SameFiles(manifest, [("r", "a", 3L, t), ("r", "b", 5L, t)]), "other size");
        Assert.False(SaveBackupService.SameFiles(manifest, [("r", "a", 3L, t), ("r", "b", 4L, t.AddTicks(1))]), "other time");
        Assert.False(SaveBackupService.SameFiles([.. manifest, new("files/r/A", "r", "A", 3, t)], [("r", "a", 3L, t), ("r", "b", 4L, t)]), "a name twice");
    }

    [Fact]
    public void A_restore_never_writes_into_the_backup_folder_reached_by_another_name()
    {
        var ctx = Ctx();
        var real = Path.Combine(Saves(ctx), "bk");
        var other = Path.Combine(_tmp, "bk-other-name");
        if (!TryLink(other, real)) return;
        // The backup folder is known by a name that no string comparison ties to the save folder (as a subst drive would be).
        var svc = new SaveBackupService(other, 5);
        var id = Forge(svc, (zip, list) => Add(zip, list, "winAppData", "RealGame/saves/bk/planted.sav", "evil"));

        var r = svc.Restore(Game, id, SavesEntry(), ctx with { BackupDir = real });

        Assert.Equal(0, r.Restored);
        Assert.NotEmpty(r.Errors);
        Assert.False(File.Exists(Path.Combine(real, "planted.sav")));
        Assert.Empty(Directory.EnumerateFiles(real, "*" + SaveLocator.TempSuffix));
    }

    [Fact]
    public void An_open_file_is_found_in_the_backup_folder_by_its_real_path()
    {
        if (!OperatingSystem.IsWindows()) return;
        var svc = new SaveBackupService(BackupRoot, 5);
        Write(Path.Combine(BackupRoot, "inside.dat"));
        Write(Path.Combine(_tmp, "outside.dat"));
        var cache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        using (var inside = File.OpenRead(Path.Combine(BackupRoot, "inside.dat"))) Assert.True(svc.IsInBackupFolder(inside.SafeFileHandle, cache));
        using (var outside = File.OpenRead(Path.Combine(_tmp, "outside.dat"))) Assert.False(svc.IsInBackupFolder(outside.SafeFileHandle, cache));

        // A backup folder whose real path cannot be found: refused (fail closed).
        var gone = new SaveBackupService(Path.Combine(_tmp, "no-such-folder"), 5);
        using var any = File.OpenRead(Path.Combine(_tmp, "outside.dat"));
        Assert.True(gone.IsInBackupFolder(any.SafeFileHandle, new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)));
    }
}

/// <summary>8.3 names of existing paths; the path itself when the drive makes none.</summary>
internal static class ShortNames
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint GetShortPathNameW(string lpszLongPath, [System.Runtime.InteropServices.Out] char[] lpszShortPath, uint cchBuffer);

    public static string Of(string path)
    {
        if (!OperatingSystem.IsWindows()) return path;
        var buffer = new char[1024];
        var n = GetShortPathNameW(path, buffer, (uint)buffer.Length);
        return n > 0 && n < buffer.Length ? new string(buffer, 0, (int)n) : path;
    }

    /// <summary>Whether the temp folder's drive gives a folder with a long name an 8.3 name (it can be switched off per volume).</summary>
    public static readonly bool Available = Probe();

    private static bool Probe()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var dir = Path.Combine(Path.GetTempPath(), "couchhud-shortname-probe " + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            return Path.GetFileName(Of(dir)).Contains('~');
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try { Directory.Delete(dir); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

/// <summary>A fact shown as skipped (not silently green) when the drive makes no 8.3 names.</summary>
public sealed class ShortNamesFactAttribute : FactAttribute
{
    public ShortNamesFactAttribute()
    {
        if (!ShortNames.Available) Skip = "the drive of the temp folder makes no 8.3 names";
    }
}
