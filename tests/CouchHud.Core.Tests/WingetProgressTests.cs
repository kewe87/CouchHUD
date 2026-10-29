using CouchHud.Core.Localization;
using CouchHud.Core.Library;
using CouchHud.Core.Updates;

namespace CouchHud.Core.Tests;

public class WingetProgressTests
{
    // The shapes winget prints with stdout redirected: a spinner drawn with backspaces, bars redrawn with carriage returns, sizes
    // with the console's decimal sign. Recorded from the documented format of winget 1.x; the wording is localized, the structure is not.
    private const string EnglishAll =
        "\r   - \b\b\b\\ \b\b\b| \b\b\b/ \b\b\b- \r                                 \r" +
        "(1/2) Found Mozilla Firefox (x64 de) [Mozilla.Firefox.de] Version 131.0.2\r\n" +
        "This application is licensed to you by its owner.\r\n" +
        "Microsoft is not responsible for, nor does it grant any licenses to, third-party packages.\r\n" +
        "Downloading https://download-installer.cdn.mozilla.net/pub/firefox/releases/131.0.2/win64/de/Firefox%20Setup%20131.0.2.msi\r\n" +
        "  ██▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒  1.00 MB / 10.0 MB\r" +
        "  ███████████████▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒  5.00 MB / 10.0 MB\r" +
        "  ██████████████████████████████  10.0 MB / 10.0 MB\r\n" +
        "Successfully verified installer hash\r\n" +
        "Starting package install...\r\n" +
        "  ▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒\r\n" +
        "Successfully installed\r\n" +
        "(2/2) Found Discord [Discord.Discord] Version 1.0.9164\r\n" +
        "Downloading https://dl.discordapp.net/distro/app/stable/win/x64/1.0.9164/DiscordSetup.exe\r\n" +
        "  ███████▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒  3.50 MB / 14.0 MB\r\n";

    private const string GermanAll =
        "(1/2) Gefunden: 7-Zip 23.01 (x64) [7zip.7zip] Version 24.08\r\n" +
        "Diese Anwendung wird vom jeweiligen Besitzer an Sie lizenziert.\r\n" +
        "Herunterladen von https://www.7-zip.org/a/7z2408-x64.exe\r\n" +
        "  ██████████████▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒  0,75 MB / 1,50 MB\r\n" +
        "  ██████████████████████████████  1,50 MB / 1,50 MB\r\n" +
        "Der Installer-Hash wurde erfolgreich überprüft.\r\n" +
        "Das Paket wird installiert ...\r\n" +
        "Erfolgreich installiert\r\n";

    private static WingetProgressInfo? Last(string text, int chunk = 7)
    {
        // Chunked the way a pipe delivers it: pieces that cut a line anywhere.
        var parser = new WingetProgressParser();
        for (var i = 0; i < text.Length; i += chunk) parser.Feed(text.AsSpan(i, Math.Min(chunk, text.Length - i)));
        parser.Flush();
        return parser.Current;
    }

    private static List<WingetProgressInfo> Steps(string text)
    {
        var list = new List<WingetProgressInfo>();
        var parser = new WingetProgressParser();
        foreach (var c in text)
            if (parser.Feed([c])) list.Add(parser.Current!);
        return list;
    }

    [Fact]
    public void English_run_tells_package_counter_id_download_and_install()
    {
        var steps = Steps(EnglishAll);
        Assert.Equal(new WingetProgressInfo(1, 2, "Mozilla.Firefox.de", WingetPhase.Prepare, 0, 0, null), steps[0]);
        Assert.Contains(steps, s => s.Index == 1 && s.Phase == WingetPhase.Download && s.BytesTotal == 10 * 1024 * 1024 && s.BytesDone == 5 * 1024 * 1024);
        // Full download: the setup is running, no bytes any more.
        Assert.Contains(steps, s => s.Index == 1 && s.Phase == WingetPhase.Install && s.BytesTotal == 0);
        var last = Last(EnglishAll)!;
        Assert.Equal((2, 2, "Discord.Discord", WingetPhase.Download), (last.Index, last.Total, last.Id, last.Phase));
        Assert.Equal(14 * 1024 * 1024, last.BytesTotal);
        Assert.Equal((long)(3.5 * 1024 * 1024), last.BytesDone);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(64)]
    [InlineData(5000)]
    public void Result_does_not_depend_on_how_the_pipe_cuts_the_output(int chunk)
    {
        Assert.Equal(Last(EnglishAll, 1), Last(EnglishAll, chunk));
        Assert.Equal(Last(GermanAll, 1), Last(GermanAll, chunk));
    }

