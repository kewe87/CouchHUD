using System.Text.Json;
using CouchHud.Core.Config;
using CouchHud.Core.Security;
using CouchHud.Sdk;
using Xunit;

namespace CouchHud.Core.Tests;

/// <summary>Profiles, part 2: the switch as a pure decision, the children's codes, deleting, the limits and the fail-closed paths.</summary>
public sealed class ProfileSwitchTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("couchhud-profiles2-").FullName;

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

    private static readonly string[] Code = ["a", "b", "x", "y", "lb", "rb"];

    /// <summary>A household: the default profile, a grown-up, Mia (child, own code) and Ben (child, no code). Parental controls as given.</summary>
    private static (AppConfig C, string Adult, string Mia, string Ben) Household(bool parental = true)
    {
        var c = new AppConfig();
        c.ParentalEnabled = parental;
        var adult = c.AddProfile("Papa", "lion", "#3fa9f5", restricted: false).Id;
        var mia = c.AddProfile("Mia", "fox", "#ff8800").Id;
        var ben = c.AddProfile("Ben", "frog", "#30a46c").Id;
        Assert.True(c.SetProfileCode(mia, Passcode.Hash(Code)));
        // The parents' own default profile without limits (a default profile with limits counts as a child's, see the old-install tests).
        var rules = c.Profile(ProfileIds.Default).Rules;
        rules.WeekdayMinutes = 0;
        rules.WeekendMinutes = 0;
        return (c, adult, mia, ben);
    }

    private static SwitchDecision Decide(AppConfig c, string from, string to, bool exhausted = false, bool game = false, bool reentry = false) =>
        ProfileSwitch.Decide(c, from, to, exhausted, game, reentry);

    // --- The switch ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void With_only_the_default_profile_nothing_changes()
    {
        var c = new AppConfig { ParentalEnabled = true };
        Assert.False(ProfileSwitch.PickerAtStart(c));
        Assert.False(ProfileSwitch.IsProtected(c, ProfileIds.Default));
        Assert.Equal(SwitchRefusal.Same, Decide(c, ProfileIds.Default, ProfileIds.Default).Refusal);
    }

    [Fact]
    public void The_picker_comes_at_the_start_only_with_more_than_one_profile()
    {
        var c = new AppConfig();
        Assert.False(ProfileSwitch.PickerAtStart(c));
        c.AddProfile("Mia");
        Assert.True(ProfileSwitch.PickerAtStart(c));
    }

    [Fact]
    public void No_switch_while_a_game_runs_whatever_the_target()
    {
        var (c, adult, mia, ben) = Household();
        foreach (var to in new[] { ProfileIds.Default, adult, mia, ben })
        {
            var d = Decide(c, ben, to, game: true);
            Assert.Equal(SwitchNeed.Refused, d.Need);
            Assert.Equal(SwitchRefusal.GameRunning, d.Refusal);
        }
    }

    [Theory]
    [InlineData("nope00000000")]
    [InlineData("../etc")]
    [InlineData("")]
    [InlineData("DEFAULT")]
    public void An_unknown_or_malformed_target_is_refused(string to)
    {
        var (c, _, _, ben) = Household();
        Assert.Equal(SwitchRefusal.Unknown, Decide(c, ben, to).Refusal);
    }

    [Fact]
    public void A_broken_target_is_refused_not_the_default_profile_instead()
    {
        var (c, _, mia, ben) = Household();
        c.Profiles!.Single(p => p.Id == mia).Data!.Rules!.WeekdayMinutes = 99999;
        c.Profiles!.Single(p => p.Id == ben).Data = null;
        Assert.Equal(SwitchRefusal.Unknown, Decide(c, ProfileIds.Default, mia).Refusal);
        Assert.Equal(SwitchRefusal.Unknown, Decide(c, ProfileIds.Default, ben).Refusal);
        Assert.False(c.SwitchActiveProfile(mia));
    }

    [Fact]
    public void Into_a_profile_without_rules_needs_the_parents_once_a_child_exists_and_parental_controls_are_on()
    {
        var (c, adult, mia, ben) = Household();
        Assert.Equal(SwitchNeed.ParentCode, Decide(c, ben, ProfileIds.Default).Need);
        Assert.Equal(SwitchNeed.ParentCode, Decide(c, ben, adult).Need);
        Assert.Equal(SwitchNeed.ParentCode, Decide(c, mia, adult).Need);
        // Between the grown-ups too: whoever stands at the controller might be a child.
        Assert.Equal(SwitchNeed.ParentCode, Decide(c, ProfileIds.Default, adult).Need);
    }

    [Fact]
    public void Without_parental_controls_profiles_without_rules_are_free()
    {
        var (c, adult, _, ben) = Household(parental: false);
        Assert.Equal(SwitchNeed.Free, Decide(c, ben, ProfileIds.Default).Need);
        Assert.Equal(SwitchNeed.Free, Decide(c, ben, adult).Need);
        Assert.False(ProfileSwitch.IsProtected(c, adult));
    }

    [Fact]
    public void An_old_install_child_on_the_default_profile_does_not_reach_a_new_adult_profile()
    {
        // Review H1: before profiles the child played on the default profile (with its limits). The parents add "Papa" as an adult's profile:
        // no child's profile exists, yet the child must not switch into it.
        var c = new AppConfig { ParentalEnabled = true };
        Assert.True(ProfileCheck.HasLimits(c.Profile(ProfileIds.Default).Rules));
        var papa = c.AddProfile("Papa", restricted: false).Id;
        Assert.True(ProfileSwitch.ChildrenGuarded(c));
        Assert.True(ProfileSwitch.IsChild(c, ProfileIds.Default));
        Assert.False(ProfileSwitch.IsChild(c, papa));
        Assert.Equal(SwitchNeed.ParentCode, Decide(c, ProfileIds.Default, papa).Need);
        Assert.True(ProfileSwitch.IsProtected(c, papa));
        // Back to the child's default profile is free, and it is not protected (no picker after the screensaver for it).
        Assert.Equal(SwitchNeed.Free, Decide(c, papa, ProfileIds.Default).Need);
        Assert.False(ProfileSwitch.IsProtected(c, ProfileIds.Default));
        // The adult's profile has no limits at all.
        Assert.False(ProfileCheck.HasLimits(c.Profile(papa).Rules));
    }

    [Fact]
    public void Without_a_child_profile_the_grown_ups_still_need_the_parents_code()
    {
        // Several profiles and parental controls on: every profile that is not a child's is guarded, a child's profile or not.
        var c = new AppConfig { ParentalEnabled = true };
        var rules = c.Profile(ProfileIds.Default).Rules;
        rules.WeekdayMinutes = 0;
        rules.WeekendMinutes = 0;
        var adult = c.AddProfile("Mama", restricted: false).Id;
        Assert.True(ProfileSwitch.ChildrenGuarded(c));
        Assert.Equal(SwitchNeed.ParentCode, Decide(c, ProfileIds.Default, adult).Need);
        Assert.Equal(SwitchNeed.ParentCode, Decide(c, adult, ProfileIds.Default).Need);
        // Without parental controls they are free.
        c.ParentalEnabled = false;
        Assert.Equal(SwitchNeed.Free, Decide(c, ProfileIds.Default, adult).Need);
    }

    [Fact]
    public void A_profile_that_changes_kind_gets_the_matching_limits()
    {
        var (c, adult, _, ben) = Household();
        c.Profile(ProfileIds.Default).Rules.WeekdayMinutes = 60;
        Assert.True(c.SetProfileRestricted(adult, true));
        Assert.Equal(60, c.Profile(adult).Rules.WeekdayMinutes);
        Assert.True(c.SetProfileRestricted(ben, false));
        Assert.False(ProfileCheck.HasLimits(c.Profile(ben).Rules));
    }

    [Fact]
    public void A_childs_code_does_not_survive_a_change_of_kind()
    {
        var (c, _, mia, _) = Household();
        Assert.True(c.Profile(mia).HasCode);
        Assert.True(c.SetProfileRestricted(mia, false));
        Assert.False(c.Profile(mia).HasCode);
        Assert.True(c.SetProfileRestricted(mia, true));
        Assert.False(c.Profile(mia).HasCode);
    }

    [Fact]
    public void Into_a_child_profile_without_a_code_is_free_and_with_one_needs_that_code()
    {
        var (c, adult, mia, ben) = Household();
        Assert.Equal(SwitchNeed.Free, Decide(c, mia, ben).Need);
        Assert.Equal(SwitchNeed.Free, Decide(c, adult, ben).Need);
        Assert.Equal(SwitchNeed.ProfileCode, Decide(c, ben, mia).Need);
        Assert.Equal(SwitchNeed.ProfileCode, Decide(c, ProfileIds.Default, mia).Need);
    }

    [Fact]
    public void A_childs_code_counts_without_parental_controls_too()
    {
        var (c, _, mia, ben) = Household(parental: false);
        Assert.Equal(SwitchNeed.ProfileCode, Decide(c, ben, mia).Need);
        Assert.True(ProfileSwitch.IsProtected(c, mia));
    }

    [Fact]
    public void Out_of_a_profile_whose_time_is_up_only_with_the_parents()
    {
        var (c, adult, mia, ben) = Household();
        // Also into another child's profile (free otherwise) and into one with a code (the child's code is not enough).
        Assert.Equal(SwitchNeed.ParentCode, Decide(c, ben, mia, exhausted: true).Need);
        Assert.Equal(SwitchNeed.ParentCode, Decide(c, mia, ben, exhausted: true).Need);
        Assert.Equal(SwitchNeed.ParentCode, Decide(c, ben, ProfileIds.Default, exhausted: true).Need);
        Assert.Equal(SwitchNeed.ParentCode, Decide(c, ben, adult, exhausted: true).Need);
    }

    [Fact]
    public void Time_that_is_up_means_nothing_without_parental_controls()
    {
        var (c, _, _, ben) = Household(parental: false);
        var other = c.AddProfile("Lea").Id;
        Assert.Equal(SwitchNeed.Free, Decide(c, ben, other, exhausted: true).Need);
    }

    [Fact]
    public void Choosing_the_profile_on_the_couch_again_changes_nothing_unless_it_waits_for_its_code()
    {
        var (c, adult, mia, ben) = Household();
        Assert.Equal(SwitchRefusal.Same, Decide(c, ben, ben).Refusal);
        Assert.Equal(SwitchRefusal.Same, Decide(c, mia, mia).Refusal);
        // Back in the picker after the lock: entering the protected profile again needs its code again.
        Assert.Equal(SwitchNeed.ProfileCode, Decide(c, mia, mia, reentry: true).Need);
        Assert.Equal(SwitchNeed.ParentCode, Decide(c, adult, adult, reentry: true).Need);
        Assert.Equal(SwitchNeed.ParentCode, Decide(c, ProfileIds.Default, ProfileIds.Default, reentry: true).Need);
        Assert.Equal(SwitchNeed.Free, Decide(c, ben, ben, reentry: true).Need);
        // Going back into the same profile with no time left gains nothing: its own code is enough.
        Assert.Equal(SwitchNeed.ProfileCode, Decide(c, mia, mia, exhausted: true, reentry: true).Need);
    }

    [Fact]
    public void Protected_means_a_code_or_no_rules_while_children_are_guarded()
    {
        var (c, adult, mia, ben) = Household();
        Assert.True(ProfileSwitch.IsProtected(c, ProfileIds.Default));
        Assert.True(ProfileSwitch.IsProtected(c, adult));
        Assert.True(ProfileSwitch.IsProtected(c, mia));
        Assert.False(ProfileSwitch.IsProtected(c, ben));
        // An unknown id is the default profile (fail closed): protected as well.
        Assert.True(ProfileSwitch.IsProtected(c, "gone00000000"));
    }

    // --- Codes ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Only_a_childs_profile_has_a_code_and_only_as_a_hash()
    {
        var (c, adult, mia, ben) = Household();
        Assert.False(c.SetProfileCode(adult, Passcode.Hash(Code)));
        Assert.False(c.SetProfileCode(ProfileIds.Default, Passcode.Hash(Code)));
        Assert.False(c.SetProfileCode(ben, "abxylbrb"));
        Assert.False(c.SetProfileCode(ben, Passcode.HeldByService));
        Assert.False(c.Profile(ben).HasCode);
        Assert.True(c.Profile(mia).HasCode);
        Assert.True(Passcode.Verify(Code, c.Profile(mia).CodeHash));
        Assert.False(Passcode.Verify(["a", "a", "a", "a", "a", "a"], c.Profile(mia).CodeHash));
        // In the file: the salted hash, never the buttons.
        var json = JsonSerializer.Serialize(c, ConfigStore.JsonOptions);
        Assert.DoesNotContain("a,b,x,y,lb,rb", json);
        Assert.Contains("pbkdf2-sha256$", json);
        // What the picker gets has no hash.
        Assert.Equal(new ProfileInfo("Mia", "fox", "#ff8800", true, true), c.Profile(mia).Info);
    }

    [Fact]
    public void A_code_can_be_removed_and_goes_with_the_child_flag()
    {
        var (c, _, mia, _) = Household();
        Assert.True(c.SetProfileCode(mia, null));
        Assert.False(c.Profile(mia).HasCode);
        Assert.True(c.SetProfileCode(mia, Passcode.Hash(Code)));
        Assert.True(c.SetProfileRestricted(mia, false));
        Assert.False(c.Profile(mia).HasCode);
        Assert.Null(c.Profiles!.Single(p => p.Id == mia).CodeHash);
    }

    [Fact]
    public void A_code_edited_onto_a_grown_ups_profile_does_not_count()
    {
        var (c, adult, _, _) = Household();
        c.Profiles!.Single(p => p.Id == adult).CodeHash = Passcode.Hash(Code);
        Assert.False(c.Profile(adult).HasCode);
        Assert.Null(c.Profile(adult).CodeHash);
        // Still the parents' to enter.
        Assert.Equal(SwitchNeed.ParentCode, Decide(c, ProfileIds.Default, adult).Need);
    }

    [Fact]
    public void Guessing_a_childs_code_is_slowed_down_and_survives_a_restart()
    {
        var now = DateTimeOffset.UnixEpoch;
        var file = ProfileIds.LockoutFile(_dir, "abc123def456");
        var lockout = new Lockout(() => now, file);
        for (var i = 0; i < 5; i++) lockout.Failed();
        Assert.True(lockout.Wait > TimeSpan.Zero);
        var again = new Lockout(() => now, file);
        Assert.True(again.Wait > TimeSpan.Zero);
        // Each further miss doubles the wait: a 6-button code out of 10 buttons (a million) is out of reach.
        again.Failed();
        Assert.True(again.Wait >= TimeSpan.FromSeconds(60));
    }

    [Theory]
    [InlineData("default")]
    [InlineData("../x")]
    [InlineData("A")]
    public void The_lockout_file_is_only_for_further_profiles(string id) =>
        Assert.Throws<ArgumentException>(() => ProfileIds.LockoutFile(_dir, id));

    // --- Deleting ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_default_and_the_active_profile_cannot_be_deleted()
    {
        var (c, _, mia, ben) = Household();
        Assert.False(c.RemoveProfile(ProfileIds.Default));
        Assert.True(c.SwitchActiveProfile(ben));
        Assert.False(c.RemoveProfile(ben));
        Assert.False(c.RemoveProfile("nope00000000"));
        Assert.True(c.RemoveProfile(mia));
        Assert.True(c.Profile(mia).IsDefault);
        Assert.Equal(3, c.AllProfiles.Count);
    }

    [Fact]
    public void Deleting_the_last_further_profile_leaves_the_file_as_it_was()
    {
        var c = new AppConfig();
        var mia = c.AddProfile("Mia").Id;
        Assert.True(c.RemoveProfile(mia));
        Assert.Null(c.Profiles);
        Assert.False(ProfileSwitch.PickerAtStart(c));
    }

    [Fact]
    public void A_deleted_profiles_play_time_account_is_renamed_not_left_and_not_loaded()
    {
        const string id = "abc123def456";
        var file = ProfileIds.LedgerFile(_dir, id);
        File.WriteAllText(file, "{}");
        var at = new DateTime(2026, 10, 27, 20, 15, 0);
        var moved = ProfileIds.RetireLedger(_dir, id, at);
        Assert.Equal(file + ".deleted-20261027-201500", moved);
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(moved));
        // A second one at the same second does not overwrite the first trace.
        File.WriteAllText(file, "{}");
        Assert.Equal(file + ".deleted-20261027-201500-2", ProfileIds.RetireLedger(_dir, id, at));
        // Nothing there: nothing to do.
        Assert.Null(ProfileIds.RetireLedger(_dir, id, at));
    }

    [Theory]
    [InlineData("default")]
    [InlineData("..")]
    [InlineData("a/b")]
    public void Only_a_further_profiles_account_can_be_retired(string id) =>
        Assert.Throws<ArgumentException>(() => ProfileIds.RetireLedger(_dir, id, DateTime.Now));

    [Fact]
    public void A_deleted_active_profile_in_the_file_falls_back_to_the_default_profile()
    {
        var (c, _, mia, _) = Household();
        Assert.True(c.SwitchActiveProfile(mia));
        var json = JsonSerializer.Serialize(c, ConfigStore.JsonOptions);
        var edited = ConfigStore.Parse(json.Replace($"\"id\": \"{mia}\"", "\"id\": \"zzz000000000\""))!;
        Assert.True(edited.ActiveProfile.IsDefault);
        Assert.Same(edited.Parental, edited.ActiveProfile.Rules);
    }

    // --- Limits --------------------------------------------------------------------------------------------------------------

    [Fact]
    public void At_most_eight_profiles()
    {
        var c = new AppConfig();
        for (var i = 1; i < AppConfig.MaxProfiles; i++) c.AddProfile($"P{i}");
        Assert.Equal(AppConfig.MaxProfiles, c.AllProfiles.Count);
        Assert.Throws<InvalidOperationException>(() => c.AddProfile("one too many"));
    }

    [Theory]
    [InlineData("Mia", "Mia")]
    [InlineData("  Mia  ", "Mia")]
    [InlineData("Mia\nBen", "Mia Ben")]
    [InlineData("Mia\r\n\tBen", "Mia Ben")]
    [InlineData("Mi\u0000a\u0007", "Mia")]
    [InlineData("‮aiM", "aiM")]
    [InlineData("Mia​", "Mia")]
    [InlineData("", null)]
    [InlineData("   \n ", null)]
    [InlineData("\u0001\u0002", null)]
    [InlineData(null, null)]
    [InlineData("abcdefghijklmnopqrstuvwxyz", "abcdefghijklmnopqrstuvwx")]
    public void Names_are_cleaned(string? raw, string? clean) => Assert.Equal(clean, ProfileNames.Clean(raw));

    [Theory]
    [InlineData("Mia", "Mia")]
    [InlineData("  Mia\n Lotta ", "Mia Lotta")]
    [InlineData("abcdefghijklmnopqrstuvwx", "abcdefghijklmnopqrstuvwx")]
    [InlineData("abcdefghijklmnopqrstuvwxy", null)]
    [InlineData("  abcdefghijklmnopqrstuvwx  ", "abcdefghijklmnopqrstuvwx")]
    [InlineData("abcdefghijklmnopqrstuvw\u0000x", "abcdefghijklmnopqrstuvwx")]
    [InlineData(" \n ", null)]
    [InlineData(null, null)]
    public void An_entered_name_is_refused_when_too_long_never_cut(string? raw, string? accepted) =>
        Assert.Equal(accepted, ProfileNames.Accept(raw));

    [Fact]
    public void Renaming_to_25_characters_is_refused_and_the_old_name_stays()
    {
        var (c, _, mia, _) = Household();
        Assert.False(c.RenameProfile(mia, new string('a', 25)));
        Assert.Equal("Mia", c.Profile(mia).DisplayName);
        Assert.True(c.RenameProfile(mia, new string('a', 24)));
    }

    [Fact]
    public void The_picker_has_the_households_look_while_a_protected_profile_waits()
    {
        var (c, papa, _, _) = Household();
        c.Language = "de";
        c.Skin = "greenlight";
        c.SwitchActiveProfile(papa);
        c.ActiveProfile.Language = "en";
        c.ActiveProfile.Skin = "deepsea";

        Assert.True(ProfileSwitch.HeldAtStart(c));
        var look = ProfileSwitch.Look(c, held: true);
        Assert.True(look.IsDefault);
        Assert.Equal("de", look.Language);
        Assert.Equal("greenlight", look.Skin);
        // Chosen: the person's own look.
        Assert.Equal("en", ProfileSwitch.Look(c, held: false).Language);
        Assert.Equal("deepsea", ProfileSwitch.Look(c, held: false).Skin);
    }

    [Fact]
    public void A_long_name_is_never_cut_inside_a_character()
    {
        var name = new string('a', 23) + "😀😀";
        var clean = ProfileNames.Clean(name)!;
        Assert.Equal(new string('a', 23), clean);
        Assert.True(clean.Length <= ProfileNames.MaxLength);
    }

    [Fact]
    public void Rename_and_look_take_only_clean_names_and_presets()
    {
        var (c, _, mia, _) = Household();
        Assert.True(c.RenameProfile(mia, "Mia\nLotta"));
        Assert.Equal("Mia Lotta", c.Profile(mia).DisplayName);
        Assert.False(c.RenameProfile(mia, " \n "));
        Assert.True(c.RenameProfile(ProfileIds.Default, "Papa"));
        Assert.Equal("Papa", c.Profile(ProfileIds.Default).DisplayName);
        Assert.False(c.RenameProfile("nope00000000", "X"));
        Assert.False(c.SetProfileLook(mia, "<img>", "#ff8800"));
        Assert.False(c.SetProfileLook(mia, "fox", "red"));
        Assert.True(c.SetProfileLook(mia, "panda", "#8e4ec6"));
        Assert.Equal("panda", c.Profile(mia).DisplayAvatar);
    }

    [Fact]
    public void Whatever_the_file_says_the_picker_shows_clean_names_and_presets()
    {
        var (c, _, mia, _) = Household();
        var entry = c.Profiles!.Single(p => p.Id == mia);
        entry.Name = "<b>Mia</b>‮" + new string('x', 100);
        entry.Avatar = "javascript:alert(1)";
        entry.Color = "url(x)";
        var view = c.Profile(mia);
        Assert.True(view.DisplayName!.Length <= ProfileNames.MaxLength);
        Assert.DoesNotContain('‮', view.DisplayName);
        Assert.Equal(ProfileLooks.Avatars[0], view.DisplayAvatar);
        Assert.Equal(ProfileLooks.Colors[0], view.DisplayColor);
        Assert.Null(new AppConfig().ActiveProfile.DisplayName);
    }

    [Fact]
    public void The_default_profile_never_is_a_childs()
    {
        var c = new AppConfig { ParentalEnabled = true };
        Assert.False(c.SetProfileRestricted(ProfileIds.Default, true));
        Assert.False(c.ActiveProfile.Restricted);
    }

    [Fact]
    public void Switching_takes_only_real_profiles()
    {
        var (c, _, mia, _) = Household();
        Assert.False(c.SwitchActiveProfile("nope00000000"));
        Assert.False(c.SwitchActiveProfile("../x"));
        Assert.True(c.ActiveProfile.IsDefault);
        Assert.True(c.SwitchActiveProfile(mia));
        Assert.Equal(mia, c.ActiveProfile.Id);
        Assert.True(c.SwitchActiveProfile(ProfileIds.Default));
        Assert.Null(c.ActiveProfileId);
    }

    // --- Contract: what plugins can reach ---------------------------------------------------------------------------------

    [Fact]
    public void A_plugin_has_nothing_to_do_with_profiles()
    {
        foreach (var type in new[] { typeof(IPluginContext), typeof(IPluginEvents), typeof(IPluginSettings) })
            foreach (var member in type.GetMembers())
                Assert.DoesNotContain("profile", member.Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_assistant_and_mcp_have_no_profile_tool_or_setting()
    {
        foreach (var tool in CouchHud.Core.Assistant.ToolCatalog.All)
            Assert.DoesNotContain("profile", tool.Name, StringComparison.OrdinalIgnoreCase);
        foreach (var key in CouchHud.Core.Assistant.ToolCatalog.SettingKeys)
            Assert.DoesNotContain("profile", key, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_bridge_lists_open_only_the_picker_at_the_locked_couch()
    {
        Assert.True(ProfilePolicy.OpenWhileLocked.IsSubsetOf(ProfilePolicy.All));
        foreach (var change in new[] { "addProfile", "renameProfile", "setProfileLook", "setProfileRestricted", "setProfileCode", "removeProfile", "setProfileRule" })
        {
            Assert.Contains(change, ProfilePolicy.All);
            Assert.DoesNotContain(change, ProfilePolicy.OpenWhileLocked);
            Assert.DoesNotContain(change, CouchHud.Core.Guard.LockPolicy.OpenWhileLocked);
        }
    }

    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_protected_profile_goes_back_to_the_picker_after_ten_minutes_without_input()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), ProfileIdleClock.Threshold);
        Assert.False(ProfileIdleClock.Due(T0, T0.AddMinutes(9).AddSeconds(59), gameRunning: false, protectedActive: true));
        Assert.True(ProfileIdleClock.Due(T0, T0.AddMinutes(10), gameRunning: false, protectedActive: true));
        Assert.True(ProfileIdleClock.Due(T0, T0.AddHours(3), gameRunning: false, protectedActive: true));
    }

    [Fact]
    public void The_idle_threshold_never_fires_during_a_game_or_for_an_unprotected_profile()
    {
        Assert.False(ProfileIdleClock.Due(T0, T0.AddHours(3), gameRunning: true, protectedActive: true));
        Assert.False(ProfileIdleClock.Due(T0, T0.AddHours(3), gameRunning: false, protectedActive: false));
        // After the game the time starts again from its end (the host moves the last activity there).
        var ended = T0.AddHours(2);
        Assert.False(ProfileIdleClock.Due(ended, ended.AddMinutes(5), gameRunning: false, protectedActive: true));
        Assert.True(ProfileIdleClock.Due(ended, ended.AddMinutes(10), gameRunning: false, protectedActive: true));
    }

    [Theory]
    [InlineData("screensaverMinutes")]
    [InlineData("displayOffMinutes")]
    public void With_several_profiles_the_way_back_to_the_picker_is_the_parents_setting(string key)
    {
        Assert.True(CouchHud.Core.Guard.LockPolicy.IsHarmlessSetting(key, profilesGuarded: false));
        Assert.False(CouchHud.Core.Guard.LockPolicy.IsHarmlessSetting(key, profilesGuarded: true));
        // Look and sound stay open to a guest either way.
        Assert.True(CouchHud.Core.Guard.LockPolicy.IsHarmlessSetting("screensaverStyle", profilesGuarded: true));
        Assert.False(CouchHud.Core.Guard.LockPolicy.IsHarmlessSetting("integrationMode", profilesGuarded: false));
    }

    [Fact]
    public void The_guard_for_those_settings_follows_parental_controls_and_the_number_of_profiles()
    {
        var c = new AppConfig();
        Assert.False(ProfileSwitch.ChildrenGuarded(c));
        c.ParentalEnabled = true;
        Assert.False(ProfileSwitch.ChildrenGuarded(c));
        c.AddProfile("Papa", ProfileLooks.Avatars[0], ProfileLooks.Colors[0], restricted: false);
        Assert.True(ProfileSwitch.ChildrenGuarded(c));
    }
}
