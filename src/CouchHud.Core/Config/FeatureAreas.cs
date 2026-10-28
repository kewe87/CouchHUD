using CouchHud.Core.Guard;
using CouchHud.Core.Library;
using CouchHud.Sdk;

namespace CouchHud.Core.Config;

/// <summary>
/// The parts of CouchHud that can be switched off per install (e.g. a pure app and browser launcher without gaming).
/// This file is the one map of what depends on what: every setting key, every bridge call that only makes sense for one
/// area, every assistant tool and every piece of background work names its area here, or "always". A new feature adds
/// its line here; the tests fail for a setting key that has none.
/// <para>
/// Switching an area off hides it and stops its work, nothing more: the library, play times, save backups and bookmarks
/// stay where they are and come back when the area is switched on again. It never loosens a restriction: parental play
/// time limits, age ratings and blocked entries keep working on what still runs, and the couch lock does not change.
/// </para>
/// </summary>
public static class FeatureAreas
{
    public const string Games = "games";
    public const string Apps = "apps";
    public const string Browser = "browser";
    public const string News = "news";
    /// <summary>Belongs to no area: settings, profiles, parental controls, couch lock, power, Windows, integrations.</summary>
    public const string Always = "always";

    /// <summary>All areas, in the order the wizard and the settings show them.</summary>
    public static readonly IReadOnlyList<string> All = [Games, Apps, Browser, News];

    /// <summary>Areas that start something. At least one of them stays on: a launcher that launches nothing is no launcher.</summary>
    public static readonly IReadOnlyList<string> LaunchAreas = [Games, Apps, Browser];

