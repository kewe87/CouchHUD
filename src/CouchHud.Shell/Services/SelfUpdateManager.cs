using System.Diagnostics;
using System.Net.Http;
using CouchHud.Core.Config;
using CouchHud.Core.Plugins;
using CouchHud.Core.Updates;
using CouchHud.Core.Localization;

namespace CouchHud.Shell.Services;

/// <summary>What the CouchHUD section of the Updates page shows.</summary>
public sealed record SelfUpdateState(
    string Current,
    bool Enabled,
    bool Available,
    string? Version,
    string? Notes,
    string? PageUrl,
    /// <summary>The release has a setup, checksum and signature.</summary>
    bool Ready,
    /// <summary>CouchHUD runs from a protected folder, so a setup may replace it.</summary>
    bool CanInstall,
    string? Problem,
    string? Error,
    long? CheckedAt,
    string? Busy,
    /// <summary>noRelease, rateLimit, noAnswer or unreachable: the UI translates these itself; null for other errors (Error holds the text).</summary>
    string? ErrorCode = null,
    /// <summary>The setup is being downloaded and that can be cancelled now (never after the hand-over to the service).</summary>
    bool Cancellable = false);

/// <summary>
/// CouchHUD updating itself from GitHub Releases: a quiet check in the background (never while a game runs),
/// a notification once per version and, on request, the signed setup run silently by the CouchHUD service (no UAC prompt).
/// The setup closes this process and starts the new one (installer/CouchHud.iss), also when CouchHUD is the shell.
/// </summary>
public sealed class SelfUpdateManager
{
    private readonly ConfigStore _config;
    private readonly CouchHud.Core.Net.NetPolicy _net;
    private readonly string _dataDir;
    private readonly Func<bool> _gameRunning;
    private readonly Action<string, string, string> _notify;
    private readonly Action<SelfUpdateState> _changed;
    private readonly SelfUpdateService _service;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SelfUpdateCheck? _last;
    private string? _busy;
    /// <summary>Set only while the setup downloads: the one thing a cancel may stop (its slot closes when the hand-over begins).</summary>
    private DownloadCancel? _download;

    public SelfUpdateManager(ConfigStore config, string dataDir, Func<bool> gameRunning, Action<string, string, string> notify, Action<SelfUpdateState> changed, CouchHud.Core.Net.NetPolicy net)
    {
        _config = config;
        _net = net;
        _dataDir = dataDir;
        _gameRunning = gameRunning;
        _notify = notify;
        _changed = changed;
        // Redirects are followed by SelfUpdateService itself (host check on every hop); timeouts are its own.
        var http = CouchHud.Core.Net.NetGate.CreateClient(CouchHud.Core.Net.NetService.Updates, _net, Timeout.InfiniteTimeSpan,
            userAgent: $"CouchHud/{CatalogRules.AppVersion}", followRedirects: false);
        _service = new SelfUpdateService(http, dataDir);
        // A start means no setup is replacing us any more.
        UpdateMarker.Clear(dataDir);
        CleanTemp();
    }

    private string TempDir => Path.Combine(_dataDir, "update-temp");

    public SelfUpdateState State => Describe();

    private SelfUpdateState Describe()
    {
        var r = _last?.Release;
        var available = _last?.UpdateAvailable == true && r is not null;
        return new SelfUpdateState(CatalogRules.AppVersion.ToString(), _config.Current.SelfUpdateCheck, available,
            available ? r!.Version.ToString() : null, available ? r!.Notes : null, available ? r!.PageUrl : null,
            available && r!.Installable, ServiceSwitch.InstallFolderIsProtected(),
            available ? r!.Problem : null,
            _last?.ErrorCode is { } code ? Strings.Get("selfupdate." + code) : _last?.Error, _last?.CheckedAt, _busy, _last?.ErrorCode,
            Volatile.Read(ref _download)?.Open == true);
    }

    private void Publish() => _changed(Describe());

    /// <summary>Asks GitHub. <paramref name="manual"/> (the user asked) ignores the setting and a running game.</summary>
    public async Task<SelfUpdateState> CheckAsync(bool manual) => (await RunAsync(manual)).State;

