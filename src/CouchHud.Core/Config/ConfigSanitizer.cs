using CouchHud.Core.Library;
using CouchHud.Core.Parental;
using CouchHud.Core.Plugins;
using CouchHud.Core.Saves;

namespace CouchHud.Core.Config;

/// <summary>The ranges the settings page enforces, in one place: the page and the load of config.json use the same numbers.</summary>
public static class ConfigLimits
{
    public const int ReturnHoldMsMin = 200, ReturnHoldMsMax = 5000;
    public const int ScreensaverMinutesMax = 240;
    public const int DisplayOffMinutesMax = 480;
    public const int SetupWizardDoneMax = 1000;
    public const int VolumeMax = 100;
    public const int NightModeStrengthMax = 100;
    public const int BrowserZoomMin = 50, BrowserZoomMax = 300;
    public const int BackupsToKeepMin = 1, BackupsToKeepMax = 50;
    public const int ParentalMinutesMax = 1440;

    /// <summary>Ports a listener may use (no privileged ports).</summary>
    public const int PortMin = 1024, PortMax = 65535;
    public const int DefaultRemotePort = 8766, DefaultMcpPort = 8765;

    // Bounds for what the file may hold at most (the page never gets near them).
    public const int MaxRemoteDevices = 64;
    public const int MaxDeviceNameLength = 64;
    public const int MaxNameLength = 200;
    /// <summary>A custom entry's name: its id is made from it, so it is cut when written, never only when loaded.</summary>
    public const int MaxEntryNameLength = MaxNameLength;
    /// <summary>Write-side limits of an own entry's program path, working folder, link and all its start parameters together, and of a bookmark's link (not cut on load: an entry that works stays as it is).</summary>
    public const int MaxEntryPathLength = 2048, MaxEntryArgsLength = 4096, MaxEntryUrlLength = 4096;
    public const int MaxListEntries = 5000;
    public const int MaxFolderEntries = 200;
    /// <summary>The UI sets no limit on collections; far above any real use.</summary>
    public const int MaxCollections = 2000;
    public const int MaxSmallLists = 64;
    public const int MaxProfileEntries = 32;
}

/// <summary>
/// One pass over a freshly read config.json: absurd numbers are clamped to what the settings page allows, over-long lists and names are cut,
/// invalid choices and ports go back to their defaults. It never loosens a protection: what it cannot read as a lock, parental control or
/// listener setting is replaced by the safer value (lock on, listener off, parental controls kept).
/// </summary>
internal static class ConfigSanitizer
{
    private static readonly HashSet<string> Logged = [];
    private const int MaxLogged = 512;

    /// <summary>Runs all corrections; returns what was corrected (names only, never values of codes or secrets).</summary>
    public static IReadOnlyList<string> Run(AppConfig c, string? path = null, Action<string>? log = null)
    {
        var fixes = new List<string>();
        void Fix(string what) => fixes.Add(what);

        Security(c, Fix);
        Choices(c, Fix);
        Numbers(c, Fix);
        Lists(c, Fix);
        Profiles(c, Fix);

        foreach (var what in fixes)
        {
            bool first;
            lock (Logged)
            {
                // Once per file and correction; bounded, a full table starts over.
                if (Logged.Count >= MaxLogged) Logged.Clear();
                first = Logged.Add((path ?? "") + "|" + what);
            }
            if (first) (log ?? (line => Console.Error.WriteLine(line)))($"[config] corrected on load: {what}");
        }
        return fixes;
    }

