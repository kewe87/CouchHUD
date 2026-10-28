using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CouchHud.Core.Config;
using CouchHud.Core.Library;
using CouchHud.Core.Parental;
using CouchHud.Sdk;
using CouchHud.Core.Localization;

namespace CouchHud.Shell.Services;

/// <summary>A small message above the running game: topmost, never takes focus or clicks.</summary>
public sealed class GameToast : Window
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000, WsExToolWindow = 0x80, WsExTransparent = 0x20;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    private static GameToast? _current;

    /// <param name="image">A small picture on the left (the screenshot just taken), optional.</param>
    public static void Show(string title, string body, TimeSpan duration, ImageSource? image = null)
    {
        _current?.Close();
        _current = new GameToast(title, body, duration, image);
        _current.Show();
    }

    /// <summary>Closes the toast on screen, if any. True when one was open (a screenshot waits a moment for it to be gone).</summary>
    public static bool CloseCurrent()
    {
        var open = _current;
        if (open is null) return false;
        _current = null;
        open.Close();
        return true;
    }

    private GameToast(string title, string body, TimeSpan duration, ImageSource? image)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        var text = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = title, Foreground = Brushes.White, FontSize = 26, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily("Segoe UI") },
                new TextBlock { Text = body, Foreground = new SolidColorBrush(Color.FromRgb(210, 214, 224)), FontSize = 18, Margin = new Thickness(0, 4, 0, 0), FontFamily = new FontFamily("Segoe UI") }
            }
        };
        UIElement inner = text;
        if (image is not null)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(6),
                ClipToBounds = true,
                Margin = new Thickness(0, 0, 20, 0),
                Child = new Image { Source = image, Height = 108, Stretch = Stretch.Uniform }
            });
            row.Children.Add(text);
            inner = row;
        }
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 12, 16, 28)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(255, 176, 0)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(28, 16, 28, 16),
            Child = inner
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GwlExStyle, GetWindowLong(hwnd, GwlExStyle) | WsExNoActivate | WsExToolWindow | WsExTransparent);
        };
        Loaded += (_, _) =>
        {
            Left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2;
            Top = SystemParameters.PrimaryScreenHeight * 0.06;
        };
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Close();
        };
        Closed += (_, _) =>
        {
            timer.Stop();
            if (_current == this) _current = null;
        };
        timer.Start();
    }
}

/// <summary>What the controller needs from the HUD.</summary>
public sealed record ParentalHooks(
    Func<GameSession?> Session,
    /// <summary>Other library games running next to the session (started outside CouchHud).</summary>
    Func<IReadOnlyList<GameSession>> Others,
    Func<IReadOnlyCollection<LibraryItem>> Library,
    /// <summary>All processes of the session are frozen (a partly frozen game still runs and counts).</summary>
    Func<bool> Paused,
    /// <summary>The couch lock is on, CouchHud runs as the shell and not in PC mode: the rules can be enforced.</summary>
    Func<bool> Enforced,
    Func<bool> HudActive,
    Action<string, string> Notify,
    Action BringHud,
    /// <summary>Asks the given games (all running ones for null) to close and ends them after two minutes, unless cancelled.</summary>
    Func<string?, CancellationToken, Task> CloseGame,
    /// <summary>Last resort when a game cannot be ended: the Windows lock screen.</summary>
    Action LockWindows,
    Func<Task<DateTimeOffset?>> RemoteTime,
    Action<string, object?> Emit);

/// <summary>
/// Parental controls in the shell: feeds the running game into the play time account, warns,
/// brings the HUD forward when the time is up and closes the game if nobody reacts.
/// </summary>
public sealed class ParentalController
{
    /// <summary>After this long without a decision the game is asked to close (then ended after two minutes).</summary>
    public static readonly TimeSpan AutoClose = TimeSpan.FromSeconds(60);

    private readonly ConfigStore _config;
    private readonly ParentalHooks _hooks;
    private long _expiredAt = -1;
    private long _nagAt;
    private long _clockCheckAt;
    private bool _closing;
    private CancellationTokenSource? _closeCts;
    private bool _checkingClock;
    // A game that ran without a start through CouchHud and breaks the rules (locked, age, time): treated like time that is up.
    private string? _blockedId;
    private string _lastStatus = "";

