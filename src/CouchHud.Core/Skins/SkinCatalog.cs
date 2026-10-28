using System.Text;
using System.Text.Json;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Skins;

public sealed record SkinInfo(string Id, string Name, string Description, IReadOnlyDictionary<string, string> Glyphs, string Css)
{
    /// <summary>UI event ("move", "select" …) → sound file URI, for skins that bring their own sounds.</summary>
    public IReadOnlyDictionary<string, string> Sounds { get; init; } = new Dictionary<string, string>();

    /// <summary>The name in every language the skin brings (skin.json "name": { "de": …, "en": … }), so a request can use either.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>One of the sound sets of the app (<see cref="SkinCatalog.SoundSetIds"/>) the skin asks for, or null.</summary>
    public string? SoundSet { get; init; }
}

/// <summary>
/// User skins: &lt;dataDir&gt;/skins/&lt;folder&gt;/skin.json + skin.css.
/// skin.json: { "name": "..." or { "de": "...", "en": "..." }, "description": the same, "glyphs": { "A": "✕" },
/// "soundSet": "deepsea", "sounds": { "select": "select.ogg" } }.
/// The stylesheet scopes itself via :root[data-skin="user:&lt;folder&gt;"].
/// Only the Couch skin lives in the UI itself (ui/skins); every other skin comes from the catalog and is a user skin like any other.
/// </summary>
public static class SkinCatalog
{
    /// <summary>Limits for what a skin.json may say; the text ends up on screen, so it is cut and cleaned.</summary>
    public const int MaxManifestBytes = 64 * 1024;
    public const int MaxNameLength = 40;
    public const int MaxDescriptionLength = 300;
    public const int MaxGlyphs = 32;
    public const int MaxGlyphKeyLength = 12;
    public const int MaxGlyphLength = 12;

    internal sealed record Manifest(IReadOnlyDictionary<string, string> Names, IReadOnlyDictionary<string, string> Descriptions,
        Dictionary<string, string> Glyphs, Dictionary<string, string> Sounds, string? SoundSet);

    /// <summary>The skins the app itself brings. Since 0.6 that is the Couch skin only.</summary>
    public static readonly string[] BuiltinIds = ["default"];

    /// <summary>
    /// The skins that were built in up to 0.5 and live in the catalog since 0.6. The catalog id is the same, the skin's id on the
    /// page is "user:&lt;id&gt;" like for every installed skin.
    /// </summary>
    public static readonly string[] MovedToCatalogIds = ["deepsea", "greenlight", "neoncity", "nova"];

    /// <summary>The sound sets the page can synthesize: the default one and one per skin that was built in once.</summary>
    public static readonly string[] SoundSetIds = ["default", .. MovedToCatalogIds];

    /// <summary>The id a skin has on the page when it came from the catalog (or a folder): "user:&lt;folder&gt;".</summary>
    public static string UserId(string folder) => "user:" + folder;

    /// <summary>The catalog id of a skin that left the app in 0.6, or null for any other skin id.</summary>
    public static string? MovedCatalogId(string? skinId) =>
        skinId is not null && skinId.StartsWith("user:", StringComparison.Ordinal) && MovedToCatalogIds.Contains(skinId[5..], StringComparer.Ordinal) ? skinId[5..] : null;

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

    /// <summary>
    /// The id a stored skin value has now: the 0.4 ids first (<see cref="MigrateId"/>), then the four skins that moved to the catalog
    /// in 0.6 become "user:&lt;id&gt;". Anything else, user skins and unknown ids included, passes through untouched.
    /// </summary>
    public static string? MigrateSkinId(string? id)
    {
        var current = MigrateId(id);
        return current is not null && MovedToCatalogIds.Contains(current, StringComparer.Ordinal) ? UserId(current) : current;
    }

    /// <summary>The name of a skin that was built in once, in a language ("de" or "en"), the same as the UI shows (ui/i18n/*/core.js "skins.name.*").</summary>
    public static string BuiltinName(string id, string language) => Strings.GetIn(language, "skins.name." + id);

