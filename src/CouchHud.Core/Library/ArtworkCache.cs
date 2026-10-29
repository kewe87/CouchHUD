using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CouchHud.Core.Library;

/// <summary>
/// Images on disk (%LOCALAPPDATA%\CouchHud\artwork): downloaded once, served from here afterwards.
/// A file's name is the SHA-256 of its source address, so nothing the network says ever becomes a path.
/// Only png, jpeg, webp and gif are accepted (checked by content, not by the server's word), files are
/// written to a temp name and moved into place, half-written or damaged files are refused, and the
/// folder is trimmed least-recently-used when it grows past its limit.
/// </summary>
public sealed partial class ArtworkCache
{
    /// <summary>Virtual host the UI loads cached images from.</summary>
    public const string Host = "art.couchhud";

    /// <summary>No single image may be larger than this.</summary>
    public const long MaxImageBytes = 12 * 1024 * 1024;

    /// <summary>Set once at startup so the URL rewriting of the UI host can find the cache.</summary>
    public static ArtworkCache? Shared { get; set; }

    private static readonly TimeSpan FailureTtl = TimeSpan.FromDays(7);

    private readonly string _root;
    private readonly long _maxBytes;
    private readonly Func<DateTime> _utcNow;
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _index = new(StringComparer.Ordinal);
    private Dictionary<string, DateTimeOffset> _failures = new();
    private DateTime _failuresSaved = DateTime.MinValue;
    private bool _failuresDirty;

    /// <summary>The list of failures is written at most this often (clock of the cache); Prune and Clear write it at once.</summary>
    private static readonly TimeSpan FailureSaveGap = TimeSpan.FromSeconds(5);

    private sealed class Entry(string ext, long size, DateTime used)
    {
        public string Ext = ext;
        public long Size = size;
        public DateTime Used = used;
    }

    /// <summary>Outcome of <see cref="TryStore"/>: a rejected image is never going to work, an error may be gone next time.</summary>
    public enum StoreResult { Stored, Rejected, Error }

    public ArtworkCache(string root, long maxBytes = 768L * 1024 * 1024, Func<DateTime>? utcNow = null)
    {
        _root = Path.GetFullPath(root).TrimEnd('\\', '/');
        _maxBytes = maxBytes;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        Directory.CreateDirectory(_root);
        Load();
        ForgetLegacySteamFailures();
    }

    public string Root => _root;

    /// <summary>Sum of all cached images.</summary>
    public long TotalBytes
    {
        get
        {
            lock (_lock) return _index.Values.Sum(e => e.Size);
        }
    }

    public int Count
    {
        get
        {
            lock (_lock) return _index.Count;
        }
    }

