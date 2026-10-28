using System.Text.RegularExpressions;

namespace CouchHud.Service.Tests;

/// <summary>
/// Codes, lock mode, parental controls, shell and the service's state on the stand-in of broken settings (security re-review L-b), pinned
/// from the shell's source (the shell has no test project): the config refuses every change then, so a call that went to the service
/// first would leave service and config apart and report success. Each entry point refuses up front, before any permission is used up
/// and before any service call, with an error the page shows; the safe direction (automatic sign-in off) stays open; and a setup hold
/// from before the fault does not keep the couch open.
/// </summary>
public sealed class ConfigFaultDriftContractTests
{
    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CouchHud.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repository root not found");
    }

    private static string Shell(params string[] path) =>
        File.ReadAllText(Path.Combine([Root(), "src", "CouchHud.Shell", .. path])).Replace("\r\n", "\n");

    private static string Hud() => Shell("Hud.cs");

    /// <summary>The body of the member declared by <paramref name="signature"/>: from it to the next line closing at its indent.</summary>
    private static string Body(string text, string signature, string close = "\n    }\n")
    {
        var start = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} not found");
        var end = text.IndexOf(close, start, StringComparison.Ordinal);
        Assert.True(end > start, $"end of {signature} not found");
        return text[start..end];
    }

    public static TheoryData<string> EntryPoints() =>
    [
        "public async Task<object> SetPasscodeAsync(string[] sequence)",
        "public async Task<object> SetParentCodeAsync(string[] sequence)",
        "public async Task<object> SetCouchLockAsync(string mode, string[]? sequence = null)",
        "public async Task<object> SetParentalAsync(string key, System.Text.Json.JsonElement value)",
        "public object SetParental(string key, System.Text.Json.JsonElement value)",
        "public object SetParentalItemLock(string id, bool locked)",
        "public object ParentalAdoptZone()",
        "public bool SetShell(bool install)",
        "public object SetIntegrationMode(string mode)",
        "public async Task SetupServiceTaskAsync(bool policies = true)",
        "public object SetBlockTaskManager(bool on)",
        "public async Task<object> EnableAutoLogonAsync(string password, bool acceptNoLock)",
        "public object SetFeatureAreas(IReadOnlyList<string> enabled)",
    ];

    [Theory]
    [MemberData(nameof(EntryPoints))]
    public void The_entry_point_refuses_on_the_stand_in_before_anything_else(string signature)
    {
        var body = Body(Hud(), signature);
        // The first statement: before the permission (a parents' grant is not used up for nothing) and before any service call.
        var first = body.Split('\n').Skip(2).First(l => l.Trim().Length > 0).Trim();
        Assert.Equal("RefuseWhileConfigFaulted();", first);
    }

    [Fact]
    public void Automatic_sign_in_can_be_switched_off_on_the_stand_in_but_not_on()
    {
        // "off" is the safe direction and a step of the way back; "allow" (part of switching it on) is refused like the rest.
        var body = Body(Hud(), "public async Task<object> SetAutoLogonAsync(string mode)");
        var allow = body.IndexOf("if (mode == \"allow\")", StringComparison.Ordinal);
        var refuse = body.IndexOf("RefuseWhileConfigFaulted();", StringComparison.Ordinal);
        var turnOff = body.IndexOf("AutoLogon.TurnOffAsync(", StringComparison.Ordinal);
        Assert.True(allow >= 0 && refuse > allow && turnOff > refuse, "the refusal belongs to the allow branch only");
        Assert.Single(Regex.Matches(body, "RefuseWhileConfigFaulted"));
    }

    [Fact]
    public void The_refusal_is_an_error_the_page_shows()
    {
        var refuse = Body(Hud(), "private void RefuseWhileConfigFaulted()");
        var line = refuse.Split('\n').Single(l => l.Contains("config.Fault is not null", StringComparison.Ordinal));
        Assert.Contains("throw new InvalidOperationException(Strings.Get(\"config.notSavedFault\"))", line);
        Assert.DoesNotContain("LockedException", line);
    }

    [Fact]
    public void Every_service_write_of_codes_or_mode_sits_behind_the_refusal()
    {
        var hud = Hud();
        // Every public member that stores, removes or switches codes or the lock mode in the service. The confirmations of a lock of
        // 0.4.0 (at the unlock, which must stay possible) check the fault themselves, see below.
        foreach (Match call in Regex.Matches(hud, @"ServiceCodes\.(StoreAsync|SetModeAsync|RemoveAsync)\("))
        {
            var member = hud.LastIndexOf("\n    public ", call.Index, StringComparison.Ordinal);
            Assert.True(member >= 0);
            var signatureEnd = hud.IndexOf('\n', member + 1);
            var signature = hud[(member + 1)..signatureEnd].Trim();
            var body = Body(hud, signature);
            Assert.True(call.Index < member + body.Length, $"{call.Value} is not inside {signature}");
            Assert.Contains("RefuseWhileConfigFaulted();", body[..(call.Index - member)]);
        }
    }

    [Fact]
    public void The_legacy_lock_confirmations_change_nothing_in_the_service_on_the_stand_in()
    {
        var codes = Shell("Services", "ServiceCodes.cs");
        var confirmations = Regex.Matches(codes, @"public static async Task<\w+> (ConfirmLegacy\w*Async)\(([^)]*)\)");
        Assert.True(confirmations.Count >= 2, "the confirmations of a lock of 0.4.0 were not found");
        foreach (Match confirmation in confirmations)
        {
            // Each takes the config store and returns before the service is asked while the settings are unusable.
            Assert.StartsWith("ConfigStore config", confirmation.Groups[2].Value);
            var body = Body(codes, confirmation.Value);
            var fault = body.IndexOf("if (config.Fault is not null) return", StringComparison.Ordinal);
            var service = body.IndexOf("AdminService.Ops.", StringComparison.Ordinal);
            Assert.True(fault >= 0 && service > fault, $"{confirmation.Groups[1].Value}: the fault check must come before the service call");
        }
        // The HUD hands its config store over at every call.
        var hud = Hud();
        foreach (Match call in Regex.Matches(hud, @"ServiceCodes\.(ConfirmLegacy\w*Async)\(([^)]*)\)"))
            Assert.StartsWith("config", call.Groups[2].Value);
    }

    [Fact]
    public void The_shell_calls_the_service_s_setters_only_on_paths_checked_against_the_stand_in()
    {
        // Every direct call of a setter or remover of the service in the shell, by file. ServiceCodes: its public callers in Hud.cs refuse
        // first, its confirmations check the fault (above). AutoLogon: switched on only behind the refusal, off is the safe direction.
        // RemoteHost: the remote firewall, no code, mode or lock state. A new call fails here until it is looked at against the stand-in.
        var allowed = new Dictionary<string, string[]>
        {
            ["ServiceCodes.cs"] = ["SetLockModeAsync", "RemovePasscodeAsync"],
            ["AutoLogon.cs"] = ["SetAutoLogonAsync"],
            ["RemoteHost.cs"] = ["SetRemoteFirewallAsync", "RemoveLegacyFirewallRulesAsync", "RemoveRemoteFirewallAsync"],
        };
        var dir = Path.Combine(Root(), "src", "CouchHud.Shell");
        var seen = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            var sep = Path.DirectorySeparatorChar;
            if (file.Contains($"{sep}obj{sep}", StringComparison.Ordinal) || file.Contains($"{sep}bin{sep}", StringComparison.Ordinal)) continue;
            var name = Path.GetFileName(file);
            foreach (Match call in Regex.Matches(File.ReadAllText(file), @"\bOps\.((?:Set|Remove)\w*Async)\("))
            {
                seen++;
                Assert.True(allowed.TryGetValue(name, out var ok) && ok.Contains(call.Groups[1].Value),
                    $"{name}: Ops.{call.Groups[1].Value} is not on the list of service writes checked against the stand-in");
            }
        }
        Assert.True(seen > 0, "no service setter found: the pattern no longer fits the shell's calls");
    }

    [Fact]
    public void A_setup_hold_from_before_the_fault_does_not_keep_the_couch_open()
    {
        var unlocked = Hud().Split('\n').Single(l => l.Contains("private bool IsUnlocked =>", StringComparison.Ordinal));
        Assert.Contains("(config.Fault is null && MonoNow() < _setupHoldUntil)", unlocked);
    }

    [Fact]
    public void Choosing_a_service_to_pause_never_reports_what_was_not_saved()
    {
        var body = Body(Hud(), "public bool ToggleCouchService(string name)");
        Assert.Contains("config.UpdateOrThrow(", body);
        Assert.DoesNotMatch(new Regex(@"\bconfig\.Update\("), body);
    }
}
