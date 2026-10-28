using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Integrations.CaseHud;

/// <summary>
/// CaseHUD API v1 on 127.0.0.1. Every request carries X-Token and the Host header 127.0.0.1:port; the token is never
/// logged or put into an error text. Errors come as <see cref="CaseHudException"/> (code and text of the API error format).
/// </summary>
public sealed class CaseHudClient(HttpClient http, CaseHudApiInfo info)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    public const long MaxJsonBytes = 2 * 1024 * 1024;
    public const long MaxImageBytes = 4 * 1024 * 1024;

    private Uri Url(string path) => new($"http://127.0.0.1:{info.Port}/api/v1/{path.TrimStart('/')}");

    /// <summary>Ids come from the API, but they end up in a URL path: no empty ones, no "..", no separators.</summary>
    private static string Id(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id is "." or ".." || id.Any(char.IsControl))
            throw new CaseHudException("bad_request", Strings.Get("casehud.badId"));
        return Uri.EscapeDataString(id);
    }

    public Task<CaseHudStatus> StatusAsync(CancellationToken ct = default) => Send<CaseHudStatus>(HttpMethod.Get, "status", null, ct);
    public Task<List<CaseHudPanel>> PanelsAsync(CancellationToken ct = default) => Send<List<CaseHudPanel>>(HttpMethod.Get, "panels", null, ct);

    public Task<CaseHudPanel> PatchPanelAsync(string id, CaseHudPanelPatch patch, CancellationToken ct = default)
    {
        // A true patch: only what was given goes over the wire.
        var body = new Dictionary<string, object>();
        if (patch.Mode is not null) body["mode"] = patch.Mode;
        if (patch.Brightness is { } b) body["brightness"] = Math.Clamp(b, 0, 100);
        if (patch.Theme is not null) body["theme"] = patch.Theme;
        return Send<CaseHudPanel>(HttpMethod.Patch, $"panels/{Id(id)}", body, ct);
    }

    public Task IdentifyAsync(string? panelId, CancellationToken ct = default) =>
        Send<JsonElement?>(HttpMethod.Post, panelId is null ? "identify" : $"panels/{Id(panelId)}/identify", null, ct);

    public Task<List<CaseHudSet>> SetsAsync(CancellationToken ct = default) => Send<List<CaseHudSet>>(HttpMethod.Get, "sets", null, ct);

    public Task ApplySetAsync(string set, string theme, IReadOnlyList<string>? panels = null, CancellationToken ct = default) =>
        Send<JsonElement?>(HttpMethod.Post, "sets/apply", panels is null ? new { set, theme } : new { set, theme, panels }, ct);

    public Task<List<CaseHudScene>> ScenesAsync(CancellationToken ct = default) => Send<List<CaseHudScene>>(HttpMethod.Get, "scenes", null, ct);
    public Task ActivateSceneAsync(string id, CancellationToken ct = default) => Send<JsonElement?>(HttpMethod.Post, $"scenes/{Id(id)}/activate", null, ct);
    public Task SetRotationAsync(bool enabled, CancellationToken ct = default) => Send<JsonElement?>(HttpMethod.Patch, "rotation", new { enabled }, ct);

    /// <summary>The automation on or off (its rules stay; they are edited in CaseHUD's window). Older builds answer 404 or 405.</summary>
    public Task SetAutomationAsync(bool enabled, CancellationToken ct = default) => Send<JsonElement?>(HttpMethod.Patch, "automation", new { enabled }, ct);
    public Task NextSceneAsync(CancellationToken ct = default) => Send<JsonElement?>(HttpMethod.Post, "rotation/next", null, ct);
    public Task PowerAsync(bool on, CancellationToken ct = default) => Send<JsonElement?>(HttpMethod.Post, "power", new { on }, ct);

    /// <summary>Gaming lease: stays valid for <paramref name="leaseSeconds"/>; active=false ends it at once.</summary>
    public Task GamingAsync(bool active, string? game = null, int? pid = null, int? leaseSeconds = null, CancellationToken ct = default) =>
        Send<JsonElement?>(HttpMethod.Post, "gaming", active ? new { active, game, pid, leaseSeconds } : new { active }, ct);

    public Task SetAwtrixAsync(bool enabled, CancellationToken ct = default) => Send<JsonElement?>(HttpMethod.Patch, "awtrix", new { enabled }, ct);

    /// <summary>Lighting (CaseHUD 1.0, only when the status carries <c>lighting</c>).</summary>
    public async Task<CaseHudLightingState> LightingAsync(CancellationToken ct = default) =>
        CaseHudLighting.Parse(await Send<JsonElement>(HttpMethod.Get, "lighting", null, ct))
        ?? throw new CaseHudException("bad_response", Strings.Get("casehud.badResponse"));

    /// <summary>A PATCH body built by <see cref="CaseHudLighting.BuildPatch"/>; 409 ack_required and 403 gui_only come back as <see cref="CaseHudException"/>. Null for an empty or non-object answer.</summary>
    public async Task<CaseHudLightingState?> PatchLightingAsync(IReadOnlyDictionary<string, object> body, CancellationToken ct = default) =>
        CaseHudLighting.Parse(await Send<JsonElement>(HttpMethod.Patch, "lighting", body, ct));

    /// <summary>Opens or focuses CaseHUD's window (202). Older CaseHUD versions answer 404 or 405 (see <see cref="CaseHudException.Status"/>).</summary>
    public Task OpenGuiAsync(CancellationToken ct = default) => Send<JsonElement?>(HttpMethod.Post, "gui/open", null, ct);

    /// <summary>Asks CaseHUD to end itself (before an update). A dropped connection counts as success.</summary>
    public async Task QuitAsync(CancellationToken ct = default)
    {
        try { await Send<JsonElement?>(HttpMethod.Post, "quit", null, ct); }
        catch (CaseHudException ex) when (ex.Code == "unreachable") { }
    }

    /// <summary>Live image of a display as PNG bytes (the only preview the couch shows).</summary>
    public async Task<byte[]> PanelPreviewAsync(string id, int? size = null, CancellationToken ct = default)
    {
        var path = $"panels/{Id(id)}/preview.png" + (size is { } s ? $"?size={Math.Clamp(s, 32, 480)}" : "");
        return await Fetch(HttpMethod.Get, path, null, MaxImageBytes, ct);
    }

    private async Task<T> Send<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var bytes = await Fetch(method, path, body, MaxJsonBytes, ct);
        if (bytes.Length == 0) return default!;
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, CaseHudJson.Options)!;
        }
        catch (JsonException)
        {
            throw new CaseHudException("bad_response", Strings.Get("casehud.badResponse"));
        }
    }

    /// <summary>
    /// One request and its body. Only the headers are awaited before the body is read, so the size limit holds while it streams
    /// (an oversized answer is never buffered whole); the timeout covers the request and the body together.
    /// </summary>
    private async Task<byte[]> Fetch(HttpMethod method, string path, object? body, long limit, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var request = new HttpRequestMessage(method, Url(path));
        Authorize(request);
        if (body is not null) request.Content = JsonContent.Create(body, options: CaseHudJson.Options);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) throw await ReadError(response, timeout.Token);
            return await ReadLimited(response, limit, timeout.Token);
        }
        catch (Exception ex) when ((ex is HttpRequestException or OperationCanceledException or IOException) && !ct.IsCancellationRequested)
        {
            throw new CaseHudException("unreachable", Strings.Get("casehud.unreachable"));
        }
    }

    /// <summary>Token and Host header; also used by the event stream.</summary>
    internal void Authorize(HttpRequestMessage request)
    {
        request.Headers.Add("X-Token", info.Token);
        request.Headers.Host = $"127.0.0.1:{info.Port}";
    }

    internal Uri EventsUrl => Url("events");

    /// <summary>
    /// The error of a refused request. 403 <c>api_disabled</c> (the API is switched off in CaseHUD; also with an old token) becomes the
    /// hint where to switch it on; CaseHUD's own message is not shown for it. <c>unauthorized</c>, <c>forbidden</c> and 404 <c>not_found</c>
    /// get our own text in the user's language; CaseHUD's English message for them only goes to the log (cleaned and capped).
    /// </summary>
    internal static async Task<CaseHudException> ReadError(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        try
        {
            var bytes = await ReadLimited(response, 16384, ct);
            using var doc = JsonDocument.Parse(bytes);
            var error = doc.RootElement.GetProperty("error");
            var code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString()! : "error";
            var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : code;
            if (CaseHudState.IsApiDisabled(status, code)) return CaseHudState.ApiOff();
            if (OwnText(code, status) is { } own)
            {
                Console.Error.WriteLine($"[casehud] {status} {CaseHudLighting.SafeMessage(code, 40)}: {CaseHudLighting.SafeMessage(message)}");
                return new CaseHudException(code, own, status);
            }
            return new CaseHudException(code, message, status);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or CaseHudException)
        {
            return new CaseHudException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "unauthorized",
                HttpStatusCode.NotFound => "not_found",
                _ => "error"
            }, Strings.Get("casehud.httpStatus", status), status);
        }
    }

    /// <summary>Our text for the refusals whose English message would only puzzle on the couch; null keeps CaseHUD's message.</summary>
    internal static string? OwnText(string code, int status) => (code, status) switch
    {
        ("unauthorized", _) => Strings.Get("casehud.err.unauthorized"),
        ("forbidden", _) => Strings.Get("casehud.err.forbidden"),
        ("not_found", _) or (_, 404) => Strings.Get("casehud.err.notFound"),
        _ => null
    };

    private static async Task<byte[]> ReadLimited(HttpResponseMessage response, long limit, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > limit) throw new CaseHudException("bad_response", Strings.Get("casehud.badResponse"));
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read <= 0) break;
            if (buffer.Length + read > limit) throw new CaseHudException("bad_response", Strings.Get("casehud.badResponse"));
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
