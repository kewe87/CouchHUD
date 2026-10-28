using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CouchHud.Core.Parental;

namespace CouchHud.Core.Config;

// Profiles per person (plan 0.5, item 5). The default profile is the one config.json always had: its favorites, play statistics,
// parental rules and UI settings stay in the old fields at the top of the file, so an older CouchHUD sees it unchanged. Only further
// profiles live in AppConfig.Profiles. Nobody outside this file and AppConfig touches those fields directly: everything goes through
// a ProfileView (AppConfig.ActiveProfile / AppConfig.Profile(id)), which hides where the data of a profile is kept.
//
// Fail-closed: an unknown profile id, a malformed id or a broken profile entry resolves to the default profile, so the default
// profile's rules apply, never "no rules". The main switch of the parental controls stays household-wide (AppConfig.ParentalEnabled).

/// <summary>One more person on the couch (the default profile is not in this list; see <see cref="AppConfig.Profiles"/>).</summary>
public sealed class UserProfile
{
    /// <summary>Fixed for the life of the profile; only <c>[a-z0-9-]</c> (see <see cref="ProfileIds"/>). It names the play time file.</summary>
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    /// <summary>Avatar id or image reference, shown in the profile picker.</summary>
    public string Avatar { get; set; } = "";

    /// <summary>Accent color (e.g. "#3fa9f5").</summary>
    public string Color { get; set; } = "";

    /// <summary>A child's profile: leaving it for another needs the parents (decided by the profile switch, not by the data).</summary>
    public bool Restricted { get; set; }

    /// <summary>
    /// A child's own code (salted PBKDF2, <see cref="CouchHud.Core.Security.Passcode.Hash"/>), so siblings cannot use each other's
    /// profile. Empty: none. Only a child's profile has one; never the code itself, never logged.
    /// </summary>
    public string? CodeHash { get; set; }

    /// <summary>Everything that belongs to this person. Missing means a broken entry: the profile is not used.</summary>
    public ProfileData? Data { get; set; }
}

/// <summary>What each person has for themselves. The default profile keeps the same things in the old top-level fields.</summary>
public sealed class ProfileData
{
    public List<string> Favorites { get; set; } = [];

    public List<ItemCollection> Collections { get; set; } = [];

    public Dictionary<string, long> LastLaunched { get; set; } = new();

    public Dictionary<string, double> PlaytimeMinutes { get; set; } = new();

    /// <summary>Length of the last play session per entry ("Continue playing").</summary>
    public Dictionary<string, double> LastSessionMinutes { get; set; } = new();

    /// <summary>
    /// The parental rules of this person. <see cref="ParentalConfig.Enabled"/> in here means nothing: the main switch is the
    /// household's (<see cref="AppConfig.ParentalEnabled"/>). Missing or out of range means a broken entry: the profile is not used.
    /// </summary>
    public ParentalConfig? Rules { get; set; }

    /// <summary>UI settings of this person; a setting left out follows the household's (the top-level field).</summary>
    public ProfileSettings Settings { get; set; } = new();
}

/// <summary>
/// THE list of UI settings each person has for themselves. A setting moves in or out of a profile here and in the matching block of
/// <see cref="ProfileView"/> (same name as the top-level field of <see cref="AppConfig"/>; a test checks the three agree).
/// Null: the household's value (the top-level field) applies.
/// </summary>
public sealed class ProfileSettings
{
    public string? Skin { get; set; }
    public bool? Sounds { get; set; }
    public int? SoundVolume { get; set; }
    public string? SoundSet { get; set; }
    public string? AudioMode { get; set; }
    public int? MusicVolume { get; set; }
    public string? Language { get; set; }
    public bool? ReduceMotion { get; set; }
}

/// <summary>What the profile picker shows of a further profile.</summary>
public sealed record ProfileInfo(string Name, string Avatar, string Color, bool Restricted, bool HasCode = false);

/// <summary>Profile ids and the play time file of a profile.</summary>
public static partial class ProfileIds
{
    /// <summary>The profile in the top-level fields of config.json.</summary>
    public const string Default = "default";

    /// <summary>Lower-case letters, digits and dashes, 1 to 32 characters, not starting with a dash.</summary>
    public static bool IsWellFormed(string? id) => id is not null && IdPattern().IsMatch(id);

    /// <summary>An id a further profile may have: well formed and not the default profile's.</summary>
    public static bool IsExtra(string? id) => IsWellFormed(id) && id != Default;

