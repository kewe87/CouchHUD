using System.Net;
using System.Security.Cryptography;
using System.Text;
using CouchHud.Core.Plugins;

namespace CouchHud.Core.Tests;

/// <summary>
/// The plugin suggestion on a normal start: the signed catalog is available from the saved copy without opening the catalog page and without
/// any network traffic, a copy that does not verify is never used, and a plugin that is installed (loaded or waiting for a restart) is not
/// suggested. Throwaway keys only.
/// </summary>
[Collection("Language")]
public sealed class PluginSuggestionTimingTests : IDisposable
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private static readonly byte[] TrustKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-suggest-timing-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _official = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private int _requests;

    public PluginSuggestionTimingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _official.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static readonly string Index = "{ \"entries\": [ { \"id\": \"casehud\", \"folder\": \"casehud\", \"name\": \"CaseHUD\", \"type\": \"plugin\", \"version\": \"1.0.0\", \"download\": \"https://example.com/c.zip\", \"sha256\": \"" + Hash + "\", \"suggestWhen\": { \"uninstallEntry\": \"CaseHUD\" } } ] }";

    private sealed class Handler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request.RequestUri!));
    }

    private string Sign(string text) =>
        Convert.ToBase64String(_official.SignData(CatalogSignature.WithContext(CatalogSignature.CatalogContext, Encoding.UTF8.GetBytes(text)), HashAlgorithmName.SHA256));

    private CatalogService Service()
    {
        HttpResponseMessage Respond(Uri u)
        {
            Interlocked.Increment(ref _requests);
            return u.ToString() switch
            {
                CatalogDefaults.IndexUrl => new(HttpStatusCode.OK) { Content = new StringContent(Index) },
                CatalogDefaults.IndexUrl + ".sig" => new(HttpStatusCode.OK) { Content = new StringContent(Sign(Index)) },
                _ => new(HttpStatusCode.NotFound)
            };
        }
        return new CatalogService(new HttpClient(new Handler(Respond)), _dir, () => [CatalogDefaults.IndexUrl], () => false, "de", _official.ExportSubjectPublicKeyInfoPem());
    }

    private static FakeRegistry WithCaseHud()
    {
        var programs = new FakeRegistry();
        programs.Values[PluginSuggestions.UninstallRoot + @"\{1}"] = new() { ["DisplayName"] = "CaseHUD" };
        return programs;
    }

    private static FakeRegistry WithPrograms(string name)
    {
        var programs = new FakeRegistry();
        programs.Values[PluginSuggestions.UninstallRoot + @"\{2}"] = new() { ["DisplayName"] = name };
        return programs;
    }

    [Fact]
    public async Task Without_a_saved_copy_the_cache_only_load_has_nothing_and_asks_the_network_for_nothing()
    {
        var result = await Service().LoadAsync(cacheOnly: true);
        Assert.Empty(result.Entries);
        Assert.Equal(0, _requests);
    }

    [Fact]
    public async Task The_saved_signed_copy_gives_the_entries_for_the_suggestion_without_any_request()
    {
        await Service().LoadAsync();
        var before = _requests;
        var cached = await Service().LoadAsync(cacheOnly: true);
        Assert.Equal(before, _requests);
        var entry = Assert.Single(cached.Entries);
        Assert.False(entry.Unsigned);
        Assert.True(CatalogService.IsDefaultSource(entry.Source));
        var due = new PluginSuggestions(_dir, WithCaseHud(), "Anna").Due(cached.Entries, [], locked: false);
        Assert.Equal("casehud", Assert.Single(due).EntryId);
    }

    [Fact]
    public async Task A_saved_copy_that_was_tampered_with_is_not_used()
    {
        await Service().LoadAsync();
        var file = Path.Combine(_dir, "catalog-cache.json");
        var text = File.ReadAllText(file);
        // The index text changed (another plugin id), the signature kept: it no longer verifies.
        var forged = Convert.ToBase64String(Encoding.UTF8.GetBytes(Index.Replace("\"casehud\"", "\"evil\"")));
        var original = Convert.ToBase64String(Encoding.UTF8.GetBytes(Index));
        Assert.Contains(original, text);
        File.WriteAllText(file, text.Replace(original, forged));
        var result = await Service().LoadAsync(cacheOnly: true);
        Assert.Empty(result.Entries);
    }

    [Fact]
    public void A_plugin_that_is_installed_but_waits_for_its_restart_is_not_suggested()
    {
        var pluginsDir = Path.Combine(_dir, "plugins");
        Directory.CreateDirectory(Path.Combine(pluginsDir, "Plain.Folder"));
        File.WriteAllText(Path.Combine(pluginsDir, "Plain.Folder", "plugin.json"), "{\"id\":\"plainplugin\",\"name\":\"Plain\"}");
        var manager = new PluginManager([(pluginsDir, true)], _dir, _ => true) { Trust = new PluginTrust(_dir, TrustKey) };
        manager.Trust!.ClearRemoved("seed");
        Assert.DoesNotContain(manager.Plugins, p => p.Loaded);
        var entries = CatalogParser.Parse("[{\"id\":\"plainplugin\",\"name\":\"Plain\",\"type\":\"plugin\",\"version\":\"1.0.0\",\"download\":\"https://x.example/c.zip\",\"sha256\":\"" + Hash + "\",\"suggestWhen\":{\"uninstallEntry\":\"Plain\"}}]", "s");
        var suggestions = new PluginSuggestions(_dir, WithPrograms("Plain"), "Anna");
        // The folder is there: the host hands its ids to Due as installed.
        Assert.Contains("plainplugin", manager.InstalledPluginIds());
        Assert.Empty(suggestions.Due(entries, manager.InstalledPluginIds().ToList(), locked: false));
        // Marked for removal: the suggestion comes back.
        manager.Trust.MarkRemoved("Plain.Folder");
        Assert.Single(suggestions.Due(entries, manager.InstalledPluginIds().ToList(), locked: false));
    }

    [Fact]
    public void The_host_prepares_the_catalog_before_the_offer_and_keeps_every_rule()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "CouchHud.slnx"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        var hud = File.ReadAllText(Path.Combine(root!, "src", "CouchHud.Shell", "Hud.cs"));
        var at = hud.IndexOf("public async Task<object> GetPluginSuggestionsAsync()", StringComparison.Ordinal);
        Assert.True(at >= 0);
        var body = hud[at..hud.IndexOf("public void DismissPluginSuggestion", StringComparison.Ordinal)];
        // Locked: nothing, before anything is loaded; then the catalog is prepared; installed means a folder (not only loaded).
        Assert.True(body.IndexOf("if (Locked)", StringComparison.Ordinal) < body.IndexOf("PrepareSuggestionsAsync", StringComparison.Ordinal));
        Assert.Contains("plugins.InstalledPluginIds()", body);
        Assert.DoesNotContain("p.Loaded", body);
        Assert.DoesNotContain("MarkOffered", body);
        var bridge = File.ReadAllText(Path.Combine(root!, "src", "CouchHud.Shell", "UiHost", "UiBridge.cs"));
        Assert.Contains("return await hud.GetPluginSuggestionsAsync();", bridge);
    }
}
