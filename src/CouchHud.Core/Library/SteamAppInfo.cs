using System.Diagnostics;
using System.Text;

namespace CouchHud.Core.Library;

/// <summary>Name and type ("game", "tool", "dlc", "application" …) of one app as Steam's own cache knows it.</summary>
public sealed record SteamAppName(string Name, string Type);

/// <summary>
/// Reads names and types from Steam's binary app cache (<c>appcache\appinfo.vdf</c>), only for the app ids asked for.
/// <para>
/// Format, as documented by SteamDatabase (https://github.com/SteamDatabase/SteamAppInfo, README and
/// SteamAppInfoParser/AppInfo.cs): uint32 magic (0x07 0x56 0x44 + version byte, versions 39, 40 and 41 = 0x27, 0x28, 0x29),
/// uint32 universe; from version 41 an int64 offset of a string table (uint32 count, then zero-terminated UTF-8 strings) that the
/// binary KeyValues use for their keys (int32 index instead of a zero-terminated key). Then per app: uint32 app id (0 ends the list),
/// uint32 size of the rest of the entry, uint32 info state, uint32 last updated, uint64 PICS token, 20 bytes SHA-1, uint32 change
/// number, from version 40 another 20 bytes SHA-1 of the binary data, then binary KeyValues (node types as in ValveKeyValue's
/// KV1BinaryNodeType: 0 object, 1 string, 2 int32, 3 float32, 4 pointer, 5 wide string, 6 color, 7 uint64, 8 end, 9 blob, 10 int64,
/// 11 alternate end).
/// </para>
/// <para>
/// The file lies in a user-writable folder and can be large (hundreds of MB with a big library): it is read as a stream, entries that
/// are not asked for are skipped by their size, and every count, size, depth and the time are bounded. Anything odd ends the read with
/// what was found so far; nothing read here is ever executed or used as a path.
/// </para>
/// </summary>
public static class SteamAppInfo
{
    /// <summary>Real files are well below this even for libraries with thousands of games (a few hundred MB at most).</summary>
    public const long MaxFileBytes = 1L * 1024 * 1024 * 1024;

    /// <summary>All strings of the key table together (version 41); real tables are a few hundred KB.</summary>
    internal const long MaxStringTableBytes = 48L * 1024 * 1024;

    internal const int MaxEntryBytes = 16 * 1024 * 1024;
    internal const int MaxStrings = 1_000_000;
    internal const int MaxStringBytes = 64 * 1024;
    internal const int MaxDepth = 32;
    internal const int MaxNodes = 200_000;

    /// <summary>The default time budget for one pass; what was found until then is used.</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(15);

    private static readonly object MemoLock = new();
    private static (string File, DateTime Written, long Size, string Wanted, Dictionary<uint, SteamAppName> Result)? _memo;

