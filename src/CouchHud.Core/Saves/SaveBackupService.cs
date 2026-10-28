using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Saves;

/// <summary>The game a backup belongs to: display name and the stable library id (folders are bound to the id).</summary>
public sealed record GameRef(string Name, string Key);

public sealed record BackupInfo(string Id, DateTime CreatedUtc, long SizeBytes, int FileCount, bool Safety, bool Complete)
{
    /// <summary>Made automatically after a game ended; rotated apart from the ones made by hand.</summary>
    public bool Auto { get; init; }

    /// <summary>Its manifest could be read: only such a file is a backup of CouchHUD that the rotation may delete.</summary>
    public bool Verified { get; init; }
}

/// <param name="LimitReached">The saves grew past <see cref="BackupOptions.MaxBytes"/> while they were copied: nothing was kept.</param>
/// <param name="Cancelled">Stopped from outside (time limit, CouchHUD closing): nothing was kept.</param>
/// <param name="Unchanged">Nothing changed since the backup <paramref name="Id"/>: no new version was made.</param>
public sealed record BackupResult(string? Id, int FileCount, long Bytes, IReadOnlyList<string> Errors, bool Complete, bool LimitReached = false, bool Cancelled = false, bool Unchanged = false);

/// <summary>How a backup runs: by hand (defaults) or automatically after a game, with a size limit and a way to stop it.</summary>
/// <param name="MaxBytes">Bytes read from the save folders at most; a folder that grows past it ends the backup without a result.</param>
/// <param name="KeepSameIncomplete">
/// An incomplete backup that holds exactly the files of the newest incomplete one of the same kind (by hand or automatic) is not
/// kept: the older one is the answer (<see cref="BackupResult.Unchanged"/>), and nothing is rotated. Without it, pressing "Back up now"
/// again and again while one file cannot be read (a game holding it, a planted hard link) pushed the older incomplete backups out.
/// </param>
public sealed record BackupOptions(bool Auto = false, long MaxBytes = long.MaxValue, CompressionLevel Level = CompressionLevel.Optimal, CancellationToken Cancel = default,
    bool KeepSameIncomplete = false)
{
    public static readonly BackupOptions Manual = new();
}

/// <summary>The saves grew past the limit while they were copied.</summary>
public sealed class BackupLimitException(string message) : IOException(message);

/// <param name="RolledBack">A write failed halfway and the state from before the restore was put back.</param>
public sealed record RestoreResult(int Restored, IReadOnlyList<string> Errors, string? SafetyBackupId, bool RolledBack = false);

/// <summary>
/// ZIP backups of a game's saves below <c>&lt;backupRoot&gt;\&lt;game&gt; [&lt;id&gt;]\&lt;yyyy-MM-dd_HH-mm&gt;.zip</c>.
/// Inside: <c>manifest.json</c> plus <c>files/&lt;placeholder&gt;/&lt;relative path&gt;</c>, so a restore finds the
/// right place even if the user name or install folder changed. Everything is written to a temp file
/// first and moved into place; single files that can't be read are reported, and such a backup is
/// marked incomplete (<c>_unvollstaendig</c>): it never counts toward the limit of complete ones and never pushes one out; incomplete
/// ones have a limit of their own of the same size (they would pile up with every backup of a folder holding an unreadable file).
/// Safety backups taken before a restore (<c>_vorher</c>) have their own, separate limit.
/// </summary>
public sealed partial class SaveBackupService(string backupRoot, int keep, int autoKeep = SaveBackupService.DefaultAutoKeep)
{
    public const int DefaultAutoKeep = 3;

    /// <summary>A restore never writes more than this in all (a manipulated backup could claim anything).</summary>
    public const long MaxRestoreBytes = 32L * 1024 * 1024 * 1024;

    /// <summary>Space a restore leaves free on the target drive.</summary>
    public const long RestoreFreeMargin = 512L * 1024 * 1024;
    private const string ManifestName = "manifest.json";
    private const int FormatVersion = 1;
    private const string SafetySuffix = "_vorher";
    private const string IncompleteSuffix = "_unvollstaendig";
    private const string AutoSuffix = "_auto";

    /// <summary>How many safety backups are kept per game, independent of the regular limit.</summary>
    public const int SafetyKeep = 3;

    /// <summary>A backup with fewer than this share of the previous one's files counts as incomplete.</summary>
    private const double SuspiciousShrink = 0.5;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    internal sealed record ZipEntryInfo(string Zip, string Root, string Rel, long Size, DateTime ModifiedUtc);

