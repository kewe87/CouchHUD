using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CouchHud.Core.Plugins;

namespace CouchHud.Core.Tests;

/// <summary>
/// The second review of catalogs besides CouchHUD's own: a pinned key is a promise (a missing or wrong signature is an error, also from the
/// cache), addresses that lead into the user's own network are refused, redirects are followed by hand and checked on every hop, names
/// cannot pose as CouchHUD, ids of CouchHUD's plugins are reserved before the official index has loaded, and a removed catalog cannot be
/// installed from. Throwaway keys only.
/// </summary>
[Collection("Language")]
public sealed class CatalogSourcesHardeningTests : IDisposable
{
    private const string Extra = "https://plugins.example.org/index.json";
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-hard-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _official = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _other = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public CatalogSourcesHardeningTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _official.Dispose();
        _publisher.Dispose();
        _other.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string Pem(ECDsa key) => key.ExportSubjectPublicKeyInfoPem();

    private static string Entry(string id) =>
        $$"""{ "id": "{{id}}", "folder": "{{id}}", "name": "{{id}}", "type": "plugin", "version": "1.0.0", "download": "https://example.com/{{id}}.zip", "sha256": "{{Hash}}" }""";

    private static string Index(params string[] entries) => "{ \"entries\": [" + string.Join(",", entries) + "] }";

    private static string Sign(ECDsa key, string text) =>
        Convert.ToBase64String(key.SignData(CatalogSignature.WithContext(CatalogSignature.CatalogContext, Encoding.UTF8.GetBytes(text)), HashAlgorithmName.SHA256));

