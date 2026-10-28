using CouchHud.Core.Integrations.CaseHud;
using CouchHud.Core.Startup;

namespace CouchHud.Core.Tests;

public class StartupTests
{
    private static IReadOnlyList<StartupEntry> Read(FakeRegistry registry, params string[] startupFiles) =>
        StartupCatalog.Read(registry, [(@"C:\Startup", "Autostart-Ordner")], _ => startupFiles);

    [Fact]
    public void Reads_run_keys_and_startup_folder_with_disabled_flags()
    {
        var registry = new FakeRegistry();
        registry.Values[@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run"] = new()
        {
            ["Discord"] = @"""C:\Users\k\AppData\Local\Discord\Update.exe"" --processStart Discord.exe",
            ["com.squirrel.Teams.Teams"] = @"C:\Users\k\AppData\Local\Microsoft\Teams\Update.exe --processStart ""Teams.exe""",
            ["CouchHud"] = @"""C:\CouchHud\CouchHud.exe"""
        };
        registry.Values[@"HKLM\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"] = new() { ["Steam"] = @"""C:\Steam\steam.exe"" -silent" };
        registry.Binary[@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run\com.squirrel.Teams.Teams"] = [3, 0, 0, 0];

        var entries = Read(registry, @"C:\Startup\OneDrive.lnk", @"C:\Startup\desktop.ini");

        Assert.Equal(new[] { "com.squirrel.Teams.Teams", "Discord", "OneDrive", "Steam" }, entries.Select(e => e.Name).ToArray());
        Assert.True(entries[0].DisabledInWindows);
        Assert.Equal("Run32:Steam", entries[3].Id);
        Assert.Equal("StartupFolder:OneDrive.lnk", entries[2].Id);
        Assert.DoesNotContain(entries, e => e.Name == "CouchHud");
    }

    [Theory]
    [InlineData("Discord", @"C:\Discord\Update.exe --processStart Discord.exe", "couch")]
    [InlineData("Steam", @"C:\Steam\steam.exe -silent", "couch")]
    [InlineData("SignalRGB", @"C:\SignalRgb\SignalRgbLauncher.exe", "couch")]
    [InlineData("OneDrive", @"C:\OneDrive\OneDrive.exe /background", "pc")]
    [InlineData("com.squirrel.Teams.Teams", @"C:\Teams\Update.exe --processStart Teams.exe", "pc")]
    [InlineData("SomethingUnknown", @"C:\Tools\thing.exe", "couch")]
    public void Suggests_couch_for_gaming_tools_and_pc_for_office(string name, string command, string expected)
    {
        Assert.Equal(expected, StartupCatalog.Suggest(new StartupEntry("Run:" + name, name, command, "Benutzer", false)));
    }

    [Fact]
    public void Mode_defaults_to_couch_and_process_name_comes_from_the_command()
    {
        var entry = new StartupEntry("Run:Spotify", "Spotify", @"""C:\Users\k\AppData\Roaming\Spotify\Spotify.exe"" /minimized", "Benutzer", false);
        Assert.Equal("couch", StartupCatalog.ModeOf(entry, new Dictionary<string, string>()));
        Assert.Equal("pc", StartupCatalog.ModeOf(entry, new Dictionary<string, string> { ["Run:Spotify"] = "pc" }));
        Assert.Equal("Spotify", StartupCatalog.ProcessName(entry));
        Assert.Null(StartupCatalog.ProcessName(entry with { Command = @"""C:\Startup\x.lnk""" }));
    }

    [Theory]
    [InlineData("WSearch", true)]
    [InlineData("SysMain", true)]
    [InlineData("Audiosrv", false)]
    [InlineData("XboxGipSvc", false)]
    [InlineData("wuauserv", false)]
    [InlineData("bad\"name", false)]
    [InlineData("", false)]
    public void Only_harmless_services_may_be_stopped(string name, bool allowed)
    {
        Assert.Equal(allowed, GamingServices.IsAllowed(name));
    }

    [Theory]
    [InlineData(true, @"""C:\Program Files\CaseHUD\CaseHUD.exe"" --background --no-tray")]
    [InlineData(false, @"""C:\Program Files\CaseHUD\CaseHUD.exe"" --background")]
    public void The_shell_starts_the_casehud_entry_as_background_and_leaves_the_registry_value_alone(bool noTray, string expected)
    {
        var registry = new FakeRegistry();
        const string runKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
        const string original = @"""C:\Program Files\CaseHUD\CaseHUD.exe"" --minimized";
        registry.Values[runKey] = new() { ["CaseHUD"] = original, ["Steam"] = @"""C:\Steam\steam.exe"" -silent" };
        registry.Values[CaseHudInfo.UninstallKey] = new() { ["InstallLocation"] = @"C:\Program Files\CaseHUD\", ["DisplayVersion"] = "1.2.0" };

        var file = Path.Combine(Path.GetTempPath(), "couchhud-cap-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new CaseHudCapabilityStore(file, _ => "stamp");
        store.Set(new CaseHudInstall(@"C:\Program Files\CaseHUD\CaseHUD.exe", @"C:\Program Files\CaseHUD", "1.2.0"), capable: true);
        var adapt = CaseHudAutostart.ForShell(registry, noTray, exists: _ => true, capabilities: store);
        File.Delete(file);
        var commands = StartupCatalog.CouchCommands(Read(registry), new Dictionary<string, string>(), adapt);

        Assert.Equal(expected, commands.Single(c => c.Entry.Name == "CaseHUD").CommandLine);
        Assert.Equal(@"""C:\Steam\steam.exe"" -silent", commands.Single(c => c.Entry.Name == "Steam").CommandLine);
        // The value belongs to CaseHUD (and is right for explorer).
        Assert.Equal(original, registry.Values[runKey]["CaseHUD"]);
    }

    [Fact]
    public void A_casehud_not_probed_yet_is_started_as_its_own_entry_says()
    {
        // Rewriting blindly would keep an old build (exit code 2 on --background) from starting at boot.
        var registry = new FakeRegistry();
        registry.Values[@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run"] = new() { ["CaseHUD"] = @"""C:\Program Files\CaseHUD\CaseHUD.exe"" --minimized" };
        registry.Values[CaseHudInfo.UninstallKey] = new() { ["InstallLocation"] = @"C:\Program Files\CaseHUD\", ["DisplayVersion"] = "1.0.0" };
        var commands = StartupCatalog.CouchCommands(Read(registry), new Dictionary<string, string>(), CaseHudAutostart.ForShell(registry, true, exists: _ => true));
        Assert.Equal(@"""C:\Program Files\CaseHUD\CaseHUD.exe"" --minimized", Assert.Single(commands).CommandLine);
    }

    [Fact]
    public void Entries_waiting_for_pc_mode_or_disabled_in_windows_are_not_started_at_boot()
    {
        var registry = new FakeRegistry();
        registry.Values[@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run"] = new() { ["A"] = @"C:\a.exe", ["B"] = @"C:\b.exe", ["C"] = @"C:\c.exe" };
        registry.Binary[@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run\C"] = [3, 0, 0, 0];
        var commands = StartupCatalog.CouchCommands(Read(registry), new Dictionary<string, string> { ["Run:B"] = StartupMode.Pc });
        Assert.Equal(["A"], commands.Select(c => c.Entry.Name));
    }

    [Fact]
    public void A_casehud_found_old_by_probing_keeps_its_entry_unchanged()
    {
        var registry = new FakeRegistry();
        registry.Values[@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run"] = new() { ["CaseHUD"] = @"""C:\Program Files\CaseHUD\CaseHUD.exe"" --minimized" };
        registry.Values[CaseHudInfo.UninstallKey] = new() { ["InstallLocation"] = @"C:\Program Files\CaseHUD\", ["DisplayVersion"] = "2.5.0" };
        // Found out by probing (the build ended on --background with code 2), whatever version it calls itself.
        var file = Path.Combine(Path.GetTempPath(), "couchhud-cap-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new CaseHudCapabilityStore(file, _ => "stamp");
        store.Set(new CaseHudInstall(@"C:\Program Files\CaseHUD\CaseHUD.exe", @"C:\Program Files\CaseHUD", "2.5.0"), capable: false);
        var commands = StartupCatalog.CouchCommands(Read(registry), new Dictionary<string, string>(), CaseHudAutostart.ForShell(registry, true, exists: _ => true, capabilities: store));
        File.Delete(file);
        Assert.Equal(@"""C:\Program Files\CaseHUD\CaseHUD.exe"" --minimized", Assert.Single(commands).CommandLine);
    }
}
