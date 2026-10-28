using System.Collections.Concurrent;
using CouchHud.Core.Config;
using CouchHud.Core.Library;
using CouchHud.Sdk;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Saves;

/// <summary>What the detail page shows about a game's saves; <see cref="Reason"/> says why there are none.</summary>
/// <param name="Unavailable">The game database could not be loaded (offline), so "no saves" is not known.</param>
public sealed record SaveInfoDto(
    bool Supported,
    bool Unavailable,
    string? Reason,
    string? GameName,
    IReadOnlyList<string> Locations,
    int FileCount,
    long TotalBytes,
    long LastWrite,
    IReadOnlyList<string> Registry,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<BackupDto> Backups,
    string BackupDir);

public sealed record BackupDto(string Id, long Created, long SizeBytes, int FileCount, bool Safety, bool Complete, bool Auto = false);

public sealed record SaveSettingsDto(string BackupDir, string DefaultBackupDir, bool Custom, int Keep, bool IncludeConfig)
{
    public bool AutoBackup { get; init; }
    public int AutoKeep { get; init; }
    public int AutoMaxGameMb { get; init; }
    public int AutoMaxTotalMb { get; init; }
    /// <summary>Space all backups take now (bytes), for the expert rows.</summary>
    public long TotalBytes { get; init; }
}

/// <summary>What the automatic backup after a game did. Only the failures and the limits are worth a notification.</summary>
public enum AutoBackupOutcome
{
    /// <summary>A new backup was made.</summary>
    Done,
    /// <summary>Nothing changed since the last backup: no new version.</summary>
    Unchanged,
    /// <summary>Off, no known save locations, no saves, the game still running, a backup already under way: nothing to say.</summary>
    Skipped,
    /// <summary>The saves of this game are larger than the per-game limit (or too many files).</summary>
    GameTooBig,
    /// <summary>All backups together would pass the total limit.</summary>
    TotalFull,
    /// <summary>The time limit ran out, or CouchHUD was closed.</summary>
    TimedOut,
    /// <summary>Kept, but some files could not be read (or a link in the save folder was skipped).</summary>
    Incomplete,
    Failed,
    /// <summary>Some save files are only held online (cloud placeholders): nothing is written until they are on this PC again.</summary>
    OnlineOnly
}

public sealed record AutoBackupResult(AutoBackupOutcome Outcome, string? BackupId = null, string? Detail = null)
{
    /// <summary>Failures and limits are told; a backup that worked (or wasn't needed) stays quiet.</summary>
    public bool Notify => Outcome is AutoBackupOutcome.GameTooBig or AutoBackupOutcome.TotalFull or AutoBackupOutcome.TimedOut
        or AutoBackupOutcome.Incomplete or AutoBackupOutcome.Failed;
}

/// <summary>The limits of the automatic backup (expert mode); the time limit is fixed.</summary>
public sealed record AutoBackupLimits(long MaxGameBytes, long MaxTotalBytes, int MaxFiles, TimeSpan Timeout)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Fewer files than a backup by hand allows: a folder with more is not a save folder that should be copied unasked.</summary>
    public const int DefaultMaxFiles = 5000;

    public static AutoBackupLimits From(AppConfig c) => new(
        (long)Math.Clamp(c.AutoBackupMaxGameMb, SaveGameService.MinGameMb, SaveGameService.MaxGameMb) * ByteSize.Mega,
        (long)Math.Clamp(c.AutoBackupMaxTotalMb, SaveGameService.MinTotalMb, SaveGameService.MaxTotalMb) * ByteSize.Mega,
        DefaultMaxFiles, DefaultTimeout);
}

