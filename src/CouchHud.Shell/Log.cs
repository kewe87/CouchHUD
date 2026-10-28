using System.Security.AccessControl;
using CouchHud.Core.Platform;

namespace CouchHud.Shell;

/// <summary>
/// Sends Console output to a log file. A WinExe has no console, and as a shell
/// there is nobody watching one anyway; the file is what gets attached to bug reports.
/// </summary>
public static class Log
{
    private const long MaxBytes = 2 * 1024 * 1024;

    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CouchHud", "logs");

    /// <param name="guard">The watcher process: no rotation (the HUD owns the file) and its own tag in the log.</param>
    public static void Init(bool guard = false)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var file = Path.Combine(Directory, "couchhud.log");
            // A hard reset may have left zero bytes at the end (lines the cache had not written yet): they go, the log keeps no garbage.
            var zeros = !guard && File.Exists(file) ? LineWriter.TrimTrailingZeros(file) : 0;
            if (!guard && File.Exists(file) && new FileInfo(file).Length > MaxBytes)
            {
                File.Move(file, Path.Combine(Directory, "couchhud.1.log"), overwrite: true);
            }
            // The HUD and the watcher write to this one file. Opened for appending only, every write call lands at the current
            // end of the file, whatever the other process wrote meanwhile; LineWriter makes each line one such call.
            var stream = new FileInfo(file).Create(FileMode.OpenOrCreate, FileSystemRights.AppendData, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None, null);
            var writer = new LineWriter(stream);
            Console.SetOut(writer);
            // Warnings and errors (restore, fail-closed) also reach the disk: after a hard reset they are what explains the state.
            Console.SetError(writer.Durable);
            Console.WriteLine(guard
                ? $"[guard] watcher process {Environment.ProcessId} started"
                : $"[app] CouchHUD {typeof(Log).Assembly.GetName().Version} starting, pid {Environment.ProcessId}");
            if (zeros > 0) Console.WriteLine($"[app] removed {zeros} zero bytes from the end of the log (lines lost to a hard reset)");
            writer.FlushToDisk();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging is best effort; never block startup because of it.
        }
    }
}
