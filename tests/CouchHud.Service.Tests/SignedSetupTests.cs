using System.Buffers.Binary;
using System.Text;
using CouchHud.Core.Plugins;
using CouchHud.Service.Authorization;
using CouchHud.Service.Contracts;
using CouchHud.Service.Setup;

namespace CouchHud.Service.Tests;

/// <summary>The signed-setup pipeline end to end over the connection handler, with throwaway keys and a fake starter (P6).</summary>
public class SignedSetupTests
{
    private const string Next = "CouchHud-Setup-0.5.0.exe";

    [Fact]
    public async Task A_signed_newer_CouchHud_setup_is_staged_under_the_signed_name_held_and_started_with_the_fixed_arguments()
    {
        using var ops = new OperationsB();
        var setup = OperationsB.FakeSetup();
        var answers = await ops.SendSetupAsync(Operation.InstallCouchHudUpdate, ops.CouchHudArgs(Next, setup), setup, Callers.User() with { PipeSessionId = 3, TokenSessionId = 3 });

        Assert.Equal(2, answers.Count);
        Assert.Equal(ErrorCodes.StreamReady, OperationsB.Code(answers[0]));
        var job = await ops.WaitForJobAsync(OperationsB.JobId(answers[1]));
        Assert.Equal("done", job.State);

        var start = Assert.Single(ops.Starter.Started);
        Assert.Equal(Next, Path.GetFileName(start.Path));
        Assert.StartsWith(ops.Staging.StagingRoot + Path.DirectorySeparatorChar, start.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.GetDirectoryName(start.Path), start.WorkingDirectory);
        var logFile = Path.Combine(ops.DataRoot, "logs", "setup-0.5.0.log");
        // A code is assumed (no lock state in the service yet) and the caller is no administrator: the session is not kept.
        Assert.Equal($"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /MERGETASKS=!autostart /SERVICEUPDATE=1 /LOG=\"{logFile}\"", start.Arguments);
        Assert.True(start.TempExisted, "the setup's own empty temp folder must exist at the start");
        Assert.Equal(Path.Combine(Path.GetDirectoryName(start.Path)!, "tmp"), start.Environment["TEMP"]);
        Assert.Equal(start.Environment["TEMP"], start.Environment["TMP"]);
        Assert.True(ops.Security.WasChecked(start.Environment["TEMP"]), "the temp folder must be checked after it was made");
        Assert.True(ops.Security.WasChecked(Path.GetDirectoryName(start.Path)!), "the setup's folder must be checked after it was made");
        Assert.Equal(Path.Combine(ops.ServiceDirectory, "data", "staging"), Path.GetDirectoryName(Path.GetDirectoryName(start.Path)));
        Assert.True(Directory.Exists(Path.GetDirectoryName(logFile)));
        Assert.Equal(1, start.JournaledJobs);   // persisted before the start: the setup stops this service
        Assert.Empty(Directory.GetFiles(ops.JournalFolder));   // this instance saw the end: record removed
        Assert.Equal(OperationsB.Sha(setup), start.Sha256);
        Assert.True(start.WriteRefused, "the staged setup must not be writable while it is started");
        Assert.True(start.DeleteRefused, "the staged setup must not be deletable while it is started");
        Assert.Contains(ops.Log.Lines, l => l.Contains($"sha256={OperationsB.Sha(setup)}"));
        Assert.Empty(ops.StagedFolders());   // removed after the setup ended
        Assert.False(ops.Parts.Setups.Busy);
    }

    private static readonly string Grant = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    /// <summary>M2: a locked couch must not keep its desktop while the update ends the lock overlay and the guard.</summary>
    [Theory]
    [InlineData(true, false, false, false)]   // code (may be locked), standard user, no grant: disconnected
    [InlineData(true, true, false, true)]     // elevated administrator
    [InlineData(true, false, true, true)]     // valid grant
    [InlineData(false, false, false, true)]   // no code: the couch cannot be locked
    public async Task The_caller_s_session_is_kept_only_when_its_couch_cannot_be_locked_or_an_administrator_or_grant_says_so(
        bool codeEnrolled, bool admin, bool withGrant, bool kept)
    {
        var grants = new FakeGrants(Grant);
        using var ops = new OperationsB(new KeepSessionPolicy(new FixedCodeState(codeEnrolled), grants));
        var setup = OperationsB.FakeSetup();
        var caller = (admin ? Callers.ElevatedAdmin() : Callers.User()) with { PipeSessionId = 4, TokenSessionId = 4 };
        var stream = new MemoryStream();
        await StreamCodec.WriteAsync(stream, new MemoryStream(setup), setup.Length);
        var header = FrameCodec.Encode(ProtocolJson.SerializeRequest(Operation.InstallCouchHudUpdate, ops.CouchHudArgs(Next, setup), withGrant ? Grant : null));
        var answers = await ops.Harness.ExchangeAsync(caller, header, stream.ToArray());

        Assert.Equal("done", (await ops.WaitForJobAsync(OperationsB.JobId(answers[1]), caller)).State);
        var start = Assert.Single(ops.Starter.Started);
        Assert.Equal(kept, start.Arguments.Contains("/KEEPSESSION=4"));
        if (!kept) Assert.DoesNotContain("/KEEPSESSION", start.Arguments);
        Assert.Equal(withGrant && !admin && codeEnrolled ? 1 : 0, grants.Consumed);   // a grant is only used up when it is needed
    }

