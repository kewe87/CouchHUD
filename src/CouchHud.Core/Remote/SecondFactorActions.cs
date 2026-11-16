namespace CouchHud.Core.Remote;

/// <summary>One action that needs the second factor while it is on, and why.</summary>
/// <param name="Id">The name bound into the challenge and used at the host's guard (<c>Hud.RequireSecondFactor</c>).</param>
/// <param name="Why">What it takes away, in one sentence (for the code, the tests and SECURITY.md).</param>
public sealed record SecondFactorAction(string Id, string Why);

/// <summary>Where a grant action or a service operation stands: needs the second factor (<see cref="Action"/>) or is left out for <see cref="Reason"/>.</summary>
public sealed record SecondFactorCover(string? Action, string Reason);

/// <summary>
/// The positive list of the actions that need the second factor (the phone's confirmation, from 0.7) while it is on, in ONE place. Only what
/// takes protection away is on it: who gets in (sign-in, shell, auto sign-in), the lock and its codes, the parental controls and the
/// children's rules, PC mode, the phones' own rights and pairing, the rights of plugins, the privacy switch Offline and the second factor
/// itself. Everything else (starting a game, extending the time, the library, the settings of the look) stays with the code alone.
/// <para>
/// <see cref="GrantActions"/> and <see cref="ServiceOperations"/> are the completeness proof: every action that asks the parents' or the couch
/// code today, and every operation of the service that takes a grant, is mapped here to one of the actions below or left out with a reason;
/// a test enumerates the sources, so a new action cannot slip in unclassified.
/// </para>
/// </summary>
public static class SecondFactorActions
{
    public const string Autologon = "autologon";
    public const string Shell = "shell";
    public const string LockMode = "lockMode";
    public const string Passcode = "passcode";
    public const string ParentCode = "parentCode";
    public const string ParentalOff = "parentalOff";
    public const string ProfileRules = "profileRules";
    public const string ProfileUnrestrict = "profileUnrestrict";
    public const string PcMode = "pcMode";
    public const string SecondFactorOff = "secondFactorOff";
    public const string RemoteLevel = "remoteLevel";
    public const string RemotePair = "remotePair";
    public const string PluginRights = "pluginRights";
    public const string PrivacyOffline = "privacyOffline";
    public const string TaskManager = "taskManager";
    public const string CompanionLocked = "companionLocked";

    public static readonly IReadOnlyList<SecondFactorAction> Critical =
    [
        new(Autologon, "Automatic sign-in lets everybody into the account without the lock."),
        new(Shell, "The shell decides what a restart starts; taking CouchHUD out opens the desktop without the lock."),
        new(LockMode, "Switching the couch lock off or to another mode changes who gets in."),
        new(Passcode, "A new couch code is the key to the couch."),
        new(ParentCode, "A new parents' code is the key to every protected setting."),
        new(ParentalOff, "Switching the parental controls off takes the children's limits away."),
        new(ProfileRules, "Rules, age limits, codes and deletion of profiles decide what a child may do (tightening too: the direction is not told apart, one rule less to get wrong)."),
        new(ProfileUnrestrict, "Switching from a child's profile to one without limits ends the children's rules for whoever holds the controller."),
        new(PcMode, "PC mode ends the couch lock and the parental controls for the desktop."),
        new(SecondFactorOff, "Switching the second factor off must not be possible without it (no downgrade)."),
        new(RemoteLevel, "Raising what a phone may do gives it more of the couch (lowering needs nothing extra)."),
        new(RemotePair, "A new phone gets a share of the couch."),
        new(PluginRights, "Installing is approving: a local package, a package of another catalog, an update that adds capabilities, a new catalog source, the automatic plugin update, and switching on a plugin that is not CouchHUD's own (or that may use the network, the assistant or programs), a plugin's function levels, approval, accepted withdrawal and confirmed program decide what code may do on this PC (CouchHUD's own signed packages without new capabilities stay free)."),
        new(PrivacyOffline, "Offline off lets CouchHUD reach the internet again (and with it the other switches)."),
        new(TaskManager, "Letting the Task Manager through gives a way to end CouchHUD."),
        new(CompanionLocked, "A companion rule that also starts on the locked couch, or beside every game, starts an app next to a game without the lock or the child's list having a say (plain rules and removing a rule need nothing extra).")
    ];

