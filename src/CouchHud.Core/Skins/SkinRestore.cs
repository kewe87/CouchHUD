using System.Text.Json;
using CouchHud.Core.Plugins;

namespace CouchHud.Core.Skins;

/// <summary>What became of one skin that the settings still ask for but that is not installed.</summary>
public enum SkinRestoreOutcome
{
    /// <summary>The skin was already there; nothing was asked of the network.</summary>
    Present,
    /// <summary>Installed from the signed catalog, verified like any other catalog install.</summary>
    Installed,
    /// <summary>This start could not install it (offline, refused, not in the catalog); the next start tries again, a few times.</summary>
    Failed,
    /// <summary>Tried on <see cref="SkinRestore.MaxAttempts"/> starts of this version without success: nothing is asked any more, the skin can still be installed from the catalog by hand.</summary>
    GaveUp
}

public enum SkinRestoreGate
{
    Go,
    /// <summary>A game is running: wait.</summary>
    Later,
    /// <summary>The update check is off: no network.</summary>
    Off
}

public sealed record SkinRestoreResult(string CatalogId, SkinRestoreOutcome Outcome, string? Reason = null);

/// <summary>
/// Since 0.6 only the Couch skin ships with the app. A household whose skin was one of the four that moved to the catalog
/// (<see cref="SkinCatalog.MovedToCatalogIds"/>) keeps its look: on the first start CouchHUD installs that skin from the signed
/// catalog, exactly as a click in the catalog would (hash, size, origin and signature checks of <see cref="CatalogService"/>),
/// with no extra rights and no trust entry (a skin is a stylesheet, no code).
///
/// Bounded on purpose: at most one attempt per skin per start, and at most <see cref="MaxAttempts"/> starts per app version;
/// the counter is written before the attempt, so a crash in the middle cannot make it loop. Until it worked, and for good if it
/// never does, the page shows the Couch skin with a notice (ui/app.js).
/// </summary>
public sealed class SkinRestore(string dataDir, CatalogService catalog, Func<SemVer> app)
{
    public const int MaxAttempts = 3;

    /// <summary>Everything one run may take: the index (10 s) and a few small downloads.</summary>
    public static readonly TimeSpan RunBudget = TimeSpan.FromMinutes(3);

    private static readonly object Lock = new();
    private string StoreFile => Path.Combine(dataDir, "skin-restore.json");

    private sealed record Ledger(string Version, Dictionary<string, int> Attempts);

    /// <summary>
    /// May the restore go online now? Update checks off: never (no network, the skin stays as it is). A game running: later (the same pass is
    /// tried again when the game is over). Same rule as the quiet update checks and the suggestions.
    /// </summary>
    public static SkinRestoreGate Gate(bool updateCheck, bool gameBusy) =>
        !updateCheck ? SkinRestoreGate.Off : gameBusy ? SkinRestoreGate.Later : SkinRestoreGate.Go;

    /// <summary>
    /// The service the restore uses: the official source only (its signature is mandatory), never the catalogs the user added, so a restore
    /// asks the network for nothing but CouchHUD's own index and package.
    /// </summary>
    public static CatalogService OfficialOnlyService(HttpClient http, string dataDir, Func<bool> allowLocal, Func<string> language, string? publicKeyPem = null) =>
        new(http, dataDir, () => [CatalogSourceRules.OfficialUrl], allowLocal, language(), publicKeyPem, language, extraSources: null);

    /// <summary>The catalog ids of the moved skins that these skin values (household and profiles) ask for, each once.</summary>
    public static IReadOnlyList<string> Wanted(IEnumerable<string?> skinValues) =>
        skinValues.Select(v => SkinCatalog.MovedCatalogId(SkinCatalog.MigrateSkinId(v))).OfType<string>().Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Is the skin folder with its two files there?</summary>
    public bool IsInstalled(string catalogId) =>
        File.Exists(Path.Combine(dataDir, "skins", catalogId, "skin.json")) && File.Exists(Path.Combine(dataDir, "skins", catalogId, "skin.css"));

    /// <summary>Is another start allowed to try for this skin (nothing installed, attempts left)?</summary>
    public bool CanTry(string catalogId) => !IsInstalled(catalogId) && !Exhausted(catalogId);

    /// <summary>Starts without any answer from the catalog (offline) that are given back; after this many even those stop.</summary>
    public const int MaxUnreachableStarts = 20;
    private const string UnreachablePrefix = "unreachable:";

    private bool Exhausted(string catalogId)
    {
        lock (Lock)
        {
            var attempts = Read().Attempts;
            return attempts.GetValueOrDefault(catalogId) >= MaxAttempts || attempts.GetValueOrDefault(UnreachablePrefix + catalogId) >= MaxUnreachableStarts;
        }
    }

