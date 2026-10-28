using System.Net;
using System.Text;
using System.Text.Json;
using CouchHud.Core.Integrations.Tv;

namespace CouchHud.Core.Tests;

/// <summary>TV control: SSAP against a fake TV on loopback, the address and input rules, Wake-on-LAN, SSDP and the controller.</summary>
public class TvTests
{
    private static WebOsOptions Options(FakeWebOsTv? secure, FakeWebOsTv? legacy = null, int promptMs = 3000) => new()
    {
        SecurePort = secure?.Port ?? FakeWebOsTv.FreePort(),
        LegacyPort = legacy?.Port ?? FakeWebOsTv.FreePort(),
        AllowLoopback = true,
        ConnectTimeout = TimeSpan.FromSeconds(3),
        RequestTimeout = TimeSpan.FromMilliseconds(1500),
        PromptTimeout = TimeSpan.FromMilliseconds(promptMs)
    };

    private sealed class MemoryStore : ITvStore
    {
        public TvPairing? Stored;
        public int Saves;
        public TvPairing? Load() => Stored;

        public void Save(TvPairing? pairing)
        {
            Stored = pairing;
            Saves++;
        }

        public readonly List<string> Seen = [];
        public IReadOnlyCollection<string> SecureSeen() => Seen.ToList();
        public void RememberSecure(IEnumerable<string> ids) => Seen.AddRange(ids.Where(i => !Seen.Contains(i)));
    }

    // ---- pairing -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Pairing_over_tls_shows_prompt_and_pins_the_certificate()
    {
        await using var tv = new FakeWebOsTv(tls: true);
        var client = new WebOsClient(Options(tv));
        var prompted = 0;
        var pairing = await client.PairAsync(IPAddress.Loopback, () => prompted++, false, CancellationToken.None);

        Assert.Equal(1, prompted);
        Assert.Equal(FakeWebOsTv.IssuedKey, pairing.ClientKey);
        Assert.True(pairing.Secure);
        Assert.Equal(WebOsProtocol.SpkiPin(tv.Certificate!), pairing.CertPin);
        Assert.Equal("A8:23:FE:01:02:03", pairing.Mac);
        Assert.Equal("OLED55TEST", pairing.Model);
        Assert.Equal("com.webos.app.hdmi2", pairing.PcApp);
        Assert.Equal("127.0.0.1", pairing.Address);
    }

    [Fact]
    public async Task Declined_prompt_is_denied()
    {
        await using var tv = new FakeWebOsTv(tls: true) { PromptAnswer = "deny" };
        var ex = await Assert.ThrowsAsync<TvException>(() => new WebOsClient(Options(tv)).PairAsync(IPAddress.Loopback, () => { }, false, CancellationToken.None));
        Assert.Equal(TvError.Denied, ex.Code);
    }

    [Fact]
    public async Task Unanswered_prompt_times_out_as_denied()
    {
        await using var tv = new FakeWebOsTv(tls: true) { PromptAnswer = "none" };
        var ex = await Assert.ThrowsAsync<TvException>(() => new WebOsClient(Options(tv, promptMs: 400)).PairAsync(IPAddress.Loopback, () => { }, false, CancellationToken.None));
        Assert.Equal(TvError.Denied, ex.Code);
    }

    [Fact]
    public async Task Old_tv_without_tls_port_is_paired_over_plain_port_only_when_allowed_and_marked()
    {
        await using var legacy = new FakeWebOsTv(tls: false);
        var ex = await Assert.ThrowsAsync<TvException>(() => new WebOsClient(Options(null, legacy)).PairAsync(IPAddress.Loopback, () => { }, false, CancellationToken.None));
        Assert.Equal(TvError.PlainOnly, ex.Code);
        Assert.Equal(0, legacy.Connections);

        var pairing = await new WebOsClient(Options(null, legacy)).PairAsync(IPAddress.Loopback, () => { }, true, CancellationToken.None);
        Assert.False(pairing.Secure);
        Assert.Null(pairing.CertPin);
    }

    [Fact]
    public async Task Registered_without_a_prompt_is_not_a_pairing()
    {
        await using var tv = new FakeWebOsTv(tls: true) { SkipPrompt = true };
        var ex = await Assert.ThrowsAsync<TvException>(() => new WebOsClient(Options(tv)).PairAsync(IPAddress.Loopback, () => { }, false, CancellationToken.None));
        Assert.Equal(TvError.BadAnswer, ex.Code);
    }

