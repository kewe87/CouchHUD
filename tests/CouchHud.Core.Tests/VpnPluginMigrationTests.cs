using System.Text.Json;
using CouchHud.Core.Config;
using CouchHud.Core.Plugins;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>
/// The VPN moved out of the core into a plugin (plugin repo CouchHud.Plugin.Vpn), and the core's own VPN is gone. What carries over (the Guide menu
/// switch and the country chosen last, as the plugin's first settings, for CouchHUD's own plugin of that id and once; there is no secret: the core
/// never held a VPN credential), the offer for a household that has NordVPN, and the signed catalog entry.
/// </summary>
public sealed class VpnPluginMigrationTests : IDisposable
{
    private const string ZipHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly byte[] TrustKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-vpn-mig-" + Guid.NewGuid().ToString("N"));

    public VpnPluginMigrationTests() => Directory.CreateDirectory(_dir);

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

    // =====================================================================
    // The seed

    [Fact]
    public void The_guide_switch_and_the_last_country_come_over_as_the_plugins_first_settings()
    {
        var seed = PluginSettingSeeds.For(new AppConfig { VpnInGuide = false, VpnLastCountry = "fr" }, "vpn")!;
        Assert.Equal(["inGuide", "lastCountry"], seed.Keys.Order());
        Assert.Equal("false", seed["inGuide"]);
        Assert.Equal("fr", seed["lastCountry"]);
        // The default of a household that never touched it: on, and no country.
        var fresh = PluginSettingSeeds.For(new AppConfig(), "vpn")!;
        Assert.Equal("true", fresh["inGuide"]);
        Assert.False(fresh.ContainsKey("lastCountry"));
    }

    [Theory]
    [InlineData("DE")]
    [InlineData("d")]
    [InlineData("deu")]
    [InlineData("1a")]
    [InlineData("--")]
    [InlineData("d\" ")]
    [InlineData("")]
    [InlineData(null)]
    public void A_last_country_that_is_not_two_lower_case_letters_is_left_out_the_config_file_is_untrusted(string? country)
    {
        var seed = PluginSettingSeeds.For(new AppConfig { VpnLastCountry = country }, "vpn")!;
        Assert.False(seed.ContainsKey("lastCountry"));
        Assert.Equal(["inGuide"], seed.Keys);
    }

    [Fact]
    public void There_is_no_secret_to_carry_over_so_the_seed_never_asks_for_the_secret_mover()
    {
        var asked = 0;
        var seed = PluginSettingSeeds.For(new AppConfig { VpnLastCountry = "de" }, "vpn", (_, _) =>
        {
            asked++;
            return "dpapi:x";
        })!;
        Assert.Equal(0, asked);
        Assert.All(seed.Values, v => Assert.DoesNotContain("dpapi", v!));
        // The config knows no VPN credential, address or path: every VPN property is one of the two.
        var names = typeof(AppConfig).GetProperties().Select(p => p.Name).Where(n => n.StartsWith("Vpn", StringComparison.Ordinal)).Order();
        Assert.Equal(["VpnInGuide", "VpnLastCountry"], names);
    }

    [Theory]
    [InlineData("nordvpn")]
    [InlineData("vpn-pro")]
    [InlineData("my-vpn")]
    [InlineData("")]
    public void Only_the_plugin_with_exactly_this_id_gets_the_seed(string id) => Assert.Null(PluginSettingSeeds.For(new AppConfig(), id));

    [Fact]
    public void The_id_is_compared_without_case_and_the_other_seeds_are_untouched()
    {
        Assert.NotNull(PluginSettingSeeds.For(new AppConfig(), "VPN"));
        Assert.Null(PluginSettingSeeds.For(new AppConfig(), "homeassistantx"));
        Assert.Equal(["gamingLease"], PluginSettingSeeds.For(new AppConfig(), "casehud")!.Keys);
    }

    private PluginManager Manager(AppConfig config) => new([(Path.Combine(_dir, "plugins"), true)], _dir, _ => true)
    {
        Trust = new PluginTrust(_dir, TrustKey),
        SettingSeeds = id => PluginSettingSeeds.For(config, id)
    };

