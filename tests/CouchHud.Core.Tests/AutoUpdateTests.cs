using System.IO.Compression;
using System.Net.Http;
using System.Text;
using CouchHud.Core.Plugins;
using CouchHud.Core.Updates;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>The automatic update of plugins and skins: the capability comparison, the decision, the manifest-against-statement check. No network, no real key.</summary>
public class AutoUpdateTests : IDisposable
{
    private static readonly SemVer App = new(0, 6, 3, "");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-auto-" + Guid.NewGuid().ToString("N"));

    public AutoUpdateTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ---- the pure comparison of capabilities

    [Fact]
    public void Same_capabilities_add_nothing_whatever_the_order()
    {
        Assert.Empty(CapabilityCompare.Added(["library", "notify"], ["notify", "library"]));
        Assert.Empty(CapabilityCompare.Added([], []));
    }

    [Fact]
    public void Fewer_capabilities_add_nothing()
    {
        Assert.Empty(CapabilityCompare.Added(["library", "notify", "pages"], ["notify"]));
        Assert.Empty(CapabilityCompare.Added(["library"], []));
    }

    [Fact]
    public void More_capabilities_are_listed()
    {
        Assert.Equal(["registry"], CapabilityCompare.Added(["library"], ["library", "registry"]));
        Assert.Equal(["library"], CapabilityCompare.Added([], ["library"]));
    }

    [Fact]
    public void A_renamed_capability_is_a_new_one()
    {
        Assert.Equal([PluginCapabilities.NetworkLoopback], CapabilityCompare.Added([PluginCapabilities.NetworkLan], [PluginCapabilities.NetworkLoopback]));
        Assert.Equal(["Library"], CapabilityCompare.Added(["library"], ["Library"]));
        Assert.Equal(["library "], CapabilityCompare.Added(["library"], ["library "]));
    }

    [Fact]
    public void An_unknown_capability_is_always_new_even_if_it_is_installed()
    {
        Assert.Equal(["root"], CapabilityCompare.Added(["root"], ["root"]));
    }

    [Fact]
    public void Internet_is_more_than_lan_and_lan_never_covers_internet()
    {
        Assert.Equal([PluginCapabilities.NetworkInternet], CapabilityCompare.Added([PluginCapabilities.NetworkLan], [PluginCapabilities.NetworkInternet]));
        Assert.Empty(CapabilityCompare.Added([PluginCapabilities.NetworkInternet], [PluginCapabilities.NetworkLan]));
        // Internet does not stand for "this PC only" either.
        Assert.Equal([PluginCapabilities.NetworkLoopback], CapabilityCompare.Added([PluginCapabilities.NetworkInternet], [PluginCapabilities.NetworkLoopback]));
    }

    // ---- the decision

    private static AutoUpdateCandidate Plugin(Func<AutoUpdateCandidate, AutoUpdateCandidate>? change = null)
    {
        var c = new AutoUpdateCandidate(CatalogKind.Plugin, Extra: false, Unsigned: false, OfficialSource: true, PluginOrigin.FirstParty, PluginOrigin.FirstParty,
            "1.0.0", "1.0.0", "1.1.0", MinCouchHud: "0.6.0", Sdk: SdkVersion.Current, App, ["library", "notify"], ["library", "notify"]);
        return change is null ? c : change(c);
    }

    private static AutoUpdateCandidate Skin(Func<AutoUpdateCandidate, AutoUpdateCandidate>? change = null)
    {
        var c = new AutoUpdateCandidate(CatalogKind.Skin, Extra: false, Unsigned: false, OfficialSource: true, null, null, "1.0.0", null, "1.1.0", null, null, App, null, null);
        return change is null ? c : change(c);
    }

    [Fact]
    public void An_official_newer_plugin_with_the_same_capabilities_applies()
    {
        Assert.Equal(AutoUpdateVerdict.Apply, AutoUpdateRules.Decide(Plugin()));
        Assert.Equal(AutoUpdateVerdict.Apply, AutoUpdateRules.Decide(Plugin(c => c with { OfferedCapabilities = ["library"] })));
        Assert.Equal(AutoUpdateVerdict.Apply, AutoUpdateRules.Decide(Plugin(c => c with { MinCouchHud = null })));
    }

    [Fact]
    public void An_official_newer_skin_applies()
    {
        Assert.Equal(AutoUpdateVerdict.Apply, AutoUpdateRules.Decide(Skin()));
    }

