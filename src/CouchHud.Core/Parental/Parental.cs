using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CouchHud.Core.Library;
using CouchHud.Sdk;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Parental;

// Parental controls: a play time account per day, an allowed time window, per-game limits,
// locked games and an age limit. Pure logic with injectable clocks; the shell feeds it the
// running game (Hud.cs) and enforces what it says.

/// <summary>What the parents set. Minutes of 0 mean "no limit"; window minutes are minutes after midnight, -1 for "no window".</summary>
public sealed class ParentalConfig
{
    public bool Enabled { get; set; }
    public int WeekdayMinutes { get; set; } = 120;
    public int WeekendMinutes { get; set; } = 180;
    public int WindowStartMinute { get; set; } = -1;
    public int WindowEndMinute { get; set; } = -1;
    /// <summary>Maximum minutes per game and day, 0 = none.</summary>
    public int PerGameMinutes { get; set; }
    /// <summary>Games that start only with the parents' code.</summary>
    public List<string> LockedItems { get; set; } = [];
    /// <summary>
    /// Games rated this age or older are locked, 0 = off. An age, not a system's code: a step of the active rating system (USK 6/12/16/18,
    /// PEGI 3/7/12/16/18, ESRB E 6, E10+ 10, T 13, M 17, AO 18, see <see cref="AgeRatings"/>), so a switch of the system keeps a safe
    /// value. Read through <see cref="AgeLimit"/>.
    /// </summary>
    public int MaxAge { get; set; }

    /// <summary>The limit that counts: 0 off, a step as stored, anything else (an edited file) the strictest step there is.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int AgeLimit => AgeRatings.NormalizeLimit(MaxAge);
    /// <summary>With an age limit: games without a known rating need the parents' code too.</summary>
    public bool BlockUnknownAge { get; set; } = true;
    /// <summary>Apps (browser, streaming …) count as play time and must be measurable; otherwise they are free.</summary>
    public bool CountApps { get; set; } = true;
    /// <summary>Time zone the day limits go by (set when the profile is switched on, so changing Windows' zone gives nothing).</summary>
    public string TimeZoneId { get; set; } = "";
    /// <summary>The play time file has been written once: a missing file afterwards counts as tampering.</summary>
    public bool LedgerStarted { get; set; }

    public bool HasWindow => WindowStartMinute is >= 0 and < 1440 && WindowEndMinute is >= 0 and < 1440 && WindowStartMinute != WindowEndMinute;
}

/// <summary>
/// A clock the child cannot turn back: it follows the monotonic tick counter and only adopts the
/// system time while the two agree. A set-back or a jump forward of the system clock is ignored
/// (until <see cref="AcceptSystemTime"/>, after waking from sleep, or a restart).
/// </summary>
public sealed class TrustedClock
{
    private readonly Func<DateTimeOffset> _systemUtc;
    private readonly Func<long> _monotonicMs;
    private readonly Func<DateTimeOffset, TimeSpan> _offset;
    private DateTimeOffset _utc;
    private long _lastMono;

    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(2);

    /// <param name="persistedUtc">The last trusted time of an earlier run: a clock set back across a restart does not help.</param>
    public TrustedClock(Func<DateTimeOffset> systemUtc, Func<long> monotonicMs, Func<DateTimeOffset, TimeSpan>? offset = null, DateTimeOffset? persistedUtc = null)
    {
        _systemUtc = systemUtc;
        _monotonicMs = monotonicMs;
        _offset = offset ?? (t => TimeZoneInfo.Local.GetUtcOffset(t));
        var sys = systemUtc();
        // At the start the system clock only counts as far as it agrees with the last trusted time: a clock turned forward
        // in the BIOS (a new day) is believed only after a web server confirms it (see AcceptSystemTime), while a clock
        // set back changes nothing. Without a stored time (first run) the system clock is all there is.
        _utc = persistedUtc is { } p ? p : sys;
        _lastMono = monotonicMs();
        if ((_utc - sys).Duration() > Tolerance) Jumped = true;
    }

    public DateTimeOffset UtcNow => _utc;
    public DateTime Local => (_utc + _offset(_utc)).DateTime;
    public DateOnly Today => DateOnly.FromDateTime(Local);

