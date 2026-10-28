using System.Text.RegularExpressions;

namespace CouchHud.Service.Tests;

/// <summary>
/// Code that runs with more rights than the CouchHUD user (or as another account) must not open the user's state through the writing
/// helpers: a <c>ConfigStore</c> restores, cleans up and writes in the user's folder, <c>DurableFile</c>, <c>PlayLedger</c> and
/// <c>Lockout</c> write and swap files there. An administrator doing that in a folder the user controls could be pointed anywhere by a
/// junction or an oplock. Reading goes through <c>ConfigStore.Peek</c>, which only reads.
/// The list, and why:
/// - CouchHud.Service and CouchHud.Privileged: the service runs as LocalSystem, the privileged helpers as an administrator or SYSTEM.
/// - Shell/Services/UninstallCleanup.cs: <c>--uninstall-cleanup</c>, started elevated by the uninstaller.
/// - Shell/Services/LockedDesktop.cs: holds the elevated release of the locked couch policies the uninstaller runs (and the user's Run entry).
/// - Core/Guard: ElevatedGuard and the snapshot it checks are what an administrator reads from the user's folder.
/// Everything else in the shell runs as the user in the user's own folder (HUD, watcher, recovery, sign-in lock, Run entries).
/// </summary>
public sealed class ElevatedContractTests
{
    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CouchHud.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repository root not found");
    }

    private static readonly string[] Folders = ["src/CouchHud.Service", "src/CouchHud.Privileged", "src/CouchHud.Core/Guard"];

    // UninstallCleanup.Run and the files its steps call directly. ServiceRemoval is in a folder above, listed to keep the steps complete.
    // TaskManagerPolicy and SignInLock live in CouchSecurity.cs next to user-side code: see the member tests below.
    private static readonly string[] Files =
    [
        "src/CouchHud.Shell/Services/UninstallCleanup.cs",
        "src/CouchHud.Shell/Services/LockedDesktop.cs",
        "src/CouchHud.Shell/Services/ShellManager.cs",
        "src/CouchHud.Shell/Services/AutoLogon.cs",
        "src/CouchHud.Shell/Services/SteamGuardService.cs",
        "src/CouchHud.Shell/Services/RemoteHost.cs",
        "src/CouchHud.Shell/Services/AdminService.cs",
        "src/CouchHud.Shell/Services/PrivilegedServices.cs",
        "src/CouchHud.Privileged/ServiceRemoval.cs"
    ];

    private static readonly Regex Writer = new(@"new\s+(CouchHud\.Core\.Config\.)?ConfigStore\s*\(|\bDurableFile\s*\.|new\s+(CouchHud\.Core\.Parental\.)?PlayLedger\s*\(|new\s+(CouchHud\.Core\.Security\.)?Lockout\s*\(");

    public static IEnumerable<object[]> Elevated()
    {
        var root = Root();
        var sep = Path.DirectorySeparatorChar;
        var all = new List<string>();
        foreach (var folder in Folders)
        {
            var full = Path.Combine(root, folder);
            Assert.True(Directory.Exists(full), folder);
            all.AddRange(Directory.EnumerateFiles(full, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}"))
                .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/')));
        }
        foreach (var file in Files)
        {
            Assert.True(File.Exists(Path.Combine(root, file)), file);
            all.Add(file);
        }
        return all.Distinct(StringComparer.Ordinal).Select(f => new object[] { f });
    }

    /// <summary>
    /// The text of a member or type from <paramref name="start"/> on: up to the matching brace, or to the semicolon of an expression body.
    /// </summary>
    private static string Member(string code, string start)
    {
        var at = code.IndexOf(start, StringComparison.Ordinal);
        Assert.True(at >= 0, start);
        Assert.Equal(-1, code.IndexOf(start, at + 1, StringComparison.Ordinal));
        var brace = code.IndexOf('{', at);
        var semicolon = code.IndexOf(';', at);
        if (semicolon >= 0 && (brace < 0 || semicolon < brace)) return code[at..(semicolon + 1)];
        var depth = 0;
        for (var i = brace; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return code[at..(i + 1)];
        }
        throw new InvalidOperationException($"no end of {start}");
    }

    private static string CouchSecurity() =>
        File.ReadAllText(Path.Combine(Root(), "src/CouchHud.Shell/Services/CouchSecurity.cs")).Replace("\r\n", "\n");

    // CouchSecurity.cs opens a ConfigStore elsewhere (user side): only the parts the uninstaller calls are checked.
    [Theory]
    [InlineData("public static class TaskManagerPolicy")]
    [InlineData("public static void RemoveEntry()")]
    public void Uninstall_steps_in_CouchSecurity_do_not_use_the_writing_state_helpers(string start)
    {
        var member = Member(CouchSecurity(), start);
        foreach (var line in member.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)))
            Assert.False(Writer.IsMatch(line), $"{start}: {line.Trim()}");
    }

    [Fact]
    public void The_checked_CouchSecurity_members_are_the_ones_the_uninstaller_calls()
    {
        Assert.Contains("public static void RemoveEntry()", Member(CouchSecurity(), "public static class SignInLock"));
        var uninstall = File.ReadAllText(Path.Combine(Root(), "src/CouchHud.Shell/Services/UninstallCleanup.cs"));
        Assert.Contains("SignInLock.RemoveEntry", uninstall);
        Assert.Contains("TaskManagerPolicy.Apply(false)", uninstall);
    }

    [Theory]
    [MemberData(nameof(Elevated))]
    public void Elevated_code_does_not_use_the_writing_state_helpers(string file)
    {
        var lines = File.ReadAllText(Path.Combine(Root(), file)).Replace("\r\n", "\n").Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal));
        foreach (var line in lines) Assert.False(Writer.IsMatch(line), $"{file}: {line.Trim()}");
    }

    [Fact]
    public void The_uninstaller_reads_the_automatic_sign_in_through_peek()
    {
        var code = File.ReadAllText(Path.Combine(Root(), "src/CouchHud.Shell/Services/UninstallCleanup.cs"));
        Assert.Contains("ConfigStore.Peek(dir)", code);
    }
}
