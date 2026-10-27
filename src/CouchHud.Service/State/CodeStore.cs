using System.Text.Json;
using System.Text.Json.Serialization;
using CouchHud.Core.Security;
using CouchHud.Privileged;
using CouchHud.Service.Authorization;
using CouchHud.Service.Contracts;

namespace CouchHud.Service.State;

/// <summary>The two codes the service keeps per account.</summary>
public enum CodeKind
{
    Couch,
    Parent
}

/// <summary>A count of wrong tries with its wait, kept across restarts (see <see cref="CodeStore"/>).</summary>
public class Backoff
{
    /// <summary>Wrong tries in a row (a try counts before it is checked, see <see cref="CodeStore.Verify"/>).</summary>
    public int Failures { get; set; }

    /// <summary>Until when (monotonic clock, ms, valid for <see cref="CodeData.BootTimeMs"/>) the next try has to wait; 0: no wait.</summary>
    public long LockedUntilMs { get; set; }

    /// <summary>How long the running lockout is (ms): after a restart of the machine it starts again in full.</summary>
    public long LockMs { get; set; }
}

/// <summary>One code: its hash and its own lockout (wrong tries at one code never lock out the other).</summary>
public sealed class CodeEntry : Backoff
{
    public string Hash { get; set; } = "";

    /// <summary>
    /// Enrolled without anybody's approval while no lock was on (trust on first use). Such a code does not survive switching a lock on unless
    /// it is set again in that same step (review 3, item 2): a code planted at an open couch never becomes the lock.
    /// </summary>
    public bool Open { get; set; }
}

public sealed class AccountCodes
{
    public CodeEntry? Couch { get; set; }

    public CodeEntry? Parent { get; set; }

    /// <summary>The couch lock mode the account set (<see cref="LockModes"/>); null: never set (see <see cref="CodeStore.View"/>).</summary>
    public string? Mode { get; set; }

    public CodeEntry? Get(CodeKind kind) => kind == CodeKind.Couch ? Couch : Parent;

    [JsonIgnore]
    public bool IsEmpty => Couch is null && Parent is null && Mode is null;

    public void Set(CodeKind kind, CodeEntry entry)
    {
        if (kind == CodeKind.Couch) Couch = entry;
        else Parent = entry;
    }
}

/// <summary>What <c>codes.json</c> holds: per account SID the couch code and the parents' code (each optional).</summary>
public sealed class CodeData
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>The machine's boot time (Unix ms) when this was written: the monotonic lockout times only mean something within one boot.</summary>
    public long BootTimeMs { get; set; }

    public Dictionary<string, AccountCodes> Accounts { get; set; } = new(StringComparer.Ordinal);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    NumberHandling = JsonNumberHandling.Strict,
    AllowTrailingCommas = false,
    ReadCommentHandling = JsonCommentHandling.Disallow,
    WriteIndented = true)]
[JsonSerializable(typeof(CodeData))]
internal sealed partial class CodeJsonContext : JsonSerializerContext
{
}

public enum CodeCheck
{
    Granted,
    Wrong,
    LockedOut,
    /// <summary>The service keeps no code of that kind for the account.</summary>
    NoCode,
    /// <summary>The store is not available (refused folder, damaged file, a write failed): nothing is checked.</summary>
    Unavailable
}

public sealed record CodeVerdict(CodeCheck Result, TimeSpan Wait = default);

public enum CodeSet
{
    Saved,
    /// <summary>EnrollPasscode: a code of that kind exists already (a change needs ChangePasscode).</summary>
    Exists,
    /// <summary>ChangePasscode: there is no code of that kind to change.</summary>
    Missing,
    /// <summary>Not a hash in the format of <c>Passcode.Hash</c>.</summary>
    BadHash,
    Unavailable
}