    public ParentalController(ConfigStore config, string dataDir, byte[]? key, ParentalHooks hooks)
    {
        _config = config;
        _hooks = hooks;
        // The account of the profile on the couch: playtime.json for the default profile, playtime-<id>.json for the others.
        // An unknown or broken profile is the default one (ConfigStore/ProfileView), so its rules and its account apply.
        var profile = config.Current.ActiveProfile;
        ProfileId = profile.Id;
        var file = ProfileIds.LedgerFile(dataDir, ProfileId);
        var ledger = new PlayLedger(file, key, expectExisting: PlayLedger.ShouldExist(file, profile.Rules.LedgerStarted));
        Service = new ParentalService(() => _config.Current.Profile(ProfileId).Rules, ledger,
            new TrustedClock(() => DateTimeOffset.UtcNow, () => Environment.TickCount64, ZoneOffset, ledger.LastUtc));
        Service.Warning += OnWarning;
        if (ledger.Tampered) Console.Error.WriteLine("[parental] the play time file was changed or removed");
        else if (!profile.IsDefault && config.Current.ParentalEnabled && !profile.Rules.LedgerStarted)
        {
            // A further profile's first account: written now and marked, so from here on a missing file is tampering.
            Service.Flush();
            if (File.Exists(file))
                config.Update(c =>
                {
                    var p = c.Profile(ProfileId);
                    if (p.Id == ProfileId) p.Rules.LedgerStarted = true;
                });
        }
    }

    private TimeSpan ZoneOffset(DateTimeOffset at)
    {
        // The time zone the day limits go by is part of this profile's rules (set when the parental controls were switched on).
        var id = _config.Current.Profile(ProfileId).Rules.TimeZoneId;
        try
        {
            return (string.IsNullOrEmpty(id) ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(id)).GetUtcOffset(at);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return TimeZoneInfo.Local.GetUtcOffset(at);
        }
    }

    public ParentalService Service { get; }

    /// <summary>The profile whose rules and play time account this controller keeps (a profile switch needs a new controller).</summary>
    public string ProfileId { get; }

    public bool Closing => _closing;

    /// <summary>A game started outside CouchHud breaks the rules and waits for the parents or the end of the grace time.</summary>
    public bool Blocked => _blockedId is not null;

    public string? BlockedGame { get; private set; }

    public string? BlockedMessage { get; private set; }

    /// <summary>The last attempt to end the game failed (elevated or hidden process).</summary>
    public bool CloseFailed { get; private set; }

    private static string Minutes(int m) => Strings.Plural("parental.minutes", m);
    private static string MinutesRef(int m) => Strings.RefPlural("parental.minutes", m);

    /// <summary>The entry whose time is counted: the session (apps only if they count).</summary>
    private LibraryItem? Counted()
    {
        var item = _hooks.Session()?.Item;
        if (item is null) return null;
        return item.Type == ItemType.Game || _config.Current.Profile(ProfileId).Rules.CountApps ? item : null;
    }

    private void OnWarning(ParentalWarning warning)
    {
        switch (warning)
        {
            case ParentalWarning.Minutes15:
            case ParentalWarning.Minutes5:
            {
                // The real time left: the first warning of a game that starts with 9 minutes left says 9.
                var m = Math.Max(1, (int)Math.Ceiling((Service.LastRemainingSeconds ?? 0) / 60.0));
                _hooks.Notify(Strings.Ref("notif.app.parental"), Strings.Ref("parental.timeLeft", MinutesRef(m)));
                GameToast.Show(Strings.Get("notif.app.parental"), Strings.Get("parental.timeLeft", Minutes(m)), TimeSpan.FromSeconds(9));
                _hooks.Emit("parental", new { kind = "warn", minutes = m });
                break;
            }
            case ParentalWarning.Expired:
                _expiredAt = Environment.TickCount64;
                _nagAt = _expiredAt;
                _hooks.Notify(Strings.Ref("notif.app.parental"), Strings.Ref("parental.timeUp"));
                if (_hooks.Enforced())
                {
                    _hooks.BringHud();
                    GameToast.Show(Strings.Get("parental.timeUpTitle"), Strings.Get("parental.saveNow"), TimeSpan.FromSeconds(9));
                }
                else
                {
                    GameToast.Show(Strings.Get("parental.timeUpTitle"), Strings.Get("parental.noEnforce"), TimeSpan.FromSeconds(9));
                }
                _hooks.Emit("parental", new { kind = "expired", advisory = !_hooks.Enforced() });
                break;
        }
    }

