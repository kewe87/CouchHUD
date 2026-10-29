using CouchHud.Core.Plugins;
using CouchHud.Sdk;
using CouchHud.Core.Updates;
using Xunit;

namespace CouchHud.Core.Tests;

public class PluginTextTests
{
    [Theory]
    [InlineData("🏠 Smart Home", "Smart Home")]
    [InlineData("Smart Home 🏠", "Smart Home")]
    [InlineData("👨‍👩‍👧 Familie", "Familie")] // ZWJ sequence
    [InlineData("🇩🇪 Deutsch", "Deutsch")] // flag
    [InlineData("👍🏽 Ok", "Ok")] // skin tone
    [InlineData("❤️ Liebe", "Liebe")] // variation selector
    [InlineData("1️⃣ Eins", "1 Eins")] // keycap: the digit stays
    [InlineData("\u202Eevil\u202C", "evil")] // bidi override
    [InlineData("a\u200Bb\u200Dc", "abc")] // zero width space and joiner
    [InlineData("  Viel   Platz\t\n", "Viel Platz")]
    [InlineData("Ärger & Öl", "Ärger & Öl")]
    [InlineData("Tür\u0001auf", "Türauf")]
    [InlineData("★ Stern → weiter", "Stern weiter")]
    public void Strips_decoration_and_keeps_text(string raw, string expected)
    {
        Assert.Equal(expected, PluginText.Strip(raw));
    }

    [Fact]
    public void Combining_marks_of_names_stay_but_variation_selectors_and_keycaps_go()
    {
        Assert.Equal("Caf\u00E9", PluginText.Strip("Cafe\u0301")); // e + combining acute composes to é
        Assert.Equal("क्षि", PluginText.Strip("क्षि")); // vowel signs and virama of another script stay
        Assert.Equal("A", PluginText.Strip("A\uFE0F\U000E0100\u20E3"));
    }

    [Theory]
    [InlineData("Еinstеllungen")] // Cyrillic Е and е among Latin letters
    [InlineData("Εinstellungen")] // Greek capital epsilon
    [InlineData("Einstellungen")]
    [InlineData("Settings")]
    [InlineData("Eins\u200Btellungen")]
    [InlineData("S3ttings")]
    public void A_third_party_title_cannot_imitate_a_core_page(string raw)
    {
        Assert.Equal("fakeplugin", PluginText.Safe(raw, "fakeplugin", firstParty: false));
    }

    [Theory]
    [InlineData("Аррѕ")] // all Cyrillic, drawn like "Apps"
    [InlineData("Ѕеttіngѕ")]
    [InlineData("ɡames")] // IPA g
    [InlineData("Ꮪettings")] // Cherokee S
    [InlineData("\uFF33\uFF45\uFF54\uFF54\uFF49\uFF4E\uFF47\uFF53")] // fullwidth "Settings"
    [InlineData("\U0001D412ettings")] // mathematical bold S
    [InlineData("Բrowser")] // Armenian letter among Latin
    public void Look_alikes_of_other_scripts_do_not_pass_for_a_core_page(string raw)
    {
        Assert.Equal("fakeplugin", PluginText.Safe(raw, "fakeplugin", firstParty: false));
    }

    [Fact]
    public void Ordinary_and_first_party_titles_pass_the_look_alike_check()
    {
        Assert.Equal("Garage Lichter", PluginText.Safe("Garage Lichter", "x", false));
        Assert.Equal("Настройки", PluginText.Safe("Настройки", "x", false)); // a whole word in another script is no look-alike
        Assert.Equal("Plugin Settings", PluginText.Safe("Plugin Settings", "x", false)); // only the exact name of a screen is refused
        Assert.Equal("Smart Home", PluginText.Safe("Smart Home", "homeassistant", firstParty: true));
        Assert.Equal("Einstellungen", PluginText.Safe("Einstellungen", "x", firstParty: true));
        Assert.Equal("homeassistant", PluginText.Safe("🏠", "homeassistant", false));
    }

    [Fact]
    public void Nothing_left_falls_back()
    {
        Assert.Equal("", PluginText.Strip("🏠🏠 \u200D"));
        Assert.Equal("", PluginText.Strip(null));
        Assert.Equal("homeassistant", PluginText.Title("🏠", "homeassistant"));
        Assert.Equal("Name", PluginText.Title("Name", "x"));
    }

    [Fact]
    public void Length_is_capped_without_cutting_a_character()
    {
        Assert.Equal(new string('a', 10), PluginText.Strip(new string('a', 50), 10));
        Assert.Equal("ab", PluginText.Strip("ab😀😀😀", 2));
        Assert.True(PluginText.Strip(new string('x', 100000)).Length <= PluginText.MaxTitle);
    }

    [Theory]
    [InlineData("🏠 Home", "Home")] // Home is a built-in title only for the check below; the cleaning itself is the same
    [InlineData("Sma\u200Drt", "Smart")]
    public void Tab_titles_are_stripped_first(string raw, string expected)
    {
        Assert.Equal(expected, PluginTabRules.CleanTitle(raw));
    }

