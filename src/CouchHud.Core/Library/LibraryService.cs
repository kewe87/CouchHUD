using CouchHud.Core.Config;
using CouchHud.Sdk;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Library;

/// <summary>
/// Combines all providers (built-in scanners, config entries, plugins) into one
/// library and keeps user state like favorites and launch times on top of it.
/// </summary>
public sealed class LibraryService(ConfigStore config, ArtworkResolver artwork, IEnumerable<ILibraryProvider> providers) : IDisposable
{
    /// <summary>A provider that takes longer (offline media server, hung plugin) is skipped for this scan.</summary>
    public TimeSpan ProviderTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>The clock of <see cref="ProviderTimeout"/> (tests drive it by hand).</summary>
    internal TimeProvider Time { get; init; } = TimeProvider.System;

    private readonly List<ILibraryProvider> _providers = [.. providers];
    private readonly object _lock = new();
    private IReadOnlyList<LibraryItem> _items = [];
    private int _artworkRunning;

    /// <summary>Raised from a background thread when artwork arrived after a scan.</summary>
    public event Action<IReadOnlyList<LibraryItem>>? Changed;

    public IReadOnlyList<LibraryItem> Items
    {
        get
        {
            lock (_lock) return _items;
        }
    }

    public void AddProvider(ILibraryProvider provider) => _providers.Add(provider);

    /// <summary>
    /// A provider left out of the next scan (a game store while Gaming is switched off, see FeatureAreas). What it found
    /// before is not deleted from the config: favourites, play times and overrides come back with the next scan that runs it.
    /// </summary>
    public Func<ILibraryProvider, bool>? SkipProvider { get; set; }

    /// <summary>Entries whose artwork is looked up and downloaded (those of switched off areas wait). Null: all.</summary>
    public Func<LibraryItem, bool>? FetchArtFor { get; set; }

    /// <summary>Whether the scan whose entries are shown left stores out (set together with the entries).</summary>
    private bool _partialScan;

    /// <summary>
    /// Whether the scan whose entries are shown saw everything it should: no store left out, no provider failed or timed out, the host's
    /// <see cref="ScanComplete"/> held before and after it. False before the first scan has finished. Only then can a record of the ignore
    /// list be called orphaned.
    /// </summary>
    private bool _scanTrusted;

    /// <summary>
    /// Set by the host: plugins are loaded and none is paused (Offline mode), so the plugin provider's answer is the real one and not an empty
    /// stand-in. Null: nothing to wait for.
    /// </summary>
    public Func<bool>? ScanComplete { get; set; }

    /// <summary>Whether a folder exists; the drive of a watched folder may be offline. A seam for tests.</summary>
    public Func<string, bool> DirectoryExists { get; set; } = Directory.Exists;

    /// <summary>Scans are numbered: one that finishes after a newer one started is dropped (its store choice may be out of date).</summary>
    private long _scanNumber;

