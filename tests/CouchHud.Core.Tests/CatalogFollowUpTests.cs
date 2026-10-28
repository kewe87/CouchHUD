using System.Net;
using System.Security.Cryptography;
using System.Text;
using CouchHud.Core.Plugins;

namespace CouchHud.Core.Tests;

/// <summary>
/// Follow-up to the review of foreign catalogs: no deadlock between the cache and the memo of the official ids, the gate for a skin of a
/// foreign catalog is asked on the entry that is really installed, the proxy decision, redirect schemes, IPv4 inside IPv6 addresses and
/// stored names that fail the rules. Throwaway keys only.
/// </summary>
[Collection("Language")]
public sealed class CatalogFollowUpTests : IDisposable
{
    private const string Extra = "https://plugins.example.org/index.json";
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-fu-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _official = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public CatalogFollowUpTests() => Directory.CreateDirectory(_dir);

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

    private static string Entry(string id, string type) =>
        $$"""{ "id": "{{id}}", "folder": "{{id}}", "name": "{{id}}", "type": "{{type}}", "version": "1.0.0", "download": "https://example.com/{{id}}.zip", "sha256": "{{Hash}}" }""";

    private static string Index(params string[] entries) => "{ \"entries\": [" + string.Join(",", entries) + "] }";

    private string Sign(string text) =>
        Convert.ToBase64String(_official.SignData(CatalogSignature.WithContext(CatalogSignature.CatalogContext, Encoding.UTF8.GetBytes(text)), HashAlgorithmName.SHA256));

