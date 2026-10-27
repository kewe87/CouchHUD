using System.Text.Json;
using System.Text.RegularExpressions;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Integrations.CaseHud;

/// <summary>What GET /status says about lighting. Present only in builds that have the /lighting endpoint (apiVersion stays 1).</summary>
public sealed record CaseHudLightingSummary(bool Enabled, bool Active);

/// <summary>
/// One parameter of an effect. Type: number, color, bool, enum or text; keys are a frozen contract on CaseHUD's side.
/// <see cref="Label"/> is English, <see cref="LabelDe"/> German (featured effects); <see cref="Step"/> is the step of a number.
/// </summary>
public sealed record CaseHudEffectParam(
    string Key, string Type, double? Min = null, double? Max = null, JsonElement? Default = null, IReadOnlyList<string>? Values = null,
    string? Label = null, string? LabelDe = null, double? Step = null);

/// <summary>An effect CaseHUD offers. Only <see cref="Featured"/> ones are shown; any name GET returned is accepted.</summary>
public sealed record CaseHudEffect(string Name, string? Label = null, bool Featured = false, IReadOnlyList<CaseHudEffectParam>? Params = null, string? LabelDe = null);

/// <summary>
/// A lighting device (controller, strip). <see cref="State"/>: ok, conflict, disabled or error, with an optional <see cref="Message"/>; both are only shown.
/// From the couch only <see cref="Disabled"/> is changed; settings and ignoreConflicts are CaseHUD-window-only (403 gui_only).
/// </summary>
public sealed record CaseHudLightingDevice(string Id, string Name = "", bool Disabled = false, string Backend = "", string State = "", string? Message = null, bool IgnoreConflicts = false);

/// <summary>GET /api/v1/lighting, and the answer of every PATCH (the full state, the same shape).</summary>
public sealed record CaseHudLightingState(
    bool Enabled, int Brightness, string Effect = "", Dictionary<string, JsonElement>? Params = null,
    IReadOnlyList<CaseHudLightingDevice>? Devices = null, IReadOnlyList<CaseHudEffect>? Effects = null);

/// <summary>
/// The rules for lighting between the page and CaseHUD: which build has it, how its refusals read, and what a PATCH may contain.
/// The page sends JSON through the bridge; only what passes <see cref="BuildPatch"/> goes on to CaseHUD, checked against the last state.
/// CaseHUD merges a PATCH: unsent fields, params, devices and zones keep their values, so the page sends only what changed.
/// </summary>
public static partial class CaseHudLighting
{
    public const int MaxDevices = 64;

    /// <summary>What is taken from an answer of CaseHUD: at most this many effects, params, values and devices, strings at most this long.</summary>
    public const int MaxEntries = 128;
    public const int MaxString = 128;

