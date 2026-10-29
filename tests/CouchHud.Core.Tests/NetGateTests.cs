using System.Net;
using System.Text.RegularExpressions;
using CouchHud.Core.Assistant;
using CouchHud.Core.Config;
using CouchHud.Core.Net;
using CouchHud.Core.Plugins;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>
/// Settings › Privacy: the policy per switch, the gate in front of every outbound request, LAN destinations that no switch touches, redirects
/// that end somewhere public, the plugin pause and the scan that keeps new code from going around the gate. Nothing here opens a socket.
/// </summary>
public class NetGateTests
{
    // ---------- the policy: one switch per service, Offline above all ----------

    public static TheoryData<NetService, string> Switches => new()
    {
        { NetService.Updates, nameof(AppConfig.SelfUpdateCheck) },
        { NetService.Catalog, nameof(AppConfig.NetCatalog) },
        { NetService.SystemUpdates, nameof(AppConfig.NetSystemUpdates) },
        { NetService.Artwork, nameof(AppConfig.OnlineArtwork) },
        { NetService.Saves, nameof(AppConfig.NetSaves) },
        { NetService.Weather, nameof(AppConfig.NetWeather) },
        { NetService.News, nameof(AppConfig.NetNews) },
        { NetService.Assistant, nameof(AppConfig.NetAssistant) }
    };

    private static void Set(AppConfig c, string property, bool value) => typeof(AppConfig).GetProperty(property)!.SetValue(c, value);

    [Fact]
    public void Everything_is_allowed_by_default_and_for_an_old_config_without_the_new_keys()
    {
        var fresh = new AppConfig();
        foreach (var service in Enum.GetValues<NetService>()) Assert.True(NetPolicy.Allows(fresh, service), service.ToString());
        Assert.False(fresh.Offline);
        Assert.False(fresh.PluginAutoUpdate);

        // A config.json of 0.6.1 (no privacy keys at all, the old toggles with their values) keeps those values and turns nothing off.
        var old = ConfigStore.Parse("""{ "selfUpdateCheck": false, "onlineArtwork": true, "catalogUpdateCheck": true }""")!;
        Assert.False(old.SelfUpdateCheck);
        Assert.False(NetPolicy.Allows(old, NetService.Updates));
        Assert.True(NetPolicy.Allows(old, NetService.Catalog));
        Assert.True(NetPolicy.Allows(old, NetService.Weather));
        Assert.False(old.Offline);
    }

    [Theory]
    [MemberData(nameof(Switches))]
    public void A_switch_stops_only_its_own_service(NetService service, string property)
    {
        var c = new AppConfig();
        Set(c, property, false);
        Assert.False(NetPolicy.Allows(c, service));
        foreach (var other in Enum.GetValues<NetService>().Where(s => s != service && s != NetService.GameInfo))
        {
            Assert.True(NetPolicy.Allows(c, other), $"{other} must not follow {service}");
        }
    }

    [Fact]
    public void Offline_stops_every_service_whatever_the_switches_say()
    {
        var c = new AppConfig { Offline = true };
        foreach (var service in Enum.GetValues<NetService>()) Assert.False(NetPolicy.Allows(c, service), service.ToString());
    }

    [Fact]
    public void Game_info_follows_the_artwork_switch_but_parental_controls_keep_the_age_ratings()
    {
        var c = new AppConfig { OnlineArtwork = false };
        Assert.False(NetPolicy.Allows(c, NetService.GameInfo));
        c.ParentalEnabled = true;
        Assert.True(NetPolicy.Allows(c, NetService.GameInfo));
        Assert.False(NetPolicy.Allows(c, NetService.Artwork));
        c.Offline = true;
        Assert.False(NetPolicy.Allows(c, NetService.GameInfo));
    }

