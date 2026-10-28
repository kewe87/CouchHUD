using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using CouchHud.Core.Config;
using CouchHud.Core.Integrations.Tv;
using CouchHud.Core.Localization;

namespace CouchHud.Shell.Services;

/// <summary>
/// The TV on the couch (LG webOS first): the bridge's tv* calls, the pairing kept in config.json with the key under DPAPI, and "TV follows
/// CouchHUD". All the protocol is <see cref="TvController"/> and <see cref="WebOsClient"/> in Core (tested against a fake TV); this class
/// only wires it to the config, the UI and the lock. Nothing blocks the UI thread: every call is awaited, the TV is never polled.
/// Not on the phone remote and not an MCP tool (deliberately, see SECURITY.md).
/// </summary>
public sealed class TvManager : IDisposable
{
    private readonly ConfigStore _config;
    private readonly Func<bool> _locked;
    private readonly Action<string> _requireInputChange;
    private readonly TvController _controller;
    private int _disposed;

    /// <param name="locked">The couch is locked right now (checked again here, not only in the bridge).</param>
    /// <param name="requireInputChange">Unlock and, with parental controls, the parents' release for exactly this input id.</param>
    public TvManager(ConfigStore config, Func<bool> locked, Action<string> requireInputChange, Action<string, object?> emit)
    {
        _config = config;
        _locked = locked;
        _requireInputChange = requireInputChange;
        _controller = new TvController(new WebOsClient(), new ConfigStoreTv(config), status => emit("tv", status));
    }

    public bool Paired => _controller.Paired;

    public async Task<object?> HandleAsync(string method, JsonElement args)
    {
        // The lock rules once more here, whoever calls: on a locked couch only state, volume, mute and on/off.
        TvPolicy.Admit(method, _locked(), Strings.Get("lock.unlockFirst"));
        JsonElement Arg(int i) => args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > i ? args[i] : default;
        bool? BoolArg(int i) => Arg(i).ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
        string StringArg(int i) => Arg(i).ValueKind == JsonValueKind.String ? Arg(i).GetString()! : throw new ArgumentException(Strings.Get("tv.badInput"));
        try
        {
            switch (method)
            {
                case "tvStatus":
                    // { refresh: true } from the page and the Guide menu: one read of the TV, unless it failed a moment ago.
                    return BoolArg(0) == true ? await _controller.RefreshAsync(user: false) : _controller.Status();
                case "tvVolume":
                    return await _controller.RefreshAsync(user: true);
                case "tvVolumeStep":
                    return await _controller.VolumeStepAsync(BoolArg(0) ?? true);
                case "tvSetVolume":
                    return await _controller.SetVolumeAsync(Arg(0).ValueKind == JsonValueKind.Number && Arg(0).TryGetInt32(out var v) ? v : throw new ArgumentException(Strings.Get("tv.badInput")));
                case "tvMute":
                    return await _controller.MuteAsync(BoolArg(0));
                case "tvPower":
                    return await _controller.PowerAsync(BoolArg(0) ?? throw new ArgumentException(Strings.Get("tv.badInput")));
                case "tvInputs":
                    return (await _controller.InputsAsync()).Select(i => new { id = i.Id, label = i.Label, connected = i.Connected }).ToList();
                case "tvSwitchInput":
                {
                    // The id is checked first (a release is never spent on garbage), then, before anything is awaited: the unlock and,
                    // with parental controls, the parents' release for exactly this input.
                    var id = TvPolicy.AdmitInputSwitch(StringArg(0), _requireInputChange);
                    var status = await _controller.SwitchInputAsync(id);
                    Console.WriteLine($"[tv] input switched to {id}");
                    return status;
                }
                case "tvDiscover":
                    return (await _controller.DiscoverAsync()).Select(d => new { address = d.Address, name = d.Name, provider = d.Provider }).ToList();
                case "tvPair":
                {
                    var address = TvAddressPolicy.ParseManual(StringArg(0)) ?? throw new ArgumentException(Strings.Get("tv.notLocal"));
                    // Unencrypted (old TVs without port 3001) only when asked for explicitly, and only in expert mode.
                    var allowPlain = BoolArg(1) == true;
                    if (allowPlain && !_config.Current.ExpertMode) throw new InvalidOperationException(Strings.Get("tv.err.plainOnly"));
                    Console.WriteLine($"[tv] pairing with {address}{(allowPlain ? " (unencrypted allowed)" : "")}");
                    TvStatus status;
                    try
                    {
                        status = await _controller.PairAsync(address, ArpCache.MacOf, allowPlain);
                    }
                    catch (TvException ex) when (ex.Code == TvError.PlainOnly)
                    {
                        // Not an error yet: the page asks (expert mode) and calls again with allowPlain.
                        Console.WriteLine("[tv] the TV has no TLS port");
                        return _controller.Status();
                    }
                    Console.WriteLine($"[tv] paired ({(status.Secure ? "TLS, key pinned" : "plain port 3000")}, Wake-on-LAN {(status.MacKnown ? "possible" : "no MAC")})");
                    return status;
                }
                case "tvCancelPair":
                    _controller.CancelPair();
                    return _controller.Status();
                case "tvForget":
                    Console.WriteLine("[tv] pairing forgotten");
                    return await _controller.ForgetAsync();
                case "tvSetMac":
                {
                    var text = Arg(0).ValueKind == JsonValueKind.String ? Arg(0).GetString() : null;
                    try { return _controller.SetMac(text); }
                    catch (ArgumentException) { throw new ArgumentException(Strings.Get("tv.badMac")); }
                }
                default:
                    throw new InvalidOperationException($"unknown method {method}");
            }
        }
        catch (TvException ex)
        {
            // Codes only in the log: never the TV's own error text, never the key.
            Console.Error.WriteLine($"[tv] {method}: {TvController.Code(ex.Code)}");
            throw new InvalidOperationException(Strings.Get("tv.err." + TvController.Code(ex.Code)));
        }
        catch (ArgumentException ex) when (ex.Message is "input")
        {
            throw new ArgumentException(Strings.Get("tv.badInput"));
        }
    }

