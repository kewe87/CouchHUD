using System.Text.Json;
using System.Windows;
using CouchHud.Core.Assistant;
using CouchHud.Core.Config;
using CouchHud.Shell.UiHost;
using CouchHud.Core.Localization;
using CouchHud.Core.Skins;

namespace CouchHud.Shell.Services;

/// <summary>The assistant's and MCP's tools, mapped onto the HUD. Runs everything on the UI thread.</summary>
public sealed class HudTools(Hud hud) : IToolHost
{
    private sealed record PluginText(string Value);

    private static readonly string[] BuiltinSkins = CouchHud.Core.Skins.SkinCatalog.BuiltinIds;

    public Task<string> CallAsync(string name, JsonElement args, CancellationToken ct) =>
        Application.Current.Dispatcher.InvokeAsync(() => RunAsync(name, args)).Task.Unwrap();

    public string Describe(string name, JsonElement args) => hud.Plugins.FindTool(name) is { } pluginTool
        ? Strings.Get("tools.plugin", pluginTool.Description, ArgumentsText(args))
        : name switch
    {
        "launch" => Strings.Get("tools.launch", ItemName(args)),
        "toggle_favorite" => Strings.Get("tools.toggleFavorite", ItemName(args)),
        "edit_item" => DescribeEdit(args),
        "add_entry" => Strings.Get("tools.addEntry", Str(args, "name"), Str(args, "path"), ArgsText(args)),
        "set_setting" => Strings.Get("tools.setSetting", Str(args, "key"), Raw(args, "value")),
        "set_skin" => Strings.Get("tools.setSkin", Str(args, "id")),
        "activate_home_entity" => Strings.Get("tools.homeAssistant", Str(args, "entity_id")),
        "list_home_entities" => Strings.Get("tools.listHome"),
        "set_volume" => Strings.Get("tools.setVolume", Raw(args, "percent")),
        "set_audio_output" => Strings.Get("tools.switchOutput"),
        "power" => Str(args, "action") switch
        {
            "shutdown" => Strings.Get("tools.shutdown"),
            "restart" => Strings.Get("tools.restart"),
            _ => Strings.Get("tools.sleep")
        },
        _ => name
    };

    private async Task<string> RunAsync(string name, JsonElement args)
    {
        // A protected profile waits for its code at the picker: the assistant and MCP do nothing at all until it is entered (review M1).
        hud.RequireProfileChosen();
        // A locked couch: the assistant and MCP may look things up and start a game or app, nothing else.
        if (hud.Locked && !ToolCatalog.AllowedWhileLocked(name))
        {
            throw new InvalidOperationException(Strings.Get("tools.locked"));
        }
        // Switched off areas (FeatureAreas): their tools and their entries are not there for the assistant and MCP either.
        if (!hud.Areas.AllowsTool(name) || (Opt(args, "id") is { } itemId && hud.HiddenByArea(itemId))
            || (name == "set_setting" && Opt(args, "key") is { } settingKey && !hud.Areas.AllowsSetting(settingKey)))
            throw new InvalidOperationException(Strings.Get("areas.off"));
        object? result = name switch
        {
            "search_library" => Search(args),
            "get_item_details" => await hud.GetDetailsAsync(Str(args, "id")),
            "launch" => Launch(args),
            "toggle_favorite" => new { favorite = hud.ToggleFavorite(Str(args, "id")) },
            "edit_item" => Summary(hud.EditItem(Str(args, "id"), Opt(args, "name"), Opt(args, "type"), OptBool(args, "hidden"))),
            "add_entry" => await AddEntryAsync(args),
            "rescan_library" => new { entries = (await hud.GetLibraryAsync(true)).Count(i => !i.Hidden) },
            "get_settings" => hud.GetSettings(forUi: false),
            "set_setting" => await SetSettingAsync(args),
            "list_skins" => ListSkins(),
            "set_skin" => SetSkin(args),
            "get_session" => hud.SessionDto() ?? new { running = false },
            "get_weather" => await hud.GetWeatherAsync() ?? (object)new { error = Strings.Get("tools.noWeatherPlace") },
            "get_news" => (await hud.GetNewsAsync()).Take(Math.Clamp(OptInt(args, "limit") ?? 10, 1, 30))
                .Select(n => new { n.Feed, n.Title, n.Summary, published = DateTimeOffset.FromUnixTimeMilliseconds(n.Published).ToLocalTime().ToString("g") }),
            "get_updates" => hud.GetUpdates(),
            "list_home_entities" => await hud.GetHomeEntitiesAsync(),
            "activate_home_entity" => await ActivateAsync(args),
            "get_audio" => hud.GetAudio(),
            "set_volume" => hud.SetVolume(Math.Clamp(OptInt(args, "percent") ?? throw new ArgumentException(Strings.Get("tools.percentMissing")), 0, 100)),
            "set_audio_output" => hud.SetAudioOutput(Str(args, "device_id")),
            "power" => Power(args),
            _ when hud.Plugins.HasTool(name) => new PluginText(await hud.Plugins.CallToolAsync(name, args)),
            _ => throw new ArgumentException(Strings.Get("tools.unknown", name))
        };
        // Plugins already answer with text (often JSON); pass it on unchanged.
        return result is PluginText text ? text.Value : JsonSerializer.Serialize(result, UiBridge.Json);
    }

