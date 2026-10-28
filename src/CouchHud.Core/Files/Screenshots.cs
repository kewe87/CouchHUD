using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using CouchHud.Core.Saves;

namespace CouchHud.Core.Files;

/// <summary>
/// Where CouchHUD's own screenshots go and what they are called: %USERPROFILE%\Pictures\CouchHUD\Screenshots\&lt;game&gt;_&lt;time&gt;.png.
/// The user's real Pictures folder below the profile, on purpose not the "Pictures" known folder: that one is often moved into
/// OneDrive, and screenshots stay on this PC. The name of a game comes from a store or a scan and is never trusted as a file name.
/// </summary>
public static class ScreenshotFiles
{
    /// <summary>Used when no game is in front, or the game's name leaves nothing usable.</summary>
    public const string DefaultName = "CouchHUD";

    /// <summary>Longest part taken from the game's name (the time and the extension come on top).</summary>
    public const int MaxNameLength = 60;

    /// <summary>A name that is already taken gets _2, _3 … up to this many tries (within one second).</summary>
    public const int MaxAttempts = 50;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³"
    };

    /// <summary>The folder below the user's profile: <c>Pictures\CouchHUD\Screenshots</c>.</summary>
    public static string Folder(string userProfile) => Path.Combine(PicturesRoot(userProfile), "CouchHUD", "Screenshots");

    private static string PicturesRoot(string userProfile) => Path.Combine(userProfile, "Pictures");

    /// <summary>
    /// The game's name as a safe part of a file name: letters, digits and a few harmless signs; everything else (path signs, colons
    /// for streams, control and direction marks, wildcards) becomes "_". No dots or spaces at the ends, never a device name such as
    /// CON or COM1, at most <see cref="MaxNameLength"/> characters. Empty or nothing left: <see cref="DefaultName"/>.
    /// </summary>
    public static string SafeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return DefaultName;
        var builder = new StringBuilder(Math.Min(name.Length, MaxNameLength * 2));
        string normalized;
        try
        {
            normalized = name.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            // A broken surrogate: the runes below turn it into a replacement sign, which becomes "_".
            normalized = name;
        }
        foreach (var rune in normalized.EnumerateRunes())
        {
            var keep = Rune.IsLetterOrDigit(rune) || rune.Value is ' ' or '-' or '_' or '.' or '(' or ')' or '[' or ']' or '\'' or '!' or '&' or '+' or ',';
            var next = keep ? rune.ToString() : "_";
            // Runs of replacements and spaces shrink to one.
            if ((next == "_" || next == " ") && builder.Length > 0 && builder[^1] == next[0]) continue;
            builder.Append(next);
            if (builder.Length >= MaxNameLength * 2) break;
        }
        var text = builder.ToString();
        if (text.Length > MaxNameLength)
        {
            // Never cut through a surrogate pair.
            var cut = MaxNameLength;
            if (char.IsHighSurrogate(text[cut - 1])) cut--;
            text = text[..cut];
        }
        text = text.Trim(' ', '.', '_');
        // ".." alone or with spaces is gone after the trim; a name of only signs leaves nothing.
        if (text.Length == 0 || text.All(c => c == '_' || c == '.' || c == ' ')) return DefaultName;
        var stem = text.Split('.')[0].TrimEnd(' ');
        if (ReservedNames.Contains(stem)) text = "_" + text;
        return text;
    }

    /// <summary>"&lt;name&gt;_2026-10-27_20-15-03.png", from the second try on with "_2", "_3" ….</summary>
    public static string FileName(string? game, DateTime localTime, int attempt = 1) =>
        $"{SafeName(game)}_{localTime:yyyy-MM-dd_HH-mm-ss}{(attempt > 1 ? $"_{attempt}" : "")}.png";

    /// <summary>
    /// Writes a new screenshot and returns its full path. The folders are made where missing; a junction or symbolic link anywhere
    /// from Pictures down (Pictures, CouchHUD, Screenshots) stops it, the file is always created new (never one that exists, never
    /// through a link of the same name), and the path Windows really opened must be the one asked for. On Windows the new file is
    /// marked "delete on close" through its own handle right away and the mark is only cleared once it is written and checked: a file
    /// that fails is removed by that handle, never by its name (which could lead somewhere else by then).
    /// </summary>
    /// <exception cref="IOException">The folder redirects, or no free name.</exception>
    public static string Save(string userProfile, string? game, DateTime localTime, Action<Stream> write)
    {
        var profile = Path.GetFullPath(userProfile);
        var folder = Path.GetFullPath(Folder(profile));
        // Checked before anything is created, so no folder is made behind a link; and again after.
        if (SavePathGuard.HasReparsePointBelow(profile, folder)) throw new IOException(Redirected);
        Directory.CreateDirectory(folder);
        if (SavePathGuard.HasReparsePointBelow(profile, folder)) throw new IOException(Redirected);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var path = Path.Combine(folder, FileName(game, localTime, attempt));
            // The name came out of SafeName; still, the file must sit right in the folder.
            if (!string.Equals(Path.GetDirectoryName(path), folder, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid screenshot name");
            if (OperatingSystem.IsWindows())
            {
                if (SaveWindows(profile, path, write)) return path;
                continue;
            }
            if (SavePortable(profile, path, write)) return path;
        }
        throw new IOException("No free file name for the screenshot");
    }

    private const string Redirected = "The screenshots folder is redirected (junction or link)";

    /// <summary>
    /// Free bytes for this user on the drive the screenshots go to (the profile's drive: a redirected Pictures folder is refused by
    /// <see cref="Save"/> anyway). Null when Windows does not say (see <see cref="ScreenshotRules.EnoughSpace"/>: then none is taken).
    /// </summary>
    public static long? FreeBytes(string userProfile)
    {
        try
        {
            var profile = Path.GetFullPath(userProfile);
            if (OperatingSystem.IsWindows())
                return Native.GetDiskFreeSpaceExW(profile, out var available, out _, out _) ? (long)Math.Min(available, long.MaxValue) : null;
            return new DriveInfo(profile).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>False when the name is taken (try the next one).</summary>
    private static bool SaveWindows(string profile, string path, Action<Stream> write)
    {
        var handle = Native.CreateFileW(path, Native.GenericWrite | Native.Delete, 0, IntPtr.Zero, Native.CreateNew, Native.FileAttributeNormal, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is Native.ErrorFileExists or Native.ErrorAlreadyExists) return false;
            throw new IOException($"Could not create the screenshot ({error})", Marshal.GetHRForLastWin32Error());
        }
        // From here on the file goes away with its handle unless it is good.
        using (handle)
        {
            if (!Native.SetDeleteOnClose(handle, true)) throw new IOException($"Could not mark the new screenshot ({Marshal.GetLastWin32Error()})");
            using var stream = new FileStream(handle, FileAccess.Write, 1 << 16);
            if (!SavePathGuard.IsFinalPath(handle, profile, path)) throw new IOException(Redirected);
            write(stream);
            stream.Flush(flushToDisk: true);
            if (!Native.SetDeleteOnClose(handle, false)) throw new IOException($"Could not keep the screenshot ({Marshal.GetLastWin32Error()})");
        }
        return true;
    }

    /// <summary>Other systems (tests on Linux and macOS): no disposition by handle, the file is removed by name.</summary>
    private static bool SavePortable(string profile, string path, Action<Stream> write)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        }
        catch (IOException) when (File.Exists(path) || Directory.Exists(path))
        {
            return false;
        }
        var done = false;
        try
        {
            using (stream)
            {
                if (!SavePathGuard.IsFinalPath(stream.SafeFileHandle, profile, path)) throw new IOException(Redirected);
                write(stream);
            }
            done = true;
            return true;
        }
        finally
        {
            if (!done)
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
    }

    private static class Native
    {
        public const uint GenericWrite = 0x40000000;
        public const uint Delete = 0x00010000;
        public const uint CreateNew = 1;
        public const uint FileAttributeNormal = 0x80;
        public const int ErrorFileExists = 80;
        public const int ErrorAlreadyExists = 183;
        private const int FileDispositionInfo = 4;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetDiskFreeSpaceExW(string directory, out ulong freeForCaller, out ulong total, out ulong totalFree);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref byte info, uint size);

        /// <summary>FILE_DISPOSITION_INFO { BOOLEAN DeleteFile }: set or clear "delete when the last handle closes".</summary>
        public static bool SetDeleteOnClose(SafeFileHandle handle, bool delete)
        {
            byte value = delete ? (byte)1 : (byte)0;
            return SetFileInformationByHandle(handle, FileDispositionInfo, ref value, 1);
        }
    }
}

