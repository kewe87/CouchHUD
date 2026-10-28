using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace CouchHud.Core.Integrations.Tv;

/// <summary>Limits and ports of the webOS client; the tests point it at a fake TV on loopback with short timeouts.</summary>
public sealed record WebOsOptions
{
    public int SecurePort { get; init; } = WebOsProtocol.SecurePort;
    public int LegacyPort { get; init; } = WebOsProtocol.LegacyPort;
    /// <summary>Only for the tests' fake TV on 127.0.0.1; the app never sets it.</summary>
    public bool AllowLoopback { get; init; }
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(4);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>How long the prompt on the TV waits for "Accept".</summary>
    public TimeSpan PromptTimeout { get; init; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// LG webOS over SSAP (see <see cref="WebOsProtocol"/>). Every connection goes to an IP literal of the local network only (checked on the
/// address actually connected, no DNS, no proxy, no redirects); TLS on port 3001 with the TV's self-signed certificate pinned by its
/// public key at pairing (trust on first use) and required afterwards. Certificate validation is only ever replaced on this one
/// handler, never globally. A TV that only has the old plain port 3000 is paired over it (the TV then says so: "unencrypted"); a TV
/// paired over TLS is never contacted without TLS again.
/// </summary>
public sealed class WebOsClient(WebOsOptions? options = null) : ITvProvider
{
    public const string ProviderId = "lg-webos";
    private readonly WebOsOptions _o = options ?? new WebOsOptions();

    public string Id => ProviderId;
    public string Name => "LG webOS";

    public async Task<TvPairing> PairAsync(IPAddress address, Action onPrompt, bool allowPlain, CancellationToken ct)
    {
        if (!TvAddressPolicy.IsAllowed(address, _o.AllowLoopback)) throw new TvException(TvError.NotLocal);
        Connected socket;
        try
        {
            socket = await ConnectSocketAsync(address, secure: true, expectedPin: null, ct);
        }
        catch (TvException ex) when (ex.Code == TvError.Unreachable)
        {
            // Only "nobody listens on 3001" can fall back to the old plain port, a TLS failure on 3001 never, and only after the user
            // confirmed it (the caller also refuses it for a TV that was paired over TLS before): someone in the network who blocks
            // 3001 must not get an unencrypted pairing silently.
            if (!allowPlain) throw new TvException(TvError.PlainOnly);
            socket = await ConnectSocketAsync(address, secure: false, expectedPin: null, ct);
        }
        await using var session = new WebOsSession(socket, _o);
        var key = await session.RegisterAsync(null, onPrompt, ct);
        // Best effort, both: the model for the page, the MAC for Wake-on-LAN (the shell also reads the PC's ARP cache).
        var model = await session.TryAsync(async c => WebOsProtocol.ParseModel(await session.CallAsync(WebOsProtocol.SystemInfo, null, c)), ct);
        var mac = await session.TryAsync(async c => WebOsProtocol.ParseMac(await session.CallAsync(WebOsProtocol.ConnectionInfo, null, c)), ct);
        var app = await session.TryAsync(async c => WebOsProtocol.ParseForegroundApp(await session.CallAsync(WebOsProtocol.ForegroundApp, null, c)), ct);
        return new TvPairing(ProviderId, Canonical(address), socket.Secure, key, socket.Pin, mac, null, model, app);
    }

    public async Task<ITvSession> ConnectAsync(TvPairing pairing, CancellationToken ct)
    {
        if (!IPAddress.TryParse(pairing.Address, out var address) || !TvAddressPolicy.IsAllowed(address, _o.AllowLoopback)) throw new TvException(TvError.NotLocal);
        if (pairing.Secure && string.IsNullOrEmpty(pairing.CertPin)) throw new TvException(TvError.CertChanged);
        var socket = await ConnectSocketAsync(address, pairing.Secure, pairing.Secure ? pairing.CertPin : null, ct);
        var session = new WebOsSession(socket, _o);
        try
        {
            await session.RegisterAsync(pairing.ClientKey, onPrompt: null, ct);
            session.StartReceiving();
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private static string Canonical(IPAddress address) => (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();

    internal sealed record Connected(ClientWebSocket Socket, HttpMessageInvoker Invoker, bool Secure, string? Pin);

    private async Task<Connected> ConnectSocketAsync(IPAddress address, bool secure, string? expectedPin, CancellationToken ct)
    {
        string? seenPin = null;
        var allowLoopback = _o.AllowLoopback;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = _o.ConnectTimeout,
            MaxResponseHeadersLength = 16,
            ConnectCallback = async (context, token) =>
            {
                var host = context.DnsEndPoint.Host.Trim('[', ']');
                if (!IPAddress.TryParse(host, out var ip) || !TvAddressPolicy.IsAllowed(ip, allowLoopback)) throw new TvException(TvError.NotLocal);
                var s = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await s.ConnectAsync(new IPEndPoint(ip, context.DnsEndPoint.Port), token);
                    return new NetworkStream(s, ownsSocket: true);
                }
                catch
                {
                    s.Dispose();
                    throw;
                }
            },
            SslOptions = new SslClientAuthenticationOptions
            {
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                // The TV's certificate is self-signed: the chain cannot be checked, the key is pinned instead. At pairing any key is taken
                // and remembered (TOFU); afterwards only that key.
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    if (certificate is null) return false;
                    // A copy made here is ours to dispose; the certificate handed in belongs to the TLS stack.
                    var own = certificate as X509Certificate2 is null ? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData()) : null;
                    try
                    {
                        seenPin = WebOsProtocol.SpkiPin(own ?? (X509Certificate2)certificate);
                    }
                    finally
                    {
                        own?.Dispose();
                    }
                    return expectedPin is null || WebOsProtocol.SamePin(expectedPin, seenPin);
                }
            }
        };
        var invoker = new HttpMessageInvoker(handler, disposeHandler: true);
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.Zero;
        ws.Options.HttpVersion = HttpVersion.Version11;
        ws.Options.HttpVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        var host = address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        var uri = new Uri(secure ? $"wss://{host}:{_o.SecurePort}/" : $"ws://{host}:{_o.LegacyPort}/");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_o.ConnectTimeout);
        try
        {
            await ws.ConnectAsync(uri, invoker, timeout.Token);
            return new Connected(ws, invoker, secure, secure ? seenPin : null);
        }
        catch (Exception ex)
        {
            ws.Dispose();
            invoker.Dispose();
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            if (Find<TvException>(ex) is { } tv) throw tv;
            if (expectedPin is not null && seenPin is not null && !WebOsProtocol.SamePin(expectedPin, seenPin)) throw new TvException(TvError.CertChanged);
            if (Find<AuthenticationException>(ex) is not null) throw new TvException(TvError.BadAnswer, "TLS");
            if (timeout.IsCancellationRequested) throw new TvException(TvError.Timeout);
            throw new TvException(TvError.Unreachable);
        }
    }

