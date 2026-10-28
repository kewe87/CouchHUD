using System.Text.RegularExpressions;
using CouchHud.Core.Platform;

namespace CouchHud.Core.Integrations.Vpn;

/// <summary>
/// NordVPN through its Windows app's command line. Source: NordVPN Help Center, "Connect to NordVPN (Windows) with Command Prompt"
/// (https://support.nordvpn.com/hc/en-us/articles/19919384880145-Connect-to-NordVPN-Windows-with-Command-Prompt, checked 2026-10):
/// run from the install folder (default <c>C:\Program Files\NordVPN\</c>):
/// <list type="bullet">
/// <item><c>nordvpn -c</c> / <c>--connect</c>: quick connect</item>
/// <item><c>nordvpn -c -g "United States"</c> / <c>--group-name</c>: best server of a group, a country or a specialty group</item>
/// <item><c>nordvpn -c -n "United States #3710"</c> / <c>--server-name</c>: one server by name</item>
/// <item><c>nordvpn -d</c> / <c>--disconnect</c>: disconnect</item>
/// </list>
/// Not documented there (so not used): a status command, cities, exit codes, whether the app must be signed in. CouchHUD reads the state
/// off the tunnel adapter (NordLynx, or NordVPN's OpenVPN/TAP adapter) and never anything that needs the account. Signing in happens in the
/// app only; CouchHUD never sees credentials.
/// </summary>
public sealed partial class NordVpnProvider : IVpnProvider
{
    public const string UninstallRoot = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    public const string UninstallRoot32 = @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    public const string AppPathsKey = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\NordVPN.exe";

    public string Id => "nordvpn";
    public string Name => "NordVPN";
    public string ExeName => "NordVPN.exe";
    public string ProcessName => "NordVPN";

    /// <summary>
    /// The countries offered, with NordVPN's English name as the app's group name. A fixed list on purpose: the name goes onto a command
    /// line, so only these exact strings ever do. Countries NordVPN adds later need an entry here.
    /// </summary>
    public IReadOnlyList<VpnCountry> Countries { get; } =
    [
        new("al", "Albania"), new("ar", "Argentina"), new("au", "Australia"), new("at", "Austria"), new("be", "Belgium"),
        new("ba", "Bosnia and Herzegovina"), new("br", "Brazil"), new("bg", "Bulgaria"), new("ca", "Canada"), new("cl", "Chile"),
        new("co", "Colombia"), new("hr", "Croatia"), new("cy", "Cyprus"), new("cz", "Czech Republic"), new("dk", "Denmark"),
        new("ee", "Estonia"), new("fi", "Finland"), new("fr", "France"), new("ge", "Georgia"), new("de", "Germany"),
        new("gr", "Greece"), new("hk", "Hong Kong"), new("hu", "Hungary"), new("is", "Iceland"), new("in", "India"),
        new("id", "Indonesia"), new("ie", "Ireland"), new("il", "Israel"), new("it", "Italy"), new("jp", "Japan"),
        new("lv", "Latvia"), new("lt", "Lithuania"), new("lu", "Luxembourg"), new("my", "Malaysia"), new("mx", "Mexico"),
        new("md", "Moldova"), new("nl", "Netherlands"), new("nz", "New Zealand"), new("mk", "North Macedonia"), new("no", "Norway"),
        new("pl", "Poland"), new("pt", "Portugal"), new("ro", "Romania"), new("rs", "Serbia"), new("sg", "Singapore"),
        new("sk", "Slovakia"), new("si", "Slovenia"), new("za", "South Africa"), new("kr", "South Korea"), new("es", "Spain"),
        new("se", "Sweden"), new("ch", "Switzerland"), new("tw", "Taiwan"), new("th", "Thailand"), new("tr", "Turkey"),
        new("ua", "Ukraine"), new("ae", "United Arab Emirates"), new("gb", "United Kingdom"), new("us", "United States"), new("vn", "Vietnam")
    ];

