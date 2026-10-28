using CouchHud.Core.Library;

namespace CouchHud.Core.Tests;

/// <summary>The downloads page: snapshots of a client, CouchHUD's own activities and installs asked for in a client.</summary>
public class DownloadCenterTests
{
    private DateTimeOffset _now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly DownloadCenter _center;
    private readonly List<DownloadEntry> _finished = [];
    private int _changes;

    public DownloadCenterTests()
    {
        _center = new DownloadCenter(() => _now);
        _center.Finished += e => _finished.Add(e);
        _center.Changed += () => _changes++;
    }

    private DownloadEntry Steam(string appId, SteamDownloadState state, long done = 50, long total = 100, bool update = false) =>
        DownloadCenter.FromSteam(new SteamDownload(appId, $"Spiel {appId}", state, total > 0 ? (double)done / total : 0, done, total, update), _now.ToUnixTimeMilliseconds());

    [Fact]
    public void Steam_lines_map_states_and_progress()
    {
        Assert.Equal(DownloadState.Downloading, Steam("1", SteamDownloadState.Downloading).State);
        Assert.Equal(DownloadState.Paused, Steam("1", SteamDownloadState.Paused).State);
        Assert.Equal(DownloadState.Queued, Steam("1", SteamDownloadState.Queued).State);
        Assert.Equal(0.5, Steam("1", SteamDownloadState.Downloading).Progress);
        // Steam has not told the size yet: no bar instead of 0 %.
        Assert.Null(Steam("1", SteamDownloadState.Downloading, 0, 0).Progress);
        Assert.Equal("steam:1", Steam("1", SteamDownloadState.Downloading).Id);
    }

    [Fact]
    public void A_line_seen_loading_that_goes_and_is_installed_is_done_once()
    {
        _center.ApplySnapshot("steam", [Steam("10", SteamDownloadState.Downloading)], _ => false);
        _center.ApplySnapshot("steam", [], id => id == "steam:10");
        var done = Assert.Single(_finished);
        Assert.Equal(DownloadState.Done, done.State);
        Assert.Equal(1, done.Progress);
        _center.ApplySnapshot("steam", [], _ => true);
        Assert.Single(_finished);
        Assert.Equal(DownloadState.Done, Assert.Single(_center.Entries()).State);
    }

    [Fact]
    public void A_line_that_goes_without_an_install_was_cancelled()
    {
        _center.ApplySnapshot("steam", [Steam("10", SteamDownloadState.Downloading)], _ => false);
        _center.ApplySnapshot("steam", [], _ => false);
        Assert.Empty(_finished);
        Assert.Empty(_center.Entries());
    }

    [Fact]
    public void A_queued_update_that_disappears_is_not_announced()
    {
        // Steam lists pending updates it never started; when they vanish nothing was downloaded here.
        _center.ApplySnapshot("steam", [Steam("10", SteamDownloadState.Queued, update: true)], _ => true);
        _center.ApplySnapshot("steam", [], _ => true);
        Assert.Empty(_finished);
    }

    [Fact]
    public void The_same_snapshot_again_is_no_change()
    {
        _center.ApplySnapshot("steam", [Steam("10", SteamDownloadState.Downloading)], _ => false);
        var before = _changes;
        _now = _now.AddSeconds(5);
        _center.ApplySnapshot("steam", [Steam("10", SteamDownloadState.Downloading)], _ => false);
        Assert.Equal(before, _changes);
        _center.ApplySnapshot("steam", [Steam("10", SteamDownloadState.Downloading, 60)], _ => false);
        Assert.Equal(before + 1, _changes);
    }

    [Fact]
    public void Own_activity_runs_reports_and_ends()
    {
        _center.Begin("update:windows", DownloadKind.Windows, "windows", "Windows-Updates");
        _center.Report("update:windows", 1.7);
        var line = Assert.Single(_center.Entries());
        Assert.Equal(DownloadState.Downloading, line.State);
        Assert.Equal(1, line.Progress);
        _center.Report("unknown", 0.5);
        _center.Complete("update:windows", ok: false, "Fehler 42");
        var failed = Assert.Single(_finished);
        Assert.Equal(DownloadState.Failed, failed.State);
        Assert.Equal("Fehler 42", failed.Detail);
        // A second complete of the same id does nothing.
        _center.Complete("update:windows", ok: true);
        Assert.Single(_finished);
        // Begin again: the old finished line makes room for the running one.
        _center.Begin("update:windows", DownloadKind.Windows, "windows", "Windows-Updates");
        Assert.Equal(DownloadState.Downloading, Assert.Single(_center.Entries()).State);
    }

