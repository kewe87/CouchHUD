using CouchHud.Core.Library;
using CouchHud.Core.Parental;
using CouchHud.Sdk;
using Xunit;

namespace CouchHud.Core.Tests;

public class ParentalTests
{
    /// <summary>A controllable machine: system clock and monotonic counter move together unless a test sets the clock.</summary>
    private sealed class Machine
    {
        // 2026-10-19 is a Monday. UTC offset 0 keeps local = UTC.
        public DateTimeOffset System = new(2026, 10, 19, 15, 0, 0, TimeSpan.Zero);
        public long Mono = 1_000_000;
        public void Advance(TimeSpan t)
        {
            System += t;
            Mono += (long)t.TotalMilliseconds;
        }

        public TrustedClock Clock(DateTimeOffset? persisted = null) => new(() => System, () => Mono, _ => TimeSpan.Zero, persisted);
    }

    private static (ParentalService Service, Machine M, PlayLedger Ledger, ParentalConfig Config, List<ParentalWarning> Warnings) Make(Action<ParentalConfig>? setup = null, string? file = null, Machine? machine = null)
    {
        var m = machine ?? new Machine();
        var config = new ParentalConfig { Enabled = true, WeekdayMinutes = 60, WeekendMinutes = 120 };
        setup?.Invoke(config);
        var ledger = new PlayLedger(file);
        var service = new ParentalService(() => config, ledger, m.Clock(ledger.LastUtc));
        var warnings = new List<ParentalWarning>();
        service.Warning += warnings.Add;
        return (service, m, ledger, config, warnings);
    }

    private static void Near(double expected, double actual, double tolerance) => Assert.InRange(actual, expected - tolerance, expected + tolerance);

    private static void Play(ParentalService s, Machine m, TimeSpan total, string game = "g1")
    {
        for (var left = total; left > TimeSpan.Zero; left -= TimeSpan.FromSeconds(3))
        {
            m.Advance(TimeSpan.FromSeconds(3));
            s.Tick(game);
        }
    }

    [Fact]
    public void Counts_play_time_and_reports_the_remaining_time()
    {
        var (s, m, _, _, _) = Make();
        Play(s, m, TimeSpan.FromMinutes(20));
        var status = s.Status("g1");
        Near(40 * 60, status.RemainingSeconds!.Value, 5);
        Assert.Equal("day", status.Limiter);
    }

    [Fact]
    public void Warns_at_15_and_5_minutes_and_when_the_time_is_up_exactly_once()
    {
        var (s, m, _, _, w) = Make();
        Play(s, m, TimeSpan.FromMinutes(61));
        Assert.Equal([ParentalWarning.Minutes15, ParentalWarning.Minutes5, ParentalWarning.Expired], w);
        Assert.True(s.Expired);
    }

    [Fact]
    public void Starting_with_little_time_left_gives_only_the_closest_warning()
    {
        var (s, m, led, _, w) = Make();
        led.Add(DateOnly.FromDateTime(m.System.DateTime), "other", 57 * 60);
        Play(s, m, TimeSpan.FromSeconds(3));
        Assert.Equal([ParentalWarning.Minutes5], w);
    }

