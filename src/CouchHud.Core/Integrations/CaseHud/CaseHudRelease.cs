using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CouchHud.Core.Localization;
using CouchHud.Core.Plugins;
using CouchHud.Core.Updates;

namespace CouchHud.Core.Integrations.CaseHud;

/// <summary>
/// The latest CaseHUD release. <see cref="Problem"/> (tooOld = below the minimum version, unverifiable, noSetup, noSignature) means it is listed
/// but must not be installed: without a checksum file nothing can be checked, and nothing is installed unchecked.
/// </summary>
public sealed record CaseHudRelease(
    string Tag, SemVer Version, bool Prerelease, string Notes, string? PageUrl,
    string? SetupName, string? SetupUrl, long SetupSize, string? SumsUrl, string? SignatureUrl, string? Problem)
{
    public bool Installable => Problem is null && SetupUrl is not null && SumsUrl is not null;
}

/// <param name="ErrorCode">noRelease, unavailable (404/401/403: private repository or no access), rateLimit, invalid, noAnswer, unreachable.</param>
public sealed record CaseHudReleaseCheck(CaseHudRelease? Release, bool UpdateAvailable, string? ErrorCode = null, string? Error = null);

public static class CaseHudVersion
{
    /// <summary>The text holds a version number at all (otherwise <see cref="Parse"/> can only guess 0.0.0).</summary>
    public static bool IsReadable(string? text) => SemVer.TryParse(text, out _) || Regex.IsMatch(text ?? "", @"\d+(\.\d+){1,3}");

    /// <summary>Tolerant: "1.1.0", "v1.1.0", "0.0.0-dev.57", "1.1.0-dev"; anything else counts as 0.0.0.</summary>
    public static SemVer Parse(string? text) =>
        SemVer.TryParse(text, out var v) ? v : SemVer.TryParse(Regex.Match(text ?? "", @"\d+(\.\d+){1,3}(-[0-9A-Za-z.-]+)?").Value, out var loose) ? loose : new SemVer(0, 0, 0, "");

    /// <summary>
    /// Offered as an update: a release that passes every check for being installed (signature file, checksum, setup, at least
    /// <see cref="CaseHudInfo.MinimumVersion"/>) and is newer than what is installed; a pre-release only for someone who already
    /// runs one. Downgrade protection is the signature (only releases signed with the built-in key are ever installed, so old
    /// unsigned builds cannot be offered) plus the monotonic version line from 1.0.0 on, not a list of version numbers.
    /// </summary>
    public static bool IsUpdate(CaseHudRelease release, SemVer installed)
    {
        if (release.Problem is not null) return false;
        if ((release.Prerelease || release.Version.Pre.Length > 0) && installed.Pre.Length == 0) return false;
        return release.Version > installed;
    }
}

public static class CaseHudReleaseParser
{
    public const int MaxNotesChars = 500;

    /// <summary>Only release files of the CaseHUD repository on github.com.</summary>
    public static bool IsAllowedAssetUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && SelfUpdateRules.IsAllowedHost(uri)
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.StartsWith($"/{CaseHudInfo.Repository}/releases/download/", StringComparison.OrdinalIgnoreCase);