    [Fact]
    public void The_clock_check_has_no_switch_only_offline_stops_it()
    {
        var c = new AppConfig { SelfUpdateCheck = false, NetCatalog = false, NetSystemUpdates = false, OnlineArtwork = false, NetSaves = false, NetWeather = false, NetNews = false, NetAssistant = false };
        Assert.True(NetPolicy.Allows(c, NetService.Time));
        c.Offline = true;
        Assert.False(NetPolicy.Allows(c, NetService.Time));
    }

    // ---------- config normalization ----------

    [Fact]
    public void The_automatic_plugin_update_needs_the_plugin_update_search()
    {
        var c = new AppConfig { PluginAutoUpdate = true };
        ConfigStore.NormalizeNet(c);
        Assert.True(c.PluginAutoUpdate);
        c.CatalogUpdateCheck = false;
        ConfigStore.NormalizeNet(c);
        Assert.False(c.PluginAutoUpdate);

        var catalogOff = new AppConfig { PluginAutoUpdate = true, NetCatalog = false };
        ConfigStore.NormalizeNet(catalogOff);
        Assert.False(catalogOff.PluginAutoUpdate);

        // Offline alone keeps the choice: it comes back when Offline goes.
        var offline = new AppConfig { PluginAutoUpdate = true, Offline = true };
        ConfigStore.NormalizeNet(offline);
        Assert.True(offline.PluginAutoUpdate);
    }

    [Fact]
    public void The_privacy_keys_survive_a_save_and_a_load_and_are_normalized_on_load()
    {
        var parsed = ConfigStore.Parse("""{ "offline": true, "netNews": false, "netCatalog": false, "pluginAutoUpdate": true }""")!;
        Assert.True(parsed.Offline);
        Assert.False(parsed.NetNews);
        Assert.False(parsed.PluginAutoUpdate);
        Assert.True(parsed.NetWeather);
    }

    // ---------- the gate ----------

