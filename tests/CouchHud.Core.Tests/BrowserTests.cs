using CouchHud.Core.Browser;
using CouchHud.Core.Input;

namespace CouchHud.Core.Tests;

public sealed class BrowserAddressTests
{
    [Theory]
    [InlineData("heise.de", "https://heise.de/")]
    [InlineData("www.example.org/path?q=1", "https://www.example.org/path?q=1")]
    [InlineData("localhost:3000", "https://localhost:3000/")]
    [InlineData("192.168.1.1", "https://192.168.1.1/")]
    [InlineData("[::1]:8080", "https://[::1]:8080/")]
    [InlineData("xn--mnchen-3ya.de", "https://xn--mnchen-3ya.de/")]
    [InlineData("  example.com  ", "https://example.com/")]
    public void TypedHostsOpenWithHttpsGuessed(string text, string url)
    {
        var target = BrowserAddress.Parse(text);
        Assert.Equal(url, target.Url);
        Assert.True(target.Guessed);
        Assert.False(target.Search);
    }

    [Theory]
    [InlineData("https://example.com/a b", "https://example.com/a%20b")]
    [InlineData("http://example.com", "http://example.com/")]
    [InlineData("HTTPS://Example.com/x", "https://example.com/x")]
    public void FullAddressesOpenAsTheyAre(string text, string url)
    {
        var target = BrowserAddress.Parse(text);
        Assert.Equal(url, target.Url);
        Assert.False(target.Guessed);
        Assert.False(target.Search);
    }