    /// <param name="signatureRequired">A release public key is set: a missing signature file makes the release not installable.</param>
    /// <returns>The release, or null for a draft or a tag that is no version.</returns>
    public static CaseHudRelease? Parse(string json, bool signatureRequired)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 }); }
        catch (JsonException ex) { throw new CatalogException(Strings.Get("selfupdate.invalidJson", ex.Message)); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || Bool(root, "draft")) return null;
            var tag = Str(root, "tag_name");
            if (tag is null || !SemVer.TryParse(tag, out var version)) return null;

            var setupName = CaseHudInfo.SetupPrefix + tag.TrimStart('v', 'V') + ".exe";
            var assets = new Dictionary<string, (string Url, long Size)>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in list.EnumerateArray())
                {
                    var name = Str(a, "name");
                    var url = Str(a, "browser_download_url");
                    if (name is null || url is null || !IsAllowedAssetUrl(url)) continue;
                    assets.TryAdd(name, (url, a.TryGetProperty("size", out var s) && s.TryGetInt64(out var size) ? size : 0));
                }
            }

            assets.TryGetValue(setupName, out var setup);
            assets.TryGetValue(CaseHudInfo.ChecksumName, out var sums);
            assets.TryGetValue(CaseHudInfo.ChecksumName + ".sig", out var sig);
            var problem = version < CaseHudInfo.MinimumVersion ? "tooOld" : setup.Url is null ? "noSetup" : sums.Url is null ? "unverifiable" : signatureRequired && sig.Url is null ? "noSignature" : null;

            var page = Str(root, "html_url");
            return new CaseHudRelease(tag, version, Bool(root, "prerelease"), ReleaseNotes.ToPlainText(Str(root, "body") ?? "", MaxNotesChars),
                page is not null && Uri.TryCreate(page, UriKind.Absolute, out var pu) && pu.Scheme == Uri.UriSchemeHttps && pu.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ? page : null,
                setup.Url is null ? null : setupName, setup.Url, setup.Size, sums.Url, sig.Url, problem);
        }
    }

    private static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString()!.Trim() : null;

    private static bool Bool(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>
/// SHA256SUMS.txt (sha256sum format, several files) and the signature over it. The hash is always required; the
/// signature (ECDSA P-256 over "casehud-release-v1\n" + file) only once <see cref="CaseHudInfo.ReleasePublicKeyPem"/> is set,
/// and then without exception. The same verification code as for CouchHUD's own releases, with its own context and key.
/// </summary>
public static class CaseHudIntegrity
{
    private static readonly Regex Line = new(@"^([0-9a-fA-F]{64}) [ *](\S.*)$", RegexOptions.Compiled);

    /// <param name="publicKeyPem">Empty = signature not required.</param>
    public static void VerifySignature(byte[] sums, string? signatureBase64, string publicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(publicKeyPem)) return;
        if (string.IsNullOrWhiteSpace(signatureBase64)) throw new CatalogException(Strings.Get("selfupdate.noSignature"));
        if (!CatalogSignature.Verify(sums, signatureBase64, publicKeyPem, CaseHudInfo.ReleaseContext, DSASignatureFormat.Rfc3279DerSequence)) throw new CatalogException(Strings.Get("selfupdate.badSignature"));
    }

    /// <summary>Why a release is not installable, as text for the user.</summary>
    public static string ProblemText(CaseHudRelease release) => release.Problem switch
    {
        "tooOld" or "noSignature" => Strings.Get("casehud.noSignedRelease"),
        "unverifiable" => Strings.Get("casehud.unverifiable"),
        _ => Strings.Get("selfupdate.notInstallable")
    };

    /// <summary>The hash for <paramref name="fileName"/> from a sha256sum file (other files may be listed; the name must match exactly).</summary>
    public static string ParseChecksum(byte[] sums, string fileName)
    {
        string? found = null;
        foreach (var raw in Encoding.UTF8.GetString(sums).Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            var m = Line.Match(line);
            if (!m.Success) throw new CatalogException(Strings.Get("selfupdate.sumFormat"));
            if (!m.Groups[2].Value.Equals(fileName, StringComparison.OrdinalIgnoreCase)) continue;
            var hash = m.Groups[1].Value.ToLowerInvariant();
            if (found is not null && found != hash) throw new CatalogException(Strings.Get("selfupdate.sumFormat"));
            found = hash;
        }
        return found ?? throw new CatalogException(Strings.Get("selfupdate.sumWrongFile", fileName));
    }
}

