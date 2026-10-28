using CouchHud.Sdk;

namespace CouchHud.Core.Guard;

/// <summary>
/// "Unlock first": the couch lock refused a call. An expected answer, not a fault: the bridge logs it as one line without a
/// stack trace and tells the page (error plus a locked flag), which asks for the code or shows nothing.
/// </summary>
public sealed class LockedException(string message) : InvalidOperationException(message);

/// <summary>
/// What a guest may do while the couch lock is closed. Pure decisions; the callers (bridge, tools, Hud)
/// translate "no" into "unlock first".
/// </summary>
public static class LockPolicy
{
    /// <summary>
    /// A locked couch starts games: every library item of type game, from a store, a game folder, a plugin or an own entry
    /// (own entries and plugins can only be added with the couch unlocked). Apps and websites need the unlock.
    /// </summary>
    public static bool MayLaunchLocked(ItemType type) => type == ItemType.Game;

    /// <summary>
    /// Bridge methods of the UI that work while the couch is locked: playing, reading, sound, unlocking.
    /// Everything else (adding apps, plugins, settings, file browsing, uninstallers …) could start a program of the guest's
    /// choice, so it needs the code first. Leaving the couch checks in Hud itself.
    /// </summary>
    public static readonly IReadOnlySet<string> OpenWhileLocked = new HashSet<string>
    {
        "getLibrary", "getFavorites", "launch", "toggleFavorite", "toggleFavoriteSite", "getStoreInfo", "getDetails", "systemInfo", "getSettings",
        "enterPcMode", "holdUnlockForSetup", "power", "quit", "unlock", "confirmWindowsUnlock", "getSecurity", "setPasscode", "setCouchLock",
        // The service: its state, and the repair (Windows' UAC prompt is the gate; the way out of a fail-closed lock).
        "getServiceStatus", "repairService",
        "getSession", "resumeGame", "quitGame", "getAudio", "setVolume", "setMute", "setAudioOutput", "getAppVolumes", "setAppVolume", "setAppMute", "getPads", "getBattery", "rumble",
        "getNews", "getFeeds", "assistantInfo", "askAssistant", "confirmAssistant", "resetAssistant",
        "getBrowser", "browserLayout", "browserFocus", "browserCommand", "displayOff", "displayOn", "getLaunchers", "getProfile", "capture", "getDownloads", "getDisplay",
        // The downloads page and the list of not installed games may be looked at; installing ("installGame"), starting an update, cancelling
        // ("cancelDownload") and opening a client's download page ("openClientDownloads") need the unlock.
        "getDownloadCenter", "dismissDownload", "getUninstalled", "getPerformance", "getCaptures",
        // Save games are not here on purpose: looking at them (getSaveInfo, getSaveSettings) and every change (backupSaves, restoreSaves,
        // deleteSaveBackup, setSaveSetting) need the unlock; restoring and deleting also the parents' code (Hud.RequireSaveChange). The
        // automatic backup after a game is the host's own work and needs no call. The "Continue playing" row lives on getLibrary.
        "getPlugins", "getPluginPage", "getPluginTabs", "getPluginGate", "getQuickActions", "runQuickAction", "getNotifications", "markNotificationsRead", "clearNotifications",
        // runQuickAction: Hud decides per entry (PluginPolicy.MayRun): only an entry of CouchHUD's own plugins that is marked AllowedWhileLocked.
        // Plugin rows: the bridge lets them through, Hud decides by the row (PluginPolicy.MayPress): only rows of CouchHUD's own plugins that
        // are marked AllowedWhileLocked; a third-party row never. The suggestions answer nothing while locked (they wait).
        "activatePluginRow", "changePluginRow", "getPluginSuggestions",
        // The answer to the automatic Bluetooth switch-on's countdown: "now" or "leave it off" (the guest could switch the radio off with
        // the keyboard's airplane key anyway; switching it off in CouchHUD, "setBluetooth", needs the code).
        "answerBluetoothRecovery",
        "getBluetooth", "getRemote", "keyboardState", "codeEntryState", "pairingState", "getUpdates", "getCatalogUpdates", "getSelfUpdate", "getWeather",
        "listSkins", "setSkin", "getGameFolders", "getCollections", "getParental", "parentalCheck", "parentalCloseGame", "parentalAuth", "setParental", "setParentalItemLock", "parentalExtend", "parentalReset", "parentalAcceptClock", "parentalAdoptZone"
    };

    /// <summary>Settings a guest may change while locked: sound and look only.</summary>
    public static readonly IReadOnlySet<string> HarmlessSettings = new HashSet<string>
    {
        "soundVolume", "soundSet", "audioMode", "musicVolume", "trailerPreviews", "favoritesMode", "rumble", "padGlyphs", "acceptedTerms",
        "screensaverMinutes", "screensaverStyle", "displayOffMinutes", "pixelShift", "reduceMotion", "pointer", "mouseUnlocked", "nightMode", "nightModeStrength", "expertMode"
    };

    /// <summary>
    /// Settings that bring a protected profile back to the profile picker (screensaver, display off). With parental controls and several
    /// profiles they are the parents' (review NEU-1): a child could otherwise switch them off on the locked couch and keep an adult's
    /// profile without limits forever. The host's own idle threshold (<see cref="CouchHud.Core.Config.ProfileIdleClock"/>) stays anyway.
    /// </summary>
    public static readonly IReadOnlySet<string> ProfileIdleSettings = new HashSet<string> { "screensaverMinutes", "displayOffMinutes" };

    /// <summary>A setting a guest may change on the locked couch; <paramref name="profilesGuarded"/>: parental controls on and several profiles.</summary>
    public static bool IsHarmlessSetting(string key, bool profilesGuarded) =>
        HarmlessSettings.Contains(key) && !(profilesGuarded && ProfileIdleSettings.Contains(key));
}