    private sealed class Inner(Func<HttpRequestMessage, HttpResponseMessage>? answer = null) : HttpMessageHandler
    {
        public List<Uri> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request.RequestUri!);
            return Task.FromResult(answer?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static HttpClient Client(NetService service, AppConfig config, Inner inner, bool follow = false, Func<string, IPAddress[]>? dns = null) =>
        new(new NetGateHandler(service, new NetPolicy(() => config), inner, follow,
            (host, _) => Task.FromResult(dns?.Invoke(host) ?? throw new InvalidOperationException($"no DNS expected for {host}"))));

    [Fact]
    public async Task A_service_that_is_on_goes_through_to_a_public_host_without_a_lookup()
    {
        var inner = new Inner();
        var client = Client(NetService.Weather, new AppConfig(), inner);
        using var response = await client.GetAsync("https://api.open-meteo.com/v1/forecast");
        Assert.Single(inner.Seen);
    }

    [Theory]
    [MemberData(nameof(Switches))]
    public async Task A_service_that_is_off_is_refused_before_anything_is_sent(NetService service, string property)
    {
        var config = new AppConfig();
        Set(config, property, false);
        var inner = new Inner();
        var client = Client(service, config, inner, dns: _ => [IPAddress.Parse("93.184.216.34")]);
        var ex = await Assert.ThrowsAsync<NetBlockedException>(() => client.GetAsync("https://example.com/x"));
        Assert.False(ex.Offline);
        Assert.Equal(service, ex.Service);
        Assert.Empty(inner.Seen);
        // Callers that survive a network failure survive this too.
        Assert.IsAssignableFrom<HttpRequestException>(ex);
    }

    [Fact]
    public async Task Offline_refuses_even_a_service_whose_switch_is_on()
    {
        var inner = new Inner();
        var client = Client(NetService.Catalog, new AppConfig { Offline = true }, inner, dns: _ => [IPAddress.Parse("93.184.216.34")]);
        var ex = await Assert.ThrowsAsync<NetBlockedException>(() => client.GetAsync("https://example.com/index.json"));
        Assert.True(ex.Offline);
        Assert.Empty(inner.Seen);
    }

    [Theory]
    [InlineData("http://192.168.1.20:3000/")]
    [InlineData("http://10.0.0.5/")]
    [InlineData("http://172.16.4.4/")]
    [InlineData("http://172.31.255.255/")]
    [InlineData("http://127.0.0.1:8123/")]
    [InlineData("http://[::1]:8123/")]
    [InlineData("http://169.254.10.10/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://[fd12:3456:789a::1]/")]
    [InlineData("http://[fc00::5]/")]
    [InlineData("http://[::ffff:192.168.1.5]/")]
    public async Task The_local_network_is_never_covered_by_a_switch(string url)
    {
        var inner = new Inner();
        var client = Client(NetService.Assistant, new AppConfig { Offline = true, NetAssistant = false }, inner);
        using var response = await client.GetAsync(url);
        Assert.Single(inner.Seen);
    }

    [Theory]
    [InlineData("http://8.8.8.8/")]
    [InlineData("http://172.32.0.1/")]
    [InlineData("http://172.15.0.1/")]
    [InlineData("http://11.0.0.1/")]
    [InlineData("http://192.169.1.1/")]
    [InlineData("http://100.64.0.1/")]
    [InlineData("http://[2606:4700:4700::1111]/")]
    [InlineData("http://[2001:db8::1]/")]
    [InlineData("http://[::ffff:8.8.8.8]/")]
    public async Task Public_addresses_are_refused_when_the_service_is_off(string url)
    {
        var inner = new Inner();
        var client = Client(NetService.News, new AppConfig { NetNews = false }, inner);
        await Assert.ThrowsAsync<NetBlockedException>(() => client.GetAsync(url));
        Assert.Empty(inner.Seen);
    }

    [Fact]
    public async Task A_name_counts_by_what_it_resolves_to_not_by_how_it_looks()
    {
        var config = new AppConfig { NetNews = false };
        IPAddress[] Dns(string host) => host switch
        {
            "homeassistant.local" => [IPAddress.Parse("192.168.1.2")],
            "router" => [IPAddress.Parse("fd00::1")],
            // Looks local, is public.
            "printer.local" => [IPAddress.Parse("93.184.216.34")],
            "localhost.evil.example" => [IPAddress.Parse("93.184.216.34")],
            "192.168.1.1.evil.example" => [IPAddress.Parse("93.184.216.34")],
            // One public answer among local ones is public.
            "mixed.example" => [IPAddress.Parse("192.168.1.9"), IPAddress.Parse("93.184.216.34")],
            "gone.example" => [],
            "localhost" => [IPAddress.Loopback, IPAddress.IPv6Loopback],
            _ => throw new System.Net.Sockets.SocketException(11001)
        };

        foreach (var local in new[] { "homeassistant.local", "router", "localhost" })
        {
            var inner = new Inner();
            using var response = await Client(NetService.News, config, inner, dns: Dns).GetAsync($"http://{local}/feed");
            Assert.Single(inner.Seen);
        }
        foreach (var name in new[] { "printer.local", "localhost.evil.example", "192.168.1.1.evil.example", "mixed.example", "gone.example", "unknown.example" })
        {
            var inner = new Inner();
            await Assert.ThrowsAsync<NetBlockedException>(() => Client(NetService.News, config, inner, dns: Dns).GetAsync($"http://{name}/feed"));
            Assert.Empty(inner.Seen);
        }
    }

    [Fact]
    public async Task A_redirect_from_the_local_network_to_a_public_host_is_refused_when_the_service_is_off()
    {
        var config = new AppConfig { NetWeather = false };
        var inner = new Inner(request => request.RequestUri!.Host == "192.168.1.5"
            ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://tracker.example/pixel") } }
            : new HttpResponseMessage(HttpStatusCode.OK));
        var client = Client(NetService.Weather, config, inner, follow: true, dns: _ => [IPAddress.Parse("93.184.216.34")]);
        await Assert.ThrowsAsync<NetBlockedException>(() => client.GetAsync("http://192.168.1.5/start"));
        // The first hop went out (it is local); the public hop did not.
        Assert.Equal([new Uri("http://192.168.1.5/start")], inner.Seen);
    }

    [Fact]
    public async Task A_redirect_to_a_relative_local_address_is_followed_and_to_a_public_one_is_followed_only_while_the_service_is_on()
    {
        Inner Redirecting() => new(request => request.RequestUri!.AbsolutePath == "/start"
            ? new HttpResponseMessage(HttpStatusCode.MovedPermanently) { Headers = { Location = new Uri("/final", UriKind.Relative) } }
            : new HttpResponseMessage(HttpStatusCode.OK));

        var local = Redirecting();
        using (await Client(NetService.Weather, new AppConfig { Offline = true }, local, follow: true).GetAsync("http://10.1.1.1/start")) { }
        Assert.Equal(["http://10.1.1.1/start", "http://10.1.1.1/final"], local.Seen.Select(u => u.ToString()));

        var on = new Inner(request => request.RequestUri!.Host == "a.example"
            ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://b.example/x") } }
            : new HttpResponseMessage(HttpStatusCode.OK));
        using (await Client(NetService.Weather, new AppConfig(), on, follow: true).GetAsync("https://a.example/start")) { }
        Assert.Equal(["https://a.example/start", "https://b.example/x"], on.Seen.Select(u => u.ToString()));
    }

    [Fact]
    public async Task Redirects_stop_after_a_few_hops_and_never_go_from_https_to_http_or_carry_credentials_to_another_host()
    {
        var loop = new Inner(request => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://loop.example/next") } });
        using var last = await Client(NetService.News, new AppConfig(), loop, follow: true).GetAsync("https://loop.example/start");
        Assert.Equal(NetGateHandler.MaxRedirects + 1, loop.Seen.Count);
        Assert.Equal(HttpStatusCode.Found, last.StatusCode);

        var downgrade = new Inner(_ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("http://plain.example/x") } });
        using var stopped = await Client(NetService.News, new AppConfig(), downgrade, follow: true).GetAsync("https://secure.example/x");
        Assert.Single(downgrade.Seen);

        var seenAuth = new List<bool>();
        var hop = new Inner(request =>
        {
            seenAuth.Add(request.Headers.Authorization is not null);
            return request.RequestUri!.Host == "one.example"
                ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://two.example/x") } }
                : new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://one.example/x") { Headers = { Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "secret") } };
        using (await Client(NetService.News, new AppConfig(), hop, follow: true).SendAsync(request)) { }
        Assert.Equal([true, false], seenAuth);
    }

    [Fact]
    public async Task A_post_is_never_followed_to_another_place()
    {
        var inner = new Inner(_ => new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri("https://other.example/x") } });
        using var response = await Client(NetService.Assistant, new AppConfig(), inner, follow: true).PostAsync("https://api.example/x", new StringContent("{}"));
        Assert.Single(inner.Seen);
        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
    }

