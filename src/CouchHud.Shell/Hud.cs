using System.Windows;
using CouchHud.Core.Assistant;
using CouchHud.Core.Config;
using CouchHud.Core.Guard;
using CouchHud.Core.Library;
using CouchHud.Core.Platform;
using CouchHud.Core.Integrations;
using System.Net.Http;
using CouchHud.Core.Skins;
using CouchHud.Core.Startup;
using CouchHud.Core.Remote;
using CouchHud.Core.Notifications;
using CouchHud.Core.Security;
using CouchHud.Core.Parental;
using CouchHud.Sdk;
using CouchHud.Shell.UiHost;
using CouchHud.Shell.Input;
using CouchHud.Shell.Services;
using CouchHud.Core.Localization;
using CouchHud.ServiceClient;
using CodeCheckResults = CouchHud.Service.Contracts.CodeCheckResults;
using CodeKinds = CouchHud.Service.Contracts.CodeKinds;
using ServiceOperation = CouchHud.Service.Contracts.Operation;
using ErrorCodes = CouchHud.Service.Contracts.ErrorCodes;
using BluetoothRecoveryController = CouchHud.Core.Input.BluetoothRecoveryController;

namespace CouchHud.Shell;

/// <summary>Ties window, controller, library and system functions together. UI thread only.</summary>
public sealed class Hud(ConfigStore config, LibraryService library, ArtworkResolver artwork, StoreInfoService storeInfo, ShellManager shell, XInputPoller pad, HttpClient http, string dataDir, bool dev, CouchHud.Core.Plugins.PluginManager plugins) : IRemoteTarget
{
    private readonly WeatherService _weather = new(http);
    private readonly NewsService _news = new(http);
    private readonly HomeAssistantClient _home = new(http)
    {
        BaseUrl = config.Current.HomeAssistantUrl,
        Token = Secrets.Unprotect(config.Current.HomeAssistantToken)
    };

    private MainWindow? _window;
    private System.Windows.Forms.NotifyIcon? _tray;
    private CancellationTokenSource? _homeHold;
    private bool _pcMode;
    private readonly GameSessionTracker _sessions = new(new Win32ProcessProbe());
    private readonly System.Windows.Threading.DispatcherTimer _sessionTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private int _ticking;
    private AudioService? _audio;
    private string? _lastAudioDefault;
    private readonly UpdateService _updates = new();
    private readonly StartupManager _startup = new(new WindowsRegistry());
    private readonly ServiceSwitch _services = new();
    // Whether CouchHud did explorer's autostart job this session (only as the shell).
    private bool _ranStartup;
    // Model answers with several tool rounds take longer than the shared client allows.
    private readonly HttpClient _aiHttp = new() { Timeout = TimeSpan.FromSeconds(120) };
    private readonly McpHost _mcp = new();
    private readonly RemoteHost _remote = new();
    private readonly NotificationCenter _notifications = new();
    private BluetoothService? _bluetoothService;

    // PINs from pairing go to the TV: shown for keyboards, compared for headsets with a display.
    private BluetoothService _bluetooth => _bluetoothService ??= new BluetoothService(
        text => Application.Current.Dispatcher.InvokeAsync(() => RequestConfirmation(text, danger: false)).Task.Unwrap());
    private readonly PerformanceMonitor _performance = new();
    private readonly NightLightService _night = new();
    private CouchHud.Core.Saves.SaveGameService? _saves;

    /// <summary>Save game backups (Ludusavi manifest); the manifest is ~20 MB, so it gets a client with a longer timeout.</summary>
    public CouchHud.Core.Saves.SaveGameService Saves => _saves ??= new CouchHud.Core.Saves.SaveGameService(
        config, library.Find, name => library.Items.Count(i => i.Name == name),
        new CouchHud.Core.Saves.ManifestStore(new HttpClient { Timeout = TimeSpan.FromMinutes(5), DefaultRequestHeaders = { { "User-Agent", "CouchHUD" } } }, Path.Combine(dataDir, "saves")),
        dataDir,
        () => new WindowsRegistry().ReadString(@"HKCU\Software\Valve\Steam", "SteamPath")?.Replace('/', '\\'),
        // Running through CouchHUD, or any process started from the install folder (launched elsewhere).
        item => _sessions.Current?.Item.Id == item.Id
                || CouchHud.Core.Saves.SaveGameService.AnyProcessUnder(new Win32ProcessProbe().Snapshot(), item.InstallDir));
    private ProfileRestore? _profileRestore;
    private ControllerMouse? _mouse;
    private ForegroundGuard? _foreground;
    private readonly ForegroundState _fgState = new();
    // A screenshot or clip is being taken through the Game Bar: the guard keeps out of the way.
    private bool _capturing;
    private GuardSnapshot? _gameBar;
    // Steam's own settings in CouchHud's favour on the couch (see SteamGuardService).
    private SteamGuardService? _steamGuardService;
    // Which game clients CouchHud started itself (only those are closed again).
    private readonly ClientOwnership _clients = new();
    private SteamGuardService SteamSettings => _steamGuardService ??= new SteamGuardService(dataDir, () => GameBusy, () => _mouse?.Active == true || _windowsUnlock.Pending || _parentAuth.Pending);
    // Policies and accessibility shortcuts of the locked couch (see LockedDesktop).
    private LockedDesktop? _lockedDesktop;
    private LockedDesktop Hardening => _lockedDesktop ??= new LockedDesktop(dataDir);
    // The startup entries run through the shell: the policies wait until they have been started.
    private bool _hardeningHold;
    private bool? _lastLocked;
    private bool _quitting;
    // Guide tap with "pause in menu": freeze the game once the HUD is active.
    private bool _pauseOnActivate;

    // Couch lock (see CouchSecurity.cs).
    private readonly Guard _guard = new();
    // Unlock, release and waiting times follow a clock that does not obey the system clock (see MonotonicClock).
    private static readonly MonotonicClock MonoClock = new();
    private static DateTimeOffset MonoNow() => MonoClock.UtcNow;
    // Kept outside the config so a restart doesn't hand out fresh tries. The couch code and the parents' code count separately:
    // wrong tries at one must not shut the other out.
    private readonly Lockout _lockout = new(MonoNow, Path.Combine(dataDir, "lockout.json"));
    private readonly Lockout _parentLockout = new(MonoNow, Path.Combine(dataDir, "lockout-parent.json"));
    private DateTimeOffset _unlockedUntil = DateTimeOffset.MinValue;
    // Windows mode: the lock screen was requested by us (see WindowsUnlockRequest); the unlock then waits for a confirmation at the TV.
    private readonly WindowsUnlockRequest _windowsUnlock = new(MonoNow);
    private WindowsConfirm _confirm = new(ConfirmKind.None, DateTimeOffset.MinValue, null, null);
    // An app the parents started while unlocked: free until the HUD is back in front (name for the display), see ForegroundContext.AppsReleased.
    private string? _appsReleased;
    // Started as the shell, i.e. without a desktop: only then is there anything to lock.
    private bool _asShell;

    /// <summary>What a game profile changed, to put back when the game ends.</summary>
    private sealed record ProfileRestore(DateTimeOffset Applied, DisplayMode? Mode, bool? Hdr, string? Audio, bool Night);
    private WindowsNotifications? _windowsNotifications;
    private Pairing? _pairing;
    private KeyboardState _keyboard = new(false, "", false, "");
    private readonly Dictionary<string, TaskCompletionSource<bool>> _confirmations = new();
    private AssistantSession? _assistant;
    private string? _assistantFor;
    private CancellationTokenSource? _assistantCts;

    public void Start(MainWindow window)
    {
        _window = window;
        // What an administrator approves through UAC is said in the HUD first (P9 review M-c); a moment to read it before the prompt.
        AdminService.AnnounceApproval = async (ops, reason) =>
        {
            Ui(() => Emit("adminApproval", new { what = ops.Select(o => o.ToString()).Distinct().ToArray(), reason }));
            // The confirmation of the lock of 0.4.0 has more to say (why at all): more time to read it.
            await Task.Delay(TimeSpan.FromSeconds(reason is null ? 2 : 8));
        };
        // Website entries from before 0.4.0 opened in the system's browser: known copy-protected streaming services stay in Edge,
        // all others now open in the couch browser. Decided once, then the entry's own setting counts.
        if (config.Current.Apps.Any(e => e.OpenInEdge is null && WebLinks.IsWebUrl(e.Url)))
            config.Update(c => WebLinks.Migrate(c.Apps));
        // Only the shell instance runs without an explorer, and everything that talks to explorer-hosted parts of Windows
        // (taskbar, notification area, toasts, COM objects of the shell) may fail there. No optional feature
        // is allowed to stop the start: each one is its own step, a failure costs that feature only.
        var optional = new OptionalSteps(Console.Error.WriteLine);
        library.Changed += items => Ui(() => Emit("library", UiBridge.ToDto(items)));

        // Asked once in the background (the answer is cached): never on the UI thread, never as a failed sign-in if the flags can tell.
        _ = Task.Run(AccountPassword.Refresh);
        plugins.IsLocked = () => Locked;
        optional.Run("session tracking", StartSessionTracking);
        _sessions.Library = () => config.Current.Parental.Enabled ? library.Items : null;
        optional.Run("parental controls", () => _ = Parental);
        StartAudio();
        optional.Run("update checks", StartUpdateChecks);
        optional.Run("catalog checks", Catalog.StartBackgroundChecks);
        optional.Run("self update checks", SelfUpdate.StartBackgroundChecks);
        optional.Run("notifications", StartNotifications);
        optional.Run("bluetooth recovery", StartBluetoothGuard);
        optional.Run("night mode", () => _night.Start(() => (config.Current.NightMode, config.Current.NightModeStrength)));
        optional.Run("hdr", ApplyUiHdr);
        _ = ApplyMcpAsync();
        _ = ApplyRemoteAsync();

        pad.Down += button => Ui(() => OnPadDown(button));
        pad.Up += button => Ui(() =>
        {
            if (!_pcMode) Emit("input", new { type = "up", button });
        });
        pad.ConnectionChanged += (index, connected) => Ui(() => Emit("pad", new { index, connected }));
        optional.Run("controller", pad.Start);
        optional.Run("controller mouse", () => _mouse = new ControllerMouse(pad.Sticks));

        // The UI mutes its background music whenever something else is in front.
        window.Activated += (_, _) =>
        {
            // The window came to the front: the HUD is active, also when the controller is on the browser page inside it (an inactive HUD
            // pauses its animations, and a page entered while paused would stay half drawn).
            Emit("focus", new { active = true });
            // The HUD is in front again (Alt+Tab, the guide button, a click): apps released for a start are held back again, not
            // only when ShowHud ran.
            _appsReleased = null;
            if (_pauseOnActivate && _sessions.Current is { } running)
            {
                // Not while BringToFront still shares the input queue of the window that was in front.
                Ui(() =>
                {
                    GamePause.Pause(VerifiedPids([running]));
                    Emit("session", SessionDto());
                });
            }
            _pauseOnActivate = false;
        };
        window.Deactivated += (_, _) =>
        {
            Emit("focus", new { active = false });
            _pauseOnActivate = false;
            // Whatever comes to the front (the game via Alt+Tab, too) must not stay frozen.
            if (GamePause.IsPaused)
            {
                GamePause.ResumeAll();
                Emit("session", SessionDto());
            }
        };

        optional.Run("session switch events", () => Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch);
        GamePause.StateFile = Path.Combine(dataDir, "paused.txt");
        optional.Run("frozen games", () => GamePause.ResumeLeftovers(GamePause.StateFile));

        // Only the shell instance takes over explorer's autostart duty.
        // Not just "no taskbar": light mode may have closed it before a restart of CouchHud.
        // A restart into PC mode that did not get its shell entry back (RunOnce missed): do it now.
        if (config.Current.PendingShellRestore) optional.Run("pending shell restore", RestorePendingShell);
        // (App waited a moment for a desktop that is just starting before it created the window.)
        var installedAsShell = optional.Run("shell entry", shell.IsInstalledAsShell, false);
        _asShell = installedAsShell && !ShellManager.IsDesktopShellRunning();
        optional.Run("shell entry check", () =>
        {
            var reconcile = shell.Reconcile(config.Current.IntegrationMode);
            if (reconcile.ForceFull) config.Update(c => c.IntegrationMode = "full");
            foreach (var note in reconcile.Notes) PostNotification("CouchHUD", Strings.Ref("hud.shellEntry"), note, security: true);
        });
        if (config.Current.ShellRemovedReason.Length > 0)
        {
            optional.Run("shell removed notice", () =>
            {
                PostNotification("CouchHUD", Strings.Ref("hud.shellRemoved"), Strings.Ref("hud.shellRemoved.body", config.Current.ShellRemovedReason), security: true);
                config.Update(c => c.ShellRemovedReason = "");
            });
        }
        if (config.Fault is { } fault)
        {
            // config.json is there but unusable: the couch runs locked and nothing is saved until the file is fixed (or removed).
            optional.Run("config fault notice", () => PostNotification("CouchHUD", Strings.Ref("hud.configFault"), Strings.Ref(fault == ConfigFault.Invalid ? "hud.configFault.invalid" : "hud.configFault.unreadable"), security: true));
        }
        optional.Run("service watch", StartServiceWatch);
        Console.WriteLine($"[hud] started: asShell {_asShell}, integration {config.Current.IntegrationMode}, lock {config.Current.CouchLock}");
        // What a crashed run left behind goes first; a locked start applies the policies again right after.
        _hardeningHold = _asShell && Full && config.Current.RunStartupApps && installedAsShell;
        // A start that locks does not release first just to set the same values again ("released", then "set" at every start); the
        // hold for the startup programs only delays setting anew, it is no reason to release. Everything else (unlocked start, PC
        // mode, light) gives back what a crashed run left behind.
        if (StartPolicyDecision.ReleaseAtStart(Locked, dev, LockOn, _pcMode, _hardeningHold)) optional.Run("lock policies reset", () => Hardening.Set(false, force: true));
        optional.Run("couch lock", ApplyCouchSecurity);
        optional.Run("stuck policies check", NotifyIfPoliciesStuck);
        optional.Run("firewall check", NotifyIfFirewallBlocks);
        // Also undoes what a crashed run left behind when we don't start on the couch.
        optional.Run("game bar migration", MigrateGameBar);
        // Before Steam's settings are touched (a restart for that does not make a client ours).
        if (Full) optional.Run("client ownership", NoteClientsAtStart);
        optional.Run("competitor suppression", () => ApplyCompetitorSuppression(announce: true));
        optional.Run("foreground guard", () =>
        {
            _foreground = new ForegroundGuard(ForegroundNow, _fgState, () => _window?.BringToFront(), OnClientNeedsAttention, Console.Error.WriteLine, GamePids, EndMouseModeWhenLocked);
            _foreground.Start();
        });
        // Without explorer nobody answers the volume keys.
        if (_asShell && _audio is not null)
        {
            optional.Run("volume keys", () =>
            {
                _mediaKeys = new MediaKeys(step => Ui(() => OnVolumeKey(step)));
                _mediaKeys.Start();
            });
        }
        if (!Full && config.Current.CloseExplorerInLight) optional.Run("taskbar hiding", HideTaskbar);
        if (_asShell && Full)
        {
            if (config.Current.RunStartupApps && installedAsShell)
            {
                _ranStartup = true;
                var modes = new Dictionary<string, string>(config.Current.StartupModes);
                Task.Run(() =>
                {
                    try
                    {
                        // CaseHUD's own entry ("--minimized") becomes the background start, without tray icon as there is no taskbar.
                        var adapt = CouchHud.Core.Integrations.CaseHud.CaseHudAutostart.ForShell(new WindowsRegistry(), noTray: !ShellManager.IsDesktopShellRunning(), canonicalPath: CaseHudManager.CanonicalPath, capabilities: CaseHudManager.SharedCapabilities(dataDir));
                        _startup.RunCouch(modes, adapt);
                        _startup.RunOnce();
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[startup] entries: {ex}");
                    }
                    finally
                    {
                        // Whatever happened, the policies must not wait forever.
                        Ui(() =>
                        {
                            _hardeningHold = false;
                            SyncLockState();
                        });
                    }
                });
            }
            optional.Run("paused services", () => _services.Apply(StartupMode.Couch, config.Current.CouchServicesInstalled));
            _ = StartLaunchersAsync();
        }
        if (optional.Failed.Count > 0) Console.Error.WriteLine($"[startup] running without: {string.Join(", ", optional.Failed)}");
    }

    private MediaKeys? _mediaKeys;

    /// <summary>Volume key without explorer: step is +2 / -2, 0 toggles mute.</summary>
    private void OnVolumeKey(int step)
    {
        if (_audio is null) return;
        var state = _audio.GetState();
        if (step == 0) _audio.SetMute(!state.Muted);
        else _audio.SetVolume(state.Volume + step);
        state = _audio.GetState();
        Emit("audio", state);
        Emit("toast", state.Muted ? Strings.Get("toast.muted") : Strings.Get("toast.volume", state.Volume));
    }

    /// <summary>
    /// Windows asks to end the session: nothing may hold it up. The guard keeps running on purpose:
    /// if the shutdown is cancelled after we are gone, it brings CouchHud back (it sees a real
    /// shutdown by itself, see Guard.Run).
    /// </summary>
    public void OnSessionEnding()
    {
        try
        {
            _foreground?.Stop();
            _quitting = true;
            // The policies are per user and outlive the session: back to the user's own before the sign-out. The helper tasks
            // run on their own, so wait a moment for them (the sign-out does not wait for us for long).
            LockedDesktop.Release(dataDir, wait: true, maxWait: TimeSpan.FromSeconds(3));
            // The Xbox button's Game Bar value is per user as well.
            ApplyCompetitorSuppression(release: true);
            GamePause.ResumeAll();
            Recovery.MarkClean(dataDir);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[hud] session end: {ex.Message}");
        }
        if (_window is not null) _window.AllowClose = true;
    }

    /// <summary>Puts the shell entry back after a restart into PC mode, when the RunOnce entry did not.</summary>
    private void RestorePendingShell()
    {
        config.Update(c => c.PendingShellRestore = false);
        try
        {
            shell.ClearRestoreFallback();
            if (shell.IsInstalledAsShell()) return;
            shell.InstallAsShell();
            SignInLock.SyncInBackground(dataDir);
            Console.WriteLine("[hud] pending shell restore done");
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Console.Error.WriteLine($"[hud] pending shell restore failed: {ex.Message}");
        }
    }

    private void Emit(string name, object? payload) => _window?.Bridge?.Emit(name, payload);

    /// <summary>Lets helpers (assistant tools) push events to the UI.</summary>
    public void Notify(string name, object? payload) => Ui(() => Emit(name, payload));

    public IReadOnlyList<LibraryItem> LibraryItems => library.Items;

    public LibraryItem? FindItem(string id) => library.Items.FirstOrDefault(i => i.Id == id);

    private static void Ui(Action action) => Application.Current?.Dispatcher.BeginInvoke(action);

    // -----------------------------------------------------------------------
    // Controller

    private bool HomeHeld() => pad.IsPressed("guide") || (pad.IsPressed("back") && pad.IsPressed("start"));

    private void OnPadDown(string button)
    {
        if (_browsing) _browserActivity = DateTimeOffset.UtcNow;
        if (_browsing && _browser is not null && _mouse?.Active == true && OnBrowserButton(button)) return;
        // Windows doesn't count controller input as activity: without this the
        // display would go dark while someone browses the library with the pad.
        Display.KeepAwake();
        if (Display.WakeIfOff()) return;
        if (_mouse?.Active == true)
        {
            // Mouse mode (signing in to a launcher): guide brings the HUD back, the rest steers the window.
            if (button == "guide" || (button is "back" or "start" && HomeHeld()))
            {
                OnHomeHold(EndMouseMode);
                return;
            }
            _mouse.Button(button);
            return;
        }
        if (button == "guide" || (button is "back" or "start" && HomeHeld()))
        {
            if (_pcMode)
            {
                // Long press only: a short tap in PC mode may be meant for another app.
                OnHomeHold(() => _ = LeavePcModeAsync());
            }
            else if (_window?.IsActive == true)
            {
                // In front already: the UI opens or closes its quick menu.
                Emit("input", new { type = "down", button = "guide" });
                Emit("input", new { type = "up", button = "guide" });
            }
            else if (GuideTapEffective)
            {
                // Console style: a tap goes to the home screen with the game still running,
                // holding on opens the guide menu on top.
                ShowHud("home");
                OnHomeHold(() => ShowHud("quick"));
            }
            else
            {
                // A tap belongs to Steam overlay / Game Bar, holding brings us back.
                OnHomeHold(() => ShowHud("quick"));
            }
            return;
        }
        // Never steer the HUD while a game owns the screen.
        if (!_pcMode && _window?.IsActive == true) Emit("input", new { type = "down", button });
    }

