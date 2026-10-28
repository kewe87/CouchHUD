using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CouchHud.Core.Files;

/// <summary>
/// Writing small state files so that a hard reset or a power cut leaves the old or the new content, never an empty file or one full of
/// zero bytes (seen in the VM: NTFS had the rename on disk, the data of the file not yet). The new content goes to a temp file in the
/// same folder, written through to the disk (<see cref="FileOptions.WriteThrough"/> and <c>Flush(true)</c>), and only then replaces the
/// file, and the renames are flushed as well (<see cref="FlushRenames"/>); the file it replaces stays as <c>.bak</c> (<see cref="BackupOf"/>).
/// Readers take the <c>.bak</c> when the file is blank or broken.
/// </summary>
public static class DurableFile
{
    /// <summary>The copy from before the last save.</summary>
    public static string BackupOf(string path) => path + ".bak";

    /// <summary>Writes <paramref name="text"/> (UTF-8) to <paramref name="path"/>; with <paramref name="keepBackup"/> the old file stays as .bak.</summary>
    public static void WriteAllText(string path, string text, bool keepBackup = true) =>
        WriteAllBytes(path, Encoding.UTF8.GetBytes(text), keepBackup);

    public static void WriteAllBytes(string path, byte[] bytes, bool keepBackup = true)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        // A name of its own per call: two processes (the HUD and its watcher) may save the same file at the same moment.
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            WriteThrough(temp, bytes);
            SwapIn(temp, path, keepBackup ? BackupOf(path) : null);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        // ReplaceFile left the state before this save as .bak: the new content goes there as well, like config.json's good copy, so a
        // restored copy never brings back an older state (play time already used, a code brake already counting).
        if (keepBackup) RefreshBackup(path, bytes);
        CleanLeftoverTemps(path, StaleTempAge);
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> (what <paramref name="path"/> holds now) through to its .bak, so the copy is never older than the file.
    /// False when that failed (the .bak keeps the state before the save; the next save tries again).
    /// </summary>
    public static bool RefreshBackup(string path, byte[] bytes) => RefreshBackup(path, bytes, SwapIn);

    /// <summary><see cref="RefreshBackup(string, byte[])"/> with the swap given (tests: a ReplaceFile that fails half way).</summary>
    internal static bool RefreshBackup(string path, byte[] bytes, Action<string, string, string?> swap)
    {
        var backup = BackupOf(path);
        var temp = $"{backup}.{Guid.NewGuid():N}.tmp";
        var written = false;
        try
        {
            WriteThrough(temp, bytes);
            written = true;
            swap(temp, backup, null);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ReplaceFile without a backup name can fail half way (1176): the old .bak is gone already, the new copy lies only in the temp
            // file. Deleting that would leave no .bak at all; it takes the .bak's place instead (it is complete and on the disk).
            if (written && !File.Exists(backup) && File.Exists(temp) && TryMoveIn(temp, backup))
            {
                Console.Error.WriteLine($"[files] {backup} was replaced half way: the new copy took its place");
                return true;
            }
            Console.Error.WriteLine($"[files] could not update {backup}: {ex.Message}");
            TryDelete(temp);
            return false;
        }
    }