/// <summary>
/// The codes of every account, kept by the service (P9) in <c>codes.json</c> in its protected data folder (<c>{app}\service\data</c>,
/// SYSTEM and Administrators only). The service checks the buttons itself; the shell only ever learns "granted" (with a grant), "wrong" or
/// "wait". Each code has its own lockout: after <see cref="FreeFailures"/> wrong tries every further one waits, twice as long each time
/// (<see cref="FirstLock"/> up to <see cref="MaxLock"/>), and the count survives a restart of the service (written before the check) and of
/// the machine (the lockout then starts again in full, see <see cref="CodeEntry.LockMs"/>). Writes are atomic (temporary file, then a move);
/// a file that is not exactly right makes the store unavailable: every code is then treated as enrolled (releases need a grant or an
/// administrator) and nothing is checked or enrolled until an administrator removes the file (fail-closed).
/// </summary>
public sealed class CodeStore : ICodeState
{
    public const string FileName = "codes.json";
    public const int MaxFileBytes = 256 * 1024;
    public const int MaxAccounts = 256;
    public const int FreeFailures = 5;
    public const int MaxFailures = 10_000;
    public static readonly TimeSpan FirstLock = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The longest wait. Short enough that a child who keeps typing wrong parents' codes on purpose cannot shut the parents out for long
    /// (and the parents always have an administrator's approval through UAC, which this lockout does not touch).
    /// </summary>
    public static readonly TimeSpan MaxLock = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan BootTolerance = TimeSpan.FromMinutes(2);

    private readonly string? _file;
    private readonly IEventSink _log;
    private readonly IMonotonicClock _clock;
    private readonly Func<long> _bootTimeMs;
    private readonly Func<IReadOnlyList<string>, string, bool> _verify;
    private readonly Lock _lock = new();
    private CodeData? _current;

    private CodeStore(string? file, CodeData? current, IEventSink log, IMonotonicClock clock, Func<long> bootTimeMs, Func<IReadOnlyList<string>, string, bool>? verify)
    {
        _file = file;
        _current = current;
        _log = log;
        _clock = clock;
        _bootTimeMs = bootTimeMs;
        _verify = verify ?? Passcode.Verify;
    }

    /// <summary>
    /// Opens the codes in <paramref name="folder"/> (null: the folder was refused, the store is unavailable). A missing file is an empty
    /// store; anything else that is wrong makes it unavailable, and the file stays as it is for an administrator to look at.
    /// </summary>
    /// <param name="verify">The PBKDF2 check (tests pass a fast one); default <see cref="Passcode.Verify"/>.</param>
    public static CodeStore Open(string? folder, IEventSink log, IMonotonicClock clock, Func<long> bootTimeMs, Func<IReadOnlyList<string>, string, bool>? verify = null)
    {
        if (folder is null)
        {
            log.Error("[codes] no trusted data folder: codes cannot be checked, every account counts as having one");
            return new CodeStore(null, null, log, clock, bootTimeMs, verify);
        }
        var file = Path.Combine(folder, FileName);
        var data = Load(file, log);
        var store = new CodeStore(file, data, log, clock, bootTimeMs, verify);
        if (data is not null) store.CarryLockoutsOverRestart();
        return store;
    }

    public bool IsAvailable
    {
        get { lock (_lock) return _current is not null; }
    }

    /// <summary>
    /// Whether the account's couch can be locked: it has a code of either kind, or its lock mode is the Windows sign-in. True when the store
    /// is unavailable (fail-closed). Decides whether a release needs a grant (<see cref="Requirement.GrantOrAdminOnceCodeEnrolled"/>).
    /// </summary>
    public bool IsEnrolled(string sid) => View(sid).Protected;

    /// <summary>Whether the account has a code of either kind (an unavailable store says yes).</summary>
    public bool HasAnyCode(string sid)
    {
        lock (_lock)
        {
            if (_current is null) return true;
            return _current.Accounts.TryGetValue(sid, out var account) && (account.Couch is not null || account.Parent is not null);
        }
    }

    /// <summary>
    /// The account's lock as the service keeps it. A mode never set counts as "code" while a couch code exists (fail-closed for a code that
    /// came first), else as "off". Unavailable: <see cref="LockView.Closed"/>.
    /// </summary>
    public LockView View(string sid)
    {
        lock (_lock)
        {
            if (_current is null) return LockView.Closed;
            if (!_current.Accounts.TryGetValue(sid, out var account)) return new LockView(LockModes.Off, false, false);
            // A code that came first counts as the code lock, unless it was enrolled freely at an open couch (that one never becomes the lock).
            var mode = account.Mode ?? (account.Couch is { Open: false } ? LockModes.Code : LockModes.Off);
            return new LockView(mode, account.Couch is not null, account.Parent is not null);
        }
    }

