namespace CouchHud.Core.Guard;

/// <summary>A window that just came to the front. The expensive questions are lazy.</summary>
/// <param name="OwnPid">The window belongs to the CouchHud process itself.</param>
/// <param name="FileOrPrintDialog">A standard file, folder or print dialog (<see cref="LockedForeground.IsFileOrPrintDialog"/>).</param>
/// <param name="Unreadable">The process is gone or Windows won't tell us about it (protected, elevated): never touched.</param>
/// <param name="WindowClass">Window class, for shells that are told by their class (file manager, console).</param>
/// <param name="OwnTree">The process descends from CouchHud. Counts for the WebView2 helpers only (<see cref="LockedForeground.IsOwnWebViewHelper"/>): everything else CouchHud starts is a client, an app or a game with its own rules.</param>
/// <param name="GameTree">The process belongs to the running game, descends from it or was started by CouchHud.</param>
/// <param name="ClientTree">Start phase: the process descends from a client family that was released for this launch (a game Steam or Epic starts).</param>
/// <param name="Fullscreen">The window covers its whole monitor.</param>
public sealed record LockedWindow(string? Process, string? Title, bool OwnPid, bool FileOrPrintDialog, Func<bool> OwnTree, Func<bool> GameTree, Func<bool> Fullscreen)
{
    public bool Unreadable { get; init; }

    public string? WindowClass { get; init; }

    public Func<bool> ClientTree { get; init; } = () => false;

    /// <summary>
    /// A process named msedgewebview2 that really is the WebView2 runtime (its program is in the runtime's folder) or that CouchHud
    /// started itself (its parent is CouchHud). The name alone is not enough: any program can carry it.
    /// </summary>
    public Func<bool> WebViewGenuine { get; init; } = () => true;

    /// <summary>The process was started by CouchHud as an app (or descends from such a process).</summary>
    public Func<bool> AppTree { get; init; } = () => false;

    /// <summary>
    /// The window belongs to a program that one of CouchHUD's own plugins restricts on the locked couch (lock.restrict, and CaseHUD
    /// itself), told by the canonical path of its program image, never by the process name. Such a window is a settings tool, never a game.
    /// </summary>
    public Func<bool> PluginRestricted { get; init; } = () => false;
}

/// <summary>
/// The locked couch only has room for CouchHud, the running game, the Windows sign-in and the accessibility tools, and
/// it takes away what is clearly not a game: shells, tools, settings, the file manager, browsers outside the game and
/// client windows outside their start phase. A window that could be a game (unknown program, game of a launcher the
/// tracker does not know) is never minimized. Pure and defensive: it never ends a process, the Windows side only
/// minimizes windows and asks dialogs to close.
/// </summary>
public static class LockedForeground
{
    /// <summary>Sign-in, UAC, the accessibility tools and the small system flyouts: they must work on a locked couch.</summary>
    private static readonly string[] SystemProcesses =
    [
        "LockApp", "LogonUI", "consent", "CredentialUIBroker", "winlogon", "SmartScreen", "wlrmdr",
        "osk", "TabTip", "TabTip32", "TextInputHost", "ctfmon", "Magnify", "Narrator", "utilman", "AtBroker",
        "ShellHost", "SndVol", "SecurityHealthSystray", "WerFault", "WerFaultSecure"
    ];

    /// <summary>
    /// The console programs of CouchHud's own helper tasks (policies, services; see PolicyGroups). Where Windows cannot run them
    /// without a window they show a console for a moment, which is not the guest's doing and must not be fought.
    /// </summary>
    private static readonly string[] OwnHelpers = ["reg", "sc"];