    /// <summary>
    /// The play time account of a deleted profile is not left lying around and not loaded again: it is renamed to
    /// <c>playtime-&lt;id&gt;.json.deleted-&lt;time&gt;</c> (the trace stays), its copy from before the last save to
    /// <c>playtime-&lt;id&gt;.json.bak.deleted-&lt;time&gt;</c> with the same time (else a new profile with the same id would find an account
    /// that "went missing"). Returns the new name of the account, or null when there was none (a lone .bak is renamed all the same).
    /// </summary>
    public static string? RetireLedger(string dataDir, string id, DateTime now)
    {
        if (!IsExtra(id)) throw new ArgumentException($"not a further profile: {id}", nameof(id));
        var file = LedgerFile(dataDir, id);
        var backup = CouchHud.Core.Files.DurableFile.BackupOf(file);
        var hasFile = File.Exists(file);
        var hasBackup = File.Exists(backup);
        if (!hasFile && !hasBackup) return null;
        var stamp = now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var suffix = $".deleted-{stamp}";
        for (var n = 2; File.Exists(file + suffix) || File.Exists(backup + suffix); n++) suffix = $".deleted-{stamp}-{n}";
        if (hasFile) File.Move(file, file + suffix);
        if (hasBackup) File.Move(backup, backup + suffix);
        return hasFile ? file + suffix : null;
    }

    /// <summary>The wrong-code counter of a child's own code, one per profile (a restart gives no fresh tries).</summary>
    public static string LockoutFile(string dataDir, string id)
    {
        if (!IsExtra(id)) throw new ArgumentException($"invalid profile id: {id}", nameof(id));
        return Path.Combine(dataDir, $"lockout-profile-{id}.json");
    }

    /// <summary>A new, fixed id (12 hex digits of a GUID).</summary>
    public static string New() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>
    /// The play time account of a profile: playtime.json for the default profile (as always), playtime-&lt;id&gt;.json for the others.
    /// Throws for an id that is not allowed, so no id can lead out of <paramref name="dataDir"/>.
    /// </summary>
    public static string LedgerFile(string dataDir, string id)
    {
        if (id == Default) return Path.Combine(dataDir, "playtime.json");
        if (!IsExtra(id)) throw new ArgumentException($"invalid profile id: {id}", nameof(id));
        var file = Path.Combine(dataDir, $"playtime-{id}.json");
        // Belt and braces: the pattern already rules out separators and dots.
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(file)), Path.GetFullPath(dataDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"invalid profile id: {id}", nameof(id));
        return file;
    }

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9-]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();
}

/// <summary>
/// One profile's data, wherever it is kept: the default profile reads and writes the top-level fields of <see cref="AppConfig"/>, a
/// further profile its <see cref="ProfileData"/>. The only way the rest of CouchHUD reaches favorites, collections, play statistics,
/// parental rules and the per-person UI settings.
/// </summary>
public sealed class ProfileView
{
    private readonly AppConfig _config;
    private readonly UserProfile? _entry;
    private readonly ProfileData? _data;

    internal ProfileView(AppConfig config, UserProfile? entry)
    {
        _config = config;
        _entry = entry;
        _data = entry?.Data;
    }

    public string Id => _entry?.Id ?? ProfileIds.Default;

    public bool IsDefault => _entry is null;

    /// <summary>Name, avatar, color and restricted of a further profile; null for the default profile. The data stays behind the view.</summary>
    public ProfileInfo? Info => _entry is null ? null : new(_entry.Name, _entry.Avatar, _entry.Color, _entry.Restricted, HasCode);

    /// <summary>The name to show, cleaned; null for a default profile nobody named (the UI says "Standard").</summary>
    public string? DisplayName => ProfileNames.Clean(_entry is null ? _config.ProfileName : _entry.Name);

    /// <summary>The avatar to show: a preset, whatever the file says.</summary>
    public string DisplayAvatar => ProfileLooks.AvatarOrDefault(_entry is null ? _config.ProfileAvatar : _entry.Avatar);

    /// <summary>The color to show: a preset, whatever the file says.</summary>
    public string DisplayColor => ProfileLooks.ColorOrDefault(_entry is null ? _config.ProfileColor : _entry.Color);

    /// <summary>A child's profile (the default profile never is one).</summary>
    public bool Restricted => _entry?.Restricted == true;

    /// <summary>A child's profile with its own code. A code on a profile that is not a child's does not count.</summary>
    public bool HasCode => _entry is { Restricted: true, CodeHash: { Length: > 0 } };

    /// <summary>The stored hash of the child's own code, for the check only (never shown, never logged).</summary>
    public string? CodeHash => HasCode ? _entry!.CodeHash : null;

    public List<string> Favorites => _data is null ? _config.Favorites : _data.Favorites;

    public List<ItemCollection> Collections => _data is null ? _config.Collections : _data.Collections;

