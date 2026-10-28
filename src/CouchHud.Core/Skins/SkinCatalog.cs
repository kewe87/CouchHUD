using System.Text.Json;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Skins;

public sealed record SkinInfo(string Id, string Name, string Description, IReadOnlyDictionary<string, string> Glyphs, string Css)
{
    /// <summary>UI event ("move", "select" …) → sound file URI, for skins that bring their own sounds.</summary>
    public IReadOnlyDictionary<string, string> Sounds { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// User skins: &lt;dataDir&gt;/skins/&lt;folder&gt;/skin.json + skin.css.
/// skin.json: { "name": "...", "description": "...", "glyphs": { "A": "✕" }, "sounds": { "select": "select.ogg" } }.
/// The stylesheet scopes itself via :root[data-skin="user:&lt;folder&gt;"].
/// Built-in skins live in the UI itself (ui/skins).
/// </summary>
public static class SkinCatalog
{
    private sealed record Manifest(string? Name, string? Description, Dictionary<string, string>? Glyphs, Dictionary<string, string>? Sounds);

    public static readonly string[] BuiltinIds = ["default", "deepsea", "greenlight", "neoncity", "nova"];

    /// <summary>
    /// Ids the built-in skins (and their sound sets) had up to 0.4, mapped to the ids they have now. Only these three fixed ids change;
    /// anything else, unknown ids and user skins ("user:&lt;folder&gt;") included, passes through untouched.
    /// </summary>
    private static readonly Dictionary<string, string> RenamedIds = new(StringComparer.Ordinal)
    {
        ["ps4"] = "deepsea",
        ["xbox"] = "greenlight",
        ["cyberpunk"] = "neoncity",
    };

    /// <summary>The current id for a skin or sound set id from an older version; any other value as it is.</summary>
    public static string? MigrateId(string? id) => id is not null && RenamedIds.TryGetValue(id, out var now) ? now : id;

    /// <summary>The name of a built-in skin in a language ("de" or "en"), the same as the UI shows (ui/i18n/*/core.js "skins.name.*").</summary>
    public static string BuiltinName(string id, string language) => Strings.GetIn(language, "skins.name." + id);

    /// <summary>
    /// The skin a request means (assistant, MCP): an id (an old one from before 0.5 too) or a built-in skin's name in any language,
    /// letters case-insensitive, then a user skin's name if exactly one has it. Null when nothing or more than one skin fits.
    /// </summary>
    public static string? Resolve(string? text, IEnumerable<SkinInfo> userSkins)
    {
        var wanted = text?.Trim();
        if (string.IsNullOrEmpty(wanted)) return null;
        var users = userSkins.ToList();
        var id = MigrateId(wanted)!;
        if (BuiltinIds.Contains(id) || users.Any(s => s.Id == id)) return id;

        foreach (var builtin in BuiltinIds)
        {
            if (string.Equals(builtin, wanted, StringComparison.OrdinalIgnoreCase)
                || string.Equals(BuiltinName(builtin, "de"), wanted, StringComparison.OrdinalIgnoreCase)
                || string.Equals(BuiltinName(builtin, "en"), wanted, StringComparison.OrdinalIgnoreCase)) return builtin;
        }
        foreach (var (old, now) in RenamedIds)
        {
            if (string.Equals(old, wanted, StringComparison.OrdinalIgnoreCase)) return now;
        }
        var named = users.Where(s => string.Equals(s.Name.Trim(), wanted, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        return named.Count == 1 ? named[0].Id : null;
    }

    /// <summary>A skin id the HUD may switch to: one of the built-in skins or an installed user skin. Anything else is refused, locked or not.</summary>
    public static bool IsKnown(string? id, IEnumerable<SkinInfo> userSkins) =>
        !string.IsNullOrEmpty(id) && (BuiltinIds.Contains(id) || userSkins.Any(s => s.Id == id));

    public static readonly string[] SoundEvents = ["move", "tab", "select", "back", "bump", "open", "close", "notify", "toggle", "boot", "music"];
    private static readonly string[] SoundTypes = [".wav", ".ogg", ".mp3"];

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<SkinInfo> ListUserSkins(string skinsDir)
    {
        if (!Directory.Exists(skinsDir)) return [];

        var skins = new List<SkinInfo>();
        foreach (var dir in Directory.EnumerateDirectories(skinsDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var folder = Path.GetFileName(dir);
            if (folder.StartsWith(CouchHud.Core.Plugins.PackageInstaller.TempPrefix, StringComparison.Ordinal)) continue;
            var css = Path.Combine(dir, "skin.css");
            var manifest = Path.Combine(dir, "skin.json");
            if (!File.Exists(css) || !File.Exists(manifest)) continue;
            try
            {
                var meta = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(manifest), Options);
                skins.Add(new SkinInfo(
                    $"user:{folder}",
                    string.IsNullOrWhiteSpace(meta?.Name) ? folder : meta!.Name!,
                    meta?.Description ?? Strings.Get("skins.custom"),
                    meta?.Glyphs ?? new Dictionary<string, string>(),
                    new Uri(css).AbsoluteUri)
                {
                    Sounds = SoundsOf(dir, meta?.Sounds)
                });
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                Console.Error.WriteLine($"[skins] {folder}: {ex.Message}");
            }
        }
        return skins;
    }

    /// <summary>Only known events and audio files inside the skin's own folder.</summary>
    internal static IReadOnlyDictionary<string, string> SoundsOf(string dir, Dictionary<string, string>? sounds)
    {
        var result = new Dictionary<string, string>();
        if (sounds is null) return result;
        var root = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
        foreach (var (name, file) in sounds)
        {
            if (!SoundEvents.Contains(name) || string.IsNullOrWhiteSpace(file)) continue;
            var path = Path.GetFullPath(Path.Combine(dir, file));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !SoundTypes.Contains(Path.GetExtension(path).ToLowerInvariant()) || !File.Exists(path)) continue;
            result[name] = new Uri(path).AbsoluteUri;
        }
        return result;
    }
}
