using System.Text.RegularExpressions;

namespace CouchHud.Service.Tests;

/// <summary>
/// The browser's recent sites with parental controls, pinned from the shell's source (as in BrowserUnlockContractTests): only a fresh
/// parents' code for exactly this shows them, the release is set in one place and reset with the lock, a game or the HUD, and
/// parental controls switched; the bridge call is not open at the locked couch.
/// </summary>
public sealed class BrowserRecentContractTests
{
    private static readonly Regex MemberDeclaration = new(@"^    (?:public|private|internal|protected)[^;=]*?\b(\w+)\s*(?:<[^>]*>)?\(");

    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CouchHud.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repository root not found");
    }

    private static string[] Lines(string file) => File.ReadAllText(file).Replace("\r\n", "\n").Split('\n');

    private static IEnumerable<(string Name, string[] Lines)> Sources(params string[] path)
    {
        var sep = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(Path.Combine([Root(), .. path]), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal) && !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal))
            .Select(f => (Path.GetFileName(f), Lines(f)));
    }

    private static bool IsComment(string line) => line.TrimStart().StartsWith("//", StringComparison.Ordinal);

    /// <summary>The member each line matching <paramref name="pattern"/> is in, as "File: Member" (comments left out).</summary>
    private static List<string> Members(string pattern)
    {
        var found = new List<string>();
        var regex = new Regex(pattern);
        foreach (var (name, lines) in Sources("src", "CouchHud.Shell"))
        {
            for (var i = 0; i < lines.Length; i++)
            {
                if (IsComment(lines[i]) || !regex.IsMatch(lines[i])) continue;
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

    /// <summary>The body of a 4-space-indented member of Hud.cs, without comment lines.</summary>
    private static string Body(string declaration)
    {
        var lines = Lines(Path.Combine(Root(), "src", "CouchHud.Shell", "Hud.cs"));
        var start = Array.FindIndex(lines, l => l.StartsWith(declaration, StringComparison.Ordinal));
        Assert.True(start >= 0, $"not found: {declaration}");
        var end = Array.FindIndex(lines, start + 1, l => l == "    }");
        return string.Join('\n', lines[start..end].Where(l => !IsComment(l)));
    }

    [Fact]
    public void Only_a_fresh_parents_code_for_exactly_this_shows_them()
    {
        var body = Body("    public object RevealBrowserRecent()");
        var unlocked = body.IndexOf("RequireUnlocked();", StringComparison.Ordinal);
        var grant = body.IndexOf("RequireParentGrant(BrowserRecentPrivacy.ParentAction);", StringComparison.Ordinal);
        var shown = body.IndexOf("_browserRecentShown = true;", StringComparison.Ordinal);
        Assert.True(unlocked >= 0 && grant > unlocked && shown > grant, "unlock, then the parents' grant, then the release");
        // Without parental controls nothing is released: the method returns before the flag.
        Assert.Contains("if (!config.Current.ParentalEnabled) return BrowserState();", body);
        Assert.True(body.IndexOf("if (!config.Current.ParentalEnabled) return BrowserState();", StringComparison.Ordinal) < shown);
        // The release is set nowhere else.
        Assert.Equal(["Hud.cs: RevealBrowserRecent"], Members(@"\b_browserRecentShown\s*=\s*true\b"));
    }

    [Fact]
    public void The_release_is_reset_with_the_lock_a_game_and_parental_controls()
    {
        var resets = Members(@"\b_browserRecentShown\s*=\s*false\b").Distinct().Order(StringComparer.Ordinal).ToList();
        Assert.Contains("Hud.cs: ForgetUnlock", resets);
        Assert.Contains("Hud.cs: SyncLockState", resets);
        Assert.Contains("Hud.cs: SetParental", resets);
        Assert.Contains("if (Locked) _browserRecentShown = false;", Body("    private void SyncLockState()"));
        Assert.Contains("SyncBrowserRecent();", Body("    private void SyncLockState()"));
    }

    [Fact]
    public void The_bridge_call_is_not_open_at_the_locked_couch()
    {
        var lists = 0;
        foreach (var (name, lines) in Sources("src"))
        {
            var text = string.Join('\n', lines);
            if (!text.Contains("OpenWhileLocked", StringComparison.Ordinal)) continue;
            lists++;
            Assert.False(text.Contains("\"revealBrowserRecent\"", StringComparison.Ordinal) && name != "UiBridge.cs",
                $"revealBrowserRecent in a file with an OpenWhileLocked list: {name}");
        }
        Assert.True(lists >= 2, $"only {lists} files with OpenWhileLocked found");
        // In the bridge it is a plain case, not in any list of calls open while locked.
        var bridge = Lines(Path.Combine(Root(), "src", "CouchHud.Shell", "UiHost", "UiBridge.cs"));
        var uses = bridge.Select((l, i) => (l, i)).Where(x => !IsComment(x.l) && x.l.Contains("\"revealBrowserRecent\"", StringComparison.Ordinal)).ToList();
        Assert.Single(uses);
        Assert.Equal("case \"revealBrowserRecent\":", uses[0].l.Trim());
        var policy = string.Join('\n', Lines(Path.Combine(Root(), "src", "CouchHud.Core", "Guard", "LockPolicy.cs")));
        Assert.DoesNotContain("revealBrowserRecent", policy);
    }
}
