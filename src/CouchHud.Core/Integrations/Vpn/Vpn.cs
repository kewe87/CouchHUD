using CouchHud.Core.Platform;

namespace CouchHud.Core.Integrations.Vpn;

// VPN services from the couch (0.5 plan, item 1). CouchHUD is no VPN client and holds no credentials: it drives the
// VPN app that is installed on the PC through that app's own command line and reads the state of the tunnel off the network adapters
// (locally, no request to anyone). One provider is implemented (NordVPN); others follow through IVpnProvider:
//   - ProtonVPN, Surfshark: only if their Windows app has a documented command line or local interface (not researched yet).
//   - WireGuard profiles: wireguard.exe /installtunnelservice and /uninstalltunnelservice need administrator rights (they install a
//     Windows service per tunnel), so a provider would need the CouchHUD service as a broker. Deliberately not done: the LocalSystem
//     service is not extended for this. Left as a documented stub (no class) until there is a way without admin rights.

/// <summary>What the couch sees of the VPN.</summary>
public enum VpnState
{
    /// <summary>No app of the provider where CouchHUD may run it (not installed, or not below Program Files).</summary>
    NotInstalled,
    Disconnected,
    Connecting,
    Connected,
    Disconnecting
}

public enum VpnTargetKind
{
    /// <summary>The app's own choice ("Quick connect").</summary>
    Quick,
    /// <summary>The best server in one country.</summary>
    Country,
    /// <summary>One named server ("Germany #812").</summary>
    Server
}

/// <summary>Where to connect to. <see cref="Country"/> is the provider's country id (ISO 3166 alpha-2, lower case).</summary>
public sealed record VpnTarget(VpnTargetKind Kind, string? Country = null, string? Server = null)
{
    public static VpnTarget Quick { get; } = new(VpnTargetKind.Quick);
}

/// <summary>A network adapter as Windows reports it (name, description, operationally up).</summary>
/// <param name="CarriesDefaultRoute">
/// Whether Windows' routing table sends traffic to the internet through this adapter (the best route to a public address, looked up
/// locally, nothing is sent). False for a tunnel that only carries its own network (NordVPN's Meshnet uses the NordLynx adapter too);
/// null when it could not be looked up.
/// </param>
public sealed record VpnAdapter(string Name, string Description, bool Up, bool? CarriesDefaultRoute = null);

/// <summary>A country the provider's app can connect to: CouchHUD's id and the provider's own name for it (what the command line takes).</summary>
public sealed record VpnCountry(string Id, string Name);

/// <summary>
/// One VPN app. Everything here is a pure decision (tested in Core); finding and running the program, reading the adapters and the
/// processes is the shell's VpnManager, which verifies every exe candidate (canonical path below Program Files, no reparse points).
/// </summary>
public interface IVpnProvider
{
    /// <summary>Short id ("nordvpn").</summary>
    string Id { get; }

    /// <summary>The brand as it is shown ("NordVPN").</summary>
    string Name { get; }

    /// <summary>File name of the app's program, the only name a candidate may have.</summary>
    string ExeName { get; }

    /// <summary>Process name of the running app (without .exe), to tell whether it runs in this session.</summary>
    string ProcessName { get; }

    /// <summary>The fixed list of countries the UI offers and the only ones a connect may name.</summary>
    IReadOnlyList<VpnCountry> Countries { get; }

    /// <summary>
    /// Where the app may be: only administrator-written sources (HKLM uninstall and App Paths entries, Program Files). Never PATH, never
    /// HKCU, never a user folder. The caller checks each candidate before it runs anything.
    /// </summary>
    IEnumerable<string> ExeCandidates(IRegistry registry, IEnumerable<string> programFiles);

    /// <summary>The arguments for a connect, each one a separate ArgumentList entry.</summary>
    /// <exception cref="ArgumentException">A country or server that is not on the list or does not match the pattern.</exception>
    IReadOnlyList<string> ConnectArguments(VpnTarget target);

    IReadOnlyList<string> DisconnectArguments();

    /// <summary>The target as it is used and remembered (a server name written the provider's way); refused like <see cref="ConnectArguments"/>.</summary>
    /// <exception cref="ArgumentException">Not a valid target.</exception>
    VpnTarget Normalize(VpnTarget target);

    /// <summary>The country id a target is in (a country, or the country of a server), null for quick connect.</summary>
    string? CountryOf(VpnTarget target);

    /// <summary>The kind of tunnel that is up ("nordlynx", "openvpn"), or null when none of the provider's adapters is up.</summary>
    string? TunnelKind(IEnumerable<VpnAdapter> adapters);

    /// <summary>
    /// What happens when the command line has not ended in time. A GUI app whose command line hands the request to its running instance
    /// ends at once; with no instance running, the started process may become the app itself. Killing that would end the user's VPN app,
    /// so it is only killed when an instance was already running (then it really hangs).
    /// </summary>
    bool KillCliOnTimeout(bool appWasRunning);
}

