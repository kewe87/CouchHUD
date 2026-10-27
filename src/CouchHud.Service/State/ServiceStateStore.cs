using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CouchHud.Core.Guard;
using CouchHud.Privileged;
using CouchHud.Service.Authorization;
using CouchHud.Service.Contracts;

namespace CouchHud.Service.State;

/// <summary>What the service set for one account: the policy groups and the Policies keys that did not exist before its first apply.</summary>
public sealed class AccountState
{
    public List<string> Groups { get; set; } = [];

    public List<string> CreatedKeys { get; set; } = [];

    /// <summary>The account's old SYSTEM tasks were removed (MigrateTasks).</summary>
    public bool Migrated { get; set; }

    /// <summary>
    /// Some of <see cref="Groups"/> were taken over from the lock CouchHUD 0.4.0 set (its SYSTEM tasks, finding B1): until an administrator
    /// confirmed the account's code or mode (security review H-1), a release and every change of codes or mode needs an administrator's
    /// approval, whatever the account holds. Gone with the last group (<see cref="StateData.Tidy"/>). Not written while false (a state of 0.4.1 reads the same).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool LegacyLock { get; set; }

    [JsonIgnore]
    public bool IsEmpty => Groups.Count == 0 && CreatedKeys.Count == 0 && !Migrated && !LegacyLock;
}

/// <summary>A service the service stopped or holds stopped: who wants it paused (account SIDs), and whether the service itself stopped it.</summary>
public sealed class PausedService
{
    public List<string> Holders { get; set; } = [];

    public bool StoppedByService { get; set; }
}