    /// <summary>The system clock disagrees with the elapsed time (set back or forward).</summary>
    public bool Jumped { get; private set; }

    public void Sync()
    {
        var mono = _monotonicMs();
        var delta = Math.Max(0, mono - _lastMono);
        _lastMono = mono;
        _utc = _utc.AddMilliseconds(delta);
        var sys = _systemUtc();
        if ((sys - _utc).Duration() <= Tolerance)
        {
            _utc = sys;
            Jumped = false;
        }
        else
        {
            Jumped = true;
        }
    }

    /// <summary>The system time was confirmed from outside (or the parents say so): adopt it, forward only unless forced.</summary>
    public void AcceptSystemTime(bool force = false)
    {
        _lastMono = _monotonicMs();
        var sys = _systemUtc();
        if (force || sys > _utc) _utc = sys;
        Sync();
    }
}

/// <summary>Played time per day and game; survives restarts and crashes (saved regularly).</summary>
public sealed class PlayLedger
{
    public sealed class DayRecord
    {
        public double Seconds { get; set; }
        public Dictionary<string, double> Games { get; set; } = [];
        public int BonusMinutes { get; set; }
    }

    private sealed class FileData
    {
        public Dictionary<string, DayRecord> Days { get; set; } = [];
        public DateTimeOffset? LastUtc { get; set; }
        public string? Mac { get; set; }
    }

    private readonly string? _file;
    private readonly bool _persist;
    private readonly byte[]? _key;
    private FileData _data = new();
    private readonly object _gate = new();

    /// <param name="key">Secret for the HMAC over the file; without it nothing is checked.</param>
    /// <param name="expectExisting">The file was written before: a missing one is tampering.</param>
    /// <param name="persist">False while the key is only a secret of this process: the time is still counted in memory, but the signed file on disk stays as it is.</param>
    public PlayLedger(string? file = null, byte[]? key = null, bool expectExisting = false, bool persist = true)
    {
        _persist = persist;
        _file = file;
        _key = key;
        if (file is null) return;
        var result = TryLoad(file, key, out var loaded);
        if (result == LoadResult.Ok) _data = loaded;
        else if (result != LoadResult.BadSignature && TryLoad(BackupOf(file), key, out var backup) == LoadResult.Ok)
        {
            // The file is gone or half written (power cut while saving) but the copy from before is intact and signed.
            // A file that is complete but whose signature does not fit is tampering and gets no second chance.
            _data = backup;
            Console.Error.WriteLine("[parental] play time file unreadable, using the copy from before");
        }
        else if (result != LoadResult.Missing || (expectExisting && key is not null))
        {
            Tampered = key is not null;
        }
    }

    private enum LoadResult { Ok, Missing, Corrupt, BadSignature }

    private static string BackupOf(string file) => CouchHud.Core.Files.DurableFile.BackupOf(file);

    /// <summary>
    /// Whether a missing file counts as tampering: it was written before (<paramref name="started"/>), or its copy from before is
    /// still there while the file itself is gone. A brand-new account (a profile just created) has neither and starts at zero.
    /// </summary>
    public static bool ShouldExist(string file, bool started) => started || File.Exists(BackupOf(file));

    /// <summary>Reads and checks one file.</summary>
    private LoadResult TryLoad(string file, byte[]? key, out FileData data)
    {
        data = new();
        if (!File.Exists(file)) return LoadResult.Missing;
        try
        {
            // The JSON null is no account (nothing played): it is a broken file like an empty one, so the copy from before counts.
            var read = JsonSerializer.Deserialize<FileData>(File.ReadAllText(file)) ?? throw new JsonException("the file holds no play time");
            if (key is not null && !MacValid(read)) return LoadResult.BadSignature;
            data = read;
            return LoadResult.Ok;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[parental] ledger unreadable: {ex.Message}");
            return LoadResult.Corrupt;
        }
    }

    /// <summary>The file was changed, deleted or is unreadable: today counts as used up until the parents reset it.</summary>
    public bool Tampered { get; private set; }