/// <summary>
/// The screenshot chord on the controller: Guide held, then <see cref="ShotButton"/>. While the chord is armed the Guide button's own
/// tap waits for its release: a tap that became a chord does nothing else (no HUD, no guide menu). Holding Guide alone still opens the
/// guide menu after the hold time, as before. Pure state, fed from the polling events on the UI thread.
/// </summary>
public sealed class GuideChord
{
    /// <summary>Y: free in Steam's own Guide chords (RB takes Steam's screenshot, X its keyboard).</summary>
    public const string ShotButton = "y";

    private bool _held;
    private bool _used;

    public bool Held => _held;

    /// <summary>Guide went down (with the chord armed).</summary>
    public void GuideDown()
    {
        _held = true;
        _used = false;
    }

    /// <summary>True when this press makes the chord: Guide is held and the button is <see cref="ShotButton"/>.</summary>
    public bool ButtonDown(string button, bool guideStillPressed)
    {
        if (!_held || !guideStillPressed || button != ShotButton) return false;
        _used = true;
        return true;
    }

    /// <summary>Guide came up. True when its own tap action is due (it was held through the chord path and no chord was made).</summary>
    public bool GuideUp()
    {
        var tap = _held && !_used;
        _held = false;
        _used = false;
        return tap;
    }