    private static void Security(AppConfig c, Action<string> fix)
    {
        // A lock mode we cannot read becomes a lock (the Windows sign-in), never "off".
        var lockMode = (c.CouchLock ?? "").Trim().ToLowerInvariant();
        if (lockMode is not ("off" or "code" or "windows")) lockMode = "windows";
        if (lockMode != c.CouchLock) { fix("couch lock mode"); c.CouchLock = lockMode; }

        // An empty parental block must not mean "parental controls off".
        if (c.Parental is null)
        {
            c.Parental = new ParentalConfig { Enabled = true };
            fix("parental controls missing, kept on");
        }
        else Rules(c.Parental, fix);

        // The list of locks for all child profiles: null entries go, the length is cut like LockedItems. A list that is gone (null) cannot say
        // what was locked: it becomes empty but marked, and then the child's profiles lock everything until the parents set it again.
        if (c.LockedItemsAllChildren is null)
        {
            c.LockedItemsAllChildren = [];
            c.LockedItemsAllChildrenUnreadable = true;
            fix("locks for all child profiles unreadable, everything locked for children");
        }
        else
        {
            var all = c.LockedItemsAllChildren;
            var changed = false;
            if (all.Any(i => i is null)) { c.LockedItemsAllChildren = all = all.Where(i => i is not null).ToList(); changed = true; }
            if (all.Count > ConfigLimits.MaxListEntries) { all.RemoveRange(ConfigLimits.MaxListEntries, all.Count - ConfigLimits.MaxListEntries); changed = true; }
            if (changed) fix("locks for all child profiles");
        }

        var integration = (c.IntegrationMode ?? "").Trim().ToLowerInvariant();
        if (integration is not ("light" or "full")) integration = "light";
        if (integration != c.IntegrationMode) { c.IntegrationMode = integration; fix("integration mode"); }

        // A port that cannot be listened on: back to the default, and the listener stays off until someone switches it on again.
        if (c.RemotePort is < ConfigLimits.PortMin or > ConfigLimits.PortMax)
        {
            c.RemotePort = ConfigLimits.DefaultRemotePort;
            c.RemoteEnabled = false;
            fix("phone connection port, listener off");
        }
        if (c.McpPort is < ConfigLimits.PortMin or > ConfigLimits.PortMax)
        {
            c.McpPort = ConfigLimits.DefaultMcpPort;
            c.McpEnabled = false;
            c.McpLan = false;
            fix("MCP port, listener off");
        }
    }

    /// <summary>The default profile's parental rules. Minutes of 0 mean "no limit", so a negative number is never turned into 0.</summary>
    private static void Rules(ParentalConfig r, Action<string> fix)
    {
        var changed = false;
        int Minutes(int v, int fallback)
        {
            if (v < 0) { changed = true; return fallback; }
            if (v > ConfigLimits.ParentalMinutesMax) { changed = true; return ConfigLimits.ParentalMinutesMax; }
            return v;
        }
        r.WeekdayMinutes = Minutes(r.WeekdayMinutes, 120);
        r.WeekendMinutes = Minutes(r.WeekendMinutes, 180);
        r.PerGameMinutes = Minutes(r.PerGameMinutes, 120);
        if (r.TimeZoneId is null) { r.TimeZoneId = ""; changed = true; }
        if (r.LockedItems is null) { r.LockedItems = []; changed = true; }
        else if (r.LockedItems.Any(i => i is null)) { r.LockedItems = r.LockedItems.Where(i => i is not null).ToList(); changed = true; }
        // Cut from the end: the games locked first stay locked.
        if (r.LockedItems.Count > ConfigLimits.MaxListEntries) { r.LockedItems.RemoveRange(ConfigLimits.MaxListEntries, r.LockedItems.Count - ConfigLimits.MaxListEntries); changed = true; }
        if (changed) fix("parental time limits and lists");
    }

    private static void Choices(AppConfig c, Action<string> fix)
    {
        void Pick(string name, string? value, string[] allowed, string fallback, Action<string> set)
        {
            if (value is not null && allowed.Contains(value)) return;
            set(fallback);
            fix(name);
        }
        Pick("screensaver style", c.ScreensaverStyle, ["art", "clock", "dim", "black"], "art", v => c.ScreensaverStyle = v);
        Pick("audio mode", c.AudioMode, ["off", "ui", "music"], "ui", v => c.AudioMode = v);
        Pick("interface HDR", c.UiHdr, ["off", "auto", "on"], "auto", v => c.UiHdr = v);
        Pick("pad glyphs", c.PadGlyphs, ["auto", "xbox", "playstation"], "auto", v => c.PadGlyphs = v);
        Pick("pointer", c.Pointer, ["skin", "system", "mouse"], "skin", v => c.Pointer = v);
        Pick("night mode", c.NightMode, ["off", "on", "evening"], "off", v => c.NightMode = v);
        Pick("language", c.Language, ["auto", "de", "en"], "auto", v => c.Language = v);

        if (FavoritesShelf.NormalizeMode(c.FavoritesMode) != c.FavoritesMode) { c.FavoritesMode = FavoritesShelf.NormalizeMode(c.FavoritesMode); fix("favorites mode"); }
        if (FavoritesShelf.NormalizeRecentDays(c.FavoritesRecentDays) != c.FavoritesRecentDays)
        {
            c.FavoritesRecentDays = FavoritesShelf.NormalizeRecentDays(c.FavoritesRecentDays);
            fix("favorites time span");
        }
    }