    internal sealed record ZipManifest(int Version, string Game, DateTime CreatedUtc, List<ZipEntryInfo> Entries)
    {
        public bool Complete { get; init; } = true;
        public bool Safety { get; init; }
        public bool Auto { get; init; }
    }

    public Func<DateTime> Now { get; init; } = () => DateTime.Now;

    /// <summary>Test seam: called before each file is written during a restore.</summary>
    internal Action<string>? BeforeWrite { get; init; }

    /// <summary>Test seam: replaces the scan of the current state taken for the safety backup.</summary>
    internal Func<ManifestEntry, SaveContext, SaveScan>? SafetyScan { get; init; }

    public string GameDirectory(GameRef game) => Path.Combine(backupRoot, FolderName(game));

    /// <summary>
    /// The game's backup folder, checked before anything is written or deleted there: it lies right below the backup folder, and neither
    /// is a junction or link (a planted one would send the backup, or a delete, somewhere else).
    /// </summary>
    private string SafeGameDirectory(GameRef game)
    {
        var root = SaveLocator.Normalize(Path.GetFullPath(backupRoot));
        var dir = SaveLocator.Normalize(Path.GetFullPath(GameDirectory(game)));
        // The whole chain from the drive on: a link anywhere above would send writes and deletes elsewhere (a subst drive is a drive).
        if (!string.Equals(Path.GetDirectoryName(dir), root, StringComparison.OrdinalIgnoreCase)
            || SavePathGuard.HasReparsePointBelow(SavePathGuard.DriveOf(root), dir))
        {
            throw new IOException(Strings.Get("saves.backupDirRedirected"));
        }
        return dir;
    }

    /// <summary>Bytes all backups in the backup folder take (one level of game folders; links are not followed).</summary>
    public long TotalBytes()
    {
        if (!Directory.Exists(backupRoot)) return 0;
        long total = 0;
        try
        {
            foreach (var dir in SavePathGuard.DirectoriesBelow(backupRoot, recurse: false))
            {
                foreach (var file in SavePathGuard.FilesBelow(dir, recurse: false)) total += file.Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return total;
    }

    /// <summary>What the rotation would delete of this game's automatic backups once one more is added.</summary>
    public long BytesFreedByNextAuto(GameRef game) =>
        List(game).Where(b => b is { Auto: true, Safety: false, Complete: true }).Skip(Math.Max(1, autoKeep) - 1).Sum(b => b.SizeBytes);

    public static string FolderName(GameRef game) => $"{SafeName(game.Name)} [{SafeName(game.Key)}]";

    /// <summary>Folder name older versions used (the display name only).</summary>
    public string LegacyDirectory(GameRef game) => Path.Combine(backupRoot, SafeName(game.Name));

    /// <summary>Moves a pre-id backup folder to the id-bound name. Call only if the name is unique in the library.</summary>
    public void MigrateLegacy(GameRef game)
    {
        var old = LegacyDirectory(game);
        var target = GameDirectory(game);
        if (!Directory.Exists(old) || Directory.Exists(target)) return;
        try
        {
            Directory.Move(old, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[saves] migrate {old}: {ex.Message}");
        }
    }

    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c)).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "Spiel" : cleaned.Length > 100 ? cleaned[..100] : cleaned;
    }

    [GeneratedRegex(@"^[0-9A-Za-z_\- ]{1,100}\.zip$")]
    private static partial Regex IdPattern();

    public static bool IsValidId(string id) => IdPattern().IsMatch(id) && Path.GetFileName(id) == id;