    private string Mac(FileData data)
    {
        var copy = new FileData { Days = data.Days, LastUtc = data.LastUtc };
        return Convert.ToBase64String(HMACSHA256.HashData(_key!, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(copy))));
    }

    private bool MacValid(FileData data) =>
        data.Mac is { Length: > 0 } mac && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(mac), Encoding.UTF8.GetBytes(Mac(data)));

    /// <summary>The parents accept the situation: empty account, counting starts over.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _data = new FileData();
            Tampered = false;
            Dirty = true;
        }
    }

    public bool Dirty { get; private set; }

    /// <summary>The last trusted time written, for <see cref="TrustedClock"/> after a restart.</summary>
    public DateTimeOffset? LastUtc
    {
        get { lock (_gate) return _data.LastUtc; }
        set { lock (_gate) { _data.LastUtc = value; Dirty = true; } }
    }

    private static string Key(DateOnly day) => day.ToString("yyyy-MM-dd");

    private DayRecord Day(DateOnly day)
    {
        if (!_data.Days.TryGetValue(Key(day), out var record)) _data.Days[Key(day)] = record = new DayRecord();
        return record;
    }

    public double Seconds(DateOnly day)
    {
        lock (_gate) return _data.Days.TryGetValue(Key(day), out var r) ? r.Seconds : 0;
    }

    public double Seconds(DateOnly day, string gameId)
    {
        lock (_gate) return _data.Days.TryGetValue(Key(day), out var r) && r.Games.TryGetValue(gameId, out var s) ? s : 0;
    }

    public int BonusMinutes(DateOnly day)
    {
        lock (_gate) return _data.Days.TryGetValue(Key(day), out var r) ? r.BonusMinutes : 0;
    }

    public void Add(DateOnly day, string? gameId, double seconds)
    {
        if (seconds <= 0) return;
        lock (_gate)
        {
            var record = Day(day);
            record.Seconds += seconds;
            if (gameId is not null) record.Games[gameId] = record.Games.GetValueOrDefault(gameId) + seconds;
            Dirty = true;
        }
    }

    public void AddBonus(DateOnly day, int minutes)
    {
        lock (_gate)
        {
            Day(day).BonusMinutes += minutes;
            Dirty = true;
        }
    }

    public void Save(DateOnly today)
    {
        if (_file is null || !_persist) return;
        lock (_gate)
        {
            foreach (var old in _data.Days.Keys.Where(k => DateOnly.TryParse(k, out var d) && d < today.AddDays(-21)).ToList()) _data.Days.Remove(old);
            try
            {
                _data.Mac = _key is null ? null : Mac(_data);
                // Written through to the disk before it replaces the old file, which stays as .bak: a power cut
                // in the middle leaves one of the two intact instead of an empty or half-written day.
                CouchHud.Core.Files.DurableFile.WriteAllText(_file, JsonSerializer.Serialize(_data));
                Dirty = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[parental] could not save the play time: {ex.Message}");
            }
        }
    }
}

public enum ParentalWarning { Minutes15, Minutes5, Expired }

/// <summary>
/// Limiter says what is closest: "window", "day" or "game". <see cref="RemainingSeconds"/> is never negative: time played
/// past the limit (the grace period before a game is closed still counts) shows as 0, not as minus.
/// </summary>
public sealed record ParentalStatus(
    bool Enabled,
    int? RemainingSeconds,
    string? Limiter,
    bool InWindow,
    double UsedSeconds,
    int? DayLimitMinutes,
    int BonusMinutes,
    bool ClockJumped)
{
    /// <summary>A limit applies and its time is used up (or the window is closed, or the account was tampered with).</summary>
    public bool TimeUp => Enabled && (RemainingSeconds is <= 0 || !InWindow);
}

public sealed record StartCheck(bool Ok, string? Reason, string Message);

public sealed partial class ParentalService
{
    public static readonly int[] WarningSeconds = [15 * 60, 5 * 60, 0];
    private static readonly TimeSpan SaveEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdleSaveEvery = TimeSpan.FromMinutes(5);

    private readonly Func<ParentalConfig> _config;
    private readonly PlayLedger _ledger;
    private readonly TrustedClock _clock;
    private readonly HashSet<int> _fired = [];
    private DateTimeOffset _lastTick;
    private DateTimeOffset _lastSave;