    [Theory]
    [InlineData("Einstellungen")]
    [InlineData("Eins\u200Btellungen")]
    [InlineData("\u202EEinstellungen")]
    [InlineData("⚙ Settings")]
    [InlineData("Einstel1ungen")]
    public void A_plugin_tab_cannot_pass_for_a_core_tab(string raw)
    {
        var tab = new PluginTab("t", "p", raw, raw);
        Assert.NotNull(PluginTabRules.Problem(tab, firstParty: false));
    }

    [Fact]
    public void A_title_of_only_decoration_is_no_tab()
    {
        Assert.Null(PluginTabRules.CleanTitle("🏠"));
        Assert.NotNull(PluginTabRules.Problem(new PluginTab("t", "p", "🏠", "🏠"), false));
    }
}

public class RestartPlanTests
{
    private static bool Exists(string _) => true;

    [Fact]
    public void Same_program_same_arguments_and_the_old_process_to_wait_for()
    {
        var plan = RestartPlan.Build(@"C:\Program Files\CouchHUD\CouchHud.exe", ["--x", "y z"], 1234, Exists)!;
        Assert.Equal(@"C:\Program Files\CouchHUD\CouchHud.exe", plan.FileName);
        Assert.Equal(["--x", "y z"], plan.Arguments);
        Assert.Equal(1234, plan.WaitForPid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("CouchHud.exe")]
    [InlineData(@"C:\x\run.cmd")]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe")]
    public void No_plan_without_a_proper_program(string? path)
    {
        Assert.Null(RestartPlan.Build(path, [], 1, Exists));
    }

    [Fact]
    public void No_plan_when_the_file_is_gone_or_the_pid_is_bad()
    {
        Assert.Null(RestartPlan.Build(@"C:\a\b.exe", [], 1, _ => false));
        Assert.Null(RestartPlan.Build(@"C:\a\b.exe", [], 0, Exists));
    }

    [Theory]
    [InlineData("1234", 1, 1234)]
    [InlineData("1", 1, null)] // itself
    [InlineData("0", 1, null)]
    [InlineData("-5", 1, null)]
    [InlineData("12 34", 1, null)]
    [InlineData("abc", 1, null)]
    [InlineData(null, 1, null)]
    public void Wait_value_is_a_positive_other_pid(string? value, int own, int? expected)
    {
        Assert.Equal(expected, RestartPlan.ParseWait(value, own));
    }
}

public class UpdateCheckScheduleTests
{
    private sealed class Clock
    {
        public DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
    }

    [Fact]
    public void First_check_after_thirty_seconds_then_twelve_hours()
    {
        var s = new UpdateCheckSchedule();
        Assert.Equal(TimeSpan.FromSeconds(30), UpdateCheckSchedule.FirstDelay);
        s.Record(true);
        Assert.Equal(TimeSpan.FromHours(12), s.NextDelay(ran: true));
    }

    [Fact]
    public void A_failure_is_retried_once_then_the_normal_rhythm_returns()
    {
        var s = new UpdateCheckSchedule();
        s.Record(false);
        Assert.Equal(UpdateCheckSchedule.RetryDelay, s.NextDelay(true));
        s.Record(false);
        Assert.Equal(UpdateCheckSchedule.Interval, s.NextDelay(true));
        s.Record(false);
        Assert.Equal(UpdateCheckSchedule.RetryDelay, s.NextDelay(true));
        s.Record(true);
        Assert.Equal(UpdateCheckSchedule.Interval, s.NextDelay(true));
    }

    [Fact]
    public void A_pass_that_did_not_run_looks_again_soon_without_network()
    {
        Assert.Equal(UpdateCheckSchedule.RetryDelay, new UpdateCheckSchedule().NextDelay(ran: false));
    }

    [Fact]
    public void Opening_a_page_checks_only_when_the_last_good_check_is_over_an_hour_old()
    {
        var c = new Clock();
        var s = new UpdateCheckSchedule(() => c.Now);
        Assert.True(s.DueOnOpen()); // never checked
        s.Record(true);
        c.Now += TimeSpan.FromMinutes(59);
        Assert.False(s.DueOnOpen());
        c.Now += TimeSpan.FromMinutes(2);
        Assert.True(s.DueOnOpen());
    }

    [Fact]
    public void A_skipped_pass_counts_as_an_attempt()
    {
        var c = new Clock();
        var s = new UpdateCheckSchedule(() => c.Now);
        s.Attempted();
        Assert.False(s.DueOnOpen());
        c.Now += TimeSpan.FromMinutes(6);
        Assert.True(s.DueOnOpen());
    }

    [Fact]
    public void Opening_a_page_never_hammers_after_a_failure()
    {
        var c = new Clock();
        var s = new UpdateCheckSchedule(() => c.Now);
        s.Record(false);
        Assert.False(s.DueOnOpen());
        c.Now += TimeSpan.FromMinutes(4);
        Assert.False(s.DueOnOpen());
        c.Now += TimeSpan.FromMinutes(2);
        Assert.True(s.DueOnOpen());
    }
}
