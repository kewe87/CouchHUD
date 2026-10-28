using System.Text.RegularExpressions;

namespace CouchHud.Core.Library;

/// <summary>What the anti-cheat check found: nothing (freezing allowed), an anti-cheat, or no answer (the check could not run or finish).</summary>
public enum AntiCheatVerdict
{
    Clean,
    Detected,
    Unknown
}

/// <param name="Marker">The name that gave the anti-cheat away (a file, folder or process), for the log; null when clean or unknown.</param>
public sealed record AntiCheatResult(AntiCheatVerdict Verdict, string? Marker = null)
{
    public static readonly AntiCheatResult Clean = new(AntiCheatVerdict.Clean);

    /// <summary>Freezing the game is allowed only after a check that ran to the end and found nothing.</summary>
    public bool MayFreeze => Verdict == AntiCheatVerdict.Clean;
}

/// <summary>How far the scan of an install folder goes; beyond any of these it stops and answers "unknown".</summary>
public sealed record AntiCheatScanLimits(int MaxDepth, int MaxEntries, TimeSpan MaxTime)
{
    /// <summary>Four folder levels below the install folder, 50,000 names, two seconds.</summary>
    public static readonly AntiCheatScanLimits Default = new(4, 50_000, TimeSpan.FromSeconds(2));
}

/// <summary>
/// Spots a game's anti-cheat from local evidence only, so CouchHUD never freezes such a game (suspending its threads can look like
/// tampering and cost the account). Two sources, no network: the names of files and folders in the game's own install folder, scanned
/// breadth first with bounds (<see cref="AntiCheatScanLimits"/>), never into a junction or link (nothing outside the folder is read), and
/// the names and images of the game's running processes. Conservative: a hit, a folder that is a link itself, a missing folder, a denied
/// read or a scan cut off by its bounds all mean "do not freeze". Names known to belong to anti-cheat systems only; a game can still have
/// one this list does not know.
/// </summary>
public static partial class AntiCheat
{
    /// <summary>Parts of names (lower case) that belong to an anti-cheat: EasyAntiCheat, BattlEye, Vanguard, nProtect GameGuard, XIGNCODE, PunkBuster, Ricochet, mhyprot, ACE, EQU8, FACEIT and generic "anticheat".</summary>
    private static readonly string[] Parts =
    [
        "anticheat", "anti-cheat", "anti_cheat", "easyanticheat", "battleye", "beclient", "beservice", "bedaisy", "vanguard", "gameguard",
        "nprotect", "npgamemon", "npggnt", "xigncode", "xcorona", "punkbuster", "pnkbstr", "pbsvc", "ricochet", "randgrid", "mhyprot",
        "anticheatexpert", "sguard", "equ8", "faceit", "start_protected_game", "blackcipher", "hackshield", "nexonguard", "wellbia"
    ];

