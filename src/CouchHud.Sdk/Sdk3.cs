namespace CouchHud.Sdk;

// SDK 3: richer declarative pages, host services behind capabilities, and the process id of a started game.
// Everything is added through optional members and default interface members, so plugins for SDK 1 and 2 keep working.
// A plugin never ships HTML or script into the HUD: pages are data that CouchHUD renders (all text as plain text).

/// <summary>
/// The capabilities a plugin can declare in plugin.json ("capabilities": [...]) and in its signed statement. A context member whose
/// capability was not declared (and granted) throws <see cref="PluginCapabilityException"/>. Some are for CouchHUD's own (first-party)
/// plugins only and are never granted to a plugin of a third-party developer, whatever it declares.
/// </summary>
public static class PluginCapabilities
{
    /// <summary>Supplies library entries (<see cref="IPluginContext.RegisterLibraryProvider"/>).</summary>
    public const string Library = "library";
    /// <summary>Pages in the settings (<see cref="IPluginContext.RegisterPage"/>, <see cref="IPluginContext.Invalidate"/>).</summary>
    public const string Pages = "pages";
    /// <summary>Entries in the guide menu (<see cref="IPluginContext.RegisterQuickAction"/>).</summary>
    public const string QuickActions = "quickActions";
    /// <summary>Tools for the AI assistant and MCP clients (<see cref="IPluginContext.RegisterTool"/>).</summary>
    public const string Assistant = "assistant";
    /// <summary>Notifications on the TV (<see cref="IPluginContext.Notify"/>).</summary>
    public const string Notify = "notify";
    /// <summary>Opens links in the couch browser (<see cref="IPluginContext.OpenUrl"/>).</summary>
    public const string Browser = "browser";
    /// <summary>Lines on the downloads page (<see cref="IPluginContext.Downloads"/>).</summary>
    public const string Downloads = "downloads";
    /// <summary>Read-only network state: adapters up or down, the default route (<see cref="IPluginContext.Network"/>).</summary>
    public const string NetworkState = "network.state";
    /// <summary>
    /// Runs the command line of a program installed below Program Files that the user confirmed for this plugin
    /// (<see cref="IPluginContext.RunTrustedCliAsync"/>). Also what the lint expects for code that starts processes itself.
    /// </summary>
    public const string Process = "process";
    /// <summary>Main tabs in the tab bar that show one of the plugin's pages (<see cref="IPluginContext.RegisterTab"/>).</summary>
    public const string Tabs = "tabs";
    /// <summary>First-party only: hands a downloaded setup to Windows (UAC asks) after the parents' code (<see cref="IPluginContext.RunSetupAsync"/>).</summary>
    public const string SetupRun = "setup.run";
    /// <summary>First-party only: a settings program that is minimized on the locked couch (<see cref="IPluginContext.RestrictWhileLocked"/>).</summary>
    public const string LockRestrict = "lock.restrict";
    /// <summary>First-party only: adapts the arguments of one program's autostart entry when CouchHUD is the shell (<see cref="IPluginContext.AdaptStartupArgs"/>).</summary>
    public const string StartupArgs = "startup.args";
    /// <summary>First-party only: a web sign-in in the couch browser whose redirect address the host catches without loading it (<see cref="IPluginContext.SignIn"/>).</summary>
    public const string BrowserSignIn = "browser.signin";

    // What the lint finds in the code (System.Reflection.Metadata, the plugin never runs for it). Declaring them grants no host member.
    /// <summary>P/Invoke, native libraries, native files in the package.</summary>
    public const string Native = "native";
    /// <summary>The Windows registry.</summary>
    public const string Registry = "registry";
    /// <summary>Sockets or HTTP to this PC only.</summary>
    public const string NetworkLoopback = "network:loopback";
    /// <summary>Sockets or HTTP to the internet or the home network.</summary>
    public const string NetworkInternet = "network:internet";
    /// <summary>Loading or generating code at run time (Assembly.Load*, Reflection.Emit). Never for third-party plugins.</summary>
    public const string DynamicCode = "dynamicCode";

    /// <summary>Every capability CouchHUD knows; anything else in a manifest or statement is refused.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Library, Pages, QuickActions, Assistant, Notify, Browser, Downloads, NetworkState, Process, Tabs, SetupRun, LockRestrict, StartupArgs, BrowserSignIn,
        Native, Registry, NetworkLoopback, NetworkInternet, DynamicCode
    ];
}

