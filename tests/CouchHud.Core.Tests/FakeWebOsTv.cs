using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace CouchHud.Core.Tests;

/// <summary>
/// A fake LG webOS TV on 127.0.0.1 for the TV tests: a TCP listener (with TLS and a fresh self-signed certificate, or plain), the
/// WebSocket upgrade by hand, then SSAP like a TV answers it. Nothing leaves the machine.
/// </summary>
internal sealed class FakeWebOsTv : IAsyncDisposable
{
    public const string IssuedKey = "fake-client-key-0123456789";

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accept;
    private readonly X509Certificate2? _cert;

    /// <summary>What the "user" does at the prompt: accept, deny or nothing.</summary>
    public string PromptAnswer { get; set; } = "accept";
    /// <summary>Answers "registered" with a key without showing the prompt first (not what a TV does).</summary>
    public bool SkipPrompt { get; set; }
    /// <summary>Keys the TV knows.</summary>
    public HashSet<string> KnownKeys { get; } = [IssuedKey];
    public int Volume { get; set; } = 12;
    public bool Muted { get; set; }
    public bool NewVolumeFormat { get; set; } = true;
    public string ForegroundApp { get; set; } = "com.webos.app.hdmi2";
    public string? SwitchedTo { get; private set; }
    public bool TurnedOff { get; private set; }
    /// <summary>Requests (uri) in order.</summary>
    public ConcurrentQueue<string> Requests { get; } = new();
    public int Connections;
    /// <summary>Uris the TV never answers (timeouts).</summary>
    public HashSet<string> Silent { get; } = [];
    /// <summary>Uris the TV refuses with "401 insufficient permissions".</summary>
    public HashSet<string> Refuse { get; } = [];
    /// <summary>Instead of the answer to this uri, a message larger than the client allows.</summary>
    public string? Oversize { get; set; }
    /// <summary>Raw input list JSON (devices array) the TV returns.</summary>
    public string InputsJson { get; set; } =
        """[{"id":"HDMI_1","label":"PlayStation\u0007 5","connected":true},{"id":"HDMI_2","label":"<b>PC</b>","connected":true},{"id":"bad id; rm","label":"x"},{"id":"HDMI_3","connected":false}]""";

    public int Port { get; }
    public X509Certificate2? Certificate => _cert;

    public FakeWebOsTv(bool tls, X509Certificate2? certificate = null)
    {
        if (tls) _cert = certificate ?? NewCertificate();
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = Task.Run(AcceptLoopAsync);
    }

