using System.Text;
using System.Text.Json;
using CouchHud.Core.Config;
using CouchHud.Core.Plugins;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>
/// Home Assistant moves out of the core into a plugin. What carries over (the address, the choice, the pins, the sign-in or the token) and
/// how: handed over as the plugin's first settings, the credential encrypted again for the plugin and never in plain text, for CouchHUD's
/// own plugin only; and what must not happen: a plugin without the right cannot sign in or take entries out of the menu, the token of
/// the plugin is of no use to another plugin, a broken config file ends as less and never as an error or a leak.
/// </summary>
public sealed class HomeAssistantPluginMigrationTests : IDisposable
{
    private const string ZipHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Token = "eyJhbGciOiJIUzI1NiJ9.long-lived-token-of-the-owner";
    private const string Refresh = "refresh-token-of-the-owner-0123456789";
    private static readonly byte[] TrustKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-ha-" + Guid.NewGuid().ToString("N"));

    public HomeAssistantPluginMigrationTests() => Directory.CreateDirectory(_dir);

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

    // The shell's DPAPI, reduced to what matters here: the text is bound to the scope (the plugin id) and says nothing readable.
    private static string Protect(string plain, string? scope) => "dpapi:" + Convert.ToBase64String(Encoding.UTF8.GetBytes((scope ?? "core") + "|" + plain)).Replace("=", "-");

    private static string Unprotect(string stored, string? scope)
    {
        if (!stored.StartsWith("dpapi:", StringComparison.Ordinal)) return stored;
        var text = Encoding.UTF8.GetString(Convert.FromBase64String(stored["dpapi:".Length..].Replace("-", "=")));
        var bar = text.IndexOf('|');
        return text[..bar] == (scope ?? "core") ? text[(bar + 1)..] : "";
    }

    private static PluginSettingSeeds.SecretMover Mover => (plugin, stored) => Protect(Unprotect(stored, null), plugin);

    private static AppConfig Configured() => new()
    {
        HomeAssistantUrl = "http://homeassistant.local:8123/",
        HomeAssistantToken = Protect(Token, null),
        HomeAssistantEntities = ["light.sofa", "scene.movie", "switch.lamp"],
        HomeAssistantPinned = ["light.sofa", "cover.garage"]
    };

    // =====================================================================
    // The seed

    [Fact]
    public void A_household_that_never_set_Home_Assistant_up_gets_no_seed_and_no_suggestion()
    {
        Assert.Null(PluginSettingSeeds.For(new AppConfig(), "homeassistant", Mover));
        Assert.Null(PluginSettingSeeds.For(new AppConfig { HomeAssistantUrl = "  " }, "homeassistant", Mover));
        var suggestions = new PluginSuggestions(_dir, new FakeRegistry(), "Anna", f => f == "homeAssistant" && PluginSettingSeeds.For(new AppConfig(), "homeassistant") is not null);
        Assert.Empty(suggestions.Due(Entries(), [], locked: false));
    }

    [Fact]
    public void The_address_the_choice_the_pins_and_the_token_come_over_as_the_first_settings()
    {
        var seed = PluginSettingSeeds.For(Configured(), "homeassistant", Mover)!;
        Assert.Equal("http://homeassistant.local:8123", seed["url"]);
        Assert.Equal("http://homeassistant.local:8123", seed["credentialUrl"]);
        Assert.Equal("""["light.sofa","scene.movie","switch.lamp"]""", seed["entities"]);
        // A pin of something that is not chosen is not carried over (the guide menu only holds chosen entities).
        Assert.Equal("""["light.sofa"]""", seed["pinned"]);
        Assert.False(seed.ContainsKey("refreshToken"));
        // The token is encrypted for the plugin, readable only as the plugin.
        Assert.Equal(Token, Unprotect(seed["token"]!, "homeassistant"));
        Assert.Equal("", Unprotect(seed["token"]!, null));
        Assert.NotEqual(Configured().HomeAssistantToken, seed["token"]);
    }