    [Fact]
    public void Weekend_has_its_own_limit()
    {
        var m = new Machine { System = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero) }; // Saturday
        var (s, _, _, _, _) = Make(machine: m);
        Assert.Equal(120 * 60, s.Status(null).RemainingSeconds);
    }

    [Fact]
    public void Time_played_past_the_limit_shows_as_zero_left_and_stays_up_fail_closed()
    {
        // 2 minutes, played on through the grace period before the game is closed: the account is overdrawn by ~50 s.
        var (s, m, _, _, _) = Make(c => c.WeekdayMinutes = 2);
        Play(s, m, TimeSpan.FromSeconds(170));
        Assert.True(s.Expired);
        var running = s.Status("g1");
        Assert.Equal(0, running.RemainingSeconds);
        Assert.True(running.TimeUp);

        // The game is gone: the service flag (running game) falls back, the account itself stays used up.
        m.Advance(TimeSpan.FromSeconds(3));
        s.Tick(null);
        Assert.False(s.Expired);
        var idle = s.Status(null);
        Assert.Equal(0, idle.RemainingSeconds);
        Assert.Equal("day", idle.Limiter);
        Assert.True(idle.TimeUp);
        Assert.False(s.CanStart("g2").Ok);
        Assert.Equal("day", s.CanStart("g2").Reason);

        // Extra time is counted from the overdrawn account, not from zero: 15 minutes give ~14 minutes.
        s.Extend(15);
        var after = s.Status(null);
        Assert.InRange(after.RemainingSeconds!.Value, 14 * 60 - 5, 15 * 60 - 40);
        Assert.False(after.TimeUp);
    }

    [Fact]
    public void Time_up_is_false_without_a_limit_and_true_outside_the_window()
    {
        var (free, _, _, _, _) = Make(c => c.WeekdayMinutes = 0);
        Assert.False(free.Status("g1").TimeUp);
        var (off, _, _, _, _) = Make(c => c.Enabled = false);
        Assert.False(off.Status("g1").TimeUp);
        var (window, _, _, _, _) = Make(c => (c.WeekdayMinutes, c.WindowStartMinute, c.WindowEndMinute) = (0, 8 * 60, 9 * 60));
        var st = window.Status(null);
        Assert.False(st.InWindow);
        Assert.Equal(0, st.RemainingSeconds);
        Assert.True(st.TimeUp);
    }

    [Fact]
    public void Zero_minutes_means_no_limit()
    {
        var (s, m, _, _, w) = Make(c => c.WeekdayMinutes = 0);
        Play(s, m, TimeSpan.FromHours(5));
        Assert.Null(s.Status("g1").RemainingSeconds);
        Assert.Empty(w);
    }

    [Fact]
    public void Time_window_blocks_the_start_outside_and_runs_out_at_the_end()
    {
        var (s, m, _, _, w) = Make(c => (c.WeekdayMinutes, c.WindowStartMinute, c.WindowEndMinute) = (0, 14 * 60, 15 * 60 + 10));
        Assert.True(s.CanStart("g1").Ok);
        Assert.Equal(10 * 60, s.Status("g1").RemainingSeconds);
        Play(s, m, TimeSpan.FromMinutes(11));
        Assert.True(s.Expired);
        Assert.Contains(ParentalWarning.Expired, w);
        Assert.Equal("window", s.CanStart("g1").Reason);
        m.Advance(TimeSpan.FromHours(18)); // next morning, before the window
        Assert.Equal("window", s.CanStart("g1").Reason);
    }

    [Fact]
    public void Window_over_midnight_works()
    {
        var m = new Machine { System = new DateTimeOffset(2026, 10, 19, 23, 0, 0, TimeSpan.Zero) };
        var (s, _, _, _, _) = Make(c => (c.WeekdayMinutes, c.WindowStartMinute, c.WindowEndMinute) = (0, 22 * 60, 1 * 60), machine: m);
        Assert.True(s.CanStart("g").Ok);
        Assert.Equal(2 * 3600, s.Status("g").RemainingSeconds);
        m.Advance(TimeSpan.FromHours(3)); // 02:00
        Assert.False(s.CanStart("g").Ok);
    }

    [Fact]
    public void Per_game_limit_counts_each_game_separately()
    {
        var (s, m, _, _, _) = Make(c => (c.WeekdayMinutes, c.PerGameMinutes) = (0, 30));
        Play(s, m, TimeSpan.FromMinutes(30), "a");
        Assert.Equal("game", s.CanStart("a").Reason);
        Assert.True(s.CanStart("b").Ok);
    }

    [Fact]
    public void Start_is_refused_when_the_day_is_used_up()
    {
        var (s, m, _, _, _) = Make();
        Play(s, m, TimeSpan.FromMinutes(60));
        var check = s.CanStart("x");
        Assert.False(check.Ok);
        Assert.Equal("day", check.Reason);
    }

    [Fact]
    public void Extension_adds_time_and_warns_again_later()
    {
        var (s, m, _, _, w) = Make();
        Play(s, m, TimeSpan.FromMinutes(61));
        Assert.True(s.Expired);
        s.Extend(30);
        Assert.False(s.Expired);
        Near(29 * 60, s.Status("g1").RemainingSeconds!.Value, 10);
        w.Clear();
        Play(s, m, TimeSpan.FromMinutes(20)); // ~9 min left
        Assert.Equal([ParentalWarning.Minutes15], w);
        Play(s, m, TimeSpan.FromMinutes(5));
        Assert.Contains(ParentalWarning.Minutes5, w);
    }

    [Fact]
    public void Extension_also_moves_the_end_of_the_window()
    {
        var (s, m, _, _, _) = Make(c => (c.WeekdayMinutes, c.WindowStartMinute, c.WindowEndMinute) = (0, 14 * 60, 15 * 60 + 5));
        Play(s, m, TimeSpan.FromMinutes(6));
        Assert.True(s.Expired);
        s.Extend(15);
        Assert.True(s.CanStart("g1").Ok);
        Assert.InRange(s.Status("g1").RemainingSeconds!.Value, 13 * 60, 15 * 60);
    }

    [Fact]
    public void Only_15_30_and_60_minutes_can_be_added()
    {
        var (s, _, _, _, _) = Make();
        Assert.Throws<ArgumentException>(() => s.Extend(10));
    }

    [Fact]
    public void Setting_the_clock_back_does_not_give_back_time()
    {
        var (s, m, _, _, _) = Make();
        Play(s, m, TimeSpan.FromMinutes(40));
        m.System -= TimeSpan.FromDays(3); // child sets the date back
        Play(s, m, TimeSpan.FromMinutes(1));
        var status = s.Status("g1");
        Assert.True(status.ClockJumped);
        Near(19 * 60, status.RemainingSeconds!.Value, 10);
    }

    [Fact]
    public void Setting_the_clock_forward_while_running_does_not_start_a_fresh_day()
    {
        var (s, m, _, _, _) = Make();
        Play(s, m, TimeSpan.FromMinutes(59));
        m.System += TimeSpan.FromDays(1);
        Play(s, m, TimeSpan.FromMinutes(2));
        Assert.True(s.Expired);
    }

    [Fact]
    public void A_clock_set_back_across_a_restart_is_ignored()
    {
        var file = Path.Combine(Path.GetTempPath(), $"couchhud-ledger-{Guid.NewGuid():N}.json");
        try
        {
            var (s, m, _, _, _) = Make(file: file);
            Play(s, m, TimeSpan.FromMinutes(50));
            s.Flush();

            var rebooted = new Machine { System = m.System - TimeSpan.FromDays(5), Mono = 5000 };
            var (s2, _, _, _, _) = Make(file: file, machine: rebooted);
            Near(10 * 60, s2.Status("g1").RemainingSeconds!.Value, 15);
            Assert.True(s2.Clock.Jumped);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Used_time_survives_a_restart()
    {
        var file = Path.Combine(Path.GetTempPath(), $"couchhud-ledger-{Guid.NewGuid():N}.json");
        try
        {
            var (s, m, _, _, _) = Make(file: file);
            Play(s, m, TimeSpan.FromMinutes(25));
            s.Extend(15);
            var (s2, _, _, _, _) = Make(file: file, machine: m);
            Near(50 * 60, s2.Status("g1").RemainingSeconds!.Value, 15);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Playing_through_midnight_starts_the_next_day_with_a_fresh_account()
    {
        var m = new Machine { System = new DateTimeOffset(2026, 10, 19, 23, 40, 0, TimeSpan.Zero) };
        var (s, _, led, _, w) = Make(machine: m);
        Play(s, m, TimeSpan.FromMinutes(30)); // 23:40 -> 00:10
        Near(20 * 60, led.Seconds(new DateOnly(2026, 10, 19)), 10);
        Near(10 * 60, led.Seconds(new DateOnly(2026, 10, 20)), 10);
        Near(50 * 60, s.Status("g1").RemainingSeconds!.Value, 10);
        Assert.False(s.Expired);
        Assert.DoesNotContain(ParentalWarning.Expired, w);
    }

    [Fact]
    public void The_end_of_the_window_ends_a_running_session()
    {
        var m = new Machine { System = new DateTimeOffset(2026, 10, 19, 19, 50, 0, TimeSpan.Zero) };
        var (s, _, _, _, w) = Make(c => (c.WeekdayMinutes, c.WindowStartMinute, c.WindowEndMinute) = (0, 14 * 60, 20 * 60), machine: m);
        Play(s, m, TimeSpan.FromMinutes(11));
        Assert.Contains(ParentalWarning.Expired, w);
    }

    [Fact]
    public void A_disabled_profile_counts_nothing_and_blocks_nothing()
    {
        var (s, m, led, _, w) = Make(c => c.Enabled = false);
        Play(s, m, TimeSpan.FromHours(3));
        Assert.True(s.CanStart("g").Ok);
        Assert.Equal(0, led.Seconds(DateOnly.FromDateTime(m.System.DateTime)));
        Assert.Empty(w);
    }

    private static LibraryItem Item(string id, ItemType type = ItemType.Game, string? dir = null, string? launch = null) => new()
    {
        Id = id,
        Source = id.Split(':')[0],
        Name = id,
        Type = type,
        InstallDir = dir,
        Launch = new LaunchTarget(launch is null ? LaunchKind.Exe : LaunchKind.Url, launch ?? "x")
    };

    private static readonly string GamesDir = Path.Combine(Path.GetTempPath(), "couchhud-parental", "Games");

    [Fact]
    public void Locked_games_and_age_limit_need_the_parents_code()
    {
        var c = new ParentalConfig { Enabled = true, MaxAge = 16, LockedItems = ["steam:1"] };
        var one = Item("steam:1");
        var two = Item("steam:2");
        IReadOnlyList<LibraryItem> all = [one, two];
        Assert.Equal("locked", ParentalService.CodeReason(c, one, all, 0));
        Assert.Equal("age", ParentalService.CodeReason(c, two, all, 18));
        Assert.Equal("age", ParentalService.CodeReason(c, two, all, 16));
        Assert.Null(ParentalService.CodeReason(c, two, all, 12));
        c.Enabled = false;
        Assert.Null(ParentalService.CodeReason(c, one, all, 18));
    }

    [Fact]
    public void Unknown_age_needs_the_code_unless_the_option_is_off()
    {
        var c = new ParentalConfig { Enabled = true, MaxAge = 12 };
        var game = Item("steam:2");
        Assert.Equal("unknown-age", ParentalService.CodeReason(c, game, [game], null));
        Assert.Null(ParentalService.CodeReason(c, game, [game], 0)); // rated "from 0"
        c.BlockUnknownAge = false;
        Assert.Null(ParentalService.CodeReason(c, game, [game], null));
        c.MaxAge = 0;
        c.BlockUnknownAge = true;
        Assert.Null(ParentalService.CodeReason(c, game, [game], null)); // no age limit, no age question
    }

    [Fact]
    public void Entries_whose_process_cannot_be_seen_need_the_code_when_they_count()
    {
        var c = new ParentalConfig { Enabled = true };
        var web = Item("custom:yt", ItemType.App, launch: "https://youtube.com");
        var game = Item("custom:g", ItemType.Game, launch: "https://x");
        var seen = Item("custom:ok", ItemType.App, dir: GamesDir);
        Assert.Equal("undetectable", ParentalService.CodeReason(c, web, [web], null));
        Assert.Equal("undetectable", ParentalService.CodeReason(c, game, [game], null));
        Assert.Null(ParentalService.CodeReason(c, seen, [seen], null));
        c.CountApps = false;
        Assert.Null(ParentalService.CodeReason(c, web, [web], null)); // apps are free
        Assert.Equal("undetectable", ParentalService.CodeReason(c, game, [game], null));
    }

    [Fact]
    public void Duplicate_entries_of_one_game_share_limit_and_lock()
    {
        var steam = Item("steam:730");
        var shortcut = Item("custom:cs", launch: "steam://rungameid/730");
        var folderA = Item("custom:a", dir: GamesDir + "\\Foo");
        var folderB = Item("custom:b", dir: GamesDir + "\\foo\\");
        Assert.Equal(ParentalService.LimitKey(steam), ParentalService.LimitKey(shortcut));
        Assert.Equal(ParentalService.LimitKey(folderA), ParentalService.LimitKey(folderB));
        var c = new ParentalConfig { Enabled = true, LockedItems = ["steam:730"] };
        Assert.True(ParentalService.IsLocked(c, shortcut, [steam, shortcut]));
        Assert.False(ParentalService.IsLocked(c, folderA, [steam, folderA]));
    }

    [Fact]
    public void Age_unknown_and_zero_are_told_apart()
    {
        static int? Age(string json)
        {
            var data = System.Text.Json.JsonDocument.Parse(json).RootElement;
            return AgeRatings.Effective(AgeRatings.Parse(data), AgeRatings.ParseRequiredAge(data), AgeRatings.Usk)?.Age;
        }
        Assert.Equal(16, Age("""{"required_age":18,"ratings":{"usk":{"rating":"16"}}}"""));
        Assert.Equal(0, Age("""{"ratings":{"usk":{"rating":"0"}}}"""));
        Assert.Equal(18, Age("""{"required_age":"18"}"""));
        Assert.Null(Age("""{"name":"x"}"""));
        Assert.Null(Age("""{"required_age":0}"""));
        Assert.Equal(12, Age("""{"required_age":12}"""));
    }

    [Fact]
    public void The_warning_knows_the_real_minutes_left()
    {
        var (s, m, led, _, w) = Make();
        led.Add(DateOnly.FromDateTime(m.System.DateTime), "other", 51 * 60); // 9 minutes left
        Play(s, m, TimeSpan.FromSeconds(3));
        Assert.Equal([ParentalWarning.Minutes15], w); // the 15 minute warning, but the text says 9
        Assert.InRange(s.LastRemainingSeconds!.Value, 8 * 60, 9 * 60);
    }

    [Fact]
    public void Extra_time_survives_midnight_for_a_window_that_crosses_it()
    {
        var m = new Machine { System = new DateTimeOffset(2026, 10, 19, 23, 40, 0, TimeSpan.Zero) };
        var (s, _, _, _, _) = Make(c => (c.WeekdayMinutes, c.WindowStartMinute, c.WindowEndMinute) = (0, 22 * 60, 0), machine: m);
        Play(s, m, TimeSpan.FromMinutes(21)); // 00:01, window ended at 00:00
        Assert.True(s.Expired);
        // Fresh start of the story: extend before midnight.
        var m2 = new Machine { System = new DateTimeOffset(2026, 10, 19, 23, 50, 0, TimeSpan.Zero) };
        var (s2, _, _, _, _) = Make(c => (c.WeekdayMinutes, c.WindowStartMinute, c.WindowEndMinute) = (0, 22 * 60, 0), machine: m2);
        s2.Extend(30); // window now until 00:30
        Play(s2, m2, TimeSpan.FromMinutes(15)); // 00:05
        Assert.False(s2.Expired);
        Assert.InRange(s2.Status("g1").RemainingSeconds!.Value, 24 * 60, 25 * 60);
    }

    [Fact]
    public void A_changed_play_time_file_counts_the_day_as_used_up()
    {
        var file = Path.Combine(Path.GetTempPath(), $"couchhud-mac-{Guid.NewGuid():N}.json");
        var key = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 };
        try
        {
            var m = new Machine();
            var ledger = new PlayLedger(file, key);
            ledger.Add(DateOnly.FromDateTime(m.System.DateTime), "g", 600);
            ledger.Save(DateOnly.FromDateTime(m.System.DateTime));
            Assert.False(new PlayLedger(file, key, expectExisting: true).Tampered);

            File.WriteAllText(file, File.ReadAllText(file).Replace("600", "1")); // the child edits the number
            var tampered = new PlayLedger(file, key, expectExisting: true);
            Assert.True(tampered.Tampered);

            var service = new ParentalService(() => new ParentalConfig { Enabled = true }, tampered, m.Clock());
            Assert.Equal("tamper", service.CanStart("g").Reason);
            Assert.Equal(0, service.Status("g").RemainingSeconds);

            tampered.Reset(); // parents accept
            Assert.True(service.CanStart("g").Ok);

            File.Delete(file);
            File.Delete(file + ".bak");
            Assert.True(new PlayLedger(file, key, expectExisting: true).Tampered); // deleted, with its copy
            Assert.False(new PlayLedger(file, key, expectExisting: false).Tampered); // first run
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void The_clock_adopts_the_system_time_only_when_asked()
    {
        var m = new Machine();
        var clock = m.Clock();
        m.System += TimeSpan.FromDays(1); // jump forward without elapsed time
        clock.Sync();
        Assert.True(clock.Jumped);
        Assert.True(clock.UtcNow < m.System - TimeSpan.FromHours(23));
        clock.AcceptSystemTime(); // confirmed by a web server
        Assert.False(clock.Jumped);
        Assert.Equal(m.System, clock.UtcNow);

        m.System -= TimeSpan.FromDays(2); // backward
        clock.Sync();
        clock.AcceptSystemTime(); // not forward: not adopted
        Assert.True(clock.Jumped);
        clock.AcceptSystemTime(force: true); // parents say it is right
        Assert.False(clock.Jumped);
    }

    [Fact]
    public void The_day_follows_the_stored_zone_not_the_one_windows_has_now()
    {
        var m = new Machine { System = new DateTimeOffset(2026, 10, 19, 22, 30, 0, TimeSpan.Zero) };
        var zone = TimeSpan.FromHours(1);
        var clock = new TrustedClock(() => m.System, () => m.Mono, _ => zone);
        Assert.Equal(new DateOnly(2026, 10, 19), clock.Today.AddDays(-1).AddDays(1));
        Assert.Equal(23, clock.Local.Hour);
        zone = TimeSpan.FromHours(1); // Windows zone changes do not reach the clock: it asks the stored zone
        Assert.Equal(23, clock.Local.Hour);
    }
}