    public ParentalService(Func<ParentalConfig> config, PlayLedger ledger, TrustedClock clock)
    {
        _config = config;
        _ledger = ledger;
        _clock = clock;
        _lastTick = _lastSave = clock.UtcNow;
    }

    public bool Tampered => _ledger.Tampered;

    public PlayLedger Ledger => _ledger;

    public event Action<ParentalWarning>? Warning;

    /// <summary>Remaining seconds of the closest limit at the last tick (for warnings).</summary>
    public int? LastRemainingSeconds { get; private set; }

    public bool Enabled => _config().Enabled;

    /// <summary>The running game is out of time (set by <see cref="Tick"/>).</summary>
    public bool Expired { get; private set; }

    public TrustedClock Clock => _clock;

    private static bool Weekend(DateOnly day) => day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    /// <summary>Call every few seconds. <paramref name="playingGame"/> is the id of the running, not paused game, or null.</summary>
    public void Tick(string? playingGame, string? runningGame = null)
    {
        _clock.Sync();
        var now = _clock.UtcNow;
        var seconds = Math.Clamp((now - _lastTick).TotalSeconds, 0, 15);
        _lastTick = now;

        if (playingGame is not null && Enabled) _ledger.Add(_clock.Today, playingGame, seconds);
        if (_ledger.Dirty && now - _lastSave >= SaveEvery) Flush();
        // Also while nothing is played: the stored time is what the clock is held to at the next start (see TrustedClock).
        else if (Enabled && now - _lastSave >= IdleSaveEvery) Flush();

        // A paused game (waiting for the parents) still counts as "out of time", it just no longer ticks.
        var game = playingGame ?? runningGame;
        if (game is null || !Enabled)
        {
            _fired.Clear();
            Expired = false;
            return;
        }

        var remaining = Status(game).RemainingSeconds;
        LastRemainingSeconds = remaining;
        if (remaining is null)
        {
            _fired.Clear();
            Expired = false;
            return;
        }
        foreach (var threshold in WarningSeconds.Where(t => remaining > t)) _fired.Remove(threshold);
        Expired = remaining <= 0;
        var crossed = WarningSeconds.Where(t => remaining <= t && !_fired.Contains(t)).ToList();
        if (crossed.Count == 0) return;
        foreach (var t in crossed) _fired.Add(t);
        var smallest = crossed.Min();
        Warning?.Invoke(smallest == 0 ? ParentalWarning.Expired : smallest <= 300 ? ParentalWarning.Minutes5 : ParentalWarning.Minutes15);
    }

    public void Flush()
    {
        _ledger.LastUtc = _clock.UtcNow;
        _ledger.Save(_clock.Today);
        _lastSave = _clock.UtcNow;
    }

    public ParentalStatus Status(string? gameId)
    {
        var c = _config();
        var today = _clock.Today;
        var used = _ledger.Seconds(today);
        var bonus = _ledger.BonusMinutes(today);
        if (!c.Enabled) return new ParentalStatus(false, null, null, true, used, null, bonus, _clock.Jumped);
        if (_ledger.Tampered) return new ParentalStatus(true, 0, "tamper", true, used, null, bonus, _clock.Jumped);

        int? remaining = null;
        string? limiter = null;
        void Consider(double seconds, string why)
        {
            var s = (int)Math.Floor(seconds);
            if (remaining is null || s < remaining) (remaining, limiter) = (s, why);
        }

        var limit = Weekend(today) ? c.WeekendMinutes : c.WeekdayMinutes;
        if (limit > 0) Consider(limit * 60.0 + bonus * 60.0 - used, "day");

        if (c.PerGameMinutes > 0 && gameId is not null)
            Consider(c.PerGameMinutes * 60.0 + bonus * 60.0 - _ledger.Seconds(today, gameId), "game");

        var inWindow = true;
        if (c.HasWindow)
        {
            var t = _clock.Local.TimeOfDay.TotalSeconds;
            var start = c.WindowStartMinute * 60.0;
            // Extra time given before midnight stretches a window that reaches past midnight (22 to 1) and nothing else:
            // in the part after midnight the window belongs to the day before and carries that day's bonus, but the
            // new day's limits (day, game) start without it.
            var carry = c.WindowEndMinute < c.WindowStartMinute && t < start ? _ledger.BonusMinutes(today.AddDays(-1)) : 0;
            var length = (((c.WindowEndMinute - c.WindowStartMinute) % 1440 + 1440) % 1440) * 60.0 + (bonus + carry) * 60.0;
            var offset = ((t - start) % 86400 + 86400) % 86400;
            inWindow = offset < Math.Min(length, 86400);
            Consider(inWindow ? length - offset : 0, "window");
        }
        // Played past the limit (the grace period before closing counts on): that is "0 left", never a negative account.
        if (remaining < 0) remaining = 0;
        return new ParentalStatus(true, remaining, limiter, inWindow, used, limit > 0 ? limit : null, bonus, _clock.Jumped);
    }

