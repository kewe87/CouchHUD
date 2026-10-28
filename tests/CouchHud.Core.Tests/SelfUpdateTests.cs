using System.Net;
using System.Security.Cryptography;
using System.Text;
using CouchHud.Core.Plugins;
using CouchHud.Core.Updates;

namespace CouchHud.Core.Tests;

public class SelfUpdateTests : IDisposable
{
    private const string Base = "https://github.com/kewe87/CouchHUD/releases/download/v0.4.0/";
    private const string Setup = "CouchHud-Setup-0.4.0.exe";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-selfupdate-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly byte[] SetupBytes = Encoding.UTF8.GetBytes("pretend this is a setup");

    public SelfUpdateTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _key.Dispose();
        _otherKey.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Pem => _key.ExportSubjectPublicKeyInfoPem();

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static string SumFile(byte[] setup, string name = Setup) => $"{Sha(setup)}  {name}\n";

    private static string Sign(ECDsa key, string text, string context = CatalogSignature.ReleaseContext) =>
        Convert.ToBase64String(key.SignData(CatalogSignature.WithContext(context, Encoding.UTF8.GetBytes(text)), HashAlgorithmName.SHA256));

    private static string ReleaseJson(string tag = "v0.4.0", bool draft = false, bool prerelease = false, string body = "## Neu\n- **Self-Update**", string[]? assets = null, string host = Base)
    {
        var names = assets ?? [$"CouchHud-Setup-{tag.TrimStart('v')}.exe", $"CouchHud-Setup-{tag.TrimStart('v')}.exe.sha256", $"CouchHud-Setup-{tag.TrimStart('v')}.exe.sha256.sig"];
        var list = string.Join(",", names.Select(n => $$"""{ "name": "{{n}}", "size": 23, "browser_download_url": "{{host}}{{n}}" }"""));
        return $$"""
            { "tag_name": "{{tag}}", "draft": {{draft.ToString().ToLowerInvariant()}}, "prerelease": {{prerelease.ToString().ToLowerInvariant()}},
              "html_url": "https://github.com/kewe87/CouchHUD/releases/tag/{{tag}}", "published_at": "2026-10-01T10:00:00Z",
              "body": {{System.Text.Json.JsonSerializer.Serialize(body)}}, "assets": [{{list}}] }
            """;
    }

    // ---- release JSON

    [Fact]
    public void Release_is_parsed_with_setup_checksum_and_signature()
    {
        var r = ReleaseParser.Parse(ReleaseJson())!;
        Assert.Equal(new SemVer(0, 4, 0, ""), r.Version);
        Assert.Equal(Setup, r.SetupName);
        Assert.Equal(Base + Setup, r.SetupUrl);
        Assert.Equal(Base + Setup + ".sha256", r.ChecksumUrl);
        Assert.Equal(Base + Setup + ".sha256.sig", r.SignatureUrl);
        Assert.True(r.Installable);
        Assert.Null(r.Problem);
        Assert.Equal("https://github.com/kewe87/CouchHUD/releases/tag/v0.4.0", r.PageUrl);
        Assert.Equal("Neu\n• Self-Update", r.Notes);
    }

    [Fact]
    public void Drafts_and_unversioned_tags_are_ignored_and_garbage_is_an_error()
    {
        Assert.Null(ReleaseParser.Parse(ReleaseJson(draft: true)));
        Assert.Null(ReleaseParser.Parse("""{ "tag_name": "nightly", "assets": [] }"""));
        Assert.Null(ReleaseParser.Parse("[]"));
        Assert.Throws<CatalogException>(() => ReleaseParser.Parse("<html>"));
    }

    [Fact]
    public void Only_the_setup_of_this_tag_counts()
    {
        // A release that carries the file of another version (or other files) has no setup.
        var wrong = ReleaseParser.Parse(ReleaseJson(assets: ["CouchHud-Setup-0.3.0.exe", "CouchHud-Setup-0.3.0.exe.sha256", "CouchHud-Setup-0.3.0.exe.sha256.sig", "notes.txt"]))!;
        Assert.False(wrong.Installable);
        Assert.Null(wrong.SetupUrl);
        Assert.Contains("kein Setup", wrong.Problem);

        var extras = ReleaseParser.Parse(ReleaseJson(assets: ["readme.txt", Setup, Setup + ".sha256", Setup + ".sha256.sig", "other.exe"]))!;
        Assert.True(extras.Installable);
        Assert.Equal(Setup, extras.SetupName);
    }

