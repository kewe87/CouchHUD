using CouchHud.Core.Files;
using CouchHud.Core.Localization;
using CouchHud.Core.Saves;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CouchHud.Core.Config;

public sealed class CustomEntry
{
    public string Name { get; set; } = "";
    /// <summary>"game" or "app".</summary>
    public string Type { get; set; } = "app";
    public string? Url { get; set; }
    public string? Path { get; set; }
    public List<string> Args { get; set; } = [];
    public string? Cwd { get; set; }
    public string? Image { get; set; }
    public string? Hero { get; set; }
    /// <summary>Web entries: true opens in Edge (streaming with copy protection), false in the couch browser, null not decided yet (migrated at start).</summary>
    public bool? OpenInEdge { get; set; }
}

/// <summary>
/// Per-game settings applied when the game starts and undone when it ends, like the
/// per-game profiles on handhelds. Null means "leave as it is".
/// </summary>
public sealed class GameProfile
{
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? RefreshRate { get; set; }
    public bool? Hdr { get; set; }
    public string? AudioDevice { get; set; }
    /// <summary>False keeps the night mode off while this game runs (e.g. for color-critical games).</summary>
    public bool? NightMode { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => Width is null && Hdr is null && AudioDevice is null && NightMode is null;
}

/// <summary>User changes on top of what a store or plugin reports.</summary>
public sealed class ItemOverride
{
    public string? Name { get; set; }
    /// <summary>"game" or "app".</summary>
    public string? Type { get; set; }
    public bool Hidden { get; set; }
    /// <summary>Artwork the user picked (https or file: URL); wins over the automatic art. Null: automatic.</summary>
    public string? Cover { get; set; }
    public string? Hero { get; set; }
    public string? Logo { get; set; }
    /// <summary>Startup parameters instead of the scanned ones (an exe start, or the own start target). Null: automatic.</summary>
    public List<string>? LaunchArgs { get; set; }
    /// <summary>Working folder instead of the scanned one. Null: automatic.</summary>
    public string? LaunchCwd { get; set; }
    /// <summary>An .exe, .bat or .cmd inside the install folder, started instead of the scanned start (checked again at every start). Null: automatic.</summary>
    public string? LaunchTarget { get; set; }
    /// <summary>
    /// SHA-256 (hex) and size of a batch file (.bat/.cmd) as the start target was approved: the approval covers this content, not just the path.
    /// A start whose file differs is refused until the target is confirmed again. Null for an .exe target (see SECURITY.md).
    /// </summary>
    public string? LaunchTargetSha256 { get; set; }
    public long? LaunchTargetSize { get; set; }

    /// <summary>Steam store entry the user picked (an app id) or "none" (no store entry fits). Null: matched automatically by the scanned name.</summary>
    public string? StoreMatch { get; set; }
    /// <summary>The picked store entry's name, for the edit menu only.</summary>
    public string? StoreMatchName { get; set; }
    /// <summary>
    /// The age rating of the match before a change the parents did not confirm: the stricter of it and the new match's counts until they do.
    /// Null: nothing pending.
    /// </summary>
    public int? StoreAgeFloor { get; set; }
    /// <summary>Own texts instead of the store's (plain text, shown as text). Null: the store's.</summary>
    public string? Description { get; set; }
    public List<string>? Developers { get; set; }
    public List<string>? Publishers { get; set; }
    public string? ReleaseDate { get; set; }
    public List<string>? Genres { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasLaunch => LaunchArgs is not null || LaunchCwd is not null || LaunchTarget is not null;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasInfo => Description is not null || Developers is not null || Publishers is not null || ReleaseDate is not null || Genres is not null;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasStore => StoreMatch is not null || StoreAgeFloor is not null;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => Name is null && Type is null && !Hidden && Cover is null && Hero is null && Logo is null && !HasLaunch && !HasInfo && !HasStore;
}

/// <summary>A user-made list of games and apps ("Couch Koop", "Kinder").</summary>
public sealed class ItemCollection
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Library item ids in the order they were added; ids that no longer exist are ignored.</summary>
    public List<string> Items { get; set; } = [];
    /// <summary>Shown as its own shelf on Home.</summary>
    public bool OnHome { get; set; }
}

/// <summary>A website in the couch browser's bookmark list.</summary>
public sealed class Bookmark
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
}

public sealed class AppConfig
{
    /// <summary>Kill explorer.exe when returning from PC mode. Null: yes if CouchHud is the shell.</summary>
    public bool? KillExplorerOnReturn { get; set; }

    /// <summary>
    /// Set when CouchHud restarts into PC mode once: if the RunOnce entry did not put the shell
    /// entry back, the next CouchHud start does (see ShellManager.RestartIntoPcMode).
    /// </summary>
    public bool PendingShellRestore { get; set; }

    /// <summary>Why the recovery removed the shell entry; the next start tells the user once, then clears it.</summary>
    public string ShellRemovedReason { get; set; } = "";

    /// <summary>Run the HKCU/HKLM Run entries on boot, which explorer normally does.</summary>
    public bool RunStartupApps { get; set; } = true;

    public bool Sounds { get; set; } = true;

    /// <summary>UI sound volume, 0-100.</summary>
    public int SoundVolume { get; set; } = 70;

    /// <summary>Sound set id, or empty to follow the skin.</summary>
    public string SoundSet { get; set; } = "";

    /// <summary>
    /// "off", "auto" (effects when Windows HDR is on) or "on" (CouchHud turns Windows HDR on
    /// while it runs and shows its HDR effects).
    /// </summary>
    public string UiHdr { get; set; } = "auto";

    /// <summary>Short controller vibration on navigation and confirm.</summary>
    public bool Rumble { get; set; } = true;

    /// <summary>Button symbols on screen: "auto" (the kind of pad used last), "xbox" or "playstation".</summary>
    public string PadGlyphs { get; set; } = "auto";

    /// <summary>Muted game trailers play in the background when an item stays focused.</summary>
    public bool TrailerPreviews { get; set; } = true;

    /// <summary>Switch audio to headphones when they are plugged in.</summary>
    public bool AutoSwitchHeadphones { get; set; } = true;

    public string Skin { get; set; } = "default";

    /// <summary>Hold Guide (or Back+Start) this long in PC mode to come back.</summary>
    public int ReturnHoldMs { get; set; } = 1000;

    /// <summary>Optional free key from steamgriddb.com for non-Steam artwork.</summary>
    public string SteamGridDbApiKey { get; set; } = "";

    /// <summary>Look up and download artwork online (Steam store, SteamGridDB with a key). Off: only what is on disk.</summary>
    public bool OnlineArtwork { get; set; } = true;

    public List<string> Favorites { get; set; } = [];

    public Dictionary<string, long> LastLaunched { get; set; } = new();

    public Dictionary<string, double> PlaytimeMinutes { get; set; } = new();

    /// <summary>Length of the last play session per entry in minutes (the "Continue playing" row on Home shows it).</summary>
    public Dictionary<string, double> LastSessionMinutes { get; set; } = new();

    public Dictionary<string, ItemOverride> Overrides { get; set; } = new();

    public List<ItemCollection> Collections { get; set; } = [];

    /// <summary>Place for the weather in the header; null hides it.</summary>
    public CouchHud.Core.Integrations.Place? WeatherLocation { get; set; }

    // LEGACY (Home Assistant is a plugin now): HomeAssistantUrl, HomeAssistantToken, HomeAssistantRefreshToken, HomeAssistantPinned and
    // HomeAssistantEntities are only read, once, to hand them to the Home Assistant plugin (PluginSettingSeeds). Nothing writes them any more.
    /// <summary>LEGACY, read only for the plugin's first settings. e.g. http://homeassistant.local:8123</summary>
    public string HomeAssistantUrl { get; set; } = "";

    /// <summary>LEGACY, read only for the plugin's first settings. Long-lived access token, encrypted for the Windows user (DPAPI).</summary>
    public string HomeAssistantToken { get; set; } = "";

    /// <summary>
    /// LEGACY, read only for the plugin's first settings. "Sign in with Home Assistant": the refresh token for <see cref="HomeAssistantUrl"/>, encrypted for the Windows user (DPAPI) like the
    /// long-lived token. At most one of the two is set; another address drops both.
    /// </summary>
    public string HomeAssistantRefreshToken { get; set; } = "";

    /// <summary>RSS/Atom feeds for the News tab.</summary>
    public List<CouchHud.Core.Integrations.NewsFeed> NewsFeeds { get; set; } =
        CouchHud.Core.Integrations.NewsService.Defaults.Select(f => new CouchHud.Core.Integrations.NewsFeed { Name = f.Name, Url = f.Url }).ToList();

    /// <summary>LEGACY, read only for the plugin's first settings. Entities shown in the guide menu for quick access.</summary>
    public List<string> HomeAssistantPinned { get; set; } = [];

    /// <summary>
    /// LEGACY, read only for the plugin's first settings. Entities chosen for the Smart Home tab, in their order (ids like "light.sofa").
    /// Untrusted when read: the seed sanitizes it (well-formed ids only).
    /// </summary>
    public List<string> HomeAssistantEntities { get; set; } = [];

