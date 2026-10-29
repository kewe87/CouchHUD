using CouchHud.Core.Config;
using CouchHud.Sdk;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Library;

/// <summary>Artwork the user picked for one entry: stored as an override, put in front of the automatic candidates.</summary>
public static class ArtworkOverrides
{
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp", ".gif", ".svg"];

    public static void Set(ItemOverride o, string slot, string? url)
    {
        switch (slot)
        {
            case "cover": o.Cover = url; break;
            case "hero": o.Hero = url; break;
            case "logo": o.Logo = url; break;
            case "square": o.Square = url; break;
            default: throw new ArgumentException("slot must be cover, hero, logo or square", nameof(slot));
        }
    }

    /// <summary>
    /// https URLs stay as they are; local image files become file: URLs. Network shares, device paths and
    /// paths without a drive letter are refused before the file system is touched.
    /// </summary>
    public static string Normalize(string pathOrUrl)
    {
        var value = pathOrUrl.Trim();
        if (value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return value;
        if (value.Contains("://") && !value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(Strings.Get("artwork.onlyHttps"));
        var path = value.StartsWith("file:", StringComparison.OrdinalIgnoreCase) ? new Uri(value).LocalPath : value;
        // Two separators in any mix start a share or a device path: \\server, //server, \/server, \\?\ and \\.\
        if (StartsWithTwoSeparators(path)) throw new ArgumentException(Strings.Get("artwork.noNetworkPaths"));
        var full = Path.GetFullPath(path);
        if (StartsWithTwoSeparators(full)) throw new ArgumentException(Strings.Get("artwork.noNetworkPaths"));
        if (OperatingSystem.IsWindows() && !(path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && IsSeparator(path[2])))
            throw new ArgumentException(Strings.Get("artwork.needFullPath"));
        if (!ImageExtensions.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException(Strings.Get("artwork.notImage"));
        if (!File.Exists(full)) throw new FileNotFoundException(Strings.Get("artwork.imageNotFound"), full);
        return CustomEntries.ToUrl(full);
    }

    private static bool IsSeparator(char c) => c is '\\' or '/';

    private static bool StartsWithTwoSeparators(string path) => path.Length >= 2 && IsSeparator(path[0]) && IsSeparator(path[1]);

    /// <summary>Puts each override first in its slot (once), keeps the automatic candidates as fallback.</summary>
    public static Artwork Apply(Artwork art, ItemOverride? o)
    {
        if (o is null) return art;
        return art with
        {
            Cover = Front(art.Cover, o.Cover),
            Hero = Front(art.Hero, o.Hero),
            Logo = Front(art.Logo, o.Logo),
            Square = Front(art.Square, o.Square)
        };
    }

    private static IReadOnlyList<string> Front(IReadOnlyList<string> list, string? url) =>
        string.IsNullOrEmpty(url) ? list : [url, .. list.Where(u => u != url)];
}
