using System.Text.Json;
using CouchHud.Core.Config;
using CouchHud.Core.Plugins;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>
/// CaseHUD moved out of the core into a plugin (plugin repo CouchHud.Plugin.CaseHud), and the core's own integration is gone. What carries over (the
/// old game-report setting, as the plugin's first settings, for CouchHUD's own plugin of that id and once; there is no secret to carry: CaseHUD
/// writes its own token and CouchHUD never kept one), the offer for a household that has CaseHUD, the signed catalog entry, and the
/// host service that hands a downloaded setup to Windows.
/// </summary>
public sealed class CaseHudPluginMigrationTests : IDisposable
{
    private const string ZipHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly byte[] TrustKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-casehud-mig-" + Guid.NewGuid().ToString("N"));

    public CaseHudPluginMigrationTests() => Directory.CreateDirectory(_dir);

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

    private static AppConfig Configured() => new()
    {
        CaseHudGamingLease = false
    };

    // =====================================================================
    // The seed

    [Fact]
    public void Only_the_game_report_comes_over_as_the_plugins_first_setting_the_setup_options_are_CaseHUDs_own_questions()
    {
        var seed = PluginSettingSeeds.For(Configured(), "casehud")!;
        Assert.Equal(["gamingLease"], seed.Keys);
        Assert.Equal("false", seed["gamingLease"]);
        // The default of a household that never touched it.
        Assert.Equal("true", PluginSettingSeeds.For(new AppConfig(), "casehud")!["gamingLease"]);
    }

    [Fact]
    public void There_is_no_secret_to_carry_over_so_the_seed_never_asks_for_the_secret_mover()
    {
        var asked = 0;
        var seed = PluginSettingSeeds.For(Configured(), "casehud", (_, _) =>
        {
            asked++;
            return "dpapi:x";
        })!;
        Assert.Equal(0, asked);
        Assert.All(seed.Values, v => Assert.True(v is "true" or "false"));
        // The config knows no CaseHUD token, address or path: the one legacy field is the one the seed reads.
        var names = typeof(AppConfig).GetProperties().Select(p => p.Name).Where(n => n.StartsWith("CaseHud", StringComparison.Ordinal)).Order();
        Assert.Equal(["CaseHudGamingLease"], names);
    }

    [Theory]
    [InlineData("casehud-pro")]
    [InlineData("case-hud")]
    [InlineData("hud")]
    [InlineData("")]
    public void Only_the_plugin_with_exactly_this_id_gets_the_seed(string id) => Assert.Null(PluginSettingSeeds.For(Configured(), id));

    [Fact]
    public void The_id_is_compared_without_case()
    {
        Assert.NotNull(PluginSettingSeeds.For(Configured(), "CaseHud"));
        Assert.Null(PluginSettingSeeds.For(Configured(), "homeassistant"));
    }

    private PluginManager Manager(AppConfig config) => new([(Path.Combine(_dir, "plugins"), true)], _dir, _ => true)
    {
        Trust = new PluginTrust(_dir, TrustKey),
        SettingSeeds = id => PluginSettingSeeds.For(config, id)
    };

    private static IPluginContext Context(PluginManager manager, string id, PluginOrigin origin, params string[] caps) =>
        manager.ContextFor(id, new PluginManager.PluginGrant(id, origin, new HashSet<string>(caps)));

    [Fact]
    public void The_first_start_of_the_plugin_has_the_old_settings_and_the_seed_is_used_up()
    {
        var applied = new List<string>();
        var manager = Manager(Configured());
        manager.SeedApplied = applied.Add;
        var settings = Context(manager, "casehud", PluginOrigin.FirstParty).Settings;
        Assert.False(settings.GetBool("gamingLease", true));
        Assert.Null(settings.Get("installFps"));
        Assert.Equal(["casehud"], applied);
    }

    [Fact]
    public void The_seed_is_used_once_a_later_change_of_the_old_config_does_not_bring_it_back()
    {
        var config = Configured();
        var settings = Context(Manager(config), "casehud", PluginOrigin.FirstParty).Settings;
        settings.Set("gamingLease", "true");
        config.CaseHudGamingLease = false;
        var again = Context(Manager(config), "casehud", PluginOrigin.FirstParty).Settings;
        Assert.True(again.GetBool("gamingLease"));
    }