    [Fact]
    public async Task A_grant_that_is_not_valid_does_not_keep_the_session()
    {
        using var ops = new OperationsB(new KeepSessionPolicy(new FixedCodeState(true), new FakeGrants(Grant)));
        var setup = OperationsB.FakeSetup();
        var stream = new MemoryStream();
        await StreamCodec.WriteAsync(stream, new MemoryStream(setup), setup.Length);
        var other = Convert.ToBase64String(new byte[32]);
        var answers = await ops.Harness.ExchangeAsync(Callers.User(), FrameCodec.Encode(ProtocolJson.SerializeRequest(Operation.InstallCouchHudUpdate, ops.CouchHudArgs(Next, setup), other)), stream.ToArray());
        await ops.WaitForJobAsync(OperationsB.JobId(answers[1]));
        Assert.DoesNotContain("/KEEPSESSION", Assert.Single(ops.Starter.Started).Arguments);
    }

    [Fact]
    public void The_setup_environment_is_built_not_inherited()
    {
        var env = SetupEnvironment.Build(@"C:\ProgramData\CouchHud\Service\staging\x\tmp", @"C:\Windows", @"C:\ProgramData", @"C:\Program Files", @"C:\Program Files (x86)", is64Bit: true);
        Assert.Equal(@"C:\ProgramData\CouchHud\Service\staging\x\tmp", env["TEMP"]);
        Assert.Equal(env["TEMP"], env["TMP"]);
        Assert.Equal(@"C:\Windows", env["SystemRoot"]);
        Assert.Equal(@"C:\Windows", env["windir"]);
        Assert.Equal("C:", env["SystemDrive"]);
        Assert.Equal(@"C:\Windows\System32\cmd.exe", env["ComSpec"]);
        Assert.All(env["PATH"].Split(';'), p => Assert.StartsWith(@"C:\Windows", p));
        Assert.DoesNotContain(env.Keys, SetupEnvironment.IsRuntimeVariable);
        Assert.DoesNotContain("USERPROFILE", env.Keys);

        // Whatever the service's own environment holds, none of it reaches the setup.
        Environment.SetEnvironmentVariable("COR_PROFILER_P6_TEST", "{x}");
        Environment.SetEnvironmentVariable("DOTNET_P6_TEST", "1");
        try
        {
            var real = SetupEnvironment.Build(Path.GetTempPath());
            Assert.DoesNotContain(real.Keys, k => k.Contains("P6_TEST"));
            Assert.DoesNotContain(real.Keys, SetupEnvironment.IsRuntimeVariable);
        }
        finally
        {
            Environment.SetEnvironmentVariable("COR_PROFILER_P6_TEST", null);
            Environment.SetEnvironmentVariable("DOTNET_P6_TEST", null);
        }
        Assert.True(SetupEnvironment.IsRuntimeVariable("cor_enable_profiling"));
        Assert.True(SetupEnvironment.IsRuntimeVariable("CORECLR_PROFILER_PATH"));
        Assert.True(SetupEnvironment.IsRuntimeVariable("COMPlus_ETWEnabled"));
        Assert.Throws<ArgumentException>(() => SetupEnvironment.Build("relative", @"C:\Windows", @"C:\ProgramData", @"C:\Program Files", "", false));
    }

    [Fact]
    public async Task Exit_code_20_reports_that_the_service_did_not_come_up()
    {
        using var ops = new OperationsB();
        ops.Starter.ExitCode = SignedSetupPipeline.ExitServiceNotStarted;
        var setup = OperationsB.FakeSetup();
        var answers = await ops.SendSetupAsync(Operation.InstallCouchHudUpdate, ops.CouchHudArgs(Next, setup), setup);
        var job = await ops.WaitForJobAsync(OperationsB.JobId(answers[1]));
        Assert.Equal("failed", job.State);
        Assert.Equal("serviceNotStarted", job.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(100000)]
    public void A_session_id_out_of_range_gives_no_command_line(int session)
    {
        Assert.Null(SignedSetupPipeline.ArgumentsFor(SetupKind.CouchHud, session));
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /MERGETASKS=!autostart /SERVICEUPDATE=1 /KEEPSESSION=99999",
            SignedSetupPipeline.ArgumentsFor(SetupKind.CouchHud, 99999));
        Assert.Equal("/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /MERGETASKS=!autostart /SERVICEUPDATE=1",
            SignedSetupPipeline.ArgumentsFor(SetupKind.CouchHud, null));
        Assert.Null(SignedSetupPipeline.ArgumentsFor(SetupKind.CouchHud, 1, "C:\\x\" /DIR=\"C:\\evil"));
        Assert.Null(SignedSetupPipeline.ArgumentsFor(SetupKind.CouchHud, 1, "relative.log"));
    }