    /// <summary>The hold action ran or the state is no longer valid (browser, mouse mode): the release does nothing.</summary>
    public void Cancel()
    {
        _held = false;
        _used = false;
    }
}

/// <summary>At most one screenshot per <see cref="MinInterval"/>, and never two at once. Times from a monotonic clock.</summary>
public sealed class ScreenshotThrottle(TimeSpan minInterval)
{
    private DateTimeOffset _last = DateTimeOffset.MinValue;
    private bool _busy;

    public TimeSpan MinInterval { get; } = minInterval;

    /// <summary>True when a screenshot may start now; it then counts as running until <see cref="End"/>.</summary>
    public bool TryBegin(DateTimeOffset now)
    {
        if (_busy) return false;
        if (_last != DateTimeOffset.MinValue && now - _last < MinInterval && now >= _last) return false;
        _busy = true;
        _last = now;
        return true;
    }

    public void End() => _busy = false;
}

/// <summary>
/// At most <see cref="Max"/> screenshots within <see cref="Window"/> (audit 0.5 A4): a turbo pad holding Guide + Y would otherwise
/// write one picture a second for hours. Once the cap is hit, the burst is over only after a <see cref="QuietGap"/> without any try: until
/// then every press is refused silently, even when the window would let one through again. Otherwise refusals and screenshots alternate
/// as the window rolls on and the note comes again and again (VM test 0.5: three times in 26 tries). Times from a monotonic clock.
/// </summary>
public sealed class ScreenshotBurstCap(int max, TimeSpan window, TimeSpan quietGap)
{
    private readonly Queue<DateTimeOffset> _taken = new();
    private bool _blocked;
    private DateTimeOffset _lastTry;

    public ScreenshotBurstCap(int max, TimeSpan window) : this(max, window, TimeSpan.FromSeconds(10)) { }

    /// <summary>20 a minute, then 10 s without a press: more than anyone presses by hand, little enough to stop a held turbo button.</summary>
    public static ScreenshotBurstCap Default() => new(20, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10));

    public int Max { get; } = max;

    public TimeSpan Window { get; } = window;

    public TimeSpan QuietGap { get; } = quietGap;

    /// <summary>
    /// True when one more screenshot fits (it is counted then). False: the cap is reached, or the burst that hit it has not paused for
    /// <see cref="QuietGap"/> yet; <paramref name="say"/> is true only for the refusal that starts a burst (one note per burst, not one
    /// per press).
    /// </summary>
    public bool TryTake(DateTimeOffset now, out bool say)
    {
        say = false;
        // A clock that went back clears the list (and ends the burst) rather than keeping old entries forever.
        while (_taken.Count > 0 && (now - _taken.Peek() >= Window || now < _taken.Peek())) _taken.Dequeue();
        if (_blocked)
        {
            var stillBursting = now >= _lastTry && now - _lastTry < QuietGap;
            _lastTry = now;
            if (stillBursting) return false;
            _blocked = false;
        }
        if (_taken.Count >= Max)
        {
            _blocked = true;
            _lastTry = now;
            say = true;
            return false;
        }
        _taken.Enqueue(now);
        return true;
    }
}

