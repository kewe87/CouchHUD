namespace CouchHud.Core.Security;

/// <summary>
/// The couch browser runs only on the unlocked couch. While someone uses it, the 2-minute unlock is extended, so a page being read
/// is not closed under them; idle, the HUD warns 30 seconds before the end. Only input of a person counts as use: the controller, a
/// real keyboard or mouse (not CouchHUD's own SendInput), the HUD bar's buttons and opening the browser. What the page does by itself
/// (a new title, an address via pushState, sound, full screen, its dialogs, downloads) never counts: otherwise a page with a ticking
/// title kept the couch unlocked for good.
/// </summary>
public static class BrowserUnlock
{
    /// <summary>Input this recent counts as use.</summary>
    public static readonly TimeSpan InputWindow = TimeSpan.FromSeconds(30);

    /// <summary>Extended only when less than this is left (the unlock is not pushed forward on every tick).</summary>
    public static readonly TimeSpan ExtendBelow = TimeSpan.FromSeconds(90);

    /// <summary>What an extension gives, counted from now.</summary>
    public static readonly TimeSpan Extension = TimeSpan.FromMinutes(2);

    /// <summary>The warning "the browser closes soon" this long before the end.</summary>
    public static readonly TimeSpan WarnBefore = TimeSpan.FromSeconds(30);

    /// <summary>The unlock after the check; <see cref="Warn"/>: show the warning now; <see cref="Warned"/>: it was shown for this run-out.</summary>
    public readonly record struct Decision(DateTimeOffset UnlockedUntil, bool Warn, bool Warned);

    /// <summary>
    /// One check (twice a second while the browser is on screen and the couch unlocked).
    /// </summary>
    /// <param name="now">Monotonic now.</param>
    /// <param name="unlockedUntil">When the unlock runs out.</param>
    /// <param name="lastUserInput">Last controller button, HUD bar button, typing from the HUD or opening of the browser (same clock as <paramref name="now"/>).
    /// Never set by an event of the page.</param>
    /// <param name="inputNow">A real keyboard or mouse (not CouchHUD's own input) or the controller's stick within <see cref="InputWindow"/>.</param>
    /// <param name="warned">The warning was shown already.</param>
    public static Decision Next(DateTimeOffset now, DateTimeOffset unlockedUntil, DateTimeOffset lastUserInput, bool inputNow, bool warned)
    {
        if (unlockedUntil <= now) return new(unlockedUntil, false, false);
        var age = now - lastUserInput;
        var used = inputNow || (age >= TimeSpan.Zero && age < InputWindow);
        if (used)
        {
            var until = unlockedUntil - now < ExtendBelow ? now + Extension : unlockedUntil;
            return new(until, false, false);
        }
        if (unlockedUntil - now <= WarnBefore && !warned) return new(unlockedUntil, true, true);
        return new(unlockedUntil, false, warned);
    }
}
