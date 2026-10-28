using System.Net;
using System.Text;
using CouchHud.Core.Config;
using CouchHud.Core.Library;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>Choosing the store entry of an entry, own texts, and the safer automatic match.</summary>
public sealed class StoreMatchTests
{
    private static LibraryItem Game(string id, string name) => new()
    {
        Id = id,
        Source = id.Split(':')[0],
        Name = name,
        Launch = new LaunchTarget(LaunchKind.Url, "x://")
    };

    private static string Temp() => Directory.CreateTempSubdirectory("couchhud-storematch-").FullName;

    // ----- match values -----

    [Theory]
    [InlineData("1145360", true)]
    [InlineData("0", false)]
    [InlineData("0123", false)]
    [InlineData("12a", false)]
    [InlineData("", false)]
    [InlineData("99999999999", false)]
    [InlineData("4294967296", false)]
    [InlineData("１２３", false)]
    public void Only_plain_app_ids_count(string text, bool ok) => Assert.Equal(ok, StoreMatches.IsAppId(text));

    [Fact]
    public void A_match_is_an_app_id_none_or_automatic()
    {
        Assert.Null(StoreMatches.Validate(null));
        Assert.Equal("none", StoreMatches.Validate("none"));
        Assert.Equal("42", StoreMatches.Validate(" 42 "));
        Assert.Throws<ArgumentException>(() => StoreMatches.Validate("steam:42"));
        Assert.Throws<ArgumentException>(() => StoreMatches.Validate("../x"));
    }

    [Fact]
    public void The_lookup_goes_by_steams_id_the_pick_or_the_scanned_name()
    {
        var steam = Game("steam:10", "Counter-Strike");
        Assert.Equal(new StoreLookup("10", null), StoreMatches.Of(steam, new ItemOverride { StoreMatch = "99" }, "Counter-Strike"));
        Assert.False(StoreMatches.CanChoose(steam));

        var epic = Game("epic:hades", "My Hades");
        Assert.Equal(new StoreLookup(null, "Hades"), StoreMatches.Of(epic, new ItemOverride { Name = "My Hades" }, "Hades"));
        Assert.Equal(new StoreLookup("1145360", null), StoreMatches.Of(epic, new ItemOverride { StoreMatch = "1145360" }, "Hades"));
        Assert.Equal(new StoreLookup(null, null), StoreMatches.Of(epic, new ItemOverride { StoreMatch = "none" }, "Hades"));
        // A hand-edited config with garbage counts as automatic.
        Assert.Equal(new StoreLookup(null, "Hades"), StoreMatches.Of(epic, new ItemOverride { StoreMatch = "evil" }, "Hades"));
        Assert.NotEqual(new StoreLookup("1", null).Key, new StoreLookup(null, "1").Key);
    }

    [Fact]
    public void Without_the_parents_the_stricter_rating_stays_until_they_confirm()
    {
        var o = new ItemOverride();
        StoreMatches.Apply(o, "none", null, previousAge: 18, confirmed: false);
        Assert.Equal(18, o.StoreAgeFloor);
        Assert.Null(o.StoreMatchName);
        // A second change cannot wash the floor out with a lower rating.
        StoreMatches.Apply(o, "42", "Some Game", previousAge: 6, confirmed: false);
        Assert.Equal(18, o.StoreAgeFloor);
        Assert.Equal("Some Game", o.StoreMatchName);
        // Back to automatic still keeps it; the override is not empty.
        StoreMatches.Apply(o, null, null, previousAge: null, confirmed: false);
        Assert.Equal(18, o.StoreAgeFloor);
        Assert.False(o.IsEmpty);
        // The parents' code confirms: the new match's rating counts.
        StoreMatches.Apply(o, null, null, previousAge: 18, confirmed: true);
        Assert.Null(o.StoreAgeFloor);
        Assert.True(o.IsEmpty);
    }

    // ----- texts -----

