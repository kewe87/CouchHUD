using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CouchHud.Core.Browser;
using CouchHud.Core.Guard;
using CouchHud.Core.Integrations;
using CouchHud.Core.Localization;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CouchHud.Shell;

/// <summary>
/// A question of a web page (alert, confirm, prompt) or a download, answered in the HUD. A page never gets a Windows dialog: that
/// would be a window of its own in front of the HUD, and the controller would no longer reach CouchHUD (the cause of a browser that
/// "stops reacting"). Completed exactly once; unanswered it counts as "no".
/// </summary>
public sealed class PageDialog
{
    private Action<bool, string?>? _complete;

    public PageDialog(string kind, string message, string defaultText, string host, long bytes, Action<bool, string?> complete, bool embedded = false, string extension = "")
    {
        Kind = kind;
        Message = message;
        DefaultText = defaultText;
        Host = host;
        Bytes = bytes;
        Embedded = embedded;
        Extension = extension;
        _complete = complete;
    }

    /// <summary>The question comes from a frame without an address of its own (about:blank, data:): the host shown is the page's, marked as embedded.</summary>
    public bool Embedded { get; }

    /// <summary>A download's file type, shown on its own and never cut (a long name could push it out of sight).</summary>
    public string Extension { get; }

    /// <summary>alert, confirm, prompt or download.</summary>
    public string Kind { get; }

    /// <summary>The page's text (cleaned and shortened), or the file name of a download.</summary>
    public string Message { get; }

    public string DefaultText { get; }

    /// <summary>The site that asks; the HUD shows it so a page cannot pass its question off as one of CouchHUD's.</summary>
    public string Host { get; }

    /// <summary>Size of a download, 0 if unknown.</summary>
    public long Bytes { get; }

    public bool Done => _complete is null;

    /// <summary>The user answered (not a cancel because the user left, the page closed or nobody answered in time).</summary>
    public bool AnsweredByUser { get; private set; }

    public void Complete(bool ok, string? text = null, bool byUser = false)
    {
        var complete = _complete;
        _complete = null;
        if (complete is null) return;
        AnsweredByUser = byUser;
        try
        {
            complete(ok, text);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ObjectDisposedException)
        {
            // The page is gone (closed, navigated away, crashed): nothing to answer any more.
            Console.Error.WriteLine($"[browser] dialog: {ex.Message}");
        }
    }
}

/// <summary>
/// The couch browser's page: a second WebView2 with its own profile and no bridge to CouchHud, so a website can never
/// call into the HUD. It lies over the content area of the HUD's "Browser" tab (the window places it, see
/// MainWindow.PlaceBrowser); the bar, the hints and the start page are HTML in the HUD, so they follow every skin.
/// (A native web view always paints above HTML, which is why the HUD hides the page while a menu or dialog is open.)
/// Everything a page could open as a window of its own is answered here or in the HUD (see BrowserPolicy).
/// </summary>
public sealed class BrowserPane : Grid
{
    // Replaced (not reused) when a start attempt fails, see WebViewRuntimeLocator.StartAsync.
    private WebView2 _web;
    private readonly string _profileDir;
    private readonly Func<bool> _allowDownloads;
    private readonly Func<bool> _strictRuntime;
    private readonly Func<bool> _historyOff;
    private bool _webTried;
    private bool _disposed;
    private Task? _ready;
    private bool _fullScreen;
    private bool _manualFullScreen;
    private bool _failed;
    // An address typed without a scheme went out as https://: if that fails on the certificate, http:// is tried once.
    private string? _httpFallback;
    // The address of the last top-level navigation, and the one whose load failed while the error page shows.
    private string _lastUrl = "";
    private string? _failedUrl;
    private bool _errorPagePending;
    private bool _errorShowing;
    private readonly HashSet<ulong> _cancelled = [];
    private readonly DialogGate _dialogGate = new();
    private readonly List<DateTime> _crashes = [];
    // The navigation that just became a download (or a 204): it ends without a page, which is no error.
    private bool _downloadStarted;
    // Counts top-level documents: text typed in the HUD goes only into the document the field was found in.
    private int _documentSerial;
    // The address that hung once and was loaded again: hanging again it gets the "page hangs" page instead of a third load.
    private string? _hungUrl;
    // The keys a keyboard uses to get out of the page, when they really went down (a page can post the same words itself).
    private readonly Dictionary<string, DateTime> _realKeys = [];
    // The last input CouchHUD itself gave the page (a click, a key, typed text) and the last key the web view saw.
    private long _inputAt = long.MinValue / 2;
    // CouchHUD's isolated world in the current document: the document's serial and the context id.
    private (int Document, int Context)? _hostWorld;
    // A top-level load in progress, and what went wrong with it beyond its error status (a refused certificate or sign-in).
    private bool _navigating;
    private bool _certificateRefused;
    private bool _authRefused;
    // When the last download started, and the last address that answered "no content" (204/205) or with a file attachment.
    private DateTime _downloadAt = DateTime.MinValue;
    private string? _noDocumentUrl;
    private readonly System.Windows.Threading.DispatcherTimer _edgeTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private int _edgeTicks;

    /// <param name="strictRuntime">A couch lock or parental controls are on: never fall back to another runtime than the machine-wide one.</param>
    /// <param name="historyOff">The setting "remember recently visited sites" is off: each start deletes the profile's history.</param>
    public BrowserPane(string profileDir, Func<bool> allowDownloads, Func<bool> strictRuntime, Func<bool> historyOff)
    {
        _profileDir = profileDir;
        _allowDownloads = allowDownloads;
        _strictRuntime = strictRuntime;
        _historyOff = historyOff;
        Background = Brushes.Black;
        _web = NewWebView();
        Children.Add(_web);
        // Manual full screen has a way out for the mouse: the pointer at the top edge for a moment.
        _edgeTimer.Tick += (_, _) => WatchTopEdge();
    }

    /// <summary>Real input went into the page (CouchHUD's click, key or text; a key on the keyboard). A page may go full screen only right after one.</summary>
    public void NoteInput() => _inputAt = Environment.TickCount64;

    private bool InputWithin(TimeSpan span) => Environment.TickCount64 - _inputAt < span.TotalMilliseconds;

    /// <summary>Someone used the real mouse or keyboard a moment ago (asked by the HUD, which knows its own injected input).</summary>
    public Func<TimeSpan, bool>? RealInputWithin { get; set; }

