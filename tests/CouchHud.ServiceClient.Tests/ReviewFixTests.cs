using CouchHud.Core.Guard;
using CouchHud.Service.Contracts;

namespace CouchHud.ServiceClient.Tests;

/// <summary>The fixes from the security review of P7 (M1–M3, L1–L7), one test or more each.</summary>
public class ReviewFixTests
{
    private static readonly Version App = new(0, 4, 1);
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    // ---- M1: a healthy service that does not set the policies is an incident --------------------

    [Fact]
    public void M1_a_running_service_whose_policies_fail_warns_then_locks_windows()
    {
        var watch = new LockWatch(TimeSpan.FromSeconds(30));
        Assert.Equal(LockWatchAction.Warn, watch.Observe(T0, true, ServiceState.Running, policiesFailing: true));
        Assert.Equal(LockWatchAction.None, watch.Observe(T0.AddSeconds(20), true, ServiceState.Running, policiesFailing: true));
        Assert.Equal(LockWatchAction.LockWindows, watch.Observe(T0.AddSeconds(30), true, ServiceState.Running, policiesFailing: true));
        // Policies set again: the incident ends.
        Assert.Equal(LockWatchAction.None, watch.Observe(T0.AddSeconds(40), true, ServiceState.Running, policiesFailing: false));
        Assert.False(watch.InIncident);
    }

    [Fact]
    public void M1_busy_counts_as_running_for_one_look_only()
    {
        var busy = ReplyParser.Parse(FakePipeServer.Fail(ErrorCodes.Busy));
        Assert.Equal(ServiceState.Running, ServiceProbe.FromHello(busy, App, out _, retryAlreadyCounted: false));
        Assert.Equal(ServiceState.NotResponding, ServiceProbe.FromHello(busy, App, out _, retryAlreadyCounted: true));
        var limited = ReplyParser.Parse(FakePipeServer.Fail(ErrorCodes.RateLimited));
        Assert.Equal(ServiceState.NotResponding, ServiceProbe.FromHello(limited, App, out _, retryAlreadyCounted: true));
    }

    [Fact]
    public async Task M1_a_service_that_keeps_saying_busy_is_not_responding_from_the_second_probe()
    {
        await using var server = new FakePipeServer(_ => FakePipeServer.Fail(ErrorCodes.Busy));
        var scm = FakeScm.RunningHere();
        var client = new ServiceClient(scm, server.Options());
        var probe = new ServiceProbe(scm, client, App, (_, _) => Task.CompletedTask);
        Assert.Equal(ServiceState.Running, (await probe.ProbeAsync(false, TimeSpan.Zero, () => T0)).State);
        Assert.Equal(ServiceState.NotResponding, (await probe.ProbeAsync(false, TimeSpan.Zero, () => T0)).State);
        Assert.Equal(ServiceState.NotResponding, (await probe.ProbeAsync(false, TimeSpan.Zero, () => T0)).State);
    }

    [Fact]
    public async Task M1_after_a_proper_answer_busy_gets_its_one_look_again()
    {
        var busy = true;
        await using var server = new FakePipeServer(_ => busy ? FakePipeServer.Fail(ErrorCodes.Busy) : FakePipeServer.Hello("0.4.1"));
        var scm = FakeScm.RunningHere();
        var probe = new ServiceProbe(scm, new ServiceClient(scm, server.Options()), App, (_, _) => Task.CompletedTask);
        Assert.Equal(ServiceState.Running, (await probe.ProbeAsync(false, TimeSpan.Zero, () => T0)).State);
        busy = false;
        Assert.Equal(ServiceState.Running, (await probe.ProbeAsync(false, TimeSpan.Zero, () => T0)).State);
        busy = true;
        Assert.Equal(ServiceState.Running, (await probe.ProbeAsync(false, TimeSpan.Zero, () => T0)).State);
    }

    // ---- M2: the repair does not depend on the elevated environment -------------------------------

    [Fact]
    public void M2_elevated_launches_run_in_system32_by_absolute_path()
    {
        var repair = EmergencyRepair.RepairService(@"C:\Windows\system32", @"C:\Program Files\CouchHud");
        Assert.Equal(@"C:\Windows\system32", repair.WorkingDirectory);
        Assert.True(Path.IsPathFullyQualified(repair.FileName));
        Assert.DoesNotContain("%", repair.Arguments);
        var cleanup = EmergencyRepair.StuckPolicyCleanup(@"C:\Windows\system32", "S-1-5-21-1-2-3-1001", [new(LockHardening.ExplorerKey, "NoRun")])!;
        Assert.Equal(@"C:\Windows\system32", cleanup.WorkingDirectory);
        Assert.Throws<ArgumentException>(() => EmergencyRepair.RepairService(@"%SystemRoot%\system32", @"C:\Program Files\CouchHud"));
    }

    // ---- L1: the SCM is asked again after the PID match -------------------------------------------

    [Fact]
    public void L1_the_service_must_still_run_as_the_same_process()
    {
        Assert.True(ServerCheck.StillTheService(new ScmInfo(ScmState.Running, 42), 42));
        Assert.False(ServerCheck.StillTheService(new ScmInfo(ScmState.Running, 43), 42));
        Assert.False(ServerCheck.StillTheService(new ScmInfo(ScmState.StopPending, 42), 42));
        Assert.False(ServerCheck.StillTheService(new ScmInfo(ScmState.Running, 42), null));
    }