    [Theory]
    [InlineData("0.5.0", "done", "installed")]
    [InlineData("0.4.1", "failed", "notInstalled")]
    public async Task The_next_instance_of_the_service_answers_for_a_setup_that_stopped_the_previous_one(string installedAfter, string state, string message)
    {
        using var ops = new OperationsB();
        using var gate = new ManualResetEventSlim();   // the setup "runs" and this instance never sees its end
        ops.Starter.Gate = gate;
        var setup = OperationsB.FakeSetup();
        var id = OperationsB.JobId((await ops.SendSetupAsync(Operation.InstallCouchHudUpdate, ops.CouchHudArgs(Next, setup), setup))[1]);
        SpinWait.SpinUntil(() => ops.Starter.Started.Count == 1, 5000);
        Assert.Single(Directory.GetFiles(ops.JournalFolder));

        // A new instance after the setup: its own job store, the same folders.
        ops.Installed.CouchHudVersion = SemVer.TryParse(installedAfter, out var v) ? v : null;
        var jobs = new Jobs.JobStore(new FakeClock());
        Assert.Equal(1, ops.NewPipeline(jobs).RecoverJobs());
        Assert.Empty(Directory.GetFiles(ops.JournalFolder));
        var job = jobs.FindFor(Callers.User(), id)!.Snapshot();
        Assert.Equal(state, job.State);
        Assert.Equal(message, job.Message);
        Assert.Null(jobs.FindFor(Callers.User(Callers.OtherSid), id));   // still the owner's only
        gate.Set();
    }

    [Fact]
    public void Journal_records_that_are_old_or_not_ours_are_dropped()
    {
        using var ops = new OperationsB();
        Assert.Null(ops.Staging.Prepare());
        var journal = ops.Journal;
        journal.Write(new Jobs.JournaledJob(new string('a', 32), Callers.UserSid, SignedSetupPipeline.CouchHudJob, "0.5.0", ops.WallMs - (long)TimeSpan.FromDays(2).TotalMilliseconds, null));
        journal.Write(new Jobs.JournaledJob(new string('b', 32), "S-1-5-18", SignedSetupPipeline.CouchHudJob, "0.5.0", ops.WallMs, null));
        File.WriteAllText(Path.Combine(ops.JournalFolder, new string('c', 32) + ".json"), "{\"id\":\"" + new string('c', 32) + "\",\"extra\":1}");
        journal.Write(new Jobs.JournaledJob(new string('d', 32), Callers.UserSid, SignedSetupPipeline.CouchHudJob, "0.5.0", ops.WallMs, null));

        var jobs = new Jobs.JobStore(new FakeClock());
        Assert.Equal(1, ops.NewPipeline(jobs).RecoverJobs());
        Assert.NotNull(jobs.Find(new string('d', 32)));
        Assert.Empty(Directory.GetFiles(ops.JournalFolder));
    }

    [Fact]
    public async Task A_tampered_setup_is_refused_after_the_stream_and_never_started()
    {
        using var ops = new OperationsB();
        var setup = OperationsB.FakeSetup();
        var args = ops.CouchHudArgs(Next, setup);
        var tampered = (byte[])setup.Clone();
        tampered[^1] ^= 0xFF;
        var answers = await ops.SendSetupAsync(Operation.InstallCouchHudUpdate, args, tampered);

        Assert.Equal(ErrorCodes.StreamReady, OperationsB.Code(answers[0]));
        Assert.Equal(ErrorCodes.VerificationFailed, OperationsB.Code(answers[1]));
        Assert.Empty(ops.Starter.Started);
        Assert.Empty(ops.StagedFolders());
        Assert.False(ops.Parts.Setups.Busy);
    }

    [Fact]
    public async Task A_signed_file_that_is_not_a_Windows_program_is_refused()
    {
        using var ops = new OperationsB();
        var notPe = Encoding.ASCII.GetBytes(new string('x', 50_000));
        var answers = await ops.SendSetupAsync(Operation.InstallCouchHudUpdate, ops.CouchHudArgs(Next, notPe), notPe);
        Assert.Equal(ErrorCodes.VerificationFailed, OperationsB.Code(answers[1]));
        Assert.Empty(ops.Starter.Started);
    }

    [Fact]
    public async Task A_wrong_signature_is_refused_before_any_byte_is_taken()
    {
        using var ops = new OperationsB();
        var setup = OperationsB.FakeSetup();
        var other = OperationsB.CouchHudSums("CouchHud-Setup-0.6.0.exe", setup);
        var args = ops.CouchHudArgs(Next, setup, signature: ops.CouchHudKey.SignCouchHud(other));
        var answer = await ops.SendHeaderAsync(Operation.InstallCouchHudUpdate, args, setup);
        Assert.Equal(ErrorCodes.VerificationFailed, OperationsB.Code(answer));
        Assert.Empty(ops.StagedFolders());
    }