    /// <summary>Game client id ("steam", "epic" …) → "standby" (starts quietly with CouchHud, the default), "demand" (only when a game or action needs it) or "off".</summary>
    public Dictionary<string, string> LauncherModes { get; set; } = new();

    /// <summary>Older versions: client id → start with CouchHud (false = off). Read only, replaced by <see cref="LauncherModes"/> at the first change.</summary>
    public Dictionary<string, bool> LauncherAutostart { get; set; } = new();

    /// <summary>"off", "ui" (sounds only) or "music" (sounds and background music).</summary>
    public string AudioMode { get; set; } = "ui";

    /// <summary>Background music volume, 0-100.</summary>
    public int MusicVolume { get; set; } = 35;

    /// <summary>A tap on the Xbox button in a game brings up the HUD, like on a console (holding opens the guide menu).</summary>
    public bool GuideTapInGame { get; set; } = true;

    /// <summary>The setup assistant was told no to the automatic sign-in: its mode question then counts the shell setup as complete without it. Only decides what the assistant offers; the sign-in itself is set up or removed elsewhere.</summary>
    public bool AutoLogonDeclined { get; set; }

    /// <summary>Guide held + Y takes a screenshot (also over a game); the Guide button's own tap then waits for its release.</summary>
    public bool ScreenshotCombo { get; set; } = true;

    /// <summary>
    /// CouchHud is in charge on the couch (full integration only): game clients and known pop-ups do not get
    /// the foreground, the Game Bar does not get the Xbox button. Everything is undone in PC mode and on quit.
    /// </summary>
    public bool SuppressCompetitors { get; set; } = true;

    /// <summary>
    /// The save backup limits are decimal MB (since 0.5). A file without this field was written with binary MB and is moved up once
    /// when read (<see cref="ConfigStore"/>, Parse); a new file starts decimal.
    /// </summary>
    public bool SaveLimitsDecimal { get; set; } = true;

    /// <summary>
    /// The plugins an install written by 0.5.x or older had in its user folder were taken over once into the list of approved plugins
    /// (PluginTrust.AdoptExisting). A file without this field was written by 0.5.x or older and reads as false; a new file and the
    /// stand-in for a broken one start true, so a missing list later adopts nothing and unknown folders stay unknown.
    /// </summary>
    public bool PluginTrustAdopted { get; set; } = true;

    /// <summary>The one-time upgrade from the blind Game Bar change to the snapshot has run.</summary>
    public bool GameBarMigrated { get; set; }

    /// <summary>The "Xbox button is CouchHUD's now" notice was shown once.</summary>
    public bool GameBarHintShown { get; set; }

    /// <summary>Screensaver after this many idle minutes in the HUD (0 = never). Protects OLED TVs from burn-in.</summary>
    public int ScreensaverMinutes { get; set; } = 5;

    /// <summary>"art" (slow artwork slideshow), "clock" (wandering clock), "dim" or "black".</summary>
    public string ScreensaverStyle { get; set; } = "art";

    /// <summary>Switch the display off after this many minutes of screensaver (0 = never).</summary>
    public int DisplayOffMinutes { get; set; }

    /// <summary>Moves the whole HUD by a few pixels now and then so static parts don't burn in.</summary>
    public bool PixelShift { get; set; } = true;

    /// <summary>Calm UI: no parallax, tilt or launch animation (the UI also honors the system's reduced-motion setting).</summary>
    public bool ReduceMotion { get; set; }

    /// <summary>Pointer inside the HUD: "skin" (the skin's own), "system" (the real Windows pointer) or "mouse" (the comic mouse, once unlocked).</summary>
    public string Pointer { get; set; } = "skin";

    /// <summary>The comic mouse pointer was unlocked by the hidden code on the info page.</summary>
    public bool MouseUnlocked { get; set; }

    /// <summary>Settings view: false shows only everyday settings, true adds the expert sections.</summary>
    public bool ExpertMode { get; set; }

    /// <summary>
    /// Turns the Bluetooth radio back on (at start and whenever it goes off) while a Bluetooth controller is paired and no other
    /// controller is connected: switched off by accident, the controller would be gone and the couch out of reach.
    /// </summary>
    public bool BluetoothAutoOn { get; set; } = true;

    /// <summary>
    /// Paired Bluetooth controllers the last time Bluetooth was on (kept by the recovery, not a setting): with the radio off Windows
    /// may not list them, and after a restart with Bluetooth off this number still says a controller needs it.
    /// </summary>
    public int BluetoothPairedControllers { get; set; }

    /// <summary>Language of the interface: "auto" (Windows display language), "de" or "en".</summary>
    public string Language { get; set; } = "auto";

    /// <summary>
    /// How deep CouchHud reaches into Windows. "light": an ordinary full-screen app.
    /// "full": shell replacement, couch lock, automatic sign-in, Task Manager policy,
    /// paused services and the autostart takeover. Nothing of "full" happens in "light".
    /// </summary>
    public string IntegrationMode { get; set; } = "light";

    /// <summary>Light mode: close the taskbar (explorer) while CouchHud is in front, start it again when leaving.</summary>
    public bool CloseExplorerInLight { get; set; }

    /// <summary>Version of the disclaimer and privacy notice the user accepted (0 = not yet).</summary>
    public int AcceptedTerms { get; set; }

    /// <summary>
    /// Version of the first-run setup assistant the user went through or skipped (0 = not yet). An install that accepted the terms
    /// before the assistant existed is only offered it, never forced into it (the UI decides that from <see cref="AcceptedTerms"/>).
    /// </summary>
    public int SetupWizardDone { get; set; }

    /// <summary>Zoom of the couch browser in percent.</summary>
    public int BrowserZoom { get; set; } = 150;

    /// <summary>Where save game backups go; empty means the Backups folder in the data folder.</summary>
    public string BackupDir { get; set; } = "";

    /// <summary>How many backups to keep per game (older ones are deleted).</summary>
    public int BackupsToKeep { get; set; } = 5;

    /// <summary>Also include "config" paths (graphics settings, keybinds) in save backups.</summary>
    public bool BackupConfigFiles { get; set; }

    /// <summary>Back up a game's saves in the background after it ended (started by CouchHUD or seen running).</summary>
    public bool AutoBackupSaves { get; set; } = true;

    /// <summary>Automatic backups kept per game, apart from the ones made by hand (expert mode).</summary>
    public int AutoBackupsToKeep { get; set; } = 3;

    /// <summary>Saves of one game larger than this (decimal MB) are not backed up automatically (expert mode).</summary>
    public int AutoBackupMaxGameMb { get; set; } = 1000;

    /// <summary>All backups together may take this much space (decimal MB) before automatic backups stop (expert mode).</summary>
    public int AutoBackupMaxTotalMb { get; set; } = 10000;

    public List<Bookmark> Bookmarks { get; set; } =
    [
        new() { Name = "YouTube", Url = "https://www.youtube.com/" },
        new() { Name = "Twitch", Url = "https://www.twitch.tv/" },
        new() { Name = "Wikipedia", Url = "https://de.wikipedia.org/" },
        new() { Name = "DuckDuckGo", Url = "https://duckduckgo.com/" }
    ];

    /// <summary>Freeze the game while the HUD is in front.</summary>
    public bool PauseGameInMenu { get; set; }

    /// <summary>
    /// What leaving the couch needs: "off", "code" (controller passcode) or "windows"
    /// (the Windows lock screen, i.e. Windows Hello, PIN or password).
    /// </summary>
    public string CouchLock { get; set; } = "off";

    /// <summary>
    /// Parental controls (play time, time window, locked games). See Core.Parental. The rules in here are the default profile's
    /// (read them through <see cref="ActiveProfile"/>); the main switch is the household's (<see cref="ParentalEnabled"/>).
    /// </summary>
    public CouchHud.Core.Parental.ParentalConfig Parental { get; set; } = new();

    /// <summary>
    /// The age rating system the age limit and the age badge go by, for the whole household: "auto" (by the Windows region), "usk",
    /// "pegi" or "esrb". Anything else (an edited file) counts as the strictest rating of all systems (<see cref="CouchHud.Core.Parental.AgeRatings.Resolve"/>).
    /// </summary>
    public string AgeRatingSystem { get; set; } = CouchHud.Core.Parental.AgeRatings.Auto;

    /// <summary>
    /// The system <see cref="AgeRatingSystem"/> resolved to when the parents last saved it or an age limit ("usk", "pegi", "esrb",
    /// "strictest"; empty before). Under "auto" a later region with another system judges games by both, the stricter verdict wins
    /// (<see cref="CouchHud.Core.Parental.AgeRatings.JudgedSystems"/>).
    /// </summary>
    public string AgeRatingSystemResolved { get; set; } = "";

    /// <summary>The household-wide main switch of the parental controls (it is not per profile).</summary>
    [JsonIgnore]
    public bool ParentalEnabled
    {
        get => Parental.Enabled;
        set => Parental.Enabled = value;
    }

    /// <summary>
    /// Profiles besides the default one (whose data stays in the top-level fields, so an older CouchHUD sees it unchanged).
    /// Null while there are none: the file then looks as it always did.
    /// </summary>
    public List<UserProfile>? Profiles { get; set; }

    /// <summary>The profile on the couch; null means the default profile.</summary>
    public string? ActiveProfileId { get; set; }

