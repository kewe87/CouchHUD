using CouchHud.Core.Plugins;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>
/// Changing a plugin setting from the settings page: the same function levels as pages and rows (child profile needs a visit the parents' code
/// opened, the locked couch needs the unlock for admin, restricted is for everyone, nobody is nobody), and the value must fit the declared kind.
/// </summary>
public class PluginSettingRulesTests
{
    private static PluginSetting Toggle(string? function = null) => new() { Key = "on", Label = "On", Kind = SettingKind.Toggle, Function = function };
    private static PluginSetting Choice() => new() { Key = "mode", Label = "Mode", Kind = SettingKind.Choice, Choices = ["a", "b"] };
    private static PluginSetting Number() => new() { Key = "n", Label = "N", Kind = SettingKind.Text, Min = 1, Max = 10 };

    // ---- who may change

    [Fact]
    public void A_child_profile_without_a_visit_is_refused_and_with_a_visit_allowed()
    {
        Assert.Equal("plugin.setting.needVisit", PluginSettingRules.Refusal(PluginLevel.Admin, locked: false, childProfile: true, visitActive: false));
        Assert.Null(PluginSettingRules.Refusal(PluginLevel.Admin, locked: false, childProfile: true, visitActive: true));
    }

    [Fact]
    public void A_visit_ends_after_five_minutes_and_belongs_to_one_profile()
    {
        var now = DateTimeOffset.UnixEpoch;
        var visits = new PluginVisits(() => now);
        visits.Begin("ha", "settings-page", "kid1");
        Assert.True(visits.Active("ha", "settings-page", "kid1"));
        Assert.False(visits.Active("ha", "settings-page", "kid2"));
        Assert.False(visits.Active("other", "settings-page", "kid1"));
        now += TimeSpan.FromMinutes(5);
        Assert.False(visits.Active("ha", "settings-page", "kid1"));
        Assert.Equal("plugin.setting.needVisit", PluginSettingRules.Refusal(PluginLevel.Admin, false, true, visits.Active("ha", "settings-page", "kid1")));
    }

    [Fact]
    public void On_the_locked_couch_admin_means_unlock_first_even_with_a_visit()
    {
        Assert.Equal("lock.unlockFirst", PluginSettingRules.Refusal(PluginLevel.Admin, locked: true, childProfile: false, visitActive: false));
        Assert.Equal("lock.unlockFirst", PluginSettingRules.Refusal(PluginLevel.Admin, locked: true, childProfile: true, visitActive: true));
        Assert.Null(PluginSettingRules.Refusal(PluginLevel.Admin, locked: false, childProfile: false, visitActive: false));
    }

    [Fact]
    public void Restricted_is_allowed_everywhere_and_nobody_nowhere()
    {
        Assert.Null(PluginSettingRules.Refusal(PluginLevel.Restricted, locked: true, childProfile: false, visitActive: false));
        Assert.Null(PluginSettingRules.Refusal(PluginLevel.Restricted, locked: false, childProfile: true, visitActive: false));
        Assert.Equal("plugin.fn.off", PluginSettingRules.Refusal(PluginLevel.Off, locked: false, childProfile: false, visitActive: true));
    }

    [Fact]
    public void A_setting_without_any_function_counts_as_admin_only()
    {
        Assert.Equal("lock.unlockFirst", PluginSettingRules.Refusal(null, true, false, false));
        Assert.Equal("plugin.setting.needVisit", PluginSettingRules.Refusal(null, false, true, false));
        Assert.Null(PluginSettingRules.Refusal(null, false, false, false));
    }

    [Fact]
    public void The_function_is_the_settings_own_tag_else_the_settings_function_else_none()
    {
        Assert.Equal("lighting", PluginSettingRules.FunctionOf(Toggle("lighting"), _ => true));
        Assert.Equal("settings", PluginSettingRules.FunctionOf(Toggle(), id => id == "settings"));
        Assert.Null(PluginSettingRules.FunctionOf(Toggle(), _ => false));
    }

    [Fact]
    public void The_settings_page_visit_covers_an_admin_setting_without_any_function_but_only_for_its_plugin_and_profile()
    {
        var now = DateTimeOffset.UnixEpoch;
        var visits = new PluginVisits(() => now);
        var page = PluginSettingRules.SettingsPageId;
        // No visit yet: the host asks for the parents' code once, which begins the settings visit.
        Assert.Equal(PluginSettingRules.NeedVisit, PluginSettingRules.Refusal(null, false, true, visits.Active("ha", page, "kid1")));
        visits.Begin("ha", page, "kid1");
        Assert.Null(PluginSettingRules.Refusal(null, false, true, visits.Active("ha", page, "kid1")));
        Assert.False(visits.Active("vpn", page, "kid1"));
        Assert.False(visits.Active("ha", page, "kid2"));
        // Locked and nobody stay refused whatever the visit says.
        Assert.Equal("lock.unlockFirst", PluginSettingRules.Refusal(null, true, true, true));
        Assert.Equal("plugin.fn.off", PluginSettingRules.Refusal(PluginLevel.Off, false, true, true));
        visits.End("ha", page);
        Assert.False(visits.Active("ha", page, "kid1"));
        visits.Begin("ha", page, "kid1");
        visits.Clear();
        Assert.False(visits.Active("ha", page, "kid1"));
        visits.Begin("ha", page, "kid1");
        now += PluginVisits.DefaultLifetime;
        Assert.False(visits.Active("ha", page, "kid1"));
    }

