using System.Text.RegularExpressions;
using CouchHud.Core.Config;
using CouchHud.Core.Guard;

namespace CouchHud.Service.Tests;

/// <summary>
/// Starting, cancelling and opening a client's download page from the Downloads tab, pinned from the shell's source: only the HUD's own
/// page reaches them (not the phone remote, MCP, the assistant or a plugin), they are closed on a locked couch and for a waiting profile,
/// the host checks the lock again, and the Updates page and the Downloads tab start updates through the same bridge commands.
/// </summary>
public sealed class DownloadControlContractTests
{
    private static readonly string[] Calls = ["cancelDownload", "openClientDownloads", "startUpdate", "startSelfUpdate", "installCatalogEntry"];

    private static readonly string[] HostMethods = ["CancelDownload", "OpenClientDownloads", "StartUpdate", "StartSelfUpdate", "InstallCatalogEntryAsync"];

    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CouchHud.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repository root not found");
    }

    private static string[] Lines(string file) => File.ReadAllText(file).Replace("\r\n", "\n").Split('\n');

    private static bool IsComment(string line) => line.TrimStart().StartsWith("//", StringComparison.Ordinal);

    private static string Code(params string[] path) => string.Join('\n', Lines(Path.Combine([Root(), .. path])).Where(l => !IsComment(l)));

    /// <summary>The body of a 4-space-indented member, without comment lines.</summary>
    private static string Body(string file, string declaration)
    {
        var lines = Lines(Path.Combine(Root(), "src", "CouchHud.Shell", file));
        var start = Array.FindIndex(lines, l => l.StartsWith(declaration, StringComparison.Ordinal));
        Assert.True(start >= 0, $"not found: {declaration}");
        var end = Array.FindIndex(lines, start + 1, l => l == "    }");
        return string.Join('\n', lines[start..end].Where(l => !IsComment(l)));
    }

    [Fact]
    public void Starting_cancelling_and_opening_a_client_are_closed_on_a_locked_couch_and_for_a_waiting_profile()
    {
        foreach (var call in Calls)
        {
            Assert.DoesNotContain(call, LockPolicy.OpenWhileLocked);
            Assert.DoesNotContain(call, ProfilePolicy.OpenWhileHeld);
        }
    }

    [Fact]
    public void The_ui_bridge_and_the_page_name_the_calls()
    {
        var bridge = Code("src", "CouchHud.Shell", "UiHost", "UiBridge.cs");
        foreach (var call in Calls) Assert.Contains($"case \"{call}\":", bridge);
        var js = File.ReadAllText(Path.Combine(Root(), "ui", "bridge.js"));
        foreach (var call in Calls) Assert.Contains($"call('{call}'", js);
    }

    [Fact]
    public void Remote_mcp_the_assistant_and_plugins_cannot_start_or_cancel_downloads()
    {
        var outside = new[]
        {
            ("CouchHud.Shell", "Services", "RemoteHost.cs"),
            ("CouchHud.Shell", "Services", "McpHost.cs"),
            ("CouchHud.Shell", "Services", "HudTools.cs"),
            ("CouchHud.Core", "Assistant", "Tools.cs"),
            ("CouchHud.Core", "Assistant", "McpServer.cs"),
            ("CouchHud.Core", "Assistant", "AssistantSession.cs"),
            ("CouchHud.Core", "Plugins", "PluginManager.cs")
        };
        foreach (var (project, folder, file) in outside)
        {
            var code = Code("src", project, folder, file);
            foreach (var method in HostMethods) Assert.False(Regex.IsMatch(code, $@"\b{method}\s*\("), $"{file} calls {method}");
            foreach (var call in Calls) Assert.DoesNotContain($"\"{call}\"", code);
        }
    }

    [Fact]
    public void The_host_checks_the_lock_and_the_profile_again_and_asks_the_parents_before_a_client_s_window()
    {
        var cancel = Body("HudDownloads.cs", "    public object CancelDownload(");
        Assert.Contains("if (Locked) throw new LockedException", cancel);
        Assert.Contains("RequireProfileChosen();", cancel);
        var open = Body("HudDownloads.cs", "    public object OpenClientDownloads(");
        Assert.Contains("InstallLinks.ClientDownloads(source)", open);
        Assert.Contains("if (Locked) throw new LockedException", open);
        Assert.Contains("RequireProfileChosen();", open);
        Assert.Contains("if (config.Current.ParentalEnabled) RequireParentGrant(\"openClientDownloads\", source);", open);
        // The link is checked before the lock and the parents: an unknown source never reaches the launcher.
        Assert.True(open.IndexOf("ClientDownloads", StringComparison.Ordinal) < open.IndexOf("Launcher.Launch", StringComparison.Ordinal));
    }

    [Fact]
    public void Downloads_start_updates_through_the_update_functions()
    {
        var downloads = File.ReadAllText(Path.Combine(Root(), "ui", "views", "downloads.js"));
        Assert.Contains("import { installSelfUpdate, startUpdate } from './updates.js';", downloads);
        Assert.Contains("import { installFromCatalog } from './plugins.js';", downloads);
        // No bridge command of its own for starting: the tab never calls api.startUpdate / startSelfUpdate / installCatalogEntry directly.
        Assert.DoesNotContain("api.startUpdate", downloads);
        Assert.DoesNotContain("api.startSelfUpdate", downloads);
        Assert.DoesNotContain("api.installCatalogEntry", downloads);
        // Search now, a restart that is due and each app update are reachable from the tab; Settings keeps the switches only.
        Assert.Contains("api.checkUpdates()", downloads);
        Assert.Contains("updates.reboot.title", downloads);
        Assert.Contains("startUpdate('apps', a.id, null)", downloads);
        var settings = File.ReadAllText(Path.Combine(Root(), "ui", "views", "settings.js"));
        Assert.DoesNotContain("updatesPage", settings);
        Assert.DoesNotContain("updatesPage", File.ReadAllText(Path.Combine(Root(), "ui", "views", "updates.js")));
    }

    [Fact]
    public void Plugin_notifications_never_carry_a_target_and_shown_ones_are_filtered()
    {
        var hud = File.ReadAllText(Path.Combine(Root(), "src", "CouchHud.Shell", "Hud.cs"));
        // The plugin path hands over plugin, title and body only.
        var line = hud.Split((char)10).First(l => l.Contains("plugins.Notifier ="));
        Assert.DoesNotContain("target", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PostNotification(plugin, title, body)", line);
        // Whatever is stored is whitelisted again when the list is built, and PostNotification normalizes what it is given.
        Assert.Contains("Target = NotificationTargets.Normalize(n.Target)", hud);
        Assert.Contains("Target: NotificationTargets.Normalize(target)", hud);
    }
}
