using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Integrations;

public sealed record HaEntity(
    string Id,
    string Domain,
    string Name,
    string State,
    string? Unit,
    string? DeviceClass,
    bool Controllable)
{
    /// <summary>Chosen for the Smart Home tab (false: only pinned to the guide menu).</summary>
    public bool Selected { get; init; }
}

/// <summary>
/// Home Assistant through its REST API, signed in with Home Assistant's own sign-in (<see cref="Session"/>, see
/// <see cref="HomeAssistantLogin"/>) or, for experts, a long-lived access token (Profile → Security in Home Assistant).
/// Read states, toggle switches and lights, run scenes and scripts.
/// </summary>
public sealed class HomeAssistantClient(HttpClient http)
{
    /// <summary>Domains we can switch with one button press.</summary>
    private static readonly HashSet<string> Toggleable = ["light", "switch", "fan", "input_boolean", "cover", "media_player", "automation"];

    /// <summary>Domains that run something once.</summary>
    private static readonly HashSet<string> Runnable = ["scene", "script", "button", "input_button"];

    /// <summary>Domains worth showing on a TV at all.</summary>
    private static readonly HashSet<string> Shown = ["light", "switch", "fan", "input_boolean", "cover", "media_player", "scene", "script", "sensor", "binary_sensor", "climate", "button", "input_button", "automation", "weather", "lock"];

    public string? BaseUrl { get; set; }

    /// <summary>Long-lived access token (the expert way). Not used while a <see cref="Session"/> is set.</summary>
    public string? Token { get; set; }

    /// <summary>
    /// Signed in with Home Assistant (OAuth): access tokens come from here, refreshed as needed. Used only while its address is
    /// <see cref="BaseUrl"/>; a session for another address sends nothing.
    /// </summary>
    public HomeAssistantSession? Session { get; set; }

    public bool Configured => !string.IsNullOrWhiteSpace(BaseUrl)
        && (Session is { } s ? HomeAssistantPolicy.SameServer(s.BaseUrl, BaseUrl) : !string.IsNullOrWhiteSpace(Token));