    /// <summary>The profile on the couch. An unknown or broken one is the default profile (its rules apply, never none).</summary>
    [JsonIgnore]
    public ProfileView ActiveProfile => Profile(ActiveProfileId);

    /// <summary>
    /// A profile by id. Null, "default", an unknown or malformed id and a broken entry all give the default profile. With the same id
    /// twice, the first entry counts (a broken first one is not replaced by a later one).
    /// </summary>
    public ProfileView Profile(string? id)
    {
        if (id is null || id == ProfileIds.Default || !ProfileIds.IsExtra(id) || Profiles is null) return new ProfileView(this, null);
        var entry = Profiles.FirstOrDefault(p => p is not null && p.Id == id);
        return new ProfileView(this, ProfileCheck.Usable(entry) ? entry : null);
    }

    /// <summary>The default profile and every usable further one, each once.</summary>
    [JsonIgnore]
    public IReadOnlyList<ProfileView> AllProfiles
    {
        get
        {
            var all = new List<ProfileView> { new(this, null) };
            var seen = new HashSet<string> { ProfileIds.Default };
            foreach (var p in Profiles ?? [])
            {
                if (p is null || !seen.Add(p.Id ?? "")) continue;
                if (ProfileCheck.Usable(p)) all.Add(new ProfileView(this, p));
            }
            return all;
        }
    }

    /// <summary>
    /// Adds a profile with a new fixed id. It starts with a copy of the default profile's parental rules (so a new profile is never
    /// freer than the default one) and a fresh play time account. Settings follow the household's until changed.
    /// </summary>
    public ProfileView AddProfile(string name, string avatar = "", string color = "", bool restricted = true)
    {
        if (AllProfiles.Count >= MaxProfiles) throw new InvalidOperationException($"at most {MaxProfiles} profiles");
        string id;
        do id = ProfileIds.New();
        while (Profiles?.Any(p => p?.Id == id) == true);
        var entry = new UserProfile
        {
            Id = id,
            Name = name,
            Avatar = avatar,
            Color = color,
            Restricted = restricted,
            // A child's profile: the default profile's rules (never freer). An adult's: really without limits (entering it needs the parents'
            // code once there are several profiles and parental controls are on, ProfileSwitch).
            Data = new ProfileData { Rules = restricted ? ProfileCheck.CopyRules(Parental) : ProfileCheck.NoLimits(ProfileCheck.CopyRules(Parental)) }
        };
        (Profiles ??= []).Add(entry);
        return new ProfileView(this, entry);
    }

    /// <summary>The most profiles a household has, the default one included.</summary>
    public const int MaxProfiles = 8;

    /// <summary>Name of the default profile as the household calls it (null: the UI's "Standard").</summary>
    public string? ProfileName { get; set; }

    /// <summary>Avatar of the default profile (one of <see cref="ProfileLooks.Avatars"/>).</summary>
    public string? ProfileAvatar { get; set; }

    /// <summary>Color of the default profile (one of <see cref="ProfileLooks.Colors"/>).</summary>
    public string? ProfileColor { get; set; }

    /// <summary>The usable entry of a further profile, or null (the default profile, unknown, malformed or broken).</summary>
    private UserProfile? UsableEntry(string? id)
    {
        if (!ProfileIds.IsExtra(id) || Profiles is null) return null;
        var entry = Profiles.FirstOrDefault(p => p is not null && p.Id == id);
        return ProfileCheck.Usable(entry) ? entry : null;
    }

    /// <summary>
    /// Makes <paramref name="id"/> the profile on the couch. Only the default profile and usable further ones: anything else is refused
    /// (false) and the profile on the couch stays. Whether the switch is allowed at all is <see cref="ProfileSwitch"/>'s decision.
    /// </summary>
    public bool SwitchActiveProfile(string id)
    {
        if (id == ProfileIds.Default)
        {
            ActiveProfileId = null;
            return true;
        }
        if (UsableEntry(id) is null) return false;
        ActiveProfileId = id;
        return true;
    }

    /// <summary>Renames a profile (the default one too); the name must pass <see cref="ProfileNames.Accept"/>. False: no such profile, no name left or too long.</summary>
    public bool RenameProfile(string id, string? name)
    {
        var clean = ProfileNames.Accept(name);
        if (clean is null) return false;
        if (id == ProfileIds.Default)
        {
            ProfileName = clean;
            return true;
        }
        if (UsableEntry(id) is not { } entry) return false;
        entry.Name = clean;
        return true;
    }

    /// <summary>Avatar and color from the presets only. False: no such profile or a value that is not a preset.</summary>
    public bool SetProfileLook(string id, string avatar, string color)
    {
        if (!ProfileLooks.IsAvatar(avatar) || !ProfileLooks.IsColor(color)) return false;
        if (id == ProfileIds.Default)
        {
            ProfileAvatar = avatar;
            ProfileColor = color;
            return true;
        }
        if (UsableEntry(id) is not { } entry) return false;
        entry.Avatar = avatar;
        entry.Color = color;
        return true;
    }

    /// <summary>
    /// Makes a further profile a child's profile or not. The default profile never is one. A profile that stops being a child's loses its
    /// own code (only children have one).
    /// </summary>
    public bool SetProfileRestricted(string id, bool restricted)
    {
        if (UsableEntry(id) is not { } entry) return false;
        if (entry.Restricted == restricted) return true;
        entry.Restricted = restricted;
        // Either way the code goes (review rest 1): an adult has none, and a profile that becomes a child's again does not get back a
        // code nobody set for this child (an old hash planted while it was an adult's would otherwise come alive).
        entry.CodeHash = null;
        // An adult's profile has no limits; a profile that becomes a child's gets the default profile's rules (never freer). The play time
        // account and whether it was started stay.
        var rules = entry.Data!.Rules!;
        var next = restricted ? ProfileCheck.CopyRules(Parental) : ProfileCheck.NoLimits(rules);
        next.LedgerStarted = rules.LedgerStarted;
        next.TimeZoneId = rules.TimeZoneId;
        entry.Data.Rules = next;
        return true;
    }

    /// <summary>
    /// Sets (a hash from <see cref="CouchHud.Core.Security.Passcode.Hash"/>) or removes (null) a child's own code. Only a child's profile
    /// has one; anything that is not a hash in the stored format is refused.
    /// </summary>
    public bool SetProfileCode(string id, string? hash)
    {
        if (UsableEntry(id) is not { Restricted: true } entry) return false;
        if (hash is not null && !CouchHud.Core.Security.Passcode.IsLocalHash(hash)) return false;
        entry.CodeHash = hash;
        return true;
    }

    /// <summary>
    /// Removes a further profile with all its entries. Never the default profile and never the profile on the couch (switch first).
    /// The caller retires its play time account (<see cref="ProfileIds.RetireLedger"/>).
    /// </summary>
    public bool RemoveProfile(string id)
    {
        if (!ProfileIds.IsExtra(id) || Profiles is null) return false;
        if (ActiveProfileId == id || Profile(ActiveProfileId).Id == id) return false;
        if (Profiles.RemoveAll(p => p is not null && p.Id == id) == 0) return false;
        if (Profiles.Count == 0) Profiles = null;
        return true;
    }

    /// <summary>Salted hash of the controller passcode (Core.Security.Passcode).</summary>
    public string PasscodeHash { get; set; } = "";

    /// <summary>CouchHud set up the automatic Windows sign-in (never the password itself; Windows keeps it as LSA secret).</summary>
    public bool AutoLogonConfigured { get; set; }

    /// <summary>With the lock on: no Task Manager in couch mode, so nobody gets around it.</summary>
    public bool BlockTaskManager { get; set; } = true;

    /// <summary>Library item id → profile.</summary>
    public Dictionary<string, GameProfile> GameProfiles { get; set; } = new();

    /// <summary>"off", "on" or "evening" (19:00 to 7:00).</summary>
    public string NightMode { get; set; } = "off";

    /// <summary>0 (barely) to 100 (very warm).</summary>
    public int NightModeStrength { get; set; } = 50;

    /// <summary>Plugin folders the user turned off (applies at the next start).</summary>
    public List<string> DisabledPlugins { get; set; } = [];

    /// <summary>
    /// The owner's choice per plugin function: plugin id → function id → "admin", "restricted" or "off" (PluginFunctionRules). Missing: the
    /// plugin's suggestion applies (CouchHUD's own plugins may suggest restricted, every other plugin starts at admin). Unknown ids and values are
    /// ignored (fail closed), a plugin's entry goes when the plugin is uninstalled. Changed only through Hud.SetPluginFunctionLevel.
    /// </summary>
    public Dictionary<string, Dictionary<string, string>> PluginFunctionLevels { get; set; } = new();

    /// <summary>
    /// Feature areas switched off for this install (<see cref="FeatureAreas"/>): "games", "apps", "browser", "news".
    /// Empty or missing means everything is on; unknown names are ignored.
    /// </summary>
    public List<string> DisabledAreas { get; set; } = [];

    /// <summary>Plugin/skin catalog indexes (https). Several allowed; the first source wins for a duplicate id.</summary>
    /// <remarks>
    /// Since 0.6 only for developers: with the developer switch (<see cref="CatalogAllowLocal"/>) the entries besides the official one are
    /// read as well (local files, a test server). The official catalog is built in whatever this says, and never needs to be listed.
    /// Catalogs of other developers go into <see cref="ExtraCatalogSources"/>.
    /// </remarks>
    public List<string> CatalogSources { get; set; } = [CouchHud.Core.Plugins.CatalogDefaults.IndexUrl];