    [Fact]
    public void Steam_request_waits_and_is_taken_over_by_the_clients_line()
    {
        _center.Request("steam:20", "steam", "Spiel 20", reportsProgress: true);
        Assert.Equal(DownloadState.Waiting, Assert.Single(_center.Entries()).State);
        Assert.True(_center.HasRequests);
        _center.ApplySnapshot("steam", [Steam("20", SteamDownloadState.Queued)], _ => false);
        Assert.False(_center.HasRequests);
        Assert.Equal(DownloadState.Queued, Assert.Single(_center.Entries()).State);
        // Taken over counts as seen: when it goes and is installed, it ends with a message.
        _center.ApplySnapshot("steam", [], _ => true);
        Assert.Equal("steam:20", Assert.Single(_finished).Id);
    }

    [Fact]
    public void Request_for_a_line_that_already_loads_adds_nothing()
    {
        _center.ApplySnapshot("steam", [Steam("20", SteamDownloadState.Downloading)], _ => false);
        _center.Request("steam:20", "steam", "Spiel 20", reportsProgress: true);
        Assert.False(_center.HasRequests);
        Assert.Single(_center.Entries());
    }

    [Fact]
    public void Steam_request_never_confirmed_goes_quietly()
    {
        _center.Request("steam:20", "steam", "Spiel 20", reportsProgress: true);
        _now += DownloadCenter.WaitingTimeout - TimeSpan.FromSeconds(1);
        _center.CheckRequests(_ => false);
        Assert.True(_center.HasRequests);
        _now += TimeSpan.FromSeconds(2);
        _center.CheckRequests(_ => false);
        Assert.False(_center.HasRequests);
        Assert.Empty(_finished);
        Assert.Empty(_center.Entries());
    }

    [Fact]
    public void Epic_request_runs_in_the_client_then_installing_then_done()
    {
        _center.Request("epic:Game", "epic", "Epic-Spiel", reportsProgress: false);
        var line = Assert.Single(_center.Entries());
        Assert.Equal(DownloadState.InClient, line.State);
        Assert.Null(line.Progress);
        // Longer than Steam's waiting time: still there.
        _now += DownloadCenter.WaitingTimeout + TimeSpan.FromMinutes(1);
        _center.CheckRequests(_ => false, id => id == "epic:Game");
        line = Assert.Single(_center.Entries());
        Assert.Equal(DownloadState.Installing, line.State);
        Assert.Null(line.Progress);
        _center.CheckRequests(id => id == "epic:Game");
        var done = Assert.Single(_finished);
        Assert.Equal(DownloadState.Done, done.State);
        Assert.False(done.IsUpdate);
        Assert.False(_center.HasRequests);
    }

    [Fact]
    public void Client_request_goes_after_a_day()
    {
        _center.Request("epic:Game", "epic", "Epic-Spiel", reportsProgress: false);
        _now += DownloadCenter.InClientTimeout + TimeSpan.FromMinutes(1);
        _center.CheckRequests(_ => false);
        Assert.Empty(_center.Entries());
    }

    [Fact]
    public void Dismiss_removes_finished_lines_and_requests()
    {
        _center.Begin("a", DownloadKind.Plugin, "plugin", "Ein Plugin");
        _center.Complete("a", ok: true);
        _center.Begin("b", DownloadKind.Plugin, "skin", "Ein Skin");
        _center.Complete("b", ok: true);
        _center.Request("epic:X", "epic", "X", reportsProgress: false);
        Assert.True(_center.Dismiss("epic:X"));
        Assert.False(_center.HasRequests);
        Assert.True(_center.Dismiss("a"));
        Assert.False(_center.Dismiss("a"));
        Assert.True(_center.Dismiss(null));
        Assert.Empty(_center.Entries());
        Assert.False(_center.Dismiss(null));
    }

    [Fact]
    public void Running_lines_are_not_dismissed()
    {
        _center.Begin("a", DownloadKind.CaseHud, "casehud", "CaseHUD");
        Assert.False(_center.Dismiss("a"));
        Assert.Single(_center.Entries());
    }

