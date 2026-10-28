using System.Windows.Media.Imaging;
using CouchHud.Core.Files;
using CouchHud.Core.Localization;
using CouchHud.Shell.Services;
using CouchHud.Shell.UiHost;

namespace CouchHud.Shell;

/// <summary>
/// Screenshots on the controller: Guide held + Y (also over a game, read by the background XInput polling like the Guide button
/// itself), or "Screenshot" in the guide menu (the menu is closed first; with a game the game comes to the front again first).
/// CouchHUD's own capture (<see cref="ScreenCapture"/>), saved only on this PC (<see cref="ScreenshotFiles"/>), never while a code
/// or a secret field is on screen (nor before the UI said what it shows), at most one per second and 20 a minute, never with less than
/// 1 GB free. Windows' own notifications on screen are in the picture like everything else. No pipe operation to the service, nothing for the phone, MCP, plugins
/// or the assistant: the only way in is the controller and the guide menu of the UI.
/// </summary>
public sealed partial class Hud
{
    private enum GuideTap
    {
        None,
        /// <summary>The HUD is in front: the release opens or closes the guide menu.</summary>
        Menu,
        /// <summary>A game is in front and the tap brings the HUD: on release, unless a chord or the hold came first.</summary>
        Home
    }

    private readonly GuideChord _guideChord = new();
    private GuideTap _guideTap;
    private readonly ScreenshotThrottle _shotThrottle = new(TimeSpan.FromSeconds(1));
    private readonly ScreenshotBurstCap _shotBurst = ScreenshotBurstCap.Default();
    /// <summary>What the UI reports on screen: a code entry (couch code, parents' code, profile code), the phone's pairing code.</summary>
    private readonly ScreenshotScreenState _uiScreen = new();
    /// <summary>The last button that reached the UI came from the phone (set in IRemoteTarget.Button, cleared by the controller).</summary>
    private bool _lastInputRemote;
    /// <summary>Until when the HUD's own screenshot toast may still be on screen (monotonic).</summary>
    private DateTimeOffset _shotToastUntil = DateTimeOffset.MinValue;

    public void SetCodeEntryState(bool open) => _uiScreen.CodeEntry(open);

    public void SetPairingState(bool open) => _uiScreen.Pairing(open);

    /// <summary>
    /// The UI page starts anew (navigation, a crashed renderer): what it reported on screen is gone with it. Until it reports again
    /// once loaded (both states, at start) no screenshot is taken: a lost report must not let one through (audit 0.5 A3).
    /// </summary>
    public void ResetUiScreenState() => _uiScreen.Reset();

    /// <summary>
    /// The chord is armed on the couch, not in PC mode (the Game Bar has the button there), not on the couch browser's pages (web pages
    /// may show what is nobody else's business, and Guide leads out there at once) and not in the mouse mode (launcher sign-ins).
    /// </summary>
    private bool ScreenshotComboArmed => config.Current.ScreenshotCombo && !_pcMode && !_browsing && _mouse?.Active != true;

    /// <summary>
    /// Guide went down with the chord armed. What a tap does waits for the release (a tap that becomes Guide + Y does nothing else);
    /// holding still opens the guide menu after the hold time, as before.
    /// </summary>
    private void OnGuideDownChord()
    {
        _guideChord.GuideDown();
        if (_window?.IsActive == true)
        {
            _guideTap = GuideTap.Menu;
        }
        else if (GuideTapEffective)
        {
            _guideTap = GuideTap.Home;
            OnHomeHold(() =>
            {
                _guideTap = GuideTap.None;
                _guideChord.Cancel();
                // The same as before: the home screen with the guide menu on top.
                ShowHud("home");
                Emit("nav", "quick");
            });
        }
        else
        {
            // A tap belongs to Steam overlay / Game Bar, holding brings us back.
            _guideTap = GuideTap.None;
            OnHomeHold(() =>
            {
                _guideChord.Cancel();
                ShowHud("quick");
            });
        }
    }