    /// <summary>Every few seconds on the UI thread.</summary>
    public void Tick()
    {
        var session = _hooks.Session();
        var game = Counted();
        // Several games at once count once, but all of them end with the time.
        var playing = game is not null && !_hooks.Paused() ? ParentalService.LimitKey(game) : null;
        Service.Tick(playing, game is null ? null : ParentalService.LimitKey(game));
        CheckClock();

        var now = Environment.TickCount64;
        if (_blockedId is not null && !(session?.Item.Id == _blockedId || _hooks.Others().Any(o => o.Item.Id == _blockedId))) ClearBlock();
        if (session is null)
        {
            _expiredAt = -1;
            _closing = false;
            CloseFailed = false;
        }
        else if ((Service.Expired || Blocked) && _hooks.Enforced())
        {
            if (_expiredAt < 0) _expiredAt = now;
            // Alt-Tab back into the game: the HUD comes forward again.
            if (!_hooks.HudActive() && !_closing && now - _nagAt > 20_000)
            {
                _nagAt = now;
                _hooks.BringHud();
                _hooks.Emit("parental", new { kind = "expired", advisory = false });
            }
            if (!_closing && now - _expiredAt > AutoClose.TotalMilliseconds) CloseNow(Service.Expired ? null : _blockedId);
        }
        else if (!Service.Expired && !Blocked)
        {
            _expiredAt = -1;
        }

        // The header and guide menu follow the clock.
        var st = Service.Status(game is null ? null : ParentalService.LimitKey(game));
        var key = $"{st.Enabled}|{(st.RemainingSeconds is { } r ? Math.Max(0, (r + 59) / 60) : -1)}|{st.Limiter}|{st.InWindow}|{st.ClockJumped}|{game?.Id}|{Service.Expired}|{_closing}|{CloseFailed}|{_blockedId}";
        if (key != _lastStatus)
        {
            _lastStatus = key;
            _hooks.Emit("parental", new { kind = "status" });
        }
    }

