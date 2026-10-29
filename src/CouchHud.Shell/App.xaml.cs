using System.Net.Http;
using System.Windows;
using CouchHud.Core.Config;
using CouchHud.Core.Library;
using CouchHud.Core.Platform;
using CouchHud.Core.Plugins;
using CouchHud.Core.Security;
using CouchHud.Shell.Input;
using CouchHud.Shell.Services;
using CouchHud.Core.Localization;

namespace CouchHud.Shell;

public partial class App : Application
{
    private const string InstanceMutexName = @"Local\CouchHud.Instance";
    private const string ActivateEventName = @"Local\CouchHud.Activate";

    /// <summary>
    /// Started by "Restart CouchHUD": wait (at most RestartPlan.MaxWait) for the old instance to end, so the single-instance lock is free.
    /// The variable only names a process id to wait for; it is removed at once so nothing started from here inherits it.
    /// </summary>
    private static void WaitForRestartedInstance()
    {
        var value = Environment.GetEnvironmentVariable(CouchHud.Core.Updates.RestartPlan.WaitVariable);
        if (value is null) return;
        Environment.SetEnvironmentVariable(CouchHud.Core.Updates.RestartPlan.WaitVariable, null);
        if (CouchHud.Core.Updates.RestartPlan.ParseWait(value, Environment.ProcessId) is not { } pid) return;
        try
        {
            using var old = System.Diagnostics.Process.GetProcessById(pid);
            // Only the old instance of this very program is waited for: a clearly different path is not ours. A path that cannot be
            // read (access denied, no module) does not skip the wait.
            string? path = null;
            try
            {
                path = old.MainModule?.FileName;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
            {
            }
            if (path is not null && !string.Equals(path, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase)) return;
            old.WaitForExit(CouchHud.Core.Updates.RestartPlan.MaxWait);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone.
        }
    }

    private static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CouchHud");

    private Mutex? _instance;
    private EventWaitHandle? _activate;
    private Hud? _hud;

    // Names of the WEBVIEW2_* variables removed at start, logged once the log is open (names only, never values).
    private static IReadOnlyList<string> _scrubbed = [];

    protected override void OnStartup(StartupEventArgs e)
    {
        // Before anything else, before any WebView2 exists: the WebView2 loader takes WEBVIEW2_* variables (user environment, settable
        // by any standard user with setx) over our options, e.g. --remote-debugging-port, which would hand the HUD page and its bridge to
        // any local program. Removed from this process; every child process (guard, games, helpers) inherits the cleaned environment.
        _scrubbed = WebViewEnvironment.ScrubProcess();
        base.OnStartup(e);

        // Helpers first, before any logging: the elevated one (the uninstaller's cleanup) must not write into
        // folders the user (or a program running as the user) can redirect. CouchHud.exe has no other elevated mode any more:
        // every administrator operation goes through the CouchHUD service (owner decision E11).
        // One-shot helpers: no window, no single-instance check.
        if (e.Args.Length == 1 && e.Args[0] == UninstallCleanup.Switch)
        {
            Shutdown(UninstallCleanup.Run());
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == Guard.Switch)
        {
            // Not elevated, so it may write the log; what the guard does must be traceable.
            Log.Init(guard: true);
            LogScrubbed();
            try
            {
                Shutdown(Guard.Run(e.Args[1]));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[guard] failed: {ex}");
                Shutdown(1);
            }
            return;
        }
        if (e.Args.Length == 1 && e.Args[0] == LockedDesktop.ReleaseSwitch)
        {
            // Started by the Run entry of a locked couch whose CouchHud did not come back as the shell: give the policies back.
            // A running CouchHud (as the shell it never starts Run entries) manages them itself, nothing to do then.
            if (Mutex.TryOpenExisting(InstanceMutexName, out var running)) running.Dispose();
            else LockedDesktop.ReleaseFromRunEntry(DataDir);
            Shutdown();
            return;
        }
        if (e.Args.Length == 1 && e.Args[0] == SteamGuardService.ReleaseSwitch)
        {
            // Run entry while Steam's settings are changed for the couch and no CouchHud came back as the shell: give them back.
            // A running CouchHud manages them itself. Steam may be starting at the same time; the service waits for a quiet moment.
            if (Mutex.TryOpenExisting(InstanceMutexName, out var runningSteam)) runningSteam.Dispose();
            else
            {
                Log.Init();
                SteamGuardService.ReleaseFromRunEntry(DataDir);
            }
            Shutdown();
            return;
        }
        if (e.Args.Length == 1 && e.Args[0] == CouchHud.Core.Guard.GameBarGuard.ReleaseSwitch)
        {
            // Run entry while CouchHud held the Game Bar button and did not come back (crash, power loss): put the user's value back.
            // A running CouchHud manages it itself.
            // A CouchHud that autostarts at the same sign-in wins: wait a moment, look again right before restoring.
            var gameBarRunning = Mutex.TryOpenExisting(InstanceMutexName, out var runningGameBar);
            runningGameBar?.Dispose();
            if (!gameBarRunning)
            {
                Thread.Sleep(TimeSpan.FromSeconds(5));
                gameBarRunning = Mutex.TryOpenExisting(InstanceMutexName, out runningGameBar);
                runningGameBar?.Dispose();
            }
            if (!gameBarRunning)
            {
                Log.Init();
                foreach (var error in CouchHud.Core.Guard.GameBarGuard.ReleaseFromRunEntry(new CouchHud.Privileged.WindowsRegistry(), DataDir, m => Console.Error.WriteLine($"[guard] {m}")))
                    Console.Error.WriteLine($"[guard] {error}");
            }
            Shutdown();
            return;
        }
        if (e.Args.Length == 1 && e.Args[0] == SignInLock.Switch)
        {
            // Run entry of an account whose automatic sign-in lands on a desktop without CouchHud as the shell: lock it at once.
            Shutdown(SignInLock.Run(DataDir));
            return;
        }
        if (e.Args.Length == 1 && e.Args[0] == ShellManager.RestoreSwitch)
        {
            // Back from a one-time boot into PC mode: the next login is the couch again.
            Log.Init();
            try
            {
                var restoring = new ShellManager(new WindowsRegistry());
                restoring.LogDiagnostics("restore-shell");
                restoring.InstallAsShell();
                new ConfigStore(DataDir).Update(c => c.PendingShellRestore = false);
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine($"[app] could not restore the shell: {ex.Message}");
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"[app] could not save the config: {ex.Message}");
            }
            finally
            {
                // However the restore went: this boot skipped the couch on purpose, and with automatic sign-in the
                // desktop must still be behind the Windows sign-in. If the state of the automatic sign-in cannot be read, lock.
                try
                {
                    if (AutoLogon.Status().Enabled) WindowsLock.LockIfConfigured(DataDir);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Console.Error.WriteLine($"[app] automatic sign-in unreadable, locking: {ex.Message}");
                    WindowsLock.LockIfConfigured(DataDir);
                }
            }
            Shutdown();
            return;
        }
        // First thing: as the shell, an unhandled error at startup means a black screen.
        Log.Init();
        LogScrubbed();
        DispatcherUnhandledException += (_, args) =>
        {
            Console.Error.WriteLine($"[app] unhandled: {args.Exception}");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Console.Error.WriteLine($"[app] fatal: {args.ExceptionObject}");
            // A frozen game would stay frozen after we're gone.
            GamePause.ResumeAll();
            if (args.IsTerminating) EmergencyDesktop();
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Console.Error.WriteLine($"[app] unobserved task: {args.Exception}");
            args.SetObserved();
        };
        // Winlogon starts us in System32; relative paths should mean "next to CouchHUD".
        Environment.CurrentDirectory = AppContext.BaseDirectory;

        WaitForRestartedInstance();
        _instance = new Mutex(initiallyOwned: true, InstanceMutexName, out var first);
        if (!first)
        {
            // A second start (e.g. a desktop shortcut while in PC mode) just wakes the running one.
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var running))
            {
                running.Set();
                running.Dispose();
            }
            Shutdown();
            return;
        }

        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        ThreadPool.RegisterWaitForSingleObject(_activate,
            (_, _) => Dispatcher.InvokeAsync(() => _hud?.LeavePcModeAsync()),
            null, Timeout.Infinite, executeOnlyOnce: false);

        // Logging off or shutting down is not a crash, and the window must not hold it up.
        SessionEnding += (_, args) =>
        {
            Console.WriteLine($"[app] session ending ({args.ReasonSessionEnding})");
            Recovery.MarkClean(DataDir);
            _hud?.OnSessionEnding();
        };

        // Recovery: Ctrl + Shift or LB + RB + View held at start, or a crash loop, as the shell only.
        // --dev (window mode, DevTools, no recovery wait, relaxed checks) only in a debug build or with the administrator's switch in
        // HKLM: a standard user can put --dev into a shortcut, a Run entry or the account's shell value.
        var decision = DeveloperMode.Decide(e.Args);
        var dev = decision.Dev;
        var shell = new ShellManager(new WindowsRegistry());
        Console.WriteLine($"[app] args: {string.Join(' ', e.Args)}, dev {dev} ({decision.Reason}), process {Environment.ProcessPath}");
        if (decision.RemoteDebuggingPort is { } port) Console.WriteLine($"[dev] remote debugging on 127.0.0.1:{port} (HKLM RemoteDebuggingPort)");
        shell.LogDiagnostics("start");
        // A desktop that is just starting gets a moment to show up before we decide (nothing is on screen yet).
        if (!dev && shell.IsInstalledAsShell()) ShellManager.WaitForDesktopShell(TimeSpan.FromSeconds(3));
        if (!dev && !ShellManager.IsDesktopShellRunning() && shell.IsInstalledAsShell())
        {
            string? reason = null;
            var crashLoop = false;
            try
            {
                if (Recovery.KeyHeld() is { } held) reason = Strings.Get("recovery.keyAtStart", held);
                else if (Recovery.CrashLoop(DataDir))
                {
                    crashLoop = true;
                    reason = Strings.Get("recovery.crashLoop");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[recovery] check failed: {ex}");
            }
            if (reason is not null)
            {
                // Off the UI thread: the recovery waits for the Windows sign-in, and a UI thread that waits cannot be reached by anything
                // (that was the black screen after the sign-in). The dispatcher stays free; the result decides what happens next.
                var why = reason;
                var crash = crashLoop;
                _ = Task.Run(() =>
                {
                    var done = true;
                    try
                    {
                        // Waits for the Windows sign-in first (see Recovery.Run); false only when Windows could not be locked.
                        done = Recovery.Run(DataDir, why);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[recovery] failed: {ex}");
                        done = false;
                    }
                    Dispatcher.InvokeAsync(() =>
                    {
                        if (done)
                        {
                            Shutdown();
                            return;
                        }
                        // Windows would not lock, so nothing was opened (the recovery waits for the sign-in; without a lock a child at the
                        // controller would get to the desktop without the password). The couch starts as usual, locked by its own lock, the
                        // user was told, and the unclean ends are counted again so that the next start tries the recovery once more.
                        Console.Error.WriteLine("[recovery] could not lock Windows, starting the couch as usual");
                        Recovery.RestoreCounters(DataDir, crash, guardRestarts: false);
                        WatchRecoveryKeys();
                        StartHud(dev);
                    });
                });
                return;
            }
            WatchRecoveryKeys();
        }

        StartHud(dev);
    }

    private static void LogScrubbed()
    {
        if (_scrubbed.Count > 0) Console.Error.WriteLine($"[app] removed WebView2 override variables from the environment: {string.Join(", ", _scrubbed)}");
        if (WebViewEnvironment.AnyLeft()) Console.Error.WriteLine("[app] WebView2 override variables could not all be removed");
    }

    /// <summary>The first seconds after the start: the recovery combination may still come (a controller shows up late).</summary>
    private void WatchRecoveryKeys()
    {
        // Five minutes up and running: this start counts as healthy, earlier unclean ends
        // don't add up to a "crash loop" anymore.
        Task.Delay(TimeSpan.FromMinutes(5)).ContinueWith(_ => Recovery.MarkHealthy(DataDir), TaskScheduler.Default);
        // A controller may only show up in the first seconds: keep watching for the combination.
        // The recovery waits for the Windows sign-in (see Recovery.Run): off the UI thread, so the HUD behind the lock screen stays alive.
        Recovery.WatchStart(held => Task.Run(() =>
        {
            try
            {
                // Windows would not lock: nothing was changed and the user was told, the HUD stays.
                if (!Recovery.Run(DataDir, Strings.Get("recovery.keyAfterStart", held))) return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[recovery] failed: {ex}");
            }
            Dispatcher.InvokeAsync(() =>
            {
                if (_hud is not null) _hud.Quit();
                else Shutdown();
            });
        }));
    }

    private void StartHud(bool dev)
    {
        try
        {
            Start(dev);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[app] startup failed: {ex}");
            EmergencyDesktop();
            Shutdown(1);
        }
    }

    private void Start(bool dev)
    {
        var dataDir = DataDir;

        var config = new ConfigStore(dataDir);
        // While the picker waits for a protected profile it speaks the household's language, not the waiting person's.
        CouchHud.Core.Localization.Strings.Use(ProfileSwitch.Look(config.Current, ProfileSwitch.HeldAtStart(config.Current)).Language);
        var registry = new WindowsRegistry();
        // Every outbound connection goes through the privacy gate (Settings > Privacy): one policy, read live from the settings.
        var net = new CouchHud.Core.Net.NetPolicy(() => config.Current);
        var http = CouchHud.Core.Net.NetGate.CreateClient(CouchHud.Core.Net.NetService.Catalog, net, TimeSpan.FromSeconds(10));
        // Everything that asks Steam or SteamGridDB goes through one polite client (rate limit, Retry-After, circuit breaker).
        // Public addresses only (see SafeNet), redirects are followed by the callers, hop by hop.
        // One leash for the Steam hosts, whichever switch the request belongs to: images (Artwork) and the store's descriptions and ratings (GameInfo).
        var steamHandler = new CouchHud.Core.Net.PoliteHandler(CouchHud.Core.Net.SafeNet.CreateHandler(), CouchHud.Core.Plugins.CatalogRules.AppVersion.ToString());
        var polite = CouchHud.Core.Net.NetGate.CreateClient(CouchHud.Core.Net.NetService.Artwork, net, TimeSpan.FromSeconds(20), steamHandler);
        var politeInfo = CouchHud.Core.Net.NetGate.CreateClient(CouchHud.Core.Net.NetService.GameInfo, net, TimeSpan.FromSeconds(20), steamHandler);
        ArtworkCache? images = null;
        try
        {
            images = new ArtworkCache(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CouchHud", "artwork"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // No folder to write to: the HUD runs without image cache (generated banners, local art only).
            Console.Error.WriteLine($"[artwork] no image cache: {ex.Message}");
        }
        ArtworkCache.Shared = images;
        var steamIds = new SteamAppIds(politeInfo, Path.Combine(dataDir, "steam-ids.json"));
        var steamAssets = new SteamAssets(polite, Path.Combine(dataDir, "steam-assets.json"));
        var artwork = new ArtworkResolver(polite, Path.Combine(dataDir, "artwork-cache.json"), Secrets.Unprotect(config.Current.SteamGridDbApiKey), images, steamIds, steamAssets)
        {
            Online = net.Allows(CouchHud.Core.Net.NetService.Artwork),
            ExeIcons = path => ExeIcons.Render(path)
        };
        var storeInfo = new StoreInfoService(politeInfo, Path.Combine(dataDir, "store-cache.json"), () => CouchHud.Core.Localization.Strings.Language, steamIds);

        // Plugins the user already had count as approved the first time; from then on only installed or approved ones load.
        PluginTrust.UseKey(() => DataKey.Get(dataDir));
        var pluginTrust = new PluginTrust(dataDir);
        // Only once, on the upgrade from 0.5.x or older (config.json without the marker): later a missing list adopts nothing.
        if (!config.Current.PluginTrustAdopted && config.Fault is null)
        {
            new OptionalSteps(Console.Error.WriteLine).Run("plugin trust", () =>
            {
                pluginTrust.AdoptExisting(Path.Combine(dataDir, "plugins"));
                if (pluginTrust.HasStore) config.Update(c => c.PluginTrustAdopted = true);
            });
        }
        // Removals and plugin updates from the catalog wait for this moment: nothing is loaded yet, so the old files are free. Removals first,
        // so a plugin removed and installed again in one session comes back as the new package.
        new OptionalSteps(Console.Error.WriteLine).Run("removed plugins", () => CatalogService.FinishRemovals(dataDir));
        new OptionalSteps(Console.Error.WriteLine).Run("pending plugin updates", () => PendingPackages.ApplyAll(dataDir, Console.Error.WriteLine));

        // Only the plugins in %APPDATA% (installed from the catalog or approved by the user): nothing is bundled next to the exe any more,
        // Wand comes from the signed catalog like every other plugin.
        var seedsGiven = new System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyDictionary<string, string?>?>(StringComparer.OrdinalIgnoreCase);
        // Read and verified once (on the first plugin that asks); any failure means no extra ids, the fixed list in the program still applies.
        var firstPartyIds = new Lazy<IReadOnlyList<string>>(() => new OptionalSteps(Console.Error.WriteLine).Run("first-party plugin ids",
            () => new CatalogService(http, dataDir, () => [], () => false).FirstPartyIds(), (IReadOnlyList<string>)[]));
        var plugins = new PluginManager(
            [(Path.Combine(dataDir, "plugins"), true)],
            dataDir,
            folder => !config.Current.DisabledPlugins.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            Trust = pluginTrust,
            // Offline mode (Settings > Privacy): plugins that may reach the internet do not start.
            OfflineNow = () => net.Offline,
            // The ids of CouchHUD's own plugins from the signed index (cached and verified): reserved for everyone else at load.
            FirstPartyIds = () => firstPartyIds.Value,
            // Withdrawn plugins as CouchHUD's signed catalog said last time (its cache is verified again before it counts).
            Revocations = new OptionalSteps(Console.Error.WriteLine).Run("plugin revocations",
                () => new CatalogService(http, dataDir, () => [], () => false).CachedRevocations(), []),
            // What the core kept for CaseHUD, Home Assistant and the VPN before they became plugins (one-time seed); a secret is encrypted again for the plugin.
            SettingSeeds = id => seedsGiven[id] = PluginSettingSeeds.For(config.Current, id, (plugin, stored) => Secrets.Protect(Secrets.Unprotect(stored), plugin)),
            // The seed is in the plugin's settings file: the encrypted legacy token fields go, so no second copy stays behind (not on failure).
            SeedApplied = id =>
            {
                if (id.Equals(PluginSettingSeeds.HomeAssistantId, StringComparison.OrdinalIgnoreCase) && seedsGiven.TryGetValue(id, out var given))
                    config.Update(c => PluginSettingSeeds.ClearLegacyHomeAssistantSecrets(c, given));
            },
            // A plugin's secrets (SetSecret, settings of the kind Secret) are encrypted for the Windows user, apart for every plugin.
            ProtectSecret = (plugin, plain) => Secrets.Protect(plain, plugin),
            UnprotectSecret = (plugin, stored) => Secrets.Unprotect(stored, plugin),
            CliRunner = async (plugin, exe, arguments, timeout, ct) =>
            {
                Console.Error.WriteLine($"[plugins] {plugin} runs {Path.GetFileName(exe)}");
                var (exit, stderr) = await TrustedCliRunner.RunAsync(exe, arguments, timeout, ct);
                return new CouchHud.Sdk.PluginCliResult(exit, stderr, exit is null);
            }
        };
        plugins.Host.CanonicalTrustedExe = exe => TrustedCliRunner.Verify(exe, Path.GetFileName(exe), "plugins", logMissing: false);
        plugins.Host.ProcessImage = pid => TrustedCliRunner.ProcessImage(pid) is { } image ? TrustedCliRunner.CanonicalPath(image) : null;
        // Loading can take seconds on a cold start: the HUD shows first, plugins join in when ready.
        _ = plugins.StartLoading();

        var library = new LibraryService(config, artwork,
            [
                new SteamScanner(registry), new EpicScanner(), new GogScanner(registry), new UbisoftScanner(registry), new XboxScanner(),
                new FolderGamesProvider(() => config.Current.GameFolders.ToList()),
                new PluginLibraryProvider(plugins)
            ]);
        // The store match: the user's pick, else the scanned name (a rename never changes the age rating).
        storeInfo.Lookup = library.StoreLookupOf;
        // An automatic match that loses a known rating keeps it as the floor until the parents confirm.
        storeInfo.RatingDropped = (item, age) => library.KeepAgeFloor(item.Id, age);
        artwork.StoreMatchOf = id => config.Current.Overrides.GetValueOrDefault(id)?.StoreMatch;
        // Switched off areas (FeatureAreas): the game stores are not read with Gaming off, unless parental controls need them to see a game
        // started outside CouchHud (the limits never get looser) or an entry of the store was marked as an app; artwork is fetched only
        // for what is shown.
        library.SkipProvider = p => p is IGameStore store && !FeatureAreas.ReadsStore(config.Current, store.Source);
        library.FetchArtFor = item => FeatureAreas.From(config.Current).Shows(item);

        _hud = new Hud(config, library, artwork, storeInfo, new ShellManager(registry), new XInputPoller(), net, dataDir, dev, plugins);
        // Lookups and downloads wait while a game runs.
        artwork.BackgroundAllowed = () => !_hud.GameBusy;
        var window = new MainWindow(_hud, dev);
        MainWindow = window;
        window.Show();
        _hud.Start(window);
        // Games of plugin providers appear once the plugins are there.
        _ = plugins.Loaded.ContinueWith(_ => { if (plugins.LibraryProviders.Count > 0) _hud.RescanLibraryInBackground(); }, TaskScheduler.Default);
        // Started from autostart or Winlogon: other windows may be up already, the HUD belongs in front.
        if (!dev) new OptionalSteps(Console.Error.WriteLine).Run("bring to front", window.BringToFront);
        // Lock and automatic sign-in without CouchHud as the shell: a Run entry locks at every sign-in (see SignInLock).
        if (!dev) _ = Task.Run(() => SignInLock.Sync(dataDir));
    }

    /// <summary>
    /// Gets the user a working Windows desktop when CouchHud can't run at all: for this session only.
    /// The shell entry stays; the unclean end is counted at the next start, and only the third in a
    /// row makes the recovery remove the entry (a single error must not end the couch for good).
    /// </summary>
    private static void EmergencyDesktop()
    {
        try
        {
            var shell = new ShellManager(new WindowsRegistry());
            // Lock and policy only for a CouchHud that is the shell right now: never in a light or dev session.
            if (ShellDecisions.EmergencyMayLock(shell.IsInstalledAsShell() && !ShellManager.IsDesktopShellRunning()))
            {
                // The couch lock still holds: Windows asks for the account before the desktop.
                WindowsLock.LockIfConfigured(DataDir);
                TaskManagerPolicy.Apply(false);
            }
            // The policies of the locked couch never stay behind, whatever the circumstances.
            LockedDesktop.Release(DataDir, wait: true);
            Console.Error.WriteLine("[app] emergency: starting the desktop for this session, the shell entry stays");
            Task.Run(shell.EnterPcModeAsync).GetAwaiter().GetResult();
            shell.LogDiagnostics("after emergency desktop");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[app] emergency desktop failed: {ex}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _activate?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