    [Fact]
    public async Task L1_a_service_that_stopped_right_after_the_match_gets_nothing()
    {
        await using var server = new FakePipeServer(_ => FakePipeServer.Ok());
        var scm = new SequenceScm(new ScmInfo(ScmState.Running, Environment.ProcessId), new ScmInfo(ScmState.StopPending, 0));
        var reply = await new ServiceClient(scm, server.Options()).CallAsync(Operation.Ping);
        Assert.Equal(CallOutcome.Squatted, reply.Outcome);
        // Both SCM answers were used: the client did connect and match the process, and only then saw the service stopping.
        Assert.Equal(2, scm.Queries);
        // No fixed wait: once the server is done with that connection, every request on it would have been recorded.
        await server.WaitSettledAsync(1);
        Assert.Equal(0, server.RequestCount);
    }

    // ---- L2: a registered service is always expected ----------------------------------------------

    [Fact]
    public void L2_a_registered_service_counts_as_expected_whatever_the_folder()
    {
        Assert.True(FailClosed.ServiceExpected(protectedInstall: false, new ScmInfo(ScmState.Stopped, 0)));
        Assert.True(FailClosed.ServiceExpected(protectedInstall: false, ScmInfo.Unknown));
        Assert.True(FailClosed.ServiceExpected(protectedInstall: true, ScmInfo.NotInstalled));
        Assert.False(FailClosed.ServiceExpected(protectedInstall: false, ScmInfo.NotInstalled));
    }

    // ---- L3: the repair only when needed, once a minute ------------------------------------------

    [Fact]
    public void L3_the_repair_needs_a_reason_and_waits_between_tries()
    {
        var gate = new RepairGate(TimeSpan.FromSeconds(60));
        Assert.Equal(RepairVerdict.NotNeeded, gate.TryBegin(T0, ServiceState.Running));
        Assert.Equal(RepairVerdict.NotNeeded, gate.TryBegin(T0, ServiceState.NotExpected));
        Assert.Equal(RepairVerdict.NotNeeded, gate.TryBegin(T0, ServiceState.Unknown));
        Assert.Equal(RepairVerdict.Allowed, gate.TryBegin(T0, ServiceState.Stopped));
        Assert.Equal(RepairVerdict.CoolingDown, gate.TryBegin(T0.AddSeconds(59), ServiceState.Squatted));
        Assert.Equal(RepairVerdict.Allowed, gate.TryBegin(T0.AddSeconds(60), ServiceState.Squatted));
    }

    // ---- L4: Windows is locked again while the incident lasts ------------------------------------

    [Fact]
    public void L4_windows_is_locked_again_every_ten_minutes_of_an_incident()
    {
        var watch = new LockWatch(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10));
        Assert.Equal(LockWatchAction.LockWindows, watch.Observe(T0, true, ServiceState.Squatted));
        Assert.Equal(LockWatchAction.None, watch.Observe(T0.AddMinutes(9), true, ServiceState.Squatted));
        Assert.Equal(LockWatchAction.LockWindows, watch.Observe(T0.AddMinutes(10), true, ServiceState.Squatted));
        Assert.Equal(LockWatchAction.None, watch.Observe(T0.AddMinutes(15), true, ServiceState.NotResponding));
        Assert.Equal(LockWatchAction.LockWindows, watch.Observe(T0.AddMinutes(20), true, ServiceState.NotResponding));
    }

    // ---- L5: --dev relaxes nothing where a service is expected ------------------------------------

    [Fact]
    public void L5_dev_relaxes_the_lock_only_without_a_service()
    {
        Assert.False(FailClosed.DevMayRelax(dev: true, serviceExpected: true));
        Assert.True(FailClosed.DevMayRelax(dev: true, serviceExpected: false));
        Assert.False(FailClosed.DevMayRelax(dev: false, serviceExpected: false));
    }

    // ---- L6: the emergency cleanup only looks at groups with evidence ------------------------------

    [Fact]
    public void L6_only_groups_with_evidence_are_cleaned()
    {
        var registry = new FakeRegistry();
        registry.WriteDword(LockHardening.ExplorerKey, "NoRun", 1);
        registry.WriteDword(LockHardening.SystemKey, "DisableTaskMgr", 1);
        Assert.Empty(EmergencyRepair.StuckValues(registry, []));
        var onlyTaskManager = EmergencyRepair.StuckValues(registry, ["taskmgr"]);
        Assert.Equal([new EmergencyRepair.StuckValue(LockHardening.SystemKey, "DisableTaskMgr")], onlyTaskManager);
        Assert.Equal(2, EmergencyRepair.StuckValues(registry, ["LOCK", "taskmgr"]).Count);
    }

    // ---- L7: a newer service ends the expected restart ----------------------------------------------

    [Theory]
    [InlineData("0.4.2", true)]
    [InlineData("0.5.0", true)]
    [InlineData("0.4.1", false)]
    [InlineData("0.4.0", false)]
    [InlineData(null, false)]
    [InlineData("x", false)]
    public void L7_a_newer_service_is_recognised(string? service, bool newer) => Assert.Equal(newer, ServiceProbe.IsNewer(service, App));

    /// <summary>Answers the infos in turn; the last one stays.</summary>
    private sealed class SequenceScm(params ScmInfo[] infos) : IServiceControl
    {
        private int _next;

        public int Queries => _next;

        public ScmInfo Query() => infos[Math.Min(_next++, infos.Length - 1)];

        public bool TryStart() => false;
    }
}
