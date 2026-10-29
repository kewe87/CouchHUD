using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CouchHud.Core.Platform;
using CouchHud.Core.Updates;
using CouchHud.Core.Localization;

namespace CouchHud.Shell.Services;

/// <summary>What the Updates page shows; sent again whenever something changes.</summary>
public sealed record UpdateState(
    IReadOnlyList<UpdateItem> Windows,
    IReadOnlyList<UpdateItem> Apps,
    bool RebootRequired,
    bool WingetAvailable,
    string? WindowsError,
    string? Busy,
    long? CheckedAt);

/// <summary>The result of one install or upgrade: the line for the toast and whether it worked (each run its own, never shared).</summary>
public sealed record UpdateRun(string Message, bool Ok);

/// <summary>
/// Windows Update (searched in-process through the Windows Update Agent, installed
/// by the CouchHUD service) and app updates through winget.
/// </summary>
public sealed class UpdateService
{
    private const string SearchCriteria = WindowsUpdateAgent.SearchCriteria;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateState _state = new([], [], false, true, null, null, null);

    public event Action<UpdateState>? Changed;

    /// <summary>Privacy (Settings > Privacy): the reason why CouchHUD must not look for or fetch Windows and app updates now, or null. Search and install both ask.</summary>
    public Func<string?>? Blocked { get; set; }

    public UpdateState State => _state;

    private void Set(UpdateState state)
    {
        _state = state;
        Changed?.Invoke(state);
    }

    public async Task<UpdateState> CheckAsync()
    {
        if (Blocked?.Invoke() is { } why)
        {
            Set(_state with { WindowsError = why, Busy = null });
            return _state;
        }
        if (!await _gate.WaitAsync(0)) return _state;
        try
        {
            Set(_state with { Busy = Strings.Get("update.checking") });
            var windowsTask = Task.Run(SearchWindows);
            var appsTask = SearchWingetAsync();
            await Task.WhenAll(windowsTask, appsTask);
            var (windows, windowsError) = windowsTask.Result;
            var (apps, wingetAvailable) = appsTask.Result;
            Set(new UpdateState(windows, apps, RebootRequired(), wingetAvailable, windowsError, null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            return _state;
        }
        finally
        {
            if (_state.Busy is not null) Set(_state with { Busy = null });
            _gate.Release();
        }
    }

    /// <summary>
    /// Upgrades one winget package, or all with <paramref name="id"/> null; returns a line for the toast and whether it worked.
    /// <paramref name="started"/> runs only once this run really has the gate: a second call that is turned away as busy never touches
    /// what the running one shows.
    /// </summary>
    /// <paramref name="progress"/> hears where winget is (package n of m, phase, bytes or percent when winget says them), throttled.
    public async Task<UpdateRun> UpgradeAppsAsync(string? id, Action? started = null, Action<AppUpgradeProgress>? progress = null)
    {
        if (Blocked?.Invoke() is { } why) throw new InvalidOperationException(why);
        if (!await _gate.WaitAsync(0)) throw new InvalidOperationException(Strings.Get("update.busy"));
        try
        {
            started?.Invoke();
            var name = id is null ? Strings.Get("update.allApps") : _state.Apps.FirstOrDefault(a => a.Id == id)?.Name ?? id;
            Set(_state with { Busy = Strings.Get("update.updating", name) });
            using var reporter = progress is null ? null
                : new WingetProgressReporter(id, _state.Apps.GroupBy(a => a.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.Ordinal),
                    _state.Apps.Count, progress);
            int exit;
            try
            {
                (exit, _) = await RunWingetAsync(Winget.UpgradeArguments(id), TimeSpan.FromMinutes(60), reporter is null ? null : chunk => reporter.Feed(chunk.Span), keepOutput: false);
            }
            catch (TimeoutException)
            {
                // winget was killed: a failed run with its own message (toast and line), not an exception nobody observes.
                Console.Error.WriteLine($"[updates] winget upgrade {id ?? "--all"} timed out");
                return new(Strings.Get("update.timeout"), false);
            }
            Console.Error.WriteLine($"[updates] winget upgrade {id ?? "--all"} exited with {exit}");
            return exit == 0 ? new(Strings.Get("update.updated", name), true) : new(Strings.Get("update.failedNamed", name, exit), false);
        }
        finally
        {
            Set(_state with { Busy = null });
            _gate.Release();
        }
    }

    /// <summary>
    /// Downloads and installs all pending Windows updates through the CouchHUD service (no UAC prompt): the service searches and
    /// installs as SYSTEM and reports its progress as a job; no automatic restart. <paramref name="started"/> as for
    /// <see cref="UpgradeAppsAsync"/>.
    /// </summary>
    /// <paramref name="progress"/> gets the share (0..1) and the agent's real byte counters while it downloads (done, total; 0, 0 otherwise).
    public async Task<UpdateRun> InstallWindowsAsync(Action<double, long, long>? progress = null, Action? started = null)
    {
        if (Blocked?.Invoke() is { } why) throw new InvalidOperationException(why);
        if (!await _gate.WaitAsync(0)) throw new InvalidOperationException(Strings.Get("update.busy"));
        try
        {
            started?.Invoke();
            Set(_state with { Busy = Strings.Get("update.installingWindows") });
            var jobProgress = new Progress<CouchHud.Service.Contracts.JobData>(job =>
            {
                Set(_state with { Busy = Strings.Get("update.installingWindowsPercent", Math.Clamp(job.Percent, 0, 100)) });
                var (done, total) = UpdateSize.Transfer(job.BytesDone, job.BytesTotal);
                progress?.Invoke(Math.Clamp(job.Percent, 0, 100) / 100.0, done, total);
            });
            var result = await AdminService.Ops.InstallWindowsUpdatesAsync(jobProgress);
            if (!result.Result.Ok) return new($"Windows Update: {AdminService.Message(result.Result)}", false);
            if (result.Job is not { } job) return new(Strings.Get("update.noResult"), false);
            if (job.State != "done") return new($"Windows Update: {Strings.Get("update.windowsJobFailed")}", false);
            Set(_state with { RebootRequired = job.RebootRequired || _state.RebootRequired });
            return new(job.Installed == 0
                ? Strings.Get("update.noneInstalled")
                : Strings.Plural("update.installed", job.Installed) + (job.RebootRequired ? Strings.Get("update.rebootNeeded") : ""), true);
        }
        finally
        {
            Set(_state with { Busy = null });
            _gate.Release();
        }
    }

    // -----------------------------------------------------------------------
    // winget

    private static async Task<(IReadOnlyList<UpdateItem> Items, bool Available)> SearchWingetAsync()
    {
        try
        {
            var (exit, output) = await RunWingetAsync(["upgrade", "--accept-source-agreements", "--disable-interactivity"], TimeSpan.FromMinutes(3));
            var items = Winget.ParseUpgrades(output);
            if (items.Count == 0 && exit != 0) Console.Error.WriteLine($"[updates] winget upgrade exited with {exit}");
            return (items, true);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return ([], false);
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine("[updates] winget timed out");
            return ([], true);
        }
    }

    /// <summary>Output kept for the update list (a table of some KB); more is dropped.</summary>
    private const int MaxKeptOutput = 4 * 1024 * 1024;

    /// <summary>Reads winget's console stream in pieces (it animates with carriage returns), tells <paramref name="onOutput"/> about each (an empty piece at the end) and keeps a capped copy if asked.</summary>
    private static Task<string> ReadOutputAsync(StreamReader reader, Action<ReadOnlyMemory<char>>? onOutput, bool keep) =>
        OutputPump.ReadAsync(reader, onOutput, keep, MaxKeptOutput, ex => Console.Error.WriteLine($"[updates] progress callback failed, reading on: {ex.GetType().Name}"));

    private static async Task<(int Exit, string Output)> RunWingetAsync(IReadOnlyList<string> args, TimeSpan timeout,
        Action<ReadOnlyMemory<char>>? onOutput = null, bool keepOutput = true)
    {
        var info = new ProcessStartInfo("winget")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info) ?? throw new System.ComponentModel.Win32Exception("winget not started");
        var stdout = ReadOutputAsync(process.StandardOutput, onOutput, keepOutput);
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException();
        }
        await stderr;
        return (process.ExitCode, await stdout);
    }

