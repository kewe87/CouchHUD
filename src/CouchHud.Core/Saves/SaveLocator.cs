using System.IO.Enumeration;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Saves;

/// <summary>The machine and game specifics that fill the manifest's placeholders.</summary>
public sealed record SaveContext
{
    public string Home { get; init; } = "";
    public string WinAppData { get; init; } = "";
    public string WinLocalAppData { get; init; } = "";
    public string WinLocalAppDataLow { get; init; } = "";
    public string WinDocuments { get; init; } = "";
    public string WinPublic { get; init; } = "";
    public string WinProgramData { get; init; } = "";
    public string WinDir { get; init; } = "";
    public string UserName { get; init; } = "";

    /// <summary>The folder holding all user profiles (C:\Users); empty: not checked. Nothing below it but the own and the public profile is touched.</summary>
    public string ProfilesDir { get; init; } = "";

    /// <summary>The folder the backups are written to; nothing below it is ever part of a save (a backup would back up itself). Empty: none.</summary>
    public string BackupDir { get; init; } = "";

    /// <summary>Tests only: decides which files count as held online only instead of their attributes.</summary>
    internal Func<FileInfo, bool>? HeldOnline { get; init; }

    /// <summary>Install folder of the game, null if unknown (then &lt;base&gt; paths can't be resolved).</summary>
    public string? InstallDir { get; init; }

    /// <summary>Main Steam folder (holds userdata), null without Steam.</summary>
    public string? SteamRoot { get; init; }
    public IReadOnlyList<string> SteamUserIds { get; init; } = [];

    /// <summary>"steam", "epic", "gog" … or null for a custom entry (then store conditions don't filter).</summary>
    public string? Store { get; init; }
    public string? StoreGameId { get; init; }

    /// <summary>Also back up paths tagged only "config".</summary>
    public bool IncludeConfig { get; init; }

    public static SaveContext ForCurrentUser(string? steamRoot = null) => new()
    {
        Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        WinAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        WinLocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        WinLocalAppDataLow = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow"),
        WinDocuments = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        WinPublic = Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public",
        WinProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        UserName = Environment.UserName,
        ProfilesDir = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/')) ?? "",
        SteamRoot = steamRoot,
        SteamUserIds = steamRoot is null ? [] : SteamUserIdsOf(steamRoot)
    };

    public static IReadOnlyList<string> SteamUserIdsOf(string steamRoot)
    {
        var userdata = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(userdata)) return [];
        try
        {
            return Directory.EnumerateDirectories(userdata)
                .Select(Path.GetFileName)
                .Where(n => n is { Length: > 0 } && n != "0" && n.All(char.IsAsciiDigit))
                .Select(n => n!)
                .ToList();
        }
        catch (IOException)
        {
            return [];
        }
    }

    /// <summary>
    /// Placeholder → folder, longest folder first. Backups store files relative to these so a
    /// restore works after the user name, drive letter or install folder changed.
    /// </summary>
    public IReadOnlyList<(string Token, string Dir)> Roots()
    {
        var list = new List<(string, string)>();
        void Add(string token, string? dir)
        {
            if (!string.IsNullOrWhiteSpace(dir)) list.Add((token, SaveLocator.Normalize(dir)));
        }
        Add("base", InstallDir);
        Add("steamRoot", SteamRoot);
        Add("steamLibrary", LibraryRoot);
        Add("winLocalAppDataLow", WinLocalAppDataLow);
        Add("winLocalAppData", WinLocalAppData);
        Add("winAppData", WinAppData);
        Add("winDocuments", WinDocuments);
        Add("winPublic", WinPublic);
        Add("winProgramData", WinProgramData);
        Add("winDir", WinDir);
        Add("home", Home);
        return list.OrderByDescending(r => r.Item2.Length).ToList();
    }

    /// <summary>Steam library folder of the game (can differ from the main Steam folder), null if not a Steam layout.</summary>
    public string? LibraryRoot => SaveLocator.LibraryRootOf(InstallDir);

    /// <summary>Folders a pattern must stay strictly below: nothing may be backed up or restored for the whole of them.</summary>
    internal IEnumerable<string> ProtectedRoots()
    {
        foreach (var d in new[] { Home, WinAppData, WinLocalAppData, WinLocalAppDataLow, WinDocuments, WinPublic, WinProgramData, SteamRoot, LibraryRoot })
        {
            if (!string.IsNullOrWhiteSpace(d)) yield return SaveLocator.Normalize(d);
        }
    }

    public string? RootDir(string token) => Roots().Where(r => r.Token == token).Select(r => r.Dir).FirstOrDefault();
}