    [Fact]
    public async Task Tls_failure_on_the_secure_port_never_falls_back_even_when_plain_is_allowed()
    {
        // Something answers on "3001" without TLS: that is a TLS failure, not "nobody listens", so no plain pairing.
        await using var notTls = new FakeWebOsTv(tls: false);
        await using var legacy = new FakeWebOsTv(tls: false);
        var options = Options(null, legacy) with { SecurePort = notTls.Port };
        var ex = await Assert.ThrowsAsync<TvException>(() => new WebOsClient(options).PairAsync(IPAddress.Loopback, () => { }, true, CancellationToken.None));
        Assert.NotEqual(TvError.PlainOnly, ex.Code);
        Assert.Equal(0, legacy.Connections);
    }

    [Fact]
    public async Task Tls_pairing_never_falls_back_to_plain()
    {
        await using var legacy = new FakeWebOsTv(tls: false);
        var options = Options(null, legacy);
        var pairing = new TvPairing(WebOsClient.ProviderId, "127.0.0.1", true, FakeWebOsTv.IssuedKey, Convert.ToBase64String(new byte[32]), null, null, null);
        var ex = await Assert.ThrowsAsync<TvException>(() => new WebOsClient(options).ConnectAsync(pairing, CancellationToken.None));
        Assert.Equal(TvError.Unreachable, ex.Code);
        Assert.Equal(0, legacy.Connections);
    }

    [Fact]
    public async Task Tls_pairing_without_pin_is_refused()
    {
        await using var tv = new FakeWebOsTv(tls: true);
        var pairing = new TvPairing(WebOsClient.ProviderId, "127.0.0.1", true, FakeWebOsTv.IssuedKey, null, null, null, null);
        var ex = await Assert.ThrowsAsync<TvException>(() => new WebOsClient(Options(tv)).ConnectAsync(pairing, CancellationToken.None));
        Assert.Equal(TvError.CertChanged, ex.Code);
    }

    [Fact]
    public async Task Changed_certificate_is_refused_with_its_own_code()
    {
        string pin;
        await using (var first = new FakeWebOsTv(tls: true))
        {
            pin = (await new WebOsClient(Options(first)).PairAsync(IPAddress.Loopback, () => { }, false, CancellationToken.None)).CertPin!;
        }
        await using var other = new FakeWebOsTv(tls: true);
        var pairing = new TvPairing(WebOsClient.ProviderId, "127.0.0.1", true, FakeWebOsTv.IssuedKey, pin, null, null, null);
        var ex = await Assert.ThrowsAsync<TvException>(() => new WebOsClient(Options(other)).ConnectAsync(pairing, CancellationToken.None));
        Assert.Equal(TvError.CertChanged, ex.Code);
        Assert.Empty(other.Requests);
    }

    [Fact]
    public async Task Forgotten_key_means_unpaired_not_a_new_prompt()
    {
        await using var tv = new FakeWebOsTv(tls: true);
        tv.KnownKeys.Clear();
        var pairing = new TvPairing(WebOsClient.ProviderId, "127.0.0.1", true, "stale-key-0123456", WebOsProtocol.SpkiPin(tv.Certificate!), null, null, null);
        var ex = await Assert.ThrowsAsync<TvException>(() => new WebOsClient(Options(tv)).ConnectAsync(pairing, CancellationToken.None));
        Assert.Equal(TvError.Unpaired, ex.Code);
    }

    [Fact]
    public async Task Public_and_loopback_addresses_are_refused_before_any_connection()
    {
        var real = new WebOsClient();
        var ex = await Assert.ThrowsAsync<TvException>(() => real.PairAsync(IPAddress.Parse("8.8.8.8"), () => { }, false, CancellationToken.None));
        Assert.Equal(TvError.NotLocal, ex.Code);
        ex = await Assert.ThrowsAsync<TvException>(() => real.PairAsync(IPAddress.Loopback, () => { }, false, CancellationToken.None));
        Assert.Equal(TvError.NotLocal, ex.Code);
        var stored = new TvPairing(WebOsClient.ProviderId, "1.1.1.1", true, "k-0123456789", "pin", null, null, null);
        ex = await Assert.ThrowsAsync<TvException>(() => real.ConnectAsync(stored, CancellationToken.None));
        Assert.Equal(TvError.NotLocal, ex.Code);
    }

    // ---- commands ------------------------------------------------------------------------------------------------------------

    private static async Task<(WebOsClient Client, TvPairing Pairing)> PairedAsync(FakeWebOsTv tv)
    {
        var client = new WebOsClient(Options(tv));
        var pairing = await client.PairAsync(IPAddress.Loopback, () => { }, false, CancellationToken.None);
        return (client, pairing);
    }

