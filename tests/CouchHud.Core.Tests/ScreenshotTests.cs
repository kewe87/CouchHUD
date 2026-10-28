using CouchHud.Core.Config;
using CouchHud.Core.Files;
using CouchHud.Core.Guard;
using Xunit;

namespace CouchHud.Core.Tests;

/// <summary>
/// Screenshots with the controller: the file name from a game's name (never a path, a stream or a device), the folder below the profile
/// (no junction out of it), the Guide + Y chord, the throttle and the refusal while a code or a secret is on screen.
/// </summary>
public sealed class ScreenshotTests : IDisposable
{
    private readonly string _profile = Directory.CreateTempSubdirectory("couchhud-shots-").FullName;
    private static readonly DateTime When = new(2026, 10, 27, 20, 15, 3, DateTimeKind.Local);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_profile, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Theory]
    [InlineData(null, "CouchHUD")]
    [InlineData("", "CouchHUD")]
    [InlineData("   ", "CouchHUD")]
    [InlineData("Hades II", "Hades II")]
    [InlineData("Baldur's Gate 3", "Baldur's Gate 3")]
    [InlineData("Half-Life 2: Episode One", "Half-Life 2_ Episode One")]
    [InlineData("..\\..\\Windows\\System32\\evil", "Windows_System32_evil")]
    [InlineData("../../etc/passwd", "etc_passwd")]
    [InlineData("C:\\Users\\x", "C_Users_x")]
    [InlineData("game.exe:stream", "game.exe_stream")]
    [InlineData("a*b?c\"d<e>f|g", "a_b_c_d_e_f_g")]
    [InlineData("..", "CouchHUD")]
    [InlineData(" . ", "CouchHUD")]
    [InlineData("///", "CouchHUD")]
    [InlineData("Pokémon", "Pokémon")]
    [InlineData("ゼルダの伝説", "ゼルダの伝説")]
    [InlineData("Game\tName\r\n", "Game_Name")]
    [InlineData("name\u202Egnp.exe", "name_gnp.exe")]
    [InlineData("trailing dots...", "trailing dots")]
    public void SafeName_keeps_letters_and_drops_path_signs(string? name, string expected)
    {
        Assert.Equal(expected, ScreenshotFiles.SafeName(name));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("NUL")]
    [InlineData("com1")]
    [InlineData("COM1.txt")]
    [InlineData("LPT9")]
    [InlineData("AUX .x")]
    public void SafeName_never_returns_a_device_name(string name)
    {
        var safe = ScreenshotFiles.SafeName(name);
        Assert.StartsWith("_", safe);
        // And the whole file name is no device either.
        var file = ScreenshotFiles.FileName(name, When);
        Assert.StartsWith("_", file);
    }

    [Fact]
    public void SafeName_is_bounded_and_never_cuts_a_surrogate_pair()
    {
        Assert.Equal(ScreenshotFiles.MaxNameLength, ScreenshotFiles.SafeName(new string('x', 500)).Length);
        // 59 letters, then letters outside the BMP: the 60th char would be the high half of a pair.
        var name = new string('a', 59) + "\U0001D400\U0001D400";
        var safe = ScreenshotFiles.SafeName(name);
        Assert.True(safe.Length <= ScreenshotFiles.MaxNameLength);
        Assert.False(char.IsHighSurrogate(safe[^1]));
        Assert.Equal(new string('a', 59), safe);
        // Signs that are dropped can leave a device name's neighbours, never the device: "LPT¹" and "CONIN$" end as plain words.
        Assert.Equal("LPT", ScreenshotFiles.SafeName("LPT¹"));
        Assert.Equal("CONIN", ScreenshotFiles.SafeName("CONIN$"));
        // A broken surrogate on its own becomes "_" (and is trimmed at the end).
        Assert.Equal("ab", ScreenshotFiles.SafeName("ab\uD800"));
    }

    [Fact]
    public void SafeName_has_no_path_or_invalid_file_name_signs()
    {
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var name in new[] { "a/b\\c:d", "x\0y", "\u0001\u001F", "<script>", "%TEMP%", "~1", "a\u200Bb" })
        {
            var safe = ScreenshotFiles.SafeName(name);
            Assert.DoesNotContain(safe, c => invalid.Contains(c));
            Assert.DoesNotContain("..", safe.Trim('.'));
        }
    }

    [Fact]
    public void FileName_has_the_time_and_a_counter_from_the_second_try()
    {
        Assert.Equal("Hades II_2026-10-27_20-15-03.png", ScreenshotFiles.FileName("Hades II", When));
        Assert.Equal("CouchHUD_2026-10-27_20-15-03_2.png", ScreenshotFiles.FileName(null, When, 2));
    }

    [Fact]
    public void Folder_is_Pictures_CouchHUD_Screenshots_below_the_profile()
    {
        Assert.Equal(Path.Combine(_profile, "Pictures", "CouchHUD", "Screenshots"), ScreenshotFiles.Folder(_profile));
    }

    [Fact]
    public void Save_makes_the_folder_and_never_overwrites()
    {
        var first = ScreenshotFiles.Save(_profile, "Hades II", When, s => s.WriteByte(1));
        var second = ScreenshotFiles.Save(_profile, "Hades II", When, s => s.WriteByte(2));
        Assert.Equal(Path.Combine(ScreenshotFiles.Folder(_profile), "Hades II_2026-10-27_20-15-03.png"), first);
        Assert.EndsWith("_2.png", second);
        Assert.Equal([1], File.ReadAllBytes(first));
        Assert.Equal([2], File.ReadAllBytes(second));
    }

    [Fact]
    public void Save_with_a_hostile_name_stays_in_the_folder()
    {
        var path = ScreenshotFiles.Save(_profile, "..\\..\\..\\evil", When, s => s.WriteByte(1));
        Assert.Equal(ScreenshotFiles.Folder(_profile), Path.GetDirectoryName(path));
    }

    [Fact]
    public void Save_removes_the_file_when_writing_fails()
    {
        Assert.Throws<InvalidOperationException>(() => ScreenshotFiles.Save(_profile, "x", When, _ => throw new InvalidOperationException("encoder")));
        Assert.Empty(Directory.GetFiles(ScreenshotFiles.Folder(_profile)));
    }

    [Fact]
    public void Save_removes_a_half_written_file_by_its_handle()
    {
        // Something already written, then the encoder fails: the file goes with the handle, no name is opened again.
        Assert.Throws<IOException>(() => ScreenshotFiles.Save(_profile, "x", When, s =>
        {
            s.Write(new byte[4096]);
            throw new IOException("disk");
        }));
        Assert.Empty(Directory.GetFiles(ScreenshotFiles.Folder(_profile)));
        // A good save afterwards keeps its file (the delete-on-close mark is taken off).
        var path = ScreenshotFiles.Save(_profile, "x", When, s => s.WriteByte(1));
        Assert.NotNull(path);
        Assert.Equal(1, new FileInfo(path!).Length);
    }

    [Fact]
    public void Save_gives_up_after_the_last_free_name()
    {
        for (var i = 0; i < ScreenshotFiles.MaxAttempts; i++) ScreenshotFiles.Save(_profile, "x", When, s => s.WriteByte(0));
        Assert.Throws<IOException>(() => ScreenshotFiles.Save(_profile, "x", When, s => s.WriteByte(0)));
    }

    [JunctionFact]
    public void Save_refuses_a_junction_in_place_of_the_screenshots_folder()
    {
        var outside = Directory.CreateTempSubdirectory("couchhud-shots-out-").FullName;
        try
        {
            var parent = Path.GetDirectoryName(ScreenshotFiles.Folder(_profile))!;
            Directory.CreateDirectory(parent);
            Links.Junction(ScreenshotFiles.Folder(_profile), outside);
            Assert.Throws<IOException>(() => ScreenshotFiles.Save(_profile, "x", When, s => s.WriteByte(1)));
            Assert.Empty(Directory.GetFileSystemEntries(outside));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [JunctionFact]
    public void Save_refuses_a_junction_in_place_of_Pictures()
    {
        var outside = Directory.CreateTempSubdirectory("couchhud-shots-out-").FullName;
        try
        {
            Links.Junction(Path.Combine(_profile, "Pictures"), outside);
            Assert.Throws<IOException>(() => ScreenshotFiles.Save(_profile, "x", When, s => s.WriteByte(1)));
            // Nothing was made behind the link.
            Assert.Empty(Directory.GetFileSystemEntries(outside));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void Guide_tap_without_chord_keeps_its_action_for_the_release()
    {
        var chord = new GuideChord();
        chord.GuideDown();
        Assert.True(chord.Held);
        Assert.True(chord.GuideUp());
        Assert.False(chord.Held);
    }

    [Fact]
    public void Guide_and_Y_makes_the_chord_and_swallows_the_tap()
    {
        var chord = new GuideChord();
        chord.GuideDown();
        Assert.True(chord.ButtonDown("y", guideStillPressed: true));
        Assert.False(chord.GuideUp());
    }

    [Theory]
    [InlineData("a")]
    [InlineData("x")]
    [InlineData("rb")]
    [InlineData("start")]
    public void Other_buttons_make_no_chord(string button)
    {
        var chord = new GuideChord();
        chord.GuideDown();
        Assert.False(chord.ButtonDown(button, guideStillPressed: true));
        Assert.True(chord.GuideUp());
    }

    [Fact]
    public void Y_without_Guide_makes_no_chord()
    {
        var chord = new GuideChord();
        Assert.False(chord.ButtonDown("y", guideStillPressed: false));
        Assert.False(chord.ButtonDown("y", guideStillPressed: true));
        // Guide went up unseen (a missed event): the pad says it is not pressed.
        chord.GuideDown();
        Assert.False(chord.ButtonDown("y", guideStillPressed: false));
    }

    [Fact]
    public void Cancel_leaves_nothing_for_the_release()
    {
        var chord = new GuideChord();
        chord.GuideDown();
        chord.Cancel();
        Assert.False(chord.GuideUp());
        Assert.False(chord.ButtonDown("y", guideStillPressed: true));
    }

    [Fact]
    public void Throttle_allows_one_per_second_and_never_two_at_once()
    {
        var throttle = new ScreenshotThrottle(TimeSpan.FromSeconds(1));
        var t0 = new DateTimeOffset(2026, 10, 27, 20, 0, 0, TimeSpan.Zero);
        Assert.True(throttle.TryBegin(t0));
        // Still running.
        Assert.False(throttle.TryBegin(t0.AddSeconds(5)));
        throttle.End();
        Assert.False(throttle.TryBegin(t0.AddMilliseconds(400)));
        Assert.False(throttle.TryBegin(t0.AddMilliseconds(999)));
        Assert.True(throttle.TryBegin(t0.AddSeconds(1)));
        throttle.End();
        // A clock that went back never locks it for good.
        Assert.True(throttle.TryBegin(t0.AddSeconds(-30)));
    }

    [Theory]
    [InlineData(false, false, false, false, ScreenshotRefusal.None)]
    [InlineData(true, false, false, false, ScreenshotRefusal.CodeEntry)]
    [InlineData(false, true, false, false, ScreenshotRefusal.CodeEntry)]
    [InlineData(false, false, true, false, ScreenshotRefusal.SecretField)]
    [InlineData(true, false, true, false, ScreenshotRefusal.CodeEntry)]
    [InlineData(false, false, false, true, ScreenshotRefusal.PairingCode)]
    [InlineData(true, false, false, true, ScreenshotRefusal.CodeEntry)]
    public void Screenshots_are_refused_while_a_code_or_secret_is_shown(bool codeEntry, bool hostPrompt, bool secret, bool pairing, ScreenshotRefusal expected)
    {
        Assert.Equal(expected, ScreenshotRules.Check(codeEntry, hostPrompt, secret, pairing));
    }

    [Fact]
    public void The_code_entry_and_the_pairing_code_can_report_themselves_on_a_locked_couch_and_a_waiting_profile()
    {
        Assert.Contains("codeEntryState", LockPolicy.OpenWhileLocked);
        Assert.Contains("codeEntryState", ProfilePolicy.OpenWhileHeld);
        Assert.Contains("pairingState", LockPolicy.OpenWhileLocked);
        Assert.Contains("pairingState", ProfilePolicy.OpenWhileHeld);
    }

    [Fact]
    public void The_chord_is_on_by_default()
    {
        Assert.True(new AppConfig().ScreenshotCombo);
    }

    [Fact]
    public void An_unknown_screen_refuses_after_the_codes_say_why()
    {
        Assert.Equal(ScreenshotRefusal.UiUnknown, ScreenshotRules.Check(false, false, false, false, uiKnown: false));
        // A known reason comes first: it says more.
        Assert.Equal(ScreenshotRefusal.CodeEntry, ScreenshotRules.Check(true, false, false, false, uiKnown: false));
        Assert.Equal(ScreenshotRefusal.PairingCode, ScreenshotRules.Check(false, false, false, true, uiKnown: false));
    }

    [Fact]
    public void A_fresh_page_is_unknown_until_it_reported_both_states()
    {
        var screen = new ScreenshotScreenState();
        Assert.False(screen.Known);
        screen.CodeEntry(false);
        Assert.False(screen.Known);
        screen.Pairing(false);
        Assert.True(screen.Known);
        // A reload: the reports of the old page are void, and nothing is known again.
        screen.Pairing(true);
        screen.Reset();
        Assert.False(screen.Known);
        Assert.False(screen.PairingShown);
        screen.Pairing(false);
        Assert.False(screen.Known);
    }

    [Fact]
    public void The_serial_moves_when_something_comes_up_and_with_a_new_page_only()
    {
        var screen = new ScreenshotScreenState();
        screen.CodeEntry(false);
        screen.Pairing(false);
        var serial = screen.Serial;
        screen.Pairing(false);
        screen.CodeEntry(false);
        Assert.Equal(serial, screen.Serial);
        // A code entry that came and went during a capture still moved it.
        screen.CodeEntry(true);
        screen.CodeEntry(false);
        Assert.NotEqual(serial, screen.Serial);
        serial = screen.Serial;
        screen.Pairing(true);
        Assert.NotEqual(serial, screen.Serial);
        // The page repeating the same report moves nothing.
        serial = screen.Serial;
        screen.Pairing(true);
        Assert.Equal(serial, screen.Serial);
        screen.Reset();
        Assert.NotEqual(serial, screen.Serial);
    }

    [Fact]
    public void The_burst_cap_stops_a_turbo_button_and_says_so_once()
    {
        var cap = new ScreenshotBurstCap(3, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10));
        var t0 = new DateTimeOffset(2026, 10, 27, 20, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 3; i++) Assert.True(cap.TryTake(t0.AddSeconds(i), out _));
        Assert.False(cap.TryTake(t0.AddSeconds(3), out var say));
        Assert.True(say);
        // The button stays held (one press every two seconds): refused silently, also once the window would have room again
        // (VM test 0.5: refusals and screenshots alternated and the note came three times in 26 tries).
        var said = 0;
        for (var t = 5.0; t <= 90; t += 2)
        {
            Assert.False(cap.TryTake(t0.AddSeconds(t), out say));
            if (say) said++;
        }
        Assert.Equal(0, said);
        // Ten quiet seconds end the burst: the window has room, so one fits again.
        Assert.True(cap.TryTake(t0.AddSeconds(101), out _));
        Assert.True(cap.TryTake(t0.AddSeconds(102), out _));
        Assert.True(cap.TryTake(t0.AddSeconds(103), out _));
        // The next burst gets its note again, once.
        Assert.False(cap.TryTake(t0.AddSeconds(104), out say));
        Assert.True(say);
        Assert.False(cap.TryTake(t0.AddSeconds(105), out say));
        Assert.False(say);
        // A clock that went back never locks it for good.
        Assert.True(cap.TryTake(t0.AddSeconds(-30), out _));
        Assert.Equal(20, ScreenshotBurstCap.Default().Max);
    }

    [Fact]
    public void After_a_quiet_gap_a_full_window_still_refuses_and_says_so_again()
    {
        var cap = new ScreenshotBurstCap(2, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10));
        var t0 = new DateTimeOffset(2026, 10, 27, 20, 0, 0, TimeSpan.Zero);
        Assert.True(cap.TryTake(t0, out _));
        Assert.True(cap.TryTake(t0.AddSeconds(1), out _));
        Assert.False(cap.TryTake(t0.AddSeconds(2), out var say));
        Assert.True(say);
        // A pause, but the window is still full: the cap holds (a new burst, one note).
        Assert.False(cap.TryTake(t0.AddSeconds(20), out say));
        Assert.True(say);
        Assert.False(cap.TryTake(t0.AddSeconds(21), out say));
        Assert.False(say);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(0L, false)]
    [InlineData((1L << 30) - 1, false)]
    [InlineData(1L << 30, true)]
    [InlineData(long.MaxValue, true)]
    public void Below_one_gigabyte_free_or_unknown_no_screenshot_is_written(long? free, bool enough)
    {
        Assert.Equal(enough, ScreenshotRules.EnoughSpace(free));
    }

    [Fact]
    public void Free_space_of_the_profile_drive_is_known()
    {
        Assert.True(ScreenshotFiles.FreeBytes(_profile) is > 0);
    }
}
