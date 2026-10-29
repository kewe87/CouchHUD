using System.Text.Json;

namespace CouchHud.Core.Config;

/// <summary>
/// The record of an App/Shell mode switch in progress: <c>{"from":"app"|"shell","steps":[{"id":"lock","before":{...}}]}</c>. The page writes it
/// before the first step and clears it when the switch ended or was rolled back; a record found at the next start means the switch was cut
/// off. The account (a program running as the user) can write the settings file, so nothing in it is trusted: only these step ids, only the
/// keys of that step and only values of the known sets or bounded plain names are accepted, nested or oversized values are refused, and the
/// page checks the same whitelist again before it restores anything (ui/core/mode-transaction.js, <c>validBefore</c>).
/// </summary>
public static class ModeSwitchJournal
{
    public const int MaxLength = 4096;
    private const int MaxSteps = 16;
    private const int MaxName = 96;

    private enum Kind { Bool, Names, Mode, Integration, Startup }

    // step id -> allowed keys of "before" and their kind. A step with an empty table has no record: its "before" must be null or empty.
    private static readonly Dictionary<string, Dictionary<string, Kind>> Steps = new(StringComparer.Ordinal)
    {
        ["integration"] = new() { ["m"] = Kind.Integration },
        ["autostart"] = new() { ["r"] = Kind.Bool, ["e"] = Kind.Startup },
        ["services"] = new() { ["k"] = Kind.Bool, ["s"] = Kind.Names },
        ["xbox"] = new() { ["v"] = Kind.Bool },
        ["suppress"] = new() { ["v"] = Kind.Bool },
        ["lock"] = new() { ["m"] = Kind.Mode, ["c"] = Kind.Bool },
        ["shell"] = new() { ["s"] = Kind.Bool },
        ["autologon"] = new() { ["on"] = Kind.Bool },
        ["release"] = new() { ["p"] = Kind.Bool },
        ["profiles"] = new() { ["p"] = Kind.Names },
        ["light"] = new() { ["m"] = Kind.Integration, ["s"] = Kind.Bool, ["l"] = Kind.Mode }
    };

    public static bool IsValid(string? text)
    {
        if (string.IsNullOrEmpty(text)) return true;
        if (text.Length > MaxLength) return false;
        try
        {
            using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            foreach (var property in root.EnumerateObject())
                if (property.Name is not ("from" or "steps")) return false;
            if (!root.TryGetProperty("from", out var from) || from.ValueKind != JsonValueKind.String || from.GetString() is not ("app" or "shell")) return false;
            if (!root.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array || steps.GetArrayLength() > MaxSteps) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var step in steps.EnumerateArray())
            {
                if (step.ValueKind != JsonValueKind.Object) return false;
                foreach (var property in step.EnumerateObject())
                    if (property.Name is not ("id" or "before")) return false;
                if (!step.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) return false;
                var name = id.GetString() ?? "";
                if (!Steps.TryGetValue(name, out var keys) || !seen.Add(name)) return false;
                if (!step.TryGetProperty("before", out var before) || before.ValueKind == JsonValueKind.Null) continue;
                if (before.ValueKind != JsonValueKind.Object || !BeforeIsValid(before, keys)) return false;
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool BeforeIsValid(JsonElement before, Dictionary<string, Kind> keys)
    {
        foreach (var property in before.EnumerateObject())
        {
            if (!keys.TryGetValue(property.Name, out var kind)) return false;
            var v = property.Value;
            var ok = kind switch
            {
                Kind.Bool => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
                Kind.Mode => v.ValueKind == JsonValueKind.String && (v.GetString() is "off" or "code" or "windows"),
                Kind.Integration => v.ValueKind == JsonValueKind.String && (v.GetString() is "light" or "full"),
                Kind.Names => v.ValueKind == JsonValueKind.Array && v.GetArrayLength() <= 24 && v.EnumerateArray().All(IsName),
                Kind.Startup => v.ValueKind == JsonValueKind.Array && v.GetArrayLength() <= 24 && v.EnumerateArray().All(IsStartupPair),
                _ => false
            };
            if (!ok) return false;
        }
        return true;
    }

    private static bool IsName(JsonElement e) =>
        e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 and <= MaxName } s && s.All(c => !char.IsControl(c));

    private static bool IsStartupPair(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array && e.GetArrayLength() == 2
        && IsName(e[0]) && e[1].ValueKind == JsonValueKind.String && (e[1].GetString() is "couch" or "pc");
}
