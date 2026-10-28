using CouchHud.Privileged;
using CouchHud.Service.Authorization;
using CouchHud.Service.Contracts;
using CouchHud.Service.Dispatch;
using CouchHud.Service.Jobs;
using CouchHud.Service.Logging;

namespace CouchHud.Service.Updates;

/// <summary>
/// The result of the service's own last Windows Update search, machine-wide (updates are). <see cref="Operation.InstallWindowsUpdates"/>
/// may only name ids from it (<see cref="StateCheck.UpdateIdsFromSearch"/>): a client cannot make the service install an update it
/// picked by id from elsewhere (an update that is hidden, or one an administrator declined).
/// </summary>
public sealed class UpdateSearchCache
{
    private readonly Lock _lock = new();
    private HashSet<string>? _ids;

    public void Set(IEnumerable<string> ids)
    {
        var set = new HashSet<string>(ids.Select(Normalize), StringComparer.Ordinal);
        lock (_lock) _ids = set;
    }

    /// <summary>The ids of the last search, null when the service has not searched yet.</summary>
    public IReadOnlySet<string>? Last
    {
        get { lock (_lock) return _ids; }
    }

    /// <summary>True when there was a search and every id is in its result.</summary>
    public bool ContainsAll(IEnumerable<string> ids)
    {
        lock (_lock) return _ids is { } known && ids.All(i => known.Contains(Normalize(i)));
    }

    /// <summary>The same normalisation the agent uses for its results (<see cref="FoundUpdate.NormalizeId"/>).</summary>
    public static string Normalize(string id) => FoundUpdate.NormalizeId(id);
}

/// <summary>
/// Search and installation of Windows updates as jobs (design §3, P6). One Windows Update job at a time machine-wide (search and
/// installation share the slot: the agent does one thing at a time anyway). Never reboots; "reboot required" is reported.
/// </summary>
public sealed class WindowsUpdateJobs(IWindowsUpdateSession agent, JobStore jobs, UpdateSearchCache cache, IEventSink log, RecentEvents events, CancellationToken stopping)
{
    public const string SearchKind = "windowsUpdateSearch";
    public const string InstallKind = "windowsUpdateInstall";

    /// <summary>Most entries a search job reports (the validator takes at most this many ids back) and the longest title.</summary>
    public const int MaxEntries = RequestValidator.MaxUpdateIds;
    public const int MaxTitleChars = 200;

    private readonly ExclusiveSlot _slot = new();

    public bool Busy => _slot.IsTaken;

    public byte[] StartSearch(CallerInfo caller)
    {
        if (_slot.TryEnter() is not { } lease) return Dispatcher.Fail(ErrorCodes.Busy);
        if (jobs.Create(caller.Sid, SearchKind) is not { } job)
        {
            lease.Dispose();
            return Dispatcher.Fail(ErrorCodes.Busy);
        }
        events.Add(caller.Sid, "windowsUpdate", "search started");
        Run(job, lease, () =>
        {
            job.Progress(5, "searching");
            var found = agent.Search(stopping);
            cache.Set(found.Select(f => f.Id));
            var entries = found.Take(MaxEntries)
                .Select(f => new UpdateEntry(UpdateSearchCache.Normalize(f.Id), f.Title.Length <= MaxTitleChars ? f.Title : f.Title[..MaxTitleChars], Math.Max(0, f.MaxDownloadBytes)))
                .ToArray();
            log.Info($"[windowsUpdate] search by sid={caller.Sid}: {found.Count} pending");
            job.Complete(jobs.Clock.Now, "searched", updates: entries);
        });
        return ProtocolJson.Serialize(Response<JobStartedData>.Success(new JobStartedData(job.Id)));
    }

    /// <param name="ids">Ids from the last search (checked by <see cref="StateCheck.UpdateIdsFromSearch"/> and here again); null: everything
    /// the last search found, or everything pending when the service has not searched yet (as the installation of 0.4.0 did).</param>
    public byte[] StartInstall(CallerInfo caller, string[]? ids)
    {
        IReadOnlySet<string>? wanted;
        if (ids is not null)
        {
            // The check ran before the rate limit; the cache may have changed since (another search).
            if (!cache.ContainsAll(ids)) return Dispatcher.Fail(ErrorCodes.Denied);
            wanted = new HashSet<string>(ids.Select(UpdateSearchCache.Normalize), StringComparer.Ordinal);
        }
        else
        {
            wanted = cache.Last;
        }

        if (_slot.TryEnter() is not { } lease) return Dispatcher.Fail(ErrorCodes.Busy);
        if (jobs.Create(caller.Sid, InstallKind) is not { } job)
        {
            lease.Dispose();
            return Dispatcher.Fail(ErrorCodes.Busy);
        }
        events.Add(caller.Sid, "windowsUpdate", "installation started");
        log.Info($"[windowsUpdate] installation by sid={caller.Sid}: {(wanted is null ? "all pending" : $"{wanted.Count} chosen")}");
        Run(job, lease, () =>
        {
            if (wanted is { Count: 0 })
            {
                job.Complete(jobs.Clock.Now, "nothingToInstall");
                return;
            }
            job.Progress(1, "installing");
            var outcome = agent.Install(wanted, percent => job.Progress(Math.Clamp(percent, 0, 99)), stopping, job.Bytes);
            log.Info($"[windowsUpdate] installed {outcome.Installed}, failed {outcome.Failed}, reboot required {outcome.RebootRequired}, error {outcome.Error ?? "none"}");
            events.Add(caller.Sid, "windowsUpdate", $"installed {outcome.Installed}{(outcome.RebootRequired ? ", reboot required" : "")}");
            if (outcome.Error is null) job.Complete(jobs.Clock.Now, "installed", outcome.Installed, outcome.RebootRequired);
            else job.Fail(jobs.Clock.Now, outcome.Error, outcome.Installed, outcome.RebootRequired);
        });
        return ProtocolJson.Serialize(Response<JobStartedData>.Success(new JobStartedData(job.Id)));
    }

    /// <summary>On its own thread (COM calls block for minutes), never past the slot: the lease ends with the work.</summary>
    private void Run(Job job, IDisposable lease, Action work)
    {
        var hold = job.HoldEnd();
        _ = Task.Factory.StartNew(() =>
        {
            try
            {
                work();
            }
            catch (OperationCanceledException)
            {
                job.Fail(jobs.Clock.Now, "cancelled");
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The type and the HRESULT only: no text from Windows into the answer.
                log.Error($"[windowsUpdate] {job.Kind} failed: {ex.GetType().Name} 0x{ex.HResult:X8}");
                job.Fail(jobs.Clock.Now, $"error:0x{ex.HResult:X8}");
            }
            finally
            {
                lease.Dispose();
                hold.Dispose();
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }
}