    private void OnHomeHold(Action action)
    {
        _homeHold?.Cancel();
        var cts = _homeHold = new CancellationTokenSource();
        var holdMs = Math.Clamp(config.Current.ReturnHoldMs, 200, 5000);
        Task.Delay(holdMs, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled && HomeHeld()) Ui(action);
        }, TaskScheduler.Default);
    }

    /// <summary>Brings the HUD to the front; target "home" or "quick" (the guide menu).</summary>
    public void ShowHud(string? target = "home")
    {
        // Coming from a running game: freeze it while the HUD is up, if wanted.
        // Frozen only once the HUD really is in front: a suspended full-screen game
        // can't hand over the screen, and if the switch fails it must keep running.
        if (_window?.IsActive != true && config.Current.PauseGameInMenu && _sessions.Current is not null) _pauseOnActivate = true;
        // The user is back in the HUD: clients released for a game start or login are held back again.
        _fgState.ReleaseAll();
        _appsReleased = null;
        ForgetUnlock();
        _window?.BringToFront();
        // No target: the HUD shows what it showed before (back from the browser to the news article).
        if (target is not null) Emit("nav", target);
    }

    // -----------------------------------------------------------------------
    // Running game

    private void StartSessionTracking()
    {
        _sessions.Started += s =>
        {
            plugins.Events.RaiseGameStarted(new GameEvent(s.Item.Id, s.Item.Name, s.Item.InstallDir, 0));
            CaseHud.GameStarted(s.Item.Name, s.Pids.Cast<int?>().FirstOrDefault());
            Ui(() => Emit("session", SessionDto()));
            if (s.External) Ui(() => CheckExternalStart(s));
        };
        _sessions.OtherStarted += s => Ui(() => CheckExternalStart(s));
        _sessions.Ended += (s, minutes) => Ui(() =>
        {
            plugins.Events.RaiseGameEnded(new GameEvent(s.Item.Id, s.Item.Name, s.Item.InstallDir, minutes));
            // Only tells CaseHUD when there is a CaseHUD manager at all (a lease only exists if it was started).
            if (_caseHudLazy is { IsValueCreated: true } caseHud) caseHud.Value.GameEnded();
            GamePause.ResumeAll();
            RestoreProfile();
            _parentalController?.Service.Flush();
            try
            {
                var item = library.AddPlaytime(s.Item.Id, minutes);
                Emit("library", UiBridge.ToDto(library.Items));
                Console.WriteLine($"[session] {item.Name}: {minutes:0.0} min");
                if (minutes >= 1)
                {
                    PostNotification("CouchHUD", item.Name, Strings.Ref("hud.played", MinutesRef(minutes), MinutesRef(item.PlaytimeMinutes)));
                }
            }
            catch (KeyNotFoundException)
            {
                // Uninstalled while running; nothing to credit.
            }
            Emit("session", null);
        });
        _sessionTimer.Tick += (_, _) =>
        {
            try
            {
                _parentalController?.Tick();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[parental] {ex.Message}");
            }
            // The 2-minute unlock runs out without an event.
            SyncLockState();
            // A Steam change that had to wait for the end of a game.
            if (_steamGuardService is { RetryDue: true } && !GameBusy) ApplySteamSettings(release: false);
            // A profile whose game was never seen running (launcher closed, start failed) is undone after a while.
            if (_profileRestore is { } pending && _sessions.Current is null && DateTimeOffset.UtcNow - pending.Applied > TimeSpan.FromMinutes(4)) RestoreProfile();
            // Process snapshots take a moment; never on the UI thread, never twice at once.
            if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
            Task.Run(() =>
            {
                try
                {
                    _sessions.Tick(DateTimeOffset.UtcNow);
                    ObserveClients();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[session] {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _ticking, 0);
                }
            });
        };
        _sessionTimer.Start();
    }

    public object? SessionDto()
    {
        var s = _sessions.Current;
        return s is null ? null : new
        {
            id = s.Item.Id,
            name = s.Item.Name,
            started = s.Started.ToUnixTimeMilliseconds(),
            minutes = s.Minutes(DateTimeOffset.UtcNow),
            paused = GamePause.IsPaused
        };
    }

    public void ResumeGame()
    {
        var s = _sessions.Current ?? throw new InvalidOperationException(Strings.Get("hud.noGameRunning"));
        if (ParentalEnforced && Parental.Service.Expired) throw new InvalidOperationException(Strings.Get("parental.timeUp"));
        ForgetUnlock();
        GamePause.ResumeAll();
        if (!Win32ProcessProbe.Activate(s.Pids)) throw new InvalidOperationException(Strings.Get("hud.cannotFocusGame"));
    }

    /// <summary>A game runs, is paused or is starting: background lookups (artwork) stay quiet.</summary>
    public bool GameBusy => _sessions.Current is not null || GamePause.IsPaused || _sessions.IsPending(DateTimeOffset.UtcNow);

    /// <summary>What the foreground guard needs to know right now (see ForegroundPolicy for the rules).</summary>
    private ForegroundContext ForegroundNow() => new(
        Enabled: Full && config.Current.SuppressCompetitors && !dev,
        // A launch that is still starting counts: the game's own windows (and its client's) must not be fought.
        GameRunning: _sessions.Current is not null || GamePause.IsPaused || _sessions.IsPending(DateTimeOffset.UtcNow),
        MouseMode: _mouse?.Active == true,
        PcMode: _pcMode,
        Browsing: _browsing,
        LockPrompt: _windowsUnlock.Pending || _parentAuth.Pending,
        Suspended: _capturing,
        Released: _fgState.Released(DateTimeOffset.UtcNow),
        // With the couch lock closed (and with parental controls) Steam, Epic & Co. stay down while a game runs
        // (their file dialogs lead out of the couch). Before that the start phase may show a login or update.
        StrictClients: (ParentalEnforced || Locked) && _sessions.Current is not null,
        Locked: Locked && !dev,
        GameStarting: _sessions.Current is null && _sessions.IsPending(DateTimeOffset.UtcNow),
        AsShell: _asShell,
        AppsReleased: _appsReleased is not null);

    /// <summary>Processes of the running game(s): what the locked couch lets stay in front.</summary>
    private IReadOnlyCollection<int> GamePids()
    {
        var pids = new List<int>();
        if (_sessions.Current is { } current) pids.AddRange(current.Pids);
        foreach (var other in _sessions.Others) pids.AddRange(other.Pids);
        return pids;
    }

    private static string ClientName(string family) => family switch
    {
        "steam" => "Steam",
        "epic" => "Epic Games",
        "ea" => "EA app",
        "ubisoft" => "Ubisoft Connect",
        "gog" => "GOG Galaxy",
        "battlenet" => "Battle.net",
        "xbox" => "Xbox",
        "playnite" => "Playnite",
        _ => ""
    };

    /// <summary>The guard gave up on a window (it keeps coming back or can't be minimized): tell the user instead of fighting.</summary>
    private void OnClientNeedsAttention(string family, string process)
    {
        var name = ClientName(family) is { Length: > 0 } known ? known : process;
        PostNotification("CouchHUD", Strings.Ref("hud.needsAttention", name),
            Strings.Ref("hud.needsAttention.body"));
    }

    /// <summary>Once, on upgrade: older versions switched the Game Bar button off without a backup; the original was "not set".</summary>
    private void MigrateGameBar()
    {
        if (config.Current.GameBarMigrated) return;
        try
        {
            var registry = new WindowsRegistry();
            var snapshot = GameBarSnapshot();
            var zero = registry.ReadDword(GameBarGuard.Key, GameBarGuard.Value) == 0;
            if (GameBarGuard.NeedsMigration(false, snapshot.HasPending, zero, config.Current.AcceptedTerms > 0, config.Current.GuideTapInGame))
            {
                // Windows' default is no value; the snapshot taken next remembers exactly that.
                registry.DeleteValue(GameBarGuard.Key, GameBarGuard.Value);
                Console.WriteLine("[guard] game bar: migrated the unsaved change of an older version");
            }
            config.Update(c => c.GameBarMigrated = true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[guard] migration: {ex.Message}");
        }
    }

    private GuardSnapshot GameBarSnapshot() => _gameBar ??= new GuardSnapshot(new WindowsRegistry(), new FileVdfStore(),
        Path.Combine(dataDir, "guard.json"), message => Console.Error.WriteLine($"[guard] {message}"));

    /// <summary>A tap on the Xbox button only reaches us if the Game Bar does not take it (light mode leaves the Game Bar alone).</summary>
    private bool GuideTapEffective => config.Current.GuideTapInGame && !SafeGameBarButton();

    /// <summary>
    /// The Xbox button is ours on the couch: Game Bar off through the guard snapshot (original kept), put back
    /// in PC mode, in light mode, with the option off and on quit. Safe to call at any time.
    /// </summary>
    private void ApplyCompetitorSuppression(bool announce = false, bool release = false)
    {
        ApplySteamSettings(release);
        try
        {
            var snapshot = GameBarSnapshot();
            var want = !release && GameBarGuard.ShouldApply(Full, _pcMode, config.Current.SuppressCompetitors, config.Current.GuideTapInGame, Locked && !dev);
            var wasOpening = want && announce && SafeGameBarButton();
            var errors = want ? snapshot.Apply([GameBarGuard.Change]) : snapshot.Restore();
            if (wasOpening && errors.Count == 0 && !config.Current.GameBarHintShown)
            {
                config.Update(c => c.GameBarHintShown = true);
                PostNotification("CouchHUD", Strings.Ref("hud.xboxButton"), Strings.Ref("hud.xboxButton.body"));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[guard] {ex.Message}");
        }
    }

    /// <summary>
    /// Steam stays out of the couch's way: no Big Picture at its start, the Guide button does not focus it (see SteamGuard).
    /// Written only while Steam is not running; applying may end Steam cleanly at a really quiet moment and start it quietly again.
    /// Back as the user had it in PC mode, in light mode and with the option off as soon as Steam is not running (never a restart for that);
    /// on quit only what can be put back right away, the Run entry does the rest.
    /// </summary>
    private void ApplySteamSettings(bool release)
    {
        try
        {
            var want = !release && !dev && SteamGuard.ShouldApply(Full, _pcMode, config.Current.SuppressCompetitors);
            // On quit nothing waits and nothing restarts: a Steam that runs keeps its state and the Run entry.
            if (release) SteamSettings.ReleaseOnQuit();
            else SteamSettings.Request(want);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[steam-guard] {ex.Message}");
        }
    }

    private static bool SafeGameBarButton()
    {
        try
        {
            return GameBarButton.OpensGameBar;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return true;
        }
    }

    private static void SetGameBarButton(bool on)
    {
        try
        {
            GameBarButton.OpensGameBar = on;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            throw new InvalidOperationException(Strings.Get("hud.gameBarFailed", ex.Message));
        }
    }

    public async Task QuitGameAsync(bool force = false)
    {
        // The entry may be old (the process ended since the last look): look again before anything is ended.
        await Task.Run(() => _sessions.Tick(DateTimeOffset.UtcNow));
        var s = _sessions.Current ?? throw new InvalidOperationException(Strings.Get("hud.noGameRunning"));
        // A frozen game can't save or close its window.
        GamePause.ResumeAll();
        await Win32ProcessProbe.QuitAsync(s.StampsOrEmpty, force);
    }

    // -----------------------------------------------------------------------
    // Parental controls (see Core/Parental and Services/ParentalController.cs)

    private ParentalController? _parentalController;

    // The parents' code in this context is a one-time release with its own code entry: it does not open the
    // couch lock (no 2-minute window) and is used up by the one action it was given for.
    // Bound to what it was asked for (action and entry) and used up by it, see ParentGrant.
    private readonly ParentGrant _grant = new(MonoNow, TimeSpan.FromSeconds(90));
    private readonly WindowsUnlockRequest _parentAuth = new(MonoNow);
    private (string? Action, string? Item) _parentAuthScope;

    private ParentalController Parental => _parentalController ??= new ParentalController(config, dataDir, ParentalKey(), new ParentalHooks(
        Session: () => _sessions.Current,
        Others: () => _sessions.Others,
        Library: () => library.Items,
        Paused: () => _sessions.Current is { } s && GamePause.AllPaused(s.Pids),
        Enforced: () => ParentalEnforced,
        HudActive: () => _window?.IsActive == true,
        Notify: (title, body) => PostNotification(Strings.Ref("notif.app.parental"), title, body, security: true),
        BringHud: BringHudForParents,
        CloseGame: CloseGameForParents,
        LockWindows: () => WindowsLock.Lock(),
        RemoteTime: RemoteTimeAsync,
        ZoneId: () => config.Current.Parental.TimeZoneId,
        Emit: Emit));

    /// <summary>Key for the HMAC over the play time file: random, protected with DPAPI for this Windows user.</summary>
    private byte[]? ParentalKey() => DataKey.Get(dataDir);

    private async Task<DateTimeOffset?> RemoteTimeAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var request = new HttpRequestMessage(HttpMethod.Head, "https://www.microsoft.com/");
        using var response = await http.SendAsync(request, cts.Token);
        return response.Headers.Date;
    }

    /// <summary>Rules can be enforced: the couch lock is on, CouchHud is the shell and not in PC mode.</summary>
    private bool ParentalEnforced => config.Current.Parental.Enabled && LockOn && !_pcMode;

    private bool HasParentCode => config.Current.CouchLock == "code" && config.Current.PasscodeHash.Length > 0
        || config.Current.CouchLock == "windows" && AccountPassword.IsSet();

    /// <summary>The parents' code was given for starting exactly this entry (used up by <see cref="Launch"/>).</summary>
    private bool GrantFor(LibraryItem item) => _grant.Fits("launch", item.Id);

    /// <summary>Changes to the profile need a fresh code every time, never the 2-minute unlock.</summary>
    private void RequireParentGrant(string action = "profile", string? item = null)
    {
        if (!HasParentCode) RequireUnlocked();
        else if (!_grant.TryConsume(action, item)) throw new InvalidOperationException(Strings.Get("parental.codeRequired"));
    }

    /// <summary>The unlock and the release do not survive a game start or a trip back to the HUD.</summary>
    private void ForgetUnlock()
    {
        _unlockedUntil = DateTimeOffset.MinValue;
        _parentCodeChainUntil = DateTimeOffset.MinValue;
        _grant.Clear();
        AdminService.Ops.Cache.Clear();
        SyncLockState();
    }

    /// <summary>
    /// Asks for the parents' code for one action. Code mode: checks the buttons. Windows mode: shows the lock
    /// screen; "parentalAuth" follows once the user is back.
    /// </summary>
    /// <param name="action">What the code is for ("extend", "launch", "profile" …): the release fits only that. Null fits anything.</param>
    /// <param name="item">The entry it is for, if it is for one.</param>
    public async Task<object> ParentalAuthAsync(string[]? sequence, string? action = null, string? item = null)
    {
        if (config.Current.CouchLock == "windows")
        {
            _parentAuthScope = (action, item);
            _parentAuth.Ask();
            if (!WindowsLock.Lock())
            {
                _parentAuth.Cancel();
                return new { ok = false, pending = false };
            }
            return new { ok = false, pending = true };
        }
        // The installed copy: the service checks the parents' code (its own lockout, its own copy of the hash). For an action that the
        // service carries out, its grant is for exactly those operations, each once (review M3); for the HUD's own actions it only checks.
        if (ServiceCodes.Expected && !AdminService.DevMayRelax(dev))
        {
            var ops = ParentOps(action);
            // No buttons: the fallback, an administrator's credential in Windows' own prompt (a locked-out or planted parents' code never
            // shuts the parents out, review M1).
            if (sequence is null) return await ParentByAdminAsync(action, item, ops);
            var check = await AdminService.Ops.VerifyCodeAsync(CodeKinds.Parent, sequence, ops);
            // No parents' code in the service yet (it is set as "both" with the next new code, or by a parent or an administrator, review
            // M-b): the UI offers the administrator's way.
            if (check.Result.Ok && check.Outcome == CodeCheckResults.NoCode) return new { ok = false, blocked = Strings.Get("codes.noParentCode"), admin = "noCode" };
            if (check.Right)
            {
                _grant.Give(action, item);
                return new { ok = true };
            }
            // Locked out: the UI offers the administrator's way.
            if (check.Result.Ok && check.Outcome == CodeCheckResults.LockedOut) return new { ok = false, waitSeconds = Math.Max(1, check.WaitSeconds), admin = "lockedOut" };
            return CodeAnswer(check);
        }
        var wait = _parentLockout.Wait;
        if (wait > TimeSpan.Zero) return new { ok = false, waitSeconds = (int)Math.Ceiling(wait.TotalSeconds) };
        if (sequence is not null && Passcode.Verify(sequence, config.Current.PasscodeHash))
        {
            _parentLockout.Succeeded();
            _grant.Give(action, item);
            return new { ok = true };
        }
        _parentLockout.Failed();
        return new { ok = false, waitSeconds = (int)Math.Ceiling(_parentLockout.Wait.TotalSeconds) };
    }

    /// <summary>
    /// The service operations a parents' approval is for, by the HUD's action (P9 review M3: a parent's grant counts only for these, each
    /// once). The HUD's own actions (extend time, start a locked game, the profile) need none: the service only checks the code.
    /// </summary>
    private static IReadOnlyList<ServiceOperation>? ParentOps(string? action) => action switch
    {
        "autologon" => [ServiceOperation.SetAutoLogon],
        "lockMode" => [ServiceOperation.SetLockMode, ServiceOperation.RemovePasscode],
        // Switching the lock with a new code coming along (review F4: only then may the approval change a code).
        "lockModeCode" => [ServiceOperation.SetLockMode, ServiceOperation.EnrollPasscode, ServiceOperation.ChangePasscode],
        // Switching the parental controls on is followed by setting the code as both (one approval for the whole chain).
        "parentalOn" => [ServiceOperation.ChangePasscode, ServiceOperation.EnrollPasscode],
        "passcode" => [ServiceOperation.ChangePasscode, ServiceOperation.EnrollPasscode],
        "parentalOff" => [ServiceOperation.RemovePasscode],
        _ => null
    };

    /// <summary>The parents' approval through an administrator's credential (Windows' own prompt on the secure desktop).</summary>
    private async Task<object> ParentByAdminAsync(string? action, string? item, IReadOnlyList<ServiceOperation>? ops)
    {
        try
        {
            // The HUD's own actions need no service operation: then a confirmation only, no grant at all (review L-a).
            if (!await AdminService.ObtainAdminGrantAsync(ops ?? [])) return new { ok = false };
        }
        catch (InvalidOperationException ex)
        {
            return new { ok = false, blocked = ex.Message };
        }
        _grant.Give(action, item);
        return new { ok = true };
    }

    private IReadOnlyList<int> ParentalPids() =>
        VerifiedPids((_sessions.Current is { } c ? [c] : Array.Empty<GameSession>()).Concat(_sessions.Others));

    /// <summary>
    /// The pids of the sessions that are still the processes they were (start time and image): a frozen process that only shares
    /// the number with a former game would be a system component.
    /// </summary>
    private static IReadOnlyList<int> VerifiedPids(IEnumerable<GameSession> sessions)
    {
        var stamps = new Dictionary<int, ProcessStamp>();
        foreach (var s in sessions)
        {
            foreach (var (pid, stamp) in s.StampsOrEmpty) stamps[pid] = stamp;
        }
        return SafeTermination.Resolve(stamps, Win32ProcessControl.Instance.List(), Win32ProcessControl.Instance).Select(p => p.Pid).ToList();
    }

    private void BringHudForParents()
    {
        _pauseOnActivate = true;
        ShowHud("home");
        // Already in front: no activation follows that would freeze the game.
        if (_window?.IsActive == true && _sessions.Current is not null && !GamePause.IsPaused)
        {
            GamePause.Pause(ParentalPids());
            Emit("session", SessionDto());
        }
    }

    private async Task CloseGameForParents(string? onlyItem, CancellationToken cancel)
    {
        var stamps = await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            GamePause.ResumeAll();
            // One game that broke the rules only (started outside CouchHud), otherwise everything that runs.
            var sessions = new List<GameSession>();
            if (_sessions.Current is { } current) sessions.Add(current);
            sessions.AddRange(_sessions.Others);
            if (onlyItem is not null) sessions = sessions.Where(x => x.Item.Id == onlyItem).ToList();
            if (sessions.Count == 0)
            {
                if (_sessions.Current is { } c) sessions.Add(c);
                sessions.AddRange(_sessions.Others);
            }
            var all = new Dictionary<int, ProcessStamp>();
            foreach (var x in sessions)
            {
                foreach (var (pid, stamp) in x.StampsOrEmpty) all[pid] = stamp;
            }
            return all;
        });
        // Closes the windows politely (to save), ends the processes after two minutes, unless the parents add time meanwhile.
        await Win32ProcessProbe.QuitAsync(stamps, false, TimeSpan.FromMinutes(2), cancel);
    }

    /// <summary>
    /// A game that runs without having been started through CouchHud (Steam's own client, a launcher, a start before CouchHud or in an
    /// unlocked moment) meets the same rules as one started here: locked list, age rating, time. If it does not pass,
    /// it is handled like time that is up (see <see cref="ParentalController.Block"/>).
    /// </summary>
    private void CheckExternalStart(GameSession session)
    {
        if (!ParentalEnforced) return;
        // The age rating may need a look-up on the network: off the UI thread, the rules themselves are checked on it.
        _ = Task.Run(() => AgeOf(session.Item)).ContinueWith(task => Ui(() =>
        {
            if (!ParentalEnforced || task.IsFaulted) return;
            var check = Parental.CheckLaunch(session.Item, task.Result, grant: false);
            if (!check.Ok) Parental.Block(session, check);
        }), TaskScheduler.Default);
    }

    private int? CachedAge(LibraryItem item) => storeInfo.Cached(item) is { RatingKnown: true } info ? info.RequiredAge : null;

    /// <summary>The age rating for the age limit; looks it up (3 s at most) when the cache has none, so no start path skips it.</summary>
    private int? AgeOf(LibraryItem item)
    {
        var p = config.Current.Parental;
        if (!p.Enabled || p.MaxAge == 0 || item.Type != ItemType.Game) return null;
        if (CachedAge(item) is { } cached) return cached;
        try
        {
            Task.Run(() => storeInfo.GetAsync(item)).Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException ex)
        {
            Console.Error.WriteLine($"[parental] no age rating for {item.Name}: {ex.InnerException?.Message}");
        }
        return CachedAge(item);
    }

    /// <summary>Why the rules are only advice right now: "noLock", "notShell" or "pcMode"; null when enforced.</summary>
    private string? ParentalAdvisory =>
        !config.Current.Parental.Enabled ? null
        : config.Current.CouchLock == "off" ? "noLock"
        : !(Full && _asShell) ? "notShell"
        : _pcMode ? "pcMode"
        : null;

    public object GetParental()
    {
        var p = config.Current.Parental;
        var s = _sessions.Current;
        var counted = s is not null && (s.Item.Type == ItemType.Game || p.CountApps) ? s.Item : null;
        var st = Parental.Service.Status(counted is null ? null : ParentalService.LimitKey(counted));
        return new
        {
            enabled = p.Enabled,
            enforced = ParentalEnforced,
            advisory = ParentalAdvisory,
            hasParentCode = HasParentCode,
            // The parents' code is only worth something on an account with a password (the lock screen, the recovery and a restart lead there).
            codeProblem = !RecoveryRules.ParentalControlsAllowed(AccountPassword.IsSet()) ? "noPassword" : null,
            weekdayMinutes = p.WeekdayMinutes,
            weekendMinutes = p.WeekendMinutes,
            windowStart = p.HasWindow ? p.WindowStartMinute : -1,
            windowEnd = p.HasWindow ? p.WindowEndMinute : -1,
            perGameMinutes = p.PerGameMinutes,
            maxAge = p.MaxAge,
            blockUnknownAge = p.BlockUnknownAge,
            countApps = p.CountApps,
            lockedItems = p.LockedItems,
            remainingSeconds = st.RemainingSeconds,
            limiter = st.Limiter,
            inWindow = st.InWindow,
            usedMinutes = (int)(st.UsedSeconds / 60),
            bonusMinutes = st.BonusMinutes,
            clockJumped = st.ClockJumped,
            tampered = Parental.Service.Tampered,
            timeZoneChanged = p.Enabled && p.TimeZoneId.Length > 0 && p.TimeZoneId != TimeZoneInfo.Local.Id,
            expired = Parental.Service.Expired || Parental.Blocked,
            blocked = Parental.Blocked,
            blockedGame = Parental.BlockedGame,
            blockedMessage = Parental.BlockedMessage,
            closing = Parental.Closing,
            closeFailed = Parental.CloseFailed,
            graceSeconds = (int)ParentalController.AutoClose.TotalSeconds,
            game = counted?.Name
        };
    }

    /// <summary>
    /// Right after the parental controls were switched on (review 3, item 6): the code becomes couch and parents' code in one step ("both"),
    /// with the approval the switch-on asked for (one UAC prompt at most). The new code is entered right before; it replaces the couch code.
    /// </summary>
    public async Task<object> SetParentCodeAsync(string[] sequence)
    {
        // Right after the switch-on (whose parents' approval it rides on); afterwards it is a change of the code like any other: the parents'
        // grant for "passcode" (review F4). The bridge refuses the call on a locked couch: switching parental controls on from there sets no
        // code (the UI says so; set it from an unlocked couch).
        var inChain = MonoNow() < _parentCodeChainUntil;
        _parentCodeChainUntil = DateTimeOffset.MinValue;
        if (!inChain)
        {
            if (HasParentCode) RequireParentGrant("passcode");
            else RequireUnlocked();
        }
        Passcode.Validate(sequence);
        if (!config.Current.Parental.Enabled) throw new InvalidOperationException(Strings.Get("parental.codeRequired"));
        if (ServiceCodes.Expected && !AdminService.DevMayRelax(dev))
        {
            await ServiceCodes.StoreAsync(sequence, parental: true);
            config.Update(c => c.PasscodeHash = Passcode.HeldByService);
            _ = ServiceCodes.RefreshAsync();
        }
        else
        {
            var hash = Passcode.Hash(sequence);
            config.Update(c => c.PasscodeHash = hash);
        }
        return GetSecurity();
    }

    /// <summary>
    /// "Paste from PC" for the secret and long fields of the settings (review 3, item 7): the clipboard's text, trimmed, at most 4096
    /// characters, never logged. A protected call (the bridge refuses it while the couch is locked; asked again here).
    /// </summary>
    public object ReadClipboard()
    {
        RequireUnlocked();
        string? raw;
        try
        {
            raw = Application.Current.Dispatcher.Invoke(() => System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText(System.Windows.TextDataFormat.UnicodeText) : null);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            raw = null;
        }
        var text = ClipboardText.Clean(raw);
        return text is null ? new { ok = false, reason = Strings.Get(raw is null || raw.Trim().Length == 0 ? "clipboard.empty" : "clipboard.unusable") } : new { ok = true, text };
    }

    /// <summary>
    /// One setting of the profile, through the service where it matters: switching the parental controls off removes the parents' code from
    /// the service first (review M2), with the parents' grant asked for it ("parentalOff") or an administrator's.
    /// </summary>
    public async Task<object> SetParentalAsync(string key, System.Text.Json.JsonElement value)
    {
        var off = key == "enabled" && value.ValueKind == System.Text.Json.JsonValueKind.False;
        if (off && config.Current.Parental.Enabled && ServiceCodes.Expected && !AdminService.DevMayRelax(dev))
        {
            if (HasParentCode && !_grant.Fits("parentalOff")) throw new InvalidOperationException(Strings.Get("parental.codeRequired"));
            if (!HasParentCode) RequireUnlocked();
            await ServiceCodes.RemoveAsync(CodeKinds.Parent);
            _ = ServiceCodes.RefreshAsync();
        }
        return SetParental(key, value);
    }

    /// <summary>One setting of the profile. Needs a fresh parents' code each time.</summary>
    public object SetParental(string key, System.Text.Json.JsonElement value)
    {
        RequireParentGrant(key != "enabled" ? "profile" : value.ValueKind == System.Text.Json.JsonValueKind.False ? "parentalOff" : "parentalOn");
        int Number(int min, int max) =>
            value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetInt32(out var n) && n >= min && n <= max
                ? n
                : throw new ArgumentException(Strings.Get("hud.range", key, min, max));
        bool Flag() => value.ValueKind == System.Text.Json.JsonValueKind.True;
        switch (key)
        {
            case "enabled":
            {
                var on = Flag();
                if (on && !RecoveryRules.ParentalControlsAllowed(AccountPassword.IsSet()))
                    throw new InvalidOperationException(Strings.Get("parental.noPassword"));
                if (on && !HasParentCode) throw new InvalidOperationException(Strings.Get("parental.needsLock"));
                if (on) _parentCodeChainUntil = MonoNow().AddMinutes(2);
                config.Update(c =>
                {
                    c.Parental.Enabled = on;
                    if (on)
                    {
                        if (c.Parental.TimeZoneId.Length == 0) c.Parental.TimeZoneId = TimeZoneInfo.Local.Id;
                        c.Parental.LedgerStarted = true;
                    }
                });
                if (on) Parental.Service.Flush();
                break;
            }
            case "weekdayMinutes":
            {
                var n = Number(0, 1440);
                config.Update(c => c.Parental.WeekdayMinutes = n);
                break;
            }
            case "weekendMinutes":
            {
                var n = Number(0, 1440);
                config.Update(c => c.Parental.WeekendMinutes = n);
                break;
            }
            case "perGameMinutes":
            {
                var n = Number(0, 1440);
                config.Update(c => c.Parental.PerGameMinutes = n);
                break;
            }
            case "maxAge":
            {
                var n = Number(0, 21);
                if (n is not (0 or 6 or 12 or 16 or 18)) throw new ArgumentException(Strings.Get("parental.maxAgeInvalid"));
                config.Update(c => c.Parental.MaxAge = n);
                break;
            }
            case "blockUnknownAge":
            {
                var on = Flag();
                config.Update(c => c.Parental.BlockUnknownAge = on);
                break;
            }
            case "countApps":
            {
                var on = Flag();
                config.Update(c => c.Parental.CountApps = on);
                break;
            }
            case "window":
            {
                if (value.ValueKind != System.Text.Json.JsonValueKind.Array || value.GetArrayLength() != 2) throw new ArgumentException(Strings.Get("parental.windowFormat"));
                var start = value[0].GetInt32();
                var end = value[1].GetInt32();
                var off = start < 0 || end < 0;
                if (!off && (start >= 1440 || end >= 1440 || start == end)) throw new ArgumentException(Strings.Get("parental.windowRange"));
                config.Update(c => (c.Parental.WindowStartMinute, c.Parental.WindowEndMinute) = off ? (-1, -1) : (start, end));
                break;
            }
            default:
                throw new ArgumentException(Strings.Get("parental.unknownSetting", key));
        }
        return GetParental();
    }

    public object SetParentalItemLock(string id, bool locked)
    {
        RequireParentGrant("lock", id);
        _ = library.Find(id) ?? throw new KeyNotFoundException($"unknown item {id}");
        config.Update(c =>
        {
            c.Parental.LockedItems.Remove(id);
            if (locked) c.Parental.LockedItems.Add(id);
        });
        return GetParental();
    }

    /// <summary>The counter file was changed or deleted: the parents accept it and counting starts over.</summary>
    public object ParentalReset()
    {
        RequireParentGrant();
        Parental.Service.Ledger.Reset();
        Parental.Service.Flush();
        return GetParental();
    }

    /// <summary>The system clock is right after all (e.g. after a battery change): adopt it.</summary>
    public object ParentalAcceptClock()
    {
        RequireParentGrant();
        Parental.Service.Clock.AcceptSystemTime(force: true);
        return GetParental();
    }

    /// <summary>Windows' time zone was changed on purpose (travel): the day limits follow it from now on.</summary>
    public object ParentalAdoptZone()
    {
        RequireParentGrant();
        config.Update(c => c.Parental.TimeZoneId = TimeZoneInfo.Local.Id);
        return GetParental();
    }

    /// <summary>Asks before the start whether the game may run (and what to tell the user). Looks up the age rating if it matters.</summary>
    public async Task<object> ParentalCheckAsync(string id)
    {
        var item = library.Find(id) ?? throw new KeyNotFoundException($"unknown item {id}");
        var p = config.Current.Parental;
        if (p.Enabled && p.MaxAge > 0 && item.Type == ItemType.Game && CachedAge(item) is null)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await storeInfo.GetAsync(item, cts.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException)
            {
                Console.Error.WriteLine($"[parental] no age rating for {item.Name}: {ex.Message}");
            }
        }
        var c = Parental.CheckLaunch(item, CachedAge(item), GrantFor(item));
        return new { ok = c.Ok, reason = c.Reason, message = c.Message, needsCode = c.NeedsCode, advisory = c.Advisory };
    }

    /// <summary>Parents add time (fresh code): the game goes on or the next start is possible.</summary>
    public object ParentalExtend(int minutes)
    {
        RequireParentGrant("extend");
        // Also calls off a game that is being closed right now (the controller resets its state).
        Parental.Extend(minutes);
        if (_sessions.Current is not null && !Parental.Closing)
        {
            try
            {
                ResumeGame();
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"[parental] resume after extension: {ex.Message}");
            }
        }
        Emit("parental", new { kind = "status" });
        return GetParental();
    }

    /// <summary>"Save and quit" in the time-is-up dialog: asks the game to close, ends it after two minutes.</summary>
    public void ParentalCloseGame()
    {
        if (_sessions.Current is null) return;
        Parental.CloseNow();
    }

    // -----------------------------------------------------------------------
    // Audio and controllers

    private void StartAudio()
    {
        try
        {
            _audio = new AudioService();
            _lastAudioDefault = _audio.Devices().FirstOrDefault(d => d.IsDefault)?.Id;
            _audio.DevicesChanged += () => Ui(OnAudioDevicesChanged);
            _audio.DeviceArrived += id => Ui(() => OnAudioDeviceArrived(id));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[audio] not available: {ex.Message}");
            _audio = null;
        }
    }

    private void OnAudioDeviceArrived(string id)
    {
        if (_audio is null || !config.Current.AutoSwitchHeadphones) return;
        try
        {
            var device = _audio.FindOutput(id);
            if (device is { Kind: "headphones", IsDefault: false }) _audio.SetDefault(id);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[audio] auto switch failed: {ex.Message}");
        }
    }

    private void OnAudioDevicesChanged()
    {
        if (_audio is null) return;
        try
        {
            var state = _audio.GetState();
            var current = state.Devices.FirstOrDefault(d => d.IsDefault);
            if (current is not null && current.Id != _lastAudioDefault)
            {
                _lastAudioDefault = current.Id;
                Emit("toast", Strings.Get("toast.output", current.Name));
            }
            Emit("audio", state);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[audio] {ex.Message}");
        }
    }

    public AudioState GetAudio() => _audio?.GetState() ?? throw new InvalidOperationException(Strings.Get("audio.unavailable"));

    public AudioState SetVolume(int percent)
    {
        var audio = _audio ?? throw new InvalidOperationException(Strings.Get("audio.unavailable"));
        audio.SetVolume(percent);
        return audio.GetState();
    }

    public AudioState SetMute(bool mute)
    {
        var audio = _audio ?? throw new InvalidOperationException(Strings.Get("audio.unavailable"));
        audio.SetMute(mute);
        return audio.GetState();
    }

    public AudioState SetAudioOutput(string id)
    {
        var audio = _audio ?? throw new InvalidOperationException(Strings.Get("audio.unavailable"));
        audio.SetDefault(id);
        _lastAudioDefault = id;
        return audio.GetState();
    }

    /// <summary>Per-app volume: apps with an audio session, the running game first.</summary>
    public object GetAppVolumes()
    {
        var audio = _audio ?? throw new InvalidOperationException(Strings.Get("audio.unavailable"));
        var pids = _sessions.Current?.Pids ?? [];
        return audio.AppVolumes(new HashSet<int>(pids));
    }

    public void SetAppVolume(string id, int percent) =>
        (_audio ?? throw new InvalidOperationException(Strings.Get("audio.unavailable"))).SetAppVolume(id, percent);

    public void SetAppMute(string id, bool mute) =>
        (_audio ?? throw new InvalidOperationException(Strings.Get("audio.unavailable"))).SetAppMute(id, mute);

    public object Pads() => pad.Pads().Select(p => new { index = p.Index, battery = p.Battery }).ToList();

    public void Rumble(string kind)
    {
        if (!config.Current.Rumble) return;
        switch (kind)
        {
            case "select":
                pad.Rumble(0.35, 60);
                break;
            case "bump":
                pad.Rumble(0.2, 40);
                break;
            case "alert":
                pad.Rumble(0.7, 180);
                break;
        }
    }

    // -----------------------------------------------------------------------
    // Bridge API

    /// <summary>Scans again and tells the UI, without anyone waiting.</summary>
    public void RescanLibraryInBackground() =>
        _ = library.ScanAsync().ContinueWith(_ => Ui(() => Emit("library", UiBridge.ToDto(library.Items))), TaskScheduler.Default);

    public Task<IReadOnlyList<LibraryItem>> GetLibraryAsync(bool rescan) => library.GetAsync(rescan);

    public LibraryItem Launch(string id)
    {
        var item = library.Find(id) ?? throw new KeyNotFoundException($"unknown item {id}");
        // A locked couch starts games; apps and websites need the unlock.
        if (Locked && !LockPolicy.MayLaunchLocked(item.Type)) throw new LockedException(Strings.Get("lock.unlockFirst"));
        if (config.Current.Parental.Enabled && (_sessions.Current is not null || _sessions.IsPending(DateTimeOffset.UtcNow)))
            throw new InvalidOperationException(Strings.Get("hud.closeGameFirst"));
        var check = Parental.CheckLaunch(item, AgeOf(item), GrantFor(item));
        if (!check.Ok) throw new InvalidOperationException(check.Message);
        if (check.UsesCode) _grant.TryConsume("launch", item.Id);
        // A website entry opens in the couch browser (it needs the unlock as the browser tile does, which the app check above gave).
        if (item.Launch.Embedded)
        {
            _ = OpenEmbeddedEntryAsync(item);
            return library.MarkLaunched(id);
        }
        // An app or a website can only be started unlocked: it stays released until the HUD is in front again (ShowHud).
        var releaseApp = !LockPolicy.MayLaunchLocked(item.Type);
        ApplyProfile(item);
        // The client of this item may show what the start needs (login, update) until the user is back in the HUD.
        _fgState.Release(ForegroundPolicy.FamilyOfLaunch(item.Source, item.Launch.Kind, item.Launch.Target), DateTimeOffset.UtcNow);
        var isUrl = item.Launch.Kind == LaunchKind.Url;
        var handler = isUrl ? Win32ProcessProbe.HandlerExe(LaunchWindowPicker.SchemeOf(item.Launch.Target)) : null;
        var windowsBefore = isUrl ? Win32ProcessProbe.WindowSnapshot() : null;
        StartClientForLaunch(item);
        Launcher.Launch(item);
        _sessions.Launched(item, DateTimeOffset.UtcNow);
        ForgetUnlock();
        if (releaseApp && LockOn)
        {
            _appsReleased = item.Name;
            Emit("toast", Strings.Get("lock.appReleased", item.Name));
        }
        if (windowsBefore is not null) _ = BringNewWindowForwardAsync(id, handler, windowsBefore, LaunchWindowPicker.SchemeOf(item.Launch.Target), config.Current.MaximizeBrowser);
        return library.MarkLaunched(id);
    }

    private async Task OpenEmbeddedEntryAsync(LibraryItem item)
    {
        try
        {
            await OpenUrlAsync(item.Launch.Target, "entry");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[browser] {item.Id}: {ex.Message}");
            Emit("toast", Strings.Get("browser.openFailed"));
        }
    }

    /// <summary>A web entry opens in Edge (copy-protected streaming) or in the couch browser.</summary>
    public async Task<IReadOnlyList<LibraryItem>> SetEntryEdgeAsync(string id, bool edge)
    {
        library.SetCustomEdge(id, edge);
        return await library.ScanAsync();
    }

    /// <summary>URL/protocol starts: the window may belong to an already running process (new tab in a minimized Edge).</summary>
    private async Task BringNewWindowForwardAsync(string id, string? handlerPath, IReadOnlyList<WindowInfo> before, string? scheme, bool maximizeWeb)
    {
        var exe = LaunchWindowPicker.ExeNameOf(handlerPath);
        var kind = WindowMatchKind.None;
        try
        {
            // The handler's running processes may be the ones that raise the window: let them take the foreground.
            if (exe is not null)
            {
                foreach (var pid in before.Where(w => string.Equals(w.ExeName, exe, StringComparison.OrdinalIgnoreCase)).Select(w => w.Pid).Distinct())
                    Native.NativeMethods.AllowSetForegroundWindow(pid);
            }
            kind = await Win32ProcessProbe.ActivateLaunchWindowAsync(before, exe, TimeSpan.FromSeconds(10), scheme, maximizeWeb);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[launch] {id} window handoff failed: {ex.Message}");
        }
        var label = kind switch { WindowMatchKind.New => "found new", WindowMatchKind.Reused => "reused", _ => "none" };
        Console.WriteLine($"[launch] {id} handler {exe ?? "unknown"}, window {label}");
        if (kind == WindowMatchKind.None) Ui(() => Emit("launch-timeout", null));
    }

    public bool ToggleFavorite(string id) => library.ToggleFavorite(id);

    /// <summary>Everything the detail page shows; store info may be missing when offline.</summary>
    public async Task<object> GetDetailsAsync(string id)
    {
        var item = library.Find(id) ?? throw new KeyNotFoundException($"unknown item {id}");

        var infoTask = storeInfo.GetAsync(item);
        var dir = item.InstallDir;
        var sizeTask = item.SizeOnDisk > 0 || dir is null
            ? Task.FromResult<long?>(item.SizeOnDisk)
            : Task.Run(() =>
            {
                // Folders like C:\Windows would take ages; an unknown size is fine.
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                return DirectorySize.Measure(dir, cts.Token);
            });
        var uninstallTask = Task.Run(() => Uninstaller.Find(item));

        StoreInfo? info = null;
        try
        {
            info = await infoTask;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[hud] store info for {item.Name}: {ex.Message}");
        }

        return new
        {
            id,
            description = info?.Description,
            developers = info?.Developers ?? Array.Empty<string>(),
            publishers = info?.Publishers ?? Array.Empty<string>(),
            releaseDate = info?.ReleaseDate,
            trailers = info?.Trailers ?? Array.Empty<string>(),
            screenshots = info?.Screenshots ?? Array.Empty<string>(),
            genres = info?.Genres ?? Array.Empty<string>(),
            installDir = item.InstallDir,
            sizeBytes = await sizeTask ?? 0,
            canUninstall = await uninstallTask is not null
        };
    }

    /// <summary>Store info only (description, trailers): cheap enough to ask on every focus.</summary>
    public async Task<StoreInfo?> GetStoreInfoAsync(string id)
    {
        var item = library.Find(id) ?? throw new KeyNotFoundException($"unknown item {id}");
        return await storeInfo.GetAsync(item);
    }

    public async Task UninstallAsync(string id)
    {
        var item = library.Find(id) ?? throw new KeyNotFoundException($"unknown item {id}");
        var command = await Task.Run(() => Uninstaller.Find(item))
                      ?? throw new InvalidOperationException(Strings.Get("hud.noUninstaller"));
        Uninstaller.Run(command);
    }

    public object SystemInfo() => new
    {
        platform = "windows",
        version = typeof(Hud).Assembly.GetName().Version?.ToString(3) ?? "dev",
        dev,
        nativeInput = pad.Available,
        isShell = shell.IsInstalledAsShell(),
        sounds = config.Current.Sounds,
        audioMode = config.Current.AudioMode,
        musicVolume = config.Current.MusicVolume,
        guideTapInGame = config.Current.GuideTapInGame,
        integrationMode = config.Current.IntegrationMode,
        acceptedTerms = config.Current.AcceptedTerms,
        setupWizardDone = config.Current.SetupWizardDone,
        screensaverMinutes = config.Current.ScreensaverMinutes,
        screensaverStyle = config.Current.ScreensaverStyle,
        displayOffMinutes = config.Current.DisplayOffMinutes,
        pixelShift = config.Current.PixelShift,
        reduceMotion = config.Current.ReduceMotion,
        pointer = config.Current.Pointer,
        mouseUnlocked = config.Current.MouseUnlocked,
        soundVolume = config.Current.SoundVolume,
        soundSet = config.Current.SoundSet,
        uiHdr = config.Current.UiHdr,
        hdrOn = SafeHdrOn(),
        trailerPreviews = config.Current.TrailerPreviews,
        homeAssistant = _home.Configured,
        weather = config.Current.WeatherLocation is not null,
        assistant = AssistantConfigured,
        skin = config.Current.Skin,
        language = CouchHud.Core.Localization.Strings.Language,
        languageSetting = config.Current.Language
    };

    public bool SetShell(bool install)
    {
        if (install) RequireFull();
        if (!install) RequireUnlocked();
        if (install) shell.InstallAsShell();
        else shell.UninstallAsShell();
        SignInLock.SyncInBackground(dataDir);
        return shell.IsInstalledAsShell();
    }

    // -----------------------------------------------------------------------
    // Settings and library management

    /// <param name="forUi">False for assistant tools: the MCP token must not travel to a model provider.</param>
    public object GetSettings(bool forUi = true)
    {
        var c = config.Current;
        return new
        {
            sounds = c.Sounds,
            audioMode = c.AudioMode,
            musicVolume = c.MusicVolume,
            guideTapInGame = c.GuideTapInGame,
            suppressCompetitors = c.SuppressCompetitors,
            integrationMode = c.IntegrationMode,
            expertMode = c.ExpertMode,
            bluetoothAutoOn = c.BluetoothAutoOn,
            language = c.Language,
            closeExplorerInLight = c.CloseExplorerInLight,
            selfUpdateCheck = c.SelfUpdateCheck,
            caseHudInstallAutostart = c.CaseHudInstallAutostart,
            caseHudInstallFps = c.CaseHudInstallFps,
            caseHudInstallApi = c.CaseHudInstallApi,
            caseHudGamingLease = c.CaseHudGamingLease,
            screensaverMinutes = c.ScreensaverMinutes,
            screensaverStyle = c.ScreensaverStyle,
            displayOffMinutes = c.DisplayOffMinutes,
            pixelShift = c.PixelShift,
            reduceMotion = c.ReduceMotion,
            pointer = c.Pointer,
            mouseUnlocked = c.MouseUnlocked,
            pauseGameInMenu = c.PauseGameInMenu,
            gameBarButton = SafeGameBarButton(),
            soundVolume = c.SoundVolume,
            soundSet = c.SoundSet,
            uiHdr = c.UiHdr,
            rumble = c.Rumble,
            autoSwitchHeadphones = c.AutoSwitchHeadphones,
            trailerPreviews = c.TrailerPreviews,
            returnHoldMs = c.ReturnHoldMs,
            runStartupApps = c.RunStartupApps,
            killExplorerOnReturn = KillExplorerOnReturn,
            closePcAppsOnReturn = c.ClosePcAppsOnReturn,
            maximizeBrowser = c.MaximizeBrowser,
            nightMode = c.NightMode,
            nightModeStrength = c.NightModeStrength,
            // Never send secrets back to the UI, only whether one is set.
            steamGridDbKeySet = !string.IsNullOrWhiteSpace(c.SteamGridDbApiKey),
            onlineArtwork = c.OnlineArtwork,
            artworkCacheMb = (int)((artwork.Images?.TotalBytes ?? 0) / (1024 * 1024)),
            weatherPlace = c.WeatherLocation,
            homeAssistantUrl = c.HomeAssistantUrl,
            homeAssistantTokenSet = !string.IsNullOrWhiteSpace(c.HomeAssistantToken),
            homeAssistantPinned = c.HomeAssistantPinned,
            aiProvider = c.AiProvider,
            aiModel = c.AiModel,
            aiModels = ChatProviders.Models,
            anthropicKeySet = !string.IsNullOrWhiteSpace(c.AnthropicApiKey),
            openAiKeySet = !string.IsNullOrWhiteSpace(c.OpenAiApiKey),
            mcpEnabled = c.McpEnabled,
            mcpLan = c.McpLan,
            mcpPort = c.McpPort,
            // Shown so it can be copied into the client's config; it only ever leaves the PC that way.
            // Not for a locked couch: the token would let any program on the network steer CouchHud.
            mcpToken = forUi && !Locked ? Secrets.Unprotect(c.McpToken) : null,
            mcpUrls = McpHost.Urls(c.McpPort, c.McpLan),
            remoteEnabled = c.RemoteEnabled,
            mirrorNotifications = c.MirrorNotifications,
            notificationStatus = _windowsNotifications?.Status ?? "off",
            isShell = shell.IsInstalledAsShell(),
            configPath = config.FilePath,
            logPath = Log.Directory
        };
    }

    /// <summary>Whitelisted settings only; the UI can't write arbitrary config keys.</summary>
    public async Task SetSettingAsync(string key, System.Text.Json.JsonElement value)
    {
        bool Bool() => value.ValueKind switch
        {
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            _ => throw new ArgumentException(Strings.Get("hud.expectsBool", key))
        };

        switch (key)
        {
            case "audioMode":
                var audioMode = value.GetString();
                if (audioMode is not ("off" or "ui" or "music")) throw new ArgumentException(Strings.Get("hud.soundInvalid"));
                config.UpdateOrThrow(c =>
                {
                    c.AudioMode = audioMode;
                    c.Sounds = audioMode != "off";
                });
                break;
            case "screensaverMinutes":
                var saver = Math.Clamp(value.GetInt32(), 0, 240);
                config.UpdateOrThrow(c => c.ScreensaverMinutes = saver);
                break;
            case "screensaverStyle":
                var style = value.GetString();
                if (style is not ("art" or "clock" or "dim" or "black")) throw new ArgumentException(Strings.Get("hud.styleInvalid"));
                config.UpdateOrThrow(c => c.ScreensaverStyle = style);
                break;
            case "displayOffMinutes":
                var off = Math.Clamp(value.GetInt32(), 0, 480);
                config.UpdateOrThrow(c => c.DisplayOffMinutes = off);
                break;
            case "caseHudInstallAutostart":
                var chAutostart = Bool();
                config.UpdateOrThrow(c => c.CaseHudInstallAutostart = chAutostart);
                break;
            case "caseHudInstallFps":
                var chFps = Bool();
                config.UpdateOrThrow(c => c.CaseHudInstallFps = chFps);
                break;
            case "caseHudInstallApi":
                var chApi = Bool();
                config.UpdateOrThrow(c => c.CaseHudInstallApi = chApi);
                break;
            case "caseHudGamingLease":
                var chLease = Bool();
                config.UpdateOrThrow(c => c.CaseHudGamingLease = chLease);
                break;
            case "bluetoothAutoOn":
                var btAuto = Bool();
                config.UpdateOrThrow(c => c.BluetoothAutoOn = btAuto);
                break;
            case "selfUpdateCheck":
                var selfCheck = Bool();
                config.UpdateOrThrow(c => c.SelfUpdateCheck = selfCheck);
                break;
            case "pixelShift":
                var shift = Bool();
                config.UpdateOrThrow(c => c.PixelShift = shift);
                break;
            case "reduceMotion":
                var calm = Bool();
                config.UpdateOrThrow(c => c.ReduceMotion = calm);
                break;
            case "pointer":
                var pointer = value.GetString();
                if (pointer is not ("skin" or "system" or "mouse")) throw new ArgumentException("pointer: skin, system or mouse");
                config.UpdateOrThrow(c => c.Pointer = pointer == "mouse" && !c.MouseUnlocked ? "skin" : pointer);
                break;
            case "mouseUnlocked":
                var mouseOn = Bool();
                config.UpdateOrThrow(c =>
                {
                    c.MouseUnlocked = mouseOn;
                    if (!mouseOn && c.Pointer == "mouse") c.Pointer = "skin";
                });
                break;
            case "closeExplorerInLight":
                var closeExplorer = Bool();
                config.UpdateOrThrow(c => c.CloseExplorerInLight = closeExplorer);
                if (!Full && !_pcMode)
                {
                    if (closeExplorer) HideTaskbar();
                    else RestoreTaskbar();
                }
                break;
            case "acceptedTerms":
                var terms = value.GetInt32();
                config.UpdateOrThrow(c => c.AcceptedTerms = terms);
                break;
            case "setupWizardDone":
                // Only a marker: the assistant itself changes nothing, every choice in it goes through the setting it belongs to.
                // Not a harmless setting: a locked couch cannot mark it (the assistant asks for the code first, like the settings).
                var wizard = Math.Clamp(value.GetInt32(), 0, 1000);
                config.UpdateOrThrow(c => c.SetupWizardDone = wizard);
                break;
            case "guideTapInGame":
                var tap = Bool();
                config.UpdateOrThrow(c => c.GuideTapInGame = tap);
                // The Game Bar would open on the same tap otherwise (full integration on the couch).
                ApplyCompetitorSuppression();
                break;
            case "suppressCompetitors":
                var suppress = Bool();
                config.UpdateOrThrow(c => c.SuppressCompetitors = suppress);
                ApplyCompetitorSuppression();
                break;
            case "pauseGameInMenu":
                var pause = Bool();
                config.UpdateOrThrow(c => c.PauseGameInMenu = pause);
                if (!pause)
                {
                    GamePause.ResumeAll();
                    Emit("session", SessionDto());
                }
                break;
            case "gameBarButton":
                var gameBar = Bool();
                // Both can't have the button: the Game Bar wins, the tap goes back to "hold".
                if (gameBar)
                {
                    config.UpdateOrThrow(c => c.GuideTapInGame = false);
                    ApplyCompetitorSuppression(); // snapshot restored first, then the explicit choice below
                }
                SetGameBarButton(gameBar);
                break;
            case "musicVolume":
                var music = Math.Clamp(value.GetInt32(), 0, 100);
                config.UpdateOrThrow(c => c.MusicVolume = music);
                break;
            case "soundVolume":
                var volume = Math.Clamp(value.GetInt32(), 0, 100);
                config.UpdateOrThrow(c => c.SoundVolume = volume);
                break;
            case "soundSet":
                var set = value.GetString() ?? "";
                if (set.Length > 0 && set is not ("default" or "ps4" or "xbox" or "cyberpunk" or "nova")) throw new ArgumentException(Strings.Get("hud.unknownSoundSet", set));
                config.UpdateOrThrow(c => c.SoundSet = set);
                break;
            case "uiHdr":
                var uiHdr = value.GetString();
                if (uiHdr is not ("off" or "auto" or "on")) throw new ArgumentException(Strings.Get("hud.hdrInvalid"));
                config.UpdateOrThrow(c => c.UiHdr = uiHdr);
                ApplyUiHdr();
                break;
            case "sounds":
                // Older callers (assistant, MCP): on/off maps onto the audio mode the UI uses.
                var soundsOn = Bool();
                config.UpdateOrThrow(c =>
                {
                    c.Sounds = soundsOn;
                    c.AudioMode = soundsOn ? (c.AudioMode == "off" ? "ui" : c.AudioMode) : "off";
                });
                break;
            case "expertMode":
                config.UpdateOrThrow(c => c.ExpertMode = Bool());
                break;
            case "language":
                var language = value.GetString();
                if (language is not ("auto" or "de" or "en")) throw new ArgumentException("Language: auto, de or en");
                config.UpdateOrThrow(c => c.Language = language);
                CouchHud.Core.Localization.Strings.Use(language);
                plugins.Events.RaiseLanguageChanged(CouchHud.Core.Localization.Strings.Language);
                break;
            case "rumble":
                config.UpdateOrThrow(c => c.Rumble = Bool());
                break;
            case "runStartupApps":
                config.UpdateOrThrow(c => c.RunStartupApps = Bool());
                break;
            case "killExplorerOnReturn":
                config.UpdateOrThrow(c => c.KillExplorerOnReturn = Bool());
                break;
            case "nightMode":
                var nightMode = value.GetString();
                if (nightMode is not ("off" or "on" or "evening")) throw new ArgumentException(Strings.Get("hud.nightInvalid"));
                config.UpdateOrThrow(c => c.NightMode = nightMode);
                _night.Apply();
                break;
            case "nightModeStrength":
                var strength = Math.Clamp(value.GetInt32(), 0, 100);
                config.UpdateOrThrow(c => c.NightModeStrength = strength);
                _night.Apply();
                break;
            case "maximizeBrowser":
                config.UpdateOrThrow(c => c.MaximizeBrowser = Bool());
                break;
            case "closePcAppsOnReturn":
                config.UpdateOrThrow(c => c.ClosePcAppsOnReturn = Bool());
                break;
            case "onlineArtwork":
                var online = Bool();
                config.UpdateOrThrow(c => c.OnlineArtwork = online);
                artwork.Online = online;
                if (online) RescanLibraryInBackground();
                break;
            case "clearArtworkCache":
                library.ClearArtworkCache();
                break;
            case "trailerPreviews":
                config.UpdateOrThrow(c => c.TrailerPreviews = Bool());
                break;
            case "autoSwitchHeadphones":
                config.UpdateOrThrow(c => c.AutoSwitchHeadphones = Bool());
                break;
            case "returnHoldMs":
                var ms = Math.Clamp(value.GetInt32(), 200, 5000);
                config.UpdateOrThrow(c => c.ReturnHoldMs = ms);
                break;
            case "weatherLocation":
                var place = value.ValueKind == System.Text.Json.JsonValueKind.Object
                    ? System.Text.Json.JsonSerializer.Deserialize<Place>(value.GetRawText(), UiBridge.Json)
                    : null;
                config.UpdateOrThrow(c => c.WeatherLocation = place);
                break;
            case "homeAssistantUrl":
                var url = value.GetString()?.Trim().TrimEnd('/') ?? "";
                if (url.Length > 0 && !Uri.TryCreate(url, UriKind.Absolute, out _)) throw new ArgumentException(Strings.Get("hud.needFullAddress"));
                // The token belongs to the server it was made for: another address asks for a new one,
                // otherwise a changed address would send the old token to whoever answers there.
                var sameServer = HomeAssistantPolicy.SameServer(config.Current.HomeAssistantUrl, url);
                config.UpdateOrThrow(c =>
                {
                    c.HomeAssistantUrl = url;
                    if (!sameServer) c.HomeAssistantToken = "";
                });
                _home.BaseUrl = url;
                if (!sameServer) _home.Token = "";
                break;
            case "homeAssistantToken":
                var token = value.GetString()?.Trim() ?? "";
                config.UpdateOrThrow(c => c.HomeAssistantToken = Secrets.Protect(token));
                _home.Token = token;
                break;
            case "steamGridDbApiKey":
                var apiKey = value.GetString()?.Trim() ?? "";
                config.UpdateOrThrow(c => c.SteamGridDbApiKey = Secrets.Protect(apiKey));
                artwork.ApiKey = apiKey;
                // Look again with the new key in the background.
                _ = library.ScanAsync().ContinueWith(t => Ui(() => Emit("library", UiBridge.ToDto(library.Items))), TaskScheduler.Default);
                break;
            case "aiProvider":
                var provider = value.GetString() ?? "";
                if (provider.Length > 0 && !ChatProviders.Models.ContainsKey(provider)) throw new ArgumentException(Strings.Get("hud.unknownProvider", provider));
                config.UpdateOrThrow(c =>
                {
                    if (c.AiProvider != provider) c.AiModel = "";
                    c.AiProvider = provider;
                });
                break;
            case "aiModel":
                var model = value.GetString()?.Trim() ?? "";
                config.UpdateOrThrow(c => c.AiModel = model);
                break;
            case "anthropicApiKey":
                var anthropicKey = value.GetString()?.Trim() ?? "";
                config.UpdateOrThrow(c => c.AnthropicApiKey = Secrets.Protect(anthropicKey));
                break;
            case "openAiApiKey":
                var openAiKey = value.GetString()?.Trim() ?? "";
                config.UpdateOrThrow(c => c.OpenAiApiKey = Secrets.Protect(openAiKey));
                break;
            case "mcpEnabled":
                var enabled = Bool();
                config.UpdateOrThrow(c =>
                {
                    c.McpEnabled = enabled;
                    if (enabled && string.IsNullOrEmpty(Secrets.Unprotect(c.McpToken))) c.McpToken = Secrets.Protect(McpHost.NewToken());
                });
                try
                {
                    await ApplyMcpAsync(throwOnError: true);
                }
                catch (InvalidOperationException)
                {
                    config.UpdateOrThrow(c => c.McpEnabled = false);
                    throw;
                }
                if (!enabled) _ = DropFirewallRuleIfUnusedAsync();
                break;
            case "mirrorNotifications":
                var mirror = Bool();
                config.UpdateOrThrow(c => c.MirrorNotifications = mirror);
                if (_windowsNotifications is not null)
                {
                    if (!mirror) _windowsNotifications.Stop();
                    else if (await _windowsNotifications.StartAsync() != "allowed")
                    {
                        config.UpdateOrThrow(c => c.MirrorNotifications = false);
                        throw new InvalidOperationException(_windowsNotifications.Status == "denied"
                            ? Strings.Get("notifications.denied")
                            : Strings.Get("notifications.notYet"));
                    }
                }
                break;
            case "remoteEnabled":
                var remote = Bool();
                config.UpdateOrThrow(c => c.RemoteEnabled = remote);
                try
                {
                    await ApplyRemoteAsync(throwOnError: true);
                }
                catch (InvalidOperationException)
                {
                    config.UpdateOrThrow(c => c.RemoteEnabled = false);
                    throw;
                }
                if (!remote) _ = DropFirewallRuleIfUnusedAsync();
                break;
            case "mcpLan":
                var lan = Bool();
                config.UpdateOrThrow(c => c.McpLan = lan);
                await ApplyMcpAsync(throwOnError: true);
                if (!lan) _ = DropFirewallRuleIfUnusedAsync();
                break;
            case "mcpToken":
                // Any value means "make a new one"; the old token stops working at once.
                config.UpdateOrThrow(c => c.McpToken = Secrets.Protect(McpHost.NewToken()));
                await ApplyMcpAsync(throwOnError: true);
                break;
            default:
                throw new ArgumentException($"unknown setting {key}");
        }
    }

    // -----------------------------------------------------------------------
    // Assistant and MCP

    private bool AssistantConfigured => config.Current.AiProvider switch
    {
        ChatProviders.Anthropic => !string.IsNullOrWhiteSpace(config.Current.AnthropicApiKey),
        ChatProviders.OpenAi => !string.IsNullOrWhiteSpace(config.Current.OpenAiApiKey),
        _ => false
    };

    public object AssistantInfo()
    {
        var c = config.Current;
        return new
        {
            configured = AssistantConfigured,
            provider = c.AiProvider,
            model = string.IsNullOrWhiteSpace(c.AiModel) && ChatProviders.Models.TryGetValue(c.AiProvider, out var models) ? models[0] : c.AiModel
        };
    }

    public async Task<string> AskAssistantAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException(Strings.Get("assistant.emptyQuestion"));
        var c = config.Current;
        var key = Secrets.Unprotect(c.AiProvider == ChatProviders.OpenAi ? c.OpenAiApiKey : c.AnthropicApiKey);
        if (!AssistantConfigured || string.IsNullOrEmpty(key)) throw new InvalidOperationException(Strings.Get("assistant.needsProvider"));

        // A new provider, model or key starts a fresh conversation.
        var identity = $"{c.AiProvider}|{c.AiModel}|{key.GetHashCode()}";
        if (_assistant is null || _assistantFor != identity)
        {
            _assistant = new AssistantSession(ChatProviders.Create(_aiHttp, c.AiProvider, key, c.AiModel), new HudTools(this), () => AllTools);
            _assistantFor = identity;
        }

        _assistantCts?.Cancel();
        _assistantCts = new CancellationTokenSource();
        try
        {
            return await _assistant.AskAsync(text, ConfirmAsync,
                call => Emit("assistant", new { type = "tool", name = call.Name }),
                _assistantCts.Token);
        }
        catch (AssistantException ex)
        {
            throw new InvalidOperationException(ex.Message);
        }
        catch (TaskCanceledException)
        {
            throw new InvalidOperationException(Strings.Get("assistant.timeout"));
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException(Strings.Get("assistant.noConnection"));
        }
    }

    /// <summary>Asks the user on screen; the UI answers through <see cref="ConfirmAssistant"/>.</summary>
    private Task<bool> ConfirmAsync(ToolCall call, string text) =>
        RequestConfirmation(text, ToolCatalog.IsDangerous(call.Name), focusCancel: true, ConfirmTimeout);

    // One question at a time, in the order they came: a second one (assistant and MCP at once, a Bluetooth PIN) waits
    // instead of replacing the first. Nobody answering means no.
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromMinutes(2);
    private const int MaxQueuedConfirmations = 8;
    private sealed record PendingConfirm(string Id, string Text, bool Danger, bool FocusCancel, TimeSpan Timeout);
    private readonly object _confirmLock = new();
    private readonly Queue<PendingConfirm> _confirmQueue = new();
    private string? _confirmShowing;

    /// <summary>A question is on the TV (or waiting for it): the phone's keys must not answer it.</summary>
    private bool ConfirmationOpen
    {
        get
        {
            lock (_confirmLock) return _confirmShowing is not null || _confirmQueue.Count > 0;
        }
    }

    private Task<bool> RequestConfirmation(string text, bool danger, bool focusCancel = false, TimeSpan? timeout = null)
    {
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_confirmLock)
        {
            if (_confirmQueue.Count >= MaxQueuedConfirmations) return Task.FromResult(false);
            _confirmations[id] = tcs;
            _confirmQueue.Enqueue(new PendingConfirm(id, text, danger, focusCancel, timeout ?? ConfirmTimeout));
        }
        ShowNextConfirmation();
        return tcs.Task;
    }

    private void ShowNextConfirmation()
    {
        PendingConfirm? next;
        lock (_confirmLock)
        {
            if (_confirmShowing is not null || !_confirmQueue.TryDequeue(out next)) return;
            _confirmShowing = next.Id;
        }
        Ui(() => Emit("assistant", new { type = "confirm", id = next.Id, text = next.Text, danger = next.Danger, focusCancel = next.FocusCancel }));
        _ = Task.Delay(next.Timeout).ContinueWith(_ => Ui(() =>
        {
            if (!FinishConfirmation(next.Id, false)) return;
            Emit("assistant", new { type = "confirmCancel", id = next.Id });
        }), TaskScheduler.Default);
    }

    /// <summary>Answers a question (once) and brings up the next one. False if it was already answered.</summary>
    private bool FinishConfirmation(string id, bool ok)
    {
        TaskCompletionSource<bool>? tcs;
        lock (_confirmLock)
        {
            if (!_confirmations.Remove(id, out tcs)) return false;
            if (_confirmShowing == id) _confirmShowing = null;
        }
        tcs.TrySetResult(ok);
        ShowNextConfirmation();
        return true;
    }

    /// <summary>MCP changes need an A on the TV too; nobody answering within a minute means no.</summary>
    private async Task<bool> ConfirmExternalAsync(ToolSpec spec, System.Text.Json.JsonElement args)
    {
        var text = Strings.Get("external.request", new HudTools(this).Describe(spec.Name, args));
        var ok = await RequestConfirmation(text, danger: true, focusCancel: true, TimeSpan.FromMinutes(1));
        if (!ok) Emit("toast", Strings.Get("external.denied"));
        return ok;
    }

    public void ConfirmAssistant(string id, bool ok) => FinishConfirmation(id, ok);

    public void ResetAssistant()
    {
        _assistantCts?.Cancel();
        List<string> open;
        lock (_confirmLock)
        {
            open = [.. _confirmations.Keys];
            _confirmQueue.Clear();
        }
        foreach (var id in open)
        {
            FinishConfirmation(id, false);
            Emit("assistant", new { type = "confirmCancel", id });
        }
        _assistant?.Reset();
    }

    private async Task ApplyMcpAsync(bool throwOnError = false)
    {
        var c = config.Current;
        try
        {
            await _mcp.StopAsync();
            var token = Secrets.Unprotect(c.McpToken);
            if (!c.McpEnabled || string.IsNullOrEmpty(token)) return;
            var server = new McpServer(new HudTools(this), typeof(Hud).Assembly.GetName().Version?.ToString(3) ?? "dev", () => AllTools)
            {
                // Changes from outside are confirmed on the TV, like the assistant's.
                Confirm = (spec, args) => Application.Current.Dispatcher.InvokeAsync(() => ConfirmExternalAsync(spec, args)).Task.Unwrap()
            };
            await _mcp.StartAsync(server, c.McpPort, c.McpLan, token);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            Console.Error.WriteLine($"[mcp] could not start: {ex.Message}");
            if (throwOnError) throw new InvalidOperationException(Strings.Get("hud.mcpFailed", c.McpPort));
        }
    }

    // -----------------------------------------------------------------------
    // Weather and Home Assistant

    public async Task<WeatherReport?> GetWeatherAsync()
    {
        var place = config.Current.WeatherLocation;
        return place is null ? null : await _weather.GetAsync(place);
    }

    public Task<IReadOnlyList<NewsItem>> GetNewsAsync() => _news.GetAsync(GetFeeds());

    public IReadOnlyList<NewsFeed> GetFeeds() => NewsService.Effective(config.Current.NewsFeeds, Strings.Language);

    /// <summary>Keeps what the user sees as their own list the moment they change it (default feeds follow the language until then).</summary>
    private void OwnFeeds() => config.Update(c => c.NewsFeeds = GetFeeds().Select(f => new NewsFeed { Name = f.Name, Url = f.Url }).ToList());

    public void AddFeed(string name, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) throw new ArgumentException(Strings.Get("hud.needFeedAddress"));
        OwnFeeds();
        config.Update(c => c.NewsFeeds.Add(new NewsFeed { Name = string.IsNullOrWhiteSpace(name) ? uri.Host : name.Trim(), Url = url.Trim() }));
    }

    public void RemoveFeed(string url)
    {
        OwnFeeds();
        config.Update(c => c.NewsFeeds.RemoveAll(f => f.Url == url));
    }

    public Task<IReadOnlyList<Place>> SearchPlacesAsync(string query) => _weather.SearchAsync(query);

    // -----------------------------------------------------------------------
    // Notifications and Bluetooth

    private void StartNotifications()
    {
        plugins.Notifier = (plugin, title, body) => Ui(() => PostNotification(plugin, title, body));
        // A plugin's link opens in the couch browser, and only on the unlocked couch (the browser leads to Windows dialogs).
        plugins.UrlOpener = (plugin, url) =>
        {
            if (!IsUnlocked) return false;
            Ui(async () =>
            {
                try
                {
                    await OpenUrlAsync(url, $"plugin:{plugin}");
                }
                catch (Exception ex) when (ex is ArgumentException or LockedException or InvalidOperationException)
                {
                    Console.Error.WriteLine($"[browser] plugin {plugin}: {ex.Message}");
                }
            });
            return true;
        };
        _notifications.Added += n => Ui(() => Emit("notification", Shown(n)));
        _windowsNotifications = new WindowsNotifications(_notifications);
        if (config.Current.MirrorNotifications) _ = _windowsNotifications.StartAsync();
    }

    /// <summary>A CouchHud notification (game ended, updates, pairing …).</summary>
    /// <param name="security">The user must see it even while a page asks for full attention (setup assistant, first-start notice), see Notification.Security.</param>
    private void PostNotification(string app, string title, string body, bool security = false) =>
        _notifications.Add(new Notification(Guid.NewGuid().ToString("N"), "couchhud", app, title, body, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Security: security));

    /// <summary>Stored references (key + arguments) become text in the language of the moment.</summary>
    private static Notification Shown(Notification n) => n with { App = Strings.Render(n.App), Title = Strings.Render(n.Title), Body = Strings.Render(n.Body) };

    private static string MinutesRef(double minutes) =>
        minutes < 60 ? Strings.Ref("time.minutes", Math.Max(1, (int)Math.Round(minutes))) : Strings.Ref("time.hoursMinutes", (int)(minutes / 60), (int)Math.Round(minutes % 60));

    private static string FormatMinutes(double minutes) =>
        minutes < 60 ? Strings.Get("time.minutes", Math.Max(1, (int)Math.Round(minutes))) : Strings.Get("time.hoursMinutes", (int)(minutes / 60), (int)Math.Round(minutes % 60));

    public object GetNotifications() => new
    {
        items = _notifications.Items.Select(Shown).ToList(),
        unread = _notifications.Unread,
        windows = _windowsNotifications?.Status ?? "off"
    };

    public void MarkNotificationsRead() => _notifications.MarkAllRead();

    public void ClearNotifications() => _notifications.Clear();

    /// <summary>
    /// The radio, whether a Bluetooth controller is connected (switching off would cut it off), and the automatic switch-on. Open
    /// while locked: the code prompt shows it when no controller is there.
    /// </summary>
    public async Task<object> GetBluetoothStateAsync()
    {
        var (available, on) = await _bluetooth.StateAsync();
        int paired = 0, connected = 0;
        if (available)
        {
            try
            {
                (paired, connected) = await _bluetooth.ControllersAsync();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[bluetooth] paired controllers: {ex.Message}");
            }
        }
        return new
        {
            available,
            on,
            // "Comes back by itself" is only said while it will: not after "leave it off" or after giving up.
            autoOn = config.Current.BluetoothAutoOn && _bluetoothRecovery?.Paused != true,
            controllersPaired = paired,
            controllerConnected = connected > 0,
            pads = pad.Pads().Count,
            countdown = _bluetoothRecovery?.CountdownRunning == true
        };
    }

    private BluetoothRecoveryController? _bluetoothRecovery;

    /// <summary>Bluetooth back on by itself while a Bluetooth controller is paired and no other one is connected (see BluetoothRecoveryController).</summary>
    private void StartBluetoothGuard()
    {
        _bluetoothRecovery = new BluetoothRecoveryController(_bluetooth, () => config.Current.BluetoothAutoOn, () => pad.Pads().Count,
            () => RealInputWithin(TimeSpan.FromSeconds(60)),
            (phase, seconds) => Emit("bluetoothRecovery", seconds is { } s ? new { phase, seconds = s } : new { phase }),
            Ui, () => TimeSpan.FromMilliseconds(Environment.TickCount64), (wait, token) => Task.Delay(wait, token),
            text => Console.WriteLine($"[bluetooth] {text}"),
            () => config.Current.BluetoothPairedControllers, count => config.Update(c => c.BluetoothPairedControllers = count));
        _bluetoothRecovery.Start();
    }

    /// <summary>
    /// The countdown before the automatic switch-on: "leave it off" (until Bluetooth is on again or CouchHUD restarts) or "now".
    /// True when it was left off; false when the countdown had already ended (the radio comes or came back on).
    /// </summary>
    public bool AnswerBluetoothRecovery(bool keepOff)
    {
        if (keepOff) return _bluetoothRecovery?.Decline() == true;
        _bluetoothRecovery?.SkipCountdown();
        return false;
    }

    public Task<IReadOnlyList<BluetoothDeviceInfo>> ScanBluetoothAsync() => _bluetooth.ScanAsync(TimeSpan.FromSeconds(6));

    public Task<string> PairBluetoothAsync(string id) => _bluetooth.PairAsync(id);

    public Task<string> UnpairBluetoothAsync(string id) => _bluetooth.UnpairAsync(id);

    /// <summary>
    /// Bluetooth on or off from CouchHUD's own page. Off with another way to work the couch (a controller that is not on Bluetooth,
    /// or the switch-off confirmed with keyboard or mouse: <paramref name="source"/> from the UI) stays off; without one the automatic
    /// switch-on brings it back, as the confirmation said. Only the source of this very confirmation counts, not the last input of
    /// Windows: CouchHUD's own SendInput (controller as mouse, wake-up moves) would look like keyboard or mouse there.
    /// </summary>
    public async Task SetBluetoothAsync(bool on, string? source = null)
    {
        var leftOff = false;
        if (!on && _bluetoothRecovery is { } recovery)
        {
            var bluetoothPads = 0;
            try
            {
                bluetoothPads = (await _bluetooth.ControllersAsync()).Connected;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[bluetooth] connected controllers: {ex.Message}");
            }
            var otherInput = pad.Pads().Count > bluetoothPads || source is "keyboard" or "mouse";
            leftOff = recovery.UserSwitchingOff(otherInput);
        }
        try
        {
            await _bluetooth.SetRadioAsync(on);
        }
        catch
        {
            if (leftOff) _bluetoothRecovery?.UserSwitchOffFailed();
            throw;
        }
    }

    private readonly WifiService _wifi = new();

    public async Task<object> GetWifiAsync() => await _wifi.StateAsync();

    public async Task<object> ScanWifiAsync() => await _wifi.ScanAsync();

    public Task<WifiConnectResult> ConnectWifiAsync(string ssid, string? password) => _wifi.ConnectAsync(ssid, password);

    public Task<string> DisconnectWifiAsync() => _wifi.DisconnectAsync();

    // -----------------------------------------------------------------------
    // Game clients: started quietly on the couch, signed in with the controller or phone

    private IReadOnlyList<GameLauncher> FindLaunchers() =>
        Launchers.Find(new WindowsRegistry(), File.Exists);

    /// <summary>The clients that already run when CouchHud starts belong to the user: CouchHud never ends them.</summary>
    private void NoteClientsAtStart() =>
        _clients.NoteRunningAtStart(FindLaunchers().Where(l => LauncherControl.IsRunning(l.Process)).Select(l => l.Id));

    private string ModeOfLauncher(GameLauncher l) => Launchers.ModeOf(l, config.Current.LauncherModes, config.Current.LauncherAutostart);

    /// <summary>
    /// Starts a client quietly. Steam's settings go in before (it is not running, so it starts the way the couch wants it and is
    /// never restarted for that). What CouchHud starts here, it closes again on quit and in PC mode.
    /// </summary>
    private bool StartClient(GameLauncher l)
    {
        // Light mode, PC mode and dev runs: CouchHud does not start clients.
        if (!Launchers.MayStartClients(Full, _pcMode, dev)) return false;
        if (LauncherControl.IsRunning(l.Process)) return false;
        // A client that ended since (the user closed it) is a new start: ours again.
        _clients.NoteEnded(l.Id);
        if (l.Id == "steam" && !dev && SteamGuard.ShouldApply(Full, _pcMode, config.Current.SuppressCompetitors)) SteamSettings.PrepareForStart();
        try
        {
            LauncherControl.Start(l.Exe, l.Arguments, minimized: true);
            _clients.NoteStarted(l.Id);
            Console.WriteLine($"[launchers] started {l.Name}");
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"[launchers] {l.Name}: {ex.Message}");
            return false;
        }
    }

    private async Task StartLaunchersAsync()
    {
        // Let the HUD settle first; the clients come up behind it.
        await Task.Delay(TimeSpan.FromSeconds(4));
        // Steam's settings are written first (a Steam that runs is restarted by that), then the clients start.
        await Task.WhenAny(SteamSettings.WhenIdleAsync(), Task.Delay(TimeSpan.FromSeconds(60)));
        // The waiting may have outlasted the mode: back in light mode or in PC mode nothing is started.
        if (!Launchers.MayStartClients(Full, _pcMode, dev)) return;
        var started = new List<string>();
        foreach (var l in await Task.Run(FindLaunchers))
        {
            // "Standby" starts with the couch; "on demand" waits for a game or action that needs the client; "off" never.
            if (!Launchers.StartsWithCouch(ModeOfLauncher(l))) continue;
            if (StartClient(l)) started.Add(l.Process);
        }
        if (started.Count == 0) return;
        // Some clients ignore "start minimized" and pop up a window (updates, news). Twice, because some open late.
        foreach (var delay in new[] { 10, 25 })
        {
            await Task.Delay(TimeSpan.FromSeconds(delay));
            if (_pcMode || _mouse?.Active == true || _sessions.Current is not null) return;
            // Helpers (steamwebhelper …) hold the windows of some clients.
            LauncherControl.MinimizeWindows(started.SelectMany(ForegroundPolicy.FamilyProcesses).Distinct());
            _window?.BringToFront();
        }
    }

    /// <summary>
    /// A game of this client is about to start (or an action needs the client): "standby" and "on demand" start it quietly now, with
    /// the game's start right after (Steam takes the start URL once it is up). Light mode never does this: the clients stay as they are.
    /// </summary>
    private void StartClientForLaunch(LibraryItem item)
    {
        if (!Full || dev || _pcMode) return;
        var family = ForegroundPolicy.FamilyOfLaunch(item.Source, item.Launch.Kind, item.Launch.Target);
        if (family is null) return;
        try
        {
            var l = FindLaunchers().FirstOrDefault(x => x.Id == family);
            if (l is not null && Launchers.StartsOnDemand(ModeOfLauncher(l))) StartClient(l);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[launchers] {ex.Message}");
        }
    }

    /// <summary>
    /// Every tick: is this still the instance of the client CouchHud started? If the user ended it and started it again, it is not
    /// ours any more (the client's own restart after an update is tolerated for a minute). When in doubt it is not closed.
    /// </summary>
    private void ObserveClients()
    {
        if (!Full || _clients.Started.Count == 0) return;
        var live = Win32ProcessControl.Instance.List();
        var launchers = FindLaunchers();
        var now = DateTimeOffset.UtcNow;
        foreach (var id in _clients.Started)
        {
            var l = launchers.FirstOrDefault(x => x.Id == id);
            if (l is null) continue;
            var family = ForegroundPolicy.FamilyProcesses(l.Process);
            var procs = live.Where(p => string.Equals(p.Name, l.Process, StringComparison.OrdinalIgnoreCase) && p.Session == Win32ProcessControl.Instance.OwnSession).ToList();
            if (procs.Count == 0)
            {
                _clients.Observe(id, running: false, start: null, startedByClient: false, now);
                continue;
            }
            var newest = procs.MaxBy(p => p.Start)!;
            var parent = live.FirstOrDefault(p => p.Pid == newest.ParentPid);
            var byClient = parent is not null && family.Contains(parent.Name, StringComparer.OrdinalIgnoreCase);
            _clients.Observe(id, running: true, start: procs.Min(p => p.Start), byClient, now);
        }
    }

    /// <summary>A game of this client runs: a session, Steam's RunningAppID, or a child process of the client that is not part of it.</summary>
    private bool ClientHasGame(GameLauncher l)
    {
        if (GameBusy) return true;
        if (l.Id == "steam" && SteamSettings.SteamGameRunning) return true;
        var control = Win32ProcessControl.Instance;
        var live = control.List();
        var family = ForegroundPolicy.FamilyProcesses(l.Process);
        var clientPids = live.Where(p => family.Contains(p.Name, StringComparer.OrdinalIgnoreCase)).Select(p => p.Pid).ToHashSet();
        return live.Any(p => clientPids.Contains(p.ParentPid) && !family.Contains(p.Name, StringComparer.OrdinalIgnoreCase)
                             && !SafeTermination.IsProtected(p, control) && p.Start > 0);
    }

    /// <summary>
    /// Quit and PC mode: the clients CouchHud started itself (and that are still that instance) are closed cleanly, Steam with
    /// -shutdown; nothing is killed. A client with a download or update, a running game (also a child process of the client), or
    /// whose closing is not known to be harmless (everything but Steam for now) stays open, in PC mode with a notice. What ran before
    /// CouchHud, or was started by the user, is never touched. Light mode: CouchHud does not manage clients at all.
    /// </summary>
    /// <returns>Whether Steam was asked to end (its settings can go back once it has).</returns>
    private bool CloseOwnClients(bool pcMode)
    {
        var steamEnded = false;
        if (!Full) return false;
        try
        {
            var launchers = FindLaunchers();
            foreach (var id in _clients.Started)
            {
                var l = launchers.FirstOrDefault(x => x.Id == id);
                if (l is null) continue;
                var running = LauncherControl.IsRunning(l.Process);
                var game = running && ClientHasGame(l);
                var downloading = running && id == "steam" && SteamSettings.SteamDownloading;
                switch (_clients.Decide(id, running, downloading, game))
                {
                    case CloseDecision.Close:
                        SteamSettings.EndSteam();
                        steamEnded = true;
                        _clients.NoteEnded(id);
                        Console.WriteLine($"[launchers] closing {l.Name}");
                        break;
                    case CloseDecision.Downloading:
                        Console.WriteLine($"[launchers] {l.Name} is downloading, left open");
                        if (pcMode) PostNotification("CouchHUD", Strings.Ref("hud.clientBusy", l.Name), Strings.Ref("hud.clientBusy.body"));
                        break;
                    case CloseDecision.GameRunning:
                    case CloseDecision.Unsafe:
                        Console.WriteLine($"[launchers] {l.Name} left open");
                        if (pcMode) PostNotification("CouchHUD", Strings.Ref("hud.clientKept", l.Name), Strings.Ref("hud.clientKept.body"));
                        break;
                    default:
                        if (!running) _clients.NoteEnded(id);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[launchers] closing: {ex.Message}");
        }
        return steamEnded;
    }

    public object GetLaunchers() => FindLaunchers().Select(l => new
    {
        l.Id,
        l.Name,
        l.Exe,
        mode = ModeOfLauncher(l),
        running = LauncherControl.IsRunning(l.Process)
    }).ToList();

    public object SetLauncherMode(string id, string mode)
    {
        if (mode is not (Launchers.Standby or Launchers.OnDemand or Launchers.Off)) throw new ArgumentException(mode);
        // The older on/off entry of this client goes: one setting per client.
        config.Update(c =>
        {
            c.LauncherModes[id] = mode;
            c.LauncherAutostart.Remove(id);
        });
        return GetLaunchers();
    }

    /// <summary>
    /// Shows the client's window for signing in or settings: the HUD steps aside, the
    /// controller becomes a mouse and the phone types into the login fields.
    /// </summary>
    public void OpenLauncher(string id)
    {
        // A client window has browsers and file dialogs: a way out of the couch.
        RequireUnlocked();
        var l = FindLaunchers().FirstOrDefault(x => x.Id == id) ?? throw new ArgumentException(Strings.Get("hud.notInstalled", id));
        // A client that is not running comes up the way CouchHud starts clients (Steam's settings first, ours when it was off); then its
        // window is shown. Starting a single-instance client again shows its window (Steam, Epic, Galaxy …).
        if (Full && !LauncherControl.IsRunning(l.Process) && Launchers.StartsOnDemand(ModeOfLauncher(l))) StartClient(l);
        LauncherControl.Start(l.Exe, "", minimized: false);
        // Mouse mode first: the foreground guard leaves the client alone while it runs.
        _mouse?.Start();
        // A client that was running already (Steam with -silent, minimized by the guard) ignores the second start: its window is
        // restored and brought forward. The HUD only steps aside when a foreign window really is in front; otherwise it is back.
        var processes = LauncherControl.ClientProcesses(l.Exe);
        _ = Task.Run(async () =>
        {
            var shown = false;
            var deadline = DateTime.UtcNow + LauncherWindowWait;
            while (!shown && DateTime.UtcNow < deadline)
            {
                try
                {
                    if (LauncherControl.BringClientToFront(processes))
                    {
                        await Task.Delay(300);
                        shown = ForegroundRules.ForeignInFront(LauncherControl.ForegroundPid(), Environment.ProcessId);
                    }
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    Console.Error.WriteLine($"[launcher] window: {ex.Message}");
                }
                if (!shown) await Task.Delay(400);
            }
            Ui(() =>
            {
                if (_mouse?.Active != true) return;
                if (shown)
                {
                    _window?.Hide();
                    PostNotification("CouchHUD", Strings.Ref("hud.launcherOpen", l.Name), Strings.Ref("hud.launcherOpen.body"));
                }
                else
                {
                    EndMouseMode();
                    PostNotification("CouchHUD", Strings.Ref("hud.launcherNoWindow", l.Name), Strings.Ref("hud.launcherNoWindow.body"));
                }
            });
        });
    }

    private static readonly TimeSpan LauncherWindowWait = TimeSpan.FromSeconds(5);

    /// <summary>The lock closed while a launcher's mouse mode ran: no mouse mode on a locked couch, the HUD is back in front.</summary>
    private void EndMouseModeWhenLocked()
    {
        if (_mouse?.Active == true && Locked && !_pcMode && !_browsing) EndMouseMode();
    }

    private void EndMouseMode()
    {
        _mouse?.Stop();
        ShowHud();
    }

    // -----------------------------------------------------------------------
    // HDR for the HUD itself

    // Whether CouchHud switched Windows HDR on (and should switch it off again on quit).
    private bool _hdrTurnedOn;

    private static bool SafeHdrOn()
    {
        try
        {
            return DisplayService.Get().HdrOn;
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>"on" switches Windows HDR on while CouchHud runs; "off" undoes that again.</summary>
    private void ApplyUiHdr()
    {
        try
        {
            var display = DisplayService.Get();
            if (config.Current.UiHdr == "on" && display.HdrSupported && !display.HdrOn)
            {
                DisplayService.SetHdr(true);
                _hdrTurnedOn = true;
            }
            else if (config.Current.UiHdr != "on" && _hdrTurnedOn)
            {
                DisplayService.SetHdr(false);
                _hdrTurnedOn = false;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"[hdr] {ex.Message}");
        }
        Emit("hdr", new { mode = config.Current.UiHdr, on = SafeHdrOn() });
    }

    // -----------------------------------------------------------------------
    // Game profiles, night mode, Game Bar, downloads

    private void ApplyProfile(LibraryItem item)
    {
        RestoreProfile();
        if (!config.Current.GameProfiles.TryGetValue(item.Id, out var p) || p.IsEmpty) return;
        DisplayMode? mode = null;
        bool? hdr = null;
        string? audio = null;
        var night = false;
        try
        {
            var display = DisplayService.Get();
            if (p.Width is int w && p.Height is int h)
            {
                var hz = p.RefreshRate ?? display.Current.Hz;
                if (display.Current != new DisplayMode(w, h, hz))
                {
                    DisplayService.SetMode(new DisplayMode(w, h, hz));
                    mode = display.Current;
                }
            }
            if (p.Hdr is bool wantHdr && display.HdrSupported && wantHdr != display.HdrOn)
            {
                DisplayService.SetHdr(wantHdr);
                hdr = display.HdrOn;
            }
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"[profile] display: {ex.Message}");
        }
        try
        {
            var current = _audio?.GetState().Devices.FirstOrDefault(d => d.IsDefault)?.Id;
            if (p.AudioDevice is { } device && current is not null && device != current)
            {
                SetAudioOutput(device);
                audio = current;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            Console.Error.WriteLine($"[profile] audio: {ex.Message}");
        }
        if (p.NightMode == false)
        {
            _night.Suspend(true);
            night = true;
        }
        _profileRestore = new ProfileRestore(DateTimeOffset.UtcNow, mode, hdr, audio, night);
        Console.WriteLine($"[profile] applied for {item.Name}");
    }

    private void RestoreProfile()
    {
        if (_profileRestore is not { } r) return;
        _profileRestore = null;
        try
        {
            if (r.Mode is { } mode) DisplayService.SetMode(mode);
            if (r.Hdr is bool hdr) DisplayService.SetHdr(hdr);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"[profile] restore display: {ex.Message}");
        }
        try
        {
            if (r.Audio is { } audio) SetAudioOutput(audio);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            Console.Error.WriteLine($"[profile] restore audio: {ex.Message}");
        }
        if (r.Night) _night.Suspend(false);
        // Mode switches reset the gamma ramp.
        _night.Apply();
    }

    public object GetProfile(string id)
    {
        config.Current.GameProfiles.TryGetValue(id, out var profile);
        DisplayState? display = null;
        try
        {
            display = DisplayService.Get();
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException)
        {
        }
        return new
        {
            profile = profile ?? new GameProfile(),
            modes = display?.Modes ?? Array.Empty<DisplayMode>(),
            hdrSupported = display?.HdrSupported ?? false,
            devices = _audio?.GetState().Devices ?? (IReadOnlyList<AudioDevice>)Array.Empty<AudioDevice>()
        };
    }

    public object SetProfile(string id, GameProfile profile)
    {
        if (library.Find(id) is null) throw new KeyNotFoundException($"unknown item {id}");
        config.Update(c =>
        {
            if (profile.IsEmpty) c.GameProfiles.Remove(id);
            else c.GameProfiles[id] = profile;
        });
        return GetProfile(id);
    }

    /// <summary>Screenshot or clip through the Game Bar, taken from the game, not from the guide menu.</summary>
    public async Task CaptureAsync(string kind)
    {
        // The Game Bar shortcuts are off while the couch is locked.
        RequireUnlocked();
        _capturing = true;
        try
        {
            if (_sessions.Current is not null) ResumeGame();
            else _window?.Hide();
            await Task.Delay(900);
            switch (kind)
            {
                case "clip": GameBar.RecordThat(); break;
                case "record": GameBar.ToggleRecording(); break;
                default: GameBar.Screenshot(); break;
            }
            if (_sessions.Current is null)
            {
                await Task.Delay(1200);
                ShowHud();
            }
        }
        finally
        {
            _capturing = false;
        }
    }

    public Task<IReadOnlyList<SteamDownload>> GetDownloadsAsync() =>
        Task.Run(() => new SteamScanner(new WindowsRegistry()).Downloads());

    // -----------------------------------------------------------------------
    // Display, performance, captures

    public DisplayState GetDisplay() => DisplayService.Get();

    public DisplayState SetDisplayMode(int width, int height, int hz)
    {
        DisplayService.SetMode(new DisplayMode(width, height, hz));
        return DisplayService.Get();
    }

    public DisplayState SetHdr(bool on)
    {
        DisplayService.SetHdr(on);
        return DisplayService.Get();
    }

    public Task<PerformanceSample> GetPerformanceAsync() => _performance.SampleAsync();

    public Task<object> GetCapturesAsync() => Task.Run<object>(() =>
    {
        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        var folders = new List<(string, string)>
        {
            (Path.Combine(videos, "Captures"), "Game Bar"),
            (Path.Combine(pictures, "Screenshots"), "Windows"),
            (Path.Combine(videos, "NVIDIA"), "NVIDIA"),
            (Path.Combine(pictures, "Steam Screenshots"), "Steam")
        };
        var steam = new WindowsRegistry().ReadString(@"HKCU\Software\Valve\Steam", "SteamPath");
        var userdata = steam is null ? null : Path.Combine(steam.Replace('/', '\\'), "userdata");
        if (userdata is not null && Directory.Exists(userdata))
        {
            foreach (var user in Directory.EnumerateDirectories(userdata)) folders.Add((Path.Combine(user, "760", "remote"), "Steam"));
        }
        return CouchHud.Core.Files.Captures.Find(folders).Select(c => new
        {
            url = LocalFiles.Rewrite(new Uri(c.Path).AbsoluteUri),
            name = Path.GetFileNameWithoutExtension(c.Path),
            c.Source,
            c.Video,
            c.Time,
            c.Size
        }).ToList();
    });

    // -----------------------------------------------------------------------
    // Plugins

    public CouchHud.Core.Plugins.PluginManager Plugins => plugins;

    /// <summary>Built-in tools plus what plugins contribute.</summary>
    private IReadOnlyList<ToolSpec> AllTools
    {
        get
        {
            IReadOnlyList<ToolSpec> all = [.. ToolCatalog.All, .. plugins.ToolSpecs];
            // A locked couch offers the model only what it may use anyway (and costs fewer tokens).
            return Locked ? all.Where(t => ToolCatalog.AllowedWhileLocked(t.Name)).ToList() : all;
        }
    }

    public object GetPlugins() => plugins.Plugins.Select(p => new
    {
        p.Folder,
        p.Id,
        p.Name,
        p.Version,
        p.Author,
        p.Description,
        p.Homepage,
        // What the user chose; takes effect at the next start (loaded says what runs now).
        enabled = !config.Current.DisabledPlugins.Contains(p.Folder, StringComparer.OrdinalIgnoreCase),
        restartNeeded = p.Enabled == config.Current.DisabledPlugins.Contains(p.Folder, StringComparer.OrdinalIgnoreCase),
        p.Loaded,
        p.Error,
        p.NeedsApproval,
        p.ApprovalCode,
        p.Changed,
        p.UserInstalled,
        pages = plugins.Pages.Where(x => x.Plugin == p.Id).Select(x => new { x.Page.Id, x.Page.Title, x.Page.Icon, x.Page.Description }),
        settings = plugins.Settings.Where(x => x.Plugin == p.Id).Select(x => new
        {
            x.Setting.Key,
            x.Setting.Label,
            x.Setting.Description,
            kind = x.Setting.Kind.ToString().ToLowerInvariant(),
            x.Setting.Choices,
            // Secrets only say whether they are set.
            value = x.Setting.Kind == SettingKind.Secret ? (string.IsNullOrEmpty(plugins.SettingsOf(p.Id).Get(x.Setting.Key)) ? "" : "•••") : plugins.SettingsOf(p.Id).Get(x.Setting.Key)
        }),
        tools = plugins.ToolSpecs.Where(t => t.Name.StartsWith(p.Id + "_", StringComparison.OrdinalIgnoreCase)).Select(t => t.Name)
    }).ToList();

    public void SetPluginEnabled(string folder, bool enabled) => config.Update(c =>
    {
        c.DisabledPlugins.RemoveAll(f => f.Equals(folder, StringComparison.OrdinalIgnoreCase));
        if (!enabled) c.DisabledPlugins.Add(folder);
    });

    public void SetPluginSetting(string pluginId, string key, string? value)
    {
        if (!plugins.Settings.Any(s => s.Plugin == pluginId && s.Setting.Key == key)) throw new ArgumentException(Strings.Get("hud.unknownSetting", key));
        plugins.SettingsOf(pluginId).Set(key, value);
    }

    public async Task<object> GetPluginPageAsync(string pluginId, string pageId)
    {
        var (page, _) = plugins.FindPage(pluginId, pageId) ?? throw new ArgumentException(Strings.Get("plugin.pageNotFound"));
        var rows = await CouchHud.Core.Plugins.PluginManager.SafeAsync($"Plugin {pluginId}", page.Rows);
        return new { page.Title, page.Description, rows };
    }

    public async Task<object> ActivatePluginRowAsync(string pluginId, string pageId, string rowId)
    {
        var (page, _) = plugins.FindPage(pluginId, pageId) ?? throw new ArgumentException(Strings.Get("plugin.pageNotFound"));
        string? message = null;
        if (page.Activate is not null) message = await CouchHud.Core.Plugins.PluginManager.SafeAsync($"Plugin {pluginId}", ct => page.Activate(rowId, ct));
        var rows = await CouchHud.Core.Plugins.PluginManager.SafeAsync($"Plugin {pluginId}", page.Rows);
        return new { message, rows };
    }

    /// <summary>
    /// The guide menu's entries. The second lines are asked from the plugins on worker threads with a short timeout,
    /// all at once: a plugin that hangs costs its own line, never the UI thread (the session timer, the unlock and the
    /// parental checks run on it).
    /// </summary>
    public async Task<object> GetQuickActionsAsync()
    {
        var actions = plugins.QuickActions;
        var details = await Task.WhenAll(actions.Select(a => plugins.DetailAsync(a.Plugin, a.Action)));
        return actions.Select((a, i) => new { plugin = a.Plugin, a.Action.Id, a.Action.Label, a.Action.Icon, detail = details[i] }).ToList();
    }

    public Task<string?> RunQuickActionAsync(string pluginId, string actionId)
    {
        var action = plugins.FindAction(pluginId, actionId) ?? throw new ArgumentException(Strings.Get("plugin.actionNotFound"));
        return CouchHud.Core.Plugins.PluginManager.SafeAsync($"Plugin {pluginId}", action.Run);
    }

    /// <summary>Installs a plugin or skin .zip into the user's folder; plugins load at the next start.</summary>
    public string InstallPackage(string zipPath, string kind)
    {
        var isSkin = kind == "skin";
        var target = CouchHud.Core.Plugins.PackageInstaller.Install(
            zipPath,
            Path.Combine(dataDir, isSkin ? "skins" : "plugins"),
            isSkin ? CouchHud.Core.Plugins.PackageInstaller.Kind.Skin : CouchHud.Core.Plugins.PackageInstaller.Kind.Plugin);
        // Installed on purpose, by the user, through this very call: these files are the approved ones.
        if (!isSkin) plugins.Trust?.Trust(Path.GetFileName(target), target);
        return Path.GetFileName(target);
    }

    /// <summary>
    /// The user approves a plugin folder that nobody installed through CouchHud, or whose files changed. Needs the couch
    /// unlocked; the plugin loads at the next start.
    /// </summary>
    public object ApprovePlugin(string folder)
    {
        RequireUnlocked();
        plugins.Approve(folder);
        return GetPlugins();
    }

    // -----------------------------------------------------------------------
    // Plugin catalog

    private CatalogManager? _catalog;

    private CatalogManager Catalog => _catalog ??= new CatalogManager(config, plugins, dataDir, () => _sessions.Current is not null,
        (app, title, body) => Ui(() => PostNotification(app, title, body)),
        updates => Ui(() => Emit("catalogUpdates", updates)));

    private SelfUpdateManager? _selfUpdate;

    private SelfUpdateManager SelfUpdate => _selfUpdate ??= new SelfUpdateManager(config, dataDir, () => _sessions.Current is not null,
        (app, title, body) => Ui(() => PostNotification(app, title, body)),
        state => Ui(() => Emit("selfUpdate", state)));

    // CaseHUD: finding, starting, installing and controlling it (all of it in CaseHudManager).
    private Lazy<CaseHudManager>? _caseHudLazy;

    private Lazy<CaseHudManager> CaseHudLazy => LazyInitializer.EnsureInitialized(ref _caseHudLazy, () => new Lazy<CaseHudManager>(() => new CaseHudManager(config, dataDir, () => _sessions.Current is not null, (name, payload) => Ui(() => Emit(name, payload)))));

    public CaseHudManager CaseHud => CaseHudLazy.Value;

    public SelfUpdateState GetSelfUpdate() => SelfUpdate.State;

    public Task<SelfUpdateState> CheckSelfUpdateAsync() => SelfUpdate.CheckAsync(manual: true);

    /// <summary>Runs in the background; on success the setup ends this process, otherwise the reason arrives as a toast.</summary>
    public void StartSelfUpdate()
    {
        _ = Task.Run(async () =>
        {
            string message;
            try
            {
                message = await SelfUpdate.InstallAsync();
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                message = ex.Message;
            }
            Ui(() => Emit("toast", message));
        });
    }

    public Task<CatalogState> GetCatalogAsync(bool refresh) => Catalog.GetAsync(refresh);

    public IReadOnlyList<CatalogRow> GetCatalogUpdates() => Catalog.Updates;

    public async Task<object> InstallCatalogEntryAsync(string id, string kind)
    {
        var (message, restartNeeded, state) = await Catalog.InstallAsync(id, kind);
        return new { message, restartNeeded, state };
    }

    // -----------------------------------------------------------------------
    // Phone remote

    private readonly object _remoteLock = new();

    // Kestrel threads pair and check tokens while the UI reads the list: always snapshots.
    private Pairing Pairing => _pairing ??= new Pairing(
        () =>
        {
            lock (_remoteLock) return config.Current.RemoteDevices.ToList();
        },
        change =>
        {
            lock (_remoteLock) config.Update(c => change(c.RemoteDevices));
        });

    KeyboardState IRemoteTarget.Keyboard => _keyboard;

    /// <summary>Only while the controller steers a window CouchHud opened (mouse mode), not in PC mode.</summary>
    private bool TypingIntoForeignWindow => _mouse?.Active == true && !_pcMode;

    async Task<string?> IRemoteTarget.TextAsync(string text, bool submit)
    {
        // A window CouchHud opened for you (a launcher's login, the browser): type straight into
        // its focused field. Never into anything else, and never on the desktop in PC mode.
        var dispatcher = Application.Current.Dispatcher;
        var foreign = await dispatcher.InvokeAsync(() => _window?.IsActive != true || _browsing);
        if (!foreign)
        {
            Ui(() => Emit("remote", new { type = "text", text, submit }));
            return null;
        }
        if (!await dispatcher.InvokeAsync(() => TypingIntoForeignWindow)) return null;
        // The link is plain HTTP in the LAN: never into a password box (the Windows sign-in, a launcher login) or a field named like one.
        // Looked at off the UI thread (UI Automation waits on programs that hang); if that takes too long the field is not known to be safe.
        // One look at a time: a hung program must not collect a thread per request. No answer counts as not checked.
        if (await FocusProbe.ProbeAsync(TimeSpan.FromSeconds(2)) is not { } look) return Strings.Get("remote.fieldUnchecked");
        if (SecretFields.IsSecret(look.Field)) return Strings.Get("remote.passwordOnTv");
        // Launchers and browsers whose field could not be identified: the phone does not type there, the TV does.
        if (SecretFields.CannotBeVerified(look.Field, look.ProcessName, look.OwnProcess)) return Strings.Get("remote.fieldUnchecked");
        return await dispatcher.InvokeAsync<string?>(() =>
        {
            // The window may have changed while the field was looked at: only the one that was checked gets the text.
            if (!TypingIntoForeignWindow || !FocusProbe.StillForeground(look.Window)) return Strings.Get("remote.fieldUnchecked");
            KeyboardInput.Type(text);
            if (submit) KeyboardInput.Key(0x0D);
            return null;
        });
    }

    void IRemoteTarget.Button(string button) => Ui(() =>
    {
        // A question on the TV (assistant, MCP, Bluetooth PIN) is answered at the TV: the phone's keys do not count,
        // otherwise a paired phone could say yes to what it asked for itself.
        if (ConfirmationOpen) return;
        if (_window?.IsActive != true || _browsing)
        {
            // The phone's guide button always brings the HUD back (from a game, a login, the browser).
            if (button == "guide")
            {
                if (_browsing) LeaveBrowser("home");
                else if (_mouse?.Active == true) EndMouseMode();
                else if (!_pcMode) ShowHud();
                return;
            }
            if (!TypingIntoForeignWindow) return;
            // Phone d-pad on a foreign window: arrows, Enter, Escape, Tab.
            switch (button)
            {
                case "a": KeyboardInput.Key(0x0D); break;
                case "b": KeyboardInput.Key(0x1B); break;
                case "x": KeyboardInput.Key(0x09); break;
                case "y": KeyboardInput.Key(0x08); break; // Backspace
                case "up": KeyboardInput.Key(0x26); break;
                case "down": KeyboardInput.Key(0x28); break;
                case "left": KeyboardInput.Key(0x25); break;
                case "right": KeyboardInput.Key(0x27); break;
            }
            return;
        }
        Emit("input", new { type = "down", button });
        Emit("input", new { type = "up", button });
    });

    /// <summary>The on-screen keyboard reports itself so the phone can show the same field.</summary>
    public void SetKeyboardState(bool open, string title, bool secret, string text) => _keyboard = new KeyboardState(open, title, secret, text);

    public object GetRemote(bool newCode = false)
    {
        // A fresh pairing code lets a phone steer CouchHud: not for a locked couch, which also keeps the current code to itself.
        if (newCode) RequireUnlocked();
        var c = config.Current;
        var address = Network.LanAddresses().FirstOrDefault();
        string? url = null, qr = null, code = null;
        if (c.RemoteEnabled && address is not null && !Locked)
        {
            code = newCode ? Pairing.NewCode(DateTimeOffset.UtcNow) : Pairing.CurrentCode(DateTimeOffset.UtcNow);
            url = $"http://{address}:{c.RemotePort}/#pair={code}";
            qr = Qr.Svg(url);
        }
        return new
        {
            enabled = c.RemoteEnabled,
            running = _remote.Running,
            url,
            address = address is null ? null : $"http://{address}:{c.RemotePort}",
            code,
            qr,
            devices = RemoteDevicesSnapshot().Select(d => new { d.Id, d.Name, paired = d.Paired.ToUnixTimeMilliseconds(), lastUsed = (d.LastUsed ?? d.Paired).ToUnixTimeMilliseconds() })
        };
    }

    public void RevokeRemote() => Pairing.RevokeAll();

    /// <summary>Unpairs one phone; its token stops working at once.</summary>
    public void RevokeRemoteDevice(string id)
    {
        if (!Pairing.Revoke(id)) throw new ArgumentException(Strings.Get("remote.noSuchDevice"));
    }

    private List<RemoteDevice> RemoteDevicesSnapshot()
    {
        lock (_remoteLock) return config.Current.RemoteDevices.ToList();
    }

    /// <summary>Opens the firewall for the ports that are in use (phone remote, MCP in the LAN), for the local subnet only.</summary>
    public async Task<object> AllowFirewallAsync()
    {
        var ports = FirewallPorts();
        if (ports.Count == 0) throw new InvalidOperationException(Strings.Get("remote.shareNothing"));
        await Firewall.AllowAsync(ports);
        // Windows' own block rules for CouchHud.exe beat our allow rule: the page says so instead of "allowed" and a phone that still cannot connect.
        return new { blocked = await Task.Run(Firewall.BlockingRuleNames) };
    }

    /// <summary>At the start: the network features are on and Windows blocks CouchHud.exe with rules of its own (cancelled firewall prompt).</summary>
    private void NotifyIfFirewallBlocks() => _ = Task.Run(() =>
    {
        try
        {
            if (FirewallPorts().Count == 0) return;
            var names = Firewall.BlockingRuleNames();
            if (names.Count == 0) return;
            Console.Error.WriteLine($"[remote] Windows blocks CouchHud.exe with its own firewall rules: {string.Join(", ", names)}");
            Ui(() => PostNotification("CouchHUD", Strings.Ref("remote.firewallBlocked"), Strings.Ref("remote.firewallBlocked.body")));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[remote] firewall check: {ex.Message}");
        }
    });

    private List<int> FirewallPorts()
    {
        var c = config.Current;
        var ports = new List<int>();
        if (c.RemoteEnabled) ports.Add(c.RemotePort);
        if (c.McpEnabled && c.McpLan && !ports.Contains(c.McpPort)) ports.Add(c.McpPort);
        return ports;
    }

    /// <summary>Switching the last network feature off takes the firewall rule away again (asks for administrator rights, a refusal is fine).</summary>
    private async Task DropFirewallRuleIfUnusedAsync()
    {
        if (FirewallPorts().Count > 0) return;
        try
        {
            if (!await Task.Run(Firewall.Exists)) return;
            await Firewall.RemoveAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            Console.Error.WriteLine($"[remote] firewall rule stays: {ex.Message}");
        }
    }

    private async Task ApplyRemoteAsync(bool throwOnError = false)
    {
        var c = config.Current;
        try
        {
            await _remote.StopAsync();
            if (!c.RemoteEnabled) return;
            if (!File.Exists(RemoteHost.PagePath)) throw new InvalidOperationException("ui/remote/index.html fehlt");
            await _remote.StartAsync(c.RemotePort, Pairing, this);
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            Console.Error.WriteLine($"[remote] could not start: {ex.Message}");
            if (throwOnError) throw new InvalidOperationException(Strings.Get("hud.remoteFailed", c.RemotePort));
        }
    }

    // -----------------------------------------------------------------------
    // Autostart, services and processes

    public object GetStartup()
    {
        var modes = config.Current.StartupModes;
        return _startup.Entries().Select(e => new
        {
            e.Id,
            e.Name,
            e.Source,
            command = e.Command,
            disabled = e.DisabledInWindows,
            mode = StartupCatalog.ModeOf(e, modes),
            suggested = StartupCatalog.Suggest(e)
        }).ToList();
    }

    public void SetStartupMode(string id, string mode)
    {
        if (mode is not (StartupMode.Couch or StartupMode.Pc)) throw new ArgumentException($"unknown mode {mode}");
        config.Update(c =>
        {
            if (mode == StartupMode.Couch) c.StartupModes.Remove(id);
            else c.StartupModes[id] = mode;
        });
    }

    /// <summary>Applies the suggestion to every entry; returns how many wait for PC mode now.</summary>
    public int ApplyStartupSuggestions()
    {
        var entries = _startup.Entries();
        config.Update(c =>
        {
            c.StartupModes.Clear();
            foreach (var e in entries.Where(e => StartupCatalog.Suggest(e) == StartupMode.Pc)) c.StartupModes[e.Id] = StartupMode.Pc;
        });
        return config.Current.StartupModes.Count;
    }

    // -----------------------------------------------------------------------
    // The CouchHUD service: status, fail-closed coupling, repair

    private readonly LockWatch _serviceWatch = new(LockWatch.DefaultGrace, LockWatch.DefaultRelock);
    private System.Threading.Timer? _serviceTimer;
    private string? _lastServiceState;
    private bool _tasksMigrated;
    private bool _codesMigrated;
    private long _codesAskedAt;
    // The parental controls were just switched on: the code step of that chain may follow without a new unlock (review 3, item 6).
    private DateTimeOffset _parentCodeChainUntil = DateTimeOffset.MinValue;
    private int _serviceTick;

    /// <summary>
    /// Looks at the service every 10 seconds (the first look waits for a service that is still starting at boot). While the couch lock
    /// wants its Windows policies and the service is missing, the user is warned once; after <see cref="LockWatch.DefaultGrace"/> (at once
    /// on squatting or a contradicting answer) Windows is locked (design §4, review M2): never a quiet step down to "no lock".
    /// </summary>
    private void StartServiceWatch()
    {
        AdminService.Ops.Alarm += status => { _ = Task.Run(CheckServiceAsync); };
        _serviceTimer = new System.Threading.Timer(_ => _ = CheckServiceAsync(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10));
    }

    private async Task CheckServiceAsync()
    {
        if (Interlocked.Exchange(ref _serviceTick, 1) == 1) return;
        try
        {
            if (_quitting) return;
            var status = await AdminService.Ops.StatusAsync();
            var healthy = status.Health == ServiceHealth.Healthy;
            AdminService.SawService(status);
            if (status.Name != _lastServiceState)
            {
                Console.WriteLine($"[service] {status.Name}{(status.Version is { } v ? " " + v : "")}");
                _lastServiceState = status.Name;
                PolicyHelper.ForgetMode();
                Ui(() => Emit("serviceStatus", AdminService.View(status)));
            }
            if (healthy)
            {
                // Back (or there from the start): what failed before is tried again; old scheduled tasks go through the service.
                if (PolicyHelper.Failed)
                {
                    PolicyHelper.ForgetFailure();
                    PolicyHelper.Retry();
                }
                if (!_tasksMigrated)
                {
                    _tasksMigrated = true;
                    await AdminService.MigrateTasksIfAnyAsync();
                }
                // The local code hash and the lock mode go to the service once (P9); afterwards the config keeps only the marker.
                if (!_codesMigrated && !AdminService.DevMayRelax(dev) && ServiceCodes.Expected)
                {
                    // Done, or a conflict the settings and the setup assistant show (the service's wins): once per start.
                    _codesMigrated = await ServiceCodes.MigrateAsync(config) != MigrationResult.NotYet;
                }
                // What the service holds (settings page, setup assistant).
                if (ServiceCodes.Expected && (ServiceCodes.Known is null || Environment.TickCount64 - _codesAskedAt > 60_000))
                {
                    _codesAskedAt = Environment.TickCount64;
                    await ServiceCodes.RefreshAsync();
                }
            }
            // A setup the service runs stops the service on purpose (self-update): no alarm for that. Only the protected marker counts.
            var updating = AdminService.RestartExpected || AdminService.SetupRunning();
            var wanted = LockOn && !_pcMode && !AdminService.DevMayRelax(dev) && !updating && PolicyHelper.Mode() != "direct";
            // A service that answers but does not set the policies is no protection either (review M1).
            switch (_serviceWatch.Observe(DateTimeOffset.UtcNow, wanted, status.State, policiesFailing: PolicyHelper.FailingSince is not null))
            {
                case LockWatchAction.Warn:
                    Console.Error.WriteLine($"[service] the couch lock needs the service ({status.Name}, policies failing: {PolicyHelper.FailingSince is not null}): the Windows policies are not active");
                    Ui(() => PostNotification("CouchHUD", Strings.Ref("service.warn.title"), Strings.Ref("service.warn.body", Strings.Get(ServiceTexts.StateKey(status.State))), security: true));
                    break;
                case LockWatchAction.LockWindows:
                    Console.Error.WriteLine($"[service] the couch lock has no working service ({status.Name}, policies failing: {PolicyHelper.FailingSince is not null}): locking Windows (fail-closed)");
                    WindowsLock.LockIfConfigured(dataDir);
                    Ui(() => PostNotification("CouchHUD", Strings.Ref("service.alarm.title"), Strings.Ref(status.Health == ServiceHealth.Alarm ? "service.alarm.squatted" : "service.alarm.gone"), security: true));
                    break;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[service] watch: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _serviceTick, 0);
        }
    }

    /// <summary>The service as the UI shows it; <paramref name="fresh"/> asks it now.</summary>
    public async Task<object> GetServiceStatusAsync(bool fresh) => AdminService.View(await AdminService.Ops.StatusAsync(fresh));

    /// <summary>"Repair the service": the installer's repair script with Windows' UAC prompt (allowed while locked: UAC is the gate).</summary>
    public async Task<object> RepairServiceAsync()
    {
        var status = await AdminService.RepairAsync();
        PolicyHelper.ForgetFailure();
        PolicyHelper.Retry();
        Ui(SyncLockState);
        return AdminService.View(status);
    }

    public async Task<object> GetServicesAsync()
    {
        var applied = config.Current.CouchServicesInstalled.ToList();
        var status = await AdminService.Ops.StatusAsync();
        var (services, protectedFolder, tasks, leftovers, policiesSet, stuck) = await Task.Run(() =>
            (ServiceSwitch.List(), ServiceSwitch.InstallFolderIsProtected(), ServiceSwitch.HasTaskLeftovers(), ServiceSwitch.HasLeftovers(),
                PolicyRelease.AnyOurs(new WindowsRegistry()), StuckPolicyGroups().Count > 0));
        var healthy = status.Health == ServiceHealth.Healthy;
        var chosen = config.Current.CouchServices;
        return new
        {
            // The CouchHUD service does everything that needs administrator rights; "set up" means: it runs and answers.
            taskInstalled = healthy,
            policyHelper = healthy,
            // The service keeps its own record of the keys it created.
            cleanupNoteCurrent = true,
            // Scheduled tasks or policy files of an earlier version: the way back and "Clean up" look at this.
            leftovers,
            // Some of the lock's Windows policies are in the registry right now (the way back releases them first).
            policiesSet,
            // Policies with our values that nothing explains: "Clean up" offers the cleanup (service first, then reg.exe with UAC).
            stuckPolicies = stuck,
            // The selection changed since it was last applied.
            pending = !chosen.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(applied),
            // Tasks of an earlier version: applying the selection lets the service remove them.
            legacyTask = tasks,
            protectedFolder,
            service = AdminService.View(status),
            services = services.Select(s => new { s.Name, s.DisplayName, s.Status, s.Automatic, s.Suggested, selected = chosen.Contains(s.Name, StringComparer.OrdinalIgnoreCase) })
        };
    }

    public bool ToggleCouchService(string name)
    {
        if (!GamingServices.IsAllowed(name)) throw new ArgumentException(Strings.Get("hud.cannotPause", name));
        var selected = false;
        config.Update(c =>
        {
            selected = c.CouchServices.RemoveAll(s => s.Equals(name, StringComparison.OrdinalIgnoreCase)) == 0;
            if (selected) c.CouchServices.Add(name);
        });
        return selected;
    }

    /// <summary>
    /// <paramref name="policies"/> true: applies the chosen services through the CouchHUD service (no administrator prompt any more) and
    /// lets the service remove the scheduled tasks of earlier versions. False is the way back and never depends on the mode: the lock's
    /// policies are released and checked in the registry, the paused services come back, the old tasks go. Fail-closed: without a
    /// healthy service nothing of that happens (the policies could not be released), the UI shows the service state and the repair.
    /// </summary>
    public async Task SetupServiceTaskAsync(bool policies = true)
    {
        if (policies) RequireFull();
        var status = await AdminService.Ops.StatusAsync(fresh: true);
        if (policies ? status.Health != ServiceHealth.Healthy : !FailClosed.MayRemoveShell(status.State))
        {
            throw new InvalidOperationException(Strings.Get("service.needed", Strings.Get(ServiceTexts.StateKey(status.State))));
        }
        if (!policies) config.UpdateOrThrow(c => c.CouchServices.Clear());
        var chosen = config.Current.CouchServices.ToList();
        // Deselected services come back first.
        var dropped = config.Current.CouchServicesInstalled.Except(chosen, StringComparer.OrdinalIgnoreCase).ToList();
        if (dropped.Count > 0) _services.Apply(StartupMode.Pc, dropped);
        // The way back: nothing may set the policies again meanwhile; they are released first and checked in the registry.
        using var freeze = policies ? null : PolicyHelper.Freeze();
        try
        {
            if (!policies)
            {
                await Task.Run(ReleaseLockPoliciesNow);
                if (PolicyRelease.AnyOurs(new WindowsRegistry())) throw new InvalidOperationException(Strings.Get(SetupExit.MessageKey(SetupExit.PoliciesStay)));
            }
            await AdminService.MigrateTasksIfAnyAsync();
            config.UpdateOrThrow(c => c.CouchServicesInstalled = chosen);
            PolicyHelper.ForgetFailure();
            if (policies && !_pcMode && Full) _services.Apply(StartupMode.Couch, chosen);
        }
        finally
        {
            if (!policies) Ui(SyncLockState);
        }
    }

    /// <summary>
    /// The lock's policies and Task Manager back, whatever the lock state is, and checked in the registry afterwards. The "unlock" is
    /// forced first (a lock that is on is turned off with its snapshot), then every group is released through its task.
    /// </summary>
    private PolicyRelease.Verdict ReleaseLockPoliciesNow()
    {
        Hardening.Set(false, force: true);
        LockedDesktop.Release(dataDir, wait: true);
        TaskManagerPolicy.Apply(false);
        return PolicyHelper.ReleaseAllVerified(dataDir);
    }

    /// <summary>First step of the way back: give the lock's Windows policies back before any task is deleted. Reports what stays.</summary>
    public async Task<object> ReleaseLockPoliciesAsync()
    {
        using var freeze = PolicyHelper.Freeze();
        var verdict = await Task.Run(ReleaseLockPoliciesNow);
        // The service wanted a grant this shell does not have (nobody entered the code here): a parent's or an administrator's approval.
        if (verdict.Remaining.Count > 0 && AdminService.Ops.Expected && AdminService.Ops.Cache.For(ServiceOperation.ReleaseLockPolicies) is null
            && await AdminService.ObtainAdminGrantAsync([ServiceOperation.ReleaseLockPolicies, ServiceOperation.ReleaseLockPolicies, ServiceOperation.CleanupPolicyKeys]))
        {
            verdict = await Task.Run(ReleaseLockPoliciesNow);
        }
        Ui(SyncLockState);
        return new { released = verdict.Released, remaining = verdict.Remaining.Count };
    }

    /// <summary>The groups that are supposed to be set right now (the rest with our values is a leftover).</summary>
    private IReadOnlyList<string> ExpectedPolicyGroups()
    {
        var expected = new List<string>();
        if (Hardening.On) expected.Add(PolicyGroups.Lock.Id);
        if (LockOn && !_pcMode && !dev && config.Current.BlockTaskManager) expected.Add(PolicyGroups.TaskManager.Id);
        return expected;
    }

    private IReadOnlyList<PolicyGroup> StuckPolicyGroups() =>
        PolicyHelper.StuckGroups(dataDir, ExpectedPolicyGroups(), LockedDesktop.HasPendingState(dataDir));

    /// <summary>
    /// "Windows restrictions stuck - clean up": the service releases what it set; what stays (or everything, without a service) goes with
    /// reg.exe for the fixed values of the lock, with one administrator confirmation.
    /// </summary>
    public async Task CleanupStuckPoliciesAsync()
    {
        using var freeze = PolicyHelper.Freeze();
        await AdminService.CleanupStuckPoliciesAsync(dataDir);
        PolicyHelper.ForgetFailure();
        // What the helper still remembers is gone with the values.
        await Task.Run(() => PolicyHelper.ReleaseAllVerified(dataDir));
        Ui(SyncLockState);
    }

    /// <summary>At the start: policies that nothing explains are said clearly (once per start).</summary>
    private void NotifyIfPoliciesStuck() => _ = Task.Run(() =>
    {
        try
        {
            // The release after a crash and the helper's own work come first.
            PolicyHelper.WaitIdle(TimeSpan.FromSeconds(30));
            if (StuckPolicyGroups().Count == 0) return;
            Console.Error.WriteLine("[lock] policies with the lock's values are stuck: no lock, no mark, no state");
            Ui(() => PostNotification("CouchHUD", Strings.Ref("stuck.title"), Strings.Ref("stuck.body"), security: true));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[hud] stuck check: {ex.Message}");
        }
    });

    public Task<IReadOnlyList<ProcessInfo>> GetProcessesAsync()
    {
        var pids = _sessions.Current?.Pids ?? (IReadOnlyCollection<int>)Array.Empty<int>();
        return Task.Run(() => ProcessList.List(pids));
    }

    /// <summary>Closes politely; the result is how many processes are still running (0 = done).</summary>
    public Task<int> QuitProcessAsync(string name, bool force) => ProcessList.QuitAsync(name, force);

    public void SwitchToProcess(string name)
    {
        if (!ProcessList.Switch(name)) throw new InvalidOperationException(Strings.Get("hud.cannotFocusWindow"));
    }

    // -----------------------------------------------------------------------
    // Updates

    private void StartUpdateChecks()
    {
        var known = 0;
        _updates.Changed += state => Ui(() =>
        {
            Emit("updates", state);
            var count = state.Windows.Count + state.Apps.Count;
            if (state.Busy is null && state.CheckedAt is not null && count > known)
            {
                PostNotification("Updates", Strings.RefPlural("hud.updatesAvailable", count), string.Join(", ", state.Windows.Select(w => w.Name).Concat(state.Apps.Select(a => a.Name)).Take(4)));
            }
            if (state.Busy is null) known = count;
        });
        // A quiet look a few minutes after boot and then every few hours, so the
        // settings can show "3 updates" without anyone asking.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(3));
            while (true)
            {
                try
                {
                    if (_sessions.Current is null) await _updates.CheckAsync();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[updates] background check failed: {ex.Message}");
                }
                await Task.Delay(TimeSpan.FromHours(6));
            }
        });
    }

    public UpdateState GetUpdates() => _updates.State;

    public Task<UpdateState> CheckUpdatesAsync() => _updates.CheckAsync();

    /// <summary>Runs in the background; the result arrives as a toast and a fresh list.</summary>
    public void StartUpdate(string kind, string? id)
    {
        _ = Task.Run(async () =>
        {
            string message;
            try
            {
                message = kind == "windows" ? await _updates.InstallWindowsAsync() : await _updates.UpgradeAppsAsync(id);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
            {
                message = ex.Message;
            }
            Ui(() => Emit("toast", message));
            await _updates.CheckAsync();
        });
    }

    public Task<IReadOnlyList<HaEntity>> GetHomeEntitiesAsync() => _home.GetEntitiesAsync();

    /// <summary>
    /// Switches one thing in Home Assistant. The id must look like an entity id and be one Home Assistant lists as switchable;
    /// locked, only lights, scenes, media players and pinned switches (see <see cref="HomeAssistantPolicy"/>).
    /// </summary>
    public async Task ActivateHomeEntityAsync(string entityId)
    {
        if (!HomeAssistantPolicy.IsValidEntityId(entityId)) throw new ArgumentException(Strings.Get("ha.invalidEntity"));
        if (Locked && !HomeAssistantPolicy.AllowedWhileLocked(entityId, config.Current.HomeAssistantPinned.Contains(entityId)))
        {
            throw new LockedException(Strings.Get("ha.lockedEntity"));
        }
        var known = await _home.GetEntitiesAsync();
        if (!known.Any(e => e.Id == entityId && e.Controllable)) throw new ArgumentException(Strings.Get("ha.unknownEntity", entityId));
        await _home.ActivateAsync(entityId);
    }

    public bool PinHomeEntity(string entityId)
    {
        if (!HomeAssistantPolicy.IsValidEntityId(entityId)) throw new ArgumentException(Strings.Get("ha.invalidEntity"));
        var pinned = false;
        config.Update(c =>
        {
            pinned = !c.HomeAssistantPinned.Remove(entityId);
            if (pinned) c.HomeAssistantPinned.Add(entityId);
        });
        return pinned;
    }

    public CouchHud.Core.Files.BrowseResult Browse(string? path, IReadOnlyCollection<string>? extensions) =>
        CouchHud.Core.Files.FileBrowser.List(path, extensions);

    public Task<IReadOnlyList<CouchHud.Core.Files.ExeCandidate>> ScanFolderAsync(string path) =>
        Task.Run(() =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            return CouchHud.Core.Files.ExeScanner.Scan(path, cts.Token);
        });

    public async Task<IReadOnlyList<LibraryItem>> AddEntriesAsync(IReadOnlyList<CustomEntry> entries)
    {
        // A locked couch starts every game without asking: what is entered as one must be able to be one, and with
        // parental controls on only the parents may enter one.
        foreach (var e in entries)
        {
            // A link has no command line; nothing with a control character goes into one; a working folder has to exist.
            if (!string.IsNullOrEmpty(e.Url)) (e.Args, e.Cwd) = ([], null);
            else
            {
                LaunchOptions.ParseArguments(string.Join(' ', e.Args));
                e.Cwd = LaunchOptions.ValidateWorkingDirectory(e.Cwd, e.Path ?? "");
            }
        }
        var games = entries.Where(e => string.Equals(e.Type, "game", StringComparison.OrdinalIgnoreCase)).ToList();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var game in games)
        {
            var ok = !string.IsNullOrWhiteSpace(game.Path) ? GameEntryRules.MayBeGame(game.Path, windows) : GameEntryRules.MayBeGameUrl(game.Url);
            if (!ok) throw new ArgumentException(Strings.Get("entry.notAGame", game.Name));
        }
        if (games.Count > 0 && config.Current.Parental.Enabled) RequireParentGrant("addGame");
        // A new website entry: Edge for known copy-protected streaming services, the couch browser otherwise (unless the page chose).
        WebLinks.Migrate(entries);
        library.AddCustomEntries(entries);
        return await library.ScanAsync();
    }

    // The ids the last apps list offered: only those can be added or asked for an icon.
    private HashSet<string> _offeredApps = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The installed programs for "add an app": the Start menu's list, Store apps included, A to Z, with the ones in the library marked.</summary>
    public async Task<IReadOnlyList<InstalledApp>> GetInstalledAppsAsync()
    {
        var raw = await AppsFolder.ListAsync();
        var apps = InstalledApps.Clean(raw, StringComparer.Create(System.Globalization.CultureInfo.CurrentCulture, ignoreCase: true));
        var marked = InstalledApps.MarkKnown(apps, library.Items, AppsFolder.KnownFolder, File.Exists);
        _offeredApps = new HashSet<string>(marked.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
        return marked;
    }

    /// <summary>Small icons (png data URLs) of listed apps, a few at a time as the list scrolls into view.</summary>
    public Task<Dictionary<string, string>> GetAppIconsAsync(IReadOnlyList<string> ids) => Task.Run(() =>
    {
        var icons = new Dictionary<string, string>();
        foreach (var id in ids.Take(24).Where(_offeredApps.Contains))
        {
            if (ExeIcons.Render(InstalledApps.UrlOf(id), 64) is { Length: > 0 } png) icons[id] = "data:image/png;base64," + Convert.ToBase64String(png);
        }
        return icons;
    });

    /// <summary>Adds a program of the apps list as an app. Only what the list offered; the start is a file or shell:AppsFolder, never text from outside.</summary>
    public async Task<IReadOnlyList<LibraryItem>> AddInstalledAppAsync(string id, string name)
    {
        if (!_offeredApps.Contains(id)) throw new ArgumentException(Strings.Get("library.nothingToAdd"));
        var app = new InstalledApp(string.IsNullOrWhiteSpace(name) ? id : name.Trim(), id);
        return await AddEntriesAsync([InstalledApps.ToEntry(app, "app", AppsFolder.KnownFolder, File.Exists)]);
    }

    /// <summary>
    /// Startup parameters and working folder of an own entry, in the edit dialog only: the person at the TV, unlocked, in expert
    /// mode. The assistant, MCP and plugins have no way here (edit_item does not carry them; add_entry shows them on the confirmation).
    /// </summary>
    public LibraryItem EditLaunch(string id, string? arguments, string? workingDirectory)
    {
        RequireUnlocked();
        if (!config.Current.ExpertMode) throw new InvalidOperationException(Strings.Get("entry.launchOptions"));
        var item = library.Find(id) ?? throw new KeyNotFoundException($"unknown item {id}");
        if (item.Source != "custom" || item.Launch.Kind != LaunchKind.Exe) throw new ArgumentException(Strings.Get("library.notCustom", item.Name));
        // What a game is given on the command line runs on a locked couch without asking: with parental controls on, the parents' code.
        if (item.Type == ItemType.Game && config.Current.Parental.Enabled) RequireParentGrant("editType", id);
        return library.EditLaunch(id, arguments, workingDirectory);
    }

    public LibraryItem EditItem(string id, string? name, string? type, bool? hidden)
    {
        // Game or app decides what counts as play time and which rules apply: the parents' code for that.
        if (type is not null && config.Current.Parental.Enabled && library.Find(id) is { } current
            && !string.Equals(current.Type.ToString(), type, StringComparison.OrdinalIgnoreCase)) RequireParentGrant("editType", id);
        // The same rule as for a new entry: no shell, browser or Windows tool as a "game".
        if (string.Equals(type, "game", StringComparison.OrdinalIgnoreCase) && library.Find(id) is { } target
            && (target.Launch.Kind == LaunchKind.Exe
                ? !GameEntryRules.MayBeGame(target.Launch.Target, Environment.GetFolderPath(Environment.SpecialFolder.Windows))
                : !GameEntryRules.MayBeGameUrl(target.Launch.Target)))
            throw new ArgumentException(Strings.Get("entry.notAGame", target.Name));
        return library.EditItem(id, name, type, hidden);
    }

    public Task<ArtworkChoices> GetArtworkChoicesAsync(string id) => library.GetArtworkChoicesAsync(id);

    public LibraryItem SetArtwork(string id, string slot, string? urlOrPath) => library.SetArtwork(id, slot, urlOrPath);

    public IReadOnlyList<ItemCollection> GetCollections() => library.GetCollections();

    public IReadOnlyList<ItemCollection> UpdateCollections(Action<List<ItemCollection>> change) => library.UpdateCollections(change);

    public IReadOnlyList<string> GetGameFolders() => config.Current.GameFolders;

    /// <summary>Watches a folder: its games show up now and after every rescan.</summary>
    public async Task<IReadOnlyList<string>> AddGameFolderAsync(string path)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) throw new ArgumentException(Strings.Get("files.folderNotFound", full));
        // Everything in a watched folder is a game that starts on a locked couch.
        if (!GameEntryRules.MayBeGameFolder(full, Environment.GetFolderPath(Environment.SpecialFolder.Windows)))
            throw new ArgumentException(Strings.Get("entry.notAFolder", full));
        if (config.Current.Parental.Enabled) RequireParentGrant("addGame");
        config.Update(c =>
        {
            if (!c.GameFolders.Any(f => f.Equals(full, StringComparison.OrdinalIgnoreCase))) c.GameFolders.Add(full);
        });
        await library.ScanAsync();
        return config.Current.GameFolders;
    }

    public async Task<IReadOnlyList<string>> RemoveGameFolderAsync(string path)
    {
        config.Update(c => c.GameFolders.RemoveAll(f => f.Equals(path, StringComparison.OrdinalIgnoreCase)));
        await library.ScanAsync();
        return config.Current.GameFolders;
    }

    /// <summary>Typical game folders on the local drives that aren't watched yet.</summary>
    public Task<IReadOnlyList<string>> DiscoverGameFoldersAsync() => Task.Run(() =>
    {
        var roots = DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed).Select(d => d.RootDirectory.FullName).ToList();
        return GameFolderDiscovery.Candidates(roots, config.Current.GameFolders);
    });

    public void RemoveEntry(string id) => library.RemoveCustomEntry(id);

    public IReadOnlyList<SkinInfo> ListSkins() => SkinCatalog.ListUserSkins(Path.Combine(dataDir, "skins"));

    public void SetSkin(string id)
    {
        // A locked couch may change the look, but only to a skin that exists.
        if (!SkinCatalog.IsKnown(id, ListSkins())) throw new ArgumentException(Strings.Get("tools.noSkin", id));
        config.Update(c => c.Skin = id);
    }

    public void Power(string action)
    {
        // Restart, shutdown and sign-out end CouchHud through the session end (OnSessionEnding marks it
        // clean). The guard keeps running, also while locked: it recognises the shutdown by itself.
        if (action == "restart-pc")
        {
            RequireUnlocked();
            // Fallback if the RunOnce entry does not run at the desktop login: the next CouchHud start restores the shell entry.
            if (shell.IsInstalledAsShell()) config.Update(c => c.PendingShellRestore = true);
            TaskManagerPolicy.Apply(false);
            LockedDesktop.Release(dataDir, wait: true);
            shell.RestartIntoPcMode();
            return;
        }
        ShellManager.Power(action);
    }

    /// <summary>From the UI or a tool: behind the couch lock.</summary>
    public Task EnterPcModeAsync()
    {
        RequireUnlocked();
        return EnterPcModeCoreAsync();
    }

    private async Task EnterPcModeCoreAsync()
    {
        _pcMode = true;
        ApplyCouchSecurity();
        // The clients CouchHud started are closed (nothing waits); Steam's settings follow as soon as Steam is gone.
        _ = Task.Run(() => CloseOwnClients(pcMode: true));
        ApplyCompetitorSuppression();
        _window?.Hide();
        EnsureTray();
        try
        {
            if (_ranStartup && !ShellManager.IsDesktopShellRunning())
            {
                // CouchHud did the autostart; explorer must not do it a second time.
                // The programs that waited for PC mode start now instead.
                StartupManager.MarkStartupDoneForExplorer();
                var modes = new Dictionary<string, string>(config.Current.StartupModes);
                // The programs that wait for PC mode start through the shell too; the policies of a quick return to the couch wait for them.
                _hardeningHold = true;
                _ = Task.Run(() =>
                {
                    try
                    {
                        _startup.RunPc(modes);
                    }
                    finally
                    {
                        Ui(() =>
                        {
                            _hardeningHold = false;
                            SyncLockState();
                        });
                    }
                });
            }
            _services.Apply(StartupMode.Pc, config.Current.CouchServicesInstalled);
            await shell.EnterPcModeAsync();
            _taskbarHidden = false;
            plugins.Events.RaisePcModeEntered();
        }
        catch
        {
            // No desktop came up: never leave the user with a hidden HUD and nothing else.
            _pcMode = false;
            ApplyCouchSecurity();
            ApplyCompetitorSuppression();
            ShowHud();
            throw;
        }
    }

    /// <summary>Last resort when the HUD itself can't run (e.g. WebView2 missing): give the user Windows.</summary>
    public async void FallbackToDesktop(string reason)
    {
        Console.Error.WriteLine($"[hud] falling back to the desktop: {reason}");
        // The couch lock (or parental controls, which keep it on anyway) still holds: Windows asks for the account first. The same
        // condition as a web view start that fails closed (RuntimeStrict), so that fallback always ends at the Windows lock.
        if (RuntimeStrict && _asShell) WindowsLock.Lock();
        try
        {
            await EnterPcModeCoreAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[hud] desktop fallback failed: {ex}");
        }
    }

    public Task LeavePcModeAsync()
    {
        if (_pcMode)
        {
            _pcMode = false;
            ShellManager.LeavePcMode(Full && KillExplorerOnReturn);
            if (_ranStartup && config.Current.ClosePcAppsOnReturn)
            {
                var modes = new Dictionary<string, string>(config.Current.StartupModes);
                _ = Task.Run(() => _startup.ClosePc(modes));
            }
            if (Full) _services.Apply(StartupMode.Couch, config.Current.CouchServicesInstalled);
            else if (config.Current.CloseExplorerInLight) HideTaskbar();
            // Back on the couch: the clients on standby come up again (Steam's settings first).
            if (_asShell && Full)
            {
                ApplySteamSettings(release: false);
                _ = StartLaunchersAsync();
            }
            plugins.Events.RaiseCouchModeEntered();
            _unlockedUntil = DateTimeOffset.MinValue;
            ApplyCouchSecurity();
            ApplyCompetitorSuppression();
        }
        ShowHud();
        return Task.CompletedTask;
    }

    // -----------------------------------------------------------------------
    // Integration level

    private bool Full => config.Current.IntegrationMode == "full";

    /// <summary>Ends explorer on the way back to the couch: on by default as soon as CouchHud is the shell.</summary>
    private bool KillExplorerOnReturn => config.Current.KillExplorerOnReturn ?? _asShell;

    // Light mode closed the taskbar and owes it a restart.
    private bool _taskbarHidden;

    private void RequireFull()
    {
        if (!Full) throw new InvalidOperationException(Strings.Get("hud.fullOnly"));
    }

    private void HideTaskbar()
    {
        if (_asShell || !ShellManager.IsDesktopShellRunning()) return;
        _taskbarHidden = ShellManager.ExitExplorer();
    }

    private void RestoreTaskbar()
    {
        if (!_taskbarHidden) return;
        _taskbarHidden = false;
        if (!ShellManager.IsDesktopShellRunning()) ShellManager.StartExplorer();
    }

    /// <summary>
    /// Switches the integration level. Going back to "light" undoes everything "full"
    /// did that CouchHud can undo by itself; the list tells the UI what happened.
    /// </summary>
    public object SetIntegrationMode(string mode)
    {
        if (mode is not ("light" or "full")) throw new ArgumentException(Strings.Get("hud.integrationInvalid"));
        var undone = new List<string>();
        if (mode == "light" && Full)
        {
            if (config.Current.Parental.Enabled) throw new InvalidOperationException(Strings.Get("hud.turnOffParentalFirst"));
            RequireUnlocked();
            if (shell.IsInstalledAsShell())
            {
                shell.UninstallAsShell();
                undone.Add(Strings.Get("hud.undo.shell"));
            }
            if (config.Current.CouchLock != "off")
            {
                config.UpdateOrThrow(c => c.CouchLock = "off");
                undone.Add(Strings.Get("hud.undo.lock"));
            }
            if (config.Current.Parental.Enabled) undone.Add(Strings.Get("hud.undo.parental"));
            _guard.Stop();
            // Light mode: CouchHud does not manage the game clients at all. Nothing is closed, nothing is remembered.
            _clients.Forget();
            TaskManagerPolicy.Apply(false);
            if (config.Current.CouchServices.Count > 0)
            {
                _services.Apply(StartupMode.Pc, config.Current.CouchServicesInstalled);
                undone.Add(Strings.Get("hud.undo.services"));
            }
            if (AutoLogon.Status().Enabled) undone.Add(Strings.Get("hud.undo.autoLogon"));
        }
        config.UpdateOrThrow(c => c.IntegrationMode = mode);
        SignInLock.SyncInBackground(dataDir);
        ApplyCouchSecurity();
        ApplyCompetitorSuppression();
        return new { mode, undone };
    }

    // -----------------------------------------------------------------------
    // Couch browser

    private BrowserPane? _browser;
    // The page is in the HUD's Browser tab and on screen / the controller steers the page (not the HUD bar).
    private bool _browserShown;
    private bool _browsing;
    // The unlock that opened the browser is extended while it is in use, and warned about before it runs out.
    private DateTimeOffset _browserActivity = DateTimeOffset.MinValue;
    private bool _browserWarned;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    /// <summary>Keyboard or mouse input within the last <paramref name="span"/> (the pad is counted by its own events).</summary>
    private static bool InputWithin(TimeSpan span)
    {
        var info = new LastInputInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info) && TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time)) < span;
    }

    /// <summary>
    /// Like <see cref="InputWithin"/>, but the last input must not be CouchHUD's own (SendInput of the controller mouse, the
    /// display wake-up, phone typing, the night light keys). Windows does not say whose the last input was, so input that came
    /// right after one of ours counts as ours: then a real key press is missed, never a fake one counted (missing it only means
    /// Bluetooth comes back on, which is the safe side).
    /// </summary>
    private static bool RealInputWithin(TimeSpan span)
    {
        var info = new LastInputInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return false;
        var age = unchecked((uint)Environment.TickCount - info.Time);
        if (TimeSpan.FromMilliseconds(age) >= span) return false;
        var lastInput = Environment.TickCount64 - age;
        return lastInput > KeyboardInput.LastInjected + 250;
    }

    /// <summary>
    /// The browser is in front and used: the 2-minute unlock is extended (so reading a page does not end in a closed
    /// browser). Idle, it says 30 seconds before the end that the browser is about to close.
    /// </summary>
    private void WatchBrowserUnlock()
    {
        var now = MonoNow();
        if (!_browserShown || _browser is null || !LockOn || _pcMode || _unlockedUntil <= now)
        {
            _browserWarned = false;
            return;
        }
        var active = now - _browserActivity < TimeSpan.FromSeconds(30) || InputWithin(TimeSpan.FromSeconds(30));
        if (active)
        {
            if (_unlockedUntil - now < TimeSpan.FromSeconds(90)) _unlockedUntil = now.AddMinutes(2);
            _browserWarned = false;
        }
        else if (_unlockedUntil - now <= TimeSpan.FromSeconds(30) && !_browserWarned)
        {
            _browserWarned = true;
            Emit("toast", Strings.Get("browser.closesSoon"));
        }
    }

    private BrowserPane Browser()
    {
        if (_browser is not null) return _browser;
        var profile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CouchHud", "Browser");
        var pane = _browser = new BrowserPane(profile, () => !LockOn || IsUnlocked, () => RuntimeStrict);
        pane.PageChanged += () =>
        {
            // Navigation counts as use: the unlock does not run out under a page being read.
            _browserActivity = DateTimeOffset.UtcNow;
            Emit("browser", BrowserState());
        };
        // A video in full screen has the whole window; the page is steered by the controller then.
        pane.FullScreenChanged += () =>
        {
            _window?.SetBrowserFullScreen(pane.FullScreen);
            if (pane.FullScreen) SetBrowserFocus(true);
            if (pane.ManualFullScreen) Emit("toast", Strings.Get("browser.fullscreenHint"));
            Emit("browser", BrowserState());
        };
        // F6 / Alt+Up on a keyboard: the way back to the bar.
        pane.BarRequested += () =>
        {
            SetBrowserFocus(false);
            Emit("browserui", "bar");
        };
        // A TV page (YouTube TV) is steered by keys: the sticks leave the cursor alone, the left stick gives arrow keys.
        pane.TvModeChanged += () =>
        {
            if (_mouse is not null) _mouse.Paused = pane.TvMode;
            Emit("browser", BrowserState());
        };
        var tvSticks = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        tvSticks.Tick += (_, _) =>
        {
            if (!_browsing || !pane.TvMode || _browser != pane)
            {
                if (_browser != pane) tvSticks.Stop();
                return;
            }
            var (x, y, _) = pad.Sticks();
            pane.TvSticks(x, y);
        };
        tvSticks.Start();
        // Parked out of sight until the HUD says where the page goes: a web view starts only inside a window.
        _window?.AttachBrowser(pane);
        return pane;
    }

    /// <summary>The HUD's Browser tab says where its page area is (CSS pixels, which are the window's device-independent pixels); hidden when the tab is not in front or a menu covers it.</summary>
    public void BrowserLayout(double x, double y, double width, double height, bool visible)
    {
        if (_browser is null) return;
        if (!visible || width < 8 || height < 8)
        {
            HideBrowserPane();
            return;
        }
        _browserShown = true;
        _window?.PlaceBrowser(_browser, x, y, width, height);
    }

    private void HideBrowserPane()
    {
        if (_browser is null || !_browserShown) return;
        _browserShown = false;
        SetBrowserFocus(false);
        // Like a tab in the background: what plays stops.
        _browser.PauseMedia();
        _window?.HideBrowser();
    }

    /// <summary>Who gets the controller: the page (sticks are the mouse, buttons are browser keys) or the HUD's bar.</summary>
    public void SetBrowserFocus(bool page)
    {
        if (page && (_browser is null || !_browserShown)) page = false;
        if (_window is not null) _window.BrowserPageFocus = page;
        if (_browsing == page) return;
        _browsing = page;
        if (page)
        {
            _mouse?.Start();
            if (_mouse is not null) _mouse.Paused = _browser!.TvMode;
            _browserActivity = DateTimeOffset.UtcNow;
            _browser!.FocusPage();
            _window?.RaiseBrowser();
        }
        else
        {
            _mouse?.Stop();
            _window?.FocusHud();
        }
    }

    /// <summary>The HUD bar's buttons.</summary>
    public void BrowserCommand(string command)
    {
        var browser = _browser;
        if (browser is null) return;
        _browserActivity = DateTimeOffset.UtcNow;
        switch (command)
        {
            case "back":
                browser.GoBack();
                break;
            case "forward":
                browser.GoForward();
                break;
            case "reload":
                browser.Reload();
                break;
            case "zoomIn":
                SetBrowserZoom(browser.Zoom + 10);
                break;
            case "zoomOut":
                SetBrowserZoom(browser.Zoom - 10);
                break;
            case "fullscreen":
                browser.SetManualFullScreen(true);
                break;
            case "close":
                CloseBrowser(false);
                break;
        }
    }

    /// <summary>
    /// The one way to show a web address (news article, store page, plugin link, website entry): in the couch browser,
    /// never in the system's. Only http(s) goes through; the lock rules of the browser apply (pages can open Windows
    /// dialogs), so a locked couch refuses and the UI asks for the code first. Back from the browser the user is where they came from.
    /// </summary>
    /// <param name="source">Where the link came from (news, detail, plugin ...), for the log only.</param>
    public async Task OpenUrlAsync(string url, string source)
    {
        if (!WebLinks.TryWebUrl(url, out var uri)) throw new ArgumentException(Strings.Get("hud.invalidAddress"));
        Console.WriteLine($"[browser] open ({source}): {uri.Host}");
        await OpenBrowserAsync(uri.AbsoluteUri);
    }

    /// <summary>
    /// Typed text becomes an address or a search: "heise.de" opens the site,
    /// "elden ring tipps" searches with DuckDuckGo (no tracking, no account).
    /// </summary>
    internal static string ToUrl(string text)
    {
        text = text.Trim();
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return uri.ToString();
        if (!text.Contains(' ') && text.Contains('.') && Uri.TryCreate("https://" + text, UriKind.Absolute, out var guessed)) return guessed.ToString();
        return "https://duckduckgo.com/?q=" + Uri.EscapeDataString(text);
    }

    /// <summary>"heise.de" typed without a scheme (not a search, not a full address): https is tried first and http only when the server speaks no TLS or refuses the connection (never on certificate errors).</summary>
    internal static bool IsGuessedAddress(string? text)
    {
        text = text?.Trim() ?? "";
        if (text.Length == 0 || text.Contains(' ')) return false;
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return false;
        return text.Contains('.');
    }

    /// <summary>Opens a website, or goes back to the open one when <paramref name="text"/> is empty.</summary>
    public async Task OpenBrowserAsync(string? text)
    {
        // Web pages can open Windows file dialogs (upload, save as PDF), a way out of the couch.
        RequireUnlocked();
        var browser = Browser();
        try
        {
            if (!string.IsNullOrWhiteSpace(text) || browser.Url.Length == 0)
            {
                await browser.OpenAsync(ToUrl(string.IsNullOrWhiteSpace(text) ? "https://duckduckgo.com/" : text), config.Current.BrowserZoom, IsGuessedAddress(text));
            }
        }
        catch
        {
            CloseBrowser(false);
            throw;
        }
        // The HUD switches to its Browser tab and reports where the page goes.
        Emit("browserui", "show");
        if (LockOn) Emit("toast", Strings.Get("browser.closesWhenLocked"));
    }

    /// <summary>
    /// The unlock ran out: the browser is for the unlocked couch only (pages, dialogs and downloads lead out of it), so
    /// it is closed. The tab stays and shows its start page.
    /// </summary>
    private void CloseBrowserWhenLocked()
    {
        if (_browser is null) return;
        CloseBrowser(_browsing);
    }

    /// <summary>The page is closed for good (close button, the lock).</summary>
    private void CloseBrowser(bool bringFront)
    {
        if (_browser is null) return;
        SetBrowserFocus(false);
        _browserShown = false;
        _window?.RemoveBrowser();
        _browser = null;
        Emit("browser", BrowserState());
        Emit("browserui", "closed");
        if (bringFront) _window?.BringToFront();
    }

    /// <summary>Back to the HUD (home, or its Browser tab for the address); the page stays open in its tab.</summary>
    private void LeaveBrowser(string? target)
    {
        HideBrowserPane();
        _mouse?.Stop();
        ShowHud(target);
    }

    /// <summary>Controller on the page: sticks are the mouse (on a TV page the keys), the rest are browser keys.</summary>
    private bool OnBrowserButton(string button)
    {
        var browser = _browser!;
        // Full screen: the View button ends it, everything else belongs to the video page.
        if (browser.FullScreen && button is "back" or "start")
        {
            browser.ExitFullScreen();
            return true;
        }
        // A TV page is steered by keys: arrows, Enter, Escape.
        if (browser.TvMode && button is "up" or "down" or "left" or "right" or "a" or "b")
        {
            browser.TvButton(button);
            return true;
        }
        switch (button)
        {
            case "guide":
                LeaveBrowser("home");
                return true;
            case "y":
                // The address is typed in the HUD, with the on-screen keyboard and the phone.
                SetBrowserFocus(false);
                Emit("browserui", "address");
                return true;
            case "b":
                // Back in the history, and out of the browser when there is none (back to where the link was).
                if (browser.CanGoBack)
                {
                    browser.GoBack();
                }
                else
                {
                    SetBrowserFocus(false);
                    Emit("browserui", "exit");
                }
                return true;
            case "up":
            case "back":
            case "start":
                // Up from the page (or the View button): the controller goes to the HUD's bar above it.
                SetBrowserFocus(false);
                Emit("browserui", "bar");
                return true;
            case "lb":
                SetBrowserZoom(browser.Zoom - 10);
                return true;
            case "rb":
                SetBrowserZoom(browser.Zoom + 10);
                return true;
            default:
                return false;
        }
    }

    public object SetBrowserZoom(int zoom)
    {
        zoom = Math.Clamp(zoom, 50, 300);
        config.Update(c => c.BrowserZoom = zoom);
        if (_browser is not null) _browser.Zoom = zoom;
        return BrowserState();
    }

    public object BrowserState() => new
    {
        open = _browser?.Url.Length > 0,
        url = _browser?.Url ?? "",
        title = _browser?.PageTitle ?? "",
        canGoBack = _browser?.CanGoBack == true,
        canGoForward = _browser?.CanGoForward == true,
        fullScreen = _browser?.FullScreen == true,
        failed = _browser?.Failed == true,
        tv = _browser?.TvMode == true,
        zoom = config.Current.BrowserZoom,
        bookmarks = config.Current.Bookmarks
    };

    public object AddBookmark(string name, string url)
    {
        url = ToUrl(url);
        config.Update(c =>
        {
            c.Bookmarks.RemoveAll(b => b.Url == url);
            c.Bookmarks.Add(new Bookmark { Name = string.IsNullOrWhiteSpace(name) ? url : name.Trim(), Url = url });
        });
        return BrowserState();
    }

    public object RemoveBookmark(string url)
    {
        config.Update(c => c.Bookmarks.RemoveAll(b => b.Url == url));
        return BrowserState();
    }

    // -----------------------------------------------------------------------
    // Couch lock

    private bool LockOn => Full && config.Current.CouchLock != "off" && _asShell;

    /// <summary>
    /// A couch lock or parental controls are configured (whatever the mode or shell state): a web view whose machine-wide runtime will
    /// not start must not fall back to another runtime then (WebViewRuntimeLocator.StartAsync).
    /// </summary>
    public bool RuntimeStrict => config.Current.CouchLock != "off" || config.Current.Parental.Enabled;

    private bool IsUnlocked => !LockOn || _pcMode || MonoNow() < _unlockedUntil || MonoNow() < _setupHoldUntil;

    // The console setup runs with the couch open: its lock step would otherwise lock the HUD right away and ask for the code in the
    // middle of the setup. Only from an unlocked state, and it ends by itself (a setup page that dies must not leave the couch open).
    private DateTimeOffset _setupHoldUntil = DateTimeOffset.MinValue;
    private static readonly TimeSpan SetupHold = TimeSpan.FromMinutes(30);

    public void HoldUnlockForSetup(bool on)
    {
        if (on)
        {
            RequireUnlocked();
            _setupHoldUntil = MonoNow().Add(SetupHold);
        }
        else
        {
            _setupHoldUntil = DateTimeOffset.MinValue;
        }
        SyncLockState();
    }

    /// <summary>The couch lock is on and not opened right now: bridge and tools allow only harmless calls.</summary>
    public bool Locked => !IsUnlocked;

    /// <summary>Settings a guest may change while locked: sound and look only (the list lives in Core, see LockPolicy).</summary>
    public static IReadOnlySet<string> HarmlessSettings => CouchHud.Core.Guard.LockPolicy.HarmlessSettings;

    private void RequireUnlocked()
    {
        if (!IsUnlocked) throw new LockedException(Strings.Get("lock.unlockFirst"));
    }

    /// <summary>Guard and Task Manager policy only while the couch is locked and in front.</summary>
    private void ApplyCouchSecurity()
    {
        var on = LockOn && !_pcMode && !dev;
        if (on) _guard.Start();
        else _guard.Stop();
        TaskManagerPolicy.Apply(on && config.Current.BlockTaskManager);
        SyncLockState();
    }

    /// <summary>
    /// Everything that follows the lock state, checked on every change and by the session timer (the 2-minute unlock
    /// ends without an event): policies and accessibility shortcuts, the Game Bar, the browser, stray explorer windows.
    /// </summary>
    private void SyncLockState()
    {
        if (_quitting) return;
        try
        {
            WatchBrowserUnlock();
            var locked = Locked && !dev;
            // Locked again: the service's grants of the unlock are of no use any more and are not kept around in memory.
            if (Locked) AdminService.Ops.Cache.ClearUnlock();
            Hardening.Set(locked && !_hardeningHold);
            if (locked) EndMouseModeWhenLocked();
            if (_lastLocked != locked)
            {
                // The first call comes from the start, which does the Game Bar itself after its migration.
                var first = _lastLocked is null;
                _lastLocked = locked;
                if (!first) ApplyCompetitorSuppression();
                plugins.Events.RaiseLockChanged(locked);
            }
            if (locked) CloseBrowserWhenLocked();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[lock] sync: {ex.Message}");
        }
    }

    /// <summary>The screensaver's last step: the TV panel goes dark until the next input.</summary>
    public void DisplayOff() => Display.Off(_window);

    /// <summary>The UI saw keyboard or mouse input after the display went off: the panel is on again.</summary>
    public void DisplayOn() => Display.MarkAwake();

    private static bool? AskAccountPassword()
    {
        if (AccountPassword.Known is null) _ = Task.Run(AccountPassword.Refresh);
        return AccountPassword.Known;
    }

    public object GetSecurity()
    {
        var logon = AutoLogon.Evaluate(config.Current.AutoLogonConfigured);
        return new
        {
            couchLock = config.Current.CouchLock,
            hasPasscode = config.Current.PasscodeHash.Length > 0,
            blockTaskManager = config.Current.BlockTaskManager,
            unlocked = IsUnlocked,
            // The app the parents started while unlocked and that stays free until the HUD is in front again, or null.
            appsReleased = _appsReleased,
            asShell = _asShell,
            // How the Windows policies of the locked couch are written: "direct", "service" (the CouchHUD service) or "none" (not active).
            policies = PolicyHelper.Mode(),
            // The CouchHUD service (state, version, whether to warn and offer the repair).
            service = AdminService.View(AdminService.Current),
            // Which codes the CouchHUD service holds for this account (null: not known / no service).
            serviceCodes = ServiceCodes.Known is { } held ? new { couch = held.Couch, parent = held.Parent, mode = held.LockMode, legacy = held.LegacyLock } : null,
            // The codes go through the CouchHUD service (the installed copy); false: the config keeps the hash and the shell checks it.
            codesInService = ServiceCodes.Expected && !AdminService.DevMayRelax(dev),
            // A parents' code in the service that this shell did not set (no parental controls here): someone else enrolled it.
            parentCodeUnknown = CodeFlow.UnknownParentCode(ServiceCodes.Known, config.Current.Parental.Enabled),
            // The service holds a code of its own that the local one was not taken over (review L-c).
            codeConflict = ServiceCodes.Conflict,
            // The service's Windows-unlock grant is at hand (Windows lock mode): a new couch code may be set with it (finding H1).
            windowsGrant = ServiceCodes.Expected && AdminService.Ops.Cache.Has(GrantKind.WindowsUnlock),
            autoLogon = logon.State is AutoLogonState.CouchHud or AutoLogonState.External,
            autoLogonUser = logon.User,
            autoLogonState = logon.State.ToString().ToLowerInvariant(),
            autoLogonOtherUser = logon.OtherUser,
            passwordlessOnly = AutoLogon.PasswordlessOnly(),
            // null while still being asked (in the background); the UI only acts on a clear false.
            accountPassword = AskAccountPassword(),
            user = Environment.UserName,
            // The HUD runs on a WebView2 runtime outside Program Files (per-user install, changeable by the user's programs): the lock
            // settings warn and point to running the setup as administrator again.
            runtimeUnprotected = WebViewRuntimeLocator.Unprotected,
            // The runtime could not be checked at all (counts as unprotected, the warning says "cannot be checked").
            runtimeUnchecked = WebViewRuntimeLocator.Unchecked
        };
    }

    /// <summary>
    /// Code mode: checks the button sequence. Windows mode: shows the Windows lock screen,
    /// the "unlocked" event follows once the user is back.
    /// </summary>
    /// <param name="asNewLegacyCode">
    /// A lock of 0.4.0 whose stored code did not match (answer <c>legacyMismatch</c>): the person chose in the HUD's dialog to have an
    /// administrator confirm the code as the lock's new one (UAC only after that explicit choice).
    /// </param>
    public async Task<object> UnlockAsync(string[]? sequence, bool asNewLegacyCode = false)
    {
        if (IsUnlocked) return new { ok = true };
        if (config.Current.CouchLock == "windows")
        {
            _windowsUnlock.Ask();
            if (!WindowsLock.Lock())
            {
                _windowsUnlock.Cancel();
                return new { ok = false, pending = false };
            }
            return new { ok = false, pending = true };
        }
        var wait = _lockout.Wait;
        if (wait > TimeSpan.Zero) return new { ok = false, waitSeconds = (int)Math.Ceiling(wait.TotalSeconds) };
        // Fail-closed (review M2): while the service that holds the lock's Windows policies is missing, squatted or contradicting itself,
        // the code does not open the couch (the policies could not be released, and a squatter may be waiting for exactly that). The ways
        // through are the repair (UAC) and the Windows sign-in (recovery). Checked before the code, so nothing is guessed meanwhile.
        var service = AdminService.Current;
        if (!AdminService.DevMayRelax(dev) && !FailClosed.MayUnlock(LockOn, service.State))
        {
            Console.Error.WriteLine($"[lock] unlock refused: the service is {service.Name}");
            return new { ok = false, blocked = Strings.Get("service.unlockBlocked", Strings.Get(ServiceTexts.StateKey(service.State))) };
        }
        // The installed copy: the service checks the code (P9) and hands out the grant the release of the policies needs; the local lockout
        // and hash are only for a copy without a service.
        if (ServiceCodes.Expected && !AdminService.DevMayRelax(dev))
        {
            if (sequence is null) return new { ok = false };
            var (check, legacy) = await CheckCodeAsync(CodeKinds.Couch, sequence, asNewLegacyCode);
            if (legacy == LegacyConfirm.Mismatch) return new { ok = false, legacyMismatch = true };
            if (check is not { Granted: true }) return CodeAnswer(check);
            _unlockedUntil = MonoNow().AddMinutes(2);
            SyncLockState();
            // Confirmed with the old code of 0.4.0: it lay readable in the configuration, the UI asks for a new one (review 4).
            return legacy == LegacyConfirm.ConfirmedOldCode ? new { ok = true, legacyOldCode = true } : new { ok = true };
        }
        if (sequence is not null && Passcode.Verify(sequence, config.Current.PasscodeHash))
        {
            _lockout.Succeeded();
            _unlockedUntil = MonoNow().AddMinutes(2);
            SyncLockState();
            return new { ok = true };
        }
        _lockout.Failed();
        return new { ok = false, waitSeconds = (int)Math.Ceiling(_lockout.Wait.TotalSeconds) };
    }

    /// <summary>
    /// Windows lock mode, also while the couch is open: the Windows sign-in once more, so the service sees the unlock itself and gives its
    /// Windows-unlock grant. That is the proof the service wants for a new couch code while the Windows sign-in is the lock (back to the
    /// code lock, finding H1 of the 0.4.1 acceptance test); without it only a parent's or an administrator's approval would do. The
    /// "unlocked" event and its confirmation follow as after <see cref="UnlockAsync"/>.
    /// </summary>
    public object WindowsSignIn()
    {
        if (config.Current.CouchLock != "windows" || !ServiceCodes.Expected || AdminService.DevMayRelax(dev)) return new { ok = false, pending = false };
        if (AdminService.Ops.Cache.Has(GrantKind.WindowsUnlock)) return new { ok = true };
        _windowsUnlock.Ask();
        if (!WindowsLock.Lock())
        {
            _windowsUnlock.Cancel();
            return new { ok = false, pending = false };
        }
        return new { ok = false, pending = true };
    }

    /// <summary>The service's answer to a code (P9); a service without the couch code yet first gets the local hash (one-time migration).</summary>
    private async Task<(CodeCheck Check, LegacyConfirm? Legacy)> CheckCodeAsync(string kind, string[] sequence, bool asNewLegacyCode)
    {
        var check = await AdminService.Ops.VerifyCodeAsync(kind, sequence);
        if (check.Result.Ok && check.Outcome == CodeCheckResults.NoCode && kind == CodeKinds.Couch)
        {
            // No code with the service yet: the config's code goes over once; a lock of 0.4.0 instead asks an administrator to confirm the
            // code just entered, which then is its only code (security review H-1). One that does not match the stored code asks nothing
            // until the person chooses "confirm as an administrator" in the HUD (third security review).
            var migrated = await ServiceCodes.MigrateAsync(config);
            var proceed = migrated == MigrationResult.Done;
            LegacyConfirm? legacy = null;
            if (migrated == MigrationResult.NeedsApproval)
            {
                legacy = await ServiceCodes.ConfirmLegacyCodeAsync(config, sequence, asNewLegacyCode);
                if (legacy == LegacyConfirm.Mismatch) return (check, legacy);
                proceed = legacy is LegacyConfirm.Confirmed or LegacyConfirm.ConfirmedOldCode;
            }
            if (proceed) check = await AdminService.Ops.VerifyCodeAsync(kind, sequence);
            if (!check.Result.Ok) Console.Error.WriteLine($"[lock] {kind} code: the service could not check it ({check.Result})");
            return (check, legacy);
        }
        if (!check.Result.Ok) Console.Error.WriteLine($"[lock] {kind} code: the service could not check it ({check.Result})");
        return (check, null);
    }

    /// <summary>What the UI hears after a code that did not open: why (the service), how long to wait, or just "wrong".</summary>
    private static object CodeAnswer(CodeCheck? check) => check switch
    {
        null => new { ok = false, blocked = Strings.Get("codes.serviceMissing") },
        { Result.Ok: false } => new { ok = false, blocked = Strings.Get("codes.serviceError", AdminService.Message(check.Result)) },
        { Outcome: CodeCheckResults.NoCode } => new { ok = false, blocked = Strings.Get("codes.noCode") },
        { Outcome: CodeCheckResults.LockedOut } or { WaitSeconds: > 0 } => new { ok = false, waitSeconds = Math.Max(1, check.WaitSeconds) },
        _ => (object)new { ok = false }
    };

    private enum ConfirmKind { None, Unlock, Parent }

    /// <summary>The Windows sign-in is done and waits for the person at the TV to confirm what it was for.</summary>
    private sealed record WindowsConfirm(ConfirmKind Kind, DateTimeOffset Until, string? Action, string? Item);

    private static readonly TimeSpan ConfirmTime = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Windows mode: the sign-in alone opens nothing. A sign-in is also what Windows Hello does in passing, or the parents do
    /// for another reason, so the UI shows what was asked for and A confirms it; only then the couch opens (or the one-time
    /// release is given). Without confirmation the request is simply dropped.
    /// </summary>
    public async Task<object> ConfirmWindowsUnlockAsync(bool accept)
    {
        var pending = _confirm;
        _confirm = new(ConfirmKind.None, DateTimeOffset.MinValue, null, null);
        if (!accept || pending.Kind == ConfirmKind.None || MonoNow() > pending.Until) return new { ok = false };
        // The service saw the unlock itself: its grant (kind "windowsUnlock") lets the policies go with the couch. Never a parent's approval
        // in the service's eyes, whatever it is confirmed for here. Without one the couch still opens; the service keeps its policies then.
        if (ServiceCodes.Expected)
        {
            // A lock of 0.4.0 not confirmed yet: the Windows unlock alone releases nothing of it; an administrator confirms the Windows sign-in
            // as its lock first (security review H-1). The unlock is not used up by the refusal before, so the grant comes after.
            if (ServiceCodes.Known?.LegacyLock != false) await ServiceCodes.ConfirmLegacyWindowsAsync();
            var grant = await AdminService.Ops.RequestUnlockGrantAsync();
            if (!grant.Ok) Console.Error.WriteLine($"[lock] no grant for the Windows unlock: {grant}");
        }
        if (pending.Kind == ConfirmKind.Unlock)
        {
            _unlockedUntil = MonoNow().AddMinutes(2);
            SyncLockState();
        }
        else
        {
            _grant.Give(pending.Action, pending.Item);
        }
        return new { ok = true };
    }

    private void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason is not (Microsoft.Win32.SessionSwitchReason.SessionLock or Microsoft.Win32.SessionSwitchReason.SessionUnlock)) return;
        var unlock = e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock;
        // On the UI thread, in the order the events came: the request knows whether the lock before this unlock was ours.
        Ui(() =>
        {
            if (!unlock)
            {
                _windowsUnlock.OnLocked();
                _parentAuth.OnLocked();
                return;
            }
            // Only a sign-in that answers a lock of ours counts (not a face recognised in passing after a standby lock).
            if (_parentAuth.OnUnlocked())
            {
                _confirm = new(ConfirmKind.Parent, MonoNow() + ConfirmTime, _parentAuthScope.Action, _parentAuthScope.Item);
                _window?.BringToFront();
                Emit("parentalAuth", new { confirm = true });
            }
            else if (_windowsUnlock.OnUnlocked())
            {
                _confirm = new(ConfirmKind.Unlock, MonoNow() + ConfirmTime, null, null);
                _window?.BringToFront();
                Emit("unlocked", new { confirm = true });
            }
        });
    }

    public async Task<object> SetPasscodeAsync(string[] sequence)
    {
        // A new code is the key to the couch: never from a locked HUD, also not for the very first code.
        if (config.Current.Parental.Enabled) RequireParentGrant("passcode");
        else RequireUnlocked();
        Passcode.Validate(sequence);
        if (ServiceCodes.Expected && !AdminService.DevMayRelax(dev))
        {
            // The service keeps the code (and, with parental controls, the same buttons as the parents' code); the config only the marker.
            await ServiceCodes.StoreAsync(sequence, config.Current.Parental.Enabled);
            config.Update(c => c.PasscodeHash = Passcode.HeldByService);
            // The answer says what the service holds now (the setup assistant checks the code lock by it), like SetParentCode.
            await ServiceCodes.RefreshAsync();
            return GetSecurity();
        }
        var hash = Passcode.Hash(sequence);
        config.Update(c => c.PasscodeHash = hash);
        return GetSecurity();
    }

    /// <summary>
    /// The account has a password. Cached (see AccountPassword); only when it said "no" and is asked again does it look again,
    /// so that setting a password in Windows and retrying works. Off the UI thread where it has to ask.
    /// </summary>
    private static bool AccountHasPassword()
    {
        if (AccountPassword.Known == true) return true;
        if (AccountPassword.Known == false) AccountPassword.Recheck();
        return Task.Run(AccountPassword.Refresh).GetAwaiter().GetResult();
    }

    /// <summary>
    /// The lock mode "Windows sign-in" leans on the Windows lock screen, which lets everybody in again on an account without a
    /// password. The controller code does not (it is a lock of its own), so there a missing password is a warning only.
    /// </summary>
    private static void RequirePasswordForWindowsLock()
    {
        if (!AccountHasPassword()) throw new InvalidOperationException(Strings.Get("lock.noPassword"));
    }

    /// <param name="sequence">A new code, when the code lock is switched on without one the service holds: it goes along in the same step.</param>
    public async Task<object> SetCouchLockAsync(string mode, string[]? sequence = null)
    {
        if (sequence is not null) Passcode.Validate(sequence);
        if (mode is not ("off" or "code" or "windows")) throw new ArgumentException(Strings.Get("hud.lockInvalid"));
        if (mode != "off") RequireFull();
        if (mode == "windows") RequirePasswordForWindowsLock();
        if (config.Current.Parental.Enabled) RequireParentGrant(sequence is null ? "lockMode" : "lockModeCode");
        else RequireUnlocked();
        if (mode == "off" && config.Current.Parental.Enabled) throw new InvalidOperationException(Strings.Get("hud.turnOffParentalFirst2"));
        if (mode == "code" && config.Current.PasscodeHash.Length == 0 && sequence is null) throw new InvalidOperationException(Strings.Get("hud.setCodeFirst"));
        var service = ServiceCodes.Expected && !AdminService.DevMayRelax(dev);
        if (!service && sequence is not null)
        {
            var local = Passcode.Hash(sequence);
            config.Update(c => c.PasscodeHash = local);
        }
        else if (service && sequence is not null && config.Current.CouchLock != "off")
        {
            // A lock is on (the Windows sign-in): the code first, with that lock's proof, then the switch.
            await ServiceCodes.StoreAsync(sequence, config.Current.Parental.Enabled);
            config.Update(c => c.PasscodeHash = Passcode.HeldByService);
            await ServiceCodes.SetModeAsync(mode);
        }
        else if (service && sequence is not null)
        {
            // From no lock: mode and code in one step (codes enrolled at an open couch do not survive a lock being switched on).
            await ServiceCodes.SetModeAsync(mode, sequence, config.Current.Parental.Enabled);
            config.Update(c => c.PasscodeHash = Passcode.HeldByService);
        }
        else if (service && mode != config.Current.CouchLock)
        {
            // The code lock without a new code: only over a couch code the service holds and keeps through the switch. From no lock the
            // service drops what was enrolled at the open couch (review 3, item 2), and the mode would be "code" with no code to open it.
            // Refused before the mode changes; the UI then asks for the code and sends it along (finding of the 0.4.2 acceptance test).
            if (mode == "code" && !CodeFlow.CodeLockKeepsCode(await ServiceCodes.RefreshAsync()))
                throw new InvalidOperationException(Strings.Get("hud.setCodeFirst"));
            // The service keeps the mode (P9 review H1): lowering needs the current mode's grant (the unlock just done), the parents' or,
            // as the fallback, an administrator's. Switched off, the couch code goes from the service too (review M2); to the Windows lock
            // the service drops it in the same step (review M-a).
            await ServiceCodes.SetModeAsync(mode);
            if (mode == "off") await ServiceCodes.RemoveAsync(CodeKinds.Couch);
            // Looked at again: a code gone in between must not leave the config at a code lock nothing opens (fail closed: the config
            // stays at its mode). No answer is asked once more; a clear "no couch code" is said as what it is and logged.
            if (mode == "code")
            {
                var after = await ServiceCodes.RefreshAsync() ?? await ServiceCodes.RefreshAsync();
                if (after?.Couch != true)
                {
                    Console.WriteLine($"[codes] lock mode code set in the service, but it holds no couch code ({(after is null ? "no answer" : "couch code missing")}); the config stays at {config.Current.CouchLock}");
                    throw new InvalidOperationException(Strings.Get(after is null ? "hud.codeLockUnconfirmed" : "hud.codeLockWithoutCode"));
                }
            }
        }
        config.Update(c =>
        {
            c.CouchLock = mode;
            // The service no longer holds a code: switching the code lock on again asks for a new one.
            if (service && mode is "off" or "windows") c.PasscodeHash = "";
        });
        ApplyCouchSecurity();
        SignInLock.SyncInBackground(dataDir);
        // Awaited, after the policies: the answer below says what the service holds now (the setup assistant checks the code lock by it).
        if (service) await ServiceCodes.RefreshAsync();
        return GetSecurity();
    }

    public object SetBlockTaskManager(bool on)
    {
        if (on) RequireFull();
        RequireUnlocked();
        config.Update(c => c.BlockTaskManager = on);
        ApplyCouchSecurity();
        return GetSecurity();
    }

    /// <summary>Windows' own dialog for automatic sign-in, steered with the controller as mouse.</summary>
    public void OpenAutoLogonDialog()
    {
        RequireFull();
        RequireUnlocked();
        // Without a password Windows signs in by itself: there is nothing to set up.
        if (!AccountHasPassword()) throw new InvalidOperationException(Strings.Get("autologon.notNeeded"));
        AutoLogon.OpenDialog();
        _mouse?.Start();
        _window?.Hide();
        PostNotification("CouchHUD", Strings.Ref("hud.autoLogonTitle"),
            Strings.Ref("hud.autoLogonHelp"));
    }

    /// <summary>
    /// Sets up the automatic sign-in: checks the Windows password, then stores it through the
    /// elevated helper. A wrong password is a result, not an error, so the UI can ask again.
    /// </summary>
    public async Task<object> EnableAutoLogonAsync(string password, bool acceptNoLock)
    {
        RequireFull();
        RequireUnlocked();
        // An account without a password signs in without one: nothing to store, and nothing to explain as an error.
        if (!AccountHasPassword()) return new { ok = false, needsLock = false, noPassword = true, error = Strings.Get("autologon.notNeeded") };
        // Automatic sign-in lands every visitor in CouchHud; the couch lock keeps them in.
        if (config.Current.CouchLock == "off" && !acceptNoLock) return new { ok = false, needsLock = true, error = Strings.Get("hud.lockIsOff") };
        // Verifying blocks: never on the UI thread. The check here gives the clear messages and the attempt limit; the service checks again.
        var (account, error) = await Task.Run(() => AutoLogon.Verify(password));
        if (account is null) return new { ok = false, needsLock = false, error };
        await AutoLogon.EnableAsync(password);
        config.Update(c => c.AutoLogonConfigured = true);
        SignInLock.SyncInBackground(dataDir);
        return new { ok = true, security = GetSecurity() };
    }

    public async Task<object> SetAutoLogonAsync(string mode)
    {
        if (mode is not ("off" or "allow")) throw new ArgumentException(Strings.Get("hud.offOrAllow"));
        if (mode == "allow")
        {
            RequireFull();
            RequireUnlocked();
            if (!AccountHasPassword()) throw new InvalidOperationException(Strings.Get("autologon.notNeeded"));
        }
        // "allow" (password sign-in next to Windows Hello) is part of switching the automatic sign-in on now.
        if (mode == "allow") throw new InvalidOperationException(Strings.Get("autologon.allowViaSetup"));
        await AutoLogon.TurnOffAsync();
        if (mode == "off") config.Update(c => c.AutoLogonConfigured = false);
        SignInLock.SyncInBackground(dataDir);
        return GetSecurity();
    }

    /// <summary>"Quit" in the UI: behind the couch lock like the PC mode.</summary>
    public void QuitFromUi()
    {
        RequireUnlocked();
        Quit();
    }

    public void Quit()
    {
        // Off the UI thread: StopAsync continues on captured contexts otherwise.
        // The keyboard hook first: nothing may wait on it while the rest shuts down.
        _mediaKeys?.Dispose();
        _bluetoothRecovery?.Dispose();
        _bluetooth.Dispose();
        // Ends a running gaming lease first (at most about 2 s), then lets go of the connections.
        if (_caseHudLazy is { IsValueCreated: true } caseHudManager) caseHudManager.Value.Dispose();
        _parentalController?.Service.Flush();
        GamePause.ResumeAll();
        _quitting = true;
        _guard.Stop();
        _foreground?.Dispose();
        // The clients CouchHud started go (asked, not waited for); Steam's settings go back as soon as Steam is gone: a short bounded wait.
        // Short: what does not fit is put back by the Run entry or the next start.
        if (CloseOwnClients(pcMode: false)) SteamSettings.WaitSteamEnded(TimeSpan.FromMilliseconds(1500));
        ApplyCompetitorSuppression(release: true);
        TaskManagerPolicy.Apply(false);
        LockedDesktop.Release(dataDir, wait: true);
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        Task.Run(() => Task.WhenAll(_mcp.StopAsync(), _remote.StopAsync())).Wait(TimeSpan.FromSeconds(3));
        _night.Dispose();
        // Leave Windows the way we found it.
        if (_hdrTurnedOn)
        {
            try
            {
                DisplayService.SetHdr(false);
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"[hdr] {ex.Message}");
            }
        }
        // Leaving for the desktop: paused services come back, a closed taskbar too
        // (also when an earlier CouchHud closed it and this one only inherited the empty desktop).
        _services.Apply(StartupMode.Pc, config.Current.CouchServicesInstalled);
        RestoreTaskbar();
        // Never leave a screen without any shell behind (as the shell, or after light mode closed the taskbar).
        if (ShellDecisions.StartDesktopOnQuit(ShellManager.WaitForDesktopShell(TimeSpan.FromSeconds(5))))
        {
            try
            {
                Task.Run(() => shell.EnterPcModeAsync()).Wait(TimeSpan.FromSeconds(30));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[hud] desktop on quit failed: {ex.Message}");
            }
        }
        // An intentional quit is not a crash: the next start must not count toward the recovery.
        Recovery.MarkClean(dataDir);
        pad.Dispose();
        _tray?.Dispose();
        if (_window is not null) _window.AllowClose = true;
        Application.Current.Shutdown();
    }

    private void EnsureTray()
    {
        if (_tray is not null) return;
        try
        {
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add(Strings.Get("tray.backToCouch"), null, (_, _) => _ = LeavePcModeAsync());
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add(Strings.Get("tray.quit"), null, (_, _) => Quit());
            var tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = (Environment.ProcessPath is { } exe ? System.Drawing.Icon.ExtractAssociatedIcon(exe) : null) ?? System.Drawing.SystemIcons.Application,
                Text = "CouchHUD",
                ContextMenuStrip = menu,
                Visible = true
            };
            tray.MouseClick += (_, e) =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Left) _ = LeavePcModeAsync();
            };
            _tray = tray;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Without a notification area (no explorer yet) the icon is missing; PC mode itself must go on.
            Console.Error.WriteLine($"[hud] tray icon not available: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
