using System.ServiceProcess;
using CouchHud.Core.Net;
using CouchHud.Core.Platform;
using CouchHud.Core.Security;
using CouchHud.Privileged;
using CouchHud.Service.Authorization;
using CouchHud.Service.Contracts;
using CouchHud.Service.Dispatch;
using CouchHud.Service.State;

namespace CouchHud.Service.Tests;

/// <summary>An in-memory registry (one hive), like the one of the core tests: values typed, keys that exist without values.</summary>
internal sealed class MemoryRegistry : IRegistry
{
    public Dictionary<string, Dictionary<string, object>> Data { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int Writes { get; private set; }

    public bool ThrowOnWrite { get; set; }

    public void CreateKey(string key)
    {
        if (!Data.ContainsKey(key)) Data[key] = new(StringComparer.OrdinalIgnoreCase);
    }

    public string? ReadString(string key, string name) => Data.TryGetValue(key, out var v) && v.TryGetValue(name, out var o) ? o as string : null;

    public int? ReadDword(string key, string name) => Data.TryGetValue(key, out var v) && v.TryGetValue(name, out var o) && o is int i ? i : null;

    public byte[]? ReadBinary(string key, string name) => null;

    public IReadOnlyDictionary<string, string> ReadValues(string key) =>
        Data.TryGetValue(key, out var v)
            ? v.Where(p => p.Value is string).ToDictionary(p => p.Key, p => (string)p.Value, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>();

    public RegistryValueType GetValueType(string key, string name) =>
        !Data.TryGetValue(key, out var v) || !v.TryGetValue(name, out var o) ? RegistryValueType.Missing
        : o switch { string => RegistryValueType.String, int => RegistryValueType.Dword, _ => RegistryValueType.Other };

    public IReadOnlyList<string> SubKeys(string key) =>
        Data.Keys.Where(k => k.StartsWith(key + @"\", StringComparison.OrdinalIgnoreCase))
            .Select(k => k[(key.Length + 1)..].Split('\\')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public void WriteString(string key, string name, string value) => Set(key, name, value);

    public void WriteDword(string key, string name, int value) => Set(key, name, value);

    private void Set(string key, string name, object value)
    {
        if (ThrowOnWrite) throw new UnauthorizedAccessException();
        Writes++;
        // Like the real registry: the parents exist too.
        var parent = key;
        while (parent.LastIndexOf('\\') is var i and > 0)
        {
            parent = parent[..i];
            if (parent.Contains('\\')) CreateKey(parent);
        }
        CreateKey(key);
        Data[key][name] = value;
    }

    public void DeleteValue(string key, string name)
    {
        if (Data.TryGetValue(key, out var v)) v.Remove(name);
    }

    public bool KeyExists(string key) => Data.ContainsKey(key) || SubKeys(key).Count > 0;

    public void DeleteKeyIfEmpty(string key)
    {
        if (Data.TryGetValue(key, out var v) && v.Count == 0 && SubKeys(key).Count == 0) Data.Remove(key);
    }

    public string Dump() => string.Join("\n", Data.OrderBy(k => k.Key).Select(k => k.Key + ": " + string.Join(",", k.Value.Select(p => $"{p.Key}={p.Value}"))));
}

internal sealed class FakeHives : IUserHives
{
    public Dictionary<string, MemoryRegistry> Loaded { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Hives of accounts that are not signed in, which only <see cref="OpenOrLoad"/> may load.</summary>
    public Dictionary<string, MemoryRegistry> OnDisk { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Opened { get; } = [];

    public List<string> LoadedFromDisk { get; } = [];

    public MemoryRegistry For(string sid)
    {
        if (!Loaded.TryGetValue(sid, out var registry)) Loaded[sid] = registry = new MemoryRegistry();
        return registry;
    }

    /// <summary>Accounts whose hive throws when opened (a failure the sweep must get past, review N-3).</summary>
    public HashSet<string> ThrowOpen { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IUserHive? OpenLoaded(string sid)
    {
        if (ThrowOpen.Contains(sid)) throw new IOException("hive failed");
        Opened.Add(sid);
        return Loaded.TryGetValue(sid, out var registry) ? new Hive(registry) : null;
    }

    /// <summary>When set, loading a hive from disk waits for it (a slow load).</summary>
    public ManualResetEventSlim? LoadGate { get; set; }

    public IUserHive? OpenOrLoad(string sid)
    {
        if (OpenLoaded(sid) is { } hive) return hive;
        LoadGate?.Wait(TimeSpan.FromSeconds(30));
        if (!OnDisk.TryGetValue(sid, out var registry)) return null;
        LoadedFromDisk.Add(sid);
        return new Hive(registry);
    }

    private sealed class Hive(IRegistry registry) : IUserHive
    {
        public IRegistry Registry { get; } = registry;

        public void Dispose()
        {
        }
    }
}

internal sealed class FakeScm : IScm
{
    public HashSet<string> Running { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ServiceStartMode> StartTypes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> FailStop { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> FailStart { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Calls { get; } = [];

    public ServiceStartMode GetStartType(string name) => StartTypes.GetValueOrDefault(name, ServiceStartMode.Automatic);

    public bool? IsRunning(string name) => Running.Contains(name);

    public bool Stop(string name, TimeSpan timeout)
    {
        Calls.Add("stop " + name);
        if (FailStop.Contains(name)) return false;
        Running.Remove(name);
        return true;
    }

    public bool Start(string name, TimeSpan timeout)
    {
        Calls.Add("start " + name);
        if (FailStart.Contains(name)) return false;
        Running.Add(name);
        return true;
    }
}

internal sealed class FakeFirewall : IFirewall
{
    public List<FirewallRuleInfo>? RuleList { get; set; } = [];
    public List<string[]> Calls { get; } = [];
    public int ExitCode { get; set; }

    public IReadOnlyList<FirewallRuleInfo>? Rules() => RuleList?.ToList();

    public int Netsh(IReadOnlyList<string> arguments)
    {
        Calls.Add([.. arguments]);
        if (ExitCode != 0) return ExitCode;
        if (RuleList is null) return 0;
        var name = arguments.FirstOrDefault(a => a.StartsWith("name=", StringComparison.Ordinal))?[5..];
        switch (arguments[2])
        {
            case "add":
                RuleList.Add(new FirewallRuleInfo(name!, true, true, true, Arg(arguments, "program"), Arg(arguments, "localport"), 6, "LocalSubnet"));
                break;
            case "set":
                RuleList.RemoveAll(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                RuleList.Add(new FirewallRuleInfo(name!, true, true, true, Arg(arguments, "program"), Arg(arguments, "localport"), 6, "LocalSubnet"));
                break;
            case "delete":
                var port = Arg(arguments, "localport");
                RuleList.RemoveAll(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && (port is null || (r.LocalPorts == port && r.Allow)));
                break;
        }
        return 0;
    }

    private static string? Arg(IReadOnlyList<string> arguments, string key) =>
        arguments.FirstOrDefault(a => a.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..];
}

internal sealed class FakeAutoLogon : IAutoLogon
{
    public bool Enabled { get; set; }
    public string User { get; set; } = "";
    public string Domain { get; set; } = "";
    public string? Marker { get; set; }
    public int TurnOnResult { get; set; }
    public int TurnOffResult { get; set; }
    public List<string> Calls { get; } = [];
    public char[]? LastPassword { get; private set; }

    public AutoLogonInfo Read() => new(Enabled, User, Domain);

    public string? SetUpBy() => Marker;

    public Exception? ThrowOnTurnOn { get; set; }

    public int TurnOn(char[] password, string user, string domain)
    {
        Calls.Add($"on {domain}\\{user} pw={new string(password)}");
        LastPassword = password;
        Array.Clear(password);
        if (ThrowOnTurnOn is { } ex) throw ex;
        if (TurnOnResult != 0) return TurnOnResult;
        Enabled = true;
        (User, Domain, Marker) = (user, domain, $@"{domain}\{user}");
        return 0;
    }

    public int TurnOff()
    {
        Calls.Add("off");
        if (TurnOffResult != 0) return TurnOffResult;
        Enabled = false;
        Marker = null;
        return 0;
    }

    public void AllowPasswordSignIn() => Calls.Add("allow");
}

internal sealed class FakeAccounts : IAccountLookup
{
    public Dictionary<string, LogonAccount> Known { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [Callers.UserSid] = new LogonAccount("kid", "COUCHPC"),
        [Callers.OtherSid] = new LogonAccount("other", "COUCHPC"),
    };

    public LogonAccount? Lookup(string sid) => Known.TryGetValue(sid, out var account) ? account : null;

    public string? SidOf(string account) =>
        Known.FirstOrDefault(k => $@"{k.Value.Domain}\{k.Value.User}".Equals(account, StringComparison.OrdinalIgnoreCase)).Key;
}

internal sealed class FakeSessions : ISessions
{
    /// <summary>Session id → account SID.</summary>
    public Dictionary<int, string> Sessions { get; } = [];

    public bool Unreadable { get; set; }

    public IReadOnlySet<string>? SignedInSids(int? leaving) =>
        Unreadable ? null : Sessions.Where(s => s.Key != leaving).Select(s => s.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
}

internal sealed class FakeLogon : ILogonVerifier
{
    public string Password { get; set; } = "right-password";

    /// <summary>The SID the logon token belongs to; null: the account's own (from <see cref="FakeAccounts"/>).</summary>
    public string? TokenSid { get; set; }

    public List<string> Calls { get; } = [];

    public LogonResult Verify(string user, string? domain, string password)
    {
        Calls.Add($@"{domain}\{user}");
        if (password != Password) return new LogonResult(LogonErrors.LogonFailure, null);
        var sid = TokenSid ?? new FakeAccounts().Known.First(k => k.Value.User == user).Key;
        return new LogonResult(0, sid);
    }
}

internal sealed class FakeTasks : ITaskScheduler
{
    public List<string> Tasks { get; } = [];
    public List<string> Deleted { get; } = [];
    public HashSet<string> FailDelete { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int Run(string taskName) => Tasks.Contains(taskName) ? 0 : 1;

    public int Query(string taskName) => Tasks.Contains(taskName) ? 0 : 1;

    public int Delete(string taskName)
    {
        if (FailDelete.Contains(taskName)) return 1;
        Deleted.Add(taskName);
        return Tasks.Remove(taskName) ? 0 : 1;
    }

    public int Create(string taskName, string xmlFile)
    {
        Tasks.Add(taskName);
        return 0;
    }

    /// <summary>The Task Scheduler cannot be asked (List throws).</summary>
    public bool ThrowList { get; set; }

    public IReadOnlyList<string> List() => ThrowList ? throw new InvalidOperationException("no task scheduler") : Tasks.ToList();

    public bool EnsureStartRight(string taskName, string sid) => true;

    /// <summary>Tasks restricted to SYSTEM and administrators (what <see cref="RestrictToAdministrators"/> did).</summary>
    public HashSet<string> Restricted { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> FailRestrict { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ThrowRestrict { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool RestrictToAdministrators(string taskName)
    {
        if (ThrowRestrict.Contains(taskName)) throw new InvalidOperationException("COM failed");
        if (FailRestrict.Contains(taskName) || !Tasks.Contains(taskName)) return false;
        Restricted.Add(taskName);
        return true;
    }

    /// <summary>Tasks set to SYSTEM and administrators plus their user's start right (what <see cref="RestrictToUserStart"/> did), with that user.</summary>
    public Dictionary<string, string> UserStartOnly { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool RestrictToUserStart(string taskName, string sid)
    {
        if (ThrowRestrict.Contains(taskName)) throw new InvalidOperationException("COM failed");
        if (FailRestrict.Contains(taskName) || !Tasks.Contains(taskName)) return false;
        UserStartOnly[taskName] = sid;
        return true;
    }
}

/// <summary>A temporary folder that goes away with the test.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "couchhud-p5-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>The P5 operations wired into the dispatcher harness, with fakes for everything Windows.</summary>
internal sealed class OpsHarness : IDisposable
{
    public static readonly string[] OnList = ["WSearch", "SysMain", "Spooler", "WinDefend"];

    public TempDir Temp { get; } = new();
    public string StateFolder { get; }
    public string AppDir { get; }
    public CapturingSink OpsLog { get; } = new();
    public FakeHives Hives { get; } = new();
    public FakeScm Scm { get; } = new();
    public FakeFirewall Firewall { get; } = new();
    public FakeAutoLogon AutoLogon { get; } = new();
    public FakeAccounts Accounts { get; } = new();
    public FakeLogon Logon { get; } = new();
    public FakeTasks Tasks { get; } = new();
    public long BootTime { get; set; } = 1_000_000;
    public DateTimeOffset Now { get; set; } = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
    public ServiceStateStore Store { get; private set; } = null!;
    public OperationsA Operations { get; private set; } = null!;
    public Harness Harness { get; private set; } = null!;

    public OpsHarness(bool codeEnrolled = false, IGrantStoreFactory? grants = null, bool stateFolder = true, Action<string>? prepareState = null)
    {
        StateFolder = System.IO.Path.Combine(Temp.Path, "state");
        AppDir = System.IO.Path.Combine(Temp.Path, "app");
        Directory.CreateDirectory(StateFolder);
        Directory.CreateDirectory(AppDir);
        prepareState?.Invoke(StateFolder);
        Hives.For(Callers.UserSid);
        Hives.For(Callers.OtherSid);
        Build(codeEnrolled, grants, stateFolder);
    }

    public static bool OnServiceList(string name) => OnList.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>A new service instance on the same state folder (a restart of the service).</summary>
    public void Restart(bool codeEnrolled = false) => Build(codeEnrolled, null, true);

    private void Build(bool codeEnrolled, IGrantStoreFactory? grants, bool stateFolder)
    {
        Store = ServiceStateStore.Open(stateFolder ? StateFolder : null, OpsLog, () => BootTime, OnServiceList);
        var tasks = Tasks;
        Operations = new OperationsA(new OperationsAParts(Store, Hives, Scm, Firewall, AutoLogon, Accounts, Logon, tasks,
            new PolicyInstaller(tasks, OpsLog, AppDir), AppDir, OpsLog, null, OnServiceList, () => Now, TimeSpan.FromSeconds(1), Sessions));
        Operations.Start();
        // As the service: the account's lock view knows a lock taken over from 0.4.0 (CodeHandlers.View).
        Harness = new Harness(grants: grants?.Create(), codeEnrolled: codeEnrolled, onServiceList: OnServiceList,
            extraHandlers: Operations.AddHandlers, extraChecks: Operations.AddChecks,
            lockViews: sid => LockView.Closed with { Legacy = Operations.IsLegacyLocked(sid) });
        Harness.State.MigrationLookup = Operations.MigrationOf;
        Harness.State.ReleaseLookup = () => Operations.ReleaseStatus;
    }

    public FakeSessions Sessions { get; } = new();


    public Task<Response> Call(CallerInfo caller, Operation op, object? args = null, string? grant = null)
    {
        var json = args switch
        {
            null => ProtocolJson.SerializeRequest(op, grant),
            ApplyLockPoliciesArgs a => ProtocolJson.SerializeRequest(op, a, grant),
            ReleaseLockPoliciesArgs a => ProtocolJson.SerializeRequest(op, a, grant),
            ServicesArgs a => ProtocolJson.SerializeRequest(op, a, grant),
            SetAutoLogonArgs a => ProtocolJson.SerializeRequest(op, a, grant),
            SetRemoteFirewallArgs a => ProtocolJson.SerializeRequest(op, a, grant),
            _ => throw new ArgumentException("args")
        };
        return Harness.OneAsync(caller, json);
    }

    public async Task<Response<T>> Call<T>(CallerInfo caller, Operation op, object? args = null, string? grant = null)
    {
        var json = args switch
        {
            null => ProtocolJson.SerializeRequest(op, grant),
            ApplyLockPoliciesArgs a => ProtocolJson.SerializeRequest(op, a, grant),
            ReleaseLockPoliciesArgs a => ProtocolJson.SerializeRequest(op, a, grant),
            ServicesArgs a => ProtocolJson.SerializeRequest(op, a, grant),
            _ => throw new ArgumentException("args")
        };
        var answers = await Harness.ExchangeAsync(caller, FrameCodec.Encode(json));
        return ProtocolJson.DeserializeResponse<T>(Assert.Single(answers))!;
    }

    public StateData State => Store.Snapshot()!;

    public void Dispose() => Temp.Dispose();
}

internal interface IGrantStoreFactory
{
    Authorization.IGrantStore Create();
}

internal sealed class ValidGrant(string grant, GrantKinds kind = GrantKinds.Couch) : IGrantStoreFactory
{
    public Authorization.IGrantStore Create() => new FakeGrants(grant, kind);
}