    public static X509Certificate2 NewCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=fake-webos-tv", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        // SChannel needs a key it can use: through PKCS#12, not the ephemeral key of CreateSelfSigned.
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
    }

    /// <summary>A port nobody listens on (bound, then released).</summary>
    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }
            Interlocked.Increment(ref Connections);
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            Stream stream = client.GetStream();
            if (_cert is not null)
            {
                var ssl = new SslStream(stream, false);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _cert }, _stop.Token);
                stream = ssl;
            }
            var key = await ReadUpgradeAsync(stream);
            if (key is null) return;
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            var response = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(response), _stop.Token);
            using var ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.Zero });
            await RunAsync(ws);
        }
        catch (Exception)
        {
            // A test ended the connection; nothing to report.
        }
    }

    private static async Task<string?> ReadUpgradeAsync(Stream stream)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        while (buffer.Count < 8192)
        {
            if (await stream.ReadAsync(one) == 0) return null;
            buffer.Add(one[0]);
            if (buffer.Count >= 4 && buffer[^1] == '\n' && buffer[^2] == '\r' && buffer[^3] == '\n' && buffer[^4] == '\r') break;
        }
        foreach (var line in Encoding.ASCII.GetString(buffer.ToArray()).Split("\r\n"))
        {
            if (line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)) return line["Sec-WebSocket-Key:".Length..].Trim();
        }
        return null;
    }

    private async Task RunAsync(WebSocket ws)
    {
        var buffer = new byte[64 * 1024];
        while (ws.State == WebSocketState.Open && !_stop.IsCancellationRequested)
        {
            var r = await ws.ReceiveAsync(buffer, _stop.Token);
            if (r.MessageType == WebSocketMessageType.Close) return;
            using var doc = JsonDocument.Parse(buffer.AsMemory(0, r.Count));
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();
            var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (type == "register")
            {
                var payload = root.GetProperty("payload");
                var key = payload.TryGetProperty("client-key", out var k) ? k.GetString() : null;
                if (key is not null && KnownKeys.Contains(key))
                {
                    await SendAsync(ws, new { type = "registered", id, payload = new Dictionary<string, object> { ["client-key"] = key } });
                    continue;
                }
                if (!SkipPrompt) await SendAsync(ws, new { type = "response", id, payload = new { pairingType = "PROMPT", returnValue = true } });
                if (PromptAnswer == "accept") await SendAsync(ws, new { type = "registered", id, payload = new Dictionary<string, object> { ["client-key"] = IssuedKey } });
                else if (PromptAnswer == "deny") await SendAsync(ws, new { type = "error", id, error = "403 User denied access", payload = new { } });
                continue;
            }
            var uri = root.GetProperty("uri").GetString()!;
            Requests.Enqueue(uri);
            if (Silent.Contains(uri)) continue;
            if (Oversize == uri)
            {
                await ws.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"response\",\"id\":\"" + id + "\",\"payload\":{\"x\":\"" + new string('a', 300 * 1024) + "\"}}"), WebSocketMessageType.Text, true, _stop.Token);
                continue;
            }
            if (Refuse.Contains(uri))
            {
                await SendAsync(ws, new { type = "error", id, error = "401 insufficient permissions", payload = new { } });
                continue;
            }
            var reqPayload = root.TryGetProperty("payload", out var rp) ? rp : default;
            object answer = uri switch
            {
                "ssap://audio/getVolume" => NewVolumeFormat
                    ? new { returnValue = true, volumeStatus = new { volume = Volume, muteStatus = Muted } }
                    : new { returnValue = true, volume = Volume, muted = Muted },
                "ssap://audio/volumeUp" => Step(+1),
                "ssap://audio/volumeDown" => Step(-1),
                "ssap://audio/setVolume" => Set(() => Volume = reqPayload.GetProperty("volume").GetInt32()),
                "ssap://audio/setMute" => Set(() => Muted = reqPayload.GetProperty("mute").GetBoolean()),
                "ssap://tv/getExternalInputList" => JsonDocument.Parse("{\"returnValue\":true,\"devices\":" + InputsJson + "}").RootElement.Clone(),
                "ssap://tv/switchInput" => Set(() => SwitchedTo = reqPayload.GetProperty("inputId").GetString()),
                "ssap://com.webos.applicationManager/getForegroundAppInfo" => new { returnValue = true, appId = ForegroundApp },
                "ssap://system/getSystemInfo" => new { returnValue = true, modelName = "OLED55\u0000TEST" },
                "ssap://com.webos.service.connectionmanager/getinfo" => new { returnValue = true, wiredInfo = new { macAddress = "a8:23:fe:01:02:03" } },
                "ssap://system/turnOff" => Set(() => TurnedOff = true),
                _ => new { returnValue = false }
            };
            await SendAsync(ws, new { type = "response", id, payload = answer });
        }
    }

    private object Step(int d)
    {
        Volume = Math.Clamp(Volume + d, 0, 100);
        return new { returnValue = true };
    }

    private static object Set(Action a)
    {
        a();
        return new { returnValue = true };
    }

    private Task SendAsync(WebSocket ws, object message) =>
        ws.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, _stop.Token);

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try { await _accept; }
        catch (Exception) { }
        _cert?.Dispose();
    }
}
