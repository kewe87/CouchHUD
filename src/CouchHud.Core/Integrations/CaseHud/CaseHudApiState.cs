using System.Text;
using System.Text.Json;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Integrations.CaseHud;

/// <summary>
/// Where CaseHUD stands for CouchHUD. Since CaseHUD made its API v1 switchable (Settings › Interfaces, off by default) "installed and
/// running" no longer means "controllable": a running CaseHUD with the API off writes no api.json and refuses every request with
/// 403 <c>api_disabled</c>.
/// </summary>
public enum CaseHudApiState
{
    NotInstalled,
    /// <summary>Installed, no CaseHUD process (instance.pid missing, its process gone, or the PID runs another program).</summary>
    NotRunning,
    /// <summary>Running, but the API is switched off in CaseHUD (no api.json, or a request was refused with 403 api_disabled).</summary>
    ApiOff,
    ApiOn
}

public static class CaseHudState
{
    /// <summary>
    /// The state from what was observed. <paramref name="apiAnnounced"/>: a valid api.json whose process is the installed CaseHUD.
    /// <paramref name="instanceRunning"/>: instance.pid names a live process that is the installed CaseHUD.exe (builds before the switch
    /// write no instance.pid, but their API is always on, so a valid api.json alone also means "running").
    /// <paramref name="apiRefused"/>: a request with the announced token was answered with 403 api_disabled.
    /// </summary>
    public static CaseHudApiState Detect(bool installed, bool instanceRunning, bool apiAnnounced, bool apiRefused)
    {
        if (apiAnnounced && !apiRefused) return CaseHudApiState.ApiOn;
        if (!installed) return CaseHudApiState.NotInstalled;
        if (instanceRunning || apiAnnounced) return CaseHudApiState.ApiOff;
        return CaseHudApiState.NotRunning;
    }

    /// <summary>The name the page gets (camelCase, stable).</summary>
    public static string Name(CaseHudApiState state) => state switch
    {
        CaseHudApiState.NotInstalled => "notInstalled",
        CaseHudApiState.NotRunning => "notRunning",
        CaseHudApiState.ApiOff => "apiOff",
        _ => "apiOn"
    };

    /// <summary>403 with code api_disabled: the API is switched off in CaseHUD. A 403 "forbidden" (Host header) is a different case and stays a generic error.</summary>
    public static bool IsApiDisabled(int status, string? code) => status == 403 && string.Equals(code, ApiDisabledCode, StringComparison.Ordinal);

    public static bool IsApiDisabled(CaseHudException ex) => IsApiDisabled(ex.Status, ex.Code);

    public const string ApiDisabledCode = "api_disabled";

    /// <summary>The exception every caller gets for a CaseHUD whose API is off: the hint where to switch it on.</summary>
    public static CaseHudException ApiOff() => new(ApiDisabledCode, Strings.Get("casehud.apiOff"), 403);
}

/// <summary>Result of reading api.json: missing (API off or CaseHUD not running), invalid (possibly half written: try again) or valid.</summary>
public enum CaseHudFileRead { Missing, Invalid, Valid }

/// <summary>
/// The two files CaseHUD keeps in %APPDATA%\casehud. The folder is writable by the user (and so by any program of the user): both
/// files are untrusted input. Size cap, strict JSON (no comments, no trailing commas, no duplicate keys, numbers as numbers), port
/// range, token charset and length. Nothing named there is ever executed; the PID only counts after its image was compared with the
/// installed CaseHUD.exe (<see cref="CaseHudDiscovery"/>).
/// </summary>
public static class CaseHudFiles
{
    public const string ApiFileName = "api.json";
    public const string PidFileName = "instance.pid";
    public const int MaxApiJsonBytes = 16 * 1024;
    public const int MaxPidFileBytes = 64;
    public const int MinPort = 1;
    public const int MinTokenLength = 16;
    public const int MaxTokenLength = 128;
    public const int MaxExeLength = 1024;
    public const int MaxVersionLength = 64;

