using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CouchHud.Core.Integrations.CaseHud;
using CouchHud.Core.Localization;
using CouchHud.Core.Plugins;

namespace CouchHud.Core.Tests;

public class CaseHudTests : IDisposable
{
    private const string Exe = @"C:\Program Files\CaseHUD\CaseHUD.exe";
    private const string Token = "s3cr3tTokenValue42";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-casehud-" + Guid.NewGuid().ToString("N"));

    public CaseHudTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string ApiJsonPath => Path.Combine(_dir, "api.json");

    private void WriteApiJson(string? text = null) =>
        File.WriteAllText(ApiJsonPath, text ?? $$"""{"apiVersion":1,"version":"1.0.5","port":51234,"token":"{{Token}}","pid":1234,"exe":"{{Exe.Replace("\\", "\\\\")}}"}""");

    private CaseHudDiscovery Discovery(Func<int, string?> image, FakeRegistry? registry = null, Func<string, bool>? exists = null) =>
        new(ApiJsonPath, image, registry ?? new FakeRegistry(), exists ?? (_ => true));

    // ---- discovery

    [Fact]
    public void Running_instance_is_found_when_pid_runs_the_announced_exe()
    {
        WriteApiJson();
        var info = Discovery(pid => pid == 1234 ? Exe : null).ReadRunning();
        Assert.NotNull(info);
        Assert.Equal(51234, info!.Port);
        Assert.DoesNotContain(Token, info.ToString());
    }

    [Fact]
    public void Exe_comparison_ignores_case()
    {
        WriteApiJson();
        Assert.NotNull(Discovery(_ => Exe.ToUpperInvariant()).ReadRunning());
    }

    [Fact]
    public void Stale_api_json_after_a_crash_is_not_running()
    {
        WriteApiJson();
        Assert.Null(Discovery(_ => null).ReadRunning());   // pid is gone
    }

    [Fact]
    public void Pid_reused_by_another_program_is_not_running()
    {
        WriteApiJson();
        Assert.Null(Discovery(_ => @"C:\Windows\notepad.exe").ReadRunning());
    }