    /// <summary>
    /// Catalogs of other developers, added from the HUD itself (expert mode, the parents' code, never on the locked couch or in a child's
    /// profile, after the warning that the developer must be trusted). At most <see cref="CouchHud.Core.Plugins.CatalogSourceRules.MaxSources"/>.
    /// The official catalog is not in this list; it cannot be removed or switched off.
    /// </summary>
    public List<CouchHud.Core.Plugins.CatalogSourceEntry> ExtraCatalogSources { get; set; } = [];

    /// <summary>Check the catalogs in the background for updates of installed plugins and skins (never while a game runs).</summary>
    public bool CatalogUpdateCheck { get; set; } = true;

    /// <summary>Look on GitHub for a newer CouchHUD in the background (never while a game runs). Installing always needs a confirmation.</summary>
    public bool SelfUpdateCheck { get; set; } = true;

    // LEGACY (CaseHUD is a plugin now): CaseHudGamingLease is only read, once, for the plugin's first settings (PluginSettingSeeds). Nothing writes it any more.
    /// <summary>LEGACY, read only for the plugin's first settings. Tell CaseHUD while a game runs (its displays can switch to the gaming scene).</summary>
    public bool CaseHudGamingLease { get; set; } = true;

    /// <summary>The paired TV (TV control, Settings › TV). The pairing key inside is DPAPI-protected.</summary>
    public CouchHud.Core.Integrations.Tv.TvConfig Tv { get; set; } = new();

    // LEGACY (the VPN is a plugin now): VpnLastCountry and VpnInGuide are only read, once, for the plugin's first settings (PluginSettingSeeds). Nothing writes them any more.
    /// <summary>
    /// LEGACY, read only for the plugin's first settings. The VPN country chosen last in CouchHUD (an id of the provider's fixed list, e.g. "de"), offered again in the Guide menu.
    /// Only a remembered choice: anything that is not on the list is ignored when it is read.
    /// </summary>
    public string? VpnLastCountry { get; set; }

    /// <summary>LEGACY, read only for the plugin's first settings. Showed the VPN in the Guide menu.</summary>
    public bool VpnInGuide { get; set; } = true;

    /// <summary>
    /// Developer switch, only in config.json: also accept http://127.0.0.1 and file: catalogs and downloads
    /// (for testing a catalog without publishing it). Off by default; the hash check stays mandatory.
    /// </summary>
    public bool CatalogAllowLocal { get; set; }

    /// <summary>Watched game folders: every game in them shows up in the library automatically.</summary>
    public List<string> GameFolders { get; set; } = [];

    /// <summary>Autostart entry id → "couch" (at boot) or "pc" (only in PC mode). Missing means couch.</summary>
    public Dictionary<string, string> StartupModes { get; set; } = new();

    /// <summary>Web entries open the browser window maximized (the TV has no use for a small window).</summary>
    public bool MaximizeBrowser { get; set; } = true;

    /// <summary>The built-in browser keeps a list of recently visited sites on its start page; off keeps none (and clears it).</summary>
    public bool BrowserHistory { get; set; } = true;

    /// <summary>
    /// What the Favorites tab (formerly Home) shows: "favorites", "recent" or "combined" (favorites first, then the entries used last).
    /// Existing installs have no value and land on the combined view, which is what Home showed before.
    /// </summary>
    public string FavoritesMode { get; set; } = Library.FavoritesShelf.ModeCombined;

    /// <summary>Websites (bookmarks marked as favorite) show in the Favorites tab. Off by default: a website is added on purpose.</summary>
    public bool FavoritesSites { get; set; }

    /// <summary>End the PC-mode autostart programs again when returning to the couch.</summary>
    public bool ClosePcAppsOnReturn { get; set; }

    /// <summary>Services paused on the couch and started again in PC mode.</summary>
    public List<string> CouchServices { get; set; } = [];

    /// <summary>Services whose scheduled tasks exist (set up with administrator rights).</summary>
    public List<string> CouchServicesInstalled { get; set; } = [];

    /// <summary>Show Windows notifications (WhatsApp, Discord …) in CouchHud.</summary>
    public bool MirrorNotifications { get; set; }

    /// <summary>Phone remote (text input, d-pad) over the LAN; off unless the user turns it on.</summary>
    public bool RemoteEnabled { get; set; }

    public int RemotePort { get; set; } = 8766;

    public List<CouchHud.Core.Remote.RemoteDevice> RemoteDevices { get; set; } = [];

    /// <summary>"anthropic", "openai" or empty (assistant off).</summary>
    public string AiProvider { get; set; } = "";

    /// <summary>Encrypted with DPAPI, like all secrets here.</summary>
    public string AnthropicApiKey { get; set; } = "";

    public string OpenAiApiKey { get; set; } = "";

    /// <summary>Empty means the provider's default model.</summary>
    public string AiModel { get; set; } = "";

    /// <summary>MCP server for external AI clients; off unless the user turns it on.</summary>
    public bool McpEnabled { get; set; }

    /// <summary>Listen on all interfaces instead of localhost only.</summary>
    public bool McpLan { get; set; }

    public int McpPort { get; set; } = 8765;

    public string McpToken { get; set; } = "";

    public List<CustomEntry> Apps { get; set; } =
    [
        new() { Name = "YouTube", Type = "app", Url = "https://www.youtube.com/tv" }
    ];
}

/// <summary>Why config.json could not be used at start.</summary>
public enum ConfigFault
{
    /// <summary>The file exists but could not be read, even after several tries (virus scanner, sync tool, disk).</summary>
    Unreadable,

    /// <summary>The file is not valid JSON.</summary>
    Invalid,

    /// <summary>
    /// The file and every usable copy are gone, but CouchHUD was set up here before (<see cref="ConfigStore.EarlierInstallTraces"/>): that is no
    /// first start with open defaults and no setup assistant, but a locked one until the file is back or the folder was removed on purpose.
    /// </summary>
    Missing
}

