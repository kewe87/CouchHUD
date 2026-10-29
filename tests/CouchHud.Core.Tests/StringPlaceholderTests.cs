using System.Text.RegularExpressions;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Tests;

/// <summary>A text with a placeholder must never be asked for without its arguments (the UI would show a literal "{0}").</summary>
public sealed class StringPlaceholderTests
{
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "CouchHud.slnx"))) return dir.FullName;
        throw new InvalidOperationException("repository root not found");
    }

    private static IEnumerable<(string File, string Text)> Sources() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(f => (f, File.ReadAllText(f)));

    [Fact]
    public void A_literal_key_with_a_placeholder_is_never_asked_without_arguments()
    {
        var bad = new List<string>();
        foreach (var (file, text) in Sources())
            foreach (Match m in Regex.Matches(text, @"Strings\.Get\(\s*""([^""]+)""\s*\)"))
                foreach (var lang in new[] { "de", "en" })
                    if (Regex.IsMatch(Strings.GetIn(lang, m.Groups[1].Value), @"\{\d")) bad.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}");
        Assert.Empty(bad.Distinct());
    }

    [Fact]
    public void The_setup_exit_texts_are_asked_with_their_code()
    {
        // The text of an exit code of the setup carries the code ({0}); asking without it showed "(Code {0})" in the page.
        foreach (var (file, text) in Sources())
            Assert.DoesNotMatch(@"Strings\.Get\(SetupExit\.MessageKey\([^()]*\)\)", text);
    }
}
