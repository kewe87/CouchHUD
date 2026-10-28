using CouchHud.Core.Config;
using CouchHud.Core.Guard;
using CouchHud.Core.Plugins;
using CouchHud.Core.Security;
using System.Text.RegularExpressions;
using CouchHud.Sdk;
using Xunit;

namespace CouchHud.Core.Tests;

/// <summary>
/// 0.6 VM findings: who counts as a child (nobody without parental controls), the refusals asked before any code, the couch code as the
/// parents' code only without parental controls, skins never in a child's profile, and the log throttle.
/// </summary>
public sealed class ChildGateTests
{
    private static PluginPolicy.GateContext Ctx(AppConfig c, bool locked = false, bool expert = true, bool developer = true) =>
        new(PluginCaller.Hud, locked, ProfileSwitch.IsChild(c, c.ActiveProfile.Id), expert, developer, c.ParentalEnabled);

    // --- F1: the default install is not a child ----------------------------------------------------------------------------------

    [Fact]
    public void A_default_install_has_the_default_minutes_and_is_not_a_child()
    {
        var c = new AppConfig();
        // The untouched default rules carry daily minutes (120 / 180): that is a limit, but dormant without parental controls.
        Assert.True(ProfileCheck.HasLimits(c.Profile(ProfileIds.Default).Rules));
        Assert.False(c.ParentalEnabled);
        Assert.False(ProfileSwitch.IsChild(c, ProfileIds.Default));
        Assert.False(ProfileSwitch.IsChild(c, "unknown-id"));
        // So plugins may be changed: the child gate does not refuse an approval, install, enable or uninstall.
        foreach (var change in Enum.GetValues<PluginChange>())
            Assert.NotEqual("plugin.gate.child", PluginPolicy.Decide(Ctx(c), change, PluginOrigin.Local, null).Refused);
    }

    [Fact]
    public void Without_parental_controls_no_profile_is_a_child_even_when_marked_restricted()
    {
        var c = new AppConfig { ParentalEnabled = false };
        var kid = c.AddProfile("Mia", "fox", "#ff8800").Id;
        Assert.True(c.Profile(kid).Restricted);
        Assert.False(ProfileSwitch.IsChild(c, kid));
        Assert.False(ProfileSwitch.IsChild(c, ProfileIds.Default));
        Assert.False(ProfileSwitch.ChildrenGuarded(c));
    }

    [Fact]
    public void With_parental_controls_and_limits_the_default_profile_is_a_child()
    {
        var c = new AppConfig { ParentalEnabled = true };
        Assert.True(ProfileCheck.HasLimits(c.Profile(ProfileIds.Default).Rules));
        Assert.True(ProfileSwitch.IsChild(c, ProfileIds.Default));
        Assert.Equal("plugin.gate.child", PluginPolicy.Decide(Ctx(c), PluginChange.Approve, PluginOrigin.Local, null).Refused);
        Assert.Equal("plugin.gate.child", PluginPolicy.Decide(Ctx(c), PluginChange.Uninstall, PluginOrigin.FirstParty, null).Refused);
    }

    [Fact]
    public void With_parental_controls_the_default_profile_without_limits_is_not_a_child()
    {
        // Existing semantics, kept: the default profile is a child's only while it has a limit (the old install where the child played on it).
        var c = new AppConfig { ParentalEnabled = true };
        var rules = c.Profile(ProfileIds.Default).Rules;
        rules.WeekdayMinutes = 0;
        rules.WeekendMinutes = 0;
        Assert.False(ProfileCheck.HasLimits(rules));
        Assert.False(ProfileSwitch.IsChild(c, ProfileIds.Default));
    }

    [Fact]
    public void With_parental_controls_a_restricted_extra_profile_is_a_child_and_an_adult_profile_is_not()
    {
        var c = new AppConfig { ParentalEnabled = true };
        var kid = c.AddProfile("Mia", "fox", "#ff8800").Id;
        var adult = c.AddProfile("Papa", "lion", "#3fa9f5", restricted: false).Id;
        Assert.True(ProfileSwitch.IsChild(c, kid));
        Assert.False(ProfileSwitch.IsChild(c, adult));
    }