    /// <summary>
    /// The system clock disagrees with the elapsed time. A forward jump is believed only when a web server's
    /// date confirms it (a real correction, e.g. after the clock battery ran out), never because the child says so.
    /// </summary>
    private void CheckClock()
    {
        if (!Service.Clock.Jumped || !Service.Enabled || _checkingClock) return;
        var now = Environment.TickCount64;
        // Right after a start the network may not be up yet: asking again soon, or the day would stay stale for minutes.
        if (_clockCheckAt != 0 && now - _clockCheckAt < 30_000) return;
        _clockCheckAt = now;
        _checkingClock = true;
        Task.Run(async () =>
        {
            try
            {
                var remote = await _hooks.RemoteTime();
                if (remote is { } r && (r - DateTimeOffset.UtcNow).Duration() < TimeSpan.FromMinutes(3))
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Service.Clock.AcceptSystemTime());
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[parental] time check: {ex.Message}");
            }
            finally
            {
                _checkingClock = false;
            }
        });
    }

    /// <summary>
    /// Asks the game(s) to close; the shell ends them after two minutes unless <see cref="Extend"/> calls it off. If it fails,
    /// remind again and lock Windows, but only while the time is still up.
    /// </summary>
    public void CloseNow(string? onlyItem = null)
    {
        if (_closing) return;
        _closing = true;
        CloseFailed = false;
        _closeCts?.Dispose();
        var cts = _closeCts = new CancellationTokenSource();
        var token = cts.Token;
        _hooks.Notify(Strings.Ref("notif.app.parental"), Strings.Ref("parental.closing"));
        Task.Run(async () =>
        {
            var failed = false;
            try
            {
                await _hooks.CloseGame(onlyItem, token);
                await Task.Delay(TimeSpan.FromSeconds(4), token);
            }
            catch (OperationCanceledException)
            {
                // The parents added time: nothing to do any more.
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[parental] closing the game: {ex.Message}");
                failed = true;
            }
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                // Called off in the meantime: Extend has reset the state, and the game goes on.
                if (token.IsCancellationRequested) return;
                failed |= _hooks.Session() is not null;
                _closing = false;
                if (!failed) return;
                // Still out of time? (A late extension must not lock the PC.)
                if (!Service.Expired && !Blocked) return;
                // Could not end it (elevated, hidden, unknown process): the parents get the decision, the PC gets locked.
                CloseFailed = true;
                _expiredAt = Environment.TickCount64;
                _nagAt = _expiredAt;
                Console.Error.WriteLine("[parental] the game could not be ended, locking Windows");
                _hooks.Notify(Strings.Ref("notif.app.parental"), Strings.Ref("parental.cannotClose"));
                _hooks.BringHud();
                _hooks.Emit("parental", new { kind = "expired", advisory = false });
                _hooks.LockWindows();
            });
        });
    }

    /// <summary>
    /// A game that is running without a start through CouchHud was checked against the rules and does not pass
    /// (<paramref name="check"/> not Ok): the HUD comes forward with the time-is-up screen, and after the grace time the game is closed
    /// unless the parents allow it with their code (extending the time).
    /// </summary>
    public void Block(GameSession session, LaunchCheck check)
    {
        if (check.Ok || !_hooks.Enforced() || _blockedId is not null) return;
        _blockedId = session.Item.Id;
        BlockedGame = session.Item.Name;
        BlockedMessage = check.Message;
        _expiredAt = Environment.TickCount64;
        _nagAt = _expiredAt;
        _hooks.Notify(Strings.Ref("notif.app.parental"), Strings.Ref("parental.blockedExternal", session.Item.Name));
        _hooks.BringHud();
        _hooks.Emit("parental", new { kind = "expired", advisory = false });
    }

    private void ClearBlock()
    {
        _blockedId = null;
        BlockedGame = null;
        BlockedMessage = null;
    }

    public void Extend(int minutes)
    {
        Service.Extend(minutes);
        // Time added while the game is being closed: the closing is called off, it must not end the game or lock Windows after all.
        _closeCts?.Cancel();
        _closing = false;
        ClearBlock();
        _expiredAt = -1;
        _nagAt = 0;
        CloseFailed = false;
    }

    public sealed record LaunchCheck(bool Ok, string? Reason, string Message, bool NeedsCode, bool Advisory, bool UsesCode = false);

    private static string CodeText(string reason, int maxAge, string system) => reason switch
    {
        "locked" => Strings.Get("parental.codeOnly"),
        "age" or "unknown-age" => AgeLimitText(system, maxAge, reason == "unknown-age"),
        _ => Strings.Get("parental.cannotTrack")
    };

    /// <summary>The age limit in a message: by the system's label ("USK 12", "PEGI 7", "ESRB T") or, for a limit off its steps, by the age.</summary>
    internal static string AgeLimitText(string system, int limit, bool unknown) =>
        AgeRatings.LimitLabel(system, limit) is { } label
            ? Strings.Get(unknown ? "parental.unknownAgeCode" : "parental.ageCode", label)
            : Strings.Get(unknown ? "parental.unknownAgeCodeYears" : "parental.ageCodeYears", limit);

    /// <summary>Time rules, locked games, age and measurability. Without enforcement (no lock, not the shell) it only advises.</summary>
    /// <param name="grant">The parents just entered their code for this one start.</param>
    public LaunchCheck CheckLaunch(LibraryItem item, int? age, bool grant)
    {
        var cfg = _config.Current.Profile(ProfileId).Rules;
        if (!cfg.Enabled) return new LaunchCheck(true, null, "", false, false);
        var enforced = _hooks.Enforced();
        var problems = new List<string>();
        string? reason = null;

        var counts = item.Type == ItemType.Game || cfg.CountApps;
        if (counts)
        {
            var time = Service.CanStart(ParentalService.LimitKey(item));
            if (!time.Ok)
            {
                problems.Add(time.Message);
                reason = time.Reason;
            }
        }
        var codeReason = ParentalService.CodeReason(cfg, item, _hooks.Library(), age);
        var usesCode = false;
        if (codeReason is not null)
        {
            if (grant) usesCode = true;
            else
            {
                var system = AgeRatings.Resolve(_config.Current.AgeRatingSystem, WindowsRegion.Current());
                problems.Add(CodeText(codeReason, AgeRatings.LimitFor(cfg.AgeLimit, system), system));
            }
        }

        if (problems.Count == 0) return new LaunchCheck(true, null, "", false, false, usesCode);
        var message = string.Join(". ", problems);
        return enforced
            ? new LaunchCheck(false, reason, message, codeReason is not null && reason is null, false)
            : new LaunchCheck(true, null, Strings.Get("parental.advisory", message), false, true);
    }
}