    /// <summary>
    /// The skin a request means (assistant, MCP): an id (an old one from before 0.5 too) or a name in any language,
    /// letters case-insensitive, then an installed skin's name if exactly one has it. Null when nothing or more than one skin fits.
    /// </summary>
    public static string? Resolve(string? text, IEnumerable<SkinInfo> userSkins)
    {
        var wanted = text?.Trim();
        if (string.IsNullOrEmpty(wanted)) return null;
        var users = userSkins.ToList();
        var id = MigrateSkinId(wanted)!;
        if (BuiltinIds.Contains(id) || users.Any(s => s.Id == id)) return id;

        foreach (var builtin in BuiltinIds.Concat(MovedToCatalogIds))
        {
            var target = BuiltinIds.Contains(builtin) ? builtin : UserId(builtin);
            // A skin that moved to the catalog is only meant when it is installed.
            if (target != builtin && users.All(s => s.Id != target)) continue;
            if (string.Equals(builtin, wanted, StringComparison.OrdinalIgnoreCase)
                || string.Equals(BuiltinName(builtin, "de"), wanted, StringComparison.OrdinalIgnoreCase)
                || string.Equals(BuiltinName(builtin, "en"), wanted, StringComparison.OrdinalIgnoreCase)) return target;
        }
        foreach (var (old, now) in RenamedIds)
        {
            if (string.Equals(old, wanted, StringComparison.OrdinalIgnoreCase) && users.Any(s => s.Id == UserId(now))) return UserId(now);
        }
        // A name of the Couch skin or of a skin that moved to the catalog (an id or either language's name) belongs to that skin alone:
        // another installed skin cannot take the name over, only the skin in the folder of that very id answers to it.
        var reserved = BuiltinIds.Concat(MovedToCatalogIds).Where(r =>
            string.Equals(r, wanted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(BuiltinName(r, "de"), wanted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(BuiltinName(r, "en"), wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        var named = users.Where(s => (string.Equals(s.Name.Trim(), wanted, StringComparison.OrdinalIgnoreCase)
                || s.Aliases.Any(a => string.Equals(a.Trim(), wanted, StringComparison.OrdinalIgnoreCase)))
            && (reserved.Count == 0 || reserved.Any(r => s.Id == UserId(r)))).Take(2).ToList();
        return named.Count == 1 ? named[0].Id : null;
    }

    /// <summary>A skin id the HUD may switch to: the Couch skin or an installed user skin. Anything else is refused, locked or not.</summary>
    public static bool IsKnown(string? id, IEnumerable<SkinInfo> userSkins) =>
        !string.IsNullOrEmpty(id) && (BuiltinIds.Contains(id) || userSkins.Any(s => s.Id == id));

    public static readonly string[] SoundEvents = ["move", "tab", "select", "back", "bump", "open", "close", "notify", "toggle", "boot", "music"];
    private static readonly string[] SoundTypes = [".wav", ".ogg", ".mp3"];
    private static readonly string[] Languages = ["de", "en"];

    public static IReadOnlyList<SkinInfo> ListUserSkins(string skinsDir, string? language = null)
    {
        if (!Directory.Exists(skinsDir)) return [];
        language ??= Strings.Language;

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
                var meta = ReadManifest(manifest);
                skins.Add(new SkinInfo(
                    UserId(folder),
                    Pick(meta.Names, language) ?? folder,
                    Pick(meta.Descriptions, language) ?? Strings.Get("skins.custom"),
                    meta.Glyphs,
                    new Uri(css).AbsoluteUri)
                {
                    Sounds = SoundsOf(dir, meta.Sounds),
                    Aliases = meta.Names.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                    SoundSet = meta.SoundSet
                });
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
            {
                Console.Error.WriteLine($"[skins] {folder}: {ex.Message}");
            }
        }
        return skins;
    }

    /// <summary>The text for a language, else the other one (a skin that brings one language still has a name everywhere).</summary>
    private static string? Pick(IReadOnlyDictionary<string, string> texts, string language) =>
        texts.GetValueOrDefault(language) ?? texts.GetValueOrDefault("") ?? texts.GetValueOrDefault(language == "de" ? "en" : "de");

    /// <summary>
    /// Reads skin.json defensively: a size limit, wrong types are ignored instead of failing, and every text that reaches the screen
    /// is cleaned (control characters and angle brackets out, whitespace collapsed, length cut), so a skin cannot bring markup or a
    /// wall of text into the skin list, the setup assistant or the assistant's answers.
    /// </summary>
    internal static Manifest ReadManifest(string path)
    {
        if (new FileInfo(path).Length > MaxManifestBytes) throw new InvalidDataException("skin.json is too large");
        using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 8 });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("skin.json is not an object");

        var glyphs = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Property(root, "glyphs") is { ValueKind: JsonValueKind.Object } g)
        {
            foreach (var p in g.EnumerateObject())
            {
                if (glyphs.Count >= MaxGlyphs) break;
                var key = CleanText(p.Name, MaxGlyphKeyLength);
                var value = p.Value.ValueKind == JsonValueKind.String ? CleanText(p.Value.GetString(), MaxGlyphLength) : null;
                if (key is not null && value is not null) glyphs[key] = value;
            }
        }

        var sounds = new Dictionary<string, string>();
        if (Property(root, "sounds") is { ValueKind: JsonValueKind.Object } snd)
        {
            foreach (var p in snd.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { } file) sounds[p.Name] = file;
            }
        }

