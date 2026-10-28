using CouchHud.Core.Plugins;
using CouchHud.Core.Skins;

namespace CouchHud.Core.Tests;

/// <summary>skin.json of an installed skin: names and descriptions per language, glyph labels, a sound set; all bounded and without markup.</summary>
[Collection("Language")]
public class SkinManifestTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("couchhud-manifest-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private SkinInfo Skin(string json, string language = "de", string folder = "x")
    {
        // A root of its own for every call, so a test may look at several skins one after the other.
        var root = Directory.CreateDirectory(Path.Combine(_dir, Guid.NewGuid().ToString("N"))).FullName;
        var dir = Directory.CreateDirectory(Path.Combine(root, folder)).FullName;
        File.WriteAllText(Path.Combine(dir, "skin.json"), json);
        File.WriteAllText(Path.Combine(dir, "skin.css"), "");
        return Assert.Single(SkinCatalog.ListUserSkins(root, language));
    }

    [Theory]
    [InlineData("de", "Sternenstaub", "Dunkel")]
    [InlineData("en", "Stardust", "Dark")]
    public void Name_and_description_are_shown_in_the_current_language(string language, string name, string description)
    {
        var skin = Skin("""{ "name": { "de": "Sternenstaub", "en": "Stardust" }, "description": { "de": "Dunkel", "en": "Dark" } }""", language);
        Assert.Equal(name, skin.Name);
        Assert.Equal(description, skin.Description);
        // A request can use either name.
        Assert.Equal(["Sternenstaub", "Stardust"], skin.Aliases);
    }

    [Fact]
    public void A_skin_with_one_language_or_a_plain_text_has_that_text_everywhere()
    {
        Assert.Equal("Only", Skin("""{ "name": { "en": "Only" } }""", "de").Name);
        Assert.Equal("Plain", Skin("""{ "name": "Plain" }""", "en", "plain").Name);
        Assert.Equal("Plain", Skin("""{ "name": "Plain" }""", "de", "plain2").Name);
    }

    [Fact]
    public void Without_a_usable_name_the_folder_names_the_skin()
    {
        foreach (var json in new[] { "{}", """{ "name": 5 }""", """{ "name": { "fr": "Non" } }""", """{ "name": "   " }""", """{ "name": "<>" }""", """{ "name": [ "a" ] }""" })
        {
            var skin = Skin(json, "de", "folderName");
            Assert.Equal("folderName", skin.Name);
        }
    }

    [Fact]
    public void Text_is_cut_and_cleaned_so_no_markup_and_no_wall_of_text_reaches_the_screen()
    {
        var skin = Skin("{ \"name\": \"<img src=x onerror=alert(1)>Evil\\u0007 \\n  Name\", \"description\": \"" + new string('x', 5000) + "\" }");
        Assert.DoesNotContain('<', skin.Name);
        Assert.DoesNotContain('>', skin.Name);
        Assert.DoesNotContain('\n', skin.Name);
        Assert.DoesNotContain('\a', skin.Name);
        Assert.Equal("img src=x onerror=alert(1)Evil Name", skin.Name);
        Assert.Equal(SkinCatalog.MaxDescriptionLength, skin.Description.Length);
        Assert.True(SkinCatalog.CleanText(new string('n', 500), SkinCatalog.MaxNameLength)!.Length == SkinCatalog.MaxNameLength);
    }

    [Fact]
    public void A_cut_never_splits_a_surrogate_pair()
    {
        var text = new string('a', 39) + "\U0001F600\U0001F600";
        var cut = SkinCatalog.CleanText(text, 40)!;
        Assert.Equal(39, cut.Length);
        Assert.True(cut.All(c => !char.IsSurrogate(c)));
    }

    [Fact]
    public void Glyph_labels_are_kept_for_the_menu_button_and_bounded()
    {
        var skin = Skin("""{ "glyphs": { "≡": "☰", "A": "<b>x</b>", "": "z", "LB": 5, "Y": "a very long label that goes on" } }""");
        Assert.Equal("☰", skin.Glyphs["≡"]);
        Assert.Equal("bxb", skin.Glyphs["A"].Replace("/", ""));
        Assert.False(skin.Glyphs.ContainsKey(""));
        Assert.False(skin.Glyphs.ContainsKey("LB"));
        Assert.InRange(skin.Glyphs["Y"].Length, 1, SkinCatalog.MaxGlyphLength);
        Assert.All(skin.Glyphs.Values, v => Assert.DoesNotContain('<', v));
    }

    [Fact]
    public void No_more_glyph_labels_than_the_limit()
    {
        var many = string.Join(",", Enumerable.Range(0, 200).Select(i => $"\"k{i}\": \"v\""));
        Assert.Equal(SkinCatalog.MaxGlyphs, Skin("{ \"glyphs\": { " + many + " } }").Glyphs.Count);
    }

    [Fact]
    public void A_sound_set_is_taken_only_when_it_is_one_the_app_has()
    {
        Assert.Equal("deepsea", Skin("""{ "soundSet": "deepsea" }""").SoundSet);
        Assert.Null(Skin("""{ "soundSet": "../../x" }""").SoundSet);
        Assert.Null(Skin("""{ "soundSet": 3 }""").SoundSet);
    }

    [Fact]
    public void An_oversized_or_nested_skin_json_is_skipped_not_fatal()
    {
        var big = Directory.CreateDirectory(Path.Combine(_dir, "big")).FullName;
        File.WriteAllText(Path.Combine(big, "skin.json"), "{ \"name\": \"" + new string('a', SkinCatalog.MaxManifestBytes + 10) + "\" }");
        File.WriteAllText(Path.Combine(big, "skin.css"), "");
        var deep = Directory.CreateDirectory(Path.Combine(_dir, "deep")).FullName;
        File.WriteAllText(Path.Combine(deep, "skin.json"), new string('[', 200) + new string(']', 200));
        File.WriteAllText(Path.Combine(deep, "skin.css"), "");
        var list = Directory.CreateDirectory(Path.Combine(_dir, "list")).FullName;
        File.WriteAllText(Path.Combine(list, "skin.json"), "[1,2]");
        File.WriteAllText(Path.Combine(list, "skin.css"), "");
        var fine = Directory.CreateDirectory(Path.Combine(_dir, "fine")).FullName;
        File.WriteAllText(Path.Combine(fine, "skin.json"), """{ "name": "Fine" }""");
        File.WriteAllText(Path.Combine(fine, "skin.css"), "");

        Assert.Equal("user:fine", Assert.Single(SkinCatalog.ListUserSkins(_dir)).Id);
    }

    [Theory]
    [InlineData("Neon‮noitcA", "NeonnoitcA")]        // right-to-left override
    [InlineData("⁦Name⁩", "Name")]             // bidi isolates
    [InlineData("Na​me‏﻿", "Name")]        // zero-width space, right-to-left mark, byte order mark
    [InlineData("Sky \U0001F600 Blue", "Sky \U0001F600 Blue")]   // a valid pair stays
    [InlineData("Lone\uD800 surrogate\uDC00", "Lone surrogate")]
    public void Invisible_format_characters_and_lone_surrogates_are_dropped(string raw, string clean) =>
        Assert.Equal(clean, SkinCatalog.CleanText(raw, 40));

    [Fact]
    public void Invisible_characters_are_dropped_from_names_descriptions_glyphs_aliases_and_catalog_names()
    {
        var skin = Skin("""{ "name": { "de": "Tief‮see", "en": "Deep​Sea" }, "description": "A⁦B⁩C", "glyphs": { "‮A": "​⁦X" } }""");
        Assert.Equal("Tiefsee", skin.Name);
        Assert.Equal("ABC", skin.Description);
        Assert.Equal(["Tiefsee", "DeepSea"], skin.Aliases);
        Assert.Equal("X", skin.Glyphs["A"]);

        var entry = CatalogParser.Parse("""{ "entries": [ { "id": "x", "names": { "de": "Na‮me​" }, "name": "n", "type": "skin", "version": "1.0.0" } ] }""", "https://example.com/i.json", "de").Single();
        Assert.Equal("Name", entry.Name);
        // Whatever is left of an all-invisible name is no name.
        Assert.Null(SkinCatalog.CleanText("‮​⁦", 40));
    }

    // ---- the catalog index

    [Fact]
    public void A_catalog_entry_may_bring_its_names_per_language_and_older_clients_still_read_name()
    {
        const string json = """
            { "entries": [ { "id": "stardust", "name": "Sternenstaub", "names": { "de": "Sternenstaub", "en": "Stardust" }, "type": "skin", "version": "1.0.0",
              "download": "https://example.com/s.zip", "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" },
              { "id": "plain", "name": "Plain", "type": "skin", "version": "1.0.0" },
              { "id": "obj", "name": { "en": "Only English" }, "type": "skin", "version": "1.0.0" },
              { "id": "evil", "names": { "de": "<script>Evil</script>" }, "name": "x", "type": "skin", "version": "1.0.0" } ] }
            """;
        var de = CatalogParser.Parse(json, "https://example.com/i.json", "de");
        var en = CatalogParser.Parse(json, "https://example.com/i.json", "en");

        Assert.Equal("Sternenstaub", de.First(e => e.Id == "stardust").Name);
        Assert.Equal("Stardust", en.First(e => e.Id == "stardust").Name);
        Assert.Equal("Plain", en.First(e => e.Id == "plain").Name);
        Assert.Equal("Only English", de.First(e => e.Id == "obj").Name);
        Assert.Equal("scriptEvil/script", de.First(e => e.Id == "evil").Name);
    }
}
