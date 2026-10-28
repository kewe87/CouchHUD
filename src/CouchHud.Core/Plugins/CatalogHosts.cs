using System.Net;
using System.Net.Sockets;

namespace CouchHud.Core.Plugins;

/// <summary>
/// Which hosts a catalog may make CouchHUD talk to. A catalog of another developer is untrusted input: an address in it must not aim the
/// request at the user's own network (a router, a printer, a service on this PC) or at a name that only resolves inside it. Only public
/// hosts, unless the developer switch is on.
/// </summary>
public static class CatalogHosts
{
    private static readonly string[] LocalSuffixes = [".local", ".localhost", ".internal", ".lan", ".home", ".home.arpa", ".corp", ".intranet", ".private"];

    /// <summary>A public address: not loopback, private, link-local, carrier-grade NAT, multicast, unspecified or unique-local.</summary>
    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.IsIPv6Multicast || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal)
            return false;
        var b = ip.GetAddressBytes();
        if (b.Length == 16)
        {
            // An IPv4 address inside an IPv6 one counts as that IPv4 address: ::a.b.c.d, NAT64 (64:ff9b::/96), 6to4 (2002::/16) and Teredo
            // (2001:0::/32, the server and the client, which is stored inverted).
            if (b[..12].All(x => x == 0)) return IsPublic(new IPAddress(b[12..]));
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b[4..12].All(x => x == 0)) return IsPublic(new IPAddress(b[12..]));
            // 64:ff9b:1::/48 (RFC 8215) is for local use: never public.
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b[4] == 0x00 && b[5] == 0x01) return false;
            // ISATAP: ...:0000:5efe:a.b.c.d or ...:0200:5efe:a.b.c.d
            if ((b[8] == 0x00 || b[8] == 0x02) && b[9] == 0x00 && b[10] == 0x5E && b[11] == 0xFE) return IsPublic(new IPAddress(b[12..]));
            if (b[0] == 0x20 && b[1] == 0x02) return IsPublic(new IPAddress(b[2..6]));
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0)
                return IsPublic(new IPAddress(b[4..8])) && IsPublic(new IPAddress(b[12..].Select(x => (byte)~x).ToArray()));
            return (b[0] & 0xFE) != 0xFC;   // unique local fc00::/7
        }
        return !(b[0] == 0 || b[0] == 10 || b[0] >= 224
                 || (b[0] == 100 && b[1] is >= 64 and <= 127)
                 || (b[0] == 169 && b[1] == 254)
                 || (b[0] == 172 && b[1] is >= 16 and <= 31)
                 || (b[0] == 192 && b[1] == 168)
                 || (b[0] == 192 && b[1] == 0 && b[2] == 0));
    }

    /// <summary>The host of a URL is a public one: an IP literal in a public range, or a name with a dot that is not a local suffix.</summary>
    public static bool IsPublicHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.IsFile) return false;
        var host = uri.IdnHost.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0) return false;
        if (IPAddress.TryParse(host, out var ip)) return IsPublic(ip);
        if (host == "localhost" || !host.Contains('.')) return false;
        return !LocalSuffixes.Any(s => host.EndsWith(s, StringComparison.Ordinal));
    }

    /// <summary>Allowed to fetch: the URL passes <see cref="CatalogParser.IsAllowedUrl"/> and (unless the developer switch is on) the host is public.</summary>
    public static bool Allowed(string url, bool developerSwitch) =>
        CatalogParser.IsAllowedUrl(url, developerSwitch) && (developerSwitch || IsPublicHost(url));

    public const int MaxRedirects = 3;

    /// <summary>
    /// GET with redirects followed by hand (at most <see cref="MaxRedirects"/>), every hop checked again like the first address. The
    /// client must not follow redirects itself (see <see cref="CreateHandler"/>); a 3xx without a usable Location is an error.
    /// </summary>
    public static async Task<HttpResponseMessage> GetAsync(HttpClient http, Uri uri, bool developerSwitch, CancellationToken ct)
    {
        for (var hop = 0; ; hop++)
        {
            if (!Allowed(uri.ToString(), developerSwitch)) throw new HttpRequestException("address not allowed");
            // A redirect never leads to a file or any other scheme: https, or (developer switch) http on this very PC.
            if (hop > 0 && !(uri.Scheme == Uri.UriSchemeHttps || (developerSwitch && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)))
                throw new HttpRequestException("address not allowed");
            HttpResponseMessage response;
            try
            {
                response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is NotSupportedException or UriFormatException or InvalidOperationException)
            {
                throw new HttpRequestException("address not allowed", ex);
            }
            if ((int)response.StatusCode is < 300 or >= 400) return response;
            var next = response.Headers.Location;
            response.Dispose();
            if (next is null || hop >= MaxRedirects) throw new HttpRequestException("too many redirects");
            try
            {
                uri = next.IsAbsoluteUri ? next : new Uri(uri, next);
            }
            catch (UriFormatException ex)
            {
                throw new HttpRequestException("address not allowed", ex);
            }
        }
    }

    /// <summary>The proxy settings of this PC (what <see cref="HttpClient.DefaultProxy"/> says); tests set their own.</summary>
    internal static System.Net.IWebProxy? SystemProxy { get; set; } = HttpClient.DefaultProxy;

    /// <summary>A request to this address would go through a proxy.</summary>
    public static bool UsesProxy(System.Net.IWebProxy? proxy, Uri target)
    {
        try
        {
            return proxy is not null && !proxy.IsBypassed(target) && proxy.GetProxy(target) is { } p && !p.Equals(target);
        }
        catch (Exception ex) when (ex is UriFormatException or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Is this endpoint the proxy that is configured for the target? Then the connection is to the proxy, not to the target.</summary>
    public static bool IsProxyEndpoint(System.Net.IWebProxy? proxy, Uri target, string host, int port)
    {
        try
        {
            if (proxy is null || proxy.IsBypassed(target)) return false;
            return proxy.GetProxy(target) is { } p && !p.Equals(target) && string.Equals(p.IdnHost, host, StringComparison.OrdinalIgnoreCase) && p.Port == port;
        }
        catch (Exception ex) when (ex is UriFormatException or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// A handler that follows no redirects and, unless the developer switch is on, connects only to public addresses (the name is resolved
    /// here and each address checked, so a public name that points into the home network is refused as well).
    /// </summary>
    public static HttpMessageHandler CreateHandler(Func<bool> developerSwitch) => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
            // Behind the system proxy the connection goes to the proxy (it may well sit in the home network); the target is then only checked
            // by its name (GetAsync). No protection against a name that is made to resolve elsewhere there: the proxy resolves it.
            var viaProxy = context.InitialRequestMessage?.RequestUri is { } target && IsProxyEndpoint(HttpClient.DefaultProxy, target, context.DnsEndPoint.Host, context.DnsEndPoint.Port);
            var usable = developerSwitch() || viaProxy ? addresses : addresses.Where(IsPublic).ToArray();
            if (usable.Length == 0) throw new HttpRequestException("address not allowed");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(usable, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    };
}