    /// <summary>ZIP can't store years before 1980 or after 2107 (and throws).</summary>
    internal static DateTimeOffset ZipTime(DateTime utc)
    {
        if (utc.Year < 1980) return new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        if (utc.Year > 2107) return new DateTimeOffset(2107, 12, 31, 0, 0, 0, TimeSpan.Zero);
        return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    // --- Backup ---------------------------------------------------------

    public BackupResult Backup(GameRef game, SaveScan scan, bool safety = false, string? protectedId = null) =>
        Backup(game, scan, BackupOptions.Manual, safety, protectedId);

    public BackupResult Backup(GameRef game, SaveScan scan, BackupOptions options, bool safety = false, string? protectedId = null)
    {
        var errors = new List<string>(scan.Errors);
        if (scan.Files.Count == 0) return new BackupResult(null, 0, 0, [Strings.Get("saves.noneFound"), .. errors], false);
        var auto = options.Auto && !safety;

        string dir;
        try
        {
            Directory.CreateDirectory(backupRoot);
            dir = SafeGameDirectory(game);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new BackupResult(null, 0, 0, [ex.Message], false);
        }
        var temp = Path.Combine(dir, "backup-" + Guid.NewGuid().ToString("N") + ".part");
        var entries = new List<ZipEntryInfo>();
        var rootCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        long read = 0;
        string? id = null;
        var complete = false;
        try
        {
            Directory.CreateDirectory(dir);
            // A planted link could appear between the check and the write.
            dir = SafeGameDirectory(game);
            // Left over when CouchHUD or Windows ended in the middle of a backup: it would count against the total limit forever.
            foreach (var stale in Directory.EnumerateFiles(dir, "backup-*.part"))
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(stale) > TimeSpan.FromDays(1)) TryDelete(stale);
            }
            var previous = safety ? null : List(game).FirstOrDefault(b => b is { Safety: false, Complete: true });
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                foreach (var file in scan.Files)
                {
                    var name = $"files/{file.Root}/{file.Rel}";
                    try
                    {
                        // Games often keep the save open; reading along is fine.
                        options.Cancel.ThrowIfCancellationRequested();
                        using var source = new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920);
                        // What was opened must be the file that was scanned: a link swapped in since then is not read.
                        if (!SavePathGuard.IsFinalPath(source.SafeFileHandle, RootPathOf(file), file.FullPath, rootCache))
                        {
                            throw new IOException(Strings.Get("saves.redirected"));
                        }
                        // A hard link may tie the save to any file of the drive: left out and named.
                        if (NativeFiles.LinkCount(source.SafeFileHandle) is > 1) throw new IOException(Strings.Get("saves.hardLink"));
                        var entry = zip.CreateEntry(name, options.Level);
                        entry.LastWriteTime = ZipTime(file.LastWriteUtc);
                        using (var target = entry.Open()) read = CopyLimited(source, target, read, options.MaxBytes, options.Cancel);
                        entries.Add(new ZipEntryInfo(name, file.Root, file.Rel, file.Size, file.LastWriteUtc));
                        bytes += file.Size;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException and not BackupLimitException and not OperationCanceledException)
                    {
                        errors.Add($"{file.Rel}: {ex.Message}");
                    }
                }

