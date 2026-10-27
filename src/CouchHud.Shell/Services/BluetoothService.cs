using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using CouchHud.Core.Input;
using CouchHud.Core.Localization;

namespace CouchHud.Shell.Services;

public sealed record BluetoothDeviceInfo(string Id, string Name, bool Paired, bool Connected, bool LowEnergy, string Kind);

/// <summary>
/// Bluetooth from the couch: list, pair and remove headsets and controllers, and turn
/// the radio on or off. Pairing accepts confirm-only and PIN-display requests, which
/// covers headsets, Xbox and PlayStation controllers.
/// </summary>
/// <param name="confirm">Shows a question on the TV (PIN to type or to compare); true to go on.</param>
public sealed class BluetoothService(Func<string, Task<bool>> confirm) : IBluetoothRadio, IDisposable
{
    // Association endpoint protocol ids for Bluetooth classic and Bluetooth LE.
    private const string ClassicProtocol = "{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}";
    private const string LowEnergyProtocol = "{bb7bb05e-5972-42b5-94fc-76eaa7084d49}";

    private static readonly string[] Properties =
    [
        "System.Devices.Aep.IsPaired",
        "System.Devices.Aep.IsConnected",
        "System.Devices.Aep.Category",
        "System.Devices.Aep.ProtocolId"
    ];

    public async Task<(bool Available, bool On)> StateAsync()
    {
        var radio = await RadioAsync();
        if (radio is not null) Watch(radio);
        return (radio is not null, radio?.State == RadioState.On);
    }

    // What tells a game controller apart: category, Bluetooth classic device class, Bluetooth LE appearance.
    private static readonly string[] KindProperties =
    [
        "System.Devices.Aep.IsConnected",
        "System.Devices.Aep.Category",
        "System.Devices.Aep.Bluetooth.Cod.Major",
        "System.Devices.Aep.Bluetooth.Cod.Minor",
        "System.Devices.Aep.Bluetooth.Le.Appearance"
    ];

    private readonly object _watchGate = new();
    private Radio? _watched;
    private volatile bool _disposed;

    /// <summary>The radio changed its state (on, off, disabled); raised on a thread pool thread.</summary>
    public event Action<RadioStatus>? RadioChanged;

