using System.Text.RegularExpressions;

namespace CouchHud.Service.Tests;

/// <summary>
/// Own start target and startup parameters, pinned from the shell's source (as ProfileContractTests): changing what an entry starts is any
/// code in the user's account, so only the HUD's own page reaches it (not the phone remote, MCP, the assistant or a plugin), never on a locked
/// couch or for a profile held at the picker, only in expert mode, and with parental controls on only with the parents' code for exactly this
/// entry; the start checks the target again.
/// </summary>
public sealed class LaunchOverrideContractTests
{
    private static readonly string[] Calls = ["getLaunchOptions", "setLaunchOverride", "editLaunch"];

    private static readonly string[] Operations = ["SetLaunchOverride", "EditLaunch", "GetLaunchOptions"];

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
    public void Only_the_ui_bridge_names_the_launch_calls()
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
    public void The_launch_operations_are_called_only_from_the_ui_bridge_and_the_host()
    {
        var call = new Regex($@"(?<recv>\w+)\s*\.\s*(?<op>{string.Join('|', Operations)})\s*\(");
        foreach (var (rel, code) in Sources())
        {
            var file = Path.GetFileName(rel);
            if (file == "UiBridge.cs") continue;
            foreach (Match m in call.Matches(code))
            {
                // The host hands the checked call on to the library.
                if (file == "Hud.cs" && m.Groups["recv"].Value == "library") continue;
                Assert.Fail($"{rel} calls {m.Groups["recv"].Value}.{m.Groups["op"].Value}(…)");
            }
        }
    }

    [Fact]
    public void A_change_needs_a_chosen_profile_the_unlock_expert_mode_and_the_parents_code_for_this_entry()
    {
        var hud = Code("src", "CouchHud.Shell", "Hud.cs");
        var require = Body(hud, "    private T RequireLaunchChange<T>(string id, Func<T> change)");
        var held = require.IndexOf("RequireProfileChosen();", StringComparison.Ordinal);
        var unlocked = require.IndexOf("RequireUnlocked();", StringComparison.Ordinal);
        Assert.True(held >= 0 && unlocked > held, "profile first, then the unlock");
        Assert.Contains("ExpertMode", require);
        Assert.Contains("var withCode = config.Current.ParentalEnabled && HasParentCode;", require);
        // The grant for exactly this entry is checked before the change and used up only after it passed (a refused change keeps it).
        var fits = require.IndexOf("if (withCode && !_grant.Fits(\"startOptions\", id)) throw", StringComparison.Ordinal);
        var change = require.IndexOf("var result = change();", StringComparison.Ordinal);
        var consume = require.IndexOf("if (withCode) _grant.TryConsume(\"startOptions\", id);", StringComparison.Ordinal);
        Assert.True(unlocked < fits && fits < change && change < consume, "unlock, grant check, change, then the grant used up");
        Assert.Contains("RequireLaunchChange(id, () => library.SetLaunchOverride(id, change));", Body(hud, "    public LaunchOptionsView SetLaunchOverride("));
        Assert.Contains("RequireLaunchChange(id, () =>", Body(hud, "    public LibraryItem EditLaunch("));
        Assert.Contains("RequireUnlocked();", Body(hud, "    public LaunchOptionsView GetLaunchOptions("));
        // The page asks for exactly this grant.
        var ui = File.ReadAllText(Path.Combine(Root(), "ui", "components", "library-tools.js"));
        Assert.Contains("action: 'startOptions', item: item.id", ui);
    }

    [Fact]
    public void The_launch_calls_are_not_open_on_a_locked_couch_or_for_a_held_profile()
    {
        var policy = Code("src", "CouchHud.Core", "Guard", "LockPolicy.cs") + Code("src", "CouchHud.Core", "Config", "ProfileSwitch.cs");
        foreach (var call in Calls) Assert.DoesNotContain($"\"{call}\"", policy);
    }

    [Fact]
    public void The_start_uses_the_override_checked_again_and_keeps_the_entry_as_it_is()
    {
        var hud = Code("src", "CouchHud.Shell", "Hud.cs");
        var launch = Body(hud, "    public LibraryItem Launch(string id, bool byPerson = false)");
        var effective = launch.IndexOf("var start = library.EffectiveLaunch(item);", StringComparison.Ordinal);
        var started = launch.IndexOf("Launcher.Launch(item, start, LaunchOverrides.PinFor(start, own));", StringComparison.Ordinal);
        Assert.True(effective >= 0 && effective < started);
        // The launch grant is used up only after the checked start, never before the override was checked.
        var consumed = launch.IndexOf("if (check.UsesCode) _grant.TryConsume(\"launch\", item.Id);", started, StringComparison.Ordinal);
        Assert.True(consumed > started, "the parents' code is used up by the start, not before it");
        Assert.True(launch.IndexOf("_grant.TryConsume(\"launch\"", StringComparison.Ordinal) > effective, "nothing used up before the override is checked");
        // A batch file nobody approved starts only on an open couch.
        Assert.Contains("if (Locked && LaunchOverrides.IsUnpinnedScript(start, own)) throw new LockedException(", launch);
        // The client and the foreground release go by what is started.
        Assert.Contains("StartClientForLaunch(item, start);", launch);
        Assert.Contains("ForegroundPolicy.FamilyOfLaunch(item.Source, start.Kind, start.Target)", launch);
        // Session, parental check and play time stay on the scanned entry.
        Assert.Contains("_sessions.Launched(item, DateTimeOffset.UtcNow);", launch);
        Assert.Contains("Parental.CheckLaunch(item, AgeOf(item), GrantFor(item));", launch);
        // The launcher holds a batch file and compares its content on that handle.
        var launcher = Code("src", "CouchHud.Shell", "Services", "Launcher.cs");
        Assert.Contains("var hold = plan.Direct ? LaunchOptions.HoldScript(target.Target, pinned) : null;", launcher);
        Assert.True(launcher.IndexOf("LaunchOptions.HoldScript(", StringComparison.Ordinal) < launcher.IndexOf("Process.Start(psi)", StringComparison.Ordinal));
        // The start itself (shell or not, the hidden console of a batch file) is LaunchOptions.StartInfo's, tested in Core.
        Assert.Contains("var psi = LaunchOptions.StartInfo(plan);", launcher);
        Assert.DoesNotContain("new ProcessStartInfo(", launcher);
    }

    [Fact]
    public void Assistant_mcp_and_plugins_have_no_way_to_the_start()
    {
        var tools = Code("src", "CouchHud.Core", "Assistant", "Tools.cs");
        var edit = tools[tools.IndexOf("Write(\"edit_item\"", StringComparison.Ordinal)..];
        var next = edit.IndexOf("Write(", 5, StringComparison.Ordinal);
        if (next > 0) edit = edit[..next];
        foreach (var field in new[] { "target", "args", "cwd", "launch" }) Assert.DoesNotContain($"\"{field}\"", edit);
        var hudTools = Code("src", "CouchHud.Shell", "Services", "HudTools.cs");
        Assert.DoesNotContain("LaunchOverride", hudTools);
        Assert.DoesNotContain("EditLaunch", hudTools);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root(), "src", "CouchHud.Sdk"), "*.cs"))
            Assert.DoesNotContain("LaunchOverride", File.ReadAllText(file));
    }
}