    [Fact]
    public void A_sign_in_comes_over_as_its_refresh_token_and_a_token_left_behind_does_not()
    {
        var config = Configured();
        config.HomeAssistantRefreshToken = Protect(Refresh, null);
        var seed = PluginSettingSeeds.For(config, "homeassistant", Mover)!;
        Assert.Equal(Refresh, Unprotect(seed["refreshToken"]!, "homeassistant"));
        // One credential at a time, as in the core: the sign-in wins.
        Assert.False(seed.ContainsKey("token"));
    }

    [Fact]
    public void No_secret_is_ever_in_the_seed_in_plain_text()
    {
        var config = Configured();
        foreach (var refresh in new[] { "", Protect(Refresh, null) })
        {
            config.HomeAssistantRefreshToken = refresh;
            var seed = PluginSettingSeeds.For(config, "homeassistant", Mover)!;
            foreach (var value in seed.Values) Assert.DoesNotContain(Token, value ?? "");
            foreach (var value in seed.Values) Assert.DoesNotContain(Refresh, value ?? "");
        }
        // A mover that hands back plain text, the unchanged text of the core, or nothing: the secret is dropped, the rest is kept.
        foreach (PluginSettingSeeds.SecretMover bad in new PluginSettingSeeds.SecretMover[] { (_, _) => Token, (_, stored) => stored, (_, _) => "", (_, _) => null, (_, _) => throw new InvalidOperationException("no DPAPI") })
        {
            var seed = PluginSettingSeeds.For(Configured(), "homeassistant", bad)!;
            Assert.False(seed.ContainsKey("token"));
            Assert.False(seed.ContainsKey("refreshToken"));
            Assert.Equal("http://homeassistant.local:8123", seed["url"]);
            Assert.Equal("""["light.sofa","scene.movie","switch.lamp"]""", seed["entities"]);
        }
        // Without a way to protect secrets nothing secret is handed over at all.
        var none = PluginSettingSeeds.For(Configured(), "homeassistant")!;
        Assert.False(none.ContainsKey("token"));
        Assert.False(none.ContainsKey("credentialUrl"));
    }

    [Fact]
    public void A_broken_config_ends_as_less_never_as_an_error_or_a_credential_for_an_unknown_server()
    {
        // No usable address: nobody knows which server the token belongs to, so it stays behind.
        var noAddress = new AppConfig { HomeAssistantUrl = "homeassistant.local", HomeAssistantToken = Protect(Token, null), HomeAssistantEntities = ["light.a"] };
        var seed = PluginSettingSeeds.For(noAddress, "homeassistant", Mover)!;
        Assert.Equal("", seed["url"]);
        Assert.False(seed.ContainsKey("token"));
        Assert.False(seed.ContainsKey("credentialUrl"));
        Assert.Equal("""["light.a"]""", seed["entities"]);

        // An address with user info, a query or an unusual scheme is no address either.
        foreach (var url in new[] { "ftp://ha.local", "http://user:pw@ha.local", "http://ha.local/?token=x", "file:///C:/x" })
            Assert.False(PluginSettingSeeds.For(new AppConfig { HomeAssistantUrl = url, HomeAssistantToken = Protect(Token, null) }, "homeassistant", Mover)!.ContainsKey("token"));

        // Lists that are null, malformed or huge.
        var odd = new AppConfig
        {
            HomeAssistantUrl = "https://ha.example.com",
            HomeAssistantEntities = ["light.a", "LIGHT.B", "light.a", "x", null!, "light.b,light.c", "../api", .. Enumerable.Range(0, 5000).Select(i => "light.l" + i)],
            HomeAssistantPinned = null!
        };
        var oddSeed = PluginSettingSeeds.For(odd, "homeassistant", Mover)!;
        var entities = JsonSerializer.Deserialize<string[]>(oddSeed["entities"]!)!;
        Assert.Equal(500, entities.Length);
        Assert.Equal("light.a", entities[0]);
        Assert.DoesNotContain("LIGHT.B", entities);
        Assert.Equal("[]", oddSeed["pinned"]);

        // A token that is just white space is no token.
        var blank = new AppConfig { HomeAssistantUrl = "https://ha.example.com", HomeAssistantToken = "   " };
        Assert.False(PluginSettingSeeds.For(blank, "homeassistant", Mover)!.ContainsKey("token"));
        // And a stored text that cannot be read: the secret goes, the settings stay.
        var garbage = new AppConfig { HomeAssistantUrl = "https://ha.example.com", HomeAssistantToken = "dpapi:%%%not-base64%%%" };
        Assert.False(PluginSettingSeeds.For(garbage, "homeassistant", (_, stored) => Protect(Unprotect(stored, null), "homeassistant"))!.ContainsKey("token"));
    }

