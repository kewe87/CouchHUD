using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CouchHud.Core.Assistant;
using CouchHud.Core.Config;
using CouchHud.Core.Guard;
using CouchHud.Core.Integrations;

namespace CouchHud.Core.Tests;

public class HomeAssistantSelectionTests
{
    private static HaEntity E(string id, bool controllable = true) =>
        new(id, id[..id.IndexOf('.')], id, "on", null, null, controllable);

    [Fact]
    public void Sanitize_keeps_order_and_drops_bad_and_duplicate_ids()
    {
        var ids = HomeAssistantSelection.Sanitize(new string?[]
        {
            "scene.kino", "light.sofa", "light.sofa", null, "", "Light.Sofa", "light.a,lock.front_door", "../services/x",
            "light.sofa ", "lock.front_door", "sensor." , ".x", new string('a', 300) + ".x"
        });

        Assert.Equal(new[] { "scene.kino", "light.sofa", "lock.front_door" }, ids);
    }

    [Fact]
    public void Sanitize_caps_the_list()
    {
        var ids = HomeAssistantSelection.Sanitize(Enumerable.Range(0, 2000).Select(i => $"light.l{i}"));

        Assert.Equal(HomeAssistantSelection.MaxEntities, ids.Count);
        Assert.Equal("light.l0", ids[0]);
    }

    [Fact]
    public void Sanitize_takes_only_strings_from_a_json_array()
    {
        using var doc = JsonDocument.Parse("""["light.a", 5, {"x":1}, ["light.b"], "switch.c", null]""");
        Assert.Equal(new[] { "light.a", "switch.c" }, HomeAssistantSelection.Sanitize(doc.RootElement));

        using var notArray = JsonDocument.Parse("""{"0":"light.a"}""");
        Assert.Empty(HomeAssistantSelection.Sanitize(notArray.RootElement));
        Assert.Empty(HomeAssistantSelection.Sanitize(default(JsonElement)));
        Assert.Empty(HomeAssistantSelection.Sanitize((IEnumerable<string?>?)null));
    }