    /// <summary>May this game start now? Time rules only; locked games and age are checked by the caller.</summary>
    public StartCheck CanStart(string? gameId)
    {
        var c = _config();
        if (!c.Enabled) return new StartCheck(true, null, "");
        _clock.Sync();
        var s = Status(gameId);
        if (s.Limiter == "tamper")
            return new StartCheck(false, "tamper", Strings.Get("parental.tamper"));
        if (!s.InWindow)
            return new StartCheck(false, "window", Strings.Get("parental.window", Hhmm(c.WindowStartMinute), Hhmm(c.WindowEndMinute)));
        if (s.RemainingSeconds is < 60)
            return s.Limiter switch
            {
                "game" => new StartCheck(false, "game", Strings.Get("parental.gameLimit")),
                "window" => new StartCheck(false, "window", Strings.Get("parental.windowEnding", Hhmm(c.WindowEndMinute))),
                _ => new StartCheck(false, "day", Strings.Get("parental.dayLimit"))
            };
        return new StartCheck(true, null, "");
    }

    /// <summary>Parents add time: raises the day, game and window limits of today.</summary>
    public void Extend(int minutes)
    {
        if (minutes is not (15 or 30 or 60)) throw new ArgumentException(Strings.Get("parental.extendInvalid"));
        _clock.Sync();
        _ledger.AddBonus(_clock.Today, minutes);
        _fired.Clear();
        Expired = false;
        _lastTick = _clock.UtcNow;
        Flush();
    }

    public static string Hhmm(int minuteOfDay) => $"{minuteOfDay / 60}:{minuteOfDay % 60:00}";

    /// <summary>Same game, different entries (a Steam game and a shortcut to it) share limit and lock.</summary>
    public static string LimitKey(LibraryItem item)
    {
        if (SteamStore.AppIdOf(item) is { } id) return $"steam:{id}";
        if (item.Launch.Kind == LaunchKind.Url && item.Launch.Target is { } t && SteamRun().Match(t) is { Success: true } m) return $"steam:{m.Groups[1].Value}";
        if (!string.IsNullOrWhiteSpace(item.InstallDir)) return "dir:" + item.InstallDir.Trim().TrimEnd((char)92, '/').ToLowerInvariant();
        return item.Id;
    }

    [GeneratedRegex(@"^steam://(?:rungameid|run|launch)/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex SteamRun();

    public static bool IsLocked(ParentalConfig c, LibraryItem item, IEnumerable<LibraryItem> all)
    {
        if (c.LockedItems.Contains(item.Id)) return true;
        var key = LimitKey(item);
        return all.Any(i => c.LockedItems.Contains(i.Id) && LimitKey(i) == key);
    }

    /// <summary>
    /// Why this entry needs the parents' code, or null: "locked" (their list), "age" (rated at the limit or above),
    /// "unknown-age" (no rating known), "undetectable" (CouchHud could not see it run, so its time could not be counted).
    /// </summary>
    public static string? CodeReason(ParentalConfig c, LibraryItem item, IEnumerable<LibraryItem> all, int? age)
    {
        if (!c.Enabled) return null;
        if (IsLocked(c, item, all)) return "locked";
        var isGame = item.Type == ItemType.Game;
        if (isGame && c.AgeLimit > 0)
        {
            if (age is { } a) return a >= c.AgeLimit ? "age" : null;
            if (c.BlockUnknownAge) return "unknown-age";
        }
        if (!GameSessionTracker.CanDetect(item) && (isGame || c.CountApps)) return "undetectable";
        return null;
    }
}
