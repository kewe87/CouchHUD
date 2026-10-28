using System.Text.Json;
using System.Text.RegularExpressions;
using CouchHud.Core.Config;
using CouchHud.Core.Skins;

namespace CouchHud.Core.Tests;

/// <summary>
/// 0.5 renamed the built-in skins that carried third-party names, 0.6 moved four of them to the catalog: old ids move to the current
/// ones once, a skin that moved becomes "user:&lt;id&gt;" (the id of every installed skin), nothing else changes.
/// </summary>
[Collection("Language")]
public class SkinRenameTests
{
    [Theory]
    [InlineData("ps4", "deepsea")]
    [InlineData("xbox", "greenlight")]
    [InlineData("cyberpunk", "neoncity")]
    public void An_old_id_maps_to_the_new_one(string old, string now)
    {
        Assert.Equal(now, SkinCatalog.MigrateId(old));
        Assert.Contains(now, SkinCatalog.MovedToCatalogIds);
        Assert.Contains(now, SkinCatalog.SoundSetIds);
        Assert.DoesNotContain(old, SkinCatalog.BuiltinIds);
        // And as a skin it is one of the catalog skins now.
        Assert.Equal("user:" + now, SkinCatalog.MigrateSkinId(old));
    }

    [Theory]
    [InlineData("default")]
    [InlineData("deepsea")]
    [InlineData("nova")]
    [InlineData("user:ps4")]
    [InlineData("user:deepsea")]
    [InlineData("PS4")]
    [InlineData("gone")]
    [InlineData("")]
    [InlineData(null)]
    public void Any_other_id_stays_as_it_is_for_sound_sets(string? id) => Assert.Equal(id, SkinCatalog.MigrateId(id));