    /// <summary>CouchHUD started or the PC woke up (fire and forget; the option and the pairing are checked inside).</summary>
    public void FollowWake()
    {
        if (Volatile.Read(ref _disposed) == 1) return;
        _ = _controller.FollowWakeAsync(_config.Current.Tv?.Follow ?? new TvFollow(false, false));
    }

    /// <summary>Before CouchHUD sends the PC to sleep or shuts it down (at most 2.5 s, awaited by the caller, never on a blocked thread).</summary>
    public Task FollowSleepAsync()
    {
        if (Volatile.Read(ref _disposed) == 1) return Task.CompletedTask;
        return _controller.FollowSleepAsync(_config.Current.Tv?.Follow ?? new TvFollow(false, false));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        // Closing the socket takes at most about a second; not on the UI thread.
        _ = Task.Run(async () => await _controller.DisposeAsync());
    }

    /// <summary>config.json as the pairing store; the key goes through DPAPI (CurrentUser) both ways.</summary>
    private sealed class ConfigStoreTv(ConfigStore config) : ITvStore
    {
        public TvPairing? Load() => config.Current.Tv?.ToPairing(Secrets.Unprotect);

        public void Save(TvPairing? pairing) => config.UpdateOrThrow(c =>
        {
            c.Tv ??= new TvConfig();
            c.Tv.SetPairing(pairing, Secrets.Protect);
        });

        public IReadOnlyCollection<string> SecureSeen() => config.Current.Tv?.SecureSeen?.ToList() ?? [];

        public void RememberSecure(IEnumerable<string> ids)
        {
            var list = ids.ToList();
            if (list.Count == 0 || list.All(SecureSeen().Contains)) return;
            config.UpdateOrThrow(c => (c.Tv ??= new TvConfig()).RememberSecure(list));
        }
    }
}

/// <summary>
/// The MAC of a neighbour from Windows' ARP cache (GetIpNetTable): after the pairing connection the TV's entry is there. Only read, no
/// packet is sent.
/// </summary>
internal static class ArpCache
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MibIpNetRow
    {
        public uint Index;
        public uint PhysAddrLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] PhysAddr;
        public uint Addr;
        public uint Type;
    }

    [DllImport("iphlpapi.dll")]
    private static extern int GetIpNetTable(IntPtr table, ref int size, bool order);

    private const int ErrorInsufficientBuffer = 122;
    private const uint TypeInvalid = 2;

    public static string? MacOf(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork) return null;
        var wanted = BitConverter.ToUInt32(address.GetAddressBytes(), 0);
        var size = 0;
        if (GetIpNetTable(IntPtr.Zero, ref size, false) != ErrorInsufficientBuffer || size <= 0 || size > 4 * 1024 * 1024) return null;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpNetTable(buffer, ref size, false) != 0) return null;
            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibIpNetRow>();
            for (var i = 0; i < count && 4 + (long)(i + 1) * rowSize <= size; i++)
            {
                var row = Marshal.PtrToStructure<MibIpNetRow>(buffer + 4 + i * rowSize);
                if (row.Addr != wanted || row.Type == TypeInvalid || row.PhysAddrLen != 6) continue;
                var mac = WakeOnLan.ParseMac(string.Join(':', row.PhysAddr.Take(6).Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture))));
                if (mac is not null) return WakeOnLan.Format(mac);
            }
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
