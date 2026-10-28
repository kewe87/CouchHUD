using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CouchHud.Core.Saves;

/// <summary>
/// The few Windows file calls the save backups need beyond .NET: the reparse tag of a path (a junction or symbolic link redirects,
/// a OneDrive placeholder does not), the real path behind an open handle, and the number of hard links of a file.
/// Off Windows every call answers "unknown".
/// </summary>
internal static class NativeFiles
{
    /// <summary>
    /// The reparse tag of <paramref name="path"/> without opening it (no cloud download): null if it is no reparse point or does not
    /// exist; <paramref name="known"/> is false if Windows could not say.
    /// </summary>
    public static uint? ReparseTag(string path, out bool known)
    {
        known = false;
        if (!OperatingSystem.IsWindows()) return null;
        var handle = FindFirstFileExW(LongPath(path), FindExInfoBasic, out var data, 0, IntPtr.Zero, 0);
        if (handle == InvalidHandle)
        {
            var error = Marshal.GetLastWin32Error();
            known = error is ErrorFileNotFound or ErrorPathNotFound;
            return null;
        }
        FindClose(handle);
        known = true;
        return (data.dwFileAttributes & (uint)FileAttributes.ReparsePoint) != 0 ? data.dwReserved0 : null;
    }

    /// <summary>The final path of an open file ("C:\…", "\\server\share\…"), null if Windows could not say.</summary>
    public static string? FinalPath(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows() || handle.IsInvalid) return null;
        var buffer = new char[1024];
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var n = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (n == 0) return null;
            if (n < buffer.Length) return Plain(new string(buffer, 0, (int)n));
            buffer = new char[n + 1];
        }
        return null;
    }

    /// <summary>The final path of a folder, all links on the way resolved (it is opened without access rights), null if unknown.</summary>
    public static string? FinalPathOfDirectory(string dir)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var handle = CreateFileW(LongPath(dir), 0, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        return handle.IsInvalid ? null : FinalPath(handle);
    }

    /// <summary>
    /// <see cref="LongName"/> for a path that may not exist yet (a backup folder to be created, a restore target): the deepest
    /// existing part is turned into its long name, the rest is kept. The path itself if Windows can't say.
    /// </summary>
    public static string LongNameOfExisting(string path) => TryLongNameOfExisting(path) ?? path;

    /// <summary><see cref="LongNameOfExisting"/>, but null if an existing part with a short name could not be expanded.</summary>
    public static string? TryLongNameOfExisting(string path)
    {
        if (!OperatingSystem.IsWindows() || !path.Contains('~')) return path;
        var existing = path.TrimEnd('\\', '/');
        var rest = "";
        while (!string.IsNullOrEmpty(existing) && !Directory.Exists(existing) && !File.Exists(existing))
        {
            rest = rest.Length == 0 ? Path.GetFileName(existing) : Path.Combine(Path.GetFileName(existing), rest);
            existing = Path.GetDirectoryName(existing);
        }
        if (string.IsNullOrEmpty(existing)) return path;
        if (TryLongName(existing) is not { } head) return null;
        return rest.Length == 0 ? head : Path.Combine(head, rest);
    }

    /// <summary>The long form of an existing path (short 8.3 parts like PROGRA~2 expanded); the path itself if Windows can't say.</summary>
    public static string LongName(string path) => TryLongName(path) ?? path;

    /// <summary><see cref="LongName"/>, but null if Windows could not expand a path with a short name.</summary>
    public static string? TryLongName(string path)
    {
        if (!OperatingSystem.IsWindows() || !path.Contains('~')) return path;
        var buffer = new char[1024];
        var n = GetLongPathNameW(LongPath(path), buffer, (uint)buffer.Length);
        if (n > buffer.Length)
        {
            buffer = new char[n];
            n = GetLongPathNameW(LongPath(path), buffer, (uint)buffer.Length);
        }
        if (n == 0 || n > buffer.Length) return null;
        var result = new string(buffer, 0, (int)n);
        return result.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal) ? Plain(result) : result;
    }

    /// <summary>Hard links of an open file, null if unknown.</summary>
    public static uint? LinkCount(SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows() || handle.IsInvalid) return null;
        return GetFileInformationByHandle(handle, out var info) ? info.nNumberOfLinks : null;
    }

    /// <summary>"\\?\C:\x" → "C:\x", "\\?\UNC\srv\share" → "\\srv\share", without a trailing backslash.</summary>
    internal static string Plain(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        return path.TrimEnd('\\');
    }

    private static string LongPath(string path)
    {
        if (path.Length < 240 || path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        return path.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + path[2..] : @"\\?\" + path;
    }

    private const int FindExInfoBasic = 1;
    private const int ErrorFileNotFound = 2, ErrorPathNotFound = 3;
    private const uint FileShareAll = 7, OpenExisting = 3, FileFlagBackupSemantics = 0x02000000;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindData
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime, ftLastAccessTime, ftLastWriteTime;
        public uint nFileSizeHigh, nFileSizeLow, dwReserved0, dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime, ftLastAccessTime, ftLastWriteTime;
        public uint dwVolumeSerialNumber, nFileSizeHigh, nFileSizeLow, nNumberOfLinks, nFileIndexHigh, nFileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFileExW(string lpFileName, int fInfoLevelId, out Win32FindData lpFindFileData, int fSearchOp, IntPtr lpSearchFilter, int dwAdditionalFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FindClose(IntPtr hFindFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle hFile, [Out] char[] lpszFilePath, uint cchFilePath, uint dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string lpszShortPath, [Out] char[] lpszLongPath, uint cchBuffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out ByHandleFileInformation lpFileInformation);
}