    [Fact]
    public async Task Only_http_and_https_pass_the_gate()
    {
        var inner = new Inner();
        var client = Client(NetService.Weather, new AppConfig(), inner);
        await Assert.ThrowsAsync<NetBlockedException>(() => client.GetAsync("ftp://example.com/x"));
        Assert.Empty(inner.Seen);
    }

    [Fact]
    public async Task A_switch_takes_effect_for_the_next_request_without_a_new_client()
    {
        var config = new AppConfig();
        var inner = new Inner();
        var client = Client(NetService.Saves, config, inner, dns: _ => [IPAddress.Parse("93.184.216.34")]);
        using (await client.GetAsync("https://manifest.example/m.yaml")) { }
        config.NetSaves = false;
        await Assert.ThrowsAsync<NetBlockedException>(() => client.GetAsync("https://manifest.example/m.yaml"));
        config.NetSaves = true;
        config.Offline = true;
        await Assert.ThrowsAsync<NetBlockedException>(() => client.GetAsync("https://manifest.example/m.yaml"));
        config.Offline = false;
        using (await client.GetAsync("https://manifest.example/m.yaml")) { }
        Assert.Equal(2, inner.Seen.Count);
    }

    [Fact]
    public async Task The_assistant_turns_a_blocked_request_into_its_own_message()
    {
        var inner = new Inner();
        var client = Client(NetService.Assistant, new AppConfig { NetAssistant = false }, inner, dns: _ => [IPAddress.Parse("160.79.104.10")]);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages") { Content = new StringContent("{}") };
        var ex = await Assert.ThrowsAsync<AssistantException>(() => ChatProviders.PostAsync(client, request, default));
        Assert.Contains("api.anthropic.com", ex.Message);
        Assert.Empty(inner.Seen);
    }