    /// <summary>Shells, tools and settings: clearly not a game, so their windows are minimized even when full-screen.</summary>
    private static readonly string[] NotGames =
    [
        "explorer", "ApplicationFrameHost", "SearchHost", "ShellExperienceHost", "StartMenuExperienceHost", "SystemSettings",
        "cmd", "conhost", "powershell", "powershell_ise", "pwsh", "WindowsTerminal", "OpenConsole", "wt", "regedit", "mmc", "taskmgr", "control",
        "notepad", "wordpad", "write", "mspaint", "calc", "SnippingTool", "charmap", "mstsc", "msinfo32", "perfmon", "resmon", "eventvwr",
        "dxdiag", "msconfig", "cleanmgr", "diskmgmt", "devmgmt", "compmgmt", "services", "wscript", "cscript", "rundll32", "msiexec",
        "OneDrive", "Teams", "Outlook", "WINWORD", "EXCEL", "POWERPNT"
    ];

    /// <summary>Browsers: not a game unless the game started them (then they are in the game's process tree).</summary>
    private static readonly string[] Browsers =
        ["chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi", "iexplore", "MicrosoftEdge", "waterfox", "librewolf", "chromium"];

    /// <summary>Windows told by their class: the file manager and the consoles.</summary>
    private static readonly string[] NotGameClasses =
        ["CabinetWClass", "ExploreWClass", "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "Shell_TrayWnd", "Progman", "WorkerW"];

    private static readonly string[] PrintTitles = ["Drucken", "Print", "Seite einrichten", "Page Setup", "Druckereinrichtung", "Print Setup"];

    /// <summary>Children that tell a file or folder dialog.</summary>
    private static readonly string[] DialogChildren =
        ["SHELLDLL_DefView", "DUIViewWndClassName", "SHBrowseForFolder ShellNameSpace Control"];

    /// <summary>Children of the common print dialog (whatever language its title is in).</summary>
    private static readonly string[] PrintChildren = ["PrintUIToolbarWindow32", "PrintUIComboBox", "PrintUIPrinterList"];

    public static IReadOnlyList<string> SystemList => SystemProcesses;

    /// <summary>A known web browser (by process or file name without .exe).</summary>
    public static bool IsBrowser(string? processName) => In(Browsers, processName);

    public static IReadOnlyList<string> NotGameList => NotGames;

    /// <summary>The WebView2 helper processes of CouchHud's own windows: the only children of CouchHud that are "own".</summary>
    public static bool IsOwnWebViewHelper(string? processName) => In(["msedgewebview2"], processName);

    /// <summary>Is this the program of the WebView2 runtime (<c>…\EdgeWebView\Application\&lt;version&gt;\msedgewebview2.exe</c>, or the runtime inside Edge)?</summary>
    public static bool IsWebViewRuntimePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = path.Replace('/', '\\');
        if (!normalized.EndsWith(@"\msedgewebview2.exe", StringComparison.OrdinalIgnoreCase)) return false;
        return normalized.Contains(@"\EdgeWebView\Application\", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains(@"\Microsoft\Edge\Application\", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSystem(string? processName) => In(SystemProcesses, processName);

    /// <summary>reg.exe or sc.exe with its console window: a helper task of the lock itself.</summary>
    public static bool IsOwnHelper(string? processName, string? windowClass) => windowClass == "ConsoleWindowClass" && In(OwnHelpers, processName);

    /// <summary>A shell, tool or browser that must not pass as a game just because its window is full-screen.</summary>
    public static bool IsNotGame(string? processName) => In(NotGames, processName) || In(Browsers, processName);

    public static bool IsNotGameClass(string? windowClass) => windowClass is not null && NotGameClasses.Contains(windowClass, StringComparer.Ordinal);

    /// <summary>The standard dialogs of Windows that browse the disk or print: the way out of a game or a client.</summary>
    public static bool IsFileOrPrintDialog(string? windowClass, string? title, IEnumerable<string> childClasses)
    {
        if (windowClass != "#32770") return false;
        var children = childClasses as ICollection<string> ?? childClasses.ToList();
        if (children.Any(c => DialogChildren.Contains(c, StringComparer.Ordinal) || PrintChildren.Contains(c, StringComparer.Ordinal))) return true;
        // The older print dialog is a property sheet: tabs plus a printer list.
        if (children.Contains("SysTabControl32") && children.Contains("SysListView32")) return true;
        return title is not null && PrintTitles.Any(p => string.Equals(p, title.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Explorer windows without a taskbar on a locked couch that runs as the shell are a file manager for the guest.</summary>
    public static bool ShouldEndExplorers(bool locked, bool asShell, bool hasTray) => locked && asShell && !hasTray;

    public static ForegroundDecision Decide(ForegroundContext ctx, LockedWindow w)
    {
        if (!ctx.Locked) return new(ForegroundAction.Allow, ForegroundReason.NotLocked);
        if (ctx.Suspended) return new(ForegroundAction.Allow, ForegroundReason.Suspended);
        if (IsSystem(w.Process) || IsOwnHelper(w.Process, w.WindowClass)) return new(ForegroundAction.Allow, ForegroundReason.LockedSystem);
        // Before the "own" and "game" checks: a file dialog of the browser's WebView2 or of a game is just as much a way out.
        if (w.FileOrPrintDialog && !w.OwnPid) return new(ForegroundAction.CloseDialog, ForegroundReason.LockedDialog);
        if (w.OwnPid) return new(ForegroundAction.Allow, ForegroundReason.OwnWindow);
        // Programs a first-party plugin restricts (CaseHUD: fan curves, lighting, designs) are settings, so not on a locked couch.
        // Before "unreadable": such a program may run elevated, and its image is still readable with limited rights.
        if (w.PluginRestricted()) return new(ForegroundAction.Minimize, ForegroundReason.LockedForeign);
        // Doubt is not a reason to minimize: a process we cannot look at (protected, elevated, already gone) is left alone.
        if (w.Unreadable) return new(ForegroundAction.Allow, ForegroundReason.LockedUnknown);
        // The running game, its helpers and (in the start phase) what CouchHud started for it. A browser the game started is part of it.
        if (w.GameTree()) return new(ForegroundAction.Allow, ForegroundReason.LockedGame);
        // An app the parents started on purpose in an unlocked moment: free until the HUD is back in front.
        if (ctx.AppsReleased && w.AppTree()) return new(ForegroundAction.Allow, ForegroundReason.LockedAppReleased);

        var family = ForegroundPolicy.Classify(w.Process);
        if (family is not null and not "nuisance")
        {
            // A client may show a login or an update while the game starts, until its window is there.
            if (ctx.GameStarting && ctx.Released is { } released && released.Contains(family))
                return new(ForegroundAction.Allow, ForegroundReason.FamilyReleased, family);
            var action = ForegroundPolicy.IsBigPicture(family, w.Title) ? ForegroundAction.CloseAndMinimize : ForegroundAction.Minimize;
            return new(action, ForegroundReason.LockedForeign, family);
        }

        // Start phase: what Steam or Epic starts for this launch is the game (or its launcher, anti-cheat, redistributables).
        if (ctx.GameStarting && w.ClientTree()) return new(ForegroundAction.Allow, ForegroundReason.LockedGame);

        if (family == "nuisance" || IsNotGame(w.Process) || IsNotGameClass(w.WindowClass))
            return new(ForegroundAction.Minimize, ForegroundReason.LockedForeign, family);

        // The WebView2 helpers of CouchHud's own windows, and only those: a client, a browser or an app that CouchHud
        // started descends from it too, but they were all dealt with above.
        if (IsOwnWebViewHelper(w.Process) && w.OwnTree() && w.WebViewGenuine()) return new(ForegroundAction.Allow, ForegroundReason.OwnWindow);

        // Everything else could be a game (one the tracker does not know, another launcher's process): left alone.
        return w.Fullscreen()
            ? new(ForegroundAction.Allow, ForegroundReason.LockedFullscreen)
            : new(ForegroundAction.Allow, ForegroundReason.LockedUnknown);
    }

    private static bool In(string[] list, string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        var name = processName.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return list.Any(p => string.Equals(p, name, StringComparison.OrdinalIgnoreCase));
    }
}
