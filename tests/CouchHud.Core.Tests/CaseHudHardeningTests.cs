using System.Net;
using System.Security.Cryptography;
using System.Text;
using CouchHud.Core.Integrations.CaseHud;
using CouchHud.Core.Updates;

namespace CouchHud.Core.Tests;

/// <summary>Lease races, event loop survival, discovery against the installation, the install flow with its one-time token, and the lock rules.</summary>
public class CaseHudHardeningTests : IDisposable
{
    private const string Exe = @"C:\Program Files\CaseHUD\CaseHUD.exe";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-casehud-hard-" + Guid.NewGuid().ToString("N"));

    public CaseHudHardeningTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, string?, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            lock (Requests) Requests.Add((request.Method, request.RequestUri!.AbsolutePath, body));
            // A real handler honours the token; this one waits for the answer the test hands out, but stops when asked to.
            return await respond(request, body).WaitAsync(ct);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static CaseHudApiInfo Info() => new() { ApiVersion = 1, Version = "1.0.5", Port = 51234, Token = "s3cr3tTokenValue42", Pid = 1, Exe = Exe };

    private const string StatusJson = """{"version":"1.0.5","apiVersion":1,"sensors":{"ok":true},"power":true,"gaming":{"active":false},"rotation":{"enabled":false},"awtrix":{"enabled":false},"panels":[]}""";

    // ---- lease

    [Fact]
    public async Task Ending_the_lease_gives_up_after_the_timeout_when_CaseHUD_hangs()
    {
        var hang = new TaskCompletionSource<HttpResponseMessage>();
        var handler = new AsyncHandler((r, b) => b!.Contains("true") ? Task.FromResult(Json("{}")) : hang.Task);
        var client = new CaseHudClient(new HttpClient(handler), Info());
        var lease = new CaseHudGamingLease(() => client);
        await lease.BeginAsync("Doom");
        var started = DateTime.UtcNow;
        await lease.EndAsync(TimeSpan.FromMilliseconds(200));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
        Assert.Contains("\"active\":false", handler.Requests.Last().Body);   // the attempt was made
        Assert.False(lease.Active);
        hang.SetResult(Json("{}"));
    }

    [Fact]
    public async Task A_renewal_that_is_still_sending_cannot_bring_the_lease_back_after_the_end()
    {
        var release = new TaskCompletionSource();
        var first = true;
        var handler = new AsyncHandler(async (r, b) =>
        {
            if (first && b!.Contains("true")) { first = false; await release.Task; }
            return Json("{}");
        });
        var client = new CaseHudClient(new HttpClient(handler), Info());
        var now = DateTimeOffset.UtcNow;
        var lease = new CaseHudGamingLease(() => client, () => now);

        var begin = lease.BeginAsync("Doom");            // blocked inside the send
        var end = lease.EndAsync();                      // must wait for it, then send active:false
        await Task.Delay(100);
        release.SetResult();
        await begin;
        await end;
        now += TimeSpan.FromMinutes(5);
        await lease.TickAsync();                         // the game is over: no new lease

        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("\"active\":true", handler.Requests[0].Body);
        Assert.Equal("""{"active":false}""", handler.Requests[1].Body);
    }

    // ---- event loop

