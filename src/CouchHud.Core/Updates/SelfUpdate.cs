using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CouchHud.Core.Plugins;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Updates;

/// <summary>
/// One CouchHUD release of GitHub. <see cref="Problem"/> set means it is listed but cannot be installed
/// (no setup, or checksum/signature not uploaded yet).
/// </summary>
public sealed record ReleaseInfo(
    string Tag,
    SemVer Version,
    bool Prerelease,
    /// <summary>Release notes as short plain text.</summary>
    string Notes,
    string? PageUrl,
    long? PublishedAt,
    string? SetupName,
    string? SetupUrl,
    long SetupSize,
    string? ChecksumUrl,
    string? SignatureUrl,
    string? Problem)
{
    public bool Installable => Problem is null && SetupUrl is not null && ChecksumUrl is not null && SignatureUrl is not null;
}

/// <summary>What the last look at GitHub found. ErrorCode (noRelease, rateLimit, noAnswer, unreachable) lets the UI translate the error itself; Error is the text in the language of the moment.</summary>
public sealed record SelfUpdateCheck(ReleaseInfo? Release, bool UpdateAvailable, bool FromCache, long CheckedAt, string? Error, string? ErrorCode = null);

/// <summary>Where a setup may come from, and which release counts as an update.</summary>
public static class SelfUpdateRules
{
    public const string Repository = "kewe87/CouchHUD";

    /// <summary>Shown when GitHub has no release at all (404), which is not the same as "up to date".</summary>
    public static string NoReleaseYet => Strings.Get("selfupdate.noRelease");
    public const string ApiUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
    public const string SetupPrefix = "CouchHud-Setup-";

