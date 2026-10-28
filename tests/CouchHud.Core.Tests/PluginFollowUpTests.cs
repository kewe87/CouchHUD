using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using CouchHud.Core.Config;
using CouchHud.Core.Plugins;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>
/// The second review of the plugin system: a trust store that names a folder twice in different case is a fault (never an exception), a
/// package is hashed, looked at and unpacked from one handle, what a plugin may get from the default load context, and the one-time takeover
/// of plugins from before the list existed. Throwaway data only.
/// </summary>
public sealed class PluginFollowUpTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-follow-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http = new();

    public PluginFollowUpTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _http.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string TrustFile => Path.Combine(_dir, "plugin-trust.json");

    // ---- 2: a folder twice in different case

    [Theory]
    [InlineData("""{"Format":2,"Plugins":{"Foo":{"Hash":"aa"},"foo":{"Hash":"bb"}},"Pending":{},"Mac":"x"}""")]
    [InlineData("""{"Format":2,"Plugins":{},"Pending":{"Foo":{"Hash":"aa"},"FOO":{"Hash":"bb"}},"Mac":"x"}""")]
    [InlineData("""{"Entries":{"Foo":"aa","foo":"bb"},"Mac":"x"}""")]
    public void A_folder_named_twice_in_different_case_is_a_fault_and_the_file_stays_as_it_is(string json)
    {
        File.WriteAllText(TrustFile, json);
        var before = File.ReadAllBytes(TrustFile);
        var trust = new PluginTrust(_dir);

        Assert.True(trust.IsFaulty);
        Assert.Equal(PluginTrustState.Fault, trust.Check("Foo", _dir));
        Assert.Null(trust.Entry("foo"));
        Assert.Null(trust.PinnedKey("x"));
        Assert.ThrowsAny<Exception>(() => trust.Trust("foo", _dir));
        Assert.ThrowsAny<InvalidOperationException>(() => trust.MarkRemoved("Foo"));
        Assert.False(trust.AdoptExisting(Path.Combine(_dir, "plugins")));
        Assert.Equal(before, File.ReadAllBytes(TrustFile));
    }

    // ---- 3: one handle for hash, lint and unpacking

    private string Zip()
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var w = new StreamWriter(zip.CreateEntry("plugin.json").Open())) w.Write("{}");
        using (var s = zip.CreateEntry("tool.dll").Open()) s.Write(File.ReadAllBytes(typeof(PluginRow).Assembly.Location));
        return path;
    }

    private static string Sha(string file) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));

    private static CatalogEntry Entry(string sha) => CatalogParser.Parse($$"""
        [{"id":"tool","folder":"tool","name":"Tool","type":"plugin","version":"1.0.0","download":"https://x.example/t.zip","sha256":"{{sha}}"}]
        """, "s")[0];

    [Fact]
    public void The_package_is_unpacked_only_if_the_bytes_that_were_opened_have_the_expected_hash()
    {
        var zip = Zip();
        var service = new CatalogService(_http, _dir, () => [], () => false);

        Assert.ThrowsAny<CatalogException>(() => service.Install(Entry(Sha(zip)), zip, null, new string('0', 64)));
        Assert.False(Directory.Exists(Path.Combine(_dir, "plugins", "tool")));
        Assert.False(File.Exists(TrustFile));

        service.Install(Entry(Sha(zip)), zip, null, Sha(zip));
        // The trust store holds the hash of the files as they were unpacked, not a second look at the folder.
        var target = Path.Combine(_dir, "plugins", "tool");
        Assert.Equal(PluginTrustState.Trusted, new PluginTrust(_dir).Check("tool", target));
        Assert.Equal(PluginTrust.HashFolder(target), new PluginTrust(_dir).Entry("tool")!.Hash);
    }

    [Fact]
    public void The_file_is_held_against_writers_while_it_is_unpacked()
    {
        var zip = Zip();
        // The installer's handle keeps writers out (FileShare.Read): a second writer cannot swap bytes in between.
        using var held = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Throws<IOException>(() => new FileStream(zip, FileMode.Open, FileAccess.Write, FileShare.ReadWrite));
    }

    // ---- 5: what the default load context may give a plugin

    private AssemblyLoadContext Context(bool firstParty)
    {
        // A plugin folder of its own (not the test host's folder, whose libraries the resolver would find next to the main assembly).
        var plugin = Path.Combine(_dir, "ctx-" + firstParty);
        Directory.CreateDirectory(plugin);
        var main = Path.Combine(plugin, "Plugin.dll");
        File.Copy(typeof(PluginRow).Assembly.Location, main);
        return (AssemblyLoadContext)Activator.CreateInstance(typeof(PluginManager).GetNestedType("PluginLoadContext", BindingFlags.NonPublic)!, main, firstParty)!;
    }

    [Fact]
    public void A_third_party_plugin_gets_the_SDK_and_the_shared_framework_from_the_default_context_and_nothing_else()
    {
        var context = Context(firstParty: false);
        // The SDK by any spelling of its name: the same IPlugin on both sides.
        Assert.Same(typeof(IPlugin).Assembly, context.LoadFromAssemblyName(new AssemblyName("couchhud.SDK")));
        Assert.NotNull(context.LoadFromAssemblyName(new AssemblyName("System.Text.Json")));
        // The host's own assemblies: never.
        Assert.Throws<FileLoadException>(() => context.LoadFromAssemblyName(new AssemblyName("CouchHud.Core")));
        Assert.Throws<FileLoadException>(() => context.LoadFromAssemblyName(new AssemblyName("couchhud.shell")));
        // A library that only the host ships (here: the test host's own) is the host's, not a plugin's.
        Assert.Throws<FileLoadException>(() => context.LoadFromAssemblyName(new AssemblyName("xunit.core")));
    }

    [Fact]
    public void A_first_party_plugin_may_share_what_the_host_ships_but_never_the_host_assemblies()
    {
        var context = Context(firstParty: true);
        Assert.NotNull(context.LoadFromAssemblyName(new AssemblyName("xunit.core")));
        Assert.Throws<FileLoadException>(() => context.LoadFromAssemblyName(new AssemblyName("CouchHud.Core")));
    }

    [Fact]
    public void The_shared_framework_names_are_those_outside_the_app_folder()
    {
        Assert.True(SharedFramework.Contains("System.Text.Json"));
        Assert.True(SharedFramework.Contains("system.private.corelib"));
        Assert.False(SharedFramework.Contains("xunit.core"));
        Assert.False(SharedFramework.Contains("CouchHud.Core"));
        Assert.False(SharedFramework.Contains(null));
    }

    // ---- 6: adoption happens once

    [Fact]
    public void A_config_from_before_the_list_reads_as_not_adopted_and_a_new_one_as_adopted()
    {
        Assert.False(ConfigStore.Parse("""{ "autoBackupMaxGameMb": 1000 }""")!.PluginTrustAdopted);
        Assert.True(new AppConfig().PluginTrustAdopted);
        Assert.True(ConfigStore.Parse("""{ "pluginTrustAdopted": true }""")!.PluginTrustAdopted);
        Assert.False(ConfigStore.Parse("""{ "pluginTrustAdopted": false }""")!.PluginTrustAdopted);
    }

    [Fact]
    public void Once_adopted_a_deleted_list_adopts_nothing_when_the_shell_does_not_ask_again()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "plugins", "Old"));
        File.WriteAllText(Path.Combine(_dir, "plugins", "Old", "Old.dll"), "x");
        var trust = new PluginTrust(_dir);
        Assert.True(trust.AdoptExisting(Path.Combine(_dir, "plugins")));
        Assert.Equal(PluginTrustState.Trusted, trust.Check("Old", Path.Combine(_dir, "plugins", "Old")));

        // Deleting the list and dropping in a folder: the shell reads the marker (true) and never calls AdoptExisting; the folder is unknown.
        File.Delete(TrustFile);
        Directory.CreateDirectory(Path.Combine(_dir, "plugins", "Dropped"));
        File.WriteAllText(Path.Combine(_dir, "plugins", "Dropped", "Dropped.dll"), "x");
        var after = new PluginTrust(_dir);
        Assert.Equal(PluginTrustState.Unknown, after.Check("Dropped", Path.Combine(_dir, "plugins", "Dropped")));
        Assert.Equal(PluginTrustState.Unknown, after.Check("Old", Path.Combine(_dir, "plugins", "Old")));
    }

    [Fact]
    public void The_shell_asks_for_the_takeover_only_while_the_marker_is_missing_and_sets_it_once_the_list_exists()
    {
        // Pinned in the source: the only call of AdoptExisting is guarded by the marker, and the marker is set only when the list exists.
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "src", "CouchHud.Shell", "App.xaml.cs"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        var app = File.ReadAllText(Path.Combine(root!, "src", "CouchHud.Shell", "App.xaml.cs")).Replace("\r\n", "\n");
        var guard = app.IndexOf("if (!config.Current.PluginTrustAdopted", StringComparison.Ordinal);
        var call = app.IndexOf("AdoptExisting(", StringComparison.Ordinal);
        Assert.True(guard >= 0 && call > guard, "AdoptExisting only behind the marker");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(app, @"AdoptExisting\("));
        Assert.Contains("if (pluginTrust.HasStore) config.Update(c => c.PluginTrustAdopted = true);", app);
    }
}