    /// <summary>"Germany #812": a country name of the list, a space, '#', a number without leading zero (at most 5 digits). Nothing else.</summary>
    [GeneratedRegex(@"^(?<country>[A-Za-z][A-Za-z .]{1,39}) #(?<number>[1-9][0-9]{0,4})\z", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex ServerPattern();

    public IEnumerable<string> ExeCandidates(IRegistry registry, IEnumerable<string> programFiles)
    {
        // App Paths (default value, then Path + exe name), only HKLM.
        if (FromIconOrPath(registry.ReadString(AppPathsKey, "")) is { } appPath) yield return appPath;
        if (Folder(registry.ReadString(AppPathsKey, "Path")) is { } appDir) yield return appDir;
        // The uninstall entry whose display name is exactly "NordVPN" (not the TAP driver, NordPass, NordLayer …).
        foreach (var root in new[] { UninstallRoot, UninstallRoot32 })
        {
            foreach (var sub in registry.SubKeys(root))
            {
                var key = root + "\\" + sub;
                var values = registry.ReadValues(key);
                if (!values.TryGetValue("DisplayName", out var name) || !string.Equals(name.Trim(), Name, StringComparison.OrdinalIgnoreCase)) continue;
                if (values.TryGetValue("InstallLocation", out var location) && Folder(location) is { } fromLocation) yield return fromLocation;
                if (values.TryGetValue("DisplayIcon", out var icon) && FromIconOrPath(icon) is { } fromIcon) yield return fromIcon;
            }
        }
        // The default folder the help article names.
        foreach (var root in programFiles)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string? path = null;
            try { path = Path.Combine(root, "NordVPN", ExeName); }
            catch (ArgumentException) { }
            if (path is not null) yield return path;
        }
    }

    /// <summary>"C:\…\NordVPN.exe" or "\"C:\…\NordVPN.exe\",0" when it names our exe; null otherwise.</summary>
    private string? FromIconOrPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        var comma = text.LastIndexOf(',');
        if (comma > 0 && int.TryParse(text[(comma + 1)..], out _)) text = text[..comma];
        text = text.Trim().Trim('"').Trim();
        return text.EndsWith("\\" + ExeName, StringComparison.OrdinalIgnoreCase) && TrustedCli.IsDrivePath(text) ? text : null;
    }

    private string? Folder(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        var text = dir.Trim().Trim('"').Trim();
        if (!TrustedCli.IsDrivePath(text)) return null;
        try { return Path.Combine(text, ExeName); }
        catch (ArgumentException) { return null; }
    }

    public IReadOnlyList<string> ConnectArguments(VpnTarget target)
    {
        switch (target.Kind)
        {
            case VpnTargetKind.Quick:
                return ["-c"];
            case VpnTargetKind.Country:
                return ["-c", "-g", CountryById(target.Country).Name];
            case VpnTargetKind.Server:
                return ["-c", "-n", ValidServer(target.Server)];
            default:
                throw new ArgumentException("unknown VPN target");
        }
    }

    public IReadOnlyList<string> DisconnectArguments() => ["-d"];

    public VpnTarget Normalize(VpnTarget target)
    {
        ConnectArguments(target);
        return target.Kind switch
        {
            VpnTargetKind.Quick => VpnTarget.Quick,
            VpnTargetKind.Country => new VpnTarget(VpnTargetKind.Country, Country: target.Country),
            _ => new VpnTarget(VpnTargetKind.Server, Server: ValidServer(target.Server))
        };
    }

    public string? CountryOf(VpnTarget target) => target.Kind switch
    {
        VpnTargetKind.Country => target.Country,
        VpnTargetKind.Server when target.Server is { } server => ServerCountry(server),
        _ => null
    };

    /// <summary>The country of the list with this id; anything else is refused.</summary>
    public VpnCountry CountryById(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length != 2) throw new ArgumentException("unknown VPN country");
        return Countries.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal))
               ?? throw new ArgumentException("unknown VPN country");
    }

    /// <summary>The server name, written the way the list writes the country ("germany #12" becomes "Germany #12"); refused otherwise.</summary>
    public string ValidServer(string? server)
    {
        var match = server is { Length: <= 48 } ? ServerPattern().Match(server) : Match.Empty;
        if (!match.Success) throw new ArgumentException("invalid VPN server name");
        var country = Countries.FirstOrDefault(c => string.Equals(c.Name, match.Groups["country"].Value, StringComparison.OrdinalIgnoreCase))
                      ?? throw new ArgumentException("invalid VPN server name");
        return $"{country.Name} #{match.Groups["number"].Value}";
    }

    /// <summary>The country id a server name belongs to ("Germany #812" → "de"), or null.</summary>
    public string? ServerCountry(string server)
    {
        var match = ServerPattern().Match(server);
        return match.Success ? Countries.FirstOrDefault(c => string.Equals(c.Name, match.Groups["country"].Value, StringComparison.OrdinalIgnoreCase))?.Id : null;
    }

    /// <summary>
    /// NordLynx (WireGuard) shows up as an adapter named or described "NordLynx"; OpenVPN uses NordVPN's own adapter ("TAP-NordVPN Windows
    /// Adapter", "NordVPN …"). Only adapters that are up count (the TAP adapter exists, down, while disconnected), and only when the
    /// internet is routed through them: Meshnet alone also brings the NordLynx adapter up, but only for its own network, and is no VPN
    /// connection. When the route could not be looked up (null) an adapter that is up counts.
    /// </summary>
    public string? TunnelKind(IEnumerable<VpnAdapter> adapters)
    {
        string? kind = null;
        foreach (var a in adapters)
        {
            if (!a.Up || a.CarriesDefaultRoute == false) continue;
            var text = a.Name + " " + a.Description;
            if (text.Contains("NordLynx", StringComparison.OrdinalIgnoreCase)) return "nordlynx";
            if (text.Contains("NordVPN", StringComparison.OrdinalIgnoreCase)) kind = "openvpn";
        }
        return kind;
    }

    public bool KillCliOnTimeout(bool appWasRunning) => appWasRunning;
}