    /// <summary>GitHub serves release files from github.com and redirects to one of its asset hosts (release-assets… since 2025).</summary>
    private static readonly string[] Hosts = ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];

    /// <summary>https, no credentials, the default port and one of the GitHub hosts.</summary>
    public static bool IsAllowedHost(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.IsDefaultPort
        && Hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>The first URL of a download: a release file of this repository on github.com.</summary>
    public static bool IsAllowedAssetUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsAllowedHost(uri)
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith($"/{Repository}/releases/download/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Newer than what runs. A pre-release is only offered to someone who already runs one; a stable
    /// version never "updates" to a beta.
    /// </summary>
    public static bool IsUpdate(ReleaseInfo release, SemVer app)
    {
        if ((release.Prerelease || release.Version.Pre.Length > 0) && app.Pre.Length == 0) return false;
        return release.Version > app;
    }
}

/// <summary>Reads the answer of <c>/releases/latest</c>.</summary>
public static class ReleaseParser
{
    public const int MaxNotesChars = 700;

    /// <returns>The release, or null for a draft or a tag that is no version.</returns>
    public static ReleaseInfo? Parse(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException ex)
        {
            throw new CatalogException(Strings.Get("selfupdate.invalidJson", ex.Message));
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (Bool(root, "draft")) return null;
            var tag = Str(root, "tag_name");
            if (tag is null || !SemVer.TryParse(tag, out var version)) return null;

            var setupName = SelfUpdateRules.SetupPrefix + tag.TrimStart('v', 'V') + ".exe";
            var assets = new Dictionary<string, (string Url, long Size)>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in list.EnumerateArray())
                {
                    var name = Str(a, "name");
                    var url = Str(a, "browser_download_url");
                    if (name is null || url is null || !SelfUpdateRules.IsAllowedAssetUrl(url)) continue;
                    assets.TryAdd(name, (url, a.TryGetProperty("size", out var s) && s.TryGetInt64(out var size) ? size : 0));
                }
            }

            string? problem = null;
            assets.TryGetValue(setupName, out var setup);
            assets.TryGetValue(setupName + ".sha256", out var sum);
            assets.TryGetValue(setupName + ".sha256.sig", out var sig);
            if (setup.Url is null) problem = Strings.Get("selfupdate.noSetup");
            else if (sum.Url is null || sig.Url is null) problem = Strings.Get("selfupdate.preparing");

            DateTimeOffset? published = DateTimeOffset.TryParse(Str(root, "published_at"), out var p) ? p : null;
            var page = Str(root, "html_url");
            return new ReleaseInfo(tag, version, Bool(root, "prerelease"), ReleaseNotes.ToPlainText(Str(root, "body") ?? "", MaxNotesChars),
                page is not null && Uri.TryCreate(page, UriKind.Absolute, out var pu) && pu.Scheme == Uri.UriSchemeHttps && pu.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ? page : null,
                published?.ToUnixTimeMilliseconds(),
                setup.Url is null ? null : setupName, setup.Url, setup.Size, sum.Url, sig.Url, problem);
        }
    }

    private static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;

    private static bool Bool(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>Release notes are Markdown; the TV shows a few lines of plain text.</summary>
public static partial class ReleaseNotes
{
    public static string ToPlainText(string markdown, int maxChars)
    {
        var text = HtmlComment().Replace(markdown.Replace("\r\n", "\n"), "");
        text = Image().Replace(text, "");
        text = Link().Replace(text, "$1");
        text = HtmlTag().Replace(text, "");
        var lines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("```")) continue;
            line = Heading().Replace(line, "");
            line = Bullet().Replace(line, "• ");
            line = line.Replace("**", "").Replace("__", "").Replace("`", "");
            line = Emphasis().Replace(line, "$1");
            line = line.Trim();
            if (line.Length == 0 && (lines.Count == 0 || lines[^1].Length == 0)) continue;
            lines.Add(line);
        }
        var result = string.Join('\n', lines).Trim();
        if (result.Length <= maxChars) return result;
        var cut = result[..maxChars];
        var space = cut.LastIndexOfAny([' ', '\n']);
        return (space > maxChars / 2 ? cut[..space] : cut).TrimEnd() + " …";
    }

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)] private static partial Regex HtmlComment();
    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")] private static partial Regex Image();
    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")] private static partial Regex Link();
    [GeneratedRegex(@"</?[A-Za-z][^>]*>")] private static partial Regex HtmlTag();
    [GeneratedRegex(@"^#{1,6}\s*")] private static partial Regex Heading();
    [GeneratedRegex(@"^[-*+]\s+")] private static partial Regex Bullet();
    [GeneratedRegex(@"(?<![\w*])[*_]([^*_\n]+)[*_](?![\w*])")] private static partial Regex Emphasis();
}

/// <summary>
/// Checksum file and signature of a release. The setup is trusted when the signature of the
/// <c>.sha256</c> file is valid (key: <see cref="CatalogSignature"/>), the file names this setup
/// and the SHA-256 of the downloaded bytes is the one in it.
/// </summary>
public static class ReleaseIntegrity
{
    private static readonly Regex Line = new(@"^([0-9a-fA-F]{64})  (\S+)$", RegexOptions.Compiled);

    public static void VerifySignature(byte[] checksumFile, string? signatureBase64, string? publicKeyPem = null)
    {
        if (string.IsNullOrWhiteSpace(signatureBase64)) throw new CatalogException(Strings.Get("selfupdate.noSignature"));
        if (!CatalogSignature.Verify(checksumFile, signatureBase64, publicKeyPem, CatalogSignature.ReleaseContext)) throw new CatalogException(Strings.Get("selfupdate.badSignature"));
    }

    /// <summary>
    /// The hash in a <c>sha256sum</c> style file. Strict: exactly one non-empty line, <c>&lt;64 hex&gt;  &lt;setup name&gt;</c>,
    /// and the name is exactly this setup (no path, no other files).
    /// </summary>
    public static string ParseChecksum(byte[] checksumFile, string setupName)
    {
        var lines = Encoding.UTF8.GetString(checksumFile).Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        if (lines.Count != 1) throw new CatalogException(Strings.Get("selfupdate.sumOneLine"));
        var m = Line.Match(lines[0]);
        if (!m.Success) throw new CatalogException(Strings.Get("selfupdate.sumFormat"));
        if (!m.Groups[2].Value.Equals(setupName, StringComparison.OrdinalIgnoreCase)) throw new CatalogException(Strings.Get("selfupdate.sumWrongFile", setupName));
        return m.Groups[1].Value.ToLowerInvariant();
    }

