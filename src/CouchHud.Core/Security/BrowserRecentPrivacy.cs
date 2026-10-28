namespace CouchHud.Core.Security;

/// <summary>
/// Whether the couch browser's start page may show the recently visited sites (titles and addresses). Hidden at the locked couch,
/// and with parental controls on also at the open couch, until the parents' code is entered afresh for exactly this (a one-time
/// grant), for this session only (until the couch is locked again, a game starts, the HUD is entered again from a game or parental
/// controls are switched). What this protects: an adult unlocked the couch and walked away, a child at the open couch still needs a
/// fresh code entry. What it does not: with parental controls the couch code and the parents' code are the same code (stored as
/// "both"), so a child who knows that code sees the list. Fail-closed: anything unknown hides them.
/// </summary>
public static class BrowserRecentPrivacy
{
    /// <summary>The action the parents' code is given for (ParentGrant): it fits nothing else.</summary>
    public const string ParentAction = "browserRecent";

    /// <summary>The list is hidden.</summary>
    /// <param name="locked">The couch is locked right now.</param>
    /// <param name="parentalOn">Parental controls are switched on (enforced or not).</param>
    /// <param name="shownByParent">The parents' code was given for the list in this session.</param>
    public static bool Hidden(bool locked, bool parentalOn, bool shownByParent) => locked || (parentalOn && !shownByParent);

    /// <summary>Hidden only because of parental controls: the start page offers the parents' code (at the locked couch, the couch code).</summary>
    public static bool NeedsParent(bool locked, bool parentalOn, bool shownByParent) => !locked && parentalOn && !shownByParent;

    /// <summary>The action the parents' code is given for when the history is switched off (ParentGrant): it fits nothing else.</summary>
    public const string HistoryOffAction = "browserHistoryOff";

    /// <summary>
    /// Switching the history off deletes the remembered sites (and the browser's own history). With parental controls on that is the
    /// parents' (audit 0.5 M1): otherwise a child at the open couch wipes what the parents would look at. Switching it on stays free.
    /// </summary>
    /// <param name="parentalOn">Parental controls are switched on (enforced or not).</param>
    /// <param name="historyOn">The history is on right now.</param>
    /// <param name="next">The value asked for.</param>
    public static bool HistoryChangeNeedsParent(bool parentalOn, bool historyOn, bool next) => parentalOn && historyOn && !next;
}