    [Fact]
    public void German_run_with_decimal_comma_works_without_knowing_a_single_german_word()
    {
        var steps = Steps(GermanAll);
        Assert.Equal("7zip.7zip", steps[0].Id);
        var download = steps.First(s => s.Phase == WingetPhase.Download && s.BytesTotal > 0);
        Assert.Equal((long)(1.5 * 1024 * 1024), download.BytesTotal);
        Assert.Equal((long)(0.75 * 1024 * 1024), download.BytesDone);
        Assert.Equal(WingetPhase.Install, Last(GermanAll)!.Phase);
    }

    [Fact]
    public void Single_package_without_counter_is_found_by_its_bracketed_id()
    {
        const string text = "Found Discord [Discord.Discord] Version 1.0.9164\nDownloading https://x.example/a.exe\n  ██▒▒  1 KB / 4 KB\n";
        var cur = Last(text)!;
        Assert.Equal(("Discord.Discord", 1, 0, WingetPhase.Download), (cur.Id, cur.Index, cur.Total, cur.Phase));
        Assert.Equal(1024, cur.BytesDone);
    }

    [Fact]
    public void Without_counter_only_the_first_bracketed_id_counts_as_a_package()
    {
        // A later line such as "[INFO] ... 5" must not become package 2.
        var cur = Last("Found A [A.A] Version 1\n[INFO] retrying in 5\nFound B [B.B] Version 2\n")!;
        Assert.Equal(("A.A", 1), (cur.Id, cur.Index));
    }

    [Fact]
    public void Bare_percent_counts_as_download_progress()
    {
        var cur = Last("(1/1) Found X [X.X] Version 1\n  ███▒▒▒  45%\n")!;
        Assert.Equal(WingetPhase.Download, cur.Phase);
        Assert.Equal(0.45, cur.Percent);
        Assert.Null(Last("(1/1) Found X [X.X] Version 1\n  900%\n")!.Percent);
    }

    [Fact]
    public void Block_bar_without_sizes_is_the_install_step_but_not_during_a_download()
    {
        Assert.Equal(WingetPhase.Install, Last("(1/1) Found X [X.X] Version 1\n  ▒▒▒▒▒▒▒▒▒▒▒▒\n")!.Phase);
        Assert.Equal(WingetPhase.Download, Last("(1/1) Found X [X.X] Version 1\nDownloading https://x.example/a\n  ▒▒▒▒▒▒▒▒▒▒▒▒\n")!.Phase);
    }

    [Fact]
    public void Spinner_noise_and_escape_sequences_change_nothing()
    {
        var parser = new WingetProgressParser();
        Assert.False(parser.Feed("\r   - \b\b\b\\ \b\b\b| \b\b\b/ \r"));
        Assert.False(parser.Feed("\u001b[?25l\u001b[2K\r\n"));
        Assert.Null(parser.Current);
        Assert.True(parser.Feed("\u001b[1m(1/3)\u001b[0m Found X [X.X] Version 1\n"));
        Assert.Equal(("X.X", 3), (parser.Current!.Id, parser.Current.Total));
    }

    [Fact]
    public void Text_before_a_package_line_and_the_upgrade_table_are_not_progress()
    {
        Assert.Null(Last("Name  Id  Version  Available  Source\n-----\nFoo  Foo.Foo  1.0  2.0  winget\n10 MB / 20 MB\n"));
    }

    [Theory]
    [InlineData("(0/2) Found X [X.X] Version 1")]
    [InlineData("(3/2) Found X [X.X] Version 1")]
    [InlineData("(1/99999) Found X [X.X] Version 1")]
    [InlineData("(1/1000) Found X [X.X] Version 1")]
    [InlineData("(1/2) Found X without id")]
    public void Nonsense_counters_are_ignored(string line) => Assert.Null(Last(line + "\n"));

    [Theory]
    [InlineData("99999999999999999999 MB / 5 MB")]
    [InlineData("5 MB / 0 MB")]
    [InlineData("5 XB / 6 XB")]
    [InlineData("1.2.3.4 MB / 9 MB")]
    public void Nonsense_sizes_are_ignored(string size)
    {
        var cur = Last("(1/1) Found X [X.X] Version 1\n" + size + "\n")!;
        Assert.Equal(WingetPhase.Prepare, cur.Phase);
        Assert.Equal(0, cur.BytesTotal);
    }

    [Fact]
    public void Hostile_output_is_bounded_and_never_throws()
    {
        var parser = new WingetProgressParser();
        // A line of a million characters is cut; the package line after it still works.
        parser.Feed(new string('x', 1_000_000));
        parser.Feed("\n");
        parser.Feed("(1/2) Found " + new string('y', 1_000_000) + " [" + new string('z', 500) + "]\n");
        Assert.Null(parser.Current);
        parser.Feed("(1/2) Found N [" + new string('z', WingetProgressParser.MaxId) + "] Version 1\n");
        Assert.Equal(WingetProgressParser.MaxId, parser.Current!.Id!.Length);
        // A backspace storm and control characters.
        parser.Feed(new string('\b', 100_000));
        parser.Feed("\u0000\u0001\u001b\u001b[\u001b]0;title");
        parser.Flush();
        Assert.Equal(1, parser.Current.Index);
    }

