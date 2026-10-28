using System.Runtime.InteropServices;

namespace CouchHud.Privileged;

/// <summary>One pending update from a search: its UpdateID (GUID, "D" format, lower case), title and largest download.</summary>
public sealed record FoundUpdate(string Id, string Title, long MaxDownloadBytes)
{
    /// <summary>
    /// The one form of an update id for every comparison (the service's cache, the request, the agent's results): a GUID in "D" format,
    /// lower case; anything else trimmed and lower case.
    /// </summary>
    public static string NormalizeId(string id) =>
        Guid.TryParse(id, out var guid) ? guid.ToString("D") : id.Trim().ToLowerInvariant();
}

/// <summary>What a download and installation came to. <see cref="Error"/> is a short code ("downloadFailed:4", "installFailed:4"), never text from Windows.</summary>
public sealed record UpdateInstallOutcome(int Installed, int Failed, bool RebootRequired, string? Error);

/// <summary>
/// Windows Update for the service: search, then download and install a chosen part of what a fresh search finds, with progress.
/// Runs as SYSTEM; never starts a reboot.
/// </summary>
public interface IWindowsUpdateSession
{
    /// <summary>Everything pending (<see cref="WindowsUpdateAgent.SearchCriteria"/>).</summary>
    IReadOnlyList<FoundUpdate> Search(CancellationToken cancellation);

    /// <summary>
    /// Searches again (with the same criteria as <see cref="Search"/>, so hidden and driver updates stay out also without ids) and
    /// downloads and installs the updates whose id is in <paramref name="ids"/> (null: all found; ids in <see cref="FoundUpdate.NormalizeId"/>
    /// form). Ids the fresh search no longer finds are skipped. <paramref name="progress"/> gets 0–100 over download and installation;
    /// <paramref name="bytes"/> the agent's own byte counters while it downloads (done, total; checked by <see cref="Core.Updates.UpdateSize.Transfer"/>),
    /// and (0, 0) once the installation begins.
    /// </summary>
    UpdateInstallOutcome Install(IReadOnlySet<string>? ids, Action<int> progress, CancellationToken cancellation, Action<long, long>? bytes = null);
}

