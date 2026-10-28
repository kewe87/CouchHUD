using CouchHud.Core.Plugins;

namespace CouchHud.Core.Security;

/// <summary>Which code the service checks when the page asks for the parents' code.</summary>
public enum ParentCheckKind
{
    /// <summary>The parents' code (the default, and the only one that may change protection).</summary>
    Parent,
    /// <summary>The couch code, acting as the parents' code (parental controls off, SECURITY.md).</summary>
    Couch
}

/// <summary>
/// Without parental controls the couch code is the parents' code (SECURITY.md), but only for a closed list of the HUD's own actions and only
/// while it is certain that nobody holds a parents' code. Everything else stays the parents' code: fail-closed. The action comes from the
/// page, so null, unknown and misspelled actions are never on the list.
/// </summary>
public static class ParentCodeKind
{
    /// <summary>
    /// Actions of the HUD itself whose grant carries no service operation and changes no protection (no code, lock mode, auto sign-in or
    /// parental switch): plugin changes, catalog sources, and the rest of the actions the page asks a grant for. A new action is added here on purpose.
    /// </summary>
    public static readonly IReadOnlySet<string> HudOnlyActions = new HashSet<string>(StringComparer.Ordinal)
    {
        "catalogSourceAdd", "catalogSourceRemove",
        "profileSwitch", "profileManage",
        "extend", "launch", "lock", "saveSettings", "storeMatch", "startOptions", "editType", "addGame", "installGame", "openClientDownloads",
        // pluginFunction: who may use a plugin function. Without parental controls there are no child profiles and the couch code is the highest
        // protection there is; with parental controls on, or a parents' code held by the service, it stays the parents' code (For below).
        "browserRecent", "tvInput", "pluginFunction"
    }.Concat(Enum.GetNames<PluginChange>().Select(n => "plugin" + n)).ToHashSet(StringComparer.Ordinal);

    /// <param name="parentalEnabled">Read from config.json, which the account can write: never enough on its own.</param>
    /// <param name="action">The action the page named (null when it named none).</param>
    /// <param name="serviceParentKnownAbsent">The service's own state is known and says it holds no parents' code; unknown or a code present is false.</param>
    public static ParentCheckKind For(bool parentalEnabled, string? action, bool serviceParentKnownAbsent) =>
        !parentalEnabled && serviceParentKnownAbsent && action is not null && HudOnlyActions.Contains(action)
            ? ParentCheckKind.Couch
            : ParentCheckKind.Parent;
}
