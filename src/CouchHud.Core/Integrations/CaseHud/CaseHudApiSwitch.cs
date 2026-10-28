using CouchHud.Core.Localization;
using CouchHud.Core.Platform;

namespace CouchHud.Core.Integrations.CaseHud;

/// <summary>
/// Switches CaseHUD's API v1 on or off for the signed-in user. The one place that knows how: CaseHUD's command line
/// <c>CaseHUD.exe --set-api=on|off</c> (confirmed by the CaseHUD project as the stable way for the signed-in user: exit 0 = set, live
/// when an instance runs, otherwise in its config; as SYSTEM/LocalService/NetworkService it writes nothing, says one line on stderr
/// and ends with a code other than 0).
/// Never from the CouchHUD service (that would be SYSTEM's config, not the user's) and never through the setup's <c>/API=1</c> for
/// the same reason: the setup runs elevated (service or UAC, possibly another administrator account). Only from the shell, in the
/// user's session, with the exe of the installation (HKLM uninstall entry, below Program Files, no links on the way), never one from a
/// user-writable folder and never anything named in api.json or instance.pid.
/// </summary>
public interface ICaseHudApiSwitch
{
    /// <exception cref="InvalidOperationException">Not installed where it may be run, or CaseHUD said no (the text says what to do).</exception>
    Task SetAsync(bool on, CancellationToken ct = default);
}

/// <summary>A started <c>CaseHUD.exe --set-api=…</c> (stdout not redirected, stderr read in the background with a cap).</summary>
public interface ICaseHudCliProcess : ICliProcess
{
}

/// <summary>CaseHUD's command line; the shared rules (time limit, trusted path, log line) are <see cref="TrustedCli"/>.</summary>
public static class CaseHudCli
{
    public const string ApiOn = "--set-api=on";
    public const string ApiOff = "--set-api=off";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    public const int MaxLogChars = TrustedCli.MaxLogChars;

    public static string SetApiArguments(bool on) => on ? ApiOn : ApiOff;

    /// <summary>How long stderr may still take after the exit (a child holding the pipe open must not keep the call waiting).</summary>
    public static readonly TimeSpan StderrGrace = TrustedCli.StderrGrace;

    /// <summary>
    /// Waits for the process with a hard limit: on timeout or cancellation the whole process tree is killed (the installer's lock must
    /// never stay taken). After the exit, stderr gets <see cref="StderrGrace"/> at most; the exit code is what counts (0 = set).
    /// Null exit = timed out.
    /// </summary>
    public static Task<(int? Exit, string Stderr)> RunAsync(ICliProcess process, CancellationToken ct, TimeSpan? timeout = null, TimeSpan? stderrGrace = null) =>
        TrustedCli.RunAsync(process, ct, timeout ?? Timeout, stderrGrace);

    /// <summary>
    /// The installed exe may be run from the user's session: a full, canonical path to CaseHUD.exe directly or further below one of
    /// <paramref name="trustedRoots"/> (Program Files; only administrators write there). No relative paths, no "..", no UNC.
    /// </summary>
    public static bool IsTrustedInstallPath(string? exe, IEnumerable<string?> trustedRoots) =>
        TrustedCli.IsTrustedInstallPath(exe, CaseHudInfo.ExeName, trustedRoots);

    /// <summary>What CaseHUD wrote to stderr, as it may go into the log: one line, control characters removed, at most 200 characters.</summary>
    public static string LogLine(string? stderr) => TrustedCli.LogLine(stderr);
}

/// <summary>
/// The switch with what touches Windows behind delegates: <paramref name="trustedExe"/> gives the verified installed exe (or null),
/// <paramref name="run"/> runs it with one argument and returns exit code and stderr (null exit = did not end in time).
/// </summary>
public sealed class CaseHudApiSwitch(Func<string?> trustedExe, Func<string, string, CancellationToken, Task<(int? Exit, string Stderr)>> run, Action<string>? log = null) : ICaseHudApiSwitch
{
    public async Task SetAsync(bool on, CancellationToken ct = default)
    {
        var exe = trustedExe() ?? throw new InvalidOperationException(Strings.Get("casehud.apiSwitchUntrusted"));
        var arguments = CaseHudCli.SetApiArguments(on);
        var (exit, stderr) = await run(exe, arguments, ct);
        if (exit == 0)
        {
            log?.Invoke($"[casehud] {arguments}: done");
            return;
        }
        log?.Invoke($"[casehud] {arguments}: exit {(exit is { } e ? e.ToString(System.Globalization.CultureInfo.InvariantCulture) : "timeout")} {CaseHudCli.LogLine(stderr)}".TrimEnd());
        throw new InvalidOperationException(exit is null ? Strings.Get("casehud.apiSwitchTimeout") : Strings.Get(on ? "casehud.apiSwitchFailed" : "casehud.apiSwitchOffFailed", exit.Value));
    }
}
