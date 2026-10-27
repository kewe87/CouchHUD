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
    /// Bridge methods of the UI that work while the couch is locked: playing, reading, sound, smart home, unlocking.
    /// Everything else (adding apps, plugins, settings, file browsing, uninstallers …) could start a program of the guest's
    /// choice, so it needs the code first. Leaving the couch checks in Hud itself.
    /// </summary>
    public static readonly IReadOnlySet<string> OpenWhileLocked = new HashSet<string>
    {
        "getLibrary", "launch", "toggleFavorite", "getStoreInfo", "getDetails", "systemInfo", "getSettings",
        "enterPcMode", "holdUnlockForSetup", "power", "quit", "unlock", "confirmWindowsUnlock", "getSecurity", "setPasscode", "setCouchLock",
        // The service: its state, and the repair (Windows' UAC prompt is the gate; the way out of a fail-closed lock).
        "getServiceStatus", "repairService",
        "getSession", "resumeGame", "quitGame", "getAudio", "setVolume", "setMute", "setAudioOutput", "getAppVolumes", "setAppVolume", "setAppMute", "getPads", "rumble",
        "getNews", "getFeeds", "assistantInfo", "askAssistant", "confirmAssistant", "resetAssistant",
        "getBrowser", "browserLayout", "browserFocus", "browserCommand", "displayOff", "displayOn", "getLaunchers", "getProfile", "capture", "getDownloads", "getDisplay", "getPerformance", "getCaptures",
        "getPlugins", "getPluginPage", "getQuickActions", "getNotifications", "markNotificationsRead", "clearNotifications",
        // The answer to the automatic Bluetooth switch-on's countdown: "now" or "leave it off" (the guest could switch the radio off with
        // the keyboard's airplane key anyway; switching it off in CouchHUD, "setBluetooth", needs the code).
        "answerBluetoothRecovery",
        "getBluetooth", "getRemote", "keyboardState", "getUpdates", "getCatalogUpdates", "getSelfUpdate", "getWeather", "getHomeEntities", "activateHomeEntity",
        "listSkins", "setSkin", "getGameFolders", "getCollections", "getParental", "parentalCheck", "parentalCloseGame", "parentalAuth", "setParental", "setParentalItemLock", "parentalExtend", "parentalReset", "parentalAcceptClock", "parentalAdoptZone"
    };

    /// <summary>Settings a guest may change while locked: sound and look only.</summary>
    public static readonly IReadOnlySet<string> HarmlessSettings = new HashSet<string>
    {
        "soundVolume", "soundSet", "audioMode", "musicVolume", "trailerPreviews", "rumble", "acceptedTerms",
        "screensaverMinutes", "screensaverStyle", "displayOffMinutes", "pixelShift", "reduceMotion", "pointer", "mouseUnlocked", "nightMode", "nightModeStrength", "expertMode"
    };
}
