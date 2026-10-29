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
        var results = await Task.WhenAll(scanned.Select(p => SafeScan(p, ProviderTimeout, ct)));
        var cfg = config.Current;
        var custom = CustomEntries.From(cfg.Apps);

        var raw = WithoutFolderDuplicates(results.SelectMany(r => r).Concat(custom)
            .GroupBy(i => i.Id).Select(g => g.First()) // first provider wins on id clashes
            .ToList());
        lock (_lock)
        {
            // A newer scan started meanwhile: its result counts, this one changes nothing.
            if (number != Interlocked.Read(ref _scanNumber)) return _items;
            _baseNames.Clear();
            _baseLastPlayed.Clear();
            foreach (var item in raw)
            {
                _baseNames[item.Id] = item.Name;
                _baseLastPlayed[item.Id] = item.LastPlayed;
            }
        }
        var merged = raw.Select(ApplyUserState).ToList();

        // The art without the user's picks: "automatic" is always this, whatever was picked before.
        var cached = artwork.ApplyCached(merged);
        var overrides = config.Current.Overrides;
        var withArt = cached.Select(i => i with { Art = ArtworkOverrides.Apply(i.Art, overrides.GetValueOrDefault(i.Id)) }).ToList();
        lock (_lock)
        {
            if (number != Interlocked.Read(ref _scanNumber)) return _items;
            _partialScan = partial;
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

    private LibraryItem ApplyUserState(LibraryItem item)
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
            Hidden = custom?.Hidden ?? false
        };
    }

    /// <summary>Rename, re-categorize or hide any entry; stored as an override so rescans keep it.</summary>
    public LibraryItem EditItem(string id, string? name, string? type, bool? hidden)
    {
        if (Find(id) is null) throw new KeyNotFoundException($"unknown item {id}");
        if (type is not null && type is not ("game" or "app")) throw new ArgumentException("type must be game or app", nameof(type));
        config.Update(c =>
        {
            var o = c.Overrides.TryGetValue(id, out var existing) ? existing : new ItemOverride();
            if (name is not null) o.Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            if (type is not null) o.Type = type;
            if (hidden is not null) o.Hidden = hidden.Value;
            if (o.IsEmpty) c.Overrides.Remove(id);
            else c.Overrides[id] = o;
        });
        return Replace(id, i => ApplyUserState(i with { Name = BaseName(i) }));
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
        config.Update(c =>
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
        config.Update(c => change(c.ActiveProfile.Collections));
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
        config.Update(c =>
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
        config.Update(c =>
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
        lock (_lock)
        {
            _items = _items.Select(i => ApplyUserState(i with
            {
                Name = BaseName(i),
                LastPlayed = _baseLastPlayed.TryGetValue(i.Id, out var played) ? played : 0
            })).ToList();
            result = _items;
        }
        Changed?.Invoke(result);
        return result;
    }

    private readonly Dictionary<string, string> _baseNames = new();

    public void AddCustomEntries(IEnumerable<CustomEntry> entries)
    {
        var valid = entries.Where(e => !string.IsNullOrWhiteSpace(e.Name) && (!string.IsNullOrWhiteSpace(e.Path) || !string.IsNullOrWhiteSpace(e.Url))).ToList();
        if (valid.Count == 0) throw new ArgumentException(Strings.Get("library.nothingToAdd"));
        config.Update(c => c.Apps.AddRange(valid));
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
            entry.Cwd =LaunchOptions.ValidateWorkingDirectory(workingDirectory, entry.Path!);
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
        fs ??= LaunchFileSystem.Real;
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
        LaunchOverrides.Effective(item, config.Current.Overrides.GetValueOrDefault(item.Id), fs ?? LaunchFileSystem.Real);

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

    /// <summary>Removes an entry the user added; store games are hidden instead.</summary>
    public void RemoveCustomEntry(string id)
    {
        var removed = false;
        config.Update(c =>
        {
            var ids = CustomEntries.From(c.Apps).Select(i => i.Id).ToList();
            var valid = c.Apps.Where(e => !string.IsNullOrWhiteSpace(e.Name) && (!string.IsNullOrEmpty(e.Url) || !string.IsNullOrEmpty(e.Path))).ToList();
            var index = ids.IndexOf(id);
            if (index < 0) return;
            c.Apps.Remove(valid[index]);
            c.Overrides.Remove(id);
            // The entry is gone for everybody: out of every profile's favorites and collections.
            foreach (var profile in c.AllProfiles)
            {
                profile.Favorites.Remove(id);
                Collections.Forget(profile.Collections, id);
            }
            removed = true;
        });
        if (!removed) throw new KeyNotFoundException(Strings.Get("library.notCustom", id));
        lock (_lock) _items = _items.Where(i => i.Id != id).ToList();
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

    private static async Task<IReadOnlyList<LibraryItem>> SafeScan(ILibraryProvider provider, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            // Task.Run: a provider that blocks before returning its task must not freeze the UI thread.
            return await Task.Run(() => provider.GetItemsAsync(cts.Token), cts.Token).WaitAsync(timeout, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // One broken, slow or offline store or plugin must not take down the whole library.
            var reason = ex is TimeoutException or OperationCanceledException ? $"no answer within {timeout.TotalSeconds:0}s" : ex.ToString();
            Console.Error.WriteLine($"[library] {provider.Name} skipped: {reason}");
            return [];
        }
    }
}

/// <summary>A pick for the same slot that was chosen later has already been stored: this older one changed nothing (not an error to show).</summary>
public sealed class PickSupersededException : Exception;