    public Dictionary<string, long> LastLaunched => _data is null ? _config.LastLaunched : _data.LastLaunched;

    public Dictionary<string, double> PlaytimeMinutes => _data is null ? _config.PlaytimeMinutes : _data.PlaytimeMinutes;

    public Dictionary<string, double> LastSessionMinutes => _data is null ? _config.LastSessionMinutes : _data.LastSessionMinutes;

    /// <summary>
    /// The parental rules of this profile (time limits, window, per-game limit, locked games, age, time zone, ledger started).
    /// Its <see cref="ParentalConfig.Enabled"/> always shows the household's main switch; switch it through
    /// <see cref="AppConfig.ParentalEnabled"/>, not here.
    /// </summary>
    public ParentalConfig Rules
    {
        get
        {
            if (_data is null) return _config.Parental;
            var rules = _data.Rules!;
            rules.Enabled = _config.Parental.Enabled;
            return rules;
        }
    }

    // --- Per-person UI settings: the same list as ProfileSettings. ---

    public string Skin
    {
        get => _data?.Settings.Skin ?? _config.Skin;
        set { if (_data is null) _config.Skin = value; else _data.Settings.Skin = value; }
    }

    public bool Sounds
    {
        get => _data?.Settings.Sounds ?? _config.Sounds;
        set { if (_data is null) _config.Sounds = value; else _data.Settings.Sounds = value; }
    }

    public int SoundVolume
    {
        get => _data?.Settings.SoundVolume ?? _config.SoundVolume;
        set { if (_data is null) _config.SoundVolume = value; else _data.Settings.SoundVolume = value; }
    }

    public string SoundSet
    {
        get => _data?.Settings.SoundSet ?? _config.SoundSet;
        set { if (_data is null) _config.SoundSet = value; else _data.Settings.SoundSet = value; }
    }

    public string AudioMode
    {
        get => _data?.Settings.AudioMode ?? _config.AudioMode;
        set { if (_data is null) _config.AudioMode = value; else _data.Settings.AudioMode = value; }
    }

    public int MusicVolume
    {
        get => _data?.Settings.MusicVolume ?? _config.MusicVolume;
        set { if (_data is null) _config.MusicVolume = value; else _data.Settings.MusicVolume = value; }
    }

    public string Language
    {
        get => _data?.Settings.Language ?? _config.Language;
        set { if (_data is null) _config.Language = value; else _data.Settings.Language = value; }
    }

    public bool ReduceMotion
    {
        get => _data?.Settings.ReduceMotion ?? _config.ReduceMotion;
        set { if (_data is null) _config.ReduceMotion = value; else _data.Settings.ReduceMotion = value; }
    }
}

/// <summary>Checks a profile entry from the file: anything broken is not used (the default profile applies instead).</summary>
internal static class ProfileCheck
{
    public static bool Usable(UserProfile? p) =>
        p is { Data: { } d }
        && ProfileIds.IsExtra(p.Id)
        && p.Name is not null && p.Avatar is not null && p.Color is not null
        && d.Favorites is not null
        && d.Collections is not null && d.Collections.All(c => c is { Id: not null, Name: not null, Items: not null })
        && d.LastLaunched is not null && d.PlaytimeMinutes is not null && d.LastSessionMinutes is not null
        && d.Settings is not null
        && d.Rules is { } r && RulesValid(r);

    /// <summary>Only values the settings page can set: anything else is a damaged or edited entry.</summary>
    public static bool RulesValid(ParentalConfig r) =>
        r.WeekdayMinutes is >= 0 and <= 1440
        && r.WeekendMinutes is >= 0 and <= 1440
        && r.PerGameMinutes is >= 0 and <= 1440
        && (r.MaxAge == 0 || AgeRatings.AllSteps.Contains(r.MaxAge))
        && (r.WindowStartMinute == -1 && r.WindowEndMinute == -1
            || r.WindowStartMinute is >= 0 and < 1440 && r.WindowEndMinute is >= 0 and < 1440 && r.WindowStartMinute != r.WindowEndMinute)
        && r.LockedItems is not null && r.LockedItems.All(i => i is not null)
        && r.TimeZoneId is not null;

    /// <summary>The same rules without any limit (an adult's profile): no daily time, no window, no per-game limit, no age, nothing locked.</summary>
    public static ParentalConfig NoLimits(ParentalConfig r) => new()
    {
        WeekdayMinutes = 0,
        WeekendMinutes = 0,
        WindowStartMinute = -1,
        WindowEndMinute = -1,
        PerGameMinutes = 0,
        LockedItems = [],
        MaxAge = 0,
        BlockUnknownAge = false,
        CountApps = r.CountApps,
        TimeZoneId = r.TimeZoneId ?? "",
        LedgerStarted = r.LedgerStarted
    };

