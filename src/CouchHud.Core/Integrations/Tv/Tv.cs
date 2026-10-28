using System.Net;
using System.Net.Sockets;

namespace CouchHud.Core.Integrations.Tv;

// TV control from the couch (0.5 plan, item 3): switch the TV on and off, choose the input, volume and mute.
// PC graphics cards generally do not speak HDMI-CEC (the CEC line is not wired to the GPU's HDMI port), so CEC needs an adapter such
// as a Pulse-Eight USB-CEC with libCEC. The first provider therefore uses the TV's network API: LG webOS (SSAP over a WebSocket, see
// WebOsClient). Further providers plug in through ITvProvider:
//   - HDMI-CEC (libCEC): documented stub only. It would need a native library (libcec.dll, GPL-2.0) and an adapter; not shipped now.
//   - Samsung Tizen, Sony Bravia, Android/Google TV: own network APIs, each its own provider later.

/// <summary>Why a TV call failed, as a code the UI translates (docs/i18n.md: no finished sentences from the host).</summary>
public enum TvError
{
    /// <summary>No answer on the TV's port (TV off, other address, network).</summary>
    Unreachable,
    /// <summary>The TV did not answer in time.</summary>
    Timeout,
    /// <summary>The TV's certificate is not the one seen at pairing: pair again (or someone else answers on that address).</summary>
    CertChanged,
    /// <summary>Pairing was declined on the TV, or the prompt was not answered.</summary>
    Denied,
    /// <summary>The TV does not know the stored key any more (reset, other TV): pair again.</summary>
    Unpaired,
    /// <summary>The TV refused the command (missing permission, wrong state).</summary>
    Refused,
    /// <summary>An answer that is not what the protocol says (too large, not JSON, wrong shape).</summary>
    BadAnswer,
    /// <summary>The address is not in the local network (public, loopback, multicast …).</summary>
    NotLocal,
    /// <summary>No TV is paired.</summary>
    NotPaired,
    /// <summary>Wake-on-LAN is not possible (no MAC known) or was sent just now.</summary>
    NoWake,
    /// <summary>The TV answers only on the old unencrypted port; pairing over it needs an explicit confirmation (expert mode).</summary>
    PlainOnly,
    /// <summary>This address or MAC was paired over TLS before: an unencrypted pairing is refused (a downgrade by someone else in the network).</summary>
    Downgrade,
    /// <summary>A pairing is running already.</summary>
    Busy
}

public sealed class TvException(TvError code, string? detail = null) : Exception(detail ?? code.ToString())
{
    public TvError Code { get; } = code;
}

/// <summary>A TV found by discovery or typed in: the address (an IP literal of the local network) and what it said about itself.</summary>
public sealed record TvDevice(string Address, string? Name, string Provider);

/// <summary>One external input of the TV ("HDMI_1", "HDMI 1"). The label comes from the TV: shown escaped, never interpreted.</summary>
public sealed record TvInput(string Id, string Label, bool Connected);

public sealed record TvVolume(int Volume, bool Muted);

/// <summary>
/// What pairing produced. <see cref="ClientKey"/> is a secret (the caller stores it with DPAPI, never logs it, never hands it to the UI);
/// <see cref="CertPin"/> is the SHA-256 of the TV certificate's public key (SPKI, base64), trusted on first use and required afterwards.
/// <see cref="PcApp"/> is the app that was in front on the TV while pairing: pairing happens from CouchHUD on the TV, so that is the PC's
/// input (e.g. "com.webos.app.hdmi2"); "TV off with the PC" only switches off while that input is in front.
/// </summary>
public sealed record TvPairing(string Provider, string Address, bool Secure, string ClientKey, string? CertPin, string? Mac, string? Name, string? Model, string? PcApp = null);

