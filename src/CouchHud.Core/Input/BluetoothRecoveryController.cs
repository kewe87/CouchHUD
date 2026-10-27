namespace CouchHud.Core.Input;

/// <summary>The Bluetooth radio as the recovery sees it (Windows.Devices.Radios in the shell, a fake in the tests).</summary>
public interface IBluetoothRadio
{
    /// <summary>The radio changed its state; may be raised on any thread.</summary>
    event Action<RadioStatus>? RadioChanged;

    /// <summary>The radio's state; also starts following its changes.</summary>
    Task<RadioStatus> StatusAsync();

    /// <summary>Paired Bluetooth game controllers (only reads what Windows knows: no search, no pairing).</summary>
    Task<int> PairedControllersAsync();

    /// <summary>Every radio of the PC is off (airplane mode).</summary>
    Task<bool> AirplaneModeAsync();

    Task SetRadioAsync(bool on);
}

/// <summary>
/// Bluetooth off takes a Bluetooth controller with it, and without a controller a locked couch is hard to leave. At the start (so
/// also after a restart of the PC) and whenever the radio goes off, this switches it on again while a Bluetooth controller is
/// paired and no other controller is connected (decision: <see cref="BluetoothRecovery"/>). When keyboard or mouse were just in
/// use, someone may have meant it: then the UI shows a countdown that can be cancelled; nobody cancels, the radio comes back.
/// Only ever switches the existing radio on: never pairs, never makes the PC discoverable. Failures back off
/// (<see cref="RecoveryBackoff"/>), a radio that is disabled or missing is only logged.
/// All state lives on one thread: every callback goes through <c>post</c>.
/// </summary>
/// <param name="emit">Phase for the UI: countdown (with seconds), idle, enabled, failed, paused.</param>
/// <param name="post">Runs an action on the owner's thread (the UI thread in the shell).</param>
/// <param name="clock">Monotonic time (never the wall clock).</param>
/// <param name="delay">Waits; cancelled through the token.</param>
/// <param name="remembered">Paired controllers seen the last time the radio was on (kept over restarts), null: none kept.</param>
/// <param name="remember">Keeps that number.</param>
public sealed class BluetoothRecoveryController(IBluetoothRadio radio, Func<bool> enabled, Func<int> connectedPads, Func<bool> otherInputRecent,
    Action<string, int?> emit, Action<Action> post, Func<TimeSpan> clock, Func<TimeSpan, CancellationToken, Task> delay, Action<string> log,
    Func<int>? remembered = null, Action<int>? remember = null)
    : IDisposable
{
    /// <summary>The pads of a radio that just went off need a moment to drop out of XInput.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);
    /// <summary>At the start the pads need a moment to show up (the poller re-checks empty slots every 2 s).</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(5);

    private readonly RecoveryBackoff _backoff = new();
    private CancellationTokenSource? _countdown;
    private CancellationTokenSource? _pending;
    private bool _busy;
    private RecoveryTrigger? _rerun;
    // A countdown ended (ran out or "now"): the UI gets a closing phase whatever happens next.
    private bool _closeCountdown;
    // Left off on purpose: stays off until the radio is on again or CouchHUD starts anew.
    private bool _declined;
    private string? _lastLogged;
    private bool _gaveUpAnnounced;
    private bool _started;
    private volatile bool _disposed;

    public bool CountdownRunning => _countdown is not null;

    /// <summary>Left off on purpose or given up: no automatic switch-on until Bluetooth is on again or the next start.</summary>
    public bool Paused => _declined || _backoff.GaveUp;

    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        radio.RadioChanged += OnRadioChanged;
        // Follow the radio from the start, also with the setting off: switched on later, it knows the state at once.
        _ = WatchAsync();
        Schedule(RecoveryTrigger.Startup, StartDelay);
    }

    private async Task WatchAsync()
    {
        try
        {
            if (await radio.StatusAsync() == RadioStatus.On) await RememberPairedAsync();
        }
        catch (Exception ex)
        {
            LogOnce($"cannot read the radio ({ex.Message})");
        }
    }

    private void OnRadioChanged(RadioStatus status) => post(() =>
    {
        if (_disposed) return;
        OnRadio(status);
    });

    /// <summary>
    /// While the radio is on, the paired controllers are certainly known: keep their number. With the radio off Windows may list
    /// fewer (or none) of them, and the recovery would never act; then this number stands in.
    /// </summary>
    private async Task RememberPairedAsync()
    {
        if (remember is null) return;
        try
        {
            var paired = await radio.PairedControllersAsync();
            if (_disposed) return;
            if (remembered?.Invoke() != paired) remember(paired);
        }
        catch (Exception ex)
        {
            LogOnce($"cannot read the paired devices ({ex.Message})");
        }
    }

    private void OnRadio(RadioStatus status)
    {
        if (status == RadioStatus.On)
        {
            _declined = false;
            _lastLogged = null;
            StopCountdown();
            CancelPending();
            _ = RememberPairedAsync();
            return;
        }
        if (_countdown is not null) return;
        Schedule(status == RadioStatus.Off ? RecoveryTrigger.RadioOff : RecoveryTrigger.Retry, Settle);
    }

    /// <summary>The user chose "leave it off" on the countdown. False when no countdown runs (any more): nothing was left off.</summary>
    public bool Decline()
    {
        if (_disposed || _countdown is null) return false;
        _declined = true;
        StopCountdown();
        log("auto switch-on cancelled by the user (until Bluetooth is on again or CouchHUD restarts)");
        return true;
    }

    /// <summary>The user chose "switch on now" on the countdown.</summary>
    public void SkipCountdown()
    {
        if (_disposed || _countdown is null) return;
        EndCountdown();
    }

    /// <summary>
    /// The user switches Bluetooth off on CouchHUD's own page. With another way to work the couch (another pad, keyboard or mouse)
    /// that is meant: left off like "leave it off" (true; <see cref="UserSwitchOffFailed"/> if the switch fails). Without one it
    /// comes back on as the confirmation said (false; the radio's off event does the rest).
    /// </summary>
    public bool UserSwitchingOff(bool otherInput)
    {
        if (_disposed || !otherInput) return false;
        _declined = true;
        StopCountdown();
        CancelPending();
        log("Bluetooth switched off in CouchHUD with another input at hand: left off (until it is on again or CouchHUD restarts)");
        return true;
    }

    /// <summary>The radio did not go off after <see cref="UserSwitchingOff"/>: nothing to leave off.</summary>
    public void UserSwitchOffFailed()
    {
        if (_disposed) return;
        _declined = false;
    }

    /// <summary>Cancels and lets go of a timer; one that is already gone (end of CouchHUD) is fine.</summary>
    private static void Drop(CancellationTokenSource? cts)
    {
        if (cts is null) return;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        cts.Dispose();
    }

    private void StopCountdown()
    {
        if (_countdown is null) return;
        Drop(_countdown);
        _countdown = null;
        _closeCountdown = false;
        emit("idle", null);
    }

    private void EndCountdown()
    {
        Drop(_countdown);
        _countdown = null;
        _closeCountdown = true;
        CancelPending();
        Request(RecoveryTrigger.CountdownElapsed);
    }

    private void CancelPending()
    {
        Drop(_pending);
        _pending = null;
    }

    /// <summary>One pending evaluation at a time: a newer one replaces the older.</summary>
    private void Schedule(RecoveryTrigger trigger, TimeSpan wait)
    {
        CancelPending();
        var cts = new CancellationTokenSource();
        _pending = cts;
        Later(wait, cts.Token, () =>
        {
            if (_pending != cts) return;
            _pending = null;
            cts.Dispose();
            Request(trigger);
        });
    }

    private async void Later(TimeSpan wait, CancellationToken token, Action then)
    {
        if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
        try
        {
            await delay(wait, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            log($"timer: {ex.Message}");
            return;
        }
        if (token.IsCancellationRequested) return;
        post(() =>
        {
            if (_disposed || token.IsCancellationRequested) return;
            then();
        });
    }

    private void LogOnce(string text)
    {
        if (_lastLogged == text) return;
        _lastLogged = text;
        log(text);
    }

    // The ending countdown wins over anything else that comes in while a check runs.
    private static int Rank(RecoveryTrigger trigger) => trigger switch
    {
        RecoveryTrigger.CountdownElapsed => 3,
        RecoveryTrigger.RadioOff => 2,
        RecoveryTrigger.Startup => 1,
        _ => 0
    };

    /// <summary>Runs a check now, or right after the one that runs (never two at once, never one lost).</summary>
    private void Request(RecoveryTrigger trigger)
    {
        if (_disposed) return;
        if (_busy)
        {
            if (_rerun is not { } queued || Rank(trigger) > Rank(queued)) _rerun = trigger;
            return;
        }
        _ = RunAsync(trigger);
    }

    private async Task RunAsync(RecoveryTrigger trigger)
    {
        _busy = true;
        try
        {
            RecoveryTrigger? next = trigger;
            while (next is { } current && !_disposed)
            {
                _rerun = null;
                try
                {
                    await EvaluateAsync(current);
                }
                catch (Exception ex)
                {
                    log($"auto switch-on: {ex.Message}");
                    if (_closeCountdown) Close("failed");
                }
                next = _rerun;
            }
        }
        finally
        {
            _busy = false;
            _rerun = null;
            // Whatever the check found (radio on, a pad back, setting off, left off, waiting for the next window): the countdown is over.
            if (_closeCountdown && _countdown is null && !_disposed) Close("idle");
        }
    }

    private void Close(string phase)
    {
        _closeCountdown = false;
        emit(phase, null);
    }

    private async Task EvaluateAsync(RecoveryTrigger trigger)
    {
        if (_countdown is not null) return;
        if (!enabled()) return;
        if (_declined && trigger != RecoveryTrigger.Startup) return;
        RadioStatus status;
        int paired = 0;
        bool airplane = false;
        try
        {
            status = await radio.StatusAsync();
            if (status == RadioStatus.Off)
            {
                paired = await radio.PairedControllersAsync();
                if (paired <= 0) paired = Math.Max(0, remembered?.Invoke() ?? 0);
                if (paired > 0) airplane = await radio.AirplaneModeAsync();
            }
        }
        catch (Exception ex)
        {
            LogOnce($"auto switch-on: cannot read the radio or the paired devices ({ex.Message})");
            if (_closeCountdown) Close("failed");
            return;
        }
        if (_disposed) return;
        var decision = BluetoothRecovery.Decide(new RecoveryInput(enabled(), status, paired, connectedPads(), otherInputRecent(), trigger, airplane));
        switch (decision.Action)
        {
            case RecoveryAction.Log:
                LogOnce($"auto switch-on not possible: {decision.Reason}");
                if (_closeCountdown) Close("failed");
                return;
            case RecoveryAction.Countdown:
                StartCountdown(decision.Reason);
                return;
            case RecoveryAction.EnableNow:
                await EnableAsync(decision.Reason);
                return;
        }
    }

    private void StartCountdown(string reason)
    {
        var cts = new CancellationTokenSource();
        _countdown = cts;
        _closeCountdown = false;
        log($"radio off with a paired controller and no other pad ({reason}): switching on in {BluetoothRecovery.CountdownSeconds} s unless cancelled");
        emit("countdown", BluetoothRecovery.CountdownSeconds);
        Later(TimeSpan.FromSeconds(BluetoothRecovery.CountdownSeconds), cts.Token, () =>
        {
            if (_countdown != cts) return;
            // Not cancelled: the same checks again (a controller may have come back), then on without a second countdown.
            EndCountdown();
        });
    }

    private void AnnounceGaveUp(string why)
    {
        if (_gaveUpAnnounced)
        {
            if (_closeCountdown) Close("idle");
            return;
        }
        _gaveUpAnnounced = true;
        _closeCountdown = false;
        log($"auto switch-on paused until CouchHUD restarts ({why})");
        emit("paused", null);
    }

    private async Task EnableAsync(string reason)
    {
        var now = clock();
        if (!_backoff.MayTry(now))
        {
            if (_backoff.GaveUp) AnnounceGaveUp("Bluetooth kept being switched off, or too many attempts");
            else Schedule(RecoveryTrigger.Retry, _backoff.NextTry - now);
            return;
        }
        try
        {
            await radio.SetRadioAsync(true);
            _backoff.Succeeded(clock());
            _lastLogged = null;
            log($"radio switched on again ({reason})");
            _closeCountdown = false;
            emit("enabled", null);
        }
        catch (Exception ex)
        {
            _backoff.Failed(clock());
            log($"switching the radio on failed ({_backoff.Failures}x): {ex.Message}");
            if (_backoff.GaveUp)
            {
                AnnounceGaveUp("too many failures");
                return;
            }
            if (_closeCountdown) Close("failed");
            Schedule(RecoveryTrigger.Retry, _backoff.NextTry - clock());
        }
    }

    /// <summary>
    /// Stops at the end of CouchHUD; may be called from any thread. From then on every callback does nothing, and the timers are
    /// let go on the owner's thread (where they live), so nothing races with a check that is still running.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        radio.RadioChanged -= OnRadioChanged;
        try
        {
            post(() =>
            {
                Drop(_countdown);
                _countdown = null;
                CancelPending();
            });
        }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
        {
            // The owner's thread is gone already: the timers die with the process, their callbacks see _disposed.
        }
    }
}