    public static string HashOf(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>The address the UI uses for a source address (whether or not it is cached yet).</summary>
    public static string UrlFor(string key) => $"https://{Host}/{HashOf(key)}";

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex HashPattern();

    [GeneratedRegex(@"^[0-9a-f]{64}\.(jpg|png|webp|gif)$")]
    private static partial Regex FilePattern();

    private string FileFor(string hash, string ext) => Path.Combine(_root, hash + ext);

    private void Load()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(".tmp", StringComparison.Ordinal))
                {
                    TryDelete(file); // an interrupted download
                    continue;
                }
                if (!FilePattern().IsMatch(name)) continue;
                var info = new FileInfo(file);
                _index[name[..64]] = new Entry(Path.GetExtension(name), info.Length, info.LastWriteTimeUtc);
            }
            _failures = JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(Path.Combine(_root, "failed.json"))) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Missing or damaged list of failures: nothing is lost but a few repeated tries.
        }
    }

    /// <summary>
    /// Once: the old fixed Steam addresses (no hash folder) failed for newer games because Steam only serves those
    /// under hashed paths. Those failures are forgotten so the games are looked up again; nothing else is touched.
    /// </summary>
    private void ForgetLegacySteamFailures()
    {
        var marker = Path.Combine(_root, "steam-hashed.migrated");
        if (File.Exists(marker)) return;
        foreach (var key in _failures.Keys.Where(SteamAssets.IsLegacyUrl).ToList()) _failures.Remove(key);
        try
        {
            File.WriteAllText(Path.Combine(_root, "failed.json") + ".tmp", JsonSerializer.Serialize(_failures));
            File.Move(Path.Combine(_root, "failed.json") + ".tmp", Path.Combine(_root, "failed.json"), overwrite: true);
            File.WriteAllText(marker, "1");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[artwork] steam failure migration: {ex.Message}");
        }
    }

    /// <summary>Whether the image for this source address is on disk. Counts as a use for the LRU order.</summary>
    public bool Has(string key)
    {
        lock (_lock)
        {
            if (!_index.TryGetValue(HashOf(key), out var e)) return false;
            e.Used = _utcNow();
            return true;
        }
    }

    /// <summary>Full path of the cached image for a source address, or null.</summary>
    public string? PathOf(string key)
    {
        var hash = HashOf(key);
        lock (_lock) return _index.TryGetValue(hash, out var e) ? FileFor(hash, e.Ext) : null;
    }

    /// <summary>
    /// Finds the file behind a request path of the virtual host ("/&lt;hash&gt;"), or null. Anything that is not
    /// exactly 64 hex digits, or leads outside the cache folder, is refused before the disk is touched.
    /// </summary>
    public string? ResolveRequest(string requestPath)
    {
        var hash = requestPath.TrimStart('/');
        if (!HashPattern().IsMatch(hash)) return null;
        lock (_lock)
        {
            if (!_index.TryGetValue(hash, out var e)) return null;
            var full = Path.GetFullPath(FileFor(hash, e.Ext));
            if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
            e.Used = _utcNow();
            return full;
        }
    }

    /// <summary>The cached bytes for a request, checked again on the way out (null when missing or damaged).</summary>
    public (byte[] Data, string ContentType)? Read(string requestPath)
    {
        var path = ResolveRequest(requestPath);
        if (path is null) return null;
        var hash = requestPath.TrimStart('/');
        Entry? seen;
        lock (_lock) _index.TryGetValue(hash, out seen);
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxImageBytes || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
            var data = File.ReadAllBytes(path);
            if (ImageSniffer.Detect(data) is not { } kind || kind.Ext != Path.GetExtension(path).TrimStart('.'))
            {
                Forget(hash, path, seen);
                return null;
            }
            return (data, kind.ContentType);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Drops a damaged file, but only if the index still holds the entry that was checked (a fresh download may have replaced it meanwhile).</summary>
    private void Forget(string hash, string path, Entry? seen)
    {
        lock (_lock)
        {
            if (seen is null || !_index.TryGetValue(hash, out var current) || !ReferenceEquals(current, seen)) return;
            _index.Remove(hash);
        }
        TryDelete(path);
    }

    /// <summary>True when the image was stored (see <see cref="TryStore"/> for the reason otherwise).</summary>
    public bool Store(string key, byte[] data, string? declaredContentType = null) => TryStore(key, data) == StoreResult.Stored;

    /// <summary>
    /// Stores an image under its source address. What it is, is decided by its bytes alone (the server's
    /// content type is only a claim): a complete png/jpeg/webp/gif of limited size and dimensions.
    /// Rejected: never going to work. Error: the disk said no, worth another try later.
    /// </summary>
    public StoreResult TryStore(string key, byte[] data)
    {
        if (data.Length == 0 || data.Length > MaxImageBytes) return StoreResult.Rejected;
        if (ImageSniffer.Detect(data) is not { } kind) return StoreResult.Rejected;

        var hash = HashOf(key);
        var target = FileFor(hash, "." + kind.Ext);
        var temp = Path.Combine(_root, $"{hash}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(data);
                stream.Flush(true);
            }
            lock (_lock)
            {
                // The same image may exist under another extension if the source changed its format.
                if (_index.TryGetValue(hash, out var old) && old.Ext != "." + kind.Ext) TryDelete(FileFor(hash, old.Ext));
                File.Move(temp, target, overwrite: true);
                _index[hash] = new Entry("." + kind.Ext, data.Length, _utcNow());
                if (_failures.Remove(key)) _failuresDirty = true;
            }
            return StoreResult.Stored;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[artwork] could not store image: {ex.Message}");
            TryDelete(temp);
            return StoreResult.Error;
        }
    }

    private DateTimeOffset Now => new(DateTime.SpecifyKind(_utcNow(), DateTimeKind.Utc));

    /// <summary>A permanent miss (404, not an image, too large): not asked again for a week.</summary>
    public void MarkFailed(string key)
    {
        lock (_lock)
        {
            _failures[key] = Now;
            _failuresDirty = true;
            // Many misses in a row (a whole library) are written once in a while, not once each.
            if (_utcNow() - _failuresSaved >= FailureSaveGap) SaveFailures();
        }
    }

    /// <summary>Forgets a recorded miss, so the address is tried again (a re-check or a manual reload).</summary>
    public void ClearFailed(string key)
    {
        lock (_lock)
        {
            if (_failures.Remove(key)) _failuresDirty = true;
        }
    }

    public bool RecentlyFailed(string key)
    {
        lock (_lock) return _failures.TryGetValue(key, out var at) && Now - at < FailureTtl;
    }

    private void SaveFailures()
    {
        _failuresSaved = _utcNow();
        _failuresDirty = false;
        try
        {
            // Old entries expire here, so the list stays small.
            foreach (var old in _failures.Where(f => Now - f.Value >= FailureTtl).Select(f => f.Key).ToList()) _failures.Remove(old);
            var file = Path.Combine(_root, "failed.json");
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(_failures));
            File.Move(file + ".tmp", file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[artwork] could not save failure list: {ex.Message}");
        }
    }

    /// <summary>Writes the list of failures if it has unsaved changes.</summary>
    public void Flush()
    {
        lock (_lock)
        {
            if (_failuresDirty) SaveFailures();
        }
    }

    /// <summary>
    /// Trims the folder to its limit, oldest use first. Images of entries that are installed
    /// (<paramref name="inUse"/>: their source addresses) are never removed. Returns the bytes freed.
    /// </summary>
    public long Prune(IEnumerable<string> inUse)
    {
        Flush();
        var keep = inUse.Select(HashOf).ToHashSet(StringComparer.Ordinal);
        long freed = 0;
        lock (_lock)
        {
            var total = _index.Values.Sum(e => e.Size);
            if (total <= _maxBytes) return 0;
            // Down to 90 %, so the next download doesn't trigger the next cleanup.
            var goal = (long)(_maxBytes * 0.9);
            foreach (var (hash, e) in _index.Where(p => !keep.Contains(p.Key)).OrderBy(p => p.Value.Used).ToList())
            {
                if (total <= goal) break;
                TryDelete(FileFor(hash, e.Ext));
                _index.Remove(hash);
                total -= e.Size;
                freed += e.Size;
            }
        }
        return freed;
    }

    /// <summary>Removes every cached image and the list of failures (the settings button).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            foreach (var (hash, e) in _index) TryDelete(FileFor(hash, e.Ext));
            _index.Clear();
            _failures.Clear();
            _failuresDirty = false;
            TryDelete(Path.Combine(_root, "failed.json"));
            TryDelete(Path.Combine(_root, "steam-hashed.migrated"));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Recognizes the image formats we accept by their bytes: complete, and not absurdly large in pixels.</summary>