    // Whether the removal of the control's own background colour variable was logged already (once per process, every pane).
    private static int _ownBackgroundNoted;

    /// <summary>How long after an input a page may still go full screen by itself (a click on a video's full-screen button).</summary>
    public static readonly TimeSpan FullScreenGrace = TimeSpan.FromSeconds(2);

    /// <summary>A key word from the page script counts only when that key really went down on a keyboard a moment ago (one use).</summary>
    private bool RealKey(string word)
    {
        if (!_realKeys.Remove(word, out var at)) return false;
        return DateTime.UtcNow - at < TimeSpan.FromSeconds(1);
    }

    /// <summary>A web view for the page; every one (also the fresh one after a failed start) reports its keys.</summary>
    private WebView2 NewWebView()
    {
        var web = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.White, AllowExternalDrop = false };
        web.PreviewKeyDown += OnWebKeyDown;
        return web;
    }

    // The web view hands keys that are no characters (F6, F11, Esc, Alt+arrows) to WPF as well: that is the proof a key was pressed.
    private void OnWebKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        var alt = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Alt) != 0;
        var word = key switch
        {
            System.Windows.Input.Key.F6 => "key:bar",
            System.Windows.Input.Key.Up when alt => "key:bar",
            System.Windows.Input.Key.F11 => "key:fullscreen",
            System.Windows.Input.Key.Escape => "key:escape",
            _ => null
        };
        if (word is not null) _realKeys[word] = DateTime.UtcNow;
        NoteInput();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CursorPoint point);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X;
        public int Y;
    }

    private void WatchTopEdge()
    {
        if (!_manualFullScreen || !IsVisible || !GetCursorPos(out var cursor))
        {
            _edgeTicks = 0;
            return;
        }
        var top = PointToScreen(new System.Windows.Point(0, 0));
        var atEdge = cursor.Y <= top.Y + 2 && cursor.X >= top.X && cursor.X <= top.X + ActualWidth * VisualTreeHelper.GetDpi(this).DpiScaleX;
        _edgeTicks = atEdge ? _edgeTicks + 1 : 0;
        if (_edgeTicks >= 3) ExitFullScreen();
    }

    // F6 and Alt+Up: back to the bar; F11: full screen on or off; Esc: ends the manual full screen. Top frame only.
    private const string KeyScript = """
        (function () {
          if (window !== window.top || !window.chrome || !window.chrome.webview) return;
          var post = function (m) { try { window.chrome.webview.postMessage(m); } catch (e) {} };
          window.addEventListener('keydown', function (e) {
            if (e.key === 'F6' || (e.altKey && e.key === 'ArrowUp')) { e.preventDefault(); post('key:bar'); }
            else if (e.key === 'F11') { e.preventDefault(); post('key:fullscreen'); }
            else if (e.key === 'Escape') post('key:escape');
          }, true);
        })();
        """;

    // What has the focus in the page: a text field, a password field, a frame (unknown) or nothing to type into.
    private const string FieldScript = """
        (() => {
          let e = document.activeElement;
          while (e && e.shadowRoot && e.shadowRoot.activeElement) e = e.shadowRoot.activeElement;
          if (!e || e === document.body || e === document.documentElement) return 'none';
          if (e.tagName === 'IFRAME' || e.tagName === 'FRAME') return 'frame';
          if (e.isContentEditable || e instanceof HTMLTextAreaElement) return 'text';
          if (e instanceof HTMLInputElement) {
            if (e.type === 'password') return 'password';
            return ['text', 'search', 'email', 'url', 'tel', 'number', ''].includes(e.type) ? 'text' : 'none';
          }
          return 'none';
        })()
        """;

    /// <summary>Keyboard way back to the HUD's bar (F6, Alt+Up): with the page focused every key goes to the page.</summary>
    public event Action? BarRequested;

    /// <summary>The page changed (address, title, history): the HUD bar shows it.</summary>
    public event Action? PageChanged;

    /// <summary>The page went full screen (video) or came back; also the manual full-screen button.</summary>
    public event Action? FullScreenChanged;

    public event Action? TvModeChanged;

    /// <summary>A page's question or a download: the HUD asks and completes it (unanswered it counts as "no").</summary>
    public event Action<PageDialog>? DialogRequested;

    /// <summary>Something the page wanted was refused or happened in the background (a text key for a toast and its argument).</summary>
    public event Action<string, string>? Notice;

    /// <summary>The page's process ended or hangs. True: the browser cannot go on (its own process ended, or crashes over and over).</summary>
    public event Action<bool>? Crashed;

    /// <summary>
    /// The page wanted to load the Home Assistant sign-in's redirect address (see <see cref="HomeAssistantLogin.IsRedirect"/>): the
    /// navigation is cancelled before any request, so the address never shows, never goes into the history and reaches no server.
    /// Arguments: that address (it carries the code: never logged) and the address of the page it came from.
    /// </summary>
    public event Action<string, string>? SignInRedirect;

    /// <summary>The last load failed (the error page is showing): no lock symbol, no "secure".</summary>
    public bool Failed => _failed;

    public bool ManualFullScreen => _manualFullScreen;

    /// <summary>Zoom in percent; 150 reads well on a TV from the couch.</summary>
    public int Zoom
    {
        get => (int)Math.Round(_web.ZoomFactor * 100);
        set => _web.ZoomFactor = Math.Clamp(value, 50, 300) / 100.0;
    }

    /// <summary>The page's address; on the error page the address that failed (so the bar, a bookmark and "reload" mean that one).</summary>
    public string Url => _errorShowing && _failedUrl is not null ? _failedUrl : _web.CoreWebView2?.Source ?? "";

    public string PageTitle => _errorShowing ? "" : _web.CoreWebView2?.DocumentTitle ?? "";

    public bool CanGoBack => _web.CoreWebView2?.CanGoBack == true;

    public bool CanGoForward => _web.CoreWebView2?.CanGoForward == true;

    /// <summary>A video (or the full-screen button) has the page over the whole window.</summary>
    public bool FullScreen => _fullScreen || _manualFullScreen;

    public void SetManualFullScreen(bool on)
    {
        if (_manualFullScreen == on) return;
        _manualFullScreen = on;
        _edgeTicks = 0;
        if (on) _edgeTimer.Start();
        else _edgeTimer.Stop();
        FullScreenChanged?.Invoke();
    }

    /// <summary>Leaves full screen: the page's own (a video) and the manual one.</summary>
    public void ExitFullScreen()
    {
        _manualFullScreen = false;
        _edgeTimer.Stop();
        _ = RunScriptAsync("document.fullscreenElement && document.exitFullscreen();" + EndPictureInPicture);
        // Out of full screen even when the page does not answer (hangs): the HUD gets its window back; a page still in full screen reports again.
        _fullScreen = false;
        FullScreenChanged?.Invoke();
    }

    /// <summary>A new document, a crash: whatever was in full screen is gone with it.</summary>
    private void ResetFullScreen()
    {
        if (!_fullScreen) return;
        _fullScreen = false;
        FullScreenChanged?.Invoke();
    }

    /// <summary>Leaving the tab: what plays stops, like a tab in the background.</summary>
    public void PauseMedia() => _ = RunScriptAsync("document.querySelectorAll('video,audio').forEach(function (m) { try { m.pause(); } catch (e) {} });" + EndPictureInPicture);

    // Should a picture-in-picture window exist after all (the switches are off, the guard script refuses it): closed.
    private const string EndPictureInPicture =
        "try { if (document.pictureInPictureElement) document.exitPictureInPicture(); } catch (e) {}" +
        "try { var d = window.documentPictureInPicture; if (d && d.window) d.window.close(); } catch (e) {}";

    /// <summary>Ends a picture-in-picture window of the page (guide, leaving, full screen).</summary>
    public void EndPictureInPictureWindow() => _ = RunScriptAsync(EndPictureInPicture);

    /// <summary>
    /// CouchHUD's own script in the page (pause, leave full screen), in a world of its own the page cannot tamper with, and
    /// without a user gesture: ExecuteScript would count as a click, and the page could then open windows or go full screen by
    /// itself for a while. True when it ran.
    /// </summary>
    private async Task<bool> RunScriptAsync(string script)
    {
        try
        {
            if (_web.CoreWebView2 is not { } core || _disposed) return false;
            // One isolated world per document (a new one for every call would pile up); a stale one (the document changed before its
            // serial did) fails once and is made again.
            for (var attempt = 0; ; attempt++)
            {
                var serial = _documentSerial;
                var cached = _hostWorld is { } w && w.Document == serial;
                var context = cached ? _hostWorld!.Value.Context : await IsolatedContextAsync(core, "couchhud-host");
                if (!cached) _hostWorld = (serial, context);
                try
                {
                    await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                        System.Text.Json.JsonSerializer.Serialize(new { expression = script, contextId = context, returnByValue = true, userGesture = false }));
                    return true;
                }
                catch (Exception) when (cached && attempt == 0)
                {
                    _hostWorld = null;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ObjectDisposedException
            or ArgumentException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            Console.Error.WriteLine($"[browser] script: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Routes every request matching <paramref name="pattern"/> through WebResourceRequested: from documents and frames and, with
    /// <see cref="CoreWebView2WebResourceRequestSourceKinds.All"/>, from shared and service workers too. A WebView2 runtime too old
    /// for that overload gets the plain filter (pages and frames only; a worker's request then ends at .invalid, which never resolves).
    /// </summary>
    private static void BlockRequests(CoreWebView2 core, string pattern)
    {
        try
        {
            core.AddWebResourceRequestedFilter(pattern, CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        }
        catch (Exception ex) when (ex is NotImplementedException or InvalidCastException or System.Runtime.InteropServices.COMException)
        {
            core.AddWebResourceRequestedFilter(pattern, CoreWebView2WebResourceContext.All);
        }
    }

    /// <summary>A fresh isolated world in the top frame (the page's scripts see nothing of it); its execution context id.</summary>
    private static async Task<int> IsolatedContextAsync(CoreWebView2 core, string name)
    {
        using var tree = System.Text.Json.JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.getFrameTree", "{}"));
        var frameId = tree.RootElement.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString();
        using var world = System.Text.Json.JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.createIsolatedWorld",
            System.Text.Json.JsonSerializer.Serialize(new { frameId, worldName = name, grantUniveralAccess = false })));
        return world.RootElement.GetProperty("executionContextId").GetInt32();
    }

    /// <summary>The page's media plays sound, or a video is in full screen: no screensaver over it (the HUD sees no input meanwhile).</summary>
    public bool Playing
    {
        get
        {
            try
            {
                return _fullScreen || _web.CoreWebView2?.IsDocumentPlayingAudio == true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                return false;
            }
        }
    }

    /// <summary>The page's origin and document, for text typed into it: if either changed meanwhile, the text is not inserted.</summary>
    public (string Origin, int Document) TypingTarget()
    {
        var source = _errorShowing ? "" : _web.CoreWebView2?.Source ?? "";
        var origin = Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.GetLeftPart(UriPartial.Authority) : "";
        return (origin, _documentSerial);
    }

    /// <summary>The page answers a script call within <paramref name="timeout"/> (the watchdog's sign of life). True also while nothing is loaded.</summary>
    public async Task<bool> PingAsync(TimeSpan timeout)
    {
        if (_web.CoreWebView2 is not { } core || _disposed) return true;
        // Through the DevTools protocol without a user gesture (ExecuteScript would hand the page a click every two seconds).
        var call = PingCallAsync(core);
        return await Task.WhenAny(call, Task.Delay(timeout)) == call;
    }

    private async Task PingCallAsync(CoreWebView2 core)
    {
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", "{\"expression\":\"1\",\"returnByValue\":true,\"userGesture\":false}");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ObjectDisposedException or ArgumentException)
        {
            // Gone or refused: no hang (the crash handling deals with a dead page).
            Console.Error.WriteLine($"[browser] ping: {ex.Message}");
        }
    }

    private Task EnsureReadyAsync()
    {
        // A failed start (WebView2 busy, updated) gets another chance next time.
        if (_ready is { IsFaulted: true } or { IsCanceled: true }) _ready = null;
        return _ready ??= InitAsync();
    }

    private async Task InitAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Defence in depth: App.OnStartup already removed every WEBVIEW2_* override; nothing may have put one back since.
        // The WPF control itself writes its background colour back each time a web view is made: removed too, but logged once only.
        var late = CouchHud.Core.Security.WebViewEnvironment.ScrubProcess();
        var foreign = CouchHud.Core.Security.WebViewEnvironment.Foreign(late);
        if (foreign.Count > 0)
            Console.Error.WriteLine($"[browser] removed WebView2 override variables: {string.Join(", ", foreign)}");
        else if (late.Count > 0 && Interlocked.Exchange(ref _ownBackgroundNoted, 1) == 0)
            Console.Error.WriteLine($"[browser] removed the web view's own {CouchHud.Core.Security.WebViewEnvironment.OwnBackgroundColor} (logged once)");
        // No picture-in-picture (see BrowserGuard.BrowserArguments); remote debugging (loopback, the HUD's port + 1) only with the administrator's switch; see DeveloperMode.
        var arguments = Services.DeveloperMode.BrowserArgumentsFor(CouchHud.Core.Security.BrowserSurface.BrowserPane,
            CouchHud.Core.Security.BrowserArguments.PaneBase);
        // The machine-wide runtime below Program Files, like the HUD (see WebViewRuntimeLocator).
        // A failed attempt leaves a web view that is not used again: each attempt after the first gets a new one in the same place.
        // (also across InitAsync calls: a start that failed earlier left _web unusable as well)
        WebView2 Fresh()
        {
            // Closed while a start was running: no new web view (and no browser process) for a pane that is gone.
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_webTried)
            {
                _webTried = true;
                return _web;
            }
            var index = Children.IndexOf(_web);
            Children.Remove(_web);
            _web.Dispose();
            _web = NewWebView();
            Children.Insert(index < 0 ? 0 : index, _web);
            return _web;
        }
        // Fail closed with a lock or parental controls on: the exception goes to the caller (the pane shows its error, no other runtime).
        await Services.WebViewRuntimeLocator.StartAsync(Fresh, _profileDir, () => new CoreWebView2EnvironmentOptions(arguments), _strictRuntime(), "browser");
        // Dispose() during the start already disposed _web (the one that started): nothing to configure.
        ObjectDisposedException.ThrowIf(_disposed, this);
        var core = _web.CoreWebView2;
        _normalAgent = core.Settings.UserAgent;
        var settings = core.Settings;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        // No "Save as", "Print", "Open in" or browser shortcuts: each could open a Windows dialog.
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        // alert, confirm, prompt and "leave this page?" are answered in the HUD (ScriptDialogOpening below), never as a Windows window.
        settings.AreDefaultScriptDialogsEnabled = false;
        // No host objects ever (the page has no bridge to CouchHud), no saved passwords or form data to fill into a stranger's page.
        settings.AreHostObjectsAllowed = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        // The zoom is the HUD's (LB/RB, the bar): Ctrl+wheel must not change it behind its back.
        settings.IsZoomControlEnabled = false;

        // Links to apps (search-ms:, ms-settings:, steam: …) never leave the browser, from frames neither.
        core.LaunchingExternalUriScheme += (_, e) =>
        {
            e.Cancel = true;
            Notice?.Invoke("browser.appLinkBlocked", SchemeOf(e.Uri));
        };
        core.FrameNavigationStarting += (_, e) =>
        {
            // The sign-in's redirect address never loads in a frame either; a frame's redirect is not taken as a sign-in.
            if (!BrowserPolicy.IsNavigable(e.Uri) || HomeAssistantLogin.IsRedirect(e.Uri)) e.Cancel = true;
        };
        // Defense in depth for the sign-in's redirect host: any request there that got past the navigation checks (an image, fetch,
        // a form in a frame, a shared or service worker) is answered here with 403, in the browser, never sent out. .invalid would
        // not resolve anyway.
        BlockRequests(core, "*://" + HomeAssistantLogin.RedirectHost + "/*");
        BlockRequests(core, "*://" + HomeAssistantLogin.RedirectHost + ":*");
        BlockRequests(core, "*://" + HomeAssistantLogin.RedirectHost + "./*");
        core.WebResourceRequested += (_, e) =>
        {
            if (HomeAssistantLogin.IsRedirect(e.Request.Uri))
                e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Forbidden", "");
        };
        // Pop-ups open in the same window (there is only one screen on the couch), and only after a click.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (BrowserPolicy.PopupTarget(e.Uri, e.IsUserInitiated) is { } target) core.Navigate(target);
        };
        core.NavigationStarting += (_, e) =>
        {
            // Home Assistant's sign-in sends the code here: never loaded (no request, no address bar, no history), handed to the HUD.
            // Checked first, before anything else of this handler could note the address.
            if (HomeAssistantLogin.IsRedirect(e.Uri))
            {
                e.Cancel = true;
                _cancelled.Add(e.NavigationId);
                SignInRedirect?.Invoke(e.Uri, core.Source ?? "");
                return;
            }
            if (_errorPagePending && e.Uri.StartsWith("data:text/html", StringComparison.OrdinalIgnoreCase))
            {
                // The error page itself (NavigateToString); any other navigation in between goes through the checks below.
                _errorPagePending = false;
                return;
            }
            if (!BrowserPolicy.IsNavigable(e.Uri))
            {
                e.Cancel = true;
                _cancelled.Add(e.NavigationId);
                Notice?.Invoke("browser.schemeBlocked", SchemeOf(e.Uri));
                return;
            }
            // Another kind of page (TV or normal): it needs the other user agent before the request goes out.
            if (IsTvPage(e.Uri) != _tv)
            {
                e.Cancel = true;
                _cancelled.Add(e.NavigationId);
                _ = NavigateAsync(e.Uri);
                return;
            }
            _errorShowing = false;
            _errorPagePending = false;
            _failedUrl = null;
            _downloadStarted = false;
            // The manual full screen belongs to the site it was turned on for: another site does not inherit the whole screen.
            if (_manualFullScreen && !_errorShowing && HostOf(e.Uri) != HostOf(_lastUrl)) ExitFullScreen();
            _lastUrl = e.Uri;
            _documentSerial++;
            if (!e.IsRedirected)
            {
                _certificateRefused = false;
                _authRefused = false;
                _noDocumentUrl = null;
            }
            _navigating = true;
            ResetFullScreen();
        };
        core.NavigationCompleted += (_, e) =>
        {
            _navigating = false;
            if (_cancelled.Remove(e.NavigationId))
            {
                PageChanged?.Invoke();
                return;
            }
            if (!e.IsSuccess && _httpFallback is { } https && NoHttpsServer(e.WebErrorStatus))
            {
                _httpFallback = null;
                core.Navigate("http://" + https["https://".Length..]);
                return;
            }
            if (e.IsSuccess) _httpFallback = null;
            // A navigation that became a download, or a 204 ("nothing new"), leaves the page as it was: no error. Neither does a server's
            // own error document (404, 500): it is shown as the server sent it. The own error page is only for "no answer at all".
            var download = _downloadStarted;
            _downloadStarted = false;
            var status = e.HttpStatusCode;
            var noDocument = download || status is 204 or 205 || (_noDocumentUrl is not null && _noDocumentUrl == _lastUrl);
            // A refused sign-in shows Chromium's bare "HTTP ERROR 401": the own page says what it is.
            if (_authRefused && !_errorShowing && !e.IsSuccess && status is 401 or 407)
            {
                _authRefused = false;
                _failed = true;
                ShowErrorPage(core, "browser.error.auth");
                PageChanged?.Invoke();
                return;
            }
            _failed = (!e.IsSuccess && !noDocument) || _errorShowing;
            var key = _certificateRefused ? "browser.error.certificate" : BrowserPolicy.ErrorKey(e.WebErrorStatus.ToString());
            if (!e.IsSuccess && !noDocument && status == 0 && !_errorShowing && key is not null)
            {
                // An aborted load may still turn out to be a download or a 204: the download is only reported after the navigation ended.
                // A moment later, if nothing like that came and nothing else was loaded meanwhile, the error page.
                if (key is "browser.error.connect" or "browser.error.generic") ShowErrorPageSoon(key);
                else ShowErrorPage(core, key);
            }
            PageChanged?.Invoke();
        };
        // A "no content" answer or a file attachment for the page being loaded: it ends without a document, which is no error.
        core.WebResourceResponseReceived += (_, e) =>
        {
            try
            {
                if (e.Request.Uri != _lastUrl) return;
                var attachment = e.Response.Headers.Contains("Content-Disposition")
                    && e.Response.Headers.GetHeader("Content-Disposition").TrimStart().StartsWith("attachment", StringComparison.OrdinalIgnoreCase);
                if (e.Response.StatusCode is 204 or 205 || attachment) _noDocumentUrl = e.Request.Uri;
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
            {
                Console.Error.WriteLine($"[browser] response: {ex.Message}");
            }
        };
        // The keys a keyboard needs to get out of the page: the page script reports them (the WPF web view gives no
        // accelerator hook). Only these exact words count; a page could send them as well, which does no harm.
        core.WebMessageReceived += (_, e) =>
        {
            string? message;
            try
            {
                message = e.TryGetWebMessageAsString();
            }
            catch (ArgumentException)
            {
                return;
            }
            switch (message)
            {
                case "key:bar" when !FullScreen && RealKey("key:bar"):
                    BarRequested?.Invoke();
                    break;
                case "key:fullscreen" when RealKey("key:fullscreen"):
                    if (FullScreen) ExitFullScreen();
                    else SetManualFullScreen(true);
                    break;
                case "key:escape" when _manualFullScreen && RealKey("key:escape"):
                    ExitFullScreen();
                    break;
            }
        };
        await core.AddScriptToExecuteOnDocumentCreatedAsync(KeyScript);
        // Videos (YouTube) go full screen over the whole window: the HUD steps aside until they end.
        core.ContainsFullScreenElementChanged += (_, _) =>
        {
            var on = core.ContainsFullScreenElement;
            // Full screen covers the whole HUD, so a page could paint a fake code prompt over it: only right after real input (the
            // click on a video's button, F or a double click), never from a timer.
            if (on && !_fullScreen && !InputWithin(FullScreenGrace) && RealInputWithin?.Invoke(FullScreenGrace) != true)
            {
                Console.Error.WriteLine("[browser] full screen without input refused");
                _fullScreen = true;
                ExitFullScreen();
                Notice?.Invoke("browser.fullscreenRefused", HostOf(core.Source));
                return;
            }
            _fullScreen = on;
            // Never a picture-in-picture window next to a full-screen page.
            if (on) EndPictureInPictureWindow();
            FullScreenChanged?.Invoke();
        };
        core.IsDocumentPlayingAudioChanged += (_, _) => PageChanged?.Invoke();
        core.ScriptDialogOpening += OnScriptDialog;
        core.DownloadStarting += OnDownload;
        // Camera, microphone, location, notifications, clipboard …: refused without a prompt, and not remembered.
        core.PermissionRequested += (_, e) =>
        {
            e.State = BrowserPolicy.AllowPermission(e.PermissionKind.ToString()) ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
            e.SavesInProfile = false;
            e.Handled = true;
        };
        // A site that wants a Windows sign-in (HTTP basic auth) would show a Windows credential window: refused, with a note.
        core.BasicAuthenticationRequested += (_, e) =>
        {
            e.Cancel = true;
            if (_navigating && HostOf(e.Uri) == HostOf(_lastUrl)) _authRefused = true;
            Notice?.Invoke("browser.authUnsupported", HostOf(e.Uri));
        };
        // A broken or forged certificate is never clicked through on the couch: the load fails and the error page says why.
        core.ServerCertificateErrorDetected += (_, e) =>
        {
            e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
            // The load then fails as "connection aborted": the error page says it was the certificate.
            if (_navigating && HostOf(e.RequestUri) == HostOf(_lastUrl)) _certificateRefused = true;
        };
        core.ProcessFailed += OnProcessFailed;
        // No print dialog, no file, folder or color picker from a page, no upload field: each opens a Windows dialog.
        await core.AddScriptToExecuteOnDocumentCreatedAsync(BrowserGuard.Script);
        core.SourceChanged += (_, _) => PageChanged?.Invoke();
        core.DocumentTitleChanged += (_, _) => PageChanged?.Invoke();
        core.HistoryChanged += (_, _) => PageChanged?.Invoke();
        // History off in the settings: what the profile kept from earlier sessions (or from before it was switched off while no
        // pane was open) goes before the first page loads (audit 0.5 B2).
        if (_historyOff()) await ClearHistoryCoreAsync(core);
    }

    /// <summary>
    /// Deletes the browsing and download history of the browser's profile (the setting "history off"). Not started yet: nothing to
    /// do now, the start does it (see InitAsync). False: not deleted (failed start, closed, WebView2 error); logged.
    /// </summary>
    public async Task<bool> ClearHistoryAsync()
    {
        try
        {
            if (_ready is null || _disposed) return false;
            // A start still running is waited for: it may have checked the setting before it changed.
            await _ready;
            if (_disposed || _web.CoreWebView2 is not { } core) return false;
            return await ClearHistoryCoreAsync(core);
        }
        catch (Exception ex)
        {
            // A failed start says so itself; the next start deletes the history (the setting stays off).
            Console.Error.WriteLine($"[browser] history not cleared: {ex.Message}");
            return false;
        }
    }

    private static async Task<bool> ClearHistoryCoreAsync(CoreWebView2 core)
    {
        try
        {
            await core.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.BrowsingHistory | CoreWebView2BrowsingDataKinds.DownloadHistory);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ObjectDisposedException)
        {
            Console.Error.WriteLine($"[browser] history not cleared: {ex.Message}");
            return false;
        }
    }

    private void OnScriptDialog(object? sender, CoreWebView2ScriptDialogOpeningEventArgs e)
    {
        // "Leave this page?": the user chose to go (typed an address, pressed back); a page does not keep the couch on it.
        if (e.Kind == CoreWebView2ScriptDialogKind.Beforeunload)
        {
            e.Accept();
            return;
        }
        if (!_dialogGate.Allow(DateTimeOffset.UtcNow, out var justBlocked))
        {
            // Not accepted: alert closed, confirm "no", prompt cancelled.
            if (justBlocked) Notice?.Invoke("browser.dialogsBlocked", HostOf(e.Uri));
            return;
        }
        if (DialogRequested is null) return;
        var kind = e.Kind switch
        {
            CoreWebView2ScriptDialogKind.Confirm => "confirm",
            CoreWebView2ScriptDialogKind.Prompt => "prompt",
            _ => "alert"
        };
        // A frame without an address of its own (about:blank, data:, srcdoc) names the page it is in, marked as embedded.
        var host = HostOf(e.Uri);
        var embedded = host.Length == 0;
        if (embedded) host = HostOf(_web.CoreWebView2?.Source);
        var deferral = e.GetDeferral();
        PageDialog? dialog = null;
        dialog = new PageDialog(kind, BrowserPolicy.DialogText(e.Message), BrowserPolicy.DialogText(e.DefaultText, 500), host, 0, (ok, text) =>
        {
            // Only the user's answer starts a chain; a question cancelled because the user left does not.
            if (dialog?.AnsweredByUser == true) _dialogGate.Answered(DateTimeOffset.UtcNow);
            try
            {
                if (ok)
                {
                    if (kind == "prompt") e.ResultText = text is null ? "" : text.Length > 2000 ? text[..2000] : text;
                    e.Accept();
                }
            }
            finally
            {
                deferral.Complete();
            }
        }, embedded);
        DialogRequested.Invoke(dialog);
    }

    // Never the browser's own download window (it has "open" and "show in folder"). With the couch lock closed nothing is downloaded;
    // otherwise the HUD asks, and the file goes into the Downloads folder under a cleaned name, never opened.
    private void OnDownload(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        // The navigation that led here ends without a page: no error page for it.
        _downloadStarted = true;
        _downloadAt = DateTime.UtcNow;
        var (handled, cancel) = BrowserGuard.Download(_allowDownloads());
        e.Handled = handled;
        var operation = e.DownloadOperation;
        // The real name: the browser renames types it distrusts (x.scf.download, Unconfirmed 1234.crdownload).
        var suggested = Path.GetFileName(e.ResultFilePath);
        string disposition = "", from = "";
        try
        {
            disposition = operation.ContentDisposition ?? "";
            from = operation.Uri ?? "";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Console.Error.WriteLine($"[browser] download: {ex.Message}");
        }
        var name = BrowserPolicy.DownloadName(suggested, disposition, from);
        if (cancel || DialogRequested is null)
        {
            e.Cancel = true;
            Notice?.Invoke("browser.downloadBlocked", name);
            return;
        }
        // Programs, scripts, shortcuts, installers, disk images: never, not even when asked.
        if (BrowserPolicy.IsDangerousDownload(suggested, disposition, from))
        {
            e.Cancel = true;
            Notice?.Invoke("browser.downloadDangerous", name);
            return;
        }
        var folder = DownloadsFolder();
        var deferral = e.GetDeferral();
        var bytes = (long)(operation.TotalBytesToReceive ?? 0);
        var source = HostOf(operation.Uri);
        var dialog = new PageDialog("download", name, "", source.Length > 0 ? source : HostOf(_web.CoreWebView2?.Source), bytes, (ok, _) =>
        {
            try
            {
                // Asked again at the answer: the lock may have closed meanwhile.
                if (ok && _allowDownloads())
                {
                    var path = BrowserPolicy.DownloadPath(folder, name, File.Exists);
                    e.ResultFilePath = path;
                    var saved = Path.GetFileName(path);
                    operation.StateChanged += (_, _) =>
                    {
                        if (operation.State == CoreWebView2DownloadState.Completed) Notice?.Invoke("browser.downloadDone", saved);
                        else if (operation.State == CoreWebView2DownloadState.Interrupted) Notice?.Invoke("browser.downloadFailed", saved);
                    };
                }
                else
                {
                    e.Cancel = true;
                }
            }
            finally
            {
                deferral.Complete();
            }
        }, source.Length == 0, BrowserPolicy.DownloadExtension(name));
        DialogRequested.Invoke(dialog);
    }

    private string DownloadsFolder()
    {
        try
        {
            var folder = _web.CoreWebView2?.Profile.DefaultDownloadFolderPath;
            if (!string.IsNullOrWhiteSpace(folder) && Path.IsPathFullyQualified(folder)) return folder;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Console.Error.WriteLine($"[browser] downloads folder: {ex.Message}");
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Console.Error.WriteLine($"[browser] process failed: {e.ProcessFailedKind}");
        if (_disposed) return;
        bool fatal;
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                fatal = true;
                break;
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                fatal = CountCrash();
                break;
            default:
                // GPU, utility and frame processes: WebView2 starts them again by itself.
                return;
        }
        _documentSerial++;
        // Not from inside the web view's own event: closing or reloading it there can deadlock or hit a half torn-down control.
        Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            ResetFullScreen();
            Crashed?.Invoke(fatal);
        });
    }

    /// <summary>One more crash or hang in the last minute; true when that is too many (the browser is closed then).</summary>
    private bool CountCrash()
    {
        var now = DateTime.UtcNow;
        _crashes.RemoveAll(t => now - t > TimeSpan.FromMinutes(1));
        _crashes.Add(now);
        return _crashes.Count > 2;
    }

    /// <summary>
    /// The watchdog found the page not answering. Counts like a crash; the first time the page is loaded again, the second time for the
    /// same address the "page hangs" page shows instead (a page that hangs on every load would otherwise be loaded forever).
    /// True: too many in a row, the browser is to be closed.
    /// </summary>
    public bool RecoverFromHang()
    {
        if (_disposed || _web.CoreWebView2 is not { } core) return false;
        if (CountCrash()) return true;
        var url = Url;
        _documentSerial++;
        ResetFullScreen();
        if (_hungUrl == url)
        {
            _hungUrl = null;
            _lastUrl = url;
            ShowErrorPage(core, "browser.error.hung", "browser.hungTitle");
            PageChanged?.Invoke();
            return false;
        }
        _hungUrl = url;
        Reload();
        return false;
    }

    /// <summary>The page could not be loaded: an error page of our own (no scripts, every text escaped) instead of a blank or half page.</summary>
    private void ShowErrorPage(CoreWebView2 core, string key, string titleKey = "browser.errorTitle")
    {
        _failedUrl = _lastUrl;
        _errorShowing = true;
        _errorPagePending = true;
        try
        {
            core.NavigateToString(BrowserPolicy.ErrorPage(Strings.Get(titleKey), Strings.Get(key), _lastUrl, Strings.Get("browser.errorHint"), Strings.Language));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            _errorPagePending = false;
            Console.Error.WriteLine($"[browser] error page: {ex.Message}");
        }
    }

    /// <summary>The error page a moment later, unless the load became a download or a 204, or another load began meanwhile.</summary>
    private void ShowErrorPageSoon(string key)
    {
        var serial = _documentSerial;
        var url = _lastUrl;
        var failedAt = DateTime.UtcNow;
        _ = Task.Delay(700).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
        {
            if (_disposed || serial != _documentSerial || _errorShowing || _web.CoreWebView2 is not { } core) return;
            if (_downloadAt >= failedAt.AddSeconds(-2) || _noDocumentUrl == url)
            {
                // The old page stays: nothing failed.
                _failed = false;
                PageChanged?.Invoke();
                return;
            }
            ShowErrorPage(core, key);
            PageChanged?.Invoke();
        }), TaskScheduler.Default);
    }

    private static string HostOf(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.IdnHost : "";

    private static string SchemeOf(string? url)
    {
        var colon = url?.IndexOf(':') ?? -1;
        return colon > 0 ? BrowserPolicy.DialogText(url![..colon], 30) : "";
    }

    /// <summary>
    /// The server does not speak TLS at all (protocol error, invalid answer), refuses the connection or does not answer on
    /// the port. Never a certificate problem: those keep the error page, otherwise a forged certificate on the
    /// network could push the connection down to plain http unnoticed.
    /// </summary>
    private static bool NoHttpsServer(CoreWebView2WebErrorStatus status) =>
        status is CoreWebView2WebErrorStatus.CannotConnect or CoreWebView2WebErrorStatus.ConnectionReset or CoreWebView2WebErrorStatus.ConnectionAborted
            or CoreWebView2WebErrorStatus.ErrorHttpInvalidServerResponse or CoreWebView2WebErrorStatus.Timeout or CoreWebView2WebErrorStatus.Unknown;

    /// <param name="guessed">The address was typed without a scheme: https first, http only when there is no https server (not on certificate errors).</param>
    public async Task OpenAsync(string url, int zoom, bool guessed = false)
    {
        _httpFallback = guessed && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? url : null;
        await EnsureReadyAsync();
        Zoom = zoom;
        await NavigateAsync(url);
    }

    /// <summary>The native window of the page (zero before it exists), for the window's z-order.</summary>
    public IntPtr PageHandle => _web.Handle;

    public void FocusPage() => _web.Focus();

    public void GoBack()
    {
        if (_web.CoreWebView2?.CanGoBack == true) _web.CoreWebView2.GoBack();
    }

    public void GoForward()
    {
        if (_web.CoreWebView2?.CanGoForward == true) _web.CoreWebView2.GoForward();
    }

    /// <summary>Loads the page again; on the error page the address that failed.</summary>
    public void Reload()
    {
        if (_web.CoreWebView2 is not { } core) return;
        if (_errorShowing && _failedUrl is { Length: > 0 } failed) _ = NavigateAsync(failed);
        else core.Reload();
    }

    // ---- Typing and keys from the controller: through the DevTools protocol, so they reach the page whichever window has the keyboard focus

    /// <summary>What has the focus in the page: "text", "password", "frame" (unknown, maybe a password) or "none".</summary>
    /// <remarks>
    /// Looked at in a world of its own (DevTools protocol): the page's scripts cannot fake document.activeElement or the element
    /// classes there. Anything that goes wrong is "unknown", which the HUD treats like a password (masked, not from the phone).
    /// </remarks>
    public async Task<string> FocusedFieldAsync()
    {
        try
        {
            if (_web.CoreWebView2 is not { } core || _disposed) return "unknown";
            using var tree = System.Text.Json.JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.getFrameTree", "{}"));
            var frameId = tree.RootElement.GetProperty("frameTree").GetProperty("frame").GetProperty("id").GetString();
            using var world = System.Text.Json.JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Page.createIsolatedWorld",
                System.Text.Json.JsonSerializer.Serialize(new { frameId, worldName = "couchhud-field", grantUniveralAccess = false })));
            var context = world.RootElement.GetProperty("executionContextId").GetInt32();
            using var result = System.Text.Json.JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                System.Text.Json.JsonSerializer.Serialize(new { expression = FieldScript, contextId = context, returnByValue = true })));
            var value = result.RootElement.GetProperty("result").TryGetProperty("value", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
            return value is "none" or "text" or "password" or "frame" ? value : "unknown";
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ObjectDisposedException
            or ArgumentException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            Console.Error.WriteLine($"[browser] field: {ex.Message}");
            return "unknown";
        }
    }

    /// <summary>Text typed in the HUD into the page's focused field (like typing it on a keyboard).</summary>
    public async Task InsertTextAsync(string text)
    {
        try
        {
            if (_web.CoreWebView2 is { } core && !_disposed)
            {
                NoteInput();
                await core.CallDevToolsProtocolMethodAsync("Input.insertText", System.Text.Json.JsonSerializer.Serialize(new { text }));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ObjectDisposedException or ArgumentException)
        {
            Console.Error.WriteLine($"[browser] type: {ex.Message}");
        }
    }

    private static readonly Dictionary<BrowserPadAction, (string Key, int Vk)> PageKeys = new()
    {
        [BrowserPadAction.ArrowUp] = ("ArrowUp", 38),
        [BrowserPadAction.ArrowDown] = ("ArrowDown", 40),
        [BrowserPadAction.ArrowLeft] = ("ArrowLeft", 37),
        [BrowserPadAction.ArrowRight] = ("ArrowRight", 39),
        [BrowserPadAction.Enter] = ("Enter", 13),
        [BrowserPadAction.Tab] = ("Tab", 9)
    };

    /// <summary>A key into the page (d-pad arrows, Enter, Tab). False: not a page key.</summary>
    public bool PageKey(BrowserPadAction action)
    {
        if (_web.CoreWebView2 is null || !PageKeys.TryGetValue(action, out var k)) return false;
        _ = SendKeyAsync(k.Key, k.Vk);
        return true;
    }

    // ---- TV pages (YouTube TV and the like): the page is made for a remote, the pad sends keys

    // A Samsung Smart TV (Tizen) user agent: YouTube serves its TV interface (Leanback) to it. (A Chromecast CrKey agent only gives the "ready to cast" screen.)
    private const string TvAgent = "Mozilla/5.0 (SMART-TV; Linux; Tizen 6.0) AppleWebKit/537.36 (KHTML, like Gecko) 76.0.3809.146/6.0 TV Safari/537.36";
    private string _normalAgent = "";
    private bool _tv;
    private DateTime _stickNext = DateTime.MinValue;
    private string _stickKey = "";

    /// <summary>The open page is a TV page: controller buttons go to it as arrow keys, Enter and Escape instead of as mouse.</summary>
    public bool TvMode => _tv;

    /// <summary>TV interfaces are served to TV browsers only: these pages get the TV user agent, every other page the normal one.</summary>
    internal static bool IsTvPage(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" &&
        (uri.Host is "www.youtube.com" or "youtube.com" or "m.youtube.com") &&
        (uri.AbsolutePath.Equals("/tv", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.StartsWith("/tv/", StringComparison.OrdinalIgnoreCase));

    private async Task SetAgentAsync(bool tv)
    {
        _tv = tv;
        await _web.CoreWebView2.CallDevToolsProtocolMethodAsync("Emulation.setUserAgentOverride",
            System.Text.Json.JsonSerializer.Serialize(new { userAgent = tv ? TvAgent : _normalAgent }));
        TvModeChanged?.Invoke();
    }

    private async Task NavigateAsync(string url)
    {
        if (_disposed || _web.CoreWebView2 is null) return;
        var tv = IsTvPage(url);
        if (tv != _tv) await SetAgentAsync(tv);
        _web.CoreWebView2.Navigate(url);
    }

    private static readonly Dictionary<string, (string Key, int Vk)> TvKeys = new()
    {
        ["up"] = ("ArrowUp", 38),
        ["down"] = ("ArrowDown", 40),
        ["left"] = ("ArrowLeft", 37),
        ["right"] = ("ArrowRight", 39),
        ["a"] = ("Enter", 13),
        ["b"] = ("Escape", 27)
    };

    /// <summary>Controller button on a TV page, sent as a key press into the page (no mouse, no window focus needed). False: not a TV key.</summary>
    public bool TvButton(string button)
    {
        if (!_tv || _web.CoreWebView2 is null || !TvKeys.TryGetValue(button, out var k)) return false;
        _ = SendKeyAsync(k.Key, k.Vk);
        return true;
    }

    /// <summary>The left stick as arrow keys with key repeat (called a few times per second).</summary>
    public void TvSticks(double x, double y)
    {
        if (!_tv) return;
        var key = "";
        if (Math.Max(Math.Abs(x), Math.Abs(y)) > 0.6) key = Math.Abs(x) > Math.Abs(y) ? (x > 0 ? "right" : "left") : (y > 0 ? "up" : "down");
        if (key.Length == 0)
        {
            _stickKey = "";
            return;
        }
        var now = DateTime.UtcNow;
        if (key == _stickKey && now < _stickNext) return;
        _stickNext = now.AddMilliseconds(key == _stickKey ? 170 : 380);
        _stickKey = key;
        TvButton(key);
    }

    private async Task SendKeyAsync(string key, int vk)
    {
        try
        {
            var core = _web.CoreWebView2;
            if (core is null || _disposed) return;
            NoteInput();
            string Json(string type) => System.Text.Json.JsonSerializer.Serialize(new { type, key, code = key, windowsVirtualKeyCode = vk, nativeVirtualKeyCode = vk, text = key == "Enter" && type == "keyDown" ? "\r" : "" });
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", Json("keyDown"));
            await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", Json("keyUp"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ObjectDisposedException)
        {
            Console.Error.WriteLine($"[browser] key: {ex.Message}");
        }
    }

    /// <summary>Ends the page: the web view disposed (its process goes with it).</summary>
    public void Dispose()
    {
        // With history off the pages of this visit go with it (best effort, at most 2 seconds; the next start deletes them anyway).
        var core = _disposed ? null : _web.CoreWebView2;
        // From here on no start creates a web view again (Fresh, InitAsync, a start still running).
        _disposed = true;
        _edgeTimer.Stop();
        if (core is not null && _historyOff())
        {
            _ = DisposeAfterClearAsync(core);
            return;
        }
        DisposeWeb();
    }

    private async Task DisposeAfterClearAsync(CoreWebView2 core)
    {
        try
        {
            var clear = ClearHistoryCoreAsync(core);
            if (await Task.WhenAny(clear, Task.Delay(TimeSpan.FromSeconds(2))) != clear || !await clear)
                Console.Error.WriteLine("[browser] history not cleared on close; the next start deletes it");
        }
        finally
        {
            DisposeWeb();
        }
    }

    private void DisposeWeb()
    {
        try
        {
            _web.Dispose();
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"[browser] dispose: {ex.Message}");
        }
    }
}