    [Fact]
    public void A_plugin_that_adds_a_capability_only_gets_a_notice()
    {
        Assert.Equal(AutoUpdateVerdict.NeedsPermission, AutoUpdateRules.Decide(Plugin(c => c with { OfferedCapabilities = ["library", "notify", "registry"] })));
        Assert.Equal(AutoUpdateVerdict.NeedsPermission, AutoUpdateRules.Decide(Plugin(c => c with { InstalledCapabilities = [PluginCapabilities.NetworkLan], OfferedCapabilities = [PluginCapabilities.NetworkInternet] })));
        // An installed plugin from before the capabilities has the old surface only: anything beyond it is new.
        Assert.Equal(AutoUpdateVerdict.NeedsPermission, AutoUpdateRules.Decide(Plugin(c => c with { InstalledCapabilities = null, OfferedCapabilities = ["library", "tabs"] })));
        Assert.Equal(AutoUpdateVerdict.Apply, AutoUpdateRules.Decide(Plugin(c => c with { InstalledCapabilities = null, OfferedCapabilities = ["library"] })));
    }

    [Fact]
    public void A_catalog_the_user_added_never_updates_by_itself_even_with_a_perfect_statement()
    {
        Assert.Equal(AutoUpdateVerdict.Ask, AutoUpdateRules.Decide(Plugin(c => c with { Extra = true })));
        Assert.Equal(AutoUpdateVerdict.Ask, AutoUpdateRules.Decide(Skin(c => c with { Extra = true })));
        Assert.Equal(AutoUpdateVerdict.Ask, AutoUpdateRules.Decide(Plugin(c => c with { OfficialSource = false })));
        Assert.Equal(AutoUpdateVerdict.Ask, AutoUpdateRules.Decide(Skin(c => c with { OfficialSource = false })));
    }

    [Fact]
    public void A_third_party_plugin_passing_as_official_still_asks()
    {
        // The statement is signed with a developer's own key, whatever the source or the id claims.
        Assert.Equal(AutoUpdateVerdict.Ask, AutoUpdateRules.Decide(Plugin(c => c with { StatementOrigin = PluginOrigin.ThirdParty })));
        Assert.Equal(AutoUpdateVerdict.Ask, AutoUpdateRules.Decide(Plugin(c => c with { StatementOrigin = PluginOrigin.Local })));
        // The installed copy is a developer's: CouchHUD's key taking it over is a key change, never automatic (key rotation).
        Assert.Equal(AutoUpdateVerdict.Ask, AutoUpdateRules.Decide(Plugin(c => c with { InstalledOrigin = PluginOrigin.ThirdParty })));
        Assert.Equal(AutoUpdateVerdict.Ask, AutoUpdateRules.Decide(Plugin(c => c with { InstalledOrigin = PluginOrigin.Local })));
        Assert.Equal(AutoUpdateVerdict.Ask, AutoUpdateRules.Decide(Plugin(c => c with { InstalledOrigin = null })));
    }

    [Fact]
    public void An_unsigned_or_unverifiable_entry_asks()
    {
        Assert.Equal(AutoUpdateVerdict.Ask, AutoUpdateRules.Decide(Plugin(c => c with { StatementOrigin = null, OfferedCapabilities = null })));
        Assert.Equal(AutoUpdateVerdict.Ask, AutoUpdateRules.Decide(Plugin(c => c with { Unsigned = true })));
        Assert.Equal(AutoUpdateVerdict.Ask, AutoUpdateRules.Decide(Skin(c => c with { Unsigned = true })));
        Assert.Equal(AutoUpdateVerdict.Ask, AutoUpdateRules.Decide(Plugin(c => c with { OfferedCapabilities = null })));
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("1.0.0", "0.9.0")]
    [InlineData("1.0.0", "1.0.0-beta")]
    [InlineData("1.0.0", "garbage")]
    [InlineData("1.0.0", "")]
    public void An_older_equal_or_unreadable_version_is_never_installed(string installed, string offered)
    {
        Assert.Equal(AutoUpdateVerdict.Ask, AutoUpdateRules.Decide(Plugin(c => c with { InstalledVersion = installed, OfferedVersion = offered })));
        Assert.Equal(AutoUpdateVerdict.Ask, AutoUpdateRules.Decide(Skin(c => c with { InstalledVersion = installed, OfferedVersion = offered })));
    }