    [Fact]
    public void Own_texts_are_bounded_plain_text_and_reset_per_field()
    {
        var o = new ItemOverride();
        InfoOverrides.Apply(o, new InfoChange { SetDescription = true, Description = " <b>Mine</b> ", SetDevelopers = true, Developers = "Studio A, Studio B;" });
        Assert.Equal("<b>Mine</b>", o.Description);
        Assert.Equal(["Studio A", "Studio B"], o.Developers!);
        Assert.Equal(["description", "developers"], InfoOverrides.Custom(o));

        foreach (var bad in new[] { "line\nbreak", "bell\u0007", "rtl\u202Eevil", "zero\u200Bwidth", new string('x', InfoOverrides.MaxDescription + 1) })
            Assert.Throws<ArgumentException>(() => InfoOverrides.Apply(o, new InfoChange { SetDescription = true, Description = bad, SetGenres = true, Genres = "Action" }));
        // A refused field changes nothing, not even the others.
        Assert.Null(o.Genres);
        Assert.Throws<ArgumentException>(() => InfoOverrides.Apply(o, new InfoChange { SetGenres = true, Genres = string.Join(',', Enumerable.Repeat("g", 9)) }));
        Assert.Throws<ArgumentException>(() => InfoOverrides.Apply(o, new InfoChange { SetPublishers = true, Publishers = new string('p', 81) }));
        Assert.Throws<ArgumentException>(() => InfoOverrides.Apply(o, new InfoChange { SetReleaseDate = true, ReleaseDate = new string('1', 41) }));

        InfoOverrides.Apply(o, new InfoChange { SetDescription = true, Description = null, SetDevelopers = true, Developers = " " });
        Assert.Null(o.Description);
        Assert.Null(o.Developers);
        Assert.True(o.IsEmpty);
    }

    [Fact]
    public void Own_texts_go_on_top_of_the_store_and_never_touch_the_rating()
    {
        var store = new StoreInfo("Store text", ["Dev"], ["Pub"], "1 Jan 2020", ["Action"]) { RequiredAge = 18, RatingKnown = true, Trailers = ["https://t/1.mp4"] };
        var o = new ItemOverride { Description = "Mine", Genres = ["Puzzle"] };
        var merged = InfoOverrides.Merge(store, o)!;
        Assert.Equal("Mine", merged.Description);
        Assert.Equal(["Dev"], merged.Developers);
        Assert.Equal(["Puzzle"], merged.Genres);
        Assert.Equal(18, merged.RequiredAge);
        Assert.Single(merged.Trailers);
        Assert.Same(store, InfoOverrides.Merge(store, new ItemOverride { Name = "x" }));
        // Without a store answer the own texts still show; values from a hand-edited file are bounded again.
        var alone = InfoOverrides.Merge(null, new ItemOverride { Description = new string('d', 5000) + "\u0007", Developers = ["a\u202E", "b"] })!;
        Assert.Equal(InfoOverrides.MaxDescription, alone.Description!.Length);
        Assert.Equal(["a", "b"], alone.Developers);
        Assert.False(alone.RatingKnown);
    }

    // ----- the store -----

