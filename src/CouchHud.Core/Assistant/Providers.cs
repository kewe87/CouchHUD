using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Assistant;

public sealed record ToolCall(string Id, string Name, JsonElement Arguments);

public sealed record ToolResult(string CallId, string Content, bool IsError = false);

/// <summary>Provider-neutral conversation entry.</summary>
public sealed class ChatMessage
{
    /// <summary>"user" or "assistant". Tool results travel as user messages.</summary>
    public required string Role { get; init; }
    public string? Text { get; init; }
    public IReadOnlyList<ToolCall> Calls { get; init; } = [];
    public IReadOnlyList<ToolResult> Results { get; init; } = [];

    public static ChatMessage User(string text) => new() { Role = "user", Text = text };
}

public sealed record ChatReply(string? Text, IReadOnlyList<ToolCall> Calls);

public interface IChatProvider
{
    string Name { get; }
    Task<ChatReply> CompleteAsync(string system, IReadOnlyList<ChatMessage> history, IReadOnlyList<ToolSpec> tools, CancellationToken ct);
}

public sealed class AssistantException(string message) : Exception(message);

public static class ChatProviders
{
    public const string Anthropic = "anthropic";
    public const string OpenAi = "openai";

    public static readonly IReadOnlyDictionary<string, string[]> Models = new Dictionary<string, string[]>
    {
        [Anthropic] = ["claude-sonnet-5-5", "claude-opus-5-5", "claude-haiku-5-5"],
        [OpenAi] = ["gpt-5", "gpt-5-mini"]
    };

    public static IChatProvider Create(HttpClient http, string provider, string apiKey, string? model) => provider switch
    {
        Anthropic => new AnthropicProvider(http, apiKey, string.IsNullOrWhiteSpace(model) ? Models[Anthropic][0] : model),
        OpenAi => new OpenAiProvider(http, apiKey, string.IsNullOrWhiteSpace(model) ? Models[OpenAi][0] : model),
        _ => throw new AssistantException(Strings.Get("assistant.noProvider"))
    };

    internal static async Task<JsonNode> PostAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (Net.NetBlockedException ex)
        {
            // The privacy settings changed under a running conversation: a plain message, not a crash.
            throw new AssistantException(ex.Message);
        }
        using var _ = response;
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new AssistantException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Strings.Get("assistant.keyInvalid"),
                HttpStatusCode.TooManyRequests => Strings.Get("assistant.rateLimited"),
                HttpStatusCode.NotFound => Strings.Get("assistant.modelNotFound"),
                _ => Strings.Get("assistant.providerError", (int)response.StatusCode, ErrorMessage(body))
            });
        }
        return JsonNode.Parse(body) ?? throw new AssistantException(Strings.Get("assistant.emptyReply"));
    }

    private static string ErrorMessage(string body)
    {
        try
        {
            return JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>() ?? body[..Math.Min(body.Length, 200)];
        }
        catch (JsonException)
        {
            return body[..Math.Min(body.Length, 200)];
        }
    }

    internal static StringContent Json(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");
}

/// <summary>Anthropic Messages API with tool use.</summary>
public sealed class AnthropicProvider(HttpClient http, string apiKey, string model) : IChatProvider
{
    public string Name => model;

    public async Task<ChatReply> CompleteAsync(string system, IReadOnlyList<ChatMessage> history, IReadOnlyList<ToolSpec> tools, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = ChatProviders.Json(BuildRequest(model, system, history, tools))
        };
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        return ParseReply(await ChatProviders.PostAsync(http, request, ct));
    }

    public static JsonObject BuildRequest(string model, string system, IReadOnlyList<ChatMessage> history, IReadOnlyList<ToolSpec> tools)
    {
        var messages = new JsonArray();
        foreach (var m in history)
        {
            var content = new JsonArray();
            foreach (var r in m.Results)
            {
                content.Add(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = r.CallId, ["content"] = r.Content, ["is_error"] = r.IsError });
            }
            if (!string.IsNullOrEmpty(m.Text)) content.Add(new JsonObject { ["type"] = "text", ["text"] = m.Text });
            foreach (var c in m.Calls)
            {
                content.Add(new JsonObject { ["type"] = "tool_use", ["id"] = c.Id, ["name"] = c.Name, ["input"] = JsonNode.Parse(c.Arguments.GetRawText()) });
            }
            messages.Add(new JsonObject { ["role"] = m.Role, ["content"] = content });
        }
        return new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = 2048,
            ["system"] = system,
            ["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = t.Schema.DeepClone()
            }).ToArray()),
            ["messages"] = messages
        };
    }

    public static ChatReply ParseReply(JsonNode response)
    {
        var text = new StringBuilder();
        var calls = new List<ToolCall>();
        foreach (var block in response["content"]?.AsArray() ?? new JsonArray())
        {
            switch (block?["type"]?.GetValue<string>())
            {
                case "text":
                    text.Append(block["text"]?.GetValue<string>());
                    break;
                case "tool_use":
                    var input = block["input"]?.ToJsonString() ?? "{}";
                    calls.Add(new ToolCall(block["id"]!.GetValue<string>(), block["name"]!.GetValue<string>(), JsonDocument.Parse(input).RootElement.Clone()));
                    break;
            }
        }
        return new ChatReply(text.Length > 0 ? text.ToString().Trim() : null, calls);
    }
}

