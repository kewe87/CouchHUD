namespace CouchHud.Sdk;

// SDK 4: page actions. A plugin page can show a few actions as small controller-button hints in the footer, the way CouchHUD's own pages do.
// Actions belong in the footer, not in the middle of the page as rows. A plugin that uses them declares "sdk": 4, so an older CouchHUD does
// not load it (it would not know the members). The host ignores Actions of a plugin that declares less than 4 (nothing is shown, a press is
// refused) and the install check flags them. Nothing else changes: plugins for SDK 1 to 3 run unchanged.

/// <summary>
/// The buttons a page action can take: the two that are free on a plugin page. A, B, LB, RB and Guide are used by the core
/// (press, back, tabs and chips, the guide menu) and the menu button opens the power menu, so a plugin can never take them.
/// </summary>
public enum PluginPageButton
{
    /// <summary>X on an Xbox pad (square on PlayStation).</summary>
    X,

    /// <summary>Y on an Xbox pad (triangle on PlayStation).</summary>
    Y
}

/// <summary>
/// One action of a page, shown as a hint in the footer (button symbol and label) while the page is on screen. Pressing the button, or
/// clicking the hint, goes through exactly the checks of a row press (the lock, the function's level, the parents' code). Exactly one of
/// <see cref="PageId"/> (open another page of the same plugin) and <see cref="ActionId"/> (handled by <see cref="PluginPage.Activate"/>, like a
/// row with that id) is set. The host shows at most three actions per page, one per button, and drops any other.
/// </summary>
/// <param name="Button">Which button.</param>
/// <param name="Label">Plain text, at most <see cref="MaxLabel"/> characters. The host cleans it like a page title (no emoji or symbols, no look-alike spellings of a core screen).</param>
public sealed record PluginPageAction(PluginPageButton Button, string Label)
{
    /// <summary>The SDK version a plugin must declare to use page actions; below it the host ignores <see cref="PluginPage.Actions"/>.</summary>
    public const int MinSdk = 4;

    public const int MaxLabel = 24;
    public const int MaxPerPage = 3;

    /// <summary>The id of another <see cref="PluginPage"/> of the same plugin that the action opens. The host opens it itself, the plugin's code is not called. Changes nothing.</summary>
    public string? PageId { get; init; }

    /// <summary>The id handed to <see cref="PluginPage.Activate"/> when the action is pressed (it counts as a press that changes something).</summary>
    public string? ActionId { get; init; }

    /// <summary>The <see cref="PluginFunction"/> this belongs to; null: the page's function.</summary>
    public string? Function { get; init; }

    /// <summary>A press needs the parents' code under parental controls (for a plugin that is not CouchHUD's own it always does, for an action that changes something).</summary>
    public bool RequiresParentGrant { get; init; }

    /// <summary>Shown and usable on the locked couch. Honoured only for CouchHUD's own plugins; any other plugin's action is hidden while locked.</summary>
    public bool AllowedWhileLocked { get; init; }

    /// <summary>An action that opens another page of the same plugin.</summary>
    public static PluginPageAction Open(PluginPageButton button, string label, string pageId) => new(button, label) { PageId = pageId };

    /// <summary>An action handled by <see cref="PluginPage.Activate"/>.</summary>
    public static PluginPageAction Run(PluginPageButton button, string label, string actionId) => new(button, label) { ActionId = actionId };
}
