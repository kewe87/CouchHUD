using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CouchHud.Core.Guard;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CouchHud.Shell;

/// <summary>
/// The couch browser's page: a second WebView2 with its own profile and no bridge to CouchHud, so a website can never
/// call into the HUD. It lies over the content area of the HUD's "Browser" tab (the window places it, see
/// MainWindow.PlaceBrowser); the bar, the hints and the start page are HTML in the HUD, so they follow every skin.
/// (A native web view always paints above HTML, which is why the HUD hides the page while a menu or dialog is open.)
/// </summary>
public sealed class BrowserPane : Grid
{
    // Replaced (not reused) when a start attempt fails, see WebViewRuntimeLocator.StartAsync.
    private WebView2 _web;
    private readonly string _profileDir;
    private readonly Func<bool> _allowDownloads;
    private readonly Func<bool> _strictRuntime;
    private bool _webTried;
    private bool _disposed;
    private Task? _ready;
    private bool _fullScreen;
    private bool _manualFullScreen;
    private bool _failed;
    // An address typed without a scheme went out as https://: if that fails on the certificate, http:// is tried once.
    private string? _httpFallback;
    private readonly System.Windows.Threading.DispatcherTimer _edgeTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private int _edgeTicks;

    /// <param name="strictRuntime">A couch lock or parental controls are on: never fall back to another runtime than the machine-wide one.</param>
    public BrowserPane(string profileDir, Func<bool> allowDownloads, Func<bool> strictRuntime)
    {
        _profileDir = profileDir;
        _allowDownloads = allowDownloads;
        _strictRuntime = strictRuntime;
        Background = Brushes.Black;
        _web = NewWebView();
        Children.Add(_web);
        // Manual full screen has a way out for the mouse: the pointer at the top edge for a moment.
        _edgeTimer.Tick += (_, _) => WatchTopEdge();
    }

    private static WebView2 NewWebView() => new() { DefaultBackgroundColor = System.Drawing.Color.White, AllowExternalDrop = false };

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

    /// <summary>Keyboard way back to the HUD's bar (F6, Alt+Up): with the page focused every key goes to the page.</summary>
    public event Action? BarRequested;

    /// <summary>The last load failed (the browser's error page is showing): no lock symbol, no "secure".</summary>
    public bool Failed => _failed;

    public bool ManualFullScreen => _manualFullScreen;


    /// <summary>The page changed (address, title, history): the HUD bar shows it.</summary>
    public event Action? PageChanged;

    /// <summary>The page went full screen (video) or came back; also the manual full-screen button.</summary>
    public event Action? FullScreenChanged;

    /// <summary>Zoom in percent; 150 reads well on a TV from the couch.</summary>
    public int Zoom
    {
        get => (int)Math.Round(_web.ZoomFactor * 100);
        set => _web.ZoomFactor = Math.Clamp(value, 50, 300) / 100.0;
    }

    public string Url => _web.CoreWebView2?.Source ?? "";