/// <summary>Ties manifest, locator and backups to the library; this is what the UI bridge talks to.</summary>
public sealed class SaveGameService(
    ConfigStore config,
    Func<string, LibraryItem?> findItem,
    Func<string, int> countWithName,
    ManifestStore manifest,
    string dataDir,
    Func<string?> steamRoot,
    Func<LibraryItem, bool> isRunning)
{
    /// <summary>How long the (possibly slow) folder scan is reused when the page is opened again.</summary>
    public static readonly TimeSpan ScanCacheTtl = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, SaveScan Scan)> _scans = new();

    /// <summary>One automatic backup at a time across all games: two games ending together must not both pass the total limit.</summary>
    private readonly SemaphoreSlim _autoOne = new(1, 1);

    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    public string DefaultBackupDir => Path.Combine(dataDir, "Backups");

    public string BackupDir => config.Current.BackupDir is { Length: > 0 } custom && Path.IsPathRooted(custom) ? custom : DefaultBackupDir;

    /// <summary>
    /// The limits are whole megabytes, decimal like every size CouchHUD shows (<see cref="ByteSize"/>). The upper bounds are the old binary
    /// bounds (20 GiB, 1 TiB) in decimal MB, rounded up, so no limit stored before 0.5 shrinks when it is moved up (FromBinaryMb).
    /// </summary>
    public const int MinGameMb = 10, MaxGameMb = 21_475, MinTotalMb = 100, MaxTotalMb = 1_099_512, MaxAutoKeep = 20;

    /// <summary>The limits the page offers (ui/components/savegames.js lists the same).</summary>
    public static readonly int[] GamePresetsMb = [100, 250, 500, 1000, 2000, 5000];

    public static readonly int[] TotalPresetsMb = [1000, 5000, 10000, 20000, 50000, 100000];

    /// <summary>
    /// A limit stored before 0.5 counted binary megabytes (MiB). As decimal MB it would shrink by 4.6 % and refuse a game that fitted
    /// before, so it goes up: to the next offered limit at or above the same number of bytes, or (above every offered limit) to the
    /// whole MB above it, within the bounds.
    /// </summary>
    public static int FromBinaryMb(int mib, int[] presets, int min, int max)
    {
        var bytes = (long)Math.Max(mib, 0) * 1024 * 1024;
        var mb = (long)Math.Ceiling(bytes / (double)ByteSize.Mega);
        foreach (var preset in presets)
        {
            if (preset >= mb) return Math.Clamp(preset, min, max);
        }
        return (int)Math.Clamp(mb, min, max);
    }

    private SaveBackupService Backups() => new(BackupDir, Math.Clamp(config.Current.BackupsToKeep, 1, 50), Math.Clamp(config.Current.AutoBackupsToKeep, 1, MaxAutoKeep));

    private SaveBackupService BackupsFor(LibraryItem item)
    {
        var service = Backups();
        if (countWithName(item.Name) == 1) service.MigrateLegacy(Ref(item));
        return service;
    }

    private static GameRef Ref(LibraryItem item) => new(item.Name, item.Id);

    public SaveSettingsDto GetSettings()
    {
        var c = config.Current;
        return new SaveSettingsDto(BackupDir, DefaultBackupDir, BackupDir != DefaultBackupDir, Math.Clamp(c.BackupsToKeep, 1, 50), c.BackupConfigFiles)
        {
            AutoBackup = c.AutoBackupSaves,
            AutoKeep = Math.Clamp(c.AutoBackupsToKeep, 1, MaxAutoKeep),
            AutoMaxGameMb = Math.Clamp(c.AutoBackupMaxGameMb, MinGameMb, MaxGameMb),
            AutoMaxTotalMb = Math.Clamp(c.AutoBackupMaxTotalMb, MinTotalMb, MaxTotalMb),
            TotalBytes = Backups().TotalBytes()
        };
    }

    /// <summary>Test seam: whether a drive letter is a network drive (backups stay on this PC).</summary>
    public Func<string, bool> IsNetworkDrive { get; init; } = root =>
    {
        try
        {
            return new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (ArgumentException)
        {
            return false;
        }
    };

    public void SetBackupDir(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            // Backups stay local: no share, no mapped network drive.
            if (!Path.IsPathRooted(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)
                || IsNetworkDrive(Path.GetPathRoot(path) ?? path))
            {
                throw new ArgumentException(Strings.Get("saves.pickLocal"));
            }
            // Not inside the places games keep their saves: the backups would be backed up with the saves and grow with every backup.
            if (IsInSaveRoot(path, SaveContext.ForCurrentUser(steamRoot()))) throw new ArgumentException(Strings.Get("saves.pickNotInSaves"));
            Directory.CreateDirectory(path);
        }
        config.Update(c => c.BackupDir = path?.Trim() ?? "");
    }

    /// <summary>The usual save places of games (Saved Games, Documents\My Games, AppData\LocalLow, the Steam folder) and what lies below.</summary>
    internal static bool IsInSaveRoot(string path, SaveContext ctx)
    {
        // Long names on both sides: an 8.3 path (SAVEDG~1) must not slip past the comparison.
        static string Long(string p) => SaveLocator.Normalize(NativeFiles.LongNameOfExisting(Path.GetFullPath(p)));
        var full = Long(path);
        string?[] roots =
        [
            string.IsNullOrWhiteSpace(ctx.Home) ? null : Path.Combine(ctx.Home, "Saved Games"),
            string.IsNullOrWhiteSpace(ctx.WinDocuments) ? null : Path.Combine(ctx.WinDocuments, "My Games"),
            ctx.WinLocalAppDataLow,
            ctx.SteamRoot
        ];
        return roots.Any(r => !string.IsNullOrWhiteSpace(r) && (full.Equals(Long(r!), StringComparison.OrdinalIgnoreCase)
                                                               || full.StartsWith(Long(r!) + "\\", StringComparison.OrdinalIgnoreCase)));
    }

    public void SetKeep(int keep) => config.Update(c => c.BackupsToKeep = Math.Clamp(keep, 1, 50));

    public void SetAutoBackup(bool on) => config.Update(c => c.AutoBackupSaves = on);

    public void SetAutoKeep(int keep) => config.Update(c => c.AutoBackupsToKeep = Math.Clamp(keep, 1, MaxAutoKeep));

    public void SetAutoMaxGameMb(int mb) => config.Update(c => c.AutoBackupMaxGameMb = Math.Clamp(mb, MinGameMb, MaxGameMb));

    public void SetAutoMaxTotalMb(int mb) => config.Update(c => c.AutoBackupMaxTotalMb = Math.Clamp(mb, MinTotalMb, MaxTotalMb));

    public void SetIncludeConfig(bool on)
    {
        config.Update(c => c.BackupConfigFiles = on);
        _scans.Clear();
    }

    internal SaveContext ContextFor(LibraryItem item)
    {
        var root = steamRoot();
        var steamId = SteamStore.AppIdOf(item);
        return SaveContext.ForCurrentUser(root) with
        {
            InstallDir = item.InstallDir,
            Store = item.Source switch
            {
                "steam" => "steam",
                "epic" => "epic",
                "gog" => "gog",
                "ubisoft" => "uplay",
                "xbox" => "microsoft",
                _ => null
            },
            StoreGameId = steamId,
            IncludeConfig = config.Current.BackupConfigFiles,
            BackupDir = BackupDir
        };
    }

    private async Task<(LibraryItem Item, ManifestEntry? Entry, string? Reason, bool Unavailable)> LookupAsync(string id)
    {
        var item = findItem(id) ?? throw new KeyNotFoundException($"unknown item {id}");
        if (item.Type == ItemType.App) return (item, null, Strings.Get("saves.noAppSaves"), false);
        var index = await manifest.GetAsync();
        if (index is null) return (item, null, Strings.Get("saves.dbNotLoaded"), true);
        long? steam = long.TryParse(SteamStore.AppIdOf(item), out var n) ? n : null;
        long? gog = item.Source == "gog" && item.Id.StartsWith("gog:", StringComparison.Ordinal) && long.TryParse(item.Id["gog:".Length..], out var g) ? g : null;
        var entry = index.Find(steam, item.Name, gog);
        return (item, entry, entry is null ? Strings.Get("saves.noLocations") : null, false);
    }

    public async Task<SaveInfoDto> GetInfoAsync(string id)
    {
        var (item, entry, reason, unavailable) = await LookupAsync(id);
        var backups = BackupsFor(item);
        var list = ToDto(backups.List(Ref(item)));
        if (entry is null) return new SaveInfoDto(false, unavailable, reason, null, [], 0, 0, 0, [], [], list, BackupDir);

        var scan = await ScanCachedAsync(item, entry);
        var last = scan.LastWriteUtc;
        string? note = null;
        if (scan.Patterns.Count == 0 && scan.RegistryKeys.Count > 0) note = Strings.Get("saves.registryOnly");
        else if (scan.Patterns.Count == 0) note = Strings.Get("saves.noLocationWindows");
        return new SaveInfoDto(scan.Patterns.Count > 0, false, note, entry.Name, scan.Locations, scan.Files.Count, scan.TotalBytes,
            last is null ? 0 : new DateTimeOffset(last.Value, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            scan.RegistryKeys, scan.Errors, list, BackupDir);
    }

    private async Task<SaveScan> ScanCachedAsync(LibraryItem item, ManifestEntry entry)
    {
        if (_scans.TryGetValue(item.Id, out var cached) && Now() - cached.At < ScanCacheTtl) return cached.Scan;
        var scan = await Task.Run(() => SaveLocator.Scan(entry, ContextFor(item)));
        _scans[item.Id] = (Now(), scan);
        return scan;
    }

    private static List<BackupDto> ToDto(IEnumerable<BackupInfo> list) =>
        list.Select(b => new BackupDto(b.Id, new DateTimeOffset(b.CreatedUtc, TimeSpan.Zero).ToUnixTimeMilliseconds(), b.SizeBytes, b.FileCount, b.Safety, b.Complete, b.Auto)).ToList();

    /// <summary>True if any process runs from a file below <paramref name="dir"/> (games started outside CouchHUD too).</summary>
    public static bool AnyProcessUnder(IEnumerable<(int Pid, string? Path)> processes, string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        var prefix = dir.TrimEnd('\\', '/') + "\\";
        return processes.Any(p => p.Path is not null && p.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private void RequireNotRunning(LibraryItem item)
    {
        if (isRunning(item)) throw new InvalidOperationException(Strings.Get("saves.gameRunning"));
    }

    /// <summary>One backup/restore per game at a time.</summary>
    private async Task<T> ExclusiveAsync<T>(string id, Func<Task<T>> work)
    {
        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0)) throw new InvalidOperationException(Strings.Get("saves.busy"));
        try
        {
            return await work();
        }
        finally
        {
            gate.Release();
            _scans.TryRemove(id, out _);
        }
    }

    public Task<BackupResult> BackupAsync(string id) => ExclusiveAsync(id, async () =>
    {
        var (item, entry, reason, _) = await LookupAsync(id);
        if (entry is null) throw new InvalidOperationException(reason);
        RequireNotRunning(item);
        // The size limit per game holds for a backup by hand too: a save folder that grows without end must not fill the drive.
        var max = AutoBackupLimits.From(config.Current).MaxGameBytes;
        return await Task.Run(() => ManualBackupCore(BackupsFor(item), Ref(item), SaveLocator.Scan(entry, ContextFor(item)), max));
    });

    /// <summary>A backup by hand: refused before copying if the saves are larger than the limit per game, stopped if they grow past it.</summary>
    internal static BackupResult ManualBackupCore(SaveBackupService backups, GameRef game, SaveScan scan, long maxBytes)
    {
        if (scan.TotalBytes > maxBytes)
        {
            return new BackupResult(null, 0, 0, [Strings.Get("saves.tooBigForLimit", ByteSize.CeilingMb(scan.TotalBytes), ByteSize.CeilingMb(maxBytes))], false, LimitReached: true);
        }
        // Exactly the files of the last complete backup by hand: no new version, the answer says so. An automatic backup is no
        // reference, it is rotated away on its own.
        if (UnchangedSince(backups, game, scan, byHand: true) is { } last) return new BackupResult(last.Id, last.FileCount, scan.TotalBytes, [], true, Unchanged: true);
        // An incomplete copy is compared after copying (which files fail is only known then): the same files as the last incomplete
        // backup by hand keep that one, so "Back up now" again and again pushes no older backup out (KeepSameIncomplete).
        return backups.Backup(game, scan, BackupOptions.Manual with { MaxBytes = maxBytes, KeepSameIncomplete = true });
    }

    /// <summary>
    /// The newest complete backup (not a safety one; with <paramref name="byHand"/> only one made by hand) if the saves are exactly
    /// the files in its manifest: same set of (root, path), same size and same change time for each. Else null. A restored older
    /// backup or a state copied in with old time stamps therefore differs from the newest backup and gets a new version.
    /// </summary>
    internal static BackupInfo? UnchangedSince(SaveBackupService backups, GameRef game, SaveScan scan, bool byHand = false)
    {
        var last = backups.List(game).FirstOrDefault(b => b is { Safety: false, Complete: true, Verified: true } && !(byHand && b.Auto));
        if (last is null || last.FileCount != scan.Files.Count) return null;
        var entries = backups.EntriesOf(game, last.Id);
        if (entries is null || entries.Count != scan.Files.Count) return null;
        return SaveBackupService.SameFiles(entries, scan.Files.Select(f => (f.Root, f.Rel, f.Size, f.LastWriteUtc))) ? last : null;
    }

    public Task<RestoreResult> RestoreAsync(string id, string backupId) => ExclusiveAsync(id, async () =>
    {
        var (item, entry, reason, _) = await LookupAsync(id);
        if (entry is null) throw new InvalidOperationException(reason);
        RequireNotRunning(item);
        return await Task.Run(() => BackupsFor(item).Restore(Ref(item), backupId, entry, ContextFor(item)));
    });

    public Task<bool> DeleteBackupAsync(string id, string backupId) => ExclusiveAsync(id, () =>
    {
        var item = findItem(id) ?? throw new KeyNotFoundException($"unknown item {id}");
        return Task.Run(() => BackupsFor(item).Delete(Ref(item), backupId));
    });

    // --- Automatic backup after a game ------------------------------------

    /// <summary>
    /// The automatic backup after a game ended (started by CouchHUD or seen running). Scan and copy run through
    /// <paramref name="run"/> (the host passes a thread in Windows' background mode: low CPU and disk priority), within the time limit
    /// of <paramref name="limits"/>. Never throws; the result says whether there is something to tell.
    /// </summary>
    public async Task<AutoBackupResult> AutoBackupAsync(string id, AutoBackupLimits limits,
        Func<Func<AutoBackupResult>, Task<AutoBackupResult>>? run = null, CancellationToken cancel = default)
    {
        if (!config.Current.AutoBackupSaves) return new AutoBackupResult(AutoBackupOutcome.Skipped);
        run ??= work => Task.Run(work);
        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        // A backup or restore by hand is under way: that one counts.
        if (!await gate.WaitAsync(0)) return new AutoBackupResult(AutoBackupOutcome.Skipped);
        var one = false;
        try
        {
            await _autoOne.WaitAsync(cancel);
            one = true;
            var (item, entry, _, _) = await LookupAsync(id);
            if (entry is null || item.Type == ItemType.App || isRunning(item)) return new AutoBackupResult(AutoBackupOutcome.Skipped);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(limits.Timeout);
            var token = timeout.Token;
            var backups = BackupsFor(item);
            var game = Ref(item);
            var ctx = ContextFor(item);
            return await run(() => AutoBackupCore(backups, game, entry, ctx, limits, token));
        }
        catch (OperationCanceledException)
        {
            return new AutoBackupResult(AutoBackupOutcome.TimedOut);
        }
        catch (KeyNotFoundException)
        {
            // Removed from the library meanwhile.
            return new AutoBackupResult(AutoBackupOutcome.Skipped);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new AutoBackupResult(AutoBackupOutcome.Failed, Detail: ex.Message);
        }
        finally
        {
            if (one) _autoOne.Release();
            gate.Release();
            _scans.TryRemove(id, out _);
        }
    }

    /// <summary>The work of the automatic backup, in this order: scan, per-game limit, unchanged, total limit, copy.</summary>
    internal static AutoBackupResult AutoBackupCore(SaveBackupService backups, GameRef game, ManifestEntry entry, SaveContext ctx,
        AutoBackupLimits limits, CancellationToken token)
    {
        // Cloud files only held online stay where they are: an automatic backup starts no download.
        var scan = SaveLocator.Scan(entry, ctx, limits.MaxFiles, token, avoidRecall: true);
        // A backup without them would count as complete, push real complete ones out by rotation and mix states on a restore:
        // nothing is written and nothing rotated until the files are on this PC.
        if (scan.SkippedOnline.Count > 0) return new AutoBackupResult(AutoBackupOutcome.OnlineOnly, Detail: scan.SkippedOnline.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (scan.Files.Count == 0) return new AutoBackupResult(AutoBackupOutcome.Skipped);
        if (scan.Truncated || scan.TotalBytes > limits.MaxGameBytes) return new AutoBackupResult(AutoBackupOutcome.GameTooBig, Detail: scan.TotalBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // Nothing written since the last good backup (and the same files): no new version that would push an older one out.
        if (UnchangedSince(backups, game, scan) is { } last) return new AutoBackupResult(AutoBackupOutcome.Unchanged, last.Id);

        var total = backups.TotalBytes() - backups.BytesFreedByNextAuto(game);
        if (total + scan.TotalBytes > limits.MaxTotalBytes) return new AutoBackupResult(AutoBackupOutcome.TotalFull);

        // KeepSameIncomplete: the same unreadable file after every game must not push the older incomplete automatic backups out.
        var result = backups.Backup(game, scan, new BackupOptions(Auto: true, MaxBytes: limits.MaxGameBytes, Level: System.IO.Compression.CompressionLevel.Fastest, Cancel: token,
            KeepSameIncomplete: true));
        if (result.Cancelled) return new AutoBackupResult(AutoBackupOutcome.TimedOut);
        // The same incomplete state as the last incomplete automatic backup: nothing new, so nothing to tell (that one was told).
        if (result.Unchanged) return new AutoBackupResult(AutoBackupOutcome.Unchanged, result.Id);
        if (result.LimitReached) return new AutoBackupResult(AutoBackupOutcome.GameTooBig);
        if (result.Id is null) return new AutoBackupResult(AutoBackupOutcome.Failed, Detail: result.Errors.FirstOrDefault());
        if (!result.Complete) return new AutoBackupResult(AutoBackupOutcome.Incomplete, result.Id, result.Errors.FirstOrDefault());
        return new AutoBackupResult(AutoBackupOutcome.Done, result.Id);
    }
}
