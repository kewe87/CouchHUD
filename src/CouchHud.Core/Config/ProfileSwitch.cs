namespace CouchHud.Core.Config;

/// <summary>What a profile switch needs.</summary>
public enum SwitchNeed
{
    /// <summary>Nothing: the switch may happen.</summary>
    Free,

    /// <summary>The child's own code of the target profile (the parents' code opens it too).</summary>
    ProfileCode,

    /// <summary>The parents' code, fresh, for exactly this switch.</summary>
    ParentCode,

    /// <summary>Not at all (see <see cref="SwitchDecision.Refusal"/>).</summary>
    Refused
}

/// <summary>Why a switch is refused.</summary>
public enum SwitchRefusal
{
    None,

    /// <summary>A game runs: no switch, so a profile switch cannot keep a game running on another person's time.</summary>
    GameRunning,

    /// <summary>No such profile (unknown, malformed or broken id).</summary>
    Unknown,

    /// <summary>Already the profile on the couch, and nothing to enter again.</summary>
    Same
}

public sealed record SwitchDecision(SwitchNeed Need, SwitchRefusal Refusal = SwitchRefusal.None)
{
    public static readonly SwitchDecision Free = new(SwitchNeed.Free);
    public static readonly SwitchDecision ProfileCode = new(SwitchNeed.ProfileCode);
    public static readonly SwitchDecision ParentCode = new(SwitchNeed.ParentCode);

    public static SwitchDecision Refused(SwitchRefusal why) => new(SwitchNeed.Refused, why);
}

/// <summary>
/// The rules of the profile switch, as pure decisions (the host asks, enforces and checks the codes). Profiles part 2.
/// <list type="bullet">
/// <item>A child's profile is a further profile marked <see cref="UserProfile.Restricted"/>, or the default profile while it has limits.
/// Every other profile (an adult's, an unlimited default profile) needs the parents' code to enter once parental controls are on and
/// there is more than one profile.</item>
/// <item>A child's profile may have its own code: then entering it needs that code (or the parents').</item>
/// <item>Out of a profile whose time is up or that is blocked (time window, a blocked game) there is no switch without the parents' code,
/// also not into another child's profile.</item>
/// <item>No switch while a game runs.</item>
/// <item>A protected profile (one that needs a code to enter) goes back to the profile picker after a restart, the couch lock, the
/// screensaver and the display going off; entering it again needs its code again.</item>
/// </list>
/// </summary>
public static class ProfileSwitch
{
    /// <summary>
    /// Parental controls are on and there is more than one profile: every profile that is not a child's is the parents' then. Deliberately
    /// not "a child's profile exists": in an installation from before profiles the child plays on the default profile, and an adult's
    /// profile created by the parents must not be open to it (review H1).
    /// </summary>
    public static bool ChildrenGuarded(AppConfig c) => c.ParentalEnabled && c.AllProfiles.Count > 1;

    /// <summary>
    /// A child's profile: a further profile marked <see cref="UserProfile.Restricted"/>, or the default profile while it has any limit
    /// (that is where the child played before there were profiles), both only while parental controls are on. An unknown id resolves to the default profile (fail-closed).
    /// </summary>
    public static bool IsChild(AppConfig c, string id)
    {
        // Without parental controls nobody is a child: the limits and the mark are dormant, and no gate may treat the household as children.
        if (!c.ParentalEnabled) return false;
        var p = c.Profile(id);
        return p.IsDefault ? ProfileCheck.HasLimits(p.Rules) : p.Restricted;
    }

    /// <summary>The default profile has rules with a limit (whatever parental controls say); for the badge in the profile list only.</summary>
    public static bool HasLimits(AppConfig c, string id)
    {
        var p = c.Profile(id);
        return p.IsDefault && ProfileCheck.HasLimits(p.Rules);
    }

    /// <summary>Entering this profile needs a code: its own one, or the parents' for a profile that is not a child's while children are guarded.</summary>
    public static bool IsProtected(AppConfig c, string id)
    {
        var p = c.Profile(id);
        return p.HasCode || (!IsChild(c, p.Id) && ChildrenGuarded(c));
    }

    /// <summary>The profile picker comes at the start only when there is more than one profile.</summary>
    public static bool PickerAtStart(AppConfig c) => c.AllProfiles.Count > 1;

    /// <summary>At the start nobody has chosen yet: the picker waits for the profile on the couch when it is protected.</summary>
    public static bool HeldAtStart(AppConfig c) => PickerAtStart(c) && IsProtected(c, c.ActiveProfile.Id);

    /// <summary>
    /// Whose look and language the page has (skin, language, sounds, animations): while the picker waits for a protected profile
    /// (<paramref name="held"/>) the household's, that is the default profile's, so the picker shows nothing of the person whose code it
    /// asks for; otherwise the profile on the couch.
    /// </summary>
    public static ProfileView Look(AppConfig c, bool held) => held ? c.Profile(ProfileIds.Default) : c.ActiveProfile;