    [Fact]
    public async Task The_search_lists_games_only_bounded_and_clean()
    {
        var items = string.Join(',', Enumerable.Range(1, 15).Select(i => $$"""{ "id": {{i}}, "name": "Game {{i}}\u0007", "type": "app" }"""));
        var http = new HttpClient(new Routes(("storesearch", $$"""{ "items": [ { "id": 99, "name": "Soundtrack", "type": "dlc" }, { "id": "7", "name": "string id" }, {{items}} ] }""")));
        var hits = await SteamStore.SearchAsync(http, "game", default);
        Assert.Equal(StoreMatches.MaxResults, hits.Count);
        Assert.Equal(new StoreCandidate("1", "Game 1"), hits[0]);

        var huge = new HttpClient(new Routes(("storesearch", "{\"items\":[],\"pad\":\"" + new string('x', SteamStore.MaxSearchBytes) + "\"}")));
        await Assert.ThrowsAsync<HttpRequestException>(() => SteamStore.SearchAsync(huge, "game", default));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a\nb")]
    public async Task A_bad_query_never_reaches_the_store(string query)
    {
        var routes = new Routes(("storesearch", """{ "items": [] }"""));
        var service = new StoreInfoService(new HttpClient(routes), Path.Combine(Temp(), "s.json"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(query));
        await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(new string('q', StoreMatches.MaxQueryLength + 1)));
        Assert.Empty(routes.Requests);
    }

    [Fact]
    public async Task Two_entries_of_the_same_name_are_no_answer_unless_one_is_exact()
    {
        var ambiguous = new HttpClient(new Routes(("storesearch", """{ "items": [ { "id": 1, "name": "DOOM (1993)", "type": "app" }, { "id": 2, "name": "DOOM: Deluxe Edition", "type": "app" } ] }""")));
        Assert.Null(await SteamStore.FindAppIdAsync(ambiguous, "Doom", default));
        var exact = new HttpClient(new Routes(("storesearch", """{ "items": [ { "id": 1, "name": "DOOM (1993)", "type": "app" }, { "id": 2, "name": "DOOM", "type": "app" } ] }""")));
        Assert.Equal("2", await SteamStore.FindAppIdAsync(exact, "Doom™", default));
        // Different years in brackets are different games.
        var years = new HttpClient(new Routes(("storesearch", """{ "items": [ { "id": 1, "name": "DOOM (1993)", "type": "app" } ] }""")));
        Assert.Null(await SteamStore.FindAppIdAsync(years, "Doom (2016)", default));
    }

    [Fact]
    public async Task The_store_info_follows_the_pick_and_never_serves_the_old_match()
    {
        var routes = new Routes(
            ("storesearch", """{ "items": [ { "id": 10, "name": "Hades", "type": "app" } ] }"""),
            ("appids=10&", """{ "10": { "success": true, "data": { "short_description": "Auto", "required_age": 16 } } }"""),
            ("appids=20&", """{ "20": { "success": true, "data": { "short_description": "Picked", "required_age": 6 } } }"""));
        var item = Game("epic:hades", "Hades");
        var o = new ItemOverride();
        var service = new StoreInfoService(new HttpClient(routes), Path.Combine(Temp(), "s.json"))
        {
            Lookup = i => StoreMatches.Of(i, o, "Hades")
        };

        Assert.Equal("Auto", (await service.GetAsync(item))!.Description);
        Assert.Equal(16, service.Cached(item)!.RequiredAge);

        o.StoreMatch = "20";
        // The cached answer belongs to the old match: not used for the rating either.
        Assert.Null(service.Cached(item));
        Assert.Equal("Picked", (await service.GetAsync(item))!.Description);
        Assert.Equal(6, service.Cached(item)!.RequiredAge);

        o.StoreMatch = "none";
        Assert.Null(service.Cached(item));
        Assert.Null(await service.GetAsync(item));
        Assert.Equal(1, routes.Requests.Count(r => r.Contains("storesearch")));
    }

    [Fact]
    public async Task An_answer_from_an_older_cache_never_stands_for_a_picked_entry()
    {
        var file = Path.Combine(Temp(), "s.json");
        var routes = new Routes(
            ("storesearch", """{ "items": [ { "id": 10, "name": "Hades", "type": "app" } ] }"""),
            ("appids=10&", """{ "10": { "success": true, "data": { "short_description": "Auto", "required_age": 16 } } }"""),
            ("appids=20&", """{ "20": { "success": true, "data": { "short_description": "Picked", "required_age": 6 } } }"""));
        var item = Game("epic:hades", "Hades");
        var o = new ItemOverride();
        StoreLookup Lookup(LibraryItem i) => StoreMatches.Of(i, o, "Hades");
        Assert.Equal("Auto", (await new StoreInfoService(new HttpClient(routes), file) { Lookup = Lookup }.GetAsync(item))!.Description);
        // As a version before the store match wrote it: no source.
        File.WriteAllText(file, System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(file), ",\"Source\":\"[^\"]*\"", ""));
        Assert.DoesNotContain("Source\":\"", File.ReadAllText(file));

        // For the automatic match it still counts (no new request) ...
        var later = new StoreInfoService(new HttpClient(routes), file) { Lookup = Lookup };
        var before = routes.Requests.Count;
        Assert.Equal("Auto", (await later.GetAsync(item))!.Description);
        Assert.Equal(before, routes.Requests.Count);

        // ... for a picked entry or "none" never: neither its texts nor its rating.
        o.StoreMatch = "20";
        Assert.Null(later.Cached(item));
        Assert.False(later.HasAnswer(item));
        Assert.Equal("Picked", (await later.GetAsync(item))!.Description);
        Assert.Equal(6, later.Cached(item)!.RequiredAge);
    }

