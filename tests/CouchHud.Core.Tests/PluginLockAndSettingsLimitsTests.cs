using System.Text;
using CouchHud.Core.Assistant;
using CouchHud.Core.Config;
using CouchHud.Core.Plugins;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>
/// Home Assistant's rights are the Home Assistant user's, also on the locked couch: guide menu entries and tools of CouchHUD's own plugins
/// can be marked for it (a flag of anybody else is ignored), and the core's own Home Assistant steps aside for the plugin. Also the size
/// limits of a plugin's settings and a protector that fails.
/// </summary>
public sealed class PluginLockAndSettingsLimitsTests : IDisposable
{
    private static readonly byte[] TrustKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-lock-" + Guid.NewGuid().ToString("N"));

    public PluginLockAndSettingsLimitsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static QuickAction Action(bool flag, bool grant = false) =>
        new() { Id = "a", Label = "A", Run = _ => Task.FromResult<string?>(null), AllowedWhileLocked = flag, RequiresParentGrant = grant };

    // =====================================================================
    // Quick actions and tools on the locked couch

    [Theory]
    [InlineData(PluginOrigin.FirstParty, true, true)]
    [InlineData(PluginOrigin.FirstParty, false, false)]
    [InlineData(PluginOrigin.ThirdParty, true, false)]
    [InlineData(PluginOrigin.ThirdParty, false, false)]
    [InlineData(PluginOrigin.Local, true, false)]
    public void A_locked_couch_runs_and_shows_only_what_CouchHUDs_own_plugin_marked_for_it(PluginOrigin origin, bool flag, bool allowed)
    {
        var action = Action(flag);
        Assert.Equal(allowed, PluginPolicy.MayRun(locked: true, origin, action, parentalEnabled: false).Refused is null);
        Assert.Equal(allowed, PluginPolicy.Shown(locked: true, origin, action));
        // Unlocked, everything is there and runs.
        Assert.Null(PluginPolicy.MayRun(locked: false, origin, action, parentalEnabled: false).Refused);
        Assert.True(PluginPolicy.Shown(locked: false, origin, action));
    }

    [Fact]
    public void The_parents_code_still_applies_to_a_locked_run_that_asks_for_it()
    {
        var decision = PluginPolicy.MayRun(locked: true, PluginOrigin.FirstParty, Action(flag: true, grant: true), parentalEnabled: true);
        Assert.Null(decision.Refused);
        Assert.True(decision.ParentCode);
        // And a third party's run always asks (under parental controls), flag or not.
        Assert.True(PluginPolicy.MayRun(locked: false, PluginOrigin.ThirdParty, Action(flag: true), parentalEnabled: true).ParentCode);
    }

    private PluginManager Manager() => new([(Path.Combine(_dir, "plugins"), true)], _dir, _ => true) { Trust = new PluginTrust(_dir, TrustKey) };

    [Theory]
    [InlineData(PluginOrigin.FirstParty, true)]
    [InlineData(PluginOrigin.ThirdParty, false)]
    [InlineData(PluginOrigin.Local, false)]
    public void A_tools_flag_counts_only_for_CouchHUDs_own_plugins(PluginOrigin origin, bool expected)
    {
        var manager = Manager();
        var ctx = manager.ContextFor("tool", new PluginManager.PluginGrant("tool", origin, new HashSet<string> { PluginCapabilities.Assistant }));
        ctx.RegisterTool(new PluginTool { Name = "act", Description = "x", Writes = true, AllowedWhileLocked = true, Run = (_, _) => Task.FromResult("") });
        ctx.RegisterTool(new PluginTool { Name = "plain", Description = "x", Run = (_, _) => Task.FromResult("") });
        Assert.Equal(expected, manager.ToolSpecs.Single(t => t.Name == "plugin_tool_act").AllowedWhileLocked);
        Assert.False(manager.ToolSpecs.Single(t => t.Name == "plugin_tool_plain").AllowedWhileLocked);
        // The built-in rule is untouched: a plugin's tool is not one of the lookups of the locked couch.
        Assert.False(ToolCatalog.AllowedWhileLocked("plugin_tool_act"));
    }

    [Fact]
    public void A_plugin_without_the_tabs_capability_gets_no_tab_from_the_host_even_if_it_asks()
    {
        var manager = Manager();
        var ctx = manager.ContextFor("homeassistant", new PluginManager.PluginGrant("homeassistant", PluginOrigin.FirstParty, new HashSet<string> { PluginCapabilities.Pages }));
        ctx.RegisterPage(new PluginPage { Id = "home", Title = "H", Rows = _ => Task.FromResult<IReadOnlyList<PluginRow>>([]) });
        Assert.Throws<PluginCapabilityException>(() => ctx.RegisterTab(new PluginTab("home", "home", "Smart Home", "Smart Home") { Area = "always", ChildSafe = true }));
        Assert.Empty(manager.ResolveTabs(AreaSet.AllOn, false));
    }

