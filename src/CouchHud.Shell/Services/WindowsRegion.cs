using System.Globalization;
using System.Runtime.InteropServices;

namespace CouchHud.Shell.Services;

/// <summary>
/// The Windows region ("Country or region" in Settings › Time &amp; language), as two letters, for the automatic choice of the age rating
/// system (<see cref="CouchHud.Core.Parental.AgeRatings.ForRegion"/>). Null when Windows gives none (then the strictest rating counts).
/// </summary>
public static class WindowsRegion
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetUserDefaultGeoName([Out] char[] geoName, int geoNameCount);

    public static string? Current()
    {
        try
        {
            var buffer = new char[16];
            var length = GetUserDefaultGeoName(buffer, buffer.Length);
            // The count includes the terminating zero; a region of three digits (UN M49, e.g. "419") has no single system.
            if (length > 1) return new string(buffer, 0, length - 1);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // Older Windows: the region of the user's format settings.
        }
        try
        {
            return RegionInfo.CurrentRegion.TwoLetterISORegionName;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
