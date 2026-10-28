using System.Security.Cryptography;
using System.Text;

namespace CouchHud.Core.Integrations;

/// <summary>What became of a navigation to the sign-in's redirect address.</summary>
public enum HaCallbackOutcome
{
    /// <summary>Code and state fit the running sign-in: the code may be exchanged (once).</summary>
    Ok,
    /// <summary>No sign-in is running (none started, already used, cancelled).</summary>
    NoFlow,
    /// <summary>The sign-in ran longer than <see cref="HomeAssistantLogin.Lifetime"/>; it is over.</summary>
    Expired,
    /// <summary>The state is not the one of the running sign-in (another flow, a page making up a callback): ignored.</summary>
    WrongState,
    /// <summary>The redirect did not come from the Home Assistant page of this sign-in.</summary>
    WrongPage,
    /// <summary>Not exactly the expected address, or code/state missing, doubled or malformed.</summary>
    Malformed,
    /// <summary>Home Assistant (or the person) refused: error=… instead of a code.</summary>
    Denied
}

/// <summary>The result of <see cref="HomeAssistantLogin.Complete"/>: on <see cref="HaCallbackOutcome.Ok"/> the code and the address it belongs to.</summary>
public sealed record HaCallback(HaCallbackOutcome Outcome, string? Code = null, string? BaseUrl = null, string? Verifier = null, long Generation = 0)
{
    public bool Ok => Outcome == HaCallbackOutcome.Ok;

    /// <summary>Never the code or the PKCE verifier: this goes to logs.</summary>
    public override string ToString() => $"HaCallback {{ Outcome = {Outcome}, Generation = {Generation} }}";
}

/// <summary>
/// "Sign in with Home Assistant": Home Assistant's own OAuth 2 flow in IndieAuth style (Authentication API, /auth/authorize and
/// /auth/token), run in the couch browser. Pure logic, no network: the sign-in page's address, the state, and the check of the
/// redirect that carries the code.
/// <para>
/// The redirect address is never loaded. Its host is <c>couchhud.invalid</c> (RFC 6761: never resolves); the couch browser cancels
/// every navigation there in NavigationStarting, before a request goes out, and hands the address to <see cref="Complete"/>. So the code
/// never reaches a server other than the Home Assistant it came from, never shows in the address bar, the history or the recent sites,
/// and no port is opened on the PC. client_id and redirect_uri share scheme and host, so Home Assistant needs no further registration
/// (it fetches client_id only for a redirect_uri on another host).
/// </para>
/// </summary>
public sealed class HomeAssistantLogin(Func<DateTimeOffset>? clock = null)
{
    /// <summary>The host of client_id and redirect_uri; .invalid never resolves, a missed cancel ends in a DNS error, not at someone's server.</summary>
    public const string RedirectHost = "couchhud.invalid";

    /// <summary>Who signs in, as Home Assistant shows it; a URL as IndieAuth wants.</summary>
    public const string ClientId = "https://" + RedirectHost + "/";

    /// <summary>Where Home Assistant sends the code: same scheme and host as <see cref="ClientId"/>.</summary>
    public const string RedirectUri = "https://" + RedirectHost + "/auth/callback";

    /// <summary>How long one sign-in may take, from the button to the code.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>Longest code accepted (Home Assistant's are 32 hex characters).</summary>
    public const int MaxCodeLength = 512;

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Lock _gate = new();
    private string? _state;
    private string? _baseUrl;
    private string? _verifier;
    private long _generation;
    private DateTimeOffset _expires;

    /// <summary>A sign-in is running and not expired.</summary>
    public bool Pending
    {
        get
        {
            lock (_gate) return _state is not null && _clock() < _expires;
        }
    }

    /// <summary>The Home Assistant address of the running sign-in (null without one).</summary>
    public string? BaseUrl
    {
        get
        {
            lock (_gate) return _state is not null && _clock() < _expires ? _baseUrl : null;
        }
    }

