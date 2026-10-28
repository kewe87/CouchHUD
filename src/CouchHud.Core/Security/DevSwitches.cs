namespace CouchHud.Core.Security;

/// <summary>
/// The WebView2 loader reads overrides from the process environment before it looks at the options CouchHUD passes: extra browser
/// arguments (--remote-debugging-port lets any local program drive the HUD page), another "runtime" folder (foreign code), another
/// user data folder, release channel, script debugger pipe and more. Any program of the user, and the user with <c>setx</c>, can put
/// such a variable into HKCU\Environment. CouchHUD therefore removes every variable with this prefix from its own environment
/// before any WebView2 is created. Generic on purpose: new variables of later loader versions are covered too. Child processes
/// (games, launchers, helpers) inherit the cleaned environment; nothing CouchHUD starts needs these variables.
/// </summary>
public static class WebViewEnvironment
{
    public const string Prefix = "WEBVIEW2_";

    /// <summary>The names among <paramref name="names"/> the loader could act on (case-insensitive, like Windows), sorted, once each.</summary>
    public static IReadOnlyList<string> Overrides(IEnumerable<string> names) =>
        [.. names.Where(n => n.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Removes the overrides from an environment block; returns the removed names (never values: they may be anything).</summary>
    public static IReadOnlyList<string> Scrub(IDictionary<string, string?> environment)
    {
        var removed = Overrides(environment.Keys);
        foreach (var name in environment.Keys.Where(k => removed.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList()) environment.Remove(name);
        return removed;
    }

    /// <summary>The variables the loader documents, removed by name too (in case enumerating the block ever misses one).</summary>
    public static readonly IReadOnlyList<string> Known =
    [
        "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", "WEBVIEW2_USER_DATA_FOLDER",
        "WEBVIEW2_RELEASE_CHANNEL_PREFERENCE", "WEBVIEW2_RELEASE_CHANNELS", "WEBVIEW2_CHANNEL_SEARCH_KIND",
        "WEBVIEW2_PIPE_FOR_SCRIPT_DEBUGGER", "WEBVIEW2_WAIT_FOR_SCRIPT_DEBUGGER"
    ];

    private const int MaxRounds = 8;

    /// <summary>Removes the overrides from this process's environment; returns the removed names.</summary>
    public static IReadOnlyList<string> ScrubProcess() =>
        ScrubProcess(() => Environment.GetEnvironmentVariables().Keys.OfType<string>().ToList(), name => Environment.SetEnvironmentVariable(name, null),
            name => Environment.GetEnvironmentVariable(name) is not null);

    /// <summary>
    /// The known names first, then rounds over the enumerated block until nothing with the prefix is left (at most eight rounds; a name
    /// that will not go is left for the caller to see in the returned list). Testable with a fake environment.
    /// </summary>
    public static IReadOnlyList<string> ScrubProcess(Func<IReadOnlyCollection<string>> names, Action<string> remove, Func<string, bool> exists)
    {
        var removed = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Known)
        {
            if (!exists(name)) continue;
            remove(name);
            removed.Add(name);
        }
        for (var round = 0; round < MaxRounds; round++)
        {
            var left = Overrides(names());
            if (left.Count == 0) break;
            foreach (var name in left)
            {
                remove(name);
                removed.Add(name);
            }
        }
        return [.. removed];
    }

    /// <summary>
    /// The one variable CouchHUD's own code puts back: the WPF WebView2 control writes its DefaultBackgroundColor into this process's
    /// environment (for the first paint before the browser runs). Only a colour; still removed like every other, but not worth a log
    /// line each time a web view is made.
    /// </summary>
    public const string OwnBackgroundColor = "WEBVIEW2_DEFAULT_BACKGROUND_COLOR";

    /// <summary>The removed names that did not come from the WPF control itself: these are worth a log line every time.</summary>
    public static IReadOnlyList<string> Foreign(IEnumerable<string> removed) =>
        [.. removed.Where(n => !string.Equals(n, OwnBackgroundColor, StringComparison.OrdinalIgnoreCase))];

    /// <summary>Whether anything with the prefix is still there (after <see cref="ScrubProcess()"/>: should never be).</summary>
    public static bool AnyLeft() => Overrides(Environment.GetEnvironmentVariables().Keys.OfType<string>()).Count > 0;
}

/// <summary>What kind of access control entry: one that grants, one that only takes away, or one the check does not understand.</summary>
public enum AceKind
{
    Allow,
    Deny,
    /// <summary>Callback, object, compound or custom entries: may grant under conditions the check cannot see, so they count as granting.</summary>
    Unknown
}

/// <summary>One access rule of a registry key, as far as the check needs it (SIDs as strings, rights as the raw mask).</summary>
public sealed record KeyAccessRule(string Sid, AceKind Kind, int Rights, bool InheritOnly)
{
    public KeyAccessRule(string sid, bool allow, int rights, bool inheritOnly) : this(sid, allow ? AceKind.Allow : AceKind.Deny, rights, inheritOnly) { }
}

/// <summary>What the machine-wide developer switch (HKLM\SOFTWARE\CouchHud) says, read by the shell.</summary>
/// <param name="AdminOnly">Owner and every allowed writer are SYSTEM, Administrators or TrustedInstaller (see <see cref="DevMode.IsAdminOnly"/>).</param>
public sealed record MachineDevSwitch(bool AdminOnly, int? DeveloperMode, int? RemoteDebuggingPort)
{
    public static readonly MachineDevSwitch None = new(false, null, null);
}

/// <summary>What a start may do for developers. Everything off unless a debug build or an administrator switched it on.</summary>
/// <param name="Dev">--dev counts: window mode, DevTools, context menu, browser keys, no recovery wait, relaxed checks for a copy without service.</param>
/// <param name="RemoteDebuggingPort">Chrome DevTools Protocol on 127.0.0.1 for the HUD (the browser pane gets the next port); null: none.</param>
public sealed record DevDecision(bool Dev, int? RemoteDebuggingPort, string Reason);

public static class DevMode
{
    public const string MachineKey = @"SOFTWARE\CouchHud";
    public const string DeveloperModeValue = "DeveloperMode";
    public const string RemoteDebuggingPortValue = "RemoteDebuggingPort";
    public const string DevSwitch = "--dev";

    public const string SystemSid = "S-1-5-18";
    public const string AdministratorsSid = "S-1-5-32-544";
    public const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    // Rights that change the key or its values: KEY_SET_VALUE, KEY_CREATE_SUB_KEY, KEY_CREATE_LINK, DELETE, WRITE_DAC, WRITE_OWNER,
    // GENERIC_WRITE, GENERIC_ALL, MAXIMUM_ALLOWED.
    private const int WriteRights = 0x2 | 0x4 | 0x20 | 0x10000 | 0x40000 | 0x80000 | 0x40000000 | 0x10000000 | 0x02000000;

    private static readonly HashSet<string> Trusted = new(StringComparer.OrdinalIgnoreCase) { SystemSid, AdministratorsSid, TrustedInstallerSid };

    /// <summary>
    /// Only administrators can have set the values: the owner is trusted (an owner can always rewrite the access list) and no allowed
    /// rule that applies to the key itself gives write rights to anyone else. Inherit-only rules (CREATOR OWNER on HKLM\SOFTWARE) do
    /// not apply to the key; deny rules only take rights away.
    /// </summary>
    /// A null access list (no DACL: everyone may do everything) is never administrator-only; an entry of a kind the check does not
    /// understand counts as granting everything (fail closed).
    public static bool IsAdminOnly(string? ownerSid, IEnumerable<KeyAccessRule>? rules) =>
        ownerSid is not null && Trusted.Contains(ownerSid) && rules is not null
        && rules.All(r => r.InheritOnly || r.Kind == AceKind.Deny || Trusted.Contains(r.Sid) || (r.Kind == AceKind.Allow && (r.Rights & WriteRights) == 0));

    /// <summary>A registry value as the switch reads it: only a REG_DWORD counts (a string "1", a QWORD or binary data are ignored).</summary>
    public static int? Dword(object? value, Microsoft.Win32.RegistryValueKind kind) =>
        kind == Microsoft.Win32.RegistryValueKind.DWord && value is int number ? number : null;

    /// <summary>
    /// The one warning line for the log when the machine switch holds values but is ignored because non-administrators can write the
    /// key; null when there is nothing to warn about. Names only, never the values (they came from someone who should not set them).
    /// </summary>
    public static string? UnsafeSwitchWarning(MachineDevSwitch machine)
    {
        if (machine.AdminOnly) return null;
        var names = new List<string>();
        if (machine.DeveloperMode is not null) names.Add(DeveloperModeValue);
        if (machine.RemoteDebuggingPort is not null) names.Add(RemoteDebuggingPortValue);
        return names.Count == 0
            ? null
            : $"[dev] warning: HKLM\\{MachineKey} is writable by non-administrators, ignoring {string.Join(" and ", names)}";
    }

    /// <summary>
    /// --dev and remote debugging. A release build ignores --dev unless an administrator set DeveloperMode=1 in HKLM (a standard user
    /// can put --dev into a shortcut, a Run entry or the shell value of the account). Remote debugging needs the same administrator
    /// switch in every build and a port of 1024–65535; the environment variable route is gone (see <see cref="WebViewEnvironment"/>).
    /// </summary>
    public static DevDecision Decide(bool devArgument, bool debugBuild, MachineDevSwitch machine)
    {
        var machineOn = machine.AdminOnly && machine.DeveloperMode == 1;
        int? port = machineOn && machine.RemoteDebuggingPort is >= 1024 and <= 65535 ? machine.RemoteDebuggingPort : null;
        if (!devArgument) return new DevDecision(false, port, "no --dev");
        if (debugBuild) return new DevDecision(true, port, "debug build");
        if (machineOn) return new DevDecision(true, port, "HKLM DeveloperMode=1");
        var why = machine.DeveloperMode == 1 && !machine.AdminOnly
            ? "--dev ignored: HKLM\\SOFTWARE\\CouchHud is writable by non-administrators"
            : "--dev ignored: release build without HKLM DeveloperMode=1";
        return new DevDecision(false, port, why);
    }
}

/// <summary>Which WebView2 asks for its extra browser arguments.</summary>
public enum BrowserSurface
{
    Hud,
    BrowserPane
}

public static class BrowserArguments
{
    public const string HudBase = "--autoplay-policy=no-user-gesture-required";

    /// <summary>The couch browser's own fixed arguments: no picture-in-picture (see <see cref="Guard.BrowserGuard.BrowserArguments"/>). Nothing from the user goes in here.</summary>
    public const string PaneBase = Guard.BrowserGuard.BrowserArguments;

    /// <summary>
    /// The arguments CouchHUD adds for developers: --remote-debugging-port only (Chromium binds it to 127.0.0.1; never with
    /// --remote-debugging-address). HUD and browser pane are separate browser processes, so the pane takes the next port.
    /// </summary>
    public static IReadOnlyList<string> Extra(int? remoteDebuggingPort, BrowserSurface surface)
    {
        if (remoteDebuggingPort is not { } port || port is < 1024 or > 65535) return [];
        var actual = surface == BrowserSurface.BrowserPane ? port + 1 : port;
        return actual > 65535 ? [] : [$"--remote-debugging-port={actual}"];
    }

    /// <summary>The argument line for a WebView2 environment: the surface's own arguments plus <see cref="Extra"/>.</summary>
    public static string Build(string? baseArguments, int? remoteDebuggingPort, BrowserSurface surface)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(baseArguments)) parts.Add(baseArguments.Trim());
        parts.AddRange(Extra(remoteDebuggingPort, surface));
        var line = string.Join(' ', parts);
        if (line.Contains("--remote-debugging-address", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("remote debugging is loopback only", nameof(baseArguments));
        return line;
    }

    /// <summary>
    /// Whether the browser process is the WebView2 runtime (<see cref="Guard.LockedForeground.IsWebViewRuntimePath"/>) below a folder only
    /// administrators can change (pass Program Files, 64 and 32 bit; not all of %WINDIR%). A per-user runtime (%LOCALAPPDATA%) is
    /// legitimate but writable by every program of the user; the shell warns rather than refusing to start.
    /// </summary>
    public static bool InProtectedFolder(string? browserExe, IEnumerable<string?> protectedRoots)
    {
        if (string.IsNullOrWhiteSpace(browserExe) || !Guard.LockedForeground.IsWebViewRuntimePath(browserExe)) return false;
        string full;
        try
        {
            full = Path.GetFullPath(browserExe);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        return protectedRoots.Any(root => !string.IsNullOrWhiteSpace(root)
            && full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }
}
