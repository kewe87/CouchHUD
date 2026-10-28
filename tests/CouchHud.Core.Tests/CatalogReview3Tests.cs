using System.Net;
using System.Security.Cryptography;
using System.Text;
using CouchHud.Core.Plugins;
using CouchHud.Core.Skins;

namespace CouchHud.Core.Tests;

/// <summary>Third check of the catalogs: reserved skin names, foreign catalogs behind a proxy, more IPv6 forms that embed IPv4. Throwaway keys only.</summary>
[Collection("Language")]
public sealed class CatalogReview3Tests : IDisposable
{
    private const string Extra = "https://plugins.example.org/index.json";
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-r3-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _official = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public CatalogReview3Tests() => Directory.CreateDirectory(_dir);

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

    private static string Entry(string id, string type, string folder = "") =>
        $$"""{ "id": "{{id}}", "folder": "{{(folder.Length > 0 ? folder : id)}}", "name": "{{id}}", "type": "{{type}}", "version": "1.0.0", "download": "https://example.com/{{id}}.zip", "sha256": "{{Hash}}" }""";

    private static string Index(params string[] entries) => "{ \"entries\": [" + string.Join(",", entries) + "] }";

    private string Sign(string text) =>
        Convert.ToBase64String(_official.SignData(CatalogSignature.WithContext(CatalogSignature.CatalogContext, Encoding.UTF8.GetBytes(text)), HashAlgorithmName.SHA256));

    private sealed class FakeHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request.RequestUri!));
    }

    private static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };

    private sealed class FixedProxy(Uri? proxy) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }
        public Uri? GetProxy(Uri destination) => proxy;
        public bool IsBypassed(Uri host) => proxy is null;
    }

    private CatalogService Service(string official, string extra, bool developerSwitch = false, IWebProxy? proxy = null) =>
        new(new HttpClient(new FakeHandler(u => u.ToString() switch
        {
            CatalogDefaults.IndexUrl => Ok(official),
            CatalogDefaults.IndexUrl + ".sig" => Ok(Sign(official)),
            Extra => Ok(extra),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        })), _dir, () => [CatalogDefaults.IndexUrl], () => developerSwitch, "de", _official.ExportSubjectPublicKeyInfoPem(), null,
            () => [new CatalogSourceEntry { Url = Extra }], proxy);

    // ---- 2: reserved skin names

    [Fact]
    public async Task A_foreign_catalog_cannot_offer_a_skin_with_a_reserved_name()
    {
        var official = Index(Entry("tool", "plugin"), Entry("stardust", "skin"), Entry("sunset", "skin", "sunset-dir"));
        var extra = Index(Entry("deepsea", "skin"), Entry("neon-city", "skin"), Entry("Nova", "skin"), Entry("default", "skin"), Entry("stardust2", "skin", "Stardust"),
            Entry("whatever", "skin", "sunset-dir"), Entry("deep-sea", "skin"), Entry("fine", "skin"));
        var result = await Service(official, extra).LoadAsync();

        foreach (var id in new[] { "deepsea", "neon-city", "Nova", "default", "stardust2", "whatever", "deep-sea" })
        {
            // Listed with a problem and no download, or (the folder of an official skin) not listed at all: never installable.
            foreach (var entry in result.Entries.Where(e => e.Id == id && e.Extra))
            {
                Assert.NotNull(entry.Problem);
                Assert.Null(entry.DownloadUrl);
            }
        }
        var fine = Assert.Single(result.Entries, e => e.Id == "fine");
        Assert.Null(fine.Problem);
        Assert.Contains("deepsea", SkinCatalog.MovedToCatalogIds);
    }

    // ---- 3: behind a proxy

    [Fact]
    public async Task Behind_a_proxy_a_foreign_catalog_is_skipped_with_a_message_unless_the_developer_switch_is_on()
    {
        var official = Index(Entry("tool", "plugin"));
        var extra = Index(Entry("fresh", "skin"));
        var proxy = new FixedProxy(new Uri("http://192.168.1.2:3128"));

        var behind = await Service(official, extra, proxy: proxy).LoadAsync();
        Assert.Contains(behind.Entries, e => e.Id == "tool");   // CouchHUD's own host works through the proxy
        Assert.DoesNotContain(behind.Entries, e => e.Id == "fresh");
        Assert.Contains(Localization.Strings.Get("catalog.source.proxy", "plugins.example.org"), behind.Error);

        // The same without a proxy: loaded. And with the developer switch behind the proxy: loaded as well.
        Assert.Contains((await Service(official, extra).LoadAsync()).Entries, e => e.Id == "fresh");
        Assert.Contains((await Service(official, extra, developerSwitch: true, proxy: proxy).LoadAsync()).Entries, e => e.Id == "fresh");
    }

    [Fact]
    public async Task Behind_a_proxy_a_package_of_a_foreign_catalog_is_not_downloaded_and_the_decision_follows_the_proxy_settings()
    {
        var official = Index(Entry("tool", "plugin"));
        var extra = Index(Entry("fresh", "skin"));
        var entry = (await Service(official, extra).LoadAsync()).Entries.Single(e => e.Id == "fresh");

        var behind = Service(official, extra, proxy: new FixedProxy(new Uri("http://192.168.1.2:3128")));
        var ex = await Assert.ThrowsAsync<CatalogException>(() => behind.InstallAsync(entry, new SemVer(9, 0, 0, ""), approve: _ => true));
        Assert.Equal(Localization.Strings.Get("catalog.source.proxy", entry.Name), ex.Message);
        Assert.True(behind.BehindProxy("https://example.com/x.zip"));
        Assert.False(Service(official, extra).BehindProxy("https://example.com/x.zip"));
        Assert.False(Service(official, extra, proxy: new FixedProxy(null)).BehindProxy("https://example.com/x.zip"));
        Assert.False(behind.BehindProxy("file:///C:/x"));
        Assert.False(behind.BehindProxy(null));
    }

    // ---- 4: more IPv6 forms

    [Theory]
    [InlineData("64:ff9b:1::1", false)]
    [InlineData("64:ff9b:1:ffff:ffff:ffff:808:808", false)]
    [InlineData("fe80::5efe:192.168.1.1", false)]
    [InlineData("2001:db8::5efe:10.1.2.3", false)]
    [InlineData("2001:db8::200:5efe:10.1.2.3", false)]
    [InlineData("2001:db8::200:5efe:8.8.8.8", true)]
    [InlineData("2001:db8::5efe:8.8.8.8", true)]
    public void More_IPv6_forms_that_carry_an_IPv4_address_are_judged_by_it(string address, bool isPublic) =>
        Assert.Equal(isPublic, CatalogHosts.IsPublic(IPAddress.Parse(address)));
}