    /// <summary>
    /// A watched folder may contain games a store already knows (e.g. D:\Games\SteamLibrary).
    /// The store entry wins: it has the better launch, artwork and playtime.
    /// </summary>
    internal static List<LibraryItem> WithoutFolderDuplicates(List<LibraryItem> items)
    {
        var storeDirs = items.Where(i => i.Source != "folder" && !string.IsNullOrEmpty(i.InstallDir))
            .Select(i => i.InstallDir!.TrimEnd('\\', '/') + "\\")
            .ToList();
        if (storeDirs.Count == 0) return items;
        return items.Where(i => i.Source != "folder" || i.InstallDir is null
            || !storeDirs.Any(d => (i.InstallDir.TrimEnd('\\', '/') + "\\").StartsWith(d, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    public async Task<IReadOnlyList<LibraryItem>> ScanAsync(CancellationToken ct = default)
    {
        var number = Interlocked.Increment(ref _scanNumber);
        var skip = SkipProvider;
        var scanned = _providers.Where(p => skip is null || !skip(p)).ToList();
        var partial = scanned.Count < _providers.Count;
        var completeBefore = ScanComplete?.Invoke() ?? true;
        var failed = 0;
        var results = await Task.WhenAll(scanned.Select(async p =>
        {
            var found = await SafeScan(p, ProviderTimeout, ct);
            if (found is null) Interlocked.Increment(ref failed);
            return found ?? [];
        }));
        var trusted = !partial && failed == 0 && completeBefore && (ScanComplete?.Invoke() ?? true);
        var cfg = config.Current;
        var custom = CustomEntries.From(cfg.Apps);

        var raw = WithoutFolderDuplicates(results.SelectMany(r => r).Concat(custom)
            .GroupBy(i => i.Id).Select(g => g.First()) // first provider wins on id clashes
            .ToList());
        // Only what the core's folder scan found is a folder guess (known by exe and game folder too); a plugin's entry never is.
        var folderItems = new HashSet<LibraryItem>(scanned.Zip(results).Where(p => IsFolderScan(p.First)).SelectMany(p => p.Second),
            ReferenceEqualityComparer.Instance);
        var folderGuesses = raw.Where(folderItems.Contains).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        // Hidden flags from before the ignore list move onto it, with the stable keys of what this scan found.
        if (cfg.Overrides.Values.Any(o => o?.Hidden == true))
            config.Update(c => LibraryIgnore.MigrateLegacy(c, raw, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), i => folderGuesses.Contains(i.Id)));
        lock (_lock)
        {
            // A newer scan started meanwhile: its result counts, this one changes nothing.
            if (number != Interlocked.Read(ref _scanNumber)) return _items;
            _folderGuesses = folderGuesses;
            _baseNames.Clear();
            _baseLastPlayed.Clear();
            foreach (var item in raw)
            {
                _baseNames[item.Id] = item.Name;
                _baseLastPlayed[item.Id] = item.LastPlayed;
            }
        }
        var ignored = IgnoredKeys();
        var merged = raw.Select(i => ApplyUserState(i, ignored, folderGuesses)).ToList();

        // The art without the user's picks: "automatic" is always this, whatever was picked before.
        var cached = artwork.ApplyCached(merged);
        var overrides = config.Current.Overrides;
        var withArt = cached.Select(i => i with { Art = ArtworkOverrides.Apply(i.Art, overrides.GetValueOrDefault(i.Id)) }).ToList();
        lock (_lock)
        {
            if (number != Interlocked.Read(ref _scanNumber)) return _items;
            _partialScan = partial;
            _scanTrusted = trusted;
            _scannedArt.Clear();
            foreach (var item in raw) _scannedArt[item.Id] = item.Art;
            _baseArt.Clear();
            foreach (var item in cached) _baseArt[item.Id] = item.Art;
            _items = withArt;
        }
        _ = FetchMissingArtworkAsync();
        return withArt;
    }

    public async Task<IReadOnlyList<LibraryItem>> GetAsync(bool rescan, CancellationToken ct = default) =>
        rescan || Items.Count == 0 ? await ScanAsync(ct) : Items;

    public LibraryItem? Find(string id) => Items.FirstOrDefault(i => i.Id == id);

    public LibraryItem MarkLaunched(string id)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        config.Update(c => c.ActiveProfile.LastLaunched[id] = now);
        return Replace(id, i => i with { LastPlayed = now });
    }

    public bool ToggleFavorite(string id)
    {
        // Taking a favorite off always works (also for an entry that is gone, e.g. uninstalled); only a library entry can become one.
        if (Find(id) is null && !config.Current.ActiveProfile.Favorites.Contains(id)) throw new KeyNotFoundException($"unknown item {id}");
        var favorite = false;
        var full = false;
        config.Update(c =>
        {
            var favorites = c.ActiveProfile.Favorites;
            favorite = !favorites.Remove(id);
            if (favorite && !FavoritesShelf.TryAdd(favorites, id)) (favorite, full) = (false, true);
        });
        if (full) throw new InvalidOperationException(Strings.Get("favorites.full", FavoritesShelf.MaxFavorites));
        if (Find(id) is not null) Replace(id, i => i with { Favorite = favorite });
        return favorite;
    }

    private LibraryItem Replace(string id, Func<LibraryItem, LibraryItem> change)
    {
        lock (_lock)
        {
            var list = _items.ToList();
            var index = list.FindIndex(i => i.Id == id);
            if (index < 0) throw new KeyNotFoundException($"unknown item {id}");
            list[index] = change(list[index]);
            _items = list;
            return list[index];
        }
    }

    /// <summary>Every key of the ignore list, for one pass over the library.</summary>
    private HashSet<string> IgnoredKeys() => LibraryIgnore.KeySet(config.Current.Ignored);

    /// <summary>Ids of the entries the core's folder scan found in the last scan (see <see cref="LibraryIgnore.KeysOf"/>).</summary>
    private HashSet<string> _folderGuesses = new(StringComparer.Ordinal);

    /// <summary>The provider whose finds are folder guesses: the core's folder scan (tests stand in for it, it reads the disk).</summary>
    internal Func<ILibraryProvider, bool> IsFolderScan { get; init; } = p => p is FolderGamesProvider;

    private HashSet<string> FolderGuesses()
    {
        lock (_lock) return _folderGuesses;
    }

    /// <summary>The ignore list keys of an entry of this library.</summary>
    private IReadOnlyList<string> KeysOf(LibraryItem item, IReadOnlySet<string>? folderGuesses = null) =>
        LibraryIgnore.KeysOf(item, config.Current.GameFolders, (folderGuesses ?? FolderGuesses()).Contains(item.Id));

    private LibraryItem ApplyUserState(LibraryItem item) => ApplyUserState(item, IgnoredKeys(), FolderGuesses());

    private LibraryItem ApplyUserState(LibraryItem item, IReadOnlySet<string> ignored, IReadOnlySet<string> folderGuesses)
    {
        var cfg = config.Current;
        var profile = cfg.ActiveProfile;
        profile.LastLaunched.TryGetValue(item.Id, out var launched);
        profile.PlaytimeMinutes.TryGetValue(item.Id, out var minutes);
        profile.LastSessionMinutes.TryGetValue(item.Id, out var lastSession);
        cfg.Overrides.TryGetValue(item.Id, out var custom);
        return item with
        {
            Favorite = profile.Favorites.Contains(item.Id),
            // Our own launch timestamps beat whatever the store recorded.
            LastPlayed = Math.Max(item.LastPlayed, launched),
            PlaytimeMinutes = minutes,
            LastSessionMinutes = lastSession,
            Name = string.IsNullOrWhiteSpace(custom?.Name) ? item.Name : custom!.Name!,
            Type = custom?.Type switch { "game" => ItemType.Game, "app" => ItemType.App, _ => item.Type },
            // On the ignore list by any stable key (a folder guess with a new id included); a flag from before the list until it is migrated.
            Hidden = custom?.Hidden == true || LibraryIgnore.IsIgnored(LibraryIgnore.KeysOf(item, cfg.GameFolders, folderGuesses.Contains(item.Id)), ignored)
        };
    }

    /// <summary>
    /// Rename, re-categorize or hide any entry; stored as an override (name, type) or on the ignore list (hidden) so rescans keep it.
    /// Who may hide is the host's decision (Hud.SetHidden); this only stores.
    /// </summary>
    private static string Clip(string name) => name.Length > ConfigLimits.MaxEntryNameLength ? name[..ConfigLimits.MaxEntryNameLength].Trim() : name;

    public LibraryItem EditItem(string id, string? name, string? type, bool? hidden)
    {
        if (Find(id) is null) throw new KeyNotFoundException($"unknown item {id}");
        if (type is not null && type is not ("game" or "app")) throw new ArgumentException("type must be game or app", nameof(type));
        // Every check before anything is stored: a full ignore list refuses the whole edit, never half of it.
        if (hidden == true) EnsureCanHide(id);
        if (name is not null || type is not null)
        {
            config.UpdateGrowing(c =>
            {
                var o = c.Overrides.TryGetValue(id, out var existing) ? existing : new ItemOverride();
                if (name is not null) o.Name = string.IsNullOrWhiteSpace(name) ? null : Clip(name.Trim());
                if (type is not null) o.Type = type;
                if (o.IsEmpty) c.Overrides.Remove(id);
                else c.Overrides[id] = o;
            });
        }
        if (hidden is not null) return SetHidden(id, hidden.Value);
        return Replace(id, i => ApplyUserState(i with { Name = BaseName(i) }));
    }

    /// <summary>
    /// Puts an entry on the ignore list ("Hide": out of every list, also after rescans and when a scanner finds it again) or takes it off
    /// ("Show again"). Keyed by the entry's stable identity (<see cref="LibraryIgnore.KeysOf"/>), not only its id. The entry stays in
    /// <see cref="Items"/> with <see cref="LibraryItem.Hidden"/> set: parental controls keep seeing it.
    /// </summary>
    public LibraryItem SetHidden(string id, bool hidden)
    {
        var item = Find(id) ?? throw new KeyNotFoundException($"unknown item {id}");
        var keys = KeysOf(item);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var before = LibraryIgnore.KeySet(config.Current.Ignored);
        config.Update(c =>
        {
            c.Ignored ??= [];
            if (hidden) LibraryIgnore.Hide(c.Ignored, item, keys, now, Strings.Get("library.ignoreFull", LibraryIgnore.MaxEntries));
            else LibraryIgnore.Show(c.Ignored, keys);
            // A flag from before the ignore list goes either way: the list is the one place now.
            if (c.Overrides.TryGetValue(id, out var o) && o.Hidden)
            {
                o.Hidden = false;
                if (o.IsEmpty) c.Overrides.Remove(id);
            }
        });
        var result = Replace(id, i => ApplyUserState(i with { Name = BaseName(i) }));
        // Other entries share a key (the same game folder, the same exe): they change with it right away, not only after the next scan.
        if (OthersShare(id, before, LibraryIgnore.KeySet(config.Current.Ignored))) ReapplyUserState();
        return Find(id) ?? result;
    }

    /// <summary>Whether an entry other than <paramref name="id"/> has a key whose state changed between the two key sets.</summary>
    private bool OthersShare(string id, HashSet<string> before, HashSet<string> after)
    {
        var changed = new HashSet<string>(before, StringComparer.Ordinal);
        changed.SymmetricExceptWith(after);
        if (changed.Count == 0) return false;
        var guesses = FolderGuesses();
        return Items.Any(i => i.Id != id && KeysOf(i, guesses).Any(changed.Contains));
    }

    /// <summary>Refuses (with the message for the user) when hiding the entry would need room the full ignore list does not have.</summary>
    public void EnsureCanHide(string id)
    {
        var item = Find(id) ?? throw new KeyNotFoundException($"unknown item {id}");
        if (!LibraryIgnore.CanHide(config.Current.Ignored, KeysOf(item)))
            throw new InvalidOperationException(Strings.Get("library.ignoreFull", LibraryIgnore.MaxEntries));
    }

    /// <summary>
    /// Records of the ignore list that no entry of the library matches any more (the game was uninstalled, a folder removed). Shown
    /// name-only under Settings › Library › Hidden entries with "Remove", so they never fill the list silently. Empty after a partial scan:
    /// an entry of a store left out may come back.
    /// </summary>
    public IReadOnlyList<IgnoredOrphan> IgnoredOrphans()
    {
        // Only a scan that saw everything proves an entry gone: not before the first scan, not after a skipped store, a failed provider,
        // plugins that are still loading or paused (they list nothing then), and not right now if that changed since the scan.
        lock (_lock)
        {
            if (_partialScan || !_scanTrusted) return [];
        }
        if (ScanComplete?.Invoke() == false) return [];
        var guesses = FolderGuesses();
        var live = Items.SelectMany(i => KeysOf(i, guesses)).ToHashSet(StringComparer.Ordinal);
        var folders = (config.Current.GameFolders ?? []).Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
        return [.. (config.Current.Ignored ?? [])
            .Where(e => e?.Keys is { Count: > 0 } && !e.Keys.Any(live.Contains) && !OnMissingFolder(e, folders))
            .Select(e => new IgnoredOrphan(e.Keys[0], e.Name, e.Source))];
    }

    /// <summary>
    /// A record with an exe or game folder key whose watched folder or drive is not there now (an unplugged disk, a network share that is
    /// down): the game may come back with it, so the record is not offered for removal.
    /// </summary>
    private bool OnMissingFolder(IgnoredEntry entry, List<string> folders)
    {
        foreach (var key in entry.Keys ?? [])
        {
            var path = key.StartsWith(LibraryIgnore.ExePrefix, StringComparison.Ordinal) ? key[LibraryIgnore.ExePrefix.Length..]
                : key.StartsWith(LibraryIgnore.DirPrefix, StringComparison.Ordinal) ? key[LibraryIgnore.DirPrefix.Length..] : null;
            if (path is null) continue;
            try
            {
                var root = Path.GetPathRoot(path);
                if (!string.IsNullOrEmpty(root) && !DirectoryExists(root)) return true;
                foreach (var watched in folders)
                {
                    var w = LibraryIgnore.NormalizePath(watched);
                    if (w.Length > 0 && path.StartsWith(w + "\\", StringComparison.Ordinal) && !DirectoryExists(watched)) return true;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or IOException)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Takes a record off the list that no entry matches any more (see <see cref="IgnoredOrphans"/>).</summary>
    public void RemoveIgnoredOrphan(string key)
    {
        var orphan = IgnoredOrphans().FirstOrDefault(o => o.Key == key) ?? throw new KeyNotFoundException($"no orphaned record {key}");
        // Only the records that are orphans themselves; a live record that shares a key stays.
        var orphans = IgnoredOrphans().Select(o => o.Key).ToHashSet(StringComparer.Ordinal);
        config.Update(c => LibraryIgnore.RemoveRecord(c.Ignored ??= [], orphan.Key, orphans));
    }

    /// <summary>Picks artwork for one slot ("cover", "hero", "logo", "square"): an https URL or a local image. Null goes back to automatic.</summary>
    public LibraryItem SetArtwork(string id, string slot, string? urlOrPath)
    {
        var url = string.IsNullOrWhiteSpace(urlOrPath) ? null : ArtworkOverrides.Normalize(urlOrPath);
        var result = StoreArtwork(id, slot, url);
        // A reset slot may still be empty (the lookup skipped it while the pick was there).
        if (url is null) _ = FetchMissingArtworkAsync();
        else if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) _ = CachePickedAsync(url);
        return result;
    }

    private LibraryItem StoreArtwork(string id, string slot, string? url)
    {
        if (Find(id) is null) throw new KeyNotFoundException($"unknown item {id}");
        ItemOverride? after = null;
        config.UpdateGrowing(c =>
        {
            var o = c.Overrides.TryGetValue(id, out var existing) ? existing : new ItemOverride();
            ArtworkOverrides.Set(o, slot, url);
            if (o.IsEmpty) c.Overrides.Remove(id);
            else c.Overrides[id] = o;
            after = o;
        });
        // Recomputed from the automatic art, so nothing is lost when a pick is reset.
        return Replace(id, i => i with { Art = ArtworkOverrides.Apply(BaseArt(i), after) });
    }

    private readonly Dictionary<string, int> _pickStarted = new();
    private readonly Dictionary<string, int> _pickStored = new();

    /// <summary>
    /// The pick of the UI: an online image from a downloadable source is fetched into the cache first, and only then stored as the pick, so
    /// the UI never points at a file that is not there yet. If the download fails nothing changes and the user gets a clear message.
    /// <paramref name="stillAllowed"/> runs right after the download, before anything is stored (the couch may have been locked meanwhile).
    /// Two picks for the same slot: the one chosen last wins; an older one that finishes later throws <see cref="PickSupersededException"/> and changes nothing.
    /// </summary>
    public async Task<LibraryItem> SetArtworkAsync(string id, string slot, string? urlOrPath, Action? stillAllowed = null, CancellationToken ct = default)
    {
        if (Find(id) is null) throw new KeyNotFoundException($"unknown item {id}");
        var url = string.IsNullOrWhiteSpace(urlOrPath) ? null : ArtworkOverrides.Normalize(urlOrPath);
        var key = id + "|" + slot;
        int mine;
        lock (_lock) _pickStarted[key] = mine = _pickStarted.GetValueOrDefault(key) + 1;
        var download = url is not null && artwork.Images is not null && ArtworkSources.IsAllowed(url);
        if (download)
        {
            bool cached;
            try
            {
                cached = await artwork.CacheNowAsync(url!, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"[artwork] picked image: {ex.Message}");
                cached = false;
            }
            if (!cached)
            {
                Console.Error.WriteLine($"[artwork] picked image could not be downloaded: {url}");
                throw new InvalidOperationException(Strings.Get("artwork.pickDownloadFailed"));
            }
        }
        stillAllowed?.Invoke();
        LibraryItem result;
        // The check and the store are one step: a pick that was chosen later and has already been stored is never overwritten.
        // A newer pick that failed or is still loading does not count (it never stored anything).
        lock (_lock)
        {
            if (_pickStored.GetValueOrDefault(key) > mine) throw new PickSupersededException();
            _pickStored[key] = mine;
            result = StoreArtwork(id, slot, url);
        }
        if (url is null) _ = FetchMissingArtworkAsync();
        Changed?.Invoke(Items);
        return result;
    }

    /// <summary>A picked online image goes to disk right away (the UI shows it from there), then the UI hears about it.</summary>
    private async Task CachePickedAsync(string url)
    {
        try
        {
            if (await artwork.CacheNowAsync(url)) Changed?.Invoke(Items);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[library] picked image: {ex.Message}");
        }
    }

    /// <summary>Empties the image cache (wrong or damaged pictures); what the library shows is fetched again in the background.</summary>
    public void ClearArtworkCache()
    {
        artwork.Images?.Clear();
        _ = FetchMissingArtworkAsync();
    }

    /// <summary>Candidates for the artwork picker.</summary>
    public Task<ArtworkChoices> GetArtworkChoicesAsync(string id, CancellationToken ct = default) =>
        artwork.GetChoicesAsync(Find(id) ?? throw new KeyNotFoundException($"unknown item {id}"), ct);

    /// <summary>
    /// "Reload artwork" for one entry: the cache times are ignored and every slot is looked up anew (see <see cref="ArtworkResolver.ReloadAsync"/>).
    /// Only the automatic art changes; a picked image stays, because the user's picks are applied on top afterwards.
    /// Returns "found", "nothing", "failed" or "busy".
    /// </summary>
    public async Task<string> ReloadArtworkAsync(string id, CancellationToken ct = default)
    {
        var item = Find(id) ?? throw new KeyNotFoundException($"unknown item {id}");
        LibraryItem automatic;
        lock (_lock) automatic = item with { Art = BaseArt(item) };
        var (updated, result) = await artwork.ReloadAsync(automatic, ct);
        if (result == ArtworkResolver.ReloadResult.Found)
        {
            IReadOnlyList<LibraryItem> merged;
            lock (_lock)
            {
                _baseArt[id] = updated.Art;
                var own = config.Current.Overrides.GetValueOrDefault(id);
                merged = _items.Select(i => i.Id == id ? i with { Art = ArtworkOverrides.Apply(updated.Art, own) } : i).ToList();
                _items = merged;
            }
            Changed?.Invoke(merged);
        }
        return result.ToString().ToLowerInvariant();
    }

    public IReadOnlyList<ItemCollection> GetCollections()
    {
        lock (_lock) return config.Current.ActiveProfile.Collections.Select(Clone).ToList();
    }

    /// <summary>Applies one change to the collections and returns the new list.</summary>
    public IReadOnlyList<ItemCollection> UpdateCollections(Action<List<ItemCollection>> change)
    {
        config.UpdateGrowing(c => change(c.ActiveProfile.Collections));
        return GetCollections();
    }

    private static ItemCollection Clone(ItemCollection c) => new() { Id = c.Id, Name = c.Name, Items = [.. c.Items], OnHome = c.OnHome };

    /// <summary>The name before user overrides, so clearing an override restores it.</summary>
    private Artwork BaseArt(LibraryItem item) => _baseArt.TryGetValue(item.Id, out var a) ? a : item.Art;

    private readonly Dictionary<string, Artwork> _baseArt = new();

    /// <summary>The art as the scanner gave it, before any look-up: where a changed store match starts again.</summary>
    private readonly Dictionary<string, Artwork> _scannedArt = new();

    /// <summary>
    /// What the store look-up of an entry goes by (<see cref="StoreMatches.Of"/>): Steam's app id, the user's pick, or the name the entry
    /// was scanned with. Never the name the user gave it: a rename must not change the age rating.
    /// </summary>
    public StoreLookup StoreLookupOf(LibraryItem item)
    {
        string scanned;
        lock (_lock) scanned = BaseName(item);
        return StoreMatches.Of(item, config.Current.Overrides.GetValueOrDefault(item.Id), scanned);
    }

    /// <summary>Texts, which of them are the user's, and the store match, for the edit menu (<paramref name="info"/>: the store's).</summary>
    public ItemInfoView GetItemInfo(string id, StoreInfo? info)
    {
        var item = Find(id) ?? throw new KeyNotFoundException($"unknown item {id}");
        var o = config.Current.Overrides.GetValueOrDefault(id);
        var shown = InfoOverrides.Merge(info, o);
        return new ItemInfoView(shown?.Description, shown?.Developers ?? [], shown?.Publishers ?? [], shown?.ReleaseDate, shown?.Genres ?? [],
            InfoOverrides.Custom(o), StoreMatches.View(item, o));
    }

    /// <summary>Own texts for an entry (see <see cref="InfoOverrides"/>); every field is checked before anything is stored.</summary>
    public void SetItemInfo(string id, InfoChange change)
    {
        if (Find(id) is null) throw new KeyNotFoundException($"unknown item {id}");
        config.UpdateGrowing(c =>
        {
            var o = c.Overrides.TryGetValue(id, out var existing) ? existing : new ItemOverride();
            InfoOverrides.Apply(o, change);
            if (o.IsEmpty) c.Overrides.Remove(id);
            else c.Overrides[id] = o;
        });
    }

    /// <summary>
    /// The store entry for an entry that is not Steam's own: an app id, "none", or null for automatic (<see cref="StoreMatches.Apply"/> for the
    /// age floor). The automatic art is looked up again from the scanned art; the user's own picks stay on top.
    /// </summary>
    public LibraryItem SetStoreMatch(string id, string? match, string? name, int? previousAge, bool confirmed)
    {
        var item = Find(id) ?? throw new KeyNotFoundException($"unknown item {id}");
        if (!StoreMatches.CanChoose(item)) throw new ArgumentException(Strings.Get("store.steamOwn", item.Name));
        StoreMatches.Validate(match);
        ItemOverride? after = null;
        config.UpdateGrowing(c =>
        {
            var o = c.Overrides.TryGetValue(id, out var existing) ? existing : new ItemOverride();
            StoreMatches.Apply(o, match, name, previousAge, confirmed);
            if (o.IsEmpty) c.Overrides.Remove(id);
            else c.Overrides[id] = o;
            after = o;
        });
        LibraryItem result;
        lock (_lock)
        {
            var current = _items.First(i => i.Id == id);
            var scanned = _scannedArt.TryGetValue(id, out var s) ? s : current.Art;
            var art = artwork.ApplyCached([current with { Art = scanned }])[0].Art;
            _baseArt[id] = art;
            result = Replace(id, i => i with { Art = ArtworkOverrides.Apply(art, after) });
        }
        Changed?.Invoke(Items);
        _ = FetchMissingArtworkAsync();
        return result;
    }

    /// <summary>The game the entry's artwork comes from, for the edit menu: automatic or the pinned one with its name.</summary>
    public ArtworkMatchView GetArtworkMatch(string id)
    {
        if (Find(id) is null) throw new KeyNotFoundException($"unknown item {id}");
        var o = config.Current.Overrides.GetValueOrDefault(id);
        return ArtworkMatches.View(o?.ArtworkMatch, o?.ArtworkMatchName);
    }

    /// <summary>
    /// Pins the game an entry's artwork comes from ("Search artwork": "steam:&lt;app id&gt;" or "sgdb:&lt;game id&gt;"), or null for automatic again.
    /// The pin is stored per entry and survives rescans and "Reload artwork"; it changes only the pictures, never the store match or the age
    /// rating. Online images picked for single slots go (they were of the wrong game); own pictures from the PC stay. The automatic art is
    /// looked up again from the scanned art.
    /// </summary>
    public LibraryItem SetArtworkMatch(string id, string? match, string? name)
    {
        var item = Find(id) ?? throw new KeyNotFoundException($"unknown item {id}");
        if (!ArtworkMatches.CanChoose(item)) throw new ArgumentException(Strings.Get("artwork.chooseOwnEntry"));
        var pin = ArtworkMatches.Validate(match);
        ItemOverride? after = null;
        config.UpdateGrowing(c =>
        {
            var o = c.Overrides.TryGetValue(id, out var existing) ? existing : new ItemOverride();
            // Online picks of the old game's pictures go only when another game is pinned; unpinning or the same pin leaves them.
            var otherGame = pin is not null && pin != o.ArtworkMatch;
            o.ArtworkMatch = pin;
            o.ArtworkMatchName = pin is null ? null : InfoOverrides.Clip(name, ArtworkMatches.MaxName);
            static string? KeepOwn(string? url) => url is not null && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? null : url;
            if (otherGame)
            {
                o.Cover = KeepOwn(o.Cover);
                o.Hero = KeepOwn(o.Hero);
                o.Logo = KeepOwn(o.Logo);
                o.Square = KeepOwn(o.Square);
            }
            if (o.IsEmpty) c.Overrides.Remove(id);
            else c.Overrides[id] = o;
            after = o;
        });
        LibraryItem result;
        lock (_lock)
        {
            var current = _items.First(i => i.Id == id);
            var scanned = _scannedArt.TryGetValue(id, out var s) ? s : current.Art;
            var art = artwork.ApplyCached([current with { Art = scanned }])[0].Art;
            _baseArt[id] = art;
            result = Replace(id, i => i with { Art = ArtworkOverrides.Apply(art, after) });
        }
        Changed?.Invoke(Items);
        _ = FetchMissingArtworkAsync();
        return result;
    }

    /// <summary>"Search artwork" for an entry (see <see cref="ArtworkResolver.SearchAsync"/>).</summary>
    public Task<ArtworkSearchResult> SearchArtworkAsync(string id, string query, CancellationToken ct = default)
    {
        var item = Find(id) ?? throw new KeyNotFoundException($"unknown item {id}");
        if (!ArtworkMatches.CanChoose(item)) throw new ArgumentException(Strings.Get("artwork.chooseOwnEntry"));
        return artwork.SearchAsync(query, ct);
    }

    /// <summary>
    /// An automatic store match lost the rating it had (<see cref="StoreInfoService.RatingDropped"/>): that rating stays as the age floor (the
    /// stricter of it and one already pending) until the parents confirm the match again (Edit › Store entry, <see cref="StoreMatches.Apply"/>).
    /// </summary>
    public void KeepAgeFloor(string id, int age)
    {
        if (age <= 0) return;
        config.Update(c =>
        {
            var o = c.Overrides.TryGetValue(id, out var existing) ? existing : new ItemOverride();
            if (age <= (o.StoreAgeFloor ?? 0)) return;
            o.StoreAgeFloor = age;
            c.Overrides[id] = o;
        });
    }

    private string BaseName(LibraryItem item) => _baseNames.TryGetValue(item.Id, out var n) ? n : item.Name;

    /// <summary>What the store recorded as last played, before our own launch times were put on top (per profile).</summary>
    private readonly Dictionary<string, long> _baseLastPlayed = new();

    /// <summary>
    /// Another profile is on the couch: favorites, launch times and play statistics are that person's now. Recomputed from what the
    /// stores gave (names and last played as scanned), so nothing of the previous profile stays on the entries; art stays as it is.
    /// </summary>
    public IReadOnlyList<LibraryItem> ReapplyUserState()
    {
        IReadOnlyList<LibraryItem> result;
        var ignored = IgnoredKeys();
        lock (_lock)
        {
            _items = _items.Select(i => ApplyUserState(i with
            {
                Name = BaseName(i),
                LastPlayed = _baseLastPlayed.TryGetValue(i.Id, out var played) ? played : 0
            }, ignored, _folderGuesses)).ToList();
            result = _items;
        }
        Changed?.Invoke(result);
        return result;
    }

    private readonly Dictionary<string, string> _baseNames = new();

    public void AddCustomEntries(IEnumerable<CustomEntry> entries)
    {
        // The id comes from the name: trimmed and cut now, like the load does, or the id changes at the next start and every lock and
        // favorite on it is lost. Trimmed first, then cut, then checked: 200 spaces and a word must not leave an empty name behind.
        var list = entries.ToList();
        foreach (var e in list)
        {
            e.Name = (e.Name ?? "").Trim();
            if (e.Name.Length > ConfigLimits.MaxEntryNameLength) e.Name = e.Name[..ConfigLimits.MaxEntryNameLength].Trim();
        }
        var valid = list.Where(e => !string.IsNullOrWhiteSpace(e.Name) && (!string.IsNullOrWhiteSpace(e.Path) || !string.IsNullOrWhiteSpace(e.Url))).ToList();
        if (valid.Count == 0) throw new ArgumentException(Strings.Get("library.nothingToAdd"));
        foreach (var e in valid) CheckEntryLengths(e);
        config.UpdateGrowing(c => c.Apps.AddRange(valid));
    }

    /// <summary>Program, folder, link and parameters of an own entry have a length limit, so entries cannot fill up the settings file.</summary>
    public static void CheckEntryLengths(CustomEntry e)
    {
        if ((e.Path?.Length ?? 0) > ConfigLimits.MaxEntryPathLength || (e.Cwd?.Length ?? 0) > ConfigLimits.MaxEntryPathLength
            || (e.Url?.Length ?? 0) > ConfigLimits.MaxEntryUrlLength || (e.Image?.Length ?? 0) > ConfigLimits.MaxEntryUrlLength || (e.Hero?.Length ?? 0) > ConfigLimits.MaxEntryUrlLength || e.Args.Sum(a => a.Length + 1) > ConfigLimits.MaxEntryArgsLength)
            throw new ArgumentException(Strings.Get("library.entryTooLong", e.Name));
    }

    /// <summary>
    /// Startup parameters and working folder of an entry the user added (an exe, not a link or a store game). The checks are
    /// <see cref="LaunchOptions"/>; a folder equal to the program's own is stored as "default".
    /// </summary>
    public LibraryItem EditLaunch(string id, string? arguments, string? workingDirectory)
    {
        var args = LaunchOptions.ParseArguments(arguments);
        LibraryItem? rebuilt = null;
        config.Update(c =>
        {
            var valid = c.Apps.Where(e => !string.IsNullOrWhiteSpace(e.Name) && (!string.IsNullOrEmpty(e.Url) || !string.IsNullOrEmpty(e.Path))).ToList();
            var index = CustomEntries.From(c.Apps).Select(i => i.Id).ToList().IndexOf(id);
            if (index < 0 || string.IsNullOrEmpty(valid[index].Path) || !string.IsNullOrEmpty(valid[index].Url)) throw new KeyNotFoundException(Strings.Get("library.notCustom", id));
            var entry = valid[index];
            if (LaunchOptions.IsScript(entry.Path)) LaunchOptions.CheckScriptArguments(args);
            var cwd = LaunchOptions.ValidateWorkingDirectory(workingDirectory, entry.Path!);
            if (args.Sum(a => a.Length + 1) > ConfigLimits.MaxEntryArgsLength || (cwd?.Length ?? 0) > ConfigLimits.MaxEntryPathLength) throw new ArgumentException(Strings.Get("library.entryTooLong", entry.Name));
            entry.Cwd = cwd;
            entry.Args = [.. args];
            rebuilt = CustomEntries.From([entry])[0];
        });
        return Replace(id, i => i with { Launch = rebuilt!.Launch, InstallDir = rebuilt.InstallDir });
    }

    /// <summary>What the edit menu shows about the start of an entry (see <see cref="LaunchOverrides.View"/>).</summary>
    public LaunchOptionsView GetLaunchOptions(string id)
    {
        var item = Find(id) ?? throw new KeyNotFoundException($"unknown item {id}");
        return LaunchOverrides.View(item, config.Current.Overrides.GetValueOrDefault(id));
    }

    /// <summary>
    /// Startup parameters, working folder or an own start target for any entry, stored as an override (the checks are
    /// <see cref="LaunchOverrides.Apply"/>). The library entry itself stays as scanned; the start applies the override.
    /// </summary>
    public LaunchOptionsView SetLaunchOverride(string id, LaunchOverrideChange change, LaunchFileSystem? fs = null)
    {
        var item = Find(id) ?? throw new KeyNotFoundException($"unknown item {id}");
        fs = WithLibraryFolders(fs);
        ItemOverride? after = null;
        config.Update(c =>
        {
            var o = c.Overrides.TryGetValue(id, out var existing) ? existing : new ItemOverride();
            LaunchOverrides.Apply(o, item, change, fs);
            if (o.IsEmpty) c.Overrides.Remove(id);
            else c.Overrides[id] = o;
            after = o;
        });
        return LaunchOverrides.View(item, after);
    }

    /// <summary>What a start of this entry runs: the scanned start with the user's override on top, checked again now.</summary>
    public LaunchTarget EffectiveLaunch(LibraryItem item, LaunchFileSystem? fs = null) =>
        LaunchOverrides.Effective(item, config.Current.Overrides.GetValueOrDefault(item.Id), WithLibraryFolders(fs));

    /// <summary>The disk checks plus the folders that are no game's own: the watched game folders and the other entries' install folders.</summary>
    private LaunchFileSystem WithLibraryFolders(LaunchFileSystem? fs)
    {
        var real = fs ?? LaunchFileSystem.Real;
        return real.LibraryFolders is not null ? real : real with
        {
            LibraryFolders = id => [.. config.Current.GameFolders, .. Items.Where(i => i.Id != id && i.InstallDir is not null).Select(i => i.InstallDir!)]
        };
    }

    /// <summary>A web entry opens in Edge (copy-protected streaming) or in the couch browser. Takes effect with the next scan.</summary>
    public void SetCustomEdge(string id, bool edge)
    {
        var found = false;
        config.Update(c =>
        {
            var ids = CustomEntries.From(c.Apps).Select(i => i.Id).ToList();
            var valid = c.Apps.Where(e => !string.IsNullOrWhiteSpace(e.Name) && (!string.IsNullOrEmpty(e.Url) || !string.IsNullOrEmpty(e.Path))).ToList();
            var index = ids.IndexOf(id);
            if (index < 0 || !WebLinks.IsWebUrl(valid[index].Url)) return;
            valid[index].OpenInEdge = edge;
            found = true;
        });
        if (!found) throw new KeyNotFoundException(Strings.Get("library.notCustom", id));
    }

    private void ForgetBase(string id)
    {
        _baseArt.Remove(id);
        _scannedArt.Remove(id);
        _baseLastPlayed.Remove(id);
        _baseNames.Remove(id);
    }

    private void MoveBase(string from, string to)
    {
        static void Move<T>(Dictionary<string, T> map, string from, string to)
        {
            if (map.Remove(from, out var value)) map[to] = value;
        }
        Move(_baseArt, from, to);
        Move(_scannedArt, from, to);
        Move(_baseLastPlayed, from, to);
        Move(_baseNames, from, to);
    }

    /// <summary>
    /// Removes an entry the user added; store games are hidden instead. <paramref name="authorize"/> runs inside the same config update, on the
    /// state the removal then changes (the entry as it is stored, and whether later entries of the same name change their id): it throws to refuse,
    /// and uses up a grant, so the check and the removal cannot drift apart. Returns whether later entries changed their id (the library is
    /// stale until the next scan).
    /// </summary>
    public bool RemoveCustomEntry(string id, Action<AppConfig, LibraryItem, bool>? authorize = null)
    {
        var removed = false;
        IReadOnlyList<(string From, string To)> moves = [];
        config.UpdateOrThrow(c =>
        {
            var before = CustomEntries.From(c.Apps);
            var ids = before.Select(i => i.Id).ToList();
            var valid = c.Apps.Where(e => !string.IsNullOrWhiteSpace(e.Name) && (!string.IsNullOrEmpty(e.Url) || !string.IsNullOrEmpty(e.Path))).ToList();
            var index = ids.IndexOf(id);
            if (index < 0) return;
            var after = CustomEntries.From(c.Apps.Where(e => !ReferenceEquals(e, valid[index])));
            moves = CustomEntryLocks.Shifts(ids, index, after.Select(i => i.Id).ToList());
            authorize?.Invoke(c, before[index], moves.Count > 0);
            c.Apps.Remove(valid[index]);
            c.Overrides.Remove(id);
            // Own entries are named after their name: a new entry with the same name must not come back hidden.
            LibraryIgnore.Show(c.Ignored ??= [], [LibraryIgnore.IdPrefix + id]);
            // The entry is gone for everybody: out of every profile's favorites and collections (also the ones the app ignores).
            CustomEntryLocks.Forget(c, id);
            // Later entries of the same name move up one id: locks, hidden mark, play time and the rest move with them (and none is lost).
            CustomEntryLocks.Rekey(c, before, index, after);
            removed = true;
        });
        if (!removed) throw new KeyNotFoundException(Strings.Get("library.notCustom", id));
        // The library follows at once (the ids the page sends next must be the current ones), not with the next scan.
        lock (_lock)
        {
            var map = moves.ToDictionary(m => m.From, m => m.To);
            _items = _items.Where(i => i.Id != id).Select(i => map.TryGetValue(i.Id, out var to) ? i with { Id = to } : i).ToList();
            ForgetBase(id);
            foreach (var (from, to) in moves) MoveBase(from, to);
        }
        if (moves.Count > 0) Changed?.Invoke(Items);
        return moves.Count > 0;
    }

    /// <summary>
    /// Adds a finished play session: the total grows, the session is remembered as the last one, and its end counts as "last played"
    /// (a game started outside CouchHUD has no launch time of ours).
    /// </summary>
    public LibraryItem AddPlaytime(string id, double minutes, DateTimeOffset? ended = null)
    {
        if (minutes <= 0) return Find(id) ?? throw new KeyNotFoundException($"unknown item {id}");
        var at = (ended ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
        double total = 0;
        long last = 0;
        config.Update(c =>
        {
            var profile = c.ActiveProfile;
            profile.PlaytimeMinutes.TryGetValue(id, out var before);
            total = before + minutes;
            profile.PlaytimeMinutes[id] = total;
            profile.LastSessionMinutes[id] = minutes;
            profile.LastLaunched.TryGetValue(id, out var launched);
            last = Math.Max(launched, at);
            profile.LastLaunched[id] = last;
        });
        return Replace(id, i => i with { PlaytimeMinutes = total, LastSessionMinutes = minutes, LastPlayed = Math.Max(i.LastPlayed, last) });
    }

    private Timer? _artworkTimer;

    /// <summary>Stops the periodic look for missing pictures.</summary>
    public void Dispose() => Interlocked.Exchange(ref _artworkTimer, null)?.Dispose();

    /// <summary>How often the library looks again for pictures that are missing (each entry has its own, daily, due time).</summary>
    private static readonly TimeSpan ArtworkTick = TimeSpan.FromHours(6);

    private async Task FetchMissingArtworkAsync()
    {
        // Once per process: a library that stays up for days still looks again for missing pictures.
        if (_artworkTimer is null)
        {
            var timer = new Timer(_ => _ = FetchMissingArtworkAsync(), null, ArtworkTick, ArtworkTick);
            if (Interlocked.CompareExchange(ref _artworkTimer, timer, null) is not null) timer.Dispose();
        }
        if (Interlocked.Exchange(ref _artworkRunning, 1) == 1) return;
        try
        {
            // Looked up on the automatic art, so a picked image never hides a slot from the lookup.
            IReadOnlyList<LibraryItem> baseItems;
            var fetchFor = FetchArtFor;
            bool partial;
            lock (_lock)
            {
                baseItems = _items.Where(i => fetchFor is null || fetchFor(i)).Select(i => i with { Art = BaseArt(i) }).ToList();
                partial = _partialScan;
            }
            var updated = await artwork.ResolveAsync(baseItems);
            if (updated is not null)
            {
                IReadOnlyList<LibraryItem> merged;
                lock (_lock)
                {
                    // Keep state that changed while we were waiting (favorites, launches).
                    var art = updated.ToDictionary(i => i.Id, i => i.Art);
                    // The user's picks are applied last, from the current config: they always win.
                    var overrides = config.Current.Overrides;
                    foreach (var (id, a) in art) _baseArt[id] = a;
                    merged = _items.Select(i => art.TryGetValue(i.Id, out var a)
                        ? i with { Art = ArtworkOverrides.Apply(a, overrides.GetValueOrDefault(i.Id)) }
                        : i).ToList();
                    _items = merged;
                }
                Changed?.Invoke(merged);
            }

            // The images themselves: once to disk, slowly, in the background. The UI shows each as it arrives.
            var slots = Items.Where(i => fetchFor is null || fetchFor(i))
                .SelectMany(i => new[] { i.Art.Cover, i.Art.Hero, i.Art.Logo, i.Art.Square, i.Art.Icon }).ToList();
            var arrived = await artwork.DownloadMissingAsync(slots, () => Changed?.Invoke(Items));
            // A scan without some stores does not know their entries: their pictures stay until a full scan.
            bool stillFull;
            lock (_lock) stillFull = !_partialScan;
            if (!partial && stillFull) artwork.PruneImages(Items);
            if (arrived > 0) Changed?.Invoke(Items);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[library] artwork lookup failed: {ex}");
        }
        finally
        {
            Interlocked.Exchange(ref _artworkRunning, 0);
        }
    }

    /// <summary>The provider's entries; null when it failed or did not answer in time (an empty list from a provider is a real answer).</summary>
    private async Task<IReadOnlyList<LibraryItem>?> SafeScan(ILibraryProvider provider, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = new CancellationTokenSource(timeout, Time);
        using var link = ct.Register(static s => ((CancellationTokenSource)s!).Cancel(), cts);
        try
        {
            // Task.Run: a provider that blocks before returning its task must not freeze the UI thread.
            return await Task.Run(() => provider.GetItemsAsync(cts.Token), cts.Token).WaitAsync(timeout, Time, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // One broken, slow or offline store or plugin must not take down the whole library.
            var reason = ex is TimeoutException or OperationCanceledException ? $"no answer within {timeout.TotalSeconds:0}s" : ex.ToString();
            Console.Error.WriteLine($"[library] {provider.Name} skipped: {reason}");
            return null;
        }
    }
}

/// <summary>A pick for the same slot that was chosen later has already been stored: this older one changed nothing (not an error to show).</summary>
public sealed class PickSupersededException : Exception;