    /// <summary>The check itself; <c>Ran</c> is false when it was skipped (setting off, game running) or another one was already running.</summary>
    private async Task<(SelfUpdateState State, bool Ran)> RunAsync(bool manual)
    {
        // A skipped pass counts as an attempt too: opening a page right after it does not start another one.
        if (!manual) _schedule.Attempted();
        if (!manual && (!_net.Allows(CouchHud.Core.Net.NetService.Updates) || _gameRunning())) return (Describe(), false);
        if (manual && !_net.Allows(CouchHud.Core.Net.NetService.Updates))
        {
            // Privacy: switched off (or Offline). Said plainly instead of a failed request.
            _last = new SelfUpdateCheck(null, false, false, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Strings.Get(_net.Offline ? "net.blockedOffline" : "net.blockedService", "github.com"));
            Publish();
            return (Describe(), false);
        }
        if (!await _gate.WaitAsync(0)) return (Describe(), false);
        try
        {
            _busy = manual ? Strings.Get("selfupdate.checking") : null;
            if (manual) Publish();
            _last = await _service.CheckAsync(CatalogRules.AppVersion, manual: manual);
            _schedule.Record(_last.Error is null);
            _busy = null;
            var state = Describe();
            Publish();
            // Once per version, and only when it can really be installed (not while the signature is still missing).
            if (state.Available && state.Ready && UpdateAnnouncements.SelfIsNew(_service.Announced, state.Version))
            {
                _service.MarkAnnounced(state.Version!);
                _notify("CouchHUD", Strings.Ref("selfupdate.available", state.Version), FirstLine(state.Notes) ?? Strings.Ref("selfupdate.availableBody"));
            }
            return (state, true);
        }
        finally
        {
            _busy = null;
            _gate.Release();
        }
    }