    [Theory]
    [InlineData("""{"apiVersion":2,"version":"2.0","port":51234,"token":"s3cr3tTokenValue42","pid":1234,"exe":"C:\\Program Files\\CaseHUD\\CaseHUD.exe"}""")]
    [InlineData("""{"apiVersion":1,"version":"1","port":0,"token":"s3cr3tTokenValue42","pid":1234,"exe":"C:\\Program Files\\CaseHUD\\CaseHUD.exe"}""")]
    [InlineData("""{"apiVersion":1,"version":"1","port":70000,"token":"s3cr3tTokenValue42","pid":1234,"exe":"C:\\Program Files\\CaseHUD\\CaseHUD.exe"}""")]
    [InlineData("""{"apiVersion":1,"version":"1","port":5,"token":"","pid":1234,"exe":"C:\\Program Files\\CaseHUD\\CaseHUD.exe"}""")]
    [InlineData("""{"apiVersion":1,"version":"1","port":5,"token":"s3cr3tTokenValue42","pid":0,"exe":"C:\\Program Files\\CaseHUD\\CaseHUD.exe"}""")]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("")]
    public void Wrong_or_broken_api_json_is_not_running(string json)
    {
        WriteApiJson(json);
        Assert.Null(Discovery(_ => Exe).ReadRunning());
    }

    [Fact]
    public void Missing_api_json_is_not_running() => Assert.Null(Discovery(_ => Exe).ReadRunning());

    [Fact]
    public void Installation_comes_from_the_uninstall_key_with_trailing_backslash()
    {
        var registry = new FakeRegistry();
        registry.Values[CaseHudInfo.UninstallKey] = new() { ["InstallLocation"] = @"C:\Program Files\CaseHUD\", ["DisplayVersion"] = "0.0.0-dev.57" };
        var install = Discovery(_ => null, registry).FindInstall();
        Assert.Equal(Exe, install!.Exe);
        Assert.Equal("0.0.0-dev.57", install.Version);
    }

    [Fact]
    public void Uninstall_entry_without_exe_or_location_is_not_installed()
    {
        var registry = new FakeRegistry();
        Assert.Null(Discovery(_ => null, registry).FindInstall());
        registry.Values[CaseHudInfo.UninstallKey] = new() { ["InstallLocation"] = @"C:\Program Files\CaseHUD\" };
        Assert.Null(Discovery(_ => null, registry, exists: _ => false).FindInstall());
    }

    [Fact]
    public void AppId_is_the_one_in_the_contract() =>
        Assert.EndsWith(@"\{6F3A9C2E-5B71-4C0D-9E8A-CA5E0D1A2B30}_is1", CaseHudInfo.UninstallKey);

    [Fact]
    public async Task Start_uses_background_and_waits_for_api_json()
    {
        var calls = 0;
        var started = new List<(string, string)>();
        var discovery = Discovery(_ => Exe);
        var info = await discovery.StartAsync(new CaseHudInstall(Exe, @"C:\Program Files\CaseHUD", "1.0.5"),
            (exe, args) => { started.Add((exe, args)); return null; },
            (_, _) => { if (++calls == 3) WriteApiJson(); return Task.CompletedTask; });
        Assert.Equal(51234, info.Port);
        Assert.Equal([(Exe, "--background")], started);
        Assert.Equal(3, calls);
    }

    private (CaseHudDiscovery Discovery, CaseHudCapabilityStore Store, CaseHudInstall Install) Probed(string version = "1.0.5")
    {
        var store = new CaseHudCapabilityStore(Path.Combine(_dir, "capability.json"), _ => "stamp-1");
        var discovery = Discovery(_ => Exe);
        discovery.Capabilities = store;
        return (discovery, store, new CaseHudInstall(Exe, @"C:\x", version));
    }

    [Fact]
    public async Task A_build_that_announces_the_api_is_remembered_as_capable()
    {
        var (discovery, store, install) = Probed();
        Assert.Equal(CaseHudCapability.Unknown, store.Get(install));
        await discovery.StartAsync(install, (_, _) => { WriteApiJson(); return null; });
        Assert.Equal(CaseHudCapability.Capable, store.Get(install));
    }

    [Theory]
    [InlineData("0.0.1")]
    [InlineData("1.0.0")]
    [InlineData("7.3.0")]
    public async Task Capability_does_not_depend_on_the_version_number(string version)
    {
        // The same behaviour gives the same result for any version string: only what the program does counts.
        var (discovery, store, install) = Probed(version);
        var ex = await Assert.ThrowsAsync<CaseHudException>(() => discovery.StartAsync(install, (_, _) => 2));
        Assert.Equal("needs_update", ex.Code);
        Assert.Equal(CaseHudCapability.Old, store.Get(install));
        File.Delete(Path.Combine(_dir, "capability.json"));
        await discovery.StartAsync(install, (_, _) => { WriteApiJson(); return null; });
        Assert.Equal(CaseHudCapability.Capable, store.Get(install));
    }

    [Fact]
    public async Task Exit_code_2_means_old_is_remembered_and_not_probed_again()
    {
        var (discovery, store, install) = Probed();
        var started = new List<string>();
        var ex = await Assert.ThrowsAsync<CaseHudException>(() => discovery.StartAsync(install, (_, args) => { started.Add(args); return 2; }, noTray: true));
        Assert.Equal("needs_update", ex.Code);
        Assert.Equal(["--background --no-tray", "--background", "--minimized"], started);
        Assert.Equal(CaseHudCapability.Old, store.Get(install));

        started.Clear();
        await Assert.ThrowsAsync<CaseHudException>(() => discovery.StartAsync(install, (_, args) => { started.Add(args); return 2; }, noTray: true));
        Assert.Equal(["--minimized"], started);   // remembered: no probing

        // Another build of the exe (new time stamp) is probed again.
        var newer = new CaseHudCapabilityStore(Path.Combine(_dir, "capability.json"), _ => "stamp-2");
        Assert.Equal(CaseHudCapability.Unknown, newer.Get(install));
    }

    [Fact]
    public async Task A_timeout_without_api_json_is_no_verdict_about_the_build()
    {
        var (discovery, store, install) = Probed();
        var ex = await Assert.ThrowsAsync<CaseHudException>(() => discovery.StartAsync(install, (_, _) => null, (_, _) => Task.CompletedTask));
        Assert.Equal("start_timeout", ex.Code);
        Assert.Equal(CaseHudCapability.Unknown, store.Get(install));
    }

    [Fact]
    public async Task A_crash_with_another_exit_code_is_not_taken_for_an_old_build()
    {
        var (discovery, store, install) = Probed();
        var ex = await Assert.ThrowsAsync<CaseHudException>(() => discovery.StartAsync(install, (_, _) => 1, (_, _) => Task.CompletedTask));
        Assert.Equal("start_timeout", ex.Code);
        Assert.Equal(CaseHudCapability.Unknown, store.Get(install));
    }

    [Fact]
    public async Task Start_adds_no_tray_without_a_taskbar()
    {
        var started = new List<string>();
        var discovery = Discovery(_ => Exe);
        await discovery.StartAsync(new CaseHudInstall(Exe, @"C:\x", "1.0.5"), (_, args) => { started.Add(args); WriteApiJson(); return null; }, noTray: true);
        Assert.Equal(["--background --no-tray"], started);
    }

    [Fact]
    public async Task Start_falls_back_when_only_no_tray_is_unknown()
    {
        var started = new List<string>();
        var discovery = Discovery(_ => Exe);
        var info = await discovery.StartAsync(new CaseHudInstall(Exe, @"C:\x", "1.0.5"), (_, args) =>
        {
            started.Add(args);
            if (args.Contains("--no-tray")) return 2;
            WriteApiJson();
            return null;
        }, noTray: true);
        Assert.Equal(51234, info.Port);
        Assert.Equal(["--background --no-tray", "--background"], started);
    }

    [Fact]
    public async Task Start_tries_again_when_the_process_cannot_start()
    {
        var started = new List<string>();
        var discovery = Discovery(_ => Exe);
        await discovery.StartAsync(new CaseHudInstall(Exe, @"C:\x", "1.0.5"), (_, args) =>
        {
            started.Add(args);
            if (args.Contains("--no-tray")) throw new System.ComponentModel.Win32Exception(2);
            WriteApiJson();
            return null;
        }, noTray: true);
        Assert.Equal(["--background --no-tray", "--background"], started);
    }

    [Theory]
    [InlineData(@"""C:\Program Files\CaseHUD\CaseHUD.exe"" --minimized", true, CaseHudCapability.Capable, @"""C:\Program Files\CaseHUD\CaseHUD.exe"" --background --no-tray")]
    [InlineData(@"""C:\Program Files\CaseHUD\CaseHUD.exe"" --minimized", false, CaseHudCapability.Capable, @"""C:\Program Files\CaseHUD\CaseHUD.exe"" --background")]
    [InlineData(@"""C:\Program Files\CaseHUD\CaseHUD.exe"" --minimized", true, CaseHudCapability.Unknown, @"""C:\Program Files\CaseHUD\CaseHUD.exe"" --minimized")]
    [InlineData(@"""C:\Program Files\CaseHUD\CaseHUD.exe"" --minimized", true, CaseHudCapability.Old, @"""C:\Program Files\CaseHUD\CaseHUD.exe"" --minimized")]
    [InlineData(@"""C:\Other\Tool.exe"" --minimized", true, CaseHudCapability.Capable, @"""C:\Other\Tool.exe"" --minimized")]
    [InlineData(@"""C:\Program Files\CaseHUD\CaseHUD.exe""", true, CaseHudCapability.Capable, @"""C:\Program Files\CaseHUD\CaseHUD.exe""")]
    public void Autostart_entry_of_casehud_is_adapted_by_capability_not_by_version(string command, bool noTray, CaseHudCapability capability, string expected) =>
        Assert.Equal(expected, CaseHudAutostart.Adapt(command, new CaseHudInstall(@"C:\Program Files\CaseHUD\CaseHUD.exe", @"C:\Program Files\CaseHUD", "1.0.5"), noTray, null, capability));

    [Fact]
    public async Task Start_gives_up_after_fifteen_seconds()
    {
        var waited = TimeSpan.Zero;
        var ex = await Assert.ThrowsAsync<CaseHudException>(() => Discovery(_ => Exe).StartAsync(
            new CaseHudInstall(Exe, @"C:\x", ""), (_, _) => null, (t, _) => { waited += t; return Task.CompletedTask; }));
        Assert.Equal("start_timeout", ex.Code);
        Assert.InRange(waited.TotalSeconds, 14.9, 15.1);
    }

    [Fact]
    public async Task Start_does_nothing_when_already_running()
    {
        WriteApiJson();
        var started = false;
        await Discovery(_ => Exe).StartAsync(new CaseHudInstall(Exe, @"C:\x", ""), (_, _) => { started = true; return null; });
        Assert.False(started);
    }

    // ---- client

    private sealed class FakeHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Url, string? Host, string? Token, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method, request.RequestUri!, request.Headers.Host, request.Headers.TryGetValues("X-Token", out var t) ? t.Single() : null, body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static CaseHudApiInfo Info() => new() { ApiVersion = 1, Version = "1.0.5", Port = 51234, Token = Token, Pid = 1234, Exe = Exe };

    private static (CaseHudClient Client, FakeHandler Handler) Client(Func<HttpRequestMessage, string?, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        return (new CaseHudClient(new HttpClient(handler), Info()), handler);
    }

    /// <summary>One line for an SSE data field; also when the source file has CRLF line ends (autocrlf), which would split the event.</summary>
    private static string StatusOneLine => string.Concat(StatusJson.Split('\n', '\r').Select(l => l.Trim()));

    private const string StatusJson = """
        {"version":"1.0.5","apiVersion":1,"sensors":{"ok":true,"error":""},"power":true,"gaming":{"active":true,"source":"external","game":"Doom"},
         "rotation":{"enabled":true,"scene":"gaming"},"awtrix":{"enabled":false,"devices":[{"name":"Clock","host":"10.0.0.5","model":"ulanzi","ok":true}]},
         "updateAvailable":"1.2.0",
         "panels":[{"id":"p1","name":"Fan 1","model":"LCD","w":480,"h":480,"connected":false,"mode":"hud","design":"clean-dark","brightness":80,"rotation":90,"fanNo":1,"error":"disconnected"}]}
        """;

    [Fact]
    public async Task Status_is_read_with_token_and_host_header()
    {
        var (client, handler) = Client((_, _) => Json(StatusJson));
        var s = await client.StatusAsync();
        Assert.True(s.Gaming.Active);
        Assert.Equal("external", s.Gaming.Source);
        Assert.Equal("1.2.0", s.UpdateAvailable);
        Assert.Equal("disconnected", s.Panels[0].Error);
        Assert.Equal(90, s.Panels[0].Rotation);
        Assert.Equal("ulanzi", s.Awtrix.Devices![0].Model);
        var r = handler.Requests.Single();
        Assert.Equal("http://127.0.0.1:51234/api/v1/status", r.Url.ToString());
        Assert.Equal("127.0.0.1:51234", r.Host);
        Assert.Equal(Token, r.Token);
    }

    [Fact]
    public async Task Every_endpoint_uses_the_documented_method_and_path()
    {
        var (client, handler) = Client((_, _) => Json("[]"));
        await client.PanelsAsync();
        await client.SetsAsync();
        await client.ScenesAsync();
        await client.ActivateSceneAsync("night");
        await client.NextSceneAsync();
        await client.SetRotationAsync(true);
        await client.PowerAsync(false);
        await client.SetAwtrixAsync(true);
        await client.IdentifyAsync(null);
        await client.IdentifyAsync("p1");
        await client.ApplySetAsync("neon", "dark", ["p1", "p2"]);
        await client.SetAutomationAsync(false);
        await client.QuitAsync();
        Assert.Equal(
        [
            "GET /api/v1/panels", "GET /api/v1/sets", "GET /api/v1/scenes",
            "POST /api/v1/scenes/night/activate", "POST /api/v1/rotation/next", "PATCH /api/v1/rotation", "POST /api/v1/power",
            "PATCH /api/v1/awtrix", "POST /api/v1/identify", "POST /api/v1/panels/p1/identify",
            "POST /api/v1/sets/apply", "PATCH /api/v1/automation", "POST /api/v1/quit"
        ], handler.Requests.Select(r => $"{r.Method} {r.Url.AbsolutePath}"));
        Assert.All(handler.Requests, r => Assert.Equal(Token, r.Token));
        Assert.Equal("""{"enabled":true}""", handler.Requests[5].Body);
        Assert.Equal("""{"on":false}""", handler.Requests[6].Body);
        Assert.Equal("""{"set":"neon","theme":"dark","panels":["p1","p2"]}""", handler.Requests[10].Body);
        Assert.Equal("""{"enabled":false}""", handler.Requests[11].Body);
    }

    [Fact]
    public async Task Panel_patch_sends_only_the_given_fields()
    {
        var (client, handler) = Client((_, _) => Json("""{"id":"p1","name":"Fan 1","model":"LCD","w":1,"h":1,"connected":true,"mode":"off","design":"d","brightness":30,"rotation":0,"fanNo":1}"""));
        var panel = await client.PatchPanelAsync("p1", new CaseHudPanelPatch(Brightness: 130, Mode: "off"));
        Assert.Equal("off", panel.Mode);
        var body = handler.Requests.Single().Body!;
        Assert.Contains("\"brightness\":100", body);   // clamped
        Assert.Contains("\"mode\":\"off\"", body);
        Assert.DoesNotContain("design", body);
        Assert.DoesNotContain("rotation", body);
        Assert.Equal(HttpMethod.Patch, handler.Requests.Single().Method);
    }

    [Theory]
    [InlineData("""{"mode":"media"}""", "media", null, null)]
    [InlineData("""{"theme":"light","brightness":40}""", null, 40, "light")]
    public void Panel_patch_from_the_page_takes_mode_brightness_and_theme(string json, string? mode, int? brightness, string? theme)
    {
        var patch = CaseHudPanelPatch.FromJson(System.Text.Json.JsonDocument.Parse(json).RootElement);
        Assert.Equal(new CaseHudPanelPatch(mode, brightness, theme), patch);
    }

    [Theory]
    // Design and mounting (rotation) are set in CaseHUD's window, not from the couch.
    [InlineData("""{"design":"neon-cpu"}""")]
    [InlineData("""{"rotation":90}""")]
    [InlineData("""{"brightness":50,"rotation":180}""")]
    [InlineData("""{"mode":"party"}""")]
    [InlineData("""{"theme":7}""")]
    [InlineData("""{"brightness":"50"}""")]
    [InlineData("""{"brightness":1.5}""")]
    [InlineData("""{}""")]
    [InlineData("""[]""")]
    public void Panel_patch_from_the_page_refuses_anything_else(string json) =>
        Assert.Throws<ArgumentException>(() => CaseHudPanelPatch.FromJson(System.Text.Json.JsonDocument.Parse(json).RootElement));

    [Fact]
    public async Task Gaming_lease_sends_game_and_seconds_and_ending_only_active_false()
    {
        var (client, handler) = Client((_, _) => Json("{}"));
        await client.GamingAsync(true, "Doom", 42, 120);
        await client.GamingAsync(false);
        Assert.Equal("""{"active":true,"game":"Doom","pid":42,"leaseSeconds":120}""", handler.Requests[0].Body);
        Assert.Equal("""{"active":false}""", handler.Requests[1].Body);
    }

    [Fact]
    public async Task Error_format_gives_code_message_and_status()
    {
        var (client, _) = Client((_, _) => Json("""{"error":{"code":"bad_request","message":"brightness must be 0-100"}}""", HttpStatusCode.BadRequest));
        var ex = await Assert.ThrowsAsync<CaseHudException>(() => client.PatchPanelAsync("p1", new CaseHudPanelPatch(Mode: "hud")));
        Assert.Equal("bad_request", ex.Code);
        Assert.Equal("brightness must be 0-100", ex.Message);
        Assert.Equal(400, ex.Status);
        Assert.DoesNotContain(Token, ex.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "not_found", "casehud.err.notFound")]
    [InlineData(HttpStatusCode.Forbidden, "unauthorized", "casehud.err.unauthorized")]
    [InlineData(HttpStatusCode.Forbidden, "forbidden", "casehud.err.forbidden")]
    public async Task Refusals_get_our_own_text_not_casehuds_english(HttpStatusCode status, string code, string key)
    {
        var (client, _) = Client((_, _) => Json("{\"error\":{\"code\":\"" + code + "\",\"message\":\"no such scene: Night\"}}", status));
        var ex = await Assert.ThrowsAsync<CaseHudException>(() => client.ActivateSceneAsync("Night"));
        Assert.Equal(code, ex.Code);
        Assert.Equal((int)status, ex.Status);
        Assert.Equal(CouchHud.Core.Localization.Strings.Get(key), ex.Message);
        Assert.DoesNotContain("no such scene", ex.Message);
    }

    [Fact]
    public void Status_reads_automation_and_conflicts_and_cleans_the_names()
    {
        var json = StatusJson.Replace("\"updateAvailable\"", """
            "automation":{"enabled":true,"activeRule":0,"when":"game","scene":"Gaming"},
            "conflicts":["OpenRGB.exe","\u202Eexe.LCM\u0007",7,null,"","openrgb.exe","a1","a2","a3","a4","a5","a6","a7","a8"],
            "updateAvailable"
            """);
        var s = System.Text.Json.JsonSerializer.Deserialize<CaseHudStatus>(json, CaseHudJson.Options)!;
        Assert.True(s.Automation!.Enabled);
        Assert.Equal(0, s.Automation.ActiveRule);
        Assert.Equal(CaseHudConflictsConverter.MaxCount, s.Conflicts!.Count);
        Assert.Equal("OpenRGB.exe", s.Conflicts[0]);
        Assert.Equal("exe.LCM", s.Conflicts[1]);          // bidi override and bell removed
        Assert.DoesNotContain("openrgb.exe", s.Conflicts); // the same name once
        Assert.DoesNotContain("", s.Conflicts);
        var longName = System.Text.Json.JsonSerializer.Deserialize<CaseHudStatus>(StatusJson.Replace("\"updateAvailable\"", "\"conflicts\":[\"" + new string('x', 300) + "\"],\"updateAvailable\""), CaseHudJson.Options)!;
        Assert.Equal(CaseHudConflictsConverter.MaxLength + 1, longName.Conflicts![0].Length);   // cut, with an ellipsis
    }

    [Theory]
    [InlineData("\"scene\":\"Night\",", "Night")]
    [InlineData("\"scene\":\"\",", "")]                       // no saved scene on screen: "", not "unknown"
    [InlineData("", null)]                                        // older build: the page falls back to /scenes, then rotation.scene
    [InlineData("\"scene\":7,", null)]
    [InlineData("\"scene\":\"\\u202ENight\\u0000\",", "Night")]
    public void Status_reads_the_scene_on_screen(string field, string? expected)
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<CaseHudStatus>(StatusJson.Replace("\"updateAvailable\"", field + "\"updateAvailable\""), CaseHudJson.Options)!;
        Assert.Equal(expected, s.Scene);
        Assert.Equal("gaming", s.Rotation.Scene);
    }

    [Fact]
    public void Names_are_cleaned_for_the_page_but_ids_stay_raw()
    {
        // Scenes are activated by id, displays addressed by id, sets applied by set and theme: those stay as CaseHUD sent them.
        var scenes = JsonSerializer.Deserialize<List<CaseHudScene>>(
            "[{\"id\":\"n\\u202Eight\",\"name\":\"\\u202ENight\\u0007\",\"active\":true},{\"id\":\"x\",\"name\":7,\"active\":false},{\"id\":\"y\",\"active\":false}]", CaseHudJson.Options)!;
        Assert.Equal("n\u202Eight", scenes[0].Id);
        Assert.Equal("Night", scenes[0].Name);
        Assert.Equal("", scenes[1].Name);   // no string: empty, never null
        Assert.Null(scenes[2].Name);        // missing: like any missing field (the page shows it as empty text)
        var sets = JsonSerializer.Deserialize<List<CaseHudSet>>("[{\"set\":\"neon\",\"theme\":\"dark\",\"name\":\"" + new string('n', 200) + "\"}]", CaseHudJson.Options)!;
        Assert.Equal("neon", sets[0].Set);
        Assert.Equal(CaseHudNameConverter.MaxLength + 1, sets[0].Name.Length);   // cut, with an ellipsis
        var status = JsonSerializer.Deserialize<CaseHudStatus>(StatusJson.Replace("\"Fan 1\"", "\"Fan\\u200B 1\\u202E\""), CaseHudJson.Options)!;
        Assert.Equal("Fan 1", status.Panels[0].Name);
        Assert.Equal("p1", status.Panels[0].Id);
        var panels = JsonSerializer.Deserialize<List<CaseHudPanel>>("[{\"id\":\"p\\u0001\",\"name\":null}]", CaseHudJson.Options)!;
        Assert.Equal("p\u0001", panels[0].Id);
        Assert.Equal("", panels[0].Name);
    }

    private static readonly CaseHudSet[] Sets = [new("neon", "dark", "Neon (dark)"), new("neon", "light", "Neon (light)"), new("mono", "dark", "Mono")];
    private static JsonElement Ids(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Apply_set_takes_only_listed_styles_known_themes_and_known_displays()
    {
        var all = CaseHudSets.Check("neon", "light", default, Sets, null);
        Assert.Equal(new CaseHudSetApply("neon", "light", null), all);
        Assert.Null(CaseHudSets.Check("neon", "dark", Ids("[]"), Sets, null).Panels);
        Assert.Null(CaseHudSets.Check("neon", "dark", Ids("null"), Sets, null).Panels);
        // Theme outside the documented two, a style /sets does not list, a style only listed in the other theme.
        Assert.Throws<ArgumentException>(() => CaseHudSets.Check("neon", "neon", default, Sets, null));
        Assert.Throws<ArgumentException>(() => CaseHudSets.Check("neon", "Dark", default, Sets, null));
        Assert.Throws<ArgumentException>(() => CaseHudSets.Check("evil", "dark", default, Sets, null));
        Assert.Throws<ArgumentException>(() => CaseHudSets.Check("mono", "light", default, Sets, null));
        Assert.Throws<ArgumentException>(() => CaseHudSets.Check("neon", "dark", default, [], null));
        // Displays: only known ids, each once; unknown ones are dropped.
        var some = CaseHudSets.Check("neon", "dark", Ids("[\"p1\",\"zz\",\"p1\",\"p2\"]"), Sets, ["p1", "p2", "p3"]);
        Assert.Equal(["p1", "p2"], some.Panels!);
        // A list without any known display must not become "all displays".
        Assert.Throws<ArgumentException>(() => CaseHudSets.Check("neon", "dark", Ids("[\"zz\"]"), Sets, ["p1"]));
        Assert.Throws<ArgumentException>(() => CaseHudSets.Check("neon", "dark", Ids("[\"p1\"]"), Sets, null));
        Assert.Throws<ArgumentException>(() => CaseHudSets.Check("neon", "dark", Ids("[\"p1\",7]"), Sets, ["p1"]));
        var many = "[" + string.Join(",", Enumerable.Range(0, CaseHudSets.MaxPanels + 1).Select(i => $"\"p{i}\"")) + "]";
        var known = Enumerable.Range(0, CaseHudSets.MaxPanels + 1).Select(i => $"p{i}").ToList();
        Assert.Throws<ArgumentException>(() => CaseHudSets.Check("neon", "dark", Ids(many), Sets, known));
        var max = "[" + string.Join(",", Enumerable.Range(0, CaseHudSets.MaxPanels).Select(i => $"\"p{i}\"")) + "]";
        Assert.Equal(CaseHudSets.MaxPanels, CaseHudSets.Check("neon", "dark", Ids(max), Sets, known).Panels!.Count);
    }

    [Theory]
    [InlineData("\"conflicts\":\"OpenRGB.exe\",")]
    [InlineData("\"conflicts\":{\"a\":1},")]
    [InlineData("\"conflicts\":null,")]
    [InlineData("")]
    public void Status_without_a_conflict_list_has_none(string field)
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<CaseHudStatus>(StatusJson.Replace("\"updateAvailable\"", field + "\"updateAvailable\""), CaseHudJson.Options)!;
        Assert.True(s.Conflicts is null || s.Conflicts.Count == 0);
        Assert.Null(s.Automation);
        Assert.Equal("1.2.0", s.UpdateAvailable);
    }

    [Fact]
    public async Task Error_without_json_still_gets_a_code()
    {
        var (client, _) = Client((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("nope") });
        var ex = await Assert.ThrowsAsync<CaseHudException>(() => client.StatusAsync());
        Assert.Equal("unauthorized", ex.Code);
    }

    [Fact]
    public async Task No_answer_is_unreachable_and_quit_tolerates_it()
    {
        var (client, _) = Client((_, _) => throw new HttpRequestException("refused"));
        Assert.Equal("unreachable", (await Assert.ThrowsAsync<CaseHudException>(() => client.StatusAsync())).Code);
        await client.QuitAsync();
    }

    [Fact]
    public async Task Garbage_json_is_a_bad_response()
    {
        var (client, _) = Client((_, _) => Json("{{{"));
        Assert.Equal("bad_response", (await Assert.ThrowsAsync<CaseHudException>(() => client.StatusAsync())).Code);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    public async Task Dangerous_ids_never_reach_the_url(string id)
    {
        var (client, handler) = Client((_, _) => Json("{}"));
        await Assert.ThrowsAsync<CaseHudException>(() => client.ActivateSceneAsync(id));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Slashes_in_ids_are_escaped()
    {
        var (client, handler) = Client((_, _) => Json("{}"));
        await client.ActivateSceneAsync("a/../b");
        Assert.Equal("/api/v1/scenes/a%2F..%2Fb/activate", handler.Requests.Single().Url.AbsolutePath.Replace("%2f", "%2F"));
    }

    [Fact]
    public async Task Previews_come_back_as_bytes_with_size()
    {
        var (client, handler) = Client((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });
        Assert.Equal([1, 2, 3], await client.PanelPreviewAsync("p1", 200));
        await client.PanelPreviewAsync("p1");
        await client.PanelPreviewAsync("p1", 9999);
        Assert.Equal("/api/v1/panels/p1/preview.png?size=200", handler.Requests[0].Url.PathAndQuery);
        Assert.Equal("/api/v1/panels/p1/preview.png", handler.Requests[1].Url.PathAndQuery);
        Assert.Equal("/api/v1/panels/p1/preview.png?size=480", handler.Requests[2].Url.PathAndQuery);
        await Assert.ThrowsAsync<CaseHudException>(() => client.PanelPreviewAsync(".."));
    }

    // ---- events

    [Fact]
    public async Task Event_stream_is_parsed_and_pings_are_ignored()
    {
        var sse = ": hello\nevent: ping\ndata: {}\n\nevent: status\ndata: " + StatusOneLine + "\n\n";
        var (client, handler) = Client((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse, Encoding.UTF8, "text/event-stream") });
        var seen = new List<string>();
        await client.ReadEventsAsync(new HttpClient(handler), (name, _) => seen.Add(name), CancellationToken.None);
        Assert.Equal(["ping", "status"], seen);
        Assert.Equal(Token, handler.Requests.Single().Token);
        Assert.Equal("127.0.0.1:51234", handler.Requests.Single().Host);
    }

    [Fact]
    public void Reconnect_pause_grows_to_thirty_seconds()
    {
        Assert.Equal([1, 2, 4, 8, 16, 30, 30], Enumerable.Range(1, 7).Select(n => (int)CaseHudEventLoop.Backoff(n).TotalSeconds));
    }

    [Fact]
    public async Task Event_loop_reports_status_and_reconnects_with_backoff()
    {
        var sse = "event: status\ndata: " + StatusOneLine + "\n\n";
        var (client, handler) = Client((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sse) });
        var statuses = 0;
        var connected = new List<bool>();
        var pauses = new List<TimeSpan>();
        using var cts = new CancellationTokenSource();
        var loop = new CaseHudEventLoop(new HttpClient(handler), () => client, _ => statuses++, connected.Add, (t, _) =>
        {
            pauses.Add(t);
            if (pauses.Count == 2) cts.Cancel();
            return Task.CompletedTask;
        });
        await loop.RunAsync(cts.Token);
        Assert.Equal(2, statuses);
        Assert.Equal([true, false, true, false], connected);
        Assert.All(pauses, p => Assert.Equal(TimeSpan.FromSeconds(1), p));   // a connection that delivered resets the pause
    }

    [Fact]
    public async Task Event_loop_without_caseHud_just_waits_longer_each_time()
    {
        var pauses = new List<TimeSpan>();
        using var cts = new CancellationTokenSource();
        var loop = new CaseHudEventLoop(new HttpClient(), () => null, _ => { }, _ => { }, (t, _) =>
        {
            pauses.Add(t);
            if (pauses.Count == 4) cts.Cancel();
            return Task.CompletedTask;
        });
        await loop.RunAsync(cts.Token);
        Assert.Equal([1, 2, 4, 8], pauses.Select(p => (int)p.TotalSeconds));
    }

    // ---- gaming lease with fake time

    [Fact]
    public async Task Lease_starts_renews_every_sixty_seconds_and_ends_with_active_false()
    {
        var now = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        var (client, handler) = Client((_, _) => Json("{}"));
        var lease = new CaseHudGamingLease(() => client, () => now);

        await lease.BeginAsync("Doom", 42);
        Assert.Single(handler.Requests);
        Assert.Contains("\"leaseSeconds\":120", handler.Requests[0].Body);

        now += TimeSpan.FromSeconds(30);
        await lease.TickAsync();
        Assert.Single(handler.Requests);   // not yet

        now += TimeSpan.FromSeconds(31);
        await lease.TickAsync();
        Assert.Equal(2, handler.Requests.Count);

        await lease.EndAsync();
        Assert.Equal("""{"active":false}""", handler.Requests[2].Body);
        now += TimeSpan.FromMinutes(5);
        await lease.TickAsync();
        await lease.EndAsync();
        Assert.Equal(3, handler.Requests.Count);   // over: no more traffic
    }

    [Fact]
    public async Task Lease_retries_after_ten_seconds_when_caseHud_is_not_there_yet()
    {
        var now = DateTimeOffset.UtcNow;
        var (client, handler) = Client((_, _) => Json("{}"));
        CaseHudClient? current = null;
        var lease = new CaseHudGamingLease(() => current, () => now);
        await lease.BeginAsync("Doom");
        Assert.Empty(handler.Requests);
        current = client;
        now += TimeSpan.FromSeconds(5);
        await lease.TickAsync();
        Assert.Empty(handler.Requests);
        now += TimeSpan.FromSeconds(6);
        await lease.TickAsync();
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Lease_survives_errors_and_tries_again()
    {
        var now = DateTimeOffset.UtcNow;
        var fail = true;
        var (client, handler) = Client((_, _) => fail ? Json("""{"error":{"code":"busy","message":"x"}}""", HttpStatusCode.ServiceUnavailable) : Json("{}"));
        var lease = new CaseHudGamingLease(() => client, () => now);
        await lease.BeginAsync("Doom");
        fail = false;
        now += TimeSpan.FromSeconds(11);
        await lease.TickAsync();
        Assert.Equal(2, handler.Requests.Count);
        await lease.EndAsync();   // errors on ending are swallowed too
    }

    // ---- release, hash, signature

    private const string Base = "https://github.com/kewe87/CaseHUD/releases/download/v1.0.5/";
    private const string Setup = "CaseHUD-Setup-1.0.5.exe";
    private static readonly byte[] SetupBytes = Encoding.UTF8.GetBytes("pretend this is the CaseHUD setup");

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static string Sums(byte[] setup) => $"{Sha([9, 9])}  CaseHUD-1.0.5-portable.zip\n{Sha(setup)}  {Setup}\n{Sha([8])}  CaseHUD-1.0.5-linux.tar.gz\n";

    private static string ReleaseJson(string tag = "v1.0.5", bool sums = true, bool sig = false, string prerelease = "false")
    {
        var names = new List<string> { $"CaseHUD-Setup-{tag.TrimStart('v')}.exe", $"CaseHUD-{tag.TrimStart('v')}-portable.zip" };
        if (sums) names.Add("SHA256SUMS.txt");
        if (sig) names.Add("SHA256SUMS.txt.sig");
        var list = string.Join(",", names.Select(n => $$"""{ "name": "{{n}}", "size": {{SetupBytes.Length}}, "browser_download_url": "https://github.com/kewe87/CaseHUD/releases/download/{{tag}}/{{n}}" }"""));
        return $$"""{ "tag_name": "{{tag}}", "draft": false, "prerelease": {{prerelease}}, "html_url": "https://github.com/kewe87/CaseHUD/releases/tag/{{tag}}", "body": "## Neu\n- Dinge", "assets": [{{list}}] }""";
    }

    [Fact]
    public void Release_with_checksums_is_installable()
    {
        var r = CaseHudReleaseParser.Parse(ReleaseJson(), signatureRequired: false)!;
        Assert.True(r.Installable);
        Assert.Equal(Setup, r.SetupName);
        Assert.Equal(Base + "SHA256SUMS.txt", r.SumsUrl);
        Assert.Null(r.SignatureUrl);
    }

    [Fact]
    public void Release_without_SHA256SUMS_is_unverifiable_and_not_installable()
    {
        var r = CaseHudReleaseParser.Parse(ReleaseJson(sums: false), false)!;
        Assert.Equal("unverifiable", r.Problem);
        Assert.False(r.Installable);
    }

    [Fact]
    public void With_a_key_set_a_missing_signature_file_blocks_the_release()
    {
        Assert.True(CaseHudReleaseParser.Parse(ReleaseJson(), false)!.Installable);
        Assert.Equal("noSignature", CaseHudReleaseParser.Parse(ReleaseJson(), true)!.Problem);
        Assert.True(CaseHudReleaseParser.Parse(ReleaseJson(sig: true), true)!.Installable);
    }

    [Fact]
    public void Assets_from_other_repositories_or_hosts_are_ignored()
    {
        var json = ReleaseJson().Replace("kewe87/CaseHUD/releases/download", "evil/CaseHUD/releases/download");
        Assert.Equal("noSetup", CaseHudReleaseParser.Parse(json, false)!.Problem);
    }

    [Fact]
    public void Dev_and_odd_versions_are_parsed_tolerantly()
    {
        Assert.Equal(new SemVer(0, 0, 0, "dev.57"), CaseHudVersion.Parse("0.0.0-dev.57"));
        Assert.Equal(new SemVer(1, 0, 5, "dev"), CaseHudVersion.Parse("1.0.5-dev"));
        Assert.Equal(new SemVer(1, 0, 5, ""), CaseHudVersion.Parse("CaseHUD 1.0.5 (build 7)"));
        Assert.Equal(new SemVer(0, 0, 0, ""), CaseHudVersion.Parse("???"));
        Assert.Equal(new SemVer(0, 0, 0, ""), CaseHudVersion.Parse(null));
    }

    [Fact]
    public void Update_rules_follow_versions_and_prereleases()
    {
        var stable = CaseHudReleaseParser.Parse(ReleaseJson(sig: true), true)!;
        Assert.True(CaseHudVersion.IsUpdate(stable, CaseHudVersion.Parse("1.0.1")));
        Assert.True(CaseHudVersion.IsUpdate(stable, CaseHudVersion.Parse("0.0.0-dev.57")));
        Assert.False(CaseHudVersion.IsUpdate(stable, CaseHudVersion.Parse("1.0.5")));
        var beta = CaseHudReleaseParser.Parse(ReleaseJson(tag: "v1.2.0-beta.1", sig: true, prerelease: "true"), true)!;
        Assert.False(CaseHudVersion.IsUpdate(beta, CaseHudVersion.Parse("1.0.5")));
        Assert.True(CaseHudVersion.IsUpdate(beta, CaseHudVersion.Parse("1.0.5-dev")));
    }

    // Release selection: signed + at least 1.0.0 + newer than installed.
    [Theory]
    [InlineData(false, "v1.0.5", "1.0.0", false)]   // unsigned (no .sig asset): never offered
    [InlineData(true, "v1.0.5", "1.0.5", false)]    // signed, same version
    [InlineData(true, "v1.0.5", "1.0.0", true)]     // signed, different and newer
    [InlineData(true, "v1.0.0", "1.0.5", false)]    // signed, different but older
    [InlineData(true, "v1.0.5", "", true)]          // installed version unknown
    public void Release_is_an_update_only_when_signed_and_really_newer(bool signed, string tag, string installed, bool expected)
    {
        var release = CaseHudReleaseParser.Parse(ReleaseJson(tag: tag, sig: signed), signatureRequired: true)!;
        Assert.Equal(expected, CaseHudVersion.IsUpdate(release, CaseHudVersion.Parse(installed)));
    }

    [Theory]
    [InlineData("v0.9.9")]
    [InlineData("v0.0.0-dev.57")]
    public void Versions_below_1_0_0_are_never_offered(string tag)
    {
        var release = CaseHudReleaseParser.Parse(ReleaseJson(tag: tag, sig: true), true)!;
        Assert.Equal("tooOld", release.Problem);
        Assert.False(release.Installable);
        Assert.False(CaseHudVersion.IsUpdate(release, CaseHudVersion.Parse("")));
        Assert.Equal(Strings.Get("casehud.noSignedRelease"), CaseHudIntegrity.ProblemText(release));
    }

    [Fact]
    public void Checksum_is_taken_from_the_multi_file_sha256sum_format()
    {
        Assert.Equal(Sha(SetupBytes), CaseHudIntegrity.ParseChecksum(Encoding.UTF8.GetBytes(Sums(SetupBytes)), Setup));
        Assert.Equal(Sha(SetupBytes), CaseHudIntegrity.ParseChecksum(Encoding.UTF8.GetBytes($"{Sha(SetupBytes).ToUpperInvariant()} *{Setup}\r\n"), Setup));
    }

    [Fact]
    public void Checksum_file_with_crlf_lines_is_read_and_signed_by_its_exact_bytes()
    {
        // CaseHUD's SHA256SUMS.txt had Windows line ends: every line ends in \r, also the one we look for.
        var crlf = Encoding.UTF8.GetBytes(Sums(SetupBytes).Replace("\n", "\r\n"));
        Assert.Equal(Sha(SetupBytes), CaseHudIntegrity.ParseChecksum(crlf, Setup));
        Assert.Equal(Sha([9, 9]), CaseHudIntegrity.ParseChecksum(crlf, "CaseHUD-1.0.5-portable.zip"));
        // The signature covers the bytes as they are, \r included.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportSubjectPublicKeyInfoPem();
        CaseHudIntegrity.VerifySignature(crlf, Sign(key, crlf), pem);
        var lf = Encoding.UTF8.GetBytes(Sums(SetupBytes));
        Assert.Throws<CatalogException>(() => CaseHudIntegrity.VerifySignature(lf, Sign(key, crlf), pem));
    }

    [Theory]
    [InlineData("")]
    [InlineData("nothex  CaseHUD-Setup-1.0.5.exe\n")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000  other.exe\n")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000 CaseHUD-Setup-1.0.5.exe.sig\n")]
    public void Bad_or_wrong_checksum_files_are_refused(string text) =>
        Assert.Throws<CatalogException>(() => CaseHudIntegrity.ParseChecksum(Encoding.UTF8.GetBytes(text), Setup));

    [Fact]
    public void Conflicting_hashes_for_the_same_file_are_refused()
    {
        var text = $"{Sha([1])}  {Setup}\n{Sha([2])}  {Setup}\n";
        Assert.Throws<CatalogException>(() => CaseHudIntegrity.ParseChecksum(Encoding.UTF8.GetBytes(text), Setup));
    }

    private static string Sign(ECDsa key, byte[] data, string context = CaseHudInfo.ReleaseContext) =>
        Convert.ToBase64String(key.SignData(CatalogSignature.WithContext(context, data), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) + "\n";   // as released: DER, base64, one line, LF

    [Fact]
    public void Signature_is_not_required_without_a_key_but_mandatory_with_one()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var sums = Encoding.UTF8.GetBytes(Sums(SetupBytes));
        CaseHudIntegrity.VerifySignature(sums, null, "");
        CaseHudIntegrity.VerifySignature(sums, "garbage", "");
        var pem = key.ExportSubjectPublicKeyInfoPem();
        CaseHudIntegrity.VerifySignature(sums, Sign(key, sums), pem);
        Assert.Throws<CatalogException>(() => CaseHudIntegrity.VerifySignature(sums, null, pem));
        Assert.Throws<CatalogException>(() => CaseHudIntegrity.VerifySignature(sums, "", pem));
    }

    [Fact]
    public void Signature_of_another_key_context_or_content_is_refused()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportSubjectPublicKeyInfoPem();
        var sums = Encoding.UTF8.GetBytes(Sums(SetupBytes));
        Assert.Throws<CatalogException>(() => CaseHudIntegrity.VerifySignature(sums, Sign(other, sums), pem));
        Assert.Throws<CatalogException>(() => CaseHudIntegrity.VerifySignature(sums, Sign(key, sums, CatalogSignature.ReleaseContext), pem));   // a CouchHUD release signature is no CaseHUD one
        Assert.Throws<CatalogException>(() => CaseHudIntegrity.VerifySignature(sums, Sign(key, sums, CatalogSignature.CatalogContext), pem));
        Assert.Throws<CatalogException>(() => CaseHudIntegrity.VerifySignature(Encoding.UTF8.GetBytes("changed"), Sign(key, sums), pem));
    }

    private (CaseHudReleaseService Service, FakeHandler Handler) Service(string pem, Func<HttpRequestMessage, string?, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        return (new CaseHudReleaseService(new HttpClient(handler), pem), handler);
    }

    private static Func<HttpRequestMessage, string?, HttpResponseMessage> Github(byte[]? setup = null, string? sums = null, string? sig = null) => (r, _) =>
    {
        var url = r.RequestUri!.ToString();
        if (url.EndsWith("/releases/latest")) return Json(ReleaseJson(sig: sig is not null));
        if (url.EndsWith("SHA256SUMS.txt.sig")) return new(HttpStatusCode.OK) { Content = new StringContent(sig ?? "") };
        if (url.EndsWith("SHA256SUMS.txt")) return new(HttpStatusCode.OK) { Content = new StringContent(sums ?? Sums(SetupBytes)) };
        return new(HttpStatusCode.OK) { Content = new ByteArrayContent(setup ?? SetupBytes) };
    };

    [Fact]
    public async Task Check_finds_the_update_for_an_old_install_and_caches()
    {
        var (service, handler) = Service("", Github());
        var check = await service.CheckAsync("1.0.1");
        Assert.True(check.UpdateAvailable);
        await service.CheckAsync("1.0.1");
        Assert.Single(handler.Requests);   // second one from the cache
        Assert.False((await service.CheckAsync("1.0.5")).UpdateAvailable);
    }

    [Fact]
    public async Task Check_translates_404_and_rate_limit_and_network_errors()
    {
        Assert.Equal("unavailable", (await Service("", (_, _) => new(HttpStatusCode.NotFound)).Service.CheckAsync(null)).ErrorCode);
        Assert.Equal("unavailable", (await Service("", (_, _) => new(HttpStatusCode.Unauthorized)).Service.CheckAsync(null)).ErrorCode);
        Assert.Equal("unavailable", (await Service("", (_, _) => new(HttpStatusCode.Forbidden)).Service.CheckAsync(null)).ErrorCode);
        Assert.Equal("rateLimit", (await Service("", (_, _) => new(HttpStatusCode.TooManyRequests)).Service.CheckAsync(null)).ErrorCode);
        Assert.Equal("unreachable", (await Service("", (_, _) => throw new HttpRequestException()).Service.CheckAsync(null)).ErrorCode);
    }

    [Fact]
    public async Task Download_with_matching_hash_passes_and_keeps_the_file()
    {
        var (service, _) = Service("", Github());
        var release = CaseHudReleaseParser.Parse(ReleaseJson(), false)!;
        using var verified = await service.DownloadAsync(release, Path.Combine(_dir, "tmp"));
        Assert.Equal(Sha(SetupBytes), verified.Sha256);
        Assert.Equal(Setup, Path.GetFileName(verified.Path));
    }

    [Fact]
    public async Task Download_with_wrong_hash_is_refused_and_leaves_nothing()
    {
        var (service, _) = Service("", Github(setup: [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33]));
        var release = CaseHudReleaseParser.Parse(ReleaseJson(), false)!;
        await Assert.ThrowsAsync<CatalogException>(() => service.DownloadAsync(release, Path.Combine(_dir, "tmp")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_dir, "tmp")));
    }

    [Fact]
    public async Task Download_of_a_release_without_checksums_is_never_attempted()
    {
        var (service, handler) = Service("", Github());
        var release = CaseHudReleaseParser.Parse(ReleaseJson(sums: false), false)!;
        await Assert.ThrowsAsync<CatalogException>(() => service.DownloadAsync(release, Path.Combine(_dir, "tmp")));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Download_with_a_key_needs_a_valid_signature()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var sumsText = Sums(SetupBytes);
        var good = Sign(key, Encoding.UTF8.GetBytes(sumsText));
        var release = CaseHudReleaseParser.Parse(ReleaseJson(sig: true), true)!;

        var (ok, _) = Service(key.ExportSubjectPublicKeyInfoPem(), Github(sums: sumsText, sig: good));
        using (await ok.DownloadAsync(release, Path.Combine(_dir, "ok"))) { }

        var (bad, _) = Service(key.ExportSubjectPublicKeyInfoPem(), Github(sums: sumsText, sig: Sign(key, Encoding.UTF8.GetBytes("other"))));
        await Assert.ThrowsAsync<CatalogException>(() => bad.DownloadAsync(release, Path.Combine(_dir, "bad")));
    }

    [Fact]
    public void The_built_in_release_key_is_the_approved_one()
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(CaseHudInfo.ReleasePublicKeyPem);
        Assert.Equal(256, key.KeySize);
        Assert.Equal(CaseHudInfo.ReleaseKeyFingerprint, Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())).ToLowerInvariant());
        Assert.Equal("cfb6863f23c892a53c93786dd1ee78478618aed680c5603fbc3955c1dde4abaf", CaseHudInfo.ReleaseKeyFingerprint);
        Assert.True(new CaseHudReleaseService(new HttpClient()).SignatureRequired);
        Assert.False(CaseHudPolicy.NeedsUnsignedConfirmation(CaseHudInfo.ReleasePublicKeyPem));
    }

    [Fact]
    public void A_signature_in_P1363_format_is_refused_only_DER_counts()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var sums = Encoding.UTF8.GetBytes(Sums(SetupBytes));
        var p1363 = Convert.ToBase64String(key.SignData(CatalogSignature.WithContext(CaseHudInfo.ReleaseContext, sums), HashAlgorithmName.SHA256));
        Assert.Throws<CatalogException>(() => CaseHudIntegrity.VerifySignature(sums, p1363, key.ExportSubjectPublicKeyInfoPem()));
    }

    [Fact]
    public async Task Download_with_a_key_refuses_tampered_checksums_and_a_missing_sig_file()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportSubjectPublicKeyInfoPem();
        var sumsText = Sums(SetupBytes);
        var signed = Sign(key, Encoding.UTF8.GetBytes(sumsText));

        var tampered = sumsText.Replace(Sha(SetupBytes), Sha([1]));   // signature belongs to the original file
        var (service, _) = Service(pem, Github(sums: tampered, sig: signed));
        var release = CaseHudReleaseParser.Parse(ReleaseJson(sig: true), true)!;
        await Assert.ThrowsAsync<CatalogException>(() => service.DownloadAsync(release, Path.Combine(_dir, "tamper")));

        var noSig = CaseHudReleaseParser.Parse(ReleaseJson(), true)!;   // no .sig asset: not even installable
        Assert.False(noSig.Installable);
        await Assert.ThrowsAsync<CatalogException>(() => service.DownloadAsync(noSig, Path.Combine(_dir, "nosig")));
    }

    [Fact]
    public async Task Private_repository_backs_off_for_twelve_hours_and_manual_check_asks_again()
    {
        var now = DateTimeOffset.UtcNow;
        var handler = new FakeHandler((_, _) => new(HttpStatusCode.NotFound));
        var service = new CaseHudReleaseService(new HttpClient(handler), "", now: () => now);
        var first = await service.CheckAsync("1.0.0");
        Assert.Equal("unavailable", first.ErrorCode);
        Assert.Equal(Strings.Get("casehud.downloadsUnavailable"), first.Error);
        Assert.Single(handler.Requests);

        // Automatic checks inside the backoff do not touch the network.
        for (var i = 0; i < 5; i++) Assert.Equal("unavailable", (await service.CheckAsync("1.0.0")).ErrorCode);
        now += TimeSpan.FromHours(11);
        Assert.Equal("unavailable", (await service.CheckAsync("1.0.0")).ErrorCode);
        Assert.Single(handler.Requests);

        // A manual check always asks.
        await service.CheckAsync("1.0.0", force: true);
        Assert.Equal(2, handler.Requests.Count);

        // After 12 hours the automatic check asks again.
        now += TimeSpan.FromHours(12.5);
        await service.CheckAsync("1.0.0");
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Backoff_ends_when_GitHub_answers_again()
    {
        var up = false;
        var service = Service("", (r, _) => up ? Github()(r, null) : new(HttpStatusCode.NotFound)).Service;
        Assert.Equal("unavailable", (await service.CheckAsync("1.0.0")).ErrorCode);
        up = true;
        Assert.True((await service.CheckAsync("0.9.0", force: true)).Release is not null);
        Assert.True((await service.CheckAsync("0.9.0")).UpdateAvailable);
    }

    [Fact]
    public async Task A_missing_checksum_or_signature_file_is_unavailable_but_sets_no_global_pause()
    {
        var (service, handler) = Service("", (r, _) => r.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt") ? new(HttpStatusCode.NotFound) : Github()(r, null));
        var release = CaseHudReleaseParser.Parse(ReleaseJson(), false)!;
        var ex = await Assert.ThrowsAsync<CatalogException>(() => service.DownloadAsync(release, Path.Combine(_dir, "none")));
        Assert.Equal(Strings.Get("casehud.downloadsUnavailable"), ex.Message);
        Assert.Single(handler.Requests);
        // One broken asset must not silence the release check for 12 hours.
        Assert.NotNull((await service.CheckAsync("0.9.0")).Release);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task A_403_without_rate_limit_pauses_only_briefly()
    {
        var now = DateTimeOffset.UtcNow;
        var handler = new FakeHandler((_, _) => new(HttpStatusCode.Forbidden));
        var service = new CaseHudReleaseService(new HttpClient(handler), "", now: () => now);
        Assert.Equal("unavailable", (await service.CheckAsync("1.0.0")).ErrorCode);
        await service.CheckAsync("1.0.0");
        Assert.Single(handler.Requests);
        now += TimeSpan.FromMinutes(16);
        await service.CheckAsync("1.0.0");
        Assert.Equal(2, handler.Requests.Count);

        // With the limit used up it is a rate limit, not "unavailable".
        var limited = new CaseHudReleaseService(new HttpClient(new FakeHandler((_, _) => new(HttpStatusCode.Forbidden) { Headers = { { "X-RateLimit-Remaining", "0" } } })), "");
        Assert.Equal("rateLimit", (await limited.CheckAsync("1.0.0")).ErrorCode);
    }

    [Fact]
    public async Task An_unreadable_installed_version_gets_no_update_hint_but_can_still_be_installed()
    {
        var (service, _) = Service("", Github());
        var check = await service.CheckAsync("???");
        Assert.NotNull(check.Release);
        Assert.False(check.UpdateAvailable);
        Assert.False(CaseHudVersion.IsReadable("???"));
        Assert.True(CaseHudVersion.IsReadable("1.0.0-dev"));
    }

    [Fact]
    public async Task A_relative_redirect_location_is_resolved_and_the_result_still_checked()
    {
        var (service, _) = Service("", (r, _) => r.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt")
            ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("/", UriKind.Relative) } }
            : Github()(r, null));
        var release = CaseHudReleaseParser.Parse(ReleaseJson(), false)!;
        // The relative location resolves; a host outside GitHub is refused as a CatalogException either way.
        await Assert.ThrowsAsync<CatalogException>(() => service.DownloadAsync(release, Path.Combine(_dir, "redir")));
    }

    [Fact]
    public void An_old_verdict_expires_after_seven_days_a_capable_one_does_not()
    {
        var now = DateTimeOffset.UtcNow;
        var file = Path.Combine(_dir, "cap-expiry.json");
        var store = new CaseHudCapabilityStore(file, _ => "s", () => now);
        var install = new CaseHudInstall(Exe, @"C:\x", "1.0.5");
        store.Set(install, capable: false);
        now += TimeSpan.FromDays(6);
        Assert.Equal(CaseHudCapability.Old, store.Get(install));
        now += TimeSpan.FromDays(2);
        Assert.Equal(CaseHudCapability.Unknown, store.Get(install));
        store.Set(install, capable: true);
        now += TimeSpan.FromDays(100);
        Assert.Equal(CaseHudCapability.Capable, store.Get(install));
    }

    [Fact]
    public void The_capability_file_is_written_atomically_and_a_huge_or_broken_file_counts_as_unknown()
    {
        var file = Path.Combine(_dir, "cap-file.json");
        var store = new CaseHudCapabilityStore(file, _ => "s");
        var install = new CaseHudInstall(Exe, @"C:\x", "1.0.5");
        store.Set(install, capable: true);
        Assert.False(File.Exists(file + ".tmp"));
        File.WriteAllText(file, new string('x', 5000));
        Assert.Equal(CaseHudCapability.Unknown, store.Get(install));
        File.WriteAllText(file, "{broken");
        Assert.Equal(CaseHudCapability.Unknown, store.Get(install));
    }

    [Fact]
    public void The_texts_name_no_version_number()
    {
        foreach (var key in new[] { "casehud.noSignedRelease", "casehud.downloadsUnavailable", "casehud.needsUpdate" })
            Assert.DoesNotMatch(@"\d+\.\d+", Strings.Get(key));
    }

    [Fact]
    public async Task Download_redirects_only_to_github_hosts()
    {
        var (service, _) = Service("", (r, _) => r.RequestUri!.Host == "github.com" && r.RequestUri.AbsolutePath.EndsWith("SHA256SUMS.txt")
            ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://evil.example/x") } }
            : Github()(r, null));
        var release = CaseHudReleaseParser.Parse(ReleaseJson(), false)!;
        await Assert.ThrowsAsync<CatalogException>(() => service.DownloadAsync(release, Path.Combine(_dir, "tmp")));
    }

    // ---- install arguments and lock rules

    [Fact]
    public async Task Open_gui_posts_and_old_versions_show_404_or_405_status()
    {
        var (client, handler) = Client((_, _) => new HttpResponseMessage(HttpStatusCode.Accepted) { Content = new StringContent("{}") });
        await client.OpenGuiAsync();
        Assert.Equal("POST /api/v1/gui/open", $"{handler.Requests.Single().Method} {handler.Requests.Single().Url.AbsolutePath}");
        foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed, HttpStatusCode.ServiceUnavailable })
        {
            var (old, _) = Client((_, _) => Json("""{"error":{"code":"x","message":"y"}}""", status));
            Assert.Equal((int)status, (await Assert.ThrowsAsync<CaseHudException>(() => old.OpenGuiAsync())).Status);
        }
    }

    [Fact]
    public void Unsigned_install_needs_the_users_confirmation_until_a_key_is_set()
    {
        Assert.True(CaseHudPolicy.NeedsUnsignedConfirmation(""));
        Assert.True(CaseHudPolicy.NeedsUnsignedConfirmation("  "));
        Assert.False(CaseHudPolicy.NeedsUnsignedConfirmation("-----BEGIN PUBLIC KEY-----"));
    }

    [Fact]
    public void Install_arguments_carry_the_chosen_tasks()
    {
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=\"autostart,fps\"", CaseHudPolicy.InstallArguments(true, true));
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=\"fps\"", CaseHudPolicy.InstallArguments(false, true));
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=\"\"", CaseHudPolicy.InstallArguments(false, false));
    }

    [Theory]
    [InlineData("caseHudStatus", true)]
    [InlineData("caseHudActivateScene", true)]
    [InlineData("caseHudPower", true)]
    [InlineData("caseHudIdentify", true)]
    [InlineData("caseHudScenes", true)]
    [InlineData("caseHudPatchPanel", false)]
    [InlineData("caseHudApplySet", false)]
    [InlineData("caseHudRotation", false)]
    [InlineData("caseHudAutomation", false)]
    [InlineData("caseHudNextScene", true)]
    [InlineData("caseHudAwtrix", false)]
    [InlineData("caseHudInstall", false)]
    [InlineData("caseHudUpdate", false)]
    [InlineData("caseHudStart", false)]
    [InlineData("caseHudOpenGui", false)]
    [InlineData("caseHudPatchLighting", false)]
    public void Couch_lock_allows_only_status_scene_power_and_identify(string method, bool open) =>
        Assert.Equal(open, CaseHudPolicy.OpenWhileLocked.Contains(method));

    [Theory]
    // Building and importing is done in CaseHUD's window: these calls are gone, so the host refuses them locked or not.
    [InlineData("caseHudDesigns")]
    [InlineData("caseHudCommunity")]
    [InlineData("caseHudImport")]
    public void Removed_editor_calls_are_unknown(string method)
    {
        Assert.DoesNotContain(method, CaseHudPolicy.All);
        Assert.False(CaseHudPolicy.Allowed(method, locked: false));
    }

    [Fact]
    public void Automation_switch_needs_the_unlock_like_the_rotation()
    {
        Assert.Contains("caseHudAutomation", CaseHudPolicy.All);
        Assert.False(CaseHudPolicy.Allowed("caseHudAutomation", locked: true));
        Assert.True(CaseHudPolicy.Allowed("caseHudAutomation", locked: false));
        Assert.Equal(CaseHudPolicy.Allowed("caseHudRotation", locked: true), CaseHudPolicy.Allowed("caseHudAutomation", locked: true));
    }
}