    [Fact]
    public void A_release_without_checksum_or_signature_is_listed_but_not_installable()
    {
        var unsigned = ReleaseParser.Parse(ReleaseJson(assets: [Setup, Setup + ".sha256"]))!;
        Assert.False(unsigned.Installable);
        Assert.NotNull(unsigned.Problem);
        Assert.Equal(Base + Setup, unsigned.SetupUrl);

        Assert.False(ReleaseParser.Parse(ReleaseJson(assets: [Setup]))!.Installable);
    }

    [Fact]
    public void Assets_from_other_hosts_or_repositories_are_dropped()
    {
        Assert.False(ReleaseParser.Parse(ReleaseJson(host: "https://evil.example/kewe87/CouchHUD/releases/download/v0.4.0/"))!.Installable);
        Assert.False(ReleaseParser.Parse(ReleaseJson(host: "https://github.com/someone/else/releases/download/v0.4.0/"))!.Installable);
        Assert.False(ReleaseParser.Parse(ReleaseJson(host: "http://github.com/kewe87/CouchHUD/releases/download/v0.4.0/"))!.Installable);
    }

    [Fact]
    public void Release_notes_become_short_plain_text()
    {
        const string md = "## What's new\r\n\r\n- **Skins** from the [catalog](https://x.example/a)\r\n* `Wächter` fixed <!-- hidden -->\r\n![img](https://x/y.png)\r\n\r\n\r\n__Full Changelog__: https://github.com/a/b/compare/v1...v2";
        var text = ReleaseNotes.ToPlainText(md, 500);
        Assert.Equal("What's new\n\n• Skins from the catalog\n• Wächter fixed\n\nFull Changelog: https://github.com/a/b/compare/v1...v2", text);

        var long_ = ReleaseNotes.ToPlainText(string.Join(" ", Enumerable.Repeat("wort", 400)), 100);
        Assert.True(long_.Length <= 102);
        Assert.EndsWith("…", long_);
    }

    // ---- version comparison

    [Theory]
    [InlineData("v0.4.0", false, "0.3.0", true)]
    [InlineData("v0.3.0", false, "0.3.0", false)]
    [InlineData("v0.2.9", false, "0.3.0", false)]
    [InlineData("v0.3.1", false, "0.3.0-beta.1", true)]
    [InlineData("v0.3.0", false, "0.3.0-beta.1", true)]   // the release of a beta is an update for it
    [InlineData("v0.4.0-beta.1", true, "0.3.0", false)]    // a stable install is not moved to a beta
    [InlineData("v0.4.0-beta.2", true, "0.4.0-beta.1", true)]
    [InlineData("v0.4.0-beta.1", true, "0.4.0-beta.2", false)]
    [InlineData("v0.10.0", false, "0.9.0", true)]
    public void Update_decision(string tag, bool prerelease, string app, bool expected)
    {
        var release = ReleaseParser.Parse(ReleaseJson(tag: tag, prerelease: prerelease))!;
        Assert.True(SemVer.TryParse(app, out var running));
        Assert.Equal(expected, SelfUpdateRules.IsUpdate(release, running));
    }

    // ---- hosts