    private static void Numbers(AppConfig c, Action<string> fix)
    {
        void Clamp(string name, int value, int min, int max, Action<int> set)
        {
            var v = Math.Clamp(value, min, max);
            if (v == value) return;
            set(v);
            fix(name);
        }
        Clamp("return hold time", c.ReturnHoldMs, ConfigLimits.ReturnHoldMsMin, ConfigLimits.ReturnHoldMsMax, v => c.ReturnHoldMs = v);
        Clamp("screensaver minutes", c.ScreensaverMinutes, 0, ConfigLimits.ScreensaverMinutesMax, v => c.ScreensaverMinutes = v);
        Clamp("display off minutes", c.DisplayOffMinutes, 0, ConfigLimits.DisplayOffMinutesMax, v => c.DisplayOffMinutes = v);
        Clamp("setup assistant marker", c.SetupWizardDone, 0, ConfigLimits.SetupWizardDoneMax, v => c.SetupWizardDone = v);
        Clamp("sound volume", c.SoundVolume, 0, ConfigLimits.VolumeMax, v => c.SoundVolume = v);
        Clamp("music volume", c.MusicVolume, 0, ConfigLimits.VolumeMax, v => c.MusicVolume = v);
        Clamp("night mode strength", c.NightModeStrength, 0, ConfigLimits.NightModeStrengthMax, v => c.NightModeStrength = v);
        Clamp("browser zoom", c.BrowserZoom, ConfigLimits.BrowserZoomMin, ConfigLimits.BrowserZoomMax, v => c.BrowserZoom = v);
        Clamp("backups to keep", c.BackupsToKeep, ConfigLimits.BackupsToKeepMin, ConfigLimits.BackupsToKeepMax, v => c.BackupsToKeep = v);
        Clamp("automatic backups to keep", c.AutoBackupsToKeep, 1, SaveGameService.MaxAutoKeep, v => c.AutoBackupsToKeep = v);
        Clamp("automatic backup game size", c.AutoBackupMaxGameMb, SaveGameService.MinGameMb, SaveGameService.MaxGameMb, v => c.AutoBackupMaxGameMb = v);
        Clamp("automatic backup total size", c.AutoBackupMaxTotalMb, SaveGameService.MinTotalMb, SaveGameService.MaxTotalMb, v => c.AutoBackupMaxTotalMb = v);
        Clamp("paired controllers", c.BluetoothPairedControllers, 0, 64, v => c.BluetoothPairedControllers = v);
    }

    /// <summary>Drops empty entries and cuts a list to <paramref name="max"/> (from the end); true when something changed.</summary>
    private static List<T> Cap<T>(List<T>? list, int max, out bool changed) where T : class
    {
        changed = false;
        if (list is null) { changed = true; return []; }
        if (list.Any(x => x is null)) { list = list.Where(x => x is not null).ToList(); changed = true; }
        if (list.Count > max) { list.RemoveRange(max, list.Count - max); changed = true; }
        return list;
    }

    private static string Cut(string? s, int max) => s is null ? "" : s.Length <= max ? s : s[..max];

