using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace CouchHud.Core.Integrations.Tv;

/// <summary>
/// Finding LG webOS TVs in the local network with SSDP: one M-SEARCH for <c>urn:lge-com:service:webos-second-screen:1</c> to the
/// multicast group 239.255.255.250:1900 from each private IPv4 address of this PC (TTL 1, so it never leaves the subnet), answers
/// collected for a few seconds. Only the sender's address is used: the LOCATION URL in the answer is never fetched, an answer from an
/// address outside the local network is dropped, and every answer is size-limited. Runs only when the user asks (Settings › TV › Search).
/// </summary>
public static class SsdpDiscovery
{
    public const string SearchTarget = "urn:lge-com:service:webos-second-screen:1";
    public static readonly IPEndPoint Group = new(IPAddress.Parse("239.255.255.250"), 1900);
    public const int MaxAnswerBytes = 2048;
    public const int MaxDevices = 16;

    public static byte[] SearchMessage() => Encoding.ASCII.GetBytes(
        "M-SEARCH * HTTP/1.1\r\n" +
        "HOST: 239.255.255.250:1900\r\n" +
        "MAN: \"ssdp:discover\"\r\n" +
        "MX: 2\r\n" +
        $"ST: {SearchTarget}\r\n" +
        "\r\n");

    /// <summary>
    /// One answer: "HTTP/1.1 200 OK" with <c>ST</c> (or <c>NT</c>) equal to the webOS target, from a local-network address. The name comes
    /// from LG's <c>DLNADeviceName.lge.com</c> header (URL-encoded) if present, cleaned like any text from the TV. Null otherwise.
    /// </summary>
    public static TvDevice? ParseAnswer(ReadOnlySpan<byte> data, IPAddress from, bool allowLoopback = false)
    {
        if (data.Length is 0 or > MaxAnswerBytes) return null;
        if (!TvAddressPolicy.IsAllowed(from, allowLoopback)) return null;
        string text;
        try { text = new UTF8Encoding(false, true).GetString(data); }
        catch (ArgumentException) { return null; }
        var lines = text.Split("\r\n");
        if (lines.Length < 2 || !lines[0].StartsWith("HTTP/1.1 200", StringComparison.Ordinal)) return null;
        string? st = null, name = null;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (key.Equals("ST", StringComparison.OrdinalIgnoreCase) || key.Equals("NT", StringComparison.OrdinalIgnoreCase)) st ??= value;
            else if (key.Equals("DLNADeviceName.lge.com", StringComparison.OrdinalIgnoreCase))
            {
                try { name = WebOsProtocol.CleanLabel(Uri.UnescapeDataString(value)); }
                catch (UriFormatException) { name = null; }
            }
        }
        if (!string.Equals(st, SearchTarget, StringComparison.OrdinalIgnoreCase)) return null;
        if (from.IsIPv4MappedToIPv6) from = from.MapToIPv4();
        return new TvDevice(from.ToString(), name, WebOsClient.ProviderId);
    }

    /// <summary>Searches for <paramref name="duration"/> (capped at 5 s); duplicates by address are merged, at most <see cref="MaxDevices"/>.</summary>
    public static async Task<IReadOnlyList<TvDevice>> SearchAsync(TimeSpan duration, CancellationToken ct)
    {
        if (duration > TimeSpan.FromSeconds(5)) duration = TimeSpan.FromSeconds(5);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(duration);
        var found = new Dictionary<string, TvDevice>();
        var sockets = new List<UdpClient>();
        try
        {
            foreach (var local in LocalAddresses())
            {
                try
                {
                    var udp = new UdpClient(new IPEndPoint(local, 0));
                    udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                    sockets.Add(udp);
                }
                catch (SocketException) { }
            }
            var message = SearchMessage();
            foreach (var udp in sockets)
            {
                for (var i = 0; i < 2; i++)
                {
                    try { await udp.SendAsync(message, Group, cts.Token); }
                    catch (SocketException) { }
                }
            }
            var lockObj = new object();
            await Task.WhenAll(sockets.Select(async udp =>
            {
                while (!cts.IsCancellationRequested)
                {
                    UdpReceiveResult r;
                    try { r = await udp.ReceiveAsync(cts.Token); }
                    catch (OperationCanceledException) { break; }
                    catch (SocketException) { break; }
                    if (ParseAnswer(r.Buffer, r.RemoteEndPoint.Address) is not { } device) continue;
                    lock (lockObj)
                    {
                        if (found.Count < MaxDevices || found.ContainsKey(device.Address)) found[device.Address] = device;
                    }
                }
            }));
        }
        finally
        {
            foreach (var s in sockets) s.Dispose();
        }
        return found.Values.OrderBy(d => d.Address, StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<IPAddress> LocalAddresses()
    {
        NetworkInterface[] interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { yield break; }
        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback || !nic.SupportsMulticast) continue;
            foreach (var u in nic.GetIPProperties().UnicastAddresses)
            {
                if (u.Address.AddressFamily == AddressFamily.InterNetwork && TvAddressPolicy.IsAllowed(u.Address)) yield return u.Address;
            }
        }
    }
}
