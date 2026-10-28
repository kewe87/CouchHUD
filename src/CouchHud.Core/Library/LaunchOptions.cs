using CouchHud.Core.Localization;
using CouchHud.Core.Platform;
using CouchHud.Sdk;

namespace CouchHud.Core.Library;

/// <summary>What the process start for a library entry looks like: no shell parsing, every part is its own field.</summary>
/// <param name="PackagedAppId">Set for a Store app: started through the activation manager, the shell is the fallback.</param>
/// <param name="Direct">Started without the shell (CreateProcess): a batch file runs through System32\cmd.exe, which the locked couch's
/// DisallowRun list refuses through the shell.</param>
public sealed record StartPlan(string FileName, string Arguments, string WorkingDirectory, string? PackagedAppId = null, bool Direct = false);

/// <summary>Startup parameters and working folder of an entry: parsing, checks and the start plan.</summary>
public static class LaunchOptions
{
    public const int MaxArgumentsLength = 2000;

    /// <summary>One line the user typed ("-d D:\x devtool --resume") as separate arguments; quotes group, nothing else is interpreted.</summary>
    public static IReadOnlyList<string> ParseArguments(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        if (text.Length > MaxArgumentsLength || text.Any(char.IsControl)) throw new ArgumentException(Strings.Get("entry.argsInvalid"));
        return Split(text);
    }

