using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using CouchHud.Core.Plugins;

namespace CouchHud.Core.Tests;

[Collection("Language")]
public class CatalogSecurityTests : IDisposable
{
    private static readonly SemVer App = new(0, 3, 0, "");
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-sec-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public CatalogSecurityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _key.Dispose();
        _otherKey.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private string Pem => _key.ExportSubjectPublicKeyInfoPem();

    private static string Sign(ECDsa key, string text, string context = CatalogSignature.CatalogContext) =>
        Convert.ToBase64String(key.SignData(CatalogSignature.WithContext(context, Encoding.UTF8.GetBytes(text)), HashAlgorithmName.SHA256));

    [Fact]
    public void A_signature_only_counts_in_the_context_it_was_made_for()
    {
        const string text = "same bytes";
        var data = Encoding.UTF8.GetBytes(text);
        Assert.True(CatalogSignature.Verify(data, Sign(_key, text), Pem));
        Assert.True(CatalogSignature.Verify(data, Sign(_key, text, CatalogSignature.ReleaseContext), Pem, CatalogSignature.ReleaseContext));
        // A release signature is no catalog signature and the other way round.
        Assert.False(CatalogSignature.Verify(data, Sign(_key, text, CatalogSignature.ReleaseContext), Pem));
        Assert.False(CatalogSignature.Verify(data, Sign(_key, text), Pem, CatalogSignature.ReleaseContext));
        // And a signature over the bare bytes (the old format) is rejected.
        var bare = Convert.ToBase64String(_key.SignData(data, HashAlgorithmName.SHA256));
        Assert.False(CatalogSignature.Verify(data, bare, Pem));
        Assert.False(CatalogSignature.Verify(data, bare, Pem, CatalogSignature.ReleaseContext));
    }

    private static string Entry(string id = "wand", string folder = "wand", string version = "1.0.0", string type = "plugin") =>
        $$"""{ "id": "{{id}}", "folder": "{{folder}}", "name": "{{id}}", "type": "{{type}}", "version": "{{version}}", "download": "https://example.com/{{id}}.zip", "sha256": "{{Hash}}" }""";

    private static string Index(params string[] entries) => "{ \"entries\": [" + string.Join(",", entries) + "] }";