    [Theory]
    [InlineData("deepsea", "user:deepsea")]
    [InlineData("greenlight", "user:greenlight")]
    [InlineData("neoncity", "user:neoncity")]
    [InlineData("nova", "user:nova")]
    [InlineData("default", "default")]
    [InlineData("user:deepsea", "user:deepsea")]
    [InlineData("user:stardust", "user:stardust")]
    [InlineData("user:ps4", "user:ps4")]
    [InlineData("PS4", "PS4")]
    [InlineData("gone", "gone")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void A_skin_that_moved_to_the_catalog_gets_the_id_of_an_installed_skin(string? stored, string? now) =>
        Assert.Equal(now, SkinCatalog.MigrateSkinId(stored));

    [Fact]
    public void An_old_config_moves_skin_and_sound_set_of_household_and_profiles()
    {
        var c = ConfigStore.Parse("""{ "skin": "ps4", "soundSet": "cyberpunk" }""")!;
        var ben = c.AddProfile("Ben");
        ben.Skin = "xbox";
        ben.SoundSet = "ps4";
        var mia = c.AddProfile("Mia");
        mia.Skin = "gone";
        var json = JsonSerializer.Serialize(c, ConfigStore.JsonOptions);

        var read = ConfigStore.Parse(json)!;

        // The skin is the catalog skin of that name now; the sound set keeps its id (the synthesized sets stay in the app).
        Assert.Equal("user:deepsea", read.Skin);
        Assert.Equal("neoncity", read.SoundSet);
        Assert.Equal("user:greenlight", read.Profile(ben.Id).Skin);
        Assert.Equal("deepsea", read.Profile(ben.Id).SoundSet);
        // Unknown ids are left alone: the HUD falls back to the default skin as before.
        Assert.Equal("gone", read.Profile(mia.Id).Skin);
        // A setting the profile does not have still follows the household.
        Assert.Equal("neoncity", read.Profile(mia.Id).SoundSet);
        // The next save writes the new ids.
        var saved = JsonSerializer.Serialize(read, ConfigStore.JsonOptions);
        Assert.DoesNotContain("\"ps4\"", saved);
        Assert.DoesNotContain("\"xbox\"", saved);
        Assert.DoesNotContain("\"cyberpunk\"", saved);
    }

    [Fact]
    public void A_0_5_config_keeps_its_skin_as_the_catalog_skin_and_nothing_else_changes()
    {
        var c = ConfigStore.Parse("""{ "skin": "nova", "soundSet": "nova" }""")!;
        var ben = c.AddProfile("Ben");
        ben.Skin = "greenlight";
        var mia = c.AddProfile("Mia");
        var read = ConfigStore.Parse(JsonSerializer.Serialize(c, ConfigStore.JsonOptions))!;

        Assert.Equal("user:nova", read.Skin);
        Assert.Equal("nova", read.SoundSet);
        Assert.Equal("user:greenlight", read.Profile(ben.Id).Skin);
        // A profile with no skin of its own follows the household.
        Assert.Equal("user:nova", read.Profile(mia.Id).Skin);
        // A second read changes nothing more.
        Assert.Equal("user:nova", ConfigStore.Parse(JsonSerializer.Serialize(read, ConfigStore.JsonOptions))!.Skin);
        // Everything the household and the profiles ask for, for the restore after the update.
        Assert.Equal(["user:greenlight", "user:nova"], ConfigStore.AskedSkins(read).OfType<string>().OrderBy(s => s, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void New_and_unchanged_ids_survive_a_read()
    {
        var c = ConfigStore.Parse("""{ "skin": "user:xbox", "soundSet": "" }""")!;
        Assert.Equal("user:xbox", c.Skin);
        Assert.Equal("", c.SoundSet);
        Assert.Equal("user:nova", ConfigStore.Parse("""{ "skin": "nova" }""")!.Skin);
        Assert.Equal("default", ConfigStore.Parse("""{ "skin": "default" }""")!.Skin);
    }

    [Fact]
    public void Old_ids_are_no_skins_of_their_own_and_only_installed_skins_are_known()
    {
        Assert.False(SkinCatalog.IsKnown("ps4", []));
        Assert.True(SkinCatalog.IsKnown("default", []));
        // Not installed: the HUD does not switch to it (and shows the Couch skin).
        Assert.False(SkinCatalog.IsKnown("deepsea", []));
        Assert.False(SkinCatalog.IsKnown("user:deepsea", []));

        var dir = Directory.CreateTempSubdirectory("couchhud-skins-").FullName;
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(dir, "deepsea")).FullName;
            File.WriteAllText(Path.Combine(folder, "skin.json"), """{ "name": "My deep sea" }""");
            File.WriteAllText(Path.Combine(folder, "skin.css"), ":root[data-skin=\"user:deepsea\"] { --accent: teal; }");
            var installed = SkinCatalog.ListUserSkins(dir);
            var skin = Assert.Single(installed);
            Assert.Equal("user:deepsea", skin.Id);
            Assert.True(SkinCatalog.IsKnown("user:deepsea", installed));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static SkinInfo Installed(string folder, string name, params string[] aliases) =>
        new("user:" + folder, name, "", new Dictionary<string, string>(), "file:///x") { Aliases = aliases };

    [Theory]
    [InlineData("xbox", "user:greenlight")]
    [InlineData("ps4", "user:deepsea")]
    [InlineData("cyberpunk", "user:neoncity")]
    [InlineData("XBOX", "user:greenlight")]
    [InlineData("neoncity", "user:neoncity")]
    [InlineData("NeonCity", "user:neoncity")]
    [InlineData("Neonstadt", "user:neoncity")]
    [InlineData("neon city", "user:neoncity")]
    [InlineData("Tiefsee", "user:deepsea")]
    [InlineData("  deep sea ", "user:deepsea")]
    [InlineData("grünlicht", "user:greenlight")]
    [InlineData("Greenlight", "user:greenlight")]
    [InlineData("couchhud", "default")]
    [InlineData("Nova", "user:nova")]
    [InlineData("user:nova", "user:nova")]
    [InlineData("nova", "user:nova")]
    [InlineData("user:neon", "user:neon")]
    [InlineData("Synthwave", "user:neon")]
    [InlineData("Sternenstaub", "user:stardust")]
    [InlineData("stardust", "user:stardust")]
    public void The_assistant_finds_an_installed_skin_by_id_old_id_or_name(string asked, string id)
    {
        var skins = new[]
        {
            Installed("neon", "Synthwave"), Installed("stardust", "Stardust", "Sternenstaub", "Stardust"),
            Installed("deepsea", "Deep Sea", "Tiefsee", "Deep Sea"), Installed("greenlight", "Greenlight"),
            Installed("neoncity", "Neon City"), Installed("nova", "Nova")
        };
        Assert.Equal(id, SkinCatalog.Resolve(asked, skins));
    }

    [Theory]
    [InlineData("Tiefsee")]
    [InlineData("deepsea")]
    [InlineData("ps4")]
    [InlineData("user:deepsea")]
    public void A_skin_that_is_not_installed_is_not_found_by_its_old_name(string asked) =>
        Assert.Null(SkinCatalog.Resolve(asked, [Installed("neon", "Synthwave")]));

    [Theory]
    [InlineData("Tiefsee")]
    [InlineData("Deep Sea")]
    [InlineData("deepsea")]
    [InlineData("CouchHUD")]
    [InlineData("nova")]
    public void Another_skin_cannot_take_over_the_name_of_the_couch_skin_or_of_a_moved_skin(string name)
    {
        // Named like the real one but in another folder, with the real ones not installed (or installed elsewhere).
        var impostor = Installed("evil", name, name);
        // (The Couch skin is always there: its name means it, never the impostor.)
        Assert.Equal(name == "CouchHUD" ? "default" : null, SkinCatalog.Resolve(name, [impostor]));
        // The skin in the folder of that very id still answers to its own name, next to the impostor.
        var real = Installed("deepsea", "Deep Sea", "Tiefsee");
        if (name is "Tiefsee" or "Deep Sea" or "deepsea") Assert.Equal("user:deepsea", SkinCatalog.Resolve(name, [impostor, real]));
        if (name == "CouchHUD") Assert.Equal("default", SkinCatalog.Resolve(name, [impostor, real]));
        // An ordinary name is still found by name.
        Assert.Equal("user:evil", SkinCatalog.Resolve("Plain Name", [Installed("evil", "Plain Name")]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("PS4 Classic")]
    [InlineData("gone")]
    [InlineData("user:gone")]
    [InlineData("Twin")]
    public void Nothing_or_an_ambiguous_name_finds_no_skin(string? asked)
    {
        var users = new[] { Installed("a", "Twin"), Installed("b", "twin") };
        Assert.Null(SkinCatalog.Resolve(asked, users));
    }

    [Fact]
    public void The_names_of_the_moved_skins_are_the_ones_the_ui_shows()
    {
        Assert.Equal("Neonstadt", SkinCatalog.BuiltinName("neoncity", "de"));
        Assert.Equal("Neon City", SkinCatalog.BuiltinName("neoncity", "en"));
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CouchHud.slnx"))) root = Path.GetDirectoryName(root)!;
        foreach (var lang in new[] { "de", "en" })
        {
            var core = File.ReadAllText(Path.Combine(root, "ui", "i18n", lang, "core.js"));
            foreach (var id in SkinCatalog.BuiltinIds.Concat(SkinCatalog.MovedToCatalogIds))
            {
                var ui = Regex.Match(core, $@"'skins\.name\.{id}': '([^']*)'").Groups[1].Value;
                Assert.Equal(ui, SkinCatalog.BuiltinName(id, lang));
            }
        }
        // The assistant and MCP go through the resolver, never a plain id comparison.
        var tools = File.ReadAllText(Path.Combine(root, "src", "CouchHud.Shell", "Services", "HudTools.cs"));
        Assert.Contains("SkinCatalog.Resolve(asked, hud.ListSkins())", tools);
        Assert.DoesNotContain("BuiltinSkins.Contains(", tools);
    }

    private static readonly Regex Brands = new(
        @"playstation|ps[1-5]|sony|xbox|microsoft|cyberpunk|cd ?projekt|night ?city|series ?x|dualshock|dualsense", RegexOptions.IgnoreCase);

    [Fact]
    public void Built_in_skins_name_no_third_party_brand()
    {
        foreach (var id in SkinCatalog.BuiltinIds.Concat(SkinCatalog.MovedToCatalogIds)) Assert.DoesNotMatch(Brands, id);

        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CouchHud.slnx"))) root = Path.GetDirectoryName(root)!;
        // Since 0.6 only the Couch skin is in the app: no other stylesheet and no list of other skins.
        var skins = Path.Combine(root, "ui", "skins");
        Assert.Equal(["index.js"], Directory.GetFiles(skins).Select(Path.GetFileName).ToArray());
        Assert.Empty(Directory.GetDirectories(skins));
        foreach (var file in Directory.GetFiles(skins))
        {
            Assert.DoesNotMatch(Brands, Path.GetFileName(file));
            var hit = Brands.Match(File.ReadAllText(file));
            Assert.False(hit.Success, $"{Path.GetFileName(file)}: \"{hit.Value}\"");
        }
        // The app's own stylesheet has no rules for the skins that left.
        var styles = File.ReadAllText(Path.Combine(root, "ui", "styles.css"));
        foreach (var id in SkinCatalog.MovedToCatalogIds) Assert.DoesNotContain($"data-skin=\"{id}\"", styles);

        // The UI texts of the skins and their sound sets, in every language.
        foreach (var lang in new[] { "de", "en" })
        {
            var core = File.ReadAllText(Path.Combine(root, "ui", "i18n", lang, "core.js"));
            var lines = Regex.Matches(core, @"^\s*'(?:skins|sound)\.[^']+':\s*'[^\n]*$", RegexOptions.Multiline).Select(m => m.Value).ToList();
            Assert.True(lines.Count >= 11, lang);
            foreach (var line in lines) Assert.DoesNotMatch(Brands, line);
        }
    }
}