    [Fact]
    public async Task A_signature_by_another_key_is_refused()
    {
        using var ops = new OperationsB();
        using var stranger = new TestKey();
        var setup = OperationsB.FakeSetup();
        var sums = OperationsB.CouchHudSums(Next, setup);
        var answer = await ops.SendHeaderAsync(Operation.InstallCouchHudUpdate, ops.CouchHudArgs(Next, setup, signature: stranger.SignCouchHud(sums)), setup);
        Assert.Equal(ErrorCodes.VerificationFailed, OperationsB.Code(answer));
    }

    [Fact]
    public async Task A_signature_of_the_right_key_for_another_purpose_is_refused()
    {
        using var ops = new OperationsB();
        var setup = OperationsB.FakeSetup();
        var sums = OperationsB.CouchHudSums(Next, setup);
        // Signed as a catalog index (domain separation).
        foreach (var signature in new[] { ops.CouchHudKey.SignCouchHud(sums, CatalogSignature.CatalogContext) })
        {
            var answer = await ops.SendHeaderAsync(Operation.InstallCouchHudUpdate, ops.CouchHudArgs(Next, setup, signature: signature), setup);
            Assert.Equal(ErrorCodes.VerificationFailed, OperationsB.Code(answer));
        }
    }

    [Fact]
    public async Task The_version_comes_from_the_signed_file_not_from_the_client()
    {
        using var ops = new OperationsB();
        var setup = OperationsB.FakeSetup();
        // A validly signed old release, announced under a new name: the name is not the signed one, so nothing is accepted.
        var answer = await ops.SendHeaderAsync(Operation.InstallCouchHudUpdate, ops.CouchHudArgs("CouchHud-Setup-0.4.0.exe", setup, clientName: "CouchHud-Setup-9.9.9.exe"), setup);
        Assert.Equal(ErrorCodes.VerificationFailed, OperationsB.Code(answer));
        Assert.Empty(ops.Starter.Started);
    }

    [Theory]
    [InlineData("CouchHud-Setup-0.4.0.exe")]   // downgrade
    [InlineData("CouchHud-Setup-0.4.1.exe")]   // the installed release again (replay)
    public async Task A_setup_that_is_not_newer_than_the_installed_CouchHud_is_denied(string name)
    {
        using var ops = new OperationsB();
        ops.Installed.CouchHudVersion = new SemVer(0, 4, 1, "");
        var setup = OperationsB.FakeSetup();
        var answer = await ops.SendHeaderAsync(Operation.InstallCouchHudUpdate, ops.CouchHudArgs(name, setup), setup);
        Assert.Equal(ErrorCodes.Denied, OperationsB.Code(answer));
    }

    [Fact]
    public async Task Without_a_readable_installed_version_no_CouchHud_setup_is_accepted()
    {
        using var ops = new OperationsB();
        ops.Installed.CouchHudVersion = null;
        var setup = OperationsB.FakeSetup();
        var answer = await ops.SendHeaderAsync(Operation.InstallCouchHudUpdate, ops.CouchHudArgs(Next, setup), setup);
        Assert.Equal(ErrorCodes.Denied, OperationsB.Code(answer));
    }

    [Fact]
    public void A_checksum_file_that_names_two_different_setups_names_none()
    {
        using var key = new TestKey();
        var setup = OperationsB.FakeSetup(1000);
        var sums = Encoding.UTF8.GetBytes($"{OperationsB.Sha(setup)}  CouchHud-Setup-0.5.0.exe\n{OperationsB.Sha(setup)}  CouchHud-Setup-0.6.0.exe\n");
        var verifier = new SignedSetupVerifier(key.PublicPem);
        var verdict = verifier.Verify(SetupKind.CouchHud, new SetupStreamArgs("CouchHud-Setup-0.6.0.exe", setup.Length, Convert.ToBase64String(sums), key.SignCouchHud(sums)), new SemVer(0, 4, 1, ""));
        Assert.False(verdict.Ok);
        Assert.Equal(ErrorCodes.VerificationFailed, verdict.Code);
    }

    [Fact]
    public async Task More_bytes_than_announced_are_cut_off_and_nothing_is_started()
    {
        using var ops = new OperationsB();
        var setup = OperationsB.FakeSetup(50_000);
        var args = ops.CouchHudArgs(Next, setup);
        var tooMany = new MemoryStream();
        WriteChunk(tooMany, setup.Concat(new byte[100]).ToArray());
        WriteChunk(tooMany, []);
        var answers = await ops.Harness.ExchangeAsync(Callers.User(), FrameCodec.Encode(ProtocolJson.SerializeRequest(Operation.InstallCouchHudUpdate, args)), tooMany.ToArray());
        Assert.Equal(ErrorCodes.StreamTooLarge, OperationsB.Code(answers[1]));
        Assert.Empty(ops.Starter.Started);
        Assert.Empty(ops.StagedFolders());
        Assert.False(ops.Parts.Setups.Busy);
    }

