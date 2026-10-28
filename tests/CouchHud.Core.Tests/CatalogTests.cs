using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using CouchHud.Core.Plugins;

namespace CouchHud.Core.Tests;

public class CatalogTests : IDisposable
{
    private static readonly SemVer App = new(0, 3, 0, "");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-cat-" + Guid.NewGuid().ToString("N"));
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    public CatalogTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string Entry(string id = "wand", string type = "plugin", string version = "1.0.0", string extra = "", string? hash = Hash, string download = "https://example.com/wand.zip") =>
        $$"""
        { "id": "{{id}}", "name": "{{id}} name", "type": "{{type}}", "version": "{{version}}",
          "description": { "de": "Beschreibung", "en": "Description" }, "author": "kewe87",
          "download": "{{download}}", {{(hash is null ? "" : $"\"sha256\": \"{hash}\",")}} "homepage": "https://example.com" {{extra}} }
        """;

    private static string Index(params string[] entries) => "{ \"version\": 1, \"entries\": [" + string.Join(",", entries) + "] }";

    private static IReadOnlyList<CatalogEntry> Parse(string json, bool local = false) => CatalogParser.Parse(json, "https://example.com/index.json", "de", local);

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

    private static string Sha(string file) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();

    // ---- parsing

    [Fact]
    public void Parses_entries_and_picks_the_language()
    {
        var e = Assert.Single(Parse(Index(Entry())));
        Assert.Equal("wand", e.Id);
        Assert.Equal("Beschreibung", e.Description);
        Assert.Equal(CatalogKind.Plugin, e.Kind);
        Assert.Null(e.Problem);
        Assert.Equal("wand", e.Folder);
        Assert.Equal("Description", CatalogParser.Parse(Index(Entry()), "s", "en")[0].Description);
    }

    [Fact]
    public void Broken_index_throws_a_catalog_error()
    {
        Assert.Throws<CatalogException>(() => Parse("{ nope"));
        Assert.Throws<CatalogException>(() => Parse("{ \"entries\": 5 }"));
        Assert.Throws<CatalogException>(() => Parse("42"));
    }

    [Fact]
    public void Broken_entries_are_skipped_not_fatal()
    {
        var json = Index(Entry(id: "ok"), "42", "{ \"id\": \"../evil\", \"name\": \"x\", \"type\": \"plugin\", \"version\": \"1.0.0\" }",
            Entry(id: "badver", version: "banana"), Entry(id: "badtype", type: "exe"), "{ \"name\": \"no id\" }");
        Assert.Equal(["ok"], Parse(json).Select(e => e.Id));
    }

    [Fact]
    public void Missing_or_malformed_hash_marks_the_entry_uninstallable()
    {
        Assert.NotNull(Assert.Single(Parse(Index(Entry(hash: null)))).Problem);
        Assert.NotNull(Assert.Single(Parse(Index(Entry(hash: "abc")))).Problem);
    }

    [Fact]
    public void Non_https_download_is_rejected_unless_the_developer_switch_allows_loopback()
    {
        Assert.NotNull(Assert.Single(Parse(Index(Entry(download: "http://example.com/a.zip")))).Problem);
        Assert.NotNull(Assert.Single(Parse(Index(Entry(download: "ftp://example.com/a.zip")))).Problem);
        Assert.NotNull(Assert.Single(Parse(Index(Entry(download: "http://127.0.0.1/a.zip")))).Problem);
        Assert.Null(Assert.Single(Parse(Index(Entry(download: "http://127.0.0.1:8080/a.zip")), local: true)).Problem);
        Assert.NotNull(Assert.Single(Parse(Index(Entry(download: "http://example.com/a.zip")), local: true)).Problem);
    }

    [Fact]
    public void Duplicate_ids_keep_the_first()
    {
        Assert.Single(Parse(Index(Entry(version: "1.0.0"), Entry(version: "2.0.0"))));
    }

    // ---- versions and status

