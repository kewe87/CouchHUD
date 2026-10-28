using System.Net;

namespace CouchHud.Core.Integrations.Tv;

/// <summary>Where the pairing is kept. The shell stores the key with DPAPI (CurrentUser) in config.json; the tests keep it in memory.</summary>
public interface ITvStore
{
    TvPairing? Load();
    void Save(TvPairing? pairing);

    /// <summary>Addresses and MACs that were ever paired over TLS (kept when the TV is forgotten): never paired without TLS again.</summary>
    IReadOnlyCollection<string> SecureSeen();

    void RememberSecure(IEnumerable<string> ids);
}

/// <summary>What the page and the Guide menu see. No secret: never the key, only whether a MAC is known.</summary>
public sealed record TvStatus(
    string Provider,
    string ProviderName,
    bool Paired,
    string? Address,
    string? Name,
    string? Model,
    bool Secure,
    bool MacKnown,
    string? Mac,
    /// <summary>"unknown", "on", "off" (not reachable), "waking" (Wake-on-LAN sent, waiting).</summary>
    string Power,
    int? Volume,
    bool? Muted,
    /// <summary>The input in front (an id of the TV's list), null when the TV shows an app or nothing is known.</summary>
    string? Input,
    /// <summary>"idle", "connecting", "prompt" (the TV shows the pairing question).</summary>
    string Pairing,
    /// <summary>The last error as a code (<see cref="TvError"/> in camel case), or null.</summary>
    string? Error,
    /// <summary>The last pairing replaced a TLS pairing of the same TV (address or MAC) whose certificate was a different one.</summary>
    bool CertReplaced = false,
    /// <summary>Some TV was paired with encryption on this PC before (only whether, never which address or MAC).</summary>
    bool SecureBefore = false);

/// <summary>Options of "TV follows CouchHUD" (expert, both off by default).</summary>
public sealed record TvFollow(bool OnWithCouchHud, bool OffWithPc);

/// <summary>
/// The TV for CouchHUD: one pairing, one connection at a time, opened when needed and closed after <see cref="IdleClose"/>. No polling:
/// the state is read when the page or the Guide menu asks. Reconnecting is bounded: every command opens at most one new connection, and
/// after a failed one automatic reads wait <see cref="Backoff"/> (a press by the user always tries once). Every call is async; nothing
/// here blocks a caller's thread.
/// </summary>
public sealed class TvController : IAsyncDisposable
{
    public static readonly TimeSpan IdleClose = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan Backoff = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan WakeWindow = TimeSpan.FromSeconds(25);
    public static readonly TimeSpan OffBudget = TimeSpan.FromSeconds(2.5);
    /// <summary>After a failed connection no new one before this, whoever asks (locked reads, the Guide menu, a press): the backoff
    /// for automatic reads cannot be got round by another call.</summary>
    public static readonly TimeSpan MinRetry = TimeSpan.FromSeconds(2);

    private readonly ITvProvider _provider;
    private readonly ITvStore _store;
    private readonly Action<TvStatus> _emit;
    private readonly Func<long> _nowMs;
    private readonly Func<byte[], IPAddress, CancellationToken, Task<bool>> _wake;
    private readonly Func<CancellationToken, Task<IReadOnlyList<TvDevice>>> _discover;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Throttle _wakeThrottle;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _lock = new();
    private readonly Timer _idle;

    private ITvSession? _session;
    private TvPairing? _pairing;
    private string _power = "unknown";
    private long _wakingUntil;
    private long _failedAt = long.MinValue;
    private long _offSentAt = long.MinValue;
    private long _connectFailedAt = long.MinValue;
    private TvError _connectError = TvError.Unreachable;
    private bool _certReplaced;
    private TvVolume? _volume;
    private string? _input;
    private string _pairingState = "idle";
    private TvError? _error;
    private CancellationTokenSource? _pairCts;
    private IReadOnlyList<TvInput>? _inputs;
    private int _disposed;