    [Fact]
    public void Finished_lines_are_kept_a_day_and_at_most_twenty()
    {
        for (var i = 0; i < DownloadCenter.MaxFinished + 5; i++)
        {
            _center.Begin($"p{i}", DownloadKind.Plugin, "plugin", $"Plugin {i}");
            _center.Complete($"p{i}", ok: true);
            _now = _now.AddSeconds(1);
        }
        var list = _center.Entries();
        Assert.Equal(DownloadCenter.MaxFinished, list.Count);
        Assert.Equal("p24", list[0].Id);
        _now += DownloadCenter.KeepFinished + TimeSpan.FromMinutes(1);
        Assert.Empty(_center.Entries());
    }

    [Fact]
    public void Order_and_summary()
    {
        _center.ApplySnapshot("steam", [
            Steam("1", SteamDownloadState.Queued),
            Steam("2", SteamDownloadState.Downloading, 20, 100),
            Steam("3", SteamDownloadState.Paused)
        ], _ => false);
        _center.Request("epic:E", "epic", "Epic", reportsProgress: false);
        _center.Begin("couchhud:self", DownloadKind.CouchHud, "couchhud", "CouchHUD 0.5.0");
        _center.Report("couchhud:self", 0.6);
        _center.Begin("done", DownloadKind.Plugin, "plugin", "Fertig");
        _center.Complete("done", ok: true);

        var list = _center.Entries();
        Assert.Equal([DownloadState.Downloading, DownloadState.Downloading, DownloadState.InClient, DownloadState.Paused, DownloadState.Queued, DownloadState.Done],
            list.Select(e => e.State));
        var summary = _center.Summary();
        // Paused and queued lines don't load right now; the epic line has no progress and does not drag the average down.
        Assert.Equal(3, summary.Active);
        Assert.Equal(1, summary.Finished);
        Assert.Equal(0.4, summary.Progress!.Value, 3);
    }

    [Fact]
    public void Summary_without_any_known_progress_has_none()
    {
        _center.Request("epic:E", "epic", "Epic", reportsProgress: false);
        Assert.Null(_center.Summary().Progress);
        Assert.Equal(1, _center.Summary().Active);
    }

    [Fact]
    public void A_line_is_cancellable_only_while_its_work_says_so_and_never_when_finished()
    {
        _center.Begin("couchhud:self", DownloadKind.CouchHud, "couchhud", "CouchHUD 0.5.0");
        Assert.False(_center.Entries().Single().Cancellable);
        _center.Report("couchhud:self", detail: "downloading", cancellable: true);
        Assert.True(_center.Entries().Single().Cancellable);
        // A report without the flag keeps it.
        _center.Report("couchhud:self", 0.5);
        Assert.True(_center.Entries().Single().Cancellable);
        _center.Report("couchhud:self", detail: "handing over", cancellable: false);
        Assert.False(_center.Entries().Single().Cancellable);
        _center.Report("couchhud:self", cancellable: true);
        _center.Complete("couchhud:self", false, "cancelled");
        Assert.False(_center.Entries().Single().Cancellable);
    }

    [Fact]
    public void An_activity_s_bytes_come_only_from_its_real_counters_and_stay_sane()
    {
        _center.Begin("update:windows", DownloadKind.Windows, "windows", "Windows Update");
        _center.Report("update:windows", 0.1, bytesDone: 5_000_000, bytesTotal: 20_000_000);
        var e = _center.Entries().Single();
        Assert.Equal((5_000_000L, 20_000_000L), (e.BytesDone, e.BytesTotal));
        // A plain progress keeps the bytes.
        _center.Report("update:windows", 0.2);
        Assert.Equal(20_000_000, _center.Entries().Single().BytesTotal);
        // Done is kept within 0..total; a total of 0 (installation phase, unknown) clears both.
        _center.Report("update:windows", bytesDone: 50_000_000, bytesTotal: 20_000_000);
        Assert.Equal(20_000_000, _center.Entries().Single().BytesDone);
        _center.Report("update:windows", bytesDone: 7, bytesTotal: 0);
        Assert.Equal((0L, 0L), (_center.Entries().Single().BytesDone, _center.Entries().Single().BytesTotal));
        _center.Report("update:windows", bytesDone: 7, bytesTotal: -3);
        Assert.Equal((0L, 0L), (_center.Entries().Single().BytesDone, _center.Entries().Single().BytesTotal));
    }
}
