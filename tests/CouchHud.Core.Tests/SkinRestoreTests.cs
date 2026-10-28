using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using CouchHud.Core.Plugins;
using CouchHud.Core.Skins;

namespace CouchHud.Core.Tests;

/// <summary>
/// 0.6 ships the Couch skin only. A household whose skin was one of the four that moved to the catalog gets it back on the first start:
/// from the signed catalog, as a normal verified catalog install, bounded in attempts. Throwaway keys only.
/// </summary>
[Collection("Language")]
public class SkinRestoreTests : IDisposable
{
    private static readonly SemVer App = new(0, 6, 0, "");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-restore-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly List<Uri> _requests = [];

    public SkinRestoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _key.Dispose();
        _otherKey.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private string Pem => _key.ExportSubjectPublicKeyInfoPem();

    private static string Sign(ECDsa key, string text) =>
        Convert.ToBase64String(key.SignData(CatalogSignature.WithContext(CatalogSignature.CatalogContext, Encoding.UTF8.GetBytes(text)), HashAlgorithmName.SHA256));

    private static byte[] SkinZip(string id, string css = "", bool traversal = false)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string text)
            {
                using var w = new StreamWriter(zip.CreateEntry(path).Open());
                w.Write(text);
            }
            Add($"{id}/skin.json", $$"""{ "name": { "de": "{{id}}", "en": "{{id}}" } }""");
            Add($"{id}/skin.css", css.Length > 0 ? css : $":root[data-skin=\"user:{id}\"] {{ --accent: teal; }}");
            if (traversal) Add($"{id}/../../evil.txt", "boom");
        }
        return ms.ToArray();
    }

    private static string Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static string Entry(string id, byte[] zip, string folder = "", string min = "0.6.0") =>
        $$"""{ "id": "{{id}}", {{(folder.Length > 0 ? $"\"folder\": \"{folder}\"," : "")}} "name": "{{id}}", "type": "skin", "version": "1.0.0", "minCouchHud": "{{min}}", "download": "https://example.com/{{id}}.zip", "sha256": "{{Hex(zip)}}" }""";

    private sealed class FakeHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request.RequestUri!));
    }

    private static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };

    /// <summary>The default catalog source (signature mandatory) with the given index text, its signature and the files it points at.</summary>
    private SkinRestore Restore(string indexText, ECDsa signer, IReadOnlyDictionary<string, byte[]> files, SemVer? app = null, string source = "") =>
        Restore(uri => _requests.Add(uri), indexText, signer, files, app, source);

    private SkinRestore Restore(Action<Uri> seen, string indexText, ECDsa signer, IReadOnlyDictionary<string, byte[]> files, SemVer? app = null, string source = "")
    {
        var url = source.Length > 0 ? source : CatalogDefaults.IndexUrl;
        var service = new CatalogService(new HttpClient(new FakeHandler(u =>
        {
            seen(u);
            if (u.AbsolutePath.EndsWith(".sig")) return Ok(Sign(signer, indexText));
            if (u.AbsolutePath.EndsWith("index.json")) return Ok(indexText);
            return files.TryGetValue(u.AbsolutePath.TrimStart('/'), out var data) ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) } : new HttpResponseMessage(HttpStatusCode.NotFound);
        })), _dir, () => [url], () => false, "de", Pem);
        return new SkinRestore(_dir, service, () => app ?? App);
    }

    private SkinRestore Offline()
    {
        var service = new CatalogService(new HttpClient(new FakeHandler(u =>
        {
            _requests.Add(u);
            throw new HttpRequestException("offline");
        })), _dir, () => [CatalogDefaults.IndexUrl], () => false, "de", Pem);
        return new SkinRestore(_dir, service, () => App);
    }

    private bool HasSkin(string id) => File.Exists(Path.Combine(_dir, "skins", id, "skin.css")) && File.Exists(Path.Combine(_dir, "skins", id, "skin.json"));

    private static string Index(params string[] entries) => "{ \"entries\": [" + string.Join(",", entries) + "] }";

    // ---- what is wanted

    [Fact]
    public void Wanted_are_the_moved_skins_the_settings_ask_for_each_once()
    {
        string?[] asked = ["user:nova", "ps4", "default", null, "user:stardust", "user:nova", "gone", "deepsea", "user:deepsea"];
        Assert.Equal(["nova", "deepsea"], SkinRestore.Wanted(asked));
        Assert.Empty(SkinRestore.Wanted(["default", "user:stardust", null]));
    }

    // ---- the catalog is fine

    [Fact]
    public async Task A_missing_skin_is_installed_from_the_signed_catalog_like_any_catalog_install()
    {
        var zip = SkinZip("deepsea");
        var restore = Restore(Index(Entry("deepsea", zip)), _key, new Dictionary<string, byte[]> { ["deepsea.zip"] = zip });

        Assert.True(restore.CanTry("deepsea"));
        var result = Assert.Single(await restore.RunAsync(["deepsea"]));

        Assert.Equal(SkinRestoreOutcome.Installed, result.Outcome);
        Assert.True(HasSkin("deepsea"));
        Assert.False(restore.CanTry("deepsea"));
        // Recorded as a catalog install (so it is updated from the catalog later) and without any trust entry: a skin is no code.
        var record = new CatalogRegistry(_dir).Read()["skin:deepsea"];
        Assert.Equal("1.0.0", record.Version);
        Assert.False(record.Pending);
        Assert.False(File.Exists(Path.Combine(_dir, "plugin-trust.json")));
        Assert.False(Directory.Exists(Path.Combine(_dir, "plugins")));
        // The folder holds the package and nothing else was put next to it.
        Assert.Equal(["deepsea"], Directory.GetDirectories(Path.Combine(_dir, "skins")).Select(Path.GetFileName).ToArray());
        // It is a skin the HUD lists, with the id the settings have.
        Assert.Equal("user:deepsea", Assert.Single(SkinCatalog.ListUserSkins(Path.Combine(_dir, "skins"))).Id);
    }

    [Fact]
    public async Task Several_missing_skins_come_with_one_index_load()
    {
        var a = SkinZip("deepsea");
        var b = SkinZip("nova");
        var restore = Restore(Index(Entry("deepsea", a), Entry("nova", b)), _key, new Dictionary<string, byte[]> { ["deepsea.zip"] = a, ["nova.zip"] = b });

        var results = await restore.RunAsync(["deepsea", "nova"]);

        Assert.All(results, r => Assert.Equal(SkinRestoreOutcome.Installed, r.Outcome));
        Assert.Single(_requests, u => u.AbsolutePath.EndsWith("index.json"));
    }

    // ---- already installed

    [Fact]
    public async Task An_installed_skin_is_left_alone_and_nothing_is_asked_of_the_network()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir, "skins", "nova")).FullName;
        File.WriteAllText(Path.Combine(folder, "skin.json"), """{ "name": "Mine" }""");
        File.WriteAllText(Path.Combine(folder, "skin.css"), "/* mine */");
        var restore = Offline();

        Assert.False(restore.CanTry("nova"));
        var result = Assert.Single(await restore.RunAsync(["nova"]));

        Assert.Equal(SkinRestoreOutcome.Present, result.Outcome);
        Assert.Empty(_requests);
        Assert.Equal("/* mine */", File.ReadAllText(Path.Combine(folder, "skin.css")));
        Assert.False(File.Exists(Path.Combine(_dir, "skin-restore.json")));
    }

    // ---- offline

    [Fact]
    public async Task Offline_the_run_fails_without_installing_and_the_next_start_may_try_again()
    {
        var restore = Offline();

        var result = Assert.Single(await restore.RunAsync(["deepsea"]));

        Assert.Equal(SkinRestoreOutcome.Failed, result.Outcome);
        Assert.False(HasSkin("deepsea"));
        Assert.True(restore.CanTry("deepsea"));
    }


    /// <summary>A catalog that answers (signed, fine) but has no deepsea: every run is a real, used attempt.</summary>
    private SkinRestore WithoutTheSkin()
    {
        var zip = SkinZip("nova");
        return Restore(Index(Entry("nova", zip)), _key, new Dictionary<string, byte[]> { ["nova.zip"] = zip });
    }

    [Fact]
    public async Task The_attempts_are_bounded_per_version_and_then_nothing_is_asked_any_more()
    {
        for (var i = 0; i < SkinRestore.MaxAttempts; i++)
        {
            Assert.True(WithoutTheSkin().CanTry("deepsea"));
            Assert.Equal(SkinRestoreOutcome.Failed, Assert.Single(await WithoutTheSkin().RunAsync(["deepsea"])).Outcome);
        }
        var before = _requests.Count;
        Assert.True(before >= SkinRestore.MaxAttempts);

        var restore = WithoutTheSkin();
        Assert.False(restore.CanTry("deepsea"));
        Assert.Equal(SkinRestoreOutcome.GaveUp, Assert.Single(await restore.RunAsync(["deepsea"])).Outcome);
        Assert.Equal(before, _requests.Count);

        // Another version of CouchHUD gets its tries again.
        var newer = new SkinRestore(_dir, new CatalogService(new HttpClient(new FakeHandler(_ => throw new HttpRequestException("offline"))), _dir, () => [CatalogDefaults.IndexUrl], () => false, "de", Pem), () => new SemVer(0, 6, 1, ""));
        Assert.True(newer.CanTry("deepsea"));
    }

    [Fact]
    public async Task A_run_that_never_reached_the_catalog_does_not_use_up_an_attempt_but_is_bounded_too()
    {
        // Network down at boot, three starts in a row: still tries left for the first start with a connection.
        for (var i = 0; i < SkinRestore.MaxAttempts + 2; i++)
            Assert.Equal(SkinRestoreOutcome.Failed, Assert.Single(await Offline().RunAsync(["deepsea"])).Outcome);
        Assert.True(Offline().CanTry("deepsea"));

        var zip = SkinZip("deepsea");
        var online = Restore(Index(Entry("deepsea", zip)), _key, new Dictionary<string, byte[]> { ["deepsea.zip"] = zip });
        Assert.Equal(SkinRestoreOutcome.Installed, Assert.Single(await online.RunAsync(["deepsea"])).Outcome);
    }

    [Fact]
    public async Task Without_any_answer_the_starts_are_bounded_as_well_so_nothing_loops()
    {
        for (var i = 0; i < SkinRestore.MaxUnreachableStarts; i++) await Offline().RunAsync(["deepsea"]);
        var before = _requests.Count;

        var restore = Offline();
        Assert.False(restore.CanTry("deepsea"));
        Assert.Equal(SkinRestoreOutcome.GaveUp, Assert.Single(await restore.RunAsync(["deepsea"])).Outcome);
        Assert.Equal(before, _requests.Count);
    }

    [Fact]
    public async Task A_second_run_in_the_same_start_counts_again_so_nothing_can_loop()
    {
        var restore = WithoutTheSkin();
        for (var i = 0; i < 10; i++) await restore.RunAsync(["deepsea"]);
        // Ten calls, at most MaxAttempts of them went to the catalog (two requests each: index and signature).
        Assert.True(_requests.Count <= SkinRestore.MaxAttempts * 2, $"{_requests.Count} requests");
        Assert.False(restore.CanTry("deepsea"));
    }

    // ---- bad signature and other refusals

    [Fact]
    public async Task A_catalog_signed_with_another_key_installs_nothing_and_downloads_nothing()
    {
        var zip = SkinZip("deepsea");
        var restore = Restore(Index(Entry("deepsea", zip)), _otherKey, new Dictionary<string, byte[]> { ["deepsea.zip"] = zip });

        var result = Assert.Single(await restore.RunAsync(["deepsea"]));

        Assert.Equal(SkinRestoreOutcome.Failed, result.Outcome);
        Assert.False(HasSkin("deepsea"));
        Assert.DoesNotContain(_requests, u => u.AbsolutePath.EndsWith(".zip"));
    }

    [Fact]
    public async Task A_catalog_without_a_signature_installs_nothing()
    {
        var zip = SkinZip("deepsea");
        var text = Index(Entry("deepsea", zip));
        var service = new CatalogService(new HttpClient(new FakeHandler(u =>
        {
            _requests.Add(u);
            return u.AbsolutePath.EndsWith(".sig") ? new HttpResponseMessage(HttpStatusCode.NotFound) : u.AbsolutePath.EndsWith("index.json") ? Ok(text) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };
        })), _dir, () => [CatalogDefaults.IndexUrl], () => false, "de", Pem);

        var result = Assert.Single(await new SkinRestore(_dir, service, () => App).RunAsync(["deepsea"]));

        Assert.Equal(SkinRestoreOutcome.Failed, result.Outcome);
        Assert.False(HasSkin("deepsea"));
        Assert.DoesNotContain(_requests, u => u.AbsolutePath.EndsWith(".zip"));
    }

    [Fact]
    public async Task An_entry_of_a_source_the_user_added_is_never_used_even_with_a_matching_name()
    {
        // Unsigned (no .sig): listed for the user to install by hand, but not a restore.
        var zip = SkinZip("deepsea");
        var text = Index(Entry("deepsea", zip));
        var service = new CatalogService(new HttpClient(new FakeHandler(u =>
        {
            _requests.Add(u);
            return u.AbsolutePath.EndsWith(".sig") ? new HttpResponseMessage(HttpStatusCode.NotFound) : u.AbsolutePath.EndsWith("index.json") ? Ok(text) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };
        })), _dir, () => ["https://example.com/index.json"], () => false, "de", Pem);

        var result = Assert.Single(await new SkinRestore(_dir, service, () => App).RunAsync(["deepsea"]));

        Assert.Equal(SkinRestoreOutcome.Failed, result.Outcome);
        Assert.False(HasSkin("deepsea"));
        Assert.DoesNotContain(_requests, u => u.AbsolutePath.EndsWith(".zip"));
    }

    [Fact]
    public async Task A_package_that_does_not_match_the_signed_hash_is_refused()
    {
        var zip = SkinZip("deepsea");
        var swapped = SkinZip("deepsea", css: ":root { --accent: red; }");
        var restore = Restore(Index(Entry("deepsea", zip)), _key, new Dictionary<string, byte[]> { ["deepsea.zip"] = swapped });

        var result = Assert.Single(await restore.RunAsync(["deepsea"]));

        Assert.Equal(SkinRestoreOutcome.Failed, result.Outcome);
        Assert.False(HasSkin("deepsea"));
        Assert.False(Directory.Exists(Path.Combine(_dir, "skins", "deepsea")));
    }

    [Fact]
    public async Task A_package_with_a_path_out_of_its_folder_is_refused_even_when_the_hash_fits()
    {
        var zip = SkinZip("deepsea", traversal: true);
        var restore = Restore(Index(Entry("deepsea", zip)), _key, new Dictionary<string, byte[]> { ["deepsea.zip"] = zip });

        var result = Assert.Single(await restore.RunAsync(["deepsea"]));

        Assert.Equal(SkinRestoreOutcome.Failed, result.Outcome);
        Assert.False(HasSkin("deepsea"));
        Assert.False(File.Exists(Path.Combine(_dir, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_dir)!, "evil.txt")));
    }

    [Fact]
    public async Task An_entry_that_needs_a_newer_couchhud_or_has_another_folder_is_refused()
    {
        var zip = SkinZip("deepsea");
        var files = new Dictionary<string, byte[]> { ["deepsea.zip"] = zip };

        var tooNew = Restore(Index(Entry("deepsea", zip, min: "0.7.0")), _key, files);
        Assert.Equal(SkinRestoreOutcome.Failed, Assert.Single(await tooNew.RunAsync(["deepsea"])).Outcome);
        Assert.False(HasSkin("deepsea"));

        // The page's id is "user:<folder>": a skin installed into another folder would not be the one the settings ask for.
        var other = Restore(Index(Entry("deepsea", zip, folder: "tiefsee")), _key, files);
        Assert.Equal(SkinRestoreOutcome.Failed, Assert.Single(await other.RunAsync(["deepsea"])).Outcome);
        Assert.False(Directory.Exists(Path.Combine(_dir, "skins")) && Directory.GetDirectories(Path.Combine(_dir, "skins")).Length > 0);
    }

    [Fact]
    public async Task A_catalog_without_the_skin_is_a_failure_not_a_crash()
    {
        var zip = SkinZip("nova");
        var restore = Restore(Index(Entry("nova", zip)), _key, new Dictionary<string, byte[]> { ["nova.zip"] = zip });

        Assert.Equal(SkinRestoreOutcome.Failed, Assert.Single(await restore.RunAsync(["deepsea"])).Outcome);
        Assert.False(HasSkin("deepsea"));
    }

    [Fact]
    public async Task Only_the_skins_that_moved_are_ever_requested()
    {
        var zip = SkinZip("stardust");
        var restore = Restore(Index(Entry("stardust", zip)), _key, new Dictionary<string, byte[]> { ["stardust.zip"] = zip });

        var results = await restore.RunAsync(["stardust", "../../evil", "default"]);

        Assert.Empty(results);
        Assert.Empty(_requests);
    }
    [Fact]
    public async Task A_skin_that_only_a_catalog_of_another_developer_lists_is_never_restored_and_does_not_use_up_the_attempts()
    {
        const string extraUrl = "https://skins.example.org/index.json";
        var zip = SkinZip("deepsea");
        var official = Index(Entry("someother", SkinZip("someother")));
        var extra = Index(Entry("deepsea", zip));
        var service = new CatalogService(new HttpClient(new FakeHandler(u =>
        {
            _requests.Add(u);
            var url = u.ToString();
            if (url == CatalogDefaults.IndexUrl) return Ok(official);
            if (url == CatalogDefaults.IndexUrl + ".sig") return Ok(Sign(_key, official));
            if (url == extraUrl) return Ok(extra);
            return u.AbsolutePath.EndsWith(".zip") ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) } : new HttpResponseMessage(HttpStatusCode.NotFound);
        })), _dir, () => [CatalogDefaults.IndexUrl], () => false, "de", Pem, null, () => [new CatalogSourceEntry { Url = extraUrl }]);
        var restore = new SkinRestore(_dir, service, () => App);

        // Far more often than the attempts allow: a source that lists the name cannot burn them.
        for (var i = 0; i < 6; i++)
        {
            var result = Assert.Single(await restore.RunAsync(["deepsea"]));
            Assert.Equal(SkinRestoreOutcome.Failed, result.Outcome);
        }
        Assert.True(restore.CanTry("deepsea"));
        Assert.False(HasSkin("deepsea"));
        Assert.DoesNotContain(_requests, u => u.AbsolutePath.EndsWith(".zip"));
    }

    // ---- when it may go online

    [Theory]
    [InlineData(true, false, SkinRestoreGate.Go)]
    [InlineData(true, true, SkinRestoreGate.Later)]
    [InlineData(false, false, SkinRestoreGate.Off)]
    [InlineData(false, true, SkinRestoreGate.Off)]
    public void The_restore_follows_the_update_check_and_waits_for_a_running_game(bool updateCheck, bool gameBusy, SkinRestoreGate expected) =>
        Assert.Equal(expected, SkinRestore.Gate(updateCheck, gameBusy));

    [Fact]
    public async Task The_restore_service_asks_only_the_official_source()
    {
        var zip = SkinZip("deepsea");
        var index = Index(Entry("deepsea", zip));
        var service = SkinRestore.OfficialOnlyService(new HttpClient(new FakeHandler(u =>
        {
            _requests.Add(u);
            if (u.AbsolutePath.EndsWith(".sig")) return Ok(Sign(_key, index));
            if (u.AbsolutePath.EndsWith("index.json")) return Ok(index);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) };
        })), _dir, () => false, () => "de", Pem);

        var loaded = await service.LoadAsync();

        Assert.NotEmpty(loaded.Entries);
        Assert.All(loaded.Entries, e => Assert.False(e.Extra));
        var official = new Uri(CatalogDefaults.IndexUrl);
        Assert.NotEmpty(_requests);
        Assert.All(_requests, u => Assert.Equal(official.Host, u.Host));
    }

    [Fact]
    public async Task Two_services_on_one_cache_file_do_not_lose_entries_when_writing_together()
    {
        var index = Index(Entry("deepsea", SkinZip("deepsea")));
        HttpClient Client() => new(new FakeHandler(u => u.AbsolutePath.EndsWith(".sig") ? Ok(Sign(_key, index)) : Ok(index)));
        var a = SkinRestore.OfficialOnlyService(Client(), _dir, () => false, () => "de", Pem);
        var b = SkinRestore.OfficialOnlyService(Client(), _dir, () => false, () => "de", Pem);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => (i % 2 == 0 ? a : b).LoadAsync()));
        var cached = await a.LoadAsync(cacheOnly: true);
        Assert.NotEmpty(cached.Entries);
    }
}