    [Fact]
    public void A_config_file_with_odd_values_reads_as_a_safe_choice()
    {
        var config = JsonSerializer.Deserialize<AppConfig>("""{"homeAssistantEntities":["light.a","LIGHT.B","light.a","x"]}""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(new[] { "light.a" }, HomeAssistantSelection.Sanitize(config.HomeAssistantEntities));

        var nulled = JsonSerializer.Deserialize<AppConfig>("""{"homeAssistantEntities":null}""", new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Empty(HomeAssistantSelection.Sanitize(nulled.HomeAssistantEntities));
        Assert.Empty(new AppConfig().HomeAssistantEntities);
    }

    [Fact]
    public void Only_chosen_entities_show_in_the_chosen_order_then_pins()
    {
        var all = new[] { E("light.a"), E("light.b"), E("scene.c"), E("switch.d"), E("sensor.e", false) };

        var scope = HomeAssistantSelection.InScope(all, ["scene.c", "light.gone", "light.a"], ["switch.d", "light.a"]);

        Assert.Equal(new[] { "scene.c", "light.a", "switch.d" }, scope.Select(e => e.Id));
        Assert.Equal(new[] { true, true, false }, scope.Select(e => e.Selected));
    }

    [Fact]
    public void Nothing_chosen_means_nothing_shown()
    {
        var all = new[] { E("light.a"), E("light.b") };
        Assert.Empty(HomeAssistantSelection.InScope(all, [], []));
        Assert.Empty(HomeAssistantSelection.InScope(all, null, null));
    }

    [Theory]
    [InlineData("light.a", true)]
    [InlineData("switch.pinned", true)]
    [InlineData("lock.front_door", false)]
    [InlineData("light.a,lock.front_door", false)]
    [InlineData("", false)]
    public void Only_chosen_or_pinned_entities_are_in_scope(string id, bool inScope) =>
        Assert.Equal(inScope, HomeAssistantSelection.IsInScope(id, ["light.a"], ["switch.pinned"]));

    [Fact]
    public void A_new_choice_drops_pins_of_entities_no_longer_chosen()
    {
        var pins = HomeAssistantSelection.PinsAfterChoice(["light.a", "switch.b", "bad id"], ["light.a"]);
        Assert.Equal(new[] { "light.a" }, pins);
    }

    [Fact]
    public void Without_a_stored_choice_the_pins_are_the_start()
    {
        // Installs from before the picker: nobody loses the guide menu entries.
        var all = new[] { E("light.a"), E("scene.kino"), E("switch.d") };
        var scope = HomeAssistantSelection.InScope(all, [], ["scene.kino", "bad id", "light.a"]);
        Assert.Equal(new[] { "scene.kino", "light.a" }, scope.Select(e => e.Id));
        Assert.All(scope, e => Assert.True(e.Selected));
        Assert.Equal(new[] { "scene.kino", "light.a" }, HomeAssistantSelection.Effective(null, ["scene.kino", "bad id", "light.a"]));
        Assert.Equal(new[] { "switch.d" }, HomeAssistantSelection.Effective(["switch.d"], ["scene.kino"]));
        Assert.True(HomeAssistantSelection.IsInScope("light.a", [], ["light.a"]));
        Assert.False(HomeAssistantSelection.IsInScope("switch.d", [], ["light.a"]));
    }

    [Fact]
    public void Suggestions_are_things_to_switch_and_never_hidden_or_technical_ones()
    {
        Assert.True(HomeAssistantSelection.IsSuggested(E("light.a"), null));
        Assert.True(HomeAssistantSelection.IsSuggested(E("scene.kino"), new HaRegistryInfo("Wohnzimmer", false)));
        Assert.True(HomeAssistantSelection.IsSuggested(E("climate.wz", false), null));
        Assert.False(HomeAssistantSelection.IsSuggested(E("light.a"), new HaRegistryInfo(null, true)));
        Assert.False(HomeAssistantSelection.IsSuggested(E("switch.child_lock"), new HaRegistryInfo(null, false, "config")));
        Assert.False(HomeAssistantSelection.IsSuggested(E("switch.led"), new HaRegistryInfo(null, false, "diagnostic")));
        Assert.False(HomeAssistantSelection.IsSuggested(E("sensor.temp", false), null));
        Assert.False(HomeAssistantSelection.IsSuggested(E("automation.x"), null));
        Assert.False(HomeAssistantSelection.IsSuggested(E("button.restart"), null));
    }

    private static Dictionary<string, HaRegistryInfo> Registry(string entities, string areas = "[]", string devices = "[]")
    {
        using var e = JsonDocument.Parse(entities);
        using var a = JsonDocument.Parse(areas);
        using var d = JsonDocument.Parse(devices);
        return HomeAssistantSelection.ParseRegistry(e.RootElement, a.RootElement, d.RootElement);
    }

    [Fact]
    public void The_registry_answer_is_parsed_defensively()
    {
        var entities = """
            {"entity_categories":{"0":"config","1":"diagnostic"},"entities":[
              {"ei":"light.sofa","ai":"wohnzimmer"},
              {"ei":"light.flur","di":"dev1","hb":true},
              {"ei":"light.own","ai":"kueche","di":"dev1"},
              {"ei":"switch.child_lock","di":"dev2","ec":0},
              {"ei":"sensor.rssi","ec":7},
              {"ei":"light.long","ai":"long"},
              {"ei":"light.nowhere","ai":"unknown"},
              {"ei":"bad id","ai":"wohnzimmer"},
              {"ai":"wohnzimmer"}, 5, "x", null,
              {"ei":"light.sofa","ai":"kueche","hb":true}
            ]}
            """;
        var areas = """[{"area_id":"wohnzimmer","name":"Wohnzimmer"},{"area_id":"kueche","name":"Küche"},{"area_id":"flur","name":"Flur"},"""
            + "{\"area_id\":\"long\",\"name\":\"\\u0007" + new string('a', 200) + "\"},{\"area_id\":5,\"name\":\"X\"},{\"name\":\"Y\"}]";
        var devices = """[{"id":"dev1","area_id":"flur"},{"id":"dev2","area_id":null},{"id":3}]""";
        var registry = Registry(entities, areas, devices);

        Assert.Equal("Wohnzimmer", registry["light.sofa"].Area);
        Assert.False(registry["light.sofa"].Hidden);
        Assert.Null(registry["light.sofa"].Category);
        Assert.Equal("Flur", registry["light.flur"].Area);
        Assert.True(registry["light.flur"].Hidden);
        Assert.Equal("Küche", registry["light.own"].Area);
        Assert.Null(registry["switch.child_lock"].Area);
        Assert.Equal("config", registry["switch.child_lock"].Category);
        Assert.Equal("other", registry["sensor.rssi"].Category);
        Assert.Equal(HomeAssistantSelection.MaxAreaLength, registry["light.long"].Area!.Length);
        Assert.DoesNotContain('\u0007', registry["light.long"].Area!);
        Assert.Null(registry["light.nowhere"].Area);
        Assert.False(registry.ContainsKey("bad id"));
        Assert.Equal(7, registry.Count);

        Assert.Empty(Registry("""{"a":1}"""));
        Assert.Empty(Registry("[]"));
        Assert.Empty(HomeAssistantSelection.ParseRegistry(default, default, default));
        // Areas and devices may be missing (their commands failed): the entities still count.
        using var only = JsonDocument.Parse("""{"entities":[{"ei":"light.a","hb":true}]}""");
        Assert.True(HomeAssistantSelection.ParseRegistry(only.RootElement, default, default)["light.a"].Hidden);
    }

    [Fact]
    public void The_catalog_carries_room_and_suggestion()
    {
        var registry = Registry(
            """{"entity_categories":{"0":"config"},"entities":[{"ei":"light.sofa","ai":"wz"},{"ei":"switch.hidden","hb":true},{"ei":"switch.lock","ec":0}]}""",
            """[{"area_id":"wz","name":"Wohnzimmer"}]""");
        var catalog = HomeAssistantSelection.Catalog([E("light.sofa"), E("switch.hidden"), E("switch.lock"), E("sensor.t", false)], registry);

        Assert.Equal("Wohnzimmer", catalog[0].Area);
        Assert.True(catalog[0].Suggested);
        Assert.False(catalog[1].Suggested);
        Assert.False(catalog[2].Suggested);
        Assert.Null(catalog[3].Area);
        Assert.False(catalog[3].Suggested);
    }

    [Theory]
    [InlineData("http://ha.local:8123", "ws://ha.local:8123/api/websocket")]
    [InlineData("https://ha.example.org/", "wss://ha.example.org/api/websocket")]
    [InlineData("http://192.168.1.5:8123/sub/", "ws://192.168.1.5:8123/sub/api/websocket")]
    [InlineData("ftp://ha.local", null)]
    [InlineData("not a url", null)]
    [InlineData("", null)]
    public void The_socket_goes_to_the_same_server(string baseUrl, string? expected) =>
        Assert.Equal(expected, HomeAssistantWebSocket.SocketUri(baseUrl)?.ToString());

    private const string RegistryEntities = """{"entity_categories":{},"entities":[{"ei":"light.a","ai":"wz"}]}""";

    [Fact]
    public async Task The_socket_sends_auth_and_three_read_commands_only()
    {
        var sent = new List<string>();
        var answer = await Converse(sent, async (send, receive) =>
        {
            await send("""{"type":"auth_required","ha_version":"2026.10"}""");
            await receive();
            await send("""{"type":"auth_ok"}""");
            for (var i = 0; i < 3; i++) await receive();
            await send("""{"type":"event","event":{}}""");
            await send("""{"id":3,"type":"result","success":true,"result":[{"id":"d","area_id":null}]}""");
            await send("""{"id":2,"type":"result","success":true,"result":[{"area_id":"wz","name":"Wohnzimmer"}]}""");
            await send("""{"id":9,"type":"result","success":true,"result":[]}""");
            await send("{\"id\":1,\"type\":\"result\",\"success\":true,\"result\":" + RegistryEntities + "}");
        });

        Assert.Equal(HaRegistryOutcome.Ok, answer.Outcome);
        Assert.Equal("Wohnzimmer", HomeAssistantSelection.ParseRegistry(answer.Entities, answer.Areas, answer.Devices)["light.a"].Area);
        Assert.Equal(4, sent.Count);
        Assert.Equal("""{"type":"auth","access_token":"secret"}""", sent[0]);
        Assert.Equal("""{"id":1,"type":"config/entity_registry/list_for_display"}""", sent[1]);
        Assert.Equal("""{"id":2,"type":"config/area_registry/list"}""", sent[2]);
        Assert.Equal("""{"id":3,"type":"config/device_registry/list"}""", sent[3]);
    }

    [Fact]
    public async Task A_refused_token_is_unauthorized_and_nothing_else_is_sent()
    {
        var sent = new List<string>();
        var answer = await Converse(sent, async (send, receive) =>
        {
            await send("""{"type":"auth_required"}""");
            await receive();
            await send("""{"type":"auth_invalid","message":"Invalid access token"}""");
        });
        Assert.Equal(HaRegistryOutcome.Unauthorized, answer.Outcome);
        Assert.Single(sent);
    }

    [Fact]
    public async Task A_failed_entity_list_or_odd_server_is_a_failure()
    {
        var failed = await Converse([], async (send, receive) =>
        {
            await send("""{"type":"auth_required"}""");
            await receive();
            await send("""{"type":"auth_ok"}""");
            for (var i = 0; i < 3; i++) await receive();
            await send("""{"id":1,"type":"result","success":false,"error":{"code":"unauthorized"}}""");
        });
        Assert.Equal(HaRegistryOutcome.Failed, failed.Outcome);

        var notHa = await Converse([], (send, _) => send("""{"hello":"world"}"""));
        Assert.Equal(HaRegistryOutcome.Failed, notHa.Outcome);

        var closed = await Converse([], (_, _) => Task.CompletedTask);
        Assert.Equal(HaRegistryOutcome.Failed, closed.Outcome);
    }

    [Fact]
    public async Task A_huge_message_is_cut_off_not_buffered()
    {
        var answer = await Converse([], async (send, receive) =>
        {
            await send("""{"type":"auth_required"}""");
            await receive();
            await send("""{"type":"auth_ok"}""");
            for (var i = 0; i < 3; i++) await receive();
            await send("{\"id\":1,\"type\":\"result\",\"success\":true,\"result\":\"" + new string('a', HomeAssistantWebSocket.MaxMessageBytes) + "\"}");
        });
        Assert.Equal(HaRegistryOutcome.Failed, answer.Outcome);
    }

    /// <summary>Runs the client side against a scripted server over a loopback TCP pair (no HTTP handshake needed).</summary>
    private static async Task<HaRegistryAnswer> Converse(List<string> sent, Func<Func<string, Task>, Func<Task>, Task> server)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var client = new TcpClient();
            var accept = listener.AcceptTcpClientAsync();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
            using var serverSide = await accept;
            using var serverSocket = WebSocket.CreateFromStream(serverSide.GetStream(), new WebSocketCreationOptions { IsServer = true });
            using var clientSocket = WebSocket.CreateFromStream(client.GetStream(), new WebSocketCreationOptions { IsServer = false });
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            var run = HomeAssistantWebSocket.RunAsync(clientSocket, "secret", cts.Token);
            var buffer = new byte[64 * 1024];
            Task Send(string text) => serverSocket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cts.Token);
            async Task Receive()
            {
                var r = await serverSocket.ReceiveAsync(buffer, cts.Token);
                sent.Add(Encoding.UTF8.GetString(buffer, 0, r.Count));
            }
            try
            {
                await server(Send, Receive);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException)
            {
                // The client gave up early (that is what some tests check).
            }
            if (!run.IsCompleted)
            {
                // Server done: close, so a client still waiting sees the end.
                try
                {
                    await serverSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
                }
                catch (Exception ex) when (ex is WebSocketException or IOException)
                {
                    // Already gone.
                }
            }
            return await run;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static HomeAssistantClient Client(HttpStatusCode status, string body, List<string> seen, List<string> sockets, HaRegistryAnswer registry) =>
        new(new HttpClient(new Answer(status, body, seen)))
        {
            BaseUrl = "http://ha.local:8123",
            Token = "t",
            FetchRegistry = (uri, token, _) =>
            {
                sockets.Add($"{uri} {token}");
                return Task.FromResult(registry);
            }
        };

    [Fact]
    public async Task The_registry_only_follows_a_token_home_assistant_accepted()
    {
        var seen = new List<string>();
        var sockets = new List<string>();
        using var entities = JsonDocument.Parse(RegistryEntities);
        using var areas = JsonDocument.Parse("""[{"area_id":"wz","name":"Küche"}]""");
        var ok = new HaRegistryAnswer(HaRegistryOutcome.Ok, entities.RootElement.Clone(), areas.RootElement.Clone());
        var client = Client(HttpStatusCode.OK, """[{"entity_id":"light.a","state":"on","attributes":{}}]""", seen, sockets, ok);

        // Not confirmed yet: no socket.
        Assert.Empty(await client.GetRegistryAsync());
        Assert.Empty(sockets);

        var (all, registry) = await client.GetCatalogAsync();
        Assert.Single(all);
        Assert.Equal("Küche", registry["light.a"].Area);
        Assert.Equal(new[] { "ws://ha.local:8123/api/websocket t" }, sockets);
        Assert.Equal(new[] { "http://ha.local:8123/api/states" }, seen);

        // A changed token is not confirmed by the old answer.
        client.Token = "other";
        Assert.Empty(await client.GetRegistryAsync());
        Assert.Single(sockets);
    }

    [Fact]
    public async Task A_refused_token_never_goes_to_the_socket_again()
    {
        var sockets = new List<string>();
        var client = Client(HttpStatusCode.OK, "[]", [], sockets, HaRegistryAnswer.Unauthorized);

        await client.GetCatalogAsync();
        await client.GetCatalogAsync();
        await client.GetCatalogAsync();
        Assert.Single(sockets);

        // A wrong token on /api/states: no socket at all.
        var denied = Client(HttpStatusCode.Unauthorized, "", [], sockets, HaRegistryAnswer.Failed);
        await Assert.ThrowsAsync<HttpRequestException>(() => denied.GetCatalogAsync());
        Assert.Single(sockets);
    }

    [Fact]
    public async Task A_slow_or_broken_registry_is_empty()
    {
        var client = Client(HttpStatusCode.OK, "[]", [], [], HaRegistryAnswer.Failed);
        await client.GetEntitiesAsync();
        Assert.Empty(await client.GetRegistryAsync());

        client.FetchRegistry = (_, _, _) => throw new WebSocketException("refused");
        Assert.Empty(await client.GetRegistryAsync());

        client.FetchRegistry = async (_, _, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return HaRegistryAnswer.Failed;
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetRegistryAsync(cts.Token));
    }

    [Fact]
    public async Task A_huge_states_answer_is_refused()
    {
        var huge = "[" + string.Join(",", Enumerable.Repeat("""{"entity_id":"light.a","state":"on"}""", HomeAssistantClient.MaxStatesBytes / 30)) + "]";
        var client = Client(HttpStatusCode.OK, huge, [], [], HaRegistryAnswer.Failed);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetEntitiesAsync());
    }

    [Theory]
    [InlineData("https://user:pw@ha.example.org:8443/lovelace?token=abc#x", "https://ha.example.org:8443/lovelace")]
    [InlineData("/auth/login", "http://ha.local:8123/auth/login")]
    [InlineData("javascript:alert(1)", "?")]
    [InlineData(null, "?")]
    public async Task A_redirect_is_shown_never_followed(string? location, string shown)
    {
        var seen = new List<string>();
        var client = new HomeAssistantClient(new HttpClient(new Redirect(location, seen))) { BaseUrl = "http://ha.local:8123", Token = "t" };
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetEntitiesAsync());
        Assert.EndsWith(shown, ex.Message);
        Assert.DoesNotContain("token=", ex.Message);
        Assert.DoesNotContain("pw", ex.Message);
        Assert.Single(seen);
    }

    [Fact]
    public void A_long_redirect_target_is_cut()
    {
        using var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri("http://ha.local/" + new string('a', 500));
        Assert.Equal(HomeAssistantClient.MaxRedirectShown + 1, HomeAssistantClient.RedirectTarget(response).Length);
    }

    private sealed class Redirect(string? location, List<string> seen) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            seen.Add(request.RequestUri!.ToString());
            var response = new HttpResponseMessage(HttpStatusCode.Found) { RequestMessage = request };
            if (location is not null) response.Headers.TryAddWithoutValidation("Location", location);
            return Task.FromResult(response);
        }
    }

    [Theory]
    [InlineData("getHomeCatalog")]
    [InlineData("setHomeSelection")]
    [InlineData("pinHomeEntity")]
    [InlineData("testHomeAssistant")]
    public void The_choice_needs_an_unlocked_couch(string method) =>
        Assert.DoesNotContain(method, LockPolicy.OpenWhileLocked);

    [Fact]
    public void Locked_the_assistant_still_cannot_switch_anything_at_home()
    {
        Assert.False(ToolCatalog.AllowedWhileLocked("activate_home_entity"));
        Assert.True(ToolCatalog.Find("list_home_entities")!.NeedsConfirmationOverMcp);
    }

    private sealed class Answer(HttpStatusCode status, string body, List<string> seen) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            seen.Add(request.RequestUri!.ToString());
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer t", request.Headers.Authorization?.ToString());
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