    public string PageTitle => _web.CoreWebView2?.DocumentTitle ?? "";

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
        if (_fullScreen) _ = RunScriptAsync("document.exitFullscreen && document.exitFullscreen()");
        FullScreenChanged?.Invoke();
    }

    /// <summary>Leaving the tab: what plays stops, like a tab in the background.</summary>
    public void PauseMedia() => _ = RunScriptAsync("document.querySelectorAll('video,audio').forEach(function (m) { try { m.pause(); } catch (e) {} })");

    private async Task RunScriptAsync(string script)
    {
        try
        {
            if (_web.CoreWebView2 is not null) await _web.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or ObjectDisposedException)
        {
            Console.Error.WriteLine($"[browser] script: {ex.Message}");
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
        if (CouchHud.Core.Security.WebViewEnvironment.ScrubProcess() is { Count: > 0 } late)
            Console.Error.WriteLine($"[browser] removed WebView2 override variables: {string.Join(", ", late)}");
        // Remote debugging (loopback, the HUD's port + 1) only with the administrator's switch; see DeveloperMode.
        var arguments = Services.DeveloperMode.BrowserArgumentsFor(CouchHud.Core.Security.BrowserSurface.BrowserPane);
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
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        // No "Save as", "Print", "Open in" or browser shortcuts: each could open a Windows dialog.
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        // Links to apps (search-ms:, ms-settings:, steam: …) never leave the browser, from frames neither.
        core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
        core.FrameNavigationStarting += (_, e) =>
        {
            if (!WebScheme(e.Uri)) e.Cancel = true;
        };
        // Pop-ups open in the same window: there is only one screen on the couch.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") core.Navigate(e.Uri);
        };
        core.NavigationStarting += (_, e) =>
        {
            if (!WebScheme(e.Uri))
            {
                e.Cancel = true;
                return;
            }
            // Another kind of page (TV or normal): it needs the other user agent before the request goes out.
            if (IsTvPage(e.Uri) != _tv)
            {
                e.Cancel = true;
                _ = NavigateAsync(e.Uri);
            }
        };
        core.NavigationCompleted += (_, e) =>
        {
            _failed = !e.IsSuccess;
            if (!e.IsSuccess && _httpFallback is { } https && NoHttpsServer(e.WebErrorStatus))
            {
                _httpFallback = null;
                core.Navigate("http://" + https["https://".Length..]);
                return;
            }
            if (e.IsSuccess) _httpFallback = null;
            PageChanged?.Invoke();
        };
        // The keys a keyboard needs to get out of the page: the page script reports them (the WPF web view gives no
        // accelerator hook). Only these exact words count; a page could send them as well, which does no harm.
        core.WebMessageReceived += (_, e) =>
        {
            switch (e.TryGetWebMessageAsString())
            {
                case "key:bar" when !FullScreen:
                    BarRequested?.Invoke();
                    break;
                case "key:fullscreen":
                    if (FullScreen) ExitFullScreen();
                    else SetManualFullScreen(true);
                    break;
                case "key:escape" when _manualFullScreen:
                    ExitFullScreen();
                    break;
            }
        };
        await core.AddScriptToExecuteOnDocumentCreatedAsync(KeyScript);
        // Videos (YouTube) go full screen over the whole window: the HUD steps aside until they end.
        core.ContainsFullScreenElementChanged += (_, _) =>
        {
            _fullScreen = core.ContainsFullScreenElement;
            FullScreenChanged?.Invoke();
        };
        // Never the browser's own download window (it has "open" and "show in folder"); with the couch lock closed nothing is downloaded at all.
        core.DownloadStarting += (_, e) =>
        {
            var (handled, cancel) = BrowserGuard.Download(_allowDownloads());
            e.Handled = handled;
            e.Cancel = cancel;
        };
        // No print dialog, no file or folder picker from a page, no upload field: each opens a Windows dialog.
        await core.AddScriptToExecuteOnDocumentCreatedAsync(BrowserGuard.Script);
        core.SourceChanged += (_, _) => PageChanged?.Invoke();
        core.DocumentTitleChanged += (_, _) => PageChanged?.Invoke();
        core.HistoryChanged += (_, _) => PageChanged?.Invoke();
    }

    /// <summary>Web pages and the browser's own pages; its error page ("chrome-error") must show, otherwise a failed load stays blank.</summary>
    private static bool WebScheme(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "about" or "data" or "blob" or "chrome-error";

    /// <summary>
    /// The server does not speak TLS at all (protocol error, invalid answer), refuses the connection or does not answer on
    /// the port. Never a certificate problem: those keep the browser's error page, otherwise a forged certificate on the
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

    public void Reload() => _web.CoreWebView2?.Reload();

    // ---- TV pages (YouTube TV and the like): the page is made for a remote, the pad sends keys

    // A Samsung Smart TV (Tizen) user agent: YouTube serves its TV interface (Leanback) to it. (A Chromecast CrKey agent only gives the "ready to cast" screen.)
    private const string TvAgent = "Mozilla/5.0 (SMART-TV; Linux; Tizen 6.0) AppleWebKit/537.36 (KHTML, like Gecko) 76.0.3809.146/6.0 TV Safari/537.36";
    private string _normalAgent = "";
    private bool _tv;
    private DateTime _stickNext = DateTime.MinValue;
    private string _stickKey = "";

    /// <summary>The open page is a TV page: controller buttons go to it as arrow keys, Enter and Escape instead of as mouse.</summary>
    public bool TvMode => _tv;

    public event Action? TvModeChanged;

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
        // From here on no start creates a web view again (Fresh, InitAsync, a start still running).
        _disposed = true;
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
