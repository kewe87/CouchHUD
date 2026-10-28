using CouchHud.Core.Config;
using CouchHud.Core.Library;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>Startup parameters, working folder and an own start target for found entries (kept as an override).</summary>
public sealed class LaunchOverrideTests
{
    private static LaunchFileSystem Fs(Func<string, bool>? redirect = null, Func<char, bool>? network = null) =>
        new(File.Exists, Directory.Exists, network ?? (_ => false), redirect ?? (_ => false), @"C:\Windows");

    /// <summary>A game folder with the game, a batch file next to it and a file outside.</summary>
    private static (string Dir, string Exe, string Bat, string Outside) GameFolder()
    {
        var dir = Directory.CreateTempSubdirectory("couchhud-launch-").FullName;
        var game = Directory.CreateDirectory(Path.Combine(dir, "My Game")).FullName;
        var exe = Path.Combine(game, "game.exe");
        var bat = Path.Combine(game, "start dx11.bat");
        var outside = Path.Combine(dir, "other.exe");
        foreach (var f in new[] { exe, bat, outside }) File.WriteAllText(f, "");
        return (game, exe, bat, outside);
    }

    private static LibraryItem Folder(string dir, string exe, string[]? args = null) => new()
    {
        Id = "folder:abc", Source = "folder", Name = "My Game", InstallDir = dir,
        Launch = new LaunchTarget(LaunchKind.Exe, exe) { Args = args ?? [], WorkingDirectory = dir }
    };

    private static LibraryItem Steam(string dir) => new()
    {
        Id = "steam:620", Source = "steam", Name = "Portal 2", InstallDir = dir, Launch = new LaunchTarget(LaunchKind.Url, "steam://rungameid/620")
    };

    private static LibraryItem Epic(string dir) => new()
    {
        Id = "epic:x", Source = "epic", Name = "Epic Game", InstallDir = dir,
        Launch = new LaunchTarget(LaunchKind.Url, "com.epicgames.launcher://apps/x?action=launch&silent=true")
    };

    [Fact]
    public void Each_source_says_honestly_where_its_parameters_go()
    {
        var (dir, exe, _, _) = GameFolder();
        Assert.Equal("override", LaunchOverrides.ArgsMode(Folder(dir, exe), null));
        Assert.Equal("override", LaunchOverrides.ArgsMode(Folder(dir, exe) with { Source = "gog", Id = "gog:1" }, null));
        Assert.Equal("steam", LaunchOverrides.ArgsMode(Steam(dir), null));
        Assert.Equal("launcher", LaunchOverrides.ArgsMode(Epic(dir), null));
        Assert.Equal("launcher", LaunchOverrides.ArgsMode(Epic(dir) with { Source = "ubisoft", Id = "ubisoft:1" }, null));
        Assert.Equal("own", LaunchOverrides.ArgsMode(Folder(dir, exe) with { Source = "custom", Id = "custom:x" }, null));
        var web = new LibraryItem { Id = "custom:w", Source = "custom", Name = "W", Launch = new LaunchTarget(LaunchKind.Url, "https://x.example") { Embedded = true } };
        Assert.Equal("none", LaunchOverrides.ArgsMode(web, null));
        Assert.False(LaunchOverrides.TargetAllowed(web));
        // With an own target, every source takes parameters for it (Steam and Epic included).
        Assert.Equal("override", LaunchOverrides.ArgsMode(Steam(dir), new ItemOverride { LaunchTarget = exe }));
        Assert.True(LaunchOverrides.TargetAllowed(Steam(dir)));
    }