/// <summary>
/// A main tab a plugin asks for (capability "tabs"): a title in German and English, an icon, and the plugin page it shows. The page is
/// drawn by CouchHUD from the plugin's data like any plugin page; a plugin never ships HTML or script. The host bounds and checks
/// everything (id, title length and characters, names that look like a built-in tab, the picture), puts the tab after the built-in
/// tabs and hides it with its area, in a child's profile, and while the plugin is off or not loaded.
/// </summary>
/// <param name="Id">Lower case letters, digits, "-" and "_", at most 32 characters; unique within the plugin.</param>
/// <param name="PageId">The <see cref="PluginPage.Id"/> the tab shows; the page must be registered by the same plugin.</param>
/// <param name="TitleDe">The tab's title in German (at most 14 characters; letters of the Latin alphabet with German umlauts, digits, a few signs); empty: the English one is used.</param>
/// <param name="TitleEn">The tab's title in English (same rules); empty: the German one is used.</param>
public sealed record PluginTab(string Id, string PageId, string TitleDe, string TitleEn)
{
    /// <summary>
    /// A symbol (exactly one emoji, arrow or shape, no letters or digits) shown before the title when there is no <see cref="Icon"/>. For a
    /// plugin that is not CouchHUD's own, a symbol that CouchHUD draws as one of its own icons (lock, gear, bell …) is replaced by a plain dot.
    /// </summary>
    public string? Glyph { get; init; }

    /// <summary>A small PNG (at most <see cref="MaxIconBytes"/> and 256 x 256 pixels), shown before the title. Anything else is dropped by the host.</summary>
    public byte[]? Icon { get; init; }

    public const int MaxIconBytes = 64 * 1024;

    /// <summary>
    /// The feature area the tab belongs to: "games", "apps" (the default), "browser", "news" or "always". With that area switched off the
    /// tab is hidden. An unknown value counts as "apps". "always" (never hidden by an area) is for CouchHUD's own plugins only; for any
    /// other plugin it counts as "apps".
    /// </summary>
    public string Area { get; init; } = "apps";

    /// <summary>
    /// Show the tab in a child's profile too. Honoured only for CouchHUD's own plugins (a feature of the core that became a plugin and was
    /// visible to children before); ignored for every other plugin, whose tabs are never shown to children. The page behind the tab stays
    /// behind the usual gates (lock, parents' code).
    /// </summary>
    public bool ChildSafe { get; init; }

    /// <summary>The <see cref="PluginFunction"/> the tab belongs to; the owner's level for it decides who sees the tab (see <see cref="PluginFunction"/>). Null: untagged.</summary>
    public string? Function { get; init; }
}

/// <summary>Who may use a plugin function. The owner chooses it per function in the plugin's settings; the host enforces it.</summary>
public enum PluginLevel
{
    /// <summary>Only when the couch is unlocked (and, under parental controls, with the parents' code as for any change).</summary>
    Admin,

    /// <summary>Everyone, also on the locked couch and in a child's profile. Only the owner can choose this for a plugin that is not CouchHUD's own.</summary>
    Restricted,

    /// <summary>Nobody: hidden, and calls are refused.</summary>
    Off
}

/// <summary>
/// A named function of a plugin that the owner can allow or forbid separately ("lighting", "setup"). The plugin declares its functions with
/// <see cref="IPluginContext.RegisterFunctions"/> and tags its pages, rows, guide entries, tabs and tools with the id (their <c>Function</c>
/// property). Items without a tag behave as before. The host checks the level on every show and every call; the plugin is never asked.
/// </summary>
/// <param name="Id">Lower case letters, digits, "-" and "_", at most 32 characters; unique within the plugin.</param>
/// <param name="LabelDe">The name in German (shown in the plugin's settings).</param>
/// <param name="LabelEn">The name in English.</param>
public sealed record PluginFunction(string Id, string LabelDe, string LabelEn)
{
    /// <summary>
    /// The level the function starts with. Only a suggestion: the owner's choice wins, and for a plugin that is not CouchHUD's own
    /// <see cref="PluginLevel.Restricted"/> and <see cref="PluginLevel.Off"/> count as <see cref="PluginLevel.Admin"/> (such a plugin cannot loosen
    /// anything for itself). Default: <see cref="PluginLevel.Admin"/>.
    /// </summary>
    public PluginLevel Suggested { get; init; } = PluginLevel.Admin;
}

/// <summary>A context member was used without its capability (see <see cref="PluginCapabilities"/>).</summary>
public sealed class PluginCapabilityException(string message) : InvalidOperationException(message);