    [Fact]
    public void Switching_a_profile_is_decided_as_before_the_change()
    {
        // ChildrenGuarded already needed parental controls: the profile gates are unchanged by the stricter IsChild.
        var off = new AppConfig { ParentalEnabled = false };
        var adult = off.AddProfile("Papa", "lion", "#3fa9f5", restricted: false).Id;
        Assert.False(ProfileSwitch.IsProtected(off, adult));
        Assert.Equal(SwitchDecision.Free, ProfileSwitch.Decide(off, ProfileIds.Default, adult, false, false, false));
        var on = new AppConfig { ParentalEnabled = true };
        var adult2 = on.AddProfile("Papa", "lion", "#3fa9f5", restricted: false).Id;
        Assert.Equal(SwitchDecision.ParentCode, ProfileSwitch.Decide(on, ProfileIds.Default, adult2, false, false, false));
    }

    // --- F3: refusals come first -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_locked_couch_and_a_child_are_refused_before_any_code_is_asked()
    {
        var c = new AppConfig();
        var locked = PluginPolicy.Decide(Ctx(c, locked: true), PluginChange.Approve, PluginOrigin.Local, null);
        Assert.Equal("lock.unlockFirst", locked.Refused);
        Assert.False(locked.ParentCode);
        var unsigned = PluginPolicy.Decide(Ctx(c, expert: false, developer: false), PluginChange.Approve, PluginOrigin.Local, null);
        Assert.Equal("plugin.gate.unsigned", unsigned.Refused);
        var child = new AppConfig { ParentalEnabled = true };
        Assert.Equal("plugin.gate.child", PluginPolicy.Decide(Ctx(child), PluginChange.Enable, PluginOrigin.FirstParty, null).Refused);
    }

    // --- F2: the couch code as the parents' code ---------------------------------------------------------------------------------

    private static ParentCheckKind Kind(bool parental, string? action, bool serviceParentKnownAbsent = true) => ParentCodeKind.For(parental, action, serviceParentKnownAbsent);

    [Fact]
    public void The_couch_code_acts_as_the_parents_code_only_for_a_listed_huds_own_action_with_parental_off_and_no_service_parents_code()
    {
        foreach (var change in Enum.GetNames<PluginChange>()) Assert.Equal(ParentCheckKind.Couch, Kind(false, "plugin" + change));
        foreach (var action in new[] { "catalogSourceAdd", "catalogSourceRemove", "profileSwitch", "profileManage", "extend", "launch" })
            Assert.Equal(ParentCheckKind.Couch, Kind(false, action));
    }

    [Fact]
    public void Everything_else_stays_the_parents_code()
    {
        // No action, an unknown or misspelled action (the page names it; it may say anything).
        Assert.Equal(ParentCheckKind.Parent, Kind(false, null));
        Assert.Equal(ParentCheckKind.Parent, Kind(false, ""));
        Assert.Equal(ParentCheckKind.Parent, Kind(false, "somethingNew"));
        Assert.Equal(ParentCheckKind.Parent, Kind(false, "PluginApprove"));
        Assert.Equal(ParentCheckKind.Parent, Kind(false, "pluginapprove"));
        Assert.Equal(ParentCheckKind.Parent, Kind(false, "pluginApprove "));
        // Switching parental controls on or off, every code, lock mode and auto sign-in action.
        foreach (var action in new[] { "parentalOn", "parentalOff", "passcode", "lockMode", "lockModeCode", "autologon", "profile", "areas", "ratingSystem", "idleSetting", "browserHistoryOff", "parentalReset", "parentalAdopt" })
            Assert.Equal(ParentCheckKind.Parent, Kind(false, action));
        // The service holds a parents' code (config.json says off: a mismatch, a planted setting), or its state is not known.
        Assert.Equal(ParentCheckKind.Parent, Kind(false, "pluginApprove", serviceParentKnownAbsent: false));
        // Parental controls on: only the parents' code, whatever the action.
        foreach (var action in ParentCodeKind.HudOnlyActions) Assert.Equal(ParentCheckKind.Parent, Kind(true, action));
        Assert.Equal(ParentCheckKind.Parent, Kind(true, null));
    }

    [Fact]
    public void The_allow_list_holds_no_action_that_carries_a_service_operation_or_changes_protection()
    {
        var hud = File.ReadAllText(Path.Combine(RepoRoot(), "src", "CouchHud.Shell", "Hud.cs"));
        var ops = hud[hud.IndexOf("ParentOps(string? action) => action switch", StringComparison.Ordinal)..];
        ops = ops[..ops.IndexOf("};", StringComparison.Ordinal)];
        var carrying = Regex.Matches(ops, "\"(\\w+)\" =>").Select(m => m.Groups[1].Value).ToList();
        Assert.Contains("passcode", carrying);
        Assert.Contains("lockMode", carrying);
        Assert.Contains("autologon", carrying);
        Assert.Contains("parentalOn", carrying);
        foreach (var action in carrying) Assert.DoesNotContain(action, ParentCodeKind.HudOnlyActions);
        foreach (var action in new[] { "parentalOn", "parentalOff", "passcode", "lockMode", "lockModeCode", "autologon" }) Assert.DoesNotContain(action, ParentCodeKind.HudOnlyActions);
    }

