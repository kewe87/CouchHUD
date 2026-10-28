using System.Text.Json;
using System.Text.Json.Nodes;

namespace CouchHud.Core.Assistant;

/// <summary>
/// The JSON-RPC side of a Model Context Protocol server: the same tools as the
/// built-in assistant, for clients like desktop chat apps or agents. Transport
/// (HTTP, auth) lives in the shell; this only maps requests to responses.
/// </summary>
/// <param name="tools">All tools, built-in plus plugins; defaults to the built-in catalog.</param>
public sealed class McpServer(IToolHost host, string version, Func<IReadOnlyList<ToolSpec>>? tools = null)
{
    private IReadOnlyList<ToolSpec> Tools => tools?.Invoke() ?? ToolCatalog.All;

    public const string ProtocolVersion = "2025-06-18";

    /// <summary>
    /// Asked before every tool that changes something; false refuses the call.
    /// The shell shows the same confirmation on the TV as for the built-in assistant.
    /// </summary>
    public Func<ToolSpec, JsonElement, Task<bool>>? Confirm { get; init; }

    /// <summary>The response to one JSON-RPC message, or null for notifications.</summary>
    public async Task<JsonObject?> HandleAsync(JsonNode? message, CancellationToken ct = default)
    {
        try
        {
            return await DispatchAsync(message, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException)
        {
            // Wrongly typed fields ("name": 5 …) are the client's mistake, not a server crash.
            var id = (message as JsonObject)?["id"]?.DeepClone();
            return id is null ? null : Error(id, -32602, $"Invalid params: {ex.Message}");
        }
    }

    private async Task<JsonObject?> DispatchAsync(JsonNode? message, CancellationToken ct)
    {
        if (message is not JsonObject request) return Error(null, -32600, "Invalid request");
        var id = request["id"]?.DeepClone();
        var method = request["method"]?.GetValueKind() == JsonValueKind.String ? request["method"]!.GetValue<string>() : null;
        if (method is null) return id is null ? null : Error(id, -32600, "Invalid request");
        // Notifications (no id) need no answer.
        if (id is null) return null;

        switch (method)
        {
            case "initialize":
                var requested = request["params"]?["protocolVersion"]?.GetValue<string>();
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = requested is "2025-03-26" or "2024-11-05" ? requested : ProtocolVersion,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = "CouchHUD", ["version"] = version },
                    ["instructions"] = "Controls the CouchHUD living-room PC: library, launching games, settings, audio and power."
                });
            case "ping":
                return Result(id, new JsonObject());
            case "tools/list":
                return Result(id, new JsonObject
                {
                    ["tools"] = new JsonArray(Tools.Select(t => (JsonNode)new JsonObject
                    {
                        ["name"] = t.Name,
                        ["description"] = t.Description,
                        ["inputSchema"] = t.Schema.DeepClone(),
                        ["annotations"] = new JsonObject { ["readOnlyHint"] = !t.Writes, ["destructiveHint"] = ToolCatalog.IsDangerous(t.Name) }
                    }).ToArray())
                });
            case "tools/call":
                return await CallAsync(id, request["params"], ct);
            default:
                return Error(id, -32601, $"Method not found: {method}");
        }
    }

    private async Task<JsonObject> CallAsync(JsonNode id, JsonNode? parameters, CancellationToken ct)
    {
        var name = parameters?["name"]?.GetValue<string>();
        var spec = name is null ? null : Tools.FirstOrDefault(t => t.Name == name);
        if (spec is null) return Error(id, -32602, $"Unknown tool: {name}");
        var args = JsonDocument.Parse(parameters?["arguments"]?.ToJsonString() ?? "{}").RootElement.Clone();
        try
        {
            if (spec.NeedsConfirmationOverMcp && Confirm is not null && !await Confirm(spec, args))
            {
                return Result(id, ToolContent("The user declined this action on the TV.", true));
            }
            var text = await host.CallAsync(spec.Name, args, ct);
            return Result(id, ToolContent(text, false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Tool errors are results the model can read, not protocol errors.
            return Result(id, ToolContent(ex.Message, true));
        }
    }

    private static JsonObject ToolContent(string text, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError
    };

    private static JsonObject Result(JsonNode id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
    };
}