    [Fact]
    public void The_settings_page_id_cannot_be_a_plugin_page_id_and_the_prompt_text_exists_in_both_languages()
    {
        Assert.StartsWith("$", PluginSettingRules.SettingsPageId);
        foreach (var language in new[] { "de", "en" })
            Assert.NotEqual("plugin.setting.needCode", CouchHud.Core.Localization.Strings.GetIn(language, "plugin.setting.needCode", "X"));
    }

    [Theory]
    [InlineData("$settings")]
    [InlineData("$x")]
    [InlineData("$")]
    public void A_plugin_page_cannot_take_an_id_starting_with_dollar(string id)
    {
        var dir = Path.Combine(Path.GetTempPath(), "couchhud-resv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var manager = new PluginManager([(Path.Combine(dir, "plugins"), true)], dir, _ => true) { Trust = new PluginTrust(dir, Enumerable.Range(1, 32).Select(i => (byte)i).ToArray()) };
            var ctx = manager.ContextFor("tool", new PluginManager.PluginGrant("tool", PluginOrigin.FirstParty, new HashSet<string> { PluginCapabilities.Pages }), markLoaded: true);
            ctx.RegisterPage(new PluginPage { Id = id, Title = "t", Rows = _ => Task.FromResult<IReadOnlyList<PluginRow>>([]) });
            ctx.RegisterPage(new PluginPage { Id = "ok", Title = "t", Rows = _ => Task.FromResult<IReadOnlyList<PluginRow>>([]) });
            Assert.DoesNotContain(manager.Pages, p => p.Page.Id == id);
            Assert.Null(manager.FindPage("tool", id));
            Assert.NotNull(manager.FindPage("tool", "ok"));
            Assert.True(PluginSettingRules.IsReservedPageId(id));
            Assert.False(PluginSettingRules.IsReservedPageId("ok"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---- the value

    [Fact]
    public void A_toggle_takes_true_or_false_only()
    {
        Assert.Equal("true", PluginSettingRules.Check(Toggle(), "true"));
        Assert.Equal("false", PluginSettingRules.Check(Toggle(), "false"));
        Assert.Null(PluginSettingRules.Check(Toggle(), "yes"));
        Assert.Null(PluginSettingRules.Check(Toggle(), "True"));
        Assert.Null(PluginSettingRules.Check(Toggle(), null));
    }

    [Fact]
    public void A_choice_outside_the_declared_choices_is_refused()
    {
        Assert.Equal("a", PluginSettingRules.Check(Choice(), "a"));
        Assert.Null(PluginSettingRules.Check(Choice(), "c"));
        Assert.Null(PluginSettingRules.Check(Choice(), "A"));
        Assert.Null(PluginSettingRules.Check(Choice(), null));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("11")]
    [InlineData("abc")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("")]
    public void A_number_outside_the_range_or_not_a_number_is_refused(string value)
    {
        Assert.Null(PluginSettingRules.Check(Number(), value));
        Assert.Null(PluginSettingRules.Check(Number(), null));
    }

    [Fact]
    public void A_number_inside_the_range_is_stored_in_invariant_form()
    {
        Assert.Equal("1", PluginSettingRules.Check(Number(), "1"));
        Assert.Equal("10", PluginSettingRules.Check(Number(), "10"));
        Assert.Equal("2.5", PluginSettingRules.Check(Number(), "2.5"));
    }

    [Fact]
    public void Oversize_text_and_secrets_and_control_characters_are_refused()
    {
        var text = new PluginSetting { Key = "t", Label = "T", Kind = SettingKind.Text };
        var secret = new PluginSetting { Key = "s", Label = "S", Kind = SettingKind.Secret };
        Assert.Equal("hello", PluginSettingRules.Check(text, "hello"));
        Assert.Equal(new string('x', PluginSettingRules.MaxText), PluginSettingRules.Check(text, new string('x', PluginSettingRules.MaxText)));
        Assert.Null(PluginSettingRules.Check(text, new string('x', PluginSettingRules.MaxText + 1)));
        Assert.Null(PluginSettingRules.Check(text, "a\nb"));
        Assert.Null(PluginSettingRules.Check(secret, new string('x', PluginSettingRules.MaxSecret + 1)));
        Assert.Equal("tok", PluginSettingRules.Check(secret, "tok"));
        // Clearing is allowed.
        Assert.Equal("", PluginSettingRules.Check(text, null));
        Assert.Equal("", PluginSettingRules.Check(secret, null));
    }

    [Fact]
    public void The_refusal_texts_exist_in_both_languages()
    {
        foreach (var language in new[] { "de", "en" })
        {
            Assert.NotEqual("plugin.setting.needVisit", CouchHud.Core.Localization.Strings.GetIn(language, "plugin.setting.needVisit"));
            Assert.NotEqual("plugin.setting.valueRefused", CouchHud.Core.Localization.Strings.GetIn(language, "plugin.setting.valueRefused", "X"));
        }
    }
}
