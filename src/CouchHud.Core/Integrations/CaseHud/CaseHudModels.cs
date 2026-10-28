using System.Text.Json;
using System.Text.Json.Serialization;

namespace CouchHud.Core.Integrations.CaseHud;

/// <summary>
/// Where CaseHUD lives and how its release is recognized. CaseHUD is a separate program (github.com/kewe87/CaseHUD);
/// CouchHUD only talks to its local API v1 and never contains its code.
/// </summary>
public static class CaseHudInfo
{
    /// <summary>Inno Setup AppId of CaseHUD (the uninstall key is "{AppId}_is1"); this is the one place it is written down.</summary>
    public const string AppId = "{6F3A9C2E-5B71-4C0D-9E8A-CA5E0D1A2B30}";

    public const string UninstallKey = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId + "_is1";
    public const string ExeName = "CaseHUD.exe";
    public const string Repository = "kewe87/CaseHUD";
    public const string ApiUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
    public const string SetupPrefix = "CaseHUD-Setup-";
    public const string ChecksumName = "SHA256SUMS.txt";
    public const int ApiVersion = 1;

    /// <summary>The first version that is offered: the line starts again at 1.0.0 and rises from there. Older tags are no CaseHUD release for CouchHUD.</summary>
    public static readonly Plugins.SemVer MinimumVersion = new(1, 0, 0, "");

    /// <summary>Signature context of CaseHUD releases (the signed bytes are this line followed by the checksum file).</summary>
    public const string ReleaseContext = "casehud-release-v1\n";

