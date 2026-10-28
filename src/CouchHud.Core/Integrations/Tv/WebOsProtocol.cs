using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CouchHud.Core.Integrations.Tv;

/// <summary>One SSAP message from the TV, already checked for shape: type, id, payload (cloned) and the error text if any.</summary>
public sealed record SsapMessage(string Type, string? Id, JsonElement Payload, string? Error);

/// <summary>
/// LG webOS "Second Screen Application Protocol" (SSAP), the messages only (no sockets): written from the behaviour documented by the
/// open-source clients aiowebostv (Apache-2.0, home-assistant-libs/aiowebostv: handshake.py, endpoints.py, webos_client.py), pywebostv
/// (MIT) and lgtv2 (MIT); no code copied. In short:
/// <list type="bullet">
/// <item>WebSocket on <c>wss://tv:3001</c> (self-signed certificate; webOS from about 2022 on, and older ones after firmware updates,
/// only accept this) or, on old TVs, <c>ws://tv:3000</c>.</item>
/// <item><c>{"type":"register","id":"register_0","payload":{"forcePairing":false,"pairingType":"PROMPT","client-key"?,"manifest":{…}}}</c>.
/// Without a known key the TV answers <c>type:"response"</c> with <c>pairingType:"PROMPT"</c>, shows a prompt, and after "Accept" sends
/// <c>type:"registered"</c> with <c>payload["client-key"]</c>; declining gives <c>type:"error"</c> ("403 User denied access").</item>
/// <item>Commands: <c>{"type":"request","id":…,"uri":"ssap://audio/volumeUp","payload":{…}}</c>, answered with the same id and
/// <c>type:"response"</c> (<c>payload.returnValue</c>) or <c>type:"error"</c> (<c>error</c> text, e.g. "401 insufficient permissions").</item>
/// </list>
/// Newer clients (aiowebostv) send the manifest without the old signed block; CouchHUD does the same and asks only for the permissions
/// its commands need (power, audio, inputs, foreground app, network info, toast).
/// </summary>
public static partial class WebOsProtocol
{
    public const int SecurePort = 3001;
    public const int LegacyPort = 3000;

    /// <summary>Largest message accepted from the TV (the input list with icon URLs is a few kB).</summary>
    public const int MaxMessageBytes = 256 * 1024;

    public const string RegisterId = "register_0";

    public const string TurnOff = "ssap://system/turnOff";
    public const string GetVolume = "ssap://audio/getVolume";
    public const string SetVolume = "ssap://audio/setVolume";
    public const string VolumeUp = "ssap://audio/volumeUp";
    public const string VolumeDown = "ssap://audio/volumeDown";
    public const string SetMute = "ssap://audio/setMute";
    public const string GetInputs = "ssap://tv/getExternalInputList";
    public const string SwitchInput = "ssap://tv/switchInput";
    public const string ForegroundApp = "ssap://com.webos.applicationManager/getForegroundAppInfo";
    public const string SystemInfo = "ssap://system/getSystemInfo";
    public const string ConnectionInfo = "ssap://com.webos.service.connectionmanager/getinfo";
    public const string CreateToast = "ssap://system.notifications/createToast";

    /// <summary>
    /// The permissions CouchHUD asks for at pairing, no more: power off and state, audio, the input list and switching, which app is in
    /// front, the network info (for the MAC that Wake-on-LAN needs) and a toast. Which permission a command needs is not documented by
    /// LG; this set follows the open-source clients. A command that answers "401" is reported as <see cref="TvError.Refused"/>.
    /// </summary>
    public static readonly IReadOnlyList<string> Permissions =
    [
        "CONTROL_POWER", "READ_POWER_STATE", "CONTROL_AUDIO", "READ_INPUT_DEVICE_LIST", "CONTROL_INPUT_TV",
        "READ_APP_STATUS", "READ_RUNNING_APPS", "READ_CURRENT_CHANNEL", "READ_NETWORK_STATE", "WRITE_NOTIFICATION_TOAST"
    ];

