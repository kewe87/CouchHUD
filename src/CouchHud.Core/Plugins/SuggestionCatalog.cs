using System.Net.Http;

namespace CouchHud.Core.Plugins;

/// <summary>
/// The signed catalog for the plugin suggestions on a normal start, kept apart from the list the catalog page shows: first the saved copy of
/// CouchHUD's own index (re-verified, no network); only when there is none, and only while fetching is allowed, the index is fetched once.
/// A result without an entry of the official source (nothing saved, offline, only a user-added catalog) is never kept, so it can neither
/// stand in for the page's list nor stop a later try. Nothing is installed or shown from here.
/// </summary>
public sealed class SuggestionCatalog(CatalogService service, Func<bool> mayFetch, Action? published = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile CatalogResult? _result;
    private int _generation;

    public IReadOnlyList<CatalogEntry> Entries => _result?.Entries ?? [];

    /// <summary>A catalog was added or removed: what was prepared is dropped, and a run that started before cannot publish afterwards.</summary>
    public void Invalidate()
    {
        Interlocked.Increment(ref _generation);
        _result = null;
    }

    /// <summary>The result holds an entry of CouchHUD's own signed index (only that counts for the suggestions and their event).</summary>
    public static bool HasOfficial(CatalogResult r) => r.Entries.Any(e => !e.Unsigned && CatalogService.IsDefaultSource(e.Source));

    /// <summary>One run at a time. <paramref name="pageHasList"/>: the catalog page has its own loaded list, which wins; nothing to do then.</summary>
    public async Task PrepareAsync(Func<bool> pageHasList)
    {
        if (_result is not null || pageHasList()) return;
        await _gate.WaitAsync();
        try
        {
            if (_result is not null || pageHasList()) return;
            var generation = Volatile.Read(ref _generation);
            CatalogResult? found = null;
            try
            {
                var cached = await service.LoadAsync(cacheOnly: true);
                if (HasOfficial(cached)) found = cached;
                else if (mayFetch())
                {
                    var fetched = await service.LoadAsync();
                    if (HasOfficial(fetched)) found = fetched;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or CatalogException or IOException or UnauthorizedAccessException or OperationCanceledException or System.Text.Json.JsonException)
            {
                Console.Error.WriteLine($"[catalog] suggestions: catalog not prepared: {ex.Message}");
                return;
            }
            if (found is not null && generation == Volatile.Read(ref _generation))
            {
                _result = found;
                published?.Invoke();
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