    private static bool TryMoveIn(string temp, string target)
    {
        try
        {
            File.Move(temp, target);
            FlushRenames(target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>How old a leftover temp file must be before it goes (a younger one may belong to a save of the other process right now).</summary>
    internal static readonly TimeSpan StaleTempAge = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Removes the temp files a hard reset left next to <paramref name="path"/> (<c>&lt;name&gt;.&lt;guid&gt;.tmp</c>, also the .bak's
    /// <c>&lt;name&gt;.bak.&lt;guid&gt;.tmp</c>) once they are older than <paramref name="age"/>, or dated in the future. Called after a save
    /// that worked: the file holds the newest state then, and no reader takes a temp file, so nothing is lost. Only names this class
    /// gives (a 32-digit hex guid), nothing else in the folder. Best effort.
    /// </summary>
    internal static void CleanLeftoverTemps(string path, TimeSpan age)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var name = Path.GetFileName(full);
            var now = DateTime.UtcNow;
            foreach (var temp in Directory.EnumerateFiles(Path.GetDirectoryName(full)!, name + ".*.tmp"))
            {
                var file = Path.GetFileName(temp);
                // The pattern also matches longer extensions (.tmpx) and short names: only exactly "<name>.<middle>.tmp".
                if (!file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || !file.StartsWith(name + ".", StringComparison.OrdinalIgnoreCase)
                    || file.Length < name.Length + 1 + 32 + 4) continue;
                var middle = file[(name.Length + 1)..^4];
                if (middle.StartsWith("bak.", StringComparison.OrdinalIgnoreCase)) middle = middle[4..];
                if (middle.Length != 32 || !middle.All(char.IsAsciiHexDigit)) continue;
                var time = File.GetLastWriteTimeUtc(temp);
                if (time <= now && now - time <= age) continue;
                TryDelete(temp);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Next save.
        }
    }

    /// <summary>Creates (or overwrites) <paramref name="path"/> and returns only once the bytes are on the disk.</summary>
    public static void WriteThrough(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>
    /// Puts the finished <paramref name="temp"/> in place of <paramref name="target"/>. ReplaceFile when the target exists (it works while a
    /// reader holds the file with the right to delete it, a move over the file does not); the old file becomes <paramref name="backup"/>.
    /// Returns once the renames are on the disk as well (<see cref="FlushRenames"/>). A reader on the .bak blocks only the .bak: ReplaceFile
    /// then fails before it changed anything (the old file cannot be moved over the open .bak), and the swap is done once more with a fresh
    /// name for the old file (<see cref="AsideOf"/>, removed right after); the .bak keeps its older, still valid state. Not without a backup
    /// name: should ReplaceFile then fail half way (1176), the old file is gone with no name left; with one it lies next to the target.
    /// </summary>
    public static void SwapIn(string temp, string target, string? backup)
    {
        if (!File.Exists(target)) File.Move(temp, target);
        else
        {
            try
            {
                File.Replace(temp, target, backup, ignoreMetadataErrors: true);
            }
            catch (IOException ex) when (backup is not null && (ex.HResult & 0xFFFF) == ErrorUnableToRemoveReplaced && File.Exists(temp))
            {
                var aside = AsideOf(backup);
                File.Replace(temp, target, aside, ignoreMetadataErrors: true);
                TryDelete(aside);
            }
        }
        FlushRenames(target);
    }

    private const int ErrorUnableToRemoveReplaced = 1175;

    /// <summary>A fresh name next to <paramref name="backup"/> for the old file when the .bak itself is held: <c>….bak.&lt;guid&gt;.old</c>.</summary>
    public static string AsideOf(string backup) => $"{backup}.{Guid.NewGuid():N}.old";

    /// <summary>
    /// Makes the renames that put <paramref name="target"/> in place durable. NTFS journals a rename in its log, and the log goes to the disk
    /// lazily: in the VM a hard reset right after a save kept the content of every temp file (written through) but lost the last rename. A
    /// flush of any handle on the volume writes the log up to now (that is why the earlier rename of the same save had survived: the .bak's
    /// temp file was flushed after it). So the folder is flushed after the swap: a directory handle (FILE_FLAG_BACKUP_SEMANTICS) does not
    /// collide with a reader of the file, and FlushFileBuffers on it writes the folder's index and the log. Without that handle the file
    /// itself is flushed. Not MoveFileEx with MOVEFILE_WRITE_THROUGH instead of ReplaceFile: it is documented only for a move done as copy
    /// and delete (another volume), and a move over a file fails while a reader holds it open even with delete sharing. Best effort: the
    /// content is in place either way, a failed flush is only logged (false).
    /// </summary>
    public static bool FlushRenames(string target)
    {
        if (!OperatingSystem.IsWindows()) return true;
        var folder = Path.GetDirectoryName(Path.GetFullPath(target))!;
        var error = Flush(folder, FileFlagBackupSemantics);
        if (error == 0 || (error = Flush(target, 0)) == 0) return true;
        Console.Error.WriteLine($"[files] could not flush the rename of {target} to the disk (error {error})");
        return false;
    }

    /// <summary>FlushFileBuffers on a handle of the folder itself (tests: that the first way works, not only the fallback).</summary>
    internal static bool FlushFolder(string folder) => OperatingSystem.IsWindows() && Flush(folder, FileFlagBackupSemantics) == 0;

    /// <summary>0 when flushed, else the Win32 error (read before the handle is closed, which would overwrite it).</summary>
    private static int Flush(string path, uint flags)
    {
        using var handle = CreateFileW(path, GenericWrite, FileShareAll, IntPtr.Zero, OpenExisting, flags, IntPtr.Zero);
        if (handle.IsInvalid) return Math.Max(1, Marshal.GetLastPInvokeError());
        return FlushFileBuffers(handle) ? 0 : Math.Max(1, Marshal.GetLastPInvokeError());
    }

    private const uint GenericWrite = 0x40000000, FileShareAll = 7, OpenExisting = 3, FileFlagBackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushFileBuffers(SafeFileHandle hFile);

    /// <summary>What a cut-off write leaves: nothing, only white space or only zero bytes.</summary>
    public static bool IsBlank(string? text)
    {
        if (string.IsNullOrEmpty(text)) return true;
        foreach (var ch in text)
            if (ch != '\0' && ch != '﻿' && !char.IsWhiteSpace(ch)) return false;
        return true;
    }

    /// <summary>
    /// Reads the file, or its .bak when the file is missing, blank or not accepted by <paramref name="valid"/> (and the .bak is). Null when
    /// neither can be used. <paramref name="fromBackup"/> says which one it was.
    /// </summary>
    public static string? ReadValid(string path, Func<string, bool> valid, out bool fromBackup)
    {
        fromBackup = false;
        if (TryRead(path) is { } text && !IsBlank(text) && valid(text)) return text;
        if (TryRead(BackupOf(path)) is { } backup && !IsBlank(backup) && valid(backup))
        {
            fromBackup = true;
            return backup;
        }
        return null;
    }

    private static string? TryRead(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Removes the file and its .bak (a state that must start over completely).</summary>
    public static void DeleteWithBackup(string path)
    {
        File.Delete(path);
        File.Delete(BackupOf(path));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp file is harmless.
        }
    }
}
