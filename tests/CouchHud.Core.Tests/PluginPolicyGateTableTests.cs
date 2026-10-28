using CouchHud.Core.Plugins;

namespace CouchHud.Core.Tests;

/// <summary>
/// The table of "does this plugin change need the parents' code" that the page's <c>needsCode</c> (ui/core/plugin-rules.js) mirrors. The
/// same rows are in scripts/ui-logic-check.mjs: when PluginPolicy.Decide changes, both tables change or one of the two tests fails.
/// Also pins that what the host hands the page about plugins holds no folder paths and no full keys.
/// </summary>
public class PluginPolicyGateTableTests
{
    // change, origin, capabilities, parental controls on, update adds capabilities, code needed
    public static IEnumerable<object[]> Rows() =>
    [
        [PluginChange.Install, PluginOrigin.ThirdParty, Array.Empty<string>(), false, false, true],
        [PluginChange.Install, PluginOrigin.ThirdParty, Array.Empty<string>(), true, false, true],
        [PluginChange.Update, PluginOrigin.ThirdParty, Array.Empty<string>(), false, false, false],
        [PluginChange.Update, PluginOrigin.ThirdParty, Array.Empty<string>(), true, false, true],
        [PluginChange.Update, PluginOrigin.ThirdParty, Array.Empty<string>(), false, true, true],
        [PluginChange.Enable, PluginOrigin.ThirdParty, Array.Empty<string>(), false, false, false],
        [PluginChange.Enable, PluginOrigin.ThirdParty, Array.Empty<string>(), true, false, true],
        [PluginChange.Uninstall, PluginOrigin.Local, Array.Empty<string>(), false, false, false],
        [PluginChange.Uninstall, PluginOrigin.Local, Array.Empty<string>(), true, false, true],
        [PluginChange.Approve, PluginOrigin.Local, Array.Empty<string>(), false, false, true],
        [PluginChange.AcceptRevoked, PluginOrigin.FirstParty, Array.Empty<string>(), false, false, true],
        [PluginChange.ConfirmExe, PluginOrigin.FirstParty, Array.Empty<string>(), false, false, true],
        [PluginChange.Install, PluginOrigin.FirstParty, new[] { "pages" }, true, false, false],
        [PluginChange.Install, PluginOrigin.FirstParty, new[] { "assistant" }, true, false, true],
        [PluginChange.Install, PluginOrigin.FirstParty, new[] { "assistant" }, false, false, false],
        [PluginChange.Update, PluginOrigin.FirstParty, new[] { "process" }, true, false, true],
        [PluginChange.Update, PluginOrigin.FirstParty, new[] { "assistant" }, true, true, true],
        [PluginChange.Update, PluginOrigin.FirstParty, new[] { "pages" }, true, true, false],
        [PluginChange.Enable, PluginOrigin.FirstParty, new[] { "process" }, true, false, false]
    ];

    [Theory]
    [MemberData(nameof(Rows))]
    public void The_parents_code_table_is_the_one_the_page_mirrors(PluginChange change, PluginOrigin origin, string[] caps, bool parental, bool adds, bool code)
    {
        var ctx = new PluginPolicy.GateContext(PluginCaller.Hud, Locked: false, ChildProfile: false, ExpertMode: true, DeveloperSwitch: true, ParentalEnabled: parental);
        var decision = PluginPolicy.Decide(ctx, change, origin, caps, adds);
        Assert.Null(decision.Refused);
        Assert.Equal(code, decision.ParentCode);
    }

    [Fact]
    public void An_update_that_adds_permissions_keeps_the_update_grant_name_but_is_gated_like_an_install()
    {
        var ctx = new PluginPolicy.GateContext(PluginCaller.Hud, false, false, ExpertMode: false, DeveloperSwitch: false, ParentalEnabled: false);
        // Outside expert mode a third-party install is refused; an update that adds permissions is too, and its grant is still "pluginUpdate".
        var plain = PluginPolicy.Decide(ctx, PluginChange.Update, PluginOrigin.ThirdParty, [], false);
        Assert.Null(plain.Refused);
        var adds = PluginPolicy.Decide(ctx, PluginChange.Update, PluginOrigin.ThirdParty, [], true);
        Assert.Equal("plugin.gate.expert", adds.Refused);
        Assert.Equal("pluginUpdate", adds.ParentAction);
    }

    [Fact]
    public void The_short_key_code_is_not_the_key()
    {
        var full = new string('a', 32) + new string('b', 32);
        var code = PackageStatements.ShortFingerprint(full);
        Assert.Equal(19, code.Length);
        Assert.DoesNotContain(full, code);
    }

    [Fact]
    public void What_the_host_hands_the_page_about_plugins_and_catalog_rows_holds_no_paths_and_no_full_keys()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "CouchHud.slnx"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        var hud = File.ReadAllText(Path.Combine(root!, "src", "CouchHud.Shell", "Hud.cs"));
        var start = hud.IndexOf("public object GetPlugins()", StringComparison.Ordinal);
        Assert.True(start > 0);
        var body = hud[start..hud.IndexOf("/// <summary>", start + 20, StringComparison.Ordinal)];
        foreach (var forbidden in new[] { "Path", "Directory", "Dir", "KeyFingerprint", "PublisherKey", "PluginDir" })
            Assert.DoesNotContain(forbidden, body.Replace("PackageStatements.ShortFingerprint(fp)", "").Replace("p.KeyFingerprint is { Length: > 0 } fp", ""));
        // The only key-derived value is the short code.
        Assert.Contains("ShortFingerprint", body);

        var rowSource = File.ReadAllText(Path.Combine(root!, "src", "CouchHud.Shell", "Services", "CatalogManager.cs"));
        var rowEnd = rowSource.IndexOf("public sealed record CatalogDetail", StringComparison.Ordinal);
        var rowRecord = rowSource[..rowEnd];
        foreach (var forbidden in new[] { "Path", "Directory", "KeyFingerprint", "PublisherKey", "Pem" })
            Assert.DoesNotContain(forbidden, rowRecord.Replace("verified key's", ""));
    }
}
