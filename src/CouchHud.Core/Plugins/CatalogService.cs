using System.Text.Json;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Plugins;

/// <summary>What the catalog sources delivered; <see cref="FromCache"/> when at least one source was offline. ErrorCode (notFound, unreachable) is set when every error is of that kind, so the UI can translate it.</summary>
public sealed record CatalogResult(IReadOnlyList<CatalogEntry> Entries, bool FromCache, long? FetchedAt, string? Error, string? ErrorCode = null);

/// <summary>What an install did; plugins always need a restart because loaded DLLs cannot be replaced or unloaded.</summary>
public sealed record InstallOutcome(string Message, bool RestartNeeded);

/// <summary>
/// What is about to be installed, for the host's gate (unlock, expert mode, the parents' code). <see cref="Statement"/> is null for a
/// package without one (SDK 2 and older, installed with the SDK 2 surface).
/// </summary>
public sealed record PluginInstallRequest(CatalogEntry Entry, VerifiedStatement? Statement, bool IsUpdate, bool AddsCapabilities)
{
    public PluginOrigin Origin => Statement?.Origin ?? (Entry.Extra ? PluginOrigin.ThirdParty : PluginOrigin.Local);
}

/// <summary>
/// Loads catalog indexes (with timeout, size limit and an offline cache), downloads packages with a
/// mandatory SHA-256 check and installs them through <see cref="PackageInstaller"/>.
/// Trust: the default source must carry a valid signature (index.json.sig, see <see cref="CatalogSignature"/>);
/// sources the user added may be unsigned and are marked as such. Only https is accepted; local sources exist
/// for development behind <paramref name="allowLocal"/>.
/// </summary>
public sealed class CatalogService(HttpClient http, string dataDir, Func<IReadOnlyList<string>> sources, Func<bool> allowLocal, string language = "de", string? publicKeyPem = null, Func<string>? languageNow = null,
    Func<IReadOnlyList<CatalogSourceEntry>>? extraSources = null, System.Net.IWebProxy? proxy = null)
{
    /// <summary>
    /// Is this address reached through a proxy? Then catalogs of other developers (index, downloads, media) are used only with the developer
    /// switch: behind a proxy their hosts are checked by name only, which is not enough for something untrusted. CouchHUD's own hosts work.
    /// </summary>
    public bool BehindProxy(string? url) =>
        url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && !uri.IsFile && CatalogHosts.UsesProxy(proxy ?? CatalogHosts.SystemProxy, uri);

    private string Language => languageNow?.Invoke() ?? language;

    public static readonly TimeSpan IndexTimeout = TimeSpan.FromSeconds(10);
    /// <summary>A download may take as long as it likes, as long as data keeps coming.</summary>
    public static TimeSpan DownloadInactivity { get; internal set; } = TimeSpan.FromSeconds(60);
    public const long MaxIndexBytes = 1024 * 1024;
    public const long MaxSignatureBytes = 4096;
    public const long MaxPackageBytes = 200L * 1024 * 1024;

    // One lock per cache file for every service on it (the restore has its own service next to the main one).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> CacheLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _cacheLock = CacheLocks.GetOrAdd(Path.GetFullPath(Path.Combine(dataDir, "catalog-cache.json")), _ => new object());

    private string CacheFile => Path.Combine(dataDir, "catalog-cache.json");

    private sealed record CacheEntry(long FetchedAt, string Data, string? Signature);

    public static bool IsDefaultSource(string source) => CatalogUrl.Same(source, CatalogDefaults.IndexUrl);

    /// <summary>
    /// Loads the indexes. With <paramref name="cacheOnly"/> nothing is fetched: every source comes from the saved copy, checked exactly like
    /// one used when offline (signature of the official index, size, address rules); a source without a copy is left out.
    /// </summary>
    public async Task<CatalogResult> LoadAsync(CancellationToken ct = default, bool cacheOnly = false)
    {
        var entries = new List<CatalogEntry>();
        var errors = new List<string>();
        var codes = new List<string?>();
        var fromCache = false;
        long? fetched = null;
        var local = allowLocal();
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // The sources given (the official one first), then the catalogs the user added besides it (CatalogSourceRules: https only, bounded, never
        // the official one again). Only a source of the second kind is "extra": third-party throughout, never revoking, never first-party.
        var given = sources().Distinct(CatalogUrl.Comparer).ToList();
        var all = given.Select(s => (Url: s, Extra: (CatalogSourceEntry?)null))
            .Concat(CatalogSourceRules.Effective(extraSources?.Invoke()).Where(e => !given.Any(g => CatalogUrl.Same(g, e.Url))).Select(e => (e.Url, Extra: (CatalogSourceEntry?)e))).ToList();
        foreach (var (source, extra) in all)
        {
            if (extra is not null && !local && BehindProxy(source))
            {
                errors.Add(Strings.Get("catalog.source.proxy", Short(source)));
                codes.Add(null);
                continue;
            }
            if (!CatalogParser.IsAllowedUrl(source, local))
            {
                errors.Add(Strings.Get("catalog.sourceHttps", source));
                codes.Add(null);
                continue;
            }
            IReadOnlyList<CatalogEntry> parsed;
            try
            {
                if (cacheOnly) throw new CatalogException("cache only");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(IndexTimeout);
                var data = await ReadBytesAsync(source, extra is null ? MaxIndexBytes : CatalogSourceRules.MaxIndexBytes, timeout.Token);
                string? signature = null;
                try
                {
                    signature = System.Text.Encoding.ASCII.GetString(await ReadBytesAsync(CatalogSignature.SigUrl(source), MaxSignatureBytes, timeout.Token));
                }
                catch (Exception ex) when (ex is HttpRequestException or CatalogException or IOException)
                {
                    // No signature file: fine for user sources, fatal for the default one (checked below).
                }
                parsed = Accept(data, signature, source, local, extra);
                CheckSerial(source, data, signature, extra);
                WriteCache(source, data, signature);
                fetched = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            catch (Exception ex) when (ex is HttpRequestException or CatalogException or OperationCanceledException or IOException)
            {
                if (ct.IsCancellationRequested) throw;
                var cached = ReadCache(source);
                try
                {
                    if (cached is null) throw;
                    parsed = Accept(Convert.FromBase64String(cached.Data), cached.Signature, source, local, extra);
                    fromCache = true;
                    fetched = Math.Min(fetched ?? long.MaxValue, cached.FetchedAt);
                    errors.Add(ex is CatalogException ? Strings.Get("catalog.sourceCached", Short(source), ex.Message) : Strings.Get("catalog.sourceOffline", Short(source)));
                    codes.Add(ex is CatalogException ? null : CodeOf(ex));
                }
                catch (Exception inner) when (inner is CatalogException or FormatException || ReferenceEquals(inner, ex))
                {
                    // A broken cache is as good as none.
                    errors.Add(Strings.Get("catalog.sourceError", Short(source), ex is OperationCanceledException ? Strings.Get("net.noAnswer") : ex.Message));
                    codes.Add(ex is CatalogException ? null : CodeOf(ex));
                    continue;
                }
            }

            foreach (var entry in extra is null ? parsed : parsed.Take(CatalogSourceRules.MaxEntries).Select(Reserved))
            {
                var idKey = $"{entry.Kind}:{entry.Id}";
                var folderKey = $"{entry.Kind}:{entry.Folder}";
                if (ids.TryGetValue(idKey, out var knownFolder))
                {
                    // Same id again: only interesting when it points somewhere else (a takeover attempt or a clash).
                    if (!knownFolder.Equals(entry.Folder, StringComparison.OrdinalIgnoreCase))
                    {
                        entries.Add(entry with { Problem = Strings.Get("catalog.collision"), DownloadUrl = null });
                    }
                    continue;
                }
                if (folders.ContainsKey(folderKey)) continue;   // first source wins
                ids[idKey] = entry.Folder;
                folders[folderKey] = entry.Id;
                entries.Add(entry);
            }
        }

        // Without the official catalog (never loaded, nothing cached) the ids of CouchHUD's own plugins are not known: plugins of another
        // catalog wait until it has loaded once.
        if (entries.Any(e => e.Extra) && DefaultInfo().Index is null)
        {
            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i].Extra && entries[i].Kind == CatalogKind.Plugin && entries[i].Problem is null)
                    entries[i] = entries[i] with { Problem = Strings.Get("catalog.source.officialFirst"), DownloadUrl = null };
            }
        }

        return new CatalogResult(entries, fromCache, fetched, errors.Count == 0 ? null : string.Join(" · ", errors),
            codes.Count > 0 && codes.All(c => c is not null && c == codes[0]) ? codes[0] : null);
    }

    /// <summary>notFound for a 404 (the repository or file does not exist), unreachable for everything else on the network.</summary>
    private static string CodeOf(Exception ex) => ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound } ? "notFound" : "unreachable";

    /// <summary>Verifies the signature (mandatory for the default source) and parses the index.</summary>
    private IReadOnlyList<CatalogEntry> Accept(byte[] data, string? signature, string source, bool local, CatalogSourceEntry? extra = null)
    {
        if (extra is not null)
        {
            // A catalog of another developer: signed only with the key pinned when it was added (never with CouchHUD's), third-party throughout.
            // With a pinned key a missing or wrong signature is an error (a cached copy is checked the same way), not a catalog "without signature".
            var pinned = !string.IsNullOrWhiteSpace(extra.PublisherKey);
            if (pinned && !CatalogSignature.Verify(data, signature, extra.PublisherKey)) throw new CatalogException(Strings.Get("catalog.source.pinMismatch"));
            return CatalogParser.Parse(System.Text.Encoding.UTF8.GetString(data), source, Language, false)
                .Select(e => OnlyPublicAddresses(e, local) with { Extra = true, Unsigned = !pinned }).ToList();
        }
        var verified = CatalogSignature.Verify(data, signature, publicKeyPem);
        if (IsDefaultSource(source) && !verified) throw new CatalogException(Strings.Get("catalog.untrusted"));
        var parsed = CatalogParser.Parse(System.Text.Encoding.UTF8.GetString(data), source, Language, local);
        return verified ? parsed : parsed.Select(e => e with { Unsigned = true }).ToList();
    }

    /// <summary>
    /// Replay protection: a fetched index whose serial is lower than the one of the saved copy of the same address is refused (the saved copy is
    /// kept and shown instead, so a revocation list never rolls back). Equal is fine, a missing serial counts as 0. Only indexes whose signature
    /// verifies take part, on both sides: the official one, a catalog with a pinned key, and a saved copy that verifies again. An unsigned catalog
    /// has nothing that proves a serial, so it is not checked (a forged high serial would otherwise lock out its real updates).
    /// </summary>
    private void CheckSerial(string source, byte[] data, string? signature, CatalogSourceEntry? extra)
    {
        if (!SignedFor(data, signature, extra)) return;
        var cached = ReadCache(source);
        if (cached is null || !TryDecode(cached.Data, out var old) || !SignedFor(old, cached.Signature, extra)) return;
        var oldSerial = CatalogParser.ReadSerial(System.Text.Encoding.UTF8.GetString(old));
        var newSerial = CatalogParser.ReadSerial(System.Text.Encoding.UTF8.GetString(data));
        if (newSerial >= oldSerial) return;
        Console.Error.WriteLine($"[catalog] {Short(source)}: older index refused (serial {newSerial} < saved {oldSerial}), keeping the saved copy");
        throw new CatalogException(Strings.Get("catalog.older", newSerial, oldSerial));
    }

    private bool SignedFor(byte[] data, string? signature, CatalogSourceEntry? extra) =>
        extra is null ? CatalogSignature.Verify(data, signature, publicKeyPem)
            : !string.IsNullOrWhiteSpace(extra.PublisherKey) && CatalogSignature.Verify(data, signature, extra.PublisherKey);

    private static bool TryDecode(string base64, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromBase64String(base64);
            return true;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    /// <summary>Addresses in an extra catalog that point into the user's own network are dropped (download: the entry cannot be installed).</summary>
    private static CatalogEntry OnlyPublicAddresses(CatalogEntry entry, bool developerSwitch)
    {
        if (developerSwitch) return entry;
        if (entry.DownloadUrl is not null && !CatalogHosts.IsPublicHost(entry.DownloadUrl))
            entry = entry with { Problem = Strings.Get("catalog.badUrl"), DownloadUrl = null };
        if (entry.IconUrl is not null && !CatalogHosts.IsPublicHost(entry.IconUrl)) entry = entry with { IconUrl = null };
        return entry with { Screenshots = entry.Screenshots.Where(CatalogHosts.IsPublicHost).ToList() };
    }

    /// <summary>An entry of an extra source may not carry the name of one of CouchHUD's own plugins (or look like it): listed, not installable.</summary>
    private CatalogEntry Reserved(CatalogEntry entry)
    {
        if (entry.Problem is not null) return entry;
        if (entry.Kind == CatalogKind.Skin)
        {
            // The skins that moved to the catalog, the built-in one and every skin of the official index.
            var names = new HashSet<string>(CouchHud.Core.Skins.SkinCatalog.MovedToCatalogIds.Concat(CouchHud.Core.Skins.SkinCatalog.BuiltinIds).Append("default"), StringComparer.OrdinalIgnoreCase);
            foreach (var skin in DefaultInfo().Index?.Entries.Where(e => e.Kind == CatalogKind.Skin) ?? [])
            {
                names.Add(skin.Id);
                names.Add(skin.Folder);
            }
            var skeletons = names.Select(PluginNames.Skeleton).ToHashSet(StringComparer.Ordinal);
            return names.Contains(entry.Id) || names.Contains(entry.Folder) || skeletons.Contains(PluginNames.Skeleton(entry.Id)) || skeletons.Contains(PluginNames.Skeleton(entry.Folder))
                ? entry with { Problem = Strings.Get("catalog.source.reserved"), DownloadUrl = null }
                : entry;
        }
        if (entry.Kind != CatalogKind.Plugin) return entry;
        var own = FirstPartyIds();
        return PluginNames.IsReserved(entry.Id, own) || PluginNames.IsReserved(entry.Folder, own)
            ? entry with { Problem = Strings.Get("catalog.source.reserved"), DownloadUrl = null }
            : entry;
    }

    private readonly object _memoLock = new();
    private long _memoVersion;
    private long _memoStamp = -1;
    private CatalogIndex? _memoIndex;
    private IReadOnlyList<string> _memoIds = [];

    /// <summary>The signed default index from the cache and the ids of CouchHUD's own plugins in it, worked out once until the cache changes.</summary>
    private (CatalogIndex? Index, IReadOnlyList<string> Ids) DefaultInfo()
    {
        // Never one lock while taking the other: the cache is read outside the memo's lock, and the result is kept only if the cache was
        // not written meanwhile (the version is the same as before the read).
        var version = Interlocked.Read(ref _memoVersion);
        lock (_memoLock)
        {
            if (_memoStamp == version) return (_memoIndex, _memoIds);
        }
        var index = ReadDefaultIndex();
        var ids = OwnIds(index);
        lock (_memoLock)
        {
            if (Interlocked.Read(ref _memoVersion) == version)
            {
                _memoIndex = index;
                _memoIds = ids;
                _memoStamp = version;
            }
        }
        return (index, ids);
    }

    private CatalogIndex? CachedDefaultIndex() => DefaultInfo().Index;

    /// <summary>The signed default index from the cache, verified again; null when there is none (or it does not verify).</summary>
    private CatalogIndex? ReadDefaultIndex()
    {
        var cached = ReadCache(CatalogDefaults.IndexUrl);
        if (cached is null) return null;
        try
        {
            var data = Convert.FromBase64String(cached.Data);
            if (!CatalogSignature.Verify(data, cached.Signature, publicKeyPem)) return null;
            return CatalogParser.ParseIndex(System.Text.Encoding.UTF8.GetString(data), CatalogDefaults.IndexUrl, Language);
        }
        catch (Exception ex) when (ex is CatalogException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The revocation list of the last signed default index in the cache. Anything else (another source, no valid signature) never revokes
    /// and never un-revokes. Empty when there is none.
    /// </summary>
    public IReadOnlyList<CatalogRevocation> CachedRevocations() => CachedDefaultIndex()?.Revoked ?? [];

    /// <summary>Ids of CouchHUD's own plugins in the signed default index (reserved for third-party developers).</summary>
    public IReadOnlyList<string> FirstPartyIds() => DefaultInfo().Ids;

    private IReadOnlyList<string> OwnIds(CatalogIndex? index)
    {
        var ids = new List<string>();
        foreach (var e in index?.Entries ?? [])
        {
            try
            {
                var signed = PackageStatements.FromFields(e.Statement, e.Signature, e.PublisherKey);
                if (signed is not null && CatalogSignature.Verify(signed.Statement, signed.Signature, publicKeyPem ?? CatalogSignature.PublicKeyPem, CatalogSignature.PluginContext))
                    ids.Add(e.Id);
            }
            catch (CatalogException)
            {
                // A broken statement makes nobody's name reserved.
            }
        }
        return ids;
    }

    /// <summary>
    /// The statement of a catalog entry, verified, matched against the entry and against what is installed (pinned key, no downgrade, not
    /// revoked). Null for an entry without one. Throws (fail closed) for anything that does not check out, and for an entry without a
    /// statement that would replace a plugin installed from a signed one (that would drop the pinned key).
    /// </summary>
    public VerifiedStatement? VerifyEntry(CatalogEntry entry)
    {
        if (entry.Kind != CatalogKind.Plugin) return null;
        var trust = new PluginTrust(dataDir);
        // A list of approved plugins that cannot be trusted says nothing about pinned keys or versions: refuse (fail closed, as an error the page shows).
        if (trust.IsFaulty) throw new CatalogException(Strings.Get("plugin.trustFault"));
        // The ids of CouchHUD's own plugins come from its signed index: until that has loaded once, a plugin of another catalog waits.
        if (entry.Extra && DefaultInfo().Index is null) throw new CatalogException(Strings.Get("catalog.source.officialFirst"));
        var revoked = CachedRevocations();
        var signed = PackageStatements.FromFields(entry.Statement, entry.Signature, entry.PublisherKey);
        if (signed is null)
        {
            // A catalog the user added is third-party: its plugins need a statement signed with their developer's own key (no unsigned installs).
            if (entry.Extra) throw new CatalogException(Strings.Get("catalog.source.needsStatement"));
            if (trust.ForId(entry.Id).Any(t => t.Entry.KeyFingerprint is not null) || trust.Entry(entry.Folder)?.KeyFingerprint is not null)
                throw new CatalogException(Strings.Get("plugin.statement.keyChanged"));
            if (revoked.Any(r => r.Matches(entry.Id, entry.Version, null))) throw new CatalogException(Strings.Get("plugin.revoked", entry.Name));
            return null;
        }
        var verified = PackageStatements.Verify(signed, publicKeyPem, FirstPartyIds(), allowFirstParty: !entry.Extra);
        PackageStatements.CheckMatches(verified, entry.Sha256 ?? "", entry);
        // The signed statement says which SDK the package was built for: a newer one than this CouchHUD implements is not installed at all.
        if (CatalogRules.NeedsNewerSdk(verified.Statement.Sdk)) throw new CatalogException(Strings.Get("catalog.needsSdk", verified.Statement.Sdk));
        PackageStatements.CheckAgainstInstalled(verified, trust);
        if (revoked.Any(r => r.Matches(verified.Statement.Id, verified.Statement.Version, verified.KeyFingerprint)))
            throw new CatalogException(Strings.Get("plugin.revoked", entry.Name));
        return verified;
    }

    private static string Short(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? (u.IsFile ? u.LocalPath : u.Host) : url;

    private async Task<byte[]> ReadBytesAsync(string url, long limit, CancellationToken ct)
    {
        var (stream0, length) = await OpenAsync(url, ct);
        await using var stream = stream0;
        if (length > limit) throw new CatalogException(Strings.Get("catalog.tooLarge"));
        using var buffer = new MemoryStream();
        await CopyLimitedAsync(stream, buffer, limit, Strings.Get("catalog.tooLarge"), ct);
        return buffer.ToArray();
    }

    private async Task<(Stream Stream, long? Length)> OpenAsync(string url, CancellationToken ct)
    {
        var uri = new Uri(url);
        if (uri.IsFile)
        {
            var file = File.OpenRead(uri.LocalPath);
            return (file, file.Length);
        }
        // Redirects by hand, at most three, every hop checked again like the first address (https, public host unless the developer switch is on).
        var response = await CatalogHosts.GetAsync(http, uri, allowLocal(), ct);
        try
        {
            response.EnsureSuccessStatusCode();
        }
        catch
        {
            response.Dispose();
            throw;
        }
        return (new ResponseStream(await response.Content.ReadAsStreamAsync(ct), response), response.Content.Headers.ContentLength);
    }

    /// <summary>Copies at most <paramref name="limit"/> bytes. With <paramref name="idle"/> the token is cancelled when no data arrives for <see cref="DownloadInactivity"/>.</summary>
    private static async Task CopyLimitedAsync(Stream from, Stream to, long limit, string message, CancellationToken ct, CancellationTokenSource? idle = null)
    {
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            idle?.CancelAfter(DownloadInactivity);
            var read = await from.ReadAsync(buffer, ct);
            if (read <= 0) break;
            total += read;
            if (total > limit) throw new CatalogException(message);
            await to.WriteAsync(buffer.AsMemory(0, read), ct);
        }
    }

    /// <summary>Disposes the HTTP response together with its stream.</summary>
    private sealed class ResponseStream(Stream inner, IDisposable response) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                response.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // -----------------------------------------------------------------------
    // Cache

    private void WriteCache(string source, byte[] data, string? signature)
    {
        lock (_cacheLock)
        {
            try
            {
                var all = ReadAllCache();
                all[source] = new CacheEntry(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), Convert.ToBase64String(data), signature);
                Directory.CreateDirectory(dataDir);
                var tmp = CacheFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(all));
                File.Move(tmp, CacheFile, overwrite: true);
                // After the new file is in place, not before: a reader that started earlier does not keep the old picture.
                if (IsDefaultSource(source)) Interlocked.Increment(ref _memoVersion);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[catalog] cache not written: {ex.Message}");
            }
        }
    }

    private CacheEntry? ReadCache(string source)
    {
        lock (_cacheLock) return ReadAllCache().GetValueOrDefault(source);
    }

    private Dictionary<string, CacheEntry> ReadAllCache()
    {
        try
        {
            return File.Exists(CacheFile)
                ? JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(CacheFile)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new();
        }
    }

    // -----------------------------------------------------------------------
    // Install

    /// <summary>
    /// Downloads, verifies and installs. Checks (in this order, nothing is written before all pass):
    /// compatibility, origin (updates only from the source the package came from), https, hash present,
    /// download size, hash equal. Skins are usable at once; plugins are installed (new) or staged
    /// (update of a plugin that may be loaded) and need a restart.
    /// </summary>
    /// <param name="approve">
    /// The host's gate, asked after the statement checked out and before anything is downloaded; it throws (or returns false) to stop. Without
    /// it only CouchHUD's own packages and packages without a statement go ahead: a third-party package is refused (fail closed).
    /// </param>
    /// <param name="updateOnly">Only replaces what is installed and not being removed (the automatic update); a first install or a removed plugin is refused.</param>
    /// <param name="checkPackage">Looks at the downloaded package once its hash matched; it throws to stop. Nothing is written before it passes.</param>
    public async Task<InstallOutcome> InstallAsync(CatalogEntry entry, SemVer app, IEnumerable<PluginInfo>? plugins = null, CancellationToken ct = default,
        Func<PluginInstallRequest, bool>? approve = null, Action<Stream>? checkPackage = null, bool updateOnly = false)
    {
        var reason = CatalogRules.Reason(entry, app);
        if (reason is not null) throw new CatalogException(reason);
        var (installed, _, _, origin) = new CatalogRegistry(dataDir).Lookup(entry, plugins ?? []);
        var originProblem = CatalogRules.OriginProblem(entry, installed, origin);
        if (originProblem is not null) throw new CatalogException(originProblem);
        var local = allowLocal();
        // A package of a catalog that has been removed since the list was loaded is not installed from the old list.
        if (entry.Extra && !CatalogSourceRules.Effective(extraSources?.Invoke()).Any(s => string.Equals(s.Url, entry.Source, StringComparison.OrdinalIgnoreCase)))
            throw new CatalogException(Strings.Get("catalog.source.removed"));
        if (entry.Extra && !local && BehindProxy(entry.DownloadUrl)) throw new CatalogException(Strings.Get("catalog.source.proxy", entry.Name));
        if (entry.DownloadUrl is null || !CatalogHosts.Allowed(entry.DownloadUrl, local)) throw new CatalogException(Strings.Get("catalog.httpsOnly"));
        if (entry.Sha256 is null) throw new CatalogException(Strings.Get("catalog.noChecksumInstall"));
        var statement = VerifyEntry(entry);
        if (entry.Kind == CatalogKind.Skin && entry.Extra)
        {
            // A skin of a catalog the user added is third-party like a plugin of one: the host's gate is asked here, on the entry that is really
            // installed, so no stale or empty list can skip it (without a gate it is refused).
            var ok = approve is not null && approve(new PluginInstallRequest(entry, null, installed, false));
            if (!ok) throw new CatalogException(Strings.Get("plugin.gate.thirdParty"));
        }
        if (entry.Kind == CatalogKind.Plugin)
        {
            var before = new PluginTrust(dataDir).Entry(entry.Folder);
            var adds = statement is not null && installed && CouchHud.Core.Updates.CapabilityCompare.Added(before?.Capabilities ?? (IEnumerable<string>)PluginPolicy.Legacy, statement.Statement.Capabilities).Count > 0;
            var request = new PluginInstallRequest(entry, statement, installed, adds);
            var ok = approve is null ? request.Origin != PluginOrigin.ThirdParty : approve(request);
            if (!ok) throw new CatalogException(Strings.Get("plugin.gate.thirdParty"));
        }

        var temp = Path.Combine(dataDir, "catalog-temp");
        Directory.CreateDirectory(temp);
        var zip = Path.Combine(temp, $"{Guid.NewGuid():N}.zip");
        try
        {
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                try
                {
                    idle.CancelAfter(DownloadInactivity);
                    var (source, length) = await OpenAsync(entry.DownloadUrl, idle.Token);
                    await using var stream = source;
                    if (length > MaxPackageBytes) throw new CatalogException(Strings.Get("catalog.packageTooLarge"));
                    await using var file = File.Create(zip);
                    await CopyLimitedAsync(stream, file, MaxPackageBytes, Strings.Get("catalog.packageTooLarge"), idle.Token, idle);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    throw new CatalogException(ex is OperationCanceledException ? Strings.Get("net.stalled") : Strings.Get("net.downloadFailed", ex.Message));
                }
            }

            try
            {
                // Hash, lint and unpack happen on one open handle that keeps writers out (the folder is writable by every program of the user).
                return Install(entry, zip, statement, entry.Sha256, checkPackage, updateOnly);
            }
            catch (InvalidDataException ex)
            {
                throw new CatalogException(Strings.Get("catalog.archiveBad", ex.Message));
            }
        }
        finally
        {
            try { File.Delete(zip); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Installs an already verified package (its statement, if it has one, verified by <see cref="VerifyEntry"/>).</summary>
    internal InstallOutcome Install(CatalogEntry entry, string verifiedZip, VerifiedStatement? statement = null, string? expectedSha256 = null, Action<Stream>? checkPackage = null, bool updateOnly = false)
    {
        var registry = new CatalogRegistry(dataDir);
        // One handle for everything below: the package is hashed, looked at and unpacked from the same bytes, with no gap to swap the file in.
        using var held = new FileStream(verifiedZip, FileMode.Open, FileAccess.Read, FileShare.Read);
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(held));
        if (expectedSha256 is not null && !PendingPackages.CryptographicEquals(sha, expectedSha256)) throw new CatalogException(Strings.Get("catalog.checksumMismatch"));
        held.Position = 0;
        // The caller's own look at the package (the automatic update compares plugin.json with the statement), on the same bytes that were hashed.
        checkPackage?.Invoke(held);
        held.Position = 0;
        // An update only (the automatic one): what is to be updated must still be there and not be on its way out (it may have been removed
        // while the package downloaded); otherwise nothing is installed or staged.
        if (updateOnly)
        {
            var folder = Path.Combine(dataDir, entry.Kind == CatalogKind.Skin ? "skins" : "plugins", entry.Folder);
            if (!Directory.Exists(folder) || (entry.Kind == CatalogKind.Plugin && new PluginTrust(dataDir).IsRemoved(entry.Folder)))
                throw new CatalogException(Strings.Get("autoupdate.gone"));
        }
        if (entry.Kind == CatalogKind.Skin)
        {
            PackageInstaller.Install(held, entry.Folder, Path.Combine(dataDir, "skins"), PackageInstaller.Kind.Skin, entry.Folder, out _);
            registry.Set(entry, pending: false);
            return new InstallOutcome(Strings.Get("catalog.skinInstalled", entry.Name, entry.Version), false);
        }

        // What the code references must be covered by what the statement declares; a package without a statement only gets its file types checked.
        PluginLint.CheckZip(held, statement?.Origin ?? PluginOrigin.Local, statement?.Statement.Capabilities ?? [], legacy: statement is null, sdk: statement?.Statement.Sdk ?? int.MaxValue);
        held.Position = 0;
        var trust = new PluginTrust(dataDir);
        var pluginRoot = Path.Combine(dataDir, "plugins");
        if (!Directory.Exists(Path.Combine(pluginRoot, entry.Folder)) && !trust.IsRemoved(entry.Folder))
        {
            var target = PackageInstaller.Install(held, entry.Folder, pluginRoot, PackageInstaller.Kind.Plugin, entry.Folder, out var folderHash);
            PendingPackages.WarnIfManifestDiffers(target, entry.Version);
            // Installed on purpose, from a verified package: the files as they were unpacked are the trusted ones.
            trust.Record(entry.Folder, folderHash, statement is null ? new PluginTrustEntry { Hash = "", Origin = PluginOrigin.Local } : statement.ToTrustEntry());
            registry.Set(entry, pending: false);
            return new InstallOutcome(Strings.Get("catalog.pluginInstalled", entry.Name, entry.Version), true);
        }

        // An older copy may be loaded (and its DLLs locked): replace it before the next start loads anything. What was verified waits in the
        // protected trust store with the package's own SHA-256 (not next to the package or in catalog-installed.json, where any program of the
        // user could change it); the next start puts in place only a package that has this entry and this hash.
        trust.StagePending(entry.Folder, statement is null ? new PluginTrustEntry { Hash = sha, Origin = PluginOrigin.Local } : statement.ToTrustEntry(sha));
        PendingPackages.Stage(dataDir, entry, verifiedZip);
        registry.Set(entry, pending: true);
        return new InstallOutcome(Strings.Get("catalog.updateReady", entry.Name, entry.Version), true);
    }

    /// <summary>
    /// Removes a user plugin (the host's gate comes first). Its approval goes first and a removal mark under the trust store's signature takes
    /// its place, so a folder that cannot be removed now (its DLLs are loaded) never loads again and never shows up as unknown: the next start
    /// deletes it before anything loads (<see cref="FinishRemovals"/>). Its settings in plugin-data stay (a reinstall finds them). True when the
    /// folder is gone from the plugins folder now.
    /// </summary>
    public bool UninstallPlugin(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || folder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || folder is "." or "..")
            throw new ArgumentException(Strings.Get("catalog.badFolder"));
        var trust = new PluginTrust(dataDir);
        trust.MarkRemoved(folder);
        var registry = new CatalogRegistry(dataDir);
        foreach (var (key, record) in registry.Read().Where(r => r.Key.StartsWith("plugin:", StringComparison.Ordinal) && r.Value.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase)).ToList())
            registry.Remove(key);
        if (!MoveToTrash(dataDir, folder)) return false;
        trust.ClearRemoved(folder);
        EmptyTrash(dataDir);
        return true;
    }

    /// <summary>The folder out of the plugins folder (into <c>plugin-trash</c>); true when it is gone (or was not there).</summary>
    private static bool MoveToTrash(string dataDir, string folder)
    {
        var dir = Path.Combine(dataDir, "plugins", folder);
        if (!Directory.Exists(dir)) return true;
        try
        {
            var trash = Path.Combine(dataDir, "plugin-trash");
            Directory.CreateDirectory(trash);
            Directory.Move(dir, Path.Combine(trash, $"{folder}.{Guid.NewGuid():N}"));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[catalog] {folder} stays until the next start: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// At start, before any plugin loads (and before staged updates go in): finishes the removals the user asked for while the DLLs were loaded.
    /// A folder that still cannot be moved keeps its mark (it does not load and is tried again next time). Never throws.
    /// </summary>
    public static void FinishRemovals(string dataDir)
    {
        try
        {
            var trust = new PluginTrust(dataDir);
            foreach (var folder in trust.RemovedFolders())
            {
                if (folder.Length == 0 || folder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || folder is "." or "..") continue;
                if (MoveToTrash(dataDir, folder)) trust.ClearRemoved(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine($"[catalog] removing plugins: {ex.Message}");
        }
        EmptyTrash(dataDir);
    }

    /// <summary>Deletes what <see cref="UninstallPlugin"/> put aside (at start, before any plugin loads); what is still in use stays for next time.</summary>
    public static void EmptyTrash(string dataDir)
    {
        var trash = Path.Combine(dataDir, "plugin-trash");
        if (!Directory.Exists(trash)) return;
        foreach (var dir in Directory.EnumerateDirectories(trash))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use (loaded right now): the next start removes it.
            }
        }
    }
}

/// <summary>Which catalog packages were installed through CouchHud: version, folder and the source they came from.</summary>
public sealed class CatalogRegistry(string dataDir)
{
    private static readonly object Lock = new();
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <param name="Sha256">Hash of the staged package (a pending update): checked again before it is put in place.</param>
    public sealed record Record(string Version, string Folder, bool Pending, string? Source = null, string? Sha256 = null);

    private string StoreFile => Path.Combine(dataDir, "catalog-installed.json");

    public static string Key(CatalogKind kind, string id) => $"{kind.ToString().ToLowerInvariant()}:{id}".ToLowerInvariant();

    public Dictionary<string, Record> Read()
    {
        lock (Lock)
        {
            try
            {
                return File.Exists(StoreFile)
                    ? new Dictionary<string, Record>(JsonSerializer.Deserialize<Dictionary<string, Record>>(File.ReadAllText(StoreFile), Options) ?? [], StringComparer.OrdinalIgnoreCase)
                    : new(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                return new(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public void Set(CatalogEntry entry, bool pending) => Set(Key(entry.Kind, entry.Id), new Record(entry.Version, entry.Folder, pending, entry.Source, pending ? entry.Sha256 : null));

    internal void Remove(string key)
    {
        lock (Lock)
        {
            var all = Read();
            if (!all.Remove(key)) return;
            File.WriteAllText(StoreFile, JsonSerializer.Serialize(all));
        }
    }

    internal void Set(string key, Record record)
    {
        lock (Lock)
        {
            var all = Read();
            all[key] = record;
            Directory.CreateDirectory(dataDir);
            var tmp = StoreFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all));
            File.Move(tmp, StoreFile, overwrite: true);
        }
    }

    /// <summary>
    /// Is the entry installed, with which version, is an update waiting for a restart, and which source did it
    /// come from (null: bundled or installed by hand)? A plugin counts by its folder (user or bundled) or plugin
    /// id; a skin by its folder. For packages installed through the catalog the recorded version wins over
    /// what plugin.json says.
    /// </summary>
    public (bool Installed, string? Version, bool Pending, string? Source) Lookup(CatalogEntry entry, IEnumerable<PluginInfo> plugins)
    {
        var record = Read().GetValueOrDefault(Key(entry.Kind, entry.Id));
        if (entry.Kind == CatalogKind.Skin)
        {
            return Directory.Exists(Path.Combine(dataDir, "skins", entry.Folder)) ? (true, record?.Version, false, record?.Source) : (false, null, false, null);
        }

        var info = plugins.FirstOrDefault(p => p.Folder.Equals(entry.Folder, StringComparison.OrdinalIgnoreCase) || p.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase));
        var onDisk = Directory.Exists(Path.Combine(dataDir, "plugins", entry.Folder));
        if (info is null && !onDisk) return (false, null, false, null);
        return (true, record?.Version ?? info?.Version, record is { Pending: true }, record?.Source);
    }
}

/// <summary>Plugin updates that wait for the next start, because the old files are in use while CouchHud runs.</summary>
public static class PendingPackages
{
    public static string Folder(string dataDir) => Path.Combine(dataDir, "pending");

    internal static void Stage(string dataDir, CatalogEntry entry, string verifiedZip)
    {
        Directory.CreateDirectory(Folder(dataDir));
        File.Copy(verifiedZip, ZipFor(dataDir, CatalogRegistry.Key(entry.Kind, entry.Id)), overwrite: true);
    }

    private static string ZipFor(string dataDir, string key) => Path.Combine(Folder(dataDir), key.Replace(':', '-') + ".zip");

    internal static string Sha256Of(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
    }

    internal static void WarnIfManifestDiffers(string pluginDir, string catalogVersion)
    {
        var declared = PluginManager.ReadManifest(pluginDir)?.Version;
        if (declared is not null && declared != catalogVersion)
        {
            Console.Error.WriteLine($"[catalog] {Path.GetFileName(pluginDir)}: plugin.json says {declared}, catalog says {catalogVersion} (catalog version counts)");
        }
    }

    /// <summary>
    /// Call at startup before the plugins load. Never throws: a failure is logged and the staged update dropped.
    /// The entry is marked as done before the attempt, so even a crash in the middle cannot repeat at every boot.
    /// </summary>
    public static IReadOnlyList<string> ApplyAll(string dataDir, Action<string>? log = null)
    {
        var applied = new List<string>();
        try
        {
            var registry = new CatalogRegistry(dataDir);
            foreach (var (key, record) in registry.Read().Where(r => r.Value.Pending).ToList())
            {
                var zip = ZipFor(dataDir, key);
                try
                {
                    registry.Set(key, record with { Pending = false });
                    if (!File.Exists(zip)) throw new FileNotFoundException("staged package missing");
                    // The folder for staged packages and catalog-installed.json are writable by every program of the user: only a package
                    // that has its entry in the signed trust store, with the SHA-256 that was verified when it was downloaded, goes in. A
                    // record without such an entry (a folder added to the registry by something else) is refused.
                    var trust = new PluginTrust(dataDir);
                    var meta = trust.PendingFor(record.Folder) ?? throw new InvalidDataException("no verified entry for the staged package");
                    // One handle that keeps writers out: the package is hashed and unpacked from the same bytes (no swap in between), and
                    // the trust store records the hash of the unpacked files taken before they moved into the plugins folder.
                    using (var stream = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (!CryptographicEquals(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream)), meta.Hash))
                            throw new InvalidDataException("the staged package is not the one that was verified");
                        stream.Position = 0;
                        trust.TakePending(record.Folder);
                        // An entry without a statement (an unsigned catalog package): never over a plugin pinned to a developer's key.
                        var unsigned = meta.PluginId is null && meta.KeyFingerprint is null;
                        if (unsigned && trust.Entry(record.Folder)?.KeyFingerprint is not null)
                            throw new InvalidDataException("signed plugin without its verified statement");
                        var target = PackageInstaller.Install(stream, record.Folder, Path.Combine(dataDir, "plugins"), PackageInstaller.Kind.Plugin, record.Folder, out var folderHash);
                        WarnIfManifestDiffers(target, record.Version);
                        trust.Record(record.Folder, folderHash, unsigned ? new PluginTrustEntry { Hash = "", Origin = PluginOrigin.Local } : meta);
                    }
                    applied.Add(key);
                }
                catch (Exception ex)
                {
                    log?.Invoke($"[catalog] pending update {key} failed: {ex.Message}");
                    try { registry.Remove(key); } catch (Exception inner) { log?.Invoke($"[catalog] {inner.Message}"); }
                }
                finally
                {
                    try { File.Delete(zip); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"[catalog] applying pending updates failed: {ex.Message}");
        }
        return applied;
    }

    internal static bool CryptographicEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(a.ToLowerInvariant()), System.Text.Encoding.ASCII.GetBytes(b.ToLowerInvariant()));
}
