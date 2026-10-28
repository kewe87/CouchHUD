using System.Text.RegularExpressions;

namespace CouchHud.Core.Library;

/// <summary>
/// The only links CouchHUD opens to install a game: fixed URL schemes of the official clients, built from ids that are checked
/// strictly (Steam: digits only; Epic: letters, digits, '.', '_' and '-', starting with a letter or digit). The ids come from files in user-writable folders,
/// so nothing else may reach the URL (no other scheme, no path, no query of their choosing), and nothing goes through a shell.
/// </summary>
public static partial class InstallLinks
{
    /// <summary>
    /// <c>steam://install/&lt;id&gt;</c>, "Installs an application", Valve Developer Community,
    /// https://developer.valvesoftware.com/wiki/Steam_browser_protocol . Steam shows its own install dialog (folder, size).
    /// </summary>
    public static string? Steam(string appId) => IsSteamAppId(appId) ? $"steam://install/{appId}" : null;

    /// <summary>
    /// <c>com.epicgames.launcher://apps/&lt;namespace&gt;%3A&lt;catalogItemId&gt;%3A&lt;appName&gt;?action=install</c>. Epic does not
    /// document the scheme; the form is the one the launcher uses for "launch" (already used by <see cref="EpicScanner"/>) with
    /// action=install, as Playnite (github.com/JosefNemec/PlayniteExtensions, source/Libraries/EpicLibrary/EpicLauncher.cs,
    /// GameInstallUrlMask) and Rai Pal (github.com/Raicuparta/rai-pal, epic_provider.rs) build it. Playnite notes that the launcher
    /// may answer with its older install dialog; the progress is then only visible in the launcher.
    /// </summary>
    public static string? Epic(string? catalogNamespace, string? catalogItemId, string? appName) =>
        IsEpicId(catalogNamespace) && IsEpicId(catalogItemId) && IsEpicId(appName)
            ? $"com.epicgames.launcher://apps/{catalogNamespace}%3A{catalogItemId}%3A{appName}?action=install"
            : null;

    /// <summary>
    /// Steam's own download page (<c>steam://open/downloads</c>, "Opens the downloads page", Valve Developer Community, Steam browser
    /// protocol): there pausing and resuming really work. Steam has no documented way to pause from outside; nothing else is opened.
    /// </summary>
    public const string SteamDownloads = "steam://open/downloads";

    /// <summary>
    /// The fixed link to a client's download page, or null when there is none: Steam only. Epic documents no link to its downloads
    /// (only the undocumented apps/…?action= form for one game), so none is guessed.
    /// </summary>
    public static string? ClientDownloads(string? source) => source == "steam" ? SteamDownloads : null;

    public static bool IsSteamAppId(string? id) =>
        id is { Length: > 0 and <= 10 } && id.All(char.IsAsciiDigit) && uint.TryParse(id, out var n) && n > 0;

    public static bool IsEpicId(string? id) => id is { Length: > 0 and <= 128 } && EpicIdPattern().IsMatch(id);

    /// <summary>The last check before a link is opened: exactly one of the two forms, nothing more.</summary>
    public static bool IsInstallUrl(string? url) => url is not null && (SteamUrlPattern().IsMatch(url) || EpicUrlPattern().IsMatch(url))
        && (!url.StartsWith("steam:", StringComparison.Ordinal) || IsSteamAppId(url["steam://install/".Length..]));

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._\-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex EpicIdPattern();

    [GeneratedRegex(@"\Asteam://install/[0-9]{1,10}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SteamUrlPattern();

    [GeneratedRegex(@"\Acom\.epicgames\.launcher://apps/[A-Za-z0-9][A-Za-z0-9._\-]{0,127}%3A[A-Za-z0-9][A-Za-z0-9._\-]{0,127}%3A[A-Za-z0-9][A-Za-z0-9._\-]{0,127}\?action=install\z", RegexOptions.CultureInvariant)]
    private static partial Regex EpicUrlPattern();
}
