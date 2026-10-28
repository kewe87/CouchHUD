namespace CouchHud.Core.Browser;

/// <summary>What a controller button does while the controller steers the couch browser's page.</summary>
public enum BrowserPadAction
{
    /// <summary>Nothing (the left stick as cursor, a button without a meaning on the page).</summary>
    Ignore,
    /// <summary>Guide: out of the browser to the home screen. Always, whatever the page does.</summary>
    LeaveToHome,
    /// <summary>The controller goes to the HUD's bar above the page.</summary>
    ToBar,
    /// <summary>The address field (on-screen keyboard in the HUD).</summary>
    Address,
    /// <summary>Text into the page's focused field (on-screen keyboard in the HUD).</summary>
    TypeText,
    /// <summary>Back in the page's history.</summary>
    GoBack,
    /// <summary>B on the first page: back to where the browser was opened from.</summary>
    Exit,
    ZoomOut,
    ZoomIn,
    ExitFullScreen,
    /// <summary>Left click where the cursor is.</summary>
    Click,
    ArrowUp,
    ArrowDown,
    ArrowLeft,
    ArrowRight,
    Enter,
    Tab,
    /// <summary>A TV page (YouTube TV): the button goes in as the remote's key (arrows, Enter, Escape).</summary>
    TvKey
}

/// <summary>The page's state that decides what a button does.</summary>
public readonly record struct BrowserPadContext(bool TvMode, bool FullScreen, bool CanGoBack);

/// <summary>
/// The controller on the couch browser's page, as one pure rule. Every button has an explicit meaning here: nothing falls through
/// to the general mouse mode (whose keys go to whichever window has the keyboard focus, and whose X starts the Windows touch keyboard
/// in a window of its own). The left stick is the cursor (on a TV page: the arrow keys, with their own repeat), so a direction that
/// only the stick reports is ignored; otherwise moving the cursor up threw the controller into the bar and moving it down scrolled
/// the page, and a TV page got every stick move twice.
/// </summary>
public static class BrowserInput
{
    public static BrowserPadAction Route(string button, bool fromStickOnly, BrowserPadContext ctx)
    {
        // The guaranteed way out, before anything else.
        if (button == "guide") return BrowserPadAction.LeaveToHome;
        if (button is "back" or "start") return ctx.FullScreen ? BrowserPadAction.ExitFullScreen : BrowserPadAction.ToBar;
        var direction = button is "up" or "down" or "left" or "right";
        if (direction && fromStickOnly) return BrowserPadAction.Ignore;
        if (ctx.TvMode && (direction || button is "a" or "b")) return BrowserPadAction.TvKey;
        return button switch
        {
            "a" => BrowserPadAction.Click,
            "b" when ctx.FullScreen => BrowserPadAction.ExitFullScreen,
            "b" => ctx.CanGoBack ? BrowserPadAction.GoBack : BrowserPadAction.Exit,
            "x" => BrowserPadAction.TypeText,
            "y" => BrowserPadAction.Address,
            "lb" => BrowserPadAction.ZoomOut,
            "rb" => BrowserPadAction.ZoomIn,
            "up" => BrowserPadAction.ArrowUp,
            "down" => BrowserPadAction.ArrowDown,
            "left" => BrowserPadAction.ArrowLeft,
            "right" => BrowserPadAction.ArrowRight,
            "rt" => BrowserPadAction.Enter,
            "lt" => BrowserPadAction.Tab,
            _ => BrowserPadAction.Ignore
        };
    }

    /// <summary>
    /// Who gets a button while the HUD is not the active window: a dialog or popup of the HUD's own window (a page's select list, a
    /// picker of the web view) counts as the HUD, anything else (a game, another program) does not.
    /// </summary>
    public static bool HudHasInput(bool windowActive, bool ownPopupInFront) => windowActive || ownPopupInFront;
}