    /// <summary>Hashes the stream from the start (it is left at the start again).</summary>
    public static void VerifyHash(Stream setup, string expectedHex)
    {
        setup.Position = 0;
        var actual = Convert.ToHexString(SHA256.HashData(setup));
        setup.Position = 0;
        if (!actual.Equals(expectedHex, StringComparison.OrdinalIgnoreCase)) throw new CatalogException(Strings.Get("catalog.checksumMismatch"));
    }
}

/// <summary>
/// The one cancel of a setup download, raced against the hand-over: <see cref="Cancel"/> and <see cref="TryHandOver"/> take the same slot,
/// so a cancel either really stops the update (true, and the hand-over is refused) or comes too late and says so (false), never both.
/// </summary>
public sealed class DownloadCancel : IDisposable
{
    private readonly CancellationTokenSource _source = new();
    private CancellationTokenSource? _open;
    private volatile bool _cancelled;

    public DownloadCancel() => _open = _source;

    public CancellationToken Token => _source.Token;

    /// <summary>Neither cancelled nor handed over yet.</summary>
    public bool Open => Volatile.Read(ref _open) is not null;

    /// <summary>A cancel took the slot (the token may follow a moment later).</summary>
    public bool Cancelled => _cancelled;

    /// <summary>Cancels the download; false once the hand-over has begun or it was already cancelled.</summary>
    public bool Cancel()
    {
        var source = Interlocked.Exchange(ref _open, null);
        if (source is null) return false;
        _cancelled = true;
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The download already ended and saw the cancel through the slot.
        }
        return true;
    }

    /// <summary>Takes the slot for the hand-over; false when a cancel came first (then nothing may be handed over).</summary>
    public bool TryHandOver() => Interlocked.Exchange(ref _open, null) is not null && !_source.IsCancellationRequested;

    public void Dispose()
    {
        Volatile.Write(ref _open, null);
        _source.Dispose();
    }
}

/// <summary>
/// A downloaded setup whose signature and hash passed. The file stays open (reading allowed, writing
/// and deleting refused) until this is disposed, so nothing can swap it before it is started.
/// </summary>
public sealed class VerifiedSetup(string path, FileStream lockHandle, string sha256, byte[]? checksumFile = null, string? signature = null) : IDisposable
{
    public string Path { get; } = path;

    /// <summary>The verified SHA-256 (lower-case hex).</summary>
    public string Sha256 { get; } = sha256;

    /// <summary>The held handle (to ask Windows where the file really is).</summary>
    public Microsoft.Win32.SafeHandles.SafeFileHandle Handle => lockHandle.SafeFileHandle;

    /// <summary>
    /// The signed checksum file this setup was verified against (the CouchHUD service verifies it again, it trusts nothing the
    /// shell checked). Null where none was downloaded.
    /// </summary>
    public byte[]? ChecksumFile { get; } = checksumFile;

    /// <summary>The signature over <see cref="ChecksumFile"/> (base64 text as downloaded); null when the release has none.</summary>
    public string? Signature { get; } = signature?.Trim();

    /// <summary>The size of the verified file.</summary>
    public long Length => lockHandle.Length;

    /// <summary>
    /// The held handle itself, at the start, for reading the verified bytes (e.g. to stream them to the service). Owned by this
    /// object: do not dispose it, dispose the <see cref="VerifiedSetup"/>.
    /// </summary>
    public Stream ReadFromStart()
    {
        lockHandle.Position = 0;
        return lockHandle;
    }