    [Fact]
    public void A_version_below_the_highest_ever_installed_is_a_downgrade_even_if_above_the_installed_one()
    {
        // 2.0.0 was installed once, the folder was rolled back to 1.0.0; 1.5.0 would be newer than 1.0.0 but is still a step back.
        Assert.Equal(AutoUpdateVerdict.Ask, AutoUpdateRules.Decide(Plugin(c => c with { HighestSeen = "2.0.0", OfferedVersion = "1.5.0" })));
        Assert.Equal(AutoUpdateVerdict.Ask, AutoUpdateRules.Decide(Plugin(c => c with { HighestSeen = "1.5.0", OfferedVersion = "1.5.0" })));
        Assert.Equal(AutoUpdateVerdict.Apply, AutoUpdateRules.Decide(Plugin(c => c with { HighestSeen = "1.5.0", OfferedVersion = "1.6.0" })));
        Assert.Equal(AutoUpdateVerdict.Ask, AutoUpdateRules.Decide(Plugin(c => c with { HighestSeen = "bad" })));
    }

    [Fact]
    public void An_entry_this_couchhud_cannot_run_asks()
    {
        Assert.Equal(AutoUpdateVerdict.Ask, AutoUpdateRules.Decide(Plugin(c => c with { MinCouchHud = "9.0.0" })));
        Assert.Equal(AutoUpdateVerdict.Ask, AutoUpdateRules.Decide(Plugin(c => c with { MinCouchHud = "garbage" })));
        Assert.Equal(AutoUpdateVerdict.Ask, AutoUpdateRules.Decide(Plugin(c => c with { Sdk = SdkVersion.Current + 1 })));
        Assert.Equal(AutoUpdateVerdict.Ask, AutoUpdateRules.Decide(Skin(c => c with { MinCouchHud = "9.0.0" })));
        Assert.Equal(AutoUpdateVerdict.Apply, AutoUpdateRules.Decide(Plugin(c => c with { Sdk = SdkVersion.Current })));
    }

    // ---- the package against its statement (a hidden escalation in plugin.json)

    private static PackageStatement Statement(params string[] capabilities) =>
        new("wand", "wand", "1.1.0", new string('a', 64), 4, "0.6.0", capabilities, "key", "MIT", null, null);