    /// <summary>The register message, with the stored key when there is one (then the TV does not ask again).</summary>
    public static byte[] Register(string? clientKey)
    {
        var payload = new Dictionary<string, object?>
        {
            ["forcePairing"] = false,
            ["pairingType"] = "PROMPT",
            ["manifest"] = new Dictionary<string, object?>
            {
                ["manifestVersion"] = 1,
                ["appVersion"] = "1.1",
                ["permissions"] = Permissions
            }
        };
        if (!string.IsNullOrEmpty(clientKey)) payload["client-key"] = clientKey;
        return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?> { ["type"] = "register", ["id"] = RegisterId, ["payload"] = payload });
    }

    /// <summary>A request; the payload is built by CouchHUD only (never from TV answers).</summary>
    public static byte[] Request(string id, string uri, object? payload = null)
    {
        var message = new Dictionary<string, object?> { ["type"] = "request", ["id"] = id, ["uri"] = uri };
        if (payload is not null) message["payload"] = payload;
        return JsonSerializer.SerializeToUtf8Bytes(message);
    }

    /// <summary>
    /// Parses one message strictly: a JSON object of at most <see cref="MaxMessageBytes"/>, depth 16, a string "type"; "id" a string
    /// (numbers are taken as text); payload an object (or absent); error a string, cut to 200 characters. Anything else: BadAnswer.
    /// </summary>
    public static SsapMessage Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > MaxMessageBytes) throw new TvException(TvError.BadAnswer, "message too large");
        try
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = 16 });
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new TvException(TvError.BadAnswer, "not an object");
            if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String) throw new TvException(TvError.BadAnswer, "no type");
            string? id = null;
            if (root.TryGetProperty("id", out var idEl))
            {
                id = idEl.ValueKind switch
                {
                    JsonValueKind.String => idEl.GetString(),
                    JsonValueKind.Number => idEl.GetRawText(),
                    _ => null
                };
            }
            var payload = root.TryGetProperty("payload", out var p) && p.ValueKind == JsonValueKind.Object ? p.Clone() : default;
            string? error = null;
            if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String)
            {
                error = e.GetString();
                if (error is { Length: > 200 }) error = error[..200];
            }
            return new SsapMessage(type.GetString()!, id, payload, error);
        }
        catch (JsonException)
        {
            throw new TvException(TvError.BadAnswer, "not JSON");
        }
    }

    /// <summary>True when an answer to a request says it failed (type error, or returnValue false).</summary>
    public static bool Failed(SsapMessage message) =>
        message.Type == "error"
        || (message.Payload.ValueKind == JsonValueKind.Object && message.Payload.TryGetProperty("returnValue", out var rv) && rv.ValueKind == JsonValueKind.False);

    /// <summary>The client key from a "registered" payload: printable ASCII, 8 to 128 characters; anything else is not a key.</summary>
    public static string? ClientKey(JsonElement payload)
    {
        var key = String(payload, "client-key");
        if (key is null || key.Length is < 8 or > 128 || !key.All(c => c is > ' ' and < (char)127)) return null;
        return key;
    }

    /// <summary>True when the payload of a "response" to the register message announces the prompt on the TV.</summary>
    public static bool IsPrompt(JsonElement payload) => String(payload, "pairingType") == "PROMPT";

    /// <summary>
    /// Volume and mute: newer webOS answers <c>{volumeStatus:{volume, muteStatus}}</c>, older ones <c>{volume, muted}</c> (as
    /// aiowebostv reads it). Volume clamped to 0–100.
    /// </summary>
    public static TvVolume ParseVolume(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw new TvException(TvError.BadAnswer, "volume");
        var source = payload.TryGetProperty("volumeStatus", out var status) && status.ValueKind == JsonValueKind.Object ? status : payload;
        if (!source.TryGetProperty("volume", out var v) || !v.TryGetInt32(out var volume)) throw new TvException(TvError.BadAnswer, "volume");
        var muted = Bool(source, "muteStatus") ?? Bool(source, "muted") ?? Bool(source, "mute") ?? false;
        return new TvVolume(Math.Clamp(volume, 0, 100), muted);
    }

    [GeneratedRegex(@"^[A-Za-z0-9_.:\-]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex InputIdPattern();

    public static bool IsValidInputId(string? id) => id is not null && InputIdPattern().IsMatch(id);

    /// <summary>At most this many inputs are taken from the list.</summary>
    public const int MaxInputs = 32;

    /// <summary>
    /// <c>{devices:[{id:"HDMI_1", label:"PlayStation", connected:true, …}]}</c>: ids that match a strict pattern only (they go back to the
    /// TV in switchInput), labels cleaned of control characters and cut to 48 characters (the UI shows them as text, never as HTML).
    /// </summary>
    public static IReadOnlyList<TvInput> ParseInputs(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array)
        {
            throw new TvException(TvError.BadAnswer, "inputs");
        }
        var list = new List<TvInput>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in devices.EnumerateArray())
        {
            if (list.Count >= MaxInputs) break;
            if (d.ValueKind != JsonValueKind.Object) continue;
            var id = String(d, "id");
            if (!IsValidInputId(id) || !seen.Add(id!)) continue;
            var label = CleanLabel(String(d, "label")) ?? id!;
            list.Add(new TvInput(id!, label, Bool(d, "connected") ?? false));
        }
        return list;
    }

    /// <summary>The app id in front (<c>{appId:"com.webos.app.hdmi1"}</c>), only if it looks like an app id.</summary>
    public static string? ParseForegroundApp(JsonElement payload)
    {
        var id = String(payload, "appId");
        return id is { Length: > 0 and <= 128 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') ? id : null;
    }

    /// <summary>
    /// The input that belongs to the app in front: webOS shows an external input as the app "com.webos.app.hdmi1" for "HDMI_1" (and
    /// "…app.hdmi2" for "HDMI_2"). Null when the app in front is no input (a streaming app, live TV) or unknown.
    /// </summary>
    public static string? InputOfApp(string? appId, IEnumerable<TvInput> inputs)
    {
        if (appId is null || !appId.StartsWith("com.webos.app.", StringComparison.Ordinal)) return null;
        var tail = appId["com.webos.app.".Length..];
        foreach (var input in inputs)
        {
            var simple = input.Id.Replace("_", "", StringComparison.Ordinal);
            if (string.Equals(simple, tail, StringComparison.OrdinalIgnoreCase)) return input.Id;
        }
        return null;
    }

    /// <summary>Model name from getSystemInfo (<c>{modelName:"OLED55C3"}</c>), cleaned.</summary>
    public static string? ParseModel(JsonElement payload) => CleanLabel(String(payload, "modelName"));

    /// <summary>
    /// The TV's MAC from the connection manager's getinfo (<c>{wiredInfo:{macAddress}, wifiInfo:{macAddress}}</c> on the TVs the
    /// open-source clients were tested with; not documented by LG). Wired first: Wake-on-LAN usually only works there.
    /// </summary>
    public static string? ParseMac(JsonElement payload)
    {
        foreach (var key in new[] { "wiredInfo", "wifiInfo" })
        {
            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty(key, out var info) && info.ValueKind == JsonValueKind.Object
                && WakeOnLan.ParseMac(String(info, "macAddress")) is { } mac)
            {
                return WakeOnLan.Format(mac);
            }
        }
        return null;
    }

    /// <summary>A label from the TV: control and format characters removed, whitespace collapsed, at most 48 characters; null if empty.</summary>
    public static string? CleanLabel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var sb = new StringBuilder(Math.Min(text.Length, 64));
        var space = false;
        foreach (var c in text)
        {
            var category = char.GetUnicodeCategory(c);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned)
            {
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                space = sb.Length > 0;
                continue;
            }
            if (space) sb.Append(' ');
            space = false;
            sb.Append(c);
            if (sb.Length >= 48) break;
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>SHA-256 over the certificate's SubjectPublicKeyInfo, base64: survives a re-issued certificate with the same key.</summary>
    public static string SpkiPin(X509Certificate2 certificate) =>
        Convert.ToBase64String(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));

    /// <summary>Constant-time comparison of two pins (both base64 of 32 bytes).</summary>
    public static bool SamePin(string? expected, string? actual)
    {
        if (expected is null || actual is null) return false;
        var a = Encoding.ASCII.GetBytes(expected);
        var b = Encoding.ASCII.GetBytes(actual);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string? String(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        } : null;
}