    // -----------------------------------------------------------------------
    // Windows Update Agent (COM, late bound)

    private static (IReadOnlyList<UpdateItem>, string?) SearchWindows()
    {
        try
        {
            dynamic session = CreateCom("Microsoft.Update.Session");
            session.ClientApplicationID = "CouchHud";
            dynamic searcher = session.CreateUpdateSearcher();
            dynamic result = searcher.Search(SearchCriteria);
            dynamic updates = result.Updates;
            var list = new List<UpdateItem>();
            int count = updates.Count;
            for (var i = 0; i < count; i++)
            {
                dynamic u = updates.Item(i);
                string id = u.Identity.UpdateID;
                string title = u.Title;
                // MaxDownloadSize alone is the full package with every variant (gigabytes for a few MB of express or delta download):
                // a size only when min and max agree, none for what is already downloaded.
                var downloaded = ComBool(() => u.IsDownloaded);
                var size = UpdateSize.Shown(ComValue(() => u.MinDownloadSize), ComValue(() => u.MaxDownloadSize), downloaded);
                bool important = u.AutoSelectOnWebSites;
                list.Add(new UpdateItem(id, "windows", title, null, null, size, important, downloaded));
            }
            return (list, null);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            Console.Error.WriteLine($"[updates] Windows Update search failed: {ex.Message}");
            return ([], Strings.Get("update.windowsUnreachable"));
        }
    }

    /// <summary>A property that may be missing on an older agent (IUpdate2+) or fail: null then.</summary>
    private static object? ComValue(Func<object?> read)
    {
        try { return read(); }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { return null; }
    }

    private static bool ComBool(Func<object?> read) => ComValue(read) is true;

    private static bool RebootRequired()
    {
        try
        {
            dynamic info = CreateCom("Microsoft.Update.SystemInfo");
            return info.RebootRequired;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return false;
        }
    }

    private static dynamic CreateCom(string progId) => WindowsUpdateAgent.CreateCom(progId);
}