    [Fact]
    public async Task Commands_reach_the_tv_and_answers_are_parsed()
    {
        await using var tv = new FakeWebOsTv(tls: true);
        var (client, pairing) = await PairedAsync(tv);
        await using var s = await client.ConnectAsync(pairing, CancellationToken.None);

        await s.VolumeStepAsync(true, CancellationToken.None);
        Assert.Equal(new TvVolume(13, false), await s.GetVolumeAsync(CancellationToken.None));
        await s.SetMuteAsync(true, CancellationToken.None);
        await s.SetVolumeAsync(150, CancellationToken.None);
        Assert.Equal(new TvVolume(100, true), await s.GetVolumeAsync(CancellationToken.None));
        tv.NewVolumeFormat = false;
        Assert.Equal(new TvVolume(100, true), await s.GetVolumeAsync(CancellationToken.None));

        var inputs = await s.GetInputsAsync(CancellationToken.None);
        Assert.Equal(["HDMI_1", "HDMI_2", "HDMI_3"], inputs.Select(i => i.Id));
        Assert.Equal("PlayStation 5", inputs[0].Label);
        // Shown as text by the UI; the host only strips control characters.
        Assert.Equal("<b>PC</b>", inputs[1].Label);
        Assert.Equal("HDMI_3", inputs[2].Label);

        await s.SwitchInputAsync("HDMI_1", CancellationToken.None);
        Assert.Equal("HDMI_1", tv.SwitchedTo);
        await Assert.ThrowsAsync<ArgumentException>(() => s.SwitchInputAsync("x\"; y", CancellationToken.None));

        Assert.Equal("com.webos.app.hdmi2", await s.GetForegroundAppAsync(CancellationToken.None));
        await s.TurnOffAsync(CancellationToken.None);
        Assert.True(tv.TurnedOff);
    }

    [Fact]
    public async Task Refused_command_and_silent_tv_give_codes()
    {
        await using var tv = new FakeWebOsTv(tls: true);
        var (client, pairing) = await PairedAsync(tv);
        await using var s = await client.ConnectAsync(pairing, CancellationToken.None);
        tv.Refuse.Add(WebOsProtocol.SwitchInput);
        Assert.Equal(TvError.Refused, (await Assert.ThrowsAsync<TvException>(() => s.SwitchInputAsync("HDMI_1", CancellationToken.None))).Code);
        tv.Silent.Add(WebOsProtocol.GetVolume);
        Assert.Equal(TvError.Timeout, (await Assert.ThrowsAsync<TvException>(() => s.GetVolumeAsync(CancellationToken.None))).Code);
    }

    [Fact]
    public async Task Oversized_answer_drops_the_connection()
    {
        await using var tv = new FakeWebOsTv(tls: true);
        var (client, pairing) = await PairedAsync(tv);
        await using var s = await client.ConnectAsync(pairing, CancellationToken.None);
        tv.Oversize = WebOsProtocol.GetInputs;
        var ex = await Assert.ThrowsAsync<TvException>(() => s.GetInputsAsync(CancellationToken.None));
        Assert.Contains(ex.Code, new[] { TvError.Unreachable, TvError.Timeout });
        Assert.False(s.Open);
    }

    // ---- protocol parsing ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Register_message_asks_only_for_needed_permissions_and_carries_the_key()
    {
        using var doc = JsonDocument.Parse(WebOsProtocol.Register("abc-key-12345"));
        var payload = doc.RootElement.GetProperty("payload");
        Assert.Equal("register", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("abc-key-12345", payload.GetProperty("client-key").GetString());
        Assert.Equal("PROMPT", payload.GetProperty("pairingType").GetString());
        var permissions = payload.GetProperty("manifest").GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).ToList();
        Assert.Contains("CONTROL_POWER", permissions);
        Assert.DoesNotContain("LAUNCH", permissions);
        Assert.DoesNotContain("CONTROL_MOUSE_AND_KEYBOARD", permissions);
        using var noKey = JsonDocument.Parse(WebOsProtocol.Register(null));
        Assert.False(noKey.RootElement.GetProperty("payload").TryGetProperty("client-key", out _));
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("{\"id\":\"x\"}")]
    [InlineData("not json")]
    [InlineData("{\"type\":1}")]
    public void Malformed_messages_are_bad_answers(string text)
    {
        Assert.Equal(TvError.BadAnswer, Assert.Throws<TvException>(() => WebOsProtocol.Parse(Encoding.UTF8.GetBytes(text))).Code);
    }

    [Fact]
    public void Deep_or_huge_messages_are_bad_answers()
    {
        var deep = "{\"type\":\"response\",\"payload\":" + new string('[', 40) + new string(']', 40) + "}";
        Assert.Throws<TvException>(() => WebOsProtocol.Parse(Encoding.UTF8.GetBytes(deep)));
        Assert.Throws<TvException>(() => WebOsProtocol.Parse(new byte[WebOsProtocol.MaxMessageBytes + 1]));
    }

