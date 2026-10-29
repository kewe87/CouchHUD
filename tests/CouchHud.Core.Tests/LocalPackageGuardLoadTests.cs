using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using CouchHud.Core.Localization;
using CouchHud.Core.Plugins;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>
/// The second half of the guard for unsigned packages: the loader judges the id the plugin's own code answers with (not only the one in
/// plugin.json), a signed plugin wins an id over a local one in either folder order, and the package that is checked is the package that
/// is installed.
/// </summary>
public class LocalPackageGuardLoadTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("couchhud-guard2-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Assemblies that were loaded stay locked until the test process ends.
        }
    }

    private string DataDir => Path.Combine(_root, "data");
    private string Plugins => Path.Combine(DataDir, "plugins");

    private static int _counter;

    /// <summary>Writes a real plugin assembly (an IPlugin whose Id is the given text) as folder/folder.dll.</summary>
    private static void WriteAssembly(string dir, string folder, string pluginId)
    {
        var name = "GuardTestPlugin" + Interlocked.Increment(ref _counter);
        var ab = new PersistedAssemblyBuilder(new AssemblyName(name), typeof(object).Assembly);
        var mb = ab.DefineDynamicModule(name);
        var tb = mb.DefineType(name + ".P", TypeAttributes.Public | TypeAttributes.Class, typeof(object), [typeof(IPlugin)]);
        tb.DefineDefaultConstructor(MethodAttributes.Public);
        var getter = tb.DefineMethod("get_Id", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.SpecialName | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Final, typeof(string), Type.EmptyTypes);
        var il = getter.GetILGenerator();
        il.Emit(OpCodes.Ldstr, pluginId);
        il.Emit(OpCodes.Ret);
        tb.DefineMethodOverride(getter, typeof(IPlugin).GetProperty("Id")!.GetMethod!);
        var init = tb.DefineMethod("Initialize", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.Final, typeof(void), [typeof(IPluginContext)]);
        init.GetILGenerator().Emit(OpCodes.Ret);
        tb.DefineMethodOverride(init, typeof(IPlugin).GetMethod("Initialize")!);
        tb.CreateType();
        Directory.CreateDirectory(dir);
        ab.Save(Path.Combine(dir, folder + ".dll"));
    }

    private void Folder(PluginTrust trust, string folder, string pluginId, string manifestId, PluginOrigin origin)
    {
        var dir = Path.Combine(Plugins, folder);
        WriteAssembly(dir, folder, pluginId);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), "{\"id\":\"" + manifestId + "\",\"version\":\"1.0.0\"}");
        trust.Trust(folder, dir, new PluginTrustEntry { Hash = "", Origin = origin, PluginId = origin == PluginOrigin.Local ? null : pluginId, Version = "1.0.0", Capabilities = [] });
    }

    private PluginManager Manager(PluginTrust trust, params string[] indexIds) =>
        new([(Plugins, true)], DataDir, _ => true) { Trust = trust, FirstPartyIds = () => indexIds };

    [Theory]
    [InlineData("homeassistant", new string[0])]
    [InlineData("freshofficial", new[] { "freshofficial" })]
    public void A_local_plugin_whose_code_answers_with_a_reserved_id_does_not_load_whatever_plugin_json_says(string codeId, string[] index)
    {
        var trust = new PluginTrust(DataDir);
        Folder(trust, "harmless", codeId, "harmless", PluginOrigin.Local);
        var manager = Manager(trust, index);

        manager.LoadAll();

        var info = Assert.Single(manager.Plugins);
        Assert.False(info.Loaded);
        Assert.Equal(Strings.Get("plugin.idMismatch", codeId), info.Error);
    }

    [Fact]
    public void An_ordinary_id_in_the_code_loads_as_before()
    {
        var trust = new PluginTrust(DataDir);
        Folder(trust, "fine", "fine", "fine", PluginOrigin.Local);
        var manager = Manager(trust, "freshofficial");
        manager.LoadAll();
        Assert.True(Assert.Single(manager.Plugins).Loaded);
    }

    [Theory]
    [InlineData("aaa", "zzz")]
    [InlineData("zzz", "aaa")]
    public void When_a_local_and_a_signed_plugin_claim_one_id_the_signed_one_wins_in_either_folder_order(string localFolder, string signedFolder)
    {
        var trust = new PluginTrust(DataDir);
        Folder(trust, localFolder, "shared", "shared", PluginOrigin.Local);
        Folder(trust, signedFolder, "shared", "shared", PluginOrigin.ThirdParty);
        var manager = Manager(trust);

        manager.LoadAll();

        var signed = manager.Plugins.Single(p => p.Folder == signedFolder);
        var local = manager.Plugins.Single(p => p.Folder == localFolder);
        Assert.True(signed.Loaded, signed.Error);
        Assert.False(local.Loaded);
        Assert.Equal(Strings.Get("plugin.idTaken", "shared"), local.Error);
    }

    private string Zip(string name, params (string Entry, string Text)[] entries)
    {
        var path = Path.Combine(_root, name + ".zip");
        using var stream = File.Create(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var (entry, text) in entries)
        {
            using var w = new StreamWriter(zip.CreateEntry(entry).Open());
            w.Write(text);
        }
        return path;
    }

    [Fact]
    public void The_installed_bytes_are_the_checked_bytes_even_when_the_file_is_swapped_after_the_check()
    {
        Directory.CreateDirectory(Plugins);
        var trust = new PluginTrust(DataDir);
        var manager = new PluginManager([(Plugins, true)], DataDir, _ => true) { Trust = trust };
        var zipPath = Zip("tool", ("tool/tool.dll", "checked bytes"), ("tool/plugin.json", "{\"id\":\"tool\"}"));

        using var copy = PackageInstaller.ReadCapped(zipPath);
        manager.RefuseLocalPackage(copy, "tool", []);
        // The file on disk is replaced by a package that would be refused (an official id) after the check.
        File.Delete(zipPath);
        Zip("tool", ("homeassistant/homeassistant.dll", "swapped bytes"), ("homeassistant/plugin.json", "{\"id\":\"homeassistant\"}"));
        var target = PackageInstaller.Install(copy, "tool", Plugins, PackageInstaller.Kind.Plugin, null, out _);

        Assert.Equal("tool", Path.GetFileName(target));
        Assert.Equal("checked bytes", File.ReadAllText(Path.Combine(target, "tool.dll")));
        Assert.False(Directory.Exists(Path.Combine(Plugins, "homeassistant")));
    }

    [Fact]
    public void The_copy_is_taken_while_writers_are_kept_out_and_a_missing_file_is_a_friendly_error()
    {
        var zipPath = Zip("held", ("held/held.dll", "x"));
        using (var writer = new FileStream(zipPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            // A writer that is already there is not a reason to read a half-written file silently; either the copy works or it says "pick a zip".
            try
            {
                using var copy = PackageInstaller.ReadCapped(zipPath);
            }
            catch (ArgumentException ex)
            {
                Assert.Equal(Strings.Get("package.pickZip"), ex.Message);
            }
        }
        Assert.Equal(Strings.Get("package.pickZip"), Assert.Throws<ArgumentException>(() => PackageInstaller.ReadCapped(Path.Combine(_root, "missing.zip"))).Message);
        Assert.Equal(Strings.Get("package.pickZip"), Assert.Throws<ArgumentException>(() => PackageInstaller.ReadCapped(Path.Combine(_root, "other.txt"))).Message);
    }

    [Fact]
    public void A_package_with_path_tricks_is_refused()
    {
        Directory.CreateDirectory(Plugins);
        var manager = new PluginManager([(Plugins, true)], DataDir, _ => true) { Trust = new PluginTrust(DataDir) };
        var zip = Zip("evil", ("evil/evil.dll", "x"), ("evil/../../escape.dll", "x"), ("evil/plugin.json", "{}"));
        Assert.Throws<InvalidOperationException>(() => manager.RefuseLocalPackage(zip, []));
        Assert.False(File.Exists(Path.Combine(DataDir, "escape.dll")));
    }

    [Fact]
    public void An_unreadable_trust_list_refuses_with_the_trust_text_and_installs_nothing()
    {
        Directory.CreateDirectory(Plugins);
        var trust = new PluginTrust(DataDir);
        Folder(trust, "existing", "existing", "existing", PluginOrigin.Local);
        var manager = new PluginManager([(Plugins, true)], DataDir, _ => true) { Trust = trust };
        foreach (var f in Directory.EnumerateFiles(DataDir, "plugin-trust*")) File.WriteAllText(f, "not a trust list");
        Assert.False(trust.Readable);
        var zip = Zip("newcomer", ("newcomer/newcomer.dll", "x"));
        var before = Directory.EnumerateFileSystemEntries(Plugins).Order().ToList();

        var ex = Assert.Throws<InvalidOperationException>(() => manager.RefuseLocalPackage(zip, []));

        Assert.Equal(Strings.Get("plugin.trustFault"), ex.Message);
        Assert.Equal(before, Directory.EnumerateFileSystemEntries(Plugins).Order().ToList());
    }

    [Fact]
    public void A_staged_update_for_the_folder_and_a_pending_removal_refuse_with_their_own_texts()
    {
        Directory.CreateDirectory(Plugins);
        var trust = new PluginTrust(DataDir);
        Folder(trust, "other", "other", "other", PluginOrigin.Local);
        trust.StagePending("staged", new PluginTrustEntry { Hash = new string('a', 64), Origin = PluginOrigin.ThirdParty, PluginId = "staged", Version = "2.0.0" });
        var manager = new PluginManager([(Plugins, true)], DataDir, _ => true) { Trust = trust };
        var zip = Zip("staged", ("staged/staged.dll", "x"));
        Assert.Equal(Strings.Get("plugin.local.signedInstalled", "staged"), Assert.Throws<InvalidOperationException>(() => manager.RefuseLocalPackage(zip, [])).Message);

        Folder(trust, "going", "going", "going", PluginOrigin.ThirdParty);
        trust.MarkRemoved("going");
        var zip2 = Zip("going", ("going/going.dll", "x"));
        Assert.Equal(Strings.Get("plugin.local.signedRemoving", "going"), Assert.Throws<InvalidOperationException>(() => manager.RefuseLocalPackage(zip2, [])).Message);
    }
}