    /// <summary>The mode exactly as stored (null: never set), for Status; null too when unavailable.</summary>
    public string? StoredMode(string sid)
    {
        lock (_lock) return _current is not null && _current.Accounts.TryGetValue(sid, out var account) ? account.Mode : null;
    }

    /// <summary>
    /// Stores the account's lock mode (whether it may is the dispatcher's question, see <see cref="StateCheck.LockModeChange"/>), all in one
    /// write: <paramref name="removeCouch"/>: the couch code goes (the switch to the Windows lock, review M-a); <paramref name="clearOpen"/>:
    /// codes enrolled freely at an open couch go (a lock is switched on, review 3); <paramref name="kinds"/> with <paramref name="hash"/>:
    /// the code that comes along with this switch (approved by it, not open). <see cref="CodeSet.Exists"/>: a code may not come along (a lock is
    /// on, or an approved code exists), nothing written.
    /// </summary>
    public CodeSet SetMode(string sid, string mode, bool removeCouch = false, bool clearOpen = false, IReadOnlyCollection<CodeKind>? kinds = null, string? hash = null)
    {
        if (LockModes.Rank(mode) < 0 || !ServiceStateStore.IsUserSid(sid)) return CodeSet.Unavailable;
        lock (_lock)
        {
            if (_current is null || _file is null) return CodeSet.Unavailable;
            if (!_current.Accounts.ContainsKey(sid) && _current.Accounts.Count >= MaxAccounts) return CodeSet.Unavailable;
            // A code coming along: checked again under the lock (the state check ran before): only from no lock, never next to an approved code.
            if (kinds is not null && hash is not null && _current.Accounts.TryGetValue(sid, out var now)
                && (LockModes.Rank(now.Mode) > 0 || now.Couch is { Open: false } || now.Parent is { Open: false })) return CodeSet.Exists;
            var next = Clone(_current);
            if (!next.Accounts.TryGetValue(sid, out var account)) next.Accounts[sid] = account = new AccountCodes();
            account.Mode = mode;
            if (clearOpen)
            {
                if (account.Couch is { Open: true }) account.Couch = null;
                if (account.Parent is { Open: true }) account.Parent = null;
            }
            if (removeCouch) account.Couch = null;
            if (kinds is not null && hash is not null)
            {
                if (!RequestValidator.IsPasscodeHash(hash)) return CodeSet.BadHash;
                foreach (var kind in kinds) account.Set(kind, new CodeEntry { Hash = hash });
            }
            return Save(next) ? CodeSet.Saved : CodeSet.Unavailable;
        }
    }

    /// <summary>
    /// Everything the account had (both codes, their lockouts and the mode) goes, in one write; with <paramref name="mode"/> the account starts
    /// over with that mode and, with <paramref name="kinds"/> and <paramref name="hash"/>, exactly that code (approved, not open). For a lock
    /// taken over from 0.4.0 (security review H-1, second round): what the account held before is forgotten when the lock is taken over, and
    /// the administrator's confirmation sets the one code (or the Windows mode) it confirms, nothing planted before survives it.
    /// </summary>
    public CodeSet Replace(string sid, string? mode, IReadOnlyCollection<CodeKind>? kinds = null, string? hash = null)
    {
        if (!ServiceStateStore.IsUserSid(sid) || (mode is not null && LockModes.Rank(mode) < 0)) return CodeSet.Unavailable;
        if (hash is not null && !RequestValidator.IsPasscodeHash(hash)) return CodeSet.BadHash;
        lock (_lock)
        {
            if (_current is null || _file is null) return CodeSet.Unavailable;
            if (mode is null && !_current.Accounts.ContainsKey(sid)) return CodeSet.Saved;
            if (mode is not null && !_current.Accounts.ContainsKey(sid) && _current.Accounts.Count >= MaxAccounts) return CodeSet.Unavailable;
            var next = Clone(_current);
            next.Accounts.Remove(sid);
            if (mode is not null)
            {
                var account = next.Accounts[sid] = new AccountCodes { Mode = mode };
                if (kinds is not null && hash is not null)
                    foreach (var kind in kinds) account.Set(kind, new CodeEntry { Hash = hash });
            }
            return Save(next) ? CodeSet.Saved : CodeSet.Unavailable;
        }
    }

