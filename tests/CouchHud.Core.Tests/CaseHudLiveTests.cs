using System.Diagnostics;
using CouchHud.Core.Integrations.CaseHud;

namespace CouchHud.Core.Tests;

/// <summary>Runs only when CASEHUD_EXE points to a CaseHUD.exe that was copied into its own folder.</summary>
public sealed class ManualFactAttribute : FactAttribute
{
    public ManualFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CASEHUD_EXE")))
            Skip = "manual: set CASEHUD_EXE to a copy of CaseHUD.exe to run it against a --demo server";
    }
}

/// <summary>
/// The client against the real CaseHUD demo server (<c>CaseHUD.exe --demo --data &lt;tmp&gt; --background</c>): every endpoint
/// and the event stream, then POST /quit. Never touches the installed CaseHUD or %APPDATA%\casehud (own data folder).
/// </summary>
public class CaseHudLiveTests
{
    private static string? ImageOf(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.MainModule?.FileName; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    [ManualFact]
    public async Task Client_works_against_the_real_demo_server()
    {
        var exe = Environment.GetEnvironmentVariable("CASEHUD_EXE")!;
        var data = Path.Combine(Path.GetTempPath(), "couchhud-casehud-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(data);
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        var discovery = new CaseHudDiscovery(Path.Combine(data, "api.json"), ImageOf, new FakeRegistry());
        CaseHudClient? client = null;
        try
        {
            var info = await discovery.StartAsync(new CaseHudInstall(exe, Path.GetDirectoryName(exe)!, ""),
                (path, _) =>
                {
                    Process.Start(new ProcessStartInfo(path) { ArgumentList = { "--demo", "--data", data, "--background" }, UseShellExecute = false })?.Dispose();
                    return null;
                });
            client = new CaseHudClient(http, info);

            var status = await client.StatusAsync();
            Assert.Equal(1, status.ApiVersion);
            Assert.NotEmpty(status.Panels);
            var panel = status.Panels[0];
            Assert.Equal((await client.PanelsAsync()).Count, status.Panels.Count);

            var sets = await client.SetsAsync();
            Assert.NotEmpty(sets);
            Assert.True((await client.PanelPreviewAsync(panel.Id, 200)).Length > 100);

            var changed = await client.PatchPanelAsync(panel.Id, new CaseHudPanelPatch(Brightness: 33, Theme: "light"));
            Assert.Equal(33, changed.Brightness);
            Assert.Equal("light", changed.Theme);
            await client.PatchPanelAsync(panel.Id, new CaseHudPanelPatch(Brightness: panel.Brightness, Theme: panel.Theme));
            var bad = await Assert.ThrowsAsync<CaseHudException>(() => client.PatchPanelAsync(panel.Id, new CaseHudPanelPatch(Mode: "party")));
            Assert.Equal("bad_request", bad.Code);
            var missing = await Assert.ThrowsAsync<CaseHudException>(() => client.PatchPanelAsync("nope", new CaseHudPanelPatch(Brightness: 1)));
            Assert.Equal("not_found", missing.Code);

            // gui/open (CaseHUD 1.0.5-dev 548db45): 202 and the window comes up; it is closed with the quit below.
            await client.OpenGuiAsync();
            await client.IdentifyAsync(panel.Id);
            await client.IdentifyAsync(null);
            await client.ApplySetAsync(sets[0].Set, sets[0].Theme);
            await client.PowerAsync(false);
            Assert.False((await client.StatusAsync()).Power);
            await client.PowerAsync(true);

            var scenes = await client.ScenesAsync();
            await client.SetRotationAsync(false);
            // Activation works for every saved scene (409 only while an automation rule holds the displays).
            if (scenes.Count > 0) await client.ActivateSceneAsync(scenes[0].Id);
            var unknown = await Assert.ThrowsAsync<CaseHudException>(() => client.ActivateSceneAsync("no-such-scene"));
            Assert.Equal("not_found", unknown.Code);
            await client.NextSceneAsync();
            await client.SetAutomationAsync(false);
            Assert.False((await client.StatusAsync()).Automation?.Enabled);
            await client.SetAutomationAsync(true);

            await client.GamingAsync(true, "Test game", Environment.ProcessId, 30);
            Assert.Equal("external", (await client.StatusAsync()).Gaming.Source);
            await client.GamingAsync(false);

            await client.SetAwtrixAsync(false);

            // Event stream: the first event is the full status.
            string? first = null;
            var stop = new CancellationTokenSource();
            try
            {
                await client.ReadEventsAsync(http, (name, _) => { first ??= name; stop.Cancel(); }, stop.Token);
            }
            catch (OperationCanceledException) { }
            Assert.Equal("status", first);
        }
        finally
        {
            if (client is not null) await client.QuitAsync();
            for (var i = 0; i < 40 && discovery.ReadRunning() is not null; i++) await Task.Delay(250);
            try { Directory.Delete(data, recursive: true); } catch (IOException) { }
        }
    }
}