    /// <summary>The Windows rules (CommandLineToArgvW): quotes group, backslashes only count in front of a quote. The inverse of <see cref="CommandLine.Quote"/>.</summary>
    private static List<string> Split(string text)
    {
        var args = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        var has = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\')
            {
                var n = 0;
                while (i < text.Length && text[i] == '\\') { n++; i++; }
                if (i < text.Length && text[i] == '"')
                {
                    current.Append('\\', n / 2);
                    if (n % 2 == 1) current.Append('"');
                    else inQuotes = !inQuotes;
                }
                else
                {
                    current.Append('\\', n);
                    i--;
                }
                has = true;
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
                has = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (has) args.Add(current.ToString());
                current.Clear();
                has = false;
            }
            else
            {
                current.Append(c);
                has = true;
            }
        }
        if (has) args.Add(current.ToString());
        return args;
    }

    /// <summary>The arguments as the user would type them, so editing shows what was saved.</summary>
    public static string FormatArguments(IEnumerable<string> args) => CommandLine.Join(args);

    /// <summary>
    /// The working folder: empty means the default (the program's own folder); otherwise an absolute folder that exists.
    /// Returns the normalized path, or null for the default.
    /// </summary>
    public static string? ValidateWorkingDirectory(string? folder, string exePath, Func<string, bool>? directoryExists = null)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        directoryExists ??= Directory.Exists;
        var text = folder.Trim().Trim('"');
        string full;
        try
        {
            if (!Path.IsPathRooted(text) || text.Any(char.IsControl)) throw new ArgumentException(Strings.Get("entry.cwdMissing", text));
            full = Path.GetFullPath(text);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException(Strings.Get("entry.cwdMissing", text));
        }
        if (!directoryExists(full)) throw new ArgumentException(Strings.Get("entry.cwdMissing", full));
        var own = Path.GetDirectoryName(exePath);
        var same = own is not null && string.Equals(Path.TrimEndingDirectorySeparator(own), Path.TrimEndingDirectorySeparator(full), StringComparison.OrdinalIgnoreCase);
        return same ? null : full;
    }

    /// <summary>A batch file (.bat, .cmd): cmd.exe reads its command line with its own rules.</summary>
    public static bool IsScript(string? path) =>
        path is not null && (path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// What cmd.exe treats as syntax even inside quotes (% and ! expand variables) or outside them (&amp; | ^ &lt; &gt; ( ) chain, pipe,
    /// escape, redirect, group), and the quote itself (cmd has no escape for it, so it would end the quoting early). Refused in the
    /// parameters of a batch file instead of escaped: cmd's escaping depends on the quote state and on how often the line
    /// is read again (a "call" inside the script reads %1 a second time), so no quoting is safe for every script.
    /// </summary>
    private static readonly char[] ScriptSpecial = ['&', '|', '^', '<', '>', '(', ')', '%', '!', '"'];

    /// <summary>
    /// Parameters a batch file may get: no character cmd.exe interprets (see <see cref="ScriptSpecial"/>), no control characters, and plain
    /// ASCII only: a program the script hands them on to may read them through the ANSI code page, where Windows' "best fit" turns lookalikes
    /// (a full-width quote, ＆ or ％) into the very characters refused here.
    /// </summary>
    public static void CheckScriptArguments(IEnumerable<string> args)
    {
        foreach (var arg in args)
            if (arg.IndexOfAny(ScriptSpecial) >= 0 || arg.Any(c => c < ' ' || c > '~')) throw new ArgumentException(Strings.Get("entry.scriptArgsInvalid"));
    }

    /// <summary>
    /// The script's path is always quoted on the line, where &amp; | ^ &lt; &gt; ( ) are plain text ("Program Files (x86)" is fine); only
    /// what expands inside quotes (% and !), the quote itself and control characters are refused.
    /// </summary>
    public static bool IsScriptPathSafe(string path) => path.IndexOfAny(['%', '!', '"']) < 0 && !path.Any(char.IsControl);

    /// <summary>One argument for cmd.exe: quoted when it has spaces (the quote itself never gets here), nothing escaped.</summary>
    private static string ScriptQuote(string arg) => arg.Length == 0 || arg.Any(char.IsWhiteSpace) ? $"\"{arg}\"" : arg;

    /// <summary>
    /// A batch file's start: System32\cmd.exe by full path, /d (no AutoRun), /v:off (no delayed expansion, whatever the registry says),
    /// /s /c "&lt;"script"&gt; &lt;args&gt;" (/s strips exactly the outer quotes). Path and parameters must be free of cmd's special characters.
    /// </summary>
    public static StartPlan ScriptPlan(string script, IReadOnlyList<string> args, string workingDirectory, string? systemDirectory = null)
    {
        if (!IsScriptPathSafe(script)) throw new ArgumentException(Strings.Get("entry.scriptPathInvalid", script));
        CheckScriptArguments(args);
        var line = string.Join(' ', new[] { $"\"{script}\"" }.Concat(args.Select(ScriptQuote)));
        var cmd = Path.Combine(systemDirectory ?? Environment.SystemDirectory, "cmd.exe");
        return new StartPlan(cmd, $"/d /v:off /s /c \"{line}\"", workingDirectory, Direct: true);
    }

    /// <summary>
    /// Opens a batch file for its start and keeps it so: others may read it, nobody may write, rename or delete it while the handle is open
    /// (the launcher holds it until cmd.exe has ended, which reads the script line by line). With <paramref name="pinned"/> the content
    /// is compared on this very handle, so what was checked is what runs.
    /// </summary>
    public static FileStream HoldScript(string script, FileFingerprint? pinned)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(script, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException(Strings.Get("entry.targetInvalid", script));
        }
        if (pinned is { } pin && FileFingerprint.Of(stream) is var now && now != pin)
        {
            stream.Dispose();
            throw new InvalidOperationException(now.TooLarge ? LaunchOverrides.ScriptTooLarge(script) : Strings.Get("entry.targetChanged", Path.GetFileName(script)));
        }
        return stream;
    }

    /// <summary>
    /// The process start for a plan. Shell execute for everything but a batch file, so an exe that needs elevation gets its UAC prompt. A batch
    /// file's cmd.exe starts directly and without a console window (CREATE_NO_WINDOW): no black window in the taskbar next to the game. Its
    /// console still exists, only hidden, so "timeout" and the like behave as before; a "pause" or a question would wait unseen, so the launcher ends exactly that cmd.exe (ScriptWatch).
    /// Program, parameters and folder are the plan's, unchanged.
    /// </summary>
    public static System.Diagnostics.ProcessStartInfo StartInfo(StartPlan plan) => new(plan.FileName)
    {
        UseShellExecute = !plan.Direct,
        Arguments = plan.Arguments,
        WorkingDirectory = plan.WorkingDirectory,
        CreateNoWindow = plan.Direct
    };

    /// <summary>Builds the start. A working folder that has gone away since is an error, not a silent start in the wrong place.</summary>
    public static StartPlan Plan(LaunchTarget target, Func<string, bool>? directoryExists = null, string? systemDirectory = null)
    {
        directoryExists ??= Directory.Exists;
        if (target.Kind == LaunchKind.Url)
        {
            var id = InstalledApps.IdOf(target.Target);
            return new StartPlan(target.Target, "", "", InstalledApps.IsPackagedId(id) ? id : null);
        }
        var cwd = target.WorkingDirectory ?? "";
        if (cwd.Length > 0 && !directoryExists(cwd)) throw new InvalidOperationException(Strings.Get("entry.cwdMissing", cwd));
        if (IsScript(target.Target)) return ScriptPlan(target.Target, target.Args, cwd, systemDirectory);
        return new StartPlan(target.Target, CommandLine.Join(target.Args), cwd);
    }
}
