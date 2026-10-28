using System.Net;
using System.Text.Json;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Integrations;

/// <summary>What /auth/token gave: an access token for the API, and (for a code) the refresh token to keep.</summary>
public sealed record HaTokens(string AccessToken, string? RefreshToken, TimeSpan ExpiresIn)
{
    /// <summary>Never the tokens: this may end up in a log.</summary>
    public override string ToString() => $"HaTokens {{ ExpiresIn = {ExpiresIn}, Refresh = {RefreshToken is not null} }}";
}

/// <summary>
/// Home Assistant's token endpoint (Authentication API): the code from the sign-in for access and refresh token, a new access token for
/// the refresh token, and revoking at sign-out. Always through the HttpClient of <see cref="HomeAssistantClient.CreateHttpClient"/>
/// (no redirects, the system's TLS check, short timeouts), always to the Home Assistant address the sign-in ran against, never with a
/// token or code in an error text.
/// </summary>
public sealed class HomeAssistantAuth(HttpClient http)
{
    /// <summary>Largest token answer read (Home Assistant's is well below 2 KB).</summary>
    public const int MaxAnswerBytes = 64 * 1024;

    /// <summary>Longest token accepted (Home Assistant's access tokens are JWTs of a few hundred characters).</summary>
    public const int MaxTokenLength = 8192;

    /// <summary>An expires_in beyond this is cut to it: the access token is fetched again at the latest then.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(24);

    /// <summary>Whole time for one call, the body included.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The code from the sign-in for an access and a refresh token (grant_type=authorization_code), with the PKCE verifier of the
    /// sign-in when there is one (Home Assistant versions that do not check PKCE ignore it).
    /// </summary>
    public async Task<HaTokens> ExchangeCodeAsync(string baseUrl, string code, string? verifier = null, CancellationToken ct = default)
    {
        KeyValuePair<string, string>[] form = verifier is null
            ? [new("grant_type", "authorization_code"), new("code", code), new("client_id", HomeAssistantLogin.ClientId)]
            : [new("grant_type", "authorization_code"), new("code", code), new("client_id", HomeAssistantLogin.ClientId), new("code_verifier", verifier)];
        var tokens = await PostTokenAsync(baseUrl, form, ct);
        if (tokens.RefreshToken is null) throw new HttpRequestException(Strings.Get("ha.signInBadAnswer"));
        return tokens;
    }

    /// <summary>A new access token for the refresh token (grant_type=refresh_token). A refused refresh token: "sign in again".</summary>
    public Task<HaTokens> RefreshAsync(string baseUrl, string refreshToken, CancellationToken ct = default) =>
        PostTokenAsync(baseUrl, [
            new("grant_type", "refresh_token"),
            new("refresh_token", refreshToken),
            new("client_id", HomeAssistantLogin.ClientId)
        ], ct);