    /// <param name="c">The configuration.</param>
    /// <param name="from">The profile on the couch (as resolved: <see cref="AppConfig.ActiveProfile"/>'s id).</param>
    /// <param name="to">The profile asked for.</param>
    /// <param name="fromExhausted">The profile on the couch has no time left or is blocked (time window, a blocked game).</param>
    /// <param name="gameRunning">A game (or app session) runs.</param>
    /// <param name="reentry">The picker stands in front of a protected profile: entering the profile on the couch again counts as entering.</param>
    public static SwitchDecision Decide(AppConfig c, string from, string to, bool fromExhausted, bool gameRunning, bool reentry)
    {
        if (gameRunning) return SwitchDecision.Refused(SwitchRefusal.GameRunning);
        var target = c.Profile(to);
        if (target.Id != to) return SwitchDecision.Refused(SwitchRefusal.Unknown);
        var same = target.Id == c.Profile(from).Id;
        if (same && !reentry) return SwitchDecision.Refused(SwitchRefusal.Same);
        // Out of a profile whose time is up: only with the parents (going back into the same one gains nothing).
        if (!same && c.ParentalEnabled && fromExhausted) return SwitchDecision.ParentCode;
        if (!IsChild(c, target.Id) && ChildrenGuarded(c)) return SwitchDecision.ParentCode;
        if (target.HasCode) return SwitchDecision.ProfileCode;
        return SwitchDecision.Free;
    }

    /// <summary>
    /// What the parents' code may do with profiles (each is a separate, used-up grant in the host): switching into a profile, and every
    /// change (create, rename, look, child's profile or not, rules, the child's code, delete). A child's own code opens its profile only.
    /// </summary>
    public const string SwitchAction = "profileSwitch";

    public const string ManageAction = "profileManage";
}

/// <summary>
/// The host's own way back to the picker (review NEU-1): a protected profile on the couch with no real input (controller, keyboard, mouse;
/// never CouchHUD's own or the phone's) for <see cref="Threshold"/> goes back to the picker, whatever the screensaver and display
/// settings say and whether CouchHUD is in front or not. Never while a game runs; after it ends the time starts again. Fixed, no setting.
/// </summary>
public static class ProfileIdleClock
{
    public static readonly TimeSpan Threshold = TimeSpan.FromMinutes(10);

    /// <param name="lastActivity">The last real input, the end of the last game or the moment the profile was chosen, whichever is latest.</param>
    public static bool Due(DateTimeOffset lastActivity, DateTimeOffset now, bool gameRunning, bool protectedActive) =>
        protectedActive && !gameRunning && now - lastActivity >= Threshold;
}

/// <summary>
/// Where profiles can be reached from: only the HUD's own page (the UI bridge), never the phone remote, MCP, the assistant or a plugin
/// (a contract test pins that). The picker works at the locked couch (children switch between their profiles there); every change needs
/// the unlock and the parents' code.
/// </summary>
public static class ProfilePolicy
{
    public static readonly IReadOnlySet<string> OpenWhileLocked = new HashSet<string> { "getProfiles", "switchProfile", "profileScreen", "profileIdle" };

    public static readonly IReadOnlySet<string> All = new HashSet<string>(OpenWhileLocked)
    {
        "addProfile", "renameProfile", "setProfileLook", "setProfileRestricted", "setProfileCode", "removeProfile", "getProfileRules", "setProfileRule"
    };

    /// <summary>
    /// While a protected profile waits for its code (the picker is required), the bridge answers only these, before the couch lock is even
    /// looked at (review M1): the picker itself, the parents' code, what the picker needs to draw itself and its code prompt, sound, the
    /// display and switching off. Everything else is refused with <see cref="ProfileHeldException"/>. Not the settings (<c>getSettings</c>):
    /// the picker draws itself from <c>systemInfo</c> (with the household's look), and the settings hold household data and, on an open couch,
    /// the MCP token.
    /// </summary>
    public static readonly IReadOnlySet<string> OpenWhileHeld = new HashSet<string>(OpenWhileLocked)
    {
        "parentalAuth", "confirmWindowsUnlock", "getParental", "getSecurity", "getProfile", "systemInfo", "listSkins",
        "getSession", "getPads", "getBattery", "getBluetooth", "rumble", "getAudio", "setVolume", "setMute", "setAudioOutput", "power", "quit",
        "displayOn", "displayOff",
        // The code entry and the pairing code tell the host they are on screen, so Guide + Y refuses the screenshot there too.
        "codeEntryState", "pairingState"
    };
}

/// <summary>A protected profile waits for its code: nothing but the picker answers (the UI stays quiet about it).</summary>
public sealed class ProfileHeldException(string message) : InvalidOperationException(message);
