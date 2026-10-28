using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CouchHud.Core.Plugins;

namespace CouchHud.Core.Tests;

/// <summary>
/// Replay protection of the catalog index: the signed index carries a "serial" that rises with every change. A fetched index with a lower
/// serial than the saved copy of the same address is refused (the saved copy stays, so a revocation list cannot be rolled back by serving an
/// old signed file), an equal or higher one is accepted, a missing serial counts as 0, and every catalog address has its own counter.
/// Throwaway keys only.
/// </summary>
[Collection("Language")]
public sealed class CatalogSerialTests : IDisposable
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string ExtraA = "https://a.example.org/index.json";
    private const string ExtraB = "https://b.example.org/index.json";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-serial-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _official = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    // What the fake network serves right now, per address.
    private readonly Dictionary<string, (string Text, string? Signature)> _served = [];

    public CatalogSerialTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _official.Dispose();
        _publisher.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string Entry(string id) =>
        $$"""{ "id": "{{id}}", "folder": "{{id}}", "name": "{{id}}", "type": "plugin", "version": "1.0.0", "download": "https://example.com/{{id}}.zip", "sha256": "{{Hash}}" }""";

    private static string Index(string? serial, params string[] ids) =>
        "{ " + (serial is null ? "" : $"\"serial\": {serial}, ") + "\"entries\": [" + string.Join(",", ids.Select(Entry)) + "] }";

    private static string Sign(ECDsa key, string text) =>
        Convert.ToBase64String(key.SignData(CatalogSignature.WithContext(CatalogSignature.CatalogContext, Encoding.UTF8.GetBytes(text)), HashAlgorithmName.SHA256));

    private void ServeOfficial(string text) => _served[CatalogDefaults.IndexUrl] = (text, Sign(_official, text));

    private void ServePinned(string url, string text) => _served[url] = (text, Sign(_publisher, text));

    private CatalogService Service(params CatalogSourceEntry[] extras)
    {
        HttpResponseMessage Respond(Uri u)
        {
            var url = u.ToString();
            if (_served.TryGetValue(url, out var page)) return new(HttpStatusCode.OK) { Content = new StringContent(page.Text) };
            if (url.EndsWith(".sig", StringComparison.Ordinal) && _served.TryGetValue(url[..^4], out var signed) && signed.Signature is not null)
                return new(HttpStatusCode.OK) { Content = new StringContent(signed.Signature) };
            return new(HttpStatusCode.NotFound);
        }
        return new CatalogService(new HttpClient(new FakeHandler(Respond)), _dir, () => [CatalogDefaults.IndexUrl], () => false, "de",
            _official.ExportSubjectPublicKeyInfoPem(), null, () => extras);
    }

    private sealed class FakeHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request.RequestUri!));
    }

    private CatalogSourceEntry Pinned(string url) => new() { Url = url, PublisherKey = _publisher.ExportSubjectPublicKeyInfoPem() };

    /// <summary>The serial of the saved copy of an address (what the next load compares with).</summary>
    private long SavedSerial(string url)
    {
        var cache = JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, "catalog-cache.json")))!.AsObject();
        return CatalogParser.ReadSerial(Encoding.UTF8.GetString(Convert.FromBase64String(cache[url]!["Data"]!.GetValue<string>())));
    }

    private static string Older(long served, long saved) => Localization.Strings.Get("catalog.older", served, saved);

    // ---- the official index

    [Fact]
    public async Task A_lower_serial_is_refused_and_the_saved_copy_stays()
    {
        ServeOfficial(Index("5", "new"));
        Assert.Contains((await Service().LoadAsync()).Entries, e => e.Id == "new");

        // Somebody serves an older, correctly signed index (for example one without the revocations of today).
        ServeOfficial(Index("3", "old"));
        var result = await Service().LoadAsync();

        Assert.DoesNotContain(result.Entries, e => e.Id == "old");
        Assert.Contains(result.Entries, e => e.Id == "new");
        Assert.True(result.FromCache);
        Assert.Contains(Older(3, 5), result.Error);
        Assert.Equal(5, SavedSerial(CatalogDefaults.IndexUrl));
    }

    [Fact]
    public async Task A_refused_old_index_does_not_bring_back_a_revoked_plugin()
    {
        var revoking = """{ "serial": 4, "entries": [], "revoked": [ { "id": "evil", "reason": "bad" } ] }""";
        var before = """{ "serial": 3, "entries": [] }""";
        var service = Service();
        ServeOfficial(revoking);
        await service.LoadAsync();
        Assert.Single(service.CachedRevocations());

        ServeOfficial(before);
        await service.LoadAsync();

        Assert.Single(service.CachedRevocations());
    }

    [Fact]
    public async Task An_equal_or_higher_serial_is_accepted()
    {
        ServeOfficial(Index("5", "first"));
        await Service().LoadAsync();

        ServeOfficial(Index("5", "same"));
        var equal = await Service().LoadAsync();
        Assert.Contains(equal.Entries, e => e.Id == "same");
        Assert.False(equal.FromCache);

        ServeOfficial(Index("6", "higher"));
        var higher = await Service().LoadAsync();
        Assert.Contains(higher.Entries, e => e.Id == "higher");
        Assert.Equal(6, SavedSerial(CatalogDefaults.IndexUrl));
    }

    [Fact]
    public async Task A_missing_serial_counts_as_zero()
    {
        // No serial after a saved one: refused. Both without: accepted (an index of 0.5 times). Without, then 1: accepted.
        ServeOfficial(Index("2", "numbered"));
        await Service().LoadAsync();
        ServeOfficial(Index(null, "plain"));
        var refused = await Service().LoadAsync();
        Assert.DoesNotContain(refused.Entries, e => e.Id == "plain");
        Assert.Contains(Older(0, 2), refused.Error);

        File.Delete(Path.Combine(_dir, "catalog-cache.json"));
        ServeOfficial(Index(null, "plain"));
        await Service().LoadAsync();
        ServeOfficial(Index(null, "plain2"));
        Assert.Contains((await Service().LoadAsync()).Entries, e => e.Id == "plain2");
        ServeOfficial(Index("1", "numbered"));
        Assert.Contains((await Service().LoadAsync()).Entries, e => e.Id == "numbered");
    }

    [Theory]
    [InlineData("-3")]
    [InlineData("1.5")]
    [InlineData("\"7\"")]
    [InlineData("null")]
    [InlineData("99999999999999999999999")]
    public void A_serial_that_is_not_a_whole_number_counts_as_zero(string value)
    {
        Assert.Equal(0, CatalogParser.ReadSerial($$"""{ "serial": {{value}}, "entries": [] }"""));
        Assert.Equal(0, CatalogParser.ParseIndex($$"""{ "serial": {{value}}, "entries": [] }""", CatalogDefaults.IndexUrl).Serial);
    }

    [Fact]
    public void The_serial_is_read_from_the_index_and_broken_json_has_none()
    {
        Assert.Equal(42, CatalogParser.ParseIndex(Index("42", "x"), CatalogDefaults.IndexUrl).Serial);
        Assert.Equal(0, CatalogParser.ReadSerial("not json"));
        Assert.Equal(0, CatalogParser.ReadSerial("[1,2]"));
    }

    [Fact]
    public async Task A_first_load_has_nothing_to_compare_with()
    {
        ServeOfficial(Index("1", "first"));
        Assert.Contains((await Service().LoadAsync()).Entries, e => e.Id == "first");
    }

    [Fact]
    public async Task A_saved_copy_that_does_not_verify_cannot_block_a_good_index()
    {
        ServeOfficial(Index("5", "real"));
        await Service().LoadAsync();

        // Somebody puts a copy with a huge serial (and no valid signature) into the cache: it must not lock out the real index.
        var file = Path.Combine(_dir, "catalog-cache.json");
        var cache = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        cache[CatalogDefaults.IndexUrl]!["Data"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(Index("999999", "forged")));
        File.WriteAllText(file, cache.ToJsonString());

        ServeOfficial(Index("6", "real2"));
        var result = await Service().LoadAsync();
        Assert.Contains(result.Entries, e => e.Id == "real2");
        Assert.False(result.FromCache);
    }

    [Fact]
    public async Task An_index_with_a_wrong_signature_is_still_refused_whatever_its_serial()
    {
        ServeOfficial(Index("5", "real"));
        await Service().LoadAsync();

        _served[CatalogDefaults.IndexUrl] = (Index("100", "forged"), Sign(_publisher, Index("100", "forged")));
        var result = await Service().LoadAsync();
        Assert.DoesNotContain(result.Entries, e => e.Id == "forged");
        Assert.Contains(result.Entries, e => e.Id == "real");
    }

    // ---- catalogs of other developers: one counter per address

    [Fact]
    public async Task Every_catalog_address_has_its_own_serial()
    {
        ServeOfficial(Index("50", "official"));
        ServePinned(ExtraA, Index("10", "a1"));
        ServePinned(ExtraB, Index("2", "b1"));
        var extras = new[] { Pinned(ExtraA), Pinned(ExtraB) };
        await Service(extras).LoadAsync();

        // A goes back (refused, A's copy stays), B rises although its number is far below A's and the official one.
        ServePinned(ExtraA, Index("9", "a0"));
        ServePinned(ExtraB, Index("3", "b2"));
        var result = await Service(extras).LoadAsync();

        Assert.Contains(result.Entries, e => e.Id == "a1");
        Assert.DoesNotContain(result.Entries, e => e.Id == "a0");
        Assert.Contains(result.Entries, e => e.Id == "b2");
        Assert.Contains(result.Entries, e => e.Id == "official");
        Assert.Contains(Older(9, 10), result.Error);
        Assert.Equal(10, SavedSerial(ExtraA));
        Assert.Equal(3, SavedSerial(ExtraB));
        Assert.Equal(50, SavedSerial(CatalogDefaults.IndexUrl));
    }

    [Fact]
    public async Task A_catalog_without_a_pinned_key_is_not_checked_so_a_forged_serial_cannot_lock_it_out()
    {
        ServeOfficial(Index("1", "official"));
        _served[ExtraA] = (Index("999999", "a1"), null);
        var unpinned = new CatalogSourceEntry { Url = ExtraA };
        await Service(unpinned).LoadAsync();

        _served[ExtraA] = (Index("2", "a2"), null);
        var result = await Service(unpinned).LoadAsync();
        Assert.Contains(result.Entries, e => e.Id == "a2");
    }

    [Fact]
    public async Task A_pinned_catalog_cannot_be_rolled_back_either_from_a_wrong_signature()
    {
        ServeOfficial(Index("1", "official"));
        ServePinned(ExtraA, Index("10", "a1"));
        await Service(Pinned(ExtraA)).LoadAsync();

        // The old file is correctly signed by the developer: refused by serial. A file with someone else's signature: refused by the pin.
        ServePinned(ExtraA, Index("4", "a0"));
        Assert.DoesNotContain((await Service(Pinned(ExtraA)).LoadAsync()).Entries, e => e.Id == "a0");
        _served[ExtraA] = (Index("11", "forged"), Sign(_official, Index("11", "forged")));
        Assert.DoesNotContain((await Service(Pinned(ExtraA)).LoadAsync()).Entries, e => e.Id == "forged");
    }

    // ---- the repository's own index

    [Fact]
    public void The_index_in_the_repository_carries_a_serial()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "catalog", "index.json"))) dir = dir.Parent;
        if (dir is null) return;   // built outside the repository
        var index = CatalogParser.ParseIndex(File.ReadAllText(Path.Combine(dir.FullName, "catalog", "index.json")), CatalogDefaults.IndexUrl);
        Assert.True(index.Serial >= 1, "catalog/index.json needs a \"serial\" of at least 1 (scripts/catalog-sign.ps1 raises it on every signing)");
    }
}