    public TvController(ITvProvider provider, ITvStore store, Action<TvStatus> emit, Func<long>? nowMs = null,
        Func<byte[], IPAddress, CancellationToken, Task<bool>>? wake = null, Func<CancellationToken, Task<IReadOnlyList<TvDevice>>>? discover = null)
    {
        _provider = provider;
        _store = store;
        _emit = emit;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _wake = wake ?? WakeOnLan.SendAsync;
        _discover = discover ?? (ct => SsdpDiscovery.SearchAsync(TimeSpan.FromSeconds(3), ct));
        _wakeThrottle = new Throttle(WakeOnLan.MinInterval, _nowMs);
        _idle = new Timer(_ => _ = CloseIdleAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _pairing = SafeLoad();
        // A TLS pairing from before the history existed counts too.
        if (_pairing is { Secure: true } p) _store.RememberSecure(Ids(p.Address, p.Mac));
    }

    private static IEnumerable<string> Ids(string? address, string? mac)
    {
        if (!string.IsNullOrEmpty(address)) yield return address;
        if (WakeOnLan.ParseMac(mac) is { } m) yield return WakeOnLan.Format(m);
    }

    /// <summary>Was this address or MAC ever paired over TLS (the history, or the pairing in use)?</summary>
    private bool WasSecure(string? address, string? mac)
    {
        var ids = Ids(address, mac).ToList();
        if (ids.Count == 0) return false;
        var seen = _store.SecureSeen();
        if (ids.Any(seen.Contains)) return true;
        lock (_lock) return _pairing is { Secure: true } p && Ids(p.Address, p.Mac).Any(ids.Contains);
    }

    private TvPairing? SafeLoad()
    {
        var p = _store.Load();
        if (p is null || p.Provider != _provider.Id || !IPAddress.TryParse(p.Address, out _) || string.IsNullOrEmpty(p.ClientKey)) return null;
        return p;
    }

    public bool Paired
    {
        get { lock (_lock) return _pairing is not null; }
    }

    public TvStatus Status()
    {
        var secureBefore = _store.SecureSeen().Count > 0;
        lock (_lock)
        {
            var p = _pairing;
            var power = _power;
            if (power != "on" && _nowMs() < _wakingUntil) power = "waking";
            return new TvStatus(_provider.Id, _provider.Name, p is not null, p?.Address, p?.Name, p?.Model, p?.Secure ?? false,
                p?.Mac is not null, p?.Mac, p is null ? "unknown" : power, _volume?.Volume, _volume?.Muted, _input, _pairingState, Code(_error), _certReplaced, secureBefore);
        }
    }

    public static string? Code(TvError? error) => error is null ? null : char.ToLowerInvariant(error.Value.ToString()[0]) + error.Value.ToString()[1..];

    private void Publish() => _emit(Status());

    // ---------------------------------------------------------------------------------------------------------------------------
    // Reading

    /// <summary>
    /// Refreshes power, volume and the input in front. <paramref name="user"/>: a press (tries even right after a failure); otherwise
    /// a read that respects the backoff (the page opening, the Guide menu).
    /// </summary>
    public async Task<TvStatus> RefreshAsync(bool user, CancellationToken ct = default)
    {
        if (!Paired) return Status();
        if (!user && _failedAt != long.MinValue && _nowMs() - _failedAt < Backoff.TotalMilliseconds) return Status();
        try
        {
            await WithSessionAsync(async (s, c) =>
            {
                var volume = await s.GetVolumeAsync(c);
                string? input = null;
                try
                {
                    var app = await s.GetForegroundAppAsync(c);
                    _inputs ??= await s.GetInputsAsync(c);
                    input = WebOsProtocol.InputOfApp(app, _inputs);
                }
                catch (TvException ex) when (ex.Code is TvError.Refused or TvError.BadAnswer)
                {
                    // Not every TV lets every key read the app in front; the volume is enough.
                }
                lock (_lock)
                {
                    _volume = volume;
                    _input = input;
                }
                return true;
            }, ct);
        }
        catch (TvException)
        {
            // Recorded in the status (WithSessionAsync).
        }
        return Status();
    }

    /// <summary>The TV's inputs (fresh from the TV).</summary>
    public async Task<IReadOnlyList<TvInput>> InputsAsync(CancellationToken ct = default)
    {
        var inputs = await WithSessionAsync((s, c) => s.GetInputsAsync(c), ct);
        lock (_lock) _inputs = inputs;
        return inputs;
    }

    // ---------------------------------------------------------------------------------------------------------------------------
    // Commands

    public async Task<TvStatus> VolumeStepAsync(bool up, CancellationToken ct = default)
    {
        var volume = await WithSessionAsync(async (s, c) =>
        {
            await s.VolumeStepAsync(up, c);
            return await s.GetVolumeAsync(c);
        }, ct);
        lock (_lock) _volume = volume;
        Publish();
        return Status();
    }

    public async Task<TvStatus> SetVolumeAsync(int volume, CancellationToken ct = default)
    {
        var v = Math.Clamp(volume, 0, 100);
        var now = await WithSessionAsync(async (s, c) =>
        {
            await s.SetVolumeAsync(v, c);
            return await s.GetVolumeAsync(c);
        }, ct);
        lock (_lock) _volume = now;
        Publish();
        return Status();
    }

    /// <summary>Mute on or off; null toggles (the state is read first).</summary>
    public async Task<TvStatus> MuteAsync(bool? mute, CancellationToken ct = default)
    {
        var now = await WithSessionAsync(async (s, c) =>
        {
            var target = mute ?? !(await s.GetVolumeAsync(c)).Muted;
            await s.SetMuteAsync(target, c);
            return await s.GetVolumeAsync(c);
        }, ct);
        lock (_lock) _volume = now;
        Publish();
        return Status();
    }

    /// <summary>Switches to an input of the TV's own list (checked against a fresh list: nothing the UI made up reaches the TV).</summary>
    public async Task<TvStatus> SwitchInputAsync(string inputId, CancellationToken ct = default)
    {
        if (!WebOsProtocol.IsValidInputId(inputId)) throw new ArgumentException("input");
        await WithSessionAsync(async (s, c) =>
        {
            var inputs = await s.GetInputsAsync(c);
            lock (_lock) _inputs = inputs;
            if (!inputs.Any(i => i.Id == inputId)) throw new ArgumentException("input");
            await s.SwitchInputAsync(inputId, c);
            return true;
        }, ct);
        lock (_lock) _input = inputId;
        Publish();
        return Status();
    }

    /// <summary>
    /// On: Wake-on-LAN to the paired TV's MAC (rate-limited), unless it answers already. Off: system/turnOff over the API.
    /// </summary>
    public async Task<TvStatus> PowerAsync(bool on, CancellationToken ct = default)
    {
        if (on) return await WakeAsync(user: true, ct);
        await WithSessionAsync(async (s, c) =>
        {
            await s.TurnOffAsync(c);
            return true;
        }, ct);
        lock (_lock)
        {
            _offSentAt = _nowMs();
            _power = "off";
            _wakingUntil = 0;
            _volume = null;
            _input = null;
        }
        await DropSessionAsync();
        Publish();
        return Status();
    }

    private async Task<TvStatus> WakeAsync(bool user, CancellationToken ct)
    {
        TvPairing pairing;
        lock (_lock) pairing = _pairing ?? throw new TvException(TvError.NotPaired);
        // Already on and answering: nothing to send.
        if (_session is { Open: true }) return Status();
        if (WakeOnLan.ParseMac(pairing.Mac) is not { } mac || !IPAddress.TryParse(pairing.Address, out var address)) throw new TvException(TvError.NoWake);
        if (!_wakeThrottle.TryTake())
        {
            // Sent a moment ago: the TV is starting; not an error for the user.
            return Status();
        }
        bool sent;
        try { sent = await _wake(mac, address, ct); }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException)
        {
            sent = false;
        }
        if (!sent)
        {
            if (user) throw new TvException(TvError.NoWake);
            return Status();
        }
        lock (_lock)
        {
            _wakingUntil = _nowMs() + (long)WakeWindow.TotalMilliseconds;
            _failedAt = long.MinValue;
            _error = null;
        }
        Publish();
        _ = CheckAfterWakeAsync();
        return Status();
    }

