using CouchHud.Core.Localization;
using CouchHud.Sdk;

namespace CouchHud.Core.Plugins;

/// <summary>Where a plugin comes from, and so what it may do.</summary>
public enum PluginOrigin
{
    /// <summary>Dropped into the folder or approved by hand, an unsigned package, or from before SDK 3: the SDK 2 surface at most.</summary>
    Local,

    /// <summary>A package signed by a third-party developer ("Fremdentwickler") whose key is pinned for this plugin id.</summary>
    ThirdParty,

    /// <summary>A package whose statement verifies against CouchHUD's own key. (Nothing is shipped next to the exe any more: since 0.6 every
    /// plugin, CouchHUD's own included, comes from the signed catalog.)</summary>
    FirstParty
}

/// <summary>
/// What a plugin may use, as pure decisions: the capabilities a plugin gets for its origin, the gates for installing and enabling, and
/// the lock rules for page rows and quick actions. The host (PluginManager, Hud, the bridge) only applies these.
/// </summary>
public static class PluginPolicy
{
    /// <summary>CouchHUD's own plugins (their statement is signed with its key).</summary>
    public static bool IsOwn(PluginOrigin origin) => origin is PluginOrigin.FirstParty;

    /// <summary>What plugins for SDK 1 and 2 (no capabilities in plugin.json) used to have: the SDK 2 surface, no host services.</summary>
    public static readonly IReadOnlySet<string> Legacy = new HashSet<string>(StringComparer.Ordinal)
    {
        PluginCapabilities.Library, PluginCapabilities.Pages, PluginCapabilities.QuickActions, PluginCapabilities.Assistant,
        PluginCapabilities.Notify, PluginCapabilities.Browser
    };

    /// <summary>Never for a plugin that is not CouchHUD's own, whatever it declares.</summary>
    public static readonly IReadOnlySet<string> FirstPartyOnly = new HashSet<string>(StringComparer.Ordinal)
    {
        PluginCapabilities.SetupRun, PluginCapabilities.LockRestrict, PluginCapabilities.StartupArgs, PluginCapabilities.BrowserSignIn
    };