    private static readonly HashSet<string> Ids = Critical.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);

    /// <summary>The action is on the list. Null, unknown and misspelled names are not: they need nothing extra.</summary>
    public static bool IsCritical(string? action) => action is not null && Ids.Contains(action);

    /// <summary>The key of the action's text (host, TV page and phone page).</summary>
    public static string TextKey(string action) => "twofa.action." + action;

    private static SecondFactorCover Yes(string action) => new(action, "");
    private static SecondFactorCover Not(string reason) => new(null, reason);

    /// <summary>Every action the HUD asks a code for (the page's names, the host's grants), mapped to the action above or left out with the reason.</summary>
    public static readonly IReadOnlyDictionary<string, SecondFactorCover> GrantActions = new Dictionary<string, SecondFactorCover>(StringComparer.Ordinal)
    {
        ["autologon"] = Yes(Autologon),
        ["lockMode"] = Yes(LockMode),
        ["lockModeCode"] = Yes(LockMode),
        ["passcode"] = Yes(Passcode),
        ["parentalOff"] = Yes(ParentalOff),
        ["profile"] = Yes(ProfileRules),
        ["profileManage"] = Yes(ProfileRules),
        ["ratingSystem"] = Yes(ProfileRules),
        ["remoteLevel"] = Yes(RemoteLevel),
        ["remotePair"] = Yes(RemotePair), // a new pairing code: with parental controls the parents' code for exactly this one (PairingGrant); the second factor confirms on a paired phone
        ["pluginApprove"] = Yes(PluginRights),
        ["pluginAcceptRevoked"] = Yes(PluginRights),
        ["pluginConfirmExe"] = Yes(PluginRights),
        ["privacy"] = Yes(PrivacyOffline),
        ["secondFactorOff"] = Yes(SecondFactorOff),
        ["parentalOn"] = Not("raises protection; the code that comes with it is the parentCode action"),
        ["extend"] = Not("everyday: a few more minutes of play time, bound to one grant"),
        ["launch"] = Not("starting one locked game: everyday, bound to the game"),
        ["lock"] = Not("locking or releasing one game: everyday, bound to the game"),
        ["lockAll"] = Not("locking or releasing one game for all child profiles: the same everyday step as lock, bound to the game and to this scope"),
        ["lockAllReset"] = Not("sets the damaged list of locks for all child profiles anew: the children are locked out of every game until then, so it only ever starts from the stricter state"),
        ["profileSwitch"] = Yes(ProfileUnrestrict),
        ["saveSettings"] = Not("backup limits only"),
        ["storeMatch"] = Not("which store entry fits a game: cosmetics"),
        ["startOptions"] = Not("start parameters of one game, bound to the game"),
        ["editType"] = Not("the kind of one entry, bound to the entry"),
        ["removeEntry"] = Not("removes one own entry; a locked one only with the parents' code, bound to the entry, and its locks move to the entries that take its id"),
        ["editHidden"] = Not("hides or shows one library entry, bound to the entry; a hidden entry keeps its play time, limits and item lock, so no way past the rules opens"),
        ["companions"] = Yes(CompanionLocked), // plain rules are judged like starting the app by hand (eligible program, couch lock, the child's own list); the second factor is asked only for WhenLocked and mode "all" (HudCompanions)
        ["addGame"] = Not("adds a library entry the child's rules still apply to (the parents' code covers it; a known limit)"),
        ["installGame"] = Not("starts a download of one game"),
        ["openClientDownloads"] = Not("opens a store client"),
        ["networkListener"] = Not("switching ON a network listener (phone connection, MCP, MCP in the home network) needs the parents' code; the phone's pairing and rights keep their own second factor"),
        ["idleSetting"] = Not("screensaver and display-off timing"),
        ["areas"] = Not("hides or shows areas; it opens no way past the lock or the rules"),
        ["catalogSourceAdd"] = Yes(PluginRights),
        ["catalogSourceRemove"] = Not("narrows the catalog"),
        ["browserRecent"] = Not("shows the recent sites of the browser for a while"),
        ["tvInput"] = Not("switching the TV's input"),
        ["pluginFunction"] = Not("use of a plugin function, not a right"),
        ["pluginPage"] = Not("use of a plugin page, not a right"),
        ["pluginAction"] = Not("use of a plugin action, not a right"),
        ["pluginInstall"] = Yes(PluginRights),
        ["pluginUpdate"] = Yes(PluginRights),
        ["pluginEnable"] = Yes(PluginRights),
        ["pluginDisable"] = Not("raises protection"),
        ["pluginUninstall"] = Not("removes the code; the parents' function levels stay keyed by plugin id, so a reinstall brings them back (removing them would be PluginRights)"),
    };

    /// <summary>
    /// The operations of the service that take a grant, mapped the same way, by <c>Operation</c> name. The couch and parent codes reach them
    /// only through the HUD's actions above; the Windows administrator's approval (UAC) is the emergency way and is not a code at all.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, SecondFactorCover> ServiceOperations = new Dictionary<string, SecondFactorCover>(StringComparer.Ordinal)
    {
        ["SetAutoLogon"] = Yes(Autologon),
        ["SetLockMode"] = Yes(LockMode),
        ["EnrollPasscode"] = Yes(Passcode),
        ["ChangePasscode"] = Yes(Passcode),
        ["RemovePasscode"] = Yes(LockMode),
        ["ReleaseLockPolicies"] = Not("takes the couch lock's policies off after a crash; the HUD's path to it runs through UAC"),
        ["CleanupPolicyKeys"] = Not("same as ReleaseLockPolicies"),
        ["SetRemoteFirewall"] = Not("Windows asks the administrator for the firewall rule itself"),
        ["ResetCodeStore"] = Not("an administrator's approval only (UAC), stronger than a second factor"),
        ["ResetServiceState"] = Not("an elevated administrator only"),
        ["ReleaseAllAccounts"] = Not("an elevated administrator only"),
    };
}