    /// <summary>Any limit at all: a daily time, a time window, a per-game limit, an age or a locked entry.</summary>
    public static bool HasLimits(ParentalConfig r) =>
        r.WeekdayMinutes > 0 || r.WeekendMinutes > 0 || r.HasWindow || r.PerGameMinutes > 0 || r.MaxAge > 0 || r.LockedItems is { Count: > 0 };

    /// <summary>A copy of the rules for a new profile; its play time account starts fresh.</summary>
    public static ParentalConfig CopyRules(ParentalConfig r) => new()
    {
        WeekdayMinutes = r.WeekdayMinutes,
        WeekendMinutes = r.WeekendMinutes,
        WindowStartMinute = r.WindowStartMinute,
        WindowEndMinute = r.WindowEndMinute,
        PerGameMinutes = r.PerGameMinutes,
        LockedItems = [.. r.LockedItems ?? []],
        MaxAge = r.MaxAge,
        BlockUnknownAge = r.BlockUnknownAge,
        CountApps = r.CountApps,
        TimeZoneId = r.TimeZoneId ?? "",
        LedgerStarted = false
    };
}

/// <summary>What a person may call a profile: 1 to 24 characters, no control, format or line break characters.</summary>
public static class ProfileNames
{
    public const int MaxLength = 24;

    /// <summary>
    /// A name entered for a profile: <see cref="Normalize">cleaned</see>, then 1 to <see cref="MaxLength"/> characters, else null (refused,
    /// never shortened without a word: the message says "1 to 24 characters", so 25 are refused).
    /// </summary>
    public static string? Accept(string? name) => Normalize(name) is { Length: > 0 and <= MaxLength } clean ? clean : null;

    /// <summary>
    /// The cleaned name to show, or null when nothing is left: <see cref="Normalize"/>, cut at <see cref="MaxLength"/> without splitting a
    /// character (only for what is already in the file; a new name goes through <see cref="Accept"/>).
    /// </summary>
    public static string? Clean(string? name)
    {
        var text = Normalize(name);
        if (text is null) return null;
        if (text.Length <= MaxLength) return text.Length == 0 ? null : text;
        // By text elements, so a surrogate pair or an emoji with a modifier is never cut in half.
        var info = new System.Globalization.StringInfo(text);
        var cut = new System.Text.StringBuilder();
        for (var i = 0; i < info.LengthInTextElements; i++)
        {
            var element = info.SubstringByTextElements(i, 1);
            if (cut.Length + element.Length > MaxLength) break;
            cut.Append(element);
        }
        var result = cut.ToString().TrimEnd();
        return result.Length == 0 ? null : result;
    }

    /// <summary>
    /// Control and format characters (bidi overrides too), private and unassigned code points removed, every run of white space and line
    /// breaks one space, nothing at either end. Not shortened.
    /// </summary>
    public static string? Normalize(string? name)
    {
        if (name is null) return null;
        var sb = new System.Text.StringBuilder(name.Length);
        var space = false;
        foreach (var ch in name)
        {
            var cat = char.GetUnicodeCategory(ch);
            if (char.IsWhiteSpace(ch) || cat is System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator)
            {
                space = sb.Length > 0;
                continue;
            }
            if (cat is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.PrivateUse or System.Globalization.UnicodeCategory.OtherNotAssigned) continue;
            if (space)
            {
                sb.Append(' ');
                space = false;
            }
            sb.Append(ch);
        }
        return sb.ToString();
    }
}

/// <summary>The avatars and colors a profile can have: only these (the picker shows them; anything else in the file is not used).</summary>
public static class ProfileLooks
{
    public static readonly IReadOnlyList<string> Avatars = ["fox", "panda", "frog", "lion", "octopus", "rocket", "cat", "dog", "unicorn", "robot", "alien", "star"];

    public static readonly IReadOnlyList<string> Colors = ["#3fa9f5", "#ff8800", "#e5484d", "#30a46c", "#8e4ec6", "#f5d90a", "#e93d82", "#12a594"];

    public static bool IsAvatar(string? id) => id is not null && Avatars.Contains(id);

    public static bool IsColor(string? color) => color is not null && Colors.Contains(color);

    /// <summary>What the picker shows: the stored value when it is one of the presets, else the first preset.</summary>
    public static string AvatarOrDefault(string? id) => IsAvatar(id) ? id! : Avatars[0];

    public static string ColorOrDefault(string? color) => IsColor(color) ? color! : Colors[0];
}