    private sealed class FakeHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request.RequestUri!));
    }

    private static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };

    private CatalogService Service(Func<Uri, HttpResponseMessage> respond, params string[] sources) =>
        new(new HttpClient(new FakeHandler(respond)), _dir, () => sources, () => false, "de", Pem);

    private string MakeZip(string name, params (string Path, string Content)[] files)
    {
        var path = Path.Combine(_dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (p, c) in files)
        {
            using var w = new StreamWriter(zip.CreateEntry(p).Open());
            w.Write(c);
        }
        return path;
    }

    // ---- signature

    [Fact]
    public void Signature_is_valid_only_for_the_exact_bytes_and_the_right_key()
    {
        var text = Index(Entry());
        var sig = Sign(_key, text);
        var data = Encoding.UTF8.GetBytes(text);

        Assert.True(CatalogSignature.Verify(data, sig, Pem));
        Assert.False(CatalogSignature.Verify(Encoding.UTF8.GetBytes(text + " "), sig, Pem));
        Assert.False(CatalogSignature.Verify(data, Sign(_otherKey, text), Pem));
        Assert.False(CatalogSignature.Verify(data, null, Pem));
        Assert.False(CatalogSignature.Verify(data, "", Pem));
        Assert.False(CatalogSignature.Verify(data, "not base64!!", Pem));
        Assert.False(CatalogSignature.Verify(data, Convert.ToBase64String(new byte[64]), Pem));
        Assert.False(CatalogSignature.Verify(data, sig, "garbage"));
    }

    [Fact]
    public void The_built_in_public_key_is_a_valid_P256_key()
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(CatalogSignature.PublicKeyPem);
        Assert.Equal(256, key.KeySize);
    }

    [Fact]
    public async Task Default_source_with_a_valid_signature_is_trusted()
    {
        var text = Index(Entry());
        var service = Service(u => u.AbsolutePath.EndsWith(".sig") ? Ok(Sign(_key, text)) : Ok(text), CatalogDefaults.IndexUrl);
        var result = await service.LoadAsync();
        var entry = Assert.Single(result.Entries);
        Assert.False(entry.Unsigned);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrongkey")]
    [InlineData("tampered")]
    public async Task Default_source_without_a_valid_signature_offers_nothing(string case_)
    {
        var text = Index(Entry());
        var service = Service(u =>
        {
            if (u.AbsolutePath.EndsWith(".sig")) return case_ == "missing" ? new HttpResponseMessage(HttpStatusCode.NotFound) : Ok(Sign(case_ == "wrongkey" ? _otherKey : _key, text));
            return Ok(case_ == "tampered" ? text.Replace("wand", "evil") : text);
        }, CatalogDefaults.IndexUrl);

        var result = await service.LoadAsync();

        Assert.Empty(result.Entries);
        Assert.Contains("vertrauenswürdig", result.Error);
    }

    [Fact]
    public async Task Tampered_default_index_falls_back_to_the_signed_cache()
    {
        var text = Index(Entry());
        var tampered = false;
        var service = Service(u => u.AbsolutePath.EndsWith(".sig") ? Ok(Sign(_key, text)) : Ok(tampered ? Index(Entry(id: "evil", folder: "evil")) : text), CatalogDefaults.IndexUrl);
        await service.LoadAsync();
        tampered = true;

        var result = await service.LoadAsync();

        Assert.True(result.FromCache);
        Assert.Equal("wand", Assert.Single(result.Entries).Id);
        Assert.Contains("vertrauenswürdig", result.Error);
    }

    [Fact]
    public async Task User_sources_may_be_unsigned_but_are_marked()
    {
        var text = Index(Entry());
        var unsigned = await Service(u => u.AbsolutePath.EndsWith(".sig") ? new HttpResponseMessage(HttpStatusCode.NotFound) : Ok(text), "https://example.com/i.json").LoadAsync();
        Assert.True(Assert.Single(unsigned.Entries).Unsigned);

        var signed = await Service(u => u.AbsolutePath.EndsWith(".sig") ? Ok(Sign(_key, text)) : Ok(text), "https://example.com/i.json").LoadAsync();
        Assert.False(Assert.Single(signed.Entries).Unsigned);

        var wrong = await Service(u => u.AbsolutePath.EndsWith(".sig") ? Ok(Sign(_otherKey, text)) : Ok(text), "https://example.com/i.json").LoadAsync();
        Assert.True(Assert.Single(wrong.Entries).Unsigned);
    }

    // ---- dedupe, origin

    [Fact]
    public async Task Same_folder_under_another_id_is_dropped_and_same_id_with_another_folder_is_a_collision()
    {
        var a = Index(Entry(id: "wand", folder: "wand"));
        var b = Index(Entry(id: "other", folder: "wand"), Entry(id: "wand", folder: "elsewhere"), Entry(id: "fresh", folder: "fresh"));
        var service = Service(u => Ok(u.Host == "a.example" ? a : b), "https://a.example/i.json", "https://b.example/i.json");

        var result = await service.LoadAsync();

        Assert.DoesNotContain(result.Entries, e => e.Id == "other");
        Assert.Contains(result.Entries, e => e.Id == "fresh" && e.Problem is null);
        var collision = result.Entries.Single(e => e.Id == "wand" && e.Folder == "elsewhere");
        Assert.Contains("Kollision", collision.Problem);
        Assert.Null(collision.DownloadUrl);
        Assert.Equal("wand", result.Entries.First(e => e.Id == "wand").Folder);
        await Assert.ThrowsAsync<CatalogException>(() => service.InstallAsync(collision, App));
    }

    [Fact]
    public async Task Updates_only_come_from_the_source_the_package_was_installed_from()
    {
        var mine = Index(Entry(version: "1.0.0"));
        var theirs = Index(Entry(version: "2.0.0"));
        var service = Service(u => Ok(u.Host == "a.example" ? mine : theirs), "https://a.example/i.json", "https://b.example/i.json");
        var fromA = (await service.LoadAsync()).Entries[0];
        Directory.CreateDirectory(Path.Combine(_dir, "plugins", "wand"));
        new CatalogRegistry(_dir).Set(fromA, pending: false);

        var fromB = fromA with { Source = "https://b.example/i.json", Version = "2.0.0" };

        var ex = await Assert.ThrowsAsync<CatalogException>(() => service.InstallAsync(fromB, App));
        Assert.Contains("anderen Quelle", ex.Message);
        Assert.Null(CatalogRules.OriginProblem(fromA, true, "https://a.example/i.json"));
        Assert.NotNull(CatalogRules.OriginProblem(fromB, true, "https://a.example/i.json"));
        Assert.Null(CatalogRules.OriginProblem(fromB, false, null));
    }

    [Fact]
    public void Bundled_or_hand_installed_packages_can_only_be_updated_from_the_default_source()
    {
        var foreign = new CatalogEntry("wand", "wand", "", null, CatalogKind.Plugin, "9.0.0", null, "https://x/y.zip", Hash, null, null, "wand", "https://evil.example/i.json", null);
        Assert.NotNull(CatalogRules.OriginProblem(foreign, true, null));
        Assert.Null(CatalogRules.OriginProblem(foreign with { Source = CatalogDefaults.IndexUrl }, true, null));
    }

    // ---- bundled override

    private string PluginDir(string root, string folder, string? version, bool dll = true)
    {
        var dir = Path.Combine(_dir, root, folder);
        Directory.CreateDirectory(dir);
        if (dll) File.WriteAllText(Path.Combine(dir, folder + ".dll"), "MZ");
        if (version is not null) File.WriteAllText(Path.Combine(dir, "plugin.json"), $"{{ \"version\": \"{version}\" }}");
        return dir;
    }

    [Fact]
    public void A_bundled_plugins_folder_is_no_longer_loaded_even_when_its_files_are_trusted()
    {
        var dir = PluginDir("bundled", "Wand", "1.0.0");
        var trust = new PluginTrust(_dir);
        trust.Trust("Wand", dir);
        var manager = new PluginManager([(Path.Combine(_dir, "bundled"), false)], _dir, _ => true) { Trust = trust };

        manager.LoadAll();

        Assert.Empty(manager.Plugins);
    }

    // ---- atomic install

    [Fact]
    public void A_failing_extraction_leaves_the_installed_version_and_no_leftovers()
    {
        var root = Path.Combine(_dir, "plugins");
        var target = Path.Combine(root, "wand");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.dll"), "keep");
        // A file and a directory with the same name: extraction fails halfway through.
        var zip = MakeZip("clash.zip", ("wand/a.dll", "MZ"), ("wand/x.dll", "MZ"), ("wand/x.dll/y.txt", "boom"));

        Assert.ThrowsAny<IOException>(() => PackageInstaller.Install(zip, root, PackageInstaller.Kind.Plugin, "wand"));

        Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "keep.dll")));
        Assert.False(File.Exists(Path.Combine(target, "a.dll")));
        Assert.Equal([target], Directory.EnumerateDirectories(root));
    }

    [Fact]
    public void A_successful_install_replaces_the_old_version_and_cleans_up()
    {
        var root = Path.Combine(_dir, "plugins");
        var target = Path.Combine(root, "wand");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "old.dll"), "old");

        PackageInstaller.Install(MakeZip("ok.zip", ("wand/new.dll", "MZ")), root, PackageInstaller.Kind.Plugin, "wand");

        Assert.False(File.Exists(Path.Combine(target, "old.dll")));
        Assert.True(File.Exists(Path.Combine(target, "new.dll")));
        Assert.Equal([target], Directory.EnumerateDirectories(root));
    }

    [Fact]
    public void A_locked_old_version_stays_in_place_and_the_new_one_is_not_left_behind()
    {
        var root = Path.Combine(_dir, "plugins");
        var target = Path.Combine(root, "wand");
        Directory.CreateDirectory(target);
        var locked = Path.Combine(target, "loaded.dll");
        File.WriteAllText(locked, "old");
        using var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read);

        var ex = Assert.Throws<InvalidOperationException>(() => PackageInstaller.Install(MakeZip("n.zip", ("wand/new.dll", "MZ")), root, PackageInstaller.Kind.Plugin, "wand"));

        Assert.Contains("geladen", ex.Message);
        Assert.True(File.Exists(locked));
        Assert.Equal([target], Directory.EnumerateDirectories(root));
    }

    // ---- pending updates at startup

    [Fact]
    public void A_broken_pending_update_is_dropped_once_and_never_retried()
    {
        var registry = new CatalogRegistry(_dir);
        Directory.CreateDirectory(PendingPackages.Folder(_dir));
        File.WriteAllText(Path.Combine(PendingPackages.Folder(_dir), "plugin-wand.zip"), "this is not a zip");
        registry.Set("plugin:wand", new CatalogRegistry.Record("2.0.0", "wand", true, CatalogDefaults.IndexUrl));
        registry.Set("plugin:gone", new CatalogRegistry.Record("1.0.0", "gone", true));   // zip missing
        var log = new List<string>();

        var applied = PendingPackages.ApplyAll(_dir, log.Add);

        Assert.Empty(applied);
        Assert.Equal(2, log.Count);
        Assert.Empty(registry.Read());
        Assert.Empty(Directory.EnumerateFiles(PendingPackages.Folder(_dir)));
        Assert.Empty(PendingPackages.ApplyAll(_dir, log.Add));
    }

    [Fact]
    public void A_pending_entry_is_marked_done_before_the_attempt()
    {
        // The zip is garbage and unreadable files must not matter: after the call nothing is pending any more.
        var registry = new CatalogRegistry(_dir);
        registry.Set("plugin:wand", new CatalogRegistry.Record("2.0.0", "wand", true));
        PendingPackages.ApplyAll(_dir);
        Assert.DoesNotContain(registry.Read().Values, r => r.Pending);
    }

    [Fact]
    public void Registry_version_wins_over_plugin_json_for_catalog_installs()
    {
        PluginDir("plugins", "wand", "1.0.0");
        var entry = new CatalogEntry("wand", "wand", "", null, CatalogKind.Plugin, "1.2.0", null, "https://x/y.zip", Hash, null, null, "wand", "s", null);
        new CatalogRegistry(_dir).Set(entry, pending: false);
        var info = new PluginInfo("wand", "wand", "wand", "1.0.0", null, null, null, true, true, null, true);

        Assert.Equal("1.2.0", new CatalogRegistry(_dir).Lookup(entry, [info]).Version);
    }

    // ---- versions

    [Fact]
    public void Four_part_versions_compare_on_the_revision()
    {
        Assert.True(SemVer.TryParse("1.2.3.4", out var a));
        Assert.True(SemVer.TryParse("1.2.3.3", out var b));
        Assert.True(SemVer.TryParse("1.2.3", out var c));
        Assert.True(a > b);
        Assert.True(b > c);
        Assert.Equal("1.2.3.4", a.ToString());
        Assert.False(SemVer.TryParse("1.2.3.4.5", out _));
    }

    [Fact]
    public void A_pre_release_of_CouchHud_does_not_satisfy_the_final_minimum()
    {
        Assert.True(SemVer.TryParse("0.4.0-beta.1", out var beta));
        var entry = new CatalogEntry("a", "a", "", null, CatalogKind.Plugin, "1.0.0", "0.4.0", "https://x/y.zip", Hash, null, null, "a", "s", null);
        Assert.False(CatalogRules.IsCompatible(entry, beta));
        Assert.True(CatalogRules.IsCompatible(entry with { MinCouchHud = "0.4.0-beta.1" }, beta));
    }

    // ---- hostile inputs

    [Theory]
    [InlineData("file://server/share/index.json", false)]
    [InlineData("file:////server/share/index.json", false)]
    [InlineData("file:///C:/Temp/index.json", true)]
    public void Network_paths_are_not_local_sources(string url, bool allowed) => Assert.Equal(allowed, CatalogParser.IsAllowedUrl(url, allowLocal: true));

    [Fact]
    public void A_zip_declaring_less_than_it_holds_is_stopped()
    {
        var root = Path.Combine(_dir, "skins");
        var zipPath = Path.Combine(_dir, "bomb.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using (var w = zip.CreateEntry("bomb/skin.json").Open()) w.Write(new byte[2 * 1024 * 1024]);
        }
        // Patch the declared uncompressed size (local header at 22, central directory entry at +24) down to 100 bytes.
        var bytes = File.ReadAllBytes(zipPath);
        BitConverter.GetBytes(100).CopyTo(bytes, 22);
        var central = bytes.AsSpan().LastIndexOf(new byte[] { 0x50, 0x4b, 0x01, 0x02 });
        BitConverter.GetBytes(100).CopyTo(bytes, central + 24);
        File.WriteAllBytes(zipPath, bytes);

        Assert.Throws<InvalidDataException>(() => PackageInstaller.Install(zipPath, root, PackageInstaller.Kind.Skin));

        Assert.False(Directory.Exists(Path.Combine(root, "bomb")));
        Assert.Empty(Directory.EnumerateDirectories(root));
    }

    private sealed class StallingStream : Stream
    {
        private bool _first = true;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_first)
            {
                _first = false;
                buffer.Span[0] = 1;
                return 1;
            }
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_stalled_download_is_cancelled_by_the_inactivity_timeout()
    {
        var before = CatalogService.DownloadInactivity;
        CatalogService.DownloadInactivity = TimeSpan.FromMilliseconds(300);
        try
        {
            var entry = new CatalogEntry("s", "s", "", null, CatalogKind.Skin, "1.0.0", null, "https://example.com/s.zip", Hash, null, null, "s", "https://example.com/i.json", null);
            var service = Service(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) });
            var started = DateTime.UtcNow;

            var ex = await Assert.ThrowsAsync<CatalogException>(() => service.InstallAsync(entry, App));

            Assert.Contains("keine Daten", ex.Message);
            Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
        }
        finally
        {
            CatalogService.DownloadInactivity = before;
        }
    }
}
