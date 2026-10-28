using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CouchHud.Core.Config;
using CouchHud.Core.Parental;
using Xunit;

namespace CouchHud.Core.Tests;

/// <summary>Profiles per person, package 1: the data model, the accessor and the play time account per profile.</summary>
public sealed class ProfileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("couchhud-profiles-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A config.json as 0.4 wrote it.</summary>
    private const string OldConfig = """
        {
          "skin": "ps4",
          "language": "en",
          "reduceMotion": true,
          "favorites": ["steam:1", "epic:2"],
          "lastLaunched": { "steam:1": 1700000000000 },
          "playtimeMinutes": { "steam:1": 42.5 },
          "lastSessionMinutes": { "steam:1": 12 },
          "collections": [ { "id": "c1", "name": "Koop", "items": ["steam:1"], "onHome": true } ],
          "parental": { "enabled": true, "weekdayMinutes": 45, "maxAge": 12, "lockedItems": ["epic:2"], "ledgerStarted": true, "timeZoneId": "W. Europe Standard Time" }
        }
        """;

    private static AppConfig Parse(string json) => ConfigStore.Parse(json)!;

    private static string Serialize(AppConfig c) => JsonSerializer.Serialize(c, ConfigStore.JsonOptions);

    private static ProfileView Child(AppConfig c, string name = "Mia")
    {
        var p = c.AddProfile(name, "fox", "#ff8800");
        p.Favorites.Add("steam:9");
        p.PlaytimeMinutes["steam:9"] = 7;
        p.Rules.WeekdayMinutes = 30;
        p.Rules.MaxAge = 6;
        p.Skin = "nova";
        return p;
    }

    // --- Old files -------------------------------------------------------------------------------------------------------

    [Fact]
    public void An_old_config_is_the_default_profile_unchanged()
    {
        File.WriteAllText(Path.Combine(_dir, "config.json"), OldConfig);
        var c = new ConfigStore(_dir).Current;
        var p = c.ActiveProfile;

        Assert.True(p.IsDefault);
        Assert.Equal(ProfileIds.Default, p.Id);
        Assert.Null(c.Profiles);
        Assert.Equal(["steam:1", "epic:2"], p.Favorites);
        Assert.Equal(1700000000000, p.LastLaunched["steam:1"]);
        Assert.Equal(42.5, p.PlaytimeMinutes["steam:1"]);
        Assert.Equal(12, p.LastSessionMinutes["steam:1"]);
        Assert.Equal("Koop", Assert.Single(p.Collections).Name);
        // The very objects of the file: writes through the view land in the old fields.
        Assert.Same(c.Favorites, p.Favorites);
        Assert.Same(c.Parental, p.Rules);
        Assert.Equal(45, p.Rules.WeekdayMinutes);
        Assert.Equal(12, p.Rules.MaxAge);
        Assert.True(p.Rules.Enabled);
        Assert.True(c.ParentalEnabled);
        Assert.Equal("deepsea", p.Skin); // 0.4 called it "ps4"
        Assert.Equal("en", p.Language);
        Assert.True(p.ReduceMotion);
    }

    [Fact]
    public void Without_further_profiles_the_file_keeps_its_old_shape()
    {
        var c = Parse(OldConfig);
        c.ActiveProfile.Favorites.Add("steam:3");
        c.ActiveProfile.Skin = "greenlight";
        var json = JsonNode.Parse(Serialize(c))!.AsObject();

        Assert.False(json.ContainsKey("profiles"));
        Assert.False(json.ContainsKey("activeProfileId"));
        Assert.False(json.ContainsKey("activeProfile"));
        Assert.False(json.ContainsKey("allProfiles"));
        Assert.False(json.ContainsKey("parentalEnabled"));
        Assert.Contains("steam:3", json["favorites"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("greenlight", json["skin"]!.GetValue<string>());
    }

    // --- Roundtrip and downgrade -------------------------------------------------------------------------------------------

    [Fact]
    public void Profiles_survive_a_save_and_a_load()
    {
        var store = new ConfigStore(_dir);
        string id = "";
        store.Update(c =>
        {
            id = Child(c).Id;
            c.ActiveProfileId = id;
        });

        var again = new ConfigStore(_dir).Current;
        var p = again.ActiveProfile;
        Assert.False(p.IsDefault);
        Assert.Equal(id, p.Id);
        Assert.Equal(new ProfileInfo("Mia", "fox", "#ff8800", true), p.Info);
        Assert.Null(again.Profile(null).Info);
        Assert.Equal(["steam:9"], p.Favorites);
        Assert.Equal(7, p.PlaytimeMinutes["steam:9"]);
        Assert.Equal(30, p.Rules.WeekdayMinutes);
        Assert.Equal(6, p.Rules.MaxAge);
        Assert.Equal("nova", p.Skin);
        // Not set for the child: follows the household.
        Assert.Equal(again.SoundVolume, p.SoundVolume);
        Assert.Equal(2, again.AllProfiles.Count);
    }

    /// <summary>What 0.4 reads of a file 0.5 wrote (it ignores fields it does not know).</summary>
    private sealed class Version04
    {
        public List<string> Favorites { get; set; } = [];
        public Dictionary<string, double> PlaytimeMinutes { get; set; } = new();
        public List<ItemCollection> Collections { get; set; } = [];
        public ParentalConfig Parental { get; set; } = new();
        public string Skin { get; set; } = "default";
        public string Language { get; set; } = "auto";
    }

    [Fact]
    public void After_a_downgrade_the_default_profile_is_what_it_was()
    {
        var c = Parse(OldConfig);
        var child = Child(c);
        c.ActiveProfileId = child.Id;
        // Everything done while the child is on the couch stays with the child.
        c.ActiveProfile.Favorites.Add("steam:10");
        c.ActiveProfile.Collections.Add(new ItemCollection { Id = "k", Name = "Kinder" });
        c.ActiveProfile.Rules.LockedItems.Add("steam:1");
        c.ActiveProfile.Language = "de";

        var old = JsonSerializer.Deserialize<Version04>(Serialize(c), ConfigStore.JsonOptions)!;

        Assert.Equal(["steam:1", "epic:2"], old.Favorites);
        Assert.Equal(42.5, Assert.Single(old.PlaytimeMinutes).Value);
        Assert.Equal("Koop", Assert.Single(old.Collections).Name);
        Assert.Equal(45, old.Parental.WeekdayMinutes);
        Assert.Equal(12, old.Parental.MaxAge);
        Assert.Equal(["epic:2"], old.Parental.LockedItems);
        Assert.True(old.Parental.Enabled);
        Assert.True(old.Parental.LedgerStarted);
        Assert.Equal("deepsea", old.Skin);
        Assert.Equal("en", old.Language);
    }

    // --- Rules and the main switch -----------------------------------------------------------------------------------------

    [Fact]
    public void The_main_switch_is_the_households_and_shows_in_every_profile()
    {
        var c = Parse(OldConfig);
        var child = Child(c);
        c.Profiles!.Single(x => x.Id == child.Id).Data!.Rules!.Enabled = false; // whatever the entry says

        Assert.True(c.Profile(child.Id).Rules.Enabled);
        c.ParentalEnabled = false;
        Assert.False(c.Profile(child.Id).Rules.Enabled);
        Assert.False(c.Parental.Enabled);
    }

    [Fact]
    public void A_new_profile_starts_with_the_default_rules_and_a_fresh_account()
    {
        var c = Parse(OldConfig);
        var p = c.AddProfile("Ben");

        Assert.True(ProfileIds.IsExtra(p.Id));
        Assert.Equal(45, p.Rules.WeekdayMinutes);
        Assert.Equal(12, p.Rules.MaxAge);
        Assert.Equal(["epic:2"], p.Rules.LockedItems);
        Assert.NotSame(c.Parental.LockedItems, p.Rules.LockedItems);
        Assert.Equal("W. Europe Standard Time", p.Rules.TimeZoneId);
        Assert.False(p.Rules.LedgerStarted);
        Assert.Empty(p.Favorites);
        Assert.Equal("deepsea", p.Skin); // follows the household until changed
    }

    [Fact]
    public void Writes_through_a_further_profile_leave_the_default_alone()
    {
        var c = Parse(OldConfig);
        var p = Child(c);
        p.ReduceMotion = false;
        p.Rules.PerGameMinutes = 20;

        Assert.True(c.ReduceMotion);
        Assert.Equal(0, c.Parental.PerGameMinutes);
        Assert.Equal("deepsea", c.Skin);
        Assert.DoesNotContain("steam:9", c.Favorites);
        Assert.True(c.ActiveProfile.ReduceMotion);
    }

    [Fact]
    public void The_parental_service_follows_the_profiles_rules()
    {
        var c = Parse(OldConfig);
        var child = Child(c);
        c.ActiveProfileId = child.Id;
        var monday = new DateTimeOffset(2026, 10, 19, 15, 0, 0, TimeSpan.Zero);
        var clock = new TrustedClock(() => monday, () => 0, _ => TimeSpan.Zero);
        var service = new ParentalService(() => c.ActiveProfile.Rules, new PlayLedger(), clock);

        Assert.Equal(30 * 60, service.Status(null).RemainingSeconds);
        c.ActiveProfileId = null;
        Assert.Equal(45 * 60, service.Status(null).RemainingSeconds);
    }

    // --- Fail-closed ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("nobody")]
    [InlineData("../playtime")]
    [InlineData("..")]
    [InlineData("A1")]
    [InlineData("")]
    [InlineData("x/y")]
    [InlineData("x\\y")]
    [InlineData("c:")]
    [InlineData("a.b")]
    [InlineData("-dash")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 33
    public void An_unknown_or_bad_active_id_gives_the_default_profiles_rules(string id)
    {
        var c = Parse(OldConfig);
        Child(c);
        c.ActiveProfileId = id;

        Assert.True(c.ActiveProfile.IsDefault);
        Assert.Same(c.Parental, c.ActiveProfile.Rules);
        Assert.Equal(12, c.ActiveProfile.Rules.MaxAge);
    }

    public static TheoryData<string> BrokenEntries => new()
    {
        """{ "id": "kid", "name": "Kid" }""", // no data
        """{ "id": "kid", "name": "Kid", "data": {} }""", // no rules
        """{ "id": "kid", "name": "Kid", "data": { "rules": null } }""",
        """{ "id": "kid", "name": "Kid", "data": { "rules": { "maxAge": 8 } } }""", // no step of any rating system
        """{ "id": "kid", "name": "Kid", "data": { "rules": { "weekdayMinutes": -1 } } }""",
        """{ "id": "kid", "name": "Kid", "data": { "rules": { "weekendMinutes": 99999 } } }""",
        """{ "id": "kid", "name": "Kid", "data": { "rules": { "perGameMinutes": -5 } } }""",
        """{ "id": "kid", "name": "Kid", "data": { "rules": { "windowStartMinute": -1, "windowEndMinute": 600 } } }""",
        """{ "id": "kid", "name": "Kid", "data": { "rules": { "windowStartMinute": 600, "windowEndMinute": 600 } } }""",
        """{ "id": "kid", "name": "Kid", "data": { "rules": { "lockedItems": null } } }""",
        """{ "id": "kid", "name": "Kid", "data": { "rules": { "timeZoneId": null } } }""",
        """{ "id": "kid", "name": "Kid", "data": { "rules": {}, "favorites": null } }""",
        """{ "id": "kid", "name": "Kid", "data": { "rules": {}, "collections": [ null ] } }""",
        """{ "id": "kid", "name": "Kid", "data": { "rules": {}, "settings": null } }""",
        """{ "id": "kid", "name": null, "data": { "rules": {} } }""",
        """{ "id": "Kid", "name": "Kid", "data": { "rules": {} } }""",
        """{ "id": "default", "name": "Kid", "data": { "rules": {} } }""",
        """{ "id": "kid\n", "name": "Kid", "data": { "rules": { "maxAge": 6 } } }""", // "$" would let a trailing line break through
        """null""",
    };

    [Theory]
    [MemberData(nameof(BrokenEntries))]
    public void A_broken_profile_entry_is_not_used_and_the_default_rules_apply(string entry)
    {
        var id = entry.Contains("\"Kid\", \"name\"") ? "Kid" : entry.Contains("\"default\"") ? "default" : entry.Contains(@"kid\n") ? @"kid\n" : "kid";
        var c = Parse($$"""{ "parental": { "enabled": true, "maxAge": 12, "weekdayMinutes": 45 }, "profiles": [ {{entry}} ], "activeProfileId": "{{id}}" }""");

        var p = c.ActiveProfile;
        Assert.True(p.IsDefault);
        Assert.Same(c.Parental, p.Rules);
        Assert.Equal(12, p.Rules.MaxAge);
        Assert.Single(c.AllProfiles);
    }

    [Fact]
    public void A_sound_profile_entry_next_to_it_is_used()
    {
        var c = Parse("""{ "parental": { "enabled": true, "maxAge": 12 }, "profiles": [ { "id": "kid", "name": "Kid", "data": { "rules": { "maxAge": 6 } } } ], "activeProfileId": "kid" }""");

        Assert.False(c.ActiveProfile.IsDefault);
        Assert.Equal(6, c.ActiveProfile.Rules.MaxAge);
        Assert.True(c.ActiveProfile.Rules.Enabled);
    }

    [Fact]
    public void With_the_same_id_twice_the_first_entry_counts_even_when_broken()
    {
        var c = Parse("""
            { "parental": { "maxAge": 12 }, "activeProfileId": "kid", "profiles": [
              { "id": "kid", "name": "Kid", "data": { "rules": { "maxAge": 99 } } },
              { "id": "kid", "name": "Kid", "data": { "rules": { "maxAge": 0 } } } ] }
            """);

        Assert.True(c.ActiveProfile.IsDefault);
        Assert.Equal(12, c.ActiveProfile.Rules.MaxAge);
        Assert.Single(c.AllProfiles);
    }

    [Fact]
    public void The_fault_config_has_no_profile_to_escape_to()
    {
        var c = ConfigStore.FaultConfig();
        c.ActiveProfileId = "kid";
        Assert.True(c.ActiveProfile.IsDefault);
        Assert.True(c.ActiveProfile.Rules.Enabled);
        Assert.Equal(6, c.ActiveProfile.Rules.MaxAge);
    }

    // --- Ids and play time files -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("../evil")]
    [InlineData("..\\evil")]
    [InlineData("a/b")]
    [InlineData("C:\\x")]
    [InlineData("A")]
    [InlineData("a.b")]
    [InlineData("a b")]
    [InlineData("")]
    [InlineData("-x")]
    [InlineData("äb")]
    [InlineData("kid\n")]
    [InlineData("kid\r\n")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void A_bad_id_has_no_play_time_file(string id)
    {
        Assert.False(ProfileIds.IsExtra(id));
        Assert.Throws<ArgumentException>(() => ProfileIds.LedgerFile(_dir, id));
    }

    [Fact]
    public void Each_profile_has_its_own_play_time_file()
    {
        Assert.Equal(Path.Combine(_dir, "playtime.json"), ProfileIds.LedgerFile(_dir, ProfileIds.Default));
        Assert.Equal(Path.Combine(_dir, "playtime-ab12-cd.json"), ProfileIds.LedgerFile(_dir, "ab12-cd"));
        var id = ProfileIds.New();
        Assert.True(ProfileIds.IsExtra(id));
        Assert.NotEqual(id, ProfileIds.New());
    }

    [Fact]
    public void Play_time_is_counted_per_profile()
    {
        var key = new byte[32];
        var today = new DateOnly(2026, 10, 19);
        var main = ProfileIds.LedgerFile(_dir, ProfileIds.Default);
        var kid = ProfileIds.LedgerFile(_dir, "kid");

        var a = new PlayLedger(main, key);
        a.Add(today, "g1", 600);
        a.Save(today);
        var b = new PlayLedger(kid, key);
        b.Add(today, "g1", 60);
        b.Save(today);

        Assert.Equal(600, new PlayLedger(main, key, expectExisting: true).Seconds(today));
        Assert.Equal(60, new PlayLedger(kid, key, expectExisting: true).Seconds(today));
    }

    [Fact]
    public void A_new_profiles_missing_account_is_no_tampering()
    {
        var file = ProfileIds.LedgerFile(_dir, "kid");
        var ledger = new PlayLedger(file, new byte[32], expectExisting: PlayLedger.ShouldExist(file, started: false));
        Assert.False(ledger.Tampered);
        Assert.Equal(0, ledger.Seconds(new DateOnly(2026, 10, 19)));
    }

    [Fact]
    public void A_started_profiles_missing_account_is_tampering_not_zero_minutes()
    {
        var key = new byte[32];
        var today = new DateOnly(2026, 10, 19);
        var file = ProfileIds.LedgerFile(_dir, "kid");
        var ledger = new PlayLedger(file, key);
        ledger.Add(today, "g1", 3600);
        ledger.Save(today);
        File.Delete(file);

        // The good copy of the first save is there already: the hour stays counted, never zero.
        var fromCopy = new PlayLedger(file, key, expectExisting: PlayLedger.ShouldExist(file, started: true));
        Assert.Equal(3600, fromCopy.Seconds(today));
        // Copy gone too: tampering.
        File.Delete(file + ".bak");
        Assert.True(new PlayLedger(file, key, expectExisting: PlayLedger.ShouldExist(file, started: true)).Tampered);
    }

    [Fact]
    public void A_deleted_account_whose_copy_is_left_is_tampering_even_before_it_was_marked()
    {
        var key = new byte[32];
        var today = new DateOnly(2026, 10, 19);
        var file = ProfileIds.LedgerFile(_dir, "kid");
        var ledger = new PlayLedger(file, key);
        ledger.Add(today, "g1", 3600);
        ledger.Save(today);
        ledger.Add(today, "g1", 60);
        ledger.Save(today); // now there is a .bak
        File.Delete(file);
        File.WriteAllText(file + ".bak", """{"Days":{}}"""); // and it was emptied as well

        Assert.True(PlayLedger.ShouldExist(file, started: false));
        Assert.True(new PlayLedger(file, key, expectExisting: PlayLedger.ShouldExist(file, started: false)).Tampered);
    }

    [Fact]
    public void With_parental_controls_off_a_good_copy_of_a_missing_account_is_used_without_alarm()
    {
        var key = new byte[32];
        var today = new DateOnly(2026, 10, 19);
        var file = ProfileIds.LedgerFile(_dir, ProfileIds.Default);
        var ledger = new PlayLedger(file, key);
        ledger.Add(today, "g1", 600);
        ledger.Save(today);
        ledger.Add(today, "g1", 60);
        ledger.Save(today); // the copy holds the 660 seconds as well (refreshed after every save)
        File.Delete(file);

        var expect = PlayLedger.ShouldExist(file, started: false);
        Assert.True(expect);
        var again = new PlayLedger(file, key, expectExisting: expect);
        Assert.False(again.Tampered);
        Assert.Equal(660, again.Seconds(today));
        var monday = new DateTimeOffset(2026, 10, 19, 15, 0, 0, TimeSpan.Zero);
        var service = new ParentalService(() => new ParentalConfig { Enabled = false }, again, new TrustedClock(() => monday, () => 0, _ => TimeSpan.Zero));
        Assert.False(service.Tampered);
        Assert.Null(service.Status("g1").Limiter);
        Assert.True(service.CanStart("g1").Ok);
    }

    // --- Contracts ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_per_person_settings_list_agrees_with_the_view_and_the_config()
    {
        foreach (var setting in typeof(ProfileSettings).GetProperties())
        {
            var top = typeof(AppConfig).GetProperty(setting.Name);
            var view = typeof(ProfileView).GetProperty(setting.Name);
            Assert.True(top is not null, $"AppConfig.{setting.Name} is missing");
            Assert.True(view is not null && view.CanWrite, $"ProfileView.{setting.Name} is missing");
            Assert.Equal(top!.PropertyType, view!.PropertyType);
            Assert.Equal(top.PropertyType, Nullable.GetUnderlyingType(setting.PropertyType) ?? setting.PropertyType);
        }
    }

    [Fact]
    public void Every_per_person_setting_is_kept_apart()
    {
        var c = new AppConfig();
        var kid = c.AddProfile("Kid");
        foreach (var setting in typeof(ProfileSettings).GetProperties())
        {
            var prop = typeof(ProfileView).GetProperty(setting.Name)!;
            var before = typeof(AppConfig).GetProperty(setting.Name)!.GetValue(c);
            object other = prop.PropertyType == typeof(bool) ? !(bool)before! : prop.PropertyType == typeof(int) ? (int)before! + 1 : before + "x";
            prop.SetValue(kid, other);
            Assert.Equal(before, typeof(AppConfig).GetProperty(setting.Name)!.GetValue(c));
            Assert.Equal(other, prop.GetValue(c.Profile(kid.Id)));
            Assert.Equal(other, setting.GetValue(c.Profiles!.Single(x => x.Id == kid.Id).Data!.Settings));
        }
    }

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CouchHud.slnx"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }

    /// <summary>The fields of the default profile, reached past the accessor.</summary>
    internal static Regex DirectUse()
    {
        var fields = new[] { "Favorites", "Collections", "LastLaunched", "PlaytimeMinutes", "LastSessionMinutes", "Parental" }
            .Concat(typeof(ProfileSettings).GetProperties().Select(p => p.Name));
        // Receivers that hold an AppConfig in src: the lambda's "c", "cfg" and "….Current".
        return new Regex(@"(?:(?<![\w.])(?:c|cfg)|\bCurrent)[?!]?\.(?:" + string.Join("|", fields) + @")\b"
            // The data of a further profile past its view, the profile list, the choice of profile and the main switch through a profile.
            + @"|\.Data[?!]?\.Rules\b|\.Profiles\b|\bActiveProfileId\s*=(?!=)|\.Rules[?!]?\.Enabled\s*=(?!=)");
    }

    [Theory]
    [InlineData("config.Update(c => c.Favorites.Add(id));", true)]
    [InlineData("var p = config.Current.Parental;", true)]
    [InlineData("if (_config.Current?.Parental.Enabled) {}", true)]
    [InlineData("cfg.LastLaunched.TryGetValue(id, out var x);", true)]
    [InlineData("config.UpdateOrThrow(c => c.Skin = id);", true)]
    [InlineData("Strings.Use(new ConfigStore(dir).Current.Language);", true)]
    [InlineData("config.Update(c => c.ActiveProfile.Favorites.Add(id));", false)]
    [InlineData("var p = config.Current.ActiveProfile.Rules;", false)]
    [InlineData("if (config.Current.ParentalEnabled) {}", false)]
    [InlineData("var lang = Strings.Language;", false)]
    [InlineData("using System.Collections.Generic;", false)]
    [InlineData("using CouchHud.Core.Parental;", false)]
    [InlineData("i with { PlaytimeMinutes = total }", false)]
    [InlineData("item.PlaytimeMinutes", false)]
    [InlineData("config.Current!.Favorites.Add(id);", true)]
    [InlineData("c!.Parental.MaxAge = 0;", true)]
    [InlineData("entry.Data.Rules.MaxAge = 0;", true)]
    [InlineData("entry.Data!.Rules!.MaxAge = 0;", true)]
    [InlineData("foreach (var p in c.Profiles ?? []) {}", true)]
    [InlineData("c.ActiveProfileId = \"kid\";", true)]
    [InlineData("c.ActiveProfile.Rules.Enabled = true;", true)]
    [InlineData("if (c.ActiveProfileId == id) {}", false)]
    [InlineData("var x = c.GameProfiles;", false)]
    public void The_contract_pattern_sees_direct_use(string line, bool direct) => Assert.Equal(direct, DirectUse().IsMatch(line));

    [Fact]
    public void Only_the_accessor_touches_the_default_profiles_fields()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var allowed = new[] { Path.Combine("CouchHud.Core", "Config", "AppConfig.cs"), Path.Combine("CouchHud.Core", "Config", "Profiles.cs") };
        var pattern = DirectUse();
        var found = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            if (rel.Split(Path.DirectorySeparatorChar).Any(part => part is "obj" or "bin")) continue;
            if (allowed.Contains(rel)) continue;
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                if (pattern.IsMatch(lines[i])) found.Add($"{rel}:{i + 1}: {lines[i].Trim()}");
        }
        Assert.True(found.Count == 0, "Use config.ActiveProfile / config.Profile(id) / config.ParentalEnabled instead:\n" + string.Join("\n", found));
    }
}