    private static readonly JsonDocumentOptions Strict = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 8,
        AllowDuplicateProperties = false
    };

    /// <summary>The token as CaseHUD writes it: ASCII letters and digits only, 16 to 128 of them (goes into an HTTP header as is).</summary>
    public static bool IsValidToken(string? token) =>
        token is { Length: >= MinTokenLength and <= MaxTokenLength } && token.All(char.IsAsciiLetterOrDigit);

    /// <summary>The content of api.json, or null when it is not exactly what CaseHUD API v1 writes.</summary>
    public static CaseHudApiInfo? ParseApiJson(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length == 0 || utf8.Length > MaxApiJsonBytes) return null;
        // A UTF-8 BOM is tolerated, nothing else in front of the object.
        if (utf8.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) utf8 = utf8[3..];
        try
        {
            using var doc = JsonDocument.Parse(utf8.ToArray(), Strict);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (Int(root, "apiVersion") != CaseHudInfo.ApiVersion) return null;
            if (Int(root, "port") is not { } port || port is < MinPort or > 65535) return null;
            if (Int(root, "pid") is not { } pid || pid <= 0) return null;
            var token = Str(root, "token");
            if (!IsValidToken(token)) return null;
            var exe = Str(root, "exe");
            if (string.IsNullOrWhiteSpace(exe) || exe.Length > MaxExeLength || exe.Any(char.IsControl)) return null;
            var version = Str(root, "version") ?? "";
            if (version.Length > MaxVersionLength || version.Any(char.IsControl)) return null;
            return new CaseHudApiInfo { ApiVersion = CaseHudInfo.ApiVersion, Version = version, Port = port, Token = token!, Pid = pid, Exe = exe };
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            return null;
        }
    }

    public static CaseHudApiInfo? ParseApiJson(string json) => ParseApiJson(Encoding.UTF8.GetBytes(json));

    private static int? Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static string? Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>instance.pid: decimal digits only (CaseHUD's contract; one line end after them is tolerated), nothing else; null otherwise.</summary>
    public static int? ParsePid(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0 || bytes.Length > MaxPidFileBytes) return null;
        if (bytes.EndsWith("\r\n"u8)) bytes = bytes[..^2];
        else if (bytes.EndsWith("\n"u8)) bytes = bytes[..^1];
        if (bytes.Length is 0 or > 10) return null;
        foreach (var b in bytes)
            if (b is < (byte)'0' or > (byte)'9') return null;
        return int.TryParse(bytes, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid) && pid > 0 ? pid : null;
    }

    /// <summary>Reads a file of at most <paramref name="max"/> bytes; null when it is missing, larger or cannot be read right now.</summary>
    public static byte[]? ReadSmall(string path, int max, out bool exists)
    {
        exists = false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            exists = true;
            if (stream.Length > max) return null;
            var buffer = new byte[max + 1];
            var total = 0;
            int read;
            while (total <= max && (read = stream.Read(buffer, total, buffer.Length - total)) > 0) total += read;
            return total > max ? null : buffer[..total];
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked while CaseHUD writes it: exists, but not readable this time.
            exists = true;
            return null;
        }
    }
}

/// <summary>
/// The quiet period of the folder watcher as a plain state machine (so it can be tested without timers): every change pushes the
/// moment to act out by <c>quiet</c>, but never past <c>maxWait</c> after the first change, so a file that keeps changing is still read.
/// </summary>
public sealed class CaseHudDebounce(TimeSpan quiet, TimeSpan maxWait)
{
    private DateTimeOffset? _first;
    private DateTimeOffset? _last;

    public bool Pending => _last is not null;

    public void Poke(DateTimeOffset now)
    {
        _first ??= now;
        _last = now;
    }

    /// <summary>Time until the change is due (zero or less = now), or null when nothing is pending.</summary>
    public TimeSpan? Due(DateTimeOffset now)
    {
        if (_last is not { } last || _first is not { } first) return null;
        var due = last + quiet < first + maxWait ? last + quiet : first + maxWait;
        return due - now;
    }

    /// <summary>True (and nothing pending afterwards) when the change is due now.</summary>
    public bool TryFire(DateTimeOffset now)
    {
        if (Due(now) is not { } left || left > TimeSpan.Zero) return false;
        _first = _last = null;
        return true;
    }
}

/// <summary>
/// Watches %APPDATA%\casehud for api.json and instance.pid (created, changed, deleted, renamed) and calls <c>changed</c> once per
/// burst of changes (<see cref="CaseHudDebounce"/>). A folder that does not exist yet, or a watcher that fails (buffer overflow,
/// folder deleted, network drive), falls back to a slow look at the two files every <see cref="PollEvery"/>; the poll tries to set
/// up the watcher again and stops once it works. No tight loop anywhere.
/// </summary>
public sealed class CaseHudFolderWatcher : IDisposable
{
    public static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(5);

    private readonly string _folder;
    private readonly Action _changed;
    private readonly bool _useWatcher;
    private readonly TimeSpan _pollEvery;
    private readonly CaseHudDebounce _debounce;
    private readonly object _lock = new();
    private readonly Timer _fire;
    private Timer? _poll;
    private FileSystemWatcher? _watcher;
    private string _fingerprint = "";
    private bool _disposed;
    private bool _running;

    /// <param name="useWatcher">False: only the poll (tests, and the fallback).</param>
    public CaseHudFolderWatcher(string folder, Action changed, bool useWatcher = true, TimeSpan? pollEvery = null, TimeSpan? quiet = null)
    {
        _folder = folder;
        _changed = changed;
        _useWatcher = useWatcher;
        _pollEvery = pollEvery ?? PollEvery;
        _debounce = new CaseHudDebounce(quiet ?? Quiet, MaxWait);
        _fire = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>True while the system watcher is active (false: the slow poll runs).</summary>
    public bool Watching
    {
        get { lock (_lock) return _watcher is not null; }
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _fingerprint = Fingerprint();
            if (!TryWatch()) StartPoll();
        }
    }