    [Fact]
    public async Task A_look_up_waiting_while_the_match_changes_asks_for_the_new_entry()
    {
        var release = new TaskCompletionSource();
        var routes = new Routes(
            ("storesearch", """{ "items": [ { "id": 10, "name": "Hades", "type": "app" } ] }"""),
            ("appids=10&", """{ "10": { "success": true, "data": { "short_description": "Auto", "required_age": 16 } } }"""),
            ("appids=20&", """{ "20": { "success": true, "data": { "short_description": "Picked", "required_age": 6 } } }"""))
        {
            Hold = ("appids=10&", release.Task)
        };
        var item = Game("epic:hades", "Hades");
        var o = new ItemOverride();
        var service = new StoreInfoService(new HttpClient(routes), Path.Combine(Temp(), "s.json")) { Lookup = i => StoreMatches.Of(i, o, "Hades") };

        // The detail page asks for the old match (its answer is slow), a second read waits behind it, then the user picks entry 20.
        var first = service.GetAsync(item);
        bool Asked() { lock (routes.Requests) return routes.Requests.Any(r => r.Contains("appids=10&")); }
        while (!Asked()) await Task.Delay(5);
        var waiting = service.GetAsync(item);
        o.StoreMatch = "20";
        release.SetResult();

        Assert.Equal("Auto", (await first)!.Description);
        // The waiting read asks for the entry as it is now, and the cache keeps the new match's answer.
        Assert.Equal("Picked", (await waiting)!.Description);
        Assert.Equal("Picked", service.Cached(item)!.Description);
        Assert.True(service.HasAnswer(item));
    }

    [Fact]
    public async Task A_store_that_did_not_answer_is_no_answer_yet()
    {
        var item = Game("epic:hades", "Hades");
        var o = new ItemOverride { StoreMatch = "20" };
        var file = Path.Combine(Temp(), "s.json");
        // Rate limit or offline: no description, but not the store's answer either (the detail page asks again).
        var failing = new StoreInfoService(new HttpClient(new Routes(("appids=20&", "<html>busy</html>")) { Status = HttpStatusCode.TooManyRequests }), file)
        {
            Lookup = i => StoreMatches.Of(i, o, "Hades")
        };
        Assert.Null(await failing.GetAsync(item));
        Assert.False(failing.HasAnswer(item));

        var answering = new StoreInfoService(new HttpClient(new Routes(("appids=20&", """{ "20": { "success": true, "data": { "short_description": "Picked" } } }"""))), file)
        {
            Lookup = i => StoreMatches.Of(i, o, "Hades")
        };
        Assert.Equal("Picked", (await answering.GetAsync(item))!.Description);
        Assert.True(answering.HasAnswer(item));

        // "None" is an answer of its own: nothing to ask for.
        o.StoreMatch = "none";
        Assert.Null(await answering.GetAsync(item));
        Assert.True(answering.HasAnswer(item));
    }