        string? soundSet = null;
        if (Property(root, "soundSet") is { ValueKind: JsonValueKind.String } ss && ss.GetString() is { } set && SoundSetIds.Contains(set, StringComparer.Ordinal)) soundSet = set;

        return new Manifest(Localized(root, "name", MaxNameLength), Localized(root, "description", MaxDescriptionLength), glyphs, sounds, soundSet);
    }

    // Names of properties are matched ignoring case, as before.
    private static JsonElement? Property(JsonElement obj, string name)
    {
        foreach (var p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        }
        return null;
    }

    /// <summary>"name": "text" (every language) or { "de": "…", "en": "…" }. Other languages and other types are ignored.</summary>
    private static IReadOnlyDictionary<string, string> Localized(JsonElement root, string property, int max)
    {
        var result = new Dictionary<string, string>();
        if (Property(root, property) is not { } v) return result;
        if (v.ValueKind == JsonValueKind.String)
        {
            if (CleanText(v.GetString(), max) is { } any) result[""] = any;
        }
        else if (v.ValueKind == JsonValueKind.Object)
        {
            foreach (var lang in Languages)
            {
                if (Property(v, lang) is { ValueKind: JsonValueKind.String } t && CleanText(t.GetString(), max) is { } text) result[lang] = text;
            }
        }
        return result;
    }

    /// <summary>
    /// One line of plain text: no control characters, no invisible format characters (bidi overrides and isolates such as U+202E and
    /// U+2066, zero-width characters U+200B-200F, the byte order mark), no lone surrogates, no angle brackets, spaces collapsed, at most
    /// <paramref name="max"/> UTF-16 units. Null when nothing is left.
    /// </summary>
    public static string? CleanText(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var sb = new StringBuilder(Math.Min(text.Length, max + 1));
        var space = false;
        foreach (var rune in text.EnumerateRunes())
        {
            // A lone surrogate comes out of the enumeration as U+FFFD: dropped like the other characters that are no text.
            if (rune.Value is '<' or '>' or 0xFFFD) continue;
            if (Rune.IsWhiteSpace(rune))
            {
                space = sb.Length > 0;
                continue;
            }
            // Control and invisible format characters (bidi overrides and isolates, zero-width characters, the byte order mark).
            if (Rune.GetUnicodeCategory(rune) is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.Surrogate or System.Globalization.UnicodeCategory.OtherNotAssigned) continue;
            var piece = rune.ToString();
            // Cut between characters: a valid surrogate pair stays whole or goes.
            if (sb.Length + (space ? 1 : 0) + piece.Length > max) break;
            if (space) { sb.Append(' '); space = false; }
            sb.Append(piece);
        }
        var result = sb.ToString().Trim();
        return result.Length == 0 ? null : result;
    }

    /// <summary>Only known events and audio files inside the skin's own folder.</summary>
    internal static IReadOnlyDictionary<string, string> SoundsOf(string dir, IReadOnlyDictionary<string, string>? sounds)
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