/// <summary>A connection to a paired TV. One command at a time is fine; the implementation serializes.</summary>
public interface ITvSession : IAsyncDisposable
{
    bool Open { get; }
    Task TurnOffAsync(CancellationToken ct);
    Task<TvVolume> GetVolumeAsync(CancellationToken ct);
    Task VolumeStepAsync(bool up, CancellationToken ct);
    Task SetVolumeAsync(int volume, CancellationToken ct);
    Task SetMuteAsync(bool mute, CancellationToken ct);
    Task<IReadOnlyList<TvInput>> GetInputsAsync(CancellationToken ct);
    Task SwitchInputAsync(string inputId, CancellationToken ct);
    /// <summary>The app in front on the TV (e.g. "com.webos.app.hdmi2"), or null.</summary>
    Task<string?> GetForegroundAppAsync(CancellationToken ct);
    Task ToastAsync(string message, CancellationToken ct);
}

/// <summary>One kind of TV (one protocol).</summary>
public interface ITvProvider
{
    /// <summary>Short id stored in the config ("lg-webos").</summary>
    string Id { get; }

    /// <summary>Shown name ("LG webOS").</summary>
    string Name { get; }

    /// <summary>
    /// Pairs with the TV at <paramref name="address"/>: the TV shows a prompt, <paramref name="onPrompt"/> is called when it does, and the
    /// result carries the key and the pinned certificate. Throws <see cref="TvException"/> (Denied, Timeout, Unreachable, NotLocal …).
    /// </summary>
    /// <param name="allowPlain">Fall back to an unencrypted connection when the TV has no TLS port (only after the user confirmed it);
    /// otherwise that case is <see cref="TvError.PlainOnly"/>.</param>
    Task<TvPairing> PairAsync(IPAddress address, Action onPrompt, bool allowPlain, CancellationToken ct);

    /// <summary>Opens a session with a stored pairing; refuses a changed certificate (<see cref="TvError.CertChanged"/>).</summary>
    Task<ITvSession> ConnectAsync(TvPairing pairing, CancellationToken ct);
}

/// <summary>Which addresses a TV may have: only the local network, never the internet, never this PC.</summary>
public static class TvAddressPolicy
{
    /// <summary>
    /// RFC 1918 (10/8, 172.16/12, 192.168/16), IPv4 link-local (169.254/16) and IPv6 unique-local (fd00::/8). Loopback only when
    /// <paramref name="allowLoopback"/> (the tests' fake TV). Not: public addresses, 100.64/10 (CGNAT, Tailscale), multicast, broadcast,
    /// 0.0.0.0, and IPv6 link-local (it needs a scope id that an address typed on a TV cannot carry reliably).
    /// </summary>
    public static bool IsAllowed(IPAddress? address, bool allowLoopback = false) => IsAllowed(address, allowLoopback, LocalSubnets);

