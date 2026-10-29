using System.Net;
using CouchHud.Core.Config;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Net;

/// <summary>The outbound services that Settings › Privacy switches one by one.</summary>
public enum NetService
{
    /// <summary>The GitHub check for a newer CouchHUD (SelfUpdateCheck).</summary>
    Updates,
    /// <summary>Plugin and skin catalog: index, update search, package and media downloads (NetCatalog).</summary>
    Catalog,
    /// <summary>CouchHUD's own search for Windows and app updates (NetSystemUpdates).</summary>
    SystemUpdates,
    /// <summary>Steam store, Steam CDN, SteamGridDB: cover art and game info (OnlineArtwork).</summary>
    Artwork,
    /// <summary>The list of save locations for save-game detection (NetSaves).</summary>
    Saves,
    /// <summary>
    /// Descriptions and age ratings of the Steam store, and the lookup of store ids. Follows the artwork switch, except that parental controls keep it
    /// on (the age ratings keep the limits from getting looser); Offline stops it.
    /// </summary>
    GameInfo,
    /// <summary>The weather service (NetWeather).</summary>
    Weather,
    /// <summary>News feeds (NetNews).</summary>
    News,
    /// <summary>The AI assistant's cloud provider (NetAssistant).</summary>
    Assistant,
    /// <summary>The clock check of the parental controls. No switch of its own: only Offline stops it (the limits then count by the PC's clock).</summary>
    Time
}

/// <summary>Raised when the privacy settings do not allow a connection. A HttpRequestException, so every caller that survives a network failure survives this too.</summary>
public sealed class NetBlockedException(NetService service, string host, bool offline)
    : HttpRequestException(Strings.Get(offline ? "net.blockedOffline" : "net.blockedService", host))
{
    public NetService Service { get; } = service;
    public string Host { get; } = host;
    public bool Offline { get; } = offline;
}

/// <summary>
/// What may go to the internet right now, read live from the settings (a switch takes effect for the next request, no restart).
/// The local network (loopback, private, link-local, unique-local addresses) is never covered by a switch.
/// </summary>
public sealed class NetPolicy(Func<AppConfig> config)
{
    public bool Offline => config().Offline;

    public bool Allows(NetService service) => Allows(config(), service);

    /// <summary>The decision as a pure function of the settings.</summary>
    public static bool Allows(AppConfig c, NetService service)
    {
        if (c.Offline) return false;
        return service switch
        {
            NetService.Updates => c.SelfUpdateCheck,
            NetService.Catalog => c.NetCatalog,
            NetService.SystemUpdates => c.NetSystemUpdates,
            NetService.Artwork => c.OnlineArtwork,
            NetService.GameInfo => c.OnlineArtwork || c.ParentalEnabled,
            NetService.Saves => c.NetSaves,
            NetService.Weather => c.NetWeather,
            NetService.News => c.NetNews,
            NetService.Assistant => c.NetAssistant,
            NetService.Time => true,
            _ => false
        };
    }
}

/// <summary>
/// The one gate every outbound HTTP request of CouchHUD goes through (the bypass scan in the tests keeps it that way). Before each request, and
/// before each redirect it follows itself, it asks <see cref="NetPolicy"/>: when the service is on the request goes on as it is; when it is off
/// (or Offline) only a destination in the local network goes on. A name is resolved here and every address must be local, so a public host is
/// not let through because its name looks local; the connection itself is checked again where the handler connects (see <see cref="CreateHandler"/>).
/// </summary>
public sealed class NetGateHandler : DelegatingHandler
{
    public const int MaxRedirects = 5;

    private readonly NetService _service;
    private readonly NetPolicy _policy;
    private readonly bool _followRedirects;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;

    /// <param name="inner">The handler below. With <paramref name="followRedirects"/> it must not follow redirects itself.</param>
    /// <param name="followRedirects">The gate follows redirects of GET and HEAD requests hop by hop, each hop checked. Off: the caller does (and sends the next hop through the gate again).</param>
    /// <param name="resolve">Name resolution (tests replace it).</param>
    public NetGateHandler(NetService service, NetPolicy policy, HttpMessageHandler inner, bool followRedirects = false,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null) : base(inner)
    {
        _service = service;
        _policy = policy;
        _followRedirects = followRedirects;
        _resolve = resolve ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var current = request;
        for (var hop = 0; ; hop++)
        {
            await CheckAsync(current.RequestUri, cancellationToken).ConfigureAwait(false);
            var response = await base.SendAsync(current, cancellationToken).ConfigureAwait(false);
            if (!_followRedirects || !IsRedirect(response.StatusCode) || response.Headers.Location is not { } location
                || (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head))
            {
                return response;
            }
            if (hop >= MaxRedirects) return response;
            var target = current.RequestUri is { IsAbsoluteUri: true } from ? new Uri(from, location) : location;
            if (target.Scheme is not ("http" or "https") || (current.RequestUri?.Scheme == "https" && target.Scheme == "http"))
            {
                return response;
            }
            response.Dispose();
            var next = new HttpRequestMessage(request.Method, target) { Version = request.Version, VersionPolicy = request.VersionPolicy };
            var sameHost = SameHost(request.RequestUri, target);
            foreach (var header in request.Headers)
            {
                // Another host gets only headers that say what is wanted, never what identifies or authorizes the caller (Authorization, Cookie,
                // API keys and anything else a caller put there for the first host).
                if (!sameHost && !SafeOnRedirect.Contains(header.Key)) continue;
                next.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            current = next;
        }
    }

    private static readonly HashSet<string> SafeOnRedirect = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept", "Accept-Language", "Accept-Charset", "User-Agent", "If-None-Match", "If-Modified-Since", "If-Range", "Range"
    };