    [Fact]
    public void The_stored_core_credentials_are_never_touched_by_the_seed()
    {
        var config = new AppConfig { HomeAssistantUrl = "http://ha.local:8123", HomeAssistantRefreshToken = "dpapi:AAAA", HomeAssistantEntities = ["light.a"] };
        _ = PluginSettingSeeds.For(config, "homeassistant", (_, _) => "dpapi:BBBB");
        Assert.Equal("dpapi:AAAA", config.HomeAssistantRefreshToken);
        Assert.Equal(["light.a"], config.HomeAssistantEntities);
    }

    [Fact]
    public void A_taken_over_sign_in_keeps_the_client_id_it_was_issued_for_and_a_token_has_none()
    {
        var withSession = new AppConfig { HomeAssistantUrl = "http://ha.local:8123", HomeAssistantRefreshToken = "dpapi:AAAA" };
        var seed = PluginSettingSeeds.For(withSession, "homeassistant", (_, _) => "dpapi:BBBB")!;
        Assert.Equal("https://couchhud.invalid/", seed["clientId"]);
        var withToken = new AppConfig { HomeAssistantUrl = "http://ha.local:8123", HomeAssistantToken = "dpapi:AAAA" };
        Assert.False(PluginSettingSeeds.For(withToken, "homeassistant", (_, _) => "dpapi:BBBB")!.ContainsKey("clientId"));
        // A sign-in whose secret could not be moved is not handed over at all, so no client_id without a token.
        Assert.False(PluginSettingSeeds.For(withSession, "homeassistant", (_, _) => null)!.ContainsKey("clientId"));
    }

    // =====================================================================
    // Size limits and a protector that fails

    private IPluginSettings Settings(Func<string, string, string>? protect = null)
    {
        var manager = Manager();
        manager.ProtectSecret = protect ?? ((id, plain) => "dpapi:" + id + ":" + plain);
        manager.UnprotectSecret = (id, stored) => stored.StartsWith("dpapi:" + id + ":", StringComparison.Ordinal) ? stored[("dpapi:" + id + ":").Length..] : "";
        return manager.ContextFor("tool", new PluginManager.PluginGrant("tool", PluginOrigin.FirstParty, new HashSet<string>())).Settings;
    }

    private string File1 => Path.Combine(_dir, "plugin-data", "tool", "settings.json");

    [Fact]
    public void A_value_longer_than_64_KB_is_refused_for_Set_and_SetSecret()
    {
        var settings = Settings();
        settings.Set("fits", new string('x', 64 * 1024));
        Assert.Equal(64 * 1024, settings.Get("fits")!.Length);
        settings.Set("big", new string('x', 64 * 1024 + 1));
        settings.SetSecret("bigSecret", new string('x', 64 * 1024 + 1));
        Assert.Null(settings.Get("big"));
        Assert.Null(settings.Get("bigSecret"));
        // A refused value leaves the old one alone.
        settings.Set("keep", "old");
        settings.Set("keep", new string('y', 100_000));
        Assert.Equal("old", settings.Get("keep"));
    }

    [Fact]
    public void The_settings_file_never_grows_beyond_1_MB()
    {
        var settings = Settings();
        for (var i = 0; i < 40; i++) settings.Set("k" + i, new string('x', 60 * 1024));
        Assert.True(new FileInfo(File1).Length <= 1024 * 1024);
        // Some were kept, the rest refused; what is there reads back, and a small change still works after the limit was hit.
        Assert.NotNull(settings.Get("k0"));
        Assert.Null(settings.Get("k39"));
        settings.Set("k0", "small");
        Assert.Equal("small", settings.Get("k0"));
        // And an oversized key is refused too.
        settings.Set(new string('k', 201), "x");
        Assert.Null(settings.Get(new string('k', 201)));
    }

    [Fact]
    public void A_protector_that_throws_is_a_refusal_and_not_an_exception_in_the_plugin()
    {
        var settings = Settings((_, _) => throw new InvalidOperationException("no DPAPI"));
        settings.Set("name", "kept");
        var ex = Record.Exception(() => settings.SetSecret("token", "secret"));
        Assert.Null(ex);
        Assert.Null(settings.Get("token"));
        Assert.Equal("kept", settings.Get("name"));
        Assert.DoesNotContain("secret", File.ReadAllText(File1));
        // The old secret survives a failed replacement (a refusal changes nothing).
        var good = Settings();
        good.SetSecret("token", "first");
        var broken = Settings((_, _) => throw new CryptographicExceptionLike());
        broken.SetSecret("token", "second");
        Assert.Equal("first", broken.Get("token"));
    }

    private sealed class CryptographicExceptionLike : Exception;

    [Fact]
    public void A_secret_whose_stored_text_is_too_long_is_refused()
    {
        var settings = Settings((_, plain) => "dpapi:" + new string('z', 70_000));
        settings.SetSecret("token", "short");
        Assert.Null(settings.Get("token"));
        Assert.True(!File.Exists(File1) || !File.ReadAllText(File1).Contains("zzzz"));
    }
}
