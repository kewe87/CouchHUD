using CouchHud.Sdk;

namespace CouchHud.Core.Plugins;

/// <summary>A page action as the footer gets it: only the button and a cleaned label, never an id or a target.</summary>
public sealed record PluginActionView(string Button, string Label, string Kind);

/// <summary>An action that passed the host's checks, with the row the press gate (PluginPolicy.MayPress) decides on.</summary>
public sealed record CheckedPageAction(PluginPageAction Action, string Label, PluginRow Row)
{
    public string Button => Action.Button.ToString();

    /// <summary>The page of the same plugin a link action opens, or null for an action handled by the plugin.</summary>
    public string? LinkPage => Action.PageId;

    public PluginActionView View => new(Button, Label, Action.PageId is null ? "action" : "link");
}

/// <summary>
/// The host's checks for the page actions a plugin hands over (footer hints). Third-party data: the button must be one of the free ones, at most
/// <see cref="PluginPageAction.MaxPerPage"/> actions with one per button, the label is cleaned like a page title and short, the target is either
/// a page of the same plugin or an action id (never an address), and a press is decided on a row built from the action, so it passes exactly
/// the gate of a row press.
/// </summary>
public static class PluginPageActions
{
    /// <summary>The row an action stands for in the press gate. The row is built here, never taken from the plugin's own rows.</summary>
    private static PluginRow AsRow(PluginPageAction action, string label) => new($"$action.{action.Button}", label)
    {
        Kind = action.PageId is null ? PluginRowKind.Action : new PluginRowKind.Link(action.PageId),
        RequiresUnlock = true,
        RequiresParentGrant = action.RequiresParentGrant,
        AllowedWhileLocked = action.AllowedWhileLocked,
        Function = action.Function
    };

    /// <summary>
    /// Labels a plugin that is not CouchHUD's own may not use: they name what the core's own footer and menus do (back, power, the parents' code,
    /// the menu, home, settings ...), so an action could pass for one of them. Compared by look (case, separators, look-alike letters), whole label.
    /// </summary>
    public static readonly IReadOnlyList<string> ReservedLabels =
    [
        "Zurück", "Zurueck", "Back", "Ausschalten", "Power off", "Poweroff", "Herunterfahren", "Shut down", "Shutdown", "Neustart", "Neu starten", "Restart", "Reboot",
        "Standby", "Ruhezustand", "Sleep", "Eltern-Code", "Elterncode", "Eltern Code", "Parents' code", "Parent code", "Parental code", "Code", "PIN",
        "Menü", "Menue", "Menu", "Startseite", "Home", "Einstellungen", "Settings", "Optionen", "Options", "Sperren", "Lock", "Entsperren", "Unlock",
        "Beenden", "Exit", "Quit", "Schließen", "Close", "Abbrechen", "Cancel", "Abmelden", "Log out", "Logout", "Sign out", "Benutzer wechseln", "Switch user",
        "Guide", "Power", "Ausschalten und beenden", "Bestätigen", "Confirm", "OK"
    ];

    private static readonly Lazy<HashSet<string>> ReservedLooks = new(() => ReservedLabels.Select(PluginText.Look).ToHashSet(StringComparer.Ordinal));

    /// <summary>
    /// Whether a label is a reserved one. Compared as it is and once more with the signs the skeleton turns into letters ("!", "|", "@", "$") and
    /// the punctuation around the text taken out first, so "Back!" or "|Home|" do not become something else before the comparison. The skeleton
    /// itself stays as it is (other callers use it).
    /// </summary>
    public static bool IsReserved(string label)
    {
        if (ReservedLooks.Value.Contains(PluginText.Look(label))) return true;
        var plain = new string(label.Where(c => c is not ('!' or '|' or '@' or '$')).ToArray()).Trim();
        plain = plain.Trim([.. plain.Where(c => char.IsPunctuation(c) || char.IsSymbol(c)).Distinct()]);
        return plain.Length > 0 && ReservedLooks.Value.Contains(PluginText.Look(plain));
    }

    /// <summary>The actions that pass the checks: the plugin's SDK is at least <see cref="PluginPageAction.MinSdk"/> (older plugins have none), valid button (first one per button wins), exactly one target, a label that is left after cleaning and is no system name, a link target that exists.</summary>
    /// <param name="sdk">The SDK version the plugin declared; below 4 the list is ignored (it could not have been built for it).</param>
    public static IReadOnlyList<CheckedPageAction> Check(IReadOnlyList<PluginPageAction>? actions, bool firstParty, Func<string, bool> pageExists, int sdk)
    {
        var list = new List<CheckedPageAction>();
        if (sdk < PluginPageAction.MinSdk) return list;
        foreach (var action in actions ?? [])
        {
            if (list.Count >= PluginPageAction.MaxPerPage) break;
            if (action is null || !Enum.IsDefined(action.Button) || list.Any(x => x.Action.Button == action.Button)) continue;
            var hasPage = !string.IsNullOrEmpty(action.PageId);
            var hasAction = !string.IsNullOrEmpty(action.ActionId);
            if (hasPage == hasAction) continue;
            if (hasPage ? action.PageId!.Length > PluginRows.MaxId || !pageExists(action.PageId) : action.ActionId!.Length > PluginRows.MaxId) continue;
            if (action.Function is { Length: > PluginRows.MaxId }) continue;
            // Cleaned like a page title: no emoji, symbols or invisible characters, no look-alike of a core screen, short.
            var label = PluginText.Safe(action.Label, "", firstParty, PluginPageAction.MaxLabel);
            if (label.Length == 0) continue;
            // A plugin that is not CouchHUD's own cannot name an action like a system function (a pad hint "Back" or "Parents' code" that is not).
            if (!firstParty && IsReserved(label)) continue;
            list.Add(new CheckedPageAction(action, label, AsRow(action, label)));
        }
        return list;
    }

    /// <summary>The action on a button, from the checked list; null when there is none (the page's action list is read fresh on every press).</summary>
    public static CheckedPageAction? Find(IReadOnlyList<CheckedPageAction> actions, string? button) =>
        Enum.TryParse<PluginPageButton>(button, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed) && parsed.ToString() == button
            ? actions.FirstOrDefault(a => a.Action.Button == parsed)
            : null;
}
