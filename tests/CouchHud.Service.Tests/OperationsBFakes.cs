using System.Security.Cryptography;
using System.Text;
using CouchHud.Core.Plugins;
using CouchHud.Privileged;
using CouchHud.Service.Contracts;
using CouchHud.Service.Dispatch;
using CouchHud.Service.Jobs;
using CouchHud.Service.Setup;
using CouchHud.Service.Updates;

namespace CouchHud.Service.Tests;

/// <summary>A throwaway release key, made in memory for one test run (never a real key).</summary>
internal sealed class TestKey : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public string PublicPem => _key.ExportSubjectPublicKeyInfoPem();

    /// <summary>CouchHUD releases: IEEE P1363 over "couchhud-release-v1\n" + file.</summary>
    public string SignCouchHud(byte[] data, string context = CatalogSignature.ReleaseContext) =>
        Convert.ToBase64String(_key.SignData(CatalogSignature.WithContext(context, data), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    public void Dispose() => _key.Dispose();
}

internal sealed class FakeInstalled : IInstalledVersions
{
    public SemVer? CouchHudVersion { get; set; } = new SemVer(0, 4, 1, "");

    public SemVer? CouchHud() => CouchHudVersion;
}

internal sealed class FakeFolderSecurity : IFolderSecurity
{
    /// <summary>Folders reported as not protected (full path), and a rule on top.</summary>
    public HashSet<string> Unprotected { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Func<string, bool>? UnprotectedWhen { get; set; }
    public List<string> Checked { get; } = [];

    public bool IsProtected(string directory)
    {
        var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        lock (Checked) Checked.Add(full);
        return !Unprotected.Contains(full) && UnprotectedWhen?.Invoke(full) != true;
    }

    public bool WasChecked(string directory)
    {
        var full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        lock (Checked) return Checked.Contains(full, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>What the starter saw at the moment of the start.</summary>
internal sealed record StartRecord(string Path, string Arguments, string WorkingDirectory, string Sha256, bool WriteRefused, bool DeleteRefused, int JournaledJobs,
    IReadOnlyDictionary<string, string> Environment, bool TempExisted);

internal sealed class FakeStarter : ISetupStarter
{
    public List<StartRecord> Started { get; } = [];
    public int ExitCode { get; set; }
    public string? ImageOverride { get; set; }
    public string? JournalFolder { get; set; }

    /// <summary>Set: the setup "runs" until it is released.</summary>
    public ManualResetEventSlim? Gate { get; set; }

    public ISetupProcess? Start(string path, string arguments, string workingDirectory, IReadOnlyDictionary<string, string> environment)
    {
        string sha;
        using (var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) sha = Convert.ToHexStringLower(SHA256.HashData(read));
        var writeRefused = Refused(() => new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite).Dispose());
        var deleteRefused = Refused(() => File.Delete(path)) || File.Exists(path);
        var journaled = JournalFolder is not null && Directory.Exists(JournalFolder) ? Directory.GetFiles(JournalFolder, "*.json").Length : 0;
        var tempExisted = environment.TryGetValue("TEMP", out var temp) && Directory.Exists(temp) && Directory.GetFileSystemEntries(temp).Length == 0;
        lock (Started) Started.Add(new StartRecord(path, arguments, workingDirectory, sha, writeRefused, deleteRefused, journaled, environment, tempExisted));
        return new FakeProcess(ImageOverride ?? path, ExitCode, Gate, this);
    }

    private static bool Refused(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>How many started processes were killed.</summary>
    public int Killed;

    private sealed class FakeProcess(string image, int exitCode, ManualResetEventSlim? gate, FakeStarter owner) : ISetupProcess
    {
        private readonly ManualResetEventSlim _killed = new();

        public int Id => 4711;
        public string? ImagePath => image;

        public bool WaitForExit(TimeSpan timeout) =>
            gate is null || WaitHandle.WaitAny([gate.WaitHandle, _killed.WaitHandle], timeout == Timeout.InfiniteTimeSpan ? -1 : (int)timeout.TotalMilliseconds) != WaitHandle.WaitTimeout;

        public int ExitCode => exitCode;

        public void Kill()
        {
            Interlocked.Increment(ref owner.Killed);
            _killed.Set();
        }

        public void Dispose()
        {
        }
    }
}

internal sealed class FakeWindowsUpdate : IWindowsUpdateSession
{
    public List<FoundUpdate> Pending { get; } = [];
    public List<IReadOnlySet<string>?> Installs { get; } = [];
    public ManualResetEventSlim? Gate { get; set; }
    public bool RebootRequired { get; set; } = true;

    public IReadOnlyList<FoundUpdate> Search(CancellationToken cancellation)
    {
        Gate?.Wait(TimeSpan.FromSeconds(10));
        return Pending.ToList();
    }

    /// <summary>Byte counters the fake reports while it "downloads" (done, total), as the agent's IDownloadProgress would.</summary>
    public (long Done, long Total)? Bytes { get; set; }

    public UpdateInstallOutcome Install(IReadOnlySet<string>? ids, Action<int> progress, CancellationToken cancellation, Action<long, long>? bytes = null)
    {
        lock (Installs) Installs.Add(ids);
        if (Bytes is { } b) bytes?.Invoke(b.Done, b.Total);
        Gate?.Wait(TimeSpan.FromSeconds(10));
        progress(50);
        var count = Pending.Count(p => ids is null || ids.Contains(p.Id));
        return new UpdateInstallOutcome(count, 0, RebootRequired, null);
    }
}

/// <summary>The P6 parts with fakes, in a short temporary folder under %TEMP% (staging paths stay below MAX_PATH; removed on dispose).</summary>
internal sealed class OperationsB : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "p6-" + Guid.NewGuid().ToString("N")[..8]);
    public TestKey CouchHudKey { get; } = new();
    public FakeInstalled Installed { get; } = new();
    public FakeFolderSecurity Security { get; } = new();
    public FakeStarter Starter { get; } = new();
    public FakeWindowsUpdate WindowsUpdate { get; } = new();
    public CapturingSink Log { get; } = new();
    public FakeClock Clock { get; } = new();
    public StagingArea Staging { get; }
    public HandlersBParts Parts { get; }
    public Harness Harness { get; }

    /// <summary>Stands for <c>{app}\service</c> (the data folder is below it).</summary>
    public string ServiceDirectory => Path.Combine(Root, "service");

    public string DataRoot => Path.Combine(ServiceDirectory, "data");

    /// <summary>Whether the caller's session is kept during a CouchHUD update; default: never (a code is assumed, no grants).</summary>
    public KeepSessionPolicy KeepSession { get; }

    public TimeSpan SetupTimeout { get; }

    public OperationsB(KeepSessionPolicy? keepSession = null, TimeSpan? setupTimeout = null, bool prepare = true)
    {
        KeepSession = keepSession ?? KeepSessionPolicy.Never;
        Directory.CreateDirectory(Root);
        var events = new Logging.RecentEvents(Clock, () => 1);
        var jobs = new JobStore(Clock);
        var cache = new UpdateSearchCache();
        SetupTimeout = setupTimeout ?? TimeSpan.FromSeconds(10);
        Directory.CreateDirectory(ServiceDirectory);
        Staging = new StagingArea(ServiceDirectory, Security, Log);
        // As at the start of the service: the data folder is checked once, the authorization reads only the outcome.
        if (prepare) Assert.Null(Staging.Prepare());
        Starter.JournalFolder = JournalFolder;
        var setups = NewPipeline(jobs, Staging);
        Parts = new HandlersBParts(new WindowsUpdateJobs(WindowsUpdate, jobs, cache, Log, events, CancellationToken.None), cache, setups, jobs);
        Harness = new Harness(extraHandlers: h => HandlersB.RegisterHandlers(h, Parts), extraChecks: c => HandlersB.RegisterChecks(c, Parts));
    }

    public string JournalFolder => Path.Combine(DataRoot, JobJournal.FolderName);

    public long WallMs { get; set; } = 1_800_000_000_000;

    public JobJournal Journal => new(JournalFolder, () => WallMs);

    /// <summary>A pipeline on the same folders (also: the next instance of the service after a restart).</summary>
    public SignedSetupPipeline NewPipeline(JobStore jobs, StagingArea? staging = null) =>
        new(new SignedSetupVerifier(CouchHudKey.PublicPem), Installed, staging ?? new StagingArea(ServiceDirectory, Security, Log), Starter, jobs, Log,
            new Logging.RecentEvents(Clock, () => 1), setupTimeout: SetupTimeout, streamInactivity: TimeSpan.FromSeconds(5), journal: Journal,
            keepSession: KeepSession);

    // ---- setups ----

    /// <summary>Bytes that pass the PE check (MZ, PE header of an i386 executable), padded with random bytes.</summary>
    public static byte[] FakeSetup(int size = 100_000)
    {
        var bytes = RandomNumberGenerator.GetBytes(size);
        Array.Clear(bytes, 0, 0x100);
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3C);
        "PE\0\0"u8.CopyTo(bytes.AsSpan(0x80));
        BitConverter.GetBytes((ushort)0x014C).CopyTo(bytes, 0x84);
        BitConverter.GetBytes((ushort)0x0102).CopyTo(bytes, 0x80 + 22);
        return bytes;
    }

    public static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static byte[] CouchHudSums(string name, byte[] setup) => Encoding.UTF8.GetBytes($"{Sha(setup)}  {name}\n");

    public SetupStreamArgs CouchHudArgs(string signedName, byte[] setup, string? clientName = null, string? signature = null)
    {
        var sums = CouchHudSums(signedName, setup);
        return new SetupStreamArgs(clientName ?? signedName, setup.Length, Convert.ToBase64String(sums), signature ?? CouchHudKey.SignCouchHud(sums));
    }

    /// <summary>The header frame followed by the bytes as <see cref="StreamCodec"/> sends them.</summary>
    public static byte[][] SetupFrames(Operation op, SetupStreamArgs args, byte[] bytes)
    {
        var stream = new MemoryStream();
        StreamCodec.WriteAsync(stream, new MemoryStream(bytes), bytes.Length).GetAwaiter().GetResult();
        return [FrameCodec.Encode(ProtocolJson.SerializeRequest(op, args)), stream.ToArray()];
    }

    public async Task<List<byte[]>> SendSetupAsync(Operation op, SetupStreamArgs args, byte[] bytes, Authorization.CallerInfo? caller = null) =>
        await Harness.ExchangeAsync(caller ?? Callers.User(), SetupFrames(op, args, bytes));

    /// <summary>A header that is to be refused: sent alone (a client sends the bytes only after "streamReady"), its one answer.</summary>
    public async Task<byte[]> SendHeaderAsync(Operation op, SetupStreamArgs args, byte[] bytes, Authorization.CallerInfo? caller = null) =>
        Assert.Single(await Harness.ExchangeAsync(caller ?? Callers.User(), FrameCodec.Encode(ProtocolJson.SerializeRequest(op, args))));

    // ---- jobs ----

    public async Task<Response<JobData>> GetJobAsync(string id, Authorization.CallerInfo? caller = null)
    {
        var answer = Assert.Single(await Harness.ExchangeAsync(caller ?? Callers.User(), FrameCodec.Encode(ProtocolJson.SerializeRequest(Operation.GetJob, new GetJobArgs(id)))));
        return ProtocolJson.DeserializeResponse<JobData>(answer)!;
    }

    public async Task<JobData> WaitForJobAsync(string id, Authorization.CallerInfo? caller = null)
    {
        for (var i = 0; i < 200; i++)
        {
            var job = Parts.Jobs.Find(id)!.Snapshot();
            if (job.State != Job.Running) return (await GetJobAsync(id, caller)).Data!;
            await Task.Delay(25);
        }
        throw new TimeoutException("job did not end");
    }

    public static string Code(byte[] answer) => ProtocolJson.DeserializeResponse(Harness.Strip(answer))!.Code;

    public static string JobId(byte[] answer) => ProtocolJson.DeserializeResponse<JobStartedData>(answer)!.Data!.JobId;

    public string[] StagedFolders() => Directory.Exists(Staging.StagingRoot) ? Directory.GetDirectories(Staging.StagingRoot) : [];

    public void Dispose()
    {
        CouchHudKey.Dispose();
        for (var i = 0; i < 20; i++)
        {
            try
            {
                if (Directory.Exists(Root)) RemoveTree(Root);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }

    /// <summary>Deletes the tree; junctions go as links, never their targets' content.</summary>
    private static void RemoveTree(string folder)
    {
        foreach (var dir in Directory.GetDirectories(folder))
        {
            if (File.GetAttributes(dir).HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(dir);
            else RemoveTree(dir);
        }
        foreach (var file in Directory.GetFiles(folder)) File.Delete(file);
        Directory.Delete(folder);
    }

    /// <summary>A junction (no privilege needed, unlike a symbolic link), made with cmd's mklink.</summary>
    public static bool MakeJunction(string link, string target)
    {
        var info = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "/c", "mklink", "/J", link, target }) info.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(info)!;
        p.WaitForExit(10_000);
        return Directory.Exists(link) && File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint);
    }
}
