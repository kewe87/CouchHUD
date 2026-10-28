using CouchHud.Core.Guard;
using CouchHud.Core.Integrations.Vpn;
using CouchHud.Core.Platform;

namespace CouchHud.Core.Tests;

/// <summary>
/// VPN (NordVPN first): the command line CouchHUD builds (only listed countries, strictly checked server names), where the app may be
/// run from, the tunnel adapter as the source of the state, the state machine of connect and disconnect, and the lock rules.
/// </summary>
public class VpnTests
{
    private static readonly NordVpnProvider Nord = new();

    // ---- arguments: fixed list, no injection

    [Fact]
    public void Quick_connect_and_disconnect_are_the_documented_flags()
    {
        Assert.Equal(["-c"], Nord.ConnectArguments(VpnTarget.Quick));
        Assert.Equal(["-d"], Nord.DisconnectArguments());
    }

    [Theory]
    [InlineData("de", "Germany")]
    [InlineData("us", "United States")]
    [InlineData("gb", "United Kingdom")]
    [InlineData("ba", "Bosnia and Herzegovina")]
    public void A_country_becomes_the_apps_group_name_as_one_argument(string id, string name)
    {
        Assert.Equal(["-c", "-g", name], Nord.ConnectArguments(new VpnTarget(VpnTargetKind.Country, Country: id)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("DE")]
    [InlineData("xx")]
    [InlineData("deu")]
    [InlineData("Germany")]
    [InlineData("de\" -d \"")]
    [InlineData("de & calc")]
    public void Anything_but_a_listed_country_id_is_refused(string? id)
    {
        Assert.Throws<ArgumentException>(() => Nord.ConnectArguments(new VpnTarget(VpnTargetKind.Country, Country: id)));
    }

    [Theory]
    [InlineData("Germany #812", "Germany #812")]
    [InlineData("germany #812", "Germany #812")]
    [InlineData("UNITED STATES #3710", "United States #3710")]
    [InlineData("Bosnia and Herzegovina #1", "Bosnia and Herzegovina #1")]
    public void A_server_name_is_written_the_way_the_list_writes_the_country(string typed, string expected)
    {
        Assert.Equal(["-c", "-n", expected], Nord.ConnectArguments(new VpnTarget(VpnTargetKind.Server, Server: typed)));
        Assert.Equal(expected, Nord.ValidServer(typed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Germany")]
    [InlineData("Germany #")]
    [InlineData("Germany #0")]
    [InlineData("Germany #012")]
    [InlineData("Germany #123456")]
    [InlineData("Narnia #1")]
    [InlineData("Germany #1 -d")]
    [InlineData("Germany #1\" -d \"")]
    [InlineData("Germany #1\n")]
    [InlineData("Germany#1")]
    [InlineData(" Germany #1")]
    [InlineData("Dedicated IP #1")]
    [InlineData("Germany  #1")]
    public void Anything_but_country_and_number_is_refused_as_a_server(string? server)
    {
        Assert.Throws<ArgumentException>(() => Nord.ConnectArguments(new VpnTarget(VpnTargetKind.Server, Server: server)));
    }

    [Fact]
    public void The_country_list_has_unique_two_letter_ids_and_plain_names()
    {
        Assert.Equal(Nord.Countries.Count, Nord.Countries.Select(c => c.Id).Distinct().Count());
        Assert.All(Nord.Countries, c =>
        {
            Assert.Matches("^[a-z]{2}$", c.Id);
            Assert.Matches("^[A-Za-z][A-Za-z .]+$", c.Name);
        });
        Assert.Equal("de", Nord.ServerCountry("Germany #812"));
        Assert.Null(Nord.ServerCountry("Narnia #1"));
    }

    [Fact]
    public void Normalize_keeps_only_what_is_used_and_names_the_country()
    {
        var server = Nord.Normalize(new VpnTarget(VpnTargetKind.Server, Country: "us", Server: "germany #5"));
        Assert.Equal(new VpnTarget(VpnTargetKind.Server, Server: "Germany #5"), server);
        Assert.Equal("de", Nord.CountryOf(server));
        Assert.Equal(VpnTarget.Quick, Nord.Normalize(new VpnTarget(VpnTargetKind.Quick, Country: "xx", Server: "evil")));
        Assert.Null(Nord.CountryOf(VpnTarget.Quick));
        Assert.Equal("fr", Nord.CountryOf(Nord.Normalize(new VpnTarget(VpnTargetKind.Country, Country: "fr"))));
        Assert.Throws<ArgumentException>(() => Nord.Normalize(new VpnTarget((VpnTargetKind)42)));
    }

    // ---- where the app may be run from

    [Fact]
    public void Candidates_come_from_hklm_app_paths_uninstall_entry_and_program_files_only()
    {
        var registry = new FakeRegistry();
        registry.Values[NordVpnProvider.AppPathsKey] = new() { [""] = @"""C:\Program Files\NordVPN\NordVPN.exe""" };
        registry.Values[NordVpnProvider.UninstallRoot + @"\{1234}_is1"] = new() { ["DisplayName"] = "NordVPN", ["InstallLocation"] = @"D:\Apps\NordVPN\", ["DisplayIcon"] = @"""E:\Nord\NordVPN.exe"",0" };
        // Other Nord products and the TAP driver are not the app.
        registry.Values[NordVpnProvider.UninstallRoot + @"\TAP"] = new() { ["DisplayName"] = "NordVPN network TAP", ["InstallLocation"] = @"C:\Evil\" };
        registry.Values[NordVpnProvider.UninstallRoot32 + @"\Pass"] = new() { ["DisplayName"] = "NordPass", ["InstallLocation"] = @"C:\Evil2\" };
        // HKCU is never read (any user could write it).
        registry.Values[@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\App Paths\NordVPN.exe"] = new() { [""] = @"C:\Users\x\NordVPN.exe" };
        var list = Nord.ExeCandidates(registry, [@"C:\Program Files"]).ToList();
        Assert.Equal(@"C:\Program Files\NordVPN\NordVPN.exe", list[0]);
        Assert.Contains(@"D:\Apps\NordVPN\NordVPN.exe", list);
        Assert.Contains(@"E:\Nord\NordVPN.exe", list);
        Assert.DoesNotContain(list, c => c.Contains("Evil", StringComparison.Ordinal) || c.Contains("Users", StringComparison.Ordinal));
        Assert.Equal(@"C:\Program Files\NordVPN\NordVPN.exe", list[^1]);
    }

    [Fact]
    public void Relative_or_foreign_registry_values_give_no_candidate()
    {
        var registry = new FakeRegistry();
        registry.Values[NordVpnProvider.AppPathsKey] = new() { [""] = @"NordVPN.exe", ["Path"] = @"\\server\share" };
        registry.Values[NordVpnProvider.UninstallRoot + @"\x"] = new() { ["DisplayName"] = "NordVPN", ["InstallLocation"] = @"..\NordVPN", ["DisplayIcon"] = @"C:\Program Files\NordVPN\other.exe" };
        Assert.Empty(Nord.ExeCandidates(registry, []));
    }

    [Theory]
    [InlineData(@"C:\Program Files\NordVPN\NordVPN.exe", true)]
    [InlineData(@"C:\Program Files (x86)\NordVPN\nordvpn.exe", true)]
    [InlineData(@"C:\Program Files\NordVPN.exe", false)]
    [InlineData(@"C:\Users\x\AppData\Local\NordVPN\NordVPN.exe", false)]
    [InlineData(@"C:\Program Files\NordVPN\..\..\Users\x\NordVPN.exe", false)]
    [InlineData(@"C:\Program Files\NordVPN\NordVPN.exe.bat", false)]
    [InlineData(@"\\server\Program Files\NordVPN\NordVPN.exe", false)]
    [InlineData(@"NordVPN.exe", false)]
    public void Only_an_exe_below_program_files_may_be_run(string exe, bool trusted)
    {
        Assert.Equal(trusted, TrustedCli.IsTrustedInstallPath(exe, Nord.ExeName, [@"C:\Program Files", @"C:\Program Files (x86)"]));
    }

    // ---- the tunnel adapter

    [Fact]
    public void The_tunnel_is_what_is_up()
    {
        Assert.Null(Nord.TunnelKind([new("Ethernet", "Intel(R) Ethernet", true), new("NordVPN", "TAP-NordVPN Windows Adapter V9", false)]));
        Assert.Equal("openvpn", Nord.TunnelKind([new("Ethernet", "Intel", true), new("Ethernet 3", "TAP-NordVPN Windows Adapter V9", true)]));
        Assert.Equal("nordlynx", Nord.TunnelKind([new("NordLynx", "NordLynx Tunnel", true), new("Ethernet 3", "TAP-NordVPN Windows Adapter V9", true)]));
        Assert.Null(Nord.TunnelKind([new("NordLynx", "NordLynx Tunnel", false)]));
        Assert.Null(Nord.TunnelKind([]));
    }

    [Fact]
    public void Meshnet_alone_is_no_vpn_connection()
    {
        // Meshnet brings the NordLynx adapter up for its own network only; the internet still goes through the Ethernet adapter.
        Assert.Null(Nord.TunnelKind([new("Ethernet", "Intel", true, true), new("NordLynx", "NordLynx Tunnel", true, false)]));
        Assert.Equal("nordlynx", Nord.TunnelKind([new("Ethernet", "Intel", true, false), new("NordLynx", "NordLynx Tunnel", true, true)]));
        Assert.Null(Nord.TunnelKind([new("Ethernet 3", "TAP-NordVPN Windows Adapter V9", true, false)]));
        // Route unknown: an adapter that is up counts, as before.
        Assert.Equal("nordlynx", Nord.TunnelKind([new("NordLynx", "NordLynx Tunnel", true, null)]));
    }

    [Fact]
    public void A_command_line_is_only_killed_when_the_app_was_running_already()
    {
        Assert.True(Nord.KillCliOnTimeout(appWasRunning: true));
        Assert.False(Nord.KillCliOnTimeout(appWasRunning: false));
    }

    private sealed class FakeCli(Task exited) : ICliProcess
    {
        public int Kills;
        public Task WaitForExitAsync(CancellationToken ct) => exited.WaitAsync(ct);
        public int ExitCode => 0;
        public Task<string> Stderr => Task.FromResult("");
        public void Kill() => Kills++;
        public void Dispose() { }
    }

    [Fact]
    public async Task A_cli_that_became_the_app_is_left_running_after_the_time_limit()
    {
        var p = new FakeCli(new TaskCompletionSource().Task);
        var result = await TrustedCli.RunAsync(p, CancellationToken.None, TimeSpan.FromMilliseconds(30), killOnTimeout: () => false).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(result.Exit);
        Assert.Equal(0, p.Kills);
        var hung = new FakeCli(new TaskCompletionSource().Task);
        await TrustedCli.RunAsync(hung, CancellationToken.None, TimeSpan.FromMilliseconds(30), killOnTimeout: () => true).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, hung.Kills);
    }

    private sealed class WindowCli(Task exited) : ICliProcess
    {
        public volatile bool Window;
        public Task WaitForExitAsync(CancellationToken ct) => exited.WaitAsync(ct);
        public int ExitCode => 0;
        public Task<string> Stderr => Task.FromResult("");
        public void Kill() { }
        public void Dispose() { }
        public bool HasWindow => Window;
    }

    [Fact]
    public async Task The_gate_stays_closed_while_the_cli_runs_without_a_window()
    {
        var poll = TimeSpan.FromMilliseconds(10);
        // Runs, no window yet: nothing; then a window: once.
        var exit = new TaskCompletionSource();
        var cli = new WindowCli(exit.Task);
        var calls = 0;
        var run = TrustedCli.RunAsync(cli, CancellationToken.None, TimeSpan.FromSeconds(30), killOnTimeout: () => false,
            onWindow: () => Interlocked.Increment(ref calls), windowPoll: poll);
        await Task.Delay(150);
        Assert.Equal(0, calls);
        cli.Window = true;
        for (var i = 0; i < 200 && Volatile.Read(ref calls) == 0; i++) await Task.Delay(10);
        await Task.Delay(100);
        Assert.Equal(1, calls);
        exit.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        // Never a window until the time limit: never called, and not afterwards either.
        var hung = new WindowCli(new TaskCompletionSource().Task);
        var none = 0;
        var result = await TrustedCli.RunAsync(hung, CancellationToken.None, TimeSpan.FromMilliseconds(120), killOnTimeout: () => false,
            onWindow: () => Interlocked.Increment(ref none), windowPoll: poll).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(result.Exit);
        hung.Window = true;
        await Task.Delay(100);
        Assert.Equal(0, none);

        // A command line that hands over to a running app and ends: never called.
        var quick = new WindowCli(Task.CompletedTask);
        var handed = 0;
        await TrustedCli.RunAsync(quick, CancellationToken.None, TimeSpan.FromSeconds(5), onWindow: () => handed++, windowPoll: poll);
        Assert.Equal(0, handed);
    }

    [Fact]
    public async Task Quitting_leaves_a_cli_that_became_the_app_running_and_kills_everything_else()
    {
        using (var cts = new CancellationTokenSource())
        {
            var app = new FakeCli(new TaskCompletionSource().Task);
            var run = TrustedCli.RunAsync(app, cts.Token, TimeSpan.FromMinutes(1), killOnTimeout: () => false);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, app.Kills);
        }
        foreach (Func<bool>? rule in new Func<bool>?[] { null, () => true })
        {
            using var cts = new CancellationTokenSource();
            var cli = new FakeCli(new TaskCompletionSource().Task);
            var run = TrustedCli.RunAsync(cli, cts.Token, TimeSpan.FromMinutes(1), killOnTimeout: rule);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, cli.Kills);
        }
    }

    // ---- state machine

    private sealed class Clock
    {
        public DateTimeOffset Now = new(2026, 10, 27, 20, 0, 0, TimeSpan.Zero);
        public void Add(TimeSpan by) => Now += by;
    }

    private static (VpnStateMachine M, Clock C) Machine()
    {
        var clock = new Clock();
        return (new VpnStateMachine(() => clock.Now), clock);
    }

    [Fact]
    public void Connect_is_connecting_until_the_tunnel_is_up()
    {
        var (m, c) = Machine();
        var target = new VpnTarget(VpnTargetKind.Country, Country: "de");
        m.RequestConnect(target, tunnelUp: false);
        Assert.True(m.Busy);
        Assert.Equal(VpnState.Connecting, m.Observe(true, false));
        c.Add(TimeSpan.FromSeconds(5));
        Assert.Equal(VpnState.Connected, m.Observe(true, true));
        Assert.False(m.Busy);
        Assert.Equal(target, m.Target);
        Assert.Null(m.Hint);
    }

    [Fact]
    public void Connect_without_a_tunnel_in_time_ends_with_the_sign_in_hint()
    {
        var (m, c) = Machine();
        m.RequestConnect(VpnTarget.Quick, tunnelUp: false);
        c.Add(VpnStateMachine.ConnectTimeout);
        Assert.Equal(VpnState.Disconnected, m.Observe(true, false));
        Assert.Equal("noConnection", m.Hint);
        Assert.Null(m.Target);
        Assert.False(m.Busy);
        // The hint stays while nothing changes, and goes once a tunnel comes up.
        Assert.Equal(VpnState.Disconnected, m.Observe(true, false));
        Assert.Equal("noConnection", m.Hint);
        Assert.Equal(VpnState.Connected, m.Observe(true, true));
        Assert.Null(m.Hint);
    }

    [Fact]
    public void A_change_of_country_waits_for_the_tunnel_to_go_down_or_a_settle_time()
    {
        var (m, c) = Machine();
        m.RequestConnect(new VpnTarget(VpnTargetKind.Country, Country: "us"), tunnelUp: true);
        Assert.Equal(VpnState.Connecting, m.Observe(true, true));
        Assert.Equal(VpnState.Connecting, m.Observe(true, false));
        Assert.Equal(VpnState.Connected, m.Observe(true, true));

        m.RequestConnect(new VpnTarget(VpnTargetKind.Country, Country: "fr"), tunnelUp: true);
        Assert.Equal(VpnState.Connecting, m.Observe(true, true));
        c.Add(VpnStateMachine.ReconnectSettle);
        Assert.Equal(VpnState.Connected, m.Observe(true, true));
        Assert.Equal("fr", m.Target?.Country);
        // Settled by time only: France is asked for, not seen.
        Assert.False(m.TargetConfirmed);
    }

    [Fact]
    public void A_place_is_confirmed_only_when_the_tunnel_came_up_after_the_request()
    {
        var (m, c) = Machine();
        m.RequestConnect(new VpnTarget(VpnTargetKind.Country, Country: "de"), tunnelUp: false);
        Assert.False(m.TargetConfirmed);
        m.Observe(true, true);
        Assert.True(m.TargetConfirmed);

        m.RequestConnect(new VpnTarget(VpnTargetKind.Country, Country: "fr"), tunnelUp: true);
        Assert.False(m.TargetConfirmed);
        m.Observe(true, false);
        m.Observe(true, true);
        Assert.True(m.TargetConfirmed);

        // Up the whole time until the limit: connected, but the place stays unconfirmed.
        m.RequestConnect(new VpnTarget(VpnTargetKind.Country, Country: "it"), tunnelUp: true);
        c.Add(VpnStateMachine.ConnectTimeout);
        Assert.Equal(VpnState.Connected, m.Observe(true, true));
        Assert.False(m.TargetConfirmed);
    }

    [Fact]
    public void Disconnect_is_done_when_the_tunnel_is_down()
    {
        var (m, c) = Machine();
        m.RequestConnect(VpnTarget.Quick, false);
        m.Observe(true, true);
        m.RequestDisconnect(tunnelUp: true);
        Assert.Equal(VpnState.Disconnecting, m.Observe(true, true));
        c.Add(TimeSpan.FromSeconds(2));
        Assert.Equal(VpnState.Disconnected, m.Observe(true, false));
        Assert.Null(m.Target);
    }

    [Fact]
    public void A_tunnel_that_stays_up_after_disconnect_says_so()
    {
        var (m, c) = Machine();
        m.RequestDisconnect(tunnelUp: true);
        c.Add(VpnStateMachine.DisconnectTimeout);
        Assert.Equal(VpnState.Connected, m.Observe(true, true));
        Assert.Equal("stillConnected", m.Hint);
        Assert.Equal(VpnState.Disconnected, m.Observe(true, false));
        Assert.Null(m.Hint);
    }

    [Fact]
    public void A_tunnel_from_the_app_itself_is_connected_without_a_place_and_its_end_forgets_ours()
    {
        var (m, _) = Machine();
        Assert.Equal(VpnState.Connected, m.Observe(true, true));
        Assert.Null(m.Target);
        m.RequestConnect(new VpnTarget(VpnTargetKind.Country, Country: "de"), false);
        m.Observe(true, true);
        Assert.NotNull(m.Target);
        // Disconnected in the app: what CouchHUD asked for is history.
        Assert.Equal(VpnState.Disconnected, m.Observe(true, false));
        Assert.Null(m.Target);
    }

    [Fact]
    public void Not_installed_drops_everything_pending_and_abort_ends_a_request()
    {
        var (m, _) = Machine();
        m.RequestConnect(VpnTarget.Quick, false);
        Assert.Equal(VpnState.NotInstalled, m.Observe(false, false));
        Assert.False(m.Busy);
        m.RequestConnect(VpnTarget.Quick, false);
        m.Abort();
        Assert.False(m.Busy);
        Assert.Null(m.Target);
        Assert.Equal(VpnState.Disconnected, m.Observe(true, false));
    }

    // ---- lock rules

    [Fact]
    public void A_locked_couch_reads_the_vpn_but_changes_nothing()
    {
        Assert.True(VpnPolicy.Allowed("vpnStatus", locked: true));
        Assert.True(VpnPolicy.Allowed("vpnCountries", locked: true));
        foreach (var change in new[] { "vpnConnect", "vpnDisconnect", "vpnOpenApp" })
        {
            Assert.False(VpnPolicy.Allowed(change, locked: true));
            Assert.True(VpnPolicy.Allowed(change, locked: false));
            Assert.DoesNotContain(change, LockPolicy.OpenWhileLocked);
        }
        Assert.False(VpnPolicy.Allowed("vpnSomethingElse", locked: false));
        Assert.DoesNotContain("vpnInGuide", LockPolicy.HarmlessSettings);
    }

    [Fact]
    public void Every_vpn_call_of_the_page_is_known_to_the_lock_rules()
    {
        string? js = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null && js is null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "ui", "bridge.js");
            if (File.Exists(path)) js = File.ReadAllText(path);
        }
        Assert.NotNull(js);
        var names = System.Text.RegularExpressions.Regex.Matches(js!, @"^\s*(vpn\w+):", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToHashSet();
        Assert.True(names.SetEquals(VpnPolicy.All), $"bridge.js and VpnPolicy.All differ: {string.Join(", ", names.Except(VpnPolicy.All).Concat(VpnPolicy.All.Except(names)))}");
    }
}