    /// <summary>Asks for another round (e.g. a half-written api.json that did not parse yet).</summary>
    public void Poke()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _debounce.Poke(DateTimeOffset.UtcNow);
            Schedule();
        }
    }

    private bool TryWatch()
    {
        if (!_useWatcher || !Directory.Exists(_folder)) return false;
        try
        {
            var w = new FileSystemWatcher(_folder)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                IncludeSubdirectories = false
            };
            w.Filters.Add(CaseHudFiles.ApiFileName);
            w.Filters.Add(CaseHudFiles.PidFileName);
            // Written through a temporary file and renamed: the rename into the name counts (the filters see the new name).
            FileSystemEventHandler onChange = (_, _) => Poke();
            w.Created += onChange;
            w.Changed += onChange;
            w.Deleted += onChange;
            w.Renamed += (_, _) => Poke();
            w.Error += (_, _) => Failed();
            w.EnableRaisingEvents = true;
            _watcher = w;
            _poll?.Dispose();
            _poll = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>The watcher lost track (overflow, folder gone): one round now, then the poll until it can watch again.</summary>
    private void Failed()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _watcher?.Dispose();
            _watcher = null;
            _debounce.Poke(DateTimeOffset.UtcNow);
            Schedule();
            StartPoll();
        }
    }

    private void StartPoll() => _poll ??= new Timer(_ => PollOnce(), null, _pollEvery, _pollEvery);

    private void PollOnce()
    {
        lock (_lock)
        {
            if (_disposed) return;
            var now = Fingerprint();
            if (now != _fingerprint)
            {
                _fingerprint = now;
                _debounce.Poke(DateTimeOffset.UtcNow);
                Schedule();
            }
            // The folder may have appeared (CaseHUD's first start) or the watcher may work again.
            if (_watcher is null && TryWatch()) _fingerprint = Fingerprint();
        }
    }

    /// <summary>Existence, size and time of both files: enough to notice a change without reading them.</summary>
    private string Fingerprint()
    {
        static string Of(string path)
        {
            try
            {
                var f = new FileInfo(path);
                return f.Exists ? $"{f.Length}:{f.LastWriteTimeUtc.Ticks}" : "-";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return "?";
            }
        }
        return Of(Path.Combine(_folder, CaseHudFiles.ApiFileName)) + "|" + Of(Path.Combine(_folder, CaseHudFiles.PidFileName));
    }

    private void Schedule()
    {
        if (_debounce.Due(DateTimeOffset.UtcNow) is { } left) _fire.Change(left < TimeSpan.Zero ? TimeSpan.Zero : left, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// One round at a time: a change that comes in while a round runs stays pending and is scheduled when that round ends. After
    /// <see cref="Dispose"/> no new round starts.
    /// </summary>
    private void Fire()
    {
        lock (_lock)
        {
            if (_disposed || _running) return;
            if (!_debounce.TryFire(DateTimeOffset.UtcNow))
            {
                Schedule();
                return;
            }
            _fingerprint = Fingerprint();
            _running = true;
        }
        try { _changed(); }
        catch (Exception ex) { Console.Error.WriteLine($"[casehud] folder change handler failed: {ex.GetType().Name}"); }
        finally
        {
            lock (_lock)
            {
                _running = false;
                if (!_disposed && _debounce.Pending) Schedule();
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _watcher?.Dispose();
            _watcher = null;
            _poll?.Dispose();
            _poll = null;
            _fire.Dispose();
        }
    }
}

/// <summary>Session and start time of a process: an announcement only counts for a CaseHUD in this session that started before it was written.</summary>
public readonly record struct CaseHudProcessFacts(int SessionId, DateTime StartUtc);

/// <summary>
/// The announcement (port, process, token) CaseHUD refused with 403 api_disabled. While it stands the state shows "API off" and requests
/// are held back; every change in the folder clears it (CaseHUD may switch the API on again with the very same values), a successful
/// answer clears it, and every <see cref="Recheck"/> one request may try again (a change the watcher missed). Not thread-safe: the
/// owner holds its lock.
/// </summary>
public sealed class CaseHudRefusal(Func<DateTimeOffset>? clock = null)
{
    public static readonly TimeSpan Recheck = TimeSpan.FromSeconds(30);

    private readonly Func<DateTimeOffset> _now = clock ?? (() => DateTimeOffset.UtcNow);
    private CaseHudApiInfo? _for;
    private DateTimeOffset _at;

    public bool Active => _for is not null;

    public void Mark(CaseHudApiInfo? info)
    {
        _for = info;
        _at = _now();
    }

    public void Clear() => _for = null;

    /// <summary>For the state shown: this exact announcement was refused and nothing has cleared that since.</summary>
    public bool Refused(CaseHudApiInfo? info) => Same(info, _for);

    /// <summary>For requests: refused, unless the last refusal is older than <see cref="Recheck"/> (then one may look again).</summary>
    public bool Blocks(CaseHudApiInfo? info) => Same(info, _for) && _now() - _at < Recheck;

    /// <summary>Same port, process and token (the token is compared in memory only, never written anywhere).</summary>
    public static bool Same(CaseHudApiInfo? a, CaseHudApiInfo? b) =>
        a is not null && b is not null && a.Port == b.Port && a.Pid == b.Pid && string.Equals(a.Token, b.Token, StringComparison.Ordinal);
}