    private static IPluginContext Context(PluginManager manager, string id, PluginOrigin origin, params string[] caps) =>
        manager.ContextFor(id, new PluginManager.PluginGrant(id, origin, new HashSet<string>(caps)));

    [Fact]
    public void The_first_start_of_the_plugin_has_the_old_settings_and_the_seed_is_used_up_once()
    {
        var config = new AppConfig { VpnInGuide = false, VpnLastCountry = "nl" };
        var applied = new List<string>();
        var manager = Manager(config);
        manager.SeedApplied = applied.Add;
        var settings = Context(manager, "vpn", PluginOrigin.FirstParty).Settings;
        Assert.False(settings.GetBool("inGuide", true));
        Assert.Equal("nl", settings.Get("lastCountry"));
        Assert.Equal(["vpn"], applied);
        // A later change of the old config does not bring the seed back.
        settings.Set("inGuide", "true");
        config.VpnInGuide = false;
        Assert.True(Context(Manager(config), "vpn", PluginOrigin.FirstParty).Settings.GetBool("inGuide"));
    }

    [Theory]
    [InlineData(PluginOrigin.ThirdParty)]
    [InlineData(PluginOrigin.Local)]
    public void A_plugin_that_is_not_CouchHUDs_own_never_gets_the_seed_even_with_the_name(PluginOrigin origin)
    {
        var settings = Context(Manager(new AppConfig { VpnInGuide = false, VpnLastCountry = "de" }), "vpn", origin, PluginCapabilities.Pages).Settings;
        Assert.Null(settings.Get("inGuide"));
        Assert.Null(settings.Get("lastCountry"));
    }

    [Fact]
    public void The_names_vpn_and_nordvpn_are_reserved_so_a_third_party_cannot_ship_a_plugin_that_takes_the_name_or_the_seed()
    {
        Assert.True(PluginNames.IsReserved("vpn"));
        Assert.True(PluginNames.IsReserved("VPN"));
        Assert.True(PluginNames.IsReserved("nordvpn"));
    }

    // =====================================================================
    // The offer

    private static IReadOnlyList<CatalogEntry> Entries() => CatalogParser.Parse($$$"""
        [{"id":"vpn","folder":"CouchHud.Plugin.Vpn","name":"VPN","type":"plugin","version":"1.0.0","download":"https://x.example/v.zip","sha256":"{{{ZipHash}}}","suggestWhen":{"uninstallEntry":"NordVPN"}},
         {"id":"plain","name":"Plain","type":"plugin","version":"1.0.0","download":"https://x.example/p.zip","sha256":"{{{ZipHash}}}"}]
        """, "s");

    private static FakeRegistry WithPrograms(params string[] names)
    {
        var registry = new FakeRegistry();
        for (var i = 0; i < names.Length; i++)
            registry.Values[PluginSuggestions.UninstallRoot + @"\{" + i + "}"] = new() { ["DisplayName"] = names[i] };
        return registry;
    }

    [Theory]
    [InlineData("NordVPN", true)]
    [InlineData("nordvpn", true)]
    [InlineData("NordVPN 7.10.0", true)]
    [InlineData("NordVPN version 7.10.0", true)]
    [InlineData("NordVPN network TAP", false)]
    [InlineData("NordPass", false)]
    [InlineData("NordLayer", false)]
    [InlineData("MyNordVPN", false)]
    [InlineData("NordVPNX", false)]
    public void A_household_with_NordVPN_installed_is_pointed_at_the_plugin_once_and_never_on_the_locked_couch(string displayName, bool offered)
    {
        var suggestions = new PluginSuggestions(_dir, WithPrograms(displayName, "Something else"), "Anna");
        Assert.Empty(suggestions.Due(Entries(), [], locked: true));
        var due = suggestions.Due(Entries(), [], locked: false);
        if (!offered)
        {
            Assert.Empty(due);
            return;
        }
        var one = Assert.Single(due);
        Assert.Equal(("vpn", "program", "NordVPN"), (one.EntryId, one.Reason, one.Program));
        // It only points at the catalog: nothing is installed, an installed plugin is not offered, and a shown one is not offered again.
        Assert.Empty(new PluginSuggestions(_dir, WithPrograms(displayName), "Ben").Due(Entries(), ["vpn"], false));
        suggestions.MarkOffered("vpn");
        Assert.Empty(new PluginSuggestions(_dir, WithPrograms(displayName), "ANNA").Due(Entries(), [], false));
        Assert.Single(new PluginSuggestions(_dir, WithPrograms(displayName), "Ben").Due(Entries(), [], false));
    }