    // Short names that would be too common as parts: the whole name (without extension) only.
    [GeneratedRegex(@"^(vgk|vgc|vgtray|x3|ace-(base|game|ats|core|safe|guard)\w*|gamemon(64)?|ucldr_\w+|tp3helper|tpsvc)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WholeNames();

    /// <summary>The marker a file, folder or process name gives away, else null.</summary>
    public static string? Match(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var lower = name.Trim().ToLowerInvariant();
        foreach (var part in Parts)
            if (lower.Contains(part, StringComparison.Ordinal)) return part;
        var stem = Path.GetFileNameWithoutExtension(lower);
        return WholeNames().IsMatch(stem) ? stem : null;
    }

    /// <summary>
    /// The install folder's names, breadth first: each name at most <see cref="AntiCheatScanLimits.MaxDepth"/> levels below the folder is
    /// checked (a link's own name too), but no link or junction is entered, and an install folder that is a link itself is not scanned.
    /// </summary>
    /// <param name="ticks">Milliseconds of a monotonic clock (tests set it); <see cref="Environment.TickCount64"/> otherwise.</param>
    /// <param name="isNetworkDrive">Whether a drive letter is a mapped network drive (tests set it); the drive's type from Windows otherwise.</param>
    public static AntiCheatResult ScanInstallDir(string? installDir, AntiCheatScanLimits? limits = null, Func<long>? ticks = null, CancellationToken cancel = default,
        Func<char, bool>? isNetworkDrive = null)
    {
        limits ??= AntiCheatScanLimits.Default;
        ticks ??= () => Environment.TickCount64;
        if (string.IsNullOrWhiteSpace(installDir)) return new(AntiCheatVerdict.Unknown, "no install folder");
        // A share is never touched (a slow or hostile server could hold the scan, and its content can change behind it): no answer.
        if (IsSharePath(installDir)) return new(AntiCheatVerdict.Unknown, "install folder on a network share");
        DirectoryInfo root;
        try
        {
            var full = Path.GetFullPath(installDir);
            if (IsSharePath(full)) return new(AntiCheatVerdict.Unknown, "install folder on a network share");
            if (DriveLetter(full) is { } letter && (isNetworkDrive ?? IsNetworkDrive)(letter)) return new(AntiCheatVerdict.Unknown, "install folder on a network drive");
            root = new DirectoryInfo(full);
            if (!root.Exists) return new(AntiCheatVerdict.Unknown, "install folder missing");
            // A drive root is no game's folder: it would be the whole disk.
            if (root.Parent is null) return new(AntiCheatVerdict.Unknown, "install folder is a drive");
            // The folder itself a junction or link: what it points to is not the game's own folder for sure.
            if (root.Attributes.HasFlag(FileAttributes.ReparsePoint)) return new(AntiCheatVerdict.Unknown, "install folder is a link");
            if (Match(root.Name) is { } own) return new(AntiCheatVerdict.Detected, own);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return new(AntiCheatVerdict.Unknown, "install folder unreadable");
        }

        var started = ticks();
        var seen = 0;
        var queue = new Queue<(DirectoryInfo Dir, int Depth)>();
        queue.Enqueue((root, 1));
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false
        };
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            try
            {
                foreach (var entry in dir.EnumerateFileSystemInfos("*", options))
                {
                    if (cancel.IsCancellationRequested) return new(AntiCheatVerdict.Unknown, "cancelled");
                    if (++seen > limits.MaxEntries) return new(AntiCheatVerdict.Unknown, "too many files");
                    if (ticks() - started > limits.MaxTime.TotalMilliseconds) return new(AntiCheatVerdict.Unknown, "took too long");
                    if (Match(entry.Name) is { } marker) return new(AntiCheatVerdict.Detected, marker);
                    // Never into a junction or link (it may lead out of the install folder), only as deep as the bound.
                    if (entry is DirectoryInfo sub && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && depth < limits.MaxDepth)
                        queue.Enqueue((sub, depth + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // A folder that cannot be read may hold the anti-cheat: no answer.
                return new(AntiCheatVerdict.Unknown, "folder unreadable");
            }
        }
        return AntiCheatResult.Clean;
    }

    /// <summary>
    /// A path on a share: \\server\share, \\?\UNC\… and every other \\.\ or \\?\ form except \\?\C:\… (a local drive in the long form).
    /// Read from the text only, nothing is touched.
    /// </summary>
    internal static bool IsSharePath(string path)
    {
        var p = path.Trim().Replace('/', '\\');
        if (!p.StartsWith(@"\\", StringComparison.Ordinal)) return false;
        var localLong = p.Length >= 7 && p.StartsWith(@"\\?\", StringComparison.Ordinal) && char.IsAsciiLetter(p[4]) && p[5] == ':' && p[6] == '\\';
        return !localLong;
    }

    private static char? DriveLetter(string full)
    {
        if (full.Length >= 2 && char.IsAsciiLetter(full[0]) && full[1] == ':') return char.ToUpperInvariant(full[0]);
        if (full.Length >= 6 && full.StartsWith(@"\\?\", StringComparison.Ordinal) && char.IsAsciiLetter(full[4]) && full[5] == ':') return char.ToUpperInvariant(full[4]);
        return null;
    }

    // Only the drive's type from Windows (GetDriveType), no access to the drive's content; an answer that fails counts as a network drive.
    private static bool IsNetworkDrive(char letter)
    {
        try
        {
            return new DriveInfo(letter + @":\").DriveType == DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>The game's running processes (names and images): one with an anti-cheat name is a hit.</summary>
    public static AntiCheatResult CheckProcesses(IEnumerable<(string? Name, string? Path)> processes)
    {
        foreach (var (name, path) in processes)
        {
            if (Match(name) is { } marker) return new(AntiCheatVerdict.Detected, marker);
            if (Match(SafeFileName(path)) is { } image) return new(AntiCheatVerdict.Detected, image);
        }
        return AntiCheatResult.Clean;
    }

    /// <summary>Both checks together: anything but two clean answers means no freeze (a hit before an unknown).</summary>
    public static AntiCheatResult Combine(AntiCheatResult install, AntiCheatResult processes) =>
        install.Verdict == AntiCheatVerdict.Detected ? install
        : processes.Verdict == AntiCheatVerdict.Detected ? processes
        : install.Verdict == AntiCheatVerdict.Unknown ? install
        : processes;

    private static string? SafeFileName(string? path)
    {
        try
        {
            return string.IsNullOrEmpty(path) ? null : Path.GetFileName(path);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>
/// Whether a running game may be frozen (the menu's pause, the parents' time-up pause): the install folder is scanned once per entry and
/// folder in the background (<see cref="Prepare"/>, at the session's start), the processes at the moment of the freeze. A scan that has not
/// finished counts as no answer. Tells the user once per game session why nothing was frozen.
/// </summary>
public sealed class FreezeGuard(Func<string?, AntiCheatResult>? scan = null)
{
    private readonly Func<string?, AntiCheatResult> _scan = scan ?? (dir => AntiCheat.ScanInstallDir(dir));
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<Task<AntiCheatResult>>> _scans = new();
    private readonly HashSet<string> _told = [];

    private static string Key(CouchHud.Sdk.LibraryItem item) => item.Id + "|" + item.InstallDir;

    /// <summary>Starts the folder scan off the calling thread (once per entry and folder) and returns it.</summary>
    public Task<AntiCheatResult> Prepare(CouchHud.Sdk.LibraryItem item) =>
        _scans.GetOrAdd(Key(item), _ => new Lazy<Task<AntiCheatResult>>(() => Task.Run(() => _scan(item.InstallDir)))).Value;

    /// <summary>The marker of an answer that is only missing because the folder scan has not finished yet.</summary>
    public const string StillScanning = "scan still running";

    /// <summary>The game ended: the next session scans again (an update may have added an anti-cheat).</summary>
    public void Forget(CouchHud.Sdk.LibraryItem item) => _scans.TryRemove(Key(item), out _);

    /// <summary>The answer now: the folder's scan (unfinished or failed: no answer) together with the game's processes.</summary>
    public AntiCheatResult Check(CouchHud.Sdk.LibraryItem item, IEnumerable<(string? Name, string? Path)> processes)
    {
        var task = Prepare(item);
        var install = task.IsCompletedSuccessfully ? task.Result
            : new AntiCheatResult(AntiCheatVerdict.Unknown, task.IsFaulted || task.IsCanceled ? "scan failed" : StillScanning);
        return AntiCheat.Combine(install, AntiCheat.CheckProcesses(processes));
    }

    /// <summary>True only the first time for this game session: the notice comes once, not on every trip to the menu.</summary>
    public bool FirstNotice(string sessionKey)
    {
        lock (_told) return _told.Add(sessionKey);
    }
}
