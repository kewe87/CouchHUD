using System.Globalization;
using System.Text.Json;

namespace CouchHud.Core.Localization;

/// <summary>
/// Texts the host shows itself (notifications, error messages that reach the UI, tray menu,
/// message boxes). German is the default and the fallback; the UI has its own files in ui/i18n.
/// The language comes from the config ("auto", "de" or "en") and can change while running.
/// </summary>
public static partial class Strings
{
    /// <summary>The active language, "de" or "en".</summary>
    public static string Language { get; private set; } = "de";

    /// <summary>Maps the config value to "de" or "en". "auto" follows the Windows display language.</summary>
    public static string Resolve(string? setting, CultureInfo? ui = null)
    {
        if (string.Equals(setting, "de", StringComparison.OrdinalIgnoreCase)) return "de";
        if (string.Equals(setting, "en", StringComparison.OrdinalIgnoreCase)) return "en";
        var culture = ui ?? CultureInfo.CurrentUICulture;
        return culture.TwoLetterISOLanguageName.Equals("de", StringComparison.OrdinalIgnoreCase) ? "de" : "en";
    }

    public static void Use(string? setting) => Language = Resolve(setting);

    internal static IReadOnlyDictionary<string, string> German => De;
    internal static IReadOnlyDictionary<string, string> English => En;

    /// <summary>The text for a key in the active language; falls back to German, then to the key itself.</summary>
    public static string Get(string key, params object?[] args) => GetIn(Language, key, args);

    /// <summary>Like <see cref="Get"/> for a given language, independent of the active one (tests).</summary>
    internal static string GetIn(string language, string key, params object?[] args)
    {
        var table = language == "en" ? En : De;
        if (!table.TryGetValue(key, out var text) && !De.TryGetValue(key, out text)) return key;
        if (args.Length == 0) return text;
        try
        {
            return string.Format(CultureInfo.CurrentCulture, text, args);
        }
        catch (FormatException)
        {
            return text;
        }
    }

    /// <summary>Key + ".one" for a count of 1, otherwise key + ".other"; {0} is the count.</summary>
    public static string Plural(string key, long count, params object?[] args) => PluralIn(Language, key, count, args);

    internal static string PluralIn(string language, string key, long count, params object?[] args) =>
        GetIn(language, key + (count == 1 ? ".one" : ".other"), [count, .. args]);

    // ---- Texts that are stored and shown later ----
    // A notification keeps its key and arguments instead of the finished text, so it reads in the language of
    // the moment it is shown (the user may switch the language in between). Arguments can be references themselves.

    private const char RefMark = '';

    private sealed record RefData(string K, long? N, List<JsonElement> A);

    // Strings made by Ref/RefPlural, by identity. Only these become nested references when passed as an argument; any other
    // string (a game name from a file, a message from outside) is stored and shown as plain text, even if it looks like a reference.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, object> Made = new();

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>A stored reference to a text; <see cref="Render"/> turns it into the text in the active language.</summary>
    public static string Ref(string key, params object?[] args) => Encode(key, null, args);

    /// <summary>Like <see cref="Ref"/> with a count: key.one or key.other; {0} is the count.</summary>
    public static string RefPlural(string key, long count, params object?[] args) => Encode(key, count, args);

    private static string Encode(string key, long? count, object?[] args)
    {
        // A nested reference goes in as an object {"R": {...}}; plain strings stay strings and are never read as a reference.
        var a = args.Select(x => x is string s && Made.TryGetValue(s, out _) ? (object?)new { R = JsonSerializer.Deserialize<JsonElement>(s.AsSpan(1)) } : x).ToArray();
        var text = RefMark + JsonSerializer.Serialize(new { K = key, N = count, A = a });
        Made.AddOrUpdate(text, Options);
        return text;
    }

    /// <summary>The text for a reference from <see cref="Ref"/>; any other text comes back unchanged.</summary>
    public static string Render(string text) => RenderIn(Language, text);

    internal static string RenderIn(string language, string text)
    {
        // Only a string Ref made in this process is a reference; text from outside that merely starts with the mark stays text.
        if (text.Length == 0 || text[0] != RefMark || !Made.TryGetValue(text, out _)) return text;
        RefData? data;
        try
        {
            data = JsonSerializer.Deserialize<RefData>(text.AsSpan(1), Options);
        }
        catch (JsonException)
        {
            return text[1..];
        }
        return data is { K: not null } ? RenderData(language, data, 0) : text[1..];
    }

    private static string RenderData(string language, RefData data, int depth)
    {
        var args = (data.A ?? []).Select(a => a.ValueKind switch
        {
            // Plain text, shown as it is: a string argument is never rendered as a reference.
            JsonValueKind.String => a.GetString() ?? "",
            JsonValueKind.Number => a.TryGetInt64(out var l) ? (object)l : a.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Object => Nested(language, a, depth),
            _ => ""
        }).ToArray();
        return data.N is { } n ? PluralIn(language, data.K, n, args) : GetIn(language, data.K, args);
    }

    /// <summary>A nested reference made by <see cref="Ref"/> (an object {"R": ...}), at most a few levels deep.</summary>
    private static string Nested(string language, JsonElement a, int depth)
    {
        if (depth >= 8 || !a.TryGetProperty("R", out var r) || r.ValueKind != JsonValueKind.Object) return "";
        try
        {
            return r.Deserialize<RefData>(Options) is { K: not null } d ? RenderData(language, d, depth + 1) : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }
}