    [Fact]
    public void Client_key_must_look_like_one()
    {
        using var ok = JsonDocument.Parse("{\"client-key\":\"0123456789abcdef\"}");
        Assert.Equal("0123456789abcdef", WebOsProtocol.ClientKey(ok.RootElement));
        using var bad = JsonDocument.Parse("{\"client-key\":\"short\"}");
        Assert.Null(WebOsProtocol.ClientKey(bad.RootElement));
        using var ctrl = JsonDocument.Parse("{\"client-key\":\"0123456789\\nabcdef\"}");
        Assert.Null(WebOsProtocol.ClientKey(ctrl.RootElement));
    }

    [Fact]
    public void Input_of_foreground_app_maps_hdmi_apps_only()
    {
        TvInput[] inputs = [new("HDMI_1", "A", true), new("HDMI_2", "B", true)];
        Assert.Equal("HDMI_2", WebOsProtocol.InputOfApp("com.webos.app.hdmi2", inputs));
        Assert.Null(WebOsProtocol.InputOfApp("netflix", inputs));
        Assert.Null(WebOsProtocol.InputOfApp("com.webos.app.livetv", inputs));
        Assert.Null(WebOsProtocol.InputOfApp(null, inputs));
    }

    [Fact]
    public void Too_many_inputs_are_cut()
    {
        var devices = string.Join(',', Enumerable.Range(0, 100).Select(i => $"{{\"id\":\"IN_{i}\",\"label\":\"x\"}}"));
        using var doc = JsonDocument.Parse("{\"devices\":[" + devices + "]}");
        Assert.Equal(WebOsProtocol.MaxInputs, WebOsProtocol.ParseInputs(doc.RootElement).Count);
    }