    [Fact]
    public void An_install_from_before_the_picker_takes_its_pins_as_the_choice()
    {
        var config = new AppConfig { HomeAssistantUrl = "http://ha.local:8123", HomeAssistantPinned = ["light.a", "scene.b"] };
        var seed = PluginSettingSeeds.For(config, "homeassistant", Mover)!;
        Assert.Equal("""["light.a","scene.b"]""", seed["entities"]);
        Assert.Equal("""["light.a","scene.b"]""", seed["pinned"]);
    }

    [Theory]
    [InlineData("homeassistant-pro")]
    [InlineData("home-assistant")]
    [InlineData("ha")]
    [InlineData("")]
    public void Only_the_plugin_with_exactly_this_id_gets_the_seed(string id) => Assert.Null(PluginSettingSeeds.For(Configured(), id, Mover));

    [Fact]
    public void The_id_is_compared_without_case_and_the_other_seed_is_unchanged()
    {
        Assert.NotNull(PluginSettingSeeds.For(Configured(), "HomeAssistant", Mover));
        Assert.NotNull(PluginSettingSeeds.For(new AppConfig { CaseHudGamingLease = false }, "casehud"));
        Assert.Null(PluginSettingSeeds.For(Configured(), "casehud-x", Mover));
    }

    // =====================================================================
    // The plugin's settings store: the seed lands once, secrets stay encrypted, one plugin's secret is nothing to another

    private PluginManager Manager(AppConfig config, bool withProtector = true) => new([(Path.Combine(_dir, "plugins"), true)], _dir, _ => true)
    {
        Trust = new PluginTrust(_dir, TrustKey),
        SettingSeeds = id => PluginSettingSeeds.For(config, id, Mover),
        ProtectSecret = withProtector ? (id, plain) => Protect(plain, id) : null,
        UnprotectSecret = withProtector ? (id, stored) => Unprotect(stored, id) : null
    };

    private static IPluginContext Context(PluginManager manager, string id, PluginOrigin origin, params string[] caps) =>
        manager.ContextFor(id, new PluginManager.PluginGrant(id, origin, new HashSet<string>(caps)));

    [Fact]
    public void The_first_start_of_the_plugin_has_the_old_settings_and_the_sign_in_works_at_once()
    {
        var manager = Manager(Configured());
        var settings = Context(manager, "homeassistant", PluginOrigin.FirstParty).Settings;
        Assert.Equal("http://homeassistant.local:8123", settings.Get("url"));
        Assert.Equal("""["light.sofa","scene.movie","switch.lamp"]""", settings.Get("entities"));
        // Read as the plugin, the token is plain again.
        Assert.Equal(Token, settings.Get("token"));
        // On disk it never is.
        var file = File.ReadAllText(Path.Combine(_dir, "plugin-data", "homeassistant", "settings.json"));
        Assert.DoesNotContain(Token, file);
        Assert.Contains("dpapi:", file);
        Assert.DoesNotContain("long-lived", file);
    }

    [Fact]
    public void The_seed_is_used_once_a_later_change_of_the_old_config_does_not_bring_it_back()
    {
        var config = Configured();
        var settings = Context(Manager(config), "homeassistant", PluginOrigin.FirstParty).Settings;
        settings.Set("url", "http://other.local:8123");
        config.HomeAssistantUrl = "http://changed-in-core.local:8123";
        config.HomeAssistantToken = Protect("a newer token", null);
        // A new start: the plugin's file exists, the old config is not asked again.
        var again = Context(Manager(config), "homeassistant", PluginOrigin.FirstParty).Settings;
        Assert.Equal("http://other.local:8123", again.Get("url"));
        Assert.Equal(Token, again.Get("token"));
    }