    /// <summary>
    /// Every key of Hud.SetSettingAsync. "always" for what every area needs (look, sound, controller, power, integrations).
    /// A test reads the switch in Hud and fails for a key missing here.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> SettingKeys = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Gaming: in-game behaviour, store art, trailers.
        ["pauseGameInMenu"] = Games,
        ["steamGridDbApiKey"] = Games,
        ["trailerPreviews"] = Games,
        // Browser.
        ["maximizeBrowser"] = Browser,
        ["browserHistory"] = Browser,
        // Everything else is shared by all areas.
        ["audioMode"] = Always, ["screensaverMinutes"] = Always, ["screensaverStyle"] = Always, ["displayOffMinutes"] = Always,
        ["caseHudInstallAutostart"] = Always, ["caseHudInstallFps"] = Always, ["caseHudInstallApi"] = Always, ["caseHudGamingLease"] = Always,
        ["tvInGuide"] = Always, ["tvOnWithCouchHud"] = Always, ["tvOffWithPc"] = Always, ["vpnInGuide"] = Always,
        ["bluetoothAutoOn"] = Always, ["selfUpdateCheck"] = Always, ["pixelShift"] = Always, ["reduceMotion"] = Always,
        ["pointer"] = Always, ["mouseUnlocked"] = Always, ["closeExplorerInLight"] = Always, ["acceptedTerms"] = Always,
        ["setupWizardDone"] = Always, ["guideTapInGame"] = Always, ["suppressCompetitors"] = Always, ["gameBarButton"] = Always,
        ["musicVolume"] = Always, ["soundVolume"] = Always, ["soundSet"] = Always, ["uiHdr"] = Always, ["sounds"] = Always,
        ["expertMode"] = Always, ["language"] = Always, ["rumble"] = Always, ["padGlyphs"] = Always, ["runStartupApps"] = Always,
        ["killExplorerOnReturn"] = Always, ["nightMode"] = Always, ["nightModeStrength"] = Always,
        ["closePcAppsOnReturn"] = Always, ["onlineArtwork"] = Always, ["clearArtworkCache"] = Always,
        ["autoSwitchHeadphones"] = Always, ["returnHoldMs"] = Always, ["weatherLocation"] = Always,
        ["homeAssistantUrl"] = Always, ["homeAssistantToken"] = Always, ["aiProvider"] = Always, ["aiModel"] = Always,
        ["anthropicApiKey"] = Always, ["openAiApiKey"] = Always, ["mcpEnabled"] = Always, ["mirrorNotifications"] = Always,
        ["remoteEnabled"] = Always, ["mcpLan"] = Always, ["mcpToken"] = Always, ["screenshotCombo"] = Always,
    };

    /// <summary>The keys of setSaveSetting (save backups exist for games only).</summary>
    public static readonly IReadOnlyDictionary<string, string> SaveSettingKeys = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["backupDir"] = Games, ["keep"] = Games, ["includeConfig"] = Games, ["autoBackup"] = Games,
        ["autoKeep"] = Games, ["autoMaxGameMb"] = Games, ["autoMaxTotalMb"] = Games,
    };

    /// <summary>
    /// Bridge calls that belong to one area. With the area off the call is refused before it does anything, whoever sends it (page,
    /// Guide menu, a plugin page). Every other call of the bridge is listed in <see cref="AlwaysMethods"/>; a test fails for a call in
    /// neither list, so a new call has to say where it belongs.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> BridgeMethods = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Gaming: save backups, game folders, store match, game clients, per-game controller profiles, not installed games.
        ["getSaveInfo"] = Games, ["backupSaves"] = Games, ["restoreSaves"] = Games, ["deleteSaveBackup"] = Games,
        ["getSaveSettings"] = Games, ["setSaveSetting"] = Games,
        ["getGameFolders"] = Games, ["addGameFolder"] = Games, ["removeGameFolder"] = Games, ["discoverGameFolders"] = Games,
        ["searchStore"] = Games, ["setStoreMatch"] = Games,
        ["getLaunchers"] = Games, ["setLauncherMode"] = Games, ["openLauncher"] = Games,
        ["getProfile"] = Games, ["setProfile"] = Games,
        ["getUninstalled"] = Games, ["installGame"] = Games, ["openClientDownloads"] = Games,
        // Steam's own download list (the Downloads tab reads the download center, which leaves game lines out while Gaming is off).
        ["getDownloads"] = Games,
        // Scanning a folder for games (Settings › Library › Scan a folder).
        ["scanFolder"] = Games,
        // Apps: the list of installed Windows programs to add from, and their icons.
        ["getInstalledApps"] = Apps, ["addInstalledApp"] = Apps, ["getAppIcons"] = Apps,
        // Browser: the couch browser itself, its window, focus and dialogs, its bookmarks, links that open in it.
        ["openBrowser"] = Browser, ["getBrowser"] = Browser, ["browserType"] = Browser, ["browserCommand"] = Browser,
        ["setBrowserZoom"] = Browser, ["revealBrowserRecent"] = Browser, ["addBookmark"] = Browser, ["removeBookmark"] = Browser,
        ["openUrl"] = Browser, ["setEntryEdge"] = Browser,
        ["browserLayout"] = Browser, ["browserFocus"] = Browser, ["browserDialogAnswer"] = Browser,
        // News.
        ["getNews"] = News, ["getFeeds"] = News, ["addFeed"] = News, ["removeFeed"] = News,
    };

    /// <summary>
    /// Bridge calls of no area: there with every choice. Calls on one entry (launch, details, edit …) are here too: they are checked per
    /// entry (<see cref="ItemMethods"/>), and the settings call per key (<see cref="SettingKeys"/>).
    /// </summary>
    public static readonly IReadOnlySet<string> AlwaysMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        // Library and entries (checked per entry or per added entry).
        "getLibrary", "launch", "toggleFavorite", "getStoreInfo", "getDetails", "uninstall", "browse", "addEntries", "editLaunch",
        "getLaunchOptions", "setLaunchOverride", "getItemInfo", "setItemInfo", "editItem", "getArtworkChoices", "setArtwork",
        "getCollections", "createCollection", "renameCollection", "deleteCollection", "setCollectionMember", "setCollectionOnHome",
        "removeEntry", "getSession", "resumeGame", "quitGame",
        // Console, settings (per key), lock, codes, parental controls and profiles.
        "systemInfo", "setShell", "enterPcMode", "power", "quit", "getSettings", "setFeatureAreas", "setSetting", "setIntegrationMode",
        "displayOn", "displayOff", "getSecurity", "getServiceStatus", "repairService", "getParental", "setParental",
        "setParentalItemLock", "parentalCheck", "parentalExtend", "parentalAuth", "confirmWindowsUnlock", "parentalReset",
        "parentalAcceptClock", "parentalAdoptZone", "parentalCloseGame", "getProfiles", "switchProfile", "profileScreen", "profileIdle",
        "addProfile", "renameProfile", "setProfileLook", "setProfileRestricted", "setProfileCode", "removeProfile", "getProfileRules",
        "setProfileRule", "unlock", "windowsSignIn", "setPasscode", "setCouchLock", "setParentCode", "readClipboard",
        "holdUnlockForSetup", "setBlockTaskManager", "openAutoLogonDialog", "enableAutoLogon", "setAutoLogon",
        // Sound, controllers, batteries, display, performance, captures and screenshots.
        "getAudio", "setVolume", "setMute", "setAudioOutput", "getAppVolumes", "setAppVolume", "setAppMute", "getBattery", "getPads",
        "rumble", "getDisplay", "setDisplayMode", "setHdr", "getPerformance", "capture", "getCaptures",
        // Assistant (its tools check the areas themselves).
        "assistantInfo", "askAssistant", "confirmAssistant", "resetAssistant",
        // Downloads tab (CouchHUD's and Windows' own downloads; game lines go with Gaming inside the center).
        "getDownloadCenter", "dismissDownload", "cancelDownload",
        // Plugins and skins.
        "getPlugins", "setPluginEnabled", "setPluginSetting", "getPluginPage", "activatePluginRow", "getQuickActions", "approvePlugin",
        "runQuickAction", "installPackage", "getCatalog", "getCatalogUpdates", "installCatalogEntry", "listSkins", "setSkin",
        // Notifications, Bluetooth, Wi-Fi, phone remote, firewall.
        "getNotifications", "markNotificationsRead", "clearNotifications", "getBluetooth", "scanBluetooth", "pairBluetooth",
        "unpairBluetooth", "getWifi", "scanWifi", "connectWifi", "disconnectWifi", "answerBluetoothRecovery", "setBluetooth", "getRemote",
        "codeEntryState", "pairingState", "keyboardState", "revokeRemote", "revokeRemoteDevice", "allowFirewall",
        // Startup, services, policies, processes, updates.
        "getStartup", "setStartupMode", "applyStartupSuggestions", "getServices", "toggleCouchService", "setupServiceTask",
        "releaseLockPolicies", "cleanupStuckPolicies", "getProcesses", "switchProcess", "killProcess", "getUpdates", "checkUpdates",
        "getSelfUpdate", "checkSelfUpdate", "startSelfUpdate", "startUpdate",
        // Weather and smart home (signing in through Home Assistant's web page needs the browser: Hud checks that itself).
        "getWeather", "searchPlaces", "getHomeEntities", "activateHomeEntity", "pinHomeEntity", "getHomeCatalog", "setHomeSelection",
        "testHomeAssistant", "signInHomeAssistant", "signOutHomeAssistant",
    };

    /// <summary>Bridge calls whose first argument is a library entry: refused for an entry of an area that is off.</summary>
    public static readonly IReadOnlySet<string> ItemMethods = new HashSet<string>(StringComparer.Ordinal)
    {
        "launch", "toggleFavorite", "getStoreInfo", "getDetails", "uninstall", "getItemInfo", "setItemInfo", "getArtworkChoices",
        "setArtwork", "editLaunch", "getLaunchOptions", "setLaunchOverride", "setStoreMatch", "setEntryEdge", "getSaveInfo",
        "backupSaves", "restoreSaves", "deleteSaveBackup", "getProfile", "setProfile", "editItem", "removeEntry",
    };

    /// <summary>Assistant and MCP tools that belong to one area (the others work on the entries that are shown).</summary>
    public static readonly IReadOnlyDictionary<string, string> Tools = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["get_news"] = News,
    };

    /// <summary>Background work and what it belongs to. Hud asks <see cref="Runs"/> before each run.</summary>
    public static readonly IReadOnlyDictionary<string, string> Services = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["gameStoreScan"] = Games,        // Steam, Epic, GOG, Ubisoft, Xbox and game folders (kept for parental controls, see LibraryAreas)
        ["gameArtwork"] = Games,          // store and SteamGridDB art for games
        ["storeMatch"] = Games,           // description and age rating from the Steam store
        ["saveAutoBackup"] = Games,       // save backup after playing, and its notifications
        ["gameDownloads"] = Games,        // Steam/Epic download watch, not installed games
        ["launcherStandby"] = Games,      // Steam, Epic & co. started in the background
        ["steamGuardSettings"] = Games,   // Steam's Big Picture/overlay settings CouchHud sets
        ["installedApps"] = Apps,         // the list of installed Windows programs (read when asked)
        ["couchBrowser"] = Browser,       // the WebView2 browser window
        ["newsFeeds"] = News,             // feed polling
    };

    /// <summary>Area of a setting key; null when the key is unknown (a test makes sure that never happens for a real key).</summary>
    public static string? AreaOfSetting(string key) =>
        SettingKeys.TryGetValue(key, out var a) ? a : key.StartsWith("saves.", StringComparison.Ordinal)
            && SaveSettingKeys.TryGetValue(key["saves.".Length..], out var s) ? s : null;

    /// <summary>The area set stored in the config: everything is on unless listed as off; unknown names are ignored.</summary>
    public static AreaSet From(AppConfig config) => new(config.DisabledAreas);

    /// <summary>
    /// The list to store for a choice of enabled areas. Unknown names are refused, at least one launch area must stay on.
    /// </summary>
    public static List<string> DisabledFor(IEnumerable<string> enabled)
    {
        var on = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in enabled)
        {
            if (!All.Contains(a)) throw new ArgumentException($"unknown area {a}");
            on.Add(a);
        }
        if (!LaunchAreas.Any(on.Contains)) throw new ArgumentException(Localization.Strings.Get("areas.needOne"));
        return All.Where(a => !on.Contains(a)).ToList();
    }

    /// <summary>
    /// The areas an entry needs: games or apps by its type, and the browser as well for a web page (https link, the couch
    /// browser) or a browser started as a program. An entry is shown only when all of them are on.
    /// </summary>
    public static IReadOnlyList<string> AreasOf(LibraryItem item)
    {
        var type = item.Type == ItemType.Game ? Games : Apps;
        return NeedsBrowser(item.Launch) ? [type, Browser] : [type];
    }

    /// <summary>
    /// Whether a game store is read during a scan. With Gaming off it is skipped, unless parental controls need it to see games
    /// started outside CouchHud, or the user marked one of its entries as an app (e.g. Wallpaper Engine from Steam): then the
    /// store is read and AreasOf decides per entry, so only the entries typed as app show up.
    /// </summary>
    public static bool ReadsStore(AppConfig config, string source)
    {
        if (From(config).Games || config.ParentalEnabled) return true;
        var prefix = source + ":";
        return config.Overrides.Any(o => o.Value?.Type == "app" && o.Key.StartsWith(prefix, StringComparison.Ordinal));
    }

    public static bool NeedsBrowser(LaunchTarget launch) =>
        launch.Kind == LaunchKind.Url ? WebLinks.IsWebUrl(launch.Target)
            : LockedForeground.IsBrowser(SafeFileName(launch.Target));

    private static string? SafeFileName(string? path)
    {
        try { return string.IsNullOrWhiteSpace(path) ? null : Path.GetFileNameWithoutExtension(path.Trim().Trim('"')); }
        catch (ArgumentException) { return null; }
    }
}

