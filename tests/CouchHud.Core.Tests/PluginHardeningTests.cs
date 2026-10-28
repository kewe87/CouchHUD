using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using CouchHud.Core.Plugins;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

public class PluginHardeningTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("couchhud-plugins-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string DataDir => Path.Combine(_root, "data");

    private string UserPlugins => Path.Combine(DataDir, "plugins");

    private string MakePlugin(string folder, string content = "dll v1")
    {
        var dir = Path.Combine(UserPlugins, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, folder + ".dll"), content);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), """{"name":"Test","version":"1.0.0"}""");
        return dir;
    }

    // ---- M10: hashes and approval ----

    [Fact]
    public void The_hash_follows_the_files_and_nothing_else()
    {
        var dir = MakePlugin("Hallo");
        var first = PluginTrust.HashFolder(dir);

        Assert.Equal(first, PluginTrust.HashFolder(dir));
        File.WriteAllText(Path.Combine(dir, "Hallo.dll"), "dll v2");
        var changed = PluginTrust.HashFolder(dir);
        Assert.NotEqual(first, changed);

        File.WriteAllText(Path.Combine(dir, "Hallo.dll"), "dll v1");
        Assert.Equal(first, PluginTrust.HashFolder(dir));

        // An extra file counts, and so does a different name for the same content.
        File.WriteAllText(Path.Combine(dir, "extra.dll"), "x");
        Assert.NotEqual(first, PluginTrust.HashFolder(dir));
        Assert.Matches("^[0-9a-f]{64}$", first);
        Assert.Matches("^[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}$", PluginTrust.ShortCode(first));
    }

    [Fact]
    public void A_folder_nobody_installed_is_unknown_and_a_changed_one_is_changed()
    {
        var dir = MakePlugin("Hallo");
        var trust = new PluginTrust(DataDir);

        Assert.Equal(PluginTrustState.Unknown, trust.Check("Hallo", dir));
        trust.Trust("Hallo", dir);
        Assert.Equal(PluginTrustState.Trusted, trust.Check("Hallo", dir));
        Assert.Equal(PluginTrustState.Trusted, new PluginTrust(DataDir).Check("hallo", dir));

        File.AppendAllText(Path.Combine(dir, "Hallo.dll"), "!");
        Assert.Equal(PluginTrustState.Changed, trust.Check("Hallo", dir));
        File.WriteAllText(Path.Combine(dir, "dropped.dll"), "evil");
        File.WriteAllText(Path.Combine(dir, "Hallo.dll"), "dll v1");
        Assert.Equal(PluginTrustState.Changed, trust.Check("Hallo", dir));

        trust.Forget("Hallo");
        Assert.Equal(PluginTrustState.Unknown, trust.Check("Hallo", dir));
    }

    [Fact]
    public void A_broken_trust_file_trusts_nothing()
    {
        var dir = MakePlugin("Hallo");
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(Path.Combine(DataDir, "plugin-trust.json"), "{ not json");

        // A list nobody can read is not "empty": it is a fault, nothing loads and nothing is written over it.
        Assert.Equal(PluginTrustState.Fault, new PluginTrust(DataDir).Check("Hallo", dir));
        Assert.Equal("{ not json", File.ReadAllText(Path.Combine(DataDir, "plugin-trust.json")));
    }

    [Fact]
    public void Plugins_from_before_the_check_are_adopted_once()
    {
        var dir = MakePlugin("Old");
        var trust = new PluginTrust(DataDir);

        Assert.True(trust.AdoptExisting(UserPlugins));
        Assert.Equal(PluginTrustState.Trusted, trust.Check("Old", dir));

        // From then on a new folder is not trusted by that.
        var late = MakePlugin("Late");
        Assert.False(trust.AdoptExisting(UserPlugins));
        Assert.Equal(PluginTrustState.Unknown, trust.Check("Late", late));
    }

    [Fact]
    public void An_unapproved_user_plugin_is_not_loaded_but_listed_with_a_code()
    {
        MakePlugin("Dropped");
        var manager = new PluginManager([(UserPlugins, true)], DataDir, _ => true) { Trust = new PluginTrust(DataDir) };

        manager.LoadAll();

        var info = Assert.Single(manager.Plugins);
        Assert.True(info.NeedsApproval);
        Assert.False(info.Changed);
        Assert.False(info.Loaded);
        Assert.NotNull(info.Error);
        Assert.Matches("^[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}$", info.ApprovalCode);
    }

    [Fact]
    public void Approving_remembers_the_files_and_a_later_change_asks_again()
    {
        var dir = MakePlugin("Dropped");
        // A real assembly (the SDK's own): approving looks at the code like an install does, garbage is no plugin to approve.
        File.Copy(typeof(CouchHud.Sdk.IPlugin).Assembly.Location, Path.Combine(dir, "Dropped.dll"), overwrite: true);
        var trust = new PluginTrust(DataDir);
        var manager = new PluginManager([(UserPlugins, true)], DataDir, _ => true) { Trust = trust };

        manager.Approve("Dropped", PluginTrust.HashFolder(dir));
        Assert.Equal(PluginTrustState.Trusted, trust.Check("Dropped", dir));

        File.WriteAllText(Path.Combine(dir, "plugin.json"), """{"name":"Test","version":"1.0.1"}""");
        var changed = new PluginManager([(UserPlugins, true)], DataDir, _ => true) { Trust = trust };
        changed.LoadAll();
        var info = Assert.Single(changed.Plugins);
        Assert.True(info.NeedsApproval);
        Assert.True(info.Changed);
    }

    [Fact]
    public void Approving_is_bound_to_the_files_the_user_was_shown_and_looks_at_their_code()
    {
        var dir = MakePlugin("Dropped");
        File.Copy(typeof(CouchHud.Sdk.IPlugin).Assembly.Location, Path.Combine(dir, "Dropped.dll"), overwrite: true);
        var trust = new PluginTrust(DataDir);
        var manager = new PluginManager([(UserPlugins, true)], DataDir, _ => true) { Trust = trust };
        var shown = PluginTrust.HashFolder(dir);

        // No hash, a wrong hash, a malformed one: refused, nothing recorded.
        Assert.Throws<InvalidOperationException>(() => manager.Approve("Dropped", null));
        Assert.Throws<InvalidOperationException>(() => manager.Approve("Dropped", new string('0', 64)));
        Assert.Throws<InvalidOperationException>(() => manager.Approve("Dropped", shown[..60]));
        Assert.Equal(PluginTrustState.Unknown, trust.Check("Dropped", dir));

        // Files swapped after the dialog showed its code: the shown hash no longer fits.
        File.AppendAllText(Path.Combine(dir, "readme.txt"), "swapped after the dialog");
        Assert.Throws<InvalidOperationException>(() => manager.Approve("Dropped", shown));
        Assert.Equal(PluginTrustState.Unknown, trust.Check("Dropped", dir));

        // Unreadable code (a DLL that is no assembly) is refused at the approval itself, even with the right hash.
        File.WriteAllText(Path.Combine(dir, "Dropped.dll"), "no assembly");
        Assert.Throws<CatalogException>(() => manager.Approve("Dropped", PluginTrust.HashFolder(dir)));
        Assert.Equal(PluginTrustState.Unknown, trust.Check("Dropped", dir));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(@"..\x")]
    [InlineData("a/b")]
    [InlineData("")]
    [InlineData(".couchhud-new-1")]
    public void Only_a_plain_folder_name_can_be_approved(string folder)
    {
        MakePlugin("Real");
        var manager = new PluginManager([(UserPlugins, true)], DataDir, _ => true) { Trust = new PluginTrust(DataDir) };

        Assert.Throws<ArgumentException>(() => manager.Approve(folder, new string('a', 64)));
    }


    // ---- M10: staged updates are checked again ----

    private static string Sha(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));

    private string MakeZip(string name, string dllContent)
    {
        var zip = Path.Combine(_root, name + ".zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("Staged.dll");
            using (var writer = new StreamWriter(entry.Open())) writer.Write(dllContent);
        }
        return zip;
    }

    private void Stage(string zip, string? sha256)
    {
        Directory.CreateDirectory(PendingPackages.Folder(DataDir));
        File.Copy(zip, Path.Combine(PendingPackages.Folder(DataDir), "plugin-staged.zip"), overwrite: true);
        new CatalogRegistry(DataDir).Set("plugin:staged", new CatalogRegistry.Record("1.1.0", "Staged", true, null, sha256));
        // The verified hash lives under the trust store's HMAC (the registry is only bookkeeping).
        if (sha256 is not null) new PluginTrust(DataDir).StagePending("Staged", new PluginTrustEntry { Hash = sha256 });
    }

    [Fact]
    public void A_staged_update_is_checked_against_the_hash_before_it_goes_in()
    {
        var good = MakeZip("good", "the verified plugin");
        Stage(good, Sha(good));
        // Something swaps the staged file after it was verified.
        File.Copy(MakeZip("evil", "a different plugin"), Path.Combine(PendingPackages.Folder(DataDir), "plugin-staged.zip"), overwrite: true);
        var log = new List<string>();

        var applied = PendingPackages.ApplyAll(DataDir, log.Add);

        Assert.Empty(applied);
        Assert.False(Directory.Exists(Path.Combine(UserPlugins, "Staged")));
        Assert.Contains(log, l => l.Contains("pending update"));
    }

    [Fact]
    public void A_staged_update_without_a_recorded_hash_is_not_applied()
    {
        Stage(MakeZip("nohash", "x"), null);

        Assert.Empty(PendingPackages.ApplyAll(DataDir));
        Assert.False(Directory.Exists(Path.Combine(UserPlugins, "Staged")));
    }

    [Fact]
    public void A_verified_staged_update_goes_in_and_counts_as_installed()
    {
        var zip = MakeZip("fine", "the verified plugin");
        Stage(zip, Sha(zip));

        var applied = PendingPackages.ApplyAll(DataDir);

        Assert.Equal(["plugin:staged"], applied);
        var dir = Path.Combine(UserPlugins, "Staged");
        Assert.True(Directory.Exists(dir));
        Assert.Equal(PluginTrustState.Trusted, new PluginTrust(DataDir).Check("Staged", dir));
    }

    // ---- M8: a hanging plugin costs its own line only ----

    private static QuickAction Action(Func<string?> detail) =>
        new() { Id = "a", Label = "A", Detail = detail, Run = _ => Task.FromResult<string?>(null) };

    [Fact]
    public async Task A_quick_action_detail_that_hangs_is_dropped_after_the_timeout_and_not_asked_again_meanwhile()
    {
        var manager = new PluginManager([], DataDir, _ => true) { DetailTimeout = TimeSpan.FromMilliseconds(150) };
        using var release = new ManualResetEventSlim();
        var calls = 0;
        var action = Action(() =>
        {
            Interlocked.Increment(ref calls);
            release.Wait(TimeSpan.FromSeconds(10));
            return "late";
        });

        var started = DateTime.UtcNow;
        Assert.Null(await manager.DetailAsync("slow", action));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));

        // While the first call still hangs, no second thread is started.
        var again = DateTime.UtcNow;
        Assert.Null(await manager.DetailAsync("slow", action));
        Assert.True(DateTime.UtcNow - again < TimeSpan.FromMilliseconds(100));
        Assert.Equal(1, calls);

        release.Set();
        await Task.Delay(100);
        Assert.Equal("late", await manager.DetailAsync("slow", action));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_quick_action_detail_answers_normally_and_a_throwing_one_is_just_empty()
    {
        var manager = new PluginManager([], DataDir, _ => true);

        Assert.Equal("Für Spiel", await manager.DetailAsync("p", Action(() => "Für Spiel")));
        Assert.Null(await manager.DetailAsync("p", Action(() => throw new InvalidOperationException("boom"))));
        Assert.Null(await manager.DetailAsync("p", new QuickAction { Id = "none", Label = "N", Run = _ => Task.FromResult<string?>(null) }));
    }

    [Fact]
    public async Task The_calling_thread_is_not_the_one_that_runs_the_plugin()
    {
        var manager = new PluginManager([], DataDir, _ => true);
        var caller = Environment.CurrentManagedThreadId;
        int? ran = null;

        await manager.DetailAsync("p", Action(() =>
        {
            ran = Environment.CurrentManagedThreadId;
            return "x";
        }));

        Assert.NotNull(ran);
        Assert.NotEqual(caller, ran);
    }

    // ---- M10: SDK 2 lock state ----

    [Fact]
    public void The_sdk_has_the_lock_state_and_stays_compatible()
    {
        Assert.Equal(3, SdkVersion.Current);
        // A host (or a test double) that knows nothing of SDK 2 answers "not locked" through the default implementation.
        IPluginContext old = new OldContext();
        Assert.False(old.Locked);
        var events = new PluginEvents();
        bool? seen = null;
        events.LockChanged += locked => seen = locked;
        events.RaiseLockChanged(true);
        SpinWait.SpinUntil(() => seen is not null, TimeSpan.FromSeconds(2));
        Assert.True(seen);
        // A manifest for SDK 1 is still read as before.
        Assert.Equal(1, JsonSerializer.Deserialize<PluginManifest>("""{"name":"X"}""")!.Sdk);
    }

    private sealed class OldContext : IPluginContext
    {
        public string DataDirectory => "";
        public IPluginSettings Settings => throw new NotSupportedException();
        public IPluginEvents Events => throw new NotSupportedException();
        public void RegisterLibraryProvider(ILibraryProvider provider) { }
        public void Log(string message) { }
        public void RegisterSettings(params PluginSetting[] settings) { }
        public void RegisterPage(PluginPage page) { }
        public void RegisterQuickAction(QuickAction action) { }
        public void RegisterTool(PluginTool tool) { }
        public void Notify(string title, string body) { }
    }
}