    [Fact]
    public void A_household_without_NordVPN_is_not_asked()
    {
        Assert.Empty(new PluginSuggestions(_dir, new FakeRegistry(), "Anna").Due(Entries(), [], false));
        Assert.Empty(new PluginSuggestions(_dir, WithPrograms("Steam", "CaseHUD"), "Anna").Due(Entries(), [], false));
    }

    // =====================================================================
    // The catalog entry

    [Fact]
    public void The_catalog_entry_in_the_repository_names_the_plugin_and_carries_the_owners_signature()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "catalog", "index.json"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var entries = CatalogParser.Parse(JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!, "catalog", "index.json"))).RootElement.GetProperty("entries").GetRawText(), "s");
        var entry = entries.Single(e => e.Id == "vpn");
        Assert.Equal(CatalogKind.Plugin, entry.Kind);
        Assert.Equal("NordVPN", entry.SuggestWhenUninstallEntry);
        Assert.Equal("CouchHud.Plugin.Vpn", entry.Folder);
        Assert.Equal("GPL-3.0-only", entry.License);
        // The owner signed it: CouchHUD's own statement for exactly this package, with its icon.
        SignedCatalogEntry.AssertOwnSigned(entry);
        // Its rights are the smallest set; a first-party plugin may have them (the lint of the package decides the rest).
        Assert.Null(PluginPolicy.CheckDeclared(PluginOrigin.FirstParty, entry.Capabilities));
        Assert.Equal(new[] { "native", "pages", "process", "quickActions", "registry" }, entry.Capabilities.Order());
        Assert.DoesNotContain(PluginCapabilities.SetupRun, entry.Capabilities);
        Assert.DoesNotContain(PluginCapabilities.Assistant, entry.Capabilities);
        Assert.DoesNotContain(PluginCapabilities.Tabs, entry.Capabilities);
        Assert.DoesNotContain(entry.Capabilities, c => c.StartsWith("network", StringComparison.Ordinal));
        // The template the owner signs says the same as the entry.
        using var template = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!, "catalog", "statements", "vpn.json")));
        Assert.Equal(entry.Capabilities.Order(), template.RootElement.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()!).Order());
        Assert.Equal("", template.RootElement.GetProperty("sha256").GetString());
        Assert.Equal("", template.RootElement.GetProperty("publisherKey").GetString());
        Assert.Equal(entry.Version.ToString(), template.RootElement.GetProperty("version").GetString());
        Assert.Equal("vpn", template.RootElement.GetProperty("id").GetString());
        Assert.Equal(entry.Folder, template.RootElement.GetProperty("folder").GetString());
        // The description in the catalog, the summary of the statement and the plugin's manifest all say the same.
        using var all = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!, "catalog", "index.json")));
        var raw = all.RootElement.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("id").GetString() == "vpn");
        Assert.Equal(template.RootElement.GetProperty("summary").GetString(), raw.GetProperty("description").GetProperty("en").GetString());
        var text0 = File.ReadAllText(Path.Combine(dir!, "catalog", "index.json"));
        Assert.DoesNotContain("},    {", text0);
        // The addresses are written in the canonical form.
        var text = File.ReadAllText(Path.Combine(dir!, "catalog", "index.json"));
        Assert.Contains("kewe87/CouchHUD-Catalog/releases/download/vpn-1.0.0/CouchHud.Plugin.Vpn-1.0.0.zip", text);
        Assert.DoesNotContain("couchhud-catalog/releases/download/vpn", text);
    }
}
