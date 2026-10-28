using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;

namespace CouchHud.Core.Integrations;

public enum HaRegistryOutcome
{
    Ok,
    /// <summary>Home Assistant refused the token (auth_invalid): never ask again with it.</summary>
    Unauthorized,
    Failed
}

/// <summary>The three registry answers (each the "result" of its command), or why there are none.</summary>
public sealed record HaRegistryAnswer(HaRegistryOutcome Outcome, JsonElement Entities = default, JsonElement Areas = default, JsonElement Devices = default)
{
    public static readonly HaRegistryAnswer Failed = new(HaRegistryOutcome.Failed);
    public static readonly HaRegistryAnswer Unauthorized = new(HaRegistryOutcome.Unauthorized);
}

/// <summary>
/// Rooms, hidden flags and categories from Home Assistant's WebSocket API on the same server and with the same token as the REST calls.
/// Three read commands that need no admin token (checked in Home Assistant's source, components/config/*_registry.py: no require_admin):
/// <c>config/entity_registry/list_for_display</c>, <c>config/area_registry/list</c>, <c>config/device_registry/list</c>.
/// Every message is size-limited, the whole exchange too; nothing else is sent, nothing else is read.
/// </summary>
public static class HomeAssistantWebSocket
{
    /// <summary>Largest single message (the device list of a big home is about 1 MB).</summary>
    public const int MaxMessageBytes = 8 * 1024 * 1024;

    /// <summary>Largest exchange in all.</summary>
    public const int MaxTotalBytes = 20 * 1024 * 1024;

    /// <summary>At most this many messages are read (auth and three answers, plus a few Home Assistant may send in between).</summary>
    private const int MaxMessages = 16;

    /// <summary>ws(s)://host:port/…/api/websocket from the configured address; null for anything that is not http or https.</summary>
    public static Uri? SocketUri(string? baseUrl)
    {
        if (!Uri.TryCreate((baseUrl ?? "").Trim().TrimEnd('/') + "/", UriKind.Absolute, out var http)) return null;
        var scheme = http.Scheme switch { "http" => "ws", "https" => "wss", _ => null };
        if (scheme is null) return null;
        var builder = new UriBuilder(new Uri(http, "api/websocket")) { Scheme = scheme };
        return builder.Uri;
    }

    /// <summary>Connects (redirects are not followed by ClientWebSocket) and runs <see cref="RunAsync"/>.</summary>
    public static async Task<HaRegistryAnswer> FetchAsync(Uri socketUri, string token, CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.Zero;
        try
        {
            await socket.ConnectAsync(socketUri, ct);
        }
        catch (WebSocketException)
        {
            return HaRegistryAnswer.Failed;
        }
        return await RunAsync(socket, token, ct);
    }

    /// <summary>The protocol on an open socket: auth, the three commands, the three results. Kept apart for the tests.</summary>
    public static async Task<HaRegistryAnswer> RunAsync(WebSocket socket, string token, CancellationToken ct)
    {
        var budget = new Budget();
        try
        {
            using (var hello = await ReceiveAsync(socket, budget, ct))
            {
                if (hello is null || TypeOf(hello.RootElement) != "auth_required") return HaRegistryAnswer.Failed;
            }
            await SendAsync(socket, new { type = "auth", access_token = token }, ct);
            using (var auth = await ReceiveAsync(socket, budget, ct))
            {
                switch (auth is null ? null : TypeOf(auth.RootElement))
                {
                    case "auth_ok": break;
                    case "auth_invalid": return HaRegistryAnswer.Unauthorized;
                    default: return HaRegistryAnswer.Failed;
                }
            }

            string[] commands = ["config/entity_registry/list_for_display", "config/area_registry/list", "config/device_registry/list"];
            for (var i = 0; i < commands.Length; i++) await SendAsync(socket, new { id = i + 1, type = commands[i] }, ct);

            var results = new JsonElement[commands.Length];
            var answered = new bool[commands.Length];
            var pending = commands.Length;
            for (var read = 0; pending > 0 && read < MaxMessages; read++)
            {
                using var doc = await ReceiveAsync(socket, budget, ct);
                if (doc is null) return HaRegistryAnswer.Failed;
                var root = doc.RootElement;
                if (TypeOf(root) != "result" || !root.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id) || id < 1 || id > commands.Length) continue;
                if (answered[id - 1]) continue;
                var ok = root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True && root.TryGetProperty("result", out _);
                // Without the entity list nothing is worth having; areas and devices only add the names.
                if (!ok && id == 1) return HaRegistryAnswer.Failed;
                if (ok) results[id - 1] = root.GetProperty("result").Clone();
                answered[id - 1] = true;
                pending--;
            }
            if (pending > 0) return HaRegistryAnswer.Failed;
            await CloseQuietlyAsync(socket);
            return new HaRegistryAnswer(HaRegistryOutcome.Ok, results[0], results[1], results[2]);
        }
        catch (Exception ex) when (ex is WebSocketException or JsonException or InvalidDataException or IOException)
        {
            return HaRegistryAnswer.Failed;
        }
    }

    private sealed class Budget
    {
        public long Used;
    }

    private static string? TypeOf(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;

    private static Task SendAsync(WebSocket socket, object message, CancellationToken ct) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, ct);

    /// <summary>One text message, growing up to <see cref="MaxMessageBytes"/>; null when the other side closes.</summary>
    private static async Task<JsonDocument?> ReceiveAsync(WebSocket socket, Budget budget, CancellationToken ct)
    {
        var chunk = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var message = new MemoryStream();
            while (true)
            {
                var r = await socket.ReceiveAsync(chunk.AsMemory(), ct);
                if (r.MessageType == WebSocketMessageType.Close) return null;
                if (r.MessageType != WebSocketMessageType.Text) throw new InvalidDataException("binary message");
                budget.Used += r.Count;
                if (message.Length + r.Count > MaxMessageBytes || budget.Used > MaxTotalBytes) throw new InvalidDataException("message too large");
                message.Write(chunk, 0, r.Count);
                if (r.EndOfMessage) break;
            }
            message.Position = 0;
            return JsonDocument.Parse(message, new JsonDocumentOptions { MaxDepth = 16 });
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }

    private static async Task CloseQuietlyAsync(WebSocket socket)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
            // Done anyway.
        }
    }
}
