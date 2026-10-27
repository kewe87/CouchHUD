using CouchHud.Core.Localization;
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

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => Name is null && Type is null && !Hidden && Cover is null && Hero is null && Logo is null;
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

    public Dictionary<string, ItemOverride> Overrides { get; set; } = new();

    public List<ItemCollection> Collections { get; set; } = [];

    /// <summary>Place for the weather in the header; null hides it.</summary>
    public CouchHud.Core.Integrations.Place? WeatherLocation { get; set; }

    /// <summary>e.g. http://homeassistant.local:8123</summary>
    public string HomeAssistantUrl { get; set; } = "";

    /// <summary>Long-lived access token, encrypted for the Windows user (DPAPI).</summary>
    public string HomeAssistantToken { get; set; } = "";

    /// <summary>RSS/Atom feeds for the News tab.</summary>
    public List<CouchHud.Core.Integrations.NewsFeed> NewsFeeds { get; set; } =
        CouchHud.Core.Integrations.NewsService.Defaults.Select(f => new CouchHud.Core.Integrations.NewsFeed { Name = f.Name, Url = f.Url }).ToList();

    /// <summary>Entities shown in the guide menu for quick access.</summary>
    public List<string> HomeAssistantPinned { get; set; } = [];

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

    /// <summary>
    /// CouchHud is in charge on the couch (full integration only): game clients and known pop-ups do not get
    /// the foreground, the Game Bar does not get the Xbox button. Everything is undone in PC mode and on quit.
    /// </summary>
    public bool SuppressCompetitors { get; set; } = true;

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

    /// <summary>Parental controls (play time, time window, locked games). See Core.Parental.</summary>
    public CouchHud.Core.Parental.ParentalConfig Parental { get; set; } = new();

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

    /// <summary>Plugin/skin catalog indexes (https). Several allowed; the first source wins for a duplicate id.</summary>
    public List<string> CatalogSources { get; set; } = [CouchHud.Core.Plugins.CatalogDefaults.IndexUrl];

    /// <summary>Check the catalogs in the background for updates of installed plugins and skins (never while a game runs).</summary>
    public bool CatalogUpdateCheck { get; set; } = true;

    /// <summary>Look on GitHub for a newer CouchHUD in the background (never while a game runs). Installing always needs a confirmation.</summary>
    public bool SelfUpdateCheck { get; set; } = true;

    /// <summary>Install CaseHUD with its autostart entry (setup task "autostart").</summary>
    public bool CaseHudInstallAutostart { get; set; } = true;

    /// <summary>Install CaseHUD with the FPS task: the user joins "Performance Log Users" (setup task "fps"; takes effect after the next sign-in).</summary>
    public bool CaseHudInstallFps { get; set; }

    /// <summary>
    /// After CouchHUD installed CaseHUD for the first time (full integration only): switch CaseHUD's API on for this user
    /// (<c>CaseHUD.exe --set-api=on</c> in the user's session). CaseHUD's own default is off; the choice is shown on the install page and
    /// named in the confirmation, so it stays the user's decision. An existing installation is never switched without a press.
    /// </summary>
    public bool CaseHudInstallApi { get; set; } = true;

    /// <summary>Tell CaseHUD while a game runs (its displays can switch to the gaming scene).</summary>
    public bool CaseHudGamingLease { get; set; } = true;

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
    Invalid
}

