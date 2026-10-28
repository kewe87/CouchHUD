using CouchHud.Core.Plugins;
using CouchHud.Sdk;

namespace CouchHud.Service.Tests;

/// <summary>
/// Every plugin runs as the same Windows user, so a plugin that may call the user's DPAPI can open whatever CouchHUD or another plugin
/// protected for that user: the per-plugin entropy is no barrier against it. The lint therefore refuses ProtectedData for third-party
/// developers (CouchHUD's own plugins may use it; the host does the encrypting for the others, see SetSecret). The real thing to find is
/// the shell itself (Secrets uses ProtectedData); a build without the shell's DLL next to the sources checks nothing here.
/// </summary>
public sealed class PluginDpapiLintTests
{
    private static string? ShellDll()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CouchHud.slnx"))) dir = Path.GetDirectoryName(dir);
        if (dir is null) return null;
        var bin = Path.Combine(dir, "src", "CouchHud.Shell", "bin");
        return Directory.Exists(bin) ? Directory.GetFiles(bin, "CouchHud.Shell.dll", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null;
    }

    private static IReadOnlyList<LintFinding> Dpapi()
    {
        var dll = ShellDll();
        return dll is null ? [] : PluginLint.Scan(File.ReadAllBytes(dll), "Shell.dll").Where(f => f.Capability == "dpapi").ToList();
    }

    [Fact]
    public void The_lint_finds_the_use_of_the_users_DPAPI()
    {
        if (ShellDll() is null) return;
        Assert.Contains(Dpapi(), f => f.Detail.Contains("ProtectedData", StringComparison.Ordinal));
    }

    [Fact]
    public void A_third_party_that_uses_it_is_refused_whatever_it_declares()
    {
        // "dpapi" is not a capability anyone can declare.
        Assert.DoesNotContain("dpapi", PluginCapabilities.All);
        Assert.NotNull(PluginPolicy.CheckDeclared(PluginOrigin.ThirdParty, ["dpapi"]));
        var found = Dpapi();
        if (ShellDll() is null) return;
        Assert.NotEmpty(found);
        Assert.NotEmpty(PluginLint.Evaluate(found, PluginOrigin.ThirdParty, []));
        Assert.NotEmpty(PluginLint.Evaluate(found, PluginOrigin.ThirdParty, PluginCapabilities.All.ToList()));
    }

    [Fact]
    public void CouchHUDs_own_plugin_may_use_it()
    {
        Assert.Empty(PluginLint.Evaluate(Dpapi(), PluginOrigin.FirstParty, []));
    }
}
