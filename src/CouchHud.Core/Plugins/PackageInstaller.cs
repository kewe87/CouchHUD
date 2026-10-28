using System.IO.Compression;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Plugins;

/// <summary>
/// Installs a plugin or skin from a .zip, as shared by the community: either the
/// files directly in the archive, or inside one top-level folder.
/// </summary>
public static class PackageInstaller
{
    public enum Kind
    {
        Plugin,
        Skin
    }

    public const int MaxFiles = 5000;
    public const long MaxUnpackedBytes = 500L * 1024 * 1024;

    /// <summary>
    /// Extracts into &lt;targetRoot&gt;/&lt;name&gt; and returns that folder. With <paramref name="expectedName"/>
    /// the package must come out under exactly that folder name (catalog installs).
    /// Everything is validated before the old version is touched.
    /// </summary>
    public static string Install(string zipPath, string targetRoot, Kind kind, string? expectedName = null) =>
        Install(zipPath, targetRoot, kind, expectedName, out _);

    /// <param name="folderHash">The <see cref="PluginTrust.HashFolder"/> of exactly the files that were unpacked, taken before they moved into place.</param>
    public static string Install(string zipPath, string targetRoot, Kind kind, string? expectedName, out string folderHash)
    {
        if (!File.Exists(zipPath) || !zipPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException(Strings.Get("package.pickZip"));
        // Nobody may write to the file while it is unpacked.
        using var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Install(stream, Path.GetFileNameWithoutExtension(zipPath), targetRoot, kind, expectedName, out folderHash);
    }

    /// <summary>
    /// The same from an open stream: the caller can hash the package and unpack it from one handle that keeps writers out
    /// (<see cref="PendingPackages.ApplyAll"/>), so what is unpacked is what was checked.
    /// </summary>
    internal static string Install(Stream zip, string zipName, string targetRoot, Kind kind, string? expectedName, out string folderHash)
    {
        folderHash = "";
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        var files = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
        if (files.Count == 0) throw new InvalidOperationException(Strings.Get("package.empty"));

        if (files.Count > MaxFiles || files.Sum(e => e.Length) > MaxUnpackedBytes) throw new InvalidOperationException(Strings.Get("package.tooLarge"));

        // One top folder for everything? Then that folder is the package.
        var tops = files.Select(e => e.FullName.Replace('\\', '/').Split('/')).Select(p => p.Length > 1 ? p[0] : "").Distinct().ToList();
        var prefix = tops.Count == 1 && tops[0].Length > 0 ? tops[0] + "/" : "";
        var name = expectedName ?? Sanitize(prefix.Length > 0 ? prefix.TrimEnd('/') : zipName);
        if (name.Length == 0 || name != Sanitize(name)) throw new InvalidOperationException(Strings.Get("package.badName"));
        // A catalog package that brings its own top folder must agree with the catalog.
        if (expectedName is not null && prefix.Length > 0 && !prefix.TrimEnd('/').Equals(expectedName, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(Strings.Get("package.folderMismatch"));

        var inner = files.Select(e => e.FullName.Replace('\\', '/')[prefix.Length..]).ToList();
        var valid = kind switch
        {
            Kind.Skin => inner.Contains("skin.json", StringComparer.OrdinalIgnoreCase),
            _ => inner.Any(f => !f.Contains('/') && f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        };
        if (!valid) throw new InvalidOperationException(kind == Kind.Skin ? Strings.Get("package.noSkin") : Strings.Get("package.noPlugin"));

        Directory.CreateDirectory(targetRoot);
        var target = Path.Combine(targetRoot, name);
        RemoveLeftovers(targetRoot);

        // No "../", absolute paths or drive tricks: everything stays inside the package folder.
        // Checked for all entries up front, so a bad archive never touches the installed version.
        var staging = Path.Combine(targetRoot, TempPrefix + "new-" + Guid.NewGuid().ToString("N"));
        var root = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
        var plan = new List<(ZipArchiveEntry Entry, string Destination)>();
        foreach (var entry in files)
        {
            var relative = entry.FullName.Replace('\\', '/')[prefix.Length..];
            if (relative.Contains(':') || relative.StartsWith('/')) throw new InvalidOperationException(Strings.Get("package.badPath"));
            var destination = Path.GetFullPath(Path.Combine(staging, relative));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(Strings.Get("package.badPath"));
            // .NET silently cuts an entry off at its declared size. A deflate stream cannot be (much) bigger than the
            // data it holds, so a compressed size far above the declared size means the header lies.
            if (entry.CompressedLength > entry.Length + entry.Length / 100 + 64) throw new InvalidDataException(Strings.Get("package.badSizes"));
            plan.Add((entry, destination));
        }

        // Unpack next to the target first, then swap: the old version is only moved aside, never deleted
        // before the new one is in place, and comes back if the swap fails.
        string? backup = null;
        try
        {
            long total = 0;
            foreach (var (entry, destination) in plan)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                total += Extract(entry, destination, MaxUnpackedBytes - total);
            }
            // The files as unpacked, before they move into the plugins folder (where any program of the user could change them).
            folderHash = PluginTrust.HashFolder(staging);

            if (Directory.Exists(target))
            {
                backup = Path.Combine(targetRoot, TempPrefix + "old-" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.Move(target, backup);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    backup = null;
                    throw new InvalidOperationException(Strings.Get("package.loaded", name));
                }
            }

            try
            {
                Directory.Move(staging, target);
            }
            catch
            {
                if (backup is not null && !Directory.Exists(target)) Directory.Move(backup, target);
                backup = null;
                throw;
            }
        }
        finally
        {
            TryDelete(staging);
            if (backup is not null) TryDelete(backup);
        }
        return target;
    }

    public const string TempPrefix = ".couchhud-";

    /// <summary>
    /// Copies one entry and counts the real bytes: an archive that declares less than it contains
    /// (zip bomb) is stopped at the declared size or the remaining budget instead of filling the disk.
    /// </summary>
    private static long Extract(ZipArchiveEntry entry, string destination, long budget)
    {
        var limit = Math.Min(entry.Length, budget);
        using var input = entry.Open();
        using var output = File.Create(destination);
        var buffer = new byte[81920];
        long written = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            written += read;
            if (written > limit) throw new InvalidDataException(Strings.Get("package.moreData"));
            output.Write(buffer, 0, read);
        }
        if (written != entry.Length) throw new InvalidDataException(Strings.Get("package.damaged"));
        return written;
    }

    private static void RemoveLeftovers(string targetRoot)
    {
        foreach (var dir in Directory.EnumerateDirectories(targetRoot, TempPrefix + "*")) TryDelete(dir);
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[plugins] could not remove {dir}: {ex.Message}");
        }
    }

    private static string Sanitize(string name) =>
        new string(name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or ' ').ToArray()).Trim(' ', '.');
}
