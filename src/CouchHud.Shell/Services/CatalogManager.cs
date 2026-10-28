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
    string? Icon,
    string? MinCouchHud,
    /// <summary>The source has no valid signature (a source the user added).</summary>
    bool Unsigned);

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
    private CatalogResult? _last;
    private string? _lastLanguage;

    public IReadOnlyList<CatalogRow> Updates { get; private set; } = [];

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
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"CouchHud/{CatalogRules.AppVersion}");
        _service = new CatalogService(http, dataDir, () => config.Current.CatalogSources.ToList(), () => config.Current.CatalogAllowLocal, Strings.Language, null, () => Strings.Language);
    }

    /// <summary>The index (fresh, or the saved copy when offline) with the status of every entry.</summary>
    public async Task<CatalogState> GetAsync(bool refresh)
    {
        if (refresh || _last is null || _lastLanguage != Strings.Language)
        {
            _last = await _service.LoadAsync();
            _lastLanguage = Strings.Language;
        }
        return Describe(_last);
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
            return new CatalogRow(e.Id, e.Name, e.Description, e.Author, e.Kind == CatalogKind.Skin ? "skin" : "plugin", e.Version, version, word, pending,
                originProblem ?? (installed && word == "installed" ? null : CatalogRules.Reason(e, app)), e.Homepage, e.IconUrl, e.MinCouchHud, e.Unsigned);
        }).ToList();
        return new CatalogState(rows, result.FromCache, result.FetchedAt, result.Error, app.ToString(), result.ErrorCode);
    }

    public async Task<(string Message, bool RestartNeeded, CatalogState State)> InstallAsync(string id, string kind, Action? started = null)
    {
        if (!await _gate.WaitAsync(0)) throw new InvalidOperationException(Strings.Get("catalog.installRunning"));
        try
        {
            // Only now this run is the one: a call turned away above never touches what the running one shows.
            started?.Invoke();
            var result = _last ?? await _service.LoadAsync();
            var wanted = kind == "skin" ? CatalogKind.Skin : CatalogKind.Plugin;
            var entry = result.Entries.Where(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && e.Kind == wanted).OrderBy(e => e.Problem is null ? 0 : 1).FirstOrDefault()
                ?? throw new InvalidOperationException(Strings.Get("catalog.notFound"));
            InstallOutcome outcome;
            try
            {
                outcome = await _service.InstallAsync(entry, CatalogRules.AppVersion, _plugins.Plugins);
            }
            catch (CatalogException ex)
            {
                throw new InvalidOperationException(ex.Message);
            }
            _last = result;
            PublishUpdates(Describe(result), announce: false);
            return (outcome.Message, outcome.RestartNeeded, Describe(result));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>One quiet pass: loads the catalogs and tells about updates for what is installed. Never while a game runs.</summary>
    public async Task CheckAsync()
    {
        if (!_config.Current.CatalogUpdateCheck || _gameRunning()) return;
        try
        {
            _last = await _service.LoadAsync();
            PublishUpdates(Describe(_last), announce: true);
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