    /// <summary>Guide came up: its tap action, unless the chord or the hold took the press.</summary>
    private void OnGuideUp()
    {
        var tap = _guideChord.GuideUp();
        var pending = _guideTap;
        _guideTap = GuideTap.None;
        if (!tap) return;
        switch (pending)
        {
            case GuideTap.Menu when _window?.IsActive == true && !_pcMode:
                Emit("input", new { type = "down", button = "guide" });
                Emit("input", new { type = "up", button = "guide" });
                break;
            case GuideTap.Home when _window?.IsActive != true && !_pcMode:
                ShowHud("home");
                break;
        }
    }

    /// <summary>Y while Guide is held through the chord path: a screenshot instead of Y in the HUD.</summary>
    private bool TryScreenshotChord(string button)
    {
        if (!_guideChord.ButtonDown(button, pad.IsPressed("guide"))) return false;
        // Neither the hold (guide menu) nor the release (home) follows this press.
        _homeHold?.Cancel();
        _ = ChordScreenshotAsync();
        return true;
    }

    /// <summary>Nobody awaits the chord's screenshot: whatever goes wrong is logged and said, never lost in a dropped task.</summary>
    private async Task ChordScreenshotAsync()
    {
        try
        {
            await TakeScreenshotAsync(fromMenu: false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[screenshot] chord failed: {ex.GetType().Name}");
            try
            {
                ShotFeedback(false, Strings.Get("screenshot.failed", ex.GetType().Name), null, null);
            }
            catch (Exception toast)
            {
                Console.Error.WriteLine($"[screenshot] toast: {toast.GetType().Name}");
            }
        }
    }

    /// <summary>
    /// Our own toasts must not end up in the next picture: the HUD's screenshot toast and the small toast over a game are closed,
    /// and if one may have been on screen the capture waits for the next frame without it.
    /// </summary>
    private async Task HideShotToastsAsync()
    {
        var gameToast = GameToast.CloseCurrent();
        var hudToast = MonoNow() < _shotToastUntil;
        if (hudToast)
        {
            _shotToastUntil = DateTimeOffset.MinValue;
            Emit("screenshot", new { hide = true });
        }
        if (gameToast || hudToast) await Task.Delay(250);
    }

    /// <summary>
    /// Takes, saves and reports one screenshot. <paramref name="fromMenu"/>: from the guide menu, which the UI has just closed; a
    /// running game comes to the front first, so the picture shows the game, not CouchHUD.
    /// </summary>
    public async Task<object> TakeScreenshotAsync(bool fromMenu)
    {
        if (Refusal() is { } refused)
        {
            ShotFeedback(false, refused, null, null);
            return new { ok = false, refused = true };
        }
        // Pressed again too soon, or one still being saved: quietly nothing (no toast for every press).
        if (!_shotThrottle.TryBegin(MonoNow())) return new { ok = false, busy = true };
        try
        {
            if (fromMenu)
            {
                if (_sessions.Current is not null)
                {
                    ResumeGame();
                    // The game needs a moment to draw itself again in front.
                    await Task.Delay(900);
                }
                else
                {
                    // The closed menu has to be gone from the screen.
                    await Task.Delay(450);
                }
                // Meanwhile a code entry may have come up.
                if (Refusal() is { } late)
                {
                    ShotFeedback(false, late, null, null);
                    return new { ok = false, refused = true };
                }
            }
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            // Never fill the drive: below 1 GB free nothing is written (audit 0.5 A4). Free space unknown: refused too, but said as such.
            var free = await Task.Run(() => ScreenshotFiles.FreeBytes(profile));
            if (!ScreenshotRules.EnoughSpace(free))
            {
                if (free is null) Console.Error.WriteLine("[screenshot] free space unknown, not taken");
                ShotFeedback(false, Strings.Get(free is null ? "screenshot.diskUnknown" : "screenshot.lowDisk"), null, null);
                return new { ok = false, refused = true };
            }
            await HideShotToastsAsync();
            var hudInFront = _window?.IsActive == true;
            var game = hudInFront ? null : _sessions.Current?.Item.Name;
            var monitor = ScreenCapture.MonitorInFront();
            // Checked again right before the capture (a code or the pairing code may have come up while the toasts went), and after it:
            // whatever came up while the picture was taken (and maybe went again) throws it away, unsaved (audit 0.5 A2).
            if (Refusal() is { } before)
            {
                ShotFeedback(false, before, null, null);
                return new { ok = false, refused = true };
            }
            // A held turbo button stops at the cap; only a screenshot that is really taken counts towards it (audit 0.5 A4).
            if (!_shotBurst.TryTake(MonoNow(), out var sayBurst))
            {
                if (sayBurst) ShotFeedback(false, Strings.Get("screenshot.tooMany", _shotBurst.Max), null, null);
                return new { ok = false, refused = true };
            }
            var serial = _uiScreen.Serial;
            var (bitmap, method) = await Task.Run(() =>
            {
                var frame = ScreenCapture.Capture(monitor);
                return (frame.AllBlack() ? null : ScreenCapture.ToBitmap(frame), frame.Method);
            });
            // The page's report of what came up at the end of the capture is a bridge message on its way: a short wait lets it arrive.
            await Task.Delay(ScreenshotRules.SettleMs);
            var during = Refusal();
            if (during is not null || serial != _uiScreen.Serial)
            {
                Console.Error.WriteLine("[screenshot] the screen changed to a code or pairing code during the capture, not saved");
                ShotFeedback(false, during ?? Strings.Get("screenshot.refusedCode"), null, null);
                return new { ok = false, refused = true };
            }
            if (bitmap is null)
            {
                Console.Error.WriteLine($"[screenshot] only black ({method}), not saved");
                ShotFeedback(false, Strings.Get("screenshot.black"), null, null);
                return new { ok = false };
            }
            var (path, thumb) = await Task.Run(() =>
                (ScreenshotFiles.Save(profile, game, DateTime.Now, stream => ScreenCapture.WritePng(bitmap, stream)), ScreenCapture.Thumbnail(bitmap)));
            // The file name only: the full path holds the account's name.
            Console.WriteLine($"[screenshot] {method}: {Path.GetFileName(path)}");
            ShotFeedback(true, Strings.Get("screenshot.saved", Path.GetFileName(path)), path, thumb);
            return new { ok = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException
                                       or System.Runtime.InteropServices.COMException or OutOfMemoryException or ArgumentException)
        {
            // The type only: a message may hold the full path, and with it the account's name.
            Console.Error.WriteLine($"[screenshot] failed: {ex.GetType().Name}");
            ShotFeedback(false, Strings.Get("screenshot.failed", ex.GetType().Name), null, null);
            return new { ok = false };
        }
        finally
        {
            _shotThrottle.End();
        }
    }

    /// <summary>The text of a refusal, null when a screenshot is fine right now.</summary>
    private string? Refusal() => ScreenshotRules.Check(_uiScreen.CodeEntryShown, _windowsUnlock.Pending || _parentAuth.Pending,
        _keyboard.Open && _keyboard.Secret, _uiScreen.PairingShown, _uiScreen.Known) switch
    {
        ScreenshotRefusal.CodeEntry => Strings.Get("screenshot.refusedCode"),
        ScreenshotRefusal.SecretField => Strings.Get("screenshot.refusedSecret"),
        ScreenshotRefusal.PairingCode => Strings.Get("screenshot.refusedPairing"),
        ScreenshotRefusal.UiUnknown => Strings.Get("screenshot.refusedStarting"),
        _ => null
    };

    /// <summary>The HUD in front: its own toast with the picture (and the UI's sound); a game in front: the small topmost toast.</summary>
    private void ShotFeedback(bool ok, string text, string? path, BitmapSource? thumb)
    {
        if (_window?.IsActive == true)
        {
            Emit("screenshot", new { ok, text, url = path is null ? null : LocalFiles.Rewrite(new Uri(path).AbsoluteUri) });
            // The UI shows it 3.5 s (5 s for a failure); a little margin for the fade.
            _shotToastUntil = MonoNow() + TimeSpan.FromSeconds(ok ? 4 : 5.5);
            return;
        }
        try
        {
            GameToast.Show(Strings.Get(ok ? "screenshot.savedTitle" : "screenshot.failedTitle"), text, TimeSpan.FromSeconds(ok ? 3 : 5), thumb);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"[screenshot] toast: {ex.GetType().Name}");
        }
    }
}
