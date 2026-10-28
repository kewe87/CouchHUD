using System.Text.Json;
using System.Text.Json.Nodes;

namespace CouchHud.Core.Assistant;

/// <summary>
/// One operation the assistant (and MCP clients) may use. <see cref="Writes"/>
/// marks anything that changes state; the shell asks before running those.
/// <see cref="Sensitive"/> asks MCP clients for a confirmation even though nothing changes
/// (a plugin's tool can do anything).
/// </summary>
public sealed record ToolSpec(string Name, string Description, JsonObject Schema, bool Writes, bool Sensitive = false)
{
    /// <summary>A tool of one of CouchHUD's own plugins that may run on the locked couch (set by the plugin manager; never for other plugins).</summary>
    public bool AllowedWhileLocked { get; init; }

    /// <summary>Does a call from outside (MCP) need an A on the TV?</summary>
    public bool NeedsConfirmationOverMcp => Writes || Sensitive;
}

/// <summary>Runs tools against the real HUD; implemented by the shell.</summary>
public interface IToolHost
{
    /// <summary>Result as JSON text for the model. Throws <see cref="ArgumentException"/> for bad input.</summary>
    Task<string> CallAsync(string name, JsonElement args, CancellationToken ct);

    /// <summary>A short German sentence for the confirmation dialog, e.g. "Cyberpunk 2077 starten".</summary>
    string Describe(string name, JsonElement args);
}

public static class ToolCatalog
{
    /// <summary>Settings the assistant may change. Secrets (API keys, tokens) are deliberately missing.</summary>
    public static readonly string[] SettingKeys =
        ["sounds", "rumble", "trailerPreviews", "autoSwitchHeadphones", "runStartupApps", "killExplorerOnReturn", "returnHoldMs"];

    public static IReadOnlyList<ToolSpec> All { get; } =
    [
        Read("search_library", "Search the game and app library. Returns id, name, type, source, favorite, playtime in minutes and last played time (unix ms). Without a query it returns everything.",
            Props(("query", Str("Part of the name, case-insensitive")), ("type", Choice("game or app", "game", "app")), ("favorites_only", Bool("Only favorites")))),
        Read("get_item_details", "Details for one library entry: description, developer, genres, install folder, size on disk, playtime.",
            Props(("id", Str("Library id from search_library"))), "id"),
        Write("launch", "Start a game or app.", Props(("id", Str("Library id"))), "id"),
        Write("toggle_favorite", "Add an entry to the favorites or remove it.", Props(("id", Str("Library id"))), "id"),
        Write("edit_item", "Rename an entry, change whether it counts as game or app, or hide it from the library.",
            Props(("id", Str("Library id")), ("name", Str("New display name")), ("type", Choice("game or app", "game", "app")), ("hidden", Bool("Hide from the library"))), "id"),
        Write("add_entry", "Add a game or app that no store knows about, by the path of its .exe.",
            Props(("name", Str("Display name")), ("path", Str("Full path to the .exe")), ("type", Choice("game or app", "game", "app")),
                ("args", new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["description"] = "Command line arguments" })),
            "name", "path", "type"),
        Read("rescan_library", "Scan Steam, Epic and all plugins again for new or removed games.", Props()),
        Read("get_settings", "Current settings.", Props()),
        Write("set_setting", "Change a setting. returnHoldMs is a number (200-5000), all others are true/false.",
            Props(("key", Choice("Setting name", SettingKeys)), ("value", new JsonObject { ["description"] = "true/false, or a number for returnHoldMs" })), "key", "value"),
        Read("list_skins", "Available designs (skins) with id, name and the other names they answer to.", Props()),
        Write("set_skin", "Switch the design.", Props(("id", Str("Skin id or name from list_skins"))), "id"),
        Read("get_session", "The game that is running right now, if any.", Props()),
        Read("get_weather", "Current weather and forecast for the configured place.", Props()),
        Read("get_news", "Latest headlines from the configured news feeds.", Props(("limit", Int("How many, default 10")))),
        Read("get_updates", "Pending Windows and app updates from the last check.", Props()),
        Read("get_audio", "Volume, mute state and audio outputs.", Props()),
        Write("set_volume", "Set the master volume.", Props(("percent", Int("0-100"))), "percent"),
        Write("set_audio_output", "Switch the default audio output (e.g. to the headset).", Props(("device_id", Str("Device id from get_audio"))), "device_id"),
        Write("power", "Sleep, restart or shut down the PC.", Props(("action", Choice("What to do", "sleep", "restart", "shutdown"))), "action")
    ];

    public static ToolSpec? Find(string name) => All.FirstOrDefault(t => t.Name == name);

    /// <summary>Plugin tools are named "plugin_&lt;plugin&gt;_&lt;tool&gt;": they can never be mistaken for a built-in one.</summary>
    public const string PluginPrefix = "plugin_";

    /// <summary>Tools whose confirmation is shown as a warning (the answer is not "run" by default and the entry is red).</summary>
    public static bool IsDangerous(string name) => name is "power" or "add_entry" or "edit_item";

    /// <summary>
    /// A locked couch: the assistant and MCP may look things up and start something from the library, nothing else
    /// (no settings, no power, no plugins). A guest at the controller gets no more from the assistant than from the HUD.
    /// </summary>
    public static bool AllowedWhileLocked(string name) =>
        name == "launch" || (Find(name) is { Writes: false } && name != "rescan_library");

    private static ToolSpec Read(string name, string description, JsonObject props, params string[] required) =>
        new(name, description, Schema(props, required), false);

    private static ToolSpec Sensitive(string name, string description, JsonObject props, params string[] required) =>
        new(name, description, Schema(props, required), false, Sensitive: true);

    private static ToolSpec Write(string name, string description, JsonObject props, params string[] required) =>
        new(name, description, Schema(props, required), true);

    private static JsonObject Schema(JsonObject props, string[] required) => new()
    {
        ["type"] = "object",
        ["properties"] = props,
        ["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray())
    };

    private static JsonObject Props(params (string Name, JsonObject Schema)[] props)
    {
        var o = new JsonObject();
        foreach (var (name, schema) in props) o[name] = schema;
        return o;
    }

    private static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };
    private static JsonObject Int(string description) => new() { ["type"] = "integer", ["description"] = description };
    private static JsonObject Bool(string description) => new() { ["type"] = "boolean", ["description"] = description };

    private static JsonObject Choice(string description, params string[] values) => new()
    {
        ["type"] = "string",
        ["description"] = description,
        ["enum"] = new JsonArray(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray())
    };
}
