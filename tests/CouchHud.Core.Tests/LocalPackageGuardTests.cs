using System.IO.Compression;
using CouchHud.Core.Localization;
using CouchHud.Core.Plugins;

namespace CouchHud.Core.Tests;

/// <summary>
/// An unsigned package (a local install) must never take over or break a plugin that is already there. The refusal comes before anything is
/// written, staged, trusted or registered; the plugin that was there stays exactly as it was.
/// </summary>
public class LocalPackageGuardTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("couchhud-guard-").FullName;

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
    private string Plugins => Path.Combine(DataDir, "plugins");
    private string Bundled => Path.Combine(_root, "bundled");

    private (PluginManager Manager, PluginTrust Trust) Setup()
    {
        Directory.CreateDirectory(Plugins);
        Directory.CreateDirectory(Bundled);
        var trust = new PluginTrust(DataDir);
        return (new PluginManager([(Bundled, false), (Plugins, true)], DataDir, _ => true) { Trust = trust }, trust);
    }

    /// <summary>A plugin that is installed: its files and its trust entry.</summary>
    private string Installed(PluginTrust trust, string folder, PluginOrigin origin, string id)
    {
        var dir = Path.Combine(Plugins, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, folder + ".dll"), "signed dll 1.0.0");
        File.WriteAllText(Path.Combine(dir, "plugin.json"), "{\"id\":\"" + id + "\",\"version\":\"1.0.0\"}");
        trust.Trust(folder, dir, new PluginTrustEntry { Hash = "", Origin = origin, PluginId = id, Version = "1.0.0", Capabilities = ["network:lan"] });
        return dir;
    }

    private string Zip(string name, string? manifest, string topFolder)
    {
        var path = Path.Combine(_root, name + ".zip");
        using (var stream = File.Create(path))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            void Add(string entry, string text)
            {
                using var w = new StreamWriter(zip.CreateEntry(topFolder + "/" + entry).Open());
                w.Write(text);
            }
            Add(topFolder + ".dll", "unsigned dll");
            if (manifest is not null) Add("plugin.json", manifest);
        }
        return path;
    }

    private static string Snapshot(string dir) =>
        string.Join("|", Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order().Select(f => Path.GetRelativePath(dir, f) + "=" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f)))));

    [Theory]
    [InlineData("homeassistant", "{\"id\":\"homeassistant\"}")]
    [InlineData("homeassistant", "{\"id\":\"homeassistant\",\"version\":\"1.1.0\"}")]
    [InlineData("Harmless", "{\"id\":\"homeassistant\",\"version\":\"1.1.0\"}")]
    [InlineData("HomeAssistant", null)]
    [InlineData("home-assistant", null)]
    public void An_unsigned_package_with_the_name_of_an_official_plugin_is_refused_and_the_official_one_is_untouched(string folder, string? manifest)
    {
        var (manager, trust) = Setup();
        var official = Installed(trust, "homeassistant", PluginOrigin.FirstParty, "homeassistant");
        var filesBefore = Snapshot(official);
        var trustBefore = trust.Entry("homeassistant");
        var zip = Zip(folder, manifest, topFolder: folder);
        var entriesBefore = Directory.EnumerateFileSystemEntries(Plugins).Order().ToList();

        var ex = Assert.Throws<InvalidOperationException>(() => manager.RefuseLocalPackage(zip, []));

        Assert.Contains("vorbehalten", ex.Message);
        Assert.Contains("blieb unberührt", ex.Message);
        Assert.Equal(filesBefore, Snapshot(official));
        var trustAfter = trust.Entry("homeassistant")!;
        Assert.Equal(trustBefore!.Hash, trustAfter.Hash);
        Assert.Equal(trustBefore.Origin, trustAfter.Origin);
        Assert.Equal(trustBefore.PluginId, trustAfter.PluginId);
        Assert.Equal(trustBefore.Version, trustAfter.Version);
        Assert.Equal(trustBefore.Capabilities, trustAfter.Capabilities);
        // Nothing staged, nothing new next to it, nothing trusted for the newcomer.
        Assert.Equal(entriesBefore, Directory.EnumerateFileSystemEntries(Plugins).Order().ToList());
        if (!folder.Equals("homeassistant", StringComparison.OrdinalIgnoreCase)) Assert.Null(trust.Entry(folder));
    }

    [Fact]
    public void A_name_from_the_signed_index_is_reserved_even_when_the_program_does_not_list_it()
    {
        var (manager, _) = Setup();
        var zip = Zip("freshofficial", "{\"id\":\"freshofficial\"}", topFolder: "freshofficial");
        var refused = Assert.Throws<InvalidOperationException>(() => manager.RefuseLocalPackage(zip, ["freshofficial"]));
        // Nothing of that name is installed: the text must not claim that an existing plugin was left untouched.
        Assert.Contains("vorbehalten", refused.Message);
        Assert.DoesNotContain("blieb unberührt", refused.Message);
        // Without the index entry it is an ordinary local package.
        manager.RefuseLocalPackage(zip, []);
    }

    [Fact]
    public void A_folder_installed_from_a_signature_is_never_replaced_by_an_unsigned_package_of_the_same_name()
    {
        var (manager, trust) = Setup();
        var third = Installed(trust, "coolplugin", PluginOrigin.ThirdParty, "coolplugin");
        var before = Snapshot(third);
        var zip = Zip("coolplugin", "{\"id\":\"coolplugin\"}", topFolder: "coolplugin");

        var ex = Assert.Throws<InvalidOperationException>(() => manager.RefuseLocalPackage(zip, []));

        Assert.Equal(Strings.Get("plugin.local.signedInstalled", "coolplugin"), ex.Message);
        Assert.Equal(before, Snapshot(third));
        Assert.Equal(PluginOrigin.ThirdParty, trust.Entry("coolplugin")!.Origin);
    }

    [Fact]
    public void A_folder_that_ships_with_the_program_is_not_replaced()
    {
        var (manager, trust) = Setup();
        Installed(trust, "other", PluginOrigin.Local, "other");
        Directory.CreateDirectory(Path.Combine(Bundled, "shipped"));
        var zip = Zip("shipped", null, topFolder: "shipped");
        Assert.Throws<InvalidOperationException>(() => manager.RefuseLocalPackage(zip, []));
    }

    [Fact]
    public void An_id_another_installed_plugin_holds_is_refused_before_the_other_one_is_disturbed()
    {
        var (manager, trust) = Setup();
        var local = Installed(trust, "mine", PluginOrigin.Local, "mine-id");
        var before = Snapshot(local);
        var zip = Zip("another", "{\"id\":\"mine-id\"}", topFolder: "another");

        var ex = Assert.Throws<InvalidOperationException>(() => manager.RefuseLocalPackage(zip, []));

        Assert.Equal(Strings.Get("plugin.local.idTaken", "mine"), ex.Message);
        Assert.Equal(before, Snapshot(local));
    }

    [Fact]
    public void A_first_party_capability_in_an_unsigned_package_is_refused_with_its_name()
    {
        var (manager, _) = Setup();
        var zip = Zip("sneaky", "{\"id\":\"sneaky\",\"capabilities\":[\"browser.signin\",\"setup.run\",\"network:lan\"]}", topFolder: "sneaky");

        var ex = Assert.Throws<InvalidOperationException>(() => manager.RefuseLocalPackage(zip, []));

        Assert.Contains("browser.signin", ex.Message);
        Assert.DoesNotContain("network:lan", ex.Message);
        Assert.Contains(Strings.Get("plugin.cap.firstPartyOnly"), ex.Message);
    }

    [Fact]
    public void A_refused_capability_is_named_in_both_languages()
    {
        try
        {
            Strings.Use("en");
            var message = PluginPolicy.DeclaredMessage(PluginOrigin.Local, ["browser.signin"]);
            Assert.Equal("This capability is only for CouchHUD's own plugins: browser.signin", message);
        }
        finally
        {
            Strings.Use("de");
        }
        Assert.Equal("Diese Berechtigung gibt es nur für CouchHUDs eigene Plugins: browser.signin", PluginPolicy.DeclaredMessage(PluginOrigin.Local, ["browser.signin"]));
    }

    [Fact]
    public void An_unknown_capability_is_named_too()
    {
        Assert.Equal(Strings.Get("plugin.cap.unknown") + ": filesystem.everything", PluginPolicy.DeclaredMessage(PluginOrigin.Local, ["network:lan", "filesystem.everything"]));
        Assert.Null(PluginPolicy.DeclaredMessage(PluginOrigin.Local, ["network:lan"]));
        Assert.Null(PluginPolicy.DeclaredMessage(PluginOrigin.FirstParty, [CouchHud.Sdk.PluginCapabilities.BrowserSignIn]));
    }

    [Theory]
    [InlineData("Spiele-Überblick")]
    [InlineData("my-little-tool")]
    public void An_ordinary_local_package_is_still_accepted(string folder)
    {
        var (manager, _) = Setup();
        var zip = Zip("ordinary", "{\"id\":\"my-little-tool\",\"version\":\"1.0.0\"}", topFolder: folder);
        manager.RefuseLocalPackage(zip, ["homeassistant"]);
    }

    private static string HudSource()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "CouchHud.slnx"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!, "src", "CouchHud.Shell", "Hud.cs"));
    }

    [Fact]
    public void The_host_asks_before_it_installs_and_before_the_code_is_used_up()
    {
        var hud = HudSource();
        var start = hud.IndexOf("public string InstallPackage(", StringComparison.Ordinal);
        var body = hud[start..hud.IndexOf("public object ApprovePlugin(", start, StringComparison.Ordinal)];
        var guard = body.IndexOf("plugins.RefuseLocalPackage(", StringComparison.Ordinal);
        Assert.True(guard > 0);
        Assert.True(guard < body.IndexOf("UseUp(ticket)", StringComparison.Ordinal));
        Assert.True(guard < body.IndexOf("PackageInstaller.Install(", StringComparison.Ordinal));
    }

    [Fact]
    public void The_plugin_list_hands_the_page_the_paused_state()
    {
        var hud = HudSource();
        var start = hud.IndexOf("public object GetPlugins()", StringComparison.Ordinal);
        var body = hud[start..hud.IndexOf("/// <summary>", start + 20, StringComparison.Ordinal)];
        Assert.Contains("p.PausedOffline", body);
        Assert.Contains("p.StartsAfterRestart", body);
    }
}