    internal static T? Find<T>(Exception? ex) where T : Exception
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is T t) return t;
            if (e is AggregateException agg)
            {
                foreach (var inner in agg.InnerExceptions)
                {
                    if (Find<T>(inner) is { } found) return found;
                }
            }
        }
        return null;
    }

    /// <summary>One open SSAP connection: registration first, then requests matched to answers by id.</summary>
    internal sealed class WebOsSession(Connected connected, WebOsOptions o) : ITvSession
    {
        /// <summary>Messages without a waiting request (the TV's own events) before the connection is dropped.</summary>
        private const int MaxUnsolicited = 100;

        private readonly ClientWebSocket _ws = connected.Socket;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<SsapMessage>> _pending = new();
        private readonly SemaphoreSlim _send = new(1, 1);
        private readonly CancellationTokenSource _stop = new();
        private Task? _loop;
        private int _next;
        private int _unsolicited;
        private int _disposed;

        public bool Open => _ws.State == WebSocketState.Open && Volatile.Read(ref _disposed) == 0;

        /// <summary>
        /// Sends register and reads until the answer to it. With a key the TV answers "registered" at once; a prompt then means the key is
        /// gone (Unpaired). Without a key (pairing) the prompt is reported and the TV's "registered" awaited up to the prompt timeout.
        /// </summary>
        public async Task<string> RegisterAsync(string? clientKey, Action? onPrompt, CancellationToken ct)
        {
            await SendAsync(WebOsProtocol.Register(clientKey), ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(o.RequestTimeout);
            var prompted = false;
            try
            {
                for (var read = 0; read < 32; read++)
                {
                    var message = await ReceiveAsync(timeout.Token) ?? throw new TvException(TvError.Unreachable, "closed");
                    if (message.Id != WebOsProtocol.RegisterId) continue;
                    switch (message.Type)
                    {
                        case "registered":
                            // Pairing (no key yet): a real TV asks first. A "registered" without the prompt is not what a TV does
                            // and is refused: the key would come from something that never asked anybody.
                            if (clientKey is null && !prompted) throw new TvException(TvError.BadAnswer, "no prompt");
                            return WebOsProtocol.ClientKey(message.Payload) ?? clientKey ?? throw new TvException(TvError.BadAnswer, "no key");
                        case "response" when WebOsProtocol.IsPrompt(message.Payload):
                            if (onPrompt is null) throw new TvException(TvError.Unpaired);
                            if (!prompted)
                            {
                                prompted = true;
                                onPrompt();
                                timeout.CancelAfter(o.PromptTimeout);
                            }
                            continue;
                        case "response":
                            continue;
                        case "error":
                            throw new TvException(onPrompt is null ? TvError.Unpaired : TvError.Denied);
                        default:
                            throw new TvException(TvError.BadAnswer, "register");
                    }
                }
                throw new TvException(TvError.BadAnswer, "register");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TvException(prompted ? TvError.Denied : TvError.Timeout);
            }
        }

        public void StartReceiving() => _loop ??= Task.Run(ReceiveLoopAsync);

        private async Task ReceiveLoopAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var message = await ReceiveAsync(_stop.Token);
                    if (message is null) break;
                    if (message.Id is not null && _pending.TryRemove(message.Id, out var waiter)) waiter.TrySetResult(message);
                    else if (Interlocked.Increment(ref _unsolicited) > MaxUnsolicited) break;
                }
            }
            catch (Exception ex) when (ex is TvException or WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
            {
                // The connection is over; the waiters below get "unreachable".
            }
            foreach (var key in _pending.Keys)
            {
                if (_pending.TryRemove(key, out var waiter)) waiter.TrySetException(new TvException(TvError.Unreachable, "closed"));
            }
            try { _ws.Abort(); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>
        /// One request and its answer's payload; Refused when the TV says no. Before the receive loop runs (pairing) the answer is read
        /// right here.
        /// </summary>
        public async Task<JsonElement> CallAsync(string uri, object? payload, CancellationToken ct)
        {
            var id = "c" + Interlocked.Increment(ref _next).ToString(System.Globalization.CultureInfo.InvariantCulture);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(o.RequestTimeout);
            SsapMessage answer;
            try
            {
                if (_loop is null)
                {
                    await SendAsync(WebOsProtocol.Request(id, uri, payload), timeout.Token);
                    answer = null!;
                    for (var read = 0; ; read++)
                    {
                        if (read >= 32) throw new TvException(TvError.BadAnswer, "no answer");
                        var m = await ReceiveAsync(timeout.Token) ?? throw new TvException(TvError.Unreachable, "closed");
                        if (m.Id == id)
                        {
                            answer = m;
                            break;
                        }
                    }
                }
                else
                {
                    if (!Open) throw new TvException(TvError.Unreachable, "closed");
                    var waiter = new TaskCompletionSource<SsapMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _pending[id] = waiter;
                    try
                    {
                        await SendAsync(WebOsProtocol.Request(id, uri, payload), timeout.Token);
                        answer = await waiter.Task.WaitAsync(timeout.Token);
                    }
                    finally
                    {
                        _pending.TryRemove(id, out _);
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TvException(TvError.Timeout);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or ObjectDisposedException)
            {
                throw new TvException(TvError.Unreachable, "closed");
            }
            if (WebOsProtocol.Failed(answer)) throw new TvException(TvError.Refused);
            return answer.Payload;
        }

        /// <summary>A best-effort call: null on any TV failure (used for the extras at pairing).</summary>
        public async Task<T?> TryAsync<T>(Func<CancellationToken, Task<T?>> call, CancellationToken ct) where T : class
        {
            try { return await call(ct); }
            catch (TvException) { return null; }
        }

        public async Task TurnOffAsync(CancellationToken ct)
        {
            try
            {
                await CallAsync(WebOsProtocol.TurnOff, null, ct);
            }
            catch (TvException ex) when (ex.Code is TvError.Timeout or TvError.Unreachable)
            {
                // The TV goes down while answering (aiowebostv does not even wait): sent is good enough.
            }
        }

        public async Task<TvVolume> GetVolumeAsync(CancellationToken ct) => WebOsProtocol.ParseVolume(await CallAsync(WebOsProtocol.GetVolume, null, ct));

        public Task VolumeStepAsync(bool up, CancellationToken ct) => CallAsync(up ? WebOsProtocol.VolumeUp : WebOsProtocol.VolumeDown, null, ct);

        public Task SetVolumeAsync(int volume, CancellationToken ct) => CallAsync(WebOsProtocol.SetVolume, new { volume = Math.Clamp(volume, 0, 100) }, ct);

        public Task SetMuteAsync(bool mute, CancellationToken ct) => CallAsync(WebOsProtocol.SetMute, new { mute }, ct);

        public async Task<IReadOnlyList<TvInput>> GetInputsAsync(CancellationToken ct) => WebOsProtocol.ParseInputs(await CallAsync(WebOsProtocol.GetInputs, null, ct));

        public Task SwitchInputAsync(string inputId, CancellationToken ct)
        {
            if (!WebOsProtocol.IsValidInputId(inputId)) throw new ArgumentException("input id", nameof(inputId));
            return CallAsync(WebOsProtocol.SwitchInput, new { inputId }, ct);
        }

        public async Task<string?> GetForegroundAppAsync(CancellationToken ct) => WebOsProtocol.ParseForegroundApp(await CallAsync(WebOsProtocol.ForegroundApp, null, ct));

        public Task ToastAsync(string message, CancellationToken ct) =>
            CallAsync(WebOsProtocol.CreateToast, new { message = WebOsProtocol.CleanLabel(message) ?? "CouchHUD" }, ct);

        private async Task SendAsync(byte[] data, CancellationToken ct)
        {
            await _send.WaitAsync(ct);
            try
            {
                await _ws.SendAsync(data, WebSocketMessageType.Text, true, ct);
            }
            finally
            {
                _send.Release();
            }
        }

        /// <summary>One text message of at most <see cref="WebOsProtocol.MaxMessageBytes"/>, parsed; null when the TV closes.</summary>
        private async Task<SsapMessage?> ReceiveAsync(CancellationToken ct)
        {
            var chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                using var buffer = new MemoryStream();
                while (true)
                {
                    var r = await _ws.ReceiveAsync(chunk.AsMemory(), ct);
                    if (r.MessageType == WebSocketMessageType.Close) return null;
                    if (r.MessageType != WebSocketMessageType.Text) throw new TvException(TvError.BadAnswer, "binary");
                    if (buffer.Length + r.Count > WebOsProtocol.MaxMessageBytes) throw new TvException(TvError.BadAnswer, "message too large");
                    buffer.Write(chunk, 0, r.Count);
                    if (r.EndOfMessage) break;
                }
                return WebOsProtocol.Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(chunk);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
            _stop.Cancel();
            try
            {
                if (_ws.State == WebSocketState.Open)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
            {
                // Closed anyway.
            }
            _ws.Abort();
            if (_loop is not null)
            {
                try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (TimeoutException) { }
            }
            _ws.Dispose();
            connected.Invoker.Dispose();
            _stop.Dispose();
            _send.Dispose();
        }
    }
}