/// <summary>
/// Loads and saves config.json. Never loses the user's file: a broken file is
/// backed up and left alone (defaults are never written over it), writes are atomic, and edits made while
/// CouchHud runs are picked up before the next save instead of being overwritten.
/// A file that exists but cannot be used does not mean "no lock, no parental controls": the store then
/// runs on a locked configuration (<see cref="FaultConfig"/>) and says so in <see cref="Fault"/>.
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
    public ConfigStore(string directory, int readAttempts = 5, TimeSpan? retryDelay = null, TimeSpan? unreadableRetry = null)
    {
        FilePath = System.IO.Path.Combine(directory, "config.json");
        _readAttempts = Math.Max(1, readAttempts);
        _retryDelay = retryDelay ?? TimeSpan.FromMilliseconds(300);
        Current = Load();
        if (Fault == ConfigFault.Unreadable)
        {
            // A virus scanner or sync tool may let go any moment: try again until the file can be read (or is invalid).
            var every = unreadableRetry ?? UnreadableRetry;
            _retry = new Timer(_ => RetryUnreadable(), null, every, every);
        }
    }

    private Timer? _retry;

    private void RetryUnreadable()
    {
        lock (_lock)
        {
            if (Fault != ConfigFault.Unreadable)
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
        Parental = new CouchHud.Core.Parental.ParentalConfig { Enabled = true, MaxAge = 6, BlockUnknownAge = true, CountApps = true }
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

    private AppConfig Load()
    {
        var read = ReadFile(out var json, out var error);
        if (read == ReadResult.Missing)
        {
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
            var config = Parse(json!) ?? new AppConfig();
            _lastSeenWrite = File.GetLastWriteTimeUtc(FilePath);
            if (ReadRecoveryNotes() is { } notes)
            {
                Apply(config, notes);
                Current = config;
                MergeRecoveryNotes(save: true);
            }
            return config;
        }
        catch (JsonException ex)
        {
            // Keep the user's broken file around and do not write defaults over it: the lock and the parental rules are in there.
            Console.Error.WriteLine($"[config] {FilePath} is invalid ({ex.Message}); starting locked");
            KeepBadCopy(json!);
            Fault = ConfigFault.Invalid;
            _lastSeenWrite = File.GetLastWriteTimeUtc(FilePath);
            var locked = FaultConfig();
            if (ReadRecoveryNotes() is { } notes) Apply(locked, notes);
            return locked;
        }
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
            var pattern = System.IO.Path.GetFileName(FilePath) + ".*.bad";
            var existing = Directory.GetFiles(dir, pattern).OrderBy(File.GetLastWriteTimeUtc).ToList();
            foreach (var f in existing)
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
            var backup = $"{FilePath}.{DateTime.Now:yyyyMMdd-HHmmss-fff}.bad";
            File.WriteAllText(backup, json);
            Console.Error.WriteLine($"[config] backed up to {backup}");
            existing.Add(backup);
            foreach (var old in existing.Take(Math.Max(0, existing.Count - MaxBadCopies)))
            {
                try
                {
                    File.Delete(old);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Stays for now.
                }
            }
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
        return config;
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
    /// virus scanner may hold the file for a moment. Returns false when it could not be saved (the change stays in memory); callers
    /// that must not claim success then use <see cref="UpdateOrThrow"/>.
    /// </summary>
    public bool Update(Action<AppConfig> change)
    {
        lock (_lock)
        {
            if (Fault is not null) TryRecover();
            else ReloadIfChangedOnDisk();
            if (Fault is null) MergeRecoveryNotes(save: false);
            change(Current);
            var saved = TrySave(Current);
            // The file stays as it is while it is unusable: only the few recovery fields go to a side file.
            if (Fault is not null) WriteRecoveryNotes();
            return saved;
        }
    }

    /// <summary>Like <see cref="Update"/>, but a change that could not be saved throws: a step that reports "done" must have saved.</summary>
    public void UpdateOrThrow(Action<AppConfig> change)
    {
        if (!Update(change)) throw new IOException(Strings.Get("config.notSaved", LastSaveError ?? FilePath));
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
    /// </summary>
    private static void SwapIn(string temp, string target)
    {
        if (File.Exists(target)) File.Replace(temp, target, null, ignoreMetadataErrors: true);
        else File.Move(temp, target);
    }

    internal Action<TimeSpan> Pause { get; set; } = Thread.Sleep;

    /// <summary>The file was repaired (or removed) while we ran locked: take it up again.</summary>
    private void TryRecover()
    {
        var read = ReadFile(out var json, out _);
        if (read == ReadResult.Missing)
        {
            Current = new AppConfig();
            Fault = null;
            return;
        }
        if (read != ReadResult.Ok) return;
        try
        {
            Current = Parse(json!) ?? new AppConfig();
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
            Current = Parse(json) ?? Current;
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
                    File.WriteAllText(temp, json);
                    MoveOver(temp, FilePath);
                    _lastSeenWrite = File.GetLastWriteTimeUtc(FilePath);
                    LastSaveError = null;
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
                File.Delete(temp);
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
