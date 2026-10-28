using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using CouchHud.Core.Platform;
using CouchHud.Core.Plugins;
using CouchHud.Core.Updates;

namespace CouchHud.Shell.Services;

/// <summary>
/// Running the command line of an installed program from the user's session (the plugins' programs), shared so every caller
/// gets the same checks: the exe comes from an administrator-written source (HKLM uninstall or App Paths entry, Program Files), its
/// real path is read through an open handle, no reparse point may be on the way and it has to lie below Program Files. Never a path from
/// PATH, from a user-writable folder or from a file the program itself wrote. No shell, arguments only through ArgumentList.
/// </summary>
internal static class TrustedCliRunner
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder name, ref uint size);

    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>The image of a process; the limited right also works for a program that runs elevated (Process.SafeHandle would be refused).</summary>
    public static string? ProcessImage(int pid)
    {
        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle.IsInvalid) return null;
        var buffer = new StringBuilder(1024);
        var size = (uint)buffer.Capacity;
        return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString(0, (int)size) : null;
    }

    /// <summary>The real path of a file through an open handle (links and short names resolved); the text itself if it cannot be opened.</summary>
    public static string CanonicalPath(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return PathSafety.StripPrefix(CouchHud.Privileged.SetupProbe.FinalPath(file.SafeFileHandle));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }

    /// <summary>Program Files (64 and 32 bit), as full paths.</summary>
    public static IEnumerable<string> TrustedRoots() =>
        new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(Environment.GetFolderPath).Where(r => !string.IsNullOrEmpty(r)).Select(r => Path.GetFullPath(r)).Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="candidate"/> as it may be run: canonical, no reparse point on the way, named <paramref name="exeName"/> and below
    /// Program Files. Null (with one log line under <paramref name="logTag"/>) otherwise; a missing file is only logged with
    /// <paramref name="logMissing"/> (the VPN looks at several candidates every 30 seconds, most of them never exist).
    /// </summary>
    public static string? Verify(string? candidate, string exeName, string logTag, bool logMissing = true)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        try
        {
            var exe = CanonicalPath(Path.GetFullPath(candidate));
            if (!File.Exists(exe))
            {
                if (logMissing) Console.Error.WriteLine($"[{logTag}] {exeName} not found at the installed path, not run");
                return null;
            }
            PathSafety.RequireNoReparsePoints(exe);
            if (TrustedCli.IsTrustedInstallPath(exe, exeName, TrustedRoots())) return exe;
            Console.Error.WriteLine($"[{logTag}] {exeName} is not installed below Program Files, not run");
            return null;
        }
        catch (Exception ex) when (ex is CatalogException or IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            Console.Error.WriteLine($"[{logTag}] install path rejected ({ex.GetType().Name})");
            return null;
        }
    }

    /// <summary>
    /// Runs the verified exe with the given arguments, no window, no shell. stdout is never redirected (no pipe of ours for a GUI program
    /// or its children to hold); stderr only with <paramref name="readStderr"/>, read with a cap. The wait is bounded by
    /// <see cref="TrustedCli.RunAsync"/>. Null exit = timed out; -1 = could not be started.
    /// </summary>
    /// <param name="started">Called with the process id once the process runs (before the wait).</param>
    /// <param name="onWindow">Called once when the process gets a main window while it runs (it became the app itself).</param>
    public static async Task<(int? Exit, string Stderr)> RunAsync(string exe, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct,
        bool readStderr = true, Func<bool>? killOnTimeout = null, Action<int>? started = null, Action? onWindow = null)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = readStderr,
            WorkingDirectory = Path.GetDirectoryName(exe)!
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        Process? process;
        try { process = Process.Start(info); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return (-1, ex.GetType().Name); }
        if (process is null) return (-1, "");
        int id;
        try { id = process.Id; }
        catch (InvalidOperationException) { id = 0; }
        if (id != 0) started?.Invoke(id);
        return await TrustedCli.RunAsync(new CliProcess(process, readStderr), ct, timeout, killOnTimeout: killOnTimeout, onWindow: onWindow);
    }

    /// <summary>Starts the verified exe without arguments (the program's own window), not waiting for it. False if it could not be started.</summary>
    public static bool StartApp(string exe)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    private sealed class CliProcess : ICliProcess
    {
        private readonly Process _process;

        public CliProcess(Process process, bool readStderr)
        {
            _process = process;
            Stderr = readStderr ? ReadCappedAsync(process.StandardError) : Task.FromResult("");
        }

        public Task<string> Stderr { get; }
        public int ExitCode => _process.ExitCode;
        public Task WaitForExitAsync(CancellationToken ct) => _process.WaitForExitAsync(ct);

        public bool HasWindow
        {
            get
            {
                try
                {
                    _process.Refresh();
                    return !_process.HasExited && _process.MainWindowHandle != IntPtr.Zero;
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return false; }
            }
        }

        public void Kill()
        {
            try { _process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or AggregateException) { }
        }

        // Closes our end of the pipe: a read still waiting (a child holds the other end) ends with an exception, caught below.
        public void Dispose() => _process.Dispose();
    }

    /// <summary>At most 4096 characters are kept; the rest is read and dropped so the process never blocks on a full pipe.</summary>
    private static async Task<string> ReadCappedAsync(StreamReader reader)
    {
        var kept = new StringBuilder();
        var chunk = new char[1024];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(chunk)) > 0)
                if (kept.Length < 4096) kept.Append(chunk, 0, Math.Min(read, 4096 - kept.Length));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        return kept.ToString();
    }
}