    private static bool IsRedirect(HttpStatusCode code) => code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static bool SameHost(Uri? a, Uri b) => a is not null && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;

    /// <summary>Throws <see cref="NetBlockedException"/> when this destination is not allowed now.</summary>
    internal async Task CheckAsync(Uri? uri, CancellationToken ct)
    {
        if (uri is null || !uri.IsAbsoluteUri) throw new NetBlockedException(_service, "?", _policy.Offline);
        if (uri.Scheme is not ("http" or "https" or "ws" or "wss")) throw new NetBlockedException(_service, uri.Scheme, _policy.Offline);
        if (_policy.Allows(_service)) return;
        if (!await NetGate.IsLocalDestinationAsync(uri.IdnHost, _resolve, ct).ConfigureAwait(false))
        {
            throw new NetBlockedException(_service, uri.IdnHost, _policy.Offline);
        }
    }
}

/// <summary>Factories for the gate and the rule for what counts as the local network.</summary>
public static class NetGate
{
    /// <summary>
    /// A client for one service. <paramref name="inner"/>: a handler that does the connecting (SafeNet's public-only one, the catalog's); null for the
    /// default, which follows redirects through the gate (<paramref name="followRedirects"/> false: the caller does, hop by hop, each hop through the gate again) and, when the service is off, connects to local addresses only. Everything is wrapped in the gate.
    /// </summary>
    public static HttpClient CreateClient(NetService service, NetPolicy policy, TimeSpan timeout, HttpMessageHandler? inner = null, string? userAgent = null, bool polite = false, bool followRedirects = true)
    {
        var client = new HttpClient(CreateHandler(service, policy, inner, polite, followRedirects)) { Timeout = timeout };
        if (userAgent is not null) client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return client;
    }

    /// <summary>The gate over <paramref name="inner"/> (or the default handler), with the polite handler between for third-party services.</summary>
    public static HttpMessageHandler CreateHandler(NetService service, NetPolicy policy, HttpMessageHandler? inner = null, bool polite = false, bool followRedirects = true)
    {
        // With a handler of the caller the caller decides about redirects (the handlers given here never follow them).
        followRedirects &= inner is null;
        HttpMessageHandler below = inner ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            // No system proxy: behind one the connection goes to the proxy, so the check of the address in ConnectAsync would be looking at the
            // proxy and not at the target and could not be trusted. Without it the check always sees the real destination.
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectCallback = (context, ct) => ConnectAsync(context, ct, () => policy.Allows(service))
        };
        if (polite) below = new PoliteHandler(below, Plugins.CatalogRules.AppVersion.ToString());
        return new NetGateHandler(service, policy, below, followRedirects);
    }

    /// <summary>The connection of the default handler: resolves the name and, when the service is off, takes local addresses only (a name that moved after the check gets nowhere).</summary>
    private static async ValueTask<Stream> ConnectAsync(System.Net.Http.SocketsHttpConnectionContext context, CancellationToken ct, Func<bool> allowed)
    {
        var host = context.DnsEndPoint.Host.Trim('[', ']');
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        var usable = allowed() ? addresses : addresses.Where(LanOnly.IsAllowed).ToArray();
        if (usable.Length == 0) throw new HttpRequestException($"{host}: address not allowed");
        var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(usable, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
            return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Is this host in the local network: an address literal that is loopback, private, link-local or unique-local, or a name whose every address is.
    /// A name that does not resolve is not local (nothing is let through on a guess), and neither is a name that has one public address among its answers.
    /// </summary>
    public static async Task<bool> IsLocalDestinationAsync(string host, Func<string, CancellationToken, Task<IPAddress[]>> resolve, CancellationToken ct)
    {
        host = host.Trim('[', ']');
        var zone = host.IndexOf('%');
        if (zone > 0) host = host[..zone];
        if (IPAddress.TryParse(host, out var literal)) return LanOnly.IsAllowed(literal);
        try
        {
            var addresses = await resolve(host, ct).ConfigureAwait(false);
            return addresses.Length > 0 && addresses.All(LanOnly.IsAllowed);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}