    /// <summary>Whether the account has a code of this kind; true when the store is unavailable (fail-closed: "exists" closes more).</summary>
    public bool Has(string sid, CodeKind kind)
    {
        lock (_lock)
        {
            if (_current is null) return true;
            return _current.Accounts.TryGetValue(sid, out var account) && account.Get(kind) is not null;
        }
    }

    /// <summary>
    /// Checks the buttons against the account's code of <paramref name="kind"/>. The try is counted and written before the (slow) check,
    /// so neither parallel requests nor a service killed during the check get a free try; a right code resets the count afterwards.
    /// </summary>
    public CodeVerdict Verify(string sid, CodeKind kind, IReadOnlyList<string> sequence)
    {
        if (CountTry(sid, kind, out var hash) is { } early) return early;

        var right = false;
        try
        {
            right = _verify(sequence, hash);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            right = false;
        }

        lock (_lock)
        {
            if (_current is null) return new CodeVerdict(CodeCheck.Unavailable);
            if (!right)
            {
                var wait = _current.Accounts.TryGetValue(sid, out var account) && account.Get(kind) is { } entry && entry.LockedUntilMs > NowMs()
                    ? TimeSpan.FromMilliseconds(entry.LockedUntilMs - NowMs())
                    : TimeSpan.Zero;
                return new CodeVerdict(CodeCheck.Wrong, wait);
            }
            // Reset only when the code is still the one that was checked (it may have been changed in between).
            var next = Clone(_current);
            if (next.Accounts.TryGetValue(sid, out var after) && after.Get(kind) is { } still && still.Hash == hash)
            {
                still.Failures = 0;
                still.LockedUntilMs = 0;
                still.LockMs = 0;
                Save(next);
            }
            return new CodeVerdict(CodeCheck.Granted);
        }
    }

    /// <summary>
    /// A try that is refused without looking at the buttons (the caller's session is locked, finding B2 of the 0.4.1 acceptance test): it
    /// counts towards the lockout like a wrong code, so a locked session gives no free tries, and nothing about the code is told.
    /// <see cref="CodeCheck.Wrong"/> (with the wait the count caused), <see cref="CodeCheck.LockedOut"/>, <see cref="CodeCheck.NoCode"/> or
    /// <see cref="CodeCheck.Unavailable"/>.
    /// </summary>
    public CodeVerdict CountRefused(string sid, CodeKind kind)
    {
        if (CountTry(sid, kind, out _) is { } early) return early;
        lock (_lock)
        {
            var wait = _current is not null && _current.Accounts.TryGetValue(sid, out var account) && account.Get(kind) is { } entry && entry.LockedUntilMs > NowMs()
                ? TimeSpan.FromMilliseconds(entry.LockedUntilMs - NowMs())
                : TimeSpan.Zero;
            return new CodeVerdict(CodeCheck.Wrong, wait);
        }
    }

    /// <summary>Counts one try of the code and writes it; a verdict when the try ends here (no code, locked out, unavailable), else null and the hash to check.</summary>
    private CodeVerdict? CountTry(string sid, CodeKind kind, out string hash)
    {
        hash = "";
        lock (_lock)
        {
            if (_current is null || _file is null) return new CodeVerdict(CodeCheck.Unavailable);
            if (!_current.Accounts.TryGetValue(sid, out var account) || account.Get(kind) is not { } entry) return new CodeVerdict(CodeCheck.NoCode);
            var now = NowMs();
            if (entry.LockedUntilMs > now) return new CodeVerdict(CodeCheck.LockedOut, TimeSpan.FromMilliseconds(entry.LockedUntilMs - now));
            var next = Clone(_current);
            var counted = next.Accounts[sid].Get(kind)!;
            counted.Failures = Math.Min(counted.Failures + 1, MaxFailures);
            if (counted.Failures >= FreeFailures)
            {
                var lockFor = LockFor(counted.Failures);
                counted.LockMs = (long)lockFor.TotalMilliseconds;
                counted.LockedUntilMs = now + counted.LockMs;
            }
            if (!Save(next)) return new CodeVerdict(CodeCheck.Unavailable);
            hash = entry.Hash;
            return null;
        }
    }

