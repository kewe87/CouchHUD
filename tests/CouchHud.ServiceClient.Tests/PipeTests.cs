using System.Text;
using CouchHud.Service.Contracts;

namespace CouchHud.ServiceClient.Tests;

/// <summary>The client against an in-process fake server (random pipe name; the real service is never involved).</summary>
public class ClientPipeTests
{
    [Fact]
    public async Task Hello_over_a_checked_pipe()
    {
        await using var server = new FakePipeServer(_ => FakePipeServer.Hello("0.4.1"));
        var client = new ServiceClient(FakeScm.RunningHere(), server.Options());
        var reply = await client.CallAsync(Operation.Hello);
        Assert.Equal(CallOutcome.Ok, reply.Outcome);
        Assert.Equal("0.4.1", reply.DataAs(ProtocolJsonContext.Default.HelloData)!.ServiceVersion);
        Assert.Equal(["Hello"], server.Ops);
    }

    [Fact]
    public async Task A_server_that_is_not_the_services_process_gets_nothing()
    {
        await using var server = new FakePipeServer(_ => FakePipeServer.Ok());
        // The SCM says the service is another process than the one holding the pipe: squatting.
        var scm = new FakeScm(new ScmInfo(ScmState.Running, Environment.ProcessId + 1));
        var client = new ServiceClient(scm, server.Options());
        var reply = await client.CallAsync(Operation.SetAutoLogon, new SetAutoLogonArgs("secret-password"));
        Assert.Equal(CallOutcome.Squatted, reply.Outcome);
        await server.WaitSettledAsync(1);
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task An_unknown_server_process_counts_as_squatting()
    {
        await using var server = new FakePipeServer(_ => FakePipeServer.Ok());
        var client = new ServiceClient(FakeScm.RunningHere(), server.Options(), _ => null);
        Assert.Equal(CallOutcome.Squatted, (await client.CallAsync(Operation.Ping)).Outcome);
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task A_service_that_restarted_in_between_is_asked_again()
    {
        await using var server = new FakePipeServer(_ => FakePipeServer.Ok(new PingData(1)));
        var scm = new QueueScm(new ScmInfo(ScmState.Running, 999999), new ScmInfo(ScmState.Running, Environment.ProcessId));
        var client = new ServiceClient(scm, server.Options());
        Assert.Equal(CallOutcome.Ok, (await client.CallAsync(Operation.Ping)).Outcome);
    }

    [Fact]
    public async Task No_connection_at_all_when_the_scm_says_it_does_not_run()
    {
        await using var server = new FakePipeServer(_ => FakePipeServer.Ok());
        Assert.Equal(CallOutcome.NotInstalled, (await new ServiceClient(new FakeScm(ScmInfo.NotInstalled), server.Options()).CallAsync(Operation.Ping)).Outcome);
        Assert.Equal(CallOutcome.NotRunning, (await new ServiceClient(new FakeScm(new ScmInfo(ScmState.Stopped, 0)), server.Options()).CallAsync(Operation.Ping)).Outcome);
        // The service stopped because its pipe name was taken: whatever listens there now is a squatter.
        var squatted = new FakeScm(new ScmInfo(ScmState.Stopped, 0, Squatting.ServiceSpecificError, Squatting.ExitCode));
        Assert.Equal(CallOutcome.Squatted, (await new ServiceClient(squatted, server.Options()).CallAsync(Operation.Ping)).Outcome);
        Assert.Equal(0, server.Connections);
    }

    [Fact]
    public async Task No_pipe_is_unreachable_after_the_connect_timeout()
    {
        var options = new ServiceClientOptions("CouchHud.Test.missing." + Guid.NewGuid().ToString("N"), TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        var reply = await new ServiceClient(FakeScm.RunningHere(), options).CallAsync(Operation.Ping);
        Assert.Equal(CallOutcome.Unreachable, reply.Outcome);
    }

    [Fact]
    public async Task A_contradicting_answer_is_inconsistent()
    {
        await using var server = new FakePipeServer(_ => FakePipeServer.Raw("""{"v":1,"ok":true,"code":"denied"}"""));
        var reply = await new ServiceClient(FakeScm.RunningHere(), server.Options()).CallAsync(Operation.Ping);
        Assert.Equal(CallOutcome.Inconsistent, reply.Outcome);
    }

    [Fact]
    public async Task A_server_that_does_not_answer_times_out()
    {
        await using var server = new FakePipeServer(_ => null);
        var reply = await new ServiceClient(FakeScm.RunningHere(), server.Options(TimeSpan.FromMilliseconds(400))).CallAsync(Operation.Ping);
        Assert.Equal(CallOutcome.Timeout, reply.Outcome);
    }

    [Fact]
    public async Task A_refusal_keeps_its_code()
    {
        await using var server = new FakePipeServer(_ => FakePipeServer.Fail(ErrorCodes.NeedGrant));
        var reply = await new ServiceClient(FakeScm.RunningHere(), server.Options()).CallAsync(Operation.ReleaseLockPolicies, new ReleaseLockPoliciesArgs(["lock"]));
        Assert.Equal(CallOutcome.Refused, reply.Outcome);
        Assert.Equal(FailureKind.NeedGrant, ErrorMap.Classify(reply));
    }

    [Fact]
    public async Task The_request_passes_the_services_own_validator()
    {
        await using var server = new FakePipeServer(_ => FakePipeServer.Ok());
        await new ServiceClient(FakeScm.RunningHere(), server.Options()).CallAsync(Operation.ApplyLockPolicies, new ApplyLockPoliciesArgs(["lock", "taskmgr"]));
        var validated = new RequestValidator(_ => true).Validate(server.Request(0));
        Assert.True(validated.Ok, validated.Detail);
        Assert.Equal(["lock", "taskmgr"], validated.Request!.GetArgs<ApplyLockPoliciesArgs>().Groups);
    }

    [Fact]
    public async Task A_setup_is_streamed_after_stream_ready_and_the_verdict_comes_after()
    {
        var setup = new byte[100_000];
        new Random(7).NextBytes(setup);
        byte[]? received = null;
        await using var server = new FakePipeServer(async (request, pipe) =>
        {
            if (FakePipeServer.OpOf(request) != nameof(Operation.InstallCouchHudUpdate)) return FakePipeServer.Fail(ErrorCodes.BadArgument);
            var header = new RequestValidator(_ => true).Validate(request).Request!.GetArgs<SetupStreamArgs>();
            await FrameCodec.WriteAsync(pipe, ProtocolJson.Serialize(new Response(Protocol.Version, true, ErrorCodes.StreamReady)));
            using var sink = new MemoryStream();
            await StreamCodec.ReadAsync(pipe, sink, header.Size);
            received = sink.ToArray();
            return FakePipeServer.Ok(new JobStartedData(new string('a', 32)));
        });
        var client = new ServiceClient(FakeScm.RunningHere(), server.Options());
        var header = new SetupStreamArgs("CouchHud-Setup-0.4.2.exe", setup.Length, Convert.ToBase64String(Encoding.ASCII.GetBytes("sums")), Convert.ToBase64String(new byte[64]));
        var reply = await client.StreamAsync(Operation.InstallCouchHudUpdate, header, new MemoryStream(setup));
        Assert.Equal(CallOutcome.Ok, reply.Outcome);
        Assert.Equal(setup, received);
        Assert.Equal(new string('a', 32), reply.DataAs(ProtocolJsonContext.Default.JobStartedData)!.JobId);
    }

    [Fact]
    public async Task No_bytes_go_without_stream_ready()
    {
        await using var server = new FakePipeServer(_ => FakePipeServer.Ok());
        var client = new ServiceClient(FakeScm.RunningHere(), server.Options());
        var header = new SetupStreamArgs("CouchHud-Setup-0.4.2.exe", 10, Convert.ToBase64String([1]), Convert.ToBase64String([1]));
        var reply = await client.StreamAsync(Operation.InstallCouchHudUpdate, header, new MemoryStream(new byte[10]));
        Assert.Equal(CallOutcome.Inconsistent, reply.Outcome);
    }

    [Fact]
    public async Task A_refused_stream_sends_no_bytes()
    {
        await using var server = new FakePipeServer(_ => FakePipeServer.Fail(ErrorCodes.Denied));
        var client = new ServiceClient(FakeScm.RunningHere(), server.Options());
        var source = new MemoryStream(new byte[10]);
        var header = new SetupStreamArgs("CouchHud-Setup-0.4.2.exe", 10, Convert.ToBase64String([1]), Convert.ToBase64String([1]));
        var reply = await client.StreamAsync(Operation.InstallCouchHudUpdate, header, source);
        Assert.Equal(CallOutcome.Refused, reply.Outcome);
        Assert.Equal(0, source.Position);
    }

    [Fact]
    public void The_client_asks_for_no_right_to_create_pipe_instances()
    {
        // AppendData is FILE_CREATE_PIPE_INSTANCE on a pipe; ReadWrite/InOut contain it.
        Assert.Equal((System.IO.Pipes.PipeAccessRights)0, ServiceClient.ClientAccess & System.IO.Pipes.PipeAccessRights.CreateNewInstance);
        Assert.Equal((System.IO.Pipes.PipeAccessRights)0, ServiceClient.ClientAccess & System.IO.Pipes.PipeAccessRights.ChangePermissions);
        Assert.Equal((System.IO.Pipes.PipeAccessRights)0, ServiceClient.ClientAccess & System.IO.Pipes.PipeAccessRights.TakeOwnership);
    }

    /// <summary>Answers the queued infos one after the other (the last one stays).</summary>
    private sealed class QueueScm(params ScmInfo[] infos) : IServiceControl
    {
        private int _next;

        public ScmInfo Query() => infos[Math.Min(_next++, infos.Length - 1)];

        public bool TryStart() => false;
    }
}

/// <summary>The router (<see cref="ServiceOps"/>) and the probe against the fake server.</summary>
public class RouterTests
{
    private static readonly Version App = new(0, 4, 1);

    private static ServiceOps Ops(FakePipeServer server, IServiceControl? scm = null, Func<DateTimeOffset>? now = null)
    {
        scm ??= FakeScm.RunningHere();
        var client = new ServiceClient(scm, server.Options());
        return new ServiceOps(client, new ServiceProbe(scm, client, App, (_, _) => Task.CompletedTask), now, (_, _) => Task.CompletedTask);
    }

    [Fact]
    public async Task Without_a_service_by_design_nothing_is_connected()
    {
        await using var server = new FakePipeServer(_ => FakePipeServer.Ok());
        var ops = new ServiceOps(new ServiceClient(FakeScm.RunningHere(), server.Options()), probe: null);
        var result = await ops.ApplyLockPoliciesAsync(["lock"]);
        Assert.Equal(FailureKind.NotExpected, result.Failure);
        Assert.Equal(ServiceState.NotExpected, (await ops.StatusAsync(fresh: true)).State);
        Assert.Equal(0, server.Connections);
    }

    [Fact]
    public async Task Busy_is_tried_once_more()
    {
        var calls = 0;
        await using var server = new FakePipeServer(_ => Interlocked.Increment(ref calls) == 1 ? FakePipeServer.Fail(ErrorCodes.Busy) : FakePipeServer.Ok(new LockPoliciesData(["lock"])));
        var result = await Ops(server).ApplyLockPoliciesAsync(["lock"]);
        Assert.True(result.Ok, result.ToString());
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Squatting_turns_the_state_to_alarm_at_once()
    {
        await using var server = new FakePipeServer(_ => FakePipeServer.Ok());
        var scm = new FakeScm(new ScmInfo(ScmState.Running, Environment.ProcessId + 7));
        var ops = Ops(server, scm);
        ServiceStatus? alarm = null;
        ops.Alarm += s => alarm = s;
        var result = await ops.ReleaseLockPoliciesAsync(["lock"]);
        Assert.Equal(FailureKind.Squatted, result.Failure);
        Assert.Equal(ServiceState.Squatted, ops.LastStatus.State);
        Assert.Equal(ServiceState.Squatted, alarm?.State);
        Assert.False(FailClosed.MayReleasePolicies(ops.LastStatus.State));
        Assert.Equal(0, server.RequestCount);
    }

    [Fact]
    public async Task The_probe_says_running_and_caches()
    {
        await using var server = new FakePipeServer(_ => FakePipeServer.Hello("0.4.1"));
        var now = DateTimeOffset.UnixEpoch;
        var ops = Ops(server, now: () => now);
        Assert.Equal(ServiceState.Running, (await ops.StatusAsync()).State);
        Assert.Equal(ServiceState.Running, (await ops.StatusAsync()).State);
        Assert.Equal(1, server.RequestCount);
        now = now.AddMinutes(1);
        await ops.StatusAsync();
        Assert.Equal(2, server.RequestCount);
    }

    [Fact]
    public async Task A_stopped_service_is_started_by_the_probe()
    {
        await using var server = new FakePipeServer(_ => FakePipeServer.Hello("0.4.1"));
        var scm = new FakeScm(new ScmInfo(ScmState.Stopped, 0)) { AfterStart = new ScmInfo(ScmState.Running, Environment.ProcessId) };
        Assert.Equal(ServiceState.Running, (await Ops(server, scm).StatusAsync()).State);
        Assert.Equal(1, scm.Starts);
    }

    [Fact]
    public async Task A_disabled_or_squatted_service_is_not_started()
    {
        await using var server = new FakePipeServer(_ => FakePipeServer.Hello("0.4.1"));
        var disabled = new FakeScm(new ScmInfo(ScmState.Stopped, 0, Disabled: true)) { AfterStart = new ScmInfo(ScmState.Running, Environment.ProcessId) };
        Assert.Equal(ServiceState.Disabled, (await Ops(server, disabled).StatusAsync()).State);
        var squatted = new FakeScm(new ScmInfo(ScmState.Stopped, 0, Squatting.ServiceSpecificError, Squatting.ExitCode)) { AfterStart = new ScmInfo(ScmState.Running, Environment.ProcessId) };
        Assert.Equal(ServiceState.Squatted, (await Ops(server, squatted).StatusAsync()).State);
        Assert.Equal(0, disabled.Starts + squatted.Starts);
    }

    [Fact]
    public async Task An_old_service_is_reported_as_old()
    {
        await using var server = new FakePipeServer(_ => FakePipeServer.Hello("0.4.0"));
        Assert.Equal(ServiceState.OldVersion, (await Ops(server).StatusAsync()).State);
    }

    [Fact]
    public async Task A_job_is_followed_until_done()
    {
        var jobId = new string('b', 32);
        var polls = 0;
        await using var server = new FakePipeServer(request => FakePipeServer.OpOf(request) switch
        {
            nameof(Operation.InstallWindowsUpdates) => FakePipeServer.Ok(new JobStartedData(jobId)),
            nameof(Operation.GetJob) => ++polls < 3
                ? FakePipeServer.Ok(new JobData(jobId, "running", polls * 30, null))
                : FakePipeServer.Ok(new JobData(jobId, "done", 100, null, Installed: 2, RebootRequired: true)),
            _ => FakePipeServer.Fail(ErrorCodes.BadArgument)
        });
        var seen = new List<int>();
        var result = await Ops(server).InstallWindowsUpdatesAsync(new SyncProgress<JobData>(j => seen.Add(j.Percent)));
        Assert.True(result.Ok);
        Assert.Equal(2, result.Job!.Installed);
        Assert.True(result.Job.RebootRequired);
        Assert.Equal([30, 60, 100], seen);
    }

    [Fact]
    public async Task Updates_are_searched_by_the_service_and_installed_by_the_ids_it_found()
    {
        var searchId = new string('e', 32);
        var installId = new string('f', 32);
        const string update = "0b2a3f4e-1111-4c3d-9e8a-0000000000a1";
        string? installRequest = null;
        await using var server = new FakePipeServer(request =>
        {
            var op = FakePipeServer.OpOf(request);
            if (op == nameof(Operation.InstallWindowsUpdates)) installRequest = System.Text.Encoding.UTF8.GetString(request);
            return op switch
            {
                nameof(Operation.SearchWindowsUpdates) => FakePipeServer.Ok(new JobStartedData(searchId)),
                nameof(Operation.InstallWindowsUpdates) => FakePipeServer.Ok(new JobStartedData(installId)),
                nameof(Operation.GetJob) when System.Text.Encoding.UTF8.GetString(request).Contains(searchId) =>
                    FakePipeServer.Ok(new JobData(searchId, "done", 100, "searched", Updates: [new UpdateEntry(update, "Security update", 1000)])),
                nameof(Operation.GetJob) => FakePipeServer.Ok(new JobData(installId, "done", 100, "installed", Installed: 1, RebootRequired: true)),
                _ => FakePipeServer.Fail(ErrorCodes.BadArgument)
            };
        });
        var ops = Ops(server);
        var search = await ops.SearchWindowsUpdatesAsync(null);
        Assert.True(search.Ok);
        var ids = search.Job!.Updates!.Select(u => u.Id).ToList();
        Assert.Equal([update], ids);

        var install = await ops.InstallWindowsUpdatesAsync(ids, null);
        Assert.True(install.Ok);
        Assert.Equal(1, install.Job!.Installed);
        Assert.Contains(update, installRequest);

        // No ids at all is no request (the service would read "all").
        var before = server.RequestCount;
        Assert.True((await ops.InstallWindowsUpdatesAsync(Array.Empty<string>(), null)).Ok);
        Assert.Equal(before, server.RequestCount);
    }

    [Fact]
    public async Task A_job_of_another_id_is_inconsistent()
    {
        await using var server = new FakePipeServer(request => FakePipeServer.OpOf(request) switch
        {
            nameof(Operation.InstallWindowsUpdates) => FakePipeServer.Ok(new JobStartedData(new string('c', 32))),
            _ => FakePipeServer.Ok(new JobData(new string('d', 32), "done", 100, null))
        });
        var result = await Ops(server).InstallWindowsUpdatesAsync(null);
        Assert.False(result.Ok);
        Assert.Equal(FailureKind.Inconsistent, result.Result.Failure);
    }

    [Fact]
    public async Task A_grant_goes_only_with_the_operations_that_can_need_one()
    {
        var grant = Convert.ToBase64String(new byte[32]);
        await using var server = new FakePipeServer(_ => FakePipeServer.Ok());
        var ops = Ops(server);
        ops.Grants = (_, _) => Task.FromResult<string?>(grant);
        await ops.ApplyLockPoliciesAsync(["lock"]);
        await ops.ReleaseLockPoliciesAsync(["lock"]);
        var validator = new RequestValidator(_ => true);
        Assert.Null(validator.Validate(server.Request(0)).Request!.Grant);
        Assert.Equal(grant, validator.Validate(server.Request(1)).Request!.Grant);
    }

    /// <summary>IProgress that reports on the calling thread (Progress&lt;T&gt; posts to the pool).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