    /// <summary>Reads <paramref name="file"/>; an unreadable or unknown file gives an empty result, never an exception.</summary>
    public static Dictionary<uint, SteamAppName> Read(string file, IReadOnlySet<uint> wanted, TimeSpan? budget = null, CancellationToken ct = default)
    {
        var result = new Dictionary<uint, SteamAppName>();
        if (wanted.Count == 0) return result;
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists) return result;
            if (info.Length > MaxFileBytes)
            {
                Console.Error.WriteLine($"[steam] appinfo.vdf is {info.Length / (1024 * 1024)} MB, ignored");
                return result;
            }
            var key = string.Join(',', wanted.Order());
            lock (MemoLock)
            {
                if (_memo is { } m && m.File == file && m.Written == info.LastWriteTimeUtc && m.Size == info.Length && m.Wanted == key)
                    return new Dictionary<uint, SteamAppName>(m.Result);
            }
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            result = Read(stream, wanted, budget ?? DefaultBudget, ct);
            lock (MemoLock) _memo = (file, info.LastWriteTimeUtc, info.Length, key, new Dictionary<uint, SteamAppName>(result));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[steam] appinfo.vdf: {ex.GetType().Name}");
        }
        return result;
    }

    /// <summary>Reads from a seekable stream. Stops at the first sign of a broken or foreign file and returns what it has.</summary>
    public static Dictionary<uint, SteamAppName> Read(Stream input, IReadOnlySet<uint> wanted, TimeSpan budget, CancellationToken ct = default)
    {
        var result = new Dictionary<uint, SteamAppName>();
        var clock = Stopwatch.StartNew();
        try
        {
            using var reader = new BinaryReader(input, Encoding.UTF8, leaveOpen: true);
            var length = input.Length;
            var magic = reader.ReadUInt32();
            var version = magic & 0xFF;
            if (magic >> 8 != 0x075644 || version is < 39 or > 41) throw new FormatException($"unknown appinfo version {magic:X8}");
            reader.ReadUInt32(); // universe

            string[]? strings = null;
            if (version >= 41)
            {
                var tableAt = reader.ReadInt64();
                if (tableAt <= input.Position || tableAt >= length) throw new FormatException("string table outside the file");
                var back = input.Position;
                input.Position = tableAt;
                var count = reader.ReadUInt32();
                if (count > MaxStrings) throw new FormatException("string table too large");
                strings = new string[count];
                long tableBytes = 0;
                for (var i = 0; i < count; i++)
                {
                    // The table alone could be made huge: the same time, cancel and size limits as the entries.
                    if ((i & 1023) == 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (clock.Elapsed > budget) throw new FormatException("time budget used up in the string table");
                    }
                    strings[i] = ReadCString(reader, MaxStringBytes);
                    tableBytes += Encoding.UTF8.GetByteCount(strings[i]) + 1;
                    if (tableBytes > MaxStringTableBytes) throw new FormatException("string table too large");
                }
                input.Position = back;
            }

            var headerBytes = 4 + 4 + 8 + 20 + 4 + (version >= 40 ? 20 : 0);
            var found = 0;
            while (found < wanted.Count)
            {
                ct.ThrowIfCancellationRequested();
                if (clock.Elapsed > budget)
                {
                    Console.Error.WriteLine($"[steam] appinfo.vdf: time budget used up, {found} of {wanted.Count} names read");
                    break;
                }
                if (length - input.Position < 4) break;
                var appId = reader.ReadUInt32();
                if (appId == 0) break;
                var size = reader.ReadUInt32();
                if (size < headerBytes || size > MaxEntryBytes || size > length - input.Position) throw new FormatException("entry size out of range");
                if (!wanted.Contains(appId))
                {
                    input.Seek(size, SeekOrigin.Current);
                    continue;
                }
                var data = reader.ReadBytes((int)size);
                if (data.Length != size) break;
                found++;
                try
                {
                    var kv = new BinaryKv(data, headerBytes, strings).ReadRoot();
                    var common = kv.GetObject("appinfo")?.GetObject("common");
                    var name = common?.GetString("name");
                    var type = common?.GetString("type");
                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(type)) result[appId] = new SteamAppName(Clean(name), type.Trim());
                }
                catch (FormatException)
                {
                    // One broken entry: skip it, its size is known.
                }
            }
        }
        catch (Exception ex) when (ex is FormatException or EndOfStreamException or IOException or ArgumentException or OverflowException)
        {
            Console.Error.WriteLine($"[steam] appinfo.vdf: {ex.GetType().Name}: {ex.Message}");
        }
        return result;
    }

    /// <summary>A name is shown on the TV: no control characters, no endless text.</summary>
    internal static string Clean(string name)
    {
        var sb = new StringBuilder(Math.Min(name.Length, 200));
        foreach (var c in name)
        {
            if (sb.Length >= 200) break;
            sb.Append(char.IsControl(c) ? ' ' : c);
        }
        return sb.ToString().Trim();
    }

    private static string ReadCString(BinaryReader reader, int max)
    {
        var bytes = new List<byte>(32);
        while (true)
        {
            var b = reader.ReadByte();
            if (b == 0) break;
            if (bytes.Count >= max) throw new FormatException("string too long");
            bytes.Add(b);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    /// <summary>Binary KeyValues of one entry, into the same tree the text parser builds (numbers become their text).</summary>
    private sealed class BinaryKv(byte[] data, int start, string[]? strings)
    {
        private int _i = start;
        private int _nodes;

        public VdfObject ReadRoot() => ReadObject(0);

        private VdfObject ReadObject(int depth)
        {
            if (depth > MaxDepth) throw new FormatException("nested too deep");
            var obj = new VdfObject();
            while (true)
            {
                var type = Byte();
                if (type is 8 or 11) return obj;
                if (++_nodes > MaxNodes) throw new FormatException("too many nodes");
                var key = Key();
                object value = type switch
                {
                    0 => ReadObject(depth + 1),
                    1 => CString(),
                    2 or 4 or 6 => BitConverter.ToInt32(Take(4)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    3 => BitConverter.ToSingle(Take(4)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    5 => WideString(),
                    7 => BitConverter.ToUInt64(Take(8)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    9 => Blob(),
                    10 => BitConverter.ToInt64(Take(8)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    _ => throw new FormatException($"unknown node type {type}")
                };
                obj[key] = value;
            }
        }

        private byte Byte()
        {
            if (_i >= data.Length) throw new FormatException("entry ends early");
            return data[_i++];
        }

        private ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || n > data.Length - _i) throw new FormatException("entry ends early");
            var span = data.AsSpan(_i, n);
            _i += n;
            return span;
        }

        private string Key()
        {
            if (strings is null) return CString();
            var index = BitConverter.ToInt32(Take(4));
            if (index < 0 || index >= strings.Length) throw new FormatException("key index outside the string table");
            return strings[index];
        }

        private string CString()
        {
            var end = Array.IndexOf(data, (byte)0, _i);
            if (end < 0 || end - _i > MaxStringBytes) throw new FormatException("bad string");
            var s = Encoding.UTF8.GetString(data, _i, end - _i);
            _i = end + 1;
            return s;
        }

        private string WideString()
        {
            var sb = new StringBuilder();
            while (true)
            {
                var c = (char)BitConverter.ToUInt16(Take(2));
                if (c == '\0') return sb.ToString();
                if (sb.Length >= MaxStringBytes) throw new FormatException("bad string");
                sb.Append(c);
            }
        }

        private string Blob()
        {
            var n = BitConverter.ToUInt32(Take(4));
            if (n > (uint)(data.Length - _i)) throw new FormatException("blob outside the entry");
            Take((int)n);
            return "";
        }
    }
}