    /// <summary>
    /// The client for Home Assistant alone: no redirects (the token must never follow one to another address), short timeout.
    /// </summary>
    public static HttpClient CreateHttpClient() =>
        new(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5) }) { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Largest /api/states answer we read (a home with 5000 entities and many attributes stays well below).</summary>
    public const int MaxStatesBytes = 32 * 1024 * 1024;

    /// <summary>The registry gets its own, shorter time: the picker must not hang on it.</summary>
    public static readonly TimeSpan RegistryTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Connects the registry WebSocket; replaceable for tests.</summary>
    public Func<Uri, string, CancellationToken, Task<HaRegistryAnswer>> FetchRegistry { get; set; } = HomeAssistantWebSocket.FetchAsync;

    // Address and token Home Assistant accepted (/api/states answered), and the ones the registry refused. Only a confirmed token goes
    // to the WebSocket, and a refused one never again: a wrong token counts as a failed login in Home Assistant (ip_ban).
    private CredentialKey? _confirmed;
    private CredentialKey? _registryDenied;

    // The credential the bookkeeping is about: the session (its access tokens change every half hour), compared by reference, or the
    // long-lived token.
    private readonly record struct CredentialKey(string Url, HomeAssistantSession? Session, string? Token);

    private CredentialKey Key => Session is { } s
        ? new((BaseUrl ?? "").Trim().TrimEnd('/'), s, null)
        : new((BaseUrl ?? "").Trim().TrimEnd('/'), null, Token);

    /// <summary>
    /// Whole time for one call, reading the body included: HttpClient.Timeout ends at the headers when the body is streamed
    /// (ResponseHeadersRead), and a server that trickles its answer must not hold the picker or the tab forever.
    /// </summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);

    public async Task<IReadOnlyList<HaEntity>> GetEntitiesAsync(CancellationToken ct = default)
    {
        var key = Key;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CallTimeout);
        using var response = await SendAsync(HttpMethod.Get, "/api/states", null, cts.Token);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden && _confirmed == key) _confirmed = null;
        EnsureOk(response);
        using var doc = await ReadJsonAsync(response, MaxStatesBytes, cts.Token);
        var entities = ParseStates(doc.RootElement);
        _confirmed = key;
        return entities;
    }

    /// <summary>
    /// Room, hidden flag and category per entity, from the WebSocket registry (see <see cref="HomeAssistantWebSocket"/>). Optional: an
    /// unconfirmed or refused token, a timeout or any failure is an empty map, and the picker groups by type instead.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, HaRegistryInfo>> GetRegistryAsync(CancellationToken ct = default)
    {
        var empty = new Dictionary<string, HaRegistryInfo>();
        var key = Key;
        if (!Configured || _confirmed != key || _registryDenied == key) return empty;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RegistryTimeout);
        HaRegistryAnswer answer;
        try
        {
            var (bearer, root) = await BearerAsync(cts.Token);
            if (HomeAssistantWebSocket.SocketUri(root) is not { } uri) return empty;
            answer = await FetchRegistry(uri, bearer, cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return empty;
        }
        catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or IOException or HttpRequestException or InvalidOperationException)
        {
            return empty;
        }
        if (answer.Outcome == HaRegistryOutcome.Unauthorized) _registryDenied = key;
        return answer.Outcome == HaRegistryOutcome.Ok ? HomeAssistantSelection.ParseRegistry(answer.Entities, answer.Areas, answer.Devices) : empty;
    }

    /// <summary>
    /// States and registry for the picker. With a token Home Assistant already accepted both run at once; otherwise the states go first,
    /// so a wrong token costs one failed login, not two.
    /// </summary>
    public async Task<(IReadOnlyList<HaEntity> Entities, IReadOnlyDictionary<string, HaRegistryInfo> Registry)> GetCatalogAsync(CancellationToken ct = default)
    {
        if (_confirmed == Key)
        {
            var states = GetEntitiesAsync(ct);
            var registry = GetRegistryAsync(ct);
            await Task.WhenAll(states, registry);
            return (states.Result, registry.Result);
        }
        var entities = await GetEntitiesAsync(ct);
        return (entities, await GetRegistryAsync(ct));
    }

    /// <summary>Reads at most <paramref name="maxBytes"/> of the body, growing as it goes, and parses it.</summary>
    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, int maxBytes, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > maxBytes) throw new HttpRequestException(Strings.Get("ha.tooLarge"));
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int n;
        while ((n = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + n > maxBytes) throw new HttpRequestException(Strings.Get("ha.tooLarge"));
            buffer.Write(chunk, 0, n);
        }
        buffer.Position = 0;
        return JsonDocument.Parse(buffer);
    }

    /// <summary>Toggles or runs an entity, depending on its domain.</summary>
    public async Task ActivateAsync(string entityId, CancellationToken ct = default)
    {
        if (!HomeAssistantPolicy.IsValidEntityId(entityId)) throw new InvalidOperationException(Strings.Get("ha.invalidEntity"));
        var domain = entityId.Split('.')[0];
        var (serviceDomain, service) = domain switch
        {
            _ when Toggleable.Contains(domain) && domain is "cover" => ("cover", "toggle"),
            _ when Toggleable.Contains(domain) => ("homeassistant", "toggle"),
            "scene" or "script" => (domain, "turn_on"),
            "button" or "input_button" => (domain, "press"),
            _ => throw new InvalidOperationException(Strings.Get("ha.cannotToggle", entityId))
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CallTimeout);
        using var response = await SendAsync(HttpMethod.Post, $"/api/services/{serviceDomain}/{service}", new { entity_id = entityId }, cts.Token);
        EnsureOk(response);
    }

    internal static IReadOnlyList<HaEntity> ParseStates(JsonElement root)
    {
        var result = new List<HaEntity>();
        foreach (var e in root.EnumerateArray())
        {
            var id = e.GetProperty("entity_id").GetString() ?? "";
            var domain = id.Split('.')[0];
            if (!Shown.Contains(domain)) continue;
            var state = e.TryGetProperty("state", out var s) ? s.GetString() ?? "" : "";
            if (state is "unavailable") continue;
            var attributes = e.TryGetProperty("attributes", out var a) ? a : default;
            string? Attr(string name) =>
                attributes.ValueKind == JsonValueKind.Object && attributes.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            result.Add(new HaEntity(
                id,
                domain,
                Attr("friendly_name") ?? id,
                state,
                Attr("unit_of_measurement"),
                Attr("device_class"),
                Toggleable.Contains(domain) || Runnable.Contains(domain)));
        }
        return result.OrderBy(e => DomainOrder(e.Domain)).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static int DomainOrder(string domain) => domain switch
    {
        "scene" => 0,
        "light" => 1,
        "switch" or "input_boolean" or "fan" => 2,
        "media_player" or "cover" or "lock" => 3,
        "climate" => 4,
        "sensor" or "binary_sensor" or "weather" => 5,
        _ => 6
    };

    /// <summary>
    /// The token for the Authorization header: the session's access token (fetched anew when it runs out) or the long-lived one.
    /// Throws when nothing is set up, or the session belongs to another address. Also returns the address the token is for: a session's
    /// own, checked again after the (possibly slow) refresh, so a token never goes to an address that was changed meanwhile.
    /// </summary>
    private async Task<(string Bearer, string BaseUrl)> BearerAsync(CancellationToken ct)
    {
        if (!Configured) throw new InvalidOperationException(Strings.Get("ha.notConfigured"));
        if (Session is not { } session) return (Token!, BaseUrl!);
        var bearer = await session.AccessTokenAsync(ct);
        if (!ReferenceEquals(Session, session) || !HomeAssistantPolicy.SameServer(session.BaseUrl, BaseUrl))
            throw new InvalidOperationException(Strings.Get("ha.notConfigured"));
        return (bearer, session.BaseUrl);
    }

    /// <summary>
    /// One call. Signed in, a 401 means the access token was refused before its time (Home Assistant restarted, token revoked): the
    /// call is made once more with a fresh one (Home Assistant refuses before acting, so nothing runs twice).
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var (bearer, root) = await BearerAsync(ct);
            using var request = new HttpRequestMessage(method, new Uri(new Uri(root.TrimEnd('/') + "/"), path.TrimStart('/')));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            if (body is not null) request.Content = JsonContent.Create(body);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode != System.Net.HttpStatusCode.Unauthorized || Session is not { } session || attempt > 0) return response;
            session.Invalidate(bearer);
            response.Dispose();
        }
    }

    private static void EnsureOk(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var code = (int)response.StatusCode;
        var reason = code switch
        {
            >= 300 and < 400 => Strings.Get("ha.redirect", RedirectTarget(response)),
            401 => Strings.Get("ha.tokenInvalid"),
            404 => Strings.Get("ha.badAddress"),
            _ => Strings.Get("ha.status", (int)response.StatusCode)
        };
        throw new HttpRequestException(reason, null, response.StatusCode);
    }

    /// <summary>Longest redirect target shown to the owner.</summary>
    public const int MaxRedirectShown = 120;

    /// <summary>
    /// Where Home Assistant sends us, only to show it (never followed): scheme, host, port and path, without user info, query or
    /// fragment (a token or code there stays out of the UI), no control characters, cut to <see cref="MaxRedirectShown"/>.
    /// </summary>
    internal static string RedirectTarget(HttpResponseMessage response)
    {
        var location = response.Headers.Location;
        if (location is null) return "?";
        if (!location.IsAbsoluteUri && response.RequestMessage?.RequestUri is { } from) location = new Uri(from, location);
        if (!location.IsAbsoluteUri || location.Scheme is not ("http" or "https")) return "?";
        var shown = location.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.Unescaped);
        shown = new string(shown.Where(c => !char.IsControl(c)).ToArray());
        return shown.Length > MaxRedirectShown ? shown[..MaxRedirectShown] + "…" : shown;
    }
}