    [Theory]
    [InlineData(PluginOrigin.ThirdParty)]
    [InlineData(PluginOrigin.Local)]
    public void A_plugin_that_is_not_CouchHUDs_own_never_gets_the_seed_even_with_the_name(PluginOrigin origin)
    {
        var settings = Context(Manager(Configured()), "casehud", origin, PluginCapabilities.Pages).Settings;
        Assert.Null(settings.Get("gamingLease"));
        Assert.Null(settings.Get("installFps"));
    }

    [Fact]
    public void The_name_casehud_is_reserved_so_a_third_party_cannot_ship_a_plugin_that_takes_the_name_or_the_seed()
    {
        Assert.True(PluginNames.IsReserved("casehud"));
        Assert.True(PluginNames.IsReserved("CaseHUD-Pro"));
        Assert.True(PluginNames.IsReserved("my_casehud"));
    }

    // =====================================================================
    // The offer

    private static IReadOnlyList<CatalogEntry> Entries() => CatalogParser.Parse($$$"""
        [{"id":"casehud","folder":"CouchHud.Plugin.CaseHud","name":"CaseHUD","type":"plugin","version":"1.0.0","download":"https://x.example/c.zip","sha256":"{{{ZipHash}}}","suggestWhen":{"uninstallEntry":"CaseHUD"}},
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
    [InlineData("CaseHUD", true)]
    [InlineData("casehud", true)]
    [InlineData("CaseHUD version 1.0.5", true)]
    [InlineData("CaseHUD 1.0.5", true)]
    [InlineData("CaseHUD v1.0.5", true)]
    [InlineData("CaseHUD Tools", false)]
    [InlineData("MyCaseHUD", false)]
    [InlineData("CaseHUDX", false)]
    public void A_household_with_CaseHUD_installed_is_pointed_at_the_plugin_once_and_never_on_the_locked_couch(string displayName, bool offered)
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
        Assert.Equal(("casehud", "program", "CaseHUD"), (one.EntryId, one.Reason, one.Program));
        // It only points at the catalog: nothing is installed, an installed plugin is not offered, and a shown one is not offered again.
        Assert.Empty(new PluginSuggestions(_dir, WithPrograms(displayName), "Ben").Due(Entries(), ["casehud"], false));
        suggestions.MarkOffered("casehud");
        Assert.Empty(new PluginSuggestions(_dir, WithPrograms(displayName), "ANNA").Due(Entries(), [], false));
        Assert.Single(new PluginSuggestions(_dir, WithPrograms(displayName), "Ben").Due(Entries(), [], false));
    }

    [Fact]
    public void A_household_without_CaseHUD_is_not_asked()
    {
        Assert.Empty(new PluginSuggestions(_dir, new FakeRegistry(), "Anna").Due(Entries(), [], false));
        Assert.Empty(new PluginSuggestions(_dir, WithPrograms("Steam", "NordVPN"), "Anna").Due(Entries(), [], false));
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
        var entry = entries.Single(e => e.Id == "casehud");
        Assert.Equal(CatalogKind.Plugin, entry.Kind);
        Assert.Equal("CaseHUD", entry.SuggestWhenUninstallEntry);
        Assert.Equal("CouchHud.Plugin.CaseHud", entry.Folder);
        Assert.Equal("GPL-3.0-only", entry.License);
        // The owner signed it: CouchHUD's own statement for exactly this package, with its icon.
        SignedCatalogEntry.AssertOwnSigned(entry);
        // Its rights: CouchHUD's own plugin may have them all, a third party could not even declare the three host services.
        Assert.Null(PluginPolicy.CheckDeclared(PluginOrigin.FirstParty, entry.Capabilities));
        Assert.NotNull(PluginPolicy.CheckDeclared(PluginOrigin.ThirdParty, entry.Capabilities));
        Assert.Contains(PluginCapabilities.SetupRun, entry.Capabilities);
        Assert.Contains(PluginCapabilities.LockRestrict, entry.Capabilities);
        Assert.DoesNotContain(PluginCapabilities.Assistant, entry.Capabilities);
        // The template the owner signs says the same as the entry.
        using var template = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!, "catalog", "statements", "casehud.json")));
        Assert.Equal(entry.Capabilities.Order(), template.RootElement.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()!).Order());
        Assert.Equal("", template.RootElement.GetProperty("sha256").GetString());
        Assert.Equal("", template.RootElement.GetProperty("publisherKey").GetString());
        Assert.Equal(entry.Version.ToString(), template.RootElement.GetProperty("version").GetString());
    }

    // =====================================================================
    // The host hands a downloaded setup to Windows (setup.run): first-party only, and the shell re-checks everything

    [Fact]
    public async Task The_setup_goes_to_the_host_with_the_plugin_named_and_nothing_runs_without_a_host()
    {
        var manager = Manager(new AppConfig());
        var asked = new List<(string Plugin, string Path, string Sha)>();
        manager.SetupRunner = (id, plugin, path, sha, _) =>
        {
            Assert.Equal("casehud", id);
            asked.Add((plugin, path, sha));
            return Task.FromResult(true);
        };
        var ctx = Context(manager, "casehud", PluginOrigin.FirstParty, PluginCapabilities.SetupRun);
        Assert.True(await ctx.RunSetupAsync(@"C:\x\Setup.exe", ZipHash, CancellationToken.None));
        Assert.Single(asked);
        Assert.Equal(@"C:\x\Setup.exe", asked[0].Path);
        // A host without the runner answers no.
        var none = Context(Manager(new AppConfig()), "casehud", PluginOrigin.FirstParty, PluginCapabilities.SetupRun);
        Assert.False(await none.RunSetupAsync(@"C:\x\Setup.exe", ZipHash, CancellationToken.None));
    }

    [Theory]
    [InlineData(PluginOrigin.ThirdParty)]
    [InlineData(PluginOrigin.Local)]
    public async Task A_plugin_that_is_not_CouchHUDs_own_can_never_hand_over_a_setup_whatever_it_declares(PluginOrigin origin)
    {
        Assert.Equal("plugin.cap.firstPartyOnly", PluginPolicy.CheckDeclared(origin, [PluginCapabilities.SetupRun]));
        var granted = PluginPolicy.Granted(origin, [PluginCapabilities.SetupRun, PluginCapabilities.Pages], 3, [PluginCapabilities.SetupRun, PluginCapabilities.Pages]);
        Assert.DoesNotContain(PluginCapabilities.SetupRun, granted);
        var manager = Manager(new AppConfig());
        var ran = false;
        manager.SetupRunner = (_, _, _, _, _) =>
        {
            ran = true;
            return Task.FromResult(true);
        };
        var ctx = Context(manager, "tool", origin, [.. granted]);
        await Assert.ThrowsAsync<PluginCapabilityException>(() => ctx.RunSetupAsync(@"C:\x\Setup.exe", ZipHash, CancellationToken.None));
        Assert.False(ran);
    }

    [Fact]
    public async Task Without_the_capability_even_CouchHUDs_own_plugin_cannot_hand_over_a_setup()
    {
        var manager = Manager(new AppConfig());
        manager.SetupRunner = (_, _, _, _, _) => Task.FromResult(true);
        var ctx = Context(manager, "casehud", PluginOrigin.FirstParty, PluginCapabilities.Pages);
        await Assert.ThrowsAsync<PluginCapabilityException>(() => ctx.RunSetupAsync(@"C:\x\Setup.exe", ZipHash, CancellationToken.None));
    }

    [Fact]
    public void A_plugin_with_the_restriction_and_startup_rights_may_name_a_program_only_below_Program_Files()
    {
        var manager = Manager(new AppConfig());
        manager.Host.CanonicalTrustedExe = p => p.StartsWith(@"C:\Program Files\", StringComparison.OrdinalIgnoreCase) ? p : null;
        var ctx = Context(manager, "casehud", PluginOrigin.FirstParty, PluginCapabilities.LockRestrict, PluginCapabilities.StartupArgs);
        Assert.True(ctx.RestrictWhileLocked(@"C:\Program Files\CaseHUD\CaseHUD.exe"));
        Assert.False(ctx.RestrictWhileLocked(@"C:\Users\kid\CaseHUD.exe"));
        Assert.False(ctx.RestrictWhileLocked("CaseHUD.exe"));
        Assert.True(ctx.AdaptStartupArgs(@"C:\Program Files\CaseHUD\CaseHUD.exe", a => a));
        Assert.False(ctx.AdaptStartupArgs(@"C:\Users\kid\CaseHUD.exe", a => a));
        Assert.Contains(@"C:\Program Files\CaseHUD\CaseHUD.exe", manager.Host.RestrictedExes);
        // The adapter changes only the arguments of the entry that starts that very program.
        manager.Host.CanonicalTrustedExe = p => p;
        var compose = manager.Host.ComposeStartup(null);
        Assert.Equal(@"""C:\Program Files\CaseHUD\CaseHUD.exe""", compose(@"""C:\Program Files\CaseHUD\CaseHUD.exe"""));
        Assert.Equal(@"""C:\Other\Tool.exe"" --minimized", compose(@"""C:\Other\Tool.exe"" --minimized"));
    }
}