    [Theory]
    [InlineData("elden ring tipps")]
    [InlineData("1.10")]
    [InlineData("3.5")]
    [InlineData("999.1.1.1")]
    [InlineData("name@example.com")]
    [InlineData("user:pass@example.com")]
    [InlineData("https://user:pass@example.com/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("ms-settings:display")]
    [InlineData("steam://run/123")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("/etc/passwd")]
    [InlineData("example")]
    [InlineData("-bad-.com")]
    [InlineData("localhost:99999")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    public void EverythingElseIsASearch(string text)
    {
        var target = BrowserAddress.Parse(text);
        Assert.True(target.Search);
        Assert.StartsWith("https://duckduckgo.com/?q=", target.Url);
        Assert.Equal(text.Trim(), Uri.UnescapeDataString(target.Url["https://duckduckgo.com/?q=".Length..]));
    }

    [Theory]
    [InlineData("!w Couch", "w Couch")]
    [InlineData("\\elden ring", "elden ring")]
    [InlineData("!!\\ x", "x")]
    [InlineData("a !w", "a !w")]
    public void ASearchNeverStartsWithAJumpSign(string text, string query)
    {
        var target = BrowserAddress.Parse(text);
        Assert.True(target.Search);
        Assert.Equal(query, Uri.UnescapeDataString(target.Url["https://duckduckgo.com/?q=".Length..]));
    }

    [Fact]
    public void OnlyJumpSignsOpenTheStartPage() => Assert.Equal(BrowserAddress.Home, BrowserAddress.Parse("!\\").Url);

    [Theory]
    [InlineData("\\\\server\\share", true)]
    [InlineData("  \\\\nas\\filme", true)]
    [InlineData("C:\\Windows", true)]
    [InlineData("d:/daten", true)]
    [InlineData("example.com", false)]
    [InlineData("c: drive", false)]
    [InlineData("\\elden", false)]
    [InlineData(null, false)]
    public void WindowsPathsAreRecognised(string? text, bool expected) => Assert.Equal(expected, BrowserAddress.IsLocalPath(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NothingTypedOpensTheStartPage(string? text) => Assert.Equal(BrowserAddress.Home, BrowserAddress.Parse(text).Url);

    [Fact]
    public void ResultIsAlwaysHttpOrHttps()
    {
        foreach (var text in new[] { "vbscript:x", "about:blank", "chrome://settings", "edge://crash", "view-source:https://a.de", "blob:https://a.de/x" })
        {
            var uri = new Uri(BrowserAddress.Parse(text).Url);
            Assert.Equal("https", uri.Scheme);
            Assert.Equal("duckduckgo.com", uri.Host);
        }
    }

    [Fact]
    public void OverlongTextIsCut()
    {
        var target = BrowserAddress.Parse(new string('a', 5000) + " b");
        Assert.True(target.Search);
        Assert.True(target.Url.Length < 2100);
    }
}

public sealed class BrowserInputTests
{
    private static readonly BrowserPadContext Page = new(TvMode: false, FullScreen: false, CanGoBack: true);
    private static readonly BrowserPadContext FirstPage = Page with { CanGoBack = false };
    private static readonly BrowserPadContext Full = Page with { FullScreen = true };
    private static readonly BrowserPadContext Tv = new(TvMode: true, FullScreen: true, CanGoBack: true);

    public static TheoryData<BrowserPadContext> AllContexts => new() { Page, FirstPage, Full, Tv, Tv with { FullScreen = false } };

    [Theory]
    [MemberData(nameof(AllContexts))]
    public void GuideAlwaysLeaves(BrowserPadContext ctx)
    {
        Assert.Equal(BrowserPadAction.LeaveToHome, BrowserInput.Route("guide", false, ctx));
        Assert.Equal(BrowserPadAction.LeaveToHome, BrowserInput.Route("guide", true, ctx));
    }

    [Theory]
    [MemberData(nameof(AllContexts))]
    public void ViewAndMenuAlwaysGiveTheControllerBackToTheHud(BrowserPadContext ctx)
    {
        foreach (var button in new[] { "back", "start" })
        {
            var action = BrowserInput.Route(button, false, ctx);
            Assert.Equal(ctx.FullScreen ? BrowserPadAction.ExitFullScreen : BrowserPadAction.ToBar, action);
        }
    }

    [Theory]
    [MemberData(nameof(AllContexts))]
    public void BAlwaysHasAWayOut(BrowserPadContext ctx)
    {
        var action = BrowserInput.Route("b", false, ctx);
        if (ctx.TvMode) Assert.Equal(BrowserPadAction.TvKey, action);
        else if (ctx.FullScreen) Assert.Equal(BrowserPadAction.ExitFullScreen, action);
        else Assert.Equal(ctx.CanGoBack ? BrowserPadAction.GoBack : BrowserPadAction.Exit, action);
    }

    [Theory]
    [InlineData("up")]
    [InlineData("down")]
    [InlineData("left")]
    [InlineData("right")]
    public void TheStickAsCursorIsNoButton(string direction)
    {
        Assert.Equal(BrowserPadAction.Ignore, BrowserInput.Route(direction, true, Page));
        Assert.Equal(BrowserPadAction.Ignore, BrowserInput.Route(direction, true, Tv));
    }

    [Theory]
    [InlineData("up", BrowserPadAction.ArrowUp)]
    [InlineData("down", BrowserPadAction.ArrowDown)]
    [InlineData("left", BrowserPadAction.ArrowLeft)]
    [InlineData("right", BrowserPadAction.ArrowRight)]
    [InlineData("a", BrowserPadAction.Click)]
    [InlineData("x", BrowserPadAction.TypeText)]
    [InlineData("y", BrowserPadAction.Address)]
    [InlineData("lb", BrowserPadAction.ZoomOut)]
    [InlineData("rb", BrowserPadAction.ZoomIn)]
    [InlineData("rt", BrowserPadAction.Enter)]
    [InlineData("lt", BrowserPadAction.Tab)]
    [InlineData("ls", BrowserPadAction.Ignore)]
    [InlineData("rs", BrowserPadAction.Ignore)]
    [InlineData("unknown", BrowserPadAction.Ignore)]
    public void EveryButtonHasAnExplicitMeaning(string button, BrowserPadAction expected) =>
        Assert.Equal(expected, BrowserInput.Route(button, false, Page));

    [Theory]
    [InlineData("up")]
    [InlineData("right")]
    [InlineData("a")]
    [InlineData("b")]
    public void TvPagesGetTheRemotesKeys(string button) => Assert.Equal(BrowserPadAction.TvKey, BrowserInput.Route(button, false, Tv));

    [Fact]
    public void NoButtonStartsAWindowsTool()
    {
        // X used to start the Windows touch keyboard (a window of its own that took the controller away).
        foreach (var button in new[] { "a", "b", "x", "y", "lb", "rb", "lt", "rt", "ls", "rs", "up", "down", "left", "right", "back", "start", "guide" })
        {
            Assert.True(Enum.IsDefined(BrowserInput.Route(button, false, Page)));
        }
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void HudKeepsInputWhileItsOwnPopupIsInFront(bool active, bool ownPopup, bool expected) =>
        Assert.Equal(expected, BrowserInput.HudHasInput(active, ownPopup));
}

public sealed class BrowserPolicyTests
{
    [Theory]
    [InlineData("https://example.com/", true)]
    [InlineData("http://example.com/", true)]
    [InlineData("about:blank", true)]
    [InlineData("data:text/html,x", true)]
    [InlineData("blob:https://example.com/123", true)]
    [InlineData("chrome-error://chromewebdata/", true)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    [InlineData("ms-settings:display", false)]
    [InlineData("steam://run/1", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("edge://settings", false)]
    [InlineData("chrome://settings", false)]
    [InlineData("mailto:a@b.de", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyWebSchemesAreNavigable(string? url, bool expected) => Assert.Equal(expected, BrowserPolicy.IsNavigable(url));

    [Theory]
    [InlineData("https://example.com/x", true, "https://example.com/x")]
    [InlineData("https://example.com/x", false, null)]
    [InlineData("http://example.com/", true, "http://example.com/")]
    [InlineData("https://user:pw@example.com/", true, null)]
    [InlineData("file:///C:/x", true, null)]
    [InlineData("javascript:alert(1)", true, null)]
    [InlineData("about:blank", true, null)]
    [InlineData("", true, null)]
    public void PopupsOpenInThePageOnlyAfterAClick(string url, bool userInitiated, string? expected) =>
        Assert.Equal(expected, BrowserPolicy.PopupTarget(url, userInitiated));

    [Theory]
    [InlineData("Camera")]
    [InlineData("Microphone")]
    [InlineData("Geolocation")]
    [InlineData("Notifications")]
    [InlineData("ClipboardRead")]
    [InlineData("UnknownPermission")]
    public void NoPermissionIsGranted(string kind) => Assert.False(BrowserPolicy.AllowPermission(kind));

    [Fact]
    public void DialogTextLosesControlAndDirectionCharacters()
    {
        Assert.Equal("Hello world", BrowserPolicy.DialogText("Hello\u0000\u0007   world"));
        Assert.Equal("evil gpj.exe", BrowserPolicy.DialogText("evil \u202Egpj.exe"));
        Assert.Equal("a b", BrowserPolicy.DialogText("a\u2066\u2069b"));
        Assert.Equal("", BrowserPolicy.DialogText(null));
        Assert.Equal("", BrowserPolicy.DialogText(" \t "));
    }

    [Fact]
    public void DialogTextIsCut()
    {
        var text = BrowserPolicy.DialogText(new string('x', 1000));
        Assert.Equal(BrowserPolicy.MaxDialogText, text.Length);
        Assert.EndsWith("…", text);
        Assert.Equal("short", BrowserPolicy.DialogText("short"));
    }

    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("..\\..\\Windows\\System32\\evil.dll", "evil.dll")]
    [InlineData("../../.bashrc", "bashrc")]
    [InlineData("C:\\Users\\x\\a.txt", "a.txt")]
    [InlineData("a:b*c?.txt", "a_b_c_.txt")]
    [InlineData("invoice\u202Etxt.exe", "invoicetxt.exe")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("nul", "_nul")]
    [InlineData("file. . .", "file")]
    [InlineData("", "download")]
    [InlineData(null, "download")]
    [InlineData("...", "download")]
    public void DownloadNamesAreSafe(string? suggested, string expected) => Assert.Equal(expected, BrowserPolicy.SafeFileName(suggested));

    [Theory]
    [InlineData("setup.exe")]
    [InlineData("SETUP.EXE")]
    [InlineData("invoice.pdf.exe")]
    [InlineData("invoice‮fdp.exe")]
    [InlineData("run.bat")]
    [InlineData("run.cmd")]
    [InlineData("x.ps1")]
    [InlineData("x.vbs")]
    [InlineData("x.js")]
    [InlineData("x.hta")]
    [InlineData("x.scr")]
    [InlineData("x.msi")]
    [InlineData("x.msix")]
    [InlineData("x.appinstaller")]
    [InlineData("x.lnk")]
    [InlineData("x.url")]
    [InlineData("x.scf")]
    [InlineData("x.library-ms")]
    [InlineData("x.searchConnector-ms")]
    [InlineData("x.settingcontent-ms")]
    [InlineData("x.appref-ms")]
    [InlineData("x.application")]
    [InlineData("x.jar")]
    [InlineData("x.reg")]
    [InlineData("x.cpl")]
    [InlineData("x.iso")]
    [InlineData("x.vhdx")]
    [InlineData("x.exe.")]
    [InlineData("x.exe ")]
    [InlineData("..\\x.exe")]
    public void ProgramsAndShortcutsAreNeverDownloaded(string name) => Assert.True(BrowserPolicy.IsDangerousDownload(name));

    [Theory]
    [InlineData("report.pdf")]
    [InlineData("photo.jpg")]
    [InlineData("archive.zip")]
    [InlineData("notes.txt")]
    [InlineData("movie.mp4")]
    [InlineData("README")]
    [InlineData("")]
    public void DocumentsMayBeDownloaded(string name) => Assert.False(BrowserPolicy.IsDangerousDownload(name));

    [Theory]
    [InlineData("Report.PDF", "pdf")]
    [InlineData("archive.tar.gz", "gz")]
    [InlineData("README", "")]
    [InlineData("a‮txt.zip", "zip")]
    public void TheFileTypeIsShownOnItsOwn(string name, string ext) => Assert.Equal(ext, BrowserPolicy.DownloadExtension(name));

    [Fact]
    public void RenamedDownloadsAreJudgedByTheirRealName()
    {
        Assert.True(BrowserPolicy.IsDangerousDownload("x.scf.download", "", "https://e.example/get"));
        Assert.True(BrowserPolicy.IsDangerousDownload("Unconfirmed 1234.crdownload", "", "https://e.example/files/link.lnk"));
        Assert.True(BrowserPolicy.IsDangerousDownload("report.pdf", "attachment; filename=\"setup.exe\"", "https://e.example/r"));
        Assert.True(BrowserPolicy.IsDangerousDownload("a.txt", "attachment; filename*=UTF-8''evil%2Escf", "https://e.example/a.txt"));
        Assert.False(BrowserPolicy.IsDangerousDownload("film.zip", "attachment; filename=film.zip", "https://e.example/film.zip"));
        Assert.Equal("x.scf", BrowserPolicy.DownloadName("x.scf.download", "", ""));
        Assert.Equal("link.lnk", BrowserPolicy.DownloadName("Unconfirmed 1234.crdownload", null, "https://e.example/files/link.lnk"));
        Assert.Equal("Bericht ä.pdf", BrowserPolicy.DownloadName("r.bin", "attachment; filename=\"r.pdf\"; filename*=UTF-8''Bericht%20%C3%A4.pdf", ""));
    }

    [Theory]
    [InlineData("dark.theme")]
    [InlineData("x.themepack")]
    [InlineData("x.deskthemepack")]
    [InlineData("office.rdp")]
    [InlineData("kb.msu")]
    [InlineData("x.cab")]
    [InlineData("root.cer")]
    [InlineData("root.crt")]
    [InlineData("root.der")]
    [InlineData("chain.p7b")]
    [InlineData("db.ade")]
    [InlineData("db.adp")]
    [InlineData("db.mdb")]
    [InlineData("db.mde")]
    [InlineData("db.accde")]
    [InlineData("box.wsb")]
    [InlineData("q.iqy")]
    [InlineData("s.slk")]
    [InlineData("x.appcontent-ms")]
    [InlineData("t.diagpkg")]
    [InlineData("page.mht")]
    [InlineData("page.mhtml")]
    [InlineData("x.settingcontent-ms")]
    [InlineData("x.library-ms")]
    [InlineData("x.searchconnector-ms")]
    public void ThemesRemoteDesktopCertificatesAndDatabasesAreNeverDownloaded(string name) => Assert.True(BrowserPolicy.IsDangerousDownload(name));

    [Fact]
    public void LongNamesKeepTheirExtension()
    {
        var name = BrowserPolicy.SafeFileName(new string('a', 400) + ".zip");
        Assert.Equal(150, name.Length);
        Assert.EndsWith(".zip", name);
    }

    [Fact]
    public void DownloadsStayInTheFolderAndNeverOverwrite()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dl");
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine(dir, "a.zip"), Path.Combine(dir, "a (2).zip") };
        var path = BrowserPolicy.DownloadPath(dir, "..\\a.zip", taken.Contains);
        Assert.Equal(Path.Combine(dir, "a (3).zip"), path);
        Assert.Equal(dir, Path.GetDirectoryName(path));
        Assert.Equal(Path.Combine(dir, "b.txt"), BrowserPolicy.DownloadPath(dir, "b.txt", _ => false));
    }

    [Theory]
    [InlineData("OperationCanceled", null)]
    [InlineData("HostNameNotResolved", "browser.error.dns")]
    [InlineData("Timeout", "browser.error.timeout")]
    [InlineData("Disconnected", "browser.error.offline")]
    [InlineData("CannotConnect", "browser.error.connect")]
    [InlineData("ConnectionReset", "browser.error.connect")]
    [InlineData("ValidAuthenticationCredentialsRequired", "browser.error.auth")]
    [InlineData("RedirectFailed", "browser.error.redirect")]
    [InlineData("CertificateCommonNameIsIncorrect", "browser.error.certificate")]
    [InlineData("CertificateExpired", "browser.error.certificate")]
    [InlineData("ClientCertificateContainsErrors", "browser.error.certificate")]
    [InlineData("Unknown", "browser.error.generic")]
    public void FailedLoadsGetATextKey(string status, string? key) => Assert.Equal(key, BrowserPolicy.ErrorKey(status));

    [Fact]
    public void ErrorKeysExistInBothLanguages()
    {
        foreach (var status in new[] { "HostNameNotResolved", "Timeout", "Disconnected", "CannotConnect", "ValidAuthenticationCredentialsRequired", "RedirectFailed", "CertificateExpired", "Unknown" })
        {
            var key = BrowserPolicy.ErrorKey(status)!;
            Assert.True(CouchHud.Core.Localization.Strings.German.ContainsKey(key), key);
            Assert.True(CouchHud.Core.Localization.Strings.English.ContainsKey(key), key);
        }
    }

    [Fact]
    public void ErrorPageEscapesEverythingAndRunsNoScript()
    {
        var html = BrowserPolicy.ErrorPage("<script>alert(1)</script>", "msg\"><img src=x onerror=alert(1)>",
            "https://evil.example/\"><script>x</script>", "hint<b>", "de\"><script>");
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<b>", html);
        Assert.DoesNotContain("<a ", html);
        Assert.Contains("default-src 'none'", html);
        Assert.Contains("&lt;script&gt;", html);
    }
}

public sealed class DialogGateTests
{
    [Fact]
    public void ADialogLoopIsStoppedForAWhile()
    {
        var gate = new DialogGate();
        var t0 = DateTimeOffset.UnixEpoch;
        for (var i = 0; i < DialogGate.Burst; i++)
        {
            Assert.True(gate.Allow(t0.AddSeconds(i), out var blocked));
            Assert.False(blocked);
        }
        Assert.False(gate.Allow(t0.AddSeconds(3), out var first));
        Assert.True(first);
        Assert.False(gate.Allow(t0.AddSeconds(4), out var second));
        Assert.False(second);
        Assert.True(gate.Allow(t0.AddSeconds(3) + DialogGate.Pause + TimeSpan.FromSeconds(1), out _));
    }

    [Fact]
    public void ASynchronousLoopIsStoppedToo()
    {
        // alert() in a loop: each one waits for its answer, slower than the burst window, but follows it at once.
        var gate = new DialogGate();
        var t = DateTimeOffset.UnixEpoch;
        var shown = 0;
        for (var i = 0; i < 10; i++)
        {
            if (!gate.Allow(t, out _)) break;
            shown++;
            t += TimeSpan.FromSeconds(5);
            gate.Answered(t);
            t += TimeSpan.FromMilliseconds(20);
        }
        Assert.Equal(DialogGate.Burst, shown);
    }

    [Fact]
    public void ADialogRightAfterAnotherStillShows()
    {
        // alert(); confirm(): the confirm is asked, not answered "no" by itself.
        var gate = new DialogGate();
        var t = DateTimeOffset.UnixEpoch;
        Assert.True(gate.Allow(t, out _));
        gate.Answered(t.AddSeconds(3));
        Assert.True(gate.Allow(t.AddSeconds(3.1), out _));
    }

    [Fact]
    public void OccasionalDialogsAlwaysShow()
    {
        var gate = new DialogGate();
        var t = DateTimeOffset.UnixEpoch;
        for (var i = 0; i < 20; i++)
        {
            Assert.True(gate.Allow(t, out _));
            t += TimeSpan.FromSeconds(4);
        }
    }
}

public sealed class BrowserWatchdogTests
{
    [Theory]
    [InlineData(false, false, false, BrowserRepair.None)]
    [InlineData(false, true, false, BrowserRepair.None)]
    [InlineData(true, false, true, BrowserRepair.ReturnToHud)]
    [InlineData(true, false, false, BrowserRepair.ReturnToHud)]
    [InlineData(true, true, false, BrowserRepair.RestartCursor)]
    [InlineData(true, true, true, BrowserRepair.None)]
    public void ControllerIsNeverLeftWithoutAnOwner(bool browsing, bool shown, bool cursor, BrowserRepair expected) =>
        Assert.Equal(expected, BrowserWatchdog.Check(browsing, shown, cursor));
}

public sealed class PadMergerStickTests
{
    private const ushort Up = 0x0001, Down = 0x0002;

    [Fact]
    public void StickDirectionsAreToldApartFromTheDpad()
    {
        var m = new PadMerger();
        m.Update([new PadSample(0, 0, 0, 0, 30000, 0, 0)]);
        Assert.True(m.IsPressed("up"));
        Assert.True(m.FromStickOnly("up"));

        m.Update([new PadSample(Up, 0, 0, 0, 0, 0, 0)]);
        Assert.False(m.FromStickOnly("up"));

        // Two pads: one pushes the stick down, the other presses the d-pad down.
        m.Update([new PadSample(0, 0, 0, 0, -30000, 0, 0), new PadSample(Down, 0, 0, 0, 0, 0, 0)]);
        Assert.False(m.FromStickOnly("down"));
        Assert.False(m.FromStickOnly("a"));
    }
}