public static class ImageSniffer
{
    public sealed record Kind(string Ext, string ContentType);

    /// <summary>More pixels than this per side is refused (decoding such an image would eat the memory of the UI).</summary>
    public const int MaxSide = 8192;

    private static readonly Kind Png = new("png", "image/png");
    private static readonly Kind Jpeg = new("jpg", "image/jpeg");
    private static readonly Kind Webp = new("webp", "image/webp");
    private static readonly Kind Gif = new("gif", "image/gif");

    /// <summary>The format, or null for anything else, for truncated files and for images with unreadable or oversized dimensions.</summary>
    public static Kind? Detect(ReadOnlySpan<byte> d)
    {
        if (d.Length < 16) return null;
        Kind? kind = null;
        if (d[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            // Ends with the IEND chunk (length 0, "IEND", crc).
            if (d[^12..].SequenceEqual(new byte[] { 0, 0, 0, 0, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82 })) kind = Png;
        }
        else if (d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF)
        {
            var end = d.Length;
            while (end > 2 && d[end - 1] == 0) end--; // some encoders pad
            if (d[end - 2] == 0xFF && d[end - 1] == 0xD9) kind = Jpeg;
        }
        else if (d[..4].SequenceEqual("GIF8"u8))
        {
            if (d[^1] == 0x3B) kind = Gif;
        }
        else if (d[..4].SequenceEqual("RIFF"u8) && d[8..12].SequenceEqual("WEBP"u8))
        {
            var declared = BitConverter.ToUInt32(d[4..8]) + 8L;
            if (declared <= d.Length && declared >= d.Length - 1) kind = Webp;
        }
        if (kind is null) return null;
        return Size(d, kind) is { } size && size.W is > 0 and <= MaxSide && size.H is > 0 and <= MaxSide ? kind : null;
    }

    /// <summary>Width and height from the header, null when it can't be read.</summary>
    internal static (int W, int H)? Size(ReadOnlySpan<byte> d, Kind kind)
    {
        switch (kind.Ext)
        {
            case "png":
                // IHDR is the first chunk: length, "IHDR", width, height.
                if (d.Length < 24 || !d[12..16].SequenceEqual("IHDR"u8)) return null;
                return (Be32(d[16..]), Be32(d[20..]));
            case "gif":
                return d.Length < 10 ? null : (d[6] | d[7] << 8, d[8] | d[9] << 8);
            case "jpg":
            {
                // Walk the segments to the first start-of-frame marker.
                var i = 2;
                while (i + 9 < d.Length)
                {
                    if (d[i] != 0xFF) return null;
                    var marker = d[i + 1];
                    if (marker == 0xFF) { i++; continue; }
                    if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7)) { i += 2; continue; }
                    var length = d[i + 2] << 8 | d[i + 3];
                    if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
                        return (d[i + 7] << 8 | d[i + 8], d[i + 5] << 8 | d[i + 6]);
                    if (length < 2) return null;
                    i += 2 + length;
                }
                return null;
            }
            case "webp":
            {
                if (d.Length < 30) return null;
                var chunk = d[12..16];
                if (chunk.SequenceEqual("VP8X"u8)) return (1 + (d[24] | d[25] << 8 | d[26] << 16), 1 + (d[27] | d[28] << 8 | d[29] << 16));
                if (chunk.SequenceEqual("VP8 "u8)) return ((d[26] | d[27] << 8) & 0x3FFF, (d[28] | d[29] << 8) & 0x3FFF);
                if (chunk.SequenceEqual("VP8L"u8) && d[20] == 0x2F)
                {
                    var bits = d[21] | d[22] << 8 | d[23] << 16 | (uint)d[24] << 24;
                    return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
                }
                return null;
            }
        }
        return null;
    }

    private static int Be32(ReadOnlySpan<byte> d) => d[0] << 24 | d[1] << 16 | d[2] << 8 | d[3];
}
