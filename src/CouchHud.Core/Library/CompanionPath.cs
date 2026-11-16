using CouchHud.Core.Guard;
using CouchHud.Core.Saves;
using Microsoft.Win32.SafeHandles;

namespace CouchHud.Core.Library;

/// <summary>
/// A program file held open (read, nobody may write or delete it) with its real path: links, junctions, 8.3 aliases and the other spellings of
/// a path are resolved by Windows from the open handle. The name checks run on this path, and CreateProcess gets exactly it.
/// </summary>
public sealed class HeldProgram(FileStream file, string path) : IDisposable
{
    /// <summary>The final path of the open file ("C:\Program Files\X\x.exe"), long names, no links.</summary>
    public string Path { get; } = path;

    /// <summary>The open file (read only, writers kept out): what is hashed, verified and started is this one.</summary>
    public FileStream Stream { get; } = file;

    private string? _sha256Hex;

    /// <summary>
    /// The SHA-256 of the held file's content, lower-case hex. Hashed once: the file is open with FileShare.Read (see
    /// <see cref="CompanionPath.Hold"/>), so nobody can write it while it is held and the content cannot differ between two calls.
    /// </summary>
    public string Sha256Hex()
    {
        if (_sha256Hex is { } known) return known;
        Stream.Position = 0;
        return _sha256Hex = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Stream)).ToLowerInvariant();
    }

    public void Dispose() => Stream.Dispose();
}

/// <summary>The canonical path of a companion program: what the name and folder rules are judged on, never the spelling the library holds.</summary>
public static class CompanionPath
{
    /// <summary>
    /// A path that is worth opening: a drive path to an .exe without a stream (<c>a.txt:x.exe</c>), wildcard, device or network form, without
    /// <c>..</c> or <c>.</c> segments and without a name that Windows would cut (trailing dot or space).
    /// </summary>
    public static bool IsPlainProgramPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) || path.Length > 259) return false;
        if (path.IndexOfAny(['"', '?', '*', '<', '>', '|']) >= 0 || path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        if (path.Length < 4 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/')) return false;
        // The drive colon is the only colon: anything later is an alternate data stream.
        if (path.IndexOf(':', 2) >= 0) return false;
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var part in path[3..].Split('\\', '/'))
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ')) return false;
        }
        return true;
    }

    /// <summary>
    /// Opens the program and reads its real path from the open handle. Null when the file is not there, cannot be opened for reading with
    /// writers kept out, has a short name Windows will not expand, or is not a plain file: the caller starts nothing.
    /// </summary>
    public static HeldProgram? Hold(string path)
    {
        if (!IsPlainProgramPath(path)) return null;
        if (!OperatingSystem.IsWindows()) return null;
        FileStream? stream = null;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var final = NativeFiles.FinalPath(stream.SafeFileHandle);
            if (final is null) { stream.Dispose(); return null; }
            // Expanded again by name: a short name left in the final path is never trusted.
            var expanded = NativeFiles.TryLongName(final);
            if (expanded is null) { stream.Dispose(); return null; }
            return new HeldProgram(stream, expanded);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            stream?.Dispose();
            return null;
        }
    }

    /// <summary>The real path, or null (see <see cref="Hold"/>).</summary>
    public static string? Resolve(string path)
    {
        using var held = Hold(path);
        return held?.Path;
    }

    /// <summary>
    /// Whether the real path may be a companion: the same file name as asked for (a link to another program is refused), nothing in the
    /// Windows folder, nothing that is not a program for the "may be a game" test. Pure; the real path comes from <see cref="Hold"/>.
    /// </summary>
    public static CompanionVerdict Judge(string asked, string real, string windowsDir)
    {
        if (!IsPlainProgramPath(real) && !IsPlainProgramPath(real.Replace('/', '\\'))) return CompanionVerdict.NoProgram;
        if (!string.Equals(System.IO.Path.GetFileName(asked.Trim().Trim('"')), System.IO.Path.GetFileName(real), StringComparison.OrdinalIgnoreCase)) return CompanionVerdict.NoProgram;
        if (LockedForeground.IsBrowser(System.IO.Path.GetFileNameWithoutExtension(real))) return CompanionVerdict.Browser;
        if (GameEntryRules.InWindowsFolder(real, windowsDir) || SafeTermination.IsSystemPath(real, windowsDir)) return CompanionVerdict.SystemFolder;
        return GameEntryRules.MayBeGame(real, windowsDir) ? CompanionVerdict.Ok : CompanionVerdict.Tool;
    }
}
