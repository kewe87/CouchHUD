using System.Text.Json;
using System.Text.RegularExpressions;
using CouchHud.Core.Config;
using CouchHud.Core.Skins;

namespace CouchHud.Core.Tests;

/// <summary>0.5 renamed the built-in skins that carried third-party names: old ids move to the new ones once, nothing else changes.</summary>
public class SkinRenameTests
{
    [Theory]
    [InlineData("ps4", "deepsea")]
    [InlineData("xbox", "greenlight")]
    [InlineData("cyberpunk", "neoncity")]
    public void An_old_id_maps_to_the_new_one(string old, string now)
    {
        Assert.Equal(now, SkinCatalog.MigrateId(old));
        Assert.Contains(now, SkinCatalog.BuiltinIds);
        Assert.DoesNotContain(old, SkinCatalog.BuiltinIds);
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
    public void Any_other_id_stays_as_it_is(string? id) => Assert.Equal(id, SkinCatalog.MigrateId(id));

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

        Assert.Equal("deepsea", read.Skin);
        Assert.Equal("neoncity", read.SoundSet);
        Assert.Equal("greenlight", read.Profile(ben.Id).Skin);
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
    public void New_and_unchanged_ids_survive_a_read()
    {
        var c = ConfigStore.Parse("""{ "skin": "user:xbox", "soundSet": "" }""")!;
        Assert.Equal("user:xbox", c.Skin);
        Assert.Equal("", c.SoundSet);
        Assert.Equal("nova", ConfigStore.Parse("""{ "skin": "nova" }""")!.Skin);
    }

    [Fact]
    public void Old_ids_are_no_skins_of_their_own_and_a_user_skin_keeps_its_prefix()
    {
        Assert.False(SkinCatalog.IsKnown("ps4", []));
        Assert.True(SkinCatalog.IsKnown("deepsea", []));

        // A user skin folder named like a built-in skin stays a user skin ("user:<folder>"), as before.
        var dir = Directory.CreateTempSubdirectory("couchhud-skins-").FullName;
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(dir, "deepsea")).FullName;
            File.WriteAllText(Path.Combine(folder, "skin.json"), """{ "name": "My deep sea" }""");
            File.WriteAllText(Path.Combine(folder, "skin.css"), ":root[data-skin=\"user:deepsea\"] { --accent: teal; }");
            var skin = Assert.Single(SkinCatalog.ListUserSkins(dir));
            Assert.Equal("user:deepsea", skin.Id);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("xbox", "greenlight")]
    [InlineData("ps4", "deepsea")]
    [InlineData("cyberpunk", "neoncity")]
    [InlineData("XBOX", "greenlight")]
    [InlineData("neoncity", "neoncity")]
    [InlineData("NeonCity", "neoncity")]
    [InlineData("Neonstadt", "neoncity")]
    [InlineData("neon city", "neoncity")]
    [InlineData("Tiefsee", "deepsea")]
    [InlineData("  deep sea ", "deepsea")]
    [InlineData("grünlicht", "greenlight")]
    [InlineData("Greenlight", "greenlight")]
    [InlineData("couchhud", "default")]
    [InlineData("Nova", "nova")]
    [InlineData("user:neon", "user:neon")]
    [InlineData("Synthwave", "user:neon")]
    public void The_assistant_finds_a_skin_by_id_old_id_or_name(string asked, string id)
    {
        var users = new[] { new SkinInfo("user:neon", "Synthwave", "", new Dictionary<string, string>(), "file:///x") };
        Assert.Equal(id, SkinCatalog.Resolve(asked, users));
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
        var users = new[]
        {
            new SkinInfo("user:a", "Twin", "", new Dictionary<string, string>(), "file:///a"),
            new SkinInfo("user:b", "twin", "", new Dictionary<string, string>(), "file:///b"),
        };
        Assert.Null(SkinCatalog.Resolve(asked, users));
    }

    [Fact]
    public void Built_in_names_are_the_ones_the_ui_shows()
    {
        Assert.Equal("Neonstadt", SkinCatalog.BuiltinName("neoncity", "de"));
        Assert.Equal("Neon City", SkinCatalog.BuiltinName("neoncity", "en"));
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CouchHud.slnx"))) root = Path.GetDirectoryName(root)!;
        foreach (var lang in new[] { "de", "en" })
        {
            var core = File.ReadAllText(Path.Combine(root, "ui", "i18n", lang, "core.js"));
            foreach (var id in SkinCatalog.BuiltinIds)
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
        foreach (var id in SkinCatalog.BuiltinIds) Assert.DoesNotMatch(Brands, id);

        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CouchHud.slnx"))) root = Path.GetDirectoryName(root)!;
        var skins = Path.Combine(root, "ui", "skins");
        var files = Directory.GetFiles(skins, "*.css").Append(Path.Combine(skins, "index.js")).ToList();
        Assert.True(files.Count >= 5);
        foreach (var file in files)
        {
            Assert.DoesNotMatch(Brands, Path.GetFileName(file));
            var hit = Brands.Match(File.ReadAllText(file));
            Assert.False(hit.Success, $"{Path.GetFileName(file)}: \"{hit.Value}\"");
        }

        // The UI texts of the skins and their sound sets, in every language.
        foreach (var lang in new[] { "de", "en" })
        {
            var core = File.ReadAllText(Path.Combine(root, "ui", "i18n", lang, "core.js"));
            var lines = Regex.Matches(core, @"^\s*'(?:skins|sound)\.[^']+':\s*'[^\n]*$", RegexOptions.Multiline).Select(m => m.Value).ToList();
            Assert.True(lines.Count >= 15, lang);
            foreach (var line in lines) Assert.DoesNotMatch(Brands, line);
        }
    }
}
