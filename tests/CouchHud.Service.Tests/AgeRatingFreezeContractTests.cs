namespace CouchHud.Service.Tests;

/// <summary>
/// The rating system switch and the anti-cheat rule for freezing a game, pinned from the shell's source: a child must not switch to a milder
/// system (refused while config.json is broken and on a locked couch, with parental controls and a parents' code only with that code, which
/// the saved change alone uses up), the switch converts every limit the strict way, and no freeze (menu or parents) skips the anti-cheat check.
/// </summary>
public sealed class AgeRatingFreezeContractTests
{
    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CouchHud.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repository root not found");
    }

    private static string Code(params string[] path) =>
        string.Join('\n', File.ReadAllText(Path.Combine([Root(), .. path])).Replace("\r\n", "\n").Split('\n')
            .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string Body(string code, string declaration)
    {
        var lines = code.Split('\n');
        var start = Array.FindIndex(lines, l => l.StartsWith(declaration, StringComparison.Ordinal));
        Assert.True(start >= 0, $"not found: {declaration}");
        var end = Array.FindIndex(lines, start + 1, l => l == "    }");
        return string.Join('\n', lines[start..end]);
    }

    private static void InOrder(string body, params string[] parts)
    {
        var at = -1;
        foreach (var part in parts)
        {
            var next = body.IndexOf(part, at + 1, StringComparison.Ordinal);
            Assert.True(next > at, $"'{part}' missing or out of order");
            at = next;
        }
    }

    private static string Hud => Code("src", "CouchHud.Shell", "Hud.cs");

    [Fact]
    public void The_system_switch_comes_after_the_fault_check_and_before_any_other_rule()
    {
        var body = Body(Hud, "    public object SetParental(string key, System.Text.Json.JsonElement value)");
        InOrder(body, "RefuseWhileConfigFaulted();", "if (key == \"ratingSystem\") return SetRatingSystem(value);", "RequireParentGrant(");
    }

    [Fact]
    public void The_system_switch_needs_an_unlocked_couch_and_the_parents_code_used_up_only_by_the_saved_change()
    {
        var body = Body(Hud, "    private object SetRatingSystem(System.Text.Json.JsonElement value)");
        InOrder(body,
            "AgeRatings.Choices.Contains(choice)",
            "RequireUnlocked();",
            "var withCode = config.Current.ParentalEnabled && HasParentCode;",
            "if (withCode && !_grant.Fits(\"ratingSystem\")) throw",
            "config.UpdateOrThrow(",
            "c.AgeRatingSystem = choice;",
            "AgeRatings.ConvertLimit(profile.Rules.MaxAge, target)",
            "if (withCode) _grant.TryConsume(\"ratingSystem\");");
        Assert.DoesNotContain("config.Update(", body);
        // The UI asks for the code for exactly this action.
        var ui = File.ReadAllText(Path.Combine(Root(), "ui", "views", "settings.js"));
        Assert.Contains("action: 'ratingSystem'", ui);
    }

    [Fact]
    public void Every_freeze_goes_through_the_anti_cheat_check()
    {
        var hud = Hud;
        Assert.DoesNotContain("VerifiedPids(", hud);
        Assert.Contains("GamePause.Pause(FreezablePids([running]));", hud);
        Assert.Contains("FreezablePids((_sessions.Current is { } c ? [c] : Array.Empty<GameSession>()).Concat(_sessions.Others));", hud);
        var body = Body(hud, "    private IReadOnlyList<int> FreezablePids(IEnumerable<GameSession> sessions)");
        InOrder(body, "_freezeGuard.Check(", "if (verdict.MayFreeze)", "pids.AddRange(", "_freezeGuard.FirstNotice(", "Emit(\"toast\"");
        // GamePause is only ever handed pids from that check.
        foreach (var line in hud.Split('\n').Where(l => l.Contains("GamePause.Pause(", StringComparison.Ordinal)))
            Assert.True(line.Contains("FreezablePids(", StringComparison.Ordinal) || line.Contains("ParentalPids()", StringComparison.Ordinal), line);
    }

    [Fact]
    public void Freezing_in_the_menu_stays_off_by_default_and_expert_only()
    {
        var config = Code("src", "CouchHud.Core", "Config", "AppConfig.cs");
        Assert.Contains("public bool PauseGameInMenu { get; set; }", config);
        Assert.DoesNotContain("PauseGameInMenu = true", Code("src", "CouchHud.Core", "Config", "Profiles.cs"));
        var settings = File.ReadAllText(Path.Combine(Root(), "ui", "views", "settings.js"));
        var category = settings.IndexOf("name: t('settings.cat.controller')", StringComparison.Ordinal);
        var row = settings.IndexOf("toggle('pauseGameInMenu'", StringComparison.Ordinal);
        var end = settings.IndexOf("info(t('settings.controller.keyboard')", StringComparison.Ordinal);
        Assert.True(category > 0 && row > category && end > row);
        Assert.Contains("...expert(", settings[category..row]);
        // The expert block closes only after the row.
        Assert.Contains("\n        ),", settings[row..end].Replace("\r\n", "\n"));
    }

    [Fact]
    public void Every_launch_check_judges_by_every_judged_system_and_saves_the_resolved_one()
    {
        var hud = Hud;
        // The checks get the verdict age (both systems after a region change, the ESRB's top step), never the plain shown age.
        foreach (var line in hud.Split('\n').Where(l => l.Contains("Parental.CheckLaunch(", StringComparison.Ordinal)))
            Assert.True(line.Contains("VerdictAge(", StringComparison.Ordinal) || line.Contains("AgeOf(", StringComparison.Ordinal) || line.Contains("task.Result", StringComparison.Ordinal), line);
        var ageOf = Body(hud, "    private int? AgeOf(LibraryItem item)");
        Assert.DoesNotContain("return cached;", ageOf);
        Assert.DoesNotContain("return KnownAge(item);", ageOf);
        var verdict = Body(hud, "    private int? VerdictAge(LibraryItem item)");
        InOrder(verdict, "JudgedRatingSystems.Select(", "AgeRatings.VerdictAge(views, p.AgeLimit, p.BlockUnknownAge)");
        Assert.Contains("AgeRatings.JudgedSystems(config.Current.AgeRatingSystem, ActiveRatingSystem, config.Current.AgeRatingSystemResolved)", hud);
        // The parents' save of the system or a limit records what it resolved to.
        InOrder(Body(hud, "    private object SetRatingSystem(System.Text.Json.JsonElement value)"), "c.AgeRatingSystem = choice;", "c.AgeRatingSystemResolved = target;");
        // A limit keeps the system recorded before (audit 0.5 L2): only the first one records it.
        Assert.Contains("Rule(r => r.MaxAge = n, n > 0 ? c => c.AgeRatingSystemResolved = AgeRatings.SavedSystemAfterLimit(c.AgeRatingSystemResolved, system) : null);", hud);
        // The hint goes by every profile's limit, and names the saved system as JudgedSystems read it.
        Assert.Contains("anyAgeLimit = config.Current.AllProfiles.Any(pr => pr.Rules.MaxAge > 0),", hud);
        Assert.Contains("ratingSystemSaved = JudgedRatingSystems is [_, var saved] ? saved : null,", hud);
        Assert.Contains("p.ratingSystemSaved && (p.anyAgeLimit ?? p.maxAge)", File.ReadAllText(Path.Combine(Root(), "ui", "views", "settings.js")));
        // The message names the limit as it counts.
        Assert.Contains("AgeRatings.LimitFor(cfg.AgeLimit, system)", Code("src", "CouchHud.Shell", "Services", "ParentalController.cs"));
    }

    [Fact]
    public void A_second_game_forgets_its_scan_and_a_running_scan_keeps_the_real_notice()
    {
        var hud = Hud;
        Assert.Contains("_sessions.OtherEnded += s => _freezeGuard.Forget(s.Item);", hud);
        var body = Body(hud, "    private IReadOnlyList<int> FreezablePids(IEnumerable<GameSession> sessions)");
        InOrder(body, "var checking = verdict.Marker == FreezeGuard.StillScanning;", "(checking ? \"|checking\" : \"\")", "\"pause.antiCheatChecking\"");
    }

    [Fact]
    public void The_anti_cheat_scan_reads_no_network()
    {
        var scan = Code("src", "CouchHud.Core", "Library", "AntiCheat.cs");
        foreach (var banned in new[] { "HttpClient", "System.Net", "WebRequest", "Socket" })
            Assert.DoesNotContain(banned, scan);
        Assert.Contains("!entry.Attributes.HasFlag(FileAttributes.ReparsePoint)", scan);
    }
}