    /// <summary>
    /// Revokes the refresh token (and every access token made from it). /auth/revoke first; Home Assistant versions without it get the
    /// older form on /auth/token with action=revoke. Home Assistant answers 200 either way; true when one of them did.
    /// </summary>
    public async Task<bool> RevokeAsync(string baseUrl, string refreshToken, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CallTimeout);
        using (var response = await PostAsync(baseUrl, "/auth/revoke", [new("token", refreshToken)], cts.Token))
        {
            if (response.IsSuccessStatusCode) return true;
            if (response.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)) return false;
        }
        using var old = await PostAsync(baseUrl, "/auth/token", [new("token", refreshToken), new("action", "revoke")], cts.Token);
        return old.IsSuccessStatusCode;
    }

    private async Task<HaTokens> PostTokenAsync(string baseUrl, KeyValuePair<string, string>[] form, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CallTimeout);
        using var response = await PostAsync(baseUrl, "/auth/token", form, cts.Token);
        var code = (int)response.StatusCode;
        if (code is >= 300 and < 400) throw new HttpRequestException(Strings.Get("ha.redirect", HomeAssistantClient.RedirectTarget(response)), null, response.StatusCode);
        var body = await ReadAsync(response, cts.Token);
        if (!response.IsSuccessStatusCode)
        {
            // 400 invalid_grant: code used, expired or from another client; refresh token revoked. 400 invalid_request: code or
            // verifier missing or not accepted. 403: user deactivated. Asking again with the same would only collect failed logins
            // (Home Assistant can ban the address for that): "sign in again".
            var error = ErrorCode(body);
            if (code is 400 or 401 or 403 && error is "invalid_grant" or "invalid_request" or "access_denied" or "invalid_client")
                throw new HttpRequestException(Strings.Get("ha.signInAgain"), null, HttpStatusCode.Unauthorized);
            throw new HttpRequestException(Strings.Get("ha.status", code), null, response.StatusCode);
        }
        return ParseTokens(body) ?? throw new HttpRequestException(Strings.Get("ha.signInBadAnswer"));
    }

    private async Task<HttpResponseMessage> PostAsync(string baseUrl, string path, KeyValuePair<string, string>[] form, CancellationToken ct)
    {
        var root = HomeAssistantLogin.NormalizeBase(baseUrl) ?? throw new InvalidOperationException(Strings.Get("ha.notConfigured"));
        // Code, refresh token and revoke never go out in clear text beyond the home network (audit 0.5 B3; HomeAssistantLogin.SignInAllowed).
        // Refused like a revoked token (401): a session over http to a public address ends at once instead of trying on every call.
        if (!HomeAssistantLogin.SignInAllowed(root)) throw new HttpRequestException(Strings.Get("ha.httpsRequired"), null, HttpStatusCode.Unauthorized);
        using var request = new HttpRequestMessage(HttpMethod.Post, root + path) { Content = new FormUrlEncodedContent(form) };
        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static async Task<byte[]> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > MaxAnswerBytes) throw new HttpRequestException(Strings.Get("ha.tooLarge"));
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int n;
        while ((n = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + n > MaxAnswerBytes) throw new HttpRequestException(Strings.Get("ha.tooLarge"));
            buffer.Write(chunk, 0, n);
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// A token answer: access_token (required), token_type Bearer (if given), expires_in in seconds (positive, cut to
    /// <see cref="MaxLifetime"/>; missing: 30 minutes as Home Assistant's), refresh_token (optional). Tokens of printable ASCII only,
    /// at most <see cref="MaxTokenLength"/>. Null for anything else.
    /// </summary>
    internal static HaTokens? ParseTokens(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("access_token", out var a) || a.ValueKind != JsonValueKind.String || !IsToken(a.GetString())) return null;
            if (root.TryGetProperty("token_type", out var type)
                && (type.ValueKind != JsonValueKind.String || !string.Equals(type.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase)))
            {
                return null;
            }
            var expires = TimeSpan.FromMinutes(30);
            if (root.TryGetProperty("expires_in", out var e))
            {
                if (e.ValueKind != JsonValueKind.Number || !e.TryGetDouble(out var seconds) || !(seconds > 0)) return null;
                expires = seconds >= MaxLifetime.TotalSeconds ? MaxLifetime : TimeSpan.FromSeconds(seconds);
            }
            string? refresh = null;
            if (root.TryGetProperty("refresh_token", out var r) && r.ValueKind != JsonValueKind.Null)
            {
                if (r.ValueKind != JsonValueKind.String || !IsToken(r.GetString())) return null;
                refresh = r.GetString();
            }
            return new HaTokens(a.GetString()!, refresh, expires);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The "error" of an OAuth error answer, only if it is a plain lower-case word (it is compared, never shown).</summary>
    internal static string? ErrorCode(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
            {
                var text = e.GetString() ?? "";
                return text.Length is > 0 and <= 40 && text.All(c => c is (>= 'a' and <= 'z') or '_') ? text : null;
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static bool IsToken(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= MaxTokenLength && value.All(c => c is > ' ' and < (char)127);
}

/// <summary>
/// A signed-in Home Assistant: the refresh token (kept by the host, encrypted) and the access token made from it, held in memory only
/// and fetched again shortly before it runs out or after Home Assistant refused it. One refresh at a time; callers wait for it.
/// Once Home Assistant refused the refresh token the session is <see cref="Dead"/>: no request goes out any more (repeated failed
/// logins can get the PC's address banned), every call fails with "sign in again" until the person signs in anew.
/// </summary>
public sealed class HomeAssistantSession(HomeAssistantAuth auth, string baseUrl, string refreshToken, Func<DateTimeOffset>? clock = null)
{
    /// <summary>A token this close to its end is fetched anew (a call must not start with one that runs out on the way).</summary>
    public static readonly TimeSpan Margin = TimeSpan.FromMinutes(1);

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private string? _access;
    private DateTimeOffset _until;
    private volatile bool _dead;

    /// <summary>Home Assistant refused the refresh token: no further request, the person has to sign in again.</summary>
    public bool Dead => _dead;

    /// <summary>The Home Assistant this session belongs to (normalized); its tokens go nowhere else.</summary>
    public string BaseUrl { get; } = HomeAssistantLogin.NormalizeBase(baseUrl) ?? throw new ArgumentException("Home Assistant address", nameof(baseUrl));

    /// <summary>The refresh token: for the host to store (encrypted) and to revoke. Never logged.</summary>
    public string RefreshToken { get; } = refreshToken;

    /// <summary>Starts with the access token the sign-in gave (no second request right after it).</summary>
    public void Seed(HaTokens tokens)
    {
        _access = tokens.AccessToken;
        _until = _clock() + tokens.ExpiresIn;
    }

    /// <summary>A valid access token, refreshed when needed.</summary>
    public async Task<string> AccessTokenAsync(CancellationToken ct = default)
    {
        ThrowIfDead();
        if (Fresh() is { } token) return token;
        await _refresh.WaitAsync(ct);
        try
        {
            ThrowIfDead();
            if (Fresh() is { } again) return again;
            HaTokens tokens;
            try
            {
                tokens = await auth.RefreshAsync(BaseUrl, RefreshToken, ct);
            }
            catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.Unauthorized)
            {
                _dead = true;
                _access = null;
                throw;
            }
            _access = tokens.AccessToken;
            _until = _clock() + tokens.ExpiresIn;
            return _access;
        }
        finally
        {
            _refresh.Release();
        }
    }

    /// <summary>Home Assistant refused the access token (401): the next call fetches a new one.</summary>
    public void Invalidate(string? refused = null)
    {
        if (refused is null || refused == _access) _access = null;
    }

    private void ThrowIfDead()
    {
        // An address that needs https says so (sign in again at the https address), every other end says "sign in again".
        if (_dead) throw new HttpRequestException(Strings.Get(NeedsHttps ? "ha.httpsRequired" : "ha.signInAgain"), null, HttpStatusCode.Unauthorized);
    }

    /// <summary>The session runs over http outside the home network (from before 0.5): it is never refreshed (audit 0.5 B3).</summary>
    public bool NeedsHttps => !HomeAssistantLogin.SignInAllowed(BaseUrl);

    private string? Fresh() => _access is { } token && _clock() + Margin < _until ? token : null;

    /// <summary>Never the tokens.</summary>
    public override string ToString() => $"HomeAssistantSession {{ BaseUrl = {BaseUrl} }}";
}