    [Theory]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("1.10.0", "1.9.9", 1)]
    [InlineData("1.0.0-beta.1", "1.0.0", -1)]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.10", -1)]
    [InlineData("v2.0", "2.0.0", 0)]
    [InlineData("1.2.3+build5", "1.2.3", 0)]
    public void Versions_compare_like_semver(string a, string b, int expected)
    {
        Assert.True(SemVer.TryParse(a, out var x));
        Assert.True(SemVer.TryParse(b, out var y));
        Assert.Equal(expected, Math.Sign(x.CompareTo(y)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1")]
    [InlineData("1.x.0")]
    public void Garbage_is_not_a_version(string text) => Assert.False(SemVer.TryParse(text, out _));

    [Fact]
    public void Status_covers_not_installed_installed_update_and_unavailable()
    {
        var e = Parse(Index(Entry(version: "1.2.0")))[0];
        Assert.Equal(CatalogStatus.NotInstalled, CatalogRules.StatusOf(e, false, null, App));
        Assert.Equal(CatalogStatus.UpdateAvailable, CatalogRules.StatusOf(e, true, "1.1.0", App));
        Assert.Equal(CatalogStatus.Installed, CatalogRules.StatusOf(e, true, "1.2.0", App));
        Assert.Equal(CatalogStatus.Installed, CatalogRules.StatusOf(e, true, "1.3.0", App));
        Assert.Equal(CatalogStatus.Installed, CatalogRules.StatusOf(e, true, null, App));

        var tooNew = Parse(Index(Entry(extra: ", \"minCouchHud\": \"9.0.0\"")))[0];
        Assert.Equal(CatalogStatus.Unavailable, CatalogRules.StatusOf(tooNew, false, null, App));
        Assert.Equal(CatalogStatus.Installed, CatalogRules.StatusOf(tooNew, true, "0.1.0", App));
        Assert.Contains("9.0.0", CatalogRules.Reason(tooNew, App));

        var ok = Parse(Index(Entry(extra: ", \"minCouchHud\": \"0.3.0\"")))[0];
        Assert.Equal(CatalogStatus.NotInstalled, CatalogRules.StatusOf(ok, false, null, App));
    }

    [Fact]
    public void App_version_comes_from_the_build()
    {
        Assert.True(CatalogRules.AppVersion >= new SemVer(0, 3, 0, ""));
    }

    [Fact]
    public void The_example_index_in_the_repository_parses()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "catalog", "index.json"))) dir = dir.Parent;
        var release = Environment.GetEnvironmentVariable("RELEASE") == "1";   // scripts/check.sh with RELEASE=1: a stale signature must never ship
        if (dir is null) { Assert.False(release, "RELEASE=1 but catalog/index.json was not found"); return; }   // built outside the repository
        var file = Path.Combine(dir.FullName, "catalog", "index.json");
        // Signed with the maintainer's key; a changed byte (or line ending) would break this.
        var signed = CatalogSignature.Verify(File.ReadAllBytes(file), File.ReadAllText(file + ".sig"));
        // A change of the index that waits for the maintainer's signature is marked by catalog/index.json.unsigned; once the
        // signature is renewed the marker has to go, so a stale signature can never pass for long.
        var marker = Path.Combine(dir.FullName, "catalog", "index.json.unsigned");
        if (release) { Assert.False(File.Exists(marker), "RELEASE=1: catalog/index.json.unsigned exists"); Assert.True(signed, "RELEASE=1: catalog/index.json.sig does not match catalog/index.json (checked with the key pinned in the code)"); }
        else if (File.Exists(marker)) Assert.False(signed, "index.json is signed again: delete catalog/index.json.unsigned");
        else Assert.True(signed);
        var entries = Parse(File.ReadAllText(file));
        Assert.Equal(["wand", "homeassistant", "casehud", "vpn", "synthwave", "stardust", "deepsea", "greenlight", "neoncity", "nova"], entries.Select(e => e.Id));
        // Every entry works with this build: the skins and plugins that ask for 0.6.0 are installable now that the product is 0.6.0.
        Assert.All(entries, e => Assert.True(CatalogRules.IsCompatible(e, CatalogRules.AppVersion), $"{e.Id} needs {e.MinCouchHud}"));
        // An icon is shown only with a hash: an entry with a statement takes it from the signed statement (never from the index), every
        // other entry with an icon names it in the index.
        Assert.All(entries.Where(e => e.IconUrl is not null && e.Statement is null),
            e => Assert.True(e.IconSha256 is { Length: 64 }, $"{e.Id} has an icon but no iconSha256, so the catalog would not show it"));
        Assert.All(entries.Where(e => e.Statement is not null), e => SignedCatalogEntry.AssertOwnSigned(e));
        // New entries use one spelling for the project's repositories (CatalogUrl.CanonicalRepositories); the host accepts any case, the index does not.
        foreach (var url in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), "\"(https://[^\"]+)\"").Select(m => m.Groups[1].Value))
            Assert.True(CatalogUrl.NonCanonicalReason(url) is null, $"{url}: {CatalogUrl.NonCanonicalReason(url)}");
        // The Home Assistant plugin waits for the owner's signed statement (catalog/statements/homeassistant.json): until the entry has one it
        // is listed but cannot be installed, which is what a missing package hash means.
        Assert.All(entries, e =>
        {
            if (e.Id is "homeassistant" or "casehud" or "vpn" && e.Statement is null) Assert.NotNull(e.Problem);
            else Assert.Null(e.Problem);
        });
        Assert.Equal("CouchHud.Plugin.Wand", entries[0].Folder);
    }

    // ---- hash

    [Fact]
    public void Hash_check_accepts_the_right_hash_and_rejects_missing_or_wrong_ones()
    {
        var zip = MakeZip("a.zip", ("x/skin.json", "{}"));
        CatalogRules.VerifySha256(zip, Sha(zip));
        CatalogRules.VerifySha256(zip, Sha(zip).ToUpperInvariant());
        Assert.Throws<CatalogException>(() => CatalogRules.VerifySha256(zip, Hash));
        Assert.Throws<CatalogException>(() => CatalogRules.VerifySha256(zip, null));
        Assert.Throws<CatalogException>(() => CatalogRules.VerifySha256(zip, "nothex"));
    }

    // ---- loading, cache, install

    private sealed class FakeHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request.RequestUri!));
        }
    }

    private CatalogService Service(FakeHandler handler, bool local = false, params string[] sources) =>
        new(new HttpClient(handler), _dir, () => sources.Length == 0 ? ["https://example.com/index.json"] : sources, () => local);

    private static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };

    [Fact]
    public async Task Load_caches_the_index_and_serves_it_when_offline()
    {
        var online = true;
        var handler = new FakeHandler(_ => online ? Ok(Index(Entry())) : throw new HttpRequestException("offline"));
        var service = Service(handler);

        var first = await service.LoadAsync();
        Assert.False(first.FromCache);
        Assert.Single(first.Entries);

        online = false;
        var second = await service.LoadAsync();
        Assert.True(second.FromCache);
        Assert.Single(second.Entries);
        Assert.NotNull(second.Error);
        Assert.NotNull(second.FetchedAt);
    }

    [Fact]
    public async Task Load_offline_without_cache_reports_an_error_instead_of_throwing()
    {
        var service = Service(new FakeHandler(_ => throw new HttpRequestException("offline")));
        var result = await service.LoadAsync();
        Assert.Empty(result.Entries);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Load_survives_a_broken_index_and_falls_back_to_the_cache()
    {
        var good = true;
        var service = Service(new FakeHandler(_ => Ok(good ? Index(Entry()) : "<html>404</html>")));
        await service.LoadAsync();
        good = false;
        var result = await service.LoadAsync();
        Assert.True(result.FromCache);
        Assert.Single(result.Entries);
    }

    [Fact]
    public async Task Load_refuses_http_sources_and_merges_several_sources()
    {
        var handler = new FakeHandler(u => Ok(u.Host == "a.example" ? Index(Entry(id: "one")) : Index(Entry(id: "two"), Entry(id: "one"))));
        var service = Service(handler, false, "https://a.example/i.json", "https://b.example/i.json", "http://c.example/i.json");
        var result = await service.LoadAsync();
        Assert.Equal(["one", "two"], result.Entries.Select(e => e.Id).Order());
        Assert.DoesNotContain(handler.Requests, r => r.Scheme == "http");
        Assert.Contains("https", result.Error);
    }

    [Fact]
    public async Task Load_ignores_an_oversized_index()
    {
        var service = Service(new FakeHandler(_ => Ok(new string(' ', (int)CatalogService.MaxIndexBytes + 10))));
        var result = await service.LoadAsync();
        Assert.Empty(result.Entries);
        Assert.NotNull(result.Error);
    }

    private static CatalogEntry Parsed(string json, bool local = false) => Parse(Index(json), local)[0];

    private string SkinZip() => MakeZip("skin.zip", ("synth/skin.json", "{}"), ("synth/skin.css", ":root{}"));

    [Fact]
    public async Task Install_downloads_verifies_and_installs_a_skin()
    {
        var zip = SkinZip();
        var bytes = File.ReadAllBytes(zip);
        var entry = Parsed(Entry(id: "synth", type: "skin", hash: Sha(zip)));
        var service = Service(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));

        var outcome = await service.InstallAsync(entry, App);

        Assert.False(outcome.RestartNeeded);
        Assert.True(File.Exists(Path.Combine(_dir, "skins", "synth", "skin.css")));
        Assert.Equal("1.0.0", new CatalogRegistry(_dir).Read()["skin:synth"].Version);
    }

    [Fact]
    public async Task Install_with_a_wrong_hash_writes_nothing()
    {
        var zip = SkinZip();
        var entry = Parsed(Entry(id: "synth", type: "skin", hash: Hash));
        var service = Service(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(zip)) }));

        var ex = await Assert.ThrowsAsync<CatalogException>(() => service.InstallAsync(entry, App));

        Assert.Contains("Prüfsumme", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(_dir, "skins")));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_dir, "catalog-temp")));
    }

    [Fact]
    public async Task Install_without_a_hash_is_refused_before_any_download()
    {
        var handler = new FakeHandler(_ => Ok("x"));
        var entry = Parsed(Entry(id: "synth", type: "skin", hash: null));
        await Assert.ThrowsAsync<CatalogException>(() => Service(handler).InstallAsync(entry, App));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Install_is_refused_when_minCouchHud_is_too_high_or_the_url_is_not_https()
    {
        var handler = new FakeHandler(_ => Ok("x"));
        var tooNew = Parsed(Entry(extra: ", \"minCouchHud\": \"9.0.0\""));
        await Assert.ThrowsAsync<CatalogException>(() => Service(handler).InstallAsync(tooNew, App));

        // Even a hand-made entry with an http URL never reaches the network.
        var http = Parsed(Entry()) with { DownloadUrl = "http://example.com/a.zip", Problem = null };
        await Assert.ThrowsAsync<CatalogException>(() => Service(handler).InstallAsync(http, App));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Install_aborts_a_package_that_is_too_large_while_downloading()
    {
        var entry = Parsed(Entry(id: "synth", type: "skin"));
        var service = Service(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ZeroStream(CatalogService.MaxPackageBytes + 1)) }));
        var ex = await Assert.ThrowsAsync<CatalogException>(() => service.InstallAsync(entry, App));
        Assert.Contains("groß", ex.Message);
    }

    private sealed class ZeroStream(long length) : Stream
    {
        private long _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - _position);
            Array.Clear(buffer, offset, n);
            _position += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private string PluginZip(string folder, string dllName = "x.dll") => MakeZip($"{folder}.zip", ($"{folder}/{dllName}", "MZ"), ($"{folder}/plugin.json", "{}"));

    [Fact]
    public void New_plugin_installs_at_once_but_needs_a_restart()
    {
        var zip = PluginZip("wand");
        var entry = Parsed(Entry(hash: Sha(zip)));
        var outcome = Service(new FakeHandler(_ => Ok(""))).Install(entry, zip);
        Assert.True(outcome.RestartNeeded);
        Assert.True(File.Exists(Path.Combine(_dir, "plugins", "wand", "x.dll")));
    }

    [Fact]
    public void Plugin_update_is_staged_and_applied_at_the_next_start()
    {
        var pluginDir = Path.Combine(_dir, "plugins", "wand");
        Directory.CreateDirectory(pluginDir);
        File.WriteAllText(Path.Combine(pluginDir, "old.dll"), "old");
        var zip = PluginZip("wand");
        var entry = Parsed(Entry(version: "1.1.0", hash: Sha(zip)));

        var outcome = Service(new FakeHandler(_ => Ok(""))).Install(entry, zip);

        Assert.True(outcome.RestartNeeded);
        Assert.True(File.Exists(Path.Combine(pluginDir, "old.dll")));   // untouched while the old one may be loaded
        Assert.True(new CatalogRegistry(_dir).Read()["plugin:wand"].Pending);

        var applied = PendingPackages.ApplyAll(_dir);

        Assert.Equal(["plugin:wand"], applied);
        Assert.False(File.Exists(Path.Combine(pluginDir, "old.dll")));
        Assert.True(File.Exists(Path.Combine(pluginDir, "x.dll")));
        Assert.False(new CatalogRegistry(_dir).Read()["plugin:wand"].Pending);
        Assert.Empty(PendingPackages.ApplyAll(_dir));
    }

    [Fact]
    public void Lookup_reports_pending_updates_with_the_staged_version()
    {
        var pluginDir = Path.Combine(_dir, "plugins", "wand");
        Directory.CreateDirectory(pluginDir);
        var zip = PluginZip("wand");
        var entry = Parsed(Entry(version: "1.1.0", hash: Sha(zip)));
        Service(new FakeHandler(_ => Ok(""))).Install(entry, zip);

        var (installed, version, pending, _) = new CatalogRegistry(_dir).Lookup(entry, []);

        Assert.True(installed);
        Assert.Equal("1.1.0", version);
        Assert.True(pending);
        Assert.False(new CatalogRegistry(_dir).Lookup(Parsed(Entry(id: "other")), []).Installed);
    }

    [Fact]
    public void Lookup_prefers_what_the_loaded_plugin_reports()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "plugins", "CouchHud.Plugin.Wand"));
        var entry = Parsed(Entry(extra: ", \"folder\": \"CouchHud.Plugin.Wand\""));
        var info = new PluginInfo("CouchHud.Plugin.Wand", "wand", "Wand", "1.0.0", null, null, null, true, true, null, false);
        var (installed, version, _, _) = new CatalogRegistry(_dir).Lookup(entry, [info]);
        Assert.True(installed);
        Assert.Equal("1.0.0", version);
        // Bundled plugins have no folder in the user directory but still count as installed.
        Assert.True(new CatalogRegistry(Path.Combine(_dir, "elsewhere")).Lookup(entry, [info]).Installed);
    }

    // ---- installer hardening

    [Fact]
    public void Zip_slip_is_refused_and_leaves_the_installed_version_alone()
    {
        var root = Path.Combine(_dir, "plugins");
        var target = Path.Combine(root, "wand");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.dll"), "keep");
        var evil = MakeZip("evil.zip", ("wand/x.dll", "MZ"), ("wand/../../escaped.txt", "pwned"));

        Assert.Throws<InvalidOperationException>(() => PackageInstaller.Install(evil, root, PackageInstaller.Kind.Plugin, "wand"));

        Assert.True(File.Exists(Path.Combine(target, "keep.dll")));
        Assert.False(File.Exists(Path.Combine(_dir, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(root, "escaped.txt")));
    }

    [Theory]
    [InlineData("C:/Windows/evil.dll")]
    [InlineData("wand/C:evil.dll")]
    [InlineData("/etc/evil.dll")]
    public void Absolute_or_drive_paths_in_the_archive_are_refused(string entryPath)
    {
        var zip = MakeZip("abs.zip", ("wand/x.dll", "MZ"), (entryPath, "x"));
        Assert.Throws<InvalidOperationException>(() => PackageInstaller.Install(zip, Path.Combine(_dir, "plugins"), PackageInstaller.Kind.Plugin, "wand"));
    }

    [Fact]
    public void Catalog_package_must_use_the_folder_name_of_its_entry()
    {
        var zip = PluginZip("other");
        Assert.Throws<InvalidOperationException>(() => PackageInstaller.Install(zip, Path.Combine(_dir, "plugins"), PackageInstaller.Kind.Plugin, "wand"));
        Assert.Throws<InvalidOperationException>(() => PackageInstaller.Install(zip, Path.Combine(_dir, "plugins"), PackageInstaller.Kind.Plugin, ".."));
    }

    [Fact]
    public void Flat_catalog_package_is_installed_under_the_expected_name()
    {
        var zip = MakeZip("download.zip", ("x.dll", "MZ"));
        var target = PackageInstaller.Install(zip, Path.Combine(_dir, "plugins"), PackageInstaller.Kind.Plugin, "wand");
        Assert.Equal(Path.Combine(_dir, "plugins", "wand"), target);
    }

    // ---- developer switch

    [Fact]
    public async Task Local_file_catalog_works_only_with_the_developer_switch()
    {
        var zip = SkinZip();
        var indexFile = Path.Combine(_dir, "dev-index.json");
        File.WriteAllText(indexFile, Index(Entry(id: "synth", type: "skin", hash: Sha(zip), download: new Uri(zip).AbsoluteUri)));
        var source = new Uri(indexFile).AbsoluteUri;
        var handler = new FakeHandler(_ => Ok(""));

        var off = await Service(handler, false, source).LoadAsync();
        Assert.Empty(off.Entries);

        var service = Service(handler, true, source);
        var on = await service.LoadAsync();
        var entry = Assert.Single(on.Entries);
        Assert.Null(entry.Problem);
        await service.InstallAsync(entry, App);
        Assert.True(File.Exists(Path.Combine(_dir, "skins", "synth", "skin.json")));
        Assert.Empty(handler.Requests);
    }
}
