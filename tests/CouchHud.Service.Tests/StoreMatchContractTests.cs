using System.Text.RegularExpressions;

namespace CouchHud.Service.Tests;

/// <summary>
/// Store match and own texts, pinned from the shell's source (as LaunchOverrideContractTests): the match decides the age rating, so only the
/// HUD's own page reaches it (not the phone remote, MCP, the assistant or a plugin), never on a locked couch or for a profile held at the
/// picker, and with parental controls on only with the parents' code for exactly this entry; without that code the stricter rating stays.
/// </summary>
public sealed class StoreMatchContractTests
{
    private static readonly string[] Calls = ["searchStore", "getItemInfo", "setStoreMatch", "setItemInfo"];

    private static readonly string[] Operations = ["SetStoreMatchAsync", "SetStoreMatch", "SetItemInfo", "SearchStoreAsync", "GetItemInfoAsync"];

    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CouchHud.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repository root not found");
    }

    private static string[] Lines(string file) => File.ReadAllText(file).Replace("\r\n", "\n").Split('\n');

    private static bool IsComment(string line) => line.TrimStart().StartsWith("//", StringComparison.Ordinal);

    private static string Code(params string[] path) => string.Join('\n', Lines(Path.Combine([Root(), .. path])).Where(l => !IsComment(l)));

    private static IEnumerable<(string Rel, string Code)> Sources()
    {
        var sep = Path.DirectorySeparatorChar;
        var root = Path.Combine(Root(), "src");
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal))
            .Select(f => (Path.GetRelativePath(root, f), string.Join('\n', Lines(f).Where(l => !IsComment(l)))));
    }

    private static string Body(string code, string declaration)
    {
        var lines = code.Split('\n');
        var start = Array.FindIndex(lines, l => l.StartsWith(declaration, StringComparison.Ordinal));
        Assert.True(start >= 0, $"not found: {declaration}");
        var end = Array.FindIndex(lines, start + 1, l => l == "    }");
        return string.Join('\n', lines[start..end]);
    }

    [Fact]
    public void Only_the_ui_bridge_names_the_calls()
    {
        foreach (var (rel, code) in Sources())
        {
            // FeatureAreas.cs only lists the call under its area (Gaming/Apps) so it is refused while that area is off.
            if (Path.GetFileName(rel) is "UiBridge.cs" or "FeatureAreas.cs") continue;
            foreach (var call in Calls)
                Assert.False(code.Contains($"\"{call}\"", StringComparison.Ordinal), $"{rel} names the bridge call {call}");
        }
        var bridge = Code("src", "CouchHud.Shell", "UiHost", "UiBridge.cs");
        foreach (var call in Calls) Assert.Contains($"case \"{call}\":", bridge);
    }

    [Fact]
    public void The_operations_are_called_only_from_the_ui_bridge_and_the_host()
    {
        var call = new Regex($@"(?<recv>\w+)\s*\.\s*(?<op>{string.Join('|', Operations)})\s*\(");
        foreach (var (rel, code) in Sources())
        {
            var file = Path.GetFileName(rel);
            if (file == "UiBridge.cs") continue;
            foreach (Match m in call.Matches(code))
            {
                // The host hands the checked call on to the library (and its own read after a change).
                if (file == "Hud.cs" && m.Groups["recv"].Value is "library" or "storeInfo") continue;
                Assert.Fail($"{rel} calls {m.Groups["recv"].Value}.{m.Groups["op"].Value}(…)");
            }
        }
    }

    [Fact]
    public void A_new_match_needs_a_chosen_profile_the_unlock_and_the_parents_code_for_this_entry()
    {
        var hud = Code("src", "CouchHud.Shell", "Hud.cs");
        var set = Body(hud, "    public async Task<ItemInfoView> SetStoreMatchAsync(");
        var held = set.IndexOf("RequireProfileChosen();", StringComparison.Ordinal);
        var unlocked = set.IndexOf("RequireUnlocked();", StringComparison.Ordinal);
        var grant = set.IndexOf("RequireParentGrant(\"storeMatch\", id);", StringComparison.Ordinal);
        var change = set.IndexOf("library.SetStoreMatch(", StringComparison.Ordinal);
        Assert.True(held >= 0 && unlocked > held && grant > unlocked && change > grant, "profile, unlock, parents' code, then the change");
        Assert.Contains("if (config.Current.ParentalEnabled)", set);
        // Confirmed only by a real code; otherwise the rating so far is kept as the floor.
        Assert.Contains("confirmed = HasParentCode;", set);
        // The floor is the strictest of every system (a region switch plus a harmless match frees nothing).
        Assert.Contains("previous = AgeRatings.KeptAge(storeInfo.Cached(item), KnownAge(item));", set);
        var info = Body(hud, "    public ItemInfoView SetItemInfo(");
        Assert.Contains("RequireProfileChosen();", info);
        Assert.Contains("RequireUnlocked();", info);
        Assert.Contains("RequireUnlocked();", Body(hud, "    public async Task<IReadOnlyList<StoreCandidate>> SearchStoreAsync("));
        var ui = File.ReadAllText(Path.Combine(Root(), "ui", "components", "item-info.js"));
        Assert.Contains("action: 'storeMatch', item: item.id", ui);
    }

    [Fact]
    public void The_age_check_keeps_the_floor()
    {
        var hud = Code("src", "CouchHud.Shell", "Hud.cs");
        Assert.Contains("private int? CachedAge(LibraryItem item) => CachedRating(item)?.Age;", hud);
        Assert.Contains("private AgeRatings.Rating? CachedRating(LibraryItem item) => CachedRating(item, ActiveRatingSystem);", hud);
        var rating = Body(hud, "    private AgeRatings.Rating? CachedRating(LibraryItem item, string system)");
        Assert.Contains("AgeRatings.Effective(info, system)", rating);
        Assert.Contains("AgeFloor(item) is { } floor && floor > (rating?.Age ?? 0) ? new AgeRatings.Rating(floor, null) : rating", rating);
        // The check gets the verdict over every judged system, each with the floor as KnownAge has it.
        Assert.Contains("CachedRating(item, s)?.Age ?? AgeFloor(item)", Body(hud, "    private int? VerdictAge(LibraryItem item)"));
        Assert.Contains("return VerdictAge(item);", Body(hud, "    private int? AgeOf(LibraryItem item)"));
        Assert.Contains("Parental.CheckLaunch(item, VerdictAge(item), GrantFor(item));", Body(hud, "    public async Task<object> ParentalCheckAsync(string id)"));
        // The automatic match goes by the scanned name, never by a rename.
        var app = Code("src", "CouchHud.Shell", "App.xaml.cs");
        Assert.Contains("storeInfo.Lookup = library.StoreLookupOf;", app);
    }

    [Fact]
    public void The_calls_are_not_open_on_a_locked_couch_or_for_a_held_profile()
    {
        var policy = Code("src", "CouchHud.Core", "Guard", "LockPolicy.cs") + Code("src", "CouchHud.Core", "Config", "ProfileSwitch.cs");
        foreach (var call in Calls) Assert.DoesNotContain($"\"{call}\"", policy);
    }

    [Fact]
    public void Assistant_mcp_remote_and_plugins_have_no_way_to_the_match_or_the_texts()
    {
        var tools = Code("src", "CouchHud.Core", "Assistant", "Tools.cs");
        var edit = tools[tools.IndexOf("Write(\"edit_item\"", StringComparison.Ordinal)..];
        var next = edit.IndexOf("Write(", 5, StringComparison.Ordinal);
        if (next > 0) edit = edit[..next];
        foreach (var field in new[] { "store", "match", "appId", "description", "developers", "publishers", "releaseDate", "genres" })
            Assert.DoesNotContain($"\"{field}\"", edit);
        var hudTools = Code("src", "CouchHud.Shell", "Services", "HudTools.cs");
        foreach (var name in new[] { "StoreMatch", "ItemInfo", "SearchStore" }) Assert.DoesNotContain(name, hudTools);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root(), "src", "CouchHud.Sdk"), "*.cs"))
        {
            var sdk = File.ReadAllText(file);
            Assert.DoesNotContain("StoreMatch", sdk);
            Assert.DoesNotContain("ItemInfo", sdk);
        }
    }
}
