using System.Text.Json;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Integrations.CaseHud;

/// <summary>What GET /status says about lighting. Present only in builds that have the /lighting endpoint (apiVersion stays 1).</summary>
public sealed record CaseHudLightingSummary(bool Enabled, bool Active);

/// <summary>
/// An effect CaseHUD offers. Only <see cref="Featured"/> ones are shown; any name GET returned is accepted. Its parameters are not read:
/// CaseHUD keeps them per effect and they are set in its window. <see cref="Name"/> stays as CaseHUD sent it (it goes back in a PATCH);
/// <see cref="Display"/> is the name as the page may show it when there is no label: cleaned and capped like a label.
/// </summary>
public sealed record CaseHudEffect(string Name, string? Label = null, bool Featured = false, string? LabelDe = null, string Display = "");

/// <summary>
/// GET /api/v1/lighting, and the answer of every PATCH, as far as the couch needs it: on/off, brightness, the effect and the list to choose from.
/// <see cref="EffectDisplay"/> is the current effect's name cleaned for the page (for an effect that is not in the list).
/// </summary>
public sealed record CaseHudLightingState(bool Enabled, int Brightness, string Effect = "", IReadOnlyList<CaseHudEffect>? Effects = null, string EffectDisplay = "");

/// <summary>
/// The rules for lighting between the page and CaseHUD: which build has it, how its refusals read, and what a PATCH may contain.
/// The page sends JSON through the bridge; only what passes <see cref="BuildPatch"/> goes on to CaseHUD, checked against the last state.
/// From the couch: on/off, brightness and the effect. Effect parameters, devices and zones are built in CaseHUD's window (CaseHUD merges a
/// PATCH, so they keep their values).
/// </summary>
public static class CaseHudLighting
{
    /// <summary>What is taken from an answer of CaseHUD: at most this many effects, strings at most this long.</summary>
    public const int MaxEntries = 128;
    public const int MaxString = 128;

