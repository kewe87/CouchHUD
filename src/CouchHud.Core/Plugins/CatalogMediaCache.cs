using System.Security.Cryptography;

namespace CouchHud.Core.Plugins;

/// <summary>
/// Icons and screenshots of catalog entries. They are never hot-linked: the shell asks this cache, which downloads over https (or the
/// developer's local sources), at most <see cref="PackageMedia.MaxIconBytes"/> / <see cref="PackageMedia.MaxScreenshotBytes"/>, checks the
/// SHA-256 the signed statement (or the signed index) names and that it is a PNG, keeps it as <c>catalog-media/&lt;sha&gt;.png</c> and
/// re-hashes it on every read. Only a verified file comes back, as a data: URI for CouchHUD's own WebView. Media without a hash is not
/// shown at all.
/// </summary>
public sealed class CatalogMediaCache
{
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private readonly string _dir;
    private readonly Func<Uri, long, CancellationToken, Task<byte[]?>> _fetch;
    private readonly Func<bool> _allowLocal;

    public CatalogMediaCache(string dataDir, HttpClient http, Func<bool>? allowLocal = null)
        : this(dataDir, (uri, max, ct) => FetchAsync(http, uri, max, ct, allowLocal ?? (() => false)), allowLocal)
    {
    }

    /// <summary>For tests: the download is handed in.</summary>
    public CatalogMediaCache(string dataDir, Func<Uri, long, CancellationToken, Task<byte[]?>> fetch, Func<bool>? allowLocal = null)
    {
        _dir = Path.Combine(dataDir, "catalog-media");
        _fetch = fetch;
        _allowLocal = allowLocal ?? (() => false);
    }

    /// <summary>The verified image as a data: URI, or null (no hash, a bad address, too large, wrong hash, not a PNG, no network).</summary>
    public async Task<string?> GetAsync(string? url, string? sha256, long maxBytes, CancellationToken ct = default)
    {
        if (sha256 is null || !IsSha(sha256) || maxBytes <= 0) return null;
        var sha = sha256.ToLowerInvariant();
        if (ReadVerified(sha, maxBytes) is { } cached) return DataUri(cached);
        if (url is null || !CatalogParser.IsAllowedUrl(url, _allowLocal()) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        byte[]? bytes;
        try
        {
            bytes = await _fetch(uri, maxBytes, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or InvalidOperationException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[catalog] media: {ex.Message}");
            return null;
        }
        if (!Acceptable(bytes, sha, maxBytes)) return null;
        Store(sha, bytes!);
        return DataUri(bytes!);
    }

    /// <summary>The icon of an entry: the statement's hash when there is one (signed by the publisher), else the signed index's.</summary>
    public Task<string?> IconAsync(CatalogEntry entry, PackageStatement? statement, CancellationToken ct = default) =>
        GetAsync(entry.IconUrl, statement?.Icon?.Sha256 ?? (statement is null ? entry.IconSha256 : null), Math.Min(statement?.Icon?.Bytes ?? PackageMedia.MaxIconBytes, PackageMedia.MaxIconBytes), ct);

    /// <summary>Screenshots: only with a statement, each address paired with the hash at the same place.</summary>
    public async Task<IReadOnlyList<string>> ScreenshotsAsync(CatalogEntry entry, PackageStatement? statement, CancellationToken ct = default)
    {
        var list = new List<string>();
        if (statement is null) return list;
        var count = Math.Min(entry.Screenshots.Count, statement.Screenshots.Count);
        for (var i = 0; i < count; i++)
        {
            var media = statement.Screenshots[i];
            if (await GetAsync(entry.Screenshots[i], media.Sha256, Math.Min(media.Bytes, PackageMedia.MaxScreenshotBytes), ct).ConfigureAwait(false) is { } uri)
                list.Add(uri);
        }
        return list;
    }

    /// <summary>Largest side and largest area a picture may have (a small file can still unpack into a huge bitmap).</summary>
    public const int MaxSide = 4096;
    public const long MaxPixels = 16L * 1024 * 1024;

    internal static bool Acceptable(byte[]? bytes, string sha, long maxBytes) =>
        bytes is not null && bytes.Length <= maxBytes && PngSize(bytes) is { } size && size.Width <= MaxSide && size.Height <= MaxSide && (long)size.Width * size.Height <= MaxPixels
        && Convert.ToHexStringLower(SHA256.HashData(bytes)) == sha;

    /// <summary>Width and height from the IHDR chunk, which must come first right after the PNG signature; null when it is not a PNG.</summary>
    public static (int Width, int Height)? PngSize(ReadOnlySpan<byte> bytes)
    {
        // Signature (8), chunk length 13 (4), "IHDR" (4), width (4), height (4).
        if (bytes.Length < 24 || !bytes[..PngMagic.Length].SequenceEqual(PngMagic)) return null;
        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes[8..12]) != 13 || !bytes[12..16].SequenceEqual("IHDR"u8)) return null;
        var width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes[16..20]);
        var height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes[20..24]);
        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue) return null;
        return ((int)width, (int)height);
    }

    private byte[]? ReadVerified(string sha, long maxBytes)
    {
        var path = Path.Combine(_dir, sha + ".png");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > maxBytes || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
            var bytes = File.ReadAllBytes(path);
            if (Acceptable(bytes, sha, maxBytes)) return bytes;
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file that cannot be read is fetched again.
        }
        return null;
    }

    private void Store(string sha, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(_dir);
            var temp = Path.Combine(_dir, sha + ".tmp");
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, Path.Combine(_dir, sha + ".png"), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[catalog] media cache: {ex.Message}");
        }
    }

    private static string DataUri(byte[] bytes) => "data:image/png;base64," + Convert.ToBase64String(bytes);

    private static bool IsSha(string s) => s.Length == 64 && s.All(Uri.IsHexDigit);

    private static async Task<byte[]?> FetchAsync(HttpClient http, Uri uri, long max, CancellationToken ct, Func<bool> allowLocal)
    {
        if (uri.IsFile)
        {
            var info = new FileInfo(uri.LocalPath);
            return info.Exists && info.Length <= max ? await File.ReadAllBytesAsync(uri.LocalPath, ct).ConfigureAwait(false) : null;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        // Redirects by hand, every hop checked again (public hosts only, unless the developer switch is on).
        using var response = await CatalogHosts.GetAsync(http, uri, allowLocal(), timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > max) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > max) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