    [Fact]
    public async Task Fewer_bytes_than_announced_are_refused()
    {
        using var ops = new OperationsB();
        var setup = OperationsB.FakeSetup(50_000);
        var args = ops.CouchHudArgs(Next, setup);
        var tooFew = new MemoryStream();
        WriteChunk(tooFew, setup[..^100]);
        WriteChunk(tooFew, []);
        var answers = await ops.Harness.ExchangeAsync(Callers.User(), FrameCodec.Encode(ProtocolJson.SerializeRequest(Operation.InstallCouchHudUpdate, args)), tooFew.ToArray());
        Assert.Equal(ErrorCodes.StreamShort, OperationsB.Code(answers[1]));
        Assert.Empty(ops.Starter.Started);
        Assert.Empty(ops.StagedFolders());
    }

    [Fact]
    public async Task A_client_that_goes_away_in_the_middle_frees_the_slot()
    {
        using var ops = new OperationsB();
        var setup = OperationsB.FakeSetup(50_000);
        var cut = new MemoryStream();
        WriteChunk(cut, setup[..20_000]);
        var answers = await ops.Harness.ExchangeAsync(Callers.User(), FrameCodec.Encode(ProtocolJson.SerializeRequest(Operation.InstallCouchHudUpdate, ops.CouchHudArgs(Next, setup))), cut.ToArray());
        Assert.Equal(ErrorCodes.Truncated, OperationsB.Code(answers[1]));
        Assert.False(ops.Parts.Setups.Busy);
        Assert.Empty(ops.StagedFolders());
    }