    /// <summary>
    /// Every member of <see cref="IPluginContext"/> and the capability it needs (null: always there). The context checks this table on
    /// every call, and a test makes sure no member is missing from it (a new member cannot slip in without a gate).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string?> ContextMembers = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["DataDirectory"] = null,
        ["Log"] = null,
        ["Language"] = null,
        ["Locked"] = null,
        ["Settings"] = null,
        ["Events"] = null,
        ["RegisterSettings"] = null,
        ["RegisterLibraryProvider"] = PluginCapabilities.Library,
        ["RegisterPage"] = PluginCapabilities.Pages,
        ["Invalidate"] = PluginCapabilities.Pages,
        ["RegisterQuickAction"] = PluginCapabilities.QuickActions,
        ["RegisterTool"] = PluginCapabilities.Assistant,
        ["Notify"] = PluginCapabilities.Notify,
        ["OpenUrl"] = PluginCapabilities.Browser,
        ["Downloads"] = PluginCapabilities.Downloads,
        ["Network"] = PluginCapabilities.NetworkState,
        ["RegisterTab"] = PluginCapabilities.Tabs,
        ["RunTrustedCliAsync"]= PluginCapabilities.Process,
        ["RunSetupAsync"] = PluginCapabilities.SetupRun,
        ["RestrictWhileLocked"] = PluginCapabilities.LockRestrict,
        ["AdaptStartupArgs"] = PluginCapabilities.StartupArgs,
        ["RemoveQuickAction"] = PluginCapabilities.QuickActions,
        ["SignIn"] = PluginCapabilities.BrowserSignIn,
        // Declaring functions grants nothing: it only gives the owner something to restrict.
        ["RegisterFunctions"] = null,
    };

    /// <summary>
    /// A capability list from a manifest or statement that may be installed for this origin. Null when it is fine, else the reason
    /// (a text key): an unknown capability, a first-party one for someone else, or run-time code for a third-party developer.
    /// </summary>
    public static string? CheckDeclared(PluginOrigin origin, IEnumerable<string>? declared)
    {
        foreach (var cap in declared ?? [])
        {
            if (!PluginCapabilities.All.Contains(cap)) return "plugin.cap.unknown";
            if (FirstPartyOnly.Contains(cap) && !IsOwn(origin)) return "plugin.cap.firstPartyOnly";
            if (cap == PluginCapabilities.DynamicCode && origin == PluginOrigin.ThirdParty) return "plugin.cap.dynamicCode";
        }
        return null;
    }

    /// <summary>
    /// <see cref="CheckDeclared"/> as a finished text that names the capabilities at fault (all of the refused kind, at most eight), or null when the list is fine.
    /// </summary>
    public static string? DeclaredMessage(PluginOrigin origin, IEnumerable<string>? declared)
    {
        var list = (declared ?? []).ToList();
        if (CheckDeclared(origin, list) is not { } key) return null;
        var names = list.Where(c => CheckDeclared(origin, [c]) == key).Distinct(StringComparer.Ordinal).Take(8).Select(c => new string(c.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-' or ':').Take(64).ToArray()));
        return Strings.Get(key) + ": " + string.Join(", ", names);
    }

    /// <summary>
    /// The capabilities a plugin really gets: what it declared (or the SDK 2 surface when it is older and declared nothing), never a
    /// first-party one for someone else, never anything unknown. <paramref name="recorded"/> are the ones it was installed with (trust
    /// store); a plugin never gets more than those, even if its plugin.json says more now.
    /// </summary>
    public static IReadOnlySet<string> Granted(PluginOrigin origin, IReadOnlyCollection<string>? declared, int sdk, IReadOnlyCollection<string>? recorded = null)
    {
        IEnumerable<string> wanted = declared is null ? sdk < 3 ? Legacy : [] : declared;
        var set = new HashSet<string>(wanted.Where(c => PluginCapabilities.All.Contains(c)), StringComparer.Ordinal);
        if (!IsOwn(origin)) set.ExceptWith(FirstPartyOnly);
        if (origin == PluginOrigin.ThirdParty) set.Remove(PluginCapabilities.DynamicCode);
        // A plugin from before the capabilities (recorded null) keeps at most the SDK 2 surface: the migration grants nothing new.
        set.IntersectWith(recorded ?? Legacy);
        return set;
    }

    // -----------------------------------------------------------------------
    // Gates for installing, updating, enabling, approving

    /// <param name="Caller">Who asks. Only the HUD's own page may change plugins; never the phone remote, MCP, the assistant or a plugin.</param>
    /// <param name="ChildProfile">A child's profile is active.</param>
    /// <param name="DeveloperSwitch">The developer switch for local packages is on.</param>
    public sealed record GateContext(PluginCaller Caller, bool Locked, bool ChildProfile, bool ExpertMode, bool DeveloperSwitch, bool ParentalEnabled);

    /// <param name="Refused">A text key why not; null when it may go ahead.</param>
    /// <param name="ParentCode">Needs a fresh parents' code (the couch code when there are no parental controls) for exactly this plugin.</param>
    public sealed record GateDecision(string? Refused, bool ParentCode, string ParentAction)
    {
        public bool Allowed => Refused is null;
    }

    /// <summary>
    /// Installing or updating a skin (catalog, package): never on the locked couch and never in a child's profile (the same gate as for plugins,
    /// without the code). Returns the text key of the refusal, null when it may go ahead. An uninstall goes through <see cref="Decide"/>.
    /// </summary>
    public static string? SkinChangeRefusal(GateContext ctx)
    {
        if (ctx.Caller != PluginCaller.Hud) return "plugin.gate.hudOnly";
        if (ctx.Locked) return "lock.unlockFirst";
        return ctx.ChildProfile ? "plugin.gate.child" : null;
    }

    /// <param name="addsCapabilities">An update that asks for capabilities the installed version did not have: treated like a new install.</param>
    public static GateDecision Decide(GateContext ctx, PluginChange change, PluginOrigin origin, IReadOnlyCollection<string>? capabilities, bool addsCapabilities = false)
    {
        var action = "plugin" + change;
        GateDecision No(string key) => new(key, false, action);
        if (ctx.Caller != PluginCaller.Hud) return No("plugin.gate.hudOnly");
        if (ctx.Locked) return No("lock.unlockFirst");
        if (ctx.ChildProfile) return No("plugin.gate.child");
        var caps = capabilities ?? [];
        if (change == PluginChange.Update && addsCapabilities) change = PluginChange.Install;
        bool code;
        switch (origin)
        {
            case PluginOrigin.ThirdParty:
                if (change == PluginChange.Install && !ctx.ExpertMode) return No("plugin.gate.expert");
                code = change is PluginChange.Install or PluginChange.Approve or PluginChange.AcceptRevoked or PluginChange.ConfirmExe || ctx.ParentalEnabled;
                break;
            case PluginOrigin.Local:
                // Approving a folder somebody dropped in is an unsigned install: the same switches and the same code.
                if (change is PluginChange.Install or PluginChange.Approve && !(ctx.DeveloperSwitch && ctx.ExpertMode)) return No("plugin.gate.unsigned");
                code = change is PluginChange.Install or PluginChange.Approve or PluginChange.AcceptRevoked or PluginChange.ConfirmExe || ctx.ParentalEnabled;
                break;
            default:
                code = change is PluginChange.AcceptRevoked or PluginChange.ConfirmExe
                       || (ctx.ParentalEnabled && (change is PluginChange.Install or PluginChange.Update)
                          && (caps.Contains(PluginCapabilities.Assistant) || caps.Contains(PluginCapabilities.SetupRun) || caps.Contains(PluginCapabilities.Process)));
                break;
        }
        return new GateDecision(null, code, action);
    }

    // -----------------------------------------------------------------------
    // Rows and quick actions

    /// <summary>A page on the locked couch: only one the plugin marked for it.</summary>
    public static bool MayView(bool locked, PluginPage page, PluginLevel? level = null, bool childProfile = false) =>
        level is { } lv ? PluginFunctionRules.Access(lv, locked, childProfile).Allowed : !locked || page.AllowedWhileLocked;

    /// <summary>Opening the page needs the parents' code: an admin function in a child's profile (one use; the host asks for it, never the plugin).</summary>
    public static bool ViewNeedsParentCode(bool locked, PluginLevel? level, bool childProfile) =>
        level is { } lv && PluginFunctionRules.Access(lv, locked, childProfile) is { Refused: null, ParentCode: true };

    /// <summary>The rows shown: on the locked couch only those marked for it.</summary>
    public static IReadOnlyList<PluginRow> Visible(bool locked, IReadOnlyList<PluginRow> rows) => locked ? rows.Where(r => r.AllowedWhileLocked).ToList() : rows;

    /// <summary>
    /// The rows shown when functions are in play: a row tagged with a function (its own or its page's; <paramref name="levelOf"/> answers) shows
    /// by that function's level; an untagged one by the old rule.
    /// </summary>
    public static IReadOnlyList<PluginRow> Visible(bool locked, bool childProfile, IReadOnlyList<PluginRow> rows, Func<PluginRow, PluginLevel?> levelOf) =>
        rows.Where(r => levelOf(r) is { } lv ? PluginFunctionRules.Access(lv, locked, childProfile).Allowed : !locked || r.AllowedWhileLocked).ToList();

    public static bool ChangesState(PluginRow row) => row.Kind is not PluginRowKind.Link;

    /// <summary>
    /// May this row be pressed (or changed)? On the locked couch only a row of CouchHUD's own plugins marked for it. Needs the parents'
    /// code under parental controls when the plugin asked for it, and always (under parental controls) for a plugin that is not
    /// CouchHUD's own and a row that changes something. A row that is not on the page (null) is refused.
    /// </summary>
    public static GateDecision MayPress(bool locked, PluginOrigin origin, PluginRow? row, bool parentalEnabled, string pluginId, PluginLevel? level = null, bool childProfile = false)
    {
        var action = "pluginRow";
        if (row is null) return new("plugin.pageNotFound", false, action);
        // A row of a function: the owner's level decides (the plugin's flags only matter for untagged rows). Restricted needs no code on purpose
        // (the owner chose that everyone may use it); admin keeps the code rule of the row.
        if (level is { } lv)
        {
            var access = PluginFunctionRules.Access(lv, locked, childProfile);
            if (access.Refused is { } why) return new(why, false, action);
            // Admin: the row's own code rule under parental controls, and always the parents' code in a child's profile (a link changes nothing).
            return new(null, lv == PluginLevel.Admin && ChangesState(row) && (access.ParentCode || (parentalEnabled && (row.RequiresParentGrant || !IsOwn(origin)))), action);
        }
        // A link only opens another page of the same plugin: the host does that itself (PluginRows.LinkTarget), never the plugin's code.
        if (!ChangesState(row)) return locked && !(IsOwn(origin) && row.AllowedWhileLocked) ? new("lock.unlockFirst", false, action) : new(null, false, action);
        if (locked && !(IsOwn(origin) && row.AllowedWhileLocked)) return new("lock.unlockFirst", false, action);
        var code = parentalEnabled && (row.RequiresParentGrant || !IsOwn(origin));
        return new(null, code, action);
    }

    /// <summary>
    /// A quick action: on the locked couch only one of CouchHUD's own plugins that is marked for it (a flag of any other plugin is ignored);
    /// the parents' code as for rows.
    /// </summary>
    public static GateDecision MayRun(bool locked, PluginOrigin origin, QuickAction action, bool parentalEnabled, PluginLevel? level = null, bool childProfile = false)
    {
        if (level is { } lv)
        {
            var access = PluginFunctionRules.Access(lv, locked, childProfile);
            if (access.Refused is { } why) return new(why, false, "pluginAction");
            return new(null, lv == PluginLevel.Admin && (access.ParentCode || (parentalEnabled && (action.RequiresParentGrant || !IsOwn(origin)))), "pluginAction");
        }
        if (locked && !(IsOwn(origin) && action.AllowedWhileLocked)) return new("lock.unlockFirst", false, "pluginAction");
        return new(null, parentalEnabled && (action.RequiresParentGrant || !IsOwn(origin)), "pluginAction");
    }

    /// <summary>The entries of the guide menu now: all of them unlocked, locked only those of CouchHUD's own plugins that are marked for it.</summary>
    public static bool Shown(bool locked, PluginOrigin origin, QuickAction action, PluginLevel? level = null, bool childProfile = false) =>
        level is { } lv ? PluginFunctionRules.Access(lv, locked, childProfile).Allowed : !locked || (IsOwn(origin) && action.AllowedWhileLocked);

    /// <summary>
    /// Changing the level of a plugin function is a change of protection (restricted loosens it): only from the HUD's own page, never on the locked
    /// couch or in a child's profile, and with the parents' code (the plain unlock only while no code exists). The action is on
    /// <see cref="CouchHud.Core.Security.ParentCodeKind.HudOnlyActions"/>: without parental controls the couch code stands in (no child profiles exist then), with them it is the parents' code.
    /// </summary>
    public static GateDecision DecideLevelChange(GateContext ctx)
    {
        const string action = "pluginFunction";
        if (ctx.Caller != PluginCaller.Hud) return new("plugin.gate.hudOnly", false, action);
        if (ctx.Locked) return new("lock.unlockFirst", false, action);
        if (ctx.ChildProfile) return new("plugin.gate.child", false, action);
        return new(null, true, action);
    }

    /// <summary>A plugin tool may run on the locked couch: only one of CouchHUD's own plugins that is marked for it.</summary>
    public static bool ToolAllowedWhileLocked(PluginOrigin origin, bool declared) => IsOwn(origin) && declared;

    /// <summary>A plugin tool for the assistant: a plugin that is not CouchHUD's own is always confirmed on the TV, even a read-only one.</summary>
    public static bool ToolWrites(PluginOrigin origin, bool declaredWrites) => declaredWrites || !IsOwn(origin);
}

public enum PluginCaller
{
    Hud,
    Remote,
    Mcp,
    Assistant,
    Plugin
}

public enum PluginChange
{
    Install,
    Update,
    Enable,
    Disable,
    Uninstall,
    Approve,
    AcceptRevoked,
    ConfirmExe
}