    /// <summary>
    /// Starts a sign-in (a running one ends): a new state, the address of Home Assistant's sign-in page to open.
    /// Throws <see cref="ArgumentException"/> for an address that is not plain http(s).
    /// </summary>
    /// <remarks>
    /// Also a PKCE (RFC 7636, S256) verifier, kept only in memory next to the state: the code alone is worth nothing to someone who
    /// catches it. Always sent: Home Assistant's sign-in page passes unknown parameters on and its token endpoint ignores an unknown
    /// <c>code_verifier</c>, so asking the metadata endpoint first would only add a request a fake server could answer "no PKCE" to.
    /// <paramref name="generation"/> is the connection's version when the sign-in started (see <see cref="HomeAssistantSignIn"/>); it comes
    /// back with the code so a result that arrives after a sign-out or another change is thrown away.
    /// </remarks>
    public string Start(string baseUrl, long generation = 0)
    {
        var root = NormalizeBase(baseUrl) ?? throw new ArgumentException("Home Assistant address must be an absolute http(s) address", nameof(baseUrl));
        if (!SignInAllowed(root)) throw new ArgumentException(Localization.Strings.Get("ha.httpsRequired")); // no parameter name: the text goes to the person as it is
        var state = NewState();
        var verifier = NewState();
        lock (_gate)
        {
            _state = state;
            _baseUrl = root;
            _verifier = verifier;
            _generation = generation;
            _expires = _clock() + Lifetime;
        }
        return AuthorizeUrl(root, state, Challenge(verifier));
    }

    /// <summary>Ends a running sign-in (browser closed, couch locked, address changed): a late code is not taken any more.</summary>
    public void Cancel()
    {
        lock (_gate) Clear();
    }

    private void Clear()
    {
        _state = null;
        _baseUrl = null;
        _verifier = null;
    }

    /// <summary>
    /// The browser was about to load <paramref name="url"/>, a redirect address (see <see cref="IsRedirect"/>), coming from the page at
    /// <paramref name="pageUrl"/>. Ok only for exactly <see cref="RedirectUri"/> with one code and one state, the state of the running,
    /// unexpired sign-in, and the page of that sign-in's Home Assistant. A fitting state ends the sign-in whatever else is wrong
    /// (one use); a wrong state leaves it running (a page cannot end someone's sign-in by guessing).
    /// </summary>
    public HaCallback Complete(string? url, string? pageUrl)
    {
        if (!TryParseCallback(url, out var code, out var state, out var error)) return new(HaCallbackOutcome.Malformed);
        lock (_gate)
        {
            if (_state is null) return new(HaCallbackOutcome.NoFlow);
            // The state first: only the sign-in's own redirect may end it, also as "too late" (a page guessing must not close the browser).
            if (state is null || !FixedEquals(state, _state)) return new(HaCallbackOutcome.WrongState);
            if (_clock() >= _expires)
            {
                Clear();
                return new(HaCallbackOutcome.Expired);
            }
            var root = _baseUrl!;
            var verifier = _verifier!;
            Clear();
            if (!SameOrigin(pageUrl, root)) return new(HaCallbackOutcome.WrongPage);
            if (error is not null) return new(HaCallbackOutcome.Denied);
            return code is null ? new(HaCallbackOutcome.Malformed) : new(HaCallbackOutcome.Ok, code, root, verifier, _generation);
        }
    }