    /// <summary>
    /// A store service on <paramref name="cacheFile"/> as a later start of CouchHUD sees it: every cached answer older than its time to live (so
    /// it asks again), the id cache gone, the store answering with <paramref name="search"/> and app 10 rated <paramref name="age"/>.
    /// </summary>
    private static StoreInfoService Later(string cacheFile, string search, int age, Func<LibraryItem, StoreLookup> lookup, List<(string, int)> dropped)
    {
        if (File.Exists(cacheFile))
            File.WriteAllText(cacheFile, System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(cacheFile),
                "\"Checked\":\"[^\"]+\"", "\"Checked\":\"2000-01-01T00:00:00+00:00\""));
        File.Delete(Path.ChangeExtension(cacheFile, ".steamids.json"));
        return new StoreInfoService(new HttpClient(new Routes(
            ("storesearch", search),
            ("appids=10&", $$"""{ "10": { "success": true, "data": { "short_description": "Auto", "required_age": {{age}} } } }"""))), cacheFile)
        {
            Lookup = lookup,
            RatingDropped = (i, a) => dropped.Add((i.Id, a))
        };
    }

    private const string HadesHit = """{ "items": [ { "id": 10, "name": "Hades", "type": "app" } ] }""";

    [Theory]
    [InlineData("""{ "items": [ { "id": 1, "name": "HADES (2018)", "type": "app" }, { "id": 2, "name": "Hades: Deluxe Edition", "type": "app" } ] }""", 0)]
    [InlineData("""{ "items": [] }""", 0)]
    [InlineData(HadesHit, 6)]
    public async Task An_automatic_match_that_loses_its_rating_keeps_the_old_one_as_the_floor(string laterSearch, int laterAge)
    {
        var dir = Temp();
        var cacheFile = Path.Combine(dir, "s.json");
        var item = Game("epic:hades", "Hades");
        StoreLookup Lookup(LibraryItem i) => StoreMatches.Of(i, null, "Hades");
        var dropped = new List<(string, int)>();
        Assert.Equal(16, (await Later(cacheFile, HadesHit, 16, Lookup, dropped).GetAsync(item))!.RequiredAge);
        Assert.Empty(dropped);

        // Later the automatic match finds nothing (ambiguous, a stricter rule, a name it cannot compare) or a milder rating: the old one is reported.
        await Later(cacheFile, laterSearch, laterAge, Lookup, dropped).GetAsync(item);
        Assert.Equal([("epic:hades", 16)], dropped);

        // The library keeps it as the floor (the stricter of pending ones) until the parents confirm the match.
        var config = new ConfigStore(dir);
        var library = new LibraryService(config, new ArtworkResolver(new HttpClient(new Routes()), Path.Combine(dir, "art.json"), null) { Online = false }, [new Fixed(item)]);
        await library.ScanAsync();
        library.KeepAgeFloor(item.Id, 16);
        library.KeepAgeFloor(item.Id, 12);
        Assert.Equal(16, config.Current.Overrides[item.Id].StoreAgeFloor);
        Assert.Equal(16, library.GetItemInfo(item.Id, null).Store.AgeFloor);
        library.SetStoreMatch(item.Id, null, null, previousAge: null, confirmed: true);
        Assert.False(config.Current.Overrides.ContainsKey(item.Id));

        // The drop was recorded with the answer: the next look-up has nothing known to lose, so no new floor after the parents confirmed.
        await Later(cacheFile, laterSearch, laterAge, Lookup, dropped).GetAsync(item);
        Assert.Single(dropped);
    }

    [Fact]
    public async Task The_floor_is_kept_before_the_cache_forgets_the_rating()
    {
        var cacheFile = Path.Combine(Temp(), "s.json");
        var item = Game("epic:hades", "Hades");
        StoreLookup Lookup(LibraryItem i) => StoreMatches.Of(i, null, "Hades");
        var dropped = new List<(string, int)>();
        await Later(cacheFile, HadesHit, 16, Lookup, dropped).GetAsync(item);
        // The floor cannot be stored (a write fails): the new answer is not taken, the known rating stays cached and counts.
        var failing = Later(cacheFile, """{ "items": [] }""", 0, Lookup, dropped);
        failing.RatingDropped = (_, _) => throw new IOException("disk full");
        Assert.Equal(16, (await failing.GetAsync(item))!.RequiredAge);
        Assert.Equal(16, failing.Cached(item)!.RequiredAge);
    }

    [Fact]
    public async Task A_stricter_rating_or_steams_own_entry_sets_no_floor()
    {
        var cacheFile = Path.Combine(Temp(), "s.json");
        var dropped = new List<(string, int)>();
        StoreLookup Lookup(LibraryItem i) => StoreMatches.Of(i, null, i.Name);
        var name = Game("epic:hades", "Hades");
        var steam = Game("steam:10", "Hades");
        await Later(cacheFile, HadesHit, 12, Lookup, dropped).GetAsync(name);
        await Later(cacheFile, HadesHit, 12, Lookup, dropped).GetAsync(steam);
        // Stricter than before: nothing pending.
        await Later(cacheFile, HadesHit, 18, Lookup, dropped).GetAsync(name);
        // Steam's own entries go by their app id, which the stricter name match does not touch (and a store change there has no pick to confirm).
        await Later(cacheFile, HadesHit, 0, Lookup, dropped).GetAsync(steam);
        Assert.Empty(dropped);
    }

    [Fact]
    public async Task The_library_matches_by_the_scanned_name_and_keeps_the_pick()
    {
        var dir = Temp();
        var config = new ConfigStore(dir);
        var item = Game("epic:hades", "Hades");
        var library = new LibraryService(config, new ArtworkResolver(new HttpClient(new Routes()), Path.Combine(dir, "art.json"), null) { Online = false }, [new Fixed(item)]);
        await library.ScanAsync();

        library.EditItem(item.Id, "Totally Different", null, null);
        Assert.Equal(new StoreLookup(null, "Hades"), library.StoreLookupOf(library.Find(item.Id)!));

        library.SetStoreMatch(item.Id, "1145360", "Hades", previousAge: 12, confirmed: false);
        var o = config.Current.Overrides[item.Id];
        Assert.Equal("1145360", o.StoreMatch);
        Assert.Equal(12, o.StoreAgeFloor);
        Assert.Equal(new StoreLookup("1145360", null), library.StoreLookupOf(library.Find(item.Id)!));
        Assert.Equal("manual", library.GetItemInfo(item.Id, null).Store.Mode);

        Assert.Throws<ArgumentException>(() => library.SetStoreMatch(item.Id, "javascript:1", null, null, true));
        Assert.Equal("1145360", config.Current.Overrides[item.Id].StoreMatch);

        library.SetItemInfo(item.Id, new InfoChange { SetDescription = true, Description = "Mine" });
        var view = library.GetItemInfo(item.Id, new StoreInfo("Store", [], [], null, []));
        Assert.Equal("Mine", view.Description);
        Assert.Equal(["description"], view.Custom);
    }

    [Fact]
    public async Task Steams_own_entries_keep_their_match()
    {
        var dir = Temp();
        var item = Game("steam:10", "Counter-Strike");
        var library = new LibraryService(new ConfigStore(dir), new ArtworkResolver(new HttpClient(new Routes()), Path.Combine(dir, "art.json"), null) { Online = false }, [new Fixed(item)]);
        await library.ScanAsync();
        Assert.Throws<ArgumentException>(() => library.SetStoreMatch(item.Id, "20", null, null, true));
        Assert.Equal("steam", library.GetItemInfo(item.Id, null).Store.Mode);
    }

    // ----- names -----

    [Theory]
    [InlineData("Final Fantasy Ⅶ", "Final Fantasy VII")]
    [InlineData("Ｈａｄｅｓ", "Hades")]
    [InlineData("Die Straße", "Die Strasse")]
    [InlineData("Doom (2016)", "DOOM")]
    public void More_spellings_of_the_same_name_match(string a, string b) =>
        Assert.True(NameMatch.Same(a, b), $"{NameMatch.Canonical(a)} / {NameMatch.Canonical(b)}");

    [Theory]
    [InlineData("Doom (1993)", "Doom (2016)")]
    [InlineData("モンスターハンター 2", "2")]
    [InlineData("Ведьмак 3", "3")]
    [InlineData("2", "2")]
    public void Names_that_cannot_be_compared_safely_never_match(string a, string b) => Assert.False(NameMatch.Same(a, b));

    private sealed class Fixed(params LibraryItem[] items) : ILibraryProvider
    {
        public string Name => "fixed";
        public Task<IReadOnlyList<LibraryItem>> GetItemsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<LibraryItem>>(items);
    }

    private sealed class Routes(params (string Part, string Body)[] routes) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        /// <summary>The status of every matched answer (a rate limit, say).</summary>
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

        /// <summary>Requests containing this part wait for the task before they are answered (a slow store).</summary>
        public (string Part, Task Until)? Hold { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            lock (Requests) Requests.Add(url);
            if (Hold is { } hold && url.Contains(hold.Part, StringComparison.Ordinal)) await hold.Until;
            foreach (var (part, body) in routes)
                if (url.Contains(part, StringComparison.Ordinal))
                    return new HttpResponseMessage(Status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
