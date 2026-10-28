namespace CouchHud.Core.Updates;

/// <summary>
/// Sizes of Windows updates as the Windows Update Agent reports them (COM, so DECIMAL, double or anything else: foreign data).
/// <c>MaxDownloadSize</c> is the full package with every variant; what Windows really loads is usually an express or delta part of it
/// (cumulative updates and Defender definitions: a few MB of "2 GB"). So a size is only shown when the smallest and the largest download
/// lie close together; otherwise none (an "up to 2 GB" on a TV still reads as "2 GB" for a tiny update). During a download the
/// agent's own byte counters (<c>IDownloadProgress</c>) are the real values.
/// </summary>
public static class UpdateSize
{
    /// <summary>More than this is no real update (1 TiB): a broken or hostile value, not a size.</summary>
    public const long MaxPlausibleBytes = 1L << 40;

    /// <summary>Min and max count as "the same size" up to this ratio (max ≤ 1.5 × min).</summary>
    public const decimal CloseRatio = 1.5m;

    /// <summary>A byte count from a COM value: null for null, text, NaN, infinity, ≤ 0, more than <see cref="MaxPlausibleBytes"/>.</summary>
    public static long? Bytes(object? value)
    {
        if (value is null or string or bool) return null;
        decimal d;
        try
        {
            d = Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidCastException or OverflowException or FormatException)
        {
            return null;
        }
        if (d <= 0 || d > MaxPlausibleBytes) return null;
        return (long)decimal.Ceiling(d);
    }

    /// <summary>
    /// The size worth showing before a download: the larger of both when they lie within <see cref="CloseRatio"/>, else null (unknown:
    /// either value missing, or the range too wide to say anything). An update that is already downloaded loads nothing: null.
    /// </summary>
    public static long? Shown(long? min, long? max, bool downloaded)
    {
        if (downloaded) return null;
        if (min is not > 0 || max is not > 0 || min > MaxPlausibleBytes || max > MaxPlausibleBytes) return null;
        var lo = Math.Min(min.Value, max.Value);
        var hi = Math.Max(min.Value, max.Value);
        return hi <= lo * CloseRatio ? hi : null;
    }

    /// <inheritdoc cref="Shown(long?, long?, bool)"/>
    public static long? Shown(object? min, object? max, bool downloaded) => Shown(Bytes(min), Bytes(max), downloaded);

    /// <summary>
    /// The agent's byte counters of a running download (TotalBytesDownloaded, TotalBytesToDownload): (0, 0) when the total is not known
    /// (yet), the done part kept within 0..total.
    /// </summary>
    public static (long Done, long Total) Transfer(object? done, object? total)
    {
        if (Bytes(total) is not { } t) return (0, 0);
        return (Math.Min(Bytes(done) ?? 0, t), t);
    }
}
