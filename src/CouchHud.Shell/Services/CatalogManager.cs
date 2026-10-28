using System.Net.Http;
using CouchHud.Core.Config;
using CouchHud.Core.Plugins;
using CouchHud.Core.Localization;

namespace CouchHud.Shell.Services;

/// <summary>A catalog entry as the UI shows it.</summary>
public sealed record CatalogRow(
    string Id,
    string Name,
    string Description,
    string? Author,
    string Kind,
    string Version,
    string? InstalledVersion,
    /// <summary>notInstalled, installed, update or unavailable.</summary>
    string Status,
    bool RestartPending,
    string? Reason,
    string? Homepage,
    /// <summary>A data: URI of the verified icon from the local cache (never the address itself), or null.</summary>
    string? Icon,
    string? MinCouchHud,
    /// <summary>The source has no valid signature (a source the user added).</summary>
    bool Unsigned)
{
    /// <summary>firstParty (signed with CouchHUD's key), thirdParty (a developer's own key: the "Fremdentwickler" warning) or local (no statement).</summary>
    public string Origin { get; init; } = "local";

    public bool ThirdParty => Origin == "thirdParty";

    /// <summary>The short form of the publisher key's fingerprint (third-party), shown next to the author.</summary>
    public string? Publisher { get; init; }

    /// <summary>From a catalog the user added: third-party throughout, whatever the entry says.</summary>
    public bool Extra { get; init; }

    public string? License { get; init; }
    public IReadOnlyList<string> Capabilities { get; init; } = [];
    public long? Size { get; init; }

    /// <summary>Why an unavailable entry is: needsNewer (CouchHUD too old) or officialFirst (load CouchHUD's own catalog first); null otherwise.</summary>
    public string? ReasonCode { get; init; }

    /// <summary>An update that asks for capabilities the installed version does not have: the host treats it like a new install (warning and code again).</summary>
    public bool AddsCapabilities { get; init; }
}

/// <summary>The detail page of an entry: the row, the long text, the verified screenshots and the changelog.</summary>
public sealed record CatalogDetail(CatalogRow Row, string? LongDescription, IReadOnlyList<string> Screenshots, string? Changelog, string? ChangelogUrl, string? Warning);

public sealed record CatalogState(IReadOnlyList<CatalogRow> Entries, bool FromCache, long? FetchedAt, string? Error, string Version, string? ErrorCode = null);

/// <summary>
/// Plugin and skin catalog for the host: loads the indexes, works out what is installed or outdated,
/// installs through <see cref="CatalogService"/> and looks for updates quietly in the background.
/// </summary>
public sealed class CatalogManager
{
    private readonly ConfigStore _config;
    private readonly PluginManager _plugins;
    private readonly CatalogService _service;
    private readonly string _dataDir;
    private readonly Func<bool> _gameRunning;
    private readonly Action<string, string, string> _notify;
    private readonly Action<IReadOnlyList<CatalogRow>> _updatesChanged;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _announced = new(StringComparer.OrdinalIgnoreCase);
    private volatile CatalogResult? _last;

    /// <summary>Counts <see cref="Invalidate"/>: a load or an icon run that started before it must not publish its result afterwards.</summary>
    private int _generation;
    private string? _lastLanguage;

    public IReadOnlyList<CatalogRow> Updates { get; private set; } = [];

    /// <summary>Gets the skins back that left the app in 0.6 (see <see cref="CouchHud.Core.Skins.SkinRestore"/>), through the same service as every catalog install.</summary>
    public CouchHud.Core.Skins.SkinRestore SkinRestore => new(_dataDir, _restoreService, () => CatalogRules.AppVersion);

    /// <summary>Own service with the official source only: the restore never asks a catalog the user added.</summary>
    private readonly CatalogService _restoreService;

    /// <summary>The display name of an entry of the last loaded index (the downloads page), else the id.</summary>
    public string NameOf(string id) =>
        _last?.Entries.FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.Name is { Length: > 0 } name ? name : id;

