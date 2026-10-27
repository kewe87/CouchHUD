using System.Net;
using System.Text;
using CouchHud.Core.Integrations.CaseHud;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Tests;

/// <summary>
/// CaseHUD's switchable API: the four states, api.json and instance.pid as untrusted input, 403 api_disabled, the folder watcher's
/// debounce, the event stream ending on a change, and the --set-api switch from the verified installation only.
/// </summary>
public class CaseHudApiSwitchTests : IDisposable
{
    private const string Exe = @"C:\Program Files\CaseHUD\CaseHUD.exe";
    private const string Token = "s3cr3tTokenValue42";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-casehud-api-" + Guid.NewGuid().ToString("N"));

    public CaseHudApiSwitchTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string ApiPath => Path.Combine(_dir, "api.json");
    private string PidPath => Path.Combine(_dir, "instance.pid");

    private static string ApiJson(int port = 51234, string token = Token, int pid = 1234, string exe = Exe) =>
        $$"""{"apiVersion":1,"version":"1.1.0","port":{{port}},"token":"{{token}}","pid":{{pid}},"exe":"{{exe.Replace("\\", "\\\\")}}"}""";

    // ---- state detection (pure)

    [Theory]
    [InlineData(false, false, false, false, CaseHudApiState.NotInstalled)]
    [InlineData(true, false, false, false, CaseHudApiState.NotRunning)]
    [InlineData(true, true, false, false, CaseHudApiState.ApiOff)]       // instance.pid verified, no api.json
    [InlineData(true, true, true, true, CaseHudApiState.ApiOff)]         // api.json still there, but 403 api_disabled
    [InlineData(true, false, true, true, CaseHudApiState.ApiOff)]        // older build without instance.pid, refused
    [InlineData(true, true, true, false, CaseHudApiState.ApiOn)]
    [InlineData(true, false, true, false, CaseHudApiState.ApiOn)]        // older build: no instance.pid, API always on
    [InlineData(false, false, true, false, CaseHudApiState.ApiOn)]       // developer run from another folder
    public void State_follows_what_was_observed(bool installed, bool instance, bool announced, bool refused, CaseHudApiState expected) =>
        Assert.Equal(expected, CaseHudState.Detect(installed, instance, announced, refused));

    [Fact]
    public void State_names_are_stable_for_the_page()
    {
        Assert.Equal("notInstalled", CaseHudState.Name(CaseHudApiState.NotInstalled));
        Assert.Equal("notRunning", CaseHudState.Name(CaseHudApiState.NotRunning));
        Assert.Equal("apiOff", CaseHudState.Name(CaseHudApiState.ApiOff));
        Assert.Equal("apiOn", CaseHudState.Name(CaseHudApiState.ApiOn));
    }

    // ---- api.json as untrusted input

    [Fact]
    public void Valid_api_json_is_read()
    {
        var info = CaseHudFiles.ParseApiJson(ApiJson());
        Assert.NotNull(info);
        Assert.Equal(51234, info!.Port);
        Assert.Equal(1234, info.Pid);
        Assert.Equal(Exe, info.Exe);
        Assert.DoesNotContain(Token, info.ToString());
    }