    [Fact]
    public void Parameters_of_a_found_exe_replace_the_scanned_ones_and_reset_to_them()
    {
        var (dir, exe, _, _) = GameFolder();
        var item = Folder(dir, exe, ["-launchParam"]);
        var o = new ItemOverride();
        Assert.Equal(["-launchParam"], LaunchOverrides.View(item, o).Args);

        LaunchOverrides.Apply(o, item, new LaunchOverrideChange { SetArgs = true, Args = "-dx11 \"-name=My Name\"" }, Fs());
        var start = LaunchOverrides.Effective(item, o, Fs());
        Assert.Equal(["-dx11", "-name=My Name"], start.Args);
        Assert.Equal(exe, start.Target);
        Assert.True(LaunchOverrides.View(item, o).Custom);

        LaunchOverrides.Apply(o, item, new LaunchOverrideChange { SetArgs = true, Args = "" }, Fs());
        Assert.Null(o.LaunchArgs);
        Assert.True(o.IsEmpty);
        Assert.Equal(["-launchParam"], LaunchOverrides.Effective(item, o, Fs()).Args);
    }

    [Fact]
    public void Steam_and_epic_take_no_parameters_without_an_own_target()
    {
        var (dir, _, _, _) = GameFolder();
        var o = new ItemOverride();
        var steam = Assert.Throws<ArgumentException>(() => LaunchOverrides.Apply(o, Steam(dir), new LaunchOverrideChange { SetArgs = true, Args = "-novid" }, Fs()));
        Assert.Contains("Steam", steam.Message);
        Assert.Throws<ArgumentException>(() => LaunchOverrides.Apply(o, Epic(dir), new LaunchOverrideChange { SetArgs = true, Args = "-x" }, Fs()));
        Assert.Throws<ArgumentException>(() => LaunchOverrides.Apply(o, Epic(dir), new LaunchOverrideChange { SetCwd = true, Cwd = dir }, Fs()));
        Assert.True(o.IsEmpty);
        // The steam:// start itself is never changed by an empty override.
        Assert.Equal("steam://rungameid/620", LaunchOverrides.Effective(Steam(dir), o, Fs()).Target);
    }

    [Fact]
    public void An_own_target_inside_the_game_folder_starts_instead_and_resets_completely()
    {
        var (dir, exe, bat, _) = GameFolder();
        var item = Steam(dir);
        var o = new ItemOverride();
        LaunchOverrides.Apply(o, item, new LaunchOverrideChange { SetTarget = true, Target = bat }, Fs());
        LaunchOverrides.Apply(o, item, new LaunchOverrideChange { SetArgs = true, Args = "-windowed \"two words\"" }, Fs());
        var start = LaunchOverrides.Effective(item, o, Fs());
        Assert.Equal(LaunchKind.Exe, start.Kind);
        Assert.Equal(bat, start.Target);
        Assert.Equal(dir, start.WorkingDirectory);
        Assert.Equal(["-windowed", "two words"], start.Args);

        LaunchOverrides.Apply(o, item, new LaunchOverrideChange { SetTarget = true, Target = null }, Fs());
        Assert.True(o.IsEmpty);
        Assert.Equal("steam://rungameid/620", LaunchOverrides.Effective(item, o, Fs()).Target);
        _ = exe;
    }