/// <summary>
/// Connecting and disconnecting as the couch sees it. The app answers nothing useful on its command line, so the state follows the
/// tunnel adapter: a connect counts as done when the tunnel is up (after a change of country: once it went down, or after a short
/// settle time), a disconnect when it is down. Without that in time the request ends with a hint instead of hanging.
/// </summary>
public sealed class VpnStateMachine(Func<DateTimeOffset> now)
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan DisconnectTimeout = TimeSpan.FromSeconds(20);
    /// <summary>A change of country while connected: the tunnel may stay up the whole time; after this it counts as done anyway.</summary>
    public static readonly TimeSpan ReconnectSettle = TimeSpan.FromSeconds(8);

    private enum Pending { None, Connect, Disconnect }

    private Pending _pending;
    private DateTimeOffset _since;
    private bool _wasUp;
    private bool _sawDown;

    /// <summary>What CouchHUD last asked for while it is (being) connected; null when the tunnel came from the app itself.</summary>
    public VpnTarget? Target { get; private set; }

    /// <summary>
    /// Whether the tunnel is known to belong to <see cref="Target"/>: it came up after the request, or went down and up again. A change
    /// of country that only settled by time (the tunnel stayed up the whole while) is not confirmed: the place is only asked for.
    /// </summary>
    public bool TargetConfirmed { get; private set; }

    /// <summary>
    /// A code for the UI after a request did not work out: "noConnection" (no tunnel in time: often not signed in in the app),
    /// "stillConnected" (the tunnel stayed up after a disconnect). Cleared by the next request or a tunnel that comes up.
    /// </summary>
    public string? Hint { get; private set; }

    public bool Busy => _pending != Pending.None;

    public void RequestConnect(VpnTarget target, bool tunnelUp)
    {
        _pending = Pending.Connect;
        _since = now();
        _wasUp = tunnelUp;
        _sawDown = false;
        Target = target;
        TargetConfirmed = false;
        Hint = null;
    }

    public void RequestDisconnect(bool tunnelUp)
    {
        _pending = Pending.Disconnect;
        _since = now();
        _wasUp = tunnelUp;
        _sawDown = !tunnelUp;
        Hint = null;
    }

    /// <summary>The command line could not be run at all: nothing is pending any more.</summary>
    public void Abort()
    {
        if (_pending == Pending.Connect) Target = null;
        _pending = Pending.None;
    }

    /// <summary>The state for what is seen now. Call it with every look at the adapters; it also ends a request that took too long.</summary>
    public VpnState Observe(bool installed, bool tunnelUp)
    {
        if (!installed)
        {
            _pending = Pending.None;
            Target = null;
            return VpnState.NotInstalled;
        }
        var elapsed = now() - _since;
        switch (_pending)
        {
            case Pending.Connect:
                if (!tunnelUp) _sawDown = true;
                if (tunnelUp && (!_wasUp || _sawDown || elapsed >= ReconnectSettle))
                {
                    _pending = Pending.None;
                    TargetConfirmed = !_wasUp || _sawDown;
                    Hint = null;
                    return VpnState.Connected;
                }
                if (elapsed >= ConnectTimeout)
                {
                    _pending = Pending.None;
                    if (tunnelUp) return VpnState.Connected;
                    Target = null;
                    Hint = "noConnection";
                    return VpnState.Disconnected;
                }
                return VpnState.Connecting;
            case Pending.Disconnect:
                if (!tunnelUp)
                {
                    _pending = Pending.None;
                    Target = null;
                    return VpnState.Disconnected;
                }
                if (elapsed >= DisconnectTimeout)
                {
                    _pending = Pending.None;
                    Hint = "stillConnected";
                    return VpnState.Connected;
                }
                return VpnState.Disconnecting;
            default:
                if (tunnelUp)
                {
                    if (Hint == "noConnection") Hint = null;
                    return VpnState.Connected;
                }
                // Down without a request of ours: the app (or the network) ended it; what we asked for is history.
                Target = null;
                if (Hint == "stillConnected") Hint = null;
                return VpnState.Disconnected;
        }
    }
}

/// <summary>What the page and the Guide menu get from the host (codes, not sentences: the UI translates them).</summary>
/// <param name="TargetConfirmed">The tunnel came up (again) after CouchHUD's request; false: the place is only asked for.</param>
/// <param name="Hint">"untrusted" (found, but not below Program Files), "noConnection", "stillConnected", "startFailed", or null.</param>
public sealed record VpnStatus(
    string Provider,
    string Name,
    VpnState State,
    bool Installed,
    bool AppRunning,
    string? Hint,
    VpnTargetKind? TargetKind,
    string? TargetCountry,
    string? TargetServer,
    bool TargetConfirmed,
    string? Tunnel,
    string? LastCountry);

/// <summary>The bridge methods of the VPN and what of it works on a locked couch.</summary>
public static class VpnPolicy
{
    /// <summary>Reading only: the state and the country list. Every change needs the unlocked couch (and with parental controls the parents).</summary>
    public static readonly IReadOnlySet<string> OpenWhileLocked = new HashSet<string> { "vpnStatus", "vpnCountries" };

    /// <summary>Every call of the VPN page; anything else with the prefix is refused.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(OpenWhileLocked) { "vpnConnect", "vpnDisconnect", "vpnOpenApp" };

    /// <summary>The action name of the parents' one-time release for VPN changes (see Hud.RequireParentGrant).</summary>
    public const string ParentAction = "vpn";

    public static bool Allowed(string method, bool locked) => All.Contains(method) && (!locked || OpenWhileLocked.Contains(method));
}
