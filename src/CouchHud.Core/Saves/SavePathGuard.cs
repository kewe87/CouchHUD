using System.IO.Enumeration;
using Microsoft.Win32.SafeHandles;

namespace CouchHud.Core.Saves;

/// <summary>
/// Where save backups may read and write. A save folder is writable by the game, its mods and whatever was downloaded into it, so a
/// junction or symbolic link planted there must never take a backup or a restore somewhere else (another profile, the Windows
/// folder, the backup folder itself). The checks run on every path a backup reads and every target a restore writes.
/// </summary>
public static class SavePathGuard
{
    /// <summary>
    /// True if <paramref name="path"/> itself or a folder between it and <paramref name="root"/> (the root not counted) is a
    /// reparse point that redirects (junction, symbolic link, mount point; not a cloud placeholder). Parts that do not exist yet are no redirection.
    /// A path that is not below the root counts as redirected: there is nothing to vouch for.
    /// </summary>
    public static bool HasReparsePointBelow(string root, string path, Dictionary<string, bool>? cache = null)
    {
        // "C:" alone would mean the current folder of drive C.
        var top = root.Length == 2 && root[1] == ':' ? root : SaveLocator.Normalize(Path.GetFullPath(root));
        var current = SaveLocator.Normalize(Path.GetFullPath(path));
        if (!current.StartsWith(top + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        while (current.Length > top.Length)
        {
            if (cache is not null && cache.TryGetValue(current, out var known))
            {
                if (known) return true;
            }
            else
            {
                var redirected = IsRedirect(current);
                cache?.Add(current, redirected);
                if (redirected) return true;
            }
            current = SaveLocator.Normalize(Path.GetDirectoryName(current) ?? "");
        }
        return false;
    }

    /// <summary>
    /// True if <paramref name="path"/> is a reparse point that sends a path elsewhere: a junction, mount point or symbolic link
    /// (name surrogates) or an AF_UNIX socket. Cloud placeholders (OneDrive), deduplicated or compressed files keep their place and
    /// are fine. A reparse point whose kind Windows does not tell counts as redirecting.
    /// </summary>
    public static bool IsRedirect(string path)
    {
        try
        {
            if (!File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) return false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Can't look: don't vouch for it.
            return true;
        }
        var tag = NativeFiles.ReparseTag(path, out var known);
        if (!known) return true;
        return tag is { } t && IsRedirectingTag(t);
    }

    /// <summary>Name surrogates (IsReparseTagNameSurrogate: junction, mount point, symbolic link …) and AF_UNIX sockets.</summary>
    public static bool IsRedirectingTag(uint tag) => (tag & NameSurrogateBit) != 0 || tag == TagAfUnix;

    private const uint NameSurrogateBit = 0x20000000, TagAfUnix = 0x80000023;

    /// <summary>
    /// A cloud file that is not on this PC (only a placeholder): reading it would start a download. The automatic backup leaves such
    /// files alone; a backup by hand reads them. The RECALL attributes are set by the file system only; OFFLINE alone can be set by
    /// any program, so it counts only on a file with a cloud reparse tag (<paramref name="tag"/>), else the file is read normally.
    /// </summary>
    public static bool NeedsRecall(FileAttributes attributes, uint? tag = null) =>
        (attributes & (RecallOnDataAccess | RecallOnOpen)) != 0
        || ((attributes & FileAttributes.Offline) != 0 && tag is { } t && IsCloudTag(t));

    /// <summary>NeedsRecall for a file on disk: the reparse tag is read only when OFFLINE is set on a reparse point.</summary>
    public static bool NeedsRecall(FileInfo file)
    {
        var attributes = file.Attributes;
        if ((attributes & (RecallOnDataAccess | RecallOnOpen)) != 0) return true;
        if ((attributes & FileAttributes.Offline) == 0 || (attributes & FileAttributes.ReparsePoint) == 0) return false;
        return NeedsRecall(attributes, NativeFiles.ReparseTag(file.FullName, out _));
    }

    /// <summary>IO_REPARSE_TAG_CLOUD and CLOUD_1 … CLOUD_F (0x9000x01A): OneDrive and other cloud files; no name surrogate.</summary>
    public static bool IsCloudTag(uint tag) => (tag & 0xFFFF0FFF) == 0x9000001A;

    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000, RecallOnOpen = (FileAttributes)0x00040000;

    /// <summary>All files below <paramref name="dir"/>, without going into folders that redirect (junctions, links).</summary>
    public static IEnumerable<FileInfo> FilesBelow(string dir, bool recurse = true)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = recurse, IgnoreInaccessible = true, AttributesToSkip = 0 };
        return new FileSystemEnumerable<FileInfo>(dir, (ref FileSystemEntry e) => (FileInfo)e.ToFileSystemInfo(), options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
            ShouldRecursePredicate = (ref FileSystemEntry e) => !EntryRedirects(ref e)
        };
    }

