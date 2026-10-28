namespace CouchHud.Core.Platform;

/// <summary>
/// A started command line of an installed program (CaseHUD's <c>--set-api</c>, NordVPN's <c>-c</c>/<c>-d</c>): stdout is never
/// redirected, stderr only when the caller asked for it (read in the background with a cap).
/// </summary>
public interface ICliProcess : IDisposable
{
    Task WaitForExitAsync(CancellationToken ct);
    int ExitCode { get; }
    /// <summary>stderr (capped); it may never finish when a child process inherited the pipe. Empty when it was not redirected.</summary>
    Task<string> Stderr { get; }
    /// <summary>Ends the process and everything it started.</summary>
    void Kill();
    /// <summary>
    /// Whether the process has a window of its own by now (a GUI program that became the app instance). A command line that hands its
    /// request to a running instance never gets one. False when it cannot be told.
    /// </summary>
    bool HasWindow => false;
}

/// <summary>
/// What every "run the installed program's command line" in CouchHUD shares (CaseHUD and the VPN apps): where the program may be run
/// from, how long it may take and what of its output may reach the log. The Windows side (canonical path, no reparse points, starting
/// the process) is in the shell's <c>TrustedCliRunner</c>.
/// </summary>
public static class TrustedCli
{
    public const int MaxLogChars = 200;

    /// <summary>How long stderr may still take after the exit (a child holding the pipe open must not keep the call waiting).</summary>
    public static readonly TimeSpan StderrGrace = TimeSpan.FromSeconds(2);

    /// <summary>How often a running command line is asked whether it has a window (see <see cref="RunAsync"/>'s onWindow).</summary>
    public static readonly TimeSpan WindowPoll = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Waits for the process with a hard limit. On timeout or cancellation (CouchHUD quits) the whole process tree is killed, unless
    /// <paramref name="killOnTimeout"/> says no (a GUI program that became the app instance itself, see the VPN provider): then it is
    /// left running and the call only stops waiting. Without a rule (CaseHUD) it is always killed. After the exit, stderr gets
    /// <see cref="StderrGrace"/> at most. Null exit = timed out.
    /// <paramref name="onWindow"/> is called once when the process gets a window of its own while it is waited for (asked every
    /// <see cref="WindowPoll"/>, never after the exit or the time limit): the sign that the command line became the app itself.
    /// </summary>
    public static async Task<(int? Exit, string Stderr)> RunAsync(ICliProcess process, CancellationToken ct, TimeSpan timeout, TimeSpan? stderrGrace = null,
        Func<bool>? killOnTimeout = null, Action? onWindow = null, TimeSpan? windowPoll = null)
    {
        using (process)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(timeout);
            using var waited = CancellationTokenSource.CreateLinkedTokenSource(limit.Token);
            var watch = onWindow is null ? Task.CompletedTask : WatchWindowAsync(process, onWindow, windowPoll ?? WindowPoll, waited.Token);
            try
            {
                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                waited.Cancel();
                await watch;
                // Timeout and cancellation alike: ended unless the caller's rule says the process is the user's app now.
                if (killOnTimeout?.Invoke() != false) process.Kill();
                ct.ThrowIfCancellationRequested();
                return (null, "");
            }
            waited.Cancel();
            await watch;
            string stderr;
            try { stderr = await process.Stderr.WaitAsync(stderrGrace ?? StderrGrace, ct); }
            catch (TimeoutException) { stderr = ""; }
            return (process.ExitCode, stderr);
        }
    }

    private static async Task WatchWindowAsync(ICliProcess process, Action onWindow, TimeSpan poll, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (process.HasWindow)
                {
                    onWindow();
                    return;
                }
                await Task.Delay(poll, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// The installed exe may be run from the user's session: a full, canonical path to <paramref name="exeName"/> in a folder below one of
    /// <paramref name="trustedRoots"/> (Program Files; only administrators write there). No relative paths, no "..", no UNC, never directly
    /// in the root itself.
    /// </summary>
    public static bool IsTrustedInstallPath(string? exe, string exeName, IEnumerable<string?> trustedRoots)
    {
        if (!IsDrivePath(exe) || string.IsNullOrEmpty(exeName)) return false;
        var parts = exe!.Split('\\', '/');
        if (parts.Any(part => part is "." or "..") || !string.Equals(parts[^1], exeName, StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var root in trustedRoots)
        {
            if (!IsDrivePath(root) || root!.Split('\\', '/').Any(part => part is "." or "..")) continue;
            var prefix = root.TrimEnd('\\', '/') + '\\';
            if (exe.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && exe.Length > prefix.Length + exeName.Length) return true;
        }
        return false;
    }

    /// <summary>"C:\…": a local drive path (written out here, so the check is the same on every system the tests run on).</summary>
    public static bool IsDrivePath(string? path) =>
        path is { Length: >= 3 } && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';

    /// <summary>What a program wrote to stderr, as it may go into the log: one line, control characters removed, at most 200 characters.</summary>
    public static string LogLine(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return "";
        var clean = new string(stderr.Select(c => char.IsControl(c) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format ? ' ' : c).ToArray()).Trim();
        while (clean.Contains("  ", StringComparison.Ordinal)) clean = clean.Replace("  ", " ", StringComparison.Ordinal);
        return clean.Length <= MaxLogChars ? clean : clean[..MaxLogChars] + "…";
    }
}
