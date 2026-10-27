using System.Text.RegularExpressions;

namespace CouchHud.Service.Tests;

/// <summary>
/// The couch browser's unlock, pinned from the shell's source (the shell has no test project, as in ZReviewBypassTests): only a
/// person's input may extend it, the unlock itself is set only where it was, and every input CouchHUD makes up itself is marked as
/// its own, so it never counts as a person at the PC. Hud is a partial class: every file of the shell is searched, not only Hud.cs.
/// </summary>
public sealed class BrowserUnlockContractTests
{
    private static readonly Regex MemberDeclaration = new(@"^    (?:public|private|internal|protected)[^;=]*?\b(\w+)\s*(?:<[^>]*>)?\(");

    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CouchHud.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repository root not found");
    }

    private static string[] Lines(string file) => File.ReadAllText(file).Replace("\r\n", "\n").Split('\n');

    private static string[] Lines(params string[] path) => Lines(Path.Combine([Root(), .. path]));

    /// <summary>The C# files under <paramref name="path"/> (relative to the repository), without build output.</summary>
    private static IEnumerable<(string Name, string[] Lines)> Sources(params string[] path)
    {
        var sep = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(Path.Combine([Root(), .. path]), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal))
            .Select(f => (Path.GetFileName(f), Lines(f)));
    }

    private static bool IsComment(string line) => line.TrimStart().StartsWith("//", StringComparison.Ordinal);

    /// <summary>The member (4-space indent) each line matching <paramref name="pattern"/> is in, as "File: Member"; field declarations left out.</summary>
    private static List<string> Members(string pattern)
    {
        var found = new List<string>();
        var regex = new Regex(pattern);
        foreach (var (name, lines) in Sources("src", "CouchHud.Shell"))
        {
            for (var i = 0; i < lines.Length; i++)
            {
                if (IsComment(lines[i]) || !regex.IsMatch(lines[i]) || lines[i].TrimStart().StartsWith("private DateTimeOffset ", StringComparison.Ordinal)) continue;
                string? member = null;
                for (var j = i; j >= 0 && member is null; j--)
                {
                    var m = MemberDeclaration.Match(lines[j]);
                    if (m.Success) member = m.Groups[1].Value;
                }
                found.Add($"{name}: {member ?? $"line {i + 1}"}");
            }
        }
        return found;
    }

    [Fact]
    public void Only_a_persons_input_marks_the_browser_as_used()
    {
        string[] allowed = ["Hud.cs: OnPadDown", "Hud.cs: BrowserCommand", "Hud.cs: BrowserTypeAsync", "Hud.cs: BrowserOpenedByPerson"];
        var setters = Members(@"\b_browserInput\s*(?:[-+]?=(?!=)|\?\?=)|\b(?:ref|out)\s+_browserInput\b");
        Assert.Contains("Hud.cs: OnPadDown", setters);
        Assert.Contains("Hud.cs: BrowserOpenedByPerson", setters);
        Assert.All(setters, m => Assert.Contains(m, allowed));
        // Opening counts only from the HUD (bridge, library or menu by a person), never from a plugin's link or MCP.
        string[] openers = ["Hud.cs: OpenBrowserFromHudAsync", "Hud.cs: OpenUrlFromHudAsync", "Hud.cs: OpenEmbeddedEntryAsync", "Hud.cs: BrowserOpenedByPerson"];
        Assert.All(Members(@"\bBrowserOpenedByPerson\b"), m => Assert.Contains(m, openers));
        var text = string.Join('\n', Lines("src", "CouchHud.Shell", "Hud.cs"));
        Assert.Contains("if (byPerson) BrowserOpenedByPerson();", text);
        var plugins = text[text.IndexOf("plugins.UrlOpener =", StringComparison.Ordinal)..];
        plugins = plugins[..plugins.IndexOf("return true;", StringComparison.Ordinal)];
        Assert.Contains("await OpenUrlAsync(", plugins);
        var bridge = string.Join('\n', Lines("src", "CouchHud.Shell", "UiHost", "UiBridge.cs"));
        Assert.Contains("hud.OpenBrowserFromHudAsync(", bridge);
        Assert.Contains("hud.OpenUrlFromHudAsync(", bridge);
        Assert.Contains("hud.Launch(StringArg(0), byPerson: true)", bridge);
        Assert.DoesNotContain("hud.OpenBrowserAsync(", bridge);
        Assert.DoesNotContain("hud.OpenUrlAsync(", bridge);
    }

    [Fact]
    public void The_ways_that_count_as_a_person_are_called_only_by_one()
    {
        // The controller: OnPadDown only from the pad's Down event.
        var padCalls = new List<string>();
        foreach (var (name, lines) in Sources("src"))
        {
            for (var i = 0; i < lines.Length; i++)
            {
                if (IsComment(lines[i]) || !Regex.IsMatch(lines[i], @"\bOnPadDown\b") || Regex.IsMatch(lines[i], @"^    private void OnPadDown\(")) continue;
                var fromPad = lines[i].Contains("pad.Down +=", StringComparison.Ordinal)
                    || lines[Math.Max(0, i - 4)..i].Any(l => l.Contains("pad.Down +=", StringComparison.Ordinal));
                padCalls.Add(fromPad && name == "Hud.cs" ? "pad.Down" : $"{name}:{i + 1}: {lines[i].Trim()}");
            }
        }
        Assert.Equal(["pad.Down"], padCalls);

        // The HUD bar, the HUD keyboard and opening from the HUD: only the HUD's bridge calls them (or uses them as a method group).
        string[] hudOnly = ["BrowserCommand", "BrowserTypeAsync", "OpenBrowserFromHudAsync", "OpenUrlFromHudAsync"];
        foreach (var method in hudOnly)
        {
            var uses = new Regex($@"\b{method}\b");
            var definition = new Regex($@"^    public\b[^=;]*\b{method}\s*\(");
            foreach (var (name, lines) in Sources("src"))
            {
                for (var i = 0; i < lines.Length; i++)
                {
                    if (IsComment(lines[i]) || !uses.IsMatch(lines[i])) continue;
                    var ok = name == "UiBridge.cs" || (name == "Hud.cs" && definition.IsMatch(lines[i]));
                    Assert.True(ok, $"{method} used outside the HUD's bridge: {name}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        // Launch as a person (a website entry then counts as use): only from the bridge, never from MCP, the assistant or a plugin.
        var asPerson = new Regex(@"\bbyPerson\s*:\s*true\b|\bLaunch\s*\([^;]*,\s*true\b");
        foreach (var (name, lines) in Sources("src"))
        {
            for (var i = 0; i < lines.Length; i++)
            {
                if (IsComment(lines[i]) || !asPerson.IsMatch(lines[i])) continue;
                Assert.True(name == "UiBridge.cs", $"Launch as a person outside the HUD's bridge: {name}:{i + 1}: {lines[i].Trim()}");
            }
        }
    }

    [Fact]
    public void The_unlock_is_set_only_where_it_was()
    {
        var setters = Members(@"\b_unlockedUntil\s*(?:[-+]?=(?!=)|\?\?=)|\b(?:ref|out)\s+_unlockedUntil\b");
        setters.Sort(StringComparer.Ordinal);
        Assert.Equal(
            ["Hud.cs: ConfirmWindowsUnlockAsync", "Hud.cs: ForgetUnlock", "Hud.cs: LeavePcModeAsync", "Hud.cs: UnlockAsync", "Hud.cs: UnlockAsync", "Hud.cs: WatchBrowserUnlock"],
            setters);
        var text = string.Join('\n', Lines("src", "CouchHud.Shell", "Hud.cs"));
        Assert.Contains("_unlockedUntil = next.UnlockedUntil;", text);
        Assert.Contains("BrowserUnlock.Next(now, _unlockedUntil, _browserInput, inputNow, _browserWarned)", text);
    }

    [Fact]
    public void Every_made_up_input_is_marked_as_CouchHUDs_own()
    {
        var sources = Sources("src").ToList();
        // Imports of the input functions under a name of their own (EntryPoint): their calls need the mark as well. The one import of
        // SendInput is the wrapper's SendInputNative (KeyboardInput.SendInput marks before and after); every other SendInput goes through it.
        var entryPoint = new Regex(@"EntryPoint\s*=\s*""(keybd_event|mouse_event|SendInput|SetCursorPos)""");
        var declared = new Regex(@"\b(?:extern|partial)\s+[\w.]+\s+(\w+)\s*\(");
        var names = new HashSet<string>(StringComparer.Ordinal) { "keybd_event", "mouse_event", "SendInputNative", "SetCursorPos" };
        foreach (var (name, lines) in sources)
        {
            for (var i = 0; i < lines.Length; i++)
            {
                if (IsComment(lines[i])) continue;
                var ep = entryPoint.Match(lines[i]);
                if (ep.Success)
                {
                    var decl = lines[i..Math.Min(lines.Length, i + 4)].Select(l => declared.Match(l)).FirstOrDefault(m => m.Success);
                    Assert.True(decl is not null, $"import without a declaration: {name}:{i + 1}");
                    var alias = decl!.Groups[1].Value;
                    if (ep.Groups[1].Value == "SendInput")
                        Assert.True(name == "ControllerMouse.cs" && alias == "SendInputNative", $"raw SendInput import in {name}:{i + 1} as {alias}");
                    names.Add(alias);
                }
                // A raw import named SendInput itself would look like the wrapper.
                Assert.False(Regex.IsMatch(lines[i], @"\b(?:extern|partial)\s+\w+\s+SendInput\s*\("), $"raw SendInput import in {name}:{i + 1}");
            }
        }
        var calls = new Regex($@"\b({string.Join('|', names.Select(Regex.Escape))})\s*\(");
        var checkedCalls = 0;
        foreach (var (name, lines) in sources)
        {
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (IsComment(line) || !calls.IsMatch(line) || declared.IsMatch(line)) continue;
                var near = string.Join('\n', lines[Math.Max(0, i - 8)..i]);
                Assert.True(near.Contains("MarkInjected()", StringComparison.Ordinal), $"input without MarkInjected: {name}:{i + 1}: {line.Trim()}");
                checkedCalls++;
            }
        }
        Assert.True(checkedCalls >= 6, $"only {checkedCalls} calls found");
    }
}