    /// <summary>
    /// GET /lighting (or a PATCH answer) read defensively: effects beyond <see cref="MaxEntries"/> and strings longer than
    /// <see cref="MaxString"/> are dropped, an effect without a string name is skipped, wrong types fall back to their default.
    /// Params, devices and zones are not read. A broken entry costs that entry, not the page. Null when the answer is no object at all.
    /// </summary>
    public static CaseHudLightingState? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var effects = Items(root, "effects").Select(ParseEffect).OfType<CaseHudEffect>().ToList();
        var brightness = Number(root, "brightness") is { } b ? (int)Math.Round(Math.Clamp(b, 0, 100)) : 0;
        var effect = Text(root, "effect") ?? "";
        return new CaseHudLightingState(Bool(root, "enabled"), brightness, effect, effects, SafeMessage(effect, MaxLabel));
    }

    /// <summary>What the page shows of an effect is at most this many text elements long.</summary>
    public const int MaxLabel = 64;

    /// <summary>
    /// The labels and the display name are shown on the page: cleaned like a message (no control or bidi characters) and capped at
    /// <see cref="MaxLabel"/> text elements. The name itself stays raw, it is what a PATCH sends back.
    /// </summary>
    private static CaseHudEffect? ParseEffect(JsonElement e) =>
        Text(e, "name") is { Length: > 0 } name
            ? new CaseHudEffect(name, Label(e, "label"), Bool(e, "featured"), Label(e, "labelDe"), SafeMessage(name, MaxLabel))
            : null;

    private static string? Label(JsonElement e, string name) => Text(e, name) is { } raw && SafeMessage(raw, MaxLabel) is { Length: > 0 } clean ? clean : null;

    /// <summary>The first <see cref="MaxEntries"/> entries of a list; the rest is dropped.</summary>
    private static IEnumerable<JsonElement> Items(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Take(MaxEntries) : [];

    /// <summary>A string of at most <see cref="MaxString"/> characters; anything else is null.</summary>
    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Length <= MaxString ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static double? Number(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;

    /// <summary>The build has /lighting when its status carries <c>lighting</c>; older builds lack it and must not be asked.</summary>
    public static bool Supported(CaseHudStatus? status) => status?.Lighting is not null;

    /// <summary>
    /// The first-start disclaimer: true or false when the build says so, null when it does not (older builds). Null is "unknown", never "not accepted".
    /// </summary>
    public static bool NeedsDisclaimer(CaseHudStatus? status) => status?.DisclaimerAccepted == false;

    /// <summary>
    /// The refusals the page answers with a question instead of an error: "ackRequired" (409, nothing was applied: confirm the
    /// lighting notice in CaseHUD's window) and "guiOnly" (403: only in CaseHUD's window).
    /// </summary>
    public static string? Problem(CaseHudException ex) => ex.Code switch
    {
        "ack_required" => "ackRequired",
        "gui_only" => "guiOnly",
        _ => null
    };

    /// <summary>
    /// A readable text for the other refusals of /lighting (error format {"error":{"code","message"}}): 400 bad_request (with CaseHUD's
    /// message, shortened), 413 payload_too_large, 500 internal (device I/O, worth another try), 401/403 forbidden or unauthorized, 404, 405.
    /// Codes this version does not know keep CaseHUD's own message.
    /// </summary>
    public static string ErrorText(CaseHudException ex)
    {
        var message = SafeMessage(ex.Message);
        return (ex.Code, ex.Status) switch
        {
            (CaseHudState.ApiDisabledCode, 403) => Strings.Get("casehud.apiOff"),
            ("bad_request", _) or (_, 400) => Strings.Get("casehud.err.badRequest", message),
            ("payload_too_large", _) or (_, 413) => Strings.Get("casehud.err.tooLarge"),
            ("internal", _) or (_, 500) => Strings.Get("casehud.err.internal"),
            ("unauthorized", _) => Strings.Get("casehud.err.unauthorized"),
            ("forbidden", _) or (_, 401 or 403) => Strings.Get("casehud.err.forbidden"),
            ("not_found", _) or (_, 404) => Strings.Get("casehud.err.notFound"),
            (_, 405) => Strings.Get("casehud.err.notAllowed"),
            _ => message
        };
    }

    public const int MaxMessage = 200;

    /// <summary>
    /// CaseHUD's message as it may be shown: control and format characters removed (bidi overrides, zero-width marks), cut after
    /// <paramref name="max"/> (default <see cref="MaxMessage"/>) text elements, so a surrogate pair or a combined character is never split.
    /// </summary>
    public static string SafeMessage(string? message, int max = MaxMessage)
    {
        if (string.IsNullOrEmpty(message)) return "";
        var clean = new System.Text.StringBuilder(message.Length);
        foreach (var rune in message.EnumerateRunes())
        {
            var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(rune.Value);
            if (category is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.Surrogate or System.Globalization.UnicodeCategory.OtherNotAssigned) continue;
            clean.Append(rune.ToString());
        }
        var text = clean.ToString().Trim();
        var info = new System.Globalization.StringInfo(text);
        return info.LengthInTextElements > max ? info.SubstringByTextElements(0, max) + "…" : text;
    }

    private static readonly HashSet<string> Fields = ["enabled", "brightness", "effect"];

    /// <summary>
    /// A PATCH body from what the page sent, against the last state: only enabled, brightness (clamped to 0..100) and an effect the state
    /// listed. Params, devices and anything else throw <see cref="ArgumentException"/>; nothing of the page's JSON is passed on as it is.
    /// </summary>
    public static Dictionary<string, object> BuildPatch(JsonElement patch, CaseHudLightingState last)
    {
        if (patch.ValueKind != JsonValueKind.Object) throw new ArgumentException("lighting: patch must be an object");
        var body = new Dictionary<string, object>();
        foreach (var field in patch.EnumerateObject())
            if (!Fields.Contains(field.Name)) throw new ArgumentException($"lighting: {field.Name} cannot be set here");

        if (patch.TryGetProperty("enabled", out var enabled))
            body["enabled"] = enabled.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new ArgumentException("lighting: enabled must be true or false")
            };

        if (patch.TryGetProperty("brightness", out var brightness))
        {
            if (brightness.ValueKind != JsonValueKind.Number || !brightness.TryGetDouble(out var b) || !double.IsFinite(b))
                throw new ArgumentException("lighting: brightness must be a number");
            body["brightness"] = (int)Math.Round(Math.Clamp(b, 0, 100));
        }

        if (patch.TryGetProperty("effect", out var name))
        {
            if (name.ValueKind != JsonValueKind.String) throw new ArgumentException("lighting: effect must be a string");
            var wanted = name.GetString();
            body["effect"] = last.Effects?.FirstOrDefault(e => e.Name == wanted)?.Name ?? throw new ArgumentException("lighting: unknown effect");
        }

        if (body.Count == 0) throw new ArgumentException("lighting: nothing to change");
        return body;
    }
}