    [Fact]
    public void A_line_bigger_than_the_cap_does_not_hide_the_next_one()
    {
        var parser = new WingetProgressParser();
        parser.Feed(new string('a', WingetProgressParser.MaxLine + 500) + "\r(1/1) Found X [X.X] Version 1\r");
        Assert.Equal("X.X", parser.Current!.Id);
    }

    // ---------------------------------------------------------------- reporter

    private sealed class Clock { public DateTime Now = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc); }

    private (WingetProgressReporter Reporter, List<AppUpgradeProgress> Seen, Clock Clock) Make(string? id, int pending = 5, Dictionary<string, string>? names = null)
    {
        var seen = new List<AppUpgradeProgress>();
        var clock = new Clock();
        return (new WingetProgressReporter(id, names ?? new() { ["X.X"] = "Xylophone" }, pending, seen.Add, () => clock.Now, (_, tick) => _ticks.Add(tick)), seen, clock);
    }

    /// <summary>The trailing-edge timers the reporters asked for; a test runs them by hand.</summary>
    private readonly List<Action> _ticks = [];

    [Fact]
    public void Reporter_sends_the_latest_dropped_frame_when_the_window_ends()
    {
        var (r, seen, clock) = Make("X.X");
        r.Feed("Found X [X.X] Version 1\n");
        r.Feed("Downloading https://x.example/a\n");
        var before = seen.Count;
        for (var i = 1; i <= 5; i++)
        {
            clock.Now = clock.Now.AddMilliseconds(10);
            r.Feed($"  ██▒▒  {i} KB / 100 KB\r");
        }
        // The first frame went out at once or was dropped, the rest were dropped: one timer, and it sends the newest state.
        var sent = seen.Count;
        Assert.Single(_ticks);
        clock.Now = clock.Now.AddMilliseconds(250);
        _ticks[0]();
        Assert.Equal(sent + 1, seen.Count);
        Assert.Equal(5 * 1024, seen[^1].BytesDone);
        // Nothing left: a second tick sends nothing, and a disposed reporter stays quiet.
        _ticks[0]();
        Assert.Equal(sent + 1, seen.Count);
        r.Feed("  ██▒▒  6 KB / 100 KB\r");
        r.Dispose();
        clock.Now = clock.Now.AddMilliseconds(250);
        _ticks[0]();
        r.Feed("  ██▒▒  7 KB / 100 KB\r");
        Assert.True(seen.Count >= before);
        Assert.NotEqual(7 * 1024, seen[^1].BytesDone);
    }

    [Fact]
    public void Reporter_never_cuts_a_surrogate_pair_at_the_name_limit()
    {
        var name = new string('a', WingetProgressReporter.MaxNameLength - 1) + "\U0001F600" + "tail";
        var (r, seen, _) = Make(null, names: new() { ["X.X"] = name });
        r.Feed("(1/1) Found X [X.X] Version 1\n");
        var shown = seen.Single().Name;
        Assert.Equal(WingetProgressReporter.MaxNameLength - 1, shown.Length);
        Assert.False(char.IsHighSurrogate(shown[^1]));
        // A pair that ends exactly at the limit stays whole.
        var whole = new string('a', WingetProgressReporter.MaxNameLength - 2) + "\U0001F600";
        Assert.Equal(whole, WingetProgressReporter.Cut(whole + "x", WingetProgressReporter.MaxNameLength));
    }

    [Fact]
    public async Task A_throwing_callback_never_stops_the_reading()
    {
        // A full pipe would stall winget: the stream is read to its end, the callback is called no more after it threw, the error is reported once.
        var text = string.Concat(Enumerable.Repeat("(1/1) Found X [X.X] Version 1\r\n", 5000));
        var calls = 0;
        var errors = new List<Exception>();
        var kept = await OutputPump.ReadAsync(new StringReader(text), _ => { calls++; throw new InvalidOperationException("boom"); }, keep: true, maxKept: 1 << 20, errors.Add);
        Assert.Equal(1, calls);
        Assert.Single(errors);
        Assert.Equal(text, kept);
    }

    [Fact]
    public async Task The_pump_caps_what_it_keeps_and_ends_with_an_empty_piece()
    {
        var pieces = new List<int>();
        var kept = await OutputPump.ReadAsync(new StringReader(new string('x', 10_000)), p => pieces.Add(p.Length), keep: true, maxKept: 2048);
        Assert.InRange(kept.Length, 2048, 2048 + 1024);
        Assert.Equal(0, pieces[^1]);
        Assert.Equal(10_000, pieces.Sum());
        Assert.Equal("", await OutputPump.ReadAsync(new StringReader("abc"), null, keep: false, maxKept: 10));
    }

    [Fact]
    public void The_timeout_message_exists_in_both_languages()
    {
        try
        {
            Strings.Use("de");
            Assert.Equal("winget hat nicht rechtzeitig geantwortet", Strings.Get("update.timeout"));
            Strings.Use("en");
            Assert.Equal("winget did not answer in time", Strings.Get("update.timeout"));
        }
        finally { Strings.Use("de"); }
    }

    [Fact]
    public void Reporter_uses_known_names_and_fills_total_from_the_pending_count_or_one()
    {
        var (all, seenAll, _) = Make(null, pending: 4);
        all.Feed("Found X [X.X] Version 1\n");
        Assert.Equal(new AppUpgradeProgress(1, 4, "Xylophone", WingetPhase.Prepare, 0, 0, null), seenAll.Single());
        var (one, seenOne, _) = Make("X.X");
        one.Feed("(1/9) Found X [X.X] Version 1\n");
        Assert.Equal(1, seenOne.Single().Total);
        var (counter, seenCounter, _) = Make(null, pending: 4);
        counter.Feed("(2/3) Found Q [Q.Q] Version 1\n");
        Assert.Equal((2, 3, "Q.Q"), (seenCounter[0].Index, seenCounter[0].Total, seenCounter[0].Name));
    }

    [Fact]
    public void Reporter_caps_unknown_names()
    {
        var (r, seen, _) = Make(null);
        r.Feed("(1/1) Found Q [" + new string('q', 120) + "] Version 1\n");
        Assert.Equal(WingetProgressReporter.MaxNameLength, seen.Single().Name.Length);
    }

    [Fact]
    public void Reporter_throttles_to_four_a_second_but_passes_changes_of_phase_at_once()
    {
        var (r, seen, clock) = Make("X.X");
        r.Feed("Found X [X.X] Version 1\n");
        r.Feed("Downloading https://x.example/a\n");
        Assert.Equal(2, seen.Count); // package, then phase download
        for (var i = 1; i <= 50; i++)
        {
            clock.Now = clock.Now.AddMilliseconds(10);
            r.Feed($"  ██▒▒  {i} KB / 100 KB\r");
        }
        // 500 ms of frames: the first came at once, then one per 250 ms at most.
        Assert.InRange(seen.Count, 3, 5);
        clock.Now = clock.Now.AddMilliseconds(1);
        r.Feed("  ██▒▒  100 KB / 100 KB\r");
        Assert.Equal(WingetPhase.Install, seen[^1].Phase);
    }

    [Fact]
    public void Reporter_reports_the_last_pending_piece_when_the_output_ends()
    {
        var (r, seen, _) = Make("X.X");
        r.Feed("Found X [X.X] Version 1");
        Assert.Empty(seen);
        r.Feed(ReadOnlySpan<char>.Empty);
        Assert.Single(seen);
    }

    // ---------------------------------------------------------------- the line on the page

    [Fact]
    public void Step_lives_on_the_line_until_it_ends_and_unknown_progress_clears_the_bar()
    {
        var center = new DownloadCenter();
        center.Begin("update:apps", DownloadKind.App, "winget", "All apps");
        center.Report("update:apps", 0.4, state: DownloadState.Downloading, bytesDone: 4, bytesTotal: 10, step: new UpdateStep(1, 2, "A", "download"));
        var e = center.Entries().Single();
        Assert.Equal(new UpdateStep(1, 2, "A", "download"), e.Step);
        Assert.Equal(0.4, e.Progress);
        center.Report("update:apps", state: DownloadState.Installing, bytesDone: 0, bytesTotal: 0, step: new UpdateStep(1, 2, "A", "install"), unknownProgress: true);
        e = center.Entries().Single();
        Assert.Null(e.Progress);
        Assert.Equal(0, e.BytesTotal);
        Assert.Equal("install", e.Step!.Phase);
        center.Complete("update:apps", false, "failed");
        Assert.Null(center.Entries().Single().Step);
    }

    [Fact]
    public void A_report_after_the_line_ended_is_ignored()
    {
        var center = new DownloadCenter();
        center.Begin("update:apps", DownloadKind.App, "winget", "All apps");
        center.Complete("update:apps", true);
        center.Report("update:apps", 0.5, step: new UpdateStep(1, 1, "A", "download"));
        Assert.Null(center.Entries().Single().Step);
    }
}