    public CatalogManager(ConfigStore config, PluginManager plugins, string dataDir, Func<bool> gameRunning,
        Action<string, string, string> notify, Action<IReadOnlyList<CatalogRow>> updatesChanged)
    {
        _config = config;
        _plugins = plugins;
        _dataDir = dataDir;
        _gameRunning = gameRunning;
        _notify = notify;
        _updatesChanged = updatesChanged;
        // No timeout on the client itself: the index and the download have their own, different ones.
        // No redirects by themselves and, without the developer switch, public addresses only (CatalogHosts): the catalogs of other developers
        // are untrusted input.
        var http = new HttpClient(CatalogHosts.CreateHandler(() => config.Current.CatalogAllowLocal)) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"CouchHud/{CatalogRules.AppVersion}");
        _service = new CatalogService(http, dataDir, () => OfficialAnd(config.Current), () => config.Current.CatalogAllowLocal, Strings.Language, null, () => Strings.Language,
            () => config.Current.ExtraCatalogSources);
        _restoreService = CouchHud.Core.Skins.SkinRestore.OfficialOnlyService(http, dataDir, () => config.Current.CatalogAllowLocal, () => Strings.Language);
        _suggest = new SuggestionCatalog(_service, () => config.Current.CatalogUpdateCheck && !gameRunning(), () => SignedEntriesChanged?.Invoke());
        _media = new CatalogMediaCache(dataDir, http, () => config.Current.CatalogAllowLocal);
    }

    /// <summary>
    /// The official catalog first, always (whatever config.json lists: it cannot be removed or switched off), then, only with the developer
    /// switch, the further entries of the old developer list. Catalogs of other developers are <see cref="AppConfig.ExtraCatalogSources"/>.
    /// </summary>
    internal static IReadOnlyList<string> OfficialAnd(AppConfig c) => CatalogSourceRules.OfficialAnd(c.CatalogSources, c.CatalogAllowLocal);

    /// <summary>
    /// Forgets the loaded list and the icons: after a catalog was added or removed nothing may be installed from, or shown of, the old list.
    /// </summary>
    public void Invalidate()
    {
        lock (_icons)
        {
            Interlocked.Increment(ref _generation);
            _last = null;
            _suggest.Invalidate();
            _icons.Clear();
        }
    }

    /// <summary>The entry comes from a catalog the user added (third-party throughout); false for an unknown entry.</summary>
    public async Task<bool> IsExtraAsync(string id, string kind)
    {
        var wanted = kind == "skin" ? CatalogKind.Skin : CatalogKind.Plugin;
        var result = await CurrentAsync();
        return result.Entries.Any(e => e.Extra && e.Kind == wanted && e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The loaded list, or a fresh load when there is none (after <see cref="Invalidate"/>); kept only if no invalidation came meanwhile.</summary>
    private async Task<CatalogResult> CurrentAsync()
    {
        var generation = Volatile.Read(ref _generation);
        if (_last is { } known) return known;
        var loaded = await _service.LoadAsync();
        lock (_icons)
        {
            if (generation == _generation) _last = loaded;
        }
        RaiseIfOfficial(loaded);
        return loaded;
    }

    /// <summary>
    /// The signed entries the suggestions read became available (the saved copy or a load finished). Raised from whatever thread did the
    /// load, after the list is set; the UI hears of it and asks again instead of polling. Not raised for a list without an official entry.
    /// </summary>
    public Action? SignedEntriesChanged { get; set; }

    private void RaiseIfOfficial(CatalogResult result)
    {
        if (SuggestionCatalog.HasOfficial(result)) SignedEntriesChanged?.Invoke();
    }

    private readonly SuggestionCatalog _suggest;
    private readonly CatalogMediaCache _media;
    private readonly Dictionary<string, string?> _icons = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The index (fresh, or the saved copy when offline) with the status of every entry.</summary>
    public async Task<CatalogState> GetAsync(bool refresh)
    {
        // A catalog added or removed while this runs: what was loaded is dropped and the run starts over.
        for (var attempt = 0; ; attempt++)
        {
            var generation = Volatile.Read(ref _generation);
            var result = _last;
            if (refresh || result is null || _lastLanguage != Strings.Language)
            {
                result = await _service.LoadAsync();
                lock (_icons)
                {
                    if (generation != _generation && attempt < 3) continue;
                    if (generation == _generation)
                    {
                        _last = result;
                        _lastLanguage = Strings.Language;
                    }
                }
                RaiseIfOfficial(result);
                refresh = false;
            }
            await LoadIconsAsync(result, generation);
            if (generation != Volatile.Read(ref _generation))
            {
                if (attempt < 3) continue;
                // Still changing after three runs: what is published now (or nothing yet), never a description of an outdated result.
                return _last is { } published ? Describe(published) : new CatalogState([], false, null, null, CatalogRules.AppVersion.ToString());
            }
            return Describe(result);
        }
    }

    /// <summary>
    /// The statement of an entry if it checks out (signature, key pin, revocation); null without one or when it does not (the install
    /// refuses it then with the reason).
    /// </summary>
    private VerifiedStatement? TryVerify(CatalogEntry entry)
    {
        if (entry.Statement is null || entry.Kind != CatalogKind.Plugin) return null;
        try
        {
            return _service.VerifyEntry(entry);
        }
        catch (Exception ex) when (ex is CatalogException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // Fail closed, never into the page: an entry that cannot be checked (also a damaged list of approved plugins) counts as unverified.
            return null;
        }
    }

    /// <summary>Icons through the verified cache, four at a time; an icon that does not check out stays empty.</summary>
    private async Task LoadIconsAsync(CatalogResult result, int generation)
    {
        using var slots = new SemaphoreSlim(4);
        // Never longer than this for the whole list, and not for more than a hundred entries: a catalog cannot stall the page.
        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await Task.WhenAll(result.Entries.Take(100).Select(async e =>
            {
                var key = $"{e.Kind}:{e.Id}@{e.Version}";
                lock (_icons) if (_icons.ContainsKey(key)) return;
                await slots.WaitAsync(overall.Token);
                try
                {
                    // An entry of a catalog the user added: media only from a package statement that checks out (the signed hash decides what is
                    // shown); the unsigned index's addresses are never fetched.
                    var verified = TryVerify(e);
                    var icon = e.Extra && (verified is null || _service.BehindProxy(e.IconUrl)) ? null : await _media.IconAsync(e, verified?.Statement, overall.Token);
                    lock (_icons)
                    {
                        if (generation == _generation) _icons[key] = icon;
                    }
                }
                finally
                {
                    slots.Release();
                }
            }));
        }
        catch (OperationCanceledException)
        {
            // Out of time: what was loaded is shown, the rest has no icon this time.
        }
    }

    private string? IconOf(CatalogEntry e)
    {
        lock (_icons) return _icons.GetValueOrDefault($"{e.Kind}:{e.Id}@{e.Version}");
    }

    /// <summary>The detail page: long text, verified screenshots, changelog and (third-party) the warning.</summary>
    public async Task<CatalogDetail> GetDetailAsync(string id, string kind)
    {
        var result = await CurrentAsync();
        var entry = Find(result, id, kind);
        var verified = TryVerify(entry);
        var shots = await _media.ScreenshotsAsync(entry, entry.Extra && entry.Screenshots.Any(_service.BehindProxy) ? null : verified?.Statement);
        var row = Describe(result).Entries.First(r => r.Id.Equals(entry.Id, StringComparison.OrdinalIgnoreCase) && r.Kind == (entry.Kind == CatalogKind.Skin ? "skin" : "plugin"));
        var changelog = verified?.Statement.Changelog ?? entry.Changelog;
        var changelogUrl = changelog is not null && CatalogParser.IsAllowedUrl(changelog, false) ? changelog : null;
        return new CatalogDetail(row, verified?.Statement.Description ?? entry.LongDescription, shots, changelogUrl is null ? changelog : null, changelogUrl,
            verified?.Origin == PluginOrigin.ThirdParty ? Strings.Get("plugin.thirdParty") : null);
    }

    private static CatalogEntry Find(CatalogResult result, string id, string kind)
    {
        var wanted = kind == "skin" ? CatalogKind.Skin : CatalogKind.Plugin;
        return result.Entries.Where(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && e.Kind == wanted).OrderBy(e => e.Problem is null ? 0 : 1).FirstOrDefault()
            ?? throw new InvalidOperationException(Strings.Get("catalog.notFound"));
    }

    private CatalogState Describe(CatalogResult result)
    {
        var app = CatalogRules.AppVersion;
        var registry = new CatalogRegistry(_dataDir);
        var plugins = _plugins.Plugins;
        var rows = result.Entries.Select(e =>
        {
            var (installed, version, pending, origin) = registry.Lookup(e, plugins);
            var originProblem = CatalogRules.OriginProblem(e, installed, origin);
            var status = CatalogRules.StatusOf(e, installed, version, app);
            var word = status switch
            {
                CatalogStatus.NotInstalled => "notInstalled",
                CatalogStatus.UpdateAvailable => "update",
                CatalogStatus.Unavailable => "unavailable",
                _ => "installed"
            };
            // Another source must not take over what a different one installed: shown as installed, never as update.
            if (originProblem is not null) word = "installed";
            var verified = TryVerify(e);
            // A statement that does not check out: the entry cannot be installed (the install says why).
            if (e.Statement is not null && e.Kind == CatalogKind.Plugin && verified is null && !installed) word = "unavailable";
            return new CatalogRow(e.Id, verified?.Statement.Name ?? e.Name, verified?.Statement.Summary ?? e.Description, verified?.Statement.Author ?? e.Author,
                e.Kind == CatalogKind.Skin ? "skin" : "plugin", e.Version, version, word, pending,
                originProblem ?? (installed && word == "installed" ? null : CatalogRules.Reason(e, app)), e.Homepage, IconOf(e), e.MinCouchHud, e.Unsigned)
            {
                Extra = e.Extra,
                Origin = verified?.Origin switch { PluginOrigin.FirstParty => "firstParty", PluginOrigin.ThirdParty => "thirdParty", _ => e.Extra && e.Kind == CatalogKind.Plugin ? "thirdParty" : "local" },
                Publisher = verified?.Origin == PluginOrigin.ThirdParty ? PackageStatements.ShortFingerprint(verified.KeyFingerprint) : null,
                License = verified?.Statement.License ?? e.License,
                Capabilities = verified?.Statement.Capabilities ?? e.Capabilities,
                Size = verified?.Statement.Size ?? e.Size,
                AddsCapabilities = word == "update" && e.Kind == CatalogKind.Plugin && verified is not null
                    && verified.Statement.Capabilities.Except(new PluginTrust(_dataDir).Entry(e.Folder)?.Capabilities ?? (IEnumerable<string>)PluginPolicy.Legacy).Any(),
                ReasonCode = e.Problem is not null && e.Extra && e.Problem == Strings.Get("catalog.source.officialFirst") ? "officialFirst"
                    : e.Problem is null && !CatalogRules.IsCompatible(e, app) ? "needsNewer" : null
            };
        }).ToList();
        return new CatalogState(rows, result.FromCache, result.FetchedAt, result.Error, app.ToString(), result.ErrorCode);
    }

    /// <param name="approve">The host's gate (see Hud.RequirePluginChange); it throws to stop. Asked after the statement checked out, before the download.</param>
    public async Task<(string Message, bool RestartNeeded, CatalogState State)> InstallAsync(string id, string kind, Action? started = null, Func<PluginInstallRequest, bool>? approve = null)
    {
        if (!await _gate.WaitAsync(0)) throw new InvalidOperationException(Strings.Get("catalog.installRunning"));
        try
        {
            // Only now this run is the one: a call turned away above never touches what the running one shows.
            started?.Invoke();
            var generation = Volatile.Read(ref _generation);
            var result = await CurrentAsync();
            var entry = Find(result, id, kind);
            InstallOutcome outcome;
            try
            {
                outcome = await _service.InstallAsync(entry, CatalogRules.AppVersion, _plugins.Plugins, approve: approve);
            }
            catch (CatalogException ex)
            {
                throw new InvalidOperationException(ex.Message);
            }
            lock (_icons)
            {
                if (generation == _generation) _last = result;
            }
            RaiseIfOfficial(result);
            PublishUpdates(Describe(result), announce: false);
            return (outcome.Message, outcome.RestartNeeded, Describe(result));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes a user plugin (the host's gate comes first). True when it is gone now, false when it goes at the next start.</summary>
    public bool Uninstall(string folder)
    {
        var gone = _service.UninstallPlugin(folder);
        if (_last is { } last) PublishUpdates(Describe(last), announce: false);
        return gone;
    }

    /// <summary>The entries of CouchHUD's own signed catalog as last loaded (for the suggestions; never a source the user added).</summary>
    public IReadOnlyList<CatalogEntry> SignedEntries =>
        (_last?.Entries ?? _suggest.Entries).Where(e => !e.Unsigned && CatalogService.IsDefaultSource(e.Source)).ToList();

    /// <summary>
    /// What a suggestion may point to: an entry of CouchHUD's own signed catalog whose plugin statement checks out (so the install goes ahead
    /// without the developer switch). An entry without a statement would end in a refusal: it is never suggested.
    /// </summary>
    public IReadOnlyList<CatalogEntry> InstallableSignedEntries =>
        SignedEntries.Where(e => e.Kind == CatalogKind.Plugin && e.Problem is null && TryVerify(e) is not null).ToList();

    /// <summary>
    /// Makes the signed entries available to the suggestions on a normal start, without the user opening the catalog: the saved copy first
    /// (no network at all). Only without any saved copy, and only while update checks are on, the index is fetched once, the same single fetch
    /// the catalog page does. Never while a game runs. Nothing is installed or shown by this.
    /// </summary>
    public Task PrepareSuggestionsAsync() => _suggest.PrepareAsync(() => _last is not null);

    /// <summary>One quiet pass: loads the catalogs and tells about updates for what is installed. Never while a game runs.</summary>
    public async Task CheckAsync()
    {
        if (!_config.Current.CatalogUpdateCheck || _gameRunning()) return;
        try
        {
            var generation = Volatile.Read(ref _generation);
            var loaded = await _service.LoadAsync();
            lock (_icons)
            {
                if (generation == _generation) _last = loaded;
            }
            RaiseIfOfficial(loaded);
            PublishUpdates(Describe(loaded), announce: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[catalog] background check failed: {ex.Message}");
        }
    }

    private void PublishUpdates(CatalogState state, bool announce)
    {
        var updates = state.Entries.Where(r => r.Status == "update").ToList();
        Updates = updates;
        _updatesChanged(updates);
        if (!announce) return;
        // Tell once per version, not at every check.
        var fresh = updates.Where(u => _announced.Add($"{u.Kind}:{u.Id}@{u.Version}")).ToList();
        if (fresh.Count == 0) return;
        _notify("Plugins", fresh.Count == 1 ? Strings.Ref("catalog.updateFor", fresh[0].Name) : Strings.Ref("catalog.pluginUpdates", fresh.Count),
            string.Join(", ", fresh.Take(4).Select(u => $"{u.Name} {u.InstalledVersion} → {u.Version}")));
    }

    /// <summary>Starts a few minutes after boot, then every 12 hours.</summary>
    public void StartBackgroundChecks()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMinutes(5));
            while (true)
            {
                try
                {
                    await CheckAsync();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[catalog] background check crashed: {ex}");
                }
                await Task.Delay(TimeSpan.FromHours(12));
            }
        });
    }
}