public sealed record SaveFile(string FullPath, string Root, string Rel, long Size, DateTime LastWriteUtc);

/// <summary>The saves of one game on this PC: resolved path patterns and what is currently on disk.</summary>
public sealed record SaveScan(
    ManifestEntry Entry,
    IReadOnlyList<string> Patterns,
    IReadOnlyList<string> RegistryKeys,
    IReadOnlyList<SaveFile> Files)
{
    /// <summary>Problems while scanning (too many files, files outside every known folder). A backup of such a scan is incomplete.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>More files than <see cref="SaveLocator.MaxFiles"/>: the list is cut off.</summary>
    public bool Truncated { get; init; }

    /// <summary>Cloud files only held online (OneDrive placeholders) the automatic backup left out so as not to download them.</summary>
    public IReadOnlyList<string> SkippedOnline { get; init; } = [];

    public long TotalBytes => Files.Sum(f => f.Size);
    public DateTime? LastWriteUtc => Files.Count == 0 ? null : Files.Max(f => f.LastWriteUtc);

    /// <summary>Folders (or files) the saves live in, with whether they exist.</summary>
    public IReadOnlyList<string> Locations => Patterns.Select(SaveLocator.LiteralPrefix).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>Resolves manifest paths for Windows, expands wildcards and decides which paths a restore may write to.</summary>
public static class SaveLocator
{
    public const int MaxFiles = 20000;

    public static string Normalize(string path) => path.Replace('/', '\\').TrimEnd('\\');

    /// <summary>Whether the manifest path applies to this PC and tag selection.</summary>
    public static bool Applies(ManifestPath p, SaveContext ctx)
    {
        var tagged = p.Tags.Count == 0 || p.Tags.Contains("save", StringComparer.OrdinalIgnoreCase)
                     || (ctx.IncludeConfig && p.Tags.Contains("config", StringComparer.OrdinalIgnoreCase));
        if (!tagged) return false;
        if (p.When.Count == 0) return true;
        return p.When.Any(c =>
            (c.Os is null || c.Os.Equals("windows", StringComparison.OrdinalIgnoreCase))
            && (c.Store is null || ctx.Store is null || c.Store.Equals(ctx.Store, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>All concrete path patterns (may contain * ? **) for the entry. Unresolvable ones are dropped.</summary>
    public static List<string> ResolvePatterns(ManifestEntry entry, SaveContext ctx)
    {
        var result = new List<string>();
        foreach (var p in entry.Files.Where(f => Applies(f, ctx)))
        {
            foreach (var path in Expand(p.Path, entry, ctx))
            {
                if (!IsSafePattern(path, ctx)) continue;
                if (!result.Contains(path, StringComparer.OrdinalIgnoreCase)) result.Add(path);
            }
        }
        return result;
    }

    /// <summary>Substitutes placeholders; a placeholder with several values (Steam users) multiplies the result.</summary>
    internal static IEnumerable<string> Expand(string template, ManifestEntry entry, SaveContext ctx)
    {
        var gameDir = ctx.InstallDir is null ? null : Path.GetFileName(ctx.InstallDir.TrimEnd('\\', '/'));
        var libraryRoot = LibraryRootOf(ctx.InstallDir);

        var values = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["<home>"] = [ctx.Home],
            ["<winAppData>"] = [ctx.WinAppData],
            ["<winLocalAppData>"] = [ctx.WinLocalAppData],
            ["<winLocalAppDataLow>"] = [ctx.WinLocalAppDataLow],
            ["<winDocuments>"] = [ctx.WinDocuments],
            ["<winPublic>"] = [ctx.WinPublic],
            ["<winProgramData>"] = [ctx.WinProgramData],
            ["<winDir>"] = [ctx.WinDir],
            ["<osUserName>"] = [ctx.UserName],
            ["<game>"] = [gameDir ?? entry.InstallDir ?? entry.Name],
            ["<storeGameId>"] = ctx.StoreGameId is null ? [] : [ctx.StoreGameId],
            // Steam-only: userdata and the library layout mean nothing for other stores.
            ["<storeUserId>"] = ctx.Store == "steam" ? ctx.SteamUserIds.ToArray() : [],
            ["<base>"] = ctx.InstallDir is not null && !IsProtected(ctx.InstallDir, ctx) ? [ctx.InstallDir] : []
        };
        // <root> is the Steam library root for install-relative paths and the main Steam folder for userdata.
        var roots = new List<string>();
        if (ctx.Store == "steam")
        {
            if (libraryRoot is not null) roots.Add(libraryRoot);
            if (ctx.SteamRoot is not null) roots.Add(ctx.SteamRoot);
        }
        values["<root>"] = roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        IEnumerable<string> current = [template];
        foreach (var (key, options) in values)
        {
            if (!template.Contains(key, StringComparison.Ordinal)) continue;
            current = current.SelectMany(t => options.Select(o => t.Replace(key, o, StringComparison.Ordinal))).ToList();
        }
        foreach (var t in current)
        {
            // Anything still in <…> is a placeholder we can't fill on Windows (xdg …).
            if (t.Contains('<') && t.Contains('>')) continue;
            var path = Normalize(t);
            if (!Path.IsPathRooted(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.Contains("..", StringComparison.Ordinal)) continue;
            yield return path;
        }
    }

    /// <summary>"…\steamapps\common\Game" → "…" (the Steam library folder), else null.</summary>
    internal static string? LibraryRootOf(string? installDir)
    {
        if (installDir is null) return null;
        var common = Path.GetDirectoryName(installDir.TrimEnd('\\', '/'));
        var steamapps = common is null ? null : Path.GetDirectoryName(common);
        if (common is null || steamapps is null) return null;
        if (!Path.GetFileName(common).Equals("common", StringComparison.OrdinalIgnoreCase)) return null;
        if (!Path.GetFileName(steamapps).Equals("steamapps", StringComparison.OrdinalIgnoreCase)) return null;
        return Path.GetDirectoryName(steamapps);
    }

    private static bool HasWildcard(string s) => s.IndexOfAny(['*', '?']) >= 0;

    /// <summary>The leading part of the pattern without wildcards: where the save folder is.</summary>
    public static string LiteralPrefix(string pattern)
    {
        var segments = pattern.Split('\\');
        var literal = segments.TakeWhile(s => !HasWildcard(s)).ToArray();
        return literal.Length == segments.Length ? pattern : string.Join('\\', literal);
    }

    /// <summary>True for a drive root, a user-profile root, Windows or anything above those: never a save location itself.</summary>
    internal static bool IsProtected(string path, SaveContext ctx)
    {
        var p = Normalize(path);
        if (p.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length < 2) return true; // drive root
        if (IsUnder(p, ctx.WinDir)) return true;
        foreach (var root in ctx.ProtectedRoots())
        {
            // The folder itself or one of its parents (C:\Users).
            if (p.Equals(root, StringComparison.OrdinalIgnoreCase) || root.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool IsUnder(string path, string? dir) =>
        !string.IsNullOrWhiteSpace(dir)
        && (path.Equals(Normalize(dir), StringComparison.OrdinalIgnoreCase) || path.StartsWith(Normalize(dir) + "\\", StringComparison.OrdinalIgnoreCase));

    /// <summary>A pattern needs at least one literal folder below the profile/data roots; Windows and drive roots are out.</summary>
    internal static bool IsSafePattern(string pattern, SaveContext ctx)
    {
        var prefix = LiteralPrefix(pattern);
        // A single file right in a drive root (C:\save.dat) isn't a save location either.
        if (!HasWildcard(pattern) && prefix.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length < 3) return false;
        return !IsProtected(prefix, ctx);
    }

    public const string TempSuffix = ".couchhud-tmp";

    /// <summary>Token for files outside every known folder: "abs/C/Games/x/save.dat".</summary>
    public const string AbsoluteToken = "abs";

    /// <summary>Files currently on disk that match the patterns, mapped to the placeholder roots.</summary>
    /// <param name="avoidRecall">Leave out cloud files that are only online (the automatic backup must not start downloads).</param>
    public static SaveScan Scan(ManifestEntry entry, SaveContext ctx, int maxFiles = MaxFiles, CancellationToken ct = default, bool avoidRecall = false)
    {
        var online = new List<string>();
        // The long name: a backup folder given as an 8.3 path (PROGRA~2) must not slip past the comparison.
        var backupDir = string.IsNullOrWhiteSpace(ctx.BackupDir) ? null : Normalize(NativeFiles.LongNameOfExisting(Path.GetFullPath(ctx.BackupDir)));
        var heldOnline = ctx.HeldOnline ?? SavePathGuard.NeedsRecall;
        var patterns = ResolvePatterns(entry, ctx);
        var registry = entry.Registry.Where(r => Applies(r, ctx)).Select(r => r.Path).ToList();
        var files = new Dictionary<string, SaveFile>(StringComparer.OrdinalIgnoreCase);
        var errors = new List<string>();
        var truncated = false;
        var roots = ctx.Roots();
        var redirects = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        foreach (var pattern in patterns)
        {
            var hits = Glob(pattern, out var globCut);
            truncated |= globCut;
            foreach (var hit in hits)
            {
                foreach (var file in FilesOf(hit))
                {
                    ct.ThrowIfCancellationRequested();
                    // Left behind by an aborted restore; never part of a save.
                    if (file.Name.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (files.ContainsKey(file.FullName)) continue;
                    // The backups themselves, if the backup folder lies in a save folder: they would grow with every backup.
                    if (backupDir is not null && IsUnder(NativeFiles.LongName(file.FullName), backupDir)) continue;
                    if (avoidRecall && heldOnline(file))
                    {
                        online.Add(file.FullName);
                        continue;
                    }
                    if (files.Count >= maxFiles) { truncated = true; break; }
                    var (token, rel) = Relative(file.FullName, roots);
                    if (token is null)
                    {
                        errors.Add(Strings.Get("saves.skippedUnknown", file.FullName));
                        continue;
                    }
                    // A junction or link planted in a save folder (a mod, a download) must not pull other data into the backup.
                    var root = SavePathGuard.RootFor(token, rel, ctx);
                    if (root is null || SavePathGuard.IsInForeignProfile(file.FullName, ctx) || SavePathGuard.HasReparsePointBelow(root, file.FullName, redirects))
                    {
                        errors.Add(Strings.Get("saves.skippedRedirect", file.FullName));
                        continue;
                    }
                    files[file.FullName] = new SaveFile(file.FullName, token, rel, file.Length, file.LastWriteTimeUtc);
                }
            }
        }
        if (truncated) errors.Add(Strings.Get("saves.truncated", maxFiles));
        return new SaveScan(entry, patterns, registry, files.Values.OrderBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase).ToList())
        {
            Errors = errors,
            Truncated = truncated,
            SkippedOnline = online
        };
    }

    /// <summary>Splits a path into placeholder root and relative part (forward slashes), null if under none.</summary>
    public static (string? Token, string Rel) Relative(string fullPath, IReadOnlyList<(string Token, string Dir)> roots)
    {
        foreach (var (token, dir) in roots)
        {
            if (fullPath.Length > dir.Length && fullPath.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return (token, fullPath[(dir.Length + 1)..].Replace('\\', '/'));
            }
        }
        // Absolute fallback for plain drive paths (D:\Games\X\save.dat).
        if (fullPath.Length > 3 && char.IsAsciiLetter(fullPath[0]) && fullPath[1] == ':' && fullPath[2] == '\\')
        {
            return (AbsoluteToken, char.ToUpperInvariant(fullPath[0]) + "/" + fullPath[3..].Replace('\\', '/'));
        }
        return (null, "");
    }

    private static IEnumerable<FileInfo> FilesOf(string path)
    {
        if (File.Exists(path))
        {
            yield return new FileInfo(path);
            yield break;
        }
        if (!Directory.Exists(path)) yield break;
        // Junctions and links are not followed; cloud folders (OneDrive) are ordinary folders here.
        foreach (var f in SavePathGuard.FilesBelow(path)) yield return f;
    }

    // --- Wildcards -------------------------------------------------------

    /// <summary>Existing files and folders matching a pattern with * ? and ** (any number of folders).</summary>
    public static List<string> Glob(string pattern) => Glob(pattern, out _);

    public static List<string> Glob(string pattern, out bool cut)
    {
        var results = new List<string>();
        cut = false;
        var segments = pattern.Split('\\');
        if (segments.Length == 0) return results;
        // "C:" needs its backslash to mean the drive root.
        var start = segments[0].EndsWith(':') ? segments[0] + "\\" : segments[0];
        Walk(start, segments, 1, results);
        cut = results.Count >= MaxFiles;
        return results;
    }

    private static void Walk(string current, string[] segments, int index, List<string> results)
    {
        if (results.Count >= MaxFiles) return;
        if (index == segments.Length)
        {
            if (File.Exists(current) || Directory.Exists(current)) results.Add(current);
            return;
        }
        var seg = segments[index];
        if (seg.Length == 0) { Walk(current, segments, index + 1, results); return; }

        if (seg == "**")
        {
            foreach (var dir in SelfAndSubdirectories(current)) Walk(dir, segments, index + 1, results);
            return;
        }
        if (!HasWildcard(seg))
        {
            Walk(Path.Combine(current, seg), segments, index + 1, results);
            return;
        }
        if (!Directory.Exists(current)) return;
        var last = index == segments.Length - 1;
        try
        {
            foreach (var entry in new DirectoryInfo(current).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 && SavePathGuard.IsRedirect(entry.FullName)) continue;
                if (!FileSystemName.MatchesSimpleExpression(seg, entry.Name, true)) continue;
                if (!last && entry is not DirectoryInfo) continue;
                Walk(entry.FullName, segments, index + 1, results);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static IEnumerable<string> SelfAndSubdirectories(string dir)
    {
        if (!Directory.Exists(dir)) yield break;
        yield return dir;
        foreach (var d in SavePathGuard.DirectoriesBelow(dir)) yield return d;
    }

    // --- What a restore may write to --------------------------------------

    /// <summary>
    /// True if <paramref name="target"/> is one of the save paths or inside a folder one of the patterns
    /// matches. A restore writes nothing else, whatever a backup file claims.
    /// </summary>
    public static bool IsAllowedTarget(string target, IReadOnlyList<string> patterns)
    {
        var full = Normalize(Path.GetFullPath(target));
        var parts = full.Split('\\');
        foreach (var pattern in patterns)
        {
            var pat = pattern.Split('\\');
            // The path itself, or any parent folder, may be what the pattern names.
            for (var len = 1; len <= parts.Length; len++)
            {
                if (SegmentsMatch(pat, 0, parts, 0, len)) return true;
            }
        }
        return false;
    }

    private static bool SegmentsMatch(string[] pat, int pi, string[] path, int xi, int pathLen)
    {
        if (pi == pat.Length) return xi == pathLen;
        if (pat[pi] == "**")
        {
            for (var skip = xi; skip <= pathLen; skip++)
            {
                if (SegmentsMatch(pat, pi + 1, path, skip, pathLen)) return true;
            }
            return false;
        }
        if (xi >= pathLen) return false;
        var ok = HasWildcard(pat[pi])
            ? FileSystemName.MatchesSimpleExpression(pat[pi], path[xi], true)
            : string.Equals(pat[pi], path[xi], StringComparison.OrdinalIgnoreCase);
        return ok && SegmentsMatch(pat, pi + 1, path, xi + 1, pathLen);
    }
}