/// <summary>
/// Loads and saves config.json. Never loses the user's file: writes are atomic and written through to the disk, the file before the
/// last save stays as config.json.bak, and edits made while CouchHud runs are picked up before the next save instead of being overwritten.
/// A broken file (empty, only zero bytes, invalid JSON, <c>null</c>) is replaced by a valid .bak, and is itself kept as
/// config.json.broken-&lt;time&gt;; it is never overwritten with defaults. Without a valid .bak it is left alone, and that does not mean
/// "no lock, no parental controls": the store then runs on a locked configuration (<see cref="FaultConfig"/>) and says so in <see cref="Fault"/>.
/// </summary>
public sealed class ConfigStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly object _lock = new();
    private readonly int _readAttempts;
    private readonly TimeSpan _retryDelay;
    private DateTime _lastSeenWrite;

    /// <param name="readAttempts">How often to try reading a file that exists but is locked by someone else (at boot a virus scanner or sync tool may hold it for a moment).</param>
    /// <param name="retryDelay">Pause between the tries; default 300 ms.</param>
    /// <param name="unreadableRetry">How often a file that could not be read is tried again in the background (default 15 s).</param>
    /// <param name="bootTime">When this boot started (tests); default from the tick count, see <see cref="_bootTime"/>.</param>
    public ConfigStore(string directory, int readAttempts = 5, TimeSpan? retryDelay = null, TimeSpan? unreadableRetry = null, Func<DateTime>? bootTime = null)
    {
        _bootTime = bootTime ?? WindowsBootTime;
        FilePath = System.IO.Path.Combine(directory, "config.json");
        _readAttempts = Math.Max(1, readAttempts);
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(300);
        Current = Load();
        // Temp files a cut-off save left behind were weighed by the restore above; what is older than a running save can be goes now.
        if (Fault is null) CleanLeftoverTemps(TimeSpan.FromMinutes(1));
        if (Fault is ConfigFault.Unreadable or ConfigFault.Missing)
        {
            // A virus scanner or sync tool may let go any moment: try again until the file can be read (or is invalid). A missing file
            // is looked for again the same way (put back from a copy of the user's).
            var every = unreadableRetry ?? UnreadableRetry;
            _retry = new Timer(_ => RetryUnreadable(), null, every, every);
        }
    }

    private Timer? _retry;

    private void RetryUnreadable()
    {
        lock (_lock)
        {
            if (Fault is not (ConfigFault.Unreadable or ConfigFault.Missing))
            {
                _retry?.Dispose();
                _retry = null;
                return;
            }
            TryRecover();
            if (Fault is null)
            {
                MergeRecoveryNotes(save: true);
                _retry?.Dispose();
                _retry = null;
            }
        }
    }

    public string FilePath { get; }

    public AppConfig Current { get; private set; }

    /// <summary>Set while the file could not be used: nothing is saved (the file stays as it is) and <see cref="Current"/> is <see cref="FaultConfig"/>.</summary>
    public ConfigFault? Fault { get; private set; }

    /// <summary>
    /// What runs when config.json is there but unusable: the couch is locked by Windows (the lock screen), with nothing else
    /// changed. As a shell that means a locked couch the owner can open; anywhere else the lock has no effect.
    /// </summary>
    public static AppConfig FaultConfig() => new()
    {
        CouchLock = "windows",
        // Parental controls strictly: every game needs the parents' code (an age limit nothing can meet, no rating counts
        // as unknown), and there is no code in this configuration, so no game starts until the file is fixed.
        Parental = new CouchHud.Core.Parental.ParentalConfig { Enabled = true, MaxAge = 6, BlockUnknownAge = true, CountApps = true },
        // No first start: the setup assistant and the notice would ask for a mode, a lock and codes on settings that are only a stand-in
        // (and could reset the lock through the service). The HUD shows the broken state instead. Never saved (see TrySave).
        AcceptedTerms = int.MaxValue,
        SetupWizardDone = int.MaxValue
    };

    /// <summary>How often an unreadable file is tried again in the background.</summary>
    public static readonly TimeSpan UnreadableRetry = TimeSpan.FromSeconds(15);

    /// <summary>The most copies of a broken config.json that are kept next to it.</summary>
    public const int MaxBadCopies = 5;

    /// <summary>Small side file with the recovery fields, written while the main file is unusable (see <see cref="RecoveryPath"/>).</summary>
    private sealed class RecoveryNotes
    {
        public string ShellRemovedReason { get; set; } = "";
        public bool PendingShellRestore { get; set; }
    }

    /// <summary>
    /// While config.json is unusable, the recovery fields (why the shell entry went, restore pending) cannot go into it.
    /// They are kept here and merged back once the file works again.
    /// </summary>
    public string RecoveryPath => FilePath + ".recovery";

    /// <summary>
    /// The copy of config.json. ReplaceFile leaves the file before the save there; right after the swap the new content is written to it as
    /// well, so a copy that is restored never brings back the state before a change that made things stricter (parental controls on, a
    /// lock, a code). Only a reset in the short window between the two steps leaves the older state, and then the temp file of the save
    /// (newer, see <see cref="BestCopy"/> and <see cref="AdoptNewerTemps"/>) wins.
    /// </summary>
    public string BackupPath => DurableFile.BackupOf(FilePath);

    /// <summary>
    /// Marker that config.json was restored: written by whichever CouchHUD process restored it (the HUD, the watcher, the sign-in lock),
    /// read and removed by the HUD for its notice (<see cref="TakeRestoredNotice"/>), so the notice is not lost when another process came first.
    /// </summary>
    public string RestoredMarkerPath => FilePath + ".restored";

    /// <summary>What the marker says: when, from which copy, and where the broken file was put (null when the file was only missing).</summary>
    public sealed record RestoredNotice(DateTimeOffset At, string? Source, string? Broken);

    /// <summary>The marker of a restore (null without one). The HUD reads it once at start and removes it with <see cref="ClearRestoredNotice"/> after the notice was shown.</summary>
    public RestoredNotice? ReadRestoredNotice()
    {
        try
        {
            if (!File.Exists(RestoredMarkerPath)) return null;
            try
            {
                if (JsonSerializer.Deserialize<RestoredNotice>(ReadShared(RestoredMarkerPath), JsonOptions) is { } notice) return notice;
            }
            catch (JsonException)
            {
                // A marker cut off by a reset still means "restored".
            }
            return new RestoredNotice(File.GetLastWriteTimeUtc(RestoredMarkerPath), null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[config] could not read the restore marker: {ex.Message}");
            return null;
        }
    }

    /// <summary>The notice was shown: the marker goes.</summary>
    public void ClearRestoredNotice() => TryDelete(RestoredMarkerPath);

    /// <summary>
    /// Reads the settings of <paramref name="directory"/> without any side effect, for code that runs with other rights than the user's (the
    /// uninstaller as an administrator) or only needs a value: it opens for reading only and never writes, deletes, renames or restores (a
    /// <see cref="ConfigStore"/> may do all of that in the user's folder, and an administrator doing it there could be pointed elsewhere by a
    /// junction or an oplock). The folder and the files must not be reparse points (junction, symbolic link). config.json first, its .bak
    /// when it is missing, blank or invalid; null when neither can be used.
    /// </summary>
    public static AppConfig? Peek(string directory)
    {
        try
        {
            if (IsLink(directory)) return null;
            var file = System.IO.Path.Combine(directory, "config.json");
            return PeekFile(file) ?? PeekFile(DurableFile.BackupOf(file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// The largest settings file <see cref="Peek"/> reads (4 MB; a real one is a few KB): an administrator's uninstaller must not be
    /// made to read gigabytes from a file the user can write (audit 0.5).
    /// </summary>
    public const int MaxPeekBytes = 4 * 1024 * 1024;

    private static AppConfig? PeekFile(string file)
    {
        if (!File.Exists(file) || IsLink(file)) return null;
        try
        {
            string text;
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length > MaxPeekBytes) return null;
                // The file Windows really opened must be this one in this folder: a link swapped in after the check above is not followed.
                if (!SavePathGuard.IsFinalPath(stream.SafeFileHandle, System.IO.Path.GetDirectoryName(file)!, file)) return null;
                // At most the cap, even if the file grows while it is read.
                var bytes = new byte[MaxPeekBytes + 1];
                var length = 0;
                int got;
                while (length < bytes.Length && (got = stream.Read(bytes, length, bytes.Length - length)) > 0) length += got;
                if (length > MaxPeekBytes) return null;
                using var reader = new StreamReader(new MemoryStream(bytes, 0, length), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                text = reader.ReadToEnd();
            }
            // Swapped for a link while it was opened: what was read is not taken.
            if (IsLink(file) || IsLink(System.IO.Path.GetDirectoryName(file)!)) return null;
            return ParseUsable(text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static bool IsLink(string path)
    {
        var attributes = File.GetAttributes(path);
        return attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    /// <summary>
    /// config.json was missing, empty, only zero bytes or invalid at start and the copy from before the last save (<see cref="BackupPath"/>)
    /// was taken instead; a broken file was put aside as <c>config.json.broken-&lt;time&gt;</c>. The HUD says so once.
    /// </summary>
    public bool RestoredFromBackup { get; private set; }

    private AppConfig Load()
    {
        var read = ReadFile(out var json, out var error);
        if (read == ReadResult.Missing)
        {
            // ReplaceFile renames the old file to .bak before the new one takes its place: a reset right in between leaves only the .bak.
            if (RestoreFromBackup(broken: false) is { } restored) return restored;
            if (MissingAfterEarlierInstall() is { } locked) return locked;
            var fresh = new AppConfig();
            TrySave(fresh);
            return fresh;
        }
        if (read == ReadResult.Failed)
        {
            Console.Error.WriteLine($"[config] could not read {FilePath} after {_readAttempts} tries, starting locked and leaving the file alone: {error}");
            Fault = ConfigFault.Unreadable;
            _lastSeenWrite = DateTime.MinValue;
            return FaultConfig();
        }

        try
        {
            var config = ParseUsable(json!);
            _lastSeenWrite = File.GetLastWriteTimeUtc(FilePath);
            config = AdoptNewerTemps(config);
            ApplyRecoveryNotes(config);
            return config;
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine(DurableFile.IsBlank(json)
                ? $"[config] {FilePath} is empty or only zero bytes (a save cut off by a reset or power loss?)"
                : $"[config] {FilePath} is invalid ({ex.Message})");
            // The copy from before the last save keeps the lock and the parental rules; the broken file is put aside, never overwritten.
            if (RestoreFromBackup(broken: true) is { } restored) return restored;
            // No usable copy: keep the user's broken file where it is and do not write defaults over it (the lock and the parental rules
            // are in there). The store runs locked until the file is repaired or removed.
            Console.Error.WriteLine($"[config] no usable backup ({BackupPath}); starting locked");
            KeepBadCopy(json!);
            Fault = ConfigFault.Invalid;
            _lastSeenWrite = File.GetLastWriteTimeUtc(FilePath);
            var locked = FaultConfig();
            if (ReadRecoveryNotes() is { } notes) Apply(locked, notes);
            return locked;
        }
    }

    /// <summary>
    /// config.json and every copy of it are gone, but <see cref="EarlierInstallTraces"/> finds that CouchHUD was set up here: the store runs
    /// locked (<see cref="ConfigFault.Missing"/>) and writes no defaults, so deleting the settings (or a reset that lost them) is no first start
    /// with an open couch, no parental controls and the setup assistant. Null when nothing points to an earlier install (a real first start).
    /// </summary>
    private AppConfig? MissingAfterEarlierInstall()
    {
        if (EarlierInstallTraces(System.IO.Path.GetDirectoryName(FilePath)!) is not { } trace) return null;
        Console.Error.WriteLine($"[config] {FilePath} and its backup are missing, but CouchHUD was set up here before ({System.IO.Path.GetFileName(trace)}); starting locked, not as a first start");
        Fault = ConfigFault.Missing;
        _lastSeenWrite = DateTime.MinValue;
        var locked = FaultConfig();
        if (ReadRecoveryNotes() is { } notes) Apply(locked, notes);
        return locked;
    }

    /// <summary>
    /// The first file in <paramref name="directory"/> that only an earlier run of CouchHUD with settings leaves behind: a copy of a broken
    /// config.json, its recovery or restore marker, a play time account (also its copy or a retired one), a code brake, the key of the signed
    /// files. Null when there is none: a new install, or a data folder removed on purpose. Files a start writes before the settings exist
    /// (the crash counter, the watcher's state) do not count. A file in the folder's place, or a folder that is itself a junction or a link
    /// (also one whose target is gone), counts as well (returns <paramref name="directory"/>): a new install leaves neither, and what such a
    /// link shows is whatever its maker points it at.
    /// </summary>
    public static string? EarlierInstallTraces(string directory)
    {
        string[] patterns = ["config.json.broken-*", "config.json.recovery", "config.json.restored", "playtime*.json*", "lockout*.json*", "parental.key"];
        try
        {
            FileAttributes attributes;
            try
            {
                // The entry itself, not what a link points to.
                attributes = File.GetAttributes(directory);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                Console.Error.WriteLine($"[config] {directory} is no plain folder ({attributes}): counted as an earlier install");
                return directory;
            }
            foreach (var pattern in patterns)
                if (Directory.EnumerateFiles(directory, pattern).FirstOrDefault() is { } found) return found;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder cannot be listed: that is no proof of a new install either.
            Console.Error.WriteLine($"[config] could not look for an earlier install in {directory}: {ex.Message}");
            return directory;
        }
        return null;
    }

    private void ApplyRecoveryNotes(AppConfig config)
    {
        if (ReadRecoveryNotes() is not { } notes) return;
        Apply(config, notes);
        Current = config;
        MergeRecoveryNotes(save: true);
    }

    /// <summary>
    /// Takes the newest valid copy (<see cref="BestCopy"/>): its content goes back in place of config.json (written through to the disk); a
    /// broken config.json (<paramref name="broken"/>) becomes <c>config.json.broken-&lt;time&gt;</c> in the same step. Null when there is no
    /// usable copy or it could not be put in place (then only what the other CouchHUD process restored meanwhile counts).
    /// </summary>
    private AppConfig? RestoreFromBackup(bool broken)
    {
        if (BestCopy() is not { } copy) return null;
        var (text, config, source) = copy;
        var temp = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        string? aside = null;
        try
        {
            DurableFile.WriteThrough(temp, System.Text.Encoding.UTF8.GetBytes(text));
            if (broken)
            {
                // The HUD and its watcher start together: if the other one restored it (or a save landed) since we read it, that file stays.
                if (UsableNow() is { } theirs)
                {
                    TryDelete(temp);
                    return theirs;
                }
                aside = BrokenName();
                // One step: the broken file becomes the .broken copy, the copy's content takes its place.
                File.Replace(temp, FilePath, aside, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, FilePath);
            }
            DurableFile.FlushRenames(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[config] could not put the backup in place: {ex.Message}");
            TryDelete(temp);
            return UsableNow();
        }
        Console.Error.WriteLine(aside is null
            ? $"[config] {FilePath} was missing, restored from {source}"
            : $"[config] {FilePath} restored from {source}, the broken file is kept as {aside}");
        RestoredFromBackup = true;
        _lastSeenWrite = File.GetLastWriteTimeUtc(FilePath);
        WriteRestoredMarker(source, aside);
        if (aside is not null) PruneBadCopies();
        // The copy is the file now: it becomes the .bak as well (a temp file that won must not lose against an older .bak next time).
        if (!string.Equals(source, BackupPath, StringComparison.OrdinalIgnoreCase)) RefreshBackup(System.Text.Encoding.UTF8.GetBytes(text));
        ApplyRecoveryNotes(config);
        return config;
    }

    /// <summary>config.json as it is now, when it is usable (another process restored or saved it); null otherwise.</summary>
    private AppConfig? UsableNow()
    {
        if (ReadFile(out var now, out _) != ReadResult.Ok) return null;
        try
        {
            var config = ParseUsable(now!);
            _lastSeenWrite = File.GetLastWriteTimeUtc(FilePath);
            return config;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The newest valid copy: the .bak, or a temp file a cut-off save left behind (<c>config.json.*.tmp</c>) when it is newer than the
    /// .bak. A reset between the two renames of ReplaceFile leaves no config.json, the old state in the .bak and the new one in the temp file.
    /// </summary>
    private (string Text, AppConfig Config, string Source)? BestCopy()
    {
        (string Text, AppConfig Config, string Source, DateTime Time)? best = null;
        foreach (var path in LeftoverTemps().Prepend(BackupPath))
        {
            try
            {
                if (!File.Exists(path)) continue;
                var time = File.GetLastWriteTimeUtc(path);
                if (best is { } b && time <= b.Time) continue;
                var text = ReadShared(path);
                best = (text, ParseUsable(text), path, time);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // A temp file still being written by the other process, or one cut off: not a copy to restore.
                if (path == BackupPath) Console.Error.WriteLine($"[config] the backup {BackupPath} cannot be used either: {ex.Message}");
            }
        }
        return best is { } found ? (found.Text, found.Config, found.Source) : null;
    }

    /// <summary>Temp files of saves next to config.json (also those of the .bak, <c>config.json.bak.*.tmp</c>).</summary>
    private IEnumerable<string> LeftoverTemps()
    {
        try
        {
            return Directory.GetFiles(System.IO.Path.GetDirectoryName(FilePath)!, System.IO.Path.GetFileName(FilePath) + ".*.tmp");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Removes temp files older than <paramref name="age"/> (younger ones may belong to a save of the other process right now). A complete
    /// one from before this boot that is newer than the file it was meant to replace stays (<see cref="NewerValidTemp"/>): it is a save whose
    /// rename the reset lost, and it is taken at the next start if taking it failed this time. One dated in the future is never taken
    /// (<see cref="NewerValidTemp"/>) and goes. Also the old files a swap had to put aside while a reader held the .bak (<c>….bak.*.old</c>).
    /// </summary>
    private void CleanLeftoverTemps(TimeSpan age)
    {
        var now = DateTime.UtcNow;
        foreach (var temp in LeftoverTemps())
        {
            try
            {
                var time = File.GetLastWriteTimeUtc(temp);
                if (time <= now && now - time <= age) continue;
                if (time <= now && NewerValidTemp(temp) is not null)
                {
                    Console.Error.WriteLine($"[config] keeping {temp}: it is newer than the file it was meant to replace");
                    continue;
                }
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Next start.
            }
        }
        // Without config.json the old file may only exist under that name (ReplaceFile stopped half way): it stays then.
        if (!File.Exists(FilePath)) return;
        try
        {
            foreach (var old in Directory.GetFiles(System.IO.Path.GetDirectoryName(FilePath)!, System.IO.Path.GetFileName(BackupPath) + ".*.old"))
                if (now - SafeWriteTime(old) > age) TryDelete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Next start.
        }
    }

    /// <summary>The file a leftover temp file was meant to replace: the .bak for <c>config.json.bak.*.tmp</c>, else config.json.</summary>
    private string TargetOf(string temp) =>
        System.IO.Path.GetFileName(temp).StartsWith(System.IO.Path.GetFileName(BackupPath) + ".", StringComparison.OrdinalIgnoreCase) ? BackupPath : FilePath;

    /// <summary>
    /// When this Windows session started (a little earlier, for the rounding between the two clocks). A rename is only lost to a reset,
    /// so only a temp file from before it can be such a save; one from this boot may belong to a save running right now in another
    /// CouchHUD process (between writing and renaming), and taking it could lay an older state over one saved since.
    /// </summary>
    private readonly Func<DateTime> _bootTime;

    private static DateTime WindowsBootTime() => DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64) - TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// The text of <paramref name="temp"/> when it is complete (usable settings), from before this boot (<see cref="_bootTime"/>), not dated in
    /// the future (clock drift: it would win at every start and roll back every later save) and newer than the file it was meant to replace;
    /// null otherwise. Such a file is a finished save whose rename was lost to a reset. A save cut off while writing is not valid JSON and
    /// does not count. Only the user writes in this folder, and the user can change config.json directly anyway: taking a temp file opens
    /// nothing new.
    /// </summary>
    private string? NewerValidTemp(string temp)
    {
        try
        {
            var time = File.GetLastWriteTimeUtc(temp);
            if (time > DateTime.UtcNow || time >= _bootTime()) return null;
            var target = TargetOf(temp);
            if (File.Exists(target) && time <= File.GetLastWriteTimeUtc(target)) return null;
            var text = ReadShared(temp);
            ParseUsable(text);
            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The newest of the leftover temp files for <paramref name="target"/> that <see cref="NewerValidTemp"/> takes, with its name and the time
    /// of the target it was weighed against (<see cref="DateTime.MinValue"/> when there is none).
    /// </summary>
    private (string Path, string Text, DateTime TargetTime)? NewestValidTemp(string target)
    {
        var targetTime = File.Exists(target) ? SafeWriteTime(target) : DateTime.MinValue;
        foreach (var temp in LeftoverTemps().Where(t => TargetOf(t) == target).OrderByDescending(SafeWriteTime))
            if (NewerValidTemp(temp) is { } text) return (temp, text, targetTime);
        return null;
    }

    /// <summary>Right before the swap: the temp file is still there and nobody saved the target since it was weighed.</summary>
    private static bool StillTakes((string Path, string Text, DateTime TargetTime) found, string target) =>
        File.Exists(found.Path) && (File.Exists(target) ? SafeWriteTime(target) : DateTime.MinValue) == found.TargetTime;

    private static DateTime SafeWriteTime(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// config.json is usable, but a reset may have lost the rename of the last save: then the file is valid but old, and the complete temp
    /// file of that save (newer, from before this boot) lies next to it. That one wins (a stricter rule saved right before the reset must
    /// not get lost) and is put in place, then removed; the same for the .bak and its temp file. Returns the settings that count.
    /// </summary>
    private AppConfig AdoptNewerTemps(AppConfig config)
    {
        if (NewestValidTemp(FilePath) is { } newer)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(newer.Text);
            var temp = $"{FilePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                DurableFile.WriteThrough(temp, bytes);
                if (!StillTakes(newer, FilePath))
                {
                    // Saved meanwhile by another CouchHUD process: that state is newer than the temp file and stays.
                    TryDelete(temp);
                    return config;
                }
                config = ParseUsable(newer.Text);
                MoveOver(temp, FilePath);
                _lastSeenWrite = File.GetLastWriteTimeUtc(FilePath);
                Console.Error.WriteLine($"[config] {FilePath} was older than the save {newer.Path} a reset left behind; that save is in place now");
                TryDelete(newer.Path);
                RefreshBackup(bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The newer settings count all the same; the temp file stays (see CleanLeftoverTemps) and the next save writes them.
                Console.Error.WriteLine($"[config] using the newer save {newer.Path}, but could not put it in place: {ex.Message}");
                TryDelete(temp);
            }
        }
        if (NewestValidTemp(BackupPath) is { } backup && StillTakes(backup, BackupPath))
        {
            Console.Error.WriteLine($"[config] {BackupPath} was older than the copy {backup.Path} a reset left behind; taking that one");
            if (RefreshBackup(System.Text.Encoding.UTF8.GetBytes(backup.Text))) TryDelete(backup.Path);
        }
        return config;
    }

    private void WriteRestoredMarker(string source, string? aside)
    {
        try
        {
            var notice = new RestoredNotice(DateTimeOffset.Now, System.IO.Path.GetFileName(source), aside is null ? null : System.IO.Path.GetFileName(aside));
            DurableFile.WriteThrough(RestoredMarkerPath, System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(notice, JsonOptions)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[config] could not write the restore marker: {ex.Message}");
        }
    }

    /// <summary>Writes <paramref name="bytes"/> (what config.json holds now) through to the .bak, so the copy is never older than the file.</summary>
    private bool RefreshBackup(byte[] bytes) => DurableFile.RefreshBackup(FilePath, bytes);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leftover temp file: harmless, cleaned at a later start.
        }
    }

    /// <summary>
    /// Keeps only the newest <see cref="MaxBadCopies"/> copies of broken files: <c>config.json.broken-*</c> and the <c>config.json.*.bad</c>
    /// of older versions together.
    /// </summary>
    private void PruneBadCopies()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(FilePath)!;
            var name = System.IO.Path.GetFileName(FilePath);
            var all = Directory.GetFiles(dir, name + ".broken-*").Concat(Directory.GetFiles(dir, name + ".*.bad"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(File.GetLastWriteTimeUtc)
                .ToList();
            foreach (var old in all.Take(Math.Max(0, all.Count - MaxBadCopies))) TryDelete(old);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Stays for now.
        }
    }

    /// <summary>A new name for a broken copy: <c>config.json.broken-&lt;time&gt;</c>, never one that exists.</summary>
    private string BrokenName()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
        var name = $"{FilePath}.broken-{stamp}";
        for (var n = 2; File.Exists(name); n++) name = $"{FilePath}.broken-{stamp}-{n}";
        return name;
    }

    /// <summary>
    /// <see cref="Parse"/> for a file that must hold settings: an empty file, one of zero bytes or the JSON <c>null</c> is broken, never
    /// "all defaults" (that would be no lock and no parental controls).
    /// </summary>
    internal static AppConfig ParseUsable(string json)
    {
        if (DurableFile.IsBlank(json)) throw new JsonException("the file is empty");
        return Parse(json) ?? throw new JsonException("the file holds no settings");
    }

    /// <summary>
    /// A copy of the broken file next to it, so the user can repair it. The same content is never copied twice (every start
    /// would otherwise add one) and only the newest <see cref="MaxBadCopies"/> copies stay.
    /// </summary>
    private void KeepBadCopy(string json)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(FilePath)!;
            var pattern = System.IO.Path.GetFileName(FilePath) + ".broken-*";
            foreach (var f in Directory.GetFiles(dir, pattern))
            {
                try
                {
                    if (File.ReadAllText(f) == json) return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Cannot compare with this one: look at the next.
                }
            }
            var backup = BrokenName();
            DurableFile.WriteThrough(backup, System.Text.Encoding.UTF8.GetBytes(json));
            Console.Error.WriteLine($"[config] backed up to {backup}");
            PruneBadCopies();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[config] backup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads the JSON and mends what older versions left open: a phone paired before the use time was kept has no last use, so
    /// it counts as used now (otherwise its token would be judged by the day it was paired and expire at once).
    /// </summary>
    internal static AppConfig? Parse(string json)
    {
        var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
        if (config is null) return null;
        var now = DateTimeOffset.UtcNow;
        foreach (var device in config.RemoteDevices ?? []) device.LastUsed ??= now;
        MigrateSaveLimits(config, json);
        MigrateSkinIds(config);
        MigrateAgeRatingSystem(config, json);
        MigrateFavorites(config);
        if (!HasProperty(json, nameof(AppConfig.PluginTrustAdopted))) config.PluginTrustAdopted = false;
        return config;
    }

    /// <summary>The skin of the household and the one each profile has set for itself (null where it follows the household).</summary>
    public static IEnumerable<string?> AskedSkins(AppConfig config) => (config.Profiles ?? []).Select(p => p?.Data?.Settings?.Skin).Append(config.Skin);

    /// <summary>
    /// Home became the Favorites tab (0.6): an unknown mode counts as the combined view, and every favorites list is cleaned (valid ids,
    /// no duplicates; a long list is not cut). Favorites and the collections pinned to Home stay as they were; nothing else was pinned on Home.
    /// </summary>
    internal static void MigrateFavorites(AppConfig config)
    {
        config.FavoritesMode = Library.FavoritesShelf.NormalizeMode(config.FavoritesMode);
        if (config.Favorites is not null) config.Favorites = Library.FavoritesShelf.Clean(config.Favorites);
        foreach (var profile in config.Profiles ?? [])
        {
            if (profile?.Data is { Favorites: not null } data) data.Favorites = Library.FavoritesShelf.Clean(data.Favorites);
        }
    }

    /// <summary>
    /// Skins renamed in 0.5 and moved to the catalog in 0.6 (<see cref="Skins.SkinCatalog.MigrateSkinId"/>, <see cref="Skins.SkinCatalog.MigrateId"/>): the skin and sound set of the household and of every profile
    /// move to the new id. The next save writes the new ids; until then every read comes to the same result.
    /// </summary>
    internal static void MigrateSkinIds(AppConfig config)
    {
        config.Skin = Skins.SkinCatalog.MigrateSkinId(config.Skin)!;
        config.SoundSet = Skins.SkinCatalog.MigrateId(config.SoundSet)!;
        foreach (var profile in config.Profiles ?? [])
        {
            if (profile?.Data?.Settings is not { } settings) continue;
            settings.Skin = Skins.SkinCatalog.MigrateSkinId(settings.Skin);
            settings.SoundSet = Skins.SkinCatalog.MigrateId(settings.SoundSet);
        }
    }

    /// <summary>
    /// Once: limits from a file without <see cref="AppConfig.SaveLimitsDecimal"/> were binary MB and go up to the next offered decimal
    /// limit (SaveGameService.FromBinaryMb), so a game that was backed up before still is. The marker is set with it, and the next save
    /// writes both; until then every read comes to the same result.
    /// </summary>
    internal static void MigrateSaveLimits(AppConfig config, string json)
    {
        if (HasProperty(json, nameof(AppConfig.SaveLimitsDecimal))) return;
        config.AutoBackupMaxGameMb = Saves.SaveGameService.FromBinaryMb(config.AutoBackupMaxGameMb, Saves.SaveGameService.GamePresetsMb,
            Saves.SaveGameService.MinGameMb, Saves.SaveGameService.MaxGameMb);
        config.AutoBackupMaxTotalMb = Saves.SaveGameService.FromBinaryMb(config.AutoBackupMaxTotalMb, Saves.SaveGameService.TotalPresetsMb,
            Saves.SaveGameService.MinTotalMb, Saves.SaveGameService.MaxTotalMb);
        config.SaveLimitsDecimal = true;
    }

    /// <summary>
    /// Once: a file from before the rating systems with an age limit set went by the USK, so it keeps the USK (and does not become
    /// looser by the Windows region). Without a limit nothing changes ("auto").
    /// </summary>
    internal static void MigrateAgeRatingSystem(AppConfig config, string json)
    {
        // Exactly the key the case-sensitive deserializer reads: a differently cased one was ignored, so the file has no system yet.
        if (HasProperty(json, "ageRatingSystem", StringComparison.Ordinal)) return;
        if (!config.AllProfiles.Any(p => p.Rules.MaxAge > 0)) return;
        config.AgeRatingSystem = CouchHud.Core.Parental.AgeRatings.Usk;
        config.AgeRatingSystemResolved = CouchHud.Core.Parental.AgeRatings.Usk;
    }

    private static bool HasProperty(string json, string name, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, name, comparison)) return true;
            }
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private RecoveryNotes? ReadRecoveryNotes()
    {
        try
        {
            return File.Exists(RecoveryPath) ? JsonSerializer.Deserialize<RecoveryNotes>(File.ReadAllText(RecoveryPath), JsonOptions) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void Apply(AppConfig config, RecoveryNotes notes)
    {
        if (notes.ShellRemovedReason.Length > 0) config.ShellRemovedReason = notes.ShellRemovedReason;
        config.PendingShellRestore = notes.PendingShellRestore;
    }

    /// <summary>The file works again: the recovery fields kept on the side go into it (and the side file goes away).</summary>
    private void MergeRecoveryNotes(bool save)
    {
        if (ReadRecoveryNotes() is not { } notes) return;
        Apply(Current, notes);
        if (save) TrySave(Current);
        try
        {
            File.Delete(RecoveryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Merged next time again, harmless.
        }
    }

    private void WriteRecoveryNotes()
    {
        try
        {
            var notes = new RecoveryNotes { ShellRemovedReason = Current.ShellRemovedReason, PendingShellRestore = Current.PendingShellRestore };
            if (notes.ShellRemovedReason.Length == 0 && !notes.PendingShellRestore) File.Delete(RecoveryPath);
            else File.WriteAllText(RecoveryPath, JsonSerializer.Serialize(notes, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[config] could not save the recovery notes: {ex.Message}");
        }
    }

    private enum ReadResult
    {
        Ok,
        Missing,
        Failed
    }

    private ReadResult ReadFile(out string? json, out string? error)
    {
        json = null;
        error = null;
        for (var attempt = 1; attempt <= _readAttempts; attempt++)
        {
            try
            {
                json = ReadShared(FilePath);
                return ReadResult.Ok;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return ReadResult.Missing;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error = ex.Message;
                if (attempt < _readAttempts) Thread.Sleep(_retryDelay);
            }
        }
        return ReadResult.Failed;
    }

    /// <summary>
    /// Changes the configuration and saves it. Saving is tried several times (see <see cref="SaveAttempts"/>): another process or a
    /// virus scanner may hold the file for a moment. Returns false when it could not be saved: a file that could not be written keeps
    /// the change in memory, while an unusable file (<see cref="Fault"/> set) refuses it, the stand-in stays as it is and only the
    /// recovery fields are taken (see <see cref="RefuseWhileFaulted"/>). Callers that must not claim success then use
    /// <see cref="UpdateOrThrow"/>; callers that change state outside the file (the service's codes and mode) refuse up front while
    /// <see cref="Fault"/> is set, so the two cannot drift apart.
    /// </summary>
    public bool Update(Action<AppConfig> change)
    {
        lock (_lock)
        {
            if (Fault is not null) TryRecover();
            else ReloadIfChangedOnDisk();
            if (Fault is null) MergeRecoveryNotes(save: false);
            else return RefuseWhileFaulted(change);
            change(Current);
            return TrySave(Current);
        }
    }

    /// <summary>
    /// A change while the file is unusable: nothing is saved, and the stand-in (<see cref="FaultConfig"/>) stays as it is in memory as well (a
    /// change applied to it before the refusal would open in this run what it locks, e.g. switch the couch lock off). The change runs on a
    /// copy; only the few recovery fields are taken from it, into the stand-in and the side file. Called under the lock.
    /// </summary>
    private bool RefuseWhileFaulted(Action<AppConfig> change)
    {
        var scratch = Parse(JsonSerializer.Serialize(Current, JsonOptions)) ?? FaultConfig();
        change(scratch);
        Current.ShellRemovedReason = scratch.ShellRemovedReason;
        Current.PendingShellRestore = scratch.PendingShellRestore;
        WriteRecoveryNotes();
        LastSaveError = FilePath + " is unusable";
        return false;
    }

    /// <summary>Like <see cref="Update"/>, but a change that could not be saved throws: a step that reports "done" must have saved.</summary>
    public void UpdateOrThrow(Action<AppConfig> change)
    {
        if (Update(change)) return;
        // While the file is unusable nothing is saved on purpose: that is not a file held by another program.
        throw new IOException(Fault is not null ? Strings.Get("config.notSavedFault") : Strings.Get("config.notSaved", LastSaveError ?? FilePath));
    }

    /// <summary>Why the last save failed (null after a save that worked).</summary>
    public string? LastSaveError { get; private set; }

    /// <summary>How often a save is tried before it counts as failed, and the pause between the tries (grows with every one).</summary>
    internal int SaveAttempts { get; set; } = 8;

    internal TimeSpan SavePause { get; set; } = TimeSpan.FromMilliseconds(60);

    /// <summary>Replaces the target with the finished temp file (a hook for tests; the real one is a move over the file).</summary>
    internal Action<string, string> MoveOver { get; set; } = SwapIn;

    /// <summary>
    /// ReplaceFile when the target exists: unlike a move over the file it succeeds while a reader has the file open with the right to
    /// delete it (our own reads do, see <see cref="ReadShared"/>); a move over an open file is "access denied" whatever is shared.
    /// The file it replaces stays as <see cref="BackupPath"/>: the copy <see cref="Load"/> takes when a reset leaves config.json broken.
    /// </summary>
    private static void SwapIn(string temp, string target) => DurableFile.SwapIn(temp, target, DurableFile.BackupOf(target));

    internal Action<TimeSpan> Pause { get; set; } = Thread.Sleep;

    /// <summary>The file was repaired (or removed) while we ran locked: take it up again.</summary>
    private void TryRecover()
    {
        var read = ReadFile(out var json, out _);
        if (read == ReadResult.Missing)
        {
            // A usable copy from before is taken first (a broken file is only ever kept without one). Without one, removing the file is no
            // way out of the locked state while traces of the earlier install are there (the copy of the broken file among them): to start
            // over, the data folder goes as a whole.
            if (RestoreFromBackup(broken: false) is { } restored) Current = restored;
            else if (EarlierInstallTraces(System.IO.Path.GetDirectoryName(FilePath)!) is not null)
            {
                Fault = ConfigFault.Missing;
                return;
            }
            else Current = new AppConfig();
            Fault = null;
            return;
        }
        if (read != ReadResult.Ok) return;
        try
        {
            Current = ParseUsable(json!);
            _lastSeenWrite = File.GetLastWriteTimeUtc(FilePath);
            Fault = null;
            Console.WriteLine($"[config] {FilePath} is usable again");
        }
        catch (JsonException)
        {
            // Still broken: stay locked.
        }
    }

    private void ReloadIfChangedOnDisk()
    {
        try
        {
            if (!File.Exists(FilePath) || File.GetLastWriteTimeUtc(FilePath) <= _lastSeenWrite) return;
            var json = ReadShared(FilePath);
            Current = ParseUsable(json);
            _lastSeenWrite = File.GetLastWriteTimeUtc(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Half-written or broken edit: keep what we have, Load's backup logic runs next start.
            Console.Error.WriteLine($"[config] ignoring external change: {ex.Message}");
        }
    }

    private bool TrySave(AppConfig config)
    {
        // A file we could not use is not ours to replace with defaults.
        if (Fault is not null)
        {
            LastSaveError = FilePath + " is unusable";
            return false;
        }
        // One temp file per process: the HUD and the guard's recovery may save at the same moment.
        var temp = $"{FilePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            var json = JsonSerializer.Serialize(config, JsonOptions);
            // Write next to the target and swap, so a power cut never leaves half a file. The swap is "access denied" while another
            // process has the file open without sharing the right to replace it (a reader of the other CouchHud process, a virus
            // scanner, the indexer): that passes in a moment, so it is tried again instead of being lost.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    // Written through to the disk before it replaces the file: a reset right after must not leave zero bytes behind.
                    var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                    DurableFile.WriteThrough(temp, bytes);
                    MoveOver(temp, FilePath);
                    _lastSeenWrite = File.GetLastWriteTimeUtc(FilePath);
                    LastSaveError = null;
                    // The .bak holds the state before this save now; it gets the new one too, so a restore never undoes a stricter rule.
                    RefreshBackup(bytes);
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LastSaveError = ex.Message;
                    if (attempt >= Math.Max(1, SaveAttempts)) throw;
                    Pause(TimeSpan.FromTicks(SavePause.Ticks * attempt));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not being able to save must never take the shell down.
            LastSaveError = ex.Message;
            Console.Error.WriteLine($"[config] could not save {FilePath} after {Math.Max(1, SaveAttempts)} tries: {ex.Message}");
            try
            {
                // Without config.json (a swap stopped half way) the temp file is the newest state there is: it stays for the restore at start.
                if (File.Exists(FilePath)) File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // Leftover temp file: harmless.
            }
            return false;
        }
    }

    /// <summary>
    /// Reads a file without keeping others from replacing it: <c>File.ReadAllText</c> shares only reading, so a replace
    /// (rename over the file) by the other CouchHud process fails with "access denied" while we read.
    /// </summary>
    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