    private string Zip(string manifest, string? top = "wand")
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var (name, text) in new[] { ("wand.dll", "MZ"), ("plugin.json", manifest) })
            {
                using var w = new StreamWriter(zip.CreateEntry(top is null ? name : top + "/" + name).Open(), new UTF8Encoding(false));
                w.Write(text);
            }
        }
        return path;
    }

    private static void Check(string zipPath, PackageStatement statement)
    {
        using var stream = File.OpenRead(zipPath);
        AutoUpdateRules.CheckPackageAgainstStatement(stream, statement);
    }

    [Theory]
    [InlineData("""{ "id": "wand", "sdk": 4, "capabilities": ["library"] }""")]
    [InlineData("""{ "id": "wand", "sdk": 4, "capabilities": [] }""")]
    [InlineData("""{ "sdk": 3 }""")]
    [InlineData("{}")]
    public void A_manifest_that_declares_the_same_or_less_passes(string manifest)
    {
        Check(Zip(manifest), Statement("library", "notify"));
        Check(Zip(manifest, top: null), Statement("library", "notify"));
    }

    [Fact]
    public void A_manifest_that_declares_more_than_the_statement_is_refused()
    {
        var zip = Zip("""{ "id": "wand", "sdk": 4, "capabilities": ["library", "registry"] }""");
        var ex = Assert.Throws<CatalogException>(() => Check(zip, Statement("library")));
        Assert.Contains("registry", ex.Message);
        Assert.Throws<CatalogException>(() => Check(Zip("""{ "capabilities": ["network:internet"] }"""), Statement("network:lan")));
        Assert.Throws<CatalogException>(() => Check(Zip("""{ "capabilities": ["Library"] }"""), Statement("library")));
        Assert.Throws<CatalogException>(() => Check(Zip("""{ "capabilities": ["somethingNew"] }"""), Statement("somethingNew")));
    }

    [Fact]
    public void A_manifest_of_another_plugin_or_a_newer_sdk_is_refused()
    {
        Assert.Throws<CatalogException>(() => Check(Zip("""{ "id": "other", "capabilities": [] }"""), Statement("library")));
        Assert.Throws<CatalogException>(() => Check(Zip("""{ "sdk": 99 }"""), Statement("library")));
    }

    [Fact]
    public void An_oversized_manifest_is_refused_and_a_broken_one_declares_nothing()
    {
        var big = "{ \"description\": \"" + new string('x', 70 * 1024) + "\" }";
        Assert.Throws<CatalogException>(() => Check(Zip(big), Statement("library")));
        Check(Zip("{ not json"), Statement("library"));
    }

    // ---- the install: nothing is written when the check stops it

    [Fact]
    public void A_package_the_check_refuses_leaves_nothing_installed_or_staged()
    {
        var zip = Zip("""{ "capabilities": ["registry"] }""");
        var sha = PackageStatements.HashFile(zip);
        var entry = new CatalogEntry("wand", "Wand", "d", null, CatalogKind.Plugin, "1.1.0", null, "https://example.com/wand.zip", sha, null, null, "wand",
            CatalogDefaults.IndexUrl, null);
        var service = new CatalogService(new HttpClient(), _dir, () => [], () => false);

        Assert.Throws<CatalogException>(() => service.Install(entry, zip, null, sha, stream => AutoUpdateRules.CheckPackageAgainstStatement(stream, Statement("library"))));

        Assert.False(Directory.Exists(Path.Combine(_dir, "plugins", "wand")));
        Assert.False(Directory.Exists(PendingPackages.Folder(_dir)) && Directory.GetFiles(PendingPackages.Folder(_dir)).Length > 0);
        Assert.Empty(new CatalogRegistry(_dir).Read());
    }

    [Fact]
    public void A_package_that_passes_the_check_is_staged_for_the_next_start_when_it_is_an_update()
    {
        // The plugin is already there: the update waits in the pending folder and is not active before the next start.
        var old = Path.Combine(_dir, "plugins", "wand");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "wand.dll"), "OLD");
        var zip = Zip("""{ "capabilities": ["library"] }""");
        var sha = PackageStatements.HashFile(zip);
        var entry = new CatalogEntry("wand", "Wand", "d", null, CatalogKind.Plugin, "1.1.0", null, "https://example.com/wand.zip", sha, null, null, "wand",
            CatalogDefaults.IndexUrl, null);
        var outcome = new CatalogService(new HttpClient(), _dir, () => [], () => false).Install(entry, zip, null, sha,
            stream => AutoUpdateRules.CheckPackageAgainstStatement(stream, Statement("library")));

        Assert.True(outcome.RestartNeeded);
        Assert.Equal("OLD", File.ReadAllText(Path.Combine(old, "wand.dll")));
        Assert.True(new CatalogRegistry(_dir).Read().Values.Single().Pending);
        Assert.Single(Directory.GetFiles(PendingPackages.Folder(_dir)));
    }

    // ---- the install gate of the automatic case

    private static PluginInstallRequest Request(CatalogEntry entry, VerifiedStatement? statement, bool isUpdate = true, bool adds = false) => new(entry, statement, isUpdate, adds);

    private static CatalogEntry EntryOf(CatalogKind kind, bool extra = false) =>
        new("wand", "Wand", "d", null, kind, "1.1.0", null, "https://example.com/wand.zip", new string('a', 64), null, null, "wand", CatalogDefaults.IndexUrl, null) { Extra = extra };

    private static VerifiedStatement Verified(PluginOrigin origin, string sha = "a", string version = "1.1.0", params string[] capabilities) =>
        new(Statement(capabilities) with { Sha256 = new string(sha[0], 64), Version = version }, origin, "fp");

    [Fact]
    public void The_automatic_gate_approves_only_the_judged_official_update()
    {
        var judged = Verified(PluginOrigin.FirstParty);
        var entry = EntryOf(CatalogKind.Plugin);
        Assert.True(AutoUpdateRules.Approves(Request(entry, judged), judged));
        // A first install, an added capability, another catalog, a developer's key, another statement than the one that was judged.
        Assert.False(AutoUpdateRules.Approves(Request(entry, judged, isUpdate: false), judged));
        Assert.False(AutoUpdateRules.Approves(Request(entry, judged, adds: true), judged));
        Assert.False(AutoUpdateRules.Approves(Request(EntryOf(CatalogKind.Plugin, extra: true), judged), judged));
        Assert.False(AutoUpdateRules.Approves(Request(entry, Verified(PluginOrigin.ThirdParty)), judged));
        Assert.False(AutoUpdateRules.Approves(Request(entry, Verified(PluginOrigin.FirstParty, sha: "b")), judged));
        Assert.False(AutoUpdateRules.Approves(Request(entry, Verified(PluginOrigin.FirstParty, version: "1.2.0")), judged));
        Assert.False(AutoUpdateRules.Approves(Request(entry, null), judged));
        Assert.False(AutoUpdateRules.Approves(Request(entry, judged), null));
        Assert.True(AutoUpdateRules.Approves(Request(EntryOf(CatalogKind.Skin), null), null));
        Assert.False(AutoUpdateRules.Approves(Request(EntryOf(CatalogKind.Skin, extra: true), null), null));
    }

    // ---- review follow-ups

    [Fact]
    public void An_update_announced_as_available_is_still_told_as_applied_once()
    {
        var ledger = new UpdateAnnouncements(null);
        var key = UpdateAnnouncements.Key("plugin", "wand", "1.1.0");
        Assert.Equal([key], ledger.TakeFresh([key]));
        Assert.Equal([key], ledger.TakeApplied([key]));
        Assert.Empty(ledger.TakeApplied([key]));
    }

    [Fact]
    public void An_update_applied_without_an_earlier_notice_is_told_once_and_never_as_available()
    {
        var ledger = new UpdateAnnouncements(null);
        var key = UpdateAnnouncements.Key("plugin", "wand", "1.1.0");
        Assert.Equal([key], ledger.TakeApplied([key]));
        Assert.Empty(ledger.TakeFresh([key]));
        Assert.Empty(ledger.TakeApplied([key]));
    }

    [Fact]
    public void The_permission_notice_is_told_once_and_takes_the_general_one_away()
    {
        var ledger = new UpdateAnnouncements(null);
        var key = UpdateAnnouncements.Key("plugin", "wand", "1.2.0");
        Assert.Equal([key], ledger.TakePermission([key]));
        Assert.Empty(ledger.TakePermission([key]));
        Assert.Empty(ledger.TakeFresh([key]));
    }

    [Fact]
    public async Task One_failing_package_does_not_lose_the_others()
    {
        var logged = new List<string>();
        var (applied, permission) = await AutoUpdateBatch.RunAsync(["a", "boom", "b", "perm", "skip"], () => true, item => item switch
        {
            "boom" => throw new InvalidCastException("unexpected"),
            "perm" => Task.FromResult(AutoStep.NeedsPermission),
            "skip" => Task.FromResult(AutoStep.Skipped),
            _ => Task.FromResult(AutoStep.Applied)
        }, (item, ex) => logged.Add(item + ":" + ex.Message));
        Assert.Equal(["a", "b"], applied);
        Assert.Equal(["perm"], permission);
        Assert.Equal(["boom:unexpected"], logged);
    }

    [Fact]
    public async Task The_batch_stops_when_the_guard_says_no_and_keeps_what_was_done()
    {
        var ran = 0;
        var (applied, _) = await AutoUpdateBatch.RunAsync(["a", "b", "c"], () => ran < 1, _ => { ran++; return Task.FromResult(AutoStep.Applied); });
        Assert.Equal(["a"], applied);
    }

    [Fact]
    public void A_skin_in_use_is_not_swapped_but_an_idle_one_is()
    {
        Assert.Equal(AutoUpdateVerdict.Ask, AutoUpdateRules.Decide(Skin(c => c with { SkinInUse = true })));
        Assert.Equal(AutoUpdateVerdict.Apply, AutoUpdateRules.Decide(Skin(c => c with { SkinInUse = false })));
    }

    [Fact]
    public void Skin_in_use_looks_at_the_household_and_every_profile_and_at_moved_skins()
    {
        Assert.True(AutoUpdateRules.IsSkinInUse("synth", ["default", "user:synth"]));
        Assert.True(AutoUpdateRules.IsSkinInUse("Synth", ["user:synth"]));
        Assert.True(AutoUpdateRules.IsSkinInUse("deepsea", ["deepsea"]));
        Assert.False(AutoUpdateRules.IsSkinInUse("synth", ["default", "user:other", null]));
        Assert.False(AutoUpdateRules.IsSkinInUse("synth", []));
    }

    [Fact]
    public void Internet_to_lan_applies_and_lan_to_internet_asks_in_the_decision()
    {
        Assert.Equal(AutoUpdateVerdict.Apply, AutoUpdateRules.Decide(Plugin(c => c with { InstalledCapabilities = [PluginCapabilities.NetworkInternet], OfferedCapabilities = [PluginCapabilities.NetworkLan] })));
        Assert.Equal(AutoUpdateVerdict.NeedsPermission, AutoUpdateRules.Decide(Plugin(c => c with { InstalledCapabilities = [PluginCapabilities.NetworkLan], OfferedCapabilities = [PluginCapabilities.NetworkInternet] })));
    }

    [Theory]
    [InlineData("1.0.0", "1.1.0-beta")]
    [InlineData("1.0.0", "2.0.0-rc.1")]
    public void A_pre_release_is_not_taken_from_a_release(string installed, string offered)
    {
        Assert.Equal(AutoUpdateVerdict.Ask, AutoUpdateRules.Decide(Plugin(c => c with { InstalledVersion = installed, OfferedVersion = offered, HighestSeen = installed })));
        Assert.Equal(AutoUpdateVerdict.Ask, AutoUpdateRules.Decide(Skin(c => c with { InstalledVersion = installed, OfferedVersion = offered })));
        Assert.Equal(AutoUpdateVerdict.Apply, AutoUpdateRules.Decide(Plugin(c => c with { InstalledVersion = "1.0.0-beta", OfferedVersion = "1.0.0-rc.1", HighestSeen = "1.0.0-beta" })));
    }

    private CatalogEntry WandEntry(string sha) =>
        new("wand", "Wand", "d", null, CatalogKind.Plugin, "1.1.0", null, "https://example.com/wand.zip", sha, null, null, "wand", CatalogDefaults.IndexUrl, null);

    [Fact]
    public void An_update_only_install_refuses_what_is_not_installed()
    {
        var zip = Zip("""{ "capabilities": ["library"] }""");
        var sha = PackageStatements.HashFile(zip);
        var service = new CatalogService(new HttpClient(), _dir, () => [], () => false);
        Assert.Throws<CatalogException>(() => service.Install(WandEntry(sha), zip, null, sha, null, updateOnly: true));
        Assert.False(Directory.Exists(Path.Combine(_dir, "plugins", "wand")));
        Assert.Empty(new CatalogRegistry(_dir).Read());
    }

    [Fact]
    public void A_plugin_removed_while_its_update_downloaded_is_not_staged()
    {
        var folder = Path.Combine(_dir, "plugins", "wand");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "wand.dll"), "OLD");
        var service = new CatalogService(new HttpClient(), _dir, () => [], () => false);
        service.UninstallPlugin("wand");
        var zip = Zip("{}");
        var sha = PackageStatements.HashFile(zip);
        Assert.Throws<CatalogException>(() => service.Install(WandEntry(sha), zip, null, sha, null, updateOnly: true));
        Assert.Empty(new CatalogRegistry(_dir).Read());
        Assert.False(Directory.Exists(PendingPackages.Folder(_dir)) && Directory.GetFiles(PendingPackages.Folder(_dir)).Length > 0);
    }

    [Fact]
    public void A_zip_with_two_plugin_jsons_or_a_traversing_name_is_refused()
    {
        string Make(params (string Name, string Text)[] files)
        {
            var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".zip");
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var (name, text) in files)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(text);
            }
            return path;
        }
        var good = """{ "capabilities": [] }""";
        Assert.Throws<CatalogException>(() => Check(Make(("plugin.json", good), ("PLUGIN.JSON", """{ "capabilities": ["registry"] }""")), Statement("library")));
        Assert.Throws<CatalogException>(() => Check(Make(("wand/plugin.json", good), ("wand/Plugin.json", good)), Statement("library")));
        Assert.Throws<CatalogException>(() => Check(Make(("plugin.json", good), ("../evil.dll", "MZ")), Statement("library")));
        Assert.Throws<CatalogException>(() => Check(Make(("plugin.json", good), ("a/../../b.dll", "MZ")), Statement("library")));
        Assert.Throws<CatalogException>(() => Check(Make(("plugin.json", good), ("c:/x.dll", "MZ")), Statement("library")));
        Check(Make(("plugin.json", good), ("wand.dll", "MZ")), Statement("library"));
    }
}