/// <summary>
/// Everything the service must remember across restarts: which policy groups and keys it owns per account, who paused which
/// service, who owns the automatic sign-in and which firewall ports it opened. Lives in <c>state.json</c> in the protected folder.
/// </summary>
public sealed class StateData
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>The machine's boot time (Unix ms) when this was written: a paused service is started by Windows again after a restart.</summary>
    public long BootTimeMs { get; set; }

    public Dictionary<string, AccountState> Accounts { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, PausedService> Paused { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? AutoLogonOwner { get; set; }

    public List<int>? FirewallPorts { get; set; }

    public AccountState Account(string sid)
    {
        if (!Accounts.TryGetValue(sid, out var account)) Accounts[sid] = account = new AccountState();
        return account;
    }

    /// <summary>Drops an account that holds nothing any more; a taken-over lock ends with its last group.</summary>
    public void Tidy(string sid)
    {
        if (!Accounts.TryGetValue(sid, out var account)) return;
        if (account.Groups.Count == 0) account.LegacyLock = false;
        if (account.IsEmpty) Accounts.Remove(sid);
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    NumberHandling = JsonNumberHandling.Strict,
    AllowTrailingCommas = false,
    ReadCommentHandling = JsonCommentHandling.Disallow,
    WriteIndented = true)]
[JsonSerializable(typeof(StateData))]
internal sealed partial class StateJsonContext : JsonSerializerContext
{
}

/// <summary>The outcome of <see cref="ServiceStateStore.Reset"/>.</summary>
public enum ResetOutcome
{
    /// <summary>A valid state was replaced by an empty one.</summary>
    Fresh,
    /// <summary>A damaged file was set aside (kept) and the state is empty now.</summary>
    CorruptKept,
    /// <summary>The state still holds something (the condition said no): nothing changed.</summary>
    Refused,
    Failed
}

/// <summary>The outcome of a change to the state.</summary>
public enum StateUpdate
{
    Saved,
    /// <summary>The change itself said no (nothing written).</summary>
    Unchanged,
    /// <summary>The state is not available (folder refused, file corrupt): nothing may be done that depends on it.</summary>
    Unavailable,
    /// <summary>The new state could not be written; the old one stays in force.</summary>
    WriteFailed
}

/// <summary>
/// The service's own state file. Read once at start and validated as a whole (versions, SIDs, group ids, keys, service names, ports,
/// sizes); a file that is not exactly right makes the state unavailable and every operation that depends on it is refused
/// (fail-closed) until an administrator removes the file. Changes go to a copy, are validated again, written to a temporary file
/// and moved over the old one (atomic on the same volume), and only then take effect in memory.
/// </summary>
public sealed class ServiceStateStore
{
    public const string FileName = "state.json";
    public const int MaxFileBytes = 256 * 1024;
    public const int MaxAccounts = 256;
    public const int MaxHolders = 256;

    // A restart is when the boot time moved by more than this (the computed boot time wanders by a few ms between reads).
    private static readonly TimeSpan BootTolerance = TimeSpan.FromMinutes(2);

    private static readonly Regex UserSid = new(@"^S-1-(5-21|12-1)(-[0-9]{1,10}){3,4}\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private readonly string? _file;
    private readonly IEventSink _log;
    private readonly Func<long> _bootTimeMs;
    private readonly Func<string, bool> _serviceOnList;
    private readonly Lock _lock = new();
    private StateData? _current;

    private ServiceStateStore(string? file, StateData? current, IEventSink log, Func<long> bootTimeMs, Func<string, bool> serviceOnList)
    {
        _file = file;
        _current = current;
        _log = log;
        _bootTimeMs = bootTimeMs;
        _serviceOnList = serviceOnList;
    }

    /// <summary>A store that is never available (the folder was refused).</summary>
    public static ServiceStateStore Unavailable(IEventSink log) => new(null, null, log, () => 0, _ => false);

    /// <summary>
    /// Opens the state in <paramref name="folder"/> (null: refused folder, the state is unavailable). A missing file is an empty state;
    /// anything else that is wrong makes it unavailable and the file is kept as it is for an administrator to look at.
    /// </summary>
    public static ServiceStateStore Open(string? folder, IEventSink log, Func<long> bootTimeMs, Func<string, bool> serviceOnList)
    {
        if (folder is null)
        {
            log.Error("[state] no trusted state folder: operations that need the service's state are refused");
            return Unavailable(log);
        }
        var file = Path.Combine(folder, FileName);
        var state = Load(file, log, serviceOnList);
        if (state is null) return new ServiceStateStore(file, null, log, bootTimeMs, serviceOnList);
        var store = new ServiceStateStore(file, state, log, bootTimeMs, serviceOnList);
        store.ForgetPausesOfEarlierBoot();
        return store;
    }

    public bool IsAvailable
    {
        get { lock (_lock) return _current is not null; }
    }

    /// <summary>A copy of the state, null when it is unavailable.</summary>
    public StateData? Snapshot()
    {
        lock (_lock) return _current is null ? null : Clone(_current);
    }

    /// <summary>
    /// Changes the state: <paramref name="change"/> works on a copy and returns false to change nothing. The copy must pass the same
    /// validation as a loaded file, is written atomically, and only then replaces the state in memory.
    /// </summary>
    public StateUpdate Update(Func<StateData, bool> change)
    {
        lock (_lock)
        {
            if (_current is null || _file is null) return StateUpdate.Unavailable;
            var next = Clone(_current);
            if (!change(next)) return StateUpdate.Unchanged;
            next.BootTimeMs = _bootTimeMs();
            if (Validate(next, _serviceOnList) is { } problem)
            {
                _log.Error($"[state] change refused: {problem}");
                return StateUpdate.WriteFailed;
            }
            if (!Write(_file, next)) return StateUpdate.WriteFailed;
            _current = next;
            return StateUpdate.Saved;
        }
    }

    /// <summary>True when a state folder is there but its file was refused (as opposed to no trusted folder at all).</summary>
    public bool IsCorrupt
    {
        get { lock (_lock) return _current is null && _file is not null; }
    }

    /// <summary>
    /// What can still be read from a refused file, without trusting it: account SIDs and service names that pass the same checks as
    /// a valid state. Used only to release what may still be set before <see cref="Reset"/>; empty when nothing can be read.
    /// </summary>
    public (IReadOnlyList<string> Sids, IReadOnlyList<string> Services) Salvage()
    {
        var sids = new List<string>();
        var services = new List<string>();
        lock (_lock)
        {
            if (_file is null) return (sids, services);
            try
            {
                var info = new FileInfo(_file);
                if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.Length > MaxFileBytes) return (sids, services);
                using var document = JsonDocument.Parse(File.ReadAllBytes(_file), new JsonDocumentOptions { MaxDepth = 8 });
                if (document.RootElement.ValueKind != JsonValueKind.Object) return (sids, services);
                if (document.RootElement.TryGetProperty("accounts", out var accounts) && accounts.ValueKind == JsonValueKind.Object)
                {
                    sids.AddRange(accounts.EnumerateObject().Select(p => p.Name).Where(IsUserSid).Take(MaxAccounts));
                }
                if (document.RootElement.TryGetProperty("paused", out var paused) && paused.ValueKind == JsonValueKind.Object)
                {
                    services.AddRange(paused.EnumerateObject().Select(p => p.Name).Where(n => ServicePolicy.IsAllowed(n, _serviceOnList)).Take(RequestValidator.MaxServiceNames));
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Nothing readable: nothing to salvage.
            }
        }
        return (sids.Distinct(StringComparer.Ordinal).ToList(), services.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>
    /// Starts over with an empty state. A refused file is renamed to <c>state.corrupt-&lt;UTC time&gt;.json</c> next to it (kept for
    /// diagnosis, never read again); a valid one is replaced, but only when <paramref name="mayReset"/> says so for the state as it
    /// is at that moment (check and swap under one lock: nothing can be noted in between and then forgotten).
    /// </summary>
    public ResetOutcome Reset(DateTimeOffset now, Func<StateData, bool>? mayReset = null)
    {
        lock (_lock)
        {
            if (_file is null) return ResetOutcome.Failed;
            if (_current is not null && mayReset is not null && !mayReset(_current)) return ResetOutcome.Refused;
            var kept = false;
            try
            {
                if (_current is null && (File.Exists(_file) || Directory.Exists(_file)))
                {
                    var folder = Path.GetDirectoryName(_file)!;
                    var target = Path.Combine(folder, $"state.corrupt-{now.UtcDateTime:yyyyMMdd-HHmmss-fff}.json");
                    if (Directory.Exists(_file)) Directory.Move(_file, target);
                    else File.Move(_file, target, overwrite: false);
                    kept = true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Error($"[state] the damaged state file could not be set aside: {ex.GetType().Name}");
                return ResetOutcome.Failed;
            }
            var fresh = new StateData { BootTimeMs = _bootTimeMs() };
            if (!Write(_file, fresh)) return ResetOutcome.Failed;
            _current = fresh;
            _log.Info(kept ? "[state] the damaged state file was set aside, the service starts with an empty state" : "[state] the state was reset");
            return kept ? ResetOutcome.CorruptKept : ResetOutcome.Fresh;
        }
    }

    /// <summary>
    /// The state as it is now, without a copy: for the state checks, which must not do more than read cached values. The object is
    /// never changed after it was published (<see cref="Update"/> works on a copy); callers must not change it either.
    /// </summary>
    public StateData? Current
    {
        get { lock (_lock) return _current; }
    }

    /// <summary>After a restart Windows started the services again by their start type: the pauses of before mean nothing now.</summary>
    private void ForgetPausesOfEarlierBoot()
    {
        var state = Snapshot();
        if (state is null || state.Paused.Count == 0) return;
        if (Math.Abs(state.BootTimeMs - _bootTimeMs()) <= BootTolerance.TotalMilliseconds) return;
        if (Update(s =>
            {
                s.Paused.Clear();
                return true;
            }) == StateUpdate.Saved)
        {
            _log.Info("[state] the machine restarted since services were paused: those pauses are forgotten");
        }
    }

    private static StateData? Load(string file, IEventSink log, Func<string, bool> serviceOnList)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists)
            {
                if (info.Attributes != (FileAttributes)(-1)) return Corrupt(log, "not a file");
                return new StateData();
            }
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return Corrupt(log, "reparse point");
            if (info.Length > MaxFileBytes) return Corrupt(log, "too large");
            var bytes = File.ReadAllBytes(file);
            if (bytes.Length > MaxFileBytes) return Corrupt(log, "too large");
            var state = JsonSerializer.Deserialize(bytes, StateJsonContext.Default.StateData);
            if (state is null) return Corrupt(log, "empty");
            // The dictionaries come back with the default comparer; the state compares SIDs exactly and service names without case.
            state.Accounts = new Dictionary<string, AccountState>(state.Accounts ?? [], StringComparer.Ordinal);
            if (state.Paused is null) return Corrupt(log, "paused missing");
            var paused = new Dictionary<string, PausedService>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, entry) in state.Paused)
            {
                if (!paused.TryAdd(name, entry)) return Corrupt(log, "service twice");
            }
            state.Paused = paused;
            if (Validate(state, serviceOnList) is { } problem) return Corrupt(log, problem);
            return state;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return Corrupt(log, ex.GetType().Name);
        }
    }

    private static StateData? Corrupt(IEventSink log, string problem)
    {
        log.Error($"[state] the state file is not valid ({problem}): operations that need the service's state are refused until an administrator removes it");
        return null;
    }

    /// <summary>Why the state is not acceptable, or null.</summary>
    internal static string? Validate(StateData state, Func<string, bool> serviceOnList)
    {
        if (state.Version != StateData.CurrentVersion) return "version";
        if (state.Accounts is null || state.Paused is null) return "missing parts";
        if (state.Accounts.Count > MaxAccounts) return "too many accounts";
        foreach (var (sid, account) in state.Accounts)
        {
            if (!IsUserSid(sid)) return "account sid";
            if (account?.Groups is null || account.CreatedKeys is null) return "account parts";
            if (account.Groups.Any(g => g is null || !RequestValidator.PolicyGroupIds.Contains(g)) || account.Groups.Distinct(StringComparer.Ordinal).Count() != account.Groups.Count) return "groups";
            if (account.CreatedKeys.Any(k => k is null || !PolicyGroups.CleanupCandidates.Contains(k, StringComparer.Ordinal))
                || account.CreatedKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != account.CreatedKeys.Count) return "keys";
            if (account.LegacyLock && account.Groups.Count == 0) return "legacy lock without groups";
        }
        foreach (var (name, entry) in state.Paused)
        {
            if (!ServicePolicy.IsAllowed(name, serviceOnList)) return "service";
            if (entry?.Holders is null || entry.Holders.Count is 0 or > MaxHolders) return "holders";
            if (entry.Holders.Any(h => !IsUserSid(h)) || entry.Holders.Distinct(StringComparer.Ordinal).Count() != entry.Holders.Count) return "holder sid";
        }
        if (state.AutoLogonOwner is not null && !IsUserSid(state.AutoLogonOwner)) return "autologon owner";
        if (state.FirewallPorts is { } ports
            && (ports.Count is 0 or > RequestValidator.MaxPorts || ports.Any(p => p is < RequestValidator.MinPort or > 65535) || ports.Distinct().Count() != ports.Count)) return "ports";
        return null;
    }

    public static bool IsUserSid(string? sid) => sid is { Length: <= 80 } && UserSid.IsMatch(sid) && PolicyGroups.IsUserSid(sid);

    private bool Write(string file, StateData state)
    {
        var temp = file + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state, StateJsonContext.Default.StateData);
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
            _log.Error($"[state] not saved: {ex.GetType().Name}");
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

    private static StateData Clone(StateData state)
    {
        var copy = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(state, StateJsonContext.Default.StateData), StateJsonContext.Default.StateData)!;
        copy.Accounts = new Dictionary<string, AccountState>(copy.Accounts, StringComparer.Ordinal);
        copy.Paused = new Dictionary<string, PausedService>(copy.Paused, StringComparer.OrdinalIgnoreCase);
        return copy;
    }
}