    /// <summary>
    /// ECDSA P-256 public key (PEM) that signs CaseHUD releases. SHA-256 over its SPKI DER bytes:
    /// <see cref="ReleaseKeyFingerprint"/> (a test pins it, so a typo or a swapped key shows at once).
    /// Empty = no signature required (the SHA-256 is always required); set, the signature is mandatory.
    /// The signature file is base64 in one line, the signature itself ASN.1 DER (not IEEE P1363).
    /// </summary>
    public const string ReleasePublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEI0dFQKFrFdKb4r6IRQxD+9Cor8th
        hUgMDlJnn1Y6rUE9bkFsOty4u+JVtFpxfiRIc+tmLG8UzbMNdf5fJU1XCw==
        -----END PUBLIC KEY-----
        """;

    public const string ReleaseKeyFingerprint = "cfb6863f23c892a53c93786dd1ee78478618aed680c5603fbc3955c1dde4abaf";
}

/// <summary>Content of CaseHUD's api.json. The token never appears in <see cref="ToString"/>.</summary>
public sealed class CaseHudApiInfo
{
    public int ApiVersion { get; init; }
    public string Version { get; init; } = "";
    public int Port { get; init; }
    public string Token { get; init; } = "";
    public int Pid { get; init; }
    public string Exe { get; init; } = "";

    public override string ToString() => $"CaseHUD {Version} on port {Port} (pid {Pid})";
}

/// <summary>Installed CaseHUD as the uninstall entry describes it. Whether it can be controlled is not read off the version, see <see cref="CaseHudCapabilityStore"/>.</summary>
public sealed record CaseHudInstall(string Exe, string Directory, string Version);

/// <summary>
/// CaseHUD's setup puts "CaseHUD.exe --minimized" into the Run key; CouchHUD runs those entries itself as the shell. For that
/// one entry the start becomes <c>--background</c> (plus <c>--no-tray</c> without a taskbar). The entry is rewritten, not skipped:
/// CouchHUD does not start CaseHUD by itself, so skipping would leave it off. A build that was probed and found old stays as
/// it is (it ends on every flag it does not know); one that was not probed yet keeps its entry too: rewriting it blindly would stop an
/// old build from starting at boot. The manager records a running CaseHUD with a valid api.json as capable, so the next boot rewrites it.
/// </summary>
public static class CaseHudAutostart
{
    /// <summary>The adapter for the shell's autostart: looks up the installed CaseHUD once and adapts the entry that starts it.</summary>
    public static Func<string, string> ForShell(Platform.IRegistry registry, bool noTray, Func<string, string>? canonicalPath = null, Func<string, bool>? exists = null, CaseHudCapabilityStore? capabilities = null)
    {
        var install = new CaseHudDiscovery("", _ => null, registry, exists, canonicalPath).FindInstall();
        var capability = install is null ? CaseHudCapability.Unknown : capabilities?.Get(install) ?? CaseHudCapability.Unknown;
        return command => Adapt(command, install, noTray, canonicalPath, capability);
    }

    /// <param name="canonicalPath">Resolves short names and links, so the entry is recognised however its path is written.</param>
    public static string Adapt(string commandLine, CaseHudInstall? install, bool noTray, Func<string, string>? canonicalPath = null, CaseHudCapability capability = CaseHudCapability.Unknown)
    {
        if (install is null || capability != CaseHudCapability.Capable) return commandLine;
        try
        {
            var (file, arguments) = Platform.CommandLine.Split(commandLine);
            string Normal(string path)
            {
                var full = Path.GetFullPath(path);
                return canonicalPath?.Invoke(full) ?? full;
            }
            if (!string.Equals(Normal(file), Normal(install.Exe), StringComparison.OrdinalIgnoreCase)) return commandLine;
            var args = Platform.CommandLine.SplitArguments(arguments).ToList();
            var at = args.FindIndex(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
            if (at < 0) return commandLine;
            args[at] = "--background";
            if (noTray && !args.Contains("--no-tray", StringComparer.OrdinalIgnoreCase)) args.Insert(at + 1, "--no-tray");
            return "\"" + file + "\" " + string.Join(' ', args.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a));
        }
        catch (ArgumentException)
        {
            return commandLine;
        }
    }
}

/// <summary>A display of CaseHUD. <see cref="Id"/> stays raw (it addresses the display); <see cref="Name"/> is cleaned for the page.</summary>
public sealed record CaseHudPanel(
    string Id, [property: JsonConverter(typeof(CaseHudNameConverter))] string Name, string Model, int W, int H, bool Connected, string Mode, string Design,
    int Brightness, int Rotation, int FanNo, string Error = "", string Theme = "dark");

public sealed record CaseHudSensors(bool Ok, string Error = "");
public sealed record CaseHudGaming(bool Active, string Source = "", string Game = "");
public sealed record CaseHudRotation(bool Enabled, string Scene = "");
public sealed record CaseHudAwtrixDevice(string Name, string Host, string Model, bool Ok);
public sealed record CaseHudAwtrix(bool Enabled, IReadOnlyList<CaseHudAwtrixDevice>? Devices = null);

/// <summary>
/// The automation of CaseHUD (scenes by rule, e.g. while a game runs). From the couch it is only switched on or off; the rules are
/// edited in CaseHUD's window. <see cref="ActiveRule"/> is the index of the rule in charge, -1 for none. Its texts (when, scene) are not taken.
/// </summary>
public sealed record CaseHudAutomation(bool Enabled, int ActiveRule = -1);

public sealed record CaseHudStatus(
    string Version, int ApiVersion, CaseHudSensors Sensors, bool Power, CaseHudGaming Gaming,
    CaseHudRotation Rotation, CaseHudAwtrix Awtrix, IReadOnlyList<CaseHudPanel> Panels, string UpdateAvailable = "",
    // CaseHUD 1.0: missing in older builds, so null means "unknown" (not "refused"); see CaseHudLighting.NeedsDisclaimer / Supported.
    bool? DisclaimerAccepted = null, CaseHudLightingSummary? Lighting = null,
    // Missing in older builds: no automation row then.
    CaseHudAutomation? Automation = null,
    // Programs that may hold the same devices (e.g. "OpenRGB.exe"). Untrusted text: cleaned and capped while it is read.
    [property: JsonConverter(typeof(CaseHudConflictsConverter))] IReadOnlyList<string>? Conflicts = null,
    // The saved scene on screen (rotation, automation or set by hand), "" for none; the one /scenes marks active. Missing (null) in
    // older builds: the page falls back to the scene list, then to rotation.scene.
    [property: JsonConverter(typeof(CaseHudTextConverter))] string? Scene = null);

/// <summary>A text field of CaseHUD that is shown: cleaned (<see cref="CaseHudLighting.SafeMessage"/>) and capped at <see cref="MaxLength"/>; anything but a string is null.</summary>
public sealed class CaseHudTextConverter : JsonConverter<string?>
{
    public const int MaxLength = 128;

    public override bool HandleNull => true;

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return CaseHudLighting.SafeMessage(reader.GetString(), MaxLength);
        reader.Skip();
        return null;
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}

/// <summary>
/// A name of CaseHUD for the page (display, scene, set): a string cleaned like a message (<see cref="CaseHudLighting.SafeMessage"/>) and capped
/// at <see cref="MaxLength"/> text elements; a value that is no string (null too) reads as empty, a missing one stays unset like any missing field. Only names take it, never the ids that address things.
/// </summary>
public sealed class CaseHudNameConverter : JsonConverter<string>
{
    public const int MaxLength = 64;

    public override bool HandleNull => true;

    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return CaseHudLighting.SafeMessage(reader.GetString(), MaxLength);
        reader.Skip();
        return "";
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value ?? "");
}

/// <summary>A style of CaseHUD. <see cref="Set"/> and <see cref="Theme"/> stay raw (they go back in /sets/apply); <see cref="Name"/> is cleaned for the page.</summary>
public sealed record CaseHudSet(string Set, string Theme, [property: JsonConverter(typeof(CaseHudNameConverter))] string Name);

/// <summary>A scene of CaseHUD. It is activated by <see cref="Id"/>, which stays raw; <see cref="Name"/> is cleaned for the page.</summary>
public sealed record CaseHudScene(string Id, [property: JsonConverter(typeof(CaseHudNameConverter))] string Name, bool Active);

/// <summary>
/// <c>conflicts</c> of the status, read defensively: at most <see cref="MaxCount"/> names of at most <see cref="MaxLength"/> text elements,
/// control and format characters removed (<see cref="CaseHudLighting.SafeMessage"/>), empty and non-string entries dropped. Anything that is
/// no list is no conflict. The names are shown as text only.
/// </summary>
public sealed class CaseHudConflictsConverter : JsonConverter<IReadOnlyList<string>?>
{
    public const int MaxCount = 8;
    public const int MaxLength = 64;

    public override bool HandleNull => true;

    public override IReadOnlyList<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            reader.Skip();
            return null;
        }
        var names = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                reader.Skip();
                continue;
            }
            if (names.Count >= MaxCount) continue;
            var name = CaseHudLighting.SafeMessage(reader.GetString(), MaxLength);
            if (name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        }
        return names;
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<string>? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }
        writer.WriteStartArray();
        foreach (var name in value) writer.WriteStringValue(name);
        writer.WriteEndArray();
    }
}

/// <summary>Fields of a panel patch from the couch: brightness, theme and mode; null = leave unchanged. Design and mounting are set in CaseHUD's window.</summary>
public sealed record CaseHudPanelPatch(string? Mode = null, int? Brightness = null, string? Theme = null)
{
    public static readonly IReadOnlySet<string> Modes = new HashSet<string>(StringComparer.Ordinal) { "hud", "media", "off" };
    public static readonly IReadOnlySet<string> Themes = new HashSet<string>(StringComparer.Ordinal) { "dark", "light" };

    /// <summary>
    /// What the page sent, checked: only mode (hud, media, off), brightness (a whole number, clamped when sent) and theme (dark, light).
    /// Any other field (design, rotation …) or value throws <see cref="ArgumentException"/>, and so does an empty patch.
    /// </summary>
    public static CaseHudPanelPatch FromJson(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new ArgumentException("panel: patch must be an object");
        foreach (var field in e.EnumerateObject())
            if (field.Name is not ("mode" or "brightness" or "theme")) throw new ArgumentException($"panel: {field.Name} cannot be set here");
        string? Pick(string n, IReadOnlySet<string> allowed)
        {
            if (!e.TryGetProperty(n, out var v)) return null;
            return v.ValueKind == JsonValueKind.String && allowed.Contains(v.GetString()!) ? v.GetString() : throw new ArgumentException($"panel: unknown {n}");
        }
        int? brightness = null;
        if (e.TryGetProperty("brightness", out var b))
            brightness = b.ValueKind == JsonValueKind.Number && b.TryGetInt32(out var i) ? i : throw new ArgumentException("panel: brightness must be a whole number");
        var patch = new CaseHudPanelPatch(Pick("mode", Modes), brightness, Pick("theme", Themes));
        return patch == new CaseHudPanelPatch() ? throw new ArgumentException("panel: nothing to change") : patch;
    }
}

/// <summary>API error: HTTP status plus the code and text of <c>{"error":{"code","message"}}</c>. Code "unreachable" = no answer at all.</summary>
public sealed class CaseHudException(string code, string message, int status = 0) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

internal static class CaseHudJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        // CaseHUD rejects unknown fields and wants only what is meant: no "game":null.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
