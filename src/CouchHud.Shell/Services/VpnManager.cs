using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.Json;
using CouchHud.Core.Config;
using CouchHud.Core.Integrations.Vpn;
using CouchHud.Core.Localization;

namespace CouchHud.Shell.Services;

/// <summary>
/// The VPN on the couch: the installed VPN app (NordVPN first) driven through its own command line, the state read off the tunnel adapter.
/// No VPN client, no credentials, no request to anyone (not even to look up the public address). The app's exe is run only from an
/// administrator-written place below Program Files (<see cref="TrustedCliRunner"/>), one command at a time, with a time limit.
/// The state follows Windows' network change event (no polling while nothing happens); only while a connect or disconnect is pending the
/// adapters are looked at once a second, for at most a minute. Nothing here blocks the UI thread: every look and every run is awaited.
/// </summary>
public sealed class VpnManager : IDisposable
{
    public static readonly TimeSpan CliTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ResolveCache = TimeSpan.FromSeconds(30);
    private const int MaxWatchSeconds = 60;

    private readonly ConfigStore _config;
    private readonly IVpnProvider _provider;
    private readonly Action _requireChange;
    private readonly Action<string, object?> _emit;
    private readonly VpnStateMachine _machine;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _cli = new(1, 1);
    /// <summary>Process ids of our own command lines that still run without a window: they are not the user's app.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, byte> _ownCli = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly System.Threading.Timer _debounce;
    private readonly int _session;
    private (string? Exe, bool Untrusted, long At)? _resolved;
    private string? _lastJson;
    private string? _startFailed;
    private int _watching;
    private bool _disposed;

