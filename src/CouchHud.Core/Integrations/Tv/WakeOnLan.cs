using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CouchHud.Core.Integrations.Tv;

/// <summary>
/// Switching the TV on: a network TV in standby does not listen on its API, so it is woken with a Wake-on-LAN "magic packet" to its MAC
/// (LG: "Turn on via Wi-Fi" / "Mobile TV On" must be on in the TV's settings). Sent only to the paired TV's MAC, only as a broadcast on
/// the interface that is in the TV's subnet (never routed), at most once per <see cref="MinInterval"/>.
/// </summary>
public static class WakeOnLan
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(10);
    public const int Port = 9;

    /// <summary>"AA:BB:CC:DD:EE:FF" or "AA-BB-…" (any case); not all-zero, not broadcast, not multicast (lowest bit of the first byte).</summary>
    public static byte[]? ParseMac(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length != 17) return null;
        var sep = t[2];
        if (sep is not (':' or '-')) return null;
        var mac = new byte[6];
        for (var i = 0; i < 6; i++)
        {
            if (i > 0 && t[i * 3 - 1] != sep) return null;
            if (!byte.TryParse(t.AsSpan(i * 3, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out mac[i])) return null;
        }
        if (mac.All(b => b == 0) || mac.All(b => b == 0xFF) || (mac[0] & 1) == 1) return null;
        return mac;
    }

    public static string Format(byte[] mac) => string.Join(':', mac.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

    /// <summary>6 × 0xFF, then the MAC 16 times (102 bytes).</summary>
    public static byte[] MagicPacket(byte[] mac)
    {
        if (mac.Length != 6) throw new ArgumentException("MAC must have 6 bytes", nameof(mac));
        var packet = new byte[102];
        for (var i = 0; i < 6; i++) packet[i] = 0xFF;
        for (var i = 0; i < 16; i++) Buffer.BlockCopy(mac, 0, packet, 6 + i * 6, 6);
        return packet;
    }

    /// <summary>The directed broadcast address of <paramref name="local"/>/<paramref name="mask"/> if <paramref name="target"/> is in that subnet.</summary>
    public static IPAddress? SubnetBroadcast(IPAddress local, IPAddress mask, IPAddress target)
    {
        if (local.AddressFamily != AddressFamily.InterNetwork || mask.AddressFamily != AddressFamily.InterNetwork || target.AddressFamily != AddressFamily.InterNetwork) return null;
        var l = local.GetAddressBytes();
        var m = mask.GetAddressBytes();
        var t = target.GetAddressBytes();
        if (m.All(b => b == 0xFF)) return null;
        var broadcast = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            if ((l[i] & m[i]) != (t[i] & m[i])) return null;
            broadcast[i] = (byte)(l[i] | ~m[i]);
        }
        return new IPAddress(broadcast);
    }

    /// <summary>
    /// Sends the packet three times to the subnet broadcast and to 255.255.255.255, from each local IPv4 address whose subnet holds the
    /// TV (so it leaves on that interface only). Returns false when no interface is in the TV's subnet (nothing sent).
    /// </summary>
    public static async Task<bool> SendAsync(byte[] mac, IPAddress tv, CancellationToken ct)
    {
        var packet = MagicPacket(mac);
        var sent = false;
        foreach (var (local, broadcast) in Routes(tv))
        {
            using var udp = new UdpClient(new IPEndPoint(local, 0)) { EnableBroadcast = true };
            udp.Client.Ttl = 1;
            for (var i = 0; i < 3; i++)
            {
                await udp.SendAsync(packet, new IPEndPoint(broadcast, Port), ct);
                await udp.SendAsync(packet, new IPEndPoint(IPAddress.Broadcast, Port), ct);
            }
            sent = true;
        }
        return sent;
    }

    private static IEnumerable<(IPAddress Local, IPAddress Broadcast)> Routes(IPAddress tv)
    {
        if (tv.IsIPv4MappedToIPv6) tv = tv.MapToIPv4();
        if (tv.AddressFamily != AddressFamily.InterNetwork) yield break;
        NetworkInterface[] interfaces;
        try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { yield break; }
        foreach (var nic in interfaces)
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var u in nic.GetIPProperties().UnicastAddresses)
            {
                if (u.Address.AddressFamily != AddressFamily.InterNetwork || u.IPv4Mask is null) continue;
                if (SubnetBroadcast(u.Address, u.IPv4Mask, tv) is { } b) yield return (u.Address, b);
            }
        }
    }
}

/// <summary>At most one event per interval (a monotonic clock is passed in).</summary>
public sealed class Throttle(TimeSpan interval, Func<long> nowMs)
{
    private long _last = long.MinValue;
    private readonly object _lock = new();

    public bool TryTake()
    {
        lock (_lock)
        {
            var now = nowMs();
            if (_last != long.MinValue && now - _last < interval.TotalMilliseconds) return false;
            _last = now;
            return true;
        }
    }
}