/// <summary>How a section of a page is drawn.</summary>
public enum PluginSectionKind
{
    List,
    Tiles
}

/// <summary>A titled group of rows on a page; rows name their section with <see cref="PluginRow.Section"/>.</summary>
public sealed record PluginSection(string Id, string? Title, PluginSectionKind Kind = PluginSectionKind.List);

/// <summary>What pressing a row does. Null on a row (the SDK 1 default) is <see cref="Action"/>.</summary>
public abstract record PluginRowKind
{
    private PluginRowKind() { }

    /// <summary>A plain press (<see cref="PluginPage.Activate"/>).</summary>
    public static PluginRowKind Action { get; } = new ActionKind();

    /// <summary>On/off (<see cref="PluginRow.On"/>); a press flips it (<see cref="PluginPage.Activate"/>).</summary>
    public static PluginRowKind Toggle { get; } = new ToggleKind();

    public sealed record ActionKind : PluginRowKind;

    public sealed record ToggleKind : PluginRowKind;

    /// <summary>A number from <paramref name="Min"/> to <paramref name="Max"/>; the host waits <paramref name="SettleMs"/> after the last step before it calls <see cref="PluginPage.Change"/>.</summary>
    public sealed record Stepper(double Min, double Max, double Step, int SettleMs = 400) : PluginRowKind;

    /// <summary>One of a few options, picked in a dialog (<see cref="PluginPage.Change"/> gets the option's id).</summary>
    public sealed record Choice(IReadOnlyList<PluginChoiceOption> Options) : PluginRowKind;

    /// <summary>Opens another page of the same plugin. Changes nothing, so it never needs the unlock.</summary>
    public sealed record Link(string PageId) : PluginRowKind;
}

public sealed record PluginChoiceOption(string Id, string Label);

/// <summary>Lines of the downloads page that belong to the plugin. Ids are the plugin's own; the host keeps them apart from other plugins.</summary>
public interface IPluginDownloads
{
    void Begin(string id, string label, bool isUpdate = false);

    void Report(string id, double? progress = null, string? detail = null, long? bytesDone = null, long? bytesTotal = null);

    void Complete(string id, bool ok, string? detail = null);
}

/// <summary>
/// A web sign-in (OAuth) in the couch browser, for CouchHUD's own plugins. The plugin names a redirect host that ends in ".invalid"
/// (RFC 6761: it never resolves). The browser cancels every navigation to that host before a request goes out (frames too, other
/// requests to it get a 403 in the browser), so the address, which carries the code, never reaches a server, the address bar or the
/// history; the host hands it to the plugin once. One sign-in at a time; it ends with the browser, with the lock, or on
/// <see cref="Cancel"/>. Opening needs the unlocked couch and is not a person's use of the browser.
/// </summary>
public interface IPluginSignIn
{
    /// <summary>
    /// Opens <paramref name="pageUrl"/> (http or https, with exactly one <c>state</c> parameter) in the couch browser. Only a redirect that
    /// carries the same state is handed over; a stray one is ignored and the page stays open. <paramref name="onRedirect"/> gets the redirect address
    /// (https://&lt;redirectHost&gt;/..., never logged) and the address of the page it came from, on a worker thread. False when refused
    /// (locked, bad addresses, another sign-in is running).
    /// </summary>
    bool Open(string pageUrl, string redirectHost, Action<string, string> onRedirect);

    /// <summary>Ends the sign-in of this plugin: a late redirect is not handed over.</summary>
    void Cancel();
}

/// <summary>One network adapter as the host sees it (read-only).</summary>
public sealed record PluginNetworkAdapter(string Id, string Name, string Description, bool Up, bool Tunnel, bool HasDefaultRoute);

/// <summary>Read-only network state, so a plugin needs neither P/Invoke nor its own polling of Windows.</summary>
public interface IPluginNetwork
{
    IReadOnlyList<PluginNetworkAdapter> Adapters();

    /// <summary>The adapter that carries the default route (lowest metric among the adapters that are up), or null.</summary>
    PluginNetworkAdapter? DefaultRoute() => Adapters().FirstOrDefault(a => a.HasDefaultRoute);
}

/// <summary>The result of <see cref="IPluginContext.RunTrustedCliAsync"/>. <see cref="Refused"/> says why nothing ran.</summary>
public sealed record PluginCliResult(int? ExitCode, string Stderr, bool TimedOut, string? Refused = null)
{
    public bool Ran => Refused is null;
}