    public void Dispose()
    {
        lockHandle.Dispose();
        try { File.Delete(Path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try { Directory.Delete(System.IO.Path.GetDirectoryName(Path)!); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Looks for a newer CouchHUD on GitHub (ETag cache, rate limit respected) and downloads the setup with
/// every check described in <see cref="ReleaseIntegrity"/>. The <see cref="HttpClient"/> must not follow
/// redirects on its own: every hop is checked against <see cref="SelfUpdateRules.IsAllowedHost"/> here.
/// </summary>
public sealed class SelfUpdateService(HttpClient http, string dataDir, Func<DateTimeOffset>? now = null, string? publicKeyPem = null, string apiUrl = SelfUpdateRules.ApiUrl)
{
    public static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(15);
    public static TimeSpan DownloadInactivity { get; internal set; } = TimeSpan.FromSeconds(60);
    /// <summary>The whole download, however slowly the data trickles in.</summary>
    public static TimeSpan DownloadLimit { get; internal set; } = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan ManualRetry = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(24);
    public const long MaxApiBytes = 1024 * 1024;
    public const long MaxSmallBytes = 4096;
    public const long MaxSetupBytes = 400L * 1024 * 1024;
    private const int MaxRedirects = 5;

    private readonly object _lock = new();
    private DateTimeOffset Now => (now ?? (() => DateTimeOffset.UtcNow))();
    private string CacheFile => Path.Combine(dataDir, "selfupdate-cache.json");

    private sealed class Cache
    {
        public string? ETag { get; set; }
        public string? Body { get; set; }
        public long FetchedAt { get; set; }
        /// <summary>Unix ms; no request before (rate limit).</summary>
        public long BackoffUntil { get; set; }
        public long LastRequestAt { get; set; }
        public string? Announced { get; set; }
    }

    /// <summary>The version the user was told about last (so a restart doesn't announce it again).</summary>
    public string? Announced => ReadCache().Announced;

    public void MarkAnnounced(string version)
    {
        var cache = ReadCache();
        cache.Announced = version;
        WriteCache(cache);
    }

    /// <param name="manual">The user asked: a rate-limit pause blocks for one minute at most instead of until GitHub's reset.</param>
    public async Task<SelfUpdateCheck> CheckAsync(SemVer app, CancellationToken ct = default, bool manual = false)
    {
        var cache = ReadCache();
        var nowMs = Now.ToUnixTimeMilliseconds();
        var paused = cache.BackoffUntil > nowMs && !(manual && nowMs - cache.LastRequestAt >= (long)ManualRetry.TotalMilliseconds);
        if (paused) return Describe(cache.Body, app, fromCache: true, cache.FetchedAt, Strings.Get("selfupdate.rateLimit"), "rateLimit");
        cache.LastRequestAt = nowMs;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ApiTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (cache.ETag is not null && cache.Body is not null) request.Headers.TryAddWithoutValidation("If-None-Match", cache.ETag);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            if (response.StatusCode == HttpStatusCode.NotModified && cache.Body is not null)
            {
                cache.FetchedAt = nowMs;
                WriteCache(cache);
                return Describe(cache.Body, app, fromCache: false, nowMs, null);
            }
            if (response.StatusCode == HttpStatusCode.NotFound) return new SelfUpdateCheck(null, false, false, nowMs, SelfUpdateRules.NoReleaseYet, "noRelease");
            if (IsRateLimit(response))
            {
                cache.BackoffUntil = BackoffEnd(response, nowMs);
                WriteCache(cache);
                return Describe(cache.Body, app, fromCache: true, cache.FetchedAt, Strings.Get("selfupdate.rateLimit"), "rateLimit");
            }
            response.EnsureSuccessStatusCode();
            var body = Encoding.UTF8.GetString(await ReadLimitedAsync(response, MaxApiBytes, timeout.Token));
            var result = Describe(body, app, fromCache: false, nowMs, null);   // throws on garbage, cache stays as it was
            cache.ETag = response.Headers.ETag?.ToString();
            cache.Body = body;
            cache.FetchedAt = nowMs;
            cache.BackoffUntil = 0;
            WriteCache(cache);
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or CatalogException or OperationCanceledException or IOException)
        {
            if (ct.IsCancellationRequested) throw;
            var code = ex is CatalogException ? null : ex is OperationCanceledException ? "noAnswer" : "unreachable";
            var reason = code is null ? ex.Message : Strings.Get("selfupdate." + code);
            return cache.Body is null
                ? new SelfUpdateCheck(null, false, false, nowMs, reason, code)
                : Describe(cache.Body, app, fromCache: true, cache.FetchedAt, reason, code);
        }
    }

    private static SelfUpdateCheck Describe(string? body, SemVer app, bool fromCache, long checkedAt, string? error, string? errorCode = null)
    {
        ReleaseInfo? release = null;
        if (body is not null)
        {
            try { release = ReleaseParser.Parse(body); }
            catch (CatalogException) when (fromCache) { }   // a broken cache is as good as none
        }
        return new SelfUpdateCheck(release, release is not null && SelfUpdateRules.IsUpdate(release, app), fromCache, checkedAt, error, errorCode);
    }

    /// <summary>429, or a 403 that says the quota is used up. Any other 403 is just an error and starts no pause.</summary>
    private static bool IsRateLimit(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests) return true;
        if (response.StatusCode != HttpStatusCode.Forbidden) return false;
        return response.Headers.RetryAfter is not null
               || (response.Headers.TryGetValues("X-RateLimit-Remaining", out var left) && left.FirstOrDefault()?.Trim() == "0");
    }

    /// <summary>When to ask again: what GitHub says, between one minute and <see cref="MaxBackoff"/>.</summary>
    private static long BackoffEnd(HttpResponseMessage response, long nowMs)
    {
        long end;
        if (response.Headers.RetryAfter?.Delta is { } delta) end = nowMs + (long)delta.TotalMilliseconds;
        else if (response.Headers.RetryAfter?.Date is { } date) end = date.ToUnixTimeMilliseconds();
        else if (response.Headers.TryGetValues("X-RateLimit-Reset", out var reset) && long.TryParse(reset.FirstOrDefault(), out var seconds)) end = seconds * 1000;
        else end = nowMs + 3_600_000;
        return Math.Clamp(end, nowMs + 60_000, nowMs + (long)MaxBackoff.TotalMilliseconds);
    }

    // -----------------------------------------------------------------------
    // Download

    /// <summary>
    /// Fetches the signed checksum file, verifies its signature, downloads the setup into <paramref name="directory"/>
    /// and compares its hash. Nothing is returned (and the file is deleted) unless all of it passes.
    /// </summary>
    public async Task<VerifiedSetup> DownloadAsync(ReleaseInfo release, string directory, CancellationToken ct = default)
    {
        if (!release.Installable) throw new CatalogException(release.Problem ?? Strings.Get("selfupdate.notInstallable"));
        var checksum = await ReadSmallAsync(release.ChecksumUrl!, ct);
        var signature = Encoding.ASCII.GetString(await ReadSmallAsync(release.SignatureUrl!, ct));
        ReleaseIntegrity.VerifySignature(checksum, signature, publicKeyPem);
        var expected = ReleaseIntegrity.ParseChecksum(checksum, release.SetupName!);

        Directory.CreateDirectory(directory);
        // The file keeps its name (it is the process name later); the folder is the unique part.
        var folder = Path.Combine(directory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, release.SetupName!);
        FileStream? handle = null;
        using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
        total.CancelAfter(DownloadLimit);
        try
        {
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(total.Token))
            {
                try
                {
                    idle.CancelAfter(DownloadInactivity);
                    using var response = await SendCheckedAsync(release.SetupUrl!, idle.Token);
                    if (response.Content.Headers.ContentLength > MaxSetupBytes) throw new CatalogException(Strings.Get("selfupdate.setupTooLarge"));
                    await using var stream = await response.Content.ReadAsStreamAsync(idle.Token);
                    await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    var buffer = new byte[81920];
                    long received = 0;
                    while (true)
                    {
                        idle.CancelAfter(DownloadInactivity);
                        var read = await stream.ReadAsync(buffer, idle.Token);
                        if (read <= 0) break;
                        received += read;
                        if (received > MaxSetupBytes) throw new CatalogException(Strings.Get("selfupdate.setupTooLarge"));
                        await file.WriteAsync(buffer.AsMemory(0, read), idle.Token);
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    // A cancel that broke the connection mid-read is a cancel, not a failed download.
                    if (ct.IsCancellationRequested) throw new OperationCanceledException(ex.Message, ex, ct);
                    throw new CatalogException(ex is OperationCanceledException
                        ? (total.IsCancellationRequested ? Strings.Get("selfupdate.downloadSlow") : Strings.Get("net.stalled"))
                        : Strings.Get("net.downloadFailed", ex.Message));
                }
            }

            ct.ThrowIfCancellationRequested();
            // Hash from the handle that is kept: once it is open nobody can write to the file any more.
            handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (release.SetupSize > 0 && handle.Length != release.SetupSize) throw new CatalogException(Strings.Get("selfupdate.setupSize"));
            ReleaseIntegrity.VerifyHash(handle, expected);
            // A cancel during the hash still wins here (the caller's DownloadCancel decides the race with the hand-over).
            ct.ThrowIfCancellationRequested();
            var verified = new VerifiedSetup(path, handle, expected, checksum, signature);
            handle = null;
            return verified;
        }
        catch
        {
            handle?.Dispose();
            try { Directory.Delete(folder, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private async Task<byte[]> ReadSmallAsync(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ApiTimeout);
        try
        {
            using var response = await SendCheckedAsync(url, timeout.Token);
            return await ReadLimitedAsync(response, MaxSmallBytes, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            throw new CatalogException(ex is OperationCanceledException ? Strings.Get("selfupdate.sumNoAnswer") : Strings.Get("selfupdate.sumLoadFailed", ex.Message));
        }
    }

    /// <summary>GET with redirects followed by hand: the first URL must be a release file of this repository, every later one a GitHub host.</summary>
    private async Task<HttpResponseMessage> SendCheckedAsync(string url, CancellationToken ct)
    {
        if (!SelfUpdateRules.IsAllowedAssetUrl(url)) throw new CatalogException(Strings.Get("selfupdate.githubOnly"));
        var uri = new Uri(url);
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                response.Dispose();
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (!SelfUpdateRules.IsAllowedHost(uri)) throw new CatalogException(Strings.Get("selfupdate.redirect", uri.Host));
                continue;
            }
            try
            {
                response.EnsureSuccessStatusCode();
            }
            catch
            {
                response.Dispose();
                throw;
            }
            return response;
        }
        throw new CatalogException(Strings.Get("selfupdate.tooManyRedirects"));
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, long limit, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > limit) throw new CatalogException(Strings.Get("selfupdate.responseTooLarge"));
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read <= 0) break;
            if (buffer.Length + read > limit) throw new CatalogException(Strings.Get("selfupdate.responseTooLarge"));
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    // -----------------------------------------------------------------------
    // Cache

    private Cache ReadCache()
    {
        lock (_lock)
        {
            try
            {
                return File.Exists(CacheFile) ? JsonSerializer.Deserialize<Cache>(File.ReadAllText(CacheFile)) ?? new() : new();
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                return new();
            }
        }
    }

    private void WriteCache(Cache cache)
    {
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(dataDir);
                var tmp = CacheFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(cache));
                File.Move(tmp, CacheFile, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[selfupdate] cache not written: {ex.Message}");
            }
        }
    }
}