    [Fact]
    public void A_target_outside_the_game_folder_on_a_share_or_of_another_kind_is_refused()
    {
        var (dir, _, _, outside) = GameFolder();
        var item = Folder(dir, Path.Combine(dir, "game.exe"));
        void Refused(string? path, LaunchFileSystem? fs = null)
        {
            var o = new ItemOverride();
            Assert.Throws<ArgumentException>(() => LaunchOverrides.Apply(o, item, new LaunchOverrideChange { SetTarget = true, Target = path }, fs ?? Fs()));
            Assert.True(o.IsEmpty);
        }
        Refused(outside);
        Refused(Path.Combine(dir, "..", "other.exe"));
        Refused(@"\\server\share\My Game\game.exe");
        Refused(@"\\?\" + Path.Combine(dir, "game.exe"));
        Refused(Path.Combine(dir, "game.exe:evil.exe"));
        Refused(Path.Combine(dir, "readme.txt"));
        Refused(Path.Combine(dir, "launch.lnk"));
        Refused(Path.Combine(dir, "missing.exe"));
        Refused(Path.Combine(dir, "CON.bat"));
        Refused(Path.Combine(dir, "x\u0001.exe"));
        Refused(Path.Combine(dir, new string('a', 300) + ".exe"));
        // A drive mapped to a share, and a link that points elsewhere.
        Refused(Path.Combine(dir, "game.exe"), Fs(network: _ => true));
        Refused(Path.Combine(dir, "game.exe"), Fs(redirect: _ => true));
        // An entry without an install folder has no own target at all.
        var noFolder = item with { InstallDir = null };
        Assert.Throws<ArgumentException>(() => LaunchOverrides.Apply(new ItemOverride(), noFolder, new LaunchOverrideChange { SetTarget = true, Target = Path.Combine(dir, "game.exe") }, Fs()));
    }

    [Fact]
    public void A_shell_or_a_tool_in_the_game_folder_is_no_game_target()
    {
        var (dir, _, _, _) = GameFolder();
        var cmd = Path.Combine(dir, "cmd.exe");
        File.WriteAllText(cmd, "");
        Assert.Throws<ArgumentException>(() => LaunchOverrides.Apply(new ItemOverride(), Folder(dir, Path.Combine(dir, "game.exe")),
            new LaunchOverrideChange { SetTarget = true, Target = cmd }, Fs()));
    }

    [Fact]
    public void The_start_checks_the_target_again()
    {
        var (dir, _, bat, outside) = GameFolder();
        var item = Folder(dir, Path.Combine(dir, "game.exe"));
        var o = new ItemOverride();
        LaunchOverrides.Apply(o, item, new LaunchOverrideChange { SetTarget = true, Target = bat }, Fs());
        File.Delete(bat);
        Assert.Throws<InvalidOperationException>(() => LaunchOverrides.Effective(item, o, Fs()));
        // An edited config cannot point outside the folder or to a share either.
        Assert.Throws<InvalidOperationException>(() => LaunchOverrides.Effective(item, new ItemOverride { LaunchTarget = outside }, Fs()));
        Assert.Throws<InvalidOperationException>(() => LaunchOverrides.Effective(item, new ItemOverride { LaunchTarget = @"\\server\share\x.exe" }, Fs()));
        Assert.Throws<InvalidOperationException>(() => LaunchOverrides.Effective(item, new ItemOverride { LaunchArgs = ["a\nb"] }, Fs()));
    }

    [Fact]
    public void A_batch_file_gets_no_cmd_syntax_in_its_parameters()
    {
        var (dir, _, bat, _) = GameFolder();
        var item = Folder(dir, Path.Combine(dir, "game.exe"));
        var o = new ItemOverride();
        LaunchOverrides.Apply(o, item, new LaunchOverrideChange { SetTarget = true, Target = bat }, Fs());
        foreach (var bad in new[] { "a & calc", "a|b", "^x", ">out", "<in", "%PATH%", "!x!", "(x)", "\"a\\\"b\"" })
            Assert.Throws<ArgumentException>(() => LaunchOverrides.Apply(o, item, new LaunchOverrideChange { SetArgs = true, Args = bad }, Fs()));
        Assert.Null(o.LaunchArgs);
        LaunchOverrides.Apply(o, item, new LaunchOverrideChange { SetArgs = true, Args = "-dx11 \"C:\\My Games\\x\" --skip=1" }, Fs());
        Assert.Equal(["-dx11", @"C:\My Games\x", "--skip=1"], o.LaunchArgs);
    }

    [Fact]
    public void A_batch_file_starts_through_cmd_by_full_path_without_the_shell()
    {
        var plan = LaunchOptions.Plan(new LaunchTarget(LaunchKind.Exe, @"D:\Games\My Game\start dx11.bat") { Args = ["-w", "two words", ""] }, _ => true, @"C:\Windows\System32");
        Assert.Equal(@"C:\Windows\System32\cmd.exe", plan.FileName);
        Assert.True(plan.Direct);
        Assert.Equal("/d /v:off /s /c \"\"D:\\Games\\My Game\\start dx11.bat\" -w \"two words\" \"\"\"", plan.Arguments);
        // Whatever the config says, cmd syntax never reaches the line.
        Assert.Throws<ArgumentException>(() => LaunchOptions.Plan(new LaunchTarget(LaunchKind.Exe, @"D:\g\run.cmd") { Args = ["x & calc"] }, _ => true));
        Assert.Throws<ArgumentException>(() => LaunchOptions.Plan(new LaunchTarget(LaunchKind.Exe, @"D:\g\%x%\run.bat"), _ => true));
        // An exe keeps the shell (UAC) and the CommandLineToArgvW quoting.
        Assert.False(LaunchOptions.Plan(new LaunchTarget(LaunchKind.Exe, @"D:\g\game.exe") { Args = ["a & b"] }, _ => true).Direct);
    }

    [Fact]
    public void A_batch_file_runs_without_a_console_window_and_everything_else_as_planned()
    {
        var plan = LaunchOptions.Plan(new LaunchTarget(LaunchKind.Exe, @"D:\Games\My Game\start.cmd") { Args = ["-w", "two words"], WorkingDirectory = @"D:\Games\My Game\bin" },
            _ => true, @"C:\Windows\System32");
        var psi = LaunchOptions.StartInfo(plan);
        // No window in the taskbar: cmd.exe's console is created hidden.
        Assert.True(psi.CreateNoWindow);
        // Started directly (never through the shell), with exactly the checked line and folder; nothing redirected, so its console stays
        // its own (timeout and the like behave as before) and no handle of CouchHUD's is passed on.
        Assert.False(psi.UseShellExecute);
        Assert.Equal(@"C:\Windows\System32\cmd.exe", psi.FileName);
        Assert.Equal(plan.Arguments, psi.Arguments);
        Assert.Equal(@"D:\Games\My Game\bin", psi.WorkingDirectory);
        Assert.False(psi.RedirectStandardInput || psi.RedirectStandardOutput || psi.RedirectStandardError);

        // An exe keeps the shell (UAC prompt) and its own window behavior.
        var exe = LaunchOptions.StartInfo(LaunchOptions.Plan(new LaunchTarget(LaunchKind.Exe, @"D:\g\game.exe") { Args = ["-x"] }, _ => true));
        Assert.True(exe.UseShellExecute);
        Assert.False(exe.CreateNoWindow);
        Assert.Equal("-x", exe.Arguments);
    }

    [Fact]
    public void The_approval_of_a_batch_file_covers_its_content()
    {
        var (dir, _, bat, _) = GameFolder();
        File.WriteAllText(bat, "@echo off\r\nstart game.exe -dx11\r\n");
        var item = Folder(dir, Path.Combine(dir, "game.exe"));
        var o = new ItemOverride();
        LaunchOverrides.Apply(o, item, new LaunchOverrideChange { SetTarget = true, Target = bat }, Fs());
        Assert.Equal(64, o.LaunchTargetSha256!.Length);
        Assert.Equal(new FileInfo(bat).Length, o.LaunchTargetSize);
        Assert.Equal(bat, LaunchOverrides.Effective(item, o, Fs()).Target);

        // The child edits the approved file: the start is refused with an honest hint, and a change of the parameters does not approve it on the side.
        File.WriteAllText(bat, "@echo off\r\ncmd.exe\r\n");
        var refused = Assert.Throws<InvalidOperationException>(() => LaunchOverrides.Effective(item, o, Fs()));
        Assert.Contains("start dx11.bat", refused.Message);
        Assert.Throws<ArgumentException>(() => LaunchOverrides.Apply(o, item, new LaunchOverrideChange { SetArgs = true, Args = "-w" }, Fs()));
        Assert.Null(o.LaunchArgs);
        // Same size, other content counts as changed too.
        File.WriteAllText(bat, "@echo off\r\nstart game.exe -dx12\r\n");
        Assert.Throws<InvalidOperationException>(() => LaunchOverrides.Effective(item, o, Fs()));

        // Picking it again (with the parents' code in the host) approves the new content.
        LaunchOverrides.Apply(o, item, new LaunchOverrideChange { SetTarget = true, Target = bat }, Fs());
        Assert.Equal(bat, LaunchOverrides.Effective(item, o, Fs()).Target);

        // A stored batch target without a fingerprint (an older or hand-edited config) or with a broken one never starts.
        Assert.Throws<InvalidOperationException>(() => LaunchOverrides.Effective(item, new ItemOverride { LaunchTarget = bat }, Fs()));
        Assert.Throws<InvalidOperationException>(() => LaunchOverrides.Effective(item,
            new ItemOverride { LaunchTarget = bat, LaunchTargetSha256 = new string('z', 64), LaunchTargetSize = o.LaunchTargetSize }, Fs()));
        // A file that cannot be read cannot be pinned.
        Assert.Throws<ArgumentException>(() => LaunchOverrides.Apply(new ItemOverride(), item, new LaunchOverrideChange { SetTarget = true, Target = bat },
            Fs() with { Fingerprint = _ => null }));

        // Back to automatic drops the fingerprint with the target.
        LaunchOverrides.Apply(o, item, new LaunchOverrideChange { SetTarget = true, Target = null }, Fs());
        Assert.Null(o.LaunchTargetSha256);
        Assert.Null(o.LaunchTargetSize);
    }

    [Fact]
    public void A_batch_file_over_one_megabyte_is_neither_pinned_nor_started()
    {
        var (dir, _, bat, _) = GameFolder();
        var item = Folder(dir, Path.Combine(dir, "game.exe"));
        File.WriteAllText(bat, "@echo off\r\n");
        var o = new ItemOverride();
        LaunchOverrides.Apply(o, item, new LaunchOverrideChange { SetTarget = true, Target = bat }, Fs());
        var pin = FileFingerprint.Pinned(o);

        // Grown past the limit after the approval: the start is refused with the honest reason, here and on the launcher's handle.
        File.WriteAllBytes(bat, new byte[FileFingerprint.MaxScriptBytes + 1]);
        Assert.True(FileFingerprint.Read(bat)!.Value.TooLarge);
        Assert.Equal("", FileFingerprint.Read(bat)!.Value.Sha256);
        var refused = Assert.Throws<InvalidOperationException>(() => LaunchOverrides.Effective(item, o, Fs()));
        Assert.Contains("MB", refused.Message);
        Assert.Contains("MB", Assert.Throws<InvalidOperationException>(() => LaunchOptions.HoldScript(bat, pin)).Message);
        // Picking it again does not approve it.
        var notPinned = Assert.Throws<ArgumentException>(() => LaunchOverrides.Apply(new ItemOverride(), item, new LaunchOverrideChange { SetTarget = true, Target = bat }, Fs()));
        Assert.Contains("start dx11.bat", notPinned.Message);
        // A hand-edited pin with a size over the limit is no pin.
        Assert.Null(FileFingerprint.Pinned(new ItemOverride { LaunchTarget = bat, LaunchTargetSha256 = new string('a', 64), LaunchTargetSize = FileFingerprint.MaxScriptBytes + 1 }));

        // Exactly at the limit is still a start script.
        File.WriteAllBytes(bat, new byte[FileFingerprint.MaxScriptBytes]);
        LaunchOverrides.Apply(o, item, new LaunchOverrideChange { SetTarget = true, Target = bat }, Fs());
        Assert.Equal(FileFingerprint.MaxScriptBytes, o.LaunchTargetSize);
    }

    [Fact]
    public void A_refused_start_reads_the_batch_file_once()
    {
        var (dir, _, bat, _) = GameFolder();
        var item = Folder(dir, Path.Combine(dir, "game.exe"));
        File.WriteAllText(bat, "@echo off\r\n");
        var o = new ItemOverride();
        LaunchOverrides.Apply(o, item, new LaunchOverrideChange { SetTarget = true, Target = bat }, Fs());
        var reads = 0;
        LaunchFileSystem Counting(FileFingerprint? answer) => Fs() with { Fingerprint = _ => { reads++; return answer; } };

        // Grown past the limit: one reading gives the refusal and its reason.
        Assert.Contains("MB", Assert.Throws<InvalidOperationException>(() => LaunchOverrides.Effective(item, o, Counting(new FileFingerprint(FileFingerprint.MaxScriptBytes + 1, "")))).Message);
        Assert.Equal(1, reads);
        // Changed: the same.
        reads = 0;
        Assert.Contains("start dx11.bat", Assert.Throws<InvalidOperationException>(() => LaunchOverrides.Effective(item, o, Counting(new FileFingerprint(3, new string('b', 64))))).Message);
        Assert.Equal(1, reads);
        // Unchanged: one reading, and it starts.
        reads = 0;
        Assert.Equal(bat, LaunchOverrides.Effective(item, o, Counting(FileFingerprint.Pinned(o))).Target);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void An_exe_target_is_not_pinned()
    {
        var (dir, _, _, _) = GameFolder();
        var other = Path.Combine(dir, "game_dx12.exe");
        File.WriteAllText(other, "v1");
        var item = Folder(dir, Path.Combine(dir, "game.exe"));
        var o = new ItemOverride();
        LaunchOverrides.Apply(o, item, new LaunchOverrideChange { SetTarget = true, Target = other }, Fs());
        Assert.Null(o.LaunchTargetSha256);
        // A game update changes the exe; replacing it is no more than replacing the scanned exe (see SECURITY.md).
        File.WriteAllText(other, "v2 after an update");
        Assert.Equal(other, LaunchOverrides.Effective(item, o, Fs()).Target);
    }

    [Fact]
    public void Only_a_pinned_own_target_is_a_batch_file_for_the_locked_couch()
    {
        var (dir, exe, bat, _) = GameFolder();
        var item = Folder(dir, exe);
        var o = new ItemOverride();
        LaunchOverrides.Apply(o, item, new LaunchOverrideChange { SetTarget = true, Target = bat }, Fs());
        var start = LaunchOverrides.Effective(item, o, Fs());
        Assert.False(LaunchOverrides.IsUnpinnedScript(start, o));
        Assert.NotNull(LaunchOverrides.PinFor(start, o));

        // An own entry or a scanned entry that starts a batch file: nobody approved its content, so the host starts it only unlocked.
        var custom = Folder(dir, bat) with { Source = "custom", Id = "custom:bat" };
        Assert.True(LaunchOverrides.IsUnpinnedScript(LaunchOverrides.Effective(custom, null, Fs()), null));
        Assert.True(LaunchOverrides.IsUnpinnedScript(new LaunchTarget(LaunchKind.Exe, @"D:\G\RUN.CMD"), null));
        Assert.True(LaunchOverrides.IsUnpinnedScript(start, new ItemOverride { LaunchTarget = bat }));
        Assert.Null(LaunchOverrides.PinFor(start, new ItemOverride { LaunchTarget = bat }));
        // An exe or a store link is no batch file.
        Assert.False(LaunchOverrides.IsUnpinnedScript(item.Launch, null));
        Assert.False(LaunchOverrides.IsUnpinnedScript(new LaunchTarget(LaunchKind.Url, "steam://rungameid/1"), null));
    }

    [Fact]
    public void The_launcher_holds_the_batch_file_and_compares_it_on_that_handle()
    {
        var (_, _, bat, _) = GameFolder();
        File.WriteAllText(bat, "@echo off\r\n");
        var pin = FileFingerprint.Read(bat);
        using (var hold = LaunchOptions.HoldScript(bat, pin))
        {
            // Nobody writes, renames or deletes it while cmd.exe runs it; reading stays possible (cmd.exe reads it).
            Assert.ThrowsAny<IOException>(() => File.AppendAllText(bat, "calc.exe\r\n"));
            Assert.ThrowsAny<IOException>(() => File.Move(bat, bat + ".old"));
            using var reader = new FileStream(bat, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Assert.Equal(pin!.Value.Size, reader.Length);
        }
        File.AppendAllText(bat, "calc.exe\r\n");
        Assert.Throws<InvalidOperationException>(() => LaunchOptions.HoldScript(bat, pin));
        // Unpinned (an own entry, started unlocked): held all the same.
        LaunchOptions.HoldScript(bat, null).Dispose();
        Assert.Throws<InvalidOperationException>(() => LaunchOptions.HoldScript(bat + ".missing.bat", null));
    }

    [Fact]
    public void A_batch_file_runs_in_its_game_folder_only()
    {
        var (dir, exe, bat, _) = GameFolder();
        var outside = Path.GetDirectoryName(dir)!;
        var sub = Directory.CreateDirectory(Path.Combine(dir, "bin")).FullName;
        var item = Folder(dir, exe);
        var o = new ItemOverride();
        LaunchOverrides.Apply(o, item, new LaunchOverrideChange { SetTarget = true, Target = bat }, Fs());
        Assert.Throws<ArgumentException>(() => LaunchOverrides.Apply(o, item, new LaunchOverrideChange { SetCwd = true, Cwd = outside }, Fs()));
        Assert.Null(o.LaunchCwd);
        LaunchOverrides.Apply(o, item, new LaunchOverrideChange { SetCwd = true, Cwd = sub }, Fs());
        Assert.Equal(sub, LaunchOverrides.Effective(item, o, Fs()).WorkingDirectory);
        // An edited config cannot move it out either.
        o.LaunchCwd = outside;
        Assert.Throws<InvalidOperationException>(() => LaunchOverrides.Effective(item, o, Fs()));

        // A folder set for an exe target does not carry over to a batch file outside the game folder; for the exe it is fine.
        var exeOverride = new ItemOverride();
        LaunchOverrides.Apply(exeOverride, item, new LaunchOverrideChange { SetCwd = true, Cwd = outside }, Fs());
        Assert.Equal(outside, exeOverride.LaunchCwd);
        // A scanned batch file (found in a folder) gets the same rule.
        var scannedBat = Folder(dir, bat);
        Assert.Throws<ArgumentException>(() => LaunchOverrides.Apply(new ItemOverride(), scannedBat, new LaunchOverrideChange { SetCwd = true, Cwd = outside }, Fs()));
        Assert.Throws<InvalidOperationException>(() => LaunchOverrides.Effective(scannedBat, new ItemOverride { LaunchCwd = outside }, Fs()));
        // A folder inside that leads elsewhere does not count as inside.
        Assert.Throws<ArgumentException>(() => LaunchOverrides.Apply(new ItemOverride(), scannedBat, new LaunchOverrideChange { SetCwd = true, Cwd = sub },
            Fs() with { RedirectBelow = (_, path) => path.EndsWith("bin", StringComparison.OrdinalIgnoreCase) }));
    }

    [Fact]
    public void A_batch_file_gets_plain_ascii_parameters_only()
    {
        foreach (var bad in new[] { "-name=M\u00fcller", "\uff06calc", "\uff05PATH\uff05", "\u201cx\u201d", "a\u00a0b" })
            Assert.Throws<ArgumentException>(() => LaunchOptions.CheckScriptArguments([bad]));
        LaunchOptions.CheckScriptArguments(["-dx11", "two words", "--skip=1", @"C:\My Games\x", "a~b"]);
        // An exe keeps any text (CommandLineToArgvW quoting, Unicode command line).
        Assert.False(LaunchOptions.Plan(new LaunchTarget(LaunchKind.Exe, @"D:\g\game.exe") { Args = ["-name=M\u00fcller"] }, _ => true).Direct);
    }

    [JunctionFact]
    public void A_junction_inside_the_game_folder_is_no_way_to_a_target()
    {
        var (dir, exe, _, _) = GameFolder();
        var elsewhere = Directory.CreateTempSubdirectory("couchhud-elsewhere-").FullName;
        var tool = Path.Combine(elsewhere, "tool.exe");
        File.WriteAllText(tool, "");
        var link = Path.Combine(dir, "linked");
        Links.Junction(link, elsewhere);
        var item = Folder(dir, exe);
        try
        {
            // At the approval: a file reached through a junction folder is refused.
            Assert.Throws<ArgumentException>(() => LaunchOverrides.Apply(new ItemOverride(), item,
                new LaunchOverrideChange { SetTarget = true, Target = Path.Combine(link, "tool.exe") }, Fs()));
        }
        finally
        {
            Directory.Delete(link);
        }

        // After the approval: a real folder "bin" swapped for a junction to elsewhere stops the start.
        var bin = Directory.CreateDirectory(Path.Combine(dir, "bin")).FullName;
        var target = Path.Combine(bin, "tool.exe");
        File.WriteAllText(target, "");
        var o = new ItemOverride();
        LaunchOverrides.Apply(o, item, new LaunchOverrideChange { SetTarget = true, Target = target }, Fs());
        Assert.Equal(target, LaunchOverrides.Effective(item, o, Fs()).Target);
        Directory.Delete(bin, recursive: true);
        Links.Junction(bin, elsewhere);
        try
        {
            Assert.True(File.Exists(target));
            Assert.Throws<InvalidOperationException>(() => LaunchOverrides.Effective(item, o, Fs()));
        }
        finally
        {
            Directory.Delete(bin);
        }
    }

    [Fact]
    public async Task The_library_keeps_the_entry_and_stores_the_override()
    {
        var (dir, exe, bat, _) = GameFolder();
        var configDir = Directory.CreateTempSubdirectory("couchhud-launchcfg-").FullName;
        var config = new ConfigStore(configDir);
        var item = Folder(dir, exe);
        var service = new LibraryService(config, new ArtworkResolver(new HttpClient(), Path.Combine(configDir, "art.json"), null), [new FixedProvider(item)]);
        await service.ScanAsync();

        var view = service.SetLaunchOverride(item.Id, new LaunchOverrideChange { SetTarget = true, Target = bat }, Fs());
        Assert.Equal(bat, view.Target);
        Assert.True(view.Custom);
        Assert.Equal(bat, config.Current.Overrides[item.Id].LaunchTarget);
        // The entry is the same entry: launch, install folder and id as scanned.
        var found = service.Find(item.Id)!;
        Assert.Equal(exe, found.Launch.Target);
        Assert.Equal(dir, found.InstallDir);
        Assert.Equal(bat, service.EffectiveLaunch(found, Fs()).Target);
        Assert.Equal(Parental.ParentalService.LimitKey(item), Parental.ParentalService.LimitKey(found));

        // A failed change leaves everything as it was.
        Assert.Throws<ArgumentException>(() => service.SetLaunchOverride(item.Id, new LaunchOverrideChange { SetArgs = true, Args = "a & b" }, Fs()));
        Assert.Null(config.Current.Overrides[item.Id].LaunchArgs);

        service.SetLaunchOverride(item.Id, new LaunchOverrideChange { SetTarget = true, Target = "" }, Fs());
        Assert.False(config.Current.Overrides.ContainsKey(item.Id));
    }

    private sealed class FixedProvider(params LibraryItem[] items) : ILibraryProvider
    {
        public string Name => "fixed";
        public Task<IReadOnlyList<LibraryItem>> GetItemsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<LibraryItem>>(items);
    }
}