    private static void Lists(AppConfig c, Action<string> fix)
    {
        c.RemoteDevices = Cap(c.RemoteDevices, ConfigLimits.MaxRemoteDevices, out var devicesChanged);
        foreach (var d in c.RemoteDevices)
            if ((d.Name ?? "").Length > ConfigLimits.MaxDeviceNameLength) { d.Name = Cut(d.Name, ConfigLimits.MaxDeviceNameLength); devicesChanged = true; }
        if (devicesChanged) fix("paired phones");

        List<string> Strings(string name, List<string>? list, int max)
        {
            var result = Cap(list, max, out var changed);
            if (changed) fix(name);
            return result;
        }
        c.Favorites = Strings("favorites", c.Favorites, ConfigLimits.MaxListEntries);
        c.GameFolders = Strings("game folders", c.GameFolders, ConfigLimits.MaxFolderEntries);
        c.GuideApps = Strings("guide apps", c.GuideApps, ConfigLimits.MaxSmallLists);
        c.DisabledPlugins = Strings("disabled plugins", c.DisabledPlugins, ConfigLimits.MaxListEntries);
        c.DisabledAreas = Strings("disabled areas", c.DisabledAreas, ConfigLimits.MaxSmallLists);
        c.CatalogSources = Strings("catalog sources", c.CatalogSources, CatalogSourceRules.MaxSources);
        c.HomeAssistantPinned = Strings("home assistant pins", c.HomeAssistantPinned, ConfigLimits.MaxListEntries);
        c.HomeAssistantEntities = Strings("home assistant entities", c.HomeAssistantEntities, ConfigLimits.MaxListEntries);
        c.CouchServices = Strings("couch services", c.CouchServices, ConfigLimits.MaxSmallLists);
        c.CouchServicesInstalled = Strings("installed couch services", c.CouchServicesInstalled, ConfigLimits.MaxSmallLists);
        c.PluginNetOff = Strings("plugin network switches", c.PluginNetOff, ConfigLimits.MaxListEntries);

        c.ExtraCatalogSources = Cap(c.ExtraCatalogSources, CatalogSourceRules.MaxSources, out var extraChanged);
        if (extraChanged) fix("extra catalog sources");

        c.Apps = Cap(c.Apps, ConfigLimits.MaxListEntries, out var appsChanged);
        foreach (var a in c.Apps) if ((a.Name ?? "").Length > ConfigLimits.MaxEntryNameLength) { a.Name = Cut((a.Name ?? "").Trim(), ConfigLimits.MaxEntryNameLength); appsChanged = true; }
        if (appsChanged) fix("own entries");

        c.Bookmarks = Cap(c.Bookmarks, ConfigLimits.MaxListEntries, out var marksChanged);
        foreach (var b in c.Bookmarks) if ((b.Name ?? "").Length > ConfigLimits.MaxNameLength) { b.Name = Cut(b.Name, ConfigLimits.MaxNameLength); marksChanged = true; }
        if (marksChanged) fix("bookmarks");

        c.Collections = Cap(c.Collections, ConfigLimits.MaxCollections, out var colsChanged);
        foreach (var col in c.Collections)
        {
            if ((col.Name ?? "").Length > ConfigLimits.MaxNameLength) { col.Name = Cut(col.Name, ConfigLimits.MaxNameLength); colsChanged = true; }
            if (col.Items is { Count: > ConfigLimits.MaxListEntries }) { col.Items.RemoveRange(ConfigLimits.MaxListEntries, col.Items.Count - ConfigLimits.MaxListEntries); colsChanged = true; }
        }
        if (colsChanged) fix("collections");
    }

    private static void Profiles(AppConfig c, Action<string> fix)
    {
        var changed = false;
        if ((c.ProfileName?.Length ?? 0) > ProfileNames.MaxLength) { c.ProfileName = ProfileNames.Clean(c.ProfileName); changed = true; }
        if (c.Profiles is { } list)
        {
            // Distinct entries in file order (a later entry with a known id was never used: the first one counts).
            var seen = new HashSet<string> { ProfileIds.Default };
            var distinct = new List<UserProfile>();
            foreach (var p in list)
            {
                if (p is null) { changed = true; continue; }
                if (!seen.Add(p.Id ?? "")) { changed = true; continue; }
                distinct.Add(p);
            }
            var activeId = ProfileIds.IsExtra(c.ActiveProfileId) ? c.ActiveProfileId : null;
            var active = activeId is null ? null : distinct.FirstOrDefault(p => p.Id == activeId);
            var room = AppConfig.MaxProfiles - 1;
            var usable = distinct.Where(ProfileCheck.Usable).ToList();
            var chosen = new HashSet<UserProfile>(usable.Take(room));
            // The profile on the couch stays, in place of the last one that would have stayed.
            if (active is not null && usable.Contains(active) && !chosen.Contains(active))
            {
                chosen.Remove(usable[room - 1]);
                chosen.Add(active);
            }
            var kept = new List<UserProfile>();
            foreach (var p in distinct)
            {
                // Broken entries stay (they count as the default profile) but only a few: the list stays small.
                var keep = ProfileCheck.Usable(p) ? chosen.Contains(p) : kept.Count < ConfigLimits.MaxProfileEntries;
                if (!keep) { changed = true; continue; }
                kept.Add(p);
                if ((p.Name?.Length ?? 0) > ProfileNames.MaxLength && ProfileNames.Clean(p.Name) is { } clean) { p.Name = clean; changed = true; }
                if (p.Data?.Settings is { } s)
                {
                    if (s.SoundVolume is { } sv && (sv < 0 || sv > ConfigLimits.VolumeMax)) { s.SoundVolume = Math.Clamp(sv, 0, ConfigLimits.VolumeMax); changed = true; }
                    if (s.MusicVolume is { } mv && (mv < 0 || mv > ConfigLimits.VolumeMax)) { s.MusicVolume = Math.Clamp(mv, 0, ConfigLimits.VolumeMax); changed = true; }
                }
            }
            if (kept.Count != list.Count) c.Profiles = kept;
        }
        if (changed) fix("profiles");
    }
}
