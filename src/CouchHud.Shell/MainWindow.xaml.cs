using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using CouchHud.Core.Security;
using CouchHud.Shell.Native;
using CouchHud.Shell.Services;
using CouchHud.Shell.UiHost;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using CouchHud.Core.Localization;

namespace CouchHud.Shell;

public partial class MainWindow : Window
{
    public const string AppHost = "app.couchhud";
    public static readonly string AppOrigin = $"https://{AppHost}/";

    // More browser crashes than this in a short time means something is badly wrong; hand over to Windows.
    private const int MaxRestarts = 3;
    private static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(2);

    private readonly Hud _hud;
    private readonly bool _dev;
    private readonly List<DateTime> _restarts = [];
    private WebView2? _web;
    private bool _frontAfterLoad;
    // The HUD and, when asked for, the couch browser on top of it: both fill the window, one at a time.
    private readonly System.Windows.Controls.Grid _root = new();
    private BrowserPane? _browser;

    public MainWindow(Hud hud, bool dev)
    {
        _hud = hud;
        _dev = dev;
        InitializeComponent();
        Content = _root;

        if (dev)
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = WindowState.Normal;
            Width = 1600;
            Height = 900;
        }

        // Exact monitor size instead of the maximized state: that stops at the taskbar.
        SourceInitialized += (_, _) =>
        {
            if (!_dev) FitToMonitor();
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
        };
        // Hidden (PC mode, game in front): the taskbar may cover the screen again.
        IsVisibleChanged += (_, e) =>
        {
            if (!_dev && e.NewValue is false) TaskbarFullscreen.Mark(new WindowInteropHelper(this).Handle, false);
        };
        Loaded += async (_, _) => await StartWebAsync();
        Closing += OnClosing;
    }

    public UiBridge? Bridge { get; private set; }

    /// <summary>Set before shutting down; otherwise closing is refused.</summary>
    public bool AllowClose { get; set; }

    private async Task StartWebAsync()
    {
        try
        {
            // A fresh control for every attempt (a web view whose start failed is not used again), always at the bottom of the window.
            WebView2 Fresh()
            {
                if (_web is { } failed)
                {
                    _root.Children.Remove(failed);
                    failed.Dispose();
                }
                var created = new WebView2
                {
                    DefaultBackgroundColor = System.Drawing.Color.FromArgb(7, 9, 15),
                    // Dropping a file or link would navigate away from the HUD.
                    AllowExternalDrop = false
                };
                _root.Children.Insert(0, created);
                _web = created;
                return created;
            }

            var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CouchHud", "WebView2");
            // UI sounds (boot jingle, navigation) must play without a click first: it's a console, not a web page.
            // Remote debugging (loopback) only with the administrator's switch in HKLM (DeveloperMode); WEBVIEW2_* variables were
            // removed from the environment at start (App.OnStartup), so these are the only extra arguments.
            CoreWebView2EnvironmentOptions Options() => new(DeveloperMode.BrowserArgumentsFor(BrowserSurface.Hud, BrowserArguments.HudBase));
            // On the machine-wide runtime below Program Files, never on a per-user one the loader might prefer; with a lock on, a
            // machine-wide runtime that will not start ends in the desktop fallback below (Windows lock), not on another runtime.
            var web = await WebViewRuntimeLocator.StartAsync(Fresh, userData, Options, _hud.RuntimeStrict, "web");
            Configure(web.CoreWebView2);
        }
        catch (Exception ex)
        {
            // Typically the WebView2 runtime is missing. A black screen helps nobody.
            Console.Error.WriteLine($"[web] could not start WebView2: {ex}");
            _hud.FallbackToDesktop(Strings.Get("web.startFailed"));
        }
    }

    private void Configure(CoreWebView2 core)
    {
        core.Settings.AreDefaultContextMenusEnabled = _dev;
        core.Settings.AreDevToolsEnabled = _dev;
        core.Settings.AreBrowserAcceleratorKeysEnabled = _dev;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsSwipeNavigationEnabled = false;

        core.SetVirtualHostNameToFolderMapping(AppHost, Path.Combine(AppContext.BaseDirectory, "ui"), CoreWebView2HostResourceAccessKind.Allow);
        LocalFiles.Attach(core);

        // The bridge can start games and change the shell; only our own UI may ever be loaded.
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(AppOrigin, StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
        };
        core.FrameNavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(AppOrigin, StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;

        Bridge = new UiBridge(_hud, core);
        core.WebMessageReceived += Bridge.OnMessage;
        core.ProcessFailed += OnProcessFailed;

        core.Navigate($"{AppOrigin}index.html");
        // At start other programs (autostart, the desktop coming up) may have taken the front: once more when the HUD is ready.
        core.NavigationCompleted += (_, _) =>
        {
            if (_frontAfterLoad || _dev) return;
            _frontAfterLoad = true;
            BringToFront();
        };
        if (_dev) core.OpenDevToolsWindow();
    }

    private (double X, double Y, double W, double H) _browserRect;
    private bool _browserFull;

    /// <summary>The controller steers the page: the keyboard focus is in the page, otherwise in the HUD.</summary>
    public bool BrowserPageFocus { get; set; }

    /// <summary>Gives the browser page its place in the window (a second web view of its own, without the bridge), out of sight until the HUD says where it goes.</summary>
    public void AttachBrowser(BrowserPane pane)
    {
        if (_browser != pane)
        {
            if (_browser is not null) _root.Children.Remove(_browser);
            _browser = pane;
            _root.Children.Add(pane);
        }
        _browserRect = (-4000, 0, 800, 600);
        ApplyBrowserRect();
    }

    /// <summary>The page lies over the HUD's page area (device-independent pixels).</summary>
    public void PlaceBrowser(BrowserPane pane, double x, double y, double width, double height)
    {
        if (_browser != pane) AttachBrowser(pane);
        _browserRect = (x, y, width, height);
        pane.Visibility = Visibility.Visible;
        ApplyBrowserRect();
        RaiseBrowser();
    }

    /// <summary>
    /// The page's window must lie above the HUD's: two web views are two child windows, and the one made last is not
    /// always the one on top (the HUD's gets raised by focus changes). Set explicitly after every placement and focus change.
    /// </summary>
    public void RaiseBrowser()
    {
        void Raise()
        {
            if (_browser is not { IsVisible: true } pane) return;
            var handle = pane.PageHandle;
            if (handle != IntPtr.Zero) NativeMethods.SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpNoActivate);
        }
        Raise();
        // The window of a web view exists only after its first layout pass: once more when the dispatcher is idle.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, Raise);
    }

    /// <summary>Over the whole window (a video in full screen) and back.</summary>
    public void SetBrowserFullScreen(bool on)
    {
        _browserFull = on;
        ApplyBrowserRect();
        RaiseBrowser();
    }

    private void ApplyBrowserRect()
    {
        if (_browser is not { } pane) return;
        if (_browserFull)
        {
            pane.HorizontalAlignment = HorizontalAlignment.Stretch;
            pane.VerticalAlignment = VerticalAlignment.Stretch;
            pane.Margin = new Thickness(0);
            pane.Width = double.NaN;
            pane.Height = double.NaN;
            return;
        }
        pane.HorizontalAlignment = HorizontalAlignment.Left;
        pane.VerticalAlignment = VerticalAlignment.Top;
        pane.Margin = new Thickness(_browserRect.X, _browserRect.Y, 0, 0);
        pane.Width = _browserRect.W;
        pane.Height = _browserRect.H;
    }

    /// <summary>Leaving the Browser tab: the page is hidden but stays open.</summary>
    public void HideBrowser()
    {
        if (_browser is not null) _browser.Visibility = Visibility.Collapsed;
        _browserFull = false;
        FocusHud();
    }

    public void FocusHud()
    {
        _web?.Focus();
        // Focusing the HUD must not put its window over a page that is still on screen.
        RaiseBrowser();
    }

    /// <summary>Ends the browser page for good.</summary>
    public void RemoveBrowser()
    {
        if (_browser is { } pane)
        {
            _root.Children.Remove(pane);
            pane.Dispose();
            _browser = null;
        }
        _browserFull = false;
        BrowserPageFocus = false;
        _web?.Focus();
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        Console.Error.WriteLine($"[web] process failed: {e.ProcessFailedKind}");
        if (AllowClose) return;

        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                _web?.Reload();
                break;
            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                // The whole WebView is dead; Reload would throw. Build a new one.
                Dispatcher.InvokeAsync(RestartWebAsync);
                break;
        }
    }

    private async Task RestartWebAsync()
    {
        var now = DateTime.UtcNow;
        _restarts.RemoveAll(t => now - t > RestartWindow);
        _restarts.Add(now);
        if (_restarts.Count > MaxRestarts)
        {
            _hud.FallbackToDesktop(Strings.Get("web.crashing"));
            return;
        }

        var old = _web;
        _web = null;
        Bridge = null;
        if (old is not null) _root.Children.Remove(old);
        old?.Dispose();
        await StartWebAsync();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (AllowClose) return;
        // As a shell there is nothing behind us, so Alt+F4 must not leave a black screen.
        e.Cancel = true;
        if (_dev) _hud.Quit();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Resolution or monitor change (TV switched on, HDMI replugged): cover the whole screen again.
        if (msg == NativeMethods.WmDisplayChange && !_dev && IsVisible) Dispatcher.BeginInvoke(() => FitToMonitor(primary: true));
        // The session end was called off after all (wParam false): CouchHud is still here and stays.
        if (msg == NativeMethods.WmEndSession && wParam == IntPtr.Zero)
        {
            Console.WriteLine("[window] session end cancelled");
            AllowClose = false;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Covers the real monitor, taskbar area included (pixels, no DPI conversion): the one the window
    /// is on, or the primary one after the monitors changed (the old one may be gone or switched off).
    /// </summary>
    private void FitToMonitor(bool primary = false)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var monitor = primary
            ? NativeMethods.MonitorFromPoint(new NativeMethods.NativePoint(), NativeMethods.MonitorDefaultToPrimary)
            : NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MonitorDefaultToNearest);
        var info = new NativeMethods.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return;
        var r = info.Monitor;
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
            NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
    }

    /// <summary>
    /// Windows blocks SetForegroundWindow for background processes unless the caller "received
    /// the last input event". So: share the input queue of the current foreground thread, toggle
    /// topmost to lift the window above the others, and only if that fails tap Alt as a last resort.
    /// </summary>
    public void BringToFront()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.ShowWindow(hwnd, NativeMethods.SwRestore);
        if (!_dev)
        {
            FitToMonitor();
            // Taskbar stays below the HUD even if Windows does not hand us the foreground.
            TaskbarFullscreen.Mark(hwnd, true);
        }

        var thisThread = NativeMethods.GetCurrentThreadId();
        var foreground = NativeMethods.GetForegroundWindow();
        var foreThread = foreground == IntPtr.Zero ? 0 : NativeMethods.GetWindowThreadProcessId(foreground, out _);
        // Never share the input queue with a window that does not answer: that would hang us as well.
        var attached = foreThread != 0 && foreThread != thisThread && !NativeMethods.IsHungAppWindow(foreground)
            && NativeMethods.AttachThreadInput(thisThread, foreThread, true);
        try
        {
            const uint flags = NativeMethods.SwpNoMove | NativeMethods.SwpNoSize | NativeMethods.SwpShowWindow | NativeMethods.SwpNoActivate;
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HwndTopmost, 0, 0, 0, 0, flags);
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HwndNoTopmost, 0, 0, 0, 0, flags);
            NativeMethods.BringWindowToTop(hwnd);
            NativeMethods.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attached) NativeMethods.AttachThreadInput(thisThread, foreThread, false);
        }
        if (NativeMethods.GetForegroundWindow() != hwnd)
        {
            Console.WriteLine("[window] foreground not granted, trying the Alt tap");
            // CouchHUD's own key: never counted as someone at the PC (the couch browser's unlock, Bluetooth).
            KeyboardInput.MarkInjected();
            NativeMethods.keybd_event(NativeMethods.VkMenu, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VkMenu, 0, NativeMethods.KeyEventFKeyUp, UIntPtr.Zero);
            KeyboardInput.MarkInjected();
            NativeMethods.SetForegroundWindow(hwnd);
        }
        if (NativeMethods.GetForegroundWindow() != hwnd)
        {
            // Still not in front: do not let the taskbar button blink at the couch.
            var flash = new NativeMethods.FlashInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.FlashInfo>(), Hwnd = hwnd, Flags = NativeMethods.FlashwStop };
            NativeMethods.FlashWindowEx(ref flash);
        }
        Activate();
        if (_browser is { IsVisible: true } pane && BrowserPageFocus) pane.FocusPage();
        else _web?.Focus();
        RaiseBrowser();
    }
}