    private sealed class FakeHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request.RequestUri!));
    }

    private static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };

    private CatalogService Service(string official, string extra, string? extraSignature, string? pinned, bool extraOffline = false, IReadOnlyList<CatalogSourceEntry>? sources = null)
    {
        HttpResponseMessage Respond(Uri u)
        {
            var url = u.ToString();
            if (url == CatalogDefaults.IndexUrl) return Ok(official);
            if (url == CatalogDefaults.IndexUrl + ".sig") return Ok(Sign(_official, official));
            if (extraOffline && url.StartsWith(Extra, StringComparison.Ordinal)) throw new HttpRequestException("offline");
            if (url == Extra) return Ok(extra);
            if (url == Extra + ".sig") return extraSignature is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : Ok(extraSignature);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
        var list = sources ?? [new CatalogSourceEntry { Url = Extra, PublisherKey = pinned }];
        return new CatalogService(new HttpClient(new FakeHandler(Respond)), _dir, () => [CatalogDefaults.IndexUrl], () => false, "de", Pem(_official), null, () => list);
    }

    // ---- 3: a pinned key is a promise

    [Fact]
    public async Task With_a_pinned_key_a_wrong_or_missing_signature_is_an_error_not_an_unsigned_catalog()
    {
        var official = Index(Entry("tool"));
        var extra = Index(Entry("other"));

        var good = await Service(official, extra, Sign(_publisher, extra), Pem(_publisher)).LoadAsync();
        Assert.Contains(good.Entries, e => e.Id == "other" && !e.Unsigned);

        foreach (var signature in new[] { Sign(_other, extra), null, "not base64" })
        {
            File.Delete(Path.Combine(_dir, "catalog-cache.json"));   // otherwise the earlier good copy serves, which is right
            var result = await Service(official, extra, signature, Pem(_publisher)).LoadAsync();
            Assert.DoesNotContain(result.Entries, e => e.Id == "other");
            Assert.Contains(result.Entries, e => e.Id == "tool");
            Assert.Contains(Localization.Strings.Get("catalog.source.pinMismatch"), result.Error);
        }
    }

    [Fact]
    public async Task A_tampered_cached_copy_of_a_pinned_catalog_is_refused_too()
    {
        var official = Index(Entry("tool"));
        var extra = Index(Entry("other"));
        await Service(official, extra, Sign(_publisher, extra), Pem(_publisher)).LoadAsync();

        // The cache of the last good load still serves when the network is down ...
        var offline = await Service(official, extra, null, Pem(_publisher), extraOffline: true).LoadAsync();
        Assert.Contains(offline.Entries, e => e.Id == "other");

        // ... but not after somebody changed it (the signature no longer fits the bytes).
        var file = Path.Combine(_dir, "catalog-cache.json");
        var cache = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        cache[Extra]!["Data"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(Index(Entry("evil"))));
        File.WriteAllText(file, cache.ToJsonString());
        var tampered = await Service(official, extra, null, Pem(_publisher), extraOffline: true).LoadAsync();
        Assert.DoesNotContain(tampered.Entries, e => e.Id is "evil" or "other");
        Assert.NotNull(tampered.Error);
    }

    // ---- 4: hosts and redirects

    [Theory]
    [InlineData("https://127.0.0.1/i.json")]
    [InlineData("https://[::1]/i.json")]
    [InlineData("https://10.1.2.3/i.json")]
    [InlineData("https://172.16.0.1/i.json")]
    [InlineData("https://172.31.255.255/i.json")]
    [InlineData("https://192.168.178.1/i.json")]
    [InlineData("https://169.254.169.254/i.json")]
    [InlineData("https://100.64.0.1/i.json")]
    [InlineData("https://100.127.255.254/i.json")]
    [InlineData("https://0.0.0.0/i.json")]
    [InlineData("https://[fe80::1]/i.json")]
    [InlineData("https://[fd00::1]/i.json")]
    [InlineData("https://[::ffff:192.168.1.1]/i.json")]
    [InlineData("https://localhost/i.json")]
    [InlineData("https://LOCALHOST./i.json")]
    [InlineData("https://nas/i.json")]
    [InlineData("https://printer.local/i.json")]
    [InlineData("https://router.fritz.box.internal/i.json")]
    [InlineData("https://2130706433/i.json")]
    public void An_address_inside_the_users_own_network_is_never_a_catalog(string url)
    {
        Assert.False(CatalogHosts.IsPublicHost(url));
        Assert.False(CatalogHosts.Allowed(url, false));
        var gate = new PluginPolicy.GateContext(PluginCaller.Hud, false, false, true, false, true);
        Assert.Equal("catalog.source.privateHost", CatalogSourceRules.Decide(gate, CatalogSourceChange.Add, url, null, null, true, []).Refused);
        Assert.Empty(CatalogSourceRules.Effective([new CatalogSourceEntry { Url = url }]));
    }

    [Theory]
    [InlineData("https://plugins.example.org/i.json")]
    [InlineData("https://8.8.8.8/i.json")]
    [InlineData("https://100.63.255.255/i.json")]
    [InlineData("https://172.32.0.1/i.json")]
    [InlineData("https://[2606:4700::1111]/i.json")]
    public void Public_hosts_are_fine(string url) => Assert.True(CatalogHosts.IsPublicHost(url));

    [Fact]
    public void The_developer_switch_allows_the_local_network_again_but_only_for_loopback_and_files()
    {
        Assert.True(CatalogHosts.Allowed("http://127.0.0.1:8080/i.json", true));
        Assert.False(CatalogHosts.Allowed("http://127.0.0.1:8080/i.json", false));
        Assert.True(CatalogHosts.Allowed("https://nas/i.json", true));
    }

    private static HttpClient Redirecting(Func<Uri, HttpResponseMessage> respond) => new(new FakeHandler(respond));

    private static HttpResponseMessage Redirect(string to) => new(HttpStatusCode.Found) { Headers = { Location = new Uri(to, UriKind.RelativeOrAbsolute) } };

    [Fact]
    public async Task Redirects_are_followed_by_hand_at_most_three_hops_and_checked_each_time()
    {
        var hops = new List<string>();
        var http = Redirecting(u =>
        {
            hops.Add(u.ToString());
            return u.AbsolutePath switch
            {
                "/a" => Redirect("/b"),
                "/b" => Redirect("https://cdn.example.org/c"),
                "/c" => Redirect("https://cdn.example.org/d"),
                "/d" => Ok("done"),
                "/long" => Redirect("/long"),
                "/http" => Redirect("http://cdn.example.org/x"),
                "/lan" => Redirect("https://192.168.0.5/x"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });

        using (var done = await CatalogHosts.GetAsync(http, new Uri("https://plugins.example.org/a"), false, default))
            Assert.Equal("done", await done.Content.ReadAsStringAsync());
        Assert.Equal(4, hops.Count);

        await Assert.ThrowsAsync<HttpRequestException>(() => CatalogHosts.GetAsync(http, new Uri("https://plugins.example.org/long"), false, default));
        await Assert.ThrowsAsync<HttpRequestException>(() => CatalogHosts.GetAsync(http, new Uri("https://plugins.example.org/http"), false, default));
        hops.Clear();
        await Assert.ThrowsAsync<HttpRequestException>(() => CatalogHosts.GetAsync(http, new Uri("https://plugins.example.org/lan"), false, default));
        Assert.DoesNotContain(hops, h => h.Contains("192.168", StringComparison.Ordinal));
        // A first address that is not allowed is not even asked.
        hops.Clear();
        await Assert.ThrowsAsync<HttpRequestException>(() => CatalogHosts.GetAsync(http, new Uri("https://10.0.0.1/a"), false, default));
        Assert.Empty(hops);
    }

    [Fact]
    public void The_handler_follows_no_redirects_by_itself()
    {
        using var handler = (SocketsHttpHandler)CatalogHosts.CreateHandler(() => false);
        Assert.False(handler.AllowAutoRedirect);
        Assert.NotNull(handler.ConnectCallback);
    }

    [Fact]
    public async Task A_catalog_that_redirects_into_the_home_network_is_an_error_and_the_address_is_never_asked()
    {
        var asked = new List<string>();
        var service = new CatalogService(new HttpClient(new FakeHandler(u =>
        {
            asked.Add(u.ToString());
            if (u.ToString() == CatalogDefaults.IndexUrl) return Ok(Index(Entry("tool")));
            if (u.ToString() == CatalogDefaults.IndexUrl + ".sig") return Ok(Sign(_official, Index(Entry("tool"))));
            return u.Host == "plugins.example.org" ? Redirect("https://192.168.1.1/admin") : new HttpResponseMessage(HttpStatusCode.NotFound);
        })), _dir, () => [CatalogDefaults.IndexUrl], () => false, "de", Pem(_official), null, () => [new CatalogSourceEntry { Url = Extra }]);
        var result = await service.LoadAsync();
        Assert.DoesNotContain(asked, a => a.Contains("192.168", StringComparison.Ordinal));
        Assert.NotNull(result.Error);
    }

    // ---- 8: names

    [Theory]
    [InlineData("Friend\u202Egnp.exe")]
    [InlineData("Fri\u200Bend")]
    [InlineData("Fri\u0007end")]
    [InlineData("Fri\u2028end")]
    [InlineData("Fri\u00A0end")]
    [InlineData("\u0420\u0435\u0442\u0440")]
    [InlineData("a\u0301")]
    public void A_name_with_invisible_or_look_alike_characters_is_refused(string name) =>
        Assert.Equal("catalog.source.badName", CatalogSourceRules.CheckNew("https://plugins.example.org/i.json", null, name, []));

    [Theory]
    [InlineData("CouchHUD Official")]
    [InlineData("couchhud")]
    [InlineData("C0uchHud plugins")]
    [InlineData("Offiziell")]
    [InlineData("0fficial Plugins")]
    [InlineData("0FFIZIELL")]
    [InlineData("The Official Catalog")]
    [InlineData("kewe87")]
    public void A_name_that_poses_as_CouchHUD_or_as_official_is_refused(string name) =>
        Assert.Equal("catalog.source.nameImitates", CatalogSourceRules.CheckNew("https://plugins.example.org/i.json", null, name, []));

    [Theory]
    [InlineData("Friend's plugins")]
    [InlineData("Müller & Söhne")]
    [InlineData("Café 42")]
    public void An_ordinary_name_is_fine(string name) =>
        Assert.Null(CatalogSourceRules.CheckNew("https://plugins.example.org/i.json", null, name, []));

    // ---- 6 and 7: reserved before the official index has loaded

    [Fact]
    public async Task Plugins_of_another_catalog_wait_until_the_official_index_has_loaded_once()
    {
        var down = new CatalogService(new HttpClient(new FakeHandler(u => u.ToString().StartsWith(CatalogDefaults.IndexUrl, StringComparison.Ordinal)
                ? throw new HttpRequestException("offline") : u.ToString() == Extra ? Ok(Index(Entry("fresh"))) : new HttpResponseMessage(HttpStatusCode.NotFound))),
            _dir, () => [CatalogDefaults.IndexUrl], () => false, "de", Pem(_official), null, () => [new CatalogSourceEntry { Url = Extra }]);
        var result = await down.LoadAsync();
        var entry = Assert.Single(result.Entries, e => e.Id == "fresh");
        Assert.True(entry.Extra);
        Assert.Equal(Localization.Strings.Get("catalog.source.officialFirst"), entry.Problem);
        Assert.Null(entry.DownloadUrl);
        Assert.Throws<CatalogException>(() => down.VerifyEntry(entry with { Problem = null }));

        // Loaded once (and so cached): the plugin is listed as installable again.
        var up = await Service(Index(Entry("tool")), Index(Entry("fresh")), null, null).LoadAsync();
        Assert.Null(up.Entries.Single(e => e.Id == "fresh").Problem);
    }

    [Theory]
    [InlineData("homeassistant")]
    [InlineData("home-assistant-pro")]
    [InlineData("nordvpn")]
    [InlineData("Stardust")]
    public void The_names_of_CouchHUDs_own_plugins_are_fixed_in_the_program(string name) => Assert.True(PluginNames.IsReserved(name));

    [Fact]
    public async Task The_ids_of_the_official_plugins_are_worked_out_once_per_load()
    {
        var service = Service(Index(Entry("tool")), Index(Entry("fresh")), null, null);
        await service.LoadAsync();
        Assert.Same(service.FirstPartyIds(), service.FirstPartyIds());
    }

    // ---- 1: a removed catalog

    [Fact]
    public async Task A_package_of_a_catalog_that_was_removed_since_the_list_loaded_is_not_installed()
    {
        var sources = new List<CatalogSourceEntry> { new() { Url = Extra } };
        var service = Service(Index(Entry("tool")), Index(Entry("fresh")), null, null, sources: sources);
        var entry = (await service.LoadAsync()).Entries.Single(e => e.Id == "fresh");
        Assert.True(entry.Extra);
        sources.Clear();
        var ex = await Assert.ThrowsAsync<CatalogException>(() => service.InstallAsync(entry, new SemVer(9, 0, 0, "")));
        Assert.Equal(Localization.Strings.Get("catalog.source.removed"), ex.Message);
    }
}