    public VpnManager(ConfigStore config, IVpnProvider provider, Action requireChange, Action<string, object?> emit)
    {
        _config = config;
        _provider = provider;
        _requireChange = requireChange;
        _emit = emit;
        _machine = new VpnStateMachine(Now);
        using (var self = Process.GetCurrentProcess()) _session = self.SessionId;
        _debounce = new System.Threading.Timer(_ => _ = RefreshAsync(), null, Timeout.Infinite, Timeout.Infinite);
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    /// <summary>A monotonic clock (a changed system time must not end or stretch a pending request).</summary>
    private static DateTimeOffset Now() => DateTimeOffset.UnixEpoch + TimeSpan.FromMilliseconds(Environment.TickCount64);

    private void OnNetworkChanged(object? sender, EventArgs e) => Debounce();
    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => Debounce();

    /// <summary>Windows sends the change event in bursts: one look half a second after the last one.</summary>
    private void Debounce()
    {
        try { _debounce.Change(500, Timeout.Infinite); }
        catch (ObjectDisposedException) { }
    }

    public async Task<object?> HandleAsync(string method, JsonElement args)
    {
        if (!VpnPolicy.All.Contains(method)) throw new InvalidOperationException($"unknown method {method}");
        JsonElement Arg(int i) => args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > i ? args[i] : default;
        switch (method)
        {
            case "vpnStatus":
                return await Task.Run(() => Snapshot(emit: false));
            case "vpnCountries":
                return _provider.Countries;
            case "vpnConnect":
            {
                // A target that is no target is refused first (it must not use up the parents' one-time release); then, before anything
                // is awaited (on the bridge's thread), the lock and the parents' release.
                var target = ParseTarget(Arg(0));
                _requireChange();
                return await ConnectAsync(target);
            }
            case "vpnDisconnect":
                _requireChange();
                return await DisconnectAsync();
            case "vpnOpenApp":
                _requireChange();
                return await Task.Run(OpenApp);
            default:
                throw new InvalidOperationException($"unknown method {method}");
        }
    }

    /// <summary>{ kind: "quick" } | { kind: "country", country: "de" } | { kind: "server", server: "Germany #812" }; checked by the provider.</summary>
    private VpnTarget ParseTarget(JsonElement value)
    {
        string? Prop(string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        var target = Prop("kind") switch
        {
            "quick" => VpnTarget.Quick,
            "country" => new VpnTarget(VpnTargetKind.Country, Country: Prop("country")),
            "server" => new VpnTarget(VpnTargetKind.Server, Server: Prop("server")),
            _ => throw new ArgumentException(Strings.Get("vpn.badTarget"))
        };
        try { return _provider.Normalize(target); }
        catch (ArgumentException) { throw new ArgumentException(Strings.Get("vpn.badTarget")); }
    }

    private async Task<VpnStatus> ConnectAsync(VpnTarget target)
    {
        var arguments = _provider.ConnectArguments(target);
        return await RunAsync(arguments, up =>
        {
            _machine.RequestConnect(target, up);
            if (_provider.CountryOf(target) is { } country) _config.Update(c => c.VpnLastCountry = country);
        }, $"connect ({target.Kind.ToString().ToLowerInvariant()}{(target.Country is { } c ? " " + c : "")})");
    }

    private Task<VpnStatus> DisconnectAsync() => RunAsync(_provider.DisconnectArguments(), up => _machine.RequestDisconnect(up), "disconnect");

    /// <summary>
    /// One command at a time. The request is noted first (the page shows "connecting" at once), the program runs in the background and
    /// the adapters are watched until the request is settled; the call itself returns right away.
    /// </summary>
    private async Task<VpnStatus> RunAsync(IReadOnlyList<string> arguments, Action<bool> request, string what)
    {
        if (!await _cli.WaitAsync(0)) throw new InvalidOperationException(Strings.Get("vpn.busy"));
        var started = false;
        try
        {
            var (exe, untrusted) = await Task.Run(Resolve);
            if (exe is null) throw new InvalidOperationException(Strings.Get(untrusted ? "vpn.untrusted" : "vpn.notInstalled", _provider.Name));
            var (up, appRunning) = await Task.Run(() => (TunnelUp(), AppRunning()));
            lock (_lock)
            {
                _startFailed = null;
                request(up);
            }
            started = true;
            // The next command may run as soon as this one ended, or as soon as it became the app itself (no instance was running: then
            // the started process is the app and never ends). "Became the app" means: it has a window of its own. Until then it is our
            // command line, not the user's app: it does not count as a running app (for the kill rule of a next command either).
            var released = 0;
            var pid = 0;
            void Release()
            {
                if (Interlocked.Exchange(ref released, 1) == 0) _cli.Release();
            }
            _ = Task.Run(async () =>
            {
                try
                {
                    var (exit, _) = await TrustedCliRunner.RunAsync(exe, arguments, CliTimeout, _stop.Token, readStderr: false,
                        killOnTimeout: () => _provider.KillCliOnTimeout(appRunning),
                        started: id =>
                        {
                            pid = id;
                            _ownCli.TryAdd(id, 0);
                        },
                        onWindow: () =>
                        {
                            _ownCli.TryRemove(pid, out _);
                            Release();
                        });
                    // The exit code is not documented; it only goes to the log. The tunnel adapter says whether it worked.
                    Console.WriteLine($"[vpn] {_provider.Id} {what}: {(exit is null ? "still running after the time limit" : exit == -1 ? "could not be started" : "exit " + exit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))}");
                    if (exit == -1)
                    {
                        lock (_lock)
                        {
                            _machine.Abort();
                            _startFailed = "startFailed";
                        }
                    }
                }
                catch (OperationCanceledException) { }
                finally
                {
                    // Ended, or still running after the time limit without a window: no longer counted as ours either way.
                    if (pid != 0) _ownCli.TryRemove(pid, out _);
                    Release();
                }
                await RefreshAsync();
            });
            StartWatch();
            return Snapshot(emit: true);
        }
        finally
        {
            if (!started) _cli.Release();
        }
    }

    /// <summary>While a request is pending: a look once a second (bounded), so "connecting" turns into a result without the page asking.</summary>
    private void StartWatch()
    {
        if (Interlocked.Exchange(ref _watching, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < MaxWatchSeconds && !_stop.IsCancellationRequested; i++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), _stop.Token);
                    Snapshot(emit: true);
                    bool busy;
                    lock (_lock) busy = _machine.Busy;
                    if (!busy) break;
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                Interlocked.Exchange(ref _watching, 0);
            }
            // A request noted between the last look and the reset above found the watch still running and did not start one: look again.
            bool pending;
            lock (_lock) pending = _machine.Busy;
            if (pending && !_stop.IsCancellationRequested) StartWatch();
        });
    }

    private object OpenApp()
    {
        var (exe, untrusted) = Resolve();
        if (exe is null) throw new InvalidOperationException(Strings.Get(untrusted ? "vpn.untrusted" : "vpn.notInstalled", _provider.Name));
        if (!TrustedCliRunner.StartApp(exe)) throw new InvalidOperationException(Strings.Get("vpn.openFailed", _provider.Name));
        Console.WriteLine($"[vpn] {_provider.Id}: app opened");
        return new { opened = true };
    }

    private Task RefreshAsync() => Task.Run(() =>
    {
        try { Snapshot(emit: true); }
        catch (Exception ex) { Console.Error.WriteLine($"[vpn] status: {ex.GetType().Name}"); }
    });

    /// <summary>The current state (thread pool: the adapters, the processes and the file checks take a moment); emitted when it changed.</summary>
    private VpnStatus Snapshot(bool emit)
    {
        if (_disposed) return Status(VpnState.NotInstalled, false, null, null);
        var (exe, untrusted) = Resolve();
        var installed = exe is not null;
        var adapters = installed ? Adapters() : [];
        var tunnel = installed ? _provider.TunnelKind(adapters) : null;
        var appRunning = installed && AppRunning();
        VpnStatus status;
        lock (_lock)
        {
            var state = _machine.Observe(installed, tunnel is not null);
            var hint = !installed && untrusted ? "untrusted" : _machine.Hint ?? _startFailed;
            status = Status(state, appRunning, hint, tunnel);
        }
        if (emit)
        {
            var json = JsonSerializer.Serialize(status, UiHost.UiBridge.Json);
            if (json != Interlocked.Exchange(ref _lastJson, json)) _emit("vpn", status);
        }
        else
        {
            Interlocked.Exchange(ref _lastJson, JsonSerializer.Serialize(status, UiHost.UiBridge.Json));
        }
        return status;
    }

    private VpnStatus Status(VpnState state, bool appRunning, string? hint, string? tunnel)
    {
        var target = _machine.Target;
        var last = _config.Current.VpnLastCountry;
        if (last is not null && !_provider.Countries.Any(c => c.Id == last)) last = null;
        return new VpnStatus(_provider.Id, _provider.Name, state, state != VpnState.NotInstalled, appRunning, hint,
            target?.Kind, target?.Country, target?.Server, target is not null && _machine.TargetConfirmed, tunnel, last);
    }

    /// <summary>The app's exe as it may be run (cached for 30 s), and whether one was found that may not be run (outside Program Files).</summary>
    private (string? Exe, bool Untrusted) Resolve()
    {
        var now = Environment.TickCount64;
        lock (_lock)
        {
            if (_resolved is { } r && now - r.At < ResolveCache.TotalMilliseconds) return (r.Exe, r.Untrusted);
        }
        string? trusted = null;
        var untrusted = false;
        foreach (var candidate in _provider.ExeCandidates(new WindowsRegistry(), TrustedCliRunner.TrustedRoots()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (TrustedCliRunner.Verify(candidate, _provider.ExeName, "vpn", logMissing: false) is { } exe)
            {
                trusted = exe;
                break;
            }
            try { untrusted |= File.Exists(candidate); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        }
        lock (_lock) _resolved = (trusted, trusted is null && untrusted, now);
        return (trusted, trusted is null && untrusted);
    }

    private static IReadOnlyList<VpnAdapter> Adapters()
    {
        try
        {
            var internet = InternetInterfaces();
            return NetworkInterface.GetAllNetworkInterfaces()
                .Select(n => new VpnAdapter(n.Name, n.Description, n.OperationalStatus == OperationalStatus.Up,
                    internet.Count == 0 ? null : Ipv4Index(n) is { } index && internet.Contains(index)))
                .ToList();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }

    private static int? Ipv4Index(NetworkInterface adapter)
    {
        try { return adapter.GetIPProperties().GetIPv4Properties()?.Index; }
        catch (Exception ex) when (ex is NetworkInformationException or NotSupportedException or InvalidOperationException) { return null; }
    }

    /// <summary>
    /// Two addresses, one in each half of IPv4 (a VPN may route 0.0.0.0/1 and 128.0.0.0/1 instead of a default route). Only Windows'
    /// routing table is asked which interface it would use; no packet is sent to them.
    /// </summary>
    private static readonly uint[] InternetProbes = [ProbeAddress(1, 0, 0, 1), ProbeAddress(192, 0, 2, 1)];

    private static uint ProbeAddress(byte a, byte b, byte c, byte d) => BitConverter.ToUInt32([a, b, c, d]);

    /// <summary>The interfaces Windows routes the internet through (by IPv4 index); empty when that could not be looked up.</summary>
    private static HashSet<int> InternetInterfaces()
    {
        var result = new HashSet<int>();
        foreach (var address in InternetProbes)
        {
            try
            {
                if (GetBestInterface(address, out var index) == 0) result.Add((int)index);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        }
        return result;
    }

    /// <summary>iphlpapi: the interface of the best route to an IPv4 address (network byte order); 0 = found. A table lookup only.</summary>
    [System.Runtime.InteropServices.DllImport("iphlpapi.dll")]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

    private bool TunnelUp() => _provider.TunnelKind(Adapters()) is not null;

    /// <summary>
    /// The app runs in this Windows session (by process name; only shown, and used for the time-limit rule of the command line). Our own
    /// command line that still runs without a window has the same name but is not the app.
    /// </summary>
    private bool AppRunning()
    {
        var processes = Process.GetProcessesByName(_provider.ProcessName);
        try
        {
            return processes.Any(p =>
            {
                try { return p.SessionId == _session && !_ownCli.ContainsKey(p.Id); }
                catch (InvalidOperationException) { return false; }
            });
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _stop.Cancel();
        _debounce.Dispose();
    }
}