    private static string? FirstLine(string? notes) =>
        notes?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);

    /// <summary>
    /// Downloads and verifies the setup, then streams exactly those verified bytes to the CouchHUD service, which verifies signature,
    /// hash and version again in its own protected folder and starts the setup (no UAC prompt, no file of ours is ever run elevated).
    /// The setup stops the service and this process and starts the shells again (restart-couchhud.ps1). Returns only when the
    /// service did not take over (refused, failed, or the setup did not end CouchHUD in time).
    /// </summary>
    public async Task<string> InstallAsync(Action? started = null)
    {
        if (_gameRunning()) throw new InvalidOperationException(Strings.Get("selfupdate.notInGame"));
        // Only the installed copy: a setup would put the new version in Program Files, not next to a dev build.
        if (!ServiceSwitch.InstallFolderIsProtected()) throw new InvalidOperationException(Strings.Get("selfupdate.installedOnly"));
        if (!await _gate.WaitAsync(0)) throw new InvalidOperationException(Strings.Get("selfupdate.busy"));
        try
        {
            // Only now this run is the one: a call turned away above never touches what the running one shows.
            started?.Invoke();
            var release = _last?.UpdateAvailable == true ? _last.Release : null;
            if (release is null) throw new InvalidOperationException(Strings.Get("selfupdate.noneKnown"));
            if (!release.Installable) throw new InvalidOperationException(release.Problem ?? Strings.Get("selfupdate.notInstallable"));
            var status = await AdminService.Ops.StatusAsync(fresh: true);
            if (status.Health != CouchHud.ServiceClient.ServiceHealth.Healthy)
            {
                throw new InvalidOperationException(Strings.Get("selfupdate.needsService", Strings.Get(CouchHud.ServiceClient.ServiceTexts.StateKey(status.State))));
            }

            VerifiedSetup setup;
            // Cancelling stops only the download: the half file goes with its folder (DownloadAsync deletes it), nothing is resumed, and a
            // new start downloads and verifies the whole setup again. The hand-over and a cancel race for one slot (DownloadCancel): a cancel
            // that came first stops the update even after the last byte and the hash, one that comes later reports false.
            using (var cancel = new DownloadCancel())
            {
                Volatile.Write(ref _download, cancel);
                _busy = Strings.Get("selfupdate.downloading", release.Version);
                Publish();
                try
                {
                    // A fresh, real folder every time (no junction planted in advance).
                    PathSafety.PrepareCleanDirectory(TempDir);
                    setup = await _service.DownloadAsync(release, TempDir, cancel.Token);
                    if (!cancel.TryHandOver())
                    {
                        // Cancel took the slot first (its flag may not be set yet): cancelled, never handed over.
                        setup.Dispose();
                        Console.Error.WriteLine("[selfupdate] download cancelled");
                        CleanTemp();
                        throw new InvalidOperationException(Strings.Get("selfupdate.cancelled"));
                    }
                }
                catch (Exception ex) when (cancel.Cancelled && ex is OperationCanceledException or CatalogException)
                {
                    Console.Error.WriteLine("[selfupdate] download cancelled");
                    CleanTemp();
                    throw new InvalidOperationException(Strings.Get("selfupdate.cancelled"));
                }
                catch (CatalogException ex)
                {
                    Console.Error.WriteLine($"[selfupdate] rejected: {ex.Message}");
                    throw new InvalidOperationException(ex.Message);
                }
                finally
                {
                    Volatile.Write(ref _download, null);
                }
            }

            using (setup)
            {
                if (setup.ChecksumFile is null || string.IsNullOrEmpty(setup.Signature)) throw new InvalidOperationException(Strings.Get("selfupdate.notInstallable"));
                LogAuthenticode(setup.Path);
                var header = new CouchHud.Service.Contracts.SetupStreamArgs(Path.GetFileName(setup.Path), setup.Length, Convert.ToBase64String(setup.ChecksumFile), setup.Signature);
                _busy = Strings.Get("selfupdate.handingOver", release.Version);
                Publish();
                // From the moment the service runs the setup, the setup stops the service: that is no reason for alarm or a Windows lock.
                AdminService.ExpectRestart(TimeSpan.FromMinutes(15));
                var result = await AdminService.Ops.InstallCouchHudUpdateAsync(header, setup.ReadFromStart(), null);
                if (!result.Started)
                {
                    AdminService.ExpectRestart(TimeSpan.Zero);
                    Console.Error.WriteLine($"[selfupdate] the service did not take the setup: {result.Result}");
                    return Strings.Get("selfupdate.serviceRefused", AdminService.Message(result.Result));
                }
                if (result.Job is { State: "failed" })
                {
                    AdminService.ExpectRestart(TimeSpan.Zero);
                    Console.Error.WriteLine("[selfupdate] the service reports the setup as failed");
                    return Strings.Get("selfupdate.serviceFailed");
                }
                // Started: the setup is running and will end this process. A lost connection to the stopping service is expected.
                _busy = Strings.Get("selfupdate.installing", release.Version);
                Publish();
            }
            await Task.Delay(TimeSpan.FromMinutes(10));
            return Strings.Get("selfupdate.slow");
        }
        finally
        {
            _busy = null;
            Publish();
            _gate.Release();
        }
    }

    /// <summary>
    /// Cancels a running download of the setup; false when none runs (not started, or the hand-over has begun). True means the update
    /// really stops: the slot is taken before the hand-over can take it (<see cref="DownloadCancel"/>).
    /// </summary>
    public bool CancelDownload() => Volatile.Read(ref _download)?.Cancel() == true;

    /// <summary>Authenticode is not required (the signature of the checksum file is); a signature is logged when there is one.</summary>
    private static void LogAuthenticode(string file)
    {
        try
        {
#pragma warning disable SYSLIB0057 // the only API that reads the Authenticode signature of a file
            using var cert = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(file);
#pragma warning restore SYSLIB0057
            Console.Error.WriteLine($"[selfupdate] setup carries an Authenticode signature: {cert.Subject}");
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException)
        {
            Console.Error.WriteLine("[selfupdate] setup has no Authenticode signature (expected until builds are code-signed)");
        }
    }

    private void CleanTemp()
    {
        try
        {
            if (Directory.Exists(TempDir)) Directory.Delete(TempDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A setup that is still running keeps its file; next time.
        }
    }

    private readonly UpdateCheckSchedule _schedule = new();

    /// <summary>
    /// About 30 seconds after the start, one retry a few minutes later after a failure (the network may not be ready at logon), then every
    /// 12 hours (GitHub allows 60 requests an hour without a token; the ETag makes most of them free, and the service backs off by itself
    /// when GitHub limits the rate). Not while a game runs (<see cref="CheckAsync"/>).
    /// </summary>
    public void StartBackgroundChecks()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(UpdateCheckSchedule.FirstDelay);
            while (true)
            {
                var ran = false;
                try
                {
                    ran = (await RunAsync(manual: false)).Ran;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[selfupdate] background check failed: {ex.Message}");
                    _schedule.Record(false);
                    ran = true;
                }
                await Task.Delay(_schedule.NextDelay(ran));
            }
        });
    }

    /// <summary>The Downloads page or the update settings opened: a quiet (non-manual) check when the last good one is older than an hour.</summary>
    public void CheckIfStale()
    {
        if (_schedule.DueOnOpen()) _ = Task.Run(async () =>
        {
            try
            {
                await CheckAsync(manual: false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[selfupdate] check on open failed: {ex.Message}");
            }
        });
    }

    // -----------------------------------------------------------------------
    // Marker for the guard

    /// <summary>A setup is alive and closing CouchHUD on purpose: the self-update (marker in the user's config folder)
    /// or the installer / uninstall (marker in the program folder, which also works across sessions).</summary>
    public static bool UpdateInProgress(string dataDir) =>
        UpdateMarker.IsActiveAny([AppContext.BaseDirectory, dataDir], DateTimeOffset.UtcNow, SetupProbe.ProcessAlive);
}