    private static void WriteChunk(Stream stream, byte[] chunk)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)chunk.Length);
        stream.Write(header);
        stream.Write(chunk);
    }

    [Fact]
    public async Task Only_one_setup_runs_at_a_time()
    {
        using var ops = new OperationsB();
        using var gate = new ManualResetEventSlim();
        ops.Starter.Gate = gate;
        var setup = OperationsB.FakeSetup();
        var first = await ops.SendSetupAsync(Operation.InstallCouchHudUpdate, ops.CouchHudArgs(Next, setup), setup);
        Assert.Equal(ErrorCodes.Ok, OperationsB.Code(first[1]));

        // The stream is taken into its own folder; the one setup slot is only needed for seal and start, and it is taken.
        var second = await ops.SendSetupAsync(Operation.InstallCouchHudUpdate, ops.CouchHudArgs(Next, setup), setup, Callers.User(Callers.OtherSid));
        Assert.Equal(ErrorCodes.StreamReady, OperationsB.Code(second[0]));
        Assert.Equal(ErrorCodes.Busy, OperationsB.Code(second[1]));
        Assert.Single(ops.StagedFolders());   // the second one's folder is gone again

        gate.Set();
        Assert.Equal("done", (await ops.WaitForJobAsync(OperationsB.JobId(first[1]))).State);
        var third = await ops.SendSetupAsync(Operation.InstallCouchHudUpdate, ops.CouchHudArgs(Next, setup), setup, Callers.User(Callers.OtherSid));
        Assert.Equal(ErrorCodes.Ok, OperationsB.Code(third[1]));
    }

    [Fact]
    public void At_most_two_streams_are_received_at_a_time()
    {
        var slots = new Jobs.CountedSlot(SignedSetupPipeline.MaxConcurrentStreams);
        var a = slots.TryEnter();
        var b = slots.TryEnter();
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Null(slots.TryEnter());
        a!.Dispose();
        a.Dispose();   // once only
        Assert.Equal(1, slots.Taken);
        Assert.NotNull(slots.TryEnter());
    }

    [Fact]
    public async Task A_setup_above_200_MB_is_refused_before_any_byte()
    {
        using var ops = new OperationsB();
        var setup = OperationsB.FakeSetup(1000);
        var sums = OperationsB.CouchHudSums(Next, setup);
        var big = new SetupStreamArgs(Next, SignedSetupVerifier.MaxCouchHudSetupBytes + 1, Convert.ToBase64String(sums), ops.CouchHudKey.SignCouchHud(sums));
        Assert.Equal(ErrorCodes.StreamTooLarge, OperationsB.Code(await ops.SendHeaderAsync(Operation.InstallCouchHudUpdate, big, setup)));
        Assert.Empty(ops.StagedFolders());
    }

    [Fact]
    public void A_setup_stream_must_come_at_8_MiB_per_second()
    {
        Assert.True(SignedSetupPipeline.MinSetupBytesPerSecond >= 8 * 1024 * 1024);
        Assert.True(SignedSetupPipeline.StreamDeadline(SignedSetupVerifier.MaxCouchHudSetupBytes) <= TimeSpan.FromSeconds(40));
    }

    [Fact]
    public async Task Two_broken_streams_an_hour_use_up_the_account_s_failure_budget()
    {
        using var ops = new OperationsB();
        var setup = OperationsB.FakeSetup(50_000);
        var tampered = (byte[])setup.Clone();
        tampered[^1] ^= 1;
        for (var i = 0; i < 2; i++)
        {
            var answers = await ops.SendSetupAsync(Operation.InstallCouchHudUpdate, ops.CouchHudArgs(Next, setup), tampered);
            Assert.Equal(ErrorCodes.VerificationFailed, OperationsB.Code(answers[1]));
        }
        // Even a good setup is refused now (before a byte is taken); another account is not affected.
        Assert.Equal(ErrorCodes.RateLimited, OperationsB.Code(await ops.SendHeaderAsync(Operation.InstallCouchHudUpdate, ops.CouchHudArgs(Next, setup), setup)));
        var other = await ops.SendSetupAsync(Operation.InstallCouchHudUpdate, ops.CouchHudArgs(Next, setup), setup, Callers.User(Callers.OtherSid));
        Assert.Equal(ErrorCodes.Ok, OperationsB.Code(other[1]));
    }

    [Fact]
    public async Task A_setup_that_does_not_end_in_time_is_ended_with_its_children_and_holds_the_slot_until_then()
    {
        using var ops = new OperationsB(setupTimeout: TimeSpan.FromMilliseconds(200));
        using var gate = new ManualResetEventSlim();   // never set: only the kill ends it
        ops.Starter.Gate = gate;
        var setup = OperationsB.FakeSetup();
        var answers = await ops.SendSetupAsync(Operation.InstallCouchHudUpdate, ops.CouchHudArgs(Next, setup), setup);
        var job = await ops.WaitForJobAsync(OperationsB.JobId(answers[1]));
        Assert.Equal("failed", job.State);
        Assert.Equal("slow", job.Message);
        Assert.Equal(1, ops.Starter.Killed);
        Assert.False(ops.Parts.Setups.Busy);
        Assert.Empty(Directory.GetFiles(ops.JournalFolder));   // every end this instance sees removes the record
    }

    [Theory]
    [InlineData(5)]
    [InlineData(SignedSetupPipeline.ExitServiceNotStarted)]
    public async Task The_journal_record_is_removed_on_a_failed_end_too(int exitCode)
    {
        using var ops = new OperationsB();
        ops.Starter.ExitCode = exitCode;
        var setup = OperationsB.FakeSetup();
        var answers = await ops.SendSetupAsync(Operation.InstallCouchHudUpdate, ops.CouchHudArgs(Next, setup), setup);
        Assert.Equal("failed", (await ops.WaitForJobAsync(OperationsB.JobId(answers[1]))).State);
        Assert.Equal(1, Assert.Single(ops.Starter.Started).JournaledJobs);
        Assert.Empty(Directory.GetFiles(ops.JournalFolder));
    }

    [Fact]
    public void Only_CouchHud_setup_records_are_recovered()
    {
        using var ops = new OperationsB();
        var journal = ops.Journal;
        Directory.CreateDirectory(ops.JournalFolder);
        journal.Write(new Jobs.JournaledJob(new string('e', 32), Callers.UserSid, "caseHudSetup", "1.1.0", ops.WallMs, null));
        journal.Write(new Jobs.JournaledJob(new string('f', 32), Callers.UserSid, "windowsUpdateInstall", "1.1.0", ops.WallMs, null));
        var jobs = new Jobs.JobStore(new FakeClock());
        Assert.Equal(0, ops.NewPipeline(jobs).RecoverJobs());
        Assert.Null(jobs.Find(new string('e', 32)));
    }

    [Fact]
    public async Task A_setup_request_without_a_stream_path_gives_its_slot_and_folder_back()
    {
        using var ops = new OperationsB();
        var setup = OperationsB.FakeSetup();
        var response = await ops.Harness.Dispatcher.DispatchAsync(Callers.User(), ProtocolJson.SerializeRequest(Operation.InstallCouchHudUpdate, ops.CouchHudArgs(Next, setup)), CancellationToken.None);
        Assert.Equal(ErrorCodes.StreamReady, OperationsB.Code(response));
        Assert.False(ops.Parts.Setups.Busy);
        Assert.Empty(ops.StagedFolders());
    }

    [Fact]
    public async Task A_process_that_runs_another_image_is_ended()
    {
        using var ops = new OperationsB();
        ops.Starter.ImageOverride = @"C:\Windows\Temp\evil.exe";
        var setup = OperationsB.FakeSetup();
        var answers = await ops.SendSetupAsync(Operation.InstallCouchHudUpdate, ops.CouchHudArgs(Next, setup), setup);
        var job = await ops.WaitForJobAsync(OperationsB.JobId(answers[1]));
        Assert.Equal("failed", job.State);
        Assert.Equal("wrongImage", job.Message);
    }

    [Fact]
    public async Task Setups_are_limited_to_six_an_hour_per_account()
    {
        using var ops = new OperationsB();
        var setup = OperationsB.FakeSetup(1000);
        using var stranger = new TestKey();
        var bad = ops.CouchHudArgs(Next, setup, signature: stranger.SignCouchHud(OperationsB.CouchHudSums(Next, setup)));
        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(ErrorCodes.VerificationFailed, OperationsB.Code(await ops.SendHeaderAsync(Operation.InstallCouchHudUpdate, bad, setup)));
        }
        Assert.Equal(ErrorCodes.RateLimited, OperationsB.Code(await ops.SendHeaderAsync(Operation.InstallCouchHudUpdate, bad, setup)));
        // Another account has its own budget.
        Assert.Equal(ErrorCodes.VerificationFailed, OperationsB.Code(await ops.SendHeaderAsync(Operation.InstallCouchHudUpdate, bad, setup, Callers.User(Callers.OtherSid))));
    }
}