    [Fact]
    public void Utf8_bom_and_unknown_fields_are_tolerated()
    {
        var json = ApiJson().TrimEnd('}') + ""","later":"field"}""";
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(json)).ToArray();
        Assert.NotNull(CaseHudFiles.ParseApiJson(bytes));
    }

    public static TheoryData<string> BadApiJson => new()
    {
        "",
        "[]",
        "null",
        ApiJson(token: "with-dash-token-value"),                              // letters and digits only (CaseHUD's contract)
        ApiJson(token: "with_underscore_token"),
        ApiJson(token: "slash/plus+equals=0123"),
        ApiJson(port: 65536),
        ApiJson(port: 0),
        ApiJson(pid: 0),
        ApiJson(pid: -5),
        ApiJson(token: "short"),                                              // too short
        ApiJson(token: "has space in the token value"),
        ApiJson(token: "line\\r\\nX-Injected: 1abcdefgh"),                     // header injection
        ApiJson(token: "quote\\\"abcdefghijklmnop"),
        ApiJson(token: new string('a', 129)),
        ApiJson(exe: ""),
        ApiJson(exe: "C:\\a\u0001b\\CaseHUD.exe").Replace("\u0001", "\\u0001"),
        ApiJson().Replace("\"apiVersion\":1", "\"apiVersion\":2"),
        ApiJson().Replace("\"port\":51234", "\"port\":\"51234\""),             // numbers as numbers
        ApiJson().Replace("\"port\":51234", "\"port\":51234.5"),
        ApiJson().TrimEnd('}') + ",}",                                         // trailing comma
        "/* c */" + ApiJson(),                                                // comments
        ApiJson().TrimEnd('}') + ",\"port\":51235}",                           // duplicate key
        ApiJson().Replace("\"token\":\"" + Token + "\",", ""),                 // missing token
        ApiJson()[..40]                                                       // half written
    };

    [Theory]
    [MemberData(nameof(BadApiJson))]
    public void Bad_api_json_is_refused(string json) => Assert.Null(CaseHudFiles.ParseApiJson(json));

    [Theory]
    [InlineData(1)]
    [InlineData(80)]
    [InlineData(65535)]
    public void Any_port_from_1_is_taken(int port) => Assert.Equal(port, CaseHudFiles.ParseApiJson(ApiJson(port: port))?.Port);

    [Fact]
    public void Token_of_16_to_128_letters_and_digits_is_taken()
    {
        Assert.NotNull(CaseHudFiles.ParseApiJson(ApiJson(token: new string('a', 16))));
        Assert.NotNull(CaseHudFiles.ParseApiJson(ApiJson(token: new string('Z', 64) + new string('9', 64))));
        Assert.Null(CaseHudFiles.ParseApiJson(ApiJson(token: new string('a', 15))));
    }

    [Fact]
    public void Oversized_api_json_is_refused_before_parsing()
    {
        var json = ApiJson().TrimEnd('}') + ",\"pad\":\"" + new string('x', CaseHudFiles.MaxApiJsonBytes) + "\"}";
        Assert.Null(CaseHudFiles.ParseApiJson(json));
    }

    [Theory]
    [InlineData("1234", 1234)]
    [InlineData("1234\r\n", 1234)]
    [InlineData("42\n", 42)]
    [InlineData("  42 ", null)]                     // digits only
    [InlineData("\uFEFF77", null)]
    [InlineData("+5", null)]
    [InlineData("", null)]
    [InlineData("0", null)]
    [InlineData("-1", null)]
    [InlineData("12a", null)]
    [InlineData("1 2", null)]
    [InlineData("99999999999", null)]
    [InlineData("{\"pid\":5}", null)]
    public void Instance_pid_is_one_number(string text, int? expected) =>
        Assert.Equal(expected, CaseHudFiles.ParsePid(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void Oversized_pid_file_is_refused() => Assert.Null(CaseHudFiles.ParsePid(Encoding.ASCII.GetBytes(new string(' ', 70) + "5")));

    // ---- discovery: api.json read results and instance.pid verified against the installed exe

    private CaseHudDiscovery Discovery(Func<int, string?> image, string? installed = Exe) =>
        new(ApiPath, image, new FakeRegistry(), _ => true) { ExpectedExe = () => installed };

    [Fact]
    public void Read_results_tell_missing_from_half_written()
    {
        var d = Discovery(_ => Exe);
        Assert.Equal(CaseHudFileRead.Missing, d.ReadApi().Read);
        File.WriteAllText(ApiPath, ApiJson()[..30]);
        Assert.Equal(CaseHudFileRead.Invalid, d.ReadApi().Read);
        File.WriteAllText(ApiPath, ApiJson());
        var (read, info) = d.ReadApi();
        Assert.Equal(CaseHudFileRead.Valid, read);
        Assert.NotNull(info);
    }

    [Fact]
    public void Instance_counts_only_when_its_process_is_the_installed_CaseHUD()
    {
        File.WriteAllText(PidPath, "4321");
        Assert.Equal(4321, Discovery(pid => pid == 4321 ? Exe : null).ReadInstance());
        Assert.Equal(4321, Discovery(_ => Exe.ToUpperInvariant()).ReadInstance());
        Assert.Null(Discovery(_ => null).ReadInstance());                                  // process gone
        Assert.Null(Discovery(_ => @"C:\Windows\notepad.exe").ReadInstance());             // PID reused by another program
        Assert.Null(Discovery(_ => @"C:\Users\x\AppData\CaseHUD.exe").ReadInstance());     // a CaseHUD.exe elsewhere is not it
        Assert.Null(Discovery(_ => Exe, installed: null).ReadInstance());                  // nothing installed: nothing to compare with
        File.WriteAllText(PidPath, "garbage");
        Assert.Null(Discovery(_ => Exe).ReadInstance());
        File.Delete(PidPath);
        Assert.Null(Discovery(_ => Exe).ReadInstance());
    }

    // ---- stale files: the process must run in this session and must have started before the file was written

    private CaseHudDiscovery Checked(CaseHudProcessFacts? facts, DateTime written)
    {
        var d = Discovery(_ => Exe);
        d.CurrentSession = 2;
        d.ProcessFacts = _ => facts;
        d.LastWriteUtc = _ => written;
        return d;
    }

    [Fact]
    public void A_file_older_than_its_process_or_from_another_session_does_not_count()
    {
        File.WriteAllText(ApiPath, ApiJson());
        File.WriteAllText(PidPath, "1234");
        var written = new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
        var before = written.AddMinutes(-3);

        var ok = Checked(new CaseHudProcessFacts(2, before), written);
        Assert.NotNull(ok.ReadRunning());
        Assert.Equal(1234, ok.ReadInstance());

        // PID reused by a CaseHUD started after the file was written: the file is stale.
        var later = Checked(new CaseHudProcessFacts(2, written.AddMinutes(1)), written);
        Assert.Null(later.ReadRunning());
        Assert.Null(later.ReadInstance());
        Assert.Equal(CaseHudFileRead.Valid, later.ReadApi().Read);    // readable, just not this process's

        // CaseHUD of another session (another signed-in user, fast user switching).
        var other = Checked(new CaseHudProcessFacts(3, before), written);
        Assert.Null(other.ReadRunning());
        Assert.Null(other.ReadInstance());

        // Facts unknown (gone, no access): not trusted.
        var unknown = Checked(null, written);
        Assert.Null(unknown.ReadRunning());
        Assert.Null(unknown.ReadInstance());

        // Clock granularity: a start up to a second after the write time still counts.
        Assert.NotNull(Checked(new CaseHudProcessFacts(2, written.AddMilliseconds(900)), written).ReadRunning());
    }

    // ---- the refusal (403 api_disabled) and switching back on with the very same port and token

    [Fact]
    public void Switching_off_and_on_again_with_identical_values_reports_the_api_on()
    {
        var now = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        var refusal = new CaseHudRefusal(() => now);
        var info = CaseHudFiles.ParseApiJson(ApiJson())!;
        CaseHudApiState State() => CaseHudState.Detect(true, true, true, refusal.Refused(info));

        refusal.Mark(info);                                             // 403 api_disabled
        Assert.Equal(CaseHudApiState.ApiOff, State());
        Assert.True(refusal.Blocks(info));

        // CaseHUD switched it on again and rewrote api.json with the same values: the folder change clears the refusal.
        refusal.Clear();
        Assert.Equal(CaseHudApiState.ApiOn, State());
        Assert.False(refusal.Blocks(CaseHudFiles.ParseApiJson(ApiJson())));
    }

    [Fact]
    public void A_refusal_is_looked_at_again_after_30_seconds_even_without_a_folder_change()
    {
        var now = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        var refusal = new CaseHudRefusal(() => now);
        var info = CaseHudFiles.ParseApiJson(ApiJson())!;
        refusal.Mark(info);
        now += TimeSpan.FromSeconds(29);
        Assert.True(refusal.Blocks(info));
        now += TimeSpan.FromSeconds(2);
        Assert.False(refusal.Blocks(info));                             // one request may try again
        Assert.True(refusal.Refused(info));                             // the page keeps saying "off" until CaseHUD answers
        refusal.Mark(info);                                             // still off: the next 30 s start
        Assert.True(refusal.Blocks(info));
        // Another announcement is never held back by an old refusal.
        Assert.False(refusal.Blocks(CaseHudFiles.ParseApiJson(ApiJson(port: 51235))));
    }

    [Fact]
    public async Task Start_of_a_running_CaseHUD_with_the_api_off_says_so_without_starting_it_again()
    {
        File.WriteAllText(PidPath, "4321");
        var d = Discovery(_ => Exe);
        var starts = 0;
        var ex = await Assert.ThrowsAsync<CaseHudException>(() => d.StartAsync(new CaseHudInstall(Exe, @"C:\Program Files\CaseHUD", "1.1.0"),
            (_, _) => { starts++; return null; }, (_, _) => Task.CompletedTask));
        Assert.True(CaseHudState.IsApiDisabled(ex));
        Assert.Equal(Strings.Get("casehud.apiOff"), ex.Message);
        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task Started_CaseHUD_without_api_json_ends_in_the_hint_not_a_timeout()
    {
        var d = Discovery(_ => Exe);
        var waits = 0;
        var ex = await Assert.ThrowsAsync<CaseHudException>(() => d.StartAsync(new CaseHudInstall(Exe, @"C:\Program Files\CaseHUD", "1.1.0"),
            (_, _) => { File.WriteAllText(PidPath, "4321"); return null; },     // runs, writes instance.pid, never an api.json
            (_, _) => { waits++; return Task.CompletedTask; }));
        Assert.True(CaseHudState.IsApiDisabled(ex));
        Assert.True(waits * 250 < CaseHudDiscovery.StartWaitSeconds * 1000);   // before the 15 s timeout
        Assert.True(waits * 250 >= CaseHudDiscovery.ApiOffGrace.TotalMilliseconds);
    }

    // ---- 403 api_disabled

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(respond(request));
        }
    }

    private const string ApiDisabledBody = """{"error":{"code":"api_disabled","message":"the API is switched off in the CaseHUD settings (Settings > Interfaces)"}}""";

    private static HttpResponseMessage Error(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static CaseHudApiInfo Info() => CaseHudFiles.ParseApiJson(ApiJson())!;

    [Theory]
    [InlineData(403, "api_disabled", true)]
    [InlineData(403, "forbidden", false)]
    [InlineData(401, "api_disabled", false)]
    [InlineData(403, "API_DISABLED", false)]
    [InlineData(403, null, false)]
    public void Only_403_api_disabled_means_switched_off(int status, string? code, bool expected) =>
        Assert.Equal(expected, CaseHudState.IsApiDisabled(status, code));

    [Fact]
    public async Task Api_disabled_becomes_the_hint_where_to_switch_it_on()
    {
        var client = new CaseHudClient(new HttpClient(new Handler(_ => Error(HttpStatusCode.Forbidden, ApiDisabledBody))), Info());
        var ex = await Assert.ThrowsAsync<CaseHudException>(() => client.StatusAsync());
        Assert.Equal("api_disabled", ex.Code);
        Assert.Equal(403, ex.Status);
        Assert.Equal(Strings.Get("casehud.apiOff"), ex.Message);
        Assert.DoesNotContain(Token, ex.Message);
        Assert.Equal(Strings.Get("casehud.apiOff"), CaseHudLighting.ErrorText(ex));
    }

    [Fact]
    public async Task Forbidden_from_the_host_check_stays_a_generic_error()
    {
        var client = new CaseHudClient(new HttpClient(new Handler(_ => Error(HttpStatusCode.Forbidden, """{"error":{"code":"forbidden","message":"bad host"}}"""))), Info());
        var ex = await Assert.ThrowsAsync<CaseHudException>(() => client.StatusAsync());
        Assert.Equal("forbidden", ex.Code);
        Assert.False(CaseHudState.IsApiDisabled(ex));
        Assert.Equal(Strings.Get("casehud.err.forbidden"), CaseHudLighting.ErrorText(ex));
    }

    [Fact]
    public async Task Event_stream_refused_with_api_disabled_is_reported()
    {
        var handler = new Handler(_ => Error(HttpStatusCode.Forbidden, ApiDisabledBody));
        var client = new CaseHudClient(new HttpClient(handler), Info());
        var reported = 0;
        using var cts = new CancellationTokenSource();
        var loop = new CaseHudEventLoop(new HttpClient(handler), () => client, _ => { }, _ => { },
            (_, _) => { cts.Cancel(); return Task.CompletedTask; }, apiDisabled: () => reported++);
        await loop.RunAsync(cts.Token);
        Assert.Equal(1, reported);
    }

    [Fact]
    public async Task Interrupt_ends_the_open_stream_and_asks_for_the_client_again()
    {
        // A stream that never sends anything until it is cancelled.
        var handler = new StreamHandler();
        var client = new CaseHudClient(new HttpClient(handler), Info());
        var asked = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var second = new TaskCompletionSource();
        var loop = new CaseHudEventLoop(new HttpClient(handler), () =>
        {
            if (++asked == 2) second.TrySetResult();
            return asked == 1 ? client : null;
        }, _ => { }, _ => { }, (t, ct) => Task.Delay(t, ct));
        var run = loop.RunAsync(cts.Token);
        await handler.Open.Task.WaitAsync(TimeSpan.FromSeconds(5));
        loop.Interrupt();
        await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(handler.Cancelled);
        cts.Cancel();
        await run;
    }

    private sealed class StreamHandler : HttpMessageHandler
    {
        public readonly TaskCompletionSource Open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool Cancelled;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Open.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    // ---- debounce of the folder watcher

    [Fact]
    public void Debounce_waits_for_quiet_but_not_forever()
    {
        var t0 = DateTimeOffset.UnixEpoch;
        var d = new CaseHudDebounce(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(2));
        Assert.Null(d.Due(t0));
        Assert.False(d.TryFire(t0));

        d.Poke(t0);
        Assert.Equal(TimeSpan.FromMilliseconds(250), d.Due(t0));
        Assert.False(d.TryFire(t0.AddMilliseconds(100)));
        d.Poke(t0.AddMilliseconds(200));                                    // another write of the same burst
        Assert.False(d.TryFire(t0.AddMilliseconds(300)));
        Assert.True(d.TryFire(t0.AddMilliseconds(450)));
        Assert.False(d.Pending);
        Assert.False(d.TryFire(t0.AddMilliseconds(900)));                   // once per burst

        // A file that keeps changing is read after maxWait at the latest.
        for (var ms = 0; ms <= 1900; ms += 100) d.Poke(t0.AddSeconds(10).AddMilliseconds(ms));
        Assert.True(d.TryFire(t0.AddSeconds(12)));
    }

    [Fact]
    public async Task Watcher_copes_with_a_folder_that_appears_later()
    {
        var folder = Path.Combine(_dir, "casehud");
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // The poll (the fallback when the system watcher is not there or fails), fast for the test.
        using var watcher = new CaseHudFolderWatcher(folder, () => changed.TrySetResult(), useWatcher: false,
            pollEvery: TimeSpan.FromMilliseconds(50), quiet: TimeSpan.FromMilliseconds(20));
        watcher.Start();
        Assert.False(watcher.Watching);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "api.json"), ApiJson());
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Watcher_reports_a_deleted_api_json()
    {
        File.WriteAllText(ApiPath, ApiJson());
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new CaseHudFolderWatcher(_dir, () => changed.TrySetResult(), useWatcher: false,
            pollEvery: TimeSpan.FromMilliseconds(50), quiet: TimeSpan.FromMilliseconds(20));
        watcher.Start();
        File.Delete(ApiPath);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Watcher_runs_one_round_at_a_time_and_catches_up_afterwards()
    {
        var running = 0;
        var most = 0;
        var calls = 0;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new CaseHudFolderWatcher(_dir, () =>
        {
            var now = Interlocked.Increment(ref running);
            most = Math.Max(most, now);
            var n = Interlocked.Increment(ref calls);
            if (n == 1)
            {
                first.TrySetResult();
                release.Task.Wait(TimeSpan.FromSeconds(5));
            }
            else second.TrySetResult();
            Interlocked.Decrement(ref running);
        }, useWatcher: false, pollEvery: TimeSpan.FromHours(1), quiet: TimeSpan.FromMilliseconds(10));
        watcher.Poke();
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Changes during the round: no second round in parallel ...
        watcher.Poke();
        await Task.Delay(100);
        watcher.Poke();
        await Task.Delay(100);
        Assert.Equal(1, Volatile.Read(ref calls));
        release.TrySetResult();
        // ... but one afterwards.
        await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, most);
    }

    [Fact]
    public async Task Watcher_calls_nothing_after_dispose()
    {
        var calls = 0;
        var watcher = new CaseHudFolderWatcher(_dir, () => Interlocked.Increment(ref calls), useWatcher: false,
            pollEvery: TimeSpan.FromHours(1), quiet: TimeSpan.FromMilliseconds(10));
        watcher.Poke();
        watcher.Dispose();
        watcher.Poke();
        await Task.Delay(150);
        Assert.Equal(0, Volatile.Read(ref calls));
    }

    // ---- the bounded run of CaseHUD.exe --set-api=… (a child holding the pipe must not hang the call)

    private sealed class FakeCli(Task exited, Task<string> stderr, int exit = 0) : ICaseHudCliProcess
    {
        public int Kills;
        public bool Disposed;
        public Task WaitForExitAsync(CancellationToken ct) => exited.WaitAsync(ct);
        public int ExitCode => exit;
        public Task<string> Stderr => stderr;
        public void Kill() => Kills++;
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task Run_returns_the_exit_code_and_stderr()
    {
        var p = new FakeCli(Task.CompletedTask, Task.FromResult("api off"), exit: 1);
        Assert.Equal((1, "api off"), await CaseHudCli.RunAsync(p, CancellationToken.None));
        Assert.Equal(0, p.Kills);
        Assert.True(p.Disposed);
    }

    [Fact]
    public async Task Stderr_held_open_by_a_child_does_not_hang_the_call()
    {
        var never = new TaskCompletionSource<string>().Task;
        var p = new FakeCli(Task.CompletedTask, never, exit: 0);
        var run = CaseHudCli.RunAsync(p, CancellationToken.None, stderrGrace: TimeSpan.FromMilliseconds(50));
        Assert.Equal((0, ""), await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(p.Disposed);
    }

    [Fact]
    public async Task A_process_that_never_ends_is_killed_with_its_tree()
    {
        var p = new FakeCli(new TaskCompletionSource().Task, new TaskCompletionSource<string>().Task);
        var run = CaseHudCli.RunAsync(p, CancellationToken.None, timeout: TimeSpan.FromMilliseconds(50));
        Assert.Equal(((int?)null, ""), await run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, p.Kills);
        Assert.True(p.Disposed);
    }

    [Fact]
    public async Task Cancelling_kills_the_process_too()
    {
        using var cts = new CancellationTokenSource();
        var p = new FakeCli(new TaskCompletionSource().Task, new TaskCompletionSource<string>().Task);
        var run = CaseHudCli.RunAsync(p, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, p.Kills);
        Assert.True(p.Disposed);
    }

    // ---- the switch: CaseHUD.exe --set-api=on|off from the verified installation only

    private static readonly string[] Roots = [@"C:\Program Files", @"C:\Program Files (x86)"];

    [Theory]
    [InlineData(@"C:\Program Files\CaseHUD\CaseHUD.exe", true)]
    [InlineData(@"c:\program files (x86)\CaseHUD\casehud.exe", true)]
    [InlineData(@"C:\Program Files\CaseHUD.exe", false)]                                  // directly in the root: no own folder
    [InlineData(@"C:\Program Files\CaseHUD\Other.exe", false)]
    [InlineData(@"C:\Program Files\..\Users\x\CaseHUD\CaseHUD.exe", false)]
    [InlineData(@"C:\Program Files Evil\CaseHUD\CaseHUD.exe", false)]                     // prefix, not the folder
    [InlineData(@"C:\Users\x\AppData\Local\CaseHUD\CaseHUD.exe", false)]                 // user-writable
    [InlineData(@"\\server\share\CaseHUD\CaseHUD.exe", false)]
    [InlineData(@"CaseHUD\CaseHUD.exe", false)]
    [InlineData("", false)]
    public void Only_an_installation_below_Program_Files_may_be_run(string exe, bool expected) =>
        Assert.Equal(expected, CaseHudCli.IsTrustedInstallPath(exe, Roots));

    [Fact]
    public void No_trusted_roots_trust_nothing() =>
        Assert.False(CaseHudCli.IsTrustedInstallPath(Exe, [null, "", @"relative\path", @"\\server\share"]));

    [Fact]
    public async Task Switch_runs_the_trusted_exe_with_exactly_one_argument()
    {
        var runs = new List<(string Exe, string Args)>();
        var log = new List<string>();
        var sw = new CaseHudApiSwitch(() => Exe, (exe, args, _) => { runs.Add((exe, args)); return Task.FromResult<(int?, string)>((0, "")); }, log.Add);
        await sw.SetAsync(true);
        await sw.SetAsync(false);
        Assert.Equal([(Exe, "--set-api=on"), (Exe, "--set-api=off")], runs);
        Assert.All(log, l => Assert.DoesNotContain(Token, l));
    }

    [Fact]
    public async Task Switch_without_a_trusted_installation_runs_nothing()
    {
        var runs = 0;
        var sw = new CaseHudApiSwitch(() => null, (_, _, _) => { runs++; return Task.FromResult<(int?, string)>((0, "")); });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sw.SetAsync(true));
        Assert.Equal(Strings.Get("casehud.apiSwitchUntrusted"), ex.Message);
        Assert.Equal(0, runs);
    }

    [Fact]
    public async Task Non_zero_exit_shows_the_hint_and_logs_stderr_shortened()
    {
        var log = new List<string>();
        var stderr = "refusing: running as SYSTEM\r\n" + new string('x', 500) + "\u202E";
        var sw = new CaseHudApiSwitch(() => Exe, (_, _, _) => Task.FromResult<(int?, string)>((3, stderr)), log.Add);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sw.SetAsync(true));
        Assert.Equal(Strings.Get("casehud.apiSwitchFailed", 3), ex.Message);
        var line = Assert.Single(log);
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\u202E', line);
        Assert.Contains("refusing: running as SYSTEM", line);
        Assert.True(line.Length < CaseHudCli.MaxLogChars + 60);
    }

    [Fact]
    public async Task Timeout_is_its_own_message()
    {
        var sw = new CaseHudApiSwitch(() => Exe, (_, _, _) => Task.FromResult<(int?, string)>((null, "")));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sw.SetAsync(false));
        Assert.Equal(Strings.Get("casehud.apiSwitchTimeout"), ex.Message);
    }

    [Fact]
    public void Log_line_is_one_short_clean_line()
    {
        Assert.Equal("", CaseHudCli.LogLine(null));
        Assert.Equal("a b", CaseHudCli.LogLine("a\r\n\tb"));
        Assert.Equal(CaseHudCli.MaxLogChars + 1, CaseHudCli.LogLine(new string('y', 1000)).Length);
    }
}