/// <summary>Which areas are on. Built from the config each time it is needed, never cached across a change.</summary>
public sealed class AreaSet
{
    private readonly HashSet<string> _off;

    /// <summary>
    /// The areas switched off. Unknown names are ignored; a list that leaves no launch area on (only by hand in config.json) counts as
    /// everything on, the same as the page does (ui/core/areas.js).
    /// </summary>
    public AreaSet(IEnumerable<string>? disabled)
    {
        _off = new HashSet<string>((disabled ?? []).Where(FeatureAreas.All.Contains), StringComparer.Ordinal);
        if (FeatureAreas.LaunchAreas.All(_off.Contains)) _off.Clear();
    }

    public static AreaSet AllOn { get; } = new([]);

    public bool On(string area) => area == FeatureAreas.Always || !_off.Contains(area);

    public bool Games => On(FeatureAreas.Games);
    public bool Apps => On(FeatureAreas.Apps);
    public bool Browser => On(FeatureAreas.Browser);
    public bool News => On(FeatureAreas.News);
    public bool AllEnabled => _off.Count == 0;

    public IReadOnlyList<string> Enabled => FeatureAreas.All.Where(On).ToList();

    public bool Shows(LibraryItem item) => FeatureAreas.AreasOf(item).All(On);

    /// <summary>A setting key whose area is off (unknown keys are left to the caller's own check).</summary>
    public bool AllowsSetting(string key) => FeatureAreas.AreaOfSetting(key) is not { } a || On(a);

    public bool AllowsMethod(string method) => !FeatureAreas.BridgeMethods.TryGetValue(method, out var a) || On(a);

    public bool AllowsTool(string tool) => !FeatureAreas.Tools.TryGetValue(tool, out var a) || On(a);

    public bool Runs(string service) => !FeatureAreas.Services.TryGetValue(service, out var a) || On(a);
}