/// <summary>
/// Asks GitHub for the latest CaseHUD release and downloads its setup with every check:
/// SHA256SUMS.txt (mandatory), signature (once a key is set), size and hash. The <see cref="HttpClient"/> must not follow
/// redirects itself: each hop is checked against the GitHub hosts here.
/// </summary>
public sealed class CaseHudReleaseService(HttpClient http, string publicKeyPem = CaseHudInfo.ReleasePublicKeyPem, string apiUrl = CaseHudInfo.ApiUrl, Func<DateTimeOffset>? now = null)
{
    public static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);
    /// <summary>After 404/401 (private repository) nothing is asked automatically for this long; "check again" by hand always asks.</summary>
    public static readonly TimeSpan UnavailableBackoff = TimeSpan.FromHours(12);
    /// <summary>A 403 without rate-limit header may be a hiccup (proxy, abuse limit): only a short pause.</summary>
    public static readonly TimeSpan ForbiddenBackoff = TimeSpan.FromMinutes(15);
    public static TimeSpan DownloadInactivity { get; internal set; } = TimeSpan.FromSeconds(60);
    public static TimeSpan DownloadLimit { get; internal set; } = TimeSpan.FromMinutes(30);
    public const long MaxApiBytes = 1024 * 1024;
    public const long MaxSmallBytes = 16384;
    public const long MaxSetupBytes = 400L * 1024 * 1024;
    private const int MaxRedirects = 5;

    private readonly object _gate = new();   // guards _cache and _unavailableUntil
    private (DateTimeOffset At, string Body)? _cache;
    private DateTimeOffset _unavailableUntil = DateTimeOffset.MinValue;
    private DateTimeOffset Now => (now ?? (() => DateTimeOffset.UtcNow))();

    public bool SignatureRequired => !string.IsNullOrWhiteSpace(publicKeyPem);
    public string PublicKeyPem => publicKeyPem;

    private static CaseHudReleaseCheck Unavailable() => new(null, false, "unavailable", Strings.Get("casehud.downloadsUnavailable"));

    /// <param name="global">The release list itself said no: pause automatic checks. A single missing asset only logs.</param>
    private void MarkUnavailable(HttpStatusCode status, bool global)
    {
        var pause = status == HttpStatusCode.Forbidden ? ForbiddenBackoff : UnavailableBackoff;
        if (global)
            lock (_gate)
            {
                _unavailableUntil = Now + pause;
                _cache = null;
            }
        // One line per real request, no matter how often the page asks.
        Console.Error.WriteLine($"[casehud] downloads unavailable (HTTP {(int)status})" + (global ? $", next automatic check in {pause.TotalMinutes:0} min" : ""));
    }

    /// <param name="installed">Version of the installed CaseHUD (tolerant, see <see cref="CaseHudVersion.Parse"/>).</param>
    /// <param name="force">A manual check: asks GitHub even inside the backoff and ignores the cache.</param>
    public async Task<CaseHudReleaseCheck> CheckAsync(string? installed, bool force = false, CancellationToken ct = default)
    {
        var version = CaseHudVersion.Parse(installed);
        // Installed, but the version cannot be read: no update hint (it would compare against 0.0.0); installing by hand stays possible.
        var versionUnknown = installed is not null && !CaseHudVersion.IsReadable(installed);
        lock (_gate)
            if (!force && Now < _unavailableUntil) return Unavailable();
        try
        {
            string body;
            (DateTimeOffset At, string Body)? cached;
            lock (_gate) cached = _cache;
            if (!force && cached is { } c && Now - c.At < CacheFor) body = c.Body;
            else
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ApiTimeout);
                using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (IsRateLimit(response)) return new CaseHudReleaseCheck(null, false, "rateLimit", Strings.Get("selfupdate.rateLimit"));
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    MarkUnavailable(response.StatusCode, global: true);
                    return Unavailable();
                }
                response.EnsureSuccessStatusCode();
                body = Encoding.UTF8.GetString(await ReadLimitedAsync(response, MaxApiBytes, timeout.Token));
                CaseHudReleaseParser.Parse(body, SignatureRequired);   // garbage must not end up in the cache
                lock (_gate)
                {
                    _cache = (Now, body);
                    _unavailableUntil = DateTimeOffset.MinValue;
                }
            }
            var release = CaseHudReleaseParser.Parse(body, SignatureRequired);
            return new CaseHudReleaseCheck(release, release is not null && !versionUnknown && CaseHudVersion.IsUpdate(release, version));
        }
        catch (Exception ex) when (ex is HttpRequestException or CatalogException or OperationCanceledException or IOException)
        {
            if (ct.IsCancellationRequested) throw;
            var code = ex is CatalogException ? "invalid" : ex is OperationCanceledException ? "noAnswer" : "unreachable";
            return new CaseHudReleaseCheck(null, false, code, ex is CatalogException ? ex.Message : Strings.Get("selfupdate." + code));
        }
    }

    /// <summary>429, or 403 with the limit used up (GitHub answers 403 for both a rate limit and a private repository).</summary>
    private static bool IsRateLimit(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.TooManyRequests
        || (response.StatusCode == HttpStatusCode.Forbidden && response.Headers.TryGetValues("X-RateLimit-Remaining", out var left) && left.FirstOrDefault() == "0");

    /// <summary>
    /// Checksum file and signature only (small files, no setup): a release is offered to the user only after its signature
    /// held, so a release without a valid signature never reaches the confirmation dialog.
    /// </summary>
    public async Task VerifySignatureAsync(CaseHudRelease release, CancellationToken ct = default)
    {
        if (!release.Installable) throw new CatalogException(CaseHudIntegrity.ProblemText(release));
        var sums = await ReadSmallAsync(release.SumsUrl!, ct);
        var signature = SignatureRequired ? Encoding.ASCII.GetString(await ReadSmallAsync(release.SignatureUrl ?? throw new CatalogException(Strings.Get("selfupdate.noSignature")), ct)) : null;
        CaseHudIntegrity.VerifySignature(sums, signature, publicKeyPem);
        CaseHudIntegrity.ParseChecksum(sums, release.SetupName!);
    }

    /// <summary>Checksum file, signature, download, size and hash. Nothing is returned (and the file is deleted) unless all of it passes.</summary>
    public async Task<VerifiedSetup> DownloadAsync(CaseHudRelease release, string directory, CancellationToken ct = default)
    {
        if (!release.Installable) throw new CatalogException(CaseHudIntegrity.ProblemText(release));
        var sums = await ReadSmallAsync(release.SumsUrl!, ct);
        var signature = SignatureRequired ? Encoding.ASCII.GetString(await ReadSmallAsync(release.SignatureUrl ?? throw new CatalogException(Strings.Get("selfupdate.noSignature")), ct)) : null;
        CaseHudIntegrity.VerifySignature(sums, signature, publicKeyPem);
        var expected = CaseHudIntegrity.ParseChecksum(sums, release.SetupName!);

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
                    throw new CatalogException(ex is OperationCanceledException
                        ? (total.IsCancellationRequested ? Strings.Get("selfupdate.downloadSlow") : Strings.Get("net.stalled"))
                        : Strings.Get("net.downloadFailed", ex.Message));
                }
            }

            // Hash from the handle that is kept: once it is open nobody can write to the file any more.
            handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (release.SetupSize > 0 && handle.Length != release.SetupSize) throw new CatalogException(Strings.Get("selfupdate.setupSize"));
            ReleaseIntegrity.VerifyHash(handle, expected);
            var verified = new VerifiedSetup(path, handle, expected, sums, signature);
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

    private async Task<HttpResponseMessage> SendCheckedAsync(string url, CancellationToken ct)
    {
        if (!CaseHudReleaseParser.IsAllowedAssetUrl(url)) throw new CatalogException(Strings.Get("selfupdate.githubOnly"));
        var uri = new Uri(url);
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                response.Dispose();
                try { uri = location.IsAbsoluteUri ? location : new Uri(uri, location); }
                catch (UriFormatException ex) { throw new CatalogException(Strings.Get("net.downloadFailed", ex.Message)); }
                if (!SelfUpdateRules.IsAllowedHost(uri)) throw new CatalogException(Strings.Get("selfupdate.redirect", uri.Host));
                continue;
            }
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // Release files of a private repository: same state as for the release list, no retry loop.
                MarkUnavailable(response.StatusCode, global: false);
                response.Dispose();
                throw new CatalogException(Strings.Get("casehud.downloadsUnavailable"));
            }
            try { response.EnsureSuccessStatusCode(); }
            catch { response.Dispose(); throw; }
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
}