    /// <summary>
    /// GET /lighting (or a PATCH answer) read defensively: entries beyond <see cref="MaxEntries"/> and strings longer than
    /// <see cref="MaxString"/> are dropped, an effect, param or device without a string name/key/id is skipped, wrong types fall back
    /// to their default. A broken entry costs that entry, not the page. Null when the answer is no object at all.
    /// </summary>
    public static CaseHudLightingState? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var effects = Items(root, "effects").Select(ParseEffect).OfType<CaseHudEffect>().ToList();
        var devices = Items(root, "devices").Select(ParseDevice).OfType<CaseHudLightingDevice>().ToList();
        var parameters = new Dictionary<string, JsonElement>();
        if (root.TryGetProperty("params", out var ps) && ps.ValueKind == JsonValueKind.Object)
            foreach (var p in ps.EnumerateObject())
            {
                if (parameters.Count >= MaxEntries) break;
                if (p.Name.Length > MaxString || !Scalar(p.Value)) continue;
                parameters[p.Name] = p.Value.Clone();
            }
        var brightness = Number(root, "brightness") is { } b ? (int)Math.Round(Math.Clamp(b, 0, 100)) : 0;
        return new CaseHudLightingState(Bool(root, "enabled"), brightness, Text(root, "effect") ?? "", parameters, devices, effects);
    }

    private static CaseHudEffect? ParseEffect(JsonElement e)
    {
        if (Text(e, "name") is not { Length: > 0 } name) return null;
        var parameters = Items(e, "params").Select(ParseParam).OfType<CaseHudEffectParam>().ToList();
        return new CaseHudEffect(name, Text(e, "label"), Bool(e, "featured"), parameters, Text(e, "labelDe"));
    }

    private static CaseHudEffectParam? ParseParam(JsonElement p)
    {
        if (Text(p, "key") is not { Length: > 0 } key || Text(p, "type") is not { Length: > 0 } type) return null;
        JsonElement? def = p.ValueKind == JsonValueKind.Object && p.TryGetProperty("default", out var d) && Scalar(d) ? d.Clone() : null;
        var values = Items(p, "values").Where(v => v.ValueKind == JsonValueKind.String && v.GetString()!.Length <= MaxString)
            .Select(v => v.GetString()!).ToList();
        // A step that is no positive number is no step (the page falls back to its own guess).
        var step = Number(p, "step") is > 0 and var s ? s : (double?)null;
        return new CaseHudEffectParam(key, type, Number(p, "min"), Number(p, "max"), def, values.Count > 0 ? values : null, Text(p, "label"), Text(p, "labelDe"), step);
    }

    /// <summary>A device needs a string id; a name that is there but no short string drops the device too. Settings and zones are not read (never sent).</summary>
    private static CaseHudLightingDevice? ParseDevice(JsonElement d)
    {
        if (Text(d, "id") is not { Length: > 0 } id) return null;
        var name = Text(d, "name");
        if (name is null && d.TryGetProperty("name", out _)) return null;
        var state = Text(d, "state") is "ok" or "conflict" or "disabled" or "error" ? Text(d, "state")! : "";
        return new CaseHudLightingDevice(id, name ?? "", Bool(d, "disabled"), Text(d, "backend") ?? "", state, Text(d, "message"), Bool(d, "ignoreConflicts"));
    }

    /// <summary>The first <see cref="MaxEntries"/> entries of a list; the rest is dropped.</summary>
    private static IEnumerable<JsonElement> Items(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().Take(MaxEntries) : [];

    /// <summary>A string of at most <see cref="MaxString"/> characters; anything else is null.</summary>
    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Length <= MaxString ? v.GetString() : null;

    private static bool Bool(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static double? Number(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n) ? n : null;

    /// <summary>A param value or default: a number, a bool or a string of at most <see cref="MaxString"/> characters. Lists and objects are not taken.</summary>
    private static bool Scalar(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString()!.Length <= MaxString,
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => true,
        _ => false
    };

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
    /// message, shortened), 413 payload_too_large, 500 internal (device I/O, worth another try), 401/403 forbidden or unauthorized, 405.
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
            ("forbidden" or "unauthorized", _) or (_, 401 or 403) => Strings.Get("casehud.err.forbidden"),
            (_, 405) => Strings.Get("casehud.err.notAllowed"),
            _ => message
        };
    }

    public const int MaxMessage = 200;

    /// <summary>
    /// CaseHUD's message as it may be shown: control and format characters removed (bidi overrides, zero-width marks), cut after
    /// <see cref="MaxMessage"/> text elements, so a surrogate pair or a combined character is never split.
    /// </summary>
    public static string SafeMessage(string? message)
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
        return info.LengthInTextElements > MaxMessage ? info.SubstringByTextElements(0, MaxMessage) + "…" : text;
    }

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorPattern();

    private static readonly HashSet<string> Fields = ["enabled", "brightness", "effect", "params", "devices"];

    /// <summary>
    /// A PATCH body from what the page sent, against the last state: only known fields, brightness clamped to 0..100, an effect the
    /// state listed, params of the effect in force (the new one when effect and params come together) by their descriptors, and
    /// devices as an object keyed by a known id with nothing but <c>disabled</c>. Text params and types this version does not know are
    /// never sent. Anything else throws <see cref="ArgumentException"/>; nothing of the page's JSON is passed on as it is.
    /// </summary>
    public static Dictionary<string, object> BuildPatch(JsonElement patch, CaseHudLightingState last)
    {
        if (patch.ValueKind != JsonValueKind.Object) throw new ArgumentException("lighting: patch must be an object");
        var body = new Dictionary<string, object>();
        foreach (var field in patch.EnumerateObject())
            if (!Fields.Contains(field.Name)) throw new ArgumentException($"lighting: unknown field {field.Name}");

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

        var effect = last.Effects?.FirstOrDefault(e => e.Name == last.Effect);
        if (patch.TryGetProperty("effect", out var name))
        {
            if (name.ValueKind != JsonValueKind.String) throw new ArgumentException("lighting: effect must be a string");
            var wanted = name.GetString();
            effect = last.Effects?.FirstOrDefault(e => e.Name == wanted) ?? throw new ArgumentException("lighting: unknown effect");
            body["effect"] = effect.Name;
        }

        if (patch.TryGetProperty("params", out var parameters))
        {
            if (parameters.ValueKind != JsonValueKind.Object) throw new ArgumentException("lighting: params must be an object");
            if (effect is null) throw new ArgumentException("lighting: no effect to set params for");
            var values = new Dictionary<string, object>();
            foreach (var p in parameters.EnumerateObject())
            {
                var descriptor = effect.Params?.FirstOrDefault(d => d.Key == p.Name) ?? throw new ArgumentException($"lighting: unknown param {p.Name}");
                values[descriptor.Key] = ParamValue(descriptor, p.Value);
            }
            if (values.Count > 0) body["params"] = values;
        }

        if (patch.TryGetProperty("devices", out var devices))
        {
            // CaseHUD merges by id: {"devices":{"<id>":{"disabled":true}}}. Settings and ignoreConflicts are window-only.
            if (devices.ValueKind != JsonValueKind.Object) throw new ArgumentException("lighting: devices must be an object keyed by id");
            var map = new Dictionary<string, object>();
            foreach (var d in devices.EnumerateObject())
            {
                if (map.Count >= MaxDevices) throw new ArgumentException("lighting: too many devices");
                if (last.Devices?.Any(x => x.Id == d.Name) != true || map.ContainsKey(d.Name)) throw new ArgumentException("lighting: unknown device");
                if (d.Value.ValueKind != JsonValueKind.Object) throw new ArgumentException("lighting: device must be an object");
                foreach (var f in d.Value.EnumerateObject())
                    if (f.Name != "disabled") throw new ArgumentException($"lighting: device field {f.Name} cannot be set here");
                if (!d.Value.TryGetProperty("disabled", out var off) || off.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new ArgumentException("lighting: device needs disabled true or false");
                map[d.Name] = new Dictionary<string, object> { ["disabled"] = off.ValueKind == JsonValueKind.True };
            }
            if (map.Count > 0) body["devices"] = map;
        }

        if (body.Count == 0) throw new ArgumentException("lighting: nothing to change");
        return body;
    }

    private static object ParamValue(CaseHudEffectParam d, JsonElement v)
    {
        switch (d.Type)
        {
            case "number":
            {
                // Without both bounds there is nothing to clamp to: such a param is not set from the couch.
                if (d.Min is not { } min || d.Max is not { } max || min > max) throw new ArgumentException($"lighting: {d.Key} has no range");
                if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var n) || !double.IsFinite(n)) throw new ArgumentException($"lighting: {d.Key} must be a number");
                n = Math.Clamp(n, min, max);
                // Whole numbers stay whole; a decimal step keeps two places at most (0.1 + 0.2 is not 0.30000000000000004 on the wire).
                var rounded = Integral(d) ? Math.Round(n, MidpointRounding.AwayFromZero) : Math.Round(n, 2, MidpointRounding.AwayFromZero);
                // Rounding may step over a bound that is not on the grid (max 4.5 with whole steps, min 0.333): clamp again.
                return Math.Clamp(rounded, min, max);
            }
            case "color":
            {
                var s = v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
                if (!ColorPattern().IsMatch(s)) throw new ArgumentException($"lighting: {d.Key} must be a color #rrggbb");
                return s.ToLowerInvariant();
            }
            case "bool":
                return v.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => throw new ArgumentException($"lighting: {d.Key} must be true or false")
                };
            case "enum":
            {
                var s = v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                if (s is null || d.Values?.Contains(s) != true) throw new ArgumentException($"lighting: {d.Key} has no such value");
                return s;
            }
            default:
                // Text has no clean controller input and is never sent; a type this version does not know neither.
                throw new ArgumentException($"lighting: {d.Key} cannot be set here");
        }
    }

    /// <summary>Whole numbers: the step says so when there is one, else integer bounds and default.</summary>
    private static bool Integral(CaseHudEffectParam d)
    {
        static bool Whole(double? x) => x is null || x == Math.Floor(x.Value);
        if (d.Step is { } step) return Whole(step) && Whole(d.Min);
        var def = d.Default is { ValueKind: JsonValueKind.Number } e && e.TryGetDouble(out var x) ? x : (double?)null;
        return Whole(d.Min) && Whole(d.Max) && Whole(def);
    }
}
