namespace CouchHud.Service.Tests;

/// <summary>
/// A game locked for all child profiles, pinned from the shell's source (as LaunchOverrideContractTests): setting and lifting it is the same
/// call as the lock of one profile (same refusal while the settings are unusable, same parents' grant for exactly this entry, no second
/// way in), and everything that judges a start or a shelf for a child reads the rules through EffectiveRules, which add the household list.
/// </summary>
public sealed class ItemLockAllChildrenContractTests
{
    private const string Declaration = "    public object SetParentalItemLock(string id, bool locked, bool allChildren = false)";

    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CouchHud.slnx"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repository root not found");
    }

    private static string ShellRoot() => Path.Combine(Root(), "src", "CouchHud.Shell");

    private static string Code(string file) =>
        string.Join('\n', File.ReadAllText(file).Replace("\r\n", "\n").Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string Shell(params string[] path) => Code(Path.Combine([ShellRoot(), .. path]));

    private static IEnumerable<string> ShellFiles() =>
        Directory.EnumerateFiles(ShellRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string Body(string code, string declaration)
    {
        var lines = code.Split('\n');
        var start = Array.FindIndex(lines, l => l.StartsWith(declaration, StringComparison.Ordinal));
        Assert.True(start >= 0, $"not found: {declaration}");
        var end = Array.FindIndex(lines, start + 1, l => l == "    }");
        return string.Join('\n', lines[start..end]);
    }

    [Fact]
    public void Locking_and_unlocking_for_all_children_need_the_same_grant_as_the_lock_of_one_profile()
    {
        var body = Body(Shell("Hud.cs"), Declaration);
        var refuse = body.IndexOf("RefuseWhileConfigFaulted();", StringComparison.Ordinal);
        var grant = body.IndexOf("RequireParentGrant(allChildren ? \"lockAll\" : \"lock\", id);", StringComparison.Ordinal);
        var update = body.IndexOf("config.UpdateOrThrow(", StringComparison.Ordinal);
        Assert.True(refuse >= 0 && refuse < grant && grant < update, "refusal, then the grant for this entry, then the change");
        // Both scopes live in this one body: this method is the only writer of the household list in the shell.
        Assert.Contains("LockedItemsAllChildren", body);
        var writers = ShellFiles().Where(f => File.ReadAllText(f).Contains("LockedItemsAllChildren", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f)!).Order().ToList();
        Assert.Equal(["Hud.cs"], writers);
        Assert.Contains("hud.SetParentalItemLock(StringArg(0), Arg(1).ValueKind == JsonValueKind.True, Arg(2).ValueKind == JsonValueKind.True)", Shell("UiHost", "UiBridge.cs"));
    }

    [Fact]
    public void Removing_an_own_entry_checks_the_lock_the_shift_and_the_grant_inside_the_removal_and_uses_the_grant_before_removing()
    {
        var body = Body(Shell("Hud.cs"), "    public void RemoveEntry(string id)");
        Assert.Contains("library.RemoveCustomEntry(id, (c, entry, shifts) =>", body);
        Assert.Contains("c.ParentalEnabled && (shifts || CustomEntryLocks.IsLocked(c, known ?? entry, all))", body);
        Assert.Contains("UseUp(CheckParentGrant(\"removeEntry\", id))", body);
        var service = File.ReadAllText(Path.Combine(Root(), "src", "CouchHud.Core", "Library", "LibraryService.cs")).Replace("\r\n", "\n");
        var remove = service[service.IndexOf("public bool RemoveCustomEntry(", StringComparison.Ordinal)..];
        var authorize = remove.IndexOf("authorize?.Invoke(c, before[index], moves.Count > 0);", StringComparison.Ordinal);
        var removal = remove.IndexOf("c.Apps.Remove(valid[index]);", StringComparison.Ordinal);
        Assert.True(authorize >= 0 && authorize < removal, "the grant is checked and used up inside the update, before anything is removed");
        Assert.Contains("CustomEntryLocks.Rekey(", remove);
        // The couch lock: the bridge refuses the call while the couch is locked.
        Assert.DoesNotContain("removeEntry", CouchHud.Core.Guard.LockPolicy.OpenWhileLocked);
    }

    [Fact]
    public void Only_the_reset_with_its_own_grant_clears_the_unreadable_mark()
    {
        var hud = Shell("Hud.cs");
        var set = Body(hud, Declaration);
        Assert.DoesNotContain("LockedItemsAllChildrenUnreadable", set);
        var reset = Body(hud, "    public object ResetParentalAllChildrenLocks()");
        var refuse = reset.IndexOf("RefuseWhileConfigFaulted();", StringComparison.Ordinal);
        var grant = reset.IndexOf("RequireParentGrant(\"lockAllReset\");", StringComparison.Ordinal);
        var update = reset.IndexOf("config.UpdateOrThrow(", StringComparison.Ordinal);
        Assert.True(refuse >= 0 && refuse < grant && grant < update);
        Assert.Contains("LockedItemsAllChildrenUnreadable = false", reset);
        // No other place in the shell clears the mark.
        var clears = ShellFiles().Sum(f => Code(f).Split("LockedItemsAllChildrenUnreadable = false").Length - 1);
        Assert.Equal(1, clears);
        Assert.Contains("hud.ResetParentalAllChildrenLocks()", Shell("UiHost", "UiBridge.cs"));
    }

    [Fact]
    public void A_full_household_list_refuses_instead_of_reporting_success()
    {
        var body = Body(Shell("Hud.cs"), Declaration);
        Assert.Contains("shared.Count >= ConfigLimits.MaxListEntries", body);
        Assert.Contains("throw new InvalidOperationException(Strings.Get(\"parental.lockAllFull\"", body);
        Assert.DoesNotContain("&& shared.Count < ConfigLimits.MaxListEntries) shared.Add", body);
    }

    [Fact]
    public void The_grant_actions_for_the_scopes_are_known_and_classified()
    {
        foreach (var action in new[] { "lock", "lockAll", "lockAllReset" })
        {
            Assert.Contains(action, CouchHud.Core.Security.ParentCodeKind.HudOnlyActions);
        }
    }

    [Theory]
    [InlineData("Services/ParentalController.cs", "    public LaunchCheck CheckLaunch(LibraryItem item, int? age, bool grant)")]
    [InlineData("HudCompanions.cs", "    private string? CompanionGate(LibraryItem app)")]
    [InlineData("HudCompanions.cs", "    public object GetGuideApps()")]
    [InlineData("Hud.cs", "    public object GetFavorites()")]
    public void Every_check_of_a_start_or_a_shelf_reads_the_effective_rules(string file, string declaration)
    {
        var body = Body(Shell(file.Split('/')), declaration);
        Assert.Contains(".EffectiveRules", body);
        Assert.DoesNotContain(".Rules;", body);
    }

    [Fact]
    public void The_play_time_service_judges_by_the_effective_rules_too()
    {
        Assert.Contains("new ParentalService(() => _config.Current.Profile(ProfileId).EffectiveRules,", Shell("Services", "ParentalController.cs"));
    }

    [Fact]
    public void Nothing_in_the_shell_reads_the_stored_list_of_one_profile_to_judge_a_lock()
    {
        // The stored list is shown on the page and changed by the setter; the judging goes through ParentalService with the effective rules.
        foreach (var f in ShellFiles())
        {
            var reads = Code(f).Split('\n').Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"\.LockedItems\b")
                && !l.Contains("lockedItems = p.LockedItems", StringComparison.Ordinal)
                && !l.Contains("rules.LockedItems.", StringComparison.Ordinal)).ToList();
            Assert.True(reads.Count == 0, $"{Path.GetFileName(f)} reads LockedItems outside the page and the setter: {string.Join(" | ", reads)}");
        }
    }
}