    /// <summary>
    /// Stores a code: <paramref name="kinds"/> is the couch code, the parents' code, or both (to the same hash). <paramref name="enroll"/>:
    /// only when the account has none of them (EnrollPasscode); otherwise only when it has at least one (ChangePasscode).
    /// <paramref name="clearOther"/>: the kind not set here goes (a code set with a parent's or an administrator's approval replaces
    /// whatever the other kind was, review M4). The hash format is checked again here. A stored code starts its lockout afresh.
    /// </summary>
    public CodeSet Set(string sid, IReadOnlyCollection<CodeKind> kinds, string hash, bool enroll, bool clearOther = false, bool open = false)
    {
        if (!RequestValidator.IsPasscodeHash(hash)) return CodeSet.BadHash;
        if (!ServiceStateStore.IsUserSid(sid) || kinds.Count == 0) return CodeSet.Unavailable;
        lock (_lock)
        {
            if (_current is null || _file is null) return CodeSet.Unavailable;
            _current.Accounts.TryGetValue(sid, out var account);
            var existing = kinds.Count(k => account?.Get(k) is not null);
            if (enroll && existing > 0) return CodeSet.Exists;
            // A free enrollment at an open couch, checked again under the lock: no lock may be on, no approved code may exist (the state
            // check ran before; a lock switched on in between would otherwise get a planted code next to it).
            if (open && account is not null && (LockModes.Rank(account.Mode) > 0 || account.Couch is { Open: false } || account.Parent is { Open: false })) return CodeSet.Exists;
            if (!enroll && existing == 0) return CodeSet.Missing;
            if (account is null && _current.Accounts.Count >= MaxAccounts) return CodeSet.Unavailable;
            var next = Clone(_current);
            if (!next.Accounts.TryGetValue(sid, out var target)) next.Accounts[sid] = target = new AccountCodes();
            foreach (var kind in kinds) target.Set(kind, new CodeEntry { Hash = hash, Open = open });
            if (clearOther)
            {
                if (!kinds.Contains(CodeKind.Couch)) target.Couch = null;
                if (!kinds.Contains(CodeKind.Parent)) target.Parent = null;
            }
            return Save(next) ? CodeSet.Saved : CodeSet.Unavailable;
        }
    }

    /// <summary>One kind (see <see cref="Set(string, IReadOnlyCollection{CodeKind}, string, bool, bool)"/>).</summary>
    public CodeSet Set(string sid, CodeKind kind, string hash, bool enroll, bool clearOther = false, bool open = false) => Set(sid, [kind], hash, enroll, clearOther, open);

    /// <summary>Whether the account has a code of <paramref name="kind"/> that somebody approved (not one enrolled freely at an open couch).</summary>
    public bool HasApproved(string sid, CodeKind kind)
    {
        lock (_lock)
        {
            if (_current is null) return true;
            return _current.Accounts.TryGetValue(sid, out var account) && account.Get(kind) is { Open: false };
        }
    }

    /// <summary>
    /// Removes the account's code of <paramref name="kind"/> (the couch lock or the parental controls were switched off); an account without
    /// any code left is dropped. Afterwards the next code of that kind is a first enrollment again.
    /// </summary>
    public CodeSet Remove(string sid, CodeKind kind)
    {
        lock (_lock)
        {
            if (_current is null || _file is null) return CodeSet.Unavailable;
            if (!_current.Accounts.TryGetValue(sid, out var account) || account.Get(kind) is null) return CodeSet.Missing;
            var next = Clone(_current);
            var target = next.Accounts[sid];
            if (kind == CodeKind.Couch) target.Couch = null;
            else target.Parent = null;
            if (target.IsEmpty) next.Accounts.Remove(sid);
            return Save(next) ? CodeSet.Saved : CodeSet.Unavailable;
        }
    }

    /// <summary>The wait after <paramref name="failures"/> wrong tries in a row (zero below <see cref="FreeFailures"/>).</summary>
    public static TimeSpan LockFor(int failures)
    {
        if (failures < FreeFailures) return TimeSpan.Zero;
        var doublings = Math.Min(failures - FreeFailures, 20);
        return TimeSpan.FromTicks(Math.Min(FirstLock.Ticks << doublings, MaxLock.Ticks));
    }

    private long NowMs() => (long)_clock.Now.TotalMilliseconds;

