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

    [Fact]
    public void Entries_waiting_for_pc_mode_or_disabled_in_windows_are_not_started_at_boot()
    {
        var registry = new FakeRegistry();
        registry.Values[@"HKCU\Software\Microsoft\Windows\CurrentVersion\Run"] = new() { ["A"] = @"C:\a.exe", ["B"] = @"C:\b.exe", ["C"] = @"C:\c.exe" };
        registry.Binary[@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run\C"] = [3, 0, 0, 0];
        var commands = StartupCatalog.CouchCommands(Read(registry), new Dictionary<string, string> { ["Run:B"] = StartupMode.Pc });
        Assert.Equal(["A"], commands.Select(c => c.Entry.Name));
    }
}