/// <summary>OpenAI Chat Completions API with function calling.</summary>
public sealed class OpenAiProvider(HttpClient http, string apiKey, string model) : IChatProvider
{
    public string Name => model;

    public async Task<ChatReply> CompleteAsync(string system, IReadOnlyList<ChatMessage> history, IReadOnlyList<ToolSpec> tools, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions")
        {
            Content = ChatProviders.Json(BuildRequest(model, system, history, tools))
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return ParseReply(await ChatProviders.PostAsync(http, request, ct));
    }

    public static JsonObject BuildRequest(string model, string system, IReadOnlyList<ChatMessage> history, IReadOnlyList<ToolSpec> tools)
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
        foreach (var m in history)
        {
            // Tool results are separate "tool" messages here, not part of a user turn.
            foreach (var r in m.Results)
            {
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = r.CallId, ["content"] = r.Content });
            }
            if (m.Role == "assistant")
            {
                var msg = new JsonObject { ["role"] = "assistant", ["content"] = m.Text };
                if (m.Calls.Count > 0)
                {
                    msg["tool_calls"] = new JsonArray(m.Calls.Select(c => (JsonNode)new JsonObject
                    {
                        ["id"] = c.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Arguments.GetRawText() }
                    }).ToArray());
                }
                messages.Add(msg);
            }
            else if (!string.IsNullOrEmpty(m.Text))
            {
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = m.Text });
            }
        }
        var body = new JsonObject
        {
            ["model"] = model,
            // Reasoning models count their thinking against this limit too.
            ["max_completion_tokens"] = 8192,
            ["tools"] = new JsonArray(tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Schema.DeepClone() }
            }).ToArray()),
            ["messages"] = messages
        };
        // A couch question doesn't need deep thinking; it keeps answers fast and cheap.
        if (model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase) || model.StartsWith("o", StringComparison.OrdinalIgnoreCase))
        {
            body["reasoning_effort"] = "low";
        }
        return body;
    }

    public static ChatReply ParseReply(JsonNode response)
    {
        var message = response["choices"]?[0]?["message"] ?? throw new AssistantException(Strings.Get("assistant.noContent"));
        var calls = new List<ToolCall>();
        foreach (var c in message["tool_calls"]?.AsArray() ?? new JsonArray())
        {
            var fn = c?["function"];
            if (fn is null) continue;
            var args = fn["arguments"]?.GetValue<string>();
            JsonElement parsed;
            try
            {
                parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(args) ? "{}" : args).RootElement.Clone();
            }
            catch (JsonException)
            {
                parsed = JsonDocument.Parse("{}").RootElement.Clone();
            }
            calls.Add(new ToolCall(c!["id"]!.GetValue<string>(), fn["name"]!.GetValue<string>(), parsed));
        }
        var text = message["content"]?.GetValueKind() == JsonValueKind.String ? message["content"]!.GetValue<string>().Trim() : null;
        if (string.IsNullOrEmpty(text) && calls.Count == 0 && response["choices"]?[0]?["finish_reason"]?.GetValue<string>() == "length")
        {
            throw new AssistantException(Strings.Get("assistant.tooLong"));
        }
        return new ChatReply(string.IsNullOrEmpty(text) ? null : text, calls);
    }
}