                complete = errors.Count == 0 && !scan.Truncated;
                if (complete && previous is { FileCount: >= 4 } && entries.Count < previous.FileCount * SuspiciousShrink)
                {
                    complete = false;
                    errors.Add(Strings.Get("saves.fewerFiles", entries.Count, previous.FileCount));
                }
                if (entries.Count > 0)
                {
                    var created = Now().ToUniversalTime();
                    var manifest = new ZipManifest(FormatVersion, game.Name, created, entries) { Complete = complete, Safety = safety, Auto = auto };
                    var m = zip.CreateEntry(ManifestName, CompressionLevel.Optimal);
                    using var w = m.Open();
                    JsonSerializer.Serialize(w, manifest, Json);
                }
            }

            if (entries.Count == 0)
            {
                TryDelete(temp);
                return new BackupResult(null, 0, 0, errors.Count > 0 ? errors : [Strings.Get("saves.noneReadable")], false);
            }

            // The same incomplete state as the newest incomplete backup of this kind: the new copy adds nothing and is not kept, so it
            // rotates nothing out. A copy that differs in any file (or is complete now) is kept as before.
            if (!complete && !safety && options.KeepSameIncomplete
                && List(game).FirstOrDefault(b => b is { Safety: false, Complete: false, Verified: true } && b.Auto == auto) is { } sameKind
                && EntriesOf(game, sameKind.Id) is { } before && SameFiles(before, entries.Select(e => (e.Root, e.Rel, e.Size, e.ModifiedUtc))))
            {
                TryDelete(temp);
                return new BackupResult(sameKind.Id, entries.Count, bytes, errors, false, Unchanged: true);
            }

            var baseName = Now().ToString("yyyy-MM-dd_HH-mm") + (safety ? SafetySuffix : "") + (auto ? AutoSuffix : "") + (complete ? "" : IncompleteSuffix);
            id = baseName + ".zip";
            for (var n = 2; File.Exists(Path.Combine(dir, id)); n++) id = $"{baseName}-{n}.zip";
            File.Move(temp, Path.Combine(dir, id));
        }
        catch (BackupLimitException ex)
        {
            TryDelete(temp);
            errors.Add(ex.Message);
            return new BackupResult(null, 0, 0, errors, false, LimitReached: true);
        }
        catch (OperationCanceledException)
        {
            TryDelete(temp);
            errors.Add(Strings.Get("saves.cancelled"));
            return new BackupResult(null, 0, 0, errors, false, Cancelled: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            TryDelete(temp);
            errors.Add(ex.Message);
            return new BackupResult(null, 0, 0, errors, false);
        }

        Rotate(game, id, protectedId);
        return new BackupResult(id, entries.Count, bytes, errors, complete);
    }

    /// <summary>
    /// A manifest's files are exactly these: the same set of (root, path), and the same size and change time for each. A manifest that
    /// names a file twice matches nothing.
    /// </summary>
    internal static bool SameFiles(IReadOnlyList<ZipEntryInfo> manifest, IEnumerable<(string Root, string Rel, long Size, DateTime ModifiedUtc)> files)
    {
        var known = new Dictionary<string, (long Size, DateTime Modified)>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in manifest)
        {
            if (!known.TryAdd(e.Root + "/" + e.Rel, (e.Size, e.ModifiedUtc))) return false;
        }
        var seen = 0;
        foreach (var f in files)
        {
            if (!known.TryGetValue(f.Root + "/" + f.Rel, out var k) || k.Size != f.Size || k.Modified.Ticks != f.ModifiedUtc.Ticks) return false;
            seen++;
        }
        return seen == known.Count;
    }

    /// <summary>The root folder a scanned file was found under (the drive for "abs" files).</summary>
    internal static string RootPathOf(SaveFile file)
    {
        if (file.Root == SaveLocator.AbsoluteToken) return file.FullPath[..2];
        var cut = file.FullPath.Length - file.Rel.Length - 1;
        return cut > 0 ? file.FullPath[..cut] : file.FullPath;
    }

    /// <summary>
    /// Regular complete backups: newest <c>keep</c>. Safety backups: newest <see cref="SafetyKeep"/>, counted
    /// apart. Incomplete ones are never touched. The new backup and a restore source always stay.
    /// </summary>
    internal void Rotate(GameRef game, params string?[] protectedIds)
    {
        // Only files that are backups of CouchHUD (manifest read) are ever deleted, and only in the checked game folder.
        var all = List(game).Where(b => b.Verified).ToList();
        string dir;
        try
        {
            dir = SafeGameDirectory(game);
        }
        catch (IOException)
        {
            return;
        }
        var regular = all.Where(b => !b.Safety && !b.Auto && b.Complete).Skip(Math.Max(1, keep));
        var automatic = all.Where(b => !b.Safety && b.Auto && b.Complete).Skip(Math.Max(1, autoKeep));
        // Incomplete automatic backups would pile up unasked after every game: they have the same limit, counted apart.
        var autoIncomplete = all.Where(b => !b.Safety && b.Auto && !b.Complete).Skip(Math.Max(1, autoKeep));
        // The same for incomplete backups by hand (a planted hard link and "Back up now" again and again).
        var regularIncomplete = all.Where(b => !b.Safety && !b.Auto && !b.Complete).Skip(Math.Max(1, keep));
        var safety = all.Where(b => b.Safety && b.Complete).Skip(SafetyKeep);
        foreach (var old in regular.Concat(regularIncomplete).Concat(automatic).Concat(autoIncomplete).Concat(safety).Where(b => !protectedIds.Contains(b.Id)))
        {
            TryDelete(Path.Combine(dir, old.Id));
        }
    }

    // --- List -----------------------------------------------------------

    /// <summary>Newest first, by the creation time stored inside the backup.</summary>
    public List<BackupInfo> List(GameRef game)
    {
        var dir = GameDirectory(game);
        if (!Directory.Exists(dir)) return [];
        var result = new List<BackupInfo>();
        foreach (var path in Directory.EnumerateFiles(dir, "*.zip"))
        {
            var info = new FileInfo(path);
            if (!IsValidId(info.Name)) continue;
            var stem = Path.GetFileNameWithoutExtension(info.Name);
            var count = 0;
            var created = info.LastWriteTimeUtc;
            var complete = !stem.Contains(IncompleteSuffix);
            var safety = stem.Contains(SafetySuffix);
            var auto = stem.Contains(AutoSuffix);
            var verified = false;
            try
            {
                using var zip = ZipFile.OpenRead(path);
                if (ReadManifest(zip) is { } m)
                {
                    count = m.Entries.Count;
                    created = m.CreatedUtc;
                    complete &= m.Complete;
                    safety |= m.Safety;
                    auto |= m.Auto;
                    verified = true;
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                complete = false;
            }
            result.Add(new BackupInfo(info.Name, created, info.Length, count, safety, complete) { Auto = auto, Verified = verified });
        }
        return result.OrderByDescending(b => b.CreatedUtc).ThenByDescending(b => b.Id, StringComparer.Ordinal).ToList();
    }

    public bool Delete(GameRef game, string id)
    {
        if (!IsValidId(id)) return false;
        var path = Path.Combine(SafeGameDirectory(game), id);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    /// <summary>The files a backup holds as its manifest names them (root, path, size, change time); null if it cannot be read.</summary>
    internal IReadOnlyList<ZipEntryInfo>? EntriesOf(GameRef game, string id)
    {
        if (!IsValidId(id)) return null;
        var path = Path.Combine(GameDirectory(game), id);
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return ReadManifest(zip)?.Entries;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static ZipManifest? ReadManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry(ManifestName);
        if (entry is null || entry.Length > 32 * 1024 * 1024) return null;
        using var s = entry.Open();
        return JsonSerializer.Deserialize<ZipManifest>(s, Json);
    }

    // --- Restore --------------------------------------------------------

    private sealed record PlanItem(ZipEntryInfo Item, string Target, string Root, ZipArchiveEntry Entry);

    /// <summary>
    /// Writes the backup's files back. Order: check every target, take a safety backup of the current state
    /// (aborting if that is not complete), write; if a write fails the state from before is put back.
    /// Only paths the manifest names as save locations are ever written, whatever the ZIP says.
    /// Files that exist now but not in the backup are left alone.
    /// </summary>
    public RestoreResult Restore(GameRef game, string id, ManifestEntry entry, SaveContext ctx)
    {
        if (!IsValidId(id)) throw new ArgumentException(Strings.Get("saves.invalidBackup"));
        var path = Path.Combine(GameDirectory(game), id);
        if (!File.Exists(path)) throw new FileNotFoundException(Strings.Get("saves.backupNotFound"));

        var errors = new List<string>();
        using var zip = ZipFile.OpenRead(path);
        var manifest = ReadManifest(zip) ?? throw new InvalidDataException(Strings.Get("saves.backupDamaged"));
        if (manifest.Version > FormatVersion) throw new InvalidDataException(Strings.Get("saves.backupNewer"));
        // A manipulated backup may list any number of files; no real save has more than a scan reads.
        if (manifest.Entries.Count > SaveLocator.MaxFiles) throw new InvalidDataException(Strings.Get("saves.restoreTooMany", SaveLocator.MaxFiles));

        var patterns = SaveLocator.ResolvePatterns(entry, ctx);

        // 1. Every target resolvable and writable, before anything changes.
        var plan = Plan(zip, manifest, ctx, patterns, errors);
        if (plan.Count == 0) throw new InvalidDataException(Strings.Get("saves.nothingToRestore") + (errors.FirstOrDefault() ?? Strings.Get("saves.empty")));
        RequireRoom(plan);
        // Folders the restore creates; removed again (when empty) if it does not go through.
        var created = new List<string>();
        var done = false;
        try
        {
            var result = RestorePlanned(game, id, entry, ctx, patterns, plan, errors, created);
            done = result.Errors.Count == 0 || result.Restored > 0;
            return result;
        }
        finally
        {
            if (!done) RemoveCreated(created);
        }
    }

    private RestoreResult RestorePlanned(GameRef game, string id, ManifestEntry entry, SaveContext ctx, IReadOnlyList<string> patterns,
        List<PlanItem> plan, List<string> errors, List<string> created)
    {
        var blocked = plan.Where(p => !IsWritable(p.Target, created)).Take(5).Select(p => p.Item.Rel).ToList();
        if (blocked.Count > 0) throw new IOException(Strings.Get("saves.notWritable") + string.Join(", ", blocked));

        // 2. Safety net: if it isn't complete, nothing has been touched yet.
        string? safetyId = null;
        var current = (SafetyScan ?? ((e, c) => SaveLocator.Scan(e, c)))(entry, ctx);
        if (current.Files.Count > 0)
        {
            var safety = Backup(game, current, safety: true, protectedId: id);
            if (safety.Id is null || !safety.Complete)
            {
                throw new IOException(Strings.Get("saves.safetyIncomplete")
                                      + string.Join("; ", safety.Errors.Take(3)));
            }
            safetyId = safety.Id;
        }

        // 3. Write; on failure undo.
        var written = new List<(string Target, bool Existed)>();
        var rootCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in plan)
            {
                BeforeWrite?.Invoke(p.Target);
                // Checked again right before the write: a link may have been planted in the save folder since the plan.
                ResolveTarget(p.Item, ctx, patterns);
                var existed = File.Exists(p.Target);
                WriteFile(p.Entry, p.Target, p.Item.ModifiedUtc, p.Root, created, rootCache);
                written.Add((p.Target, existed));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            errors.Add(Strings.Get("saves.writeFailed", ex.Message));
            var rollbackErrors = Rollback(game, written, safetyId, ctx, patterns, created, rootCache);
            errors.AddRange(rollbackErrors);
            return new RestoreResult(0, errors, safetyId, RolledBack: rollbackErrors.Count == 0);
        }
        return new RestoreResult(written.Count, errors, safetyId);
    }

    private List<PlanItem> Plan(ZipArchive zip, ZipManifest manifest, SaveContext ctx, IReadOnlyList<string> patterns, List<string> errors)
    {
        var plan = new List<PlanItem>();
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in manifest.Entries)
        {
            try
            {
                var target = ResolveTarget(item, ctx, patterns, out var root);
                // Never into the backups themselves (also when the backup folder is given by its 8.3 name).
                if (InBackupRoot(target)) throw new InvalidDataException(Strings.Get("saves.inBackupDir"));
                if (!item.Zip.StartsWith("files/", StringComparison.Ordinal) || zip.GetEntry(item.Zip) is not { } zipEntry)
                {
                    throw new InvalidDataException(Strings.Get("saves.fileMissing"));
                }
                // Links and folders stored in the ZIP are never written: only plain files come back.
                if (IsLinkOrFolder(zipEntry)) throw new InvalidDataException(Strings.Get("saves.illegalPath"));
                // The same target twice: the second would silently win over the first.
                if (!targets.Add(target)) throw new InvalidDataException(Strings.Get("saves.illegalPath"));
                plan.Add(new PlanItem(item, target, root, zipEntry));
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or NotSupportedException or PathTooLongException)
            {
                errors.Add($"{item.Rel}: {ex.Message}");
            }
        }
        return plan;
    }

    /// <summary>Whether <paramref name="path"/> lies in the backup folder, compared by long names; if a short name cannot be expanded: yes.</summary>
    internal bool InBackupRoot(string path)
    {
        if (NativeFiles.TryLongNameOfExisting(Path.GetFullPath(backupRoot)) is not { } longRoot
            || NativeFiles.TryLongNameOfExisting(Path.GetFullPath(path)) is not { } longPath) return true;
        var rootDir = SaveLocator.Normalize(longRoot);
        var full = SaveLocator.Normalize(longPath);
        return full.Equals(rootDir, StringComparison.OrdinalIgnoreCase) || full.StartsWith(rootDir + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether an open file lies in the backup folder, by the real paths Windows reports for both (a subst drive, a link or an 8.3 name
    /// on the way change nothing). Unknown real path of the backup folder or the file: yes (fail closed).
    /// </summary>
    internal bool IsInBackupFolder(Microsoft.Win32.SafeHandles.SafeFileHandle handle, Dictionary<string, string?> cache)
    {
        if (!OperatingSystem.IsWindows()) return false;
        const string key = "|backup-root|";
        if (!cache.TryGetValue(key, out var rootFinal))
        {
            rootFinal = NativeFiles.FinalPathOfDirectory(backupRoot);
            cache[key] = rootFinal;
        }
        if (rootFinal is null || NativeFiles.FinalPath(handle) is not { } final) return true;
        var top = rootFinal.TrimEnd('\\');
        return final.Equals(top, StringComparison.OrdinalIgnoreCase) || final.StartsWith(top + "\\", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Unix file type bits or Windows attributes in the ZIP's external attributes: a link (or a folder) is not a save file.</summary>
    internal static bool IsLinkOrFolder(ZipArchiveEntry entry)
    {
        var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        var windows = (FileAttributes)(entry.ExternalAttributes & 0xFFFF);
        return unixType is 0xA000 or 0x4000 || entry.FullName.EndsWith('/')
               || windows.HasFlag(FileAttributes.ReparsePoint) || windows.HasFlag(FileAttributes.Directory);
    }

    /// <summary>
    /// Refuses a restore that would write more than <see cref="MaxRestoreBytes"/> or fill a drive: a manipulated backup may declare
    /// any size. The copy itself never writes more than an entry declares (<see cref="WriteFile"/>).
    /// </summary>
    private static void RequireRoom(List<PlanItem> plan)
    {
        long total = 0;
        foreach (var p in plan) total += Math.Max(0, p.Entry.Length);
        if (total > MaxRestoreBytes) throw new IOException(Strings.Get("saves.restoreTooBig"));
        foreach (var drive in plan.GroupBy(p => Path.GetPathRoot(p.Target) ?? "", StringComparer.OrdinalIgnoreCase))
        {
            if (drive.Key.Length == 0) continue;
            long free;
            try
            {
                free = new DriveInfo(drive.Key).AvailableFreeSpace;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                continue;
            }
            if (free < drive.Sum(p => Math.Max(0, p.Entry.Length)) + RestoreFreeMargin) throw new IOException(Strings.Get("saves.restoreNoSpace"));
        }
    }

    /// <summary>Copies at most <paramref name="max"/> bytes in all (<paramref name="done"/> so far); returns the new total.</summary>
    internal static long CopyLimited(Stream source, Stream target, long done, long max, CancellationToken cancel)
    {
        var buffer = new byte[81920];
        int n;
        while ((n = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancel.ThrowIfCancellationRequested();
            done += n;
            if (done > max) throw new BackupLimitException(Strings.Get("saves.limitGrew"));
            target.Write(buffer, 0, n);
        }
        return done;
    }

    /// <summary>Creates a folder and notes which of its parts did not exist yet (deepest first), so a failed restore can take them back.</summary>
    private static void CreateDirectories(string dir, List<string> created)
    {
        var missing = new List<string>();
        for (var d = dir; !string.IsNullOrEmpty(d) && !Directory.Exists(d); d = Path.GetDirectoryName(d)) missing.Add(d);
        Directory.CreateDirectory(dir);
        created.AddRange(missing);
    }

    /// <summary>Removes the folders a failed restore created, as far as they are empty (deepest first).</summary>
    private static void RemoveCreated(List<string> created)
    {
        foreach (var dir in created.Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(d => d.Length))
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Probes whether the target can be written without changing it (the folder may get created, and is noted).</summary>
    private static bool IsWritable(string target, List<string> created)
    {
        try
        {
            if (Directory.Exists(target)) return false;
            var dir = Path.GetDirectoryName(target)!;
            CreateDirectories(dir, created);
            if (File.Exists(target))
            {
                // Read-only is fine, it gets cleared for the write; locked is not.
                using var fs = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                return true;
            }
            var probe = Path.Combine(dir, ".couchhud-probe-" + Guid.NewGuid().ToString("N") + SaveLocator.TempSuffix);
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A read-only file can't be opened ReadWrite; check the attribute alone.
            return File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReadOnly) != 0;
        }
    }

    private void WriteFile(ZipArchiveEntry zipEntry, string target, DateTime modifiedUtc, string root, List<string> created,
        Dictionary<string, string?> rootCache)
    {
        CreateDirectories(Path.GetDirectoryName(target)!, created);
        // A fresh, short name in the target folder nobody could have prepared (a planted link of the same name), created new, never
        // opened if it exists. The suffix keeps a leftover out of every scan.
        var temp = Path.Combine(Path.GetDirectoryName(target)!, $".{Guid.NewGuid():N}{SaveLocator.TempSuffix}");
        FileAttributes? original = File.Exists(target) ? File.GetAttributes(target) : null;
        var readOnly = original is { } a && (a & FileAttributes.ReadOnly) != 0;
        try
        {
            using (var source = zipEntry.Open())
            using (var dest = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920))
            {
                // The file really lies where it was meant to: no link on the way swapped in since the check.
                if (!SavePathGuard.IsFinalPath(dest.SafeFileHandle, root, temp, rootCache)) throw new IOException(Strings.Get("saves.redirected"));
                // Never into the backups, by the real paths of both (also through a subst drive or another name of the same folder).
                if (IsInBackupFolder(dest.SafeFileHandle, rootCache)) throw new IOException(Strings.Get("saves.inBackupDir"));
                // Never more than the entry declares (RequireRoom counted that): a ZIP bomb stops here.
                CopyLimited(source, dest, 0, Math.Max(0, zipEntry.Length), CancellationToken.None);
            }
            try
            {
                File.SetLastWriteTimeUtc(temp, modifiedUtc);
            }
            catch (ArgumentOutOfRangeException)
            {
                // Absurd stored date: keep "now".
            }
            if (readOnly) File.SetAttributes(target, original!.Value & ~FileAttributes.ReadOnly);
            File.Move(temp, target, true);
            if (readOnly) File.SetAttributes(target, original!.Value);
        }
        catch
        {
            TryDelete(temp);
            if (readOnly)
            {
                try { File.SetAttributes(target, original!.Value); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            throw;
        }
    }

    /// <summary>Deletes what the restore created and writes the safety backup back over what it changed.</summary>
    private List<string> Rollback(GameRef game, List<(string Target, bool Existed)> written, string? safetyId,
        SaveContext ctx, IReadOnlyList<string> patterns, List<string> created, Dictionary<string, string?> rootCache)
    {
        var errors = new List<string>();
        foreach (var (target, existed) in written.Where(w => !w.Existed))
        {
            try { File.Delete(target); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add($"Rollback {target}: {ex.Message}"); }
        }
        if (safetyId is null) return errors;
        try
        {
            using var zip = ZipFile.OpenRead(Path.Combine(GameDirectory(game), safetyId));
            var manifest = ReadManifest(zip) ?? throw new InvalidDataException(Strings.Get("saves.manifestMissing"));
            var plan = Plan(zip, manifest, ctx, patterns, errors);
            foreach (var p in plan)
            {
                try { WriteFile(p.Entry, p.Target, p.Item.ModifiedUtc, p.Root, created, rootCache); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add($"Rollback {p.Item.Rel}: {ex.Message}"); }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            errors.Add(Strings.Get("saves.rollbackFailed", ex.Message));
        }
        if (errors.Count > 0) errors.Add(Strings.Get("saves.beforeRestore", safetyId));
        return errors;
    }

    /// <summary>Absolute target for a manifest entry; throws if it would leave its root or the save locations.</summary>
    internal static string ResolveTarget(ZipEntryInfo item, SaveContext ctx, IReadOnlyList<string> patterns) =>
        ResolveTarget(item, ctx, patterns, out _);

    /// <summary>Names Windows reserves for devices (with any extension too): writing "NUL.sav" or "CON" never reaches a file.</summary>
    private static readonly HashSet<string> ReservedNames = new(
        ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
         .. Enumerable.Range(1, 9).Select(n => $"COM{n}"), .. Enumerable.Range(1, 9).Select(n => $"LPT{n}"),
         "COM\u00B9", "COM\u00B2", "COM\u00B3", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>A path part Windows would change or treat as a device: reserved names, a trailing dot or space.</summary>
    internal static bool IsIllegalPart(string part)
    {
        if (part.EndsWith('.') || part.EndsWith(' ')) return true;
        var stem = part.Split('.')[0].TrimEnd(' ');
        return ReservedNames.Contains(stem);
    }

    internal static string ResolveTarget(ZipEntryInfo item, SaveContext ctx, IReadOnlyList<string> patterns, out string root)
    {
        var parts = item.Rel.Split('/');
        string rootDir;
        if (item.Root == SaveLocator.AbsoluteToken)
        {
            // "C/Games/x/save.dat": drive letter first.
            if (parts.Length < 2 || parts[0].Length != 1 || !char.IsAsciiLetter(parts[0][0])) throw new InvalidDataException(Strings.Get("saves.illegalPath"));
            rootDir = char.ToUpperInvariant(parts[0][0]) + ":";
            parts = parts[1..];
        }
        else
        {
            rootDir = ctx.RootDir(item.Root) ?? throw new InvalidDataException(Strings.Get("saves.unknownRoot", item.Root));
        }
        if (item.Rel.Length == 0 || parts.Any(p => p.Length == 0 || p is "." or ".." || p.Contains(':') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || IsIllegalPart(p)))
        {
            throw new InvalidDataException(Strings.Get("saves.illegalPath"));
        }
        var full = Path.GetFullPath(Path.Combine(rootDir + "\\", string.Join('\\', parts)));
        if (!full.StartsWith(rootDir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(Strings.Get("saves.outsideRoot"));
        if (!SaveLocator.IsAllowedTarget(full, patterns)) throw new InvalidDataException(Strings.Get("saves.notSaveLocation"));
        // Only the own profile (and folders outside all profiles), and never through a junction or link in the save folder: the
        // pattern check above looks at the name only, a planted link would carry the write elsewhere.
        if (SavePathGuard.IsInForeignProfile(full, ctx) || SavePathGuard.HasReparsePointBelow(rootDir, full))
        {
            throw new InvalidDataException(Strings.Get("saves.redirected"));
        }
        root = rootDir;
        return full;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