    /// <summary>All folders below <paramref name="dir"/> (recursively), without folders that redirect and what is behind them.</summary>
    public static IEnumerable<string> DirectoriesBelow(string dir, bool recurse = true)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = recurse, IgnoreInaccessible = true, AttributesToSkip = 0 };
        return new FileSystemEnumerable<string>(dir, (ref FileSystemEntry e) => e.ToFullPath(), options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => e.IsDirectory && !EntryRedirects(ref e),
            ShouldRecursePredicate = (ref FileSystemEntry e) => !EntryRedirects(ref e)
        };
    }

    /// <summary>True for an entry that is a redirecting reparse point (checked by tag only when it is a reparse point at all).</summary>
    public static bool EntryRedirects(ref FileSystemEntry e) =>
        (e.Attributes & FileAttributes.ReparsePoint) != 0 && IsRedirect(e.ToFullPath());

    /// <summary>
    /// True if the file open behind <paramref name="handle"/> really is <paramref name="expected"/> below <paramref name="root"/>: the
    /// final path Windows resolves must be the root's final path plus the same rest. A link swapped in between check and open is
    /// caught here. Off Windows nothing can be said and true is returned; on Windows "unknown" is false.
    /// </summary>
    public static bool IsFinalPath(SafeFileHandle handle, string root, string expected, Dictionary<string, string?>? rootCache = null)
    {
        if (!OperatingSystem.IsWindows()) return true;
        var top = SaveLocator.Normalize(root.Length == 2 && root[1] == ':' ? root : Path.GetFullPath(root));
        // Short names (PROGRA~2) become long ones: the final path Windows reports is always long.
        var full = SaveLocator.Normalize(NativeFiles.LongName(Path.GetFullPath(expected)));
        if (!full.StartsWith(top + "\\", StringComparison.OrdinalIgnoreCase))
        {
            var longTop = SaveLocator.Normalize(NativeFiles.LongName(top + "\\"));
            if (!full.StartsWith(longTop + "\\", StringComparison.OrdinalIgnoreCase)) return false;
            top = longTop;
        }
        string? rootFinal;
        if (rootCache is null || !rootCache.TryGetValue(top, out rootFinal))
        {
            rootFinal = NativeFiles.FinalPathOfDirectory(top + "\\");
            rootCache?.Add(top, rootFinal);
        }
        var final = NativeFiles.FinalPath(handle);
        return rootFinal is not null && final is not null
               && string.Equals(final, rootFinal + full[top.Length..], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The drive (or share) a path starts at, as a root for <see cref="HasReparsePointBelow"/>.</summary>
    public static string DriveOf(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? "";
        return root.Length >= 2 && root[1] == ':' ? root[..2] : SaveLocator.Normalize(root);
    }

    /// <summary>
    /// True if the path lies in the profile of another Windows user (below the profiles folder, but neither in the own profile nor in
    /// the public one). Backups run as the signed-in user and stay in that user's data.
    /// </summary>
    public static bool IsInForeignProfile(string path, SaveContext ctx)
    {
        if (string.IsNullOrWhiteSpace(ctx.ProfilesDir)) return false;
        var full = SaveLocator.Normalize(Path.GetFullPath(path));
        if (!IsUnder(full, ctx.ProfilesDir)) return false;
        return !IsUnder(full, ctx.Home) && !IsUnder(full, ctx.WinPublic);
    }

    /// <summary>The folder a stored root token stands for on this PC ("abs" files: the drive), null if unknown.</summary>
    public static string? RootFor(string token, string rel, SaveContext ctx)
    {
        if (token != SaveLocator.AbsoluteToken) return ctx.RootDir(token);
        return rel.Length >= 2 && char.IsAsciiLetter(rel[0]) && rel[1] == '/' ? char.ToUpperInvariant(rel[0]) + ":" : null;
    }

    private static bool IsUnder(string path, string? dir) =>
        !string.IsNullOrWhiteSpace(dir)
        && (path.Equals(SaveLocator.Normalize(dir), StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(SaveLocator.Normalize(dir) + "\\", StringComparison.OrdinalIgnoreCase));
}
