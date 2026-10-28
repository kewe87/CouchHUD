using System.Net;
using System.Text;
using System.Text.Json;
using CouchHud.Core.Integrations;
using CouchHud.Core.Library;
using CouchHud.Core.Localization;
using CouchHud.Core.Plugins;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>Texts that are stored, cached or compared must follow the language switch (QA round 4).</summary>
// Switches the global language: runs alone, never next to tests that read German texts.
[Collection("Language")]
public class LanguageFixTests
{
    private sealed class Handler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(respond(request.RequestUri!));
        }
    }

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    // ---- notifications keep key and arguments

    [Fact]
    public void A_stored_text_reads_in_the_language_of_the_moment()
    {
        var stored = Strings.Ref("hud.played", "5 min", "2 h");
        Assert.Equal(Strings.GetIn("de", "hud.played", "5 min", "2 h"), Strings.RenderIn("de", stored));
        Assert.Equal(Strings.GetIn("en", "hud.played", "5 min", "2 h"), Strings.RenderIn("en", stored));
        Assert.NotEqual(Strings.RenderIn("de", stored), Strings.RenderIn("en", stored));
    }

    [Fact]
    public void Arguments_can_be_references_and_plurals_work()
    {
        var stored = Strings.Ref("hud.played", Strings.Ref("time.minutes", 7), Strings.Ref("time.hoursMinutes", 1, 30));
        Assert.Equal(Strings.GetIn("en", "hud.played", Strings.GetIn("en", "time.minutes", 7), Strings.GetIn("en", "time.hoursMinutes", 1, 30)), Strings.RenderIn("en", stored));
        var plural = Strings.RefPlural("hud.updatesAvailable", 1);
        Assert.Equal(Strings.PluralIn("de", "hud.updatesAvailable", 1), Strings.RenderIn("de", plural));
        Assert.Equal(Strings.PluralIn("en", "hud.updatesAvailable", 3), Strings.RenderIn("en", Strings.RefPlural("hud.updatesAvailable", 3)));
    }

    [Fact]
    public void Plain_text_comes_back_unchanged()
    {
        Assert.Equal("WhatsApp: Hallo", Strings.RenderIn("en", "WhatsApp: Hallo"));
        Assert.Equal("", Strings.RenderIn("en", ""));
    }

    [Fact]
    public void A_name_from_outside_that_looks_like_a_reference_stays_text()
    {
        // A game name from a file could be made to look like a stored reference to another text.
        var forged = "\u0001{\"K\":\"hud.configFault\",\"N\":null,\"A\":[]}";
        var stored = Strings.Ref("downloads.installed", forged);
        var shown = Strings.RenderIn("en", stored);
        Assert.Equal(Strings.GetIn("en", "downloads.installed", forged), shown);
        Assert.DoesNotContain(Strings.GetIn("en", "hud.configFault"), shown);
        // Not as a whole text either: only strings made by Ref are references.
        Assert.Equal(forged, Strings.RenderIn("en", forged));
        // A real reference as an argument still renders.
        Assert.Equal(Strings.GetIn("en", "downloads.installed", Strings.GetIn("en", "time.minutes", 3)),
            Strings.RenderIn("en", Strings.Ref("downloads.installed", Strings.Ref("time.minutes", 3))));
    }

    // ---- default news feeds follow the language until the user changes them

    [Fact]
    public void Default_feeds_follow_the_language_but_a_changed_list_stays()
    {
        var german = NewsService.Defaults.Select(f => new NewsFeed { Name = f.Name, Url = f.Url }).ToList();
        Assert.Equal(NewsService.Defaults.Select(f => f.Url), NewsService.Effective(german, "de").Select(f => f.Url));
        Assert.Equal(NewsService.DefaultsEnglish.Select(f => f.Url), NewsService.Effective(german, "en").Select(f => f.Url));

        german.Add(new NewsFeed { Name = "Mine", Url = "https://example.com/feed" });
        Assert.Equal(3, NewsService.Effective(german, "en").Count);
        german.RemoveAt(0);
        Assert.Equal(2, NewsService.Effective(german, "en").Count);
        Assert.Contains(NewsService.Effective(german, "en"), f => f.Url == "https://example.com/feed");
    }

    // ---- store description and weather search ask in the active language

    [Fact]
    public async Task Store_info_is_fetched_and_cached_per_language()
    {
        var handler = new Handler(u =>
        {
            if (u.ToString().Contains("storesearch")) return Json("""{ "items": [ { "id": 1145360, "name": "Hades" } ] }""");
            return Json(u.ToString().Contains("l=english")
                ? """{ "1145360": { "success": true, "data": { "short_description": "Defy the god of the dead." } } }"""
                : """{ "1145360": { "success": true, "data": { "short_description": "Trotze dem Gott der Toten." } } }""");
        });
        var cache = Path.Combine(Directory.CreateTempSubdirectory("couchhud-lang-").FullName, "store.json");
        var language = "de";
        var service = new StoreInfoService(new HttpClient(handler), cache, () => language);
        var item = new LibraryItem { Id = "epic:hades", Source = "epic", Name = "Hades", Launch = new LaunchTarget(LaunchKind.Url, "x://") };

        Assert.Equal("Trotze dem Gott der Toten.", (await service.GetAsync(item))!.Description);
        language = "en";
        Assert.Equal("Defy the god of the dead.", (await service.GetAsync(item))!.Description);
        Assert.Contains(handler.Urls, u => u.Contains("l=english"));
        Assert.Contains(handler.Urls, u => u.Contains("l=german"));

        // Both are cached now: no further request in either language.
        var calls = handler.Urls.Count;
        Assert.Equal("Defy the god of the dead.", (await service.GetAsync(item))!.Description);
        language = "de";
        Assert.Equal("Trotze dem Gott der Toten.", (await service.GetAsync(item))!.Description);
        Assert.Equal(calls, handler.Urls.Count);
    }

    [Fact]
    public async Task Place_search_uses_the_active_language()
    {
        var handler = new Handler(_ => Json("""{ "results": [] }"""));
        Strings.Use("en");
        try
        {
            await new WeatherService(new HttpClient(handler)).SearchAsync("Berlin");
        }
        finally
        {
            Strings.Use("de");
        }
        Assert.Contains("language=en", handler.Urls.Single());
    }

    // ---- catalog: language of the descriptions and error codes

    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Index = """{ "entries": [ { "id": "wand", "name": "Wand", "type": "plugin", "version": "1.0.0", "description": { "de": "Beschreibung", "en": "Description" }, "download": "https://example.com/wand.zip", "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" } ] }""";

    [Fact]
    public async Task Catalog_descriptions_follow_the_language_of_the_moment()
    {
        var dir = Directory.CreateTempSubdirectory("couchhud-cat-").FullName;
        var handler = new Handler(u => u.AbsolutePath.EndsWith(".sig") ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Index) });
        var language = "de";
        var service = new CatalogService(new HttpClient(handler), dir, () => ["https://example.com/index.json"], () => false, "de", null, () => language);
        Assert.Equal("Beschreibung", (await service.LoadAsync()).Entries.Single().Description);
        language = "en";
        Assert.Equal("Description", (await service.LoadAsync()).Entries.Single().Description);
        Assert.Contains(Hash, Index);
    }

    [Fact]
    public async Task A_missing_catalog_is_not_found_not_an_internet_problem()
    {
        var dir = Directory.CreateTempSubdirectory("couchhud-cat-").FullName;
        var service = new CatalogService(new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))), dir, () => ["https://example.com/index.json"], () => false);
        var result = await service.LoadAsync();
        Assert.Equal("notFound", result.ErrorCode);
        Assert.NotNull(result.Error);

        var offline = new CatalogService(new HttpClient(new Handler(_ => throw new HttpRequestException("offline"))), dir, () => ["https://example.com/index.json"], () => false);
        Assert.Equal("unreachable", (await offline.LoadAsync()).ErrorCode);
    }

    // ---- plugin.json: description as text or per language

    [Theory]
    [InlineData("""{ "name": "A", "description": "Nur Text" }""", "de", "Nur Text")]
    [InlineData("""{ "name": "A", "description": "Nur Text" }""", "en", "Nur Text")]
    [InlineData("""{ "name": "A", "description": { "de": "Hallo", "en": "Hello" } }""", "de", "Hallo")]
    [InlineData("""{ "name": "A", "description": { "de": "Hallo", "en": "Hello" } }""", "en", "Hello")]
    [InlineData("""{ "name": "A", "description": { "de": "Hallo" } }""", "en", "Hallo")]
    [InlineData("""{ "name": "A" }""", "en", null)]
    public void The_manifest_description_is_a_text_or_one_per_language(string json, string language, string? expected)
    {
        var manifest = JsonSerializer.Deserialize<PluginManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(expected, manifest.Description?.For(language));
    }

    [Fact]
    public void The_bundled_wand_manifest_has_both_languages()
    {
        var dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "plugins", "CouchHud.Plugin.Wand"));
        var manifest = PluginManager.ReadManifest(dir);
        Assert.NotNull(manifest);
        Assert.Contains("Cheats", manifest!.Description!.For("en"));
        Assert.NotEqual(manifest.Description.For("de"), manifest.Description.For("en"));
    }

    // ---- SDK: older hosts and plugins keep working

    private sealed class MinimalContext : IPluginContext
    {
        public string DataDirectory => "";
        public IPluginSettings Settings => null!;
        public IPluginEvents Events => new MinimalEvents();
        public void RegisterLibraryProvider(ILibraryProvider provider) { }
        public void Log(string message) { }
        public void RegisterSettings(params PluginSetting[] settings) { }
        public void RegisterPage(PluginPage page) { }
        public void RegisterQuickAction(QuickAction action) { }
        public void RegisterTool(PluginTool tool) { }
        public void Notify(string title, string body) { }
    }

    private sealed class MinimalEvents : IPluginEvents
    {
        public event Action<GameEvent>? GameStarted { add { } remove { } }
        public event Action<GameEvent>? GameEnded { add { } remove { } }
        public event Action? PcModeEntered { add { } remove { } }
        public event Action? CouchModeEntered { add { } remove { } }
    }

    [Fact]
    public void A_host_without_the_language_members_answers_german_and_ignores_the_event()
    {
        IPluginContext context = new MinimalContext();
        Assert.Equal("de", context.Language);
        context.Events.LanguageChanged += _ => { };   // compiles and does nothing
    }

    [Fact]
    public async Task The_host_raises_language_changes_to_plugins()
    {
        var events = new PluginEvents();
        var seen = new TaskCompletionSource<string>();
        events.LanguageChanged += l => seen.TrySetResult(l);
        events.RaiseLanguageChanged("en");
        Assert.Equal("en", await seen.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}

/// <summary>Tests that change the global language run alone, so no other test sees English by accident.</summary>
[CollectionDefinition("Language", DisableParallelization = true)]
public sealed class LanguageCollection;
