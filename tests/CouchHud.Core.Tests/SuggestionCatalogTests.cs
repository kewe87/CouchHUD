using System.Net;
using System.Security.Cryptography;
using System.Text;
using CouchHud.Core.Plugins;

namespace CouchHud.Core.Tests;

/// <summary>
/// What the host prepares before the first plugin offer (<see cref="SuggestionCatalog"/>): the saved copy first, one fetch only when the saved
/// copy holds nothing of CouchHUD's own index, never when fetching is not allowed, a failed or foreign-only result is not kept, a copy that does
/// not verify is never used, runs do not overlap. Throwaway keys only.
/// </summary>
[Collection("Language")]
public sealed class SuggestionCatalogTests : IDisposable
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Extra = "https://plugins.example.org/index.json";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-suggest-cat-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _official = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private int _requests;
    private bool _officialUp = true;

    public SuggestionCatalogTests() => Directory.CreateDirectory(_dir);

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

    private static string Index(string id) =>
        "{ \"entries\": [ { \"id\": \"" + id + "\", \"folder\": \"" + id + "\", \"name\": \"" + id + "\", \"type\": \"plugin\", \"version\": \"1.0.0\", \"download\": \"https://example.com/c.zip\", \"sha256\": \"" + Hash + "\" } ] }";

    private sealed class Handler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request.RequestUri!));
    }

    private string Sign(string text) =>
        Convert.ToBase64String(_official.SignData(CatalogSignature.WithContext(CatalogSignature.CatalogContext, Encoding.UTF8.GetBytes(text)), HashAlgorithmName.SHA256));

    private CatalogService Service(bool withExtra = true)
    {
        HttpResponseMessage Respond(Uri u)
        {
            Interlocked.Increment(ref _requests);
            var text = u.ToString();
            if (text.StartsWith(Extra, StringComparison.Ordinal))
                return text.EndsWith(".sig", StringComparison.Ordinal) ? new(HttpStatusCode.NotFound) : new(HttpStatusCode.OK) { Content = new StringContent(Index("other")) };
            if (!_officialUp) throw new HttpRequestException("offline");
            return text switch
            {
                CatalogDefaults.IndexUrl => new(HttpStatusCode.OK) { Content = new StringContent(Index("casehud")) },
                CatalogDefaults.IndexUrl + ".sig" => new(HttpStatusCode.OK) { Content = new StringContent(Sign(Index("casehud"))) },
                _ => new(HttpStatusCode.NotFound)
            };
        }
        IReadOnlyList<CatalogSourceEntry> extras = withExtra ? [new CatalogSourceEntry { Url = Extra }] : [];
        return new CatalogService(new HttpClient(new Handler(Respond)), _dir, () => [CatalogDefaults.IndexUrl], () => false, "de", _official.ExportSubjectPublicKeyInfoPem(), null, () => extras);
    }

    [Fact]
    public async Task Only_entries_of_a_catalog_the_user_added_in_the_saved_copy_do_not_stop_the_single_fetch()
    {
        _officialUp = false;
        await Service().LoadAsync();   // the official source is down: only the user's catalog is saved
        _officialUp = true;
        var suggest = new SuggestionCatalog(Service(), () => true);
        await suggest.PrepareAsync(() => false);
        Assert.Contains(suggest.Entries, e => e.Id == "casehud" && !e.Unsigned);
    }

    [Fact]
    public async Task Third_party_entries_alone_never_become_suggestion_entries()
    {
        _officialUp = false;
        await Service().LoadAsync();
        var suggest = new SuggestionCatalog(Service(), () => false);
        await suggest.PrepareAsync(() => false);
        Assert.Empty(suggest.Entries);
    }

    [Fact]
    public async Task Offline_without_a_copy_keeps_nothing_and_a_later_try_works()
    {
        _officialUp = false;
        var suggest = new SuggestionCatalog(Service(false), () => true);
        await suggest.PrepareAsync(() => false);
        Assert.Empty(suggest.Entries);
        _officialUp = true;
        await suggest.PrepareAsync(() => false);
        Assert.Single(suggest.Entries, e => e.Id == "casehud");
    }

    [Fact]
    public async Task With_fetching_not_allowed_there_is_no_request_at_all_without_a_copy()
    {
        var suggest = new SuggestionCatalog(Service(), () => false);
        await suggest.PrepareAsync(() => false);
        Assert.Empty(suggest.Entries);
        Assert.Equal(0, _requests);
    }

    [Fact]
    public async Task The_publish_callback_fires_once_per_prepared_result_and_never_for_a_third_party_only_list()
    {
        var published = 0;
        _officialUp = false;
        await Service().LoadAsync();   // only the user's catalog is saved
        var foreign = new SuggestionCatalog(Service(), () => false, () => published++);
        await foreign.PrepareAsync(() => false);
        Assert.Equal(0, published);

        _officialUp = true;
        var suggest = new SuggestionCatalog(Service(), () => true, () => published++);
        await suggest.PrepareAsync(() => false);
        Assert.Equal(1, published);
        // Prepared already: asking again publishes nothing more.
        await suggest.PrepareAsync(() => false);
        await Task.WhenAll(suggest.PrepareAsync(() => false), suggest.PrepareAsync(() => false));
        Assert.Equal(1, published);
        // After an invalidation the next prepared result is a new one.
        suggest.Invalidate();
        await suggest.PrepareAsync(() => false);
        Assert.Equal(2, published);
    }

    [Fact]
    public async Task HasOfficial_is_false_for_a_third_party_only_result_and_true_with_an_official_entry()
    {
        _officialUp = false;
        Assert.False(SuggestionCatalog.HasOfficial(await Service().LoadAsync()));
        _officialUp = true;
        Assert.True(SuggestionCatalog.HasOfficial(await Service().LoadAsync()));
    }

    [Fact]
    public async Task The_saved_copy_is_used_without_a_request_even_when_fetching_is_not_allowed()
    {
        await Service().LoadAsync();
        var before = _requests;
        var suggest = new SuggestionCatalog(Service(), () => false);
        await suggest.PrepareAsync(() => false);
        Assert.Equal(before, _requests);
        Assert.Single(suggest.Entries, e => e.Id == "casehud");
    }

    [Fact]
    public async Task The_list_of_the_catalog_page_wins_and_invalidating_drops_what_was_prepared()
    {
        var suggest = new SuggestionCatalog(Service(), () => true);
        await suggest.PrepareAsync(() => true);
        Assert.Empty(suggest.Entries);
        Assert.Equal(0, _requests);
        await suggest.PrepareAsync(() => false);
        Assert.NotEmpty(suggest.Entries);
        suggest.Invalidate();
        Assert.Empty(suggest.Entries);
    }

    [Fact]
    public async Task A_tampered_saved_copy_never_reaches_the_entries_and_runs_do_not_overlap()
    {
        await Service(false).LoadAsync();
        var file = Path.Combine(_dir, "catalog-cache.json");
        var original = Convert.ToBase64String(Encoding.UTF8.GetBytes(Index("casehud")));
        var forged = Convert.ToBase64String(Encoding.UTF8.GetBytes(Index("evil")));
        File.WriteAllText(file, File.ReadAllText(file).Replace(original, forged));
        var suggest = new SuggestionCatalog(Service(false), () => false);
        await Task.WhenAll(suggest.PrepareAsync(() => false), suggest.PrepareAsync(() => false));
        Assert.DoesNotContain(suggest.Entries, e => e.Id == "evil");
        // Fetching allowed: one run fetches (index and signature), the others find the result and ask for nothing.
        var before = _requests;
        var fetching = new SuggestionCatalog(Service(false), () => true);
        await Task.WhenAll(fetching.PrepareAsync(() => false), fetching.PrepareAsync(() => false), fetching.PrepareAsync(() => false));
        Assert.Single(fetching.Entries, e => e.Id == "casehud");
        Assert.True(_requests - before <= 2, $"requests: {_requests - before}");
    }
}
