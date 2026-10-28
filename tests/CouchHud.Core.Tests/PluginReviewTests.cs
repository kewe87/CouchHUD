using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CouchHud.Core.Localization;
using CouchHud.Core.Plugins;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>
/// The review of the plugin system before 0.6: updates through the real install path keep the trust store sound and are bound to it, no key
/// means a fault, removal marks, host assemblies, row filtering, link rows, the image limits and the gate for approving a local folder.
/// All keys are throwaway keys made here (the trust store's key is the test key of <see cref="TestTrustKey"/>).
/// </summary>
public sealed class PluginReviewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-review-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _own = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _dev = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly HttpClient _http = new();

    public PluginReviewTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _own.Dispose();
        _dev.Dispose();
        _http.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string Pem(ECDsa key) => key.ExportSubjectPublicKeyInfoPem();

    private VerifiedStatement Statement(string version, string sha)
    {
        var json = new JsonObject
        {
            ["id"] = "tool", ["folder"] = "tool", ["version"] = version, ["sha256"] = sha, ["sdk"] = 3,
            ["capabilities"] = new JsonArray(), ["publisherKey"] = PackageStatements.Fingerprint(Pem(_dev)), ["license"] = "MIT"
        };
        var bytes = Encoding.UTF8.GetBytes(json.ToJsonString());
        var sig = Convert.ToBase64String(_dev.SignData(CatalogSignature.WithContext(CatalogSignature.PluginContext, bytes), HashAlgorithmName.SHA256));
        return PackageStatements.Verify(new SignedStatement(bytes, sig, Pem(_dev)), Pem(_own));
    }

    private static CatalogEntry Entry(string version, string sha) => CatalogParser.Parse($$"""
        [{"id":"tool","folder":"tool","name":"Tool","type":"plugin","version":"{{version}}","download":"https://x.example/t.zip","sha256":"{{sha}}"}]
        """, "s")[0];

    private string Zip(string content)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var w = new StreamWriter(zip.CreateEntry("plugin.json").Open())) w.Write("{}");
        // Any .NET assembly that references only the SDK and the base library will do: the SDK itself.
        using (var s = zip.CreateEntry("tool.dll").Open()) s.Write(File.ReadAllBytes(typeof(PluginRow).Assembly.Location));
        using (var w = new StreamWriter(zip.CreateEntry("readme.txt").Open())) w.Write(content);
        return path;
    }

    private static string Sha(string file) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));

    private string PluginsDir => Path.Combine(_dir, "plugins");

    private CatalogService Service() => new(_http, _dir, () => [], () => false);

    /// <summary>Version 1.0.0 installed through the catalog's install path, then 1.1.0 staged over it the same way.</summary>
    private (string Zip, string Sha) InstalledThenStaged()
    {
        var first = Zip("v1");
        Service().Install(Entry("1.0.0", Sha(first)), first, Statement("1.0.0", Sha(first)));
        var update = Zip("v2");
        var outcome = Service().Install(Entry("1.1.0", Sha(update)), update, Statement("1.1.0", Sha(update)));
        Assert.True(outcome.RestartNeeded);
        return (update, Sha(update));
    }

    // ---- updates

    [Fact]
    public void An_update_through_the_install_path_keeps_the_trust_store_sound_and_goes_in_at_the_next_start()
    {
        var (_, sha) = InstalledThenStaged();

        var trust = new PluginTrust(_dir);
        Assert.False(trust.IsFaulty);
        Assert.Equal(sha, trust.PendingFor("tool")!.Hash);
        Assert.Equal(PluginTrustState.Trusted, trust.Check("tool", Path.Combine(PluginsDir, "tool")));

        Assert.Equal(["plugin:tool"], PendingPackages.ApplyAll(_dir));
        var after = new PluginTrust(_dir);
        Assert.False(after.IsFaulty);
        Assert.Equal("1.1.0", after.Entry("tool")!.Version);
        Assert.Null(after.PendingFor("tool"));
        Assert.Equal("v2", File.ReadAllText(Path.Combine(PluginsDir, "tool", "readme.txt")));
        Assert.Equal(PluginTrustState.Trusted, after.Check("tool", Path.Combine(PluginsDir, "tool")));
    }

    [Fact]
    public void An_unsigned_catalog_update_is_staged_with_its_hash_as_well()
    {
        var first = Zip("v1");
        Service().Install(Entry("1.0.0", Sha(first)), first);
        var update = Zip("v2");
        Service().Install(Entry("1.1.0", Sha(update)), update);

        Assert.Equal(Sha(update), new PluginTrust(_dir).PendingFor("tool")!.Hash);
        Assert.Equal(["plugin:tool"], PendingPackages.ApplyAll(_dir));
        Assert.Equal(PluginTrustState.Trusted, new PluginTrust(_dir).Check("tool", Path.Combine(PluginsDir, "tool")));
    }

    [Fact]
    public void A_swapped_staged_package_is_refused_even_with_its_hash_written_into_the_registry()
    {
        InstalledThenStaged();
        var evil = Zip("evil");
        File.Copy(evil, Path.Combine(PendingPackages.Folder(_dir), "plugin-tool.zip"), overwrite: true);
        var registry = new CatalogRegistry(_dir);
        registry.Set("plugin:tool", registry.Read()["plugin:tool"] with { Sha256 = Sha(evil) });

        Assert.Empty(PendingPackages.ApplyAll(_dir));
        Assert.Equal("v1", File.ReadAllText(Path.Combine(PluginsDir, "tool", "readme.txt")));
    }

    [Fact]
    public void A_staged_package_without_an_entry_in_the_trust_store_is_refused()
    {
        var injected = Zip("evil");
        Directory.CreateDirectory(PendingPackages.Folder(_dir));
        File.Copy(injected, Path.Combine(PendingPackages.Folder(_dir), "plugin-evil.zip"));
        new CatalogRegistry(_dir).Set("plugin:evil", new CatalogRegistry.Record("1.0.0", "Evil", true, null, Sha(injected)));

        Assert.Empty(PendingPackages.ApplyAll(_dir));
        Assert.False(Directory.Exists(Path.Combine(PluginsDir, "Evil")));
    }

    [Fact]
    public void A_staged_entry_needs_the_package_hash()
    {
        var trust = new PluginTrust(_dir);
        Assert.Throws<ArgumentException>(() => trust.StagePending("tool", new PluginTrustEntry { Hash = "" }));
        Assert.Throws<ArgumentException>(() => trust.StagePending("tool", new PluginTrustEntry { Hash = "abc" }));
        Assert.False(trust.IsFaulty);
    }

    [Fact]
    public void A_damaged_trust_store_refuses_to_verify_without_throwing_anything_else()
    {
        var first = Zip("v1");
        Service().Install(Entry("1.0.0", Sha(first)), first);
        var file = Path.Combine(_dir, "plugin-trust.json");
        File.WriteAllText(file, File.ReadAllText(file).Replace("tool", "toal"));

        Assert.True(new PluginTrust(_dir).IsFaulty);
        Assert.Equal(Strings.Get("plugin.trustFault"), Assert.Throws<CatalogException>(() => Service().VerifyEntry(Entry("1.1.0", Sha(first)))).Message);
        Assert.Throws<CatalogException>(() => new PluginTrust(_dir).ForId("tool"));
    }

    // ---- no key, no store

    [Fact]
    public void Without_the_key_an_existing_store_is_a_fault_and_nothing_is_written()
    {
        var dir = Path.Combine(PluginsDir, "tool");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "tool.dll"), "x");
        new PluginTrust(_dir).Trust("tool", dir);
        var file = Path.Combine(_dir, "plugin-trust.json");
        var before = File.ReadAllBytes(file);

        var keyless = new PluginTrust(_dir, []);
        Assert.True(keyless.IsFaulty);
        Assert.Equal(PluginTrustState.Fault, keyless.Check("tool", dir));
        Assert.ThrowsAny<InvalidOperationException>(() => keyless.Trust("tool", dir));
        Assert.ThrowsAny<InvalidOperationException>(() => keyless.MarkRemoved("tool"));
        Assert.False(keyless.AdoptExisting(PluginsDir));
        Assert.Equal(before, File.ReadAllBytes(file));
        Assert.Equal(PluginTrustState.Trusted, new PluginTrust(_dir).Check("tool", dir));
    }

    [Fact]
    public void Without_the_key_nothing_is_adopted_into_a_new_store()
    {
        Directory.CreateDirectory(Path.Combine(PluginsDir, "tool"));
        Assert.False(new PluginTrust(_dir, []).AdoptExisting(PluginsDir));
        Assert.False(File.Exists(Path.Combine(_dir, "plugin-trust.json")));
    }

    // ---- removal marks

    [Fact]
    public void A_plugin_that_cannot_be_removed_now_is_marked_never_loads_and_goes_at_the_next_start()
    {
        var first = Zip("v1");
        Service().Install(Entry("1.0.0", Sha(first)), first);
        var dir = Path.Combine(PluginsDir, "tool");
        using (new FileStream(Path.Combine(dir, "readme.txt"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.False(Service().UninstallPlugin("tool"));
        }
        var trust = new PluginTrust(_dir);
        Assert.False(trust.IsFaulty);
        Assert.True(trust.IsRemoved("tool"));
        Assert.Null(trust.Entry("tool"));
        // Never offered as an unknown folder to approve: it does not even load.
        var manager = new PluginManager([(PluginsDir, true)], _dir, _ => true) { Trust = trust };
        manager.LoadAll();
        Assert.DoesNotContain(manager.Plugins, p => p.Folder == "tool");

        CatalogService.FinishRemovals(_dir);
        Assert.False(Directory.Exists(dir));
        Assert.False(new PluginTrust(_dir).IsRemoved("tool"));
        Assert.False(new PluginTrust(_dir).IsFaulty);
    }

    [Fact]
    public void A_removal_mark_is_under_the_signature()
    {
        var trust = new PluginTrust(_dir);
        trust.MarkRemoved("tool");
        var file = Path.Combine(_dir, "plugin-trust.json");
        File.WriteAllText(file, File.ReadAllText(file).Replace("\"tool\"", "\"tooo\""));
        Assert.True(new PluginTrust(_dir).IsFaulty);
    }

    [Fact]
    public void A_reinstall_after_a_removal_clears_the_mark()
    {
        var trust = new PluginTrust(_dir);
        trust.MarkRemoved("tool");
        var zip = Zip("v1");
        Service().Install(Entry("1.0.0", Sha(zip)), zip, Statement("1.0.0", Sha(zip)));
        // The folder was gone, but the mark was still there: staged, and the next start removes and then installs.
        CatalogService.FinishRemovals(_dir);
        PendingPackages.ApplyAll(_dir);
        var after = new PluginTrust(_dir);
        Assert.False(after.IsRemoved("tool"));
        Assert.Equal(PluginTrustState.Trusted, after.Check("tool", Path.Combine(PluginsDir, "tool")));
    }

    // ---- host assemblies

    [Fact]
    public void No_package_may_reference_the_host()
    {
        foreach (var origin in Enum.GetValues<PluginOrigin>())
        {
            Assert.Equal("plugin.lint.hostAssembly", PluginLint.CheckReference("CouchHud.Core", origin, new HashSet<string>()));
            Assert.Equal("plugin.lint.hostAssembly", PluginLint.CheckReference("CouchHud.Shell", origin, new HashSet<string>(["CouchHud.Shell"])));
        }
        Assert.Null(PluginLint.CheckReference("CouchHud.Sdk", PluginOrigin.ThirdParty, new HashSet<string>()));
        Assert.Null(PluginLint.CheckReference("System.Text.Json", PluginOrigin.ThirdParty, new HashSet<string>()));
        Assert.Null(PluginLint.CheckReference("Helper", PluginOrigin.ThirdParty, new HashSet<string>(["Helper"], StringComparer.OrdinalIgnoreCase)));
        Assert.Equal("plugin.lint.assemblyRef", PluginLint.CheckReference("Newtonsoft.Json", PluginOrigin.ThirdParty, new HashSet<string>()));
        Assert.Null(PluginLint.CheckReference("Newtonsoft.Json", PluginOrigin.FirstParty, new HashSet<string>()));
    }

    [Fact]
    public void A_package_whose_code_references_the_host_is_refused()
    {
        // This test assembly references CouchHud.Core.
        var image = File.ReadAllBytes(typeof(PluginReviewTests).Assembly.Location);
        Assert.Contains("CouchHud.Core", PluginLint.References(image));
        var path = Path.Combine(_dir, "host.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        using (var s = zip.CreateEntry("Tool.dll").Open()) s.Write(image);
        var all = new[] { PluginCapabilities.Process, PluginCapabilities.Registry, PluginCapabilities.NetworkInternet, PluginCapabilities.DynamicCode, PluginCapabilities.Native };
        var ex = Assert.Throws<CatalogException>(() => PluginLint.CheckZip(path, PluginOrigin.FirstParty, all));
        Assert.Contains("CouchHud.Core", ex.Message);
    }

    [Fact]
    public void The_host_assemblies_are_the_ones_CouchHUD_ships()
    {
        Assert.Contains("CouchHud.Core", PluginManager.HostAssemblies);
        Assert.Contains("CouchHud.Shell", PluginManager.HostAssemblies);
        Assert.DoesNotContain("CouchHud.Sdk", PluginManager.HostAssemblies);
    }

    // ---- rows

    private static byte[] Png(uint width, uint height, int size = 64)
    {
        var b = new byte[size];
        byte[] magic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        magic.CopyTo(b, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), 13);
        "IHDR"u8.CopyTo(b.AsSpan(12));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), height);
        return b;
    }

    [Fact]
    public void Row_images_are_small_pngs_only()
    {
        Assert.StartsWith("data:image/png;base64,", PluginRows.ImageUri(Png(64, 64)));
        Assert.Null(PluginRows.ImageUri(Encoding.ASCII.GetBytes("<svg onload=alert(1)>")));
        Assert.Null(PluginRows.ImageUri(Png(64, 64, PluginRow.MaxImageBytes + 1)));
        Assert.Null(PluginRows.ImageUri(Png(5000, 10)));
        Assert.Null(PluginRows.ImageUri(Png(0, 10)));
        Assert.NotNull(PluginRows.ImageUri(Png(4096, 4096)));
        Assert.Null(PluginRows.ImageUri(Png(4097, 1)));
        Assert.Null(PluginRows.ImageUri([]));
    }

    [Fact]
    public void Rows_are_bounded_in_number_and_length()
    {
        var rows = Enumerable.Range(0, PluginRows.MaxRows + 50).Select(i => new PluginRow("r" + i, new string('x', 5000)) { Detail = new string('d', 5000), Badge = new string('b', 100) })
            .Append(new PluginRow("", "no id")).Append(new PluginRow(new string('i', PluginRows.MaxId + 1), "long id")).ToList();
        var views = PluginRows.Filter(rows);
        Assert.Equal(PluginRows.MaxRows, views.Count);
        Assert.All(views, v =>
        {
            Assert.Equal(PluginRows.MaxLabel, v.Label.Length);
            Assert.Equal(PluginRows.MaxDetail, v.Detail!.Length);
            Assert.Equal(PluginRows.MaxMark, v.Badge!.Length);
        });
        Assert.Empty(PluginRows.Filter([new PluginRow("", "x"), new PluginRow(new string('i', PluginRows.MaxId + 1), "x")]));
        var choice = new PluginRow("c", "C") { Kind = new PluginRowKind.Choice(Enumerable.Range(0, 100).Select(i => new PluginChoiceOption("o" + i, "O")).ToList()) };
        Assert.Equal(PluginRows.MaxOptions, PluginRows.Filter([choice])[0].Options!.Count);
    }

    [Fact]
    public void A_changed_value_must_be_one_the_row_offers()
    {
        var choice = new PluginRow("c", "C") { Kind = new PluginRowKind.Choice([new("de", "DE"), new("us", "US")]) };
        Assert.Equal("de", PluginRows.CheckValue(choice, "de"));
        Assert.Null(PluginRows.CheckValue(choice, "ru"));
        Assert.Null(PluginRows.CheckValue(choice, null));

        var stepper = new PluginRow("s", "S") { Kind = new PluginRowKind.Stepper(0, 100, 5) };
        Assert.Equal("35", PluginRows.CheckValue(stepper, "35"));
        Assert.Equal("100", PluginRows.CheckValue(stepper, "100.0"));
        Assert.Null(PluginRows.CheckValue(stepper, "36"));
        Assert.Null(PluginRows.CheckValue(stepper, "105"));
        Assert.Null(PluginRows.CheckValue(stepper, "-5"));
        Assert.Null(PluginRows.CheckValue(stepper, "NaN"));
        Assert.Null(PluginRows.CheckValue(stepper, "Infinity"));
        Assert.Null(PluginRows.CheckValue(stepper, "1e400"));
        Assert.Null(PluginRows.CheckValue(new PluginRow("b", "B") { Kind = new PluginRowKind.Stepper(10, 0, 1) }, "5"));
        Assert.Null(PluginRows.CheckValue(new PluginRow("z", "Z") { Kind = new PluginRowKind.Stepper(0, 10, 0) }, "5"));
        Assert.Null(PluginRows.CheckValue(new PluginRow("t", "T") { Kind = PluginRowKind.Toggle }, "1"));
        Assert.Null(PluginRows.CheckValue(new PluginRow("a", "A"), "1"));
    }

    // ---- link rows: opened by the host, only to a page of the same plugin

    [Fact]
    public void A_link_row_leads_only_to_an_existing_page_of_the_same_plugin()
    {
        var pages = new HashSet<string> { "main", "other" };
        Assert.Equal("other", PluginRows.LinkTarget(new PluginRow("l", "L") { Kind = new PluginRowKind.Link("other") }, pages.Contains));
        Assert.Null(PluginRows.LinkTarget(new PluginRow("l", "L") { Kind = new PluginRowKind.Link("missing") }, pages.Contains));
        Assert.Null(PluginRows.LinkTarget(new PluginRow("l", "L") { Kind = new PluginRowKind.Link("") }, pages.Contains));
        Assert.Null(PluginRows.LinkTarget(new PluginRow("l", "L"), pages.Contains));
    }

    [Fact]
    public void A_third_party_link_row_is_refused_on_the_locked_couch_and_needs_no_code_under_parental_controls()
    {
        var link = new PluginRow("l", "L") { Kind = new PluginRowKind.Link("other") };
        Assert.Equal("lock.unlockFirst", PluginPolicy.MayPress(true, PluginOrigin.ThirdParty, link, false, "tool").Refused);
        Assert.Equal("lock.unlockFirst", PluginPolicy.MayPress(true, PluginOrigin.ThirdParty, link with { AllowedWhileLocked = true }, true, "tool").Refused);
        var parental = PluginPolicy.MayPress(false, PluginOrigin.ThirdParty, link, true, "tool");
        Assert.True(parental.Allowed);
        Assert.False(parental.ParentCode);
        // The page it leads to is checked on its own: a page not marked for the locked couch stays closed.
        Assert.False(PluginPolicy.MayView(true, new PluginPage { Id = "other", Title = "O", Rows = _ => Task.FromResult<IReadOnlyList<PluginRow>>([]) }));
    }

    // ---- media cache

    [Fact]
    public async Task Media_with_huge_dimensions_is_not_cached()
    {
        foreach (var png in new[] { Png(4097, 10), Png(10, 4097), Png(4096, 4097), Png(0, 1), Png(uint.MaxValue, 1) })
        {
            Assert.Null(CatalogMediaCache.PngSize(png) is { } s && s.Width <= CatalogMediaCache.MaxSide && s.Height <= CatalogMediaCache.MaxSide ? "small" : null);
            var cache = new CatalogMediaCache(_dir, (_, _, _) => Task.FromResult<byte[]?>(png));
            Assert.Null(await cache.GetAsync("https://x.example/i.png", Convert.ToHexStringLower(SHA256.HashData(png)), PackageMedia.MaxIconBytes));
        }
        Assert.Equal((4096, 4096), CatalogMediaCache.PngSize(Png(4096, 4096)));
        // 4096 x 4096 is exactly 16 MP: still allowed.
        var ok = Png(4096, 4096);
        var okCache = new CatalogMediaCache(_dir, (_, _, _) => Task.FromResult<byte[]?>(ok));
        Assert.NotNull(await okCache.GetAsync("https://x.example/ok.png", Convert.ToHexStringLower(SHA256.HashData(ok)), PackageMedia.MaxIconBytes));
    }

    // ---- Wand's statement (signed by the release owner with scripts/sign-plugin-statement.ps1)

    [Fact]
    public void Wands_statement_template_is_a_valid_first_party_statement_once_filled()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CouchHud.slnx"))) root = Path.GetDirectoryName(root)!;
        var template = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "catalog", "statements", "wand.json")))!.AsObject();
        template["sha256"] = new string('a', 64);
        template["publisherKey"] = new string('b', 64);
        template["size"] = 12345;
        var statement = PackageStatements.Parse(Encoding.UTF8.GetBytes(template.ToJsonString()));
        Assert.Equal("wand", statement.Id);
        Assert.Equal("CouchHud.Plugin.Wand", statement.Folder);
        Assert.Null(PluginPolicy.CheckDeclared(PluginOrigin.FirstParty, statement.Capabilities));
        // It starts programs (Wand itself), which the lint finds: declared, and nothing first-party-only.
        Assert.Contains(PluginCapabilities.Process, statement.Capabilities);
        Assert.DoesNotContain(statement.Capabilities, PluginPolicy.FirstPartyOnly.Contains);
    }

    [Fact]
    public void Wands_manifest_statement_and_index_entry_agree_and_ask_for_0_6()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "CouchHud.slnx"))) root = Path.GetDirectoryName(root)!;
        var statement = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "catalog", "statements", "wand.json")))!.AsObject();
        var manifest = PluginManager.ReadManifest(Path.Combine(root, "plugins", "CouchHud.Plugin.Wand"))!;
        Assert.Equal("wand", manifest.Id);
        Assert.Equal(statement["version"]!.GetValue<string>(), manifest.Version);
        Assert.Equal(statement["sdk"]!.GetValue<int>(), manifest.Sdk);
        Assert.Equal(statement["license"]!.GetValue<string>(), manifest.License);
        Assert.Equal(statement["capabilities"]!.AsArray().Select(c => c!.GetValue<string>()).Order(), manifest.Capabilities!.Order());
        // 1.0.0 is the package 0.5 clients download from the live index and must stay unchanged: the 0.6 package is a new version.
        Assert.True(SemVer.TryParse(manifest.Version, out var v) && SemVer.TryParse("1.0.0", out var old) && v > old);
        // The icon already online (icons/wand.png) is named in the signed statement (the entry's own iconSha256 only serves until it is signed).
        Assert.Equal(64, statement["icon"]!["sha256"]!.GetValue<string>().Length);
        Assert.True(statement["icon"]!["bytes"]!.GetValue<long>() is > 0 and <= PackageMedia.MaxIconBytes);
        // A 0.5 client reads minCouchHud from the shared index and then offers nobody the package it cannot load.
        var entry = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "catalog", "index.json")))!["entries"]!.AsArray().Single(e => e!["id"]!.GetValue<string>() == "wand")!;
        Assert.Equal("0.6.0", entry["minCouchHud"]!.GetValue<string>());
        Assert.Equal("0.6.0", statement["minCouchHud"]!.GetValue<string>());
        Assert.Equal(statement["icon"]!["sha256"]!.GetValue<string>(), entry["iconSha256"]!.GetValue<string>());
    }

    // ---- approving a local folder is as strict as installing an unsigned package

    private static readonly PluginPolicy.GateContext Open = new(PluginCaller.Hud, Locked: false, ChildProfile: false, ExpertMode: true, DeveloperSwitch: true, ParentalEnabled: false);

    [Fact]
    public void Approving_a_local_folder_needs_expert_mode_the_developer_switch_and_the_code()
    {
        Assert.Equal("plugin.gate.unsigned", PluginPolicy.Decide(Open with { DeveloperSwitch = false }, PluginChange.Approve, PluginOrigin.Local, []).Refused);
        Assert.Equal("plugin.gate.unsigned", PluginPolicy.Decide(Open with { ExpertMode = false }, PluginChange.Approve, PluginOrigin.Local, []).Refused);
        var open = PluginPolicy.Decide(Open, PluginChange.Approve, PluginOrigin.Local, []);
        Assert.True(open.Allowed);
        Assert.True(open.ParentCode);
    }
}