/// <summary>
/// The data folder below the service's own folder (security review H1/H2): checked, never moved aside; a failed check refuses setups
/// with an alarm; the folders of a staged setup are held open.
/// </summary>
public class StagingAreaTests
{
    [Fact]
    public void The_data_folder_lies_below_the_service_folder_and_is_checked_link_by_link()
    {
        using var ops = new OperationsB(prepare: false);
        Directory.CreateDirectory(Path.Combine(ops.DataRoot, "staging", "leftover"));
        Assert.False(ops.Staging.Ready);
        Assert.Null(ops.Staging.Prepare());
        Assert.True(ops.Staging.Ready);
        Assert.Equal(Path.Combine(ops.ServiceDirectory, "data"), ops.Staging.DataRoot);
        Assert.Equal(CouchHud.Privileged.ServiceInstall.DataFolder(ops.ServiceDirectory), ops.Staging.DataRoot);
        Assert.Equal(CouchHud.Privileged.ServiceInstall.SetupLogFolder(ops.ServiceDirectory), ops.Staging.SetupLogRoot);
        Assert.Throws<ArgumentException>(() => ops.Staging.EnsureFolder(Path.Combine(ops.Root, "elsewhere")));
        foreach (var folder in new[] { ops.ServiceDirectory, ops.DataRoot, ops.Staging.StagingRoot }) Assert.True(ops.Security.WasChecked(folder), folder);
        Assert.Empty(ops.StagedFolders());
    }

    [Theory]
    [InlineData("service")]
    [InlineData("data")]
    [InlineData("staging")]
    public void A_folder_in_the_chain_that_is_not_protected_refuses_setups_with_an_alarm_and_nothing_is_moved(string which)
    {
        using var ops = new OperationsB(prepare: false);
        Directory.CreateDirectory(ops.Staging.StagingRoot);
        File.WriteAllText(Path.Combine(ops.Staging.StagingRoot, "planted.dll"), "x");
        var folder = which switch { "service" => ops.ServiceDirectory, "data" => ops.DataRoot, _ => ops.Staging.StagingRoot };
        ops.Security.Unprotected.Add(folder);

        Assert.NotNull(ops.Staging.Prepare());
        Assert.False(ops.Staging.Ready);
        Assert.False(ops.Parts.Setups.Available);
        Assert.Contains(ops.Log.Lines, l => l.StartsWith("E [alarm]"));
        Assert.True(File.Exists(Path.Combine(ops.Staging.StagingRoot, "planted.dll")));   // nothing moved, nothing used
        Assert.Empty(Directory.GetDirectories(ops.Root, "*untrusted*", SearchOption.AllDirectories));
        Assert.Throws<UnsafeFolderException>(() => ops.Staging.Create("CouchHud-Setup-0.5.0.exe"));
    }

    [Fact]
    public async Task Setups_are_refused_while_the_data_folder_is_not_safe()
    {
        using var ops = new OperationsB();
        ops.Security.Unprotected.Add(ops.DataRoot);
        var setup = OperationsB.FakeSetup();
        // The authorization still sees the cached "ready"; the full check before staging refuses, and the flag follows.
        Assert.Equal(ErrorCodes.Unavailable, OperationsB.Code(await ops.SendHeaderAsync(Operation.InstallCouchHudUpdate, ops.CouchHudArgs("CouchHud-Setup-0.5.0.exe", setup), setup)));
        Assert.False(ops.Parts.Setups.Available);
        Assert.Equal(ErrorCodes.Denied, OperationsB.Code(await ops.SendHeaderAsync(Operation.InstallCouchHudUpdate, ops.CouchHudArgs("CouchHud-Setup-0.5.0.exe", setup), setup)));
        Assert.Empty(ops.Starter.Started);
    }