    /// <summary>
    /// As above, with the PC's own IPv4 subnets (address, mask) for the network and broadcast addresses: an address that is the network
    /// or broadcast address of a subnet of this PC is never a TV. Outside the PC's subnets, x.x.x.0 and x.x.x.255 are refused (the usual
    /// /24 home network); inside a larger subnet of the PC those can be real hosts and stay allowed.
    /// </summary>
    public static bool IsAllowed(IPAddress? address, bool allowLoopback, Func<IReadOnlyList<(IPAddress Address, IPAddress Mask)>> subnets)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return allowLoopback;
        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork && IsNetworkOrBroadcast(b, subnets())) return false;
        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => b[0] == 10
                                          || (b[0] == 172 && b[1] is >= 16 and <= 31)
                                          || (b[0] == 192 && b[1] == 168 && !(b[2] == 0 && b[3] == 0))
                                          || (b[0] == 169 && b[1] == 254 && b[2] is > 0 and < 255),
            AddressFamily.InterNetworkV6 => b[0] == 0xFD,
            _ => false
        };
    }

    private static bool IsNetworkOrBroadcast(byte[] b, IReadOnlyList<(IPAddress Address, IPAddress Mask)> subnets)
    {
        var ip = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        foreach (var (local, mask) in subnets)
        {
            if (local.AddressFamily != AddressFamily.InterNetwork || mask.AddressFamily != AddressFamily.InterNetwork) continue;
            var l = local.GetAddressBytes();
            var m = mask.GetAddressBytes();
            var lu = (uint)(l[0] << 24 | l[1] << 16 | l[2] << 8 | l[3]);
            var mu = (uint)(m[0] << 24 | m[1] << 16 | m[2] << 8 | m[3]);
            // /31 and /32 have no network or broadcast address; a zero mask is no subnet.
            if (mu == 0 || mu >= 0xFFFFFFFE || (ip & mu) != (lu & mu)) continue;
            return (ip & ~mu) == 0 || (ip | mu) == uint.MaxValue;
        }
        return b[3] is 0 or 255;
    }

    /// <summary>The PC's IPv4 subnets (interfaces that are up); empty when they cannot be read.</summary>
    public static IReadOnlyList<(IPAddress Address, IPAddress Mask)> LocalSubnets()
    {
        try
        {
            return System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork && u.IPv4Mask is not null)
                .Select(u => (u.Address, u.IPv4Mask))
                .ToList();
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            return [];
        }
    }

    /// <summary>
    /// An address typed by the user: an IP literal of the local network (as text, normalized), or null. Host names are not taken: the
    /// address is pinned to the certificate and the key, and a name could point elsewhere tomorrow (or resolve to a public address).
    /// </summary>
    public static IPAddress? ParseManual(string? text, bool allowLoopback = false)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length is 0 or > 45) return null;
        // IPAddress.TryParse takes "10" or "0x0a.1" as well; only the plain dotted form or an IPv6 literal.
        if (trimmed.Contains('.') && trimmed.Count(c => c == '.') != 3) return null;
        if (!trimmed.All(c => char.IsAsciiHexDigit(c) || c is '.' or ':')) return null;
        if (!IPAddress.TryParse(trimmed, out var ip)) return null;
        if (ip.AddressFamily == AddressFamily.InterNetwork && ip.ToString() != trimmed) return null;
        return IsAllowed(ip, allowLoopback) ? ip : null;
    }
}

/// <summary>Bridge methods of the TV page and the Guide menu rows, and what a locked couch may use of them.</summary>
public static class TvPolicy
{
    /// <summary>
    /// Locked couch: reading the state, volume, mute and switching the TV on or off. They are harmless (the remote on the table does the
    /// same) and a guest should not need the code to turn the sound down or the TV off. Nothing here can reach another device or app.
    /// </summary>
    public static readonly IReadOnlySet<string> OpenWhileLocked = new HashSet<string>
    {
        "tvStatus", "tvVolume", "tvVolumeStep", "tvSetVolume", "tvMute", "tvPower"
    };

    /// <summary>
    /// Only unlocked: discovery, pairing and forgetting the TV, the manual address and MAC, and switching the input (the other input may
    /// be a console, a set-top box or a stick, i.e. a way around the couch lock and the parental controls). The input list itself is
    /// read only to offer the switch, so it is unlocked-only too.
    /// </summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(OpenWhileLocked)
    {
        "tvDiscover", "tvPair", "tvCancelPair", "tvForget", "tvSetMac", "tvInputs", "tvSwitchInput"
    };

    /// <summary>The action name of the parents' one-time release for switching the input (see Hud.RequireParentGrant).</summary>
    public const string ParentAction = "tvInput";

    public static bool Allowed(string method, bool locked) => All.Contains(method) && (!locked || OpenWhileLocked.Contains(method));

    /// <summary>The host's own lock check at the start of every TV call (not only the bridge's): <see cref="Guard.LockedException"/> otherwise.</summary>
    public static void Admit(string method, bool locked, string lockedMessage)
    {
        if (!All.Contains(method)) throw new InvalidOperationException($"unknown method {method}");
        if (!Allowed(method, locked)) throw new Guard.LockedException(lockedMessage);
    }

    /// <summary>
    /// Switching the input: the id is checked first (a parents' release is never spent on an id the TV cannot have), then
    /// <paramref name="requireRelease"/> (unlock and, with parental controls, the release for exactly this input) runs with it.
    /// </summary>
    public static string AdmitInputSwitch(string? inputId, Action<string> requireRelease)
    {
        if (inputId is null || !WebOsProtocol.IsValidInputId(inputId)) throw new ArgumentException("input");
        requireRelease(inputId);
        return inputId;
    }
}