/// <summary>
/// What the UI page reported on screen (a code entry, the phone's pairing code), and whether it has reported at all since it started
/// (audit 0.5 A3): a fresh page (start, reload, renderer crash) knows nothing until it reports both, and screenshots are refused until
/// then (fail closed). <see cref="Serial"/> moves with every report that something came up and with every new page, so a capture that
/// ran while one came (and maybe went again) is thrown away.
/// </summary>
public sealed class ScreenshotScreenState
{
    private bool _codeKnown;
    private bool _pairingKnown;

    public bool CodeEntryShown { get; private set; }

    public bool PairingShown { get; private set; }

    /// <summary>The page reported both states since it started.</summary>
    public bool Known => _codeKnown && _pairingKnown;

    public int Serial { get; private set; }

    /// <summary>The page's report of a code entry; the same report again (the page repeats them now and then) moves nothing.</summary>
    public void CodeEntry(bool open)
    {
        if (open && !(CodeEntryShown && _codeKnown)) Serial++;
        CodeEntryShown = open;
        _codeKnown = true;
    }

    /// <summary>The page's report of the pairing code; the same report again moves nothing.</summary>
    public void Pairing(bool open)
    {
        if (open && !(PairingShown && _pairingKnown)) Serial++;
        PairingShown = open;
        _pairingKnown = true;
    }

    /// <summary>A new page: nothing it reported before counts, until it reports again.</summary>
    public void Reset()
    {
        Serial++;
        CodeEntryShown = false;
        PairingShown = false;
        _codeKnown = false;
        _pairingKnown = false;
    }
}

public enum ScreenshotRefusal
{
    None,
    /// <summary>A code entry is on screen: the couch code, the parents' code, a profile code or the Windows sign-in for CouchHUD.</summary>
    CodeEntry,
    /// <summary>The on-screen keyboard is open on a secret field (or a pasted value, which is often a token).</summary>
    SecretField,
    /// <summary>The phone's pairing code or QR code is on screen: a picture of it would pair a phone.</summary>
    PairingCode,
    /// <summary>The UI page has not said yet what it shows (just started or reloaded): nothing is known, nothing is taken.</summary>
    UiUnknown
}

/// <summary>When a screenshot is refused. A child taking screenshots under parental controls is fine; codes and secrets never.</summary>
public static class ScreenshotRules
{
    /// <summary>Below this much free space on the drive of the screenshots no new one is written (audit 0.5 A4): 1 GiB.</summary>
    public const long MinFreeBytes = 1L << 30;

    /// <summary>After the capture the host waits this long before it checks the page's reports again (a report still on its way arrives).</summary>
    public const int SettleMs = 150;

    /// <param name="codeEntryShown">The UI shows a code entry (it reports it, see the bridge's codeEntryState).</param>
    /// <param name="hostCodePrompt">The host waits for the Windows sign-in or the parents' code.</param>
    /// <param name="secretKeyboard">The on-screen keyboard is open on a field the remote must not see either.</param>
    /// <param name="pairingShown">The UI shows the phone's pairing code or QR code (see the bridge's pairingState).</param>
    /// <param name="uiKnown">The UI page has reported what it shows since it started (see <see cref="ScreenshotScreenState"/>).</param>
    public static ScreenshotRefusal Check(bool codeEntryShown, bool hostCodePrompt, bool secretKeyboard, bool pairingShown = false, bool uiKnown = true) =>
        codeEntryShown || hostCodePrompt ? ScreenshotRefusal.CodeEntry
        : secretKeyboard ? ScreenshotRefusal.SecretField
        : pairingShown ? ScreenshotRefusal.PairingCode
        : !uiKnown ? ScreenshotRefusal.UiUnknown
        : ScreenshotRefusal.None;

    /// <summary>Enough room for another screenshot. Unknown free space (null) counts as not enough: fail closed.</summary>
    public static bool EnoughSpace(long? freeBytes) => freeBytes is { } free && free >= MinFreeBytes;
}