/// <summary>Which bridge calls work while the couch is locked: look at the status, switch scene, power and identify. Everything else needs the unlock.</summary>
public static class CaseHudPolicy
{
    public static readonly IReadOnlySet<string> OpenWhileLocked = new HashSet<string>
    {
        "caseHudStatus", "caseHudScenes", "caseHudActivateScene", "caseHudNextScene", "caseHudPower", "caseHudIdentify", "caseHudPreview",
        // Reading the lighting changes nothing; every change of it is a setting and needs the code (caseHudPatchLighting).
        "caseHudLighting"
    };

    /// <summary>Every call the CaseHUD page can make. Anything else is refused by the host, so nothing slips past the lock rules unnoticed.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(OpenWhileLocked)
    {
        "caseHudCheckUpdate", "caseHudPrepare", "caseHudStart", "caseHudInstall", "caseHudUpdate", "caseHudOpenGui", "caseHudPanels",
        "caseHudPatchPanel", "caseHudDesigns", "caseHudSets", "caseHudApplySet", "caseHudRotation", "caseHudCommunity", "caseHudImport", "caseHudAwtrix",
        "caseHudPatchLighting",
        // Switching CaseHUD's API on or off is a setting of CaseHUD: never without the code.
        "caseHudSetApi"
    };

    /// <summary>A known call that may run now: always when unlocked, only the short list while the couch is locked.</summary>
    public static bool Allowed(string method, bool locked) => All.Contains(method) && (!locked || OpenWhileLocked.Contains(method));

    /// <summary>
    /// While no release key is set a release is only protected by its SHA-256 (from the checksum file): installing it then needs
    /// an explicit confirmation of the user ("not signed, checksum only"). With a key the signature is mandatory and nothing is asked.
    /// </summary>
    public static bool NeedsUnsignedConfirmation(string publicKeyPem) => string.IsNullOrWhiteSpace(publicKeyPem);

    /// <summary>
    /// Silent-install options: the optional tasks the user chose in the expert settings (the sensor service is always installed).
    /// Never <c>/API=1</c>: the setup runs elevated (the CouchHUD service as SYSTEM, or UAC, possibly another administrator), so it would
    /// switch the API on for the wrong account; the shell does it afterwards in the user's session (<see cref="ICaseHudApiSwitch"/>).
    /// </summary>
    public static string InstallArguments(bool autostart, bool fps)
    {
        var tasks = new List<string>();
        if (autostart) tasks.Add("autostart");
        if (fps) tasks.Add("fps");
        return $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=\"{string.Join(',', tasks)}\"";
    }
}