/// <summary>
/// The Windows Update Agent through COM (late bound, as <see cref="WindowsUpdateAgent"/>). Download and installation run
/// asynchronously (BeginDownload / BeginInstall), which needs real callback objects (the P0 spike: passing none fails); progress
/// comes from polling the job's GetProgress, the completion from the callback. No source prompts, quiet installation.
/// If the asynchronous call is not available the synchronous one runs (no progress then).
/// </summary>
public sealed class WindowsUpdateSession(string clientApplicationId = "CouchHud") : IWindowsUpdateSession
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>OperationResultCode: orcSucceeded and orcSucceededWithErrors.</summary>
    private static bool Succeeded(int code) => code is 2 or 3;

    public IReadOnlyList<FoundUpdate> Search(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        dynamic session = NewSession();
        dynamic found = session.CreateUpdateSearcher().Search(WindowsUpdateAgent.SearchCriteria).Updates;
        var list = new List<FoundUpdate>();
        int count = found.Count;
        for (var i = 0; i < count; i++)
        {
            dynamic u = found.Item(i);
            FoundUpdate entry = Describe(u);
            list.Add(entry);
        }
        return list;
    }

    public UpdateInstallOutcome Install(IReadOnlySet<string>? ids, Action<int> progress, CancellationToken cancellation, Action<long, long>? bytes = null)
    {
        cancellation.ThrowIfCancellationRequested();
        dynamic session = NewSession();
        dynamic found = session.CreateUpdateSearcher().Search(WindowsUpdateAgent.SearchCriteria).Updates;
        dynamic chosen = WindowsUpdateAgent.CreateCom("Microsoft.Update.UpdateColl");
        int count = found.Count;
        for (var i = 0; i < count; i++)
        {
            dynamic u = found.Item(i);
            FoundUpdate entry = Describe(u);
            if (ids is not null && !ids.Contains(FoundUpdate.NormalizeId(entry.Id))) continue;
            if (!(bool)u.EulaAccepted) u.AcceptEula();
            chosen.Add(u);
        }
        int total = chosen.Count;
        if (total == 0) return new UpdateInstallOutcome(0, 0, false, null);
        progress(0);

        dynamic downloader = session.CreateUpdateDownloader();
        downloader.Updates = chosen;
        dynamic downloaded = RunJob(
            begin: done => downloader.BeginDownload(new DownloadProgressCallback(), new DownloadCompletedCallback(done), null),
            end: job => downloader.EndDownload(job),
            fallback: () => downloader.Download(),
            percent => progress(percent / 2), cancellation,
            // IDownloadProgress: what is really loaded (express and delta parts), not the full package of MaxDownloadSize.
            state =>
            {
                object? loaded = state.TotalBytesDownloaded, toLoad = state.TotalBytesToDownload;
                var (done, total) = Core.Updates.UpdateSize.Transfer(loaded, toLoad);
                bytes?.Invoke(done, total);
            });
        bytes?.Invoke(0, 0);
        if (!Succeeded((int)downloaded.ResultCode)) return new UpdateInstallOutcome(0, total, false, $"downloadFailed:{(int)downloaded.ResultCode}");

        dynamic ready = WindowsUpdateAgent.CreateCom("Microsoft.Update.UpdateColl");
        for (var i = 0; i < total; i++)
        {
            dynamic u = chosen.Item(i);
            if ((bool)u.IsDownloaded) ready.Add(u);
        }
        int readyCount = ready.Count;
        if (readyCount == 0) return new UpdateInstallOutcome(0, total, false, "nothingDownloaded");

        dynamic installer = session.CreateUpdateInstaller();
        // Nobody is there to answer: no prompts for a source medium, and installers are told to stay quiet (IUpdateInstaller2).
        installer.AllowSourcePrompts = false;
        installer.ForceQuiet = true;
        installer.Updates = ready;
        dynamic outcome = RunJob(
            begin: done => installer.BeginInstall(new InstallationProgressCallback(), new InstallationCompletedCallback(done), null),
            end: job => installer.EndInstall(job),
            fallback: () => installer.Install(),
            percent => progress(50 + percent / 2), cancellation);
        var installed = 0;
        for (var i = 0; i < readyCount; i++)
        {
            if (Succeeded((int)outcome.GetUpdateResult(i).ResultCode)) installed++;
        }
        int code = outcome.ResultCode;
        progress(100);
        return new UpdateInstallOutcome(installed, total - installed, (bool)outcome.RebootRequired, Succeeded(code) ? null : $"installFailed:{code}");
    }

    private dynamic NewSession()
    {
        dynamic session = WindowsUpdateAgent.CreateCom("Microsoft.Update.Session");
        session.ClientApplicationID = clientApplicationId;
        return session;
    }

    private static FoundUpdate Describe(dynamic u)
    {
        string id = u.Identity.UpdateID;
        string title = u.Title ?? "";
        long size;
        try
        {
            size = Convert.ToInt64(u.MaxDownloadSize);
        }
        catch (Exception ex) when (ex is InvalidCastException or OverflowException or FormatException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            size = 0;
        }
        return new FoundUpdate(FoundUpdate.NormalizeId(id), title, size);
    }

    /// <summary>
    /// Begin…, then poll GetProgress until the completed callback fires (or the job says it is done), then End…. Cancellation asks
    /// the job to abort and still waits for the end (End… must be called). Without the asynchronous call: the synchronous one.
    /// </summary>
    private static dynamic RunJob(Func<ManualResetEventSlim, dynamic> begin, Func<dynamic, dynamic> end, Func<dynamic> fallback, Action<int> percent, CancellationToken cancellation,
        Action<dynamic>? details = null)
    {
        using var done = new ManualResetEventSlim();
        dynamic job;
        try
        {
            job = begin(done);
        }
        catch (Exception ex) when (ex is InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException
            || (ex is COMException com && com.HResult == unchecked((int)0x80004002)))   // E_NOINTERFACE: the callbacks were not taken
        {
            return fallback();
        }
        var aborted = false;
        while (!done.Wait(PollInterval))
        {
            if (cancellation.IsCancellationRequested && !aborted)
            {
                aborted = true;
                try { job.RequestAbort(); } catch (COMException) { }
            }
            try
            {
                if ((bool)job.IsCompleted) break;
                dynamic state = job.GetProgress();
                percent(Math.Clamp((int)state.PercentComplete, 0, 100));
                details?.Invoke(state);
            }
            catch (Exception ex) when (ex is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                // A progress that cannot be read is no reason to stop.
            }
        }
        var result = end(job);
        cancellation.ThrowIfCancellationRequested();
        return result;
    }

    // ---- the callback interfaces of wuapi.h (IUnknown-based; the arguments are not used, so they stay raw pointers) ----

    [ComImport, Guid("8C3F1CDD-6173-4591-AEBD-A56A53CA77C1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDownloadProgressChangedCallback
    {
        void Invoke(IntPtr downloadJob, IntPtr callbackArgs);
    }

    [ComImport, Guid("77254866-9F5B-4C8E-B9E2-C77A8530D64B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDownloadCompletedCallback
    {
        void Invoke(IntPtr downloadJob, IntPtr callbackArgs);
    }

    [ComImport, Guid("E01402D5-F8DA-43BA-A012-38894BD048F1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IInstallationProgressChangedCallback
    {
        void Invoke(IntPtr installationJob, IntPtr callbackArgs);
    }

    [ComImport, Guid("45F4F6F3-D602-4F98-9A8A-3EFA152AD2D3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IInstallationCompletedCallback
    {
        void Invoke(IntPtr installationJob, IntPtr callbackArgs);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class DownloadProgressCallback : IDownloadProgressChangedCallback
    {
        public void Invoke(IntPtr downloadJob, IntPtr callbackArgs)
        {
            // Progress is polled from the job.
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class DownloadCompletedCallback(ManualResetEventSlim done) : IDownloadCompletedCallback
    {
        public void Invoke(IntPtr downloadJob, IntPtr callbackArgs) => Signal(done);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class InstallationProgressCallback : IInstallationProgressChangedCallback
    {
        public void Invoke(IntPtr installationJob, IntPtr callbackArgs)
        {
        }
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class InstallationCompletedCallback(ManualResetEventSlim done) : IInstallationCompletedCallback
    {
        public void Invoke(IntPtr installationJob, IntPtr callbackArgs) => Signal(done);
    }

    private static void Signal(ManualResetEventSlim done)
    {
        try
        {
            done.Set();
        }
        catch (ObjectDisposedException)
        {
            // A late callback after the wait ended.
        }
    }
}