    [Theory]
    [InlineData("https://github.com/kewe87/CouchHUD/releases/download/v1/x.exe", true)]
    [InlineData("https://GitHub.com/kewe87/CouchHUD/releases/download/v1/x.exe", true)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/1/2?x=y", true)]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/1/2?x=y", true)]
    [InlineData("http://github.com/kewe87/CouchHUD/releases/download/v1/x.exe", false)]
    [InlineData("https://github.com.evil.example/x.exe", false)]
    [InlineData("https://evilgithub.com/x.exe", false)]
    [InlineData("https://githubusercontent.com/x.exe", false)]
    [InlineData("https://raw.githubusercontent.com/kewe87/CouchHUD/main/x.exe", false)]
    [InlineData("https://user:pw@github.com/x.exe", false)]
    [InlineData("https://github.com:8443/x.exe", false)]
    [InlineData("file:///C:/x.exe", false)]
    public void Download_hosts_are_an_allowlist(string url, bool allowed) =>
        Assert.Equal(allowed, SelfUpdateRules.IsAllowedHost(new Uri(url)));

    [Theory]
    [InlineData("https://github.com/kewe87/CouchHUD/releases/download/v1/x.exe", true)]
    [InlineData("https://github.com/someone/else/releases/download/v1/x.exe", false)]
    [InlineData("https://github.com/kewe87/CouchHUD/archive/main.zip", false)]
    [InlineData("https://objects.githubusercontent.com/kewe87/CouchHUD/releases/download/v1/x.exe", false)]
    [InlineData(null, false)]
    [InlineData("kein url", false)]
    public void The_first_download_url_must_be_a_release_file_of_this_repository(string? url, bool allowed) =>
        Assert.Equal(allowed, SelfUpdateRules.IsAllowedAssetUrl(url));

    // ---- signature and hash

    [Fact]
    public void Checksum_file_signature_is_valid_only_for_the_exact_bytes_and_key()
    {
        var sum = SumFile(SetupBytes);
        var bytes = Encoding.UTF8.GetBytes(sum);
        ReleaseIntegrity.VerifySignature(bytes, Sign(_key, sum), Pem);

        Assert.Throws<CatalogException>(() => ReleaseIntegrity.VerifySignature(bytes, Sign(_otherKey, sum), Pem));
        Assert.Throws<CatalogException>(() => ReleaseIntegrity.VerifySignature(Encoding.UTF8.GetBytes(sum + " "), Sign(_key, sum), Pem));
        Assert.Throws<CatalogException>(() => ReleaseIntegrity.VerifySignature(bytes, "not base64!", Pem));
        // A catalog signature (same key, same bytes) is no release signature.
        Assert.Throws<CatalogException>(() => ReleaseIntegrity.VerifySignature(bytes, Sign(_key, sum, CatalogSignature.CatalogContext), Pem));
        Assert.Throws<CatalogException>(() => ReleaseIntegrity.VerifySignature(bytes, null, Pem));
        Assert.Throws<CatalogException>(() => ReleaseIntegrity.VerifySignature(bytes, "  ", Pem));
    }

    [Fact]
    public void The_built_in_key_rejects_a_signature_made_with_another_key()
    {
        var sum = SumFile(SetupBytes);
        Assert.Throws<CatalogException>(() => ReleaseIntegrity.VerifySignature(Encoding.UTF8.GetBytes(sum), Sign(_key, sum)));
    }

    [Fact]
    public void Checksum_line_must_name_this_setup()
    {
        var hash = Sha(SetupBytes);
        Assert.Equal(hash, ReleaseIntegrity.ParseChecksum(Encoding.UTF8.GetBytes($"{hash}  {Setup}\n"), Setup));
        Assert.Equal(hash, ReleaseIntegrity.ParseChecksum(Encoding.UTF8.GetBytes($"{hash.ToUpperInvariant()}  {Setup}\r\n\r\n"), Setup));

        // Strict: exactly one line, exactly this name.
        Assert.Throws<CatalogException>(() => ReleaseIntegrity.ParseChecksum(Encoding.UTF8.GetBytes($"{Sha([1])}  other.exe\n{hash}  {Setup}\n"), Setup));
        Assert.Throws<CatalogException>(() => ReleaseIntegrity.ParseChecksum(Encoding.UTF8.GetBytes($"{hash} *{Setup}\n"), Setup));
        Assert.Throws<CatalogException>(() => ReleaseIntegrity.ParseChecksum(Encoding.UTF8.GetBytes($"{hash}  dir/{Setup}\n"), Setup));
        Assert.Throws<CatalogException>(() => ReleaseIntegrity.ParseChecksum(Encoding.UTF8.GetBytes($"{hash}  {Setup} extra\n"), Setup));
        Assert.Throws<CatalogException>(() => ReleaseIntegrity.ParseChecksum([], Setup));

        Assert.Throws<CatalogException>(() => ReleaseIntegrity.ParseChecksum(Encoding.UTF8.GetBytes($"{hash}  other.exe\n"), Setup));
        Assert.Throws<CatalogException>(() => ReleaseIntegrity.ParseChecksum(Encoding.UTF8.GetBytes($"{hash}\n"), Setup));
        Assert.Throws<CatalogException>(() => ReleaseIntegrity.ParseChecksum(Encoding.UTF8.GetBytes("nonsense"), Setup));
        Assert.Throws<CatalogException>(() => ReleaseIntegrity.ParseChecksum(Encoding.UTF8.GetBytes($"{hash}  {Setup}\n{Sha([2])}  {Setup}\n"), Setup));
    }

    [Fact]
    public void Hash_check_accepts_the_right_bytes_only()
    {
        using var good = new MemoryStream(SetupBytes);
        ReleaseIntegrity.VerifyHash(good, Sha(SetupBytes));
        Assert.Equal(0, good.Position);

        using var changed = new MemoryStream([.. SetupBytes, 0]);
        Assert.Throws<CatalogException>(() => ReleaseIntegrity.VerifyHash(changed, Sha(SetupBytes)));
    }

    // ---- download (fake GitHub)

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Bytes(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };

    private static HttpResponseMessage Redirect(string to) => new(HttpStatusCode.Found) { Headers = { Location = new Uri(to) } };

    private (SelfUpdateService Service, FakeHandler Handler) Service(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        return (new SelfUpdateService(new HttpClient(handler), _dir, null, Pem), handler);
    }

    private ReleaseInfo Release() => ReleaseParser.Parse(ReleaseJson())!;

    /// <summary>A fake release: setup (maybe redirected to the CDN), sum and signature.</summary>
    private Func<HttpRequestMessage, HttpResponseMessage> Github(byte[]? setup = null, string? sum = null, string? signature = null, string? cdn = "https://release-assets.githubusercontent.com/blob/1")
    {
        setup ??= SetupBytes;
        sum ??= SumFile(SetupBytes);
        signature ??= Sign(_key, sum);
        return request => request.RequestUri!.AbsoluteUri switch
        {
            var u when u == Base + Setup => cdn is null ? Bytes(setup) : Redirect(cdn),
            var u when u == cdn => Bytes(setup),
            var u when u == Base + Setup + ".sha256" => Bytes(Encoding.UTF8.GetBytes(sum)),
            var u when u == Base + Setup + ".sha256.sig" => Bytes(Encoding.ASCII.GetBytes(signature)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };
    }

    [Fact]
    public async Task A_signed_release_downloads_through_the_cdn_redirect()
    {
        var (service, handler) = Service(Github());
        using var verified = await service.DownloadAsync(Release(), Path.Combine(_dir, "tmp"));
        Assert.Equal(SetupBytes, File.ReadAllBytes(verified.Path));
        Assert.Contains(handler.Requests, u => u.Host == "release-assets.githubusercontent.com");
        // The file is held: it can't be swapped before it is started.
        Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(verified.Path, [1, 2, 3]));
        Assert.ThrowsAny<IOException>(() => File.Delete(verified.Path));
    }

    [Fact]
    public async Task A_cancel_after_the_download_returned_stops_the_hand_over()
    {
        // The last byte and the hash are through, then the cancel arrives: it must win (true) and the hand-over must be refused.
        var (service, _) = Service(Github(cdn: null));
        using var cancel = new DownloadCancel();
        var verified = await service.DownloadAsync(Release(), Path.Combine(_dir, "tmp"), cancel.Token);
        Assert.True(cancel.Open);
        Assert.True(cancel.Cancel());
        Assert.True(cancel.Cancelled);
        Assert.True(cancel.Token.IsCancellationRequested);
        Assert.False(cancel.Open);
        Assert.False(cancel.TryHandOver());
        Assert.False(cancel.Cancel());
        verified.Dispose();
    }

    [Fact]
    public async Task A_cancel_after_the_hand_over_began_reports_false()
    {
        var (service, _) = Service(Github(cdn: null));
        using var cancel = new DownloadCancel();
        using var verified = await service.DownloadAsync(Release(), Path.Combine(_dir, "tmp"), cancel.Token);
        Assert.True(cancel.TryHandOver());
        Assert.False(cancel.Cancel());
        Assert.False(cancel.Cancelled);
        Assert.False(cancel.Token.IsCancellationRequested);
        Assert.False(cancel.TryHandOver());
    }

    [Fact]
    public void A_cancel_after_the_end_reports_false()
    {
        var cancel = new DownloadCancel();
        cancel.Dispose();
        Assert.False(cancel.Cancel());
        Assert.False(cancel.TryHandOver());
    }

    /// <summary>A body that, mid-read, is cancelled and then breaks the way a closed connection does.</summary>
    private sealed class BreakingStream(CancellationTokenSource cancel) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            cancel.Cancel();
            throw new IOException("connection closed");
        }
    }

    [Fact]
    public async Task A_cancel_that_breaks_the_connection_is_a_cancel_not_a_failed_download()
    {
        using var cancel = new CancellationTokenSource();
        var github = Github(cdn: null);
        var (service, _) = Service(request => request.RequestUri!.AbsoluteUri == Base + Setup
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BreakingStream(cancel)) }
            : github(request));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DownloadAsync(Release(), Path.Combine(_dir, "tmp"), cancel.Token));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_dir, "tmp")));
    }

    [Fact]
    public async Task Disposing_removes_the_setup()
    {
        var (service, _) = Service(Github(cdn: null));
        var verified = await service.DownloadAsync(Release(), Path.Combine(_dir, "tmp"));
        var path = verified.Path;
        verified.Dispose();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task A_setup_with_a_different_hash_is_rejected_and_deleted()
    {
        // A longer file already fails the size test, a same-size swap the hash.
        var (service, _) = Service(Github(setup: [.. SetupBytes, 0x21]));
        var tampered = SetupBytes.ToArray();
        tampered[0] ^= 1;
        var (service2, _) = Service(Github(setup: tampered));
        await Assert.ThrowsAsync<CatalogException>(() => service.DownloadAsync(Release(), Path.Combine(_dir, "tmp")));
        var ex = await Assert.ThrowsAsync<CatalogException>(() => service2.DownloadAsync(Release(), Path.Combine(_dir, "tmp")));
        Assert.Contains("Prüfsumme", ex.Message);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "tmp")));
    }

    [Fact]
    public async Task A_signature_of_another_key_is_rejected_before_the_setup_is_downloaded()
    {
        var sum = SumFile(SetupBytes);
        var (service, handler) = Service(Github(signature: Sign(_otherKey, sum)));
        var ex = await Assert.ThrowsAsync<CatalogException>(() => service.DownloadAsync(Release(), Path.Combine(_dir, "tmp")));
        Assert.Contains("Signatur", ex.Message);
        Assert.DoesNotContain(handler.Requests, u => u.AbsoluteUri == Base + Setup);
    }

    [Fact]
    public async Task A_missing_signature_file_blocks_the_install()
    {
        var github = Github();
        var (service, handler) = Service(r => r.RequestUri!.AbsoluteUri.EndsWith(".sig") ? new HttpResponseMessage(HttpStatusCode.NotFound) : github(r));
        await Assert.ThrowsAsync<CatalogException>(() => service.DownloadAsync(Release(), Path.Combine(_dir, "tmp")));
        Assert.DoesNotContain(handler.Requests, u => u.AbsoluteUri == Base + Setup);

        // A signature that is empty or not Base64 is the same as none.
        var (service2, _) = Service(Github(signature: "-"));
        await Assert.ThrowsAsync<CatalogException>(() => service2.DownloadAsync(Release(), Path.Combine(_dir, "tmp")));
    }

    [Fact]
    public async Task A_checksum_that_names_another_file_is_rejected()
    {
        var sum = SumFile(SetupBytes, "CouchHud-Setup-0.3.0.exe");
        var (service, _) = Service(Github(sum: sum));
        await Assert.ThrowsAsync<CatalogException>(() => service.DownloadAsync(Release(), Path.Combine(_dir, "tmp")));
    }

    [Theory]
    [InlineData("https://evil.example/setup.exe")]
    [InlineData("http://objects.githubusercontent.com/blob/1")]
    [InlineData("https://github.com.evil.example/setup.exe")]
    public async Task A_redirect_to_any_other_host_is_refused(string target)
    {
        var (service, handler) = Service(Github(cdn: target));
        var ex = await Assert.ThrowsAsync<CatalogException>(() => service.DownloadAsync(Release(), Path.Combine(_dir, "tmp")));
        Assert.Contains("Weiterleitung", ex.Message);
        Assert.DoesNotContain(handler.Requests, u => u.AbsoluteUri == target);
    }

    [Fact]
    public async Task A_not_yet_signed_release_cannot_be_downloaded()
    {
        var (service, handler) = Service(Github());
        var unsigned = ReleaseParser.Parse(ReleaseJson(assets: [Setup, Setup + ".sha256"]))!;
        await Assert.ThrowsAsync<CatalogException>(() => service.DownloadAsync(unsigned, Path.Combine(_dir, "tmp")));
        Assert.Empty(handler.Requests);
    }

    // ---- check (ETag, rate limit)

    [Fact]
    public async Task The_check_remembers_the_etag_and_uses_the_cache_on_304()
    {
        var calls = 0;
        string? sentTag = null;
        var (service, _) = Service(request =>
        {
            calls++;
            sentTag = request.Headers.TryGetValues("If-None-Match", out var v) ? v.First() : null;
            if (calls == 1)
            {
                var ok = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReleaseJson()) };
                ok.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"abc\"");
                return ok;
            }
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        });
        var app = new SemVer(0, 3, 0, "");

        var first = await service.CheckAsync(app);
        Assert.True(first.UpdateAvailable);
        Assert.Equal("v0.4.0", first.Release!.Tag);
        Assert.Null(first.Error);
        Assert.Null(sentTag);

        var second = await service.CheckAsync(app);
        Assert.Equal("\"abc\"", sentTag);
        Assert.True(second.UpdateAvailable);
        Assert.False(second.FromCache);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task The_rate_limit_pauses_requests_and_keeps_showing_the_cached_release()
    {
        var calls = 0;
        var clock = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
        var handler = new FakeHandler(_ =>
        {
            calls++;
            if (calls == 1) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReleaseJson()) };
            var limited = new HttpResponseMessage(HttpStatusCode.Forbidden);
            limited.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
            limited.Headers.TryAddWithoutValidation("X-RateLimit-Reset", clock.AddMinutes(30).ToUnixTimeSeconds().ToString());
            return limited;
        });
        var service = new SelfUpdateService(new HttpClient(handler), _dir, () => clock, Pem);
        var app = new SemVer(0, 3, 0, "");

        Assert.True((await service.CheckAsync(app)).UpdateAvailable);
        clock = clock.AddMinutes(1);
        var limitedResult = await service.CheckAsync(app);
        Assert.Equal(2, calls);
        Assert.True(limitedResult.FromCache);
        Assert.True(limitedResult.UpdateAvailable);
        Assert.Contains("Limit", limitedResult.Error);
        Assert.Equal("rateLimit", limitedResult.ErrorCode);

        clock = clock.AddMinutes(5);
        await service.CheckAsync(app);
        Assert.Equal(2, calls);   // still waiting, no request

        clock = clock.AddMinutes(30);
        await service.CheckAsync(app);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Offline_without_a_cache_is_an_error_not_an_exception()
    {
        var (service, _) = Service(_ => throw new HttpRequestException("offline"));
        var result = await service.CheckAsync(new SemVer(0, 3, 0, ""));
        Assert.False(result.UpdateAvailable);
        Assert.Null(result.Release);
        Assert.NotNull(result.Error);
        Assert.Equal("unreachable", result.ErrorCode);
    }

    [Fact]
    public async Task No_release_yet_is_no_update()
    {
        var (service, _) = Service(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var result = await service.CheckAsync(new SemVer(0, 3, 0, ""));
        Assert.False(result.UpdateAvailable);
        Assert.Equal(SelfUpdateRules.NoReleaseYet, result.Error);
        Assert.Equal("noRelease", result.ErrorCode);
    }
}
