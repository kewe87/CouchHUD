using CouchHud.Privileged;
using CouchHud.Service.Authorization;
using CouchHud.Service.Contracts;
using CouchHud.Service.Jobs;

namespace CouchHud.Service.Tests;

/// <summary>Windows Update search and installation as jobs, GetJob and its owner check (P6).</summary>
public class WindowsUpdateJobTests
{
    private static readonly string A = "0b2a3f4e-1111-4c3d-9e8a-0000000000a1";
    private static readonly string B = "0b2a3f4e-1111-4c3d-9e8a-0000000000b2";
    private static readonly string Unknown = "0b2a3f4e-1111-4c3d-9e8a-0000000000c3";

    private static byte[] Request(Operation op) => FrameCodec.Encode(ProtocolJson.SerializeRequest(op));

    private static byte[] Install(params string[]? ids) =>
        FrameCodec.Encode(ProtocolJson.SerializeRequest(Operation.InstallWindowsUpdates, new InstallWindowsUpdatesArgs(ids)));

    private static async Task<byte[]> OneAsync(OperationsB ops, byte[] frame, CallerInfo? caller = null) =>
        Assert.Single(await ops.Harness.ExchangeAsync(caller ?? Callers.User(), frame));

    private static OperationsB WithPending()
    {
        var ops = new OperationsB();
        ops.WindowsUpdate.Pending.Add(new FoundUpdate(A, "Security update A", 1000));
        ops.WindowsUpdate.Pending.Add(new FoundUpdate(B, new string('t', 500), 2000));
        return ops;
    }

    [Fact]
    public async Task A_search_job_reports_what_is_pending()
    {
        using var ops = WithPending();
        var id = OperationsB.JobId(await OneAsync(ops, Request(Operation.SearchWindowsUpdates)));
        var job = await ops.WaitForJobAsync(id);
        Assert.Equal("done", job.State);
        Assert.Equal([A, B], job.Updates!.Select(u => u.Id));
        Assert.Equal(200, job.Updates![1].Title.Length);   // long titles are cut
    }

    [Fact]
    public async Task Ids_from_the_service_s_last_search_are_installed_and_a_reboot_is_reported()
    {
        using var ops = WithPending();
        await ops.WaitForJobAsync(OperationsB.JobId(await OneAsync(ops, Request(Operation.SearchWindowsUpdates))));
        var job = await ops.WaitForJobAsync(OperationsB.JobId(await OneAsync(ops, Install(A.ToUpperInvariant()))));
        Assert.Equal("done", job.State);
        Assert.Equal(1, job.Installed);
        Assert.True(job.RebootRequired);
        Assert.Equal([A], Assert.Single(ops.WindowsUpdate.Installs)!);
    }

    [Fact]
    public async Task Ids_that_were_not_in_the_service_s_search_are_refused()
    {
        using var ops = WithPending();
        // No search yet: no id is known.
        Assert.Equal(ErrorCodes.Denied, OperationsB.Code(await OneAsync(ops, Install(A))));
        await ops.WaitForJobAsync(OperationsB.JobId(await OneAsync(ops, Request(Operation.SearchWindowsUpdates))));
        Assert.Equal(ErrorCodes.Denied, OperationsB.Code(await OneAsync(ops, Install(Unknown))));
        Assert.Equal(ErrorCodes.Denied, OperationsB.Code(await OneAsync(ops, Install(A, Unknown))));
        Assert.Empty(ops.WindowsUpdate.Installs);
    }

    [Fact]
    public async Task Without_ids_everything_from_the_last_search_is_installed()
    {
        using var ops = WithPending();
        await ops.WaitForJobAsync(OperationsB.JobId(await OneAsync(ops, Request(Operation.SearchWindowsUpdates))));
        ops.WindowsUpdate.Pending.Add(new FoundUpdate(Unknown, "came later", 1));
        var job = await ops.WaitForJobAsync(OperationsB.JobId(await OneAsync(ops, Install(null))));
        Assert.Equal(2, job.Installed);
        Assert.Equal(new HashSet<string> { A, B }, Assert.Single(ops.WindowsUpdate.Installs)!.ToHashSet());
    }

    [Fact]
    public async Task Without_ids_and_without_a_search_everything_pending_is_installed()
    {
        using var ops = WithPending();
        var job = await ops.WaitForJobAsync(OperationsB.JobId(await OneAsync(ops, Install(null))));
        Assert.Equal(2, job.Installed);
        Assert.Null(Assert.Single(ops.WindowsUpdate.Installs));
    }

    [Fact]
    public async Task Only_one_Windows_Update_job_runs_at_a_time_machine_wide()
    {
        using var ops = WithPending();
        using var gate = new ManualResetEventSlim();
        ops.WindowsUpdate.Gate = gate;
        var first = OperationsB.JobId(await OneAsync(ops, Install(null)));
        Assert.Equal(ErrorCodes.Busy, OperationsB.Code(await OneAsync(ops, Request(Operation.SearchWindowsUpdates), Callers.User(Callers.OtherSid))));
        Assert.Equal(ErrorCodes.Busy, OperationsB.Code(await OneAsync(ops, Install(null))));
        gate.Set();
        Assert.Equal("done", (await ops.WaitForJobAsync(first)).State);
        Assert.Equal(ErrorCodes.Ok, OperationsB.Code(await OneAsync(ops, Request(Operation.SearchWindowsUpdates))));
    }