    [Theory]
    [InlineData(PluginOrigin.ThirdParty)]
    [InlineData(PluginOrigin.Local)]
    public void A_plugin_that_is_not_CouchHUDs_own_never_gets_the_seed_even_with_the_name(PluginOrigin origin)
    {
        var settings = Context(Manager(Configured()), "homeassistant", origin, PluginCapabilities.Pages).Settings;
        Assert.Null(settings.Get("url"));
        Assert.Null(settings.Get("token"));
        Assert.Null(settings.Get("entities"));
    }

    [Fact]
    public void Another_plugin_cannot_read_the_token_of_the_Home_Assistant_plugin()
    {
        var manager = Manager(Configured());
        _ = Context(manager, "homeassistant", PluginOrigin.FirstParty).Settings.Get("token");
        foreach (var origin in new[] { PluginOrigin.FirstParty, PluginOrigin.ThirdParty, PluginOrigin.Local })
        {
            var other = Context(manager, "other-" + origin, origin).Settings;
            Assert.Null(other.Get("token"));
            Assert.Null(other.Get("refreshToken"));
            Assert.Null(other.Get("url"));
        }
        // Even with the stored text copied into its own file, another plugin reads nothing: the text is bound to the plugin id.
        var stored = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(Path.Combine(_dir, "plugin-data", "homeassistant", "settings.json")))!["token"]!;
        var thief = Context(manager, "thief", PluginOrigin.FirstParty).Settings;
        thief.Set("copy", "x");
        File.WriteAllText(Path.Combine(_dir, "plugin-data", "thief", "settings.json"), JsonSerializer.Serialize(new Dictionary<string, string?> { ["token"] = stored }));
        var fresh = Context(Manager(Configured()), "thief", PluginOrigin.FirstParty).Settings;
        Assert.Equal("", fresh.Get("token"));
        // Each plugin has its own file; the host's list of settings shows a secret only as "set".
        Assert.NotEqual(Path.Combine(_dir, "plugin-data", "homeassistant"), Path.Combine(_dir, "plugin-data", "thief"));
    }

    [Fact]
    public void A_setting_of_the_kind_Secret_is_kept_encrypted_whichever_way_it_is_set()
    {
        var manager = Manager(new AppConfig());
        var ctx = Context(manager, "tool", PluginOrigin.FirstParty, PluginCapabilities.Pages);
        ctx.RegisterSettings(new PluginSetting { Key = "token", Label = "Token", Kind = SettingKind.Secret }, new PluginSetting { Key = "url", Label = "Url", Kind = SettingKind.Text });
        // As the host's settings page sets it:
        manager.SettingsOf("tool").Set("token", "typed-secret");
        manager.SettingsOf("tool").Set("url", "http://plain.example");
        ctx.Settings.SetSecret("hidden", "bookkeeping-secret");
        var file = File.ReadAllText(Path.Combine(_dir, "plugin-data", "tool", "settings.json"));
        Assert.DoesNotContain("typed-secret", file);
        Assert.DoesNotContain("bookkeeping-secret", file);
        Assert.Contains("http://plain.example", file);
        Assert.Equal("typed-secret", ctx.Settings.Get("token"));
        Assert.Equal("bookkeeping-secret", ctx.Settings.Get("hidden"));
        ctx.Settings.SetSecret("hidden", null);
        Assert.Null(ctx.Settings.Get("hidden"));
    }

    [Fact]
    public void Without_a_protector_a_secret_is_not_kept_at_all_and_a_plain_value_cannot_pose_as_one()
    {
        var manager = Manager(new AppConfig(), withProtector: false);
        var ctx = Context(manager, "tool", PluginOrigin.FirstParty);
        ctx.Settings.SetSecret("token", "secret");
        Assert.Null(ctx.Settings.Get("token"));
        Assert.DoesNotContain("secret", File.ReadAllText(Path.Combine(_dir, "plugin-data", "tool", "settings.json")));
        // A plugin cannot plant a stored-looking value that would be decrypted as something else.
        var withProtector = Context(Manager(new AppConfig()), "tool2", PluginOrigin.FirstParty).Settings;
        withProtector.Set("note", "dpapi:Zm9v");
        Assert.Null(withProtector.Get("note"));
        // A file that holds a stored text and no way to read it: empty, never the stored text.
        File.WriteAllText(Path.Combine(_dir, "plugin-data", "tool", "settings.json"), """{"token":"dpapi:Zm9v"}""");
        Assert.Equal("", Context(Manager(new AppConfig(), withProtector: false), "tool", PluginOrigin.FirstParty).Settings.Get("token"));
    }

    // =====================================================================
    // What a plugin without the right cannot do

    [Fact]
    public void Without_the_capability_a_plugin_can_neither_sign_in_nor_take_entries_out_of_the_menu()
    {
        var opened = 0;
        var manager = Manager(new AppConfig());
        manager.SignInOpener = (_, _, _, _, _) => ++opened > 0;
        var ctx = Context(manager, "tool", PluginOrigin.FirstParty, PluginCapabilities.Pages);
        Assert.Throws<PluginCapabilityException>(() => ctx.SignIn);
        Assert.Throws<PluginCapabilityException>(() => ctx.RemoveQuickAction("pin.light.sofa"));
        Assert.Equal(0, opened);
    }

    [Theory]
    [InlineData(PluginOrigin.ThirdParty)]
    [InlineData(PluginOrigin.Local)]
    public void A_plugin_that_is_not_CouchHUDs_own_can_never_have_the_sign_in_whatever_it_declares(PluginOrigin origin)
    {
        Assert.Equal("plugin.cap.firstPartyOnly", PluginPolicy.CheckDeclared(origin, [PluginCapabilities.BrowserSignIn]));
        var granted = PluginPolicy.Granted(origin, [PluginCapabilities.BrowserSignIn, PluginCapabilities.Pages], 3, [PluginCapabilities.BrowserSignIn, PluginCapabilities.Pages]);
        Assert.DoesNotContain(PluginCapabilities.BrowserSignIn, granted);
        var manager = Manager(new AppConfig());
        manager.SignInOpener = (_, _, _, _, _) => true;
        var ctx = Context(manager, "tool", origin, [.. granted]);
        Assert.Throws<PluginCapabilityException>(() => ctx.SignIn);
    }

    [Fact]
    public void The_sign_in_goes_through_the_host_with_the_plugin_named_and_a_late_redirect_is_cancelled()
    {
        var manager = Manager(new AppConfig());
        string? who = null;
        string? host = null;
        var cancelled = new List<string>();
        manager.SignInOpener = (id, _, _, redirectHost, _) =>
        {
            (who, host) = (id, redirectHost);
            return true;
        };
        manager.SignInCanceller = cancelled.Add;
        var ctx = Context(manager, "homeassistant", PluginOrigin.FirstParty, PluginCapabilities.BrowserSignIn);
        Assert.True(ctx.SignIn!.Open("http://ha.local:8123/auth/authorize", "homeassistant.couchhud.invalid", (_, _) => { }));
        Assert.Equal(("homeassistant", "homeassistant.couchhud.invalid"), (who, host));
        ctx.SignIn.Cancel();
        Assert.Equal(["homeassistant"], cancelled);
        // A host without a browser (no opener) refuses, and a receiver is required.
        var none = Context(Manager(new AppConfig()), "homeassistant", PluginOrigin.FirstParty, PluginCapabilities.BrowserSignIn);
        Assert.False(none.SignIn is { } s && s.Open("http://ha.local/", "x.invalid", (_, _) => { }));
        Assert.False(ctx.SignIn.Open("http://ha.local/", "x.invalid", null!));
    }

    // =====================================================================
    // The suggestion

    private static IReadOnlyList<CatalogEntry> Entries() => CatalogParser.Parse($$$"""
        [{"id":"homeassistant","folder":"CouchHud.Plugin.HomeAssistant","name":"Home Assistant","type":"plugin","version":"1.0.0","download":"https://x.example/h.zip","sha256":"{{{ZipHash}}}","suggestWhen":{"feature":"homeAssistant"}},
         {"id":"other","name":"Other","type":"plugin","version":"1.0.0","download":"https://x.example/o.zip","sha256":"{{{ZipHash}}}","suggestWhen":{"feature":"somethingElse"}},
         {"id":"plain","name":"Plain","type":"plugin","version":"1.0.0","download":"https://x.example/p.zip","sha256":"{{{ZipHash}}}"}]
        """, "s");

    [Fact]
    public void A_household_that_had_Home_Assistant_is_pointed_at_the_plugin_once_and_never_on_the_locked_couch()
    {
        var had = new PluginSuggestions(_dir, new FakeRegistry(), "Anna", f => f == "homeAssistant");
        Assert.Empty(had.Due(Entries(), [], locked: true));
        var due = Assert.Single(had.Due(Entries(), [], locked: false));
        Assert.Equal(("homeassistant", "feature", "homeAssistant"), (due.EntryId, due.Reason, due.Program));
        // It only points at the catalog: marking it offered is all that happens, and an installed plugin is not suggested.
        Assert.Empty(new PluginSuggestions(_dir, new FakeRegistry(), "Ben", f => f == "homeAssistant").Due(Entries(), ["homeassistant"], false));
        had.MarkOffered("homeassistant");
        Assert.Empty(new PluginSuggestions(_dir, new FakeRegistry(), "ANNA", f => f == "homeAssistant").Due(Entries(), [], false));
        Assert.Single(new PluginSuggestions(_dir, new FakeRegistry(), "Ben", f => f == "homeAssistant").Due(Entries(), [], false));
    }

    [Fact]
    public void Only_a_feature_CouchHUD_knows_can_be_named_in_a_catalog_entry()
    {
        var entries = Entries();
        Assert.Equal("homeAssistant", entries.Single(e => e.Id == "homeassistant").SuggestWhenFeature);
        Assert.Null(entries.Single(e => e.Id == "other").SuggestWhenFeature);
        Assert.Null(entries.Single(e => e.Id == "plain").SuggestWhenFeature);
        // Whatever the feature, a household without it is not asked.
        Assert.Empty(new PluginSuggestions(_dir, new FakeRegistry(), "Anna", _ => false).Due(entries, [], false));
        Assert.Empty(new PluginSuggestions(_dir, new FakeRegistry(), "Anna").Due(entries, [], false));
    }

    [Fact]
    public void The_catalog_entry_in_the_repository_names_the_plugin_and_carries_the_owners_signature()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "catalog", "index.json"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var entries = CatalogParser.Parse(JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!, "catalog", "index.json"))).RootElement.GetProperty("entries").GetRawText(), "s");
        var entry = entries.Single(e => e.Id == "homeassistant");
        Assert.Equal(CatalogKind.Plugin, entry.Kind);
        Assert.Equal("homeAssistant", entry.SuggestWhenFeature);
        // The owner signed it: CouchHUD's own statement for exactly this package, with its icon.
        SignedCatalogEntry.AssertOwnSigned(entry);
        Assert.NotNull(PluginPolicy.CheckDeclared(PluginOrigin.ThirdParty, entry.Capabilities));
        Assert.Null(PluginPolicy.CheckDeclared(PluginOrigin.FirstParty, entry.Capabilities));
        // The template the owner signs says the same as the entry.
        using var template = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!, "catalog", "statements", "homeassistant.json")));
        Assert.Equal(entry.Capabilities.Order(), template.RootElement.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()!).Order());
        Assert.Equal("", template.RootElement.GetProperty("sha256").GetString());
        Assert.Equal("", template.RootElement.GetProperty("publisherKey").GetString());
    }
}
