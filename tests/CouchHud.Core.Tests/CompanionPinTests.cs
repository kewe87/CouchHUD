using System.Runtime.InteropServices;
using CouchHud.Core.Config;
using CouchHud.Core.Library;
using CouchHud.Core.Security;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>
/// The locked-couch confirmation of a companion program. Real files for the hold (nobody may rewrite, delete, rename or link-write the file
/// while it is held), and the pure decisions (what is pinned, what starts, what the page shows, what a confirmation refuses).
/// </summary>
public class CompanionPinTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chud-pin-" + Guid.NewGuid().ToString("N")[..8]);

    public CompanionPinTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newFile, string existingFile, IntPtr security);

    private string Exe(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static StartPlan PlanOf(string exe, string args = "", string cwd = "") => new(exe, args, cwd);

    private static CompanionRule Rule(bool whenLocked = true, string sha = "", string pin = "") =>
        new() { AppId = "wand", WhenLocked = whenLocked, Sha256 = sha, LockedSha256 = pin };

    private static readonly string Pin = CompanionPin.Version + new string('a', 64);

    private static PinStart Decide(CompanionRule rule, bool plannedLocked, bool lockedNow, string digest, bool folderWritable = false) =>
        CompanionPin.Decide(rule, plannedLocked, lockedNow, digest, () => folderWritable);

    // ---- the hold, on real files

    [Fact]
    public void A_held_program_cannot_be_rewritten_deleted_or_renamed()
    {
        var exe = Exe("app.exe", "original");
        using var held = CompanionPin.Hold(PlanOf(exe));
        Assert.NotNull(held);
        Assert.ThrowsAny<IOException>(() => File.WriteAllText(exe, "swapped"));
        Assert.ThrowsAny<IOException>(() => File.Delete(exe));
        Assert.ThrowsAny<IOException>(() => File.Move(exe, Path.Combine(_dir, "moved.exe")));
        Assert.ThrowsAny<IOException>(() => File.Replace(Exe("other.exe", "other"), exe, null));
        Assert.Equal("original", File.ReadAllText(exe));
    }

    [Fact]
    public void A_hard_link_to_the_held_file_cannot_write_it_either()
    {
        var exe = Exe("app.exe", "original");
        var link = Path.Combine(_dir, "link.exe");
        Assert.True(CreateHardLinkW(link, exe, IntPtr.Zero), "the test needs a hard link on NTFS");
        using var held = CompanionPin.Hold(PlanOf(exe));
        Assert.NotNull(held);
        // The link is the same file object: the share mode of the hold applies to it.
        Assert.ThrowsAny<IOException>(() => File.WriteAllText(link, "swapped"));
        Assert.Equal("original", File.ReadAllText(exe));
    }

    [Fact]
    public void A_different_file_at_the_same_path_after_the_hold_is_released_is_a_changed_program()
    {
        var exe = Exe("app.exe", "original");
        var plan = PlanOf(exe);
        string pin;
        using (var held = CompanionPin.Hold(plan)!)
            pin = CompanionPin.DigestOf(plan, held);
        var rule = Rule(pin: pin);

        using (var again = CompanionPin.Hold(plan)!)
            Assert.Equal(PinStart.Start, Decide(rule, plannedLocked: true, lockedNow: true, CompanionPin.DigestOf(plan, again)));

        File.WriteAllText(exe, "swapped by a child");
        using (var swapped = CompanionPin.Hold(plan)!)
        {
            var digest = CompanionPin.DigestOf(plan, swapped);
            Assert.NotEqual(pin, digest);
            Assert.Equal(PinStart.SkipChanged, Decide(rule, plannedLocked: true, lockedNow: true, digest));
            // The page shows it as changed (after the host noted it at the last locked start) and offers the one-action confirm.
            Assert.Equal("pinChanged", CompanionPin.Problem(rule, changedAtLastLockedStart: true, folderWritable: false));
        }
    }

    [Fact]
    public void The_digest_covers_path_arguments_folder_and_file()
    {
        var a = Exe("a.exe", "same bytes");
        var b = Exe("b.exe", "same bytes");
        using var heldA = CompanionPin.Hold(PlanOf(a))!;
        using var heldB = CompanionPin.Hold(PlanOf(b))!;
        var baseline = CompanionPin.DigestOf(PlanOf(a), heldA);
        Assert.StartsWith(CompanionPin.Version, baseline);
        Assert.Equal(baseline, CompanionPin.DigestOf(PlanOf(a), heldA));
        Assert.NotEqual(baseline, CompanionPin.DigestOf(PlanOf(b), heldB));                       // another path, same bytes
        Assert.NotEqual(baseline, CompanionPin.DigestOf(PlanOf(a, args: "--evil"), heldA));       // other arguments
        Assert.NotEqual(baseline, CompanionPin.DigestOf(PlanOf(a, cwd: _dir + @"\sub"), heldA));  // other working folder
    }

    // ---- the pure decisions

    [Fact]
    public void A_pin_of_an_older_format_or_a_bare_hash_counts_as_not_confirmed()
    {
        Assert.False(CompanionPin.IsPin(new string('a', 64)));
        Assert.False(CompanionPin.IsPin("1:" + new string('a', 64)));
        Assert.False(CompanionPin.IsPin("2:" + new string('a', 64)));   // the previous format (before the folder flag): fails closed
        Assert.True(CompanionPin.IsPin(Pin));
        var kept = CompanionRules.Sanitize([new CompanionRule { AppId = "a", WhenLocked = true, LockedSha256 = new string('a', 64) }, new CompanionRule { AppId = "b", WhenLocked = true, LockedSha256 = Pin.ToUpperInvariant().Replace("2:", "2:") }]);
        Assert.Equal("", kept[0].LockedSha256);
        Assert.Equal("pinMissing", CompanionPin.Problem(kept[0], false, false));
    }

    [Fact]
    public void A_rule_is_pinned_only_when_it_newly_starts_on_the_locked_couch_and_has_no_signature_rule()
    {
        Assert.True(CompanionPin.ShouldPin(null, nowLocked: true));
        Assert.True(CompanionPin.ShouldPin(Rule(whenLocked: false), nowLocked: true));
        Assert.False(CompanionPin.ShouldPin(Rule(whenLocked: true), nowLocked: true));        // already so: a plain save does not re-pin
        Assert.False(CompanionPin.ShouldPin(Rule(whenLocked: false), nowLocked: false));
        Assert.False(CompanionPin.ShouldPin(Rule(whenLocked: false, sha: new string('b', 64)), nowLocked: true)); // Wand is exempt
    }

    [Theory]
    [InlineData(false, false, false, "Start")]          // an open couch never looks at the pin
    [InlineData(true, false, false, "SkipNotLocked")]   // locked and the rule is not for the locked couch
    [InlineData(false, true, false, "SkipNotLocked")]   // the couch locked after the plan: locked now counts
    [InlineData(true, true, false, "SkipUnpinned")]
    [InlineData(true, true, true, "Start")]
    public void The_start_decision_reads_the_lock_at_planning_time_and_right_before_the_start(bool plannedLocked, bool lockedNow, bool pinned, string expected)
    {
        var rule = Rule(whenLocked: expected != "SkipNotLocked" || (!plannedLocked && !lockedNow), pin: pinned ? Pin : "");
        if (expected == "SkipNotLocked") rule.WhenLocked = false;
        Assert.Equal(Enum.Parse<PinStart>(expected), Decide(rule, plannedLocked, lockedNow, Pin));
    }

    [Fact]
    public void A_plan_made_open_that_is_locked_by_the_time_of_the_start_must_meet_the_pin()
    {
        var unpinned = Rule(pin: "");
        Assert.Equal(PinStart.Start, Decide(unpinned, plannedLocked: false, lockedNow: false, Pin));
        Assert.Equal(PinStart.SkipUnpinned, Decide(unpinned, plannedLocked: false, lockedNow: true, Pin));
        Assert.Equal(PinStart.SkipChanged, Decide(Rule(pin: Pin), plannedLocked: false, lockedNow: true, CompanionPin.Version + new string('b', 64)));
    }

    [Fact]
    public void A_signature_rule_is_exempt_from_the_pin_but_not_from_the_locked_couch_switch()
    {
        var wand = Rule(sha: new string('c', 64));
        Assert.Equal(PinStart.Start, Decide(wand, true, true, Pin));
        Assert.Equal(PinStart.SkipNotLocked, Decide(Rule(whenLocked: false, sha: new string('c', 64)), true, true, Pin));
        Assert.Null(CompanionPin.Problem(wand, true, true));
    }

    [Fact]
    public void The_page_problem_states()
    {
        Assert.Null(CompanionPin.Problem(Rule(whenLocked: false), true, true));            // not for the locked couch: nothing to confirm
        Assert.Equal("pinMissing", CompanionPin.Problem(Rule(pin: ""), false, true));       // a legacy 0.7.0 rule
        Assert.Equal("pinChanged", CompanionPin.Problem(Rule(pin: Pin), true, true));       // the last locked start found another program
        Assert.Equal("folderWritable", CompanionPin.Problem(Rule(pin: Pin), false, true));  // confirmed, but a warning about the folder
        Assert.Null(CompanionPin.Problem(Rule(pin: Pin), false, false));
        Assert.True(CompanionPin.Confirmable("pinMissing"));
        Assert.True(CompanionPin.Confirmable("pinChanged"));
        Assert.True(CompanionPin.Confirmable("folderWritable"));    // cleared by allowing a writable folder when confirming
        Assert.False(CompanionPin.Confirmable(null));
    }

    [Fact]
    public void A_stale_changed_flag_is_gone_once_the_rule_is_confirmed_again()
    {
        // The host keeps the flag per app and clears it on a confirm, a re-pin and a removal; what the page shows follows the flag.
        var rule = Rule(pin: Pin);
        Assert.Equal("pinChanged", CompanionPin.Problem(rule, changedAtLastLockedStart: true, folderWritable: false));
        Assert.Null(CompanionPin.Problem(rule, changedAtLastLockedStart: false, folderWritable: false));
    }

    [Fact]
    public void A_confirmation_is_refused_unless_the_rule_fits_and_the_file_is_the_one_that_was_shown()
    {
        var now = CompanionPin.Version + new string('1', 64);
        Assert.Equal("missing", CompanionPin.ConfirmRefusal(null, now, now));
        Assert.Equal("notLocked", CompanionPin.ConfirmRefusal(Rule(whenLocked: false), now, now));
        Assert.Equal("signatureRule", CompanionPin.ConfirmRefusal(Rule(sha: new string('c', 64)), now, now));
        Assert.Equal("changed", CompanionPin.ConfirmRefusal(Rule(), now, CompanionPin.Version + new string('2', 64)));   // the file changed between showing and confirming
        Assert.Equal("changed", CompanionPin.ConfirmRefusal(Rule(), "garbage", now));
        Assert.Null(CompanionPin.ConfirmRefusal(Rule(), now, now));
        Assert.Null(CompanionPin.ConfirmRefusal(Rule(pin: Pin), now, now));   // confirming again after a change
    }

    [Fact]
    public void A_folder_others_can_write_in_is_flagged()
    {
        const int Write = 0x2;
        const int ReadOnly = 0x120089;
        string[] trusted = ["S-1-5-18", "S-1-5-32-544"];
        var programFiles = trusted.Select(sid => new AclEntry(sid, FolderProtection.FileAllAccess)).Append(new AclEntry("S-1-5-32-545", ReadOnly)).ToList();
        Assert.False(CompanionPin.FolderWritable("S-1-5-18", programFiles));
        foreach (var who in new[] { "S-1-5-32-545", "S-1-5-11", "S-1-1-0" })
            Assert.True(CompanionPin.FolderWritable("S-1-5-18", programFiles.Append(new AclEntry(who, Write)).ToList()), who);
        // An owner who is a normal user can always change the ACL.
        Assert.True(CompanionPin.FolderWritable("S-1-5-21-1-2-3-1001", programFiles));
        Assert.True(CompanionPin.FolderWritable(null, programFiles));
    }

    // ---- the folder at the start

    [Fact]
    public void A_folder_that_became_writable_after_the_confirmation_blocks_the_locked_start_unless_it_was_allowed()
    {
        var plan = PlanOf(@"C:\Program Files\X\x.exe");
        var pin = CompanionPin.Digest(plan, @"C:\Program Files\X\x.exe", new string('1', 64));
        var pinAllowed = CompanionPin.Digest(plan, @"C:\Program Files\X\x.exe", new string('1', 64), allowWritableFolder: true);
        Assert.NotEqual(pin, pinAllowed);   // the choice is part of what was confirmed

        var rule = Rule(pin: pin);
        Assert.Equal(PinStart.Start, Decide(rule, true, true, pin, folderWritable: false));
        Assert.Equal(PinStart.SkipFolderWritable, Decide(rule, true, true, pin, folderWritable: true));   // the ACL changed later
        Assert.Equal("folderWritable", CompanionPin.Problem(rule, false, true));

        var allowed = Rule(pin: pinAllowed);
        allowed.AllowWritableFolder = true;
        Assert.Equal(PinStart.Start, Decide(allowed, true, true, pinAllowed, folderWritable: true));
        Assert.Null(CompanionPin.Problem(allowed, false, true));
        // Editing the flag by hand breaks the pin instead of allowing anything.
        var edited = Rule(pin: pin);
        edited.AllowWritableFolder = true;
        Assert.Equal(PinStart.SkipChanged, Decide(edited, true, true, pinAllowed));   // the host computes the digest with the flag as it reads it
        // An unlocked start never looks at the folder.
        Assert.Equal(PinStart.Start, Decide(rule, false, false, pin, folderWritable: true));
        // Without a way to look at the folder the start is blocked (fail closed).
        Assert.Equal(PinStart.SkipFolderWritable, CompanionPin.Decide(rule, true, true, pin));
    }

    private const int Modify = 0x1301BF;
    private const int ReadExecute = 0x1200A9;
    private const int DeleteChild = 0x40;
    private static readonly string[] Trusted = ["S-1-5-18", "S-1-5-32-544", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"];

    private static (string?, IReadOnlyList<AclEntry>) Protected(params AclEntry[] extra) =>
        ("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464",
            Trusted.Select(sid => new AclEntry(sid, FolderProtection.FileAllAccess)).Append(new AclEntry("S-1-5-32-545", ReadExecute)).Concat(extra).ToList());

    [Fact]
    public void Inherit_only_entries_for_files_count_as_write_access_to_the_files_in_the_folder()
    {
        var plain = Protected().Item2;
        Assert.False(CompanionPin.FolderWritable(Trusted[0], plain));
        // Users:(OI)(IO)(M) never applies to the folder itself, but every file in it is writable by Users.
        Assert.True(CompanionPin.FolderWritable(Trusted[0], Protected(new AclEntry("S-1-5-32-545", Modify, AppliesHere: false, ObjectInherit: true)).Item2));
        Assert.True(CompanionPin.FolderWritable(Trusted[0], Protected(new AclEntry("S-1-5-11", Modify, AppliesHere: false, ObjectInherit: true)).Item2));
        Assert.True(CompanionPin.FolderWritable(Trusted[0], Protected(new AclEntry("S-1-1-0", FolderProtection.GenericWrite, AppliesHere: false, ObjectInherit: true)).Item2));
        // Inherited by sub-folders only (CI), or read access for files: not a write to the files here.
        Assert.False(CompanionPin.FolderWritable(Trusted[0], Protected(new AclEntry("S-1-5-32-545", Modify, AppliesHere: false, ObjectInherit: false)).Item2));
        Assert.False(CompanionPin.FolderWritable(Trusted[0], Protected(new AclEntry("S-1-5-32-545", ReadExecute, AppliesHere: false, ObjectInherit: true)).Item2));
        // CREATOR OWNER is only a placeholder.
        Assert.False(CompanionPin.FolderWritable(Trusted[0], Protected(new AclEntry("S-1-3-0", Modify, AppliesHere: false, ObjectInherit: true)).Item2));
    }

    private static CompanionPin.AclReader Tree(Dictionary<string, (string?, IReadOnlyList<AclEntry>)> acls) =>
        path => acls.TryGetValue(path.TrimEnd('\\'), out var acl) ? acl : null;

    private static Dictionary<string, (string?, IReadOnlyList<AclEntry>)> ProgramFilesTree() => new(StringComparer.OrdinalIgnoreCase)
    {
        [@"C:\Program Files\X\x.exe"] = Protected(),
        [@"C:\Program Files\X"] = Protected(),
        [@"C:\Program Files"] = Protected(),
        [@"C:"] = Protected()
    };

    [Fact]
    public void The_program_its_folders_and_every_folder_above_them_are_judged()
    {
        const string Exe = @"C:\Program Files\X\x.exe";
        Assert.False(CompanionPin.Writable(Tree(ProgramFilesTree()), Exe, @"C:\Program Files\X"));

        // The exe file's own ACL.
        var file = ProgramFilesTree();
        file[Exe] = Protected(new AclEntry("S-1-5-32-545", Modify));
        Assert.True(CompanionPin.Writable(Tree(file), Exe, @"C:\Program Files\X"));

        // The folder: write by Users, and the inherit-only entry for files.
        var folder = ProgramFilesTree();
        folder[@"C:\Program Files\X"] = Protected(new AclEntry("S-1-5-32-545", Modify, AppliesHere: false, ObjectInherit: true));
        Assert.True(CompanionPin.Writable(Tree(folder), Exe, @"C:\Program Files\X"));

        // The working folder is judged the same way.
        var cwd = ProgramFilesTree();
        cwd[@"D:\Work"] = Protected(new AclEntry("S-1-5-11", Modify));
        cwd[@"D:"] = Protected();
        Assert.True(CompanionPin.Writable(Tree(cwd), Exe, @"D:\Work"));

        // Swapping a folder above: Users may delete children (or change permissions) of an ancestor.
        var ancestor = ProgramFilesTree();
        ancestor[@"C:\Program Files"] = Protected(new AclEntry("S-1-5-32-545", DeleteChild));
        Assert.True(CompanionPin.Writable(Tree(ancestor), Exe, @"C:\Program Files\X"));
        var root = ProgramFilesTree();
        root[@"C:"] = Protected(new AclEntry("S-1-5-11", FolderProtection.ChangePermissions));
        Assert.True(CompanionPin.Writable(Tree(root), Exe, @"C:\Program Files\X"));
        // An inherit-only entry on an ancestor is for its children, not for the ancestor.
        var inheritOnly = ProgramFilesTree();
        inheritOnly[@"C:"] = Protected(new AclEntry("S-1-5-11", Modify, AppliesHere: false, ObjectInherit: true));
        Assert.False(CompanionPin.Writable(Tree(inheritOnly), Exe, @"C:\Program Files\X"));
    }

    [Fact]
    public void An_acl_that_cannot_be_read_counts_as_writable()
    {
        const string Exe = @"C:\Program Files\X\x.exe";
        var missing = ProgramFilesTree();
        missing.Remove(@"C:\Program Files");
        Assert.True(CompanionPin.Writable(Tree(missing), Exe, @"C:\Program Files\X"));
        Assert.True(CompanionPin.Writable(_ => throw new UnauthorizedAccessException(), Exe, @"C:\Program Files\X"));
        Assert.True(CompanionPin.Writable(_ => throw new InvalidOperationException(), Exe, ""));
    }

    // ---- the open-couch note of a launch

    private static GameSession SessionOf(string id, long launch, bool external = false) =>
        new(new LibraryItem { Id = id, Name = id, Source = "custom", Type = ItemType.Game, Launch = new LaunchTarget(LaunchKind.Exe, @"D:\g.exe") }, DateTimeOffset.UtcNow, [1], external, null, launch);

    [Fact]
    public void An_open_couch_note_belongs_to_the_launch_that_made_it()
    {
        var at = DateTimeOffset.UtcNow;
        var note = new OpenLaunchNote("game", 7, at);
        Assert.True(note.Accepts(SessionOf("game", 7), at.AddSeconds(30)));
        Assert.False(note.Accepts(SessionOf("game", 8), at.AddSeconds(30)));          // another launch of the same game
        Assert.False(note.Accepts(SessionOf("game", 0), at.AddSeconds(30)));          // started outside CouchHud
        Assert.False(note.Accepts(SessionOf("game", 7, external: true), at.AddSeconds(30)));
        Assert.False(note.Accepts(SessionOf("other", 7), at.AddSeconds(30)));
        Assert.False(note.Accepts(SessionOf("game", 7), at.AddMinutes(6)));            // too old
        Assert.True(note.Belongs(SessionOf("game", 7)));
        Assert.False(note.Belongs(SessionOf("game", 9)));
        Assert.False(new OpenLaunchNote("game", 0, at).Belongs(SessionOf("game", 0)));
    }

    [Fact]
    public void Forgetting_the_unlock_keeps_the_note_only_for_the_session_being_handed_over()
    {
        var note = new OpenLaunchNote("game", 7, DateTimeOffset.UtcNow);
        Assert.Same(note, OpenLaunchNote.AfterForgetUnlock(note, SessionOf("game", 7)));   // the switch's own hand-over
        Assert.Null(OpenLaunchNote.AfterForgetUnlock(note, null));                         // trip to the HUD, profile switch, new start
        Assert.Null(OpenLaunchNote.AfterForgetUnlock(note, SessionOf("game", 8)));         // another launch of the same game
        Assert.Null(OpenLaunchNote.AfterForgetUnlock(note, SessionOf("other", 9)));        // another game never inherits it
        Assert.Null(OpenLaunchNote.AfterForgetUnlock(note, SessionOf("game", 7, external: true)));
        Assert.Null(OpenLaunchNote.AfterForgetUnlock(null, SessionOf("game", 7)));
    }

    [Fact]
    public void The_tracker_gives_each_launch_a_serial_and_the_session_carries_it()
    {
        var dir = Path.Combine(Path.GetTempPath(), "couchhud-pin-tracker");
        var probe = new Probe();
        var tracker = new GameSessionTracker(probe);
        var started = new List<GameSession>();
        tracker.Started += started.Add;
        var item = new LibraryItem { Id = "custom:g", Source = "custom", Name = "g", InstallDir = dir, Launch = new LaunchTarget(LaunchKind.Url, "x://") };
        var t0 = DateTimeOffset.Parse("2026-10-17T20:00:00Z");

        var first = tracker.Launched(item, t0);
        var second = tracker.Launched(item, t0.AddSeconds(1));
        Assert.True(first > 0 && second > first);
        probe.Processes.Add((42, Path.Combine(dir, "g.exe")));
        tracker.Tick(t0.AddSeconds(10));
        // The session carries the serial of the launch it came from: a note made for the first launch does not fit it.
        var session = Assert.Single(started);
        Assert.Equal(second, session.Launch);
        Assert.False(new OpenLaunchNote("custom:g", first, t0).Accepts(session, t0.AddSeconds(10)));
        Assert.True(new OpenLaunchNote("custom:g", second, t0).Accepts(session, t0.AddSeconds(10)));
    }

    private sealed class Probe : IProcessProbe
    {
        public List<(int, string?)> Processes { get; } = [];
        public IReadOnlyList<(int Pid, string? Path)> Snapshot() => Processes;
        public int SteamRunningAppId() => 0;
    }
}