    [Fact]
    public async Task Only_the_owner_or_an_elevated_administrator_reads_a_job()
    {
        using var ops = WithPending();
        var id = OperationsB.JobId(await OneAsync(ops, Request(Operation.SearchWindowsUpdates)));
        await ops.WaitForJobAsync(id);

        Assert.True((await ops.GetJobAsync(id)).Ok);
        var other = await ops.GetJobAsync(id, Callers.User(Callers.OtherSid));
        Assert.Equal(ErrorCodes.Denied, other.Code);
        Assert.Null(other.Data);
        Assert.True((await ops.GetJobAsync(id, Callers.ElevatedAdmin() with { Sid = Callers.OtherSid })).Ok);
        // An unknown id looks the same as somebody else's.
        Assert.Equal(ErrorCodes.Denied, (await ops.GetJobAsync(new string('0', 32))).Code);
    }

    [Fact]
    public async Task A_failing_agent_fails_the_job_without_text_from_Windows()
    {
        using var ops = new OperationsB();
        var agent = new ThrowingUpdate();
        var jobs = new JobStore(new FakeClock());
        var wu = new Updates.WindowsUpdateJobs(agent, jobs, new Updates.UpdateSearchCache(), ops.Log, new Logging.RecentEvents(new FakeClock()), CancellationToken.None);
        var id = OperationsB.JobId(wu.StartSearch(Callers.User()));
        for (var i = 0; i < 200 && jobs.Find(id)!.IsRunning; i++) await Task.Delay(25);
        var job = jobs.Find(id)!.Snapshot();
        Assert.Equal("failed", job.State);
        Assert.StartsWith("error:0x", job.Message);
        Assert.DoesNotContain("secret", job.Message);
        Assert.False(wu.Busy);
    }

    private sealed class ThrowingUpdate : IWindowsUpdateSession
    {
        public IReadOnlyList<FoundUpdate> Search(CancellationToken cancellation) => throw new System.Runtime.InteropServices.COMException("secret detail", unchecked((int)0x8024402C));

        public UpdateInstallOutcome Install(IReadOnlySet<string>? ids, Action<int> progress, CancellationToken cancellation, Action<long, long>? bytes = null) => throw new NotSupportedException();
    }

    [Fact]
    public void Searches_and_installations_have_their_own_rate_limits()
    {
        Assert.Equal(RateBucket.WindowsUpdates, RateLimiter.BucketFor(Operation.InstallWindowsUpdates));
        Assert.Equal(RateBucket.UpdateSearch, RateLimiter.BucketFor(Operation.SearchWindowsUpdates));
        Assert.Equal(RateBucket.Setups, RateLimiter.BucketFor(Operation.InstallCouchHudUpdate));
        Assert.Equal(RateBucket.Setups, RateLimiter.BucketFor(Operation.InstallCaseHud));
    }

    [Fact]
    public void The_job_store_stays_bounded_and_keeps_running_jobs()
    {
        var clock = new FakeClock();
        var store = new JobStore(clock);
        var running = Enumerable.Range(0, JobStore.MaxJobs).Select(_ => store.Create(Callers.UserSid, "x")!).ToList();
        Assert.Null(store.Create(Callers.UserSid, "x"));
        running[0].Complete(clock.Now);
        Assert.NotNull(store.Create(Callers.UserSid, "x"));
        Assert.Null(store.Find(running[0].Id));
        Assert.All(running.Skip(1), j => Assert.NotNull(store.Find(j.Id)));
        Assert.Matches("^[0-9a-f]{32}$", running[1].Id);
    }

    [Fact]
    public async Task A_running_installation_reports_the_agent_s_real_byte_counters_and_drops_them_at_the_end()
    {
        using var ops = WithPending();
        using var gate = new ManualResetEventSlim();
        ops.WindowsUpdate.Gate = gate;
        ops.WindowsUpdate.Bytes = (12_000_000, 48_000_000);
        var id = OperationsB.JobId(await OneAsync(ops, Install(null)));
        JobData? running = null;
        for (var i = 0; i < 200 && running is not { BytesTotal: > 0 }; i++)
        {
            running = (await ops.GetJobAsync(id)).Data;
            if (running is not { BytesTotal: > 0 }) await Task.Delay(10);
        }
        Assert.Equal(12_000_000, running!.BytesDone);
        Assert.Equal(48_000_000, running.BytesTotal);
        gate.Set();
        var done = await ops.WaitForJobAsync(id);
        Assert.Equal("done", done.State);
        Assert.Equal(0, done.BytesDone);
        Assert.Equal(0, done.BytesTotal);
    }

    [Fact]
    public void Byte_counters_of_a_job_stay_sane()
    {
        var store = new JobStore(new FakeClock());
        var job = store.Create(Callers.UserSid, "x")!;
        job.Bytes(500, 100);
        Assert.Equal((100L, 100L), (job.Snapshot().BytesDone, job.Snapshot().BytesTotal));
        job.Bytes(-5, 100);
        Assert.Equal(0, job.Snapshot().BytesDone);
        job.Bytes(50, -1);
        Assert.Equal((0L, 0L), (job.Snapshot().BytesDone, job.Snapshot().BytesTotal));
    }
}