    /// <summary>Two looks after a wake (8 and 16 s): "waking" turns into "on" without anybody asking. Bounded, never more.</summary>
    private async Task CheckAfterWakeAsync()
    {
        try
        {
            for (var i = 0; i < 2; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(8), _stop.Token);
                var status = await RefreshAsync(user: true, _stop.Token);
                Publish();
                if (status.Power == "on") return;
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    // ---------------------------------------------------------------------------------------------------------------------------
    // TV follows CouchHUD

    /// <summary>CouchHUD started or the PC woke up: switch the TV on (only with the option, only a paired TV with a MAC).</summary>
    public Task FollowWakeAsync(TvFollow follow)
    {
        if (!follow.OnWithCouchHud || !Paired) return Task.CompletedTask;
        return SafeAsync(() => WakeAsync(user: false, _stop.Token));
    }

    /// <summary>
    /// The PC goes to sleep or shuts down from CouchHUD: switch the TV off, but only when the PC's input is in front on the TV (somebody
    /// watching TV or another input keeps it). Bounded to <see cref="OffBudget"/>; sent once even if sleep is announced twice.
    /// </summary>
    public async Task FollowSleepAsync(TvFollow follow)
    {
        if (!follow.OffWithPc || !Paired) return;
        if (_offSentAt != long.MinValue && _nowMs() - _offSentAt < 30_000) return;
        string? pcApp;
        lock (_lock) pcApp = _pairing?.PcApp;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        budget.CancelAfter(OffBudget);
        try
        {
            var off = await WithSessionAsync(async (s, c) =>
            {
                var app = await s.GetForegroundAppAsync(c);
                if (pcApp is null || app != pcApp) return false;
                await s.TurnOffAsync(c);
                return true;
            }, budget.Token);
            if (off)
            {
                lock (_lock)
                {
                    _offSentAt = _nowMs();
                    _power = "off";
                }
            }
        }
        catch (Exception ex) when (ex is TvException or OperationCanceledException)
        {
            // Best effort: the PC goes to sleep anyway.
        }
    }

    private static async Task SafeAsync(Func<Task> work)
    {
        try { await work(); }
        catch (Exception ex) when (ex is TvException or OperationCanceledException or ObjectDisposedException) { }
    }

    // ---------------------------------------------------------------------------------------------------------------------------
    // Pairing

    public Task<IReadOnlyList<TvDevice>> DiscoverAsync(CancellationToken ct = default) => _discover(ct);

    /// <summary>
    /// Pairs with the TV at <paramref name="address"/> (a local IP literal). The status says "prompt" while the TV asks; the new pairing
    /// replaces the old one only when the TV said yes. <paramref name="mac"/>: a MAC found by the caller (ARP cache) if the TV did not
    /// tell its own.
    /// </summary>
    /// <param name="allowPlain">The user confirmed an unencrypted pairing (expert mode) for a TV without TLS port. Still refused
    /// (<see cref="TvError.Downgrade"/>) for an address or MAC that was ever paired over TLS.</param>
    public async Task<TvStatus> PairAsync(IPAddress address, Func<IPAddress, string?>? macLookup = null, bool allowPlain = false, CancellationToken ct = default)
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            if (_pairingState != "idle") throw new TvException(TvError.Busy);
            _pairingState = "connecting";
            _error = null;
            _certReplaced = false;
            cts = _pairCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        }
        Publish();
        try
        {
            var canonical = (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
            string? lookedUp = null;
            if (allowPlain)
            {
                // Before anything is sent unencrypted: a TV that had TLS here (or this MAC) does not lose it.
                lookedUp = macLookup?.Invoke(address);
                if (WasSecure(canonical, lookedUp)) throw new TvException(TvError.Downgrade);
            }
            await _gate.WaitAsync(cts.Token);
            try
            {
                await DropSessionCoreAsync();
                TvPairing? old;
                lock (_lock) old = _pairing;
                var pairing = await _provider.PairAsync(address, () =>
                {
                    lock (_lock) _pairingState = "prompt";
                    Publish();
                }, allowPlain, cts.Token);
                if (pairing.Mac is null && (lookedUp ?? macLookup?.Invoke(address)) is { } found && WakeOnLan.ParseMac(found) is { } parsed)
                {
                    pairing = pairing with { Mac = WakeOnLan.Format(parsed) };
                }
                // The MAC the TV told only now: an unencrypted pairing with a TV that had TLS is not kept either.
                if (!pairing.Secure && WasSecure(pairing.Address, pairing.Mac)) throw new TvException(TvError.Downgrade);
                var replaced = pairing.Secure && old is { Secure: true, CertPin: { Length: > 0 } oldPin }
                    && (old.Address == pairing.Address || (old.Mac is not null && old.Mac == pairing.Mac))
                    && (pairing.CertPin is null || !WebOsProtocol.SamePin(oldPin, pairing.CertPin));
                _store.Save(pairing);
                if (pairing.Secure) _store.RememberSecure(Ids(pairing.Address, pairing.Mac));
                lock (_lock)
                {
                    _certReplaced = replaced;
                    _connectFailedAt = long.MinValue;
                    _pairing = pairing;
                    _power = "on";
                    _failedAt = long.MinValue;
                    _inputs = null;
                    _volume = null;
                    _input = null;
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (TvException ex)
        {
            lock (_lock) _error = ex.Code;
            throw;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            lock (_lock) _error = TvError.Denied;
            throw new TvException(TvError.Denied);
        }
        finally
        {
            lock (_lock)
            {
                _pairingState = "idle";
                if (ReferenceEquals(_pairCts, cts)) _pairCts = null;
            }
            cts.Dispose();
            Publish();
        }
        return Status();
    }

    public void CancelPair()
    {
        lock (_lock)
        {
            try { _pairCts?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>Forgets the TV: key, pin, MAC and address are removed.</summary>
    public async Task<TvStatus> ForgetAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await DropSessionCoreAsync();
            _store.Save(null);
            lock (_lock)
            {
                _pairing = null;
                _certReplaced = false;
                _connectFailedAt = long.MinValue;
                _power = "unknown";
                _volume = null;
                _input = null;
                _inputs = null;
                _error = null;
                _wakingUntil = 0;
            }
        }
        finally
        {
            _gate.Release();
        }
        Publish();
        return Status();
    }

    /// <summary>Sets or clears the MAC for Wake-on-LAN by hand (expert).</summary>
    public TvStatus SetMac(string? mac)
    {
        string? value = null;
        if (!string.IsNullOrWhiteSpace(mac)) value = WakeOnLan.ParseMac(mac) is { } parsed ? WakeOnLan.Format(parsed) : throw new ArgumentException("mac");
        lock (_lock)
        {
            if (_pairing is null) throw new TvException(TvError.NotPaired);
            _pairing = _pairing with { Mac = value };
            _store.Save(_pairing);
        }
        Publish();
        return Status();
    }

    // ---------------------------------------------------------------------------------------------------------------------------
    // The connection

    /// <summary>
    /// Runs <paramref name="work"/> on the open connection, opening one if needed (at most one new connection per call). A failure is
    /// recorded in the status; a refused certificate or key keeps its code until the TV is paired again.
    /// </summary>
    private async Task<T> WithSessionAsync<T>(Func<ITvSession, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        TvPairing pairing;
        lock (_lock) pairing = _pairing ?? throw new TvException(TvError.NotPaired);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        if (!await _gate.WaitAsync(TimeSpan.FromSeconds(8), linked.Token)) throw new TvException(TvError.Timeout, "busy");
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var fresh = false;
                if (_session is not { Open: true })
                {
                    await DropSessionCoreAsync();
                    if (_connectFailedAt != long.MinValue && _nowMs() - _connectFailedAt < MinRetry.TotalMilliseconds) throw new TvException(_connectError, "retry");
                    try
                    {
                        _session = await _provider.ConnectAsync(pairing, linked.Token);
                    }
                    catch (TvException ex)
                    {
                        _connectFailedAt = _nowMs();
                        _connectError = ex.Code;
                        throw;
                    }
                    _connectFailedAt = long.MinValue;
                    fresh = true;
                }
                try
                {
                    var result = await work(_session, linked.Token);
                    lock (_lock)
                    {
                        _power = "on";
                        _wakingUntil = 0;
                        _failedAt = long.MinValue;
                        _error = null;
                    }
                    ArmIdle();
                    return result;
                }
                catch (TvException ex) when (ex.Code == TvError.Unreachable && !fresh && attempt == 0)
                {
                    // A kept connection the TV had already closed (standby, idle): one new connection, never more.
                    await DropSessionCoreAsync();
                }
            }
        }
        catch (TvException ex)
        {
            lock (_lock)
            {
                _error = ex.Code;
                if (ex.Code is TvError.Unreachable or TvError.Timeout)
                {
                    _failedAt = _nowMs();
                    if (_nowMs() >= _wakingUntil) _power = "off";
                    _volume = null;
                    _input = null;
                }
            }
            await DropSessionCoreAsync();
            Publish();
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ArmIdle()
    {
        try { _idle.Change(IdleClose, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    private async Task CloseIdleAsync()
    {
        if (!await _gate.WaitAsync(0)) return;
        try { await DropSessionCoreAsync(); }
        finally { _gate.Release(); }
    }

    private async Task DropSessionAsync()
    {
        await _gate.WaitAsync();
        try { await DropSessionCoreAsync(); }
        finally { _gate.Release(); }
    }

    /// <summary>Closes the connection (the caller holds the gate).</summary>
    private async Task DropSessionCoreAsync()
    {
        var s = _session;
        _session = null;
        if (s is null) return;
        try { await s.DisposeAsync(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Net.WebSockets.WebSocketException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _stop.Cancel();
        await _idle.DisposeAsync();
        var s = _session;
        _session = null;
        if (s is not null)
        {
            try { await s.DisposeAsync(); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or System.Net.WebSockets.WebSocketException) { }
        }
    }
}
