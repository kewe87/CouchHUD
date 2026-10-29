using System.Text.RegularExpressions;

namespace CouchHud.Service.Tests;

/// <summary>
/// The console setup on the stand-in of broken settings, pinned from the shell's and the page's source (the shell has no test project):
/// the host refuses the setup's hold with an error the page shows (a LockedException would be taken quietly as "locked", and every step
/// ran anyway), the page asks the host for the fault again before it starts, and it stops when the hold is refused.
/// </summary>
public sealed class ConsoleSetupFaultContractTests
{
    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CouchHud.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repository root not found");
    }

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine([Root(), .. path])).Replace("\r\n", "\n");

    /// <summary>The body of the member declared by <paramref name="signature"/>: from it to the next line closing at its indent.</summary>
    private static string Body(string text, string signature, string close = "\n    }\n")
    {
        var start = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} not found");
        var end = text.IndexOf(close, start, StringComparison.Ordinal);
        Assert.True(end > start, $"end of {signature} not found");
        return text[start..end];
    }

    [Fact]
    public void The_host_refuses_the_setup_hold_on_the_stand_in_with_an_error_that_is_shown()
    {
        var hold = Body(Read("src", "CouchHud.Shell", "Hud.cs"), "public void HoldUnlockForSetup(bool on)");
        var faultLine = hold.Split('\n').Single(l => l.Contains("config.Fault is not null", StringComparison.Ordinal));
        Assert.Contains("throw new InvalidOperationException(", faultLine);
        Assert.DoesNotContain("LockedException", faultLine);
        // The fault check comes before the hold is set.
        Assert.True(hold.IndexOf("config.Fault", StringComparison.Ordinal) < hold.IndexOf("_setupHoldUntil = MonoNow()", StringComparison.Ordinal));
    }

    [Fact]
    public void The_host_tells_the_fault_with_every_security_read()
    {
        var security = Body(Read("src", "CouchHud.Shell", "Hud.cs"), "public object GetSecurity()");
        Assert.Matches(new Regex(@"\n\s*configFault = config\.Fault is not null,"), security);
    }

    [Fact]
    public void The_console_setup_stops_before_its_first_step_without_the_hold()
    {
        var start = Body(Read("ui", "views", "console-setup.js"), "    async start() {", "\n    },\n");
        // The hold goes through the rule that reads the fault fresh and reports a refusal, never through a quiet attempt.
        Assert.DoesNotMatch(new Regex(@"attempt\(\(\) => api\.holdUnlockForSetup\(true\)"), start);
        var begin = start.IndexOf("beginSetupHold(", StringComparison.Ordinal);
        var abort = start.IndexOf("if (!begun.ok)", StringComparison.Ordinal);
        var firstStep = start.IndexOf("await runRows(rows", StringComparison.Ordinal);
        Assert.True(begin >= 0 && abort > begin && firstStep > abort, "the setup must check the hold before its first step");
        Assert.Contains("return;", start[abort..firstStep]);
        Assert.Contains("readSecurity: () => api.getSecurity()", start);
    }
}
