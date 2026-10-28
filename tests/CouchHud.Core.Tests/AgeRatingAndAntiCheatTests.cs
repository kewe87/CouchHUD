using System.Text.Json;
using CouchHud.Core.Config;
using CouchHud.Core.Library;
using CouchHud.Core.Parental;
using CouchHud.Sdk;
using Xunit;

namespace CouchHud.Core.Tests;

public class AgeRatingTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static Dictionary<string, int> Ratings(string ratings) => AgeRatings.Parse(Json($$"""{"ratings":{{ratings}}}"""));

    [Theory]
    [InlineData("usk", "\"0\"", 0)]
    [InlineData("usk", "\"6\"", 6)]
    [InlineData("usk", "\"16\"", 16)]
    [InlineData("usk", "18", 18)]
    [InlineData("pegi", "\"3\"", 3)]
    [InlineData("pegi", "\"7\"", 7)]
    [InlineData("pegi", "\"12\"", 12)]
    [InlineData("pegi", "\"18+\"", 18)]
    [InlineData("esrb", "\"ec\"", 3)]
    [InlineData("esrb", "\"e\"", 6)]
    [InlineData("esrb", "\"E\"", 6)]
    [InlineData("esrb", "\"e10\"", 10)]
    [InlineData("esrb", "\"E10+\"", 10)]
    [InlineData("esrb", "\"t\"", 13)]
    [InlineData("esrb", "\"m\"", 17)]
    [InlineData("esrb", "\"ao\"", 18)]
    [InlineData("esrb", "\"A-O\"", 18)]
    [InlineData("dejus", "\"l\"", 0)]
    [InlineData("dejus", "\"14\"", 14)]
    [InlineData("cero", "\"a\"", 0)]
    [InlineData("cero", "\"d\"", 17)]
    [InlineData("cero", "\"z\"", 18)]
    [InlineData("usk", "\"99\"", 21)]
    public void Every_system_is_read(string system, string rating, int age) =>
        Assert.Equal(age, AgeRatings.ParseCode(system, Json(rating)));

    [Theory]
    [InlineData("usk", "\"ab 12\"")]
    [InlineData("usk", "\"-6\"")]
    [InlineData("usk", "\"1e3\"")]
    [InlineData("usk", "\"\"")]
    [InlineData("usk", "12.5")]
    [InlineData("usk", "true")]
    [InlineData("usk", "[16]")]
    [InlineData("usk", "\"0x10\"")]
    [InlineData("usk", "\"9999\"")]
    [InlineData("pegi", "\"mature\"")]
    [InlineData("pegi", "\"1-2\"")]
    [InlineData("esrb", "\"x\"")]
    [InlineData("esrb", "\"17\"")]
    [InlineData("esrb", "{}")]
    [InlineData("cero", "\"q\"")]
    [InlineData("dejus", "\"livre\"")]
    public void A_rating_that_cannot_be_read_is_malformed_never_mild(string system, string rating) =>
        Assert.Equal(AgeRatings.Malformed, AgeRatings.ParseCode(system, Json(rating)));

    [Fact]
    public void Rating_pending_and_null_are_no_rating()
    {
        Assert.Null(AgeRatings.ParseCode("esrb", Json("\"rp\"")));
        Assert.Null(AgeRatings.ParseCode("esrb", Json("\"RP\"")));
        Assert.Null(AgeRatings.ParseCode("usk", Json("null")));
    }

    [Fact]
    public void The_store_block_is_read_with_broken_entries_kept_as_malformed()
    {
        var r = Ratings("""{"usk":{"rating":"12"},"pegi":"16","esrb":{"descriptors":"x"},"cero":null,"dejus":{"rating":"18"},"oflc":{"rating":"m"}}""");
        Assert.Equal(12, r["usk"]);
        Assert.Equal(AgeRatings.Malformed, r["pegi"]);
        Assert.Equal(AgeRatings.Malformed, r["esrb"]);
        Assert.False(r.ContainsKey("cero"));
        Assert.Equal(18, r["dejus"]);
        Assert.False(r.ContainsKey("oflc"));
        Assert.Empty(AgeRatings.Parse(Json("""{"ratings":"usk 0"}""")));
        Assert.Empty(AgeRatings.Parse(Json("""{"ratings":null}""")));
        Assert.Empty(AgeRatings.Parse(Json("[]")));
    }

    [Fact]
    public void Steams_required_age()
    {
        Assert.Equal(18, AgeRatings.ParseRequiredAge(Json("""{"required_age":"18"}""")));
        Assert.Equal(12, AgeRatings.ParseRequiredAge(Json("""{"required_age":12}""")));
        Assert.Equal(21, AgeRatings.ParseRequiredAge(Json("""{"required_age":400}""")));
        Assert.Null(AgeRatings.ParseRequiredAge(Json("""{"required_age":0}""")));
        Assert.Null(AgeRatings.ParseRequiredAge(Json("""{"required_age":"-5"}""")));
        Assert.Null(AgeRatings.ParseRequiredAge(Json("""{"name":"x"}""")));
    }

    [Fact]
    public void The_active_system_wins_then_the_strictest_other_then_steam_then_unknown()
    {
        var all = Ratings("""{"usk":{"rating":"12"},"pegi":{"rating":"16"},"esrb":{"rating":"t"}}""");
        Assert.Equal(new AgeRatings.Rating(12, "usk"), AgeRatings.Effective(all, 18, "usk"));
        Assert.Equal(new AgeRatings.Rating(16, "pegi"), AgeRatings.Effective(all, 18, "pegi"));
        Assert.Equal(new AgeRatings.Rating(13, "esrb"), AgeRatings.Effective(all, 18, "esrb"));

        // No USK rating: the strictest of the others.
        var noUsk = Ratings("""{"pegi":{"rating":"12"},"esrb":{"rating":"m"},"cero":{"rating":"b"}}""");
        Assert.Equal(new AgeRatings.Rating(17, "esrb"), AgeRatings.Effective(noUsk, 6, "usk"));
        // ClassInd and CERO count as a fallback only.
        Assert.Equal(new AgeRatings.Rating(14, "dejus"), AgeRatings.Effective(Ratings("""{"dejus":{"rating":"14"}}"""), null, "pegi"));
        // Among equal ages PEGI is named before the foreign systems (the badge then reads "PEGI 14"); a stricter foreign rating still
        // counts and is named (never the milder PEGI for the check: VM test 0.5, display only).
        Assert.Equal(new AgeRatings.Rating(14, "pegi"), AgeRatings.Effective(Ratings("""{"dejus":{"rating":"14"},"pegi":{"rating":"14"}}"""), null, "usk"));
        Assert.Equal(new AgeRatings.Rating(16, "dejus"), AgeRatings.Effective(Ratings("""{"dejus":{"rating":"16"},"pegi":{"rating":"12"}}"""), null, "usk"));
        // Strictest: every system, the highest.
        Assert.Equal(new AgeRatings.Rating(17, "esrb"), AgeRatings.Effective(noUsk, null, "strictest"));

        // Steam's own required age counts in the fallback too, as in StrictestAge: above every other rating it wins.
        var pegi12 = Ratings("""{"pegi":{"rating":"12"}}""");
        Assert.Equal(new AgeRatings.Rating(18, "steam"), AgeRatings.Effective(pegi12, 18, "usk"));
        Assert.Equal(new AgeRatings.Rating(12, "pegi"), AgeRatings.Effective(pegi12, 12, "usk"));
        Assert.Equal(new AgeRatings.Rating(21, "steam"), AgeRatings.Effective(pegi12, 99, "strictest"));

        // No rating at all: Steam's own, then unknown.
        Assert.Equal(new AgeRatings.Rating(18, "steam"), AgeRatings.Effective(new Dictionary<string, int>(), 18, "usk"));
        Assert.Null(AgeRatings.Effective(new Dictionary<string, int>(), null, "usk"));
        Assert.Null(AgeRatings.Effective(new Dictionary<string, int>(), 0, "usk"));
    }

    [Fact]
    public void Malformed_data_makes_the_age_unknown_and_never_looser()
    {
        // The active system's rating is broken: unknown, not the milder other one.
        var own = Ratings("""{"usk":{"rating":"ab 18"},"pegi":{"rating":"3"}}""");
        Assert.Null(AgeRatings.Effective(own, 3, "usk"));
        // Another system's rating is broken while the active one is missing: unknown (it may have been the strictest).
        var other = Ratings("""{"pegi":{"rating":"3"},"esrb":{"rating":"zz"}}""");
        Assert.Null(AgeRatings.Effective(other, 3, "usk"));
        // ESRB rating pending: no rating, the chain goes on.
        Assert.Equal(new AgeRatings.Rating(16, "pegi"), AgeRatings.Effective(Ratings("""{"esrb":{"rating":"rp"},"pegi":{"rating":"16"}}"""), null, "esrb"));
        // An edited cache with a negative or huge value.
        Assert.Null(AgeRatings.Effective(new Dictionary<string, int> { ["usk"] = -7 }, 18, "usk"));
        Assert.Equal(21, AgeRatings.Effective(new Dictionary<string, int> { ["pegi"] = 500 }, null, "pegi")!.Age);
        // The strictest age over all is unknown with any broken rating.
        Assert.Null(AgeRatings.StrictestAge(other, 3));
        Assert.Equal(18, AgeRatings.StrictestAge(Ratings("""{"usk":{"rating":"12"}}"""), 18));
    }

    [Fact]
    public void An_older_cache_with_only_one_age_still_counts()
    {
        var legacy = new StoreInfo(null, [], [], null, []) { RatingKnown = true, RequiredAge = 16 };
        Assert.Equal(new AgeRatings.Rating(16, null), AgeRatings.Effective(legacy, "pegi"));
        Assert.Null(AgeRatings.Effective(legacy with { RequiredAge = null }, "usk"));
        var fresh = StoreInfoService_Parse("""{"required_age":0,"ratings":{"pegi":{"rating":"7"},"usk":{"rating":"12"}}}""");
        Assert.Equal(new AgeRatings.Rating(12, "usk"), AgeRatings.Effective(fresh, "usk"));
        Assert.Equal(new AgeRatings.Rating(7, "pegi"), AgeRatings.Effective(fresh, "pegi"));
        Assert.Equal(12, fresh.RequiredAge);
        // The cache round trip keeps the per-system ratings.
        var back = JsonSerializer.Deserialize<StoreInfo>(JsonSerializer.Serialize(fresh))!;
        Assert.Equal(7, back.Ratings!["pegi"]);
    }

    private static StoreInfo StoreInfoService_Parse(string data) =>
        StoreInfoService.Parse(Json("{\"1\":{\"success\":true,\"data\":" + data + "}}"), "1")!;

    [Theory]
    [InlineData("DE", "usk")]
    [InlineData("de", "usk")]
    [InlineData("US", "esrb")]
    [InlineData("CA", "esrb")]
    [InlineData("FR", "pegi")]
    [InlineData("AT", "pegi")]
    [InlineData("GB", "pegi")]
    [InlineData("CH", "pegi")]
    [InlineData("JP", "strictest")]
    [InlineData("BR", "strictest")]
    [InlineData("419", "strictest")]
    [InlineData("", "strictest")]
    [InlineData(null, "strictest")]
    public void The_region_picks_the_system(string? region, string system) => Assert.Equal(system, AgeRatings.ForRegion(region));

    [Fact]
    public void An_explicit_choice_beats_the_region_and_anything_else_is_strictest()
    {
        Assert.Equal("pegi", AgeRatings.Resolve("pegi", "DE"));
        Assert.Equal("usk", AgeRatings.Resolve("auto", "DE"));
        Assert.Equal("esrb", AgeRatings.Resolve("auto", "US"));
        Assert.Equal("strictest", AgeRatings.Resolve("USK", "DE"));
        Assert.Equal("strictest", AgeRatings.Resolve("none", "DE"));
        Assert.Equal("strictest", AgeRatings.Resolve(null, "DE"));
        Assert.Equal("auto", new AppConfig().AgeRatingSystem);
    }

    [Fact]
    public void The_picker_offers_the_systems_steps()
    {
        Assert.Equal([6, 12, 16, 18], AgeRatings.Steps("usk"));
        Assert.Equal([3, 7, 12, 16, 18], AgeRatings.Steps("pegi"));
        // No AO step under the ESRB (a limit there would let every M game through).
        Assert.Equal([6, 10, 13, 17], AgeRatings.Steps("esrb"));
        Assert.Null(AgeRatings.LimitLabel("esrb", 18));
        Assert.Equal("ESRB AO", AgeRatings.Label("esrb", 18));
        Assert.Equal("ESRB E10+", AgeRatings.LimitLabel("esrb", 10));
        Assert.Equal("ESRB T", AgeRatings.LimitLabel("esrb", 13));
        Assert.Equal("ESRB EC", AgeRatings.Label("esrb", 3));
        Assert.Equal("PEGI 7", AgeRatings.LimitLabel("pegi", 7));
        Assert.Equal("USK 12", AgeRatings.LimitLabel("usk", 12));
        Assert.Null(AgeRatings.LimitLabel("usk", 7));
        Assert.Null(AgeRatings.LimitLabel("strictest", 12));
    }

    [Fact]
    public void Switching_the_system_never_loosens_the_limit()
    {
        foreach (var from in new[] { "usk", "pegi", "esrb", "strictest" })
        foreach (var to in new[] { "usk", "pegi", "esrb", "strictest" })
        foreach (var step in AgeRatings.Steps(from))
        {
            var converted = AgeRatings.ConvertLimit(step, to);
            Assert.True(converted <= step, $"{from} {step} -> {to} {converted}");
            Assert.True(converted > 0);
            Assert.True(AgeRatings.Steps(to).Contains(converted) || converted < AgeRatings.Steps(to).Min());
        }
        Assert.Equal(12, AgeRatings.ConvertLimit(13, "usk"));    // ESRB T -> USK 12
        Assert.Equal(16, AgeRatings.ConvertLimit(17, "pegi"));   // ESRB M -> PEGI 16
        Assert.Equal(6, AgeRatings.ConvertLimit(7, "esrb"));     // PEGI 7 -> ESRB E
        Assert.Equal(6, AgeRatings.ConvertLimit(7, "usk"));      // PEGI 7 -> USK 6
        Assert.Equal(3, AgeRatings.ConvertLimit(3, "usk"));      // below USK 6: stays 3 (stricter than every step)
        Assert.Equal(0, AgeRatings.ConvertLimit(0, "esrb"));     // off stays off
        Assert.Equal(3, AgeRatings.ConvertLimit(15, "pegi"));    // an edited value is read the strict way first
        Assert.Equal(17, AgeRatings.ConvertLimit(18, "esrb"));   // USK/PEGI 18 -> ESRB M, never AO
    }

    [Fact]
    public void A_stored_18_counts_as_m_under_the_esrb_and_ao_stays_blocked()
    {
        Assert.Equal(17, AgeRatings.LimitFor(18, "esrb"));
        Assert.Equal(18, AgeRatings.LimitFor(18, "usk"));
        Assert.Equal(18, AgeRatings.LimitFor(18, "pegi"));
        Assert.Equal(18, AgeRatings.LimitFor(18, "strictest"));
        Assert.Equal(16, AgeRatings.LimitFor(16, "esrb"));
        Assert.Equal(0, AgeRatings.LimitFor(0, "esrb"));
        Assert.Equal(3, AgeRatings.LimitFor(15, "esrb"));
        // An M game under a stored 18: blocked (as under USK 18).
        Assert.Equal("age", Reason(18, false, AgeRatings.VerdictAge([("esrb", 17)], 18, false)));
        // An AO game is above M: blocked at every ESRB step.
        foreach (var step in AgeRatings.Steps("esrb"))
            Assert.Equal("age", Reason(step, false, AgeRatings.VerdictAge([("esrb", 18)], step, false)));
        // T under M: allowed.
        Assert.Null(Reason(17, false, AgeRatings.VerdictAge([("esrb", 13)], 17, false)));
    }

    /// <summary>The parents' rule for one game at this limit (measurability left out, it is not about the age).</summary>
    private static string? Reason(int limit, bool blockUnknown, int? age)
    {
        var rules = new ParentalConfig { Enabled = true, MaxAge = limit, BlockUnknownAge = blockUnknown };
        var item = new LibraryItem { Id = "steam:1", Source = "steam", Name = "G", Type = ItemType.Game, Launch = new LaunchTarget(LaunchKind.Url, "steam://rungameid/1") };
        var reason = ParentalService.CodeReason(rules, item, [item], age);
        return reason == "undetectable" ? null : reason;
    }

    [Fact]
    public void Under_auto_a_region_change_judges_by_both_systems_until_the_parents_save()
    {
        Assert.Equal(["esrb", "usk"], AgeRatings.JudgedSystems("auto", "esrb", "usk"));
        Assert.Equal(["usk"], AgeRatings.JudgedSystems("auto", "usk", "usk"));
        Assert.Equal(["esrb"], AgeRatings.JudgedSystems("auto", "esrb", ""));
        Assert.Equal(["esrb"], AgeRatings.JudgedSystems("auto", "esrb", null));
        Assert.Equal(["esrb"], AgeRatings.JudgedSystems("auto", "esrb", "  "));
        // An edited saved value: read the way the deserializer kept it (case, spaces), anything else counts as the strictest.
        Assert.Equal(["esrb", "usk"], AgeRatings.JudgedSystems("auto", "esrb", " USK "));
        Assert.Equal(["esrb"], AgeRatings.JudgedSystems("auto", "esrb", "ESRB"));
        Assert.Equal(["esrb", "strictest"], AgeRatings.JudgedSystems("auto", "esrb", "nonsense"));
        Assert.Equal(["strictest"], AgeRatings.JudgedSystems("auto", "strictest", "nonsense"));
        Assert.Equal(["pegi", "strictest"], AgeRatings.JudgedSystems("auto", "pegi", "strictest"));
        // An explicit choice does not follow the region: one system.
        Assert.Equal(["usk"], AgeRatings.JudgedSystems("usk", "usk", "esrb"));
    }

    [Fact]
    public void Saving_a_limit_after_a_region_switch_keeps_the_old_system_judged()
    {
        // The first limit records the system it was saved under.
        Assert.Equal("usk", AgeRatings.SavedSystemAfterLimit("", "usk"));
        Assert.Equal("usk", AgeRatings.SavedSystemAfterLimit(null, "usk"));
        Assert.Equal("usk", AgeRatings.SavedSystemAfterLimit("  ", "usk"));
        // The child switched the region to the US, then the parents changed a limit: the USK stays judged next to the ESRB (audit 0.5 L2).
        var saved = AgeRatings.SavedSystemAfterLimit("usk", "esrb");
        Assert.Equal("usk", saved);
        Assert.Equal(["esrb", "usk"], AgeRatings.JudgedSystems("auto", "esrb", saved));
        // An edited value stays as it is (JudgedSystems reads it as the strictest).
        Assert.Equal("nonsense", AgeRatings.SavedSystemAfterLimit("nonsense", "esrb"));
    }

    [Theory]
    [InlineData("usk", "usk", false)]
    [InlineData("usk", "esrb", true)]
    [InlineData(null, "pegi", true)]
    [InlineData("", "pegi", true)]
    [InlineData("nonsense", "usk", true)]
    public void Re_picking_the_saved_system_moves_no_limit(string? saved, string target, bool moves) =>
        Assert.Equal(moves, AgeRatings.LimitsMove(saved, target));

    [Fact]
    public void A_region_switch_never_makes_a_blocked_game_allowed()
    {
        var games = new[]
        {
            Ratings("""{"usk":{"rating":"18"},"pegi":{"rating":"18"},"esrb":{"rating":"m"}}"""),
            Ratings("""{"usk":{"rating":"16"},"pegi":{"rating":"18"},"esrb":{"rating":"m"}}"""),
            Ratings("""{"usk":{"rating":"12"},"pegi":{"rating":"16"},"esrb":{"rating":"t"}}"""),
            Ratings("""{"usk":{"rating":"6"},"pegi":{"rating":"7"},"esrb":{"rating":"e10"}}"""),
            Ratings("""{"usk":{"rating":"0"},"pegi":{"rating":"3"},"esrb":{"rating":"e"}}"""),
            Ratings("""{"pegi":{"rating":"16"}}"""),
            Ratings("""{"esrb":{"rating":"ao"}}"""),
            Ratings("""{"usk":{"rating":"18"}}"""),
            new Dictionary<string, int>()
        };
        string[] systems = ["usk", "pegi", "esrb", "strictest"];
        var checkedCases = 0;
        foreach (var saved in systems)
        foreach (var now in systems)
        foreach (var limit in AgeRatings.Steps(saved))
        foreach (var blockUnknown in new[] { false, true })
        foreach (var ratings in games)
        {
            int? Age(string system) => AgeRatings.Effective(ratings, null, system)?.Age;
            var before = Reason(limit, blockUnknown, AgeRatings.VerdictAge([(saved, Age(saved))], limit, blockUnknown));
            var judged = AgeRatings.JudgedSystems("auto", now, saved);
            var after = Reason(limit, blockUnknown, AgeRatings.VerdictAge(judged.Select(s => (s, Age(s))).ToList(), limit, blockUnknown));
            if (before is null) continue;
            checkedCases++;
            Assert.True(after is not null, $"{saved}->{now} limit {limit} unknown {blockUnknown}: {string.Join(",", ratings)}");
        }
        Assert.True(checkedCases > 100);
    }

    [Fact]
    public void The_verdict_age_takes_the_stricter_verdict()
    {
        // USK 16 allows, PEGI 18 blocks at 18: blocked.
        Assert.Equal(18, AgeRatings.VerdictAge([("usk", 16), ("pegi", 18)], 18, false));
        // Both allow: the higher age, still below the limit.
        Assert.Equal(12, AgeRatings.VerdictAge([("usk", 6), ("pegi", 12)], 16, false));
        // Unknown in one system: with "block unknown" that decides, without it the known one does.
        Assert.Null(AgeRatings.VerdictAge([("usk", 6), ("esrb", null)], 16, true));
        Assert.Equal(6, AgeRatings.VerdictAge([("usk", 6), ("esrb", null)], 16, false));
        // A blocking age beats an unknown one either way.
        Assert.Equal(16, AgeRatings.VerdictAge([("usk", 16), ("esrb", null)], 16, true));
        // No limit: the active system's age as it is.
        Assert.Equal(12, AgeRatings.VerdictAge([("usk", 12), ("pegi", 16)], 0, true));
        Assert.Null(AgeRatings.VerdictAge([], 16, false));
    }

    [Fact]
    public void An_old_config_with_an_age_limit_keeps_the_usk()
    {
        var old = ConfigStore.Parse("""{"parental":{"maxAge":12}}""")!;
        Assert.Equal(12, old.Parental.MaxAge);
        Assert.Equal("usk", old.AgeRatingSystem);
        Assert.Equal("usk", old.AgeRatingSystemResolved);
        // No limit: "auto" as for a new install.
        var noLimit = ConfigStore.Parse("""{"parental":{"maxAge":0}}""")!;
        Assert.Equal("auto", noLimit.AgeRatingSystem);
        Assert.Equal("", noLimit.AgeRatingSystemResolved);
        // A file that already names a system keeps it (the migration runs once).
        var chosen = ConfigStore.Parse("""{"ageRatingSystem":"auto","parental":{"maxAge":12}}""")!;
        Assert.Equal("auto", chosen.AgeRatingSystem);
        var esrb = ConfigStore.Parse("""{"ageRatingSystem":"esrb","ageRatingSystemResolved":"esrb","parental":{"maxAge":17}}""")!;
        Assert.Equal("esrb", esrb.AgeRatingSystem);
        Assert.Equal("esrb", esrb.AgeRatingSystemResolved);
        // A key in another case is not read by the deserializer: the file has no system yet, so it still gets the USK.
        var cased = ConfigStore.Parse("""{"AgeRatingSystem":"auto","parental":{"maxAge":12}}""")!;
        Assert.Equal("usk", cased.AgeRatingSystem);
        Assert.Equal("usk", cased.AgeRatingSystemResolved);
    }

    [Fact]
    public void A_region_switch_and_a_harmless_rematch_keep_the_game_blocked()
    {
        // Limit USK 18 saved under the USK; the game is USK 18 but ESRB T.
        var before = StoreInfoService_Parse("""{"required_age":0,"ratings":{"usk":{"rating":"18"},"esrb":{"rating":"t"}}}""");
        const int limit = 18;
        // The child switches the region to the US: under the ESRB alone the game shows as T (13).
        var knownNow = AgeRatings.Effective(before, "esrb")!.Age;
        Assert.Equal(13, knownNow);
        // ... and re-matches the game to something harmless. The kept floor is the strictest of every system, not the active one's.
        var floor = AgeRatings.KeptAge(before, knownNow);
        Assert.Equal(18, floor);
        var harmless = StoreInfoService_Parse("""{"required_age":0,"ratings":{"usk":{"rating":"0"},"esrb":{"rating":"e"}}}""");
        int? Age(string system, int? keptFloor) => Math.Max(AgeRatings.Effective(harmless, system)?.Age ?? 0, keptFloor ?? 0);
        var judged = AgeRatings.JudgedSystems("auto", "esrb", "usk");
        foreach (var blockUnknown in new[] { false, true })
        {
            Assert.NotNull(Reason(limit, blockUnknown, AgeRatings.VerdictAge(judged.Select(s => (s, Age(s, floor))).ToList(), limit, blockUnknown)));
            // Only the active system's age kept as the floor would have freed it.
            Assert.Null(Reason(limit, blockUnknown, AgeRatings.VerdictAge(judged.Select(s => (s, Age(s, knownNow))).ToList(), limit, blockUnknown)));
        }
        // Nothing cached (or unknown): the known age stays; a lower cached strictest never lowers it.
        Assert.Equal(16, AgeRatings.KeptAge(null, 16));
        Assert.Null(AgeRatings.KeptAge(null, null));
        Assert.Equal(16, AgeRatings.KeptAge(before with { RequiredAge = 12 }, 16));
        Assert.Equal(16, AgeRatings.KeptAge(before with { RatingKnown = false }, 16));
    }

    [Fact]
    public void A_stored_limit_off_the_steps_is_read_the_strict_way()
    {
        Assert.Equal(0, AgeRatings.NormalizeLimit(0));
        Assert.Equal(16, AgeRatings.NormalizeLimit(16));
        Assert.Equal(3, AgeRatings.NormalizeLimit(15));
        Assert.Equal(3, AgeRatings.NormalizeLimit(-4));
        Assert.Equal(3, AgeRatings.NormalizeLimit(99));
        Assert.Equal(3, new ParentalConfig { MaxAge = 21 }.AgeLimit);
        Assert.Equal(12, new ParentalConfig { MaxAge = 12 }.AgeLimit);
    }
}

