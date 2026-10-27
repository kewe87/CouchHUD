using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CouchHud.Core.Config;
using CouchHud.Core.Integrations.CaseHud;
using CouchHud.Core.Localization;
using CouchHud.Core.Plugins;
using CouchHud.Core.Updates;
using Microsoft.Win32.SafeHandles;

namespace CouchHud.Shell.Services;

/// <summary>
/// CaseHUD (the LCD tool for fans and pumps) next to CouchHUD: finds it, starts it, installs and updates it
/// and relays the bridge calls of the CaseHUD page to its local API v1. CouchHUD contains none of CaseHUD's code.
/// The event stream and the gaming lease run only while there is something to tell; without CaseHUD this is
/// a file check now and then.
/// Since CaseHUD's API can be switched off (Settings › Interfaces, off by default) the manager tells four states apart
/// (<see cref="CaseHudApiState"/>): it watches %APPDATA%\casehud (api.json, instance.pid) instead of polling, reads port and token again
/// on every change, ends the event stream when api.json goes and names the place to switch the API on instead of a connection error.
/// </summary>
public sealed class CaseHudManager : IDisposable
{
    private readonly ConfigStore _config;
    private readonly string _dataDir;
    private readonly Func<bool> _gameRunning;
    private readonly Action<string, object?> _emit;
    private readonly HttpClient _http;
    private readonly HttpClient _github;
    private readonly CaseHudDiscovery _discovery;
    private readonly CaseHudCapabilityStore _capabilities;
    private readonly CaseHudReleaseService _releases;
    private readonly CaseHudGamingLease _lease;
    private readonly CaseHudInstaller _installer;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _lock = new();
    private CaseHudStatus? _lastStatus;
    private CaseHudLightingState? _lighting;
    private CaseHudReleaseCheck? _release;
    private volatile string? _busy;
    private volatile string? _error;
    private bool _loopStarted;
    private bool _connected;
    private System.Threading.Timer? _leaseTimer;
    private (CaseHudInstall? Install, CaseHudCapability Capability, DateTimeOffset At)? _installState;
    private CaseHudApiInfo? _cachedInfo;
    private DateTimeOffset _cachedAt = DateTimeOffset.MinValue;
    private readonly ICaseHudApiSwitch _apiSwitch;
    private CaseHudFolderWatcher? _watcher;
    private CaseHudEventLoop? _loop;
    /// <summary>The announcement a request was refused with 403 api_disabled for; any folder change or a success clears it, rechecked every 30 s.</summary>
    private readonly CaseHudRefusal _refusal = new();
    private DateTimeOffset _rereadAt = DateTimeOffset.MinValue;
    private bool _instance;
    private DateTimeOffset _instanceAt = DateTimeOffset.MinValue;
    private int _invalidReads;

    public CaseHudManager(ConfigStore config, string dataDir, Func<bool> gameRunning, Action<string, object?> emit)
    {
        _config = config;
        _dataDir = dataDir;
        _gameRunning = gameRunning;
        _emit = emit;
        // Local API: no proxy, no redirects; the timeouts are the client's own (an event stream stays open for hours).
        _http = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _github = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        _github.DefaultRequestHeaders.UserAgent.ParseAdd($"CouchHud/{CatalogRules.AppVersion}");
        // Developer/test switch: a CaseHUD started with --data <dir> (e.g. --demo) keeps its api.json there, and may run from any folder.
        // Debug builds only: in a release it would let any program that sets the variable (user environment) point the
        // discovery at its own api.json and drop the check of PID and program file.
#if DEBUG
        var dataOverride = Environment.GetEnvironmentVariable("COUCHHUD_CASEHUD_DATA");
#else
        string? dataOverride = null;
#endif
        var overridden = !string.IsNullOrWhiteSpace(dataOverride);
        _discovery = new CaseHudDiscovery(overridden ? Path.Combine(dataOverride!, "api.json") : CaseHudDiscovery.DefaultApiJsonPath(),
            ProcessImage, new WindowsRegistry(), canonicalPath: CanonicalPath);
        _capabilities = SharedCapabilities(dataDir);
        _discovery.Capabilities = _capabilities;
        // Only the installed CaseHUD counts as CaseHUD (not any program that writes an api.json).
        if (!overridden) _discovery.ExpectedExe = () => _discovery.FindInstall()?.Exe;
        // Only a CaseHUD in this session that started before it wrote the file (a stale file with a reused PID does not count).
        _discovery.ProcessFacts = ProcessFacts;
        using (var self = Process.GetCurrentProcess()) _discovery.CurrentSession = self.SessionId;
        _releases = new CaseHudReleaseService(_github);
        _lease = new CaseHudGamingLease(CurrentClient);
        _apiSwitch = new CaseHudApiSwitch(TrustedExe, RunCliAsync, line => Console.Error.WriteLine(line));
        _installer = new CaseHudInstaller(_releases, new CaseHudInstallGate(), _discovery.FindInstall, CurrentClientFresh, new ShellSetupRunner(() => _discovery.FindInstall() is not null),
            StartCoreAsync, _gameRunning, () => (_config.Current.CaseHudInstallAutostart, _config.Current.CaseHudInstallFps),
            Path.Combine(_dataDir, "casehud-temp"), text => { _busy = text; Publish(); },
            apiAfterInstall: ApiAfterInstall, enableApi: EnableApiAfterInstallAsync);
        // Once, here: the manager is created once (Lazy in Hud), so this cannot delete a setup that is running.
        CleanTemp();
    }