/// <summary>The checks before a setup that a plugin downloaded is handed to Windows (the shell adds the unlock, the held handle and UAC).</summary>
public sealed class PluginSetupRulesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "couchhud-setup-rules-" + Guid.NewGuid().ToString("N"), "plugin-data");
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    public PluginSetupRulesTests() => Directory.CreateDirectory(Path.Combine(_root, "casehud", "setup"));

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_root)!, true);

    private string Good => Path.Combine(_root, "casehud", "setup", "abc", "CaseHUD-Setup-1.0.5.exe");

    [Fact]
    public void A_setup_in_a_plugins_data_folder_with_a_real_hash_may_go()
    {
        Assert.Null(PluginSetupRules.Refuse(Good, Hash, _root));
        Assert.Null(PluginSetupRules.Refuse(Good, Hash.ToUpperInvariant(), _root));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"Setup.exe")]
    [InlineData(@"\\server\share\plugin-data\Setup.exe")]
    [InlineData("C:/plugin-data/x/Setup.exe")]
    public void A_path_that_is_not_a_full_local_path_is_refused(string? path) => Assert.Equal("path", PluginSetupRules.Refuse(path, Hash, _root));

    [Fact]
    public void Dots_control_characters_and_overlong_names_are_refused()
    {
        Assert.Equal("path", PluginSetupRules.Refuse(Path.Combine(_root, "casehud", "..", "..", "evil.exe"), Hash, _root));
        Assert.Equal("path", PluginSetupRules.Refuse(_root + @"\casehud\.\Setup.exe", Hash, _root));
        Assert.Equal("path", PluginSetupRules.Refuse(Good + "\u0007", Hash, _root));
        Assert.Equal("path", PluginSetupRules.Refuse(Path.Combine(_root, new string('a', 300) + ".exe"), Hash, _root));
    }

    [Theory]
    [InlineData(@"Setup.msi")]
    [InlineData(@"Setup.bat")]
    [InlineData(@"Setup.exe.txt")]
    [InlineData(@"Setup")]
    public void Only_an_exe_goes(string name) => Assert.Equal("type", PluginSetupRules.Refuse(Path.Combine(_root, "casehud", name), Hash, _root));

    [Fact]
    public void Only_a_file_inside_the_plugins_data_folder_goes()
    {
        Assert.Equal("place", PluginSetupRules.Refuse(@"C:\Windows\System32\cmd.exe", Hash, _root));
        Assert.Equal("place", PluginSetupRules.Refuse(@"C:\Program Files\CaseHUD\CaseHUD.exe", Hash, _root));
        Assert.Equal("place", PluginSetupRules.Refuse(Path.Combine(Path.GetDirectoryName(_root)!, "plugin-data-evil", "Setup.exe"), Hash, _root));
        Assert.Equal("place", PluginSetupRules.Refuse(Path.Combine(Path.GetDirectoryName(_root)!, "Setup.exe"), Hash, _root));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0")]
    public void A_hash_that_is_not_64_hex_digits_is_refused(string? hash) => Assert.Equal("hash", PluginSetupRules.Refuse(Good, hash, _root));

    [Fact]
    public void The_hash_is_checked_against_the_bytes_that_are_held()
    {
        var bytes = "pretend this is a setup"u8.ToArray();
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        using var stream = new MemoryStream(bytes);
        Assert.True(PluginSetupRules.HashMatches(stream, sha));
        Assert.Equal(0, stream.Position);
        Assert.True(PluginSetupRules.HashMatches(stream, sha.ToLowerInvariant()));
        Assert.False(PluginSetupRules.HashMatches(stream, Hash));
        using var other = new MemoryStream("a different file"u8.ToArray());
        Assert.False(PluginSetupRules.HashMatches(other, sha));
        Assert.False(PluginSetupRules.HashMatches(new MemoryStream(), sha));
    }

    [Fact]
    public void The_time_and_the_size_are_bounded()
    {
        Assert.True(PluginSetupRules.MaxRun <= TimeSpan.FromMinutes(10));
        Assert.True(PluginSetupRules.MaxSetupBytes <= 400L * 1024 * 1024);
    }
}