    private static Task<HttpResponseMessage> Sse(string text) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) });

    [Fact]
    public async Task Event_loop_survives_handlers_that_throw_and_odd_exceptions()
    {
        var calls = 0;
        var handler = new AsyncHandler((r, b) =>
        {
            calls++;
            if (calls == 1) throw new InvalidOperationException("odd");
            return Sse("event: status\ndata: " + StatusJson + "\n\n");
        });
        var client = new CaseHudClient(new HttpClient(handler), Info());
        var statuses = 0;
        var pauses = 0;
        using var cts = new CancellationTokenSource();
        var loop = new CaseHudEventLoop(new HttpClient(handler), () => client,
            _ => { statuses++; throw new InvalidOperationException("handler bug"); },
            _ => throw new InvalidOperationException("handler bug"),
            (_, _) => { if (++pauses == 3) cts.Cancel(); return Task.CompletedTask; });
        await loop.RunAsync(cts.Token);
        Assert.Equal(3, pauses);       // the loop went on after every error
        Assert.Equal(2, statuses);
    }

    [Fact]
    public async Task When_CaseHUD_refuses_the_stream_with_503_the_status_is_polled_every_five_seconds()
    {
        var handler = new AsyncHandler((r, b) => r.RequestUri!.AbsolutePath.EndsWith("/events")
            ? Task.FromResult(Json("""{"error":{"code":"too_many_clients","message":"x"}}""", HttpStatusCode.ServiceUnavailable))
            : Task.FromResult(Json(StatusJson)));
        var client = new CaseHudClient(new HttpClient(handler), Info());
        var statuses = 0;
        var pauses = new List<TimeSpan>();
        using var cts = new CancellationTokenSource();
        var loop = new CaseHudEventLoop(new HttpClient(handler), () => client, _ => statuses++, _ => { },
            (t, _) => { pauses.Add(t); if (pauses.Count == 3) cts.Cancel(); return Task.CompletedTask; });
        await loop.RunAsync(cts.Token);
        Assert.Equal(3, statuses);
        Assert.All(pauses, p => Assert.Equal(CaseHudEventLoop.PollEvery, p));
    }

    // ---- discovery against the installation

    private string ApiJson()
    {
        var path = Path.Combine(_dir, "api.json");
        File.WriteAllText(path, $$"""{"apiVersion":1,"version":"1.0.5","port":51234,"token":"s3cr3tTokenValue42","pid":1,"exe":"{{Exe.Replace("\\", "\\\\")}}"}""");
        return path;
    }

    [Fact]
    public void Running_process_must_be_the_installed_CaseHUD()
    {
        var path = ApiJson();
        var d = new CaseHudDiscovery(path, _ => Exe, new FakeRegistry()) { ExpectedExe = () => Exe };
        Assert.NotNull(d.ReadRunning());
        d.ExpectedExe = () => @"C:\Elsewhere\CaseHUD.exe";            // api.json and process agree, but it is not the installed one
        Assert.Null(d.ReadRunning());
        d.ExpectedExe = () => null;                                     // not installed at all
        Assert.Null(d.ReadRunning());
        d.ExpectedExe = null;                                           // test/dev environments
        Assert.NotNull(d.ReadRunning());
    }

    [Fact]
    public void Paths_are_compared_by_their_real_location()
    {
        var path = ApiJson();
        // The process shows a link; the canonical form (the shell asks the file handle) is the installed file.
        const string link = @"C:\link\CaseHUD.exe";
        string Canonical(string p) => p.EndsWith(@"link\CaseHUD.exe", StringComparison.OrdinalIgnoreCase) ? Exe : p;
        var d = new CaseHudDiscovery(path, _ => link, new FakeRegistry(), canonicalPath: Canonical) { ExpectedExe = () => Exe };
        Assert.NotNull(d.ReadRunning());
        var plain = new CaseHudDiscovery(path, _ => link, new FakeRegistry()) { ExpectedExe = () => Exe };
        Assert.Null(plain.ReadRunning());
    }

    // ---- install flow

    private const string Tag = "v1.0.5";
    private static readonly byte[] SetupBytes = Encoding.UTF8.GetBytes("pretend this is the CaseHUD setup");

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static string ReleaseJson(string tag)
    {
        var name = $"CaseHUD-Setup-{tag.TrimStart('v')}.exe";
        string Asset(string n) => $$"""{ "name": "{{n}}", "size": {{SetupBytes.Length}}, "browser_download_url": "https://github.com/kewe87/CaseHUD/releases/download/{{tag}}/{{n}}" }""";
        return $$"""{ "tag_name": "{{tag}}", "draft": false, "prerelease": false, "body": "x", "assets": [{{Asset(name)}}, {{Asset("SHA256SUMS.txt")}}] }""";
    }

    private sealed class Fixture
    {
        public string ReleaseTag = Tag;
        public string? InstalledVersion;
        public bool Running;
        public int Starts;
        public int RunnerCalls;
        public string? RunnerArgs;
        public Exception? RunnerThrows;
        public int ExitCode;
        public List<string?> Busy = [];
        public CaseHudInstaller Installer = null!;
        public CaseHudInstallGate Gate = null!;
        public string TempDir = "";
        public AsyncHandler Github = null!;
        public AsyncHandler Local = null!;
        public bool ApiChoice;
        public int ApiCalls;
        public int StartsAtApiCall = -1;
        public Exception? ApiThrows;
    }

    private Fixture Make(Func<DateTimeOffset>? clock = null, bool gameRunning = false)
    {
        var f = new Fixture { TempDir = Path.Combine(_dir, "temp") };
        f.Github = new AsyncHandler((r, _) =>
        {
            var url = r.RequestUri!.ToString();
            if (url.EndsWith("/releases/latest")) return Task.FromResult(Json(ReleaseJson(f.ReleaseTag)));
            if (url.EndsWith("SHA256SUMS.txt")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{Sha(SetupBytes)}  CaseHUD-Setup-{f.ReleaseTag.TrimStart('v')}.exe\n") });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(SetupBytes) });
        });
        f.Local = new AsyncHandler((r, _) =>
        {
            if (r.RequestUri!.AbsolutePath.EndsWith("/quit")) f.Running = false;   // CaseHUD ends itself
            return Task.FromResult(Json("{}", HttpStatusCode.Accepted));
        });
        var local = new CaseHudClient(new HttpClient(f.Local), Info());
        f.Gate = new CaseHudInstallGate(clock);
        var runner = new FakeRunner(f);
        f.Installer = new CaseHudInstaller(new CaseHudReleaseService(new HttpClient(f.Github), ""), f.Gate,
            () => f.InstalledVersion is null ? null : new CaseHudInstall(Exe, @"C:\Program Files\CaseHUD", f.InstalledVersion),
            () => f.Running ? local : null, runner, () => { f.Starts++; f.Running = true; return Task.CompletedTask; },
            () => gameRunning, () => (true, false), f.TempDir, text => f.Busy.Add(text), (_, _) => Task.CompletedTask,
            apiAfterInstall: () => f.ApiChoice,
            enableApi: _ =>
            {
                f.ApiCalls++;
                f.StartsAtApiCall = f.Starts;
                return f.ApiThrows is null ? Task.CompletedTask : Task.FromException(f.ApiThrows);
            });
        return f;
    }

    private sealed class FakeRunner(Fixture f) : ICaseHudSetupRunner
    {
        public Task<int> RunAsync(VerifiedSetup setup, string arguments, Action<string> step, CancellationToken ct)
        {
            f.RunnerCalls++;
            f.RunnerArgs = arguments;
            Assert.True(File.Exists(setup.Path));   // the verified file is still there while the setup runs
            if (f.RunnerThrows is not null) throw f.RunnerThrows;
            return Task.FromResult(f.ExitCode);
        }
    }

    [Fact]
    public async Task Install_without_a_valid_token_does_nothing()
    {
        var f = Make();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Installer.InstallAsync(null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Installer.InstallAsync(""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Installer.InstallAsync("made-up"));
        Assert.Equal(0, f.RunnerCalls);
        Assert.Empty(f.Github.Requests);   // not even a request
    }

    [Fact]
    public async Task First_installation_runs_the_setup_with_the_options_and_starts_CaseHUD()
    {
        var f = Make();
        var prepare = await f.Installer.PrepareAsync();
        Assert.NotNull(prepare.Token);
        Assert.False(prepare.Installed);
        Assert.True(prepare.Unsigned);
        Assert.Equal("1.0.5", prepare.Release!.Version.ToString());

        await f.Installer.InstallAsync(prepare.Token);
        Assert.Equal(1, f.RunnerCalls);
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=\"autostart\"", f.RunnerArgs);
        Assert.Equal(1, f.Starts);
        Assert.False(Directory.Exists(f.TempDir));   // the setup file is gone again
        Assert.Null(f.Busy.Last());
    }

    // ---- the API switch after installing (CaseHUD's API is off by default; never /API=1 from the elevated setup)

    [Fact]
    public async Task First_installation_switches_the_api_on_in_the_user_session_when_chosen_before_starting()
    {
        var f = Make();
        f.ApiChoice = true;
        await f.Installer.InstallAsync((await f.Installer.PrepareAsync()).Token);
        Assert.Equal(1, f.ApiCalls);
        Assert.Equal(0, f.StartsAtApiCall);                 // set first (config), then started: api.json appears at once
        Assert.Equal(1, f.Starts);
        Assert.DoesNotContain("/API", f.RunnerArgs!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Without_the_choice_the_api_stays_as_CaseHUD_ships_it()
    {
        var f = Make();
        f.ApiChoice = false;
        await f.Installer.InstallAsync((await f.Installer.PrepareAsync()).Token);
        Assert.Equal(0, f.ApiCalls);
        Assert.Equal(1, f.Starts);
    }

    [Fact]
    public async Task An_update_never_overrules_the_api_setting_made_in_CaseHUD()
    {
        var f = Make();
        f.ApiChoice = true;
        f.InstalledVersion = "1.0.1";
        await f.Installer.InstallAsync((await f.Installer.PrepareAsync()).Token);
        Assert.Equal(1, f.RunnerCalls);
        Assert.Equal(0, f.ApiCalls);
    }

    [Fact]
    public async Task A_failing_api_switch_does_not_fail_the_installation()
    {
        var f = Make();
        f.ApiChoice = true;
        f.ApiThrows = new InvalidOperationException("exit 3");
        await f.Installer.InstallAsync((await f.Installer.PrepareAsync()).Token);
        Assert.Equal(1, f.ApiCalls);
        Assert.Equal(1, f.Starts);
        Assert.Null(f.Busy.Last());
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void Setup_arguments_never_switch_the_api(bool autostart, bool fps) =>
        Assert.DoesNotContain("/API", CaseHudPolicy.InstallArguments(autostart, fps), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Switching_the_api_needs_the_unlock()
    {
        Assert.Contains("caseHudSetApi", CaseHudPolicy.All);
        Assert.False(CaseHudPolicy.Allowed("caseHudSetApi", locked: true));
        Assert.True(CaseHudPolicy.Allowed("caseHudSetApi", locked: false));
        Assert.DoesNotContain("caseHudInstallApi", CouchHud.Core.Guard.LockPolicy.HarmlessSettings);
    }

    [Fact]
    public async Task The_token_works_once()
    {
        var f = Make();
        var token = (await f.Installer.PrepareAsync()).Token;
        await f.Installer.InstallAsync(token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Installer.InstallAsync(token));
        Assert.Equal(1, f.RunnerCalls);
    }

    [Fact]
    public void Tokens_expire_and_only_the_newest_counts()
    {
        var now = DateTimeOffset.UtcNow;
        var gate = new CaseHudInstallGate(() => now);
        var release = CaseHudReleaseParser.Parse(ReleaseJson(Tag), false)!;
        var old = gate.Issue(release);
        var newest = gate.Issue(release);
        Assert.Null(gate.TryConsume(old));
        now += CaseHudInstallGate.Lifetime + TimeSpan.FromSeconds(1);
        Assert.Null(gate.TryConsume(newest));
        var fresh = gate.Issue(release);
        Assert.Equal(CaseHudReleaseIdentity.Of(release), gate.TryConsume(fresh));
    }

    [Fact]
    public async Task A_release_that_changed_after_the_dialog_is_refused()
    {
        var f = Make();
        var token = (await f.Installer.PrepareAsync()).Token;
        f.ReleaseTag = "v1.2.0";   // GitHub moved on between the dialog and the install
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Installer.InstallAsync(token));
        Assert.Equal(0, f.RunnerCalls);
        Assert.DoesNotContain(f.Github.Requests, r => r.Path.EndsWith(".exe"));   // nothing was downloaded
    }

    [Fact]
    public async Task No_token_for_a_downgrade_or_the_same_version_and_an_old_token_cannot_make_one()
    {
        var f = Make();
        f.InstalledVersion = "2.0.0";
        var same = await f.Installer.PrepareAsync();
        Assert.Null(same.Token);
        Assert.True(same.Installed);
        Assert.False(same.IsUpdate);

        f.InstalledVersion = "1.0.1";
        var token = (await f.Installer.PrepareAsync()).Token;
        Assert.NotNull(token);
        f.InstalledVersion = "2.0.0";    // meanwhile the PC got a newer one by other means
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Installer.InstallAsync(token));
        Assert.Equal(0, f.RunnerCalls);
    }

    [Fact]
    public async Task Update_of_a_running_CaseHUD_quits_it_first()
    {
        var f = Make();
        f.InstalledVersion = "1.0.1";
        f.Running = true;
        var token = (await f.Installer.PrepareAsync()).Token;
        await f.Installer.InstallAsync(token);
        Assert.Contains(f.Local.Requests, r => r.Path.EndsWith("/quit"));
        Assert.Equal(1, f.RunnerCalls);
        Assert.Equal(1, f.Starts);
    }

    [Fact]
    public async Task Declined_UAC_brings_a_closed_CaseHUD_back()
    {
        var f = Make();
        f.InstalledVersion = "1.0.1";
        f.Running = true;
        f.RunnerThrows = new SetupDeclinedException();
        var token = (await f.Installer.PrepareAsync()).Token;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Installer.InstallAsync(token));
        Assert.True(f.Running);
        Assert.Equal(1, f.Starts);
        Assert.False(Directory.Exists(f.TempDir));
    }

    [Fact]
    public async Task A_failing_setup_brings_CaseHUD_back_too_and_reports_the_code()
    {
        var f = Make();
        f.InstalledVersion = "1.0.1";
        f.Running = true;
        f.ExitCode = 5;
        var token = (await f.Installer.PrepareAsync()).Token;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => f.Installer.InstallAsync(token));
        Assert.Contains("5", ex.Message);
        Assert.Equal(1, f.Starts);
    }

    [Fact]
    public async Task A_CaseHUD_that_was_not_running_stays_off_when_the_setup_fails()
    {
        var f = Make();
        f.ExitCode = 1;
        var token = (await f.Installer.PrepareAsync()).Token;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Installer.InstallAsync(token));
        Assert.Equal(0, f.Starts);
    }

    [Fact]
    public async Task Nothing_is_installed_while_a_game_runs_and_the_token_stays_valid()
    {
        var game = true;
        var f = Make(gameRunning: false);
        var token = (await f.Installer.PrepareAsync()).Token;
        var blocked = Make(gameRunning: game);
        await Assert.ThrowsAsync<InvalidOperationException>(() => blocked.Installer.InstallAsync(blocked.Gate.Issue(CaseHudReleaseParser.Parse(ReleaseJson(Tag), false)!)));
        Assert.Equal(0, blocked.RunnerCalls);
        await f.Installer.InstallAsync(token);
    }

    // ---- lock rules

    private static string? BridgeJs()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "ui", "bridge.js");
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        return null;
    }

    [Fact]
    public void Every_call_the_page_can_make_is_known_to_the_lock_rules()
    {
        var js = BridgeJs();
        Assert.NotNull(js);
        var names = System.Text.RegularExpressions.Regex.Matches(js!, @"^\s*(caseHud\w+):", System.Text.RegularExpressions.RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToHashSet();
        Assert.True(names.SetEquals(CaseHudPolicy.All), $"bridge.js and CaseHudPolicy.All differ: {string.Join(", ", names.Except(CaseHudPolicy.All).Concat(CaseHudPolicy.All.Except(names)))}");
        Assert.True(CaseHudPolicy.OpenWhileLocked.IsSubsetOf(CaseHudPolicy.All));
    }

    [Fact]
    public void Locked_couch_lets_through_exactly_status_scenes_power_and_identify()
    {
        var open = CaseHudPolicy.All.Where(m => CaseHudPolicy.Allowed(m, locked: true)).OrderBy(m => m).ToArray();
        Assert.Equal(["caseHudActivateScene", "caseHudIdentify", "caseHudLighting", "caseHudNextScene", "caseHudPower", "caseHudPreview", "caseHudScenes", "caseHudStatus"], open);
        Assert.All(CaseHudPolicy.All, m => Assert.True(CaseHudPolicy.Allowed(m, locked: false)));
        Assert.False(CaseHudPolicy.Allowed("caseHudSomethingNew", locked: false));
        Assert.False(CaseHudPolicy.Allowed("caseHudSomethingNew", locked: true));
        Assert.False(CaseHudPolicy.Allowed("caseHudInstall", locked: true));
        Assert.False(CaseHudPolicy.Allowed("caseHudPrepare", locked: true));
    }
}