    /// <summary>
    /// A lockout written in an earlier boot: its monotonic time means nothing now, so it starts again in full (a restart never shortens a
    /// wait). Within the same boot (a restart of the service only) the times stay as they are.
    /// </summary>
    private void CarryLockoutsOverRestart()
    {
        lock (_lock)
        {
            if (_current is null) return;
            var boot = _bootTimeMs();
            if (boot != 0 && Math.Abs(_current.BootTimeMs - boot) <= BootTolerance.TotalMilliseconds) return;
            var next = Clone(_current);
            var changed = false;
            foreach (var account in next.Accounts.Values)
            {
                foreach (var entry in new Backoff?[] { account.Couch, account.Parent })
                {
                    if (entry is null || entry.LockMs <= 0) continue;
                    entry.LockedUntilMs = NowMs() + entry.LockMs;
                    changed = true;
                }
            }
            if (changed || _current.BootTimeMs != boot) Save(next);
        }
    }

    private bool Save(CodeData next)
    {
        next.BootTimeMs = _bootTimeMs();
        if (Validate(next) is { } problem)
        {
            _log.Error($"[codes] change refused: {problem}");
            return false;
        }
        if (!Write(_file!, next)) return false;
        _current = next;
        return true;
    }

    private static CodeData? Load(string file, IEventSink log)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists)
            {
                if (info.Attributes != (FileAttributes)(-1)) return Corrupt(log, "not a file");
                return new CodeData();
            }
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return Corrupt(log, "reparse point");
            if (info.Length > MaxFileBytes) return Corrupt(log, "too large");
            var bytes = File.ReadAllBytes(file);
            if (bytes.Length > MaxFileBytes) return Corrupt(log, "too large");
            var data = JsonSerializer.Deserialize(bytes, CodeJsonContext.Default.CodeData);
            if (data is null) return Corrupt(log, "empty");
            if (data.Accounts is null) return Corrupt(log, "accounts missing");
            var accounts = new Dictionary<string, AccountCodes>(StringComparer.Ordinal);
            foreach (var (sid, account) in data.Accounts)
            {
                if (!accounts.TryAdd(sid, account)) return Corrupt(log, "account twice");
            }
            data.Accounts = accounts;
            if (Validate(data) is { } problem) return Corrupt(log, problem);
            return data;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return Corrupt(log, ex.GetType().Name);
        }
    }

    private static CodeData? Corrupt(IEventSink log, string problem)
    {
        log.Error($"[codes] the code file is not valid ({problem}): codes are not checked and every account counts as having one until an administrator removes it");
        return null;
    }

    /// <summary>Why the data is not acceptable, or null.</summary>
    internal static string? Validate(CodeData data)
    {
        if (data.Version != CodeData.CurrentVersion) return "version";
        if (data.Accounts is null) return "accounts missing";
        if (data.Accounts.Count > MaxAccounts) return "too many accounts";
        foreach (var (sid, account) in data.Accounts)
        {
            if (!ServiceStateStore.IsUserSid(sid)) return "account sid";
            if (account is null || account.IsEmpty) return "empty account";
            if (account.Mode is not null && LockModes.Rank(account.Mode) < 0) return "mode";
            foreach (var entry in new[] { account.Couch, account.Parent })
            {
                if (entry is null) continue;
                if (!RequestValidator.IsPasscodeHash(entry.Hash)) return "hash";
            }
            foreach (var entry in new Backoff?[] { account.Couch, account.Parent })
            {
                if (entry is null) continue;
                if (entry.Failures is < 0 or > MaxFailures) return "failures";
                if (entry.LockMs < 0 || entry.LockMs > (long)MaxLock.TotalMilliseconds) return "lock";
                if (entry.LockedUntilMs < 0) return "locked until";
            }
        }
        return null;
    }

    private bool Write(string file, CodeData data)
    {
        var temp = file + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(data, CodeJsonContext.Default.CodeData);
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, file, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log.Error($"[codes] not saved: {ex.GetType().Name}");
            try
            {
                File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }
            return false;
        }
    }

    private static CodeData Clone(CodeData data)
    {
        var copy = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(data, CodeJsonContext.Default.CodeData), CodeJsonContext.Default.CodeData)!;
        copy.Accounts = new Dictionary<string, AccountCodes>(copy.Accounts, StringComparer.Ordinal);
        return copy;
    }

    public static CodeKind? KindOf(string? wire) => wire switch
    {
        CodeKinds.Couch => CodeKind.Couch,
        CodeKinds.Parent => CodeKind.Parent,
        _ => null
    };
}