    [Fact]
    public async Task The_default_client_refuses_before_it_connects_and_a_failed_connection_counts_for_nobody()
    {
        // No server involved: the public literal is refused by the gate itself, so nothing is dialed.
        var client = NetGate.CreateClient(NetService.News, new NetPolicy(() => new AppConfig { NetNews = false }), TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<NetBlockedException>(() => client.GetAsync("http://203.0.113.9/feed"));
    }

    [Fact]
    public async Task A_redirect_to_another_host_carries_only_harmless_headers()
    {
        var seen = new List<(string Host, string[] Headers)>();
        var hop = new Inner(request =>
        {
            seen.Add((request.RequestUri!.Host, request.Headers.Select(h => h.Key).ToArray()));
            return request.RequestUri!.Host == "one.example"
                ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://two.example/x") } }
                : new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://one.example/x");
        request.Headers.TryAddWithoutValidation("Cookie", "session=1");
        request.Headers.TryAddWithoutValidation("x-api-key", "secret");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer secret");
        request.Headers.TryAddWithoutValidation("X-Anything", "1");
        request.Headers.TryAddWithoutValidation("Accept-Language", "de");
        request.Headers.TryAddWithoutValidation("User-Agent", "CouchHUD/1");
        request.Headers.TryAddWithoutValidation("If-None-Match", "\"abc\"");
        using (await Client(NetService.News, new AppConfig(), hop, follow: true).SendAsync(request)) { }

        var second = seen.Single(s => s.Host == "two.example").Headers;
        Assert.Equal(["Accept-Language", "If-None-Match", "User-Agent"], second.OrderBy(h => h, StringComparer.Ordinal));
        Assert.Contains("Cookie", seen.Single(s => s.Host == "one.example").Headers);
    }

    [Fact]
    public async Task A_redirect_inside_the_same_host_keeps_its_headers()
    {
        var seen = new List<string[]>();
        var hop = new Inner(request =>
        {
            seen.Add(request.Headers.Select(h => h.Key).ToArray());
            return request.RequestUri!.AbsolutePath == "/a"
                ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://one.example/b") } }
                : new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://one.example/a");
        request.Headers.TryAddWithoutValidation("x-api-key", "secret");
        using (await Client(NetService.News, new AppConfig(), hop, follow: true).SendAsync(request)) { }
        Assert.Equal(2, seen.Count);
        Assert.Contains("x-api-key", seen[1]);
    }

    // Fail closed: addresses that are neither a home network nor a normal public one are not "local".
    [Theory]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://[::]/")]
    [InlineData("http://224.0.0.1/")]
    [InlineData("http://[ff02::1]/")]
    [InlineData("http://255.255.255.255/")]
    [InlineData("http://100.64.0.1/")]
    [InlineData("http://198.18.0.1/")]
    public async Task Unspecified_multicast_and_shared_addresses_are_not_local(string url)
    {
        var inner = new Inner();
        await Assert.ThrowsAsync<NetBlockedException>(() => Client(NetService.Weather, new AppConfig { Offline = true }, inner).GetAsync(url));
        Assert.Empty(inner.Seen);
    }

    [Fact]
    public async Task A_decimal_address_is_read_like_the_address_it_stands_for()
    {
        // 2130706433 is 127.0.0.1 (the URL parser turns it into the dotted form, so the gate sees the loopback address); 3232235777 is 192.168.1.1.
        var inner = new Inner();
        var client = Client(NetService.Weather, new AppConfig { Offline = true }, inner);
        using (await client.GetAsync("http://2130706433/")) { }
        using (await client.GetAsync("http://3232235777/")) { }
        Assert.Equal(["http://127.0.0.1/", "http://192.168.1.1/"], inner.Seen.Select(u => u.ToString()));
        // And a decimal that is public stays refused.
        await Assert.ThrowsAsync<NetBlockedException>(() => client.GetAsync("http://134744072/"));
    }

    // ---------- the plugin pause ----------

    [Theory]
    [InlineData(true, new[] { "pages", "network:internet" }, true)]
    [InlineData(true, new[] { "network:internet" }, true)]
    [InlineData(true, new[] { "pages", "network:lan" }, false)]
    [InlineData(true, new[] { "network:loopback" }, false)]
    [InlineData(true, new string[0], false)]
    [InlineData(false, new[] { "network:internet" }, false)]
    public void Offline_pauses_a_plugin_only_when_it_may_reach_the_internet(bool offline, string[] capabilities, bool paused)
    {
        Assert.Equal(paused, PluginOfflinePolicy.Pauses(offline, capabilities));
    }

    [Fact]
    public void A_plugin_that_names_both_the_internet_and_the_home_network_is_paused_and_the_lan_capability_is_known()
    {
        Assert.True(PluginOfflinePolicy.Pauses(true, [PluginCapabilities.NetworkLan, PluginCapabilities.NetworkInternet]));
        Assert.False(PluginOfflinePolicy.Pauses(true, null));
        Assert.Contains(PluginCapabilities.NetworkLan, PluginCapabilities.All);
    }

    // ---------- nothing goes around the gate ----------

    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CouchHud.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repository root not found");
    }

    /// <summary>
    /// The only places that may create an HTTP client, a socket, a UDP/TCP client, a web socket or resolve a name, and why. Everything else asks
    /// NetGate for its client, so the privacy switches and Offline cover it. Each entry lists the lines it allows (a regex per line) and how many
    /// hits the file has, so a new bypass in an allowed file fails too. A new entry needs a reason a reviewer accepts.
    /// </summary>
    private sealed record Place(string Reason, int Hits, params string[] Lines);

    private static readonly Dictionary<string, Place> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["src/CouchHud.Core/Net/NetGate.cs"] = new("the gate itself", 5, "."),
        ["src/CouchHud.Core/Net/SafeNet.cs"] = new("public-only connector, handed to the gate by the caller", 3, "."),
        ["src/CouchHud.Core/Plugins/CatalogHosts.cs"] = new("catalog connector (public-only, developer switch), handed to the gate by the caller", 3, "."),
        ["src/CouchHud.Core/Plugins/PluginLint.cs"] = new("names of types the lint looks for in plugins, no network code", 4, "^\\(\"System\\.(Net|Net\\.Http|Net\\.WebSockets)\", \""),
        ["src/CouchHud.Core/Integrations/Tv/SsdpDiscovery.cs"] = new("UDP discovery on the local network interfaces", 2, "UdpClient"),
        ["src/CouchHud.Core/Integrations/Tv/WakeOnLan.cs"] = new("Wake-on-LAN broadcast on the local network", 1, "new UdpClient\\(new IPEndPoint\\(local, 0\\)\\) \\{ EnableBroadcast"),
        // The paired TV: its connect callback takes only addresses TvAddressPolicy allows (local network). Exactly these five lines.
        ["src/CouchHud.Core/Integrations/Tv/WebOsClient.cs"] = new("the paired TV: only addresses TvAddressPolicy allows, never the internet", 5,
            "^internal sealed record Connected\\(ClientWebSocket Socket, HttpMessageInvoker Invoker,",
            "^var handler = new SocketsHttpHandler$",
            "^var s = new Socket\\(ip\\.AddressFamily, SocketType\\.Stream, ProtocolType\\.Tcp\\)",
            "^var invoker = new HttpMessageInvoker\\(handler, disposeHandler: true\\);$",
            "^var ws = new ClientWebSocket\\(\\);$")
    };

    private static readonly Regex Bypass = new(
        @"new\s+HttpClient\s*[\(\{]|HttpClient\s+\w+\s*=\s*new\s*\(|new\s+ClientWebSocket\s*\(|SocketsHttpHandler|HttpClientHandler|new\s+WebClient\b|HttpWebRequest|WebRequest\.Create|new\s+TcpClient\s*\(|\bTcpClient\b|\bUdpClient\b|HttpMessageInvoker|new\s+(System\.Net\.Sockets\.)?Socket\s*\(|\bDns\.",
        RegexOptions.Compiled);

    private static IEnumerable<(string File, int Line, string Text)> Hits(string root)
    {
        foreach (var folder in new[] { "src", "plugins" })
        {
            var dir = Path.Combine(root, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var path in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                if (relative.Contains("/obj/") || relative.Contains("/bin/")) continue;
                var lines = File.ReadAllLines(path);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();
                    if (line.StartsWith("//", StringComparison.Ordinal)) continue;
                    if (Bypass.IsMatch(line)) yield return (relative, i + 1, line);
                }
            }
        }
    }

    [Fact]
    public void No_outbound_connection_is_made_outside_the_gate()
    {
        var root = Root();
        var stray = new List<string>();
        foreach (var hit in Hits(root))
        {
            if (Allowed.TryGetValue(hit.File, out var place) && place.Lines.Any(p => Regex.IsMatch(hit.Text, p))) continue;
            stray.Add($"{hit.File}:{hit.Line}: {hit.Text}");
        }
        Assert.True(stray.Count == 0, "Outbound connections must go through NetGate (Core/Net/NetGate.cs) so Settings › Privacy covers them:\n" + string.Join("\n", stray));
    }

    [Fact]
    public void The_allowlist_is_pinned_to_what_the_files_hold_today()
    {
        var root = Root();
        var counts = Hits(root).GroupBy(h => h.File, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        foreach (var (file, place) in Allowed)
        {
            Assert.True(counts.TryGetValue(file, out var n), $"{file} no longer needs its place in the allowlist");
            Assert.True(n == place.Hits, $"{file} has {n} hits, the allowlist pins {place.Hits}: review the new line, then change the pin ({place.Reason})");
        }
    }

    [Fact]
    public void The_scan_catches_what_it_is_for()
    {
        foreach (var bad in new[]
        {
            "var c = new HttpClient();", "var c = new HttpClient { Timeout = t };", "private readonly HttpClient _h = new() { Timeout = t };", "var s = new Socket(a, b, c);", "using var u = new UdpClient(0);", "var t = new TcpClient(host, 80);", "var a = Dns.GetHostAddresses(h);", "var i = new HttpMessageInvoker(h);",
            "using var ws = new ClientWebSocket();", "var h = new SocketsHttpHandler();", "var h = new HttpClientHandler();", "var w = new WebClient();"
        })
        {
            Assert.Matches(Bypass, bad);
        }
        Assert.DoesNotMatch(Bypass, "var c = NetGate.CreateClient(NetService.News, net, t);");
        Assert.DoesNotMatch(Bypass, "public static async Task<IReadOnlyList<StoreCandidate>> SearchAsync(HttpClient http, string query)");
    }

    [Fact]
    public void The_apps_clients_come_from_the_gate()
    {
        var root = Root();
        string Read(params string[] parts) => File.ReadAllText(Path.Combine([root, .. parts]));
        Assert.Contains("NetGate.CreateClient", Read("src", "CouchHud.Shell", "App.xaml.cs"));
        Assert.Contains("NetGate.CreateClient", Read("src", "CouchHud.Shell", "Hud.cs"));
        Assert.Contains("NetGate.CreateClient", Read("src", "CouchHud.Shell", "Services", "CatalogManager.cs"));
        Assert.Contains("NetGate.CreateClient", Read("src", "CouchHud.Shell", "Services", "SelfUpdateManager.cs"));
    }
}
