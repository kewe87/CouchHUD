using System.Text;

namespace CouchHud.Core.Platform;

/// <summary>
/// Reads small files from folders other programs can write to. The limit holds for what is really read, not for a length checked
/// beforehand: a file that grows between the check and the read (or reports a wrong length) is still never read beyond it.
/// </summary>
public static class BoundedFile
{
    /// <summary>
    /// The file's text (encoding from its byte order mark, UTF-8 otherwise, like <see cref="File.ReadAllText(string)"/>), or null when
    /// it holds more than <paramref name="maxBytes"/>. At most <paramref name="maxBytes"/> + 1 bytes are read.
    /// </summary>
    public static string? ReadAllText(string path, long maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan);
        // Cheap early answer; the loop below is what really bounds the read.
        if (stream.CanSeek && stream.Length > maxBytes) return null;
        var bytes = ReadAtMost(stream, maxBytes);
        if (bytes is null) return null;
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>All bytes of <paramref name="stream"/>, or null as soon as there are more than <paramref name="maxBytes"/>.</summary>
    public static byte[]? ReadAtMost(Stream stream, long maxBytes)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        long total = 0;
        while (true)
        {
            // Never ask for more than one byte past the limit.
            var want = (int)Math.Min(chunk.Length, maxBytes + 1 - total);
            var n = stream.Read(chunk, 0, want);
            if (n == 0) break;
            total += n;
            if (total > maxBytes) return null;
            buffer.Write(chunk, 0, n);
        }
        return buffer.ToArray();
    }
}