    [Fact]
    public void The_call_site_uses_the_allow_list_with_the_known_service_state_and_never_for_service_operations()
    {
        var hud = File.ReadAllText(Path.Combine(RepoRoot(), "src", "CouchHud.Shell", "Hud.cs"));
        Assert.Contains("ops is null && ParentCodeKind.For(config.Current.ParentalEnabled, action, fresh is { Parent: false }) == ParentCheckKind.Couch", hud);
        // The service state is asked for fresh (bounded, a failure or timeout is null = unknown) and never taken from the cached Known value here.
        Assert.Contains("await FreshServiceCodesAsync()", hud);
        var fn = hud[hud.IndexOf("HeldCodes?> FreshServiceCodesAsync()", StringComparison.Ordinal)..];
        Assert.Contains("ServiceCodes.RefreshAsync()", fn);
        Assert.Contains("Task.Delay(TimeSpan.FromSeconds(5))", fn);
        Assert.Contains("catch (Exception)", fn);
        Assert.DoesNotContain("Known is { Parent: false }", hud);
        Assert.DoesNotContain("CouchCodeActsAsParents", hud);
        // The verification of a parents' kind carries the operations, the couch kind none.
        Assert.Contains("VerifyCodeAsync(kind, sequence, kind == CodeKinds.Parent ? ops : null)", hud);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "CouchHud.slnx"))) return dir.FullName;
        throw new InvalidOperationException("repository root not found");
    }

    [Fact]
    public void The_gate_query_names_are_exactly_the_plugin_changes()
    {
        var hud = File.ReadAllText(Path.Combine(RepoRoot(), "src", "CouchHud.Shell", "Hud.cs"));
        Assert.Contains("Enum.IsDefined(ch)", hud);
        Assert.DoesNotContain("throw new ArgumentException(change)", hud);
    }


    // --- Skins: never in a child's profile ---------------------------------------------------------------------------------------

    [Fact]
    public void A_skin_is_not_installed_or_updated_on_the_locked_couch_or_in_a_child_profile()
    {
        var plain = new AppConfig();
        Assert.Null(PluginPolicy.SkinChangeRefusal(Ctx(plain)));
        Assert.Equal("lock.unlockFirst", PluginPolicy.SkinChangeRefusal(Ctx(plain, locked: true)));
        var child = new AppConfig { ParentalEnabled = true };
        Assert.Equal("plugin.gate.child", PluginPolicy.SkinChangeRefusal(Ctx(child)));
        // Not from the remote, MCP, the assistant or a plugin.
        Assert.Equal("plugin.gate.hudOnly", PluginPolicy.SkinChangeRefusal(Ctx(plain) with { Caller = PluginCaller.Remote }));
    }

    // --- Bridge: the gate query is readable while locked, the changes are not ------------------------------------------------------

    [Fact]
    public void The_gate_query_passes_the_locked_bridge_and_the_changes_do_not()
    {
        Assert.Contains("getPluginGate", LockPolicy.OpenWhileLocked);
        Assert.Contains("getPluginGate", FeatureAreas.AlwaysMethods);
        Assert.DoesNotContain("approvePlugin", LockPolicy.OpenWhileLocked);
    }

    // --- Log throttle ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void A_message_that_repeats_every_ten_seconds_is_written_once_and_then_after_the_interval()
    {
        var throttle = new LogThrottle(TimeSpan.FromMinutes(10));
        var t = new DateTime(2026, 10, 28, 10, 0, 0, DateTimeKind.Utc);
        Assert.True(throttle.Allow("NeedGrant", t, out var none));
        Assert.Equal(0, none);
        for (var i = 1; i <= 30; i++) Assert.False(throttle.Allow("NeedGrant", t.AddSeconds(10 * i), out _));
        // Another text passes at once.
        Assert.True(throttle.Allow("Busy", t.AddSeconds(5), out _));
        // After the interval it is written again, with the count it left out.
        Assert.True(throttle.Allow("NeedGrant", t.AddMinutes(11), out var left));
        Assert.Equal(30, left);
    }
}