    [Fact]
    public void A_staging_folder_that_is_not_protected_after_it_was_made_is_refused_and_removed()
    {
        using var ops = new OperationsB();
        ops.Security.UnprotectedWhen = f => Path.GetDirectoryName(f)!.Equals(ops.Staging.StagingRoot, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<UnsafeFolderException>(() => ops.Staging.Create("CouchHud-Setup-0.5.0.exe"));
        Assert.Empty(ops.StagedFolders());
        Assert.Contains(ops.Log.Lines, l => l.StartsWith("E [alarm]"));
    }

    [Fact]
    public async Task A_temp_folder_that_is_not_protected_after_it_was_made_stops_the_setup()
    {
        using var ops = new OperationsB();
        ops.Security.UnprotectedWhen = f => Path.GetFileName(f) == "tmp";
        var setup = OperationsB.FakeSetup();
        var answers = await ops.SendSetupAsync(Operation.InstallCouchHudUpdate, ops.CouchHudArgs("CouchHud-Setup-0.5.0.exe", setup), setup);
        var job = await ops.WaitForJobAsync(OperationsB.JobId(answers[1]));
        Assert.Equal("notStarted", job.Message);
        Assert.Empty(ops.Starter.Started);
        Assert.Contains(ops.Log.Lines, l => l.StartsWith("E [alarm]"));
    }

    [Theory]
    [InlineData("data")]
    [InlineData("staging")]
    public void A_junction_in_the_chain_is_refused_not_moved_and_nothing_lands_in_its_target(string which)
    {
        using var ops = new OperationsB(prepare: false);
        var target = Path.Combine(ops.Root, "target");
        Directory.CreateDirectory(target);
        if (which == "staging") Directory.CreateDirectory(ops.DataRoot);
        var link = which == "data" ? ops.DataRoot : ops.Staging.StagingRoot;
        Assert.True(OperationsB.MakeJunction(link, target), "junction not created");

        Assert.NotNull(ops.Staging.Prepare());
        Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint));   // left where it is
        Assert.Throws<UnsafeFolderException>(() => ops.Staging.Create("CouchHud-Setup-0.5.0.exe"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
        Assert.Contains(ops.Log.Lines, l => l.StartsWith("E [alarm]"));
    }

    [Fact]
    public void A_junction_that_appears_after_the_check_is_refused_when_the_file_is_made()
    {
        using var ops = new OperationsB();
        var target = Path.Combine(ops.Root, "target");
        Directory.CreateDirectory(target);
        Directory.Delete(ops.Staging.StagingRoot);
        Assert.True(OperationsB.MakeJunction(ops.Staging.StagingRoot, target), "junction not created");

        Assert.Throws<UnsafeFolderException>(() => ops.Staging.Create("CouchHud-Setup-0.5.0.exe"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target));
    }

    [Fact]
    public void The_folders_of_a_staged_setup_cannot_be_renamed_while_it_is_staged()
    {
        using var ops = new OperationsB();
        var staged = ops.Staging.Create("CouchHud-Setup-0.5.0.exe");
        Assert.Equal(3, staged.HeldFolders);   // data, staging, <guid>
        Assert.ThrowsAny<IOException>(() => Directory.Move(staged.Folder, staged.Folder + "-moved"));
        Assert.ThrowsAny<IOException>(() => Directory.Move(ops.Staging.StagingRoot, ops.Staging.StagingRoot + "-moved"));
        Assert.ThrowsAny<IOException>(() => Directory.Move(ops.DataRoot, ops.DataRoot + "-moved"));
        ops.Staging.CreateTempFolder(staged);
        Assert.Equal(4, staged.HeldFolders);
        staged.Dispose();
        Assert.False(Directory.Exists(staged.Folder));
        Directory.Move(ops.Staging.StagingRoot, ops.Staging.StagingRoot + "-moved");   // free again
    }

    [Fact]
    public void A_folder_the_test_user_made_is_not_protected_by_the_real_check()
    {
        using var ops = new OperationsB();
        // Owned by this (non-system) user: the real ACL check must say no.
        Assert.False(new WindowsFolderSecurity().IsProtected(ops.Root));
        Assert.False(new WindowsFolderSecurity().IsProtected(Path.Combine(ops.Root, "missing")));
    }

    [Theory]
    [InlineData(@"..\x.exe")]
    [InlineData(@"C:\x.exe")]
    [InlineData("")]
    public void Only_a_plain_name_is_staged(string name)
    {
        using var ops = new OperationsB();
        Assert.Throws<ArgumentException>(() => ops.Staging.Create(name));
    }

    [Fact]
    public void Only_a_Windows_executable_passes_the_PE_check()
    {
        Assert.True(PortableExecutable.IsExecutable(new MemoryStream(OperationsB.FakeSetup(4096))));
        var dll = OperationsB.FakeSetup(4096);
        BitConverter.GetBytes((ushort)0x2102).CopyTo(dll, 0x80 + 22);
        Assert.False(PortableExecutable.IsExecutable(new MemoryStream(dll)));
        Assert.False(PortableExecutable.IsExecutable(new MemoryStream(new byte[10])));
        var farOffset = OperationsB.FakeSetup(4096);
        BitConverter.GetBytes(int.MaxValue).CopyTo(farOffset, 0x3C);
        Assert.False(PortableExecutable.IsExecutable(new MemoryStream(farOffset)));
    }
}