    private object Search(JsonElement args)
    {
        var query = Opt(args, "query");
        var type = Opt(args, "type");
        var favorites = OptBool(args, "favorites_only") == true;
        return hud.LibraryItems
            .Where(i => !i.Hidden)
            .Where(i => string.IsNullOrWhiteSpace(query) || i.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(i => type is null || string.Equals(i.Type.ToString(), type, StringComparison.OrdinalIgnoreCase))
            .Where(i => !favorites || i.Favorite)
            .OrderByDescending(i => i.LastPlayed)
            .Take(50)
            .Select(i => new { i.Id, i.Name, i.Type, i.Source, i.Favorite, playtimeMinutes = i.PlaytimeMinutes, lastPlayed = i.LastPlayed })
            .ToList();
    }

    private static object Summary(CouchHud.Sdk.LibraryItem i) => new { i.Id, i.Name, i.Type, i.Hidden, i.Favorite };

    private object Launch(JsonElement args)
    {
        var item = hud.Launch(Str(args, "id"));
        return new { launched = item.Name };
    }

    private async Task<object> AddEntryAsync(JsonElement args)
    {
        var path = Str(args, "path");
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) throw new ArgumentException(Strings.Get("tools.noExe", path));
        var type = Opt(args, "type") is "game" ? "game" : "app";
        var entry = new CustomEntry
        {
            Name = Str(args, "name"),
            Type = type,
            Path = path,
            Cwd = Path.GetDirectoryName(path),
            Args = args.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                : new List<string>()
        };
        var added = await hud.AddEntriesAsync([entry]);
        return added.Select(Summary);
    }

    private async Task<object> SetSettingAsync(JsonElement args)
    {
        var key = Str(args, "key");
        if (!ToolCatalog.SettingKeys.Contains(key)) throw new ArgumentException(Strings.Get("tools.cannotChange", key));
        if (!args.TryGetProperty("value", out var value)) throw new ArgumentException(Strings.Get("tools.valueMissing"));
        // Models sometimes send "true" or "1500" as strings.
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString() ?? "";
            value = JsonDocument.Parse(text is "true" or "false" || int.TryParse(text, out _) ? text : JsonSerializer.Serialize(text)).RootElement.Clone();
        }
        await hud.SetSettingAsync(key, value);
        hud.Notify("settings", null);
        return new { key, value = value.GetRawText() };
    }

    /// <summary>Built-in skins with their name in the current language and the other names they answer to, then the user skins.</summary>
    private IEnumerable<object> ListSkins() =>
        BuiltinSkins.Select(id => (object)new
        {
            id,
            name = SkinCatalog.BuiltinName(id, Strings.Language),
            aliases = new[] { SkinCatalog.BuiltinName(id, "de"), SkinCatalog.BuiltinName(id, "en") }.Distinct().ToArray()
        }).Concat(hud.ListSkins().Select(s => (object)new { id = s.Id, name = s.Name, aliases = Array.Empty<string>() }));

    private object SetSkin(JsonElement args)
    {
        var asked = Str(args, "id");
        // An id (also one from before 0.5) or a skin's name in either language.
        var id = SkinCatalog.Resolve(asked, hud.ListSkins()) ?? throw new ArgumentException(Strings.Get("tools.noSkin", asked));
        hud.SetSkin(id);
        hud.Notify("skin", id);
        return new { skin = id };
    }

    private async Task<object> ActivateAsync(JsonElement args)
    {
        await hud.ActivateHomeEntityAsync(Str(args, "entity_id"));
        return new { done = true };
    }

    private object Power(JsonElement args)
    {
        var action = Str(args, "action");
        if (action is not ("sleep" or "restart" or "shutdown")) throw new ArgumentException(Strings.Get("tools.unknownAction", action));
        // Give the answer a moment to reach the screen (or the MCP client) first.
        _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ => Application.Current.Dispatcher.InvokeAsync(() => hud.Power(action)), TaskScheduler.Default);
        return new { action, inSeconds = 3 };
    }

    private string ItemName(JsonElement args) => hud.FindItem(Opt(args, "id") ?? "")?.Name ?? Opt(args, "id") ?? Strings.Get("tools.entry");

    private string DescribeEdit(JsonElement args)
    {
        var name = ItemName(args);
        var parts = new List<string>();
        if (Opt(args, "name") is { } newName) parts.Add(Strings.Get("tools.rename", newName));
        if (Opt(args, "type") is { } type) parts.Add(type == "game" ? Strings.Get("tools.asGame") : Strings.Get("tools.asApp"));
        if (OptBool(args, "hidden") is { } hidden) parts.Add(hidden ? Strings.Get("tools.hide") : Strings.Get("tools.unhide"));
        return $"{name}: {(parts.Count > 0 ? string.Join(", ", parts) : Strings.Get("tools.edit"))}";
    }

    private static string Str(JsonElement args, string name) =>
        Opt(args, name) ?? throw new ArgumentException(Strings.Get("tools.missing", name));

    private static string? Opt(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private static bool? OptBool(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static int? OptInt(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return (int)Math.Round(d);
        return v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var i) ? i : null;
    }

    /// <summary>Startup arguments in full: the confirmation must show exactly what would run.</summary>
    private static string ArgsText(JsonElement args) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty("args", out var a) && a.ValueKind == JsonValueKind.Array && a.GetArrayLength() > 0
            ? " " + string.Join(" ", a.EnumerateArray().Select(x => x.ToString()))
            : "";

    /// <summary>What a plugin tool is called with, in full (cut at a sane length): the confirmation must show what would run.</summary>
    internal static string ArgumentsText(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.EnumerateObject().Any()) return "–";
        var text = args.GetRawText();
        return text.Length <= 400 ? text : text[..400] + " …";
    }

    private static string Raw(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) ? v.ToString() : "?";
}