    /// <summary>The radio's state for the recovery: off and disabled are different things (disabled is not ours to change).</summary>
    public async Task<RadioStatus> StatusAsync()
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return RadioStatus.AccessDenied;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return RadioStatus.AccessDenied;
        }
        var radio = await RadioAsync();
        if (radio is null) return RadioStatus.NoAdapter;
        Watch(radio);
        return Map(radio.State);
    }

    private static RadioStatus Map(RadioState state) => state switch
    {
        RadioState.On => RadioStatus.On,
        RadioState.Off => RadioStatus.Off,
        RadioState.Disabled => RadioStatus.Disabled,
        _ => RadioStatus.Unknown
    };

    /// <summary>Follows the radio's state changes (once per radio object; a new adapter is picked up at the next read).</summary>
    private void Watch(Radio radio)
    {
        lock (_watchGate)
        {
            if (_disposed) return;
            if (_watched is not null && _watched.Name == radio.Name) return;
            if (_watched is not null) _watched.StateChanged -= OnRadioStateChanged;
            _watched = radio;
            radio.StateChanged += OnRadioStateChanged;
        }
    }

    private void OnRadioStateChanged(Radio sender, object args)
    {
        if (_disposed) return;
        try
        {
            RadioChanged?.Invoke(Map(sender.State));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[bluetooth] radio state: {ex.Message}");
        }
    }

    /// <summary>
    /// Paired Bluetooth game controllers (category, device class or LE appearance, the name as the last hint) and how many of them
    /// are connected right now. Only reads what Windows already knows: no search, no pairing, no change of discoverability.
    /// </summary>
    public async Task<(int Paired, int Connected)> ControllersAsync()
    {
        var query = $"System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True AND (System.Devices.Aep.ProtocolId:=\"{ClassicProtocol}\" OR System.Devices.Aep.ProtocolId:=\"{LowEnergyProtocol}\")";
        var devices = await DeviceInformation.FindAllAsync(query, KindProperties, DeviceInformationKind.AssociationEndpoint);
        int paired = 0, connected = 0;
        foreach (var d in devices)
        {
            var props = d.Properties;
            var categories = props.TryGetValue("System.Devices.Aep.Category", out var cat) ? cat as string[] : null;
            if (!BluetoothDeviceKind.IsController(categories, Number(props, "System.Devices.Aep.Bluetooth.Cod.Major"), Number(props, "System.Devices.Aep.Bluetooth.Cod.Minor"),
                    Number(props, "System.Devices.Aep.Bluetooth.Le.Appearance"), d.Name)) continue;
            paired++;
            if (props.TryGetValue("System.Devices.Aep.IsConnected", out var c) && c is bool on && on) connected++;
        }
        return (paired, connected);
    }

    public async Task<int> PairedControllersAsync() => (await ControllersAsync()).Paired;

    /// <summary>Airplane mode: more than one radio and every one of them off (with Bluetooth alone it cannot be told apart).</summary>
    public async Task<bool> AirplaneModeAsync()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync();
            return radios.Count > 1 && radios.All(r => r.State != RadioState.On);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }

    /// <summary>Stops following the radio (at the end of CouchHUD).</summary>
    public void Dispose()
    {
        lock (_watchGate)
        {
            _disposed = true;
            if (_watched is not null) _watched.StateChanged -= OnRadioStateChanged;
            _watched = null;
        }
        RadioChanged = null;
    }

    private static int? Number(IReadOnlyDictionary<string, object> props, string key) =>
        props.TryGetValue(key, out var v) ? v switch
        {
            byte b => b,
            ushort u => u,
            short s => s,
            uint ui => (int)Math.Min(ui, int.MaxValue),
            int i => i,
            _ => null
        } : null;

    public async Task SetRadioAsync(bool on)
    {
        if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) throw new InvalidOperationException(Strings.Get("bluetooth.notAllowed"));
        var radio = await RadioAsync() ?? throw new InvalidOperationException(Strings.Get("bluetooth.noAdapter"));
        var result = await radio.SetStateAsync(on ? RadioState.On : RadioState.Off);
        if (result != RadioAccessStatus.Allowed) throw new InvalidOperationException(Strings.Get("bluetooth.switchFailed"));
    }

    /// <summary>Paired devices plus whatever is in pairing mode nearby (searches for a few seconds).</summary>
    public async Task<IReadOnlyList<BluetoothDeviceInfo>> ScanAsync(TimeSpan duration)
    {
        var found = new Dictionary<string, BluetoothDeviceInfo>();
        var gate = new object();
        var query = $"(System.Devices.Aep.ProtocolId:=\"{ClassicProtocol}\" OR System.Devices.Aep.ProtocolId:=\"{LowEnergyProtocol}\")";
        var watcher = DeviceInformation.CreateWatcher(query, Properties, DeviceInformationKind.AssociationEndpoint);
        var done = new TaskCompletionSource();

        watcher.Added += (_, info) =>
        {
            if (ToInfo(info) is { } device) lock (gate) found[device.Id] = device;
        };
        watcher.Updated += (_, update) =>
        {
            lock (gate)
            {
                if (!found.TryGetValue(update.Id, out var known)) return;
                var props = update.Properties;
                found[update.Id] = known with
                {
                    Paired = props.TryGetValue("System.Devices.Aep.IsPaired", out var p) && p is bool paired ? paired : known.Paired,
                    Connected = props.TryGetValue("System.Devices.Aep.IsConnected", out var c) && c is bool connected ? connected : known.Connected
                };
            }
        };
        watcher.Removed += (_, update) =>
        {
            lock (gate) found.Remove(update.Id);
        };
        watcher.EnumerationCompleted += (_, _) => { };
        watcher.Stopped += (_, _) => done.TrySetResult();

        watcher.Start();
        await Task.Delay(duration);
        if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
        {
            watcher.Stop();
            await Task.WhenAny(done.Task, Task.Delay(2000));
        }

        lock (gate)
        {
            return found.Values
                .Where(d => d.Name.Length > 0)
                .OrderByDescending(d => d.Connected)
                .ThenByDescending(d => d.Paired)
                .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }

    public async Task<string> PairAsync(string id)
    {
        var device = await DeviceInformation.CreateFromIdAsync(id, Properties, DeviceInformationKind.AssociationEndpoint);
        if (device.Pairing.IsPaired) return Strings.Get("bluetooth.alreadyPaired", device.Name);
        if (!device.Pairing.CanPair) throw new InvalidOperationException(Strings.Get("bluetooth.cannotPair", device.Name));

        var custom = device.Pairing.Custom;
        async void OnRequested(DeviceInformationCustomPairing sender, DevicePairingRequestedEventArgs args)
        {
            var deferral = args.GetDeferral();
            try
            {
                switch (args.PairingKind)
                {
                    case DevicePairingKinds.ProvidePin:
                        // Older headsets: the PIN is almost always 0000.
                        args.Accept("0000");
                        break;
                    case DevicePairingKinds.DisplayPin:
                        // Keyboards: the user types this PIN on the device.
                        if (await confirm(Strings.Get("bluetooth.typePin", args.Pin))) args.Accept();
                        break;
                    case DevicePairingKinds.ConfirmPinMatch:
                        if (await confirm(Strings.Get("bluetooth.matchPin", device.Name, args.Pin))) args.Accept();
                        break;
                    default:
                        args.Accept();
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[bluetooth] pairing request: {ex.Message}");
            }
            finally
            {
                deferral.Complete();
            }
        }
        custom.PairingRequested += OnRequested;
        try
        {
            var result = await custom.PairAsync(DevicePairingKinds.ConfirmOnly | DevicePairingKinds.DisplayPin | DevicePairingKinds.ProvidePin | DevicePairingKinds.ConfirmPinMatch);
            return result.Status switch
            {
                DevicePairingResultStatus.Paired or DevicePairingResultStatus.AlreadyPaired => $"{device.Name} gekoppelt",
                DevicePairingResultStatus.AuthenticationTimeout or DevicePairingResultStatus.ConnectionRejected => throw new InvalidOperationException(Strings.Get("bluetooth.noAnswer", device.Name)),
                _ => throw new InvalidOperationException(Strings.Get("bluetooth.pairFailed", result.Status))
            };
        }
        finally
        {
            custom.PairingRequested -= OnRequested;
        }
    }

    public async Task<string> UnpairAsync(string id)
    {
        var device = await DeviceInformation.CreateFromIdAsync(id, Properties, DeviceInformationKind.AssociationEndpoint);
        var result = await device.Pairing.UnpairAsync();
        return result.Status is DeviceUnpairingResultStatus.Unpaired or DeviceUnpairingResultStatus.AlreadyUnpaired
            ? $"{device.Name} entfernt"
            : throw new InvalidOperationException(Strings.Get("bluetooth.removeFailed", result.Status));
    }

    private static async Task<Radio?> RadioAsync()
    {
        try
        {
            return (await Radio.GetRadiosAsync()).FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static BluetoothDeviceInfo? ToInfo(DeviceInformation info)
    {
        var props = info.Properties;
        var paired = props.TryGetValue("System.Devices.Aep.IsPaired", out var p) && p is bool b && b;
        var connected = props.TryGetValue("System.Devices.Aep.IsConnected", out var c) && c is bool bc && bc;
        var lowEnergy = props.TryGetValue("System.Devices.Aep.ProtocolId", out var proto) && proto is Guid g && g == Guid.Parse(LowEnergyProtocol);
        return new BluetoothDeviceInfo(info.Id, info.Name ?? "", paired, connected, lowEnergy, BluetoothDeviceKind.Kind(info.Name));
    }
}