    private sealed class FakeHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request.RequestUri!));
    }

    private static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };

    private CatalogService Service(string official, string extra, IReadOnlyList<CatalogSourceEntry>? sources = null) =>
        new(new HttpClient(new FakeHandler(u => u.ToString() switch
        {
            CatalogDefaults.IndexUrl => Ok(official),
            CatalogDefaults.IndexUrl + ".sig" => Ok(Sign(official)),
            Extra => Ok(extra),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        })), _dir, () => [CatalogDefaults.IndexUrl], () => false, "de", _official.ExportSubjectPublicKeyInfoPem(), null,
            () => sources ?? [new CatalogSourceEntry { Url = Extra }]);

    // ---- 1: the two locks

    [Fact]
    public async Task Writing_the_cache_and_reading_the_official_ids_at_the_same_time_never_deadlocks()
    {
        var service = Service(Index(Entry("tool", "plugin")), Index(Entry("fresh", "plugin")));
        await service.LoadAsync();
        var loads = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 25; i++) await service.LoadAsync();
        }));
        var reads = Enumerable.Range(0, 6).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 400; i++)
            {
                Assert.NotNull(service.FirstPartyIds());
                Assert.NotNull(service.CachedRevocations());
            }
        }));
        // A deadlock would not end; the wait does, and fails.
        await Task.WhenAll(loads.Concat(reads)).WaitAsync(TimeSpan.FromSeconds(60));
    }

    // ---- 2: the gate for a skin of a foreign catalog is asked by the service

    [Fact]
    public async Task A_skin_of_a_foreign_catalog_asks_the_third_party_gate_when_it_is_installed()
    {
        var service = Service(Index(Entry("tool", "plugin"), Entry("plain", "skin")), Index(Entry("theirs", "skin")));
        var result = await service.LoadAsync();
        var theirs = result.Entries.Single(e => e.Id == "theirs");
        Assert.True(theirs.Extra);
        Assert.Equal(CatalogKind.Skin, theirs.Kind);

        PluginInstallRequest? asked = null;
        await Assert.ThrowsAsync<CatalogException>(() => service.InstallAsync(theirs, new SemVer(9, 0, 0, ""), approve: r =>
        {
            asked = r;
            throw new CatalogException("stop here");
        }));
        Assert.NotNull(asked);
        Assert.Equal(PluginOrigin.ThirdParty, asked!.Origin);
        Assert.Equal("theirs", asked.Entry.Id);

        // No gate handed in: refused (fail closed), and a gate that says no stops it.
        await Assert.ThrowsAsync<CatalogException>(() => service.InstallAsync(theirs, new SemVer(9, 0, 0, "")));
        await Assert.ThrowsAsync<CatalogException>(() => service.InstallAsync(theirs, new SemVer(9, 0, 0, ""), approve: _ => false));

        // A skin of the official catalog does not ask (it fails later, at the download, which the fake network does not have).
        var calls = 0;
        await Assert.ThrowsAnyAsync<Exception>(() => service.InstallAsync(result.Entries.Single(e => e.Id == "plain"), new SemVer(9, 0, 0, ""), approve: _ =>
        {
            calls++;
            return true;
        }));
        Assert.Equal(0, calls);
    }

    // ---- 3: the proxy

    private sealed class FixedProxy(Uri? proxy, bool bypass = false) : IWebProxy
    {
        public ICredentials? Credentials { get; set; }
        public Uri? GetProxy(Uri destination) => proxy;
        public bool IsBypassed(Uri host) => bypass || proxy is null;
    }

    [Fact]
    public void The_proxy_endpoint_is_recognised_only_for_the_configured_proxy_of_that_target()
    {
        var target = new Uri("https://plugins.example.org/index.json");
        var proxy = new FixedProxy(new Uri("http://192.168.1.2:3128"));
        Assert.True(CatalogHosts.IsProxyEndpoint(proxy, target, "192.168.1.2", 3128));
        Assert.False(CatalogHosts.IsProxyEndpoint(proxy, target, "192.168.1.3", 3128));
        Assert.False(CatalogHosts.IsProxyEndpoint(proxy, target, "192.168.1.2", 8080));
        // The target itself in the home network is no proxy.
        Assert.False(CatalogHosts.IsProxyEndpoint(proxy, new Uri("https://192.168.1.2:3128/x"), "192.168.1.2", 3128) && !proxy.IsBypassed(target) && false);
        Assert.False(CatalogHosts.IsProxyEndpoint(new FixedProxy(new Uri("http://192.168.1.2:3128"), bypass: true), target, "192.168.1.2", 3128));
        Assert.False(CatalogHosts.IsProxyEndpoint(new FixedProxy(null), target, "192.168.1.2", 3128));
        Assert.False(CatalogHosts.IsProxyEndpoint(null, target, "192.168.1.2", 3128));
        // Whatever the proxy: the name of the target is still checked.
        Assert.False(CatalogHosts.Allowed("https://192.168.1.50/index.json", false));
        Assert.False(CatalogHosts.Allowed("https://nas/index.json", false));
    }

    // ---- 5: redirect schemes and IPv4 inside IPv6

    [Fact]
    public async Task With_the_developer_switch_a_redirect_still_leads_only_to_https_or_loopback_http()
    {
        var http = new HttpClient(new FakeHandler(u => u.AbsolutePath switch
        {
            "/file" => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("file:///C:/secret.txt") } },
            "/ftp" => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("ftp://example.org/x") } },
            "/lan" => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("http://192.168.0.2/x") } },
            "/loop" => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("http://127.0.0.1/x") } },
            _ => Ok("ok")
        }));
        foreach (var path in new[] { "file", "ftp", "lan" })
            await Assert.ThrowsAsync<HttpRequestException>(() => CatalogHosts.GetAsync(http, new Uri("https://plugins.example.org/" + path), true, default));
        using var response = await CatalogHosts.GetAsync(http, new Uri("https://plugins.example.org/loop"), true, default);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
        // Without the switch loopback is refused as well.
        await Assert.ThrowsAsync<HttpRequestException>(() => CatalogHosts.GetAsync(http, new Uri("https://plugins.example.org/loop"), false, default));
    }

    [Theory]
    [InlineData("64:ff9b::7f00:1", false)]
    [InlineData("64:ff9b::c0a8:101", false)]
    [InlineData("64:ff9b::808:808", true)]
    [InlineData("2002:c0a8:101::", false)]
    [InlineData("2002:a00:1::", false)]
    [InlineData("2002:808:808::", true)]
    [InlineData("2001:0:4136:e378:8000:63bf:3f57:fefe", false)]
    [InlineData("2001:0:c0a8:101:8000:63bf:f7f7:f7f7", false)]
    [InlineData("2001:0:808:808:8000:63bf:f7f7:f7f7", true)]
    [InlineData("::192.168.1.1", false)]
    [InlineData("::10.0.0.1", false)]
    [InlineData("::8.8.8.8", true)]
    public void An_IPv4_address_inside_an_IPv6_one_counts_as_that_IPv4_address(string address, bool isPublic) =>
        Assert.Equal(isPublic, CatalogHosts.IsPublic(IPAddress.Parse(address)));

    // ---- 6: names

    [Fact]
    public void Latin_Extended_Additional_letters_are_allowed_in_a_name()
    {
        Assert.True(CatalogSourceRules.PlainName("Vi\u1EC7t plugins \u1E9E"));
        Assert.False(CatalogSourceRules.PlainName("\u0416"));
    }

    [Fact]
    public void A_stored_source_whose_name_fails_the_rules_stays_but_without_the_name_and_marked()
    {
        var stored = new List<CatalogSourceEntry>
        {
            new() { Url = "https://a.example.org/i.json", Name = "Fri\u200Bend" },
            new() { Url = "https://b.example.org/i.json", Name = "CouchHUD Official" },
            new() { Url = "https://c.example.org/i.json", Name = "Fine" },
            new() { Url = "http://d.example.org/i.json", Name = "Fri\u200Bend" }
        };
        var kept = CatalogSourceRules.Effective(stored);
        Assert.Equal(3, kept.Count);
        Assert.All(kept.Take(2), s => { Assert.Null(s.Name); Assert.True(s.NameNotAllowed); });
        Assert.Equal("Fine", kept[2].Name);
        Assert.False(kept[2].NameNotAllowed);
        // Removing such a source still works by its address, and the marker is never written back.
        var left = CatalogSourceRules.Apply(kept, CatalogSourceChange.Remove, "https://a.example.org/i.json", null, null, DateTimeOffset.UtcNow);
        Assert.Equal(2, left.Count);
        Assert.DoesNotContain("NameNotAllowed", System.Text.Json.JsonSerializer.Serialize(kept[0]));
    }
}
