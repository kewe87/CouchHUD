using System.Globalization;

namespace CouchHud.Core.Localization;

/// <summary>
/// The one size format of CouchHUD: decimal units (1 MB = 1,000,000 bytes, 1 GB = 1,000 MB), the way Windows Update, Steam and the stores
/// count. The page uses the same rule (ui/core/size.js), so a size reads the same in a host text and on the page.
/// </summary>
public static class ByteSize
{
    public const long Kilo = 1000;
    public const long Mega = Kilo * Kilo;

    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>"1,5 GB" (German) or "1.5 GB" (English): one decimal below 10, none above; "0 B" for nothing or less.</summary>
    public static string Format(long bytes, string? language = null)
    {
        var culture = CultureInfo.GetCultureInfo((language ?? Strings.Language) == "de" ? "de-DE" : "en-GB");
        if (bytes <= 0) return "0 B";
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < Units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        static int Digits(double v, int u) => u == 0 || v >= 10 ? 0 : 1;
        // 999.96 KB would round to "1000 KB": that is 1 MB.
        if (unit < Units.Length - 1 && Math.Round(value, Digits(value, unit)) >= 1000)
        {
            value /= 1000;
            unit++;
        }
        var text = Math.Round(value, Digits(value, unit), MidpointRounding.AwayFromZero).ToString(Digits(value, unit) == 0 ? "0" : "0.#", culture);
        return $"{text} {Units[unit]}";
    }

    /// <summary>Whole megabytes, rounded up ("1001 MB" for a byte over 1000 MB): for a size compared with a limit, so it never reads as equal.</summary>
    public static string CeilingMb(long bytes) => $"{Math.Ceiling(Math.Max(0, bytes) / (double)Mega).ToString("0", CultureInfo.InvariantCulture)} MB";
}
