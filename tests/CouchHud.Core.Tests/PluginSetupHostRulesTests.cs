using CouchHud.Core.Plugins;

namespace CouchHud.Core.Tests;

/// <summary>What the host checks before a setup of a plugin runs: whose folder, what else is in it, and the proof that a person pressed something.</summary>
public sealed class PluginSetupHostRulesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "couchhud-setup-host-" + Guid.NewGuid().ToString("N"), "plugin-data");
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    public PluginSetupHostRulesTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "casehud", "setup", "a"));
        Directory.CreateDirectory(Path.Combine(_root, "other", "setup", "a"));
    }

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_root)!, true);

    private string Own => Path.Combine(_root, "casehud", "setup", "a", "Setup.exe");

    private string Others => Path.Combine(_root, "other", "setup", "a", "Setup.exe");

    [Fact]
    public void A_path_under_another_plugins_folder_is_refused_the_root_is_the_callers_own()
    {
        Assert.Null(PluginSetupRules.Refuse(Own, Hash, Path.Combine(_root, "casehud")));
        Assert.Equal("place", PluginSetupRules.Refuse(Others, Hash, Path.Combine(_root, "casehud")));
        Assert.Equal("place", PluginSetupRules.Refuse(Own, Hash, Path.Combine(_root, "other")));
        // A folder whose name only begins like the caller's is not the caller's.
        Directory.CreateDirectory(Path.Combine(_root, "casehud-evil", "a"));
        Assert.Equal("place", PluginSetupRules.Refuse(Path.Combine(_root, "casehud-evil", "a", "Setup.exe"), Hash, Path.Combine(_root, "casehud")));
    }

    [Fact]
    public void The_setup_must_be_alone_in_its_folder()
    {
        File.WriteAllText(Own, "x");
        Assert.True(PluginSetupRules.FolderHoldsOnlyTheSetup(Own));
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(Own)!, "evil.dll"), "x");
        Assert.False(PluginSetupRules.FolderHoldsOnlyTheSetup(Own));
        var b = Path.Combine(_root, "casehud", "setup", "b");
        Directory.CreateDirectory(Path.Combine(b, "sub"));
        File.WriteAllText(Path.Combine(b, "Setup.exe"), "x");
        Assert.False(PluginSetupRules.FolderHoldsOnlyTheSetup(Path.Combine(b, "Setup.exe")));
        Assert.False(PluginSetupRules.FolderHoldsOnlyTheSetup(Path.Combine(_root, "casehud", "setup", "none", "Setup.exe")));
    }

    [Fact]
    public void A_press_pays_for_one_setup_of_that_plugin_only()
    {
        var press = new PluginSetupPress();
        Assert.False(press.TryConsume("casehud"));
        press.Issue("casehud");
        Assert.False(press.TryConsume("other"));
        Assert.True(press.TryConsume("CaseHud"));
        Assert.False(press.TryConsume("casehud"));
        press.Issue("casehud");
        press.Clear();
        Assert.False(press.TryConsume("casehud"));
    }

    [Fact]
    public void The_token_is_gone_when_the_call_returns_used_or_not_and_only_for_that_plugin()
    {
        var press = new PluginSetupPress();
        press.Issue("casehud");
        press.Issue("other");
        press.Clear("CaseHud");
        Assert.False(press.TryConsume("casehud"));
        Assert.True(press.TryConsume("other"));
        // A hand-over after the press is over finds nothing.
        press.Issue("casehud");
        Assert.True(press.TryConsume("casehud"));
        press.Clear("casehud");
        Assert.False(press.TryConsume("casehud"));
    }

    [Fact]
    public void A_press_runs_out()
    {
        var now = DateTimeOffset.UtcNow;
        var press = new PluginSetupPress(() => now);
        press.Issue("casehud");
        now += PluginSetupPress.Lifetime + TimeSpan.FromSeconds(1);
        Assert.False(press.TryConsume("casehud"));
        press.Issue("casehud");
        now += PluginSetupPress.Lifetime - TimeSpan.FromSeconds(1);
        Assert.True(press.TryConsume("casehud"));
        // Long enough for the longest download of a plugin (30 minutes), short enough to be a press and not a permission.
        Assert.InRange(PluginSetupPress.Lifetime, TimeSpan.FromMinutes(31), TimeSpan.FromMinutes(40));
    }
}