    /// <summary>One pass over the skins. Never throws for a network, catalog or package problem; those are results.</summary>
    public async Task<IReadOnlyList<SkinRestoreResult>> RunAsync(IEnumerable<string> catalogIds, CancellationToken ct = default)
    {
        var results = new List<SkinRestoreResult>();
        var todo = new List<string>();
        foreach (var id in catalogIds.Where(i => SkinCatalog.MovedToCatalogIds.Contains(i, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal))
        {
            if (IsInstalled(id)) results.Add(new SkinRestoreResult(id, SkinRestoreOutcome.Present));
            else if (Exhausted(id)) results.Add(new SkinRestoreResult(id, SkinRestoreOutcome.GaveUp));
            else todo.Add(id);
        }
        if (todo.Count == 0) return results;

        // Counted before anything is asked: a crash or a hang cannot turn this into a loop.
        try
        {
            foreach (var id in todo) Count(id);
        }
        catch (IOException ex)
        {
            foreach (var id in todo) results.Add(new SkinRestoreResult(id, SkinRestoreOutcome.Failed, ex.Message));
            return results;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(RunBudget);
        CatalogResult index;
        try
        {
            index = await catalog.LoadAsync(budget.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Refund(todo);
            foreach (var id in todo) results.Add(new SkinRestoreResult(id, SkinRestoreOutcome.Failed, ex.Message));
            return results;
        }
        // Not a single source answered (and no saved copy): nothing was decided about the skin, so the attempt is not used up.
        if (index.Entries.Count == 0 && index.Error is not null)
        {
            Refund(todo);
            foreach (var id in todo) results.Add(new SkinRestoreResult(id, SkinRestoreOutcome.Failed, index.Error));
            return results;
        }

        foreach (var id in todo)
        {
            // Only an entry of a source whose signature checked out, that is a skin with this very id and folder (the page's id is "user:<folder>").
            // Never an entry of a catalog the user added, whatever it claims or whether it is signed with a pinned key.
            var entry = index.Entries.FirstOrDefault(e => e.Kind == CatalogKind.Skin && !e.Extra && !e.Unsigned && e.Problem is null
                && e.Id.Equals(id, StringComparison.Ordinal) && e.Folder.Equals(id, StringComparison.Ordinal));
            if (entry is null)
            {
                // Only a catalog of another developer lists it: nothing was decided about the skin, so such a source cannot use up the attempts.
                if (index.Entries.Any(e => e.Kind == CatalogKind.Skin && e.Extra && e.Id.Equals(id, StringComparison.Ordinal))
                    && !index.Entries.Any(e => e.Kind == CatalogKind.Skin && !e.Extra && e.Id.Equals(id, StringComparison.Ordinal)))
                    Refund([id]);
                results.Add(new SkinRestoreResult(id, SkinRestoreOutcome.Failed, index.Error ?? "not in the catalog"));
                continue;
            }
            try
            {
                await catalog.InstallAsync(entry, app(), null, budget.Token);
                results.Add(new SkinRestoreResult(id, SkinRestoreOutcome.Installed));
            }
            catch (Exception ex) when (ex is CatalogException or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException or OperationCanceledException or InvalidDataException)
            {
                results.Add(new SkinRestoreResult(id, SkinRestoreOutcome.Failed, ex.Message));
            }
        }
        return results;
    }

    private Ledger Read()
    {
        var version = app().ToString();
        try
        {
            if (File.Exists(StoreFile) && JsonSerializer.Deserialize<Ledger>(File.ReadAllText(StoreFile)) is { Attempts: not null } ledger && ledger.Version == version) return ledger;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A broken file is as good as none; the attempts start again, still bounded by the next run's count.
        }
        // Another app version: the counting starts over, so an update gets its tries again.
        return new Ledger(version, new Dictionary<string, int>(StringComparer.Ordinal));
    }

    /// <summary>The index could not be loaded at all (network down at boot): the attempt is given back, and a separate, larger bound counts these.</summary>
    private void Refund(IEnumerable<string> ids)
    {
        try
        {
            foreach (var id in ids)
            {
                Count(id, -1);
                Count(UnreachablePrefix + id, +1);
            }
        }
        catch (IOException)
        {
            // The refund is best effort; the attempt stays used then, which only errs on the side of asking less.
        }
    }

    private void Count(string catalogId, int delta = 1)
    {
        lock (Lock)
        {
            var ledger = Read();
            ledger.Attempts[catalogId] = Math.Max(0, ledger.Attempts.GetValueOrDefault(catalogId) + delta);
            try
            {
                Directory.CreateDirectory(dataDir);
                var tmp = StoreFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(ledger));
                File.Move(tmp, StoreFile, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Without the file the count cannot hold; do not go online then.
                throw new IOException("skin restore: counter not saved", ex);
            }
        }
    }
}