    // ---- addresses -----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.3.4", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("169.254.10.10", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("fe80::1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("100.64.1.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("::ffff:192.168.1.2", true)]
    public void Only_local_network_addresses_are_tv_addresses(string address, bool allowed)
    {
        Assert.Equal(allowed, TvAddressPolicy.IsAllowed(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("192.168.1.20", "192.168.1.20")]
    [InlineData(" 10.0.0.7 ", "10.0.0.7")]
    [InlineData("fd00::5", "fd00::5")]
    [InlineData("lgtv.local", null)]
    [InlineData("192.168.1", null)]
    [InlineData("3232235796", null)]
    [InlineData("0xC0.0xA8.1.20", null)]
    [InlineData("192.168.001.020", null)]
    [InlineData("8.8.8.8", null)]
    [InlineData("", null)]
    public void Manual_address_is_a_local_ip_literal(string text, string? expected)
    {
        Assert.Equal(expected, TvAddressPolicy.ParseManual(text)?.ToString());
    }

    // ---- Wake-on-LAN ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("a8:23:fe:01:02:03", "A8:23:FE:01:02:03")]
    [InlineData("A8-23-FE-01-02-03", "A8:23:FE:01:02:03")]
    [InlineData("A8:23-FE:01:02:03", null)]
    [InlineData("00:00:00:00:00:00", null)]
    [InlineData("FF:FF:FF:FF:FF:FF", null)]
    [InlineData("01:00:5E:00:00:01", null)]
    [InlineData("A823FE010203", null)]
    [InlineData("zz:23:fe:01:02:03", null)]
    public void Mac_addresses_are_strict(string text, string? expected)
    {
        var mac = WakeOnLan.ParseMac(text);
        Assert.Equal(expected, mac is null ? null : WakeOnLan.Format(mac));
    }

    [Fact]
    public void Magic_packet_is_six_ff_and_sixteen_macs()
    {
        var mac = WakeOnLan.ParseMac("A8:23:FE:01:02:03")!;
        var packet = WakeOnLan.MagicPacket(mac);
        Assert.Equal(102, packet.Length);
        Assert.All(packet.Take(6), b => Assert.Equal(0xFF, b));
        for (var i = 0; i < 16; i++) Assert.Equal(mac, packet.Skip(6 + i * 6).Take(6));
    }

    [Fact]
    public void Broadcast_only_in_the_tvs_subnet()
    {
        var mask = IPAddress.Parse("255.255.255.0");
        Assert.Equal(IPAddress.Parse("192.168.1.255"), WakeOnLan.SubnetBroadcast(IPAddress.Parse("192.168.1.10"), mask, IPAddress.Parse("192.168.1.20")));
        Assert.Null(WakeOnLan.SubnetBroadcast(IPAddress.Parse("192.168.2.10"), mask, IPAddress.Parse("192.168.1.20")));
        Assert.Null(WakeOnLan.SubnetBroadcast(IPAddress.Parse("192.168.1.10"), IPAddress.Parse("255.255.255.255"), IPAddress.Parse("192.168.1.10")));
    }

    [Fact]
    public void Throttle_allows_one_per_interval()
    {
        long now = 0;
        var t = new Throttle(TimeSpan.FromSeconds(10), () => now);
        Assert.True(t.TryTake());
        now = 9_000;
        Assert.False(t.TryTake());
        now = 10_000;
        Assert.True(t.TryTake());
    }

    // ---- SSDP ----------------------------------------------------------------------------------------------------------------

    private static byte[] Answer(string st, string extra = "") => Encoding.UTF8.GetBytes(
        $"HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=1800\r\nLOCATION: http://8.8.8.8:1234/evil.xml\r\nST: {st}\r\n{extra}\r\n");

    [Fact]
    public void Ssdp_answer_gives_the_senders_address_and_never_the_location()
    {
        var device = SsdpDiscovery.ParseAnswer(Answer(SsdpDiscovery.SearchTarget, "DLNADeviceName.lge.com: %5BLG%5D%20webOS%20TV%0A\r\n"), IPAddress.Parse("192.168.1.20"));
        Assert.NotNull(device);
        Assert.Equal("192.168.1.20", device!.Address);
        Assert.Equal("[LG] webOS TV", device.Name);
        Assert.Equal(WebOsClient.ProviderId, device.Provider);
    }

    [Fact]
    public void Ssdp_answers_from_outside_wrong_target_or_too_large_are_dropped()
    {
        Assert.Null(SsdpDiscovery.ParseAnswer(Answer(SsdpDiscovery.SearchTarget), IPAddress.Parse("8.8.8.8")));
        Assert.Null(SsdpDiscovery.ParseAnswer(Answer("upnp:rootdevice"), IPAddress.Parse("192.168.1.20")));
        Assert.Null(SsdpDiscovery.ParseAnswer(Encoding.UTF8.GetBytes("NOTIFY * HTTP/1.1\r\nNT: " + SsdpDiscovery.SearchTarget + "\r\n\r\n"), IPAddress.Parse("192.168.1.20")));
        Assert.Null(SsdpDiscovery.ParseAnswer(new byte[SsdpDiscovery.MaxAnswerBytes + 1], IPAddress.Parse("192.168.1.20")));
        Assert.Contains("ST: urn:lge-com:service:webos-second-screen:1", Encoding.ASCII.GetString(SsdpDiscovery.SearchMessage()));
    }

    // ---- policy and config ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Locked_couch_keeps_volume_and_power_but_not_inputs_or_pairing()
    {
        foreach (var m in new[] { "tvStatus", "tvVolume", "tvVolumeStep", "tvSetVolume", "tvMute", "tvPower" }) Assert.True(TvPolicy.Allowed(m, locked: true), m);
        foreach (var m in new[] { "tvSwitchInput", "tvInputs", "tvPair", "tvForget", "tvDiscover", "tvSetMac", "tvCancelPair" })
        {
            Assert.False(TvPolicy.Allowed(m, locked: true), m);
            Assert.True(TvPolicy.Allowed(m, locked: false), m);
        }
        Assert.False(TvPolicy.Allowed("tvLaunchApp", locked: false));
    }

    [Fact]
    public void Config_keeps_the_key_only_protected()
    {
        var config = new TvConfig();
        var pairing = new TvPairing(WebOsClient.ProviderId, "192.168.1.20", true, "secret-key-123456", "pin", "A8:23:FE:01:02:03", null, "OLED", "com.webos.app.hdmi1");
        config.SetPairing(pairing, s => "dpapi:" + new string(s.Reverse().ToArray()));
        Assert.DoesNotContain("secret-key-123456", JsonSerializer.Serialize(config));
        Assert.Equal(pairing, config.ToPairing(s => new string(s["dpapi:".Length..].Reverse().ToArray())));
        // A key that cannot be decrypted (other Windows user) is no pairing.
        Assert.Null(config.ToPairing(_ => ""));
        config.SetPairing(null, s => s);
        Assert.Null(config.ToPairing(s => s));
        Assert.Equal("", config.ClientKey);
    }

    // ---- controller ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Controller_pairs_controls_and_never_shows_the_key()
    {
        await using var tv = new FakeWebOsTv(tls: true);
        var store = new MemoryStore();
        var statuses = new List<TvStatus>();
        await using var c = new TvController(new WebOsClient(Options(tv)), store, s => { lock (statuses) statuses.Add(s); });

        var status = await c.PairAsync(IPAddress.Loopback);
        Assert.True(status.Paired);
        Assert.Equal(FakeWebOsTv.IssuedKey, store.Stored!.ClientKey);
        lock (statuses) Assert.Contains(statuses, s => s.Pairing == "prompt");

        status = await c.VolumeStepAsync(up: false);
        Assert.Equal(11, status.Volume);
        status = await c.MuteAsync(null);
        Assert.True(status.Muted);
        status = await c.RefreshAsync(user: true);
        Assert.Equal("on", status.Power);
        Assert.Equal("HDMI_2", status.Input);

        await Assert.ThrowsAsync<ArgumentException>(() => c.SwitchInputAsync("HDMI_9"));
        status = await c.SwitchInputAsync("HDMI_1");
        Assert.Equal("HDMI_1", tv.SwitchedTo);

        lock (statuses) Assert.DoesNotContain(statuses, s => JsonSerializer.Serialize(s).Contains(FakeWebOsTv.IssuedKey));

        status = await c.ForgetAsync();
        Assert.False(status.Paired);
        Assert.Null(store.Stored);
    }

    [Fact]
    public async Task Controller_wakes_only_with_a_mac_and_rate_limited()
    {
        var store = new MemoryStore
        {
            Stored = new TvPairing(WebOsClient.ProviderId, "192.168.1.20", true, "key-0123456789", "pin", null, null, null)
        };
        var wakes = new List<(byte[] Mac, IPAddress To)>();
        long now = 1_000_000;
        await using var c = new TvController(new WebOsClient(), store, _ => { }, () => now, (mac, to, _) =>
        {
            wakes.Add((mac, to));
            return Task.FromResult(true);
        });

        Assert.Equal(TvError.NoWake, (await Assert.ThrowsAsync<TvException>(() => c.PowerAsync(true))).Code);
        c.SetMac("a8:23:fe:01:02:03");
        Assert.Equal("A8:23:FE:01:02:03", store.Stored!.Mac);
        Assert.Throws<ArgumentException>(() => c.SetMac("ff:ff:ff:ff:ff:ff"));

        var status = await c.PowerAsync(true);
        Assert.Equal("waking", status.Power);
        await c.PowerAsync(true);
        Assert.Single(wakes);
        Assert.Equal(IPAddress.Parse("192.168.1.20"), wakes[0].To);
        now += 11_000;
        await c.PowerAsync(true);
        Assert.Equal(2, wakes.Count);

        // Following CouchHUD: off by default.
        await c.FollowWakeAsync(new TvFollow(false, false));
        Assert.Equal(2, wakes.Count);
    }

    [Fact]
    public async Task Tv_off_with_pc_only_while_the_pc_input_is_in_front()
    {
        await using var tv = new FakeWebOsTv(tls: true);
        var store = new MemoryStore();
        await using var c = new TvController(new WebOsClient(Options(tv)), store, _ => { });
        await c.PairAsync(IPAddress.Loopback);
        Assert.Equal("com.webos.app.hdmi2", store.Stored!.PcApp);

        await c.FollowSleepAsync(new TvFollow(false, false));
        Assert.False(tv.TurnedOff);

        tv.ForegroundApp = "netflix";
        await c.FollowSleepAsync(new TvFollow(false, true));
        Assert.False(tv.TurnedOff);

        tv.ForegroundApp = "com.webos.app.hdmi2";
        await c.FollowSleepAsync(new TvFollow(false, true));
        Assert.True(tv.TurnedOff);
    }

    [Fact]
    public async Task Unreachable_tv_is_off_and_automatic_reads_back_off()
    {
        var store = new MemoryStore
        {
            Stored = new TvPairing(WebOsClient.ProviderId, "127.0.0.1", false, "key-0123456789", null, null, null, null)
        };
        var options = Options(null) with { LegacyPort = FakeWebOsTv.FreePort() };
        long now = 5_000_000;
        await using var c = new TvController(new WebOsClient(options), store, _ => { }, () => now);
        var status = await c.RefreshAsync(user: true);
        Assert.Equal("off", status.Power);
        Assert.Equal("unreachable", status.Error);

        // Within the backoff an automatic read does not connect again.
        await using var tv = new FakeWebOsTv(tls: false);
        var status2 = await c.RefreshAsync(user: false);
        Assert.Equal("off", status2.Power);
    }

    [Fact]
    public async Task Cert_change_keeps_its_code_in_the_status()
    {
        await using var tv = new FakeWebOsTv(tls: true);
        var store = new MemoryStore
        {
            Stored = new TvPairing(WebOsClient.ProviderId, "127.0.0.1", true, FakeWebOsTv.IssuedKey, Convert.ToBase64String(new byte[32]), null, null, null)
        };
        await using var c = new TvController(new WebOsClient(Options(tv)), store, _ => { });
        var status = await c.RefreshAsync(user: true);
        Assert.Equal("certChanged", status.Error);
        Assert.Equal(TvError.CertChanged, (await Assert.ThrowsAsync<TvException>(() => c.VolumeStepAsync(true))).Code);
    }

    // ---- review fixes: downgrade, cert replaced, busy, retry spacing, network/broadcast, host gates -----------------------------

    [Fact]
    public async Task Controller_offers_plain_only_on_request_and_never_for_a_tv_that_had_tls()
    {
        await using var legacy = new FakeWebOsTv(tls: false);
        var store = new MemoryStore();
        await using var c = new TvController(new WebOsClient(Options(null, legacy)), store, _ => { });

        Assert.Equal(TvError.PlainOnly, (await Assert.ThrowsAsync<TvException>(() => c.PairAsync(IPAddress.Loopback))).Code);
        Assert.Equal("plainOnly", c.Status().Error);
        Assert.Null(store.Stored);

        // This address was paired over TLS once (also after "Remove TV"): no plain pairing, nothing sent.
        store.Seen.Add("127.0.0.1");
        Assert.Equal(TvError.Downgrade, (await Assert.ThrowsAsync<TvException>(() => c.PairAsync(IPAddress.Loopback, allowPlain: true))).Code);
        Assert.Equal(0, legacy.Connections);

        // Same TV under another address, known by its MAC from the ARP cache: refused before connecting.
        store.Seen.Clear();
        store.Seen.Add("A8:23:FE:01:02:03");
        Assert.Equal(TvError.Downgrade, (await Assert.ThrowsAsync<TvException>(() => c.PairAsync(IPAddress.Loopback, _ => "a8-23-fe-01-02-03", allowPlain: true))).Code);
        Assert.Equal(0, legacy.Connections);

        // ... and when only the TV itself tells the MAC: the plain pairing is not kept.
        Assert.Equal(TvError.Downgrade, (await Assert.ThrowsAsync<TvException>(() => c.PairAsync(IPAddress.Loopback, allowPlain: true))).Code);
        Assert.Null(store.Stored);

        // A TV that never had TLS: fine after the confirmation.
        store.Seen.Clear();
        var status = await c.PairAsync(IPAddress.Loopback, allowPlain: true);
        Assert.True(status.Paired);
        Assert.False(status.Secure);
        Assert.Empty(store.Seen);
    }

    [Fact]
    public async Task Tls_pairing_is_remembered_and_blocks_a_later_plain_one()
    {
        await using var tv = new FakeWebOsTv(tls: true);
        var store = new MemoryStore();
        await using var c = new TvController(new WebOsClient(Options(tv)), store, _ => { });
        await c.PairAsync(IPAddress.Loopback);
        Assert.Contains("127.0.0.1", store.Seen);
        Assert.Contains("A8:23:FE:01:02:03", store.Seen);
        var forgotten = await c.ForgetAsync();
        Assert.Contains("127.0.0.1", store.Seen);
        // The page learns only that there was an encrypted pairing, not where.
        Assert.True(forgotten.SecureBefore);
        Assert.DoesNotContain("127.0.0.1", JsonSerializer.Serialize(forgotten));
        Assert.Equal(TvError.Downgrade, (await Assert.ThrowsAsync<TvException>(() => c.PairAsync(IPAddress.Loopback, allowPlain: true))).Code);
    }

    [Fact]
    public async Task Repairing_with_another_certificate_is_flagged()
    {
        var store = new MemoryStore();
        await using (var first = new FakeWebOsTv(tls: true))
        {
            await using var c1 = new TvController(new WebOsClient(Options(first)), store, _ => { });
            Assert.False((await c1.PairAsync(IPAddress.Loopback)).CertReplaced);
        }
        await using var other = new FakeWebOsTv(tls: true);
        await using var c = new TvController(new WebOsClient(Options(other)), store, _ => { });
        Assert.Equal("certChanged", (await c.RefreshAsync(user: true)).Error);
        var status = await c.PairAsync(IPAddress.Loopback);
        Assert.True(status.CertReplaced);
        Assert.Equal(WebOsProtocol.SpkiPin(other.Certificate!), store.Stored!.CertPin);
        // Same certificate again: no flag.
        Assert.False((await c.PairAsync(IPAddress.Loopback)).CertReplaced);
    }

    [Fact]
    public async Task A_second_pairing_while_one_runs_is_busy()
    {
        await using var tv = new FakeWebOsTv(tls: true) { PromptAnswer = "none" };
        var prompt = new TaskCompletionSource();
        await using var c = new TvController(new WebOsClient(Options(tv, promptMs: 5000)), new MemoryStore(), s => { if (s.Pairing == "prompt") prompt.TrySetResult(); });
        var first = c.PairAsync(IPAddress.Loopback);
        await prompt.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(TvError.Busy, (await Assert.ThrowsAsync<TvException>(() => c.PairAsync(IPAddress.Loopback))).Code);
        c.CancelPair();
        Assert.Equal(TvError.Denied, (await Assert.ThrowsAsync<TvException>(() => first)).Code);
    }

    [Fact]
    public async Task After_a_failed_connection_no_new_one_for_two_seconds_whoever_asks()
    {
        await using var tv = new FakeWebOsTv(tls: true);
        var store = new MemoryStore
        {
            Stored = new TvPairing(WebOsClient.ProviderId, "127.0.0.1", true, FakeWebOsTv.IssuedKey, Convert.ToBase64String(new byte[32]), null, null, null)
        };
        long now = 7_000_000;
        await using var c = new TvController(new WebOsClient(Options(tv)), store, _ => { }, () => now);
        Assert.Equal("certChanged", (await c.RefreshAsync(user: true)).Error);
        var tries = tv.Connections;
        // Locked reads (tvVolume), steps, a press: none of them connects again within the spacing.
        await c.RefreshAsync(user: true);
        Assert.Equal(TvError.CertChanged, (await Assert.ThrowsAsync<TvException>(() => c.VolumeStepAsync(true))).Code);
        Assert.Equal(tries, tv.Connections);
        now += 2_100;
        await c.RefreshAsync(user: true);
        Assert.Equal(tries + 1, tv.Connections);
    }

    [Theory]
    [InlineData("192.168.1.255", "192.168.1.10", "255.255.255.0", false)]
    [InlineData("192.168.1.0", "192.168.1.10", "255.255.255.0", false)]
    [InlineData("192.168.1.20", "192.168.1.10", "255.255.255.0", true)]
    [InlineData("192.168.0.255", "192.168.1.10", "255.255.254.0", true)]
    [InlineData("192.168.1.255", "192.168.1.10", "255.255.254.0", false)]
    [InlineData("10.0.0.255", "10.0.0.2", "255.255.0.0", true)]
    [InlineData("10.0.255.255", "10.0.0.2", "255.255.0.0", false)]
    [InlineData("192.168.5.255", "192.168.1.10", "255.255.255.0", false)]
    [InlineData("192.168.5.0", "192.168.1.10", "255.255.255.0", false)]
    [InlineData("192.168.5.7", "192.168.1.10", "255.255.255.0", true)]
    public void Network_and_broadcast_addresses_are_no_tv(string address, string local, string mask, bool allowed)
    {
        IReadOnlyList<(IPAddress, IPAddress)> Subnets() => [(IPAddress.Parse(local), IPAddress.Parse(mask))];
        Assert.Equal(allowed, TvAddressPolicy.IsAllowed(IPAddress.Parse(address), false, Subnets));
    }

    [Fact]
    public void Host_gate_refuses_locked_calls_itself()
    {
        Assert.Throws<CouchHud.Core.Guard.LockedException>(() => TvPolicy.Admit("tvSwitchInput", locked: true, "locked"));
        Assert.Throws<CouchHud.Core.Guard.LockedException>(() => TvPolicy.Admit("tvPair", locked: true, "locked"));
        Assert.Throws<CouchHud.Core.Guard.LockedException>(() => TvPolicy.Admit("tvInputs", locked: true, "locked"));
        TvPolicy.Admit("tvVolumeStep", locked: true, "locked");
        TvPolicy.Admit("tvSwitchInput", locked: false, "locked");
        Assert.Throws<InvalidOperationException>(() => TvPolicy.Admit("tvExec", locked: false, "locked"));
    }

    [Fact]
    public void Input_switch_checks_the_id_before_the_release_and_binds_the_release_to_it()
    {
        var released = new List<string>();
        Assert.Throws<ArgumentException>(() => TvPolicy.AdmitInputSwitch("HDMI 1; rm", released.Add));
        Assert.Throws<ArgumentException>(() => TvPolicy.AdmitInputSwitch(null, released.Add));
        Assert.Empty(released);
        Assert.Equal("HDMI_2", TvPolicy.AdmitInputSwitch("HDMI_2", released.Add));
        Assert.Equal(["HDMI_2"], released);
        // A refused release (no parents' code) stops the switch.
        Assert.Throws<InvalidOperationException>(() => TvPolicy.AdmitInputSwitch("HDMI_3", _ => throw new InvalidOperationException("code")));
    }
}