    /// <summary>
    /// An address for the sign-in's redirect host, in any form (http or https, any path, any port): the browser must never load it,
    /// neither as a page nor in a frame. Checked on the parsed host, so case and a trailing dot do not slip past.
    /// </summary>
    public static bool IsRedirect(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && string.Equals(uri.IdnHost.TrimEnd('.'), RedirectHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The address of Home Assistant's sign-in page: <c>{base}/auth/authorize?response_type=code&amp;client_id=…&amp;redirect_uri=…&amp;state=…</c>,
    /// with <c>code_challenge=…&amp;code_challenge_method=S256</c> when a challenge is given.
    /// </summary>
    public static string AuthorizeUrl(string baseUrl, string state, string? challenge = null)
    {
        var root = NormalizeBase(baseUrl) ?? throw new ArgumentException("Home Assistant address must be an absolute http(s) address", nameof(baseUrl));
        var url = root + "/auth/authorize?response_type=code"
            + "&client_id=" + Uri.EscapeDataString(ClientId)
            + "&redirect_uri=" + Uri.EscapeDataString(RedirectUri)
            + "&state=" + Uri.EscapeDataString(state);
        return challenge is null ? url : url + "&code_challenge=" + Uri.EscapeDataString(challenge) + "&code_challenge_method=S256";
    }

    /// <summary>The PKCE S256 challenge of a verifier: base64url(SHA-256(ASCII verifier)) without padding.</summary>
    public static string Challenge(string verifier)
    {
        var hash = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier));
        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>256 random bits, base64url without padding (43 characters).</summary>
    public static string NewState()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// The Home Assistant address as the root for its endpoints: absolute http(s), no user info, query or fragment, without a trailing
    /// slash. Null for anything else.
    /// </summary>
    public static string? NormalizeBase(string? baseUrl)
    {
        var text = (baseUrl ?? "").Trim().TrimEnd('/');
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.Host.Length == 0) return null;
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    /// <summary>
    /// The sign-in may run at this address (audit 0.5 B3): https always; plain http only to a host in the home network, because the
    /// refresh token (it never expires) goes out with the exchange and with every refresh, readable by anyone on the way. In the home
    /// network: a loopback, private (10/8, 172.16/12, 192.168/16, IPv6 fc00::/7), link-local (169.254/16, fe80::/10) or Tailscale
    /// address (100.64/10), a single-label name ("homeassistant"), localhost, or a name ending in .local (mDNS, Home Assistant's own
    /// homeassistant.local), .home.arpa, .fritz.box, .lan or .internal. Anything else (a public name or address) needs https. False for
    /// an address that is no Home Assistant address at all.
    /// </summary>
    public static bool SignInAllowed(string? baseUrl)
    {
        if (NormalizeBase(baseUrl) is not { } root || !Uri.TryCreate(root, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme == "https") return true;
        return InHomeNetwork(uri);
    }

    private static readonly string[] HomeSuffixes = [".local", ".home.arpa", ".fritz.box", ".lan", ".internal"];

    private static bool InHomeNetwork(Uri uri)
    {
        var host = uri.IdnHost.TrimEnd('.');
        // Names only the home network resolves: a single label ("homeassistant", from the router's DNS), mDNS (.local), RFC 8375
        // (.home.arpa), the usual router zones (.fritz.box, .lan) and .internal (reserved by ICANN for private use). Any other name is
        // public DNS and needs https.
        if (uri.HostNameType is UriHostNameType.Dns)
            return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || (host.Length > 0 && !host.Contains('.'))
                || HomeSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && host.Length > suffix.Length);
        if (!System.Net.IPAddress.TryParse(host.Trim('[', ']'), out var address)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (System.Net.IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                // 100.64.0.0/10 (carrier-grade NAT): Tailscale's addresses for the devices of one's own tailnet.
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
        }
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal;
        return false;
    }

    /// <summary>Same scheme, host and port: the page the redirect came from is the Home Assistant of the sign-in.</summary>
    public static bool SameOrigin(string? url, string? baseUrl) =>
        Uri.TryCreate(url, UriKind.Absolute, out var a)
        && Uri.TryCreate(baseUrl, UriKind.Absolute, out var b)
        && string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.IdnHost.TrimEnd('.'), b.IdnHost.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;

    /// <summary>
    /// The address shown in the bar while a sign-in runs: Home Assistant's page without query and fragment (the state stays off the
    /// screen and out of the recent sites). Other addresses unchanged.
    /// </summary>
    public static string Shown(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.GetLeftPart(UriPartial.Path) : url;

    /// <summary>
    /// Splits a redirect: exactly <see cref="RedirectUri"/> (https, the host, default port, the path, no user info, no fragment), at most
    /// one code, one state and one error, a code of URL-safe characters only. Other parameters (Home Assistant adds storeToken) are ignored.
    /// </summary>
    internal static bool TryParseCallback(string? url, out string? code, out string? state, out string? error)
    {
        code = state = error = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != "https" || !string.Equals(uri.IdnHost, RedirectHost, StringComparison.OrdinalIgnoreCase) || !uri.IsDefaultPort
            || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/auth/callback")
        {
            return false;
        }
        var query = uri.Query.TrimStart('?');
        if (query.Length == 0 || query.Length > 4096) return false;
        foreach (var part in query.Split('&'))
        {
            if (part.Length == 0) continue;
            var eq = part.IndexOf('=');
            var name = eq < 0 ? part : part[..eq];
            var value = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..]);
            switch (name)
            {
                case "code":
                    if (code is not null || !IsToken(value, MaxCodeLength)) return false;
                    code = value;
                    break;
                case "state":
                    if (state is not null || !IsToken(value, 256)) return false;
                    state = value;
                    break;
                case "error":
                    if (error is not null) return false;
                    error = value.Length == 0 ? "error" : value;
                    break;
            }
        }
        return state is not null && (code is not null || error is not null);
    }

    /// <summary>1 to <paramref name="max"/> characters of A-Z, a-z, 0-9 and -._~ (no spaces, no control characters, nothing to inject).</summary>
    private static bool IsToken(string value, int max) =>
        value.Length is > 0 && value.Length <= max && value.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '.' or '_' or '~');

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
