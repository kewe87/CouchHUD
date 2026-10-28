using System.Text;
using CouchHud.Core.Assistant;
using CouchHud.Core.Config;
using CouchHud.Core.Guard;
using CouchHud.Core.Plugins;

namespace CouchHud.Core.Tests;

/// <summary>
/// Home Assistant, CaseHUD and the VPN are plugins from the signed catalog; the core's own integrations are gone. Without a plugin the core shows
/// no Smart Home tab, no CaseHUD or VPN entry and offers no assistant tool for them, no setting key or bridge call of them is known, and a 0.5
/// config that still holds their old fields hands them to the plugins as first settings (and to nothing else).
/// </summary>
public sealed class RemovedCoreIntegrationsTests : IDisposable
{
    private static readonly byte[] TrustKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-removed-" + Guid.NewGuid().ToString("N"));

    public RemovedCoreIntegrationsTests() => Directory.CreateDirectory(_dir);

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

    private PluginManager NoPlugins() => new([(Path.Combine(_dir, "plugins"), true)], _dir, _ => true) { Trust = new PluginTrust(_dir, TrustKey) };

    [Fact]
    public void With_no_plugin_loaded_the_core_has_no_Smart_Home_tab_no_guide_entries_and_no_tools_of_the_three()
    {
        var plugins = NoPlugins();
        Assert.Empty(plugins.ResolveTabs(AreaSet.AllOn, childProfile: false));
        Assert.Empty(plugins.ResolveTabs(AreaSet.AllOn, childProfile: true));
        Assert.Empty(plugins.QuickActions);
        Assert.Empty(plugins.ToolSpecs);
        // The assistant's and MCP's own tools: nothing about the smart home, CaseHUD or a VPN.
        foreach (var tool in ToolCatalog.All)
        {
            Assert.DoesNotContain("home", tool.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("casehud", tool.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("vpn", tool.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Home Assistant", tool.Description, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Null(ToolCatalog.Find("list_home_entities"));
        Assert.Null(ToolCatalog.Find("activate_home_entity"));
        // Not on a locked couch either: the names are no tools, so nothing is offered or run for them.
        Assert.False(ToolCatalog.AllowedWhileLocked("list_home_entities"));
        Assert.False(ToolCatalog.AllowedWhileLocked("activate_home_entity"));
    }

    [Fact]
    public void No_setting_key_and_no_bridge_call_of_the_three_is_known_so_the_page_cannot_reach_them()
    {
        foreach (var key in new[]
                 {
                     "homeAssistantUrl", "homeAssistantToken", "caseHudInstallAutostart", "caseHudInstallFps", "caseHudInstallApi", "caseHudGamingLease", "vpnInGuide"
                 })
        {
            Assert.Null(FeatureAreas.AreaOfSetting(key));
            Assert.False(LockPolicy.IsHarmlessSetting(key, profilesGuarded: false));
        }
        foreach (var call in new[]
                 {
                     "getHomeEntities", "activateHomeEntity", "pinHomeEntity", "getHomeCatalog", "setHomeSelection", "testHomeAssistant", "signInHomeAssistant", "signOutHomeAssistant",
                     "caseHudStatus", "caseHudInstall", "caseHudActivateScene", "vpnStatus", "vpnCountries", "vpnConnect", "vpnDisconnect", "vpnOpenApp"
                 })
        {
            Assert.DoesNotContain(call, FeatureAreas.AlwaysMethods);
            Assert.False(FeatureAreas.BridgeMethods.ContainsKey(call));
            // A guest on the locked couch gets none of them.
            Assert.DoesNotContain(call, LockPolicy.OpenWhileLocked);
        }
    }

    [Fact]
    public void A_config_of_0_5_with_all_three_set_up_hands_everything_to_the_plugins_and_the_old_setup_fields_are_ignored()
    {
        var token = "dpapi:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("core|secret")).Replace("=", "-");
        var config = ConfigStore.Parse($$"""
            {
              "homeAssistantUrl": "http://homeassistant.local:8123/",
              "homeAssistantToken": "{{token}}",
              "homeAssistantEntities": ["light.sofa", "scene.movie"],
              "homeAssistantPinned": ["light.sofa"],
              "caseHudInstallAutostart": false, "caseHudInstallFps": true, "caseHudInstallApi": false,
              "caseHudGamingLease": false,
              "vpnInGuide": false, "vpnLastCountry": "fr"
            }
            """)!;
        string? MoveOnly(string plugin, string stored) => "dpapi:moved-for-" + plugin;

        var ha = PluginSettingSeeds.For(config, "homeassistant", MoveOnly)!;
        Assert.Equal("http://homeassistant.local:8123", ha["url"]);
        Assert.Contains("light.sofa", ha["entities"]!);
        Assert.Equal("[\"light.sofa\"]", ha["pinned"]);
        Assert.Equal("dpapi:moved-for-homeassistant", ha["token"]);
        // The credentials are bound to the address they were made for.
        Assert.Equal("http://homeassistant.local:8123", ha["credentialUrl"]);

        var casehud = PluginSettingSeeds.For(config, "casehud")!;
        Assert.Equal(["gamingLease"], casehud.Keys);
        Assert.Equal("false", casehud["gamingLease"]);

        var vpn = PluginSettingSeeds.For(config, "vpn")!;
        Assert.Equal("false", vpn["inGuide"]);
        Assert.Equal("fr", vpn["lastCountry"]);

        // Only CouchHUD's own plugin of exactly that id gets a seed.
        Assert.Null(PluginSettingSeeds.For(config, "homeassistant-evil", MoveOnly));
        Assert.Null(PluginSettingSeeds.For(config, "somebody"));
    }

    [Fact]
    public void After_a_written_seed_the_legacy_token_copies_go_but_a_seed_without_them_keeps_them()
    {
        var config = new AppConfig { HomeAssistantUrl = "http://ha.local:8123", HomeAssistantToken = "dpapi:AAAA", HomeAssistantRefreshToken = "dpapi:BBBB" };
        // The move failed (or no usable address): the seed has no secret, nothing is cleared, a later start can try again.
        var noSecret = PluginSettingSeeds.For(config, "homeassistant", (_, _) => null);
        Assert.False(PluginSettingSeeds.ClearLegacyHomeAssistantSecrets(config, noSecret));
        Assert.False(PluginSettingSeeds.ClearLegacyHomeAssistantSecrets(config, null));
        Assert.Equal("dpapi:AAAA", config.HomeAssistantToken);
        Assert.Equal("dpapi:BBBB", config.HomeAssistantRefreshToken);
        // The seed held the re-encrypted secret and was written: both old copies go, the non-secret fields stay.
        var withSecret = PluginSettingSeeds.For(config, "homeassistant", (plugin, _) => "dpapi:moved-" + plugin)!;
        Assert.True(PluginSettingSeeds.ClearLegacyHomeAssistantSecrets(config, withSecret));
        Assert.Equal("", config.HomeAssistantToken);
        Assert.Equal("", config.HomeAssistantRefreshToken);
        Assert.Equal("http://ha.local:8123", config.HomeAssistantUrl);
    }

    [Fact]
    public void A_fresh_config_and_the_legacy_fields_stay_readable_but_a_save_does_not_bring_back_removed_setup_options()
    {
        var config = ConfigStore.Parse("""{ "caseHudInstallFps": true, "vpnLastCountry": "de" }""")!;
        Assert.Equal("de", config.VpnLastCountry);
        // The three CaseHUD setup options are gone from the config: they are not even written.
        var written = System.Text.Json.JsonSerializer.Serialize(config, ConfigStore.JsonOptions);
        Assert.DoesNotContain("caseHudInstall", written, StringComparison.OrdinalIgnoreCase);
        // A household that never set Home Assistant up gets no seed for it.
        Assert.Null(PluginSettingSeeds.For(new AppConfig(), "homeassistant", (_, s) => s));
    }
}