public class AntiCheatTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("couchhud-ac-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Game(params string[] files)
    {
        var game = Directory.CreateDirectory(Path.Combine(_dir, "Game " + Guid.NewGuid().ToString("N")[..6])).FullName;
        foreach (var f in files)
        {
            var path = Path.Combine(game, f);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (f.EndsWith('\\')) Directory.CreateDirectory(path);
            else File.WriteAllText(path, "");
        }
        return game;
    }

    [Theory]
    [InlineData("EasyAntiCheat\\EasyAntiCheat_EOS_Setup.exe")]
    [InlineData("Binaries\\Win64\\EasyAntiCheat\\Settings.json")]
    [InlineData("BattlEye\\BEService_x64.exe")]
    [InlineData("start_protected_game.exe")]
    [InlineData("GameGuard\\GameMon.des")]
    [InlineData("XIGNCODE\\x3.xem")]
    [InlineData("pb\\pnkbstrA.exe")]
    [InlineData("bin\\vgk.sys")]
    [InlineData("mhyprot2.sys")]
    [InlineData("ACE-Base.sys")]
    [InlineData("equ8\\equ8.dll")]
    [InlineData("AntiCheatExpert\\SGuard64.exe")]
    public void Known_anti_cheat_files_are_found(string file)
    {
        var result = AntiCheat.ScanInstallDir(Game("game.exe", "data\\level1.pak", file));
        Assert.Equal(AntiCheatVerdict.Detected, result.Verdict);
        Assert.False(result.MayFreeze);
    }

    [Fact]
    public void A_plain_game_may_be_frozen()
    {
        var result = AntiCheat.ScanInstallDir(Game("game.exe", "data\\level1.pak", "data\\ace_of_spades.png", "x3d.dll", "docs\\race.txt"));
        Assert.Equal(AntiCheatVerdict.Clean, result.Verdict);
        Assert.True(result.MayFreeze);
    }

    [Fact]
    public void No_folder_or_a_drive_is_no_answer()
    {
        Assert.Equal(AntiCheatVerdict.Unknown, AntiCheat.ScanInstallDir(null).Verdict);
        Assert.Equal(AntiCheatVerdict.Unknown, AntiCheat.ScanInstallDir(Path.Combine(_dir, "missing")).Verdict);
        Assert.Equal(AntiCheatVerdict.Unknown, AntiCheat.ScanInstallDir(Path.GetPathRoot(_dir)).Verdict);
    }

    [Theory]
    [InlineData(@"\\server\share\Games\Game")]
    [InlineData(@"//server/share/Games/Game")]
    [InlineData(@"\\?\UNC\server\share\Game")]
    [InlineData(@"\\.\UNC\server\share\Game")]
    [InlineData(@"\\.\pipe\x")]
    [InlineData(@"\\?\GLOBALROOT\Device\Mup\server\share")]
    public void A_share_is_no_answer_and_never_touched(string path)
    {
        var result = AntiCheat.ScanInstallDir(path, isNetworkDrive: _ => throw new InvalidOperationException("not asked for a share"));
        Assert.Equal(AntiCheatVerdict.Unknown, result.Verdict);
        Assert.Equal("install folder on a network share", result.Marker);
    }

    [Fact]
    public void A_mapped_network_drive_is_no_answer_a_local_long_path_is_scanned()
    {
        var game = Game("game.exe");
        var letter = char.ToUpperInvariant(game[0]);
        var mapped = AntiCheat.ScanInstallDir(game, isNetworkDrive: d => d == letter);
        Assert.Equal(AntiCheatVerdict.Unknown, mapped.Verdict);
        Assert.Equal("install folder on a network drive", mapped.Marker);
        Assert.Equal(AntiCheatVerdict.Clean, AntiCheat.ScanInstallDir(game, isNetworkDrive: _ => false).Verdict);
        // The long form of a local folder is no share.
        Assert.False(AntiCheat.IsSharePath(@"\\?\" + game));
        Assert.Equal(AntiCheatVerdict.Unknown, AntiCheat.ScanInstallDir(@"\\?\" + game, isNetworkDrive: d => d == letter).Verdict);
    }

    [Fact]
    public void The_scan_stops_at_its_bounds_and_says_unknown()
    {
        var game = Game("a\\b\\c\\d\\e\\EasyAntiCheat.exe");
        // Deeper than the bound: not seen, but nothing else either.
        Assert.Equal(AntiCheatVerdict.Clean, AntiCheat.ScanInstallDir(game, new AntiCheatScanLimits(3, 1000, TimeSpan.FromSeconds(5))).Verdict);
        Assert.Equal(AntiCheatVerdict.Detected, AntiCheat.ScanInstallDir(game, new AntiCheatScanLimits(6, 1000, TimeSpan.FromSeconds(5))).Verdict);

        var many = Game(Enumerable.Range(0, 30).Select(i => $"file{i}.dat").ToArray());
        Assert.Equal(AntiCheatVerdict.Unknown, AntiCheat.ScanInstallDir(many, new AntiCheatScanLimits(4, 10, TimeSpan.FromSeconds(5))).Verdict);

        long now = 0;
        var slow = AntiCheat.ScanInstallDir(many, new AntiCheatScanLimits(4, 1000, TimeSpan.FromMilliseconds(100)), () => now += 50);
        Assert.Equal(AntiCheatVerdict.Unknown, slow.Verdict);

        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Equal(AntiCheatVerdict.Unknown, AntiCheat.ScanInstallDir(many, cancel: cancel.Token).Verdict);
    }

    [JunctionFact]
    public void A_junction_is_not_followed()
    {
        var outside = Game("EasyAntiCheat\\EasyAntiCheat.exe");
        var game = Game("game.exe");
        Links.Junction(Path.Combine(game, "mods"), outside);
        try
        {
            // The anti-cheat behind the junction is outside the install folder: not read.
            Assert.Equal(AntiCheatVerdict.Clean, AntiCheat.ScanInstallDir(game).Verdict);
        }
        finally
        {
            Directory.Delete(Path.Combine(game, "mods"));
        }

        // An install folder that is a junction itself: no answer, nothing read.
        var link = Path.Combine(_dir, "linked-game");
        Links.Junction(link, game);
        try
        {
            Assert.Equal(AntiCheatVerdict.Unknown, AntiCheat.ScanInstallDir(link).Verdict);
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void Processes_of_the_game_give_it_away()
    {
        Assert.Equal(AntiCheatVerdict.Detected, AntiCheat.CheckProcesses([("game.exe", @"C:\G\game.exe"), ("BEService.exe", null)]).Verdict);
        Assert.Equal(AntiCheatVerdict.Detected, AntiCheat.CheckProcesses([(null, @"C:\G\EasyAntiCheat_EOS.exe")]).Verdict);
        Assert.Equal(AntiCheatVerdict.Clean, AntiCheat.CheckProcesses([("game.exe", @"C:\G\game.exe"), ("crashpad_handler.exe", null)]).Verdict);
        Assert.Equal(AntiCheatVerdict.Clean, AntiCheat.CheckProcesses([(null, "bad|path\0")]).Verdict);
    }

    [Fact]
    public void Both_checks_together_and_a_hit_beats_unknown()
    {
        var clean = AntiCheatResult.Clean;
        var hit = new AntiCheatResult(AntiCheatVerdict.Detected, "battleye");
        var unknown = new AntiCheatResult(AntiCheatVerdict.Unknown, "x");
        Assert.True(AntiCheat.Combine(clean, clean).MayFreeze);
        Assert.Equal(hit, AntiCheat.Combine(unknown, hit));
        Assert.Equal(hit, AntiCheat.Combine(hit, clean));
        Assert.Equal(unknown, AntiCheat.Combine(unknown, clean));
    }

    private static LibraryItem Item(string id, string? dir) => new()
    {
        Id = id,
        Source = "custom",
        Name = id,
        Type = ItemType.Game,
        InstallDir = dir,
        Launch = new LaunchTarget(LaunchKind.Exe, "x")
    };

    [Fact]
    public async Task The_guard_scans_once_and_an_unfinished_scan_means_no_freeze()
    {
        var gate = new ManualResetEventSlim();
        var scans = 0;
        var guard = new FreezeGuard(_ =>
        {
            Interlocked.Increment(ref scans);
            gate.Wait(TimeSpan.FromSeconds(10));
            return AntiCheatResult.Clean;
        });
        var item = Item("custom:a", @"C:\Games\A");
        var first = guard.Prepare(item);
        Assert.Equal(AntiCheatVerdict.Unknown, guard.Check(item, []).Verdict);
        gate.Set();
        await first;
        Assert.True(guard.Check(item, []).MayFreeze);
        Assert.False(guard.Check(item, [("vgc.exe", null)]).MayFreeze);
        Assert.Equal(1, scans);

        guard.Forget(item);
        await guard.Prepare(item);
        Assert.Equal(2, scans);

        var failing = new FreezeGuard(_ => throw new IOException("boom"));
        var task = failing.Prepare(item);
        await Assert.ThrowsAsync<IOException>(() => task);
        Assert.Equal(AntiCheatVerdict.Unknown, failing.Check(item, []).Verdict);

        Assert.True(guard.FirstNotice("a|1"));
        Assert.False(guard.FirstNotice("a|1"));
        Assert.True(guard.FirstNotice("a|2"));
    }
}