    private static CaseHudCapabilityStore? _sharedCapabilities;
    private static readonly object SharedLock = new();

    /// <summary>One store for the manager and the autostart adapter of the Hud.</summary>
    internal static CaseHudCapabilityStore SharedCapabilities(string dataDir)
    {
        lock (SharedLock) return _sharedCapabilities ??= new CaseHudCapabilityStore(CapabilityFile(dataDir));
    }

    /// <summary>Where the result of the capability probe is kept (shared with the autostart adapter in the Hud).</summary>
    internal static string CapabilityFile(string dataDir) => Path.Combine(dataDir, "casehud-capability.json");

    // -----------------------------------------------------------------------
    // Finding the process

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder name, ref uint size);

    private const uint ProcessQueryLimitedInformation = 0x1000;

    /// <summary>The image of a process; the limited right also works for a CaseHUD that runs elevated (Process.SafeHandle would be refused).</summary>
    private static string? ProcessImage(int pid)
    {
        using var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle.IsInvalid) return null;
        var buffer = new StringBuilder(1024);
        var size = (uint)buffer.Capacity;
        return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString(0, (int)size) : null;
    }

    /// <summary>The real path of a file through an open handle (links and short names resolved); the text itself if it cannot be opened.</summary>
    internal static string CanonicalPath(string path) => TrustedCliRunner.CanonicalPath(path);

    private static readonly object InstalledLock = new();
    private static (string? Exe, DateTimeOffset At) _installedExe = (null, DateTimeOffset.MinValue);

    /// <summary>
    /// The window of process <paramref name="pid"/> belongs to the installed CaseHUD: its image (canonical path) is CaseHUD's
    /// installed exe. Told by the path, not the name, so a program called CaseHUD.exe elsewhere does not count (couch lock, see LockedWindow.CaseHud).
    /// </summary>
    internal static bool IsCaseHudProcess(int pid)
    {
        string? exe;
        lock (InstalledLock)
        {
            if (DateTimeOffset.UtcNow - _installedExe.At > TimeSpan.FromSeconds(30))
            {
                var install = new CaseHudDiscovery("", _ => null, new WindowsRegistry(), canonicalPath: CanonicalPath).FindInstall();
                _installedExe = (install is null ? null : CanonicalPath(Path.GetFullPath(install.Exe)), DateTimeOffset.UtcNow);
            }
            exe = _installedExe.Exe;
        }
        if (exe is null) return false;
        var image = ProcessImage(pid);
        return image is not null && string.Equals(CanonicalPath(image), exe, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The running CaseHUD as its api.json announces it, looked up at most once a second (the process check is not free and the lease and
    /// the page ask often); the folder watcher makes the next look fresh on every change.
    /// </summary>
    private CaseHudApiInfo? AnnouncedInfo(bool fresh = false)
    {
        lock (_lock)
        {
            if (!fresh && DateTimeOffset.UtcNow - _cachedAt < TimeSpan.FromSeconds(1)) return _cachedInfo;
            StoreInfo(_discovery.ReadRunning());
            return _cachedInfo;
        }
    }

    /// <summary>The announcement to talk to: none while the API refused exactly this one (switched off, api.json not gone yet).</summary>
    private CaseHudApiInfo? CurrentInfo(bool fresh = false)
    {
        var info = AnnouncedInfo(fresh);
        lock (_lock) return _refusal.Blocks(info) ? null : info;
    }

    /// <summary>Under _lock. Returns whether the announcement changed (another port, process or token, or it came or went).</summary>
    private bool StoreInfo(CaseHudApiInfo? info)
    {
        var before = _cachedInfo;
        _cachedInfo = info;
        _cachedAt = DateTimeOffset.UtcNow;
        // A CaseHUD that is running with a valid api.json has shown that it can be controlled (the entry is rewritten at the next boot).
        if (info is not null && before is null && _discovery.FindInstall() is { } found && _capabilities.Get(found) == CaseHudCapability.Unknown)
        {
            _capabilities.Set(found, capable: true);
            _installState = null;
        }
        var changed = !(before is null && info is null) && !CaseHudRefusal.Same(before, info);
        if (changed) _refusal.Clear();
        return changed;
    }

    /// <summary>Session and start time of a process, or null (gone, no access).</summary>
    private static CaseHudProcessFacts? ProcessFacts(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return new CaseHudProcessFacts(process.SessionId, process.StartTime.ToUniversalTime());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// CaseHUD said "unauthorized" (always 403): the token may have changed under us. The next look reads api.json again; at most every
    /// 5 s, so a CaseHUD that keeps refusing does not turn into a file-reading loop.
    /// </summary>
    private void OnUnauthorized()
    {
        lock (_lock)
        {
            if (DateTimeOffset.UtcNow - _rereadAt < TimeSpan.FromSeconds(5)) return;
            _rereadAt = DateTimeOffset.UtcNow;
            _cachedAt = DateTimeOffset.MinValue;
        }
    }

    /// <summary>CaseHUD answered: whatever refusal there was is over.</summary>
    private void Answered()
    {
        lock (_lock) _refusal.Clear();
    }

    /// <summary>instance.pid names a live process whose image is the installed CaseHUD.exe; looked up at most every two seconds.</summary>
    private bool InstanceRunning()
    {
        lock (_lock)
        {
            if (DateTimeOffset.UtcNow - _instanceAt < TimeSpan.FromSeconds(2)) return _instance;
            _instance = _discovery.ReadInstance() is not null;
            _instanceAt = DateTimeOffset.UtcNow;
            return _instance;
        }
    }

    /// <summary>Not installed, installed but not running, running with the API off, or controllable.</summary>
    private CaseHudApiState ApiState(CaseHudInstall? install)
    {
        var announced = AnnouncedInfo();
        bool refused;
        lock (_lock) refused = _refusal.Refused(announced);
        return CaseHudState.Detect(install is not null, InstanceRunning(), announced is not null, refused);
    }

    /// <summary>CaseHUD answered 403 api_disabled: the API was switched off. Stop talking to this announcement until a new one appears.</summary>
    private void OnApiDisabled()
    {
        lock (_lock)
        {
            _refusal.Mark(_cachedInfo);
            _lastStatus = null;
            _connected = false;
            _lighting = null;
        }
        Publish();
    }

    // -----------------------------------------------------------------------
    // Watching %APPDATA%\casehud

    private void StartWatching()
    {
        lock (_lock)
        {
            if (_watcher is not null) return;
            _watcher = new CaseHudFolderWatcher(_discovery.DataFolder, OnFolderChanged);
        }
        _watcher.Start();
    }

    /// <summary>
    /// api.json or instance.pid changed (debounced). Port and token are read again; a half-written api.json gets a few more tries; when the
    /// announcement changed or went, the open event stream ends (it would talk to a closed port or with an invalid token).
    /// </summary>
    private void OnFolderChanged()
    {
        if (_stop.IsCancellationRequested) return;
        var (read, info) = _discovery.ReadApi();
        if (read == CaseHudFileRead.Invalid && _invalidReads++ < 5)
        {
            _watcher?.Poke();
            return;
        }
        _invalidReads = 0;
        bool changed;
        lock (_lock)
        {
            changed = StoreInfo(info);
            // Every change in the folder ends a refusal: CaseHUD may have switched the API on again with the very same port and token.
            if (_refusal.Active)
            {
                _refusal.Clear();
                changed = true;
            }
            _instanceAt = DateTimeOffset.MinValue;
            if (changed)
            {
                _lastStatus = null;
                _connected = false;
                _lighting = null;
            }
        }
        if (changed) _loop?.Interrupt();
        Publish();
    }

    private CaseHudClient? CurrentClient() => CurrentInfo() is { } info ? new CaseHudClient(_http, info) : null;

    /// <summary>Without the cache: for places that wait for CaseHUD to appear or go.</summary>
    private CaseHudClient? CurrentClientFresh() => CurrentInfo(fresh: true) is { } info ? new CaseHudClient(_http, info) : null;

    /// <summary>A client, or the reason there is none: the API hint when CaseHUD runs with its API off, else "not running".</summary>
    private CaseHudClient Client()
    {
        if (CurrentClient() is { } client) return client;
        throw ApiState(InstallState().Install) == CaseHudApiState.ApiOff ? CaseHudState.ApiOff() : new CaseHudException("not_running", Strings.Get("casehud.notRunning"));
    }

    // -----------------------------------------------------------------------
    // Game lease

    public void GameStarted(string name, int? pid)
    {
        if (!_config.Current.CaseHudGamingLease) return;
        _ = _lease.BeginAsync(name, pid);
        // Renew every 15 s; the lease itself decides whether 60 s have passed.
        _leaseTimer ??= new System.Threading.Timer(_ => _ = _lease.TickAsync(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
    }

    public void GameEnded()
    {
        _ = _lease.EndAsync();
        _leaseTimer?.Dispose();
        _leaseTimer = null;
    }

    // -----------------------------------------------------------------------
    // Status

    private void StartEventLoop()
    {
        lock (_lock)
        {
            if (_loopStarted) return;
            _loopStarted = true;
        }
        var loop = new CaseHudEventLoop(_http, CurrentClient, s =>
        {
            Answered();
            _lastStatus = s;
            Publish();
        }, connected =>
        {
            if (connected) Answered();
            _connected = connected;
            if (!connected) _lastStatus = null;
            Publish();
        }, apiDisabled: OnApiDisabled);
        _loop = loop;
        StartWatching();
        _ = Task.Run(() => loop.RunAsync(_stop.Token));
    }

    /// <summary>Install entry and probe result, kept for a few seconds: the page polls, registry and file are not read every time.</summary>
    private (CaseHudInstall? Install, CaseHudCapability Capability) InstallState()
    {
        lock (_lock)
        {
            if (_installState is { } s && DateTimeOffset.UtcNow - s.At < TimeSpan.FromSeconds(5)) return (s.Install, s.Capability);
            var install = _discovery.FindInstall();
            var capability = install is null ? CaseHudCapability.Unknown : _capabilities.Get(install);
            _installState = (install, capability, DateTimeOffset.UtcNow);
            return (install, capability);
        }
    }

    private void Publish()
    {
        // Nothing goes out once the manager is shut down (a watcher or loop round that was still running).
        if (_stop.IsCancellationRequested) return;
        _emit("caseHud", Describe());
    }

    /// <summary>What the CaseHUD page shows: install state, running state, last status, update and progress.</summary>
    public object Describe()
    {
        var (install, capability) = InstallState();
        var release = _release?.Release;
        var apiState = ApiState(install);
        var running = apiState == CaseHudApiState.ApiOn && _lastStatus is not null && _connected;
        var apiUpdate = _lastStatus?.UpdateAvailable;
        return new
        {
            installed = install is not null,
            version = install?.Version ?? "",
            // A build that was probed and does not know --background has no API: the page asks for the update instead of offering control.
            needsUpdate = install is not null && capability == CaseHudCapability.Old,
            running,
            // notInstalled, notRunning, apiOff (running, API switched off in CaseHUD), apiOn.
            state = CaseHudState.Name(apiState),
            status = running ? _lastStatus : null,
            update = new
            {
                available = _release?.UpdateAvailable == true || !string.IsNullOrEmpty(apiUpdate),
                version = _release?.UpdateAvailable == true ? release!.Version.ToString() : apiUpdate ?? "",
                notes = _release?.UpdateAvailable == true ? release!.Notes : "",
                installable = release?.Installable ?? false,
                problem = release?.Problem,
                unavailable = _release?.ErrorCode == "unavailable",
                error = _release?.ErrorCode is "unavailable" or "rateLimit" ? _release.Error : null,
                latest = release?.Version.ToString()
            },
            busy = _busy,
            error = _error,
            options = new
            {
                autostart = _config.Current.CaseHudInstallAutostart,
                fps = _config.Current.CaseHudInstallFps,
                lease = _config.Current.CaseHudGamingLease,
                // Switch the API on after a first installation: offered (and done) in full integration only.
                api = _config.Current.CaseHudInstallApi,
                full = IsFull
            },
            unsigned = CaseHudPolicy.NeedsUnsignedConfirmation(CaseHudInfo.ReleasePublicKeyPem)
        };
    }

    // -----------------------------------------------------------------------
    // Bridge

    /// <summary>All calls of the CaseHUD page. The couch lock is checked by the caller (<see cref="CaseHudPolicy"/>).</summary>
    public async Task<object?> HandleAsync(string method, JsonElement args)
    {
        if (!CaseHudPolicy.All.Contains(method)) throw new InvalidOperationException($"unknown method {method}");
        try
        {
            var result = await DispatchAsync(method, args);
            if (method == "caseHudStatus") _error = null;
            return result;
        }
        catch (CaseHudException ex) when (CaseHudState.IsApiDisabled(ex))
        {
            // Not an error of this call: the API is off. The page shows the hint in place of the controls.
            OnApiDisabled();
            throw;
        }
        catch (CaseHudException ex) when (ex.Code == "unauthorized")
        {
            OnUnauthorized();
            _error = ex.Message;
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Every failure ends up on the page, not only the ones of the install.
            _error = ex.Message;
            throw;
        }
    }

    private async Task<object?> DispatchAsync(string method, JsonElement args)
    {
        JsonElement Arg(int i) => args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > i ? args[i] : default;
        string Str(int i) => Arg(i).ValueKind == JsonValueKind.String ? Arg(i).GetString()! : throw new ArgumentException($"{method}: argument {i} must be a string");
        bool Bool(int i) => Arg(i).ValueKind == JsonValueKind.True;

        switch (method)
        {
            case "caseHudStatus":
                StartEventLoop();
                if (_lastStatus is null && CurrentClient() is { } c)
                {
                    try { _lastStatus = await c.StatusAsync(); _connected = true; Answered(); }
                    catch (CaseHudException ex) when (CaseHudState.IsApiDisabled(ex))
                    {
                        lock (_lock) _refusal.Mark(_cachedInfo);
                        _lastStatus = null;
                        _connected = false;
                    }
                    catch (CaseHudException ex) when (ex.Code == "unauthorized") { OnUnauthorized(); }
                    catch (CaseHudException) { }
                }
                return Describe();
            case "caseHudCheckUpdate":
                _release = await _releases.CheckAsync(_discovery.FindInstall()?.Version, force: Bool(0));
                return Describe();
            case "caseHudPrepare":
            {
                // A fresh look at the release; the token is what install and update will accept, for this release only.
                var prepare = await _installer.PrepareAsync();
                var release = prepare.Release;
                _release = release is null ? _release : new CaseHudReleaseCheck(release, prepare.IsUpdate);
                return new
                {
                    token = prepare.Token,
                    version = release?.Version.ToString(),
                    installed = prepare.Installed,
                    isUpdate = prepare.IsUpdate,
                    unsigned = prepare.Unsigned,
                    notes = release?.Notes,
                    error = prepare.Error
                };
            }
            case "caseHudStart":
                await StartAsync();
                return Describe();
            case "caseHudInstall":
            case "caseHudUpdate":
                _error = null;
                await _installer.InstallAsync(Arg(0).ValueKind == JsonValueKind.String ? Arg(0).GetString() : null);
                lock (_lock) _installState = null;
                _release = await _releases.CheckAsync(_discovery.FindInstall()?.Version);
                return Describe();
            case "caseHudOpenGui":
                await OpenGuiAsync();
                return null;
            case "caseHudSetApi":
            {
                var on = Bool(0);
                await _apiSwitch.SetAsync(on);
                lock (_lock)
                {
                    _cachedAt = DateTimeOffset.MinValue;
                    _instanceAt = DateTimeOffset.MinValue;
                    _refusal.Clear();
                }
                _error = null;
                StartEventLoop();
                _loop?.Interrupt();
                return Describe();
            }
            case "caseHudPanels": return await Client().PanelsAsync();
            case "caseHudPatchPanel":
                // CaseHUD's refusal reaches the page as a readable, cleaned and capped text (as for lighting).
                try { return await Client().PatchPanelAsync(Str(0), CaseHudPanelPatch.FromJson(Arg(1))); }
                catch (CaseHudException ex) when (ex.Status > 0 && !CaseHudState.IsApiDisabled(ex)) { throw new InvalidOperationException(CaseHudLighting.ErrorText(ex)); }
            case "caseHudLighting": return await LightingAsync();
            case "caseHudPatchLighting": return await PatchLightingAsync(Arg(0));
            case "caseHudDesigns": return await Client().DesignsAsync();
            case "caseHudSets": return await Client().SetsAsync();
            case "caseHudApplySet":
            {
                var ids = Arg(2).ValueKind == JsonValueKind.Array ? Arg(2).EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : null;
                await Client().ApplySetAsync(Str(0), Str(1), ids is { Count: > 0 } ? ids : null);
                return null;
            }
            case "caseHudScenes": return await Client().ScenesAsync();
            case "caseHudActivateScene":
                try { await Client().ActivateSceneAsync(Str(0)); }
                catch (CaseHudException ex) when (ex.Code == "conflict") { throw new InvalidOperationException(Strings.Get("casehud.sceneConflict")); }
                return null;
            case "caseHudRotation": await Client().SetRotationAsync(Bool(0)); return null;
            case "caseHudNextScene": await Client().NextSceneAsync(); return null;
            case "caseHudPower": await Client().PowerAsync(Bool(0)); return null;
            case "caseHudIdentify": await Client().IdentifyAsync(Arg(0).ValueKind == JsonValueKind.String ? Arg(0).GetString() : null); return null;
            case "caseHudCommunity": return await Client().CommunityAsync();
            case "caseHudImport": await Client().ImportCommunityAsync(Str(0)); return null;
            case "caseHudAwtrix": await Client().SetAwtrixAsync(Bool(0)); return null;
            case "caseHudPreview":
            {
                // As a data: URL: the token stays on the host side and never appears in a WebView URL.
                var size = Arg(2).ValueKind == JsonValueKind.Number ? Arg(2).GetInt32() : (int?)null;
                var png = await Client().PreviewAsync(Str(0), Str(1), size, Arg(3).ValueKind == JsonValueKind.String ? Arg(3).GetString() : null);
                return "data:image/png;base64," + Convert.ToBase64String(png);
            }
            default:
                throw new InvalidOperationException($"unknown method {method}");
        }
    }

    // -----------------------------------------------------------------------
    // Lighting (CaseHUD 1.0)

    /// <summary>Only for a build whose status carries <c>lighting</c>: older ones have no endpoint and are not asked.</summary>
    private async Task<CaseHudLightingState> LightingAsync()
    {
        var client = Client();
        if (_lastStatus is null)
        {
            try { _lastStatus = await client.StatusAsync(); }
            catch (CaseHudException) { }
        }
        if (!CaseHudLighting.Supported(_lastStatus)) throw new InvalidOperationException(Strings.Get("casehud.lightingUnsupported"));
        try { return _lighting = await client.LightingAsync(); }
        catch (CaseHudException ex) when (ex.Status > 0 && !CaseHudState.IsApiDisabled(ex)) { throw new InvalidOperationException(CaseHudLighting.ErrorText(ex)); }
    }

    /// <summary>
    /// A change of the lighting: checked against the last state (CaseHudLighting.BuildPatch), so the page cannot send arbitrary JSON.
    /// CaseHUD answers 200 with the full state, which becomes the new one. The two refusals the user can resolve in CaseHUD's window
    /// (409 ack_required, 403 gui_only; nothing was applied) come back as { problem }; every other refusal as a readable error.
    /// </summary>
    private async Task<object?> PatchLightingAsync(JsonElement patch)
    {
        var last = _lighting ?? await LightingAsync();
        var body = CaseHudLighting.BuildPatch(patch, last);
        try
        {
            // Only an empty answer (not the documented 200 with the state) costs another read.
            _lighting = await Client().PatchLightingAsync(body) ?? await Client().LightingAsync();
            return new { state = _lighting };
        }
        catch (CaseHudException ex) when (CaseHudLighting.Problem(ex) is { } problem)
        {
            return new { problem };
        }
        catch (CaseHudException ex) when (ex.Status > 0 && !CaseHudState.IsApiDisabled(ex))
        {
            // A 400 usually means the state moved on (a device went away, lighting was switched off): the next check uses the fresh one.
            if (ex.Status == 400)
            {
                try { _lighting = await Client().LightingAsync(); }
                catch (CaseHudException) { }
            }
            throw new InvalidOperationException(CaseHudLighting.ErrorText(ex));
        }
    }

    // -----------------------------------------------------------------------
    // Start, GUI

    private async Task StartAsync()
    {
        await StartCoreAsync();
        try { _lastStatus = await Client().StatusAsync(); _connected = true; }
        catch (CaseHudException ex) { _error = ex.Message; }
        Publish();
    }

    private async Task StartCoreAsync()
    {
        var install = _discovery.FindInstall() ?? throw new CaseHudException("not_installed", Strings.Get("casehud.notInstalled"));
        StartEventLoop();
        // Without a taskbar (CouchHUD as the shell) CaseHUD has no tray to put its icon on.
        await _discovery.StartAsync(install, (exe, arguments) =>
        {
            using var process = Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false, WorkingDirectory = install.Directory });
            // Gone within a second: the flag was not known (exit code 2) or it could not run; the discovery tries the next form.
            return process is not null && process.WaitForExit(1000) ? process.ExitCode : null;
        }, noTray: !ShellManager.IsDesktopShellRunning());
        lock (_lock)
        {
            _cachedAt = DateTimeOffset.MinValue;
            _installState = null;
        }
    }

    /// <summary>CaseHUD's own window on the PC (design editor): the API opens it and puts it in front; older versions get started again instead.</summary>
    private async Task OpenGuiAsync()
    {
        var install = _discovery.FindInstall() ?? throw new CaseHudException("not_installed", Strings.Get("casehud.notInstalled"));
        // Running CaseHUD with the endpoint: let it take the foreground, then ask it to show its window.
        if (CurrentInfo(fresh: true) is { } info)
        {
            Native.NativeMethods.AllowSetForegroundWindow(info.Pid);
            try
            {
                await new CaseHudClient(_http, info).OpenGuiAsync();
                return;
            }
            catch (CaseHudException ex) when (ex.Status == 503) { throw new InvalidOperationException(Strings.Get("casehud.guiUnavailable")); }
            catch (CaseHudException ex) when (ex.Status is 404 or 405) { /* older CaseHUD: fall back to starting the exe */ }
        }
        Process.Start(new ProcessStartInfo(install.Exe) { UseShellExecute = false, WorkingDirectory = install.Directory })?.Dispose();
    }

    // -----------------------------------------------------------------------
    // The setup: updates through the service, the first installation with UAC

    /// <summary>
    /// An update of an installed CaseHUD goes to the CouchHUD service (owner decision E7, P0 spike): the verified bytes are streamed, the
    /// service verifies them again against the built-in CaseHUD key and runs the setup itself (no UAC prompt); CaseHUD is started again
    /// in this session afterwards by CouchHUD (CaseHudInstaller; the setup ends CaseHUD for the update, the job says RestartCaseHud). The first installation stays with Windows' UAC
    /// prompt (it needs the user's choices and is a deliberate decision of an administrator), as does a release without a signature
    /// (the service accepts signed setups only): the real path of the held file is checked first, then exactly that one is started.
    /// </summary>
    private sealed class ShellSetupRunner(Func<bool> installed) : ICaseHudSetupRunner
    {
        public async Task<int> RunAsync(VerifiedSetup setup, string arguments, Action<string> step, CancellationToken ct)
        {
            if (installed() && setup.ChecksumFile is not null && !string.IsNullOrEmpty(setup.Signature))
            {
                step(Strings.Get("casehud.installing", "{version}"));
                var header = new CouchHud.Service.Contracts.SetupStreamArgs(Path.GetFileName(setup.Path), setup.Length, Convert.ToBase64String(setup.ChecksumFile), setup.Signature);
                var result = await AdminService.Ops.InstallCaseHudAsync(header, setup.ReadFromStart(), null, ct);
                AdminService.ThrowIfFailed(result.Result);
                if (result.Job is { State: "failed" }) throw new InvalidOperationException(Strings.Get("casehud.serviceFailed"));
                return 0;
            }
            string startPath;
            try
            {
                var final = SetupProbe.FinalPath(setup.Handle);
                PathSafety.RequireSamePath(setup.Path, final);
                startPath = PathSafety.StripPrefix(final);
                PathSafety.RequireNoReparsePoints(startPath);
            }
            catch (Exception ex) when (ex is CatalogException or IOException)
            {
                Console.Error.WriteLine($"[casehud] rejected: {ex.Message}");
                throw new InvalidOperationException(ex.Message);
            }

            step(Strings.Get("admin.waiting"));
            Process process;
            try
            {
                process = Process.Start(new ProcessStartInfo(startPath, arguments) { UseShellExecute = true, Verb = "runas" })
                          ?? throw new InvalidOperationException(Strings.Get("selfupdate.setupNotStarted"));
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                throw new SetupDeclinedException();
            }

            using (process)
            {
                step(Strings.Get("casehud.installing", "{version}"));
                if (!await Task.Run(() => process.WaitForExit(TimeSpan.FromMinutes(10)), ct))
                    throw new InvalidOperationException(Strings.Get("selfupdate.slow"));
                return process.ExitCode;
            }
        }
    }

    // -----------------------------------------------------------------------
    // CaseHUD's API switch (CaseHUD.exe --set-api=on|off, in this user's session)

    private bool IsFull => string.Equals(_config.Current.IntegrationMode, "full", StringComparison.Ordinal);

    /// <summary>After a first installation: only in full integration and only when the user left the choice on.</summary>
    private bool ApiAfterInstall() => IsFull && _config.Current.CaseHudInstallApi;

    /// <summary>The installer's hook: a failure becomes the hint on the page, the installation itself stays a success.</summary>
    private async Task EnableApiAfterInstallAsync(CancellationToken ct)
    {
        try { await _apiSwitch.SetAsync(true, ct); }
        catch (InvalidOperationException ex) { _error = ex.Message; }
        lock (_lock)
        {
            _cachedAt = DateTimeOffset.MinValue;
            _instanceAt = DateTimeOffset.MinValue;
        }
    }

    /// <summary>
    /// The installed CaseHUD.exe as it may be run from here: from the HKLM uninstall entry (only administrators write it), its real path
    /// (links and short names resolved), no reparse point on the way, and inside Program Files (only administrators write there).
    /// Never a path from api.json or instance.pid, never one in a user-writable folder; null otherwise.
    /// </summary>
    private string? TrustedExe()
    {
        var install = _discovery.FindInstall();
        if (install is null) return null;
        try
        {
            var exe = CanonicalPath(Path.GetFullPath(install.Exe));
            PathSafety.RequireNoReparsePoints(exe);
            if (CaseHudCli.IsTrustedInstallPath(exe, TrustedCliRunner.TrustedRoots())) return exe;
            Console.Error.WriteLine("[casehud] api switch: CaseHUD is not installed below Program Files, not run");
            return null;
        }
        catch (Exception ex) when (ex is CatalogException or IOException or ArgumentException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[casehud] api switch: install path rejected ({ex.GetType().Name})");
            return null;
        }
    }

    /// <summary>
    /// Runs the verified exe with one argument, no window, no shell. Only stderr is redirected (for one capped log line); stdout is not, so
    /// no pipe of ours is left for a child to hold. The wait is bounded by <see cref="CaseHudCli.RunAsync"/>: timeout or cancellation kill
    /// the process tree, stderr gets a short grace after the exit. Null exit = timed out (ended).
    /// </summary>
    private static async Task<(int? Exit, string Stderr)> RunCliAsync(string exe, string arguments, CancellationToken ct)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!
        };
        info.ArgumentList.Add(arguments);
        Process? process;
        try { process = Process.Start(info); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return (-1, ex.GetType().Name); }
        if (process is null) return (-1, "");
        return await CaseHudCli.RunAsync(new CliProcess(process), ct);
    }

    private sealed class CliProcess : ICaseHudCliProcess
    {
        private readonly Process _process;

        public CliProcess(Process process)
        {
            _process = process;
            Stderr = ReadCappedAsync(process.StandardError);
        }

        public Task<string> Stderr { get; }
        public int ExitCode => _process.ExitCode;
        public Task WaitForExitAsync(CancellationToken ct) => _process.WaitForExitAsync(ct);

        public void Kill()
        {
            try { _process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or AggregateException) { }
        }

        // Closes our end of the pipe: a read still waiting (a child holds the other end) ends with an exception, caught below.
        public void Dispose() => _process.Dispose();
    }

    /// <summary>At most 4096 characters are kept; the rest is read and dropped so the process never blocks on a full pipe.</summary>
    private static async Task<string> ReadCappedAsync(StreamReader reader)
    {
        var kept = new StringBuilder();
        var chunk = new char[1024];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(chunk)) > 0)
                if (kept.Length < 4096) kept.Append(chunk, 0, Math.Min(read, 4096 - kept.Length));
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        return kept.ToString();
    }

    private void CleanTemp()
    {
        try
        {
            var dir = Path.Combine(_dataDir, "casehud-temp");
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _leaseTimer?.Dispose();
        _watcher?.Dispose();
        // End a running lease (CaseHUD would otherwise keep the gaming scene for up to two more minutes), but never wait long.
        try { Task.Run(() => _lease.EndAsync(TimeSpan.FromMilliseconds(1500))).Wait(TimeSpan.FromMilliseconds(2000)); }
        catch (AggregateException) { }
        _http.Dispose();
        _github.Dispose();
    }
}
